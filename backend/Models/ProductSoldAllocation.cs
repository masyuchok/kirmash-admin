namespace backend.Models;

/// <summary>
/// Sold quantities split between resolved variant lines and legacy unnamed sales (FIFO spillover).
/// </summary>
public sealed class ProductSoldAllocation
{
    public Dictionary<(string ProductId, string VariantId), int> SoldByLine { get; set; } = new();

    public Dictionary<string, int> LegacyUnnamedSoldByProduct { get; set; } =
        new( StringComparer.OrdinalIgnoreCase );
}
