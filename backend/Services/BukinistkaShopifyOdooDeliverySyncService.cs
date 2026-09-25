using backend.Data;
using backend.Models;
using backend.Services.Auth;
using backend.Services.Odoo;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// Shopify sales of SyncOnSale accepted offers → Odoo Wydanie (partner Kirma.sh, note = order #).
/// Multiple matching books from one Shopify order go into one picking.
/// </summary>
public sealed class BukinistkaShopifyOdooDeliverySyncService
{
    public const string CashSaleOrderIdPrefix = "cash-";

    private readonly AppDbContext _db;
    private readonly ShopifyOrderFetchService _orders;
    private readonly OdooAuthService _odooAuth;
    private readonly OdooStockDeliveryService _deliveries;
    private readonly KirmaBukinistkaOfferService _offers;
    private readonly IConfiguration _config;
    private readonly ILogger<BukinistkaShopifyOdooDeliverySyncService> _logger;

    public BukinistkaShopifyOdooDeliverySyncService(
        AppDbContext db,
        ShopifyOrderFetchService orders,
        OdooAuthService odooAuth,
        OdooStockDeliveryService deliveries,
        KirmaBukinistkaOfferService offers,
        IConfiguration config,
        ILogger<BukinistkaShopifyOdooDeliverySyncService> logger )
    {
        _db = db;
        _orders = orders;
        _odooAuth = odooAuth;
        _deliveries = deliveries;
        _offers = offers;
        _config = config;
        _logger = logger;
    }

    public static string BuildCashSaleOrderId( int cashSaleId ) =>
        $"{CashSaleOrderIdPrefix}{cashSaleId}";

