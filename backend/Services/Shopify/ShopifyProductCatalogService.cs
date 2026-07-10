using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Models;
using backend.Services;

namespace backend.Services.Shopify;

public class ShopifyProductCatalogService
{
    private readonly ShopifyGraphqlClient _graphql;

    public ShopifyProductCatalogService( ShopifyGraphqlClient graphql )
    {
        _graphql = graphql;
    }

    public async Task<List<ShopifyCatalogProduct>> FetchAllProductsAsync( string shop, string accessToken )
    {
        List<ShopifyCatalogProduct> result = new();
        string? afterCursor = null;
        bool hasNextPage;

        do
        {
            using JsonDocument json = await _graphql.ExecuteAsync(
                shop,
                accessToken,
                ShopifyGraphqlQueries.ProductsPage,
                new { after = afterCursor }
            );
            JsonElement products = json.RootElement.GetProperty( "data" ).GetProperty( "products" );
            JsonElement edges = products.GetProperty( "edges" );

            foreach (JsonElement edge in edges.EnumerateArray())
            {
                ShopifyCatalogProduct? product = ParseProductNode( edge.GetProperty( "node" ) );
                if (product is not null)
                {
                    result.Add( product );
                }
            }

            JsonElement pageInfo = products.GetProperty( "pageInfo" );
            hasNextPage = pageInfo.GetProperty( "hasNextPage" ).GetBoolean();
            afterCursor = pageInfo.GetProperty( "endCursor" ).GetString();
        } while (hasNextPage && !string.IsNullOrWhiteSpace( afterCursor ));

        return result
            .OrderBy( p => p.Title, StringComparer.OrdinalIgnoreCase )
            .ToList();
    }

