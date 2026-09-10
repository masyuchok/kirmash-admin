namespace backend.Models;

/// <summary>
/// Idempotency ledger: pending offer qty already reduced for a Shopify order or Odoo POS line.
/// </summary>
public class KirmaBukinistkaPendingOfferSaleDeduction
{
    public const string SourceShopifyOrder = "ShopifyOrder";
    public const string SourceOdooPosLine = "OdooPosLine";

    public int Id { get; set; }
    public int OfferId { get; set; }
    public KirmaBukinistkaOffer Offer { get; set; } = default!;
    public string Source { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
