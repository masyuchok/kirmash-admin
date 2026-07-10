using System.Text.RegularExpressions;
using backend.Models;
using backend.Services.Shopify;

namespace backend.Services;

internal static class VatReportHelpers
{
    public static string BuildProductLineKey( string shopifyProductId, string? shopifyVariantId )
    {
        string productId = ShopifyIds.NormalizeProductId( shopifyProductId.Trim() );
        string variantId = string.IsNullOrWhiteSpace( shopifyVariantId )
            ? string.Empty
            : ShopifyIds.NormalizeVariantId( shopifyVariantId.Trim() );
        return string.IsNullOrEmpty( variantId ) ? productId : $"{productId}::{variantId}";
    }

    public static bool ProductLineKeysEqual(
        string productIdA,
        string? variantIdA,
        string productIdB,
        string? variantIdB ) =>
        string.Equals(
            BuildProductLineKey( productIdA, variantIdA ),
            BuildProductLineKey( productIdB, variantIdB ),
            StringComparison.OrdinalIgnoreCase );

    public static bool ProductLinesCompatible(
        string productIdA,
        string? variantIdA,
        string productIdB,
        string? variantIdB )
    {
        string productA = ShopifyIds.NormalizeProductId( productIdA.Trim() );
        string productB = ShopifyIds.NormalizeProductId( productIdB.Trim() );
        if (string.IsNullOrWhiteSpace( productA ) || string.IsNullOrWhiteSpace( productB ))
        {
            return false;
        }

        if (!string.Equals( productA, productB, StringComparison.OrdinalIgnoreCase ))
        {
            return false;
        }

        if (ProductLineKeysEqual( productIdA, variantIdA, productIdB, variantIdB ))
        {
            return true;
        }

        string variantA = string.IsNullOrWhiteSpace( variantIdA )
            ? string.Empty
            : ShopifyIds.NormalizeVariantId( variantIdA.Trim() );
        string variantB = string.IsNullOrWhiteSpace( variantIdB )
            ? string.Empty
            : ShopifyIds.NormalizeVariantId( variantIdB.Trim() );
        return string.IsNullOrEmpty( variantA ) || string.IsNullOrEmpty( variantB );
    }