    public async Task<Dictionary<string, string>> FetchProductTitlesByIdsAsync(
        string shop,
        string accessToken,
        IReadOnlyCollection<string> normalizedProductIds )
    {
        Dictionary<string, string> titles = new( StringComparer.OrdinalIgnoreCase );
        if (normalizedProductIds.Count == 0)
        {
            return titles;
        }

        List<string> gids = normalizedProductIds
            .Select( ShopifyIds.NormalizeProductId )
            .Where( id => !string.IsNullOrWhiteSpace( id ) )
            .Select( ShopifyIds.ToProductGid )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .ToList();

        const int batchSize = 50;
        for (int offset = 0; offset < gids.Count; offset += batchSize)
        {
            string[] batch = gids.Skip( offset ).Take( batchSize ).ToArray();
            using JsonDocument json = await _graphql.ExecuteAsync(
                shop,
                accessToken,
                ShopifyGraphqlQueries.ProductTitleNodes,
                new { ids = batch } );

            if (!json.RootElement.TryGetProperty( "data", out JsonElement dataEl ) ||
                !dataEl.TryGetProperty( "nodes", out JsonElement nodesEl ) ||
                nodesEl.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement node in nodesEl.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string productId = string.Empty;
                if (node.TryGetProperty( "legacyResourceId", out JsonElement legacyIdEl ) &&
                    legacyIdEl.ValueKind == JsonValueKind.Number &&
                    legacyIdEl.TryGetInt64( out long legacyId ))
                {
                    productId = legacyId.ToString();
                }
                else if (node.TryGetProperty( "id", out JsonElement gidEl ) &&
                         gidEl.ValueKind == JsonValueKind.String)
                {
                    productId = ShopifyIds.NormalizeProductId( gidEl.GetString() ?? string.Empty );
                }

                string title = node.TryGetProperty( "title", out JsonElement titleEl ) &&
                               titleEl.ValueKind == JsonValueKind.String
                    ? (titleEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                if (string.IsNullOrWhiteSpace( productId ) || string.IsNullOrWhiteSpace( title ))
                {
                    continue;
                }

                titles[productId] = title;
            }
        }

        return titles;
    }

    private static ShopifyCatalogProduct? ParseProductNode( JsonElement node )
    {
        string productName = node.GetProperty( "title" ).GetString() ?? "—";
        string productType = node.TryGetProperty( "productType", out JsonElement productTypeEl ) &&
                             productTypeEl.ValueKind == JsonValueKind.String
            ? (productTypeEl.GetString() ?? string.Empty)
            : string.Empty;
        List<ProductVariantItem> variants = new();
        string? mainImageUrl = null;
        int quantityInStock = 0;

        if (node.TryGetProperty( "totalInventory", out JsonElement totalInventoryEl ) &&
            totalInventoryEl.ValueKind == JsonValueKind.Number &&
            totalInventoryEl.TryGetInt32( out int parsedInventory ))
        {
            quantityInStock = parsedInventory;
        }

        if (node.TryGetProperty( "featuredImage", out JsonElement imageEl ) &&
            imageEl.ValueKind == JsonValueKind.Object &&
            imageEl.TryGetProperty( "url", out JsonElement imageUrlEl ) &&
            imageUrlEl.ValueKind == JsonValueKind.String)
        {
            mainImageUrl = imageUrlEl.GetString();
        }

        if (node.TryGetProperty( "variants", out JsonElement variantsEl ) &&
            variantsEl.ValueKind == JsonValueKind.Object &&
            variantsEl.TryGetProperty( "edges", out JsonElement variantEdgesEl ) &&
            variantEdgesEl.ValueKind == JsonValueKind.Array)
        {
            string? defaultVariantId = null;
            string? defaultVariantBarcode = null;
            int defaultVariantQuantity = 0;

            foreach (JsonElement edgeEl in variantEdgesEl.EnumerateArray())
            {
                if (!edgeEl.TryGetProperty( "node", out JsonElement variantNode ) ||
                    variantNode.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string variantId = variantNode.TryGetProperty( "id", out JsonElement variantIdEl ) &&
                                   variantIdEl.ValueKind == JsonValueKind.String
                    ? ShopifyIds.NormalizeVariantId( variantIdEl.GetString() ?? string.Empty )
                    : string.Empty;
                string variantName = variantNode.TryGetProperty( "title", out JsonElement variantTitleEl ) &&
                                     variantTitleEl.ValueKind == JsonValueKind.String
                    ? (variantTitleEl.GetString() ?? string.Empty)
                    : string.Empty;
                string variantBarcode = variantNode.TryGetProperty( "barcode", out JsonElement barcodeEl ) &&
                                        barcodeEl.ValueKind == JsonValueKind.String
                    ? (barcodeEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                int variantQuantity = variantNode.TryGetProperty( "inventoryQuantity", out JsonElement variantQtyEl ) &&
                                      variantQtyEl.ValueKind == JsonValueKind.Number &&
                                      variantQtyEl.TryGetInt32( out int parsedVariantQty )
                    ? parsedVariantQty
                    : 0;

                if ((string.IsNullOrWhiteSpace( variantName ) || variantName == "Default Title") &&
                    variantNode.TryGetProperty( "selectedOptions", out JsonElement selectedOptionsEl ) &&
                    selectedOptionsEl.ValueKind == JsonValueKind.Array)
                {
                    List<string> optionValues = new();
                    foreach (JsonElement opt in selectedOptionsEl.EnumerateArray())
                    {
                        if (opt.TryGetProperty( "value", out JsonElement valEl ) &&
                            valEl.ValueKind == JsonValueKind.String)
                        {
                            string val = valEl.GetString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace( val ) &&
                                !string.Equals( val, "Default Title", StringComparison.OrdinalIgnoreCase ))
                            {
                                optionValues.Add( val );
                            }
                        }
                    }

                    if (optionValues.Count > 0)
                    {
                        variantName = string.Join( " / ", optionValues );
                    }
                }

                bool isDefaultTitle = string.IsNullOrWhiteSpace( variantName ) ||
                                      string.Equals( variantName, "Default Title", StringComparison.OrdinalIgnoreCase );
                if (isDefaultTitle)
                {
                    defaultVariantId ??= variantId;
                    if (!string.IsNullOrWhiteSpace( variantBarcode ))
                    {
                        defaultVariantBarcode = variantBarcode;
                    }

                    defaultVariantQuantity = variantQuantity;
                    continue;
                }

                variants.Add( new ProductVariantItem
                {
                    VariantId = variantId,
                    VariantName = variantName,
                    Barcode = variantBarcode,
                    QuantityInStock = variantQuantity
                } );
            }

            if (variants.Count == 0 && !string.IsNullOrWhiteSpace( defaultVariantId ))
            {
                variants.Add( new ProductVariantItem
                {
                    VariantId = defaultVariantId,
                    VariantName = "Default Title",
                    Barcode = defaultVariantBarcode ?? string.Empty,
                    QuantityInStock = defaultVariantQuantity
                } );
            }
        }

        if (variants.Count > 0)
        {
            quantityInStock = variants.Sum( v => v.QuantityInStock );
        }

        string productId = string.Empty;
        if (node.TryGetProperty( "legacyResourceId", out JsonElement legacyIdEl ) &&
            legacyIdEl.ValueKind == JsonValueKind.Number &&
            legacyIdEl.TryGetInt64( out long legacyId ))
        {
            productId = legacyId.ToString();
        }
        else if (node.TryGetProperty( "id", out JsonElement gidEl ) &&
                 gidEl.ValueKind == JsonValueKind.String)
        {
            productId = ShopifyIds.NormalizeProductId( gidEl.GetString() ?? string.Empty );
        }

        if (string.IsNullOrWhiteSpace( productId ))
        {
            return null;
        }

        string author = ParseAuthor( node );
        string isbn = ParseIsbn( node, variants );

        return new ShopifyCatalogProduct
        {
            ProductId = productId,
            Title = productName,
            ProductType = productType,
            Author = author,
            Isbn = isbn,
            TotalInventory = quantityInStock,
            ImageUrl = string.IsNullOrWhiteSpace( mainImageUrl ) ? null : mainImageUrl,
            Variants = variants
        };
    }

    private static string ParseIsbn( JsonElement node, IReadOnlyList<ProductVariantItem> variants )
    {
        string? fromAlias = ReadAliasedMetafield( node, "isbnMetafield" );
        if (!string.IsNullOrWhiteSpace( fromAlias ))
        {
            string normalized = VatReportHelpers.NormalizeIsbn( fromAlias );
            if (!string.IsNullOrWhiteSpace( normalized ))
            {
                return normalized;
            }
        }

        if (node.TryGetProperty( "metafields", out JsonElement metafieldsEl ) &&
            metafieldsEl.ValueKind == JsonValueKind.Object &&
            metafieldsEl.TryGetProperty( "edges", out JsonElement metafieldEdgesEl ) &&
            metafieldEdgesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement edge in metafieldEdgesEl.EnumerateArray())
            {
                if (!edge.TryGetProperty( "node", out JsonElement metafieldNode ) ||
                    metafieldNode.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string key = metafieldNode.TryGetProperty( "key", out JsonElement keyEl ) &&
                               keyEl.ValueKind == JsonValueKind.String
                    ? (keyEl.GetString() ?? string.Empty)
                    : string.Empty;
                if (!string.Equals( key, "isbn", StringComparison.OrdinalIgnoreCase ))
                {
                    continue;
                }

                string? value = ReadMetafieldValue( metafieldNode );
                string normalized = VatReportHelpers.NormalizeIsbn( value );
                if (!string.IsNullOrWhiteSpace( normalized ))
                {
                    return normalized;
                }
            }
        }

        foreach (ProductVariantItem variant in variants)
        {
            string normalized = VatReportHelpers.NormalizeIsbn( variant.Barcode );
            if (!string.IsNullOrWhiteSpace( normalized ))
            {
                return normalized;
            }
        }

        return string.Empty;
    }

    private static string? ReadMetafieldValue( JsonElement metafieldNode )
    {
        if (metafieldNode.TryGetProperty( "value", out JsonElement valueEl ) &&
            valueEl.ValueKind == JsonValueKind.String)
        {
            return valueEl.GetString();
        }

        return null;
    }

    private static string ParseAuthor( JsonElement node )
    {
        string? fromAlias = ReadAliasedMetafield( node, "authorMetafield" );
        if (!string.IsNullOrWhiteSpace( fromAlias ))
        {
            return fromAlias;
        }

        fromAlias = ReadAliasedMetafield( node, "autorMetafield" );
        if (!string.IsNullOrWhiteSpace( fromAlias ))
        {
            return fromAlias;
        }

        if (node.TryGetProperty( "metafields", out JsonElement metafieldsEl ) &&
            metafieldsEl.ValueKind == JsonValueKind.Object &&
            metafieldsEl.TryGetProperty( "edges", out JsonElement metafieldEdgesEl ) &&
            metafieldEdgesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement edge in metafieldEdgesEl.EnumerateArray())
            {
                if (!edge.TryGetProperty( "node", out JsonElement metafieldNode ) ||
                    metafieldNode.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string key = metafieldNode.TryGetProperty( "key", out JsonElement keyEl ) &&
                               keyEl.ValueKind == JsonValueKind.String
                    ? (keyEl.GetString() ?? string.Empty)
                    : string.Empty;
                if (!IsAuthorMetafieldKey( key ))
                {
                    continue;
                }

                string? value = ReadMetafieldNodeValue( metafieldNode );
                if (!string.IsNullOrWhiteSpace( value ))
                {
                    return value;
                }
            }
        }

        if (node.TryGetProperty( "tags", out JsonElement tagsEl ) &&
            tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement tagEl in tagsEl.EnumerateArray())
            {
                if (tagEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? fromTag = ParseAuthorFromTag( tagEl.GetString() );
                if (!string.IsNullOrWhiteSpace( fromTag ))
                {
                    return fromTag;
                }
            }
        }

        if (node.TryGetProperty( "vendor", out JsonElement vendorEl ) &&
            vendorEl.ValueKind == JsonValueKind.String)
        {
            string vendor = (vendorEl.GetString() ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace( vendor ) &&
                !string.Equals( vendor, "Kirma", StringComparison.OrdinalIgnoreCase ) &&
                !string.Equals( vendor, "Kamunikat", StringComparison.OrdinalIgnoreCase ))
            {
                return vendor;
            }
        }

        return string.Empty;
    }

    private static bool IsAuthorMetafieldKey( string key )
    {
        if (string.IsNullOrWhiteSpace( key ))
        {
            return false;
        }

        string normalized = key.Trim().ToLowerInvariant();
        return normalized is "author" or "autor" or "authors" or "аўтар" or "book_author" or "book-author";
    }

    private static string? ReadAliasedMetafield( JsonElement node, string aliasProperty )
    {
        if (!node.TryGetProperty( aliasProperty, out JsonElement metafieldEl ) ||
            metafieldEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadMetafieldNodeValue( metafieldEl );
    }

    private static string? ReadMetafieldNodeValue( JsonElement metafieldNode )
    {
        if (!metafieldNode.TryGetProperty( "value", out JsonElement valueEl ) ||
            valueEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return NormalizeAuthorValue( valueEl.GetString() );
    }

    private static string? NormalizeAuthorValue( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string trimmed = raw.Trim();
        if (!trimmed.StartsWith( "[", StringComparison.Ordinal ))
        {
            return trimmed;
        }

        try
        {
            JsonNode? parsed = JsonNode.Parse( trimmed );
            if (parsed is JsonArray array)
            {
                List<string> parts = array
                    .Select( x => x?.ToString()?.Trim() ?? string.Empty )
                    .Where( x => !string.IsNullOrWhiteSpace( x ) )
                    .ToList();
                if (parts.Count > 0)
                {
                    return string.Join( ", ", parts );
                }
            }
        }
        catch (JsonException)
        {
            // Keep raw value when Shopify returns a non-JSON list payload.
        }

        return trimmed;
    }

    private static string? ParseAuthorFromTag( string? tag )
    {
        if (string.IsNullOrWhiteSpace( tag ))
        {
            return null;
        }

        string trimmed = tag.Trim();
        string[] prefixes =
        [
            "author:",
            "autor:",
            "аўтар:",
            "аўтар ",
            "author ",
            "autor "
        ];

        foreach (string prefix in prefixes)
        {
            if (trimmed.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ))
            {
                string value = trimmed[prefix.Length..].Trim();
                return string.IsNullOrWhiteSpace( value ) ? null : value;
            }
        }

        return null;
    }
}
