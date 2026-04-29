using System.Text.Json;
using System.Globalization;
using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services
{
    public class ProductService
    {
        private readonly AppDbContext _db;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public ProductService( AppDbContext db, IHttpContextAccessor httpContextAccessor )
        {
            _db = db;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task<List<ProductWithSuppliersListItem>> GetProductsWithSuppliersAsync()
        {
            string? shop = _httpContextAccessor.HttpContext?.User.FindFirst( "shop" )?.Value;
            string? accessToken = _httpContextAccessor.HttpContext?.User.FindFirst( "access_token" )?.Value;

            if (string.IsNullOrWhiteSpace( shop ) || string.IsNullOrWhiteSpace( accessToken ))
            {
                throw new InvalidOperationException( "Няма Shopify-кантэксту для загрузкі прадуктаў." );
            }

            List<SupplyProduct> supplyProducts = await _db.SupplyProducts
                .AsNoTracking()
                .Include( sp => sp.Supply )
                .ThenInclude( s => s.Supplier )
                .ToListAsync();

            Dictionary<string, HashSet<string>> suppliersByProductId = supplyProducts
                .GroupBy( sp => NormalizeFromShopifyGid( sp.ShopifyProductId ) )
                .ToDictionary(
                    g => g.Key,
                    g => g.Select( sp => sp.Supply.Supplier.Name )
                        .Where( n => !string.IsNullOrWhiteSpace( n ) )
                        .ToHashSet( StringComparer.OrdinalIgnoreCase ),
                    StringComparer.OrdinalIgnoreCase
                );

            Dictionary<string, List<ProductSupplierPriceItem>> supplierPricesByProductId = supplyProducts
                .GroupBy( sp => NormalizeFromShopifyGid( sp.ShopifyProductId ) )
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .GroupBy(
                            sp => new
                            {
                                SupplierId = sp.Supply.SupplierId,
                                SupplierName = sp.Supply.Supplier.Name
                            }
                        )
                        .Select( supplierGroup =>
                            supplierGroup
                                .OrderByDescending( sp => sp.Supply.Date )
                                .ThenByDescending( sp => sp.Supply.Id )
                                .Select( sp => new ProductSupplierPriceItem
                                {
                                    SupplierId = sp.Supply.SupplierId,
                                    SupplierName = sp.Supply.Supplier.Name,
                                    SupplierPrice = sp.SupplierPrice,
                                    SalePrice = sp.SalePrice
                                } )
                                .First()
                        )
                        .OrderBy( x => x.SupplierName, StringComparer.OrdinalIgnoreCase )
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase
                );

            Dictionary<string, string> lastSyncedSupplierByProductId = supplyProducts
                .Where( sp => sp.SyncWithShopify )
                .GroupBy( sp => NormalizeFromShopifyGid( sp.ShopifyProductId ) )
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .OrderByDescending( sp => sp.Supply.Date )
                        .ThenByDescending( sp => sp.Supply.Id )
                        .Select( sp => sp.Supply.Supplier.Name )
                        .FirstOrDefault() ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase
                );

            Dictionary<string, List<ProductUnsyncedSupplierItem>> unsyncedSuppliersByProductId = supplyProducts
                .Where( sp => !sp.SyncWithShopify )
                .GroupBy( sp => NormalizeFromShopifyGid( sp.ShopifyProductId ) )
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .GroupBy(
                            sp => new
                            {
                                SupplierId = sp.Supply.SupplierId,
                                SupplierName = sp.Supply.Supplier.Name
                            }
                        )
                        .Select( sg => new ProductUnsyncedSupplierItem
                        {
                            SupplierId = sg.Key.SupplierId,
                            SupplierName = sg.Key.SupplierName,
                            Quantity = sg.Sum( x => x.Quantity )
                        } )
                        .OrderBy( x => x.SupplierName, StringComparer.OrdinalIgnoreCase )
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase
                );

            Dictionary<string, int> unsyncedQuantityByProductId = await _db.SupplyProducts
                .AsNoTracking()
                .Where( sp => !sp.SyncWithShopify )
                .GroupBy( sp => sp.ShopifyProductId )
                .ToDictionaryAsync(
                    g => NormalizeFromShopifyGid( g.Key ),
                    g => g.Sum( sp => sp.Quantity )
                );

            return await FetchShopifyProductsAsync(
                shop,
                accessToken,
                suppliersByProductId,
                supplierPricesByProductId,
                lastSyncedSupplierByProductId,
                unsyncedSuppliersByProductId,
                unsyncedQuantityByProductId
            );
        }

        public async Task<ProductSyncResult> SyncUnsyncedSupplierRowAsync( string shopifyProductId, int supplierId )
        {
            if (string.IsNullOrWhiteSpace( shopifyProductId ))
            {
                throw new InvalidOperationException( "Не зададзены Shopify ID прадукту." );
            }
            if (supplierId <= 0)
            {
                throw new InvalidOperationException( "Не зададзены пастаўшчык." );
            }

            string? shop = _httpContextAccessor.HttpContext?.User.FindFirst( "shop" )?.Value;
            string? accessToken = _httpContextAccessor.HttpContext?.User.FindFirst( "access_token" )?.Value;
            if (string.IsNullOrWhiteSpace( shop ) || string.IsNullOrWhiteSpace( accessToken ))
            {
                throw new InvalidOperationException( "Няма Shopify-кантэксту для сінхранізацыі." );
            }

            string normalizedId = NormalizeFromShopifyGid( shopifyProductId.Trim() );

            List<SupplyProduct> candidateRows = await _db.SupplyProducts
                .Include( sp => sp.Supply )
                .Where( sp => !sp.SyncWithShopify && sp.Supply.SupplierId == supplierId )
                .ToListAsync();

            List<SupplyProduct> rowsToSync = candidateRows
                .Where( sp => NormalizeFromShopifyGid( sp.ShopifyProductId ) == normalizedId )
                .ToList();

            if (rowsToSync.Count == 0)
            {
                throw new InvalidOperationException( "Не знойдзены несінхранізаваныя радкі для гэтага прадукту і пастаўшчыка." );
            }

            int delta = rowsToSync.Sum( r => r.Quantity );
            if (delta <= 0)
            {
                throw new InvalidOperationException( "Няма колькасці для сінхранізацыі." );
            }

            decimal salePriceToSync = rowsToSync
                .OrderByDescending( r => r.Supply.Date )
                .ThenByDescending( r => r.Supply.Id )
                .Select( r => r.SalePrice )
                .FirstOrDefault();

            if (salePriceToSync < 0)
            {
                throw new InvalidOperationException( "Цана продажу не можа быць адмоўнай." );
            }

            (int previous, int next) = await ApplySingleInventoryDeltaToShopifyAsync( shop, accessToken, normalizedId, delta );
            // Update Shopify price only when sale price is explicitly set (> 0).
            if (salePriceToSync > 0)
            {
                await UpdateProductPriceInShopifyAsync( shop, accessToken, normalizedId, salePriceToSync );
            }

            foreach (SupplyProduct row in rowsToSync)
            {
                row.SyncWithShopify = true;
            }
            await _db.SaveChangesAsync();

            return new ProductSyncResult
            {
                ShopifyProductId = normalizedId,
                SupplierId = supplierId,
                SyncedQuantity = delta,
                PreviousAvailable = previous,
                NewAvailable = next
            };
        }

        private static string NormalizeFromShopifyGid( string id )
        {
            const string prefix = "gid://shopify/Product/";
            return id.StartsWith( prefix, StringComparison.OrdinalIgnoreCase )
                ? id[prefix.Length..]
                : id;
        }

        private async Task<List<ProductWithSuppliersListItem>> FetchShopifyProductsAsync(
            string shop,
            string accessToken,
            Dictionary<string, HashSet<string>> suppliersByProductId,
            Dictionary<string, List<ProductSupplierPriceItem>> supplierPricesByProductId,
            Dictionary<string, string> lastSyncedSupplierByProductId,
            Dictionary<string, List<ProductUnsyncedSupplierItem>> unsyncedSuppliersByProductId,
            Dictionary<string, int> unsyncedQuantityByProductId
        )
        {
            List<ProductWithSuppliersListItem> result = new();
            using HttpClient client = new();

            string? afterCursor = null;
            bool hasNextPage;

            do
            {
                const string query = """
                query ProductsPage($after: String) {
                  products(first: 250, after: $after) {
                    edges {
                      cursor
                      node {
                        id
                        legacyResourceId
                        title
                        productType
                        totalInventory
                        featuredImage {
                          url
                        }
                      }
                    }
                    pageInfo {
                      hasNextPage
                      endCursor
                    }
                  }
                }
                """;

                string payload = JsonSerializer.Serialize( new
                {
                    query,
                    variables = new { after = afterCursor }
                } );

                using HttpRequestMessage request = new(
                    HttpMethod.Post,
                    $"https://{shop}/admin/api/2024-10/graphql.json"
                );
                request.Headers.Add( "X-Shopify-Access-Token", accessToken );
                request.Content = new StringContent( payload, System.Text.Encoding.UTF8, "application/json" );

                using HttpResponseMessage response = await client.SendAsync( request );
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException( "Не ўдалося загрузіць прадукты з Shopify." );
                }

                using JsonDocument json = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
                JsonElement root = json.RootElement;
                JsonElement products = root.GetProperty( "data" ).GetProperty( "products" );
                JsonElement edges = products.GetProperty( "edges" );

                foreach (JsonElement edge in edges.EnumerateArray())
                {
                    JsonElement node = edge.GetProperty( "node" );
                    string productName = node.GetProperty( "title" ).GetString() ?? "—";
                    string productType = node.TryGetProperty( "productType", out JsonElement productTypeEl ) &&
                                         productTypeEl.ValueKind == JsonValueKind.String
                        ? (productTypeEl.GetString() ?? string.Empty)
                        : string.Empty;
                    string? mainImageUrl = null;
                    int quantityInStock = 0;
                    string productId = "";
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

                    if (node.TryGetProperty( "legacyResourceId", out JsonElement legacyIdEl ) &&
                        legacyIdEl.ValueKind == JsonValueKind.Number &&
                        legacyIdEl.TryGetInt64( out long legacyId ))
                    {
                        productId = legacyId.ToString();
                    }
                    else if (node.TryGetProperty( "id", out JsonElement gidEl ) &&
                             gidEl.ValueKind == JsonValueKind.String)
                    {
                        string gid = gidEl.GetString() ?? "";
                        productId = NormalizeFromShopifyGid( gid );
                    }

                    if (string.IsNullOrWhiteSpace( productId ))
                    {
                        continue;
                    }

                    suppliersByProductId.TryGetValue( productId, out HashSet<string>? suppliersSet );
                    List<string> suppliers = (suppliersSet ?? [])
                        .OrderBy( n => n, StringComparer.OrdinalIgnoreCase )
                        .ToList();
                    supplierPricesByProductId.TryGetValue( productId, out List<ProductSupplierPriceItem>? supplierPrices );
                    lastSyncedSupplierByProductId.TryGetValue( productId, out string? lastSyncedSupplierName );
                    unsyncedSuppliersByProductId.TryGetValue( productId, out List<ProductUnsyncedSupplierItem>? unsyncedSuppliers );
                    bool hasSupplyQuantityOverride = unsyncedQuantityByProductId.TryGetValue( productId, out int overrideQuantity );
                    int effectiveQuantity = hasSupplyQuantityOverride ? overrideQuantity : quantityInStock;

                    result.Add( new ProductWithSuppliersListItem
                    {
                        ShopifyProductId = productId,
                        ProductName = productName,
                        ProductType = productType,
                        ProductAdminUrl = $"https://admin.shopify.com/store/{shop.Replace( ".myshopify.com", "", StringComparison.OrdinalIgnoreCase )}/products/{productId}",
                        MainImageUrl = string.IsNullOrWhiteSpace( mainImageUrl ) ? null : mainImageUrl,
                        QuantityInStock = effectiveQuantity,
                        ShopifyQuantityInStock = quantityInStock,
                        HasSupplyQuantityOverride = hasSupplyQuantityOverride,
                        LastSyncedSupplierName = lastSyncedSupplierName ?? string.Empty,
                        Suppliers = suppliers,
                        UnsyncedSuppliers = unsyncedSuppliers ?? new List<ProductUnsyncedSupplierItem>(),
                        SupplierPrices = supplierPrices ?? new List<ProductSupplierPriceItem>()
                    } );
                }

                JsonElement pageInfo = products.GetProperty( "pageInfo" );
                hasNextPage = pageInfo.GetProperty( "hasNextPage" ).GetBoolean();
                afterCursor = pageInfo.GetProperty( "endCursor" ).GetString();
            } while (hasNextPage && !string.IsNullOrWhiteSpace( afterCursor ));

            return result
                .OrderBy( p => p.ProductName, StringComparer.OrdinalIgnoreCase )
                .ToList();
        }

        private static long? ParseShopifyNumericProductId( string raw )
        {
            if (long.TryParse( raw, out long direct )) return direct;
            const string prefix = "gid://shopify/Product/";
            if (raw.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ))
            {
                string part = raw[prefix.Length..];
                return long.TryParse( part, out long gidId ) ? gidId : null;
            }
            return null;
        }

        private static async Task<string> ReadContentAsync( HttpResponseMessage response )
        {
            return await response.Content.ReadAsStringAsync();
        }

        private async Task<(int previous, int next)> ApplySingleInventoryDeltaToShopifyAsync(
            string shop,
            string accessToken,
            string shopifyProductId,
            int delta
        )
        {
            using HttpClient client = new();
            client.DefaultRequestHeaders.Add( "X-Shopify-Access-Token", accessToken );

            long locationId = await GetDefaultLocationIdAsync( client, shop );
            long? productId = ParseShopifyNumericProductId( shopifyProductId );
            if (!productId.HasValue)
            {
                throw new InvalidOperationException( "Некарэктны Shopify ID прадукту." );
            }

            long inventoryItemId = await GetInventoryItemIdByProductAsync( client, shop, productId.Value );
            int current = await GetCurrentInventoryAsync( client, shop, inventoryItemId, locationId );
            int next = Math.Max( 0, current + delta );
            await SetInventoryAsync( client, shop, inventoryItemId, locationId, next );

            return (current, next);
        }

        private async Task UpdateProductPriceInShopifyAsync(
            string shop,
            string accessToken,
            string shopifyProductId,
            decimal salePrice
        )
        {
            using HttpClient client = new();
            client.DefaultRequestHeaders.Add( "X-Shopify-Access-Token", accessToken );

            long? productId = ParseShopifyNumericProductId( shopifyProductId );
            if (!productId.HasValue)
            {
                throw new InvalidOperationException( "Некарэктны Shopify ID прадукту." );
            }

            long variantId = await GetPrimaryVariantIdByProductAsync( client, shop, productId.Value );
            string priceString = salePrice.ToString( "0.00", CultureInfo.InvariantCulture );
            string payload = JsonSerializer.Serialize( new
            {
                variant = new
                {
                    id = variantId,
                    price = priceString
                }
            } );

            using HttpContent content = new StringContent( payload, System.Text.Encoding.UTF8, "application/json" );
            using HttpResponseMessage response = await client.PutAsync(
                $"https://{shop}/admin/api/2024-10/variants/{variantId}.json",
                content
            );
            if (!response.IsSuccessStatusCode)
            {
                string body = await ReadContentAsync( response );
                throw new InvalidOperationException( $"Не ўдалося абнавіць цану ў Shopify: {body}" );
            }
        }

        private async Task<long> GetDefaultLocationIdAsync( HttpClient client, string shop )
        {
            using HttpResponseMessage response = await client.GetAsync(
                $"https://{shop}/admin/api/2024-10/locations.json?limit=1"
            );
            if (!response.IsSuccessStatusCode)
            {
                string body = await ReadContentAsync( response );
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

        private async Task<long> GetInventoryItemIdByProductAsync( HttpClient client, string shop, long productId )
        {
            using HttpResponseMessage response = await client.GetAsync(
                $"https://{shop}/admin/api/2024-10/products/{productId}.json"
            );
            if (!response.IsSuccessStatusCode)
            {
                string body = await ReadContentAsync( response );
                throw new InvalidOperationException( $"Не ўдалося атрымаць прадукт {productId} з Shopify: {body}" );
            }

            using JsonDocument json = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
            JsonElement product = json.RootElement.GetProperty( "product" );
            JsonElement variants = product.GetProperty( "variants" );
            if (variants.GetArrayLength() == 0)
            {
                throw new InvalidOperationException( $"Для прадукту {productId} няма варыянтаў." );
            }
            return variants[0].GetProperty( "inventory_item_id" ).GetInt64();
        }

        private async Task<long> GetPrimaryVariantIdByProductAsync( HttpClient client, string shop, long productId )
        {
            using HttpResponseMessage response = await client.GetAsync(
                $"https://{shop}/admin/api/2024-10/products/{productId}.json"
            );
            if (!response.IsSuccessStatusCode)
            {
                string body = await ReadContentAsync( response );
                throw new InvalidOperationException( $"Не ўдалося атрымаць прадукт {productId} з Shopify: {body}" );
            }

            using JsonDocument json = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
            JsonElement product = json.RootElement.GetProperty( "product" );
            JsonElement variants = product.GetProperty( "variants" );
            if (variants.GetArrayLength() == 0)
            {
                throw new InvalidOperationException( $"Для прадукту {productId} няма варыянтаў." );
            }
            return variants[0].GetProperty( "id" ).GetInt64();
        }

        private async Task<int> GetCurrentInventoryAsync(
            HttpClient client,
            string shop,
            long inventoryItemId,
            long locationId
        )
        {
            using HttpResponseMessage response = await client.GetAsync(
                $"https://{shop}/admin/api/2024-10/inventory_levels.json?inventory_item_ids={inventoryItemId}&location_ids={locationId}"
            );
            if (!response.IsSuccessStatusCode)
            {
                string body = await ReadContentAsync( response );
                throw new InvalidOperationException( $"Не ўдалося атрымаць inventory level: {body}" );
            }

            using JsonDocument json = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
            JsonElement levels = json.RootElement.GetProperty( "inventory_levels" );
            if (levels.GetArrayLength() == 0) return 0;
            JsonElement availableEl = levels[0].GetProperty( "available" );
            return availableEl.ValueKind == JsonValueKind.Number ? availableEl.GetInt32() : 0;
        }

        private async Task SetInventoryAsync(
            HttpClient client,
            string shop,
            long inventoryItemId,
            long locationId,
            int available
        )
        {
            string payload = JsonSerializer.Serialize( new
            {
                location_id = locationId,
                inventory_item_id = inventoryItemId,
                available
            } );

            using HttpContent content = new StringContent( payload, System.Text.Encoding.UTF8, "application/json" );
            using HttpResponseMessage response = await client.PostAsync(
                $"https://{shop}/admin/api/2024-10/inventory_levels/set.json",
                content
            );
            if (!response.IsSuccessStatusCode)
            {
                string body = await ReadContentAsync( response );
                throw new InvalidOperationException( $"Не ўдалося ўсталяваць inventory level: {body}" );
            }
        }
    }
}
