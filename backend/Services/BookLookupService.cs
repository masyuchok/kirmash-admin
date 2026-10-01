using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using backend.Models;
using backend.Services.ImageFetch;
using backend.Services.Shopify;
using Microsoft.AspNetCore.Http;

namespace backend.Services;

public sealed class BookLookupService
{
    private const int MaxFileBytes = 8 * 1024 * 1024;
    private const int ResultsPerSearch = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<BookLookupService> _logger;
    private readonly TavilySearchService _tavily;
    private readonly BookLookupSessionStore _sessions;
    private readonly BookTempMediaStore _tempMedia;
    private readonly ShopifyInventoryService _shopifyInventory;
    private readonly BookCoverStylizer _coverStylizer;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IRemoteImageFetcher _imageFetcher;

    public BookLookupService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<BookLookupService> logger,
        TavilySearchService tavily,
        BookLookupSessionStore sessions,
        BookTempMediaStore tempMedia,
        ShopifyInventoryService shopifyInventory,
        BookCoverStylizer coverStylizer,
        IHttpContextAccessor httpContextAccessor,
        IRemoteImageFetcher imageFetcher )
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
        _tavily = tavily;
        _sessions = sessions;
        _tempMedia = tempMedia;
        _shopifyInventory = shopifyInventory;
        _coverStylizer = coverStylizer;
        _httpContextAccessor = httpContextAccessor;
        _imageFetcher = imageFetcher;
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
        BookLookupCandidateDto? heuristic = null;
        string? pageBlob = null;

        // Universal: fetch page text, AI structures fields for ANY shop.
        // Heuristics (Woo / label regex) only seed blanks.
        try
        {
            PageTitleHints pageHints = await TryFetchPageTitleHintsAsync(
                cleanUrl,
                cancellationToken );
            if (!string.IsNullOrWhiteSpace( pageHints.RawHtml ))
            {
                pageBlob = TruncateForPrompt(
                    HtmlToPlainPreserveLines( pageHints.RawHtml ),
                    14000 );
                BookLookupCandidateDto? fromHtml = ParseCatalogPageContent(
                    pageHints.RawHtml,
                    cleanUrl );
                if (fromHtml is not null && !string.IsNullOrWhiteSpace( fromHtml.Title ))
                {
                    fromHtml.Source = "manual";
                    fromHtml.Url = cleanUrl;
                    heuristic = PreferRicherCandidate( heuristic, fromHtml );
                }
            }
            else
            {
                pageBlob = FirstNonEmpty( pageHints.Description, pageHints.OgTitle );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "from-url direct HTML failed for {Url}", cleanUrl );
        }

        if ((pageBlob?.Length ?? 0) < 400)
        {
            try
            {
                TavilyExtractResult? extracted = await _tavily.ExtractAsync(
                    cleanUrl,
                    cancellationToken );
                if (!string.IsNullOrWhiteSpace( extracted?.RawContent )
                    && !LooksLikeBotWallTitle(
                        extracted.RawContent[..Math.Min( extracted.RawContent.Length, 200 )] ))
                {
                    pageBlob = TruncateForPrompt( extracted.RawContent, 14000 );
                    BookLookupCandidateDto? fromExtract = ParseCatalogPageContent(
                        extracted.RawContent,
                        cleanUrl );
                    if (fromExtract is not null && !string.IsNullOrWhiteSpace( fromExtract.Title ))
                    {
                        fromExtract.Source = "manual";
                        fromExtract.Url = cleanUrl;
                        heuristic = PreferRicherCandidate( heuristic, fromExtract );
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning( ex, "from-url extract failed for {Url}", cleanUrl );
            }
        }

        try
        {
            BookLookupCandidateDto? fromStore = await TryImportUrlViaWooStoreAsync(
                cleanUrl,
                cancellationToken );
            if (fromStore is not null && !string.IsNullOrWhiteSpace( fromStore.Title ))
            {
                fromStore.Source = "manual";
                fromStore.Url = cleanUrl;
                heuristic = PreferRicherCandidate( heuristic, fromStore );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "from-url Woo store failed for {Url}", cleanUrl );
        }

        BookLookupCandidateDto? ai = await TryParseProductPageWithAiAsync(
            cleanUrl,
            pageBlob,
            seed: heuristic,
            cancellationToken );

        BookLookupCandidateDto? best = MergePreferAi( ai, heuristic );
        if (best is null || string.IsNullOrWhiteSpace( best.Title ))
        {
            BookLookupCandidateDto? fromIndex = await TryImportUrlViaIndexerAsync(
                cleanUrl,
                cancellationToken );
            if (fromIndex is not null)
            {
                fromIndex.Source = "manual";
                fromIndex.Url = cleanUrl;
                best = PreferRicherCandidate( best, fromIndex );
            }

            BookLookupCandidateDto? fromWeb = await TryImportUrlViaWebMentionsAsync(
                cleanUrl,
                cancellationToken );
            if (fromWeb is not null)
            {
                fromWeb.Source = "manual";
                fromWeb.Url = cleanUrl;
                best = PreferRicherCandidate( best, fromWeb );
            }
        }

        if (best is not null && !string.IsNullOrWhiteSpace( best.Title ))
        {
            best = PostProcessCatalogCandidate( best );
            _logger.LogInformation(
                "Book lookup from-url: {Url} → {Title} / {Author} (ai={HasAi})",
                cleanUrl,
                best.Title,
                best.Author,
                ai is not null );
            return best;
        }

        throw new InvalidOperationException(
            "Не ўдалося прачытаць назву са старонкі крамы. Паспрабуйце яшчэ раз або ўвядзіце даныя ўручную." );
    }

    /// <summary>
    /// AI fields win when present; heuristics only fill blanks.
    /// </summary>
    private static BookLookupCandidateDto? MergePreferAi(
        BookLookupCandidateDto? ai,
        BookLookupCandidateDto? heuristic )
    {
        if (ai is null || string.IsNullOrWhiteSpace( ai.Title ))
        {
            return heuristic is null ? null : PostProcessCatalogCandidate( heuristic );
        }

        if (heuristic is null || string.IsNullOrWhiteSpace( heuristic.Title ))
        {
            return PostProcessCatalogCandidate( ai );
        }

        return PostProcessCatalogCandidate( new BookLookupCandidateDto
        {
            Title = FirstNonEmpty( ai.Title, heuristic.Title ) ?? ai.Title,
            Author = FirstNonEmpty(
                NormalizeAuthorCandidate( ai.Author ),
                NormalizeAuthorCandidate( heuristic.Author ) ),
            Isbn = FirstNonEmpty( ai.Isbn, heuristic.Isbn ),
            Publisher = FirstNonEmpty( ai.Publisher, heuristic.Publisher ),
            Description = FirstNonEmpty(
                CapDescription( ai.Description ),
                CapDescription( heuristic.Description ) ),
            CoverImageUrl = PreferRealCoverImageUrl( ai.CoverImageUrl, heuristic.CoverImageUrl ),
            AdditionalImageUrls = heuristic.AdditionalImageUrls?.Count > 0
                ? heuristic.AdditionalImageUrls
                : ai.AdditionalImageUrls,
            WeightKg = ai.WeightKg is > 0 ? ai.WeightKg : heuristic.WeightKg,
            SalePrice = ai.SalePrice is > 0 ? ai.SalePrice : heuristic.SalePrice,
            CoverType = FirstNonEmpty( ai.CoverType, heuristic.CoverType ),
            AgeRating = FirstNonEmpty( ai.AgeRating, heuristic.AgeRating ),
            Format = FirstNonEmpty( ai.Format, heuristic.Format ),
            Illustrator = FirstNonEmpty( ai.Illustrator, heuristic.Illustrator ),
            Language = FirstNonEmpty( ai.Language, heuristic.Language ),
            PlaceOfPublication = FirstNonEmpty(
                ai.PlaceOfPublication,
                heuristic.PlaceOfPublication ),
            PageCount = ai.PageCount is > 0 ? ai.PageCount : heuristic.PageCount,
            Translation = FirstNonEmpty( ai.Translation, heuristic.Translation ),
            Year = ai.Year is > 0 ? ai.Year : heuristic.Year,
            Url = FirstNonEmpty( ai.Url, heuristic.Url ) ?? ai.Url,
            Source = "manual",
            Snippet = FirstNonEmpty( ai.Snippet, heuristic.Snippet ),
        } );
    }

    private async Task<BookLookupCandidateDto> FinalizeFromUrlCandidateAsync(
        BookLookupCandidateDto candidate,
        string cleanUrl,
        string? pageBlob,
        CancellationToken cancellationToken )
    {
        candidate.Url = cleanUrl;
        candidate.Source = "manual";

        string? blob = pageBlob;
        if ((blob?.Length ?? 0) < 400)
        {
            try
            {
                PageTitleHints hints = await TryFetchPageTitleHintsAsync(
                    cleanUrl,
                    cancellationToken );
                blob = FirstNonEmpty(
                    TruncateForPrompt(
                        HtmlToPlainPreserveLines( hints.RawHtml ?? string.Empty ),
                        14000 ),
                    hints.Description,
                    blob,
                    candidate.Description,
                    candidate.Snippet );

                if ((blob?.Length ?? 0) < 400)
                {
                    TavilyExtractResult? extracted = await _tavily.ExtractAsync(
                        cleanUrl,
                        cancellationToken );
                    if (!string.IsNullOrWhiteSpace( extracted?.RawContent ))
                    {
                        blob = TruncateForPrompt( extracted.RawContent, 14000 );
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug( ex, "AI enrich: page re-fetch failed for {Url}", cleanUrl );
            }
        }

        BookLookupCandidateDto? ai = await TryParseProductPageWithAiAsync(
            cleanUrl,
            blob,
            candidate,
            cancellationToken );
        return MergePreferAi( ai, candidate )
            ?? PostProcessCatalogCandidate( candidate );
    }

    private async Task<BookLookupCandidateDto?> TryParseProductPageWithAiAsync(
        string pageUrl,
        string? pageText,
        BookLookupCandidateDto? seed,
        CancellationToken cancellationToken )
    {
        string blob = (pageText ?? string.Empty).Trim();
        if (blob.Length < 40 && seed is null)
        {
            return null;
        }

        string openAiKey = (_config["OpenAI:ApiKey"] ?? string.Empty).Trim();
        string groqKey = (_config["Groq:ApiKey"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( openAiKey ) && string.IsNullOrWhiteSpace( groqKey ))
        {
            _logger.LogWarning( "No OpenAI/Groq key — skip AI page extract for {Url}", pageUrl );
            return null;
        }

        string systemPrompt =
            """
            You extract bookstore product metadata from an arbitrary product page (HTML or plain text).
            Return ONLY valid JSON with keys:
            title, author, isbn, publisher, description, salePrice, coverImageUrl,
            coverType, year, pageCount, language, placeOfPublication, format, ageRating, weightKg.
            Rules:
            - title = book title ONLY (no author, no shop name).
            - author = person name(s), never buttons like "Add to cart".
            - description = main product blurb only; no related products, tags, nav, scripts.
            - salePrice = numeric price as shown (e.g. 6 for £6.00); null if unknown.
            - coverType = "soft" or "hard" when page says soft/paperback/мяккая/твёрдая; else null.
            - language = language of the book text (e.g. беларуская), not the shop UI language.
            - format like 130×200 when dimensions given.
            - Use null for unknown fields. Prefer Cyrillic Belarusian names when present on page.
            """;

        string seedJson = seed is null
            ? "{}"
            : JsonSerializer.Serialize( new
            {
                seed.Title,
                seed.Author,
                seed.Isbn,
                seed.Publisher,
                seed.Description,
                seed.SalePrice,
                seed.CoverImageUrl,
                seed.CoverType,
                seed.Year,
                seed.PageCount,
                seed.Language,
                seed.PlaceOfPublication,
                seed.Format,
                seed.AgeRating,
                seed.WeightKg,
            } );

        string userPrompt =
            $"""
            Product URL: {pageUrl}

            Current heuristic extract (may be wrong — fix it):
            {seedJson}

            Page content:
            {TruncateForPrompt( blob, 12000 )}
            """;

        try
        {
            string body;
            if (!string.IsNullOrWhiteSpace( openAiKey ))
            {
                Dictionary<string, object?> payload = new()
                {
                    ["model"] = ResolveOpenAiModel(),
                    ["max_completion_tokens"] = 1200,
                    ["reasoning_effort"] = "low",
                    ["response_format"] = new { type = "json_object" },
                    ["messages"] = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt },
                    },
                };
                body = await SendOpenAiChatAsync( openAiKey, payload, cancellationToken );
            }
            else
            {
                Dictionary<string, object?> payload = new()
                {
                    ["model"] = ResolveTextModel(),
                    ["temperature"] = 0.1,
                    ["max_tokens"] = 1200,
                    ["response_format"] = new { type = "json_object" },
                    ["messages"] = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt },
                    },
                };
                body = await SendGroqChatAsync( groqKey, payload, cancellationToken );
            }

            BookLookupCandidateDto? parsed = ParseAiProductPageJson( body, pageUrl );
            if (parsed is not null)
            {
                _logger.LogInformation(
                    "AI page extract for {Url}: {Title} / {Author} price={Price}",
                    pageUrl,
                    parsed.Title,
                    parsed.Author,
                    parsed.SalePrice );
            }

            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "AI page extract failed for {Url}", pageUrl );
            return null;
        }
    }

    private static BookLookupCandidateDto? ParseAiProductPageJson( string apiBody, string pageUrl )
    {
        string? content = ExtractOpenAiMessageContent( apiBody );
        if (string.IsNullOrWhiteSpace( content ))
        {
            return null;
        }

        content = content.Trim();
        if (content.StartsWith( "```", StringComparison.Ordinal ))
        {
            content = Regex.Replace( content, @"^```(?:json)?\s*", string.Empty );
            content = Regex.Replace( content, @"\s*```$", string.Empty );
        }

        using JsonDocument doc = JsonDocument.Parse( content );
        JsonElement root = doc.RootElement;

        string title = ReadJsonFlexibleString( root, "title" );
        if (string.IsNullOrWhiteSpace( title ))
        {
            return null;
        }

        (string titleOnly, string? authorFromTitle) = SplitCatalogTitleWithAuthor( title );
        string cleaned = CleanBookTitle(
            string.IsNullOrWhiteSpace( titleOnly ) ? title : titleOnly );
        if (string.IsNullOrWhiteSpace( cleaned ))
        {
            return null;
        }

        string? author = NormalizeAuthorCandidate(
            FirstNonEmpty( ReadJsonFlexibleString( root, "author" ), authorFromTitle ) );
        if (!string.IsNullOrWhiteSpace( author ))
        {
            author = FormatAuthorFirstLast( author, null );
        }

        string? description = CapDescription( ReadJsonFlexibleString( root, "description" ) );
        string? cover = PreferRealCoverImageUrl( ReadJsonFlexibleString( root, "coverImageUrl" ) );
        decimal? salePrice = ReadJsonFlexibleDecimal( root, "salePrice" );
        if (salePrice is <= 0 or > 100_000)
        {
            salePrice = null;
        }

        string? isbnRaw = ReadJsonFlexibleString( root, "isbn" );
        return new BookLookupCandidateDto
        {
            Title = cleaned,
            Author = author,
            Isbn = IsbnUtil.NormalizePreferHyphens( isbnRaw )
                ?? ExtractIsbnFromText( isbnRaw ?? string.Empty ),
            Publisher = NullIfWhiteSpace( ReadJsonFlexibleString( root, "publisher" ) ),
            Description = description,
            CoverImageUrl = cover,
            SalePrice = salePrice,
            CoverType = BookProductCoverType.Normalize( ReadJsonFlexibleString( root, "coverType" ) ),
            AgeRating = BookAgeRating.NormalizeLabeled( ReadJsonFlexibleString( root, "ageRating" ) ),
            Format = BookBibliographicFields.NormalizeFormat( ReadJsonFlexibleString( root, "format" ) ),
            Language = BookBibliographicFields.NormalizeLanguage(
                ReadJsonFlexibleString( root, "language" ) ),
            PlaceOfPublication = BookBibliographicFields.NormalizePlace(
                ReadJsonFlexibleString( root, "placeOfPublication" ) ),
            PageCount = ReadJsonFlexibleInt( root, "pageCount" )
                ?? BookBibliographicFields.NormalizePageCount(
                    ReadJsonFlexibleString( root, "pageCount" ) ),
            Year = ReadJsonFlexibleInt( root, "year" )
                ?? BookBibliographicFields.NormalizeYear( ReadJsonFlexibleString( root, "year" ) ),
            WeightKg = ReadJsonFlexibleDecimal( root, "weightKg" ),
            Url = pageUrl,
            Source = "manual",
            Snippet = TruncateSnippet( description ),
        };
    }

    private static string? ExtractOpenAiMessageContent( string apiBody )
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse( apiBody );
            if (doc.RootElement.TryGetProperty( "choices", out JsonElement choices )
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                JsonElement msg = choices[0].GetProperty( "message" );
                if (msg.TryGetProperty( "content", out JsonElement contentEl )
                    && contentEl.ValueKind == JsonValueKind.String)
                {
                    return contentEl.GetString();
                }
            }
        }
        catch
        {
            // fall through
        }

        return null;
    }

    private static string ReadJsonFlexibleString( JsonElement root, string name )
    {
        if (!root.TryGetProperty( name, out JsonElement el )
            || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return el.ValueKind == JsonValueKind.String
            ? (el.GetString() ?? string.Empty).Trim()
            : el.ToString().Trim();
    }

    private static decimal? ReadJsonFlexibleDecimal( JsonElement root, string name )
    {
        if (!root.TryGetProperty( name, out JsonElement el )
            || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal( out decimal n ))
        {
            return n;
        }

        if (el.ValueKind == JsonValueKind.String
            && decimal.TryParse(
                (el.GetString() ?? string.Empty).Replace( ',', '.' ),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal fromStr ))
        {
            return fromStr;
        }

        return null;
    }

    private static int? ReadJsonFlexibleInt( JsonElement root, string name )
    {
        if (!root.TryGetProperty( name, out JsonElement el )
            || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32( out int n ))
        {
            return n;
        }

        if (el.ValueKind == JsonValueKind.String
            && int.TryParse(
                el.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int fromStr ))
        {
            return fromStr;
        }

        return null;
    }

    private static BookLookupCandidateDto MergeCatalogCandidates(
        BookLookupCandidateDto primary,
        BookLookupCandidateDto? secondary )
    {
        if (secondary is null || string.IsNullOrWhiteSpace( secondary.Title ))
        {
            return primary;
        }

        static string? PreferClean( string? current, string? incoming, bool junkCheck )
        {
            bool curBad = string.IsNullOrWhiteSpace( current )
                || (junkCheck && LooksLikeJunkDescription( current ));
            bool incBad = string.IsNullOrWhiteSpace( incoming )
                || (junkCheck && LooksLikeJunkDescription( incoming ));
            if (curBad && !incBad)
            {
                return incoming;
            }

            if (!curBad)
            {
                return current;
            }

            return incoming ?? current;
        }

        string? author = PreferClean( primary.Author, secondary.Author, junkCheck: true );
        if (!string.IsNullOrWhiteSpace( author )
            && Regex.IsMatch( author, @"add to cart|корзин|кошык", RegexOptions.IgnoreCase ))
        {
            author = secondary.Author;
        }

        return new BookLookupCandidateDto
        {
            Title = PreferTitle( primary.Title, secondary.Title ),
            Author = NormalizeAuthorCandidate( author ),
            Isbn = FirstNonEmpty( primary.Isbn, secondary.Isbn ),
            Publisher = FirstNonEmpty( primary.Publisher, secondary.Publisher ),
            Description = PreferClean( primary.Description, secondary.Description, junkCheck: true ),
            CoverImageUrl = PreferRealCoverImageUrl( primary.CoverImageUrl, secondary.CoverImageUrl ),
            AdditionalImageUrls = primary.AdditionalImageUrls?.Count > 0
                ? primary.AdditionalImageUrls
                : secondary.AdditionalImageUrls,
            WeightKg = primary.WeightKg is > 0 ? primary.WeightKg : secondary.WeightKg,
            SalePrice = primary.SalePrice is > 0 ? primary.SalePrice : secondary.SalePrice,
            CoverType = FirstNonEmpty( primary.CoverType, secondary.CoverType ),
            AgeRating = FirstNonEmpty( primary.AgeRating, secondary.AgeRating ),
            Format = FirstNonEmpty( primary.Format, secondary.Format ),
            Illustrator = FirstNonEmpty( primary.Illustrator, secondary.Illustrator ),
            Language = FirstNonEmpty( primary.Language, secondary.Language ),
            PlaceOfPublication = FirstNonEmpty(
                primary.PlaceOfPublication,
                secondary.PlaceOfPublication ),
            PageCount = primary.PageCount is > 0 ? primary.PageCount : secondary.PageCount,
            Translation = FirstNonEmpty( primary.Translation, secondary.Translation ),
            Year = primary.Year is > 0 ? primary.Year : secondary.Year,
            Url = FirstNonEmpty( primary.Url, secondary.Url ) ?? primary.Url,
            Source = primary.Source,
            Snippet = FirstNonEmpty( primary.Snippet, secondary.Snippet ),
        };

        static string PreferTitle( string? current, string? incoming )
        {
            bool curGlued = !string.IsNullOrWhiteSpace( current )
                && Regex.IsMatch( current, @"\s/\s+\S+" );
            bool incOk = !string.IsNullOrWhiteSpace( incoming )
                && !Regex.IsMatch( incoming, @"\s/\s+\S+" );
            if (curGlued && incOk)
            {
                return incoming!;
            }

            return FirstNonEmpty( current, incoming ) ?? string.Empty;
        }
    }

    private static BookLookupCandidateDto PostProcessCatalogCandidate( BookLookupCandidateDto c )
    {
        (string titleOnly, string? authorFromTitle) = SplitCatalogTitleWithAuthor( c.Title );
        string cleaned = CleanBookTitle(
            string.IsNullOrWhiteSpace( titleOnly ) ? c.Title : titleOnly );
        string? author = NormalizeAuthorCandidate( c.Author )
            ?? NormalizeAuthorCandidate( authorFromTitle );
        if (!string.IsNullOrWhiteSpace( author ))
        {
            author = FormatAuthorFirstLast( author, null );
        }

        c.Title = cleaned;
        c.Author = author;
        c.Description = CapDescription( c.Description );
        c.CoverImageUrl = PreferRealCoverImageUrl( c.CoverImageUrl );
        if (c.SalePrice is <= 0)
        {
            c.SalePrice = null;
        }

        return c;
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
        string? rawSlug = TryGetRawProductSlugFromUrl( cleanUrl );
        List<string> slugVariants = new();
        foreach (string? candidate in new[] { rawSlug, slug })
        {
            if (string.IsNullOrWhiteSpace( candidate ))
            {
                continue;
            }

            string trimmed = candidate.Trim().Trim( '/' );
            if (trimmed.Length > 0
                && !slugVariants.Contains( trimmed, StringComparer.Ordinal ))
            {
                slugVariants.Add( trimmed );
            }
        }

        if (slugVariants.Count == 0)
        {
            return null;
        }

        string host = uri.Host.Trim().TrimEnd( '.' );
        List<string> endpoints = new();
        foreach (string slugVariant in slugVariants)
        {
            string escaped = Uri.EscapeDataString( slugVariant );
            endpoints.Add( $"https://{host}/wp-json/wc/store/v1/products?slug={escaped}" );
            endpoints.Add( $"https://{host}/wp-json/wc/store/products?slug={escaped}" );
            endpoints.Add( $"https://{host}/wp-json/wp/v2/product?slug={escaped}&_embed=1" );
            endpoints.Add( $"https://{host}/wp-json/wp/v2/product?slug={escaped}" );
        }

        HttpClient client = _httpClientFactory.CreateClient( "BookLookupJson" );
        BookLookupCandidateDto? best = null;
        long? productId = null;

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

                productId ??= TryReadProductIdFromJson( body );
                BookLookupCandidateDto? parsed = TryParseWooOrWpProductJson( body, cleanUrl );
                best = PreferRicherCandidate( best, parsed );
                if (IsRichShopCandidate( best ) && productId is not null)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning( ex, "from-url Woo/WP store failed for {Url}", endpoint );
            }
        }

        // Kamunikat.shop: slug filter on WC Store often returns []; WP finds id,
        // then Store /products/{id} has attributes/price/weight/description.
        if (productId is long id && id > 0 && !IsRichShopCandidate( best ))
        {
            BookLookupCandidateDto? byId = await TryFetchWooStoreProductByIdAsync(
                client,
                host,
                id,
                cleanUrl,
                cancellationToken );
            best = PreferRicherCandidate( best, byId );
        }

        if (best is not null && !string.IsNullOrWhiteSpace( best.Title ))
        {
            _logger.LogInformation(
                "Book lookup from-url via shop API: {Url} → {Title} / {Author} price={Price}",
                cleanUrl,
                best.Title,
                best.Author,
                best.SalePrice );
            return best;
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

                productId ??= TryReadProductIdFromJson( json );
                BookLookupCandidateDto? parsed = TryParseWooOrWpProductJson( json, cleanUrl );
                best = PreferRicherCandidate( best, parsed );
                if (IsRichShopCandidate( best ))
                {
                    break;
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

        if (productId is long tavilyId && tavilyId > 0 && !IsRichShopCandidate( best ))
        {
            foreach (string idEndpoint in new[]
                     {
                         $"https://{host}/wp-json/wc/store/v1/products/{tavilyId}",
                         $"https://{host}/wp-json/wc/store/products/{tavilyId}",
                     })
            {
                try
                {
                    TavilyExtractResult? extracted = await _tavily.ExtractAsync(
                        idEndpoint,
                        cancellationToken );
                    if (extracted is null || string.IsNullOrWhiteSpace( extracted.RawContent ))
                    {
                        continue;
                    }

                    string? json = ExtractJsonPayload( extracted.RawContent.Trim() );
                    if (string.IsNullOrWhiteSpace( json ))
                    {
                        continue;
                    }

                    BookLookupCandidateDto? parsed = TryParseWooOrWpProductJson( json, cleanUrl );
                    best = PreferRicherCandidate( best, parsed );
                    if (IsRichShopCandidate( best ))
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "from-url Tavily store-by-id failed for {Url}",
                        idEndpoint );
                }
            }
        }

        if (best is not null && !string.IsNullOrWhiteSpace( best.Title ))
        {
            _logger.LogInformation(
                "Book lookup from-url via Tavily→shop API: {Url} → {Title} / {Author}",
                cleanUrl,
                best.Title,
                best.Author );
            return best;
        }

        return null;
    }

    private async Task<BookLookupCandidateDto?> TryFetchWooStoreProductByIdAsync(
        HttpClient client,
        string host,
        long productId,
        string cleanUrl,
        CancellationToken cancellationToken )
    {
        string[] endpoints =
        {
            $"https://{host}/wp-json/wc/store/v1/products/{productId}",
            $"https://{host}/wp-json/wc/store/products/{productId}",
        };

        foreach (string endpoint in endpoints)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(
                    endpoint,
                    cancellationToken );
                string body = await response.Content.ReadAsStringAsync( cancellationToken );
                string? mediaType = response.Content.Headers.ContentType?.MediaType;
                if (!response.IsSuccessStatusCode
                    || LooksLikeCloudflareOrHtmlPayload( body, mediaType ))
                {
                    continue;
                }

                BookLookupCandidateDto? parsed = TryParseWooOrWpProductJson( body, cleanUrl );
                if (parsed is not null && !string.IsNullOrWhiteSpace( parsed.Title ))
                {
                    _logger.LogInformation(
                        "Book lookup enriched via WC Store by id {ProductId}: {Title} / {Author}",
                        productId,
                        parsed.Title,
                        parsed.Author );
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning( ex, "WC Store by id failed for {Url}", endpoint );
            }
        }

        return null;
    }

    private static string? TryGetRawProductSlugFromUrl( string url )
    {
        Match m = Regex.Match(
            url ?? string.Empty,
            @"/(?:pradukt|produkt|product)/(?<slug>[^/?#]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );
        if (!m.Success)
        {
            return null;
        }

        string slug = m.Groups["slug"].Value.Trim().Trim( '/' );
        return string.IsNullOrWhiteSpace( slug ) ? null : slug;
    }

    private static long? TryReadProductIdFromJson( string body )
    {
        if (string.IsNullOrWhiteSpace( body )
            || LooksLikeCloudflareOrHtmlPayload( body, null ))
        {
            return null;
        }

        try
        {
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

            if (product.TryGetProperty( "id", out JsonElement idEl ))
            {
                if (idEl.ValueKind == JsonValueKind.Number
                    && idEl.TryGetInt64( out long id )
                    && id > 0)
                {
                    return id;
                }

                if (idEl.ValueKind == JsonValueKind.String
                    && long.TryParse( idEl.GetString(), out long parsed )
                    && parsed > 0)
                {
                    return parsed;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static bool IsRichShopCandidate( BookLookupCandidateDto? cand )
    {
        if (cand is null || string.IsNullOrWhiteSpace( cand.Title ))
        {
            return false;
        }

        if (NeedsAiPageEnrichment( cand ))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace( cand.Author )
            && (!string.IsNullOrWhiteSpace( cand.CoverImageUrl )
                || cand.SalePrice is > 0
                || !string.IsNullOrWhiteSpace( cand.Isbn )
                || !string.IsNullOrWhiteSpace( cand.Description ));
    }

    /// <summary>
    /// Heuristic parse missed critical fields or filled chrome junk — run AI page extract.
    /// </summary>
    private static bool NeedsAiPageEnrichment( BookLookupCandidateDto? cand )
    {
        if (cand is null || string.IsNullOrWhiteSpace( cand.Title ))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace( cand.Author )
            || LooksLikeJunkDescription( cand.Author )
            || Regex.IsMatch(
                cand.Author,
                @"add to cart|корзин|кошык|browse shop|in stock",
                RegexOptions.IgnoreCase ))
        {
            return true;
        }

        if (LooksLikeJunkDescription( cand.Description ?? string.Empty ))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace( cand.Description )
            || (cand.Description?.Length ?? 0) < 40)
        {
            return true;
        }

        if (cand.SalePrice is null or <= 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace( cand.CoverImageUrl ))
        {
            return true;
        }

        // Title still glued with author: "Title / Author"
        if (Regex.IsMatch( cand.Title, @"\s/\s+\S+\s+\S+" ))
        {
            return true;
        }

        return false;
    }

    private static BookLookupCandidateDto? PreferRicherCandidate(
        BookLookupCandidateDto? current,
        BookLookupCandidateDto? incoming )
    {
        if (incoming is null || string.IsNullOrWhiteSpace( incoming.Title ))
        {
            return current;
        }

        if (current is null || string.IsNullOrWhiteSpace( current.Title ))
        {
            return incoming;
        }

        return ScoreShopCandidate( incoming ) > ScoreShopCandidate( current )
            ? incoming
            : current;
    }

    private static int ScoreShopCandidate( BookLookupCandidateDto c )
    {
        int score = 0;
        if (!string.IsNullOrWhiteSpace( c.Title )) score += 1;
        if (!string.IsNullOrWhiteSpace( c.Author )
            && NormalizeAuthorCandidate( c.Author ) is not null)
        {
            score += 3;
        }

        if (!string.IsNullOrWhiteSpace( c.Description )
            && !LooksLikeJunkDescription( c.Description ))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace( c.CoverImageUrl )
            && !IsSocialCardImageUrl( c.CoverImageUrl ))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace( c.Isbn )) score += 2;
        if (!string.IsNullOrWhiteSpace( c.Publisher )) score += 1;
        if (c.SalePrice is > 0) score += 2;
        if (c.WeightKg is > 0) score += 1;
        if (!string.IsNullOrWhiteSpace( c.CoverType )) score += 1;
        if (c.Year is > 0) score += 1;
        if (c.PageCount is > 0) score += 1;
        if (!string.IsNullOrWhiteSpace( c.PlaceOfPublication )) score += 1;
        return score;
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
        List<string> authorParts = new();
        string? isbnAttr = null;
        string? publisherAttr = null;
        string? weightAttr = null;
        string? coverAttr = null;
        string? ageAttr = null;
        string? formatAttr = null;
        string? illustratorAttr = null;
        string? languageAttr = null;
        string? pageCountAttr = null;
        string? placeAttr = null;
        string? translationAttr = null;
        string? yearAttr = null;
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
                List<string> terms = AllWooAttributeTermNames( attr );
                if (terms.Count == 0)
                {
                    continue;
                }

                string joined = string.Join( " ", terms );

                if (taxonomy.Contains( "autar", StringComparison.OrdinalIgnoreCase )
                    || taxonomy.Contains( "author", StringComparison.OrdinalIgnoreCase )
                    || attrName.Contains( "Аўтар", StringComparison.OrdinalIgnoreCase )
                    || attrName.Contains( "Автор", StringComparison.OrdinalIgnoreCase )
                    || attrName.Equals( "Author", StringComparison.OrdinalIgnoreCase ))
                {
                    foreach (string term in terms)
                    {
                        authorParts.Add( term );
                    }

                    authorAttr ??= terms[0];
                }
                else if (taxonomy.Contains( "isbn", StringComparison.OrdinalIgnoreCase )
                    || attrName.Contains( "ISBN", StringComparison.OrdinalIgnoreCase ))
                {
                    isbnAttr ??= terms[0];
                }
                else if (taxonomy.Contains( "vydav", StringComparison.OrdinalIgnoreCase )
                    || taxonomy.Contains( "publisher", StringComparison.OrdinalIgnoreCase )
                    || attrName.Contains( "Выдавец", StringComparison.OrdinalIgnoreCase )
                    || attrName.Contains( "Издател", StringComparison.OrdinalIgnoreCase )
                    || attrName.Contains( "Publisher", StringComparison.OrdinalIgnoreCase ))
                {
                    publisherAttr ??= terms[0];
                }
                else if (IsWeightAttributeLabel( taxonomy, attrName ))
                {
                    weightAttr ??= terms[0];
                }
                else if (BookProductCoverType.IsCoverAttributeLabel( taxonomy, attrName ))
                {
                    coverAttr ??= terms[0];
                }
                else if (BookAgeRating.IsAgeAttributeLabel( taxonomy, attrName ))
                {
                    ageAttr ??= BookAgeRating.Normalize( joined );
                    foreach (string term in terms)
                    {
                        ageAttr ??= BookAgeRating.Normalize( term );
                    }
                }
                else if (BookBibliographicFields.IsFormatAttributeLabel( taxonomy, attrName ))
                {
                    formatAttr ??= BookBibliographicFields.NormalizeFormat( joined );
                }
                else if (BookBibliographicFields.IsIllustratorAttributeLabel( taxonomy, attrName ))
                {
                    illustratorAttr ??= BookBibliographicFields.NormalizeIllustrator( joined );
                }
                else if (BookBibliographicFields.IsLanguageAttributeLabel( taxonomy, attrName ))
                {
                    languageAttr ??= BookBibliographicFields.NormalizeLanguage( joined )
                        ?? CollapseAttributeTerms( terms );
                }
                else if (BookBibliographicFields.IsPageCountAttributeLabel( taxonomy, attrName ))
                {
                    pageCountAttr ??= joined;
                }
                else if (BookBibliographicFields.IsPlaceAttributeLabel( taxonomy, attrName ))
                {
                    placeAttr ??= BookBibliographicFields.NormalizePlace( joined )
                        ?? CollapseAttributeTerms( terms );
                }
                else if (BookBibliographicFields.IsTranslationAttributeLabel( taxonomy, attrName ))
                {
                    translationAttr ??= BookBibliographicFields.NormalizeTranslation( joined )
                        ?? CollapseAttributeTerms( terms );
                }
                else if (BookBibliographicFields.IsYearAttributeLabel( taxonomy, attrName ))
                {
                    yearAttr ??= joined;
                }
                else
                {
                    foreach (string term in terms)
                    {
                        if (BookProductCoverType.Normalize( term ) is not null)
                        {
                            coverAttr ??= term;
                        }

                        ageAttr ??= BookAgeRating.Normalize( term );
                        formatAttr ??= BookBibliographicFields.NormalizeFormat( term );
                    }
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

        string? description = ExtractWooProductDescription( product );
        List<string> imageUrls = ExtractWooProductImageUrls( product );
        string? coverUrl = imageUrls.Count > 0 ? imageUrls[0] : null;
        List<string> additionalUrls = imageUrls.Skip( 1 ).ToList();
        decimal? weightKg = ExtractWooProductWeightKg( product, weightAttr );
        decimal? salePrice = ExtractWooProductSalePrice( product );
        string? coverType = BookProductCoverType.Normalize( coverAttr )
            ?? BookProductCoverType.Normalize(
                ExtractLabeledField(
                    description ?? string.Empty,
                    "Вокладка",
                    "Обложка",
                    "Переплёт",
                    "Переплет",
                    "Binding",
                    "Cover" ) );
        string? ageFromDesc = ExtractLabeledField(
            description ?? string.Empty,
            "Возрастные ограничения",
            "Возрастное ограничение",
            "Возрастная категория",
            "Узрост",
            "Ўзрост",
            "Возраст",
            "Age",
            "Age rating" );
        string? ageRating = ageAttr
            ?? BookAgeRating.NormalizeLabeled( ageFromDesc )
            ?? BookAgeRating.Normalize( description )
            ?? BookAgeRating.Normalize( name );
        string? format = formatAttr
            ?? BookBibliographicFields.NormalizeFormat( description );
        string? illustrator = illustratorAttr
            ?? BookBibliographicFields.ExtractIllustratorFromText( description );
        string? language = BookBibliographicFields.ResolveLanguage(
            languageAttr,
            description,
            name );
        int? pageCount = BookBibliographicFields.NormalizePageCount( pageCountAttr )
            ?? BookBibliographicFields.NormalizePageCount( description );
        string? place = placeAttr
            ?? BookBibliographicFields.ExtractPlaceFromText( description );
        string? publisher = NullIfWhiteSpace( publisherAttr )
            ?? BookBibliographicFields.ExtractPublisherFromText( description );
        string? translation = translationAttr
            ?? BookBibliographicFields.NormalizeTranslation( description );
        int? year = BookBibliographicFields.NormalizeYear( yearAttr )
            ?? BookBibliographicFields.NormalizeYear( description );
        string? isbn = IsbnUtil.NormalizePreferHyphens( isbnAttr )
            ?? ExtractIsbnFromText( name )
            ?? ExtractIsbnFromText( description ?? string.Empty );

        string? author = FormatAuthorsList( authorParts );
        if (string.IsNullOrWhiteSpace( author ) && !string.IsNullOrWhiteSpace( authorFromTitle ))
        {
            author = FormatAuthorFirstLast( authorFromTitle, null );
        }

        // Kamunikat often puts Given-Surname in the URL slug when attributes omit author.
        if (string.IsNullOrWhiteSpace( author )
            && TryParseProductUrlSlug( cleanUrl, out _, out string? slugAuthor )
            && !string.IsNullOrWhiteSpace( slugAuthor ))
        {
            author = FormatAuthorFirstLast( slugAuthor, null );
        }

        return new BookLookupCandidateDto
        {
            Title = cleaned,
            Author = author,
            Isbn = isbn,
            Publisher = publisher,
            Description = description,
            CoverImageUrl = coverUrl,
            AdditionalImageUrls = additionalUrls,
            WeightKg = weightKg,
            SalePrice = salePrice,
            CoverType = coverType,
            AgeRating = ageRating,
            Format = format,
            Illustrator = illustrator,
            Language = language,
            PageCount = pageCount,
            PlaceOfPublication = place,
            Translation = translation,
            Year = year,
            Url = cleanUrl,
            Source = "manual",
            Snippet = TruncateSnippet( name ),
        };
    }

    private static string CollapseAttributeTerms( IEnumerable<string> terms )
    {
        string joined = string.Join( ", ", terms.Select( t => t.Trim() ).Where( t => t.Length > 0 ) );
        return string.IsNullOrWhiteSpace( joined ) ? string.Empty : joined;
    }

    private static string? FormatAuthorsList( IEnumerable<string> authors )
    {
        List<string> formatted = new();
        HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );
        foreach (string raw in authors)
        {
            foreach (string piece in raw.Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries ))
            {
                string one = FormatAuthorFirstLast( piece, null ).Trim();
                if (string.IsNullOrWhiteSpace( one ) || !seen.Add( one ))
                {
                    continue;
                }

                formatted.Add( one );
            }
        }

        return formatted.Count == 0 ? null : string.Join( ", ", formatted );
    }

    private static List<string> AllWooAttributeTermNames( JsonElement attr )
    {
        List<string> result = new();
        if (attr.TryGetProperty( "terms", out JsonElement terms )
            && terms.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement term in terms.EnumerateArray())
            {
                if (term.TryGetProperty( "name", out JsonElement nameEl )
                    && nameEl.ValueKind == JsonValueKind.String)
                {
                    string? n = DecodeHtml( nameEl.GetString() )?.Trim();
                    if (!string.IsNullOrWhiteSpace( n ))
                    {
                        result.Add( n );
                    }
                }
            }
        }

        if (attr.TryGetProperty( "options", out JsonElement options )
            && options.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement opt in options.EnumerateArray())
            {
                if (opt.ValueKind == JsonValueKind.String)
                {
                    string? n = DecodeHtml( opt.GetString() )?.Trim();
                    if (!string.IsNullOrWhiteSpace( n ))
                    {
                        result.Add( n );
                    }
                }
            }
        }

        if (result.Count == 0)
        {
            string? first = FirstWooAttributeTermName( attr );
            if (!string.IsNullOrWhiteSpace( first ))
            {
                result.Add( first );
            }
        }

        return result;
    }

    private static decimal? ExtractWooProductSalePrice( JsonElement product )
    {
        // WC Store API: prices.price / prices.sale_price as minor units.
        if (product.TryGetProperty( "prices", out JsonElement prices )
            && prices.ValueKind == JsonValueKind.Object)
        {
            int minor = 2;
            if (prices.TryGetProperty( "currency_minor_unit", out JsonElement minorEl )
                && minorEl.TryGetInt32( out int minorVal )
                && minorVal >= 0
                && minorVal <= 4)
            {
                minor = minorVal;
            }

            foreach (string key in new[] { "sale_price", "price", "regular_price" })
            {
                if (!prices.TryGetProperty( key, out JsonElement pe ))
                {
                    continue;
                }

                string? raw = pe.ValueKind switch
                {
                    JsonValueKind.String => pe.GetString(),
                    JsonValueKind.Number => pe.GetRawText(),
                    _ => null
                };
                if (string.IsNullOrWhiteSpace( raw ))
                {
                    continue;
                }

                if (decimal.TryParse(
                        raw,
                        NumberStyles.Number,
                        CultureInfo.InvariantCulture,
                        out decimal minorAmount )
                    && minorAmount > 0m)
                {
                    decimal major = minorAmount / (decimal)Math.Pow( 10, minor );
                    return Math.Round( major, 2, MidpointRounding.AwayFromZero );
                }
            }
        }

        foreach (string key in new[] { "price", "regular_price", "sale_price" })
        {
            if (!product.TryGetProperty( key, out JsonElement pe ))
            {
                continue;
            }

            string? raw = pe.ValueKind switch
            {
                JsonValueKind.String => pe.GetString(),
                JsonValueKind.Number => pe.GetRawText(),
                _ => null
            };
            if (string.IsNullOrWhiteSpace( raw ))
            {
                continue;
            }

            string t = raw.Trim().Replace( ',', '.' );
            if (decimal.TryParse(
                    t,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out decimal value )
                && value > 0m
                && value < 100_000m)
            {
                return Math.Round( value, 2, MidpointRounding.AwayFromZero );
            }
        }

        return null;
    }

    private static bool IsWeightAttributeLabel( string taxonomy, string attrName )
    {
        return taxonomy.Contains( "weight", StringComparison.OrdinalIgnoreCase )
            || taxonomy.Contains( "vaga", StringComparison.OrdinalIgnoreCase )
            || taxonomy.Contains( "ves", StringComparison.OrdinalIgnoreCase )
            || attrName.Contains( "Вага", StringComparison.OrdinalIgnoreCase )
            || attrName.Contains( "Вес", StringComparison.OrdinalIgnoreCase )
            || attrName.Contains( "Weight", StringComparison.OrdinalIgnoreCase )
            || attrName.Contains( "кг", StringComparison.OrdinalIgnoreCase )
            || attrName.Contains( "г.", StringComparison.OrdinalIgnoreCase );
    }

    private static decimal? ExtractWooProductWeightKg( JsonElement product, string? weightAttr )
    {
        if (product.TryGetProperty( "weight", out JsonElement weightEl ))
        {
            decimal? fromProp = ParseWeightToKg( weightEl.ValueKind switch
            {
                JsonValueKind.String => weightEl.GetString(),
                JsonValueKind.Number => weightEl.GetRawText(),
                _ => null
            } );
            if (fromProp is > 0m)
            {
                return fromProp;
            }
        }

        // Some stores put weight under shipping / dimensions.
        if (product.TryGetProperty( "dimensions", out JsonElement dims )
            && dims.ValueKind == JsonValueKind.Object
            && dims.TryGetProperty( "weight", out JsonElement dimWeight ))
        {
            decimal? fromDim = ParseWeightToKg(
                dimWeight.ValueKind == JsonValueKind.String
                    ? dimWeight.GetString()
                    : dimWeight.ValueKind == JsonValueKind.Number
                        ? dimWeight.GetRawText()
                        : null );
            if (fromDim is > 0m)
            {
                return fromDim;
            }
        }

        return ParseWeightToKg( weightAttr );
    }

    private static decimal? ParseWeightToKg( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string t = raw.Trim().ToLowerInvariant()
            .Replace( ',', '.' )
            .Replace( '\u00a0', ' ' );

        // Extract first number.
        Match m = Regex.Match( t, @"(\d+(?:\.\d+)?)" );
        if (!m.Success
            || !decimal.TryParse(
                m.Groups[1].Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal value )
            || value <= 0m)
        {
            return null;
        }

        bool grams = Regex.IsMatch( t, @"\b(g|gr|gram|grams|г|гр|грам)\b" )
            || (t.Contains( 'г' ) && !t.Contains( "кг" ) && !t.Contains( "kg" ));
        bool kilograms = Regex.IsMatch( t, @"\b(kg|кг)\b" );

        if (grams && !kilograms)
        {
            value /= 1000m;
        }
        else if (!kilograms && !grams && value > 20m)
        {
            // Bare numbers like "350" on book shops are usually grams.
            value /= 1000m;
        }

        if (value <= 0m || value > 50m)
        {
            return null;
        }

        return Math.Round( value, 3, MidpointRounding.AwayFromZero );
    }

    private static List<string> ExtractWooProductImageUrls( JsonElement product )
    {
        List<string> urls = new();
        HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );

        void TryAdd( string? src )
        {
            if (string.IsNullOrWhiteSpace( src )
                || !Uri.TryCreate( src.Trim(), UriKind.Absolute, out Uri? uri )
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            string trimmed = src.Trim();
            // Prefer full-size: strip common WP size suffixes when possible.
            string normalized = Regex.Replace(
                trimmed,
                @"-\d+x\d+(?=\.(?:jpe?g|png|webp|gif)$)",
                string.Empty,
                RegexOptions.IgnoreCase );
            if (seen.Add( normalized ))
            {
                urls.Add( normalized );
            }
            else if (seen.Add( trimmed ))
            {
                urls.Add( trimmed );
            }
        }

        if (product.TryGetProperty( "images", out JsonElement images )
            && images.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement image in images.EnumerateArray())
            {
                if (image.TryGetProperty( "src", out JsonElement srcEl )
                    && srcEl.ValueKind == JsonValueKind.String)
                {
                    TryAdd( srcEl.GetString() );
                }
            }
        }

        // WP REST with &_embed=1 — featured media lives under _embedded, not images[].
        if (product.TryGetProperty( "_embedded", out JsonElement embedded )
            && embedded.ValueKind == JsonValueKind.Object)
        {
            foreach (string key in new[] { "wp:featuredmedia", "wp:featured_media" })
            {
                if (!embedded.TryGetProperty( key, out JsonElement mediaArr )
                    || mediaArr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement media in mediaArr.EnumerateArray())
                {
                    if (media.TryGetProperty( "source_url", out JsonElement sourceUrl )
                        && sourceUrl.ValueKind == JsonValueKind.String)
                    {
                        TryAdd( sourceUrl.GetString() );
                    }

                    if (media.TryGetProperty( "media_details", out JsonElement details )
                        && details.ValueKind == JsonValueKind.Object
                        && details.TryGetProperty( "sizes", out JsonElement sizes )
                        && sizes.ValueKind == JsonValueKind.Object
                        && sizes.TryGetProperty( "full", out JsonElement full )
                        && full.ValueKind == JsonValueKind.Object
                        && full.TryGetProperty( "source_url", out JsonElement fullUrl )
                        && fullUrl.ValueKind == JsonValueKind.String)
                    {
                        TryAdd( fullUrl.GetString() );
                    }
                }
            }
        }

        if (product.TryGetProperty( "yoast_head_json", out JsonElement yoast )
            && yoast.ValueKind == JsonValueKind.Object
            && yoast.TryGetProperty( "og_image", out JsonElement ogImages )
            && ogImages.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement og in ogImages.EnumerateArray())
            {
                if (og.ValueKind == JsonValueKind.Object
                    && og.TryGetProperty( "url", out JsonElement ogUrl )
                    && ogUrl.ValueKind == JsonValueKind.String)
                {
                    TryAdd( ogUrl.GetString() );
                }
            }
        }

        return urls;
    }

    private static string? ExtractWooProductCoverUrl( JsonElement product )
    {
        List<string> urls = ExtractWooProductImageUrls( product );
        return urls.Count > 0 ? urls[0] : null;
    }

    private static string? ExtractWooProductDescription( JsonElement product )
    {
        string? shortHtml = null;
        string? fullHtml = null;

        if (product.TryGetProperty( "short_description", out JsonElement shortEl )
            && shortEl.ValueKind == JsonValueKind.String)
        {
            shortHtml = shortEl.GetString();
        }

        if (product.TryGetProperty( "description", out JsonElement descEl ))
        {
            if (descEl.ValueKind == JsonValueKind.String)
            {
                fullHtml = descEl.GetString();
            }
            else if (descEl.ValueKind == JsonValueKind.Object
                && descEl.TryGetProperty( "rendered", out JsonElement rendered ))
            {
                fullHtml = rendered.GetString();
            }
        }

        // WP REST uses content.rendered instead of description.
        if (string.IsNullOrWhiteSpace( fullHtml )
            && product.TryGetProperty( "content", out JsonElement contentEl ))
        {
            if (contentEl.ValueKind == JsonValueKind.String)
            {
                fullHtml = contentEl.GetString();
            }
            else if (contentEl.ValueKind == JsonValueKind.Object
                && contentEl.TryGetProperty( "rendered", out JsonElement contentRendered ))
            {
                fullHtml = contentRendered.GetString();
            }
        }

        // Prefer the longer useful blurb (shop short_description is often truncated card text).
        string? shortPlain = string.IsNullOrWhiteSpace( shortHtml )
            ? null
            : StripHtmlTags( DecodeHtml( shortHtml ) ?? string.Empty ).Trim();
        string? fullPlain = string.IsNullOrWhiteSpace( fullHtml )
            ? null
            : StripHtmlTags( DecodeHtml( fullHtml ) ?? string.Empty ).Trim();
        string? plain = PreferRicherDescription( shortPlain, fullPlain );
        if (string.IsNullOrWhiteSpace( plain ) || plain.Length < 20)
        {
            return null;
        }

        // Cap for the edit form / Shopify body.
        return CapDescription( plain );
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

            BookLookupCandidateDto? parsed = ParseCatalogPageContent( raw, cleanUrl );
            if (parsed is null || string.IsNullOrWhiteSpace( parsed.Title ))
            {
                return null;
            }

            _logger.LogInformation(
                "Book lookup from-url via Tavily extract: {Url} → {Title} / {Author} cover={HasCover}",
                cleanUrl,
                parsed.Title,
                parsed.Author,
                !string.IsNullOrWhiteSpace( parsed.CoverImageUrl ) );
            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "from-url extract failed for {Url}", cleanUrl );
            return null;
        }
    }

    /// <summary>
    /// Parse shop / catalog HTML or Tavily markdown/plain into a book candidate.
    /// Handles LiveLib-style labels where the value is on the next line after "Издательство:".
    /// </summary>
    private static BookLookupCandidateDto? ParseCatalogPageContent( string raw, string cleanUrl )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        // Keep newlines so LiveLib "Label:\nValue" fields parse; StripHtmlTags collapses
        // everything to one line and ExtractLabeledField then swallows the whole page tail.
        string plain = HtmlToPlainPreserveLines( raw );
        string? rawTitle = ExtractMetaContent( raw, "og:title" )
            ?? ExtractHtmlTagText( raw, "h1" )
            ?? ExtractMarkdownHeading( raw )
            ?? ExtractHtmlTagText( raw, "title" )
            ?? ExtractLabeledField( plain, "Назва", "Title", "Тытул", "Название" );
        rawTitle = DecodeHtml( rawTitle );

        (string titleFromCatalog, string? authorFromCatalogTitle) =
            SplitCatalogTitleWithAuthor( rawTitle );
        string? title = FirstNonEmpty( titleFromCatalog, rawTitle );

        if (string.IsNullOrWhiteSpace( title ) || LooksLikeBotWallTitle( title ))
        {
            title = plain
                .Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
                .Select( l => Regex.Replace( l, @"^#+\s*", string.Empty ).Trim() )
                .Where( l => l.Length is >= 3 and <= 220 )
                .FirstOrDefault( l =>
                    !LooksLikeBotWallTitle( l )
                    && !l.StartsWith( "http", StringComparison.OrdinalIgnoreCase )
                    && !Regex.IsMatch(
                        l,
                        @"^(Home|Галоўная|Главная|Каш|Cart|Menu|Хочу|Ещё|Еще)\b",
                        RegexOptions.IgnoreCase ) );
            (titleFromCatalog, authorFromCatalogTitle) = SplitCatalogTitleWithAuthor( title );
            title = FirstNonEmpty( titleFromCatalog, title );
        }

        if (string.IsNullOrWhiteSpace( title ) || LooksLikeBotWallTitle( title ))
        {
            return null;
        }

        string? author = DecodeHtml(
            authorFromCatalogTitle
            ?? ExtractMetaContent( raw, "book:author" )
            ?? ExtractRelAuthor( raw )
            ?? ExtractAuthorNearTitle( plain, title )
            ?? ExtractLabeledField(
                plain,
                "Аўтар",
                "Автор",
                "Author",
                "Аўтары",
                "Авторы" )
            // LiveLib often puts the profile URL in <meta name="author"> — try last and sanitize.
            ?? ExtractMetaContent( raw, "author" ) );
        author = NormalizeAuthorCandidate( author );

        string? coverImageUrl = PreferRealCoverImageUrl(
            ExtractCatalogCoverImageUrl( raw, cleanUrl ),
            DecodeHtml( ExtractMetaContent( raw, "og:image" ) ),
            DecodeHtml( ExtractMetaContent( raw, "twitter:image" ) ),
            DecodeHtml( ExtractMetaContent( raw, "twitter:image:src" ) ) );

        string? metaDescription = DecodeHtml(
            ExtractMetaContent( raw, "og:description" )
            ?? ExtractMetaContent( raw, "description" ) );
        if (!string.IsNullOrWhiteSpace( metaDescription ))
        {
            metaDescription = StripHtmlTags( metaDescription ).Trim();
        }

        string? bodyDescription = ExtractDescriptionParagraph( plain, title, author )
            ?? ExtractDescriptionFromHtml( raw );
        string? description = PreferRicherDescription( metaDescription, bodyDescription );
        description = CapDescription( description );

        // Free-text blob for universal label scraping (any shop / catalog / blog).
        string textBlob = string.Join(
            "\n",
            new[] { plain, description }.Where( s => !string.IsNullOrWhiteSpace( s ) ) );

        string? publisher = ExtractLabeledField(
            textBlob,
            "Выдавец",
            "Выдавецтва",
            "Издатель",
            "Издательство",
            "Publisher",
            "Wydawca",
            "Wydawnictwo" );
        if (string.IsNullOrWhiteSpace( publisher ))
        {
            publisher = BookBibliographicFields.ExtractPublisherFromText( textBlob );
        }

        string? isbn = ExtractIsbnFromText( textBlob );

        string? yearRaw = ExtractLabeledField(
            textBlob,
            "Год издания",
            "Год выдання",
            "Год издательства",
            "Year",
            "Year of publication",
            "Rok wydania",
            "Rok" );
        string? languageRaw = ExtractLabeledField(
            textBlob,
            "Язык",
            "Мова",
            "Language",
            "Język" );
        string? placeRaw = ExtractLabeledField(
            textBlob,
            "Месца выхаду",
            "Месца выдання",
            "Место издания",
            "Miejsce wydania",
            "Place of publication",
            "City" );
        if (string.IsNullOrWhiteSpace( placeRaw ))
        {
            placeRaw = BookBibliographicFields.ExtractPlaceFromText( textBlob );
        }

        string? ageRaw = ExtractLabeledField(
            textBlob,
            "Возрастные ограничения",
            "Возрастное ограничение",
            "Возрастная категория",
            "Узрост",
            "Ўзрост",
            "Возраст",
            "Age",
            "Age rating",
            "Age restriction" );
        string? pagesRaw = ExtractLabeledField(
            textBlob,
            "Количество страниц",
            "Колькасць старонак",
            "Старонак",
            "Pages",
            "Page count",
            "Liczba stron" );
        string? coverRaw = ExtractLabeledField(
            textBlob,
            "Вокладка",
            "Обложка",
            "Переплёт",
            "Переплет",
            "Binding",
            "Cover",
            "Oprawa" );

        if (!string.IsNullOrWhiteSpace( placeRaw ))
        {
            // LiveLib: "Беласток. Выдавец: Offset-Print" → city + publisher.
            Match vyd = Regex.Match(
                placeRaw,
                @"^(?<city>.+?)\s*(?:Выдавец|Издатель|Publisher)\s*:\s*(?<pub>.+)$",
                RegexOptions.IgnoreCase );
            if (vyd.Success)
            {
                if (string.IsNullOrWhiteSpace( publisher ))
                {
                    publisher = vyd.Groups["pub"].Value.Trim().Trim( '*', '_', '#' );
                }

                placeRaw = vyd.Groups["city"].Value.Trim().TrimEnd( '.', ';', '|', ' ' );
            }
            else
            {
                int cut = placeRaw.IndexOfAny( ['.', ';', '|'] );
                if (cut > 1 && cut < 40)
                {
                    placeRaw = placeRaw[..cut].Trim();
                }
            }
        }

        decimal? salePrice = ExtractPriceFromPlainText( plain );
        (string titleOnly, string? authorFromTitle) = SplitAuthorFromTitle( title, author );
        string cleaned = CleanBookTitle(
            string.IsNullOrWhiteSpace( titleOnly ) ? title : titleOnly );
        if (string.IsNullOrWhiteSpace( cleaned ))
        {
            return null;
        }

        string? resolvedAuthor = authorFromTitle ?? author;
        if (!string.IsNullOrWhiteSpace( resolvedAuthor ))
        {
            resolvedAuthor = NormalizeAuthorCandidate( resolvedAuthor );
            if (!string.IsNullOrWhiteSpace( resolvedAuthor ))
            {
                resolvedAuthor = FormatAuthorFirstLast( resolvedAuthor.Trim(), null );
            }
        }
        else if (IsWooProductPathUrl( cleanUrl )
            && TryParseProductUrlSlug( cleanUrl, out _, out string? slugAuthor )
            && !string.IsNullOrWhiteSpace( slugAuthor ))
        {
            // Kamunikat-style /pradukt/firstname-lastname-title only — not LiveLib /book/id-…
            resolvedAuthor = FormatAuthorFirstLast( slugAuthor, null );
        }

        return new BookLookupCandidateDto
        {
            Title = cleaned,
            Author = resolvedAuthor,
            Isbn = isbn,
            Publisher = NullIfWhiteSpace( publisher ),
            Description = NullIfWhiteSpace( description ),
            CoverImageUrl = coverImageUrl,
            SalePrice = salePrice,
            CoverType = BookProductCoverType.Normalize( coverRaw ),
            AgeRating = BookAgeRating.NormalizeLabeled( ageRaw )
                ?? BookAgeRating.Normalize( description )
                ?? BookAgeRating.Normalize( plain ),
            Language = BookBibliographicFields.ResolveLanguage(
                languageRaw,
                description,
                cleaned ),
            PlaceOfPublication = BookBibliographicFields.NormalizePlace( placeRaw )
                ?? BookBibliographicFields.ExtractPlaceFromText( placeRaw )
                ?? BookBibliographicFields.ExtractPlaceFromText( textBlob ),
            PageCount = BookBibliographicFields.NormalizePageCount( pagesRaw ),
            Year = BookBibliographicFields.NormalizeYear( yearRaw )
                ?? BookBibliographicFields.NormalizeYear( description ),
            Url = cleanUrl,
            Source = "manual",
            Snippet = TruncateSnippet( plain ),
        };
    }

    private static bool IsWooProductPathUrl( string url )
    {
        if (!Uri.TryCreate( url, UriKind.Absolute, out Uri? uri ))
        {
            return false;
        }

        return Regex.IsMatch(
            uri.AbsolutePath,
            @"/(?:pradukt|produkt|product|kniharnia|kniha|shop|store)/",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );
    }

    private static (string Title, string? Author) SplitCatalogTitleWithAuthor( string? raw )
    {
        string t = (raw ?? string.Empty).Trim();
        if (t.Length < 3)
        {
            return (t, null);
        }

        // Shop/card blurbs: «Siva zozula». Автор: Виктор Стахвюк
        Match labeled = Regex.Match(
            t,
            @"^(?<title>.+?)\s*[.…]?\s*(?:Автор(?:ы)?|Аўтар(?:ы)?|Author(?:s)?)\s*[:：]\s*(?<author>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );
        if (labeled.Success)
        {
            string title = UnwrapTitleQuotes( labeled.Groups["title"].Value.Trim() );
            string author = labeled.Groups["author"].Value.Trim().Trim( '.', ';', ',' );
            if (title.Length >= 1 && author.Length >= 2)
            {
                return (title, author);
            }
        }

        // LiveLib og:title: "Книга Siva zozula — Виктор Стахвюк"
        Match m = Regex.Match(
            t,
            @"^(?:Книга|Book)\s+(?<title>.+?)\s*[—–−|]\s*(?<author>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );
        if (m.Success)
        {
            return (UnwrapTitleQuotes( m.Groups["title"].Value.Trim() ), m.Groups["author"].Value.Trim());
        }

        m = Regex.Match(
            t,
            @"^(?<title>.{3,160}?)\s+[—–−]\s+(?<author>[^\d]{3,80})$",
            RegexOptions.CultureInvariant );
        if (m.Success)
        {
            string author = m.Groups["author"].Value.Trim();
            if (author.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).Length is >= 1 and <= 4)
            {
                return (UnwrapTitleQuotes( m.Groups["title"].Value.Trim() ), author);
            }
        }

        // Skaryna / many shops: "Title / Author"
        Match slash = Regex.Match(
            t,
            @"^(?<title>.{3,160}?)\s*/\s*(?<author>[^\d/]{3,80})$",
            RegexOptions.CultureInvariant );
        if (slash.Success)
        {
            string author = slash.Groups["author"].Value.Trim();
            string[] authorParts = author.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
            if (authorParts.Length is >= 1 and <= 5
                && !Regex.IsMatch( author, @"add to cart|shop|cart|price|£|\$", RegexOptions.IgnoreCase ))
            {
                return (UnwrapTitleQuotes( slash.Groups["title"].Value.Trim() ), author);
            }
        }

        return (UnwrapTitleQuotes( t ), null);
    }

    private static string UnwrapTitleQuotes( string title )
    {
        string t = title.Trim();
        if (t.Length >= 2)
        {
            char a = t[0];
            char b = t[^1];
            if ((a == '«' && b == '»')
                || (a == '"' && b == '"')
                || (a == '\'' && b == '\'')
                || (a == '“' && b == '”')
                || (a == '„' && b == '“')
                || (a == '„' && b == '”'))
            {
                t = t[1..^1].Trim();
            }
        }

        return t;
    }

    private static string? ExtractAuthorNearTitle( string plain, string title )
    {
        if (string.IsNullOrWhiteSpace( plain ) || string.IsNullOrWhiteSpace( title ))
        {
            return null;
        }

        string[] lines = plain
            .Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
            .Select( l => Regex.Replace( l, @"^#+\s*", string.Empty ).Trim() )
            .Where( l => l.Length > 0 )
            .ToArray();

        string titleKey = Regex.Replace( title, @"\s+", " " ).Trim();
        for (int i = 0; i < lines.Length - 1; i++)
        {
            string line = Regex.Replace( lines[i], @"\s+", " " ).Trim();
            if (!line.Equals( titleKey, StringComparison.OrdinalIgnoreCase )
                && !line.Contains( titleKey, StringComparison.OrdinalIgnoreCase ))
            {
                continue;
            }

            string next = Regex.Replace( lines[i + 1], @"\s+", " " ).Trim();
            if (LooksLikePersonNameLine( next ))
            {
                return next;
            }
        }

        return null;
    }

    private static bool LooksLikePersonNameLine( string line )
    {
        if (string.IsNullOrWhiteSpace( line ) || line.Length is < 3 or > 80)
        {
            return false;
        }

        if (Regex.IsMatch(
                line,
                @"ISBN|Издател|Выдавец|Год|Язык|Мова|Хочу|реценз|цитат|http|www\.|livelib|/author/|\d{4}",
                RegexOptions.IgnoreCase ))
        {
            return false;
        }

        string[] parts = line.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
        return parts.Length is >= 2 and <= 4
            && parts.All( p => p.Length >= 2 && !p.Any( char.IsDigit ) );
    }

    private static string? ExtractDescriptionParagraph(
        string plain,
        string? title,
        string? author )
    {
        if (string.IsNullOrWhiteSpace( plain ))
        {
            return null;
        }

        string? best = null;
        foreach (string line in plain.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ))
        {
            string t = Regex.Replace( line, @"^#+\s*", string.Empty ).Trim();
            t = StripDescriptionChrome( t );
            if (t.Length < 80 || LooksLikeJunkDescription( t ))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace( title )
                && t.Contains( title, StringComparison.OrdinalIgnoreCase )
                && t.Length < title.Length + 40)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace( author )
                && t.Equals( author, StringComparison.OrdinalIgnoreCase ))
            {
                continue;
            }

            if (Regex.IsMatch(
                    t,
                    @"^(Издател|Выдавец|ISBN|Год|Язык|Мова|Месца|Место|Возраст|Хочу|Реценз|Скрыть|Подробнее)",
                    RegexOptions.IgnoreCase ))
            {
                continue;
            }

            // Skip obvious truncated blurbs when a fuller paragraph exists.
            if (LooksLikeTruncatedBlurb( t ) && t.Length < 220)
            {
                continue;
            }

            if (best is null || t.Length > best.Length)
            {
                best = t;
            }
        }

        return CapDescription( best );
    }

    /// <summary>
    /// LiveLib / catalog HTML often keeps the full annotation in a dedicated block
    /// while og:description is a short preview ending with "…".
    /// </summary>
    private static string? ExtractDescriptionFromHtml( string raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string[] patterns =
        [
            // LiveLib annotation / about blocks
            @"<(?:div|section|p)[^>]*(?:class|itemprop)=[""'][^""']*(?:annotation|description|about|item-about|book-description|lenta-card__text)[^""']*[""'][^>]*>(?<c>[\s\S]{80,12000}?)</(?:div|section|p)>",
            @"<meta[^>]+itemprop=[""']description[""'][^>]+content=[""'](?<c>[^""']{80,})[""']",
            @"<meta[^>]+content=[""'](?<c>[^""']{80,})[""'][^>]+itemprop=[""']description[""']",
        ];

        string? best = null;
        foreach (string pattern in patterns)
        {
            foreach (Match m in Regex.Matches( raw, pattern, RegexOptions.IgnoreCase ))
            {
                string chunk = StripHtmlTags( DecodeHtml( m.Groups["c"].Value ) ?? string.Empty );
                chunk = StripDescriptionChrome( chunk );
                if (chunk.Length < 80 || LooksLikeJunkDescription( chunk ))
                {
                    continue;
                }

                if (best is null || chunk.Length > best.Length)
                {
                    best = chunk;
                }
            }
        }

        return CapDescription( best );
    }

    private static string? PreferRicherDescription( string? a, string? b )
    {
        string? left = SanitizeDescriptionCandidate( a );
        string? right = SanitizeDescriptionCandidate( b );
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        bool leftTrunc = LooksLikeTruncatedBlurb( left );
        bool rightTrunc = LooksLikeTruncatedBlurb( right );
        if (leftTrunc && !rightTrunc && right.Length >= left.Length)
        {
            return right;
        }

        if (rightTrunc && !leftTrunc && left.Length >= right.Length)
        {
            return left;
        }

        // Prefer substantially longer body text over short meta preview.
        if (right.Length >= left.Length + 40)
        {
            return right;
        }

        if (left.Length >= right.Length + 40)
        {
            return left;
        }

        return right.Length > left.Length ? right : left;
    }

    private static string? SanitizeDescriptionCandidate( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ) || raw.Trim().Length < 20)
        {
            return null;
        }

        string t = StripDescriptionChrome( raw );
        if (LooksLikeJunkDescription( t ))
        {
            return null;
        }

        return NullIfWhiteSpace( t );
    }

    private static bool LooksLikeTruncatedBlurb( string text )
    {
        string t = text.TrimEnd();
        return t.EndsWith( "…", StringComparison.Ordinal )
            || t.EndsWith( "...", StringComparison.Ordinal )
            || t.EndsWith( "..", StringComparison.Ordinal );
    }

    /// <summary>
    /// Reject Next.js/PageSpeed/script blobs accidentally scraped into "description".
    /// </summary>
    private static bool LooksLikeJunkDescription( string text )
    {
        if (string.IsNullOrWhiteSpace( text ))
        {
            return true;
        }

        string t = text.Trim();
        if (Regex.IsMatch(
                t,
                @"pagespeed\.|lazyLoadImages|__next_f|\$Sreact|self\.__next|\$RB\s*=|\$RV\s*=|function\s*\(|=>\s*\{|performance\.now\(|webpackJsonp|__NEXT_DATA__|window\.__|document\.cookie|<\/?script\b",
                RegexOptions.IgnoreCase ))
        {
            return true;
        }

        // Dense code-ish punctuation vs letters.
        int letters = t.Count( char.IsLetter );
        int codey = t.Count( c => c is '{' or '}' or ';' or '=' or '(' or ')' or '[' or ']' or '$' or '_' );
        if (letters > 0 && codey >= 12 && codey * 2 >= letters)
        {
            return true;
        }

        return false;
    }

    private static string StripDescriptionChrome( string text )
    {
        string t = Regex.Replace( text ?? string.Empty, @"\s+", " " ).Trim();
        // Cut shop chrome / related products that Tavily often appends after the blurb.
        Match cut = Regex.Match(
            t,
            @"\s*(?:Additional information|You May Also Like|Related products|Related\b|Tags:|ShareTweet|Add to cart|##\s*Additional|##\s*You May|##\s*Related)\b",
            RegexOptions.IgnoreCase );
        if (cut.Success && cut.Index >= 60)
        {
            t = t[..cut.Index].Trim();
        }

        t = Regex.Replace(
            t,
            @"\s*(?:Скрыть|Подробнее|Показать\s+полностью|Читать\s+(?:далее|дальше)|Read\s+more|Show\s+more)\s*$",
            string.Empty,
            RegexOptions.IgnoreCase );
        // Drop leftover markdown image crumbs.
        t = Regex.Replace( t, @"!\[[^\]]*\]\([^)]+\)", " " );
        t = Regex.Replace( t, @"\[[^\]]*\]\([^)]+\)", " " );
        t = Regex.Replace( t, @"\*+", " " );
        t = Regex.Replace( t, @"\s+", " " ).Trim();
        return t;
    }

    private static string? CapDescription( string? text )
    {
        string? cleaned = SanitizeDescriptionCandidate( text );
        if (cleaned is null)
        {
            return null;
        }

        return cleaned.Length <= 8000 ? cleaned : cleaned[..8000].TrimEnd() + "…";
    }

    /// <summary>
    /// Prefer real book-cover CDN assets over social cards (LiveLib /opengraph-image).
    /// </summary>
    private static string? PreferRealCoverImageUrl( params string?[] candidates )
    {
        string? fallback = null;
        foreach (string? raw in candidates)
        {
            string? url = NormalizeAbsoluteImageUrl( raw );
            if (url is null)
            {
                continue;
            }

            if (IsSocialCardImageUrl( url ))
            {
                fallback ??= url;
                continue;
            }

            return UpgradeCatalogCoverImageUrl( url );
        }

        return fallback is null ? null : UpgradeCatalogCoverImageUrl( fallback );
    }

    private static string? ExtractCatalogCoverImageUrl( string raw, string pageUrl )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string? bookId = null;
        Match idMatch = Regex.Match(
            pageUrl ?? string.Empty,
            @"/book/(?<id>\d+)",
            RegexOptions.IgnoreCase );
        if (idMatch.Success)
        {
            bookId = idMatch.Groups["id"].Value;
        }

        string? best = null;
        int bestScore = -1;
        foreach (Match img in Regex.Matches(
                     raw,
                     @"<img\b(?<attrs>[^>]*)>",
                     RegexOptions.IgnoreCase ))
        {
            string attrs = img.Groups["attrs"].Value;
            string? src = FirstImgSrc( attrs );
            if (string.IsNullOrWhiteSpace( src ))
            {
                continue;
            }

            string? abs = NormalizeAbsoluteImageUrl( src, pageUrl );
            if (abs is null || IsSocialCardImageUrl( abs ))
            {
                continue;
            }

            int score = ScoreCatalogCoverCandidate( abs, attrs, bookId );
            if (score > bestScore)
            {
                bestScore = score;
                best = abs;
            }
        }

        return bestScore >= 20 ? best : null;
    }

    private static string? FirstImgSrc( string attrs )
    {
        foreach (string attr in new[]
                 {
                     "data-pagespeed-lazy-src",
                     "data-src",
                     "data-original",
                     "src",
                 })
        {
            Match m = Regex.Match(
                attrs,
                $@"{attr}\s*=\s*[""'](?<v>[^""']+)[""']",
                RegexOptions.IgnoreCase );
            if (m.Success)
            {
                string v = m.Groups["v"].Value.Trim();
                if (!string.IsNullOrWhiteSpace( v )
                    && !v.StartsWith( "data:", StringComparison.OrdinalIgnoreCase ))
                {
                    return v;
                }
            }
        }

        return null;
    }

    private static int ScoreCatalogCoverCandidate( string url, string attrs, string? bookId )
    {
        string low = url.ToLowerInvariant();
        string a = attrs.ToLowerInvariant();
        int score = 0;

        if (low.Contains( "/boocover/", StringComparison.Ordinal ))
        {
            score += 80;
        }

        if (a.Contains( "cover_covermedia", StringComparison.Ordinal )
            || a.Contains( "book-cover", StringComparison.Ordinal )
            || a.Contains( "cover-image", StringComparison.Ordinal )
            || a.Contains( "product-image", StringComparison.Ordinal ))
        {
            score += 40;
        }

        if (!string.IsNullOrWhiteSpace( bookId )
            && low.Contains( "/" + bookId + "/", StringComparison.Ordinal ))
        {
            score += 50;
        }

        if (low.Contains( "userpic", StringComparison.Ordinal )
            || low.Contains( "selepic", StringComparison.Ordinal )
            || low.Contains( "/avatar", StringComparison.Ordinal )
            || a.Contains( "userimage", StringComparison.Ordinal )
            || a.Contains( "selectioncard", StringComparison.Ordinal ))
        {
            score -= 100;
        }

        if (Regex.IsMatch( low, @"/\d{2,3}x\d{2,3}/" ))
        {
            score += 5; // sized cover variants
        }

        return score;
    }

    private static bool IsSocialCardImageUrl( string url )
    {
        string low = url.ToLowerInvariant();
        return low.Contains( "/opengraph-image", StringComparison.Ordinal )
            || low.Contains( "opengraph", StringComparison.Ordinal )
            || low.Contains( "/twitter-image", StringComparison.Ordinal )
            || low.Contains( "og-image", StringComparison.Ordinal )
            || low.Contains( "share-image", StringComparison.Ordinal )
            || low.Contains( "social-card", StringComparison.Ordinal );
    }

    private static string UpgradeCatalogCoverImageUrl( string url )
    {
        // LiveLib: …/boocover/{id}/200x305/… → …/l/… (slightly larger real cover JPEG).
        string upgraded = Regex.Replace(
            url,
            @"(/boocover/\d+)/(\d+x\d+)/",
            "$1/l/",
            RegexOptions.IgnoreCase );
        return upgraded;
    }

    private static string? NormalizeAbsoluteImageUrl( string? raw, string? pageUrl = null )
    {
        string? decoded = DecodeHtml( raw )?.Trim();
        if (string.IsNullOrWhiteSpace( decoded ))
        {
            return null;
        }

        if (Uri.TryCreate( decoded, UriKind.Absolute, out Uri? abs )
            && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
        {
            return abs.ToString();
        }

        if (!string.IsNullOrWhiteSpace( pageUrl )
            && Uri.TryCreate( pageUrl, UriKind.Absolute, out Uri? page )
            && Uri.TryCreate( page, decoded, out Uri? resolved )
            && (resolved.Scheme == Uri.UriSchemeHttp || resolved.Scheme == Uri.UriSchemeHttps))
        {
            return resolved.ToString();
        }

        return null;
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

        // Longer labels first so "Возрастные ограничения" wins over "Возраст".
        string[] ordered = labels
            .Where( l => !string.IsNullOrWhiteSpace( l ) )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .OrderByDescending( l => l.Length )
            .ToArray();
        string alt = string.Join( "|", ordered.Select( Regex.Escape ) );
        // Stop before the next bibliographic label on the same collapsed line.
        const string NextLabelStop =
            @"(?=\s*(?:$|\r?\n|(?:Выдавец(?:тва)?|Издател(?:ьство|ь)?|Publisher|Wydawca|Wydawnictwo|ISBN|ІСБН|Год(?:\s+издания|\s+выдання)?|Year|Язык|Мова|Language|Месца|Место|Place|Возраст(?:ные\s+ограничения|ное\s+ограничение|ная\s+категория)?|Узрост|Ўзрост|Age|Количество|Колькасць|Старонок|Pages|Вокладка|Обложка|Перепл[её]т|Binding|Cover|Аўтар|Автор|Author|Назва|Title|Рэдактар|Редактор|Editor)\s*[:：\-–—]))";

        // Same line: "Издательство: Offset-Print"
        Match m = Regex.Match(
            text,
            $@"(?im)^\s*(?:{alt})\s*[:：\-–—]\s*(?<v>\S.*?){NextLabelStop}" );
        if (m.Success)
        {
            string? cleaned = CleanLabeledValue( m.Groups["v"].Value, alt );
            if (cleaned is not null)
            {
                return cleaned;
            }
        }

        // Inline anywhere (after StripHtmlTags collapsed newlines into spaces):
        // "… Возрастные ограничения: 16+ Год издания: 2006 …"
        m = Regex.Match(
            text,
            $@"(?i)(?:{alt})\s*[:：\-–—]\s*(?<v>\S.*?){NextLabelStop}" );
        if (m.Success)
        {
            string? cleaned = CleanLabeledValue( m.Groups["v"].Value, alt );
            if (cleaned is not null)
            {
                return cleaned;
            }
        }

        // Next line value (LiveLib layout):
        // Издательство:
        // Offset-Print
        m = Regex.Match(
            text,
            $@"(?im)^\s*(?:{alt})\s*[:：\-–—]\s*\r?\n+\s*(?<v>[^\r\n]{{1,220}})\s*$" );
        if (!m.Success)
        {
            return null;
        }

        return CleanLabeledValue( m.Groups["v"].Value, alt );
    }

    private static string? CleanLabeledValue( string raw, string alt )
    {
        string v = raw.Trim().Trim( '*', '_', '#', '"', '\'', '«', '»' );
        // LiveLib sometimes repeats the label: "ISBN: ISBN: 83-…"
        v = Regex.Replace(
            v,
            $@"^(?:{alt})\s*[:：\-–—]\s*",
            string.Empty,
            RegexOptions.IgnoreCase );
        v = v.Trim().Trim( '*', '_', '#', '"', '\'', '«', '»' );
        if (v.Length is < 1 or > 220)
        {
            return null;
        }

        // Truncate if a mid-value label slipped through.
        Match cut = Regex.Match(
            v,
            @"\s+(?:Выдавец|Издател|Publisher|ISBN|Год|Year|Язык|Мова|Language|Месца|Место|Возраст|Узрост|Age|Количество|Pages|Обложка|Вокладка|Аўтар|Автор|Author)\s*[:：\-–—]",
            RegexOptions.IgnoreCase );
        if (cut.Success && cut.Index > 0)
        {
            v = v[..cut.Index].Trim();
        }

        return v.Length is >= 1 and <= 220 ? v : null;
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
            @"/(?:pradukt|produkt|product|kniharnia)/(?<slug>[^/]+)/?",
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

    public async Task<BookCreateFromLookupResultDto> CreateDraftShellAsync(
        BookCreateDraftShellRequest request,
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

        string? descriptionHtml = string.IsNullOrWhiteSpace( request.DescriptionHtml )
            ? null
            : request.DescriptionHtml.Trim();

        decimal salePrice = request.SalePrice < 0m ? 0m : request.SalePrice;

        string? isbn = IsbnUtil.NormalizePreferHyphens( request.Isbn )
            ?? (request.Isbn ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( isbn ))
        {
            isbn = null;
        }

        decimal? weightKg = null;
        if (request.WeightKg is decimal w && w > 0m)
        {
            weightKg = Math.Round( w, 3, MidpointRounding.AwayFromZero );
        }

        int? quantity = null;
        if (request.Quantity is int q && q > 0)
        {
            quantity = q;
        }

        string? author = NullIfWhiteSpace( request.Author );
        string? coverType = BookProductCoverType.Normalize( request.CoverType );
        string? ageRating = BookAgeRating.Normalize( request.AgeRating )
            ?? NullIfWhiteSpace( request.AgeRating );
        string? format = BookBibliographicFields.NormalizeFormat( request.Format )
            ?? NullIfWhiteSpace( request.Format );
        string? illustrator = NullIfWhiteSpace( request.Illustrator );
        string? language = BookBibliographicFields.NormalizeLanguage( request.Language )
            ?? NullIfWhiteSpace( request.Language );
        string? placeOfPublication = NullIfWhiteSpace( request.PlaceOfPublication );
        string? translation = NullIfWhiteSpace( request.Translation );
        string? publisher = NullIfWhiteSpace( request.Publisher );
        int? pageCount = request.PageCount is int pc && pc > 0
            ? pc
            : null;
        int? year = request.Year is int y && y > 0
            ? y
            : null;
        IReadOnlyList<string>? genres = request.Genres is { Count: > 0 }
            ? ShopifyInventoryService.NormalizeGenreLabels( request.Genres )
            : null;

        string? descriptionPlain = string.IsNullOrWhiteSpace( descriptionHtml )
            ? null
            : StripHtmlTags( descriptionHtml );
        IReadOnlyList<string> seoImageIds = BuildSeoImageIds( request.SeoImageIds );
        (
            string SeoTitle,
            string SeoDescription,
            string Handle,
            List<BookImageAltDto> ImageAlts
        ) seo = await GenerateBookSeoAsync(
            title,
            descriptionPlain,
            author,
            language,
            genres,
            seoImageIds,
            cancellationToken );

        ShopifyInventoryService.CreatedShopifyProduct created =
            await _shopifyInventory.CreateMinimalDraftProductAsync(
                shopSession.Shop,
                shopSession.AccessToken,
                title,
                descriptionHtml,
                salePrice,
                barcode: isbn,
                weightKg: weightKg,
                inventoryQuantity: quantity,
                author: author,
                coverType: coverType,
                ageRating: ageRating,
                format: format,
                illustrator: illustrator,
                language: language,
                placeOfPublication: placeOfPublication,
                translation: translation,
                pageCount: pageCount,
                year: year,
                genres: genres,
                seoTitle: seo.SeoTitle,
                seoDescription: seo.SeoDescription,
                handle: seo.Handle,
                vendor: publisher );

        await _shopifyInventory.TryAssignBookCategoryAsync(
            shopSession.Shop,
            shopSession.AccessToken,
            created.ProductId );

        // Photos: client uploads modal preview bytes via POST books/attach-draft-images.

        string storeSlug = shopSession.Shop.Replace(
            ".myshopify.com",
            "",
            StringComparison.OrdinalIgnoreCase );
        string adminUrl =
            $"https://admin.shopify.com/store/{storeSlug}/products/{created.ProductId}";

        return new BookCreateFromLookupResultDto
        {
            ShopifyProductId = created.ProductId,
            ShopifyVariantId = created.VariantId,
            Title = created.Title,
            ShopifyAdminUrl = adminUrl,
            ImageAlts = seo.ImageAlts,
        };
    }

    public async Task<BookAttachDraftImagesResultDto> AttachDraftImagesAsync(
        BookAttachDraftImagesRequest request,
        CancellationToken cancellationToken )
    {
        ShopifySession shopSession = ShopifySessionReader.Require(
            _httpContextAccessor,
            "Няма Shopify-сесіі. Перазайдзіце праз Shopify." );

        string productId = ShopifyIds.NormalizeProductId(
            (request.ShopifyProductId ?? string.Empty).Trim() );
        if (string.IsNullOrWhiteSpace( productId ))
        {
            throw new InvalidOperationException( "Некарэктны Shopify product id." );
        }

        BookAttachDraftImagesResultDto result = new();
        bool coverAttached = false;
        string? coverAlt = NullIfWhiteSpace( request.CoverImageAlt );
        List<string> extraAlts = (request.AdditionalImageAlts ?? new List<string>())
            .Select( a => (a ?? string.Empty).Trim() )
            .ToList();
        int extraAltIndex = 0;

        // Cover: prefer final styled/temp bytes (what the modal preview shows).
        if (!string.IsNullOrWhiteSpace( request.CoverTempMediaId )
            && _tempMedia.TryGet( request.CoverTempMediaId, out BookTempMediaEntry coverTemp ))
        {
            try
            {
                string mediaId = await _shopifyInventory.AttachProductImageAsync(
                    shopSession.Shop,
                    shopSession.AccessToken,
                    productId,
                    coverTemp.Bytes,
                    GuessImageFileName( coverTemp.Bytes, "cover" ),
                    mimeType: coverTemp.ContentType,
                    alt: coverAlt,
                    tempMediaId: coverTemp.Id,
                    cancellationToken );
                coverAttached = true;
                result.AttachedCount++;
                result.AttachedMediaIds.Add( mediaId );
                _tempMedia.Remove( coverTemp.Id );
            }
            catch (Exception ex)
            {
                _logger.LogWarning( ex, "Temp cover attach failed for {ProductId}", productId );
                result.Errors.Add( $"coverTemp: {ex.Message}" );
            }
        }
        else if (!string.IsNullOrWhiteSpace( request.CoverTempMediaId ))
        {
            result.Errors.Add(
                $"coverTemp: tempMediaId '{request.CoverTempMediaId}' не знойдзены ў кэшы (TTL / няўдалы fetch)." );
        }

        // Fallback: browser-provided base64 of the same preview (still our bytes, not CDN URL).
        if (!coverAttached)
        {
            byte[]? coverBytes = TryDecodeImagePayload( request.CoverImageBase64 );
            if (coverBytes is { Length: > 0 } && coverBytes.Length <= MaxFileBytes)
            {
                try
                {
                    string mediaId = await _shopifyInventory.AttachProductImageAsync(
                        shopSession.Shop,
                        shopSession.AccessToken,
                        productId,
                        coverBytes,
                        GuessImageFileName( coverBytes, "cover" ),
                        alt: coverAlt,
                        cancellationToken: cancellationToken );
                    coverAttached = true;
                    result.AttachedCount++;
                    result.AttachedMediaIds.Add( mediaId );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning( ex, "Base64 cover attach failed for {ProductId}", productId );
                    result.Errors.Add( $"cover: {ex.Message}" );
                }
            }
        }

        if (!coverAttached)
        {
            if (!string.IsNullOrWhiteSpace( request.CoverImageUrl ))
            {
                result.Errors.Add(
                    "cover: CDN URL не загружаецца ў Shopify. Патрэбны tempMediaId з байтамі на серверы." );
            }
            else if (result.Errors.Count == 0)
            {
                result.Errors.Add( "cover: няма байтаў на серверы (tempMediaId / base64)." );
            }
        }

        int extraIndex = 0;
        HashSet<string> attachedTempIds = new( StringComparer.OrdinalIgnoreCase );
        if (!string.IsNullOrWhiteSpace( request.CoverTempMediaId ))
        {
            attachedTempIds.Add( request.CoverTempMediaId.Trim() );
        }

        if (request.AdditionalTempMediaIds is { Count: > 0 })
        {
            foreach (string? rawId in request.AdditionalTempMediaIds)
            {
                if (string.IsNullOrWhiteSpace( rawId ))
                {
                    continue;
                }

                string tempId = rawId.Trim();
                if (!attachedTempIds.Add( tempId ))
                {
                    continue;
                }

                extraIndex++;
                if (!_tempMedia.TryGet( tempId, out BookTempMediaEntry extraTemp ))
                {
                    result.Errors.Add( $"extraTemp{extraIndex}: tempMediaId '{tempId}' не знойдзены." );
                    continue;
                }

                try
                {
                    string? extraAlt = extraAltIndex < extraAlts.Count
                        ? NullIfWhiteSpace( extraAlts[extraAltIndex] )
                        : null;
                    extraAltIndex++;
                    string mediaId = await _shopifyInventory.AttachProductImageAsync(
                        shopSession.Shop,
                        shopSession.AccessToken,
                        productId,
                        extraTemp.Bytes,
                        GuessImageFileName( extraTemp.Bytes, $"extra-{extraIndex}" ),
                        mimeType: extraTemp.ContentType,
                        alt: extraAlt,
                        tempMediaId: extraTemp.Id,
                        cancellationToken );
                    result.AttachedCount++;
                    result.AttachedMediaIds.Add( mediaId );
                    _tempMedia.Remove( extraTemp.Id );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Temp extra image {Index} failed for {ProductId}",
                        extraIndex,
                        productId );
                    result.Errors.Add( $"extraTemp{extraIndex}: {ex.Message}" );
                }
            }
        }

        if (request.AdditionalImageBase64 is { Count: > 0 })
        {
            foreach (string? raw in request.AdditionalImageBase64)
            {
                byte[]? bytes = TryDecodeImagePayload( raw );
                if (bytes is null || bytes.Length == 0 || bytes.Length > MaxFileBytes)
                {
                    continue;
                }

                extraIndex++;
                try
                {
                    string? extraAlt = extraAltIndex < extraAlts.Count
                        ? NullIfWhiteSpace( extraAlts[extraAltIndex] )
                        : null;
                    extraAltIndex++;
                    string mediaId = await _shopifyInventory.AttachProductImageAsync(
                        shopSession.Shop,
                        shopSession.AccessToken,
                        productId,
                        bytes,
                        GuessImageFileName( bytes, $"extra-{extraIndex}" ),
                        alt: extraAlt,
                        cancellationToken: cancellationToken );
                    result.AttachedCount++;
                    result.AttachedMediaIds.Add( mediaId );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Base64 extra image {Index} failed for {ProductId}",
                        extraIndex,
                        productId );
                    result.Errors.Add( $"extra{extraIndex}: {ex.Message}" );
                }
            }
        }

        if (request.AdditionalImageUrls is { Count: > 0 })
        {
            result.Errors.Add(
                "extras: CDN URL не загружаюцца ў Shopify. Патрэбныя additionalTempMediaIds." );
        }

        if (result.AttachedCount == 0)
        {
            _logger.LogWarning(
                "AttachDraftImages attached nothing for {ProductId}. Errors={Errors}",
                productId,
                string.Join( " | ", result.Errors ) );
        }

        return result;
    }

    private static string GuessImageFileName( byte[] bytes, string stem )
    {
        string? contentType = GuessImageContentType( bytes );
        string ext = contentType switch
        {
            "image/png" => "png",
            "image/webp" => "webp",
            "image/gif" => "gif",
            _ => "jpg",
        };
        return $"{stem}.{ext}";
    }



    private async Task<(
        string SeoTitle,
        string SeoDescription,
        string Handle,
        List<BookImageAltDto> ImageAlts )> GenerateBookSeoAsync(
        string title,
        string? descriptionPlain,
        string? author,
        string? language,
        IReadOnlyList<string>? genres,
        IReadOnlyList<string> imageIds,
        CancellationToken cancellationToken )
    {
        string primaryTitle = ExtractPrimaryBookTitle( title );
        string seoLang = ResolveSeoLanguage( language, descriptionPlain, primaryTitle );
        IReadOnlyList<string> ids = BuildSeoImageIds( imageIds );

        string? seoTitle = null;
        string? seoDescription = null;
        List<BookImageAltDto> imageAlts = new();
        BookSeoAnalysis? analysis = null;

        try
        {
            string openAiKey = (_config["OpenAI:ApiKey"] ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace( openAiKey ))
            {
                string model = ResolveOpenAiModel();
                string description = TruncateForPrompt( descriptionPlain, 2500 );
                string authorLine = string.IsNullOrWhiteSpace( author ) ? "(unknown)" : author.Trim();
                string languageLine = string.IsNullOrWhiteSpace( language ) ? "(unknown)" : language.Trim();
                string genresLine = genres is { Count: > 0 }
                    ? string.Join( ", ", genres )
                    : "(none)";
                string langHint = seoLang switch
                {
                    "ru" => "русский",
                    "pl" => "польский",
                    "en" => "английский",
                    _ => "белорусский",
                };

                analysis = await AnalyzeBookForSeoWithOpenAiAsync(
                    openAiKey,
                    model,
                    title,
                    primaryTitle,
                    authorLine,
                    languageLine,
                    genresLine,
                    langHint,
                    description,
                    cancellationToken );

                if (analysis is null)
                {
                    _logger.LogWarning( "OpenAI book analysis empty; SEO will use local fallback." );
                }
                else
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        (string? t, string? d, List<BookImageAltDto> alts) =
                            await WriteBookSeoFromAnalysisWithOpenAiAsync(
                                openAiKey,
                                model,
                                title,
                                primaryTitle,
                                authorLine,
                                languageLine,
                                genresLine,
                                langHint,
                                analysis,
                                ids,
                                cancellationToken,
                                stricter: attempt > 0 );

                        bool titleOk = IsAcceptableSeoTitle( t );
                        bool descOk = IsAcceptableMetaDescription( d, descriptionPlain );
                        if (titleOk && descOk)
                        {
                            seoTitle = t;
                            seoDescription = d;
                            imageAlts = alts;
                            break;
                        }

                        _logger.LogWarning(
                            "OpenAI SEO write attempt {Attempt} rejected (titleOk={TitleOk}, descOk={DescOk}, paste={Paste}, spam={Spam}, titleLen={TitleLen}, descLen={DescLen}, sample={Sample})",
                            attempt + 1,
                            titleOk,
                            descOk,
                            LooksLikeRawDescriptionPaste( d, descriptionPlain ),
                            LooksLikeKeywordSpamMeta( d ),
                            t?.Length ?? 0,
                            d?.Length ?? 0,
                            TruncateForPrompt( d, 120 ) );
                    }
                }
            }
            else
            {
                _logger.LogWarning( "OpenAI:ApiKey is empty; SEO title/description use local fallback." );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "OpenAI SEO bundle failed; using local fallback." );
        }

        if (string.IsNullOrWhiteSpace( seoTitle ) || !IsAcceptableSeoTitle( seoTitle ))
        {
            string quoted = FormatQuotedTitle( primaryTitle, title );
            seoTitle = string.IsNullOrWhiteSpace( author )
                ? $"{quoted} | Kirma.sh"
                : $"{quoted} — {author.Trim()} | Kirma.sh";
        }

        seoTitle = EnsureSeoTitleSuffix( TruncateSeoTitle( seoTitle! ) );
        string handle = ShortenBookHandle( SlugifyLatin( primaryTitle ), primaryTitle );

        if (!IsAcceptableMetaDescription( seoDescription, descriptionPlain ))
        {
            string? fromAnalysis = BuildMetaDescriptionFromAnalysis(
                primaryTitle,
                title,
                author,
                analysis,
                seoLang );
            // Accept analysis-based copy if it is a real book blurb (not spam/paste),
            // even when slightly shorter than the ideal floor.
            if (!string.IsNullOrWhiteSpace( fromAnalysis )
                && !LooksLikeKeywordSpamMeta( fromAnalysis )
                && !LooksLikeRawDescriptionPaste( fromAnalysis, descriptionPlain )
                && !fromAnalysis.Contains( '…' )
                && !fromAnalysis.Contains( "..." ))
            {
                seoDescription = fromAnalysis;
            }
            else
            {
                seoDescription = BuildExpandedFallbackMetaDescription(
                    primaryTitle,
                    title,
                    author,
                    genres,
                    seoLang );
            }
        }

        seoDescription = FinalizeMetaDescription( seoDescription, seoLang );
        imageAlts = NormalizeImageAlts( ids, imageAlts, primaryTitle, title, author, seoLang );

        return (seoTitle, seoDescription, handle, imageAlts);
    }

    private sealed class BookSeoAnalysis
    {
        public string About { get; init; } = string.Empty;
        public string CentralSubject { get; init; } = string.Empty;
        public string? Genre { get; init; }
        public List<string> Themes { get; init; } = new();
        public List<string> People { get; init; } = new();
        public List<string> Places { get; init; } = new();
        public List<string> PeriodsOrEvents { get; init; } = new();
        public string RawJson { get; init; } = string.Empty;
    }

    private async Task<(string? SeoTitle, string? SeoDescription, List<BookImageAltDto> ImageAlts)>
        WriteBookSeoFromAnalysisWithOpenAiAsync(
            string apiKey,
            string model,
            string title,
            string primaryTitle,
            string authorLine,
            string languageLine,
            string genresLine,
            string langHint,
            BookSeoAnalysis analysis,
            IReadOnlyList<string> imageIds,
            CancellationToken cancellationToken,
            bool stricter = false )
    {
        string imagesJson = JsonSerializer.Serialize(
            imageIds.Select( id => new
            {
                imageId = id,
                role = string.Equals( id, "cover", StringComparison.OrdinalIgnoreCase )
                    ? "main cover"
                    : "additional product image",
            } ) );

        string systemPrompt = BuildOpenAiBookSeoSystemPrompt( stricter );
        string userPrompt =
            $"""
            ДАННЫЕ КАРТОЧКИ ТОВАРА:
            Полное название: {title}
            Основное название: {primaryTitle}
            Автор: {authorLine}
            Язык (поле): {languageLine}
            Жанры: {genresLine}
            Подсказка по языку SEO: {langHint}

            РАЗБОР КНИГИ (пиши SEO ТОЛЬКО по этому разбору; не выдумывай и не собирай обрывки слов):
            {analysis.RawJson}

            ИЗОБРАЖЕНИЯ (ровно один imageAlts на каждый imageId, сохранить id без изменений):
            {imagesJson}

            Meta Description: кратко О ЧЁМ книга (содержание по разбору) + доставка.
            Одно связное предложение. Цель — весь seoDescription до 160 символов с пробелами (с доставкой).
            НЕ список слов через запятую. НЕ «кніга, даступная ў краме».
            """;

        Dictionary<string, object?> payload = new()
        {
            ["model"] = model,
            // gpt-5.6-terra (and similar) reject non-default temperature — omit it.
            ["max_completion_tokens"] = 700,
            ["reasoning_effort"] = "low",
            ["response_format"] = new { type = "json_object" },
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            }
        };

        string body = await SendOpenAiChatAsync( apiKey, payload, cancellationToken );
        if (!TryGetMessageContent( body, out string? content ) || string.IsNullOrWhiteSpace( content ))
        {
            return (null, null, new List<BookImageAltDto>());
        }

        string json = ExtractJsonObject( content );
        using JsonDocument doc = JsonDocument.Parse( json );
        string? seoTitle = doc.RootElement.TryGetProperty( "seoTitle", out JsonElement titleEl )
            && titleEl.ValueKind == JsonValueKind.String
            ? titleEl.GetString()?.Trim()
            : null;
        string? seoDescription = doc.RootElement.TryGetProperty( "seoDescription", out JsonElement descEl )
            && descEl.ValueKind == JsonValueKind.String
            ? descEl.GetString()?.Trim()
            : null;

        List<BookImageAltDto> alts = new();
        if (doc.RootElement.TryGetProperty( "imageAlts", out JsonElement altsEl )
            && altsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in altsEl.EnumerateArray())
            {
                string imageId = item.TryGetProperty( "imageId", out JsonElement idEl )
                    ? (idEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                string alt = item.TryGetProperty( "alt", out JsonElement altEl )
                    ? (altEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace( imageId ) && !string.IsNullOrWhiteSpace( alt ))
                {
                    alts.Add( new BookImageAltDto { ImageId = imageId, Alt = alt } );
                }
            }
        }

        return (seoTitle, seoDescription, alts);
    }

    /// <summary>
    /// Step 1: paraphrase the book into structured meaning. No SEO copy, no source pasting.
    /// </summary>
    private async Task<BookSeoAnalysis?> AnalyzeBookForSeoWithOpenAiAsync(
        string apiKey,
        string model,
        string title,
        string primaryTitle,
        string authorLine,
        string languageLine,
        string genresLine,
        string langHint,
        string description,
        CancellationToken cancellationToken )
    {
        const string systemPrompt =
            """
            Ты литературный редактор. По данным карточки товара составь КРАТКИЙ РАЗБОР книги своими словами.

            Верни ТОЛЬКО JSON:
            {
              "about": "одно связное предложение на языке книги: о чём книга (без названия и без автора)",
              "centralSubject": "кто/что в центре",
              "genre": "жанр если явно известен, иначе null",
              "themes": ["2–5 тем как именные словосочетания"],
              "people": ["важные имена"],
              "places": ["важные места"],
              "periodsOrEvents": ["периоды или события"]
            }

            ПРАВИЛА:
            - Не копируй предложения и фрагменты исходного описания.
            - Не вытаскивай случайные глаголы/обрывки («гаварылася», «давялося», «перажыць»).
            - themes — осмысленные понятия: «беларусы Падляшша», «гістарычная памяць», «бежанства»; НЕ отдельные глаголы.
            - about — нормальное предложение: что это за книга и о чём. Пример: «Кніга пра беларусаў Падляшша, бежанства, перасяленні, злачынствы і гістарычную памяць.»
            - Не выдумывай факты, которых нет во входных данных.
            - Пиши about и themes на языке книги.
            """;

        string userPrompt =
            $"""
            Название: {title}
            Краткое название: {primaryTitle}
            Автор: {authorLine}
            Язык: {languageLine}
            Жанры: {genresLine}
            Язык ответа: {langHint}

            Описание товара (только чтобы понять содержание; НЕ копировать):
            {description}
            """;

        Dictionary<string, object?> payload = new()
        {
            ["model"] = model,
            // gpt-5.6-terra rejects custom temperature — omit (API default only).
            ["max_completion_tokens"] = 450,
            ["reasoning_effort"] = "low",
            ["response_format"] = new { type = "json_object" },
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            }
        };

        try
        {
            string body = await SendOpenAiChatAsync( apiKey, payload, cancellationToken );
            if (!TryGetMessageContent( body, out string? content ) || string.IsNullOrWhiteSpace( content ))
            {
                return null;
            }

            string json = ExtractJsonObject( content );
            using JsonDocument doc = JsonDocument.Parse( json );
            return new BookSeoAnalysis
            {
                About = GetJsonString( doc.RootElement, "about" ),
                CentralSubject = GetJsonString( doc.RootElement, "centralSubject" ),
                Genre = NullIfEmpty( GetJsonString( doc.RootElement, "genre" ) ),
                Themes = GetJsonStringArray( doc.RootElement, "themes" ),
                People = GetJsonStringArray( doc.RootElement, "people" ),
                Places = GetJsonStringArray( doc.RootElement, "places" ),
                PeriodsOrEvents = GetJsonStringArray( doc.RootElement, "periodsOrEvents" ),
                RawJson = json,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "OpenAI book analysis failed." );
            return null;
        }
    }

    private static string GetJsonString( JsonElement root, string name )
    {
        if (!root.TryGetProperty( name, out JsonElement el ) || el.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return (el.GetString() ?? string.Empty).Trim();
    }

    private static string? NullIfEmpty( string value )
        => string.IsNullOrWhiteSpace( value ) || value is "null" or "None" or "none"
            ? null
            : value.Trim();

    private static List<string> GetJsonStringArray( JsonElement root, string name )
    {
        List<string> result = new();
        if (!root.TryGetProperty( name, out JsonElement el ) || el.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? s = item.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace( s ) && s is not ("null" or "None"))
            {
                result.Add( s );
            }
        }

        return result;
    }

    private static string BuildOpenAiBookSeoSystemPrompt( bool stricter )
    {
        const string core =
            """
            Ты — SEO-редактор интернет-магазина белорусских книг Kirma.sh.

            На вход ты получаешь данные карточки товара и готовый РАЗБОР книги (about, themes, places…).
            Исходного длинного описания товара у тебя нет — пиши только по разбору.

            Твоя задача — НЕ собирать ключевые слова и НЕ склеивать обрывки. Ты должен по разбору понять, о чём книга, и написать новый, естественный SEO-текст связными предложениями.

            Ты создаёшь:
            1. SEO Title
            2. Meta Description
            3. ALT-тексты изображений

            ================================
            ГЛАВНЫЙ ПРИНЦИП
            ================================

            Разбор книги — это ИСТОЧНИК СМЫСЛА.

            Это НЕ список, который нужно:
            - сокращать;
            - копировать через запятую;
            - превращать в набор ключевых слов.

            Сначала прочитай разбор как редактор и пойми содержание книги.

            Определи:
            - о чём эта книга;
            - кто или что находится в центре книги;
            - жанр или тип книги, если он известен;
            - главные темы;
            - важные имена, места, события и исторические периоды;
            - какие 2–5 понятий лучше всего характеризуют именно эту книгу для потенциального читателя.

            После этого НАПИШИ НОВЫЙ ТЕКСТ своими словами — как нормальные предложения.

            ВАЖНО:
            ключевые понятия нужны тебе для понимания содержания, а не для механического перечисления через запятую.

            Результат должен звучать как текст, написанный профессиональным редактором для человека, а не как список SEO-ключей.

            ================================
            ЯЗЫК
            ================================

            Определи язык книги по данным товара.

            SEO Title, Meta Description и ALT должны быть написаны НА ЯЗЫКЕ КНИГИ.

            Белорусская книга → по-белорусски.
            Русская → по-русски.
            Польская → по-польски.
            Английская → по-английски.

            Название книги сохраняй в оригинальном написании.

            Текст должен быть грамматически правильным и естественным для выбранного языка.

            Склоняй имя и фамилию автора, если этого требует грамматика.

            Например:

            ПРАВИЛЬНО:
            «Камяні мусілі паляцець» Анэты Прымака-Онішк

            НЕПРАВИЛЬНО:
            «Камяні мусілі паляцець» Анэта Прымака-Онішк

            ================================
            SEO TITLE
            ================================

            Создай естественный SEO Title, который помогает понять, что находится на странице.

            Предпочтительная структура:

            «Название» — Автор | дополнительный контекст | Kirma.sh

            В конце ВСЕГДА должно быть:

            | Kirma.sh

            Дополнительный контекст добавляй только тогда, когда он действительно полезен:
            - па-беларуску;
            - дзіцячая кніга;
            - гістарычны раман;
            - кніга пра Беларусь;
            - вершы;
            - мемуары;
            и т. п.

            Не придумывай жанр или характеристики, которых нет в исходных данных.

            Не превращай Title в набор ключевых слов.

            Ориентир по длине — до 60–70 символов, но естественность и информативность важнее попытки заполнить лимит.

            ================================
            META DESCRIPTION
            ================================

            Meta Description — это НОВАЯ краткая аннотация книги для человека, который увидел страницу в Google.

            Главный вопрос, на который должен отвечать текст:

            «О ЧЁМ ЭТА КНИГА?»

            Человек должен понять содержание книги, даже если он никогда раньше о ней не слышал.

            Предпочтительная логика:

            «Название» + автор + естественное объяснение содержания книги + доставка по миру.

            НЕ используй шаблон механически. Предложение должно звучать естественно.

            Обычно достаточно:
            - главной темы;
            - 2–4 важных конкретных аспектов;
            - места/эпохи/персонажа, если это действительно существенно.

            ================================
            КРИТИЧЕСКИ ВАЖНО
            ================================

            НЕ копируй начало исходного описания.

            НЕ сокращай исходное описание путём удаления слов.

            НЕ составляй текст из отдельных слов и фрагментов исходного описания.

            НЕ перечисляй слова только потому, что они встретились в исходнике.

            НЕ пиши бессвязные цепочки ключевых слов.

            НЕ используй формулировки вроде:

            «кніга пра паўночна-ўсходняй Польшчы, гаварылася, давялося, перажыць»

            Это не предложение и не описание книги.

            Каждое предложение должно иметь нормальную грамматическую структуру и законченный смысл.

            НЕ используй многоточие.

            НЕ обрывай предложения.

            НЕ обрезай готовый текст до лимита символов.

            Если получилось слишком длинно — ПЕРЕПИШИ короче.

            ================================
            КАК НУЖНО МЫСЛИТЬ
            ================================

            Например, исходное описание рассказывает:

            - о беларуска-православной общине Подляшья;
            - о беженстве;
            - о послевоенных переселениях;
            - о преступлениях;
            - об исторической памяти;
            - автор работает с интервью и архивами.

            Не нужно копировать эти пункты подряд.

            Сначала сформулируй главный смысл:

            Это документальная книга о пережитом белорусами Подляшья и о памяти об этих событиях.

            И только после этого напиши Meta Description.

            ХОРОШИЙ РЕЗУЛЬТАТ:

            «Камяні мусілі паляцець» Анэты Прымака-Онішк — кніга пра беларусаў Падляшша, бежанства, перасяленні, злачынствы і гістарычную памяць. Дастаўка па свеце.

            ПОЧЕМУ ЭТО ХОРОШО:

            - сразу понятно, о чём книга;
            - названа конкретная общность — беларусы Падляшша;
            - названы реальные темы книги;
            - текст читается как нормальное предложение;
            - нет бессмысленного SEO-спама;
            - исходная аннотация не скопирована;
            - есть полезные поисковые понятия;
            - есть информация о доставке.

            ПЛОХО:

            «Камяні мусілі паляцець» Анэта Прымака-Онішк — кніга пра паўночна-ўсходняй Польшчы, гаварылася, давялося, перажыць. Дастаўка па свеце.

            ПОЧЕМУ ЭТО ПЛОХО:

            - нарушена грамматика;
            - непонятно, о чём книга;
            - слова механически вытащены из исходного текста;
            - потеряны беларусы Падляшша;
            - потеряны основные темы;
            - это выглядит как набор ключевых слов.

            ================================
            ДЛИНА META DESCRIPTION
            ================================

            Цель: весь Meta Description (с доставкой) — до 160 символов с пробелами.

            Это ориентир, а не повод резать текст многоточием.

            Хороший законченный description на 130–160 символов лучше длинного оборванного.

            Никогда не добавляй бессмысленные слова ради достижения лимита.

            Никогда не обрезай предложение многоточием ради лимита.

            Если текст длиннее 160 — заново сформулируй мысль короче (без «…»).

            ================================
            ДОСТАВКА
            ================================

            В конце Meta Description добавляй информацию о доставке по миру НА ЯЗЫКЕ КНИГИ.

            Белорусский:
            Дастаўка па свеце.

            Русский:
            Доставка по миру.

            Польский:
            Wysyłka na świat.

            Английский:
            Worldwide shipping.

            Это дополнительная информация.

            Основная часть Meta Description должна объяснять содержание книги.

            ================================
            ALT-ТЕКСТЫ
            ================================

            Для каждого изображения создай отдельный ALT.

            ALT должен в первую очередь точно описывать изображение.

            Для основной обложки используй естественную структуру:

            Название + автор + что изображено.

            Например:

            «Віно з дзьмухаўцоў» Рэя Брэдберы — вокладка кнігі

            «Эвридика, проверь, выключила ли ты газ» Татьяны Замировской — обложка книги

            Если на изображении показан разворот, задняя обложка или иллюстрации, опиши именно это:

            Разварот кнігі «Краіна Беларусь» з ілюстрацыямі

            Задняя вокладка кнігі «Silva Rerum»

            Не используй одинаковый ALT для разных изображений, если видно, что на них разное содержание.

            Не добавляй в ALT:
            - Kirma.sh;
            - доставку;
            - цену;
            - купить;
            - заказать;
            - бессмысленные SEO-ключи.

            ALT должен быть коротким, естественным и описательным.

            Не придумывай визуальные детали, которых ты не видишь или которых нет во входных данных.

            ================================
            ЗАПРЕТ НА ВЫДУМЫВАНИЕ
            ================================

            Используй только факты из входных данных.

            Не придумывай:
            - автора;
            - сюжет;
            - жанр;
            - язык;
            - переводчика;
            - издательство;
            - возраст читателя;
            - награды;
            - исторические события;
            - характеристики издания.

            Можно делать вывод о главной теме книги из предоставленного описания, но нельзя добавлять новые факты.

            ================================
            ФИНАЛЬНАЯ ПРОВЕРКА
            ================================

            Перед ответом молча проверь Meta Description:

            1. Понятно ли человеку, О ЧЁМ книга?
            2. Это новый текст, а не обрезанный исходный description?
            3. Это связное предложение, а не набор ключевых слов?
            4. Выбраны ли самые важные темы книги?
            5. Нет ли бессмысленных слов только ради SEO?
            6. Правильна ли грамматика?
            7. Правильно ли склонено имя автора?
            8. Нет ли многоточия или оборванного предложения?
            9. Есть ли доставка по миру?
            10. Использован ли язык книги?

            Если хотя бы один пункт не выполнен — перепиши текст.

            Перед ответом молча проверь SEO Title:

            1. Есть название книги?
            2. Автор указан, если известен и помещается естественно?
            3. Нет ли выдуманных характеристик?
            4. Заканчивается ли Title на "| Kirma.sh"?
            5. Использован ли язык книги?

            Перед ответом молча проверь ALT-тексты:

            1. Каждый ALT описывает изображение?
            2. Нет SEO-спама?
            3. Нет выдуманных визуальных деталей?
            4. Использован язык книги?

            Верни только валидный JSON без Markdown, комментариев и объяснений:
            {"seoTitle":"...","seoDescription":"...","imageAlts":[{"imageId":"INPUT_IMAGE_ID","alt":"..."}]}
            Ровно один imageAlts на каждый входной imageId; сохраняй id без изменений.
            """;

        if (!stricter)
        {
            return core;
        }

        return core
            + """


            ПОВТОРНАЯ ПОПЫТКА: предыдущий ответ не прошёл проверку — часто из‑за копирования/обрезки исходного описания или набора ключевых слов.
            Полностью забудь структуру исходного description.
            Напиши НОВЫЙ связный Meta Description своими словами: о чём книга + 2–4 важные темы + доставка.
            Не копируй начало исходника. Не собирай обрывки слов. Не используй многоточие.
            SEO Title должен заканчиваться на | Kirma.sh.
            """;
    }

    private static IReadOnlyList<string> BuildSeoImageIds( IEnumerable<string>? raw )
    {
        List<string> ids = (raw ?? Array.Empty<string>())
            .Select( id => (id ?? string.Empty).Trim() )
            .Where( id => !string.IsNullOrWhiteSpace( id ) )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .ToList();
        if (ids.Count == 0)
        {
            ids.Add( "cover" );
        }
        else if (!ids.Any( id => string.Equals( id, "cover", StringComparison.OrdinalIgnoreCase )))
        {
            ids.Insert( 0, "cover" );
        }

        return ids;
    }

    private static List<BookImageAltDto> NormalizeImageAlts(
        IReadOnlyList<string> imageIds,
        IReadOnlyList<BookImageAltDto> generated,
        string primaryTitle,
        string fullTitle,
        string? author,
        string seoLang )
    {
        Dictionary<string, string> byId = new( StringComparer.OrdinalIgnoreCase );
        foreach (BookImageAltDto item in generated)
        {
            if (!string.IsNullOrWhiteSpace( item.ImageId ) && !string.IsNullOrWhiteSpace( item.Alt ))
            {
                byId[item.ImageId.Trim()] = TruncateAtWordBoundary( item.Alt.Trim(), 125 );
            }
        }

        string quoted = FormatQuotedTitle( primaryTitle, fullTitle );
        string authorPart = string.IsNullOrWhiteSpace( author ) ? string.Empty : " " + author.Trim();
        string coverWord = seoLang switch
        {
            "ru" => "обложка книги",
            "pl" => "okładka książki",
            "en" => "book cover",
            _ => "вокладка кнігі",
        };
        string extraWord = seoLang switch
        {
            "ru" => $"Дополнительное фото книги {quoted}",
            "pl" => $"Dodatkowe zdjęcie książki {quoted}",
            "en" => $"Additional photo of the book {quoted}",
            _ => $"Дадатковае фота кнігі {quoted}",
        };

        List<BookImageAltDto> result = new();
        foreach (string id in imageIds)
        {
            if (byId.TryGetValue( id, out string? alt ) && !string.IsNullOrWhiteSpace( alt ))
            {
                result.Add( new BookImageAltDto { ImageId = id, Alt = alt } );
                continue;
            }

            bool isCover = string.Equals( id, "cover", StringComparison.OrdinalIgnoreCase );
            string fallback = isCover
                ? $"{quoted.Trim( '«', '»' )}{authorPart} — {coverWord}".Trim()
                : extraWord;
            result.Add( new BookImageAltDto
            {
                ImageId = id,
                Alt = TruncateAtWordBoundary( fallback, 125 ),
            } );
        }

        return result;
    }

    private static bool IsAcceptableSeoTitle( string? seoTitle )
    {
        string text = (seoTitle ?? string.Empty).Trim();
        if (text.Length < 12)
        {
            return false;
        }

        if (!text.EndsWith( "| Kirma.sh", StringComparison.OrdinalIgnoreCase ))
        {
            return false;
        }

        return text.Length <= 90;
    }

    private static string TruncateSeoTitle( string seoTitle )
    {
        string text = EnsureSeoTitleSuffix( seoTitle );
        const string suffix = " | Kirma.sh";
        if (text.Length <= 70)
        {
            return text;
        }

        string without = text;
        if (without.EndsWith( suffix, StringComparison.OrdinalIgnoreCase ))
        {
            without = without[..^suffix.Length].TrimEnd( ' ', '|' );
        }

        string clipped = TruncateAtWordBoundary( without, 70 - suffix.Length ).TrimEnd( '…', ' ', '|' );
        return EnsureSeoTitleSuffix( clipped );
    }


    private static string FormatQuotedTitle( string primaryTitle, string fullTitle )
    {
        string shortTitle = string.IsNullOrWhiteSpace( primaryTitle )
            ? (fullTitle ?? string.Empty).Trim()
            : primaryTitle.Trim();
        if (string.IsNullOrWhiteSpace( shortTitle ))
        {
            shortTitle = "Кніга";
        }

        return shortTitle.StartsWith( '«' ) ? shortTitle : $"«{shortTitle}»";
    }

    private static bool IsAcceptableMetaDescription( string? seoDescription, string? descriptionPlain )
    {
        if (string.IsNullOrWhiteSpace( seoDescription ))
        {
            return false;
        }

        if (seoDescription.Contains( '…' ) || seoDescription.Contains( "..." ))
        {
            return false;
        }

        if (IsWeakMetaDescription( seoDescription ))
        {
            return false;
        }

        if (LooksLikeKeywordSpamMeta( seoDescription ))
        {
            return false;
        }

        if (LooksLikeRawDescriptionPaste( seoDescription, descriptionPlain ))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Rejects empty/short/generic shop-filler forms and unfinished blurbs.
    /// </summary>
    private static bool IsWeakMetaDescription( string? seoDescription )
    {
        string text = StripDeliveryAndShopSuffix( seoDescription );
        if (text.Length < 70)
        {
            return true;
        }

        string folded = text.ToLowerInvariant();
        if (folded.Contains( "даступная ў краме", StringComparison.Ordinal )
            || folded.Contains( "доступная в магазине", StringComparison.Ordinal )
            || folded.Contains( "з асартыменту крамы", StringComparison.Ordinal )
            || folded.Contains( "в ассортименте магазина", StringComparison.Ordinal )
            || folded.Contains( "available from the", StringComparison.Ordinal )
            || folded.Contains( "książka dostępna", StringComparison.Ordinal ))
        {
            return true;
        }

        int dash = text.IndexOf( " — ", StringComparison.Ordinal );
        string after = dash >= 0 && dash < 100
            ? text[(dash + 3)..].Trim().TrimEnd( '.' )
            : text;
        string afterFolded = after.ToLowerInvariant();
        if (afterFolded is "кніга" or "книга" or "раман" or "роман" or "кніга." or "книга.")
        {
            return true;
        }

        if (Regex.IsMatch( afterFolded, @"^(кніга|книга|раман|роман)\.?$" ))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// True for mechanical keyword chains like «…гаварылася, давялося, перажыць».
    /// Allows a natural theme list with «і/и/and» (e.g. бежанства, перасяленні і памяць).
    /// </summary>
    private static bool LooksLikeKeywordSpamMeta( string? seoDescription )
    {
        string text = StripDeliveryAndShopSuffix( seoDescription );
        int dash = text.IndexOf( " — ", StringComparison.Ordinal );
        string after = dash >= 0 && dash < 120
            ? text[(dash + 3)..].Trim()
            : text;

        // Bare verb stubs ripped from Belarusian/Russian blurbs — never belong in meta.
        if (Regex.IsMatch(
                after,
                @"\b(гаварылася|давялося|перажыць|выключаецца|парушае|рассказывает|пришлось)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant ))
        {
            return true;
        }

        string[] parts = after.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        if (parts.Length >= 4)
        {
            int singleWord = parts.Count( p =>
            {
                string[] words = Regex.Split( p.Trim(), @"\s+" ).Where( w => w.Length > 0 ).ToArray();
                return words.Length == 1;
            } );
            // 3+ lone words between commas = keyword dump (not «A, B, C і D»).
            if (singleWord >= 3)
            {
                return true;
            }
        }

        // Broken «кніга пра …яй, …» style fragments.
        if (Regex.IsMatch(
                after,
                @"\b(кніга пра|книга про)\s+[\p{L}\-]+яй\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant ))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Build meta from OpenAI analysis (about / themes) — content about the book, not a shop filler.
    /// </summary>
    private static string? BuildMetaDescriptionFromAnalysis(
        string primaryTitle,
        string fullTitle,
        string? author,
        BookSeoAnalysis? analysis,
        string seoLang )
    {
        if (analysis is null)
        {
            return null;
        }

        string quoted = FormatQuotedTitle( primaryTitle, fullTitle );
        string authorTrimmed = (author ?? string.Empty).Trim();
        string head = string.IsNullOrWhiteSpace( authorTrimmed )
            ? quoted
            : $"{quoted} {authorTrimmed}";

        string blurb = (analysis.About ?? string.Empty).Trim();
        blurb = StripDeliveryAndShopSuffix( blurb ).Trim().TrimEnd( '.', '!', '?' );

        // Avoid nesting title/author if the model already put them in about.
        if (blurb.Contains( primaryTitle, StringComparison.OrdinalIgnoreCase )
            || blurb.StartsWith( '«' )
            || (!string.IsNullOrWhiteSpace( authorTrimmed )
                && blurb.Contains( authorTrimmed, StringComparison.OrdinalIgnoreCase )))
        {
            // Keep as a full candidate; Finalize will add delivery and fit length.
            return string.IsNullOrWhiteSpace( blurb ) ? null : blurb + ".";
        }

        if (string.IsNullOrWhiteSpace( blurb ))
        {
            blurb = BuildAboutFromThemes( analysis, seoLang );
        }

        if (string.IsNullOrWhiteSpace( blurb ))
        {
            string subject = (analysis.CentralSubject ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace( subject ))
            {
                blurb = seoLang switch
                {
                    "ru" => $"книга о {subject}",
                    "pl" => $"książka o {subject}",
                    "en" => $"a book about {subject}",
                    _ => $"кніга пра {subject}",
                };
            }
        }

        if (string.IsNullOrWhiteSpace( blurb ))
        {
            return null;
        }

        // Ensure blurb reads as a clause after the em dash.
        if (char.IsUpper( blurb[0] ) && !blurb.StartsWith( "Кніга", StringComparison.Ordinal )
            && !blurb.StartsWith( "Книга", StringComparison.Ordinal )
            && !blurb.StartsWith( "A book", StringComparison.OrdinalIgnoreCase )
            && !blurb.StartsWith( "Książka", StringComparison.OrdinalIgnoreCase ))
        {
            blurb = char.ToLowerInvariant( blurb[0] ) + blurb[1..];
        }

        return $"{head} — {blurb}.";
    }

    private static string BuildAboutFromThemes( BookSeoAnalysis analysis, string seoLang )
    {
        List<string> themes = analysis.Themes
            .Concat( analysis.Places )
            .Concat( analysis.PeriodsOrEvents )
            .Select( t => (t ?? string.Empty).Trim() )
            .Where( t => t.Length is >= 3 and <= 40 )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .Take( 4 )
            .ToList();
        if (themes.Count == 0)
        {
            return string.Empty;
        }

        string joined = JoinNaturalList( themes, seoLang );
        return seoLang switch
        {
            "ru" => $"книга о {joined}",
            "pl" => $"książka o {joined}",
            "en" => $"a book about {joined}",
            _ => $"кніга пра {joined}",
        };
    }

    private static string JoinNaturalList( IReadOnlyList<string> items, string seoLang )
    {
        if (items.Count == 1)
        {
            return items[0];
        }

        string andWord = seoLang switch
        {
            "ru" => "и",
            "pl" => "i",
            "en" => "and",
            _ => "і",
        };

        if (items.Count == 2)
        {
            return $"{items[0]} {andWord} {items[1]}";
        }

        return string.Join( ", ", items.Take( items.Count - 1 ) ) + $" {andWord} {items[^1]}";
    }

    /// <summary>
    /// Last-resort local blurb: title + author + genre — natural sentence, never keyword dump.
    /// </summary>
    private static string BuildExpandedFallbackMetaDescription(
        string primaryTitle,
        string fullTitle,
        string? author,
        IReadOnlyList<string>? genres,
        string seoLang )
    {
        string quoted = FormatQuotedTitle( primaryTitle, fullTitle );
        string authorTrimmed = (author ?? string.Empty).Trim();
        string head = string.IsNullOrWhiteSpace( authorTrimmed )
            ? quoted
            : $"{quoted} {authorTrimmed}";

        string? genre = genres?
            .Select( g => (g ?? string.Empty).Trim() )
            .FirstOrDefault( g => g.Length is >= 3 and <= 40 );

        string body = seoLang switch
        {
            "ru" => string.IsNullOrWhiteSpace( genre )
                ? $"{head} — книга из ассортимента белорусских изданий."
                : $"{head} — {genre}.",
            "pl" => string.IsNullOrWhiteSpace( genre )
                ? $"{head} — książka z oferty księgarni."
                : $"{head} — {genre}.",
            "en" => string.IsNullOrWhiteSpace( genre )
                ? $"{head} — a book from the store assortment."
                : $"{head} — {genre}.",
            _ => string.IsNullOrWhiteSpace( genre )
                ? $"{head} — кніга з асартыменту беларускіх выданняў."
                : $"{head} — {genre}.",
        };

        return Regex.Replace( body, @"\s+", " " ).Trim();
    }

    /// <summary>
    /// Normalize meta description, fit ≈160 chars with delivery, no ellipsis cuts.
    /// </summary>
    private static string FinalizeMetaDescription( string? value, string? seoLang = null )
    {
        string lang = string.IsNullOrWhiteSpace( seoLang ) ? "be" : seoLang.Trim();
        string text = StripDeliveryAndShopSuffix( value );
        text = Regex.Replace( text, @"\s+", " " ).Trim();
        text = text.TrimEnd( '…', ' ', ',' );
        if (string.IsNullOrWhiteSpace( text ))
        {
            text = lang == "ru" ? "Книга" : "Кніга";
        }

        if (!text.EndsWith( '.' ) && !text.EndsWith( '!' ) && !text.EndsWith( '?' ))
        {
            text += ".";
        }

        string delivery = lang switch
        {
            "ru" => "Доставка по миру.",
            "pl" => "Wysyłka na świat.",
            "en" => "Worldwide shipping.",
            _ => "Дастаўка па свеце.",
        };

        const int maxTotal = 160;
        int room = maxTotal - delivery.Length - 1; // space before delivery
        if (room < 60)
        {
            room = 60;
        }

        if (text.Length > room)
        {
            text = FitMetaBodyWithoutEllipsis( text, room );
        }

        return AppendDeliverySuffix( text.TrimEnd(), lang );
    }

    /// <summary>
    /// Shorten to maxChars at a word/clause boundary without «…».
    /// </summary>
    private static string FitMetaBodyWithoutEllipsis( string text, int maxChars )
    {
        string trimmed = text.Trim();
        if (trimmed.Length <= maxChars)
        {
            return trimmed.TrimEnd();
        }

        string window = trimmed[..maxChars];
        int lastStop = Math.Max(
            window.LastIndexOf( ". " ),
            Math.Max( window.LastIndexOf( "! " ), window.LastIndexOf( "? " ) ) );
        if (lastStop >= 50)
        {
            return window[..(lastStop + 1)].Trim();
        }

        int cut = window.LastIndexOf( ' ' );
        if (cut < 40)
        {
            cut = maxChars;
        }

        string body = window[..cut].TrimEnd( ',', ';', ':', ' ', '-', '—' );
        return body.EndsWith( '.' ) || body.EndsWith( '!' ) || body.EndsWith( '?' )
            ? body
            : body + ".";
    }

    private static string StripDeliveryAndShopSuffix( string? value )
    {
        string text = (value ?? string.Empty).Trim().Trim( '"', '“', '”' );
        text = Regex.Replace(
            text,
            @"\s*(Дастаўка па ўсім свеце|Дастаўка па свеце|Доставка по всему миру|Доставка па свеце|Доставка по миру|Wysyłka na cały świat|Wysyłka na świat|Worldwide shipping)\.?\s*$",
            "",
            RegexOptions.IgnoreCase );
        text = Regex.Replace(
            text,
            @"\s*(Купить в Kirma\.sh|Заказать онлайн|Купіць у Kirma\.sh)\.?\s*$",
            "",
            RegexOptions.IgnoreCase );
        text = Regex.Replace(
            text,
            @"\s*\|\s*Kirma\.sh\s*$",
            "",
            RegexOptions.IgnoreCase );
        return text.Trim().TrimEnd( '|', ' ' );
    }

    private static string AppendDeliverySuffix( string body, string seoLang )
    {
        string delivery = seoLang switch
        {
            "ru" => "Доставка по миру.",
            "pl" => "Wysyłka na świat.",
            "en" => "Worldwide shipping.",
            _ => "Дастаўка па свеце.",
        };

        string trimmed = (body ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( trimmed ))
        {
            return delivery;
        }

        if (trimmed.EndsWith( '.' ) || trimmed.EndsWith( '!' ) || trimmed.EndsWith( '?' ) || trimmed.EndsWith( '…' ))
        {
            return trimmed + " " + delivery;
        }

        return trimmed + ". " + delivery;
    }

    /// <summary>
    /// "be" or "ru". Critical: «беларуская» must NOT match as Russian (contains substring «рус»).
    /// </summary>
    private static string ResolveSeoLanguage(
        string? language,
        string? descriptionPlain,
        string? sampleText )
    {
        if (IsExplicitBelarusianLanguage( language ))
        {
            return "be";
        }

        if (IsExplicitRussianLanguage( language ))
        {
            return "ru";
        }

        string folded = FoldLangLabel( language );
        if (folded is "pl" or "polish" || folded.Contains( "поль", StringComparison.Ordinal ))
        {
            return "pl";
        }

        if (folded is "en" or "eng" or "english" || folded.Contains( "англі", StringComparison.Ordinal )
            || folded.Contains( "англ", StringComparison.Ordinal ))
        {
            return "en";
        }

        string sample = string.Join(
            ' ',
            new[] { sampleText, descriptionPlain, language }.Where( s => !string.IsNullOrWhiteSpace( s ) ) );
        if (LooksBelarusianText( sample ))
        {
            return "be";
        }

        if (LooksMostlyRussianText( sample ))
        {
            return "ru";
        }

        return "be";
    }

    private static bool IsExplicitBelarusianLanguage( string? language )
    {
        string folded = FoldLangLabel( language );
        if (string.IsNullOrWhiteSpace( folded ))
        {
            return false;
        }

        return folded is "be" or "bel" or "belarusian"
            || folded.Contains( "беларус", StringComparison.Ordinal )
            || folded.Contains( "белорус", StringComparison.Ordinal )
            || folded.Contains( "belarus", StringComparison.Ordinal );
    }

    private static bool IsExplicitRussianLanguage( string? language )
    {
        string folded = FoldLangLabel( language );
        if (string.IsNullOrWhiteSpace( folded ))
        {
            return false;
        }

        if (IsExplicitBelarusianLanguage( language ))
        {
            return false;
        }

        return folded is "ru" or "rus" or "russian" or "русский" or "русская" or "русском" or "русски"
            || folded.StartsWith( "русск", StringComparison.Ordinal );
    }

    private static string FoldLangLabel( string? language ) =>
        (language ?? string.Empty).Trim().ToLowerInvariant();

    private static bool LooksBelarusianText( string? text )
    {
        if (string.IsNullOrWhiteSpace( text ))
        {
            return false;
        }

        return text.IndexOf( 'ў' ) >= 0
            || text.IndexOf( 'Ў' ) >= 0
            || text.Contains( "па-беларуску", StringComparison.OrdinalIgnoreCase )
            || text.Contains( "беларуск", StringComparison.OrdinalIgnoreCase );
    }

    private static bool LooksMostlyRussianText( string? text )
    {
        if (string.IsNullOrWhiteSpace( text ) || LooksBelarusianText( text ))
        {
            return false;
        }

        return text.Contains( " это ", StringComparison.OrdinalIgnoreCase )
            || text.Contains( "книга", StringComparison.OrdinalIgnoreCase )
            || text.Contains( "роман", StringComparison.OrdinalIgnoreCase )
            || text.Contains( "эссе", StringComparison.OrdinalIgnoreCase );
    }

    private static string TruncateAtWordBoundary( string text, int maxChars )
    {
        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        int cut = trimmed.LastIndexOf( ' ', maxChars );
        if (cut < 40)
        {
            cut = maxChars;
        }

        return trimmed[..cut].TrimEnd( ',', ';', ':', ' ', '-' ) + "…";
    }

    /// <summary>
    /// True when the blurb after the dash copies the source (opener, first sentence, or long consecutive phrase).
    /// Isolated keyword reuse (Падляшша, бежанства) is allowed and must NOT trigger this.
    /// </summary>
    private static bool LooksLikeRawDescriptionPaste( string? seoDescription, string? descriptionPlain )
    {
        string seo = Regex.Replace( (seoDescription ?? string.Empty).Trim(), @"\s+", " " );
        string plain = Regex.Replace( (descriptionPlain ?? string.Empty).Trim(), @"\s+", " " );
        if (string.IsNullOrWhiteSpace( seo ) || plain.Length < 40)
        {
            return false;
        }

        string seoCore = seo;
        int dash = seo.IndexOf( " — ", StringComparison.Ordinal );
        if (dash < 0)
        {
            dash = seo.IndexOf( " - ", StringComparison.Ordinal );
        }

        if (dash >= 0 && dash < 100)
        {
            seoCore = seo[(dash + 3)..].Trim();
        }

        seoCore = Regex.Replace(
            seoCore,
            @"^(кніга пра\s+|книга про\s+|кніга\s+|книга\s+)",
            "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );
        seoCore = StripDeliveryAndShopSuffix( seoCore );
        if (seoCore.Length < 36)
        {
            return false;
        }

        // Compare against the opening of the source description only.
        string plainOpen = plain.Length > 80 ? plain[..80] : plain;
        string seoOpen = seoCore.Length > 48 ? seoCore[..48] : seoCore;
        if (plainOpen.StartsWith( seoOpen, StringComparison.OrdinalIgnoreCase )
            || seoOpen.StartsWith( plainOpen[..Math.Min( 36, plainOpen.Length )], StringComparison.OrdinalIgnoreCase ))
        {
            return true;
        }

        // Also catch near-copy of the first sentence.
        string firstSentence = Regex.Split( plain, @"(?<=[\.\!\?…])\s+" ).FirstOrDefault() ?? plain;
        firstSentence = firstSentence.Trim();
        if (firstSentence.Length >= 40)
        {
            string needle = firstSentence[..Math.Min( 40, firstSentence.Length )];
            if (seoCore.Contains( needle, StringComparison.OrdinalIgnoreCase ))
            {
                return true;
            }
        }

        // Catch consecutive multi-word phrases copied from anywhere in the source opener (~220 chars).
        if (HasCopiedConsecutivePhrase( seoCore, plain.Length > 220 ? plain[..220] : plain, minWords: 6 ))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// True if seoCore contains a run of <paramref name="minWords"/>+ consecutive source words.
    /// </summary>
    private static bool HasCopiedConsecutivePhrase( string seoCore, string sourceWindow, int minWords )
    {
        string[] sourceWords = Regex
            .Split( sourceWindow.ToLowerInvariant(), @"[^\p{L}\p{N}]+" )
            .Where( w => w.Length >= 2 )
            .ToArray();
        string[] seoWords = Regex
            .Split( seoCore.ToLowerInvariant(), @"[^\p{L}\p{N}]+" )
            .Where( w => w.Length >= 2 )
            .ToArray();
        if (sourceWords.Length < minWords || seoWords.Length < minWords)
        {
            return false;
        }

        HashSet<string> seoNgrams = new( StringComparer.Ordinal );
        for (int i = 0; i <= seoWords.Length - minWords; i++)
        {
            seoNgrams.Add( string.Join( ' ', seoWords.Skip( i ).Take( minWords ) ) );
        }

        for (int i = 0; i <= sourceWords.Length - minWords; i++)
        {
            string ngram = string.Join( ' ', sourceWords.Skip( i ).Take( minWords ) );
            if (seoNgrams.Contains( ngram ))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Main title before subtitle (period / colon / em dash).
    /// </summary>
    internal static string ExtractPrimaryBookTitle( string? raw )
    {
        string title = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( title ))
        {
            return string.Empty;
        }

        // Strip wrapping guillemets for splitting, re-apply later if needed.
        string working = title;
        foreach (string sep in new[] { ". ", ".\n", ": ", " — ", " – ", " - " })
        {
            int idx = working.IndexOf( sep, StringComparison.Ordinal );
            if (idx > 8)
            {
                working = working[..idx].Trim();
                break;
            }
        }

        return working.Trim().Trim( '«', '»', '"' );
    }

    /// <summary>
    /// Cap handle length: prefer primary-title slug, max 6 segments / 48 chars.
    /// </summary>
    internal static string ShortenBookHandle( string? slug, string? primaryTitleFallback )
    {
        string candidate = (slug ?? string.Empty).Trim().Trim( '-' );
        if (string.IsNullOrWhiteSpace( candidate ))
        {
            candidate = SlugifyLatin( primaryTitleFallback );
        }

        if (string.IsNullOrWhiteSpace( candidate ))
        {
            return "book";
        }

        string[] parts = candidate.Split( '-', StringSplitOptions.RemoveEmptyEntries );
        const int maxParts = 6;
        const int maxChars = 48;
        if (parts.Length > maxParts)
        {
            parts = parts.Take( maxParts ).ToArray();
        }

        string joined = string.Join( '-', parts );
        while (joined.Length > maxChars && parts.Length > 2)
        {
            parts = parts.Take( parts.Length - 1 ).ToArray();
            joined = string.Join( '-', parts );
        }

        if (joined.Length > maxChars)
        {
            joined = joined[..maxChars].Trim( '-' );
        }

        return string.IsNullOrWhiteSpace( joined ) ? "book" : joined;
    }

    private static string EnsureSeoTitleSuffix( string seoTitle )
    {
        string trimmed = seoTitle.Trim();
        if (trimmed.EndsWith( "| Kirma.sh", StringComparison.OrdinalIgnoreCase ))
        {
            return trimmed;
        }

        return trimmed + " | Kirma.sh";
    }


    private static string? NullIfWhiteSpace( string? value )
    {
        string trimmed = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace( trimmed ) ? null : trimmed;
    }

    /// <summary>
    /// Bel/Rus → Latin slug: lowercase, hyphens, no spaces.
    /// </summary>
    internal static string SlugifyLatin( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return string.Empty;
        }

        StringBuilder sb = new( raw.Length * 2 );
        foreach (char ch in raw.Trim().ToLowerInvariant())
        {
            if (CyrillicToLatin.TryGetValue( ch, out string? mapped ))
            {
                if (mapped.Length > 0)
                {
                    sb.Append( mapped );
                }

                continue;
            }

            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append( ch );
                continue;
            }

            if (ch is ' ' or '_' or '-' or '.' or '/' or '\\')
            {
                sb.Append( '-' );
            }
        }

        string slug = Regex.Replace( sb.ToString(), "-{2,}", "-" ).Trim( '-' );
        return slug;
    }

    private static readonly Dictionary<char, string> CyrillicToLatin = new()
    {
        ['а'] = "a",
        ['б'] = "b",
        ['в'] = "v",
        ['г'] = "h",
        ['ґ'] = "g",
        ['д'] = "d",
        ['е'] = "e",
        ['ё'] = "yo",
        ['ж'] = "zh",
        ['з'] = "z",
        ['і'] = "i",
        ['й'] = "y",
        ['к'] = "k",
        ['л'] = "l",
        ['м'] = "m",
        ['н'] = "n",
        ['о'] = "o",
        ['п'] = "p",
        ['р'] = "r",
        ['с'] = "s",
        ['т'] = "t",
        ['у'] = "u",
        ['ў'] = "u",
        ['ф'] = "f",
        ['х'] = "kh",
        ['ц'] = "ts",
        ['ч'] = "ch",
        ['ш'] = "sh",
        ['щ'] = "shch",
        ['ъ'] = "",
        ['ы'] = "y",
        ['ь'] = "",
        ['э'] = "e",
        ['ю'] = "yu",
        ['я'] = "ya",
        ['и'] = "i",
    };

    public async Task<BookGenreOptionsDto> GetGenreOptionsAsync( CancellationToken cancellationToken )
    {
        ShopifySession shopSession = ShopifySessionReader.Require(
            _httpContextAccessor,
            "Няма Shopify-сесіі. Перазайдзіце праз Shopify." );

        ShopifyInventoryService.BookGenreMetafieldOptions options =
            await _shopifyInventory.GetBookGenreOptionsAsync(
                shopSession.Shop,
                shopSession.AccessToken );

        return new BookGenreOptionsDto
        {
            Namespace = options.Namespace,
            Key = options.Key,
            Type = options.TypeName,
            Options = options.Options.ToList(),
        };
    }

    public async Task<BookVendorOptionsDto> GetVendorOptionsAsync( CancellationToken cancellationToken )
    {
        ShopifySession shopSession = ShopifySessionReader.Require(
            _httpContextAccessor,
            "Няма Shopify-сесіі. Перазайдзіце праз Shopify." );

        IReadOnlyList<string> options = await _shopifyInventory.GetProductVendorOptionsAsync(
            shopSession.Shop,
            shopSession.AccessToken );

        return new BookVendorOptionsDto { Options = options.ToList() };
    }

    public async Task<BookSuggestVendorResultDto> SuggestVendorAsync(
        BookSuggestVendorRequest request,
        CancellationToken cancellationToken )
    {
        ShopifySession shopSession = ShopifySessionReader.Require(
            _httpContextAccessor,
            "Няма Shopify-сесіі. Перазайдзіце праз Shopify." );

        IReadOnlyList<string> vendors = await _shopifyInventory.GetProductVendorOptionsAsync(
            shopSession.Shop,
            shopSession.AccessToken );
        if (vendors.Count == 0)
        {
            return new BookSuggestVendorResultDto();
        }

        string? vendor = ShopifyInventoryService.ResolveVendorFromSources(
            request.Publisher,
            request.SupplierPageSnippet,
            request.PriceListRowText,
            vendors );

        return new BookSuggestVendorResultDto { Vendor = vendor };
    }

    public async Task<BookSuggestGenresResultDto> SuggestGenresAsync(
        BookSuggestGenresRequest request,
        CancellationToken cancellationToken )
    {
        ShopifySession shopSession = ShopifySessionReader.Require(
            _httpContextAccessor,
            "Няма Shopify-сесіі. Перазайдзіце праз Shopify." );

        ShopifyInventoryService.BookGenreMetafieldOptions options =
            await _shopifyInventory.GetBookGenreOptionsAsync(
                shopSession.Shop,
                shopSession.AccessToken );

        IReadOnlyList<string> allowed = options.Options;
        if (allowed.Count == 0)
        {
            return new BookSuggestGenresResultDto();
        }

        string title = (request.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( title )
            && string.IsNullOrWhiteSpace( request.Description )
            && string.IsNullOrWhiteSpace( request.SupplierPageSnippet )
            && string.IsNullOrWhiteSpace( request.PriceListRowText ))
        {
            return new BookSuggestGenresResultDto();
        }

        string descriptionBlob = string.Join(
            "\n",
            new[]
            {
                request.Title,
                request.Author,
                request.Publisher,
                request.Description,
                request.SupplierPageSnippet,
                request.PriceListRowText,
            }.Where( s => !string.IsNullOrWhiteSpace( s ) ) );

        string openAiKey = (_config["OpenAI:ApiKey"] ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace( openAiKey ))
        {
            try
            {
                List<string> suggested = await SuggestGenresWithOpenAiAsync(
                    openAiKey,
                    request,
                    allowed,
                    cancellationToken );
                IReadOnlyList<string> filtered =
                    ShopifyInventoryService.FilterGenresToAllowed( suggested, allowed );
                if (filtered.Count > 0)
                {
                    return new BookSuggestGenresResultDto { Genres = filtered.ToList() };
                }
            }
            catch (Exception ex) when (
                ex is InvalidOperationException
                || ex is HttpRequestException
                || ex is TaskCanceledException
                || ex is JsonException)
            {
                _logger.LogWarning( ex, "Genre suggest via OpenAI failed; trying Groq/local." );
            }
        }

        try
        {
            List<string> suggested = await SuggestGenresWithGroqAsync(
                request,
                allowed,
                cancellationToken );
            IReadOnlyList<string> filtered =
                ShopifyInventoryService.FilterGenresToAllowed( suggested, allowed );
            if (filtered.Count > 0)
            {
                return new BookSuggestGenresResultDto { Genres = filtered.ToList() };
            }
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
            || IsTransientGroqFailure( ex )
            || ex is HttpRequestException
            || ex is TaskCanceledException)
        {
            _logger.LogWarning( ex, "Genre suggest via Groq failed; trying local text match." );
        }

        IReadOnlyList<string> fallback =
            ShopifyInventoryService.MatchGenresInText( descriptionBlob, allowed );
        return new BookSuggestGenresResultDto { Genres = fallback.ToList() };
    }

    private async Task<List<string>> SuggestGenresWithOpenAiAsync(
        string apiKey,
        BookSuggestGenresRequest request,
        IReadOnlyList<string> allowed,
        CancellationToken cancellationToken )
    {
        string model = ResolveOpenAiModel();
        string description = TruncateForPrompt( request.Description, 2500 );
        string snippet = TruncateForPrompt( request.SupplierPageSnippet, 1200 );
        string priceRow = TruncateForPrompt( request.PriceListRowText, 800 );
        string allowedJson = JsonSerializer.Serialize( allowed );

        const string systemPrompt =
            """
            You assign book genres for a Belarusian bookstore catalog (Kirma.sh).
            Reply with ONE JSON object: {"genres":["..."]}.

            Rules:
            - Read title, author, description, supplier snippet and price-list row carefully.
            - Select ALL genres from Allowed that reasonably fit the book (not only the single best one).
            - Include every specific label that applies (e.g. both «Паэзія» and «Дзіцячая літаратура» when both fit).
            - Prefer specific labels, but still include broader ones when they are clearly correct too.
            - Every string in "genres" MUST be copied EXACTLY from the Allowed genres list (same spelling and language).
            - Do not invent new genres. Do not translate or paraphrase labels.
            - Return {"genres":[]} only when nothing in Allowed fits.
            - Order: most specific / central first, then additional fits.
            """;

        string userPrompt =
            $"""
            Title: {request.Title ?? ""}
            Author: {request.Author ?? ""}
            Publisher: {request.Publisher ?? ""}
            ISBN: {request.Isbn ?? ""}
            Cover type: {request.CoverType ?? ""}
            Description:
            {description}

            Supplier page snippet:
            {snippet}

            Price list row:
            {priceRow}

            Allowed genres (JSON array — copy strings exactly):
            {allowedJson}
            """;

        Dictionary<string, object?> payload = new()
        {
            ["model"] = model,
            // gpt-5.6-terra rejects custom temperature — omit (API default only).
            ["max_completion_tokens"] = 600,
            ["reasoning_effort"] = "low",
            ["response_format"] = new { type = "json_object" },
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            }
        };

        string body = await SendOpenAiChatAsync( apiKey, payload, cancellationToken );
        return ParseSuggestedGenreList( body );
    }

    private async Task<List<string>> SuggestGenresWithGroqAsync(
        BookSuggestGenresRequest request,
        IReadOnlyList<string> allowed,
        CancellationToken cancellationToken )
    {
        string apiKey = RequireGroqApiKey();
        string model = ResolveTextModel();

        string description = TruncateForPrompt( request.Description, 2500 );
        string snippet = TruncateForPrompt( request.SupplierPageSnippet, 1200 );
        string priceRow = TruncateForPrompt( request.PriceListRowText, 800 );
        string allowedJson = JsonSerializer.Serialize( allowed );

        const string systemPrompt =
            """
            You assign book genres for a Belarusian bookstore catalog.
            Reply with ONE JSON object: {"genres":["..."]}.
            Rules:
            - Read the title and description carefully and infer ALL fitting genres.
            - Select every Allowed label that reasonably fits (not only 1–3).
            - Every string in "genres" MUST be copied EXACTLY from the Allowed genres list (same spelling and language).
            - Do not invent new genres. Do not translate labels.
            - Prefer specific matching labels, but include all that apply.
            """;

        string userPrompt =
            $"""
            Title: {request.Title ?? ""}
            Author: {request.Author ?? ""}
            Publisher: {request.Publisher ?? ""}
            ISBN: {request.Isbn ?? ""}
            Cover type: {request.CoverType ?? ""}
            Description:
            {description}

            Supplier page snippet:
            {snippet}

            Price list row:
            {priceRow}

            Allowed genres (JSON array — copy strings exactly):
            {allowedJson}
            """;

        object payload = new
        {
            model,
            temperature = 0,
            max_completion_tokens = 600,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            }
        };

        string body = await SendGroqChatAsync( apiKey, payload, cancellationToken );
        return ParseSuggestedGenreList( body );
    }

    private static List<string> ParseSuggestedGenreList( string chatBody )
    {
        if (!TryGetMessageContent( chatBody, out string? content ) || string.IsNullOrWhiteSpace( content ))
        {
            return new List<string>();
        }

        string json = ExtractJsonObject( content );
        using JsonDocument doc = JsonDocument.Parse( json );
        if (!doc.RootElement.TryGetProperty( "genres", out JsonElement genresEl )
            || genresEl.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        List<string> result = new();
        foreach (JsonElement item in genresEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string? value = item.GetString();
                if (!string.IsNullOrWhiteSpace( value ))
                {
                    result.Add( value.Trim() );
                }
            }
        }

        return result;
    }

    private static string TruncateForPrompt( string? raw, int maxChars )
    {
        string text = (raw ?? string.Empty).Trim();
        if (text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars];
    }

    private async Task AttachAdditionalGalleryImagesAsync(
        ShopifySession shopSession,
        string productId,
        string? coverImageUrl,
        IReadOnlyList<string>? additionalImageUrls,
        CancellationToken cancellationToken )
    {
        if (additionalImageUrls is null || additionalImageUrls.Count == 0)
        {
            return;
        }

        HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );
        if (!string.IsNullOrWhiteSpace( coverImageUrl ))
        {
            seen.Add( coverImageUrl.Trim() );
        }

        int index = 0;
        foreach (string raw in additionalImageUrls)
        {
            string url = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( url )
                || !Uri.TryCreate( url, UriKind.Absolute, out Uri? uri )
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !seen.Add( url ))
            {
                continue;
            }

            index++;
            try
            {
                byte[]? bytes = await TryDownloadCoverBytesAsync( url, cancellationToken );
                if (bytes is { Length: > 0 })
                {
                    byte[] resized = _coverStylizer.ResizeMaxWidthJpeg( bytes, BookCoverStylizer.OutputSize );
                    await _shopifyInventory.AttachProductImageAsync(
                        shopSession.Shop,
                        shopSession.AccessToken,
                        productId,
                        resized,
                        $"gallery-{index}.jpg" );
                }
                else
                {
                    _logger.LogWarning(
                        "Skipping gallery CDN image {Index} for product {ProductId}; bytes unavailable",
                        index,
                        productId );
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to attach gallery image {Index} to product {ProductId}",
                    index,
                    productId );
            }
        }
    }

    public async Task<BookFetchCoverResultDto> FetchCoverImageAsync(
        BookFetchCoverRequest request,
        CancellationToken cancellationToken )
    {
        string rawUrl = (request.Url ?? string.Empty).Trim();
        if (!Uri.TryCreate( rawUrl, UriKind.Absolute, out Uri? uri )
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new BookFetchCoverResultDto
            {
                Found = false,
                SourceUrl = rawUrl,
                Error = "Invalid absolute http(s) URL.",
            };
        }

        RemoteImageFetchResult fetch = await _imageFetcher.FetchAsync(
            rawUrl,
            request.PageUrl,
            cancellationToken );

        string? fetchSource = fetch.Source.ToString().ToLowerInvariant();

        _logger.LogInformation(
            "fetch-cover diag SourceUrl={SourceUrl} FinalUrl={FinalUrl} Status={Status} FetchSource={FetchSource} DirectStatus={DirectStatus} ShouldFallbackToRelay={ShouldFallback} RelayConfigured={RelayConfigured} RelayAttempted={RelayAttempted} RelayStatus={RelayStatus} RelayUrlHost={RelayUrlHost} ContentType={ContentType} ByteLength={ByteLength} TempMediaId={TempMediaId} Error={Error} RelayError={RelayError}",
            rawUrl,
            fetch.FinalUrl,
            fetch.StatusCode,
            fetchSource,
            fetch.DirectStatus,
            fetch.ShouldFallbackToRelay,
            fetch.RelayConfigured,
            fetch.RelayAttempted,
            fetch.RelayStatus,
            fetch.RelayUrlHost,
            fetch.MimeType,
            fetch.ByteLength,
            (string?)null,
            fetch.Error,
            fetch.RelayError );

        if (!fetch.Success || fetch.Bytes is null || fetch.Bytes.Length == 0)
        {
            return new BookFetchCoverResultDto
            {
                Found = false,
                SourceUrl = rawUrl,
                FinalUrl = fetch.FinalUrl,
                StatusCode = fetch.StatusCode,
                ReasonPhrase = fetch.ReasonPhrase,
                ContentType = fetch.MimeType,
                ContentLengthHeader = fetch.ContentLengthHeader,
                ByteLength = fetch.ByteLength,
                Error = fetch.Error ?? "Download returned no image bytes.",
                ExceptionType = fetch.ExceptionType,
                FetchSource = fetchSource,
                DirectStatus = fetch.DirectStatus ?? fetch.StatusCode,
                RelayConfigured = fetch.RelayConfigured,
                RelayAttempted = fetch.RelayAttempted,
                ShouldFallbackToRelay = fetch.ShouldFallbackToRelay,
                RelayStatus = fetch.RelayStatus,
                RelayError = fetch.RelayError,
                RelayUrlHost = fetch.RelayUrlHost,
            };
        }

        string contentType = RemoteImageContent.GuessMimeType( fetch.Bytes )
            ?? fetch.MimeType
            ?? "image/jpeg";
        if (!contentType.StartsWith( "image/", StringComparison.OrdinalIgnoreCase ))
        {
            contentType = RemoteImageContent.GuessMimeType( fetch.Bytes ) ?? "image/jpeg";
        }

        BookTempMediaEntry stored = _tempMedia.Put( fetch.Bytes, contentType, rawUrl );

        _logger.LogInformation(
            "fetch-cover cached SourceUrl={SourceUrl} FetchSource={FetchSource} ByteLength={ByteLength} MimeType={MimeType} TempMediaId={TempMediaId} DirectStatus={DirectStatus} RelayAttempted={RelayAttempted} RelayUrlHost={RelayUrlHost}",
            rawUrl,
            fetchSource,
            fetch.Bytes.Length,
            contentType,
            stored.Id,
            fetch.DirectStatus,
            fetch.RelayAttempted,
            fetch.RelayUrlHost );

        return new BookFetchCoverResultDto
        {
            Found = true,
            CoverImageBase64 = null,
            TempMediaId = stored.Id,
            TempMediaPath = $"/books/temp-media/{stored.Id}",
            SourceUrl = rawUrl,
            FinalUrl = fetch.FinalUrl,
            StatusCode = fetch.StatusCode,
            ReasonPhrase = fetch.ReasonPhrase,
            ContentType = contentType,
            ContentLengthHeader = fetch.ContentLengthHeader,
            ByteLength = fetch.Bytes.Length,
            FetchSource = fetchSource,
            DirectStatus = fetch.DirectStatus,
            RelayConfigured = fetch.RelayConfigured,
            RelayAttempted = fetch.RelayAttempted,
            ShouldFallbackToRelay = fetch.ShouldFallbackToRelay,
            RelayStatus = fetch.RelayStatus,
            RelayError = fetch.RelayError,
            RelayUrlHost = fetch.RelayUrlHost,
        };
    }

    public bool TryGetTempMedia( string id, out BookTempMediaEntry entry ) =>
        _tempMedia.TryGet( id, out entry );

    public async Task<BookStyleCoverResultDto> StyleCoverPreviewAsync(
        BookStyleCoverRequest request,
        CancellationToken cancellationToken )
    {
        byte[]? sourceBytes = null;
        string? sourceTempId = null;

        if (!string.IsNullOrWhiteSpace( request.CoverTempMediaId )
            && _tempMedia.TryGet( request.CoverTempMediaId, out BookTempMediaEntry tempSource )
            && tempSource.Bytes.Length > 0)
        {
            sourceBytes = tempSource.Bytes;
            sourceTempId = tempSource.Id;
        }

        sourceBytes ??= TryDecodeImagePayload( request.CoverImageBase64 );

        if (sourceBytes is null
            && !string.IsNullOrWhiteSpace( request.SessionId )
            && _sessions.TryGet( request.SessionId, out BookLookupSessionState session )
            && session.CoverImageBytes is { Length: > 0 })
        {
            sourceBytes = session.CoverImageBytes;
        }

        if (sourceBytes is null)
        {
            string rawCoverUrl = (request.CoverImageUrl ?? string.Empty).Trim();
            if (Uri.TryCreate( rawCoverUrl, UriKind.Absolute, out Uri? coverUri )
                && (coverUri.Scheme == Uri.UriSchemeHttp || coverUri.Scheme == Uri.UriSchemeHttps))
            {
                sourceBytes = await TryDownloadCoverBytesAsync( rawCoverUrl, cancellationToken );
            }
        }

        if (sourceBytes is null || sourceBytes.Length == 0)
        {
            throw new InvalidOperationException( "Няма выявы вокладкі для апрацоўкі." );
        }

        try
        {
            byte[] styledPng = _coverStylizer.StyleToSquarePng( sourceBytes );
            BookTempMediaEntry stored = _tempMedia.Put( styledPng, "image/png", sourceTempId );
            // Keep source temp around until attach succeeds — attach must use styled id,
            // but deleting source here races with create that still holds the fetch id.
            return new BookStyleCoverResultDto
            {
                StyledCoverDataUrl = ToDataUrl( styledPng, "image/png" ),
                TempMediaId = stored.Id,
                TempMediaPath = $"/books/temp-media/{stored.Id}",
            };
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogWarning( ex, "Cover stylizer failed" );
            throw new InvalidOperationException( "Не ўдалося апрацаваць выяву вокладкі." );
        }
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
        public string? OgImage { get; init; }
        public string? Description { get; init; }
        public decimal? SalePrice { get; init; }
        /// <summary>Raw HTML body (capped) for full catalog field parsing.</summary>
        public string? RawHtml { get; init; }

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
            const int maxBytes = 400_000;
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
            string? ogImage = PreferRealCoverImageUrl(
                ExtractCatalogCoverImageUrl( html, url ),
                ExtractMetaContent( html, "og:image" ),
                ExtractMetaContent( html, "twitter:image" ),
                ExtractMetaContent( html, "twitter:image:src" ) );

            string? description = DecodeHtml(
                ExtractMetaContent( html, "og:description" )
                ?? ExtractMetaContent( html, "description" ) );
            if (!string.IsNullOrWhiteSpace( description ))
            {
                description = StripHtmlTags( description ).Trim();
                if (description.Length < 20)
                {
                    description = null;
                }
                else if (description.Length > 4000)
                {
                    description = description[..4000].TrimEnd() + "…";
                }
            }

            string plain = StripHtmlTags( html );
            decimal? salePrice = ExtractPriceFromPlainText( plain );

            return new PageTitleHints
            {
                OgTitle = DecodeHtml( og ),
                HtmlTitle = DecodeHtml( htmlTitle ),
                H1 = DecodeHtml( h1 ),
                AuthorHint = DecodeHtml( authorHint ),
                OgImage = DecodeHtml( ogImage ),
                Description = description,
                SalePrice = salePrice,
                RawHtml = html,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug( ex, "Could not fetch page title hints for {Url}", url );
            return new PageTitleHints();
        }
    }

    private static decimal? ExtractPriceFromPlainText( string plain )
    {
        if (string.IsNullOrWhiteSpace( plain ))
        {
            return null;
        }

        // "35,00 zł" / "35.00 PLN" / "£6.00" / "$12" near product price wording.
        Match m = Regex.Match(
            plain,
            @"(?:£|\$|€)\s*(?<v>\d{1,4}(?:[.,]\d{2})?)\b|(?<!\d)(?<v>\d{1,4}(?:[.,]\d{2})?)\s*(?:zł|zl|PLN|Br|BYN|€|EUR|GBP|USD)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );
        if (!m.Success)
        {
            return null;
        }

        string raw = m.Groups["v"].Value.Replace( ',', '.' );
        if (!decimal.TryParse( raw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value )
            || value <= 0
            || value > 100_000)
        {
            return null;
        }

        return Math.Round( value, 2, MidpointRounding.AwayFromZero );
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
            // Kamunikat / LiveLib: author profile link under the title.
            m = Regex.Match(
                html,
                @"<a\b[^>]*href\s*=\s*[""'](?<href>[^""']*(?:/author/|author|autor|auhtar|аўтар)[^""']*)[""'][^>]*>(?<v>.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline );
        }

        if (!m.Success)
        {
            return null;
        }

        string inner = Regex.Replace( m.Groups["v"].Value, "<[^>]+>", " " );
        inner = Regex.Replace( inner, @"\s+", " " ).Trim();
        string? normalized = NormalizeAuthorCandidate( inner );
        if (!string.IsNullOrWhiteSpace( normalized ))
        {
            return normalized;
        }

        if (m.Groups["href"].Success)
        {
            return NormalizeAuthorCandidate( m.Groups["href"].Value );
        }

        return null;
    }

    /// <summary>
    /// Drop profile URLs mistakenly used as author; recover a name from LiveLib author slugs.
    /// </summary>
    private static string? NormalizeAuthorCandidate( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string t = DecodeHtml( raw )?.Trim() ?? string.Empty;
        if (t.Length == 0)
        {
            return null;
        }

        // Bare or absolute author profile URLs (LiveLib meta/og often does this).
        if (Regex.IsMatch(
                t,
                @"^(?:https?:\/\/)?(?:www\.)?livelib\.ru\/author\/",
                RegexOptions.IgnoreCase )
            || Regex.IsMatch( t, @"^https?:\/\/", RegexOptions.IgnoreCase )
            || t.Contains( "/author/", StringComparison.OrdinalIgnoreCase ))
        {
            Match slug = Regex.Match(
                t,
                @"/author/(?:\d+-)?(?<slug>[a-z0-9\-_%]+)/?",
                RegexOptions.IgnoreCase );
            if (slug.Success)
            {
                string fromSlug = HumanizeAuthorSlug( slug.Groups["slug"].Value );
                return string.IsNullOrWhiteSpace( fromSlug ) ? null : fromSlug;
            }

            return null;
        }

        if (t.Contains( "://", StringComparison.Ordinal )
            || t.Contains( "www.", StringComparison.OrdinalIgnoreCase ))
        {
            return null;
        }

        // Reject leftover nav / shop chrome junk.
        if (Regex.IsMatch(
                t,
                @"^(Home|Галоўная|Главная|Хочу|ISBN|Издател|Выдавец|Add to cart|Добавить в корзину|Дадаць у кошык|In stock|Out of stock|Browse Shop|Your cart|Share|Tweet|Quantity|Колькасць)\b",
                RegexOptions.IgnoreCase ))
        {
            return null;
        }

        if (Regex.IsMatch(
                t,
                @"add to cart|добавить в корзину|дадаць у кошык|browse shop|your cart is empty",
                RegexOptions.IgnoreCase ))
        {
            return null;
        }

        return t.Length is >= 2 and <= 120 ? t : null;
    }

    private static string HumanizeAuthorSlug( string slug )
    {
        string s = Uri.UnescapeDataString( (slug ?? string.Empty).Trim() )
            .Replace( '_', '-' );
        if (string.IsNullOrWhiteSpace( s ))
        {
            return string.Empty;
        }

        // Drop trailing truncated bits that are clearly incomplete alone.
        string[] parts = s.Split( '-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        if (parts.Length == 0)
        {
            return string.Empty;
        }

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

        string text = StripScriptAndStyleBlocks( value );
        text = Regex.Replace( text, "<[^>]+>", " " );
        return Regex.Replace( text, @"\s+", " " ).Trim();
    }

    /// <summary>
    /// HTML/markdown → plain text keeping line breaks (needed for LiveLib labeled fields).
    /// </summary>
    private static string HtmlToPlainPreserveLines( string? value )
    {
        if (string.IsNullOrWhiteSpace( value ))
        {
            return string.Empty;
        }

        string text = StripScriptAndStyleBlocks( value );
        text = Regex.Replace(
            text,
            @"<(?:br|/?p|/?div|/?li|/?tr|/?h[1-6]|/?section|/?article|/?header|/?footer)[^>]*>",
            "\n",
            RegexOptions.IgnoreCase );
        text = Regex.Replace( text, "<[^>]+>", " " );
        text = System.Net.WebUtility.HtmlDecode( text ) ?? text;
        text = text.Replace( '\u00a0', ' ' );
        text = Regex.Replace( text, @"[^\S\r\n]+", " " );
        text = Regex.Replace( text, @"\r\n?|\n", "\n" );
        text = Regex.Replace( text, @"\n{3,}", "\n\n" );
        return text.Trim();
    }

    private static string StripScriptAndStyleBlocks( string html ) =>
        Regex.Replace(
            html,
            @"<(script|style|noscript|template|svg)\b[^>]*>[\s\S]*?</\1\s*>",
            " ",
            RegexOptions.IgnoreCase );

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
        // Drop trailing "Автор: …" if still glued to the title.
        t = Regex.Replace(
            t,
            @"\s*[.…]?\s*(?:Автор(?:ы)?|Аўтар(?:ы)?|Author(?:s)?)\s*[:：].*$",
            string.Empty,
            RegexOptions.IgnoreCase );
        return UnwrapTitleQuotes( t.Trim().Trim( ',', ';', '.', ':', '—' ).Trim() );
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

        // «Title». Автор: Name  /  "Title". Author: Name
        Match labeledAuthor = Regex.Match(
            t,
            @"^(?<title>.+?)\s*[.…]?\s*(?:Автор(?:ы)?|Аўтар(?:ы)?|Author(?:s)?)\s*[:：]\s*(?<author>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );
        if (labeledAuthor.Success)
        {
            string titleOnly = UnwrapTitleQuotes( labeledAuthor.Groups["title"].Value.Trim() );
            string authorOnly = labeledAuthor.Groups["author"].Value.Trim().Trim( '.', ';', ',' );
            if (titleOnly.Length >= 1 && authorOnly.Length >= 2)
            {
                return (
                    titleOnly,
                    FormatAuthorFirstLast( authorOnly, knownAuthor ) );
            }
        }

        t = UnwrapTitleQuotes( t );

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

        // Supplier catalogs often use Surname GivenName — flip to GivenName Surname.
        if (LooksLikeSurname( parts[0] ) && !LooksLikeSurname( parts[1] ))
        {
            return $"{parts[1]} {parts[0]}";
        }

        if (LooksLikeGivenName( parts[1] ) && !LooksLikeGivenName( parts[0] ))
        {
            return $"{parts[1]} {parts[0]}";
        }

        return author;
    }

    private static bool LooksLikeSurname( string token )
    {
        string t = token.Trim().ToLowerInvariant();
        if (t.Length < 2)
        {
            return false;
        }

        // Compound surnames: Прымака-Онішк, Скарына-Мінскі.
        if (t.Contains( '-', StringComparison.Ordinal ))
        {
            return true;
        }

        return t.EndsWith( "віч", StringComparison.Ordinal )
            || t.EndsWith( "вич", StringComparison.Ordinal )
            || t.EndsWith( "ўна", StringComparison.Ordinal )
            || t.EndsWith( "евна", StringComparison.Ordinal )
            || t.EndsWith( "овна", StringComparison.Ordinal )
            || t.EndsWith( "скі", StringComparison.Ordinal )
            || t.EndsWith( "ская", StringComparison.Ordinal )
            || t.EndsWith( "цкая", StringComparison.Ordinal )
            || t.EndsWith( "cki", StringComparison.Ordinal )
            || t.EndsWith( "cka", StringComparison.Ordinal )
            || t.EndsWith( "ska", StringComparison.Ordinal )
            || t.EndsWith( "ski", StringComparison.Ordinal )
            || t.EndsWith( "енка", StringComparison.Ordinal )
            || t.EndsWith( "энка", StringComparison.Ordinal )
            || t.EndsWith( "оў", StringComparison.Ordinal )
            || t.EndsWith( "ёў", StringComparison.Ordinal )
            || t.EndsWith( "ов", StringComparison.Ordinal )
            || t.EndsWith( "ова", StringComparison.Ordinal )
            || t.EndsWith( "ева", StringComparison.Ordinal )
            || t.EndsWith( "ёва", StringComparison.Ordinal )
            // Багданава, Купалава — беларускія жаночыя на -ава
            || t.EndsWith( "ава", StringComparison.Ordinal )
            || t.EndsWith( "ина", StringComparison.Ordinal )
            || t.EndsWith( "іна", StringComparison.Ordinal )
            || t.EndsWith( "ына", StringComparison.Ordinal )
            || t.EndsWith( "ук", StringComparison.Ordinal )
            || t.EndsWith( "юк", StringComparison.Ordinal )
            || t.EndsWith( "як", StringComparison.Ordinal )
            || t.EndsWith( "ец", StringComparison.Ordinal )
            || t.EndsWith( "шк", StringComparison.Ordinal )
            || t.EndsWith( "ік", StringComparison.Ordinal )
            || t.EndsWith( "ык", StringComparison.Ordinal );
    }

    /// <summary>Heuristic for given names (Анэта, Мікалай) vs surnames.</summary>
    private static bool LooksLikeGivenName( string token )
    {
        string t = token.Trim().ToLowerInvariant();
        if (t.Length < 2 || t.Contains( '-', StringComparison.Ordinal ))
        {
            return false;
        }

        if (LooksLikeSurname( t ))
        {
            return false;
        }

        // Feminine / soft endings common for given names.
        if (t.EndsWith( "а", StringComparison.Ordinal )
            || t.EndsWith( "я", StringComparison.Ordinal )
            || t.EndsWith( "ія", StringComparison.Ordinal )
            || t.EndsWith( "ия", StringComparison.Ordinal ))
        {
            return true;
        }

        // Short-ish given names without surname endings.
        return t.Length <= 8;
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


    private static string? ExtractIsbnFromText( string text )
    {
        if (string.IsNullOrWhiteSpace( text ))
        {
            return null;
        }

        // LiveLib often repeats the label: "ISBN: ISBN: 83-60456-03-8"
        string? labeled = ExtractLabeledField( text, "ISBN", "ІСБН", "Isbn" );
        if (!string.IsNullOrWhiteSpace( labeled ))
        {
            string? fromLabel = IsbnUtil.NormalizePreferHyphens( labeled );
            if (!string.IsNullOrWhiteSpace( fromLabel ))
            {
                return fromLabel;
            }
        }

        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            text,
            @"\b97[89][\d\- ]{10,20}\b" ))
        {
            string? normalized = IsbnUtil.NormalizePreferHyphens( m.Value );
            if (!string.IsNullOrWhiteSpace( normalized ))
            {
                return normalized;
            }
        }

        // ISBN-10 (e.g. 83-60456-03-8) — checksum filters false positives.
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            text,
            @"\b[\dXx](?:[\dXx]|[\- ]){8,14}[\dXx]\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase ))
        {
            string? normalized = IsbnUtil.NormalizePreferHyphens( m.Value );
            if (!string.IsNullOrWhiteSpace( normalized ))
            {
                return normalized;
            }
        }

        return null;
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
                    "Праверце Groq:Model / GROQ_MODEL." );
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

    private async Task<string> SendOpenAiChatAsync(
        string apiKey,
        object payload,
        CancellationToken cancellationToken )
    {
        HttpClient client = _httpClientFactory.CreateClient( "OpenAI" );
        const int maxAttempts = 3;
        string? lastBody = null;
        int lastStatus = 0;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                "https://api.openai.com/v1/chat/completions" );
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
                "OpenAI chat failed attempt {Attempt}/{Max}: {Status} {Body}",
                attempt,
                maxAttempts,
                lastStatus,
                body );

            bool retriable = lastStatus is 429 or 500 or 502 or 503;
            if (!retriable || attempt >= maxAttempts)
            {
                break;
            }

            int delayMs = TryReadRetryAfterMs( response )
                ?? (int)Math.Min( 12_000, 800 * Math.Pow( 2, attempt - 1 ) );
            await Task.Delay( delayMs, cancellationToken );
        }

        if (lastStatus == 401 || lastStatus == 403)
        {
            throw new InvalidOperationException(
                "OpenAI API: invalid key or no access (check OPENAI_API_KEY)." );
        }

        if (lastStatus == 429)
        {
            throw new InvalidOperationException(
                "OpenAI API: rate limit (429). Wait and retry." );
        }

        throw new InvalidOperationException(
            $"OpenAI API error: {lastStatus}. {TruncateForPrompt( lastBody, 200 )}" );
    }

    private string ResolveOpenAiModel()
    {
        string model = (_config["OpenAI:Model"] ?? "gpt-5.6-terra").Trim();
        return string.IsNullOrWhiteSpace( model ) ? "gpt-5.6-terra" : model;
    }

    private string ResolveTextModel()
    {
        string model = (_config["Groq:Model"] ?? "openai/gpt-oss-20b").Trim();
        return string.IsNullOrWhiteSpace( model ) ? "openai/gpt-oss-20b" : model;
    }


    private static string ToDataUrl( byte[] bytes, string? contentType )
    {
        string mime = string.IsNullOrWhiteSpace( contentType ) ? "image/jpeg" : contentType.Trim();
        return $"data:{mime};base64,{Convert.ToBase64String( bytes )}";
    }

    private async Task<byte[]?> TryDownloadCoverBytesAsync(
        string imageUrl,
        CancellationToken cancellationToken )
    {
        RemoteImageFetchResult fetch = await _imageFetcher.FetchAsync(
            imageUrl,
            pageUrl: null,
            cancellationToken );
        return fetch.Success && fetch.Bytes is { Length: > 0 } ? fetch.Bytes : null;
    }


    private static string? GuessImageContentType( byte[] bytes ) =>
        RemoteImageContent.GuessMimeType( bytes );

    private byte[]? TryDecodeImagePayload( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string payload = raw.Trim();
        if (payload.StartsWith( "data:", StringComparison.OrdinalIgnoreCase ))
        {
            int comma = payload.IndexOf( ',' );
            if (comma < 0 || comma >= payload.Length - 1)
            {
                return null;
            }

            payload = payload[(comma + 1)..];
        }

        try
        {
            byte[] bytes = Convert.FromBase64String( payload );
            if (bytes.Length == 0 || bytes.Length > MaxFileBytes)
            {
                return null;
            }

            return bytes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "Failed to decode cover image base64 payload" );
            return null;
        }
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

}
