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
                VatReportType.Foreign => new List<VatReportRow>(),
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
                VatReportType.Foreign => new List<VatReportRow>(),
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
                                Items = x.Items
                                    .Select( i => new VatReportDetailsPolandItem
                                    {
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
            }
            else
            {
                rows = report.Rows
                    .GroupBy( r => r.ShopifyOrderId )
                    .Select( g => new VatReportDetailsSummaryRow
                    {
                        Type = report.Type,
                        Name = g.First().OrderNumber ?? g.Key,
                        ShopifyOrderId = g.Key,
                        Vat = Round2( g.Sum( x => x.VatAmount ) ),
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
                                Items = x.Items
                                    .Select( i => new VatReportDetailsPolandItem
                                    {
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

        public async Task UpdateRowAsync(
            int rowId,
            decimal vatRatePercent,
            decimal grossAmount,
            decimal vatAmount,
            decimal netAmount
        )
        {
            if (vatRatePercent != 5m && vatRatePercent != 23m)
            {
                throw new InvalidOperationException( "Стаўка VAT павінна быць 5 або 23." );
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

        private async Task<List<VatReportRow>> BuildPolandRowsAsync( int year, int month )
        {
            List<ShopifyOrderDto> orders = await FetchOrdersForPolandAsync( year, month );
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
                    (decimal assignedRate, string reason) = ClassifyVatRate( item.ProductType, item.Title );
                    ShopifyClassifiedItemDto classified = new()
                    {
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

            DateTime from = new( year, month, 1, 0, 0, 0, DateTimeKind.Utc );
            DateTime to = from.AddMonths( 1 );
            // Include Polish delivery and pickup orders (pickup often has no shipping_address).
            // Use explicit UTC timestamps and additionally validate period in code.
            string queryFilter = $"created_at:>={from:yyyy-MM-ddTHH:mm:ssZ} created_at:<{to:yyyy-MM-ddTHH:mm:ssZ}";
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
                              productType
                            }
                            variant {
                              product { productType }
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
                            string productType = string.Empty;
                            if (itemNode.TryGetProperty( "product", out JsonElement lineProductEl ) &&
                                lineProductEl.ValueKind == JsonValueKind.Object &&
                                lineProductEl.TryGetProperty( "productType", out JsonElement lineProductTypeEl ) &&
                                lineProductTypeEl.ValueKind == JsonValueKind.String)
                            {
                                productType = lineProductTypeEl.GetString() ?? string.Empty;
                            }
                            if (string.IsNullOrWhiteSpace( productType ) &&
                                itemNode.TryGetProperty( "variant", out JsonElement variantEl ) &&
                                variantEl.ValueKind == JsonValueKind.Object &&
                                variantEl.TryGetProperty( "product", out JsonElement variantProductEl ) &&
                                variantProductEl.ValueKind == JsonValueKind.Object &&
                                variantProductEl.TryGetProperty( "productType", out JsonElement variantProductTypeEl ) &&
                                variantProductTypeEl.ValueKind == JsonValueKind.String)
                            {
                                productType = variantProductTypeEl.GetString() ?? string.Empty;
                            }
                            decimal unitPrice = ReadMoney( itemNode, "originalUnitPriceSet" );
                            if (unitPrice <= 0m) continue;
                            decimal originalTotal = ReadMoney( itemNode, "originalTotalSet" );
                            decimal discountedTotal = ReadMoney( itemNode, "discountedTotalSet" );
                            decimal lineTotalGross = originalTotal > 0m ? originalTotal : unitPrice * quantity;
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
            public List<ShopifyLineItemDto> Items { get; set; } = new();
        }

        private sealed class ShopifyLineItemDto
        {
            public int Quantity { get; set; }
            public decimal UnitPrice { get; set; }
            public decimal LineTotalGross { get; set; }
            public string ProductType { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
        }

        private sealed class ShopifyClassifiedItemDto
        {
            public string ProductTitle { get; set; } = string.Empty;
            public string ProductType { get; set; } = string.Empty;
            public int Quantity { get; set; }
            public decimal UnitPrice { get; set; }
            public decimal GrossAmount { get; set; }
            public decimal AssignedVatRatePercent { get; set; }
            public string AssignmentReason { get; set; } = string.Empty;
        }
    }
}
