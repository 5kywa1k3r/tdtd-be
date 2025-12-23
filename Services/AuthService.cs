using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Common.Cache;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Options;

namespace tdtd_be.Services
{
    public sealed class AuthService
    {
        private readonly MongoDbContext _ctx;
        private readonly IOptions<MongoOptions> _opt;
        private readonly JwtService _jwt;
        private readonly RedisUserCache _cache;
        private readonly PasswordHasher<AppUser> _hasher = new();

        public AuthService(
            MongoDbContext ctx,
            IOptions<MongoOptions> opt,
            JwtService jwt,
            RedisUserCache cache
        )
        {
            _ctx = ctx;
            _opt = opt;
            _jwt = jwt;
            _cache = cache;
        }

        private IMongoCollection<AppUser> Users => _ctx.Users;
        private IMongoCollection<RefreshTokenDoc> RefreshTokens => _ctx.RefreshTokens;

        public async Task<(AuthResponse resp, string refreshRaw)> SignUpAsync(SignUpRequest req, CancellationToken ct)
        {
            var username = req.Username?.Trim();
            if (string.IsNullOrWhiteSpace(username))
                throw new InvalidOperationException("Username không hợp lệ.");

            // friendly check (unique index vẫn là tuyến cuối)
            var exists = await Users.Find(x => x.Username == username).AnyAsync(ct);
            if (exists) throw new InvalidOperationException("Username đã tồn tại.");

            var user = new AppUser
            {
                Username = username,
                FullName = string.IsNullOrWhiteSpace(req.FullName) ? username : req.FullName.Trim(),
                UnitTypeId = new List<string>(),
                UnitId = "",
                UnitName = "",
                Roles = new List<string>(),
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            user.PasswordHash = _hasher.HashPassword(user, req.Password);

            try
            {
                await Users.InsertOneAsync(user, cancellationToken: ct);
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                throw new InvalidOperationException("Username đã tồn tại.");
            }

            return await IssueTokensAsync(user, ct);
        }

        public async Task<(AuthResponse resp, string refreshRaw)> LoginAsync(LoginRequest req, CancellationToken ct)
        {
            var key = req.Username?.Trim();
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("Sai tài khoản hoặc mật khẩu.");

            var user = await Users.Find(x => x.Username == key).FirstOrDefaultAsync(ct);
            if (user is null) throw new InvalidOperationException("Sai tài khoản hoặc mật khẩu.");

            var vr = _hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
            if (vr == PasswordVerificationResult.Failed)
                throw new InvalidOperationException("Sai tài khoản hoặc mật khẩu.");

            if (!user.IsActive)
                throw new InvalidOperationException("Tài khoản đang bị khóa.");

            return await IssueTokensAsync(user, ct);
        }

        public async Task<(AuthResponse resp, string refreshRaw)> RefreshAsync(string refreshRaw, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(refreshRaw))
                throw new InvalidOperationException("Refresh token không hợp lệ hoặc đã hết hạn.");

            var hash = _jwt.Sha256(refreshRaw);

            var tokenDoc = await RefreshTokens.Find(x => x.TokenHash == hash).FirstOrDefaultAsync(ct);
            if (tokenDoc is null || !tokenDoc.IsActive)
                throw new InvalidOperationException("Refresh token không hợp lệ hoặc đã hết hạn.");

            var user = await Users.Find(x => x.Id == tokenDoc.UserId).FirstOrDefaultAsync(ct);
            if (user is null) throw new InvalidOperationException("User không tồn tại.");
            if (!user.IsActive) throw new InvalidOperationException("Tài khoản đang bị khóa.");

            // rotation: revoke old + issue new
            var newRefreshRaw = _jwt.CreateRefreshTokenRaw();
            var newHash = _jwt.Sha256(newRefreshRaw);

            tokenDoc.RevokedAt = DateTime.UtcNow;
            tokenDoc.ReplacedByTokenHash = newHash;
            await RefreshTokens.ReplaceOneAsync(x => x.Id == tokenDoc.Id, tokenDoc, cancellationToken: ct);

            var newDoc = new RefreshTokenDoc
            {
                UserId = user.Id,
                TokenHash = newHash,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(_jwt.RefreshTokenDays())
            };
            await RefreshTokens.InsertOneAsync(newDoc, cancellationToken: ct);

            // tv + cache me
            await _cache.EnsureTokenVersionAsync(user.Id);
            var tv = await _cache.GetTokenVersionAsync(user.Id);

            var access = _jwt.CreateAccessToken(user, tv);
            var me = ToMeResponse(user);

            await _cache.SetMeAsync(me);

            var resp = new AuthResponse(
                AccessToken: access.token,
                ExpiresInSeconds: _jwt.AccessTokenExpiresInSeconds(),
                User: me
            );

            return (resp, newRefreshRaw);
        }

        public async Task LogoutAsync(string refreshRaw, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(refreshRaw)) return;

            var hash = _jwt.Sha256(refreshRaw);

            var tokenDoc = await RefreshTokens.Find(x => x.TokenHash == hash).FirstOrDefaultAsync(ct);
            if (tokenDoc is null) return;

            tokenDoc.RevokedAt = DateTime.UtcNow;
            await RefreshTokens.ReplaceOneAsync(x => x.Id == tokenDoc.Id, tokenDoc, cancellationToken: ct);
        }

        // gọi khi khóa user/đổi roles/unit/reset password...
        public async Task RevokeUserSessionsAsync(string userId, CancellationToken ct)
        {
            await _cache.BumpTokenVersionAsync(userId);
            await _cache.DeleteMeAsync(userId);

            await RefreshTokens.UpdateManyAsync(
                x => x.UserId == userId && x.RevokedAt == null,
                Builders<RefreshTokenDoc>.Update.Set(x => x.RevokedAt, DateTime.UtcNow),
                cancellationToken: ct
            );
        }

        private async Task<(AuthResponse resp, string refreshRaw)> IssueTokensAsync(AppUser user, CancellationToken ct)
        {
            if (!user.IsActive) throw new InvalidOperationException("Tài khoản đang bị khóa.");

            // create refresh
            var refreshRaw = _jwt.CreateRefreshTokenRaw();
            var refreshHash = _jwt.Sha256(refreshRaw);

            var rt = new RefreshTokenDoc
            {
                UserId = user.Id,
                TokenHash = refreshHash,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(_jwt.RefreshTokenDays())
            };

            await RefreshTokens.InsertOneAsync(rt, cancellationToken: ct);

            // tv + access token
            await _cache.EnsureTokenVersionAsync(user.Id);
            var tv = await _cache.GetTokenVersionAsync(user.Id);

            var access = _jwt.CreateAccessToken(user, tv);

            // cache me
            var me = ToMeResponse(user);
            await _cache.SetMeAsync(me);

            var resp = new AuthResponse(
                AccessToken: access.token,
                ExpiresInSeconds: _jwt.AccessTokenExpiresInSeconds(),
                User: me
            );

            return (resp, refreshRaw);
        }

        private static MeResponse ToMeResponse(AppUser u)
        {
            return new MeResponse(
                id: u.Id,
                username: u.Username ?? "",
                fullName: u.FullName ?? "",
                unitTypeId: u.UnitTypeId ?? new List<string>(),
                unitId: u.UnitId ?? "",
                unitName: u.UnitName ?? "",
                roles: u.Roles ?? new List<string>(),
                isActive: u.IsActive
            );
        }
    }
}
