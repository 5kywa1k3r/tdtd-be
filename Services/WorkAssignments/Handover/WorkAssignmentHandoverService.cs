using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Operations;
using tdtd_be.DTOs.Users;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services;
using tdtd_be.Services.Common;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.Works;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments.Runtime;
using tdtd_be.Services.Notifications;

namespace tdtd_be.Services.WorkAssignments.Handover;

public sealed class WorkAssignmentHandoverService : IWorkAssignmentHandoverService
{
    private readonly MongoDbContext _ctx;
    private readonly IDynamicFlowDefinitionTransactionRunner _transactions;
    private readonly IDocRoleService _docRole;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
    private readonly IWorkAssignmentStatusRepairService _statusRepair;
    private readonly IWorkStatusOperationLogService _statusLog;
    private readonly IUserActionLogService _userActionLog;
    private readonly INotificationService _notifications;
    private readonly IWorkPermissionService _workPermission;

    public WorkAssignmentHandoverService(
        MongoDbContext ctx,
        IDynamicFlowDefinitionTransactionRunner transactions,
        IDocRoleService docRole,
        IDocRoleReadModelProjectionService docRoleReadModelProjection,
        IWorkAssignmentStatusRepairService statusRepair,
        IWorkStatusOperationLogService statusLog,
        IUserActionLogService userActionLog,
        INotificationService notifications,
        IWorkPermissionService workPermission)
    {
        _ctx = ctx;
        _transactions = transactions;
        _docRole = docRole;
        _docRoleReadModelProjection = docRoleReadModelProjection;
        _statusRepair = statusRepair;
        _statusLog = statusLog;
        _userActionLog = userActionLog;
        _notifications = notifications;
        _workPermission = workPermission;
    }

