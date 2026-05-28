using backend.Models;

namespace backend.Services;

internal static class VatReportHelpers
{
    public static decimal Round2( decimal value ) =>
        Math.Round( value, 2, MidpointRounding.AwayFromZero );

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
        string deliveryAddress )
    {
        string order = (orderNumberBase ?? string.Empty).Trim();
        string name = (deliveryName ?? string.Empty).Trim();
        string address = (deliveryAddress ?? string.Empty).Trim();
        string encoded = $"{order} || {name} || {address}";
        if (encoded.Length <= 64) return encoded;

        int available = Math.Max( 0, 64 - order.Length - 8 );
        string clippedName = name[..Math.Min( name.Length, available )];
        available = Math.Max( 0, 64 - order.Length - clippedName.Length - 8 );
        string clippedAddress = address[..Math.Min( address.Length, available )];
        encoded = $"{order} || {clippedName} || {clippedAddress}";
        return encoded.Length <= 64 ? encoded : order[..Math.Min( order.Length, 64 )];
    }

    public static (string orderNumber, string deliveryName, string deliveryAddress) ParseOrderNumberAndContact(
        string orderNumberRaw )
    {
        if (string.IsNullOrWhiteSpace( orderNumberRaw )) return (string.Empty, string.Empty, string.Empty);
        string[] parts = orderNumberRaw.Split( "||", StringSplitOptions.TrimEntries );
        if (parts.Length >= 3)
        {
            return (parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
        }

        if (parts.Length == 2)
        {
            return (parts[0].Trim(), string.Empty, parts[1].Trim());
        }

        return (orderNumberRaw.Trim(), string.Empty, string.Empty);
    }
}
