using System.Text;

namespace backend.Services;

/// <summary>
/// Rough Belarusian Łacinka → Cyrillic for Kamunikat-style product slugs.
/// Not a full orthography converter — enough to search and prefill forms.
/// </summary>
internal static class BelarusianLacinka
{
    public static string SlugToPhrase( string slug )
    {
        if (string.IsNullOrWhiteSpace( slug ))
        {
            return string.Empty;
        }

        string[] parts = slug.Split( '-', StringSplitOptions.RemoveEmptyEntries );
        return string.Join( " ", parts.Select( WordToCyrillic ) );
    }

    public static string WordToCyrillic( string latinWord )
    {
        if (string.IsNullOrWhiteSpace( latinWord ))
        {
            return string.Empty;
        }

        string w = latinWord.Trim().ToLowerInvariant();
        var sb = new StringBuilder( w.Length );
        for (int i = 0; i < w.Length; )
        {
            if (i + 1 < w.Length)
            {
                string dig = w.Substring( i, 2 );
                string? mapped = dig switch
                {
                    "ch" => "х",
                    "cz" => "ч",
                    "sz" => "ш",
                    "io" => "ё",
                    "ja" => "я",
                    "je" => "е",
                    "ju" => "ю",
                    "ia" => "я",
                    "ie" => "е",
                    "iu" => "ю",
                    "dz" => "дз",
                    _ => null,
                };
                if (mapped is not null)
                {
                    sb.Append( mapped );
                    i += 2;
                    continue;
                }
            }

            sb.Append( MapChar( w[i] ) );
            i++;
        }

        return sb.ToString();
    }

    private static string MapChar( char c ) => c switch
    {
        'a' => "а",
        'b' => "б",
        'c' => "ц",
        'ć' => "ць",
        'č' => "ч",
        'd' => "д",
        'e' => "е",
        'f' => "ф",
        'g' => "г",
        'h' => "г",
        'i' => "і",
        'j' => "й",
        'k' => "к",
        'l' => "л",
        'ł' => "л",
        'm' => "м",
        'n' => "н",
        'o' => "о",
        'p' => "п",
        'r' => "р",
        's' => "с",
        'ś' => "сь",
        'š' => "ш",
        't' => "т",
        'u' => "у",
        'ŭ' => "ў",
        'v' => "в",
        'y' => "ы",
        'z' => "з",
        'ź' => "зь",
        'ž' => "ж",
        _ => c.ToString(),
    };
}
