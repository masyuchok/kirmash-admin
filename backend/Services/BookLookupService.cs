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

        BookVisionExtract extracted = await ExtractFromImagesAsync(
            coverBytes,
            cover.ContentType,
            isbnBytes,
            isbnPhoto?.ContentType,
            cancellationToken );

        if (string.IsNullOrWhiteSpace( extracted.Title ))
        {
            throw new InvalidOperationException(
                "Не ўдалося прачытаць назву кнігі з фота. Паспрабуйце іншае фота або ўвядзіце назву ўручную." );
        }

        BookLookupSessionState session = await CreateSessionAsync(
            extracted.Title,
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
            Message = "Праверце, ці правільна прачытаны даныя з фота.",
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
    /// </summary>
    public async Task<BookLookupCandidateDto> ImportFromUrlAsync(
        string? sessionId,
        string url,
        CancellationToken cancellationToken )
    {
        string trimmed = (url ?? string.Empty).Trim();
        if (!Uri.TryCreate( trimmed, UriKind.Absolute, out Uri? uri )
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException( "Укажыце карэктную http(s) спасылку." );
        }

        BookLookupSessionState session;
        if (!string.IsNullOrWhiteSpace( sessionId )
            && _sessions.TryGet( sessionId, out BookLookupSessionState existing ))
        {
            session = existing;
        }
        else
        {
            session = new BookLookupSessionState
            {
                SessionId = string.IsNullOrWhiteSpace( sessionId )
                    ? Guid.NewGuid().ToString( "N" )
                    : sessionId.Trim(),
                QueryTitle = string.Empty,
            };
        }

        PendingSearchHit pending = new()
        {
            Title = string.Empty,
            Url = uri.ToString(),
            Content = string.Empty,
            Source = "manual",
        };

        BookLookupCandidateDto candidate = await NormalizeHitAsync(
            session,
            pending,
            cancellationToken );

        if (string.IsNullOrWhiteSpace( candidate.Title ))
        {
            throw new InvalidOperationException(
                "Не ўдалося прачытаць назву са старонкі. Праверце спасылку або ўвядзіце даныя ўручную." );
        }

        candidate.Source = "manual";
        candidate.Url = uri.ToString();
        return candidate;
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

    private async Task<IReadOnlyList<TavilySearchHit>> SearchSupplierAsync(
        string query,
        IReadOnlyList<string> domains,
        CancellationToken cancellationToken )
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

        // Fallback: some indexes ignore include_domains — try explicit site: operator.
        List<TavilySearchHit> siteHits = new();
        foreach (string domain in domains.Take( 3 ))
        {
            string apex = StripWww( domain );
            string siteQuery = $"site:{apex} {query}";
            _logger.LogInformation( "Book lookup site: fallback query={Query}", siteQuery );
            IReadOnlyList<TavilySearchHit> batch = FilterHitsToDomains(
                await _tavily.SearchAsync(
                    siteQuery,
                    includeDomains: null,
                    ResultsPerSearch,
                    cancellationToken ),
                domains );
            foreach (TavilySearchHit hit in batch)
            {
                if (siteHits.Any( h => NormalizeUrlKey( h.Url ) == NormalizeUrlKey( hit.Url ) ))
                {
                    continue;
                }

                siteHits.Add( hit );
            }

            if (siteHits.Count > 0)
            {
                break;
            }
        }

        return siteHits;
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
        string softTitle = SoftTitleForSearch( session.QueryTitle );
        string authorLast = AuthorLastName( author );

        if (forSupplier)
        {
            // Title phrase first (punctuation-stripped) — shop titles often contain the
            // OCR phrase rather than matching it exactly.
            if (!string.IsNullOrWhiteSpace( softTitle ))
            {
                queries.Add( softTitle );
            }

            string coreTitle = SoftTitleCore( softTitle, maxWords: 5 );
            if (!string.IsNullOrWhiteSpace( coreTitle )
                && !string.Equals( coreTitle, softTitle, StringComparison.OrdinalIgnoreCase ))
            {
                queries.Add( coreTitle );
            }

            if (!string.IsNullOrWhiteSpace( author ) && !string.IsNullOrWhiteSpace( softTitle ))
            {
                queries.Add( $"{author.Trim()} {softTitle}" );
            }
            else if (!string.IsNullOrWhiteSpace( authorLast ) && !string.IsNullOrWhiteSpace( softTitle ))
            {
                queries.Add( $"{authorLast} {softTitle}" );
            }

            if (!string.IsNullOrWhiteSpace( isbn ))
            {
                // Bare digits match both 9788367937856 and 978-83-67937-85-6.
                queries.Add( isbn );
            }

            if (!string.IsNullOrWhiteSpace( author ))
            {
                queries.Add( author.Trim() );
            }
            else if (!string.IsNullOrWhiteSpace( authorLast ))
            {
                queries.Add( authorLast );
            }

            return queries
                .Where( q => !string.IsNullOrWhiteSpace( q ) )
                .Select( q => q.Trim() )
                .Distinct( StringComparer.OrdinalIgnoreCase )
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
        return string.Join( " ", SoftTitleWords( title ).Take( 8 ) );
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

        return false;
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

        if (a.Length < 4 || b.Length < 4)
        {
            return false;
        }

        // Slight ending drift: каханне / кахання / каханню
        string shorter = a.Length <= b.Length ? a : b;
        string longer = a.Length <= b.Length ? b : a;
        return longer.StartsWith( shorter[..^1], StringComparison.Ordinal )
            || (shorter.Length >= 5 && longer.StartsWith( shorter[..^2], StringComparison.Ordinal ));
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
                if (parsed.SameBook == false)
                {
                    return new BookLookupCandidateDto
                    {
                        Title = string.Empty,
                        Url = hit.Url,
                        Source = hit.Source,
                    };
                }

                if (!string.IsNullOrWhiteSpace( parsed.Title ))
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

        return string.IsNullOrWhiteSpace( query ) ? (best ?? string.Empty) : query;
    }

    private static int ScoreTitleAgainstQuery( string candidate, IReadOnlyList<string> queryWords )
    {
        if (queryWords.Count == 0)
        {
            return 0;
        }

        string norm = FoldBeLetters( NormalizeForMatch( candidate ) );
        return queryWords.Count( w => norm.Contains( w, StringComparison.Ordinal ) );
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
            || string.Equals( hit.Source, "manual", StringComparison.OrdinalIgnoreCase );

        bool isbnMatch = !string.IsNullOrWhiteSpace( session.QueryIsbn )
            && digits.Contains( session.QueryIsbn, StringComparison.Ordinal );

        bool authorMatch = !string.IsNullOrWhiteSpace( lastNorm )
            && lastNorm.Length >= 3
            && blobNorm.Contains( lastNorm, StringComparison.Ordinal );

        // Primary title signal: OCR phrase contained in shop title (punctuation-tolerant).
        bool titlePhraseInCandidate = SoftTitlePhraseContainedIn( candidate.Title, session.QueryTitle );
        bool titlePhraseInHit = SoftTitlePhraseContainedIn( hit.Title, session.QueryTitle )
            || SoftTitlePhraseContainedIn( $"{hit.Title} {hit.Content}", session.QueryTitle );
        bool titlePhraseMatch = titlePhraseInCandidate || titlePhraseInHit;

        IReadOnlyList<string> titleWords = SoftTitleWords( session.QueryTitle )
            .Where( w => w.Length >= 3 )
            .ToArray();
        int titleHits = titleWords.Count( w => blobNorm.Contains( FoldBeLetters( w ), StringComparison.Ordinal )
            || FoldBeLetters( blobNorm ).Contains( FoldBeLetters( w ), StringComparison.Ordinal ) );

        if (isbnMatch)
        {
            return true;
        }

        // Page shows a different author than the cover OCR → different book.
        string candidateLast = NormalizeForMatch( AuthorLastName( candidate.Author ) );
        if (!string.IsNullOrWhiteSpace( lastNorm )
            && lastNorm.Length >= 3
            && !string.IsNullOrWhiteSpace( candidateLast )
            && candidateLast.Length >= 3
            && !string.Equals( lastNorm, candidateLast, StringComparison.Ordinal )
            && !AuthorTokenStemsMatch( lastNorm, candidateLast ))
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
        string model = ResolveVisionModel();

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
        return parsed;
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
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning( "Groq book lookup failed: {Status} {Body}", (int)response.StatusCode, body );
            if ((int)response.StatusCode == 404)
            {
                string modelHint = TryReadPayloadModel( payload ) ?? "(невядома)";
                throw new InvalidOperationException(
                    $"Groq API памылка: 404 (мадэль «{modelHint}» не знойдзена). " +
                    "Праверце Groq:VisionModel / GROQ_VISION_MODEL." );
            }

            throw new InvalidOperationException(
                $"Groq API памылка: {(int)response.StatusCode}." );
        }

        return body;
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
