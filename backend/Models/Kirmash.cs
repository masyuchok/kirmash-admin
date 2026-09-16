namespace backend.Models;

public class Kirmash
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateOnly EventDate { get; set; }
    public string Status { get; set; } = "draft";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<KirmashLine> Lines { get; set; } = new();
    public List<KirmashPriceTag> PriceTags { get; set; } = new();
}

public class KirmashLine
{
    public int Id { get; set; }
    public int KirmashId { get; set; }
    public Kirmash? Kirmash { get; set; }

    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }

    public List<KirmashPriceTag> PriceTags { get; set; } = new();
}

public class KirmashPriceTag
{
    public int Id { get; set; }
    public int KirmashId { get; set; }
    public Kirmash? Kirmash { get; set; }
    public int KirmashLineId { get; set; }
    public KirmashLine? KirmashLine { get; set; }

    public int Sequence { get; set; }
    public string Title { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public string CheckoutUrl { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class KirmashLineInput
{
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
}

public sealed class KirmashUpsertRequest
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EventDate { get; set; } = string.Empty;
    public List<KirmashLineInput> Lines { get; set; } = new();
}

public sealed class KirmashListItemDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EventDate { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int LinesCount { get; set; }
    public int TagsCount { get; set; }
    public int UnitsCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class KirmashLineDto
{
    public int Id { get; set; }
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
}

public sealed class KirmashDetailDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EventDate { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<KirmashLineDto> Lines { get; set; } = new();
    public int PriceTagsCount { get; set; }
}

public sealed class KirmashPriceTagDto
{
    public int Id { get; set; }
    public int KirmashLineId { get; set; }
    public int Sequence { get; set; }
    public string Title { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public string CheckoutUrl { get; set; } = string.Empty;
}
