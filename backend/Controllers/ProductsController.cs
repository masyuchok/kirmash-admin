using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers
{
    [ApiController]
    [Route( "[controller]" )]
    [Authorize]
    public class ProductsController : Controller
    {
        private readonly ProductService _service;

        public ProductsController( ProductService service )
        {
            _service = service;
        }

        [HttpGet]
        public async Task<ActionResult<List<ProductWithSuppliersListItem>>> GetAll()
        {
            try
            {
                List<ProductWithSuppliersListItem> products = await _service.GetProductsWithSuppliersAsync();
                return Ok( products );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка атрымання прадуктаў", details = ex.Message } );
            }
        }
    }
}
