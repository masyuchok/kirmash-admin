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

        // Always keep a rolling replay window. Odoo orders can become visible late,
        // and a one-time cursor rewind/migration may already have been consumed by
        // an older deployment. Odoo line ids make this replay idempotent.
        int lookbackDays = 30;
        if (int.TryParse( _config["Odoo:PosSyncLookbackDays"], out int configuredLookback ))
        {
            lookbackDays = Math.Clamp( configuredLookback, 1, 180 );
        }

        DateTime rollingSince = now.AddDays( -lookbackDays );
        DateTime since = state.LastSyncedAtUtc.HasValue
            && state.LastSyncedAtUtc.Value < rollingSince
                ? state.LastSyncedAtUtc.Value
                : rollingSince;
        List<OdooPosSalesReader.PosOrderLine> lines = await _posReader.FetchPaidLinesSinceAsync(
            since,
            minOrderIdExclusive: null,
            cancellationToken );

        HashSet<int> alreadyProcessedLineIds = await _db.KirmaBukinistkaPosSales
            .AsNoTracking()
            .Select( x => x.OdooPosOrderLineId )
            .Distinct()
            .ToHashSetAsync( cancellationToken );

        // Fingerprints stop double Shopify deductions when the same physical POS
        // sale appears once as pos.order.line and once as WH/POS stock.move
        // (different Odoo ids). Only cross-source pairs are matched so two real
        // same-day sales of qty 1 are not collapsed.
        List<(int ProductId, int Quantity, DateTime SoldAtUtc, int LineId)> recordedSales =
            await _db.KirmaBukinistkaPosSales
                .AsNoTracking()
                .Where( x => !x.IsReturn && !x.IsReversed && x.Quantity > 0 )
                .Select( x => new ValueTuple<int, int, DateTime, int>(
                    x.OdooProductId,
                    x.Quantity,
                    x.SoldAtUtc,
                    x.OdooPosOrderLineId ) )
                .ToListAsync( cancellationToken );
        HashSet<int> consumedRecordedSaleIndexes = new();

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
                && x.OdooProductId > 0 )
            .OrderBy( x => x.AcceptedAtUtc ?? x.CreatedAtUtc )
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

        // Ordinary Buk→Kirma stock was already added to Shopify on accept, so POS
        // sales must hit those buckets before any stale "own stock" buffer.
        // Kirma→Buk / assignment keep own-stock-first (Buk's pre-receipt inventory).
        // Offers whose Shopify product/variant was deleted are skipped: allocating to
        // them would fail on inventory apply and block later offers for the same
        // Odoo product (e.g. a duplicate offer relinked to a new Shopify card).
        // Only verify Shopify keys that can actually collide in this sync window —
        // checking every accepted offer sequentially times out behind Cloudflare.
        HashSet<int> lineProductIds = lines
            .Select( x => x.ProductId )
            .Where( id => id > 0 )
            .ToHashSet();
        HashSet<int> collidingOdooProducts = acceptedOffers
            .Where( o => o.OdooProductId is int pid && lineProductIds.Contains( pid ) )
            .GroupBy( o => o.OdooProductId!.Value )
            .Where( g => g
                .Select( o => BuildShopifyInventoryKey( o.ShopifyProductId, o.ShopifyVariantId ) )
                .Distinct( StringComparer.Ordinal )
                .Count() > 1 )
            .Select( g => g.Key )
            .ToHashSet();
        List<KirmaBukinistkaOffer> offersNeedingShopifyCheck = acceptedOffers
            .Where( o =>
                o.OdooProductId is int pid
                && collidingOdooProducts.Contains( pid )
                && !string.IsNullOrWhiteSpace( o.ShopifyProductId ) )
            .ToList();
        HashSet<string> deadShopifyKeys = await FindDeadShopifyKeysAsync(
            shop,
            accessToken,
            offersNeedingShopifyCheck,
            cancellationToken );

        Dictionary<int, Queue<OfferBucket>> sharedBukToKirmaByProduct = new();
        Dictionary<int, Queue<OfferBucket>> consignmentAfterOwnByProduct = new();
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

            string offerKey = BuildShopifyInventoryKey( offer.ShopifyProductId, offer.ShopifyVariantId );
            if (deadShopifyKeys.Contains( offerKey ))
            {
                _logger.LogWarning(
                    "Skipping accepted offer {OfferId}: Shopify {Key} no longer exists.",
                    offer.Id,
                    offerKey );
                continue;
            }

            Dictionary<int, Queue<OfferBucket>> target = IsOrdinaryBukToKirma( offer )
                ? sharedBukToKirmaByProduct
                : consignmentAfterOwnByProduct;

            if (!target.TryGetValue( odooProductId, out Queue<OfferBucket>? queue ))
            {
                queue = new Queue<OfferBucket>();
                target[odooProductId] = queue;
            }

            queue.Enqueue( new OfferBucket( offer, remaining ) );
        }

        int linesProcessed = 0;
        int unitsSynced = 0;
        int maxOrderId = state.LastProcessedOrderId ?? 0;
        Dictionary<string, int> shopifyDeltas = new( StringComparer.Ordinal );

        // Fix earlier misclassification: own-buffer ate shared Buk→Kirma units.
        unitsSynced += await ReattributeMisclassifiedOwnStockAsync(
            sharedBukToKirmaByProduct,
            shopifyDeltas,
            now,
            cancellationToken );

        foreach (OdooPosSalesReader.PosOrderLine line in lines)
        {
            maxOrderId = Math.Max( maxOrderId, line.OrderId );
            if (alreadyProcessedLineIds.Contains( line.LineId ))
            {
                continue;
            }

            int lineQty = (int)Math.Floor( line.Quantity );
            if (lineQty > 0 && TryConsumeCrossSourceDuplicate(
                    recordedSales,
                    consumedRecordedSaleIndexes,
                    line.LineId,
                    line.ProductId,
                    lineQty,
                    line.SoldAtUtc ))
            {
                alreadyProcessedLineIds.Add( line.LineId );
                continue;
            }

            bool hasOwn = ownBuffers.TryGetValue( line.ProductId, out KirmaBukinistkaOdooOwnStockBuffer? buffer )
                          && buffer is not null
                          && buffer.OwnQtyRemaining > 0;
            bool hasSharedBuk = sharedBukToKirmaByProduct.TryGetValue(
                                    line.ProductId,
                                    out Queue<OfferBucket>? sharedQueue )
                                && sharedQueue is not null
                                && sharedQueue.Count > 0;
            bool hasConsignment = consignmentAfterOwnByProduct.TryGetValue(
                                      line.ProductId,
                                      out Queue<OfferBucket>? consignmentQueue )
                                  && consignmentQueue is not null
                                  && consignmentQueue.Count > 0;
            bool hasPendingBukToKirma = pendingBukToKirmaOdooIds.Contains( line.ProductId );
            if (!hasOwn && !hasSharedBuk && !hasConsignment && !hasPendingBukToKirma)
            {
                continue;
            }

            int toAllocate = lineQty;
            if (toAllocate <= 0)
            {
                continue;
            }

            List<KirmaBukinistkaPosSale> createdForLine = new();

            // 1) Ordinary Buk→Kirma shared stock (already on Shopify) first.
            toAllocate = AllocateToOfferBuckets(
                line,
                sharedQueue,
                toAllocate,
                now,
                createdForLine,
                shopifyDeltas,
                ref unitsSynced );

            // 2) Bukinistka's own pre-receipt stock — no Shopify delta.
            if (toAllocate > 0 && hasOwn && buffer is not null)
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

            // 3) Kirma→Buk / assignment consignment → Shopify inventory decrease.
            toAllocate = AllocateToOfferBuckets(
                line,
                consignmentQueue,
                toAllocate,
                now,
                createdForLine,
                shopifyDeltas,
                ref unitsSynced );

            // 4) Leftover → shrink Pending Buk→Kirma offers for this Odoo product.
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
                minOrderIdExclusive: null,
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

        // Apply Shopify deltas one product at a time. A missing/deleted Shopify
        // variant must not abort the whole POS sync — otherwise unrelated books
        // (e.g. Віно) never get inventory or history updates.
        HashSet<string> failedShopifyKeys = new( StringComparer.Ordinal );
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
                failedShopifyKeys.Add( productKey );
                _logger.LogError(
                    ex,
                    "Failed to apply Shopify inventory delta {Delta} for product {ProductId}. Skipping this product; continuing sync.",
                    delta,
                    productKey );
            }
        }

        if (failedShopifyKeys.Count > 0)
        {
            DropAddedPosSalesForFailedShopifyKeys( failedShopifyKeys, ref unitsSynced, ref linesProcessed );
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
        // Only active Kirma-attributed sales awaiting invoice. Ordinary
        // Bukinistka→Kirma stock sales affect shared availability/history but are
        // Bukinistka's own sales and must not be invoiced by Kirma.
        List<KirmaBukinistkaPosSale> rows = await _db.KirmaBukinistkaPosSales
            .AsNoTracking()
            .Where( x =>
                !x.IsOwnStock &&
                !x.IsReturn &&
                !x.IsReversed &&
                !x.IsInvoiced &&
                x.Quantity > 0 &&
                (!x.OfferId.HasValue || !_db.KirmaBukinistkaOffers.Any( offer =>
                    offer.Id == x.OfferId.Value
                    && offer.Direction == KirmaBukinistkaOfferDirections.BukinistkaToKirma
                    && !offer.IsAssignment )) )
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

    private void DropAddedPosSalesForFailedShopifyKeys(
        HashSet<string> failedShopifyKeys,
        ref int unitsSynced,
        ref int linesProcessed )
    {
        List<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<KirmaBukinistkaPosSale>> tracked =
            _db.ChangeTracker.Entries<KirmaBukinistkaPosSale>()
                .Where( e => e.State is EntityState.Added or EntityState.Modified )
                .ToList();

        HashSet<int> failedLineIds = new();
        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<KirmaBukinistkaPosSale> entry in tracked)
        {
            KirmaBukinistkaPosSale sale = entry.Entity;
            if (sale.IsOwnStock)
            {
                continue;
            }

            string key = BuildShopifyInventoryKey( sale.ShopifyProductId, sale.ShopifyVariantId );
            if (failedShopifyKeys.Contains( key ))
            {
                failedLineIds.Add( sale.OdooPosOrderLineId );
            }
        }

        if (failedLineIds.Count == 0)
        {
            return;
        }

        HashSet<int> droppedLineIds = new();
        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<KirmaBukinistkaPosSale> entry in tracked)
        {
            KirmaBukinistkaPosSale sale = entry.Entity;
            if (!failedLineIds.Contains( sale.OdooPosOrderLineId ))
            {
                continue;
            }

            if (entry.State == EntityState.Modified)
            {
                // Revert a reattributed own-stock row when Shopify apply failed.
                entry.CurrentValues.SetValues( entry.OriginalValues );
                entry.State = EntityState.Unchanged;
                continue;
            }

            if (sale.IsOwnStock)
            {
                KirmaBukinistkaOdooOwnStockBuffer? buffer = _db.KirmaBukinistkaOdooOwnStockBuffers.Local
                    .FirstOrDefault( b => b.OdooProductId == sale.OdooProductId );
                if (buffer is not null)
                {
                    buffer.OwnQtyRemaining += sale.Quantity;
                }
            }
            else if (!sale.IsReturn)
            {
                unitsSynced -= sale.Quantity;
            }

            droppedLineIds.Add( sale.OdooPosOrderLineId );
            entry.State = EntityState.Detached;
        }

        linesProcessed = Math.Max( 0, linesProcessed - droppedLineIds.Count );
    }

    /// <summary>
    /// Checks each distinct offer Shopify key once; returns keys that 404 (deleted).
    /// Transient errors are treated as alive so real sales are not silently skipped.
    /// </summary>
    private async Task<HashSet<string>> FindDeadShopifyKeysAsync(
        string shop,
        string accessToken,
        IReadOnlyList<KirmaBukinistkaOffer> acceptedOffers,
        CancellationToken cancellationToken )
    {
        HashSet<string> dead = new( StringComparer.Ordinal );
        IEnumerable<string> keys = acceptedOffers
            .Where( o => !string.IsNullOrWhiteSpace( o.ShopifyProductId ) )
            .Select( o => BuildShopifyInventoryKey( o.ShopifyProductId, o.ShopifyVariantId ) )
            .Distinct( StringComparer.Ordinal );

        foreach (string key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!await _inventory.ProductKeyExistsAsync( shop, accessToken, key ))
                {
                    dead.Add( key );
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning( ex, "Could not verify Shopify key {Key}; assuming alive.", key );
            }
        }

        return dead;
    }

    private static bool IsOrdinaryBukToKirma( KirmaBukinistkaOffer offer ) =>
        string.Equals(
            offer.Direction,
            KirmaBukinistkaOfferDirections.BukinistkaToKirma,
            StringComparison.OrdinalIgnoreCase )
        && !offer.IsAssignment;

    private static int AllocateToOfferBuckets(
        OdooPosSalesReader.PosOrderLine line,
        Queue<OfferBucket>? queue,
        int toAllocate,
        DateTime now,
        List<KirmaBukinistkaPosSale> createdForLine,
        Dictionary<string, int> shopifyDeltas,
        ref int unitsSynced )
    {
        while (toAllocate > 0 && queue is not null && queue.Count > 0)
        {
            OfferBucket bucket = queue.Peek();
            DateTime availableAt = bucket.Offer.AcceptedAtUtc ?? bucket.Offer.CreatedAtUtc;
            // Odoo serializes date_order without a timezone. Depending on the Odoo
            // server setting, parsing it as UTC can shift a real post-accept sale
            // up to a few hours before AcceptedAtUtc. Keep a narrow tolerance so
            // same-day sales are not silently lost during a backfill.
            if (line.SoldAtUtc < availableAt.AddHours( -4 ))
            {
                break;
            }

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

        return toAllocate;
    }

    /// <summary>
    /// Own-stock buffer previously ran before ordinary Buk→Kirma shared stock, so
    /// POS sales after accept were saved as IsOwnStock and never reduced Shopify.
    /// Reclassify those rows onto the shared offers and queue inventory deltas.
    /// </summary>
    private async Task<int> ReattributeMisclassifiedOwnStockAsync(
        Dictionary<int, Queue<OfferBucket>> sharedBukToKirmaByProduct,
        Dictionary<string, int> shopifyDeltas,
        DateTime now,
        CancellationToken cancellationToken )
    {
        if (sharedBukToKirmaByProduct.Count == 0)
        {
            return 0;
        }

        HashSet<int> odooIds = sharedBukToKirmaByProduct.Keys.ToHashSet();
        List<KirmaBukinistkaPosSale> ownSales = await _db.KirmaBukinistkaPosSales
            .Where( x =>
                odooIds.Contains( x.OdooProductId )
                && x.IsOwnStock
                && !x.IsReturn
                && !x.IsReversed
                && x.Quantity > 0 )
            .OrderBy( x => x.SoldAtUtc )
            .ThenBy( x => x.Id )
            .ToListAsync( cancellationToken );

        if (ownSales.Count == 0)
        {
            return 0;
        }

        int units = 0;
        foreach (KirmaBukinistkaPosSale sale in ownSales)
        {
            if (!sharedBukToKirmaByProduct.TryGetValue( sale.OdooProductId, out Queue<OfferBucket>? queue )
                || queue is null
                || queue.Count == 0)
            {
                continue;
            }

            int left = sale.Quantity;
            while (left > 0 && queue.Count > 0)
            {
                OfferBucket bucket = queue.Peek();
                DateTime availableAt = bucket.Offer.AcceptedAtUtc ?? bucket.Offer.CreatedAtUtc;
                if (sale.SoldAtUtc < availableAt.AddHours( -4 ))
                {
                    break;
                }

                int take = Math.Min( left, bucket.Remaining );
                if (take <= 0)
                {
                    queue.Dequeue();
                    continue;
                }

                // First take reuses the existing row; further splits would need new rows.
                // In practice own-stock rows are qty 1 for books.
                if (take == sale.Quantity)
                {
                    KirmaBukinistkaOffer offer = bucket.Offer;
                    sale.IsOwnStock = false;
                    sale.OfferId = offer.Id;
                    sale.ShopifyProductId = offer.ShopifyProductId;
                    sale.ShopifyVariantId = offer.ShopifyVariantId ?? string.Empty;
                    sale.ProductName = string.IsNullOrWhiteSpace( offer.ProductName )
                        ? sale.ProductName
                        : offer.ProductName;

                    string shopifyKey = BuildShopifyInventoryKey(
                        offer.ShopifyProductId,
                        offer.ShopifyVariantId );
                    shopifyDeltas[shopifyKey] =
                        shopifyDeltas.GetValueOrDefault( shopifyKey ) - take;
                    units += take;
                }
                else
                {
                    // Partial: shrink original own row and add a shared sale for take.
                    KirmaBukinistkaOffer offer = bucket.Offer;
                    sale.Quantity -= take;
                    _db.KirmaBukinistkaPosSales.Add( new KirmaBukinistkaPosSale
                    {
                        OdooPosOrderId = sale.OdooPosOrderId,
                        OdooPosOrderLineId = sale.OdooPosOrderLineId,
                        OdooPosOrderName = sale.OdooPosOrderName,
                        OfferId = offer.Id,
                        OdooProductId = sale.OdooProductId,
                        ShopifyProductId = offer.ShopifyProductId,
                        ShopifyVariantId = offer.ShopifyVariantId ?? string.Empty,
                        Quantity = take,
                        ProductName = string.IsNullOrWhiteSpace( offer.ProductName )
                            ? sale.ProductName
                            : offer.ProductName,
                        IsOwnStock = false,
                        SoldAtUtc = sale.SoldAtUtc,
                        CreatedAtUtc = now,
                    } );

                    string shopifyKey = BuildShopifyInventoryKey(
                        offer.ShopifyProductId,
                        offer.ShopifyVariantId );
                    shopifyDeltas[shopifyKey] =
                        shopifyDeltas.GetValueOrDefault( shopifyKey ) - take;
                    units += take;
                }

                bucket.Remaining -= take;
                left -= take;
                if (bucket.Remaining <= 0)
                {
                    queue.Dequeue();
                }
            }
        }

        if (units > 0)
        {
            _logger.LogInformation(
                "Reattributed {Units} own-stock POS units to ordinary Buk→Kirma offers.",
                units );
        }

        return units;
    }

    /// <summary>
    /// Consumes one recorded sale that already covers this Odoo line via the
    /// other source (pos.order.line ↔ WH/POS stock.move). Returns true when the
    /// incoming line must be skipped to avoid a second Shopify deduction.
    /// </summary>
    private static bool TryConsumeCrossSourceDuplicate(
        List<(int ProductId, int Quantity, DateTime SoldAtUtc, int LineId)> recordedSales,
        HashSet<int> consumedIndexes,
        int incomingLineId,
        int productId,
        int quantity,
        DateTime soldAtUtc )
    {
        bool incomingIsStockMove = incomingLineId < 0;
        int bestIndex = -1;
        double bestHours = double.MaxValue;

        for (int i = 0; i < recordedSales.Count; i++)
        {
            if (consumedIndexes.Contains( i ))
            {
                continue;
            }

            (int ProductId, int Quantity, DateTime SoldAtUtc, int LineId) recorded = recordedSales[i];
            bool recordedIsStockMove = recorded.LineId < 0;
            // Only pair opposite sources.
            if (incomingIsStockMove == recordedIsStockMove)
            {
                continue;
            }

            if (recorded.ProductId != productId || recorded.Quantity != quantity)
            {
                continue;
            }

            double hours = Math.Abs( (recorded.SoldAtUtc - soldAtUtc).TotalHours );
            if (hours > 12 || hours >= bestHours)
            {
                continue;
            }

            bestHours = hours;
            bestIndex = i;
        }

        if (bestIndex < 0)
        {
            return false;
        }

        consumedIndexes.Add( bestIndex );
        return true;
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
