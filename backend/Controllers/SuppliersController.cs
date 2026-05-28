using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers
{
    [ApiController]
    [Route( "[controller]" )]
    [Authorize]
    public class SuppliersController : Controller
    {
        private readonly IConfiguration _config;
        private readonly SupplierService _service;

        private readonly SupplierInventoryService _inventoryService;
        private readonly InventorySalesCacheService _salesCacheService;

        public SuppliersController(
            IConfiguration config,
            SupplierService service,
            SupplierInventoryService inventoryService,
            InventorySalesCacheService salesCacheService )
        {
            _config = config;
            _service = service;
            _inventoryService = inventoryService;
            _salesCacheService = salesCacheService;
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

        [HttpGet( "inventory" )]
        public async Task<ActionResult<SupplierInventoryResponse>> GetInventory(
            [FromQuery] int? supplierId,
            [FromQuery] bool refresh = false )
        {
            try
            {
                SupplierInventoryResponse response = await _inventoryService.GetInventoryAsync( supplierId, refresh );
                return Ok( response );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка атрымання інвентарызацыі", details = ex.Message } );
            }
        }

        [HttpPost( "inventory/refresh" )]
        public async Task<IActionResult> RefreshInventorySales()
        {
            try
            {
                DateTime? syncedAt = await _salesCacheService.EnsureFreshAsync( force: true );
                return Ok( new { salesSyncedAtUtc = syncedAt } );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка абнаўлення продажаў", details = ex.Message } );
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
