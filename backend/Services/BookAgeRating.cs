using System.Text.RegularExpressions;

namespace backend.Services;

/// <summary>
/// Normalizes age recommendation labels found on covers, shop pages, and price lists
/// into canonical forms like "0+", "6+", "12+", "16+", "18+".
/// </summary>
public static class BookAgeRating
{
    private static readonly Regex ExplicitPlus = new(
        @"\b(\d{1,2})\s*\+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant );

    private static readonly Regex FromAgePhrase = new(
        @"(?:для\s+(?:детей|дзяцей|детей)|od\s+lat|от|ад|from|wiek|возр[аa]ст|ўзрост|возраст)\s*:?\s*(\d{1,2})\s*\+?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );

    private static readonly Regex YearsPhrase = new(
        @"\b(\d{1,2})\s*(?:лет|года|год|рок[іi]ў?|lat|years?|yrs?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );

    public static string? Normalize( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string t = raw.Trim();
        t = Regex.Replace( t, @"\s+", " " );

        if (Regex.IsMatch(
                t,
                @"только\s+для\s+взрослых|толькі\s+для\s+дарослых|adults?\s+only|dla\s+dorosłych|18\s*years?\s*and\s*(?:over|older)",
                RegexOptions.IgnoreCase ))
        {
            return "18+";
        }

        Match plus = ExplicitPlus.Match( t );
        if (plus.Success && int.TryParse( plus.Groups[1].Value, out int plusAge ))
        {
            return Canonical( plusAge );
        }

        Match fromAge = FromAgePhrase.Match( t );
        if (fromAge.Success && int.TryParse( fromAge.Groups[1].Value, out int fromN ))
        {
            return Canonical( fromN );
        }

        Match years = YearsPhrase.Match( t );
        if (years.Success && int.TryParse( years.Groups[1].Value, out int yearsN ))
        {
            // Alone "3 года" in a title is too ambiguous; only accept when context suggests rating.
            if (LooksLikeAgeContext( t ))
            {
                return Canonical( yearsN );
            }
        }

        return null;
    }

    public static bool IsAgeAttributeLabel( string taxonomy, string attrName )
    {
        string tax = taxonomy ?? string.Empty;
        string name = attrName ?? string.Empty;
        return tax.Contains( "age", StringComparison.OrdinalIgnoreCase )
            || tax.Contains( "wiek", StringComparison.OrdinalIgnoreCase )
            || tax.Contains( "vozrast", StringComparison.OrdinalIgnoreCase )
            || tax.Contains( "uzrost", StringComparison.OrdinalIgnoreCase )
            || name.Contains( "возраст", StringComparison.OrdinalIgnoreCase )
            || name.Contains( "ўзрост", StringComparison.OrdinalIgnoreCase )
            || name.Contains( "узрост", StringComparison.OrdinalIgnoreCase )
            || name.Contains( "wiek", StringComparison.OrdinalIgnoreCase )
            || name.Contains( "age", StringComparison.OrdinalIgnoreCase )
            || name.Contains( "рекоменд", StringComparison.OrdinalIgnoreCase )
            || name.Contains( "рэкаменд", StringComparison.OrdinalIgnoreCase )
            || name.Contains( "для кого", StringComparison.OrdinalIgnoreCase )
            || name.Contains( "для каго", StringComparison.OrdinalIgnoreCase );
    }

    private static bool LooksLikeAgeContext( string t )
    {
        return Regex.IsMatch(
            t,
            @"возраст|ўзрост|узрост|wiek|age\b|рекоменд|рэкаменд|для\s+(?:детей|дзяцей|детей)|od\s+lat|от\s+\d|ад\s+\d",
            RegexOptions.IgnoreCase );
    }

    private static string? Canonical( int age )
    {
        if (age < 0 || age > 21)
        {
            return null;
        }

        // Common bookstore buckets; keep exact number if uncommon (e.g. 3+, 10+).
        return $"{age}+";
    }
}
