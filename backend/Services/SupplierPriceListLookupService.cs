using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// Looks up supplier unit cost (PLN brutto) from the supplier's price-list URL
/// (typically a Google Sheet exported as CSV).
/// </summary>
public sealed class SupplierPriceListLookupService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SupplierPriceListLookupService> _logger;

    public SupplierPriceListLookupService(
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger<SupplierPriceListLookupService> logger )
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<PriceListMatch?> LookupFromPriceListAsync(
        int supplierId,
        string? title,
        string? isbn,
        string? author,
        CancellationToken cancellationToken )
    {
        Supplier? supplier = await _db.Suppliers
            .AsNoTracking()
            .FirstOrDefaultAsync( s => s.Id == supplierId, cancellationToken );
        string? priceListUrl = supplier?.PriceListUrl?.Trim();
        if (string.IsNullOrWhiteSpace( priceListUrl ))
        {
            return null;
        }

        string? csvUrl = TryBuildCsvExportUrl( priceListUrl );
        if (string.IsNullOrWhiteSpace( csvUrl ))
        {
            _logger.LogWarning( "Cannot derive CSV URL from price list {Url}", priceListUrl );
            return null;
        }

        string csv;
        try
        {
            HttpClient client = _httpClientFactory.CreateClient( "BookLookupPage" );
            using HttpResponseMessage response = await client.GetAsync( csvUrl, cancellationToken );
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Price list download failed {Status} for supplier {SupplierId}",
                    (int)response.StatusCode,
                    supplierId );
                return null;
            }

            csv = await response.Content.ReadAsStringAsync( cancellationToken );
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "Price list download error for supplier {SupplierId}", supplierId );
            return null;
        }

        if (string.IsNullOrWhiteSpace( csv ) || csv.Length < 8)
        {
            return null;
        }

        // Google often returns HTML login wall for private sheets.
        if (csv.TrimStart().StartsWith( "<", StringComparison.Ordinal )
            || csv.Contains( "accounts.google.com", StringComparison.OrdinalIgnoreCase ))
        {
            _logger.LogWarning(
                "Price list for supplier {SupplierId} is not publicly readable CSV",
                supplierId );
            return null;
        }

        return FindMatchInCsv( csv, title, isbn, author );
    }

    public sealed record PriceListMatch(
        decimal? UnitCostBrutto,
        decimal? WeightKg,
        string? CoverType,
        string? RawRowText = null,
        string? AgeRating = null,
        string? Format = null,
        string? Illustrator = null,
        string? Language = null,
        int? PageCount = null,
        string? PlaceOfPublication = null,
        string? Translation = null,
        int? Year = null );

    public async Task<decimal?> LookupUnitCostBruttoAsync(
        int supplierId,
        string? title,
        string? isbn,
        string? author,
        CancellationToken cancellationToken )
    {
        PriceListMatch? match = await LookupFromPriceListAsync(
            supplierId,
            title,
            isbn,
            author,
            cancellationToken );
        return match?.UnitCostBrutto;
    }

    internal static string? TryBuildCsvExportUrl( string rawUrl )
    {
        if (!Uri.TryCreate( rawUrl.Trim(), UriKind.Absolute, out Uri? uri )
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        string host = uri.Host.ToLowerInvariant();
        string path = uri.AbsolutePath;

        // Already a CSV / pub export.
        if (path.EndsWith( ".csv", StringComparison.OrdinalIgnoreCase )
            || uri.Query.Contains( "output=csv", StringComparison.OrdinalIgnoreCase )
            || uri.Query.Contains( "format=csv", StringComparison.OrdinalIgnoreCase ))
        {
            return uri.ToString();
        }

        // Published sheet: /spreadsheets/d/e/{id}/pub
        if (path.Contains( "/pub", StringComparison.OrdinalIgnoreCase ))
        {
            string sep = string.IsNullOrEmpty( uri.Query ) ? "?" : "&";
            if (!uri.Query.Contains( "output=", StringComparison.OrdinalIgnoreCase ))
            {
                return uri.GetLeftPart( UriPartial.Path ) + uri.Query + sep + "output=csv";
            }

            return uri.ToString();
        }

        // Standard Google Sheet: /spreadsheets/d/{id}/edit#gid=0
        Match idMatch = Regex.Match( path, @"/spreadsheets/d/([a-zA-Z0-9-_]+)" );
        if (idMatch.Success && host.Contains( "google", StringComparison.Ordinal ))
        {
            string sheetId = idMatch.Groups[1].Value;
            string gid = "0";
            Match gidMatch = Regex.Match( uri.Fragment + uri.Query, @"gid=(\d+)" );
            if (gidMatch.Success)
            {
                gid = gidMatch.Groups[1].Value;
            }

            return $"https://docs.google.com/spreadsheets/d/{sheetId}/export?format=csv&gid={gid}";
        }

        return uri.ToString();
    }

    internal static PriceListMatch? FindMatchInCsv(
        string csv,
        string? title,
        string? isbn,
        string? author )
    {
        List<string[]> rows = ParseCsv( csv );
        if (rows.Count < 2)
        {
            return null;
        }

        string[] header = rows[0];
        int isbnCol = FindColumnIndex( header, "isbn", "іsbn", "штрих", "barcode", "ean" );
        int titleCol = FindColumnIndex(
            header,
            "назва",
            "назван",
            "nazwa",
            "title",
            "tytuł",
            "tytul",
            "book",
            "кніга",
            "книга",
            "pozycja",
            "name" );
        // Prefer the plain title column over "назва ў краме / Shopify" variants.
        int plainTitleCol = FindColumnIndexExactish( header, "назва", "nazwa", "title", "name" );
        if (plainTitleCol >= 0)
        {
            titleCol = plainTitleCol;
        }

        int authorCol = FindColumnIndex(
            header,
            "аўтар", "автор", "author", "autor", "аўтары" );
        int priceCol = FindColumnIndex(
            header,
            "брутто", "brutto", "брута", "цана", "цена", "cena", "price", "цэн", "цен", "zł", "zl", "pln", "koszt", "netto" );
        int weightCol = FindColumnIndex(
            header,
            "вага", "вес", "weight", "waga", "кг", "gram", "грам", "masa" );
        int coverCol = FindColumnIndex(
            header,
            "воклад", "облож", "opraw", "cover", "binding", "мякк", "мягк", "цвёрд", "тверд", "miękk", "tward" );
        int ageCol = FindColumnIndex(
            header,
            "век", "возраст", "ўзрост", "узрост", "wiek", "age", "рекоменд", "рэкаменд", "od lat", "для кого", "для каго" );
        int formatCol = FindColumnIndex(
            header, "фармат", "формат", "format", "размер", "памер", "size", "wymiar" );
        int illustratorCol = FindColumnIndex(
            header, "ілюстрат", "иллюстрат", "illustrat", "ilustrat", "мастак", "художник" );
        int languageCol = FindColumnIndex(
            header, "мова", "язык", "language", "język", "jezyk", "lang" );
        int pagesCol = FindColumnIndex(
            header, "старон", "страниц", "stron", "pages", "page", "колькасць" );
        int placeCol = FindColumnIndex(
            header, "месца", "место", "miejsce", "place", "город", "горад", "city" );
        int translationCol = FindColumnIndex(
            header, "пераклад", "перевод", "przekład", "przeklad", "tłumacz", "tlumacz", "transl" );
        int yearCol = FindColumnIndex(
            header, "год", "rok", "year", "выдан", "издан", "wydan" );

        // Prefer Kirma / brutto purchase-price columns when several price-like headers exist.
        int kirmaPriceCol = FindColumnIndex( header, "kirma" );
        int bruttoCol = FindColumnIndex( header, "брутто", "brutto", "брута", "gross" );
        if (kirmaPriceCol >= 0 && LooksLikePriceHeader( header[kirmaPriceCol] ))
        {
            priceCol = kirmaPriceCol;
        }
        else if (bruttoCol >= 0)
        {
            priceCol = bruttoCol;
        }

        if (priceCol < 0)
        {
            for (int c = header.Length - 1; c >= 0; c--)
            {
                if (c == weightCol)
                {
                    continue;
                }

                if (ParseMoney( header[c] ) is null
                    && rows.Skip( 1 ).Take( 8 ).Any( r => r.Length > c && ParseMoney( r[c] ) is not null ))
                {
                    priceCol = c;
                    break;
                }
            }
        }

        if (priceCol < 0 && weightCol < 0 && coverCol < 0 && ageCol < 0
            && formatCol < 0 && illustratorCol < 0 && languageCol < 0
            && pagesCol < 0 && placeCol < 0 && translationCol < 0 && yearCol < 0)
        {
            return null;
        }

        string? wantIsbn = IsbnUtil.Normalize( isbn );
        string wantTitle = NormalizeText( StripAuthorLabelFromTitle( title ) );
        string wantAuthor = NormalizeText( author );

        decimal? bestPrice = null;
        decimal? bestWeight = null;
        string? bestCover = null;
        string? bestRawRow = null;
        string? bestAge = null;
        string? bestFormat = null;
        string? bestIllustrator = null;
        string? bestLanguage = null;
        int? bestPages = null;
        string? bestPlace = null;
        string? bestTranslation = null;
        int? bestYear = null;
        int bestScore = 0;

        for (int i = 1; i < rows.Count; i++)
        {
            string[] row = rows[i];
            string rowJoined = string.Join( " ", row );

            decimal? price = priceCol >= 0 && priceCol < row.Length
                ? ParseMoney( row[priceCol] )
                : null;
            if (price is <= 0m or > 10_000m)
            {
                price = null;
            }

            decimal? weight = weightCol >= 0 && weightCol < row.Length
                ? ParseWeightKg( row[weightCol] )
                : null;

            string? cover = null;
            if (coverCol >= 0 && coverCol < row.Length)
            {
                cover = BookProductCoverType.Normalize( row[coverCol] );
            }

            cover ??= BookProductCoverType.Normalize( rowJoined );

            string? age = ageCol >= 0 && ageCol < row.Length
                ? BookAgeRating.Normalize( row[ageCol] )
                : null;
            age ??= BookAgeRating.Normalize( rowJoined );

            string? format = formatCol >= 0 && formatCol < row.Length
                ? BookBibliographicFields.NormalizeFormat( row[formatCol] )
                : null;
            format ??= BookBibliographicFields.NormalizeFormat( rowJoined );

            string? illustrator = illustratorCol >= 0 && illustratorCol < row.Length
                ? BookBibliographicFields.NormalizeIllustrator( row[illustratorCol] )
                : null;
            illustrator ??= BookBibliographicFields.ExtractIllustratorFromText( rowJoined );

            string? language = languageCol >= 0 && languageCol < row.Length
                ? BookBibliographicFields.NormalizeLanguage( row[languageCol] )
                : null;
            language ??= BookBibliographicFields.ResolveLanguage( null, rowJoined );

            int? pages = pagesCol >= 0 && pagesCol < row.Length
                ? BookBibliographicFields.NormalizePageCount( row[pagesCol] )
                : null;
            pages ??= BookBibliographicFields.NormalizePageCount( rowJoined );

            string? place = placeCol >= 0 && placeCol < row.Length
                ? BookBibliographicFields.NormalizePlace( row[placeCol] )
                    ?? (row[placeCol].Trim().Length is > 1 and <= 80 ? row[placeCol].Trim() : null)
                : null;
            place ??= BookBibliographicFields.NormalizePlace( rowJoined );

            string? translation = translationCol >= 0 && translationCol < row.Length
                ? BookBibliographicFields.NormalizeTranslation( row[translationCol] )
                    ?? (row[translationCol].Trim().Length is > 1 and <= 80 ? row[translationCol].Trim() : null)
                : null;
            translation ??= BookBibliographicFields.NormalizeTranslation( rowJoined );

            int? year = yearCol >= 0 && yearCol < row.Length
                ? BookBibliographicFields.NormalizeYear( row[yearCol] )
                : null;
            year ??= BookBibliographicFields.NormalizeYear( rowJoined );

            if (price is null && weight is null && cover is null && age is null
                && format is null && illustrator is null && language is null
                && pages is null && place is null && translation is null && year is null)
            {
                continue;
            }

            int score = 0;
            if (!string.IsNullOrWhiteSpace( wantIsbn ) && isbnCol >= 0 && isbnCol < row.Length)
            {
                string? rowIsbn = IsbnUtil.Normalize( row[isbnCol] );
                if (!string.IsNullOrWhiteSpace( rowIsbn ) && rowIsbn == wantIsbn)
                {
                    score += 100;
                }
            }

            string rowTitle = titleCol >= 0 && titleCol < row.Length
                ? NormalizeText( row[titleCol] )
                : string.Empty;
            string rowAuthor = authorCol >= 0 && authorCol < row.Length
                ? NormalizeText( row[authorCol] )
                : string.Empty;

            string rowBlob = NormalizeText( rowJoined );

            // Price lists often put "Title, Author" in one cell — strip author for title match.
            string rowTitleCore = StripTrailingAuthorFromTitle( rowTitle, wantAuthor );
            if (string.IsNullOrWhiteSpace( rowTitleCore ))
            {
                rowTitleCore = rowTitle;
            }

            if (!string.IsNullOrWhiteSpace( wantTitle ))
            {
                if (!string.IsNullOrWhiteSpace( rowTitleCore )
                    && TitlesSoftMatch( wantTitle, rowTitleCore ))
                {
                    score += 40;
                }
                else if (TitlesSoftMatch( wantTitle, rowBlob ))
                {
                    score += 30;
                }
                else
                {
                    string titleHaystack = string.IsNullOrWhiteSpace( rowTitleCore )
                        ? rowBlob
                        : rowTitleCore;
                    int overlap = CountTokenOverlap( wantTitle, titleHaystack );
                    int wantTokens = CountSignificantTokens( wantTitle );
                    // Short titles like "Siva zozula" (2 tokens): full token hit is enough.
                    if (wantTokens > 0 && overlap >= wantTokens)
                    {
                        score += 40;
                    }
                    else if (overlap >= 3)
                    {
                        score += 30 + overlap;
                    }
                    else if (overlap >= 2)
                    {
                        score += 15 + overlap;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace( wantAuthor ))
            {
                if (AuthorsSoftMatch( wantAuthor, rowAuthor )
                    || AuthorsSoftMatch( wantAuthor, rowBlob )
                    || AuthorsSoftMatch( wantAuthor, rowTitle ))
                {
                    score += 15;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestPrice = price;
                bestWeight = weight;
                bestCover = cover;
                bestAge = age;
                bestFormat = format;
                bestIllustrator = illustrator;
                bestLanguage = language;
                bestPages = pages;
                bestPlace = place;
                bestTranslation = translation;
                bestYear = year;
                bestRawRow = string.Join(
                    " | ",
                    row.Select( cell => (cell ?? string.Empty).Trim() )
                        .Where( cell => !string.IsNullOrWhiteSpace( cell ) ) );
            }
        }

        if (bestScore < 20)
        {
            return null;
        }

        if (bestPrice is null && bestWeight is null && bestCover is null && bestAge is null
            && bestFormat is null && bestIllustrator is null && bestLanguage is null
            && bestPages is null && bestPlace is null && bestTranslation is null && bestYear is null)
        {
            return null;
        }

        return new PriceListMatch(
            bestPrice,
            bestWeight,
            bestCover,
            bestRawRow,
            bestAge,
            bestFormat,
            bestIllustrator,
            bestLanguage,
            bestPages,
            bestPlace,
            bestTranslation,
            bestYear );
    }

    private static decimal? ParseWeightKg( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string t = raw.Trim().ToLowerInvariant()
            .Replace( ',', '.' )
            .Replace( '\u00a0', ' ' );

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
            || (t.Contains( 'г' ) && !t.Contains( "кг", StringComparison.Ordinal ) && !t.Contains( "kg" ));
        bool kilograms = Regex.IsMatch( t, @"\b(kg|кг)\b" );

        if (grams && !kilograms)
        {
            value /= 1000m;
        }
        else if (!kilograms && !grams)
        {
            // Bare numbers: treat as grams if clearly book-sized (>20 would be huge in kg).
            if (value > 20m)
            {
                value /= 1000m;
            }
            // Values like 0.35 stay as kg; 350 → grams already handled; 350 without unit → grams.
            else if (value >= 50m)
            {
                value /= 1000m;
            }
        }

        if (value <= 0m || value > 50m)
        {
            return null;
        }

        return Math.Round( value, 3, MidpointRounding.AwayFromZero );
    }

    private static int CountTokenOverlap( string a, string b )
    {
        HashSet<string> ta = a.Split( ' ', StringSplitOptions.RemoveEmptyEntries )
            .Where( t => t.Length >= 3 )
            .ToHashSet( StringComparer.Ordinal );
        if (ta.Count == 0)
        {
            return 0;
        }

        return b.Split( ' ', StringSplitOptions.RemoveEmptyEntries )
            .Count( t => t.Length >= 3 && ta.Contains( t ) );
    }

    private static int CountSignificantTokens( string text ) =>
        text.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).Count( t => t.Length >= 3 );

    private static bool TitlesSoftMatch( string wantTitle, string haystack )
    {
        if (string.IsNullOrWhiteSpace( wantTitle ) || string.IsNullOrWhiteSpace( haystack ))
        {
            return false;
        }

        if (haystack.Contains( wantTitle, StringComparison.Ordinal )
            || wantTitle.Contains( haystack, StringComparison.Ordinal ))
        {
            return true;
        }

        int wantTokens = CountSignificantTokens( wantTitle );
        if (wantTokens == 0)
        {
            return false;
        }

        return CountTokenOverlap( wantTitle, haystack ) >= wantTokens;
    }

    /// <summary>
    /// "«Siva zozula». Автор: Виктор Стахвюк" → "Siva zozula" for price-list matching.
    /// </summary>
    private static string? StripAuthorLabelFromTitle( string? title )
    {
        if (string.IsNullOrWhiteSpace( title ))
        {
            return title;
        }

        Match m = Regex.Match(
            title.Trim(),
            @"^(?<t>.+?)\s*[.…]?\s*(?:Автор(?:ы)?|Аўтар(?:ы)?|Author(?:s)?)\s*[:：]\s*.+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );
        if (m.Success)
        {
            return m.Groups["t"].Value.Trim().Trim( '«', '»', '"', '\'', '“', '”', '„' );
        }

        return title;
    }

    private static bool LooksLikePriceHeader( string header )
    {
        string h = (header ?? string.Empty).Trim().ToLowerInvariant();
        return h.Contains( "цэн", StringComparison.Ordinal )
            || h.Contains( "цен", StringComparison.Ordinal )
            || h.Contains( "цана", StringComparison.Ordinal )
            || h.Contains( "цена", StringComparison.Ordinal )
            || h.Contains( "cena", StringComparison.Ordinal )
            || h.Contains( "price", StringComparison.Ordinal )
            || h.Contains( "pln", StringComparison.Ordinal )
            || h.Contains( "zł", StringComparison.Ordinal )
            || h.Contains( "brutt", StringComparison.Ordinal )
            || h.Contains( "брут", StringComparison.Ordinal )
            || h.Contains( "koszt", StringComparison.Ordinal )
            || h.Contains( "netto", StringComparison.Ordinal );
    }

    /// <summary>
    /// Soft author match: exact, reversed order, or ≥2 shared tokens / shared surname-like token.
    /// </summary>
    private static bool AuthorsSoftMatch( string wantAuthor, string haystack )
    {
        if (string.IsNullOrWhiteSpace( wantAuthor ) || string.IsNullOrWhiteSpace( haystack ))
        {
            return false;
        }

        if (haystack.Contains( wantAuthor, StringComparison.Ordinal ))
        {
            return true;
        }

        string[] wantParts = wantAuthor.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
        if (wantParts.Length >= 2)
        {
            string reversed = string.Join( ' ', wantParts.Reverse() );
            if (haystack.Contains( reversed, StringComparison.Ordinal ))
            {
                return true;
            }
        }

        HashSet<string> wantTokens = ExpandAuthorTokens( wantAuthor );
        if (wantTokens.Count == 0)
        {
            return false;
        }

        HashSet<string> hayTokens = ExpandAuthorTokens( haystack );
        int hits = wantTokens.Count( t => hayTokens.Contains( t ) );
        if (hits >= 2)
        {
            return true;
        }

        // Single distinctive surname token (often hyphenated) is enough.
        return wantTokens.Any(
            t => t.Contains( '-', StringComparison.Ordinal ) && hayTokens.Contains( t ) );
    }

    private static HashSet<string> ExpandAuthorTokens( string text )
    {
        HashSet<string> result = new( StringComparer.Ordinal );
        foreach (string part in text.Split( ' ', StringSplitOptions.RemoveEmptyEntries ))
        {
            if (part.Length < 3)
            {
                continue;
            }

            result.Add( part );
            if (!part.Contains( '-', StringComparison.Ordinal ))
            {
                continue;
            }

            foreach (string sub in part.Split( '-', StringSplitOptions.RemoveEmptyEntries ))
            {
                if (sub.Length >= 3)
                {
                    result.Add( sub );
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Removes trailing ", Author Name" from price-list title cells.
    /// </summary>
    private static string StripTrailingAuthorFromTitle( string rowTitle, string wantAuthor )
    {
        if (string.IsNullOrWhiteSpace( rowTitle ))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace( wantAuthor ))
        {
            string[] wantParts = wantAuthor.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
            string[] variants =
            [
                wantAuthor,
                wantParts.Length >= 2 ? string.Join( ' ', wantParts.Reverse() ) : wantAuthor,
            ];
            foreach (string variant in variants.Distinct( StringComparer.Ordinal ))
            {
                if (string.IsNullOrWhiteSpace( variant ) || variant.Length < 4)
                {
                    continue;
                }

                int idx = rowTitle.LastIndexOf( variant, StringComparison.Ordinal );
                if (idx <= 0)
                {
                    continue;
                }

                string before = rowTitle[..idx].Trim().TrimEnd( ',', ';', '.', '—', '–', ' ' );
                if (before.Length >= 6)
                {
                    return before;
                }
            }
        }

        // Generic: take text before the last comma if it looks like "Title, Author".
        int comma = rowTitle.LastIndexOf( ',' );
        if (comma >= 6 && comma < rowTitle.Length - 4)
        {
            string before = rowTitle[..comma].Trim();
            string after = rowTitle[(comma + 1)..].Trim();
            if (before.Length >= 6
                && after.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).Length is >= 1 and <= 4)
            {
                return before;
            }
        }

        return rowTitle;
    }

    private static string NormalizeText( string? value )
    {
        if (string.IsNullOrWhiteSpace( value ))
        {
            return string.Empty;
        }

        string t = value.Trim().ToLowerInvariant();
        t = t.Replace( 'ё', 'е' ).Replace( 'ў', 'у' ).Replace( 'і', 'i' );
        t = Regex.Replace( t, @"[«»""„‟''′]", "" );
        t = Regex.Replace( t, @"[.,;:!?()\[\]{}|/\\+=*#@&%]+", " " );
        t = Regex.Replace( t, @"\s+", " " );
        return t.Trim();
    }

    private static int FindColumnIndex( string[] header, params string[] needles )
    {
        for (int i = 0; i < header.Length; i++)
        {
            string h = header[i].Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace( h ))
            {
                continue;
            }

            foreach (string needle in needles)
            {
                if (h.Contains( needle, StringComparison.OrdinalIgnoreCase ))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Prefer a header that is essentially just the label (e.g. "Назва"),
    /// not a longer variant like "назва ў Kirma / Shopify".
    /// </summary>
    private static int FindColumnIndexExactish( string[] header, params string[] labels )
    {
        int best = -1;
        int bestLen = int.MaxValue;
        for (int i = 0; i < header.Length; i++)
        {
            string h = Regex.Replace( header[i].Trim().ToLowerInvariant(), @"\s+", " " );
            if (string.IsNullOrWhiteSpace( h ))
            {
                continue;
            }

            foreach (string label in labels)
            {
                if (h.Equals( label, StringComparison.OrdinalIgnoreCase )
                    || h.StartsWith( label + " ", StringComparison.OrdinalIgnoreCase )
                    || h.StartsWith( label + "(", StringComparison.OrdinalIgnoreCase ))
                {
                    // Skip "назва ў …" / "title in shop" style columns.
                    if (Regex.IsMatch(
                            h,
                            @"\b(у|ў|в|in|shop|shopify|kirma|крам|магаз)\b",
                            RegexOptions.IgnoreCase )
                        && !h.Equals( label, StringComparison.OrdinalIgnoreCase ))
                    {
                        continue;
                    }

                    if (h.Length < bestLen)
                    {
                        bestLen = h.Length;
                        best = i;
                    }
                }
            }
        }

        return best;
    }

    private static decimal? ParseMoney( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string t = raw.Trim()
            .Replace( "\u00a0", "" )
            .Replace( " ", "" )
            .Replace( "zł", "", StringComparison.OrdinalIgnoreCase )
            .Replace( "zl", "", StringComparison.OrdinalIgnoreCase )
            .Replace( "pln", "", StringComparison.OrdinalIgnoreCase )
            .Trim();

        // 12,50 or 12.50 — if both separators, assume EU: 1.234,56
        if (t.Contains( ',' ) && t.Contains( '.' ))
        {
            if (t.LastIndexOf( ',' ) > t.LastIndexOf( '.' ))
            {
                t = t.Replace( ".", "" ).Replace( ',', '.' );
            }
            else
            {
                t = t.Replace( ",", "" );
            }
        }
        else if (t.Contains( ',' ))
        {
            t = t.Replace( ',', '.' );
        }

        Match m = Regex.Match( t, @"-?\d+(?:\.\d+)?" );
        if (!m.Success
            || !decimal.TryParse(
                m.Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal value ))
        {
            return null;
        }

        return Math.Round( value, 2, MidpointRounding.AwayFromZero );
    }

    private static List<string[]> ParseCsv( string csv )
    {
        string all = csv.Replace( "\r\n", "\n" ).Replace( '\r', '\n' );
        string firstLine = all.Split( '\n' ).FirstOrDefault( l => !string.IsNullOrWhiteSpace( l ) )
            ?? string.Empty;
        char delimiter = DetectDelimiter( firstLine );
        return ParseCsvWithDelimiter( all, delimiter );
    }

    private static char DetectDelimiter( string firstLine )
    {
        int commas = CountUnquoted( firstLine, ',' );
        int semis = CountUnquoted( firstLine, ';' );
        int tabs = CountUnquoted( firstLine, '\t' );
        if (tabs >= commas && tabs >= semis && tabs > 0) return '\t';
        if (semis >= commas && semis > 0) return ';';
        return ',';
    }

    private static int CountUnquoted( string line, char ch )
    {
        int count = 0;
        bool inQuotes = false;
        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && c == ch)
            {
                count++;
            }
        }

        return count;
    }

    private static List<string[]> ParseCsvWithDelimiter( string all, char delimiter )
    {
        List<string[]> rows = new();
        List<string> current = new();
        StringBuilder cell = new();
        bool inQuotes = false;

        for (int i = 0; i < all.Length; i++)
        {
            char c = all[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < all.Length && all[i + 1] == '"')
                    {
                        cell.Append( '"' );
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append( c );
                }

                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                continue;
            }

            if (c == delimiter)
            {
                current.Add( cell.ToString() );
                cell.Clear();
                continue;
            }

            if (c == '\n')
            {
                current.Add( cell.ToString() );
                cell.Clear();
                if (current.Any( x => !string.IsNullOrWhiteSpace( x ) ))
                {
                    rows.Add( current.ToArray() );
                }

                current = new List<string>();
                continue;
            }

            cell.Append( c );
        }

        current.Add( cell.ToString() );
        if (current.Any( x => !string.IsNullOrWhiteSpace( x ) ))
        {
            rows.Add( current.ToArray() );
        }

        return rows;
    }
}
