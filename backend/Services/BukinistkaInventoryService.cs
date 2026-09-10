using backend.Data;
using backend.Models;
using backend.Services.Auth;
using backend.Services.Odoo;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public sealed class BukinistkaInventoryService
{
    private readonly AppDbContext _db;
    private readonly OdooProductService _odooProducts;
    private readonly IConfiguration _config;

    public BukinistkaInventoryService(
        AppDbContext db,
        OdooProductService odooProducts,
        IConfiguration config )
    {
        _db = db;
        _odooProducts = odooProducts;
        _config = config;
    }

    public async Task<BukinistkaInventoryResponse> ListAsync(
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default )
    {
        if (BukinistkaJwtAuthentication.TryValidateCookie( httpRequest, _config ) is null)
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        List<KirmaBukinistkaOffer> acceptedOffers = await _db.KirmaBukinistkaOffers
            .AsNoTracking()
            .Where( x =>
                x.Status == KirmaBukinistkaOfferStatuses.Accepted
                && x.OdooProductId != null
                && x.OdooProductId > 0 )
            .OrderByDescending( x => x.CreatedAtUtc )
            .ThenByDescending( x => x.Id )
            .ToListAsync( cancellationToken );

        if (acceptedOffers.Count == 0)
        {
            return new BukinistkaInventoryResponse();
        }

        HashSet<int> productIds = acceptedOffers
            .Select( x => x.OdooProductId!.Value )
            .ToHashSet();

        Dictionary<int, int> acceptedByProduct = acceptedOffers
            .GroupBy( x => x.OdooProductId!.Value )
            .ToDictionary( g => g.Key, g => g.Sum( x => x.Quantity ) );

        Dictionary<int, int> wydanieByProduct = await _db.KirmaBukinistkaShopifyDeliverySyncs
            .AsNoTracking()
            .Where( x => productIds.Contains( x.OdooProductId ) && !x.IsCancelled )
            .GroupBy( x => x.OdooProductId )
            .Select( g => new { OdooProductId = g.Key, Qty = g.Sum( x => x.Quantity ) } )
            .ToDictionaryAsync( x => x.OdooProductId, x => x.Qty, cancellationToken );

        Dictionary<int, int> soldByProduct = await _db.KirmaBukinistkaPosSales
            .AsNoTracking()
            .Where( x =>
                productIds.Contains( x.OdooProductId )
                && !x.IsOwnStock
                && !x.IsReversed
                && !x.IsReturn
                && x.Quantity > 0 )
            .GroupBy( x => x.OdooProductId )
            .Select( g => new { OdooProductId = g.Key, Qty = g.Sum( x => x.Quantity ) } )
            .ToDictionaryAsync( x => x.OdooProductId, x => x.Qty, cancellationToken );

        Dictionary<int, KirmaBukinistkaOffer> latestOfferByProduct = new();
        foreach (KirmaBukinistkaOffer offer in acceptedOffers)
        {
            int productId = offer.OdooProductId!.Value;
            if (!latestOfferByProduct.ContainsKey( productId ))
            {
                latestOfferByProduct[productId] = offer;
            }
        }

        Dictionary<int, OdooProductListItem> odooById = new();
        try
        {
            OdooProductListResponse odooList =
                await _odooProducts.ListProductsAsync(
                    httpRequest,
                    cancellationToken: cancellationToken );
            foreach (OdooProductListItem item in odooList.Products)
            {
                if (productIds.Contains( item.Id ))
                {
                    odooById[item.Id] = item;
                }
            }
        }
        catch
        {
            // Inventory still works from local aggregations if Odoo list fails.
        }

        string odooBaseUrl = (_config["Odoo:BaseUrl"] ?? string.Empty).Trim().TrimEnd( '/' );

        List<BukinistkaInventoryRowDto> rows = new();
        foreach (int productId in productIds.OrderBy( id =>
                     latestOfferByProduct.TryGetValue( id, out KirmaBukinistkaOffer? o )
                         ? o.ProductName
                         : $"#{id}",
                     StringComparer.OrdinalIgnoreCase ))
        {
            KirmaBukinistkaOffer? offer = latestOfferByProduct.GetValueOrDefault( productId );
            int acceptedRaw = acceptedByProduct.GetValueOrDefault( productId );
            int wydanie = wydanieByProduct.GetValueOrDefault( productId );
            int acceptedNet = Math.Max( 0, acceptedRaw - wydanie );
            int sold = soldByProduct.GetValueOrDefault( productId );
            int paid = 0;
            int toPay = Math.Max( 0, sold - paid );
            // Remaining Kirma consignment at Bukinistka (not mixed with own stock).
            int inStock = Math.Max( 0, acceptedNet - sold );

            string name = !string.IsNullOrWhiteSpace( offer?.ProductName )
                ? offer!.ProductName.Trim()
                : odooById.GetValueOrDefault( productId )?.Name
                  ?? $"Odoo #{productId}";

            string odooUrl = odooById.GetValueOrDefault( productId )?.OdooUrl ?? string.Empty;
            if (string.IsNullOrWhiteSpace( odooUrl ) && !string.IsNullOrWhiteSpace( odooBaseUrl ))
            {
                odooUrl = $"{odooBaseUrl}/odoo/product.product/{productId}";
            }

            rows.Add( new BukinistkaInventoryRowDto
            {
                OdooProductId = productId,
                ProductName = name,
                MainImageUrl = string.IsNullOrWhiteSpace( offer?.MainImageUrl )
                    ? null
                    : offer!.MainImageUrl!.Trim(),
                OdooUrl = odooUrl,
                AcceptedQty = acceptedNet,
                QuantityInStock = inStock,
                SoldQty = sold,
                PaidQty = paid,
                QuantityToPay = toPay,
            } );
        }

        return new BukinistkaInventoryResponse { Rows = rows };
    }
}
