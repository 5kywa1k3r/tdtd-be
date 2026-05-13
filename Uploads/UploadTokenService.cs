using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using tdtd_be.Common.Errors;

namespace tdtd_be.Uploads;

public sealed class UploadTokenService
{
    private readonly SymmetricSecurityKey _key;

    public UploadTokenService(IConfiguration cfg)
    {
        var jwtKey = cfg["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(jwtKey))
            throw AppExceptionFactory.Create(AppErrorCode.AUTH_CONFIG_INVALID, new { key = "Jwt:Key", consumer = nameof(UploadTokenService) });

        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
    }

    public string Issue(
        string userId,
        string fileName,
        string mime,
        long size,
        string sourceType,
        string? sourceId,
        int ttlSeconds)
    {
        var now = DateTime.UtcNow;
        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new("typ", "upload"),
            new("uid", userId),
            new("fn", fileName),
            new("mime", mime),
            new("len", size.ToString()),
            new("st", sourceType ?? "UPLOAD"),
        };

        if (!string.IsNullOrWhiteSpace(sourceId))
            claims.Add(new("sid", sourceId));

        var token = new JwtSecurityToken(
            claims: claims,
            notBefore: now,
            expires: now.AddSeconds(ttlSeconds),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ✅ wrapper cho controller dùng cho gọn
    public string CreateToken(UploadTokenPayload p, int ttlSeconds = 900)
        => Issue(
            userId: p.UserId,
            fileName: p.FileName,
            mime: p.Mime,
            size: p.Length,
            sourceType: p.SourceType,
            sourceId: p.SourceId,
            ttlSeconds: ttlSeconds);

    public UploadTokenPayload? Validate(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _key,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(5)
            }, out _);

            if (principal.FindFirstValue("typ") != "upload") return null;

            var uid = principal.FindFirstValue("uid");
            var fn = principal.FindFirstValue("fn");
            var mime = principal.FindFirstValue("mime");
            var lenStr = principal.FindFirstValue("len");
            var st = principal.FindFirstValue("st") ?? "UPLOAD";
            var sid = principal.FindFirstValue("sid");

            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(fn) || string.IsNullOrWhiteSpace(lenStr))
                return null;

            _ = long.TryParse(lenStr, out var len);

            return new UploadTokenPayload
            {
                UserId = uid!,
                FileName = fn!,
                Mime = string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime!,
                Length = len,
                SourceType = st,
                SourceId = sid
            };
        }
        catch
        {
            return null;
        }
    }
}

public sealed class UploadTokenPayload
{
    public string UserId { get; set; } = default!;
    public string FileName { get; set; } = default!;
    public string Mime { get; set; } = default!;
    public long Length { get; set; }
    public string SourceType { get; set; } = "UPLOAD";
    public string? SourceId { get; set; }
}
