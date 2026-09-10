using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using backend.Models;

namespace backend.Services;

public sealed class GroqInvoiceExtractionService
{
    private const int MaxCatalogCandidates = 40;
    private const int TopCandidatesPerLine = 8;
    private const int MaxProductNameChars = 90;
    private const int MaxInvoiceCharsForLlm = 4_500;
    private const int MaxPromptChars = 12_000;
    private const double LocalMatchMinScore = 0.55;
    private const int MatchBatchSize = 12;

    private static readonly Regex InvoiceSkuPrefixRegex = new(
        @"^(?:[A-Z]{2,6}-[A-F0-9]{8,20})\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled );

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<GroqInvoiceExtractionService> _logger;

    public GroqInvoiceExtractionService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<GroqInvoiceExtractionService> logger )
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<VatReportExpenseInvoiceExtractResult> ExtractFromTextAsync(
        string invoiceText,
        IReadOnlyList<string> expenseTypeNames,
        CancellationToken cancellationToken = default )
    {
        string apiKey = RequireApiKey();
        string model = ResolveModel();

        string typesList = expenseTypeNames.Count == 0
            ? "(none)"
            : string.Join( ", ", expenseTypeNames.Select( t => $"\"{t}\"" ) );

        const string systemPrompt =
            """
            Extract Polish VAT invoice HEADER fields only (not every product line).
            Reply with ONE JSON object only (no markdown).
            Keys: invoiceNumber, expenseDate (YYYY-MM-DD), grossAmount, vatAmount, netAmount,
            vendorName, comment, suggestedExpenseTypeName, products (always []).
            Unknown fields: null. Dot decimals. Prefer invoice totals (Do zapłaty / Razem).
            Example: {"invoiceNumber":null,"expenseDate":null,"grossAmount":null,"vatAmount":null,"netAmount":null,"vendorName":null,"comment":null,"suggestedExpenseTypeName":null,"products":[]}
            """;

        // Header+totals only — line items are parsed separately (large invoices exceed TPM).
        string clippedInvoice = InvoicePdfTextExtractor.ClipKeepingHeadAndTail(
            invoiceText,
            MaxInvoiceCharsForLlm );
        string userPrompt =
            $"""
            Allowed expense type names: {typesList}

            Invoice text:
            ---
            {clippedInvoice}
            ---
            """;

        LlmInvoiceFields fields = await CallGroqAsync<LlmInvoiceFields>(
            apiKey,
            model,
            systemPrompt,
            userPrompt,
            cancellationToken );

        return NormalizeHeader( fields, expenseTypeNames );
    }

    /// <summary>
    /// Fallback when deterministic PDF parsing finds no rows: extract products from text chunks via Groq.
    /// </summary>
    public async Task ExtractProductsFromTextChunksAsync(
        VatReportExpenseInvoiceExtractResult result,
        string invoiceText,
        CancellationToken cancellationToken = default )
    {
        if (string.IsNullOrWhiteSpace( invoiceText ))
        {
            return;
        }

        string apiKey = RequireApiKey();
        string model = ResolveModel();

        const string systemPrompt =
            """
            Extract product/service LINE ITEMS from a Polish VAT invoice fragment.
            Reply with ONE JSON object only (no markdown).
            Shape: {"products":[{"title":"...","barcode":null,"quantity":1,"unitGrossPrice":null,"vatRatePercent":null}]}
            Rules:
            - Include every product/book line in this fragment.
            - title = readable book/product name; SKU prefixes like CZB-A8FE… may stay or be dropped.
            - unitGrossPrice = unit price AFTER discount (Razem/Ilość) when possible.
            - quantity integer >= 1. Skip shipping-only / header / totals rows.
            - If no products in this fragment, return {"products":[]}.
            """;

        const int chunkSize = 3_200;
        const int overlap = 250;
        List<VatReportExpenseInvoiceExtractProduct> all = new();
        HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );

