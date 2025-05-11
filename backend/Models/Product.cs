namespace backend.Models
{
    public class Product
    {
        public int Id { get; set; }
        public string ShopifyId { get; set; }

        public int SupplierId { get; set; }
        public Supplier Supplier { get; set; }
    }
}
