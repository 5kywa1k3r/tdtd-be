using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;

namespace tdtd_be.Common.Auth;

public static class UnitManagementScope
{
    public static bool Contains(string? root, string? code, bool includeSelf = true)
        => !string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(code)
           && root.Length % 3 == 0 && code.Length % 3 == 0
           && root.All(char.IsAsciiDigit) && code.All(char.IsAsciiDigit)
           && code.StartsWith(root, StringComparison.Ordinal)
           && (includeSelf || code.Length > root.Length);

    // Null scope is reserved for global administrators.
    public static async Task<string?> ResolveAsync(MongoDbContext ctx, MeResponse me, CancellationToken ct)
    {
        if (RoleGuard.IsAdmin(me) || RoleGuard.IsSystemAdmin(me)) return null;
        if (!RoleGuard.TryGetManagerUnit(me, out var unitId))
            throw AppExceptionFactory.Forbidden(AppErrorCode.UNIT_SCOPE_FORBIDDEN);
        var unit = await ctx.Units.Find(x => x.Id == unitId && !x.IsDeleted && !x.IsVirtual).FirstOrDefaultAsync(ct);
        if (unit is null || !Contains(unit.Code, unit.Code))
            throw AppExceptionFactory.Forbidden(AppErrorCode.UNIT_SCOPE_FORBIDDEN);
        return unit.Code;
    }

    public static void Require(string? scope, string? code, bool includeSelf = true)
    {
        if (scope is not null && !Contains(scope, code, includeSelf))
            throw AppExceptionFactory.Forbidden(AppErrorCode.UNIT_SCOPE_FORBIDDEN);
    }
}
