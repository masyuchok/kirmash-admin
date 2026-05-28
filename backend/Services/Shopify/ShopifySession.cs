namespace backend.Services.Shopify;

public sealed record ShopifySession( string Shop, string AccessToken );

public static class ShopifySessionReader
{
    public static bool TryGet( IHttpContextAccessor httpContextAccessor, out ShopifySession session )
    {
        string? shop = httpContextAccessor.HttpContext?.User.FindFirst( "shop" )?.Value;
        string? accessToken = httpContextAccessor.HttpContext?.User.FindFirst( "access_token" )?.Value;
        if (string.IsNullOrWhiteSpace( shop ) || string.IsNullOrWhiteSpace( accessToken ))
        {
            session = null!;
            return false;
        }

        session = new ShopifySession( shop, accessToken );
        return true;
    }

    public static ShopifySession Require(
        IHttpContextAccessor httpContextAccessor,
        string missingContextMessage )
    {
        if (!TryGet( httpContextAccessor, out ShopifySession? session ))
        {
            throw new InvalidOperationException( missingContextMessage );
        }

        return session;
    }
}
