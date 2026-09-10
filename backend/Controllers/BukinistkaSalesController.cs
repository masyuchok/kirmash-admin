using backend.Models;
using backend.Services;
using backend.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Authorize]
[Route( "bukinistka/sales" )]
public class BukinistkaSalesController : ControllerBase
{
    private readonly BukinistkaPosShopifySyncService _posSync;
    private readonly BukinistkaShopifyOdooDeliverySyncService _deliverySync;
    private readonly BukinistkaPosInvoiceService _invoice;
    private readonly IConfiguration _config;

    public BukinistkaSalesController(
        BukinistkaPosShopifySyncService posSync,
        BukinistkaShopifyOdooDeliverySyncService deliverySync,
        BukinistkaPosInvoiceService invoice,
        IConfiguration config )
    {
        _posSync = posSync;
        _deliverySync = deliverySync;
        _invoice = invoice;
        _config = config;
    }

    /// <summary>Bukinistka portal: POS sales of accepted Kirma consignment stock.</summary>
    [AllowAnonymous]
    [HttpGet( "portal/received" )]
    public async Task<ActionResult<List<KirmaBukinistkaPosSaleDto>>> ListPortalReceived(
        CancellationToken cancellationToken )
    {
        try
        {
            RequireBukinistkaSession();
            return Ok( await _posSync.ListSalesAsync( cancellationToken ) );
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

    /// <summary>Bukinistka portal: Kirma Shopify sales of offers sent by Bukinistka.</summary>
    [AllowAnonymous]
    [HttpGet( "portal/sent" )]
    public async Task<ActionResult<List<KirmaBukinistkaShopifyDeliverySaleDto>>> ListPortalSent(
        CancellationToken cancellationToken )
    {
        try
        {
            RequireBukinistkaSession();
            return Ok( await _deliverySync.ListSentShopifySalesAsync( cancellationToken ) );
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

    /// <summary>Bukinistka portal: sync POS + Shopify delivery sales.</summary>
    [AllowAnonymous]
    [HttpPost( "portal/sync" )]
    public async Task<ActionResult<object>> SyncPortal( CancellationToken cancellationToken )
    {
        try
        {
            RequireBukinistkaSession();
            KirmaBukinistkaPosSyncResultDto pos =
                await _posSync.SyncAsync( cancellationToken );
            KirmaBukinistkaShopifyDeliverySyncResultDto delivery =
                await _deliverySync.SyncAsync( cancellationToken );

            return Ok( new
            {
                pos.Skipped,
                pos.SkipReason,
                pos.OrdersScanned,
                pos.LinesProcessed,
                pos.UnitsSynced,
                pos.SyncedAtUtc,
                deliverySkipped = delivery.Skipped,
                deliverySkipReason = delivery.SkipReason,
                deliveryOrdersScanned = delivery.OrdersScanned,
                deliveryPickingsCreated = delivery.PickingsCreated,
                deliveryUnitsSynced = delivery.UnitsSynced,
            } );
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

    private void RequireBukinistkaSession()
    {
        if (BukinistkaJwtAuthentication.TryValidateCookie( Request, _config ) is null)
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }
    }

    [HttpGet]
    public async Task<ActionResult<List<KirmaBukinistkaPosSaleDto>>> List(
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _posSync.ListSalesAsync( cancellationToken ) );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }

    [HttpPost( "sync" )]
    public async Task<ActionResult<object>> Sync( CancellationToken cancellationToken )
    {
        try
        {
            KirmaBukinistkaPosSyncResultDto pos =
                await _posSync.SyncAsync( cancellationToken );
            KirmaBukinistkaShopifyDeliverySyncResultDto delivery =
                await _deliverySync.SyncAsync( cancellationToken );

            return Ok( new
            {
                pos.Skipped,
                pos.SkipReason,
                pos.OrdersScanned,
                pos.LinesProcessed,
                pos.UnitsSynced,
                pos.SyncedAtUtc,
                deliverySkipped = delivery.Skipped,
                deliverySkipReason = delivery.SkipReason,
                deliveryOrdersScanned = delivery.OrdersScanned,
                deliveryPickingsCreated = delivery.PickingsCreated,
                deliveryUnitsSynced = delivery.UnitsSynced,
            } );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }

    /// <summary>
    /// Issue a Poland VAT invoice for selected Bukinistka POS sales (gross from offers).
    /// </summary>
    [HttpPost( "invoice" )]
    public async Task<ActionResult<KirmaBukinistkaPosInvoiceResultDto>> Invoice(
        [FromBody] KirmaBukinistkaPosInvoiceRequest request,
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _invoice.InvoiceSalesAsync( request, cancellationToken ) );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }
}
