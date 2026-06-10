namespace backend.Models;

public class VatReportCashSale
{
    public int Id { get; set; }
    public int VatReportId { get; set; }
    public VatReport VatReport { get; set; } = default!;
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public string ProductTitle { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal GrossAmount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