    public static string ExtractVariantTitleFromProductLineTitle( string? productTitle )
    {
        if (string.IsNullOrWhiteSpace( productTitle ))
        {
            return string.Empty;
        }

        string title = productTitle.Trim();
        foreach (string separator in new[] { " — ", " – ", " - " })
        {
            int index = title.LastIndexOf( separator, StringComparison.Ordinal );
            if (index < 0)
            {
                continue;
            }

            string variantTitle = title[(index + separator.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace( variantTitle ))
            {
                return variantTitle;
            }
        }

        foreach (string separator in new[] { " · ", " ·", "· " })
        {
            int index = title.LastIndexOf( separator, StringComparison.Ordinal );
            if (index < 0)
            {
                continue;
            }

            string variantTitle = title[(index + separator.Length)..].Trim();
            if (LooksLikePinVariantSuffix( variantTitle ))
            {
                return variantTitle;
            }
        }

        int commaIndex = title.LastIndexOf( ',' );
        if (commaIndex > 0 && commaIndex < title.Length - 1 &&
            title.Count( ch => ch == ',' ) == 1)
        {
            string variantTitle = title[(commaIndex + 1)..].Trim();
            if (LooksLikeCommaSeparatedVariantSuffix( variantTitle ))
            {
                return variantTitle;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Books and other products may appear under different Shopify product IDs over time.
    /// Match titles loosely: exact, prefix, or the segment before the first comma.
    /// </summary>
    public static string NormalizeProductTitleForMatch( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return string.Empty;
        }

        string title = raw.Trim()
            .Replace( '«', '"' )
            .Replace( '»', '"' )
            .Replace( '„', '"' )
            .Replace( '“', '"' )
            .Replace( '”', '"' );
        return string.Join(
            ' ',
            title.Split( (char[]?)null, StringSplitOptions.RemoveEmptyEntries ) );
    }

    private static readonly Regex IsbnDigitRunRegex = new( @"\d{10}|\d{13}", RegexOptions.Compiled );

    /// <summary>Normalize ISBN/barcode to digits-only (10 or 13 chars) for comparison.</summary>
    public static string NormalizeIsbn( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return string.Empty;
        }

        string trimmed = raw.Trim();
        List<char> digits = new( trimmed.Length );
        foreach (char ch in trimmed)
        {
            if (char.IsDigit( ch ))
            {
                digits.Add( ch );
            }
            else if (digits.Count == 9 && (ch == 'X' || ch == 'x'))
            {
                digits.Add( 'X' );
            }
        }

        if (digits.Count is not (10 or 13))
        {
            return string.Empty;
        }

        return new string( digits.ToArray() );
    }

    public static bool IsbnsMatch( string? leftRaw, string? rightRaw )
    {
        string left = NormalizeIsbn( leftRaw );
        string right = NormalizeIsbn( rightRaw );
        if (string.IsNullOrWhiteSpace( left ) || string.IsNullOrWhiteSpace( right ))
        {
            return false;
        }

        return string.Equals( left, right, StringComparison.OrdinalIgnoreCase );
    }

    public static string? ExtractIsbnFromText( string? text )
    {
        if (string.IsNullOrWhiteSpace( text ))
        {
            return null;
        }

        string direct = NormalizeIsbn( text );
        if (!string.IsNullOrWhiteSpace( direct ))
        {
            return direct;
        }

        foreach (Match match in IsbnDigitRunRegex.Matches( text ))
        {
            string candidate = NormalizeIsbn( match.Value );
            if (!string.IsNullOrWhiteSpace( candidate ))
            {
                return candidate;
            }
        }

        return null;
    }

    public static string ExtractProductTitleSearchToken( string? productTitle )
    {
        string normalized = NormalizeProductTitleForMatch( productTitle );
        if (string.IsNullOrWhiteSpace( normalized ))
        {
            return string.Empty;
        }

        int commaIndex = normalized.IndexOf( ',' );
        string primary = commaIndex > 0 ? normalized[..commaIndex].Trim() : normalized;
        if (primary.Length >= 6)
        {
            return primary;
        }

        return normalized.Length >= 6 ? normalized : string.Empty;
    }

    public static bool ProductTitlesMatch( string? leftRaw, string? rightRaw )
    {
        string left = NormalizeProductTitleForMatch( leftRaw );
        string right = NormalizeProductTitleForMatch( rightRaw );
        if (string.IsNullOrWhiteSpace( left ) || string.IsNullOrWhiteSpace( right ))
        {
            return false;
        }

        if (string.Equals( left, right, StringComparison.OrdinalIgnoreCase ))
        {
            return true;
        }

        if (TitlePrefixMatchesAtBoundary( left, right ) || TitlePrefixMatchesAtBoundary( right, left ))
        {
            return true;
        }

        string leftPrimary = PrimaryTitleSegment( left );
        string rightPrimary = PrimaryTitleSegment( right );
        if (leftPrimary.Length >= 6 &&
            string.Equals( leftPrimary, rightPrimary, StringComparison.OrdinalIgnoreCase ))
        {
            return true;
        }

        return TitlePrefixMatchesAtBoundary( leftPrimary, rightPrimary ) ||
               TitlePrefixMatchesAtBoundary( rightPrimary, leftPrimary );
    }

    /// <summary>
    /// Prefix match only when the shorter title ends at a title boundary (end or comma),
    /// so "Book A" does not match "Book A part two".
    /// </summary>
    private static bool TitlePrefixMatchesAtBoundary( string shorter, string longer )
    {
        if (shorter.Length < 8 || longer.Length < shorter.Length)
        {
            return false;
        }

        if (!longer.StartsWith( shorter, StringComparison.OrdinalIgnoreCase ))
        {
            return false;
        }

        return longer.Length == shorter.Length || longer[shorter.Length] == ',';
    }

    private static string PrimaryTitleSegment( string title )
    {
        int commaIndex = title.IndexOf( ',' );
        return commaIndex > 0 ? title[..commaIndex].Trim() : title;
    }

    /// <summary>
    /// VAT report order lines may store only a fragment of the catalog title without a product id.
    /// </summary>
    public static bool ProductTitleContainedIn( string? fragmentRaw, string? fullRaw )
    {
        string fragment = NormalizeProductTitleForMatch( fragmentRaw );
        string full = NormalizeProductTitleForMatch( fullRaw );
        if (fragment.Length < 8 || full.Length < fragment.Length)
        {
            return false;
        }

        if (string.Equals( fragment, full, StringComparison.OrdinalIgnoreCase ))
        {
            return true;
        }

        string fragmentPrimary = PrimaryTitleSegment( fragment );
        string fullPrimary = PrimaryTitleSegment( full );
        return TitlePrefixMatchesAtBoundary( fragmentPrimary, fullPrimary ) ||
               TitlePrefixMatchesAtBoundary( fullPrimary, fragmentPrimary );
    }

    private static bool LooksLikeCommaSeparatedVariantSuffix( string suffix )
    {
        if (string.IsNullOrWhiteSpace( suffix ) || suffix.Length > 80)
        {
            return false;
        }

        if (suffix.Contains( "частка", StringComparison.OrdinalIgnoreCase ) ||
            suffix.Contains( "том", StringComparison.OrdinalIgnoreCase ))
        {
            return false;
        }

        return true;
    }

    private static bool LooksLikePinVariantSuffix( string suffix )
    {
        if (string.IsNullOrWhiteSpace( suffix ) || suffix.Length > 50)
        {
            return false;
        }

        if (suffix.Contains( '.', StringComparison.Ordinal ) ||
            suffix.Contains( "частка", StringComparison.OrdinalIgnoreCase ) ||
            suffix.Contains( "том", StringComparison.OrdinalIgnoreCase ))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Pin variants are often "style / color". Invoice text may use a different grammatical
    /// form than Shopify (e.g. Радкова vs Радковай) while still referring to the same variant.
    /// </summary>
    public static bool VariantTitlesEquivalentForPaymentMatch( string? leftRaw, string? rightRaw )
    {
        string left = (leftRaw ?? string.Empty).Trim();
        string right = (rightRaw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( left ) || string.IsNullOrWhiteSpace( right ))
        {
            return false;
        }

        if (string.Equals( left, right, StringComparison.OrdinalIgnoreCase ))
        {
            return true;
        }

        if (!TryParsePinStyleColorVariant( left, out string leftStyle, out string leftColor ) ||
            !TryParsePinStyleColorVariant( right, out string rightStyle, out string rightColor ))
        {
            return string.Equals(
                NormalizeLooseVariantLabel( left ),
                NormalizeLooseVariantLabel( right ),
                StringComparison.OrdinalIgnoreCase );
        }

        return string.Equals( leftColor, rightColor, StringComparison.OrdinalIgnoreCase ) &&
               PinVariantStyleKeysEquivalent( leftStyle, rightStyle );
    }

    private static string NormalizeLooseVariantLabel( string raw )
    {
        string s = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return string.Join(
            ' ',
            s.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) );
    }

    private static bool TryParsePinStyleColorVariant( string title, out string style, out string color )
    {
        style = string.Empty;
        color = string.Empty;
        int slash = title.IndexOf( '/', StringComparison.Ordinal );
        if (slash <= 0 || slash >= title.Length - 1)
        {
            return false;
        }

        style = title[..slash].Trim();
        color = title[(slash + 1)..].Trim();
        if (string.IsNullOrWhiteSpace( style ) || string.IsNullOrWhiteSpace( color ))
        {
            return false;
        }

        if (!LooksLikePinVariantSuffix( title ))
        {
            return false;
        }

        return true;
    }

    private static bool PinVariantStyleKeysEquivalent( string leftStyle, string rightStyle ) =>
        string.Equals(
            CanonicalPinStyleKey( leftStyle ),
            CanonicalPinStyleKey( rightStyle ),
            StringComparison.OrdinalIgnoreCase );

    private static string CanonicalPinStyleKey( string style )
    {
        string s = style.Trim().ToLowerInvariant();
        if (s.StartsWith( "радков", StringComparison.Ordinal ))
        {
            return "радков";
        }

        if (s.StartsWith( "загалоўн", StringComparison.Ordinal ))
        {
            return "загалоўн";
        }

        return s;
    }

    public static void ParseProductLineKey( string lineKey, out string productId, out string variantId )
    {
        int separator = lineKey.IndexOf( "::", StringComparison.Ordinal );
        if (separator < 0)
        {
            productId = lineKey;
            variantId = string.Empty;
            return;
        }

        productId = lineKey[..separator];
        variantId = lineKey[(separator + 2)..];
    }

    public static decimal Round2( decimal value ) =>
        Math.Round( value, 2, MidpointRounding.AwayFromZero );

    public static (decimal Gross, decimal Vat, decimal Net) FinalizeSupplierPaymentAmounts(
        decimal grossAmount,
        decimal vatAmount )
    {
        decimal gross = Round2( grossAmount );
        decimal vat = Round2( Math.Clamp( vatAmount, 0m, gross ) );
        decimal net = Round2( gross - vat );
        return (gross, vat, net);
    }

    public static decimal ComputeVatFromProductLines(
        IEnumerable<(decimal LineGross, decimal VatRatePercent)> lines,
        bool applyVat )
    {
        if (!applyVat)
        {
            return 0m;
        }

        decimal total = 0m;
        foreach ((decimal lineGross, decimal vatRatePercent) in lines)
        {
            if (lineGross <= 0m || vatRatePercent <= 0m)
            {
                continue;
            }

            decimal rate = vatRatePercent / 100m;
            total += lineGross * rate / (1m + rate);
        }

        return Round2( total );
    }

    public static void ValidatePeriod( int year, int month )
    {
        if (month < 1 || month > 12)
        {
            throw new InvalidOperationException( "Месяц павінен быць у дыяпазоне 1..12." );
        }

        if (year < 2000 || year > 3000)
        {
            throw new InvalidOperationException( "Некарэктны год справаздачы." );
        }
    }

    public static string NormalizeReportType( string? reportType )
    {
        if (string.IsNullOrWhiteSpace( reportType )) return VatReportType.Poland;
        string normalized = reportType.Trim().ToLowerInvariant();
        return normalized switch
        {
            VatReportType.Poland => VatReportType.Poland,
            VatReportType.Foreign => VatReportType.Foreign,
            _ => throw new InvalidOperationException( "Тып справаздачы павінен быць poland або foreign." )
        };
    }

    public static string BuildReportName( string type, int year, int month )
    {
        string prefix = type == VatReportType.Poland ? "Польшча" : "Замежжа";
        return $"{prefix} {year:D4}-{month:D2}";
    }

    public static string NormalizeOrderNumber( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return string.Empty;
        }

        string trimmed = raw.Trim();
        if (trimmed.StartsWith( "#", StringComparison.Ordinal ))
        {
            trimmed = trimmed[1..].Trim();
        }

        return trimmed;
    }

    public static bool OrderNumbersMatch( string? left, string? right )
    {
        string leftBase = NormalizeOrderNumber( ParseOrderNumberAndContact( left ?? string.Empty ).orderNumber );
        string rightBase = NormalizeOrderNumber( ParseOrderNumberAndContact( right ?? string.Empty ).orderNumber );
        return !string.IsNullOrWhiteSpace( leftBase ) &&
               string.Equals( leftBase, rightBase, StringComparison.OrdinalIgnoreCase );
    }

    public static string EncodeOrderNumberWithContact(
        string orderNumberBase,
        string deliveryName,
        string deliveryAddress,
        string? countryCode = null )
    {
        const int maxLen = 64;
        const string sep = " || ";
        string order = (orderNumberBase ?? string.Empty).Trim();
        string name = (deliveryName ?? string.Empty).Trim();
        string address = (deliveryAddress ?? string.Empty).Trim();
        string country = NormalizeCountryCode( countryCode );

        if (string.IsNullOrEmpty( country ))
        {
            return EncodeOrderNumberLegacy( order, name, address, maxLen, sep );
        }

        string Build( string contactName, string contactAddress ) =>
            $"{order}{sep}{contactName}{sep}{contactAddress}{sep}{country}";

        string full = Build( name, address );
        if (full.Length <= maxLen) return full;

        int overhead = order.Length + country.Length + sep.Length * 3;
        int available = maxLen - overhead;
        if (available <= 0)
        {
            string orderWithCountry = $"{order}{sep}{country}";
            return orderWithCountry.Length <= maxLen
                ? orderWithCountry
                : order[..Math.Min( order.Length, maxLen )];
        }

        string clippedName = name;
        string clippedAddress = address;
        while (Build( clippedName, clippedAddress ).Length > maxLen && clippedAddress.Length > 0)
        {
            clippedAddress = clippedAddress[..^1];
        }

        while (Build( clippedName, clippedAddress ).Length > maxLen && clippedName.Length > 0)
        {
            clippedName = clippedName[..^1];
        }

        return Build( clippedName, clippedAddress );
    }

    public static (string orderNumber, string deliveryName, string deliveryAddress, string countryCode)
        ParseOrderNumberAndContact( string orderNumberRaw )
    {
        if (string.IsNullOrWhiteSpace( orderNumberRaw ))
        {
            return (string.Empty, string.Empty, string.Empty, string.Empty);
        }

        string[] parts = orderNumberRaw.Split( "||", StringSplitOptions.TrimEntries );
        if (parts.Length >= 4)
        {
            return (
                parts[0].Trim(),
                parts[1].Trim(),
                parts[2].Trim(),
                NormalizeCountryCode( parts[3] )
            );
        }

        if (parts.Length >= 3)
        {
            return (parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), string.Empty);
        }

        if (parts.Length == 2)
        {
            return (parts[0].Trim(), string.Empty, parts[1].Trim(), string.Empty);
        }

        return (orderNumberRaw.Trim(), string.Empty, string.Empty, string.Empty);
    }

