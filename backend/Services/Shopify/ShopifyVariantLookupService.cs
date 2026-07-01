using backend.Models;
using Microsoft.AspNetCore.Http;

namespace backend.Services.Shopify;

public class ShopifyVariantLookupService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes( 15 );
    private static (
        DateTime CachedAtUtc,
        Dictionary<string, string> TitleById,
        Dictionary<string, Dictionary<string, string>> IdByTitleByProduct,
        Dictionary<string, string> DefaultVariantIdByProduct,
        Dictionary<string, string> ProductTitleById,
        Dictionary<(string ProductId, string VariantId), string> VariantTitleByLine,
        Dictionary<(string ProductId, string VariantId), int> StockByLine)? _cache;
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
        (Dictionary<string, string> titleById, _, _, _, _, _) = await GetVariantCatalogMapsCachedAsync();
        return titleById;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetDefaultVariantIdByProductCachedAsync()
    {
        (_, _, Dictionary<string, string> defaultVariantIdByProduct, _, _, _) =
            await GetVariantCatalogMapsCachedAsync();
        return defaultVariantIdByProduct;
    }

    public async Task<IReadOnlyDictionary<string, Dictionary<string, string>>> GetVariantIdByProductTitleMapCachedAsync()
    {
        (_, Dictionary<string, Dictionary<string, string>> idByTitleByProduct, _, _, _, _) =
            await GetVariantCatalogMapsCachedAsync();
        return idByTitleByProduct;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetProductTitleByIdMapCachedAsync()
    {
        (_, _, _, Dictionary<string, string> productTitleById, _, _) = await GetVariantCatalogMapsCachedAsync();
        return productTitleById;
    }

    public async Task<IReadOnlyDictionary<(string ProductId, string VariantId), string>> GetVariantTitleByLineMapCachedAsync()
    {
        (_, _, _, _, Dictionary<(string ProductId, string VariantId), string> variantTitleByLine, _) =
            await GetVariantCatalogMapsCachedAsync();
        return variantTitleByLine;
    }

    public async Task<IReadOnlyDictionary<(string ProductId, string VariantId), int>> GetStockByLineMapCachedAsync()
    {
        (_, _, _, _, _, Dictionary<(string ProductId, string VariantId), int> stockByLine) =
            await GetVariantCatalogMapsCachedAsync();
        return stockByLine;
    }

    /// <summary>
    /// Shopify products with at least one named variant (not "Default Title").
    /// </summary>
    public async Task<IReadOnlySet<string>> GetMultiVariantProductIdsCachedAsync()
    {
        (_, Dictionary<string, Dictionary<string, string>> idByTitleByProduct, _, _, _, _) =
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

        if (titles.TryGetValue( title, out string? variantId ))
        {
            return variantId;
        }

        foreach (KeyValuePair<string, string> entry in titles)
        {
            if (VatReportHelpers.VariantTitlesEquivalentForPaymentMatch( title, entry.Key ))
            {
                return entry.Value;
            }
        }

        return string.Empty;
    }

    private async Task<(
        Dictionary<string, string> TitleById,
        Dictionary<string, Dictionary<string, string>> IdByTitleByProduct,
        Dictionary<string, string> DefaultVariantIdByProduct,
        Dictionary<string, string> ProductTitleById,
        Dictionary<(string ProductId, string VariantId), string> VariantTitleByLine,
        Dictionary<(string ProductId, string VariantId), int> StockByLine)> GetVariantCatalogMapsCachedAsync()
    {
        if (_cache is { } cached && DateTime.UtcNow - cached.CachedAtUtc < CacheLifetime)
        {
            return (
                cached.TitleById,
                cached.IdByTitleByProduct,
                cached.DefaultVariantIdByProduct,
                cached.ProductTitleById,
                cached.VariantTitleByLine,
                cached.StockByLine );
        }

        await CacheLock.WaitAsync();
        try
        {
            if (_cache is { } cachedAgain && DateTime.UtcNow - cachedAgain.CachedAtUtc < CacheLifetime)
            {
                return (
                    cachedAgain.TitleById,
                    cachedAgain.IdByTitleByProduct,
                    cachedAgain.DefaultVariantIdByProduct,
                    cachedAgain.ProductTitleById,
                    cachedAgain.VariantTitleByLine,
                    cachedAgain.StockByLine );
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
            Dictionary<string, string> defaultVariantIdByProduct = new( StringComparer.OrdinalIgnoreCase );
            Dictionary<string, string> productTitleById = new( StringComparer.OrdinalIgnoreCase );
            Dictionary<(string ProductId, string VariantId), string> variantTitleByLine =
                new( ProductVariantKeyComparer.Instance );
            Dictionary<(string ProductId, string VariantId), int> stockByLine =
                new( ProductVariantKeyComparer.Instance );

            foreach (ShopifyCatalogProduct product in catalogProducts)
            {
                string productId = ShopifyIds.NormalizeProductId( product.ProductId );
                if (string.IsNullOrWhiteSpace( productId ))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace( product.Title ))
                {
                    productTitleById[productId] = product.Title.Trim();
                }

                if (product.Variants.Count == 0)
                {
                    stockByLine[(productId, string.Empty)] = product.TotalInventory;
                    continue;
                }

                foreach (ProductVariantItem variant in product.Variants)
                {
                    string variantId = ShopifyIds.NormalizeVariantId( variant.VariantId );
                    string variantName = (variant.VariantName ?? string.Empty).Trim();
                    bool isDefaultTitle = string.Equals( variantName, "Default Title", StringComparison.OrdinalIgnoreCase );
                    if (string.IsNullOrWhiteSpace( variantId ))
                    {
                        continue;
                    }

                    stockByLine[(productId, variantId)] = variant.QuantityInStock;
                    variantTitleByLine[(productId, variantId)] = string.IsNullOrWhiteSpace( variantName )
                        ? "Default Title"
                        : variantName;

                    if (!defaultVariantIdByProduct.ContainsKey( productId ))
                    {
                        defaultVariantIdByProduct[productId] = variantId;
                    }

                    if (string.IsNullOrWhiteSpace( variantName ) || isDefaultTitle)
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

            _cache = (
                DateTime.UtcNow,
                titleById,
                idByTitleByProduct,
                defaultVariantIdByProduct,
                productTitleById,
                variantTitleByLine,
                stockByLine );
            return (
                titleById,
                idByTitleByProduct,
                defaultVariantIdByProduct,
                productTitleById,
                variantTitleByLine,
                stockByLine );
        }
        finally
        {
            CacheLock.Release();
        }
    }
}
