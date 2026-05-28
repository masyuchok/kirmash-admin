namespace backend.Services.Shopify;

public static class ShopifyApi
{
    public const string Version = "2024-10";

    public static string RestUrl( string shop, string path ) =>
        $"https://{shop}/admin/api/{Version}/{path.TrimStart( '/' )}";

    public static string GraphQlUrl( string shop ) => RestUrl( shop, "graphql.json" );
}
