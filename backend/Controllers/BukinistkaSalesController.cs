using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Authorize]
[Route( "bukinistka/sales" )]
public class BukinistkaSalesController : ControllerBase
{
    private readonly BukinistkaPosShopifySyncService _sync;

    public BukinistkaSalesController( BukinistkaPosShopifySyncService sync )
    {
        _sync = sync;
    }

    [HttpGet]
    public async Task<ActionResult<List<KirmaBukinistkaPosSaleDto>>> List(
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _sync.ListSalesAsync( cancellationToken ) );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }

    [HttpPost( "sync" )]
    public async Task<ActionResult<KirmaBukinistkaPosSyncResultDto>> Sync(
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _sync.SyncAsync( cancellationToken ) );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }
}
