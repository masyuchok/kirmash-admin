using Microsoft.Extensions.Options;

namespace backend.Services.ImageFetch;

/// <summary>
/// Downloads supplier images directly from this process's egress IP.
/// Follows redirects manually so every hop is allowlist-checked.
/// </summary>
public sealed class DirectRemoteImageFetcher : IRemoteImageFetcher
{
    public const string HttpClientName = "BookLookupImage";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SupplierImageHostAllowlist _allowlist;
    private readonly ImageFetchOptions _options;
    private readonly ILogger<DirectRemoteImageFetcher> _logger;

    public DirectRemoteImageFetcher(
        IHttpClientFactory httpClientFactory,
        SupplierImageHostAllowlist allowlist,
        IOptions<ImageFetchOptions> options,
        ILogger<DirectRemoteImageFetcher> logger )
    {
        _httpClientFactory = httpClientFactory;
        _allowlist = allowlist;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RemoteImageFetchResult> FetchAsync(
        string imageUrl,
        string? pageUrl,
        CancellationToken cancellationToken )
    {
        RemoteImageFetchResult last = RemoteImageFetchResult.Fail(
            RemoteImageFetchSource.Direct,
            "No download attempt." );

        foreach (string? referer in BuildRefererAttempts( imageUrl, pageUrl ))
        {
            RemoteImageFetchResult attempt = await FetchOnceAsync(
                imageUrl,
                pageUrl,
                referer,
                cancellationToken );
            last = attempt;
            if (attempt.Success)
            {
                return attempt;
            }
        }

        return last;
    }

    private static IEnumerable<string?> BuildRefererAttempts( string imageUrl, string? pageUrl )
    {
        List<string?> referers = new();
        if (!string.IsNullOrWhiteSpace( pageUrl ))
        {
            referers.Add( pageUrl.Trim() );
        }

        if (Uri.TryCreate( imageUrl, UriKind.Absolute, out Uri? imageUri ))
        {
            string siteRoot = $"{imageUri.Scheme}://{imageUri.Authority}/";
            if (!referers.Contains( siteRoot, StringComparer.OrdinalIgnoreCase ))
            {
                referers.Add( siteRoot );
            }
        }

        referers.Add( null );
        return referers;
    }

    private async Task<RemoteImageFetchResult> FetchOnceAsync(
        string imageUrl,
        string? pageUrl,
        string? refererUrl,
        CancellationToken cancellationToken )
    {
        if (!_allowlist.TryValidateAbsoluteHttpUrl(
                imageUrl,
                out Uri currentUri,
                out string urlError,
                pageUrl ))
        {
            return RemoteImageFetchResult.Fail( RemoteImageFetchSource.Direct, urlError );
        }

        (bool dnsOk, string? dnsError) =
            await _allowlist.ValidateResolvedAddressesAsync( currentUri, cancellationToken );
        if (!dnsOk)
        {
            return RemoteImageFetchResult.Fail( RemoteImageFetchSource.Direct, dnsError ?? "DNS blocked." );
        }

        HttpClient client = _httpClientFactory.CreateClient( HttpClientName );
        const int maxRedirects = 8;

        try
        {
            for (int hop = 0; hop <= maxRedirects; hop++)
            {
                using HttpRequestMessage request = new( HttpMethod.Get, currentUri );
                request.Version = System.Net.HttpVersion.Version11;
                request.VersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;

                if (!string.IsNullOrWhiteSpace( refererUrl )
                    && Uri.TryCreate( refererUrl.Trim(), UriKind.Absolute, out Uri? refererUri )
                    && string.Equals(
                        refererUri.Host,
                        currentUri.Host,
                        StringComparison.OrdinalIgnoreCase ))
                {
                    request.Headers.Referrer = refererUri;
                }

                using HttpResponseMessage response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken );

                int status = (int)response.StatusCode;
                string? contentType = response.Content.Headers.ContentType?.MediaType;
                long? contentLength = response.Content.Headers.ContentLength;
                string finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? currentUri.ToString();

                if (IsRedirect( response.StatusCode ))
                {
                    Uri? next = response.Headers.Location;
                    if (next is null)
                    {
                        return RemoteImageFetchResult.Fail(
                            RemoteImageFetchSource.Direct,
                            $"Redirect {status} without Location.",
                            status,
                            finalUrl,
                            contentType,
                            response.ReasonPhrase,
                            contentLength );
                    }

                    if (!next.IsAbsoluteUri)
                    {
                        next = new Uri( currentUri, next );
                    }

                    if (!_allowlist.TryValidateAbsoluteHttpUrl(
                            next.ToString(),
                            out Uri allowedNext,
                            out string redirectError,
                            pageUrl ))
                    {
                        return RemoteImageFetchResult.Fail(
                            RemoteImageFetchSource.Direct,
                            $"Redirect blocked: {redirectError}",
                            status,
                            next.ToString(),
                            contentType,
                            response.ReasonPhrase,
                            contentLength );
                    }

                    (bool nextDnsOk, string? nextDnsError) =
                        await _allowlist.ValidateResolvedAddressesAsync( allowedNext, cancellationToken );
                    if (!nextDnsOk)
                    {
                        return RemoteImageFetchResult.Fail(
                            RemoteImageFetchSource.Direct,
                            $"Redirect DNS blocked: {nextDnsError}",
                            status,
                            allowedNext.ToString() );
                    }

                    currentUri = allowedNext;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    byte[] peek = await ReadAtMostAsync( response.Content, 512, cancellationToken );
                    string refererNote = string.IsNullOrWhiteSpace( refererUrl )
                        ? "Referer=(none)"
                        : $"Referer={refererUrl}";
                    return RemoteImageFetchResult.Fail(
                        RemoteImageFetchSource.Direct,
                        $"HTTP {status} {response.ReasonPhrase}. {refererNote}, "
                        + $"ContentType={contentType ?? "(none)"}, "
                        + $"peekLooksHtml={RemoteImageContent.LooksLikeHtml( peek )}.",
                        status,
                        finalUrl,
                        contentType,
                        response.ReasonPhrase,
                        contentLength );
                }

                byte[] bytes = await response.Content.ReadAsByteArrayAsync( cancellationToken );
                string? validationError = RemoteImageContent.ValidateImageBytes(
                    bytes,
                    contentType,
                    _options.MaxBytes );
                if (validationError is not null)
                {
                    return RemoteImageFetchResult.Fail(
                        RemoteImageFetchSource.Direct,
                        validationError,
                        status,
                        finalUrl,
                        contentType,
                        response.ReasonPhrase,
                        contentLength );
                }

                string mime = RemoteImageContent.GuessMimeType( bytes )
                    ?? (contentType?.StartsWith( "image/", StringComparison.OrdinalIgnoreCase ) == true
                        ? contentType
                        : "image/jpeg");

                return RemoteImageFetchResult.Ok(
                    RemoteImageFetchSource.Direct,
                    bytes,
                    mime,
                    RemoteImageContent.GuessFileName( imageUrl, mime ),
                    status,
                    finalUrl,
                    contentLength );
            }

            return RemoteImageFetchResult.Fail(
                RemoteImageFetchSource.Direct,
                $"Too many redirects (max {maxRedirects}).",
                finalUrl: currentUri.ToString() );
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "Direct image download failed for {Url}", imageUrl );
            return RemoteImageFetchResult.Fail(
                RemoteImageFetchSource.Direct,
                ex.Message,
                exceptionType: ex.GetType().FullName );
        }
    }

    private static bool IsRedirect( System.Net.HttpStatusCode status ) =>
        status is System.Net.HttpStatusCode.MovedPermanently
            or System.Net.HttpStatusCode.Found
            or System.Net.HttpStatusCode.SeeOther
            or System.Net.HttpStatusCode.TemporaryRedirect
            or System.Net.HttpStatusCode.PermanentRedirect
            or (System.Net.HttpStatusCode)308
            or (System.Net.HttpStatusCode)307;

    private static async Task<byte[]> ReadAtMostAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken )
    {
        await using Stream stream = await content.ReadAsStreamAsync( cancellationToken );
        byte[] buffer = new byte[maxBytes];
        int read = await stream.ReadAsync( buffer.AsMemory( 0, maxBytes ), cancellationToken );
        if (read <= 0)
        {
            return Array.Empty<byte>();
        }

        if (read == maxBytes)
        {
            return buffer;
        }

        byte[] exact = new byte[read];
        Buffer.BlockCopy( buffer, 0, exact, 0, read );
        return exact;
    }
}
