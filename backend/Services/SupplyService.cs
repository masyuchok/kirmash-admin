using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace backend.Services
{
    public class SupplyService
    {
        private readonly AppDbContext _db;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public SupplyService( AppDbContext db, IHttpContextAccessor httpContextAccessor )
        {
            _db = db;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task<List<Supply>> GetAllAsync()
        {
            return await _db.Supplies
                .OrderBy(s => s.Date)
                .ToListAsync();
        }

        public async Task<List<SupplyListItem>> GetSupplyList()
        {
            return await _db.Supplies
                .Select(s => new SupplyListItem
                {
                    Id = s.Id,
                    SupplierId = s.SupplierId,
                    SupplierName = s.Supplier.Name,
                    Date = s.Date,
                    ProductNumber = s.SupplyProducts.Count
                })
                .OrderBy(s => s.Date)
                .ToListAsync();
        }

        public async Task<SupplyDetailsResponse?> GetSupplyDetailsAsync( int id )
        {
            return await _db.Supplies
                .AsNoTracking()
                .Where( s => s.Id == id )
                .Select( s => new SupplyDetailsResponse
                {
                    Id = s.Id,
                    SupplierId = s.SupplierId,
                    SupplierName = s.Supplier.Name,
                    Date = s.Date,
                    Products = s.SupplyProducts
                        .Select( p => new SupplyDetailsProductItem
                        {
                            ShopifyProductId = p.ShopifyProductId,
                            Quantity = p.Quantity,
                            SupplierPrice = p.SupplierPrice,
                            MarginPercent = p.MarginPercent,
                            SalePrice = p.SalePrice,
                            SyncWithShopify = p.SyncWithShopify
                        } )
                        .ToList()
                } )
                .FirstOrDefaultAsync();
        }

        public async Task<SupplySaveResult> SaveSupplyAsync( SupplySaveRequest request )
        {
            string? shop = _httpContextAccessor.HttpContext?.User.FindFirst( "shop" )?.Value;
            string? accessToken = _httpContextAccessor.HttpContext?.User.FindFirst( "access_token" )?.Value;
            string? syncWarning = null;

            if (request.SupplierId <= 0)
            {
                throw new InvalidOperationException( "Неабходна выбраць пастаўшчыка." );
            }

            List<SupplyProductSaveItem> requestProducts = request.Products ?? new List<SupplyProductSaveItem>();

            foreach (SupplyProductSaveItem item in requestProducts)
            {
                if (string.IsNullOrWhiteSpace( item.ShopifyProductId ))
                {
                    throw new InvalidOperationException( "Тавар без Shopify ID не можа быць захаваны." );
                }
                if (item.Quantity <= 0)
                {
                    throw new InvalidOperationException( "Колькасць павінна быць больш за 0." );
                }
                if (item.SupplierPrice < 0 || item.MarginPercent < 0 || item.SalePrice < 0)
                {
                    throw new InvalidOperationException( "Цэны і працэнт не могуць быць адмоўнымі." );
                }
            }

            bool supplierExists = await _db.Suppliers.AnyAsync( s => s.Id == request.SupplierId );
            if (!supplierExists)
            {
                throw new InvalidOperationException( "Пастаўшчык не знойдзены." );
            }

            await using var tx = await _db.Database.BeginTransactionAsync();

            Supply supply;
            Dictionary<string, int> previousQuantities = new( StringComparer.OrdinalIgnoreCase );
            if (request.SupplyId.HasValue && request.SupplyId.Value > 0)
            {
                supply = await _db.Supplies
                    .Include( s => s.SupplyProducts )
                    .FirstOrDefaultAsync( s => s.Id == request.SupplyId.Value )
                    ?? throw new InvalidOperationException( "Пастаўка не знойдзена." );

                previousQuantities = supply.SupplyProducts
                    .Where( p => p.SyncWithShopify )
                    .GroupBy( p => p.ShopifyProductId.Trim(), StringComparer.OrdinalIgnoreCase )
                    .ToDictionary( g => g.Key, g => g.Sum( p => p.Quantity ), StringComparer.OrdinalIgnoreCase );

                supply.SupplierId = request.SupplierId;
                supply.Date = request.Date;
                supply.SupplyProducts.Clear();
            }
            else
            {
                supply = new Supply
                {
                    SupplierId = request.SupplierId,
                    Date = request.Date,
                };
                _db.Supplies.Add( supply );
            }

            Dictionary<string, int> newQuantities = requestProducts
                .Where( p => p.SyncWithShopify )
                .GroupBy( p => p.ShopifyProductId.Trim(), StringComparer.OrdinalIgnoreCase )
                .ToDictionary( g => g.Key, g => g.Sum( p => p.Quantity ), StringComparer.OrdinalIgnoreCase );

            Dictionary<string, int> deltas = new( StringComparer.OrdinalIgnoreCase );
            foreach (string key in previousQuantities.Keys.Union( newQuantities.Keys, StringComparer.OrdinalIgnoreCase ))
            {
                int oldQty = previousQuantities.TryGetValue( key, out int o ) ? o : 0;
                int newQty = newQuantities.TryGetValue( key, out int n ) ? n : 0;
                int delta = newQty - oldQty;
                if (delta != 0)
                {
                    deltas[key] = delta;
                }
            }

            List<SupplyInventoryUpdateResult> updates = new();
            if (!string.IsNullOrWhiteSpace( shop ) && !string.IsNullOrWhiteSpace( accessToken ))
            {
                try
                {
                    updates = await ApplyInventoryToShopifyAsync( shop, accessToken, deltas );
                }
                catch (Exception ex)
                {
                    syncWarning = ex.Message;
                }
            }
            else
            {
                syncWarning = "Няма Shopify-кантэксту для абнаўлення астаткаў.";
            }

            foreach (SupplyProductSaveItem item in requestProducts)
            {
                if (string.IsNullOrWhiteSpace( item.ShopifyProductId ))
                {
                    continue;
                }

                supply.SupplyProducts.Add( new SupplyProduct
                {
                    ShopifyProductId = item.ShopifyProductId.Trim(),
                    Quantity = item.Quantity,
                    SupplierPrice = item.SupplierPrice,
                    MarginPercent = item.MarginPercent,
                    SalePrice = item.SalePrice,
                    SyncWithShopify = item.SyncWithShopify
                } );
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return new SupplySaveResult
            {
                SupplyId = supply.Id,
                Warning = syncWarning,
                InventoryUpdates = updates
            };
        }

        private static long? ParseShopifyNumericProductId( string raw )
        {
            if (long.TryParse( raw, out long direct )) return direct;
            const string prefix = "gid://shopify/Product/";
            if (raw.StartsWith( prefix, StringComparison.OrdinalIgnoreCase))
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

        private async Task<List<SupplyInventoryUpdateResult>> ApplyInventoryToShopifyAsync(
            string shop,
            string accessToken,
            Dictionary<string, int> deltas
        )
        {
            List<SupplyInventoryUpdateResult> result = new();
            using HttpClient client = new();
            client.DefaultRequestHeaders.Add( "X-Shopify-Access-Token", accessToken );

            long locationId = await GetDefaultLocationIdAsync( client, shop );

            foreach (KeyValuePair<string, int> item in deltas)
            {
                long? productId = ParseShopifyNumericProductId( item.Key.Trim() );
                if (!productId.HasValue) continue;

                long inventoryItemId = await GetInventoryItemIdByProductAsync( client, shop, productId.Value );
                int current = await GetCurrentInventoryAsync( client, shop, inventoryItemId, locationId );
                int next = Math.Max( 0, current + item.Value );
                await SetInventoryAsync( client, shop, inventoryItemId, locationId, next );
                result.Add( new SupplyInventoryUpdateResult
                {
                    ShopifyProductId = item.Key.Trim(),
                    PreviousAvailable = current,
                    AddedQuantity = item.Value,
                    NewAvailable = next
                } );
            }

            return result;
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
