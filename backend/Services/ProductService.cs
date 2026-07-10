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
    private readonly ShopifyVariantLookupService _variantLookup;
    private readonly ProductLedgerService _ledger;
    private readonly InventorySalesCacheService _salesCache;

    public ProductService(
        AppDbContext db,
        IHttpContextAccessor httpContextAccessor,
        ShopifyProductCatalogService catalog,
        ShopifyInventoryService inventory,
        ShopifyVariantLookupService variantLookup,
        ProductLedgerService ledger,
        InventorySalesCacheService salesCache )
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _catalog = catalog;
        _inventory = inventory;
        _variantLookup = variantLookup;
        _ledger = ledger;
        _salesCache = salesCache;
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
                SupplierPrices = supplierPrices ?? [],
                OverpaidLines = []
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

    public async Task<ProductHistoryResponse> GetProductHistoryAsync(
        string shopifyProductId,
        string? shopifyVariantId = null,
        int? supplierId = null,
        string? variantTitle = null )
    {
        if (string.IsNullOrWhiteSpace( shopifyProductId ))
        {
            throw new InvalidOperationException( "Не зададзены Shopify ID прадукту." );
        }

        string normalizedProductId = ShopifyIds.NormalizeProductId( shopifyProductId.Trim() );
        string? normalizedVariantFilter = string.IsNullOrWhiteSpace( shopifyVariantId )
            ? null
            : ShopifyIds.NormalizeVariantId( shopifyVariantId.Trim() );

        List<string> productIdCandidates = ProductLedgerService.BuildProductIdCandidates( normalizedProductId );
        string productName = await ResolveProductNameAsync( normalizedProductId, productIdCandidates );
        (Dictionary<string, string> variantTitles, Dictionary<string, Dictionary<string, string>> variantIdByTitle) =
            await GetVariantCatalogMapsCachedAsync();
        IReadOnlyDictionary<string, string> defaultVariantByProduct =
            await _ledger.GetDefaultVariantByProductAsync();
        IReadOnlyDictionary<string, string> legacySaleVariantByProduct =
            await _ledger.GetLegacySaleVariantByProductAsync();
        string? filterVariantTitle = string.IsNullOrWhiteSpace( variantTitle ) ? null : variantTitle.Trim();
        if (string.IsNullOrWhiteSpace( filterVariantTitle ) && !string.IsNullOrWhiteSpace( normalizedVariantFilter ))
        {
            filterVariantTitle = ResolveVariantTitle( normalizedVariantFilter, variantTitles );
        }

        // Single-variant products aggregate paid/sold in supplier inventory; payment lines often
        // lack variant ids, so a variant filter would hide them while «Аплочана» still counts them.
        bool useProductTotals =
            VariantLegacyDefaults.GetNamedVariantCount( normalizedProductId, variantIdByTitle ) <= 1;
        string? paymentVariantFilter = useProductTotals ? null : normalizedVariantFilter;
        string? paymentFilterVariantTitle = useProductTotals ? null : filterVariantTitle;

        List<SupplyProduct> supplyLines = await _db.SupplyProducts
            .AsNoTracking()
            .Include( sp => sp.Supply )
            .ThenInclude( s => s.Supplier )
            .Where( sp => productIdCandidates.Contains( sp.ShopifyProductId ) )
            .ToListAsync();

        List<ProductHistorySupplyEvent> supplies = supplyLines
            .Where( sp => ProductLedgerService.MatchesVariantFilter(
                VariantLegacyDefaults.ResolveVariantId(
                    sp.ShopifyProductId,
                    sp.ShopifyVariantId,
                    defaultVariantByProduct,
                    variantIdByTitle,
                    legacySaleVariantByProduct ),
                ResolveVariantTitle(
                    VariantLegacyDefaults.ResolveVariantId(
                        sp.ShopifyProductId,
                        sp.ShopifyVariantId,
                        defaultVariantByProduct,
                        variantIdByTitle,
                        legacySaleVariantByProduct ),
                    variantTitles ),
                normalizedVariantFilter,
                filterVariantTitle ) )
            .Where( sp => !supplierId.HasValue || sp.Supply.SupplierId == supplierId.Value )
            .Select( sp =>
            {
                string variantId = VariantLegacyDefaults.ResolveVariantId(
                    sp.ShopifyProductId,
                    sp.ShopifyVariantId,
                    defaultVariantByProduct,
                    variantIdByTitle,
                    legacySaleVariantByProduct );
                return new ProductHistorySupplyEvent
                {
                    Date = sp.Supply.Date.ToString( "yyyy-MM-dd" ),
                    SupplyId = sp.SupplyId,
                    SupplierId = sp.Supply.SupplierId,
                    SupplierName = sp.Supply.Supplier.Name ?? string.Empty,
                    ShopifyVariantId = variantId,
                    VariantTitle = ResolveVariantTitle( variantId, variantTitles ),
                    Quantity = sp.Quantity
                };
            } )
            .OrderByDescending( x => x.Date )
            .ThenByDescending( x => x.SupplyId )
            .ToList();

        List<ProductHistorySaleEvent> sales = await _ledger.GetSaleEventsForProductAsync(
            normalizedProductId,
            normalizedVariantFilter,
            filterVariantTitle,
            supplierId,
            productName,
            matchByProductIdOnly: true,
            loadLiveShopifyOrders: false );

        List<VatReportExpenseProduct> paymentLines = await _db.VatReportExpenseProducts
            .AsNoTracking()
            .Include( p => p.VatReportExpense )
            .ThenInclude( e => e.Supplier )
            .Include( p => p.VatReportExpense )
            .ThenInclude( e => e.ExpenseInvoiceType )
            .Include( p => p.VatReportExpense )
            .ThenInclude( e => e.VatReport )
            .Where( p =>
                productIdCandidates.Contains( p.ShopifyProductId ) &&
                p.VatReportExpense.ExpenseInvoiceType.Name == ExpenseInvoiceTypeSeeder.SupplierPaymentDefaultName )
            .ToListAsync();

        List<ProductHistoryPaymentEvent> payments = paymentLines
            .Where( p => ProductLedgerService.PaymentLineMatchesProduct(
                normalizedProductId,
                p.ShopifyProductId,
                p.ProductTitle,
                productName,
                productIdCandidates ) )
            .Where( p => ProductLedgerService.MatchesPaymentVariantFilter(
                p.ShopifyProductId,
                p.ShopifyVariantId,
                p.ProductTitle,
                paymentVariantFilter,
                paymentFilterVariantTitle,
                variantIdByTitle,
                defaultVariantByProduct,
                variantTitles,
                legacySaleVariantByProduct ) )
            .Where( p => !supplierId.HasValue || p.VatReportExpense.SupplierId == supplierId.Value )
            .Select( p =>
            {
                string variantId = ProductLedgerService.ResolvePaymentVariantForHistory(
                    p.ShopifyProductId,
                    p.ShopifyVariantId,
                    p.ProductTitle,
                    variantIdByTitle,
                    defaultVariantByProduct,
                    legacySaleVariantByProduct );
                return new ProductHistoryPaymentEvent
                {
                    DateUtc = p.VatReportExpense.ExpenseDateUtc.ToString( "O" ),
                    ExpenseId = p.VatReportExpenseId,
                    ReportId = p.VatReportExpense.VatReportId,
                    SupplierId = p.VatReportExpense.SupplierId,
                    SupplierName = p.VatReportExpense.Supplier?.Name ?? string.Empty,
                    InvoiceNumber = p.VatReportExpense.InvoiceNumber ?? string.Empty,
                    ShopifyVariantId = variantId,
                    VariantTitle = ProductLedgerService.ResolvePaymentDisplayVariantTitle(
                        variantId,
                        p.ProductTitle,
                        variantTitles ),
                    Quantity = p.Quantity
                };
            } )
            .OrderByDescending( x => x.DateUtc, StringComparer.Ordinal )
            .ToList();

        return new ProductHistoryResponse
        {
            ShopifyProductId = normalizedProductId,
            ProductName = productName,
            Supplies = supplies,
            Sales = sales,
            Payments = payments
        };
    }

    private async Task<string> ResolveProductNameAsync( string normalizedProductId, List<string> productIdCandidates )
    {
        try
        {
            IReadOnlyDictionary<string, string> catalogNames =
                await _variantLookup.GetProductTitleByIdMapCachedAsync();
            if (catalogNames.TryGetValue( normalizedProductId, out string? catalogName ) &&
                !string.IsNullOrWhiteSpace( catalogName ))
            {
                return catalogName.Trim();
            }
        }
        catch
        {
            // Fall back to report row titles below.
        }

        string? fromRowItem = await _db.VatReportRowItems
            .AsNoTracking()
            .Where( i => productIdCandidates.Contains( i.ShopifyProductId ) && i.ProductTitle != "" )
            .Select( i => i.ProductTitle )
            .FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace( fromRowItem ))
        {
            return fromRowItem.Trim();
        }

        string? fromExpense = await _db.VatReportExpenseProducts
            .AsNoTracking()
            .Where( p => productIdCandidates.Contains( p.ShopifyProductId ) && p.ProductTitle != "" )
            .Select( p => p.ProductTitle )
            .FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace( fromExpense ))
        {
            return fromExpense.Trim();
        }

        return normalizedProductId;
    }

    private static string ResolveVariantTitle( string variantId, IReadOnlyDictionary<string, string> variantTitles )
    {
        if (string.IsNullOrWhiteSpace( variantId ))
        {
            return string.Empty;
        }

        return variantTitles.TryGetValue( variantId, out string? title ) ? title : string.Empty;
    }

    private async Task<(Dictionary<string, string> TitleById, Dictionary<string, Dictionary<string, string>> IdByTitleByProduct)> GetVariantCatalogMapsCachedAsync()
    {
        IReadOnlyDictionary<string, string> titleById = await _variantLookup.GetVariantTitleByIdMapCachedAsync();
        IReadOnlyDictionary<string, Dictionary<string, string>> idByTitleByProduct =
            await _variantLookup.GetVariantIdByProductTitleMapCachedAsync();
        return (
            new Dictionary<string, string>( titleById, StringComparer.OrdinalIgnoreCase ),
            idByTitleByProduct.ToDictionary(
                entry => entry.Key,
                entry => new Dictionary<string, string>( entry.Value, StringComparer.OrdinalIgnoreCase ),
                StringComparer.OrdinalIgnoreCase ) );
    }

    public async Task<IReadOnlyDictionary<string, string>> GetVariantTitleByIdMapCachedAsync() =>
        await _variantLookup.GetVariantTitleByIdMapCachedAsync();

    public async Task<IReadOnlyDictionary<string, Dictionary<string, string>>> GetVariantIdByProductTitleMapCachedAsync() =>
        await _variantLookup.GetVariantIdByProductTitleMapCachedAsync();
}
