namespace backend.Models;

public class VatReportUnpaidAllocation
{
    public int Id { get; set; }
    public int SalePeriodYear { get; set; }
    public int SalePeriodMonth { get; set; }
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public string ProductTitle { get; set; } = string.Empty;
    public int SupplierId { get; set; }
    public int Quantity { get; set; }
    public int VatReportExpenseId { get; set; }
    public VatReportExpense VatReportExpense { get; set; } = default!;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
