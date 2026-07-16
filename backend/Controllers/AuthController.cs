using backend.Models;
using backend.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace backend.Controllers
{
    [ApiController]
    [Route( "[controller]" )]
    public class AuthController : Controller
    {
        private readonly IConfiguration _config;
        private readonly JwtService _jwt;
        private readonly IWebHostEnvironment _env;
        private readonly IHttpClientFactory _httpClientFactory;

        public AuthController(
            IConfiguration config,
            JwtService jwt,
            IWebHostEnvironment env,
            IHttpClientFactory httpClientFactory )
        {
            _config = config;
            _jwt = jwt;
            _env = env;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>Development-only: shows OAuth redirect_uri to add in Shopify Partners.</summary>
        [HttpGet( "setup-info" )]
        public IActionResult SetupInfo()
        {
            if (!_env.IsDevelopment( ))
            {
                return NotFound( );
            }

            string? redirectUri = TryGetOAuthRedirectUri( out string? error );
            if (redirectUri is null)
            {
                return BadRequest( new { error } );
            }

            return Ok( new
            {
                redirectUri,
                baseUrl = GetOAuthBaseUrl( ),
                clientUrl = GetRequiredPublicUrl( "ClientUrl" ),
                hint = "Add redirectUri to Shopify app → Allowed redirection URL(s).",
            } );
        }

        [HttpGet( "login" )]
        public IActionResult Login( [FromQuery] string shop )
        {
            string? clientId = _config["Shopify:ApiKey"];
            string? scopes = _config["Shopify:Scopes"];
            string? redirectUri = TryGetOAuthRedirectUri( out string? configError );
            if (redirectUri is null)
            {
                return StatusCode( 500, configError ?? "OAuth redirect URI is not configured." );
            }

            string shopifyUrl = $"https://{shop}/admin/oauth/authorize" +
                             $"?client_id={clientId}" +
                             $"&scope={HttpUtility.UrlEncode( scopes )}" +
                             $"&redirect_uri={HttpUtility.UrlEncode( redirectUri )}";

            return Redirect( shopifyUrl );
        }

        [HttpGet( "callback" )]
        public async Task<IActionResult> Callback( [FromQuery] string code, [FromQuery] string shop, [FromQuery] string hmac )
        {
            // Checks if this response has been modified
            if (!IsHmacValid( Request.Query, hmac ))
            {
                return BadRequest( "Invalid HMAC signature" );
            }
                
            HttpClient client = _httpClientFactory.CreateClient( "Shopify" );
            HttpResponseMessage response = await client.PostAsync(
                $"https://{shop}/admin/oauth/access_token",
                new FormUrlEncodedContent( new Dictionary<string, string>
                {
                    ["client_id"] = _config["Shopify:ApiKey"]!,
                    ["client_secret"] = _config["Shopify:ApiSecret"]!,
                    ["code"] = code
                } ) );

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode( 500, "Token exchange with Shopify failed" );
            }

            ShopifyTokenResponse? tokenResponse = await response.Content.ReadFromJsonAsync<ShopifyTokenResponse>( );
            if (string.IsNullOrWhiteSpace( tokenResponse?.access_token ))
            {
                return StatusCode( 500, "Token exchange with Shopify failed: empty token" );
            }

            // Generates the JWT Token
            string jwt = _jwt.GenerateJwtToken( shop, tokenResponse.access_token );

            string cookieName = _config["Auth:CookieName"] ?? "jwt_token";
            Response.Cookies.Append( cookieName, jwt, BuildAuthCookieOptions( ) );

            string? clientUrl = GetRequiredPublicUrl( "ClientUrl" );
            if (string.IsNullOrWhiteSpace( clientUrl ))
            {
                return StatusCode( 500, "ClientUrl is not configured." );
            }

            return Redirect( $"{clientUrl}/" );
        }

        [HttpPost( "logout" )]
        public IActionResult Logout()
        {
            ClearAuthCookie( );
            return Ok( new { success = true } );
        }

        private void ClearAuthCookie()
        {
            string cookieName = _config["Auth:CookieName"] ?? "jwt_token";
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

        /// <summary>
        /// Public API base for OAuth callback. In local dev with Next.js proxy, always {ClientUrl}/api
        /// so redirect_uri matches http://localhost:3000/api/auth/callback (not legacy :7183).
        /// </summary>
        private string? GetOAuthBaseUrl()
        {
            string? clientUrl = GetRequiredPublicUrl( "ClientUrl" );
            if (_env.IsDevelopment( )
                && clientUrl is not null
                && (clientUrl.Contains( "localhost", StringComparison.OrdinalIgnoreCase )
                    || clientUrl.Contains( "127.0.0.1", StringComparison.OrdinalIgnoreCase )))
            {
                return $"{clientUrl}/api";
            }

            return GetRequiredPublicUrl( "BaseUrl" );
        }

        private string? TryGetOAuthRedirectUri( out string? error )
        {
            error = null;
            string? baseUrl = GetOAuthBaseUrl( );
            if (baseUrl is null)
            {
                error = "BaseUrl / ClientUrl is not configured.";
                return null;
            }

            return $"{baseUrl}/auth/callback";
        }

        private bool IsLocalhostRequest() =>
            Request.Host.Host.Contains( "localhost", StringComparison.OrdinalIgnoreCase )
            || Request.Host.Host is "127.0.0.1";

        private string? GetRequiredPublicUrl( string key )
        {
            string? value = _config[key]?.Trim().TrimEnd( '/' );
            if (string.IsNullOrWhiteSpace( value ))
            {
                return null;
            }

            if (!_env.IsDevelopment( )
                && (value.Contains( "localhost", StringComparison.OrdinalIgnoreCase )
                    || value.Contains( "127.0.0.1", StringComparison.OrdinalIgnoreCase )))
            {
                throw new InvalidOperationException(
                    $"Configuration '{key}' must not point to localhost in Production (current: {value})." );
            }

            return value;
        }

        private bool IsHmacValid( IQueryCollection query, string receivedHmac )
        {
            string secret = _config["Shopify:ApiSecret"]!;
            string[] sortedParams = query
                .Where( kvp => kvp.Key != "hmac" && kvp.Key != "signature" )
                .OrderBy( kvp => kvp.Key, StringComparer.Ordinal )
                .Select( kvp => $"{kvp.Key}={kvp.Value}" )
                .ToArray( );

            string data = string.Join( "&", sortedParams );

            byte[] keyBytes = Encoding.UTF8.GetBytes( secret );
            byte[] dataBytes = Encoding.UTF8.GetBytes( data );

            using HMACSHA256 hmac = new HMACSHA256( keyBytes );
            byte[] hash = hmac.ComputeHash( dataBytes );
            string calculatedHmac = BitConverter.ToString( hash ).Replace( "-", "" ).ToLower( );

            return calculatedHmac == receivedHmac;
        }
    }
}
