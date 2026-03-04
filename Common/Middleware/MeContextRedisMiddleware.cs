using MongoDB.Driver;
using System.Security.Claims;
using tdtd_be.Common.Cache;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;

namespace tdtd_be.Common.Middleware
{
    /// <summary>
    /// Full flow:
    /// JwtBearer validates token -> this middleware:
    /// 1) kill-switch token version (tv)
    /// 2) Redis-first for "me"
    /// 3) Mongo fallback (User -> Unit -> UnitType)
    /// 4) Claims fallback (last resort)
    /// 5) set Items["me"]
    /// </summary>
    public sealed class MeContextRedisMiddleware : IMiddleware
    {
        public const string MeItemKey = "me";

        private readonly RedisUserCache? _cache;

        private readonly IMongoCollection<AppUser> _users;
        private readonly IMongoCollection<Unit> _units;
        private readonly IMongoCollection<UnitType> _unitTypes;
        private readonly UserContext? _userContext;

        public MeContextRedisMiddleware(
            RedisUserCache? cache,
            MongoDbContext ctx,
            UserContext? userContext = null)
        {
            _cache = cache;
            _users = ctx.Users;
            _units = ctx.Units;
            _unitTypes = ctx.UnitTypes;

            _userContext = userContext;
        }

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if (context.User?.Identity?.IsAuthenticated != true)
            {
                await next(context);
                return;
            }

            var userId =
                context.User.FindFirstValue("sub")
                ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (string.IsNullOrWhiteSpace(userId))
            {
                await next(context);
                return;
            }

            // ===== Kill-switch: token version (tv) =====
            var tokenTvStr = context.User.FindFirstValue("tv") ?? "0";
            _ = long.TryParse(tokenTvStr, out var tokenTv);

            // Chỉ check tokenVersion nếu Redis bật (có cache)
            if (_cache is not null)
            {
                await _cache.EnsureTokenVersionAsync(userId);
                var currentTv = await _cache.GetTokenVersionAsync(userId);

                if (tokenTv < currentTv)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "TOKEN_REVOKED",
                        message = "Token đã bị thu hồi."
                    });
                    return;
                }
            }

            // ===== Redis-first: Me =====
            MeResponse? me = null;
            if (_cache is not null)
            {
                me = await _cache.GetMeAsync(userId);
            }

            // ===== Mongo fallback =====
            if (me is null)
            {
                me = await BuildMeFromMongoAsync(userId);
                if (me is not null)
                {
                    // ✅ chỉ cache khi đã dựng được me chuẩn
                    await _cache.SetMeAsync(me);
                }
            }

            // ===== Claims fallback (last resort) =====
            if (me is null)
            {
                me = BuildMeFromClaims(context.User, userId);

                // claims fallback chỉ nên cache nếu muốn tối ưu,
                // nhưng đây là "last resort" nên vẫn cache để giảm hit.
                await _cache.SetMeAsync(me);
            }

            // ===== Active check =====
            if (me.IsDeleted)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "INACTIVE_USER",
                    message = "Tài khoản đang bị khóa."
                });
                return;
            }

            // ===== Set HttpContext.Items =====
            context.Items[MeItemKey] = me;

            // ===== Optional: fill UserContext for controller injection =====
            if (_userContext is not null)
            {
                _userContext.UserId = me.Id;
                _userContext.FullName = me.FullName;
                _userContext.UnitId = me.UnitId;
                _userContext.UnitName = me.UnitName;
                _userContext.UnitCode = me.UnitCode;
                _userContext.UnitTypeCodes = me.UnitTypeCodes;
                _userContext.Roles = me.Roles ?? new List<string>();
                _userContext.PositionCode = me.PositionCode;
            }

            await next(context);
        }

        private async Task<MeResponse?> BuildMeFromMongoAsync(string userId)
        {
            // 1) load user
            var entity = await _users
                .Find(x => x.Id == userId)
                .Project(x => new
                {
                    x.Id,
                    x.Username,
                    x.FullName,
                    x.UnitId,
                    x.Roles,
                    x.PositionCode,
                    x.IsDeleted
                })
                .FirstOrDefaultAsync();

            if (entity is null) return null;
            if (entity.UnitId is null) return null;

            var unit = await _units
                .Find(u => u.Id == entity.UnitId)
                .FirstOrDefaultAsync();

            // 2) active check sớm để khỏi resolve thừa
            // (middleware vẫn check lần cuối ở ngoài)
            if (entity.IsDeleted)
            {
                return new MeResponse(
                    entity.Id,
                    entity.Username,
                    entity.FullName,
                    new List<string>(),
                    entity.UnitId,
                    unit?.Symbol,
                    unit.ShortName,
                    unit.Code,
                    entity.Roles ?? new List<string>(),
                    entity.PositionCode,
                    entity.IsDeleted
                );
            }

            // 3) resolve unitTypeCodes from Unit -> UnitType
            var unitTypeCodes = new List<string>();

            if (unit?.UnitTypeCodes is { Count: > 0 })
            {
                unitTypeCodes = await _unitTypes
                    .Find(t => unit.UnitTypeCodes.Contains(t.Code) && !t.IsDeleted)
                    .Project(t => t.Code)
                    .ToListAsync();
            }

            return new MeResponse(
                entity.Id,
                entity.Username,
                entity.FullName,
                unitTypeCodes,
                entity.UnitId,
                unit.Symbol,
                unit.ShortName,
                unit.Code,
                entity.Roles ?? new List<string>(),
                entity.PositionCode,
                entity.IsDeleted
            );
        }

        private static MeResponse BuildMeFromClaims(ClaimsPrincipal user, string userId)
        {
            // ✅ claim "unitTypeCodes" (CSV). Nếu không có => empty (last resort)
            var unitTypeCsv = user.FindFirstValue("unitTypeCodes") ?? "";
            var unitTypeCodes = string.IsNullOrWhiteSpace(unitTypeCsv)
                ? new List<string>()
                : unitTypeCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            var rolesCsv = user.FindFirstValue("roles") ?? "";
            var roles = string.IsNullOrWhiteSpace(rolesCsv)
                ? new List<string>()
                : rolesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            var isDeletedStr = user.FindFirstValue("isDeleted") ?? "false";
            var isDeleted = string.Equals(isDeletedStr, "true", StringComparison.OrdinalIgnoreCase);

            return new MeResponse(
                id: userId,
                username: user.FindFirstValue("username") ?? "",
                fullName: user.FindFirstValue("fullName") ?? "",
                unitTypeCodes: unitTypeCodes,
                unitId: user.FindFirstValue("unitId") ?? "",
                unitSymbol: user.FindFirstValue("unitSymbol") ?? "",
                unitName: user.FindFirstValue("unitName") ?? "",
                unitCode: user.FindFirstValue("unitCode") ?? "",
                positionCode: user.FindFirstValue("positionCode") ?? "",
                roles: roles,
                isDeleted: isDeleted
            );
        }
    }
}