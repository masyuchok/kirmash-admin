using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Models;

namespace backend.Services.Shopify;

public class ShopifyInventoryService
{
    public const string DefaultBookProductType = "Кніга";
    public const string BookAuthorMetafieldNamespace = "book";
    public const string BookAuthorMetafieldKey = "author";
    private const string PreferredInventoryLocationName = "Bukinistka";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ShopifyGraphqlClient _graphql;

    public sealed record CreateShopifyProductInput(
        string Title,
        decimal SalePrice,
        decimal UnitCost,
        string? Vendor,
        string? ProductType,
        string? Barcode,
        string? DescriptionHtml,
        string? Author,
        decimal? WeightKg,
        bool PublishAsDraft = true );

    public sealed record CreatedShopifyProduct(
        string ProductId,
        string VariantId,
        string Title );

    public ShopifyInventoryService(
        IHttpClientFactory httpClientFactory,
        ShopifyGraphqlClient graphql )
    {
        _httpClientFactory = httpClientFactory;
        _graphql = graphql;
    }

    public async Task<(int Previous, int Next)> ApplyInventoryDeltaByProductKeyAsync(
        string shop,
        string accessToken,
        string shopifyProductId,
        int delta )
    {
        Dictionary<string, (int Previous, int Next)> results =
            await ApplyInventoryDeltasByProductKeyAsync(
                shop,
                accessToken,
                new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase )
                {
                    [shopifyProductId] = delta
                } );
        return results.TryGetValue( ShopifyIds.NormalizeProductId( shopifyProductId ), out (int Previous, int Next) entry )
            ? entry
            : (0, 0);
    }

    /// <summary>
    /// Applies multiple product inventory deltas with one location lookup (avoids N× locations.json).
    /// Zero deltas are skipped.
    /// </summary>
    public async Task<Dictionary<string, (int Previous, int Next)>> ApplyInventoryDeltasByProductKeyAsync(
        string shop,
        string accessToken,
        IReadOnlyDictionary<string, int> deltasByProductId )
    {
        Dictionary<string, (int Previous, int Next)> results =
            new( StringComparer.OrdinalIgnoreCase );
        if (deltasByProductId.Count == 0)
        {
            return results;
        }

        HttpClient client = _httpClientFactory.CreateClient( "Shopify" );
        long locationId = await ResolveInventoryLocationIdAsync( client, shop, accessToken );

        foreach ((string rawProductId, int delta) in deltasByProductId)
        {
            if (delta == 0)
            {
                continue;
            }

            if (!TryParseSyncKey( rawProductId, out long productId, out long? variantId ))
            {
                // Fallback: plain product GID / numeric id without "::".
                string normalized = ShopifyIds.NormalizeProductId( rawProductId );
                long? parsedProductId = ShopifyIds.TryParseNumericProductId( normalized );
                if (!parsedProductId.HasValue)
                {
                    throw new InvalidOperationException( "Некарэктны Shopify ID прадукту." );
                }

                productId = parsedProductId.Value;
                variantId = null;
            }

            long inventoryItemId = variantId.HasValue
                ? await GetInventoryItemIdByVariantAsync(
                    client,
                    shop,
                    variantId.Value,
                    accessToken )
                : await GetInventoryItemIdByProductAsync(
                    client,
                    shop,
                    productId,
                    accessToken );
            (int current, int next) = await ApplyInventoryDeltaAtLocationAsync(
                client,
                shop,
                accessToken,
                inventoryItemId,
                locationId,
                delta );
            string resultKey = variantId.HasValue
                ? BuildLinePriceKey( productId.ToString(), variantId.Value.ToString() )
                : ShopifyIds.NormalizeProductId( productId.ToString() );
            results[resultKey] = (current, next);
        }

        return results;
    }

    public async Task SetVariantPriceByProductKeyAsync(
        string shop,
        string accessToken,
        string shopifyProductId,
        decimal salePrice )
    {
        HttpClient client = _httpClientFactory.CreateClient( "Shopify" );
        await SetVariantPriceByProductKeyAsync( shop, accessToken, shopifyProductId, salePrice, client );
    }

    public async Task SetVariantPriceAsync(
        string shop,
        string accessToken,
        string shopifyProductId,
        string? shopifyVariantId,
        decimal salePrice )
    {
        HttpClient client = _httpClientFactory.CreateClient( "Shopify" );
        long? productId = ShopifyIds.TryParseNumericProductId( shopifyProductId );
        if (!productId.HasValue)
        {
            throw new InvalidOperationException( "Некарэктны Shopify ID прадукту." );
        }

        long variantId;
        if (!string.IsNullOrWhiteSpace( shopifyVariantId ))
        {
            long? parsedVariantId = ShopifyIds.TryParseNumericVariantId( shopifyVariantId );
            if (!parsedVariantId.HasValue)
            {
                throw new InvalidOperationException( "Некарэктны Shopify ID варыянта." );
            }

            variantId = parsedVariantId.Value;
        }
        else
        {
            variantId = await GetPrimaryVariantIdByProductAsync( client, shop, productId.Value, accessToken );
        }

        await SetVariantPriceAsync( client, shop, variantId, salePrice, accessToken );
    }

    public async Task<CreatedShopifyProduct> CreateProductAsync(
        string shop,
        string accessToken,
        CreateShopifyProductInput input )
    {
        string title = (input.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( title ))
        {
            throw new InvalidOperationException( "Укажыце назву тавару." );
        }

        if (input.SalePrice < 0m)
        {
            throw new InvalidOperationException( "Цана продажу не можа быць адмоўнай." );
        }

        decimal salePrice = Math.Round( input.SalePrice, 2, MidpointRounding.AwayFromZero );
        string priceString = salePrice.ToString( "0.00", CultureInfo.InvariantCulture );

        Dictionary<string, object?> variant = new()
        {
            ["price"] = priceString,
            ["inventory_management"] = "shopify",
            ["inventory_policy"] = "deny",
        };

        if (!string.IsNullOrWhiteSpace( input.Barcode ))
        {
            variant["barcode"] = input.Barcode.Trim();
        }

        if (input.WeightKg is decimal weightValue && weightValue > 0m)
        {
            variant["weight"] = Math.Round( weightValue, 3, MidpointRounding.AwayFromZero );
            variant["weight_unit"] = "kg";
        }

        Dictionary<string, object?> productPayload = new()
        {
            ["title"] = title,
            ["variants"] = new[] { variant },
        };

        if (input.PublishAsDraft)
        {
            productPayload["status"] = "draft";
        }

        string? vendor = (input.Vendor ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace( vendor ))
        {
            productPayload["vendor"] = vendor;
        }

        productPayload["product_type"] = DefaultBookProductType;

        string? description = (input.DescriptionHtml ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace( description ))
        {
            productPayload["body_html"] = description;
        }

        string? authorForTags = (input.Author ?? string.Empty).Trim();
        string? barcodeForTags = (input.Barcode ?? string.Empty).Trim();
        string tags = BuildBookProductTags( barcodeForTags, authorForTags );
        if (!string.IsNullOrWhiteSpace( tags ))
        {
            productPayload["tags"] = tags;
        }

        string payload = JsonSerializer.Serialize( new { product = productPayload } );
        HttpClient client = _httpClientFactory.CreateClient( "Shopify" );
        using StringContent content = new( payload, Encoding.UTF8, "application/json" );
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Post,
            ShopifyApi.RestUrl( shop, "products.json" ),
            content );
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            if (IsShopifyBarcodeConflict( body ))
            {
                throw new InvalidOperationException(
                    "barcode_conflict: Штрихкод ужо заняты ў Shopify. " +
                    "Абярыце пусты ISBN або ўвядзіце іншы ўручную." );
            }

            throw new InvalidOperationException( $"Не ўдалося стварыць тавар у Shopify: {body}" );
        }

        using JsonDocument json = JsonDocument.Parse( body );
        JsonElement product = json.RootElement.GetProperty( "product" );
        long productId = product.GetProperty( "id" ).GetInt64();
        JsonElement variants = product.GetProperty( "variants" );
        if (variants.GetArrayLength() == 0)
        {
            throw new InvalidOperationException( "Shopify стварыў тавар без варыянта." );
        }

        long variantId = variants[0].GetProperty( "id" ).GetInt64();
        string normalizedProductId = productId.ToString();
        string normalizedVariantId = variantId.ToString();

        string? author = (input.Author ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace( author ))
        {
            await TrySetAuthorMetafieldsAsync(
                client,
                shop,
                accessToken,
                productId,
                author );
        }

        if (!string.IsNullOrWhiteSpace( input.Barcode ))
        {
            try
            {
                await SetProductMetafieldAsync(
                    client,
                    shop,
                    accessToken,
                    productId,
                    "isbn",
                    input.Barcode.Trim() );
            }
            catch
            {
                // ISBN metafield is optional.
            }
        }

        if (input.UnitCost >= 0m)
        {
            await SetVariantCostByProductKeyAsync(
                shop,
                accessToken,
                normalizedProductId,
                normalizedVariantId,
                Math.Round( input.UnitCost, 2, MidpointRounding.AwayFromZero ) );
        }

        if (input.WeightKg is decimal weightKg && weightKg > 0m)
        {
            await SetVariantWeightAsync(
                client,
                shop,
                variantId,
                Math.Round( weightKg, 3, MidpointRounding.AwayFromZero ),
                accessToken );
        }

        return new CreatedShopifyProduct(
            normalizedProductId,
            normalizedVariantId,
            title );
    }

    public async Task AttachProductImageAsync(
        string shop,
        string accessToken,
        string shopifyProductId,
        byte[] imageBytes,
        string fileName )
    {
        if (imageBytes is null || imageBytes.Length == 0)
        {
            return;
        }

        string productId = ShopifyIds.NormalizeProductId( shopifyProductId );
        if (string.IsNullOrWhiteSpace( productId ))
        {
            throw new InvalidOperationException( "Некарэктны Shopify product id." );
        }

        string safeName = string.IsNullOrWhiteSpace( fileName ) ? "cover.jpg" : fileName.Trim();
        Dictionary<string, object?> imagePayload = new()
        {
            ["attachment"] = Convert.ToBase64String( imageBytes ),
            ["filename"] = safeName,
        };

        string payload = JsonSerializer.Serialize( new { image = imagePayload } );
        HttpClient client = _httpClientFactory.CreateClient( "Shopify" );
        using StringContent content = new( payload, Encoding.UTF8, "application/json" );
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Post,
            ShopifyApi.RestUrl( shop, $"products/{productId}/images.json" ),
            content );
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException( $"Не ўдалося загрузіць выяву ў Shopify: {body}" );
        }
    }

    private static string BuildBookProductTags( string? barcodeDigits, string? author )
    {
        List<string> tags = new();
        HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );

        void AddTag( string? value )
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( trimmed ) || !seen.Add( trimmed ))
            {
                return;
            }

            tags.Add( trimmed );
        }

        AddTag( barcodeDigits );

        if (string.IsNullOrWhiteSpace( author ))
        {
            return string.Join( ", ", tags );
        }

        foreach (string authorPart in SplitAuthorParts( author ))
        {
            (string firstName, string? lastName, string fullName) = SplitAuthorName( authorPart );
            AddTag( firstName );
            AddTag( lastName );
            AddTag( fullName );
        }

        return string.Join( ", ", tags );
    }

    private static IEnumerable<string> SplitAuthorParts( string author )
    {
        string[] parts = author.Split(
            ',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries );
        if (parts.Length == 0)
        {
            yield return author.Trim();
            yield break;
        }

        foreach (string part in parts)
        {
            if (!string.IsNullOrWhiteSpace( part ))
            {
                yield return part.Trim();
            }
        }
    }

    private static (string FirstName, string? LastName, string FullName) SplitAuthorName( string author )
    {
        string fullName = author.Trim();
        if (string.IsNullOrWhiteSpace( fullName ))
        {
            return (string.Empty, null, string.Empty);
        }

        string[] parts = fullName.Split(
            ' ',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries );
        if (parts.Length == 0)
        {
            return (fullName, null, fullName);
        }

        if (parts.Length == 1)
        {
            return (parts[0], null, fullName);
        }

        return (parts[0], string.Join( ' ', parts.Skip( 1 ) ), fullName);
    }

    private static string SerializeAuthorListMetafieldValue( string author )
    {
        string[] entries = author
            .Split( ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries )
            .Where( part => !string.IsNullOrWhiteSpace( part ) )
            .Select( part => part.Trim() )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .ToArray();

        if (entries.Length == 0)
        {
            entries = [author.Trim()];
        }

        return JsonSerializer.Serialize( entries );
    }

    private const string MetafieldsSetMutation = """
        mutation MetafieldsSet($metafields: [MetafieldsSetInput!]!) {
          metafieldsSet(metafields: $metafields) {
            metafields { id namespace key }
            userErrors { field message }
          }
        }
        """;

    private const string BookAuthorMetafieldDefinitionQuery = """
        query BookAuthorMetafieldDefinition {
          metafieldDefinitions(
            first: 10
            ownerType: PRODUCT
            key: "author"
          ) {
            nodes {
              namespace
              key
              type { name }
              constraints {
                key
                values(first: 10) {
                  nodes {
                    value
                  }
                }
              }
            }
          }
        }
        """;

    private const string ProductUpdateCategoryMutation = """
        mutation ProductUpdateCategory($product: ProductUpdateInput!) {
          productUpdate(product: $product) {
            product { id category { id } }
            userErrors { field message }
          }
        }
        """;

    private const string VerifyProductMetafieldQuery = """
        query VerifyProductMetafield($id: ID!, $namespace: String!, $key: String!) {
          product(id: $id) {
            metafield(namespace: $namespace, key: $key) {
              value
              type
            }
          }
        }
        """;

    private sealed record BookAuthorMetafieldTarget(
        string Namespace,
        string Key,
        string TypeName,
        string? CategoryGid );

    private static readonly ConcurrentDictionary<string, BookAuthorMetafieldTarget?> BookAuthorTargetCache = new();

    private async Task TrySetAuthorMetafieldsAsync(
        HttpClient client,
        string shop,
        string accessToken,
        long productId,
        string author )
    {
        BookAuthorMetafieldTarget target = await ResolveBookAuthorMetafieldTargetAsync(
            shop,
            accessToken );
        if (string.IsNullOrWhiteSpace( target.CategoryGid ))
        {
            throw new InvalidOperationException(
                "Не знайшлі катэгорыю для metafield book.author у Shopify. " +
                "Праверце, што metafield «Аўтар» прывязаны да катэгорыі кнігі." );
        }

        string listValue = SerializeAuthorListMetafieldValue( author );

        if (await TrySetAuthorViaProductUpdateAsync(
                shop,
                accessToken,
                productId,
                target,
                listValue ))
        {
            return;
        }

        if (await TrySetBookAuthorMetafieldGraphqlAsync(
                shop,
                accessToken,
                productId,
                target,
                listValue ))
        {
            return;
        }

        await UpsertProductMetafieldAsync(
            client,
            shop,
            accessToken,
            productId,
            target.Namespace,
            target.Key,
            listValue,
            target.TypeName );

        if (!await VerifyAuthorMetafieldAsync( shop, accessToken, productId, target, listValue ))
        {
            throw new InvalidOperationException(
                $"Не ўдалося запісаць metafield «{target.Namespace}.{target.Key}» у Shopify." );
        }
    }

    private async Task<BookAuthorMetafieldTarget> ResolveBookAuthorMetafieldTargetAsync(
        string shop,
        string accessToken )
    {
        if (BookAuthorTargetCache.TryGetValue( shop, out BookAuthorMetafieldTarget? cached )
            && cached is not null)
        {
            return cached;
        }

        BookAuthorMetafieldTarget fallback = new(
            BookAuthorMetafieldNamespace,
            BookAuthorMetafieldKey,
            "list.single_line_text_field",
            null );

        (bool success, JsonDocument? document, string? _) = await _graphql.TryExecuteAsync(
            shop,
            accessToken,
            BookAuthorMetafieldDefinitionQuery );
        if (!success || document is null)
        {
            BookAuthorTargetCache[shop] = fallback;
            return fallback;
        }

        using (document)
        {
            BookAuthorMetafieldTarget? parsed = ParseBookAuthorMetafieldTarget( document );
            BookAuthorMetafieldTarget resolved = parsed ?? fallback;
            BookAuthorTargetCache[shop] = resolved;
            return resolved;
        }
    }

    private static BookAuthorMetafieldTarget? ParseBookAuthorMetafieldTarget( JsonDocument document )
    {
        if (!document.RootElement.TryGetProperty( "data", out JsonElement data ) ||
            !data.TryGetProperty( "metafieldDefinitions", out JsonElement definitions ) ||
            !definitions.TryGetProperty( "nodes", out JsonElement nodes ) ||
            nodes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        BookAuthorMetafieldTarget? preferred = null;
        BookAuthorMetafieldTarget? constrained = null;

        foreach (JsonElement definition in nodes.EnumerateArray())
        {
            string ns = definition.TryGetProperty( "namespace", out JsonElement nsEl ) &&
                        nsEl.ValueKind == JsonValueKind.String
                ? (nsEl.GetString() ?? string.Empty).Trim()
                : string.Empty;
            string key = definition.TryGetProperty( "key", out JsonElement keyEl ) &&
                         keyEl.ValueKind == JsonValueKind.String
                ? (keyEl.GetString() ?? string.Empty).Trim()
                : string.Empty;
            if (string.IsNullOrWhiteSpace( ns ) || !string.Equals( key, "author", StringComparison.OrdinalIgnoreCase ))
            {
                continue;
            }

            string typeName = "list.single_line_text_field";
            if (definition.TryGetProperty( "type", out JsonElement typeEl ) &&
                typeEl.ValueKind == JsonValueKind.Object &&
                typeEl.TryGetProperty( "name", out JsonElement typeNameEl ) &&
                typeNameEl.ValueKind == JsonValueKind.String)
            {
                string? parsedType = typeNameEl.GetString();
                if (!string.IsNullOrWhiteSpace( parsedType ))
                {
                    typeName = parsedType.Trim();
                }
            }

            string? categoryGid = ParseCategoryGidFromDefinitionNode( definition );
            BookAuthorMetafieldTarget candidate = new( ns, key, typeName, categoryGid );
            if (string.Equals( ns, BookAuthorMetafieldNamespace, StringComparison.OrdinalIgnoreCase ))
            {
                preferred = candidate;
                break;
            }

            if (categoryGid is not null)
            {
                constrained ??= candidate;
            }
        }

        return preferred ?? constrained;
    }

    private static string? ParseCategoryGidFromDefinitionNode( JsonElement definition )
    {
        if (!definition.TryGetProperty( "constraints", out JsonElement constraints ) ||
            constraints.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string constraintKey = constraints.TryGetProperty( "key", out JsonElement keyEl ) &&
                               keyEl.ValueKind == JsonValueKind.String
            ? (keyEl.GetString() ?? string.Empty)
            : string.Empty;
        if (!string.Equals( constraintKey, "category", StringComparison.OrdinalIgnoreCase ))
        {
            return null;
        }

        if (!constraints.TryGetProperty( "values", out JsonElement values ) ||
            !values.TryGetProperty( "nodes", out JsonElement valueNodes ) ||
            valueNodes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement valueNode in valueNodes.EnumerateArray())
        {
            if (valueNode.TryGetProperty( "value", out JsonElement valueEl ) &&
                valueEl.ValueKind == JsonValueKind.String)
            {
                string? gid = ShopifyIds.ToTaxonomyCategoryGid( valueEl.GetString() );
                if (!string.IsNullOrWhiteSpace( gid ))
                {
                    return gid;
                }
            }
        }

        return null;
    }

    private async Task<bool> TrySetAuthorViaProductUpdateAsync(
        string shop,
        string accessToken,
        long productId,
        BookAuthorMetafieldTarget target,
        string listValue )
    {
        if (string.IsNullOrWhiteSpace( target.CategoryGid ))
        {
            return false;
        }

        string productGid = $"gid://shopify/Product/{productId}";
        Dictionary<string, object?> product = new()
        {
            ["id"] = productGid,
            ["category"] = target.CategoryGid,
            ["metafields"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["namespace"] = target.Namespace,
                    ["key"] = target.Key,
                    ["type"] = target.TypeName,
                    ["value"] = listValue,
                },
            },
        };

        (bool success, JsonDocument? document, string? error) = await _graphql.TryExecuteAsync(
            shop,
            accessToken,
            ProductUpdateCategoryMutation,
            new { product } );
        if (!success || document is null)
        {
            return false;
        }

        using (document)
        {
            if (HasProductUpdateUserErrors( document, out string? userError ))
            {
                return false;
            }
        }

        return await VerifyAuthorMetafieldAsync( shop, accessToken, productId, target, listValue );
    }

    private async Task<bool> VerifyAuthorMetafieldAsync(
        string shop,
        string accessToken,
        long productId,
        BookAuthorMetafieldTarget target,
        string expectedListValue )
    {
        string productGid = $"gid://shopify/Product/{productId}";
        (bool success, JsonDocument? document, string? _) = await _graphql.TryExecuteAsync(
            shop,
            accessToken,
            VerifyProductMetafieldQuery,
            new
            {
                id = productGid,
                @namespace = target.Namespace,
                key = target.Key,
            } );
        if (!success || document is null)
        {
            return false;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty( "data", out JsonElement data ) ||
                !data.TryGetProperty( "product", out JsonElement product ) ||
                product.ValueKind != JsonValueKind.Object ||
                !product.TryGetProperty( "metafield", out JsonElement metafield ) ||
                metafield.ValueKind != JsonValueKind.Object ||
                !metafield.TryGetProperty( "value", out JsonElement valueEl ) ||
                valueEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? actual = valueEl.GetString();
            if (string.IsNullOrWhiteSpace( actual ))
            {
                return false;
            }

            try
            {
                JsonNode? expectedNode = JsonNode.Parse( expectedListValue );
                JsonNode? actualNode = JsonNode.Parse( actual );
                return JsonNode.DeepEquals( expectedNode, actualNode );
            }
            catch (JsonException)
            {
                return string.Equals(
                    actual.Trim(),
                    expectedListValue.Trim(),
                    StringComparison.OrdinalIgnoreCase );
            }
        }
    }

    private async Task<bool> TrySetBookAuthorMetafieldGraphqlAsync(
        string shop,
        string accessToken,
        long productId,
        BookAuthorMetafieldTarget target,
        string listValue )
    {
        if (!string.IsNullOrWhiteSpace( target.CategoryGid ))
        {
            await TrySetCategoryOnlyViaProductUpdateAsync(
                shop,
                accessToken,
                productId,
                target.CategoryGid );
        }

        string ownerId = $"gid://shopify/Product/{productId}";

        foreach (bool includeType in new[] { false, true })
        {
            Dictionary<string, object?> metafield = new()
            {
                ["ownerId"] = ownerId,
                ["namespace"] = target.Namespace,
                ["key"] = target.Key,
                ["value"] = listValue,
            };
            if (includeType)
            {
                metafield["type"] = target.TypeName;
            }

            (bool success, JsonDocument? document, string? _) = await _graphql.TryExecuteAsync(
                shop,
                accessToken,
                MetafieldsSetMutation,
                new { metafields = new[] { metafield } } );
            if (!success || document is null)
            {
                continue;
            }

            using (document)
            {
                if (!HasMetafieldsSetUserErrors( document )
                    && await VerifyAuthorMetafieldAsync(
                        shop,
                        accessToken,
                        productId,
                        target,
                        listValue ))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task TrySetCategoryOnlyViaProductUpdateAsync(
        string shop,
        string accessToken,
        long productId,
        string categoryGid )
    {
        string productGid = $"gid://shopify/Product/{productId}";
        (bool success, JsonDocument? document, string? error) = await _graphql.TryExecuteAsync(
            shop,
            accessToken,
            ProductUpdateCategoryMutation,
            new
            {
                product = new Dictionary<string, object?>
                {
                    ["id"] = productGid,
                    ["category"] = categoryGid,
                },
            } );
        if (!success || document is null)
        {
            throw new InvalidOperationException(
                $"Не ўдалося прывязаць катэгорыю кнігі ў Shopify: {error}" );
        }

        using (document)
        {
            if (HasProductUpdateUserErrors( document, out string? userError ))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace( userError )
                        ? "Не ўдалося прывязаць катэгорыю кнігі ў Shopify для metafield book.author."
                        : $"Не ўдалося прывязаць катэгорыю кнігі ў Shopify: {userError}" );
            }
        }
    }

    private static bool HasProductUpdateUserErrors(
        JsonDocument document,
        out string? userError )
    {
        userError = null;
        if (!document.RootElement.TryGetProperty( "data", out JsonElement data ) ||
            !data.TryGetProperty( "productUpdate", out JsonElement update ))
        {
            return true;
        }

        if (update.TryGetProperty( "userErrors", out JsonElement userErrors ) &&
            userErrors.ValueKind == JsonValueKind.Array &&
            userErrors.GetArrayLength() > 0)
        {
            userError = FormatGraphqlUserErrors( userErrors );
            return true;
        }

        if (update.TryGetProperty( "product", out JsonElement product ) &&
            product.ValueKind == JsonValueKind.Object)
        {
            return false;
        }

        return true;
    }

    private static string? FormatGraphqlUserErrors( JsonElement userErrors )
    {
        List<string> messages = new();
        foreach (JsonElement error in userErrors.EnumerateArray())
        {
            if (error.TryGetProperty( "message", out JsonElement messageEl ) &&
                messageEl.ValueKind == JsonValueKind.String)
            {
                string? message = messageEl.GetString();
                if (!string.IsNullOrWhiteSpace( message ))
                {
                    messages.Add( message.Trim() );
                }
            }
        }

        return messages.Count == 0 ? null : string.Join( "; ", messages );
    }

    private static bool HasMetafieldsSetUserErrors( JsonDocument document )
    {
        if (!document.RootElement.TryGetProperty( "data", out JsonElement data ) ||
            !data.TryGetProperty( "metafieldsSet", out JsonElement set ))
        {
            return true;
        }

        if (set.TryGetProperty( "userErrors", out JsonElement userErrors ) &&
            userErrors.ValueKind == JsonValueKind.Array &&
            userErrors.GetArrayLength() > 0)
        {
            return true;
        }

        if (set.TryGetProperty( "metafields", out JsonElement metafields ) &&
            metafields.ValueKind == JsonValueKind.Array &&
            metafields.GetArrayLength() > 0)
        {
            return false;
        }

        return true;
    }

    private static async Task UpsertProductMetafieldAsync(
        HttpClient client,
        string shop,
        string accessToken,
        long productId,
        string metafieldNamespace,
        string key,
        string value,
        string metafieldType )
    {
        if (string.IsNullOrWhiteSpace( value ))
        {
            return;
        }

        long? existingId = await FindProductMetafieldIdAsync(
            client,
            shop,
            accessToken,
            productId,
            metafieldNamespace,
            key );

        string payload;
        string url;
        HttpMethod method;
        if (existingId is > 0)
        {
            payload = JsonSerializer.Serialize( new
            {
                metafield = new
                {
                    id = existingId.Value,
                    value,
                    type = metafieldType,
                }
            } );
            url = ShopifyApi.RestUrl( shop, $"metafields/{existingId.Value}.json" );
            method = HttpMethod.Put;
        }
        else
        {
            payload = JsonSerializer.Serialize( new
            {
                metafield = new
                {
                    @namespace = metafieldNamespace,
                    key,
                    value,
                    type = metafieldType,
                }
            } );
            url = ShopifyApi.RestUrl( shop, $"products/{productId}/metafields.json" );
            method = HttpMethod.Post;
        }

        using StringContent content = new( payload, Encoding.UTF8, "application/json" );
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            method,
            url,
            content );
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Не ўдалося запісаць metafield «{metafieldNamespace}.{key}» у Shopify: {body}" );
        }
    }

    private static async Task<long?> FindProductMetafieldIdAsync(
        HttpClient client,
        string shop,
        string accessToken,
        long productId,
        string metafieldNamespace,
        string key )
    {
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Get,
            ShopifyApi.RestUrl( shop, $"products/{productId}/metafields.json" ) );
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse( body );
        if (!json.RootElement.TryGetProperty( "metafields", out JsonElement metafields ) ||
            metafields.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement metafield in metafields.EnumerateArray())
        {
            string ns = metafield.TryGetProperty( "namespace", out JsonElement nsEl ) &&
                        nsEl.ValueKind == JsonValueKind.String
                ? (nsEl.GetString() ?? string.Empty)
                : string.Empty;
            string metafieldKey = metafield.TryGetProperty( "key", out JsonElement keyEl ) &&
                                  keyEl.ValueKind == JsonValueKind.String
                ? (keyEl.GetString() ?? string.Empty)
                : string.Empty;
            if (!string.Equals( ns, metafieldNamespace, StringComparison.Ordinal ) ||
                !string.Equals( metafieldKey, key, StringComparison.Ordinal ))
            {
                continue;
            }

            if (metafield.TryGetProperty( "id", out JsonElement idEl ) &&
                idEl.TryGetInt64( out long id ))
            {
                return id;
            }
        }

        return null;
    }

    private static async Task SetProductMetafieldAsync(
        HttpClient client,
        string shop,
        string accessToken,
        long productId,
        string key,
        string value,
        string metafieldNamespace = "custom",
        string metafieldType = "single_line_text_field" )
    {
        if (string.IsNullOrWhiteSpace( value ))
        {
            return;
        }

        string payload = JsonSerializer.Serialize( new
        {
            metafield = new
            {
                @namespace = metafieldNamespace,
                key,
                value,
                type = metafieldType,
            }
        } );

        using StringContent content = new( payload, Encoding.UTF8, "application/json" );
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Post,
            ShopifyApi.RestUrl( shop, $"products/{productId}/metafields.json" ),
            content );
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Не ўдалося запісаць metafield «{key}» у Shopify: {body}" );
        }
    }

    private static bool IsShopifyBarcodeConflict( string body )
    {
        if (string.IsNullOrWhiteSpace( body ))
        {
            return false;
        }

        string lower = body.ToLowerInvariant();
        return lower.Contains( "barcode" ) &&
               (lower.Contains( "already" ) || lower.Contains( "taken" ) || lower.Contains( "unique" ));
    }

    /// <summary>
    /// Sets Shopify InventoryItem.cost for the product (or specific variant when provided).
    /// </summary>
    public async Task SetVariantCostByProductKeyAsync(
        string shop,
        string accessToken,
        string shopifyProductId,
        string? shopifyVariantId,
        decimal unitCost )
    {
        if (unitCost < 0m)
        {
            throw new InvalidOperationException( "Кошт не можа быць адмоўным." );
        }

        HttpClient client = _httpClientFactory.CreateClient( "Shopify" );
        long? productId = ShopifyIds.TryParseNumericProductId( shopifyProductId );
        if (!productId.HasValue)
        {
            throw new InvalidOperationException( "Некарэктны Shopify ID прадукту." );
        }

        long inventoryItemId;
        if (!string.IsNullOrWhiteSpace( shopifyVariantId ))
        {
            long? variantId = ShopifyIds.TryParseNumericVariantId( shopifyVariantId );
            if (!variantId.HasValue)
            {
                throw new InvalidOperationException( "Некарэктны Shopify ID варыянта." );
            }

            inventoryItemId = await GetInventoryItemIdByVariantAsync(
                client,
                shop,
                variantId.Value,
                accessToken );
        }
        else
        {
            inventoryItemId = await GetInventoryItemIdByProductAsync(
                client,
                shop,
                productId.Value,
                accessToken );
        }

        await SetInventoryItemCostAsync( client, shop, inventoryItemId, unitCost, accessToken );
    }

    public async Task<Dictionary<string, decimal>> GetVariantPricesByProductKeysAsync(
        string shop,
        string accessToken,
        IEnumerable<(string ProductId, string VariantId)> lineKeys )
    {
        Dictionary<string, decimal> prices = new( StringComparer.OrdinalIgnoreCase );
        IEnumerable<(string ProductId, string VariantId)> normalizedKeys = lineKeys
            .Select( key =>
            (
                ProductId: ShopifyIds.NormalizeProductId( key.ProductId ),
                VariantId: ShopifyIds.NormalizeVariantId( key.VariantId )
            ) )
            .Where( key => !string.IsNullOrWhiteSpace( key.ProductId ) )
            .Distinct();

        HttpClient client = _httpClientFactory.CreateClient( "Shopify" );
        foreach (IGrouping<string, (string ProductId, string VariantId)> productGroup in normalizedKeys.GroupBy( x => x.ProductId, StringComparer.OrdinalIgnoreCase ))
        {
            long? productId = ShopifyIds.TryParseNumericProductId( productGroup.Key );
            if (!productId.HasValue)
            {
                continue;
            }

            JsonElement product;
            try
            {
                product = await GetProductJsonAsync( client, shop, productId.Value, accessToken );
            }
            catch
            {
                continue;
            }

            JsonElement variants = product.GetProperty( "variants" );
            Dictionary<string, decimal> variantPrices = new( StringComparer.OrdinalIgnoreCase );
            string? defaultVariantId = null;
            foreach (JsonElement variant in variants.EnumerateArray())
            {
                string variantId = variant.GetProperty( "id" ).GetInt64().ToString( );
                variantPrices[variantId] = ParseShopifyPrice( variant );
                defaultVariantId ??= variantId;
            }

            foreach ((string ProductId, string VariantId) key in productGroup)
            {
                string lookupVariantId = string.IsNullOrWhiteSpace( key.VariantId )
                    ? defaultVariantId ?? string.Empty
                    : key.VariantId;
                if (string.IsNullOrWhiteSpace( lookupVariantId ))
                {
                    continue;
                }

                if (variantPrices.TryGetValue( lookupVariantId, out decimal price ))
                {
                    prices[BuildLinePriceKey( key.ProductId, key.VariantId )] = price;
                }
            }
        }

        return prices;
    }

    private static string BuildLinePriceKey( string productId, string variantId ) =>
        string.IsNullOrWhiteSpace( variantId ) ? productId : $"{productId}::{variantId}";

    private static decimal ParseShopifyPrice( JsonElement variant )
    {
        if (!variant.TryGetProperty( "price", out JsonElement priceEl ))
        {
            return 0m;
        }

        string? raw = priceEl.GetString();
        return decimal.TryParse( raw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed )
            ? parsed
            : 0m;
    }

    public async Task<List<SupplyInventoryUpdateResult>> ApplySupplySyncAsync(
        string shop,
        string accessToken,
        Dictionary<string, int> deltas,
        Dictionary<string, decimal> syncedSalePrices )
    {
        List<SupplyInventoryUpdateResult> result = new();
        HttpClient client = _httpClientFactory.CreateClient( "Shopify" );
        long locationId = await ResolveInventoryLocationIdAsync( client, shop, accessToken );

        // Inventory deltas first, then price-only lines.
        List<string> orderedKeys = new( deltas.Keys );
        foreach (string priceKey in syncedSalePrices.Keys)
        {
            if (deltas.ContainsKey( priceKey ))
            {
                continue;
            }

            if (syncedSalePrices[priceKey] > 0)
            {
                orderedKeys.Add( priceKey );
            }
        }

        foreach (string key in orderedKeys)
        {
            if (!TryParseSyncKey( key, out long productId, out long? variantId ))
            {
                continue;
            }

            int delta = deltas.TryGetValue( key, out int d ) ? d : 0;
            decimal salePrice = syncedSalePrices.TryGetValue( key, out decimal p ) ? p : 0;

            if (delta != 0)
            {
                long inventoryItemId = variantId.HasValue
                    ? await GetInventoryItemIdByVariantAsync( client, shop, variantId.Value, accessToken )
                    : await GetInventoryItemIdByProductAsync( client, shop, productId, accessToken );
                (int current, int next) = await ApplyInventoryDeltaAtLocationAsync(
                    client,
                    shop,
                    accessToken,
                    inventoryItemId,
                    locationId,
                    delta );
                result.Add( new SupplyInventoryUpdateResult
                {
                    ShopifyProductId = key.Trim(),
                    PreviousAvailable = current,
                    AddedQuantity = delta,
                    NewAvailable = next
                } );
            }

            if (salePrice > 0)
            {
                long resolvedVariantId = variantId
                    ?? await GetPrimaryVariantIdByProductAsync( client, shop, productId, accessToken );
                await SetVariantPriceAsync( client, shop, resolvedVariantId, salePrice, accessToken );
            }
        }

        return result;
    }

    private async Task SetVariantPriceByProductKeyAsync(
        string shop,
        string accessToken,
        string shopifyProductId,
        decimal salePrice,
        HttpClient client )
    {
        long? productId = ShopifyIds.TryParseNumericProductId( shopifyProductId );
        if (!productId.HasValue)
        {
            throw new InvalidOperationException( "Некарэктны Shopify ID прадукту." );
        }

        long variantId = await GetPrimaryVariantIdByProductAsync( client, shop, productId.Value, accessToken );
        await SetVariantPriceAsync( client, shop, variantId, salePrice, accessToken );
    }

    private static async Task<long> ResolveInventoryLocationIdAsync(
        HttpClient client,
        string shop,
        string accessToken )
    {
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Get,
            ShopifyApi.RestUrl( shop, "locations.json?limit=250" )
        );
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException( $"Не ўдалося атрымаць лакацыю Shopify: {body}" );
        }

        using JsonDocument json = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
        JsonElement locations = json.RootElement.GetProperty( "locations" );
        if (locations.GetArrayLength() == 0)
        {
            throw new InvalidOperationException( "У Shopify не знойдзены склад (location)." );
        }

        long? preferredId = null;
        long? fallbackId = null;
        foreach (JsonElement location in locations.EnumerateArray())
        {
            if (location.TryGetProperty( "active", out JsonElement activeEl )
                && activeEl.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            long id = location.GetProperty( "id" ).GetInt64();
            fallbackId ??= id;

            string? name = location.TryGetProperty( "name", out JsonElement nameEl )
                ? nameEl.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace( name )
                && string.Equals(
                    name.Trim(),
                    PreferredInventoryLocationName,
                    StringComparison.OrdinalIgnoreCase ))
            {
                preferredId = id;
                break;
            }
        }

        if (preferredId.HasValue)
        {
            return preferredId.Value;
        }

        if (fallbackId.HasValue)
        {
            return fallbackId.Value;
        }

        return locations[0].GetProperty( "id" ).GetInt64();
    }

    private static async Task<(int Previous, int Next)> ApplyInventoryDeltaAtLocationAsync(
        HttpClient client,
        string shop,
        string accessToken,
        long inventoryItemId,
        long locationId,
        int delta )
    {
        if (!await HasInventoryLevelAtLocationAsync(
                client,
                shop,
                inventoryItemId,
                locationId,
                accessToken ))
        {
            await ConnectInventoryLevelAsync(
                client,
                shop,
                inventoryItemId,
                locationId,
                accessToken );
        }

        int current = await GetAvailableQuantityAsync(
            client,
            shop,
            inventoryItemId,
            locationId,
            accessToken );
        int next = Math.Max( 0, current + delta );
        await SetAvailableQuantityAsync(
            client,
            shop,
            inventoryItemId,
            locationId,
            next,
            accessToken );
        return (current, next);
    }

    private static async Task<bool> HasInventoryLevelAtLocationAsync(
        HttpClient client,
        string shop,
        long inventoryItemId,
        long locationId,
        string accessToken )
    {
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Get,
            ShopifyApi.RestUrl(
                shop,
                $"inventory_levels.json?inventory_item_ids={inventoryItemId}&location_ids={locationId}"
            )
        );
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException( $"Не ўдалося атрымаць inventory level: {body}" );
        }

        using JsonDocument json = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
        return json.RootElement.GetProperty( "inventory_levels" ).GetArrayLength() > 0;
    }

    private static async Task ConnectInventoryLevelAsync(
        HttpClient client,
        string shop,
        long inventoryItemId,
        long locationId,
        string accessToken )
    {
        string payload = JsonSerializer.Serialize( new
        {
            location_id = locationId,
            inventory_item_id = inventoryItemId
        } );

        using StringContent content = new( payload, Encoding.UTF8, "application/json" );
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Post,
            ShopifyApi.RestUrl( shop, "inventory_levels/connect.json" ),
            content
        );
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Не ўдалося падключыць inventory item да лакацыі: {body}" );
        }
    }

    private static bool TryParseSyncKey( string key, out long productId, out long? variantId )
    {
        productId = 0;
        variantId = null;
        string trimmed = key.Trim();
        if (string.IsNullOrEmpty( trimmed ))
        {
            return false;
        }

        string productPart = trimmed;
        string? variantPart = null;
        int sep = trimmed.IndexOf( "::", StringComparison.Ordinal );
        if (sep >= 0)
        {
            productPart = trimmed[..sep];
            variantPart = trimmed[(sep + 2)..];
        }

        long? parsedProductId = ShopifyIds.TryParseNumericProductId( productPart );
        if (!parsedProductId.HasValue)
        {
            return false;
        }

        productId = parsedProductId.Value;
        if (!string.IsNullOrWhiteSpace( variantPart ))
        {
            long? parsedVariantId = ShopifyIds.TryParseNumericVariantId( variantPart );
            if (!parsedVariantId.HasValue)
            {
                return false;
            }

            variantId = parsedVariantId.Value;
        }

        return true;
    }

    private static async Task<long> GetInventoryItemIdByVariantAsync(
        HttpClient client,
        string shop,
        long variantId,
        string accessToken )
    {
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Get,
            ShopifyApi.RestUrl( shop, $"variants/{variantId}.json" )
        );
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException( $"Не ўдалося атрымаць варыянт {variantId} з Shopify: {body}" );
        }

        using JsonDocument json = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
        return json.RootElement.GetProperty( "variant" ).GetProperty( "inventory_item_id" ).GetInt64();
    }

    private static async Task<long> GetInventoryItemIdByProductAsync(
        HttpClient client,
        string shop,
        long productId,
        string accessToken )
    {
        JsonElement product = await GetProductJsonAsync( client, shop, productId, accessToken );
        JsonElement variants = product.GetProperty( "variants" );
        if (variants.GetArrayLength() == 0)
        {
            throw new InvalidOperationException( $"Для прадукту {productId} няма варыянтаў." );
        }

        return variants[0].GetProperty( "inventory_item_id" ).GetInt64();
    }

    private static async Task<long> GetPrimaryVariantIdByProductAsync(
        HttpClient client,
        string shop,
        long productId,
        string accessToken )
    {
        JsonElement product = await GetProductJsonAsync( client, shop, productId, accessToken );
        JsonElement variants = product.GetProperty( "variants" );
        if (variants.GetArrayLength() == 0)
        {
            throw new InvalidOperationException( $"Для прадукту {productId} няма варыянтаў." );
        }

        return variants[0].GetProperty( "id" ).GetInt64();
    }

    private static async Task<JsonElement> GetProductJsonAsync(
        HttpClient client,
        string shop,
        long productId,
        string accessToken )
    {
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Get,
            ShopifyApi.RestUrl( shop, $"products/{productId}.json" )
        );
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException( $"Не ўдалося атрымаць прадукт {productId} з Shopify: {body}" );
        }

        using JsonDocument json = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
        return json.RootElement.GetProperty( "product" ).Clone();
    }

    /// <summary>
    /// True when the sync key ("productId" or "productId::variantId") still resolves
    /// to a live Shopify product/variant. False on 404 (deleted). Other errors rethrow.
    /// </summary>
    public async Task<bool> ProductKeyExistsAsync(
        string shop,
        string accessToken,
        string productKey )
    {
        if (!TryParseSyncKey( productKey, out long productId, out long? variantId ))
        {
            string normalized = ShopifyIds.NormalizeProductId( productKey );
            long? parsed = ShopifyIds.TryParseNumericProductId( normalized );
            if (!parsed.HasValue)
            {
                return false;
            }

            productId = parsed.Value;
            variantId = null;
        }

        HttpClient client = _httpClientFactory.CreateClient( "Shopify" );
        string url = variantId.HasValue
            ? ShopifyApi.RestUrl( shop, $"variants/{variantId.Value}.json" )
            : ShopifyApi.RestUrl( shop, $"products/{productId}.json" );

        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Get,
            url );
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Не ўдалося праверыць Shopify {productKey}: {body}" );
        }

        return true;
    }

    private static async Task<int> GetAvailableQuantityAsync(
        HttpClient client,
        string shop,
        long inventoryItemId,
        long locationId,
        string accessToken )
    {
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Get,
            ShopifyApi.RestUrl(
                shop,
                $"inventory_levels.json?inventory_item_ids={inventoryItemId}&location_ids={locationId}"
            )
        );
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException( $"Не ўдалося атрымаць inventory level: {body}" );
        }

        using JsonDocument json = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
        JsonElement levels = json.RootElement.GetProperty( "inventory_levels" );
        if (levels.GetArrayLength() == 0) return 0;
        JsonElement availableEl = levels[0].GetProperty( "available" );
        return availableEl.ValueKind == JsonValueKind.Number ? availableEl.GetInt32() : 0;
    }

    private static async Task SetAvailableQuantityAsync(
        HttpClient client,
        string shop,
        long inventoryItemId,
        long locationId,
        int available,
        string accessToken )
    {
        string payload = JsonSerializer.Serialize( new
        {
            location_id = locationId,
            inventory_item_id = inventoryItemId,
            available
        } );

        using StringContent content = new( payload, Encoding.UTF8, "application/json" );
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Post,
            ShopifyApi.RestUrl( shop, "inventory_levels/set.json" ),
            content
        );
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException( $"Не ўдалося ўсталяваць inventory level: {body}" );
        }
    }

    private static async Task SetVariantPriceAsync(
        HttpClient client,
        string shop,
        long variantId,
        decimal salePrice,
        string accessToken )
    {
        string priceString = salePrice.ToString( "0.00", CultureInfo.InvariantCulture );
        string payload = JsonSerializer.Serialize( new
        {
            variant = new
            {
                id = variantId,
                price = priceString
            }
        } );

        using StringContent content = new( payload, Encoding.UTF8, "application/json" );
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Put,
            ShopifyApi.RestUrl( shop, $"variants/{variantId}.json" ),
            content
        );
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException( $"Не ўдалося абнавіць цану ў Shopify: {body}" );
        }
    }

    private static async Task SetVariantWeightAsync(
        HttpClient client,
        string shop,
        long variantId,
        decimal weightKg,
        string accessToken )
    {
        decimal rounded = Math.Round( weightKg, 3, MidpointRounding.AwayFromZero );
        long inventoryItemId = await GetInventoryItemIdByVariantAsync(
            client,
            shop,
            variantId,
            accessToken );

        string payload = JsonSerializer.Serialize( new
        {
            inventory_item = new
            {
                id = inventoryItemId,
                weight = rounded,
                weight_unit = "kg",
            }
        } );

        using StringContent content = new( payload, Encoding.UTF8, "application/json" );
        using HttpResponseMessage inventoryResponse = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Put,
            ShopifyApi.RestUrl( shop, $"inventory_items/{inventoryItemId}.json" ),
            content );
        if (inventoryResponse.IsSuccessStatusCode)
        {
            return;
        }

        string variantPayload = JsonSerializer.Serialize( new
        {
            variant = new
            {
                id = variantId,
                weight = rounded,
                weight_unit = "kg",
            }
        } );

        using StringContent variantContent = new( variantPayload, Encoding.UTF8, "application/json" );
        using HttpResponseMessage variantResponse = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Put,
            ShopifyApi.RestUrl( shop, $"variants/{variantId}.json" ),
            variantContent );
        if (!variantResponse.IsSuccessStatusCode)
        {
            string body = await variantResponse.Content.ReadAsStringAsync();
            throw new InvalidOperationException( $"Не ўдалося абнавіць вагу ў Shopify: {body}" );
        }
    }

    private static async Task SetInventoryItemCostAsync(
        HttpClient client,
        string shop,
        long inventoryItemId,
        decimal unitCost,
        string accessToken )
    {
        string costString = unitCost.ToString( "0.00", CultureInfo.InvariantCulture );
        string payload = JsonSerializer.Serialize( new
        {
            inventory_item = new
            {
                id = inventoryItemId,
                cost = costString
            }
        } );

        using StringContent content = new( payload, Encoding.UTF8, "application/json" );
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Put,
            ShopifyApi.RestUrl( shop, $"inventory_items/{inventoryItemId}.json" ),
            content
        );
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException( $"Не ўдалося абнавіць кошт у Shopify: {body}" );
        }
    }
}