    public async Task<KirmaBukinistkaShopifyDeliverySyncResultDto> SyncAsync(
        CancellationToken cancellationToken = default )
    {
        DateTime now = DateTime.UtcNow;
        string shop = (_config["Shopify:Shop"] ?? string.Empty).Trim();
        string accessToken = (_config["Shopify:AccessToken"] ?? string.Empty).Trim();
        string odooLogin = (_config["Odoo:SyncLogin"] ?? string.Empty).Trim();
        string odooPassword = _config["Odoo:SyncPassword"] ?? string.Empty;
        string odooBase = (_config["Odoo:BaseUrl"] ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace( shop ) || string.IsNullOrWhiteSpace( accessToken ))
        {
            return new KirmaBukinistkaShopifyDeliverySyncResultDto
            {
                Skipped = true,
                SkipReason = "Shopify Shop/AccessToken не наладжаныя ў канфігу.",
                SyncedAtUtc = now,
            };
        }

        if (string.IsNullOrWhiteSpace( odooLogin )
            || string.IsNullOrWhiteSpace( odooPassword )
            || string.IsNullOrWhiteSpace( odooBase ))
        {
            return new KirmaBukinistkaShopifyDeliverySyncResultDto
            {
                Skipped = true,
                SkipReason = "Odoo SyncLogin/SyncPassword/BaseUrl не наладжаныя ў канфігу.",
                SyncedAtUtc = now,
            };
        }

        KirmaBukinistkaShopifyDeliverySyncState state = await _db.KirmaBukinistkaShopifyDeliverySyncStates
            .OrderBy( x => x.Id )
            .FirstOrDefaultAsync( cancellationToken )
            ?? new KirmaBukinistkaShopifyDeliverySyncState();

        if (state.Id == 0)
        {
            _db.KirmaBukinistkaShopifyDeliverySyncStates.Add( state );
        }

        // First run: only look at recent window so we don't backfill years of orders.
        DateTime since = state.LastSyncedAtUtc ?? now.AddDays( -14 );

        OdooSession odooSession = await _odooAuth.AuthenticateAsync( odooLogin, odooPassword );

        int pickingsCancelled = await CancelDeliveriesForCancelledShopifyOrdersAsync(
            shop,
            accessToken,
            odooSession,
            now,
            cancellationToken );

        List<KirmaBukinistkaOffer> syncOffers = await _db.KirmaBukinistkaOffers
            .Where( x =>
                x.Status == KirmaBukinistkaOfferStatuses.Accepted
                && x.SyncOnSale
                && x.OdooProductId != null
                && x.OdooProductId > 0 )
            .OrderBy( x => x.CreatedAtUtc )
            .ThenBy( x => x.Id )
            .ToListAsync( cancellationToken );

        bool hasPendingKirmaToBuk = await _offers.HasPendingKirmaToBukOffersAsync( cancellationToken );

        if (syncOffers.Count == 0 && !hasPendingKirmaToBuk)
        {
            state.LastSyncedAtUtc = now;
            await _db.SaveChangesAsync( cancellationToken );
            return new KirmaBukinistkaShopifyDeliverySyncResultDto
            {
                Skipped = false,
                OrdersScanned = 0,
                PickingsCreated = 0,
                PickingsCancelled = pickingsCancelled,
                UnitsSynced = 0,
                SyncedAtUtc = now,
            };
        }

        Dictionary<int, int> alreadySyncedByOffer = await _db.KirmaBukinistkaShopifyDeliverySyncs
            .AsNoTracking()
            .Where( x => !x.IsCancelled )
            .GroupBy( x => x.OfferId )
            .Select( g => new { OfferId = g.Key, Qty = g.Sum( x => x.Quantity ) } )
            .ToDictionaryAsync( x => x.OfferId, x => x.Qty, cancellationToken );

        // POS sales of the same consignment also consume accepted Kirma qty at Bukinistka.
        Dictionary<int, int> posSoldByOffer = await _db.KirmaBukinistkaPosSales
            .AsNoTracking()
            .Where( x =>
                x.OfferId != null
                && !x.IsOwnStock
                && !x.IsReversed
                && !x.IsReturn
                && x.Quantity > 0 )
            .GroupBy( x => x.OfferId!.Value )
            .Select( g => new { OfferId = g.Key, Qty = g.Sum( x => x.Quantity ) } )
            .ToDictionaryAsync( x => x.OfferId, x => x.Qty, cancellationToken );

        // shopifyProductId -> FIFO remaining offer buckets
        Dictionary<string, Queue<OfferBucket>> remainingByProduct =
            new( StringComparer.OrdinalIgnoreCase );
        foreach (KirmaBukinistkaOffer offer in syncOffers)
        {
            // Every accepted offer is a shared availability bucket. Odoo POS sales
            // consume it regardless of direction; otherwise a later Shopify order
            // could allocate stock that Bukinistka has already sold.
            int used =
                alreadySyncedByOffer.GetValueOrDefault( offer.Id )
                + posSoldByOffer.GetValueOrDefault( offer.Id );

            int remaining = offer.Quantity - used;
            if (remaining <= 0)
            {
                continue;
            }

            string productKey = offer.ShopifyProductId.Trim();
            if (string.IsNullOrWhiteSpace( productKey ))
            {
                continue;
            }

            if (!remainingByProduct.TryGetValue( productKey, out Queue<OfferBucket>? queue ))
            {
                queue = new Queue<OfferBucket>();
                remainingByProduct[productKey] = queue;
            }

            queue.Enqueue( new OfferBucket( offer, remaining ) );
        }

        if (remainingByProduct.Count == 0 && !hasPendingKirmaToBuk)
        {
            state.LastSyncedAtUtc = now;
            await _db.SaveChangesAsync( cancellationToken );
            return new KirmaBukinistkaShopifyDeliverySyncResultDto
            {
                Skipped = false,
                OrdersScanned = 0,
                PickingsCreated = 0,
                PickingsCancelled = pickingsCancelled,
                UnitsSynced = 0,
                SyncedAtUtc = now,
            };
        }

        HashSet<string> alreadyProcessedOrderIds = await _db.KirmaBukinistkaShopifyDeliverySyncs
            .AsNoTracking()
            .Select( x => x.ShopifyOrderId )
            .Distinct()
            .ToHashSetAsync( cancellationToken );

        List<ShopifyOrderDto> orders = await _orders.FetchOrdersSinceWithCredentialsAsync(
            shop,
            accessToken,
            since );

        Dictionary<int, int> uomCache = new();

        int pickingsCreated = 0;
        int unitsSynced = 0;

        foreach (ShopifyOrderDto order in orders.OrderBy( x => x.CreatedAtUtc ))
        {
            List<(string ProductId, string VariantId, int Quantity)> saleLines = new();
            Dictionary<string, int> qtyByProduct = new( StringComparer.OrdinalIgnoreCase );
            Dictionary<string, string> variantByProduct = new( StringComparer.OrdinalIgnoreCase );
            foreach (ShopifyLineItemDto item in order.Items)
            {
                if (item.Quantity <= 0)
                {
                    continue;
                }

                string productId = ShopifyIds.NormalizeProductId( item.ShopifyProductId ).Trim();
                if (string.IsNullOrWhiteSpace( productId ))
                {
                    continue;
                }

                string variantId = string.IsNullOrWhiteSpace( item.ShopifyVariantId )
                    ? string.Empty
                    : ShopifyIds.NormalizeVariantId( item.ShopifyVariantId.Trim() );
                saleLines.Add( (productId, variantId, item.Quantity) );
                qtyByProduct[productId] = qtyByProduct.GetValueOrDefault( productId ) + item.Quantity;
                if (!variantByProduct.ContainsKey( productId ) && !string.IsNullOrWhiteSpace( variantId ))
                {
                    variantByProduct[productId] = variantId;
                }
            }

            if (saleLines.Count > 0)
            {
                await _offers.ShrinkPendingKirmaToBukForShopifySaleAsync(
                    order.OrderId,
                    saleLines,
                    now,
                    cancellationToken );
            }

            if (alreadyProcessedOrderIds.Contains( order.OrderId )
                || remainingByProduct.Count == 0)
            {
                continue;
            }

            List<(OfferBucket Bucket, int Take, string ProductId, string VariantId)> allocations = new();

            foreach ((string productId, int soldQty) in qtyByProduct)
            {
                if (!remainingByProduct.TryGetValue( productId, out Queue<OfferBucket>? queue )
                    || queue.Count == 0)
                {
                    continue;
                }

                int toAllocate = soldQty;
                while (toAllocate > 0 && queue.Count > 0)
                {
                    OfferBucket bucket = queue.Peek();
                    int take = Math.Min( toAllocate, bucket.Remaining );
                    if (take <= 0)
                    {
                        queue.Dequeue();
                        continue;
                    }

                    string variantId = (bucket.Offer.ShopifyVariantId ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace( variantId ))
                    {
                        variantId = variantByProduct.GetValueOrDefault( productId ) ?? string.Empty;
                    }

                    allocations.Add( (bucket, take, productId, variantId) );
                    bucket.Remaining -= take;
                    toAllocate -= take;
                    if (bucket.Remaining <= 0)
                    {
                        queue.Dequeue();
                    }
                }
            }

            if (allocations.Count == 0)
            {
                continue;
            }

            // Merge same Odoo product lines for one picking.
            Dictionary<int, (int UomId, decimal Qty)> linesByOdooProduct = new();
            foreach ((OfferBucket bucket, int take, _, _) in allocations)
            {
                int odooProductId = bucket.Offer.OdooProductId!.Value;
                if (!uomCache.TryGetValue( odooProductId, out int uomId ))
                {
                    uomId = await _deliveries.ResolveProductUomIdAsync(
                        odooSession,
                        odooProductId,
                        cancellationToken );
                    uomCache[odooProductId] = uomId;
                }

                if (linesByOdooProduct.TryGetValue( odooProductId, out var existing ))
                {
                    linesByOdooProduct[odooProductId] = (existing.UomId, existing.Qty + take);
                }
                else
                {
                    linesByOdooProduct[odooProductId] = (uomId, take);
                }
            }

            List<OdooStockDeliveryService.DeliveryLine> deliveryLines = linesByOdooProduct
                .Select( kv => new OdooStockDeliveryService.DeliveryLine(
                    kv.Key,
                    kv.Value.UomId,
                    kv.Value.Qty ) )
                .ToList();

            string note = string.IsNullOrWhiteSpace( order.OrderNumber )
                ? order.OrderId
                : order.OrderNumber.Trim();

            OdooStockDeliveryService.DeliveryResult picking;
            try
            {
                picking = await _deliveries.CreateOutgoingDeliveryAsync(
                    odooSession,
                    deliveryLines,
                    note,
                    cancellationToken );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to create Odoo Wydanie for Shopify order {OrderNumber}",
                    note );
                throw;
            }

            foreach ((OfferBucket bucket, int take, string productId, string variantId) in allocations)
            {
                _db.KirmaBukinistkaShopifyDeliverySyncs.Add( new KirmaBukinistkaShopifyDeliverySync
                {
                    ShopifyOrderId = order.OrderId,
                    ShopifyOrderNumber = note,
                    ShopifyProductId = productId,
                    ShopifyVariantId = variantId,
                    OfferId = bucket.Offer.Id,
                    OdooProductId = bucket.Offer.OdooProductId!.Value,
                    Quantity = take,
                    OdooPickingId = picking.PickingId,
                    OdooPickingName = picking.PickingName,
                    SoldAtUtc = order.CreatedAtUtc,
                    CreatedAtUtc = now,
                } );
                unitsSynced += take;
            }

            alreadyProcessedOrderIds.Add( order.OrderId );
            pickingsCreated++;
        }

