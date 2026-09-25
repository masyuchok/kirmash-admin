using System.Security.Claims;
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
    private readonly ShopifyInventoryService _shopifyInventory;

    public KirmaBukinistkaOfferService(
        AppDbContext db,
        IHttpContextAccessor http,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        OdooProductService odooProducts,
        OdooStockReceiptService odooReceipts,
        ShopifyInventoryService shopifyInventory )
    {
        _db = db;
        _http = http;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _odooProducts = odooProducts;
        _odooReceipts = odooReceipts;
        _shopifyInventory = shopifyInventory;
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
            Direction = KirmaBukinistkaOfferDirections.KirmaToBukinistka,
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
            SyncOnSale = request.SyncOnSale,
            CreatedByLogin = createdBy,
            Status = KirmaBukinistkaOfferStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        };

        _db.KirmaBukinistkaOffers.Add( row );
        await _db.SaveChangesAsync();
        return await ToDtoAsync( row );
    }

    public async Task<List<KirmaBukinistkaOfferDto>> ListForBukinistkaAsync( HttpRequest request )
    {
        if (BukinistkaJwtAuthentication.TryValidateCookie( request, _config ) is null)
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        // Pending Kirma→Buk offers, plus accepted ones with a pending peer price change.
        List<KirmaBukinistkaOffer> rows = await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .Where( x =>
                (string.IsNullOrWhiteSpace( x.Direction )
                    || x.Direction == KirmaBukinistkaOfferDirections.KirmaToBukinistka) &&
                (
                    string.IsNullOrWhiteSpace( x.Status )
                    || x.Status == KirmaBukinistkaOfferStatuses.Pending
                    || (x.Status == KirmaBukinistkaOfferStatuses.Accepted && x.PeerPriceChangePending)
                ) )
            .OrderByDescending( x => x.PeerPriceChangePending )
            .ThenByDescending( x => x.CreatedAtUtc )
            .ThenByDescending( x => x.Id )
            .Take( 1000 )
            .ToListAsync();

        return await MapDtosAsync( rows );
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
                (string.IsNullOrWhiteSpace( x.Direction )
                    || x.Direction == KirmaBukinistkaOfferDirections.KirmaToBukinistka) &&
                (
                    string.IsNullOrWhiteSpace( x.Status )
                    || x.Status == KirmaBukinistkaOfferStatuses.Pending
                    || (x.Status == KirmaBukinistkaOfferStatuses.Accepted && x.PeerPriceChangePending)
                ) );
    }

    public async Task<List<KirmaBukinistkaOfferDto>> ListSentForKirmaAsync()
    {
        if (!ShopifySessionReader.TryGet( _http, out _ ))
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Kirma." );
        }

        List<KirmaBukinistkaOffer> rows = await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .Where( x =>
                string.IsNullOrWhiteSpace( x.Direction )
                || x.Direction == KirmaBukinistkaOfferDirections.KirmaToBukinistka )
            .OrderByDescending( x => x.CreatedAtUtc )
            .ThenByDescending( x => x.Id )
            .Take( 1000 )
            .ToListAsync();

        return await MapDtosAsync( rows );
    }

    public async Task<KirmaBukinistkaOfferDto> CreateFromBukinistkaAsync(
        KirmaBukinistkaOfferCreateFromBukinistkaRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default )
    {
        ClaimsPrincipal? principal = BukinistkaJwtAuthentication.TryValidateCookie( httpRequest, _config );
        if (principal is null)
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        if (request.OdooProductId <= 0)
        {
            throw new InvalidOperationException( "Выберыце прадукт Odoo." );
        }

        if (request.Quantity <= 0)
        {
            throw new InvalidOperationException( "Колькасць павінна быць больш за нуль." );
        }

        if (request.GrossUnitCost < 0m)
        {
            throw new InvalidOperationException( "Кошт брута не можа быць адмоўным." );
        }

        OdooProductService.OdooProductSnapshot snapshot = await _odooProducts.GetProductSnapshotAsync(
            httpRequest,
            request.OdooProductId,
            cancellationToken );

        OdooProductService.OdooProductShopifySourceDetails odooDetails =
            await _odooProducts.GetProductShopifySourceDetailsAsync(
                httpRequest,
                request.OdooProductId,
                cancellationToken );

        (string shopifyProductId, string shopifyVariantId) = await TryResolveShopifyIdsForOdooProductAsync(
            request.OdooProductId,
            cancellationToken );

        string createdBy = principal.FindFirst( "sub" )?.Value
            ?? principal.Identity?.Name
            ?? "bukinistka";

        string odooBase = (_config["Odoo:BaseUrl"] ?? string.Empty).Trim().TrimEnd( '/' );
        string odooUrl = odooBase.Length > 0
            ? $"{odooBase}/web#id={request.OdooProductId}&model=product.product&view_type=form"
            : string.Empty;

        KirmaBukinistkaOffer row = new()
        {
            Direction = KirmaBukinistkaOfferDirections.BukinistkaToKirma,
            OdooProductId = request.OdooProductId,
            ShopifyProductId = shopifyProductId,
            ShopifyVariantId = shopifyVariantId,
            ProductName = (snapshot.Name ?? string.Empty).Trim(),
            ProductAuthor = (odooDetails.Author ?? string.Empty).Trim(),
            MainImageUrl = null,
            ProductAdminUrl = odooUrl,
            StorefrontUrl = string.Empty,
            SupplierName = string.IsNullOrWhiteSpace( odooDetails.PublisherName )
                ? null
                : odooDetails.PublisherName.Trim(),
            Quantity = request.Quantity,
            GrossUnitCost = Math.Round( request.GrossUnitCost, 2, MidpointRounding.AwayFromZero ),
            SyncOnSale = request.SyncOnSale,
            IsAssignment = request.IsAssignment,
            CreatedByLogin = createdBy,
            Status = KirmaBukinistkaOfferStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        };

        if (string.IsNullOrWhiteSpace( row.ProductName ))
        {
            row.ProductName = $"Odoo #{request.OdooProductId}";
        }

        _db.KirmaBukinistkaOffers.Add( row );
        await _db.SaveChangesAsync( cancellationToken );
        return await ToDtoAsync( row, cancellationToken );
    }

    public async Task<List<KirmaBukinistkaOfferDto>> ListReceivedForKirmaAsync()
    {
        if (!ShopifySessionReader.TryGet( _http, out _ ))
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Kirma." );
        }

        List<KirmaBukinistkaOffer> rows = await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .Where( x => x.Direction == KirmaBukinistkaOfferDirections.BukinistkaToKirma )
            .OrderByDescending( x => x.PeerPriceChangePending )
            .ThenByDescending( x => x.CreatedAtUtc )
            .ThenByDescending( x => x.Id )
            .Take( 1000 )
            .ToListAsync();

        return await EnrichBukinistkaSalePricesFromOdooAsync(
            await EnrichProductAuthorsFromOdooAsync(
                await EnrichPublisherNamesFromOdooAsync(
                    await EnrichShopifySalePricesAsync( await MapDtosAsync( rows ) ) ) ) );
    }

    public async Task<int> CountPendingForKirmaAsync()
    {
        if (!ShopifySessionReader.TryGet( _http, out _ ))
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Kirma." );
        }

        return await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .CountAsync( x =>
                x.Direction == KirmaBukinistkaOfferDirections.BukinistkaToKirma &&
                (
                    string.IsNullOrWhiteSpace( x.Status )
                    || x.Status == KirmaBukinistkaOfferStatuses.Pending
                    || (x.Status == KirmaBukinistkaOfferStatuses.Accepted && x.PeerPriceChangePending)
                ) );
    }

    public async Task<List<KirmaBukinistkaOfferDto>> ListSentByBukinistkaAsync( HttpRequest request )
    {
        if (BukinistkaJwtAuthentication.TryValidateCookie( request, _config ) is null)
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        List<KirmaBukinistkaOffer> rows = await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .Where( x => x.Direction == KirmaBukinistkaOfferDirections.BukinistkaToKirma )
            .OrderByDescending( x => x.CreatedAtUtc )
            .ThenByDescending( x => x.Id )
            .Take( 1000 )
            .ToListAsync();

        return await MapDtosAsync( rows );
    }

    public async Task RejectForKirmaAsync( int id )
    {
        RequireKirmaSession();
        KirmaBukinistkaOffer? row = await _db.KirmaBukinistkaOffers
            .FirstOrDefaultAsync( x => x.Id == id );
        if (row is null)
        {
            throw new InvalidOperationException( "Прапанова не знойдзена." );
        }

        if (!string.Equals(
                NormalizeDirection( row.Direction ),
                KirmaBukinistkaOfferDirections.BukinistkaToKirma,
                StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Гэтая прапанова не ад Букіністкі." );
        }

        string status = NormalizeStatus( row.Status );
        if (!string.Equals( status, KirmaBukinistkaOfferStatuses.Pending, StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Адхіліць можна толькі новыя (неразобраныя) прапановы." );
        }

        row.Status = KirmaBukinistkaOfferStatuses.Rejected;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Kirma closes an already accepted Buk→Kirma offer whose Shopify product/variant
    /// no longer exists (deleted card). No inventory is touched — there is nothing
    /// left in Shopify to adjust. Refuses when the Shopify product is still alive,
    /// because then stock would have to be reversed manually first.
    /// </summary>
    public async Task CloseOrphanedReceivedAsync( int id, CancellationToken cancellationToken = default )
    {
        RequireKirmaSession();
        KirmaBukinistkaOffer? row = await _db.KirmaBukinistkaOffers
            .FirstOrDefaultAsync( x => x.Id == id, cancellationToken );
        if (row is null)
        {
            throw new InvalidOperationException( "Прапанова не знойдзена." );
        }

        if (!string.Equals(
                NormalizeDirection( row.Direction ),
                KirmaBukinistkaOfferDirections.BukinistkaToKirma,
                StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Гэтая прапанова не ад Букіністкі." );
        }

        string status = NormalizeStatus( row.Status );
        if (!string.Equals( status, KirmaBukinistkaOfferStatuses.Accepted, StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Закрыць можна толькі прынятую прапанову." );
        }

        string shop = (_config["Shopify:Shop"] ?? string.Empty).Trim();
        string accessToken = (_config["Shopify:AccessToken"] ?? string.Empty).Trim();
        if (ShopifySessionReader.TryGet( _http, out ShopifySession session ))
        {
            if (!string.IsNullOrWhiteSpace( session.Shop )) shop = session.Shop.Trim();
            if (!string.IsNullOrWhiteSpace( session.AccessToken )) accessToken = session.AccessToken.Trim();
        }

        if (string.IsNullOrWhiteSpace( shop ) || string.IsNullOrWhiteSpace( accessToken ))
        {
            throw new InvalidOperationException( "Shopify Shop/AccessToken не наладжаныя." );
        }

        if (!string.IsNullOrWhiteSpace( row.ShopifyProductId ))
        {
            string product = ShopifyIds.NormalizeProductId( row.ShopifyProductId.Trim() );
            string variant = string.IsNullOrWhiteSpace( row.ShopifyVariantId )
                ? string.Empty
                : ShopifyIds.NormalizeVariantId( row.ShopifyVariantId.Trim() );
            string key = string.IsNullOrWhiteSpace( variant ) ? product : $"{product}::{variant}";

            bool alive = await _shopifyInventory.ProductKeyExistsAsync( shop, accessToken, key );
            if (alive)
            {
                throw new InvalidOperationException(
                    "Shopify-картка гэтай прапановы яшчэ існуе. Закрыць можна толькі прапанову, " +
                    "чыя картка Shopify выдаленая." );
            }
        }

        bool hasSales = await _db.KirmaBukinistkaPosSales
            .AsNoTracking()
            .AnyAsync( x => x.OfferId == row.Id && !x.IsReversed && !x.IsReturn && x.Quantity > 0, cancellationToken );
        if (hasSales)
        {
            throw new InvalidOperationException(
                "Па гэтай прапанове ўжо ёсць запісаныя продажы. Закрыць нельга." );
        }

        row.Status = KirmaBukinistkaOfferStatuses.Rejected;
        await _db.SaveChangesAsync( cancellationToken );
    }

    public async Task CancelSentByBukinistkaAsync( int id, HttpRequest request )
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

        if (!string.Equals(
                NormalizeDirection( row.Direction ),
                KirmaBukinistkaOfferDirections.BukinistkaToKirma,
                StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Гэтая прапанова не ад Букіністкі." );
        }

        string status = NormalizeStatus( row.Status );
        if (string.Equals( status, KirmaBukinistkaOfferStatuses.Accepted, StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Нельга скасаваць ужо прынятую прапанову." );
        }

        _db.KirmaBukinistkaOffers.Remove( row );
        await _db.SaveChangesAsync();
    }

    public async Task<KirmaBukinistkaOfferDto> UpdateSentByBukinistkaAsync(
        int id,
        KirmaBukinistkaOfferUpdateRequest request,
        HttpRequest httpRequest )
    {
        if (BukinistkaJwtAuthentication.TryValidateCookie( httpRequest, _config ) is null)
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        KirmaBukinistkaOffer? row = await _db.KirmaBukinistkaOffers
            .FirstOrDefaultAsync( x => x.Id == id );
        if (row is null)
        {
            throw new InvalidOperationException( "Прапанова не знойдзена." );
        }

        if (!string.Equals(
                NormalizeDirection( row.Direction ),
                KirmaBukinistkaOfferDirections.BukinistkaToKirma,
                StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Гэтая прапанова не ад Букіністкі." );
        }

        await ApplySentOfferUpdateAsync( row, request );
        return await ToDtoAsync( row );
    }

    public async Task<KirmaBukinistkaOfferDto> ApplyPriceChangeByKirmaAsync(
        int id,
        CancellationToken cancellationToken = default )
    {
        RequireKirmaSession();
        if (!ShopifySessionReader.TryGet( _http, out ShopifySession session ))
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Kirma." );
        }

        KirmaBukinistkaOffer? row = await _db.KirmaBukinistkaOffers
            .FirstOrDefaultAsync( x => x.Id == id, cancellationToken );
        if (row is null)
        {
            throw new InvalidOperationException( "Прапанова не знойдзена." );
        }

        if (!string.Equals(
                NormalizeDirection( row.Direction ),
                KirmaBukinistkaOfferDirections.BukinistkaToKirma,
                StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Гэтая прапанова не ад Букіністкі." );
        }

        string status = NormalizeStatus( row.Status );
        if (!string.Equals( status, KirmaBukinistkaOfferStatuses.Accepted, StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Абнавіць кошт у Shopify можна толькі для прынятых прапаноў." );
        }

        if (!row.PeerPriceChangePending)
        {
            throw new InvalidOperationException( "Няма няпрымененых змен кошту для гэтай прапановы." );
        }

        if (string.IsNullOrWhiteSpace( row.ShopifyProductId ))
        {
            throw new InvalidOperationException( "У прапанове няма звязанага прадукта Shopify." );
        }

        string shop = string.IsNullOrWhiteSpace( session.Shop )
            ? (_config["Shopify:Shop"] ?? string.Empty).Trim()
            : session.Shop.Trim();
        string accessToken = string.IsNullOrWhiteSpace( session.AccessToken )
            ? (_config["Shopify:AccessToken"] ?? string.Empty).Trim()
            : session.AccessToken.Trim();
        if (string.IsNullOrWhiteSpace( shop ) || string.IsNullOrWhiteSpace( accessToken ))
        {
            throw new InvalidOperationException( "Shopify Shop/AccessToken не наладжаныя." );
        }

        await _shopifyInventory.SetVariantCostByProductKeyAsync(
            shop,
            accessToken,
            row.ShopifyProductId,
            string.IsNullOrWhiteSpace( row.ShopifyVariantId ) ? null : row.ShopifyVariantId,
            row.GrossUnitCost );

        row.PeerPriceChangePending = false;
        await _db.SaveChangesAsync( cancellationToken );
        return await ToDtoAsync( row, cancellationToken );
    }

    public async Task<KirmaBukinistkaOfferDto> UpdateShopifySalePriceByKirmaAsync(
        int id,
        KirmaBukinistkaOfferShopifySalePriceRequest request,
        CancellationToken cancellationToken = default )
    {
        RequireKirmaSession();
        if (!ShopifySessionReader.TryGet( _http, out ShopifySession session ))
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Kirma." );
        }

        if (request.SalePrice < 0m)
        {
            throw new InvalidOperationException( "Цана продажу не можа быць адмоўнай." );
        }

        KirmaBukinistkaOffer? row = await _db.KirmaBukinistkaOffers
            .FirstOrDefaultAsync( x => x.Id == id, cancellationToken );
        if (row is null)
        {
            throw new InvalidOperationException( "Прапанова не знойдзена." );
        }

        if (!string.Equals(
                NormalizeDirection( row.Direction ),
                KirmaBukinistkaOfferDirections.BukinistkaToKirma,
                StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Гэтая прапанова не ад Букіністкі." );
        }

        string status = NormalizeStatus( row.Status );
        if (!string.Equals( status, KirmaBukinistkaOfferStatuses.Accepted, StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException(
                "Цану ў Shopify можна змяняць толькі для прынятых прапаноў." );
        }

        if (string.IsNullOrWhiteSpace( row.ShopifyProductId ))
        {
            throw new InvalidOperationException( "У прапанове няма звязанага прадукта Shopify." );
        }

        string shop = string.IsNullOrWhiteSpace( session.Shop )
            ? (_config["Shopify:Shop"] ?? string.Empty).Trim()
            : session.Shop.Trim();
        string accessToken = string.IsNullOrWhiteSpace( session.AccessToken )
            ? (_config["Shopify:AccessToken"] ?? string.Empty).Trim()
            : session.AccessToken.Trim();
        if (string.IsNullOrWhiteSpace( shop ) || string.IsNullOrWhiteSpace( accessToken ))
        {
            throw new InvalidOperationException( "Shopify Shop/AccessToken не наладжаныя." );
        }

        decimal salePrice = Math.Round(
            request.SalePrice,
            2,
            MidpointRounding.AwayFromZero );
        await _shopifyInventory.SetVariantPriceAsync(
            shop,
            accessToken,
            row.ShopifyProductId,
            string.IsNullOrWhiteSpace( row.ShopifyVariantId ) ? null : row.ShopifyVariantId,
            salePrice );

        return await ToDtoAsync( row, cancellationToken );
    }

    public async Task<KirmaBukinistkaOfferDto> ApplyPriceChangeByBukinistkaAsync(
        int id,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default )
    {
        if (BukinistkaJwtAuthentication.TryValidateCookie( httpRequest, _config ) is null)
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        KirmaBukinistkaOffer? row = await _db.KirmaBukinistkaOffers
            .FirstOrDefaultAsync( x => x.Id == id, cancellationToken );
        if (row is null)
        {
            throw new InvalidOperationException( "Прапанова не знойдзена." );
        }

        if (!string.Equals(
                NormalizeDirection( row.Direction ),
                KirmaBukinistkaOfferDirections.KirmaToBukinistka,
                StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Гэтая прапанова не ад Кірмаша." );
        }

        string status = NormalizeStatus( row.Status );
        if (!string.Equals( status, KirmaBukinistkaOfferStatuses.Accepted, StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Абнавіць кошт у Odoo можна толькі для прынятых прапаноў." );
        }

        if (!row.PeerPriceChangePending)
        {
            throw new InvalidOperationException( "Няма няпрымененых змен кошту для гэтай прапановы." );
        }

        if (row.OdooProductId is null or <= 0)
        {
            throw new InvalidOperationException( "У прапанове няма звязанага прадукта Odoo." );
        }

        await _odooProducts.UpdateStandardPriceAsync(
            httpRequest,
            row.OdooProductId.Value,
            row.GrossUnitCost,
            cancellationToken );

        row.PeerPriceChangePending = false;
        await _db.SaveChangesAsync( cancellationToken );
        return await ToDtoAsync( row, cancellationToken );
    }

    /// <summary>
    /// When Shopify sells units that are still in a Pending Kirma→Buk offer, decrease offer qty
    /// (delete at 0). Idempotent per Shopify order + offer.
    /// Does not call SaveChanges — caller must persist.
    /// </summary>
    public async Task<int> ShrinkPendingKirmaToBukForShopifySaleAsync(
        string shopifyOrderId,
        IReadOnlyList<(string ProductId, string VariantId, int Quantity)> lines,
        DateTime utcNow,
        CancellationToken cancellationToken = default )
    {
        string orderId = (shopifyOrderId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( orderId ) || lines.Count == 0)
        {
            return 0;
        }

        List<(string ProductId, string VariantId, int Quantity)> normalized = lines
            .Select( line => (
                ProductId: ShopifyIds.NormalizeProductId( (line.ProductId ?? string.Empty).Trim() ),
                VariantId: string.IsNullOrWhiteSpace( line.VariantId )
                    ? string.Empty
                    : ShopifyIds.NormalizeVariantId( line.VariantId.Trim() ),
                Quantity: line.Quantity ))
            .Where( line => !string.IsNullOrWhiteSpace( line.ProductId ) && line.Quantity > 0 )
            .ToList();
        if (normalized.Count == 0)
        {
            return 0;
        }

        HashSet<string> productIds = normalized
            .Select( x => x.ProductId )
            .ToHashSet( StringComparer.OrdinalIgnoreCase );

        List<KirmaBukinistkaOffer> pending = await _db.KirmaBukinistkaOffers
            .Where( x =>
                x.Status == KirmaBukinistkaOfferStatuses.Pending
                && x.Quantity > 0
                && (string.IsNullOrWhiteSpace( x.Direction )
                    || x.Direction == KirmaBukinistkaOfferDirections.KirmaToBukinistka)
                && productIds.Contains( x.ShopifyProductId ) )
            .OrderBy( x => x.CreatedAtUtc )
            .ThenBy( x => x.Id )
            .ToListAsync( cancellationToken );
        if (pending.Count == 0)
        {
            return 0;
        }

        HashSet<int> pendingIds = pending.Select( x => x.Id ).ToHashSet();
        Dictionary<int, int> alreadyByOffer = await _db.KirmaBukinistkaPendingOfferSaleDeductions
            .AsNoTracking()
            .Where( x =>
                x.Source == KirmaBukinistkaPendingOfferSaleDeduction.SourceShopifyOrder
                && x.SourceKey == orderId
                && pendingIds.Contains( x.OfferId ) )
            .ToDictionaryAsync( x => x.OfferId, x => x.Quantity, cancellationToken );

        int unitsShrunk = 0;
        List<KirmaBukinistkaOffer> toRemove = new();

        foreach ((string productId, string variantId, int soldQty) in normalized)
        {
            int toAllocate = soldQty;
            foreach (KirmaBukinistkaOffer offer in pending)
            {
                if (toAllocate <= 0)
                {
                    break;
                }

                if (!MatchesShopifyLine( offer, productId, variantId ))
                {
                    continue;
                }

                if (alreadyByOffer.TryGetValue( offer.Id, out int alreadyTaken ))
                {
                    // Credit prior deductions for this order once per offer.
                    if (toAllocate > 0)
                    {
                        int credit = Math.Min( toAllocate, alreadyTaken );
                        toAllocate -= credit;
                    }

                    continue;
                }

                if (offer.Quantity <= 0)
                {
                    continue;
                }

                int take = Math.Min( toAllocate, offer.Quantity );
                if (take <= 0)
                {
                    continue;
                }

                offer.Quantity -= take;
                toAllocate -= take;
                unitsShrunk += take;
                alreadyByOffer[offer.Id] = take;
                _db.KirmaBukinistkaPendingOfferSaleDeductions.Add(
                    new KirmaBukinistkaPendingOfferSaleDeduction
                    {
                        OfferId = offer.Id,
                        Source = KirmaBukinistkaPendingOfferSaleDeduction.SourceShopifyOrder,
                        SourceKey = orderId,
                        Quantity = take,
                        CreatedAtUtc = utcNow,
                    } );

                if (offer.Quantity <= 0)
                {
                    toRemove.Add( offer );
                }
            }
        }

        if (toRemove.Count > 0)
        {
            _db.KirmaBukinistkaOffers.RemoveRange( toRemove );
        }

        return unitsShrunk;
    }

    /// <summary>
    /// When Odoo POS sells units that are still in a Pending Buk→Kirma offer, decrease offer qty
    /// (delete at 0). Idempotent per POS line + offer. Does not call SaveChanges.
    /// </summary>
    public async Task<int> ShrinkPendingBukToKirmaForPosSaleAsync(
        int odooPosOrderLineId,
        int odooProductId,
        int quantity,
        DateTime utcNow,
        CancellationToken cancellationToken = default )
    {
        if (odooPosOrderLineId <= 0 || odooProductId <= 0 || quantity <= 0)
        {
            return 0;
        }

        string sourceKey = odooPosOrderLineId.ToString();

        List<KirmaBukinistkaOffer> pending = await _db.KirmaBukinistkaOffers
            .Where( x =>
                x.Status == KirmaBukinistkaOfferStatuses.Pending
                && x.Quantity > 0
                && x.Direction == KirmaBukinistkaOfferDirections.BukinistkaToKirma
                && x.OdooProductId == odooProductId )
            .OrderBy( x => x.CreatedAtUtc )
            .ThenBy( x => x.Id )
            .ToListAsync( cancellationToken );
        if (pending.Count == 0)
        {
            return 0;
        }

        HashSet<int> pendingIds = pending.Select( x => x.Id ).ToHashSet();
        Dictionary<int, int> alreadyByOffer = await _db.KirmaBukinistkaPendingOfferSaleDeductions
            .AsNoTracking()
            .Where( x =>
                x.Source == KirmaBukinistkaPendingOfferSaleDeduction.SourceOdooPosLine
                && x.SourceKey == sourceKey
                && pendingIds.Contains( x.OfferId ) )
            .ToDictionaryAsync( x => x.OfferId, x => x.Quantity, cancellationToken );

        int toAllocate = quantity;
        int unitsShrunk = 0;
        List<KirmaBukinistkaOffer> toRemove = new();

        foreach (KirmaBukinistkaOffer offer in pending)
        {
            if (toAllocate <= 0)
            {
                break;
            }

            if (alreadyByOffer.TryGetValue( offer.Id, out int alreadyTaken ))
            {
                toAllocate -= alreadyTaken;
                continue;
            }

            int take = Math.Min( toAllocate, offer.Quantity );
            if (take <= 0)
            {
                continue;
            }

            offer.Quantity -= take;
            toAllocate -= take;
            unitsShrunk += take;
            _db.KirmaBukinistkaPendingOfferSaleDeductions.Add(
                new KirmaBukinistkaPendingOfferSaleDeduction
                {
                    OfferId = offer.Id,
                    Source = KirmaBukinistkaPendingOfferSaleDeduction.SourceOdooPosLine,
                    SourceKey = sourceKey,
                    Quantity = take,
                    CreatedAtUtc = utcNow,
                } );

            if (offer.Quantity <= 0)
            {
                toRemove.Add( offer );
            }
        }

        if (toRemove.Count > 0)
        {
            _db.KirmaBukinistkaOffers.RemoveRange( toRemove );
        }

        return unitsShrunk;
    }

    /// <summary>True when any Pending Kirma→Buk offer still has quantity.</summary>
    public Task<bool> HasPendingKirmaToBukOffersAsync(
        CancellationToken cancellationToken = default ) =>
        _db.KirmaBukinistkaOffers.AnyAsync(
            x =>
                x.Status == KirmaBukinistkaOfferStatuses.Pending
                && x.Quantity > 0
                && (string.IsNullOrWhiteSpace( x.Direction )
                    || x.Direction == KirmaBukinistkaOfferDirections.KirmaToBukinistka),
            cancellationToken );

    public async Task<KirmaBukinistkaOfferAcceptByKirmaResultDto> AcceptForKirmaAsync(
        int id,
        KirmaBukinistkaOfferAcceptByKirmaRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default )
    {
        if (!ShopifySessionReader.TryGet( _http, out ShopifySession session ))
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Kirma." );
        }

        string productId = ShopifyIds.NormalizeProductId( (request.ShopifyProductId ?? string.Empty).Trim() );
        if (string.IsNullOrWhiteSpace( productId ))
        {
            throw new InvalidOperationException( "Выберыце прадукт Shopify для звязкі." );
        }

        string variantId = string.IsNullOrWhiteSpace( request.ShopifyVariantId )
            ? string.Empty
            : ShopifyIds.NormalizeVariantId( request.ShopifyVariantId.Trim() );

        KirmaBukinistkaOffer? row = await _db.KirmaBukinistkaOffers
            .FirstOrDefaultAsync( x => x.Id == id, cancellationToken );
        if (row is null)
        {
            throw new InvalidOperationException( "Прапанова не знойдзена." );
        }

        if (!string.Equals(
                NormalizeDirection( row.Direction ),
                KirmaBukinistkaOfferDirections.BukinistkaToKirma,
                StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Гэтая прапанова не ад Букіністкі." );
        }

        string status = NormalizeStatus( row.Status );
        if (!string.Equals( status, KirmaBukinistkaOfferStatuses.Pending, StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Прыняць можна толькі новыя (неразобраныя) прапановы." );
        }

        if (row.OdooProductId is null || row.OdooProductId <= 0)
        {
            throw new InvalidOperationException( "У прапанове няма Odoo прадукту." );
        }

        // Prefer the logged-in OAuth session token (same as «OK» price cell).
        // Config SHOPIFY_ACCESS_TOKEN may lack write_products.
        string shop = string.IsNullOrWhiteSpace( session.Shop )
            ? (_config["Shopify:Shop"] ?? string.Empty).Trim()
            : session.Shop.Trim();
        string accessToken = string.IsNullOrWhiteSpace( session.AccessToken )
            ? (_config["Shopify:AccessToken"] ?? string.Empty).Trim()
            : session.AccessToken.Trim();
        if (string.IsNullOrWhiteSpace( shop ) || string.IsNullOrWhiteSpace( accessToken ))
        {
            throw new InvalidOperationException( "Shopify Shop/AccessToken не наладжаныя." );
        }

        int odooProductId = row.OdooProductId.Value;

        if (row.IsAssignment)
        {
            // Assignment → like Kirma consignment at Buk: set Odoo owner, keep Odoo qty,
            // +Shopify so POS can −Shopify, own-buffer for remaining Buk stock.
            OdooProductService.OdooProductSnapshot snapshot =
                await _odooProducts.GetProductSnapshotAsync(
                    httpRequest,
                    odooProductId,
                    cancellationToken );
            int qtyBefore = (int)Math.Floor( snapshot.QuantityInStock );
            int bufferBefore = Math.Max( 0, qtyBefore - row.Quantity );

            await _odooProducts.SetKirmaOwnerCompanyAsync(
                httpRequest,
                odooProductId,
                cancellationToken );

            await _shopifyInventory.ApplyInventoryDeltaByProductKeyAsync(
                shop,
                accessToken,
                productId,
                row.Quantity );

            await AddOwnStockBufferForAcceptAsync(
                odooProductId,
                bufferBefore,
                excludeOfferId: row.Id,
                cancellationToken );

            row.OdooQuantityBeforeAccept = qtyBefore;
        }
        else
        {
            await _shopifyInventory.ApplyInventoryDeltaByProductKeyAsync(
                shop,
                accessToken,
                productId,
                row.Quantity );
        }

        List<string> syncWarnings = new();

        if (request.SalePrice.HasValue)
        {
            if (request.SalePrice.Value < 0m)
            {
                throw new InvalidOperationException( "Цана продажу не можа быць адмоўнай." );
            }

            decimal salePrice = Math.Round(
                request.SalePrice.Value,
                2,
                MidpointRounding.AwayFromZero );
            try
            {
                await _shopifyInventory.SetVariantPriceAsync(
                    shop,
                    accessToken,
                    productId,
                    variantId,
                    salePrice );
            }
            catch (Exception ex)
            {
                syncWarnings.Add( FormatShopifyPriceSyncWarning( ex ) );
            }
        }

        decimal offerCost = Math.Round( row.GrossUnitCost, 2, MidpointRounding.AwayFromZero );
        if (offerCost >= 0m)
        {
            try
            {
                await _shopifyInventory.SetVariantCostByProductKeyAsync(
                    shop,
                    accessToken,
                    productId,
                    string.IsNullOrWhiteSpace( variantId ) ? null : variantId,
                    offerCost );
            }
            catch (Exception ex)
            {
                syncWarnings.Add( FormatShopifyCostSyncWarning( ex ) );
            }
        }

        string storeSlug = shop.Replace( ".myshopify.com", "", StringComparison.OrdinalIgnoreCase );
        row.ShopifyProductId = productId;
        row.ShopifyVariantId = variantId;
        row.ProductAdminUrl = $"https://admin.shopify.com/store/{storeSlug}/products/{productId}";
        row.StorefrontUrl = await TryBuildStorefrontUrlAsync( session, productId ) ?? row.StorefrontUrl;
        row.Status = KirmaBukinistkaOfferStatuses.Accepted;
        row.AcceptedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync( cancellationToken );

        ProductLedgerService.InvalidateSoldByLineCache();
        return new KirmaBukinistkaOfferAcceptByKirmaResultDto
        {
            Offer = await ToDtoAsync( row, cancellationToken ),
            ShopifySyncWarning = syncWarnings.Count > 0
                ? string.Join( " ", syncWarnings )
                : null,
        };
    }

    private static string FormatShopifyPriceSyncWarning( Exception ex )
    {
        string message = ex.Message;
        if (message.Contains( "write_products", StringComparison.OrdinalIgnoreCase ))
        {
            return "Прапанова прынята, але цану ў Shopify не ўдалося абнавіць: патрэбны дазвол write_products (пераўсталюйце дадатак Kirma ў Shopify).";
        }

        return $"Прапанова прынята, але цану ў Shopify не ўдалося абнавіць: {message}";
    }

    private static string FormatShopifyCostSyncWarning( Exception ex ) =>
        $"Прапанова прынята, але кошт закупкі ў Shopify не ўдалося абнавіць: {ex.Message}";

    /// <summary>
    /// Kirma: preview Odoo fields before creating a new Shopify product for a received offer.
    /// </summary>
    public async Task<KirmaBukinistkaOfferCreateShopifyProductPreviewDto> GetCreateShopifyProductPreviewAsync(
        int id,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default )
    {
        RequireKirmaSession();
        KirmaBukinistkaOffer row = await RequirePendingReceivedOfferAsync( id );
        if (row.OdooProductId is null or <= 0)
        {
            throw new InvalidOperationException( "У прапанове няма Odoo прадукту." );
        }

        OdooProductService.OdooProductShopifySourceDetails odoo =
            await _odooProducts.GetProductShopifySourceDetailsWithSyncSessionAsync(
                row.OdooProductId.Value,
                cancellationToken );

        decimal? odooListPrice = odoo.ListPrice > 0m
            ? Math.Round( odoo.ListPrice, 2, MidpointRounding.AwayFromZero )
            : null;

        return new KirmaBukinistkaOfferCreateShopifyProductPreviewDto
        {
            ProductName = odoo.Name.Trim(),
            ProductAuthor = odoo.Author ?? (row.ProductAuthor ?? string.Empty).Trim(),
            GrossUnitCost = Math.Round( row.GrossUnitCost, 2, MidpointRounding.AwayFromZero ),
            OdooListPrice = odooListPrice,
            Vendor = odoo.PublisherName ?? row.SupplierName,
            ProductType = ShopifyInventoryService.DefaultBookProductType,
            BarcodeDigits = odoo.BarcodeDigits,
            WeightKg = odoo.WeightKg > 0m
                ? $"{odoo.WeightKg.ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture )} kg"
                : odoo.WeightKgLabel,
            DescriptionHtml = odoo.DescriptionHtml,
        };
    }

    /// <summary>
    /// Kirma: create a new Shopify product card from a received Bukinistka offer + Odoo metadata.
    /// </summary>
    public async Task<KirmaBukinistkaOfferCreateShopifyProductResultDto> CreateShopifyProductForReceivedOfferAsync(
        int id,
        KirmaBukinistkaOfferCreateShopifyProductRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default )
    {
        if (!ShopifySessionReader.TryGet( _http, out ShopifySession session ))
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Kirma." );
        }

        if (request.SalePrice < 0m)
        {
            throw new InvalidOperationException( "Цана продажу не можа быць адмоўнай." );
        }

        KirmaBukinistkaOffer row = await RequirePendingReceivedOfferAsync( id );
        if (row.OdooProductId is null or <= 0)
        {
            throw new InvalidOperationException( "У прапанове няма Odoo прадукту." );
        }

        OdooProductService.OdooProductShopifySourceDetails odoo =
            await _odooProducts.GetProductShopifySourceDetailsWithSyncSessionAsync(
                row.OdooProductId.Value,
                cancellationToken );

        string? barcodeDigits = ResolveCreateShopifyBarcode( request, odoo.BarcodeDigits );
        string title = odoo.Name.Trim();
        if (string.IsNullOrWhiteSpace( title ))
        {
            title = row.ProductName.Trim();
        }

        string? author = odoo.Author;
        if (string.IsNullOrWhiteSpace( author ))
        {
            author = string.IsNullOrWhiteSpace( row.ProductAuthor ) ? null : row.ProductAuthor.Trim();
        }

        decimal salePrice = Math.Round( request.SalePrice, 2, MidpointRounding.AwayFromZero );
        decimal unitCost = Math.Round( row.GrossUnitCost, 2, MidpointRounding.AwayFromZero );
        decimal? weightKg = odoo.WeightKg > 0m ? odoo.WeightKg : null;

        ShopifyInventoryService.CreatedShopifyProduct created;
        try
        {
            created = await _shopifyInventory.CreateProductAsync(
                session.Shop,
                session.AccessToken,
                new ShopifyInventoryService.CreateShopifyProductInput(
                    title,
                    salePrice,
                    unitCost,
                    odoo.PublisherName ?? row.SupplierName,
                    ShopifyInventoryService.DefaultBookProductType,
                    barcodeDigits,
                    odoo.DescriptionHtml,
                    author,
                    weightKg,
                    PublishAsDraft: true ) );
        }
        catch (Exception ex) when (IsShopifyBarcodeConflictMessage( ex.Message ))
        {
            throw new InvalidOperationException(
                "barcode_conflict: Штрихкод ужо заняты ў Shopify. " +
                "Абярыце пусты ISBN або ўвядзіце іншы ўручную." );
        }

        string storeSlug = session.Shop.Replace( ".myshopify.com", "", StringComparison.OrdinalIgnoreCase );
        return new KirmaBukinistkaOfferCreateShopifyProductResultDto
        {
            ShopifyProductId = created.ProductId,
            ShopifyVariantId = created.VariantId,
            ShopifyProductName = created.Title,
            SalePrice = salePrice,
        };
    }

    public async Task<BukinistkaProposeEligibilityDto> GetBukProposeEligibilityAsync(
        int odooProductId,
        CancellationToken cancellationToken = default )
    {
        if (odooProductId <= 0)
        {
            return new BukinistkaProposeEligibilityDto
            {
                CanPropose = false,
                BlockReason = "Некарэктны прадукт."
            };
        }

        string? reason = await GetBukProposeBlockReasonAsync( odooProductId, cancellationToken );
        return new BukinistkaProposeEligibilityDto
        {
            CanPropose = reason is null,
            BlockReason = reason
        };
    }

    public async Task<BukinistkaProposeEligibilityDto> GetKirmaProposeEligibilityAsync(
        string shopifyProductId,
        string? shopifyVariantId = null,
        CancellationToken cancellationToken = default )
    {
        string productId = ShopifyIds.NormalizeProductId( (shopifyProductId ?? string.Empty).Trim() );
        if (string.IsNullOrWhiteSpace( productId ))
        {
            return new BukinistkaProposeEligibilityDto
            {
                CanPropose = false,
                BlockReason = "Некарэктны прадукт."
            };
        }

        string variantId = string.IsNullOrWhiteSpace( shopifyVariantId )
            ? string.Empty
            : ShopifyIds.NormalizeVariantId( shopifyVariantId.Trim() );
        string? reason = await GetKirmaProposeBlockReasonAsync( productId, variantId, cancellationToken );
        return new BukinistkaProposeEligibilityDto
        {
            CanPropose = reason is null,
            BlockReason = reason
        };
    }

    public async Task<Dictionary<int, BukinistkaProposeEligibilityDto>> GetBukProposeEligibilityMapAsync(
        IEnumerable<int> odooProductIds,
        CancellationToken cancellationToken = default)
    {
        Dictionary<int, BukinistkaProposeEligibilityDto> result = new();
        foreach (int odooProductId in odooProductIds.Distinct())
        {
            result[odooProductId] = await GetBukProposeEligibilityAsync( odooProductId, cancellationToken );
        }

        return result;
    }

    public async Task<KirmaBukinistkaOfferDto> UpdateSentAsync(
        int id,
        KirmaBukinistkaOfferUpdateRequest request )
    {
        RequireKirmaSession();
        KirmaBukinistkaOffer? row = await _db.KirmaBukinistkaOffers
            .FirstOrDefaultAsync( x => x.Id == id );
        if (row is null)
        {
            throw new InvalidOperationException( "Прапанова не знойдзена." );
        }

        if (!string.Equals(
                NormalizeDirection( row.Direction ),
                KirmaBukinistkaOfferDirections.KirmaToBukinistka,
                StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Гэтая прапанова не ад Кірмаша." );
        }

        await ApplySentOfferUpdateAsync( row, request );
        return await ToDtoAsync( row );
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

        if (!string.Equals(
                NormalizeDirection( row.Direction ),
                KirmaBukinistkaOfferDirections.KirmaToBukinistka,
                StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Гэтая прапанова не ад Кірмаша." );
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

        if (!string.Equals(
                NormalizeDirection( row.Direction ),
                KirmaBukinistkaOfferDirections.KirmaToBukinistka,
                StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Гэтая прапанова не ад Кірмаша." );
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
        row.AcceptedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync( cancellationToken );
        return await ToDtoAsync( row, cancellationToken );
    }

    /// <summary>
    /// Bukinistka: preview Shopify fields before creating a new Odoo product for an offer.
    /// </summary>
    public async Task<KirmaBukinistkaOfferCreateProductPreviewDto> GetCreateProductPreviewAsync(
        int id,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default )
    {
        if (BukinistkaJwtAuthentication.TryValidateCookie( httpRequest, _config ) is null)
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        KirmaBukinistkaOffer row = await RequirePendingOfferAsync( id );
        ShopifyProductSnapshot shopify = await FetchShopifyProductSnapshotAsync(
            row,
            cancellationToken );

        return new KirmaBukinistkaOfferCreateProductPreviewDto
        {
            ProductName = row.ProductName,
            ProductAuthor = row.ProductAuthor,
            GrossUnitCost = Math.Round( row.GrossUnitCost, 2, MidpointRounding.AwayFromZero ),
            ShopifySalePrice = shopify.SalePrice,
            Vendor = shopify.Vendor,
            ProductType = shopify.ProductType,
            BarcodeDigits = shopify.BarcodeDigits,
            WeightKg = shopify.WeightKgLabel,
        };
    }

    /// <summary>
    /// Bukinistka: create a new Odoo product card from the Kirma offer + Shopify metadata.
    /// </summary>
    public async Task<KirmaBukinistkaOfferCreateProductResultDto> CreateOdooProductForOfferAsync(
        int id,
        KirmaBukinistkaOfferCreateProductRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default )
    {
        if (BukinistkaJwtAuthentication.TryValidateCookie( httpRequest, _config ) is null)
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        if (request.ListPrice < 0m)
        {
            throw new InvalidOperationException( "Цана продажу не можа быць адмоўнай." );
        }

        KirmaBukinistkaOffer row = await RequirePendingOfferAsync( id );
        ShopifyProductSnapshot shopify = await FetchShopifyProductSnapshotAsync(
            row,
            cancellationToken );

        string? barcodeDigits = ResolveCreateProductBarcode( request, shopify.BarcodeDigits );

        List<string> authors = SplitAuthors( row.ProductAuthor );
        if (authors.Count == 0)
        {
            authors = SplitAuthors( shopify.Author );
        }

        OdooProductService.CreatedOdooProduct created;
        try
        {
            created = await _odooProducts.CreateProductFromShopifyAsync(
                httpRequest,
                new OdooProductService.CreateProductFromShopifyInput(
                    row.ProductName,
                    Math.Round( request.ListPrice, 2, MidpointRounding.AwayFromZero ),
                    Math.Round( row.GrossUnitCost, 2, MidpointRounding.AwayFromZero ),
                    barcodeDigits,
                    authors,
                    shopify.Vendor,
                    shopify.ProductType,
                    shopify.WeightKgLabel,
                    shopify.DescriptionHtml,
                    shopify.ImageUrls ),
                cancellationToken );
        }
        catch (Exception ex) when (IsOdooBarcodeConflict( ex.Message ))
        {
            throw new InvalidOperationException(
                "barcode_conflict: Штрихкод ужо заняты ў Odoo (у журналаў часта аднолькавы ISBN). " +
                "Абярыце пусты ISBN або ўвядзіце іншы ўручную." );
        }

        return new KirmaBukinistkaOfferCreateProductResultDto
        {
            OdooProductId = created.Id,
            OdooProductName = created.Name,
            OdooUrl = created.OdooUrl,
            ListPrice = Math.Round( request.ListPrice, 2, MidpointRounding.AwayFromZero ),
        };
    }

    private static string? ResolveCreateProductBarcode(
        KirmaBukinistkaOfferCreateProductRequest request,
        string? shopifyBarcodeDigits )
    {
        if (request.OmitBarcode == true)
        {
            return null;
        }

        if (request.BarcodeDigits is not null)
        {
            string? overrideDigits = DigitsOnly( request.BarcodeDigits );
            return string.IsNullOrWhiteSpace( overrideDigits ) ? null : overrideDigits;
        }

        return string.IsNullOrWhiteSpace( shopifyBarcodeDigits ) ? null : shopifyBarcodeDigits;
    }

    private static bool IsOdooBarcodeConflict( string? message )
    {
        if (string.IsNullOrWhiteSpace( message ))
        {
            return false;
        }

        string m = message;
        return m.Contains( "already assigned", StringComparison.OrdinalIgnoreCase ) ||
               m.Contains( "Kody kreskowe", StringComparison.OrdinalIgnoreCase ) ||
               m.Contains( "przypisane", StringComparison.OrdinalIgnoreCase ) ||
               (m.Contains( "barcode", StringComparison.OrdinalIgnoreCase ) &&
                m.Contains( "assigned", StringComparison.OrdinalIgnoreCase ));
    }

    /// <summary>
    /// Batch receipt: apply price choices, create one Odoo Przyjęcia, then mark offers Accepted.
    /// On Odoo failure, keeps (or updates) the Open/Failed draft with LastError for retry.
    /// </summary>
    public async Task<KirmaBukinistkaOfferReceiptResultDto> SaveReceiptForBukinistkaAsync(
        KirmaBukinistkaOfferReceiptRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default )
    {
        System.Security.Claims.ClaimsPrincipal principal = RequireBukinistkaPrincipal( httpRequest );

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

        try
        {
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
                item.Offer.AcceptedAtUtc = DateTime.UtcNow;
            }

            KirmaBukinistkaReceiptDraft? draft = await FindActiveReceiptDraftAsync( cancellationToken );
            if (draft is not null)
            {
                draft.Status = KirmaBukinistkaReceiptDraftStatuses.Completed;
                draft.LastError = null;
                draft.OdooPickingId = receipt.PickingId;
                draft.OdooPickingName = receipt.PickingName;
                draft.UpdatedAtUtc = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync( cancellationToken );

            return new KirmaBukinistkaOfferReceiptResultDto
            {
                PickingId = receipt.PickingId,
                PickingName = receipt.PickingName,
                Offers = await MapDtosAsync( prepared.Select( x => x.Offer ).ToList(), cancellationToken ),
            };
        }
        catch (Exception ex) when (ex is not UnauthorizedAccessException)
        {
            await MarkActiveReceiptDraftFailedAsync( principal, request.Lines, ex.Message, cancellationToken );
            throw;
        }
    }

    public async Task<KirmaBukinistkaReceiptDraftDto?> GetActiveReceiptDraftForBukinistkaAsync(
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default )
    {
        RequireBukinistkaPrincipal( httpRequest );
        KirmaBukinistkaReceiptDraft? draft = await FindActiveReceiptDraftAsync( cancellationToken );
        return draft is null ? null : ToReceiptDraftDto( draft );
    }

    public async Task<KirmaBukinistkaReceiptDraftDto> UpsertReceiptDraftForBukinistkaAsync(
        KirmaBukinistkaReceiptDraftUpsertRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default )
    {
        System.Security.Claims.ClaimsPrincipal principal = RequireBukinistkaPrincipal( httpRequest );
        string login = ResolveBukinistkaLogin( principal );
        List<KirmaBukinistkaReceiptDraftLineRequest> lines = request.Lines ?? new();

        if (lines.Count > 0)
        {
            List<int> offerIds = lines.Select( x => x.OfferId ).Distinct().ToList();
            if (offerIds.Count != lines.Count)
            {
                throw new InvalidOperationException( "Прапанова не можа быць дададзена ў прыёмку двойчы." );
            }

            foreach (KirmaBukinistkaReceiptDraftLineRequest line in lines)
            {
                if (line.OfferId <= 0 || line.OdooProductId <= 0)
                {
                    throw new InvalidOperationException( "Некарэктны радок прыёмкі." );
                }
            }

            int pendingCount = await _db.KirmaBukinistkaOffers
                .AsNoTracking()
                .CountAsync(
                    x => offerIds.Contains( x.Id )
                         && x.Status == KirmaBukinistkaOfferStatuses.Pending,
                    cancellationToken );
            if (pendingCount != offerIds.Count)
            {
                throw new InvalidOperationException(
                    "У чарнавік можна дадаць толькі непрынятыя прапановы." );
            }
        }

        KirmaBukinistkaReceiptDraft? draft = await FindActiveReceiptDraftAsync( cancellationToken );
        DateTime now = DateTime.UtcNow;
        if (draft is null)
        {
            draft = new KirmaBukinistkaReceiptDraft
            {
                Status = KirmaBukinistkaReceiptDraftStatuses.Open,
                CreatedByLogin = login,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            _db.KirmaBukinistkaReceiptDrafts.Add( draft );
        }
        else
        {
            draft.Status = KirmaBukinistkaReceiptDraftStatuses.Open;
            draft.LastError = null;
            draft.UpdatedAtUtc = now;
            _db.KirmaBukinistkaReceiptDraftLines.RemoveRange( draft.Lines );
            draft.Lines.Clear();
        }

        foreach (KirmaBukinistkaReceiptDraftLineRequest line in lines)
        {
            draft.Lines.Add( new KirmaBukinistkaReceiptDraftLine
            {
                OfferId = line.OfferId,
                OdooProductId = line.OdooProductId,
                OdooProductName = (line.OdooProductName ?? string.Empty).Trim(),
                ListPrice = line.ListPrice,
                ApplyKirmaCostPrice = line.ApplyKirmaCostPrice,
            } );
        }

        await _db.SaveChangesAsync( cancellationToken );
        return ToReceiptDraftDto( draft );
    }

    public async Task DeleteActiveReceiptDraftForBukinistkaAsync(
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default )
    {
        RequireBukinistkaPrincipal( httpRequest );
        KirmaBukinistkaReceiptDraft? draft = await FindActiveReceiptDraftAsync( cancellationToken );
        if (draft is null)
        {
            return;
        }

        _db.KirmaBukinistkaReceiptDrafts.Remove( draft );
        await _db.SaveChangesAsync( cancellationToken );
    }

    private async Task<KirmaBukinistkaReceiptDraft?> FindActiveReceiptDraftAsync(
        CancellationToken cancellationToken )
    {
        return await _db.KirmaBukinistkaReceiptDrafts
            .Include( x => x.Lines )
            .Where( x =>
                x.Status == KirmaBukinistkaReceiptDraftStatuses.Open
                || x.Status == KirmaBukinistkaReceiptDraftStatuses.Failed )
            .OrderByDescending( x => x.UpdatedAtUtc )
            .FirstOrDefaultAsync( cancellationToken );
    }

    private async Task MarkActiveReceiptDraftFailedAsync(
        System.Security.Claims.ClaimsPrincipal principal,
        List<KirmaBukinistkaOfferReceiptLineRequest> lines,
        string errorMessage,
        CancellationToken cancellationToken )
    {
        try
        {
            string login = ResolveBukinistkaLogin( principal );
            string trimmedError = (errorMessage ?? string.Empty).Trim();
            if (trimmedError.Length > 2048)
            {
                trimmedError = trimmedError[..2048];
            }

            KirmaBukinistkaReceiptDraft? draft = await FindActiveReceiptDraftAsync( cancellationToken );
            DateTime now = DateTime.UtcNow;
            if (draft is null)
            {
                draft = new KirmaBukinistkaReceiptDraft
                {
                    Status = KirmaBukinistkaReceiptDraftStatuses.Failed,
                    CreatedByLogin = login,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    LastError = trimmedError,
                };
                _db.KirmaBukinistkaReceiptDrafts.Add( draft );

                foreach (KirmaBukinistkaOfferReceiptLineRequest line in lines)
                {
                    draft.Lines.Add( new KirmaBukinistkaReceiptDraftLine
                    {
                        OfferId = line.OfferId,
                        OdooProductId = line.OdooProductId,
                        OdooProductName = string.Empty,
                        ListPrice = line.ListPrice,
                        ApplyKirmaCostPrice = line.ApplyKirmaCostPrice,
                    } );
                }
            }
            else
            {
                draft.Status = KirmaBukinistkaReceiptDraftStatuses.Failed;
                draft.LastError = trimmedError;
                draft.UpdatedAtUtc = now;
            }

            await _db.SaveChangesAsync( cancellationToken );
        }
        catch
        {
            // Do not hide the original Odoo/receipt error.
        }
    }

    private System.Security.Claims.ClaimsPrincipal RequireBukinistkaPrincipal( HttpRequest httpRequest )
    {
        System.Security.Claims.ClaimsPrincipal? principal =
            BukinistkaJwtAuthentication.TryValidateCookie( httpRequest, _config );
        if (principal is null)
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        return principal;
    }

    private static string ResolveBukinistkaLogin( System.Security.Claims.ClaimsPrincipal principal ) =>
        principal.FindFirst( "odoo_login" )?.Value?.Trim()
        ?? principal.FindFirst( "sub" )?.Value?.Trim()
        ?? "bukinistka";

    private static KirmaBukinistkaReceiptDraftDto ToReceiptDraftDto( KirmaBukinistkaReceiptDraft draft ) =>
        new()
        {
            Id = draft.Id,
            Status = draft.Status,
            LastError = draft.LastError,
            CreatedAtUtc = draft.CreatedAtUtc,
            UpdatedAtUtc = draft.UpdatedAtUtc,
            Lines = draft.Lines
                .OrderBy( x => x.Id )
                .Select( x => new KirmaBukinistkaReceiptDraftLineDto
                {
                    OfferId = x.OfferId,
                    OdooProductId = x.OdooProductId,
                    OdooProductName = x.OdooProductName,
                    ListPrice = x.ListPrice,
                    ApplyKirmaCostPrice = x.ApplyKirmaCostPrice,
                } )
                .ToList(),
        };

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

    private async Task ApplySentOfferUpdateAsync(
        KirmaBukinistkaOffer row,
        KirmaBukinistkaOfferUpdateRequest request )
    {
        if (request.GrossUnitCost < 0m)
        {
            throw new InvalidOperationException( "Кошт брута не можа быць адмоўным." );
        }

        string status = NormalizeStatus( row.Status );
        bool pending = string.Equals(
            status,
            KirmaBukinistkaOfferStatuses.Pending,
            StringComparison.OrdinalIgnoreCase );
        bool accepted = string.Equals(
            status,
            KirmaBukinistkaOfferStatuses.Accepted,
            StringComparison.OrdinalIgnoreCase );

        if (!pending && !accepted)
        {
            throw new InvalidOperationException(
                "Можна змяняць толькі чакаючыя або прынятыя (яшчэ актуальныя) прапановы." );
        }

        if (accepted)
        {
            Dictionary<int, int> remainingById = await BuildRemainingByOfferIdAsync( [row] );
            int remaining = remainingById.GetValueOrDefault( row.Id, row.Quantity );
            if (remaining <= 0)
            {
                throw new InvalidOperationException(
                    "Нельга змяняць кошт: усе адзінкі ўжо прададзеныя." );
            }

            // Quantity is fixed after accept — only gross cost may change.
            if (request.Quantity != row.Quantity)
            {
                throw new InvalidOperationException(
                    "Пасля прыёмкі можна мяняць толькі кошт брута, не колькасць." );
            }
        }
        else if (request.Quantity <= 0)
        {
            throw new InvalidOperationException( "Колькасць павінна быць больш за нуль." );
        }

        decimal nextCost = Math.Round( request.GrossUnitCost, 2, MidpointRounding.AwayFromZero );
        bool costChanged = row.GrossUnitCost != nextCost;

        if (pending)
        {
            row.Quantity = request.Quantity;
        }

        row.GrossUnitCost = nextCost;
        if (accepted && costChanged)
        {
            row.PeerPriceChangePending = true;
        }

        await _db.SaveChangesAsync();
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

        if (!string.Equals(
                NormalizeDirection( row.Direction ),
                KirmaBukinistkaOfferDirections.KirmaToBukinistka,
                StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Гэтая прапанова не ад Кірмаша." );
        }

        return row;
    }

    private async Task<KirmaBukinistkaOffer> RequirePendingReceivedOfferAsync( int id )
    {
        RequireKirmaSession();
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
                "Прыняць можна толькі новыя (неразобраныя) прапановы." );
        }

        if (!string.Equals(
                NormalizeDirection( row.Direction ),
                KirmaBukinistkaOfferDirections.BukinistkaToKirma,
                StringComparison.OrdinalIgnoreCase ))
        {
            throw new InvalidOperationException( "Гэтая прапанова не ад Букіністкі." );
        }

        return row;
    }

    private static string? ResolveCreateShopifyBarcode(
        KirmaBukinistkaOfferCreateShopifyProductRequest request,
        string? odooBarcodeDigits )
    {
        if (request.OmitBarcode == true)
        {
            return null;
        }

        if (request.BarcodeDigits is not null)
        {
            string? overrideDigits = DigitsOnly( request.BarcodeDigits );
            return string.IsNullOrWhiteSpace( overrideDigits ) ? null : overrideDigits;
        }

        return string.IsNullOrWhiteSpace( odooBarcodeDigits ) ? null : odooBarcodeDigits;
    }

    private static bool IsShopifyBarcodeConflictMessage( string? message )
    {
        if (string.IsNullOrWhiteSpace( message ))
        {
            return false;
        }

        string lower = message.ToLowerInvariant();
        return lower.Contains( "barcode_conflict" ) ||
               (lower.Contains( "barcode" ) &&
                (lower.Contains( "already" ) || lower.Contains( "taken" ) || lower.Contains( "unique" )));
    }

    private static string NormalizeStatus( string? status ) =>
        string.IsNullOrWhiteSpace( status )
            ? KirmaBukinistkaOfferStatuses.Pending
            : status.Trim();

    private static string NormalizeDirection( string? direction ) =>
        string.IsNullOrWhiteSpace( direction )
            ? KirmaBukinistkaOfferDirections.KirmaToBukinistka
            : direction.Trim();

    private async Task EnsureCanProposeKirmaToBukAsync(
        string shopifyProductId,
        string shopifyVariantId,
        CancellationToken cancellationToken = default )
    {
        string? reason = await GetKirmaProposeBlockReasonAsync(
            shopifyProductId,
            shopifyVariantId,
            cancellationToken );
        if (reason is not null)
        {
            throw new InvalidOperationException( reason );
        }
    }

    private async Task EnsureCanProposeBukToKirmaAsync(
        int odooProductId,
        CancellationToken cancellationToken = default )
    {
        string? reason = await GetBukProposeBlockReasonAsync( odooProductId, cancellationToken );
        if (reason is not null)
        {
            throw new InvalidOperationException( reason );
        }
    }

    private async Task<string?> GetKirmaProposeBlockReasonAsync(
        string shopifyProductId,
        string shopifyVariantId,
        CancellationToken cancellationToken )
    {
        List<KirmaBukinistkaOffer> related = await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .Where( x => x.ShopifyProductId == shopifyProductId )
            .ToListAsync( cancellationToken );

        HashSet<int> linkedOdooIds = related
            .Where( x => x.OdooProductId is > 0 )
            .Select( x => x.OdooProductId!.Value )
            .ToHashSet();

        if (linkedOdooIds.Count > 0)
        {
            List<KirmaBukinistkaOffer> byOdoo = await _db.KirmaBukinistkaOffers
                .AsNoTracking()
                .Where( x => x.OdooProductId != null && linkedOdooIds.Contains( x.OdooProductId.Value ) )
                .ToListAsync( cancellationToken );
            related = related
                .Concat( byOdoo )
                .GroupBy( x => x.Id )
                .Select( g => g.First() )
                .ToList();
        }

        Dictionary<int, int> remainingMap = await BuildRemainingByOfferIdAsync( related, cancellationToken );
        foreach (KirmaBukinistkaOffer offer in related)
        {
            string status = NormalizeStatus( offer.Status );
            string direction = NormalizeDirection( offer.Direction );
            int remaining = remainingMap.GetValueOrDefault( offer.Id, offer.Quantity );
            bool sameShopify = MatchesShopifyLine( offer, shopifyProductId, shopifyVariantId );
            bool linkedViaOdoo = offer.OdooProductId is > 0 && linkedOdooIds.Contains( offer.OdooProductId.Value );

            if (string.Equals( status, KirmaBukinistkaOfferStatuses.Pending, StringComparison.OrdinalIgnoreCase ))
            {
                if (string.Equals( direction, KirmaBukinistkaOfferDirections.BukinistkaToKirma, StringComparison.OrdinalIgnoreCase )
                    && (sameShopify || linkedViaOdoo))
                {
                    return "Ужо ёсць актыўная прапанова ад Букіністкі па гэтым тавары.";
                }

                if (sameShopify
                    && string.Equals( direction, KirmaBukinistkaOfferDirections.KirmaToBukinistka, StringComparison.OrdinalIgnoreCase ))
                {
                    return "Ужо ёсць неразобраная прапанова ў Букіністыку па гэтым тавары.";
                }
            }

            if (string.Equals( status, KirmaBukinistkaOfferStatuses.Accepted, StringComparison.OrdinalIgnoreCase )
                && remaining > 0)
            {
                if (string.Equals( direction, KirmaBukinistkaOfferDirections.BukinistkaToKirma, StringComparison.OrdinalIgnoreCase )
                    && (sameShopify || linkedViaOdoo))
                {
                    return "Экземпляры ад Букіністкі яшчэ не закончыліся.";
                }

                if (sameShopify
                    && string.Equals( direction, KirmaBukinistkaOfferDirections.KirmaToBukinistka, StringComparison.OrdinalIgnoreCase ))
                {
                    return "Папярэдняя прапанова ў Букіністыку яшчэ не вычарпаная.";
                }
            }
        }

        return null;
    }

    private async Task<string?> GetBukProposeBlockReasonAsync(
        int odooProductId,
        CancellationToken cancellationToken )
    {
        List<KirmaBukinistkaOffer> related = await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .Where( x => x.OdooProductId == odooProductId )
            .ToListAsync( cancellationToken );

        // Also block via Shopify ids that were linked to this Odoo product on accepted Kirma→Buk offers.
        HashSet<string> linkedShopifyIds = related
            .Where( x =>
                string.Equals(
                    NormalizeDirection( x.Direction ),
                    KirmaBukinistkaOfferDirections.KirmaToBukinistka,
                    StringComparison.OrdinalIgnoreCase )
                && !string.IsNullOrWhiteSpace( x.ShopifyProductId ) )
            .Select( x => x.ShopifyProductId )
            .ToHashSet( StringComparer.OrdinalIgnoreCase );

        if (linkedShopifyIds.Count > 0)
        {
            List<KirmaBukinistkaOffer> byShopify = await _db.KirmaBukinistkaOffers
                .AsNoTracking()
                .Where( x => linkedShopifyIds.Contains( x.ShopifyProductId ) )
                .ToListAsync( cancellationToken );
            related = related
                .Concat( byShopify )
                .GroupBy( x => x.Id )
                .Select( g => g.First() )
                .ToList();
        }

        Dictionary<int, int> remainingMap = await BuildRemainingByOfferIdAsync( related, cancellationToken );
        foreach (KirmaBukinistkaOffer offer in related)
        {
            string status = NormalizeStatus( offer.Status );
            string direction = NormalizeDirection( offer.Direction );
            int remaining = remainingMap.GetValueOrDefault( offer.Id, offer.Quantity );
            bool sameOdoo = offer.OdooProductId == odooProductId;

            if (string.Equals( status, KirmaBukinistkaOfferStatuses.Pending, StringComparison.OrdinalIgnoreCase ))
            {
                if (string.Equals( direction, KirmaBukinistkaOfferDirections.KirmaToBukinistka, StringComparison.OrdinalIgnoreCase )
                    && (sameOdoo || linkedShopifyIds.Contains( offer.ShopifyProductId )))
                {
                    return "Ужо ёсць актыўная прапанова ад Кірмаша па гэтым тавары.";
                }

                if (sameOdoo
                    && string.Equals( direction, KirmaBukinistkaOfferDirections.BukinistkaToKirma, StringComparison.OrdinalIgnoreCase ))
                {
                    return "Ужо ёсць неразобраная прапанова Кірмашу па гэтым тавары.";
                }
            }

            if (string.Equals( status, KirmaBukinistkaOfferStatuses.Accepted, StringComparison.OrdinalIgnoreCase )
                && remaining > 0)
            {
                if (string.Equals( direction, KirmaBukinistkaOfferDirections.KirmaToBukinistka, StringComparison.OrdinalIgnoreCase )
                    && sameOdoo)
                {
                    return "Экземпляры ад Кірмаша яшчэ не закончыліся.";
                }

                if (sameOdoo
                    && string.Equals( direction, KirmaBukinistkaOfferDirections.BukinistkaToKirma, StringComparison.OrdinalIgnoreCase ))
                {
                    return "Папярэдняя прапанова Кірмашу яшчэ не вычарпаная.";
                }
            }
        }

        return null;
    }

    private static bool MatchesShopifyLine(
        KirmaBukinistkaOffer offer,
        string shopifyProductId,
        string shopifyVariantId )
    {
        if (!string.Equals(
                ShopifyIds.NormalizeProductId( offer.ShopifyProductId ),
                shopifyProductId,
                StringComparison.OrdinalIgnoreCase ))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace( shopifyVariantId )
            || string.IsNullOrWhiteSpace( offer.ShopifyVariantId ))
        {
            return true;
        }

        return string.Equals(
            ShopifyIds.NormalizeVariantId( offer.ShopifyVariantId ),
            shopifyVariantId,
            StringComparison.OrdinalIgnoreCase );
    }

    private async Task<(string ProductId, string VariantId)> TryResolveShopifyIdsForOdooProductAsync(
        int odooProductId,
        CancellationToken cancellationToken )
    {
        KirmaBukinistkaOffer? linked = await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .Where( x =>
                x.OdooProductId == odooProductId
                && x.Status == KirmaBukinistkaOfferStatuses.Accepted
                && !string.IsNullOrWhiteSpace( x.ShopifyProductId ) )
            .OrderByDescending( x => x.CreatedAtUtc )
            .ThenByDescending( x => x.Id )
            .FirstOrDefaultAsync( cancellationToken );

        if (linked is null)
        {
            return (string.Empty, string.Empty);
        }

        return (
            ShopifyIds.NormalizeProductId( linked.ShopifyProductId ),
            ShopifyIds.NormalizeVariantId( linked.ShopifyVariantId ) );
    }

    private async Task<Dictionary<int, int>> BuildRemainingByOfferIdAsync(
        IReadOnlyList<KirmaBukinistkaOffer> offers,
        CancellationToken cancellationToken = default )
    {
        Dictionary<int, int> result = new();
        if (offers.Count == 0)
        {
            return result;
        }

        List<int> offerIds = offers.Select( x => x.Id ).ToList();
        Dictionary<int, int> deliveryUsed = await _db.KirmaBukinistkaShopifyDeliverySyncs
            .AsNoTracking()
            .Where( x => offerIds.Contains( x.OfferId ) && !x.IsCancelled )
            .GroupBy( x => x.OfferId )
            .Select( g => new { OfferId = g.Key, Qty = g.Sum( x => x.Quantity ) } )
            .ToDictionaryAsync( x => x.OfferId, x => x.Qty, cancellationToken );

        Dictionary<int, int> posUsed = await _db.KirmaBukinistkaPosSales
            .AsNoTracking()
            .Where( x =>
                x.OfferId != null
                && offerIds.Contains( x.OfferId.Value )
                && !x.IsOwnStock
                && !x.IsReversed
                && !x.IsReturn
                && x.Quantity > 0 )
            .GroupBy( x => x.OfferId!.Value )
            .Select( g => new { OfferId = g.Key, Qty = g.Sum( x => x.Quantity ) } )
            .ToDictionaryAsync( x => x.OfferId, x => x.Qty, cancellationToken );

        foreach (KirmaBukinistkaOffer offer in offers)
        {
            int used = deliveryUsed.GetValueOrDefault( offer.Id );
            if (CountsAsKirmaPosConsignment( offer ))
            {
                used += posUsed.GetValueOrDefault( offer.Id );
            }

            result[offer.Id] = Math.Max( 0, offer.Quantity - used );
        }

        return result;
    }

    /// <summary>
    /// Kirma→Buk offers and Buk assignments accepted as Kirma consignments at Buk POS.
    /// </summary>
    private static bool CountsAsKirmaPosConsignment( KirmaBukinistkaOffer offer ) =>
        offer.IsAssignment
        || string.Equals(
            NormalizeDirection( offer.Direction ),
            KirmaBukinistkaOfferDirections.KirmaToBukinistka,
            StringComparison.OrdinalIgnoreCase );

    private async Task<List<KirmaBukinistkaOfferDto>> MapDtosAsync(
        IReadOnlyList<KirmaBukinistkaOffer> rows,
        CancellationToken cancellationToken = default)
    {
        Dictionary<int, int> remaining = await BuildRemainingByOfferIdAsync( rows, cancellationToken );
        return rows
            .Select( row => ToDto( row, remaining.GetValueOrDefault( row.Id, row.Quantity ) ) )
            .ToList();
    }

    private async Task<KirmaBukinistkaOfferDto> ToDtoAsync(
        KirmaBukinistkaOffer row,
        CancellationToken cancellationToken = default)
    {
        Dictionary<int, int> remaining = await BuildRemainingByOfferIdAsync( [row], cancellationToken );
        return ToDto( row, remaining.GetValueOrDefault( row.Id, row.Quantity ) );
    }

    private static KirmaBukinistkaOfferDto ToDto( KirmaBukinistkaOffer row, int remainingQuantity ) => new()
    {
        Id = row.Id,
        Direction = NormalizeDirection( row.Direction ),
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
        SyncOnSale = row.SyncOnSale,
        IsAssignment = row.IsAssignment,
        PeerPriceChangePending = row.PeerPriceChangePending,
        RemainingQuantity = remainingQuantity,
        CreatedAtUtc = row.CreatedAtUtc,
    };

    private async Task<List<KirmaBukinistkaOfferDto>> EnrichShopifySalePricesAsync(
        List<KirmaBukinistkaOfferDto> dtos,
        CancellationToken cancellationToken = default )
    {
        List<(string ProductId, string VariantId)> lineKeys = dtos
            .Where( x => !string.IsNullOrWhiteSpace( x.ShopifyProductId ) )
            .Select( x =>
            (
                ProductId: ShopifyIds.NormalizeProductId( x.ShopifyProductId ),
                VariantId: string.IsNullOrWhiteSpace( x.ShopifyVariantId )
                    ? string.Empty
                    : ShopifyIds.NormalizeVariantId( x.ShopifyVariantId )
            ) )
            .Where( x => !string.IsNullOrWhiteSpace( x.ProductId ) )
            .Distinct()
            .ToList();

        if (lineKeys.Count == 0)
        {
            return dtos;
        }

        if (!ShopifySessionReader.TryGet( _http, out ShopifySession session ))
        {
            return dtos;
        }

        string shop = string.IsNullOrWhiteSpace( session.Shop )
            ? (_config["Shopify:Shop"] ?? string.Empty).Trim()
            : session.Shop.Trim();
        string accessToken = string.IsNullOrWhiteSpace( session.AccessToken )
            ? (_config["Shopify:AccessToken"] ?? string.Empty).Trim()
            : session.AccessToken.Trim();
        if (string.IsNullOrWhiteSpace( shop ) || string.IsNullOrWhiteSpace( accessToken ))
        {
            return dtos;
        }

        Dictionary<string, decimal> prices;
        try
        {
            prices = await _shopifyInventory.GetVariantPricesByProductKeysAsync(
                shop,
                accessToken,
                lineKeys );
        }
        catch
        {
            return dtos;
        }

        foreach (KirmaBukinistkaOfferDto dto in dtos)
        {
            if (string.IsNullOrWhiteSpace( dto.ShopifyProductId ))
            {
                continue;
            }

            string productId = ShopifyIds.NormalizeProductId( dto.ShopifyProductId );
            string variantId = string.IsNullOrWhiteSpace( dto.ShopifyVariantId )
                ? string.Empty
                : ShopifyIds.NormalizeVariantId( dto.ShopifyVariantId );
            string lineKey = string.IsNullOrWhiteSpace( variantId )
                ? productId
                : $"{productId}::{variantId}";

            if (prices.TryGetValue( lineKey, out decimal linePrice ) && linePrice > 0m)
            {
                dto.ShopifySalePrice = Math.Round( linePrice, 2, MidpointRounding.AwayFromZero );
                continue;
            }

            if (prices.TryGetValue( productId, out decimal productPrice ) && productPrice > 0m)
            {
                dto.ShopifySalePrice = Math.Round( productPrice, 2, MidpointRounding.AwayFromZero );
            }
        }

        return dtos;
    }

    private async Task<List<KirmaBukinistkaOfferDto>> EnrichPublisherNamesFromOdooAsync(
        List<KirmaBukinistkaOfferDto> dtos,
        CancellationToken cancellationToken = default )
    {
        List<int> productIds = dtos
            .Where( x => string.IsNullOrWhiteSpace( x.SupplierName ) && x.OdooProductId is > 0 )
            .Select( x => x.OdooProductId!.Value )
            .Distinct()
            .ToList();

        if (productIds.Count == 0)
        {
            return dtos;
        }

        Dictionary<int, string> publishers;
        try
        {
            publishers = await _odooProducts.GetPublisherNamesByProductIdsWithSyncSessionAsync(
                productIds,
                cancellationToken );
        }
        catch
        {
            return dtos;
        }

        foreach (KirmaBukinistkaOfferDto dto in dtos)
        {
            if (!string.IsNullOrWhiteSpace( dto.SupplierName ))
            {
                continue;
            }

            int? odooProductId = dto.OdooProductId;
            if (odooProductId is null or <= 0)
            {
                continue;
            }

            int productId = odooProductId.Value;

            if (publishers.TryGetValue( productId, out string? publisher )
                && !string.IsNullOrWhiteSpace( publisher ))
            {
                dto.SupplierName = publisher.Trim();
            }
        }

        return dtos;
    }

    private async Task<List<KirmaBukinistkaOfferDto>> EnrichProductAuthorsFromOdooAsync(
        List<KirmaBukinistkaOfferDto> dtos,
        CancellationToken cancellationToken = default )
    {
        List<int> productIds = dtos
            .Where( x => string.IsNullOrWhiteSpace( x.ProductAuthor ) && x.OdooProductId is > 0 )
            .Select( x => x.OdooProductId!.Value )
            .Distinct()
            .ToList();

        if (productIds.Count == 0)
        {
            return dtos;
        }

        Dictionary<int, string> authors;
        try
        {
            authors = await _odooProducts.GetAuthorNamesByProductIdsWithSyncSessionAsync(
                productIds,
                cancellationToken );
        }
        catch
        {
            return dtos;
        }

        foreach (KirmaBukinistkaOfferDto dto in dtos)
        {
            if (!string.IsNullOrWhiteSpace( dto.ProductAuthor ))
            {
                continue;
            }

            int? odooProductId = dto.OdooProductId;
            if (odooProductId is null or <= 0)
            {
                continue;
            }

            if (authors.TryGetValue( odooProductId.Value, out string? author )
                && !string.IsNullOrWhiteSpace( author ))
            {
                dto.ProductAuthor = author.Trim();
            }
        }

        return dtos;
    }

    private async Task<List<KirmaBukinistkaOfferDto>> EnrichBukinistkaSalePricesFromOdooAsync(
        List<KirmaBukinistkaOfferDto> dtos,
        CancellationToken cancellationToken = default )
    {
        List<int> productIds = dtos
            .Where( x =>
                x.OdooProductId is > 0
                && (x.BukinistkaSalePrice is null || x.BukinistkaSalePrice <= 0m) )
            .Select( x => x.OdooProductId!.Value )
            .Distinct()
            .ToList();

        if (productIds.Count == 0)
        {
            return dtos;
        }

        Dictionary<int, decimal> prices;
        try
        {
            prices = await _odooProducts.GetListPricesByProductIdsWithSyncSessionAsync(
                productIds,
                cancellationToken );
        }
        catch
        {
            return dtos;
        }

        foreach (KirmaBukinistkaOfferDto dto in dtos)
        {
            if (dto.BukinistkaSalePrice is decimal existing && existing > 0m)
            {
                continue;
            }

            int? odooProductId = dto.OdooProductId;
            if (odooProductId is null or <= 0)
            {
                continue;
            }

            if (prices.TryGetValue( odooProductId.Value, out decimal listPrice ) && listPrice > 0m)
            {
                dto.BukinistkaSalePrice = listPrice;
            }
        }

        return dtos;
    }

    private sealed record ShopifyProductSnapshot(
        decimal? SalePrice,
        string? Vendor,
        string? ProductType,
        string? Author,
        string? BarcodeDigits,
        string? WeightKgLabel,
        string? DescriptionHtml,
        IReadOnlyList<string> ImageUrls );

    private async Task<ShopifyProductSnapshot> FetchShopifyProductSnapshotAsync(
        KirmaBukinistkaOffer offer,
        CancellationToken cancellationToken )
    {
        string shop = (_config["Shopify:Shop"] ?? string.Empty).Trim();
        string accessToken = (_config["Shopify:AccessToken"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( shop ) || string.IsNullOrWhiteSpace( accessToken ))
        {
            throw new InvalidOperationException(
                "Shopify Shop/AccessToken не наладжаныя ў канфігу — нельга прачытаць даныя тавару." );
        }

        if (!long.TryParse( offer.ShopifyProductId, out long productId ) || productId <= 0)
        {
            throw new InvalidOperationException( "Некарэктны ідэнтыфікатар тавару Shopify." );
        }

        HttpClient client = _httpClientFactory.CreateClient();
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Get,
            ShopifyApi.RestUrl( shop, $"products/{productId}.json" ) );
        string body = await response.Content.ReadAsStringAsync( cancellationToken );
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Не ўдалося атрымаць тавар з Shopify: {TrimForError( body )}" );
        }

        using JsonDocument json = JsonDocument.Parse( body );
        if (!json.RootElement.TryGetProperty( "product", out JsonElement product )
            || product.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException( "Shopify вярнуў нечаканы адказ пра тавар." );
        }

        string? vendor = ReadJsonString( product, "vendor" );
        string? productType = ReadJsonString( product, "product_type" );
        string? author = null;

        JsonElement? selectedVariant = null;
        string wantedVariantId = (offer.ShopifyVariantId ?? string.Empty).Trim();
        if (product.TryGetProperty( "variants", out JsonElement variants )
            && variants.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement variant in variants.EnumerateArray())
            {
                if (!string.IsNullOrWhiteSpace( wantedVariantId )
                    && variant.TryGetProperty( "id", out JsonElement idEl )
                    && idEl.ValueKind == JsonValueKind.Number
                    && string.Equals(
                        idEl.GetInt64().ToString(),
                        wantedVariantId,
                        StringComparison.Ordinal ))
                {
                    selectedVariant = variant;
                    break;
                }
            }

            if (selectedVariant is null && variants.GetArrayLength() > 0)
            {
                selectedVariant = variants[0];
            }
        }

        decimal? salePrice = null;
        string? barcodeDigits = null;
        string? weightKgLabel = null;
        if (selectedVariant is JsonElement variantEl)
        {
            if (variantEl.TryGetProperty( "price", out JsonElement priceEl )
                && priceEl.ValueKind == JsonValueKind.String
                && decimal.TryParse(
                    priceEl.GetString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out decimal parsedPrice ))
            {
                salePrice = Math.Round( parsedPrice, 2, MidpointRounding.AwayFromZero );
            }

            string? barcode = ReadJsonString( variantEl, "barcode" );
            barcodeDigits = DigitsOnly( barcode );

            decimal weight = 0m;
            if (variantEl.TryGetProperty( "weight", out JsonElement weightEl )
                && weightEl.ValueKind == JsonValueKind.Number)
            {
                weight = weightEl.GetDecimal();
            }

            string weightUnit = ReadJsonString( variantEl, "weight_unit" ) ?? "kg";
            if (weight > 0m)
            {
                decimal kg = ConvertWeightToKg( weight, weightUnit );
                if (kg > 0m)
                {
                    weightKgLabel = kg.ToString(
                        "0.##",
                        System.Globalization.CultureInfo.InvariantCulture );
                }
            }
        }

        List<string> imageUrls = ExtractShopifyImageUrls( product );
        string? descriptionHtml = ReadJsonString( product, "body_html" );
        if (string.IsNullOrWhiteSpace( descriptionHtml ))
        {
            descriptionHtml = null;
        }

        return new ShopifyProductSnapshot(
            salePrice,
            string.IsNullOrWhiteSpace( vendor ) ? null : vendor.Trim(),
            string.IsNullOrWhiteSpace( productType ) ? null : productType.Trim(),
            author,
            string.IsNullOrWhiteSpace( barcodeDigits ) ? null : barcodeDigits,
            weightKgLabel,
            descriptionHtml,
            imageUrls );
    }

    private static List<string> ExtractShopifyImageUrls( JsonElement product )
    {
        List<(int Position, string Url)> images = new();
        if (product.TryGetProperty( "images", out JsonElement imagesEl )
            && imagesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement image in imagesEl.EnumerateArray())
            {
                string? src = ReadJsonString( image, "src" );
                if (string.IsNullOrWhiteSpace( src ))
                {
                    continue;
                }

                int position = 0;
                if (image.TryGetProperty( "position", out JsonElement posEl )
                    && posEl.ValueKind == JsonValueKind.Number
                    && posEl.TryGetInt32( out int parsedPos ))
                {
                    position = parsedPos;
                }

                images.Add( (position, src) );
            }
        }

        if (images.Count == 0
            && product.TryGetProperty( "image", out JsonElement singleImage )
            && singleImage.ValueKind == JsonValueKind.Object)
        {
            string? src = ReadJsonString( singleImage, "src" );
            if (!string.IsNullOrWhiteSpace( src ))
            {
                images.Add( (1, src) );
            }
        }

        return images
            .OrderBy( x => x.Position )
            .ThenBy( x => x.Url, StringComparer.OrdinalIgnoreCase )
            .Select( x => x.Url )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .ToList();
    }

    private static List<string> SplitAuthors( string? raw )
    {
        List<string> result = new();
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return result;
        }

        foreach (string part in raw.Split(
                     [',', ';', '/', '|', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ))
        {
            string author = part.Trim();
            if (!string.IsNullOrWhiteSpace( author ))
            {
                result.Add( author );
            }
        }

        return result;
    }

    private static string? DigitsOnly( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        char[] digits = raw.Where( char.IsDigit ).ToArray();
        return digits.Length == 0 ? null : new string( digits );
    }

    private static decimal ConvertWeightToKg( decimal weight, string unit )
    {
        string normalized = (unit ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "g" or "gram" or "grams" => weight / 1000m,
            "kg" or "kilogram" or "kilograms" => weight,
            "oz" or "ounce" or "ounces" => weight * 0.028349523125m,
            "lb" or "lbs" or "pound" or "pounds" => weight * 0.45359237m,
            _ => weight
        };
    }

    private static string? ReadJsonString( JsonElement parent, string property )
    {
        if (!parent.TryGetProperty( property, out JsonElement value )
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace( text ) ? null : text;
    }

    private static string TrimForError( string body )
    {
        string trimmed = (body ?? string.Empty).Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }

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
}
