using backend.Models;
using backend.Services.Auth;
using backend.Services.Odoo;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace backend.Controllers;

[ApiController]
[Route( "auth/odoo" )]
public class OdooAuthController : Controller
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly JwtService _jwt;
    private readonly OdooAuthService _odooAuth;

    public OdooAuthController(
        IConfiguration config,
        IWebHostEnvironment env,
        JwtService jwt,
        OdooAuthService odooAuth )
    {
        _config = config;
        _env = env;
        _jwt = jwt;
        _odooAuth = odooAuth;
    }

    [HttpPost( "login" )]
    public async Task<IActionResult> Login( [FromBody] OdooLoginRequest request )
    {
        if (request is null)
        {
            return BadRequest( new { error = "Пусты запыт." } );
        }

        try
        {
            OdooSession session = await _odooAuth.AuthenticateAsync( request.Login, request.Password );
            string jwt = _jwt.GenerateOrganizationToken(
                "bukinistka",
                session.Login,
                session.SessionId,
                new Dictionary<string, string>
                {
                    ["odoo_db"] = session.Database,
                    ["odoo_uid"] = session.Uid.ToString( ),
                    ["odoo_login"] = session.Login,
                    ["odoo_name"] = session.Name ?? session.Login,
                } );

            string cookieName = GetBukinistkaCookieName( );
            Response.Cookies.Append( cookieName, jwt, BuildAuthCookieOptions( ) );

            return Ok( new
            {
                success = true,
                redirectUrl = "/bukinistka",
                user = new
                {
                    login = session.Login,
                    name = session.Name ?? session.Login,
                },
            } );
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }

    [HttpGet( "me" )]
    public IActionResult Me()
    {
        ClaimsPrincipal? principal = BukinistkaJwtAuthentication.TryValidateCookie( Request, _config );
        if (principal is null)
        {
            return Unauthorized( new { error = "Няма актыўнай сесіі Bukinistka." } );
        }

        return Ok( new
        {
            login = principal.FindFirst( "odoo_login" )?.Value,
            name = principal.FindFirst( "odoo_name" )?.Value,
            uid = principal.FindFirst( "odoo_uid" )?.Value,
            database = principal.FindFirst( "odoo_db" )?.Value,
        } );
    }

    [HttpPost( "logout" )]
    public IActionResult Logout()
    {
        ClearBukinistkaCookie( );
        return Ok( new { success = true } );
    }

    /// <summary>Development-only: shows Odoo connection settings for Bukinistka.</summary>
    [HttpGet( "setup-info" )]
    public IActionResult SetupInfo()
    {
        if (!_env.IsDevelopment( ))
        {
            return NotFound( );
        }

        return Ok( new
        {
            odooBaseUrl = _config["Odoo:BaseUrl"],
            odooDatabase = _config["Odoo:Database"],
            clientUrl = GetRequiredPublicUrl( "ClientUrl" ),
            hint = "Bukinistka uses Odoo /web/session/authenticate (login + password). No OAuth module required.",
        } );
    }

    private void ClearBukinistkaCookie()
    {
        string cookieName = GetBukinistkaCookieName( );
        CookieOptions options = BuildAuthCookieOptions( );
        options.Expires = DateTimeOffset.UnixEpoch;
        Response.Cookies.Append( cookieName, string.Empty, options );

        if (!string.IsNullOrEmpty( options.Domain ))
        {
            CookieOptions hostOnly = BuildAuthCookieOptions( );
            hostOnly.Domain = null;
            hostOnly.Expires = DateTimeOffset.UnixEpoch;
            Response.Cookies.Append( cookieName, string.Empty, hostOnly );
        }
    }

    private string GetBukinistkaCookieName() =>
        _config["Auth:BukinistkaCookieName"]?.Trim() ?? "bukinistka_token";

    private CookieOptions BuildAuthCookieOptions()
    {
        bool useSecureCookie = Request.IsHttps
            || (!_env.IsDevelopment( ) && !IsLocalhostRequest( ));
        CookieOptions cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = useSecureCookie,
            SameSite = useSecureCookie ? SameSiteMode.None : SameSiteMode.Lax,
            Path = "/",
        };

        string? clientUrl = GetRequiredPublicUrl( "ClientUrl" );
        if (clientUrl is not null && clientUrl.Contains( "kirma.sh", StringComparison.OrdinalIgnoreCase ))
        {
            cookieOptions.Domain = ".kirma.sh";
        }

        return cookieOptions;
    }

    private bool IsLocalhostRequest() =>
        Request.Host.Host.Contains( "localhost", StringComparison.OrdinalIgnoreCase )
        || Request.Host.Host is "127.0.0.1";

    private string? GetRequiredPublicUrl( string key )
    {
        string? value = _config[key]?.Trim( ).TrimEnd( '/' );
        return string.IsNullOrWhiteSpace( value ) ? null : value;
    }
}
