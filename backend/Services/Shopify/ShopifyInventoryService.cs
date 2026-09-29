using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using backend.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace backend.Services.Shopify;

public class ShopifyInventoryService
{
    public const string DefaultBookProductType = "Кніга";
    /// <summary>Shopify Standard Product Taxonomy: Media &gt; Books &gt; Print Books.</summary>
    public const string PrintBooksTaxonomyCategoryGid = "gid://shopify/TaxonomyCategory/aa-1-13-8";
    public const string BookAuthorMetafieldNamespace = "book";
    public const string BookAuthorMetafieldKey = "author";
    public const string BookGenreMetafieldNamespace = "book";
    public const string BookGenreMetafieldKey = "genre";
    private const string PreferredInventoryLocationName = "Bukinistka";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ShopifyGraphqlClient _graphql;
    private readonly IConfiguration _config;
    private readonly ILogger<ShopifyInventoryService> _logger;

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
        string? CoverType,
        IReadOnlyList<string>? Genres = null,
        bool PublishAsDraft = true,
        string? SeoTitle = null,
        string? SeoDescription = null,
        string? Handle = null,
        string? AgeRating = null,
        string? Format = null,
        string? Illustrator = null,
        string? Language = null,
        string? PlaceOfPublication = null,
        string? Translation = null,
        int? PageCount = null,
        int? Year = null,
        bool IncludeCoverTypeInTags = true );

    public sealed record BookGenreMetafieldOptions(
        string Namespace,
        string Key,
        string TypeName,
        IReadOnlyList<string> Options,
        string? CategoryGid );

    public sealed record CreatedShopifyProduct(
        string ProductId,
        string VariantId,
        string Title );

    public ShopifyInventoryService(
        IHttpClientFactory httpClientFactory,
        ShopifyGraphqlClient graphql,
        IConfiguration config,
        ILogger<ShopifyInventoryService> logger )
    {
        _httpClientFactory = httpClientFactory;
        _graphql = graphql;
        _config = config;
        _logger = logger;
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
        else
        {
            productPayload["status"] = "active";
        }

        string? handle = (input.Handle ?? string.Empty).Trim();
        string? seoTitle = (input.SeoTitle ?? string.Empty).Trim();
        string? seoDescription = (input.SeoDescription ?? string.Empty).Trim();

        // Prefer setting SEO after create — some shops reject global SEO tags on REST create.
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
        string tags = BuildBookProductTags(
            barcodeForTags,
            authorForTags,
            input.IncludeCoverTypeInTags ? input.CoverType : null );
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
        if (!response.IsSuccessStatusCode && IsShopifyBarcodeConflict( body )
            && !string.IsNullOrWhiteSpace( input.Barcode )
            && variant.ContainsKey( "barcode" ))
        {
            // ISBN already used — still create the product without barcode.
            _logger.LogWarning(
                "Barcode conflict on create; retrying without barcode. Body={Body}",
                body.Length > 400 ? body[..400] : body );
            variant.Remove( "barcode" );
            productPayload["variants"] = new[] { variant };
            // Also drop ISBN-only tag duplicate if it was the only barcode tag — keep author tags.
            string tagsWithoutBarcode = BuildBookProductTags(
                null,
                authorForTags,
                input.IncludeCoverTypeInTags ? input.CoverType : null );
            if (!string.IsNullOrWhiteSpace( tagsWithoutBarcode ))
            {
                productPayload["tags"] = tagsWithoutBarcode;
            }
            else
            {
                productPayload.Remove( "tags" );
            }

            string retryPayload = JsonSerializer.Serialize( new { product = productPayload } );
            using StringContent retryContent = new( retryPayload, Encoding.UTF8, "application/json" );
            using HttpResponseMessage retryResponse = await ShopifyAuthorizedHttp.SendAsync(
                client,
                accessToken,
                HttpMethod.Post,
                ShopifyApi.RestUrl( shop, "products.json" ),
                retryContent );
            body = await retryResponse.Content.ReadAsStringAsync();
            if (!retryResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException( $"Не ўдалося стварыць тавар у Shopify: {body}" );
            }
        }
        else if (!response.IsSuccessStatusCode)
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

        await TrySetProductSeoAsync(
            shop,
            accessToken,
            normalizedProductId,
            handle,
            seoTitle,
            seoDescription );

        string? author = (input.Author ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace( author ))
        {
            try
            {
                await TrySetAuthorMetafieldsAsync(
                    client,
                    shop,
                    accessToken,
                    productId,
                    author );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to write book.author metafield for product {ProductId}",
                    productId );
            }
        }

        IReadOnlyList<string> genres = NormalizeGenreLabels( input.Genres );
        if (genres.Count > 0)
        {
            try
            {
                await TrySetGenreMetafieldsAsync(
                    client,
                    shop,
                    accessToken,
                    productId,
                    genres );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to write book.genre metafield for product {ProductId}",
                    productId );
            }
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

        await TrySetBookBibliographicMetafieldsAsync(
            client,
            shop,
            accessToken,
            productId,
            input );

        if (input.UnitCost >= 0m)
        {
            try
            {
                await SetVariantCostByProductKeyAsync(
                    shop,
                    accessToken,
                    normalizedProductId,
                    normalizedVariantId,
                    Math.Round( input.UnitCost, 2, MidpointRounding.AwayFromZero ) );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to set unit cost for product {ProductId}",
                    productId );
            }
        }

        if (input.WeightKg is decimal weightKg && weightKg > 0m)
        {
            try
            {
                await SetVariantWeightAsync(
                    client,
                    shop,
                    variantId,
                    Math.Round( weightKg, 3, MidpointRounding.AwayFromZero ),
                    accessToken );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to set weight for product {ProductId}",
                    productId );
            }
        }

        // Select all sales channels in Publishing (including drafts).
        await TryPublishProductToAllChannelsAsync( shop, accessToken, normalizedProductId );

        return new CreatedShopifyProduct(
            normalizedProductId,
            normalizedVariantId,
            title );
    }

    /// <summary>
    /// Minimal draft shell: title + description + sale price + optional book fields.
    /// Photo/category attached after create.
    /// </summary>
    public async Task<CreatedShopifyProduct> CreateMinimalDraftProductAsync(
        string shop,
        string accessToken,
        string title,
        string? descriptionHtml,
        decimal salePrice = 0m,
        string? barcode = null,
        decimal? weightKg = null,
        int? inventoryQuantity = null,
        string? author = null,
        string? coverType = null,
        string? ageRating = null,
        string? format = null,
        string? illustrator = null,
        string? language = null,
        string? placeOfPublication = null,
        string? translation = null,
        int? pageCount = null,
        int? year = null,
        IReadOnlyList<string>? genres = null,
        string? seoTitle = null,
        string? seoDescription = null,
        string? handle = null,
        string? vendor = null )
    {
        string normalizedTitle = (title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( normalizedTitle ))
        {
            throw new InvalidOperationException( "Укажыце назву тавару." );
        }

        if (salePrice < 0m)
        {
            throw new InvalidOperationException( "Цана продажу не можа быць адмоўнай." );
        }

        string priceString = Math.Round( salePrice, 2, MidpointRounding.AwayFromZero )
            .ToString( "0.00", CultureInfo.InvariantCulture );

        string? barcodeTrimmed = string.IsNullOrWhiteSpace( barcode ) ? null : barcode.Trim();
        string? authorTrimmed = string.IsNullOrWhiteSpace( author ) ? null : author.Trim();
        string? vendorTrimmed = string.IsNullOrWhiteSpace( vendor ) ? null : vendor.Trim();
        string? coverTypeNorm = BookProductCoverType.Normalize( coverType );
        string? ageRatingNorm = BookAgeRating.Normalize( ageRating )
            ?? (string.IsNullOrWhiteSpace( ageRating ) ? null : ageRating.Trim());
        string? formatNorm = BookBibliographicFields.NormalizeFormat( format )
            ?? (string.IsNullOrWhiteSpace( format ) ? null : format.Trim());
        string? illustratorTrimmed =
            string.IsNullOrWhiteSpace( illustrator ) ? null : illustrator.Trim();
        string? languageNorm = BookBibliographicFields.NormalizeLanguage( language )
            ?? (string.IsNullOrWhiteSpace( language ) ? null : language.Trim());
        string? placeTrimmed =
            string.IsNullOrWhiteSpace( placeOfPublication ) ? null : placeOfPublication.Trim();
        string? translationTrimmed =
            string.IsNullOrWhiteSpace( translation ) ? null : translation.Trim();
        int? pageCountNorm = pageCount is int pc && pc > 0 ? pc : null;
        int? yearNorm = year is int y && y > 0 ? y : null;
        IReadOnlyList<string> genresNorm = NormalizeGenreLabels( genres );
        decimal? weightRounded = weightKg is decimal w && w > 0m
            ? Math.Round( w, 3, MidpointRounding.AwayFromZero )
            : null;

        Dictionary<string, object?> variant = new()
        {
            ["price"] = priceString,
            ["inventory_management"] = "shopify",
            ["inventory_policy"] = "deny",
        };

        if (!string.IsNullOrWhiteSpace( barcodeTrimmed ))
        {
            variant["barcode"] = barcodeTrimmed;
        }

        if (weightRounded is decimal weightOnCreate)
        {
            variant["weight"] = weightOnCreate;
            variant["weight_unit"] = "kg";
        }

        Dictionary<string, object?> productPayload = new()
        {
            ["title"] = normalizedTitle,
            ["status"] = "draft",
            ["product_type"] = DefaultBookProductType,
            ["variants"] = new[] { variant },
        };

        if (!string.IsNullOrWhiteSpace( vendorTrimmed ))
        {
            productPayload["vendor"] = vendorTrimmed;
        }

        string? description = (descriptionHtml ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace( description ))
        {
            productPayload["body_html"] = description;
        }

        string tags = BuildBookProductTags( barcodeTrimmed, authorTrimmed, coverTypeNorm );
        if (!string.IsNullOrWhiteSpace( tags ))
        {
            productPayload["tags"] = tags;
        }

        HttpClient client = _httpClientFactory.CreateClient( "Shopify" );
        string payload = JsonSerializer.Serialize( new { product = productPayload } );
        using StringContent content = new( payload, Encoding.UTF8, "application/json" );
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Post,
            ShopifyApi.RestUrl( shop, "products.json" ),
            content );
        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode
            && IsShopifyBarcodeConflict( body )
            && variant.ContainsKey( "barcode" ))
        {
            _logger.LogWarning(
                "Barcode conflict on draft create; retrying without barcode. Body={Body}",
                body );
            variant.Remove( "barcode" );
            string tagsWithoutBarcode = BuildBookProductTags(
                null,
                authorTrimmed,
                coverTypeNorm );
            if (!string.IsNullOrWhiteSpace( tagsWithoutBarcode ))
            {
                productPayload["tags"] = tagsWithoutBarcode;
            }
            else
            {
                productPayload.Remove( "tags" );
            }

            productPayload["variants"] = new[] { variant };
            string retryPayload = JsonSerializer.Serialize( new { product = productPayload } );
            using StringContent retryContent = new( retryPayload, Encoding.UTF8, "application/json" );
            using HttpResponseMessage retryResponse = await ShopifyAuthorizedHttp.SendAsync(
                client,
                accessToken,
                HttpMethod.Post,
                ShopifyApi.RestUrl( shop, "products.json" ),
                retryContent );
            body = await retryResponse.Content.ReadAsStringAsync();
            if (!retryResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException( $"Не ўдалося стварыць чарнавік у Shopify: {body}" );
            }
        }
        else if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException( $"Не ўдалося стварыць чарнавік у Shopify: {body}" );
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

        await TrySetProductSeoAsync(
            shop,
            accessToken,
            normalizedProductId,
            handle,
            seoTitle,
            seoDescription );

        if (!string.IsNullOrWhiteSpace( authorTrimmed ))
        {
            try
            {
                await TrySetAuthorMetafieldsAsync(
                    client,
                    shop,
                    accessToken,
                    productId,
                    authorTrimmed );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to write author metafield for draft {ProductId}",
                    productId );
            }
        }

        if (genresNorm.Count > 0)
        {
            try
            {
                await TrySetGenreMetafieldsAsync(
                    client,
                    shop,
                    accessToken,
                    productId,
                    genresNorm );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to write book.genre metafield for draft {ProductId}",
                    productId );
            }
        }

        if (!string.IsNullOrWhiteSpace( barcodeTrimmed ))
        {
            try
            {
                await SetProductMetafieldAsync(
                    client,
                    shop,
                    accessToken,
                    productId,
                    "isbn",
                    barcodeTrimmed );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to write isbn metafield for draft {ProductId}",
                    productId );
            }
        }

        await TrySetBookBibliographicMetafieldsAsync(
            client,
            shop,
            accessToken,
            productId,
            new CreateShopifyProductInput(
                Title: normalizedTitle,
                SalePrice: salePrice,
                UnitCost: 0m,
                Vendor: vendorTrimmed,
                ProductType: DefaultBookProductType,
                Barcode: barcodeTrimmed,
                DescriptionHtml: description,
                Author: authorTrimmed,
                WeightKg: weightRounded,
                CoverType: coverTypeNorm,
                Genres: genresNorm,
                AgeRating: ageRatingNorm,
                Format: formatNorm,
                Illustrator: illustratorTrimmed,
                Language: languageNorm,
                PlaceOfPublication: placeTrimmed,
                Translation: translationTrimmed,
                PageCount: pageCountNorm,
                Year: yearNorm,
                IncludeCoverTypeInTags: false ) );

        if (weightRounded is decimal weightAfter)
        {
            try
            {
                await SetVariantWeightAsync(
                    client,
                    shop,
                    variantId,
                    weightAfter,
                    accessToken );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to set weight for draft {ProductId}",
                    productId );
            }
        }

        if (inventoryQuantity is int qty && qty > 0)
        {
            try
            {
                long locationId = await ResolveInventoryLocationIdAsync( client, shop, accessToken );
                long inventoryItemId = await GetInventoryItemIdByVariantAsync(
                    client,
                    shop,
                    variantId,
                    accessToken );
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

                await SetAvailableQuantityAsync(
                    client,
                    shop,
                    inventoryItemId,
                    locationId,
                    qty,
                    accessToken );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to set inventory quantity {Quantity} for draft {ProductId}",
                    qty,
                    productId );
            }
        }

        // Select all sales channels in Publishing (draft stays draft).
        await TryPublishProductToAllChannelsAsync( shop, accessToken, normalizedProductId );

        return new CreatedShopifyProduct(
            normalizedProductId,
            normalizedVariantId,
            product.TryGetProperty( "title", out JsonElement titleEl )
                ? (titleEl.GetString() ?? normalizedTitle)
                : normalizedTitle );
    }

    /// <summary>
    /// Assign Shopify category «Print Books» (not parent «Books in Media»).
    /// </summary>
    public async Task TryAssignBookCategoryAsync(
        string shop,
        string accessToken,
        string shopifyProductId )
    {
        try
        {
            long? productId = ShopifyIds.TryParseNumericProductId( shopifyProductId );
            if (!productId.HasValue)
            {
                return;
            }

            string categoryGid = await ResolvePrintBooksCategoryGidAsync( shop, accessToken );
            await TrySetCategoryOnlyViaProductUpdateAsync(
                shop,
                accessToken,
                productId.Value,
                categoryGid );

            _logger.LogInformation(
                "Assigned category {CategoryGid} to draft product {ProductId}",
                categoryGid,
                shopifyProductId );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to assign Print Books category for draft {ProductId}",
                shopifyProductId );
        }
    }

    private async Task<string> ResolvePrintBooksCategoryGidAsync(
        string shop,
        string accessToken )
    {
        try
        {
            (bool success, JsonDocument? document, string? error) = await _graphql.TryExecuteAsync(
                shop,
                accessToken,
                TaxonomyCategoriesSearchQuery,
                new { search = "Print Books" } );
            if (success && document is not null)
            {
                using (document)
                {
                    string? matched = PickPrintBooksCategoryGid( document );
                    if (!string.IsNullOrWhiteSpace( matched ))
                    {
                        return matched;
                    }
                }
            }
            else
            {
                _logger.LogWarning(
                    "Taxonomy search for Print Books failed: {Error}",
                    error );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "Taxonomy search for Print Books threw" );
        }

        return PrintBooksTaxonomyCategoryGid;
    }

    private static string? PickPrintBooksCategoryGid( JsonDocument document )
    {
        if (!document.RootElement.TryGetProperty( "data", out JsonElement data )
            || !data.TryGetProperty( "taxonomy", out JsonElement taxonomy )
            || !taxonomy.TryGetProperty( "categories", out JsonElement categories )
            || !categories.TryGetProperty( "nodes", out JsonElement nodes )
            || nodes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? exact = null;
        string? contains = null;
        foreach (JsonElement node in nodes.EnumerateArray())
        {
            string id = node.TryGetProperty( "id", out JsonElement idEl )
                && idEl.ValueKind == JsonValueKind.String
                    ? (idEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;
            if (string.IsNullOrWhiteSpace( id ))
            {
                continue;
            }

            string name = node.TryGetProperty( "name", out JsonElement nameEl )
                && nameEl.ValueKind == JsonValueKind.String
                    ? (nameEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;
            string fullName = node.TryGetProperty( "fullName", out JsonElement fullEl )
                && fullEl.ValueKind == JsonValueKind.String
                    ? (fullEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;

            if (string.Equals( name, "Print Books", StringComparison.OrdinalIgnoreCase ))
            {
                exact = id;
                break;
            }

            if (contains is null
                && (fullName.Contains( "Print Books", StringComparison.OrdinalIgnoreCase )
                    || name.Contains( "Print Books", StringComparison.OrdinalIgnoreCase )))
            {
                contains = id;
            }
        }

        return exact ?? contains;
    }

    public async Task<string> AttachProductImageAsync(
        string shop,
        string accessToken,
        string shopifyProductId,
        byte[] imageBytes,
        string fileName,
        string? mimeType = null,
        string? alt = null,
        string? tempMediaId = null,
        CancellationToken cancellationToken = default )
    {
        if (imageBytes is null || imageBytes.Length == 0)
        {
            throw new InvalidOperationException( "Пустыя байты выявы." );
        }

        if (!LooksLikeImageBytes( imageBytes ))
        {
            throw new InvalidOperationException(
                "Файл не падобны на выяву (магчыма HTML/памылка CDN)." );
        }

        string numericId = ShopifyIds.NormalizeProductId( shopifyProductId );
        if (string.IsNullOrWhiteSpace( numericId ))
        {
            throw new InvalidOperationException( "Некарэктны Shopify product id." );
        }

        string productGid = ShopifyIds.ToProductGid( numericId );
        string safeName = string.IsNullOrWhiteSpace( fileName )
            ? GuessFileNameFromBytes( imageBytes, "cover" )
            : Path.GetFileName( fileName.Trim() );
        string resolvedMime = ResolveImageMimeType( imageBytes, mimeType );

        _logger.LogInformation(
            "shopify-image-attach start ProductId={ProductId} TempMediaId={TempMediaId} FileName={FileName} MimeType={MimeType} ByteLength={ByteLength}",
            numericId,
            tempMediaId,
            safeName,
            resolvedMime,
            imageBytes.Length );

        HashSet<string> mediaBefore = await LoadProductMediaImageIdsAsync(
            shop,
            accessToken,
            productGid,
            cancellationToken );

        StagedUploadTarget staged = await CreateStagedUploadAsync(
            shop,
            accessToken,
            safeName,
            resolvedMime,
            imageBytes.Length,
            cancellationToken );

        _logger.LogInformation(
            "shopify-image-attach stagedUploadsCreate ok ProductId={ProductId} TempMediaId={TempMediaId} FileName={FileName} HasResourceUrl={HasResourceUrl}",
            numericId,
            tempMediaId,
            safeName,
            !string.IsNullOrWhiteSpace( staged.ResourceUrl ) );

        int uploadStatus = await UploadBytesToStagedTargetAsync(
            staged,
            imageBytes,
            safeName,
            resolvedMime,
            cancellationToken );

        _logger.LogInformation(
            "shopify-image-attach binary upload ProductId={ProductId} TempMediaId={TempMediaId} HttpStatus={HttpStatus}",
            numericId,
            tempMediaId,
            uploadStatus );

        string mediaImageId = await AttachStagedMediaToProductAsync(
            shop,
            accessToken,
            productGid,
            staged.ResourceUrl,
            alt,
            mediaBefore,
            cancellationToken );

        _logger.LogInformation(
            "shopify-image-attach productUpdate ok ProductId={ProductId} TempMediaId={TempMediaId} MediaImageId={MediaImageId}",
            numericId,
            tempMediaId,
            mediaImageId );

        return mediaImageId;
    }

    private sealed record StagedUploadTarget(
        string Url,
        string ResourceUrl,
        IReadOnlyList<(string Name, string Value)> Parameters );

    private const string StagedUploadsCreateMutation = """
        mutation StagedUploadsCreate($input: [StagedUploadInput!]!) {
          stagedUploadsCreate(input: $input) {
            stagedTargets {
              url
              resourceUrl
              parameters { name value }
            }
            userErrors { field message }
          }
        }
        """;

    private const string ProductUpdateMediaMutation = """
        mutation ProductUpdateMedia($product: ProductUpdateInput!, $media: [CreateMediaInput!]) {
          productUpdate(product: $product, media: $media) {
            product {
              id
              media(first: 50) {
                nodes {
                  id
                  ... on MediaImage {
                    id
                    status
                  }
                }
              }
            }
            userErrors { field message }
          }
        }
        """;

    private const string ProductMediaIdsQuery = """
        query ProductMediaIds($id: ID!) {
          product(id: $id) {
            media(first: 50) {
              nodes {
                id
                ... on MediaImage { id }
              }
            }
          }
        }
        """;

    private async Task<StagedUploadTarget> CreateStagedUploadAsync(
        string shop,
        string accessToken,
        string fileName,
        string mimeType,
        int byteLength,
        CancellationToken cancellationToken )
    {
        var input = new[]
        {
            new Dictionary<string, object?>
            {
                ["resource"] = "PRODUCT_IMAGE",
                ["filename"] = fileName,
                ["mimeType"] = mimeType,
                ["httpMethod"] = "POST",
                ["fileSize"] = byteLength.ToString( CultureInfo.InvariantCulture ),
            },
        };

        (bool ok, JsonDocument? document, string? error) = await _graphql.TryExecuteAsync(
            shop,
            accessToken,
            StagedUploadsCreateMutation,
            new { input },
            cancellationToken );
        if (!ok || document is null)
        {
            throw new InvalidOperationException(
                $"stagedUploadsCreate failed: {error ?? "unknown GraphQL error"}" );
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty( "data", out JsonElement data )
                || !data.TryGetProperty( "stagedUploadsCreate", out JsonElement payload ))
            {
                throw new InvalidOperationException( "stagedUploadsCreate: empty data payload." );
            }

            if (TryReadUserErrors( payload, "userErrors", out string? userError ))
            {
                throw new InvalidOperationException( $"stagedUploadsCreate userErrors: {userError}" );
            }

            if (!payload.TryGetProperty( "stagedTargets", out JsonElement targets )
                || targets.ValueKind != JsonValueKind.Array
                || targets.GetArrayLength() != 1)
            {
                int count = targets.ValueKind == JsonValueKind.Array ? targets.GetArrayLength() : 0;
                throw new InvalidOperationException(
                    $"stagedUploadsCreate expected 1 stagedTarget, got {count}." );
            }

            JsonElement target = targets[0];
            string? url = target.TryGetProperty( "url", out JsonElement urlEl )
                ? urlEl.GetString()
                : null;
            string? resourceUrl = target.TryGetProperty( "resourceUrl", out JsonElement resEl )
                ? resEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace( url ) || string.IsNullOrWhiteSpace( resourceUrl ))
            {
                throw new InvalidOperationException(
                    "stagedUploadsCreate missing url or resourceUrl." );
            }

            List<(string Name, string Value)> parameters = new();
            if (target.TryGetProperty( "parameters", out JsonElement paramsEl )
                && paramsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement p in paramsEl.EnumerateArray())
                {
                    string? name = p.TryGetProperty( "name", out JsonElement n ) ? n.GetString() : null;
                    string? value = p.TryGetProperty( "value", out JsonElement v ) ? v.GetString() : null;
                    if (!string.IsNullOrWhiteSpace( name ))
                    {
                        parameters.Add( (name!, value ?? string.Empty) );
                    }
                }
            }

            if (parameters.Count == 0)
            {
                throw new InvalidOperationException( "stagedUploadsCreate returned no parameters." );
            }

            return new StagedUploadTarget( url!, resourceUrl!, parameters );
        }
    }

    private async Task<int> UploadBytesToStagedTargetAsync(
        StagedUploadTarget staged,
        byte[] imageBytes,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken )
    {
        HttpClient client = _httpClientFactory.CreateClient( "Shopify" );
        using MultipartFormDataContent form = new();

        // All stagedTarget.parameters first, names/values unchanged.
        // GCS requires Content-Disposition name="..." with quotes; .NET's Add(name)
        // emits bare name=key which GCS rejects as "Malformed multipart body".
        // StringContent also defaults to Content-Type: text/plain; charset=utf-8 -
        // clear it so parts match browser/curl form fields.
        foreach ((string name, string value) in staged.Parameters)
        {
            StringContent field = new( value );
            field.Headers.ContentType = null;
            form.Add( field, QuoteMultipartFormName( name ) );
        }

        // File part strictly last. Do not set request Content-Type manually -
        // MultipartFormDataContent must keep its generated boundary.
        ByteArrayContent fileContent = new( imageBytes );
        fileContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue( mimeType );
        form.Add( fileContent, QuoteMultipartFormName( "file" ), fileName );
        NormalizeStagedFileContentDisposition( fileContent );

        // No Shopify access token on the staged upload host.
        using HttpResponseMessage response = await client.PostAsync(
            staged.Url,
            form,
            cancellationToken );
        int status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync( cancellationToken );
            string peek = body.Length > 300 ? body[..300] + "..." : body;
            throw new InvalidOperationException(
                $"Staged binary upload HTTP {status}: {peek}" );
        }

        return status;
    }

    /// <summary>
    /// GCS staged uploads require quoted form-data names (name="key").
    /// Passing already-quoted names through MultipartFormDataContent.Add keeps them.
    /// </summary>
    private static string QuoteMultipartFormName( string name )
    {
        if (string.IsNullOrEmpty( name ))
        {
            return "\"\"";
        }

        return name[0] == '"' ? name : $"\"{name}\"";
    }

    /// <summary>
    /// Drop filename* (GCS rejects it) and ensure filename="..." is quoted.
    /// </summary>
    private static void NormalizeStagedFileContentDisposition( ByteArrayContent fileContent )
    {
        System.Net.Http.Headers.ContentDispositionHeaderValue? disposition =
            fileContent.Headers.ContentDisposition;
        if (disposition is null)
        {
            return;
        }

        disposition.FileNameStar = null;
        if (!string.IsNullOrEmpty( disposition.FileName ) && disposition.FileName[0] != '"')
        {
            disposition.FileName = $"\"{disposition.FileName.Trim( '"' )}\"";
        }
    }

    private async Task<string> AttachStagedMediaToProductAsync(
        string shop,
        string accessToken,
        string productGid,
        string resourceUrl,
        string? alt,
        HashSet<string> mediaIdsBefore,
        CancellationToken cancellationToken )
    {
        Dictionary<string, object?> product = new() { ["id"] = productGid };
        List<Dictionary<string, object?>> media =
        [
            BuildCreateMediaInput( resourceUrl, alt ),
        ];

        (bool ok, JsonDocument? document, string? error) = await _graphql.TryExecuteAsync(
            shop,
            accessToken,
            ProductUpdateMediaMutation,
            new { product, media },
            cancellationToken );
        if (!ok || document is null)
        {
            throw new InvalidOperationException(
                $"productUpdate(media) failed: {error ?? "unknown GraphQL error"}" );
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty( "data", out JsonElement data )
                || !data.TryGetProperty( "productUpdate", out JsonElement payload ))
            {
                throw new InvalidOperationException( "productUpdate(media): empty data payload." );
            }

            if (TryReadUserErrors( payload, "userErrors", out string? userError ))
            {
                _logger.LogWarning(
                    "shopify-image-attach productUpdate userErrors ProductGid={ProductGid} Error={Error}",
                    productGid,
                    userError );
                throw new InvalidOperationException( $"productUpdate userErrors: {userError}" );
            }

            string? mediaImageId = TryFindNewMediaImageId( payload, mediaIdsBefore );
            if (!string.IsNullOrWhiteSpace( mediaImageId ))
            {
                return mediaImageId;
            }
        }

        // Fallback: re-query product media if payload did not surface a new id.
        HashSet<string> mediaAfter = await LoadProductMediaImageIdsAsync(
            shop,
            accessToken,
            productGid,
            cancellationToken );
        string? created = mediaAfter.Except( mediaIdsBefore ).FirstOrDefault();
        if (string.IsNullOrWhiteSpace( created ))
        {
            throw new InvalidOperationException(
                "productUpdate succeeded but no new MediaImage id was returned." );
        }

        return created;
    }

    private async Task<HashSet<string>> LoadProductMediaImageIdsAsync(
        string shop,
        string accessToken,
        string productGid,
        CancellationToken cancellationToken )
    {
        HashSet<string> ids = new( StringComparer.Ordinal );
        (bool ok, JsonDocument? document, string? _) = await _graphql.TryExecuteAsync(
            shop,
            accessToken,
            ProductMediaIdsQuery,
            new { id = productGid },
            cancellationToken );
        if (!ok || document is null)
        {
            return ids;
        }

        using (document)
        {
            if (document.RootElement.TryGetProperty( "data", out JsonElement data )
                && data.TryGetProperty( "product", out JsonElement product )
                && product.ValueKind == JsonValueKind.Object
                && product.TryGetProperty( "media", out JsonElement media )
                && media.TryGetProperty( "nodes", out JsonElement nodes )
                && nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement node in nodes.EnumerateArray())
                {
                    if (node.TryGetProperty( "id", out JsonElement idEl ))
                    {
                        string? id = idEl.GetString();
                        if (!string.IsNullOrWhiteSpace( id ))
                        {
                            ids.Add( id );
                        }
                    }
                }
            }
        }

        return ids;
    }

    private static string? TryFindNewMediaImageId(
        JsonElement productUpdatePayload,
        HashSet<string> mediaIdsBefore )
    {
        if (!productUpdatePayload.TryGetProperty( "product", out JsonElement product )
            || product.ValueKind != JsonValueKind.Object
            || !product.TryGetProperty( "media", out JsonElement media )
            || !media.TryGetProperty( "nodes", out JsonElement nodes )
            || nodes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? lastNew = null;
        foreach (JsonElement node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty( "id", out JsonElement idEl ))
            {
                continue;
            }

            string? id = idEl.GetString();
            if (string.IsNullOrWhiteSpace( id ) || mediaIdsBefore.Contains( id ))
            {
                continue;
            }

            lastNew = id;
        }

        return lastNew;
    }

    private static Dictionary<string, object?> BuildCreateMediaInput( string resourceUrl, string? alt )
    {
        Dictionary<string, object?> media = new()
        {
            ["originalSource"] = resourceUrl,
            ["mediaContentType"] = "IMAGE",
        };
        if (!string.IsNullOrWhiteSpace( alt ))
        {
            media["alt"] = alt.Trim();
        }

        return media;
    }

    private static bool TryReadUserErrors(
        JsonElement parent,
        string propertyName,
        out string? combined )
    {
        combined = null;
        if (!parent.TryGetProperty( propertyName, out JsonElement errors )
            || errors.ValueKind != JsonValueKind.Array
            || errors.GetArrayLength() == 0)
        {
            return false;
        }

        List<string> parts = new();
        foreach (JsonElement err in errors.EnumerateArray())
        {
            string? message = err.TryGetProperty( "message", out JsonElement msg )
                ? msg.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace( message ))
            {
                parts.Add( message! );
            }
        }

        if (parts.Count == 0)
        {
            combined = errors.ToString();
            return true;
        }

        combined = string.Join( "; ", parts );
        return true;
    }

    private static string ResolveImageMimeType( byte[] bytes, string? mimeType )
    {
        if (!string.IsNullOrWhiteSpace( mimeType )
            && mimeType.StartsWith( "image/", StringComparison.OrdinalIgnoreCase )
            && !mimeType.Contains( "html", StringComparison.OrdinalIgnoreCase ))
        {
            return mimeType.Trim();
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        if (bytes.Length >= 3 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
        {
            return "image/gif";
        }

        return "image/jpeg";
    }

    public async Task AttachProductImageFromUrlAsync(
        string shop,
        string accessToken,
        string shopifyProductId,
        string imageUrl )
    {
        // Never ask Shopify to pull supplier CDN URLs directly — that yields
        // "Media processing failed" in Admin when Shopify cannot fetch/decode them.
        // Callers must download bytes first and use AttachProductImageAsync.
        _ = shop;
        _ = accessToken;
        _ = shopifyProductId;
        _ = imageUrl;
        throw new InvalidOperationException(
            "Падцягванне фота па CDN URL у Shopify адключана. Патрэбныя байты выявы." );
    }

    private static bool LooksLikeImageBytes( byte[] bytes )
    {
        if (bytes.Length < 12)
        {
            return false;
        }

        // JPEG
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return true;
        }

        // PNG
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return true;
        }

        // GIF
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
        {
            return true;
        }

        // WEBP: RIFF....WEBP
        if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return true;
        }

        return false;
    }

    private static string GuessFileNameFromBytes( byte[] bytes, string stem )
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return $"{stem}.jpg";
        }

        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return $"{stem}.png";
        }

        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return $"{stem}.webp";
        }

        return $"{stem}.jpg";
    }

    private static string BuildBookProductTags( string? barcodeDigits, string? author, string? coverType )
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

        // ISBN tag: digits only (no hyphens/spaces).
        AddTag( NormalizeIsbnTag( barcodeDigits ) );

        string? coverTag = NormalizeCoverTypeTag( coverType );
        AddTag( coverTag );

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

    /// <summary>ISBN for tags: strip dashes/spaces; prefer validated digits-only form.</summary>
    private static string? NormalizeIsbnTag( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string? validated = IsbnUtil.Normalize( raw );
        if (!string.IsNullOrWhiteSpace( validated ))
        {
            return validated;
        }

        string digits = Regex.Replace( raw.Trim(), @"[^\dXx]", string.Empty ).ToUpperInvariant();
        return string.IsNullOrWhiteSpace( digits ) ? null : digits;
    }

    private static string? NormalizeCoverTypeTag( string? coverType )
    {
        string t = (coverType ?? string.Empty).Trim().ToLowerInvariant();
        if (t is "soft" or "мяккая" or "мягкая" or "paperback" or "softcover")
        {
            return "мяккая вокладка";
        }

        if (t is "hard" or "цвёрдая" or "твердая" or "hardcover" or "hardback")
        {
            return "цвёрдая вокладка";
        }

        return null;
    }

    private async Task TrySetBookBibliographicMetafieldsAsync(
        HttpClient client,
        string shop,
        string accessToken,
        long productId,
        CreateShopifyProductInput input )
    {
        try
        {
            await TrySetBookCoverMetafieldAsync(
                client,
                shop,
                accessToken,
                productId,
                input.CoverType );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to write book.cover metafield for product {ProductId}",
                productId );
        }

        await TrySetCustomTextMetafieldBestEffortAsync(
            client, shop, accessToken, productId, "age", input.AgeRating );
        await TrySetCustomTextMetafieldBestEffortAsync(
            client, shop, accessToken, productId, "format", input.Format );
        await TrySetCustomTextMetafieldBestEffortAsync(
            client, shop, accessToken, productId, "illustrator", input.Illustrator );
        await TrySetCustomLanguageMetafieldBestEffortAsync(
            client, shop, accessToken, productId, input.Language );
        await TrySetCustomTextMetafieldBestEffortAsync(
            client, shop, accessToken, productId, "publication_place", input.PlaceOfPublication );
        await TrySetCustomTextMetafieldBestEffortAsync(
            client, shop, accessToken, productId, "translator", input.Translation );
        await TrySetCustomNumericOrTextMetafieldBestEffortAsync(
            client, shop, accessToken, productId, "pages", input.PageCount );
        await TrySetCustomNumericOrTextMetafieldBestEffortAsync(
            client, shop, accessToken, productId, "year", input.Year );
    }

    private async Task TrySetBookCoverMetafieldAsync(
        HttpClient client,
        string shop,
        string accessToken,
        long productId,
        string? coverType )
    {
        string? mapped = MapCoverTypeToMetafieldValue( coverType );
        if (string.IsNullOrWhiteSpace( mapped ))
        {
            return;
        }

        (string TypeName, IReadOnlyList<string> Options) def =
            await ResolveMetafieldDefinitionAsync( shop, accessToken, "book", "cover" );

        string valueToWrite = mapped;
        if (def.Options.Count > 0)
        {
            string[] candidates = coverType is not null
                && IsSoftCover( coverType )
                    ? ["мяккая", "Мяккая", "мягкая", "Мягкая", mapped]
                    : IsHardCover( coverType )
                        ? ["цвёрдая", "Цвёрдая", "твердая", "Твердая", mapped]
                        : [mapped];

            IReadOnlyList<string> matched = FilterGenresToAllowed( candidates, def.Options );
            if (matched.Count == 0)
            {
                _logger.LogWarning(
                    "book.cover value «{Value}» not in allowed choices for product {ProductId}",
                    mapped,
                    productId );
                return;
            }

            valueToWrite = matched[0];
        }

        string typeName = string.IsNullOrWhiteSpace( def.TypeName )
            ? "single_line_text_field"
            : def.TypeName;
        string metafieldValue = typeName.StartsWith( "list.", StringComparison.OrdinalIgnoreCase )
            ? JsonSerializer.Serialize( new[] { valueToWrite } )
            : valueToWrite;

        await UpsertProductMetafieldAsync(
            client,
            shop,
            accessToken,
            productId,
            "book",
            "cover",
            metafieldValue,
            typeName );
    }

    private static string? MapCoverTypeToMetafieldValue( string? coverType )
    {
        if (string.IsNullOrWhiteSpace( coverType ))
        {
            return null;
        }

        if (IsSoftCover( coverType ))
        {
            return "мяккая";
        }

        if (IsHardCover( coverType ))
        {
            return "цвёрдая";
        }

        string trimmed = coverType.Trim();
        return string.IsNullOrWhiteSpace( trimmed ) ? null : trimmed;
    }

    private static bool IsSoftCover( string? coverType )
    {
        string t = (coverType ?? string.Empty).Trim().ToLowerInvariant();
        return t is "soft" or "мяккая" or "мягкая" or "paperback" or "softcover"
            || t.Contains( "мякк", StringComparison.Ordinal )
            || t.Contains( "мягк", StringComparison.Ordinal )
            || t.Contains( "soft", StringComparison.Ordinal );
    }

    private static bool IsHardCover( string? coverType )
    {
        string t = (coverType ?? string.Empty).Trim().ToLowerInvariant();
        return t is "hard" or "цвёрдая" or "твердая" or "hardcover" or "hardback"
            || t.Contains( "цвёрд", StringComparison.Ordinal )
            || t.Contains( "тверд", StringComparison.Ordinal )
            || t.Contains( "цверд", StringComparison.Ordinal )
            || t.Contains( "hard", StringComparison.Ordinal );
    }

    private async Task TrySetCustomTextMetafieldBestEffortAsync(
        HttpClient client,
        string shop,
        string accessToken,
        long productId,
        string key,
        string? value )
    {
        string trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( trimmed ))
        {
            return;
        }

        try
        {
            (string TypeName, IReadOnlyList<string> Options) def =
                await ResolveMetafieldDefinitionAsync( shop, accessToken, "custom", key );
            string typeName = string.IsNullOrWhiteSpace( def.TypeName )
                ? "single_line_text_field"
                : def.TypeName;
            string valueToWrite = trimmed;
            if (def.Options.Count > 0)
            {
                IReadOnlyList<string> matched = FilterGenresToAllowed( [trimmed], def.Options );
                if (matched.Count == 0)
                {
                    _logger.LogWarning(
                        "custom.{Key} value «{Value}» not in allowed choices for product {ProductId}",
                        key,
                        trimmed,
                        productId );
                    return;
                }

                valueToWrite = matched[0];
            }

            string metafieldValue = typeName.StartsWith( "list.", StringComparison.OrdinalIgnoreCase )
                ? JsonSerializer.Serialize( new[] { valueToWrite } )
                : valueToWrite;

            await UpsertProductMetafieldAsync(
                client,
                shop,
                accessToken,
                productId,
                "custom",
                key,
                metafieldValue,
                typeName );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to write custom.{Key} metafield for product {ProductId}",
                key,
                productId );
        }
    }

    private async Task TrySetCustomLanguageMetafieldBestEffortAsync(
        HttpClient client,
        string shop,
        string accessToken,
        long productId,
        string? language )
    {
        string trimmed = (language ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( trimmed ))
        {
            return;
        }

        try
        {
            (string TypeName, IReadOnlyList<string> Options) def =
                await ResolveMetafieldDefinitionAsync( shop, accessToken, "custom", "language" );
            string typeName = string.IsNullOrWhiteSpace( def.TypeName )
                ? "single_line_text_field"
                : def.TypeName;
            string valueToWrite = trimmed;
            if (def.Options.Count > 0)
            {
                IReadOnlyList<string> matched = FilterGenresToAllowed( [trimmed], def.Options );
                if (matched.Count == 0)
                {
                    _logger.LogWarning(
                        "custom.language value «{Value}» not in allowed choices for product {ProductId}",
                        trimmed,
                        productId );
                    return;
                }

                valueToWrite = matched[0];
            }

            string metafieldValue = typeName.StartsWith( "list.", StringComparison.OrdinalIgnoreCase )
                ? JsonSerializer.Serialize( new[] { valueToWrite } )
                : valueToWrite;

            await UpsertProductMetafieldAsync(
                client,
                shop,
                accessToken,
                productId,
                "custom",
                "language",
                metafieldValue,
                typeName );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to write custom.language metafield for product {ProductId}",
                productId );
        }
    }

    private async Task TrySetCustomNumericOrTextMetafieldBestEffortAsync(
        HttpClient client,
        string shop,
        string accessToken,
        long productId,
        string key,
        int? value )
    {
        if (value is null)
        {
            return;
        }

        try
        {
            (string TypeName, IReadOnlyList<string> _) def =
                await ResolveMetafieldDefinitionAsync( shop, accessToken, "custom", key );
            string typeName = string.IsNullOrWhiteSpace( def.TypeName )
                ? "number_integer"
                : def.TypeName;

            string metafieldValue;
            string writeType;
            if (typeName.Contains( "number_integer", StringComparison.OrdinalIgnoreCase )
                || typeName.Equals( "number_integer", StringComparison.OrdinalIgnoreCase ))
            {
                writeType = typeName.StartsWith( "list.", StringComparison.OrdinalIgnoreCase )
                    ? typeName
                    : "number_integer";
                metafieldValue = writeType.StartsWith( "list.", StringComparison.OrdinalIgnoreCase )
                    ? JsonSerializer.Serialize( new[] { value.Value.ToString( CultureInfo.InvariantCulture ) } )
                    : value.Value.ToString( CultureInfo.InvariantCulture );
            }
            else if (string.IsNullOrWhiteSpace( def.TypeName ))
            {
                // Prefer number_integer when definition is unknown and we have an int.
                writeType = "number_integer";
                metafieldValue = value.Value.ToString( CultureInfo.InvariantCulture );
            }
            else
            {
                writeType = typeName;
                string asText = value.Value.ToString( CultureInfo.InvariantCulture );
                metafieldValue = writeType.StartsWith( "list.", StringComparison.OrdinalIgnoreCase )
                    ? JsonSerializer.Serialize( new[] { asText } )
                    : asText;
            }

            await UpsertProductMetafieldAsync(
                client,
                shop,
                accessToken,
                productId,
                "custom",
                key,
                metafieldValue,
                writeType );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to write custom.{Key} metafield for product {ProductId}",
                key,
                productId );
        }
    }

    private async Task<(string TypeName, IReadOnlyList<string> Options)> ResolveMetafieldDefinitionAsync(
        string shop,
        string accessToken,
        string metafieldNamespace,
        string key )
    {
        (bool success, JsonDocument? document, string? _) = await _graphql.TryExecuteAsync(
            shop,
            accessToken,
            ProductMetafieldDefinitionByNsKeyQuery,
            new { @namespace = metafieldNamespace, key } );
        if (!success || document is null)
        {
            return (string.Empty, Array.Empty<string>());
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty( "data", out JsonElement data )
                || !data.TryGetProperty( "metafieldDefinitions", out JsonElement definitions )
                || !definitions.TryGetProperty( "nodes", out JsonElement nodes )
                || nodes.ValueKind != JsonValueKind.Array)
            {
                return (string.Empty, Array.Empty<string>());
            }

            foreach (JsonElement definition in nodes.EnumerateArray())
            {
                string ns = definition.TryGetProperty( "namespace", out JsonElement nsEl )
                    && nsEl.ValueKind == JsonValueKind.String
                    ? (nsEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                string defKey = definition.TryGetProperty( "key", out JsonElement keyEl )
                    && keyEl.ValueKind == JsonValueKind.String
                    ? (keyEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                if (!string.Equals( ns, metafieldNamespace, StringComparison.OrdinalIgnoreCase )
                    || !string.Equals( defKey, key, StringComparison.OrdinalIgnoreCase ))
                {
                    continue;
                }

                string typeName = string.Empty;
                if (definition.TryGetProperty( "type", out JsonElement typeEl )
                    && typeEl.ValueKind == JsonValueKind.Object
                    && typeEl.TryGetProperty( "name", out JsonElement typeNameEl )
                    && typeNameEl.ValueKind == JsonValueKind.String)
                {
                    typeName = (typeNameEl.GetString() ?? string.Empty).Trim();
                }

                IReadOnlyList<string> options = ParseChoicesFromValidations( definition );
                return (typeName, options);
            }
        }

        return (string.Empty, Array.Empty<string>());
    }

    private async Task TryPublishProductToAllChannelsAsync(
        string shop,
        string accessToken,
        string productId )
    {
        try
        {
            string workingToken = accessToken;
            List<object>? publicationInputs = await TryResolvePublicationInputsAsync( shop, workingToken );
            if (publicationInputs is null || publicationInputs.Count == 0)
            {
                string configToken = (_config["Shopify:AccessToken"] ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace( configToken )
                    && !string.Equals( configToken, accessToken, StringComparison.Ordinal ))
                {
                    _logger.LogWarning(
                        "Publications unavailable with session token; retrying with Shopify:AccessToken for product {ProductId}",
                        productId );
                    publicationInputs = await TryResolvePublicationInputsAsync( shop, configToken );
                    if (publicationInputs is { Count: > 0 })
                    {
                        workingToken = configToken;
                    }
                }
            }

            if (publicationInputs is null || publicationInputs.Count == 0)
            {
                await LogMissingPublicationScopesAsync( shop, accessToken );
                _logger.LogWarning(
                    "Cannot publish product {ProductId} to sales channels: no publication IDs. "
                    + "In Shopify Admin → Settings → Apps → Develop apps → [your Admin API app] → "
                    + "Configuration → enable Read/Write publications (and Product listings if shown) → Save → Install app. "
                    + "For the Kirma OAuth app: update scopes, then logout and login again via Shopify.",
                    productId );
                return;
            }

            string productGid = $"gid://shopify/Product/{productId}";
            (bool publishOk, JsonDocument? publishDoc, string? publishError) =
                await _graphql.TryExecuteAsync(
                    shop,
                    workingToken,
                    PublishablePublishMutation,
                    new { id = productGid, input = publicationInputs } );
            if (!publishOk || publishDoc is null)
            {
                await LogMissingPublicationScopesAsync( shop, workingToken );
                _logger.LogWarning(
                    "publishablePublish failed for product {ProductId}: {Error}",
                    productId,
                    publishError );
                return;
            }

            using (publishDoc)
            {
                if (publishDoc.RootElement.TryGetProperty( "data", out JsonElement data )
                    && data.TryGetProperty( "publishablePublish", out JsonElement publish )
                    && publish.TryGetProperty( "userErrors", out JsonElement userErrors )
                    && userErrors.ValueKind == JsonValueKind.Array
                    && userErrors.GetArrayLength() > 0)
                {
                    _logger.LogWarning(
                        "publishablePublish userErrors for product {ProductId}: {Errors}",
                        productId,
                        userErrors.ToString() );
                }
                else
                {
                    List<string> cachedIds = new();
                    foreach (object item in publicationInputs)
                    {
                        if (item is null)
                        {
                            continue;
                        }

                        // Supports anonymous { publicationId = "..." }.
                        System.Reflection.PropertyInfo? prop = item.GetType().GetProperty( "publicationId" );
                        string? id = prop?.GetValue( item ) as string;
                        if (!string.IsNullOrWhiteSpace( id ))
                        {
                            cachedIds.Add( id );
                        }
                    }

                    if (cachedIds.Count > 0)
                    {
                        PublicationIdsCache[shop] = cachedIds
                            .Distinct( StringComparer.Ordinal )
                            .ToList();
                    }

                    _logger.LogInformation(
                        "Published product {ProductId} to {Count} sales channels",
                        productId,
                        publicationInputs.Count );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish product {ProductId} to sales channels",
                productId );
        }
    }

    private async Task<List<object>?> TryResolvePublicationInputsAsync( string shop, string accessToken )
    {
        // 1) Explicit config (comma/space-separated publication GIDs).
        List<object>? fromConfig = TryPublicationInputsFromConfig();
        if (fromConfig is { Count: > 0 })
        {
            return fromConfig;
        }

        // 2) In-memory cache from a previous successful publish on this shop.
        if (PublicationIdsCache.TryGetValue( shop, out IReadOnlyList<string>? cached )
            && cached is { Count: > 0 })
        {
            return cached.Select( id => (object)new { publicationId = id } ).ToList();
        }

        // 3) publications query (needs read_publications).
        List<object>? fromQuery = await TryLoadPublicationInputsAsync( shop, accessToken );
        if (fromQuery is { Count: > 0 })
        {
            return fromQuery;
        }

        // 4) Discover IDs from already-published products (usually only needs read_products).
        return await TryDiscoverPublicationInputsFromProductsAsync( shop, accessToken );
    }

    private List<object>? TryPublicationInputsFromConfig()
    {
        string raw = (_config["Shopify:PublicationIds"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        List<object> inputs = raw
            .Split( new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries )
            .Select( s => s.Trim() )
            .Where( s => s.StartsWith( "gid://shopify/Publication/", StringComparison.OrdinalIgnoreCase ) )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .Select( id => (object)new { publicationId = id } )
            .ToList();
        return inputs.Count > 0 ? inputs : null;
    }

    private async Task<List<object>?> TryLoadPublicationInputsAsync( string shop, string accessToken )
    {
        (bool listOk, JsonDocument? listDoc, string? listError) = await _graphql.TryExecuteAsync(
            shop,
            accessToken,
            PublicationsQuery );
        if (!listOk || listDoc is null)
        {
            _logger.LogWarning( "Failed to list publications: {Error}", listError );
            return null;
        }

        List<object> publicationInputs = new();
        using (listDoc)
        {
            if (!listDoc.RootElement.TryGetProperty( "data", out JsonElement data )
                || !data.TryGetProperty( "publications", out JsonElement publications )
                || !publications.TryGetProperty( "edges", out JsonElement edges )
                || edges.ValueKind != JsonValueKind.Array)
            {
                return publicationInputs;
            }

            foreach (JsonElement edge in edges.EnumerateArray())
            {
                if (!edge.TryGetProperty( "node", out JsonElement node )
                    || !node.TryGetProperty( "id", out JsonElement idEl )
                    || idEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? publicationId = idEl.GetString();
                if (string.IsNullOrWhiteSpace( publicationId ))
                {
                    continue;
                }

                publicationInputs.Add( new { publicationId } );
            }
        }

        return publicationInputs;
    }

    /// <summary>
    /// Collect publication GIDs from active products that are already on sales channels.
    /// Often works with read_products when the top-level publications query is denied.
    /// </summary>
    private async Task<List<object>?> TryDiscoverPublicationInputsFromProductsAsync(
        string shop,
        string accessToken )
    {
        (bool ok, JsonDocument? doc, string? error) = await _graphql.TryExecuteAsync(
            shop,
            accessToken,
            DiscoverPublicationsFromProductsQuery );
        if (!ok || doc is null)
        {
            _logger.LogWarning(
                "Failed to discover publications from products: {Error}",
                error );
            return null;
        }

        HashSet<string> ids = new( StringComparer.Ordinal );
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty( "data", out JsonElement data )
                || !data.TryGetProperty( "products", out JsonElement products )
                || !products.TryGetProperty( "edges", out JsonElement edges )
                || edges.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement edge in edges.EnumerateArray())
            {
                if (!edge.TryGetProperty( "node", out JsonElement node ))
                {
                    continue;
                }

                CollectPublicationIdsFromResourcePublications( node, "resourcePublications", ids );
                CollectPublicationIdsFromResourcePublications( node, "resourcePublicationsV2", ids );
            }
        }

        if (ids.Count == 0)
        {
            _logger.LogWarning( "No publication IDs discovered from existing products." );
            return null;
        }

        _logger.LogInformation(
            "Discovered {Count} publication IDs from existing products",
            ids.Count );
        PublicationIdsCache[shop] = ids.ToList();
        return ids.Select( id => (object)new { publicationId = id } ).ToList();
    }

    private static void CollectPublicationIdsFromResourcePublications(
        JsonElement productNode,
        string fieldName,
        HashSet<string> ids )
    {
        if (!productNode.TryGetProperty( fieldName, out JsonElement pubs ))
        {
            return;
        }

        if (pubs.TryGetProperty( "edges", out JsonElement edges ) && edges.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement edge in edges.EnumerateArray())
            {
                if (!edge.TryGetProperty( "node", out JsonElement node ))
                {
                    continue;
                }

                TryAddPublicationId( node, ids );
            }
        }

        if (pubs.TryGetProperty( "nodes", out JsonElement nodes ) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement node in nodes.EnumerateArray())
            {
                TryAddPublicationId( node, ids );
            }
        }
    }

    private static void TryAddPublicationId( JsonElement resourcePublicationNode, HashSet<string> ids )
    {
        if (!resourcePublicationNode.TryGetProperty( "publication", out JsonElement publication )
            || !publication.TryGetProperty( "id", out JsonElement idEl )
            || idEl.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string? id = idEl.GetString();
        if (!string.IsNullOrWhiteSpace( id ))
        {
            ids.Add( id );
        }
    }

    private async Task LogMissingPublicationScopesAsync( string shop, string accessToken )
    {
        try
        {
            (bool ok, JsonDocument? doc, string? error) = await _graphql.TryExecuteAsync(
                shop,
                accessToken,
                CurrentAccessScopesQuery );
            if (!ok || doc is null)
            {
                _logger.LogWarning( "Could not read current access scopes: {Error}", error );
                return;
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty( "data", out JsonElement data )
                    || !data.TryGetProperty( "currentAppInstallation", out JsonElement install )
                    || !install.TryGetProperty( "accessScopes", out JsonElement scopes )
                    || scopes.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                List<string> handles = scopes
                    .EnumerateArray()
                    .Select( s =>
                        s.TryGetProperty( "handle", out JsonElement h ) && h.ValueKind == JsonValueKind.String
                            ? (h.GetString() ?? string.Empty).Trim()
                            : string.Empty )
                    .Where( h => !string.IsNullOrWhiteSpace( h ) )
                    .OrderBy( h => h, StringComparer.Ordinal )
                    .ToList();
                bool hasRead = handles.Any( h =>
                    h.Equals( "read_publications", StringComparison.OrdinalIgnoreCase )
                    || h.Equals( "read_product_listings", StringComparison.OrdinalIgnoreCase ) );
                bool hasWrite = handles.Any( h =>
                    h.Equals( "write_publications", StringComparison.OrdinalIgnoreCase )
                    || h.Equals( "write_product_listings", StringComparison.OrdinalIgnoreCase ) );
                _logger.LogWarning(
                    "Shopify token scopes ({Count}): {Scopes}. HasReadPublications={HasRead}, HasWritePublications={HasWrite}",
                    handles.Count,
                    string.Join( ',', handles ),
                    hasRead,
                    hasWrite );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "Failed to log Shopify access scopes" );
        }
    }

    private async Task TrySetProductSeoAsync(
        string shop,
        string accessToken,
        string productId,
        string? handle,
        string? seoTitle,
        string? seoDescription )
    {
        string? trimmedHandle = (handle ?? string.Empty).Trim();
        string? trimmedTitle = (seoTitle ?? string.Empty).Trim();
        string? trimmedDescription = (seoDescription ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace( trimmedHandle )
            && string.IsNullOrWhiteSpace( trimmedTitle )
            && string.IsNullOrWhiteSpace( trimmedDescription ))
        {
            return;
        }

        try
        {
            Dictionary<string, object?> product = new()
            {
                ["id"] = $"gid://shopify/Product/{productId}",
            };
            if (!string.IsNullOrWhiteSpace( trimmedHandle ))
            {
                product["handle"] = trimmedHandle;
            }

            if (!string.IsNullOrWhiteSpace( trimmedTitle )
                || !string.IsNullOrWhiteSpace( trimmedDescription ))
            {
                Dictionary<string, object?> seo = new();
                if (!string.IsNullOrWhiteSpace( trimmedTitle ))
                {
                    seo["title"] = trimmedTitle;
                }

                if (!string.IsNullOrWhiteSpace( trimmedDescription ))
                {
                    seo["description"] = trimmedDescription;
                }

                product["seo"] = seo;
            }

            (bool ok, JsonDocument? document, string? error) = await _graphql.TryExecuteAsync(
                shop,
                accessToken,
                ProductSeoUpdateMutation,
                new { product } );
            if (!ok || document is null)
            {
                _logger.LogWarning(
                    "SEO productUpdate failed for product {ProductId}: {Error}",
                    productId,
                    error );
                return;
            }

            using (document)
            {
                if (HasProductUpdateUserErrors( document, out string? userError ))
                {
                    _logger.LogWarning(
                        "SEO productUpdate userErrors for product {ProductId}: {Error}",
                        productId,
                        userError );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "Failed to set SEO for product {ProductId}", productId );
        }
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

    private const string BookGenreMetafieldDefinitionQuery = """
        query BookGenreMetafieldDefinition {
          metafieldDefinitions(
            first: 10
            ownerType: PRODUCT
            namespace: "book"
            key: "genre"
          ) {
            nodes {
              namespace
              key
              type { name }
              validations {
                name
                value
              }
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

    private const string ShopProductVendorsQuery = """
        query ShopProductVendors($first: Int!, $after: String) {
          shop {
            productVendors(first: $first, after: $after) {
              pageInfo { hasNextPage endCursor }
              edges { node }
            }
          }
        }
        """;

    private const string ProductVendorsFromProductsQuery = """
        query ProductVendorsFromProducts($cursor: String) {
          products(first: 250, after: $cursor) {
            pageInfo { hasNextPage endCursor }
            edges {
              node { vendor }
            }
          }
        }
        """;

    private const string ProductUpdateCategoryMutation = """
        mutation ProductUpdateCategory($product: ProductUpdateInput!) {
          productUpdate(product: $product) {
            product { id category { id name } }
            userErrors { field message }
          }
        }
        """;

    private const string TaxonomyCategoriesSearchQuery = """
        query TaxonomyCategoriesSearch($search: String!) {
          taxonomy {
            categories(search: $search, first: 25) {
              nodes {
                id
                name
                fullName
              }
            }
          }
        }
        """;

    private const string ProductSeoUpdateMutation = """
        mutation ProductSeoUpdate($product: ProductUpdateInput!) {
          productUpdate(product: $product) {
            product { id handle }
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

    private const string ProductMetafieldDefinitionByNsKeyQuery = """
        query ProductMetafieldDefinitionByNsKey($namespace: String!, $key: String!) {
          metafieldDefinitions(
            first: 5
            ownerType: PRODUCT
            namespace: $namespace
            key: $key
          ) {
            nodes {
              namespace
              key
              type { name }
              validations {
                name
                value
              }
            }
          }
        }
        """;

    private const string PublicationsQuery = """
        query PublicationsList {
          publications(first: 50) {
            edges {
              node {
                id
              }
            }
          }
        }
        """;

    private const string DiscoverPublicationsFromProductsQuery = """
        query DiscoverPublicationsFromProducts {
          products(first: 15, query: "status:active") {
            edges {
              node {
                resourcePublications(first: 25) {
                  edges {
                    node {
                      publication { id }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private const string CurrentAccessScopesQuery = """
        query CurrentAccessScopes {
          currentAppInstallation {
            accessScopes {
              handle
            }
          }
        }
        """;

    private const string PublishablePublishMutation = """
        mutation PublishablePublish($id: ID!, $input: [PublicationInput!]!) {
          publishablePublish(id: $id, input: $input) {
            userErrors { field message }
          }
        }
        """;

    private sealed record BookAuthorMetafieldTarget(
        string Namespace,
        string Key,
        string TypeName,
        string? CategoryGid );

    private sealed record BookGenreMetafieldTarget(
        string Namespace,
        string Key,
        string TypeName,
        IReadOnlyList<string> Options,
        string? CategoryGid );

    private static readonly ConcurrentDictionary<string, BookAuthorMetafieldTarget?> BookAuthorTargetCache = new();
    private static readonly ConcurrentDictionary<string, BookGenreMetafieldTarget?> BookGenreTargetCache = new();
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> ProductVendorOptionsCache = new();
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> PublicationIdsCache = new();

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

    public async Task<BookGenreMetafieldOptions> GetBookGenreOptionsAsync(
        string shop,
        string accessToken )
    {
        BookGenreMetafieldTarget target = await ResolveBookGenreMetafieldTargetAsync( shop, accessToken );
        return new BookGenreMetafieldOptions(
            target.Namespace,
            target.Key,
            target.TypeName,
            target.Options,
            target.CategoryGid );
    }

    public async Task<IReadOnlyList<string>> GetProductVendorOptionsAsync(
        string shop,
        string accessToken )
    {
        if (ProductVendorOptionsCache.TryGetValue( shop, out IReadOnlyList<string>? cached )
            && cached is { Count: > 0 })
        {
            return cached;
        }

        IReadOnlyList<string> fromShop = await TryLoadVendorsViaShopProductVendorsAsync(
            shop,
            accessToken );
        IReadOnlyList<string> vendors =
            fromShop.Count > 0
                ? fromShop
                : await LoadVendorsViaProductsScanAsync( shop, accessToken );

        ProductVendorOptionsCache[shop] = vendors;
        return vendors;
    }

    /// <summary>
    /// Maps a publisher hint / page / price-list text onto an existing Shopify Vendor label.
    /// </summary>
    public static string? ResolveVendorFromSources(
        string? publisherHint,
        string? pageSnippet,
        string? priceListRow,
        IReadOnlyList<string> vendors )
    {
        if (vendors.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace( publisherHint ))
        {
            IReadOnlyList<string> fromHint = FilterGenresToAllowed(
                new[] { publisherHint.Trim() },
                vendors );
            if (fromHint.Count > 0)
            {
                return fromHint[0];
            }
        }

        string blob = string.Join(
            "\n",
            new[] { pageSnippet, priceListRow, publisherHint }
                .Where( s => !string.IsNullOrWhiteSpace( s ) ) );
        IReadOnlyList<string> fromText = MatchGenresInText( blob, vendors );
        return fromText.Count > 0 ? fromText[0] : null;
    }

    private async Task<IReadOnlyList<string>> TryLoadVendorsViaShopProductVendorsAsync(
        string shop,
        string accessToken )
    {
        HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );
        List<string> result = new();
        string? cursor = null;
        const int pageSize = 250;
        const int maxPages = 20;

        for (int page = 0; page < maxPages; page++)
        {
            (bool success, JsonDocument? document, string? _) = await _graphql.TryExecuteAsync(
                shop,
                accessToken,
                ShopProductVendorsQuery,
                new { first = pageSize, after = cursor } );
            if (!success || document is null)
            {
                return Array.Empty<string>();
            }

            using (document)
            {
                if (!document.RootElement.TryGetProperty( "data", out JsonElement data )
                    || !data.TryGetProperty( "shop", out JsonElement shopEl )
                    || !shopEl.TryGetProperty( "productVendors", out JsonElement vendorsEl ))
                {
                    return Array.Empty<string>();
                }

                if (vendorsEl.TryGetProperty( "edges", out JsonElement edges )
                    && edges.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement edge in edges.EnumerateArray())
                    {
                        if (!edge.TryGetProperty( "node", out JsonElement node )
                            || node.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        string? vendor = node.GetString()?.Trim();
                        if (string.IsNullOrWhiteSpace( vendor ) || !seen.Add( vendor ))
                        {
                            continue;
                        }

                        result.Add( vendor );
                    }
                }

                bool hasNext =
                    vendorsEl.TryGetProperty( "pageInfo", out JsonElement pageInfo )
                    && pageInfo.TryGetProperty( "hasNextPage", out JsonElement hasNextEl )
                    && hasNextEl.ValueKind == JsonValueKind.True;
                if (!hasNext)
                {
                    break;
                }

                cursor =
                    pageInfo.TryGetProperty( "endCursor", out JsonElement endCursor )
                    && endCursor.ValueKind == JsonValueKind.String
                        ? endCursor.GetString()
                        : null;
                if (string.IsNullOrWhiteSpace( cursor ))
                {
                    break;
                }
            }
        }

        result.Sort( StringComparer.OrdinalIgnoreCase );
        return result;
    }

    private async Task<IReadOnlyList<string>> LoadVendorsViaProductsScanAsync(
        string shop,
        string accessToken )
    {
        HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );
        List<string> result = new();
        string? cursor = null;
        const int maxPages = 20;

        for (int page = 0; page < maxPages; page++)
        {
            (bool success, JsonDocument? document, string? _) = await _graphql.TryExecuteAsync(
                shop,
                accessToken,
                ProductVendorsFromProductsQuery,
                new { cursor } );
            if (!success || document is null)
            {
                break;
            }

            using (document)
            {
                if (!document.RootElement.TryGetProperty( "data", out JsonElement data )
                    || !data.TryGetProperty( "products", out JsonElement products ))
                {
                    break;
                }

                if (products.TryGetProperty( "edges", out JsonElement edges )
                    && edges.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement edge in edges.EnumerateArray())
                    {
                        if (!edge.TryGetProperty( "node", out JsonElement node )
                            || !node.TryGetProperty( "vendor", out JsonElement vendorEl )
                            || vendorEl.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        string? vendor = vendorEl.GetString()?.Trim();
                        if (string.IsNullOrWhiteSpace( vendor ) || !seen.Add( vendor ))
                        {
                            continue;
                        }

                        result.Add( vendor );
                    }
                }

                bool hasNext =
                    products.TryGetProperty( "pageInfo", out JsonElement pageInfo )
                    && pageInfo.TryGetProperty( "hasNextPage", out JsonElement hasNextEl )
                    && hasNextEl.ValueKind == JsonValueKind.True;
                if (!hasNext)
                {
                    break;
                }

                cursor =
                    pageInfo.TryGetProperty( "endCursor", out JsonElement endCursor )
                    && endCursor.ValueKind == JsonValueKind.String
                        ? endCursor.GetString()
                        : null;
                if (string.IsNullOrWhiteSpace( cursor ))
                {
                    break;
                }
            }
        }

        result.Sort( StringComparer.OrdinalIgnoreCase );
        return result;
    }

    private async Task TrySetGenreMetafieldsAsync(
        HttpClient client,
        string shop,
        string accessToken,
        long productId,
        IReadOnlyList<string> genres )
    {
        BookGenreMetafieldTarget target = await ResolveBookGenreMetafieldTargetAsync(
            shop,
            accessToken );

        IReadOnlyList<string> allowed =
            target.Options.Count > 0
                ? FilterGenresToAllowed( genres, target.Options )
                : genres;
        if (allowed.Count == 0)
        {
            return;
        }

        string metafieldValue = SerializeGenreMetafieldValue( allowed, target.TypeName );

        if (await TrySetGenreViaProductUpdateAsync(
                shop,
                accessToken,
                productId,
                target,
                metafieldValue ))
        {
            return;
        }

        if (await TrySetBookGenreMetafieldGraphqlAsync(
                shop,
                accessToken,
                productId,
                target,
                metafieldValue ))
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
            metafieldValue,
            target.TypeName );

        if (!await VerifyGenreMetafieldAsync( shop, accessToken, productId, target, metafieldValue ))
        {
            throw new InvalidOperationException(
                $"Не ўдалося запісаць metafield «{target.Namespace}.{target.Key}» у Shopify." );
        }
    }

    private async Task<BookGenreMetafieldTarget> ResolveBookGenreMetafieldTargetAsync(
        string shop,
        string accessToken )
    {
        if (BookGenreTargetCache.TryGetValue( shop, out BookGenreMetafieldTarget? cached )
            && cached is not null)
        {
            return cached;
        }

        BookGenreMetafieldTarget fallback = new(
            BookGenreMetafieldNamespace,
            BookGenreMetafieldKey,
            "list.single_line_text_field",
            Array.Empty<string>(),
            null );

        (bool success, JsonDocument? document, string? _) = await _graphql.TryExecuteAsync(
            shop,
            accessToken,
            BookGenreMetafieldDefinitionQuery );
        if (!success || document is null)
        {
            BookGenreTargetCache[shop] = fallback;
            return fallback;
        }

        using (document)
        {
            BookGenreMetafieldTarget? parsed = ParseBookGenreMetafieldTarget( document );
            BookGenreMetafieldTarget resolved = parsed ?? fallback;
            BookGenreTargetCache[shop] = resolved;
            return resolved;
        }
    }

    private static BookGenreMetafieldTarget? ParseBookGenreMetafieldTarget( JsonDocument document )
    {
        if (!document.RootElement.TryGetProperty( "data", out JsonElement data ) ||
            !data.TryGetProperty( "metafieldDefinitions", out JsonElement definitions ) ||
            !definitions.TryGetProperty( "nodes", out JsonElement nodes ) ||
            nodes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

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
            if (!string.Equals( ns, BookGenreMetafieldNamespace, StringComparison.OrdinalIgnoreCase )
                || !string.Equals( key, BookGenreMetafieldKey, StringComparison.OrdinalIgnoreCase ))
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

            IReadOnlyList<string> options = ParseChoicesFromValidations( definition );
            string? categoryGid = ParseCategoryGidFromDefinitionNode( definition );
            return new BookGenreMetafieldTarget( ns, key, typeName, options, categoryGid );
        }

        return null;
    }

    private static IReadOnlyList<string> ParseChoicesFromValidations( JsonElement definition )
    {
        if (!definition.TryGetProperty( "validations", out JsonElement validations )
            || validations.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        foreach (JsonElement validation in validations.EnumerateArray())
        {
            string name = validation.TryGetProperty( "name", out JsonElement nameEl ) &&
                          nameEl.ValueKind == JsonValueKind.String
                ? (nameEl.GetString() ?? string.Empty).Trim()
                : string.Empty;
            if (!string.Equals( name, "choices", StringComparison.OrdinalIgnoreCase ))
            {
                continue;
            }

            string? raw = validation.TryGetProperty( "value", out JsonElement valueEl ) &&
                          valueEl.ValueKind == JsonValueKind.String
                ? valueEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace( raw ))
            {
                continue;
            }

            try
            {
                using JsonDocument choicesDoc = JsonDocument.Parse( raw );
                if (choicesDoc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                List<string> labels = new();
                HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );
                foreach (JsonElement item in choicesDoc.RootElement.EnumerateArray())
                {
                    string? label = item.ValueKind == JsonValueKind.String
                        ? item.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace( label ))
                    {
                        continue;
                    }

                    string trimmed = label.Trim();
                    if (seen.Add( trimmed ))
                    {
                        labels.Add( trimmed );
                    }
                }

                return labels;
            }
            catch (JsonException)
            {
                // ignore malformed choices payload
            }
        }

        return Array.Empty<string>();
    }

    private static string SerializeGenreMetafieldValue(
        IReadOnlyList<string> genres,
        string typeName )
    {
        bool isList = typeName.StartsWith( "list.", StringComparison.OrdinalIgnoreCase );
        if (isList)
        {
            return JsonSerializer.Serialize( genres.ToArray() );
        }

        return genres[0];
    }

    public static IReadOnlyList<string> NormalizeGenreLabels( IEnumerable<string>? raw )
    {
        if (raw is null)
        {
            return Array.Empty<string>();
        }

        List<string> result = new();
        HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );
        foreach (string? item in raw)
        {
            string trimmed = (item ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( trimmed ) || !seen.Add( trimmed ))
            {
                continue;
            }

            result.Add( trimmed );
        }

        return result;
    }

    public static IReadOnlyList<string> FilterGenresToAllowed(
        IEnumerable<string>? proposed,
        IReadOnlyList<string> allowed )
    {
        if (allowed.Count == 0)
        {
            return NormalizeGenreLabels( proposed );
        }

        Dictionary<string, string> byFolded = new( StringComparer.Ordinal );
        foreach (string option in allowed)
        {
            string trimmed = option.Trim();
            if (string.IsNullOrWhiteSpace( trimmed ))
            {
                continue;
            }

            byFolded.TryAdd( FoldGenreLabel( trimmed ), trimmed );
        }

        List<string> result = new();
        HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );
        foreach (string? item in proposed ?? Array.Empty<string>())
        {
            string trimmed = (item ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( trimmed ))
            {
                continue;
            }

            string? canonical = null;
            if (byFolded.TryGetValue( FoldGenreLabel( trimmed ), out string? exact ))
            {
                canonical = exact;
            }
            else
            {
                // Soft match: proposed is unique prefix/substring of one allowed label.
                string folded = FoldGenreLabel( trimmed );
                foreach ((string key, string value) in byFolded)
                {
                    if (key.Contains( folded, StringComparison.Ordinal )
                        || folded.Contains( key, StringComparison.Ordinal ))
                    {
                        if (folded.Length >= 4 || key.Length <= folded.Length + 2)
                        {
                            canonical = value;
                            break;
                        }
                    }
                }
            }

            if (canonical is null || !seen.Add( canonical ))
            {
                continue;
            }

            result.Add( canonical );
        }

        return result;
    }

    /// <summary>
    /// Finds allowed genre labels that appear explicitly in page/price/description text.
    /// </summary>
    public static IReadOnlyList<string> MatchGenresInText(
        string? text,
        IReadOnlyList<string> allowed )
    {
        if (string.IsNullOrWhiteSpace( text ) || allowed.Count == 0)
        {
            return Array.Empty<string>();
        }

        string hay = FoldGenreLabel( text );
        List<string> result = new();
        HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );
        foreach (string option in allowed.OrderByDescending( o => o.Length ))
        {
            string trimmed = option.Trim();
            if (trimmed.Length < 3)
            {
                continue;
            }

            string needle = FoldGenreLabel( trimmed );
            if (needle.Length < 3 || !hay.Contains( needle, StringComparison.Ordinal ))
            {
                continue;
            }

            if (seen.Add( trimmed ))
            {
                result.Add( trimmed );
            }
        }

        return result;
    }

    private static string FoldGenreLabel( string raw )
    {
        string t = raw.Trim().ToLowerInvariant();
        t = t.Replace( 'ё', 'е' ).Replace( 'ў', 'у' ).Replace( 'і', 'i' );
        t = Regex.Replace( t, @"\s+", " " );
        return t;
    }

    private async Task<bool> TrySetGenreViaProductUpdateAsync(
        string shop,
        string accessToken,
        long productId,
        BookGenreMetafieldTarget target,
        string metafieldValue )
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
                    ["value"] = metafieldValue,
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

        return await VerifyGenreMetafieldAsync( shop, accessToken, productId, target, metafieldValue );
    }

    private async Task<bool> TrySetBookGenreMetafieldGraphqlAsync(
        string shop,
        string accessToken,
        long productId,
        BookGenreMetafieldTarget target,
        string metafieldValue )
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
                ["value"] = metafieldValue,
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
                    && await VerifyGenreMetafieldAsync(
                        shop,
                        accessToken,
                        productId,
                        target,
                        metafieldValue ))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<bool> VerifyGenreMetafieldAsync(
        string shop,
        string accessToken,
        long productId,
        BookGenreMetafieldTarget target,
        string expectedValue )
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
                JsonNode? expectedNode = JsonNode.Parse( expectedValue );
                JsonNode? actualNode = JsonNode.Parse( actual );
                return JsonNode.DeepEquals( expectedNode, actualNode );
            }
            catch (JsonException)
            {
                return string.Equals(
                    actual.Trim(),
                    expectedValue.Trim(),
                    StringComparison.Ordinal );
            }
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
