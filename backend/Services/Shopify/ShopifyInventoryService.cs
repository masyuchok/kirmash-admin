using System.Globalization;
using System.Text;
using System.Text.Json;
using backend.Models;

namespace backend.Services.Shopify;

public class ShopifyInventoryService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ShopifyInventoryService( IHttpClientFactory httpClientFactory )
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<(int Previous, int Next)> ApplyInventoryDeltaByProductKeyAsync(
        string shop,
        string accessToken,
        string shopifyProductId,
        int delta )
    {
        long? productId = ShopifyIds.TryParseNumericProductId( shopifyProductId );
        if (!productId.HasValue)
        {
            throw new InvalidOperationException( "Некарэктны Shopify ID прадукту." );
        }

        HttpClient client = _httpClientFactory.CreateClient( "Shopify" );
        long locationId = await GetDefaultLocationIdAsync( client, shop, accessToken );
        long inventoryItemId = await GetInventoryItemIdByProductAsync( client, shop, productId.Value, accessToken );
        int current = await GetAvailableQuantityAsync( client, shop, inventoryItemId, locationId, accessToken );
        int next = Math.Max( 0, current + delta );
        await SetAvailableQuantityAsync( client, shop, inventoryItemId, locationId, next, accessToken );
        return (current, next);
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

    public async Task<List<SupplyInventoryUpdateResult>> ApplySupplySyncAsync(
        string shop,
        string accessToken,
        Dictionary<string, int> deltas,
        Dictionary<string, decimal> syncedSalePrices )
    {
        List<SupplyInventoryUpdateResult> result = new();
        HttpClient client = _httpClientFactory.CreateClient( "Shopify" );
        long locationId = await GetDefaultLocationIdAsync( client, shop, accessToken );
        HashSet<string> allKeys = new(
            deltas.Keys.Union( syncedSalePrices.Keys, StringComparer.OrdinalIgnoreCase ),
            StringComparer.OrdinalIgnoreCase
        );

        foreach (string key in allKeys)
        {
            long? productId = ShopifyIds.TryParseNumericProductId( key.Trim() );
            if (!productId.HasValue) continue;

            int delta = deltas.TryGetValue( key, out int d ) ? d : 0;
            decimal salePrice = syncedSalePrices.TryGetValue( key, out decimal p ) ? p : 0;

            if (delta != 0)
            {
                long inventoryItemId = await GetInventoryItemIdByProductAsync( client, shop, productId.Value, accessToken );
                int current = await GetAvailableQuantityAsync( client, shop, inventoryItemId, locationId, accessToken );
                int next = Math.Max( 0, current + delta );
                await SetAvailableQuantityAsync( client, shop, inventoryItemId, locationId, next, accessToken );
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
                long variantId = await GetPrimaryVariantIdByProductAsync( client, shop, productId.Value, accessToken );
                await SetVariantPriceAsync( client, shop, variantId, salePrice, accessToken );
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

    private static async Task<long> GetDefaultLocationIdAsync( HttpClient client, string shop, string accessToken )
    {
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Get,
            ShopifyApi.RestUrl( shop, "locations.json?limit=1" )
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

        return locations[0].GetProperty( "id" ).GetInt64();
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
}
