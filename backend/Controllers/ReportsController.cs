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

        [HttpGet( "{id:int}/source-orders" )]
        public async Task<ActionResult<List<VatReportSourceOrderOption>>> GetSourceOrders( int id )
        {
            try
            {
                List<VatReportSourceOrderOption> options = await _service.GetSourceOrderOptionsAsync( id );
                return Ok( options );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка атрымання замоў за месяц", details = ex.Message } );
            }
        }

        [HttpPost( "{id:int}/regenerate" )]
        public async Task<ActionResult<VatReportListItem>> Regenerate( int id )
        {
            try
            {
                VatReportListItem updated = await _service.RegenerateAsync( id );
                return Ok( updated );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка перегенерацыі справаздачы", details = ex.Message } );
            }
        }

        [HttpPatch( "rows/{rowId:int}" )]
        public async Task<IActionResult> UpdateRow( int rowId, [FromBody] VatReportRowUpdateRequest request )
        {
            try
            {
                await _service.UpdateRowAsync(
                    rowId,
                    request.VatRatePercent,
                    request.GrossAmount,
                    request.VatAmount,
                    request.NetAmount,
                    request.ShippingGrossAmount
                );
                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка захавання радка справаздачы", details = ex.Message } );
            }
        }

        [HttpPost( "{id:int}/rows" )]
        public async Task<IActionResult> AddRow( int id, [FromBody] VatReportRowCreateRequest request )
        {
            try
            {
                await _service.AddRowAsync( id, request );
                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка дадання радка справаздачы", details = ex.Message } );
            }
        }

        [HttpDelete( "rows/{rowId:int}" )]
        public async Task<IActionResult> DeleteRow( int rowId )
        {
            try
            {
                await _service.DeleteRowAsync( rowId );
                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка выдалення радка справаздачы", details = ex.Message } );
            }
        }
    }
}
