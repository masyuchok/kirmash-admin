using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route( "Kirmashes" )]
[Authorize]
public sealed class KirmashesController : ControllerBase
{
    private readonly KirmashService _service;

    public KirmashesController( KirmashService service )
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<List<KirmashListItemDto>>> List( CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _service.ListAsync( cancellationToken ) );
        }
        catch (Exception ex)
        {
            return StatusCode( 500, new { error = "Памылка загрузкі кірмашоў", details = ex.Message } );
        }
    }

    [HttpGet( "{id:int}" )]
    public async Task<ActionResult<KirmashDetailDto>> Get( int id, CancellationToken cancellationToken )
    {
        try
        {
            KirmashDetailDto? row = await _service.GetAsync( id, cancellationToken );
            return row is null ? NotFound( new { error = "Кірмаш не знойдзены." } ) : Ok( row );
        }
        catch (Exception ex)
        {
            return StatusCode( 500, new { error = "Памылка загрузкі кірмаша", details = ex.Message } );
        }
    }

    [HttpPost]
    public async Task<ActionResult<KirmashDetailDto>> Create(
        [FromBody] KirmashUpsertRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            KirmashDetailDto created = await _service.CreateAsync( request, cancellationToken );
            return Ok( created );
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return StatusCode( 500, new { error = "Не ўдалося стварыць кірмаш", details = ex.Message } );
        }
    }

    [HttpPut( "{id:int}" )]
    public async Task<ActionResult<KirmashDetailDto>> Update(
        int id,
        [FromBody] KirmashUpsertRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            KirmashDetailDto? updated = await _service.UpdateAsync( id, request, cancellationToken );
            return updated is null
                ? NotFound( new { error = "Кірмаш не знойдзены." } )
                : Ok( updated );
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return StatusCode( 500, new { error = "Не ўдалося захаваць кірмаш", details = ex.Message } );
        }
    }

    [HttpDelete( "{id:int}" )]
    public async Task<IActionResult> Delete( int id, CancellationToken cancellationToken )
    {
        bool deleted = await _service.DeleteAsync( id, cancellationToken );
        return deleted ? Ok( new { ok = true } ) : NotFound( new { error = "Кірмаш не знойдзены." } );
    }

    [HttpGet( "{id:int}/price-tags" )]
    public async Task<ActionResult<List<KirmashPriceTagDto>>> GetPriceTags(
        int id,
        CancellationToken cancellationToken )
    {
        if (await _service.GetAsync( id, cancellationToken ) is null)
        {
            return NotFound( new { error = "Кірмаш не знойдзены." } );
        }

        return Ok( await _service.GetPriceTagsAsync( id, cancellationToken ) );
    }

    [HttpPost( "{id:int}/price-tags/generate" )]
    public async Task<ActionResult<List<KirmashPriceTagDto>>> GeneratePriceTags(
        int id,
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _service.GeneratePriceTagsAsync( id, cancellationToken ) );
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }
}
