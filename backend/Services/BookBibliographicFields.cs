using System.Globalization;
using System.Text.RegularExpressions;

namespace backend.Services;

/// <summary>
/// Parses bibliographic metadata from shop attributes, descriptions, and price-list cells.
/// </summary>
public static class BookBibliographicFields
{
    private static readonly Regex FormatSize = new(
        @"\b(\d{2,3})\s*[x×хXХ]\s*(\d{2,3})\s*(?:mm|мм|cm|см)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );

    private static readonly Regex FormatLetter = new(
        @"\b([AB][0-9]|[AB][0-9]/[0-9])\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );

    private static readonly Regex PageCount = new(
        @"\b(\d{2,4})\s*(?:с\.|стр\.?|старонак|страниц|stron(?:y|a)?|pages?|szt\.?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );

    private static readonly Regex YearExplicit = new(
        @"(?:год(?:\s+выдання)?|rok(?:\s+wydania)?|year(?:\s+of\s+publication)?|wydanie|издание|выданне)\s*:?\s*(19|20)\d{2}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );

    private static readonly Regex YearBare = new(
        @"\b((?:19|20)\d{2})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant );

    private static readonly Regex TranslationPhrase = new(
        @"(?:пераклад|перевод|przekład|tłumaczenie|translated?\s+from|translation\s+from)\s*(?:з|с|z|from)?\s*([^\n,;.|]{2,40})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );

    private static readonly Regex IllustratorLabeled = new(
        @"(?:ілюстратар(?:ы)?|иллюстратор(?:ы)?|illustrator(?:s)?|ilustrator(?:zy)?|ілюстрацыі|иллюстрации|ilustracje|мастак|художник)\s*:?\s*(.+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );

    public static string? NormalizeFormat( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string t = Collapse( raw );
        Match size = FormatSize.Match( t );
        if (size.Success)
        {
            return $"{size.Groups[1].Value}×{size.Groups[2].Value}";
        }

        Match letter = FormatLetter.Match( t );
        if (letter.Success && LooksLikeFormatContext( t ))
        {
            return letter.Groups[1].Value.ToUpperInvariant();
        }

        // Attribute cell that is only a size-like token.
        if (t.Length <= 24 && FormatSize.IsMatch( t ))
        {
            Match m = FormatSize.Match( t );
            return $"{m.Groups[1].Value}×{m.Groups[2].Value}";
        }

        return null;
    }

    /// <summary>
    /// For dedicated illustrator attribute/column cells: keep only person-like names.
    /// </summary>
    public static string? NormalizeIllustrator( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string t = Collapse( raw );
        // If free text contains a label, extract only the labeled value.
        Match labeled = IllustratorLabeled.Match( t );
        if (labeled.Success)
        {
            t = labeled.Groups[1].Value;
            // Cut trailing next-field noise.
            t = Regex.Split(
                t,
                @"\s*[|;]\s*|(?=\b(?:мова|язык|год|rok|фармат|формат|ISBN|аўтар|автор)\b)",
                RegexOptions.IgnoreCase )[0];
        }

        t = Regex.Replace(
            t,
            @"^(?:ілюстратар(?:ы)?|иллюстратор(?:ы)?|illustrator(?:s)?|ilustrator(?:zy)?|ілюстрацыі|иллюстрации|ilustracje|мастак|художник)\s*:?\s*",
            "",
            RegexOptions.IgnoreCase );
        t = t.Trim().Trim( ',', ';', '.', ':', '|' );
        return LooksLikePersonName( t ) ? t : null;
    }

    /// <summary>
    /// Only extracts illustrator when the text explicitly labels one — never from bare titles/rows.
    /// </summary>
    public static string? ExtractIllustratorFromText( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        if (!IllustratorLabeled.IsMatch( raw ))
        {
            return null;
        }

        return NormalizeIllustrator( raw );
    }

    private static bool LooksLikePersonName( string t )
    {
        if (string.IsNullOrWhiteSpace( t ))
        {
            return false;
        }

        t = Collapse( t );
        if (t.Length is < 2 or > 60)
        {
            return false;
        }

        // Copyright / title / catalog junk.
        if (Regex.IsMatch(
                t,
                @"©|copyright|копирайт|аўтарск|авторск|зборнік|сборник|collection|эсэ|эссе|essay|туры|ISBN|\b\d{2,}\b",
                RegexOptions.IgnoreCase ))
        {
            return false;
        }

        // Multi-sentence titles like "…. Зборнік …"
        if (Regex.IsMatch( t, @"\.\s+\S" ))
        {
            return false;
        }

        if (BookAgeRating.Normalize( t ) is not null || NormalizeFormat( t ) is not null)
        {
            return false;
        }

        if (Regex.IsMatch( t, @"^\d" ) || Regex.IsMatch( t, @"\d{3,}" ))
        {
            return false;
        }

        string[] tokens = t.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        if (tokens.Length is < 1 or > 5)
        {
            return false;
        }

        foreach (string token in tokens)
        {
            if (Regex.IsMatch( token, @"^[A-Za-zА-Яа-яЁёІіЎў'\-]+\.?$" )
                || Regex.IsMatch( token, @"^[A-Za-zА-Яа-яЁёІіЎў]\.$" ))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    public static string? NormalizeLanguage( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        // Long free text: only accept labeled phrases or short attribute cells.
        string collapsed = Collapse( raw );
        if (collapsed.Length > 60)
        {
            return ExtractExplicitLanguage( collapsed ) ?? InferLanguageFromText( collapsed );
        }

        string t = collapsed.ToLowerInvariant();
        List<string> labels = new();
        void Add( string label )
        {
            if (!labels.Contains( label, StringComparer.OrdinalIgnoreCase ))
            {
                labels.Add( label );
            }
        }

        if (Regex.IsMatch( t, @"беларус|belarus|белорус|\bbe\b" ))
        {
            Add( "беларуская" );
        }

        if (Regex.IsMatch( t, @"польск|polish|polski|\bpl\b" ))
        {
            Add( "польская" );
        }

        if (Regex.IsMatch( t, @"руск|русск|russian|rosyjski|\bru\b" ))
        {
            Add( "руская" );
        }

        if (Regex.IsMatch( t, @"англел|англий|english|angielski|\ben\b" ))
        {
            Add( "англійская" );
        }

        if (Regex.IsMatch( t, @"украін|украин|ukrain|ukraińsk|\buk\b" ))
        {
            Add( "украінская" );
        }

        if (Regex.IsMatch( t, @"літоў|литов|lithuan|litewsk|\blt\b" ))
        {
            Add( "літоўская" );
        }

        if (labels.Count > 0)
        {
            return string.Join( ", ", labels );
        }

        if (t.Length is >= 2 and <= 40
            && !Regex.IsMatch( t, @"\d" )
            && LooksLikeLanguageContext( collapsed ))
        {
            return collapsed;
        }

        return null;
    }

    /// <summary>
    /// Prefer explicit language attribute / "мова:" label; otherwise infer from description text.
    /// </summary>
    public static string? ResolveLanguage( string? attributeOrShort, params string?[] texts )
    {
        string? fromAttr = NormalizeLanguage( attributeOrShort );
        if (!string.IsNullOrWhiteSpace( fromAttr ) && (attributeOrShort?.Trim().Length ?? 0) <= 60)
        {
            return fromAttr;
        }

        foreach (string? text in texts)
        {
            string? explicitLang = ExtractExplicitLanguage( text );
            if (!string.IsNullOrWhiteSpace( explicitLang ))
            {
                return explicitLang;
            }
        }

        foreach (string? text in texts)
        {
            string? inferred = InferLanguageFromText( text );
            if (!string.IsNullOrWhiteSpace( inferred ))
            {
                return inferred;
            }
        }

        return fromAttr;
    }

    public static string? ExtractExplicitLanguage( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        Match m = Regex.Match(
            raw,
            @"(?:мова|язык|language|język|jezyk)\s*[:\-–]?\s*([^\n.;|]{2,40})",
            RegexOptions.IgnoreCase );
        if (!m.Success)
        {
            return null;
        }

        string piece = m.Groups[1].Value.Trim().Trim( ',', ';', '.', ':' );
        // Re-run short-cell normalization only (avoid recursion into long-text path).
        if (piece.Length > 60)
        {
            piece = piece[..60];
        }

        string t = piece.ToLowerInvariant();
        if (Regex.IsMatch( t, @"беларус|belarus|белорус|\bbe\b" ))
        {
            return "беларуская";
        }

        if (Regex.IsMatch( t, @"польск|polish|polski|\bpl\b" ))
        {
            return "польская";
        }

        if (Regex.IsMatch( t, @"руск|русск|russian|rosyjski|\bru\b" ))
        {
            return "руская";
        }

        if (Regex.IsMatch( t, @"англел|англий|english|angielski|\ben\b" ))
        {
            return "англійская";
        }

        if (Regex.IsMatch( t, @"украін|украин|ukrain|ukraińsk|\buk\b" ))
        {
            return "украінская";
        }

        if (Regex.IsMatch( t, @"літоў|литов|lithuan|litewsk|\blt\b" ))
        {
            return "літоўская";
        }

        return piece.Length is >= 2 and <= 40 ? piece : null;
    }

    /// <summary>
    /// Guess language from orthography of the description/body text.
    /// </summary>
    public static string? InferLanguageFromText( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string t = Collapse( raw );
        if (t.Length < 40)
        {
            return null;
        }

        int cyr = 0;
        int lat = 0;
        foreach (char c in t)
        {
            if (IsCyrillicLetter( c ))
            {
                cyr++;
            }
            else if (IsLatinLetter( c ))
            {
                lat++;
            }
        }

        if (lat > cyr * 2 && lat >= 30)
        {
            int pl = CountMatches( t, @"[ĄĆĘŁŃÓŚŹŻąćęłńóśźż]" );
            if (pl >= 2 || Regex.IsMatch( t, @"\b(się|nie|jest|oraz|który|książka)\b", RegexOptions.IgnoreCase ))
            {
                return "польская";
            }

            if (Regex.IsMatch(
                    t,
                    @"\b(the|and|with|from|this|that|book|about)\b",
                    RegexOptions.IgnoreCase ))
            {
                return "англійская";
            }

            return pl > 0 ? "польская" : "англійская";
        }

        if (cyr < 20)
        {
            return null;
        }

        int be = CountChars( t, 'ў', 'Ў' ) * 6
            + CountChars( t, 'і', 'І' )
            + CountMatches( t, @"\b(гэта|ёсць|таксама|яшчэ|каб|толькі|ўсё|ўжо)\b" ) * 3;
        int ru = CountChars( t, 'ы', 'Ы' )
            + CountChars( t, 'и', 'И' )
            + CountChars( t, 'щ', 'Щ' ) * 4
            + CountChars( t, 'ъ', 'Ъ' ) * 4
            + CountMatches( t, @"\b(это|есть|также|только|уже|чтобы)\b" ) * 3;
        int uk = CountChars( t, 'ї', 'Ї' ) * 5
            + CountChars( t, 'є', 'Є' ) * 5
            + CountChars( t, 'ґ', 'Ґ' ) * 5
            + CountMatches( t, @"\b(це|є|також|ще|щоб|тільки)\b" ) * 3;

        // ў is decisive for Belarusian.
        if (CountChars( t, 'ў', 'Ў' ) >= 1 && be >= ru && be >= uk)
        {
            return "беларуская";
        }

        if (uk >= be && uk >= ru && uk >= 3)
        {
            return "украінская";
        }

        if (be > ru && be >= 3)
        {
            return "беларуская";
        }

        if (ru >= 3)
        {
            return "руская";
        }

        if (be > 0)
        {
            return "беларуская";
        }

        return ru > 0 ? "руская" : null;
    }

    private static int CountChars( string t, params char[] chars )
    {
        int n = 0;
        foreach (char c in t)
        {
            foreach (char want in chars)
            {
                if (c == want)
                {
                    n++;
                    break;
                }
            }
        }

        return n;
    }

    private static int CountMatches( string t, string pattern ) =>
        Regex.Matches( t, pattern, RegexOptions.IgnoreCase ).Count;

    private static bool IsCyrillicLetter( char c ) =>
        c is (>= 'А' and <= 'я')
            or 'Ё' or 'ё'
            or 'І' or 'і' or 'Ў' or 'ў'
            or 'Ї' or 'ї' or 'Є' or 'є' or 'Ґ' or 'ґ';

    private static bool IsLatinLetter( char c ) =>
        c is (>= 'A' and <= 'Z')
            or (>= 'a' and <= 'z')
            or 'Ą' or 'Ć' or 'Ę' or 'Ł' or 'Ń' or 'Ó' or 'Ś' or 'Ź' or 'Ż'
            or 'ą' or 'ć' or 'ę' or 'ł' or 'ń' or 'ó' or 'ś' or 'ź' or 'ż';

    public static int? NormalizePageCount( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string t = Collapse( raw );
        Match m = PageCount.Match( t );
        if (m.Success
            && int.TryParse( m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pages )
            && pages is >= 8 and <= 5000)
        {
            return pages;
        }

        // Bare number in a dedicated "pages" attribute cell.
        if (Regex.IsMatch( t, @"^\d{2,4}$" )
            && int.TryParse( t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bare )
            && bare is >= 8 and <= 5000)
        {
            return bare;
        }

        return null;
    }

    public static string? NormalizePlace( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string t = Collapse( raw );
        t = Regex.Replace(
            t,
            @"^(?:месца\s+выдання|место\s+издания|miejsce\s+wydania|place\s+of\s+publication|город|горад)\s*:?\s*",
            "",
            RegexOptions.IgnoreCase );
        t = t.Trim().Trim( ',', ';', '.', ':' );
        if (t.Length is < 2 or > 80)
        {
            return null;
        }

        if (Regex.IsMatch( t, @"^\d" ) || BookAgeRating.Normalize( t ) is not null)
        {
            return null;
        }

        return t;
    }

    public static string? NormalizeTranslation( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string t = Collapse( raw );
        Match m = TranslationPhrase.Match( t );
        if (m.Success)
        {
            string lang = m.Groups[1].Value.Trim().Trim( ',', ';', '.', ':' );
            if (lang.Length is >= 2 and <= 40)
            {
                string? normalizedLang = NormalizeLanguage( lang ) ?? lang;
                return $"з {normalizedLang}";
            }
        }

        if (LooksLikeTranslationContext( t ) && t.Length <= 80)
        {
            return t;
        }

        return null;
    }

    public static int? NormalizeYear( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string t = Collapse( raw );
        Match labeled = YearExplicit.Match( t );
        if (labeled.Success)
        {
            Match y = YearBare.Match( labeled.Value );
            if (y.Success
                && int.TryParse( y.Groups[1].Value, out int year )
                && year is >= 1800 and <= 2100)
            {
                return year;
            }
        }

        if (Regex.IsMatch( t, @"^\d{4}$" )
            && int.TryParse( t, out int bare )
            && bare is >= 1800 and <= 2100)
        {
            return bare;
        }

        if (LooksLikeYearContext( t ))
        {
            Match y = YearBare.Match( t );
            if (y.Success
                && int.TryParse( y.Groups[1].Value, out int year )
                && year is >= 1800 and <= 2100)
            {
                return year;
            }
        }

        return null;
    }

    public static string? LanguageFromOcrCode( string? code )
    {
        string c = (code ?? string.Empty).Trim().ToLowerInvariant();
        return c switch
        {
            "be" => "беларуская",
            "pl" => "польская",
            "ru" => "руская",
            "en" => "англійская",
            "uk" => "украінская",
            "lt" => "літоўская",
            _ => NormalizeLanguage( code ),
        };
    }

    public static bool IsFormatAttributeLabel( string taxonomy, string attrName ) =>
        MatchesAny( taxonomy, attrName, "format", "фармат", "формат", "размер", "памер", "size", "wymiar" );

    public static bool IsIllustratorAttributeLabel( string taxonomy, string attrName ) =>
        MatchesAny(
            taxonomy,
            attrName,
            "illustrat",
            "ilustrat",
            "ілюстрат",
            "иллюстрат",
            "мастак",
            "художник" );

    public static bool IsLanguageAttributeLabel( string taxonomy, string attrName ) =>
        MatchesAny( taxonomy, attrName, "language", "lang", "мова", "язык", "język", "jezyk" );

    public static bool IsPageCountAttributeLabel( string taxonomy, string attrName ) =>
        MatchesAny(
            taxonomy,
            attrName,
            "page",
            "pages",
            "старон",
            "страниц",
            "stron",
            "колькасць стар",
            "количество стр" );

    public static bool IsPlaceAttributeLabel( string taxonomy, string attrName ) =>
        MatchesAny(
            taxonomy,
            attrName,
            "place",
            "city",
            "месца",
            "место",
            "miejsce",
            "город",
            "горад" );

    public static bool IsTranslationAttributeLabel( string taxonomy, string attrName ) =>
        MatchesAny(
            taxonomy,
            attrName,
            "transl",
            "пераклад",
            "перевод",
            "przekład",
            "przeklad",
            "tłumacz",
            "tlumacz" );

    public static bool IsYearAttributeLabel( string taxonomy, string attrName ) =>
        MatchesAny( taxonomy, attrName, "year", "год", "rok", "выдан", "издан", "wydan" );

    private static bool MatchesAny( string taxonomy, string attrName, params string[] needles )
    {
        string tax = taxonomy ?? string.Empty;
        string name = attrName ?? string.Empty;
        foreach (string needle in needles)
        {
            if (tax.Contains( needle, StringComparison.OrdinalIgnoreCase )
                || name.Contains( needle, StringComparison.OrdinalIgnoreCase ))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeFormatContext( string t ) =>
        Regex.IsMatch( t, @"фармат|формат|format|размер|памер|mm|мм|wymiar", RegexOptions.IgnoreCase );

    private static bool LooksLikeLanguageContext( string t ) =>
        Regex.IsMatch( t, @"мова|язык|language|język|jezyk", RegexOptions.IgnoreCase );

    private static bool LooksLikeTranslationContext( string t ) =>
        Regex.IsMatch( t, @"пераклад|перевод|przekład|tłumaczen|translat", RegexOptions.IgnoreCase );

    private static bool LooksLikeYearContext( string t ) =>
        Regex.IsMatch( t, @"год|rok|year|выдан|издан|wydan", RegexOptions.IgnoreCase );

    private static string Collapse( string raw )
    {
        string t = raw.Trim();
        t = Regex.Replace( t, @"\s+", " " );
        return t;
    }
}
