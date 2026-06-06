namespace backend.Models;

public class SupplyCatalogProductItem
{
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal VatRatePercent { get; set; } = 23m;
}
