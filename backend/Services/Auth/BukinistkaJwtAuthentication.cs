using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace backend.Services.Auth;

public static class BukinistkaJwtAuthentication
{
    public static ClaimsPrincipal? TryValidateCookie( HttpRequest request, IConfiguration config )
    {
        string cookieName = config["Auth:BukinistkaCookieName"]?.Trim() ?? "bukinistka_token";
        string? token = request.Cookies[cookieName];
        if (string.IsNullOrWhiteSpace( token ))
        {
            return null;
        }

        string? secret = config["Jwt:Secret"];
        if (string.IsNullOrWhiteSpace( secret ))
        {
            return null;
        }

        TokenValidationParameters parameters = new()
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey( Encoding.UTF8.GetBytes( secret ) ),
            ClockSkew = TimeSpan.FromMinutes( 1 ),
        };

        JwtSecurityTokenHandler handler = new();
        try
        {
            ClaimsPrincipal principal = handler.ValidateToken( token, parameters, out SecurityToken _ );
            string? org = principal.FindFirst( "org" )?.Value;
            if (!string.Equals( org, "bukinistka", StringComparison.OrdinalIgnoreCase ))
            {
                return null;
            }

            return principal;
        }
        catch
        {
            return null;
        }
    }
}
