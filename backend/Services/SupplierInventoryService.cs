using backend.Data;

using backend.Models;

using Microsoft.EntityFrameworkCore;



namespace backend.Services

{

    public class SupplierInventoryService

    {

        private readonly AppDbContext _db;

        private readonly ProductService _productService;

        private readonly InventorySalesCacheService _salesCacheService;



        public SupplierInventoryService(

            AppDbContext db,

            ProductService productService,

            InventorySalesCacheService salesCacheService )

        {

            _db = db;

            _productService = productService;

            _salesCacheService = salesCacheService;

        }



        public async Task<SupplierInventoryResponse> GetInventoryAsync( int? supplierId, bool forceRefresh = false )

        {

            if (supplierId.HasValue && supplierId.Value <= 0)

            {

                throw new InvalidOperationException( "Некарэктны ідэнтыфікатар пастаўшчыка." );

            }



            if (supplierId.HasValue)

            {

                bool supplierExists = await _db.Suppliers.AnyAsync( s => s.Id == supplierId.Value );

                if (!supplierExists)

                {

                    throw new InvalidOperationException( "Пастаўшчык не знойдзены." );

                }

            }



            await ExpenseInvoiceTypeSeeder.EnsureDefaultAsync( _db );

            DateTime? salesSyncedAtUtc = await _salesCacheService.EnsureFreshAsync( force: forceRefresh );



            List<SupplyBatch> supplyBatches = await LoadSupplyBatchesAsync( supplierId );

            List<ProductWithSuppliersListItem>? catalog = await TryLoadCatalogAsync();

            Dictionary<string, string> productNames = await BuildProductNamesAsync( catalog );

            Dictionary<string, int> stockByProduct = BuildStockByProduct( catalog );

            Dictionary<string, int> soldByProduct = await _salesCacheService.GetSoldByProductAsync();



            Dictionary<(int SupplierId, string ProductId), int> soldBySupplierProduct = AllocateSoldBySupplierFifo(

                soldByProduct,

                supplyBatches

            );

            Dictionary<(int SupplierId, string ProductId), int> paidBySupplierProduct =

                await LoadPaidBySupplierProductAsync();

            Dictionary<(int SupplierId, string ProductId), decimal> latestSupplierPrice =

                supplyBatches

                    .GroupBy( x => (x.SupplierId, x.ShopifyProductId) )

                    .ToDictionary(

                        g => g.Key,

                        g => g.OrderByDescending( x => x.SupplyDate ).ThenByDescending( x => x.SupplyId ).First().SupplierPrice

                    );



            HashSet<(int SupplierId, string ProductId)> keys = new();

            foreach (SupplyBatch batch in supplyBatches)

            {

                keys.Add( (batch.SupplierId, batch.ShopifyProductId) );

            }

            foreach (KeyValuePair<(int SupplierId, string ProductId), int> sold in soldBySupplierProduct)

            {

                keys.Add( sold.Key );

            }

            foreach (KeyValuePair<(int SupplierId, string ProductId), int> paid in paidBySupplierProduct)

            {

                keys.Add( paid.Key );

            }



            Dictionary<int, string> supplierNames = await _db.Suppliers

                .AsNoTracking()

                .Where( s => !supplierId.HasValue || s.Id == supplierId.Value )

                .ToDictionaryAsync( s => s.Id!.Value, s => s.Name );



            List<SupplierInventoryRow> rows = keys

                .Select( key =>

                {

                    productNames.TryGetValue( key.ProductId, out string? productName );

                    latestSupplierPrice.TryGetValue( key, out decimal supplierPrice );

                    soldBySupplierProduct.TryGetValue( key, out int soldQuantity );

                    paidBySupplierProduct.TryGetValue( key, out int paidQuantity );

                    stockByProduct.TryGetValue( key.ProductId, out int quantityInStock );

                    supplierNames.TryGetValue( key.SupplierId, out string? supplierName );



                    return new SupplierInventoryRow

                    {

                        SupplierId = key.SupplierId,

                        SupplierName = supplierName ?? string.Empty,

                        ShopifyProductId = key.ProductId,

                        ProductName = string.IsNullOrWhiteSpace( productName ) ? key.ProductId : productName,

                        SupplierPrice = supplierPrice,

                        QuantityInStock = quantityInStock,

                        SoldQuantity = soldQuantity,

                        PaidQuantity = paidQuantity,

                        QuantityToPay = soldQuantity - paidQuantity

                    };

                } )

                .OrderBy( x => x.SupplierName, StringComparer.OrdinalIgnoreCase )

                .ThenBy( x => x.ProductName, StringComparer.OrdinalIgnoreCase )

                .ToList();



            return new SupplierInventoryResponse

            {

                Rows = rows,

                SalesSyncedAtUtc = salesSyncedAtUtc

            };

        }



        private async Task<List<SupplyBatch>> LoadSupplyBatchesAsync( int? supplierId )

        {

            List<SupplyProduct> supplyProducts = await _db.SupplyProducts

                .AsNoTracking()

                .Include( sp => sp.Supply )

                .Where( sp => !supplierId.HasValue || sp.Supply.SupplierId == supplierId.Value )

                .OrderBy( sp => sp.Supply.Date )

                .ThenBy( sp => sp.Supply.Id )

                .ThenBy( sp => sp.Id )

                .ToListAsync();



            return supplyProducts

                .Select( sp => new SupplyBatch

                {

                    SupplyId = sp.SupplyId,

                    SupplyDate = sp.Supply.Date,

                    SupplierId = sp.Supply.SupplierId,

                    ShopifyProductId = NormalizeProductId( sp.ShopifyProductId ),

                    Quantity = sp.Quantity,

                    SupplierPrice = sp.SupplierPrice

                } )

                .ToList();

        }



