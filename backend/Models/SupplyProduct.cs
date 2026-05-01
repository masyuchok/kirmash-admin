namespace backend.Models
{
    public class SupplyProduct
    {
        public int Id { get; set; }

        public int SupplyId { get; set; }
        public Supply Supply { get; set; } = default!;

        public string ShopifyProductId { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal SupplierPrice { get; set; }
        public decimal VatRatePercent { get; set; } = 23m;
        public decimal MarginPercent { get; set; }
        public decimal SalePrice { get; set; }
        public bool SyncWithShopify { get; set; } = true;
    }
}