        state.LastSyncedAtUtc = now;
        await _db.SaveChangesAsync( cancellationToken );

        return new KirmaBukinistkaShopifyDeliverySyncResultDto
        {
            Skipped = false,
            OrdersScanned = orders.Count,
            PickingsCreated = pickingsCreated,
            PickingsCancelled = pickingsCancelled,
            UnitsSynced = unitsSynced,
            SyncedAtUtc = now,
        };
    }

    /// <summary>
    /// Cash sale in Kirma VAT report → Odoo Wydanie for remaining SyncOnSale accepted qty.
    /// </summary>
    public Task CreateForCashSaleAsync(
        int cashSaleId,
        string shopifyProductId,
        string shopifyVariantId,
        int quantity,
        DateTime soldAtUtc,
        CancellationToken cancellationToken = default )
    {
        if (cashSaleId <= 0 || quantity <= 0)
        {
            return Task.CompletedTask;
        }

        return CreateForManualSaleOrderAsync(
            BuildCashSaleOrderId( cashSaleId ),
            $"Наяўнымі #{cashSaleId}",
            soldAtUtc,
            [(shopifyProductId, shopifyVariantId, quantity)],
            cancellationToken );
    }

    /// <summary>
    /// Manual VAT-report sale (cash / foreign / etc.) → one Odoo Wydanie for SyncOnSale remaining qty.
    /// No-op when nothing SyncOnSale remains. Throws if SyncOnSale remains but Odoo is misconfigured.
    /// Idempotent per <paramref name="syncOrderId"/>.
    /// </summary>
    public async Task CreateForManualSaleOrderAsync(
        string syncOrderId,
        string note,
        DateTime soldAtUtc,
        IReadOnlyList<(string ProductId, string VariantId, int Quantity)> lines,
        CancellationToken cancellationToken = default )
    {
        string orderId = (syncOrderId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( orderId ) || lines.Count == 0)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        await _offers.ShrinkPendingKirmaToBukForShopifySaleAsync(
            orderId,
            lines,
            nowUtc,
            cancellationToken );

        bool alreadyExists = await _db.KirmaBukinistkaShopifyDeliverySyncs
            .AsNoTracking()
            .AnyAsync( x => x.ShopifyOrderId == orderId && !x.IsCancelled, cancellationToken );
        if (alreadyExists)
        {
            await _db.SaveChangesAsync( cancellationToken );
            return;
        }

        Dictionary<string, int> qtyByProduct = new( StringComparer.OrdinalIgnoreCase );
        Dictionary<string, string> variantByProduct = new( StringComparer.OrdinalIgnoreCase );
        foreach ((string rawProductId, string rawVariantId, int quantity) in lines)
        {
            if (quantity <= 0)
            {
                continue;
            }

            string productId = ShopifyIds.NormalizeProductId( rawProductId ).Trim();
            if (string.IsNullOrWhiteSpace( productId ))
            {
                continue;
            }

            qtyByProduct[productId] = qtyByProduct.GetValueOrDefault( productId ) + quantity;
            if (!variantByProduct.ContainsKey( productId ))
            {
                variantByProduct[productId] = string.IsNullOrWhiteSpace( rawVariantId )
                    ? string.Empty
                    : ShopifyIds.NormalizeVariantId( rawVariantId ).Trim();
            }
        }

        if (qtyByProduct.Count == 0)
        {
            return;
        }

        List<string> productIds = qtyByProduct.Keys.ToList();
        List<KirmaBukinistkaOffer> syncOffers = await _db.KirmaBukinistkaOffers
            .Where( x =>
                x.Status == KirmaBukinistkaOfferStatuses.Accepted
                && x.SyncOnSale
                && x.OdooProductId != null
                && x.OdooProductId > 0
                && productIds.Contains( x.ShopifyProductId ) )
            .OrderBy( x => x.CreatedAtUtc )
            .ThenBy( x => x.Id )
            .ToListAsync( cancellationToken );

        if (syncOffers.Count == 0)
        {
            return;
        }

        Dictionary<int, int> alreadySyncedByOffer = await _db.KirmaBukinistkaShopifyDeliverySyncs
            .AsNoTracking()
            .Where( x => !x.IsCancelled )
            .GroupBy( x => x.OfferId )
            .Select( g => new { OfferId = g.Key, Qty = g.Sum( x => x.Quantity ) } )
            .ToDictionaryAsync( x => x.OfferId, x => x.Qty, cancellationToken );

        Dictionary<int, int> posSoldByOffer = await _db.KirmaBukinistkaPosSales
            .AsNoTracking()
            .Where( x =>
                x.OfferId != null
                && !x.IsOwnStock
                && !x.IsReversed
                && !x.IsReturn
                && x.Quantity > 0 )
            .GroupBy( x => x.OfferId!.Value )
            .Select( g => new { OfferId = g.Key, Qty = g.Sum( x => x.Quantity ) } )
            .ToDictionaryAsync( x => x.OfferId, x => x.Qty, cancellationToken );

        Dictionary<string, Queue<OfferBucket>> remainingByProduct =
            new( StringComparer.OrdinalIgnoreCase );
        foreach (KirmaBukinistkaOffer offer in syncOffers)
        {
            int used =
                alreadySyncedByOffer.GetValueOrDefault( offer.Id )
                + posSoldByOffer.GetValueOrDefault( offer.Id );
            int remaining = offer.Quantity - used;
            if (remaining <= 0)
            {
                continue;
            }

            string productKey = offer.ShopifyProductId.Trim();
            if (!remainingByProduct.TryGetValue( productKey, out Queue<OfferBucket>? queue ))
            {
                queue = new Queue<OfferBucket>();
                remainingByProduct[productKey] = queue;
            }

            queue.Enqueue( new OfferBucket( offer, remaining ) );
        }

        if (remainingByProduct.Count == 0)
        {
            return;
        }

        string odooLogin = (_config["Odoo:SyncLogin"] ?? string.Empty).Trim();
        string odooPassword = _config["Odoo:SyncPassword"] ?? string.Empty;
        string odooBase = (_config["Odoo:BaseUrl"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( odooLogin )
            || string.IsNullOrWhiteSpace( odooPassword )
            || string.IsNullOrWhiteSpace( odooBase ))
        {
            throw new InvalidOperationException(
                "Тавар сінхранізаваны з Букіністкай (Sync пры продажы), але Odoo SyncLogin/SyncPassword/BaseUrl не наладжаныя." );
        }

        List<(OfferBucket Bucket, int Take, string ProductId, string VariantId)> allocations = new();
        foreach ((string productId, int soldQty) in qtyByProduct)
        {
            if (!remainingByProduct.TryGetValue( productId, out Queue<OfferBucket>? queue )
                || queue.Count == 0)
            {
                continue;
            }

            string variantId = variantByProduct.GetValueOrDefault( productId ) ?? string.Empty;
            int toAllocate = soldQty;
            while (toAllocate > 0 && queue.Count > 0)
            {
                OfferBucket bucket = queue.Peek();
                int take = Math.Min( toAllocate, bucket.Remaining );
                if (take <= 0)
                {
                    queue.Dequeue();
                    continue;
                }

                string offerVariant = (bucket.Offer.ShopifyVariantId ?? string.Empty).Trim();
                allocations.Add( (
                    bucket,
                    take,
                    productId,
                    string.IsNullOrWhiteSpace( offerVariant ) ? variantId : offerVariant) );
                bucket.Remaining -= take;
                toAllocate -= take;
                if (bucket.Remaining <= 0)
                {
                    queue.Dequeue();
                }
            }
        }

        if (allocations.Count == 0)
        {
            return;
        }

        OdooSession odooSession = await _odooAuth.AuthenticateAsync( odooLogin, odooPassword );
        Dictionary<int, int> uomCache = new();
        Dictionary<int, (int UomId, decimal Qty)> linesByOdooProduct = new();
        foreach ((OfferBucket bucket, int take, _, _) in allocations)
        {
            int odooProductId = bucket.Offer.OdooProductId!.Value;
            if (!uomCache.TryGetValue( odooProductId, out int uomId ))
            {
                uomId = await _deliveries.ResolveProductUomIdAsync(
                    odooSession,
                    odooProductId,
                    cancellationToken );
                uomCache[odooProductId] = uomId;
            }

            if (linesByOdooProduct.TryGetValue( odooProductId, out var existing ))
            {
                linesByOdooProduct[odooProductId] = (existing.UomId, existing.Qty + take);
            }
            else
            {
                linesByOdooProduct[odooProductId] = (uomId, take);
            }
        }

        List<OdooStockDeliveryService.DeliveryLine> deliveryLines = linesByOdooProduct
            .Select( kv => new OdooStockDeliveryService.DeliveryLine(
                kv.Key,
                kv.Value.UomId,
                kv.Value.Qty ) )
            .ToList();

        string pickingNote = string.IsNullOrWhiteSpace( note ) ? orderId : note.Trim();
        DateTime now = DateTime.UtcNow;

        OdooStockDeliveryService.DeliveryResult picking;
        try
        {
            picking = await _deliveries.CreateOutgoingDeliveryAsync(
                odooSession,
                deliveryLines,
                pickingNote,
                cancellationToken );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to create Odoo Wydanie for report sale {OrderId}",
                orderId );
            throw;
        }

        foreach ((OfferBucket bucket, int take, string productId, string variantId) in allocations)
        {
            _db.KirmaBukinistkaShopifyDeliverySyncs.Add( new KirmaBukinistkaShopifyDeliverySync
            {
                ShopifyOrderId = orderId,
                ShopifyOrderNumber = pickingNote,
                ShopifyProductId = productId,
                ShopifyVariantId = variantId,
                OfferId = bucket.Offer.Id,
                OdooProductId = bucket.Offer.OdooProductId!.Value,
                Quantity = take,
                OdooPickingId = picking.PickingId,
                OdooPickingName = picking.PickingName,
                SoldAtUtc = soldAtUtc,
                CreatedAtUtc = now,
            } );
        }

        await _db.SaveChangesAsync( cancellationToken );
    }

    public Task CancelForCashSaleAsync(
        int cashSaleId,
        CancellationToken cancellationToken = default )
    {
        if (cashSaleId <= 0)
        {
            return Task.CompletedTask;
        }

        return CancelForManualSaleOrderAsync( BuildCashSaleOrderId( cashSaleId ), cancellationToken );
    }

    public async Task CancelForManualSaleOrderAsync(
        string syncOrderId,
        CancellationToken cancellationToken = default )
    {
        string orderId = (syncOrderId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( orderId ))
        {
            return;
        }

        List<KirmaBukinistkaShopifyDeliverySync> rows = await _db.KirmaBukinistkaShopifyDeliverySyncs
            .Where( x => x.ShopifyOrderId == orderId && !x.IsCancelled )
            .ToListAsync( cancellationToken );
        if (rows.Count == 0)
        {
            return;
        }

        string odooLogin = (_config["Odoo:SyncLogin"] ?? string.Empty).Trim();
        string odooPassword = _config["Odoo:SyncPassword"] ?? string.Empty;
        string odooBase = (_config["Odoo:BaseUrl"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( odooLogin )
            || string.IsNullOrWhiteSpace( odooPassword )
            || string.IsNullOrWhiteSpace( odooBase ))
        {
            throw new InvalidOperationException(
                "Не ўдалося адмяніць Wydanie: Odoo SyncLogin/SyncPassword/BaseUrl не наладжаныя." );
        }

        OdooSession odooSession = await _odooAuth.AuthenticateAsync( odooLogin, odooPassword );
        DateTime now = DateTime.UtcNow;
        HashSet<int> pickingIds = rows
            .Select( x => x.OdooPickingId )
            .Where( id => id > 0 )
            .ToHashSet();

        foreach (int pickingId in pickingIds)
        {
            await _deliveries.CancelOutgoingDeliveryAsync(
                odooSession,
                pickingId,
                cancellationToken );
        }

        foreach (KirmaBukinistkaShopifyDeliverySync row in rows)
        {
            row.IsCancelled = true;
            row.CancelledAtUtc = now;
        }

        await _db.SaveChangesAsync( cancellationToken );
    }

    private async Task<int> CancelDeliveriesForCancelledShopifyOrdersAsync(
        string shop,
        string accessToken,
        OdooSession odooSession,
        DateTime now,
        CancellationToken cancellationToken )
    {
        List<KirmaBukinistkaShopifyDeliverySync> activeRows = await _db.KirmaBukinistkaShopifyDeliverySyncs
            .Where( x => !x.IsCancelled )
            .ToListAsync( cancellationToken );
        if (activeRows.Count == 0)
        {
            return 0;
        }

        List<string> orderIds = activeRows
            // Manual/cash VAT-report Wydanie rows are not real Shopify orders — skip cancel polling.
            .Where( x =>
                !x.ShopifyOrderId.StartsWith( CashSaleOrderIdPrefix, StringComparison.OrdinalIgnoreCase )
                && !x.ShopifyOrderId.StartsWith( "manual-", StringComparison.OrdinalIgnoreCase ) )
            .Select( x => ShopifyIds.NormalizeOrderId( x.ShopifyOrderId ) )
            .Where( x => !string.IsNullOrWhiteSpace( x ) )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .ToList();

        HashSet<string> cancelledOrderIds = await _orders.GetCancelledOrderIdsWithCredentialsAsync(
            shop,
            accessToken,
            orderIds );
        if (cancelledOrderIds.Count == 0)
        {
            _logger.LogInformation(
                "No cancelled Shopify orders among {Count} active Wydanie sync rows.",
                activeRows.Count );
            return 0;
        }

        List<KirmaBukinistkaShopifyDeliverySync> toCancel = activeRows
            .Where( x => cancelledOrderIds.Contains( ShopifyIds.NormalizeOrderId( x.ShopifyOrderId ) ) )
            .ToList();

        _logger.LogInformation(
            "Cancelling {Pickings} Odoo Wydanie for {Orders} cancelled Shopify orders.",
            toCancel.Select( x => x.OdooPickingId ).Distinct().Count(),
            cancelledOrderIds.Count );

        HashSet<int> pickingIds = toCancel
            .Select( x => x.OdooPickingId )
            .Where( id => id > 0 )
            .ToHashSet();

        int cancelledPickings = 0;
        foreach (int pickingId in pickingIds)
        {
            try
            {
                await _deliveries.CancelOutgoingDeliveryAsync(
                    odooSession,
                    pickingId,
                    cancellationToken );
                cancelledPickings++;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to cancel Odoo Wydanie {PickingId} after Shopify order cancel",
                    pickingId );
                throw;
            }
        }

        foreach (KirmaBukinistkaShopifyDeliverySync row in toCancel)
        {
            row.IsCancelled = true;
            row.CancelledAtUtc = now;
        }

        return cancelledPickings;
    }

    public async Task<List<KirmaBukinistkaShopifyDeliverySaleDto>> ListSentShopifySalesAsync(
        CancellationToken cancellationToken = default )
    {
        List<KirmaBukinistkaShopifyDeliverySync> rows = await _db.KirmaBukinistkaShopifyDeliverySyncs
            .AsNoTracking()
            .Where( x => !x.IsCancelled && x.Quantity > 0 )
            .OrderByDescending( x => x.SoldAtUtc )
            .ThenByDescending( x => x.Id )
            .Take( 500 )
            .ToListAsync( cancellationToken );

        HashSet<int> offerIds = rows
            .Select( x => x.OfferId )
            .Where( id => id > 0 )
            .ToHashSet();
        if (offerIds.Count == 0)
        {
            return [];
        }

        Dictionary<int, KirmaBukinistkaOffer> offers = await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .Where( o =>
                offerIds.Contains( o.Id )
                && o.Direction == KirmaBukinistkaOfferDirections.BukinistkaToKirma )
            .ToDictionaryAsync( o => o.Id, cancellationToken );

        List<KirmaBukinistkaShopifyDeliverySaleDto> result = new();
        foreach (KirmaBukinistkaShopifyDeliverySync row in rows)
        {
            if (!offers.TryGetValue( row.OfferId, out KirmaBukinistkaOffer? offer ))
            {
                continue;
            }

            result.Add( new KirmaBukinistkaShopifyDeliverySaleDto
            {
                Id = row.Id,
                OfferId = row.OfferId,
                ShopifyOrderId = row.ShopifyOrderId,
                ShopifyOrderNumber = row.ShopifyOrderNumber,
                OdooPickingName = row.OdooPickingName,
                Quantity = row.Quantity,
                ProductName = offer.ProductName,
                GrossUnitCost = Math.Round( offer.GrossUnitCost, 2, MidpointRounding.AwayFromZero ),
                SupplierName = string.IsNullOrWhiteSpace( offer.SupplierName )
                    ? null
                    : offer.SupplierName.Trim(),
                SoldAtUtc = row.SoldAtUtc,
                CreatedAtUtc = row.CreatedAtUtc,
            } );
        }

        return result;
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
