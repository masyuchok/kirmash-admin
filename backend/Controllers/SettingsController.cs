using System.Net.Mail;
using System.Linq;
using backend.Data;
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
    public class SettingsController : Controller
    {
        private readonly AppDbContext _db;
        private readonly VatReportFinanceSyncService _vatFinanceSync;

        public SettingsController( AppDbContext db, VatReportFinanceSyncService vatFinanceSync )
        {
            _db = db;
            _vatFinanceSync = vatFinanceSync;
        }

        [HttpGet( "invoice" )]
        public async Task<ActionResult<InvoiceSettingsDto>> GetInvoice()
        {
            InvoiceSettings? settings = await _db.InvoiceSettings.AsNoTracking().FirstOrDefaultAsync();
            if (settings is null)
            {
                return Ok( new InvoiceSettingsDto { Currency = "PLN" } );
            }

            return Ok( new InvoiceSettingsDto
            {
                CompanyName = settings.CompanyName,
                Address = settings.Address,
                Email = settings.Email,
                Website = settings.Website,
                Nip = settings.Nip,
                Currency = string.IsNullOrWhiteSpace( settings.Currency ) ? "PLN" : settings.Currency
            } );
        }

        [HttpPut( "invoice" )]
        public async Task<IActionResult> SaveInvoice( [FromBody] InvoiceSettingsDto request )
        {
            string companyName = (request.CompanyName ?? string.Empty).Trim();
            string address = (request.Address ?? string.Empty).Trim();
            string email = (request.Email ?? string.Empty).Trim();
            string website = (request.Website ?? string.Empty).Trim();
            string nip = (request.Nip ?? string.Empty).Trim();
            string currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace( companyName ) ||
                string.IsNullOrWhiteSpace( address ) ||
                string.IsNullOrWhiteSpace( email ) ||
                string.IsNullOrWhiteSpace( website ) ||
                string.IsNullOrWhiteSpace( nip ) ||
                string.IsNullOrWhiteSpace( currency ))
            {
                return BadRequest( new { error = "Усе палі абавязковыя." } );
            }

            if (!IsValidEmail( email ))
            {
                return BadRequest( new { error = "Некарэктны e-mail." } );
            }

            if (!IsValidWebsite( website ))
            {
                return BadRequest( new { error = "Некарэктная спасылка." } );
            }

            if (!IsValidNip( nip ))
            {
                return BadRequest( new { error = "NIP павінен утрымліваць роўна 10 лічбаў." } );
            }
            if (!IsValidCurrency( currency ))
            {
                return BadRequest( new { error = "Валюта павінна ўтрымліваць 3 літары (напрыклад, PLN)." } );
            }

            InvoiceSettings? settings = await _db.InvoiceSettings.FirstOrDefaultAsync();
            if (settings is null)
            {
                settings = new InvoiceSettings();
                _db.InvoiceSettings.Add( settings );
            }

            settings.CompanyName = companyName;
            settings.Address = address;
            settings.Email = email;
            settings.Website = website;
            settings.Nip = nip;
            settings.Currency = currency;
            settings.UpdatedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            return Ok();
        }

        [HttpGet( "invoice-expense-types" )]
        public async Task<ActionResult<List<ExpenseInvoiceTypeDto>>> GetExpenseInvoiceTypes()
        {
            await ExpenseInvoiceTypeSeeder.EnsureDefaultAsync( _db );
            List<ExpenseInvoiceTypeDto> rows = await _db.ExpenseInvoiceTypes
                .AsNoTracking()
                .OrderBy( x => x.IsSystem ? 0 : 1 )
                .ThenBy( x => x.Name )
                .Select( x => new ExpenseInvoiceTypeDto
                {
                    Id = x.Id,
                    Name = x.Name,
                    IsSystem = x.IsSystem
                } )
                .ToListAsync();
            return Ok( rows );
        }

        [HttpPost( "invoice-expense-types" )]
        public async Task<ActionResult<ExpenseInvoiceTypeDto>> CreateExpenseInvoiceType( [FromBody] ExpenseInvoiceTypeCreateRequest request )
        {
            string name = (request.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( name ))
            {
                return BadRequest( new { error = "Назва абавязковая." } );
            }

            await ExpenseInvoiceTypeSeeder.EnsureDefaultAsync( _db );
            bool exists = await _db.ExpenseInvoiceTypes.AnyAsync( x => x.Name.ToLower() == name.ToLower() );
            if (exists)
            {
                return BadRequest( new { error = "Такі тып ужо існуе." } );
            }

            ExpenseInvoiceType row = new()
            {
                Name = name,
                IsSystem = false,
                CreatedAtUtc = DateTime.UtcNow
            };
            _db.ExpenseInvoiceTypes.Add( row );
            await _db.SaveChangesAsync();
            return Ok( new ExpenseInvoiceTypeDto { Id = row.Id, Name = row.Name, IsSystem = row.IsSystem } );
        }

        [HttpPut( "invoice-expense-types/{id:int}" )]
        public async Task<ActionResult<ExpenseInvoiceTypeDto>> UpdateExpenseInvoiceType( int id, [FromBody] ExpenseInvoiceTypeUpdateRequest request )
        {
            string name = (request.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( name ))
            {
                return BadRequest( new { error = "Назва абавязковая." } );
            }

            await ExpenseInvoiceTypeSeeder.EnsureDefaultAsync( _db );
            ExpenseInvoiceType? row = await _db.ExpenseInvoiceTypes.FirstOrDefaultAsync( x => x.Id == id );
            if (row is null)
            {
                return NotFound( new { error = "Тып не знойдзены." } );
            }
            if (row.IsSystem)
            {
                return BadRequest( new { error = "Сістэмны тып нельга рэдагаваць." } );
            }

            bool exists = await _db.ExpenseInvoiceTypes.AnyAsync( x => x.Id != id && x.Name.ToLower() == name.ToLower() );
            if (exists)
            {
                return BadRequest( new { error = "Такі тып ужо існуе." } );
            }

            row.Name = name;
            await _db.SaveChangesAsync();
            return Ok( new ExpenseInvoiceTypeDto { Id = row.Id, Name = row.Name, IsSystem = row.IsSystem } );
        }

        [HttpGet( "vat-auto-finance" )]
        public async Task<ActionResult<VatAutoFinanceSettingsDto>> GetVatAutoFinance()
        {
            VatAutoFinanceSettings settings = await _vatFinanceSync.GetOrCreateSettingsAsync();
            return Ok(
                new VatAutoFinanceSettingsDto
                {
                    IsEnabled = settings.IsEnabled,
                    FinancePersonId = settings.FinancePersonId
                }
            );
        }

        [HttpPut( "vat-auto-finance" )]
        public async Task<IActionResult> SaveVatAutoFinance( [FromBody] VatAutoFinanceSettingsDto request )
        {
            if (request.IsEnabled)
            {
                if (!request.FinancePersonId.HasValue || request.FinancePersonId.Value <= 0)
                {
                    return BadRequest( new { error = "Выберыце асобу з фінансаў." } );
                }

                bool personExists = await _db.FinancePersons.AnyAsync( p => p.Id == request.FinancePersonId.Value );
                if (!personExists)
                {
                    return BadRequest( new { error = "Асоба не знойдзена." } );
                }
            }

            VatAutoFinanceSettings settings = await _vatFinanceSync.GetOrCreateSettingsAsync();
            settings.IsEnabled = request.IsEnabled;
            settings.FinancePersonId = request.IsEnabled ? request.FinancePersonId : null;
            settings.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _vatFinanceSync.ResyncAllReportPeriodsAsync();
            return Ok();
        }

        [HttpDelete( "invoice-expense-types/{id:int}" )]
        public async Task<IActionResult> DeleteExpenseInvoiceType( int id )
        {
            await ExpenseInvoiceTypeSeeder.EnsureDefaultAsync( _db );
            ExpenseInvoiceType? row = await _db.ExpenseInvoiceTypes
                .FirstOrDefaultAsync( x => x.Id == id );
            if (row is null)
            {
                return NotFound( new { error = "Тып не знойдзены." } );
            }
            if (row.IsSystem)
            {
                return BadRequest( new { error = "Сістэмны тып нельга выдаліць." } );
            }
            bool hasExpenses = await _db.VatReportExpenses
                .AnyAsync( e => e.ExpenseInvoiceTypeId == id );
            if (hasExpenses)
            {
                return BadRequest( new { error = "Нельга выдаліць тып, які ўжо выкарыстоўваецца ў расходах." } );
            }

            _db.ExpenseInvoiceTypes.Remove( row );
            await _db.SaveChangesAsync();
            return Ok();
        }

        private static bool IsValidEmail( string value )
        {
            try
            {
                _ = new MailAddress( value );
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsValidWebsite( string value )
        {
            string trimmed = value.Trim();
            if (string.IsNullOrWhiteSpace( trimmed )) return false;
            if (trimmed.Contains( ' ' )) return false;

            if (trimmed.Contains( "://" ))
            {
                if (!Uri.TryCreate( trimmed, UriKind.Absolute, out Uri? absoluteUri )) return false;
                if (absoluteUri.Scheme != Uri.UriSchemeHttp && absoluteUri.Scheme != Uri.UriSchemeHttps) return false;
                return !string.IsNullOrWhiteSpace( absoluteUri.Host ) && absoluteUri.Host.Contains( '.' );
            }

            // Allow plain domains like "kirma.sh" without protocol.
            if (!Uri.TryCreate( $"https://{trimmed}", UriKind.Absolute, out Uri? normalizedUri )) return false;
            return !string.IsNullOrWhiteSpace( normalizedUri.Host ) && normalizedUri.Host.Contains( '.' );
        }

        private static bool IsValidNip( string value )
        {
            string digitsOnly = new( value.Where( char.IsDigit ).ToArray() );
            return digitsOnly.Length == 10 && digitsOnly == value;
        }

        private static bool IsValidCurrency( string value ) =>
            value.Length == 3 && value.All( c => c >= 'A' && c <= 'Z' );
    }
}
