namespace backend.Models
{
    public class VatReport
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
        public string[] Documents { get; set; } = [];
        public string[] ShopifyOrderIds { get; set; } = [];
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public List<VatReportRow> Rows { get; set; } = new();
    }
}
