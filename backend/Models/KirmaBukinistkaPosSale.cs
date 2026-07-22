namespace backend.Models;

public class KirmaBukinistkaPosSale
{
    public int Id { get; set; }
    public int OdooPosOrderId { get; set; }
    public int OdooPosOrderLineId { get; set; }
    public string? OdooPosOrderName { get; set; }
    public int? OfferId { get; set; }
    public int OdooProductId { get; set; }
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string ProductName { get; set; } = string.Empty;
    /// <summary>
    /// Sale against Bukinistka's own pre-receipt stock — does not decrease Shopify.
    /// </summary>
    public bool IsOwnStock { get; set; }
    public DateTime SoldAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// Per Odoo product: how many units of Bukinistka's own stock must sell before Kirma consignment.
/// </summary>
public class KirmaBukinistkaOdooOwnStockBuffer
{
    public int Id { get; set; }
    public int OdooProductId { get; set; }
    public int OwnQtyRemaining { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class KirmaBukinistkaPosSyncState
{
    public int Id { get; set; }
    public DateTime? LastSyncedAtUtc { get; set; }
    public int? LastProcessedOrderId { get; set; }
}

public class KirmaBukinistkaPosSaleDto
{
    public int Id { get; set; }
    public int OdooPosOrderId { get; set; }
    public string? OdooPosOrderName { get; set; }
    public int OdooProductId { get; set; }
    public string ShopifyProductId { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public bool IsOwnStock { get; set; }
    public DateTime SoldAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class KirmaBukinistkaPosSyncResultDto
{
    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }
    public int OrdersScanned { get; set; }
    public int LinesProcessed { get; set; }
    public int UnitsSynced { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}
