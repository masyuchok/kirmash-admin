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

    public BooksController( BookLookupService lookup )
    {
        _lookup = lookup;
    }

    [HttpPost( "lookup-from-photo" )]
    [RequestSizeLimit( 32 * 1024 * 1024 )]
    [RequestFormLimits( MultipartBodyLengthLimit = 32 * 1024 * 1024 )]
    public async Task<ActionResult<BookLookupStepResultDto>> LookupFromPhoto(
        [FromForm] IFormFile cover,
        [FromForm] IFormFile? isbnPhoto,
        [FromForm] int? supplierId,
        CancellationToken cancellationToken )
    {
        try
        {
            if (cover is null)
            {
                return BadRequest( new { error = "Дадайце фота вокладкі." } );
            }

            BookLookupStepResultDto result = await _lookup.StartFromPhotoAsync(
                cover,
                isbnPhoto,
                supplierId,
                cancellationToken );
            return Ok( result );
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return StatusCode( 500, new { error = "Памылка пошуку па фота", details = ex.Message } );
        }
    }

    [HttpPost( "lookup-from-text" )]
    public async Task<ActionResult<BookLookupStepResultDto>> LookupFromText(
        [FromBody] BookLookupFromTextRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            BookLookupStepResultDto result = await _lookup.StartFromTextAsync( request, cancellationToken );
            return Ok( result );
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return StatusCode( 500, new { error = "Памылка пошуку па назве", details = ex.Message } );
        }
    }

    [HttpPost( "lookup/{sessionId}/confirm-ocr" )]
    public async Task<ActionResult<BookLookupStepResultDto>> ConfirmOcr(
        string sessionId,
        [FromBody] BookLookupConfirmOcrRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            BookLookupStepResultDto result = await _lookup.ConfirmOcrAndSearchAsync(
                sessionId,
                request,
                cancellationToken );
            return Ok( result );
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return StatusCode( 500, new { error = "Памылка запуску пошуку", details = ex.Message } );
        }
    }

    [HttpPost( "lookup/{sessionId}/next" )]
    public async Task<ActionResult<BookLookupStepResultDto>> LookupNext(
        string sessionId,
        CancellationToken cancellationToken )
    {
        try
        {
            BookLookupStepResultDto result = await _lookup.NextAsync( sessionId, cancellationToken );
            return Ok( result );
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return StatusCode( 500, new { error = "Памылка наступнага пошуку", details = ex.Message } );
        }
    }

    [HttpPost( "lookup/{sessionId}/search-by-photo" )]
    public async Task<ActionResult<BookLookupStepResultDto>> SearchByPhoto(
        string sessionId,
        CancellationToken cancellationToken )
    {
        try
        {
            BookLookupStepResultDto result = await _lookup.SearchByPhotoAsync(
                sessionId,
                cancellationToken );
            return Ok( result );
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return StatusCode( 500, new { error = "Памылка пошуку па фота", details = ex.Message } );
        }
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

    [HttpPost( "create-from-lookup" )]
    public async Task<ActionResult<BookCreateFromLookupResultDto>> CreateFromLookup(
        [FromBody] BookCreateFromLookupRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            BookCreateFromLookupResultDto result =
                await _lookup.CreateShopifyProductAsync( request, cancellationToken );
            return Ok( result );
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return StatusCode( 500, new { error = "Памылка стварэння тавару", details = ex.Message } );
        }
    }
}
