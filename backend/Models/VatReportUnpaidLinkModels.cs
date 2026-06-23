namespace backend.Models;

public class VatReportUnpaidLinkOptionsResponse
{
    public List<VatReportOverpaidExpenseProductOption> OverpaidProducts { get; set; } = new();
    public List<VatReportSupplierExpenseOption> SupplierInvoices { get; set; } = new();
    public List<VatReportSupplierExpenseOption> SupplierPaymentRecords { get; set; } = new();
}

public class VatReportOverpaidExpenseProductOption
{
    public int ExpenseProductId { get; set; }
    public int ExpenseId { get; set; }
    public DateTime ExpenseDateUtc { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public string ProductTitle { get; set; } = string.Empty;
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public string ShopifyVariantTitle { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int OverpaidQuantity { get; set; }
}

public class VatReportSupplierExpenseOption
{
    public int ExpenseId { get; set; }
    public DateTime ExpenseDateUtc { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public string ExpenseInvoiceTypeName { get; set; } = string.Empty;
    public decimal GrossAmount { get; set; }
    public int TotalProductUnits { get; set; }
    public bool HasInvoice { get; set; }
}

public class VatReportUnpaidLinkRequest
{
    public int PeriodYear { get; set; }
    public int PeriodMonth { get; set; }
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public string ProductTitle { get; set; } = string.Empty;
    public int SupplierId { get; set; }
    public int Quantity { get; set; }
    /// <summary>replace | link</summary>
    public string Mode { get; set; } = string.Empty;
    /// <summary>invoice | payment — які спіс выкарыстоўваўся пры link</summary>
    public string? LinkSource { get; set; }
    public int? ExpenseProductId { get; set; }
    public int? ExpenseId { get; set; }
}
