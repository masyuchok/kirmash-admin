using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder( args );

string relayToken = Environment.GetEnvironmentVariable( "IMAGE_FETCH_RELAY_TOKEN" )
    ?? builder.Configuration["RelayToken"]
    ?? string.Empty;

HashSet<string> allowedHosts = new(
    (builder.Configuration.GetSection( "ImageAllowedHosts" ).Get<string[]>() ?? [])
        .Where( h => !string.IsNullOrWhiteSpace( h ) )
        .Select( h => h.Trim().ToLowerInvariant() ),
    StringComparer.OrdinalIgnoreCase );

int maxBytes = builder.Configuration.GetValue( "MaxBytes", 8 * 1024 * 1024 );

builder.Services.AddHttpClient( "RelayDownload", client =>
{
    client.Timeout = TimeSpan.FromSeconds( 25 );
    client.DefaultRequestVersion = HttpVersion.Version11;
    client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36" );
    client.DefaultRequestHeaders.Accept.ParseAdd(
        "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8" );
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd( "be,ru;q=0.9,en;q=0.8" );
} )
.ConfigurePrimaryHttpMessageHandler( () => new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    AllowAutoRedirect = false,
    ConnectTimeout = TimeSpan.FromSeconds( 10 ),
} );

var app = builder.Build();

if (string.IsNullOrWhiteSpace( relayToken ))
{
    app.Logger.LogWarning(
        "IMAGE_FETCH_RELAY_TOKEN / RelayToken is empty — all /fetch requests will be rejected." );
}

app.MapGet( "/health", () => Results.Ok( new { ok = true, service = "image-fetch-relay" } ) );

app.MapPost( "/fetch", async Task<IResult> (
    HttpRequest httpRequest,
    RelayFetchBody body,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken ) =>
{
    ILogger logger = loggerFactory.CreateLogger( "Fetch" );

    string? auth = httpRequest.Headers.Authorization.FirstOrDefault();
    if (string.IsNullOrWhiteSpace( relayToken )
        || string.IsNullOrWhiteSpace( auth )
        || !auth.StartsWith( "Bearer ", StringComparison.OrdinalIgnoreCase )
        || !string.Equals( auth["Bearer ".Length..].Trim(), relayToken, StringComparison.Ordinal ))
    {
        return Results.Json(
            new { error = "Unauthorized" },
            statusCode: StatusCodes.Status401Unauthorized );
    }

    string? imageUrl = body.Url?.Trim();
    if (string.IsNullOrWhiteSpace( imageUrl ))
    {
        return Results.Json( new { error = "url is required" }, statusCode: 400 );
    }

    if (!TryValidateUrl( imageUrl, allowedHosts, out Uri currentUri, out string urlError ))
    {
        return Results.Json( new { error = urlError }, statusCode: 400 );
    }

    (bool dnsOk, string? dnsError) = await ValidateDnsAsync( currentUri, cancellationToken );
    if (!dnsOk)
    {
        return Results.Json( new { error = dnsError }, statusCode: 400 );
    }

    string? referer = string.IsNullOrWhiteSpace( body.Referer ) ? null : body.Referer.Trim();
    HttpClient client = httpClientFactory.CreateClient( "RelayDownload" );

    try
    {
        for (int hop = 0; hop <= 8; hop++)
        {
            using HttpRequestMessage request = new( HttpMethod.Get, currentUri );
            if (!string.IsNullOrWhiteSpace( referer )
                && Uri.TryCreate( referer, UriKind.Absolute, out Uri? refererUri )
                && string.Equals( refererUri.Host, currentUri.Host, StringComparison.OrdinalIgnoreCase ))
            {
                request.Headers.Referrer = refererUri;
            }

            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken );

            if (IsRedirect( response.StatusCode ))
            {
                Uri? next = response.Headers.Location;
                if (next is null)
                {
                    return Results.Json(
                        new { error = $"Redirect {(int)response.StatusCode} without Location" },
                        statusCode: 502 );
                }

                if (!next.IsAbsoluteUri)
                {
                    next = new Uri( currentUri, next );
                }

                if (!TryValidateUrl(
                        next.ToString(),
                        allowedHosts,
                        out Uri allowedNext,
                        out string redirectError ))
                {
                    return Results.Json(
                        new { error = $"Redirect blocked: {redirectError}" },
                        statusCode: 400 );
                }

                (bool nextDnsOk, string? nextDnsError) =
                    await ValidateDnsAsync( allowedNext, cancellationToken );
                if (!nextDnsOk)
                {
                    return Results.Json( new { error = nextDnsError }, statusCode: 400 );
                }

                currentUri = allowedNext;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Upstream {Status} for {Url}",
                    (int)response.StatusCode,
                    currentUri );
                return Results.Json(
                    new
                    {
                        error = $"Upstream HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                        statusCode = (int)response.StatusCode,
                    },
                    statusCode: 502 );
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync( cancellationToken );
            string? contentType = response.Content.Headers.ContentType?.MediaType;
            string? validation = ValidateImage( bytes, contentType, maxBytes );
            if (validation is not null)
            {
                return Results.Json( new { error = validation }, statusCode: 502 );
            }

            string mime = GuessMime( bytes )
                ?? (contentType?.StartsWith( "image/", StringComparison.OrdinalIgnoreCase ) == true
                    ? contentType
                    : "image/jpeg");

            logger.LogInformation(
                "Fetched {Url} bytes={ByteLength} mime={Mime}",
                imageUrl,
                bytes.Length,
                mime );

            return Results.File( bytes, mime );
        }

        return Results.Json( new { error = "Too many redirects" }, statusCode: 502 );
    }
    catch (Exception ex)
    {
        logger.LogWarning( ex, "Fetch failed for {Url}", imageUrl );
        return Results.Json( new { error = ex.Message }, statusCode: 502 );
    }
} );

