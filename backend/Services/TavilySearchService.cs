using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using backend.Models;
using Microsoft.Extensions.Caching.Memory;

namespace backend.Services;

public sealed class TavilySearchHit
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public sealed class TavilySearchService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<TavilySearchService> _logger;

    public TavilySearchService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<TavilySearchService> logger )
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TavilySearchHit>> SearchAsync(
        string query,
        IReadOnlyList<string>? includeDomains,
        int maxResults,
        CancellationToken cancellationToken )
    {
        string apiKey = (_config["Tavily:ApiKey"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( apiKey ))
        {
            throw new InvalidOperationException(
                "Tavily API key не наладжаны (Tavily:ApiKey / TAVILY_API_KEY)." );
        }

        string trimmedQuery = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( trimmedQuery ))
        {
            return Array.Empty<TavilySearchHit>();
        }

        Dictionary<string, object?> payload = new()
        {
            ["api_key"] = apiKey,
            ["query"] = trimmedQuery,
            ["search_depth"] = "basic",
            ["include_answer"] = false,
            ["max_results"] = Math.Clamp( maxResults, 1, 8 ),
        };

        if (includeDomains is { Count: > 0 })
        {
            payload["include_domains"] = includeDomains
                .Where( d => !string.IsNullOrWhiteSpace( d ) )
                .Select( d => d.Trim().ToLowerInvariant() )
                .Distinct( StringComparer.OrdinalIgnoreCase )
                .Take( 5 )
                .ToArray();
        }

        HttpClient client = _httpClientFactory.CreateClient( "Tavily" );
        using HttpRequestMessage request = new( HttpMethod.Post, "https://api.tavily.com/search" );
        request.Headers.Accept.Add( new MediaTypeWithQualityHeaderValue( "application/json" ) );
        request.Content = new StringContent(
            JsonSerializer.Serialize( payload ),
            Encoding.UTF8,
            "application/json" );

        using HttpResponseMessage response = await client.SendAsync( request, cancellationToken );
        string body = await response.Content.ReadAsStringAsync( cancellationToken );
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning( "Tavily search failed: {Status} {Body}", (int)response.StatusCode, body );
            throw new InvalidOperationException(
                $"Tavily API памылка: {(int)response.StatusCode}." );
        }

        List<TavilySearchHit> hits = new();
        using JsonDocument doc = JsonDocument.Parse( body );
        if (!doc.RootElement.TryGetProperty( "results", out JsonElement results )
            || results.ValueKind != JsonValueKind.Array)
        {
            return hits;
        }

        foreach (JsonElement item in results.EnumerateArray())
        {
            string url = item.TryGetProperty( "url", out JsonElement u )
                ? (u.GetString() ?? string.Empty).Trim()
                : string.Empty;
            if (string.IsNullOrWhiteSpace( url ) || !Uri.TryCreate( url, UriKind.Absolute, out _ ))
            {
                continue;
            }

            hits.Add( new TavilySearchHit
            {
                Title = item.TryGetProperty( "title", out JsonElement t )
                    ? (t.GetString() ?? string.Empty).Trim()
                    : string.Empty,
                Url = url,
                Content = item.TryGetProperty( "content", out JsonElement c )
                    ? (c.GetString() ?? string.Empty).Trim()
                    : string.Empty,
            } );
        }

        return hits;
    }
}

public sealed class BookLookupSessionState
{
    public string SessionId { get; init; } = string.Empty;
    public string QueryTitle { get; set; } = string.Empty;
    public string? QueryAuthor { get; set; }
    public string? QueryIsbn { get; set; }
    public int? SupplierId { get; set; }
    public string? SupplierName { get; set; }
    public List<string> SupplierDomains { get; set; } = new();
    public HashSet<string> ExcludeUrls { get; } = new( StringComparer.OrdinalIgnoreCase );
    public Queue<PendingSearchHit> Queue { get; } = new();
    public int SupplierSearchCalls { get; set; }
    public int WebSearchCalls { get; set; }
    public int SupplierQueryIndex { get; set; }
    public int WebQueryIndex { get; set; }
    public int PresentedHits { get; set; }
    public bool SupplierPhaseExhausted { get; set; }
    public byte[]? CoverImageBytes { get; set; }
    public string? CoverContentType { get; set; }
    public BookLookupCandidateDto? CandidateBeingShown { get; set; }

    /// <summary>Cached Google Lens hits for optional photo search button.</summary>
    public List<PendingSearchHit> PhotoLensHits { get; set; } = new();
    public bool PhotoLensFetched { get; set; }
    public bool PhotoSearchSupplierDone { get; set; }
    public bool PhotoSearchWebDone { get; set; }
}

public sealed class PendingSearchHit
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Source { get; set; } = "web";
}

public sealed class BookLookupSessionStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes( 30 );
    private readonly IMemoryCache _cache;

    public BookLookupSessionStore( IMemoryCache cache )
    {
        _cache = cache;
    }

    public void Save( BookLookupSessionState state )
    {
        _cache.Set( CacheKey( state.SessionId ), state, Ttl );
    }

    public bool TryGet( string sessionId, out BookLookupSessionState state )
    {
        if (_cache.TryGetValue( CacheKey( sessionId ), out BookLookupSessionState? found )
            && found is not null)
        {
            state = found;
            return true;
        }

        state = null!;
        return false;
    }

    public void Remove( string sessionId ) => _cache.Remove( CacheKey( sessionId ) );

    private static string CacheKey( string sessionId ) => $"book-lookup:{sessionId}";
}
