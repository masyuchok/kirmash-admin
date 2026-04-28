namespace backend.Models
{
    public class ProductWithSuppliersListItem
    {
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string ProductType { get; set; } = string.Empty;
        public string ProductAdminUrl { get; set; } = string.Empty;
        public string? MainImageUrl { get; set; }
        public int QuantityInStock { get; set; }
        public List<string> Suppliers { get; set; } = new();
    }
}
