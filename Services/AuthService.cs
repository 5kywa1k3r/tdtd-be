//Services/AuthService.cs
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Caching;
using tdtd_be.Common.Exceptions;
using tdtd_be.Common.Security;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Options;
namespace tdtd_be.Services
{
    public sealed class AuthService
    {
        private readonly MongoDbContext _db;
        private readonly IJwtService _jwt;
        private readonly JwtOptions _jwtOpt;
        private readonly IAppCache _cache;

        public AuthService(MongoDbContext db, IJwtService jwt, IOptions<JwtOptions> jwtOpt, IAppCache cache)
        {
            _db = db;
            _jwt = jwt;
            _jwtOpt = jwtOpt.Value;
            _cache = cache;
        }

        public async Task<TokenResponse> LoginAsync(LoginRequest req, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                throw AppException.BadRequest("Thiếu Username/Password.");

            // Anti brute-force (nhẹ)
            var failKey = CacheKeys.LoginFail(req.Username);
            var failCount = await _cache.GetAsync<int>(failKey, ct);
            if (failCount >= 8)
                throw new AppException(429, "TOO_MANY_ATTEMPTS", "Thử lại sau (tạm khóa 15 phút).");

            // Cache user theo username 2 phút
            var userKey = CacheKeys.UserByUsername(req.Username);
            var user = await _cache.GetAsync<AppUser>(userKey, ct);

            if (user is null)
            {
                user = await _db.Users.Find(x => x.Username == req.Username).FirstOrDefaultAsync(ct);
                if (user is not null)
                    await _cache.SetAsync(userKey, user, TimeSpan.FromMinutes(2), ct);
            }

            if (user is null || !user.IsActive || !PasswordHasher.Verify(req.Password, user.PasswordHash))
            {
                await _cache.SetAsync(failKey, failCount + 1, TimeSpan.FromMinutes(15), ct);
                throw AppException.Unauthorized("Sai tài khoản hoặc mật khẩu.");
            }

            // clear fail
            await _cache.RemoveAsync(failKey, ct);

            var accessToken = _jwt.CreateAccessToken(user);

            // refresh token: lưu HASH vào Mongo (raw trả về client)
            var refreshRaw = _jwt.CreateRefreshTokenRaw();
            var refreshHash = TokenHashing.Sha256(refreshRaw);

            var rt = new RefreshTokenDoc
            {
                UserId = user.Id,
                TokenHash = refreshHash,
                ExpiresAt = DateTime.UtcNow.AddDays(_jwtOpt.RefreshTokenDays),
            };
            await _db.RefreshTokens.InsertOneAsync(rt, cancellationToken: ct);

            // cache user by id 30 phút (để phục vụ middleware/service khác)
            await _cache.SetAsync(CacheKeys.UserById(user.Id), new
            {
                user.Id,
                user.FullName,
                user.JobTitle,
                user.UnitId,
                user.UnitName,
                user.Roles
            }, TimeSpan.FromMinutes(30), ct);

            return new TokenResponse(accessToken, refreshRaw);
        }

        // (Tuỳ chọn) refresh rotate chuẩn
        public async Task<TokenResponse> RefreshAsync(RefreshRequest req, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(req.RefreshToken))
                throw AppException.BadRequest("Thiếu refreshToken.");

            var oldHash = TokenHashing.Sha256(req.RefreshToken);

            var old = await _db.RefreshTokens
                .Find(x => x.TokenHash == oldHash && x.RevokedAt == null && x.ExpiresAt > DateTime.UtcNow)
                .FirstOrDefaultAsync(ct);

            if (old is null) throw AppException.Unauthorized("Refresh token không hợp lệ/hết hạn.");

            var user = await _db.Users.Find(x => x.Id == old.UserId).FirstOrDefaultAsync(ct);
            if (user is null || !user.IsActive) throw AppException.Unauthorized("User không hợp lệ.");

            // rotate
            var newRaw = _jwt.CreateRefreshTokenRaw();
            var newHash = TokenHashing.Sha256(newRaw);

            var now = DateTime.UtcNow;
            var updateOld = Builders<RefreshTokenDoc>.Update
                .Set(x => x.RevokedAt, now)
                .Set(x => x.ReplacedByTokenHash, newHash);

            await _db.RefreshTokens.UpdateOneAsync(x => x.Id == old.Id, updateOld, cancellationToken: ct);

            await _db.RefreshTokens.InsertOneAsync(new RefreshTokenDoc
            {
                UserId = user.Id,
                TokenHash = newHash,
                ExpiresAt = now.AddDays(_jwtOpt.RefreshTokenDays),
                CreatedAt = now
            }, cancellationToken: ct);

            var access = _jwt.CreateAccessToken(user);
            return new TokenResponse(access, newRaw);
        }
    }
}
