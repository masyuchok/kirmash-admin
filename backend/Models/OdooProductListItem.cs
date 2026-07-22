namespace backend.Models;

public sealed class OdooProductListItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? DefaultCode { get; set; }
    public string? Barcode { get; set; }
    public decimal QuantityInStock { get; set; }
    public decimal ListPrice { get; set; }
    public decimal StandardPrice { get; set; }
    public string? UomName { get; set; }
    public string? SupplierName { get; set; }
    public string OdooUrl { get; set; } = string.Empty;
}

public sealed class OdooProductListResponse
{
    public List<OdooProductListItem> Products { get; set; } = new();
}
