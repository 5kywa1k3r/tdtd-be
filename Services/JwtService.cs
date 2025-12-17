//Services/JwtService.cs
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using tdtd_be.Common;
using tdtd_be.Models;
using tdtd_be.Options;

namespace tdtd_be.Services
{
    public interface IJwtService
    {
        string CreateAccessToken(AppUser u);
        string CreateRefreshTokenRaw();
    }

    public sealed class JwtService : IJwtService
    {
        private readonly JwtOptions _opt;
        public JwtService(IOptions<JwtOptions> opt) => _opt = opt.Value;

        public string CreateAccessToken(AppUser u)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Key));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, u.Id),
            new(ClaimTypes.NameIdentifier, u.Id),
            new("name", u.FullName),
            new(ClaimTypes.Name, u.FullName),

            new(AppClaimTypes.JobTitle, u.JobTitle),
            new(AppClaimTypes.UnitId, u.UnitId),
            new(AppClaimTypes.UnitName, u.UnitName),

            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

            claims.AddRange(u.Roles.Distinct().Select(r => new Claim(ClaimTypes.Role, r)));

            var token = new JwtSecurityToken(
                issuer: _opt.Issuer,
                audience: _opt.Audience,
                claims: claims,
                notBefore: DateTime.UtcNow,
                expires: DateTime.UtcNow.AddMinutes(_opt.AccessTokenMinutes),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public string CreateRefreshTokenRaw()
        {
            var bytes = RandomNumberGenerator.GetBytes(64);
            return Convert.ToBase64String(bytes);
        }
    }
}
