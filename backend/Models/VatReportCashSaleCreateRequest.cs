namespace backend.Models;

public class VatReportCashSaleCreateRequest
{
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ProductTitle { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
