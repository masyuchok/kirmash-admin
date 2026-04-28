using System.Text.Json;
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

            Dictionary<string, HashSet<string>> suppliersByProductId = await _db.SupplyProducts
                .AsNoTracking()
                .Include( sp => sp.Supply )
                .ThenInclude( s => s.Supplier )
                .GroupBy( sp => sp.ShopifyProductId )
                .ToDictionaryAsync(
                    g => NormalizeFromShopifyGid( g.Key ),
                    g => g.Select( sp => sp.Supply.Supplier.Name )
                          .Where( n => !string.IsNullOrWhiteSpace( n ) )
                          .ToHashSet( StringComparer.OrdinalIgnoreCase )
                );

            return await FetchShopifyProductsAsync( shop, accessToken, suppliersByProductId );
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
            Dictionary<string, HashSet<string>> suppliersByProductId
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

                    result.Add( new ProductWithSuppliersListItem
                    {
                        ShopifyProductId = productId,
                        ProductName = productName,
                        ProductType = productType,
                        ProductAdminUrl = $"https://admin.shopify.com/store/{shop.Replace( ".myshopify.com", "", StringComparison.OrdinalIgnoreCase )}/products/{productId}",
                        MainImageUrl = string.IsNullOrWhiteSpace( mainImageUrl ) ? null : mainImageUrl,
                        QuantityInStock = quantityInStock,
                        Suppliers = suppliers
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
    }
}
