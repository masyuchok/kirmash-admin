using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace backend.Services.ImageFetch;

/// <summary>
/// Asks a configured relay (different egress) to download the image URL and return raw bytes.
/// Frontend never talks to the relay; credentials stay in backend config only.
/// </summary>
public sealed class RelayRemoteImageFetcher : IRemoteImageFetcher
{
    public const string HttpClientName = "ImageFetchRelay";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SupplierImageHostAllowlist _allowlist;
    private readonly ImageFetchOptions _options;
    private readonly ILogger<RelayRemoteImageFetcher> _logger;

    public RelayRemoteImageFetcher(
        IHttpClientFactory httpClientFactory,
        SupplierImageHostAllowlist allowlist,
        IOptions<ImageFetchOptions> options,
        ILogger<RelayRemoteImageFetcher> logger )
    {
        _httpClientFactory = httpClientFactory;
        _allowlist = allowlist;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace( _options.RelayUrl )
        && !string.IsNullOrWhiteSpace( _options.RelayToken );

    public async Task<RemoteImageFetchResult> FetchAsync(
        string imageUrl,
        string? pageUrl,
        CancellationToken cancellationToken )
    {
        if (!_allowlist.TryValidateAbsoluteHttpUrl( imageUrl, out _, out string urlError, pageUrl ))
        {
            return RemoteImageFetchResult.Fail( RemoteImageFetchSource.Relay, urlError );
        }

        if (!IsConfigured)
        {
            return RemoteImageFetchResult.Fail(
                RemoteImageFetchSource.Relay,
                "Image fetch relay is not configured (IMAGE_FETCH_RELAY_URL / IMAGE_FETCH_RELAY_TOKEN)." );
        }

        string baseUrl = _options.RelayUrl!.Trim().TrimEnd( '/' );
        string fetchUrl = baseUrl.EndsWith( "/fetch", StringComparison.OrdinalIgnoreCase )
            ? baseUrl
            : $"{baseUrl}/fetch";

        try
        {
            HttpClient client = _httpClientFactory.CreateClient( HttpClientName );
            using HttpRequestMessage request = new( HttpMethod.Post, fetchUrl );
            request.Headers.Authorization =
                new AuthenticationHeaderValue( "Bearer", _options.RelayToken!.Trim() );
            request.Content = JsonContent.Create(
                new RelayFetchRequest
                {
                    Url = imageUrl.Trim(),
                    Referer = string.IsNullOrWhiteSpace( pageUrl ) ? null : pageUrl.Trim(),
                } );

            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken );

            int status = (int)response.StatusCode;
            string? contentType = response.Content.Headers.ContentType?.MediaType;

            if (!response.IsSuccessStatusCode)
            {
                string bodyPeek = await ReadErrorBodyAsync( response, cancellationToken );
                return RemoteImageFetchResult.Fail(
                    RemoteImageFetchSource.Relay,
                    $"Relay HTTP {status}: {bodyPeek}",
                    status,
                    fetchUrl,
                    contentType,
                    response.ReasonPhrase );
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync( cancellationToken );
            string? validationError = RemoteImageContent.ValidateImageBytes(
                bytes,
                contentType,
                _options.MaxBytes );
            if (validationError is not null)
            {
                return RemoteImageFetchResult.Fail(
                    RemoteImageFetchSource.Relay,
                    $"Relay returned invalid image: {validationError}",
                    status,
                    fetchUrl,
                    contentType );
            }

            string mime = RemoteImageContent.GuessMimeType( bytes )
                ?? (contentType?.StartsWith( "image/", StringComparison.OrdinalIgnoreCase ) == true
                    ? contentType
                    : "image/jpeg");

            return RemoteImageFetchResult.Ok(
                RemoteImageFetchSource.Relay,
                bytes,
                mime,
                RemoteImageContent.GuessFileName( imageUrl, mime ),
                status,
                finalUrl: imageUrl );
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "Relay image fetch failed for {Url}", imageUrl );
            return RemoteImageFetchResult.Fail(
                RemoteImageFetchSource.Relay,
                ex.Message,
                exceptionType: ex.GetType().FullName );
        }
    }

    private static async Task<string> ReadErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken )
    {
        try
        {
            string text = await response.Content.ReadAsStringAsync( cancellationToken );
            if (string.IsNullOrWhiteSpace( text ))
            {
                return response.ReasonPhrase ?? "empty body";
            }

            return text.Length <= 300 ? text.Trim() : text.Trim()[..300] + "…";
        }
        catch
        {
            return response.ReasonPhrase ?? "unreadable error body";
        }
    }

    private sealed class RelayFetchRequest
    {
        [JsonPropertyName( "url" )]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName( "referer" )]
        public string? Referer { get; set; }
    }
}
