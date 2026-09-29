using System.Text;

namespace backend.Services.ImageFetch;

internal static class RemoteImageContent
{
    public static bool LooksLikeHtml( byte[] bytes )
    {
        int len = Math.Min( bytes.Length, 64 );
        if (len < 15)
        {
            return false;
        }

        string head = Encoding.ASCII.GetString( bytes, 0, len ).TrimStart().ToLowerInvariant();
        return head.StartsWith( "<!doctype", StringComparison.Ordinal )
            || head.StartsWith( "<html", StringComparison.Ordinal );
    }

    public static string? GuessMimeType( byte[] bytes )
    {
        if (bytes.Length >= 3
            && bytes[0] == 0xFF
            && bytes[1] == 0xD8
            && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 8
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 12
            && bytes[0] == (byte)'R'
            && bytes[1] == (byte)'I'
            && bytes[2] == (byte)'F'
            && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W'
            && bytes[9] == (byte)'E'
            && bytes[10] == (byte)'B'
            && bytes[11] == (byte)'P')
        {
            return "image/webp";
        }

        if (bytes.Length >= 6
            && bytes[0] == (byte)'G'
            && bytes[1] == (byte)'I'
            && bytes[2] == (byte)'F')
        {
            return "image/gif";
        }

        return null;
    }

    public static string GuessFileName( string? imageUrl, string mimeType )
    {
        if (!string.IsNullOrWhiteSpace( imageUrl )
            && Uri.TryCreate( imageUrl, UriKind.Absolute, out Uri? uri ))
        {
            string name = Path.GetFileName( uri.AbsolutePath );
            if (!string.IsNullOrWhiteSpace( name )
                && name.IndexOf( '.', StringComparison.Ordinal ) > 0
                && name.Length <= 180)
            {
                return name;
            }
        }

        string mime = (mimeType ?? string.Empty).ToLowerInvariant();
        if (mime.Contains( "png", StringComparison.Ordinal )) return "cover.png";
        if (mime.Contains( "webp", StringComparison.Ordinal )) return "cover.webp";
        if (mime.Contains( "gif", StringComparison.Ordinal )) return "cover.gif";
        return "cover.jpg";
    }

    public static string? ValidateImageBytes(
        byte[] bytes,
        string? contentTypeHeader,
        int maxBytes )
    {
        if (bytes.Length == 0)
        {
            return "Empty response body.";
        }

        if (bytes.Length > maxBytes)
        {
            return $"Body too large: {bytes.Length} bytes (max {maxBytes}).";
        }

        if (!string.IsNullOrWhiteSpace( contentTypeHeader )
            && contentTypeHeader.Contains( "text/html", StringComparison.OrdinalIgnoreCase ))
        {
            return "Content-Type is text/html (likely bot wall / error page).";
        }

        if (LooksLikeHtml( bytes ))
        {
            return "Body looks like HTML despite non-HTML Content-Type.";
        }

        string? magic = GuessMimeType( bytes );
        if (magic is null
            && (string.IsNullOrWhiteSpace( contentTypeHeader )
                || !contentTypeHeader.StartsWith( "image/", StringComparison.OrdinalIgnoreCase )))
        {
            return $"Not an image by magic bytes and Content-Type={contentTypeHeader ?? "(none)"}.";
        }

        return null;
    }
}