        private async Task<List<ProductWithSuppliersListItem>?> TryLoadCatalogAsync()

        {

            try

            {

                return await _productService.GetProductsWithSuppliersAsync();

            }

            catch

            {

                return null;

            }

        }



        private async Task<Dictionary<string, string>> BuildProductNamesAsync( List<ProductWithSuppliersListItem>? catalog )

        {

            Dictionary<string, string> names = new( StringComparer.OrdinalIgnoreCase );



            List<VatReportExpenseProduct> expenseProducts = await _db.VatReportExpenseProducts

                .AsNoTracking()

                .ToListAsync();

            foreach (VatReportExpenseProduct product in expenseProducts)

            {

                string productId = NormalizeProductId( product.ShopifyProductId );

                if (string.IsNullOrWhiteSpace( productId )) continue;

                if (!string.IsNullOrWhiteSpace( product.ProductTitle ))

                {

                    names[productId] = product.ProductTitle.Trim();

                }

            }



            if (catalog is null) return names;



            foreach (ProductWithSuppliersListItem product in catalog)

            {

                string productId = NormalizeProductId( product.ShopifyProductId );

                if (string.IsNullOrWhiteSpace( productId )) continue;

                if (!string.IsNullOrWhiteSpace( product.ProductName ))

                {

                    names[productId] = product.ProductName.Trim();

                }

            }



            return names;

        }



        private static Dictionary<string, int> BuildStockByProduct( List<ProductWithSuppliersListItem>? catalog )

        {

            Dictionary<string, int> stock = new( StringComparer.OrdinalIgnoreCase );

            if (catalog is null) return stock;



            foreach (ProductWithSuppliersListItem product in catalog)

            {

                string productId = NormalizeProductId( product.ShopifyProductId );

                if (string.IsNullOrWhiteSpace( productId )) continue;

                stock[productId] = product.QuantityInStock;

            }



            return stock;

        }



        private static Dictionary<(int SupplierId, string ProductId), int> AllocateSoldBySupplierFifo(

            Dictionary<string, int> soldByProduct,

            List<SupplyBatch> supplyBatches )

        {

            Dictionary<(int SupplierId, string ProductId), int> soldBySupplierProduct = new();

            IEnumerable<string> productIds = supplyBatches

                .Select( x => x.ShopifyProductId )

                .Concat( soldByProduct.Keys )

                .Distinct( StringComparer.OrdinalIgnoreCase );



            foreach (string productId in productIds)

            {

                int remainingSold = soldByProduct.GetValueOrDefault( productId );

                if (remainingSold <= 0) continue;



                foreach (SupplyBatch batch in supplyBatches.Where( x => x.ShopifyProductId == productId ))

                {

                    if (remainingSold <= 0) break;

                    int allocated = Math.Min( remainingSold, Math.Max( 0, batch.Quantity ) );

                    if (allocated <= 0) continue;



                    (int SupplierId, string ProductId) key = (batch.SupplierId, productId);

                    soldBySupplierProduct[key] = soldBySupplierProduct.GetValueOrDefault( key ) + allocated;

                    remainingSold -= allocated;

                }

            }



            return soldBySupplierProduct;

        }



        private async Task<Dictionary<(int SupplierId, string ProductId), int>> LoadPaidBySupplierProductAsync()

        {

            List<PaidProductRow> paidProducts = await _db.VatReportExpenseProducts

                .AsNoTracking()

                .Where( p =>

                    p.VatReportExpense.SupplierId.HasValue &&

                    p.VatReportExpense.ExpenseInvoiceType.Name == ExpenseInvoiceTypeSeeder.SupplierPaymentDefaultName )

                .Select( p => new PaidProductRow

                {

                    SupplierId = p.VatReportExpense.SupplierId!.Value,

                    ShopifyProductId = p.ShopifyProductId,

                    Quantity = p.Quantity

                } )

                .ToListAsync();



            return paidProducts

                .GroupBy( p => (p.SupplierId, NormalizeProductId( p.ShopifyProductId )) )

                .ToDictionary( g => g.Key, g => g.Sum( x => x.Quantity ) );

        }



        private static string NormalizeProductId( string raw )

        {

            if (string.IsNullOrWhiteSpace( raw )) return string.Empty;

            const string prefix = "gid://shopify/Product/";

            string trimmed = raw.Trim();

            return trimmed.StartsWith( prefix, StringComparison.OrdinalIgnoreCase )

                ? trimmed[prefix.Length..]

                : trimmed;

        }



        private sealed class SupplyBatch

        {

            public int SupplyId { get; set; }

            public DateOnly SupplyDate { get; set; }

            public int SupplierId { get; set; }

            public string ShopifyProductId { get; set; } = string.Empty;

            public int Quantity { get; set; }

            public decimal SupplierPrice { get; set; }

        }



        private sealed class PaidProductRow

        {

            public int SupplierId { get; set; }

            public string ShopifyProductId { get; set; } = string.Empty;

            public int Quantity { get; set; }

        }

    }

}

