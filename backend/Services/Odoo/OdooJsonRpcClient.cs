using System.Text;
using System.Text.Json;

namespace backend.Services.Odoo;

public sealed class OdooJsonRpcClient
{
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;

    public OdooJsonRpcClient( IConfiguration config, IHttpClientFactory httpClientFactory )
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<(JsonElement Result, string? SessionIdFromCookie)> AuthenticateWebSessionAsync(
        string login,
        string password,
        CancellationToken cancellationToken = default )
    {
        string database = RequireDatabase( );
        (JsonDocument response, string? sessionIdFromCookie) = await PostJsonRpcAsync(
            "/web/session/authenticate",
            new Dictionary<string, object?>
            {
                ["db"] = database,
                ["login"] = login.Trim( ),
                ["password"] = password,
            },
            sessionId: null,
            cancellationToken );

        using (response)
        {
            JsonElement root = response.RootElement;
            if (TryGetRpcError( root, out string? errorMessage ))
            {
                throw new UnauthorizedAccessException( errorMessage );
            }

            if (!root.TryGetProperty( "result", out JsonElement result ))
            {
                throw new InvalidOperationException( "Odoo вярнуў нечаканы адказ на ўваход." );
            }

            if (result.ValueKind == JsonValueKind.False
                || (result.TryGetProperty( "uid", out JsonElement uidEl )
                    && uidEl.ValueKind == JsonValueKind.False))
            {
                throw new UnauthorizedAccessException( "Няверны логін або пароль Odoo." );
            }

            if (!result.TryGetProperty( "uid", out JsonElement uidProperty )
                || uidProperty.ValueKind != JsonValueKind.Number
                || uidProperty.GetInt32() <= 0)
            {
                throw new UnauthorizedAccessException( "Няверны логін або пароль Odoo." );
            }

            // Clone: JsonElement is invalid after JsonDocument is disposed.
            return (result.Clone(), sessionIdFromCookie);
        }
    }

    public async Task<JsonElement> CallKwAsync(
        OdooSession session,
        string model,
        string method,
        object[] args,
        Dictionary<string, object?>? kwargs = null,
        CancellationToken cancellationToken = default )
    {
        (JsonDocument response, _) = await PostJsonRpcAsync(
            "/web/dataset/call_kw",
            new Dictionary<string, object?>
            {
                ["model"] = model,
                ["method"] = method,
                ["args"] = args,
                ["kwargs"] = kwargs ?? new Dictionary<string, object?>(),
            },
            session.SessionId,
            cancellationToken );

        using (response)
        {
            JsonElement root = response.RootElement;
            if (TryGetRpcError( root, out string? errorMessage ))
            {
                throw new InvalidOperationException( errorMessage );
            }

            if (!root.TryGetProperty( "result", out JsonElement result ))
            {
                throw new InvalidOperationException( "Odoo вярнуў нечаканы адказ." );
            }

            return result.Clone();
        }
    }

    private async Task<(JsonDocument Document, string? SessionIdFromCookie)> PostJsonRpcAsync(
        string path,
        Dictionary<string, object?> parameters,
        string? sessionId,
        CancellationToken cancellationToken )
    {
        string baseUrl = RequireBaseUrl( );
        string normalizedPath = path.StartsWith( "/", StringComparison.Ordinal ) ? path : $"/{path}";
        object payload = new
        {
            jsonrpc = "2.0",
            method = "call",
            @params = parameters,
            id = Random.Shared.Next( 1, int.MaxValue ),
        };

        string json = JsonSerializer.Serialize( payload );
        HttpClient client = _httpClientFactory.CreateClient( "Odoo" );
        using HttpRequestMessage request = new( HttpMethod.Post, $"{baseUrl}{normalizedPath}" );
        request.Content = new StringContent( json, Encoding.UTF8, "application/json" );
        if (!string.IsNullOrWhiteSpace( sessionId ))
        {
            request.Headers.Add( "Cookie", $"session_id={sessionId}" );
        }

        using HttpResponseMessage response = await client.SendAsync( request, cancellationToken );
        string body = await response.Content.ReadAsStringAsync( cancellationToken );
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Запыт да Odoo не ўдаўся ({(int)response.StatusCode}): {TrimBody( body )}" );
        }

        string? sessionIdFromCookie = TryReadSessionIdFromCookies( response );
        try
        {
            return (JsonDocument.Parse( body ), sessionIdFromCookie);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException( $"Odoo вярнуў не-JSON адказ: {TrimBody( body )}", ex );
        }
    }

    private static string? TryReadSessionIdFromCookies( HttpResponseMessage response )
    {
        if (!response.Headers.TryGetValues( "Set-Cookie", out IEnumerable<string>? cookies ))
        {
            return null;
        }

        foreach (string cookie in cookies)
        {
            foreach (string part in cookie.Split( ';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries ))
            {
                if (!part.StartsWith( "session_id=", StringComparison.OrdinalIgnoreCase ))
                {
                    continue;
                }

                string value = part["session_id=".Length..].Trim( );
                if (!string.IsNullOrWhiteSpace( value ))
                {
                    return value;
                }
            }
        }

        return null;
    }

    public string ConfiguredDatabase => RequireDatabase();

    private string RequireBaseUrl()
    {
        string? baseUrl = _config["Odoo:BaseUrl"]?.Trim( ).TrimEnd( '/' );
        if (string.IsNullOrWhiteSpace( baseUrl ))
        {
            throw new InvalidOperationException( "Odoo:BaseUrl is not configured." );
        }

        return baseUrl;
    }

    private string RequireDatabase()
    {
        string? database = _config["Odoo:Database"]?.Trim( );
        if (string.IsNullOrWhiteSpace( database ))
        {
            throw new InvalidOperationException( "Odoo:Database is not configured." );
        }

        return database;
    }

    private static bool TryGetRpcError( JsonElement root, out string message )
    {
        if (!root.TryGetProperty( "error", out JsonElement error ))
        {
            message = string.Empty;
            return false;
        }

        if (error.TryGetProperty( "data", out JsonElement data )
            && data.TryGetProperty( "message", out JsonElement dataMessage )
            && dataMessage.ValueKind == JsonValueKind.String)
        {
            message = dataMessage.GetString( ) ?? "Памылка Odoo.";
            return true;
        }

        if (error.TryGetProperty( "message", out JsonElement errorMessage )
            && errorMessage.ValueKind == JsonValueKind.String)
        {
            message = errorMessage.GetString( ) ?? "Памылка Odoo.";
            return true;
        }

        message = "Памылка Odoo.";
        return true;
    }

    private static string TrimBody( string body ) =>
        body.Length <= 300 ? body : $"{body[..300]}...";
}
