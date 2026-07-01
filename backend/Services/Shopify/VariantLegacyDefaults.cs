namespace backend.Services.Shopify;

/// <summary>
/// Lines recorded before Shopify variants existed (empty variant id) are attributed to the
/// product's first named variant once variants appear in the catalog.
/// </summary>
public static class VariantLegacyDefaults
{
    public static Dictionary<string, string> BuildDefaultVariantIdByProduct(
        IEnumerable<Models.ProductWithSuppliersListItem>? catalog )
    {
        Dictionary<string, string> map = new( StringComparer.OrdinalIgnoreCase );
        if (catalog is null)
        {
            return map;
        }

        foreach (Models.ProductWithSuppliersListItem product in catalog)
        {
            if (product.Variants.Count == 0)
            {
                continue;
            }

            string productId = ShopifyIds.NormalizeProductId( product.ShopifyProductId );
            if (string.IsNullOrWhiteSpace( productId ) || map.ContainsKey( productId ))
            {
                continue;
            }

            foreach (Models.ProductVariantItem variant in product.Variants)
            {
                string variantId = ShopifyIds.NormalizeVariantId( variant.VariantId );
                string variantName = (variant.VariantName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace( variantId ) ||
                    string.IsNullOrWhiteSpace( variantName ) ||
                    string.Equals( variantName, "Default Title", StringComparison.OrdinalIgnoreCase ))
                {
                    continue;
                }

                map[productId] = variantId;
                break;
            }
        }

        return map;
    }

    public static int GetNamedVariantCount(
        string productId,
        IReadOnlyDictionary<string, Dictionary<string, string>>? variantIdByTitleByProduct )
    {
        string normalizedProductId = ShopifyIds.NormalizeProductId( productId );
        if (string.IsNullOrWhiteSpace( normalizedProductId ) || variantIdByTitleByProduct is null)
        {
            return 0;
        }

        return variantIdByTitleByProduct.TryGetValue( normalizedProductId, out Dictionary<string, string>? titles )
            ? titles.Count
            : 0;
    }

    /// <summary>
    /// True when the variant id is one of the product's named Shopify variants (not the legacy default).
    /// </summary>
    public static bool IsNamedCatalogVariantForProduct(
        string productId,
        string variantId,
        IReadOnlyDictionary<string, Dictionary<string, string>>? variantIdByTitleByProduct )
    {
        string normalizedProductId = ShopifyIds.NormalizeProductId( productId );
        string normalizedVariantId = ShopifyIds.NormalizeVariantId( variantId );
        if (string.IsNullOrWhiteSpace( normalizedProductId ) ||
            string.IsNullOrWhiteSpace( normalizedVariantId ) ||
            variantIdByTitleByProduct is null ||
            !variantIdByTitleByProduct.TryGetValue( normalizedProductId, out Dictionary<string, string>? titles ))
        {
            return false;
        }

        foreach (string namedVariantId in titles.Values)
        {
            if (string.Equals( namedVariantId, normalizedVariantId, StringComparison.OrdinalIgnoreCase ))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsDefaultVariantTitle( string? variantTitle )
    {
        string title = (variantTitle ?? string.Empty).Trim();
        return title.Length == 0 ||
               string.Equals( title, "Default Title", StringComparison.OrdinalIgnoreCase );
    }

    /// <summary>
    /// Sale line with no named variant attribution (empty/default title or legacy Shopify variant id).
    /// </summary>
    public static bool IsLegacyUnnamedSaleLine(
        string productId,
        string variantId,
        string? variantTitle,
        IReadOnlyDictionary<string, Dictionary<string, string>>? variantIdByTitleByProduct )
    {
        if (!IsDefaultVariantTitle( variantTitle ))
        {
            return false;
        }

        string normalizedVariantId = ShopifyIds.NormalizeVariantId( variantId );
        if (string.IsNullOrWhiteSpace( normalizedVariantId ))
        {
            return true;
        }

        return !IsNamedCatalogVariantForProduct( productId, normalizedVariantId, variantIdByTitleByProduct );
    }

    public static string ResolveVariantId(
        string productId,
        string variantId,
        IReadOnlyDictionary<string, string> defaultVariantByProduct,
        IReadOnlyDictionary<string, Dictionary<string, string>>? variantIdByTitleByProduct = null,
        IReadOnlyDictionary<string, string>? legacySaleVariantByProduct = null )
    {
        string normalizedProductId = ShopifyIds.NormalizeProductId( productId );
        string normalizedVariantId = ShopifyIds.NormalizeVariantId( variantId );
        if (!string.IsNullOrWhiteSpace( normalizedVariantId ))
        {
            if (variantIdByTitleByProduct is null)
            {
                return normalizedVariantId;
            }

            if (IsNamedCatalogVariantForProduct(
                    normalizedProductId,
                    normalizedVariantId,
                    variantIdByTitleByProduct ))
            {
                return normalizedVariantId;
            }

            int namedVariantCount = GetNamedVariantCount( normalizedProductId, variantIdByTitleByProduct );
            if (namedVariantCount <= 1 &&
                defaultVariantByProduct.TryGetValue( normalizedProductId, out string? canonicalVariantId ) &&
                !string.IsNullOrWhiteSpace( canonicalVariantId ))
            {
                return canonicalVariantId;
            }
        }

        if (IsLegacyUnnamedSaleLine(
                normalizedProductId,
                normalizedVariantId,
                null,
                variantIdByTitleByProduct ) &&
            legacySaleVariantByProduct is not null &&
            legacySaleVariantByProduct.TryGetValue( normalizedProductId, out string? legacyVariant ) &&
            !string.IsNullOrWhiteSpace( legacyVariant ))
        {
            return legacyVariant;
        }

        if (GetNamedVariantCount( normalizedProductId, variantIdByTitleByProduct ) > 1)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace( normalizedProductId ) &&
            defaultVariantByProduct.TryGetValue( normalizedProductId, out string? defaultVariantId ) &&
            !string.IsNullOrWhiteSpace( defaultVariantId ))
        {
            return defaultVariantId;
        }

        return string.Empty;
    }

    public static Dictionary<(string ProductId, string VariantId), int> RemapQuantityByLine(
        IReadOnlyDictionary<(string ProductId, string VariantId), int> source,
        IReadOnlyDictionary<string, string> defaultVariantByProduct )
    {
        Dictionary<(string ProductId, string VariantId), int> result =
            new( ProductVariantKeyComparer.Instance );
        foreach (KeyValuePair<(string ProductId, string VariantId), int> entry in source)
        {
            if (entry.Value <= 0)
            {
                continue;
            }

            string productId = ShopifyIds.NormalizeProductId( entry.Key.ProductId );
            string variantId = ResolveVariantId( productId, entry.Key.VariantId, defaultVariantByProduct );
            (string ProductId, string VariantId) key = (productId, variantId);
            result[key] = result.GetValueOrDefault( key ) + entry.Value;
        }

        return result;
    }
}
