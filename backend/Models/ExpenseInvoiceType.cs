namespace backend.Models
{
    public class ExpenseInvoiceType
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsSystem { get; set; }
        public DateTime CreatedAtUtc { get; set; }

        public List<VatReportExpense> Expenses { get; set; } = new();
    }
}