        for (int start = 0; start < invoiceText.Length; start += Math.Max( 1, chunkSize - overlap ))
        {
            int len = Math.Min( chunkSize, invoiceText.Length - start );
            string chunk = invoiceText.Substring( start, len );
            if (chunk.Trim().Length < 40)
            {
                break;
            }

            string userPrompt =
                $"""
                Invoice fragment:
                ---
                {chunk}
                ---
                """;

            try
            {
                LlmInvoiceFields fields = await CallGroqAsync<LlmInvoiceFields>(
                    apiKey,
                    model,
                    systemPrompt,
                    userPrompt,
                    cancellationToken );

                if (fields.Products is not { Count: > 0 })
                {
                    continue;
                }

                foreach (LlmInvoiceProductLine line in fields.Products)
                {
                    string? title = NullIfWhite( line.Title );
                    if (title is null)
                    {
                        continue;
                    }

                    title = CleanInvoiceProductTitle( title );
                    if (string.IsNullOrWhiteSpace( title ) || title.Length < 3)
                    {
                        continue;
                    }

                    int qty = line.Quantity is > 0 ? (int)Math.Round( line.Quantity.Value ) : 1;
                    if (qty <= 0)
                    {
                        qty = 1;
                    }

                    string dedupeKey = $"{title.ToLowerInvariant()}::{qty}";
                    if (!seen.Add( dedupeKey ))
                    {
                        continue;
                    }

                    string? barcode = NullIfWhite( line.Barcode );
                    if (barcode is not null)
                    {
                        barcode = new string( barcode.Where( char.IsDigit ).ToArray() );
                        if (barcode.Length == 0)
                        {
                            barcode = null;
                        }
                    }

                    all.Add( new VatReportExpenseInvoiceExtractProduct
                    {
                        Title = title,
                        Barcode = barcode,
                        Quantity = qty,
                        UnitGrossPrice = RoundMoney( line.UnitGrossPrice ),
                        VatRatePercent = RoundMoney( line.VatRatePercent )
                    } );
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning( ex, "Groq product chunk extract failed at offset {Offset}.", start );
            }

            if (start + len >= invoiceText.Length)
            {
                break;
            }
        }

        if (all.Count > 0)
        {
            result.Products = all;
        }
    }

    public async Task MatchProductsToCatalogAsync(
        VatReportExpenseInvoiceExtractResult result,
        IReadOnlyList<InvoiceCatalogMatchCandidate> catalog,
        CancellationToken cancellationToken = default )
    {
        if (result.Products.Count == 0 || catalog.Count == 0)
        {
            return;
        }

        // Normalize titles for matching (drop SKU noise); keep readable book name for Groq.
        foreach (VatReportExpenseInvoiceExtractProduct product in result.Products)
        {
            string cleaned = CleanInvoiceProductTitle( product.Title );
            if (!string.IsNullOrWhiteSpace( cleaned ))
            {
                product.Title = cleaned;
            }
        }

        // Groq is primary for semantic title matching; process in small batches (free-tier TPM).
        int groqMatchedBatches = 0;
        int groqFailedBatches = 0;
        try
        {
            string apiKey = RequireApiKey();
            string model = ResolveModel();

            for (int offset = 0; offset < result.Products.Count; offset += MatchBatchSize)
            {
                List<int> batchIndexes = Enumerable
                    .Range( offset, Math.Min( MatchBatchSize, result.Products.Count - offset ) )
                    .ToList();

                List<VatReportExpenseInvoiceExtractProduct> batchProducts =
                    batchIndexes.Select( i => result.Products[i] ).ToList();

                List<InvoiceCatalogMatchCandidate> shortlist =
                    SelectCatalogShortlist( batchProducts, catalog );

                var invoiceLines = batchIndexes.Select( index =>
                {
                    VatReportExpenseInvoiceExtractProduct p = result.Products[index];
                    return new
                    {
                        i = index,
                        title = Truncate( p.Title, MaxProductNameChars ),
                        barcode = p.Barcode,
                        quantity = p.Quantity,
                        unitGrossPrice = p.UnitGrossPrice,
                        vatRatePercent = p.VatRatePercent
                    };
                } );

                string catalogBlock = BuildCatalogBlock( shortlist, MaxPromptChars - 2_500 );

                const string systemPrompt =
                    """
                    You match invoice book/product lines to a shop catalog by meaning of the title.
                    Reply with ONE JSON object only (no markdown).

                    Rules:
                    - Identify the same book even if wording, language, script, transliteration, punctuation, or author order differs.
                    - Ignore supplier SKUs/codes (e.g. CZB-A8FEAB60DF7A) — match the human book title.
                    - Prefer barcode/ISBN when present and reliable.
                    - If several catalog rows are similar, pick the closest title; if truly unsure, use null ids.
                    - Never invent shopifyProductId / shopifyVariantId — only ids from the catalog list.

                    Shape:
                    {"matches":[{"i":0,"shopifyProductId":null,"shopifyVariantId":null,"catalogProductName":null}]}
                    Include every invoice line index i exactly once.
                    """;

                string userPrompt =
                    $"""
                    Invoice lines (match each i to one catalog row or null):
                    {JsonSerializer.Serialize( invoiceLines )}

                    Catalog candidates (id|variant|name|vat|price):
                    {catalogBlock}
                    """;

                try
                {
                    LlmMatchResponse matchResponse = await CallGroqAsync<LlmMatchResponse>(
                        apiKey,
                        model,
                        systemPrompt,
                        userPrompt,
                        cancellationToken );

                    ApplyMatches( result, shortlist, matchResponse, onlyFillEmpty: true );
                    groqMatchedBatches++;
                }
                catch (Exception batchEx)
                {
                    groqFailedBatches++;
                    _logger.LogWarning(
                        batchEx,
                        "Groq product match batch failed at offset {Offset}.",
                        offset );
                }
            }

            if (groqFailedBatches > 0 && groqMatchedBatches == 0)
            {
                AppendWarning(
                    result,
                    "AI-супастаўленне не ўдалося — спрабуем лакальны пошук па назве." );
            }
            else if (groqFailedBatches > 0)
            {
                AppendWarning(
                    result,
                    $"AI-супастаўленне часткова: {groqMatchedBatches} пачак ОК, {groqFailedBatches} з памылкай." );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "Groq product match failed; falling back to local title match." );
            AppendWarning(
                result,
                "AI-супастаўленне не ўдалося — спрабуем лакальны пошук па назве." );
        }

        // Fallback only for lines Groq left unmatched.
        ApplyLocalCatalogMatches( result, catalog );
        FinalizeMatchWarnings( result );
    }

