namespace backend.Models
{
    public class SupplyListItem
    {
        public int Id { get; set; }

        public string SupplierName { get; set; }
        public DateOnly Date { get; set; }
        public int BooksNumber { get; set; }

    }
}
