namespace backend.Models;

public class VatReportForeignRowCreateRequest
{
    public string OrderNumber { get; set; } = string.Empty;
    public DateTime OrderDateUtc { get; set; }
    public string DeliveryName { get; set; } = string.Empty;
    public string DeliveryAddress { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public decimal ShippingGrossAmount { get; set; }
    public List<VatReportForeignRowItemCreateRequest> Items { get; set; } = [];
}
