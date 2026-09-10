using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route( "bukinistka/inventory" )]
public class BukinistkaInventoryController : ControllerBase
{
    private readonly BukinistkaInventoryService _inventory;

    public BukinistkaInventoryController( BukinistkaInventoryService inventory )
    {
        _inventory = inventory;
    }

    [HttpGet]
    public async Task<ActionResult<BukinistkaInventoryResponse>> List(
        CancellationToken cancellationToken )
    {
        try
        {
            return Ok( await _inventory.ListAsync( Request, cancellationToken ) );
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
