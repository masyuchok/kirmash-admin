using System.Security.Claims;
using System.Text.Json;
using backend.Models;
using backend.Services.Auth;
using Microsoft.AspNetCore.Http;

namespace backend.Services.Odoo;

public sealed class OdooProductService
{
    private readonly OdooJsonRpcClient _client;
    private readonly IConfiguration _config;

    public OdooProductService( OdooJsonRpcClient client, IConfiguration config )
    {
        _client = client;
        _config = config;
    }

    public async Task<OdooProductListResponse> ListProductsAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default )
    {
        if (!TryResolveSession( request, out OdooSession session ))
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        object[] domain =
        [
            new object[] { "active", "=", true }
        ];

        Dictionary<string, object?> kwargs = new()
        {
            ["fields"] = new[]
            {
                "id",
                "product_tmpl_id",
                "display_name",
                "name",
                "default_code",
                "barcode",
                "qty_available",
                "list_price",
                "standard_price",
                "uom_id",
            },
            ["limit"] = 5000,
            ["order"] = "name asc",
        };

        JsonElement result = await _client.CallKwAsync(
            session,
            "product.product",
            "search_read",
            [domain],
            kwargs,
            cancellationToken );

        string odooBaseUrl = _client.ConfiguredBaseUrl;
        List<ProductRow> productRows = new();
        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement row in result.EnumerateArray())
            {
                int id = ReadInt( row, "id" );
                if (id <= 0)
                {
                    continue;
                }

                string name = ReadString( row, "display_name" )
                    ?? ReadString( row, "name" )
                    ?? $"#{id}";

                productRows.Add( new ProductRow(
                    id,
                    ReadMany2OneId( row, "product_tmpl_id" ),
                    name,
                    ReadOptionalString( row, "default_code" ),
                    ReadOptionalString( row, "barcode" ),
                    ReadDecimal( row, "qty_available" ),
                    ReadDecimal( row, "list_price" ),
                    ReadDecimal( row, "standard_price" ),
                    ReadMany2OneName( row, "uom_id" )
                ) );
            }
        }

        Dictionary<(int ProductId, int TemplateId), string> suppliersByKey =
            await LoadSupplierNamesAsync( session, productRows, cancellationToken );

        List<OdooProductListItem> products = productRows
            .Select( row =>
            {
                string? supplierName = null;
                if (suppliersByKey.TryGetValue( (row.Id, row.TemplateId), out string? byVariant ))
                {
                    supplierName = byVariant;
                }
                else if (row.TemplateId > 0
                         && suppliersByKey.TryGetValue( (0, row.TemplateId), out string? byTemplate ))
                {
                    supplierName = byTemplate;
                }

                return new OdooProductListItem
                {
                    Id = row.Id,
                    Name = row.Name,
                    DefaultCode = row.DefaultCode,
                    Barcode = row.Barcode,
                    QuantityInStock = row.QuantityInStock,
                    ListPrice = row.ListPrice,
                    StandardPrice = row.StandardPrice,
                    UomName = row.UomName,
                    SupplierName = supplierName,
                    OdooUrl = BuildProductUrl( odooBaseUrl, row.Id ),
                };
            } )
            .ToList();

        return new OdooProductListResponse { Products = products };
    }

    public sealed record OdooProductSnapshot(
        int Id,
        string Name,
        int UomId,
        decimal QuantityInStock,
        decimal ListPrice,
        decimal StandardPrice );

    public async Task<OdooProductSnapshot> GetProductSnapshotAsync(
        HttpRequest request,
        int productId,
        CancellationToken cancellationToken = default )
    {
        if (!TryResolveSession( request, out OdooSession session ))
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        return await GetProductSnapshotAsync( session, productId, cancellationToken );
    }

    public async Task UpdateListPriceAsync(
        HttpRequest request,
        int productId,
        decimal listPrice,
        CancellationToken cancellationToken = default )
    {
        if (!TryResolveSession( request, out OdooSession session ))
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        if (productId <= 0)
        {
            throw new InvalidOperationException( "Некарэктны ідэнтыфікатар прадукта Odoo." );
        }

        if (listPrice < 0m)
        {
            throw new InvalidOperationException( "Цана продажу не можа быць адмоўнай." );
        }

        decimal rounded = Math.Round( listPrice, 2, MidpointRounding.AwayFromZero );
        await _client.CallKwAsync(
            session,
            "product.product",
            "write",
            [new[] { productId }, new Dictionary<string, object?> { ["list_price"] = rounded }],
            null,
            cancellationToken );
    }

    public async Task UpdateStandardPriceAsync(
        HttpRequest request,
        int productId,
        decimal standardPrice,
        CancellationToken cancellationToken = default )
    {
        if (!TryResolveSession( request, out OdooSession session ))
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        if (productId <= 0)
        {
            throw new InvalidOperationException( "Некарэктны ідэнтыфікатар прадукта Odoo." );
        }

        if (standardPrice < 0m)
        {
            throw new InvalidOperationException( "Кошт закупкі не можа быць адмоўным." );
        }

        decimal rounded = Math.Round( standardPrice, 2, MidpointRounding.AwayFromZero );
        await _client.CallKwAsync(
            session,
            "product.product",
            "write",
            [new[] { productId }, new Dictionary<string, object?> { ["standard_price"] = rounded }],
            null,
            cancellationToken );
    }

    public async Task IncreaseQuantityAsync(
        HttpRequest request,
        int productId,
        int delta,
        CancellationToken cancellationToken = default )
    {
        if (!TryResolveSession( request, out OdooSession session ))
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        if (productId <= 0)
        {
            throw new InvalidOperationException( "Некарэктны ідэнтыфікатар прадукта Odoo." );
        }

        if (delta <= 0)
        {
            throw new InvalidOperationException( "Колькасць для дадавання павінна быць больш за нуль." );
        }

        OdooProductSnapshot snapshot = await GetProductSnapshotAsync( session, productId, cancellationToken );
        decimal targetQty = snapshot.QuantityInStock + delta;

        int? quantId = await FindInternalQuantIdAsync( session, productId, cancellationToken );
        if (quantId is null)
        {
            int locationId = await ResolveStockLocationIdAsync( session, cancellationToken );
            JsonElement created = await _client.CallKwAsync(
                session,
                "stock.quant",
                "create",
                [
                    new Dictionary<string, object?>
                    {
                        ["product_id"] = productId,
                        ["location_id"] = locationId,
                        ["inventory_quantity"] = targetQty,
                    }
                ],
                null,
                cancellationToken );

            quantId = created.ValueKind == JsonValueKind.Number && created.TryGetInt32( out int id )
                ? id
                : null;
            if (quantId is null or <= 0)
            {
                throw new InvalidOperationException( "Не ўдалося стварыць складскі запіс у Odoo." );
            }
        }
        else
        {
            await _client.CallKwAsync(
                session,
                "stock.quant",
                "write",
                [
                    new[] { quantId.Value },
                    new Dictionary<string, object?> { ["inventory_quantity"] = targetQty }
                ],
                null,
                cancellationToken );
        }

        await _client.CallKwAsync(
            session,
            "stock.quant",
            "action_apply_inventory",
            [new[] { quantId.Value }],
            null,
            cancellationToken );
    }

    private async Task<OdooProductSnapshot> GetProductSnapshotAsync(
        OdooSession session,
        int productId,
        CancellationToken cancellationToken )
    {
        if (productId <= 0)
        {
            throw new InvalidOperationException( "Некарэктны ідэнтыфікатар прадукта Odoo." );
        }

        object[] domain =
        [
            new object[] { "id", "=", productId }
        ];

        Dictionary<string, object?> kwargs = new()
        {
            ["fields"] = new[]
            {
                "id",
                "display_name",
                "name",
                "uom_id",
                "qty_available",
                "list_price",
                "standard_price",
            },
            ["limit"] = 1,
        };

        JsonElement result = await _client.CallKwAsync(
            session,
            "product.product",
            "search_read",
            [domain],
            kwargs,
            cancellationToken );

        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
        {
            throw new InvalidOperationException( "Прадукт Odoo не знойдзены." );
        }

        JsonElement row = result[0];
        string name = ReadString( row, "display_name" )
            ?? ReadString( row, "name" )
            ?? $"#{ReadInt( row, "id" )}";
        int uomId = ReadMany2OneId( row, "uom_id" );
        if (uomId <= 0)
        {
            uomId = 1;
        }

        return new OdooProductSnapshot(
            ReadInt( row, "id" ),
            name,
            uomId,
            ReadDecimal( row, "qty_available" ),
            ReadDecimal( row, "list_price" ),
            ReadDecimal( row, "standard_price" ) );
    }

    private async Task<int?> FindInternalQuantIdAsync(
        OdooSession session,
        int productId,
        CancellationToken cancellationToken )
    {
        object[] domain =
        [
            new object[] { "product_id", "=", productId },
            new object[] { "location_id.usage", "=", "internal" },
        ];

        Dictionary<string, object?> kwargs = new()
        {
            ["fields"] = new[] { "id", "quantity", "location_id" },
            ["limit"] = 1,
            ["order"] = "quantity desc, id asc",
        };

        JsonElement result = await _client.CallKwAsync(
            session,
            "stock.quant",
            "search_read",
            [domain],
            kwargs,
            cancellationToken );

        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
        {
            return null;
        }

        int id = ReadInt( result[0], "id" );
        return id > 0 ? id : null;
    }

    private async Task<int> ResolveStockLocationIdAsync(
        OdooSession session,
        CancellationToken cancellationToken )
    {
        object[] domain =
        [
            new object[] { "usage", "=", "internal" },
            new object[] { "barcode", "!=", false },
        ];

        // Prefer stock location; fall back to any internal location.
        Dictionary<string, object?> kwargs = new()
        {
            ["fields"] = new[] { "id", "complete_name" },
            ["limit"] = 1,
            ["order"] = "id asc",
        };

        JsonElement withBarcode = await _client.CallKwAsync(
            session,
            "stock.location",
            "search_read",
            [domain],
            kwargs,
            cancellationToken );

        if (withBarcode.ValueKind == JsonValueKind.Array && withBarcode.GetArrayLength() > 0)
        {
            int id = ReadInt( withBarcode[0], "id" );
            if (id > 0)
            {
                return id;
            }
        }

        object[] fallbackDomain =
        [
            new object[] { "usage", "=", "internal" }
        ];

        JsonElement anyInternal = await _client.CallKwAsync(
            session,
            "stock.location",
            "search_read",
            [fallbackDomain],
            kwargs,
            cancellationToken );

        if (anyInternal.ValueKind == JsonValueKind.Array && anyInternal.GetArrayLength() > 0)
        {
            int id = ReadInt( anyInternal[0], "id" );
            if (id > 0)
            {
                return id;
            }
        }

        throw new InvalidOperationException( "Не знойдзена ўнутраная лакацыя складу ў Odoo." );
    }

    private async Task<Dictionary<(int ProductId, int TemplateId), string>> LoadSupplierNamesAsync(
        OdooSession session,
        List<ProductRow> products,
        CancellationToken cancellationToken )
    {
        Dictionary<(int ProductId, int TemplateId), string> result = new();
        if (products.Count == 0)
        {
            return result;
        }

        int[] templateIds = products
            .Select( p => p.TemplateId )
            .Where( id => id > 0 )
            .Distinct()
            .ToArray();
        if (templateIds.Length == 0)
        {
            return result;
        }

        // Dostawca from Zakup tab: product.supplierinfo.partner_id
        object[] domain =
        [
            new object[] { "product_tmpl_id", "in", templateIds }
        ];

        Dictionary<string, object?> kwargs = new()
        {
            ["fields"] = new[]
            {
                "id",
                "product_tmpl_id",
                "product_id",
                "partner_id",
                "sequence",
            },
            ["limit"] = 20000,
            ["order"] = "sequence asc, id asc",
        };

        JsonElement supplierRows;
        try
        {
            supplierRows = await _client.CallKwAsync(
                session,
                "product.supplierinfo",
                "search_read",
                [domain],
                kwargs,
                cancellationToken );
        }
        catch
        {
            // Purchase/supplierinfo may be unavailable for some users/modules.
            return result;
        }

        if (supplierRows.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        // First seller by sequence wins for each (product_id, template_id) key.
        // product_id=0 means template-level seller (all variants).
        foreach (JsonElement row in supplierRows.EnumerateArray())
        {
            int templateId = ReadMany2OneId( row, "product_tmpl_id" );
            if (templateId <= 0)
            {
                continue;
            }

            string? partnerName = ReadMany2OneName( row, "partner_id" );
            if (string.IsNullOrWhiteSpace( partnerName ))
            {
                continue;
            }

            int productId = ReadMany2OneId( row, "product_id" );
            var key = (productId, templateId);
            if (!result.ContainsKey( key ))
            {
                result[key] = partnerName;
            }
        }

        return result;
    }

    private static string BuildProductUrl( string baseUrl, int productId ) =>
        $"{baseUrl.TrimEnd( '/' )}/odoo/product.product/{productId}";

    private bool TryResolveSession( HttpRequest request, out OdooSession session )
    {
        session = null!;
        ClaimsPrincipal? principal = BukinistkaJwtAuthentication.TryValidateCookie( request, _config );
        return principal is not null && OdooSessionReader.TryGetFromPrincipal( principal, out session );
    }

    private readonly record struct ProductRow(
        int Id,
        int TemplateId,
        string Name,
        string? DefaultCode,
        string? Barcode,
        decimal QuantityInStock,
        decimal ListPrice,
        decimal StandardPrice,
        string? UomName );

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

        if (value.ValueKind == JsonValueKind.False || value.ValueKind == JsonValueKind.Null)
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
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out decimal parsed ) ? parsed : 0m,
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

    private static string? ReadOptionalString( JsonElement row, string property )
    {
        if (!row.TryGetProperty( property, out JsonElement value ))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.False || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace( text ) ? null : text;
    }

    private static string? ReadMany2OneName( JsonElement row, string property )
    {
        if (!row.TryGetProperty( property, out JsonElement value ))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.False || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 2)
        {
            JsonElement nameEl = value[1];
            if (nameEl.ValueKind == JsonValueKind.String)
            {
                string? name = nameEl.GetString()?.Trim();
                return string.IsNullOrWhiteSpace( name ) ? null : name;
            }
        }

        return null;
    }
}
