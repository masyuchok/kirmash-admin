using System.Text.Json;

namespace backend.Services.Odoo;

public sealed class OdooAuthService
{
    private readonly OdooJsonRpcClient _client;

    public OdooAuthService( OdooJsonRpcClient client )
    {
        _client = client;
    }

    public async Task<OdooSession> AuthenticateAsync( string login, string password )
    {
        if (string.IsNullOrWhiteSpace( login ))
        {
            throw new UnauthorizedAccessException( "Увядзіце логін Odoo." );
        }

        if (string.IsNullOrWhiteSpace( password ))
        {
            throw new UnauthorizedAccessException( "Увядзіце пароль Odoo." );
        }

        (JsonElement result, string? sessionIdFromCookie) =
            await _client.AuthenticateWebSessionAsync( login, password );
        int uid = result.GetProperty( "uid" ).GetInt32( );
        string database = result.TryGetProperty( "db", out JsonElement dbEl ) && dbEl.ValueKind == JsonValueKind.String
            ? dbEl.GetString( ) ?? string.Empty
            : string.Empty;
        string sessionId = result.TryGetProperty( "session_id", out JsonElement sessionEl )
                           && sessionEl.ValueKind == JsonValueKind.String
            ? sessionEl.GetString( ) ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace( sessionId ))
        {
            sessionId = sessionIdFromCookie ?? string.Empty;
        }
        string resolvedLogin = result.TryGetProperty( "username", out JsonElement usernameEl )
                               && usernameEl.ValueKind == JsonValueKind.String
            ? usernameEl.GetString( ) ?? login.Trim( )
            : login.Trim( );
        string? name = result.TryGetProperty( "name", out JsonElement nameEl ) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString( )
            : null;

        if (string.IsNullOrWhiteSpace( sessionId ))
        {
            throw new InvalidOperationException( "Odoo не вярнуў session_id пасля ўваходу." );
        }

        if (string.IsNullOrWhiteSpace( database ))
        {
            // Odoo Online sometimes omits db in JSON; use configured database.
            database = _client.ConfiguredDatabase;
        }

        return new OdooSession( database, uid, resolvedLogin, sessionId, name );
    }
}
