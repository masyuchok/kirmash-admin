namespace backend.Models
{
    public class Product
    {
        public int Id { get; set; }
        public string ShopifyId { get; set; }

        public List<Supply> Supplies = new List<Supply>();
    }
}
