using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;

namespace tdtd_be.Common.Auth;

public static class WorkRoleGuard
{
    private const string ROLE_ADMIN = "ADMIN";
    private const string ROLE_SYSTEM_ADMIN = "SYSTEM_ADMIN";

    public static bool IsSystemAdmin(MeResponse me)
        => me.Roles.Any(r => string.Equals(r, ROLE_SYSTEM_ADMIN, StringComparison.OrdinalIgnoreCase));

    public static bool IsAdmin(MeResponse me)
        => me.Roles.Any(r => string.Equals(r, ROLE_ADMIN, StringComparison.OrdinalIgnoreCase));

    public static void RequireCanManageWork(MeResponse me)
    {
        if (IsSystemAdmin(me) || IsAdmin(me)) return;
        throw AppExceptionFactory.Forbidden(AppErrorCode.WORK_FORBIDDEN);
    }

    public static void RequireCanReadWork(MeResponse me)
    {
        if (IsSystemAdmin(me) || IsAdmin(me)) return;
        throw AppExceptionFactory.Forbidden(AppErrorCode.WORK_FORBIDDEN);
    }
}
