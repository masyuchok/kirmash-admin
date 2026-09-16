using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using backend.Models;
using backend.Services.Auth;
using Microsoft.AspNetCore.Http;

namespace backend.Services.Odoo;

public sealed class OdooProductService
{
    private const int MaxShopifyImages = 20;
    private const int MaxImageBytes = 8 * 1024 * 1024;

    private readonly OdooJsonRpcClient _client;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OdooBukinistkaSessionResolver _sessions;

    public OdooProductService(
        OdooJsonRpcClient client,
        IConfiguration config,
        IHttpClientFactory httpClientFactory,
        OdooBukinistkaSessionResolver sessions )
    {
        _client = client;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _sessions = sessions;
    }

    public async Task<OdooProductListResponse> ListProductsAsync(
        HttpRequest request,
        string? search = null,
        CancellationToken cancellationToken = default )
    {
        return await WithOdooSessionAsync( request, async session =>
        {
        const int limit = 5000;
        string? searchTerm = string.IsNullOrWhiteSpace( search ) ? null : search.Trim();
        object[] domain = BuildProductListDomain( searchTerm );

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
            ["limit"] = limit,
            ["order"] = "name asc",
        };

        JsonElement countResult = await _client.CallKwAsync(
            session,
            "product.product",
            "search_count",
            [domain],
            null,
            cancellationToken );
        int totalCount = countResult.ValueKind == JsonValueKind.Number
            ? countResult.GetInt32()
            : 0;

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

        Dictionary<int, string> publishersByTemplateId =
            await LoadPublisherNamesByTemplateIdAsync( session, productRows, cancellationToken );
        Dictionary<int, string> authorsByTemplateId =
            await LoadAuthorNamesByTemplateIdAsync( session, productRows, cancellationToken );
        Dictionary<(int ProductId, int TemplateId), string> suppliersByKey =
            await LoadSupplierNamesAsync( session, productRows, cancellationToken );

        List<OdooProductListItem> products = productRows
            .Select( row =>
            {
                string? publisherOrSupplier = null;
                if (row.TemplateId > 0
                    && publishersByTemplateId.TryGetValue( row.TemplateId, out string? publisher )
                    && !string.IsNullOrWhiteSpace( publisher ))
                {
                    publisherOrSupplier = publisher;
                }
                else if (suppliersByKey.TryGetValue( (row.Id, row.TemplateId), out string? byVariant ))
                {
                    publisherOrSupplier = byVariant;
                }
                else if (row.TemplateId > 0
                         && suppliersByKey.TryGetValue( (0, row.TemplateId), out string? byTemplate ))
                {
                    publisherOrSupplier = byTemplate;
                }

                string? authorName = null;
                if (row.TemplateId > 0
                    && authorsByTemplateId.TryGetValue( row.TemplateId, out string? author )
                    && !string.IsNullOrWhiteSpace( author ))
                {
                    authorName = author;
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
                    SupplierName = publisherOrSupplier,
                    AuthorName = authorName,
                    OdooUrl = BuildProductUrl( odooBaseUrl, row.Id ),
                };
            } )
            .ToList();

        if (string.IsNullOrWhiteSpace( searchTerm ))
        {
            products = products
                .OrderByDescending( p => p.QuantityInStock )
                .ThenBy( p => p.Name, StringComparer.OrdinalIgnoreCase )
                .ToList();
        }

        return new OdooProductListResponse
        {
            Products = products,
            TotalCount = totalCount,
            IsTruncated = totalCount > products.Count,
        };
        }, cancellationToken );
    }

    private static object[] BuildProductListDomain( string? searchTerm )
    {
        if (string.IsNullOrWhiteSpace( searchTerm ))
        {
            return [new object[] { "active", "=", true }];
        }

        return
        [
            new object[] { "active", "=", true },
            "|",
            "|",
            "|",
            new object[] { "name", "ilike", searchTerm },
            new object[] { "display_name", "ilike", searchTerm },
            new object[] { "barcode", "ilike", searchTerm },
            new object[] { "default_code", "ilike", searchTerm },
        ];
    }

    public sealed record OdooProductSnapshot(
        int Id,
        string Name,
        int UomId,
        decimal QuantityInStock,
        decimal ListPrice,
        decimal StandardPrice );

    public sealed record OdooProductShopifySourceDetails(
        string Name,
        decimal ListPrice,
        string? BarcodeDigits,
        string? PublisherName,
        string? CategoryName,
        string? WeightKgLabel,
        string? DescriptionHtml,
        string? Author,
        decimal WeightKg );

    public async Task<OdooProductShopifySourceDetails> GetProductShopifySourceDetailsAsync(
        HttpRequest request,
        int productId,
        CancellationToken cancellationToken = default )
    {
        return await WithOdooSessionAsync(
            request,
            session => GetProductShopifySourceDetailsAsync( session, productId, cancellationToken ),
            cancellationToken );
    }

    public async Task<OdooProductShopifySourceDetails> GetProductShopifySourceDetailsWithSyncSessionAsync(
        int productId,
        CancellationToken cancellationToken = default )
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            OdooSession session = await _sessions.ResolveSyncSessionAsync(
                cancellationToken,
                forceRefresh: attempt > 0 );
            try
            {
                return await GetProductShopifySourceDetailsAsync( session, productId, cancellationToken );
            }
            catch (UnauthorizedAccessException ex) when (
                attempt == 0
                && (OdooBukinistkaSessionResolver.IsSessionExpiredMessage( ex.Message )
                    || string.Equals(
                        ex.Message,
                        OdooBukinistkaSessionResolver.UserFriendlySessionExpiredMessage,
                        StringComparison.Ordinal )))
            {
                _sessions.InvalidateSyncSession();
            }
        }

        throw new UnauthorizedAccessException(
            OdooBukinistkaSessionResolver.UserFriendlySessionExpiredMessage );
    }

    public async Task<Dictionary<int, string>> GetPublisherNamesByProductIdsAsync(
        HttpRequest request,
        IReadOnlyCollection<int> productIds,
        CancellationToken cancellationToken = default )
    {
        return await WithOdooSessionAsync(
            request,
            session => GetPublisherNamesByProductIdsAsync( session, productIds, cancellationToken ),
            cancellationToken );
    }

    public async Task<Dictionary<int, string>> GetPublisherNamesByProductIdsWithSyncSessionAsync(
        IReadOnlyCollection<int> productIds,
        CancellationToken cancellationToken = default )
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            OdooSession session = await _sessions.ResolveSyncSessionAsync(
                cancellationToken,
                forceRefresh: attempt > 0 );
            try
            {
                return await GetPublisherNamesByProductIdsAsync( session, productIds, cancellationToken );
            }
            catch (UnauthorizedAccessException ex) when (
                attempt == 0
                && (OdooBukinistkaSessionResolver.IsSessionExpiredMessage( ex.Message )
                    || string.Equals(
                        ex.Message,
                        OdooBukinistkaSessionResolver.UserFriendlySessionExpiredMessage,
                        StringComparison.Ordinal )))
            {
                _sessions.InvalidateSyncSession();
            }
        }

        throw new UnauthorizedAccessException(
            OdooBukinistkaSessionResolver.UserFriendlySessionExpiredMessage );
    }

    public async Task<Dictionary<int, string>> GetAuthorNamesByProductIdsWithSyncSessionAsync(
        IReadOnlyCollection<int> productIds,
        CancellationToken cancellationToken = default )
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            OdooSession session = await _sessions.ResolveSyncSessionAsync(
                cancellationToken,
                forceRefresh: attempt > 0 );
            try
            {
                return await GetAuthorNamesByProductIdsAsync( session, productIds, cancellationToken );
            }
            catch (UnauthorizedAccessException ex) when (
                attempt == 0
                && (OdooBukinistkaSessionResolver.IsSessionExpiredMessage( ex.Message )
                    || string.Equals(
                        ex.Message,
                        OdooBukinistkaSessionResolver.UserFriendlySessionExpiredMessage,
                        StringComparison.Ordinal )))
            {
                _sessions.InvalidateSyncSession();
            }
        }

        throw new UnauthorizedAccessException(
            OdooBukinistkaSessionResolver.UserFriendlySessionExpiredMessage );
    }

    public async Task<Dictionary<int, decimal>> GetListPricesByProductIdsWithSyncSessionAsync(
        IReadOnlyCollection<int> productIds,
        CancellationToken cancellationToken = default )
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            OdooSession session = await _sessions.ResolveSyncSessionAsync(
                cancellationToken,
                forceRefresh: attempt > 0 );
            try
            {
                return await GetListPricesByProductIdsAsync( session, productIds, cancellationToken );
            }
            catch (UnauthorizedAccessException ex) when (
                attempt == 0
                && (OdooBukinistkaSessionResolver.IsSessionExpiredMessage( ex.Message )
                    || string.Equals(
                        ex.Message,
                        OdooBukinistkaSessionResolver.UserFriendlySessionExpiredMessage,
                        StringComparison.Ordinal )))
            {
                _sessions.InvalidateSyncSession();
            }
        }

        throw new UnauthorizedAccessException(
            OdooBukinistkaSessionResolver.UserFriendlySessionExpiredMessage );
    }

    public async Task<OdooProductSnapshot> GetProductSnapshotAsync(
        HttpRequest request,
        int productId,
        CancellationToken cancellationToken = default )
    {
        return await WithOdooSessionAsync(
            request,
            session => GetProductSnapshotAsync( session, productId, cancellationToken ),
            cancellationToken );
    }

    public async Task UpdateListPriceAsync(
        HttpRequest request,
        int productId,
        decimal listPrice,
        CancellationToken cancellationToken = default )
    {
        if (productId <= 0)
        {
            throw new InvalidOperationException( "Некарэктны ідэнтыфікатар прадукта Odoo." );
        }

        if (listPrice < 0m)
        {
            throw new InvalidOperationException( "Цана продажу не можа быць адмоўнай." );
        }

        decimal rounded = Math.Round( listPrice, 2, MidpointRounding.AwayFromZero );
        await WithOdooSessionAsync( request, async session =>
        {
            await _client.CallKwAsync(
                session,
                "product.product",
                "write",
                [new[] { productId }, new Dictionary<string, object?> { ["list_price"] = rounded }],
                null,
                cancellationToken );
            return true;
        }, cancellationToken );
    }

    public async Task UpdateStandardPriceAsync(
        HttpRequest request,
        int productId,
        decimal standardPrice,
        CancellationToken cancellationToken = default )
    {
        if (productId <= 0)
        {
            throw new InvalidOperationException( "Некарэктны ідэнтыфікатар прадукта Odoo." );
        }

        if (standardPrice < 0m)
        {
            throw new InvalidOperationException( "Кошт закупкі не можа быць адмоўным." );
        }

        decimal rounded = Math.Round( standardPrice, 2, MidpointRounding.AwayFromZero );
        await WithOdooSessionAsync( request, async session =>
        {
            await _client.CallKwAsync(
                session,
                "product.product",
                "write",
                [new[] { productId }, new Dictionary<string, object?> { ["standard_price"] = rounded }],
                null,
                cancellationToken );
            return true;
        }, cancellationToken );
    }

    /// <summary>
    /// Sets Studio owner (x_studio_many2many…) to Kirma.sh company on an existing product.
    /// </summary>
    public async Task SetKirmaOwnerCompanyAsync(
        HttpRequest request,
        int productId,
        CancellationToken cancellationToken = default )
    {
        if (productId <= 0)
        {
            throw new InvalidOperationException( "Некарэктны ідэнтыфікатар прадукта Odoo." );
        }

        await WithOdooSessionAsync( request, async session =>
        {
            int? ownerCompanyId = await ResolveKirmaOwnerCompanyIdAsync( session, cancellationToken );
            if (ownerCompanyId is null or <= 0)
            {
                throw new InvalidOperationException(
                    "У Odoo не знойдзена кампанія Kirma.sh для ўласніка прадукту." );
            }

            await _client.CallKwAsync(
                session,
                "product.product",
                "write",
                [
                    new[] { productId },
                    new Dictionary<string, object?>
                    {
                        ["x_studio_many2many_field_21t_1ipn4f8oc"] =
                            new object[] { new object[] { 6, 0, new[] { ownerCompanyId.Value } } },
                    }
                ],
                null,
                cancellationToken );
            return true;
        }, cancellationToken );
    }

    public sealed record CreateProductFromShopifyInput(
        string Name,
        decimal ListPrice,
        decimal StandardPrice,
        string? IsbnDigits,
        IReadOnlyList<string> AuthorNames,
        string? PublisherName,
        string? CategoryName,
        string? WeightKgLabel,
        string? DescriptionHtml,
        IReadOnlyList<string> ImageUrls );

    public sealed record CreatedOdooProduct( int Id, string Name, string OdooUrl );

    /// <summary>
    /// Creates a new Odoo product card for Kirma consignments (qty stays 0 until Przyjęcia).
    /// </summary>
    public async Task<CreatedOdooProduct> CreateProductFromShopifyAsync(
        HttpRequest request,
        CreateProductFromShopifyInput input,
        CancellationToken cancellationToken = default )
    {
        string name = (input.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( name ))
        {
            throw new InvalidOperationException( "Укажыце назву тавару." );
        }

        if (input.ListPrice < 0m)
        {
            throw new InvalidOperationException( "Цана продажу не можа быць адмоўнай." );
        }

        if (input.StandardPrice < 0m)
        {
            throw new InvalidOperationException( "Кошт закупкі не можа быць адмоўным." );
        }

        decimal listPrice = Math.Round( input.ListPrice, 2, MidpointRounding.AwayFromZero );
        decimal standardPrice = Math.Round( input.StandardPrice, 2, MidpointRounding.AwayFromZero );
        string? isbn = NormalizeDigitsOnly( input.IsbnDigits );

        return await WithOdooSessionAsync( request, async session =>
        {
        List<int> authorIds = await ResolveAuthorAttributeIdsAsync(
            session,
            input.AuthorNames,
            cancellationToken );
        int? publisherId = await ResolveOrCreatePublisherIdAsync(
            session,
            input.PublisherName,
            cancellationToken );
        int? categoryId = await ResolveCategoryIdAsync(
            session,
            input.CategoryName,
            cancellationToken );
        int? ownerCompanyId = await ResolveKirmaOwnerCompanyIdAsync( session, cancellationToken );

        Dictionary<string, object?> values = new()
        {
            ["name"] = name,
            // Typ produktu — Towary (Goods)
            ["type"] = "consu",
            // Śledź magazyn — так
            ["is_storable"] = true,
            ["tracking"] = "none",
            ["list_price"] = listPrice,
            // Store / stock stays 0 until receipt (Przyjęcia)
        };

        string? notes = (input.DescriptionHtml ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace( notes ))
        {
            // Notatki / Internal Notes on product.template
            values["description"] = notes;
        }

        if (!string.IsNullOrWhiteSpace( isbn ))
        {
            values["barcode"] = isbn;
            values["x_studio_char_field_1sg_1is4llgdb"] = isbn;
        }

        if (authorIds.Count > 0)
        {
            // Producent (authors)
            values["x_studio_autor_1"] = new object[] { new object[] { 6, 0, authorIds.ToArray() } };
        }

        if (publisherId is > 0)
        {
            // Publisher — Shopify Vendor
            values["x_studio_producent"] = publisherId.Value;
        }

        if (ownerCompanyId is > 0)
        {
            // Owner — Kirma.sh / kirmash
            values["x_studio_many2many_field_21t_1ipn4f8oc"] =
                new object[] { new object[] { 6, 0, new[] { ownerCompanyId.Value } } };
        }

        if (categoryId is > 0)
        {
            // Kategoria — Shopify genre / product type
            values["categ_id"] = categoryId.Value;
        }

        string? weightLabel = (input.WeightKgLabel ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace( weightLabel ))
        {
            values["x_studio_weight"] = weightLabel;
            if (decimal.TryParse(
                    weightLabel,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out decimal weightKg )
                && weightKg > 0m)
            {
                values["weight"] = weightKg;
            }
        }

        JsonElement created = await _client.CallKwAsync(
            session,
            "product.template",
            "create",
            [values],
            null,
            cancellationToken );

        int templateId = created.ValueKind == JsonValueKind.Number && created.TryGetInt32( out int tmplId )
            ? tmplId
            : 0;
        if (templateId <= 0)
        {
            throw new InvalidOperationException( "Не ўдалося стварыць картку тавару ў Odoo." );
        }

        JsonElement variants = await _client.CallKwAsync(
            session,
            "product.product",
            "search_read",
            [
                new object[]
                {
                    new object[] { "product_tmpl_id", "=", templateId }
                }
            ],
            new Dictionary<string, object?>
            {
                ["fields"] = new[] { "id", "display_name", "name" },
                ["limit"] = 1,
                ["order"] = "id asc",
            },
            cancellationToken );

        if (variants.ValueKind != JsonValueKind.Array || variants.GetArrayLength() == 0)
        {
            throw new InvalidOperationException( "Прадукт Odoo створаны, але варыянт не знойдзены." );
        }

        int productId = ReadInt( variants[0], "id" );
        if (productId <= 0)
        {
            throw new InvalidOperationException( "Прадукт Odoo створаны, але варыянт не знойдзены." );
        }

        string productName = ReadString( variants[0], "display_name" )
            ?? ReadString( variants[0], "name" )
            ?? name;

        await _client.CallKwAsync(
            session,
            "product.product",
            "write",
            [new[] { productId }, new Dictionary<string, object?> { ["standard_price"] = standardPrice }],
            null,
            cancellationToken );

        await AttachShopifyImagesAsync(
            session,
            templateId,
            productName,
            input.ImageUrls,
            cancellationToken );

        return new CreatedOdooProduct(
            productId,
            productName,
            BuildProductUrl( _client.ConfiguredBaseUrl, productId ) );
        }, cancellationToken );
    }

    private async Task AttachShopifyImagesAsync(
        OdooSession session,
        int templateId,
        string productName,
        IReadOnlyList<string>? imageUrls,
        CancellationToken cancellationToken )
    {
        if (templateId <= 0 || imageUrls is null || imageUrls.Count == 0)
        {
            return;
        }

        HttpClient http = _httpClientFactory.CreateClient();
        List<string> urls = imageUrls
            .Where( u => !string.IsNullOrWhiteSpace( u ) )
            .Select( u => u.Trim() )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .Take( MaxShopifyImages )
            .ToList();

        bool mainImageSet = false;
        int index = 0;
        foreach (string url in urls)
        {
            index++;
            cancellationToken.ThrowIfCancellationRequested();

            DownloadedImage? image = await TryDownloadImageAsync( http, url, index, cancellationToken );
            if (image is null)
            {
                continue;
            }

            if (!mainImageSet)
            {
                try
                {
                    await _client.CallKwAsync(
                        session,
                        "product.template",
                        "write",
                        [
                            new[] { templateId },
                            new Dictionary<string, object?> { ["image_1920"] = image.Base64 }
                        ],
                        null,
                        cancellationToken );
                    mainImageSet = true;
                }
                catch
                {
                    // Fall through and still attach as document/attachment.
                }
            }

            try
            {
                await _client.CallKwAsync(
                    session,
                    "ir.attachment",
                    "create",
                    [
                        new Dictionary<string, object?>
                        {
                            ["name"] = image.FileName,
                            ["type"] = "binary",
                            ["datas"] = image.Base64,
                            ["res_model"] = "product.template",
                            ["res_id"] = templateId,
                            ["mimetype"] = image.MimeType,
                            ["description"] = $"Shopify · {productName}",
                        }
                    ],
                    null,
                    cancellationToken );
            }
            catch
            {
                // Do not fail product creation if a secondary image cannot be attached.
            }
        }
    }

    private sealed record DownloadedImage( string FileName, string MimeType, string Base64 );

    private static async Task<DownloadedImage?> TryDownloadImageAsync(
        HttpClient http,
        string url,
        int index,
        CancellationToken cancellationToken )
    {
        try
        {
            using HttpRequestMessage request = new( HttpMethod.Get, url );
            using HttpResponseMessage response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken );
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            long? length = response.Content.Headers.ContentLength;
            if (length is > MaxImageBytes)
            {
                return null;
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync( cancellationToken );
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
            {
                return null;
            }

            string mime = response.Content.Headers.ContentType?.MediaType?.Trim()
                ?? GuessMimeFromUrl( url );
            if (string.IsNullOrWhiteSpace( mime ) || !mime.StartsWith( "image/", StringComparison.OrdinalIgnoreCase ))
            {
                mime = GuessMimeFromUrl( url );
            }

            string extension = mime.ToLowerInvariant() switch
            {
                "image/png" => "png",
                "image/webp" => "webp",
                "image/gif" => "gif",
                "image/jpeg" or "image/jpg" => "jpg",
                _ => "jpg"
            };

            return new DownloadedImage(
                $"shopify-{index}.{extension}",
                mime.StartsWith( "image/", StringComparison.OrdinalIgnoreCase ) ? mime : $"image/{extension}",
                Convert.ToBase64String( bytes ) );
        }
        catch
        {
            return null;
        }
    }

    private static string GuessMimeFromUrl( string url )
    {
        string path = url.Split( '?', 2 )[0].ToLowerInvariant();
        if (path.EndsWith( ".png", StringComparison.Ordinal )) return "image/png";
        if (path.EndsWith( ".webp", StringComparison.Ordinal )) return "image/webp";
        if (path.EndsWith( ".gif", StringComparison.Ordinal )) return "image/gif";
        return "image/jpeg";
    }

    private async Task<List<int>> ResolveAuthorAttributeIdsAsync(
        OdooSession session,
        IReadOnlyList<string> authorNames,
        CancellationToken cancellationToken )
    {
        List<int> ids = new();
        if (authorNames is null || authorNames.Count == 0)
        {
            return ids;
        }

        HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );
        foreach (string raw in authorNames)
        {
            string author = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( author ) || !seen.Add( author ))
            {
                continue;
            }

            JsonElement existing = await _client.CallKwAsync(
                session,
                "product.attribute",
                "search_read",
                [
                    new object[]
                    {
                        new object[] { "name", "=ilike", author }
                    }
                ],
                new Dictionary<string, object?>
                {
                    ["fields"] = new[] { "id", "name" },
                    ["limit"] = 1,
                },
                cancellationToken );

            if (existing.ValueKind == JsonValueKind.Array && existing.GetArrayLength() > 0)
            {
                int id = ReadInt( existing[0], "id" );
                if (id > 0)
                {
                    ids.Add( id );
                    continue;
                }
            }

            JsonElement created = await _client.CallKwAsync(
                session,
                "product.attribute",
                "create",
                [
                    new Dictionary<string, object?>
                    {
                        ["name"] = author,
                        ["create_variant"] = "no_variant",
                        ["display_type"] = "radio",
                    }
                ],
                null,
                cancellationToken );

            int createdId = created.ValueKind == JsonValueKind.Number && created.TryGetInt32( out int aId )
                ? aId
                : 0;
            if (createdId > 0)
            {
                ids.Add( createdId );
            }
        }

        return ids;
    }

    private async Task<int?> ResolveOrCreatePublisherIdAsync(
        OdooSession session,
        string? publisherName,
        CancellationToken cancellationToken )
    {
        string name = (publisherName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( name ))
        {
            return null;
        }

        JsonElement existing = await _client.CallKwAsync(
            session,
            "x_producent",
            "search_read",
            [
                new object[]
                {
                    new object[] { "x_name", "=ilike", name }
                }
            ],
            new Dictionary<string, object?>
            {
                ["fields"] = new[] { "id", "x_name" },
                ["limit"] = 1,
            },
            cancellationToken );

        if (existing.ValueKind == JsonValueKind.Array && existing.GetArrayLength() > 0)
        {
            int id = ReadInt( existing[0], "id" );
            return id > 0 ? id : null;
        }

        JsonElement created = await _client.CallKwAsync(
            session,
            "x_producent",
            "create",
            [
                new Dictionary<string, object?>
                {
                    ["x_name"] = name,
                    ["x_active"] = true,
                }
            ],
            null,
            cancellationToken );

        int createdId = created.ValueKind == JsonValueKind.Number && created.TryGetInt32( out int pId )
            ? pId
            : 0;
        return createdId > 0 ? createdId : null;
    }

    private async Task<int?> ResolveCategoryIdAsync(
        OdooSession session,
        string? categoryName,
        CancellationToken cancellationToken )
    {
        string name = (categoryName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( name ))
        {
            return null;
        }

        JsonElement exact = await _client.CallKwAsync(
            session,
            "product.category",
            "search_read",
            [
                new object[]
                {
                    new object[] { "name", "=ilike", name }
                }
            ],
            new Dictionary<string, object?>
            {
                ["fields"] = new[] { "id", "name", "complete_name" },
                ["limit"] = 1,
            },
            cancellationToken );

        if (exact.ValueKind == JsonValueKind.Array && exact.GetArrayLength() > 0)
        {
            int id = ReadInt( exact[0], "id" );
            return id > 0 ? id : null;
        }

        JsonElement fuzzy = await _client.CallKwAsync(
            session,
            "product.category",
            "search_read",
            [
                new object[]
                {
                    new object[] { "complete_name", "ilike", name }
                }
            ],
            new Dictionary<string, object?>
            {
                ["fields"] = new[] { "id", "name", "complete_name" },
                ["limit"] = 1,
            },
            cancellationToken );

        if (fuzzy.ValueKind == JsonValueKind.Array && fuzzy.GetArrayLength() > 0)
        {
            int id = ReadInt( fuzzy[0], "id" );
            return id > 0 ? id : null;
        }

        return null;
    }

    private async Task<int?> ResolveKirmaOwnerCompanyIdAsync(
        OdooSession session,
        CancellationToken cancellationToken )
    {
        foreach (string needle in new[] { "Kirma.sh", "kirmash", "Kirma" })
        {
            JsonElement rows = await _client.CallKwAsync(
                session,
                "res.company",
                "search_read",
                [
                    new object[]
                    {
                        new object[] { "name", "ilike", needle }
                    }
                ],
                new Dictionary<string, object?>
                {
                    ["fields"] = new[] { "id", "name" },
                    ["limit"] = 5,
                    ["order"] = "id asc",
                },
                cancellationToken );

            if (rows.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement row in rows.EnumerateArray())
            {
                string? companyName = ReadString( row, "name" );
                if (string.IsNullOrWhiteSpace( companyName ))
                {
                    continue;
                }

                if (companyName.Contains( "kirma", StringComparison.OrdinalIgnoreCase ))
                {
                    int id = ReadInt( row, "id" );
                    if (id > 0)
                    {
                        return id;
                    }
                }
            }
        }

        return null;
    }

    private static string? NormalizeDigitsOnly( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        char[] digits = raw.Where( char.IsDigit ).ToArray();
        return digits.Length == 0 ? null : new string( digits );
    }

    public async Task IncreaseQuantityAsync(
        HttpRequest request,
        int productId,
        int delta,
        CancellationToken cancellationToken = default )
    {
        if (productId <= 0)
        {
            throw new InvalidOperationException( "Некарэктны ідэнтыфікатар прадукта Odoo." );
        }

        if (delta <= 0)
        {
            throw new InvalidOperationException( "Колькасць для дадавання павінна быць больш за нуль." );
        }

        await WithOdooSessionAsync( request, async session =>
        {
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
        return true;
        }, cancellationToken );
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

    private async Task<OdooProductShopifySourceDetails> GetProductShopifySourceDetailsAsync(
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
                "list_price",
                "barcode",
                "description",
                "product_tmpl_id",
                "x_studio_producent",
                "x_studio_autor_1",
                "categ_id",
                "x_studio_weight",
                "weight",
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
        string? barcode = ReadOptionalString( row, "barcode" );
        string? publisher = ReadMany2OneName( row, "x_studio_producent" );
        int templateId = ReadMany2OneId( row, "product_tmpl_id" );
        if (string.IsNullOrWhiteSpace( publisher ) && templateId > 0)
        {
            publisher = await ReadPublisherNameFromTemplateAsync( session, templateId, cancellationToken );
        }

        if (string.IsNullOrWhiteSpace( publisher ) && templateId > 0)
        {
            publisher = await ReadSupplierNameFromTemplateAsync( session, productId, templateId, cancellationToken );
        }

        string? category = ReadMany2OneName( row, "categ_id" );
        string? weightLabel = ReadOptionalString( row, "x_studio_weight" );
        string? description = ReadOptionalString( row, "description" );
        decimal weightKg = ReadDecimal( row, "weight" );

        List<int> authorIds = ReadRelationIds( row, "x_studio_autor_1" );
        if (authorIds.Count == 0 && templateId > 0)
        {
            authorIds = await ReadTemplateAuthorIdsAsync( session, templateId, cancellationToken );
        }

        if (string.IsNullOrWhiteSpace( description ) && templateId > 0)
        {
            description = await ReadTemplateDescriptionAsync( session, templateId, cancellationToken );
        }

        if (string.IsNullOrWhiteSpace( weightLabel ) && templateId > 0)
        {
            weightLabel = await ReadTemplateWeightLabelAsync( session, templateId, cancellationToken );
        }

        if (weightKg <= 0m && templateId > 0)
        {
            weightKg = await ReadTemplateWeightKgAsync( session, templateId, cancellationToken );
        }

        string? author = null;
        if (templateId > 0)
        {
            author = await ReadTemplateAuthorNameAsync( session, templateId, cancellationToken );
        }

        if (string.IsNullOrWhiteSpace( author ))
        {
            author = await ResolveAuthorFromOdooRowAsync( session, row, cancellationToken );
        }

        if (string.IsNullOrWhiteSpace( author ) && authorIds.Count > 0)
        {
            author = await ResolveAuthorNamesByIdsAsync( session, authorIds, cancellationToken );
        }

        if (string.IsNullOrWhiteSpace( author ) && templateId > 0)
        {
            author = await ReadAuthorFromTemplateAttributeLinesAsync(
                session,
                templateId,
                cancellationToken );
        }

        decimal resolvedWeightKg = ResolveWeightKg( weightKg, weightLabel );

        return new OdooProductShopifySourceDetails(
            name,
            ReadDecimal( row, "list_price" ),
            NormalizeDigitsOnly( barcode ),
            string.IsNullOrWhiteSpace( publisher ) ? null : publisher.Trim(),
            string.IsNullOrWhiteSpace( category ) ? null : category.Trim(),
            string.IsNullOrWhiteSpace( weightLabel ) ? null : weightLabel.Trim(),
            string.IsNullOrWhiteSpace( description ) ? null : description.Trim(),
            string.IsNullOrWhiteSpace( author ) ? null : author.Trim(),
            resolvedWeightKg );
    }

    private static decimal ResolveWeightKg( decimal weightKg, string? weightLabel )
    {
        if (weightKg > 0m)
        {
            return Math.Round( weightKg, 3, MidpointRounding.AwayFromZero );
        }

        if (string.IsNullOrWhiteSpace( weightLabel ))
        {
            return 0m;
        }

        string label = weightLabel.Trim();
        if (TryParseWeightDecimal( label, out decimal parsed ) && parsed > 0m)
        {
            return Math.Round( parsed, 3, MidpointRounding.AwayFromZero );
        }

        Match match = Regex.Match( label, @"(\d+(?:[.,]\d+)?)" );
        if (match.Success
            && TryParseWeightDecimal( match.Groups[1].Value, out decimal extracted )
            && extracted > 0m)
        {
            return Math.Round( extracted, 3, MidpointRounding.AwayFromZero );
        }

        return 0m;
    }

    private static bool TryParseWeightDecimal( string value, out decimal parsed )
    {
        if (decimal.TryParse(
                value,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out parsed ))
        {
            return true;
        }

        return decimal.TryParse(
            value.Replace( ',', '.' ),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out parsed );
    }

    private async Task<List<int>> ReadTemplateAuthorIdsAsync(
        OdooSession session,
        int templateId,
        CancellationToken cancellationToken )
    {
        JsonElement row = await ReadProductTemplateRowAsync(
            session,
            templateId,
            ["x_studio_autor_1"],
            cancellationToken );
        return row.ValueKind == JsonValueKind.Object
            ? ReadRelationIds( row, "x_studio_autor_1" )
            : [];
    }

    private async Task<string?> ReadTemplateAuthorNameAsync(
        OdooSession session,
        int templateId,
        CancellationToken cancellationToken )
    {
        JsonElement row = await ReadProductTemplateRowAsync(
            session,
            templateId,
            ["x_studio_autor_1"],
            cancellationToken );
        if (row.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return await ResolveAuthorFromOdooRowAsync( session, row, cancellationToken );
    }

    private async Task<string?> ResolveAuthorFromOdooRowAsync(
        OdooSession session,
        JsonElement row,
        CancellationToken cancellationToken )
    {
        string? author = ReadOptionalString( row, "x_studio_autor_1" );
        if (!string.IsNullOrWhiteSpace( author ))
        {
            return author.Trim();
        }

        author = ReadMany2OneName( row, "x_studio_autor_1" );
        if (!string.IsNullOrWhiteSpace( author ))
        {
            return author.Trim();
        }

        List<int> authorIds = ReadRelationIds( row, "x_studio_autor_1" );
        if (authorIds.Count == 0)
        {
            return null;
        }

        return await ResolveAuthorNamesByIdsAsync( session, authorIds, cancellationToken );
    }

    private async Task<string?> ReadAuthorFromTemplateAttributeLinesAsync(
        OdooSession session,
        int templateId,
        CancellationToken cancellationToken )
    {
        object[] domain =
        [
            new object[] { "product_tmpl_id", "=", templateId }
        ];

        Dictionary<string, object?> kwargs = new()
        {
            ["fields"] = new[] { "attribute_id", "value_ids" },
            ["limit"] = 50,
        };

        JsonElement lines;
        try
        {
            lines = await _client.CallKwAsync(
                session,
                "product.template.attribute.line",
                "search_read",
                [domain],
                kwargs,
                cancellationToken );
        }
        catch
        {
            return null;
        }

        if (lines.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<int> valueIds = new();
        foreach (JsonElement line in lines.EnumerateArray())
        {
            string? attributeName = ReadMany2OneName( line, "attribute_id" );
            if (!IsAuthorAttributeName( attributeName ))
            {
                continue;
            }

            valueIds.AddRange( ReadRelationIds( line, "value_ids" ) );
        }

        if (valueIds.Count == 0)
        {
            return null;
        }

        return await ResolveAuthorNamesByIdsAsync( session, valueIds, cancellationToken );
    }

    private static bool IsAuthorAttributeName( string? attributeName )
    {
        if (string.IsNullOrWhiteSpace( attributeName ))
        {
            return false;
        }

        string normalized = attributeName.Trim().ToLowerInvariant();
        return normalized.Contains( "autor", StringComparison.Ordinal )
               || normalized.Contains( "author", StringComparison.Ordinal )
               || normalized.Contains( "аўтар", StringComparison.Ordinal )
               || normalized.Contains( "producent", StringComparison.Ordinal );
    }

    private async Task<Dictionary<int, string>> GetAuthorNamesByProductIdsAsync(
        OdooSession session,
        IReadOnlyCollection<int> productIds,
        CancellationToken cancellationToken )
    {
        Dictionary<int, string> result = new();
        int[] ids = productIds.Where( id => id > 0 ).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return result;
        }

        const int batchSize = 200;
        for (int offset = 0; offset < ids.Length; offset += batchSize)
        {
            int[] batch = ids.Skip( offset ).Take( batchSize ).ToArray();
            object[] domain =
            [
                new object[] { "id", "in", batch }
            ];

            Dictionary<string, object?> kwargs = new()
            {
                ["fields"] = new[]
                {
                    "id",
                    "product_tmpl_id",
                    "x_studio_autor_1",
                },
                ["limit"] = batch.Length,
            };

            JsonElement rows = await _client.CallKwAsync(
                session,
                "product.product",
                "search_read",
                [domain],
                kwargs,
                cancellationToken );

            if (rows.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement row in rows.EnumerateArray())
            {
                int productId = ReadInt( row, "id" );
                if (productId <= 0)
                {
                    continue;
                }

                int templateId = ReadMany2OneId( row, "product_tmpl_id" );
                string? author = null;
                if (templateId > 0)
                {
                    author = await ReadTemplateAuthorNameAsync( session, templateId, cancellationToken );
                }

                if (string.IsNullOrWhiteSpace( author ))
                {
                    author = await ResolveAuthorFromOdooRowAsync( session, row, cancellationToken );
                }

                if (string.IsNullOrWhiteSpace( author ) && templateId > 0)
                {
                    author = await ReadAuthorFromTemplateAttributeLinesAsync(
                        session,
                        templateId,
                        cancellationToken );
                }

                if (!string.IsNullOrWhiteSpace( author ))
                {
                    result[productId] = author.Trim();
                }
            }
        }

        return result;
    }

    private async Task<string?> ReadTemplateDescriptionAsync(
        OdooSession session,
        int templateId,
        CancellationToken cancellationToken )
    {
        JsonElement row = await ReadProductTemplateRowAsync(
            session,
            templateId,
            ["description"],
            cancellationToken );
        return row.ValueKind == JsonValueKind.Object
            ? ReadOptionalString( row, "description" )
            : null;
    }

    private async Task<string?> ReadTemplateWeightLabelAsync(
        OdooSession session,
        int templateId,
        CancellationToken cancellationToken )
    {
        JsonElement row = await ReadProductTemplateRowAsync(
            session,
            templateId,
            ["x_studio_weight"],
            cancellationToken );
        return row.ValueKind == JsonValueKind.Object
            ? ReadOptionalString( row, "x_studio_weight" )
            : null;
    }

    private async Task<decimal> ReadTemplateWeightKgAsync(
        OdooSession session,
        int templateId,
        CancellationToken cancellationToken )
    {
        JsonElement row = await ReadProductTemplateRowAsync(
            session,
            templateId,
            ["weight", "x_studio_weight"],
            cancellationToken );
        if (row.ValueKind != JsonValueKind.Object)
        {
            return 0m;
        }

        decimal weightKg = ReadDecimal( row, "weight" );
        string? weightLabel = ReadOptionalString( row, "x_studio_weight" );
        return ResolveWeightKg( weightKg, weightLabel );
    }

    private async Task<JsonElement> ReadProductTemplateRowAsync(
        OdooSession session,
        int templateId,
        string[] fields,
        CancellationToken cancellationToken )
    {
        object[] domain =
        [
            new object[] { "id", "=", templateId }
        ];

        Dictionary<string, object?> kwargs = new()
        {
            ["fields"] = fields,
            ["limit"] = 1,
        };

        JsonElement result = await _client.CallKwAsync(
            session,
            "product.template",
            "search_read",
            [domain],
            kwargs,
            cancellationToken );

        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
        {
            return default;
        }

        return result[0];
    }

    private async Task<string?> ResolveAuthorNamesByIdsAsync(
        OdooSession session,
        IReadOnlyList<int> attributeIds,
        CancellationToken cancellationToken )
    {
        int[] ids = attributeIds.Where( id => id > 0 ).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return null;
        }

        string? fromValues = await ReadOdooRecordNamesAsync(
            session,
            "product.attribute.value",
            ids,
            cancellationToken );
        if (!string.IsNullOrWhiteSpace( fromValues ))
        {
            return fromValues;
        }

        string? fromAttributes = await ReadOdooRecordNamesAsync(
            session,
            "product.attribute",
            ids,
            cancellationToken );
        if (!string.IsNullOrWhiteSpace( fromAttributes ))
        {
            return fromAttributes;
        }

        string? fromPartners = await ReadOdooRecordNamesAsync(
            session,
            "res.partner",
            ids,
            cancellationToken );
        if (!string.IsNullOrWhiteSpace( fromPartners ))
        {
            return fromPartners;
        }

        try
        {
            return await ReadOdooRecordNamesAsync(
                session,
                "x_autor",
                ids,
                cancellationToken,
                nameField: "x_name" );
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> ReadOdooRecordNamesAsync(
        OdooSession session,
        string model,
        IReadOnlyList<int> ids,
        CancellationToken cancellationToken,
        string nameField = "name" )
    {
        int[] normalizedIds = ids.Where( id => id > 0 ).Distinct().ToArray();
        if (normalizedIds.Length == 0)
        {
            return null;
        }

        object[] domain =
        [
            new object[] { "id", "in", normalizedIds }
        ];

        Dictionary<string, object?> kwargs = new()
        {
            ["fields"] = new[] { "id", nameField },
            ["limit"] = normalizedIds.Length,
        };

        JsonElement rows;
        try
        {
            rows = await _client.CallKwAsync(
                session,
                model,
                "search_read",
                [domain],
                kwargs,
                cancellationToken );
        }
        catch
        {
            return null;
        }

        if (rows.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string> names = new();
        foreach (JsonElement row in rows.EnumerateArray())
        {
            string? name = ReadOptionalString( row, nameField );
            if (!string.IsNullOrWhiteSpace( name ))
            {
                names.Add( name.Trim() );
            }
        }

        return names.Count == 0 ? null : string.Join( ", ", names );
    }

    private static List<int> ReadRelationIds( JsonElement row, string property )
    {
        if (!row.TryGetProperty( property, out JsonElement value )
            || value.ValueKind == JsonValueKind.False
            || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32( out int singleId ) && singleId > 0)
        {
            return [singleId];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        if (value.GetArrayLength() >= 1
            && value[0].ValueKind == JsonValueKind.Number
            && value[0].TryGetInt32( out int tupleId )
            && tupleId > 0)
        {
            return [tupleId];
        }

        return ReadMany2ManyIds( row, property );
    }

    private async Task<string?> ResolveAttributeNamesAsync(
        OdooSession session,
        IReadOnlyList<int> attributeIds,
        CancellationToken cancellationToken )
    {
        return await ResolveAuthorNamesByIdsAsync( session, attributeIds, cancellationToken );
    }

    private static List<int> ReadMany2ManyIds( JsonElement row, string property )
    {
        List<int> ids = new();
        if (!row.TryGetProperty( property, out JsonElement value )
            || value.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32( out int id ) && id > 0)
            {
                ids.Add( id );
                continue;
            }

            if (item.ValueKind == JsonValueKind.Array
                && item.GetArrayLength() >= 1
                && item[0].ValueKind == JsonValueKind.Number
                && item[0].TryGetInt32( out int nestedId )
                && nestedId > 0)
            {
                ids.Add( nestedId );
            }
        }

        return ids;
    }

    private async Task<Dictionary<int, decimal>> GetListPricesByProductIdsAsync(
        OdooSession session,
        IReadOnlyCollection<int> productIds,
        CancellationToken cancellationToken )
    {
        Dictionary<int, decimal> result = new();
        int[] ids = productIds.Where( id => id > 0 ).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return result;
        }

        const int batchSize = 200;
        for (int offset = 0; offset < ids.Length; offset += batchSize)
        {
            int[] batch = ids.Skip( offset ).Take( batchSize ).ToArray();
            object[] domain =
            [
                new object[] { "id", "in", batch }
            ];

            Dictionary<string, object?> kwargs = new()
            {
                ["fields"] = new[] { "id", "list_price" },
                ["limit"] = batch.Length,
            };

            JsonElement rows = await _client.CallKwAsync(
                session,
                "product.product",
                "search_read",
                [domain],
                kwargs,
                cancellationToken );

            if (rows.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement row in rows.EnumerateArray())
            {
                int id = ReadInt( row, "id" );
                decimal listPrice = ReadDecimal( row, "list_price" );
                if (id > 0 && listPrice > 0m)
                {
                    result[id] = Math.Round( listPrice, 2, MidpointRounding.AwayFromZero );
                }
            }
        }

        return result;
    }

    private async Task<Dictionary<int, string>> GetPublisherNamesByProductIdsAsync(
        OdooSession session,
        IReadOnlyCollection<int> productIds,
        CancellationToken cancellationToken )
    {
        Dictionary<int, string> result = new();
        int[] ids = productIds.Where( id => id > 0 ).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return result;
        }

        List<ProductRow> productRows = new();
        const int batchSize = 200;
        for (int offset = 0; offset < ids.Length; offset += batchSize)
        {
            int[] batch = ids.Skip( offset ).Take( batchSize ).ToArray();
            object[] domain =
            [
                new object[] { "id", "in", batch }
            ];

            Dictionary<string, object?> kwargs = new()
            {
                ["fields"] = new[] { "id", "product_tmpl_id", "x_studio_producent" },
                ["limit"] = batch.Length,
            };

            JsonElement rows = await _client.CallKwAsync(
                session,
                "product.product",
                "search_read",
                [domain],
                kwargs,
                cancellationToken );

            if (rows.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement row in rows.EnumerateArray())
            {
                int id = ReadInt( row, "id" );
                if (id <= 0)
                {
                    continue;
                }

                string? publisherOnVariant = ReadMany2OneName( row, "x_studio_producent" );
                if (!string.IsNullOrWhiteSpace( publisherOnVariant ))
                {
                    result[id] = publisherOnVariant.Trim();
                    continue;
                }

                int templateId = ReadMany2OneId( row, "product_tmpl_id" );
                productRows.Add( new ProductRow(
                    id,
                    templateId,
                    string.Empty,
                    null,
                    null,
                    0m,
                    0m,
                    0m,
                    null ) );
            }
        }

        if (productRows.Count == 0)
        {
            return result;
        }

        Dictionary<int, string> publishersByTemplateId =
            await LoadPublisherNamesByTemplateIdAsync( session, productRows, cancellationToken );
        Dictionary<(int ProductId, int TemplateId), string> suppliersByKey =
            await LoadSupplierNamesAsync( session, productRows, cancellationToken );

        foreach (ProductRow row in productRows)
        {
            if (result.ContainsKey( row.Id ))
            {
                continue;
            }

            string? publisherOrSupplier = null;
            if (row.TemplateId > 0
                && publishersByTemplateId.TryGetValue( row.TemplateId, out string? publisher )
                && !string.IsNullOrWhiteSpace( publisher ))
            {
                publisherOrSupplier = publisher;
            }
            else if (suppliersByKey.TryGetValue( (row.Id, row.TemplateId), out string? byVariant ))
            {
                publisherOrSupplier = byVariant;
            }
            else if (row.TemplateId > 0
                     && suppliersByKey.TryGetValue( (0, row.TemplateId), out string? byTemplate ))
            {
                publisherOrSupplier = byTemplate;
            }

            if (!string.IsNullOrWhiteSpace( publisherOrSupplier ))
            {
                result[row.Id] = publisherOrSupplier.Trim();
            }
        }

        return result;
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

    private async Task<string?> ReadPublisherNameFromTemplateAsync(
        OdooSession session,
        int templateId,
        CancellationToken cancellationToken )
    {
        Dictionary<int, string> publishers = await LoadPublisherNamesByTemplateIdAsync(
            session,
            [new ProductRow( 0, templateId, string.Empty, null, null, 0m, 0m, 0m, null )],
            cancellationToken );
        return publishers.TryGetValue( templateId, out string? publisher ) ? publisher : null;
    }

    private async Task<string?> ReadSupplierNameFromTemplateAsync(
        OdooSession session,
        int productId,
        int templateId,
        CancellationToken cancellationToken )
    {
        ProductRow row = new( productId, templateId, string.Empty, null, null, 0m, 0m, 0m, null );
        Dictionary<(int ProductId, int TemplateId), string> suppliers =
            await LoadSupplierNamesAsync( session, [row], cancellationToken );
        if (suppliers.TryGetValue( (productId, templateId), out string? byVariant ))
        {
            return byVariant;
        }

        return suppliers.TryGetValue( (0, templateId), out string? byTemplate ) ? byTemplate : null;
    }

    /// <summary>
    /// Publisher (Odoo Studio field «Publisher» / x_studio_producent → x_producent).
    /// </summary>
    private async Task<Dictionary<int, string>> LoadPublisherNamesByTemplateIdAsync(
        OdooSession session,
        List<ProductRow> products,
        CancellationToken cancellationToken )
    {
        Dictionary<int, string> result = new();
        int[] templateIds = products
            .Select( p => p.TemplateId )
            .Where( id => id > 0 )
            .Distinct()
            .ToArray();
        if (templateIds.Length == 0)
        {
            return result;
        }

        // Batch in chunks — Odoo "in" domains with thousands of ids can be heavy.
        const int chunkSize = 500;
        for (int offset = 0; offset < templateIds.Length; offset += chunkSize)
        {
            int[] chunk = templateIds.Skip( offset ).Take( chunkSize ).ToArray();
            object[] domain =
            [
                new object[] { "id", "in", chunk }
            ];

            Dictionary<string, object?> kwargs = new()
            {
                ["fields"] = new[] { "id", "x_studio_producent" },
                ["limit"] = chunk.Length,
            };

            JsonElement rows;
            try
            {
                rows = await _client.CallKwAsync(
                    session,
                    "product.template",
                    "search_read",
                    [domain],
                    kwargs,
                    cancellationToken );
            }
            catch
            {
                // Studio field may be missing on some DBs; keep list usable.
                return result;
            }

            if (rows.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement row in rows.EnumerateArray())
            {
                int templateId = ReadInt( row, "id" );
                if (templateId <= 0)
                {
                    continue;
                }

                string? publisher = ReadMany2OneName( row, "x_studio_producent" );
                if (string.IsNullOrWhiteSpace( publisher ))
                {
                    continue;
                }

                result[templateId] = publisher.Trim();
            }
        }

        return result;
    }

    /// <summary>
    /// Author(s) from Odoo Studio field x_studio_autor_1 (char / many2one / many2many).
    /// </summary>
    private async Task<Dictionary<int, string>> LoadAuthorNamesByTemplateIdAsync(
        OdooSession session,
        List<ProductRow> products,
        CancellationToken cancellationToken )
    {
        Dictionary<int, string> result = new();
        int[] templateIds = products
            .Select( p => p.TemplateId )
            .Where( id => id > 0 )
            .Distinct()
            .ToArray();
        if (templateIds.Length == 0)
        {
            return result;
        }

        Dictionary<int, List<int>> pendingAuthorIdsByTemplate = new();
        HashSet<int> allAuthorIds = new();

        const int chunkSize = 500;
        for (int offset = 0; offset < templateIds.Length; offset += chunkSize)
        {
            int[] chunk = templateIds.Skip( offset ).Take( chunkSize ).ToArray();
            object[] domain =
            [
                new object[] { "id", "in", chunk }
            ];

            Dictionary<string, object?> kwargs = new()
            {
                ["fields"] = new[] { "id", "x_studio_autor_1" },
                ["limit"] = chunk.Length,
            };

            JsonElement rows;
            try
            {
                rows = await _client.CallKwAsync(
                    session,
                    "product.template",
                    "search_read",
                    [domain],
                    kwargs,
                    cancellationToken );
            }
            catch
            {
                // Studio field may be missing on some DBs; keep list usable.
                return result;
            }

            if (rows.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement row in rows.EnumerateArray())
            {
                int templateId = ReadInt( row, "id" );
                if (templateId <= 0)
                {
                    continue;
                }

                string? direct = ReadOptionalString( row, "x_studio_autor_1" );
                if (string.IsNullOrWhiteSpace( direct ))
                {
                    direct = ReadMany2OneName( row, "x_studio_autor_1" );
                }

                if (!string.IsNullOrWhiteSpace( direct ))
                {
                    result[templateId] = direct.Trim();
                    continue;
                }

                List<int> authorIds = ReadRelationIds( row, "x_studio_autor_1" );
                if (authorIds.Count == 0)
                {
                    continue;
                }

                pendingAuthorIdsByTemplate[templateId] = authorIds;
                foreach (int authorId in authorIds)
                {
                    allAuthorIds.Add( authorId );
                }
            }
        }

        if (allAuthorIds.Count == 0)
        {
            return result;
        }

        Dictionary<int, string> namesById = await ResolveAuthorNameMapByIdsAsync(
            session,
            allAuthorIds.ToArray(),
            cancellationToken );

        foreach ((int templateId, List<int> authorIds) in pendingAuthorIdsByTemplate)
        {
            List<string> names = new();
            foreach (int authorId in authorIds)
            {
                if (namesById.TryGetValue( authorId, out string? name )
                    && !string.IsNullOrWhiteSpace( name ))
                {
                    names.Add( name.Trim() );
                }
            }

            if (names.Count > 0)
            {
                result[templateId] = string.Join( ", ", names.Distinct( StringComparer.OrdinalIgnoreCase ) );
            }
        }

        return result;
    }

    private async Task<Dictionary<int, string>> ResolveAuthorNameMapByIdsAsync(
        OdooSession session,
        IReadOnlyList<int> attributeIds,
        CancellationToken cancellationToken )
    {
        Dictionary<int, string> result = new();
        int[] ids = attributeIds.Where( id => id > 0 ).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return result;
        }

        async Task MergeFromModelAsync( string model, string nameField = "name" )
        {
            if (result.Count >= ids.Length)
            {
                return;
            }

            int[] missing = ids.Where( id => !result.ContainsKey( id ) ).ToArray();
            if (missing.Length == 0)
            {
                return;
            }

            object[] domain =
            [
                new object[] { "id", "in", missing }
            ];

            Dictionary<string, object?> kwargs = new()
            {
                ["fields"] = new[] { "id", nameField },
                ["limit"] = missing.Length,
            };

            JsonElement rows;
            try
            {
                rows = await _client.CallKwAsync(
                    session,
                    model,
                    "search_read",
                    [domain],
                    kwargs,
                    cancellationToken );
            }
            catch
            {
                return;
            }

            if (rows.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement row in rows.EnumerateArray())
            {
                int id = ReadInt( row, "id" );
                string? name = ReadOptionalString( row, nameField );
                if (id > 0 && !string.IsNullOrWhiteSpace( name ))
                {
                    result[id] = name.Trim();
                }
            }
        }

        await MergeFromModelAsync( "product.attribute.value" );
        await MergeFromModelAsync( "product.attribute" );
        await MergeFromModelAsync( "res.partner" );
        await MergeFromModelAsync( "x_autor", "x_name" );

        return result;
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

    private async Task<T> WithOdooSessionAsync<T>(
        HttpRequest request,
        Func<OdooSession, Task<T>> action,
        CancellationToken cancellationToken )
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            OdooSession session = await _sessions.ResolveAsync(
                request,
                cancellationToken,
                forceRefresh: attempt > 0 );
            try
            {
                return await action( session );
            }
            catch (UnauthorizedAccessException ex) when (
                attempt == 0
                && (OdooBukinistkaSessionResolver.IsSessionExpiredMessage( ex.Message )
                    || string.Equals(
                        ex.Message,
                        OdooBukinistkaSessionResolver.UserFriendlySessionExpiredMessage,
                        StringComparison.Ordinal )))
            {
                _sessions.InvalidateSyncSession();
            }
        }

        throw new UnauthorizedAccessException(
            OdooBukinistkaSessionResolver.UserFriendlySessionExpiredMessage );
    }

    private static string BuildProductUrl( string baseUrl, int productId ) =>
        $"{baseUrl.TrimEnd( '/' )}/odoo/product.product/{productId}";

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
