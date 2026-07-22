using System.Globalization;
using System.Text.Json;

namespace backend.Services.Odoo;

public sealed class OdooPosSalesReader
{
    private readonly OdooJsonRpcClient _client;
    private readonly OdooAuthService _auth;
    private readonly IConfiguration _config;

    public OdooPosSalesReader(
        OdooJsonRpcClient client,
        OdooAuthService auth,
        IConfiguration config )
    {
        _client = client;
        _auth = auth;
        _config = config;
    }

    public sealed record PosOrderLine(
        int OrderId,
        string? OrderName,
        int LineId,
        int ProductId,
        decimal Quantity,
        DateTime SoldAtUtc );

    public bool IsConfigured
    {
        get
        {
            string login = (_config["Odoo:SyncLogin"] ?? string.Empty).Trim();
            string password = _config["Odoo:SyncPassword"] ?? string.Empty;
            string baseUrl = (_config["Odoo:BaseUrl"] ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace( login )
                   && !string.IsNullOrWhiteSpace( password )
                   && !string.IsNullOrWhiteSpace( baseUrl );
        }
    }

    public async Task<List<PosOrderLine>> FetchPaidLinesSinceAsync(
        DateTime sinceUtc,
        int? minOrderIdExclusive,
        CancellationToken cancellationToken = default )
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException( "Odoo sync credentials are not configured." );
        }

        string login = _config["Odoo:SyncLogin"]!.Trim();
        string password = _config["Odoo:SyncPassword"]!;
        OdooSession session = await _auth.AuthenticateAsync( login, password );

        // Look back a little for late writes; idempotency handles duplicates.
        DateTime since = sinceUtc.AddHours( -1 );
        string sinceStr = since.ToString( "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture );

        List<object> domain = new()
        {
            new object[] { "state", "in", new[] { "paid", "done", "invoiced" } },
            new object[] { "date_order", ">=", sinceStr },
        };
        if (minOrderIdExclusive is int minId && minId > 0)
        {
            // Still use date window; order id filter alone can miss backdated rows.
            domain.Add( new object[] { "id", ">=", Math.Max( 1, minId - 50 ) } );
        }

        JsonElement orders = await _client.CallKwAsync(
            session,
            "pos.order",
            "search_read",
            [domain.ToArray()],
            new Dictionary<string, object?>
            {
                ["fields"] = new[] { "id", "name", "date_order", "state" },
                ["limit"] = 500,
                ["order"] = "id asc",
            },
            cancellationToken );

        List<PosOrderLine> result = new();
        if (orders.ValueKind != JsonValueKind.Array || orders.GetArrayLength() == 0)
        {
            return result;
        }

        List<(int OrderId, string? Name, DateTime SoldAt)> orderMeta = new();
        foreach (JsonElement order in orders.EnumerateArray())
        {
            int orderId = ReadInt( order, "id" );
            if (orderId <= 0)
            {
                continue;
            }

            orderMeta.Add( (
                orderId,
                ReadString( order, "name" ),
                ReadDateTimeUtc( order, "date_order" ) ) );
        }

        if (orderMeta.Count == 0)
        {
            return result;
        }

        int[] orderIds = orderMeta.Select( x => x.OrderId ).ToArray();
        Dictionary<int, (string? Name, DateTime SoldAt)> byOrder =
            orderMeta.ToDictionary( x => x.OrderId, x => (x.Name, x.SoldAt) );

        JsonElement lines = await _client.CallKwAsync(
            session,
            "pos.order.line",
            "search_read",
            [
                new object[]
                {
                    new object[] { "order_id", "in", orderIds }
                }
            ],
            new Dictionary<string, object?>
            {
                ["fields"] = new[] { "id", "order_id", "product_id", "qty" },
                ["limit"] = 5000,
                ["order"] = "id asc",
            },
            cancellationToken );

        if (lines.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement line in lines.EnumerateArray())
        {
            int lineId = ReadInt( line, "id" );
            int orderId = ReadMany2OneId( line, "order_id" );
            int productId = ReadMany2OneId( line, "product_id" );
            decimal qty = ReadDecimal( line, "qty" );
            if (lineId <= 0 || orderId <= 0 || productId <= 0 || qty <= 0)
            {
                continue;
            }

            if (!byOrder.TryGetValue( orderId, out var meta ))
            {
                continue;
            }

            result.Add( new PosOrderLine(
                orderId,
                meta.Name,
                lineId,
                productId,
                qty,
                meta.SoldAt ) );
        }

        return result;
    }

    private static int ReadInt( JsonElement row, string property )
    {
        if (!row.TryGetProperty( property, out JsonElement value ))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32( out int i ) ? i : 0,
            JsonValueKind.String => int.TryParse( value.GetString(), out int parsed ) ? parsed : 0,
            _ => 0
        };
    }

    private static int ReadMany2OneId( JsonElement row, string property )
    {
        if (!row.TryGetProperty( property, out JsonElement value ))
        {
            return 0;
        }

        if (value.ValueKind is JsonValueKind.False or JsonValueKind.Null)
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt32( out int id ) ? id : 0;
        }

        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 1)
        {
            JsonElement idEl = value[0];
            if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32( out int id ))
            {
                return id;
            }
        }

        return 0;
    }

    private static decimal ReadDecimal( JsonElement row, string property )
    {
        if (!row.TryGetProperty( property, out JsonElement value ))
        {
            return 0m;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal( out decimal d ) ? d : 0m,
            JsonValueKind.String => decimal.TryParse(
                value.GetString(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out decimal parsed )
                ? parsed
                : 0m,
            _ => 0m
        };
    }

    private static string? ReadString( JsonElement row, string property )
    {
        if (!row.TryGetProperty( property, out JsonElement value )
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace( text ) ? null : text;
    }

    private static DateTime ReadDateTimeUtc( JsonElement row, string property )
    {
        string? raw = ReadString( row, property );
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return DateTime.UtcNow;
        }

        if (DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime dt ))
        {
            return DateTime.SpecifyKind( dt, DateTimeKind.Utc );
        }

        return DateTime.UtcNow;
    }
}