    private static void ApplyLocalCatalogMatches(
        VatReportExpenseInvoiceExtractResult result,
        IReadOnlyList<InvoiceCatalogMatchCandidate> catalog )
    {
        HashSet<string> usedKeys = new( StringComparer.OrdinalIgnoreCase );

        foreach (VatReportExpenseInvoiceExtractProduct product in result.Products)
        {
            if (!string.IsNullOrWhiteSpace( product.ShopifyProductId ))
            {
                usedKeys.Add( CatalogKey( product.ShopifyProductId, product.ShopifyVariantId ) );
                continue;
            }

            string barcode = new string( (product.Barcode ?? string.Empty).Where( char.IsDigit ).ToArray() );
            string cleanTitle = CleanInvoiceProductTitle( product.Title );
            string titleNorm = NormalizeMatchText( cleanTitle );
            string titleLatin = ToLatinFold( titleNorm );
            string titleCyr = ToCyrillicFold( titleNorm );

            List<(InvoiceCatalogMatchCandidate Candidate, double Score)> ranked = new();
            foreach (InvoiceCatalogMatchCandidate candidate in catalog)
            {
                string key = CatalogKey( candidate.ShopifyProductId, candidate.ShopifyVariantId );
                if (usedKeys.Contains( key ))
                {
                    continue;
                }

                double score = ScoreCatalogCandidate(
                    cleanTitle,
                    barcode,
                    titleNorm,
                    titleLatin,
                    titleCyr,
                    candidate );
                if (score >= LocalMatchMinScore)
                {
                    ranked.Add( (candidate, score) );
                }
            }

            if (ranked.Count == 0)
            {
                continue;
            }

            ranked.Sort( ( a, b ) => b.Score.CompareTo( a.Score ) );
            (InvoiceCatalogMatchCandidate best, double bestScore) = ranked[0];
            double secondScore = ranked.Count > 1 ? ranked[1].Score : 0;
            bool confident =
                bestScore >= 0.72
                || bestScore - secondScore >= 0.12
                || (bestScore >= LocalMatchMinScore && ranked.Count == 1);

            if (!confident)
            {
                continue;
            }

            AssignCatalogMatch( product, best );
            usedKeys.Add( CatalogKey( best.ShopifyProductId, best.ShopifyVariantId ) );
        }
    }

    private static void AssignCatalogMatch(
        VatReportExpenseInvoiceExtractProduct product,
        InvoiceCatalogMatchCandidate matched )
    {
        product.ShopifyProductId = matched.ShopifyProductId;
        product.ShopifyVariantId = matched.ShopifyVariantId;
        product.CatalogProductName = matched.ProductName;
        if (product.UnitGrossPrice is null or <= 0 && matched.SupplierPrice > 0)
        {
            product.UnitGrossPrice = RoundMoney( matched.SupplierPrice );
        }

        if (product.VatRatePercent is null or <= 0)
        {
            product.VatRatePercent = matched.VatRatePercent;
        }
    }

