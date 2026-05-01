using System.Net.Mail;
using System.Linq;
using backend.Data;
using backend.Models;
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

        public SettingsController( AppDbContext db )
        {
            _db = db;
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
