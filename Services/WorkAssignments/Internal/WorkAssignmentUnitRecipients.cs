using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Internal;

// Assignment-only expansion. Aggregate consumers retain the legacy service method.
internal static class WorkAssignmentUnitRecipients
{
    public static bool TryExpand(IReadOnlyCollection<Unit> selected, IReadOnlyCollection<Unit> candidates,
        out List<Unit> concrete, out string? error)
    {
        var result = new Dictionary<string, Unit>(StringComparer.Ordinal);
        error = null;
        foreach (var unit in selected)
        {
            if (unit.IsDeleted || !UnitManagementScope.Contains(unit.Code, unit.Code))
            { error = "Đơn vị nhận việc không còn hợp lệ hoặc mã đơn vị sai cấu trúc."; break; }
            if (!unit.IsVirtual) { result[unit.Id] = unit; continue; }
            var descendants = candidates.Where(x => !x.IsDeleted && !x.IsVirtual
                && x.Code?.StartsWith(unit.Code!, StringComparison.Ordinal) == true).ToList();
            if (descendants.Any(x => !UnitManagementScope.Contains(x.Code, x.Code)))
            { error = "Nhóm đã chọn có đơn vị mang mã sai cấu trúc. Cần xử lý trước khi giao việc."; break; }
            var members = descendants.Where(x => UnitManagementScope.Contains(unit.Code, x.Code, false)).ToList();
            if (members.Count == 0)
            { error = "Nhóm đơn vị đã chọn không có đơn vị nhận việc hợp lệ."; break; }
            foreach (var member in members) result[member.Id] = member;
        }
        concrete = error is null ? result.Values.ToList() : [];
        return error is null;
    }

    public static List<string> RequireSingleManagers(IReadOnlyCollection<string> unitIds, IEnumerable<AppUser> accounts)
    {
        var byUnit = accounts.Where(x => !x.IsDeleted && x.AccountKind == ManagementAccountKind.UnitManager && x.UnitId != null)
            .GroupBy(x => x.UnitId!).ToDictionary(x => x.Key, x => x.ToList());
        if (unitIds.Any(id => !byUnit.TryGetValue(id, out var managers) || managers.Count != 1))
            throw AppExceptionFactory.BadRequest(AppErrorCode.UNIT_MANAGER_MISSING,
                message: "Đơn vị đã chọn thiếu hoặc có nhiều tài khoản đại diện. Cần xác định đúng tài khoản đại diện trước khi giao việc.");
        return unitIds.Select(id => byUnit[id][0].Id).Distinct(StringComparer.Ordinal).ToList();
    }
}
