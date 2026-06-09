namespace backend.Models
{
    public class VatReportExpenseProduct
    {
        public int Id { get; set; }
        public int VatReportExpenseId { get; set; }
        public VatReportExpense VatReportExpense { get; set; } = default!;
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ShopifyVariantId { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitGrossPrice { get; set; }
    }
}
