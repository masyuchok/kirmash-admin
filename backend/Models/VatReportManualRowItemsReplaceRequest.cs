namespace backend.Models;

public class VatReportManualRowItemsReplaceRequest
{
    public List<VatReportForeignRowItemCreateRequest> Items { get; set; } = [];
}
