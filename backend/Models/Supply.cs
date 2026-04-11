namespace backend.Models
{
    public class Supply
    {
        public int Id { get; set; }
        public Supplier Supplier { get; set; }

        public List<Product> Products = new List<Product>();
        public DateOnly Date {  get; set; }
    }
}
