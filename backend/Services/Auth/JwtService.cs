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
            return GenerateOrganizationToken(
                "kirma",
                shop,
                accessToken,
                new Dictionary<string, string> { ["shop"] = shop } );
        }

        public string GenerateOrganizationToken(
            string organization,
            string subject,
            string accessToken,
            IReadOnlyDictionary<string, string>? extraClaims = null )
        {
            SymmetricSecurityKey key = new SymmetricSecurityKey( Encoding.UTF8.GetBytes( _config["Jwt:Secret"]! ) );
            SigningCredentials creds = new SigningCredentials( key, SecurityAlgorithms.HmacSha256 );

            List<Claim> claims =
            [
                new Claim( "org", organization ),
                new Claim( JwtRegisteredClaimNames.Sub, subject ),
                new Claim( "access_token", accessToken ),
            ];

            if (extraClaims is not null)
            {
                foreach (KeyValuePair<string, string> pair in extraClaims)
                {
                    if (pair.Key is "org" or "access_token" or JwtRegisteredClaimNames.Sub)
                    {
                        continue;
                    }

                    claims.Add( new Claim( pair.Key, pair.Value ) );
                }
            }

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
