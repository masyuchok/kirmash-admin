using System.Net.Http.Headers;
using System.Text.Json;

namespace backend.Services;

public sealed class SerpApiLensHit
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Source { get; set; }
    public string? Thumbnail { get; set; }
}

/// <summary>
/// Google Lens via SerpAPI: upload cover photo, get visually similar pages
/// (same book cover, not an exact duplicate of the hand-held shot).
/// </summary>
public sealed class SerpApiGoogleLensService
{
    public const int MaxUploadBytes = 500 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<SerpApiGoogleLensService> _logger;

    public SerpApiGoogleLensService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<SerpApiGoogleLensService> logger )
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public bool IsConfigured()
    {
        return !string.IsNullOrWhiteSpace( GetApiKey() );
    }

    public async Task<IReadOnlyList<SerpApiLensHit>> SearchByImageAsync(
        byte[] imageBytes,
        string? contentType,
        CancellationToken cancellationToken )
    {
        string apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace( apiKey ))
        {
            throw new InvalidOperationException(
                "SerpAPI ключ не наладжаны (BookLookup:SerpApiKey / SERPAPI_API_KEY)." );
        }

        if (imageBytes is null || imageBytes.Length == 0)
        {
            throw new InvalidOperationException( "Няма фота вокладкі для пошуку." );
        }

        if (imageBytes.Length > MaxUploadBytes)
        {
            throw new InvalidOperationException(
                $"Фота для пошуку па вокладцы занадта вялікае (макс. {MaxUploadBytes / 1024} KB). Зрабіце здымак бліжэй або меншы файл." );
        }

        string imageId = await UploadImageAsync( apiKey, imageBytes, contentType, cancellationToken );
        return await SearchLensAsync( apiKey, imageId, cancellationToken );
    }

    private async Task<string> UploadImageAsync(
        string apiKey,
        byte[] imageBytes,
        string? contentType,
        CancellationToken cancellationToken )
    {
        HttpClient client = _httpClientFactory.CreateClient( "SerpApi" );
        using MultipartFormDataContent form = new();
        form.Add( new StringContent( apiKey ), "api_key" );

        string mime = string.IsNullOrWhiteSpace( contentType ) ? "image/jpeg" : contentType.Trim();
        string fileName = mime.Contains( "png", StringComparison.OrdinalIgnoreCase )
            ? "cover.png"
            : mime.Contains( "webp", StringComparison.OrdinalIgnoreCase )
                ? "cover.webp"
                : "cover.jpg";

        ByteArrayContent fileContent = new( imageBytes );
        fileContent.Headers.ContentType = new MediaTypeHeaderValue( mime );
        form.Add( fileContent, "image", fileName );

        using HttpResponseMessage response = await client.PostAsync(
            "https://serpapi.com/image",
            form,
            cancellationToken );
        string body = await response.Content.ReadAsStringAsync( cancellationToken );
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning( "SerpAPI image upload failed: {Status} {Body}", (int)response.StatusCode, body );
            throw new InvalidOperationException( $"SerpAPI upload памылка: {(int)response.StatusCode}." );
        }

        using JsonDocument doc = JsonDocument.Parse( body );
        if (doc.RootElement.TryGetProperty( "error", out JsonElement err ))
        {
            throw new InvalidOperationException( $"SerpAPI: {err.GetString()}" );
        }

        string? imageId = doc.RootElement.TryGetProperty( "image_id", out JsonElement idEl )
            ? idEl.GetString()
            : null;
        if (string.IsNullOrWhiteSpace( imageId ))
        {
            throw new InvalidOperationException( "SerpAPI не вярнуў image_id." );
        }

        return imageId;
    }

    private async Task<IReadOnlyList<SerpApiLensHit>> SearchLensAsync(
        string apiKey,
        string imageId,
        CancellationToken cancellationToken )
    {
        HttpClient client = _httpClientFactory.CreateClient( "SerpApi" );
        string url =
            "https://serpapi.com/search.json"
            + $"?engine=google_lens"
            + $"&api_key={Uri.EscapeDataString( apiKey )}"
            + $"&image_id={Uri.EscapeDataString( imageId )}"
            + "&type=visual_matches"
            + "&hl=be"
            + "&auto_crop=true";

        using HttpResponseMessage response = await client.GetAsync( url, cancellationToken );
        string body = await response.Content.ReadAsStringAsync( cancellationToken );
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning( "SerpAPI Lens failed: {Status} {Body}", (int)response.StatusCode, body );
            throw new InvalidOperationException( $"SerpAPI Lens памылка: {(int)response.StatusCode}." );
        }

        List<SerpApiLensHit> hits = new();
        using JsonDocument doc = JsonDocument.Parse( body );
        if (doc.RootElement.TryGetProperty( "error", out JsonElement err ))
        {
            throw new InvalidOperationException( $"SerpAPI: {err.GetString()}" );
        }

        AppendMatches( doc.RootElement, "visual_matches", hits );
        AppendMatches( doc.RootElement, "exact_matches", hits );

        // Dedupe by URL
        Dictionary<string, SerpApiLensHit> byUrl = new( StringComparer.OrdinalIgnoreCase );
        foreach (SerpApiLensHit hit in hits)
        {
            if (string.IsNullOrWhiteSpace( hit.Url ) || !Uri.TryCreate( hit.Url, UriKind.Absolute, out _ ))
            {
                continue;
            }

            if (!byUrl.ContainsKey( hit.Url ))
            {
                byUrl[hit.Url] = hit;
            }
        }

        _logger.LogInformation( "SerpAPI Lens returned {Count} unique URLs", byUrl.Count );
        return byUrl.Values.ToList();
    }

    private static void AppendMatches( JsonElement root, string property, List<SerpApiLensHit> hits )
    {
        if (!root.TryGetProperty( property, out JsonElement arr ) || arr.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement item in arr.EnumerateArray())
        {
            string link = item.TryGetProperty( "link", out JsonElement linkEl )
                ? (linkEl.GetString() ?? string.Empty).Trim()
                : string.Empty;
            if (string.IsNullOrWhiteSpace( link ))
            {
                continue;
            }

            hits.Add( new SerpApiLensHit
            {
                Title = item.TryGetProperty( "title", out JsonElement titleEl )
                    ? (titleEl.GetString() ?? string.Empty).Trim()
                    : string.Empty,
                Url = link,
                Source = item.TryGetProperty( "source", out JsonElement srcEl )
                    ? srcEl.GetString()
                    : null,
                Thumbnail = item.TryGetProperty( "thumbnail", out JsonElement thEl )
                    ? thEl.GetString()
                    : null,
            } );
        }
    }

    private string GetApiKey()
    {
        return (_config["BookLookup:SerpApiKey"]
            ?? _config["SerpApi:ApiKey"]
            ?? string.Empty).Trim();
    }
}
