using backend.Data;
using backend.Models;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public class SupplyService
{
    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ShopifyInventoryService _shopifyInventory;

    public SupplyService(
        AppDbContext db,
        IHttpContextAccessor httpContextAccessor,
        ShopifyInventoryService shopifyInventory )
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _shopifyInventory = shopifyInventory;
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
                    ProductNumber = s.SupplyProducts.Count,
                    TotalQuantity = s.SupplyProducts.Sum( p => p.Quantity )
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
                            VatRatePercent = p.VatRatePercent,
                            MarginPercent = p.MarginPercent,
                            SalePrice = p.SalePrice,
                            SyncWithShopify = p.SyncWithShopify
                        } )
                        .ToList()
                } )
                .FirstOrDefaultAsync();
        }

        public async Task<bool> DeleteSupplyAsync( int id )
        {
            Supply? supply = await _db.Supplies
                .FirstOrDefaultAsync( s => s.Id == id );

            if (supply == null)
            {
                return false;
            }

            _db.Supplies.Remove( supply );
            await _db.SaveChangesAsync();
            return true;
        }

    public async Task<SupplySaveResult> SaveSupplyAsync( SupplySaveRequest request )
    {
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
                if (item.VatRatePercent != 5m && item.VatRatePercent != 23m)
                {
                    throw new InvalidOperationException( "Працэнт VAT можа быць толькі 5 або 23." );
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

            Dictionary<string, decimal> syncedSalePrices = requestProducts
                .Where( p => p.SyncWithShopify )
                .GroupBy( p => p.ShopifyProductId.Trim(), StringComparer.OrdinalIgnoreCase )
                .ToDictionary(
                    g => g.Key,
                    g => g.Last().SalePrice,
                    StringComparer.OrdinalIgnoreCase
                );

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
        if (ShopifySessionReader.TryGet( _httpContextAccessor, out ShopifySession session ))
        {
            try
            {
                updates = await _shopifyInventory.ApplySupplySyncAsync(
                    session.Shop,
                    session.AccessToken,
                    deltas,
                    syncedSalePrices
                );
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
                    VatRatePercent = item.VatRatePercent,
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
}
