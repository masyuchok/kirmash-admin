namespace backend.Models
{
    public class ProductHistoryResponse
    {
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public List<ProductHistorySupplyEvent> Supplies { get; set; } = new();
        public List<ProductHistorySaleEvent> Sales { get; set; } = new();
        public List<ProductHistoryPaymentEvent> Payments { get; set; } = new();
    }

    public class ProductHistorySupplyEvent
    {
        public string Date { get; set; } = string.Empty;
        public int SupplyId { get; set; }
        public int SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public string ShopifyVariantId { get; set; } = string.Empty;
        public string VariantTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }

    public class ProductHistorySaleEvent
    {
        public string DateUtc { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string OrderNumber { get; set; } = string.Empty;
        public int? ReportId { get; set; }
        public string ShopifyVariantId { get; set; } = string.Empty;
        public string VariantTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }

    public class ProductHistoryPaymentEvent
    {
        public string DateUtc { get; set; } = string.Empty;
        public int ExpenseId { get; set; }
        public int ReportId { get; set; }
        public int? SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public string InvoiceNumber { get; set; } = string.Empty;
        public string ShopifyVariantId { get; set; } = string.Empty;
        public string VariantTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }
}
