using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route( "bukinistka/offers" )]
public class BukinistkaOffersController : ControllerBase
{
    private readonly KirmaBukinistkaOfferService _offers;

    public BukinistkaOffersController( KirmaBukinistkaOfferService offers )
    {
        _offers = offers;
    }

    /// <summary>Kirma creates an offer for Bukinistka.</summary>
    [Authorize]
    [HttpPost]
    public async Task<ActionResult<KirmaBukinistkaOfferDto>> Create(
        [FromBody] KirmaBukinistkaOfferCreateRequest request )
    {
        try
        {
            return Ok( await _offers.CreateAsync( request ) );
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }

    /// <summary>Bukinistka lists offers from Kirma.</summary>
    [HttpGet]
    public async Task<ActionResult<List<KirmaBukinistkaOfferDto>>> List()
    {
        try
        {
            return Ok( await _offers.ListForBukinistkaAsync( Request ) );
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }

    /// <summary>Bukinistka: count of pending (unprocessed) offers from Kirma.</summary>
    [HttpGet( "pending-count" )]
    public async Task<ActionResult<object>> PendingCount()
    {
        try
        {
            int count = await _offers.CountPendingForBukinistkaAsync( Request );
            return Ok( new { count } );
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }

    /// <summary>Kirma lists offers it sent to Bukinistka.</summary>
    [Authorize]
    [HttpGet( "sent" )]
    public async Task<ActionResult<List<KirmaBukinistkaOfferDto>>> ListSent()
    {
        try
        {
            return Ok( await _offers.ListSentForKirmaAsync() );
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }

    /// <summary>Kirma updates a pending sent offer.</summary>
    [Authorize]
    [HttpPut( "{id:int}" )]
    public async Task<ActionResult<KirmaBukinistkaOfferDto>> Update(
        int id,
        [FromBody] KirmaBukinistkaOfferUpdateRequest request )
    {
        try
        {
            return Ok( await _offers.UpdateSentAsync( id, request ) );
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }

    /// <summary>Kirma cancels (deletes) a pending sent offer, or deletes a rejected one.</summary>
    [Authorize]
    [HttpDelete( "{id:int}" )]
    public async Task<IActionResult> Cancel( int id )
    {
        try
        {
            await _offers.DeleteSentAsync( id );
            return Ok( new { success = true } );
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }

    /// <summary>Bukinistka rejects a pending offer from Kirma.</summary>
    [HttpPost( "{id:int}/reject" )]
    public async Task<IActionResult> Reject( int id )
    {
        try
        {
            await _offers.RejectForBukinistkaAsync( id, Request );
            return Ok( new { success = true } );
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }

    /// <summary>Bukinistka accepts a pending offer and links it to an existing Odoo product.</summary>
    [HttpPost( "{id:int}/accept" )]
    public async Task<ActionResult<KirmaBukinistkaOfferDto>> Accept(
        int id,
        [FromBody] KirmaBukinistkaOfferAcceptRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _offers.AcceptForBukinistkaAsync( id, request, Request, cancellationToken ) );
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }

    /// <summary>
    /// Bukinistka saves a batch receipt: creates Odoo Przyjęcia for Kirma.sh and accepts linked offers.
    /// </summary>
    [HttpPost( "receipt" )]
    public async Task<ActionResult<KirmaBukinistkaOfferReceiptResultDto>> SaveReceipt(
        [FromBody] KirmaBukinistkaOfferReceiptRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _offers.SaveReceiptForBukinistkaAsync( request, Request, cancellationToken ) );
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }
}
