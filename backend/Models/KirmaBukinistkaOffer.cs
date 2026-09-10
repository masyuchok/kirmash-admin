namespace backend.Models;

public static class KirmaBukinistkaOfferStatuses
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
}

public static class KirmaBukinistkaOfferDirections
{
    public const string KirmaToBukinistka = "KirmaToBukinistka";
    public const string BukinistkaToKirma = "BukinistkaToKirma";
}

public class KirmaBukinistkaOffer
{
    public int Id { get; set; }
    /// <summary>KirmaToBukinistka (default) or BukinistkaToKirma.</summary>
    public string Direction { get; set; } = KirmaBukinistkaOfferDirections.KirmaToBukinistka;
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
    /// <summary>
    /// When true, Shopify sales of this accepted offer create an Odoo Wydanie.
    /// </summary>
    public bool SyncOnSale { get; set; }
    /// <summary>
    /// Buk→Kirma create that, after Kirma accepts, accounts like Kirma→Buk
    /// (Odoo owner = Kirma, Buk POS sales pay Kirma).
    /// </summary>
    public bool IsAssignment { get; set; }
    /// <summary>
    /// Sender updated GrossUnitCost after accept; peer should apply cost to Shopify/Odoo.
    /// </summary>
    public bool PeerPriceChangePending { get; set; }
    public string CreatedByLogin { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
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
    public bool SyncOnSale { get; set; }
}

public class KirmaBukinistkaOfferCreateFromBukinistkaRequest
{
    public int OdooProductId { get; set; }
    public int Quantity { get; set; }
    public decimal GrossUnitCost { get; set; }
    public bool SyncOnSale { get; set; } = true;
    /// <summary>When true, create as assignment (Назначэнне), not a plain propose.</summary>
    public bool IsAssignment { get; set; }
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

public class KirmaBukinistkaOfferAcceptByKirmaRequest
{
    public string ShopifyProductId { get; set; } = string.Empty;
    public string? ShopifyVariantId { get; set; }
    /// <summary>New Shopify sale price; null/omitted keeps current variant price.</summary>
    public decimal? SalePrice { get; set; }
}

public class KirmaBukinistkaOfferAcceptByKirmaResultDto
{
    public KirmaBukinistkaOfferDto Offer { get; set; } = new();
    public string? ShopifySyncWarning { get; set; }
}

public class KirmaBukinistkaOfferShopifySalePriceRequest
{
    public decimal SalePrice { get; set; }
}

/// <summary>Preview data for creating a new Odoo product from a Kirma offer.</summary>
public class KirmaBukinistkaOfferCreateProductPreviewDto
{
    public string ProductName { get; set; } = string.Empty;
    public string ProductAuthor { get; set; } = string.Empty;
    public decimal GrossUnitCost { get; set; }
    public decimal? ShopifySalePrice { get; set; }
    public string? Vendor { get; set; }
    public string? ProductType { get; set; }
    public string? BarcodeDigits { get; set; }
    public string? WeightKg { get; set; }
}

public class KirmaBukinistkaOfferCreateProductRequest
{
    public decimal ListPrice { get; set; }

    /// <summary>
    /// When true, create the Odoo product without barcode/ISBN
    /// (needed when magazines share the same barcode in Odoo).
    /// </summary>
    public bool? OmitBarcode { get; set; }

    /// <summary>
    /// When set (and <see cref="OmitBarcode"/> is not true), overrides the Shopify barcode.
    /// Empty / whitespace means omit barcode.
    /// </summary>
    public string? BarcodeDigits { get; set; }
}

public class KirmaBukinistkaOfferCreateProductResultDto
{
    public int OdooProductId { get; set; }
    public string OdooProductName { get; set; } = string.Empty;
    public string OdooUrl { get; set; } = string.Empty;
    public decimal ListPrice { get; set; }
}

/// <summary>Preview data for creating a new Shopify product from a Bukinistka offer.</summary>
public class KirmaBukinistkaOfferCreateShopifyProductPreviewDto
{
    public string ProductName { get; set; } = string.Empty;
    public string ProductAuthor { get; set; } = string.Empty;
    public decimal GrossUnitCost { get; set; }
    public decimal? OdooListPrice { get; set; }
    public string? Vendor { get; set; }
    public string? ProductType { get; set; }
    public string? BarcodeDigits { get; set; }
    public string? WeightKg { get; set; }
    public string? DescriptionHtml { get; set; }
}

public class KirmaBukinistkaOfferCreateShopifyProductRequest
{
    public decimal SalePrice { get; set; }

    /// <summary>When true, create without barcode/ISBN.</summary>
    public bool? OmitBarcode { get; set; }

    /// <summary>Override Odoo barcode when <see cref="OmitBarcode"/> is not true.</summary>
    public string? BarcodeDigits { get; set; }
}

public class KirmaBukinistkaOfferCreateShopifyProductResultDto
{
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public string ShopifyProductName { get; set; } = string.Empty;
    public decimal SalePrice { get; set; }
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
    public string Direction { get; set; } = KirmaBukinistkaOfferDirections.KirmaToBukinistka;
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
    public bool SyncOnSale { get; set; }
    public bool IsAssignment { get; set; }
    public bool PeerPriceChangePending { get; set; }
    public int RemainingQuantity { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>Live Shopify variant sale price when the offer is linked to a product.</summary>
    public decimal? ShopifySalePrice { get; set; }
    /// <summary>Odoo list_price — Bukinistka retail sale price.</summary>
    public decimal? BukinistkaSalePrice { get; set; }
}

public class BukinistkaProposeEligibilityDto
{
    public bool CanPropose { get; set; }
    public string? BlockReason { get; set; }
}
