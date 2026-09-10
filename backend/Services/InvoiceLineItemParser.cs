using System.Globalization;
using System.Text.RegularExpressions;
using backend.Models;

namespace backend.Services;

/// <summary>
/// Deterministic line-item extraction for large text PDFs (e.g. 100+ rows).
/// Works best when PDF text is reconstructed as visual lines (see InvoicePdfTextExtractor).
/// </summary>
public sealed class InvoiceLineItemParser
{
    // PdfPig may drop/alter hyphens: CZB-A8FE…, CZB – A8FE…, CZBA8FE…
    private static readonly Regex SkuTokenRegex = new(
        @"(?<sku>[A-Z]{2,6}\s*[-–—]?\s*[A-F0-9]{8,20})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled );

    private static readonly Regex MoneyRegex = new(
        @"(?<![\d])(?<amount>\d{1,3}(?:[ \u00A0]?\d{3})*,\d{2}|\d+[.,]\d{2})(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled );

    private static readonly Regex QtyAfterUnitRegex = new(
        @"(?:szt\.?|шт\.?|pcs\.?|pc\.?)\s*(?<qty>\d{1,4})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled );

    private static readonly Regex LineWithSkuRegex = new(
        @"^(?<lp>\d{1,3})?\s*(?<sku>[A-Z]{2,6}\s*[-–—]?\s*[A-F0-9]{8,20})\s+(?<rest>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Multiline );

    private static readonly Regex IsbnRegex = new(
        @"\b(?:97[89][-\s]?)?(?:\d[-\s]?){9}[\dXx]\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled );

    public IReadOnlyList<VatReportExpenseInvoiceExtractProduct> Parse( string pdfText )
    {
        if (string.IsNullOrWhiteSpace( pdfText ))
        {
            return Array.Empty<VatReportExpenseInvoiceExtractProduct>();
        }

        // Preferred: one product per visual line that starts with (Lp) + SKU.
        List<VatReportExpenseInvoiceExtractProduct> fromLines = ParseLineOriented( pdfText );
        if (fromLines.Count >= 2)
        {
            return fromLines;
        }

        List<Match> skuMatches = SkuTokenRegex.Matches( pdfText ).Cast<Match>().ToList();
        if (skuMatches.Count < 2)
        {
            return ParseWithoutSku( pdfText );
        }

        List<VatReportExpenseInvoiceExtractProduct> products = new();
        for (int i = 0; i < skuMatches.Count; i++)
        {
            Match skuMatch = skuMatches[i];
            int bodyStart = skuMatch.Index + skuMatch.Length;
            int bodyEnd = i + 1 < skuMatches.Count ? skuMatches[i + 1].Index : pdfText.Length;
            if (bodyEnd <= bodyStart)
            {
                continue;
            }

            string body = pdfText[bodyStart..bodyEnd].Trim();
            VatReportExpenseInvoiceExtractProduct? product = ParseBody( body );
            if (product is null)
            {
                continue;
            }

            products.Add( product );
        }

        return products.Count >= 2 ? products : ParseWithoutSku( pdfText );
    }

    private static List<VatReportExpenseInvoiceExtractProduct> ParseLineOriented( string pdfText )
    {
        List<VatReportExpenseInvoiceExtractProduct> products = new();
        foreach (string rawLine in pdfText.Split( '\n' ))
        {
            string line = rawLine.Trim();
            if (line.Length < 8)
            {
                continue;
            }

            Match m = LineWithSkuRegex.Match( line );
            if (!m.Success)
            {
                // Title may wrap to next line without SKU — skip those for now.
                continue;
            }

            VatReportExpenseInvoiceExtractProduct? product = ParseBody( m.Groups["rest"].Value );
            if (product is not null)
            {
                products.Add( product );
            }
        }

        return products;
    }

    private static IReadOnlyList<VatReportExpenseInvoiceExtractProduct> ParseWithoutSku( string pdfText )
    {
        List<VatReportExpenseInvoiceExtractProduct> products = new();
        foreach (Match row in Regex.Matches(
                     pdfText,
                     @"(?:^|\n)\s*(?<n>\d{1,3})[.)\t ]\s+(?<body>[^\n]{8,300})",
                     RegexOptions.Multiline ))
        {
            string body = row.Groups["body"].Value.Trim();
            // Skip header-like numbered noise.
            if (Regex.IsMatch( body, @"^(Lp|Nazwa|Ilość|Cena|Rabat|Razem)\b", RegexOptions.IgnoreCase ))
            {
                continue;
            }

            VatReportExpenseInvoiceExtractProduct? product = ParseBody( body );
            if (product is not null)
            {
                products.Add( product );
            }
        }

        return products;
    }

    private static VatReportExpenseInvoiceExtractProduct? ParseBody( string body )
    {
        if (string.IsNullOrWhiteSpace( body ))
        {
            return null;
        }

        string cleaned = Regex.Replace(
            body,
            @"\b(Razem|Suma|Do zapłaty|VAT|Netto|Brutto|NIP|PKO|IBAN|przelew)\b.*$",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline );

        List<decimal> amounts = MoneyRegex.Matches( cleaned )
            .Select( m => ParseMoney( m.Groups["amount"].Value ) )
            .Where( v => v is > 0 )
            .Select( v => v!.Value )
            .ToList();

        int qty = 1;
        Match qtyMatch = QtyAfterUnitRegex.Match( cleaned );
        if (qtyMatch.Success
            && int.TryParse( qtyMatch.Groups["qty"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedQty )
            && parsedQty > 0
            && parsedQty <= 5000)
        {
            qty = parsedQty;
        }
        else
        {
            Match bareQty = Regex.Match(
                cleaned,
                @"\b(?<qty>[1-9]\d{0,3})\b(?=(?:\s|[^\d])*(?:\d{1,3}(?:[ \u00A0]?\d{3})*,\d{2}))" );
            if (bareQty.Success
                && int.TryParse( bareQty.Groups["qty"].Value, out parsedQty )
                && parsedQty > 0
                && parsedQty <= 5000)
            {
                qty = parsedQty;
            }
        }

        string title = cleaned;
        title = QtyAfterUnitRegex.Replace( title, " " );
        title = MoneyRegex.Replace( title, " " );
        title = Regex.Replace( title, @"\b\d{1,2}\s*%", " " );
        title = Regex.Replace( title, @"\s+", " " ).Trim( ' ', '-', '–', '—', ',', '.', ';', '\t' );
        if (title.Length < 3)
        {
            return null;
        }

        if (Regex.IsMatch(
                title,
                @"^(sprzedawca|nabywca|faktura|data|termin|nazwa|ilość|cena|rabat)\b",
                RegexOptions.IgnoreCase ))
        {
            return null;
        }

        decimal? unitGross = null;
        if (amounts.Count >= 1)
        {
            decimal lineTotal = amounts[^1];
            if (qty > 0)
            {
                unitGross = Math.Round( lineTotal / qty, 2, MidpointRounding.AwayFromZero );
            }
        }

        string? barcode = null;
        Match isbn = IsbnRegex.Match( cleaned );
        if (isbn.Success)
        {
            barcode = new string( isbn.Value.Where( char.IsDigit ).ToArray() );
            if (barcode.Length is < 10 or > 13)
            {
                barcode = null;
            }
        }

        return new VatReportExpenseInvoiceExtractProduct
        {
            Title = title.Length > 200 ? title[..200].Trim() : title,
            Barcode = barcode,
            Quantity = qty,
            UnitGrossPrice = unitGross,
            VatRatePercent = null
        };
    }

    private static decimal? ParseMoney( string raw )
    {
        string normalized = raw
            .Replace( "\u00A0", "", StringComparison.Ordinal )
            .Replace( " ", "", StringComparison.Ordinal )
            .Replace( ',', '.' );
        return decimal.TryParse(
            normalized,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out decimal value )
            ? value
            : null;
    }
}
