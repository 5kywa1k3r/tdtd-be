using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;

namespace tdtd_be.Common.Auth;

public static class RoleGuard
{
    private const string AccountKindSystemAdmin = "SYSTEM_ADMIN";
    private const string AccountKindUnitManager = "UNIT_MANAGER";
    private const string AccountKindLevelManager = "LEVEL_MANAGER";
    private const string GeneratedUnitManagerPrefix = "mu_";
    private const string GeneratedLevelManagerPrefix = "ml_";

    public static bool Has(MeResponse me, string role)
        => me.Roles?.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase)) == true;

    public static bool IsAdmin(MeResponse me) => Has(me, Roles.ADMIN);
    public static bool IsSystemAdmin(MeResponse me)
        => Has(me, Roles.SYSTEM_ADMIN) ||
           string.Equals(me.AccountKind, AccountKindSystemAdmin, StringComparison.OrdinalIgnoreCase);

    public static bool IsManagerLevel(MeResponse me)
        => Has(me, Roles.MANAGER_LEVEL) ||
           string.Equals(me.AccountKind, AccountKindLevelManager, StringComparison.OrdinalIgnoreCase);

    public static bool TryGetGeneratedLevelManager(MeResponse me, out int level)
    {
        level = 0;
        if (!IsManagerLevel(me)) return false;

        var username = (me.Username ?? string.Empty).Trim().ToLowerInvariant();
        if (!username.StartsWith(GeneratedLevelManagerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var raw = username[GeneratedLevelManagerPrefix.Length..].Trim();
        return int.TryParse(raw, out level) && level >= 0;
    }

    public static bool IsGeneratedManagementAccount(MeResponse me)
    {
        if (me is null) return false;

        var username = (me.Username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(username)) return false;

        if (string.Equals(me.AccountKind, AccountKindUnitManager, StringComparison.OrdinalIgnoreCase))
            return username.StartsWith(GeneratedUnitManagerPrefix, StringComparison.OrdinalIgnoreCase);

        if (string.Equals(me.AccountKind, AccountKindLevelManager, StringComparison.OrdinalIgnoreCase))
            return username.StartsWith(GeneratedLevelManagerPrefix, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    public static bool TryGetManagerUnit(MeResponse me, out string unitId)
    {
        unitId = "";
        if (me.Roles is null) return false;
        foreach (var r in me.Roles)
            if (Roles.IsManagerUnit(r, out unitId)) return true;
        return false;
    }

    public static void RequireSystemAdmin(MeResponse me)
    {
        if (!IsSystemAdmin(me)) throw AppExceptionFactory.Forbidden(AppErrorCode.AUTH_SYSTEM_ADMIN_REQUIRED);
    }

    public static void RequireAdmin(MeResponse me)
    {
        if (!IsAdmin(me)) throw AppExceptionFactory.Forbidden(AppErrorCode.AUTH_ADMIN_REQUIRED);
    }

    public static void RequireAdminOrSystemAdmin(MeResponse me)
    {
        if (!IsAdmin(me) && !IsSystemAdmin(me))
            throw AppExceptionFactory.Forbidden(AppErrorCode.AUTH_ADMIN_OR_SYSTEM_ADMIN_REQUIRED);
    }
}
