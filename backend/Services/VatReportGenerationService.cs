using backend.Data;
using backend.Models;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public class VatReportGenerationService
{
    private readonly AppDbContext _db;
    private readonly ShopifyOrderFetchService _shopifyOrders;

    public VatReportGenerationService( AppDbContext db, ShopifyOrderFetchService shopifyOrders )
    {
        _db = db;
        _shopifyOrders = shopifyOrders;
    }

    public async Task<VatReportListItem> GenerateAsync( int periodYear, int periodMonth, string reportType )
        {
            VatReportHelpers.ValidatePeriod( periodYear, periodMonth );
            string normalizedType = VatReportHelpers.NormalizeReportType( reportType );
            bool exists = await _db.VatReports.AnyAsync(
                r => r.PeriodYear == periodYear && r.PeriodMonth == periodMonth && r.Type == normalizedType
            );
            if (exists)
            {
                throw new InvalidOperationException( "РЎРїСЂР°РІР°Р·РґР°С‡Р° Р·Р° РіСЌС‚С‹ РјРµСЃСЏС† СѓР¶Рѕ С–СЃРЅСѓРµ. Р’С‹РєР°СЂС‹СЃС‚Р°Р№С†Рµ РїРµСЂРµРіРµРЅРµСЂР°С†С‹СЋ." );
            }

            List<VatReportRow> rows = normalizedType switch
            {
                VatReportType.Poland => await BuildPolandRowsAsync( periodYear, periodMonth ),
                VatReportType.Foreign => await BuildForeignRowsAsync( periodYear, periodMonth ),
                _ => throw new InvalidOperationException( "РќРµРІСЏРґРѕРјС‹ С‚С‹Рї СЃРїСЂР°РІР°Р·РґР°С‡С‹." )
            };

            decimal vatTotal = rows.Sum( r => r.VatAmount );
            VatReport report = new()
            {
                PeriodYear = periodYear,
                PeriodMonth = periodMonth,
                Type = normalizedType,
                Name = VatReportHelpers.BuildReportName( normalizedType, periodYear, periodMonth ),
                Document = null,
                Vat = VatReportHelpers.Round2( vatTotal ),
                VatCredit = 0m,
                VatToPay = VatReportHelpers.Round2( vatTotal ),
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
                throw new InvalidOperationException( "РЎРїСЂР°РІР°Р·РґР°С‡Р° РЅРµ Р·РЅРѕР№РґР·РµРЅР°." );
            }

            List<VatReportRow> rows = report.Type switch
            {
                VatReportType.Poland => await BuildPolandRowsAsync( report.PeriodYear, report.PeriodMonth ),
                VatReportType.Foreign => await BuildForeignRowsAsync( report.PeriodYear, report.PeriodMonth ),
                _ => throw new InvalidOperationException( "РќРµРІСЏРґРѕРјС‹ С‚С‹Рї СЃРїСЂР°РІР°Р·РґР°С‡С‹." )
            };

            if (report.Rows.Count > 0)
            {
                _db.VatReportRows.RemoveRange( report.Rows );
            }

            decimal vatTotal = rows.Sum( r => r.VatAmount );
            report.Vat = VatReportHelpers.Round2( vatTotal );
            report.VatCredit = 0m;
            report.VatToPay = VatReportHelpers.Round2( vatTotal );
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
    public async Task<List<VatReportSourceOrderOption>> GetSourceOrderOptionsAsync( int reportId )
        {
            VatReport? report = await _db.VatReports
                .AsNoTracking()
                .FirstOrDefaultAsync( r => r.Id == reportId );
            if (report is null)
            {
                throw new InvalidOperationException( "РЎРїСЂР°РІР°Р·РґР°С‡Р° РЅРµ Р·РЅРѕР№РґР·РµРЅР°." );
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
    private async Task<List<VatReportRow>> BuildPolandRowsAsync( int year, int month )
        {
            List<ShopifyOrderDto> orders = await _shopifyOrders.FetchOrdersForPolandAsync( year, month );
            Dictionary<string, decimal> supplyVatRates = await GetSupplyVatRatesAsync();
            List<VatReportRow> rows = new();

            foreach (ShopifyOrderDto order in orders)
            {
                decimal shippingGross = VatReportHelpers.Round2( order.ShippingGross );
                decimal gross5 = 0m;
                decimal gross23 = 0m;
                List<ShopifyClassifiedItemDto> items5 = new();
                List<ShopifyClassifiedItemDto> items23 = new();

                foreach (ShopifyLineItemDto item in order.Items)
                {
                    decimal lineGross = VatReportHelpers.Round2( item.LineTotalGross );
                    if (lineGross <= 0) continue;
                    (decimal assignedRate, string reason) = ResolveVatRateForReportItem(
                        item.ShopifyProductId,
                        supplyVatRates
                    );
                    ShopifyClassifiedItemDto classified = new()
                    {
                        ShopifyProductId = ShopifyIds.NormalizeGid( item.ShopifyProductId, "gid://shopify/Product/" ).Trim(),
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

                decimal originalOrderGross = VatReportHelpers.Round2( totalGross + shippingGross );
                if (order.CurrentTotalGross > 0m && originalOrderGross > 0m && order.CurrentTotalGross < originalOrderGross)
                {
                    decimal ratio = order.CurrentTotalGross / originalOrderGross;
                    gross5 = VatReportHelpers.Round2( gross5 * ratio );
                    gross23 = VatReportHelpers.Round2( gross23 * ratio );
                    shippingGross = VatReportHelpers.Round2( shippingGross * ratio );

                    decimal adjustedOrderGross = VatReportHelpers.Round2( gross5 + gross23 + shippingGross );
                    decimal drift = VatReportHelpers.Round2( order.CurrentTotalGross - adjustedOrderGross );
                    if (drift != 0m)
                    {
                        if (gross23 >= gross5 && gross23 > 0m) gross23 = VatReportHelpers.Round2( Math.Max( 0m, gross23 + drift ) );
                        else if (gross5 > 0m) gross5 = VatReportHelpers.Round2( Math.Max( 0m, gross5 + drift ) );
                        else shippingGross = VatReportHelpers.Round2( Math.Max( 0m, shippingGross + drift ) );
                    }
                }

                totalGross = gross5 + gross23;
                if (totalGross <= 0m) continue;

                if (gross5 > 0m)
                {
                    decimal shippingFor5 = totalGross > 0m ? VatReportHelpers.Round2( shippingGross * (gross5 / totalGross) ) : 0m;
                    rows.Add( BuildRow( order, 5m, gross5, shippingFor5, items5 ) );
                }

                if (gross23 > 0m)
                {
                    decimal shippingFor23 = totalGross > 0m ? VatReportHelpers.Round2( shippingGross * (gross23 / totalGross) ) : 0m;
                    rows.Add( BuildRow( order, 23m, gross23, shippingFor23, items23 ) );
                }

                if (gross5 > 0m && gross23 > 0m && shippingGross > 0m)
                {
                    // Keep totals exact after rounding split percentages.
                    decimal assigned = rows.Where( r => r.ShopifyOrderId == order.OrderId ).Sum( r => r.ShippingGrossAmount );
                    decimal diff = VatReportHelpers.Round2( shippingGross - assigned );
                    if (diff != 0m)
                    {
                        VatReportRow target = rows.Last( r => r.ShopifyOrderId == order.OrderId );
                        target.ShippingGrossAmount = VatReportHelpers.Round2( target.ShippingGrossAmount + diff );
                        RecalculateAmounts( target );
                    }
                }
            }

            return rows;
        }

    private async Task<List<VatReportRow>> BuildForeignRowsAsync( int year, int month )
        {
            List<ShopifyOrderDto> orders = await _shopifyOrders.FetchOrdersForForeignAsync( year, month );
            Dictionary<string, decimal> supplyVatRates = await GetSupplyVatRatesAsync();
            List<string> orderIds = orders
                .Select( o => o.OrderId )
                .Where( id => !string.IsNullOrWhiteSpace( id ) )
                .Distinct( StringComparer.OrdinalIgnoreCase )
                .ToList();
            Dictionary<string, ForeignDeliveryInfo> deliveryByOrderId =
                await _shopifyOrders.FetchForeignDeliveryInfoAsync( orderIds );
            List<VatReportRow> rows = new();
            foreach (ShopifyOrderDto order in orders)
            {
                deliveryByOrderId.TryGetValue( order.OrderId, out ForeignDeliveryInfo? deliveryInfo );
                string orderNumber = ResolveForeignOrderNumber( order.OrderNumber, deliveryInfo );
                decimal shippingGross = VatReportHelpers.Round2( order.ShippingGross );
                bool isEuDestination = IsEuCountryCode( order.CountryCode );
                Dictionary<decimal, decimal> grossByRate = new();
                Dictionary<decimal, List<ShopifyClassifiedItemDto>> itemsByRate = new();

                foreach (ShopifyLineItemDto item in order.Items)
                {
                    decimal lineGross = VatReportHelpers.Round2( item.LineTotalGross );
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
                        ShopifyProductId = ShopifyIds.NormalizeGid( item.ShopifyProductId, "gid://shopify/Product/" ).Trim(),
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
                    decimal shippingForRate = totalGross > 0m ? VatReportHelpers.Round2( shippingGross * (goodsGross / totalGross) ) : 0m;
                    rows.Add( BuildRow( order, rate, goodsGross, shippingForRate, itemsByRate[rate], orderNumber ) );
                }

                if (grossByRate.Count > 1 && shippingGross > 0m)
                {
                    decimal assigned = rows.Where( r => r.ShopifyOrderId == order.OrderId ).Sum( r => r.ShippingGrossAmount );
                    decimal diff = VatReportHelpers.Round2( shippingGross - assigned );
                    if (diff != 0m)
                    {
                        VatReportRow target = rows.Last( r => r.ShopifyOrderId == order.OrderId );
                        target.ShippingGrossAmount = VatReportHelpers.Round2( target.ShippingGrossAmount + diff );
                        RecalculateAmounts( target );
                    }
                }
            }

            return rows;
        }

    private static string ResolveForeignOrderNumber( string orderNumber, ForeignDeliveryInfo? deliveryInfo )
    {
        if (deliveryInfo is null)
        {
            return orderNumber;
        }

        string address = !string.IsNullOrWhiteSpace( deliveryInfo.ShippingAddress )
            ? deliveryInfo.ShippingAddress
            : deliveryInfo.BillingAddress;
        if (string.IsNullOrWhiteSpace( deliveryInfo.Name ) && string.IsNullOrWhiteSpace( address ))
        {
            return orderNumber;
        }

        return VatReportHelpers.EncodeOrderNumberWithContact( orderNumber, deliveryInfo.Name, address );
    }

    private static VatReportRow BuildRow(
            ShopifyOrderDto order,
            decimal vatRatePercent,
            decimal goodsGross,
            decimal shippingGross,
            List<ShopifyClassifiedItemDto> items,
            string? orderNumber = null
        )
        {
            decimal gross = VatReportHelpers.Round2( goodsGross + shippingGross );
            decimal rate = vatRatePercent / 100m;
            decimal vat = VatReportHelpers.Round2( gross * rate / (1m + rate) );
            decimal net = VatReportHelpers.Round2( gross - vat );
            decimal netShipping = VatReportHelpers.Round2( shippingGross / (1m + rate) );

            return new VatReportRow
            {
                ShopifyOrderId = order.OrderId,
                OrderNumber = string.IsNullOrWhiteSpace( orderNumber ) ? order.OrderNumber : orderNumber,
                OrderDateUtc = order.CreatedAtUtc,
                VatRatePercent = vatRatePercent,
                GrossAmount = gross,
                VatAmount = vat,
                NetAmount = net,
                ShippingGrossAmount = VatReportHelpers.Round2( shippingGross ),
                ShippingNetAmount = netShipping,
                Items = items
                    .Select( x => new VatReportRowItem
                    {
                        ShopifyProductId = string.IsNullOrWhiteSpace( x.ShopifyProductId ) ? string.Empty : x.ShopifyProductId,
                        ProductTitle = string.IsNullOrWhiteSpace( x.ProductTitle ) ? "—" : x.ProductTitle,
                        ProductType = x.ProductType ?? string.Empty,
                        Quantity = x.Quantity,
                        UnitPrice = VatReportHelpers.Round2( x.UnitPrice ),
                        GrossAmount = VatReportHelpers.Round2( x.GrossAmount ),
                        AssignedVatRatePercent = x.AssignedVatRatePercent,
                        AssignmentReason = x.AssignmentReason
                    } )
                    .ToList()
            };
        }

    private static void RecalculateAmounts( VatReportRow row )
        {
            decimal rate = row.VatRatePercent / 100m;
            row.ShippingNetAmount = VatReportHelpers.Round2( row.ShippingGrossAmount / (1m + rate) );
            row.GrossAmount = VatReportHelpers.Round2( row.GrossAmount );
            row.VatAmount = VatReportHelpers.Round2( row.GrossAmount * rate / (1m + rate) );
            row.NetAmount = VatReportHelpers.Round2( row.GrossAmount - row.VatAmount );
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
            if (source.Contains( "Р·Р°РєР»Р°РґРє" ) || source.Contains( "bookmark" ))
            {
                return false;
            }
            return source.Contains( "book" ) ||
                   source.Contains( "РєРЅС–Рі" ) ||
                   source.Contains( "РєРЅРёРі" ) ||
                   source.Contains( "С‡Р°СЃРѕРїС–СЃ" ) ||
                   source.Contains( "С‡Р°СЃРѕРїiСЃ" ) ||
                   source.Contains( "С‡Р°СЃРѕРїРёСЃС‹" ) ||
                   source.Contains( "С‡Р°СЃРѕРїiСЃС‹" ) ||
                   source.Contains( "Р¶СѓСЂРЅР°Р»" ) ||
                   source.Contains( "ksiД…Еј" ) ||
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
            string normalizedId = ShopifyIds.NormalizeGid( shopifyProductId, "gid://shopify/Product/" ).Trim();
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
                    x => ShopifyIds.NormalizeGid( x.ProductId, "gid://shopify/Product/" ).Trim(),
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
}
