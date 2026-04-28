namespace backend.Models
{
    public class SupplyProduct
    {
        public int Id { get; set; }

        public int SupplyId { get; set; }
        public Supply Supply { get; set; } = default!;

        public string ShopifyProductId { get; set; } = string.Empty;
    }
}
