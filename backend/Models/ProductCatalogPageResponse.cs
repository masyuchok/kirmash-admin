namespace backend.Models;

public sealed class ProductCatalogPageResponse
{
    public List<ProductWithSuppliersListItem> Items { get; set; } = new();
    public bool HasNextPage { get; set; }
    public string? EndCursor { get; set; }
    public string ProductCreateAdminUrl { get; set; } = string.Empty;
    public List<string> ProductTypes { get; set; } = new();
}
