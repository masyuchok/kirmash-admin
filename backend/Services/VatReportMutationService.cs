using backend.Data;
using backend.Models;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public class VatReportMutationService
{
    /// <summary>Temporarily skip Shopify inventory changes for cash sales (read_locations scope pending).</summary>
    private const bool SyncCashSaleInventoryWithShopify = false;

    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ShopifyInventoryService _shopifyInventory;

    public VatReportMutationService(
        AppDbContext db,
        IHttpContextAccessor httpContextAccessor,
        ShopifyInventoryService shopifyInventory )
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _shopifyInventory = shopifyInventory;
    }

    public async Task MoveRowToForeignAsync( int rowId, string deliveryName, string deliveryAddress )
        {
            VatReportRow? sourceRow = await _db.VatReportRows
                .Include( r => r.Items )
                .Include( r => r.VatReport )
                .FirstOrDefaultAsync( r => r.Id == rowId );
            if (sourceRow is null)
            {
                throw new InvalidOperationException( "Радок справаздачы не знойдзены." );
            }
            if (!string.Equals( sourceRow.VatReport.Type, VatReportType.Poland, StringComparison.OrdinalIgnoreCase ))
            {
                throw new InvalidOperationException( "Перанос у замежныя даступны толькі з польскага справаздачы." );
            }

            string cleanName = (deliveryName ?? string.Empty).Trim();
            string cleanAddress = (deliveryAddress ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( cleanName ))
            {
                throw new InvalidOperationException( "Увядзіце імя атрымальніка для фактуры." );
            }
            if (string.IsNullOrWhiteSpace( cleanAddress ))
            {
                throw new InvalidOperationException( "Увядзіце адрас для пераносу ў замежныя." );
            }

            int year = sourceRow.VatReport.PeriodYear;
            int month = sourceRow.VatReport.PeriodMonth;
            VatReport sourceReport = sourceRow.VatReport;

            VatReport? targetReport = await _db.VatReports
                .FirstOrDefaultAsync( r =>
                    r.PeriodYear == year &&
                    r.PeriodMonth == month &&
                    r.Type == VatReportType.Foreign
                );
            if (targetReport is null)
            {
                targetReport = new VatReport
                {
                    PeriodYear = year,
                    PeriodMonth = month,
                    Type = VatReportType.Foreign,
                    Name = VatReportHelpers.BuildReportName( VatReportType.Foreign, year, month ),
                    Document = null,
                    Vat = 0m,
                    VatCredit = 0m,
                    VatToPay = 0m,
                    Documents = [],
                    ShopifyOrderIds = [],
                    CreatedAtUtc = DateTime.UtcNow
                };
                _db.VatReports.Add( targetReport );
                await _db.SaveChangesAsync();
            }

            string safeOrderNumber = (sourceRow.OrderNumber ?? string.Empty).Trim();
            string orderNumberBase = string.IsNullOrWhiteSpace( safeOrderNumber ) ? $"manual-{sourceRow.Id}" : safeOrderNumber;
            string encodedOrderNumber = VatReportHelpers.EncodeOrderNumberWithContact( orderNumberBase, cleanName, cleanAddress );
            string targetShopifyOrderId = !string.IsNullOrWhiteSpace( sourceRow.ShopifyOrderId )
                ? sourceRow.ShopifyOrderId
                : $"manual-moved-{sourceRow.Id}";

            VatReportRow targetRow = new()
            {
                VatReportId = targetReport.Id,
                ShopifyOrderId = targetShopifyOrderId,
                OrderNumber = encodedOrderNumber,
                OrderDateUtc = sourceRow.OrderDateUtc,
                VatRatePercent = sourceRow.VatRatePercent,
                GrossAmount = sourceRow.GrossAmount,
                VatAmount = sourceRow.VatAmount,
                NetAmount = sourceRow.NetAmount,
                ShippingGrossAmount = sourceRow.ShippingGrossAmount,
                ShippingNetAmount = sourceRow.ShippingNetAmount,
                InvoiceFileName = sourceRow.InvoiceFileName,
                InvoiceContentType = sourceRow.InvoiceContentType,
                InvoiceData = sourceRow.InvoiceData,
                Items = sourceRow.Items.Select( i => new VatReportRowItem
                {
                    ShopifyProductId = i.ShopifyProductId,
                    ProductTitle = i.ProductTitle,
                    ProductType = i.ProductType,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice,
                    GrossAmount = i.GrossAmount,
                    AssignedVatRatePercent = i.AssignedVatRatePercent,
                    AssignmentReason = i.AssignmentReason
                } ).ToList()
            };

            _db.VatReportRows.Add( targetRow );
            _db.VatReportRows.Remove( sourceRow );
            await _db.SaveChangesAsync();

            await RecalculateReportTotalsAsync( sourceReport.Id );
            await RecalculateReportTotalsAsync( targetReport.Id );
        }

    public async Task UpdateRowAsync(
            int rowId,
            decimal vatRatePercent,
            decimal grossAmount,
            decimal vatAmount,
            decimal netAmount,
            decimal? shippingGrossAmount = null
        )
        {
            if (vatRatePercent != 0m && vatRatePercent != 5m && vatRatePercent != 23m)
            {
                throw new InvalidOperationException( "Стаўка VAT павінна быць 0, 5 або 23." );
            }
            if (grossAmount < 0m || vatAmount < 0m || netAmount < 0m)
            {
                throw new InvalidOperationException( "Сумы не могуць быць адмоўнымі." );
            }

            VatReportRow? row = await _db.VatReportRows
                .Include( r => r.VatReport )
                .FirstOrDefaultAsync( r => r.Id == rowId );
            if (row is null)
            {
                throw new InvalidOperationException( "Радок справаздачы не знойдзены." );
            }

            row.VatRatePercent = VatReportHelpers.Round2( vatRatePercent );
            row.GrossAmount = VatReportHelpers.Round2( grossAmount );
            row.VatAmount = VatReportHelpers.Round2( vatAmount );
            row.NetAmount = VatReportHelpers.Round2( netAmount );
            if (shippingGrossAmount.HasValue)
            {
                if (shippingGrossAmount.Value < 0m)
                {
                    throw new InvalidOperationException( "Сума дастаўкі не можа быць адмоўнай." );
                }

                row.ShippingGrossAmount = VatReportHelpers.Round2( shippingGrossAmount.Value );
                decimal rate = row.VatRatePercent / 100m;
                row.ShippingNetAmount = VatReportHelpers.Round2( row.ShippingGrossAmount / (1m + rate) );
            }

            int reportId = row.VatReportId;
            decimal totalVat = await _db.VatReportRows
                .Where( x => x.VatReportId == reportId )
                .Select( x => x.Id == rowId ? row.VatAmount : x.VatAmount )
                .SumAsync();

            VatReport report = row.VatReport;
            report.Vat = VatReportHelpers.Round2( totalVat );
            report.VatToPay = report.Vat;

            await _db.SaveChangesAsync();
        }

    public async Task UpdateRowItemVatAsync( int itemId, decimal vatRatePercent )
        {
            if (vatRatePercent != 0m && vatRatePercent != 5m && vatRatePercent != 23m)
            {
                throw new InvalidOperationException( "Стаўка VAT павінна быць 0, 5 або 23." );
            }

            VatReportRowItem? item = await _db.VatReportRowItems
                .Include( i => i.VatReportRow )
                .FirstOrDefaultAsync( i => i.Id == itemId );
            if (item is null)
            {
                throw new InvalidOperationException( "Пазіцыя радка справаздачы не знойдзена." );
            }

            VatReportRow row = item.VatReportRow;
            item.AssignedVatRatePercent = VatReportHelpers.Round2( vatRatePercent );
            item.AssignmentReason = "manual override";

            // When items in the same order are assigned to different VAT rates,
            // the delivery (shipping) must be split across those VAT groups too.
            int reportId = row.VatReportId;
            string shopifyOrderId = row.ShopifyOrderId;

            List<VatReportRow> orderRows = await _db.VatReportRows
                .Where( r => r.VatReportId == reportId && r.ShopifyOrderId == shopifyOrderId )
                .Include( r => r.Items )
                .ToListAsync();

            if (orderRows.Count == 0)
            {
                await _db.SaveChangesAsync();
                await RecalculateReportTotalsAsync( reportId );
                return;
            }

            List<VatReportRowItem> allItems = orderRows.SelectMany( r => r.Items ).ToList();
            decimal totalGoodsGross = VatReportHelpers.Round2( allItems.Sum( x => x.GrossAmount ) );
            decimal totalShippingGross = VatReportHelpers.Round2( orderRows.Sum( r => r.ShippingGrossAmount ) );

            // Group items by currently assigned VAT rate (0/5/23) and rebuild shipping distribution accordingly.
            Dictionary<decimal, decimal> goodsGrossByRate = allItems
                .GroupBy( i => i.AssignedVatRatePercent )
                .ToDictionary( g => g.Key, g => VatReportHelpers.Round2( g.Sum( x => x.GrossAmount ) ) );

            // Ensure we have rows for every encountered rate.
            Dictionary<decimal, VatReportRow> rowsByRate = orderRows
                .GroupBy( r => r.VatRatePercent )
                .ToDictionary( g => g.Key, g => g.First() );

            Dictionary<decimal, VatReportRow> targetRowsByRate = new();
            Dictionary<decimal, decimal> shippingGrossByRate = new();

            VatReportRow template = orderRows.First();
            foreach ((decimal rate, decimal goodsGross) in goodsGrossByRate)
            {
                if (rate != 0m && rate != 5m && rate != 23m) continue;

                if (!rowsByRate.TryGetValue( rate, out VatReportRow? target ))
                {
                    target = new VatReportRow
                    {
                        VatReportId = template.VatReportId,
                        ShopifyOrderId = template.ShopifyOrderId,
                        OrderNumber = template.OrderNumber,
                        OrderDateUtc = template.OrderDateUtc,
                        VatRatePercent = VatReportHelpers.Round2( rate ),
                        GrossAmount = 0m,
                        VatAmount = 0m,
                        NetAmount = 0m,
                        ShippingGrossAmount = 0m,
                        ShippingNetAmount = 0m,
                        InvoiceFileName = string.Empty,
                        InvoiceContentType = "application/pdf",
                        InvoiceData = null,
                        Items = new List<VatReportRowItem>()
                    };
                    _db.VatReportRows.Add( target );
                    rowsByRate[rate] = target;
                    orderRows.Add( target );
                }

                targetRowsByRate[rate] = target;

                decimal shippingForRate = totalGoodsGross > 0m
                    ? VatReportHelpers.Round2( totalShippingGross * (goodsGross / totalGoodsGross) )
                    : 0m;
                shippingGrossByRate[rate] = shippingForRate;
            }

            // Keep totals exact after rounding split.
            decimal assignedShippingGross = VatReportHelpers.Round2( shippingGrossByRate.Values.Sum() );
            decimal diff = VatReportHelpers.Round2( totalShippingGross - assignedShippingGross );
            if (diff != 0m && shippingGrossByRate.Count > 0)
            {
                // Add drift to the rate group with the biggest goods gross.
                decimal driftRate = shippingGrossByRate
                    .OrderByDescending( kv => goodsGrossByRate.TryGetValue( kv.Key, out decimal gg ) ? gg : 0m )
                    .Select( kv => kv.Key )
                    .First();
                shippingGrossByRate[driftRate] = VatReportHelpers.Round2( shippingGrossByRate[driftRate] + diff );
            }

            // Move items into correct VAT rows and recalculate row amounts from scratch.
            foreach (VatReportRowItem i in allItems)
            {
                decimal rate = i.AssignedVatRatePercent;
                if (!targetRowsByRate.TryGetValue( rate, out VatReportRow? target ))
                {
                    // Shouldn't happen due to goodsGrossByRate construction, but keep safe.
                    continue;
                }
                i.VatReportRow = target;
            }

            // Recalculate each row based on which items are assigned to it.
            foreach (VatReportRow r in orderRows.Where( x => x.ShopifyOrderId == shopifyOrderId ))
            {
                if (!targetRowsByRate.ContainsKey( r.VatRatePercent ))
                {
                    r.ShippingGrossAmount = 0m;
                    r.ShippingNetAmount = 0m;
                    r.GrossAmount = 0m;
                    r.VatAmount = 0m;
                    r.NetAmount = 0m;
                    continue;
                }

                decimal rate = r.VatRatePercent;
                List<VatReportRowItem> itemsForRate = allItems
                    .Where( x => x.AssignedVatRatePercent == rate )
                    .ToList();
                decimal goodsGross = VatReportHelpers.Round2( itemsForRate.Sum( x => x.GrossAmount ) );
                decimal shippingGross = shippingGrossByRate.TryGetValue( rate, out decimal sg ) ? sg : 0m;

                decimal vatRate = rate / 100m;
                decimal shippingNet = VatReportHelpers.Round2( shippingGross / (1m + vatRate) );
                decimal shippingVat = VatReportHelpers.Round2( shippingGross - shippingNet );

                decimal itemsVat = VatReportHelpers.Round2(
                    itemsForRate.Sum( x =>
                    {
                        decimal rRate = x.AssignedVatRatePercent / 100m;
                        return rRate <= 0m ? 0m : VatReportHelpers.Round2( x.GrossAmount * rRate / (1m + rRate) );
                    } )
                );

                decimal gross = VatReportHelpers.Round2( goodsGross + shippingGross );
                decimal vatAmount = VatReportHelpers.Round2( itemsVat + shippingVat );
                decimal netAmount = VatReportHelpers.Round2( gross - vatAmount );

                r.ShippingGrossAmount = VatReportHelpers.Round2( shippingGross );
                r.ShippingNetAmount = VatReportHelpers.Round2( shippingNet );
                r.GrossAmount = gross;
                r.VatAmount = vatAmount;
                r.NetAmount = netAmount;
            }

            await _db.SaveChangesAsync();
            await RecalculateReportTotalsAsync( reportId );
        }

    public async Task AddRowAsync( int reportId, VatReportRowCreateRequest request )
        {
            if (string.IsNullOrWhiteSpace( request.OrderNumber ))
            {
                throw new InvalidOperationException( "Нумар замовы абавязковы." );
            }
            if (request.OrderDateUtc == default)
            {
                throw new InvalidOperationException( "Дата замовы абавязковая." );
            }
            if (request.VatRatePercent != 5m && request.VatRatePercent != 23m)
            {
                throw new InvalidOperationException( "Стаўка VAT павінна быць 5 або 23." );
            }
            if (request.GrossAmount < 0m || request.VatAmount < 0m || request.NetAmount < 0m)
            {
                throw new InvalidOperationException( "Сумы не могуць быць адмоўнымі." );
            }

            VatReport? report = await _db.VatReports.FirstOrDefaultAsync( r => r.Id == reportId );
            if (report is null)
            {
                throw new InvalidOperationException( "Справаздача не знойдзена." );
            }

            VatReportRow row = new()
            {
                VatReportId = report.Id,
                ShopifyOrderId = string.Empty,
                OrderNumber = request.OrderNumber.Trim(),
                OrderDateUtc = DateTime.SpecifyKind( request.OrderDateUtc, DateTimeKind.Utc ),
                VatRatePercent = VatReportHelpers.Round2( request.VatRatePercent ),
                GrossAmount = VatReportHelpers.Round2( request.GrossAmount ),
                VatAmount = VatReportHelpers.Round2( request.VatAmount ),
                NetAmount = VatReportHelpers.Round2( request.NetAmount ),
                ShippingGrossAmount = 0m,
                ShippingNetAmount = 0m,
                Items = new List<VatReportRowItem>()
            };

            _db.VatReportRows.Add( row );
            await _db.SaveChangesAsync();

            await RecalculateReportTotalsAsync( report.Id );
        }

    public async Task DeleteRowAsync( int rowId )
        {
            VatReportRow? row = await _db.VatReportRows
                .Include( r => r.VatReport )
                .FirstOrDefaultAsync( r => r.Id == rowId );
            if (row is null)
            {
                throw new InvalidOperationException( "Радок справаздачы не знойдзены." );
            }

            int reportId = row.VatReportId;
            _db.VatReportRows.Remove( row );
            await _db.SaveChangesAsync();

            await RecalculateReportTotalsAsync( reportId );
        }

    public async Task<int> AddExpenseAsync( int reportId, VatReportExpenseCreateRequest request )
        {
            if (request.GrossAmount < 0m || request.VatAmount < 0m || request.NetAmount < 0m)
            {
                throw new InvalidOperationException( "Сумы не могуць быць адмоўнымі." );
            }

            VatReport? report = await _db.VatReports.FirstOrDefaultAsync( r => r.Id == reportId );
            if (report is null)
            {
                throw new InvalidOperationException( "Справаздача не знойдзена." );
            }

            if (!string.Equals( report.Type, VatReportType.Poland, StringComparison.OrdinalIgnoreCase ))
            {
                throw new InvalidOperationException( "Р Р°СЃС…РѕРґС‹ РјРѕР¶РЅР° РґР°РґР°РІР°С†СЊ С‚РѕР»СЊРєС– Сћ РїРѕР»СЊСЃРєС– СЃРїСЂР°РІР°Р·РґР°С‡Сѓ." );
            }

            await ExpenseInvoiceTypeSeeder.EnsureDefaultAsync( _db );
            ExpenseInvoiceType? invoiceType = await _db.ExpenseInvoiceTypes
                .FirstOrDefaultAsync( x => x.Id == request.ExpenseInvoiceTypeId );
            if (invoiceType is null)
            {
                throw new InvalidOperationException( "Тып расходнай фактуры не знойдзены." );
            }

            decimal gross = VatReportHelpers.Round2( request.GrossAmount );
            decimal vat = VatReportHelpers.Round2( request.VatAmount );
            decimal net = VatReportHelpers.Round2( request.NetAmount );
            if (gross <= 0m && vat <= 0m && net <= 0m)
            {
                throw new InvalidOperationException( "Увядзіце хаця б адну суму." );
            }

            bool isSupplierPayment = string.Equals(
                invoiceType.Name,
                ExpenseInvoiceTypeSeeder.SupplierPaymentDefaultName,
                StringComparison.Ordinal );

            int? supplierId = null;
            List<VatReportExpenseProductCreateRequest> productLines = new();
            if (isSupplierPayment)
            {
                if (!request.SupplierId.HasValue || request.SupplierId.Value <= 0)
                {
                    throw new InvalidOperationException( "Выберыце пастаўшчыка." );
                }

                Supplier? supplier = await _db.Suppliers.FirstOrDefaultAsync( s => s.Id == request.SupplierId.Value );
                if (supplier is null)
                {
                    throw new InvalidOperationException( "Пастаўшчык не знойдзены." );
                }

                productLines = (request.Products ?? new List<VatReportExpenseProductCreateRequest>())
                    .Where( p => p.Quantity > 0 && !string.IsNullOrWhiteSpace( p.ShopifyProductId ) )
                    .ToList();
                if (productLines.Count == 0)
                {
                    throw new InvalidOperationException( "Дадайце хаця б адзін тавар з колькасцю." );
                }

                List<string> supplierProductIds = await _db.SupplyProducts
                    .AsNoTracking()
                    .Where( sp => sp.Supply.SupplierId == request.SupplierId.Value )
                    .Select( sp => sp.ShopifyProductId )
                    .ToListAsync();
                HashSet<string> normalizedSupplierProductIds = supplierProductIds
                    .Select( id => ShopifyIds.NormalizeGid( id, "gid://shopify/Product/" ).Trim() )
                    .ToHashSet( StringComparer.OrdinalIgnoreCase );

                foreach (VatReportExpenseProductCreateRequest line in productLines)
                {
                    string normalizedProductId = ShopifyIds.NormalizeGid(
                        line.ShopifyProductId.Trim(),
                        "gid://shopify/Product/"
                    ).Trim();
                    if (!normalizedSupplierProductIds.Contains( normalizedProductId ))
                    {
                        throw new InvalidOperationException(
                            $"Тавар «{line.ProductTitle}» не належыць выбранаму пастаўшчыку."
                        );
                    }
                }

                supplierId = supplier.Id;
            }

            DateTime expenseDate = request.ExpenseDateUtc == default
                ? DateTime.UtcNow
                : DateTime.SpecifyKind( request.ExpenseDateUtc, DateTimeKind.Utc );

            VatReportExpense expense = new()
            {
                VatReportId = reportId,
                ExpenseInvoiceTypeId = invoiceType.Id,
                GrossAmount = gross,
                VatAmount = vat,
                NetAmount = net,
                ExpenseDateUtc = expenseDate,
                Comment = string.IsNullOrWhiteSpace( request.Comment ) ? null : request.Comment.Trim(),
                IsPaid = request.IsPaid,
                SupplierId = supplierId,
                CreatedAtUtc = DateTime.UtcNow
            };

            foreach (VatReportExpenseProductCreateRequest line in productLines)
            {
                expense.Products.Add( new VatReportExpenseProduct
                {
                    ShopifyProductId = ShopifyIds.NormalizeGid(
                        line.ShopifyProductId.Trim(),
                        "gid://shopify/Product/"
                    ).Trim(),
                    ProductTitle = string.IsNullOrWhiteSpace( line.ProductTitle )
                        ? line.ShopifyProductId.Trim()
                        : line.ProductTitle.Trim(),
                    Quantity = line.Quantity
                } );
            }

            _db.VatReportExpenses.Add( expense );
            await _db.SaveChangesAsync();
            return expense.Id;
        }

    public async Task UploadExpenseInvoiceAsync( int expenseId, string fileName, string contentType, byte[] data )
        {
            VatReportExpense? expense = await _db.VatReportExpenses.FirstOrDefaultAsync( x => x.Id == expenseId );
            if (expense is null)
            {
                throw new InvalidOperationException( "Расход не знойдзены." );
            }
            if (data.Length == 0)
            {
                throw new InvalidOperationException( "Файл пусты." );
            }
            if (data.Length > 10 * 1024 * 1024)
            {
                throw new InvalidOperationException( "Файл занадта вялікі. Максімум 10 MB." );
            }

            expense.InvoiceFileName = fileName;
            expense.InvoiceContentType = contentType;
            expense.InvoiceData = data;
            await _db.SaveChangesAsync();
        }

    public async Task<(string FileName, string ContentType, byte[] Data)> GetExpenseInvoiceAsync( int expenseId )
        {
            VatReportExpense? expense = await _db.VatReportExpenses.FirstOrDefaultAsync( x => x.Id == expenseId );
            if (expense is null)
            {
                throw new InvalidOperationException( "Расход не знойдзены." );
            }
            if (expense.InvoiceData is null || expense.InvoiceData.Length == 0)
            {
                throw new InvalidOperationException( "Фактура для гэтага расходу не загружана." );
            }

            return (
                string.IsNullOrWhiteSpace( expense.InvoiceFileName ) ? $"expense-{expense.Id}.pdf" : expense.InvoiceFileName,
                string.IsNullOrWhiteSpace( expense.InvoiceContentType ) ? "application/pdf" : expense.InvoiceContentType,
                expense.InvoiceData
            );
        }

    public async Task DeleteExpenseAsync( int expenseId )
        {
            VatReportExpense? expense = await _db.VatReportExpenses.FirstOrDefaultAsync( x => x.Id == expenseId );
            if (expense is null)
            {
                throw new InvalidOperationException( "Расход не знойдзены." );
            }

            _db.VatReportExpenses.Remove( expense );
            await _db.SaveChangesAsync();
        }

    public async Task UploadRowInvoiceAsync( int rowId, string fileName, string contentType, byte[] data )
        {
            VatReportRow? row = await _db.VatReportRows.FirstOrDefaultAsync( r => r.Id == rowId );
            if (row is null)
            {
                throw new InvalidOperationException( "Радок справаздачы не знойдзены." );
            }
            if (data.Length == 0)
            {
                throw new InvalidOperationException( "Файл пусты." );
            }
            if (data.Length > 10 * 1024 * 1024)
            {
                throw new InvalidOperationException( "Файл занадта вялікі. Максімум 10 MB." );
            }

            row.InvoiceFileName = fileName;
            row.InvoiceContentType = contentType;
            row.InvoiceData = data;
            await _db.SaveChangesAsync();
        }

    public async Task<(string FileName, string ContentType, byte[] Data)> GetRowInvoiceAsync( int rowId )
        {
            VatReportRow? row = await _db.VatReportRows.FirstOrDefaultAsync( r => r.Id == rowId );
            if (row is null)
            {
                throw new InvalidOperationException( "Радок справаздачы не знойдзены." );
            }
            if (row.InvoiceData is null || row.InvoiceData.Length == 0)
            {
                throw new InvalidOperationException( "Фактура для гэтага радка не загружана." );
            }

            return (
                string.IsNullOrWhiteSpace( row.InvoiceFileName ) ? $"invoice-{row.Id}.pdf" : row.InvoiceFileName,
                string.IsNullOrWhiteSpace( row.InvoiceContentType ) ? "application/pdf" : row.InvoiceContentType,
                row.InvoiceData
            );
        }

    public async Task<int> AddCashSaleAsync( int reportId, VatReportCashSaleCreateRequest request )
    {
        if (string.IsNullOrWhiteSpace( request.ShopifyProductId ))
        {
            throw new InvalidOperationException( "Выберыце тавар." );
        }
        if (request.Quantity <= 0)
        {
            throw new InvalidOperationException( "Колькасць павінна быць больш за 0." );
        }
        if (request.UnitPrice < 0m)
        {
            throw new InvalidOperationException( "Цана не можа быць адмоўнай." );
        }

        VatReport? report = await _db.VatReports.FirstOrDefaultAsync( r => r.Id == reportId );
        if (report is null)
        {
            throw new InvalidOperationException( "Справаздача не знойдзена." );
        }
        if (!string.Equals( report.Type, VatReportType.Poland, StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Наяўныя продажы даступныя толькі ў польскай справаздачы." );
        }

        string productId = ShopifyIds.NormalizeProductId( request.ShopifyProductId.Trim() );
        string title = string.IsNullOrWhiteSpace( request.ProductTitle ) ? productId : request.ProductTitle.Trim();
        decimal unitPrice = VatReportHelpers.Round2( request.UnitPrice );
        decimal gross = VatReportHelpers.Round2( unitPrice * request.Quantity );

        if (SyncCashSaleInventoryWithShopify)
        {
            ShopifySession session = ShopifySessionReader.Require(
                _httpContextAccessor,
                "Няма Shopify-кантэксту для абнаўлення склада."
            );
            await _shopifyInventory.ApplyInventoryDeltaByProductKeyAsync(
                session.Shop,
                session.AccessToken,
                productId,
                -request.Quantity
            );
        }

        VatReportCashSale sale = new()
        {
            VatReportId = reportId,
            ShopifyProductId = productId,
            ProductTitle = title,
            Quantity = request.Quantity,
            UnitPrice = unitPrice,
            GrossAmount = gross,
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.VatReportCashSales.Add( sale );
        await _db.SaveChangesAsync();
        return sale.Id;
    }

    public async Task DeleteCashSaleAsync( int cashSaleId )
    {
        VatReportCashSale? sale = await _db.VatReportCashSales.FirstOrDefaultAsync( x => x.Id == cashSaleId );
        if (sale is null)
        {
            throw new InvalidOperationException( "Запіс наяўнай продажы не знойдзены." );
        }

        if (SyncCashSaleInventoryWithShopify)
        {
            ShopifySession session = ShopifySessionReader.Require(
                _httpContextAccessor,
                "Няма Shopify-кантэксту для абнаўлення склада."
            );
            await _shopifyInventory.ApplyInventoryDeltaByProductKeyAsync(
                session.Shop,
                session.AccessToken,
                sale.ShopifyProductId,
                sale.Quantity
            );
        }

        _db.VatReportCashSales.Remove( sale );
        await _db.SaveChangesAsync();
    }

    private async Task RecalculateReportTotalsAsync( int reportId )
        {
            decimal totalVat = await _db.VatReportRows
                .Where( x => x.VatReportId == reportId )
                .SumAsync( x => x.VatAmount );
            string[] orderIds = await _db.VatReportRows
                .Where( x => x.VatReportId == reportId && !string.IsNullOrWhiteSpace( x.ShopifyOrderId ) )
                .Select( x => x.ShopifyOrderId )
                .Distinct()
                .ToArrayAsync();

            VatReport? report = await _db.VatReports.FirstOrDefaultAsync( r => r.Id == reportId );
            if (report is null) return;

            report.Vat = VatReportHelpers.Round2( totalVat );
            report.VatToPay = report.Vat;
            report.ShopifyOrderIds = orderIds;
            await _db.SaveChangesAsync();
        }
}
