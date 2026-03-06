using tdtd_be.DTOs.Auth;

namespace tdtd_be.Common.Auth;

public static class WorkRoleGuard
{
    // TODO: thay đúng role codes của nếu khác
    private const string ROLE_ADMIN = "ADMIN";
    private const string ROLE_SYSTEM_ADMIN = "SYSTEM_ADMIN";

    public static bool IsSystemAdmin(MeResponse me)
        => me.Roles.Any(r => string.Equals(r, ROLE_SYSTEM_ADMIN, StringComparison.OrdinalIgnoreCase));

    public static bool IsAdmin(MeResponse me)
        => me.Roles.Any(r => string.Equals(r, ROLE_ADMIN, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// B1: quyền tạo/sửa/xóa work tối thiểu: ADMIN hoặc SYSTEM_ADMIN.
    /// Sau B2/B3 sẽ mở rộng theo leader/assignee.
    /// </summary>
    public static void RequireCanManageWork(MeResponse me)
    {
        if (IsSystemAdmin(me) || IsAdmin(me)) return;
        throw new BadHttpRequestException("WORK: forbidden.");
    }

    /// <summary>
    /// B1: quyền xem work: tạm cho ADMIN/SYS.
    /// Sau B2/B3: mở rộng leaderDirective/leaderWatch/assignees.
    /// </summary>
    public static void RequireCanReadWork(MeResponse me)
    {
        if (IsSystemAdmin(me) || IsAdmin(me)) return;
        throw new BadHttpRequestException("WORK: forbidden.");
    }
}