using backend.Data;
using backend.Models;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public class ProductService
{
    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ShopifyProductCatalogService _catalog;
    private readonly ShopifyInventoryService _inventory;

    public ProductService(
        AppDbContext db,
        IHttpContextAccessor httpContextAccessor,
        ShopifyProductCatalogService catalog,
        ShopifyInventoryService inventory )
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _catalog = catalog;
        _inventory = inventory;
    }

    public async Task<List<ProductWithSuppliersListItem>> GetProductsWithSuppliersAsync()
    {
        ShopifySession session = ShopifySessionReader.Require(
            _httpContextAccessor,
            "Няма Shopify-кантэксту для загрузкі прадуктаў."
        );

        List<SupplyProduct> supplyProducts = await _db.SupplyProducts
            .AsNoTracking()
            .Include( sp => sp.Supply )
            .ThenInclude( s => s.Supplier )
            .ToListAsync();

        Dictionary<string, HashSet<string>> suppliersByProductId = supplyProducts
            .GroupBy( sp => ShopifyIds.NormalizeProductId( sp.ShopifyProductId ) )
            .ToDictionary(
                g => g.Key,
                g => g.Select( sp => sp.Supply.Supplier.Name )
                    .Where( n => !string.IsNullOrWhiteSpace( n ) )
                    .ToHashSet( StringComparer.OrdinalIgnoreCase ),
                StringComparer.OrdinalIgnoreCase
            );

        Dictionary<string, List<ProductSupplierPriceItem>> supplierPricesByProductId = supplyProducts
            .GroupBy( sp => ShopifyIds.NormalizeProductId( sp.ShopifyProductId ) )
            .ToDictionary(
                g => g.Key,
                g => g
                    .GroupBy(
                        sp => new
                        {
                            sp.Supply.SupplierId,
                            sp.Supply.Supplier.Name
                        }
                    )
                    .Select( supplierGroup =>
                        supplierGroup
                            .OrderByDescending( sp => sp.Supply.Date )
                            .ThenByDescending( sp => sp.Supply.Id )
                            .Select( sp => new ProductSupplierPriceItem
                            {
                                SupplierId = sp.Supply.SupplierId,
                                SupplierName = sp.Supply.Supplier.Name,
                                SupplierPrice = sp.SupplierPrice,
                                SalePrice = sp.SalePrice
                            } )
                            .First()
                    )
                    .OrderBy( x => x.SupplierName, StringComparer.OrdinalIgnoreCase )
                    .ToList(),
                StringComparer.OrdinalIgnoreCase
            );

        Dictionary<string, string> lastSyncedSupplierByProductId = supplyProducts
            .Where( sp => sp.SyncWithShopify )
            .GroupBy( sp => ShopifyIds.NormalizeProductId( sp.ShopifyProductId ) )
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderByDescending( sp => sp.Supply.Date )
                    .ThenByDescending( sp => sp.Supply.Id )
                    .Select( sp => sp.Supply.Supplier.Name )
                    .FirstOrDefault() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase
            );

        Dictionary<string, List<ProductUnsyncedSupplierItem>> unsyncedSuppliersByProductId = supplyProducts
            .Where( sp => !sp.SyncWithShopify )
            .GroupBy( sp => ShopifyIds.NormalizeProductId( sp.ShopifyProductId ) )
            .ToDictionary(
                g => g.Key,
                g => g
                    .GroupBy(
                        sp => new
                        {
                            sp.Supply.SupplierId,
                            sp.Supply.Supplier.Name
                        }
                    )
                    .Select( sg => new ProductUnsyncedSupplierItem
                    {
                        SupplierId = sg.Key.SupplierId,
                        SupplierName = sg.Key.Name,
                        Quantity = sg.Sum( x => x.Quantity )
                    } )
                    .OrderBy( x => x.SupplierName, StringComparer.OrdinalIgnoreCase )
                    .ToList(),
                StringComparer.OrdinalIgnoreCase
            );

        Dictionary<string, int> unsyncedQuantityByProductId = await _db.SupplyProducts
            .AsNoTracking()
            .Where( sp => !sp.SyncWithShopify )
            .GroupBy( sp => sp.ShopifyProductId )
            .ToDictionaryAsync(
                g => ShopifyIds.NormalizeProductId( g.Key ),
                g => g.Sum( sp => sp.Quantity )
            );

        List<ShopifyCatalogProduct> catalogProducts =
            await _catalog.FetchAllProductsAsync( session.Shop, session.AccessToken );

        string storeSlug = session.Shop.Replace( ".myshopify.com", "", StringComparison.OrdinalIgnoreCase );
        List<ProductWithSuppliersListItem> result = new();

        foreach (ShopifyCatalogProduct product in catalogProducts)
        {
            suppliersByProductId.TryGetValue( product.ProductId, out HashSet<string>? suppliersSet );
            List<string> suppliers = (suppliersSet ?? [])
                .OrderBy( n => n, StringComparer.OrdinalIgnoreCase )
                .ToList();
            supplierPricesByProductId.TryGetValue( product.ProductId, out List<ProductSupplierPriceItem>? supplierPrices );
            lastSyncedSupplierByProductId.TryGetValue( product.ProductId, out string? lastSyncedSupplierName );
            unsyncedSuppliersByProductId.TryGetValue( product.ProductId, out List<ProductUnsyncedSupplierItem>? unsyncedSuppliers );
            bool hasSupplyQuantityOverride = unsyncedQuantityByProductId.TryGetValue( product.ProductId, out int overrideQuantity );
            int effectiveQuantity = hasSupplyQuantityOverride ? overrideQuantity : product.TotalInventory;

            result.Add( new ProductWithSuppliersListItem
            {
                ShopifyProductId = product.ProductId,
                ProductName = product.Title,
                ProductAuthor = product.Author,
                ProductType = product.ProductType,
                ProductAdminUrl = $"https://admin.shopify.com/store/{storeSlug}/products/{product.ProductId}",
                MainImageUrl = product.ImageUrl,
                QuantityInStock = effectiveQuantity,
                ShopifyQuantityInStock = product.TotalInventory,
                HasSupplyQuantityOverride = hasSupplyQuantityOverride,
                LastSyncedSupplierName = lastSyncedSupplierName ?? string.Empty,
                Suppliers = suppliers,
                UnsyncedSuppliers = unsyncedSuppliers ?? [],
                Variants = product.Variants,
                SupplierPrices = supplierPrices ?? []
            } );
        }

        return result;
    }

    public async Task<ProductSyncResult> SyncUnsyncedSupplierRowAsync( string shopifyProductId, int supplierId )
    {
        if (string.IsNullOrWhiteSpace( shopifyProductId ))
        {
            throw new InvalidOperationException( "Не зададзены Shopify ID прадукту." );
        }
        if (supplierId <= 0)
        {
            throw new InvalidOperationException( "Не зададзены пастаўшчык." );
        }

        ShopifySession session = ShopifySessionReader.Require(
            _httpContextAccessor,
            "Няма Shopify-кантэксту для сінхранізацыі."
        );

        string normalizedId = ShopifyIds.NormalizeProductId( shopifyProductId.Trim() );

        List<SupplyProduct> candidateRows = await _db.SupplyProducts
            .Include( sp => sp.Supply )
            .Where( sp => !sp.SyncWithShopify && sp.Supply.SupplierId == supplierId )
            .ToListAsync();

        List<SupplyProduct> rowsToSync = candidateRows
            .Where( sp => ShopifyIds.NormalizeProductId( sp.ShopifyProductId ) == normalizedId )
            .ToList();

        if (rowsToSync.Count == 0)
        {
            throw new InvalidOperationException( "Не знойдзены несінхранізаваныя радкі для гэтага прадукту і пастаўшчыка." );
        }

        int delta = rowsToSync.Sum( r => r.Quantity );
        if (delta <= 0)
        {
            throw new InvalidOperationException( "Няма колькасці для сінхранізацыі." );
        }

        decimal salePriceToSync = rowsToSync
            .OrderByDescending( r => r.Supply.Date )
            .ThenByDescending( r => r.Supply.Id )
            .Select( r => r.SalePrice )
            .FirstOrDefault();

        if (salePriceToSync < 0)
        {
            throw new InvalidOperationException( "Цана продажу не можа быць адмоўнай." );
        }

        (int previous, int next) = await _inventory.ApplyInventoryDeltaByProductKeyAsync(
            session.Shop,
            session.AccessToken,
            normalizedId,
            delta
        );
        if (salePriceToSync > 0)
        {
            await _inventory.SetVariantPriceByProductKeyAsync(
                session.Shop,
                session.AccessToken,
                normalizedId,
                salePriceToSync
            );
        }

        foreach (SupplyProduct row in rowsToSync)
        {
            row.SyncWithShopify = true;
        }
        await _db.SaveChangesAsync();

        return new ProductSyncResult
        {
            ShopifyProductId = normalizedId,
            SupplierId = supplierId,
            SyncedQuantity = delta,
            PreviousAvailable = previous,
            NewAvailable = next
        };
    }
}
