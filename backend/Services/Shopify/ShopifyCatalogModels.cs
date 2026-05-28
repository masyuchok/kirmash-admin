using backend.Models;

namespace backend.Services.Shopify;

public sealed class ShopifyCatalogProduct
{
    public required string ProductId { get; init; }
    public required string Title { get; init; }
    public string ProductType { get; init; } = string.Empty;
    public int TotalInventory { get; init; }
    public string? ImageUrl { get; init; }
    public List<ProductVariantItem> Variants { get; init; } = [];
}
