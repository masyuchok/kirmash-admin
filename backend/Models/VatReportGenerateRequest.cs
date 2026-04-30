namespace backend.Models
{
    public class VatReportGenerateRequest
    {
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public string Type { get; set; } = VatReportType.Poland;
    }
}
