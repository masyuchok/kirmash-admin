using backend.Data;
using backend.Models;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// Shared supply/sale/payment logic for product history and supplier inventory.
/// Reported VAT periods use report rows; other months use Shopify order data.
/// </summary>
public class ProductLedgerService
{
    private static readonly TimeSpan SoldByLineCacheTtl = TimeSpan.FromMinutes( 10 );
    private static readonly SemaphoreSlim SoldByLineCacheLock = new( 1, 1 );
    private static ProductSoldAllocation? _soldByLineCache;
    private static DateTime _soldByLineCachedAtUtc;

    private readonly AppDbContext _db;
    private readonly ShopifyVariantLookupService _variantLookup;
    private readonly ShopifyOrderFetchService _shopifyOrders;
    private readonly VatReportGenerationService _generation;

    public ProductLedgerService(
        AppDbContext db,
        ShopifyVariantLookupService variantLookup,
        ShopifyOrderFetchService shopifyOrders,
        VatReportGenerationService generation )
    {
        _db = db;
        _variantLookup = variantLookup;
        _shopifyOrders = shopifyOrders;
        _generation = generation;
    }

    /// <summary>
    /// Sold quantities for inventory: reported months from VAT report rows,
    /// unreported months from <see cref="InventoryProductSales"/> cache (populated via
    /// <see cref="InventorySalesCacheService.EnsureFreshAsync"/>).
    /// Does not repair empty report rows or call Shopify for unresolved report lines (fast path).
    /// </summary>
    public async Task<ProductSoldAllocation> GetSoldByLineAsync()
    {
        if (_soldByLineCache is not null &&
            DateTime.UtcNow - _soldByLineCachedAtUtc < SoldByLineCacheTtl)
        {
            return _soldByLineCache;
        }

        await SoldByLineCacheLock.WaitAsync();
        try
        {
            if (_soldByLineCache is not null &&
                DateTime.UtcNow - _soldByLineCachedAtUtc < SoldByLineCacheTtl)
            {
                return _soldByLineCache;
            }

            LedgerVariantContext variantContext = await LoadVariantContextAsync();
            ProductSoldAllocation allocation = new()
            {
                SoldByLine = new Dictionary<(string ProductId, string VariantId), int>(
                    ProductVariantKeyComparer.Instance ),
                LegacyUnnamedSoldByProduct = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase )
            };

            List<ProductLedgerSaleLine> reportLinesForAllocation =
                await GetReportSaleLinesAsync( variantContext, repairUnresolved: false );

            foreach (ProductLedgerSaleLine line in reportLinesForAllocation)
            {
                AddSaleLineToAllocation( line, variantContext, allocation );
            }

            foreach (ProductLedgerSaleLine line in await GetShopifySaleLinesFromCacheAsync( variantContext ))
            {
                AddSaleLineToAllocation( line, variantContext, allocation );
            }

            _soldByLineCache = allocation;
            _soldByLineCachedAtUtc = DateTime.UtcNow;
            return allocation;
        }
        finally
        {
            SoldByLineCacheLock.Release();
        }
    }

    public static void InvalidateSoldByLineCache()
    {
        _soldByLineCache = null;
        _soldByLineCachedAtUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Sold qty by product line using the same loaders and matching rules as product history.
    /// </summary>
    public async Task<ProductSoldAllocation> GetSoldByLineForProductsAsync(
        IReadOnlyList<(string ProductId, string? ProductName)> products )
    {
        LedgerVariantContext variantContext = await LoadVariantContextAsync();
        ProductSoldAllocation allocation = new()
        {
            SoldByLine = new Dictionary<(string ProductId, string VariantId), int>(
                ProductVariantKeyComparer.Instance ),
            LegacyUnnamedSoldByProduct = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase )
        };

        if (products.Count == 0)
        {
            return allocation;
        }

        HashSet<string> seenSaleKeys = new( StringComparer.OrdinalIgnoreCase );
        HashSet<(int Year, int Month)> reportedPeriods = await GetReportedPeriodsAsync();
        HashSet<string> processedProductIds = new( StringComparer.OrdinalIgnoreCase );

        foreach ((string rawProductId, string? productName) in products)
        {
            string normalizedProductId = ShopifyIds.NormalizeProductId( rawProductId );
            if (string.IsNullOrWhiteSpace( normalizedProductId ) ||
                !processedProductIds.Add( normalizedProductId ))
            {
                continue;
            }

            List<string> productIdCandidates =
                await BuildExpandedProductIdCandidatesAsync( normalizedProductId, productName );
            HashSet<string> normalizedCandidateIds = BuildNormalizedProductIdSet( productIdCandidates );
            string? productIsbn = ResolveContextProductIsbn( normalizedProductId, variantContext );

            List<ProductLedgerSaleLine> lines = await LoadReportSaleLinesForProductAsync(
                variantContext,
                normalizedProductId,
                productIdCandidates,
                normalizedCandidateIds,
                productName );

            await ApplyShopifyRefundAdjustmentsAsync(
                lines,
                variantContext,
                reportedPeriods );

            List<InventoryProductSale> cachedShopifySales = await _db.InventoryProductSales
                .AsNoTracking()
                .Where( row =>
                    row.PeriodYear > 0 &&
                    row.PeriodMonth > 0 &&
                    productIdCandidates.Contains( row.ShopifyProductId ) )
                .ToListAsync();

            foreach (InventoryProductSale row in cachedShopifySales)
            {
                if (reportedPeriods.Contains( (row.PeriodYear, row.PeriodMonth) )) continue;
                if (row.SoldQuantity <= 0) continue;

                string productId = ShopifyIds.NormalizeProductId( row.ShopifyProductId );
                string variantId = ResolveEffectiveVariantId(
                    row.ShopifyProductId,
                    row.ShopifyVariantId,
                    string.Empty,
                    variantContext.VariantIdByTitle,
                    variantContext.DefaultVariantByProduct,
                    variantContext.LegacySaleVariantByProduct );

                ProductLedgerSaleLine shopifyLine = new()
                {
                    ProductId = productId,
                    VariantId = variantId,
                    Quantity = row.SoldQuantity,
                    Source = "shopify",
                    DateUtc = new DateTime( row.PeriodYear, row.PeriodMonth, 1, 0, 0, 0, DateTimeKind.Utc )
                };
                if (!SaleLineMatchesProduct( shopifyLine, normalizedCandidateIds, productIsbn ))
                {
                    continue;
                }

                shopifyLine.ProductId = normalizedProductId;
                lines.Add( shopifyLine );
            }

            foreach (ProductLedgerSaleLine line in lines)
            {
                string dedupKey = BuildSaleLineDedupKey( line );
                if (!seenSaleKeys.Add( dedupKey ))
                {
                    continue;
                }

                AddSaleLineToAllocation( line, variantContext, allocation );
            }
        }

        return allocation;
    }

    public async Task<List<ProductHistorySaleEvent>> GetSaleEventsForProductAsync(
        string normalizedProductId,
        string? normalizedVariantFilter,
        string? filterVariantTitle,
        int? supplierId = null,
        string? productName = null,
        bool matchByProductIdOnly = false,
        bool loadLiveShopifyOrders = false )
    {
        List<string> productIdCandidates = matchByProductIdOnly
            ? BuildProductIdCandidates( normalizedProductId )
            : await BuildExpandedProductIdCandidatesAsync( normalizedProductId, productName );
        HashSet<string> normalizedCandidateIds = BuildNormalizedProductIdSet( productIdCandidates );
        LedgerVariantContext variantContext = await LoadVariantContextAsync();
        string? productIsbn = ResolveContextProductIsbn( normalizedProductId, variantContext );
        List<ProductHistorySaleEvent> sales = new();

        List<ProductLedgerSaleLine> reportLines = await LoadReportSaleLinesForProductAsync(
            variantContext,
            normalizedProductId,
            productIdCandidates,
            normalizedCandidateIds,
            productName,
            matchByProductIdOnly );

        if (loadLiveShopifyOrders)
        {
            await AppendSaleLinesFromUnresolvedReportRowsAsync( reportLines, variantContext );

            List<ProductLedgerSaleLine> shopifyOrderLines = await GetShopifySaleEventsForProductAsync(
                normalizedProductId,
                productIdCandidates,
                variantContext );
            HashSet<string> existingSaleKeys = reportLines
                .Select( BuildSaleLineDedupKey )
                .ToHashSet( StringComparer.OrdinalIgnoreCase );
            foreach (ProductLedgerSaleLine shopifyOrderLine in shopifyOrderLines)
            {
                string dedupKey = BuildSaleLineDedupKey( shopifyOrderLine );
                if (!existingSaleKeys.Add( dedupKey ))
                {
                    continue;
                }

                reportLines.Add( shopifyOrderLine );
            }
        }

        HashSet<(int Year, int Month)> reportedPeriods = await GetReportedPeriodsAsync();
        await ApplyShopifyRefundAdjustmentsAsync(
            reportLines,
            variantContext,
            reportedPeriods );

        List<InventoryProductSale> cachedShopifySales = await _db.InventoryProductSales
            .AsNoTracking()
            .Where( row =>
                row.PeriodYear > 0 &&
                row.PeriodMonth > 0 &&
                productIdCandidates.Contains( row.ShopifyProductId ) )
            .ToListAsync();

        foreach (InventoryProductSale row in cachedShopifySales)
        {
            if (reportedPeriods.Contains( (row.PeriodYear, row.PeriodMonth) )) continue;
            if (row.SoldQuantity <= 0) continue;

            string productId = ShopifyIds.NormalizeProductId( row.ShopifyProductId );
            string variantId = ResolveEffectiveVariantId(
                row.ShopifyProductId,
                row.ShopifyVariantId,
                string.Empty,
                variantContext.VariantIdByTitle,
                variantContext.DefaultVariantByProduct,
                variantContext.LegacySaleVariantByProduct );

            ProductLedgerSaleLine shopifyLine = new()
            {
                ProductId = productId,
                VariantId = variantId,
                Quantity = row.SoldQuantity,
                Source = "shopify",
                DateUtc = new DateTime( row.PeriodYear, row.PeriodMonth, 1, 0, 0, 0, DateTimeKind.Utc )
            };
            if (!SaleLineMatchesProduct( shopifyLine, normalizedCandidateIds, productIsbn, matchByProductIdOnly ))
            {
                continue;
            }

            shopifyLine.ProductId = normalizedProductId;
            reportLines.Add( shopifyLine );
        }

        await AssignLegacySaleVariantsBySupplierFifoAsync(
            reportLines,
            normalizedCandidateIds,
            productIdCandidates,
            supplierId,
            variantContext );

        foreach (ProductLedgerSaleLine line in reportLines)
        {
            if (!SaleLineMatchesProduct( line, normalizedCandidateIds, productIsbn, matchByProductIdOnly ))
            {
                continue;
            }

            string resolvedVariantId = VariantLegacyDefaults.ResolveVariantId(
                line.ProductId,
                line.VariantId,
                variantContext.DefaultVariantByProduct,
                variantContext.VariantIdByTitle,
                variantContext.LegacySaleVariantByProduct );
            string variantTitle = ResolveLineVariantTitle(
                line.ProductId,
                resolvedVariantId,
                line.VariantTitle,
                variantContext );
            if (!MatchesVariantFilter(
                    resolvedVariantId,
                    variantTitle,
                    normalizedVariantFilter,
                    filterVariantTitle ))
            {
                continue;
            }

            sales.Add( new ProductHistorySaleEvent
            {
                DateUtc = line.DateUtc.ToString( "O" ),
                Source = line.Source,
                OrderNumber = line.OrderNumber,
                ReportId = line.ReportId,
                ShopifyVariantId = resolvedVariantId,
                VariantTitle = variantTitle,
                Quantity = line.Quantity
            } );
        }

        return sales
            .OrderByDescending( x => x.DateUtc, StringComparer.Ordinal )
            .ToList();
    }

    public async Task<string> ResolveProductNameForLedgerAsync( string normalizedProductId )
    {
        List<string> productIdCandidates = BuildProductIdCandidates( normalizedProductId );
        try
        {
            LedgerVariantContext variantContext = await LoadVariantContextAsync();
            if (variantContext.ProductTitleById.TryGetValue( normalizedProductId, out string? catalogName ) &&
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

        string? fromCash = await _db.VatReportCashSales
            .AsNoTracking()
            .Where( s => productIdCandidates.Contains( s.ShopifyProductId ) && s.ProductTitle != "" )
            .Select( s => s.ProductTitle )
            .FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace( fromCash ))
        {
            return fromCash.Trim();
        }

        return normalizedProductId;
    }

    /// <summary>
    /// Total sold qty for a product using the same pipeline as product history.
    /// </summary>
    public async Task<int> GetTotalSoldQuantityForProductAsync(
        string normalizedProductId,
        string? productName = null,
        int? supplierId = null )
    {
        List<ProductHistorySaleEvent> sales = await GetSaleEventsForProductAsync(
            normalizedProductId,
            normalizedVariantFilter: null,
            filterVariantTitle: null,
            supplierId,
            productName );
        return sales.Sum( sale => sale.Quantity );
    }

    public async Task<HashSet<(int Year, int Month)>> GetReportedPeriodsAsync()
    {
        List<(int Year, int Month)> periods = await _db.VatReports
            .AsNoTracking()
            .Select( r => new ValueTuple<int, int>( r.PeriodYear, r.PeriodMonth ) )
            .Distinct()
            .ToListAsync();

        return periods.ToHashSet();
    }

    public async Task<List<(int Year, int Month)>> GetUnreportedPeriodsAsync()
    {
        HashSet<(int Year, int Month)> reportedPeriods = await GetReportedPeriodsAsync();
        DateOnly? earliestSupplyDate = await _db.Supplies
            .AsNoTracking()
            .MinAsync( s => (DateOnly?)s.Date );
        List<DateOnly> reportMonthStarts = await _db.VatReports
            .AsNoTracking()
            .Select( r => new DateOnly( r.PeriodYear, r.PeriodMonth, 1 ) )
            .ToListAsync();
        DateOnly? earliestReportDate = reportMonthStarts.Count > 0
            ? reportMonthStarts.Min()
            : null;

        DateOnly? startDate = earliestSupplyDate ?? earliestReportDate;
        if (!startDate.HasValue)
        {
            return [];
        }

        DateOnly startMonth = new( startDate.Value.Year, startDate.Value.Month, 1 );
        DateOnly endMonth = DateOnly.FromDateTime( DateTime.UtcNow );
        List<(int Year, int Month)> unreported = new();

        for (DateOnly monthCursor = startMonth;
             monthCursor <= endMonth;
             monthCursor = monthCursor.AddMonths( 1 ))
        {
            (int Year, int Month) key = (monthCursor.Year, monthCursor.Month);
            if (!reportedPeriods.Contains( key ))
            {
                unreported.Add( key );
            }
        }

        return unreported;
    }

    public static string ResolveEffectiveVariantId(
        string shopifyProductId,
        string shopifyVariantId,
        string variantTitle,
        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle,
        IReadOnlyDictionary<string, string> defaultVariantByProduct,
        IReadOnlyDictionary<string, string>? legacySaleVariantByProduct = null )
    {
        string normalizedProductId = ShopifyIds.NormalizeProductId( shopifyProductId );
        string normalizedVariantId = ShopifyIds.NormalizeVariantId( shopifyVariantId );
        string normalizedTitle = VariantLegacyDefaults.IsDefaultVariantTitle( variantTitle )
            ? string.Empty
            : (variantTitle ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace( normalizedVariantId ) &&
            VariantLegacyDefaults.IsNamedCatalogVariantForProduct(
                normalizedProductId,
                normalizedVariantId,
                variantIdByTitle ))
        {
            return normalizedVariantId;
        }

        if (!string.IsNullOrWhiteSpace( normalizedTitle ))
        {
            string fromTitle = ShopifyVariantLookupService.ResolveVariantIdByProductTitle(
                shopifyProductId,
                normalizedTitle,
                variantIdByTitle );
            if (!string.IsNullOrWhiteSpace( fromTitle ))
            {
                return fromTitle;
            }
        }

        if (VariantLegacyDefaults.IsLegacyUnnamedSaleLine(
                normalizedProductId,
                normalizedVariantId,
                normalizedTitle,
                variantIdByTitle ) &&
            legacySaleVariantByProduct is not null &&
            legacySaleVariantByProduct.TryGetValue( normalizedProductId, out string? ledgerVariant ) &&
            !string.IsNullOrWhiteSpace( ledgerVariant ))
        {
            return ledgerVariant;
        }

        if (VariantLegacyDefaults.GetNamedVariantCount( normalizedProductId, variantIdByTitle ) > 1)
        {
            return string.Empty;
        }

        return VariantLegacyDefaults.ResolveVariantId(
            normalizedProductId,
            string.Empty,
            defaultVariantByProduct,
            variantIdByTitle );
    }

    public static string ResolvePaymentVariantId(
        string shopifyProductId,
        string shopifyVariantId,
        IReadOnlyDictionary<string, string> defaultVariantByProduct,
        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle,
        IReadOnlyDictionary<string, string>? legacySaleVariantByProduct = null ) =>
        VariantLegacyDefaults.ResolveVariantId(
            shopifyProductId,
            shopifyVariantId,
            defaultVariantByProduct,
            variantIdByTitle,
            legacySaleVariantByProduct );

    public static string BuildStrictProductLineKey(
        string shopifyProductId,
        string shopifyVariantId,
        IReadOnlyDictionary<string, string> defaultVariantByProduct,
        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle,
        IReadOnlyDictionary<string, string>? legacySaleVariantByProduct = null ) =>
        BuildStrictProductLineKey(
            shopifyProductId,
            shopifyVariantId,
            null,
            defaultVariantByProduct,
            variantIdByTitle,
            legacySaleVariantByProduct );

    public static string BuildStrictProductLineKey(
        string shopifyProductId,
        string shopifyVariantId,
        string? productTitle,
        IReadOnlyDictionary<string, string> defaultVariantByProduct,
        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle,
        IReadOnlyDictionary<string, string>? legacySaleVariantByProduct = null )
    {
        string productId = ShopifyIds.NormalizeProductId( shopifyProductId );
        string variantId = ResolveEffectiveVariantId(
            shopifyProductId,
            shopifyVariantId,
            VatReportHelpers.ExtractVariantTitleFromProductLineTitle( productTitle ),
            variantIdByTitle,
            defaultVariantByProduct,
            legacySaleVariantByProduct );
        return VatReportHelpers.BuildProductLineKey( productId, variantId );
    }

    public static bool MatchesStrictProductLineKey(
        string shopifyProductId,
        string shopifyVariantId,
        string lineKey,
        IReadOnlyDictionary<string, string> defaultVariantByProduct,
        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle,
        IReadOnlyDictionary<string, string>? legacySaleVariantByProduct = null ) =>
        string.Equals(
            BuildStrictProductLineKey(
                shopifyProductId,
                shopifyVariantId,
                defaultVariantByProduct,
                variantIdByTitle,
                legacySaleVariantByProduct ),
            lineKey,
            StringComparison.OrdinalIgnoreCase );

    public static bool MatchesVariantFilter(
        string lineVariantId,
        string lineVariantTitle,
        string? normalizedVariantFilter,
        string? filterVariantTitle )
    {
        if (string.IsNullOrWhiteSpace( normalizedVariantFilter ) && string.IsNullOrWhiteSpace( filterVariantTitle ))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace( normalizedVariantFilter ))
        {
            string lineVariant = ShopifyIds.NormalizeVariantId( lineVariantId );
            if (!string.IsNullOrWhiteSpace( lineVariant ) &&
                string.Equals( lineVariant, normalizedVariantFilter, StringComparison.OrdinalIgnoreCase ))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace( filterVariantTitle ))
        {
            string title = (lineVariantTitle ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace( title ) &&
                string.Equals( title, filterVariantTitle, StringComparison.OrdinalIgnoreCase ))
            {
                return true;
            }
        }

        return false;
    }

    public static string ResolvePaymentVariantForHistory(
        string shopifyProductId,
        string shopifyVariantId,
        string? productTitle,
        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle,
        IReadOnlyDictionary<string, string> defaultVariantByProduct,
        IReadOnlyDictionary<string, string>? legacySaleVariantByProduct = null ) =>
        ResolveEffectiveVariantId(
            shopifyProductId,
            shopifyVariantId,
            VatReportHelpers.ExtractVariantTitleFromProductLineTitle( productTitle ),
            variantIdByTitle,
            defaultVariantByProduct,
            legacySaleVariantByProduct );

    public static string ResolvePaymentDisplayVariantTitle(
        string resolvedVariantId,
        string? productTitle,
        IReadOnlyDictionary<string, string> variantTitles )
    {
        string explicitTitle = VatReportHelpers.ExtractVariantTitleFromProductLineTitle( productTitle );
        if (!string.IsNullOrWhiteSpace( explicitTitle ))
        {
            return explicitTitle.Trim();
        }

        if (string.IsNullOrWhiteSpace( resolvedVariantId ))
        {
            return string.Empty;
        }

        return variantTitles.TryGetValue( resolvedVariantId, out string? title )
            ? title
            : string.Empty;
    }

    public static bool PaymentLineMatchesProduct(
        string normalizedProductId,
        string rawProductId,
        string? productTitle,
        string? productName,
        IEnumerable<string>? productIdCandidates = null )
    {
        string lineProductId = ShopifyIds.NormalizeProductId( rawProductId );
        if (!string.IsNullOrWhiteSpace( lineProductId ))
        {
            return string.Equals( lineProductId, normalizedProductId, StringComparison.OrdinalIgnoreCase );
        }

        return !string.IsNullOrWhiteSpace( productName ) &&
               VatReportHelpers.ProductTitlesMatch( productTitle ?? string.Empty, productName );
    }

    /// <summary>
    /// Variant filter for supplier payments: when the expense line title names a variant,
    /// only that variant matches; unnamed lines on multi-variant products are excluded.
    /// </summary>
    public static bool MatchesPaymentVariantFilter(
        string shopifyProductId,
        string shopifyVariantId,
        string? productTitle,
        string? normalizedVariantFilter,
        string? filterVariantTitle,
        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle,
        IReadOnlyDictionary<string, string> defaultVariantByProduct,
        IReadOnlyDictionary<string, string> variantTitles,
        IReadOnlyDictionary<string, string>? legacySaleVariantByProduct = null )
    {
        if (string.IsNullOrWhiteSpace( normalizedVariantFilter ) && string.IsNullOrWhiteSpace( filterVariantTitle ))
        {
            return true;
        }

        string normalizedProductId = ShopifyIds.NormalizeProductId( shopifyProductId );
        string explicitVariantTitle = VatReportHelpers.ExtractVariantTitleFromProductLineTitle( productTitle );
        int namedVariantCount = VariantLegacyDefaults.GetNamedVariantCount( normalizedProductId, variantIdByTitle );

        if (!string.IsNullOrWhiteSpace( explicitVariantTitle ))
        {
            if (!string.IsNullOrWhiteSpace( filterVariantTitle ))
            {
                return string.Equals( explicitVariantTitle, filterVariantTitle, StringComparison.OrdinalIgnoreCase ) ||
                       VatReportHelpers.VariantTitlesEquivalentForPaymentMatch(
                           explicitVariantTitle,
                           filterVariantTitle );
            }

            string resolvedFromExplicit = ShopifyVariantLookupService.ResolveVariantIdByProductTitle(
                shopifyProductId,
                explicitVariantTitle,
                variantIdByTitle );
            if (!string.IsNullOrWhiteSpace( normalizedVariantFilter ) &&
                !string.IsNullOrWhiteSpace( resolvedFromExplicit ))
            {
                return string.Equals(
                    ShopifyIds.NormalizeVariantId( resolvedFromExplicit ),
                    normalizedVariantFilter,
                    StringComparison.OrdinalIgnoreCase );
            }

            return false;
        }

        if (namedVariantCount > 1 && string.IsNullOrWhiteSpace( ShopifyIds.NormalizeVariantId( shopifyVariantId ) ))
        {
            return false;
        }

        string resolvedVariantId = ResolvePaymentVariantForHistory(
            shopifyProductId,
            shopifyVariantId,
            productTitle,
            variantIdByTitle,
            defaultVariantByProduct,
            legacySaleVariantByProduct );
        string displayVariantTitle = ResolvePaymentDisplayVariantTitle(
            resolvedVariantId,
            productTitle,
            variantTitles );
        return MatchesVariantFilter(
            resolvedVariantId,
            displayVariantTitle,
            normalizedVariantFilter,
            filterVariantTitle );
    }

    public Task<List<string>> BuildExpandedProductIdCandidatesAsync(
        string normalizedProductId,
        string? productName )
    {
        _ = productName;
        return Task.FromResult( BuildProductIdCandidates( normalizedProductId ) );
    }

    private async Task<List<ProductLedgerSaleLine>> LoadReportSaleLinesForProductAsync(
        LedgerVariantContext variantContext,
        string normalizedProductId,
        List<string> productIdCandidates,
        HashSet<string> normalizedCandidateIds,
        string? productName,
        bool matchByProductIdOnly = false )
    {
        List<ProductLedgerSaleLine> lines = new();
        string? productIsbn = ResolveContextProductIsbn( normalizedProductId, variantContext );

        IQueryable<VatReportRowItem> orderQuery = _db.VatReportRowItems
            .AsNoTracking()
            .Include( i => i.VatReportRow )
            .ThenInclude( r => r.VatReport )
            .Where( i => i.Quantity > 0 );

        List<VatReportRowItem> orderItems = matchByProductIdOnly
            ? await orderQuery
                .Where( i => productIdCandidates.Contains( i.ShopifyProductId ) )
                .ToListAsync()
            : await orderQuery
                .Where( i =>
                    productIdCandidates.Contains( i.ShopifyProductId ) || i.ShopifyProductId == "" )
                .ToListAsync();

        if (!matchByProductIdOnly)
        {
            orderItems = orderItems
                .Where( i => ReportItemMatchesProduct( i, normalizedCandidateIds, productIsbn, matchByProductIdOnly ) )
                .ToList();
        }

        foreach (VatReportRowItem item in orderItems)
        {
            ProductLedgerSaleLine saleLine = CreateReportOrderSaleLine( item, item.VatReportRow, variantContext );
            if (string.IsNullOrWhiteSpace( saleLine.ProductId ))
            {
                string? canonicalProductId = ResolveCanonicalProductId(
                    saleLine.ProductId,
                    item.Barcode,
                    variantContext.ProductIdByIsbn,
                    variantContext.IsbnByProductId );
                if (string.IsNullOrWhiteSpace( canonicalProductId ) ||
                    !string.Equals( canonicalProductId, normalizedProductId, StringComparison.OrdinalIgnoreCase ))
                {
                    continue;
                }

                saleLine.ProductId = canonicalProductId;
            }

            if (!SaleLineMatchesProduct( saleLine, normalizedCandidateIds, productIsbn, matchByProductIdOnly ))
            {
                continue;
            }

            saleLine.ProductId = normalizedProductId;
            lines.Add( saleLine );
        }

        List<VatReportCashSale> cashSales = matchByProductIdOnly
            ? await _db.VatReportCashSales
                .AsNoTracking()
                .Include( s => s.VatReport )
                .Where( s => s.Quantity > 0 && productIdCandidates.Contains( s.ShopifyProductId ) )
                .ToListAsync()
            : await _db.VatReportCashSales
                .AsNoTracking()
                .Include( s => s.VatReport )
                .Where( s =>
                    s.Quantity > 0 &&
                    (productIdCandidates.Contains( s.ShopifyProductId ) || s.ShopifyProductId == "") )
                .ToListAsync();

        if (matchByProductIdOnly || string.IsNullOrWhiteSpace( productIsbn ))
        {
            cashSales = cashSales
                .Where( s => productIdCandidates.Contains( s.ShopifyProductId ) )
                .ToList();
        }

        foreach (VatReportCashSale sale in cashSales)
        {
            string productId = ShopifyIds.NormalizeProductId( sale.ShopifyProductId );
            string variantTitleFromProduct = VatReportHelpers.ExtractVariantTitleFromProductLineTitle( sale.ProductTitle );
            string variantId = ResolveEffectiveVariantId(
                string.IsNullOrWhiteSpace( productId ) ? normalizedProductId : sale.ShopifyProductId,
                sale.ShopifyVariantId,
                variantTitleFromProduct,
                variantContext.VariantIdByTitle,
                variantContext.DefaultVariantByProduct,
                variantContext.LegacySaleVariantByProduct );

            ProductLedgerSaleLine cashLine = new()
            {
                ProductId = string.IsNullOrWhiteSpace( productId ) ? normalizedProductId : productId,
                VariantId = variantId,
                VariantTitle = variantTitleFromProduct,
                ProductTitle = sale.ProductTitle ?? string.Empty,
                Quantity = sale.Quantity,
                Source = "cash",
                DateUtc = VatReportHelpers.ResolveCashSaleDateUtc(
                    sale.VatReport.PeriodYear,
                    sale.VatReport.PeriodMonth ),
                OrderNumber = string.Empty,
                ReportId = sale.VatReportId
            };
            if (!SaleLineMatchesProduct( cashLine, normalizedCandidateIds, productIsbn, matchByProductIdOnly ))
            {
                continue;
            }

            cashLine.ProductId = normalizedProductId;
            lines.Add( cashLine );
        }

        return lines;
    }

    private static HashSet<string> BuildNormalizedProductIdSet( IEnumerable<string> productIdCandidates ) =>
        productIdCandidates
            .Select( ShopifyIds.NormalizeProductId )
            .Where( id => !string.IsNullOrWhiteSpace( id ) )
            .ToHashSet( StringComparer.OrdinalIgnoreCase );

    private static void AddProductIdCandidate( HashSet<string> candidates, string rawProductId )
    {
        string productId = ShopifyIds.NormalizeProductId( rawProductId );
        if (string.IsNullOrWhiteSpace( productId ))
        {
            return;
        }

        candidates.Add( productId );
        candidates.Add( $"gid://shopify/Product/{productId}" );
    }

    private static bool ReportItemMatchesProduct(
        VatReportRowItem item,
        HashSet<string> normalizedCandidateIds,
        string? productIsbn,
        bool matchByProductIdOnly )
    {
        string lineProductId = ShopifyIds.NormalizeProductId( item.ShopifyProductId );
        if (!string.IsNullOrWhiteSpace( lineProductId ) && normalizedCandidateIds.Contains( lineProductId ))
        {
            return true;
        }

        if (matchByProductIdOnly)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace( productIsbn ) &&
               VatReportHelpers.IsbnsMatch( item.Barcode, productIsbn );
    }

    private static string? ResolveContextProductIsbn(
        string normalizedProductId,
        LedgerVariantContext variantContext )
    {
        if (variantContext.IsbnByProductId.TryGetValue( normalizedProductId, out string? isbn ) &&
            !string.IsNullOrWhiteSpace( isbn ))
        {
            return isbn;
        }

        return null;
    }

    private static string ResolveLineIsbn( ProductLedgerSaleLine line )
    {
        string fromBarcode = VatReportHelpers.NormalizeIsbn( line.Barcode );
        if (!string.IsNullOrWhiteSpace( fromBarcode ))
        {
            return fromBarcode;
        }

        return VatReportHelpers.ExtractIsbnFromText( line.ProductTitle ) ?? string.Empty;
    }

    private static bool SaleLineMatchesProduct(
        ProductLedgerSaleLine line,
        HashSet<string> normalizedCandidateIds,
        string? productIsbn,
        bool matchByProductIdOnly = false )
    {
        string lineProductId = ShopifyIds.NormalizeProductId( line.ProductId );
        if (!string.IsNullOrWhiteSpace( lineProductId ) && normalizedCandidateIds.Contains( lineProductId ))
        {
            return true;
        }

        if (matchByProductIdOnly)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace( productIsbn ))
        {
            return false;
        }

        return VatReportHelpers.IsbnsMatch( ResolveLineIsbn( line ), productIsbn );
    }

    public static List<string> BuildProductIdCandidates( string normalizedProductId )
    {
        List<string> candidates = new() { normalizedProductId };
        string gid = $"gid://shopify/Product/{normalizedProductId}";
        if (!candidates.Contains( gid, StringComparer.OrdinalIgnoreCase ))
        {
            candidates.Add( gid );
        }

        return candidates;
    }

    public static bool ProductIdMatches( string normalizedProductId, string rawProductId ) =>
        string.Equals(
            ShopifyIds.NormalizeProductId( rawProductId ),
            normalizedProductId,
            StringComparison.OrdinalIgnoreCase );

    private async Task<List<ProductLedgerSaleLine>> GetReportSaleLinesAsync(
        LedgerVariantContext variantContext,
        bool repairUnresolved = true )
    {
        if (repairUnresolved && await _db.VatReportRows.AnyAsync( r => !r.Items.Any() ))
        {
            await _generation.RepairAllRowsWithoutItemsAsync();
        }

        List<ProductLedgerSaleLine> lines = new();

        List<VatReportRowItem> orderSaleLines = await _db.VatReportRowItems
            .AsNoTracking()
            .Include( i => i.VatReportRow )
            .ThenInclude( r => r.VatReport )
            .Where( i => i.Quantity > 0 )
            .ToListAsync();

        foreach (VatReportRowItem item in orderSaleLines)
        {
            AppendReportOrderSaleLine(
                lines,
                item,
                variantContext );
        }

        if (repairUnresolved)
        {
            await AppendSaleLinesFromUnresolvedReportRowsAsync( lines, variantContext );
        }

        List<VatReportCashSale> cashSaleLines = await _db.VatReportCashSales
            .AsNoTracking()
            .Include( s => s.VatReport )
            .Where( s => s.Quantity > 0 )
            .ToListAsync();

        foreach (VatReportCashSale sale in cashSaleLines)
        {
            string? canonicalProductId = ResolveCanonicalProductId(
                sale.ShopifyProductId,
                string.Empty,
                variantContext.ProductIdByIsbn,
                variantContext.IsbnByProductId );
            if (string.IsNullOrWhiteSpace( canonicalProductId ))
            {
                continue;
            }

            string variantTitleFromProduct = VatReportHelpers.ExtractVariantTitleFromProductLineTitle( sale.ProductTitle );
            string variantId = ResolveEffectiveVariantId(
                canonicalProductId,
                sale.ShopifyVariantId,
                variantTitleFromProduct,
                variantContext.VariantIdByTitle,
                variantContext.DefaultVariantByProduct,
                variantContext.LegacySaleVariantByProduct );

            lines.Add( new ProductLedgerSaleLine
            {
                ProductId = canonicalProductId,
                VariantId = variantId,
                VariantTitle = variantTitleFromProduct,
                Quantity = sale.Quantity,
                Source = "cash",
                DateUtc = VatReportHelpers.ResolveCashSaleDateUtc(
                    sale.VatReport.PeriodYear,
                    sale.VatReport.PeriodMonth ),
                OrderNumber = string.Empty,
                ReportId = sale.VatReportId
            } );
        }

        return lines;
    }

    private async Task AppendSaleLinesFromUnresolvedReportRowsAsync(
        List<ProductLedgerSaleLine> lines,
        LedgerVariantContext variantContext )
    {
        List<VatReportRow> unresolvedRows = await _db.VatReportRows
            .AsNoTracking()
            .Include( r => r.VatReport )
            .Include( r => r.Items )
            .Where( r => !r.Items.Any() )
            .ToListAsync();
        if (unresolvedRows.Count == 0)
        {
            return;
        }

        HashSet<string> existingKeys = lines
            .Select( line => BuildSaleLineDedupKey( line ) )
            .ToHashSet( StringComparer.OrdinalIgnoreCase );

        foreach (VatReportRow row in unresolvedRows)
        {
            if (row.VatReport is null)
            {
                continue;
            }

            VatReportRow? resolved = await _generation.TryResolveRowFromShopifyAsync(
                row.VatReport.PeriodYear,
                row.VatReport.PeriodMonth,
                row.VatReport.Type,
                row.ShopifyOrderId,
                row.OrderNumber,
                row.VatRatePercent );
            if (resolved is null || resolved.Items.Count == 0)
            {
                continue;
            }

            foreach (VatReportRowItem item in resolved.Items)
            {
                ProductLedgerSaleLine saleLine = CreateReportOrderSaleLine( item, row, variantContext );
                string dedupKey = BuildSaleLineDedupKey( saleLine );
                if (!existingKeys.Add( dedupKey ))
                {
                    continue;
                }

                lines.Add( saleLine );
            }
        }
    }

    private async Task<List<(string ProductId, string ProductTitle)>> BuildReportProductTitleIndexAsync(
        List<VatReportRowItem> orderSaleLines )
    {
        HashSet<(string ProductId, string ProductTitle)> titles = new();

        void AddTitle( string rawProductId, string? productTitle )
        {
            string productId = ShopifyIds.NormalizeProductId( rawProductId );
            string title = (productTitle ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( productId ) || string.IsNullOrWhiteSpace( title ))
            {
                return;
            }

            titles.Add( (productId, title) );
        }

        foreach (VatReportRowItem item in orderSaleLines)
        {
            AddTitle( item.ShopifyProductId, item.ProductTitle );
        }

        List<(string ShopifyProductId, string ProductTitle)> cashTitles = await _db.VatReportCashSales
            .AsNoTracking()
            .Where( sale => sale.ShopifyProductId != "" && sale.ProductTitle != "" )
            .Select( sale => new ValueTuple<string, string>( sale.ShopifyProductId, sale.ProductTitle ) )
            .Distinct()
            .ToListAsync();
        foreach ((string shopifyProductId, string productTitle) in cashTitles)
        {
            AddTitle( shopifyProductId, productTitle );
        }

        List<(string ShopifyProductId, string ProductTitle)> expenseTitles = await _db.VatReportExpenseProducts
            .AsNoTracking()
            .Where( product => product.ShopifyProductId != "" && product.ProductTitle != "" )
            .Select( product => new ValueTuple<string, string>( product.ShopifyProductId, product.ProductTitle ) )
            .Distinct()
            .ToListAsync();
        foreach ((string shopifyProductId, string productTitle) in expenseTitles)
        {
            AddTitle( shopifyProductId, productTitle );
        }

        return titles.ToList();
    }

    private async Task<Dictionary<string, string>> BuildKnownProductNameIndexAsync(
        LedgerVariantContext variantContext )
    {
        Dictionary<string, string> index = new( StringComparer.OrdinalIgnoreCase );

        void AddName( string rawProductId, string? productTitle )
        {
            string productId = ShopifyIds.NormalizeProductId( rawProductId );
            string title = (productTitle ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( productId ) || string.IsNullOrWhiteSpace( title ))
            {
                return;
            }

            index[productId] = title;
        }

        foreach (KeyValuePair<string, string> entry in variantContext.ProductTitleById )
        {
            AddName( entry.Key, entry.Value );
        }

        List<(string ShopifyProductId, string ProductTitle)> expenseTitles = await _db.VatReportExpenseProducts
            .AsNoTracking()
            .Where( product => product.ShopifyProductId != "" && product.ProductTitle != "" )
            .Select( product => new ValueTuple<string, string>( product.ShopifyProductId, product.ProductTitle ) )
            .Distinct()
            .ToListAsync();
        foreach ((string shopifyProductId, string productTitle) in expenseTitles)
        {
            AddName( shopifyProductId, productTitle );
        }

        List<(string ShopifyProductId, string ProductTitle)> cashTitles = await _db.VatReportCashSales
            .AsNoTracking()
            .Where( sale => sale.ShopifyProductId != "" && sale.ProductTitle != "" )
            .Select( sale => new ValueTuple<string, string>( sale.ShopifyProductId, sale.ProductTitle ) )
            .Distinct()
            .ToListAsync();
        foreach ((string shopifyProductId, string productTitle) in cashTitles)
        {
            AddName( shopifyProductId, productTitle );
        }

        List<(string ShopifyProductId, string ProductTitle)> rowTitles = await _db.VatReportRowItems
            .AsNoTracking()
            .Where( item => item.ShopifyProductId != "" && item.ProductTitle != "" )
            .Select( item => new ValueTuple<string, string>( item.ShopifyProductId, item.ProductTitle ) )
            .Distinct()
            .ToListAsync();
        foreach ((string shopifyProductId, string productTitle) in rowTitles)
        {
            AddName( shopifyProductId, productTitle );
        }

        return index;
    }

    private static void AppendReportOrderSaleLine(
        List<ProductLedgerSaleLine> lines,
        VatReportRowItem item,
        LedgerVariantContext variantContext )
    {
        ProductLedgerSaleLine saleLine = CreateReportOrderSaleLine( item, item.VatReportRow, variantContext );
        string? canonicalProductId = ResolveCanonicalProductId(
            saleLine.ProductId,
            item.Barcode,
            variantContext.ProductIdByIsbn,
            variantContext.IsbnByProductId );
        if (string.IsNullOrWhiteSpace( canonicalProductId ))
        {
            return;
        }

        if (!string.Equals( saleLine.ProductId, canonicalProductId, StringComparison.OrdinalIgnoreCase ))
        {
            saleLine.ProductId = canonicalProductId;
            string variantTitleHint = ResolveOrderSaleVariantTitleHint( item );
            saleLine.VariantId = ResolveEffectiveVariantId(
                canonicalProductId,
                item.ShopifyVariantId,
                variantTitleHint,
                variantContext.VariantIdByTitle,
                variantContext.DefaultVariantByProduct,
                variantContext.LegacySaleVariantByProduct );
        }

        lines.Add( saleLine );
    }

    private static string? ResolveCanonicalProductId(
        string? rawProductId,
        string? barcodeOrIsbn,
        IReadOnlyDictionary<string, string> productIdByIsbn,
        IReadOnlyDictionary<string, string> catalogIsbnByProductId )
    {
        string normalized = ShopifyIds.NormalizeProductId( rawProductId ?? string.Empty );
        if (!string.IsNullOrWhiteSpace( normalized ))
        {
            return normalized;
        }

        return ResolveProductIdFromIsbn( barcodeOrIsbn, productIdByIsbn, catalogIsbnByProductId );
    }

    private static string? ResolveProductIdFromIsbn(
        string? rawIsbn,
        IReadOnlyDictionary<string, string> productIdByIsbn,
        IReadOnlyDictionary<string, string> catalogIsbnByProductId )
    {
        string isbn = VatReportHelpers.NormalizeIsbn( rawIsbn );
        if (string.IsNullOrWhiteSpace( isbn ))
        {
            isbn = VatReportHelpers.ExtractIsbnFromText( rawIsbn ) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace( isbn ))
        {
            return null;
        }

        if (productIdByIsbn.TryGetValue( isbn, out string? productId ) &&
            !string.IsNullOrWhiteSpace( productId ))
        {
            return ShopifyIds.NormalizeProductId( productId );
        }

        foreach (KeyValuePair<string, string> entry in catalogIsbnByProductId)
        {
            if (VatReportHelpers.IsbnsMatch( isbn, entry.Value ))
            {
                return entry.Key;
            }
        }

        return null;
    }

    private static ProductLedgerSaleLine CreateReportOrderSaleLine(
        VatReportRowItem item,
        VatReportRow reportRow,
        LedgerVariantContext variantContext )
    {
        string productId = ShopifyIds.NormalizeProductId( item.ShopifyProductId );
        string variantTitleHint = ResolveOrderSaleVariantTitleHint( item );
        string variantId = ResolveEffectiveVariantId(
            item.ShopifyProductId,
            item.ShopifyVariantId,
            variantTitleHint,
            variantContext.VariantIdByTitle,
            variantContext.DefaultVariantByProduct,
            variantContext.LegacySaleVariantByProduct );

        return new ProductLedgerSaleLine
        {
            ProductId = productId,
            VariantId = variantId,
            VariantTitle = variantTitleHint,
            ProductTitle = item.ProductTitle ?? string.Empty,
            Barcode = item.Barcode ?? string.Empty,
            Quantity = item.Quantity,
            Source = "order",
            DateUtc = reportRow.OrderDateUtc,
            OrderNumber = reportRow.OrderNumber ?? string.Empty,
            ShopifyOrderId = reportRow.ShopifyOrderId ?? string.Empty,
            ReportId = reportRow.VatReportId
        };
    }

    private static string BuildSaleLineDedupKey( ProductLedgerSaleLine line ) =>
        $"{line.ReportId}::{line.OrderNumber}::{line.ProductId}::{line.VariantId}::{line.Quantity}::{line.DateUtc:O}";

    private async Task<List<ProductLedgerSaleLine>> GetShopifySaleLinesFromCacheAsync( LedgerVariantContext variantContext )
    {
        HashSet<(int Year, int Month)> reportedPeriods = await GetReportedPeriodsAsync();
        List<InventoryProductSale> rows = await _db.InventoryProductSales
            .AsNoTracking()
            .Where( row => row.PeriodYear > 0 && row.PeriodMonth > 0 )
            .ToListAsync();

        List<ProductLedgerSaleLine> lines = new();
        foreach (InventoryProductSale row in rows)
        {
            if (reportedPeriods.Contains( (row.PeriodYear, row.PeriodMonth) )) continue;
            if (string.IsNullOrWhiteSpace( row.ShopifyProductId ) || row.SoldQuantity <= 0) continue;

            string productId = ShopifyIds.NormalizeProductId( row.ShopifyProductId );
            string variantId = ResolveEffectiveVariantId(
                row.ShopifyProductId,
                row.ShopifyVariantId,
                string.Empty,
                variantContext.VariantIdByTitle,
                variantContext.DefaultVariantByProduct,
                variantContext.LegacySaleVariantByProduct );

            lines.Add( new ProductLedgerSaleLine
            {
                ProductId = productId,
                VariantId = variantId,
                Quantity = row.SoldQuantity,
                Source = "shopify",
                DateUtc = new DateTime( row.PeriodYear, row.PeriodMonth, 1, 0, 0, 0, DateTimeKind.Utc )
            } );
        }

        return lines;
    }

    private async Task<List<ProductLedgerSaleLine>> GetShopifySaleEventsForProductAsync(
        string normalizedProductId,
        List<string> productIdCandidates,
        LedgerVariantContext variantContext )
    {
        List<(int Year, int Month)> unreportedPeriods = await GetUnreportedPeriodsAsync();
        if (unreportedPeriods.Count == 0)
        {
            return [];
        }

        List<ProductLedgerSaleLine> lines = new();
        foreach ((int year, int month) in unreportedPeriods)
        {
            List<ShopifyOrderDto> poland = await _shopifyOrders.FetchOrdersForPolandAsync( year, month );
            List<ShopifyOrderDto> foreign = await _shopifyOrders.FetchOrdersForForeignAsync( year, month );
            AppendShopifyOrderLines(
                lines,
                poland,
                normalizedProductId,
                productIdCandidates,
                variantContext );
            AppendShopifyOrderLines(
                lines,
                foreign,
                normalizedProductId,
                productIdCandidates,
                variantContext );
        }

        return lines;
    }

    private static void AppendShopifyOrderLines(
        List<ProductLedgerSaleLine> lines,
        List<ShopifyOrderDto> orders,
        string normalizedProductId,
        List<string> productIdCandidates,
        LedgerVariantContext variantContext )
    {
        foreach (ShopifyOrderDto order in orders)
        {
            string? productIsbn = variantContext.IsbnByProductId.TryGetValue( normalizedProductId, out string? isbn )
                ? isbn
                : null;

            foreach (ShopifyLineItemDto item in order.Items)
            {
                if (item.Quantity <= 0) continue;

                bool matchesProduct = ProductIdMatches( normalizedProductId, item.ShopifyProductId );
                if (!matchesProduct && !string.IsNullOrWhiteSpace( productIsbn ))
                {
                    matchesProduct = VatReportHelpers.IsbnsMatch( item.Barcode, productIsbn );
                }

                if (!matchesProduct)
                {
                    continue;
                }

                string productId = ShopifyIds.NormalizeProductId( item.ShopifyProductId );
                if (string.IsNullOrWhiteSpace( productId ))
                {
                    productId = normalizedProductId;
                }

                string variantId = ResolveEffectiveVariantId(
                    item.ShopifyProductId,
                    item.ShopifyVariantId,
                    item.VariantTitle,
                    variantContext.VariantIdByTitle,
                    variantContext.DefaultVariantByProduct,
                    variantContext.LegacySaleVariantByProduct );

                lines.Add( new ProductLedgerSaleLine
                {
                    ProductId = productId,
                    VariantId = variantId,
                    VariantTitle = item.VariantTitle,
                    ProductTitle = item.Title,
                    Barcode = item.Barcode,
                    Quantity = item.Quantity,
                    Source = "shopify",
                    DateUtc = order.CreatedAtUtc,
                    OrderNumber = order.OrderNumber
                } );
            }
        }
    }

    private async Task<LedgerVariantContext> LoadVariantContextAsync()
    {
        IReadOnlyDictionary<string, string> defaultVariantByProduct =
            await LoadMergedDefaultVariantByProductAsync();
        IReadOnlyDictionary<string, string> variantTitleById;
        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle;
        if (_variantLookup.IsCatalogCacheWarm)
        {
            try
            {
                variantTitleById = await _variantLookup.GetVariantTitleByIdMapCachedAsync();
                variantIdByTitle = await _variantLookup.GetVariantIdByProductTitleMapCachedAsync();
            }
            catch
            {
                variantTitleById = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
                variantIdByTitle = new Dictionary<string, Dictionary<string, string>>( StringComparer.OrdinalIgnoreCase );
            }
        }
        else
        {
            variantTitleById = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
            variantIdByTitle = new Dictionary<string, Dictionary<string, string>>( StringComparer.OrdinalIgnoreCase );
        }

        IReadOnlyDictionary<string, string> legacySaleVariantByProduct =
            await LoadLegacySaleVariantByProductAsync( defaultVariantByProduct, variantIdByTitle );
        IReadOnlyDictionary<string, string> productTitleById;
        IReadOnlyDictionary<string, string> isbnByProductId;
        IReadOnlyDictionary<string, string> productIdByIsbn;
        if (_variantLookup.IsCatalogCacheWarm)
        {
            try
            {
                productTitleById = await _variantLookup.GetProductTitleByIdMapCachedAsync();
                isbnByProductId = await _variantLookup.GetIsbnByProductIdMapCachedAsync();
                productIdByIsbn = await _variantLookup.GetProductIdByIsbnMapCachedAsync();
            }
            catch
            {
                productTitleById = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
                isbnByProductId = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
                productIdByIsbn = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
            }
        }
        else
        {
            productTitleById = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
            isbnByProductId = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
            productIdByIsbn = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
        }

        return new LedgerVariantContext(
            defaultVariantByProduct,
            variantIdByTitle.ToDictionary(
                entry => entry.Key,
                entry => new Dictionary<string, string>( entry.Value, StringComparer.OrdinalIgnoreCase ),
                StringComparer.OrdinalIgnoreCase ),
            new Dictionary<string, string>( variantTitleById, StringComparer.OrdinalIgnoreCase ),
            legacySaleVariantByProduct,
            new Dictionary<string, string>( productTitleById, StringComparer.OrdinalIgnoreCase ),
            new Dictionary<string, string>( isbnByProductId, StringComparer.OrdinalIgnoreCase ),
            new Dictionary<string, string>( productIdByIsbn, StringComparer.OrdinalIgnoreCase ) );
    }

    public async Task<IReadOnlyDictionary<string, string>> GetDefaultVariantByProductAsync() =>
        await LoadMergedDefaultVariantByProductAsync();

    public async Task<IReadOnlyDictionary<string, string>> GetLegacySaleVariantByProductAsync()
    {
        IReadOnlyDictionary<string, string> defaultVariantByProduct =
            await LoadMergedDefaultVariantByProductAsync();
        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle;
        if (_variantLookup.IsCatalogCacheWarm)
        {
            try
            {
                variantIdByTitle = await _variantLookup.GetVariantIdByProductTitleMapCachedAsync();
            }
            catch
            {
                variantIdByTitle = new Dictionary<string, Dictionary<string, string>>( StringComparer.OrdinalIgnoreCase );
            }
        }
        else
        {
            variantIdByTitle = new Dictionary<string, Dictionary<string, string>>( StringComparer.OrdinalIgnoreCase );
        }

        return await LoadLegacySaleVariantByProductAsync( defaultVariantByProduct, variantIdByTitle );
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadMergedDefaultVariantByProductAsync()
    {
        Dictionary<string, string> merged = new( StringComparer.OrdinalIgnoreCase );
        if (_variantLookup.IsCatalogCacheWarm)
        {
            try
            {
                foreach (KeyValuePair<string, string> entry in
                         await _variantLookup.GetDefaultVariantIdByProductCachedAsync())
                {
                    merged[entry.Key] = entry.Value;
                }
            }
            catch
            {
                // Shopify catalog is optional; fall back to ledger lines below.
            }
        }

        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle;
        if (_variantLookup.IsCatalogCacheWarm)
        {
            try
            {
                variantIdByTitle = await _variantLookup.GetVariantIdByProductTitleMapCachedAsync();
            }
            catch
            {
                variantIdByTitle = new Dictionary<string, Dictionary<string, string>>( StringComparer.OrdinalIgnoreCase );
            }
        }
        else
        {
            variantIdByTitle = new Dictionary<string, Dictionary<string, string>>( StringComparer.OrdinalIgnoreCase );
        }

        await ExpenseInvoiceTypeSeeder.EnsureDefaultAsync( _db );

        List<LedgerVariantSeedRow> paymentVariants = await _db.VatReportExpenseProducts
            .AsNoTracking()
            .Where( p =>
                p.VatReportExpense.SupplierId.HasValue &&
                p.VatReportExpense.ExpenseInvoiceType.Name == ExpenseInvoiceTypeSeeder.SupplierPaymentDefaultName &&
                !string.IsNullOrWhiteSpace( p.ShopifyProductId ) &&
                !string.IsNullOrWhiteSpace( p.ShopifyVariantId ) )
            .Select( p => new LedgerVariantSeedRow
            {
                ShopifyProductId = p.ShopifyProductId,
                ShopifyVariantId = p.ShopifyVariantId
            } )
            .ToListAsync();

        List<LedgerVariantSeedRow> supplyVariants = await _db.SupplyProducts
            .AsNoTracking()
            .Where( sp =>
                !string.IsNullOrWhiteSpace( sp.ShopifyProductId ) &&
                !string.IsNullOrWhiteSpace( sp.ShopifyVariantId ) )
            .Select( sp => new LedgerVariantSeedRow
            {
                ShopifyProductId = sp.ShopifyProductId,
                ShopifyVariantId = sp.ShopifyVariantId
            } )
            .ToListAsync();

        foreach (LedgerVariantSeedRow row in paymentVariants.Concat( supplyVariants ))
        {
            string productId = ShopifyIds.NormalizeProductId( row.ShopifyProductId );
            string variantId = ShopifyIds.NormalizeVariantId( row.ShopifyVariantId );
            if (string.IsNullOrWhiteSpace( productId ) || string.IsNullOrWhiteSpace( variantId ))
            {
                continue;
            }

            if (merged.ContainsKey( productId ))
            {
                continue;
            }

            if (VariantLegacyDefaults.GetNamedVariantCount( productId, variantIdByTitle ) > 1 &&
                !VariantLegacyDefaults.IsNamedCatalogVariantForProduct(
                    productId,
                    variantId,
                    variantIdByTitle ))
            {
                continue;
            }

            merged[productId] = variantId;
        }

        return merged;
    }

    /// <summary>
    /// Legacy sales without variant attribution are mapped to the variant used in supplier payments
    /// (when exactly one paid variant exists), even if the product later gained more variants.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> LoadLegacySaleVariantByProductAsync(
        IReadOnlyDictionary<string, string> defaultVariantByProduct,
        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle )
    {
        Dictionary<string, Dictionary<string, int>> paymentQtyByProductVariant = new( StringComparer.OrdinalIgnoreCase );
        Dictionary<string, HashSet<string>> supplyVariantsByProduct = new( StringComparer.OrdinalIgnoreCase );

        void AddResolvedVariant(
            string rawProductId,
            string rawVariantId,
            int quantity,
            bool isPayment )
        {
            string productId = ShopifyIds.NormalizeProductId( rawProductId );
            string variantId = VariantLegacyDefaults.ResolveVariantId(
                productId,
                rawVariantId,
                defaultVariantByProduct,
                variantIdByTitle );
            if (string.IsNullOrWhiteSpace( productId ) || string.IsNullOrWhiteSpace( variantId ))
            {
                return;
            }

            if (isPayment)
            {
                if (!paymentQtyByProductVariant.TryGetValue( productId, out Dictionary<string, int>? byVariant ))
                {
                    byVariant = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
                    paymentQtyByProductVariant[productId] = byVariant;
                }

                byVariant[variantId] = byVariant.GetValueOrDefault( variantId ) + Math.Max( 0, quantity );
                return;
            }

            if (!supplyVariantsByProduct.TryGetValue( productId, out HashSet<string>? variants ))
            {
                variants = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
                supplyVariantsByProduct[productId] = variants;
            }

            variants.Add( variantId );
        }

        await ExpenseInvoiceTypeSeeder.EnsureDefaultAsync( _db );

        List<LedgerVariantSeedRow> paymentLines = await _db.VatReportExpenseProducts
            .AsNoTracking()
            .Where( p =>
                p.VatReportExpense.SupplierId.HasValue &&
                p.VatReportExpense.ExpenseInvoiceType.Name == ExpenseInvoiceTypeSeeder.SupplierPaymentDefaultName &&
                !string.IsNullOrWhiteSpace( p.ShopifyProductId ) )
            .Select( p => new LedgerVariantSeedRow
            {
                ShopifyProductId = p.ShopifyProductId,
                ShopifyVariantId = p.ShopifyVariantId,
                Quantity = p.Quantity
            } )
            .ToListAsync();

        List<LedgerVariantSeedRow> supplyLines = await _db.SupplyProducts
            .AsNoTracking()
            .Where( sp => !string.IsNullOrWhiteSpace( sp.ShopifyProductId ) )
            .Select( sp => new LedgerVariantSeedRow
            {
                ShopifyProductId = sp.ShopifyProductId,
                ShopifyVariantId = sp.ShopifyVariantId,
                Quantity = sp.Quantity
            } )
            .ToListAsync();

        foreach (LedgerVariantSeedRow row in paymentLines)
        {
            AddResolvedVariant( row.ShopifyProductId, row.ShopifyVariantId, row.Quantity, isPayment: true );
        }

        foreach (LedgerVariantSeedRow row in supplyLines)
        {
            AddResolvedVariant( row.ShopifyProductId, row.ShopifyVariantId, row.Quantity, isPayment: false );
        }

        HashSet<string> productIds = new( paymentQtyByProductVariant.Keys, StringComparer.OrdinalIgnoreCase );
        productIds.UnionWith( supplyVariantsByProduct.Keys );

        Dictionary<string, string> result = new( StringComparer.OrdinalIgnoreCase );
        foreach (string productId in productIds)
        {
            HashSet<string> paidVariantIds = paymentQtyByProductVariant.TryGetValue( productId, out Dictionary<string, int>? byVariant )
                ? byVariant
                    .Where( entry => entry.Value > 0 )
                    .Select( entry => entry.Key )
                    .ToHashSet( StringComparer.OrdinalIgnoreCase )
                : new HashSet<string>( StringComparer.OrdinalIgnoreCase );

            if (paidVariantIds.Count == 1)
            {
                result[productId] = paidVariantIds.First();
                continue;
            }

            if (paidVariantIds.Count > 1)
            {
                continue;
            }

            if (supplyVariantsByProduct.TryGetValue( productId, out HashSet<string>? supplyVariants ) &&
                supplyVariants.Count == 1)
            {
                result[productId] = supplyVariants.First();
            }
        }

        return result;
    }

    /// <summary>
    /// Report rows keep original sale qty; Shopify currentQuantity excludes refunds/returns.
    /// When <paramref name="reportedPeriodsToSkip"/> is set, only order lines outside those
    /// report months are adjusted (product history: reported months stay as in VAT reports).
    /// </summary>
    private async Task ApplyShopifyRefundAdjustmentsAsync(
        List<ProductLedgerSaleLine> lines,
        LedgerVariantContext variantContext,
        HashSet<(int Year, int Month)>? reportedPeriodsToSkip = null )
    {
        List<ProductLedgerSaleLine> orderLines = lines
            .Where( line =>
                string.Equals( line.Source, "order", StringComparison.OrdinalIgnoreCase ) &&
                !string.IsNullOrWhiteSpace( line.ShopifyOrderId ) &&
                !line.ReportId.HasValue &&
                (reportedPeriodsToSkip is null ||
                 !reportedPeriodsToSkip.Contains( (line.DateUtc.Year, line.DateUtc.Month) )) )
            .ToList();
        if (orderLines.Count == 0)
        {
            return;
        }

        HashSet<string> orderIds = orderLines
            .Select( line => ShopifyIds.NormalizeOrderId( line.ShopifyOrderId ) )
            .Where( id => !string.IsNullOrWhiteSpace( id ) )
            .ToHashSet( StringComparer.OrdinalIgnoreCase );
        if (orderIds.Count == 0)
        {
            return;
        }

        Dictionary<string, ShopifyOrderDto> ordersById;
        try
        {
            ordersById = await _shopifyOrders.FetchOrdersByIdsAsync( orderIds );
        }
        catch
        {
            return;
        }

        foreach (ProductLedgerSaleLine line in orderLines)
        {
            string orderId = ShopifyIds.NormalizeOrderId( line.ShopifyOrderId );
            if (string.IsNullOrWhiteSpace( orderId ) ||
                !ordersById.TryGetValue( orderId, out ShopifyOrderDto? order ))
            {
                continue;
            }

            bool productOnOrder = order.Items.Any( item =>
                ProductIdMatches( line.ProductId, item.ShopifyProductId ) );
            int netQty = ResolveNetSoldQuantityOnOrder( line, order, variantContext );
            if (netQty > 0)
            {
                line.Quantity = netQty;
            }
            else if (productOnOrder)
            {
                line.Quantity = netQty;
            }
        }

        lines.RemoveAll( line => line.Quantity <= 0 );
    }

    private static int ResolveNetSoldQuantityOnOrder(
        ProductLedgerSaleLine line,
        ShopifyOrderDto order,
        LedgerVariantContext variantContext )
    {
        int matched = 0;
        string lineVariant = ShopifyIds.NormalizeVariantId( line.VariantId );
        string lineTitle = (line.VariantTitle ?? string.Empty).Trim();
        string canonicalLineVariant = VariantLegacyDefaults.ResolveVariantId(
            line.ProductId,
            line.VariantId,
            variantContext.DefaultVariantByProduct,
            variantContext.VariantIdByTitle,
            variantContext.LegacySaleVariantByProduct );
        int namedVariantCount = VariantLegacyDefaults.GetNamedVariantCount(
            line.ProductId,
            variantContext.VariantIdByTitle );

        foreach (ShopifyLineItemDto item in order.Items)
        {
            if (!ProductIdMatches( line.ProductId, item.ShopifyProductId ))
            {
                continue;
            }

            string itemVariant = ShopifyIds.NormalizeVariantId( item.ShopifyVariantId );
            string canonicalItemVariant = VariantLegacyDefaults.ResolveVariantId(
                item.ShopifyProductId,
                item.ShopifyVariantId,
                variantContext.DefaultVariantByProduct,
                variantContext.VariantIdByTitle,
                variantContext.LegacySaleVariantByProduct );

            bool variantMatches;
            if (!string.IsNullOrWhiteSpace( lineVariant ) && !string.IsNullOrWhiteSpace( itemVariant ))
            {
                variantMatches =
                    string.Equals( lineVariant, itemVariant, StringComparison.OrdinalIgnoreCase ) ||
                    ( !string.IsNullOrWhiteSpace( canonicalLineVariant ) &&
                      !string.IsNullOrWhiteSpace( canonicalItemVariant ) &&
                      string.Equals(
                          canonicalLineVariant,
                          canonicalItemVariant,
                          StringComparison.OrdinalIgnoreCase ) );
            }
            else if (!string.IsNullOrWhiteSpace( lineTitle ) && !string.IsNullOrWhiteSpace( item.VariantTitle ))
            {
                variantMatches = VatReportHelpers.VariantTitlesEquivalentForPaymentMatch(
                    lineTitle,
                    item.VariantTitle );
            }
            else
            {
                variantMatches = namedVariantCount <= 1;
            }

            if (!variantMatches)
            {
                continue;
            }

            matched += item.Quantity;
        }

        return matched;
    }

    private static bool SaleLineMatchesNormalizedProductIds(
        HashSet<string> normalizedCandidateIds,
        string rawProductId )
    {
        string productId = ShopifyIds.NormalizeProductId( rawProductId );
        return !string.IsNullOrWhiteSpace( productId ) &&
               normalizedCandidateIds.Contains( productId );
    }

    private static void AddQuantity(
        Dictionary<(string ProductId, string VariantId), int> soldByLine,
        string productId,
        string variantId,
        int quantity )
    {
        if (string.IsNullOrWhiteSpace( productId ) || quantity <= 0) return;
        (string ProductId, string VariantId) key = (productId, variantId);
        soldByLine[key] = soldByLine.GetValueOrDefault( key ) + quantity;
    }

    private static void AddSaleLineToAllocation(
        ProductLedgerSaleLine line,
        LedgerVariantContext variantContext,
        ProductSoldAllocation allocation )
    {
        if (string.IsNullOrWhiteSpace( line.ProductId ) || line.Quantity <= 0)
        {
            return;
        }

        string productId = ShopifyIds.NormalizeProductId( line.ProductId );
        string variantTitle = ResolveLineVariantTitle(
            productId,
            line.VariantId,
            line.VariantTitle,
            variantContext );
        string variantId = ResolveEffectiveVariantId(
            productId,
            line.VariantId,
            variantTitle,
            variantContext.VariantIdByTitle,
            variantContext.DefaultVariantByProduct,
            variantContext.LegacySaleVariantByProduct );

        if (!string.IsNullOrWhiteSpace( variantId ))
        {
            AddQuantity( allocation.SoldByLine, productId, variantId, line.Quantity );
            return;
        }

        if (VariantLegacyDefaults.GetNamedVariantCount( productId, variantContext.VariantIdByTitle ) <= 1)
        {
            allocation.LegacyUnnamedSoldByProduct[productId] =
                allocation.LegacyUnnamedSoldByProduct.GetValueOrDefault( productId ) + line.Quantity;
            return;
        }

        if (VariantLegacyDefaults.IsLegacyUnnamedSaleLine(
                productId,
                line.VariantId,
                variantTitle,
                variantContext.VariantIdByTitle ))
        {
            allocation.LegacyUnnamedSoldByProduct[productId] =
                allocation.LegacyUnnamedSoldByProduct.GetValueOrDefault( productId ) + line.Quantity;
        }
    }

    private static string TryResolveVariantIdByTitle(
        string productId,
        string variantTitle,
        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle ) =>
        ShopifyVariantLookupService.ResolveVariantIdByProductTitle(
            productId,
            variantTitle,
            variantIdByTitle );

    private static string ResolveOrderSaleVariantTitleHint( VatReportRowItem item )
    {
        if (!string.IsNullOrWhiteSpace( item.VariantTitle ))
        {
            return item.VariantTitle.Trim();
        }

        return VatReportHelpers.ExtractVariantTitleFromProductLineTitle( item.ProductTitle );
    }

    private static string ResolveVariantTitle( string variantId, IReadOnlyDictionary<string, string> variantTitles )
    {
        if (string.IsNullOrWhiteSpace( variantId ))
        {
            return string.Empty;
        }

        return variantTitles.TryGetValue( variantId, out string? title ) ? title : string.Empty;
    }

    private static string ResolveLineVariantTitle(
        string productId,
        string variantId,
        string rawTitle,
        LedgerVariantContext variantContext )
    {
        if (!VariantLegacyDefaults.IsDefaultVariantTitle( rawTitle ))
        {
            return rawTitle.Trim();
        }

        string fromId = ResolveVariantTitle( variantId, variantContext.VariantTitleById );
        if (!string.IsNullOrWhiteSpace( fromId ))
        {
            return fromId;
        }

        string normalizedProductId = ShopifyIds.NormalizeProductId( productId );
        string normalizedVariantId = ShopifyIds.NormalizeVariantId( variantId );
        if (string.IsNullOrWhiteSpace( normalizedProductId ) || string.IsNullOrWhiteSpace( normalizedVariantId ))
        {
            return string.Empty;
        }

        if (!variantContext.VariantIdByTitle.TryGetValue(
                normalizedProductId,
                out Dictionary<string, string>? titles ))
        {
            return string.Empty;
        }

        foreach (KeyValuePair<string, string> entry in titles)
        {
            if (string.Equals( entry.Value, normalizedVariantId, StringComparison.OrdinalIgnoreCase ))
            {
                return entry.Key;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Legacy sales without variant id use the same supplier FIFO as inventory so history matches sold/unpaid counts.
    /// </summary>
    private async Task AssignLegacySaleVariantsBySupplierFifoAsync(
        List<ProductLedgerSaleLine> saleLines,
        HashSet<string> normalizedCandidateIds,
        List<string> productIdCandidates,
        int? supplierId,
        LedgerVariantContext variantContext )
    {
        bool hasLegacy = saleLines.Any( line =>
        {
            if (!SaleLineMatchesNormalizedProductIds( normalizedCandidateIds, line.ProductId ) ||
                !string.IsNullOrWhiteSpace( line.VariantId ))
            {
                return false;
            }

            string variantTitle = ResolveLineVariantTitle(
                line.ProductId,
                line.VariantId,
                line.VariantTitle,
                variantContext );
            return VariantLegacyDefaults.IsLegacyUnnamedSaleLine(
                line.ProductId,
                line.VariantId,
                variantTitle,
                variantContext.VariantIdByTitle );
        } );
        if (!hasLegacy)
        {
            return;
        }

        List<SupplyProduct> supplyProducts = await _db.SupplyProducts
            .AsNoTracking()
            .Include( sp => sp.Supply )
            .Where( sp => productIdCandidates.Contains( sp.ShopifyProductId ) )
            .Where( sp => !supplierId.HasValue || sp.Supply.SupplierId == supplierId.Value )
            .OrderBy( sp => sp.Supply.Date )
            .ThenBy( sp => sp.SupplyId )
            .ThenBy( sp => sp.Id )
            .ToListAsync();

        List<MutableSupplyBatch> batches = new();
        foreach (SupplyProduct supplyProduct in supplyProducts)
        {
            string productId = ShopifyIds.NormalizeProductId( supplyProduct.ShopifyProductId );
            string variantId = VariantLegacyDefaults.ResolveVariantId(
                supplyProduct.ShopifyProductId,
                supplyProduct.ShopifyVariantId,
                variantContext.DefaultVariantByProduct,
                variantContext.VariantIdByTitle,
                variantContext.LegacySaleVariantByProduct );
            if (string.IsNullOrWhiteSpace( productId ) ||
                string.IsNullOrWhiteSpace( variantId ) ||
                supplyProduct.Quantity <= 0)
            {
                continue;
            }

            batches.Add( new MutableSupplyBatch
            {
                ProductId = productId,
                VariantId = variantId,
                RemainingCapacity = supplyProduct.Quantity
            } );
        }

        if (batches.Count == 0)
        {
            return;
        }

        List<ProductLedgerSaleLine> productSales = saleLines
            .Where( line => SaleLineMatchesNormalizedProductIds( normalizedCandidateIds, line.ProductId ) )
            .OrderBy( line => line.DateUtc )
            .ThenBy( line => line.OrderNumber, StringComparer.OrdinalIgnoreCase )
            .ToList();

        foreach (ProductLedgerSaleLine sale in productSales)
        {
            int remaining = sale.Quantity;
            if (remaining <= 0)
            {
                continue;
            }

            string variantTitle = ResolveLineVariantTitle(
                sale.ProductId,
                sale.VariantId,
                sale.VariantTitle,
                variantContext );
            if (string.IsNullOrWhiteSpace( sale.VariantId ) && !string.IsNullOrWhiteSpace( variantTitle ))
            {
                sale.VariantId = TryResolveVariantIdByTitle(
                    sale.ProductId,
                    variantTitle,
                    variantContext.VariantIdByTitle );
            }

            bool legacy = string.IsNullOrWhiteSpace( sale.VariantId ) &&
                VariantLegacyDefaults.IsLegacyUnnamedSaleLine(
                    sale.ProductId,
                    sale.VariantId,
                    variantTitle,
                    variantContext.VariantIdByTitle );
            if (!legacy && string.IsNullOrWhiteSpace( sale.VariantId ))
            {
                continue;
            }

            IEnumerable<MutableSupplyBatch> candidates = legacy
                ? batches.Where( batch =>
                    string.Equals( batch.ProductId, sale.ProductId, StringComparison.OrdinalIgnoreCase ) )
                : batches.Where( batch =>
                    string.Equals( batch.ProductId, sale.ProductId, StringComparison.OrdinalIgnoreCase ) &&
                    string.Equals( batch.VariantId, sale.VariantId, StringComparison.OrdinalIgnoreCase ) );

            foreach (MutableSupplyBatch batch in candidates)
            {
                if (remaining <= 0)
                {
                    break;
                }

                if (batch.RemainingCapacity <= 0)
                {
                    continue;
                }

                int take = Math.Min( remaining, batch.RemainingCapacity );
                if (legacy && string.IsNullOrWhiteSpace( sale.VariantId ))
                {
                    sale.VariantId = batch.VariantId;
                    sale.VariantTitle = ResolveLineVariantTitle(
                        sale.ProductId,
                        sale.VariantId,
                        sale.VariantTitle,
                        variantContext );
                }

                batch.RemainingCapacity -= take;
                remaining -= take;
            }
        }
    }

    private readonly record struct LedgerVariantContext(
        IReadOnlyDictionary<string, string> DefaultVariantByProduct,
        IReadOnlyDictionary<string, Dictionary<string, string>> VariantIdByTitle,
        IReadOnlyDictionary<string, string> VariantTitleById,
        IReadOnlyDictionary<string, string> LegacySaleVariantByProduct,
        IReadOnlyDictionary<string, string> ProductTitleById,
        IReadOnlyDictionary<string, string> IsbnByProductId,
        IReadOnlyDictionary<string, string> ProductIdByIsbn );

    private sealed class ProductLedgerSaleLine
    {
        public string ProductId { get; set; } = string.Empty;
        public string VariantId { get; set; } = string.Empty;
        public string VariantTitle { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
        public string Barcode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public string Source { get; set; } = string.Empty;
        public DateTime DateUtc { get; set; }
        public string OrderNumber { get; set; } = string.Empty;
        public string ShopifyOrderId { get; set; } = string.Empty;
        public int? ReportId { get; set; }
    }

    private sealed class LedgerVariantSeedRow
    {
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ShopifyVariantId { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }

    private sealed class MutableSupplyBatch
    {
        public string ProductId { get; set; } = string.Empty;
        public string VariantId { get; set; } = string.Empty;
        public int RemainingCapacity { get; set; }
    }
}
