using System.Security.Claims;
using tdtd_be.Common.Cache;
using tdtd_be.DTOs.Auth;

namespace tdtd_be.Common.Middleware
{
    public sealed class MeContextRedisMiddleware : IMiddleware
    {
        public const string MeItemKey = "me";
        private readonly RedisUserCache _cache;

        public MeContextRedisMiddleware(RedisUserCache cache) => _cache = cache;

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if (context.User?.Identity?.IsAuthenticated != true)
            {
                await next(context);
                return;
            }

            var userId = context.User.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(userId))
            {
                await next(context);
                return;
            }

            // kill switch
            var tokenTvStr = context.User.FindFirstValue("tv") ?? "0";
            _ = long.TryParse(tokenTvStr, out var tokenTv);

            await _cache.EnsureTokenVersionAsync(userId);
            var currentTv = await _cache.GetTokenVersionAsync(userId);

            if (tokenTv < currentTv)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "TOKEN_REVOKED", message = "Token đã bị thu hồi." });
                return;
            }

            // cache first
            var me = await _cache.GetMeAsync(userId);
            if (me is null)
            {
                me = BuildMeFromClaims(context.User, userId);
                await _cache.SetMeAsync(me);
            }

            if (!me.IsActive)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "INACTIVE_USER", message = "Tài khoản đang bị khóa." });
                return;
            }

            context.Items[MeItemKey] = me;
            await next(context);
        }

        private static MeResponse BuildMeFromClaims(ClaimsPrincipal user, string userId)
        {
            var unitTypeCsv = user.FindFirstValue("unitTypeId") ?? "";
            var unitTypeId = string.IsNullOrWhiteSpace(unitTypeCsv)
                ? new List<string>()
                : unitTypeCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            var isActiveStr = user.FindFirstValue("isActive") ?? "true";
            var isActive = string.Equals(isActiveStr, "true", StringComparison.OrdinalIgnoreCase);
            var rolesCsv = user.FindFirstValue("roles") ?? "";
            var roles = string.IsNullOrWhiteSpace(rolesCsv)
                ? new List<string>()
                : rolesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .ToList();
            return new MeResponse(
                id: userId,
                username: user.FindFirstValue("username") ?? "",
                fullName: user.FindFirstValue("fullName") ?? "",
                unitTypeId: unitTypeId,
                unitId: user.FindFirstValue("unitId") ?? "",
                unitName: user.FindFirstValue("unitName") ?? "",
                roles: roles,
                isActive: isActive
            );
        }
    }
}
