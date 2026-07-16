namespace backend.Models
{
    public class SupplierInventoryPriceOverride
    {
        public int Id { get; set; }
        public int SupplierId { get; set; }
        public Supplier Supplier { get; set; } = default!;
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ShopifyVariantId { get; set; } = string.Empty;
        public decimal NetUnitPrice { get; set; }
        public decimal VatRatePercent { get; set; }
        public decimal MarginPercent { get; set; }
        public decimal SalePrice { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
