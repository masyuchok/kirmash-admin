using System.Globalization;
using backend.Data;
using backend.Models;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public sealed class KirmashService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public KirmashService( AppDbContext db, IConfiguration config )
    {
        _db = db;
        _config = config;
    }

    public async Task<List<KirmashListItemDto>> ListAsync( CancellationToken cancellationToken = default )
    {
        List<Kirmash> rows = await _db.Kirmashes
            .AsNoTracking()
            .Include( k => k.Lines )
            .Include( k => k.PriceTags )
            .OrderByDescending( k => k.EventDate )
            .ThenByDescending( k => k.Id )
            .ToListAsync( cancellationToken );

        return rows.Select( ToListItem ).ToList();
    }

    public async Task<KirmashDetailDto?> GetAsync( int id, CancellationToken cancellationToken = default )
    {
        Kirmash? row = await _db.Kirmashes
            .AsNoTracking()
            .Include( k => k.Lines )
            .Include( k => k.PriceTags )
            .FirstOrDefaultAsync( k => k.Id == id, cancellationToken );
        return row is null ? null : ToDetail( row );
    }

    public async Task<KirmashDetailDto> CreateAsync(
        KirmashUpsertRequest request,
        CancellationToken cancellationToken = default )
    {
        Kirmash entity = new()
        {
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Status = "draft"
        };
        ApplyUpsert( entity, request );
        _db.Kirmashes.Add( entity );
        await _db.SaveChangesAsync( cancellationToken );
        return (await GetAsync( entity.Id, cancellationToken ))!;
    }

    public async Task<KirmashDetailDto?> UpdateAsync(
        int id,
        KirmashUpsertRequest request,
        CancellationToken cancellationToken = default )
    {
        Kirmash? entity = await _db.Kirmashes
            .Include( k => k.Lines )
            .Include( k => k.PriceTags )
            .FirstOrDefaultAsync( k => k.Id == id, cancellationToken );
        if (entity is null)
        {
            return null;
        }

        _db.KirmashPriceTags.RemoveRange( entity.PriceTags );
        _db.KirmashLines.RemoveRange( entity.Lines );
        entity.PriceTags.Clear();
        entity.Lines.Clear();
        entity.Status = "draft";
        entity.UpdatedAtUtc = DateTime.UtcNow;
        ApplyUpsert( entity, request );
        await _db.SaveChangesAsync( cancellationToken );
        return await GetAsync( entity.Id, cancellationToken );
    }

    public async Task<bool> DeleteAsync( int id, CancellationToken cancellationToken = default )
    {
        Kirmash? entity = await _db.Kirmashes.FirstOrDefaultAsync( k => k.Id == id, cancellationToken );
        if (entity is null)
        {
            return false;
        }

        _db.Kirmashes.Remove( entity );
        await _db.SaveChangesAsync( cancellationToken );
        return true;
    }

    public async Task<List<KirmashPriceTagDto>> GetPriceTagsAsync(
        int id,
        CancellationToken cancellationToken = default )
    {
        return await _db.KirmashPriceTags
            .AsNoTracking()
            .Where( t => t.KirmashId == id )
            .OrderBy( t => t.Sequence )
            .ThenBy( t => t.Id )
            .Select( t => new KirmashPriceTagDto
            {
                Id = t.Id,
                KirmashLineId = t.KirmashLineId,
                Sequence = t.Sequence,
                Title = t.Title,
                UnitPrice = t.UnitPrice,
                CheckoutUrl = t.CheckoutUrl
            } )
            .ToListAsync( cancellationToken );
    }

    public async Task<List<KirmashPriceTagDto>> GeneratePriceTagsAsync(
        int id,
        CancellationToken cancellationToken = default )
    {
        Kirmash? entity = await _db.Kirmashes
            .Include( k => k.Lines )
            .Include( k => k.PriceTags )
            .FirstOrDefaultAsync( k => k.Id == id, cancellationToken );
        if (entity is null)
        {
            throw new InvalidOperationException( "Кірмаш не знойдзены." );
        }

        if (entity.Lines.Count == 0)
        {
            throw new InvalidOperationException( "Дадайце тавары перад стварэннем цэннікаў." );
        }

        string host = ResolveStorefrontHost();
        _db.KirmashPriceTags.RemoveRange( entity.PriceTags );
        entity.PriceTags.Clear();

        int sequence = 0;
        DateTime now = DateTime.UtcNow;
        foreach (KirmashLine line in entity.Lines.OrderBy( l => l.Id ))
        {
            string checkoutUrl = BuildCartPermalink( host, line.ShopifyVariantId );
            for (int i = 0; i < line.Quantity; i++)
            {
                sequence++;
                entity.PriceTags.Add( new KirmashPriceTag
                {
                    KirmashId = entity.Id,
                    KirmashLineId = line.Id,
                    Sequence = sequence,
                    Title = line.Title,
                    UnitPrice = line.UnitPrice,
                    CheckoutUrl = checkoutUrl,
                    CreatedAtUtc = now
                } );
            }
        }

        entity.Status = "ready";
        entity.UpdatedAtUtc = now;
        await _db.SaveChangesAsync( cancellationToken );
        return await GetPriceTagsAsync( id, cancellationToken );
    }

    private void ApplyUpsert( Kirmash entity, KirmashUpsertRequest request )
    {
        string title = (request.Title ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            throw new InvalidOperationException( "Укажыце назву кірмаша." );
        }

        if (!DateOnly.TryParse(
                (request.EventDate ?? string.Empty).Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly eventDate ))
        {
            throw new InvalidOperationException( "Няправільная дата кірмаша (чакаецца YYYY-MM-DD)." );
        }

        List<KirmashLineInput> lines = (request.Lines ?? new List<KirmashLineInput>())
            .Where( l => !string.IsNullOrWhiteSpace( l.ShopifyProductId ) )
            .ToList();
        if (lines.Count == 0)
        {
            throw new InvalidOperationException( "Дадайце хаця б адзін тавар." );
        }

        entity.Title = title.Length > 256 ? title[..256] : title;
        entity.Description = (request.Description ?? string.Empty).Trim();
        entity.EventDate = eventDate;

        foreach (KirmashLineInput input in lines)
        {
            string productId = ShopifyIds.NormalizeProductId( input.ShopifyProductId );
            string variantId = ShopifyIds.NormalizeVariantId( input.ShopifyVariantId ?? string.Empty );
            if (string.IsNullOrWhiteSpace( productId ))
            {
                throw new InvalidOperationException( "Няправільны Shopify product id." );
            }

            if (ShopifyIds.TryParseNumericVariantId( variantId ) is null
                && ShopifyIds.TryParseNumericVariantId( input.ShopifyVariantId ?? string.Empty ) is null)
            {
                // Allow empty variant only if we can still resolve later — require numeric for cart URL.
                if (string.IsNullOrWhiteSpace( variantId ))
                {
                    throw new InvalidOperationException(
                        $"Для «{(input.Title ?? productId).Trim()}» патрэбны Shopify variant id." );
                }
            }

            int qty = Math.Max( 1, input.Quantity );
            if (qty > 5000)
            {
                throw new InvalidOperationException( "Колькасць занадта вялікая." );
            }

            string lineTitle = (input.Title ?? string.Empty).Trim();
            if (lineTitle.Length == 0)
            {
                lineTitle = productId;
            }

            entity.Lines.Add( new KirmashLine
            {
                ShopifyProductId = productId,
                ShopifyVariantId = variantId,
                Title = lineTitle.Length > 512 ? lineTitle[..512] : lineTitle,
                UnitPrice = Math.Round( Math.Max( 0, input.UnitPrice ), 2, MidpointRounding.AwayFromZero ),
                Quantity = qty
            } );
        }
    }

    private string ResolveStorefrontHost()
    {
        string host = (_config["Shopify:StorefrontHost"]
            ?? _config["Shopify:Shop"]
            ?? "kirma.sh").Trim();
        host = host.Replace( "https://", "", StringComparison.OrdinalIgnoreCase )
            .Replace( "http://", "", StringComparison.OrdinalIgnoreCase )
            .Trim()
            .TrimEnd( '/' );
        if (host.EndsWith( ".myshopify.com", StringComparison.OrdinalIgnoreCase )
            || string.IsNullOrWhiteSpace( host ))
        {
            // Prefer public custom domain for customer-facing QR codes.
            host = "kirma.sh";
        }

        return host;
    }

    public static string BuildCartPermalink( string host, string variantId )
    {
        long? numeric = ShopifyIds.TryParseNumericVariantId( variantId );
        if (numeric is null)
        {
            throw new InvalidOperationException(
                $"Не ўдалося пабудаваць cart-спасылку для variant «{variantId}»." );
        }

        string cleanHost = host.Trim().TrimEnd( '/' );
        return $"https://{cleanHost}/cart/{numeric.Value}:1";
    }

    private static KirmashListItemDto ToListItem( Kirmash k ) => new()
    {
        Id = k.Id,
        Title = k.Title,
        Description = k.Description,
        EventDate = k.EventDate.ToString( "yyyy-MM-dd", CultureInfo.InvariantCulture ),
        Status = k.Status,
        LinesCount = k.Lines.Count,
        TagsCount = k.PriceTags.Count,
        UnitsCount = k.Lines.Sum( l => l.Quantity ),
        CreatedAtUtc = k.CreatedAtUtc
    };

    private static KirmashDetailDto ToDetail( Kirmash k ) => new()
    {
        Id = k.Id,
        Title = k.Title,
        Description = k.Description,
        EventDate = k.EventDate.ToString( "yyyy-MM-dd", CultureInfo.InvariantCulture ),
        Status = k.Status,
        CreatedAtUtc = k.CreatedAtUtc,
        UpdatedAtUtc = k.UpdatedAtUtc,
        PriceTagsCount = k.PriceTags.Count,
        Lines = k.Lines
            .OrderBy( l => l.Id )
            .Select( l => new KirmashLineDto
            {
                Id = l.Id,
                ShopifyProductId = l.ShopifyProductId,
                ShopifyVariantId = l.ShopifyVariantId,
                Title = l.Title,
                UnitPrice = l.UnitPrice,
                Quantity = l.Quantity
            } )
            .ToList()
    };
}
