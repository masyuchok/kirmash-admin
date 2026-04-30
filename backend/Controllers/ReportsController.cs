using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers
{
    [ApiController]
    [Route( "[controller]" )]
    [Authorize]
    public class ReportsController : Controller
    {
        private readonly VatReportService _service;

        public ReportsController( VatReportService service )
        {
            _service = service;
        }

        [HttpGet]
        public async Task<ActionResult<List<VatReportListItem>>> GetAll()
        {
            try
            {
                List<VatReportListItem> reports = await _service.GetAllAsync();
                return Ok( reports );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка атрымання справаздач", details = ex.Message } );
            }
        }

        [HttpPost( "generate" )]
        public async Task<ActionResult<VatReportListItem>> Generate( [FromBody] VatReportGenerateRequest request )
        {
            try
            {
                VatReportListItem created = await _service.GenerateAsync(
                    request.PeriodYear,
                    request.PeriodMonth,
                    request.Type
                );
                return Ok( created );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка генерацыі справаздачы", details = ex.Message } );
            }
        }

        [HttpGet( "{id:int}" )]
        public async Task<ActionResult<VatReportDetailsResponse>> GetDetails( int id )
        {
            try
            {
                VatReportDetailsResponse details = await _service.GetDetailsAsync( id );
                return Ok( details );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка атрымання дэталяў справаздачы", details = ex.Message } );
            }
        }
    }
}
