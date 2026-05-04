using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers
{
    [ApiController]
    [Route("[controller]")]
    [Authorize]
    public class SupplyController : Controller
    {
        private readonly SupplyService _service;

        public SupplyController(SupplyService service)
        {
            _service = service;
        }

        [HttpGet]
        public async Task<ActionResult<List<SupplyListItem>>> GetAll()
        {
            try
            {
                List<SupplyListItem> supplies = await _service.GetSupplyList();

                return Ok(supplies);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Памылка атрымання спіса паставак", details = ex.Message });
            }
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<SupplyDetailsResponse>> GetOne( int id )
        {
            try
            {
                SupplyDetailsResponse? supply = await _service.GetSupplyDetailsAsync( id );
                if (supply == null)
                {
                    return NotFound( new { error = "Пастаўка не знойдзена" } );
                }
                return Ok( supply );
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Памылка атрымання пастаўкі", details = ex.Message });
            }
        }

        [HttpPost("save")]
        public async Task<ActionResult<object>> Save( [FromBody] SupplySaveRequest request )
        {
            try
            {
                SupplySaveResult result = await _service.SaveSupplyAsync( request );
                return Ok( new
                {
                    id = result.SupplyId,
                    warning = result.Warning,
                    inventoryUpdates = result.InventoryUpdates
                } );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Памылка захавання пастаўкі", details = ex.Message });
            }
        }

        [HttpDelete("{id:int}")]
        public async Task<ActionResult<object>> Delete( int id )
        {
            try
            {
                bool deleted = await _service.DeleteSupplyAsync( id );
                if (!deleted)
                {
                    return NotFound( new { error = "Пастаўка не знойдзена" } );
                }

                return Ok( new { ok = true } );
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Памылка выдалення пастаўкі", details = ex.Message });
            }
        }
    }
}
