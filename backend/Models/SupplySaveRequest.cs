namespace backend.Models
{
    public class SupplySaveRequest
    {
        public int? SupplyId { get; set; }
        public int SupplierId { get; set; }
        public DateOnly Date { get; set; }
        public List<SupplyProductSaveItem> Products { get; set; } = new();
    }

    public class SupplyProductSaveItem
    {
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ShopifyVariantId { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal SupplierPrice { get; set; }
        public decimal VatRatePercent { get; set; } = 23m;
        public decimal MarginPercent { get; set; }
        public decimal SalePrice { get; set; }
        public bool SyncWithShopify { get; set; } = true;
    }
}
