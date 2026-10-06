using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Pickers;
using tdtd_be.DTOs.Pickers;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.WorkAssignments;

public sealed partial class WorkAssignmentService
{
    public async Task<AssignmentPickerScope> GetPickerScopeAsync(PickerContext context, CancellationToken ct,
        ScopedPickerRequest? query = null)
    {
        var actorId = _me.RequireMe().Id;
        if (context.Purpose is not (PickerPurpose.AssignmentRecipients or PickerPurpose.AssignmentWatchers)
            || !ObjectId.TryParse(context.WorkId, out _))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_WORK_NOT_FOUND);
        var work = await _lookup.LoadWorkAsync(context.WorkId!, ct);
        if (!string.IsNullOrWhiteSpace(context.ParentAssignmentId) && !ObjectId.TryParse(context.ParentAssignmentId, out _))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_PARENT_NOT_FOUND);
        var parent = await _lookup.LoadParentAsync(context.ParentAssignmentId, work.Id, ct);
        WorkAssignmentCreateScopeGuard.EnsureCanCreateWithinScope(work, parent, actorId, []);
        await EnsureCreateScopeOpenAsync(work, parent, actorId, ct);
        if (!string.IsNullOrWhiteSpace(parent?.FlowInstanceId)
            || await _ctx.DynamicFlowInstances.Find(x => x.WorkId == work.Id && !x.IsDeleted).AnyAsync(ct))
            throw AppExceptionFactory.Create(AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE);
        var actor = await _ctx.Users.Find(x => x.Id == actorId && !x.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.Unauthorized(AppErrorCode.WORK_ASSIGNMENT_ACTOR_REQUIRED);
        if (parent is not null && !WorkAssignmentCurrentAuthority.CanLeadChild(actor))
            throw AppExceptionFactory.Forbidden(AppErrorCode.WORK_ASSIGNMENT_BRANCH_CREATE_FORBIDDEN);
        var actorUnit = await _ctx.Units.Find(x => x.Id == actor.UnitId && !x.IsDeleted).FirstOrDefaultAsync(ct);
        if (!WorkAssignmentTargetScopeValidator.ValidUnit(actorUnit))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_ASSIGNEE_SCOPE_INVALID,
                message: "Đơn vị của tài khoản đang đăng nhập không hợp lệ hoặc mã đơn vị sai cấu trúc.");

        var selectedUnits = NormalizePickerIds(context.AssigneeUnitIds);
        var selectedUsers = NormalizePickerIds(context.AssigneeUserIds);
        var managers = await _unitSelection.ResolveUnitManagerUserIdsAsync(selectedUnits, ct);
        var recipientIds = selectedUsers.Concat(managers).Distinct(StringComparer.Ordinal).ToList();
        var recipients = await WorkAssignmentUserHelper.BuildAssigneesAsync(_ctx, recipientIds, ct);
        // A conflicting draft remains readable and removable. The command always rejects it.
        await EnsureAssignmentTargetsAllowedAsync(actorId, recipients, ct,
            rejectOverlap: context.Purpose == PickerPurpose.AssignmentWatchers);
        if (parent is not null) await EnsureChildTargetsBelowActorAsync(actorId, recipients, ct);
        var selectedAccounts = recipientIds.Count == 0 ? new List<AppUser>()
            : await _ctx.Users.Find(x => recipientIds.Contains(x.Id) && !x.IsDeleted)
                .Project(x => new AppUser { Id = x.Id, UnitId = x.UnitId, AccountKind = x.AccountKind, Username = x.Username })
                .ToListAsync(ct);

        var isUserQuery = query?.Operation is "users" or "lookup" or "restoreUsers";
        List<Unit> units;
        if (isUserQuery && context.Purpose == PickerPurpose.AssignmentRecipients)
        {
            var f = Builders<Unit>.Filter;
            // Search only the actor's subtree plus selected unit accounts, never the whole directory per keystroke.
            var selectedUnitIds = selectedAccounts.Select(x => x.UnitId).ToList();
            units = await _ctx.Units.Find(f.Eq(x => x.IsDeleted, false) &
                (f.Regex(x => x.Code, new BsonRegularExpression("^" + actorUnit!.Code)) | f.In(x => x.Id, selectedUnitIds!)))
                .ToListAsync(ct);
        }
        else if (isUserQuery && context.Purpose == PickerPurpose.AssignmentWatchers)
        {
            var ids = recipients.Select(x => x.UnitId).Append(actor.UnitId).Distinct().ToList();
            units = await _ctx.Units.Find(x => !x.IsDeleted && ids.Contains(x.Id)).ToListAsync(ct);
        }
        else if (query?.Operation is "restoreUnits" or "expandUnits")
        {
            var ids = NormalizePickerIds(query.Ids);
            var requested = await _ctx.Units.Find(x => !x.IsDeleted && ids.Contains(x.Id)).ToListAsync(ct);
            var groups = requested.Where(x => x.IsVirtual && UnitManagementScope.Contains(x.Code, x.Code)).ToList();
            var f = Builders<Unit>.Filter;
            var descendants = groups.Count == 0 ? new List<Unit>() : await _ctx.Units.Find(
                f.Eq(x => x.IsDeleted, false) & f.Eq(x => x.IsVirtual, false)
                & f.Or(groups.Select(g => f.Regex(x => x.Code, new BsonRegularExpression("^" + g.Code)))))
                .ToListAsync(ct);
            units = requested.Concat(descendants).DistinctBy(x => x.Id).ToList();
        }
        else units = await _ctx.Units.Find(x => !x.IsDeleted).ToListAsync(ct);
        var unitMap = units.ToDictionary(x => x.Id, StringComparer.Ordinal);
        bool Allowed(AppUser user) => WorkAssignmentTargetScopeValidator.CanAssignTarget(actor, actorUnit, user,
            unitMap, false, _targetScopePolicy) && (parent is null || ChildTargetAllowed(actor, actorUnit!, user, unitMap));

        if (context.Purpose == PickerPurpose.AssignmentWatchers)
        {
            var recipientUnitIds = recipients.Select(x => x.UnitId).Where(x => x != null).ToList();
            var filter = Builders<AppUser>.Filter.Where(x => !x.IsDeleted && recipientUnitIds.Contains(x.UnitId)
                && Positions.KnownCodes.Contains(x.PositionCode!));
            var (watchers, count) = await QueryPickerUsersAsync(filter, query, ct);
            return new(units, watchers, []) { UserTotalRows = count };
        }

        var personalUnitIds = units.Where(x => WorkAssignmentTargetScopeValidator.ValidUnit(x)
            && UnitManagementScope.Contains(actorUnit!.Code, x.Code)).Select(x => x.Id).ToList();
        var uf = Builders<AppUser>.Filter;
        // Match explicit NORMAL_USER or the legacy blank kind without manager prefixes.
        var personalKind = uf.Eq(x => x.AccountKind, ManagementAccountKind.NormalUser)
            | ((uf.Eq(x => x.AccountKind, null) | uf.Eq(x => x.AccountKind, ""))
                & uf.Not(uf.Regex(x => x.Username, new BsonRegularExpression("^(mu_|ml_)", "i"))));
        var personalFilter = uf.Eq(x => x.IsDeleted, false) & uf.Ne(x => x.Id, actorId)
            & uf.In(x => x.UnitId, personalUnitIds) & personalKind;
        var (people, total) = isUserQuery ? await QueryPickerUsersAsync(personalFilter, query, ct)
            : (new List<AppUser>(), (long?)null);
        var conflicts = WorkAssignmentTargetScopeValidator.FindOverlaps(selectedAccounts.Concat(people), unitMap);
        if (isUserQuery) return new(units, people, []) { UserConflicts = conflicts, UserTotalRows = total };

        // Only unit account queries are needed to build the directory; do not load all personnel.
        var managerUnitIds = units.Where(x => WorkAssignmentTargetScopeValidator.ValidUnit(x)).Select(x => x.Id).ToList();
        var unitAccounts = await _ctx.Users.Find(x => !x.IsDeleted && x.AccountKind == ManagementAccountKind.UnitManager
            && x.UnitId != null && managerUnitIds.Contains(x.UnitId))
            .Project(x => new AppUser { Id = x.Id, UnitId = x.UnitId, AccountKind = x.AccountKind, Username = x.Username })
            .ToListAsync(ct);
        var byUnit = unitAccounts.Where(x => x.UnitId != null).GroupBy(x => x.UnitId!)
            .ToDictionary(x => x.Key, x => x.ToList());
        var selectable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            if (!UnitManagementScope.Contains(unit.Code, unit.Code)) continue;
            if (!WorkAssignmentUnitRecipients.TryExpand([unit], units, out var concrete, out _)) continue;
            if (concrete.Count == 0 || concrete.Any(x => !byUnit.TryGetValue(x.Id, out var accounts)
                || accounts.Count != 1 || !Allowed(accounts[0]))) continue;
            selectable.Add(unit.Id);
        }
        // Synthetic normal users only convey navigable units; they are never returned as user rows.
        var navigation = personalUnitIds.Select(id => new AppUser { Id = "navigation", UnitId = id }).ToList();
        return new(units, navigation, selectable);
    }

    private async Task<(List<AppUser> Users, long? Total)> QueryPickerUsersAsync(FilterDefinition<AppUser> filter,
        ScopedPickerRequest? query, CancellationToken ct)
    {
        var f = Builders<AppUser>.Filter;
        if (!string.IsNullOrWhiteSpace(query?.UnitId)) filter &= f.Eq(x => x.UnitId, query.UnitId.Trim());
        var q = query?.Q?.Trim() ?? "";
        if (query?.Operation == "restoreUsers") filter &= f.In(x => x.Id, NormalizePickerIds(query.Ids));
        else if (query?.Operation == "lookup") filter &= f.Regex(x => x.Username, new BsonRegularExpression("^" + Regex.Escape(q) + "$", "i"));
        else if (q.Length > 0) filter &= f.Regex(x => x.Username, new BsonRegularExpression(Regex.Escape(q), "i"))
            | f.Regex(x => x.FullName, new BsonRegularExpression(Regex.Escape(q), "i"));
        var total = await _ctx.Users.CountDocumentsAsync(filter, cancellationToken: ct);
        IFindFluent<AppUser, AppUser> find = _ctx.Users.Find(filter).SortBy(x => x.Username).ThenBy(x => x.Id);
        if (query?.Operation != "restoreUsers") find = find.Skip((int)Math.Min((long)Math.Max(0, query?.Page ?? 0)
            * Math.Clamp(query?.PageSize ?? 20, 1, 50), int.MaxValue)).Limit(Math.Clamp(query?.PageSize ?? 20, 1, 50));
        var users = await find.Project(x => new AppUser { Id = x.Id, Username = x.Username, FullName = x.FullName,
            UnitId = x.UnitId, PositionCode = x.PositionCode, AccountKind = x.AccountKind }).ToListAsync(ct);
        return (users, total);
    }

    private static List<string> NormalizePickerIds(IEnumerable<string>? values)
    {
        var ids = (values ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (ids.Count > 2000 || ids.Any(x => !ObjectId.TryParse(x, out _)))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_ASSIGNEE_SCOPE_INVALID);
        return ids;
    }

    private static bool ChildTargetAllowed(AppUser actor, Unit actorUnit, AppUser target,
        IReadOnlyDictionary<string, Unit> units) => units.TryGetValue(target.UnitId ?? "", out var unit)
        && (WorkAssignmentTargetScopeValidator.IsPersonalRecipient(target)
            ? UnitManagementScope.Contains(actorUnit.Code, unit.Code)
            : WorkAssignmentTargetScopeValidator.IsUnitManager(actor)
                || UnitManagementScope.Contains(actorUnit.Code, unit.Code, false));

    private async Task EnsureChildTargetsBelowActorAsync(string actorId, IReadOnlyCollection<UserRef> targets, CancellationToken ct)
    {
        var actor = await _ctx.Users.Find(x => x.Id == actorId && !x.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.Unauthorized(AppErrorCode.WORK_ASSIGNMENT_ACTOR_REQUIRED);
        if (!WorkAssignmentCurrentAuthority.CanLeadChild(actor))
            throw AppExceptionFactory.Forbidden(AppErrorCode.WORK_ASSIGNMENT_BRANCH_CREATE_FORBIDDEN);
        var userIds = targets.Select(x => x.UserId).ToList();
        if (userIds.Count == 0) return;
        var accounts = await _ctx.Users.Find(x => userIds.Contains(x.Id) && !x.IsDeleted).ToListAsync(ct);
        var ids = accounts.Select(x => x.UnitId).Append(actor.UnitId).Distinct().ToList();
        var units = (await _ctx.Units.Find(x => ids.Contains(x.Id) && !x.IsDeleted).ToListAsync(ct))
            .ToDictionary(x => x.Id, StringComparer.Ordinal);
        if (!units.TryGetValue(actor.UnitId ?? "", out var actorUnit) || accounts.Count != userIds.Count
            || accounts.Any(x => !ChildTargetAllowed(actor, actorUnit, x, units)))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_ASSIGNEE_SCOPE_INVALID);
    }
}
