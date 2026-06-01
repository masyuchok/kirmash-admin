namespace backend.Services.Shopify;

public static class ShopifyIds
{
    public static string NormalizeGid( string id, string prefix )
    {
        if (string.IsNullOrWhiteSpace( id )) return string.Empty;
        return id.StartsWith( prefix, StringComparison.OrdinalIgnoreCase )
            ? id[prefix.Length..]
            : id.Trim();
    }

    public static string NormalizeOrderId( string id ) => NormalizeGid( id, "gid://shopify/Order/" );

    public static string NormalizeProductId( string id ) => NormalizeGid( id, "gid://shopify/Product/" );

    public static string NormalizeVariantId( string id ) => NormalizeGid( id, "gid://shopify/ProductVariant/" );

    public static long? TryParseNumericProductId( string raw )
    {
        if (string.IsNullOrWhiteSpace( raw )) return null;
        if (long.TryParse( raw, out long direct )) return direct;

        const string prefix = "gid://shopify/Product/";
        if (raw.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ))
        {
            string part = raw[prefix.Length..];
            return long.TryParse( part, out long gidId ) ? gidId : null;
        }

        return null;
    }

    public static long? TryParseNumericVariantId( string raw )
    {
        if (string.IsNullOrWhiteSpace( raw )) return null;
        if (long.TryParse( raw, out long direct )) return direct;

        const string prefix = "gid://shopify/ProductVariant/";
        if (raw.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ))
        {
            string part = raw[prefix.Length..];
            return long.TryParse( part, out long gidId ) ? gidId : null;
        }

        return TryParseNumericProductId( raw );
    }
}
