using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Pickers;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.WorkAssignments;

public sealed partial class WorkAssignmentService
{
    public async Task<AssignmentPickerScope> GetPickerScopeAsync(PickerContext context, CancellationToken ct)
    {
        // Identity is exclusively taken from the authenticated session, never the request.
        var actorId = _me.RequireMe().Id;
        if (context.Purpose is not (PickerPurpose.AssignmentRecipients or PickerPurpose.AssignmentWatchers)
            || !ObjectId.TryParse(context.WorkId, out _))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_WORK_NOT_FOUND);

        var work = await _lookup.LoadWorkAsync(context.WorkId!, ct);
        if (!string.IsNullOrWhiteSpace(context.ParentAssignmentId) && !ObjectId.TryParse(context.ParentAssignmentId, out _))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_PARENT_NOT_FOUND);
        var parent = await _lookup.LoadParentAsync(context.ParentAssignmentId, work.Id, ct);
        WorkAssignmentCreateScopeGuard.EnsureCanCreateWithinScope(work, parent, actorId, Array.Empty<string>());
        await EnsureCreateScopeOpenAsync(work, parent, actorId, ct);
        if (!string.IsNullOrWhiteSpace(parent?.FlowInstanceId)
            || await _ctx.DynamicFlowInstances.Find(x => x.WorkId == work.Id && !x.IsDeleted).AnyAsync(ct))
            throw AppExceptionFactory.Create(AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE);

        var units = await _ctx.Units.Find(x => !x.IsDeleted).ToListAsync(ct);
        var actor = await _ctx.Users.Find(x => x.Id == actorId && !x.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.Unauthorized(AppErrorCode.WORK_ASSIGNMENT_ACTOR_REQUIRED);
        if (parent is not null && !WorkAssignmentCurrentAuthority.CanLeadChild(actor))
            throw AppExceptionFactory.Forbidden(AppErrorCode.WORK_ASSIGNMENT_BRANCH_CREATE_FORBIDDEN);
        var unitMap = units.ToDictionary(x => x.Id, StringComparer.Ordinal);
        unitMap.TryGetValue(actor.UnitId ?? "", out var actorUnit);
        if (actorUnit is null || !ObjectId.TryParse(actor.UnitId, out _))
            throw AppExceptionFactory.Unauthorized(AppErrorCode.WORK_ASSIGNMENT_ACTOR_REQUIRED);
        var hasDescendants = actorUnit is not null && await HasAssignableDescendantUnitAsync(actorUnit, ct);
        bool Allowed(AppUser user) => user.Id != actorId && ObjectId.TryParse(user.UnitId, out _)
            && unitMap.ContainsKey(user.UnitId!) && WorkAssignmentTargetScopeValidator.CanAssignTarget(
                actor, actorUnit, user, unitMap, hasDescendants, _targetScopePolicy)
            && (parent is null || WorkAssignmentTargetScopeValidator.IsUnitManager(actor)
                || WorkAssignmentUnitHierarchy.IsStrictAncestor(actorUnit, unitMap[user.UnitId!], unitMap));
        // Query only units that can contain a valid target. Prefix/peer/configured-type rules
        // are evaluated once per unit, not via thousands of thrown user-validation exceptions.
        // Own-unit eligibility also depends on PositionCode. Include that unit in
        // the coarse query; Allowed below still checks every actual account.
        var candidateUnitIds = units.Where(unit => unit.Id == actorUnit.Id || new[] { ManagementAccountKind.UnitManager,
                ManagementAccountKind.NormalUser, ManagementAccountKind.LevelManager }.Any(kind =>
                Allowed(new AppUser { Id = "picker-candidate", UnitId = unit.Id, AccountKind = kind })))
            .Select(unit => unit.Id).ToList();
        var users = await _ctx.Users.Find(x => !x.IsDeleted && x.UnitId != null && candidateUnitIds.Contains(x.UnitId))
            .Project(x => new AppUser { Id = x.Id, Username = x.Username, FullName = x.FullName, UnitId = x.UnitId,
                PositionCode = x.PositionCode, AccountKind = x.AccountKind }).ToListAsync(ct);
        var allowedUsers = users.Where(Allowed).ToList();
        var allowedUserIds = allowedUsers.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);

        // Validate ALL submitted context IDs, including when the operation is only a lookup.
        var selectedUnitIds = NormalizePickerIds(context.AssigneeUnitIds);
        var selectedUserIds = NormalizePickerIds(context.AssigneeUserIds);
        var managers = await _unitSelection.ResolveUnitManagerUserIdsAsync(selectedUnitIds, ct);
        var recipientIds = selectedUserIds.Concat(managers.Where(x => x != actorId)).Distinct(StringComparer.Ordinal).ToList();
        var recipients = await WorkAssignmentUserHelper.BuildAssigneesAsync(_ctx, recipientIds, ct);
        await EnsureAssignmentTargetsAllowedAsync(actorId, recipients, ct);
        if (recipientIds.Any(id => !allowedUserIds.Contains(id)))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_ASSIGNEE_SCOPE_INVALID);

        if (context.Purpose == PickerPurpose.AssignmentWatchers)
        {
            var recipientUnits = recipients.Select(x => x.UnitId).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet();
            // Same unit condition as BuildLeaderWatchersAsync, including empty => no candidates.
            var watchers = await _ctx.Users.Find(x => !x.IsDeleted && recipientUnits.Contains(x.UnitId)
                    && Positions.KnownCodes.Contains(x.PositionCode!))
                .Project(x => new AppUser { Id = x.Id, Username = x.Username, FullName = x.FullName,
                    UnitId = x.UnitId, PositionCode = x.PositionCode }).ToListAsync(ct);
            return new AssignmentPickerScope(units, watchers, new HashSet<string>());
        }

        // Match UnitSelectionService: real units -> UNIT_MANAGER; virtual units -> every
        // concrete descendant, missing manager => unselectable, auto-expanded self is omitted.
        var managersByUnit = users.Where(x => x.AccountKind == ManagementAccountKind.UnitManager && x.UnitId != null)
            .GroupBy(x => x.UnitId!).ToDictionary(x => x.Key, x => x.ToList());
        var selectable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            var concrete = unit.IsVirtual
                ? units.Where(x => !x.IsVirtual && !string.IsNullOrWhiteSpace(unit.Code)
                    && x.Code != null && x.Code.StartsWith(unit.Code.Trim(), StringComparison.Ordinal)).ToList()
                : new List<Unit> { unit };
            if (concrete.Count == 0 || concrete.Any(x => !managersByUnit.ContainsKey(x.Id))) continue;
            var targets = concrete.SelectMany(x => managersByUnit[x.Id]).Where(x => x.Id != actorId).ToList();
            if (targets.Count > 0 && targets.All(x => allowedUserIds.Contains(x.Id))) selectable.Add(unit.Id);
        }
        return new AssignmentPickerScope(units, allowedUsers, selectable);
    }

    private static List<string> NormalizePickerIds(IEnumerable<string>? values)
    {
        var ids = (values ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (ids.Count > 2000 || ids.Any(x => !ObjectId.TryParse(x, out _)))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_ASSIGNEE_SCOPE_INVALID);
        return ids;
    }

    private async Task EnsureChildTargetsBelowActorAsync(string actorId, IReadOnlyCollection<UserRef> targets, CancellationToken ct)
    {
        var actor = await _ctx.Users.Find(x => x.Id == actorId && !x.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.Unauthorized(AppErrorCode.WORK_ASSIGNMENT_ACTOR_REQUIRED);
        if (WorkAssignmentTargetScopeValidator.IsUnitManager(actor)) return;
        if (!WorkAssignmentCurrentAuthority.CanLeadChild(actor))
            throw AppExceptionFactory.Forbidden(AppErrorCode.WORK_ASSIGNMENT_BRANCH_CREATE_FORBIDDEN);
        var units = (await _ctx.Units.Find(x => !x.IsDeleted).ToListAsync(ct))
            .ToDictionary(x => x.Id, StringComparer.Ordinal);
        if (actor.UnitId is null || !units.TryGetValue(actor.UnitId, out var actorUnit) ||
            targets.Count == 0 || targets.Any(x => x.UnitId is null ||
                !units.TryGetValue(x.UnitId, out var targetUnit) ||
                !WorkAssignmentUnitHierarchy.IsStrictAncestor(actorUnit, targetUnit, units)))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_ASSIGNEE_SCOPE_INVALID);
    }
}
