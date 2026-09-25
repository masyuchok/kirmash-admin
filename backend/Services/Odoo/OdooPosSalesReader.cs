using System.Globalization;
using System.Text.Json;

namespace backend.Services.Odoo;

public sealed class OdooPosSalesReader
{
    private readonly OdooJsonRpcClient _client;
    private readonly OdooAuthService _auth;
    private readonly IConfiguration _config;
    private readonly ILogger<OdooPosSalesReader> _logger;

    public OdooPosSalesReader(
        OdooJsonRpcClient client,
        OdooAuthService auth,
        IConfiguration config,
        ILogger<OdooPosSalesReader> logger )
    {
        _client = client;
        _auth = auth;
        _config = config;
        _logger = logger;
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

        List<PosOrderLine> result = new();
        List<(int OrderId, string? Name, DateTime SoldAt)> orderMeta = new();
        const int orderPageSize = 500;
        for (int offset = 0; ; offset += orderPageSize)
        {
            JsonElement orders = await _client.CallKwAsync(
                session,
                "pos.order",
                "search_read",
                [domain.ToArray()],
                new Dictionary<string, object?>
                {
                    ["fields"] = new[] { "id", "name", "date_order", "state" },
                    ["limit"] = orderPageSize,
                    ["offset"] = offset,
                    ["order"] = "id asc",
                },
                cancellationToken );

            if (orders.ValueKind != JsonValueKind.Array || orders.GetArrayLength() == 0)
            {
                break;
            }

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

            if (orders.GetArrayLength() < orderPageSize)
            {
                break;
            }
        }

        Dictionary<int, (string? Name, DateTime SoldAt)> byOrder =
            orderMeta.ToDictionary( x => x.OrderId, x => (x.Name, x.SoldAt) );

        foreach (int[] orderIds in orderMeta
                     .Select( x => x.OrderId )
                     .Distinct()
                     .Chunk( 400 ))
        {
            const int linePageSize = 5000;
            for (int offset = 0; ; offset += linePageSize)
            {
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
                        ["limit"] = linePageSize,
                        ["offset"] = offset,
                        ["order"] = "id asc",
                    },
                    cancellationToken );

                if (lines.ValueKind != JsonValueKind.Array || lines.GetArrayLength() == 0)
                {
                    break;
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

                if (lines.GetArrayLength() < linePageSize)
                {
                    break;
                }
            }
        }

        // Some Odoo installations expose completed POS stock pickings to the sync
        // account but omit the corresponding pos.order/pos.order.line records.
        // Use WH/POS stock moves as a fallback and deduplicate against normal POS lines.
        try
        {
            await AppendCompletedPosStockMovesAsync(
                session,
                since,
                result,
                cancellationToken );
        }
        catch (Exception ex)
        {
            // Normal pos.order lines remain usable when the sync account cannot
            // read stock pickings/moves.
            _logger.LogWarning( ex, "Could not read completed WH/POS stock moves." );
        }

