using backend.Data;
using backend.Models;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public class VatReportProfitService
{
    private readonly AppDbContext _db;
    private readonly ShopifyVariantLookupService _variantLookup;
    private readonly ShopifyOrderFetchService _shopifyOrders;
    private Dictionary<(int Year, int Month), decimal>? _cogsByPeriodCache;
    private Dictionary<(int Year, int Month), decimal>? _nonSupplierExpenseByPeriodCache;
    private Dictionary<(int Year, int Month), decimal>? _financePaymentsByPeriodCache;
    private SaleCostAllocationResult? _allocationCache;
    private Dictionary<string, int>? _totalSoldByLineKeyCache;
    private List<SaleUnit>? _saleUnitsWithSuppliersCache;

    public VatReportProfitService(
        AppDbContext db,
        ShopifyVariantLookupService variantLookup,
        ShopifyOrderFetchService shopifyOrders )
    {
        _db = db;
        _variantLookup = variantLookup;
        _shopifyOrders = shopifyOrders;
    }

    public async Task<decimal> ComputePeriodProfitAsync(
        int periodYear,
        int periodMonth,
        IEnumerable<VatReportDetailsSummaryRow> summaryRows )
    {
        Dictionary<(int Year, int Month), decimal> cogsByPeriod = await GetCogsByPeriodCachedAsync();
        Dictionary<(int Year, int Month), decimal> nonSupplierByPeriod =
            await GetNonSupplierExpenseGrossByPeriodCachedAsync();
        Dictionary<(int Year, int Month), decimal> financeByPeriod =
            await GetFinancePaymentsByPeriodCachedAsync();

        return ComputePeriodProfit(
            periodYear,
            periodMonth,
            summaryRows,
            cogsByPeriod,
            nonSupplierByPeriod,
            financeByPeriod );
    }

    public decimal ComputePeriodProfit(
        int periodYear,
        int periodMonth,
        IEnumerable<VatReportDetailsSummaryRow> summaryRows,
        IReadOnlyDictionary<(int Year, int Month), decimal> cogsByPeriod,
        IReadOnlyDictionary<(int Year, int Month), decimal> nonSupplierByPeriod,
        IReadOnlyDictionary<(int Year, int Month), decimal> financeByPeriod )
    {
        decimal revenue = summaryRows
            .Where( r =>
                r.Type == VatReportType.Poland ||
                r.Type == VatReportType.Foreign ||
                r.Type == VatReportType.Cash )
            .Sum( r => r.GrossAmount );

        (int Year, int Month) key = (periodYear, periodMonth);
        decimal nonSupplierExpenseGross = nonSupplierByPeriod.GetValueOrDefault( key );
        decimal cogs = cogsByPeriod.GetValueOrDefault( key );
        decimal financePayments = financeByPeriod.GetValueOrDefault( key );

        return VatReportHelpers.Round2( revenue - nonSupplierExpenseGross - cogs - financePayments );
    }

    public async Task<Dictionary<(int Year, int Month), decimal>> GetCogsByPeriodCachedAsync()
    {
        if (_cogsByPeriodCache is not null)
        {
            return _cogsByPeriodCache;
        }

        _cogsByPeriodCache = await BuildCogsByPeriodAsync();
        return _cogsByPeriodCache;
    }

    public async Task<Dictionary<(int Year, int Month), decimal>> GetNonSupplierExpenseGrossByPeriodCachedAsync()
    {
        if (_nonSupplierExpenseByPeriodCache is not null)
        {
            return _nonSupplierExpenseByPeriodCache;
        }

        _nonSupplierExpenseByPeriodCache = await BuildNonSupplierExpenseGrossByPeriodAsync();
        return _nonSupplierExpenseByPeriodCache;
    }

    public async Task<Dictionary<(int Year, int Month), decimal>> GetFinancePaymentsByPeriodCachedAsync()
    {
        if (_financePaymentsByPeriodCache is not null)
        {
            return _financePaymentsByPeriodCache;
        }

        _financePaymentsByPeriodCache = await BuildFinancePaymentsByPeriodAsync();
        return _financePaymentsByPeriodCache;
    }

    private static bool IsProfitFinanceOutflowKind( FinanceMovement movement ) =>
        movement.Kind switch
        {
            FinanceMovementKind.Payment => true,
            FinanceMovementKind.OutgoingTransfer => true,
            _ => false
        };

    private async Task<decimal> SumNonSupplierExpenseGrossAsync( int periodYear, int periodMonth )
    {
        Dictionary<(int Year, int Month), decimal> byPeriod =
            await GetNonSupplierExpenseGrossByPeriodCachedAsync();
        return byPeriod.GetValueOrDefault( (periodYear, periodMonth) );
    }

    private async Task<Dictionary<(int Year, int Month), decimal>> BuildNonSupplierExpenseGrossByPeriodAsync()
    {
        List<ExpensePeriodGrossRow> expenses = await _db.VatReportExpenses
            .AsNoTracking()
            .Where( e => e.VatReport.Type == VatReportType.Poland )
            .Select( e => new ExpensePeriodGrossRow
            {
                PeriodYear = e.VatReport.PeriodYear,
                PeriodMonth = e.VatReport.PeriodMonth,
                GrossAmount = e.GrossAmount,
                TypeName = e.ExpenseInvoiceType.Name
            } )
            .ToListAsync();

        Dictionary<(int Year, int Month), decimal> result = new();
        foreach (ExpensePeriodGrossRow expense in expenses.Where( e => !IsSupplierPaymentType( e.TypeName ) ))
        {
            (int Year, int Month) key = (expense.PeriodYear, expense.PeriodMonth);
            result[key] = result.GetValueOrDefault( key ) + expense.GrossAmount;
        }

        foreach ((int Year, int Month) key in result.Keys.ToList())
        {
            result[key] = VatReportHelpers.Round2( result[key] );
        }

        return result;
    }

    private async Task<Dictionary<(int Year, int Month), decimal>> BuildFinancePaymentsByPeriodAsync()
    {
        List<FinanceMovement> movements = await _db.FinanceMovements.AsNoTracking().ToListAsync();
        Dictionary<(int Year, int Month), decimal> result = new();
        foreach (FinanceMovement movement in movements.Where( IsProfitFinanceOutflowKind ))
        {
            (int Year, int Month) key = (movement.MovementDate.Year, movement.MovementDate.Month);
            result[key] = result.GetValueOrDefault( key ) + movement.Amount;
        }

        foreach ((int Year, int Month) key in result.Keys.ToList())
        {
            result[key] = VatReportHelpers.Round2( result[key] );
        }

        return result;
    }

    public async Task<decimal> ComputePeriodCogsAsync( int periodYear, int periodMonth )
    {
        Dictionary<(int Year, int Month), decimal> cogsByPeriod = await GetCogsByPeriodCachedAsync();
        return cogsByPeriod.GetValueOrDefault( (periodYear, periodMonth) );
    }

    public async Task<Dictionary<string, int>> GetSoldQuantityBySupplierLineKeyAsync( int supplierId )
    {
        List<SaleUnit> sales = await GetSaleUnitsWithSuppliersCachedAsync();
        return await BuildSoldQuantityByLineKeyAsync(
            sales.Where( sale => sale.SupplierId == supplierId && sale.Quantity > 0 ) );
    }

    public async Task<Dictionary<string, int>> GetTotalSoldQuantityByLineKeyAsync()
    {
        if (_totalSoldByLineKeyCache is not null)
        {
            return _totalSoldByLineKeyCache;
        }

        _totalSoldByLineKeyCache = await BuildSoldQuantityByLineKeyAsync(
            await LoadSaleUnitsAsync( deduplicate: true ) );
        return _totalSoldByLineKeyCache;
    }

    private async Task<Dictionary<string, int>> BuildSoldQuantityByLineKeyAsync(
        IEnumerable<SaleUnit> sales )
    {
        QuantityLineKeyMaps lineKeyMaps = await GetQuantityLineKeyMapsAsync();
        Dictionary<string, int> sold = new( StringComparer.OrdinalIgnoreCase );
        foreach (SaleUnit sale in sales)
        {
            if (sale.Quantity <= 0)
            {
                continue;
            }

            string lineKey = ResolveQuantityLineKey(
                sale.ProductId,
                sale.VariantId,
                sale.VariantTitle,
                sale.ProductTitle,
                lineKeyMaps );
            if (string.IsNullOrWhiteSpace( lineKey ))
            {
                continue;
            }

            sold[lineKey] = sold.GetValueOrDefault( lineKey ) + sale.Quantity;
        }

        return sold;
    }

    public async Task<QuantityLineKeyMaps> GetQuantityLineKeyMapsAsync()
    {
        IReadOnlyDictionary<string, string> variantToProduct = await LoadVariantToProductMapAsync();
        return new QuantityLineKeyMaps
        {
            VariantToProduct = variantToProduct,
            VariantIdByTitle = await _variantLookup.GetVariantIdByProductTitleMapCachedAsync(),
            MultiVariantProductIds = await LoadMultiVariantProductIdsAsync( variantToProduct )
        };
    }

    public static string ResolveQuantityLineKey(
        string productIdRaw,
        string variantIdRaw,
        string? variantTitleRaw,
        string? productTitleRaw,
        QuantityLineKeyMaps maps )
    {
        string variantId = NormalizeVariantId( variantIdRaw );
        string catalogProductId = ResolveCatalogProductIdForLineKey(
            NormalizeProductId( productIdRaw ),
            variantId,
            maps.VariantToProduct );

        if (string.IsNullOrWhiteSpace( variantId ))
        {
            string variantTitle = NormalizeVariantTitle( variantTitleRaw ?? string.Empty );
            if (string.IsNullOrWhiteSpace( variantTitle ))
            {
                variantTitle = NormalizeVariantTitle(
                    VatReportHelpers.ExtractVariantTitleFromProductLineTitle( productTitleRaw ?? string.Empty ) );
            }

            if (!string.IsNullOrWhiteSpace( variantTitle ) && !string.IsNullOrWhiteSpace( catalogProductId ))
            {
                string resolved = ShopifyVariantLookupService.ResolveVariantIdByProductTitle(
                    catalogProductId,
                    variantTitle,
                    maps.VariantIdByTitle );
                if (!string.IsNullOrWhiteSpace( resolved ))
                {
                    variantId = NormalizeVariantId( resolved );
                }
            }
        }

        string normalizedProductId = NormalizeProductId( catalogProductId );
        if (!string.IsNullOrWhiteSpace( normalizedProductId ) &&
            !maps.MultiVariantProductIds.Contains( normalizedProductId ))
        {
            variantId = string.Empty;
        }

        return VatReportHelpers.BuildProductLineKey( catalogProductId, variantId );
    }

    private async Task<List<SaleUnit>> GetSaleUnitsWithSuppliersCachedAsync()
    {
        if (_saleUnitsWithSuppliersCache is not null)
        {
            return _saleUnitsWithSuppliersCache;
        }

        _saleUnitsWithSuppliersCache = await LoadSaleUnitsAsync();
        return _saleUnitsWithSuppliersCache;
    }

    private void InvalidateDerivedCaches()
    {
        _allocationCache = null;
        _cogsByPeriodCache = null;
        _totalSoldByLineKeyCache = null;
        _saleUnitsWithSuppliersCache = null;
    }

    private static string ResolveCatalogProductIdForLineKey(
        string productRaw,
        string variantRaw,
        IReadOnlyDictionary<string, string> variantToProduct )
    {
        string variantMapped = ResolveVariantMappedProductId( variantRaw, variantToProduct );
        if (!string.IsNullOrWhiteSpace( variantMapped ))
        {
            return variantMapped;
        }

        string variantAsProduct = ResolveVariantMappedProductId( productRaw, variantToProduct );
        if (!string.IsNullOrWhiteSpace( variantAsProduct ))
        {
            return variantAsProduct;
        }

        return NormalizeProductId( productRaw );
    }

    public async Task<List<VatReportUnpaidProductRow>> GetUnpaidProductsForPeriodAsync(
        int periodYear,
        int periodMonth )
    {
        SaleCostAllocationResult allocation = await GetSaleCostAllocationCachedAsync();
        allocation.UnpaidByPeriod.TryGetValue( (periodYear, periodMonth), out List<UnpaidAccumulator>? lines );
        lines ??= [];

        List<VatReportUnpaidAllocation> manualLinks = await _db.VatReportUnpaidAllocations
            .AsNoTracking()
            .Where( allocationRow =>
                allocationRow.SalePeriodYear == periodYear &&
                allocationRow.SalePeriodMonth == periodMonth )
            .ToListAsync();

        Dictionary<int, ExpenseLabelRow> expenseLabels = await LoadExpenseLabelsAsync(
            manualLinks.Select( link => link.VatReportExpenseId ) );

        HashSet<int> supplierIds = lines
            .Where( line => line.SupplierId.HasValue )
            .Select( line => line.SupplierId!.Value )
            .Concat( manualLinks.Select( link => link.SupplierId ) )
            .ToHashSet();
        Dictionary<int, string> supplierNames = await LoadSupplierNamesAsync( supplierIds );

        List<VatReportUnpaidProductRow> result = lines
            .Select( line => MapUnpaidAccumulatorToRow( line, supplierNames ) )
            .ToList();

        await EnrichUnpaidVariantTitlesAsync( result, periodYear, periodMonth );
        ApplyManualLinkDeductions( result, manualLinks, expenseLabels );

        return result
            .Where( row => row.Quantity > 0 )
            .OrderBy( row => row.ProductTitle, StringComparer.OrdinalIgnoreCase )
            .ThenBy( row => row.ShopifyProductId, StringComparer.OrdinalIgnoreCase )
            .ToList();
    }

    private static void ApplyManualLinkDeductions(
        List<VatReportUnpaidProductRow> rows,
        IReadOnlyList<VatReportUnpaidAllocation> manualLinks,
        IReadOnlyDictionary<int, ExpenseLabelRow> expenseLabels )
    {
        foreach (VatReportUnpaidAllocation link in manualLinks)
        {
            ExpenseLabelRow? expense = expenseLabels.GetValueOrDefault( link.VatReportExpenseId );
            string paymentLabel = expense is null ? $"#{link.VatReportExpenseId}" : FormatExpenseLabel( expense );
            int remaining = link.Quantity;

            foreach (VatReportUnpaidProductRow row in rows)
            {
                if (remaining <= 0)
                {
                    break;
                }

                if (row.SupplierId != link.SupplierId)
                {
                    continue;
                }

                if (!VatReportHelpers.ProductLinesCompatible(
                        row.ShopifyProductId,
                        row.ShopifyVariantId,
                        link.ShopifyProductId,
                        link.ShopifyVariantId ))
                {
                    continue;
                }

                int deduct = Math.Min( remaining, row.Quantity );
                if (deduct <= 0)
                {
                    continue;
                }

                row.Quantity -= deduct;
                row.EstimatedCogs = VatReportHelpers.Round2( row.Quantity * row.UnitSupplyPrice );
                row.IsManuallyLinked = true;
                row.LinkedExpenseId = link.VatReportExpenseId;
                row.LinkedPaymentLabel = paymentLabel;
                remaining -= deduct;
            }
        }
    }

    public async Task<Dictionary<string, string>> GetVariantTitleByIdMapAsync()
    {
        Dictionary<string, string> map = new( StringComparer.OrdinalIgnoreCase );

        List<VariantTitleRow> rowItems = await _db.VatReportRowItems
            .AsNoTracking()
            .Where( item =>
                item.ShopifyVariantId != "" &&
                item.VariantTitle != "" )
            .Select( item => new VariantTitleRow
            {
                VariantId = item.ShopifyVariantId,
                Title = item.VariantTitle
            } )
            .ToListAsync();
        foreach (VariantTitleRow row in rowItems)
        {
            AddVariantTitle( map, row.VariantId, row.Title );
        }

        return map;
    }

    public async Task<Dictionary<string, string>> GetVariantTitleLookupAsync()
    {
        Dictionary<string, string> titles = await GetVariantTitleByIdMapAsync();
        try
        {
            IReadOnlyDictionary<string, string> catalogTitles =
                await _variantLookup.GetVariantTitleByIdMapCachedAsync();
            foreach (KeyValuePair<string, string> entry in catalogTitles)
            {
                if (!titles.ContainsKey( entry.Key ))
                {
                    titles[entry.Key] = entry.Value;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Shopify context may be unavailable.
        }

        return titles;
    }

    private async Task EnrichUnpaidVariantTitlesAsync(
        List<VatReportUnpaidProductRow> rows,
        int periodYear,
        int periodMonth )
    {
        if (rows.Count == 0)
        {
            return;
        }

        List<OrderLineVariantRow> lineRows = await LoadOrderLineVariantRowsForUnpaidAsync( rows );
        foreach (VatReportUnpaidProductRow row in rows)
        {
            ApplyVariantFromOrderLines( row, lineRows );
        }

        await EnrichUnpaidVariantsFromShopifyOrderIdsAsync( rows, lineRows );

        Dictionary<string, string> titles = await GetVariantTitleLookupAsync();
        IReadOnlyDictionary<string, Dictionary<string, string>> idByTitle =
            await _variantLookup.GetVariantIdByProductTitleMapCachedAsync();

        foreach (VatReportUnpaidProductRow row in rows)
        {
            if (string.IsNullOrWhiteSpace( row.ShopifyVariantId ) &&
                !string.IsNullOrWhiteSpace( row.ShopifyVariantTitle ))
            {
                string resolvedId = ShopifyVariantLookupService.ResolveVariantIdByProductTitle(
                    row.ShopifyProductId,
                    row.ShopifyVariantTitle,
                    idByTitle );
                if (!string.IsNullOrWhiteSpace( resolvedId ))
                {
                    row.ShopifyVariantId = resolvedId;
                }
            }

            if (!string.IsNullOrWhiteSpace( row.ShopifyVariantTitle ))
            {
                continue;
            }

            string variantId = NormalizeVariantId( row.ShopifyVariantId );
            if (string.IsNullOrWhiteSpace( variantId ))
            {
                continue;
            }

            if (titles.TryGetValue( variantId, out string? title ) && !string.IsNullOrWhiteSpace( title ))
            {
                row.ShopifyVariantTitle = title;
            }
        }

        foreach (VatReportUnpaidProductRow row in rows)
        {
            if (!string.IsNullOrWhiteSpace( row.ShopifyVariantTitle ))
            {
                continue;
            }

            string fromTitle = VatReportHelpers.ExtractVariantTitleFromProductLineTitle( row.ProductTitle );
            if (!string.IsNullOrWhiteSpace( fromTitle ))
            {
                row.ShopifyVariantTitle = NormalizeVariantTitle( fromTitle );
            }
        }
    }

    private async Task<List<OrderLineVariantRow>> LoadOrderLineVariantRowsForUnpaidAsync(
        List<VatReportUnpaidProductRow> rows )
    {
        HashSet<string> productIdCandidates = new( StringComparer.OrdinalIgnoreCase );
        foreach (VatReportUnpaidProductRow row in rows )
        {
            string productId = NormalizeProductId( row.ShopifyProductId );
            if (string.IsNullOrWhiteSpace( productId ))
            {
                continue;
            }

            productIdCandidates.Add( productId );
            productIdCandidates.Add( $"gid://shopify/Product/{productId}" );
        }

        if (productIdCandidates.Count == 0)
        {
            return [];
        }

        return await _db.VatReportRowItems
            .AsNoTracking()
            .Include( i => i.VatReportRow )
            .Where( i => productIdCandidates.Contains( i.ShopifyProductId ) )
            .Select( i => new OrderLineVariantRow
            {
                RowItemId = i.Id,
                OrderId = i.VatReportRow.ShopifyOrderId,
                OrderDateUtc = i.VatReportRow.OrderDateUtc,
                ProductId = i.ShopifyProductId,
                VariantId = i.ShopifyVariantId,
                VariantTitle = i.VariantTitle,
                Quantity = i.Quantity
            } )
            .ToListAsync();
    }

    private static void ApplyVariantFromOrderLines(
        VatReportUnpaidProductRow row,
        IReadOnlyList<OrderLineVariantRow> lineRows )
    {
        string productId = NormalizeProductId( row.ShopifyProductId );
        if (string.IsNullOrWhiteSpace( productId ))
        {
            return;
        }

        if (row.SourceSaleRowItemId > 0)
        {
            OrderLineVariantRow? sourceRow = lineRows.FirstOrDefault( line => line.RowItemId == row.SourceSaleRowItemId );
            if (sourceRow is not null)
            {
                ApplyVariantMatchToRow( row, sourceRow );
                if (!string.IsNullOrWhiteSpace( row.ShopifyVariantTitle ))
                {
                    return;
                }
            }
        }

        List<OrderLineVariantRow> productLines = lineRows
            .Where( line =>
                ProductIdsEqual( line.ProductId, productId ) &&
                (
                    !string.IsNullOrWhiteSpace( line.VariantTitle ) ||
                    !string.IsNullOrWhiteSpace( line.VariantId ) ) )
            .ToList();
        if (productLines.Count == 0)
        {
            return;
        }

        string orderId = NormalizeOrderId( row.ShopifyOrderId );
        if (!string.IsNullOrWhiteSpace( orderId ))
        {
            List<OrderLineVariantRow> byOrder = lineRows
                .Where( line =>
                    NormalizeOrderId( line.OrderId ) == orderId &&
                    ProductIdsEqual( line.ProductId, productId ) )
                .OrderBy( line => line.RowItemId )
                .ToList();
            if (row.SourceSaleRowItemId > 0 &&
                TryApplyPositionalVariantFromOrderLines( row, byOrder, row.SourceSaleRowItemId.Value ) &&
                !string.IsNullOrWhiteSpace( row.ShopifyVariantTitle ))
            {
                return;
            }

            List<OrderLineVariantRow> byOrderWithVariant = byOrder
                .Where( line =>
                    !string.IsNullOrWhiteSpace( line.VariantTitle ) ||
                    !string.IsNullOrWhiteSpace( line.VariantId ) )
                .ToList();
            OrderLineVariantRow? orderMatch = PickBestVariantLineMatch(
                byOrderWithVariant,
                row.Quantity,
                line => line.Quantity,
                line => NormalizeVariantId( line.VariantId ),
                line => NormalizeVariantTitle( line.VariantTitle ) );
            if (orderMatch is not null)
            {
                ApplyVariantMatchToRow( row, orderMatch );
                if (!string.IsNullOrWhiteSpace( row.ShopifyVariantTitle ))
                {
                    return;
                }
            }
        }

        if (!row.SaleOrderDateUtc.HasValue)
        {
            return;
        }

        DateOnly saleDate = DateOnly.FromDateTime( row.SaleOrderDateUtc.Value );
        List<OrderLineVariantRow> onDate = productLines
            .Where( line => DateOnly.FromDateTime( line.OrderDateUtc ) == saleDate )
            .ToList();
        if (onDate.Count == 0)
        {
            return;
        }

        string rowVariantId = NormalizeVariantId( row.ShopifyVariantId );
        if (!string.IsNullOrWhiteSpace( rowVariantId ))
        {
            OrderLineVariantRow? byVariant = onDate.FirstOrDefault( line =>
                NormalizeVariantId( line.VariantId ) == rowVariantId );
            if (byVariant is not null)
            {
                ApplyVariantMatchToRow( row, byVariant );
                return;
            }
        }

        List<OrderLineVariantRow> distinctVariants = onDate
            .GroupBy( line => BuildVariantLineKey( line ) )
            .Select( group => group.First() )
            .ToList();
        if (distinctVariants.Count == 1)
        {
            ApplyVariantMatchToRow( row, distinctVariants[0] );
            return;
        }

        List<OrderLineVariantRow> sameQty = onDate
            .Where( line => line.Quantity == row.Quantity )
            .GroupBy( line => BuildVariantLineKey( line ) )
            .Select( group => group.First() )
            .ToList();
        if (sameQty.Count == 1)
        {
            ApplyVariantMatchToRow( row, sameQty[0] );
        }
    }

    private static string BuildVariantLineKey( OrderLineVariantRow line )
    {
        string variantId = NormalizeVariantId( line.VariantId );
        if (!string.IsNullOrWhiteSpace( variantId ))
        {
            return variantId;
        }

        return NormalizeVariantTitle( line.VariantTitle ).ToLowerInvariant();
    }

    private static void ApplyVariantMatchToRow( VatReportUnpaidProductRow row, OrderLineVariantRow match )
    {
        if (string.IsNullOrWhiteSpace( row.ShopifyVariantId ) && !string.IsNullOrWhiteSpace( match.VariantId ))
        {
            row.ShopifyVariantId = NormalizeVariantId( match.VariantId );
        }

        if (string.IsNullOrWhiteSpace( row.ShopifyVariantTitle ) && !string.IsNullOrWhiteSpace( match.VariantTitle ))
        {
            row.ShopifyVariantTitle = NormalizeVariantTitle( match.VariantTitle );
        }
    }

    private static void ApplyVariantMatchToSale( SaleUnit sale, OrderLineVariantRow match )
    {
        if (string.IsNullOrWhiteSpace( sale.VariantId ) && !string.IsNullOrWhiteSpace( match.VariantId ))
        {
            sale.VariantId = NormalizeVariantId( match.VariantId );
        }

        if (string.IsNullOrWhiteSpace( sale.VariantTitle ) && !string.IsNullOrWhiteSpace( match.VariantTitle ))
        {
            sale.VariantTitle = NormalizeVariantTitle( match.VariantTitle );
        }
    }

    private static void MatchSaleUnitsToOrderLines(
        List<SaleUnit> sales,
        List<OrderLineVariantRow> lines )
    {
        List<SaleUnit> needsVariant = sales
            .Where( sale =>
                string.IsNullOrWhiteSpace( sale.VariantId ) ||
                string.IsNullOrWhiteSpace( sale.VariantTitle ) )
            .OrderBy( sale => sale.Id )
            .ToList();
        if (needsVariant.Count == 0 || lines.Count == 0)
        {
            return;
        }

        foreach (SaleUnit sale in needsVariant)
        {
            OrderLineVariantRow? direct = lines.FirstOrDefault( line => line.RowItemId == sale.Id );
            if (direct is not null)
            {
                ApplyVariantMatchToSale( sale, direct );
            }
        }

        needsVariant = needsVariant
            .Where( sale =>
                string.IsNullOrWhiteSpace( sale.VariantId ) ||
                string.IsNullOrWhiteSpace( sale.VariantTitle ) )
            .ToList();
        if (needsVariant.Count == 0)
        {
            return;
        }

        List<OrderLineVariantRow> linesWithVariant = lines
            .Where( line =>
                !string.IsNullOrWhiteSpace( line.VariantId ) ||
                !string.IsNullOrWhiteSpace( line.VariantTitle ) )
            .ToList();
        if (needsVariant.Count == linesWithVariant.Count)
        {
            for (int index = 0; index < needsVariant.Count; index++)
            {
                ApplyVariantMatchToSale( needsVariant[index], linesWithVariant[index] );
            }

            return;
        }

        foreach (SaleUnit sale in needsVariant)
        {
            OrderLineVariantRow? match = PickBestVariantLineMatch(
                linesWithVariant,
                sale.Quantity,
                line => line.Quantity,
                line => NormalizeVariantId( line.VariantId ),
                line => NormalizeVariantTitle( line.VariantTitle ) );
            if (match is not null)
            {
                ApplyVariantMatchToSale( sale, match );
            }
        }
    }

    private async Task EnrichSaleVariantsFromRowItemsAsync( List<SaleUnit> sales )
    {
        List<SaleUnit> needsLookup = sales
            .Where( sale =>
                string.IsNullOrWhiteSpace( sale.VariantId ) ||
                string.IsNullOrWhiteSpace( sale.VariantTitle ) )
            .ToList();
        if (needsLookup.Count == 0)
        {
            return;
        }

        HashSet<int> saleIds = needsLookup.Select( sale => sale.Id ).ToHashSet();
        List<OrderLineVariantRow> directRows = await _db.VatReportRowItems
            .AsNoTracking()
            .Where( item => saleIds.Contains( item.Id ) )
            .Select( item => new OrderLineVariantRow
            {
                RowItemId = item.Id,
                OrderId = string.Empty,
                OrderDateUtc = default,
                ProductId = item.ShopifyProductId,
                VariantId = item.ShopifyVariantId,
                VariantTitle = item.VariantTitle,
                Quantity = item.Quantity
            } )
            .ToListAsync();

        foreach (SaleUnit sale in needsLookup)
        {
            OrderLineVariantRow? row = directRows.FirstOrDefault( line => line.RowItemId == sale.Id );
            if (row is not null)
            {
                ApplyVariantMatchToSale( sale, row );
            }
        }

        List<SaleUnit> stillNeeds = needsLookup
            .Where( sale =>
                (
                    string.IsNullOrWhiteSpace( sale.VariantId ) ||
                    string.IsNullOrWhiteSpace( sale.VariantTitle ) ) &&
                !string.IsNullOrWhiteSpace( sale.ShopifyOrderId ) )
            .ToList();
        if (stillNeeds.Count == 0)
        {
            return;
        }

        HashSet<string> orderIdCandidates = new( StringComparer.OrdinalIgnoreCase );
        foreach (SaleUnit sale in stillNeeds)
        {
            string orderId = NormalizeOrderId( sale.ShopifyOrderId );
            if (string.IsNullOrWhiteSpace( orderId ))
            {
                continue;
            }

            orderIdCandidates.Add( orderId );
            orderIdCandidates.Add( $"gid://shopify/Order/{orderId}" );
        }

        if (orderIdCandidates.Count == 0)
        {
            return;
        }

        List<OrderLineVariantRow> orderLineRows = await _db.VatReportRowItems
            .AsNoTracking()
            .Include( item => item.VatReportRow )
            .Where( item => orderIdCandidates.Contains( item.VatReportRow.ShopifyOrderId ) )
            .Select( item => new OrderLineVariantRow
            {
                RowItemId = item.Id,
                OrderId = item.VatReportRow.ShopifyOrderId,
                OrderDateUtc = item.VatReportRow.OrderDateUtc,
                ProductId = item.ShopifyProductId,
                VariantId = item.ShopifyVariantId,
                VariantTitle = item.VariantTitle,
                Quantity = item.Quantity
            } )
            .ToListAsync();

        foreach (IGrouping<string, SaleUnit> orderGroup in stillNeeds.GroupBy( sale => sale.ShopifyOrderId ))
        {
            foreach (IGrouping<string, SaleUnit> productGroup in orderGroup.GroupBy( sale => sale.ProductId ))
            {
                List<OrderLineVariantRow> linesForProduct = orderLineRows
                    .Where( line =>
                        NormalizeOrderId( line.OrderId ) == orderGroup.Key &&
                        ProductIdsEqual( line.ProductId, productGroup.Key ) )
                    .OrderBy( line => line.RowItemId )
                    .ToList();
                MatchSaleUnitsToOrderLines( productGroup.ToList(), linesForProduct );
            }
        }
    }

    private async Task EnrichSaleVariantsFromShopifyOrdersAsync( List<SaleUnit> sales )
    {
        List<SaleUnit> needsLookup = sales
            .Where( sale =>
                (string.IsNullOrWhiteSpace( sale.VariantId ) || string.IsNullOrWhiteSpace( sale.VariantTitle )) &&
                !string.IsNullOrWhiteSpace( sale.ShopifyOrderId ) &&
                !IsManualShopifyOrderId( sale.ShopifyOrderId ) )
            .ToList();
        if (needsLookup.Count == 0)
        {
            return;
        }

        HashSet<string> orderIdCandidates = new( StringComparer.OrdinalIgnoreCase );
        foreach (SaleUnit sale in needsLookup)
        {
            string orderId = NormalizeOrderId( sale.ShopifyOrderId );
            if (string.IsNullOrWhiteSpace( orderId ))
            {
                continue;
            }

            orderIdCandidates.Add( orderId );
            orderIdCandidates.Add( $"gid://shopify/Order/{orderId}" );
        }

        if (orderIdCandidates.Count == 0)
        {
            return;
        }

        Dictionary<string, ShopifyOrderDto> ordersById;
        try
        {
            ordersById = await _shopifyOrders.FetchOrdersByIdsAsync( orderIdCandidates );
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (ordersById.Count == 0)
        {
            return;
        }

        List<OrderLineVariantRow> orderLineRows = await _db.VatReportRowItems
            .AsNoTracking()
            .Include( item => item.VatReportRow )
            .Where( item => orderIdCandidates.Contains( item.VatReportRow.ShopifyOrderId ) )
            .Select( item => new OrderLineVariantRow
            {
                RowItemId = item.Id,
                OrderId = item.VatReportRow.ShopifyOrderId,
                OrderDateUtc = item.VatReportRow.OrderDateUtc,
                ProductId = item.ShopifyProductId,
                VariantId = item.ShopifyVariantId,
                VariantTitle = item.VariantTitle,
                Quantity = item.Quantity
            } )
            .ToListAsync();

        List<(int RowItemId, string VariantId, string VariantTitle)> persistUpdates = [];
        foreach (SaleUnit sale in needsLookup)
        {
            ApplyVariantFromShopifyOrderForSale( sale, ordersById, orderLineRows, persistUpdates );
        }

        if (persistUpdates.Count > 0)
        {
            await PersistRowItemVariantBackfillAsync( persistUpdates );
            InvalidateDerivedCaches();
        }
    }

    private async Task PersistRowItemVariantBackfillAsync(
        IReadOnlyList<(int RowItemId, string VariantId, string VariantTitle)> updates )
    {
        Dictionary<int, (string VariantId, string VariantTitle)> byId = new();
        foreach ((int rowItemId, string variantId, string variantTitle) in updates)
        {
            if (rowItemId <= 0 || string.IsNullOrWhiteSpace( variantId ))
            {
                continue;
            }

            byId[rowItemId] = (variantId, variantTitle);
        }

        if (byId.Count == 0)
        {
            return;
        }

        List<VatReportRowItem> rowItems = await _db.VatReportRowItems
            .Where( item => byId.Keys.Contains( item.Id ) )
            .ToListAsync();
        bool changed = false;
        foreach (VatReportRowItem rowItem in rowItems)
        {
            if (!byId.TryGetValue( rowItem.Id, out (string VariantId, string VariantTitle) update ))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace( rowItem.ShopifyVariantId ) && !string.IsNullOrWhiteSpace( update.VariantId ))
            {
                rowItem.ShopifyVariantId = update.VariantId;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace( rowItem.VariantTitle ) && !string.IsNullOrWhiteSpace( update.VariantTitle ))
            {
                rowItem.VariantTitle = update.VariantTitle;
                changed = true;
            }
        }

        if (changed)
        {
            await _db.SaveChangesAsync();
        }
    }

    private static bool IsManualShopifyOrderId( string orderId ) =>
        orderId.StartsWith( "manual-", StringComparison.OrdinalIgnoreCase );

    private static void ApplyVariantFromShopifyOrderForSale(
        SaleUnit sale,
        IReadOnlyDictionary<string, ShopifyOrderDto> ordersById,
        IReadOnlyList<OrderLineVariantRow> lineRows,
        List<(int RowItemId, string VariantId, string VariantTitle)> persistUpdates )
    {
        string productId = NormalizeProductId( sale.ProductId );
        if (string.IsNullOrWhiteSpace( productId ))
        {
            return;
        }

        string orderId = NormalizeOrderId( sale.ShopifyOrderId );
        if (string.IsNullOrWhiteSpace( orderId ) ||
            !ordersById.TryGetValue( orderId, out ShopifyOrderDto? order ))
        {
            return;
        }

        List<ShopifyLineItemDto> productLines = GetOrderedShopifyProductLines( order, productId );
        if (sale.Id > 0 && sale.Id < 1_000_000 &&
            TryApplyPositionalVariantFromShopifyOrderForSale(
                sale,
                lineRows,
                orderId,
                productId,
                productLines,
                sale.Id ))
        {
            QueueSaleVariantPersist( persistUpdates, sale );
            return;
        }

        ShopifyLineItemDto? orderMatch = PickBestVariantLineMatch(
            productLines,
            sale.Quantity,
            line => line.Quantity,
            line => NormalizeVariantId( line.ShopifyVariantId ),
            line => NormalizeVariantTitle( line.VariantTitle ) );
        if (orderMatch is not null)
        {
            ApplyShopifyLineVariantToSale( sale, orderMatch );
            QueueSaleVariantPersist( persistUpdates, sale );
        }
    }

    private static bool TryApplyPositionalVariantFromShopifyOrderForSale(
        SaleUnit sale,
        IReadOnlyList<OrderLineVariantRow> lineRows,
        string orderId,
        string productId,
        IReadOnlyList<ShopifyLineItemDto> shopifyLines,
        int sourceSaleRowItemId )
    {
        if (sourceSaleRowItemId <= 0 || shopifyLines.Count == 0)
        {
            return false;
        }

        List<int> rowItemIds = lineRows
            .Where( line =>
                NormalizeOrderId( line.OrderId ) == orderId &&
                ProductIdsEqual( line.ProductId, productId ) )
            .OrderBy( line => line.RowItemId )
            .Select( line => line.RowItemId )
            .ToList();
        if (rowItemIds.Count != shopifyLines.Count)
        {
            return false;
        }

        int index = rowItemIds.IndexOf( sourceSaleRowItemId );
        if (index < 0)
        {
            return false;
        }

        ApplyShopifyLineVariantToSale( sale, shopifyLines[index] );
        return !string.IsNullOrWhiteSpace( sale.VariantId ) || !string.IsNullOrWhiteSpace( sale.VariantTitle );
    }

    private static void ApplyShopifyLineVariantToSale( SaleUnit sale, ShopifyLineItemDto line )
    {
        if (string.IsNullOrWhiteSpace( sale.VariantId ) && !string.IsNullOrWhiteSpace( line.ShopifyVariantId ))
        {
            sale.VariantId = NormalizeVariantId( line.ShopifyVariantId );
        }

        if (string.IsNullOrWhiteSpace( sale.VariantTitle ) && !string.IsNullOrWhiteSpace( line.VariantTitle ))
        {
            sale.VariantTitle = NormalizeVariantTitle( line.VariantTitle );
        }
    }

    private static void QueueSaleVariantPersist(
        List<(int RowItemId, string VariantId, string VariantTitle)> persistUpdates,
        SaleUnit sale )
    {
        if (sale.Id <= 0 || sale.Id >= 1_000_000 || string.IsNullOrWhiteSpace( sale.VariantId ))
        {
            return;
        }

        persistUpdates.Add( (sale.Id, sale.VariantId, sale.VariantTitle) );
    }

    private static bool TryApplyPositionalVariantFromOrderLines(
        VatReportUnpaidProductRow row,
        IReadOnlyList<OrderLineVariantRow> orderLines,
        int sourceSaleRowItemId )
    {
        if (sourceSaleRowItemId <= 0 || orderLines.Count == 0)
        {
            return false;
        }

        OrderLineVariantRow? match = orderLines.FirstOrDefault( line => line.RowItemId == sourceSaleRowItemId );
        if (match is null)
        {
            return false;
        }

        ApplyVariantMatchToRow( row, match );
        return true;
    }

    private async Task EnrichUnpaidVariantsFromShopifyOrderIdsAsync(
        List<VatReportUnpaidProductRow> rows,
        IReadOnlyList<OrderLineVariantRow> lineRows )
    {
        List<VatReportUnpaidProductRow> needsLookup = rows
            .Where( row =>
                string.IsNullOrWhiteSpace( row.ShopifyVariantTitle ) ||
                string.IsNullOrWhiteSpace( row.ShopifyVariantId ) )
            .ToList();
        if (needsLookup.Count == 0)
        {
            return;
        }

        foreach (VatReportUnpaidProductRow row in needsLookup)
        {
            if (!string.IsNullOrWhiteSpace( row.ShopifyOrderId ))
            {
                continue;
            }

            if (row.SourceSaleRowItemId > 0)
            {
                OrderLineVariantRow? sourceRow = lineRows.FirstOrDefault( line => line.RowItemId == row.SourceSaleRowItemId );
                if (sourceRow is not null && !string.IsNullOrWhiteSpace( sourceRow.OrderId ))
                {
                    row.ShopifyOrderId = NormalizeOrderId( sourceRow.OrderId );
                }
            }
        }

        HashSet<string> orderIds = new( StringComparer.OrdinalIgnoreCase );
        foreach (VatReportUnpaidProductRow row in needsLookup)
        {
            string orderId = NormalizeOrderId( row.ShopifyOrderId );
            if (!string.IsNullOrWhiteSpace( orderId ))
            {
                orderIds.Add( orderId );
            }
        }

        if (orderIds.Count == 0)
        {
            return;
        }

        Dictionary<string, ShopifyOrderDto> ordersById;
        try
        {
            ordersById = await _shopifyOrders.FetchOrdersByIdsAsync( orderIds );
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (ordersById.Count == 0)
        {
            return;
        }

        foreach (VatReportUnpaidProductRow row in needsLookup)
        {
            ApplyVariantFromShopifyOrder( row, ordersById, lineRows );
        }
    }

    private static void ApplyVariantFromShopifyOrder(
        VatReportUnpaidProductRow row,
        IReadOnlyDictionary<string, ShopifyOrderDto> ordersById,
        IReadOnlyList<OrderLineVariantRow> lineRows )
    {
        string productId = NormalizeProductId( row.ShopifyProductId );
        if (string.IsNullOrWhiteSpace( productId ))
        {
            return;
        }

        string orderId = NormalizeOrderId( row.ShopifyOrderId );
        if (string.IsNullOrWhiteSpace( orderId ) ||
            !ordersById.TryGetValue( orderId, out ShopifyOrderDto? order ))
        {
            return;
        }

        List<ShopifyLineItemDto> productLines = GetOrderedShopifyProductLines( order, productId );
        if (row.SourceSaleRowItemId > 0 &&
            TryApplyPositionalVariantFromShopifyOrder(
                row,
                lineRows,
                orderId,
                productId,
                productLines,
                row.SourceSaleRowItemId.Value ))
        {
            return;
        }

        ShopifyLineItemDto? orderMatch = PickBestVariantLineMatch(
            productLines,
            row.Quantity,
            line => line.Quantity,
            line => NormalizeVariantId( line.ShopifyVariantId ),
            line => NormalizeVariantTitle( line.VariantTitle ) );
        if (orderMatch is not null)
        {
            ApplyShopifyLineVariantToUnpaidRow( row, orderMatch );
        }
    }

    private static List<ShopifyLineItemDto> GetOrderedShopifyProductLines(
        ShopifyOrderDto order,
        string productId )
    {
        return order.Items
            .Select( ( line, index ) => (line, index) )
            .Where( entry =>
                ProductIdsEqual( entry.line.ShopifyProductId, productId ) &&
                (
                    !string.IsNullOrWhiteSpace( entry.line.VariantTitle ) ||
                    !string.IsNullOrWhiteSpace( entry.line.ShopifyVariantId ) ) )
            .OrderBy( entry => entry.index )
            .Select( entry => entry.line )
            .ToList();
    }

    private static bool TryApplyPositionalVariantFromShopifyOrder(
        VatReportUnpaidProductRow row,
        IReadOnlyList<OrderLineVariantRow> lineRows,
        string orderId,
        string productId,
        IReadOnlyList<ShopifyLineItemDto> shopifyLines,
        int sourceSaleRowItemId )
    {
        if (sourceSaleRowItemId <= 0 || shopifyLines.Count == 0)
        {
            return false;
        }

        List<int> rowItemIds = lineRows
            .Where( line =>
                NormalizeOrderId( line.OrderId ) == orderId &&
                ProductIdsEqual( line.ProductId, productId ) )
            .OrderBy( line => line.RowItemId )
            .Select( line => line.RowItemId )
            .ToList();
        if (rowItemIds.Count != shopifyLines.Count)
        {
            return false;
        }

        int index = rowItemIds.IndexOf( sourceSaleRowItemId );
        if (index < 0)
        {
            return false;
        }

        ApplyShopifyLineVariantToUnpaidRow( row, shopifyLines[index] );
        return !string.IsNullOrWhiteSpace( row.ShopifyVariantTitle );
    }

    private static void ApplyShopifyLineVariantToUnpaidRow(
        VatReportUnpaidProductRow row,
        ShopifyLineItemDto line )
    {
        if (string.IsNullOrWhiteSpace( row.ShopifyVariantId ) && !string.IsNullOrWhiteSpace( line.ShopifyVariantId ))
        {
            row.ShopifyVariantId = NormalizeVariantId( line.ShopifyVariantId );
        }

        if (string.IsNullOrWhiteSpace( row.ShopifyVariantTitle ) && !string.IsNullOrWhiteSpace( line.VariantTitle ))
        {
            row.ShopifyVariantTitle = NormalizeVariantTitle( line.VariantTitle );
        }
    }

    private static T? PickBestVariantLineMatch<T>(
        IReadOnlyList<T> candidates,
        int rowQuantity,
        Func<T, int> quantitySelector,
        Func<T, string> variantIdSelector,
        Func<T, string> variantTitleSelector )
    {
        if (candidates.Count == 0)
        {
            return default;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        if (rowQuantity > 0)
        {
            List<T> sameQty = candidates
                .Where( line => quantitySelector( line ) == rowQuantity )
                .ToList();
            T? sameQtyMatch = PickSingleDistinctVariant( sameQty, variantIdSelector, variantTitleSelector );
            if (sameQtyMatch is not null)
            {
                return sameQtyMatch;
            }
        }

        return PickSingleDistinctVariant( candidates, variantIdSelector, variantTitleSelector );
    }

    private static T? PickSingleDistinctVariant<T>(
        IReadOnlyList<T> candidates,
        Func<T, string> variantIdSelector,
        Func<T, string> variantTitleSelector )
    {
        if (candidates.Count == 0)
        {
            return default;
        }

        List<T> distinct = candidates
            .GroupBy( line =>
            {
                string variantId = variantIdSelector( line );
                if (!string.IsNullOrWhiteSpace( variantId ))
                {
                    return variantId;
                }

                return variantTitleSelector( line ).ToLowerInvariant();
            } )
            .Select( group => group.First() )
            .ToList();
        return distinct.Count == 1 ? distinct[0] : default;
    }

    private static void AddVariantTitle(
        Dictionary<string, string> map,
        string variantIdRaw,
        string titleRaw )
    {
        string variantId = NormalizeVariantId( variantIdRaw );
        string title = NormalizeVariantTitle( titleRaw );
        if (string.IsNullOrWhiteSpace( variantId ) || string.IsNullOrWhiteSpace( title )) return;
        map[variantId] = title;
    }

    private static VatReportUnpaidProductRow MapUnpaidAccumulatorToRow(
        UnpaidAccumulator line,
        IReadOnlyDictionary<int, string> supplierNames )
    {
        decimal estimatedCogs = VatReportHelpers.Round2( line.Quantity * line.UnitSupplyPrice );
        string supplierName = line.SupplierId.HasValue &&
                              supplierNames.TryGetValue( line.SupplierId.Value, out string? name )
            ? name
            : string.Empty;
        return new VatReportUnpaidProductRow
        {
            ShopifyProductId = line.ShopifyProductId,
            ShopifyVariantId = line.ShopifyVariantId,
            ShopifyVariantTitle = line.ShopifyVariantTitle,
            ShopifyOrderId = line.ShopifyOrderId,
            ProductTitle = line.ProductTitle,
            Quantity = line.Quantity,
            SupplierId = line.SupplierId,
            SupplierName = supplierName,
            UnitSupplyPrice = line.UnitSupplyPrice,
            EstimatedCogs = estimatedCogs,
            SaleOrderDateUtc = line.EarliestSaleOrderDateUtc,
            SourceSaleRowItemId = line.SourceSaleRowItemId
        };
    }

    private async Task<Dictionary<int, ExpenseLabelRow>> LoadExpenseLabelsAsync( IEnumerable<int> expenseIds )
    {
        List<int> ids = expenseIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, ExpenseLabelRow>();
        }

        List<ExpenseLabelRow> rows = await _db.VatReportExpenses
            .AsNoTracking()
            .Where( expense => ids.Contains( expense.Id ) )
            .Select( expense => new ExpenseLabelRow
            {
                Id = expense.Id,
                InvoiceNumber = expense.InvoiceNumber,
                Comment = expense.Comment ?? string.Empty
            } )
            .ToListAsync();

        return rows.ToDictionary( row => row.Id );
    }

    private static string FormatExpenseLabel( ExpenseLabelRow expense )
    {
        if (!string.IsNullOrWhiteSpace( expense.InvoiceNumber ))
        {
            return expense.InvoiceNumber.Trim();
        }

        if (!string.IsNullOrWhiteSpace( expense.Comment ))
        {
            return expense.Comment.Trim();
        }

        return $"#{expense.Id}";
    }

    private async Task<decimal> ResolveUnpaidSupplyUnitPriceAsync(
        string productId,
        int supplierId,
        int periodYear,
        int periodMonth )
    {
        List<SupplyPriceRow> supplyPriceRows = await LoadSupplyPriceRowsAsync();
        int lastDay = DateTime.DaysInMonth( periodYear, periodMonth );
        DateTime periodEndUtc = new( periodYear, periodMonth, lastDay, 12, 0, 0, DateTimeKind.Utc );
        return ResolveSupplyFallbackUnitPrice(
            supplyPriceRows,
            NormalizeProductId( productId ),
            supplierId,
            periodEndUtc );
    }

    public async Task<VatReportProductAllocationDebugResponse> GetProductAllocationDebugAsync( string titleFragment )
    {
        string search = titleFragment.Trim();
        List<SaleUnit> rawSales = await LoadSaleUnitsAsync( deduplicate: false );
        List<SaleUnit> sales = DeduplicateSales( rawSales );
        HashSet<int> includedIds = sales.Select( s => s.Id ).ToHashSet();
        Dictionary<string, string> variantToProduct = await LoadVariantToProductMapAsync();
        List<PaymentUnit> paymentUnits = await LoadPaymentUnitsAsync();
        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle =
            await _variantLookup.GetVariantIdByProductTitleMapCachedAsync();
        IReadOnlyDictionary<string, string> variantTitleById =
            await _variantLookup.GetVariantTitleByIdMapCachedAsync();
        EnrichPaymentUnitVariants( paymentUnits, variantIdByTitle, variantTitleById );

        bool MatchesSearch( string title ) =>
            string.IsNullOrWhiteSpace( search ) ||
            title.Contains( search, StringComparison.OrdinalIgnoreCase );

        VatReportProductAllocationDebugResponse response = new()
        {
            SearchTitle = search,
            Sales = rawSales
                .Where( s => MatchesSearch( s.ProductTitle ) )
                .OrderBy( s => s.DateUtc )
                .ThenBy( s => s.Id )
                .Select( s => new VatReportAllocationDebugSaleRow
                {
                    SaleId = s.Id,
                    ShopifyOrderId = s.ShopifyOrderId,
                    ProductId = s.ProductId,
                    VariantId = s.VariantId,
                    ProductTitle = s.ProductTitle,
                    ReportType = s.ReportType,
                    PeriodYear = s.PeriodYear,
                    PeriodMonth = s.PeriodMonth,
                    OrderDateUtc = s.DateUtc,
                    Quantity = s.Quantity,
                    SupplierId = s.SupplierId,
                    IncludedAfterDedup = includedIds.Contains( s.Id )
                } )
                .ToList(),
            Payments = paymentUnits
                .Where( p => MatchesSearch( p.ProductTitle ) )
                .Select( p => new VatReportAllocationDebugPaymentRow
                {
                    PaymentId = p.Id,
                    ProductId = p.ProductId,
                    VariantId = p.VariantId,
                    ProductTitle = p.ProductTitle,
                    SupplierId = p.SupplierId,
                    Quantity = p.Quantity,
                    ExpenseDateUtc = p.DateUtc
                } )
                .ToList()
        };

        List<VatReportAllocationDebugStepRow> steps = [];
        List<SupplyPriceRow> supplyPrices = await LoadSupplyPriceRowsAsync();
        List<ManualAllocationPool> manualPools = await LoadManualAllocationPoolsAsync();
        Dictionary<string, string> productCoreTitleById = await LoadProductCoreTitleByIdMapAsync();
        HashSet<string> multiVariantProductIds = await LoadMultiVariantProductIdsAsync( variantToProduct );
        RunSalePaymentAllocation(
            sales,
            paymentUnits,
            supplyPrices,
            variantToProduct,
            productCoreTitleById,
            multiVariantProductIds,
            manualPools,
            steps,
            search );
        response.Steps = steps;
        return response;
    }

    private async Task<Dictionary<int, string>> LoadSupplierNamesAsync( IEnumerable<int> supplierIds )
    {
        List<int> ids = supplierIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        return await _db.Suppliers
            .AsNoTracking()
            .Where( s => s.Id.HasValue && ids.Contains( s.Id.Value ) )
            .ToDictionaryAsync( s => s.Id!.Value, s => s.Name );
    }

    private async Task<SaleCostAllocationResult> GetSaleCostAllocationCachedAsync()
    {
        if (_allocationCache is not null)
        {
            return _allocationCache;
        }

        _allocationCache = await BuildSaleCostAllocationAsync();
        _cogsByPeriodCache = _allocationCache.CogsByPeriod;
        return _allocationCache;
    }

    private async Task<Dictionary<(int Year, int Month), decimal>> BuildCogsByPeriodAsync()
    {
        SaleCostAllocationResult allocation = await GetSaleCostAllocationCachedAsync();
        return allocation.CogsByPeriod;
    }

    private async Task<SaleCostAllocationResult> BuildSaleCostAllocationAsync()
    {
        List<SaleUnit> saleUnits = await LoadSaleUnitsAsync();
        Dictionary<string, string> variantToProduct = await LoadVariantToProductMapAsync();
        List<PaymentUnit> paymentUnits = await LoadPaymentUnitsAsync();
        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle =
            await _variantLookup.GetVariantIdByProductTitleMapCachedAsync();
        IReadOnlyDictionary<string, string> variantTitleById =
            await _variantLookup.GetVariantTitleByIdMapCachedAsync();
        EnrichPaymentUnitVariants( paymentUnits, variantIdByTitle, variantTitleById );
        List<SupplyPriceRow> supplyPriceRows = await LoadSupplyPriceRowsAsync();
        List<ManualAllocationPool> manualPools = await LoadManualAllocationPoolsAsync();
        Dictionary<string, string> productCoreTitleById = await LoadProductCoreTitleByIdMapAsync();
        HashSet<string> multiVariantProductIds = await LoadMultiVariantProductIdsAsync( variantToProduct );
        return RunSalePaymentAllocation(
            saleUnits,
            paymentUnits,
            supplyPriceRows,
            variantToProduct,
            productCoreTitleById,
            multiVariantProductIds,
            manualPools );
    }

    private async Task<HashSet<string>> LoadMultiVariantProductIdsAsync(
        IReadOnlyDictionary<string, string> variantToProduct )
    {
        HashSet<string> productIds = new( StringComparer.OrdinalIgnoreCase );
        foreach (IGrouping<string, KeyValuePair<string, string>> group in variantToProduct
                     .GroupBy( entry => NormalizeProductId( entry.Value ) ))
        {
            string productId = group.Key;
            if (string.IsNullOrWhiteSpace( productId ))
            {
                continue;
            }

            if (group.Select( entry => NormalizeVariantId( entry.Key ) ).Distinct( StringComparer.OrdinalIgnoreCase ).Count() > 1)
            {
                productIds.Add( productId );
            }
        }

        try
        {
            IReadOnlySet<string> catalogProductIds = await _variantLookup.GetMultiVariantProductIdsCachedAsync();
            foreach (string productId in catalogProductIds)
            {
                string normalized = NormalizeProductId( productId );
                if (!string.IsNullOrWhiteSpace( normalized ))
                {
                    productIds.Add( normalized );
                }
            }
        }
        catch
        {
            // Shopify catalog unavailable — keep DB-derived ids only.
        }

        return productIds;
    }

    private async Task<Dictionary<string, string>> LoadProductCoreTitleByIdMapAsync()
    {
        Dictionary<string, string> map = new( StringComparer.OrdinalIgnoreCase );

        void Add( string? productIdRaw, string? productTitle )
        {
            string productId = NormalizeProductId( productIdRaw ?? string.Empty );
            string coreTitle = ExtractCoreProductTitleForMatch( productTitle ?? string.Empty );
            if (string.IsNullOrWhiteSpace( productId ) || coreTitle.Length < 3)
            {
                return;
            }

            if (map.TryGetValue( productId, out string? existingCore ))
            {
                if (coreTitle.Length > existingCore.Length )
                {
                    map[productId] = coreTitle;
                }
            }
            else
            {
                map[productId] = coreTitle;
            }
        }

        List<VariantProductTitleRow> rowItems = await _db.VatReportRowItems
            .AsNoTracking()
            .Where( item => !string.IsNullOrWhiteSpace( item.ShopifyProductId ) )
            .Select( item => new VariantProductTitleRow
            {
                ProductId = item.ShopifyProductId,
                ProductTitle = item.ProductTitle
            } )
            .ToListAsync();
        foreach (VariantProductTitleRow row in rowItems)
        {
            Add( row.ProductId, row.ProductTitle );
        }

        List<VariantProductTitleRow> cashSales = await _db.VatReportCashSales
            .AsNoTracking()
            .Where( sale => !string.IsNullOrWhiteSpace( sale.ShopifyProductId ) )
            .Select( sale => new VariantProductTitleRow
            {
                ProductId = sale.ShopifyProductId,
                ProductTitle = sale.ProductTitle
            } )
            .ToListAsync();
        foreach (VariantProductTitleRow row in cashSales)
        {
            Add( row.ProductId, row.ProductTitle );
        }

        List<VariantProductTitleRow> expenseProducts = await _db.VatReportExpenseProducts
            .AsNoTracking()
            .Where( product =>
                !string.IsNullOrWhiteSpace( product.ShopifyProductId ) ||
                !string.IsNullOrWhiteSpace( product.ProductTitle ) )
            .Select( product => new VariantProductTitleRow
            {
                ProductId = product.ShopifyProductId,
                ProductTitle = product.ProductTitle
            } )
            .ToListAsync();
        foreach (VariantProductTitleRow row in expenseProducts)
        {
            Add( row.ProductId, row.ProductTitle );
        }

        return map;
    }

    private async Task<List<ManualAllocationPool>> LoadManualAllocationPoolsAsync()
    {
        List<ManualAllocationPool> pools = await _db.VatReportUnpaidAllocations
            .AsNoTracking()
            .Select( allocation => new ManualAllocationPool
            {
                SalePeriodYear = allocation.SalePeriodYear,
                SalePeriodMonth = allocation.SalePeriodMonth,
                ShopifyProductId = allocation.ShopifyProductId,
                ShopifyVariantId = allocation.ShopifyVariantId,
                SupplierId = allocation.SupplierId,
                VatReportExpenseId = allocation.VatReportExpenseId,
                Remaining = allocation.Quantity
            } )
            .ToListAsync();

        foreach (ManualAllocationPool pool in pools)
        {
            pool.ShopifyProductId = NormalizeProductId( pool.ShopifyProductId );
            pool.ShopifyVariantId = NormalizeVariantId( pool.ShopifyVariantId );
        }

        return pools;
    }

    private static SaleCostAllocationResult RunSalePaymentAllocation(
        IReadOnlyList<SaleUnit> saleUnits,
        IReadOnlyList<PaymentUnit> paymentUnitsSource,
        IReadOnlyList<SupplyPriceRow> supplyPriceRows,
        IReadOnlyDictionary<string, string> variantToProduct,
        IReadOnlyDictionary<string, string> productCoreTitleById,
        IReadOnlySet<string> multiVariantProductIds,
        IReadOnlyList<ManualAllocationPool> manualAllocationPools,
        List<VatReportAllocationDebugStepRow>? traceSteps = null,
        string? traceTitleFilter = null )
    {
        Dictionary<(int Year, int Month), decimal> cogsByPeriod = new();
        Dictionary<(int Year, int Month), List<UnpaidAccumulator>> unpaidByPeriod = new();
        List<ManualAllocationPool> manualPools = manualAllocationPools
            .Select( pool => new ManualAllocationPool
            {
                SalePeriodYear = pool.SalePeriodYear,
                SalePeriodMonth = pool.SalePeriodMonth,
                ShopifyProductId = pool.ShopifyProductId,
                ShopifyVariantId = pool.ShopifyVariantId,
                SupplierId = pool.SupplierId,
                VatReportExpenseId = pool.VatReportExpenseId,
                Remaining = pool.Remaining
            } )
            .ToList();
        List<PaymentUnit> availablePayments = paymentUnitsSource
            .Select( p => new PaymentUnit
            {
                Id = p.Id,
                ExpenseId = p.ExpenseId,
                DateUtc = p.DateUtc,
                CatalogProductId = p.CatalogProductId,
                ProductId = p.ProductId,
                VariantId = p.VariantId,
                VariantTitle = p.VariantTitle,
                ProductTitle = p.ProductTitle,
                SupplierId = p.SupplierId,
                UnitGrossPrice = p.UnitGrossPrice,
                Quantity = p.Quantity,
                Remaining = p.Remaining
            } )
            .ToList();

        bool ShouldTrace( string productTitle ) =>
            traceSteps is null ||
            string.IsNullOrWhiteSpace( traceTitleFilter ) ||
            productTitle.Contains( traceTitleFilter, StringComparison.OrdinalIgnoreCase );

        void Trace( string eventName, string details )
        {
            if (traceSteps is null) return;
            traceSteps.Add( new VatReportAllocationDebugStepRow
            {
                Order = traceSteps.Count + 1,
                Event = eventName,
                Details = details
            } );
        }

        void AddCogs( int year, int month, decimal amount )
        {
            if (amount <= 0m) return;
            (int Year, int Month) key = (year, month);
            cogsByPeriod[key] = cogsByPeriod.GetValueOrDefault( key ) + amount;
        }

        void AddUnpaid(
            int year,
            int month,
            string shopifyOrderId,
            string productId,
            string variantId,
            string variantTitle,
            string productTitle,
            int? supplierId,
            int quantity,
            decimal unitSupplyPrice,
            DateTime saleOrderDateUtc,
            int sourceSaleRowItemId )
        {
            if (quantity <= 0) return;
            (int Year, int Month) key = (year, month);
            if (!unpaidByPeriod.TryGetValue( key, out List<UnpaidAccumulator>? lines ))
            {
                lines = [];
                unpaidByPeriod[key] = lines;
            }

            UnpaidAccumulator? existing = lines.FirstOrDefault( line =>
                UnpaidAccumulatorKeysMatch(
                    line,
                    productId,
                    variantId,
                    variantTitle,
                    shopifyOrderId,
                    supplierId,
                    sourceSaleRowItemId ) );
            if (existing is null)
            {
                lines.Add( new UnpaidAccumulator
                {
                    ShopifyProductId = productId,
                    ShopifyVariantId = variantId,
                    ShopifyVariantTitle = variantTitle,
                    ShopifyOrderId = shopifyOrderId,
                    ProductTitle = productTitle,
                    SupplierId = supplierId,
                    Quantity = quantity,
                    UnitSupplyPrice = unitSupplyPrice,
                    EarliestSaleOrderDateUtc = saleOrderDateUtc,
                    SourceSaleRowItemId = sourceSaleRowItemId > 0 ? sourceSaleRowItemId : null
                } );
                return;
            }

            existing.Quantity += quantity;
            if (string.IsNullOrWhiteSpace( existing.ShopifyVariantTitle ) && !string.IsNullOrWhiteSpace( variantTitle ))
            {
                existing.ShopifyVariantTitle = variantTitle;
            }

            if (string.IsNullOrWhiteSpace( existing.ShopifyVariantId ) && !string.IsNullOrWhiteSpace( variantId ))
            {
                existing.ShopifyVariantId = variantId;
            }

            if (!existing.SourceSaleRowItemId.HasValue && sourceSaleRowItemId > 0)
            {
                existing.SourceSaleRowItemId = sourceSaleRowItemId;
            }

            if (saleOrderDateUtc < existing.EarliestSaleOrderDateUtc)
            {
                existing.EarliestSaleOrderDateUtc = saleOrderDateUtc;
                if (!string.IsNullOrWhiteSpace( shopifyOrderId ))
                {
                    existing.ShopifyOrderId = shopifyOrderId;
                }

                if (sourceSaleRowItemId > 0)
                {
                    existing.SourceSaleRowItemId = sourceSaleRowItemId;
                }
            }
        }

        static (int Year, int Month) ResolveUnpaidPeriod( SaleUnit sale )
        {
            if (string.Equals( sale.ReportType, VatReportType.Cash, StringComparison.OrdinalIgnoreCase ))
            {
                return (sale.PeriodYear, sale.PeriodMonth);
            }

            return VatReportHelpers.ResolveSaleCalendarPeriod( sale.DateUtc );
        }

        static bool UnpaidAccumulatorKeysMatch(
            UnpaidAccumulator line,
            string productId,
            string variantId,
            string variantTitle,
            string shopifyOrderId,
            int? supplierId,
            int sourceSaleRowItemId )
        {
            if (line.SupplierId != supplierId)
            {
                return false;
            }

            if (!ProductIdsEqual( line.ShopifyProductId, productId ))
            {
                return false;
            }

            if (line.SourceSaleRowItemId.HasValue && sourceSaleRowItemId > 0)
            {
                return line.SourceSaleRowItemId.Value == sourceSaleRowItemId;
            }

            string existingVariantId = NormalizeVariantId( line.ShopifyVariantId );
            string incomingVariantId = NormalizeVariantId( variantId );
            if (!string.IsNullOrWhiteSpace( existingVariantId ) || !string.IsNullOrWhiteSpace( incomingVariantId ))
            {
                return VatReportHelpers.ProductLineKeysEqual(
                    line.ShopifyProductId,
                    line.ShopifyVariantId,
                    productId,
                    variantId );
            }

            string existingVariantTitle = NormalizeVariantTitle( line.ShopifyVariantTitle );
            string incomingVariantTitle = NormalizeVariantTitle( variantTitle );
            if (!string.IsNullOrWhiteSpace( existingVariantTitle ) || !string.IsNullOrWhiteSpace( incomingVariantTitle ))
            {
                return string.Equals(
                    existingVariantTitle,
                    incomingVariantTitle,
                    StringComparison.OrdinalIgnoreCase );
            }

            return false;
        }

        (decimal Cost, int UnpaidQuantity) AllocateFromPayments(
            SaleUnit sale,
            int quantity )
        {
            decimal allocatedCost = 0m;
            int remaining = quantity;
            (int unpaidPeriodYear, int unpaidPeriodMonth) = ResolveUnpaidPeriod( sale );

            if (sale.SupplierId.HasValue && sale.SupplierId.Value > 0)
            {
                foreach (ManualAllocationPool pool in manualPools.Where( pool =>
                             pool.Remaining > 0 &&
                             pool.SalePeriodYear == unpaidPeriodYear &&
                             pool.SalePeriodMonth == unpaidPeriodMonth &&
                             pool.SupplierId == sale.SupplierId.Value &&
                             VatReportHelpers.ProductLinesCompatible(
                                 pool.ShopifyProductId,
                                 pool.ShopifyVariantId,
                                 sale.ProductId,
                                 sale.VariantId ) ))
                {
                    foreach (PaymentUnit payment in availablePayments
                        .Where( p => p.ExpenseId == pool.VatReportExpenseId && p.Remaining > 0 )
                        .OrderBy( p => p.DateUtc )
                        .ThenBy( p => p.Id ))
                    {
                        if (remaining <= 0 || pool.Remaining <= 0) break;

                        int take = Math.Min( remaining, Math.Min( payment.Remaining, pool.Remaining ) );
                        if (take <= 0) continue;

                        payment.Remaining -= take;
                        pool.Remaining -= take;
                        remaining -= take;
                        decimal unitCost = payment.UnitGrossPrice > 0m
                            ? payment.UnitGrossPrice
                            : ResolveSupplyFallbackUnitPrice(
                                supplyPriceRows,
                                sale.ProductId,
                                sale.SupplierId,
                                payment.DateUtc );
                        allocatedCost += take * unitCost;

                        if (ShouldTrace( sale.ProductTitle ))
                        {
                            Trace(
                                "manual-link",
                                $"expenseId={pool.VatReportExpenseId} paymentId={payment.Id} take={take}" );
                        }
                    }
                }
            }

            IEnumerable<PaymentUnit> ProductPaymentCandidates( bool sameSupplierOnly )
            {
                // Payments are not gated by expense date vs sale date: an invoice dated 30 June
                // must still cover sales earlier in the same month (e.g. 13 June).
                IEnumerable<PaymentUnit> query = availablePayments
                    .Where( p =>
                        p.Remaining > 0 &&
                        PaymentMatchesProduct(
                            p,
                            sale,
                            variantToProduct,
                            productCoreTitleById,
                            multiVariantProductIds ) );

                if (!sale.SupplierId.HasValue || sale.SupplierId.Value <= 0)
                {
                    return sameSupplierOnly ? query : [];
                }

                return sameSupplierOnly
                    ? query.Where( p =>
                        !p.SupplierId.HasValue || p.SupplierId.Value == sale.SupplierId.Value )
                    : query.Where( p =>
                        p.SupplierId.HasValue && p.SupplierId.Value != sale.SupplierId.Value );
            }

            void ConsumePayment( PaymentUnit payment, int take, string traceKind )
            {
                payment.Remaining -= take;
                remaining -= take;
                int? priceSupplierId = payment.SupplierId ?? sale.SupplierId;
                decimal unitCost = payment.UnitGrossPrice > 0m
                    ? payment.UnitGrossPrice
                    : ResolveSupplyFallbackUnitPrice(
                        supplyPriceRows,
                        sale.ProductId,
                        priceSupplierId,
                        payment.DateUtc );
                allocatedCost += take * unitCost;

                if (ShouldTrace( sale.ProductTitle ))
                {
                    Trace(
                        traceKind,
                        $"paymentId={payment.Id} take={take} remainingSale={remaining} paymentLeft={payment.Remaining}" );
                }
            }

            List<PaymentUnit> sameSupplierCandidates = ProductPaymentCandidates( sameSupplierOnly: true )
                .OrderBy( p => p.DateUtc )
                .ThenBy( p => p.Id )
                .ToList();

            if (ShouldTrace( sale.ProductTitle ))
            {
                int matchingRemaining = availablePayments.Count( p =>
                    p.Remaining > 0 &&
                    PaymentMatchesProduct(
                        p,
                        sale,
                        variantToProduct,
                        productCoreTitleById,
                        multiVariantProductIds ) );
                string paymentPool = string.Join(
                    ", ",
                    availablePayments
                        .Where( p => PaymentMatchesProduct(
                            p,
                            sale,
                            variantToProduct,
                            productCoreTitleById,
                            multiVariantProductIds ) )
                        .OrderBy( p => p.DateUtc )
                        .ThenBy( p => p.Id )
                        .Select( p => $"#{p.Id}:{p.Remaining}/{p.Quantity}" ) );
                Trace(
                    "sale",
                    $"saleId={sale.Id} order={sale.ShopifyOrderId} product={sale.ProductId} " +
                    $"variant={sale.VariantId} supplier={sale.SupplierId} qty={quantity} " +
                    $"candidates={sameSupplierCandidates.Count} matchingRemaining={matchingRemaining} pool=[{paymentPool}]" );
            }

            foreach (PaymentUnit payment in sameSupplierCandidates)
            {
                if (remaining <= 0) break;

                int take = Math.Min( remaining, payment.Remaining );
                if (take <= 0) continue;

                ConsumePayment( payment, take, "payment" );
            }

            if (remaining > 0)
            {
                foreach (PaymentUnit payment in ProductPaymentCandidates( sameSupplierOnly: false )
                             .OrderBy( p => p.DateUtc )
                             .ThenBy( p => p.Id )
                             .ToList())
                {
                    if (remaining <= 0) break;

                    int take = Math.Min( remaining, payment.Remaining );
                    if (take <= 0) continue;

                    ConsumePayment( payment, take, "payment-cross-supplier" );
                }
            }

            return (allocatedCost, remaining);
        }

        foreach (SaleUnit sale in saleUnits.OrderBy( s => s.DateUtc ).ThenBy( s => s.Id ))
        {
            (int unpaidPeriodYear, int unpaidPeriodMonth) = ResolveUnpaidPeriod( sale );
            (decimal cost, int unpaidQuantity) = AllocateFromPayments( sale, sale.Quantity );

            if (unpaidQuantity > 0)
            {
                decimal supplyUnitPrice = ResolveSupplyFallbackUnitPrice(
                    supplyPriceRows,
                    sale.ProductId,
                    sale.SupplierId,
                    sale.DateUtc );
                if (supplyUnitPrice > 0m)
                {
                    cost += unpaidQuantity * supplyUnitPrice;
                }

                AddUnpaid(
                    unpaidPeriodYear,
                    unpaidPeriodMonth,
                    sale.ShopifyOrderId,
                    sale.ProductId,
                    sale.VariantId,
                    sale.VariantTitle,
                    sale.ProductTitle,
                    sale.SupplierId,
                    unpaidQuantity,
                    supplyUnitPrice,
                    sale.DateUtc,
                    sale.Id );

                if (ShouldTrace( sale.ProductTitle ))
                {
                    Trace(
                        "unpaid",
                        $"saleId={sale.Id} period={unpaidPeriodYear}-{unpaidPeriodMonth:D2} unpaidQty={unpaidQuantity}" );
                }
            }

            AddCogs( sale.PeriodYear, sale.PeriodMonth, cost );
        }

        foreach (KeyValuePair<(int Year, int Month), decimal> entry in cogsByPeriod.ToList())
        {
            cogsByPeriod[entry.Key] = VatReportHelpers.Round2( entry.Value );
        }

        return new SaleCostAllocationResult
        {
            CogsByPeriod = cogsByPeriod,
            UnpaidByPeriod = unpaidByPeriod
        };
    }

    private async Task<List<SupplyPriceRow>> LoadSupplyPriceRowsAsync()
    {
        List<SupplyPriceRow> rows = await _db.SupplyProducts
            .AsNoTracking()
            .Where( sp =>
                sp.SupplierPrice > 0m &&
                !string.IsNullOrWhiteSpace( sp.ShopifyProductId ) )
            .Select( sp => new SupplyPriceRow
            {
                SupplierId = sp.Supply.SupplierId,
                ProductId = sp.ShopifyProductId,
                SupplierPrice = sp.SupplierPrice,
                SupplyDate = sp.Supply.Date,
                SupplyId = sp.SupplyId,
                RowId = sp.Id
            } )
            .ToListAsync();

        return rows
            .Select( row =>
            {
                row.ProductId = NormalizeProductId( row.ProductId );
                return row;
            } )
            .OrderByDescending( row => row.SupplyDate )
            .ThenByDescending( row => row.SupplyId )
            .ThenByDescending( row => row.RowId )
            .ToList();
    }

    private static decimal ResolveSupplyFallbackUnitPrice(
        IReadOnlyList<SupplyPriceRow> rows,
        string productId,
        int? supplierId,
        DateTime asOfDateUtc )
    {
        DateOnly asOfDate = DateOnly.FromDateTime( asOfDateUtc );
        IEnumerable<SupplyPriceRow> candidates = rows
            .Where( row =>
                ProductIdsEqual( row.ProductId, productId ) &&
                row.SupplyDate <= asOfDate );

        if (supplierId.HasValue && supplierId.Value > 0)
        {
            candidates = candidates.Where( row => row.SupplierId == supplierId.Value );
        }

        SupplyPriceRow? match = candidates
            .OrderByDescending( row => row.SupplyDate )
            .ThenByDescending( row => row.SupplyId )
            .ThenByDescending( row => row.RowId )
            .FirstOrDefault();

        return match?.SupplierPrice ?? 0m;
    }

    private async Task<List<SaleUnit>> LoadSaleUnitsAsync( bool deduplicate = true )
    {
        List<SaleUnit> sales = new();

        List<RowSaleRow> rowSales = await _db.VatReportRowItems
            .AsNoTracking()
            .Where( i =>
                i.Quantity > 0 &&
                !string.IsNullOrWhiteSpace( i.ShopifyProductId ) &&
                (i.VatReportRow.VatReport.Type == VatReportType.Poland ||
                 i.VatReportRow.VatReport.Type == VatReportType.Foreign) )
            .Select( i => new RowSaleRow
            {
                Id = i.Id,
                ShopifyOrderId = i.VatReportRow.ShopifyOrderId,
                ReportType = i.VatReportRow.VatReport.Type,
                ProductId = i.ShopifyProductId,
                VariantId = i.ShopifyVariantId,
                VariantTitle = i.VariantTitle,
                ProductTitle = i.ProductTitle,
                Quantity = i.Quantity,
                OrderDateUtc = i.VatReportRow.OrderDateUtc,
                PeriodYear = i.VatReportRow.VatReport.PeriodYear,
                PeriodMonth = i.VatReportRow.VatReport.PeriodMonth
            } )
            .ToListAsync();

        foreach (RowSaleRow row in rowSales)
        {
            sales.Add( new SaleUnit
            {
                Id = row.Id,
                ShopifyOrderId = NormalizeOrderId( row.ShopifyOrderId ),
                ReportType = row.ReportType,
                ProductId = NormalizeProductId( row.ProductId ),
                VariantId = NormalizeVariantId( row.VariantId ),
                VariantTitle = NormalizeVariantTitle( row.VariantTitle ),
                ProductTitle = row.ProductTitle,
                Quantity = row.Quantity,
                DateUtc = row.OrderDateUtc,
                PeriodYear = row.PeriodYear,
                PeriodMonth = row.PeriodMonth,
                SupplierId = null
            } );
        }

        List<CashSaleRow> cashSales = await _db.VatReportCashSales
            .AsNoTracking()
            .Where( x => x.Quantity > 0 && !string.IsNullOrWhiteSpace( x.ShopifyProductId ) )
            .Select( x => new CashSaleRow
            {
                Id = x.Id,
                ProductId = x.ShopifyProductId,
                VariantId = x.ShopifyVariantId,
                ProductTitle = x.ProductTitle,
                Quantity = x.Quantity,
                CreatedAtUtc = x.CreatedAtUtc,
                PeriodYear = x.VatReport.PeriodYear,
                PeriodMonth = x.VatReport.PeriodMonth
            } )
            .ToListAsync();

        foreach (CashSaleRow row in cashSales)
        {
            sales.Add( new SaleUnit
            {
                Id = 1_000_000 + row.Id,
                ShopifyOrderId = string.Empty,
                ReportType = VatReportType.Cash,
                ProductId = NormalizeProductId( row.ProductId ),
                VariantId = NormalizeVariantId( row.VariantId ),
                VariantTitle = VatReportHelpers.ExtractVariantTitleFromProductLineTitle( row.ProductTitle ),
                ProductTitle = row.ProductTitle,
                Quantity = row.Quantity,
                DateUtc = VatReportHelpers.ResolveCashSaleDateUtc( row.PeriodYear, row.PeriodMonth ),
                PeriodYear = row.PeriodYear,
                PeriodMonth = row.PeriodMonth,
                SupplierId = null
            } );
        }

        List<SupplyEventRow> supplyEvents = await LoadSupplyEventsAsync();
        Dictionary<string, string> variantToProduct = await LoadVariantToProductMapAsync();
        await EnrichSaleVariantsFromRowItemsAsync( sales );
        bool needsCatalogVariantLookup = sales.Any( sale => string.IsNullOrWhiteSpace( sale.VariantId ) );
        if (needsCatalogVariantLookup)
        {
            IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle =
                await _variantLookup.GetVariantIdByProductTitleMapCachedAsync();
            IReadOnlyDictionary<string, string> variantTitleById =
                await _variantLookup.GetVariantTitleByIdMapCachedAsync();
            EnrichMissingSaleVariantIds( sales, variantIdByTitle, variantTitleById );
        }
        else if (sales.Any( sale =>
                     string.IsNullOrWhiteSpace( sale.VariantTitle ) && !string.IsNullOrWhiteSpace( sale.VariantId ) ))
        {
            IReadOnlyDictionary<string, string> variantTitleById =
                await _variantLookup.GetVariantTitleByIdMapCachedAsync();
            foreach (SaleUnit sale in sales)
            {
                if (string.IsNullOrWhiteSpace( sale.VariantTitle ) && !string.IsNullOrWhiteSpace( sale.VariantId ) &&
                    variantTitleById.TryGetValue( sale.VariantId, out string? title ) &&
                    !string.IsNullOrWhiteSpace( title ))
                {
                    sale.VariantTitle = NormalizeVariantTitle( title );
                }
            }
        }

        AssignSuppliersFromSupplyFifo( sales, supplyEvents, variantToProduct );

        List<SaleUnit> result = deduplicate ? DeduplicateSales( sales ) : sales;
        _saleUnitsWithSuppliersCache = result;
        return result;
    }

    private static List<SaleUnit> DeduplicateSales( List<SaleUnit> sales )
    {
        static int ReportTypePriority( string reportType ) =>
            string.Equals( reportType, VatReportType.Poland, StringComparison.OrdinalIgnoreCase ) ? 0 :
            string.Equals( reportType, VatReportType.Foreign, StringComparison.OrdinalIgnoreCase ) ? 1 : 2;

        List<SaleUnit> result = new();
        foreach (IGrouping<string, SaleUnit> group in sales.GroupBy( BuildSaleDedupKey ))
        {
            SaleUnit representative = group
                .OrderBy( sale => ReportTypePriority( sale.ReportType ) )
                .ThenByDescending( sale => string.IsNullOrWhiteSpace( sale.VariantId ) ? 0 : 1 )
                .ThenByDescending( sale => string.IsNullOrWhiteSpace( sale.VariantTitle ) ? 0 : 1 )
                .ThenBy( sale => sale.Id )
                .First();
            int totalQuantity = group.Sum( sale => sale.Quantity );
            if (totalQuantity != representative.Quantity)
            {
                representative = new SaleUnit
                {
                    Id = representative.Id,
                    ShopifyOrderId = representative.ShopifyOrderId,
                    ReportType = representative.ReportType,
                    ProductId = representative.ProductId,
                    VariantId = representative.VariantId,
                    VariantTitle = representative.VariantTitle,
                    ProductTitle = representative.ProductTitle,
                    Quantity = totalQuantity,
                    DateUtc = representative.DateUtc,
                    PeriodYear = representative.PeriodYear,
                    PeriodMonth = representative.PeriodMonth,
                    SupplierId = representative.SupplierId
                };
            }

            result.Add( representative );
        }

        return result;
    }

    private static string BuildSaleDedupKey( SaleUnit sale )
    {
        if (string.IsNullOrWhiteSpace( sale.ShopifyOrderId ))
        {
            return $"cash:{sale.Id}";
        }

        // One Shopify order line per product/variant/period — do not key on row-item id or
        // Poland + Foreign copies of the same order consume two payment units.
        string variantPart = NormalizeVariantId( sale.VariantId );
        if (string.IsNullOrWhiteSpace( variantPart ))
        {
            string variantTitle = NormalizeVariantTitle( sale.VariantTitle );
            if (!string.IsNullOrWhiteSpace( variantTitle ))
            {
                variantPart = $"title:{variantTitle.ToLowerInvariant()}";
            }
        }

        return $"{sale.ShopifyOrderId}|{sale.ProductId}|{variantPart}|{sale.PeriodYear:D4}-{sale.PeriodMonth:D2}";
    }

    private async Task<Dictionary<string, string>> LoadVariantToProductMapAsync()
    {
        Dictionary<string, string> map = new( StringComparer.OrdinalIgnoreCase );

        void AddMapping( string? variantRaw, string? productRaw )
        {
            string variantId = NormalizeVariantId( variantRaw ?? string.Empty );
            string productId = NormalizeProductId( productRaw ?? string.Empty );
            if (string.IsNullOrWhiteSpace( variantId ) || string.IsNullOrWhiteSpace( productId ))
            {
                return;
            }

            map[variantId] = productId;
        }

        List<VariantProductRow> supplyRows = await _db.SupplyProducts
            .AsNoTracking()
            .Where( sp =>
                !string.IsNullOrWhiteSpace( sp.ShopifyVariantId ) &&
                !string.IsNullOrWhiteSpace( sp.ShopifyProductId ) )
            .Select( sp => new VariantProductRow
            {
                VariantId = sp.ShopifyVariantId,
                ProductId = sp.ShopifyProductId
            } )
            .ToListAsync();
        foreach (VariantProductRow row in supplyRows)
        {
            AddMapping( row.VariantId, row.ProductId );
        }

        List<VariantProductRow> expenseRows = await _db.VatReportExpenseProducts
            .AsNoTracking()
            .Where( p =>
                !string.IsNullOrWhiteSpace( p.ShopifyVariantId ) &&
                !string.IsNullOrWhiteSpace( p.ShopifyProductId ) )
            .Select( p => new VariantProductRow
            {
                VariantId = p.ShopifyVariantId,
                ProductId = p.ShopifyProductId
            } )
            .ToListAsync();
        foreach (VariantProductRow row in expenseRows)
        {
            AddMapping( row.VariantId, row.ProductId );
        }

        List<VariantProductRow> cashRows = await _db.VatReportCashSales
            .AsNoTracking()
            .Where( x =>
                !string.IsNullOrWhiteSpace( x.ShopifyVariantId ) &&
                !string.IsNullOrWhiteSpace( x.ShopifyProductId ) )
            .Select( x => new VariantProductRow
            {
                VariantId = x.ShopifyVariantId,
                ProductId = x.ShopifyProductId
            } )
            .ToListAsync();
        foreach (VariantProductRow row in cashRows)
        {
            AddMapping( row.VariantId, row.ProductId );
        }

        return map;
    }

    private static void EnrichMissingSaleVariantIds(
        List<SaleUnit> sales,
        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle,
        IReadOnlyDictionary<string, string> variantTitleById )
    {
        foreach (SaleUnit sale in sales)
        {
            if (string.IsNullOrWhiteSpace( sale.VariantId ))
            {
                string variantTitleCandidate = sale.VariantTitle;
                if (string.IsNullOrWhiteSpace( variantTitleCandidate ) &&
                    string.IsNullOrWhiteSpace( sale.ShopifyOrderId ))
                {
                    variantTitleCandidate = VatReportHelpers.ExtractVariantTitleFromProductLineTitle( sale.ProductTitle );
                }

                if (!string.IsNullOrWhiteSpace( variantTitleCandidate ))
                {
                    string resolved = ShopifyVariantLookupService.ResolveVariantIdByProductTitle(
                        sale.ProductId,
                        variantTitleCandidate,
                        variantIdByTitle );
                    if (!string.IsNullOrWhiteSpace( resolved ))
                    {
                        sale.VariantId = resolved;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace( sale.VariantTitle ) && !string.IsNullOrWhiteSpace( sale.VariantId ) &&
                variantTitleById.TryGetValue( sale.VariantId, out string? title ) &&
                !string.IsNullOrWhiteSpace( title ))
            {
                sale.VariantTitle = NormalizeVariantTitle( title );
            }
        }
    }

    private static void AssignSuppliersFromSupplyFifo(
        List<SaleUnit> sales,
        List<SupplyEventRow> supplyEvents,
        IReadOnlyDictionary<string, string> variantToProduct )
    {
        List<SupplyBatchRow> batches = new();
        List<TimelineEntry> timeline = new();

        foreach (SupplyEventRow supplyEvent in supplyEvents)
        {
            timeline.Add( new TimelineEntry
            {
                DateUtc = supplyEvent.SupplyDate.ToDateTime( TimeOnly.MinValue, DateTimeKind.Utc ),
                KindOrder = 0,
                Sequence = supplyEvent.SupplyId * 10_000 + supplyEvent.RowId,
                Supply = supplyEvent
            } );
        }

        foreach (SaleUnit sale in sales)
        {
            timeline.Add( new TimelineEntry
            {
                DateUtc = sale.DateUtc,
                KindOrder = 1,
                Sequence = sale.Id,
                Sale = sale
            } );
        }

        foreach (TimelineEntry entry in timeline
                     .OrderBy( entry => entry.DateUtc )
                     .ThenBy( entry => entry.KindOrder )
                     .ThenBy( entry => entry.Sequence ))
        {
            if (entry.Supply is not null)
            {
                if (entry.Supply.Quantity > 0)
                {
                    batches.Add( new SupplyBatchRow
                    {
                        SupplierId = entry.Supply.SupplierId,
                        ProductId = entry.Supply.ProductId,
                        VariantId = entry.Supply.VariantId,
                        Quantity = entry.Supply.Quantity,
                        Remaining = entry.Supply.Quantity,
                        SupplyDate = entry.Supply.SupplyDate
                    } );
                }
                else
                {
                    ApplySupplyReturn( batches, entry.Supply, variantToProduct );
                }

                continue;
            }

            if (entry.Sale is not null)
            {
                AssignSaleFromBatches( entry.Sale, batches, variantToProduct );
            }
        }
    }

    private static void ApplySupplyReturn(
        List<SupplyBatchRow> batches,
        SupplyEventRow supplyReturn,
        IReadOnlyDictionary<string, string> variantToProduct )
    {
        int remaining = Math.Abs( supplyReturn.Quantity );
        foreach (SupplyBatchRow batch in batches
                     .Where( batch =>
                         batch.SupplierId == supplyReturn.SupplierId &&
                         batch.Remaining > 0 &&
                         ProductLineMatchesBatch(
                             supplyReturn.ProductId,
                             supplyReturn.VariantId,
                             batch,
                             variantToProduct ) )
                     .OrderBy( batch => batch.SupplyDate )
                     .ThenBy( batch => batch.SupplierId ))
        {
            if (remaining <= 0)
            {
                break;
            }

            int take = Math.Min( remaining, batch.Remaining );
            if (take <= 0)
            {
                continue;
            }

            batch.Remaining -= take;
            remaining -= take;
        }
    }

    private static void AssignSaleFromBatches(
        SaleUnit sale,
        List<SupplyBatchRow> batches,
        IReadOnlyDictionary<string, string> variantToProduct )
    {
        int remaining = sale.Quantity;
        foreach (SupplyBatchRow batch in batches.Where( batch =>
                     SupplyLineMatchesProduct( sale, batch, variantToProduct ) ))
        {
            if (remaining <= 0)
            {
                break;
            }

            if (batch.Remaining <= 0)
            {
                continue;
            }

            int take = Math.Min( remaining, batch.Remaining );
            if (take <= 0)
            {
                continue;
            }

            if (!sale.SupplierId.HasValue)
            {
                sale.SupplierId = batch.SupplierId;
            }

            batch.Remaining -= take;
            remaining -= take;
        }
    }

    private static bool ProductLineMatchesBatch(
        string productId,
        string variantId,
        SupplyBatchRow batch,
        IReadOnlyDictionary<string, string> variantToProduct )
    {
        SaleUnit probe = new()
        {
            ProductId = productId,
            VariantId = variantId
        };
        return SupplyLineMatchesProduct( probe, batch, variantToProduct );
    }

    private async Task<List<SupplyEventRow>> LoadSupplyEventsAsync()
    {
        List<SupplyEventRow> supplyEvents = await _db.SupplyProducts
            .AsNoTracking()
            .Select( sp => new SupplyEventRow
            {
                SupplyId = sp.SupplyId,
                RowId = sp.Id,
                SupplierId = sp.Supply.SupplierId,
                ProductId = sp.ShopifyProductId,
                VariantId = sp.ShopifyVariantId,
                Quantity = sp.Quantity,
                SupplyDate = sp.Supply.Date
            } )
            .ToListAsync();

        return supplyEvents
            .Select( supplyEvent =>
            {
                supplyEvent.ProductId = NormalizeProductId( supplyEvent.ProductId );
                supplyEvent.VariantId = NormalizeVariantId( supplyEvent.VariantId );
                return supplyEvent;
            } )
            .Where( supplyEvent =>
                supplyEvent.Quantity != 0 &&
                (
                    !string.IsNullOrWhiteSpace( supplyEvent.ProductId ) ||
                    !string.IsNullOrWhiteSpace( supplyEvent.VariantId ) ) )
            .OrderBy( supplyEvent => supplyEvent.SupplyDate )
            .ThenBy( supplyEvent => supplyEvent.SupplyId )
            .ThenBy( supplyEvent => supplyEvent.RowId )
            .ToList();
    }

    private async Task<List<PaymentUnit>> LoadPaymentUnitsAsync()
    {
        List<PaymentUnit> units = new();
        List<PaymentProductRow> rows = await _db.VatReportExpenseProducts
            .AsNoTracking()
            .Where( p =>
                p.Quantity > 0 &&
                (
                    !string.IsNullOrWhiteSpace( p.ShopifyProductId ) ||
                    !string.IsNullOrWhiteSpace( p.ShopifyVariantId ) ||
                    !string.IsNullOrWhiteSpace( p.ProductTitle )
                ) &&
                (p.VatReportExpense.ExpenseInvoiceType.Name == ExpenseInvoiceTypeSeeder.SupplierPaymentDefaultName ||
                 p.VatReportExpense.ExpenseInvoiceType.Name == ExpenseInvoiceTypeSeeder.LegacySupplierPaymentName) )
            .Select( p => new PaymentProductRow
            {
                ExpenseId = p.VatReportExpenseId,
                ExpenseGrossAmount = p.VatReportExpense.GrossAmount,
                Id = p.Id,
                ProductId = p.ShopifyProductId,
                VariantId = p.ShopifyVariantId,
                ProductTitle = p.ProductTitle,
                Quantity = p.Quantity,
                UnitGrossPrice = p.UnitGrossPrice,
                DateUtc = p.VatReportExpense.ExpenseDateUtc,
                SupplierId = p.VatReportExpense.SupplierId
            } )
            .OrderBy( p => p.DateUtc )
            .ThenBy( p => p.Id )
            .ToListAsync();

        foreach (IGrouping<int, PaymentProductRow> expenseGroup in rows.GroupBy( r => r.ExpenseId ))
        {
            List<PaymentProductRow> lines = expenseGroup.OrderBy( x => x.Id ).ToList();
            decimal extraPerUnit = ComputeSupplierPaymentExtraPerUnit(
                lines[0].ExpenseGrossAmount,
                lines.Select( l => (l.Quantity, l.UnitGrossPrice) )
            );

            foreach (PaymentProductRow row in lines)
            {
                units.Add( new PaymentUnit
                {
                    Id = row.Id,
                    ExpenseId = row.ExpenseId,
                    CatalogProductId = NormalizeProductId( row.ProductId ),
                    ProductId = ResolvePaymentProductId( row.ProductId, row.VariantId ),
                    VariantId = NormalizeVariantId( row.VariantId ),
                    VariantTitle = NormalizeVariantTitle(
                        VatReportHelpers.ExtractVariantTitleFromProductLineTitle( row.ProductTitle ) ),
                    ProductTitle = row.ProductTitle?.Trim() ?? string.Empty,
                    Quantity = row.Quantity,
                    Remaining = row.Quantity,
                    UnitGrossPrice = row.UnitGrossPrice + extraPerUnit,
                    DateUtc = row.DateUtc,
                    SupplierId = row.SupplierId
                } );
            }
        }

        return units
            .OrderBy( u => u.DateUtc )
            .ThenBy( u => u.Id )
            .ToList();
    }

    private static decimal ComputeSupplierPaymentExtraPerUnit(
        decimal expenseGrossAmount,
        IEnumerable<(int Quantity, decimal UnitGrossPrice)> lines )
    {
        List<(int Quantity, decimal UnitGrossPrice)> lineList = lines.ToList();
        if (lineList.Count == 0)
        {
            return 0m;
        }

        decimal productsGross = VatReportHelpers.Round2(
            lineList.Sum( line => line.Quantity * line.UnitGrossPrice )
        );
        decimal surplus = VatReportHelpers.Round2( Math.Max( 0m, expenseGrossAmount - productsGross ) );
        int totalUnits = lineList.Sum( line => line.Quantity );
        return totalUnits > 0 ? surplus / totalUnits : 0m;
    }

    private static bool SupplyLineMatchesProduct(
        SaleUnit sale,
        SupplyBatchRow batch,
        IReadOnlyDictionary<string, string> variantToProduct )
    {
        string saleProductId = ResolveCatalogProductId( sale.ProductId, variantToProduct );
        if (string.IsNullOrWhiteSpace( saleProductId ))
        {
            saleProductId = sale.ProductId;
        }

        string batchProductId = ResolveCatalogProductId( batch.ProductId, variantToProduct );
        if (string.IsNullOrWhiteSpace( batchProductId ))
        {
            batchProductId = batch.ProductId;
        }

        return VatReportHelpers.ProductLinesCompatible(
            saleProductId,
            sale.VariantId,
            batchProductId,
            batch.VariantId );
    }

    private static bool IsSupplierPaymentType( string typeName ) =>
        string.Equals( typeName, ExpenseInvoiceTypeSeeder.SupplierPaymentDefaultName, StringComparison.Ordinal ) ||
        string.Equals( typeName, ExpenseInvoiceTypeSeeder.LegacySupplierPaymentName, StringComparison.Ordinal );

    private static string NormalizeProductId( string raw ) =>
        ShopifyIds.NormalizeGid( raw?.Trim() ?? string.Empty, "gid://shopify/Product/" ).Trim();

    private static string NormalizeVariantId( string raw ) =>
        ShopifyIds.NormalizeVariantId( raw?.Trim() ?? string.Empty ).Trim();

    private static string ResolvePaymentProductId( string productIdRaw, string variantIdRaw )
    {
        string productId = NormalizeProductId( productIdRaw );
        if (!string.IsNullOrWhiteSpace( productId ))
        {
            return productId;
        }

        return NormalizeVariantId( variantIdRaw );
    }

    private static string NormalizeOrderId( string raw ) =>
        ShopifyIds.NormalizeOrderId( raw?.Trim() ?? string.Empty ).Trim();

    private static bool ProductHasNamedVariants(
        string catalogProductId,
        IReadOnlySet<string> multiVariantProductIds ) =>
        !string.IsNullOrWhiteSpace( catalogProductId ) &&
        multiVariantProductIds.Contains( NormalizeProductId( catalogProductId ) );

    private static bool PaymentMatchesProduct(
        PaymentUnit payment,
        SaleUnit sale,
        IReadOnlyDictionary<string, string> variantToProduct,
        IReadOnlyDictionary<string, string> productCoreTitleById,
        IReadOnlySet<string> multiVariantProductIds )
    {
        string saleProductId = ResolveCatalogProductId( sale.ProductId, variantToProduct );
        if (string.IsNullOrWhiteSpace( saleProductId ))
        {
            saleProductId = NormalizeProductId( sale.ProductId );
        }

        string paymentProductId = ResolvePaymentProductId( payment.ProductId, payment.VariantId );
        string paymentProductIdResolved = ResolveCatalogProductId( paymentProductId, variantToProduct );
        if (string.IsNullOrWhiteSpace( paymentProductIdResolved ))
        {
            paymentProductIdResolved = paymentProductId;
        }

        string paymentCatalogProductId = string.IsNullOrWhiteSpace( payment.CatalogProductId )
            ? paymentProductIdResolved
            : payment.CatalogProductId;

        if (!ProductIdsEqual( saleProductId, paymentCatalogProductId ) &&
            !ProductIdsEqual( saleProductId, paymentProductIdResolved ))
        {
            if (!string.IsNullOrWhiteSpace( payment.CatalogProductId ))
            {
                return false;
            }

            if (ProductTitlesCompatibleLoosely( payment.ProductTitle, sale.ProductTitle ))
            {
                return true;
            }

            string saleCoreTitle = ResolveProductCoreTitle( sale.ProductId, sale.ProductTitle, productCoreTitleById );
            string paymentCoreTitle = ResolveProductCoreTitle(
                payment.ProductId,
                payment.ProductTitle,
                productCoreTitleById );
            return CoreProductTitlesOverlap( saleCoreTitle, paymentCoreTitle );
        }

        if (!ProductHasNamedVariants( saleProductId, multiVariantProductIds ))
        {
            return true;
        }

        return PaymentVariantLinesMatch(
            saleProductId,
            sale,
            paymentCatalogProductId,
            payment );
    }

    private static bool PaymentVariantLinesMatch(
        string saleCatalogProductId,
        SaleUnit sale,
        string paymentCatalogProductId,
        PaymentUnit payment )
    {
        string saleVariantId = NormalizeVariantId( sale.VariantId );
        string paymentVariantId = NormalizeVariantId( payment.VariantId );

        if (!string.IsNullOrWhiteSpace( saleVariantId ) && !string.IsNullOrWhiteSpace( paymentVariantId ))
        {
            return VatReportHelpers.ProductLineKeysEqual(
                saleCatalogProductId,
                saleVariantId,
                paymentCatalogProductId,
                paymentVariantId );
        }

        string saleTitle = ResolvePaymentMatchVariantTitle( sale.VariantTitle, sale.ProductTitle );
        string paymentTitle = ResolvePaymentMatchVariantTitle( payment.VariantTitle, payment.ProductTitle );
        if (string.IsNullOrWhiteSpace( saleTitle ) || string.IsNullOrWhiteSpace( paymentTitle ))
        {
            return false;
        }

        return string.Equals( saleTitle, paymentTitle, StringComparison.OrdinalIgnoreCase );
    }

    private static string ResolvePaymentMatchVariantTitle( string variantTitle, string productTitle )
    {
        if (!string.IsNullOrWhiteSpace( variantTitle ))
        {
            return NormalizeVariantTitle( variantTitle );
        }

        return NormalizeVariantTitle(
            VatReportHelpers.ExtractVariantTitleFromProductLineTitle( productTitle ) );
    }

    private static void EnrichPaymentUnitVariants(
        List<PaymentUnit> payments,
        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle,
        IReadOnlyDictionary<string, string> variantTitleById )
    {
        foreach (PaymentUnit payment in payments)
        {
            if (string.IsNullOrWhiteSpace( payment.VariantTitle ))
            {
                string fromTitle = VatReportHelpers.ExtractVariantTitleFromProductLineTitle( payment.ProductTitle );
                if (!string.IsNullOrWhiteSpace( fromTitle ))
                {
                    payment.VariantTitle = NormalizeVariantTitle( fromTitle );
                }
            }

            string catalogProductId = !string.IsNullOrWhiteSpace( payment.CatalogProductId )
                ? NormalizeProductId( payment.CatalogProductId )
                : NormalizeProductId( payment.ProductId );

            if (string.IsNullOrWhiteSpace( payment.VariantId ) &&
                !string.IsNullOrWhiteSpace( payment.VariantTitle ) &&
                !string.IsNullOrWhiteSpace( catalogProductId ))
            {
                string resolved = ShopifyVariantLookupService.ResolveVariantIdByProductTitle(
                    catalogProductId,
                    payment.VariantTitle,
                    variantIdByTitle );
                if (!string.IsNullOrWhiteSpace( resolved ))
                {
                    payment.VariantId = NormalizeVariantId( resolved );
                }
            }

            if (string.IsNullOrWhiteSpace( payment.VariantTitle ) &&
                !string.IsNullOrWhiteSpace( payment.VariantId ) &&
                variantTitleById.TryGetValue( NormalizeVariantId( payment.VariantId ), out string? title ) &&
                !string.IsNullOrWhiteSpace( title ))
            {
                payment.VariantTitle = NormalizeVariantTitle( title );
            }
        }
    }

    private static string ResolveProductCoreTitle(
        string productId,
        string productTitle,
        IReadOnlyDictionary<string, string> productCoreTitleById )
    {
        string normalizedProductId = NormalizeProductId( productId );
        if (!string.IsNullOrWhiteSpace( normalizedProductId ) &&
            productCoreTitleById.TryGetValue( normalizedProductId, out string? mappedCoreTitle ) &&
            !string.IsNullOrWhiteSpace( mappedCoreTitle ))
        {
            return mappedCoreTitle;
        }

        return ExtractCoreProductTitleForMatch( productTitle );
    }

    private static bool ProductIdsShareCoreTitle(
        string saleProductIdRaw,
        string paymentProductIdRaw,
        IReadOnlyDictionary<string, string> productCoreTitleById )
    {
        string saleProductId = NormalizeProductId( saleProductIdRaw );
        string paymentProductId = NormalizeProductId( paymentProductIdRaw );
        if (string.IsNullOrWhiteSpace( saleProductId ) || string.IsNullOrWhiteSpace( paymentProductId ))
        {
            return false;
        }

        if (ProductIdsEqual( saleProductId, paymentProductId ))
        {
            return true;
        }

        if (!productCoreTitleById.TryGetValue( saleProductId, out string? saleCore ) ||
            !productCoreTitleById.TryGetValue( paymentProductId, out string? paymentCore ))
        {
            return false;
        }

        return CoreProductTitlesOverlap( saleCore, paymentCore );
    }

    private static bool ProductTitlesCompatibleLoosely( string left, string right )
    {
        if (ProductTitlesEqual( left, right ))
        {
            return true;
        }

        string coreLeft = ExtractCoreProductTitleForMatch( left );
        string coreRight = ExtractCoreProductTitleForMatch( right );
        if (CoreProductTitlesOverlap( coreLeft, coreRight ))
        {
            return true;
        }

        string fullLeft = NormalizeProductTitle( left );
        string fullRight = NormalizeProductTitle( right );
        if (fullLeft.Length >= 8 && fullRight.Length >= 8)
        {
            return fullLeft.Contains( fullRight, StringComparison.OrdinalIgnoreCase ) ||
                   fullRight.Contains( fullLeft, StringComparison.OrdinalIgnoreCase );
        }

        return false;
    }

    private static bool CoreProductTitlesOverlap( string coreLeft, string coreRight )
    {
        if (coreLeft.Length < 3 || coreRight.Length < 3)
        {
            return false;
        }

        if (string.Equals( coreLeft, coreRight, StringComparison.OrdinalIgnoreCase ))
        {
            return true;
        }

        return coreLeft.Contains( coreRight, StringComparison.OrdinalIgnoreCase ) ||
               coreRight.Contains( coreLeft, StringComparison.OrdinalIgnoreCase );
    }

    private static string ExtractCoreProductTitleForMatch( string raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return string.Empty;
        }

        // Split on dash/comma in the raw title first: NormalizeProductTitle turns dashes into
        // spaces, so "Наступны прыпынак — смерць" must be split before normalization.
        string title = raw.Trim();
        foreach (string separator in new[] { " — ", " – ", " - ", "—", "–" })
        {
            int index = title.LastIndexOf( separator, StringComparison.Ordinal );
            if (index < 0)
            {
                continue;
            }

            string after = title[(index + separator.Length)..].Trim();
            if (after.Length >= 3)
            {
                title = after;
                break;
            }
        }

        int comma = title.IndexOf( ',', StringComparison.Ordinal );
        if (comma >= 0)
        {
            title = title[..comma].Trim();
        }

        return NormalizeProductTitle( title );
    }

    private static string ResolveSaleVariantTitleForPaymentMatch( SaleUnit sale )
    {
        if (!string.IsNullOrWhiteSpace( sale.VariantTitle ))
        {
            return sale.VariantTitle.Trim();
        }

        return VatReportHelpers.ExtractVariantTitleFromProductLineTitle( sale.ProductTitle );
    }

    private static string ResolvePaymentVariantTitleForPaymentMatch( PaymentUnit payment )
    {
        return VatReportHelpers.ExtractVariantTitleFromProductLineTitle( payment.ProductTitle );
    }

    private static bool PaymentVariantTitlesConflict( SaleUnit sale, PaymentUnit payment )
    {
        if (string.IsNullOrWhiteSpace( sale.VariantId ) && string.IsNullOrWhiteSpace( payment.VariantId ))
        {
            return false;
        }

        string saleProductId = NormalizeProductId( sale.ProductId );
        string paymentProductId = NormalizeProductId(
            ResolvePaymentProductId( payment.ProductId, payment.VariantId ) );
        if (ProductIdsEqual( saleProductId, paymentProductId ))
        {
            return false;
        }

        if (ProductTitlesCompatibleLoosely( payment.ProductTitle, sale.ProductTitle ))
        {
            return false;
        }

        string saleVariantTitle = ResolveSaleVariantTitleForPaymentMatch( sale );
        string paymentVariantTitle = ResolvePaymentVariantTitleForPaymentMatch( payment );
        if (string.IsNullOrWhiteSpace( saleVariantTitle ) || string.IsNullOrWhiteSpace( paymentVariantTitle ))
        {
            return false;
        }

        return !string.Equals(
            saleVariantTitle,
            paymentVariantTitle,
            StringComparison.OrdinalIgnoreCase );
    }

    private static string ResolveCatalogProductId(
        string raw,
        IReadOnlyDictionary<string, string> variantToProduct )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return string.Empty;
        }

        string variantMapped = ResolveVariantMappedProductId( raw, variantToProduct );
        if (!string.IsNullOrWhiteSpace( variantMapped ))
        {
            return variantMapped;
        }

        return NormalizeProductId( raw );
    }

    private static string ResolveVariantMappedProductId(
        string raw,
        IReadOnlyDictionary<string, string> variantToProduct )
    {
        string variantId = NormalizeVariantId( raw );
        if (string.IsNullOrWhiteSpace( variantId ))
        {
            return string.Empty;
        }

        return variantToProduct.TryGetValue( variantId, out string? productId ) ? productId : string.Empty;
    }

    private static bool ProductTitlesEqual( string left, string right )
    {
        string normalizedLeft = NormalizeProductTitle( left );
        string normalizedRight = NormalizeProductTitle( right );
        return !string.IsNullOrWhiteSpace( normalizedLeft ) &&
               string.Equals( normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase );
    }

    private static string NormalizeProductTitle( string raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return string.Empty;
        }

        System.Text.StringBuilder normalized = new();
        bool lastWasSpace = false;
        foreach (char character in raw.Trim().ToLowerInvariant())
        {
            char mapped = character switch
            {
                '«' or '»' or '„' or '“' or '”' => '"',
                _ => character
            };

            if (char.IsLetterOrDigit( mapped ) || mapped == '"')
            {
                normalized.Append( mapped );
                lastWasSpace = false;
                continue;
            }

            if (!lastWasSpace)
            {
                normalized.Append( ' ' );
                lastWasSpace = true;
            }
        }

        return normalized.ToString().Trim();
    }

    private static string NormalizeVariantTitle( string raw )
    {
        string title = (raw ?? string.Empty).Trim();
        return string.Equals( title, "Default Title", StringComparison.OrdinalIgnoreCase )
            ? string.Empty
            : title;
    }

    private static bool ProductIdsEqual( string left, string right ) =>
        string.Equals( NormalizeProductId( left ), NormalizeProductId( right ), StringComparison.OrdinalIgnoreCase );

    private sealed class SaleUnit
    {
        public int Id { get; set; }
        public string ShopifyOrderId { get; set; } = string.Empty;
        public string ReportType { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
        public string VariantId { get; set; } = string.Empty;
        public string VariantTitle { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public DateTime DateUtc { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public int? SupplierId { get; set; }
    }

    private sealed class PaymentUnit
    {
        public int Id { get; set; }
        public int ExpenseId { get; set; }
        public string CatalogProductId { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
        public string VariantId { get; set; } = string.Empty;
        public string VariantTitle { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public int Remaining { get; set; }
        public decimal UnitGrossPrice { get; set; }
        public DateTime DateUtc { get; set; }
        public int? SupplierId { get; set; }
    }

    private sealed class ExpensePeriodGrossRow
    {
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public decimal GrossAmount { get; set; }
        public string TypeName { get; set; } = string.Empty;
    }

    private sealed class ExpenseGrossRow
    {
        public decimal GrossAmount { get; set; }
        public string TypeName { get; set; } = string.Empty;
    }

    private sealed class RowSaleRow
    {
        public int Id { get; set; }
        public string ShopifyOrderId { get; set; } = string.Empty;
        public string ReportType { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
        public string VariantId { get; set; } = string.Empty;
        public string VariantTitle { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public DateTime OrderDateUtc { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
    }

    private sealed class CashSaleRow
    {
        public int Id { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public string VariantId { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
    }

    private sealed class PaymentProductRow
    {
        public int ExpenseId { get; set; }
        public decimal ExpenseGrossAmount { get; set; }
        public int Id { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public string VariantId { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitGrossPrice { get; set; }
        public DateTime DateUtc { get; set; }
        public int? SupplierId { get; set; }
    }

    private sealed class SupplyBatchRow
    {
        public int SupplierId { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public string VariantId { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public int Remaining { get; set; }
        public DateOnly SupplyDate { get; set; }
    }

    private sealed class SupplyEventRow
    {
        public int SupplyId { get; set; }
        public int RowId { get; set; }
        public int SupplierId { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public string VariantId { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public DateOnly SupplyDate { get; set; }
    }

    private sealed class TimelineEntry
    {
        public DateTime DateUtc { get; set; }
        public int KindOrder { get; set; }
        public int Sequence { get; set; }
        public SupplyEventRow? Supply { get; set; }
        public SaleUnit? Sale { get; set; }
    }

    private sealed class SupplyPriceRow
    {
        public int SupplierId { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public decimal SupplierPrice { get; set; }
        public DateOnly SupplyDate { get; set; }
        public int SupplyId { get; set; }
        public int RowId { get; set; }
    }

    private sealed class VariantTitleRow
    {
        public string VariantId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
    }

    private sealed class OrderLineVariantRow
    {
        public int RowItemId { get; set; }
        public string OrderId { get; set; } = string.Empty;
        public DateTime OrderDateUtc { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public string VariantId { get; set; } = string.Empty;
        public string VariantTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }

    private sealed class UnpaidAccumulator
    {
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ShopifyVariantId { get; set; } = string.Empty;
        public string ShopifyVariantTitle { get; set; } = string.Empty;
        public string ShopifyOrderId { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
        public int? SupplierId { get; set; }
        public int Quantity { get; set; }
        public decimal UnitSupplyPrice { get; set; }
        public DateTime EarliestSaleOrderDateUtc { get; set; }
        public int? SourceSaleRowItemId { get; set; }
    }

    private sealed class ExpenseLabelRow
    {
        public int Id { get; set; }
        public string InvoiceNumber { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
    }

    private sealed class ManualAllocationPool
    {
        public int SalePeriodYear { get; set; }
        public int SalePeriodMonth { get; set; }
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ShopifyVariantId { get; set; } = string.Empty;
        public int SupplierId { get; set; }
        public int VatReportExpenseId { get; set; }
        public int Remaining { get; set; }
    }

    private sealed class VariantProductTitleRow
    {
        public string ProductId { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
    }

    private sealed class VariantProductRow
    {
        public string VariantId { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
    }

    private sealed class SaleCostAllocationResult
    {
        public Dictionary<(int Year, int Month), decimal> CogsByPeriod { get; set; } = new();
        public Dictionary<(int Year, int Month), List<UnpaidAccumulator>> UnpaidByPeriod { get; set; } = new();
    }
}

public sealed class QuantityLineKeyMaps
{
    public IReadOnlyDictionary<string, string> VariantToProduct { get; init; } =
        new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

    public IReadOnlyDictionary<string, Dictionary<string, string>> VariantIdByTitle { get; init; } =
        new Dictionary<string, Dictionary<string, string>>( StringComparer.OrdinalIgnoreCase );

    public IReadOnlySet<string> MultiVariantProductIds { get; init; } =
        new HashSet<string>( StringComparer.OrdinalIgnoreCase );
}