app.Run();

static bool IsRedirect( HttpStatusCode status ) =>
    status is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect
        or (HttpStatusCode)307
        or (HttpStatusCode)308;

static bool TryValidateUrl(
    string url,
    HashSet<string> allowedHosts,
    out Uri uri,
    out string error )
{
    uri = null!;
    error = string.Empty;
    if (!Uri.TryCreate( url, UriKind.Absolute, out Uri? parsed )
        || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
    {
        error = "URL is not absolute http(s).";
        return false;
    }

    if (parsed.IsLoopback
        || string.Equals( parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase ))
    {
        error = "Host is loopback/localhost.";
        return false;
    }

    if (allowedHosts.Count > 0
        && !allowedHosts.Contains( parsed.Host.Trim().TrimEnd( '.' ).ToLowerInvariant() ))
    {
        error = $"Host '{parsed.Host}' is not allowlisted.";
        return false;
    }

    if (IPAddress.TryParse( parsed.Host, out IPAddress? ip ) && IsBlockedIp( ip ))
    {
        error = "Host is a blocked IP literal.";
        return false;
    }

    uri = parsed;
    return true;
}

static async Task<(bool Ok, string? Error)> ValidateDnsAsync(
    Uri uri,
    CancellationToken cancellationToken )
{
    if (IPAddress.TryParse( uri.Host, out IPAddress? literal ))
    {
        return IsBlockedIp( literal )
            ? (false, "Host is a blocked IP literal.")
            : (true, null);
    }

    try
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync( uri.Host, cancellationToken );
        foreach (IPAddress address in addresses)
        {
            if (IsBlockedIp( address ))
            {
                return (false, $"Host resolves to blocked address {address}.");
            }
        }

        return (true, null);
    }
    catch (Exception ex) when (ex is SocketException or ArgumentException)
    {
        return (false, $"DNS failed: {ex.Message}");
    }
}

static bool IsBlockedIp( IPAddress address )
{
    if (IPAddress.IsLoopback( address )) return true;
    if (address.AddressFamily == AddressFamily.InterNetwork)
    {
        byte[] b = address.GetAddressBytes();
        if (b[0] == 10) return true;
        if (b[0] == 127) return true;
        if (b[0] == 169 && b[1] == 254) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 0) return true;
    }

    if (address.AddressFamily == AddressFamily.InterNetworkV6)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal)
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsBlockedIp( address.MapToIPv4() );
        }
    }

    return false;
}

static string? ValidateImage( byte[] bytes, string? contentType, int maxBytes )
{
    if (bytes.Length == 0) return "Empty body.";
    if (bytes.Length > maxBytes) return $"Body too large ({bytes.Length}).";
    if (!string.IsNullOrWhiteSpace( contentType )
        && contentType.Contains( "text/html", StringComparison.OrdinalIgnoreCase ))
    {
        return "Content-Type is text/html.";
    }

    if (LooksLikeHtml( bytes )) return "Body looks like HTML.";
    if (GuessMime( bytes ) is null
        && (string.IsNullOrWhiteSpace( contentType )
            || !contentType.StartsWith( "image/", StringComparison.OrdinalIgnoreCase )))
    {
        return $"Not an image (Content-Type={contentType ?? "none"}).";
    }

    return null;
}

static bool LooksLikeHtml( byte[] bytes )
{
    int len = Math.Min( bytes.Length, 64 );
    if (len < 15) return false;
    string head = Encoding.ASCII.GetString( bytes, 0, len ).TrimStart().ToLowerInvariant();
    return head.StartsWith( "<!doctype", StringComparison.Ordinal )
        || head.StartsWith( "<html", StringComparison.Ordinal );
}

static string? GuessMime( byte[] bytes )
{
    if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        return "image/jpeg";
    if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        return "image/png";
    if (bytes.Length >= 12
        && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
        && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        return "image/webp";
    return null;
}

sealed class RelayFetchBody
{
    [JsonPropertyName( "url" )]
    public string? Url { get; set; }

    [JsonPropertyName( "referer" )]
    public string? Referer { get; set; }
}
