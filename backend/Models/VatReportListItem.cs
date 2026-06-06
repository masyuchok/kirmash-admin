namespace backend.Models
{
    public class VatReportListItem
    {
        public int Id { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Document { get; set; }
        public decimal Vat { get; set; }
        public decimal VatCredit { get; set; }
        public decimal VatToPay { get; set; }
        public List<string> Documents { get; set; } = new();
        public List<string> ShopifyOrderIds { get; set; } = new();
        public bool IsLocked { get; set; }
    }
}
