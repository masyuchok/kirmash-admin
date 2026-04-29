namespace backend.Models
{
    public class ProductSyncRequest
    {
        public string ShopifyProductId { get; set; } = string.Empty;
        public int SupplierId { get; set; }
    }

    public class ProductSyncResult
    {
        public string ShopifyProductId { get; set; } = string.Empty;
        public int SupplierId { get; set; }
        public int SyncedQuantity { get; set; }
        public int PreviousAvailable { get; set; }
        public int NewAvailable { get; set; }
    }
}
