using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using backend.Data;
using backend.Models;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public sealed class BookLookupService
{
    private const int MaxFileBytes = 8 * 1024 * 1024;
    private const int DefaultMaxSupplierSearches = 4;
    private const int DefaultMaxWebSearches = 3;
    private const int DefaultMaxPresentedHits = 6;
    private const int ResultsPerSearch = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<BookLookupService> _logger;
    private readonly TavilySearchService _tavily;
    private readonly SerpApiGoogleLensService _lens;
    private readonly BookLookupSessionStore _sessions;
    private readonly AppDbContext _db;
    private readonly ShopifyInventoryService _shopifyInventory;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public BookLookupService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<BookLookupService> logger,
        TavilySearchService tavily,
        SerpApiGoogleLensService lens,
        BookLookupSessionStore sessions,
        AppDbContext db,
        ShopifyInventoryService shopifyInventory,
        IHttpContextAccessor httpContextAccessor )
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
        _tavily = tavily;
        _lens = lens;
        _sessions = sessions;
        _db = db;
        _shopifyInventory = shopifyInventory;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<BookLookupStepResultDto> StartFromPhotoAsync(
        IFormFile cover,
        IFormFile? isbnPhoto,
        int? supplierId,
        CancellationToken cancellationToken )
    {
        ValidateImage( cover, "cover" );
        if (isbnPhoto is not null)
        {
            ValidateImage( isbnPhoto, "isbnPhoto" );
        }

        byte[] coverBytes = await ReadAllBytesAsync( cover, cancellationToken );
        byte[]? isbnBytes = isbnPhoto is null
            ? null
            : await ReadAllBytesAsync( isbnPhoto, cancellationToken );

        BookVisionExtract extracted;
        string? ocrWarning = null;
        try
        {
            extracted = await ExtractFromImagesAsync(
                coverBytes,
                cover.ContentType,
                isbnBytes,
                isbnPhoto?.ContentType,
                cancellationToken );
        }
        catch (Exception ex)
        {
            // Groq 503/over-capacity must not block product creation — open manual OCR form.
            _logger.LogWarning( ex, "Vision OCR failed; opening manual confirm form" );
            extracted = new BookVisionExtract();
            ocrWarning =
                "Не ўдалося аўтаматычна прачытаць вокладку (Groq часова недаступны). " +
                "Увядзіце назву / аўтара ўручную і націсніце «Так, шукаць».";
        }

        if (string.IsNullOrWhiteSpace( extracted.Title ) && string.IsNullOrWhiteSpace( ocrWarning ))
        {
            throw new InvalidOperationException(
                "Не ўдалося прачытаць назву кнігі з фота. Паспрабуйце іншае фота або ўвядзіце назву ўручную." );
        }

        BookLookupSessionState session = await CreateSessionAsync(
            extracted.Title ?? string.Empty,
            extracted.Author,
            extracted.Isbn,
            supplierId,
            coverBytes,
            string.IsNullOrWhiteSpace( cover.ContentType ) ? "image/jpeg" : cover.ContentType,
            cancellationToken );

        return new BookLookupStepResultDto
        {
            SessionId = session.SessionId,
            Candidate = null,
            QueryTitle = session.QueryTitle,
            QueryAuthor = session.QueryAuthor,
            QueryIsbn = session.QueryIsbn,
            AttemptsUsed = 0,
            AttemptsMax = MaxAttemptsBudget( session ),
            Done = false,
            AwaitingOcrConfirm = true,
            Message = ocrWarning,
        };
    }

    public async Task<BookLookupStepResultDto> ConfirmOcrAndSearchAsync(
        string sessionId,
        BookLookupConfirmOcrRequest request,
        CancellationToken cancellationToken )
    {
        if (!_sessions.TryGet( sessionId, out BookLookupSessionState session ))
        {
            throw new InvalidOperationException( "Сесія пошуку не знойдзена або скончылася. Пачніце зноў." );
        }

        string title = (request.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( title ))
        {
            throw new InvalidOperationException( "Укажыце назву кнігі." );
        }

        session.QueryTitle = title;
        session.QueryAuthor = string.IsNullOrWhiteSpace( request.Author )
            ? null
            : request.Author.Trim();
        session.QueryIsbn = IsbnUtil.Normalize( request.Isbn )
            ?? (string.IsNullOrWhiteSpace( request.Isbn ) ? null : request.Isbn.Trim());
        session.Queue.Clear();
        session.ExcludeUrls.Clear();
        session.SupplierSearchCalls = 0;
        session.WebSearchCalls = 0;
        session.SupplierQueryIndex = 0;
        session.WebQueryIndex = 0;
        session.PresentedHits = 0;
        session.CandidateBeingShown = null;
        session.CatalogPrefillDone = false;
        session.SupplierCatalogBlocked = false;
        session.SupplierPhaseExhausted = session.SupplierDomains.Count == 0;
        _sessions.Save( session );

        return await PresentNextAsync( session, cancellationToken );
    }

    public async Task<BookLookupStepResultDto> StartFromTextAsync(
        BookLookupFromTextRequest request,
        CancellationToken cancellationToken )
    {
        string title = (request.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( title ))
        {
            throw new InvalidOperationException( "Укажыце назву кнігі." );
        }

        string? isbn = IsbnUtil.Normalize( request.Isbn );
        string? author = string.IsNullOrWhiteSpace( request.Author ) ? null : request.Author.Trim();
        BookLookupSessionState session = await CreateSessionAsync(
            title,
            author,
            isbn,
            request.SupplierId,
            coverBytes: null,
            coverContentType: null,
            cancellationToken );

        return await PresentNextAsync( session, cancellationToken );
    }

    public async Task<BookLookupStepResultDto> NextAsync(
        string sessionId,
        CancellationToken cancellationToken )
    {
        if (!_sessions.TryGet( sessionId, out BookLookupSessionState session ))
        {
            throw new InvalidOperationException( "Сесія пошуку не знойдзена або скончылася. Пачніце зноў." );
        }

        if (session.CandidateBeingShown is not null)
        {
            session.ExcludeUrls.Add( NormalizeUrlKey( session.CandidateBeingShown.Url ) );
            session.CandidateBeingShown = null;
        }

        return await PresentNextAsync( session, cancellationToken );
    }

    /// <summary>
    /// Optional button: Google Lens by cover — supplier domains first, then once internet.
    /// </summary>
    public async Task<BookLookupStepResultDto> SearchByPhotoAsync(
        string sessionId,
        CancellationToken cancellationToken )
    {
        if (!_sessions.TryGet( sessionId, out BookLookupSessionState session ))
        {
            throw new InvalidOperationException( "Сесія пошуку не знойдзена або скончылася. Пачніце зноў." );
        }

        if (session.CoverImageBytes is null || session.CoverImageBytes.Length == 0)
        {
            throw new InvalidOperationException( "Няма фота вокладкі ў сесіі. Пачніце з фота." );
        }

        if (!_lens.IsConfigured())
        {
            throw new InvalidOperationException(
                "Пошук па фота не наладжаны (BookLookup:SerpApiKey / SERPAPI_API_KEY)." );
        }

        if (session.PhotoSearchSupplierDone && session.PhotoSearchWebDone)
        {
            return EnrichFlags( session, new BookLookupStepResultDto
            {
                SessionId = session.SessionId,
                Candidate = null,
                QueryTitle = session.QueryTitle,
                QueryAuthor = session.QueryAuthor,
                QueryIsbn = session.QueryIsbn,
                AttemptsUsed = session.PresentedHits,
                AttemptsMax = MaxAttemptsBudget( session ),
                Done = true,
                Message = "Пошук па фота ўжо выкарыстаны. Увядзіце спасылку ўручную або даныя кнігі.",
            } );
        }

        if (session.CandidateBeingShown is not null)
        {
            session.ExcludeUrls.Add( NormalizeUrlKey( session.CandidateBeingShown.Url ) );
            session.CandidateBeingShown = null;
        }

        session.Queue.Clear();

        if (!session.PhotoLensFetched)
        {
            IReadOnlyList<SerpApiLensHit> lensHits = await _lens.SearchByImageAsync(
                session.CoverImageBytes,
                session.CoverContentType,
                cancellationToken );
            session.PhotoLensHits = lensHits
                .Select( h => new PendingSearchHit
                {
                    Title = h.Title,
                    Url = h.Url,
                    Content = h.Source ?? string.Empty,
                    Source = "image",
                } )
                .ToList();
            session.PhotoLensFetched = true;
            _logger.LogInformation(
                "Photo search cached {Count} Lens hits for session {SessionId}",
                session.PhotoLensHits.Count,
                session.SessionId );
        }

        // First click: supplier domains only. Do not silently fall through to the open web
        // in the same request — that is why Facebook/kirma appeared as the "first" photo hit.
        if (!session.PhotoSearchSupplierDone)
        {
            session.PhotoSearchSupplierDone = true;
            if (session.SupplierDomains.Count > 0)
            {
                IEnumerable<PendingSearchHit> supplierHits = session.PhotoLensHits
                    .Where( h => IsSupplierHost( h.Url, session.SupplierDomains ) );
                EnqueueHits(
                    session,
                    supplierHits.Select( h => new TavilySearchHit
                    {
                        Title = h.Title,
                        Url = h.Url,
                        Content = h.Content,
                    } ).ToList(),
                    "image" );
                _sessions.Save( session );
                if (session.Queue.Count > 0)
                {
                    return await PresentNextAsync( session, cancellationToken );
                }

                _sessions.Save( session );
                return EnrichFlags( session, new BookLookupStepResultDto
                {
                    SessionId = session.SessionId,
                    Candidate = null,
                    QueryTitle = session.QueryTitle,
                    QueryAuthor = session.QueryAuthor,
                    QueryIsbn = session.QueryIsbn,
                    AttemptsUsed = session.PresentedHits,
                    AttemptsMax = MaxAttemptsBudget( session ),
                    Done = true,
                    Message =
                        "На сайце пастаўшчыка па фота вокладкі нічога не знойдзена. Націсніце «Шукаць па фота» яшчэ раз для пошуку ў інтэрнэце, або ўвядзіце спасылку.",
                } );
            }
        }

        if (!session.PhotoSearchWebDone)
        {
            session.PhotoSearchWebDone = true;
            IEnumerable<PendingSearchHit> webHits = session.SupplierDomains.Count > 0
                ? session.PhotoLensHits.Where( h => !IsSupplierHost( h.Url, session.SupplierDomains ) )
                : session.PhotoLensHits;
            EnqueueHits(
                session,
                webHits.Select( h => new TavilySearchHit
                {
                    Title = h.Title,
                    Url = h.Url,
                    Content = h.Content,
                } ).ToList(),
                "image" );
            _sessions.Save( session );
            if (session.Queue.Count > 0)
            {
                return await PresentNextAsync( session, cancellationToken );
            }
        }

        _sessions.Save( session );
        return EnrichFlags( session, new BookLookupStepResultDto
        {
            SessionId = session.SessionId,
            Candidate = null,
            QueryTitle = session.QueryTitle,
            QueryAuthor = session.QueryAuthor,
            QueryIsbn = session.QueryIsbn,
            AttemptsUsed = session.PresentedHits,
            AttemptsMax = MaxAttemptsBudget( session ),
            Done = true,
            Message = "Па фота вокладкі кнігу не знойдзена. Увядзіце спасылку ўручную або даныя кнігі.",
        } );
    }

    /// <summary>
    /// Manual URL: pull title/author from the page into a candidate for the create form.
    /// When Cloudflare blocks our server, falls back to Tavily search/extract, then URL slug.
    /// </summary>
    public async Task<BookLookupCandidateDto> ImportFromUrlAsync(
        string? sessionId,
        string url,
        CancellationToken cancellationToken )
    {
        _ = sessionId;
        string trimmed = (url ?? string.Empty).Trim();
        if (!Uri.TryCreate( trimmed, UriKind.Absolute, out Uri? uri )
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException( "Укажыце карэктную http(s) спасылку." );
        }

        string cleanUrl = CleanProductUrl( uri );

        // 1) WooCommerce / WordPress product API for this exact slug —
        // same title/author/ISBN as on the product page (HTML is Cloudflare-blocked;
        // Tavily Extract also fails to fetch Kamunikat).
        BookLookupCandidateDto? fromStore = await TryImportUrlViaWooStoreAsync(
            cleanUrl,
            cancellationToken );
        if (fromStore is not null && !string.IsNullOrWhiteSpace( fromStore.Title ))
        {
            fromStore.Source = "manual";
            fromStore.Url = cleanUrl;
            return fromStore;
        }

        // 2) Tavily Extract — read page HTML via Tavily when the shop allows it.
        BookLookupCandidateDto? fromExtract = await TryImportUrlViaExtractAsync(
            cleanUrl,
            cancellationToken );
        if (fromExtract is not null && !string.IsNullOrWhiteSpace( fromExtract.Title ))
        {
            fromExtract.Source = "manual";
            fromExtract.Url = cleanUrl;
            return fromExtract;
        }

        // 3) Tavily Search index — title/snippet when extract is empty.
        BookLookupCandidateDto? fromIndex = await TryImportUrlViaIndexerAsync(
            cleanUrl,
            cancellationToken );
        if (fromIndex is not null && !string.IsNullOrWhiteSpace( fromIndex.Title ))
        {
            fromIndex.Source = "manual";
            fromIndex.Url = cleanUrl;
            return fromIndex;
        }

        // 3b) Broader web search (book often cited on FB/news with Cyrillic title).
        BookLookupCandidateDto? fromWeb = await TryImportUrlViaWebMentionsAsync(
            cleanUrl,
            cancellationToken );
        if (fromWeb is not null && !string.IsNullOrWhiteSpace( fromWeb.Title ))
        {
            fromWeb.Source = "manual";
            fromWeb.Url = cleanUrl;
            return fromWeb;
        }

        // 4) Direct HTML (non-Cloudflare shops).
        try
        {
            PageTitleHints pageHints = await TryFetchPageTitleHintsAsync(
                cleanUrl,
                cancellationToken );
            string? directTitle = FirstNonEmpty(
                pageHints.H1,
                pageHints.OgTitle,
                pageHints.HtmlTitle );
            if (!string.IsNullOrWhiteSpace( directTitle )
                && !LooksLikeBotWallTitle( directTitle ))
            {
                (string titleOnly, string? authorFromTitle) = SplitAuthorFromTitle(
                    directTitle,
                    pageHints.AuthorHint );
                string cleaned = CleanBookTitle(
                    string.IsNullOrWhiteSpace( titleOnly ) ? directTitle : titleOnly );
                if (!string.IsNullOrWhiteSpace( cleaned ))
                {
                    _logger.LogInformation(
                        "Book lookup from-url via direct HTML: {Url} → {Title}",
                        cleanUrl,
                        cleaned );
                    return new BookLookupCandidateDto
                    {
                        Title = cleaned,
                        Author = authorFromTitle ?? pageHints.AuthorHint,
                        Url = cleanUrl,
                        Source = "manual",
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "from-url direct HTML failed for {Url}", cleanUrl );
        }

        // No URL-slug guessing: user requires fields from the shop page itself.
        throw new InvalidOperationException(
            "Не ўдалося прачытаць назву са старонкі крамы (Cloudflare). Паспрабуйце яшчэ раз або ўвядзіце даныя ўручную." );
    }

    private async Task<BookLookupCandidateDto?> TryImportUrlViaWooStoreAsync(
        string cleanUrl,
        CancellationToken cancellationToken )
    {
        if (!Uri.TryCreate( cleanUrl, UriKind.Absolute, out Uri? uri ))
        {
            return null;
        }

        string? slug = TryGetProductSlugFromPath( uri.AbsolutePath );
        if (string.IsNullOrWhiteSpace( slug ))
        {
            return null;
        }

        string host = uri.Host.Trim().TrimEnd( '.' );
        string[] endpoints =
        {
            $"https://{host}/wp-json/wc/store/v1/products?slug={Uri.EscapeDataString( slug )}",
            $"https://{host}/wp-json/wc/store/products?slug={Uri.EscapeDataString( slug )}",
            $"https://{host}/wp-json/wp/v2/product?slug={Uri.EscapeDataString( slug )}",
        };

        HttpClient client = _httpClientFactory.CreateClient( "BookLookupJson" );
        foreach (string endpoint in endpoints)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(
                    endpoint,
                    cancellationToken );
                int status = (int)response.StatusCode;
                string body = await response.Content.ReadAsStringAsync( cancellationToken );
                string? mediaType = response.Content.Headers.ContentType?.MediaType;

                if (status is 403 or 503
                    || LooksLikeCloudflareOrHtmlPayload( body, mediaType ))
                {
                    _logger.LogWarning(
                        "from-url Woo/WP store blocked or HTML challenge: {Url} → {Status} ct={ContentType}",
                        endpoint,
                        status,
                        mediaType ?? "?" );
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "from-url Woo/WP store {Url} → {Status}",
                        endpoint,
                        status );
                    continue;
                }

                BookLookupCandidateDto? parsed = TryParseWooOrWpProductJson( body, cleanUrl );
                if (parsed is not null && !string.IsNullOrWhiteSpace( parsed.Title ))
                {
                    _logger.LogInformation(
                        "Book lookup from-url via shop API: {Url} → {Title} / {Author}",
                        cleanUrl,
                        parsed.Title,
                        parsed.Author );
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning( ex, "from-url Woo/WP store failed for {Url}", endpoint );
            }
        }

        // Direct shop API blocked (Cloudflare on our IP) — ask Tavily to fetch the JSON URL.
        foreach (string endpoint in endpoints)
        {
            try
            {
                TavilyExtractResult? extracted = await _tavily.ExtractAsync(
                    endpoint,
                    cancellationToken );
                if (extracted is null || string.IsNullOrWhiteSpace( extracted.RawContent ))
                {
                    continue;
                }

                string raw = extracted.RawContent.Trim();
                // Extract may wrap JSON in markdown fences or prose — pull the JSON array/object.
                string? json = ExtractJsonPayload( raw );
                if (string.IsNullOrWhiteSpace( json ))
                {
                    continue;
                }

                BookLookupCandidateDto? parsed = TryParseWooOrWpProductJson( json, cleanUrl );
                if (parsed is not null && !string.IsNullOrWhiteSpace( parsed.Title ))
                {
                    _logger.LogInformation(
                        "Book lookup from-url via Tavily→shop API: {Url} → {Title} / {Author}",
                        cleanUrl,
                        parsed.Title,
                        parsed.Author );
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "from-url Tavily shop API extract failed for {Url}",
                    endpoint );
            }
        }

        return null;
    }

    private static bool LooksLikeCloudflareOrHtmlPayload( string body, string? mediaType )
    {
        if (!string.IsNullOrWhiteSpace( mediaType )
            && mediaType.Contains( "html", StringComparison.OrdinalIgnoreCase ))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace( body ))
        {
            return true;
        }

        string trim = body.TrimStart();
        if (trim.StartsWith( '<' )
            || trim.Contains( "Just a moment", StringComparison.OrdinalIgnoreCase )
            || trim.Contains( "cf-browser-verification", StringComparison.OrdinalIgnoreCase )
            || trim.Contains( "Attention Required", StringComparison.OrdinalIgnoreCase ))
        {
            return true;
        }

        return false;
    }

    private static string? ExtractJsonPayload( string raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string t = raw.Trim();
        // ```json ... ```
        Match fence = Regex.Match(
            t,
            @"```(?:json)?\s*(?<j>[\s\S]*?)```",
            RegexOptions.IgnoreCase );
        if (fence.Success)
        {
            t = fence.Groups["j"].Value.Trim();
        }

        int arr = t.IndexOf( '[', StringComparison.Ordinal );
        int obj = t.IndexOf( '{', StringComparison.Ordinal );
        int start = -1;
        if (arr >= 0 && (obj < 0 || arr < obj))
        {
            start = arr;
        }
        else if (obj >= 0)
        {
            start = obj;
        }

        if (start < 0)
        {
            return null;
        }

        t = t[start..];
        // Prefer full array/object if balanced enough; otherwise return as-is for JsonDocument.
        return t.Trim();
    }

    private static BookLookupCandidateDto? TryParseWooOrWpProductJson( string body, string cleanUrl )
    {
        if (string.IsNullOrWhiteSpace( body )
            || LooksLikeCloudflareOrHtmlPayload( body, null ))
        {
            return null;
        }

        try
        {
            return ParseWooOrWpProductJson( body, cleanUrl );
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BookLookupCandidateDto? ParseWooOrWpProductJson( string body, string cleanUrl )
    {
        if (string.IsNullOrWhiteSpace( body ))
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse( body );
        JsonElement root = doc.RootElement;
        JsonElement product = root;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0)
            {
                return null;
            }

            product = root[0];
        }
        else if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // WC Store: "name"; WP REST: title.rendered
        string? name = null;
        if (product.TryGetProperty( "name", out JsonElement nameEl )
            && nameEl.ValueKind == JsonValueKind.String)
        {
            name = DecodeHtml( nameEl.GetString() );
        }
        else if (product.TryGetProperty( "title", out JsonElement titleEl )
            && titleEl.ValueKind == JsonValueKind.Object
            && titleEl.TryGetProperty( "rendered", out JsonElement rendered ))
        {
            name = DecodeHtml( rendered.GetString() );
        }

        if (string.IsNullOrWhiteSpace( name ) || LooksLikeBotWallTitle( name ))
        {
            return null;
        }

        string? authorAttr = null;
        string? isbnAttr = null;
        string? publisherAttr = null;
        if (product.TryGetProperty( "attributes", out JsonElement attrs )
            && attrs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement attr in attrs.EnumerateArray())
            {
                string taxonomy = attr.TryGetProperty( "taxonomy", out JsonElement taxEl )
                    ? (taxEl.GetString() ?? string.Empty)
                    : string.Empty;
                string attrName = attr.TryGetProperty( "name", out JsonElement anEl )
                    ? (anEl.GetString() ?? string.Empty)
                    : string.Empty;
                string? termName = FirstWooAttributeTermName( attr );
                if (string.IsNullOrWhiteSpace( termName ))
                {
                    continue;
                }

                if (taxonomy.Contains( "autar", StringComparison.OrdinalIgnoreCase )
                    || taxonomy.Contains( "author", StringComparison.OrdinalIgnoreCase )
                    || attrName.Contains( "Аўтар", StringComparison.OrdinalIgnoreCase )
                    || attrName.Contains( "Автор", StringComparison.OrdinalIgnoreCase )
                    || attrName.Equals( "Author", StringComparison.OrdinalIgnoreCase ))
                {
                    authorAttr ??= termName;
                }
                else if (taxonomy.Contains( "isbn", StringComparison.OrdinalIgnoreCase )
                    || attrName.Contains( "ISBN", StringComparison.OrdinalIgnoreCase ))
                {
                    isbnAttr ??= termName;
                }
                else if (taxonomy.Contains( "vydav", StringComparison.OrdinalIgnoreCase )
                    || taxonomy.Contains( "publisher", StringComparison.OrdinalIgnoreCase )
                    || attrName.Contains( "Выдавец", StringComparison.OrdinalIgnoreCase )
                    || attrName.Contains( "Издател", StringComparison.OrdinalIgnoreCase )
                    || attrName.Contains( "Publisher", StringComparison.OrdinalIgnoreCase ))
                {
                    publisherAttr ??= termName;
                }
            }
        }

        (string titleOnly, string? authorFromTitle) = SplitAuthorFromTitle( name, authorAttr );
        string cleaned = CleanBookTitle(
            string.IsNullOrWhiteSpace( titleOnly ) ? name : titleOnly );
        if (string.IsNullOrWhiteSpace( cleaned ))
        {
            return null;
        }

        string? isbn = IsbnUtil.Normalize( isbnAttr ) ?? ExtractIsbnFromText( name );
        string? author = authorAttr ?? authorFromTitle;

        return new BookLookupCandidateDto
        {
            Title = cleaned,
            Author = author,
            Isbn = isbn,
            Publisher = publisherAttr,
            Url = cleanUrl,
            Source = "manual",
            Snippet = TruncateSnippet( name ),
        };
    }

    private static string? FirstWooAttributeTermName( JsonElement attr )
    {
        if (attr.TryGetProperty( "terms", out JsonElement terms )
            && terms.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement term in terms.EnumerateArray())
            {
                if (term.TryGetProperty( "name", out JsonElement n )
                    && n.ValueKind == JsonValueKind.String)
                {
                    string? v = DecodeHtml( n.GetString() );
                    if (!string.IsNullOrWhiteSpace( v ))
                    {
                        return v.Trim();
                    }
                }
            }
        }

        // Some payloads put options as string array.
        if (attr.TryGetProperty( "options", out JsonElement options )
            && options.ValueKind == JsonValueKind.Array
            && options.GetArrayLength() > 0
            && options[0].ValueKind == JsonValueKind.String)
        {
            return DecodeHtml( options[0].GetString() )?.Trim();
        }

        return null;
    }

    private static string CleanProductUrl( Uri uri )
    {
        // Drop tracking/?v= noise so matching and extract work on the canonical product URL.
        var builder = new UriBuilder( uri )
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };
        string path = builder.Path.TrimEnd( '/' );
        builder.Path = path.Length == 0 ? "/" : path + "/";
        return builder.Uri.ToString();
    }

    private static bool LooksLikeBotWallTitle( string? title )
    {
        if (string.IsNullOrWhiteSpace( title ))
        {
            return true;
        }

        string t = title.Trim();
        return t.Contains( "Just a moment", StringComparison.OrdinalIgnoreCase )
            || t.Contains( "Attention Required", StringComparison.OrdinalIgnoreCase )
            || t.Contains( "Cloudflare", StringComparison.OrdinalIgnoreCase )
            || string.Equals( t, "403", StringComparison.Ordinal )
            || string.Equals( t, "404", StringComparison.Ordinal );
    }

    private async Task<BookLookupCandidateDto?> TryImportUrlViaIndexerAsync(
        string cleanUrl,
        CancellationToken cancellationToken )
    {
        if (!Uri.TryCreate( cleanUrl, UriKind.Absolute, out Uri? uri ))
        {
            return null;
        }

        string? slug = TryGetProductSlugFromPath( uri.AbsolutePath );
        if (string.IsNullOrWhiteSpace( slug ) || slug.Length < 6)
        {
            return null;
        }

        string apex = StripWww( uri.Host );
        string query = $"site:{apex} {slug.Replace( '-', ' ' )}";
        try
        {
            IReadOnlyList<TavilySearchHit> hits = await _tavily.SearchAsync(
                query,
                includeDomains: new[] { apex, "www." + apex },
                ResultsPerSearch,
                cancellationToken );

            TavilySearchHit? best = hits
                .OrderByDescending( h =>
                {
                    int score = 0;
                    string key = NormalizeUrlKey( h.Url );
                    if (key.Contains( slug, StringComparison.OrdinalIgnoreCase ))
                    {
                        score += 100;
                    }

                    if (NormalizeUrlKey( cleanUrl ) == key)
                    {
                        score += 50;
                    }

                    if (!string.IsNullOrWhiteSpace( h.Title ))
                    {
                        score += 10;
                    }

                    return score;
                } )
                .FirstOrDefault( h =>
                    !string.IsNullOrWhiteSpace( h.Title )
                    && !LooksLikeBotWallTitle( h.Title ) );

            if (best is null)
            {
                return null;
            }

            (string titleOnly, string? author) = SplitAuthorFromTitle( best.Title, null );
            string title = CleanBookTitle(
                string.IsNullOrWhiteSpace( titleOnly ) ? best.Title : titleOnly );
            if (string.IsNullOrWhiteSpace( title ))
            {
                return null;
            }

            _logger.LogInformation(
                "Book lookup from-url via indexer: {Url} → {Title}",
                cleanUrl,
                title );
            return new BookLookupCandidateDto
            {
                Title = title,
                Author = author,
                Url = cleanUrl,
                Source = "manual",
                Snippet = TruncateSnippet( best.Content ),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "from-url indexer search failed for {Url}", cleanUrl );
            return null;
        }
    }

    /// <summary>
    /// When the shop itself is Cloudflare-blocked, Tavily still finds Cyrillic titles
    /// on Facebook / news that cite the same book (often with the product slug or ISBN).
    /// </summary>
    private async Task<BookLookupCandidateDto?> TryImportUrlViaWebMentionsAsync(
        string cleanUrl,
        CancellationToken cancellationToken )
    {
        if (!Uri.TryCreate( cleanUrl, UriKind.Absolute, out Uri? uri ))
        {
            return null;
        }

        string? slug = TryGetProductSlugFromPath( uri.AbsolutePath );
        if (string.IsNullOrWhiteSpace( slug ) || slug.Length < 6)
        {
            return null;
        }

        string cyrillicPhrase = BelarusianLacinka.SlugToPhrase( slug );
        string latinPhrase = slug.Replace( '-', ' ' );
        string[] queries =
        {
            $"{cyrillicPhrase} kamunikat",
            $"{latinPhrase} kamunikat",
            cleanUrl,
        };

        try
        {
            foreach (string query in queries)
            {
                if (string.IsNullOrWhiteSpace( query ) || query.Length < 8)
                {
                    continue;
                }

                IReadOnlyList<TavilySearchHit> hits = await _tavily.SearchAsync(
                    query,
                    includeDomains: null,
                    ResultsPerSearch,
                    cancellationToken );

                TavilySearchHit? best = hits
                    .Where( h =>
                        !string.IsNullOrWhiteSpace( h.Title )
                        && !LooksLikeBotWallTitle( h.Title )
                        && ContainsCyrillic( h.Title ) )
                    .OrderByDescending( h =>
                    {
                        int score = 0;
                        string blob = $"{h.Title}\n{h.Url}\n{h.Content}";
                        if (blob.Contains( slug, StringComparison.OrdinalIgnoreCase )
                            || blob.Contains( "kamunikat", StringComparison.OrdinalIgnoreCase ))
                        {
                            score += 50;
                        }

                        if (SoftTitlePhraseContainedIn( h.Title, cyrillicPhrase )
                            || SoftTitlePhraseContainedIn( cyrillicPhrase, h.Title ))
                        {
                            score += 40;
                        }

                        if (ContainsCyrillic( h.Title ))
                        {
                            score += 20;
                        }

                        return score;
                    } )
                    .FirstOrDefault();

                if (best is null)
                {
                    continue;
                }

                (string titleOnly, string? author) = SplitAuthorFromTitle( best.Title, null );
                string title = CleanBookTitle(
                    string.IsNullOrWhiteSpace( titleOnly ) ? best.Title : titleOnly );
                if (string.IsNullOrWhiteSpace( title ) || !ContainsCyrillic( title ))
                {
                    continue;
                }

                _logger.LogInformation(
                    "Book lookup from-url via web mentions: {Url} → {Title}",
                    cleanUrl,
                    title );
                return new BookLookupCandidateDto
                {
                    Title = title,
                    Author = author,
                    Url = cleanUrl,
                    Source = "manual",
                    Isbn = ExtractIsbnFromText( $"{best.Title}\n{best.Content}" ),
                    Snippet = TruncateSnippet( best.Content ),
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "from-url web mentions failed for {Url}", cleanUrl );
        }

        return null;
    }

    private static bool ContainsCyrillic( string? text )
    {
        if (string.IsNullOrEmpty( text ))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c is (>= '\u0400' and <= '\u04FF'))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<BookLookupCandidateDto?> TryImportUrlViaExtractAsync(
        string cleanUrl,
        CancellationToken cancellationToken )
    {
        try
        {
            TavilyExtractResult? extracted = await _tavily.ExtractAsync( cleanUrl, cancellationToken );
            if (extracted is null || string.IsNullOrWhiteSpace( extracted.RawContent ))
            {
                return null;
            }

            string raw = extracted.RawContent;
            if (LooksLikeBotWallTitle( raw[..Math.Min( raw.Length, 200 )] ))
            {
                _logger.LogWarning( "Tavily extract returned bot-wall content for {Url}", cleanUrl );
                return null;
            }

            string plain = StripHtmlTags( raw );
            string? title = ExtractMetaContent( raw, "og:title" )
                ?? ExtractHtmlTagText( raw, "h1" )
                ?? ExtractMarkdownHeading( raw )
                ?? ExtractHtmlTagText( raw, "title" )
                ?? ExtractLabeledField( plain, "Назва", "Title", "Тытул" );
            title = DecodeHtml( title );
            if (string.IsNullOrWhiteSpace( title ) || LooksLikeBotWallTitle( title ))
            {
                // First substantial content line (skip nav crumbs).
                title = plain
                    .Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
                    .Select( l => Regex.Replace( l, @"^#+\s*", string.Empty ).Trim() )
                    .Where( l => l.Length is >= 8 and <= 220 )
                    .FirstOrDefault( l =>
                        !LooksLikeBotWallTitle( l )
                        && !l.StartsWith( "http", StringComparison.OrdinalIgnoreCase )
                        && !Regex.IsMatch( l, @"^(Home|Галоўная|Каш|Cart|Menu)\b", RegexOptions.IgnoreCase ) );
            }

            if (string.IsNullOrWhiteSpace( title ) || LooksLikeBotWallTitle( title ))
            {
                return null;
            }

            string? author = DecodeHtml(
                ExtractMetaContent( raw, "book:author" )
                ?? ExtractMetaContent( raw, "author" )
                ?? ExtractRelAuthor( raw )
                ?? ExtractLabeledField( plain, "Аўтар", "Автор", "Author", "Аўтары" ) );
            string? publisher = ExtractLabeledField(
                plain,
                "Выдавец",
                "Выдавецтва",
                "Издательство",
                "Publisher" );
            string? isbn = ExtractIsbnFromText( plain );

            (string titleOnly, string? authorFromTitle) = SplitAuthorFromTitle( title, author );
            string cleaned = CleanBookTitle(
                string.IsNullOrWhiteSpace( titleOnly ) ? title : titleOnly );
            if (string.IsNullOrWhiteSpace( cleaned ))
            {
                return null;
            }

            _logger.LogInformation(
                "Book lookup from-url via Tavily extract: {Url} → {Title} / {Author}",
                cleanUrl,
                cleaned,
                authorFromTitle ?? author );
            return new BookLookupCandidateDto
            {
                Title = cleaned,
                Author = authorFromTitle ?? author,
                Isbn = isbn,
                Publisher = publisher,
                Url = cleanUrl,
                Source = "manual",
                Snippet = TruncateSnippet( plain ),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "from-url extract failed for {Url}", cleanUrl );
            return null;
        }
    }

    private static string? ExtractMarkdownHeading( string markdown )
    {
        if (string.IsNullOrWhiteSpace( markdown ))
        {
            return null;
        }

        Match m = Regex.Match(
            markdown,
            @"^#{1,3}\s+(?<t>.+)$",
            RegexOptions.Multiline );
        if (!m.Success)
        {
            return null;
        }

        string t = m.Groups["t"].Value.Trim();
        return t.Length is >= 3 and <= 220 ? t : null;
    }

    private static string? ExtractLabeledField( string text, params string[] labels )
    {
        if (string.IsNullOrWhiteSpace( text ) || labels.Length == 0)
        {
            return null;
        }

        string alt = string.Join( "|", labels.Select( Regex.Escape ) );
        Match m = Regex.Match(
            text,
            $@"(?im)^\s*(?:{alt})\s*[:：\-–—]\s*(?<v>.+?)\s*$" );
        if (!m.Success)
        {
            return null;
        }

        string v = m.Groups["v"].Value.Trim().Trim( '*', '_', '#' );
        return v.Length is >= 2 and <= 180 ? v : null;
    }

    private static string? TruncateSnippet( string? text )
    {
        if (string.IsNullOrWhiteSpace( text ))
        {
            return null;
        }

        string t = text.Trim();
        return t.Length <= 280 ? t : t[..280] + "…";
    }

    private static string? TryGetProductSlugFromPath( string absolutePath )
    {
        Match m = Regex.Match(
            absolutePath ?? string.Empty,
            @"/(?:pradukt|produkt|product)/(?<slug>[^/]+)/?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );
        if (!m.Success)
        {
            return null;
        }

        string slug = Uri.UnescapeDataString( m.Groups["slug"].Value ).Trim().Trim( '/' );
        return string.IsNullOrWhiteSpace( slug ) ? null : slug;
    }

    private static bool TryParseProductUrlSlug(
        string url,
        out string title,
        out string? author )
    {
        title = string.Empty;
        author = null;
        if (!Uri.TryCreate( url, UriKind.Absolute, out Uri? uri ))
        {
            return false;
        }

        string? slug = TryGetProductSlugFromPath( uri.AbsolutePath );
        if (string.IsNullOrWhiteSpace( slug ))
        {
            return false;
        }

        string[] parts = slug.Split( '-', StringSplitOptions.RemoveEmptyEntries );
        if (parts.Length < 3)
        {
            return false;
        }

        // Heuristic: firstname-lastname-title-words… (Kamunikat Łacinka style).
        // Prefer Belarusian Cyrillic so the form matches the shop page language.
        string[] cyrParts = parts
            .Select( BelarusianLacinka.WordToCyrillic )
            .Where( w => !string.IsNullOrWhiteSpace( w ) )
            .ToArray();
        if (cyrParts.Length >= 3)
        {
            author = CapitalizeCyrillicWord( cyrParts[0] )
                + " "
                + CapitalizeCyrillicWord( cyrParts[1] );
            title = string.Join(
                " ",
                cyrParts.Skip( 2 ).Select( CapitalizeCyrillicWord ) );
            if (title.Length >= 3)
            {
                return true;
            }
        }

        string authorLatin = $"{CapitalizeLatin( parts[0] )} {CapitalizeLatin( parts[1] )}";
        string titleLatin = string.Join(
            " ",
            parts.Skip( 2 ).Select( CapitalizeLatin ) );
        if (titleLatin.Length < 3)
        {
            return false;
        }

        author = authorLatin;
        title = titleLatin;
        return true;
    }

    private static string CapitalizeCyrillicWord( string word )
    {
        if (string.IsNullOrWhiteSpace( word ))
        {
            return string.Empty;
        }

        string w = word.Trim();
        if (w.Length == 1)
        {
            return w.ToUpperInvariant();
        }

        return char.ToUpper( w[0], CultureInfo.InvariantCulture ) + w[1..];
    }

    private static string CapitalizeLatin( string word )
    {
        if (string.IsNullOrWhiteSpace( word ))
        {
            return string.Empty;
        }

        string w = word.Trim().ToLowerInvariant();
        if (w.Length == 1)
        {
            return w;
        }

        return char.ToUpperInvariant( w[0] ) + w[1..];
    }

    public async Task<BookCreateFromLookupResultDto> CreateShopifyProductAsync(
        BookCreateFromLookupRequest request,
        CancellationToken cancellationToken )
    {
        ShopifySession shopSession = ShopifySessionReader.Require(
            _httpContextAccessor,
            "Няма Shopify-сесіі. Перазайдзіце праз Shopify." );

        string title = (request.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( title ))
        {
            throw new InvalidOperationException( "Укажыце назву тавару." );
        }

        string? isbn = IsbnUtil.Normalize( request.Isbn ) ?? (request.Isbn ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( isbn ))
        {
            isbn = null;
        }

        byte[]? coverBytes = null;
        string? coverContentType = null;
        if (request.UseCoverImage
            && !string.IsNullOrWhiteSpace( request.SessionId )
            && _sessions.TryGet( request.SessionId, out BookLookupSessionState session ))
        {
            coverBytes = session.CoverImageBytes;
            coverContentType = session.CoverContentType;
        }

        ShopifyInventoryService.CreatedShopifyProduct created =
            await _shopifyInventory.CreateProductAsync(
                shopSession.Shop,
                shopSession.AccessToken,
                new ShopifyInventoryService.CreateShopifyProductInput(
                    title,
                    request.SalePrice,
                    request.UnitCost,
                    string.IsNullOrWhiteSpace( request.Publisher ) ? null : request.Publisher.Trim(),
                    ShopifyInventoryService.DefaultBookProductType,
                    isbn,
                    string.IsNullOrWhiteSpace( request.DescriptionHtml )
                        ? null
                        : request.DescriptionHtml.Trim(),
                    string.IsNullOrWhiteSpace( request.Author ) ? null : request.Author.Trim(),
                    WeightKg: null,
                    PublishAsDraft: true ) );

        if (coverBytes is { Length: > 0 })
        {
            try
            {
                await _shopifyInventory.AttachProductImageAsync(
                    shopSession.Shop,
                    shopSession.AccessToken,
                    created.ProductId,
                    coverBytes,
                    GuessImageFileName( coverContentType ) );
            }
            catch (Exception ex)
            {
                _logger.LogWarning( ex, "Failed to attach cover image to product {ProductId}", created.ProductId );
            }
        }

        if (!string.IsNullOrWhiteSpace( request.SessionId ))
        {
            _sessions.Remove( request.SessionId );
        }

        return new BookCreateFromLookupResultDto
        {
            ShopifyProductId = created.ProductId,
            ShopifyVariantId = created.VariantId,
            Title = created.Title,
        };
    }

    private async Task<BookLookupSessionState> CreateSessionAsync(
        string title,
        string? author,
        string? isbn,
        int? supplierId,
        byte[]? coverBytes,
        string? coverContentType,
        CancellationToken cancellationToken )
    {
        string? normalizedIsbn = IsbnUtil.Normalize( isbn );
        List<string> domains = new();
        string? supplierName = null;

        if (supplierId is int sid && sid > 0)
        {
            Supplier? supplier = await _db.Suppliers
                .AsNoTracking()
                .FirstOrDefaultAsync( s => s.Id == sid, cancellationToken );
            if (supplier is not null)
            {
                supplierName = supplier.Name;
                foreach (string? raw in new[] { supplier.Website, supplier.PriceListUrl })
                {
                    foreach (string host in ExpandHosts( raw ))
                    {
                        domains.Add( host );
                    }
                }

                domains = domains
                    .Distinct( StringComparer.OrdinalIgnoreCase )
                    .ToList();

                _logger.LogInformation(
                    "Book lookup supplier {SupplierId} ({Name}) domains: [{Domains}]",
                    sid,
                    supplierName,
                    string.Join( ", ", domains ) );
            }
            else
            {
                _logger.LogWarning( "Book lookup: supplier {SupplierId} not found", sid );
            }
        }
        else
        {
            _logger.LogInformation( "Book lookup: no supplierId — web search only" );
        }

        BookLookupSessionState session = new()
        {
            SessionId = Guid.NewGuid().ToString( "N" ),
            QueryTitle = title.Trim(),
            QueryAuthor = string.IsNullOrWhiteSpace( author ) ? null : author.Trim(),
            QueryIsbn = normalizedIsbn,
            SupplierId = supplierId,
            SupplierName = supplierName,
            SupplierDomains = domains,
            CoverImageBytes = coverBytes,
            CoverContentType = coverContentType,
            SupplierPhaseExhausted = domains.Count == 0,
        };

        _sessions.Save( session );
        return session;
    }

    private async Task<BookLookupStepResultDto> PresentNextAsync(
        BookLookupSessionState session,
        CancellationToken cancellationToken )
    {
        int maxPresented = ReadInt( "BookLookup:MaxPresentedHits", DefaultMaxPresentedHits );
        int attemptsMax = MaxAttemptsBudget( session );

        while (session.PresentedHits < maxPresented)
        {
            if (session.Queue.Count == 0)
            {
                bool filled = await TryFillQueueAsync( session, cancellationToken );
                if (!filled)
                {
                    break;
                }
            }

            while (session.Queue.Count > 0)
            {
                PendingSearchHit pending = session.Queue.Dequeue();
                string urlKey = NormalizeUrlKey( pending.Url );
                if (session.ExcludeUrls.Contains( urlKey ))
                {
                    continue;
                }

                // Cheap reject before LLM / page fetch (wrong Tavily hits were burning minutes).
                if (!LooksPromisingHit( session, pending ))
                {
                    _logger.LogInformation(
                        "Skipping unpromising hit {Url} (title={Title})",
                        pending.Url,
                        pending.Title );
                    session.ExcludeUrls.Add( urlKey );
                    continue;
                }

                BookLookupCandidateDto candidate = await NormalizeHitAsync(
                    session,
                    pending,
                    cancellationToken );

                if (string.IsNullOrWhiteSpace( candidate.Url )
                    || string.IsNullOrWhiteSpace( candidate.Title ))
                {
                    session.ExcludeUrls.Add( urlKey );
                    continue;
                }

                if (!IsRelevantCandidate( session, pending, candidate ))
                {
                    _logger.LogInformation(
                        "Skipping irrelevant hit {Url} (title={Title})",
                        pending.Url,
                        candidate.Title );
                    session.ExcludeUrls.Add( urlKey );
                    continue;
                }

                session.ExcludeUrls.Add( urlKey );
                session.PresentedHits++;
                session.CandidateBeingShown = candidate;
                _sessions.Save( session );

                return EnrichFlags( session, new BookLookupStepResultDto
                {
                    SessionId = session.SessionId,
                    Candidate = candidate,
                    QueryTitle = session.QueryTitle,
                    QueryAuthor = session.QueryAuthor,
                    QueryIsbn = session.QueryIsbn,
                    AttemptsUsed = session.PresentedHits,
                    AttemptsMax = Math.Min( maxPresented, attemptsMax ),
                    Done = false,
                    Message = null,
                } );
            }
        }

        session.CandidateBeingShown = null;
        _sessions.Save( session );

        return EnrichFlags( session, new BookLookupStepResultDto
        {
            SessionId = session.SessionId,
            Candidate = null,
            QueryTitle = session.QueryTitle,
            QueryAuthor = session.QueryAuthor,
            QueryIsbn = session.QueryIsbn,
            AttemptsUsed = session.PresentedHits,
            AttemptsMax = Math.Min( maxPresented, attemptsMax ),
            Done = true,
            Message = session.PresentedHits == 0
                ? (session.SupplierDomains.Count > 0
                    ? "Кнігу не знойдзена на сайце пастаўшчыка. Паспрабуйце пошук па фота або ўвядзіце спасылку."
                    : "Кнігу не знойдзена. Паспрабуйце пошук па фота або ўвядзіце спасылку.")
                : "Больш варыянтаў няма. Паспрабуйце пошук па фота або ўвядзіце спасылку.",
        } );
    }

    private BookLookupStepResultDto EnrichFlags(
        BookLookupSessionState session,
        BookLookupStepResultDto dto )
    {
        bool hasCover = session.CoverImageBytes is { Length: > 0 };
        bool lensReady = _lens.IsConfigured();
        bool photoLeft = !(session.PhotoSearchSupplierDone && session.PhotoSearchWebDone);
        dto.CanSearchByPhoto = hasCover && lensReady && photoLeft;
        dto.PhotoSearchExhausted = session.PhotoSearchSupplierDone && session.PhotoSearchWebDone;
        return dto;
    }

    /// <summary>
    /// Fast path: one author catalog probe. Cloudflare often 403s the shop from
    /// datacenter IPs — stop immediately and fall through to Tavily / slug guess.
    /// </summary>
    private async Task<bool> TryPrefillWordpressCatalogAsync(
        BookLookupSessionState session,
        CancellationToken cancellationToken )
    {
        if (session.SupplierCatalogBlocked)
        {
            return false;
        }

        string authorLast = AuthorLastName( session.QueryAuthor );
        if (!string.IsNullOrWhiteSpace( authorLast ) && LooksLikeOcrAllCaps( authorLast ))
        {
            authorLast = HumanizeOcrName( authorLast );
        }
        else if (!string.IsNullOrWhiteSpace( session.QueryAuthor )
            && LooksLikeOcrAllCaps( session.QueryAuthor ))
        {
            authorLast = AuthorLastName( HumanizeOcrName( session.QueryAuthor.Trim() ) );
        }

        string softTitle = SoftTitleForSearch( session.QueryTitle );
        string core3 = SoftTitleCore( softTitle, maxWords: 3 );

        // At most two cheap probes — never burn 4× WP+HTML round-trips on 403.
        List<string> queries = new();
        if (!string.IsNullOrWhiteSpace( authorLast ))
        {
            queries.Add( authorLast );
        }
        else if (!string.IsNullOrWhiteSpace( core3 ))
        {
            queries.Add( core3 );
        }

        foreach (string query in queries.Take( 1 ))
        {
            _logger.LogInformation(
                "Book lookup catalog prefill: {Query} domains={Domains}",
                query,
                string.Join( ",", session.SupplierDomains ) );

            (IReadOnlyList<TavilySearchHit> hits, bool blocked) =
                await SearchWordpressCatalogAsync(
                    query,
                    session.SupplierDomains,
                    cancellationToken );
            if (blocked)
            {
                session.SupplierCatalogBlocked = true;
                _logger.LogWarning(
                    "Book lookup: supplier catalog blocked (Cloudflare); skipping further direct shop HTTP" );
                return false;
            }

            if (hits.Count == 0)
            {
                continue;
            }

            EnqueueHits( session, RankHits( hits, session ), "supplier" );
            if (session.Queue.Count > 0)
            {
                _logger.LogInformation(
                    "Book lookup catalog prefill queued {Count} hit(s)",
                    session.Queue.Count );
                return true;
            }
        }

        _logger.LogInformation( "Book lookup catalog prefill found no products" );
        return false;
    }

    private async Task<IReadOnlyList<TavilySearchHit>> SearchSupplierAsync(
        BookLookupSessionState session,
        string query,
        IReadOnlyList<string> domains,
        CancellationToken cancellationToken )
    {
        if (!session.SupplierCatalogBlocked)
        {
            (IReadOnlyList<TavilySearchHit> catalogHits, bool blocked) =
                await SearchWordpressCatalogAsync( query, domains, cancellationToken );
            if (blocked)
            {
                session.SupplierCatalogBlocked = true;
            }

            if (catalogHits.Count > 0)
            {
                _logger.LogInformation(
                    "Book lookup supplier catalog hit {Count} product(s) for query={Query}",
                    catalogHits.Count,
                    query );
                return catalogHits;
            }
        }

        // Bare ISBN is almost never indexed for these shops — skip slow Tavily.
        if (IsBareIsbnQuery( query ))
        {
            _logger.LogInformation(
                "Book lookup: skipping Tavily for bare ISBN query on supplier domains" );
            return Array.Empty<TavilySearchHit>();
        }

        try
        {
            IReadOnlyList<TavilySearchHit> hits = FilterHitsToDomains(
                await _tavily.SearchAsync(
                    query,
                    domains,
                    ResultsPerSearch,
                    cancellationToken ),
                domains );

            if (hits.Count > 0)
            {
                return hits;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "Tavily supplier search failed for query={Query}", query );
        }

        string apex = StripWww( domains[0] );
        if (string.IsNullOrWhiteSpace( apex ))
        {
            return Array.Empty<TavilySearchHit>();
        }

        try
        {
            string siteQuery = $"site:{apex} {query}";
            _logger.LogInformation( "Book lookup site: fallback query={Query}", siteQuery );
            return FilterHitsToDomains(
                await _tavily.SearchAsync(
                    siteQuery,
                    includeDomains: null,
                    ResultsPerSearch,
                    cancellationToken ),
                domains );
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "Tavily site: fallback failed for query={Query}", query );
            return Array.Empty<TavilySearchHit>();
        }
    }

    private static bool IsBareIsbnQuery( string query )
    {
        string trimmed = (query ?? string.Empty).Trim();
        string? asIsbn = IsbnUtil.Normalize( trimmed );
        return !string.IsNullOrWhiteSpace( asIsbn )
            && string.Equals( DigitsOnly( trimmed ), asIsbn, StringComparison.Ordinal );
    }

    /// <summary>
    /// Shop catalog: WooCommerce REST, then HTML <c>?s=</c> search.
    /// Returns Blocked=true when Cloudflare (or similar) 403s the shop.
    /// </summary>
    private async Task<(IReadOnlyList<TavilySearchHit> Hits, bool Blocked)> SearchWordpressCatalogAsync(
        string query,
        IReadOnlyList<string> domains,
        CancellationToken cancellationToken )
    {
        string trimmed = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( trimmed ) || domains.Count == 0)
        {
            return (Array.Empty<TavilySearchHit>(), false);
        }

        if (IsBareIsbnQuery( trimmed ))
        {
            return (Array.Empty<TavilySearchHit>(), false);
        }

        HttpClient client = _httpClientFactory.CreateClient( "BookLookupPage" );
        List<TavilySearchHit> hits = new();
        HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );
        string? apex = domains
            .Select( StripWww )
            .FirstOrDefault( a => !string.IsNullOrWhiteSpace( a ) );
        if (string.IsNullOrWhiteSpace( apex ))
        {
            return (Array.Empty<TavilySearchHit>(), false);
        }

        (bool restOk, bool restBlocked) = await TryWordpressRestCatalogAsync(
            client,
            apex,
            trimmed,
            domains,
            hits,
            seen,
            cancellationToken );
        if (restBlocked)
        {
            return (hits, true);
        }

        if (hits.Count > 0)
        {
            return (hits, false);
        }

        if (!restOk)
        {
            (_, bool htmlBlocked) = await TryStorefrontHtmlSearchAsync(
                client,
                apex,
                trimmed,
                domains,
                hits,
                seen,
                cancellationToken );
            if (htmlBlocked)
            {
                return (hits, true);
            }
        }

        return (hits, false);
    }

    private async Task<(bool Ok, bool Blocked)> TryWordpressRestCatalogAsync(
        HttpClient client,
        string apex,
        string query,
        IReadOnlyList<string> domains,
        List<TavilySearchHit> hits,
        HashSet<string> seen,
        CancellationToken cancellationToken )
    {
        string url =
            $"https://{apex}/wp-json/wp/v2/product?search={Uri.EscapeDataString( query )}&per_page=5";
        try
        {
            using HttpResponseMessage response = await client.GetAsync( url, cancellationToken );
            int status = (int)response.StatusCode;
            if (status is 403 or 503)
            {
                _logger.LogWarning( "WordPress catalog {Url} returned {Status}", url, status );
                return (false, true);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning( "WordPress catalog {Url} returned {Status}", url, status );
                return (false, false);
            }

            string body = await response.Content.ReadAsStringAsync( cancellationToken );
            using JsonDocument doc = JsonDocument.Parse( body );
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return (true, false);
            }

            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                string link = item.TryGetProperty( "link", out JsonElement linkEl )
                    ? (linkEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                string title = string.Empty;
                if (item.TryGetProperty( "title", out JsonElement titleEl )
                    && titleEl.ValueKind == JsonValueKind.Object
                    && titleEl.TryGetProperty( "rendered", out JsonElement rendered ))
                {
                    title = DecodeHtml( rendered.GetString() ?? string.Empty ) ?? string.Empty;
                }

                string excerpt = string.Empty;
                if (item.TryGetProperty( "excerpt", out JsonElement excerptEl )
                    && excerptEl.ValueKind == JsonValueKind.Object
                    && excerptEl.TryGetProperty( "rendered", out JsonElement excerptRendered ))
                {
                    excerpt = StripHtmlTags(
                        DecodeHtml( excerptRendered.GetString() ?? string.Empty ) ?? string.Empty );
                }

                TryAddCatalogHit( hits, seen, domains, link, title, excerpt );
            }

            return (true, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "WordPress catalog search failed for {Url}", url );
            return (false, false);
        }
    }

    private async Task<(bool Ok, bool Blocked)> TryStorefrontHtmlSearchAsync(
        HttpClient client,
        string apex,
        string query,
        IReadOnlyList<string> domains,
        List<TavilySearchHit> hits,
        HashSet<string> seen,
        CancellationToken cancellationToken )
    {
        string url =
            $"https://{apex}/?s={Uri.EscapeDataString( query )}&post_type=product";
        try
        {
            using HttpRequestMessage request = new( HttpMethod.Get, url );
            request.Headers.TryAddWithoutValidation( "Referer", $"https://{apex}/" );
            using HttpResponseMessage response = await client.SendAsync( request, cancellationToken );
            int status = (int)response.StatusCode;
            if (status is 403 or 503)
            {
                _logger.LogWarning( "Storefront HTML search {Url} returned {Status}", url, status );
                return (false, true);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning( "Storefront HTML search {Url} returned {Status}", url, status );
                return (false, false);
            }

            string html = await response.Content.ReadAsStringAsync( cancellationToken );
            if (string.IsNullOrWhiteSpace( html )
                || html.Contains( "Just a moment", StringComparison.OrdinalIgnoreCase )
                || html.Contains( "cf-browser-verification", StringComparison.OrdinalIgnoreCase ))
            {
                _logger.LogWarning( "Storefront HTML search {Url} looks like a bot wall", url );
                return (false, true);
            }

            int before = hits.Count;
            foreach ((string link, string title) in ExtractProductLinksFromSearchHtml( html, apex ))
            {
                TryAddCatalogHit( hits, seen, domains, link, title, content: string.Empty );
                if (hits.Count >= 5)
                {
                    break;
                }
            }

            if (hits.Count > before)
            {
                _logger.LogInformation(
                    "Book lookup storefront HTML search queued {Count} hit(s) for query={Query}",
                    hits.Count - before,
                    query );
            }

            return (true, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "Storefront HTML search failed for {Url}", url );
            return (false, false);
        }
    }

    private static void TryAddCatalogHit(
        List<TavilySearchHit> hits,
        HashSet<string> seen,
        IReadOnlyList<string> domains,
        string link,
        string title,
        string content )
    {
        if (string.IsNullOrWhiteSpace( link ) || string.IsNullOrWhiteSpace( title ))
        {
            return;
        }

        if (!IsSupplierHost( link, domains ))
        {
            return;
        }

        string key = NormalizeUrlKey( link );
        if (!seen.Add( key ))
        {
            return;
        }

        hits.Add( new TavilySearchHit
        {
            Title = title.Trim(),
            Url = link.Trim(),
            Content = (content ?? string.Empty).Trim(),
        } );
    }

    /// <summary>
    /// Pull product cards from WooCommerce/WordPress search HTML.
    /// </summary>
    private static IEnumerable<(string Url, string Title)> ExtractProductLinksFromSearchHtml(
        string html,
        string apex )
    {
        if (string.IsNullOrWhiteSpace( html ))
        {
            yield break;
        }

        // href="https://shop/pradukt/slug/" ... title text in nearby heading or link body.
        Regex linkRe = new(
            $@"href\s*=\s*[""'](?<url>https?://(?:www\.)?{Regex.Escape( apex )}/(?:pradukt|produkt|product)/[^""'#?]+/?)[""'][^>]*>(?<inner>.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant );

        HashSet<string> yielded = new( StringComparer.OrdinalIgnoreCase );
        foreach (Match m in linkRe.Matches( html ))
        {
            string url = m.Groups["url"].Value.Trim();
            string inner = StripHtmlTags( DecodeHtml( m.Groups["inner"].Value ) ?? string.Empty )
                .Trim();
            if (string.IsNullOrWhiteSpace( inner ) || inner.Length < 3)
            {
                continue;
            }

            // Skip nav/noise ("Дадаць у кошык", prices-only).
            if (inner.Length > 180 || LooksLikeCartOrUiNoise( inner ))
            {
                continue;
            }

            string key = NormalizeUrlKey( url );
            if (!yielded.Add( key ))
            {
                continue;
            }

            yield return (url, inner);
        }
    }

    private static bool LooksLikeCartOrUiNoise( string text )
    {
        string n = text.Trim().ToLowerInvariant();
        return n.Contains( "кошык", StringComparison.Ordinal )
            || n.Contains( "корзин", StringComparison.Ordinal )
            || n.Contains( "add to cart", StringComparison.Ordinal )
            || n.Contains( "czytaj więcej", StringComparison.Ordinal )
            || n.Contains( "чытаць далей", StringComparison.Ordinal )
            || (n.Length <= 12 && n.Any( char.IsDigit ) && n.Contains( "zł", StringComparison.Ordinal ));
    }

    private static IReadOnlyList<TavilySearchHit> FilterHitsToDomains(
        IReadOnlyList<TavilySearchHit> hits,
        IReadOnlyList<string> domains )
    {
        if (domains.Count == 0)
        {
            return hits;
        }

        return hits
            .Where( h => IsSupplierHost( h.Url, domains ) )
            .ToList();
    }

    private async Task<bool> TryFillQueueAsync(
        BookLookupSessionState session,
        CancellationToken cancellationToken )
    {
        int maxSupplier = ReadInt( "BookLookup:MaxSupplierSearches", DefaultMaxSupplierSearches );
        int maxWeb = ReadInt( "BookLookup:MaxWebSearches", DefaultMaxWebSearches );

        if (!session.SupplierPhaseExhausted
            && session.SupplierDomains.Count > 0
            && !session.CatalogPrefillDone)
        {
            session.CatalogPrefillDone = true;
            if (await TryPrefillWordpressCatalogAsync( session, cancellationToken ))
            {
                _sessions.Save( session );
                return true;
            }

            // Direct shop HTTP is often Cloudflare-blocked from the server.
            // Search the shop via Tavily (indexed pages) — never invent product URLs.
            if (await TrySearchSupplierViaIndexerAsync( session, cancellationToken ))
            {
                _sessions.Save( session );
                return true;
            }

            _sessions.Save( session );
        }

        if (!session.SupplierPhaseExhausted
            && session.SupplierDomains.Count > 0
            && session.SupplierSearchCalls < maxSupplier)
        {
            IReadOnlyList<string> queries = BuildSearchQueries( session, forSupplier: true );
            if (session.SupplierQueryIndex >= queries.Count)
            {
                session.SupplierPhaseExhausted = true;
                _sessions.Save( session );
                return await TryFillQueueAsync( session, cancellationToken );
            }

            string query = queries[session.SupplierQueryIndex];
            session.SupplierQueryIndex++;
            _logger.LogInformation(
                "Book lookup supplier search #{Call}: {Query} domains={Domains}",
                session.SupplierSearchCalls + 1,
                query,
                string.Join( ",", session.SupplierDomains ) );

            IReadOnlyList<TavilySearchHit> hits = await SearchSupplierAsync(
                session,
                query,
                session.SupplierDomains,
                cancellationToken );
            session.SupplierSearchCalls++;
            EnqueueHits( session, RankHits( hits, session ), "supplier" );
            if (session.Queue.Count > 0)
            {
                _sessions.Save( session );
                return true;
            }

            _logger.LogInformation(
                "Book lookup supplier search returned 0 usable hits for query={Query}",
                query );
            _sessions.Save( session );
            return await TryFillQueueAsync( session, cancellationToken );
        }

        session.SupplierPhaseExhausted = true;

        // When a supplier site is configured, do not fall back to the open web —
        // that surfaces news/tag pages instead of the shop product.
        bool allowWebFallback = ReadBool( "BookLookup:AllowWebFallback", defaultValue: false );
        if (session.SupplierDomains.Count > 0 && !allowWebFallback)
        {
            _logger.LogInformation(
                "Book lookup: supplier domains set ({Domains}); skipping web fallback",
                string.Join( ",", session.SupplierDomains ) );
            return false;
        }

        if (session.WebSearchCalls < maxWeb)
        {
            IReadOnlyList<string> queries = BuildSearchQueries( session, forSupplier: false );
            if (session.WebQueryIndex >= queries.Count)
            {
                return false;
            }

            string query = queries[session.WebQueryIndex];
            session.WebQueryIndex++;
            _logger.LogInformation(
                "Book lookup web search #{Call}: {Query}",
                session.WebSearchCalls + 1,
                query );

            IReadOnlyList<TavilySearchHit> hits = await _tavily.SearchAsync(
                query,
                includeDomains: null,
                ResultsPerSearch,
                cancellationToken );
            session.WebSearchCalls++;
            EnqueueHits( session, RankHits( hits, session ), "web" );
            _sessions.Save( session );
            if (session.Queue.Count > 0)
            {
                return true;
            }

            return await TryFillQueueAsync( session, cancellationToken );
        }

        return false;
    }

    private void EnqueueHits(
        BookLookupSessionState session,
        IReadOnlyList<TavilySearchHit> hits,
        string source )
    {
        foreach (TavilySearchHit hit in hits)
        {
            string key = NormalizeUrlKey( hit.Url );
            if (session.ExcludeUrls.Contains( key ))
            {
                continue;
            }

            if (session.Queue.Any( q => NormalizeUrlKey( q.Url ) == key ))
            {
                continue;
            }

            session.Queue.Enqueue( new PendingSearchHit
            {
                Title = hit.Title,
                Url = hit.Url,
                Content = hit.Content,
                Source = source,
            } );
        }
    }

    private static bool LooksPromisingHit(
        BookLookupSessionState session,
        PendingSearchHit hit )
    {
        if (string.IsNullOrWhiteSpace( hit.Url ))
        {
            return false;
        }

        // Manual URLs are trusted enough to normalize.
        if (string.Equals( hit.Source, "manual", StringComparison.OrdinalIgnoreCase ))
        {
            return true;
        }

        if (SoftTitlePhraseContainedIn( hit.Title, session.QueryTitle )
            || SoftTitlePhraseContainedIn( $"{hit.Title} {hit.Content}", session.QueryTitle ))
        {
            return true;
        }

        string last = AuthorLastName( session.QueryAuthor );
        string blobNorm = NormalizeForMatch( $"{hit.Title}\n{hit.Url}\n{hit.Content}" );
        bool authorHit = !string.IsNullOrWhiteSpace( last )
            && last.Length >= 3
            && (blobNorm.Contains( NormalizeForMatch( last ), StringComparison.Ordinal )
                || FoldBeLetters( blobNorm ).Contains(
                    FoldBeLetters( NormalizeForMatch( last ) ),
                    StringComparison.Ordinal ));

        IReadOnlyList<string> titleWords = SoftTitleWords( session.QueryTitle )
            .Where( w => w.Length >= 3 && !IsWeakTitleWord( w ) )
            .ToArray();
        int titleHits = CountSoftWordHits( SoftTitleWords( $"{hit.Title} {hit.Content}" ), titleWords );

        if (authorHit && titleHits >= 1)
        {
            return true;
        }

        if (titleHits >= 2)
        {
            return true;
        }

        // Latin slug in URL (mikalaj-statkevich-i-heta…).
        string slug = BuildProductSlug( session.QueryAuthor, session.QueryTitle );
        if (!string.IsNullOrWhiteSpace( slug )
            && slug.Length >= 8
            && hit.Url.Contains( slug, StringComparison.OrdinalIgnoreCase ))
        {
            return true;
        }

        string latinAuthor = ToBelarusianLatinSlug(
            string.Join( " ", SoftTitleWords( session.QueryAuthor ).Take( 2 ) ) );
        if (!string.IsNullOrWhiteSpace( latinAuthor )
            && latinAuthor.Length >= 5
            && hit.Url.Contains( latinAuthor, StringComparison.OrdinalIgnoreCase )
            && titleHits >= 1)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Search the supplier shop via Tavily (works when Cloudflare blocks our server IP).
    /// Only enqueues real indexed hits — never invents /pradukt/… URLs.
    /// </summary>
    private async Task<bool> TrySearchSupplierViaIndexerAsync(
        BookLookupSessionState session,
        CancellationToken cancellationToken )
    {
        if (session.SupplierDomains.Count == 0)
        {
            return false;
        }

        string apex = StripWww( session.SupplierDomains[0] );
        if (string.IsNullOrWhiteSpace( apex ))
        {
            return false;
        }

        string? author = session.QueryAuthor;
        if (!string.IsNullOrWhiteSpace( author ) && LooksLikeOcrAllCaps( author ))
        {
            author = HumanizeOcrName( author.Trim() );
        }

        string authorLast = AuthorLastName( author );
        string softTitle = SoftTitleForSearch( session.QueryTitle );
        string core3 = SoftTitleCore( softTitle, maxWords: 3 );
        string latinAuthor = ToBelarusianLatinSlug(
            string.Join( " ", SoftTitleWords( author ).Take( 2 ) ) );
        string latinTitle = ToBelarusianLatinSlug(
            string.Join(
                " ",
                TitleTokensForSlug( session.QueryTitle ).Where( w => !IsSlugParticle( w ) ).Take( 3 ) ) );

        List<string> queries = new();
        void AddQuery( string? q )
        {
            if (string.IsNullOrWhiteSpace( q ))
            {
                return;
            }

            string t = q.Trim();
            if (!queries.Contains( t, StringComparer.OrdinalIgnoreCase ))
            {
                queries.Add( t );
            }
        }

        if (!string.IsNullOrWhiteSpace( latinAuthor ) && latinAuthor.Length >= 5)
        {
            AddQuery( $"site:{apex}/pradukt {latinAuthor.Replace( '-', ' ' )}" );
            AddQuery( $"site:{apex} {latinAuthor.Replace( '-', ' ' )}" );
        }

        if (!string.IsNullOrWhiteSpace( latinAuthor ) && !string.IsNullOrWhiteSpace( latinTitle ))
        {
            AddQuery(
                $"site:{apex} {latinAuthor.Replace( '-', ' ' )} {latinTitle.Replace( '-', ' ' )}" );
        }

        if (!string.IsNullOrWhiteSpace( authorLast ) && !string.IsNullOrWhiteSpace( core3 ))
        {
            AddQuery( $"site:{apex} {authorLast} {core3}" );
        }

        if (!string.IsNullOrWhiteSpace( authorLast ))
        {
            AddQuery( $"site:{apex} {authorLast}" );
        }

        if (!string.IsNullOrWhiteSpace( softTitle ))
        {
            AddQuery( $"site:{apex} {softTitle}" );
        }

        foreach (string query in queries.Take( 4 ))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation( "Book lookup indexer search: {Query}", query );

            IReadOnlyList<TavilySearchHit> found;
            try
            {
                found = FilterHitsToDomains(
                    await _tavily.SearchAsync(
                        query,
                        includeDomains: null,
                        ResultsPerSearch,
                        cancellationToken ),
                    session.SupplierDomains );
            }
            catch (Exception ex)
            {
                _logger.LogWarning( ex, "Indexer search failed for {Query}", query );
                continue;
            }

            IReadOnlyList<TavilySearchHit> ranked = RankHits( found, session )
                .Where( h => LooksPromisingHit(
                    session,
                    new PendingSearchHit
                    {
                        Title = h.Title,
                        Url = h.Url,
                        Content = h.Content,
                        Source = "supplier",
                    } ) )
                .Take( ResultsPerSearch )
                .ToList();

            if (ranked.Count == 0)
            {
                continue;
            }

            EnqueueHits( session, ranked, "supplier" );
            if (session.Queue.Count > 0)
            {
                _logger.LogInformation(
                    "Book lookup indexer queued {Count} hit(s) for {Query}",
                    session.Queue.Count,
                    query );
                return true;
            }
        }

        return false;
    }

    /// <summary>Latin fragment for Tavily queries (not for inventing URLs).</summary>
    private static string BuildProductSlug( string? author, string? title )
    {
        return BuildProductSlugVariants( author, title ).FirstOrDefault() ?? string.Empty;
    }

    private static IReadOnlyList<string> BuildProductSlugVariants( string? author, string? title )
    {
        List<string> parts = new();
        foreach (string w in SoftTitleWords( author ).Take( 2 ))
        {
            string latin = ToBelarusianLatinSlug( w );
            if (!string.IsNullOrWhiteSpace( latin ))
            {
                parts.Add( latin );
            }
        }

        int authorParts = parts.Count;
        foreach (string w in TitleTokensForSlug( title ))
        {
            string latin = ToBelarusianLatinSlug( w );
            if (!string.IsNullOrWhiteSpace( latin ))
            {
                parts.Add( latin );
            }
        }

        if (parts.Count <= authorParts)
        {
            return Array.Empty<string>();
        }

        List<string> variants = new();
        variants.Add( string.Join( "-", parts ) );
        if (parts.Count > authorParts + 4)
        {
            variants.Add( string.Join( "-", parts.Take( authorParts + 4 ) ) );
        }

        return variants
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .Where( v => v.Length >= 8 )
            .ToList();
    }

    private static IReadOnlyList<string> TitleTokensForSlug( string? title )
    {
        if (string.IsNullOrWhiteSpace( title ))
        {
            return Array.Empty<string>();
        }

        List<string> result = new();
        foreach (string raw in NormalizeForMatch( title )
                     .Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ))
        {
            string w = FoldBeLetters( raw );
            if (string.IsNullOrWhiteSpace( w ))
            {
                continue;
            }

            if (IsWeakTitleWord( w ) || IsSlugGenreWord( w ))
            {
                break;
            }

            if (w.Length == 1 && !IsSlugParticle( w ))
            {
                continue;
            }

            result.Add( w );
            if (result.Count( t => !IsSlugParticle( t ) ) >= 5)
            {
                break;
            }
        }

        return result;
    }

    private static bool IsSlugParticle( string word )
    {
        string w = FoldBeLetters( word.Trim().ToLowerInvariant() );
        return w is "і" or "и" or "у" or "i" or "u" or "a" or "the" or "of";
    }

    private static bool IsSlugGenreWord( string word )
    {
        string w = FoldBeLetters( word.Trim().ToLowerInvariant() );
        if (w is "эсэ" or "эссе" or "есе" or "essay" or "essays" or "проза" or "паэзія"
            or "вершы" or "аповесць" or "раман")
        {
            return true;
        }

        return w.StartsWith( "турэмн", StringComparison.Ordinal )
            || w.StartsWith( "тюремн", StringComparison.Ordinal )
            || w.StartsWith( "prison", StringComparison.Ordinal );
    }

    private static string ToBelarusianLatinSlug( string? word )
    {
        if (string.IsNullOrWhiteSpace( word ))
        {
            return string.Empty;
        }

        string folded = FoldBeLetters( word.Trim().ToLowerInvariant() );
        StringBuilder sb = new( folded.Length * 2 );
        char prev = '\0';
        foreach (char c in folded)
        {
            if (c == 'е')
            {
                // табе→tabie (е after labial); статкевіч→statkevich (е after к).
                bool labial = prev is 'б' or 'п' or 'в' or 'м' or 'ф';
                sb.Append( labial ? "ie" : "e" );
            }
            else
            {
                sb.Append( c switch
                {
                    'а' => "a",
                    'б' => "b",
                    'в' => "v",
                    'г' => "h",
                    'д' => "d",
                    'ё' => "io",
                    'ж' => "zh",
                    'з' => "z",
                    'і' => "i",
                    'й' => "j",
                    'к' => "k",
                    'л' => "l",
                    'м' => "m",
                    'н' => "n",
                    'о' => "o",
                    'п' => "p",
                    'р' => "r",
                    'с' => "s",
                    'т' => "t",
                    'у' => "u",
                    'ф' => "f",
                    'х' => "kh",
                    'ц' => "c",
                    'ч' => "ch",
                    'ш' => "sh",
                    'ы' => "y",
                    'э' => "e",
                    'ю' => "iu",
                    'я' => "ia",
                    'ь' or '\'' or '’' => "",
                    _ when c is >= 'a' and <= 'z' || c is >= '0' and <= '9' => c.ToString(),
                    _ => ""
                } );
            }

            if (char.IsLetter( c ))
            {
                prev = c;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Supplier site: soft title (+ shorter core) → author → ISBN.
    /// Titles are punctuation-stripped so near-matches still hit.
    /// Web: ISBN first, then combined/soft queries.
    /// </summary>
    private static IReadOnlyList<string> BuildSearchQueries(
        BookLookupSessionState session,
        bool forSupplier )
    {
        List<string> queries = new();
        string? isbn = session.QueryIsbn;
        string? author = session.QueryAuthor;
        // OCR authors are often ALL CAPS — humanize for shop/Tavily queries.
        if (!string.IsNullOrWhiteSpace( author ) && LooksLikeOcrAllCaps( author ))
        {
            author = HumanizeOcrName( author.Trim() );
        }

        string softTitle = SoftTitleForSearch( session.QueryTitle );
        string authorLast = AuthorLastName( author );

        if (forSupplier)
        {
            // When Cloudflare blocks the shop, bare ISBN never helps and only wastes a slot.
            if (!session.SupplierCatalogBlocked && !string.IsNullOrWhiteSpace( isbn ))
            {
                queries.Add( isbn );
            }

            string core3 = SoftTitleCore( softTitle, maxWords: 3 );
            string core5 = SoftTitleCore( softTitle, maxWords: 5 );
            string latinSlug = BuildProductSlug( author, session.QueryTitle );
            string latinAuthor = ToBelarusianLatinSlug(
                string.Join( " ", SoftTitleWords( author ).Take( 2 ) ) );

            // Author + short title first — best Tavily signal for Cyrillic shops.
            if (!string.IsNullOrWhiteSpace( authorLast ) && !string.IsNullOrWhiteSpace( core3 ))
            {
                queries.Add( $"{authorLast} {core3}" );
            }

            if (!string.IsNullOrWhiteSpace( authorLast ))
            {
                queries.Add( authorLast );
            }
            else if (!string.IsNullOrWhiteSpace( author ))
            {
                queries.Add( author.Trim() );
            }

            if (!string.IsNullOrWhiteSpace( latinSlug ) && latinSlug.Length >= 8)
            {
                queries.Add( latinSlug.Replace( '-', ' ' ) );
            }

            if (!string.IsNullOrWhiteSpace( latinAuthor ) && latinAuthor.Length >= 4)
            {
                queries.Add( latinAuthor.Replace( '-', ' ' ) );
            }

            if (!string.IsNullOrWhiteSpace( core3 ))
            {
                queries.Add( core3 );
            }

            if (!string.IsNullOrWhiteSpace( softTitle )
                && !string.Equals( softTitle, core3, StringComparison.OrdinalIgnoreCase )
                && !string.Equals( softTitle, core5, StringComparison.OrdinalIgnoreCase ))
            {
                queries.Add( softTitle );
            }
            else if (!string.IsNullOrWhiteSpace( core5 )
                && !string.Equals( core5, core3, StringComparison.OrdinalIgnoreCase ))
            {
                queries.Add( core5 );
            }

            return queries
                .Where( q => !string.IsNullOrWhiteSpace( q ) )
                .Select( q => q.Trim() )
                .Distinct( StringComparer.OrdinalIgnoreCase )
                .Take( 5 )
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace( isbn ))
        {
            queries.Add( isbn );
        }

        if (!string.IsNullOrWhiteSpace( author ) && !string.IsNullOrWhiteSpace( softTitle ))
        {
            queries.Add( $"{author} {softTitle}" );
        }

        if (!string.IsNullOrWhiteSpace( authorLast ) && !string.IsNullOrWhiteSpace( softTitle ))
        {
            string withLast = $"{authorLast} {softTitle}";
            if (!queries.Contains( withLast, StringComparer.OrdinalIgnoreCase ))
            {
                queries.Add( withLast );
            }
        }

        if (!string.IsNullOrWhiteSpace( softTitle ))
        {
            queries.Add( softTitle );
        }

        if (!string.IsNullOrWhiteSpace( author ))
        {
            queries.Add( author );
        }

        if (!string.IsNullOrWhiteSpace( softTitle ))
        {
            queries.Add( $"{softTitle} book" );
        }

        return queries
            .Where( q => !string.IsNullOrWhiteSpace( q ) )
            .Select( q => q.Trim() )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .Take( 5 )
            .ToList();
    }

    private static string SoftTitleForSearch( string? title )
    {
        // Keep queries short — long OCR strings (subtitle/genre) miss shop pages.
        return string.Join( " ", SoftTitleWords( title ).Take( 5 ) );
    }

    /// <summary>
    /// True when the OCR/query title phrase appears inside the page book title,
    /// ignoring punctuation/case/ў↔у. Allows the shop title to be longer
    /// (subtitle, series) OR shorter (cover has extra genre line).
    /// </summary>
    private static bool SoftTitlePhraseContainedIn( string? haystackTitle, string? queryTitle )
    {
        IReadOnlyList<string> needle = SoftTitleWords( queryTitle )
            .Where( w => w.Length >= 2 )
            .Select( FoldBeLetters )
            .Take( 8 )
            .ToArray();
        if (needle.Count == 0 || string.IsNullOrWhiteSpace( haystackTitle ))
        {
            return false;
        }

        IReadOnlyList<string> hay = SoftTitleWords( haystackTitle )
            .Select( FoldBeLetters )
            .ToArray();
        if (hay.Count == 0)
        {
            return false;
        }

        if (PhraseWordsMatchInOrder( hay, needle ))
        {
            return true;
        }

        // Cover OCR often appends genre ("ЗБОРНІК ТУРЭМНЫХ ЭСЭ") that the shop omits —
        // treat a shorter shop title as a match when its phrase sits inside the OCR title.
        if (hay.Count >= 2 && hay.Count < needle.Count && PhraseWordsMatchInOrder( needle, hay ))
        {
            return true;
        }

        // Core of the query (first 3–5 content words) inside the shop title.
        if (needle.Count > 3)
        {
            IReadOnlyList<string> core = needle.Take( Math.Min( 5, needle.Count - 1 ) ).ToArray();
            if (PhraseWordsMatchInOrder( hay, core ))
            {
                return true;
            }
        }

        // Unordered soft overlap: enough distinctive words regardless of order/subtitle.
        int distinctive = needle.Count( w => w.Length >= 3 );
        if (distinctive >= 2)
        {
            int need = distinctive <= 3 ? 2 : Math.Max( 2, ( distinctive + 1 ) / 2 );
            if (CountSoftWordHits( hay, needle ) >= need)
            {
                return true;
            }
        }

        return false;
    }

    private static int CountSoftWordHits(
        IReadOnlyList<string> hayWords,
        IReadOnlyList<string> needleWords )
    {
        int hits = 0;
        foreach (string needle in needleWords)
        {
            if (needle.Length < 3)
            {
                continue;
            }

            if (hayWords.Any( h => WordsSoftEqual( h, needle ) ))
            {
                hits++;
            }
        }

        return hits;
    }

    private static bool PhraseWordsMatchInOrder(
        IReadOnlyList<string> hay,
        IReadOnlyList<string> needle )
    {
        if (needle.Count == 0 || hay.Count == 0)
        {
            return false;
        }

        string needlePhrase = string.Join( " ", needle );
        string hayPhrase = string.Join( " ", hay );
        if (hayPhrase.Contains( needlePhrase, StringComparison.Ordinal ))
        {
            return true;
        }

        int hi = 0;
        int matched = 0;
        foreach (string n in needle)
        {
            bool found = false;
            while (hi < hay.Count)
            {
                string h = hay[hi++];
                if (WordsSoftEqual( h, n ))
                {
                    matched++;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                break;
            }
        }

        if (needle.Count <= 2)
        {
            return matched == needle.Count;
        }

        int need = needle.Count <= 4 ? needle.Count : needle.Count - 1;
        return matched >= need;
    }

    private static bool WordsSoftEqual( string a, string b )
    {
        if (string.Equals( a, b, StringComparison.Ordinal ))
        {
            return true;
        }

        // Short tokens (3 chars): allow one trailing-char drift (эсе/эсэ).
        if (a.Length < 3 || b.Length < 3)
        {
            return false;
        }

        if (a.Length == 3 || b.Length == 3)
        {
            string shorter = a.Length <= b.Length ? a : b;
            string longer = a.Length <= b.Length ? b : a;
            return longer.Length <= 4
                && longer.StartsWith( shorter[..^1], StringComparison.Ordinal );
        }

        // Slight ending drift: каханне / кахання / каханню
        string shortW = a.Length <= b.Length ? a : b;
        string longW = a.Length <= b.Length ? b : a;
        return longW.StartsWith( shortW[..^1], StringComparison.Ordinal )
            || (shortW.Length >= 5 && longW.StartsWith( shortW[..^2], StringComparison.Ordinal ));
    }

    /// <summary>Shorter title query so Tavily still finds near-matches.</summary>
    private static string SoftTitleCore( string softTitle, int maxWords )
    {
        if (string.IsNullOrWhiteSpace( softTitle ))
        {
            return string.Empty;
        }

        string[] words = softTitle.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
        if (words.Length <= maxWords)
        {
            return softTitle;
        }

        return string.Join( " ", words.Take( maxWords ) );
    }

    private static IReadOnlyList<string> SoftTitleWords( string? title )
    {
        if (string.IsNullOrWhiteSpace( title ))
        {
            return Array.Empty<string>();
        }

        // Strip punctuation/dashes/quotes so "Сны…" ≈ "Сны" and "А-Б" splits cleanly.
        return NormalizeForMatch( title )
            .Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
            .Select( FoldBeLetters )
            .Where( w => w.Length >= 2 )
            .Where( w => !IsStopWord( w ) )
            .ToArray();
    }

    /// <summary>
    /// Letters/digits only, lowercased, punctuation → spaces (for fuzzy title compare).
    /// </summary>
    private static string NormalizeForMatch( string? text )
    {
        if (string.IsNullOrWhiteSpace( text ))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[text.Length];
        int n = 0;
        bool prevSpace = true;
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit( c ))
            {
                buffer[n++] = char.ToLowerInvariant( c );
                prevSpace = false;
            }
            else if (!prevSpace)
            {
                buffer[n++] = ' ';
                prevSpace = true;
            }
        }

        if (n > 0 && buffer[n - 1] == ' ')
        {
            n--;
        }

        return new string( buffer[..n] );
    }

    private static bool IsStopWord( string word )
    {
        string w = word.Trim().ToLowerInvariant();
        return w is "і" or "и" or "a" or "the" or "of" or "and" or "або" or "для";
    }

    private static string AuthorLastName( string? author )
    {
        if (string.IsNullOrWhiteSpace( author ))
        {
            return string.Empty;
        }

        string[] parts = author.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        return parts.Length == 0 ? string.Empty : parts[^1];
    }

    private static IReadOnlyList<TavilySearchHit> RankHits(
        IReadOnlyList<TavilySearchHit> hits,
        BookLookupSessionState session )
    {
        return hits
            .OrderByDescending( h => ScoreHit( h, session ) )
            .ToList();
    }

    private static int ScoreHit( TavilySearchHit hit, BookLookupSessionState session )
    {
        string blob = $"{hit.Title}\n{hit.Url}\n{hit.Content}";
        string blobNorm = NormalizeForMatch( blob );
        string blobDigits = DigitsOnly( blob );
        int score = 0;

        if (!string.IsNullOrWhiteSpace( session.QueryIsbn )
            && blobDigits.Contains( session.QueryIsbn, StringComparison.Ordinal ))
        {
            score += 100;
        }

        if (SoftTitlePhraseContainedIn( hit.Title, session.QueryTitle )
            || SoftTitlePhraseContainedIn( $"{hit.Title} {hit.Content}", session.QueryTitle ))
        {
            score += 50;
        }

        string last = AuthorLastName( session.QueryAuthor );
        if (!string.IsNullOrWhiteSpace( last )
            && blobNorm.Contains( NormalizeForMatch( last ), StringComparison.Ordinal ))
        {
            score += 30;
        }

        foreach (string word in SoftTitleWords( session.QueryTitle )
            .Where( w => w.Length >= 3 )
            .Take( 6 ))
        {
            if (blobNorm.Contains( word, StringComparison.Ordinal ))
            {
                score += 8;
            }
        }

        return score;
    }

    private static string DigitsOnly( string value )
    {
        if (string.IsNullOrEmpty( value ))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[value.Length];
        int n = 0;
        foreach (char c in value)
        {
            if (char.IsDigit( c ))
            {
                buffer[n++] = c;
            }
        }

        return new string( buffer[..n] );
    }

    private async Task<BookLookupCandidateDto> NormalizeHitAsync(
        BookLookupSessionState session,
        PendingSearchHit hit,
        CancellationToken cancellationToken )
    {
        // Catalog/search titles are trustworthy when they already soft-match the OCR query.
        // Skip LLM + Cloudflare HTML for these — they were wrongly rejecting real Kamunikat hits.
        bool hitTitleMatchesQuery = SoftTitlePhraseContainedIn( hit.Title, session.QueryTitle );
        string queryAuthorLast = AuthorLastName( session.QueryAuthor );
        bool hitAuthorMatchesQuery = !string.IsNullOrWhiteSpace( queryAuthorLast )
            && queryAuthorLast.Length >= 3
            && NormalizeForMatch( hit.Title )
                .Contains( NormalizeForMatch( queryAuthorLast ), StringComparison.Ordinal );

        if (hitTitleMatchesQuery
            || (hitAuthorMatchesQuery
                && CountSoftWordHits(
                    SoftTitleWords( hit.Title ),
                    SoftTitleWords( session.QueryTitle ).Where( w => w.Length >= 3 && !IsWeakTitleWord( w ) ).ToArray() ) >= 2))
        {
            (string titleOnly, string? authorFromTitle) = SplitAuthorFromTitle(
                hit.Title,
                session.QueryAuthor );
            string catalogTitle = CleanBookTitle(
                string.IsNullOrWhiteSpace( titleOnly ) ? hit.Title : titleOnly );
            string? catalogAuthor = ResolveNominativeAuthor(
                authorFromTitle,
                session.QueryAuthor,
                hit.Title,
                string.IsNullOrWhiteSpace( session.QueryAuthor )
                    ? null
                    : HumanizeOcrName( session.QueryAuthor.Trim() ) );

            if (string.IsNullOrWhiteSpace( catalogAuthor ) && hitAuthorMatchesQuery)
            {
                catalogAuthor = FormatAuthorFirstLast(
                    HumanizeOcrName( session.QueryAuthor!.Trim() ),
                    session.QueryAuthor );
            }

            _logger.LogInformation(
                "Book lookup using catalog/search title for {Url}: {Title}",
                hit.Url,
                catalogTitle );

            string snippet = hit.Content ?? string.Empty;
            if (snippet.Length > 280)
            {
                snippet = snippet[..280];
            }

            return new BookLookupCandidateDto
            {
                Title = catalogTitle,
                Author = catalogAuthor,
                Isbn = session.QueryIsbn,
                Publisher = null,
                Url = hit.Url,
                Source = hit.Source,
                Snippet = snippet,
            };
        }

        PageTitleHints pageHints = await TryFetchPageTitleHintsAsync( hit.Url, cancellationToken );
        string pageTitleRaw = pageHints.BestRawTitle
            ?? (string.IsNullOrWhiteSpace( hit.Title ) ? session.QueryTitle : hit.Title);

        string hitBlob = $"{pageTitleRaw}\n{hit.Title}\n{hit.Url}\n{hit.Content}\n{pageHints.AuthorHint}";
        string? author = null;
        string? isbn = ExtractIsbnFromText( hitBlob );
        string? publisher = null;
        string? llmTitle = null;

        // Only keep query ISBN when the hit page actually contains those digits.
        if (string.IsNullOrWhiteSpace( isbn )
            && !string.IsNullOrWhiteSpace( session.QueryIsbn )
            && DigitsOnly( hitBlob ).Contains( session.QueryIsbn, StringComparison.Ordinal ))
        {
            isbn = session.QueryIsbn;
        }

        try
        {
            BookHitNormalize? parsed = await NormalizeHitWithLlmAsync(
                session,
                hit,
                pageHints,
                cancellationToken );
            if (parsed is not null)
            {
                // Prefer the search-hit title over Cloudflare interstitial H1.
                if (parsed.SameBook == false)
                {
                    bool catalogTitleOk = SoftTitlePhraseContainedIn( hit.Title, session.QueryTitle );
                    bool htmlTitleOk = SoftTitlePhraseContainedIn(
                        FirstNonEmpty( pageHints.H1, pageHints.OgTitle ) ?? string.Empty,
                        session.QueryTitle );
                    if (!catalogTitleOk && !htmlTitleOk)
                    {
                        _logger.LogInformation(
                            "LLM marked {Url} as different book; skipping",
                            hit.Url );
                        return new BookLookupCandidateDto
                        {
                            Title = string.Empty,
                            Url = hit.Url,
                            Source = hit.Source,
                        };
                    }

                    _logger.LogInformation(
                        "LLM marked {Url} as different book; catalog/HTML title still matches — keeping",
                        hit.Url );
                }

                if (!string.IsNullOrWhiteSpace( parsed.Title )
                    && parsed.SameBook != false)
                {
                    llmTitle = parsed.Title.Trim();
                }
                else if (!string.IsNullOrWhiteSpace( parsed.Title )
                    && SoftTitlePhraseContainedIn( parsed.Title, session.QueryTitle ))
                {
                    llmTitle = parsed.Title.Trim();
                }

                // Author only from the hit/page — never inherit query author blindly.
                if (!string.IsNullOrWhiteSpace( parsed.Author )
                    && AuthorAppearsInText( parsed.Author, hitBlob ))
                {
                    author = parsed.Author.Trim();
                }

                string? parsedIsbn = IsbnUtil.Normalize( parsed.Isbn );
                if (!string.IsNullOrWhiteSpace( parsedIsbn )
                    && DigitsOnly( hitBlob ).Contains( parsedIsbn, StringComparison.Ordinal ))
                {
                    isbn = parsedIsbn;
                }

                if (!string.IsNullOrWhiteSpace( parsed.Publisher ))
                {
                    publisher = parsed.Publisher.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "LLM normalize failed for {Url}", hit.Url );
        }

        if (string.IsNullOrWhiteSpace( author )
            && !string.IsNullOrWhiteSpace( pageHints.AuthorHint )
            && AuthorAppearsInText( pageHints.AuthorHint, hitBlob ))
        {
            author = pageHints.AuthorHint.Trim();
        }

        // Tavily/search titles are often "Author, Book Title – Shop" when Cloudflare blocks HTML fetch.
        (string hitTitleOnly, string? hitTitleAuthor) = SplitAuthorFromTitle( hit.Title, author ?? pageHints.AuthorHint );
        if (string.IsNullOrWhiteSpace( author ) && !string.IsNullOrWhiteSpace( hitTitleAuthor ))
        {
            author = hitTitleAuthor;
        }

        string? pageAuthorForSplit = author ?? pageHints.AuthorHint ?? hitTitleAuthor;
        string title = ResolveTitleFromPage(
            session.QueryTitle,
            pageAuthorForSplit,
            pageHints.H1,
            pageHints.OgTitle,
            llmTitle,
            pageHints.HtmlTitle,
            hitTitleOnly,
            hit.Title );

        // Prefer shop/search wording over ALL-CAPS OCR when soft-matched.
        if (LooksLikeOcrAllCaps( title )
            && !string.IsNullOrWhiteSpace( hitTitleOnly )
            && SoftTitlePhraseContainedIn( hitTitleOnly, session.QueryTitle ))
        {
            title = CleanBookTitle( hitTitleOnly );
        }

        // Never show the raw OCR query as if it came from the shop page.
        if (!string.IsNullOrWhiteSpace( session.QueryTitle )
            && string.Equals(
                NormalizeForMatch( title ),
                NormalizeForMatch( session.QueryTitle ),
                StringComparison.Ordinal )
            && LooksLikeOcrAllCaps( session.QueryTitle ))
        {
            string? shopTitle = FirstNonEmpty(
                pageHints.H1,
                pageHints.OgTitle,
                hitTitleOnly,
                pageHints.HtmlTitle );
            if (!string.IsNullOrWhiteSpace( shopTitle )
                && !LooksLikeOcrAllCaps( shopTitle ))
            {
                title = CleanBookTitle( SplitAuthorFromTitle( shopTitle, pageAuthorForSplit ).Title );
            }
            else
            {
                // No usable shop title — drop this hit rather than paint OCR onto a wrong URL.
                title = string.Empty;
            }
        }

        author = ResolveNominativeAuthor(
            SplitAuthorFromTitle( title, pageAuthorForSplit ).Author,
            pageHints.AuthorHint ?? hitTitleAuthor,
            hitBlob,
            author );

        // Re-split in case ResolveTitle still had an author prefix.
        (title, string? splitAuthor) = SplitAuthorFromTitle( title, author ?? pageAuthorForSplit );
        title = CleanBookTitle( title );
        if (string.IsNullOrWhiteSpace( author ) && !string.IsNullOrWhiteSpace( splitAuthor ))
        {
            author = ResolveNominativeAuthor( splitAuthor, pageHints.AuthorHint, hitBlob, null );
        }

        bool sameBookConfirmed = SoftTitlePhraseContainedIn( title, session.QueryTitle )
            || SoftTitlePhraseContainedIn( hit.Title, session.QueryTitle )
            || (!string.IsNullOrWhiteSpace( session.QueryIsbn )
                && !string.IsNullOrWhiteSpace( isbn )
                && string.Equals( isbn, session.QueryIsbn, StringComparison.Ordinal ));

        // Cover OCR author only when the page is already confirmed as the same book
        // and the shop HTML had no author (Cloudflare / thin snippet).
        if (string.IsNullOrWhiteSpace( author )
            && sameBookConfirmed
            && !string.IsNullOrWhiteSpace( session.QueryAuthor ))
        {
            author = FormatAuthorFirstLast(
                HumanizeOcrName( session.QueryAuthor.Trim() ),
                session.QueryAuthor );
        }

        // Final guard: drop authors that contradict the hit text (unless OCR fallback above).
        if (!string.IsNullOrWhiteSpace( author )
            && !sameBookConfirmed
            && !AuthorAppearsInText( author, hitBlob )
            && !AuthorAppearsInText( author, title ))
        {
            author = null;
        }

        return new BookLookupCandidateDto
        {
            Title = title,
            Author = author,
            Isbn = isbn,
            Publisher = publisher,
            Url = hit.Url,
            Source = hit.Source,
            Snippet = string.IsNullOrWhiteSpace( hit.Content )
                ? null
                : hit.Content.Length > 280
                    ? hit.Content[..280] + "…"
                    : hit.Content,
        };
    }

    /// <summary>
    /// Prefer the supplier-page title that matches the cover OCR query:
    /// page punctuation/spelling, no author/site junk. OCR is for matching only.
    /// </summary>
    private static string ResolveTitleFromPage(
        string? queryTitle,
        string? authorHint,
        params string?[] pageCandidates )
    {
        string query = (queryTitle ?? string.Empty).Trim();
        IReadOnlyList<string> queryWords = SoftTitleWords( query )
            .Where( w => w.Length >= 3 )
            .Select( FoldBeLetters )
            .ToArray();

        string? best = null;
        int bestScore = -1;

        foreach (string? raw in pageCandidates)
        {
            if (string.IsNullOrWhiteSpace( raw ))
            {
                continue;
            }

            // Never treat raw OCR query as a "page" candidate.
            if (string.Equals( raw.Trim(), query, StringComparison.OrdinalIgnoreCase )
                && LooksLikeOcrAllCaps( raw ))
            {
                continue;
            }

            (string cleaned, _) = SplitAuthorFromTitle( raw, authorHint );
            cleaned = CleanBookTitle( cleaned );
            if (string.IsNullOrWhiteSpace( cleaned ) || LooksLikeOcrAllCaps( cleaned ))
            {
                // ALL CAPS line is almost never how a shop writes the title.
                if (LooksLikeOcrAllCaps( cleaned ))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace( cleaned ))
                {
                    continue;
                }
            }

            int score = ScoreTitleAgainstQuery( cleaned, queryWords );
            if (score > bestScore
                || (score == bestScore
                    && best is not null
                    && cleaned.Length > best.Length))
            {
                bestScore = score;
                best = cleaned;
            }
        }

        // Prefer page wording when the OCR phrase is contained in the shop title
        // (punctuation/case may differ; shop title may be longer or shorter).
        if (!string.IsNullOrWhiteSpace( best )
            && SoftTitlePhraseContainedIn( best, query ))
        {
            return best;
        }

        // Fallback: enough distinctive word overlap.
        int minScore = queryWords.Count <= 2 ? Math.Max( 1, queryWords.Count ) : 2;
        if (!string.IsNullOrWhiteSpace( best ) && bestScore >= minScore)
        {
            return best;
        }

        // Never keep ALL-CAPS OCR when any decent shop/search title was found.
        if (!string.IsNullOrWhiteSpace( best )
            && LooksLikeOcrAllCaps( query )
            && bestScore >= 1)
        {
            return best;
        }

        if (!string.IsNullOrWhiteSpace( best )
            && LooksLikeOcrAllCaps( query )
            && SoftTitlePhraseContainedIn( query, best ))
        {
            return best;
        }

        // Prefer any real shop title over copying the OCR query onto a wrong page.
        if (!string.IsNullOrWhiteSpace( best ))
        {
            return best;
        }

        // No page title at all — empty so PresentNext skips (do not paint OCR on a random URL).
        return string.Empty;
    }

    private static int ScoreTitleAgainstQuery( string candidate, IReadOnlyList<string> queryWords )
    {
        if (queryWords.Count == 0)
        {
            return 0;
        }

        IReadOnlyList<string> candWords = SoftTitleWords( candidate );
        return CountSoftWordHits( candWords, queryWords );
    }

    /// <summary>Belarusian OCR often confuses ў/у — fold for matching only.</summary>
    private static string FoldBeLetters( string value )
    {
        if (string.IsNullOrEmpty( value ))
        {
            return string.Empty;
        }

        return value
            .Replace( 'ў', 'у' )
            .Replace( 'Ў', 'у' );
    }

    private static bool LooksLikeOcrAllCaps( string? text )
    {
        if (string.IsNullOrWhiteSpace( text ))
        {
            return false;
        }

        int letters = 0;
        int upper = 0;
        foreach (char c in text)
        {
            if (!char.IsLetter( c ))
            {
                continue;
            }

            letters++;
            if (char.IsUpper( c ))
            {
                upper++;
            }
        }

        return letters >= 4 && upper * 10 >= letters * 8;
    }

    private sealed class PageTitleHints
    {
        public string? OgTitle { get; init; }
        public string? HtmlTitle { get; init; }
        public string? H1 { get; init; }
        public string? AuthorHint { get; init; }

        public string? BestRawTitle =>
            FirstNonEmpty( H1, OgTitle, HtmlTitle );
    }

    private async Task<PageTitleHints> TryFetchPageTitleHintsAsync(
        string url,
        CancellationToken cancellationToken )
    {
        if (string.IsNullOrWhiteSpace( url ) || !Uri.TryCreate( url, UriKind.Absolute, out Uri? uri ))
        {
            return new PageTitleHints();
        }

        try
        {
            HttpClient client = _httpClientFactory.CreateClient( "BookLookupPage" );
            using HttpRequestMessage request = new( HttpMethod.Get, uri );
            request.Headers.Accept.Add( new MediaTypeWithQualityHeaderValue( "text/html" ) );

            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken );
            if (!response.IsSuccessStatusCode)
            {
                return new PageTitleHints();
            }

            string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!string.IsNullOrWhiteSpace( mediaType )
                && !mediaType.Contains( "html", StringComparison.OrdinalIgnoreCase )
                && !mediaType.Contains( "text", StringComparison.OrdinalIgnoreCase )
                && !mediaType.Contains( "xml", StringComparison.OrdinalIgnoreCase ))
            {
                return new PageTitleHints();
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync( cancellationToken );
            using MemoryStream buffer = new();
            byte[] chunk = new byte[8192];
            const int maxBytes = 250_000;
            int total = 0;
            while (total < maxBytes)
            {
                int read = await stream.ReadAsync(
                    chunk.AsMemory( 0, Math.Min( chunk.Length, maxBytes - total ) ),
                    cancellationToken );
                if (read <= 0)
                {
                    break;
                }

                buffer.Write( chunk, 0, read );
                total += read;
            }

            string html = Encoding.UTF8.GetString( buffer.ToArray() );
            // Retry with charset from meta if needed — UTF-8 covers most shop pages.
            string? og = ExtractMetaContent( html, "og:title" )
                ?? ExtractMetaContent( html, "twitter:title" );
            string? htmlTitle = ExtractHtmlTagText( html, "title" );
            string? h1 = ExtractHtmlTagText( html, "h1" );
            string? authorHint = ExtractMetaContent( html, "book:author" )
                ?? ExtractMetaContent( html, "author" )
                ?? ExtractRelAuthor( html );

            return new PageTitleHints
            {
                OgTitle = DecodeHtml( og ),
                HtmlTitle = DecodeHtml( htmlTitle ),
                H1 = DecodeHtml( h1 ),
                AuthorHint = DecodeHtml( authorHint ),
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug( ex, "Could not fetch page title hints for {Url}", url );
            return new PageTitleHints();
        }
    }

    private static string? FirstNonEmpty( params string?[] values )
    {
        foreach (string? v in values)
        {
            if (!string.IsNullOrWhiteSpace( v ))
            {
                return v.Trim();
            }
        }

        return null;
    }

    private static string? ExtractMetaContent( string html, string propertyOrName )
    {
        string pattern =
            $@"<meta\s[^>]*(?:property|name)\s*=\s*[""']{Regex.Escape( propertyOrName )}[""'][^>]*content\s*=\s*[""'](?<v>[^""']+)[""'][^>]*>|<meta\s[^>]*content\s*=\s*[""'](?<v>[^""']+)[""'][^>]*(?:property|name)\s*=\s*[""']{Regex.Escape( propertyOrName )}[""'][^>]*>";
        Match m = Regex.Match( html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline );
        return m.Success ? m.Groups["v"].Value.Trim() : null;
    }

    private static string? ExtractHtmlTagText( string html, string tag )
    {
        Match m = Regex.Match(
            html,
            $@"<{tag}\b[^>]*>(?<v>.*?)</{tag}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline );
        if (!m.Success)
        {
            return null;
        }

        string inner = Regex.Replace( m.Groups["v"].Value, "<[^>]+>", " " );
        return Regex.Replace( inner, @"\s+", " " ).Trim();
    }

    private static string? ExtractRelAuthor( string html )
    {
        Match m = Regex.Match(
            html,
            @"<a\b[^>]*rel\s*=\s*[""'][^""']*author[^""']*[""'][^>]*>(?<v>.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline );
        if (!m.Success)
        {
            // Kamunikat-style: author link often right under h1; grab first product author link text.
            m = Regex.Match(
                html,
                @"<a\b[^>]*href\s*=\s*[""'][^""']*(?:author|autor|auhtar|аўтар)[^""']*[""'][^>]*>(?<v>.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline );
        }

        if (!m.Success)
        {
            return null;
        }

        string inner = Regex.Replace( m.Groups["v"].Value, "<[^>]+>", " " );
        return Regex.Replace( inner, @"\s+", " " ).Trim();
    }

    private static string? DecodeHtml( string? value )
    {
        if (string.IsNullOrWhiteSpace( value ))
        {
            return value;
        }

        return System.Net.WebUtility.HtmlDecode( value )?.Trim();
    }

    private static string StripHtmlTags( string? value )
    {
        if (string.IsNullOrWhiteSpace( value ))
        {
            return string.Empty;
        }

        string text = Regex.Replace( value, "<[^>]+>", " " );
        return Regex.Replace( text, @"\s+", " " ).Trim();
    }

    /// <summary>
    /// Strip site junk (" - Facebook") and keep the book title only.
    /// </summary>
    private static string CleanBookTitle( string? title )
    {
        if (string.IsNullOrWhiteSpace( title ))
        {
            return string.Empty;
        }

        string t = title.Trim();
        t = Regex.Replace(
            t,
            @"\s*[-–—|•]\s*(Facebook|Instagram|Twitter|X|YouTube|VK|Telegram|LinkedIn)\s*$",
            string.Empty,
            RegexOptions.IgnoreCase );
        t = Regex.Replace(
            t,
            @"\s*[-–—|]\s*(Home|Галоўная|Strona główna|Kamunikat\s*Shop|Kamunikat)\s*$",
            string.Empty,
            RegexOptions.IgnoreCase );
        t = Regex.Replace(
            t,
            @"\s*[|•]\s*Kamunikat(?:\s*Shop)?\s*$",
            string.Empty,
            RegexOptions.IgnoreCase );
        return t.Trim().Trim( ',', ';', '.', ':', '—' ).Trim();
    }

    /// <summary>
    /// "Мікалай Статкевіч, І гэта ўсё ў табе…" → title without author prefix.
    /// </summary>
    private static (string Title, string? Author) SplitAuthorFromTitle(
        string? title,
        string? knownAuthor )
    {
        if (string.IsNullOrWhiteSpace( title ))
        {
            return (string.Empty, null);
        }

        string t = CleanBookTitle( title.Trim() );

        if (!string.IsNullOrWhiteSpace( knownAuthor ))
        {
            string author = knownAuthor.Trim();
            foreach (string variant in AuthorNameVariants( author ))
            {
                if (t.StartsWith( variant + ",", StringComparison.OrdinalIgnoreCase )
                    || t.StartsWith( variant + " —", StringComparison.OrdinalIgnoreCase )
                    || t.StartsWith( variant + " –", StringComparison.OrdinalIgnoreCase )
                    || t.StartsWith( variant + " -", StringComparison.OrdinalIgnoreCase )
                    || t.StartsWith( variant + ":", StringComparison.OrdinalIgnoreCase ))
                {
                    int cut = variant.Length;
                    while (cut < t.Length && (t[cut] is ',' or ':' or '—' or '–' or '-' or ' '))
                    {
                        cut++;
                    }

                    string rest = t[cut..].Trim();
                    if (rest.Length >= 3)
                    {
                        return (rest, author);
                    }
                }
            }

            // Prefix before comma contains the known author last name.
            int commaKnown = t.IndexOf( ',', StringComparison.Ordinal );
            if (commaKnown > 0 && commaKnown < t.Length - 3)
            {
                string prefix = t[..commaKnown].Trim();
                string last = AuthorLastName( author );
                if (!string.IsNullOrWhiteSpace( last )
                    && last.Length >= 3
                    && prefix.Contains( last, StringComparison.OrdinalIgnoreCase ))
                {
                    return (t[(commaKnown + 1)..].Trim(), FormatAuthorFirstLast( prefix, author ));
                }
            }
        }

        // Heuristic: "Firstname Lastname, Book title…" (Kamunikat / search hit titles).
        int comma = t.IndexOf( ',', StringComparison.Ordinal );
        if (comma > 0 && comma < t.Length - 3)
        {
            string prefix = t[..comma].Trim();
            string rest = t[(comma + 1)..].Trim();
            string[] parts = prefix.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
            if (parts.Length is 2 or 3
                && rest.Length >= 3
                && parts.All( p => p.Length >= 2 && p.Any( char.IsLetter ) )
                && (parts.Any( LooksLikeSurname ) || parts.Length == 2))
            {
                return (rest, FormatAuthorFirstLast( prefix, knownAuthor ));
            }
        }

        return (t, null);
    }

    private static IEnumerable<string> AuthorNameVariants( string author )
    {
        yield return author;
        string[] parts = author.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        if (parts.Length >= 2)
        {
            // "Статкевіч Мікалай" ↔ "Мікалай Статкевіч"
            yield return string.Join( " ", parts.Reverse() );
            if (parts.Length == 2)
            {
                yield return $"{parts[0]} {parts[1]}";
            }
        }
    }

    /// <summary>
    /// Prefer shop-page author spelling/case. OCR ALL CAPS is humanized only as last resort
    /// by the caller when the same book is already confirmed.
    /// </summary>
    private static string? ResolveNominativeAuthor(
        string? splitFromTitle,
        string? pageAuthor,
        string hitBlob,
        string? llmAuthor )
    {
        string?[] pageFirst =
        [
            splitFromTitle,
            pageAuthor,
            llmAuthor,
        ];

        foreach (string? raw in pageFirst)
        {
            if (string.IsNullOrWhiteSpace( raw ))
            {
                continue;
            }

            string normalized = LooksLikeOcrAllCaps( raw ) ? HumanizeOcrName( raw.Trim() ) : raw.Trim();

            // Author may appear only in the search-hit title after Cloudflare blocks HTML.
            if (!AuthorAppearsInText( normalized, hitBlob )
                && !AuthorAppearsInText( raw, hitBlob ))
            {
                // Still accept when this value itself came from splitting that hit title.
                if (!string.Equals( splitFromTitle?.Trim(), raw.Trim(), StringComparison.OrdinalIgnoreCase )
                    && !string.Equals( pageAuthor?.Trim(), raw.Trim(), StringComparison.OrdinalIgnoreCase ))
                {
                    continue;
                }
            }

            string candidate = FormatAuthorFirstLast( normalized, splitFromTitle ?? pageAuthor );
            if (!LooksGenitivePersonName( candidate ))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Convert OCR "МІКАЛАЙ СТАТКЕВІЧ" → "Мікалай Статкевіч".</summary>
    private static string HumanizeOcrName( string name )
    {
        if (string.IsNullOrWhiteSpace( name ))
        {
            return name;
        }

        if (!LooksLikeOcrAllCaps( name ))
        {
            return name.Trim();
        }

        string[] parts = name.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            if (p.Length == 0)
            {
                continue;
            }

            parts[i] = char.ToUpperInvariant( p[0] ) + p[1..].ToLowerInvariant();
        }

        return string.Join( " ", parts );
    }

    private static string FormatAuthorFirstLast( string author, string? preferredOrderHint )
    {
        string[] parts = author.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        if (parts.Length != 2)
        {
            return author;
        }

        // Reorder using hint, but keep the casing from `author` (page), not from OCR hint.
        if (!string.IsNullOrWhiteSpace( preferredOrderHint ))
        {
            string[] hintParts = preferredOrderHint.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
            if (hintParts.Length == 2
                && SameAuthorToken( parts[0], hintParts[1] )
                && SameAuthorToken( parts[1], hintParts[0] ))
            {
                return $"{parts[1]} {parts[0]}";
            }
        }

        if (LooksLikeSurname( parts[0] ) && !LooksLikeSurname( parts[1] ))
        {
            return $"{parts[1]} {parts[0]}";
        }

        return author;
    }

    private static bool LooksLikeSurname( string token )
    {
        string t = token.Trim().ToLowerInvariant();
        return t.EndsWith( "віч", StringComparison.Ordinal )
            || t.EndsWith( "вич", StringComparison.Ordinal )
            || t.EndsWith( "скі", StringComparison.Ordinal )
            || t.EndsWith( "ская", StringComparison.Ordinal )
            || t.EndsWith( "cki", StringComparison.Ordinal )
            || t.EndsWith( "ska", StringComparison.Ordinal )
            || t.EndsWith( "ski", StringComparison.Ordinal );
    }

    private static bool LooksGenitivePersonName( string author )
    {
        string[] parts = author.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        if (parts.Length < 2)
        {
            return false;
        }

        // Masculine genitive in be/ru often: Мікалая Статкевіча (both end with а/я).
        static bool EndsSoftGenitive( string p )
        {
            string x = p.Trim().ToLowerInvariant();
            return x.EndsWith( 'а' ) || x.EndsWith( 'я' ) || x.EndsWith( 'у' ) || x.EndsWith( 'ю' );
        }

        return parts.Count( EndsSoftGenitive ) >= 2;
    }

    private static bool SameAuthorPerson( string? a, string? b )
    {
        if (string.IsNullOrWhiteSpace( a ) || string.IsNullOrWhiteSpace( b ))
        {
            return false;
        }

        return SameAuthorToken( AuthorLastName( a ), AuthorLastName( b ) );
    }

    private static bool SameAuthorToken( string? a, string? b )
    {
        if (string.IsNullOrWhiteSpace( a ) || string.IsNullOrWhiteSpace( b ))
        {
            return false;
        }

        string na = AuthorTokenStem( a );
        string nb = AuthorTokenStem( b );
        if (na.Length < 3 || nb.Length < 3)
        {
            return false;
        }

        return na == nb
            || na.StartsWith( nb, StringComparison.Ordinal )
            || nb.StartsWith( na, StringComparison.Ordinal );
    }

    private static string AuthorTokenStem( string token )
    {
        string t = NormalizeForMatch( token );
        // Strip common be/ru case endings for surname/given-name matching.
        string[] endings = ["ого", "ему", "ом", "ем", "ой", "ей", "ую", "юю", "ая", "яя", "а", "я", "у", "ю", "е", "і", "ы"];
        foreach (string end in endings)
        {
            if (t.Length > end.Length + 3 && t.EndsWith( end, StringComparison.Ordinal ))
            {
                return t[..^end.Length];
            }
        }

        return t;
    }

    private static bool AuthorAppearsInText( string author, string text )
    {
        if (string.IsNullOrWhiteSpace( author ) || string.IsNullOrWhiteSpace( text ))
        {
            return false;
        }

        string last = AuthorLastName( author );
        if (!string.IsNullOrWhiteSpace( last )
            && last.Length >= 3
            && text.Contains( last, StringComparison.OrdinalIgnoreCase ))
        {
            return true;
        }

        // Full name as written (e.g. "Уладзімір Караткевіч").
        return text.Contains( author.Trim(), StringComparison.OrdinalIgnoreCase );
    }

    private static bool IsRelevantCandidate(
        BookLookupSessionState session,
        PendingSearchHit hit,
        BookLookupCandidateDto candidate )
    {
        // Match against the hit page only — never against query fields copied onto the candidate.
        string blob = $"{hit.Title}\n{hit.Url}\n{hit.Content}\n{candidate.Title}\n{candidate.Author}";
        string blobNorm = NormalizeForMatch( blob );
        string digits = DigitsOnly( blob );
        string last = AuthorLastName( session.QueryAuthor );
        string lastNorm = NormalizeForMatch( last );
        bool onSupplierSite = session.SupplierDomains.Count > 0
            && IsSupplierHost( hit.Url, session.SupplierDomains );
        bool fromImageSearch = string.Equals( hit.Source, "image", StringComparison.OrdinalIgnoreCase )
            || string.Equals( hit.Source, "manual", StringComparison.OrdinalIgnoreCase )
            || string.Equals( hit.Source, "slug", StringComparison.OrdinalIgnoreCase );

        bool isbnMatch = !string.IsNullOrWhiteSpace( session.QueryIsbn )
            && digits.Contains( session.QueryIsbn, StringComparison.Ordinal );

        bool authorMatch = !string.IsNullOrWhiteSpace( lastNorm )
            && lastNorm.Length >= 3
            && (blobNorm.Contains( lastNorm, StringComparison.Ordinal )
                || FoldBeLetters( blobNorm ).Contains( FoldBeLetters( lastNorm ), StringComparison.Ordinal )
                || BlobHasAuthorStem( blobNorm, lastNorm ));

        // Title signals from product titles only — not from long descriptions
        // (descriptions often contain generic words like «зборнік» and false-match).
        bool titlePhraseInCandidate = SoftTitlePhraseContainedIn( candidate.Title, session.QueryTitle );
        bool titlePhraseInHit = SoftTitlePhraseContainedIn( hit.Title, session.QueryTitle );
        bool titlePhraseMatch = titlePhraseInCandidate || titlePhraseInHit;

        IReadOnlyList<string> titleWords = SoftTitleWords( session.QueryTitle )
            .Where( w => w.Length >= 3 && !IsWeakTitleWord( w ) )
            .ToArray();
        IReadOnlyList<string> titleFieldWords = SoftTitleWords( $"{candidate.Title} {hit.Title}" );
        int titleHits = CountSoftWordHits( titleFieldWords, titleWords );

        if (isbnMatch)
        {
            return true;
        }

        // Different author on the page than cover OCR → always a different book.
        string candidateLast = NormalizeForMatch( AuthorLastName( candidate.Author ) );
        string hitAuthorLast = NormalizeForMatch(
            AuthorLastName( SplitAuthorFromTitle( hit.Title, null ).Author ) );
        string pageAuthorLast = !string.IsNullOrWhiteSpace( candidateLast )
            ? candidateLast
            : hitAuthorLast;
        if (!string.IsNullOrWhiteSpace( lastNorm )
            && lastNorm.Length >= 3
            && !string.IsNullOrWhiteSpace( pageAuthorLast )
            && pageAuthorLast.Length >= 3
            && !string.Equals( lastNorm, pageAuthorLast, StringComparison.Ordinal )
            && !AuthorTokenStemsMatch( lastNorm, pageAuthorLast ))
        {
            return false;
        }

        // Soft title phrase containment is enough (shop title may be longer / punctuated).
        if (titlePhraseMatch)
        {
            return true;
        }

        if (fromImageSearch)
        {
            if (onSupplierSite && (authorMatch || titleHits >= 2 || (titleWords.Count <= 1 && titleHits >= 1)))
            {
                return true;
            }

            if (!onSupplierSite && (titleHits >= 2 || (authorMatch && titleHits >= 1)))
            {
                return true;
            }

            if (string.Equals( hit.Source, "manual", StringComparison.OrdinalIgnoreCase )
                && !string.IsNullOrWhiteSpace( candidate.Title ))
            {
                return true;
            }
        }

        if (onSupplierSite)
        {
            if (authorMatch && titleHits >= 1)
            {
                return true;
            }

            // Need real title overlap — one generic word is not enough.
            int titleNeed = titleWords.Count <= 2
                ? Math.Max( 1, titleWords.Count )
                : 2;
            if (titleHits >= titleNeed && titleNeed > 0)
            {
                return true;
            }

            if (authorMatch && titleWords.Count == 0)
            {
                return true;
            }

            return false;
        }

        int titleNeedWithAuthor = titleWords.Count <= 2 ? Math.Max( 1, titleWords.Count ) : 2;
        if (authorMatch && titleHits >= titleNeedWithAuthor)
        {
            return true;
        }

        int titleNeedAlone = titleWords.Count <= 2
            ? titleWords.Count
            : 2;
        if (titleHits >= titleNeedAlone && titleNeedAlone > 0)
        {
            return true;
        }

        return false;
    }

    private static bool IsWeakTitleWord( string word )
    {
        string w = FoldBeLetters( word.Trim().ToLowerInvariant() );
        return w is "зборнік" or "сборник" or "кніга" or "книга" or "book" or "том"
            or "частка" or "часть" or "выданне" or "издание" or "найлепшае"
            || IsSlugGenreWord( w );
    }

    private static bool BlobHasAuthorStem( string blobNorm, string authorLastNorm )
    {
        string stem = AuthorTokenStem( FoldBeLetters( authorLastNorm ) );
        if (stem.Length < 3)
        {
            return false;
        }

        foreach (string word in SoftTitleWords( blobNorm ))
        {
            if (AuthorTokenStemsMatch( stem, FoldBeLetters( word ) ))
            {
                return true;
            }
        }

        return FoldBeLetters( blobNorm ).Contains( stem, StringComparison.Ordinal );
    }

    private static bool AuthorTokenStemsMatch( string a, string b )
    {
        string sa = AuthorTokenStem( a );
        string sb = AuthorTokenStem( b );
        return sa.Length >= 3
            && sb.Length >= 3
            && (sa.StartsWith( sb, StringComparison.Ordinal )
                || sb.StartsWith( sa, StringComparison.Ordinal ));
    }

    private static string? ExtractIsbnFromText( string text )
    {
        if (string.IsNullOrWhiteSpace( text ))
        {
            return null;
        }

        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            text,
            @"\b97[89][\d\- ]{10,20}\b" ))
        {
            string? normalized = IsbnUtil.Normalize( m.Value );
            if (!string.IsNullOrWhiteSpace( normalized ))
            {
                return normalized;
            }
        }

        return null;
    }

    private async Task<BookHitNormalize?> NormalizeHitWithLlmAsync(
        BookLookupSessionState session,
        PendingSearchHit hit,
        PageTitleHints pageHints,
        CancellationToken cancellationToken )
    {
        string apiKey = RequireGroqApiKey();
        string model = ResolveTextModel();

        const string systemPrompt =
            """
            Extract the BOOK TITLE and author from the supplier product page.
            Reply with ONE JSON object: {"title":"...","author":null,"isbn":null,"publisher":null,"sameBook":true}.

            title rules (critical):
            - The Query title comes from the book COVER (OCR). Use it only to identify WHICH book.
            - title MUST be taken from the page (h1 / og:title / product heading), with the page's own
              punctuation, capitalization, and wording (e.g. full subtitle as on the shop page).
            - Do NOT copy the raw OCR query string if the page has a cleaner/fuller title for the same book.
            - title = book name ONLY — no author, no " - Facebook", no shop/site name, no breadcrumbs.
            - If heading is "Author Name, Book Title. Subtitle", return only "Book Title. Subtitle".
            - Keep original spelling (Belarusian/Polish/Russian). Do not translate or invent words.

            author rules (critical):
            - author MUST be nominative case: "Мікалай Статкевіч", NEVER "Мікалая Статкевіча".
            - Prefer "Firstname Lastname". Prefer author link / heading over prose.

            sameBook=false if the page is clearly a different book than the Query title/author/ISBN.
            Never invent ISBN/author that do not appear on the page.
            """;

        string userPrompt =
            $"""
            Query title (from book COVER OCR — match this book on the page): {session.QueryTitle}
            Query author: {session.QueryAuthor ?? ""}
            Query ISBN: {session.QueryIsbn ?? ""}

            Page h1: {pageHints.H1 ?? ""}
            Page og:title: {pageHints.OgTitle ?? ""}
            Page <title>: {pageHints.HtmlTitle ?? ""}
            Page author hint: {pageHints.AuthorHint ?? ""}

            Search hit title: {hit.Title}
            Hit URL: {hit.Url}
            Hit snippet: {hit.Content}
            """;

        object payload = new
        {
            model,
            temperature = 0,
            max_completion_tokens = 400,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            }
        };

        string body = await SendGroqChatAsync( apiKey, payload, cancellationToken );
        if (!TryGetMessageContent( body, out string? content ) || string.IsNullOrWhiteSpace( content ))
        {
            return null;
        }

        string json = ExtractJsonObject( content );
        return JsonSerializer.Deserialize<BookHitNormalize>( json, JsonOptions );
    }

    private async Task<BookVisionExtract> ExtractFromImagesAsync(
        byte[] coverBytes,
        string? coverContentType,
        byte[]? isbnBytes,
        string? isbnContentType,
        CancellationToken cancellationToken )
    {
        string apiKey = RequireGroqApiKey();
        IReadOnlyList<string> models = ResolveVisionModelCandidates();

        List<object> contentParts = new()
        {
            new
            {
                type = "text",
                text =
                    """
                    You OCR a book COVER (image 1) and optional ISBN/barcode photo (image 2).
                    Reply with ONE JSON object only, no markdown:
                    {"title":"...","author":null,"isbn":null,"language":null}

                    Layout rules:
                    - author = person name, often the LARGEST text (e.g. top of cover).
                    - title = book title + subtitle lines (often smaller text). Join title lines with a single space.
                    - Do NOT put the author into title.
                    - isbn = ISBN-10/13 digits only from image 2 (or cover if clearly printed). Else null.

                    Spelling (critical for Belarusian/Polish/Russian Cyrillic):
                    - Copy letters EXACTLY as printed. Keep word spaces: "І ГЭТА" not "ІГЗАТА".
                    - Belarusian: distinguish І vs И, Ў vs У, ё if present. Prefer Ў when the printed letter is short U (ў).
                    - Do not invent, translate, or autocorrect words.
                    - language = be|pl|ru|en|null when clear.
                    """
            },
            new
            {
                type = "image_url",
                image_url = new
                {
                    url = ToDataUrl( coverBytes, coverContentType )
                }
            }
        };

        if (isbnBytes is { Length: > 0 })
        {
            contentParts.Add( new
            {
                type = "image_url",
                image_url = new
                {
                    url = ToDataUrl( isbnBytes, isbnContentType )
                }
            } );
        }

        Exception? lastError = null;
        foreach (string model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object payload = new
            {
                model,
                temperature = 0,
                max_completion_tokens = 700,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = contentParts
                    }
                }
            };

            try
            {
                string body = await SendGroqChatAsync( apiKey, payload, cancellationToken );
                if (!TryGetMessageContent( body, out string? content ) || string.IsNullOrWhiteSpace( content ))
                {
                    throw new InvalidOperationException( "Vision-мадэль не вярнула адказ." );
                }

                string json = ExtractJsonObject( content );
                BookVisionExtract? parsed = JsonSerializer.Deserialize<BookVisionExtract>( json, JsonOptions );
                if (parsed is null)
                {
                    throw new InvalidOperationException( "Не ўдалося разабраць адказ vision-мадэлі." );
                }

                parsed.Title = NormalizeOcrText( parsed.Title );
                parsed.Author = string.IsNullOrWhiteSpace( parsed.Author )
                    ? null
                    : NormalizeOcrText( parsed.Author );
                parsed.Isbn = IsbnUtil.Normalize( parsed.Isbn );

                if (!string.Equals( model, models[0], StringComparison.OrdinalIgnoreCase ))
                {
                    _logger.LogInformation( "Vision OCR succeeded with fallback model {Model}", model );
                }

                return parsed;
            }
            catch (Exception ex) when (IsTransientGroqFailure( ex ))
            {
                lastError = ex;
                _logger.LogWarning(
                    ex,
                    "Vision OCR transient failure on model {Model}; trying next fallback if any",
                    model );
            }
        }

        throw lastError
            ?? new InvalidOperationException( "Не ўдалося прачытаць вокладку праз Groq." );
    }

    private static bool IsTransientGroqFailure( Exception ex )
    {
        string msg = ex.Message ?? string.Empty;
        return msg.Contains( "503", StringComparison.Ordinal )
            || msg.Contains( "502", StringComparison.Ordinal )
            || msg.Contains( "429", StringComparison.Ordinal )
            || msg.Contains( "over capacity", StringComparison.OrdinalIgnoreCase )
            || msg.Contains( "rate limit", StringComparison.OrdinalIgnoreCase )
            || msg.Contains( "занадта шмат", StringComparison.OrdinalIgnoreCase )
            || msg.Contains( "перагружаны", StringComparison.OrdinalIgnoreCase );
    }

    private static string NormalizeOcrText( string? value )
    {
        if (string.IsNullOrWhiteSpace( value ))
        {
            return string.Empty;
        }

        // Collapse whitespace only — do not "fix" letters (OCR mistakes stay editable by user).
        return System.Text.RegularExpressions.Regex
            .Replace( value.Trim(), @"\s+", " " );
    }

    private async Task<string> SendGroqChatAsync(
        string apiKey,
        object payload,
        CancellationToken cancellationToken )
    {
        HttpClient client = _httpClientFactory.CreateClient( "Groq" );
        const int maxAttempts = 4;
        string? lastBody = null;
        int lastStatus = 0;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                "https://api.groq.com/openai/v1/chat/completions" );
            request.Headers.Authorization = new AuthenticationHeaderValue( "Bearer", apiKey );
            request.Content = new StringContent(
                JsonSerializer.Serialize( payload ),
                Encoding.UTF8,
                "application/json" );

            using HttpResponseMessage response = await client.SendAsync( request, cancellationToken );
            string body = await response.Content.ReadAsStringAsync( cancellationToken );
            if (response.IsSuccessStatusCode)
            {
                return body;
            }

            lastBody = body;
            lastStatus = (int)response.StatusCode;
            _logger.LogWarning(
                "Groq book lookup failed attempt {Attempt}/{Max}: {Status} {Body}",
                attempt,
                maxAttempts,
                lastStatus,
                body );

            if (lastStatus == 404)
            {
                string modelHint = TryReadPayloadModel( payload ) ?? "(невядома)";
                throw new InvalidOperationException(
                    $"Groq API памылка: 404 (мадэль «{modelHint}» не знойдзена). " +
                    "Праверце Groq:VisionModel / GROQ_VISION_MODEL." );
            }

            bool retriable = lastStatus is 429 or 502 or 503 or 529;
            if (!retriable || attempt >= maxAttempts)
            {
                break;
            }

            int delayMs = TryReadRetryAfterMs( response )
                ?? TryReadRetryAfterMsFromBody( body )
                ?? (int)Math.Min( 12_000, 800 * Math.Pow( 2, attempt - 1 ) );
            _logger.LogInformation(
                "Groq transient {Status}; waiting {DelayMs}ms before retry",
                lastStatus,
                delayMs );
            await Task.Delay( delayMs, cancellationToken );
        }

        if (lastStatus == 429)
        {
            throw new InvalidOperationException(
                "Groq API: занадта шмат запытаў (429). Пачакайце 20–30 секунд і паўтарыце." );
        }

        if (lastStatus is 502 or 503 or 529)
        {
            throw new InvalidOperationException(
                $"Groq API памылка: {lastStatus} (перагружаны). Пачакайце хвіліну і паўтарыце, або ўвядзіце назву ўручную." );
        }

        throw new InvalidOperationException(
            $"Groq API памылка: {lastStatus}." );
    }

    private static int? TryReadRetryAfterMs( HttpResponseMessage response )
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta
            && delta > TimeSpan.Zero)
        {
            return (int)Math.Clamp( delta.TotalMilliseconds, 500, 30_000 );
        }

        if (response.Headers.TryGetValues( "Retry-After", out IEnumerable<string>? values ))
        {
            string? raw = values.FirstOrDefault();
            if (double.TryParse(
                    raw,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double seconds )
                && seconds > 0)
            {
                return (int)Math.Clamp( seconds * 1000, 500, 30_000 );
            }
        }

        return null;
    }

    private static int? TryReadRetryAfterMsFromBody( string body )
    {
        if (string.IsNullOrWhiteSpace( body ))
        {
            return null;
        }

        // Groq: "Please try again in 2.5s" / "try again in 1s"
        Match m = Regex.Match(
            body,
            @"try again in\s+([0-9]+(?:\.[0-9]+)?)\s*s",
            RegexOptions.IgnoreCase );
        if (m.Success
            && double.TryParse(
                m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double seconds )
            && seconds > 0)
        {
            return (int)Math.Clamp( ( seconds + 0.25 ) * 1000, 500, 30_000 );
        }

        return null;
    }

    private static string? TryReadPayloadModel( object payload )
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse( JsonSerializer.Serialize( payload ) );
            if (doc.RootElement.TryGetProperty( "model", out JsonElement model ))
            {
                return model.GetString();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private string RequireGroqApiKey()
    {
        string apiKey = (_config["Groq:ApiKey"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( apiKey ))
        {
            throw new InvalidOperationException(
                "Groq API key не наладжаны (Groq:ApiKey / GROQ_API_KEY)." );
        }

        return apiKey;
    }

    private string ResolveTextModel()
    {
        string model = (_config["Groq:Model"] ?? "openai/gpt-oss-20b").Trim();
        return string.IsNullOrWhiteSpace( model ) ? "openai/gpt-oss-20b" : model;
    }

    private string ResolveVisionModel()
    {
        string model = (_config["Groq:VisionModel"] ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace( model ))
        {
            return model;
        }

        // Current Groq vision model (preview). Override via Groq:VisionModel.
        return "qwen/qwen3.8-27b";
    }

    private IReadOnlyList<string> ResolveVisionModelCandidates()
    {
        List<string> models = new();
        void Add( string? raw )
        {
            string m = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( m ))
            {
                return;
            }

            if (!models.Contains( m, StringComparer.OrdinalIgnoreCase ))
            {
                models.Add( m );
            }
        }

        Add( ResolveVisionModel() );
        // Fallbacks when primary is over capacity (503).
        Add( "meta-llama/llama-4-scout-17b-16e-instruct" );
        Add( "meta-llama/llama-4-maverick-17b-128e-instruct" );
        return models;
    }

    private int ReadInt( string key, int fallback )
    {
        string? raw = _config[key];
        return int.TryParse( raw, out int value ) && value > 0 ? value : fallback;
    }

    private bool ReadBool( string key, bool defaultValue )
    {
        string? raw = _config[key];
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return defaultValue;
        }

        return bool.TryParse( raw, out bool value ) ? value : defaultValue;
    }

    private int MaxAttemptsBudget( BookLookupSessionState session )
    {
        int maxSupplier = ReadInt( "BookLookup:MaxSupplierSearches", DefaultMaxSupplierSearches );
        int maxWeb = ReadInt( "BookLookup:MaxWebSearches", DefaultMaxWebSearches );
        int maxPresented = ReadInt( "BookLookup:MaxPresentedHits", DefaultMaxPresentedHits );
        int searchSlots = (session.SupplierDomains.Count > 0 ? maxSupplier : 0) + maxWeb;
        return Math.Min( maxPresented, Math.Max( searchSlots * 2, 3 ) );
    }

    private static void ValidateImage( IFormFile file, string fieldName )
    {
        if (file.Length <= 0)
        {
            throw new InvalidOperationException( $"Файл {fieldName} пусты." );
        }

        if (file.Length > MaxFileBytes)
        {
            throw new InvalidOperationException( $"Файл {fieldName} занадта вялікі (макс. 8 MB)." );
        }

        string contentType = (file.ContentType ?? string.Empty).Trim().ToLowerInvariant();
        if (!contentType.StartsWith( "image/", StringComparison.Ordinal ))
        {
            throw new InvalidOperationException( $"Файл {fieldName} павінен быць выявай." );
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync( IFormFile file, CancellationToken ct )
    {
        await using Stream stream = file.OpenReadStream();
        using MemoryStream ms = new();
        await stream.CopyToAsync( ms, ct );
        return ms.ToArray();
    }

    private static string ToDataUrl( byte[] bytes, string? contentType )
    {
        string mime = string.IsNullOrWhiteSpace( contentType ) ? "image/jpeg" : contentType.Trim();
        return $"data:{mime};base64,{Convert.ToBase64String( bytes )}";
    }

    private static string GuessImageFileName( string? contentType )
    {
        string mime = (contentType ?? string.Empty).ToLowerInvariant();
        if (mime.Contains( "png" )) return "cover.png";
        if (mime.Contains( "webp" )) return "cover.webp";
        if (mime.Contains( "gif" )) return "cover.gif";
        return "cover.jpg";
    }

    private static string? TryGetHost( string? url )
    {
        if (string.IsNullOrWhiteSpace( url ))
        {
            return null;
        }

        string trimmed = url.Trim();
        if (!trimmed.Contains( "://", StringComparison.Ordinal ))
        {
            trimmed = "https://" + trimmed;
        }

        if (!Uri.TryCreate( trimmed, UriKind.Absolute, out Uri? uri ))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace( uri.Host ) ? null : uri.Host;
    }

    /// <summary>
    /// Hosts for Tavily include_domains: apex + www variant, www stripped for matching.
    /// </summary>
    private static IEnumerable<string> ExpandHosts( string? url )
    {
        string? host = TryGetHost( url );
        if (string.IsNullOrWhiteSpace( host ))
        {
            yield break;
        }

        string normalized = StripWww( host );
        yield return normalized;
        if (!normalized.Equals( host, StringComparison.OrdinalIgnoreCase ))
        {
            yield return host.ToLowerInvariant();
        }

        yield return "www." + normalized;
    }

    private static string StripWww( string host )
    {
        string h = host.Trim().ToLowerInvariant();
        return h.StartsWith( "www.", StringComparison.Ordinal )
            ? h["www.".Length..]
            : h;
    }

    private static bool IsSupplierHost( string url, IReadOnlyList<string> domains )
    {
        if (string.IsNullOrWhiteSpace( url ) || domains.Count == 0)
        {
            return false;
        }

        if (!Uri.TryCreate( url, UriKind.Absolute, out Uri? uri )
            || string.IsNullOrWhiteSpace( uri.Host ))
        {
            return false;
        }

        string hitHost = StripWww( uri.Host );
        foreach (string domain in domains)
        {
            string d = StripWww( domain );
            if (hitHost.Equals( d, StringComparison.OrdinalIgnoreCase )
                || hitHost.EndsWith( "." + d, StringComparison.OrdinalIgnoreCase ))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeUrlKey( string url )
    {
        if (!Uri.TryCreate( url, UriKind.Absolute, out Uri? uri ))
        {
            return url.Trim().ToLowerInvariant();
        }

        string path = uri.AbsolutePath.TrimEnd( '/' );
        return $"{uri.Host.ToLowerInvariant()}{path.ToLowerInvariant()}";
    }

    private static bool TryGetMessageContent( string body, out string? content )
    {
        content = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse( body );
            content = doc.RootElement
                .GetProperty( "choices" )[0]
                .GetProperty( "message" )
                .GetProperty( "content" )
                .GetString();
            return !string.IsNullOrWhiteSpace( content );
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractJsonObject( string content )
    {
        string trimmed = content.Trim();
        if (trimmed.StartsWith( "```", StringComparison.Ordinal ))
        {
            int firstNl = trimmed.IndexOf( '\n' );
            int lastFence = trimmed.LastIndexOf( "```", StringComparison.Ordinal );
            if (firstNl >= 0 && lastFence > firstNl)
            {
                trimmed = trimmed[(firstNl + 1)..lastFence].Trim();
            }
        }

        if (trimmed.StartsWith( '{' ) && trimmed.EndsWith( '}' ))
        {
            return trimmed;
        }

        int start = trimmed.IndexOf( '{' );
        int end = trimmed.LastIndexOf( '}' );
        if (start >= 0 && end > start)
        {
            return trimmed[start..(end + 1)];
        }

        return trimmed;
    }

    private sealed class BookVisionExtract
    {
        public string? Title { get; set; }
        public string? Author { get; set; }
        public string? Isbn { get; set; }
        public string? Language { get; set; }
    }

    private sealed class BookHitNormalize
    {
        public string? Title { get; set; }
        public string? Author { get; set; }
        public string? Isbn { get; set; }
        public string? Publisher { get; set; }
        public bool? SameBook { get; set; }
    }
}
