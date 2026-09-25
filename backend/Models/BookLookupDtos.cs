namespace backend.Models;

public sealed class BookLookupCandidateDto
{
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? Isbn { get; set; }
    public string? Publisher { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Source { get; set; } = "web"; // supplier | web | image
    public string? Snippet { get; set; }
}

public sealed class BookLookupStepResultDto
{
    public string SessionId { get; set; } = string.Empty;
    public BookLookupCandidateDto? Candidate { get; set; }
    public string? QueryTitle { get; set; }
    public string? QueryAuthor { get; set; }
    public string? QueryIsbn { get; set; }
    public int AttemptsUsed { get; set; }
    public int AttemptsMax { get; set; }
    public bool Done { get; set; }
    /// <summary>True after photo OCR — wait for user to confirm title/author/ISBN before search.</summary>
    public bool AwaitingOcrConfirm { get; set; }
    /// <summary>Cover is in session and SerpAPI is configured — show «search by photo» button.</summary>
    public bool CanSearchByPhoto { get; set; }
    /// <summary>Both supplier-filtered and web photo searches have been used.</summary>
    public bool PhotoSearchExhausted { get; set; }
    public string? Message { get; set; }
}

public sealed class BookLookupFromTextRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? Isbn { get; set; }
    public int? SupplierId { get; set; }
}

public sealed class BookLookupConfirmOcrRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? Isbn { get; set; }
}

public sealed class BookCreateFromLookupRequest
{
    public string SessionId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? Isbn { get; set; }
    public string? Publisher { get; set; }
    public string? DescriptionHtml { get; set; }
    public decimal SalePrice { get; set; }
    public decimal UnitCost { get; set; }
    public bool UseCoverImage { get; set; } = true;
}

public sealed class BookCreateFromLookupResultDto
{
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}

public sealed class BookLookupFromUrlRequest
{
    public string? SessionId { get; set; }
    public string Url { get; set; } = string.Empty;
}
