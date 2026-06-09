using backend.Models;

namespace backend.Services;

internal static class VatReportHelpers
{
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
}
