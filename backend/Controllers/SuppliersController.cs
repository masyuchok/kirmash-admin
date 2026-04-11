using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers
{
    [ApiController]
    [Route( "[controller]" )]
    [Authorize]
    public class SuppliersController : Controller
    {
        private readonly IConfiguration _config;
        private readonly SupplierService _service;

        public SuppliersController(IConfiguration config, SupplierService service )
        {
            _config = config;
            _service = service;
        }

        [HttpPost( "add" )]
        public async Task<IActionResult> Add( [FromBody] Supplier supplier )
        {
            try
            {
                int? id = await _service.AddSupplier( supplier );

                return Ok( id );
            }
            catch ( InvalidOperationException ex )
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch ( Exception ex )
            {
                return StatusCode( 500, new { error = "Памылка дадавання пастаўшчыка", details = ex.Message } );
            }
        }

        [HttpGet]
        public async Task<ActionResult<List<Supplier>>> GetAll( )
        {
            try
            {
                List<Supplier> suppliers = await _service.GetAllAsync( );

                return Ok( suppliers );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка атрымання спіса пастаўшчыкоў", details = ex.Message } );
            }
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<Supplier>> Get(int id)
        {
            try
            {
                Supplier supplier = await _service.GetSupplier(id);

                return Ok(supplier);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Памылка атрымання пастаўшчыка", details = ex.Message });
            }
        }

        [HttpPatch("{id}")]
        public async Task<IActionResult> UpdateSupplier(int id, [FromBody] Supplier supplier)
        {
            try
            {
                await _service.UpdateSupplier(id, supplier);

                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Памылка абнаўлення пастаўшчыка", details = ex.Message });
            }
        }
    }
}
