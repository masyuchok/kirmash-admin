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
    }
}
