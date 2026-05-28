using System.Globalization;
using System.Text.Json;
using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services
{
    public class VatReportService
    {
        private readonly AppDbContext _db;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public VatReportService( AppDbContext db, IHttpContextAccessor httpContextAccessor )
        {
            _db = db;
            _httpContextAccessor = httpContextAccessor;
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

        public async Task<VatReportListItem> GenerateAsync( int periodYear, int periodMonth, string reportType )
        {
            ValidatePeriod( periodYear, periodMonth );
            string normalizedType = NormalizeReportType( reportType );
            bool exists = await _db.VatReports.AnyAsync(
                r => r.PeriodYear == periodYear && r.PeriodMonth == periodMonth && r.Type == normalizedType
            );
            if (exists)
            {
                throw new InvalidOperationException( "Справаздача за гэты месяц ужо існуе. Выкарыстайце перегенерацыю." );
            }

            List<VatReportRow> rows = normalizedType switch
            {
                VatReportType.Poland => await BuildPolandRowsAsync( periodYear, periodMonth ),
                VatReportType.Foreign => await BuildForeignRowsAsync( periodYear, periodMonth ),
                _ => throw new InvalidOperationException( "Невядомы тып справаздачы." )
            };

            decimal vatTotal = rows.Sum( r => r.VatAmount );
            VatReport report = new()
            {
                PeriodYear = periodYear,
                PeriodMonth = periodMonth,
                Type = normalizedType,
                Name = BuildReportName( normalizedType, periodYear, periodMonth ),
                Document = null,
                Vat = Round2( vatTotal ),
                VatCredit = 0m,
                VatToPay = Round2( vatTotal ),
                Documents = [],
                ShopifyOrderIds = rows.Select( r => r.ShopifyOrderId ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray(),
                CreatedAtUtc = DateTime.UtcNow,
                Rows = rows
            };

            _db.VatReports.Add( report );
            await _db.SaveChangesAsync();

            return new VatReportListItem
            {
                Id = report.Id,
                PeriodYear = report.PeriodYear,
                PeriodMonth = report.PeriodMonth,
                Type = report.Type,
                Name = report.Name,
                Document = report.Document,
                Vat = report.Vat,
                VatCredit = report.VatCredit,
                VatToPay = report.VatToPay,
                Documents = report.Documents.ToList(),
                ShopifyOrderIds = report.ShopifyOrderIds.ToList()
            };
        }

        public async Task<VatReportListItem> RegenerateAsync( int id )
        {
            VatReport? report = await _db.VatReports
                .Include( r => r.Rows )
                .ThenInclude( r => r.Items )
                .FirstOrDefaultAsync( r => r.Id == id );
            if (report is null)
            {
                throw new InvalidOperationException( "Справаздача не знойдзена." );
            }

            List<VatReportRow> rows = report.Type switch
            {
                VatReportType.Poland => await BuildPolandRowsAsync( report.PeriodYear, report.PeriodMonth ),
                VatReportType.Foreign => await BuildForeignRowsAsync( report.PeriodYear, report.PeriodMonth ),
                _ => throw new InvalidOperationException( "Невядомы тып справаздачы." )
            };

            if (report.Rows.Count > 0)
            {
                _db.VatReportRows.RemoveRange( report.Rows );
            }

            decimal vatTotal = rows.Sum( r => r.VatAmount );
            report.Vat = Round2( vatTotal );
            report.VatCredit = 0m;
            report.VatToPay = Round2( vatTotal );
            report.ShopifyOrderIds = rows
                .Select( r => r.ShopifyOrderId )
                .Distinct( StringComparer.OrdinalIgnoreCase )
                .ToArray();
            report.Rows = rows;

            await _db.SaveChangesAsync();

            return new VatReportListItem
            {
                Id = report.Id,
                PeriodYear = report.PeriodYear,
                PeriodMonth = report.PeriodMonth,
                Type = report.Type,
                Name = report.Name,
                Document = report.Document,
                Vat = report.Vat,
                VatCredit = report.VatCredit,
                VatToPay = report.VatToPay,
                Documents = report.Documents.ToList(),
                ShopifyOrderIds = report.ShopifyOrderIds.ToList()
            };
        }

        public async Task<VatReportDetailsResponse> GetDetailsAsync( int id )
        {
            VatReport? report = await _db.VatReports
                .AsNoTracking()
                .Include( r => r.Rows )
                .ThenInclude( r => r.Items )
                .Include( r => r.Expenses )
                .ThenInclude( e => e.ExpenseInvoiceType )
                .Include( r => r.Expenses )
                .ThenInclude( e => e.Supplier )
                .Include( r => r.Expenses )
                .ThenInclude( e => e.Products )
                .FirstOrDefaultAsync( r => r.Id == id );
            if (report is null)
            {
                throw new InvalidOperationException( "Справаздача не знойдзена." );
            }

            List<VatReportDetailsSummaryRow> rows;
            if (string.Equals( report.Type, VatReportType.Poland, StringComparison.OrdinalIgnoreCase ))
            {
                // For Poland reports, UI should always show exactly one summary row.
                rows = new List<VatReportDetailsSummaryRow>
                {
                    new VatReportDetailsSummaryRow
                    {
                        Type = VatReportType.Poland,
                        Name = "Польша",
                        ShopifyOrderId = "poland",
                        Vat = Round2( report.Rows.Sum( x => x.VatAmount ) ),
                        PolandRows = report.Rows
                            .OrderByDescending( x => x.OrderDateUtc )
                            .ThenBy( x => x.OrderNumber )
                            .ThenBy( x => x.VatRatePercent )
                            .Select( x => new VatReportDetailsPolandRow
                            {
                                Id = x.Id,
                                OrderNumber = x.OrderNumber,
                                OrderDateUtc = x.OrderDateUtc,
                                VatRatePercent = x.VatRatePercent,
                                GrossAmount = x.GrossAmount,
                                VatAmount = x.VatAmount,
                                NetAmount = x.NetAmount,
                                ShippingGrossAmount = x.ShippingGrossAmount,
                                ShippingNetAmount = x.ShippingNetAmount,
                                InvoiceFileName = x.InvoiceFileName,
                                Items = x.Items
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
                            } )
                            .ToList()
                    }
                };
                decimal expenseVat = Round2( report.Expenses.Sum( x => x.VatAmount ) );
                decimal expenseGross = Round2( report.Expenses.Sum( x => x.GrossAmount ) );
                rows.Add( new VatReportDetailsSummaryRow
                {
                    Type = "expense",
                    Name = "Расход",
                    ShopifyOrderId = "expense-summary",
                    Vat = expenseVat,
                    GrossAmount = expenseGross,
                    NetAmount = Round2( expenseGross - expenseVat ),
                    ExpenseRows = report.Expenses
                        .OrderByDescending( x => x.CreatedAtUtc )
                        .Select( x => new VatReportExpenseRow
                        {
                            Id = x.Id,
                            GrossAmount = x.GrossAmount,
                            VatAmount = x.VatAmount,
                            NetAmount = x.NetAmount,
                            ExpenseDateUtc = x.ExpenseDateUtc,
                            Comment = x.Comment ?? string.Empty,
                            IsPaid = x.IsPaid,
                            ExpenseInvoiceTypeId = x.ExpenseInvoiceTypeId,
                            ExpenseInvoiceTypeName = x.ExpenseInvoiceType.Name,
                            InvoiceFileName = x.InvoiceFileName,
                            CreatedAtUtc = x.CreatedAtUtc,
                            SupplierId = x.SupplierId,
                            SupplierName = x.Supplier?.Name ?? string.Empty,
                            Products = x.Products
                                .OrderBy( p => p.ProductTitle )
                                .Select( p => new VatReportExpenseProductRow
                                {
                                    Id = p.Id,
                                    ShopifyProductId = p.ShopifyProductId,
                                    ProductTitle = p.ProductTitle,
                                    Quantity = p.Quantity
                                } )
                                .ToList()
                        } )
                        .ToList()
                } );
            }
            else
            {
                Dictionary<string, ForeignDeliveryInfo> deliveryByOrderId = await FetchForeignDeliveryInfoAsync(
                    report.Rows
                        .Select( r => r.ShopifyOrderId )
                        .Where( idValue => !string.IsNullOrWhiteSpace( idValue ) )
                        .Distinct()
                        .ToList()
                );
                rows = report.Rows
                    .GroupBy( r => r.ShopifyOrderId )
                    .Select( g =>
                    {
                        deliveryByOrderId.TryGetValue( g.Key, out ForeignDeliveryInfo? info );
                        (string parsedOrderNumber, string parsedDeliveryName, string parsedDeliveryAddress) = ParseOrderNumberAndContact( g.First().OrderNumber );
                        return new VatReportDetailsSummaryRow
                        {
                            Type = report.Type,
                            Name = !string.IsNullOrWhiteSpace( parsedOrderNumber ) ? parsedOrderNumber : (g.First().OrderNumber ?? g.Key),
                            ShopifyOrderId = g.Key,
                            OrderDateUtc = g.Min( x => x.OrderDateUtc ),
                            DeliveryName = info?.Name ?? parsedDeliveryName,
                            DeliveryAddress = info?.ShippingAddress ?? info?.BillingAddress ?? parsedDeliveryAddress,
                            ShippingAddress = info?.ShippingAddress ?? string.Empty,
                            BillingAddress = info?.BillingAddress ?? string.Empty,
                            GrossAmount = Round2( g.Sum( x => x.GrossAmount ) ),
                            Vat = Round2( g.Sum( x => x.VatAmount ) ),
                            NetAmount = Round2( g.Sum( x => x.NetAmount ) ),
                            PolandRows = g
                                .OrderBy( x => x.VatRatePercent )
                                .Select( x => new VatReportDetailsPolandRow
                                {
                                    Id = x.Id,
                                    OrderNumber = x.OrderNumber,
                                    OrderDateUtc = x.OrderDateUtc,
                                    VatRatePercent = x.VatRatePercent,
                                    GrossAmount = x.GrossAmount,
                                    VatAmount = x.VatAmount,
                                    NetAmount = x.NetAmount,
                                    ShippingGrossAmount = x.ShippingGrossAmount,
                                    ShippingNetAmount = x.ShippingNetAmount,
                                    InvoiceFileName = x.InvoiceFileName,
                                    Items = x.Items
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
                                } )
                                .ToList()
                        };
                    } )
                    .OrderByDescending( x => x.Vat )
                    .ToList();
            }

            return new VatReportDetailsResponse
            {
                Id = report.Id,
                PeriodYear = report.PeriodYear,
                PeriodMonth = report.PeriodMonth,
                Vat = report.Vat,
                Rows = rows
            };
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
                    Name = BuildReportName( VatReportType.Foreign, year, month ),
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
            string encodedOrderNumber = EncodeOrderNumberWithContact( orderNumberBase, cleanName, cleanAddress );
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

            row.VatRatePercent = Round2( vatRatePercent );
            row.GrossAmount = Round2( grossAmount );
            row.VatAmount = Round2( vatAmount );
            row.NetAmount = Round2( netAmount );
            if (shippingGrossAmount.HasValue)
            {
                if (shippingGrossAmount.Value < 0m)
                {
                    throw new InvalidOperationException( "Сума дастаўкі не можа быць адмоўнай." );
                }

                row.ShippingGrossAmount = Round2( shippingGrossAmount.Value );
                decimal rate = row.VatRatePercent / 100m;
                row.ShippingNetAmount = Round2( row.ShippingGrossAmount / (1m + rate) );
            }

            int reportId = row.VatReportId;
            decimal totalVat = await _db.VatReportRows
                .Where( x => x.VatReportId == reportId )
                .Select( x => x.Id == rowId ? row.VatAmount : x.VatAmount )
                .SumAsync();

            VatReport report = row.VatReport;
            report.Vat = Round2( totalVat );
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
            item.AssignedVatRatePercent = Round2( vatRatePercent );
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
            decimal totalGoodsGross = Round2( allItems.Sum( x => x.GrossAmount ) );
            decimal totalShippingGross = Round2( orderRows.Sum( r => r.ShippingGrossAmount ) );

            // Group items by currently assigned VAT rate (0/5/23) and rebuild shipping distribution accordingly.
            Dictionary<decimal, decimal> goodsGrossByRate = allItems
                .GroupBy( i => i.AssignedVatRatePercent )
                .ToDictionary( g => g.Key, g => Round2( g.Sum( x => x.GrossAmount ) ) );

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
                        VatRatePercent = Round2( rate ),
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
                    ? Round2( totalShippingGross * (goodsGross / totalGoodsGross) )
                    : 0m;
                shippingGrossByRate[rate] = shippingForRate;
            }

            // Keep totals exact after rounding split.
            decimal assignedShippingGross = Round2( shippingGrossByRate.Values.Sum() );
            decimal diff = Round2( totalShippingGross - assignedShippingGross );
            if (diff != 0m && shippingGrossByRate.Count > 0)
            {
                // Add drift to the rate group with the biggest goods gross.
                decimal driftRate = shippingGrossByRate
                    .OrderByDescending( kv => goodsGrossByRate.TryGetValue( kv.Key, out decimal gg ) ? gg : 0m )
                    .Select( kv => kv.Key )
                    .First();
                shippingGrossByRate[driftRate] = Round2( shippingGrossByRate[driftRate] + diff );
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
                decimal goodsGross = Round2( itemsForRate.Sum( x => x.GrossAmount ) );
                decimal shippingGross = shippingGrossByRate.TryGetValue( rate, out decimal sg ) ? sg : 0m;

                decimal vatRate = rate / 100m;
                decimal shippingNet = Round2( shippingGross / (1m + vatRate) );
                decimal shippingVat = Round2( shippingGross - shippingNet );

                decimal itemsVat = Round2(
                    itemsForRate.Sum( x =>
                    {
                        decimal rRate = x.AssignedVatRatePercent / 100m;
                        return rRate <= 0m ? 0m : Round2( x.GrossAmount * rRate / (1m + rRate) );
                    } )
                );

                decimal gross = Round2( goodsGross + shippingGross );
                decimal vatAmount = Round2( itemsVat + shippingVat );
                decimal netAmount = Round2( gross - vatAmount );

                r.ShippingGrossAmount = Round2( shippingGross );
                r.ShippingNetAmount = Round2( shippingNet );
                r.GrossAmount = gross;
                r.VatAmount = vatAmount;
                r.NetAmount = netAmount;
            }

            await _db.SaveChangesAsync();
            await RecalculateReportTotalsAsync( reportId );
        }

        public async Task<List<VatReportSourceOrderOption>> GetSourceOrderOptionsAsync( int reportId )
        {
            VatReport? report = await _db.VatReports
                .AsNoTracking()
                .FirstOrDefaultAsync( r => r.Id == reportId );
            if (report is null)
            {
                throw new InvalidOperationException( "Справаздача не знойдзена." );
            }
            if (!string.Equals( report.Type, VatReportType.Poland, StringComparison.OrdinalIgnoreCase ))
            {
                return new List<VatReportSourceOrderOption>();
            }

            return (await BuildPolandRowsAsync( report.PeriodYear, report.PeriodMonth ))
                .OrderByDescending( x => x.OrderDateUtc )
                .ThenBy( x => x.OrderNumber )
                .ThenBy( x => x.VatRatePercent )
                .Select( x => new VatReportSourceOrderOption
                {
                    ShopifyOrderId = x.ShopifyOrderId,
                    OrderNumber = x.OrderNumber,
                    OrderDateUtc = x.OrderDateUtc,
                    VatRatePercent = x.VatRatePercent,
                    GrossAmount = x.GrossAmount,
                    VatAmount = x.VatAmount,
                    NetAmount = x.NetAmount
                } )
                .ToList();
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
                VatRatePercent = Round2( request.VatRatePercent ),
                GrossAmount = Round2( request.GrossAmount ),
                VatAmount = Round2( request.VatAmount ),
                NetAmount = Round2( request.NetAmount ),
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
                throw new InvalidOperationException( "Расходы можна дадаваць толькі ў польскі справаздачу." );
            }

            await ExpenseInvoiceTypeSeeder.EnsureDefaultAsync( _db );
            ExpenseInvoiceType? invoiceType = await _db.ExpenseInvoiceTypes
                .FirstOrDefaultAsync( x => x.Id == request.ExpenseInvoiceTypeId );
            if (invoiceType is null)
            {
                throw new InvalidOperationException( "Тып расходнай фактуры не знойдзены." );
            }

            decimal gross = Round2( request.GrossAmount );
            decimal vat = Round2( request.VatAmount );
            decimal net = Round2( request.NetAmount );
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
                    .Select( id => NormalizeFromShopifyGid( id, "gid://shopify/Product/" ).Trim() )
                    .ToHashSet( StringComparer.OrdinalIgnoreCase );

                foreach (VatReportExpenseProductCreateRequest line in productLines)
                {
                    string normalizedProductId = NormalizeFromShopifyGid(
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
                    ShopifyProductId = NormalizeFromShopifyGid(
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

            report.Vat = Round2( totalVat );
            report.VatToPay = report.Vat;
            report.ShopifyOrderIds = orderIds;
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// Aggregates sold quantities from live Shopify orders using the same Poland/Foreign
        /// inclusion rules as VAT report generation (not from persisted report rows).
        /// </summary>
        public async Task<Dictionary<string, int>> GetSoldQuantitiesByProductFromShopifyAsync()
        {
            DateOnly? earliestSupplyDate = await _db.Supplies
                .AsNoTracking()
                .MinAsync( s => (DateOnly?)s.Date );
            if (!earliestSupplyDate.HasValue)
            {
                return new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
            }

            DateOnly startMonth = new( earliestSupplyDate.Value.Year, earliestSupplyDate.Value.Month, 1 );
            DateOnly endMonth = DateOnly.FromDateTime( DateTime.UtcNow );
            Dictionary<string, int> soldByProduct = new( StringComparer.OrdinalIgnoreCase );

            for (DateOnly monthCursor = startMonth; monthCursor <= endMonth; monthCursor = monthCursor.AddMonths( 1 ))
            {
                List<ShopifyOrderDto> polandOrders = await FetchOrdersForPolandAsync( monthCursor.Year, monthCursor.Month );
                List<ShopifyOrderDto> foreignOrders = await FetchOrdersForForeignAsync( monthCursor.Year, monthCursor.Month );
                AddOrderItemsToSoldMap( polandOrders, soldByProduct );
                AddOrderItemsToSoldMap( foreignOrders, soldByProduct );
            }

            return soldByProduct;
        }

        public async Task<Dictionary<string, int>> GetSoldQuantitiesFromShopifySinceAsync( DateTime sinceUtc )
        {
            DateTime toUtc = DateTime.UtcNow;
            if (sinceUtc >= toUtc)
            {
                return new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
            }

            DateOnly startMonth = DateOnly.FromDateTime( sinceUtc );
            DateOnly endMonth = DateOnly.FromDateTime( toUtc );
            Dictionary<string, int> soldByProduct = new( StringComparer.OrdinalIgnoreCase );

            for (DateOnly monthCursor = new DateOnly( startMonth.Year, startMonth.Month, 1 );
                 monthCursor <= endMonth;
                 monthCursor = monthCursor.AddMonths( 1 ))
            {
                List<ShopifyOrderDto> polandOrders = await FetchOrdersForPolandAsync( monthCursor.Year, monthCursor.Month );
                List<ShopifyOrderDto> foreignOrders = await FetchOrdersForForeignAsync( monthCursor.Year, monthCursor.Month );
                AddOrdersToSoldMapSince( polandOrders, sinceUtc, soldByProduct );
                AddOrdersToSoldMapSince( foreignOrders, sinceUtc, soldByProduct );
            }

            return soldByProduct;
        }

        private static void AddOrdersToSoldMapSince(
            List<ShopifyOrderDto> orders,
            DateTime sinceUtc,
            Dictionary<string, int> soldByProduct )
        {
            foreach (ShopifyOrderDto order in orders)
            {
                if (order.CreatedAtUtc <= sinceUtc) continue;
                AddOrderItemsToSoldMap( new List<ShopifyOrderDto> { order }, soldByProduct );
            }
        }

        private static void AddOrderItemsToSoldMap(
            List<ShopifyOrderDto> orders,
            Dictionary<string, int> soldByProduct )
        {
            foreach (ShopifyOrderDto order in orders)
            {
                foreach (ShopifyLineItemDto item in order.Items)
                {
                    if (item.Quantity <= 0) continue;
                    string productId = NormalizeFromShopifyGid( item.ShopifyProductId, "gid://shopify/Product/" ).Trim();
                    if (string.IsNullOrWhiteSpace( productId )) continue;
                    soldByProduct[productId] = soldByProduct.GetValueOrDefault( productId ) + item.Quantity;
                }
            }
        }

        private async Task<List<VatReportRow>> BuildPolandRowsAsync( int year, int month )
        {
            List<ShopifyOrderDto> orders = await FetchOrdersForPolandAsync( year, month );
            Dictionary<string, decimal> supplyVatRates = await GetSupplyVatRatesAsync();
            List<VatReportRow> rows = new();

            foreach (ShopifyOrderDto order in orders)
            {
                decimal shippingGross = Round2( order.ShippingGross );
                decimal gross5 = 0m;
                decimal gross23 = 0m;
                List<ShopifyClassifiedItemDto> items5 = new();
                List<ShopifyClassifiedItemDto> items23 = new();

                foreach (ShopifyLineItemDto item in order.Items)
                {
                    decimal lineGross = Round2( item.LineTotalGross );
                    if (lineGross <= 0) continue;
                    (decimal assignedRate, string reason) = ResolveVatRateForReportItem(
                        item.ShopifyProductId,
                        supplyVatRates
                    );
                    ShopifyClassifiedItemDto classified = new()
                    {
                        ShopifyProductId = NormalizeFromShopifyGid( item.ShopifyProductId, "gid://shopify/Product/" ).Trim(),
                        ProductTitle = item.Title,
                        ProductType = item.ProductType,
                        Quantity = item.Quantity,
                        UnitPrice = item.UnitPrice,
                        GrossAmount = lineGross,
                        AssignedVatRatePercent = assignedRate,
                        AssignmentReason = reason
                    };
                    if (assignedRate == 5m)
                    {
                        gross5 += lineGross;
                        items5.Add( classified );
                    }
                    else
                    {
                        gross23 += lineGross;
                        items23.Add( classified );
                    }
                }

                decimal totalGross = gross5 + gross23;
                if (totalGross <= 0m) continue;

                decimal originalOrderGross = Round2( totalGross + shippingGross );
                if (order.CurrentTotalGross > 0m && originalOrderGross > 0m && order.CurrentTotalGross < originalOrderGross)
                {
                    decimal ratio = order.CurrentTotalGross / originalOrderGross;
                    gross5 = Round2( gross5 * ratio );
                    gross23 = Round2( gross23 * ratio );
                    shippingGross = Round2( shippingGross * ratio );

                    decimal adjustedOrderGross = Round2( gross5 + gross23 + shippingGross );
                    decimal drift = Round2( order.CurrentTotalGross - adjustedOrderGross );
                    if (drift != 0m)
                    {
                        if (gross23 >= gross5 && gross23 > 0m) gross23 = Round2( Math.Max( 0m, gross23 + drift ) );
                        else if (gross5 > 0m) gross5 = Round2( Math.Max( 0m, gross5 + drift ) );
                        else shippingGross = Round2( Math.Max( 0m, shippingGross + drift ) );
                    }
                }

                totalGross = gross5 + gross23;
                if (totalGross <= 0m) continue;

                if (gross5 > 0m)
                {
                    decimal shippingFor5 = totalGross > 0m ? Round2( shippingGross * (gross5 / totalGross) ) : 0m;
                    rows.Add( BuildRow( order, 5m, gross5, shippingFor5, items5 ) );
                }

                if (gross23 > 0m)
                {
                    decimal shippingFor23 = totalGross > 0m ? Round2( shippingGross * (gross23 / totalGross) ) : 0m;
                    rows.Add( BuildRow( order, 23m, gross23, shippingFor23, items23 ) );
                }

                if (gross5 > 0m && gross23 > 0m && shippingGross > 0m)
                {
                    // Keep totals exact after rounding split percentages.
                    decimal assigned = rows.Where( r => r.ShopifyOrderId == order.OrderId ).Sum( r => r.ShippingGrossAmount );
                    decimal diff = Round2( shippingGross - assigned );
                    if (diff != 0m)
                    {
                        VatReportRow target = rows.Last( r => r.ShopifyOrderId == order.OrderId );
                        target.ShippingGrossAmount = Round2( target.ShippingGrossAmount + diff );
                        RecalculateAmounts( target );
                    }
                }
            }

            return rows;
        }

        private async Task<List<VatReportRow>> BuildForeignRowsAsync( int year, int month )
        {
            List<ShopifyOrderDto> orders = await FetchOrdersForForeignAsync( year, month );
            Dictionary<string, decimal> supplyVatRates = await GetSupplyVatRatesAsync();
            List<VatReportRow> rows = new();
            foreach (ShopifyOrderDto order in orders)
            {
                decimal shippingGross = Round2( order.ShippingGross );
                bool isEuDestination = IsEuCountryCode( order.CountryCode );
                Dictionary<decimal, decimal> grossByRate = new();
                Dictionary<decimal, List<ShopifyClassifiedItemDto>> itemsByRate = new();

                foreach (ShopifyLineItemDto item in order.Items)
                {
                    decimal lineGross = Round2( item.LineTotalGross );
                    if (lineGross <= 0m) continue;
                    (decimal classifiedRate, string classifiedReason) = ResolveVatRateForReportItem(
                        item.ShopifyProductId,
                        supplyVatRates
                    );
                    decimal assignedRate = !isEuDestination ? 0m : classifiedRate;
                    string reason = !isEuDestination ? "non-eu-destination" : classifiedReason;
                    if (!grossByRate.ContainsKey( assignedRate )) grossByRate[assignedRate] = 0m;
                    if (!itemsByRate.ContainsKey( assignedRate )) itemsByRate[assignedRate] = new List<ShopifyClassifiedItemDto>();
                    grossByRate[assignedRate] += lineGross;
                    itemsByRate[assignedRate].Add( new ShopifyClassifiedItemDto
                    {
                        ShopifyProductId = NormalizeFromShopifyGid( item.ShopifyProductId, "gid://shopify/Product/" ).Trim(),
                        ProductTitle = item.Title,
                        ProductType = item.ProductType,
                        Quantity = item.Quantity,
                        UnitPrice = item.UnitPrice,
                        GrossAmount = lineGross,
                        AssignedVatRatePercent = assignedRate,
                        AssignmentReason = reason
                    } );
                }

                decimal totalGross = grossByRate.Values.Sum();
                if (totalGross <= 0m) continue;
                foreach ((decimal rate, decimal goodsGross) in grossByRate.OrderBy( x => x.Key ))
                {
                    decimal shippingForRate = totalGross > 0m ? Round2( shippingGross * (goodsGross / totalGross) ) : 0m;
                    rows.Add( BuildRow( order, rate, goodsGross, shippingForRate, itemsByRate[rate] ) );
                }

                if (grossByRate.Count > 1 && shippingGross > 0m)
                {
                    decimal assigned = rows.Where( r => r.ShopifyOrderId == order.OrderId ).Sum( r => r.ShippingGrossAmount );
                    decimal diff = Round2( shippingGross - assigned );
                    if (diff != 0m)
                    {
                        VatReportRow target = rows.Last( r => r.ShopifyOrderId == order.OrderId );
                        target.ShippingGrossAmount = Round2( target.ShippingGrossAmount + diff );
                        RecalculateAmounts( target );
                    }
                }
            }

            return rows;
        }

        private static VatReportRow BuildRow(
            ShopifyOrderDto order,
            decimal vatRatePercent,
            decimal goodsGross,
            decimal shippingGross,
            List<ShopifyClassifiedItemDto> items
        )
        {
            decimal gross = Round2( goodsGross + shippingGross );
            decimal rate = vatRatePercent / 100m;
            decimal vat = Round2( gross * rate / (1m + rate) );
            decimal net = Round2( gross - vat );
            decimal netShipping = Round2( shippingGross / (1m + rate) );

            return new VatReportRow
            {
                ShopifyOrderId = order.OrderId,
                OrderNumber = order.OrderNumber,
                OrderDateUtc = order.CreatedAtUtc,
                VatRatePercent = vatRatePercent,
                GrossAmount = gross,
                VatAmount = vat,
                NetAmount = net,
                ShippingGrossAmount = Round2( shippingGross ),
                ShippingNetAmount = netShipping,
                Items = items
                    .Select( x => new VatReportRowItem
                    {
                        ShopifyProductId = string.IsNullOrWhiteSpace( x.ShopifyProductId ) ? string.Empty : x.ShopifyProductId,
                        ProductTitle = string.IsNullOrWhiteSpace( x.ProductTitle ) ? "—" : x.ProductTitle,
                        ProductType = x.ProductType ?? string.Empty,
                        Quantity = x.Quantity,
                        UnitPrice = Round2( x.UnitPrice ),
                        GrossAmount = Round2( x.GrossAmount ),
                        AssignedVatRatePercent = x.AssignedVatRatePercent,
                        AssignmentReason = x.AssignmentReason
                    } )
                    .ToList()
            };
        }

        private static void RecalculateAmounts( VatReportRow row )
        {
            decimal rate = row.VatRatePercent / 100m;
            row.ShippingNetAmount = Round2( row.ShippingGrossAmount / (1m + rate) );
            row.GrossAmount = Round2( row.GrossAmount );
            row.VatAmount = Round2( row.GrossAmount * rate / (1m + rate) );
            row.NetAmount = Round2( row.GrossAmount - row.VatAmount );
        }

        private async Task<List<ShopifyOrderDto>> FetchOrdersForPolandAsync( int year, int month )
        {
            string? shop = _httpContextAccessor.HttpContext?.User.FindFirst( "shop" )?.Value;
            string? accessToken = _httpContextAccessor.HttpContext?.User.FindFirst( "access_token" )?.Value;
            if (string.IsNullOrWhiteSpace( shop ) || string.IsNullOrWhiteSpace( accessToken ))
            {
                throw new InvalidOperationException( "Няма Shopify-кантэксту для генерацыі справаздачы." );
            }

            (DateTime from, DateTime to) = GetPolandMonthBoundsUtc( year, month );
            // Include Polish delivery and pickup orders (pickup often has no shipping_address).
            // Use explicit UTC timestamps and additionally validate period in code.
            string queryFilter = $"status:any created_at:>={from:yyyy-MM-ddTHH:mm:ssZ} created_at:<{to:yyyy-MM-ddTHH:mm:ssZ}";
            TimeZoneInfo polandTz = GetPolandTimeZone();

            List<ShopifyOrderDto> result = new();
            string? afterCursor = null;
            bool hasNextPage;

            using HttpClient client = new();
            do
            {
                const string query = """
                query OrdersPage($query: String!, $after: String) {
                  orders(first: 100, after: $after, sortKey: CREATED_AT, query: $query) {
                    edges {
                      cursor
                      node {
                        id
                        name
                        createdAt
                        currentTotalPriceSet {
                          shopMoney { amount }
                        }
                        shippingAddress { countryCodeV2 }
                        billingAddress { countryCodeV2 }
                        shippingLines(first: 20) {
                          nodes {
                            title
                            originalPriceSet { shopMoney { amount } }
                          }
                        }
                        lineItems(first: 250) {
                          nodes {
                            quantity
                            title
                            originalUnitPriceSet { shopMoney { amount } }
                            originalTotalSet { shopMoney { amount } }
                            discountedTotalSet { shopMoney { amount } }
                            discountAllocations {
                              allocatedAmountSet {
                                shopMoney { amount }
                              }
                            }
                            product {
                              id
                              productType
                            }
                            variant {
                              product {
                                id
                                productType
                              }
                            }
                          }
                        }
                      }
                    }
                    pageInfo { hasNextPage endCursor }
                  }
                }
                """;

                string payload = JsonSerializer.Serialize( new
                {
                    query,
                    variables = new { query = queryFilter, after = afterCursor }
                } );

                using HttpRequestMessage request = new(
                    HttpMethod.Post,
                    $"https://{shop}/admin/api/2024-10/graphql.json"
                );
                request.Headers.Add( "X-Shopify-Access-Token", accessToken );
                request.Content = new StringContent( payload, System.Text.Encoding.UTF8, "application/json" );

                using HttpResponseMessage response = await client.SendAsync( request );
                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync();
                    throw new InvalidOperationException( $"Не ўдалося атрымаць заказы Shopify: {body}" );
                }

                using JsonDocument json = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
                JsonElement orders = json.RootElement
                    .GetProperty( "data" )
                    .GetProperty( "orders" );

                foreach (JsonElement edge in orders.GetProperty( "edges" ).EnumerateArray())
                {
                    JsonElement node = edge.GetProperty( "node" );
                    string shippingCountryCode = node.TryGetProperty( "shippingAddress", out JsonElement shippingAddressEl ) &&
                                         shippingAddressEl.ValueKind == JsonValueKind.Object &&
                                         shippingAddressEl.TryGetProperty( "countryCodeV2", out JsonElement countryCodeEl ) &&
                                         countryCodeEl.ValueKind == JsonValueKind.String
                        ? (countryCodeEl.GetString() ?? string.Empty)
                        : string.Empty;
                    string billingCountryCode = node.TryGetProperty( "billingAddress", out JsonElement billingAddressEl ) &&
                                                billingAddressEl.ValueKind == JsonValueKind.Object &&
                                                billingAddressEl.TryGetProperty( "countryCodeV2", out JsonElement billingCountryCodeEl ) &&
                                                billingCountryCodeEl.ValueKind == JsonValueKind.String
                        ? (billingCountryCodeEl.GetString() ?? string.Empty)
                        : string.Empty;
                    bool hasPickupShippingLine = false;
                    bool hasZeroShippingLineWithTitle = false;
                    if (node.TryGetProperty( "shippingLines", out JsonElement checkShippingLinesEl ) &&
                        checkShippingLinesEl.ValueKind == JsonValueKind.Object &&
                        checkShippingLinesEl.TryGetProperty( "nodes", out JsonElement checkShippingNodesEl ) &&
                        checkShippingNodesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement shippingNode in checkShippingNodesEl.EnumerateArray())
                        {
                            if (shippingNode.TryGetProperty( "title", out JsonElement shippingTitleEl ) &&
                                shippingTitleEl.ValueKind == JsonValueKind.String)
                            {
                                string shippingTitle = (shippingTitleEl.GetString() ?? string.Empty).ToLowerInvariant();
                                decimal shippingLineAmount = ReadMoney( shippingNode, "originalPriceSet" );
                                if (!string.IsNullOrWhiteSpace( shippingTitle ) && shippingLineAmount == 0m)
                                {
                                    hasZeroShippingLineWithTitle = true;
                                }
                                if (shippingTitle.Contains( "pickup" ) ||
                                    shippingTitle.Contains( "odbiór" ) ||
                                    shippingTitle.Contains( "odbior" ) ||
                                    shippingTitle.Contains( "самовывоз" ))
                                {
                                    hasPickupShippingLine = true;
                                    break;
                                }
                            }
                        }
                    }

                    bool isPolandOrder =
                        string.Equals( shippingCountryCode, "PL", StringComparison.OrdinalIgnoreCase ) ||
                        (string.IsNullOrWhiteSpace( shippingCountryCode ) &&
                         string.Equals( billingCountryCode, "PL", StringComparison.OrdinalIgnoreCase )) ||
                        hasPickupShippingLine ||
                        (string.IsNullOrWhiteSpace( shippingCountryCode ) && hasZeroShippingLineWithTitle);
                    if (!isPolandOrder) continue;

                    string orderId = node.TryGetProperty( "id", out JsonElement idEl ) && idEl.ValueKind == JsonValueKind.String
                        ? NormalizeFromShopifyGid( idEl.GetString() ?? string.Empty, "gid://shopify/Order/" )
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace( orderId )) continue;

                    string orderNumber = node.TryGetProperty( "name", out JsonElement nameEl ) && nameEl.ValueKind == JsonValueKind.String
                        ? (nameEl.GetString() ?? orderId)
                        : orderId;
                    DateTime createdAt = node.TryGetProperty( "createdAt", out JsonElement createdAtEl ) &&
                                         createdAtEl.ValueKind == JsonValueKind.String &&
                                         DateTime.TryParse( createdAtEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime parsedCreatedAt )
                        ? parsedCreatedAt
                        : DateTime.UtcNow;
                    DateTime createdAtPoland = TimeZoneInfo.ConvertTimeFromUtc( createdAt, polandTz );
                    if (createdAtPoland.Year != year || createdAtPoland.Month != month) continue;

                    decimal shippingGross = 0m;
                    if (node.TryGetProperty( "shippingLines", out JsonElement shippingLinesEl ) &&
                        shippingLinesEl.ValueKind == JsonValueKind.Object &&
                        shippingLinesEl.TryGetProperty( "nodes", out JsonElement shippingNodesEl ) &&
                        shippingNodesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement shippingNode in shippingNodesEl.EnumerateArray())
                        {
                            shippingGross += ReadMoney( shippingNode, "originalPriceSet" );
                        }
                    }

                    List<ShopifyLineItemDto> items = new();
                    if (node.TryGetProperty( "lineItems", out JsonElement lineItemsEl ) &&
                        lineItemsEl.ValueKind == JsonValueKind.Object &&
                        lineItemsEl.TryGetProperty( "nodes", out JsonElement itemNodesEl ) &&
                        itemNodesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement itemNode in itemNodesEl.EnumerateArray())
                        {
                            int quantity = itemNode.TryGetProperty( "quantity", out JsonElement qtyEl ) &&
                                           qtyEl.ValueKind == JsonValueKind.Number &&
                                           qtyEl.TryGetInt32( out int parsedQty )
                                ? parsedQty
                                : 0;
                            if (quantity <= 0) continue;

                            string title = itemNode.TryGetProperty( "title", out JsonElement titleEl ) &&
                                           titleEl.ValueKind == JsonValueKind.String
                                ? (titleEl.GetString() ?? string.Empty)
                                : string.Empty;
                            string productId = string.Empty;
                            string productType = string.Empty;
                            if (itemNode.TryGetProperty( "product", out JsonElement lineProductEl ) &&
                                lineProductEl.ValueKind == JsonValueKind.Object)
                            {
                                if (lineProductEl.TryGetProperty( "id", out JsonElement lineProductIdEl ) &&
                                    lineProductIdEl.ValueKind == JsonValueKind.String)
                                {
                                    productId = NormalizeFromShopifyGid(
                                        lineProductIdEl.GetString() ?? string.Empty,
                                        "gid://shopify/Product/"
                                    );
                                }
                                if (lineProductEl.TryGetProperty( "productType", out JsonElement lineProductTypeEl ) &&
                                    lineProductTypeEl.ValueKind == JsonValueKind.String)
                                {
                                    productType = lineProductTypeEl.GetString() ?? string.Empty;
                                }
                            }
                            if ((string.IsNullOrWhiteSpace( productType ) || string.IsNullOrWhiteSpace( productId )) &&
                                itemNode.TryGetProperty( "variant", out JsonElement variantEl ) &&
                                variantEl.ValueKind == JsonValueKind.Object &&
                                variantEl.TryGetProperty( "product", out JsonElement variantProductEl ) &&
                                variantProductEl.ValueKind == JsonValueKind.Object)
                            {
                                if (string.IsNullOrWhiteSpace( productId ) &&
                                    variantProductEl.TryGetProperty( "id", out JsonElement variantProductIdEl ) &&
                                    variantProductIdEl.ValueKind == JsonValueKind.String)
                                {
                                    productId = NormalizeFromShopifyGid(
                                        variantProductIdEl.GetString() ?? string.Empty,
                                        "gid://shopify/Product/"
                                    );
                                }
                                if (string.IsNullOrWhiteSpace( productType ) &&
                                    variantProductEl.TryGetProperty( "productType", out JsonElement variantProductTypeEl ) &&
                                    variantProductTypeEl.ValueKind == JsonValueKind.String)
                                {
                                    productType = variantProductTypeEl.GetString() ?? string.Empty;
                                }
                            }
                            decimal unitPrice = ReadMoney( itemNode, "originalUnitPriceSet" );
                            decimal originalTotal = ReadMoney( itemNode, "originalTotalSet" );
                            decimal discountedTotal = ReadMoney( itemNode, "discountedTotalSet" );
                            decimal lineTotalGross = originalTotal > 0m ? originalTotal : unitPrice * quantity;
                            if (lineTotalGross <= 0m && discountedTotal > 0m)
                            {
                                lineTotalGross = discountedTotal;
                            }
                            if (lineTotalGross <= 0m) continue;
                            if (unitPrice <= 0m)
                            {
                                unitPrice = quantity > 0 ? Round2( lineTotalGross / quantity ) : 0m;
                            }
                            decimal allocatedDiscountTotal = 0m;
                            if (itemNode.TryGetProperty( "discountAllocations", out JsonElement discountAllocationsEl ) &&
                                discountAllocationsEl.ValueKind == JsonValueKind.Array)
                            {
                                foreach (JsonElement allocationEl in discountAllocationsEl.EnumerateArray())
                                {
                                    if (allocationEl.TryGetProperty( "allocatedAmountSet", out JsonElement amountSetEl ) &&
                                        amountSetEl.ValueKind == JsonValueKind.Object &&
                                        amountSetEl.TryGetProperty( "shopMoney", out JsonElement shopMoneyEl ) &&
                                        shopMoneyEl.ValueKind == JsonValueKind.Object &&
                                        shopMoneyEl.TryGetProperty( "amount", out JsonElement amountEl ) &&
                                        amountEl.ValueKind == JsonValueKind.String &&
                                        decimal.TryParse(
                                            amountEl.GetString(),
                                            NumberStyles.Number,
                                            CultureInfo.InvariantCulture,
                                            out decimal parsedAllocation))
                                    {
                                        allocatedDiscountTotal += parsedAllocation;
                                    }
                                }
                            }
                            if (allocatedDiscountTotal > 0m)
                            {
                                lineTotalGross = Math.Max( 0m, lineTotalGross - allocatedDiscountTotal );
                            }
                            else if (discountedTotal > 0m && discountedTotal < lineTotalGross)
                            {
                                lineTotalGross = discountedTotal;
                            }
                            items.Add( new ShopifyLineItemDto
                            {
                                ShopifyProductId = productId,
                                Quantity = quantity,
                                UnitPrice = unitPrice,
                                LineTotalGross = Round2( lineTotalGross ),
                                ProductType = productType,
                                Title = title
                            } );
                        }
                    }

                    if (items.Count == 0) continue;

                    result.Add( new ShopifyOrderDto
                    {
                        OrderId = orderId,
                        OrderNumber = orderNumber,
                        CreatedAtUtc = createdAt,
                        CurrentTotalGross = ReadMoney( node, "currentTotalPriceSet" ),
                        ShippingGross = Round2( shippingGross ),
                        Items = items
                    } );
                }

                JsonElement pageInfo = orders.GetProperty( "pageInfo" );
                hasNextPage = pageInfo.GetProperty( "hasNextPage" ).GetBoolean();
                afterCursor = pageInfo.GetProperty( "endCursor" ).GetString();
            } while (hasNextPage && !string.IsNullOrWhiteSpace( afterCursor ));

            return result;
        }

        private async Task<List<ShopifyOrderDto>> FetchOrdersForForeignAsync( int year, int month )
        {
            string? shop = _httpContextAccessor.HttpContext?.User.FindFirst( "shop" )?.Value;
            string? accessToken = _httpContextAccessor.HttpContext?.User.FindFirst( "access_token" )?.Value;
            if (string.IsNullOrWhiteSpace( shop ) || string.IsNullOrWhiteSpace( accessToken ))
            {
                throw new InvalidOperationException( "Няма Shopify-кантэксту для генерацыі справаздачы." );
            }

            // For foreign reports fetch all statuses and filter month in code.
            // This avoids Shopify created_at query edge cases around timezone boundaries.
            string queryFilter = "status:any";
            List<ShopifyOrderDto> result = new();
            string? afterCursor = null;
            bool hasNextPage;
            using HttpClient client = new();
            TimeZoneInfo polandTz = GetPolandTimeZone();
            do
            {
                const string query = """
                query OrdersPage($query: String!, $after: String) {
                  orders(first: 100, after: $after, sortKey: CREATED_AT, query: $query) {
                    edges {
                      cursor
                      node {
                        id
                        name
                        createdAt
                        shippingAddress { countryCodeV2 }
                        billingAddress { countryCodeV2 }
                        shippingLines(first: 20) {
                          nodes {
                            title
                            originalPriceSet { shopMoney { amount } }
                          }
                        }
                        lineItems(first: 250) {
                          nodes {
                            quantity
                            title
                            originalUnitPriceSet { shopMoney { amount } }
                            originalTotalSet { shopMoney { amount } }
                            discountedTotalSet { shopMoney { amount } }
                            discountAllocations {
                              allocatedAmountSet { shopMoney { amount } }
                            }
                            product {
                              id
                              productType
                            }
                            variant {
                              product {
                                id
                                productType
                              }
                            }
                          }
                        }
                      }
                    }
                    pageInfo { hasNextPage endCursor }
                  }
                }
                """;
                string payload = JsonSerializer.Serialize( new { query, variables = new { query = queryFilter, after = afterCursor } } );
                using HttpRequestMessage request = new( HttpMethod.Post, $"https://{shop}/admin/api/2024-10/graphql.json" );
                request.Headers.Add( "X-Shopify-Access-Token", accessToken );
                request.Content = new StringContent( payload, System.Text.Encoding.UTF8, "application/json" );
                using HttpResponseMessage response = await client.SendAsync( request );
                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync();
                    throw new InvalidOperationException( $"Не ўдалося атрымаць заказы Shopify: {body}" );
                }

                using JsonDocument json = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
                JsonElement orders = json.RootElement.GetProperty( "data" ).GetProperty( "orders" );
                foreach (JsonElement edge in orders.GetProperty( "edges" ).EnumerateArray())
                {
                    JsonElement node = edge.GetProperty( "node" );
                    string shippingCountryCode = node.TryGetProperty( "shippingAddress", out JsonElement shippingAddressEl ) &&
                                                 shippingAddressEl.ValueKind == JsonValueKind.Object &&
                                                 shippingAddressEl.TryGetProperty( "countryCodeV2", out JsonElement countryCodeEl ) &&
                                                 countryCodeEl.ValueKind == JsonValueKind.String
                        ? (countryCodeEl.GetString() ?? string.Empty)
                        : string.Empty;
                    string billingCountryCode = node.TryGetProperty( "billingAddress", out JsonElement billingAddressEl ) &&
                                                billingAddressEl.ValueKind == JsonValueKind.Object &&
                                                billingAddressEl.TryGetProperty( "countryCodeV2", out JsonElement billingCountryCodeEl ) &&
                                                billingCountryCodeEl.ValueKind == JsonValueKind.String
                        ? (billingCountryCodeEl.GetString() ?? string.Empty)
                        : string.Empty;
                    bool hasPickupShippingLine = false;
                    bool hasZeroShippingLineWithTitle = false;
                    if (node.TryGetProperty( "shippingLines", out JsonElement checkShippingLinesEl ) &&
                        checkShippingLinesEl.ValueKind == JsonValueKind.Object &&
                        checkShippingLinesEl.TryGetProperty( "nodes", out JsonElement checkShippingNodesEl ) &&
                        checkShippingNodesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement shippingNode in checkShippingNodesEl.EnumerateArray())
                        {
                            if (shippingNode.TryGetProperty( "title", out JsonElement shippingTitleEl ) &&
                                shippingTitleEl.ValueKind == JsonValueKind.String)
                            {
                                string shippingTitle = (shippingTitleEl.GetString() ?? string.Empty).ToLowerInvariant();
                                decimal shippingLineAmount = ReadMoney( shippingNode, "originalPriceSet" );
                                if (!string.IsNullOrWhiteSpace( shippingTitle ) && shippingLineAmount == 0m)
                                {
                                    hasZeroShippingLineWithTitle = true;
                                }
                                if (shippingTitle.Contains( "pickup" ) ||
                                    shippingTitle.Contains( "pick up" ) ||
                                    shippingTitle.Contains( "odbiór" ) ||
                                    shippingTitle.Contains( "odbior" ) ||
                                    shippingTitle.Contains( "самовывоз" ))
                                {
                                    hasPickupShippingLine = true;
                                    break;
                                }
                            }
                        }
                    }
                    string countryCode = string.IsNullOrWhiteSpace( shippingCountryCode ) ? billingCountryCode : shippingCountryCode;
                    if (hasPickupShippingLine || (string.IsNullOrWhiteSpace( shippingCountryCode ) && hasZeroShippingLineWithTitle))
                    {
                        // Local pickup should be treated as Poland flow, not foreign.
                        continue;
                    }
                    if (string.Equals( countryCode, "PL", StringComparison.OrdinalIgnoreCase )) continue;

                    string orderId = node.TryGetProperty( "id", out JsonElement idEl ) && idEl.ValueKind == JsonValueKind.String
                        ? NormalizeFromShopifyGid( idEl.GetString() ?? string.Empty, "gid://shopify/Order/" )
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace( orderId )) continue;
                    string orderNumber = node.TryGetProperty( "name", out JsonElement nameEl ) && nameEl.ValueKind == JsonValueKind.String
                        ? (nameEl.GetString() ?? orderId)
                        : orderId;
                    DateTimeOffset parsedCreatedAtOffset = DateTimeOffset.MinValue;
                    bool parsedCreated = node.TryGetProperty( "createdAt", out JsonElement createdAtEl ) &&
                                         createdAtEl.ValueKind == JsonValueKind.String &&
                                         DateTimeOffset.TryParse(
                                             createdAtEl.GetString(),
                                             CultureInfo.InvariantCulture,
                                             DateTimeStyles.None,
                                             out parsedCreatedAtOffset
                                         );
                    DateTimeOffset createdAtOffset = parsedCreated
                        ? parsedCreatedAtOffset
                        : DateTimeOffset.UtcNow;
                    DateTime createdAt = createdAtOffset.UtcDateTime;
                    DateTime createdAtPoland = TimeZoneInfo.ConvertTimeFromUtc( createdAt, polandTz );
                    bool inRequestedMonthByPoland = createdAtPoland.Year == year && createdAtPoland.Month == month;
                    bool inRequestedMonthByUtc = createdAt.Year == year && createdAt.Month == month;
                    bool inRequestedMonthByOrderLocal =
                        createdAtOffset.Year == year && createdAtOffset.Month == month;
                    if (!inRequestedMonthByPoland && !inRequestedMonthByUtc && !inRequestedMonthByOrderLocal) continue;

                    decimal shippingGross = 0m;
                    if (node.TryGetProperty( "shippingLines", out JsonElement shippingLinesEl ) &&
                        shippingLinesEl.ValueKind == JsonValueKind.Object &&
                        shippingLinesEl.TryGetProperty( "nodes", out JsonElement shippingNodesEl ) &&
                        shippingNodesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement shippingNode in shippingNodesEl.EnumerateArray())
                        {
                            shippingGross += ReadMoney( shippingNode, "originalPriceSet" );
                        }
                    }

                    List<ShopifyLineItemDto> items = new();
                    if (node.TryGetProperty( "lineItems", out JsonElement lineItemsEl ) &&
                        lineItemsEl.ValueKind == JsonValueKind.Object &&
                        lineItemsEl.TryGetProperty( "nodes", out JsonElement itemNodesEl ) &&
                        itemNodesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement itemNode in itemNodesEl.EnumerateArray())
                        {
                            int quantity = itemNode.TryGetProperty( "quantity", out JsonElement qtyEl ) &&
                                           qtyEl.ValueKind == JsonValueKind.Number &&
                                           qtyEl.TryGetInt32( out int parsedQty )
                                ? parsedQty
                                : 0;
                            if (quantity <= 0) continue;
                            string title = itemNode.TryGetProperty( "title", out JsonElement titleEl ) &&
                                           titleEl.ValueKind == JsonValueKind.String
                                ? (titleEl.GetString() ?? string.Empty)
                                : string.Empty;
                            string productId = string.Empty;
                            string productType = string.Empty;
                            if (itemNode.TryGetProperty( "product", out JsonElement lineProductEl ) &&
                                lineProductEl.ValueKind == JsonValueKind.Object)
                            {
                                if (lineProductEl.TryGetProperty( "id", out JsonElement lineProductIdEl ) &&
                                    lineProductIdEl.ValueKind == JsonValueKind.String)
                                {
                                    productId = NormalizeFromShopifyGid(
                                        lineProductIdEl.GetString() ?? string.Empty,
                                        "gid://shopify/Product/"
                                    );
                                }
                                if (lineProductEl.TryGetProperty( "productType", out JsonElement lineProductTypeEl ) &&
                                    lineProductTypeEl.ValueKind == JsonValueKind.String)
                                {
                                    productType = lineProductTypeEl.GetString() ?? string.Empty;
                                }
                            }
                            if ((string.IsNullOrWhiteSpace( productType ) || string.IsNullOrWhiteSpace( productId )) &&
                                itemNode.TryGetProperty( "variant", out JsonElement variantEl ) &&
                                variantEl.ValueKind == JsonValueKind.Object &&
                                variantEl.TryGetProperty( "product", out JsonElement variantProductEl ) &&
                                variantProductEl.ValueKind == JsonValueKind.Object)
                            {
                                if (string.IsNullOrWhiteSpace( productId ) &&
                                    variantProductEl.TryGetProperty( "id", out JsonElement variantProductIdEl ) &&
                                    variantProductIdEl.ValueKind == JsonValueKind.String)
                                {
                                    productId = NormalizeFromShopifyGid(
                                        variantProductIdEl.GetString() ?? string.Empty,
                                        "gid://shopify/Product/"
                                    );
                                }
                                if (string.IsNullOrWhiteSpace( productType ) &&
                                    variantProductEl.TryGetProperty( "productType", out JsonElement variantProductTypeEl ) &&
                                    variantProductTypeEl.ValueKind == JsonValueKind.String)
                                {
                                    productType = variantProductTypeEl.GetString() ?? string.Empty;
                                }
                            }
                            decimal unitPrice = ReadMoney( itemNode, "originalUnitPriceSet" );
                            decimal originalTotal = ReadMoney( itemNode, "originalTotalSet" );
                            decimal discountedTotal = ReadMoney( itemNode, "discountedTotalSet" );
                            decimal lineTotalGross = originalTotal > 0m ? originalTotal : unitPrice * quantity;
                            if (lineTotalGross <= 0m && discountedTotal > 0m)
                            {
                                lineTotalGross = discountedTotal;
                            }
                            if (lineTotalGross <= 0m) continue;
                            if (unitPrice <= 0m)
                            {
                                unitPrice = quantity > 0 ? Round2( lineTotalGross / quantity ) : 0m;
                            }
                            decimal allocatedDiscountTotal = 0m;
                            if (itemNode.TryGetProperty( "discountAllocations", out JsonElement discountAllocationsEl ) &&
                                discountAllocationsEl.ValueKind == JsonValueKind.Array)
                            {
                                foreach (JsonElement allocationEl in discountAllocationsEl.EnumerateArray())
                                {
                                    if (allocationEl.TryGetProperty( "allocatedAmountSet", out JsonElement amountSetEl ) &&
                                        amountSetEl.ValueKind == JsonValueKind.Object &&
                                        amountSetEl.TryGetProperty( "shopMoney", out JsonElement shopMoneyEl ) &&
                                        shopMoneyEl.ValueKind == JsonValueKind.Object &&
                                        shopMoneyEl.TryGetProperty( "amount", out JsonElement amountEl ) &&
                                        amountEl.ValueKind == JsonValueKind.String &&
                                        decimal.TryParse(
                                            amountEl.GetString(),
                                            NumberStyles.Number,
                                            CultureInfo.InvariantCulture,
                                            out decimal parsedAllocation))
                                    {
                                        allocatedDiscountTotal += parsedAllocation;
                                    }
                                }
                            }
                            if (allocatedDiscountTotal > 0m) lineTotalGross = Math.Max( 0m, lineTotalGross - allocatedDiscountTotal );
                            else if (discountedTotal > 0m && discountedTotal < lineTotalGross) lineTotalGross = discountedTotal;

                            items.Add( new ShopifyLineItemDto
                            {
                                ShopifyProductId = productId,
                                Quantity = quantity,
                                UnitPrice = unitPrice,
                                LineTotalGross = Round2( lineTotalGross ),
                                ProductType = productType,
                                Title = title
                            } );
                        }
                    }
                    if (items.Count == 0) continue;
                    result.Add( new ShopifyOrderDto
                    {
                        OrderId = orderId,
                        OrderNumber = orderNumber,
                        CreatedAtUtc = createdAt,
                        ShippingGross = Round2( shippingGross ),
                        Items = items,
                        CountryCode = countryCode
                    } );
                }

                JsonElement pageInfo = orders.GetProperty( "pageInfo" );
                hasNextPage = pageInfo.GetProperty( "hasNextPage" ).GetBoolean();
                afterCursor = pageInfo.GetProperty( "endCursor" ).GetString();
            } while (hasNextPage && !string.IsNullOrWhiteSpace( afterCursor ));

            return result;
        }

        private static TimeZoneInfo GetPolandTimeZone()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById( "Europe/Warsaw" );
            }
            catch
            {
                // Windows fallback
                return TimeZoneInfo.FindSystemTimeZoneById( "Central European Standard Time" );
            }
        }

        private static (DateTime fromUtc, DateTime toUtc) GetPolandMonthBoundsUtc( int year, int month )
        {
            TimeZoneInfo polandTz = GetPolandTimeZone();
            DateTime localFrom = new( year, month, 1, 0, 0, 0, DateTimeKind.Unspecified );
            DateTime localTo = localFrom.AddMonths( 1 );
            DateTime fromUtc = TimeZoneInfo.ConvertTimeToUtc( localFrom, polandTz );
            DateTime toUtc = TimeZoneInfo.ConvertTimeToUtc( localTo, polandTz );
            return (fromUtc, toUtc);
        }

        private static decimal ReadMoney( JsonElement node, string setProperty )
        {
            if (!node.TryGetProperty( setProperty, out JsonElement priceSetEl ) ||
                priceSetEl.ValueKind != JsonValueKind.Object ||
                !priceSetEl.TryGetProperty( "shopMoney", out JsonElement moneyEl ) ||
                moneyEl.ValueKind != JsonValueKind.Object ||
                !moneyEl.TryGetProperty( "amount", out JsonElement amountEl ) ||
                amountEl.ValueKind != JsonValueKind.String)
            {
                return 0m;
            }
            return decimal.TryParse(
                amountEl.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal value
            ) ? value : 0m;
        }

        private async Task<Dictionary<string, ForeignDeliveryInfo>> FetchForeignDeliveryInfoAsync( List<string> orderIds )
        {
            Dictionary<string, ForeignDeliveryInfo> result = new( StringComparer.OrdinalIgnoreCase );
            if (orderIds.Count == 0) return result;
            string? shop = _httpContextAccessor.HttpContext?.User.FindFirst( "shop" )?.Value;
            string? accessToken = _httpContextAccessor.HttpContext?.User.FindFirst( "access_token" )?.Value;
            if (string.IsNullOrWhiteSpace( shop ) || string.IsNullOrWhiteSpace( accessToken )) return result;
            using HttpClient client = new();
            const int batchSize = 50;
            for (int i = 0; i < orderIds.Count; i += batchSize)
            {
                List<string> batch = orderIds.Skip( i ).Take( batchSize ).ToList();
                string[] gids = batch.Select( id => $"gid://shopify/Order/{id}" ).ToArray();
                const string query = """
                query OrderNodes($ids:[ID!]!) {
                  nodes(ids:$ids) {
                    ... on Order {
                      id
                      shippingAddress { firstName lastName address1 address2 city zip country countryCodeV2 }
                      billingAddress { firstName lastName address1 address2 city zip country countryCodeV2 }
                    }
                  }
                }
                """;
                string payload = JsonSerializer.Serialize( new { query, variables = new { ids = gids } } );
                using HttpRequestMessage request = new( HttpMethod.Post, $"https://{shop}/admin/api/2024-10/graphql.json" );
                request.Headers.Add( "X-Shopify-Access-Token", accessToken );
                request.Content = new StringContent( payload, System.Text.Encoding.UTF8, "application/json" );
                using HttpResponseMessage response = await client.SendAsync( request );
                if (!response.IsSuccessStatusCode) continue;
                using JsonDocument json = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
                if (!json.RootElement.TryGetProperty( "data", out JsonElement dataEl ) ||
                    !dataEl.TryGetProperty( "nodes", out JsonElement nodesEl ) ||
                    nodesEl.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement node in nodesEl.EnumerateArray())
                {
                    if (node.ValueKind != JsonValueKind.Object) continue;
                    if (!node.TryGetProperty( "id", out JsonElement idEl ) || idEl.ValueKind != JsonValueKind.String) continue;
                    string orderId = NormalizeFromShopifyGid( idEl.GetString() ?? string.Empty, "gid://shopify/Order/" );
                    if (string.IsNullOrWhiteSpace( orderId )) continue;
                    JsonElement shippingAddr = node.TryGetProperty( "shippingAddress", out JsonElement shippingEl ) && shippingEl.ValueKind == JsonValueKind.Object
                        ? shippingEl
                        : default;
                    JsonElement billingAddr = node.TryGetProperty( "billingAddress", out JsonElement billingEl ) && billingEl.ValueKind == JsonValueKind.Object
                        ? billingEl
                        : default;
                    JsonElement addr = shippingAddr.ValueKind == JsonValueKind.Object ? shippingAddr : billingAddr;
                    string firstName = ReadString( addr, "firstName" );
                    string lastName = ReadString( addr, "lastName" );
                    string name = $"{firstName} {lastName}".Trim();
                    string shippingAddress = FormatAddress( shippingAddr );
                    string billingAddress = FormatAddress( billingAddr );
                    result[orderId] = new ForeignDeliveryInfo
                    {
                        Name = name,
                        ShippingAddress = shippingAddress,
                        BillingAddress = billingAddress
                    };
                }
            }

            return result;
        }

        private static string FormatAddress( JsonElement addr )
        {
            if (addr.ValueKind != JsonValueKind.Object) return string.Empty;
            return string.Join( ", ", new[]
            {
                ReadString( addr, "address1" ),
                ReadString( addr, "address2" ),
                ReadString( addr, "city" ),
                ReadString( addr, "zip" ),
                ReadString( addr, "country" )
            }.Where( x => !string.IsNullOrWhiteSpace( x ) ));
        }

        private static string ReadString( JsonElement node, string prop )
        {
            if (node.ValueKind != JsonValueKind.Object) return string.Empty;
            return node.TryGetProperty( prop, out JsonElement valueEl ) && valueEl.ValueKind == JsonValueKind.String
                ? (valueEl.GetString() ?? string.Empty)
                : string.Empty;
        }

        private static bool IsEuCountryCode( string? countryCode )
        {
            if (string.IsNullOrWhiteSpace( countryCode )) return false;
            return countryCode.Trim().ToUpperInvariant() switch
            {
                "AT" or "BE" or "BG" or "HR" or "CY" or "CZ" or "DK" or "EE" or "FI" or "FR" or "DE" or "GR" or "HU" or "IE" or
                "IT" or "LV" or "LT" or "LU" or "MT" or "NL" or "PL" or "PT" or "RO" or "SK" or "SI" or "ES" or "SE" => true,
                _ => false
            };
        }

        private static bool IsBookProduct( string productType, string title )
        {
            string source = $"{productType} {title}".ToLowerInvariant();
            if (source.Contains( "закладк" ) || source.Contains( "bookmark" ))
            {
                return false;
            }
            return source.Contains( "book" ) ||
                   source.Contains( "кніг" ) ||
                   source.Contains( "книг" ) ||
                   source.Contains( "часопіс" ) ||
                   source.Contains( "часопiс" ) ||
                   source.Contains( "часописы" ) ||
                   source.Contains( "часопiсы" ) ||
                   source.Contains( "журнал" ) ||
                   source.Contains( "książ" ) ||
                   source.Contains( "ksiaz" );
        }

        private static (decimal rate, string reason) ClassifyVatRate( string productType, string title )
        {
            if (IsBookProduct( productType, title ))
            {
                return (5m, "book-rule");
            }
            return (23m, "default-non-book");
        }

        private static (decimal rate, string reason) ResolveVatRateForReportItem(
            string shopifyProductId,
            IReadOnlyDictionary<string, decimal> supplyVatRates
        )
        {
            string normalizedId = NormalizeFromShopifyGid( shopifyProductId, "gid://shopify/Product/" ).Trim();
            if (!string.IsNullOrWhiteSpace( normalizedId ) &&
                supplyVatRates.TryGetValue( normalizedId, out decimal configuredRate ))
            {
                return (configuredRate, "supply-vat-rate");
            }

            return (23m, "default-supply-rate");
        }

        private async Task<Dictionary<string, decimal>> GetSupplyVatRatesAsync()
        {
            var rows = await _db.SupplyProducts
                .AsNoTracking()
                .Where( p => !string.IsNullOrWhiteSpace( p.ShopifyProductId ) )
                .Select( p => new
                {
                    ProductId = p.ShopifyProductId,
                    VatRate = p.VatRatePercent,
                    Date = p.Supply.Date,
                    p.SupplyId,
                    RowId = p.Id
                } )
                .ToListAsync();

            return rows
                .GroupBy(
                    x => NormalizeFromShopifyGid( x.ProductId, "gid://shopify/Product/" ).Trim(),
                    StringComparer.OrdinalIgnoreCase
                )
                .Where( g => !string.IsNullOrWhiteSpace( g.Key ) )
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .OrderByDescending( x => x.Date )
                        .ThenByDescending( x => x.SupplyId )
                        .ThenByDescending( x => x.RowId )
                        .First().VatRate,
                    StringComparer.OrdinalIgnoreCase
                );
        }

        private static string NormalizeFromShopifyGid( string id, string prefix )
        {
            return id.StartsWith( prefix, StringComparison.OrdinalIgnoreCase )
                ? id[prefix.Length..]
                : id;
        }

        private static string NormalizeReportType( string? reportType )
        {
            if (string.IsNullOrWhiteSpace( reportType )) return VatReportType.Poland;
            string normalized = reportType.Trim().ToLowerInvariant();
            return normalized switch
            {
                VatReportType.Poland => VatReportType.Poland,
                VatReportType.Foreign => VatReportType.Foreign,
                _ => throw new InvalidOperationException( "Тып справаздачы павінен быць poland або foreign." )
            };
        }

        private static string BuildReportName( string type, int year, int month )
        {
            string prefix = type == VatReportType.Poland ? "Польшча" : "Замежжа";
            return $"{prefix} {year:D4}-{month:D2}";
        }

        private static string EncodeOrderNumberWithContact( string orderNumberBase, string deliveryName, string deliveryAddress )
        {
            string order = (orderNumberBase ?? string.Empty).Trim();
            string name = (deliveryName ?? string.Empty).Trim();
            string address = (deliveryAddress ?? string.Empty).Trim();
            string encoded = $"{order} || {name} || {address}";
            if (encoded.Length <= 64) return encoded;

            int available = Math.Max( 0, 64 - order.Length - 8 );
            string clippedName = name[..Math.Min( name.Length, available )];
            available = Math.Max( 0, 64 - order.Length - clippedName.Length - 8 );
            string clippedAddress = address[..Math.Min( address.Length, available )];
            encoded = $"{order} || {clippedName} || {clippedAddress}";
            return encoded.Length <= 64 ? encoded : order[..Math.Min( order.Length, 64 )];
        }

        private static (string orderNumber, string deliveryName, string deliveryAddress) ParseOrderNumberAndContact( string orderNumberRaw )
        {
            if (string.IsNullOrWhiteSpace( orderNumberRaw )) return (string.Empty, string.Empty, string.Empty);
            string[] parts = orderNumberRaw.Split( "||", StringSplitOptions.TrimEntries );
            if (parts.Length >= 3)
            {
                return (parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
            }
            if (parts.Length == 2)
            {
                return (parts[0].Trim(), string.Empty, parts[1].Trim());
            }
            return (orderNumberRaw.Trim(), string.Empty, string.Empty);
        }

        private static void ValidatePeriod( int year, int month )
        {
            if (month < 1 || month > 12)
            {
                throw new InvalidOperationException( "Месяц павінен быць у дыяпазоне 1..12." );
            }
            if (year < 2000 || year > 3000)
            {
                throw new InvalidOperationException( "Некарэктны год справаздачы." );
            }
        }

        private static decimal Round2( decimal value ) =>
            Math.Round( value, 2, MidpointRounding.AwayFromZero );

        private sealed class ShopifyOrderDto
        {
            public string OrderId { get; set; } = string.Empty;
            public string OrderNumber { get; set; } = string.Empty;
            public DateTime CreatedAtUtc { get; set; }
            public decimal CurrentTotalGross { get; set; }
            public decimal ShippingGross { get; set; }
            public string CountryCode { get; set; } = string.Empty;
            public List<ShopifyLineItemDto> Items { get; set; } = new();
        }

        private sealed class ShopifyLineItemDto
        {
            public string ShopifyProductId { get; set; } = string.Empty;
            public int Quantity { get; set; }
            public decimal UnitPrice { get; set; }
            public decimal LineTotalGross { get; set; }
            public string ProductType { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
        }

        private sealed class ShopifyClassifiedItemDto
        {
            public string ShopifyProductId { get; set; } = string.Empty;
            public string ProductTitle { get; set; } = string.Empty;
            public string ProductType { get; set; } = string.Empty;
            public int Quantity { get; set; }
            public decimal UnitPrice { get; set; }
            public decimal GrossAmount { get; set; }
            public decimal AssignedVatRatePercent { get; set; }
            public string AssignmentReason { get; set; } = string.Empty;
        }

        private sealed class ForeignDeliveryInfo
        {
            public string Name { get; set; } = string.Empty;
            public string ShippingAddress { get; set; } = string.Empty;
            public string BillingAddress { get; set; } = string.Empty;
        }
    }
}
