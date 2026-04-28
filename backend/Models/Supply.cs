namespace backend.Models
{
    public class Supply
    {
        public int Id { get; set; }
        public int SupplierId { get; set; }
        public Supplier Supplier { get; set; } = default!;

        public List<SupplyProduct> SupplyProducts { get; set; } = new();
        public DateOnly Date {  get; set; }
    }
}
