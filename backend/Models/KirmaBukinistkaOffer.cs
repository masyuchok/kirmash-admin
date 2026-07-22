namespace backend.Models;

public static class KirmaBukinistkaOfferStatuses
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
}

public class KirmaBukinistkaOffer
{
    public int Id { get; set; }
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string ProductAuthor { get; set; } = string.Empty;
    public string? MainImageUrl { get; set; }
    public string ProductAdminUrl { get; set; } = string.Empty;
    public string StorefrontUrl { get; set; } = string.Empty;
    public string? SupplierName { get; set; }
    public int Quantity { get; set; }
    public decimal GrossUnitCost { get; set; }
    public string Status { get; set; } = KirmaBukinistkaOfferStatuses.Pending;
    public int? OdooProductId { get; set; }
    public int? OdooQuantityBeforeAccept { get; set; }
    public decimal? AcceptedListPrice { get; set; }
    public string CreatedByLogin { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public class KirmaBukinistkaOfferCreateRequest
{
    public string ShopifyProductId { get; set; } = string.Empty;
    public string? ShopifyVariantId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? ProductAuthor { get; set; }
    public string? MainImageUrl { get; set; }
    public string? ProductAdminUrl { get; set; }
    public string? SupplierName { get; set; }
    public int Quantity { get; set; }
    public decimal GrossUnitCost { get; set; }
}

public class KirmaBukinistkaOfferUpdateRequest
{
    public int Quantity { get; set; }
    public decimal GrossUnitCost { get; set; }
}

public class KirmaBukinistkaOfferAcceptRequest
{
    public int OdooProductId { get; set; }
    /// <summary>New sale price; null/omitted keeps current Odoo list_price.</summary>
    public decimal? ListPrice { get; set; }
    /// <summary>
    /// When true, set Odoo standard_price (Кошт) to the offer gross unit cost.
    /// When false/null and costs differ, keep existing Odoo cost.
    /// </summary>
    public bool? ApplyKirmaCostPrice { get; set; }
}

public class KirmaBukinistkaOfferReceiptLineRequest
{
    public int OfferId { get; set; }
    public int OdooProductId { get; set; }
    public decimal? ListPrice { get; set; }
    public bool? ApplyKirmaCostPrice { get; set; }
}

public class KirmaBukinistkaOfferReceiptRequest
{
    public List<KirmaBukinistkaOfferReceiptLineRequest> Lines { get; set; } = new();
}

public class KirmaBukinistkaOfferReceiptResultDto
{
    public int PickingId { get; set; }
    public string PickingName { get; set; } = string.Empty;
    public List<KirmaBukinistkaOfferDto> Offers { get; set; } = new();
}

public class KirmaBukinistkaOfferDto
{
    public int Id { get; set; }
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string ProductAuthor { get; set; } = string.Empty;
    public string? MainImageUrl { get; set; }
    public string ProductAdminUrl { get; set; } = string.Empty;
    public string StorefrontUrl { get; set; } = string.Empty;
    public string? SupplierName { get; set; }
    public int Quantity { get; set; }
    public decimal GrossUnitCost { get; set; }
    public string Status { get; set; } = KirmaBukinistkaOfferStatuses.Pending;
    public int? OdooProductId { get; set; }
    public int? OdooQuantityBeforeAccept { get; set; }
    public decimal? AcceptedListPrice { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
