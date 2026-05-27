namespace backend.Models
{
    public class VatReportExpense
    {
        public int Id { get; set; }
        public int VatReportId { get; set; }
        public VatReport VatReport { get; set; } = default!;
        public int ExpenseInvoiceTypeId { get; set; }
        public ExpenseInvoiceType ExpenseInvoiceType { get; set; } = default!;
        public decimal GrossAmount { get; set; }
        public decimal VatAmount { get; set; }
        public decimal NetAmount { get; set; }
        public DateTime ExpenseDateUtc { get; set; }
        public string? Comment { get; set; }
        public bool IsPaid { get; set; }
        public string InvoiceFileName { get; set; } = string.Empty;
        public string InvoiceContentType { get; set; } = string.Empty;
        public byte[]? InvoiceData { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public int? SupplierId { get; set; }
        public Supplier? Supplier { get; set; }
        public List<VatReportExpenseProduct> Products { get; set; } = new();
    }
}
