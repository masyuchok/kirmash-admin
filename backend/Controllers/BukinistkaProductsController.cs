using backend.Models;
using backend.Services.Odoo;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route( "bukinistka/products" )]
public class BukinistkaProductsController : ControllerBase
{
    private readonly OdooProductService _products;

    public BukinistkaProductsController( OdooProductService products )
    {
        _products = products;
    }

    [HttpGet]
    public async Task<ActionResult<OdooProductListResponse>> List( CancellationToken cancellationToken )
    {
        try
        {
            OdooProductListResponse response = await _products.ListProductsAsync( Request, cancellationToken );
            return Ok( response );
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
