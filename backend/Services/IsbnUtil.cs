using System.Text.RegularExpressions;

namespace backend.Services;

public static class IsbnUtil
{
    private static readonly Regex DigitsRegex = new( @"[^0-9Xx]", RegexOptions.Compiled );

    public static string? Normalize( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string cleaned = DigitsRegex.Replace( raw.Trim(), string.Empty ).ToUpperInvariant();
        if (cleaned.Length is not (10 or 13))
        {
            // Sometimes OCR keeps "ISBN" digits mixed — try last 13/10 digit run
            Match m13 = Regex.Match( raw, @"\b97[89][\d\- ]{10,16}\b" );
            if (m13.Success)
            {
                cleaned = DigitsRegex.Replace( m13.Value, string.Empty );
            }
            else
            {
                Match m10 = Regex.Match( raw, @"\b[\dXx][\d\- ]{8,14}[\dXx]\b" );
                if (m10.Success)
                {
                    cleaned = DigitsRegex.Replace( m10.Value, string.Empty ).ToUpperInvariant();
                }
            }
        }

        if (cleaned.Length == 10 || cleaned.Length == 13)
        {
            return IsValid( cleaned ) ? cleaned : null;
        }

        return null;
    }

    public static bool IsValid( string isbn )
    {
        if (isbn.Length == 10)
        {
            int sum = 0;
            for (int i = 0; i < 10; i++)
            {
                char c = isbn[i];
                int v = c is 'X' or 'x' ? 10 : c - '0';
                if (v < 0 || v > 10 || (v == 10 && i != 9))
                {
                    return false;
                }

                sum += v * (10 - i);
            }

            return sum % 11 == 0;
        }

        if (isbn.Length == 13)
        {
            if (!isbn.All( char.IsDigit ))
            {
                return false;
            }

            int sum = 0;
            for (int i = 0; i < 13; i++)
            {
                int d = isbn[i] - '0';
                sum += d * (i % 2 == 0 ? 1 : 3);
            }

            return sum % 10 == 0;
        }

        return false;
    }

    public static string FormatForDisplay( string? isbn ) =>
        string.IsNullOrWhiteSpace( isbn ) ? string.Empty : isbn.Trim();
}
