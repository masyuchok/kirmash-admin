using System.Globalization;
using System.Text.Json;
using backend.Services.Auth;

namespace backend.Services.Odoo;

/// <summary>
/// Creates Odoo outgoing pickings (Wydanie) for Kirma.sh — used when Shopify sells
/// consignment stock that Bukinistka accepted with SyncOnSale.
/// </summary>
public sealed class OdooStockDeliveryService
{
    public const string DefaultPartnerName = "Kirma.sh";

    private readonly OdooJsonRpcClient _client;
    private readonly IConfiguration _config;

    public OdooStockDeliveryService( OdooJsonRpcClient client, IConfiguration config )
    {
        _client = client;
        _config = config;
    }

    public sealed record DeliveryLine( int ProductId, int UomId, decimal Quantity );

    public sealed record DeliveryResult( int PickingId, string PickingName );

    public async Task<DeliveryResult> CreateOutgoingDeliveryAsync(
        OdooSession session,
        IReadOnlyList<DeliveryLine> lines,
        string note,
        CancellationToken cancellationToken = default )
    {
        if (lines.Count == 0)
        {
            throw new InvalidOperationException( "Няма радкоў для Wydanie." );
        }

        foreach (DeliveryLine line in lines)
        {
            if (line.ProductId <= 0 || line.Quantity <= 0)
            {
                throw new InvalidOperationException( "Некарэктны радок Wydanie." );
            }
        }

        string partnerName = (_config["Odoo:KirmaPartnerName"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( partnerName ))
        {
            partnerName = DefaultPartnerName;
        }

        int partnerId = await ResolveOrCreatePartnerAsync( session, partnerName, cancellationToken );
        (int pickingTypeId, int locationSrcId, int locationDestId) =
            await ResolveOutgoingPickingTypeAsync( session, cancellationToken );

        string noteText = (note ?? string.Empty).Trim();
        DateTime scheduled = DateTime.UtcNow;

        Dictionary<string, object?> pickingVals = new()
        {
            ["partner_id"] = partnerId,
            ["picking_type_id"] = pickingTypeId,
            ["location_id"] = locationSrcId,
            ["location_dest_id"] = locationDestId,
            ["scheduled_date"] = scheduled.ToString(
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture ),
            ["origin"] = string.IsNullOrWhiteSpace( noteText ) ? partnerName : noteText,
        };
        if (!string.IsNullOrWhiteSpace( noteText ))
        {
            pickingVals["note"] = noteText;
        }

        JsonElement created = await _client.CallKwAsync(
            session,
            "stock.picking",
            "create",
            [pickingVals],
            null,
            cancellationToken );

        int pickingId = created.ValueKind == JsonValueKind.Number && created.TryGetInt32( out int id )
            ? id
            : 0;
        if (pickingId <= 0)
        {
            throw new InvalidOperationException( "Не ўдалося стварыць Wydanie ў Odoo." );
        }

        foreach (DeliveryLine line in lines)
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
        return new DeliveryResult( pickingId, pickingName );
    }

    /// <summary>Cancel an outgoing picking (Wydanie), reversing stock if already validated.</summary>
    public async Task CancelOutgoingDeliveryAsync(
        OdooSession session,
        int pickingId,
        CancellationToken cancellationToken = default )
    {
        if (pickingId <= 0)
        {
            return;
        }

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
                ["fields"] = new[] { "id", "state" },
                ["limit"] = 1,
            },
            cancellationToken );

        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
        {
            return;
        }

        string? state = ReadString( rows[0], "state" );
        if (string.Equals( state, "cancel", StringComparison.OrdinalIgnoreCase ))
        {
            return;
        }

        // Done pickings cannot be cancelled — create a return to restore stock.
        if (string.Equals( state, "done", StringComparison.OrdinalIgnoreCase ))
        {
            await CreateReturnForDonePickingAsync( session, pickingId, cancellationToken );
            return;
        }

        try
        {
            await _client.CallKwAsync(
                session,
                "stock.picking",
                "action_cancel",
                [new[] { pickingId }],
                null,
                cancellationToken );
        }
        catch (Exception ex)
        {
            // Some intermediate states still require a return.
            try
            {
                await CreateReturnForDonePickingAsync( session, pickingId, cancellationToken );
                return;
            }
            catch
            {
                throw new InvalidOperationException(
                    $"Не ўдалося адмяніць Wydanie #{pickingId} у Odoo: {ex.Message}",
                    ex );
            }
        }

