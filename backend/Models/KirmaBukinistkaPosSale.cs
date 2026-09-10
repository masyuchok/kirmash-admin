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
    /// <summary>True when a later POS return reversed this sale (hidden from sales list).</summary>
    public bool IsReversed { get; set; }
    /// <summary>True for rows that record a processed POS return line (idempotency).</summary>
    public bool IsReturn { get; set; }
    /// <summary>True after a Poland VAT invoice was issued for this POS sale.</summary>
    public bool IsInvoiced { get; set; }
    public DateTime? InvoicedAtUtc { get; set; }
    public int? VatReportRowId { get; set; }
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
    public int? OfferId { get; set; }
    public int OdooProductId { get; set; }
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal? GrossUnitCost { get; set; }
    public string? SupplierName { get; set; }
    public bool IsOwnStock { get; set; }
    public DateTime SoldAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class KirmaBukinistkaPosInvoiceRequest
{
    public List<int> SaleIds { get; set; } = new();
    public DateTime InvoiceDateUtc { get; set; }
    public string? InvoiceNumber { get; set; }
    /// <summary>5 or 23. Defaults to 5 (books).</summary>
    public decimal? VatRatePercent { get; set; }
}

public class KirmaBukinistkaPosInvoiceResultDto
{
    public int VatReportId { get; set; }
    public int VatReportRowId { get; set; }
    public int PeriodYear { get; set; }
    public int PeriodMonth { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public decimal GrossAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal NetAmount { get; set; }
    public int InvoicedSaleCount { get; set; }
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

/// <summary>
/// Idempotency for Shopify order → Odoo Wydanie lines (SyncOnSale offers).
/// </summary>
public class KirmaBukinistkaShopifyDeliverySync
{
    public int Id { get; set; }
    public string ShopifyOrderId { get; set; } = string.Empty;
    public string ShopifyOrderNumber { get; set; } = string.Empty;
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public int OfferId { get; set; }
    public int OdooProductId { get; set; }
    public int Quantity { get; set; }
    public int OdooPickingId { get; set; }
    public string? OdooPickingName { get; set; }
    public bool IsCancelled { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public DateTime SoldAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class KirmaBukinistkaShopifyDeliverySyncState
{
    public int Id { get; set; }
    public DateTime? LastSyncedAtUtc { get; set; }
}

public class KirmaBukinistkaShopifyDeliverySyncResultDto
{
    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }
    public int OrdersScanned { get; set; }
    public int PickingsCreated { get; set; }
    public int PickingsCancelled { get; set; }
    public int UnitsSynced { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

public class KirmaBukinistkaShopifyDeliverySaleDto
{
    public int Id { get; set; }
    public int OfferId { get; set; }
    public string ShopifyOrderId { get; set; } = string.Empty;
    public string? ShopifyOrderNumber { get; set; }
    public string? OdooPickingName { get; set; }
    public int Quantity { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal? GrossUnitCost { get; set; }
    public string? SupplierName { get; set; }
    public DateTime SoldAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

