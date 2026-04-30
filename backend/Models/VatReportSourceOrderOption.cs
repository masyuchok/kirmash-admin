namespace backend.Models
{
    public class VatReportSourceOrderOption
    {
        public string ShopifyOrderId { get; set; } = string.Empty;
        public string OrderNumber { get; set; } = string.Empty;
        public DateTime OrderDateUtc { get; set; }
        public decimal VatRatePercent { get; set; }
        public decimal GrossAmount { get; set; }
        public decimal VatAmount { get; set; }
        public decimal NetAmount { get; set; }
    }
}
