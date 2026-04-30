namespace backend.Models
{
    public class VatReportRowUpdateRequest
    {
        public decimal VatRatePercent { get; set; }
        public decimal GrossAmount { get; set; }
        public decimal VatAmount { get; set; }
        public decimal NetAmount { get; set; }
    }
}
