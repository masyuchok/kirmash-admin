using System.Text.RegularExpressions;

namespace backend.Services;

public static class BookProductCoverType
{
    public const string Soft = "soft";
    public const string Hard = "hard";

    public static string? Normalize( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string t = raw.Trim().ToLowerInvariant();
        t = Regex.Replace( t, @"\s+", " " );

        if (Regex.IsMatch( t, @"мягк|мякк|soft|paperback|miękk|miekk|broszur" ))
        {
            return Soft;
        }

        if (Regex.IsMatch( t, @"твёрд|тверд|цвёрд|цверд|hard|hardcover|hardback|tward" ))
        {
            return Hard;
        }

        return null;
    }

    public static bool IsCoverAttributeLabel( string taxonomy, string attrName )
    {
        return taxonomy.Contains( "cover", StringComparison.OrdinalIgnoreCase )
            || taxonomy.Contains( "binding", StringComparison.OrdinalIgnoreCase )
            || taxonomy.Contains( "opraw", StringComparison.OrdinalIgnoreCase )
            || taxonomy.Contains( "voklad", StringComparison.OrdinalIgnoreCase )
            || attrName.Contains( "воклад", StringComparison.OrdinalIgnoreCase )
            || attrName.Contains( "облож", StringComparison.OrdinalIgnoreCase )
            || attrName.Contains( "opraw", StringComparison.OrdinalIgnoreCase )
            || attrName.Contains( "binding", StringComparison.OrdinalIgnoreCase )
            || attrName.Contains( "cover", StringComparison.OrdinalIgnoreCase )
            || attrName.Contains( "пераплёт", StringComparison.OrdinalIgnoreCase )
            || attrName.Contains( "перепл", StringComparison.OrdinalIgnoreCase );
    }
}
