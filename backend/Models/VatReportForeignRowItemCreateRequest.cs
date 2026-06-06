namespace backend.Models;

public class VatReportForeignRowItemCreateRequest
{
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ProductTitle { get; set; } = string.Empty;
    public string ProductType { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
