namespace backend.Models;

public class BukinistkaInventoryRowDto
{
    public int OdooProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? MainImageUrl { get; set; }
    public string OdooUrl { get; set; } = string.Empty;
    /// <summary>Accepted qty minus Wydanie (Shopify→Odoo deliveries).</summary>
    public int AcceptedQty { get; set; }
    /// <summary>Remaining Kirma consignment at Bukinistka (accepted − sold).</summary>
    public int QuantityInStock { get; set; }
    /// <summary>Bukinistka POS sales against Kirma consignment only.</summary>
    public int SoldQty { get; set; }
    /// <summary>Not implemented yet — always 0.</summary>
    public int PaidQty { get; set; }
    /// <summary>sold − paid.</summary>
    public int QuantityToPay { get; set; }
}

public class BukinistkaInventoryResponse
{
    public List<BukinistkaInventoryRowDto> Rows { get; set; } = new();
}
