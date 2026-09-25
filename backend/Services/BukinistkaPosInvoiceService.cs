using backend.Data;
using backend.Models;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// Issues Poland VAT invoices for Bukinistka POS consignment sales (stock already moved).
/// </summary>
public sealed class BukinistkaPosInvoiceService
{
    private readonly AppDbContext _db;
    private readonly VatReportGenerationService _generation;
    private readonly VatReportMutationService _mutations;
    private readonly VatReportLockService _locks;

    public BukinistkaPosInvoiceService(
        AppDbContext db,
        VatReportGenerationService generation,
        VatReportMutationService mutations,
        VatReportLockService locks )
    {
        _db = db;
        _generation = generation;
        _mutations = mutations;
        _locks = locks;
    }

    public async Task<KirmaBukinistkaPosInvoiceResultDto> InvoiceSalesAsync(
        KirmaBukinistkaPosInvoiceRequest request,
        CancellationToken cancellationToken = default )
    {
        List<int> saleIds = (request.SaleIds ?? [])
            .Where( id => id > 0 )
            .Distinct()
            .ToList();
        if (saleIds.Count == 0)
        {
            throw new InvalidOperationException( "Абярыце хаця б адзін продаж." );
        }

        if (request.InvoiceDateUtc == default)
        {
            throw new InvalidOperationException( "Дата фактуры абавязковая." );
        }

        DateTime invoiceDateUtc = DateTime.SpecifyKind( request.InvoiceDateUtc, DateTimeKind.Utc );
        int periodYear = invoiceDateUtc.Year;
        int periodMonth = invoiceDateUtc.Month;
        VatReportHelpers.ValidatePeriod( periodYear, periodMonth );

        decimal vatRate = request.VatRatePercent ?? 5m;
        if (vatRate != 5m && vatRate != 23m)
        {
            throw new InvalidOperationException( "Стаўка VAT павінна быць 5 або 23." );
        }

        List<KirmaBukinistkaPosSale> sales = await _db.KirmaBukinistkaPosSales
            .Where( x => saleIds.Contains( x.Id ) )
            .ToListAsync( cancellationToken );

        if (sales.Count != saleIds.Count)
        {
            throw new InvalidOperationException( "Адна або некалькі продажаў не знойдзены." );
        }

        foreach (KirmaBukinistkaPosSale sale in sales)
        {
            if (sale.IsOwnStock || sale.IsReturn || sale.IsReversed || sale.Quantity <= 0)
            {
                throw new InvalidOperationException(
                    $"Продаж «{sale.ProductName}» нельга ўключыць у фактуру." );
            }

            if (sale.IsInvoiced)
            {
                throw new InvalidOperationException(
                    $"Продаж «{sale.ProductName}» ужо ў фактуры." );
            }

            if (!sale.OfferId.HasValue || sale.OfferId.Value <= 0)
            {
                throw new InvalidOperationException(
                    $"Продаж «{sale.ProductName}» не звязаны з прапановай (няма цаны брута)." );
            }
        }

        HashSet<int> offerIds = sales
            .Where( s => s.OfferId.HasValue )
            .Select( s => s.OfferId!.Value )
            .ToHashSet();
        Dictionary<int, KirmaBukinistkaOffer> offersById = await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .Where( o => offerIds.Contains( o.Id ) )
            .ToDictionaryAsync( o => o.Id, cancellationToken );

        foreach (KirmaBukinistkaPosSale sale in sales)
        {
            if (!sale.OfferId.HasValue
                || !offersById.TryGetValue( sale.OfferId.Value, out KirmaBukinistkaOffer? offer ))
            {
                continue;
            }

            if (string.Equals(
                    offer.Direction,
                    KirmaBukinistkaOfferDirections.BukinistkaToKirma,
                    StringComparison.OrdinalIgnoreCase )
                && !offer.IsAssignment)
            {
                throw new InvalidOperationException(
                    $"Продаж «{sale.ProductName}» належыць Букіністцы і не можа быць у фактуры Kirma." );
            }
        }

        List<VatReportForeignRowItemCreateRequest> items = new();
        foreach (KirmaBukinistkaPosSale sale in sales.OrderBy( s => s.SoldAtUtc ).ThenBy( s => s.Id ))
        {
            if (!offersById.TryGetValue( sale.OfferId!.Value, out KirmaBukinistkaOffer? offer ))
            {
                throw new InvalidOperationException(
                    $"Прапанова для продажу «{sale.ProductName}» не знойдзена." );
            }

            decimal unitGross = Math.Round( offer.GrossUnitCost, 2, MidpointRounding.AwayFromZero );
            if (unitGross <= 0m)
            {
                throw new InvalidOperationException(
                    $"Кошт брута для «{sale.ProductName}» павінен быць больш за 0." );
            }

            string productId = string.IsNullOrWhiteSpace( sale.ShopifyProductId )
                ? offer.ShopifyProductId
                : sale.ShopifyProductId;
            string variantId = string.IsNullOrWhiteSpace( sale.ShopifyVariantId )
                ? (offer.ShopifyVariantId ?? string.Empty)
                : sale.ShopifyVariantId;

            items.Add( new VatReportForeignRowItemCreateRequest
            {
                ShopifyProductId = productId,
                ShopifyVariantId = variantId,
                ProductTitle = string.IsNullOrWhiteSpace( sale.ProductName )
                    ? offer.ProductName
                    : sale.ProductName,
                Quantity = sale.Quantity,
                UnitPrice = unitGross,
            } );
        }

        // Merge identical product/variant/price lines for a cleaner invoice.
        List<VatReportForeignRowItemCreateRequest> mergedItems = items
            .GroupBy(
                i =>
                    $"{ShopifyIds.NormalizeProductId( i.ShopifyProductId )}::{ShopifyIds.NormalizeVariantId( i.ShopifyVariantId )}::{i.UnitPrice:F2}",
                StringComparer.OrdinalIgnoreCase )
            .Select( g =>
            {
                VatReportForeignRowItemCreateRequest first = g.First();
                return new VatReportForeignRowItemCreateRequest
                {
                    ShopifyProductId = first.ShopifyProductId,
                    ShopifyVariantId = first.ShopifyVariantId,
                    ProductTitle = first.ProductTitle,
                    Quantity = g.Sum( x => x.Quantity ),
                    UnitPrice = first.UnitPrice,
                };
            } )
            .ToList();

        VatReport report = await GetOrCreatePolandReportAsync( periodYear, periodMonth, cancellationToken );

        string orderNumber = string.IsNullOrWhiteSpace( request.InvoiceNumber )
            ? $"Bukinistka {invoiceDateUtc:dd.MM.yyyy}"
            : request.InvoiceNumber.Trim();

        VatReportRow row = await _mutations.AddPolandManualRowWithoutStockEffectsAsync(
            report.Id,
            orderNumber,
            invoiceDateUtc,
            vatRate,
            mergedItems,
            shopifyOrderIdPrefix: "manual-bukpos-",
            assignmentReason: "bukinistka-pos-invoice" );

        DateTime now = DateTime.UtcNow;
        foreach (KirmaBukinistkaPosSale sale in sales)
        {
            sale.IsInvoiced = true;
            sale.InvoicedAtUtc = now;
            sale.VatReportRowId = row.Id;
        }

        await _db.SaveChangesAsync( cancellationToken );

        return new KirmaBukinistkaPosInvoiceResultDto
        {
            VatReportId = report.Id,
            VatReportRowId = row.Id,
            PeriodYear = periodYear,
            PeriodMonth = periodMonth,
            OrderNumber = row.OrderNumber,
            GrossAmount = row.GrossAmount,
            VatAmount = row.VatAmount,
            NetAmount = row.NetAmount,
            InvoicedSaleCount = sales.Count,
        };
    }

    private async Task<VatReport> GetOrCreatePolandReportAsync(
        int periodYear,
        int periodMonth,
        CancellationToken cancellationToken )
    {
        await _locks.EnsurePeriodUnlockedAsync( periodYear, periodMonth );

        VatReport? existing = await _db.VatReports
            .FirstOrDefaultAsync(
                r =>
                    r.PeriodYear == periodYear &&
                    r.PeriodMonth == periodMonth &&
                    r.Type == VatReportType.Poland,
                cancellationToken );

        if (existing is not null)
        {
            return existing;
        }

        await _generation.GenerateAsync( periodYear, periodMonth, VatReportType.Poland );

        VatReport? created = await _db.VatReports
            .FirstOrDefaultAsync(
                r =>
                    r.PeriodYear == periodYear &&
                    r.PeriodMonth == periodMonth &&
                    r.Type == VatReportType.Poland,
                cancellationToken );

        if (created is null)
        {
            throw new InvalidOperationException(
                "Не ўдалося стварыць польскую справаздачу за абраны месяц." );
        }

        return created;
    }
}
