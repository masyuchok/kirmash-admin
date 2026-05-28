using System.Text.Json;
using backend.Models;

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
            foreach (JsonElement edgeEl in variantEdgesEl.EnumerateArray())
            {
                if (!edgeEl.TryGetProperty( "node", out JsonElement variantNode ) ||
                    variantNode.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string variantId = variantNode.TryGetProperty( "id", out JsonElement variantIdEl ) &&
                                   variantIdEl.ValueKind == JsonValueKind.String
                    ? ShopifyIds.NormalizeProductId( variantIdEl.GetString() ?? string.Empty )
                    : string.Empty;
                string variantName = variantNode.TryGetProperty( "title", out JsonElement variantTitleEl ) &&
                                     variantTitleEl.ValueKind == JsonValueKind.String
                    ? (variantTitleEl.GetString() ?? string.Empty)
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

                if (string.IsNullOrWhiteSpace( variantName ) ||
                    string.Equals( variantName, "Default Title", StringComparison.OrdinalIgnoreCase ))
                {
                    continue;
                }

                variants.Add( new ProductVariantItem
                {
                    VariantId = variantId,
                    VariantName = variantName,
                    QuantityInStock = variantQuantity
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

        return new ShopifyCatalogProduct
        {
            ProductId = productId,
            Title = productName,
            ProductType = productType,
            TotalInventory = quantityInStock,
            ImageUrl = string.IsNullOrWhiteSpace( mainImageUrl ) ? null : mainImageUrl,
            Variants = variants
        };
    }
}
