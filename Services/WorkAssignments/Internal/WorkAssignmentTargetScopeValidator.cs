using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Services;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentTargetScopeValidator
{
    public static void EnsureCanAssignTargets(AppUser actor, Unit? actorUnit,
        IReadOnlyCollection<AppUser> targets, IReadOnlyDictionary<string, Unit> units,
        bool actorUnitHasAssignableDescendants, WorkAssignmentTargetScopePolicy? targetScopePolicy = null, bool rejectOverlap = true)
    {
        foreach (var target in targets)
        {
            if (actor.Id == target.Id)
                throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_SELF_ASSIGNMENT_NOT_ALLOWED);
            if (!CanAssignTarget(actor, actorUnit, target, units, actorUnitHasAssignableDescendants, targetScopePolicy))
                throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_ASSIGNEE_SCOPE_INVALID,
                    new { reason = "recipientOutsideManagementScope" },
                    "Tài khoản nhận việc hoặc mã đơn vị không hợp lệ, hoặc người nhận nằm ngoài phạm vi được giao. Hãy kiểm tra lại lựa chọn.");
        }
        if (!rejectOverlap) return;
        var conflicts = FindOverlaps(targets, units);
        if (conflicts.Count == 0) return;
        var first = conflicts.First();
        var person = targets.First(x => x.Id == first.Key);
        throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_ASSIGNEE_SCOPE_INVALID,
            new { reason = "unitPersonalOverlap", conflicts = conflicts.Select(x => new {
                userId = x.Key, unitId = x.Value.Id, unitName = x.Value.ShortName ?? x.Value.FullName }) },
            $"{person.FullName} ({person.Username}) thuộc phạm vi của {first.Value.ShortName ?? first.Value.FullName} đã được chọn nhận việc. Bỏ người này hoặc bỏ đơn vị đó để tiếp tục.");
    }

    // Branch creation authority is checked separately by the caller.
    public static bool CanAssignTarget(AppUser actor, Unit? actorUnit, AppUser target,
        IReadOnlyDictionary<string, Unit> units, bool hasDescendants, WorkAssignmentTargetScopePolicy? policy = null)
    {
        if (actor.IsDeleted || target.IsDeleted || actor.Id == target.Id || !ValidUnit(actorUnit)
            || !units.TryGetValue(target.UnitId ?? "", out var targetUnit) || !ValidUnit(targetUnit)) return false;
        if (IsPersonalRecipient(target))
            return UnitManagementScope.Contains(actorUnit!.Code, targetUnit.Code);
        if (!IsUnitRecipient(target)) return false;
        // Retain the existing unit-recipient policy for non-unit-account actors.
        if (!IsUnitManager(actor)) return true;
        return IsPeerUnit(actorUnit!, targetUnit)
            || UnitManagementScope.Contains(actorUnit!.Code, targetUnit.Code, includeSelf: false)
            || policy?.AllowsConfiguredTargetAccountKind(actorUnit, targetUnit, ManagementAccountKind.UnitManager) == true;
    }

    public static bool ValidUnit(Unit? unit) => unit is { IsDeleted: false, IsVirtual: false }
        && UnitManagementScope.Contains(unit.Code, unit.Code);
    public static bool IsUnitRecipient(AppUser user) => RecipientKind(user) == ManagementAccountKind.UnitManager;
    public static bool IsPersonalRecipient(AppUser user) => RecipientKind(user) == ManagementAccountKind.NormalUser;
    // Explicit account kind takes precedence over legacy username conventions.
    private static string RecipientKind(AppUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.AccountKind)) return user.AccountKind.Trim().ToUpperInvariant();
        if (IsUnitManager(user)) return ManagementAccountKind.UnitManager;
        if (IsLevelManager(user)) return ManagementAccountKind.LevelManager;
        return ManagementAccountKind.NormalUser;
    }

    public static Dictionary<string, Unit> FindOverlaps(IEnumerable<AppUser> recipients,
        IReadOnlyDictionary<string, Unit> units)
    {
        var users = recipients.ToList();
        var roots = users.Where(IsUnitRecipient).Select(x => units.GetValueOrDefault(x.UnitId ?? ""))
            .Where(ValidUnit).GroupBy(x => x!.Code!, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First()!, StringComparer.Ordinal);
        var conflicts = new Dictionary<string, Unit>(StringComparer.Ordinal);
        foreach (var person in users.Where(IsPersonalRecipient))
        {
            if (!units.TryGetValue(person.UnitId ?? "", out var unit) || !ValidUnit(unit)) continue;
            for (var length = 3; length <= unit.Code!.Length; length += 3)
                if (roots.TryGetValue(unit.Code[..length], out var covering))
                { conflicts[person.Id] = covering; break; }
        }
        return conflicts;
    }

    // These actor helpers are shared with handover; retain their existing semantics.
    public static bool IsUnitManager(AppUser user)
        => string.Equals(user.AccountKind, ManagementAccountKind.UnitManager, StringComparison.OrdinalIgnoreCase)
           || (user.Username ?? "").StartsWith(ManagementAccountConvention.UnitManagerPrefix, StringComparison.OrdinalIgnoreCase);
    public static bool IsLevelManager(AppUser user)
        => string.Equals(user.AccountKind, ManagementAccountKind.LevelManager, StringComparison.OrdinalIgnoreCase)
           || (user.Username ?? "").StartsWith(ManagementAccountConvention.LevelManagerPrefix, StringComparison.OrdinalIgnoreCase);
    private static bool IsPeerUnit(Unit actor, Unit target) => actor.Id != target.Id
        && actor.Code!.Length == target.Code!.Length && actor.Code[..^3] == target.Code[..^3];
}
