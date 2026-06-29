using backend.Models;
using Microsoft.AspNetCore.Http;

namespace backend.Services.Shopify;

public class ShopifyVariantLookupService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes( 15 );
    private static (DateTime CachedAtUtc, Dictionary<string, string> TitleById, Dictionary<string, Dictionary<string, string>> IdByTitleByProduct)? _cache;
    private static readonly SemaphoreSlim CacheLock = new( 1, 1 );

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ShopifyProductCatalogService _catalog;

    public ShopifyVariantLookupService(
        IHttpContextAccessor httpContextAccessor,
        ShopifyProductCatalogService catalog )
    {
        _httpContextAccessor = httpContextAccessor;
        _catalog = catalog;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetVariantTitleByIdMapCachedAsync()
    {
        (Dictionary<string, string> titleById, _) = await GetVariantCatalogMapsCachedAsync();
        return titleById;
    }

    public async Task<IReadOnlyDictionary<string, Dictionary<string, string>>> GetVariantIdByProductTitleMapCachedAsync()
    {
        (_, Dictionary<string, Dictionary<string, string>> idByTitleByProduct) =
            await GetVariantCatalogMapsCachedAsync();
        return idByTitleByProduct;
    }

    /// <summary>
    /// Shopify products with at least one named variant (not "Default Title").
    /// </summary>
    public async Task<IReadOnlySet<string>> GetMultiVariantProductIdsCachedAsync()
    {
        (_, Dictionary<string, Dictionary<string, string>> idByTitleByProduct) =
            await GetVariantCatalogMapsCachedAsync();
        HashSet<string> productIds = new( StringComparer.OrdinalIgnoreCase );
        foreach (KeyValuePair<string, Dictionary<string, string>> entry in idByTitleByProduct)
        {
            if (entry.Value.Count > 0)
            {
                productIds.Add( entry.Key );
            }
        }

        return productIds;
    }

    public static string ResolveVariantIdByProductTitle(
        string shopifyProductId,
        string variantTitle,
        IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle )
    {
        string productId = ShopifyIds.NormalizeProductId( shopifyProductId.Trim() );
        string title = (variantTitle ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( productId ) || string.IsNullOrWhiteSpace( title ))
        {
            return string.Empty;
        }

        if (!variantIdByTitle.TryGetValue( productId, out Dictionary<string, string>? titles ))
        {
            return string.Empty;
        }

        return titles.TryGetValue( title, out string? variantId ) ? variantId : string.Empty;
    }

    private async Task<(Dictionary<string, string> TitleById, Dictionary<string, Dictionary<string, string>> IdByTitleByProduct)> GetVariantCatalogMapsCachedAsync()
    {
        if (_cache is { } cached && DateTime.UtcNow - cached.CachedAtUtc < CacheLifetime)
        {
            return (cached.TitleById, cached.IdByTitleByProduct);
        }

        await CacheLock.WaitAsync();
        try
        {
            if (_cache is { } cachedAgain && DateTime.UtcNow - cachedAgain.CachedAtUtc < CacheLifetime)
            {
                return (cachedAgain.TitleById, cachedAgain.IdByTitleByProduct);
            }

            ShopifySession session = ShopifySessionReader.Require(
                _httpContextAccessor,
                "Няма Shopify-кантэксту для загрузкі варыянтаў."
            );

            List<ShopifyCatalogProduct> catalogProducts =
                await _catalog.FetchAllProductsAsync( session.Shop, session.AccessToken );

            Dictionary<string, string> titleById = new( StringComparer.OrdinalIgnoreCase );
            Dictionary<string, Dictionary<string, string>> idByTitleByProduct =
                new( StringComparer.OrdinalIgnoreCase );

            foreach (ShopifyCatalogProduct product in catalogProducts)
            {
                string productId = ShopifyIds.NormalizeProductId( product.ProductId );
                if (string.IsNullOrWhiteSpace( productId ))
                {
                    continue;
                }

                foreach (ProductVariantItem variant in product.Variants)
                {
                    string variantId = ShopifyIds.NormalizeVariantId( variant.VariantId );
                    string variantName = (variant.VariantName ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace( variantId ) ||
                        string.IsNullOrWhiteSpace( variantName ) ||
                        string.Equals( variantName, "Default Title", StringComparison.OrdinalIgnoreCase ))
                    {
                        continue;
                    }

                    titleById[variantId] = variantName;
                    if (!idByTitleByProduct.TryGetValue( productId, out Dictionary<string, string>? titles ))
                    {
                        titles = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
                        idByTitleByProduct[productId] = titles;
                    }

                    titles[variantName] = variantId;
                }
            }

            _cache = (DateTime.UtcNow, titleById, idByTitleByProduct);
            return (titleById, idByTitleByProduct);
        }
        finally
        {
            CacheLock.Release();
        }
    }
}
