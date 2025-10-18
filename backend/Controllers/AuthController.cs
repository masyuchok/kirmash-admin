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

        public AuthController( IConfiguration config, JwtService jwt, IWebHostEnvironment env )
        {
            _config = config;
            _jwt = jwt;
            _env = env;
        }

        [HttpGet( "login" )]
        public IActionResult Login( [FromQuery] string shop )
        {
            string? clientId = _config["Shopify:ApiKey"];
            string? scopes = _config["Shopify:Scopes"];
            string? baseUrl = _config["BaseUrl"];
            string? redirectUri = $"{baseUrl}/auth/callback";

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
                
            // Gets Shopify token
            HttpClient client = new HttpClient( );
            HttpResponseMessage response = await client.PostAsync(
                $"https://{shop}/admin/oauth/access_token",
                new FormUrlEncodedContent( new Dictionary<string, string>
                {
                    ["client_id"] = _config["Shopify:ApiKey"]!,
                    ["client_secret"] = _config["Shopify:ApiSecret"]!,
                    ["code"] = code
                } ) );

            if (response != null && !response.IsSuccessStatusCode)
            {
                return StatusCode( 500, "Token exchange with Shopify failed" );
            }

            ShopifyTokenResponse tokenResponse = await response.Content.ReadFromJsonAsync<ShopifyTokenResponse>( );

            // Generates the JWT Token
            string jwt = _jwt.GenerateJwtToken( shop, tokenResponse!.access_token! );

            // Sets JWT to Cookies
            Response.Cookies.Append( "jwt_token", jwt, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Path = "/",
                //Domain = ".kirma.sh" // for prod only
            } );

            // Redirect to landing page
            return Redirect( $"{_config["ClientUrl"]}/" );
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
