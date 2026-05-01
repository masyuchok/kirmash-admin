namespace backend.Models
{
    public class SupplySaveResult
    {
        public int SupplyId { get; set; }
        public string? Warning { get; set; }
        public List<SupplyInventoryUpdateResult> InventoryUpdates { get; set; } = new();
    }

    public class SupplyInventoryUpdateResult
    {
        public string ShopifyProductId { get; set; } = string.Empty;
        public int PreviousAvailable { get; set; }
        public int AddedQuantity { get; set; }
        public int NewAvailable { get; set; }
    }
}
