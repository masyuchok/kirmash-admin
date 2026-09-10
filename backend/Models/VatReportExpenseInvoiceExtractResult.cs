namespace backend.Models;

public class VatReportExpenseInvoiceExtractProduct
{
    public string Title { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public int Quantity { get; set; }
    public decimal? UnitGrossPrice { get; set; }
    public decimal? VatRatePercent { get; set; }
    public string? ShopifyProductId { get; set; }
    public string? ShopifyVariantId { get; set; }
    public string? CatalogProductName { get; set; }
}

public class VatReportExpenseInvoiceExtractResult
{
    public string? InvoiceNumber { get; set; }
    /// <summary>yyyy-MM-dd for the expense date input.</summary>
    public string? ExpenseDateUtc { get; set; }
    public decimal? GrossAmount { get; set; }
    public decimal? VatAmount { get; set; }
    public decimal? NetAmount { get; set; }
    public string? VendorName { get; set; }
    public int? SuggestedSupplierId { get; set; }
    public string? SuggestedSupplierName { get; set; }
    public string? Comment { get; set; }
    /// <summary>Optional hint matching an ExpenseInvoiceType.Name.</summary>
    public string? SuggestedExpenseTypeName { get; set; }
    public List<VatReportExpenseInvoiceExtractProduct> Products { get; set; } = new();
    public string? Warning { get; set; }
}

public sealed class InvoiceCatalogMatchCandidate
{
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal VatRatePercent { get; set; }
    public decimal SupplierPrice { get; set; }
}
