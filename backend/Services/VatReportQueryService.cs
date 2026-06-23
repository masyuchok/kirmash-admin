using backend.Data;
using backend.Models;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public class VatReportQueryService
{
    private readonly AppDbContext _db;
    private readonly ShopifyOrderFetchService _shopifyOrders;
    private readonly VatReportProfitService _profitService;

    public VatReportQueryService(
        AppDbContext db,
        ShopifyOrderFetchService shopifyOrders,
        VatReportProfitService profitService )
    {
        _db = db;
        _shopifyOrders = shopifyOrders;
        _profitService = profitService;
    }

    public async Task<List<VatReportListItem>> GetAllAsync()
    {
        return await _db.VatReports
            .AsNoTracking()
            .OrderByDescending( r => r.PeriodYear )
            .ThenByDescending( r => r.PeriodMonth )
            .ThenByDescending( r => r.Id )
            .Select( r => new VatReportListItem
            {
                Id = r.Id,
                PeriodYear = r.PeriodYear,
                PeriodMonth = r.PeriodMonth,
                Type = r.Type,
                Name = r.Name,
                Document = r.Document,
                Vat = r.Vat,
                VatCredit = r.VatCredit,
                VatToPay = r.VatToPay,
                Documents = r.Documents.ToList(),
                ShopifyOrderIds = r.ShopifyOrderIds.ToList(),
                IsLocked = r.IsLocked
            } )
            .ToListAsync();
    }

    public async Task<List<VatReportPeriodListItem>> GetPeriodSummariesAsync()
    {
        List<VatReportListItem> allReports = await GetAllAsync();
        IEnumerable<IGrouping<(int PeriodYear, int PeriodMonth), VatReportListItem>> periodGroups = allReports
            .GroupBy( r => (r.PeriodYear, r.PeriodMonth) )
            .OrderByDescending( g => g.Key.PeriodYear )
            .ThenByDescending( g => g.Key.PeriodMonth );

        Dictionary<(int Year, int Month), PeriodRollup> rollups = await LoadPeriodRollupsAsync();
        Dictionary<(int Year, int Month), decimal> cogsByPeriod =
            await _profitService.GetCogsByPeriodCachedAsync();
        Dictionary<(int Year, int Month), decimal> nonSupplierByPeriod =
            await _profitService.GetNonSupplierExpenseGrossByPeriodCachedAsync();
        Dictionary<(int Year, int Month), decimal> financeByPeriod =
            await _profitService.GetFinancePaymentsByPeriodCachedAsync();

        List<VatReportPeriodListItem> result = new();
        foreach (IGrouping<(int PeriodYear, int PeriodMonth), VatReportListItem> group in periodGroups)
        {
            List<VatReportListItem> periodReports = group
                .OrderByDescending( r => r.Id )
                .ToList();
            VatReportListItem? polandReport = periodReports.FirstOrDefault( r =>
                string.Equals( r.Type, VatReportType.Poland, StringComparison.OrdinalIgnoreCase )
            );
            int baseReportId = polandReport?.Id ?? periodReports[0].Id;

            (int Year, int Month) periodKey = (group.Key.PeriodYear, group.Key.PeriodMonth);
            if (!rollups.TryGetValue( periodKey, out PeriodRollup? rollup ))
            {
                rollup = new PeriodRollup();
            }
            List<VatReportDetailsSummaryRow> summaryRows =
            [
                new VatReportDetailsSummaryRow
                {
                    Type = VatReportType.Poland,
                    GrossAmount = rollup.PolandGross
                },
                new VatReportDetailsSummaryRow
                {
                    Type = VatReportType.Foreign,
                    GrossAmount = rollup.ForeignGross
                },
                new VatReportDetailsSummaryRow
                {
                    Type = VatReportType.Cash,
                    GrossAmount = rollup.CashGross
                }
            ];

            result.Add(
                new VatReportPeriodListItem
                {
                    PeriodYear = group.Key.PeriodYear,
                    PeriodMonth = group.Key.PeriodMonth,
                    TotalVat = rollup.TotalVat,
                    Profit = _profitService.ComputePeriodProfit(
                        group.Key.PeriodYear,
                        group.Key.PeriodMonth,
                        summaryRows,
                        cogsByPeriod,
                        nonSupplierByPeriod,
                        financeByPeriod ),
                    IsLocked = periodReports.Any( r => r.IsLocked ),
                    PrimaryReportId = baseReportId,
                    Reports = periodReports
                }
            );
        }

        return result;
    }

    private async Task<Dictionary<(int Year, int Month), PeriodRollup>> LoadPeriodRollupsAsync()
    {
        List<ReportPeriodAggregateRow> rowAggregates = await _db.VatReportRows
            .AsNoTracking()
            .Select( r => new ReportPeriodAggregateRow
            {
                PeriodYear = r.VatReport.PeriodYear,
                PeriodMonth = r.VatReport.PeriodMonth,
                ReportType = r.VatReport.Type,
                VatAmount = r.VatAmount,
                GrossAmount = r.GrossAmount
            } )
            .ToListAsync();

        List<CashPeriodAggregateRow> cashAggregates = await _db.VatReportCashSales
            .AsNoTracking()
            .GroupBy( x => new { x.VatReport.PeriodYear, x.VatReport.PeriodMonth } )
            .Select( g => new CashPeriodAggregateRow
            {
                PeriodYear = g.Key.PeriodYear,
                PeriodMonth = g.Key.PeriodMonth,
                GrossAmount = g.Sum( x => x.GrossAmount )
            } )
            .ToListAsync();

        List<ExpensePeriodVatRow> expenseVatRows = await _db.VatReportExpenses
            .AsNoTracking()
            .Where( e => e.IncludeVatInTotal )
            .GroupBy( e => new { e.VatReport.PeriodYear, e.VatReport.PeriodMonth } )
            .Select( g => new ExpensePeriodVatRow
            {
                PeriodYear = g.Key.PeriodYear,
                PeriodMonth = g.Key.PeriodMonth,
                VatAmount = g.Sum( e => e.VatAmount )
            } )
            .ToListAsync();

        Dictionary<(int Year, int Month), PeriodRollup> rollups = new();
        foreach (ReportPeriodAggregateRow row in rowAggregates)
        {
            (int Year, int Month) key = (row.PeriodYear, row.PeriodMonth);
            if (!rollups.TryGetValue( key, out PeriodRollup? rollup ))
            {
                rollup = new PeriodRollup();
                rollups[key] = rollup;
            }

            if (string.Equals( row.ReportType, VatReportType.Poland, StringComparison.OrdinalIgnoreCase ))
            {
                rollup.PolandGross += row.GrossAmount;
                rollup.PolandVat += row.VatAmount;
            }
            else if (string.Equals( row.ReportType, VatReportType.Foreign, StringComparison.OrdinalIgnoreCase ))
            {
                rollup.ForeignGross += row.GrossAmount;
                rollup.ForeignVat += row.VatAmount;
            }
        }

        foreach (CashPeriodAggregateRow cash in cashAggregates)
        {
            (int Year, int Month) key = (cash.PeriodYear, cash.PeriodMonth);
            if (!rollups.TryGetValue( key, out PeriodRollup? rollup ))
            {
                rollup = new PeriodRollup();
                rollups[key] = rollup;
            }

            rollup.CashGross += cash.GrossAmount;
        }

        foreach (ExpensePeriodVatRow expense in expenseVatRows)
        {
            (int Year, int Month) key = (expense.PeriodYear, expense.PeriodMonth);
            if (!rollups.TryGetValue( key, out PeriodRollup? rollup ))
            {
                rollup = new PeriodRollup();
                rollups[key] = rollup;
            }

            rollup.ExpenseVatForTotal += expense.VatAmount;
        }

        foreach (PeriodRollup rollup in rollups.Values)
        {
            rollup.PolandGross = VatReportHelpers.Round2( rollup.PolandGross );
            rollup.ForeignGross = VatReportHelpers.Round2( rollup.ForeignGross );
            rollup.CashGross = VatReportHelpers.Round2( rollup.CashGross );
            rollup.PolandVat = VatReportHelpers.Round2( rollup.PolandVat );
            rollup.ForeignVat = VatReportHelpers.Round2( rollup.ForeignVat );
            rollup.ExpenseVatForTotal = VatReportHelpers.Round2( rollup.ExpenseVatForTotal );
            rollup.TotalVat = VatReportHelpers.Round2( rollup.PolandVat + rollup.ForeignVat - rollup.ExpenseVatForTotal );
        }

        return rollups;
    }

    private sealed class PeriodRollup
    {
        public decimal PolandGross { get; set; }
        public decimal ForeignGross { get; set; }
        public decimal CashGross { get; set; }
        public decimal PolandVat { get; set; }
        public decimal ForeignVat { get; set; }
        public decimal ExpenseVatForTotal { get; set; }
        public decimal TotalVat { get; set; }
    }

    private sealed class ReportPeriodAggregateRow
    {
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public string ReportType { get; set; } = string.Empty;
        public decimal VatAmount { get; set; }
        public decimal GrossAmount { get; set; }
    }

    private sealed class CashPeriodAggregateRow
    {
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public decimal GrossAmount { get; set; }
    }

    private sealed class ExpensePeriodVatRow
    {
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public decimal VatAmount { get; set; }
    }

    public async Task<VatReportDetailsResponse> GetDetailsAsync( int id )
    {
        var header = await _db.VatReports
            .AsNoTracking()
            .Where( r => r.Id == id )
            .Select( r => new { r.Id, r.PeriodYear, r.PeriodMonth, r.Type, r.Vat, r.IsLocked } )
            .FirstOrDefaultAsync();
        if (header is null)
        {
            throw new InvalidOperationException( "Справаздача не знойдзена." );
        }

        List<ReportRowData> reportRows = await LoadReportRowsAsync( id );
        List<ReportExpenseData> expenses = await LoadReportExpensesAsync( id );
        List<ReportCashSaleData> cashSales = await LoadReportCashSalesAsync( id );

        List<VatReportDetailsSummaryRow> rows = string.Equals(
            header.Type,
            VatReportType.Poland,
            StringComparison.OrdinalIgnoreCase )
            ? BuildPolandSummaryRows( reportRows, cashSales, expenses )
            : await BuildForeignSummaryRowsAsync( header.Type, reportRows );

        bool periodLocked = await _db.VatReports
            .AsNoTracking()
            .AnyAsync( r =>
                r.PeriodYear == header.PeriodYear &&
                r.PeriodMonth == header.PeriodMonth &&
                r.IsLocked
            );

        return new VatReportDetailsResponse
        {
            Id = header.Id,
            PeriodYear = header.PeriodYear,
            PeriodMonth = header.PeriodMonth,
            IsLocked = periodLocked,
            Vat = ComputeTotalVatFromSummaryRows( rows ),
            Profit = await _profitService.ComputePeriodProfitAsync(
                header.PeriodYear,
                header.PeriodMonth,
                rows ),
            Rows = rows
        };
    }

    public async Task<VatReportCombinedDetailsResponse> GetCombinedDetailsAsync( int baseReportId )
    {
        VatReportDetailsResponse baseDetails = await GetDetailsAsync( baseReportId );
        bool baseIsPoland = baseDetails.Rows.Any( r => r.Type == VatReportType.Poland );
        string siblingType = baseIsPoland ? VatReportType.Foreign : VatReportType.Poland;

        int? siblingId = await _db.VatReports
            .AsNoTracking()
            .Where( r =>
                r.PeriodYear == baseDetails.PeriodYear &&
                r.PeriodMonth == baseDetails.PeriodMonth &&
                r.Type == siblingType )
            .Select( r => (int?)r.Id )
            .FirstOrDefaultAsync();

        if (!siblingId.HasValue)
        {
            List<VatReportDetailsSummaryRow> rowsWithUnpaid =
            [
                ..baseDetails.Rows,
                await BuildUnpaidSummaryRowAsync( baseDetails.PeriodYear, baseDetails.PeriodMonth )
            ];
            return new VatReportCombinedDetailsResponse
            {
                Details = new VatReportDetailsResponse
                {
                    Id = baseDetails.Id,
                    PeriodYear = baseDetails.PeriodYear,
                    PeriodMonth = baseDetails.PeriodMonth,
                    IsLocked = baseDetails.IsLocked,
                    Vat = baseDetails.Vat,
                    Profit = baseDetails.Profit,
                    Rows = rowsWithUnpaid
                },
                ForeignRows = baseDetails.Rows
                    .Where( r => r.Type == VatReportType.Foreign )
                    .ToList()
            };
        }

        VatReportDetailsResponse siblingDetails = await GetDetailsAsync( siblingId.Value );
        VatReportDetailsResponse polandDetails = baseIsPoland ? baseDetails : siblingDetails;
        VatReportDetailsResponse foreignDetails = baseIsPoland ? siblingDetails : baseDetails;

        List<VatReportDetailsSummaryRow> polandSummaryRows = polandDetails.Rows
            .Where( r => r.Type == VatReportType.Poland )
            .ToList();
        List<VatReportDetailsSummaryRow> cashSummaryRows = polandDetails.Rows
            .Where( r => r.Type == VatReportType.Cash )
            .ToList();
        List<VatReportDetailsSummaryRow> expenseSummaryRows = polandDetails.Rows
            .Where( r => r.Type == VatReportType.Expense )
            .ToList();
        List<VatReportDetailsSummaryRow> foreignRows = foreignDetails.Rows
            .Where( r => r.Type == VatReportType.Foreign )
            .ToList();

        decimal foreignSummaryVat = foreignRows.Sum( row => row.Vat );
        decimal foreignSummaryNet = foreignRows.Sum( row => row.NetAmount );
        decimal foreignSummaryGross = foreignRows.Sum( row => row.GrossAmount );

        List<VatReportDetailsSummaryRow> combinedRows =
        [
            ..polandSummaryRows,
            new VatReportDetailsSummaryRow
            {
                Type = VatReportType.Foreign,
                Name = "Замежжа",
                ShopifyOrderId = "foreign-summary",
                Vat = VatReportHelpers.Round2( foreignSummaryVat ),
                NetAmount = VatReportHelpers.Round2( foreignSummaryNet ),
                GrossAmount = VatReportHelpers.Round2( foreignSummaryGross ),
                PolandRows = []
            },
            ..cashSummaryRows,
            ..expenseSummaryRows,
            await BuildUnpaidSummaryRowAsync( polandDetails.PeriodYear, polandDetails.PeriodMonth )
        ];

        bool periodLocked = await _db.VatReports
            .AsNoTracking()
            .AnyAsync( r =>
                r.PeriodYear == polandDetails.PeriodYear &&
                r.PeriodMonth == polandDetails.PeriodMonth &&
                r.IsLocked
            );

        return new VatReportCombinedDetailsResponse
        {
            ForeignRows = foreignRows,
            Details = new VatReportDetailsResponse
            {
                Id = polandDetails.Id,
                PeriodYear = polandDetails.PeriodYear,
                PeriodMonth = polandDetails.PeriodMonth,
                IsLocked = periodLocked,
                Vat = ComputeTotalVatFromSummaryRows( combinedRows ),
                Profit = await _profitService.ComputePeriodProfitAsync(
                    polandDetails.PeriodYear,
                    polandDetails.PeriodMonth,
                    combinedRows ),
                Rows = combinedRows
            }
        };
    }

    private async Task<List<ReportRowData>> LoadReportRowsAsync( int reportId )
    {
        return await _db.VatReportRows
            .AsNoTracking()
            .Where( r => r.VatReportId == reportId )
            .Select( r => new ReportRowData
            {
                Id = r.Id,
                ShopifyOrderId = r.ShopifyOrderId,
                OrderNumber = r.OrderNumber,
                OrderDateUtc = r.OrderDateUtc,
                VatRatePercent = r.VatRatePercent,
                GrossAmount = r.GrossAmount,
                VatAmount = r.VatAmount,
                NetAmount = r.NetAmount,
                ShippingGrossAmount = r.ShippingGrossAmount,
                ShippingNetAmount = r.ShippingNetAmount,
                InvoiceFileName = r.InvoiceFileName,
                Items = r.Items
                    .Select( i => new ReportRowItemData
                    {
                        Id = i.Id,
                        ShopifyVariantId = i.ShopifyVariantId,
                        VariantTitle = i.VariantTitle,
                        ProductTitle = i.ProductTitle,
                        ProductType = i.ProductType,
                        Quantity = i.Quantity,
                        UnitPrice = i.UnitPrice,
                        GrossAmount = i.GrossAmount,
                        AssignedVatRatePercent = i.AssignedVatRatePercent,
                        AssignmentReason = i.AssignmentReason
                    } )
                    .ToList()
            } )
            .ToListAsync();
    }

    private async Task<List<ReportExpenseData>> LoadReportExpensesAsync( int reportId )
    {
        return await _db.VatReportExpenses
            .AsNoTracking()
            .Where( e => e.VatReportId == reportId )
            .Select( e => new ReportExpenseData
            {
                Id = e.Id,
                GrossAmount = e.GrossAmount,
                VatAmount = e.VatAmount,
                NetAmount = e.NetAmount,
                ExpenseDateUtc = e.ExpenseDateUtc,
                Comment = e.Comment ?? string.Empty,
                InvoiceNumber = e.InvoiceNumber,
                IsPaid = e.IsPaid,
                IsByProsvet = e.IsByProsvet,
                IncludeVatInTotal = e.IncludeVatInTotal,
                ExpenseInvoiceTypeId = e.ExpenseInvoiceTypeId,
                ExpenseInvoiceTypeName = e.ExpenseInvoiceType.Name,
                InvoiceFileName = e.InvoiceFileName,
                CreatedAtUtc = e.CreatedAtUtc,
                SupplierId = e.SupplierId,
                SupplierName = e.Supplier != null ? e.Supplier.Name : string.Empty,
                Products = e.Products
                    .Select( p => new ReportExpenseProductData
                    {
                        Id = p.Id,
                        ShopifyProductId = p.ShopifyProductId,
                        ShopifyVariantId = p.ShopifyVariantId,
                        ProductTitle = p.ProductTitle,
                        Quantity = p.Quantity,
                        UnitGrossPrice = p.UnitGrossPrice
                    } )
                    .ToList()
            } )
            .ToListAsync();
    }

    private async Task<List<ReportCashSaleData>> LoadReportCashSalesAsync( int reportId )
    {
        return await _db.VatReportCashSales
            .AsNoTracking()
            .Where( x => x.VatReportId == reportId )
            .Select( x => new ReportCashSaleData
            {
                Id = x.Id,
                ShopifyProductId = x.ShopifyProductId,
                ShopifyVariantId = x.ShopifyVariantId,
                ProductTitle = x.ProductTitle,
                Quantity = x.Quantity,
                UnitPrice = x.UnitPrice,
                GrossAmount = x.GrossAmount,
                CreatedAtUtc = x.CreatedAtUtc
            } )
            .ToListAsync();
    }

    private async Task<VatReportDetailsSummaryRow> BuildUnpaidSummaryRowAsync( int periodYear, int periodMonth )
    {
        List<VatReportUnpaidProductRow> unpaidProducts =
            await _profitService.GetUnpaidProductsForPeriodAsync( periodYear, periodMonth );
        decimal estimatedCogs = VatReportHelpers.Round2(
            unpaidProducts.Where( row => !row.IsManuallyLinked ).Sum( row => row.EstimatedCogs )
        );

        return new VatReportDetailsSummaryRow
        {
            Type = VatReportType.Unpaid,
            Name = "Неаплачанае",
            ShopifyOrderId = "unpaid-summary",
            Vat = 0m,
            GrossAmount = estimatedCogs,
            NetAmount = estimatedCogs,
            UnpaidProductRows = unpaidProducts
        };
    }

    private static decimal ComputeTotalVatFromSummaryRows( IEnumerable<VatReportDetailsSummaryRow> rows )
    {
        decimal polandVat = rows.Where( r => r.Type == VatReportType.Poland ).Sum( r => r.Vat );
        decimal foreignVat = rows.Where( r => r.Type == VatReportType.Foreign ).Sum( r => r.Vat );
        decimal expenseVat = SumExpenseVatForTotal( rows );
        return VatReportHelpers.Round2( polandVat + foreignVat - expenseVat );
    }

    private static decimal SumExpenseVatForTotal( IEnumerable<VatReportDetailsSummaryRow> rows )
    {
        VatReportDetailsSummaryRow? expenseRow = rows.FirstOrDefault( r => r.Type == VatReportType.Expense );
        if (expenseRow?.ExpenseRows is { Count: > 0 })
        {
            return VatReportHelpers.Round2(
                expenseRow.ExpenseRows.Where( e => e.IncludeVatInTotal ).Sum( e => e.VatAmount ) );
        }

        return rows.Where( r => r.Type == VatReportType.Expense ).Sum( r => r.Vat );
    }

    private static decimal ComputeProfitFromSummaryRows( IEnumerable<VatReportDetailsSummaryRow> rows )
    {
        // Legacy fallback; profit is computed asynchronously via VatReportProfitService.
        decimal polandGross = rows.Where( r => r.Type == VatReportType.Poland ).Sum( r => r.GrossAmount );
        decimal foreignGross = rows.Where( r => r.Type == VatReportType.Foreign ).Sum( r => r.GrossAmount );
        decimal cashGross = rows.Where( r => r.Type == VatReportType.Cash ).Sum( r => r.GrossAmount );
        decimal expenseGross = rows.Where( r => r.Type == VatReportType.Expense ).Sum( r => r.GrossAmount );
        return VatReportHelpers.Round2( polandGross + foreignGross + cashGross - expenseGross );
    }

    private static List<VatReportDetailsSummaryRow> BuildPolandSummaryRows(
        List<ReportRowData> reportRows,
        List<ReportCashSaleData> cashSales,
        List<ReportExpenseData> expenses )
    {
        List<VatReportDetailsSummaryRow> rows =
        [
            new VatReportDetailsSummaryRow
            {
                Type = VatReportType.Poland,
                Name = "Польшча",
                ShopifyOrderId = "poland",
                GrossAmount = VatReportHelpers.Round2( reportRows.Sum( x => x.GrossAmount ) ),
                NetAmount = VatReportHelpers.Round2( reportRows.Sum( x => x.NetAmount ) ),
                Vat = VatReportHelpers.Round2( reportRows.Sum( x => x.VatAmount ) ),
                PolandRows = reportRows
                    .OrderByDescending( x => x.OrderDateUtc )
                    .ThenBy( x => x.OrderNumber )
                    .ThenBy( x => x.VatRatePercent )
                    .Select( MapPolandRow )
                    .ToList()
            },
            BuildCashSummaryRow( cashSales ),
            BuildExpenseSummaryRow( expenses )
        ];

        return rows;
    }

    private static VatReportDetailsSummaryRow BuildExpenseSummaryRow( List<ReportExpenseData> expenses )
    {
        decimal expenseVat = VatReportHelpers.Round2( expenses.Sum( x => x.VatAmount ) );
        decimal expenseGross = VatReportHelpers.Round2( expenses.Sum( x => x.GrossAmount ) );
        return new VatReportDetailsSummaryRow
        {
            Type = VatReportType.Expense,
            Name = "Расход",
            ShopifyOrderId = "expense-summary",
            Vat = expenseVat,
            GrossAmount = expenseGross,
            NetAmount = VatReportHelpers.Round2( expenseGross - expenseVat ),
            ExpenseRows = expenses
                .OrderByDescending( x => x.CreatedAtUtc )
                .Select( MapExpenseRow )
                .ToList()
        };
    }

    private static VatReportDetailsSummaryRow BuildCashSummaryRow( List<ReportCashSaleData> cashSales )
    {
        decimal gross = VatReportHelpers.Round2( cashSales.Sum( x => x.GrossAmount ) );
        return new VatReportDetailsSummaryRow
        {
            Type = VatReportType.Cash,
            Name = "Наяўнымі",
            ShopifyOrderId = "cash-summary",
            Vat = 0m,
            GrossAmount = gross,
            NetAmount = gross,
            CashSaleRows = cashSales
                .OrderByDescending( x => x.CreatedAtUtc )
                .Select( MapCashSaleRow )
                .ToList()
        };
    }

    private static VatReportCashSaleRow MapCashSaleRow( ReportCashSaleData sale ) =>
        new()
        {
            Id = sale.Id,
            ShopifyProductId = sale.ShopifyProductId,
            ShopifyVariantId = sale.ShopifyVariantId,
            ProductTitle = sale.ProductTitle,
            Quantity = sale.Quantity,
            UnitPrice = sale.UnitPrice,
            GrossAmount = sale.GrossAmount,
            CreatedAtUtc = sale.CreatedAtUtc
        };

    private async Task<List<VatReportDetailsSummaryRow>> BuildForeignSummaryRowsAsync(
        string reportType,
        List<ReportRowData> reportRows )
    {
        Dictionary<string, ForeignDeliveryInfo> deliveryByOrderId = new( StringComparer.OrdinalIgnoreCase );
        List<string> orderIds = reportRows
            .Select( r => r.ShopifyOrderId )
            .Where( idValue => !string.IsNullOrWhiteSpace( idValue ) )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .ToList();

        if (orderIds.Count > 0)
        {
            deliveryByOrderId = await _shopifyOrders.FetchForeignDeliveryInfoAsync( orderIds );
        }

        return reportRows
            .GroupBy( r => r.ShopifyOrderId )
            .Select( g =>
            {
                ForeignDeliveryInfo? info = null;
                deliveryByOrderId.TryGetValue( g.Key, out info );
                ReportRowData first = g.First();
                (string parsedOrderNumber, string parsedDeliveryName, string parsedDeliveryAddress, string parsedCountryCode) =
                    VatReportHelpers.ParseOrderNumberAndContact( first.OrderNumber );
                string shippingCountryCode = !string.IsNullOrWhiteSpace( info?.ShippingCountryCode )
                    ? info!.ShippingCountryCode
                    : parsedCountryCode;
                string billingCountryCode = !string.IsNullOrWhiteSpace( info?.BillingCountryCode )
                    ? info!.BillingCountryCode
                    : parsedCountryCode;
                return new VatReportDetailsSummaryRow
                {
                    Type = reportType,
                    Name = !string.IsNullOrWhiteSpace( parsedOrderNumber )
                        ? parsedOrderNumber
                        : (first.OrderNumber ?? g.Key),
                    ShopifyOrderId = g.Key,
                    OrderDateUtc = g.Min( x => x.OrderDateUtc ),
                    DeliveryName = !string.IsNullOrWhiteSpace( parsedDeliveryName )
                        ? parsedDeliveryName
                        : (info?.Name ?? string.Empty),
                    DeliveryAddress = !string.IsNullOrWhiteSpace( info?.ShippingAddress )
                        ? info!.ShippingAddress
                        : (!string.IsNullOrWhiteSpace( parsedDeliveryAddress )
                            ? parsedDeliveryAddress
                            : (info?.BillingAddress ?? string.Empty)),
                    ShippingAddress = !string.IsNullOrWhiteSpace( info?.ShippingAddress )
                        ? info!.ShippingAddress
                        : parsedDeliveryAddress,
                    BillingAddress = !string.IsNullOrWhiteSpace( info?.BillingAddress )
                        ? info!.BillingAddress
                        : string.Empty,
                    ShippingCountryCode = shippingCountryCode,
                    BillingCountryCode = billingCountryCode,
                    GrossAmount = VatReportHelpers.Round2( g.Sum( x => x.GrossAmount ) ),
                    Vat = VatReportHelpers.Round2( g.Sum( x => x.VatAmount ) ),
                    NetAmount = VatReportHelpers.Round2( g.Sum( x => x.NetAmount ) ),
                    PolandRows = g
                        .OrderBy( x => x.VatRatePercent )
                        .Select( MapPolandRow )
                        .ToList()
                };
            } )
            .OrderByDescending( x => x.Vat )
            .ToList();
    }

    private static VatReportDetailsPolandRow MapPolandRow( ReportRowData row ) =>
        new()
        {
            Id = row.Id,
            OrderNumber = row.OrderNumber,
            OrderDateUtc = row.OrderDateUtc,
            VatRatePercent = row.VatRatePercent,
            GrossAmount = row.GrossAmount,
            VatAmount = row.VatAmount,
            NetAmount = row.NetAmount,
            ShippingGrossAmount = row.ShippingGrossAmount,
            ShippingNetAmount = row.ShippingNetAmount,
            InvoiceFileName = row.InvoiceFileName,
            Items = row.Items
                .Select( i => new VatReportDetailsPolandItem
                {
                    Id = i.Id,
                    ShopifyVariantId = i.ShopifyVariantId,
                    VariantTitle = i.VariantTitle,
                    ProductTitle = i.ProductTitle,
                    ProductType = i.ProductType,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice,
                    GrossAmount = i.GrossAmount,
                    AssignedVatRatePercent = i.AssignedVatRatePercent,
                    AssignmentReason = i.AssignmentReason
                } )
                .ToList()
        };

    private static VatReportExpenseRow MapExpenseRow( ReportExpenseData expense ) =>
        new()
        {
            Id = expense.Id,
            GrossAmount = expense.GrossAmount,
            VatAmount = expense.VatAmount,
            NetAmount = expense.NetAmount,
            ExpenseDateUtc = expense.ExpenseDateUtc,
            Comment = expense.Comment,
            InvoiceNumber = expense.InvoiceNumber,
            IsPaid = expense.IsPaid,
            IsByProsvet = expense.IsByProsvet,
            IncludeVatInTotal = expense.IncludeVatInTotal,
            ExpenseInvoiceTypeId = expense.ExpenseInvoiceTypeId,
            ExpenseInvoiceTypeName = expense.ExpenseInvoiceTypeName,
            InvoiceFileName = expense.InvoiceFileName,
            CreatedAtUtc = expense.CreatedAtUtc,
            SupplierId = expense.SupplierId,
            SupplierName = expense.SupplierName,
            Products = expense.Products
                .Select( p => new VatReportExpenseProductRow
                {
                    Id = p.Id,
                    ShopifyProductId = p.ShopifyProductId,
                    ShopifyVariantId = p.ShopifyVariantId,
                    ProductTitle = p.ProductTitle,
                    Quantity = p.Quantity,
                    UnitGrossPrice = p.UnitGrossPrice
                } )
                .ToList()
        };

    private sealed class ReportRowData
    {
        public int Id { get; set; }
        public string ShopifyOrderId { get; set; } = string.Empty;
        public string OrderNumber { get; set; } = string.Empty;
        public DateTime OrderDateUtc { get; set; }
        public decimal VatRatePercent { get; set; }
        public decimal GrossAmount { get; set; }
        public decimal VatAmount { get; set; }
        public decimal NetAmount { get; set; }
        public decimal ShippingGrossAmount { get; set; }
        public decimal ShippingNetAmount { get; set; }
        public string InvoiceFileName { get; set; } = string.Empty;
        public List<ReportRowItemData> Items { get; set; } = new();
    }

    private sealed class ReportRowItemData
    {
        public int Id { get; set; }
        public string ShopifyVariantId { get; set; } = string.Empty;
        public string VariantTitle { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
        public string ProductType { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal GrossAmount { get; set; }
        public decimal AssignedVatRatePercent { get; set; }
        public string AssignmentReason { get; set; } = string.Empty;
    }

    private sealed class ReportExpenseData
    {
        public int Id { get; set; }
        public decimal GrossAmount { get; set; }
        public decimal VatAmount { get; set; }
        public decimal NetAmount { get; set; }
        public DateTime ExpenseDateUtc { get; set; }
        public string Comment { get; set; } = string.Empty;
        public string InvoiceNumber { get; set; } = string.Empty;
        public bool IsPaid { get; set; }
        public bool IsByProsvet { get; set; }
        public bool IncludeVatInTotal { get; set; }
        public int ExpenseInvoiceTypeId { get; set; }
        public string ExpenseInvoiceTypeName { get; set; } = string.Empty;
        public string InvoiceFileName { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public int? SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public List<ReportExpenseProductData> Products { get; set; } = new();
    }

    private sealed class ReportExpenseProductData
    {
        public int Id { get; set; }
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ShopifyVariantId { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitGrossPrice { get; set; }
    }

    private sealed class ReportCashSaleData
    {
        public int Id { get; set; }
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ShopifyVariantId { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal GrossAmount { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
