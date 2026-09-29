namespace backend.Models;

public sealed class BookLookupCandidateDto
{
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? Isbn { get; set; }
    public string? Publisher { get; set; }
    public string? Description { get; set; }
    public string? CoverImageUrl { get; set; }
    /// <summary>Extra gallery images from the source page (not styled).</summary>
    public List<string> AdditionalImageUrls { get; set; } = new();
    /// <summary>Book weight in kilograms when the source page provides it.</summary>
    public decimal? WeightKg { get; set; }
    /// <summary>Shop sale price from the source product page (typically PLN).</summary>
    public decimal? SalePrice { get; set; }
    /// <summary>Cover binding: soft | hard.</summary>
    public string? CoverType { get; set; }
    /// <summary>Age recommendation like 0+, 12+, 18+ when found on page/cover/price list.</summary>
    public string? AgeRating { get; set; }
    /// <summary>Book format / size, e.g. 130×200 or A5.</summary>
    public string? Format { get; set; }
    /// <summary>Illustrator name(s).</summary>
    public string? Illustrator { get; set; }
    /// <summary>Language label(s), e.g. беларуская.</summary>
    public string? Language { get; set; }
    /// <summary>Page count when known.</summary>
    public int? PageCount { get; set; }
    /// <summary>Place of publication (city).</summary>
    public string? PlaceOfPublication { get; set; }
    /// <summary>Translation note, e.g. з польскай.</summary>
    public string? Translation { get; set; }
    /// <summary>Publication year.</summary>
    public int? Year { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Source { get; set; } = "web"; // supplier | web | image
    public string? Snippet { get; set; }
}

public sealed class BookCreateDraftShellRequest
{
    public string Title { get; set; } = string.Empty;
    public string? DescriptionHtml { get; set; }
    public decimal SalePrice { get; set; }
    public string? CoverImageUrl { get; set; }
    public string? CoverImageBase64 { get; set; }
    public bool CoverAlreadyStyled { get; set; }
    public bool UseCoverImage { get; set; } = true;
    public List<string>? AdditionalImageUrls { get; set; }
    public string? Isbn { get; set; }
    public decimal? WeightKg { get; set; }
    public int? Quantity { get; set; }
    public string? Author { get; set; }
    public string? CoverType { get; set; }
    public string? AgeRating { get; set; }
    public string? Format { get; set; }
    public string? Illustrator { get; set; }
    public string? Language { get; set; }
    public int? PageCount { get; set; }
    public string? PlaceOfPublication { get; set; }
    public string? Translation { get; set; }
    public int? Year { get; set; }
    /// <summary>Publisher → Shopify Vendor.</summary>
    public string? Publisher { get; set; }
    public List<string>? Genres { get; set; }
    /// <summary>
    /// Stable image slot ids for SEO ALT generation (e.g. cover, extra-1).
    /// Returned alts are applied on attach-draft-images.
    /// </summary>
    public List<string>? SeoImageIds { get; set; }
}

public sealed class BookAttachDraftImagesRequest
{
    public string ShopifyProductId { get; set; } = string.Empty;
    /// <summary>Data URL or raw base64 of the cover as shown in the modal.</summary>
    public string? CoverImageBase64 { get; set; }
    /// <summary>Data URLs / base64 for gallery images shown in the modal.</summary>
    public List<string>? AdditionalImageBase64 { get; set; }
    public string? CoverImageUrl { get; set; }
    public List<string>? AdditionalImageUrls { get; set; }
    /// <summary>Temp media id from POST books/cache-images (preferred over URL).</summary>
    public string? CoverTempMediaId { get; set; }
    public List<string>? AdditionalTempMediaIds { get; set; }
    /// <summary>ALT for the cover image (from OpenAI SEO bundle).</summary>
    public string? CoverImageAlt { get; set; }
    /// <summary>ALTs for additional images, same order as AdditionalTempMediaIds / base64.</summary>
    public List<string>? AdditionalImageAlts { get; set; }
}

public sealed class BookAttachDraftImagesResultDto
{
    public int AttachedCount { get; set; }
    public List<string> Errors { get; set; } = new();
    /// <summary>Shopify MediaImage GIDs created during this attach call (cover first, then extras).</summary>
    public List<string> AttachedMediaIds { get; set; } = new();
}

public sealed class BookImageAltDto
{
    public string ImageId { get; set; } = string.Empty;
    public string Alt { get; set; } = string.Empty;
}

public sealed class BookCreateFromLookupResultDto
{
    public string ShopifyProductId { get; set; } = string.Empty;
    public string ShopifyVariantId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ShopifyAdminUrl { get; set; } = string.Empty;
    /// <summary>SEO ALT texts keyed by SeoImageIds (cover, extra-1, …).</summary>
    public List<BookImageAltDto> ImageAlts { get; set; } = new();
}

public sealed class BookLookupFromUrlRequest
{
    public string? SessionId { get; set; }
    public string Url { get; set; } = string.Empty;
}

public sealed class BookStyleCoverRequest
{
    public string? SessionId { get; set; }
    public string? CoverImageUrl { get; set; }
    /// <summary>Optional raw base64 or data-URL when the browser can fetch the image but the server cannot.</summary>
    public string? CoverImageBase64 { get; set; }
    /// <summary>Preferred: bytes already cached via fetch-cover.</summary>
    public string? CoverTempMediaId { get; set; }
}

public sealed class BookStyleCoverResultDto
{
    public string StyledCoverDataUrl { get; set; } = string.Empty;
    /// <summary>Styled PNG stored in temp media (use for Shopify attach).</summary>
    public string? TempMediaId { get; set; }
    public string? TempMediaPath { get; set; }
}

public sealed class BookFetchCoverRequest
{
    public string Url { get; set; } = string.Empty;
    /// <summary>Product page URL on the supplier site — used as Referer (helps some CDNs / Cloudflare).</summary>
    public string? PageUrl { get; set; }
}

public sealed class BookFetchCoverResultDto
{
    public string? CoverImageBase64 { get; set; }
    public bool Found { get; set; }
    public string? TempMediaId { get; set; }
    public string? TempMediaPath { get; set; }
    /// <summary>Diagnostic fields when Found=false (or always in Development).</summary>
    public string? SourceUrl { get; set; }
    public string? FinalUrl { get; set; }
    public int? StatusCode { get; set; }
    public string? ReasonPhrase { get; set; }
    public string? ContentType { get; set; }
    public long? ContentLengthHeader { get; set; }
    public int? ByteLength { get; set; }
    public string? Error { get; set; }
    public string? ExceptionType { get; set; }
    /// <summary>direct | relay — which downloader produced the bytes (dev diagnostics).</summary>
    public string? FetchSource { get; set; }
    public int? DirectStatus { get; set; }
    public bool? RelayConfigured { get; set; }
    public bool? RelayAttempted { get; set; }
    public bool? ShouldFallbackToRelay { get; set; }
    public int? RelayStatus { get; set; }
    public string? RelayError { get; set; }
    /// <summary>Hostname only of IMAGE_FETCH_RELAY_URL.</summary>
    public string? RelayUrlHost { get; set; }
}

public sealed class BookLookupSupplierCostRequest
{
    public int SupplierId { get; set; }
    public string? Title { get; set; }
    public string? Isbn { get; set; }
    public string? Author { get; set; }
}

public sealed class BookLookupSupplierCostResultDto
{
    public decimal? UnitCostBrutto { get; set; }
    public decimal? WeightKg { get; set; }
    public string? CoverType { get; set; }
    public string? AgeRating { get; set; }
    public string? Format { get; set; }
    public string? Illustrator { get; set; }
    public string? Language { get; set; }
    public int? PageCount { get; set; }
    public string? PlaceOfPublication { get; set; }
    public string? Translation { get; set; }
    public int? Year { get; set; }
    public string? PriceListRowText { get; set; }
    public bool Found { get; set; }
}

public sealed class BookGenreOptionsDto
{
    public string Namespace { get; set; } = "book";
    public string Key { get; set; } = "genre";
    public string Type { get; set; } = "list.single_line_text_field";
    public List<string> Options { get; set; } = new();
}

public sealed class BookSuggestGenresRequest
{
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? CoverType { get; set; }
    public string? Publisher { get; set; }
    public string? Isbn { get; set; }
    public string? SupplierPageSnippet { get; set; }
    public string? PriceListRowText { get; set; }
    public int? SupplierId { get; set; }
    public List<string>? SelectedGenres { get; set; }
}

public sealed class BookSuggestGenresResultDto
{
    public List<string> Genres { get; set; } = new();
}

public sealed class BookVendorOptionsDto
{
    public List<string> Options { get; set; } = new();
}

public sealed class BookSuggestVendorRequest
{
    public string? Publisher { get; set; }
    public string? SupplierPageSnippet { get; set; }
    public string? PriceListRowText { get; set; }
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Isbn { get; set; }
    public int? SupplierId { get; set; }
}

public sealed class BookSuggestVendorResultDto
{
    public string? Vendor { get; set; }
}
