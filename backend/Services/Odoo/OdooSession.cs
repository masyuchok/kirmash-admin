namespace backend.Services.Odoo;

public sealed record OdooSession(
    string Database,
    int Uid,
    string Login,
    string SessionId,
    string? Name );

public static class OdooSessionReader
{
    public static bool TryGet( IHttpContextAccessor httpContextAccessor, out OdooSession session )
    {
        session = null!;
        System.Security.Claims.ClaimsPrincipal? user = httpContextAccessor.HttpContext?.User;
        return user is not null && TryGetFromPrincipal( user, out session );
    }

    public static bool TryGetFromPrincipal(
        System.Security.Claims.ClaimsPrincipal user,
        out OdooSession session )
    {
        session = null!;

        string? org = user.FindFirst( "org" )?.Value;
        if (!string.Equals( org, "bukinistka", StringComparison.OrdinalIgnoreCase ))
        {
            return false;
        }

        string? database = user.FindFirst( "odoo_db" )?.Value;
        string? uidRaw = user.FindFirst( "odoo_uid" )?.Value;
        string? login = user.FindFirst( "odoo_login" )?.Value
            ?? user.FindFirst( System.Security.Claims.ClaimTypes.NameIdentifier )?.Value
            ?? user.FindFirst( "sub" )?.Value;
        string? sessionId = user.FindFirst( "access_token" )?.Value;
        string? name = user.FindFirst( "odoo_name" )?.Value;

        if (string.IsNullOrWhiteSpace( database )
            || string.IsNullOrWhiteSpace( login )
            || string.IsNullOrWhiteSpace( sessionId )
            || !int.TryParse( uidRaw, out int uid )
            || uid <= 0)
        {
            return false;
        }

        session = new OdooSession( database, uid, login, sessionId, name );
        return true;
    }
}
