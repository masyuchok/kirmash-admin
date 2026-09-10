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

    /// <summary>Bukinistka creates an offer for Kirma.</summary>
    [HttpPost( "from-bukinistka" )]
    public async Task<ActionResult<KirmaBukinistkaOfferDto>> CreateFromBukinistka(
        [FromBody] KirmaBukinistkaOfferCreateFromBukinistkaRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _offers.CreateFromBukinistkaAsync( request, Request, cancellationToken ) );
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

    /// <summary>Kirma lists offers received from Bukinistka.</summary>
    [Authorize]
    [HttpGet( "received" )]
    public async Task<ActionResult<List<KirmaBukinistkaOfferDto>>> ListReceived()
    {
        try
        {
            return Ok( await _offers.ListReceivedForKirmaAsync() );
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

    /// <summary>Kirma: count of pending (unprocessed) offers from Bukinistka.</summary>
    [Authorize]
    [HttpGet( "received/pending-count" )]
    public async Task<ActionResult<object>> ReceivedPendingCount()
    {
        try
        {
            int count = await _offers.CountPendingForKirmaAsync();
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

    /// <summary>Bukinistka lists offers it sent to Kirma.</summary>
    [HttpGet( "sent-by-bukinistka" )]
    public async Task<ActionResult<List<KirmaBukinistkaOfferDto>>> ListSentByBukinistka()
    {
        try
        {
            return Ok( await _offers.ListSentByBukinistkaAsync( Request ) );
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

    /// <summary>Bukinistka: whether an Odoo product can be proposed to Kirma.</summary>
    [HttpGet( "eligibility/bukinistka/{odooProductId:int}" )]
    public async Task<ActionResult<BukinistkaProposeEligibilityDto>> BukEligibility(
        int odooProductId,
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _offers.GetBukProposeEligibilityAsync( odooProductId, cancellationToken ) );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }

    /// <summary>Kirma: whether a Shopify product can be proposed to Bukinistka.</summary>
    [Authorize]
    [HttpGet( "eligibility/kirma" )]
    public async Task<ActionResult<BukinistkaProposeEligibilityDto>> KirmaEligibility(
        [FromQuery] string shopifyProductId,
        [FromQuery] string? shopifyVariantId,
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _offers.GetKirmaProposeEligibilityAsync(
                shopifyProductId,
                shopifyVariantId,
                cancellationToken ) );
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

    /// <summary>Kirma updates a pending or accepted (still active) sent offer.</summary>
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

    /// <summary>Bukinistka updates a pending or accepted (still active) offer it sent to Kirma.</summary>
    [HttpPut( "sent-by-bukinistka/{id:int}" )]
    public async Task<ActionResult<KirmaBukinistkaOfferDto>> UpdateSentByBukinistka(
        int id,
        [FromBody] KirmaBukinistkaOfferUpdateRequest request )
    {
        try
        {
            return Ok( await _offers.UpdateSentByBukinistkaAsync( id, request, Request ) );
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

    /// <summary>Kirma applies Bukinistka's updated gross cost to Shopify InventoryItem.cost.</summary>
    [Authorize]
    [HttpPost( "{id:int}/apply-price-change" )]
    public async Task<ActionResult<KirmaBukinistkaOfferDto>> ApplyPriceChange(
        int id,
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _offers.ApplyPriceChangeByKirmaAsync( id, cancellationToken ) );
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

    /// <summary>Kirma sets Shopify variant sale price for an accepted offer from Bukinistka.</summary>
    [Authorize]
    [HttpPost( "{id:int}/shopify-sale-price" )]
    public async Task<ActionResult<KirmaBukinistkaOfferDto>> UpdateShopifySalePrice(
        int id,
        [FromBody] KirmaBukinistkaOfferShopifySalePriceRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _offers.UpdateShopifySalePriceByKirmaAsync(
                id,
                request,
                cancellationToken ) );
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

    /// <summary>Bukinistka applies Kirma's updated gross cost to Odoo standard_price.</summary>
    [HttpPost( "{id:int}/apply-price-change-by-bukinistka" )]
    public async Task<ActionResult<KirmaBukinistkaOfferDto>> ApplyPriceChangeByBukinistka(
        int id,
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _offers.ApplyPriceChangeByBukinistkaAsync( id, Request, cancellationToken ) );
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

    /// <summary>Bukinistka cancels a pending/rejected offer it sent to Kirma.</summary>
    [HttpDelete( "sent-by-bukinistka/{id:int}" )]
    public async Task<IActionResult> CancelSentByBukinistka( int id )
    {
        try
        {
            await _offers.CancelSentByBukinistkaAsync( id, Request );
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

    /// <summary>Kirma rejects a pending offer from Bukinistka.</summary>
    [Authorize]
    [HttpPost( "{id:int}/reject-by-kirma" )]
    public async Task<IActionResult> RejectByKirma( int id )
    {
        try
        {
            await _offers.RejectForKirmaAsync( id );
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

    /// <summary>Kirma accepts a pending offer from Bukinistka and links a Shopify product (+qty).</summary>
    [Authorize]
    [HttpPost( "{id:int}/accept-by-kirma" )]
    public async Task<ActionResult<KirmaBukinistkaOfferAcceptByKirmaResultDto>> AcceptByKirma(
        int id,
        [FromBody] KirmaBukinistkaOfferAcceptByKirmaRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _offers.AcceptForKirmaAsync( id, request, Request, cancellationToken ) );
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

    /// <summary>Kirma: Odoo preview before creating a new Shopify product for a received offer.</summary>
    [Authorize]
    [HttpGet( "{id:int}/create-shopify-product-preview" )]
    public async Task<ActionResult<KirmaBukinistkaOfferCreateShopifyProductPreviewDto>> CreateShopifyProductPreview(
        int id,
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _offers.GetCreateShopifyProductPreviewAsync( id, Request, cancellationToken ) );
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

    /// <summary>Kirma: create a new Shopify product card from a received Bukinistka offer.</summary>
    [Authorize]
    [HttpPost( "{id:int}/create-shopify-product" )]
    public async Task<ActionResult<KirmaBukinistkaOfferCreateShopifyProductResultDto>> CreateShopifyProduct(
        int id,
        [FromBody] KirmaBukinistkaOfferCreateShopifyProductRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _offers.CreateShopifyProductForReceivedOfferAsync(
                id,
                request,
                Request,
                cancellationToken ) );
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

    /// <summary>Bukinistka: Shopify preview before creating a new Odoo product for an offer.</summary>
    [HttpGet( "{id:int}/create-product-preview" )]
    public async Task<ActionResult<KirmaBukinistkaOfferCreateProductPreviewDto>> CreateProductPreview(
        int id,
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _offers.GetCreateProductPreviewAsync( id, Request, cancellationToken ) );
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

    /// <summary>Bukinistka: create a new Odoo product card from the Kirma offer.</summary>
    [HttpPost( "{id:int}/create-product" )]
    public async Task<ActionResult<KirmaBukinistkaOfferCreateProductResultDto>> CreateProduct(
        int id,
        [FromBody] KirmaBukinistkaOfferCreateProductRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _offers.CreateOdooProductForOfferAsync( id, request, Request, cancellationToken ) );
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized( new { error = ex.Message } );
        }
        catch (Exception ex) when (ex.Message.StartsWith( "barcode_conflict:", StringComparison.OrdinalIgnoreCase ))
        {
            string message = ex.Message["barcode_conflict:".Length..].Trim();
            return BadRequest( new { error = message, code = "barcode_conflict" } );
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

    /// <summary>Bukinistka: current Open/Failed receipt draft, or null.</summary>
    [HttpGet( "receipt-draft" )]
    public async Task<IActionResult> GetReceiptDraft( CancellationToken cancellationToken )
    {
        try
        {
            KirmaBukinistkaReceiptDraftDto? draft =
                await _offers.GetActiveReceiptDraftForBukinistkaAsync( Request, cancellationToken );
            // Explicit JSON null — Ok(null) can become 204 with empty body.
            if (draft is null)
            {
                return Content( "null", "application/json" );
            }

            return Ok( draft );
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

    /// <summary>Bukinistka: upsert the active receipt draft (full line replace).</summary>
    [HttpPut( "receipt-draft" )]
    public async Task<ActionResult<KirmaBukinistkaReceiptDraftDto>> UpsertReceiptDraft(
        [FromBody] KirmaBukinistkaReceiptDraftUpsertRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _offers.UpsertReceiptDraftForBukinistkaAsync( request, Request, cancellationToken ) );
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

    /// <summary>Bukinistka: discard the active Open/Failed receipt draft.</summary>
    [HttpDelete( "receipt-draft" )]
    public async Task<IActionResult> DeleteReceiptDraft( CancellationToken cancellationToken )
    {
        try
        {
            await _offers.DeleteActiveReceiptDraftForBukinistkaAsync( Request, cancellationToken );
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
}
