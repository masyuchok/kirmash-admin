using backend.Data;
using backend.Models;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public class VatReportQueryService
{
    private readonly AppDbContext _db;
    private readonly ShopifyOrderFetchService _shopifyOrders;

    public VatReportQueryService( AppDbContext db, ShopifyOrderFetchService shopifyOrders )
    {
        _db = db;
        _shopifyOrders = shopifyOrders;
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
                ShopifyOrderIds = r.ShopifyOrderIds.ToList()
            } )
            .ToListAsync();
    }

    public async Task<VatReportDetailsResponse> GetDetailsAsync( int id )
    {
        var header = await _db.VatReports
            .AsNoTracking()
            .Where( r => r.Id == id )
            .Select( r => new { r.Id, r.PeriodYear, r.PeriodMonth, r.Type, r.Vat } )
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

        return new VatReportDetailsResponse
        {
            Id = header.Id,
            PeriodYear = header.PeriodYear,
            PeriodMonth = header.PeriodMonth,
            Vat = header.Vat,
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
            return new VatReportCombinedDetailsResponse
            {
                Details = baseDetails,
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

        return new VatReportCombinedDetailsResponse
        {
            ForeignRows = foreignRows,
            Details = new VatReportDetailsResponse
            {
                Id = polandDetails.Id,
                PeriodYear = polandDetails.PeriodYear,
                PeriodMonth = polandDetails.PeriodMonth,
                Vat = VatReportHelpers.Round2( polandDetails.Vat + foreignDetails.Vat ),
                Rows =
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
                    ..expenseSummaryRows
                ]
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
                IsPaid = e.IsPaid,
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
                        ProductTitle = p.ProductTitle,
                        Quantity = p.Quantity
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
                ProductTitle = x.ProductTitle,
                Quantity = x.Quantity,
                UnitPrice = x.UnitPrice,
                GrossAmount = x.GrossAmount,
                CreatedAtUtc = x.CreatedAtUtc
            } )
            .ToListAsync();
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
                Vat = VatReportHelpers.Round2( reportRows.Sum( x => x.VatAmount ) ),
                PolandRows = reportRows
                    .OrderByDescending( x => x.OrderDateUtc )
                    .ThenBy( x => x.OrderNumber )
                    .ThenBy( x => x.VatRatePercent )
                    .Select( MapPolandRow )
                    .ToList()
            },
            BuildCashSummaryRow( cashSales )
        ];

        if (expenses.Count == 0) return rows;

        decimal expenseVat = VatReportHelpers.Round2( expenses.Sum( x => x.VatAmount ) );
        decimal expenseGross = VatReportHelpers.Round2( expenses.Sum( x => x.GrossAmount ) );
        rows.Add( new VatReportDetailsSummaryRow
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
        } );

        return rows;
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
        Dictionary<string, ForeignDeliveryInfo>? deliveryByOrderId = null;
        bool needsShopifyLookup = reportRows
            .Select( r => r.OrderNumber )
            .Distinct()
            .Any( orderNumber =>
            {
                (string _, string parsedName, string parsedAddress) =
                    VatReportHelpers.ParseOrderNumberAndContact( orderNumber );
                return string.IsNullOrWhiteSpace( parsedName ) && string.IsNullOrWhiteSpace( parsedAddress );
            } );

        if (needsShopifyLookup)
        {
            deliveryByOrderId = await _shopifyOrders.FetchForeignDeliveryInfoAsync(
                reportRows
                    .Select( r => r.ShopifyOrderId )
                    .Where( idValue => !string.IsNullOrWhiteSpace( idValue ) )
                    .Distinct()
                    .ToList()
            );
        }

        return reportRows
            .GroupBy( r => r.ShopifyOrderId )
            .Select( g =>
            {
                ForeignDeliveryInfo? info = null;
                deliveryByOrderId?.TryGetValue( g.Key, out info );
                ReportRowData first = g.First();
                (string parsedOrderNumber, string parsedDeliveryName, string parsedDeliveryAddress) =
                    VatReportHelpers.ParseOrderNumberAndContact( first.OrderNumber );
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
                    DeliveryAddress = !string.IsNullOrWhiteSpace( parsedDeliveryAddress )
                        ? parsedDeliveryAddress
                        : (info?.ShippingAddress ?? info?.BillingAddress ?? string.Empty),
                    ShippingAddress = info?.ShippingAddress ?? parsedDeliveryAddress,
                    BillingAddress = info?.BillingAddress ?? string.Empty,
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
            IsPaid = expense.IsPaid,
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
                    ProductTitle = p.ProductTitle,
                    Quantity = p.Quantity
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
        public bool IsPaid { get; set; }
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
        public string ProductTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }

    private sealed class ReportCashSaleData
    {
        public int Id { get; set; }
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal GrossAmount { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
