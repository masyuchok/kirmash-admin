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

        SupplyEnrichmentMaps maps = await LoadSupplyEnrichmentMapsAsync();
        List<ShopifyCatalogProduct> catalogProducts =
            await _catalog.FetchAllProductsAsync( session.Shop, session.AccessToken );

        return MapCatalogProducts( catalogProducts, maps, session.Shop );
    }

    public async Task<ProductCatalogPageResponse> GetProductsCatalogPageAsync(
        string? search,
        IReadOnlyList<string>? productTypes,
        string? after,
        int pageSize,
        bool includeProductTypes )
    {
        ShopifySession session = ShopifySessionReader.Require(
            _httpContextAccessor,
            "Няма Shopify-кантэксту для загрузкі прадуктаў."
        );

        string? query = BuildShopifyCatalogQuery( search, productTypes );
        ShopifyCatalogPage page = await _catalog.FetchProductsPageAsync(
            session.Shop,
            session.AccessToken,
            pageSize,
            string.IsNullOrWhiteSpace( after ) ? null : after.Trim(),
            query );

        SupplyEnrichmentMaps maps = await LoadSupplyEnrichmentMapsAsync();
        List<ProductWithSuppliersListItem> items = MapCatalogProducts( page.Products, maps, session.Shop );

        List<string> types = [];
        if (includeProductTypes)
        {
            types = (await _catalog.FetchProductTypesAsync( session.Shop, session.AccessToken )).ToList();
        }

        return new ProductCatalogPageResponse
        {
            Items = items,
            HasNextPage = page.HasNextPage,
            EndCursor = page.EndCursor,
            ProductCreateAdminUrl = BuildProductCreateAdminUrl( session.Shop ),
            ProductTypes = types,
        };
    }

    private static string? BuildShopifyCatalogQuery(
        string? search,
        IReadOnlyList<string>? productTypes )
    {
        List<string> parts = new();
        string? trimmedSearch = string.IsNullOrWhiteSpace( search ) ? null : search.Trim();
        if (!string.IsNullOrWhiteSpace( trimmedSearch ))
        {
            string escaped = trimmedSearch.Replace( "\"", string.Empty, StringComparison.Ordinal );
            // Search across catalog fields; wildcard helps partial title matches.
            parts.Add( $"title:*{escaped}*" );
        }

        if (productTypes is { Count: > 0 })
        {
            List<string> typeClauses = productTypes
                .Where( t => !string.IsNullOrWhiteSpace( t ) )
                .Select( t => t.Trim().Replace( "\"", string.Empty, StringComparison.Ordinal ) )
                .Where( t => t.Length > 0 )
                .Select( t => $"product_type:\"{t}\"" )
                .ToList();
            if (typeClauses.Count == 1)
            {
                parts.Add( typeClauses[0] );
            }
            else if (typeClauses.Count > 1)
            {
                parts.Add( "(" + string.Join( " OR ", typeClauses ) + ")" );
            }
        }

        return parts.Count == 0 ? null : string.Join( " AND ", parts );
    }

    private static string BuildProductCreateAdminUrl( string shop )
    {
        string storeSlug = shop.Replace( ".myshopify.com", "", StringComparison.OrdinalIgnoreCase );
        return $"https://admin.shopify.com/store/{storeSlug}/products/new";
    }

    private sealed class SupplyEnrichmentMaps
    {
        public required Dictionary<string, HashSet<string>> SuppliersByProductId { get; init; }
        public required Dictionary<string, List<ProductSupplierPriceItem>> SupplierPricesByProductId { get; init; }
        public required Dictionary<string, string> LastSyncedSupplierByProductId { get; init; }
        public required Dictionary<string, List<ProductUnsyncedSupplierItem>> UnsyncedSuppliersByProductId { get; init; }
        public required Dictionary<string, int> UnsyncedQuantityByProductId { get; init; }
    }

    private async Task<SupplyEnrichmentMaps> LoadSupplyEnrichmentMapsAsync()
    {
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
                    {
                        List<SupplyProduct> ordered = supplierGroup
                            .OrderByDescending( sp => sp.Supply.Date )
                            .ThenByDescending( sp => sp.Supply.Id )
                            .ToList();
                        SupplyProduct priceSource =
                            ordered.FirstOrDefault( sp => sp.SupplierPrice > 0 )
                            ?? ordered[0];
                        SupplyProduct saleSource =
                            ordered.FirstOrDefault( sp => sp.SalePrice > 0 )
                            ?? priceSource;

                        return new ProductSupplierPriceItem
                        {
                            SupplierId = priceSource.Supply.SupplierId,
                            SupplierName = priceSource.Supply.Supplier.Name,
                            SupplierPrice = priceSource.SupplierPrice,
                            SalePrice = saleSource.SalePrice
                        };
                    } )
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

        return new SupplyEnrichmentMaps
        {
            SuppliersByProductId = suppliersByProductId,
            SupplierPricesByProductId = supplierPricesByProductId,
            LastSyncedSupplierByProductId = lastSyncedSupplierByProductId,
            UnsyncedSuppliersByProductId = unsyncedSuppliersByProductId,
            UnsyncedQuantityByProductId = unsyncedQuantityByProductId,
        };
    }

    private static List<ProductWithSuppliersListItem> MapCatalogProducts(
        IReadOnlyList<ShopifyCatalogProduct> catalogProducts,
        SupplyEnrichmentMaps maps,
        string shop )
    {
        string storeSlug = shop.Replace( ".myshopify.com", "", StringComparison.OrdinalIgnoreCase );
        List<ProductWithSuppliersListItem> result = new();

        foreach (ShopifyCatalogProduct product in catalogProducts)
        {
            maps.SuppliersByProductId.TryGetValue( product.ProductId, out HashSet<string>? suppliersSet );
            List<string> suppliers = (suppliersSet ?? [])
                .OrderBy( n => n, StringComparer.OrdinalIgnoreCase )
                .ToList();
            maps.SupplierPricesByProductId.TryGetValue(
                product.ProductId,
                out List<ProductSupplierPriceItem>? supplierPrices );
            maps.LastSyncedSupplierByProductId.TryGetValue(
                product.ProductId,
                out string? lastSyncedSupplierName );
            maps.UnsyncedSuppliersByProductId.TryGetValue(
                product.ProductId,
                out List<ProductUnsyncedSupplierItem>? unsyncedSuppliers );
            bool hasSupplyQuantityOverride =
                maps.UnsyncedQuantityByProductId.TryGetValue( product.ProductId, out int overrideQuantity );
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
                OverpaidLines = [],
                ShopifySalePrice = product.SalePrice
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

        List<KirmaBukinistkaOffer> acceptedBukinistkaOffers = await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .Where( x =>
                x.Direction == KirmaBukinistkaOfferDirections.BukinistkaToKirma
                && x.Status == KirmaBukinistkaOfferStatuses.Accepted
                && productIdCandidates.Contains( x.ShopifyProductId ) )
            .ToListAsync();

        List<ProductHistoryBukinistkaOfferEvent> bukinistkaOffers = acceptedBukinistkaOffers
            .Where( x => ProductLedgerService.MatchesVariantFilter(
                VariantLegacyDefaults.ResolveVariantId(
                    normalizedProductId,
                    x.ShopifyVariantId,
                    defaultVariantByProduct,
                    variantIdByTitle,
                    legacySaleVariantByProduct ),
                ResolveVariantTitle(
                    VariantLegacyDefaults.ResolveVariantId(
                        normalizedProductId,
                        x.ShopifyVariantId,
                        defaultVariantByProduct,
                        variantIdByTitle,
                        legacySaleVariantByProduct ),
                    variantTitles ),
                normalizedVariantFilter,
                filterVariantTitle ) )
            .Select( x =>
            {
                string variantId = VariantLegacyDefaults.ResolveVariantId(
                    normalizedProductId,
                    x.ShopifyVariantId,
                    defaultVariantByProduct,
                    variantIdByTitle,
                    legacySaleVariantByProduct );
                return new ProductHistoryBukinistkaOfferEvent
                {
                    DateUtc = (x.AcceptedAtUtc ?? x.CreatedAtUtc).ToString( "O" ),
                    OfferId = x.Id,
                    ShopifyVariantId = variantId,
                    VariantTitle = ResolveVariantTitle( variantId, variantTitles ),
                    Quantity = x.Quantity,
                    GrossUnitCost = Math.Round( x.GrossUnitCost, 2, MidpointRounding.AwayFromZero ),
                    IsAssignment = x.IsAssignment,
                };
            } )
            .OrderByDescending( x => x.DateUtc, StringComparer.Ordinal )
            .ThenByDescending( x => x.OfferId )
            .ToList();

        return new ProductHistoryResponse
        {
            ShopifyProductId = normalizedProductId,
            ProductName = productName,
            Supplies = supplies,
            Sales = sales,
            Payments = payments,
            BukinistkaOffers = bukinistkaOffers
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
