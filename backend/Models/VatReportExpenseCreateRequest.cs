namespace backend.Models
{
    public class VatReportExpenseCreateRequest
    {
        public decimal GrossAmount { get; set; }
        public decimal VatAmount { get; set; }
        public decimal NetAmount { get; set; }
        public DateTime ExpenseDateUtc { get; set; }
        public string? Comment { get; set; }
        public string? InvoiceNumber { get; set; }
        public bool IsPaid { get; set; }
        public bool IsByProsvet { get; set; }
        public int ExpenseInvoiceTypeId { get; set; }
        public int? SupplierId { get; set; }
        public List<VatReportExpenseProductCreateRequest>? Products { get; set; }
    }
}
