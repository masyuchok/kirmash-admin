namespace backend.Models
{
    public class VatReportRow
    {
        public int Id { get; set; }
        public int VatReportId { get; set; }
        public VatReport VatReport { get; set; } = default!;
        public string ShopifyOrderId { get; set; } = string.Empty;
        public string OrderNumber { get; set; } = string.Empty;
        public DateTime OrderDateUtc { get; set; }
        public decimal VatRatePercent { get; set; }
        public decimal GrossAmount { get; set; }
        public decimal VatAmount { get; set; }
        public decimal NetAmount { get; set; }
        public decimal ShippingGrossAmount { get; set; }
        public decimal ShippingNetAmount { get; set; }
        public string InvoiceFileName { get; set; } = string.Empty;
        public string InvoiceContentType { get; set; } = string.Empty;
        public byte[]? InvoiceData { get; set; }
        public List<VatReportRowItem> Items { get; set; } = new();
    }
}
