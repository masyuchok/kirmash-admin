using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using backend.Services.Auth;
using Microsoft.AspNetCore.Http;

namespace backend.Services.Odoo;

public sealed class OdooStockReceiptService
{
    public const string DefaultPartnerName = "Kirma.sh";

    private readonly OdooJsonRpcClient _client;
    private readonly IConfiguration _config;

    public OdooStockReceiptService( OdooJsonRpcClient client, IConfiguration config )
    {
        _client = client;
        _config = config;
    }

    public sealed record ReceiptLine( int ProductId, string ProductName, int UomId, decimal Quantity );

    public sealed record ReceiptResult( int PickingId, string PickingName );

    public async Task<ReceiptResult> CreateIncomingReceiptAsync(
        HttpRequest request,
        IReadOnlyList<ReceiptLine> lines,
        CancellationToken cancellationToken = default )
    {
        if (!TryResolveSession( request, out OdooSession session ))
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        if (lines.Count == 0)
        {
            throw new InvalidOperationException( "Дадайце хаця б адну кнігу ў прыёмку." );
        }

        foreach (ReceiptLine line in lines)
        {
            if (line.ProductId <= 0 || line.Quantity <= 0)
            {
                throw new InvalidOperationException( "Некарэктны радок прыёмкі." );
            }
        }

        string partnerName = (_config["Odoo:KirmaPartnerName"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( partnerName ))
        {
            partnerName = DefaultPartnerName;
        }

        int partnerId = await ResolveOrCreatePartnerAsync( session, partnerName, cancellationToken );
        (int pickingTypeId, int locationSrcId, int locationDestId) =
            await ResolveIncomingPickingTypeAsync( session, cancellationToken );

        DateTime scheduled = DateTime.UtcNow;
        JsonElement created = await _client.CallKwAsync(
            session,
            "stock.picking",
            "create",
            [
                new Dictionary<string, object?>
                {
                    ["partner_id"] = partnerId,
                    ["picking_type_id"] = pickingTypeId,
                    ["location_id"] = locationSrcId,
                    ["location_dest_id"] = locationDestId,
                    ["scheduled_date"] = scheduled.ToString(
                        "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture ),
                    ["origin"] = partnerName,
                }
            ],
            null,
            cancellationToken );

        int pickingId = created.ValueKind == JsonValueKind.Number && created.TryGetInt32( out int id )
            ? id
            : 0;
        if (pickingId <= 0)
        {
            throw new InvalidOperationException( "Не ўдалося стварыць прыёмку ў Odoo." );
        }

        // Create moves separately — compatible across Odoo versions
        // (move_ids_without_package is not available in all databases).
        foreach (ReceiptLine line in lines)
        {
            int uomId = line.UomId > 0 ? line.UomId : 1;
            await _client.CallKwAsync(
                session,
                "stock.move",
                "create",
                [
                    new Dictionary<string, object?>
                    {
                        ["product_id"] = line.ProductId,
                        ["product_uom_qty"] = line.Quantity,
                        ["product_uom"] = uomId,
                        ["picking_id"] = pickingId,
                        ["picking_type_id"] = pickingTypeId,
                        ["location_id"] = locationSrcId,
                        ["location_dest_id"] = locationDestId,
                    }
                ],
                null,
                cancellationToken );
        }

        await _client.CallKwAsync(
            session,
            "stock.picking",
            "action_confirm",
            [new[] { pickingId }],
            null,
            cancellationToken );

        await SetMovesDoneQuantityAsync( session, pickingId, cancellationToken );

        await ValidatePickingAsync( session, pickingId, cancellationToken );

        string pickingName = await ReadPickingNameAsync( session, pickingId, cancellationToken )
            ?? $"#{pickingId}";
        return new ReceiptResult( pickingId, pickingName );
    }

    private async Task SetMovesDoneQuantityAsync(
        OdooSession session,
        int pickingId,
        CancellationToken cancellationToken )
    {
        JsonElement moves = await _client.CallKwAsync(
            session,
            "stock.move",
            "search_read",
            [
                new object[]
                {
                    new object[] { "picking_id", "=", pickingId }
                }
            ],
            new Dictionary<string, object?>
            {
                ["fields"] = new[] { "id", "product_uom_qty" },
                ["limit"] = 500,
            },
            cancellationToken );

        if (moves.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement move in moves.EnumerateArray())
        {
            int moveId = ReadInt( move, "id" );
            if (moveId <= 0)
            {
                continue;
            }

            decimal qty = ReadDecimal( move, "product_uom_qty" );
            // Odoo 17+: field is "quantity" (not quantity_done).
            await _client.CallKwAsync(
                session,
                "stock.move",
                "write",
                [new[] { moveId }, new Dictionary<string, object?> { ["quantity"] = qty }],
                null,
                cancellationToken );
        }
    }

    private async Task ValidatePickingAsync(
        OdooSession session,
        int pickingId,
        CancellationToken cancellationToken )
    {
        JsonElement result;
        try
        {
            result = await _client.CallKwAsync(
                session,
                "stock.picking",
                "button_validate",
                [new[] { pickingId }],
                new Dictionary<string, object?>
                {
                    ["context"] = new Dictionary<string, object?>
                    {
                        ["skip_sms"] = true,
                        ["skip_backorder"] = true,
                    }
                },
                cancellationToken );
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Не ўдалося пацвердзіць прыёмку ў Odoo: {ex.Message}",
                ex );
        }

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty( "res_model", out JsonElement modelEl )
            || modelEl.GetString( ) is not string model
            || string.IsNullOrWhiteSpace( model ))
        {
            return;
        }

        if (!string.Equals( model, "stock.immediate.transfer", StringComparison.OrdinalIgnoreCase )
            && !string.Equals( model, "stock.backorder.confirmation", StringComparison.OrdinalIgnoreCase ))
        {
            return;
        }

        Dictionary<string, object?>? kwargs = null;
        if (result.TryGetProperty( "context", out JsonElement contextEl )
            && contextEl.ValueKind == JsonValueKind.Object)
        {
            kwargs = new Dictionary<string, object?>
            {
                ["context"] = JsonElementToObject( contextEl )
            };
        }

        JsonElement created = await _client.CallKwAsync(
            session,
            model,
            "create",
            [new Dictionary<string, object?>()],
            kwargs,
            cancellationToken );

        if (created.ValueKind != JsonValueKind.Number
            || !created.TryGetInt32( out int wizardId )
            || wizardId <= 0)
        {
            throw new InvalidOperationException( "Не ўдалося стварыць wizard пацверджання прыёмкі." );
        }

        string method = string.Equals(
            model,
            "stock.backorder.confirmation",
            StringComparison.OrdinalIgnoreCase )
            ? "process_cancel_backorder"
            : "process";

        try
        {
            await _client.CallKwAsync(
                session,
                model,
                method,
                [new[] { wizardId }],
                null,
                cancellationToken );
        }
        catch when (method != "process")
        {
            await _client.CallKwAsync(
                session,
                model,
                "process",
                [new[] { wizardId }],
                null,
                cancellationToken );
        }
    }

