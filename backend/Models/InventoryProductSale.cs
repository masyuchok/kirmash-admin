namespace backend.Models
{
    public class InventoryProductSale
    {
        public int Id { get; set; }
        public string ShopifyProductId { get; set; } = string.Empty;
        public int SoldQuantity { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
