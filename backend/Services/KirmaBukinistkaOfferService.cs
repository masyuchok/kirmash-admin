using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Services.Auth;
using backend.Services.Odoo;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public sealed class KirmaBukinistkaOfferService
{
    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _http;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly OdooProductService _odooProducts;
    private readonly OdooStockReceiptService _odooReceipts;

    public KirmaBukinistkaOfferService(
        AppDbContext db,
        IHttpContextAccessor http,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        OdooProductService odooProducts,
        OdooStockReceiptService odooReceipts )
    {
        _db = db;
        _http = http;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _odooProducts = odooProducts;
        _odooReceipts = odooReceipts;
    }

    public async Task<KirmaBukinistkaOfferDto> CreateAsync( KirmaBukinistkaOfferCreateRequest request )
    {
        if (!ShopifySessionReader.TryGet( _http, out ShopifySession session ))
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Kirma." );
        }

        string productId = ShopifyIds.NormalizeProductId( (request.ShopifyProductId ?? string.Empty).Trim() );
        if (string.IsNullOrWhiteSpace( productId ))
        {
            throw new InvalidOperationException( "Некарэктны ідэнтыфікатар тавару." );
        }

        string productName = (request.ProductName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( productName ))
        {
            throw new InvalidOperationException( "Укажыце назву тавару." );
        }

        if (request.Quantity <= 0)
        {
            throw new InvalidOperationException( "Колькасць павінна быць больш за нуль." );
        }

        if (request.GrossUnitCost < 0m)
        {
            throw new InvalidOperationException( "Кошт брута не можа быць адмоўным." );
        }

        string variantId = string.IsNullOrWhiteSpace( request.ShopifyVariantId )
            ? string.Empty
            : ShopifyIds.NormalizeVariantId( request.ShopifyVariantId.Trim() );

        string adminUrl = (request.ProductAdminUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( adminUrl ))
        {
            string storeSlug = session.Shop.Replace( ".myshopify.com", "", StringComparison.OrdinalIgnoreCase );
            adminUrl = $"https://admin.shopify.com/store/{storeSlug}/products/{productId}";
        }

        string storefrontUrl = await TryBuildStorefrontUrlAsync( session, productId ) ?? string.Empty;
        string createdBy = _http.HttpContext?.User?.FindFirst( "sub" )?.Value
            ?? _http.HttpContext?.User?.Identity?.Name
            ?? session.Shop;

        KirmaBukinistkaOffer row = new()
        {
            ShopifyProductId = productId,
            ShopifyVariantId = variantId,
            ProductName = productName,
            ProductAuthor = (request.ProductAuthor ?? string.Empty).Trim(),
            MainImageUrl = string.IsNullOrWhiteSpace( request.MainImageUrl )
                ? null
                : request.MainImageUrl.Trim(),
            ProductAdminUrl = adminUrl,
            StorefrontUrl = storefrontUrl,
            SupplierName = string.IsNullOrWhiteSpace( request.SupplierName )
                ? null
                : request.SupplierName.Trim(),
            Quantity = request.Quantity,
            GrossUnitCost = Math.Round( request.GrossUnitCost, 2, MidpointRounding.AwayFromZero ),
            CreatedByLogin = createdBy,
            Status = KirmaBukinistkaOfferStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        };

        _db.KirmaBukinistkaOffers.Add( row );
        await _db.SaveChangesAsync();
        return ToDto( row );
    }

    public async Task<List<KirmaBukinistkaOfferDto>> ListForBukinistkaAsync( HttpRequest request )
    {
        if (BukinistkaJwtAuthentication.TryValidateCookie( request, _config ) is null)
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        // Bukinistka only sees pending offers; rejected stay for Kirma "Высланыя".
        List<KirmaBukinistkaOffer> rows = await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .Where( x =>
                string.IsNullOrWhiteSpace( x.Status )
                || x.Status == KirmaBukinistkaOfferStatuses.Pending )
            .OrderByDescending( x => x.CreatedAtUtc )
            .ThenByDescending( x => x.Id )
            .Take( 1000 )
            .ToListAsync();

        return rows.Select( ToDto ).ToList();
    }

    public async Task<int> CountPendingForBukinistkaAsync( HttpRequest request )
    {
        if (BukinistkaJwtAuthentication.TryValidateCookie( request, _config ) is null)
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        return await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .CountAsync( x =>
                string.IsNullOrWhiteSpace( x.Status )
                || x.Status == KirmaBukinistkaOfferStatuses.Pending );
    }

    public async Task<List<KirmaBukinistkaOfferDto>> ListSentForKirmaAsync()
    {
        if (!ShopifySessionReader.TryGet( _http, out _ ))
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Kirma." );
        }

        List<KirmaBukinistkaOffer> rows = await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .OrderByDescending( x => x.CreatedAtUtc )
            .ThenByDescending( x => x.Id )
            .Take( 1000 )
            .ToListAsync();

        return rows.Select( ToDto ).ToList();
    }

    public async Task<KirmaBukinistkaOfferDto> UpdateSentAsync(
        int id,
        KirmaBukinistkaOfferUpdateRequest request )
    {
        RequireKirmaSession();
        KirmaBukinistkaOffer row = await RequirePendingOfferAsync( id );

        if (request.Quantity <= 0)
        {
            throw new InvalidOperationException( "Колькасць павінна быць больш за нуль." );
        }

        if (request.GrossUnitCost < 0m)
        {
            throw new InvalidOperationException( "Кошт брута не можа быць адмоўным." );
        }

        row.Quantity = request.Quantity;
        row.GrossUnitCost = Math.Round( request.GrossUnitCost, 2, MidpointRounding.AwayFromZero );
        await _db.SaveChangesAsync();
        return ToDto( row );
    }

    public async Task CancelSentAsync( int id )
    {
        RequireKirmaSession();
        KirmaBukinistkaOffer row = await RequirePendingOfferAsync( id );
        _db.KirmaBukinistkaOffers.Remove( row );
        await _db.SaveChangesAsync();
    }

    public async Task RejectForBukinistkaAsync( int id, HttpRequest request )
    {
        if (BukinistkaJwtAuthentication.TryValidateCookie( request, _config ) is null)
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        KirmaBukinistkaOffer? row = await _db.KirmaBukinistkaOffers
            .FirstOrDefaultAsync( x => x.Id == id );
        if (row is null)
        {
            throw new InvalidOperationException( "Прапанова не знойдзена." );
        }

        string status = NormalizeStatus( row.Status );
        if (!string.Equals( status, KirmaBukinistkaOfferStatuses.Pending, StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Адхіліць можна толькі новыя (неразобраныя) прапановы." );
        }

        row.Status = KirmaBukinistkaOfferStatuses.Rejected;
        await _db.SaveChangesAsync();
    }

    public async Task<KirmaBukinistkaOfferDto> AcceptForBukinistkaAsync(
        int id,
        KirmaBukinistkaOfferAcceptRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default )
    {
        if (BukinistkaJwtAuthentication.TryValidateCookie( httpRequest, _config ) is null)
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        if (request.OdooProductId <= 0)
        {
            throw new InvalidOperationException( "Выберыце прадукт Odoo для звязкі." );
        }

        KirmaBukinistkaOffer? row = await _db.KirmaBukinistkaOffers
            .FirstOrDefaultAsync( x => x.Id == id, cancellationToken );
        if (row is null)
        {
            throw new InvalidOperationException( "Прапанова не знойдзена." );
        }

        string status = NormalizeStatus( row.Status );
        if (!string.Equals( status, KirmaBukinistkaOfferStatuses.Pending, StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Прыняць можна толькі новыя (неразобраныя) прапановы." );
        }

        OdooProductService.OdooProductSnapshot snapshot = await _odooProducts.GetProductSnapshotAsync(
            httpRequest,
            request.OdooProductId,
            cancellationToken );

        int qtyBefore = (int)Math.Round(
            snapshot.QuantityInStock,
            MidpointRounding.AwayFromZero );

        bool applyListPrice = request.ListPrice.HasValue;
        decimal? acceptedListPrice = null;
        if (applyListPrice)
        {
            if (request.ListPrice!.Value < 0m)
            {
                throw new InvalidOperationException( "Цана продажу не можа быць адмоўнай." );
            }

            acceptedListPrice = Math.Round(
                request.ListPrice.Value,
                2,
                MidpointRounding.AwayFromZero );

            // Skip write when price is unchanged (within 2 decimals).
            if (acceptedListPrice.Value != Math.Round( snapshot.ListPrice, 2, MidpointRounding.AwayFromZero ))
            {
                await _odooProducts.UpdateListPriceAsync(
                    httpRequest,
                    request.OdooProductId,
                    acceptedListPrice.Value,
                    cancellationToken );
            }
            else
            {
                acceptedListPrice = null;
            }
        }

        decimal offerCost = Math.Round( row.GrossUnitCost, 2, MidpointRounding.AwayFromZero );
        decimal odooCost = Math.Round( snapshot.StandardPrice, 2, MidpointRounding.AwayFromZero );
        bool costDiffers = offerCost != odooCost;
        if (costDiffers && request.ApplyKirmaCostPrice == true)
        {
            await _odooProducts.UpdateStandardPriceAsync(
                httpRequest,
                request.OdooProductId,
                offerCost,
                cancellationToken );
        }

        await _odooProducts.IncreaseQuantityAsync(
            httpRequest,
            request.OdooProductId,
            row.Quantity,
            cancellationToken );

        await AddOwnStockBufferForAcceptAsync(
            request.OdooProductId,
            qtyBefore,
            excludeOfferId: row.Id,
            cancellationToken );

        row.OdooProductId = request.OdooProductId;
        row.OdooQuantityBeforeAccept = qtyBefore;
        row.AcceptedListPrice = acceptedListPrice;
        row.Status = KirmaBukinistkaOfferStatuses.Accepted;
        await _db.SaveChangesAsync( cancellationToken );
        return ToDto( row );
    }

    /// <summary>
    /// Batch receipt: apply price choices, create one Odoo Przyjęcia, then mark offers Accepted.
    /// </summary>
    public async Task<KirmaBukinistkaOfferReceiptResultDto> SaveReceiptForBukinistkaAsync(
        KirmaBukinistkaOfferReceiptRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default )
    {
        if (BukinistkaJwtAuthentication.TryValidateCookie( httpRequest, _config ) is null)
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw new InvalidOperationException( "Дадайце хаця б адну кнігу ў прыёмку." );
        }

        List<int> offerIds = request.Lines.Select( x => x.OfferId ).Distinct().ToList();
        if (offerIds.Count != request.Lines.Count)
        {
            throw new InvalidOperationException( "Прапанова не можа быць дададзена ў прыёмку двойчы." );
        }

        List<KirmaBukinistkaOffer> offers = await _db.KirmaBukinistkaOffers
            .Where( x => offerIds.Contains( x.Id ) )
            .ToListAsync( cancellationToken );

        if (offers.Count != offerIds.Count)
        {
            throw new InvalidOperationException( "Адна або некалькі прапаноў не знойдзены." );
        }

        Dictionary<int, KirmaBukinistkaOffer> byId = offers.ToDictionary( x => x.Id );
        List<OdooStockReceiptService.ReceiptLine> receiptLines = new();
        List<(KirmaBukinistkaOffer Offer, int OdooProductId, int QtyBefore, decimal? AcceptedListPrice)> prepared
            = new();

        foreach (KirmaBukinistkaOfferReceiptLineRequest line in request.Lines)
        {
            if (!byId.TryGetValue( line.OfferId, out KirmaBukinistkaOffer? row ))
            {
                throw new InvalidOperationException( "Прапанова не знойдзена." );
            }

            string status = NormalizeStatus( row.Status );
            if (!string.Equals( status, KirmaBukinistkaOfferStatuses.Pending, StringComparison.OrdinalIgnoreCase ))
            {
                throw new InvalidOperationException(
                    $"Прапанова «{row.ProductName}» ужо апрацаваная." );
            }

            if (line.OdooProductId <= 0)
            {
                throw new InvalidOperationException(
                    $"Для «{row.ProductName}» выберыце прадукт Odoo." );
            }

            OdooProductService.OdooProductSnapshot snapshot =
                await _odooProducts.GetProductSnapshotAsync(
                    httpRequest,
                    line.OdooProductId,
                    cancellationToken );

            int qtyBefore = (int)Math.Round(
                snapshot.QuantityInStock,
                MidpointRounding.AwayFromZero );

            decimal? acceptedListPrice = null;
            if (line.ListPrice.HasValue)
            {
                if (line.ListPrice.Value < 0m)
                {
                    throw new InvalidOperationException( "Цана продажу не можа быць адмоўнай." );
                }

                decimal roundedList = Math.Round(
                    line.ListPrice.Value,
                    2,
                    MidpointRounding.AwayFromZero );
                if (roundedList != Math.Round( snapshot.ListPrice, 2, MidpointRounding.AwayFromZero ))
                {
                    await _odooProducts.UpdateListPriceAsync(
                        httpRequest,
                        line.OdooProductId,
                        roundedList,
                        cancellationToken );
                    acceptedListPrice = roundedList;
                }
            }

            decimal offerCost = Math.Round( row.GrossUnitCost, 2, MidpointRounding.AwayFromZero );
            decimal odooCost = Math.Round( snapshot.StandardPrice, 2, MidpointRounding.AwayFromZero );
            if (offerCost != odooCost && line.ApplyKirmaCostPrice == true)
            {
                await _odooProducts.UpdateStandardPriceAsync(
                    httpRequest,
                    line.OdooProductId,
                    offerCost,
                    cancellationToken );
            }

            receiptLines.Add( new OdooStockReceiptService.ReceiptLine(
                line.OdooProductId,
                snapshot.Name,
                snapshot.UomId,
                row.Quantity ) );

            prepared.Add( (row, line.OdooProductId, qtyBefore, acceptedListPrice) );
        }

        OdooStockReceiptService.ReceiptResult receipt =
            await _odooReceipts.CreateIncomingReceiptAsync(
                httpRequest,
                receiptLines,
                cancellationToken );

        // Own-stock buffers before marking Accepted (so prior Kirma remaining is correct).
        Dictionary<int, int> acceptedQtyAddedThisReceipt = new();
        foreach ((KirmaBukinistkaOffer Offer, int OdooProductId, int QtyBefore, decimal? AcceptedListPrice) item
                 in prepared)
        {
            await AddOwnStockBufferForAcceptAsync(
                item.OdooProductId,
                item.QtyBefore,
                excludeOfferId: item.Offer.Id,
                cancellationToken,
                extraAcceptedQtyByProduct: acceptedQtyAddedThisReceipt );

            acceptedQtyAddedThisReceipt[item.OdooProductId] =
                acceptedQtyAddedThisReceipt.GetValueOrDefault( item.OdooProductId ) + item.Offer.Quantity;

            item.Offer.OdooProductId = item.OdooProductId;
            item.Offer.OdooQuantityBeforeAccept = item.QtyBefore;
            item.Offer.AcceptedListPrice = item.AcceptedListPrice;
            item.Offer.Status = KirmaBukinistkaOfferStatuses.Accepted;
        }

        await _db.SaveChangesAsync( cancellationToken );

        return new KirmaBukinistkaOfferReceiptResultDto
        {
            PickingId = receipt.PickingId,
            PickingName = receipt.PickingName,
            Offers = prepared.Select( x => ToDto( x.Offer ) ).ToList(),
        };
    }

    public async Task DeleteSentAsync( int id )
    {
        RequireKirmaSession();
        KirmaBukinistkaOffer? row = await _db.KirmaBukinistkaOffers
            .FirstOrDefaultAsync( x => x.Id == id );
        if (row is null)
        {
            throw new InvalidOperationException( "Прапанова не знойдзена." );
        }

        string status = NormalizeStatus( row.Status );
        bool canDelete =
            string.Equals( status, KirmaBukinistkaOfferStatuses.Pending, StringComparison.OrdinalIgnoreCase )
            || string.Equals( status, KirmaBukinistkaOfferStatuses.Rejected, StringComparison.OrdinalIgnoreCase );
        if (!canDelete)
        {
            throw new InvalidOperationException(
                "Выдаліць можна толькі непрынятыя або адхіленыя прапановы." );
        }

        _db.KirmaBukinistkaOffers.Remove( row );
        await _db.SaveChangesAsync();
    }

    private async Task AddOwnStockBufferForAcceptAsync(
        int odooProductId,
        int qtyBeforeAccept,
        int excludeOfferId,
        CancellationToken cancellationToken,
        Dictionary<int, int>? extraAcceptedQtyByProduct = null )
    {
        if (odooProductId <= 0 || qtyBeforeAccept < 0)
        {
            return;
        }

        int acceptedQty = await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .Where( x =>
                x.Id != excludeOfferId
                && x.Status == KirmaBukinistkaOfferStatuses.Accepted
                && x.OdooProductId == odooProductId )
            .SumAsync( x => (int?)x.Quantity ?? 0, cancellationToken );

        if (extraAcceptedQtyByProduct is not null
            && extraAcceptedQtyByProduct.TryGetValue( odooProductId, out int extra ))
        {
            acceptedQty += extra;
        }

        int soldKirma = await _db.KirmaBukinistkaPosSales
            .AsNoTracking()
            .Where( x => x.OdooProductId == odooProductId && !x.IsOwnStock )
            .SumAsync( x => (int?)x.Quantity ?? 0, cancellationToken );

        int kirimaRemaining = Math.Max( 0, acceptedQty - soldKirma );
        int ownAdd = Math.Max( 0, qtyBeforeAccept - kirimaRemaining );
        if (ownAdd <= 0)
        {
            return;
        }

        KirmaBukinistkaOdooOwnStockBuffer? buffer = await _db.KirmaBukinistkaOdooOwnStockBuffers
            .FirstOrDefaultAsync( x => x.OdooProductId == odooProductId, cancellationToken );
        if (buffer is null)
        {
            buffer = new KirmaBukinistkaOdooOwnStockBuffer
            {
                OdooProductId = odooProductId,
                OwnQtyRemaining = 0,
            };
            _db.KirmaBukinistkaOdooOwnStockBuffers.Add( buffer );
        }

        buffer.OwnQtyRemaining += ownAdd;
        buffer.UpdatedAtUtc = DateTime.UtcNow;
    }

    private void RequireKirmaSession()
    {
        if (!ShopifySessionReader.TryGet( _http, out _ ))
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Kirma." );
        }
    }

    private async Task<KirmaBukinistkaOffer> RequirePendingOfferAsync( int id )
    {
        KirmaBukinistkaOffer? row = await _db.KirmaBukinistkaOffers
            .FirstOrDefaultAsync( x => x.Id == id );
        if (row is null)
        {
            throw new InvalidOperationException( "Прапанова не знойдзена." );
        }

        string status = NormalizeStatus( row.Status );
        if (!string.Equals( status, KirmaBukinistkaOfferStatuses.Pending, StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException(
                "Можна змяняць або адмяняць толькі прапановы, якія яшчэ не прынятыя і не адхіленыя." );
        }

        return row;
    }

    private static string NormalizeStatus( string? status ) =>
        string.IsNullOrWhiteSpace( status )
            ? KirmaBukinistkaOfferStatuses.Pending
            : status.Trim();

    private async Task<string?> TryBuildStorefrontUrlAsync( ShopifySession session, string productId )
    {
        try
        {
            if (!long.TryParse( productId, out long numericId ))
            {
                return null;
            }

            HttpClient client = _httpClientFactory.CreateClient();
            using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
                client,
                session.AccessToken,
                HttpMethod.Get,
                ShopifyApi.RestUrl( session.Shop, $"products/{numericId}.json?fields=id,handle" )
            );
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using JsonDocument json = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
            if (!json.RootElement.TryGetProperty( "product", out JsonElement product )
                || !product.TryGetProperty( "handle", out JsonElement handleEl )
                || handleEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? handle = handleEl.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace( handle ))
            {
                return null;
            }

            string host = await TryGetStorefrontHostAsync( session ) ?? "kirma.sh";
            return $"https://{host.Trim().TrimEnd( '/' )}/products/{handle}";
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TryGetStorefrontHostAsync( ShopifySession session )
    {
        try
        {
            HttpClient client = _httpClientFactory.CreateClient();
            using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
                client,
                session.AccessToken,
                HttpMethod.Get,
                ShopifyApi.RestUrl( session.Shop, "shop.json?fields=domain,myshopify_domain" )
            );
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using JsonDocument json = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
            if (!json.RootElement.TryGetProperty( "shop", out JsonElement shop ))
            {
                return null;
            }

            if (shop.TryGetProperty( "domain", out JsonElement domainEl )
                && domainEl.ValueKind == JsonValueKind.String)
            {
                string? domain = domainEl.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace( domain ))
                {
                    return domain;
                }
            }

            if (shop.TryGetProperty( "myshopify_domain", out JsonElement myshopifyEl )
                && myshopifyEl.ValueKind == JsonValueKind.String)
            {
                string? myshopify = myshopifyEl.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace( myshopify ))
                {
                    return myshopify;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static KirmaBukinistkaOfferDto ToDto( KirmaBukinistkaOffer row ) => new()
    {
        Id = row.Id,
        ShopifyProductId = row.ShopifyProductId,
        ShopifyVariantId = row.ShopifyVariantId,
        ProductName = row.ProductName,
        ProductAuthor = row.ProductAuthor,
        MainImageUrl = row.MainImageUrl,
        ProductAdminUrl = row.ProductAdminUrl,
        StorefrontUrl = row.StorefrontUrl,
        SupplierName = row.SupplierName,
        Quantity = row.Quantity,
        GrossUnitCost = row.GrossUnitCost,
        Status = string.IsNullOrWhiteSpace( row.Status )
            ? KirmaBukinistkaOfferStatuses.Pending
            : row.Status,
        OdooProductId = row.OdooProductId,
        OdooQuantityBeforeAccept = row.OdooQuantityBeforeAccept,
        AcceptedListPrice = row.AcceptedListPrice,
        CreatedAtUtc = row.CreatedAtUtc,
    };
}
