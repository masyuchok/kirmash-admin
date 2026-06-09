namespace backend.Models
{
    public class VatReportExpenseProductCreateRequest
    {
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ShopifyVariantId { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitGrossPrice { get; set; }
        public decimal VatRatePercent { get; set; }
    }
}
