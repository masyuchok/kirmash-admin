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
    /// <summary>Author(s) from Odoo Studio field x_studio_autor_1.</summary>
    public string? AuthorName { get; set; }
    public string OdooUrl { get; set; } = string.Empty;
    public bool CanProposeToKirma { get; set; } = true;
    public string? ProposeBlockReason { get; set; }
}

public sealed class OdooProductListResponse
{
    public List<OdooProductListItem> Products { get; set; } = new();
    /// <summary>Total active products matching the query in Odoo.</summary>
    public int TotalCount { get; set; }
    /// <summary>True when Odoo has more matches than returned in Products.</summary>
    public bool IsTruncated { get; set; }
}
