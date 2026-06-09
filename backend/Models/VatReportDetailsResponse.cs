namespace backend.Models
{
    public class VatReportDetailsResponse
    {
        public int Id { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public bool IsLocked { get; set; }
        public decimal Vat { get; set; }
        public decimal Profit { get; set; }
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
        public string ShippingCountryCode { get; set; } = string.Empty;
        public string BillingCountryCode { get; set; } = string.Empty;
        public decimal GrossAmount { get; set; }
        public decimal Vat { get; set; }
        public decimal NetAmount { get; set; }
        public List<VatReportDetailsPolandRow> PolandRows { get; set; } = new();
        public List<VatReportExpenseRow> ExpenseRows { get; set; } = new();
        public List<VatReportCashSaleRow> CashSaleRows { get; set; } = new();
    }

    public class VatReportCashSaleRow
    {
        public int Id { get; set; }
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal GrossAmount { get; set; }
        public DateTime CreatedAtUtc { get; set; }
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

    public class VatReportExpenseRow
    {
        public int Id { get; set; }
        public decimal GrossAmount { get; set; }
        public decimal VatAmount { get; set; }
        public decimal NetAmount { get; set; }
        public DateTime ExpenseDateUtc { get; set; }
        public string Comment { get; set; } = string.Empty;
        public string InvoiceNumber { get; set; } = string.Empty;
        public bool IsPaid { get; set; }
        public bool IsByProsvet { get; set; }
        public bool IncludeVatInTotal { get; set; }
        public int ExpenseInvoiceTypeId { get; set; }
        public string ExpenseInvoiceTypeName { get; set; } = string.Empty;
        public string InvoiceFileName { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public int? SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public List<VatReportExpenseProductRow> Products { get; set; } = new();
    }

    public class VatReportExpenseProductRow
    {
        public int Id { get; set; }
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitGrossPrice { get; set; }
    }
}
