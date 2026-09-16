using backend.Data;
using backend.Models;
using backend.Services.Odoo;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public sealed class BukinistkaPosShopifySyncService
{
    private readonly AppDbContext _db;
    private readonly OdooPosSalesReader _posReader;
    private readonly ShopifyInventoryService _inventory;
    private readonly KirmaBukinistkaOfferService _offers;
    private readonly IConfiguration _config;
    private readonly ILogger<BukinistkaPosShopifySyncService> _logger;

    public BukinistkaPosShopifySyncService(
        AppDbContext db,
        OdooPosSalesReader posReader,
        ShopifyInventoryService inventory,
        KirmaBukinistkaOfferService offers,
        IConfiguration config,
        ILogger<BukinistkaPosShopifySyncService> logger )
    {
        _db = db;
        _posReader = posReader;
        _inventory = inventory;
        _offers = offers;
        _config = config;
        _logger = logger;
    }

    public async Task<KirmaBukinistkaPosSyncResultDto> SyncAsync(
        CancellationToken cancellationToken = default )
    {
        DateTime now = DateTime.UtcNow;
        string shop = (_config["Shopify:Shop"] ?? string.Empty).Trim();
        string accessToken = (_config["Shopify:AccessToken"] ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace( shop ) || string.IsNullOrWhiteSpace( accessToken ))
        {
            return new KirmaBukinistkaPosSyncResultDto
            {
                Skipped = true,
                SkipReason = "Shopify Shop/AccessToken не наладжаныя ў канфігу.",
                SyncedAtUtc = now,
            };
        }

        if (!_posReader.IsConfigured)
        {
            return new KirmaBukinistkaPosSyncResultDto
            {
                Skipped = true,
                SkipReason = "Odoo SyncLogin/SyncPassword не наладжаныя ў канфігу.",
                SyncedAtUtc = now,
            };
        }

        KirmaBukinistkaPosSyncState state = await _db.KirmaBukinistkaPosSyncStates
            .OrderBy( x => x.Id )
            .FirstOrDefaultAsync( cancellationToken )
            ?? new KirmaBukinistkaPosSyncState();

        if (state.Id == 0)
        {
            _db.KirmaBukinistkaPosSyncStates.Add( state );
        }

        DateTime since = state.LastSyncedAtUtc ?? now.AddDays( -14 );
        List<OdooPosSalesReader.PosOrderLine> lines = await _posReader.FetchPaidLinesSinceAsync(
            since,
            state.LastProcessedOrderId,
            cancellationToken );

        HashSet<int> alreadyProcessedLineIds = await _db.KirmaBukinistkaPosSales
            .AsNoTracking()
            .Select( x => x.OdooPosOrderLineId )
            .Distinct()
            .ToHashSetAsync( cancellationToken );

        List<string> pendingPosDeductionKeys = await _db.KirmaBukinistkaPendingOfferSaleDeductions
            .AsNoTracking()
            .Where( x => x.Source == KirmaBukinistkaPendingOfferSaleDeduction.SourceOdooPosLine )
            .Select( x => x.SourceKey )
            .Distinct()
            .ToListAsync( cancellationToken );
        foreach (string key in pendingPosDeductionKeys)
        {
            if (int.TryParse( key, out int lineId ) && lineId > 0)
            {
                alreadyProcessedLineIds.Add( lineId );
            }
        }

        List<KirmaBukinistkaOffer> acceptedOffers = await _db.KirmaBukinistkaOffers
            .Where( x =>
                x.Status == KirmaBukinistkaOfferStatuses.Accepted
                && x.OdooProductId != null
                && x.OdooProductId > 0
                && (x.IsAssignment
                    || string.IsNullOrWhiteSpace( x.Direction )
                    || x.Direction == KirmaBukinistkaOfferDirections.KirmaToBukinistka) )
            .OrderBy( x => x.CreatedAtUtc )
            .ThenBy( x => x.Id )
            .ToListAsync( cancellationToken );

        HashSet<int> pendingBukToKirmaOdooIds = await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .Where( x =>
                x.Status == KirmaBukinistkaOfferStatuses.Pending
                && x.Quantity > 0
                && x.Direction == KirmaBukinistkaOfferDirections.BukinistkaToKirma
                && x.OdooProductId != null
                && x.OdooProductId > 0 )
            .Select( x => x.OdooProductId!.Value )
            .Distinct()
            .ToHashSetAsync( cancellationToken );

        Dictionary<int, int> soldByOfferId = await _db.KirmaBukinistkaPosSales
            .AsNoTracking()
            .Where( x => x.OfferId != null && !x.IsOwnStock && !x.IsReversed && !x.IsReturn )
            .GroupBy( x => x.OfferId!.Value )
            .Select( g => new { OfferId = g.Key, Qty = g.Sum( x => x.Quantity ) } )
            .ToDictionaryAsync( x => x.OfferId, x => x.Qty, cancellationToken );

        // Shopify→Odoo Wydanie also consumes the same accepted Kirma consignment qty.
        Dictionary<int, int> wydanieByOfferId = await _db.KirmaBukinistkaShopifyDeliverySyncs
            .AsNoTracking()
            .Where( x => !x.IsCancelled )
            .GroupBy( x => x.OfferId )
            .Select( g => new { OfferId = g.Key, Qty = g.Sum( x => x.Quantity ) } )
            .ToDictionaryAsync( x => x.OfferId, x => x.Qty, cancellationToken );

        Dictionary<int, KirmaBukinistkaOdooOwnStockBuffer> ownBuffers = await _db
            .KirmaBukinistkaOdooOwnStockBuffers
            .Where( x => x.OwnQtyRemaining > 0 )
            .ToDictionaryAsync( x => x.OdooProductId, cancellationToken );

        // odooProductId -> queue of remaining offer buckets (FIFO)
        Dictionary<int, Queue<OfferBucket>> remainingByOdooProduct = new();
        foreach (KirmaBukinistkaOffer offer in acceptedOffers)
        {
            int odooProductId = offer.OdooProductId!.Value;
            int alreadySold =
                soldByOfferId.GetValueOrDefault( offer.Id )
                + wydanieByOfferId.GetValueOrDefault( offer.Id );
            int remaining = offer.Quantity - alreadySold;
            if (remaining <= 0)
            {
                continue;
            }

            if (!remainingByOdooProduct.TryGetValue( odooProductId, out Queue<OfferBucket>? queue ))
            {
                queue = new Queue<OfferBucket>();
                remainingByOdooProduct[odooProductId] = queue;
            }

            queue.Enqueue( new OfferBucket( offer, remaining ) );
        }

        int linesProcessed = 0;
        int unitsSynced = 0;
        int maxOrderId = state.LastProcessedOrderId ?? 0;
        Dictionary<string, int> shopifyDeltas = new( StringComparer.Ordinal );

        foreach (OdooPosSalesReader.PosOrderLine line in lines)
        {
            maxOrderId = Math.Max( maxOrderId, line.OrderId );
            if (alreadyProcessedLineIds.Contains( line.LineId ))
            {
                continue;
            }

            bool hasOwn = ownBuffers.TryGetValue( line.ProductId, out KirmaBukinistkaOdooOwnStockBuffer? buffer )
                          && buffer is not null
                          && buffer.OwnQtyRemaining > 0;
            bool hasKirma = remainingByOdooProduct.TryGetValue( line.ProductId, out Queue<OfferBucket>? queue )
                            && queue is not null
                            && queue.Count > 0;
            bool hasPendingBukToKirma = pendingBukToKirmaOdooIds.Contains( line.ProductId );
            if (!hasOwn && !hasKirma && !hasPendingBukToKirma)
            {
                continue;
            }

            int toAllocate = (int)Math.Floor( line.Quantity );
            if (toAllocate <= 0)
            {
                continue;
            }

            List<KirmaBukinistkaPosSale> createdForLine = new();

            // 1) Bukinistka's own pre-receipt stock sells first — no Shopify delta.
            if (hasOwn && buffer is not null)
            {
                int ownTake = Math.Min( toAllocate, buffer.OwnQtyRemaining );
                if (ownTake > 0)
                {
                    string ownName = ResolveProductName( line.ProductId, acceptedOffers );

                    createdForLine.Add( new KirmaBukinistkaPosSale
                    {
                        OdooPosOrderId = line.OrderId,
                        OdooPosOrderLineId = line.LineId,
                        OdooPosOrderName = line.OrderName,
                        OfferId = null,
                        OdooProductId = line.ProductId,
                        ShopifyProductId = string.Empty,
                        ShopifyVariantId = string.Empty,
                        Quantity = ownTake,
                        ProductName = ownName,
                        IsOwnStock = true,
                        SoldAtUtc = line.SoldAtUtc,
                        CreatedAtUtc = now,
                    } );

                    buffer.OwnQtyRemaining -= ownTake;
                    buffer.UpdatedAtUtc = now;
                    toAllocate -= ownTake;
                    if (buffer.OwnQtyRemaining <= 0)
                    {
                        ownBuffers.Remove( line.ProductId );
                    }
                }
            }

            // 2) Then Kirma consignment → Shopify inventory decrease.
            while (toAllocate > 0 && queue is not null && queue.Count > 0)
            {
                OfferBucket bucket = queue.Peek();
                int take = Math.Min( toAllocate, bucket.Remaining );
                if (take <= 0)
                {
                    queue.Dequeue();
                    continue;
                }

                KirmaBukinistkaOffer offer = bucket.Offer;
                createdForLine.Add( new KirmaBukinistkaPosSale
                {
                    OdooPosOrderId = line.OrderId,
                    OdooPosOrderLineId = line.LineId,
                    OdooPosOrderName = line.OrderName,
                    OfferId = offer.Id,
                    OdooProductId = line.ProductId,
                    ShopifyProductId = offer.ShopifyProductId,
                    ShopifyVariantId = offer.ShopifyVariantId ?? string.Empty,
                    Quantity = take,
                    ProductName = string.IsNullOrWhiteSpace( offer.ProductName )
                        ? $"Odoo #{line.ProductId}"
                        : offer.ProductName,
                    IsOwnStock = false,
                    SoldAtUtc = line.SoldAtUtc,
                    CreatedAtUtc = now,
                } );

                string shopifyKey = BuildShopifyInventoryKey(
                    offer.ShopifyProductId,
                    offer.ShopifyVariantId );
                shopifyDeltas[shopifyKey] = shopifyDeltas.GetValueOrDefault( shopifyKey ) - take;

                bucket.Remaining -= take;
                toAllocate -= take;
                unitsSynced += take;
                if (bucket.Remaining <= 0)
                {
                    queue.Dequeue();
                }
            }

            // 3) Leftover → shrink Pending Buk→Kirma offers for this Odoo product.
            int pendingShrunk = 0;
            if (toAllocate > 0 && hasPendingBukToKirma)
            {
                pendingShrunk = await _offers.ShrinkPendingBukToKirmaForPosSaleAsync(
                    line.LineId,
                    line.ProductId,
                    toAllocate,
                    now,
                    cancellationToken );
            }

            if (createdForLine.Count == 0 && pendingShrunk <= 0)
            {
                continue;
            }

            if (createdForLine.Count > 0)
            {
                _db.KirmaBukinistkaPosSales.AddRange( createdForLine );
            }

            alreadyProcessedLineIds.Add( line.LineId );
            linesProcessed++;
        }

        // POS returns (negative qty): restore Shopify and hide reversed sales.
        List<OdooPosSalesReader.PosOrderLine> returnLines =
            await _posReader.FetchReturnLinesSinceAsync(
                since,
                state.LastProcessedOrderId,
                cancellationToken );

        List<KirmaBukinistkaPosSale> openSales = await _db.KirmaBukinistkaPosSales
            .Where( x => !x.IsReversed && !x.IsReturn && x.Quantity > 0 )
            .OrderBy( x => x.SoldAtUtc )
            .ThenBy( x => x.Id )
            .ToListAsync( cancellationToken );

        Dictionary<int, Queue<KirmaBukinistkaPosSale>> openByProduct = new();
        foreach (KirmaBukinistkaPosSale sale in openSales)
        {
            if (!openByProduct.TryGetValue( sale.OdooProductId, out Queue<KirmaBukinistkaPosSale>? q ))
            {
                q = new Queue<KirmaBukinistkaPosSale>();
                openByProduct[sale.OdooProductId] = q;
            }

            q.Enqueue( sale );
        }

        // Reload own buffers tracked in memory + DB for return restore.
        Dictionary<int, KirmaBukinistkaOdooOwnStockBuffer> ownBuffersAll = await _db
            .KirmaBukinistkaOdooOwnStockBuffers
            .ToDictionaryAsync( x => x.OdooProductId, cancellationToken );

        foreach (OdooPosSalesReader.PosOrderLine ret in returnLines)
        {
            maxOrderId = Math.Max( maxOrderId, ret.OrderId );
            if (alreadyProcessedLineIds.Contains( ret.LineId ))
            {
                continue;
            }

            int toReverse = (int)Math.Floor( ret.Quantity );
            if (toReverse <= 0)
            {
                continue;
            }

            if (!openByProduct.TryGetValue( ret.ProductId, out Queue<KirmaBukinistkaPosSale>? saleQueue )
                || saleQueue.Count == 0)
            {
                // No synced Kirma/own sale to reverse — ignore.
                continue;
            }

            int reversedUnits = 0;
            while (toReverse > 0 && saleQueue.Count > 0)
            {
                KirmaBukinistkaPosSale sale = saleQueue.Peek();
                int take = Math.Min( toReverse, sale.Quantity );
                if (take <= 0)
                {
                    saleQueue.Dequeue();
                    continue;
                }

                if (sale.IsOwnStock)
                {
                    if (!ownBuffersAll.TryGetValue( sale.OdooProductId, out KirmaBukinistkaOdooOwnStockBuffer? buf ))
                    {
                        buf = new KirmaBukinistkaOdooOwnStockBuffer
                        {
                            OdooProductId = sale.OdooProductId,
                            OwnQtyRemaining = 0,
                            UpdatedAtUtc = now,
                        };
                        _db.KirmaBukinistkaOdooOwnStockBuffers.Add( buf );
                        ownBuffersAll[sale.OdooProductId] = buf;
                    }

                    buf.OwnQtyRemaining += take;
                    buf.UpdatedAtUtc = now;
                }
                else if (!string.IsNullOrWhiteSpace( sale.ShopifyProductId ))
                {
                    string shopifyKey = BuildShopifyInventoryKey(
                        sale.ShopifyProductId,
                        sale.ShopifyVariantId );
                    shopifyDeltas[shopifyKey] =
                        shopifyDeltas.GetValueOrDefault( shopifyKey ) + take;
                }

                sale.Quantity -= take;
                if (sale.Quantity <= 0)
                {
                    sale.Quantity = 0;
                    sale.IsReversed = true;
                    saleQueue.Dequeue();
                }

                toReverse -= take;
                reversedUnits += take;
            }

            if (reversedUnits <= 0)
            {
                continue;
            }

            // Idempotency marker for this return line (hidden from sales list).
            _db.KirmaBukinistkaPosSales.Add( new KirmaBukinistkaPosSale
            {
                OdooPosOrderId = ret.OrderId,
                OdooPosOrderLineId = ret.LineId,
                OdooPosOrderName = ret.OrderName,
                OfferId = null,
                OdooProductId = ret.ProductId,
                ShopifyProductId = string.Empty,
                ShopifyVariantId = string.Empty,
                Quantity = reversedUnits,
                ProductName = ResolveProductName( ret.ProductId, acceptedOffers ),
                IsOwnStock = false,
                IsReturn = true,
                IsReversed = false,
                SoldAtUtc = ret.SoldAtUtc,
                CreatedAtUtc = now,
            } );
            alreadyProcessedLineIds.Add( ret.LineId );
            linesProcessed++;
        }

        foreach ((string productKey, int delta) in shopifyDeltas)
        {
            if (delta == 0 || string.IsNullOrWhiteSpace( productKey ))
            {
                continue;
            }

            try
            {
                await _inventory.ApplyInventoryDeltaByProductKeyAsync(
                    shop,
                    accessToken,
                    productKey,
                    delta );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to apply Shopify inventory delta {Delta} for product {ProductId}",
                    delta,
                    productKey );
                throw;
            }
        }

        state.LastSyncedAtUtc = now;
        if (maxOrderId > 0)
        {
            state.LastProcessedOrderId = maxOrderId;
        }

        await _db.SaveChangesAsync( cancellationToken );

        ProductLedgerService.InvalidateSoldByLineCache();

        return new KirmaBukinistkaPosSyncResultDto
        {
            Skipped = false,
            OrdersScanned = lines.Select( x => x.OrderId )
                .Concat( returnLines.Select( x => x.OrderId ) )
                .Distinct()
                .Count(),
            LinesProcessed = linesProcessed,
            UnitsSynced = unitsSynced,
            SyncedAtUtc = now,
        };
    }

    public async Task<List<KirmaBukinistkaPosSaleDto>> ListSalesAsync(
        CancellationToken cancellationToken = default )
    {
        // Only active Kirma-attributed sales awaiting invoice (own-stock / returns / reversed / invoiced hidden).
        List<KirmaBukinistkaPosSale> rows = await _db.KirmaBukinistkaPosSales
            .AsNoTracking()
            .Where( x =>
                !x.IsOwnStock &&
                !x.IsReturn &&
                !x.IsReversed &&
                !x.IsInvoiced &&
                x.Quantity > 0 )
            .OrderByDescending( x => x.SoldAtUtc )
            .ThenByDescending( x => x.Id )
            .Take( 500 )
            .ToListAsync( cancellationToken );

        HashSet<int> offerIds = rows
            .Where( x => x.OfferId.HasValue && x.OfferId.Value > 0 )
            .Select( x => x.OfferId!.Value )
            .ToHashSet();
        Dictionary<int, (decimal Gross, string? Supplier)> offerMeta = offerIds.Count == 0
            ? new Dictionary<int, (decimal, string?)>()
            : await _db.KirmaBukinistkaOffers
                .AsNoTracking()
                .Where( o => offerIds.Contains( o.Id ) )
                .ToDictionaryAsync(
                    o => o.Id,
                    o => (
                        Math.Round( o.GrossUnitCost, 2, MidpointRounding.AwayFromZero ),
                        string.IsNullOrWhiteSpace( o.SupplierName ) ? null : o.SupplierName.Trim()
                    ),
                    cancellationToken );

        return rows.Select( x =>
        {
            decimal? gross = null;
            string? supplier = null;
            if (x.OfferId.HasValue &&
                offerMeta.TryGetValue( x.OfferId.Value, out (decimal Gross, string? Supplier) meta ))
            {
                gross = meta.Gross;
                supplier = meta.Supplier;
            }

            return new KirmaBukinistkaPosSaleDto
            {
                Id = x.Id,
                OdooPosOrderId = x.OdooPosOrderId,
                OdooPosOrderName = x.OdooPosOrderName,
                OfferId = x.OfferId,
                OdooProductId = x.OdooProductId,
                ShopifyProductId = x.ShopifyProductId,
                ShopifyVariantId = x.ShopifyVariantId,
                Quantity = x.Quantity,
                ProductName = x.ProductName,
                GrossUnitCost = gross,
                SupplierName = supplier,
                IsOwnStock = false,
                SoldAtUtc = x.SoldAtUtc,
                CreatedAtUtc = x.CreatedAtUtc,
            };
        } ).ToList();
    }

    private static string ResolveProductName( int odooProductId, List<KirmaBukinistkaOffer> acceptedOffers )
    {
        KirmaBukinistkaOffer? match = acceptedOffers.FirstOrDefault( x => x.OdooProductId == odooProductId );
        if (match is not null && !string.IsNullOrWhiteSpace( match.ProductName ))
        {
            return match.ProductName;
        }

        return $"Odoo #{odooProductId}";
    }

    private static string BuildShopifyInventoryKey( string productId, string? variantId )
    {
        string product = (productId ?? string.Empty).Trim();
        string variant = (variantId ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace( variant ) ? product : $"{product}::{variant}";
    }

    private sealed class OfferBucket
    {
        public OfferBucket( KirmaBukinistkaOffer offer, int remaining )
        {
            Offer = offer;
            Remaining = remaining;
        }

        public KirmaBukinistkaOffer Offer { get; }
        public int Remaining { get; set; }
    }
}