        return result;
    }

    /// <summary>
    /// POS return lines (negative qty) since <paramref name="sinceUtc"/>.
    /// </summary>
    public async Task<List<PosOrderLine>> FetchReturnLinesSinceAsync(
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

        DateTime since = sinceUtc.AddHours( -1 );
        string sinceStr = since.ToString( "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture );

        List<object> domain = new()
        {
            new object[] { "state", "in", new[] { "paid", "done", "invoiced" } },
            new object[] { "date_order", ">=", sinceStr },
        };
        if (minOrderIdExclusive is int minId && minId > 0)
        {
            domain.Add( new object[] { "id", ">=", Math.Max( 1, minId - 50 ) } );
        }

        List<PosOrderLine> result = new();
        List<(int OrderId, string? Name, DateTime SoldAt)> orderMeta = new();
        const int orderPageSize = 500;
        for (int offset = 0; ; offset += orderPageSize)
        {
            JsonElement orders = await _client.CallKwAsync(
                session,
                "pos.order",
                "search_read",
                [domain.ToArray()],
                new Dictionary<string, object?>
                {
                    ["fields"] = new[] { "id", "name", "date_order", "state" },
                    ["limit"] = orderPageSize,
                    ["offset"] = offset,
                    ["order"] = "id asc",
                },
                cancellationToken );

            if (orders.ValueKind != JsonValueKind.Array || orders.GetArrayLength() == 0)
            {
                break;
            }

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

            if (orders.GetArrayLength() < orderPageSize)
            {
                break;
            }
        }

        if (orderMeta.Count == 0)
        {
            return result;
        }

        Dictionary<int, (string? Name, DateTime SoldAt)> byOrder =
            orderMeta.ToDictionary( x => x.OrderId, x => (x.Name, x.SoldAt) );

        foreach (int[] orderIds in orderMeta
                     .Select( x => x.OrderId )
                     .Distinct()
                     .Chunk( 400 ))
        {
            const int linePageSize = 5000;
            for (int offset = 0; ; offset += linePageSize)
            {
                JsonElement lines = await _client.CallKwAsync(
                    session,
                    "pos.order.line",
                    "search_read",
                    [
                        new object[]
                        {
                            new object[] { "order_id", "in", orderIds },
                            new object[] { "qty", "<", 0 },
                        }
                    ],
                    new Dictionary<string, object?>
                    {
                        ["fields"] = new[] { "id", "order_id", "product_id", "qty" },
                        ["limit"] = linePageSize,
                        ["offset"] = offset,
                        ["order"] = "id asc",
                    },
                    cancellationToken );

                if (lines.ValueKind != JsonValueKind.Array || lines.GetArrayLength() == 0)
                {
                    break;
                }

                foreach (JsonElement line in lines.EnumerateArray())
                {
                    int lineId = ReadInt( line, "id" );
                    int orderId = ReadMany2OneId( line, "order_id" );
                    int productId = ReadMany2OneId( line, "product_id" );
                    decimal qty = ReadDecimal( line, "qty" );
                    if (lineId <= 0 || orderId <= 0 || productId <= 0 || qty >= 0)
                    {
                        continue;
                    }

                    if (!byOrder.TryGetValue( orderId, out var meta ))
                    {
                        continue;
                    }

                    // Store absolute quantity; callers treat these as returns.
                    result.Add( new PosOrderLine(
                        orderId,
                        meta.Name,
                        lineId,
                        productId,
                        Math.Abs( qty ),
                        meta.SoldAt ) );
                }

                if (lines.GetArrayLength() < linePageSize)
                {
                    break;
                }
            }
        }

        return result;
    }

    private async Task AppendCompletedPosStockMovesAsync(
        OdooSession session,
        DateTime sinceUtc,
        List<PosOrderLine> result,
        CancellationToken cancellationToken )
    {
        string sinceStr = sinceUtc.ToString(
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture );
        HashSet<int> matchedRegularIndexes = new();

        const int movePageSize = 5000;
        for (int offset = 0; ; offset += movePageSize)
        {
            JsonElement moves = await _client.CallKwAsync(
                session,
                "stock.move",
                "search_read",
                [
                    new object[]
                    {
                        new object[] { "state", "=", "done" },
                        new object[] { "date", ">=", sinceStr },
                        new object[] { "reference", "=like", "WH/POS/%" },
                    }
                ],
                new Dictionary<string, object?>
                {
                    ["fields"] = new[] { "id", "reference", "date", "product_id", "quantity" },
                    ["limit"] = movePageSize,
                    ["offset"] = offset,
                    ["order"] = "id asc",
                },
                cancellationToken );

            if (moves.ValueKind != JsonValueKind.Array || moves.GetArrayLength() == 0)
            {
                break;
            }

            foreach (JsonElement move in moves.EnumerateArray())
            {
                int moveId = ReadInt( move, "id" );
                int productId = ReadMany2OneId( move, "product_id" );
                decimal qty = ReadDecimal( move, "quantity" );
                string? reference = ReadString( move, "reference" );
                DateTime movedAt = ReadDateTimeUtc( move, "date" );
                if (moveId <= 0
                    || productId <= 0
                    || qty <= 0
                    || string.IsNullOrWhiteSpace( reference )
                    || !reference.StartsWith( "WH/POS/", StringComparison.OrdinalIgnoreCase ))
                {
                    continue;
                }

                // A normal POS line and its stock move usually differ by only
                // minutes. Consume one matching normal line so fallback rows do
                // not double-count repeated sales of the same product.
                int regularIndex = -1;
                for (int i = 0; i < result.Count; i++)
                {
                    if (matchedRegularIndexes.Contains( i ))
                    {
                        continue;
                    }

                    PosOrderLine regular = result[i];
                    if (regular.LineId <= 0
                        || regular.ProductId != productId
                        || regular.Quantity != qty
                        || Math.Abs( (regular.SoldAtUtc - movedAt).TotalHours ) > 12)
                    {
                        continue;
                    }

                    regularIndex = i;
                    break;
                }

                if (regularIndex >= 0)
                {
                    matchedRegularIndexes.Add( regularIndex );
                    continue;
                }

                // Negative ids keep the stock-move fallback namespace separate
                // from positive pos.order/pos.order.line ids.
                result.Add( new PosOrderLine(
                    -moveId,
                    reference,
                    -moveId,
                    productId,
                    qty,
                    movedAt ) );
            }

            if (moves.GetArrayLength() < movePageSize)
            {
                break;
            }
        }
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
