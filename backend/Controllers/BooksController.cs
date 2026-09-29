using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route( "books" )]
[Authorize]
public sealed class BooksController : ControllerBase
{
    private readonly BookLookupService _lookup;
    private readonly SupplierPriceListLookupService _priceListLookup;
    private readonly ILogger<BooksController> _logger;

    public BooksController(
        BookLookupService lookup,
        SupplierPriceListLookupService priceListLookup,
        ILogger<BooksController> logger )
    {
        _lookup = lookup;
        _priceListLookup = priceListLookup;
        _logger = logger;
    }

    [HttpPost( "lookup/from-url" )]
    public async Task<ActionResult<BookLookupCandidateDto>> LookupFromUrl(
        [FromBody] BookLookupFromUrlRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            BookLookupCandidateDto result = await _lookup.ImportFromUrlAsync(
                request.SessionId,
                request.Url,
                cancellationToken );
            return Ok( result );
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return StatusCode( 500, new { error = "Памылка чытання спасылкі", details = ex.Message } );
        }
    }

    [HttpPost( "style-cover" )]
    public async Task<ActionResult<BookStyleCoverResultDto>> StyleCover(
        [FromBody] BookStyleCoverRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            BookStyleCoverResultDto result =
                await _lookup.StyleCoverPreviewAsync( request, cancellationToken );
            return Ok( result );
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning( ex, "style-cover rejected" );
            // Soft-fail: preview is optional; avoid noisy 400 when CDN blocks the image.
            return Ok( new BookStyleCoverResultDto { StyledCoverDataUrl = string.Empty } );
        }
        catch (Exception ex)
        {
            return StatusCode( 500, new { error = "Памылка апрацоўкі вокладкі", details = ex.Message } );
        }
    }

    [HttpPost( "fetch-cover" )]
    public async Task<ActionResult<BookFetchCoverResultDto>> FetchCover(
        [FromBody] BookFetchCoverRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            _logger.LogInformation(
                "fetch-cover request Url={Url}",
                (request.Url ?? string.Empty).Trim() );
            BookFetchCoverResultDto result =
                await _lookup.FetchCoverImageAsync( request, cancellationToken );
            return Ok( result );
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "fetch-cover failed" );
            return Ok(
                new BookFetchCoverResultDto
                {
                    Found = false,
                    SourceUrl = request.Url,
                    Error = ex.Message,
                    ExceptionType = ex.GetType().FullName,
                } );
        }
    }

    [HttpGet( "temp-media/{id}" )]
    [AllowAnonymous]
    [ResponseCache( Duration = 0, NoStore = true )]
    public IActionResult GetTempMedia( string id )
    {
        // Unguessable GUID ids; anonymous GET so <img src> works even if cookie
        // is missing on image subrequests through the proxy.
        if (!_lookup.TryGetTempMedia( id, out BookTempMediaEntry entry ))
        {
            return NotFound();
        }

        return File( entry.Bytes, entry.ContentType );
    }

    [HttpPost( "lookup-supplier-cost" )]
    public async Task<ActionResult<BookLookupSupplierCostResultDto>> LookupSupplierCost(
        [FromBody] BookLookupSupplierCostRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            if (request.SupplierId <= 0)
            {
                return Ok( new BookLookupSupplierCostResultDto { Found = false } );
            }

            decimal? cost = null;
            decimal? weight = null;
            var match = await _priceListLookup.LookupFromPriceListAsync(
                request.SupplierId,
                request.Title,
                request.Isbn,
                request.Author,
                cancellationToken );
            if (match is not null)
            {
                cost = match.UnitCostBrutto;
                weight = match.WeightKg;
            }

            return Ok( new BookLookupSupplierCostResultDto
            {
                UnitCostBrutto = cost,
                WeightKg = weight,
                CoverType = match?.CoverType,
                AgeRating = match?.AgeRating,
                Format = match?.Format,
                Illustrator = match?.Illustrator,
                Language = match?.Language,
                PageCount = match?.PageCount,
                PlaceOfPublication = match?.PlaceOfPublication,
                Translation = match?.Translation,
                Year = match?.Year,
                PriceListRowText = match?.RawRowText,
                Found = cost is > 0m
                    || weight is > 0m
                    || !string.IsNullOrWhiteSpace( match?.CoverType )
                    || !string.IsNullOrWhiteSpace( match?.AgeRating )
                    || !string.IsNullOrWhiteSpace( match?.Format )
                    || !string.IsNullOrWhiteSpace( match?.Illustrator )
                    || !string.IsNullOrWhiteSpace( match?.Language )
                    || match?.PageCount is > 0
                    || !string.IsNullOrWhiteSpace( match?.PlaceOfPublication )
                    || !string.IsNullOrWhiteSpace( match?.Translation )
                    || match?.Year is > 0,
            } );
        }
        catch (Exception ex)
        {
            return StatusCode(
                500,
                new { error = "Памылка пошуку цаны ў прайсе", details = ex.Message } );
        }
    }

    [HttpGet( "genre-options" )]
    public async Task<ActionResult<BookGenreOptionsDto>> GenreOptions(
        CancellationToken cancellationToken )
    {
        try
        {
            BookGenreOptionsDto result = await _lookup.GetGenreOptionsAsync( cancellationToken );
            return Ok( result );
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return StatusCode( 500, new { error = "Памылка чытання жанраў", details = ex.Message } );
        }
    }

    [HttpGet( "vendor-options" )]
    public async Task<ActionResult<BookVendorOptionsDto>> VendorOptions(
        CancellationToken cancellationToken )
    {
        try
        {
            BookVendorOptionsDto result = await _lookup.GetVendorOptionsAsync( cancellationToken );
            return Ok( result );
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return StatusCode(
                500,
                new { error = "Памылка чытання выдаўцоў (Vendor)", details = ex.Message } );
        }
    }

    [HttpPost( "suggest-vendor" )]
    public async Task<ActionResult<BookSuggestVendorResultDto>> SuggestVendor(
        [FromBody] BookSuggestVendorRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            if (string.IsNullOrWhiteSpace( request.PriceListRowText )
                && request.SupplierId is int supplierId
                && supplierId > 0)
            {
                var match = await _priceListLookup.LookupFromPriceListAsync(
                    supplierId,
                    request.Title,
                    request.Isbn,
                    request.Author,
                    cancellationToken );
                if (!string.IsNullOrWhiteSpace( match?.RawRowText ))
                {
                    request.PriceListRowText = match.RawRowText;
                }
            }

            BookSuggestVendorResultDto result =
                await _lookup.SuggestVendorAsync( request, cancellationToken );
            return Ok( result );
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return StatusCode(
                500,
                new { error = "Памылка прапановы выдаўца", details = ex.Message } );
        }
    }

    [HttpPost( "suggest-genres" )]
    public async Task<ActionResult<BookSuggestGenresResultDto>> SuggestGenres(
        [FromBody] BookSuggestGenresRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            if (string.IsNullOrWhiteSpace( request.PriceListRowText )
                && request.SupplierId is int supplierId
                && supplierId > 0)
            {
                var match = await _priceListLookup.LookupFromPriceListAsync(
                    supplierId,
                    request.Title,
                    request.Isbn,
                    request.Author,
                    cancellationToken );
                if (!string.IsNullOrWhiteSpace( match?.RawRowText ))
                {
                    request.PriceListRowText = match.RawRowText;
                }
            }

            BookSuggestGenresResultDto result =
                await _lookup.SuggestGenresAsync( request, cancellationToken );
            return Ok( result );
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return StatusCode( 500, new { error = "Памылка прапановы жанраў", details = ex.Message } );
        }
    }

    [HttpPost( "create-draft-shell" )]
    public async Task<ActionResult<BookCreateFromLookupResultDto>> CreateDraftShell(
        [FromBody] BookCreateDraftShellRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            BookCreateFromLookupResultDto result =
                await _lookup.CreateDraftShellAsync( request, cancellationToken );
            return Ok( result );
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning( ex, "create-draft-shell rejected" );
            return BadRequest( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            _logger.LogError( ex, "create-draft-shell failed" );
            return StatusCode(
                500,
                new { error = "Памылка стварэння чарнавіка", details = ex.Message } );
        }
    }

    [HttpPost( "attach-draft-images" )]
    [RequestSizeLimit( 32 * 1024 * 1024 )]
    public async Task<ActionResult<BookAttachDraftImagesResultDto>> AttachDraftImages(
        [FromBody] BookAttachDraftImagesRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            BookAttachDraftImagesResultDto result =
                await _lookup.AttachDraftImagesAsync( request, cancellationToken );
            return Ok( result );
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning( ex, "attach-draft-images rejected" );
            return BadRequest( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            _logger.LogError( ex, "attach-draft-images failed" );
            return StatusCode(
                500,
                new { error = "Памылка загрузкі фота", details = ex.Message } );
        }
    }

}
