namespace backend.Models
{
    public class SupplyListItem
    {
        public int Id { get; set; }

        public string SupplierName { get; set; } = string.Empty;
        public DateOnly Date { get; set; }
        public int ProductNumber { get; set; }

    }
}
