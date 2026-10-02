using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Services;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentTargetScopeValidator
{
    public static void EnsureCanAssignTargets(
        AppUser actorUser,
        Unit? actorUnit,
        IReadOnlyCollection<AppUser> targetUsers,
        IReadOnlyDictionary<string, Unit> unitById,
        bool actorUnitHasAssignableDescendants,
        WorkAssignmentTargetScopePolicy? targetScopePolicy = null)
    {
        if (!IsUnitManager(actorUser))
            return;

        if (actorUnit is null)
            throw InvalidScope("actorUnitMissing", actorUser.Id, null, null);

        foreach (var targetUser in targetUsers)
        {
            if (string.Equals(actorUser.Id, targetUser.Id, StringComparison.Ordinal))
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.WORK_ASSIGNMENT_SELF_ASSIGNMENT_NOT_ALLOWED,
                    new { actorUserId = actorUser.Id });

            var reason = TargetRejectionReason(actorUnit, targetUser, unitById,
                actorUnitHasAssignableDescendants, targetScopePolicy);
            if (reason is not null)
                throw InvalidScope(reason, actorUser.Id, targetUser.Id, ResolveUnit(targetUser, unitById)?.Id);
        }
    }

    // Read-side filtering shares the write policy without throwing an exception per candidate.
    public static bool CanAssignTarget(AppUser actor, Unit? actorUnit, AppUser target,
        IReadOnlyDictionary<string, Unit> units, bool hasDescendants, WorkAssignmentTargetScopePolicy? policy = null)
        => !IsUnitManager(actor) || (actorUnit is not null && actor.Id != target.Id
            && TargetRejectionReason(actorUnit, target, units, hasDescendants, policy) is null);

    private static string? TargetRejectionReason(Unit actorUnit, AppUser targetUser,
        IReadOnlyDictionary<string, Unit> unitById, bool hasDescendants, WorkAssignmentTargetScopePolicy? policy)
    {
        var targetUnit = ResolveUnit(targetUser, unitById);
        if (IsUnitManager(targetUser))
            return targetUnit is not null && (IsPeerUnit(actorUnit, targetUnit) || IsDescendantUnit(actorUnit, targetUnit)
                || policy?.AllowsConfiguredTarget(actorUnit, targetUnit, targetUser) == true)
                ? null : "unitManagerOutsideAllowedUnit";
        if (IsNormalUser(targetUser))
            return targetUnit is not null && ((actorUnit.Id == targetUnit.Id
                    && (!hasDescendants || IsDepartmentLeader(targetUser)))
                || policy?.AllowsConfiguredTarget(actorUnit, targetUnit, targetUser) == true)
                ? null : "normalUserOutsideFinalUnit";
        return "unsupportedTargetAccountKind";
    }

    // A department's child teams do not prevent its unit account from assigning
    // its own chief/deputy. Position alone never grants access to another unit.
    private static bool IsDepartmentLeader(AppUser user)
        => user.PositionCode?.Trim().ToUpperInvariant() is
            "TRUONG_PHONG" or "PHO_TRUONG_PHONG" or "PHO_TRUONG_PHONG_PHU_TRACH";

    public static bool IsUnitManager(AppUser user)
        => string.Equals(user.AccountKind, ManagementAccountKind.UnitManager, StringComparison.OrdinalIgnoreCase) ||
           (user.Username ?? string.Empty).StartsWith(ManagementAccountConvention.UnitManagerPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsLevelManager(AppUser user)
        => string.Equals(user.AccountKind, ManagementAccountKind.LevelManager, StringComparison.OrdinalIgnoreCase) ||
           (user.Username ?? string.Empty).StartsWith(ManagementAccountConvention.LevelManagerPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsNormalUser(AppUser user)
        => !IsUnitManager(user) &&
           !IsLevelManager(user) &&
           (string.IsNullOrWhiteSpace(user.AccountKind) ||
            string.Equals(user.AccountKind, ManagementAccountKind.NormalUser, StringComparison.OrdinalIgnoreCase));

    private static Unit? ResolveUnit(AppUser user, IReadOnlyDictionary<string, Unit> unitById)
    {
        var unitId = user.UnitId?.Trim();
        if (string.IsNullOrWhiteSpace(unitId))
            return null;

        return unitById.TryGetValue(unitId, out var unit) ? unit : null;
    }

    private static bool IsPeerUnit(Unit actorUnit, Unit targetUnit)
    {
        if (string.Equals(actorUnit.Id, targetUnit.Id, StringComparison.Ordinal))
            return false;

        return actorUnit.Level == targetUnit.Level &&
               string.Equals(actorUnit.ParentUnitId ?? string.Empty, targetUnit.ParentUnitId ?? string.Empty, StringComparison.Ordinal);
    }

    private static bool IsDescendantUnit(Unit actorUnit, Unit targetUnit)
    {
        if (string.Equals(actorUnit.Id, targetUnit.Id, StringComparison.Ordinal))
            return false;

        var actorCode = actorUnit.Code?.Trim();
        var targetCode = targetUnit.Code?.Trim();
        if (!string.IsNullOrWhiteSpace(actorCode) &&
            !string.IsNullOrWhiteSpace(targetCode) &&
            targetUnit.Level > actorUnit.Level &&
            targetCode.StartsWith(actorCode, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(targetUnit.ParentUnitId, actorUnit.Id, StringComparison.Ordinal);
    }

    private static AppException InvalidScope(
        string reason,
        string? actorUserId,
        string? targetUserId,
        string? targetUnitId)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_ASSIGNEE_SCOPE_INVALID,
            new { reason, actorUserId, targetUserId, targetUnitId });
}
