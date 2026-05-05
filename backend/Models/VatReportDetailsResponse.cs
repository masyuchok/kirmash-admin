namespace backend.Models
{
    public class VatReportDetailsResponse
    {
        public int Id { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public decimal Vat { get; set; }
        public List<VatReportDetailsSummaryRow> Rows { get; set; } = new();
    }

    public class VatReportDetailsSummaryRow
    {
        public string Type { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ShopifyOrderId { get; set; } = string.Empty;
        public DateTime? OrderDateUtc { get; set; }
        public string DeliveryName { get; set; } = string.Empty;
        public string DeliveryAddress { get; set; } = string.Empty;
        public string ShippingAddress { get; set; } = string.Empty;
        public string BillingAddress { get; set; } = string.Empty;
        public decimal GrossAmount { get; set; }
        public decimal Vat { get; set; }
        public decimal NetAmount { get; set; }
        public List<VatReportDetailsPolandRow> PolandRows { get; set; } = new();
    }

    public class VatReportDetailsPolandRow
    {
        public int Id { get; set; }
        public string OrderNumber { get; set; } = string.Empty;
        public DateTime OrderDateUtc { get; set; }
        public decimal VatRatePercent { get; set; }
        public decimal GrossAmount { get; set; }
        public decimal VatAmount { get; set; }
        public decimal NetAmount { get; set; }
        public decimal ShippingGrossAmount { get; set; }
        public decimal ShippingNetAmount { get; set; }
        public string InvoiceFileName { get; set; } = string.Empty;
        public List<VatReportDetailsPolandItem> Items { get; set; } = new();
    }

    public class VatReportDetailsPolandItem
    {
        public int Id { get; set; }
        public string ProductTitle { get; set; } = string.Empty;
        public string ProductType { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal GrossAmount { get; set; }
        public decimal AssignedVatRatePercent { get; set; }
        public string AssignmentReason { get; set; } = string.Empty;
    }
}
