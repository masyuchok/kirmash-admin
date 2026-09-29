using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace backend.Services;

/// <summary>
/// Unified shop cover style: square ≤1080 PNG, cut-out on transparent canvas,
/// soft drop shadow matching the brand sample (shadow bottom-right).
/// </summary>
public sealed class BookCoverStylizer
{
    public const int OutputSize = 1080;
    private const float CoverMaxFraction = 0.78f;
    private const int ShadowOffsetX = 22;
    private const int ShadowOffsetY = 28;
    private const float ShadowBlurSigma = 18f;
    private const float ShadowOpacity = 0.42f;

    private readonly ILogger<BookCoverStylizer> _logger;

    public BookCoverStylizer( ILogger<BookCoverStylizer> logger )
    {
        _logger = logger;
    }

    public byte[] StyleToSquarePng( byte[] sourceBytes )
    {
        if (sourceBytes is null || sourceBytes.Length == 0)
        {
            throw new InvalidOperationException( "Пустая выява вокладкі." );
        }

        using Image<Rgba32> source = Image.Load<Rgba32>( sourceBytes );
        KnockOutUniformBackground( source );
        Rectangle content = FindOpaqueBounds( source );
        if (content.Width < 8 || content.Height < 8)
        {
            content = new Rectangle( 0, 0, source.Width, source.Height );
        }

        using Image<Rgba32> cropped = source.Clone( ctx => ctx.Crop( content ) );

        int maxCover = (int)(OutputSize * CoverMaxFraction);
        float scale = Math.Min(
            (float)maxCover / cropped.Width,
            (float)maxCover / cropped.Height );
        int coverW = Math.Max( 1, (int)Math.Round( cropped.Width * scale ) );
        int coverH = Math.Max( 1, (int)Math.Round( cropped.Height * scale ) );

        cropped.Mutate( ctx => ctx.Resize( coverW, coverH ) );

        int coverX = (OutputSize - coverW) / 2;
        int coverY = (OutputSize - coverH) / 2;

        using Image<Rgba32> canvas = new( OutputSize, OutputSize, Color.Transparent );
        DrawSoftShadow( canvas, coverX, coverY, coverW, coverH );
        canvas.Mutate( ctx => ctx.DrawImage( cropped, new Point( coverX, coverY ), 1f ) );

        using MemoryStream ms = new();
        canvas.SaveAsPng( ms );
        _logger.LogInformation(
            "Styled cover PNG {W}x{H} from {SrcW}x{SrcH} ({Bytes} bytes)",
            OutputSize,
            OutputSize,
            source.Width,
            source.Height,
            ms.Length );
        return ms.ToArray();
    }

    /// <summary>
    /// Gallery extras: no cut-out/shadow — only downscale so width ≤ maxWidth.
    /// </summary>
    public byte[] ResizeMaxWidthJpeg( byte[] sourceBytes, int maxWidth = OutputSize )
    {
        if (sourceBytes is null || sourceBytes.Length == 0)
        {
            throw new InvalidOperationException( "Пустая выява." );
        }

        if (maxWidth < 1)
        {
            maxWidth = OutputSize;
        }

        using Image<Rgba32> source = Image.Load<Rgba32>( sourceBytes );
        if (source.Width > maxWidth)
        {
            float scale = (float)maxWidth / source.Width;
            int w = maxWidth;
            int h = Math.Max( 1, (int)Math.Round( source.Height * scale ) );
            source.Mutate( ctx => ctx.Resize( w, h ) );
        }

        using MemoryStream ms = new();
        source.SaveAsJpeg( ms );
        return ms.ToArray();
    }

    private static void DrawSoftShadow(
        Image<Rgba32> canvas,
        int coverX,
        int coverY,
        int coverW,
        int coverH )
    {
        using Image<Rgba32> shadowLayer = new( OutputSize, OutputSize, Color.Transparent );
        int sx = coverX + ShadowOffsetX;
        int sy = coverY + ShadowOffsetY;

        shadowLayer.ProcessPixelRows( accessor =>
        {
            for (int y = 0; y < coverH; y++)
            {
                int py = sy + y;
                if (py < 0 || py >= OutputSize)
                {
                    continue;
                }

                Span<Rgba32> row = accessor.GetRowSpan( py );
                for (int x = 0; x < coverW; x++)
                {
                    int px = sx + x;
                    if (px < 0 || px >= OutputSize)
                    {
                        continue;
                    }

                    row[px] = new Rgba32( 0, 0, 0, 255 );
                }
            }
        } );

        shadowLayer.Mutate( ctx => ctx.GaussianBlur( ShadowBlurSigma ) );

        // Tint shadow opacity.
        shadowLayer.ProcessPixelRows( accessor =>
        {
            for (int y = 0; y < OutputSize; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan( y );
                for (int x = 0; x < OutputSize; x++)
                {
                    ref Rgba32 p = ref row[x];
                    if (p.A == 0)
                    {
                        continue;
                    }

                    byte a = (byte)Math.Clamp( (int)(p.A * ShadowOpacity), 0, 255 );
                    p = new Rgba32( 0, 0, 0, a );
                }
            }
        } );

        canvas.Mutate( ctx => ctx.DrawImage( shadowLayer, new Point( 0, 0 ), 1f ) );
    }

    /// <summary>
    /// If corners share a nearly uniform background color, punch it to transparent
    /// so the cover reads as a cut-out (shop photos on white/gray).
    /// </summary>
    private static void KnockOutUniformBackground( Image<Rgba32> image )
    {
        Rgba32 c0 = image[0, 0];
        Rgba32 c1 = image[image.Width - 1, 0];
        Rgba32 c2 = image[0, image.Height - 1];
        Rgba32 c3 = image[image.Width - 1, image.Height - 1];
        if (!ColorsClose( c0, c1, 18 )
            || !ColorsClose( c0, c2, 18 )
            || !ColorsClose( c0, c3, 18 ))
        {
            return;
        }

        // Don't knock out dark artistic covers where corners are black intentional content.
        int brightness = (c0.R + c0.G + c0.B) / 3;
        if (brightness < 40)
        {
            return;
        }

        const int threshold = 28;
        image.ProcessPixelRows( accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan( y );
                for (int x = 0; x < row.Length; x++)
                {
                    if (ColorsClose( row[x], c0, threshold ))
                    {
                        row[x] = new Rgba32( 0, 0, 0, 0 );
                    }
                }
            }
        } );
    }

    private static bool ColorsClose( Rgba32 a, Rgba32 b, int threshold )
    {
        return Math.Abs( a.R - b.R ) <= threshold
            && Math.Abs( a.G - b.G ) <= threshold
            && Math.Abs( a.B - b.B ) <= threshold;
    }

    private static Rectangle FindOpaqueBounds( Image<Rgba32> image )
    {
        int minX = image.Width;
        int minY = image.Height;
        int maxX = -1;
        int maxY = -1;

        image.ProcessPixelRows( accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan( y );
                for (int x = 0; x < row.Length; x++)
                {
                    if (row[x].A < 16)
                    {
                        continue;
                    }

                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }
        } );

        if (maxX < minX || maxY < minY)
        {
            return new Rectangle( 0, 0, image.Width, image.Height );
        }

        return new Rectangle( minX, minY, maxX - minX + 1, maxY - minY + 1 );
    }
}