    private static string CleanInvoiceProductTitle( string? title )
    {
        string value = (title ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        value = InvoiceSkuPrefixRegex.Replace( value, string.Empty ).Trim();
        // Fallback: leading CODE-HEX blob without requiring trailing space quality.
        if (value.Length > 12
            && value.Contains( '-', StringComparison.Ordinal )
            && char.IsLetter( value[0] ))
        {
            int space = value.IndexOf( ' ' );
            if (space > 4 && space <= 24)
            {
                string prefix = value[..space];
                if (Regex.IsMatch( prefix, @"^[A-Za-z]{2,6}-[A-Fa-f0-9]{6,}$" ))
                {
                    value = value[(space + 1)..].Trim();
                }
            }
        }

        return value;
    }

    private static List<InvoiceCatalogMatchCandidate> SelectCatalogShortlist(
        IReadOnlyList<VatReportExpenseInvoiceExtractProduct> products,
        IReadOnlyList<InvoiceCatalogMatchCandidate> catalog )
    {
        Dictionary<string, InvoiceCatalogMatchCandidate> selected = new( StringComparer.OrdinalIgnoreCase );

        foreach (VatReportExpenseInvoiceExtractProduct product in products)
        {
            string barcode = new string( (product.Barcode ?? string.Empty).Where( char.IsDigit ).ToArray() );
            string titleNorm = NormalizeMatchText( product.Title );
            string titleLatin = ToLatinFold( titleNorm );
            string titleCyr = ToCyrillicFold( titleNorm );

            List<(InvoiceCatalogMatchCandidate Candidate, double Score)> ranked = new();
            foreach (InvoiceCatalogMatchCandidate candidate in catalog)
            {
                double score = ScoreCatalogCandidate( product.Title, barcode, titleNorm, titleLatin, titleCyr, candidate );
                if (score <= 0)
                {
                    continue;
                }

                ranked.Add( (candidate, score) );
            }

            foreach ((InvoiceCatalogMatchCandidate candidate, double _) in ranked
                .OrderByDescending( x => x.Score )
                .Take( TopCandidatesPerLine ))
            {
                string key = CatalogKey( candidate.ShopifyProductId, candidate.ShopifyVariantId );
                selected[key] = candidate;
                if (selected.Count >= MaxCatalogCandidates)
                {
                    return selected.Values.OrderBy( c => c.ProductName, StringComparer.OrdinalIgnoreCase ).ToList();
                }
            }
        }

        if (selected.Count == 0)
        {
            // Absolute fallback: first N catalog rows so Groq still has something.
            return catalog
                .Take( Math.Min( MaxCatalogCandidates, 40 ) )
                .ToList();
        }

        return selected.Values
            .OrderBy( c => c.ProductName, StringComparer.OrdinalIgnoreCase )
            .Take( MaxCatalogCandidates )
            .ToList();
    }

    private static double ScoreCatalogCandidate(
        string invoiceTitle,
        string barcode,
        string titleNorm,
        string titleLatin,
        string titleCyr,
        InvoiceCatalogMatchCandidate candidate )
    {
        string cleanInvoiceTitle = CleanInvoiceProductTitle( invoiceTitle );
        if (string.IsNullOrWhiteSpace( titleNorm ))
        {
            titleNorm = NormalizeMatchText( cleanInvoiceTitle );
            titleLatin = ToLatinFold( titleNorm );
            titleCyr = ToCyrillicFold( titleNorm );
        }

        string nameNorm = NormalizeMatchText( candidate.ProductName );
        if (string.IsNullOrWhiteSpace( nameNorm ))
        {
            return 0;
        }

        string nameDigits = new string( nameNorm.Where( char.IsDigit ).ToArray() );
        if (barcode.Length >= 8
            && nameDigits.Length >= 8
            && (nameDigits.Contains( barcode, StringComparison.Ordinal )
                || barcode.Contains( nameDigits, StringComparison.Ordinal )))
        {
            return 100;
        }

        string nameLatin = ToLatinFold( nameNorm );
        string nameCyr = ToCyrillicFold( nameNorm );

        double best = 0;
        best = Math.Max( best, TokenOverlapScore( titleNorm, nameNorm ) );
        best = Math.Max( best, TokenOverlapScore( titleLatin, nameLatin ) );
        best = Math.Max( best, TokenOverlapScore( titleCyr, nameCyr ) );
        best = Math.Max( best, TokenOverlapScore( titleLatin, nameNorm ) );
        best = Math.Max( best, TokenOverlapScore( titleNorm, nameLatin ) );

        // Prefer containment of the cleaned human title (without SKU).
        if (titleNorm.Length >= 8 && nameNorm.Contains( titleNorm, StringComparison.Ordinal ))
        {
            best = Math.Max( best, 0.96 );
        }
        else if (nameNorm.Length >= 8 && titleNorm.Contains( nameNorm, StringComparison.Ordinal ))
        {
            best = Math.Max( best, 0.94 );
        }

        if (cleanInvoiceTitle.Length >= 8
            && candidate.ProductName.Contains( cleanInvoiceTitle, StringComparison.OrdinalIgnoreCase ))
        {
            best = Math.Max( best, 0.93 );
        }

        return best;
    }

    private static double TokenOverlapScore( string a, string b )
    {
        if (string.IsNullOrWhiteSpace( a ) || string.IsNullOrWhiteSpace( b ))
        {
            return 0;
        }

        if (a == b)
        {
            return 1;
        }

        if (a.Contains( b, StringComparison.Ordinal ) || b.Contains( a, StringComparison.Ordinal ))
        {
            return 0.95;
        }

        string[] aTokens = a.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
            .Where( t => t.Length > 2 )
            .ToArray();
        HashSet<string> bTokens = b.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
            .Where( t => t.Length > 2 )
            .ToHashSet( StringComparer.Ordinal );
        if (aTokens.Length == 0 || bTokens.Count == 0)
        {
            return 0;
        }

        int hits = aTokens.Count( t => bTokens.Contains( t ) );
        return (double)hits / aTokens.Length;
    }

    private static string NormalizeMatchText( string value )
    {
        string lower = value.ToLowerInvariant().Normalize( NormalizationForm.FormD );
        StringBuilder sb = new( lower.Length );
        foreach (char ch in lower)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory( ch );
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit( ch ))
            {
                sb.Append( ch );
            }
            else
            {
                sb.Append( ' ' );
            }
        }

