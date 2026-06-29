namespace backend.Models;

public class VatReportProductAllocationDebugResponse
{
    public string SearchTitle { get; set; } = string.Empty;
    public List<VatReportAllocationDebugSaleRow> Sales { get; set; } = new();
    public List<VatReportAllocationDebugPaymentRow> Payments { get; set; } = new();
    public List<VatReportAllocationDebugStepRow> Steps { get; set; } = new();
}

public class VatReportAllocationDebugSaleRow
{
    public int SaleId { get; set; }
    public string ShopifyOrderId { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public string VariantId { get; set; } = string.Empty;
    public string ProductTitle { get; set; } = string.Empty;
    public string ReportType { get; set; } = string.Empty;
    public int PeriodYear { get; set; }
    public int PeriodMonth { get; set; }
    public DateTime OrderDateUtc { get; set; }
    public int Quantity { get; set; }
    public int? SupplierId { get; set; }
    public bool IncludedAfterDedup { get; set; }
}

public class VatReportAllocationDebugPaymentRow
{
    public int PaymentId { get; set; }
    public string ProductId { get; set; } = string.Empty;
    public string VariantId { get; set; } = string.Empty;
    public string ProductTitle { get; set; } = string.Empty;
    public int? SupplierId { get; set; }
    public int Quantity { get; set; }
    public DateTime ExpenseDateUtc { get; set; }
}

public class VatReportAllocationDebugStepRow
{
    public int Order { get; set; }
    public string Event { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}
