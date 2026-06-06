namespace backend.Models;

public class VatReportPeriodListItem
{
    public int PeriodYear { get; set; }
    public int PeriodMonth { get; set; }
    /// <summary>Усяго VAT (як у злучанай справаздачы).</summary>
    public decimal TotalVat { get; set; }
    public decimal Profit { get; set; }
    public bool IsLocked { get; set; }
    public int PrimaryReportId { get; set; }
    public List<VatReportListItem> Reports { get; set; } = new();
}