        return string.Join(
            ' ',
            sb.ToString().Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) );
    }

    private static string ToLatinFold( string value )
    {
        // Rough Belarusian/Russian phonetic fold for catalog prefilter.
        return value
            .Replace( "щ", "shch", StringComparison.Ordinal )
            .Replace( "ш", "sh", StringComparison.Ordinal )
            .Replace( "ч", "ch", StringComparison.Ordinal )
            .Replace( "ж", "zh", StringComparison.Ordinal )
            .Replace( "ю", "yu", StringComparison.Ordinal )
            .Replace( "я", "ya", StringComparison.Ordinal )
            .Replace( "ё", "yo", StringComparison.Ordinal )
            .Replace( "й", "i", StringComparison.Ordinal )
            .Replace( "х", "h", StringComparison.Ordinal )
            .Replace( "ц", "ts", StringComparison.Ordinal )
            .Replace( "ў", "u", StringComparison.Ordinal )
            .Replace( "і", "i", StringComparison.Ordinal )
            .Replace( "ї", "i", StringComparison.Ordinal )
            .Replace( "є", "ye", StringComparison.Ordinal )
            .Replace( "а", "a", StringComparison.Ordinal )
            .Replace( "б", "b", StringComparison.Ordinal )
            .Replace( "в", "v", StringComparison.Ordinal )
            .Replace( "г", "h", StringComparison.Ordinal )
            .Replace( "д", "d", StringComparison.Ordinal )
            .Replace( "е", "e", StringComparison.Ordinal )
            .Replace( "з", "z", StringComparison.Ordinal )
            .Replace( "и", "i", StringComparison.Ordinal )
            .Replace( "к", "k", StringComparison.Ordinal )
            .Replace( "л", "l", StringComparison.Ordinal )
            .Replace( "м", "m", StringComparison.Ordinal )
            .Replace( "н", "n", StringComparison.Ordinal )
            .Replace( "о", "o", StringComparison.Ordinal )
            .Replace( "п", "p", StringComparison.Ordinal )
            .Replace( "р", "r", StringComparison.Ordinal )
            .Replace( "с", "s", StringComparison.Ordinal )
            .Replace( "т", "t", StringComparison.Ordinal )
            .Replace( "у", "u", StringComparison.Ordinal )
            .Replace( "ф", "f", StringComparison.Ordinal )
            .Replace( "ы", "y", StringComparison.Ordinal )
            .Replace( "э", "e", StringComparison.Ordinal )
            .Replace( "ь", "", StringComparison.Ordinal )
            .Replace( "ъ", "", StringComparison.Ordinal );
    }

    private static string ToCyrillicFold( string value )
    {
        // Inverse rough fold for Latin invoice titles → Cyrillic-ish tokens.
        return value
            .Replace( "shch", "щ", StringComparison.Ordinal )
            .Replace( "sch", "щ", StringComparison.Ordinal )
            .Replace( "zh", "ж", StringComparison.Ordinal )
            .Replace( "ch", "ч", StringComparison.Ordinal )
            .Replace( "sh", "ш", StringComparison.Ordinal )
            .Replace( "yu", "ю", StringComparison.Ordinal )
            .Replace( "ya", "я", StringComparison.Ordinal )
            .Replace( "yo", "ё", StringComparison.Ordinal )
            .Replace( "ts", "ц", StringComparison.Ordinal )
            .Replace( "kh", "х", StringComparison.Ordinal );
    }

    private static string Truncate( string? value, int maxChars )
    {
        string text = (value ?? string.Empty).Trim();
        if (text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars];
    }

    private static VatReportExpenseInvoiceExtractResult NormalizeHeader(
        LlmInvoiceFields fields,
        IReadOnlyList<string> expenseTypeNames )
    {
        decimal? gross = RoundMoney( fields.GrossAmount );
        decimal? vat = RoundMoney( fields.VatAmount );
        decimal? net = RoundMoney( fields.NetAmount );

        List<string> warnings = new();
        if (gross is decimal g && vat is decimal v && net is decimal n)
        {
            decimal expectedNet = Math.Round( g - v, 2, MidpointRounding.AwayFromZero );
            if (Math.Abs( expectedNet - n ) > 0.05m)
            {
                warnings.Add( $"Сумы могуць быць недакладныя (брута {g} − VAT {v} ≠ нета {n}). Праверце." );
            }
        }
        else if (gross is decimal g2 && vat is decimal v2 && net is null)
        {
            net = Math.Round( g2 - v2, 2, MidpointRounding.AwayFromZero );
        }
        else if (gross is decimal g3 && net is decimal n3 && vat is null)
        {
            vat = Math.Round( g3 - n3, 2, MidpointRounding.AwayFromZero );
        }
        else if (net is decimal n4 && vat is decimal v4 && gross is null)
        {
            gross = Math.Round( n4 + v4, 2, MidpointRounding.AwayFromZero );
        }

        string? suggestedType = fields.SuggestedExpenseTypeName?.Trim();
        if (!string.IsNullOrWhiteSpace( suggestedType )
            && !expenseTypeNames.Any( t =>
                string.Equals( t, suggestedType, StringComparison.OrdinalIgnoreCase ) ))
        {
            suggestedType = null;
        }

        List<VatReportExpenseInvoiceExtractProduct> products = new();
        if (fields.Products is { Count: > 0 })
        {
            foreach (LlmInvoiceProductLine line in fields.Products)
            {
                string? title = NullIfWhite( line.Title );
                if (title is null)
                {
                    continue;
                }

                title = CleanInvoiceProductTitle( title );
                if (string.IsNullOrWhiteSpace( title ))
                {
                    continue;
                }

                int qty = line.Quantity is > 0 ? (int)Math.Round( line.Quantity.Value ) : 0;
                if (qty <= 0)
                {
                    continue;
                }

                string? barcode = NullIfWhite( line.Barcode );
                if (barcode is not null)
                {
                    barcode = new string( barcode.Where( char.IsDigit ).ToArray() );
                    if (barcode.Length == 0)
                    {
                        barcode = null;
                    }
                }

                products.Add( new VatReportExpenseInvoiceExtractProduct
                {
                    Title = title,
                    Barcode = barcode,
                    Quantity = qty,
                    UnitGrossPrice = RoundMoney( line.UnitGrossPrice ),
                    VatRatePercent = RoundMoney( line.VatRatePercent )
                } );
            }
        }

        if (products.Count > 0
            && string.IsNullOrWhiteSpace( suggestedType )
            && expenseTypeNames.Any( t =>
                string.Equals( t, "Аплата пастаўшчыку", StringComparison.OrdinalIgnoreCase ) ))
        {
            suggestedType = "Аплата пастаўшчыку";
        }

        return new VatReportExpenseInvoiceExtractResult
        {
            InvoiceNumber = NullIfWhite( fields.InvoiceNumber ),
            ExpenseDateUtc = NormalizeDate( fields.ExpenseDate ),
            GrossAmount = gross,
            VatAmount = vat,
            NetAmount = net,
            VendorName = NullIfWhite( fields.VendorName ),
            Comment = NullIfWhite( fields.Comment ),
            SuggestedExpenseTypeName = suggestedType,
            Products = products,
            Warning = warnings.Count == 0 ? null : string.Join( " ", warnings )
        };
    }

    private static void ApplyMatches(
        VatReportExpenseInvoiceExtractResult result,
        IReadOnlyList<InvoiceCatalogMatchCandidate> catalog,
        LlmMatchResponse matchResponse,
        bool onlyFillEmpty = false )
    {
        Dictionary<string, InvoiceCatalogMatchCandidate> byKey = catalog
            .GroupBy( c => CatalogKey( c.ShopifyProductId, c.ShopifyVariantId ), StringComparer.OrdinalIgnoreCase )
            .ToDictionary( g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase );

        Dictionary<string, InvoiceCatalogMatchCandidate> byProductOnly = catalog
            .GroupBy( c => c.ShopifyProductId.Trim(), StringComparer.OrdinalIgnoreCase )
            .Where( g => g.Count() == 1 )
            .ToDictionary( g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase );

        Dictionary<int, LlmMatchItem> byIndex = (matchResponse.Matches ?? new List<LlmMatchItem>())
            .Where( m => m.I >= 0 )
            .GroupBy( m => m.I )
            .ToDictionary( g => g.Key, g => g.First() );

        for (int i = 0; i < result.Products.Count; i++)
        {
            VatReportExpenseInvoiceExtractProduct product = result.Products[i];
            if (onlyFillEmpty && !string.IsNullOrWhiteSpace( product.ShopifyProductId ))
            {
                continue;
            }

            if (!byIndex.TryGetValue( i, out LlmMatchItem? match ))
            {
                continue;
            }

            string? productId = NullIfWhite( match.ShopifyProductId );
            string variantId = NullIfWhite( match.ShopifyVariantId ) ?? string.Empty;
            InvoiceCatalogMatchCandidate? matched = null;
            if (productId is not null)
            {
                string key = CatalogKey( productId, variantId );
                if (byKey.TryGetValue( key, out InvoiceCatalogMatchCandidate? exact ))
                {
                    matched = exact;
                }
                else if (byProductOnly.TryGetValue( productId, out InvoiceCatalogMatchCandidate? single ))
                {
                    matched = single;
                }
                else
                {
                    matched = catalog.FirstOrDefault( c =>
                        string.Equals( c.ShopifyProductId, productId, StringComparison.OrdinalIgnoreCase ) );
                }
            }

            if (matched is null)
            {
                continue;
            }

            AssignCatalogMatch( product, matched );
        }
    }

    private static void FinalizeMatchWarnings( VatReportExpenseInvoiceExtractResult result )
    {
        List<string> warnings = new();
        if (!string.IsNullOrWhiteSpace( result.Warning ))
        {
            warnings.Add( result.Warning );
        }

        int matchedCount = result.Products.Count( p => !string.IsNullOrWhiteSpace( p.ShopifyProductId ) );
        if (matchedCount > 0)
        {
            warnings.Add( $"Знойдзена тавараў: {matchedCount}/{result.Products.Count}." );
        }

        List<string> unmatched = result.Products
            .Where( p => string.IsNullOrWhiteSpace( p.ShopifyProductId ) )
            .Select( p => p.Title )
            .Where( t => !string.IsNullOrWhiteSpace( t ) )
            .ToList();
        if (unmatched.Count > 0)
        {
            warnings.Add(
                $"Не знойдзена ў каталогу: {string.Join( "; ", unmatched.Take( 5 ) )}"
                + (unmatched.Count > 5 ? "…" : string.Empty) );
        }

        result.Warning = warnings.Count == 0 ? null : string.Join( " ", warnings );
    }

    private static void AppendWarning( VatReportExpenseInvoiceExtractResult result, string warning )
    {
        if (string.IsNullOrWhiteSpace( warning ))
        {
            return;
        }

        result.Warning = string.IsNullOrWhiteSpace( result.Warning )
            ? warning
            : $"{result.Warning} {warning}";
    }

    private static string BuildCatalogBlock(
        IReadOnlyList<InvoiceCatalogMatchCandidate> catalog,
        int maxChars )
    {
        // Compact TSV: id|variant|name|vat|price — much smaller than JSON.
        StringBuilder sb = new();
        sb.AppendLine( "id|v|n|vat|p" );
        foreach (InvoiceCatalogMatchCandidate c in catalog.Take( MaxCatalogCandidates ))
        {
            string name = Truncate( c.ProductName, MaxProductNameChars )
                .Replace( '|', '/' )
                .Replace( '\n', ' ' )
                .Replace( '\r', ' ' );
            string line =
                $"{c.ShopifyProductId}|{c.ShopifyVariantId}|{name}|{c.VatRatePercent.ToString( CultureInfo.InvariantCulture )}|{c.SupplierPrice.ToString( CultureInfo.InvariantCulture )}";
            if (sb.Length + line.Length + 1 > maxChars)
            {
                break;
            }

            sb.AppendLine( line );
        }

        return sb.ToString();
    }

    private async Task<T> CallGroqAsync<T>(
        string apiKey,
        string model,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken )
    {
        // gpt-oss often fails Groq json_object validation and burns tokens on reasoning.
        bool isGptOss = model.Contains( "gpt-oss", StringComparison.OrdinalIgnoreCase );

        string? content = await TryCompleteAsync(
            apiKey,
            BuildChatPayload( model, systemPrompt, userPrompt, useJsonObjectMode: !isGptOss ),
            cancellationToken );

        if (string.IsNullOrWhiteSpace( content ) && !isGptOss)
        {
            _logger.LogWarning( "Groq JSON mode failed; retrying without response_format." );
            content = await TryCompleteAsync(
                apiKey,
                BuildChatPayload( model, systemPrompt, userPrompt, useJsonObjectMode: false ),
                cancellationToken );
        }

        if (string.IsNullOrWhiteSpace( content ))
        {
            throw new InvalidOperationException( "Groq вярнуў пусты адказ." );
        }

        string json = ExtractJsonObject( content );
        T? parsed = JsonSerializer.Deserialize<T>( json, JsonOptions );
        if (parsed is null)
        {
            throw new InvalidOperationException( "Не ўдалося разабраць JSON ад Groq." );
        }

        return parsed;
    }

    private async Task<string?> TryCompleteAsync(
        string apiKey,
        object payload,
        CancellationToken cancellationToken )
    {
        try
        {
            string body = await SendGroqChatAsync( apiKey, payload, cancellationToken );
            return TryGetMessageContent( body, out string? content ) ? content : null;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains( "json_validate_failed", StringComparison.Ordinal )
                                                   || ex.Message.Contains( "Failed to validate JSON", StringComparison.Ordinal ))
        {
            _logger.LogWarning( ex, "Groq JSON validation failed." );
            return null;
        }
    }

    private static object BuildChatPayload(
        string model,
        string systemPrompt,
        string userPrompt,
        bool useJsonObjectMode )
    {
        bool isGptOss = model.Contains( "gpt-oss", StringComparison.OrdinalIgnoreCase );
        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt }
        };

        if (isGptOss)
        {
            // Low reasoning + enough completion tokens so the visible JSON is not empty.
            return new
            {
                model,
                temperature = 0,
                max_completion_tokens = 8192,
                reasoning_effort = "low",
                messages
            };
        }

        if (useJsonObjectMode)
        {
            return new
            {
                model,
                temperature = 0,
                max_completion_tokens = 4096,
                response_format = new { type = "json_object" },
                messages
            };
        }

        return new
        {
            model,
            temperature = 0,
            max_completion_tokens = 4096,
            messages
        };
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
            // Some Groq errors put usable JSON in failed_generation.
            if (TryRecoverFailedGeneration( body, out string recovered ))
            {
                _logger.LogWarning(
                    "Groq HTTP {Status}; recovered failed_generation JSON.",
                    (int)response.StatusCode );
                return WrapAsFakeCompletion( recovered );
            }

            _logger.LogWarning( "Groq extract failed: {Status} {Body}", (int)response.StatusCode, body );
            throw new InvalidOperationException(
                $"Groq API памылка: {(int)response.StatusCode}. {TrimBody( body )}" );
        }

        return body;
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

    private static bool TryRecoverFailedGeneration( string errorBody, out string recovered )
    {
        recovered = string.Empty;
        try
        {
            using JsonDocument doc = JsonDocument.Parse( errorBody );
            if (!doc.RootElement.TryGetProperty( "error", out JsonElement error ))
            {
                return false;
            }

            if (!error.TryGetProperty( "failed_generation", out JsonElement failed )
                || failed.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? raw = failed.GetString();
            if (string.IsNullOrWhiteSpace( raw ))
            {
                return false;
            }

            recovered = ExtractJsonObject( raw );
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string WrapAsFakeCompletion( string content ) =>
        JsonSerializer.Serialize( new
        {
            choices = new[]
            {
                new { message = new { content } }
            }
        } );

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

    private string RequireApiKey()
    {
        string apiKey = (_config["Groq:ApiKey"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( apiKey ))
        {
            throw new InvalidOperationException(
                "Groq API key не наладжаны (Groq:ApiKey / GROQ_API_KEY)." );
        }

        return apiKey;
    }

    private string ResolveModel()
    {
        string model = (_config["Groq:Model"] ?? "openai/gpt-oss-20b").Trim();
        return string.IsNullOrWhiteSpace( model ) ? "openai/gpt-oss-20b" : model;
    }

    private static string CatalogKey( string productId, string? variantId ) =>
        $"{productId.Trim()}::{(variantId ?? string.Empty).Trim()}";

    private static string? NormalizeDate( string? raw )
    {
        raw = NullIfWhite( raw );
        if (raw is null)
        {
            return null;
        }

        if (DateTime.TryParseExact(
                raw,
                ["yyyy-MM-dd", "yyyy-M-d", "dd.MM.yyyy", "dd/MM/yyyy", "yyyy.MM.dd"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsed ))
        {
            return parsed.ToString( "yyyy-MM-dd", CultureInfo.InvariantCulture );
        }

        if (DateTime.TryParse( raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed ))
        {
            return parsed.ToString( "yyyy-MM-dd", CultureInfo.InvariantCulture );
        }

        return null;
    }

    private static decimal? RoundMoney( decimal? value ) =>
        value is null
            ? null
            : Math.Round( value.Value, 2, MidpointRounding.AwayFromZero );

    private static string? NullIfWhite( string? value ) =>
        string.IsNullOrWhiteSpace( value ) ? null : value.Trim();

    private static string TrimBody( string body ) =>
        body.Length <= 400 ? body : body[..400] + "…";

    private sealed class LlmInvoiceFields
    {
        public string? InvoiceNumber { get; set; }
        public string? ExpenseDate { get; set; }
        public decimal? GrossAmount { get; set; }
        public decimal? VatAmount { get; set; }
        public decimal? NetAmount { get; set; }
        public string? VendorName { get; set; }
        public string? Comment { get; set; }
        public string? SuggestedExpenseTypeName { get; set; }
        public List<LlmInvoiceProductLine>? Products { get; set; }
    }

    private sealed class LlmInvoiceProductLine
    {
        public string? Title { get; set; }
        public string? Barcode { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? UnitGrossPrice { get; set; }
        public decimal? VatRatePercent { get; set; }
    }

    private sealed class LlmMatchResponse
    {
        public List<LlmMatchItem>? Matches { get; set; }
    }

    private sealed class LlmMatchItem
    {
        public int I { get; set; }
        public string? ShopifyProductId { get; set; }
        public string? ShopifyVariantId { get; set; }
        public string? CatalogProductName { get; set; }
    }
}
