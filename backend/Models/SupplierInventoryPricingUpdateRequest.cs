namespace backend.Models;

public class SupplierInventoryPricingUpdateRequest
{
    public int SupplierId { get; set; }
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public decimal NetUnitPrice { get; set; }
    public decimal VatRatePercent { get; set; }
    public decimal MarginPercent { get; set; }
    public decimal SalePrice { get; set; }
    public bool SyncWithShopify { get; set; }
}
