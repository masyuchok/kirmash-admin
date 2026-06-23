using backend.Data;
using backend.Models;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public class SupplyService
{
    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ShopifyInventoryService _shopifyInventory;
    private readonly ShopifyProductCatalogService _shopifyCatalog;

    public SupplyService(
        AppDbContext db,
        IHttpContextAccessor httpContextAccessor,
        ShopifyInventoryService shopifyInventory,
        ShopifyProductCatalogService shopifyCatalog )
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _shopifyInventory = shopifyInventory;
        _shopifyCatalog = shopifyCatalog;
    }

        public async Task<List<Supply>> GetAllAsync()
        {
            return await _db.Supplies
                .OrderBy(s => s.Date)
                .ToListAsync();
        }

        public async Task<List<SupplyListItem>> GetSupplyList()
        {
            return await _db.Supplies
                .Select(s => new SupplyListItem
                {
                    Id = s.Id,
                    SupplierId = s.SupplierId,
                    SupplierName = s.Supplier.Name,
                    Date = s.Date,
                    ProductNumber = s.SupplyProducts.Count,
                    TotalQuantity = s.SupplyProducts.Sum( p => p.Quantity )
                })
                .OrderBy(s => s.Date)
                .ToListAsync();
        }

        public async Task<SupplyDetailsResponse?> GetSupplyDetailsAsync( int id )
        {
            return await _db.Supplies
                .AsNoTracking()
                .Where( s => s.Id == id )
                .Select( s => new SupplyDetailsResponse
                {
                    Id = s.Id,
                    SupplierId = s.SupplierId,
                    SupplierName = s.Supplier.Name,
                    Date = s.Date,
                    Products = s.SupplyProducts
                        .Select( p => new SupplyDetailsProductItem
                        {
                            ShopifyProductId = p.ShopifyProductId,
                            ShopifyVariantId = p.ShopifyVariantId,
                            Quantity = p.Quantity,
                            SupplierPrice = p.SupplierPrice,
                            VatRatePercent = p.VatRatePercent,
                            MarginPercent = p.MarginPercent,
                            SalePrice = p.SalePrice,
                            SyncWithShopify = p.SyncWithShopify
                        } )
                        .ToList()
                } )
                .FirstOrDefaultAsync();
        }

        public async Task<bool> DeleteSupplyAsync( int id )
        {
            Supply? supply = await _db.Supplies
                .FirstOrDefaultAsync( s => s.Id == id );

            if (supply == null)
            {
                return false;
            }

            _db.Supplies.Remove( supply );
            await _db.SaveChangesAsync();
            return true;
        }

    public async Task<SupplySaveResult> SaveSupplyAsync( SupplySaveRequest request )
    {
        string? syncWarning = null;

            if (request.SupplierId <= 0)
            {
                throw new InvalidOperationException( "Неабходна выбраць пастаўшчыка." );
            }

            List<SupplyProductSaveItem> requestProducts = request.Products ?? new List<SupplyProductSaveItem>();

            foreach (SupplyProductSaveItem item in requestProducts)
            {
                if (string.IsNullOrWhiteSpace( item.ShopifyProductId ))
                {
                    throw new InvalidOperationException( "Тавар без Shopify ID не можа быць захаваны." );
                }
                if (item.SupplierPrice < 0 || item.MarginPercent < 0 || item.SalePrice < 0)
                {
                    throw new InvalidOperationException( "Цэны і працэнт не могуць быць адмоўнымі." );
                }
                if (item.VatRatePercent != 5m && item.VatRatePercent != 23m)
                {
                    throw new InvalidOperationException( "Працэнт VAT можа быць толькі 5 або 23." );
                }
            }

            if (requestProducts.Count == 0 && !(request.SupplyId.HasValue && request.SupplyId.Value > 0))
            {
                throw new InvalidOperationException( "Дадайце хаця б адзін тавар." );
            }

            bool supplierExists = await _db.Suppliers.AnyAsync( s => s.Id == request.SupplierId );
            if (!supplierExists)
            {
                throw new InvalidOperationException( "Пастаўшчык не знойдзены." );
            }

            await using var tx = await _db.Database.BeginTransactionAsync();

            Supply supply;
            Dictionary<string, int> previousQuantities = new( StringComparer.OrdinalIgnoreCase );
            Dictionary<string, int> previousAllLineQuantities = new( StringComparer.OrdinalIgnoreCase );
            if (request.SupplyId.HasValue && request.SupplyId.Value > 0)
            {
                supply = await _db.Supplies
                    .Include( s => s.SupplyProducts )
                    .FirstOrDefaultAsync( s => s.Id == request.SupplyId.Value )
                    ?? throw new InvalidOperationException( "Пастаўка не знойдзена." );

                previousQuantities = supply.SupplyProducts
                    .Where( p => p.SyncWithShopify )
                    .GroupBy( p => BuildShopifySyncKey( p.ShopifyProductId, p.ShopifyVariantId ), StringComparer.OrdinalIgnoreCase )
                    .ToDictionary( g => g.Key, g => g.Sum( p => p.Quantity ), StringComparer.OrdinalIgnoreCase );

                previousAllLineQuantities = supply.SupplyProducts
                    .GroupBy( p => BuildShopifySyncKey( p.ShopifyProductId, p.ShopifyVariantId ), StringComparer.OrdinalIgnoreCase )
                    .ToDictionary( g => g.Key, g => g.Sum( p => p.Quantity ), StringComparer.OrdinalIgnoreCase );

                supply.SupplierId = request.SupplierId;
                supply.Date = request.Date;
                supply.SupplyProducts.Clear();
            }
            else
            {
                supply = new Supply
                {
                    SupplierId = request.SupplierId,
                    Date = request.Date,
                };
                _db.Supplies.Add( supply );
            }

            Dictionary<string, int> netExcludingCurrent = await GetSupplierProductNetQuantitiesAsync(
                request.SupplierId,
                request.SupplyId.HasValue && request.SupplyId.Value > 0 ? request.SupplyId.Value : null
            );

            foreach (SupplyProductSaveItem item in requestProducts)
            {
                if (item.Quantity == 0)
                {
                    throw new InvalidOperationException( "Колькасць не можа быць роўнай 0." );
                }

                string lineKey = BuildShopifySyncKey( item.ShopifyProductId, item.ShopifyVariantId );
                if (item.Quantity < 0)
                {
                    int previousInCurrent = previousAllLineQuantities.GetValueOrDefault( lineKey );
                    int maxReturnable = netExcludingCurrent.GetValueOrDefault( lineKey ) + previousInCurrent;
                    if (maxReturnable <= 0)
                    {
                        throw new InvalidOperationException(
                            "Адмоўная колькасць даступная толькі для тавараў, якія раней былі ў пастаўках гэтага пастаўшчыка."
                        );
                    }

                    if (Math.Abs( item.Quantity ) > maxReturnable)
                    {
                        throw new InvalidOperationException(
                            $"Нельга вернуць больш за {maxReturnable} шт. (атрымана ад пастаўшчыка)."
                        );
                    }
                }
            }

            Dictionary<string, int> newQuantities = requestProducts
                .Where( p => p.SyncWithShopify )
                .GroupBy( p => BuildShopifySyncKey( p.ShopifyProductId, p.ShopifyVariantId ), StringComparer.OrdinalIgnoreCase )
                .ToDictionary( g => g.Key, g => g.Sum( p => p.Quantity ), StringComparer.OrdinalIgnoreCase );

            Dictionary<string, decimal> syncedSalePrices = requestProducts
                .Where( p => p.SyncWithShopify )
                .GroupBy( p => BuildShopifySyncKey( p.ShopifyProductId, p.ShopifyVariantId ), StringComparer.OrdinalIgnoreCase )
                .ToDictionary(
                    g => g.Key,
                    g => g.Last().SalePrice,
                    StringComparer.OrdinalIgnoreCase
                );

            Dictionary<string, int> deltas = new( StringComparer.OrdinalIgnoreCase );
            foreach (string key in previousQuantities.Keys.Union( newQuantities.Keys, StringComparer.OrdinalIgnoreCase ))
            {
                int oldQty = previousQuantities.TryGetValue( key, out int o ) ? o : 0;
                int newQty = newQuantities.TryGetValue( key, out int n ) ? n : 0;
                int delta = newQty - oldQty;
                if (delta != 0)
                {
                    deltas[key] = delta;
                }
            }

        List<SupplyInventoryUpdateResult> updates = new();
        if (ShopifySessionReader.TryGet( _httpContextAccessor, out ShopifySession session ))
        {
            try
            {
                updates = await _shopifyInventory.ApplySupplySyncAsync(
                    session.Shop,
                    session.AccessToken,
                    deltas,
                    syncedSalePrices
                );
            }
            catch (Exception ex)
            {
                syncWarning = ex.Message;
            }
        }
        else
        {
            syncWarning = "Няма Shopify-кантэксту для абнаўлення астаткаў.";
        }

            foreach (SupplyProductSaveItem item in requestProducts)
            {
                if (string.IsNullOrWhiteSpace( item.ShopifyProductId ))
                {
                    continue;
                }

                supply.SupplyProducts.Add( new SupplyProduct
                {
                    ShopifyProductId = item.ShopifyProductId.Trim(),
                    ShopifyVariantId = (item.ShopifyVariantId ?? string.Empty).Trim(),
                    Quantity = item.Quantity,
                    SupplierPrice = item.SupplierPrice,
                    VatRatePercent = item.VatRatePercent,
                    MarginPercent = item.MarginPercent,
                    SalePrice = item.SalePrice,
                    SyncWithShopify = item.SyncWithShopify
                } );
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        return new SupplySaveResult
        {
            SupplyId = supply.Id,
            Warning = syncWarning,
            InventoryUpdates = updates
        };
    }

    private static string BuildShopifySyncKey( string shopifyProductId, string? shopifyVariantId )
    {
        string productId = shopifyProductId.Trim();
        string variantId = (shopifyVariantId ?? string.Empty).Trim();
        return string.IsNullOrEmpty( variantId ) ? productId : $"{productId}::{variantId}";
    }

    public async Task<List<SupplySupplierProductBalanceItem>> GetSupplierProductBalancesAsync(
        int supplierId,
        int? excludeSupplyId = null )
    {
        if (supplierId <= 0)
        {
            return new List<SupplySupplierProductBalanceItem>();
        }

        Dictionary<string, int> netByLine = await GetSupplierProductNetQuantitiesAsync( supplierId, excludeSupplyId );
        List<SupplySupplierProductBalanceItem> result = new();
        foreach ((string lineKey, int netQuantity) in netByLine.OrderBy( x => x.Key, StringComparer.OrdinalIgnoreCase ))
        {
            (string productId, string variantId) = ParseShopifySyncKey( lineKey );
            result.Add( new SupplySupplierProductBalanceItem
            {
                ShopifyProductId = productId,
                ShopifyVariantId = variantId,
                NetQuantity = netQuantity
            } );
        }

        return result;
    }

    private async Task<Dictionary<string, int>> GetSupplierProductNetQuantitiesAsync(
        int supplierId,
        int? excludeSupplyId )
    {
        IQueryable<SupplyProduct> query = _db.SupplyProducts
            .AsNoTracking()
            .Where( sp => sp.Supply.SupplierId == supplierId );
        if (excludeSupplyId.HasValue && excludeSupplyId.Value > 0)
        {
            query = query.Where( sp => sp.SupplyId != excludeSupplyId.Value );
        }

        List<SupplyProductNetRow> rows = await query
            .Select( sp => new SupplyProductNetRow
            {
                ShopifyProductId = sp.ShopifyProductId,
                ShopifyVariantId = sp.ShopifyVariantId,
                Quantity = sp.Quantity
            } )
            .ToListAsync();

        return rows
            .GroupBy(
                row => BuildShopifySyncKey( row.ShopifyProductId, row.ShopifyVariantId ),
                StringComparer.OrdinalIgnoreCase
            )
            .ToDictionary( g => g.Key, g => g.Sum( x => x.Quantity ), StringComparer.OrdinalIgnoreCase );
    }

    private static (string ProductId, string VariantId) ParseShopifySyncKey( string lineKey )
    {
        const string separator = "::";
        int idx = lineKey.IndexOf( separator, StringComparison.Ordinal );
        if (idx < 0)
        {
            return (lineKey, string.Empty);
        }

        return (lineKey[..idx], lineKey[(idx + separator.Length)..]);
    }

    public async Task<List<SupplyCatalogProductItem>> GetCatalogProductsAsync( int? supplierId )
    {
        IQueryable<SupplyProduct> query = _db.SupplyProducts
            .AsNoTracking()
            .Include( sp => sp.Supply );
        if (supplierId.HasValue && supplierId.Value > 0)
        {
            query = query.Where( sp => sp.Supply.SupplierId == supplierId.Value );
        }

        List<SupplyCatalogRow> rows = await query
            .Select( sp => new SupplyCatalogRow
            {
                ShopifyProductId = sp.ShopifyProductId,
                ShopifyVariantId = sp.ShopifyVariantId,
                VatRatePercent = sp.VatRatePercent,
                SupplierPrice = sp.SupplierPrice,
                SupplyDate = sp.Supply.Date,
                SupplyId = sp.Supply.Id
            } )
            .ToListAsync();

        Dictionary<string, string> productNames = await BuildProductNamesAsync();

        return rows
            .Select( row =>
            {
                string productId = ShopifyIds.NormalizeProductId( row.ShopifyProductId.Trim() );
                string variantId = string.IsNullOrWhiteSpace( row.ShopifyVariantId )
                    ? string.Empty
                    : ShopifyIds.NormalizeVariantId( row.ShopifyVariantId.Trim() );
                return new
                {
                    ProductId = productId,
                    VariantId = variantId,
                    row.VatRatePercent,
                    row.SupplierPrice,
                    row.SupplyDate,
                    row.SupplyId
                };
            } )
            .Where( x => !string.IsNullOrWhiteSpace( x.ProductId ) )
            .GroupBy( x => (x.ProductId, x.VariantId), SupplyCatalogLineComparer.Instance )
            .Select( g =>
            {
                var latestOverall = g
                    .OrderByDescending( x => x.SupplyDate )
                    .ThenByDescending( x => x.SupplyId )
                    .First();
                var latestWithPrice = g
                    .Where( x => x.SupplierPrice > 0m )
                    .OrderByDescending( x => x.SupplyDate )
                    .ThenByDescending( x => x.SupplyId )
                    .FirstOrDefault();
                productNames.TryGetValue( latestOverall.ProductId, out string? productName );
                return new SupplyCatalogProductItem
                {
                    ShopifyProductId = latestOverall.ProductId,
                    ShopifyVariantId = latestOverall.VariantId,
                    ProductName = string.IsNullOrWhiteSpace( productName ) ? latestOverall.ProductId : productName,
                    VatRatePercent = latestWithPrice?.VatRatePercent ?? latestOverall.VatRatePercent,
                    SupplierPrice = latestWithPrice?.SupplierPrice ?? 0m
                };
            } )
            .OrderBy( x => x.ProductName, StringComparer.OrdinalIgnoreCase )
            .ToList();
    }

    private async Task<Dictionary<string, string>> BuildProductNamesAsync()
    {
        Dictionary<string, string> names = new( StringComparer.OrdinalIgnoreCase );

        List<VatReportExpenseProduct> expenseProducts = await _db.VatReportExpenseProducts
            .AsNoTracking()
            .Where( p => p.ProductTitle != "" )
            .ToListAsync();
        foreach (VatReportExpenseProduct product in expenseProducts)
        {
            string productId = ShopifyIds.NormalizeProductId( product.ShopifyProductId );
            if (string.IsNullOrWhiteSpace( productId ) || string.IsNullOrWhiteSpace( product.ProductTitle )) continue;
            names[productId] = product.ProductTitle.Trim();
        }

        List<VatReportCashSale> cashSales = await _db.VatReportCashSales
            .AsNoTracking()
            .Where( s => s.ProductTitle != "" )
            .ToListAsync();
        foreach (VatReportCashSale sale in cashSales)
        {
            string productId = ShopifyIds.NormalizeProductId( sale.ShopifyProductId );
            if (string.IsNullOrWhiteSpace( productId ) || string.IsNullOrWhiteSpace( sale.ProductTitle )) continue;
            names[productId] = sale.ProductTitle.Trim();
        }

        List<VatReportRowItem> rowItems = await _db.VatReportRowItems
            .AsNoTracking()
            .Where( i => i.ProductTitle != "" )
            .ToListAsync();
        foreach (VatReportRowItem item in rowItems)
        {
            string productId = ShopifyIds.NormalizeProductId( item.ShopifyProductId );
            if (string.IsNullOrWhiteSpace( productId ) || string.IsNullOrWhiteSpace( item.ProductTitle )) continue;
            names[productId] = item.ProductTitle.Trim();
        }

        if (ShopifySessionReader.TryGet( _httpContextAccessor, out ShopifySession? session ))
        {
            List<ShopifyCatalogProduct> catalogProducts =
                await _shopifyCatalog.FetchAllProductsAsync( session.Shop, session.AccessToken );
            foreach (ShopifyCatalogProduct product in catalogProducts)
            {
                if (string.IsNullOrWhiteSpace( product.ProductId ) || string.IsNullOrWhiteSpace( product.Title )) continue;
                names[product.ProductId] = product.Title.Trim();
            }
        }

        return names;
    }

    private sealed class SupplyProductNetRow
    {
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ShopifyVariantId { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }

    private sealed class SupplyCatalogRow
    {
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ShopifyVariantId { get; set; } = string.Empty;
        public decimal VatRatePercent { get; set; }
        public decimal SupplierPrice { get; set; }
        public DateOnly SupplyDate { get; set; }
        public int SupplyId { get; set; }
    }

    private sealed class SupplyCatalogLineComparer : IEqualityComparer<(string ProductId, string VariantId)>
    {
        public static SupplyCatalogLineComparer Instance { get; } = new();

        public bool Equals( (string ProductId, string VariantId) x, (string ProductId, string VariantId) y ) =>
            string.Equals( x.ProductId, y.ProductId, StringComparison.OrdinalIgnoreCase ) &&
            string.Equals( x.VariantId, y.VariantId, StringComparison.OrdinalIgnoreCase );

        public int GetHashCode( (string ProductId, string VariantId) obj ) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode( obj.ProductId ),
                StringComparer.OrdinalIgnoreCase.GetHashCode( obj.VariantId )
            );
    }
}
