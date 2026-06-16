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

            var targetUnit = ResolveUnit(targetUser, unitById);

            if (IsUnitManager(targetUser))
            {
                if (targetUnit is not null &&
                    (IsPeerUnit(actorUnit, targetUnit) ||
                     IsDescendantUnit(actorUnit, targetUnit) ||
                     targetScopePolicy?.AllowsConfiguredTarget(actorUnit, targetUnit, targetUser) == true))
                {
                    continue;
                }

                throw InvalidScope("unitManagerOutsideAllowedUnit", actorUser.Id, targetUser.Id, targetUnit?.Id);
            }

            if (IsNormalUser(targetUser))
            {
                if (!actorUnitHasAssignableDescendants &&
                    targetUnit is not null &&
                    string.Equals(actorUnit.Id, targetUnit.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                if (targetUnit is not null &&
                    targetScopePolicy?.AllowsConfiguredTarget(actorUnit, targetUnit, targetUser) == true)
                {
                    continue;
                }

                throw InvalidScope("normalUserOutsideFinalUnit", actorUser.Id, targetUser.Id, targetUnit?.Id);
            }

            throw InvalidScope("unsupportedTargetAccountKind", actorUser.Id, targetUser.Id, targetUnit?.Id);
        }
    }

    public static bool IsUnitManager(AppUser user)
        => string.Equals(user.AccountKind, ManagementAccountKind.UnitManager, StringComparison.OrdinalIgnoreCase) ||
           (user.Username ?? string.Empty).StartsWith(ManagementAccountConvention.UnitManagerPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsLevelManager(AppUser user)
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
