using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers
{
    [ApiController]
    [Route( "[controller]" )]
    [Authorize]
    public class FinancesController : Controller
    {
        private readonly FinanceService _finance;

        public FinancesController( FinanceService finance )
        {
            _finance = finance;
        }

        [HttpGet( "persons" )]
        public async Task<ActionResult<List<FinancePersonDto>>> ListPersons() =>
            Ok( await _finance.ListPersonsAsync() );

        [HttpPost( "persons" )]
        public async Task<ActionResult<FinancePersonDto>> CreatePerson( [FromBody] FinancePersonCreateRequest request )
        {
            string name = (request.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( name ))
            {
                return BadRequest( new { error = "Імя абавязковае." } );
            }

            return Ok( await _finance.CreatePersonAsync( name ) );
        }

        [HttpPut( "persons/{id:int}" )]
        public async Task<ActionResult<FinancePersonDto>> UpdatePerson( int id, [FromBody] FinancePersonUpdateRequest request )
        {
            string name = (request.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( name ))
            {
                return BadRequest( new { error = "Імя абавязковае." } );
            }

            FinancePersonDto? row = await _finance.UpdatePersonAsync( id, name );
            if (row is null)
            {
                return NotFound( new { error = "Асоба не знойдзена." } );
            }

            return Ok( row );
        }

        [HttpDelete( "persons/{id:int}" )]
        public async Task<IActionResult> DeletePerson( int id )
        {
            bool deleted = await _finance.DeletePersonAsync( id );
            if (!deleted)
            {
                return NotFound( new { error = "Асоба не знойдзена." } );
            }

            return Ok();
        }

        [HttpGet( "persons/{personId:int}/overview" )]
        public async Task<ActionResult<FinancePersonOverviewDto>> GetOverview( int personId )
        {
            FinancePersonOverviewDto? overview = await _finance.GetPersonOverviewAsync( personId );
            if (overview is null)
            {
                return NotFound( new { error = "Асоба не знойдзена." } );
            }

            return Ok( overview );
        }

        [HttpPost( "movements" )]
        public async Task<ActionResult<FinanceMovementDto>> CreateMovement( [FromBody] FinanceMovementCreateRequest request )
        {
            FinanceMovementDto? row = await _finance.CreateMovementAsync( request );
            if (row is null)
            {
                return BadRequest( new { error = "Некарэктныя дадзеныя руху." } );
            }

            return Ok( row );
        }

        [HttpPut( "movements/{id:int}" )]
        public async Task<ActionResult<FinanceMovementDto>> UpdateMovement( int id, [FromBody] FinanceMovementUpdateRequest request )
        {
            FinanceMovementDto? row = await _finance.UpdateMovementAsync( id, request );
            if (row is null)
            {
                return BadRequest( new { error = "Некарэктныя дадзеныя руху." } );
            }

            return Ok( row );
        }

        [HttpDelete( "movements/{id:int}" )]
        public async Task<IActionResult> DeleteMovement( int id )
        {
            bool deleted = await _finance.DeleteMovementAsync( id );
            if (!deleted)
            {
                return NotFound( new { error = "Рух не знойдзены." } );
            }

            return Ok();
        }

        [HttpPost( "recurring" )]
        public async Task<ActionResult<FinanceRecurringExpenseDto>> CreateRecurring( [FromBody] FinanceRecurringExpenseCreateRequest request )
        {
            FinanceRecurringExpenseDto? row = await _finance.CreateRecurringAsync( request );
            if (row is null)
            {
                return BadRequest( new { error = "Некарэктныя дадзеныя рэгулярнага расходу." } );
            }

            return Ok( row );
        }

        [HttpPut( "recurring/{id:int}" )]
        public async Task<ActionResult<FinanceRecurringExpenseDto>> UpdateRecurring( int id, [FromBody] FinanceRecurringExpenseUpdateRequest request )
        {
            FinanceRecurringExpenseDto? row = await _finance.UpdateRecurringAsync( id, request );
            if (row is null)
            {
                return BadRequest( new { error = "Некарэктныя дадзеныя рэгулярнага расходу." } );
            }

            return Ok( row );
        }

        [HttpDelete( "recurring/{id:int}" )]
        public async Task<IActionResult> DeleteRecurring( int id )
        {
            bool deleted = await _finance.DeleteRecurringAsync( id );
            if (!deleted)
            {
                return NotFound( new { error = "Рэгулярны расход не знойдзены." } );
            }

            return Ok();
        }
    }
}
