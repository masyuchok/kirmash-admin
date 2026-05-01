using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace backend.Services.Auth
{
    public class JwtService
    {
        private readonly IConfiguration _config;

        public JwtService( IConfiguration config )
        {
            _config = config;
        }

        public string GenerateJwtToken( string shop, string accessToken )
        {
            SymmetricSecurityKey key = new SymmetricSecurityKey( Encoding.UTF8.GetBytes( _config["Jwt:Secret"]! ) );
            SigningCredentials creds = new SigningCredentials( key, SecurityAlgorithms.HmacSha256 );

            Claim[] claims = {
                new Claim("shop", shop),
                new Claim("access_token", accessToken)
            };

            JwtSecurityToken token = new JwtSecurityToken(
                issuer: _config["Jwt:Issuer"],
                audience: _config["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddDays( 7 ),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler( ).WriteToken( token );
        }
    }
}