    private static string NormalizeCountryCode( string? countryCode ) =>
        string.IsNullOrWhiteSpace( countryCode ) ? string.Empty : countryCode.Trim().ToUpperInvariant();

    private static string EncodeOrderNumberLegacy(
        string order,
        string name,
        string address,
        int maxLen,
        string sep )
    {
        string encoded = $"{order}{sep}{name}{sep}{address}";
        if (encoded.Length <= maxLen) return encoded;

        int available = Math.Max( 0, maxLen - order.Length - 8 );
        string clippedName = name[..Math.Min( name.Length, available )];
        available = Math.Max( 0, maxLen - order.Length - clippedName.Length - 8 );
        string clippedAddress = address[..Math.Min( address.Length, available )];
        encoded = $"{order}{sep}{clippedName}{sep}{clippedAddress}";
        return encoded.Length <= maxLen ? encoded : order[..Math.Min( order.Length, maxLen )];
    }

    public static (int Year, int Month) ResolveSaleCalendarPeriod( DateTime dateUtc )
    {
        DateTime utc = DateTime.SpecifyKind( dateUtc, DateTimeKind.Utc );
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc( utc, GetBusinessTimeZone() );
        return (local.Year, local.Month);
    }

    public static DateTime ResolveCashSaleDateUtc( int periodYear, int periodMonth )
    {
        int lastDay = DateTime.DaysInMonth( periodYear, periodMonth );
        // End of the report month in business time so FIFO sorts after same-day payments
        // and the date does not roll into the next month when displayed in Europe/Warsaw.
        DateTime localEnd = new DateTime( periodYear, periodMonth, lastDay, 23, 59, 59, DateTimeKind.Unspecified );
        return TimeZoneInfo.ConvertTimeToUtc( localEnd, GetBusinessTimeZone() );
    }

    private static TimeZoneInfo GetBusinessTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById( "Europe/Warsaw" );
        }
        catch
        {
            return TimeZoneInfo.FindSystemTimeZoneById( "Central European Standard Time" );
        }
    }
}
