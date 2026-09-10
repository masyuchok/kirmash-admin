using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace backend.Services;

public sealed class InvoicePdfTextExtractor
{
    private const int MaxExtractedChars = 500_000;

    public string ExtractText( byte[] pdfBytes )
    {
        if (pdfBytes.Length == 0)
        {
            throw new InvalidOperationException( "Файл PDF пусты." );
        }

        using PdfDocument document = PdfDocument.Open( pdfBytes );
        StringBuilder sb = new();
        foreach (Page page in document.GetPages())
        {
            string pageText = ExtractPageLines( page );
            if (pageText.Length == 0)
            {
                // Fallback if word geometry is empty.
                pageText = page.Text?.Trim() ?? string.Empty;
            }

            if (pageText.Length == 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine( "---" );
            }

            sb.Append( pageText );
            if (sb.Length >= MaxExtractedChars)
            {
                break;
            }
        }

        string text = sb.ToString().Trim();
        if (text.Length < 40)
        {
            throw new InvalidOperationException(
                "З PDF амаль няма тэксту. Магчыма, гэта скан/фота — OCR пакуль не падтрымліваецца." );
        }

        if (text.Length > MaxExtractedChars)
        {
            text = text[..MaxExtractedChars];
        }

        return text;
    }

    /// <summary>
    /// Rebuild reading order by Y then X — plain page.Text often mangles invoice tables.
    /// </summary>
    private static string ExtractPageLines( Page page )
    {
        List<Word> words = page.GetWords().ToList();
        if (words.Count == 0)
        {
            return string.Empty;
        }

        // Cluster words onto visual rows (tolerance ~ half a typical line height).
        double medianHeight = words
            .Select( w => w.BoundingBox.Height )
            .Where( h => h > 0.1 )
            .OrderBy( h => h )
            .DefaultIfEmpty( 10 )
            .ElementAt( Math.Max( 0, words.Count / 2 ) );
        double yTolerance = Math.Max( 2.0, medianHeight * 0.6 );

        List<List<Word>> lines = new();
        foreach (Word word in words.OrderByDescending( w => w.BoundingBox.Bottom )
                     .ThenBy( w => w.BoundingBox.Left ))
        {
            List<Word>? line = lines.LastOrDefault();
            if (line is null)
            {
                lines.Add( new List<Word> { word } );
                continue;
            }

            double lineY = line.Average( w => w.BoundingBox.Bottom );
            if (Math.Abs( word.BoundingBox.Bottom - lineY ) <= yTolerance)
            {
                line.Add( word );
            }
            else
            {
                lines.Add( new List<Word> { word } );
            }
        }

        StringBuilder sb = new();
        foreach (List<Word> line in lines)
        {
            IOrderedEnumerable<Word> ordered = line.OrderBy( w => w.BoundingBox.Left );
            string lineText = string.Join( " ", ordered.Select( w => w.Text ) ).Trim();
            if (lineText.Length == 0)
            {
                continue;
            }

            sb.AppendLine( lineText );
        }

        return sb.ToString().Trim();
    }

    public static string ClipKeepingHeadAndTail( string text, int maxChars )
    {
        if (string.IsNullOrEmpty( text ) || text.Length <= maxChars)
        {
            return text;
        }

        int head = (maxChars * 2) / 3;
        int tail = maxChars - head - 5;
        if (tail < 200)
        {
            tail = 200;
            head = Math.Max( 100, maxChars - tail - 5 );
        }

        return text[..head] + "\n...\n" + text[^tail..];
    }
}
