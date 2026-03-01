using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Common.Cache;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;

namespace tdtd_be.Services
{
    public sealed class AuthService
    {
        private readonly MongoDbContext _ctx;
        private readonly IOptions<MongoOptions> _opt;
        private readonly JwtService _jwt;
        private readonly RedisUserCache _cache;

        // Khuyến nghị: inject IPasswordHasher<AppUser> thay vì new, nhưng giữ như bệ hạ đang dùng cũng chạy
        private readonly PasswordHasher<AppUser> _hasher = new();

        public AuthService(
            MongoDbContext ctx,
            IOptions<MongoOptions> opt,
            JwtService jwt,
            RedisUserCache cache)
        {
            _ctx = ctx;
            _opt = opt;
            _jwt = jwt;
            _cache = cache;
        }

        private IMongoCollection<AppUser> Users => _ctx.Users;
        private IMongoCollection<RefreshTokenDoc> RefreshTokens => _ctx.RefreshTokens;

        private static string NormalizeUsername(string? input)
        {
            var u = input?.Trim();
            if (string.IsNullOrWhiteSpace(u))
                throw new InvalidOperationException("Sai tài khoản hoặc mật khẩu.");
            return u.ToLowerInvariant();
        }

        /// <summary>
        /// Resolve info từ Unit: UnitCode + UnitTypeCodes
        /// </summary>
        private async Task<(string unitSymbol, string unitName, string unitCode, List<string> unitTypeCodes)> ResolveUnitInfoAsync(AppUser u, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(u.UnitId))
                return ("", "", "", new List<string>());

            var unit = await _ctx.Units
                .Find(x => x.Id == u.UnitId)
                .Project(x => new { x.Symbol, x.ShortName, x.Code, x.UnitTypeCodes })
                .FirstOrDefaultAsync(ct);

            var unitCode = unit?.Code ?? "";
            var unitName = unit?.ShortName ?? "";
            var unitSymbol = unit?.Symbol ?? "";

            if (unit?.UnitTypeCodes is not { Count: > 0 })
                return (unitSymbol, unitName, unitCode, new List<string>());

            var codes = await _ctx.UnitTypes
                .Find(t => unit.UnitTypeCodes.Contains(t.Code) && !t.IsDeleted)
                .Project(t => t.Code)
                .ToListAsync(ct);

            return (unitSymbol, unitName, unitCode, codes);
        }

        public async Task<(AuthResponse resp, string refreshRaw)> LoginAsync(LoginRequest req, CancellationToken ct)
        {
            var key = NormalizeUsername(req.Username);

            var user = await Users.Find(x => x.Username == key && !x.IsDeleted).FirstOrDefaultAsync(ct);
            if (user is null) throw new InvalidOperationException("Sai tài khoản hoặc mật khẩu.");

            var vr = _hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
            if (vr == PasswordVerificationResult.Failed)
                throw new InvalidOperationException("Sai tài khoản hoặc mật khẩu.");

            if (user.IsDeleted)
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
            if (user.IsDeleted) throw new InvalidOperationException("Tài khoản đang bị khóa.");

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

            // tv + resolve unit info
            await _cache.EnsureTokenVersionAsync(user.Id);
            var tv = await _cache.GetTokenVersionAsync(user.Id);

            var (unitSymbol, unitname, unitCode, unitTypeCodes) = await ResolveUnitInfoAsync(user, ct);

            // ✅ cần sửa JwtService để nhận unitCode (bên dưới)
            var access = _jwt.CreateAccessToken(user, tv, unitTypeCodes, unitSymbol, unitname, unitCode);

            var me = ToMeResponse(user, unitTypeCodes, unitSymbol, unitname, unitCode);
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
            if (user.IsDeleted) throw new InvalidOperationException("Tài khoản đang bị khóa.");

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

            // tv + resolve unit info
            await _cache.EnsureTokenVersionAsync(user.Id);
            var tv = await _cache.GetTokenVersionAsync(user.Id);

            var (unitSymbol, unitName, unitCode, unitTypeCodes) = await ResolveUnitInfoAsync(user, ct);

            // ✅ cần sửa JwtService để nhận unitCode (bên dưới)
            var access = _jwt.CreateAccessToken(user, tv, unitTypeCodes, unitSymbol, unitName, unitCode);

            // cache me
            var me = ToMeResponse(user, unitTypeCodes, unitSymbol, unitName, unitCode);
            await _cache.SetMeAsync(me);

            var resp = new AuthResponse(
                AccessToken: access.token,
                ExpiresInSeconds: _jwt.AccessTokenExpiresInSeconds(),
                User: me
            );

            return (resp, refreshRaw);
        }

        private static MeResponse ToMeResponse(AppUser u, List<string> unitTypeCodes,string unitSymbol, string unitName, string unitCode)
        {
            return new MeResponse(
                id: u.Id,
                username: u.Username ?? "",
                fullName: u.FullName ?? "",
                unitTypeCodes: unitTypeCodes ?? new List<string>(),
                unitId: u.UnitId ?? "",
                unitSymbol: unitSymbol ?? "",
                unitName: unitName ?? "",
                unitCode: unitCode ?? "",     
                roles: u.Roles ?? new List<string>(),
                positionCode: u.PositionCode ?? "",
                isDeleted: u.IsDeleted
            );
        }
    }
}