    private static object JsonElementToObject( JsonElement el )
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
            {
                Dictionary<string, object?> dict = new();
                foreach (JsonProperty prop in el.EnumerateObject())
                {
                    dict[prop.Name] = JsonElementToObject( prop.Value );
                }

                return dict;
            }
            case JsonValueKind.Array:
            {
                List<object?> list = new();
                foreach (JsonElement item in el.EnumerateArray())
                {
                    list.Add( JsonElementToObject( item ) );
                }

                return list;
            }
            case JsonValueKind.String:
                return el.GetString( ) ?? string.Empty;
            case JsonValueKind.Number:
                if (el.TryGetInt64( out long l ))
                {
                    return l;
                }

                if (el.TryGetDecimal( out decimal d ))
                {
                    return d;
                }

                return 0;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null!;
        }
    }

    private async Task<int> ResolveOrCreatePartnerAsync(
        OdooSession session,
        string partnerName,
        CancellationToken cancellationToken )
    {
        JsonElement found = await _client.CallKwAsync(
            session,
            "res.partner",
            "search_read",
            [
                new object[]
                {
                    new object[] { "name", "=", partnerName }
                }
            ],
            new Dictionary<string, object?>
            {
                ["fields"] = new[] { "id", "name" },
                ["limit"] = 1,
            },
            cancellationToken );

        if (found.ValueKind == JsonValueKind.Array && found.GetArrayLength() > 0)
        {
            int id = ReadInt( found[0], "id" );
            if (id > 0)
            {
                return id;
            }
        }

        // Soft match.
        JsonElement soft = await _client.CallKwAsync(
            session,
            "res.partner",
            "search_read",
            [
                new object[]
                {
                    new object[] { "name", "ilike", partnerName }
                }
            ],
            new Dictionary<string, object?>
            {
                ["fields"] = new[] { "id", "name" },
                ["limit"] = 1,
            },
            cancellationToken );

        if (soft.ValueKind == JsonValueKind.Array && soft.GetArrayLength() > 0)
        {
            int id = ReadInt( soft[0], "id" );
            if (id > 0)
            {
                return id;
            }
        }

        JsonElement created = await _client.CallKwAsync(
            session,
            "res.partner",
            "create",
            [
                new Dictionary<string, object?>
                {
                    ["name"] = partnerName,
                    ["supplier_rank"] = 1,
                    ["company_type"] = "company",
                }
            ],
            null,
            cancellationToken );

        if (created.ValueKind == JsonValueKind.Number && created.TryGetInt32( out int newId ) && newId > 0)
        {
            return newId;
        }

        throw new InvalidOperationException( $"Не ўдалося знайсці або стварыць партнёра «{partnerName}» у Odoo." );
    }

    private async Task<(int PickingTypeId, int LocationSrcId, int LocationDestId)> ResolveIncomingPickingTypeAsync(
        OdooSession session,
        CancellationToken cancellationToken )
    {
        JsonElement types = await _client.CallKwAsync(
            session,
            "stock.picking.type",
            "search_read",
            [
                new object[]
                {
                    new object[] { "code", "=", "incoming" }
                }
            ],
            new Dictionary<string, object?>
            {
                ["fields"] = new[]
                {
                    "id",
                    "name",
                    "default_location_src_id",
                    "default_location_dest_id",
                },
                ["limit"] = 1,
                ["order"] = "id asc",
            },
            cancellationToken );

        if (types.ValueKind != JsonValueKind.Array || types.GetArrayLength() == 0)
        {
            throw new InvalidOperationException( "У Odoo не знойдзены тып аперацыі прыёмкі (incoming)." );
        }

        JsonElement row = types[0];
        int pickingTypeId = ReadInt( row, "id" );
        int src = ReadMany2OneId( row, "default_location_src_id" );
        int dest = ReadMany2OneId( row, "default_location_dest_id" );

        if (src <= 0)
        {
            src = await ResolveLocationByUsageAsync( session, "supplier", cancellationToken );
        }

        if (dest <= 0)
        {
            dest = await ResolveLocationByUsageAsync( session, "internal", cancellationToken );
        }

        if (pickingTypeId <= 0 || src <= 0 || dest <= 0)
        {
            throw new InvalidOperationException( "Не ўдалося вызначыць лакацыі для прыёмкі Odoo." );
        }

        return (pickingTypeId, src, dest);
    }

    private async Task<int> ResolveLocationByUsageAsync(
        OdooSession session,
        string usage,
        CancellationToken cancellationToken )
    {
        JsonElement rows = await _client.CallKwAsync(
            session,
            "stock.location",
            "search_read",
            [
                new object[]
                {
                    new object[] { "usage", "=", usage }
                }
            ],
            new Dictionary<string, object?>
            {
                ["fields"] = new[] { "id" },
                ["limit"] = 1,
                ["order"] = "id asc",
            },
            cancellationToken );

        if (rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0)
        {
            return ReadInt( rows[0], "id" );
        }

        return 0;
    }

    private async Task<string?> ReadPickingNameAsync(
        OdooSession session,
        int pickingId,
        CancellationToken cancellationToken )
    {
        JsonElement rows = await _client.CallKwAsync(
            session,
            "stock.picking",
            "search_read",
            [
                new object[]
                {
                    new object[] { "id", "=", pickingId }
                }
            ],
            new Dictionary<string, object?>
            {
                ["fields"] = new[] { "id", "name" },
                ["limit"] = 1,
            },
            cancellationToken );

        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
        {
            return null;
        }

        return ReadString( rows[0], "name" );
    }

    private bool TryResolveSession( HttpRequest request, out OdooSession session )
    {
        session = null!;
        ClaimsPrincipal? principal = BukinistkaJwtAuthentication.TryValidateCookie( request, _config );
        return principal is not null && OdooSessionReader.TryGetFromPrincipal( principal, out session );
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
}
