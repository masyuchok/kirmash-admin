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
    private readonly IConfiguration _config;
    private readonly ILogger<BukinistkaPosShopifySyncService> _logger;

    public BukinistkaPosShopifySyncService(
        AppDbContext db,
        OdooPosSalesReader posReader,
        ShopifyInventoryService inventory,
        IConfiguration config,
        ILogger<BukinistkaPosShopifySyncService> logger )
    {
        _db = db;
        _posReader = posReader;
        _inventory = inventory;
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

        List<KirmaBukinistkaOffer> acceptedOffers = await _db.KirmaBukinistkaOffers
            .Where( x =>
                x.Status == KirmaBukinistkaOfferStatuses.Accepted
                && x.OdooProductId != null
                && x.OdooProductId > 0 )
            .OrderBy( x => x.CreatedAtUtc )
            .ThenBy( x => x.Id )
            .ToListAsync( cancellationToken );

        Dictionary<int, int> soldByOfferId = await _db.KirmaBukinistkaPosSales
            .AsNoTracking()
            .Where( x => x.OfferId != null && !x.IsOwnStock )
            .GroupBy( x => x.OfferId!.Value )
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
            int alreadySold = soldByOfferId.GetValueOrDefault( offer.Id );
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
            if (!hasOwn && !hasKirma)
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

                string shopifyKey = offer.ShopifyProductId;
                shopifyDeltas[shopifyKey] = shopifyDeltas.GetValueOrDefault( shopifyKey ) - take;

                bucket.Remaining -= take;
                toAllocate -= take;
                unitsSynced += take;
                if (bucket.Remaining <= 0)
                {
                    queue.Dequeue();
                }
            }

            if (createdForLine.Count == 0)
            {
                continue;
            }

            _db.KirmaBukinistkaPosSales.AddRange( createdForLine );
            alreadyProcessedLineIds.Add( line.LineId );
            linesProcessed++;
        }

        foreach ((string productKey, int delta) in shopifyDeltas)
        {
            if (delta >= 0 || string.IsNullOrWhiteSpace( productKey ))
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

        return new KirmaBukinistkaPosSyncResultDto
        {
            Skipped = false,
            OrdersScanned = lines.Select( x => x.OrderId ).Distinct().Count(),
            LinesProcessed = linesProcessed,
            UnitsSynced = unitsSynced,
            SyncedAtUtc = now,
        };
    }

    public async Task<List<KirmaBukinistkaPosSaleDto>> ListSalesAsync(
        CancellationToken cancellationToken = default )
    {
        // Only Kirma-attributed sales (own-stock burn is internal accounting).
        List<KirmaBukinistkaPosSale> rows = await _db.KirmaBukinistkaPosSales
            .AsNoTracking()
            .Where( x => !x.IsOwnStock )
            .OrderByDescending( x => x.SoldAtUtc )
            .ThenByDescending( x => x.Id )
            .Take( 500 )
            .ToListAsync( cancellationToken );

        return rows.Select( x => new KirmaBukinistkaPosSaleDto
        {
            Id = x.Id,
            OdooPosOrderId = x.OdooPosOrderId,
            OdooPosOrderName = x.OdooPosOrderName,
            OdooProductId = x.OdooProductId,
            ShopifyProductId = x.ShopifyProductId,
            Quantity = x.Quantity,
            ProductName = x.ProductName,
            IsOwnStock = false,
            SoldAtUtc = x.SoldAtUtc,
            CreatedAtUtc = x.CreatedAtUtc,
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
