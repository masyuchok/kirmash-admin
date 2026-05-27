namespace backend.Models
{
    public class ExpenseInvoiceTypeDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsSystem { get; set; }
    }

    public class ExpenseInvoiceTypeCreateRequest
    {
        public string Name { get; set; } = string.Empty;
    }

    public class ExpenseInvoiceTypeUpdateRequest
    {
        public string Name { get; set; } = string.Empty;
    }
}