    public async Task<WorkAssignmentHandoverResponse> HandoverAsync(
        string assignmentId,
        HandoverWorkAssignmentRequest request,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        var startedAtUtc = DateTime.UtcNow;
        var operationId = ObjectId.GenerateNewId().ToString();
        var stopwatch = Stopwatch.StartNew();
        var fromAssigneeUserId = NormalizeRequired(
            request.FromAssigneeUserId,
            AppErrorCode.WORK_ASSIGNMENT_HANDOVER_FROM_REQUIRED);
        var toAssigneeUserId = NormalizeRequired(
            request.ToAssigneeUserId,
            AppErrorCode.WORK_ASSIGNMENT_HANDOVER_TO_REQUIRED);
        var flowInstanceId = await _ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .Project(x => x.FlowInstanceId)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(flowInstanceId))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE,
                new
                {
                    assignmentId,
                    flowInstanceId,
                    command = "HANDOVER_ASSIGNMENT",
                    reason = "DYNAMIC_FLOW_PARTICIPANT_MUTATION_BLOCKED_UNTIL_P6",
                    blockedUntilPhase = "P6"
                });
        }

        try
        {
            var response = await ExecuteHandoverAsync(
                assignmentId,
                request,
                actorUserId,
                fromAssigneeUserId,
                toAssigneeUserId,
                operationId,
                ct);

            stopwatch.Stop();
            await WriteOperationLogAsync(
                result: "SUCCESS",
                operationId,
                startedAtUtc,
                stopwatch.ElapsedMilliseconds,
                assignmentId,
                response.Assignment.WorkId,
                actorUserId,
                fromAssigneeUserId,
                toAssigneeUserId,
                request,
                response,
                ex: null,
                ct);

            await _userActionLog.RecordAsync(new UserActionLogSeed
            {
                Action = UserActionLogActions.AssignmentHandover,
                Scope = "assignment",
                ActorUserId = actorUserId,
                WorkId = response.Assignment.WorkId,
                WorkAssignmentId = assignmentId,
                FromUserId = fromAssigneeUserId,
                ToUserId = toAssigneeUserId,
                TargetUserId = toAssigneeUserId,
                Summary = $"Handover assignment {response.Assignment.Code}",
                Data = new Dictionary<string, string>
                {
                    { "operationId", operationId },
                    { "periodCount", response.PeriodCount.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    { "reportCount", response.ReportCount.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    { "queueItemCount", response.QueueItemCount.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                },
                OccurredAtUtc = startedAtUtc
            }, CancellationToken.None);

            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            await WriteOperationLogAsync(
                result: "FAILED",
                operationId,
                startedAtUtc,
                stopwatch.ElapsedMilliseconds,
                assignmentId,
                workId: null,
                actorUserId,
                fromAssigneeUserId,
                toAssigneeUserId,
                request,
                response: null,
                ex,
                ct);

            throw;
        }
    }

    public async Task<PagedResult<WorkAssignmentHandoverHistoryRow>> SearchHistoryAsync(
        string workId,
        WorkAssignmentHandoverHistorySearchRequest request,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        if (string.IsNullOrWhiteSpace(workId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_REPORT_WORK_ID_REQUIRED);

        await _workPermission.EnsureCanReadAsync(workId, actorUserId, ct);

        var page = Math.Max(0, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var f = Builders<WorkAssignmentHandoverHistory>.Filter;
        var filter = f.Eq(x => x.WorkId, workId.Trim()) & f.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(request.WorkAssignmentId))
            filter &= f.Eq(x => x.WorkAssignmentId, request.WorkAssignmentId.Trim());

        var total = await _ctx.WorkAssignmentHandoverHistories.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.WorkAssignmentHandoverHistories
            .Find(filter)
            .Sort(Builders<WorkAssignmentHandoverHistory>.Sort.Descending(x => x.CreatedAtUtc))
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return new PagedResult<WorkAssignmentHandoverHistoryRow>(
            rows.Select(ToHistoryRow).ToList(),
            total,
            page,
            pageSize);
    }

    private async Task<WorkAssignmentHandoverResponse> ExecuteHandoverAsync(
        string assignmentId,
        HandoverWorkAssignmentRequest request,
        string actorUserId,
        string fromAssigneeUserId,
        string toAssigneeUserId,
        string operationId,
        CancellationToken ct)
    {
        if (!string.Equals(actorUserId, fromAssigneeUserId, StringComparison.Ordinal))
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_ACTOR_MISMATCH,
                new { assignmentId, actorUserId, fromAssigneeUserId });

        if (string.Equals(fromAssigneeUserId, toAssigneeUserId, StringComparison.Ordinal))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_TARGET_SAME_AS_SOURCE,
                new { assignmentId, fromAssigneeUserId, toAssigneeUserId });

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND,
                new { assignmentId });

        if (!string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE,
                new
                {
                    assignmentId,
                    flowInstanceId = assignment.FlowInstanceId,
                    command = "HANDOVER_ASSIGNMENT",
                    reason = "DYNAMIC_FLOW_PARTICIPANT_MUTATION_BLOCKED_UNTIL_P6",
                    blockedUntilPhase = "P6"
                });
        }

        if (!assignment.IsActive)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_INACTIVE_ASSIGNMENT,
                new { assignmentId });

        var currentAssignees = assignment.Assignees ?? new List<UserRef>();
        var fromAssignee = currentAssignees
            .FirstOrDefault(x => string.Equals(x.UserId, fromAssigneeUserId, StringComparison.Ordinal))
            ?? throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_SOURCE_NOT_IN_ASSIGNMENT,
                new { assignmentId, fromAssigneeUserId });

        if (currentAssignees.Any(x => string.Equals(x.UserId, toAssigneeUserId, StringComparison.Ordinal)))
            throw AppExceptionFactory.Create(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_TARGET_ALREADY_ASSIGNED,
                new { assignmentId, toAssigneeUserId });

        var users = await _ctx.Users
            .Find(x => (x.Id == fromAssigneeUserId || x.Id == toAssigneeUserId) && !x.IsDeleted)
            .ToListAsync(ct);

        var fromUser = users.FirstOrDefault(x => string.Equals(x.Id, fromAssigneeUserId, StringComparison.Ordinal))
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_SOURCE_USER_NOT_FOUND,
                new { assignmentId, fromAssigneeUserId });

        var toUser = users.FirstOrDefault(x => string.Equals(x.Id, toAssigneeUserId, StringComparison.Ordinal))
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_TARGET_USER_NOT_FOUND,
                new { assignmentId, toAssigneeUserId });

        var targetAssignee = (await WorkAssignmentUserHelper.BuildAssigneesAsync(
                _ctx,
                new List<string> { toAssigneeUserId },
                ct))
            .First();

        ValidateTransition(fromUser, toUser, fromAssignee, targetAssignee);

        var now = DateTime.UtcNow;
        var updatedAssignees = currentAssignees
            .Select(x => string.Equals(x.UserId, fromAssigneeUserId, StringComparison.Ordinal)
                ? CloneUserRef(targetAssignee)
                : CloneUserRef(x))
            .ToList();

        var mutation = await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var assignmentFilter =
                    Builders<WorkAssignment>.Filter.Eq(
                        x => x.Id,
                        assignment.Id) &
                    Builders<WorkAssignment>.Filter.Eq(
                        x => x.WorkId,
                        assignment.WorkId) &
                    Builders<WorkAssignment>.Filter.Eq(
                        x => x.UpdatedAtUtc,
                        assignment.UpdatedAtUtc) &
                    Builders<WorkAssignment>.Filter.Eq(
                        x => x.IsActive,
                        true) &
                    Builders<WorkAssignment>.Filter.Eq(
                        x => x.IsDeleted,
                        false) &
                    (Builders<WorkAssignment>.Filter.Eq(
                         x => x.FlowInstanceId,
                         null) |
                     Builders<WorkAssignment>.Filter.Exists(
                         x => x.FlowInstanceId,
                         false)) &
                    Builders<WorkAssignment>.Filter.ElemMatch(
                        x => x.Assignees,
                        x => x.UserId == fromAssigneeUserId) &
                    Builders<WorkAssignment>.Filter.Not(
                        Builders<WorkAssignment>.Filter.ElemMatch(
                            x => x.Assignees,
                            x => x.UserId == toAssigneeUserId));
                var currentAssignment = await _ctx.WorkAssignments
                    .Find(session, assignmentFilter)
                    .FirstOrDefaultAsync(transactionCt);
                if (currentAssignment is null ||
                    (currentAssignment.Assignees ?? new List<UserRef>())
                        .Count(item => string.Equals(
                            item.UserId,
                            fromAssigneeUserId,
                            StringComparison.Ordinal)) != 1)
                {
                    throw AppExceptionFactory.Create(
                        AppErrorCode.WORK_ASSIGNMENT_HANDOVER_ASSIGNMENT_CHANGED,
                        new
                        {
                            assignmentId,
                            fromAssigneeUserId,
                            toAssigneeUserId
                        });
                }

                await EnsureNoTargetLaneCollisionsAsync(
                    session,
                    currentAssignment,
                    toAssigneeUserId,
                    transactionCt);

                var bindings = await _ctx.WorkTemplateAssignees
                    .Find(
                        session,
                        item => item.WorkAssignmentId == currentAssignment.Id &&
                                item.WorkId == currentAssignment.WorkId &&
                                item.AssigneeUserId == fromAssigneeUserId &&
                                item.IsActive &&
                                !item.IsDeleted)
                    .ToListAsync(transactionCt);
                if (bindings.Count != 1)
                {
                    throw AppExceptionFactory.NotFound(
                        AppErrorCode.WORK_ASSIGNMENT_HANDOVER_BINDING_NOT_FOUND,
                        new { assignmentId, fromAssigneeUserId });
                }
                var binding = bindings[0];

                var periods = await _ctx.WorkReportPeriods
                    .Find(
                        session,
                        item => item.WorkAssignmentId == currentAssignment.Id &&
                                item.AssigneeUserId == fromAssigneeUserId &&
                                !item.IsDeleted)
                    .ToListAsync(transactionCt);
                var reports = await _ctx.WorkAssignmentReports
                    .Find(
                        session,
                        item => item.WorkAssignmentId == currentAssignment.Id &&
                                item.AssigneeUserId == fromAssigneeUserId &&
                                !item.IsDeleted)
                    .ToListAsync(transactionCt);
                var queueItems = await _ctx.WorkAssignmentQueueItems
                    .Find(
                        session,
                        item => item.WorkAssignmentId == currentAssignment.Id &&
                                item.AssigneeUserId == fromAssigneeUserId &&
                                !item.IsDeleted)
                    .ToListAsync(transactionCt);

                ValidateHandoverMembershipRows(
                    currentAssignment,
                    binding,
                    periods,
                    reports,
                    queueItems);
                EnsureLifecycleTargetUnitInScope(
                    currentAssignment,
                    periods,
                    reports,
                    targetAssignee);

                var assignmentResult = await _ctx.WorkAssignments.UpdateOneAsync(
                    session,
                    assignmentFilter,
                    Builders<WorkAssignment>.Update
                        .Set(x => x.Assignees, updatedAssignees)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId),
                    cancellationToken: transactionCt);
                EnsureExactCas(
                    assignmentResult,
                    1,
                    "P9_HANDOVER_ASSIGNMENT_CAS_LOST");

                var bindingResult = await _ctx.WorkTemplateAssignees.UpdateOneAsync(
                    session,
                    x => x.Id == binding.Id &&
                         x.WorkId == currentAssignment.WorkId &&
                         x.WorkAssignmentId == currentAssignment.Id &&
                         x.AssigneeUserId == fromAssigneeUserId &&
                         x.UpdatedAtUtc == binding.UpdatedAtUtc &&
                         x.IsActive &&
                         !x.IsDeleted,
                    BuildBindingAssigneeUpdate(
                        targetAssignee,
                        now,
                        actorUserId),
                    cancellationToken: transactionCt);
                EnsureExactCas(
                    bindingResult,
                    1,
                    "P9_HANDOVER_BINDING_CAS_LOST");

                await ApplyPeriodHandoverAsync(
                    session,
                    currentAssignment,
                    binding,
                    periods,
                    targetAssignee,
                    fromAssigneeUserId,
                    toAssigneeUserId,
                    actorUserId,
                    now,
                    transactionCt);
                await ApplyReportHandoverAsync(
                    session,
                    currentAssignment,
                    reports,
                    fromAssigneeUserId,
                    toAssigneeUserId,
                    actorUserId,
                    now,
                    transactionCt);
                await ApplyQueueHandoverAsync(
                    session,
                    currentAssignment,
                    queueItems,
                    fromAssigneeUserId,
                    toAssigneeUserId,
                    actorUserId,
                    now,
                    transactionCt);

                await WorkDirectSourceRevisionFence.IncrementAsync(
                    _ctx,
                    session,
                    currentAssignment.WorkId,
                    transactionCt);

                await _ctx.WorkAssignmentHandoverHistories.InsertOneAsync(
                    session,
                    new WorkAssignmentHandoverHistory
                    {
                        Id = operationId,
                        OperationId = operationId,
                        WorkId = currentAssignment.WorkId,
                        WorkAssignmentId = currentAssignment.Id,
                        AssignmentCode = currentAssignment.Code,
                        DynamicFormTemplateId = currentAssignment.DynamicFormTemplateId,
                        DynamicFormTemplateCode = currentAssignment.DynamicFormTemplateCode,
                        DynamicFormTemplateName = currentAssignment.DynamicFormTemplateName,
                        FromAssigneeUserId = fromAssigneeUserId,
                        ToAssigneeUserId = toAssigneeUserId,
                        ActorUserId = actorUserId,
                        FromAssignee = CloneUserRef(fromAssignee),
                        ToAssignee = CloneUserRef(targetAssignee),
                        Actor = CloneUserRef(fromAssignee),
                        Reason = NullIfWhiteSpace(request.Reason),
                        Comment = NullIfWhiteSpace(request.Comment),
                        WorkTemplateAssigneeId = binding.Id,
                        PeriodCount = periods.Count,
                        ReportCount = reports.Count,
                        QueueItemCount = queueItems.Count,
                        Result = "SUCCESS",
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        CreatedByUserId = actorUserId,
                        UpdatedByUserId = actorUserId,
                        IsDeleted = false
                    },
                    cancellationToken: transactionCt);

                currentAssignment.Assignees = updatedAssignees;
                currentAssignment.UpdatedAtUtc = now;
                currentAssignment.UpdatedByUserId = actorUserId;
                return new HandoverMutationResult(
                    currentAssignment,
                    binding.Id,
                    periods.Select(item => item.Id).ToArray(),
                    periods.Count,
                    reports.Count,
                    queueItems.Count);
            },
            ct);

        assignment = mutation.Assignment;
        var sourcePeriodIds = mutation.PeriodIds;
        var oldTemplateKeys = sourcePeriodIds.Count == 0
            ? new List<ReportTemplateKey>()
            : await _ctx.MyReportPeriodListDocRoles
                .Find(x => sourcePeriodIds.Contains(x.WorkReportPeriodId) && !x.IsDeleted)
                .Project(x => new ReportTemplateKey(
                    x.WorkId,
                    x.DynamicFormTemplateId,
                    x.UserId))
                .ToListAsync(ct);

        await _docRole.UpsertWorkAssignmentRolesAsync(assignment, ct);
        await _docRole.RebuildWorkParticipantRolesFromAssignmentsAsync(assignment.WorkId, actorUserId, ct);

        await _statusRepair.RebuildWorkTreeAsync(assignment.WorkId, ct);

        foreach (var periodId in sourcePeriodIds.Where(x => !string.IsNullOrWhiteSpace(x)))
            await _docRoleReadModelProjection.RebuildReportPeriodAsync(periodId, actorUserId, ct);

        foreach (var key in oldTemplateKeys
                     .Where(x => !string.IsNullOrWhiteSpace(x.DynamicFormTemplateId))
                     .Distinct())
            await _docRoleReadModelProjection.RebuildMyReportTemplateAsync(
                key.WorkId,
                key.DynamicFormTemplateId!,
                key.UserId,
                actorUserId,
                ct);

        await _notifications.NotifyAssignmentHandoverAsync(
            assignment,
            fromAssigneeUserId,
            toAssigneeUserId,
            operationId,
            actorUserId,
            ct);

        var detail = WorkAssignmentResponseMapper.ToResponse(
            assignment,
            hasData: sourcePeriodIds.Count > 0 || mutation.ReportCount > 0);

        var response = new WorkAssignmentHandoverResponse
        {
            Assignment = detail,
            FromAssigneeUserId = fromAssigneeUserId,
            ToAssigneeUserId = toAssigneeUserId,
            WorkTemplateAssigneeId = mutation.BindingId,
            PeriodCount = mutation.PeriodCount,
            ReportCount = mutation.ReportCount,
            QueueItemCount = mutation.QueueItemCount
        };

        return response;
    }

    private static WorkAssignmentHandoverHistoryRow ToHistoryRow(WorkAssignmentHandoverHistory x)
        => new(
            x.Id,
            x.WorkId,
            x.WorkAssignmentId,
            x.AssignmentCode,
            x.DynamicFormTemplateId,
            x.DynamicFormTemplateCode,
            x.DynamicFormTemplateName,
            ToUserRefDto(x.FromAssignee),
            ToUserRefDto(x.ToAssignee),
            ToUserRefDto(x.Actor),
            x.Reason,
            x.Comment,
            x.WorkTemplateAssigneeId,
            x.PeriodCount,
            x.ReportCount,
            x.QueueItemCount,
            x.Result,
            x.CreatedAtUtc);

    private static UserRefDTO? ToUserRefDto(UserRef? x)
        => x is null
            ? null
            : new UserRefDTO(
                x.UserId,
                x.Username,
                x.FullName,
                x.UnitId,
                x.UnitSymbol,
                x.UnitShortName,
                x.UnitName,
                x.PositionCode,
                x.PositionName);

    private async Task EnsureNoTargetLaneCollisionsAsync(
        IClientSessionHandle session,
        WorkAssignment assignment,
        string targetAssigneeUserId,
        CancellationToken ct)
    {
        var assignmentId = assignment.Id;

        var targetBindingExists = await _ctx.WorkTemplateAssignees
            .Find(
                session,
                x => x.WorkAssignmentId == assignmentId &&
                     x.AssigneeUserId == targetAssigneeUserId &&
                     !x.IsDeleted)
            .AnyAsync(ct);

        if (targetBindingExists)
            throw HandoverCollision(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_TARGET_BINDING_EXISTS,
                assignment,
                targetAssigneeUserId);

        if (!string.IsNullOrWhiteSpace(assignment.DynamicFormTemplateId))
        {
            var targetActiveTemplateBindingExists = await _ctx.WorkTemplateAssignees
                .Find(
                    session,
                    x => x.WorkId == assignment.WorkId &&
                         x.DynamicFormTemplateId == assignment.DynamicFormTemplateId &&
                         x.AssigneeUserId == targetAssigneeUserId &&
                         x.IsActive &&
                         !x.IsDeleted)
                .AnyAsync(ct);

            if (targetActiveTemplateBindingExists)
                throw HandoverCollision(
                    AppErrorCode.WORK_ASSIGNMENT_HANDOVER_TARGET_TEMPLATE_BINDING_EXISTS,
                    assignment,
                    targetAssigneeUserId);
        }

        var targetPeriodExists = await _ctx.WorkReportPeriods
            .Find(
                session,
                x => x.WorkAssignmentId == assignmentId &&
                     x.AssigneeUserId == targetAssigneeUserId &&
                     !x.IsDeleted)
            .AnyAsync(ct);

        if (targetPeriodExists)
            throw HandoverCollision(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_TARGET_PERIOD_EXISTS,
                assignment,
                targetAssigneeUserId);

        var targetReportExists = await _ctx.WorkAssignmentReports
            .Find(
                session,
                x => x.WorkAssignmentId == assignmentId &&
                     x.AssigneeUserId == targetAssigneeUserId &&
                     !x.IsDeleted)
            .AnyAsync(ct);

        if (targetReportExists)
            throw HandoverCollision(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_TARGET_REPORT_EXISTS,
                assignment,
                targetAssigneeUserId);

        var targetQueueExists = await _ctx.WorkAssignmentQueueItems
            .Find(
                session,
                x => x.WorkAssignmentId == assignmentId &&
                     x.AssigneeUserId == targetAssigneeUserId &&
                     !x.IsDeleted)
            .AnyAsync(ct);

        if (targetQueueExists)
            throw HandoverCollision(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_TARGET_QUEUE_EXISTS,
                assignment,
                targetAssigneeUserId);
    }

    private static void ValidateHandoverMembershipRows(
        WorkAssignment assignment,
        WorkTemplateAssignee binding,
        IReadOnlyList<WorkReportPeriod> periods,
        IReadOnlyList<WorkAssignmentReport> reports,
        IReadOnlyList<WorkAssignmentQueueItem> queueItems)
    {
        if (!binding.IsActive ||
            !string.Equals(binding.WorkId, assignment.WorkId, StringComparison.Ordinal) ||
            !string.Equals(binding.WorkAssignmentId, assignment.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "P9_HANDOVER_BINDING_OWNER_INVALID");
        }

        var periodById = periods
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.Ordinal);
        var reportById = reports
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.Ordinal);

        foreach (var period in periods)
        {
            if (!string.Equals(period.WorkId, assignment.WorkId, StringComparison.Ordinal) ||
                !string.Equals(period.WorkAssignmentId, assignment.Id, StringComparison.Ordinal) ||
                !string.Equals(period.WorkTemplateAssigneeId, binding.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "P9_HANDOVER_PERIOD_OWNER_INVALID");
            }

            if (!string.IsNullOrWhiteSpace(period.CurrentReportId))
            {
                if (!reportById.TryGetValue(period.CurrentReportId, out var currentReport) ||
                    !currentReport.IsCurrent ||
                    !currentReport.IsActive ||
                    !string.Equals(
                        currentReport.WorkReportPeriodId,
                        period.Id,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "P9_HANDOVER_CURRENT_REPORT_OWNER_INVALID");
                }
            }

            if (!string.IsNullOrWhiteSpace(period.SourceLifecycleReportId))
            {
                if (!reportById.TryGetValue(
                        period.SourceLifecycleReportId,
                        out var lifecycleReport) ||
                    !string.Equals(
                        lifecycleReport.WorkReportPeriodId,
                        period.Id,
                        StringComparison.Ordinal) ||
                    lifecycleReport.LifecycleRevision !=
                    period.SourceLifecycleRevision)
                {
                    throw new InvalidOperationException(
                        "P9_HANDOVER_LIFECYCLE_REPORT_OWNER_INVALID");
                }
            }
        }

        foreach (var report in reports)
        {
            if (!string.Equals(report.WorkId, assignment.WorkId, StringComparison.Ordinal) ||
                !string.Equals(report.WorkAssignmentId, assignment.Id, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(report.WorkReportPeriodId) ||
                !periodById.ContainsKey(report.WorkReportPeriodId))
            {
                throw new InvalidOperationException(
                    "P9_HANDOVER_REPORT_OWNER_INVALID");
            }
        }

        foreach (var queueItem in queueItems)
        {
            if (!string.Equals(queueItem.WorkId, assignment.WorkId, StringComparison.Ordinal) ||
                !string.Equals(queueItem.WorkAssignmentId, assignment.Id, StringComparison.Ordinal) ||
                !periods.Any(period => string.Equals(
                    period.PeriodKey,
                    queueItem.PeriodKey,
                    StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "P9_HANDOVER_QUEUE_OWNER_INVALID");
            }
        }
    }

    private static void EnsureLifecycleTargetUnitInScope(
        WorkAssignment assignment,
        IReadOnlyCollection<WorkReportPeriod> periods,
        IReadOnlyCollection<WorkAssignmentReport> reports,
        UserRef targetAssignee)
    {
        var hasLifecycleAuthority = periods.Any(period =>
                !string.IsNullOrWhiteSpace(period.CurrentReportId) ||
                !string.IsNullOrWhiteSpace(period.SourceLifecycleReportId) ||
                period.SourceLifecycleRevision > 0 ||
                period.SourceLifecycleAppliedAtUtc.HasValue ||
                period.HistoricalDataApproved ||
                period.HistoricalDataApprovedAtUtc.HasValue) ||
            reports.Any(report =>
                report.IsCurrent ||
                report.Status == WorkAssignmentReportStatus.Approved ||
                report.LifecycleRevision > 0);
        if (!hasLifecycleAuthority)
            return;

        var targetUnitId = NullIfWhiteSpace(targetAssignee.UnitId);
        var targetUnitIds = (assignment.TargetUnitIds ?? new List<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.Ordinal);
        if (targetUnitId is not null && targetUnitIds.Contains(targetUnitId))
            return;

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_HANDOVER_UNIT_MISMATCH,
            new
            {
                assignmentId = assignment.Id,
                targetAssigneeUserId = targetAssignee.UserId,
                targetUnitId,
                targetUnitIds = targetUnitIds.OrderBy(value => value, StringComparer.Ordinal),
                reason = "P9_HANDOVER_TARGET_UNIT_OUT_OF_SCOPE"
            });
    }

    private async Task ApplyPeriodHandoverAsync(
        IClientSessionHandle session,
        WorkAssignment assignment,
        WorkTemplateAssignee binding,
        IReadOnlyList<WorkReportPeriod> periods,
        UserRef targetAssignee,
        string fromAssigneeUserId,
        string toAssigneeUserId,
        string actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        if (periods.Count == 0)
            return;

        var writes = periods
            .Select(period => new UpdateOneModel<WorkReportPeriod>(
                Builders<WorkReportPeriod>.Filter.Eq(x => x.Id, period.Id) &
                Builders<WorkReportPeriod>.Filter.Eq(x => x.WorkId, assignment.WorkId) &
                Builders<WorkReportPeriod>.Filter.Eq(x => x.WorkAssignmentId, assignment.Id) &
                Builders<WorkReportPeriod>.Filter.Eq(x => x.WorkTemplateAssigneeId, binding.Id) &
                Builders<WorkReportPeriod>.Filter.Eq(x => x.AssigneeUserId, fromAssigneeUserId) &
                Builders<WorkReportPeriod>.Filter.Eq(x => x.UpdatedAtUtc, period.UpdatedAtUtc) &
                Builders<WorkReportPeriod>.Filter.Eq(x => x.Status, period.Status) &
                Builders<WorkReportPeriod>.Filter.Eq(x => x.CurrentReportId, period.CurrentReportId) &
                Builders<WorkReportPeriod>.Filter.Eq(x => x.SourceLifecycleReportId, period.SourceLifecycleReportId) &
                Builders<WorkReportPeriod>.Filter.Eq(x => x.SourceLifecycleRevision, period.SourceLifecycleRevision) &
                Builders<WorkReportPeriod>.Filter.Eq(x => x.IsDeleted, false),
                Builders<WorkReportPeriod>.Update
                    .Set(x => x.AssigneeUserId, toAssigneeUserId)
                    .Set(x => x.AssigneeUnitId, NullIfWhiteSpace(targetAssignee.UnitId))
                    .Set(x => x.WorkTemplateAssigneeId, binding.Id)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId)))
            .Cast<WriteModel<WorkReportPeriod>>()
            .ToList();
        var result = await _ctx.WorkReportPeriods.BulkWriteAsync(
            session,
            writes,
            new BulkWriteOptions { IsOrdered = true },
            ct);
        EnsureBulkCas(result, periods.Count, "P9_HANDOVER_PERIOD_CAS_LOST");
    }

    private async Task ApplyReportHandoverAsync(
        IClientSessionHandle session,
        WorkAssignment assignment,
        IReadOnlyList<WorkAssignmentReport> reports,
        string fromAssigneeUserId,
        string toAssigneeUserId,
        string actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        if (reports.Count == 0)
            return;

        var writes = reports
            .Select(report => new UpdateOneModel<WorkAssignmentReport>(
                Builders<WorkAssignmentReport>.Filter.Eq(x => x.Id, report.Id) &
                Builders<WorkAssignmentReport>.Filter.Eq(x => x.WorkId, assignment.WorkId) &
                Builders<WorkAssignmentReport>.Filter.Eq(x => x.WorkAssignmentId, assignment.Id) &
                Builders<WorkAssignmentReport>.Filter.Eq(x => x.WorkReportPeriodId, report.WorkReportPeriodId) &
                Builders<WorkAssignmentReport>.Filter.Eq(x => x.AssigneeUserId, fromAssigneeUserId) &
                Builders<WorkAssignmentReport>.Filter.Eq(x => x.UpdatedAtUtc, report.UpdatedAtUtc) &
                Builders<WorkAssignmentReport>.Filter.Eq(x => x.Status, report.Status) &
                Builders<WorkAssignmentReport>.Filter.Eq(x => x.LifecycleRevision, report.LifecycleRevision) &
                Builders<WorkAssignmentReport>.Filter.Eq(x => x.PayloadRevision, report.PayloadRevision) &
                Builders<WorkAssignmentReport>.Filter.Eq(x => x.PayloadHash, report.PayloadHash) &
                Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsCurrent, report.IsCurrent) &
                Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsActive, report.IsActive) &
                Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false),
                Builders<WorkAssignmentReport>.Update
                    .Set(x => x.AssigneeUserId, toAssigneeUserId)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId)))
            .Cast<WriteModel<WorkAssignmentReport>>()
            .ToList();
        var result = await _ctx.WorkAssignmentReports.BulkWriteAsync(
            session,
            writes,
            new BulkWriteOptions { IsOrdered = true },
            ct);
        EnsureBulkCas(result, reports.Count, "P9_HANDOVER_REPORT_CAS_LOST");
    }

    private async Task ApplyQueueHandoverAsync(
        IClientSessionHandle session,
        WorkAssignment assignment,
        IReadOnlyList<WorkAssignmentQueueItem> queueItems,
        string fromAssigneeUserId,
        string toAssigneeUserId,
        string actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        if (queueItems.Count == 0)
            return;

        var writes = queueItems
            .Select(queueItem => new UpdateOneModel<WorkAssignmentQueueItem>(
                Builders<WorkAssignmentQueueItem>.Filter.Eq(x => x.Id, queueItem.Id) &
                Builders<WorkAssignmentQueueItem>.Filter.Eq(x => x.WorkId, assignment.WorkId) &
                Builders<WorkAssignmentQueueItem>.Filter.Eq(x => x.WorkAssignmentId, assignment.Id) &
                Builders<WorkAssignmentQueueItem>.Filter.Eq(x => x.AssigneeUserId, fromAssigneeUserId) &
                Builders<WorkAssignmentQueueItem>.Filter.Eq(x => x.UpdatedAtUtc, queueItem.UpdatedAtUtc) &
                Builders<WorkAssignmentQueueItem>.Filter.Eq(x => x.IsActive, queueItem.IsActive) &
                Builders<WorkAssignmentQueueItem>.Filter.Eq(x => x.IsDeleted, false),
                Builders<WorkAssignmentQueueItem>.Update
                    .Set(x => x.AssigneeUserId, toAssigneeUserId)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId)))
            .Cast<WriteModel<WorkAssignmentQueueItem>>()
            .ToList();
        var result = await _ctx.WorkAssignmentQueueItems.BulkWriteAsync(
            session,
            writes,
            new BulkWriteOptions { IsOrdered = true },
            ct);
        EnsureBulkCas(result, queueItems.Count, "P9_HANDOVER_QUEUE_CAS_LOST");
    }

    private static void EnsureExactCas(
        UpdateResult result,
        long expected,
        string reason)
    {
        if (result.MatchedCount != expected || result.ModifiedCount != expected)
            throw new InvalidOperationException(reason);
    }

    private static void EnsureBulkCas<TDocument>(
        BulkWriteResult<TDocument> result,
        long expected,
        string reason)
    {
        if (result.MatchedCount != expected || result.ModifiedCount != expected)
            throw new InvalidOperationException(reason);
    }

    private static void ValidateTransition(
        AppUser fromUser,
        AppUser toUser,
        UserRef fromAssignee,
        UserRef toAssignee)
    {
        var fromIsUnitManager = IsUnitManager(fromUser);
        var toIsUnitManager = IsUnitManager(toUser);
        var fromIsNormal = IsNormalUser(fromUser);
        var toIsNormal = IsNormalUser(toUser);

        if (fromIsUnitManager && toIsUnitManager)
            return;

        if ((fromIsUnitManager && toIsNormal) ||
            (fromIsNormal && toIsNormal) ||
            (fromIsNormal && toIsUnitManager))
        {
            EnsureSameUnit(fromUser, toUser, fromAssignee, toAssignee);
            return;
        }

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_HANDOVER_TRANSITION_INVALID,
            new
            {
                fromUserId = fromUser.Id,
                toUserId = toUser.Id,
                fromAccountKind = fromUser.AccountKind,
                toAccountKind = toUser.AccountKind
            });
    }

    private static bool IsUnitManager(AppUser user)
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

    private static void EnsureSameUnit(
        AppUser fromUser,
        AppUser toUser,
        UserRef fromAssignee,
        UserRef toAssignee)
    {
        var fromUnitId = NullIfWhiteSpace(fromUser.UnitId) ?? NullIfWhiteSpace(fromAssignee.UnitId);
        var toUnitId = NullIfWhiteSpace(toUser.UnitId) ?? NullIfWhiteSpace(toAssignee.UnitId);

        if (string.IsNullOrWhiteSpace(fromUnitId) ||
            string.IsNullOrWhiteSpace(toUnitId) ||
            !string.Equals(fromUnitId, toUnitId, StringComparison.Ordinal))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_UNIT_MISMATCH,
                new
                {
                    fromUserId = fromUser.Id,
                    toUserId = toUser.Id,
                    fromUnitId,
                    toUnitId
                });
        }
    }

    private static UpdateDefinition<WorkTemplateAssignee> BuildBindingAssigneeUpdate(
        UserRef assignee,
        DateTime now,
        string actorUserId)
    {
        return Builders<WorkTemplateAssignee>.Update
            .Set(x => x.AssigneeUserId, assignee.UserId)
            .Set(x => x.AssigneeUsername, assignee.Username ?? string.Empty)
            .Set(x => x.AssigneeFullName, assignee.FullName ?? string.Empty)
            .Set(x => x.AssigneeUnitId, NullIfWhiteSpace(assignee.UnitId))
            .Set(x => x.AssigneeUnitSymbol, assignee.UnitSymbol)
            .Set(x => x.AssigneeUnitShortName, assignee.UnitShortName)
            .Set(x => x.AssigneeUnitName, assignee.UnitName)
            .Set(x => x.IsActive, true)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);
    }

    private async Task WriteOperationLogAsync(
        string result,
        string operationId,
        DateTime startedAtUtc,
        long durationMs,
        string assignmentId,
        string? workId,
        string actorUserId,
        string fromAssigneeUserId,
        string toAssigneeUserId,
        HandoverWorkAssignmentRequest request,
        WorkAssignmentHandoverResponse? response,
        Exception? ex,
        CancellationToken ct)
    {
        var summary =
            $"fromAssigneeUserId={fromAssigneeUserId};toAssigneeUserId={toAssigneeUserId};" +
            $"periods={response?.PeriodCount ?? 0};reports={response?.ReportCount ?? 0};queueRows={response?.QueueItemCount ?? 0};" +
            $"operationId={operationId};reason={TrimForLog(request.Reason)};comment={TrimForLog(request.Comment)}";

        await _statusLog.WriteAsync(new WorkStatusOperationLog
        {
            Operation = "ASSIGNMENT_HANDOVER",
            Scope = "work-assignment",
            Result = result,
            WorkId = workId,
            WorkAssignmentId = assignmentId,
            ActorUserId = actorUserId,
            Summary = summary,
            ErrorType = ex?.GetType().FullName,
            ErrorMessage = ex?.Message,
            ErrorStackTrace = ex?.ToString(),
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = DateTime.UtcNow,
            DurationMs = durationMs
        }, ct);
    }

    private static string NormalizeRequired(string? value, AppErrorCode code)
    {
        var normalized = NullIfWhiteSpace(value);
        if (normalized is null)
            throw AppExceptionFactory.BadRequest(code);

        return normalized;
    }

    private static void EnsureActor(string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            throw AppExceptionFactory.Unauthorized(AppErrorCode.WORK_ASSIGNMENT_ACTOR_REQUIRED);
    }

    private static AppException HandoverCollision(
        AppErrorCode code,
        WorkAssignment assignment,
        string targetAssigneeUserId)
        => AppExceptionFactory.Create(
            code,
            new
            {
                assignmentId = assignment.Id,
                assignment.WorkId,
                assignment.DynamicFormTemplateId,
                targetAssigneeUserId
            });

    private static UserRef CloneUserRef(UserRef input)
    {
        return new UserRef
        {
            UserId = input.UserId,
            Username = input.Username ?? string.Empty,
            FullName = input.FullName ?? string.Empty,
            UnitId = NullIfWhiteSpace(input.UnitId),
            UnitSymbol = input.UnitSymbol,
            UnitShortName = input.UnitShortName,
            UnitName = input.UnitName,
            PositionCode = input.PositionCode,
            PositionName = input.PositionName
        };
    }

    private static string TrimForLog(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().Replace('\r', ' ').Replace('\n', ' ');
        return normalized.Length <= 200 ? normalized : normalized[..200];
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record HandoverMutationResult(
        WorkAssignment Assignment,
        string BindingId,
        IReadOnlyList<string> PeriodIds,
        long PeriodCount,
        long ReportCount,
        long QueueItemCount);

    private sealed record ReportTemplateKey(string WorkId, string? DynamicFormTemplateId, string UserId);
}