        // Verify; if still not cancelled (e.g. partially done), fall back to return.
        JsonElement after = await _client.CallKwAsync(
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
                ["fields"] = new[] { "id", "state" },
                ["limit"] = 1,
            },
            cancellationToken );

        string? stateAfter = after.ValueKind == JsonValueKind.Array && after.GetArrayLength() > 0
            ? ReadString( after[0], "state" )
            : null;
        if (string.Equals( stateAfter, "done", StringComparison.OrdinalIgnoreCase ))
        {
            await CreateReturnForDonePickingAsync( session, pickingId, cancellationToken );
        }
    }

    private async Task CreateReturnForDonePickingAsync(
        OdooSession session,
        int pickingId,
        CancellationToken cancellationToken )
    {
        Dictionary<string, object?> context = new()
        {
            ["active_id"] = pickingId,
            ["active_ids"] = new[] { pickingId },
            ["active_model"] = "stock.picking",
        };

        // Odoo 17+: create({}) with active picking in context fills return lines.
        JsonElement wizardIdEl = await _client.CallKwAsync(
            session,
            "stock.return.picking",
            "create",
            [new Dictionary<string, object?>()],
            new Dictionary<string, object?> { ["context"] = context },
            cancellationToken );

        if (wizardIdEl.ValueKind != JsonValueKind.Number
            || !wizardIdEl.TryGetInt32( out int wizardId )
            || wizardId <= 0)
        {
            // Fallback: explicit picking_id (older Odoo).
            wizardIdEl = await _client.CallKwAsync(
                session,
                "stock.return.picking",
                "create",
                [
                    new Dictionary<string, object?>
                    {
                        ["picking_id"] = pickingId,
                    }
                ],
                new Dictionary<string, object?> { ["context"] = context },
                cancellationToken );

            if (wizardIdEl.ValueKind != JsonValueKind.Number
                || !wizardIdEl.TryGetInt32( out wizardId )
                || wizardId <= 0)
            {
                throw new InvalidOperationException(
                    $"Не ўдалося стварыць wizard вяртання для Wydanie #{pickingId}." );
            }
        }

        // Method names differ across Odoo versions.
        string[] methods =
        [
            "action_create_returns",
            "action_create_returns_all",
            "create_returns",
            "_create_returns",
        ];

        JsonElement? result = null;
        List<string> errors = new();
        foreach (string method in methods)
        {
            try
            {
                result = await _client.CallKwAsync(
                    session,
                    "stock.return.picking",
                    method,
                    [new[] { wizardId }],
                    new Dictionary<string, object?> { ["context"] = context },
                    cancellationToken );
                break;
            }
            catch (Exception ex)
            {
                errors.Add( $"{method}: {ex.Message}" );
            }
        }

        if (result is null)
        {
            throw new InvalidOperationException(
                $"Не ўдалося стварыць вяртанне па Wydanie #{pickingId}: "
                + string.Join( " | ", errors ) );
        }

        int returnPickingId = TryReadResId( result.Value );
        if (returnPickingId <= 0)
        {
            returnPickingId = await FindLatestReturnPickingIdAsync(
                session,
                pickingId,
                cancellationToken );
        }

        if (returnPickingId <= 0)
        {
            throw new InvalidOperationException(
                $"Вяртанне для Wydanie #{pickingId} не створана (няма id прыёмкі вяртання)." );
        }

        try
        {
            await _client.CallKwAsync(
                session,
                "stock.picking",
                "action_confirm",
                [new[] { returnPickingId }],
                null,
                cancellationToken );
        }
        catch
        {
            // May already be confirmed.
        }

        await SetMovesDoneQuantityAsync( session, returnPickingId, cancellationToken );
        await ValidatePickingAsync( session, returnPickingId, cancellationToken );
    }

    private async Task<int> FindLatestReturnPickingIdAsync(
        OdooSession session,
        int originPickingId,
        CancellationToken cancellationToken )
    {
        JsonElement rows = await _client.CallKwAsync(
            session,
            "stock.picking",
            "search_read",
            [
                new object[]
                {
                    new object[] { "return_id", "=", originPickingId }
                }
            ],
            new Dictionary<string, object?>
            {
                ["fields"] = new[] { "id" },
                ["limit"] = 1,
                ["order"] = "id desc",
            },
            cancellationToken );

        if (rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0)
        {
            return ReadInt( rows[0], "id" );
        }

        // Older fields: origin_returned_picking_id on moves / picking
        JsonElement alt = await _client.CallKwAsync(
            session,
            "stock.picking",
            "search_read",
            [
                new object[]
                {
                    new object[] { "origin_returned_picking_id", "=", originPickingId }
                }
            ],
            new Dictionary<string, object?>
            {
                ["fields"] = new[] { "id" },
                ["limit"] = 1,
                ["order"] = "id desc",
            },
            cancellationToken );

        if (alt.ValueKind == JsonValueKind.Array && alt.GetArrayLength() > 0)
        {
            return ReadInt( alt[0], "id" );
        }

        return 0;
    }

    private static int TryReadResId( JsonElement result )
    {
        if (result.ValueKind == JsonValueKind.Number && result.TryGetInt32( out int direct ))
        {
            return direct;
        }

        if (result.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (result.TryGetProperty( "res_id", out JsonElement resIdEl ))
        {
            if (resIdEl.ValueKind == JsonValueKind.Number && resIdEl.TryGetInt32( out int id ))
            {
                return id;
            }
        }

        // Sometimes nested in domain / action
        if (result.TryGetProperty( "res_ids", out JsonElement resIdsEl )
            && resIdsEl.ValueKind == JsonValueKind.Array
            && resIdsEl.GetArrayLength() > 0
            && resIdsEl[0].ValueKind == JsonValueKind.Number
            && resIdsEl[0].TryGetInt32( out int first ))
        {
            return first;
        }

        return 0;
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
                $"Не ўдалося пацвердзіць Wydanie ў Odoo: {ex.Message}",
                ex );
        }

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty( "res_model", out JsonElement modelEl )
            || modelEl.GetString() is not string model
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
            throw new InvalidOperationException( "Не ўдалося стварыць wizard пацверджання Wydanie." );
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
                return el.GetString() ?? string.Empty;
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
                    ["customer_rank"] = 1,
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

    private async Task<(int PickingTypeId, int LocationSrcId, int LocationDestId)> ResolveOutgoingPickingTypeAsync(
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
                    new object[] { "code", "=", "outgoing" }
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
                ["limit"] = 20,
                ["order"] = "id asc",
            },
            cancellationToken );

        if (types.ValueKind != JsonValueKind.Array || types.GetArrayLength() == 0)
        {
            throw new InvalidOperationException( "У Odoo не знойдзены тып аперацыі Wydanie (outgoing)." );
        }

        JsonElement? preferred = null;
        foreach (JsonElement row in types.EnumerateArray())
        {
            string name = ReadString( row, "name" ) ?? string.Empty;
            if (name.Contains( "Wydan", StringComparison.OrdinalIgnoreCase )
                || name.Contains( "Delivery", StringComparison.OrdinalIgnoreCase ))
            {
                preferred = row;
                break;
            }
        }

        JsonElement chosen = preferred ?? types[0];
        int pickingTypeId = ReadInt( chosen, "id" );
        int src = ReadMany2OneId( chosen, "default_location_src_id" );
        int dest = ReadMany2OneId( chosen, "default_location_dest_id" );

        if (src <= 0)
        {
            src = await ResolveLocationByUsageAsync( session, "internal", cancellationToken );
        }

        if (dest <= 0)
        {
            dest = await ResolveLocationByUsageAsync( session, "customer", cancellationToken );
        }

        if (pickingTypeId <= 0 || src <= 0 || dest <= 0)
        {
            throw new InvalidOperationException( "Не ўдалося вызначыць лакацыі для Wydanie Odoo." );
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

    public async Task<int> ResolveProductUomIdAsync(
        OdooSession session,
        int productId,
        CancellationToken cancellationToken )
    {
        if (productId <= 0)
        {
            return 1;
        }

        JsonElement rows = await _client.CallKwAsync(
            session,
            "product.product",
            "search_read",
            [
                new object[]
                {
                    new object[] { "id", "=", productId }
                }
            ],
            new Dictionary<string, object?>
            {
                ["fields"] = new[] { "id", "uom_id" },
                ["limit"] = 1,
            },
            cancellationToken );

        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
        {
            return 1;
        }

        int uom = ReadMany2OneId( rows[0], "uom_id" );
        return uom > 0 ? uom : 1;
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
