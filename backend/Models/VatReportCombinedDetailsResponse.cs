namespace backend.Models;

public class VatReportCombinedDetailsResponse
{
    public VatReportDetailsResponse Details { get; set; } = new();
    public List<VatReportDetailsSummaryRow> ForeignRows { get; set; } = new();
}
