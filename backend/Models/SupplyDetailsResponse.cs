namespace backend.Models
{
    public class SupplyDetailsResponse
    {
        public int Id { get; set; }
        public int SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public DateOnly Date { get; set; }
        public List<SupplyDetailsProductItem> Products { get; set; } = new();
    }

    public class SupplyDetailsProductItem
    {
        public string ShopifyProductId { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal SupplierPrice { get; set; }
        public decimal MarginPercent { get; set; }
        public decimal SalePrice { get; set; }
        public bool SyncWithShopify { get; set; }
    }
}
