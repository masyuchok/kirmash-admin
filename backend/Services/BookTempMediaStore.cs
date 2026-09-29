using Microsoft.Extensions.Caching.Memory;

namespace backend.Services;

public sealed class BookTempMediaEntry
{
    public string Id { get; init; } = string.Empty;
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
    public string ContentType { get; init; } = "image/jpeg";
    public string? SourceUrl { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Short-lived in-memory store for book photos downloaded from supplier CDNs
/// so the browser can preview same-origin and Shopify attach uses local bytes.
/// </summary>
public sealed class BookTempMediaStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes( 45 );
    private readonly IMemoryCache _cache;

    public BookTempMediaStore( IMemoryCache cache )
    {
        _cache = cache;
    }

    public BookTempMediaEntry Put( byte[] bytes, string contentType, string? sourceUrl = null )
    {
        if (bytes is null || bytes.Length == 0)
        {
            throw new ArgumentException( "Empty image bytes.", nameof( bytes ) );
        }

        string id = Guid.NewGuid().ToString( "N" );
        BookTempMediaEntry entry = new()
        {
            Id = id,
            Bytes = bytes,
            ContentType = string.IsNullOrWhiteSpace( contentType ) ? "image/jpeg" : contentType.Trim(),
            SourceUrl = string.IsNullOrWhiteSpace( sourceUrl ) ? null : sourceUrl.Trim(),
        };

        _cache.Set(
            CacheKey( id ),
            entry,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = Ttl,
            } );

        return entry;
    }

    public bool TryGet( string id, out BookTempMediaEntry entry )
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace( id ))
        {
            return false;
        }

        if (_cache.TryGetValue( CacheKey( id.Trim() ), out BookTempMediaEntry? found )
            && found is not null
            && found.Bytes.Length > 0)
        {
            entry = found;
            return true;
        }

        return false;
    }

    public void Remove( string id )
    {
        if (!string.IsNullOrWhiteSpace( id ))
        {
            _cache.Remove( CacheKey( id.Trim() ) );
        }
    }

    private static string CacheKey( string id ) => $"book-temp-media:{id}";
}
