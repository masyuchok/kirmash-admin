namespace backend.Models;

public class SupplySupplierProductBalanceItem
{
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public int NetQuantity { get; set; }
}
