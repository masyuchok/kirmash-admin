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

        [HttpGet( "{id:int}/combined-details" )]
        public async Task<ActionResult<VatReportCombinedDetailsResponse>> GetCombinedDetails( int id )
        {
            try
            {
                VatReportCombinedDetailsResponse details = await _service.GetCombinedDetailsAsync( id );
                return Ok( details );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка атрымання злучанай справаздачы", details = ex.Message } );
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

        [HttpPatch( "rows/items/{itemId:int}" )]
        public async Task<IActionResult> UpdateRowItemVat( int itemId, [FromBody] VatReportRowItemUpdateRequest request )
        {
            try
            {
                await _service.UpdateRowItemVatAsync( itemId, request.VatRatePercent );
                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка захавання VAT па тавары", details = ex.Message } );
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

        [HttpPost( "{id:int}/expenses" )]
        public async Task<IActionResult> AddExpense( int id, [FromBody] VatReportExpenseCreateRequest request )
        {
            try
            {
                int expenseId = await _service.AddExpenseAsync( id, request );
                return Ok( new { id = expenseId } );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка дадання расходу", details = ex.Message } );
            }
        }

        [HttpDelete( "expenses/{expenseId:int}" )]
        public async Task<IActionResult> DeleteExpense( int expenseId )
        {
            try
            {
                await _service.DeleteExpenseAsync( expenseId );
                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка выдалення расходу", details = ex.Message } );
            }
        }

        [HttpPost( "rows/{rowId:int}/move-to-foreign" )]
        public async Task<IActionResult> MoveRowToForeign( int rowId, [FromBody] MoveVatReportRowToForeignRequest request )
        {
            try
            {
                await _service.MoveRowToForeignAsync( rowId, request.DeliveryName, request.DeliveryAddress );
                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка пераносу радка ў замежныя", details = ex.Message } );
            }
        }

        [HttpPost( "rows/{rowId:int}/invoice" )]
        [RequestSizeLimit( 10 * 1024 * 1024 )]
        public async Task<IActionResult> UploadInvoice( int rowId, [FromForm] VatReportInvoiceUploadRequest request )
        {
            try
            {
                IFormFile? file = request.File;
                if (file is null || file.Length == 0)
                {
                    return BadRequest( new { error = "Файл не абраны." } );
                }
                string contentType = string.IsNullOrWhiteSpace( file.ContentType ) ? "application/octet-stream" : file.ContentType;
                await using MemoryStream ms = new();
                await file.CopyToAsync( ms );
                await _service.UploadRowInvoiceAsync( rowId, file.FileName, contentType, ms.ToArray() );
                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка загрузкі фактуры", details = ex.Message } );
            }
        }

        [HttpPost( "expenses/{expenseId:int}/invoice" )]
        [RequestSizeLimit( 10 * 1024 * 1024 )]
        public async Task<IActionResult> UploadExpenseInvoice( int expenseId, [FromForm] VatReportInvoiceUploadRequest request )
        {
            try
            {
                IFormFile? file = request.File;
                if (file is null || file.Length == 0)
                {
                    return BadRequest( new { error = "Файл не абраны." } );
                }
                string contentType = string.IsNullOrWhiteSpace( file.ContentType ) ? "application/octet-stream" : file.ContentType;
                await using MemoryStream ms = new();
                await file.CopyToAsync( ms );
                await _service.UploadExpenseInvoiceAsync( expenseId, file.FileName, contentType, ms.ToArray() );
                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка загрузкі фактуры", details = ex.Message } );
            }
        }

        [HttpGet( "expenses/{expenseId:int}/invoice" )]
        public async Task<IActionResult> DownloadExpenseInvoice( int expenseId )
        {
            try
            {
                (string fileName, string contentType, byte[] data) = await _service.GetExpenseInvoiceAsync( expenseId );
                return File( data, contentType, fileName );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка загрузкі фактуры", details = ex.Message } );
            }
        }

        [HttpGet( "rows/{rowId:int}/invoice" )]
        public async Task<IActionResult> DownloadInvoice( int rowId )
        {
            try
            {
                (string fileName, string contentType, byte[] data) = await _service.GetRowInvoiceAsync( rowId );
                return File( data, contentType, fileName );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка загрузкі фактуры", details = ex.Message } );
            }
        }
    }
}
