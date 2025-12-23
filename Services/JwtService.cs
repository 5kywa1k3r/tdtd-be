// Services/JwtService.cs
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using tdtd_be.Models;
using tdtd_be.Options;

namespace tdtd_be.Services
{
    public sealed class JwtService
    {
        private readonly JwtOptions _opt;
        private readonly SigningCredentials _creds;

        public JwtService(IOptions<JwtOptions> opt)
        {
            _opt = opt.Value;

            if (string.IsNullOrWhiteSpace(_opt.Key))
                throw new InvalidOperationException("Jwt:Key is required.");

            var keyBytes = Encoding.UTF8.GetBytes(_opt.Key);
            var key = new SymmetricSecurityKey(keyBytes);

            _creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        }

        public (string token, DateTime expiresAtUtc) CreateAccessToken(AppUser u, long tokenVersion)
        {
            var now = DateTime.UtcNow;
            var exp = now.AddMinutes(_opt.AccessTokenMinutes);

            // ===== Claims chuẩn theo bệ hạ =====
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, u.Id),

                new("username", u.Username ?? ""),
                new("fullName", u.FullName ?? ""),

                // CSV
                new("unitTypeId", (u.UnitTypeId is { Count: > 0 }) ? string.Join(",", u.UnitTypeId) : ""),
                new("unitId", u.UnitId ?? ""),
                new("unitName", u.UnitName ?? ""),

                new("isActive", u.IsActive ? "true" : "false"),

                // roles CSV (middleware parse)
                new("roles", (u.Roles is { Count: > 0 }) ? string.Join(",", u.Roles) : ""),

                // token version for redis kill-switch
                new("tv", tokenVersion.ToString())
            };

            var jwt = new JwtSecurityToken(
                issuer: _opt.Issuer,
                audience: _opt.Audience,
                claims: claims,
                notBefore: now,
                expires: exp,
                signingCredentials: _creds
            );

            var token = new JwtSecurityTokenHandler().WriteToken(jwt);
            return (token, exp);
        }

        public string CreateRefreshTokenRaw()
        {
            // random 32 bytes -> base64url
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            return Base64UrlEncoder.Encode(bytes);
        }

        public string Sha256(string raw)
        {
            if (raw is null) raw = "";
            var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        public int AccessTokenExpiresInSeconds() => _opt.AccessTokenMinutes * 60;
        public int RefreshTokenDays() => _opt.RefreshTokenDays;
    }
}
