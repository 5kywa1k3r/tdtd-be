using tdtd_be.DTOs.Auth;

namespace tdtd_be.Common.Auth;

public static class RoleGuard
{
    public static bool Has(MeResponse me, string role)
        => me.Roles?.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase)) == true;

    public static bool IsAdmin(MeResponse me) => Has(me, Roles.ADMIN);
    public static bool IsSystemAdmin(MeResponse me) => Has(me, Roles.SYSTEM_ADMIN);
    public static bool IsManagerLevel(MeResponse me) => Has(me, Roles.MANAGER_LEVEL);

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
        if (!IsSystemAdmin(me)) throw new BadHttpRequestException("SYSTEM_ADMIN required.");
    }

    public static void RequireAdmin(MeResponse me)
    {
        if (!IsAdmin(me)) throw new BadHttpRequestException("ADMIN required.");
    }

    // (optional helper) dùng cho nhiều nơi
    public static void RequireAdminOrSystemAdmin(MeResponse me)
    {
        if (!IsAdmin(me) && !IsSystemAdmin(me))
            throw new BadHttpRequestException("ADMIN or SYSTEM_ADMIN required.");
    }
}