namespace backend.Services.Shopify;

public sealed class ShopifyOrderDto
{
    public string OrderId { get; set; } = string.Empty;
    public string OrderNumber { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public decimal CurrentTotalGross { get; set; }
    public decimal ShippingGross { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public List<ShopifyLineItemDto> Items { get; set; } = new();
}

public sealed class ShopifyLineItemDto
{
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public string VariantTitle { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotalGross { get; set; }
    public string ProductType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}

public sealed class ForeignDeliveryInfo
{
    public string Name { get; set; } = string.Empty;
    public string ShippingAddress { get; set; } = string.Empty;
    public string BillingAddress { get; set; } = string.Empty;
    public string ShippingCountryCode { get; set; } = string.Empty;
    public string BillingCountryCode { get; set; } = string.Empty;
}

internal enum ShopifyOrderScope
{
    Poland,
    Foreign
}

internal readonly record struct OrderShippingContext(
    string ShippingCountryCode,
    string BillingCountryCode,
    bool HasPickupShippingLine,
    bool HasZeroShippingLineWithTitle
);
