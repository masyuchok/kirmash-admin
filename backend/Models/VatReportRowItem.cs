namespace backend.Models
{
    public class VatReportRowItem
    {
        public int Id { get; set; }
        public int VatReportRowId { get; set; }
        public VatReportRow VatReportRow { get; set; } = default!;
        public string ProductTitle { get; set; } = string.Empty;
        public string ProductType { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal GrossAmount { get; set; }
        public decimal AssignedVatRatePercent { get; set; }
        public string AssignmentReason { get; set; } = string.Empty;
    }
}
