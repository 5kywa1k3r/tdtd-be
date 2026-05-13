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
using tdtd_be.Services;
using tdtd_be.Services.Common;
using tdtd_be.Services.Works;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments.Runtime;
using tdtd_be.Services.Notifications;

namespace tdtd_be.Services.WorkAssignments.Handover;

public sealed class WorkAssignmentHandoverService : IWorkAssignmentHandoverService
{
    private readonly MongoDbContext _ctx;
    private readonly IDocRoleService _docRole;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
    private readonly IWorkAssignmentStatusRepairService _statusRepair;
    private readonly IWorkStatusOperationLogService _statusLog;
    private readonly IUserActionLogService _userActionLog;
    private readonly INotificationService _notifications;
    private readonly IWorkPermissionService _workPermission;

    public WorkAssignmentHandoverService(
        MongoDbContext ctx,
        IDocRoleService docRole,
        IDocRoleReadModelProjectionService docRoleReadModelProjection,
        IWorkAssignmentStatusRepairService statusRepair,
        IWorkStatusOperationLogService statusLog,
        IUserActionLogService userActionLog,
        INotificationService notifications,
        IWorkPermissionService workPermission)
    {
        _ctx = ctx;
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

        await EnsureNoTargetLaneCollisionsAsync(assignment, toAssigneeUserId, ct);

        var binding = await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkAssignmentId == assignment.Id &&
                x.AssigneeUserId == fromAssigneeUserId &&
                !x.IsDeleted)
            .Sort(Builders<WorkTemplateAssignee>.Sort
                .Descending(x => x.IsActive)
                .Descending(x => x.UpdatedAtUtc))
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_BINDING_NOT_FOUND,
                new { assignmentId, fromAssigneeUserId });

        var sourcePeriodIds = await _ctx.WorkReportPeriods
            .Find(x =>
                x.WorkAssignmentId == assignment.Id &&
                x.AssigneeUserId == fromAssigneeUserId &&
                !x.IsDeleted)
            .Project(x => x.Id)
            .ToListAsync(ct);

        var oldTemplateKeys = sourcePeriodIds.Count == 0
            ? new List<ReportTemplateKey>()
            : await _ctx.MyReportPeriodListDocRoles
                .Find(x => sourcePeriodIds.Contains(x.WorkReportPeriodId) && !x.IsDeleted)
                .Project(x => new ReportTemplateKey(x.WorkId, x.DynamicFormTemplateId, x.UserId))
                .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var updatedAssignees = currentAssignees
            .Select(x => string.Equals(x.UserId, fromAssigneeUserId, StringComparison.Ordinal)
                ? CloneUserRef(targetAssignee)
                : CloneUserRef(x))
            .ToList();

        var assignmentFilter = Builders<WorkAssignment>.Filter.Eq(x => x.Id, assignment.Id)
                               & Builders<WorkAssignment>.Filter.Eq(x => x.IsDeleted, false)
                               & Builders<WorkAssignment>.Filter.ElemMatch(
                                   x => x.Assignees,
                                   x => x.UserId == fromAssigneeUserId)
                               & Builders<WorkAssignment>.Filter.Not(
                                   Builders<WorkAssignment>.Filter.ElemMatch(
                                       x => x.Assignees,
                                       x => x.UserId == toAssigneeUserId));

        var assignmentUpdate = Builders<WorkAssignment>.Update
            .Set(x => x.Assignees, updatedAssignees)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);

        var assignmentResult = await _ctx.WorkAssignments.UpdateOneAsync(
            assignmentFilter,
            assignmentUpdate,
            cancellationToken: ct);

        if (assignmentResult.MatchedCount == 0)
            throw AppExceptionFactory.Create(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_ASSIGNMENT_CHANGED,
                new { assignmentId, fromAssigneeUserId, toAssigneeUserId });

        var bindingResult = await _ctx.WorkTemplateAssignees.UpdateOneAsync(
            x => x.Id == binding.Id &&
                 x.AssigneeUserId == fromAssigneeUserId &&
                 !x.IsDeleted,
            BuildBindingAssigneeUpdate(targetAssignee, now, actorUserId),
            cancellationToken: ct);

        if (bindingResult.MatchedCount == 0)
            throw AppExceptionFactory.Create(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_BINDING_CHANGED,
                new { assignmentId, bindingId = binding.Id, fromAssigneeUserId });

        var periodResult = await _ctx.WorkReportPeriods.UpdateManyAsync(
            x => x.WorkAssignmentId == assignment.Id &&
                 x.AssigneeUserId == fromAssigneeUserId &&
                 !x.IsDeleted,
            Builders<WorkReportPeriod>.Update
                .Set(x => x.AssigneeUserId, toAssigneeUserId)
                .Set(x => x.AssigneeUnitId, NullIfWhiteSpace(targetAssignee.UnitId))
                .Set(x => x.WorkTemplateAssigneeId, binding.Id)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        var reportResult = await _ctx.WorkAssignmentReports.UpdateManyAsync(
            x => x.WorkAssignmentId == assignment.Id &&
                 x.AssigneeUserId == fromAssigneeUserId &&
                 !x.IsDeleted,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.AssigneeUserId, toAssigneeUserId)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        var queueResult = await _ctx.WorkAssignmentQueueItems.UpdateManyAsync(
            x => x.WorkAssignmentId == assignment.Id &&
                 x.AssigneeUserId == fromAssigneeUserId &&
                 !x.IsDeleted,
            Builders<WorkAssignmentQueueItem>.Update
                .Set(x => x.AssigneeUserId, toAssigneeUserId)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        assignment.Assignees = updatedAssignees;
        assignment.UpdatedAtUtc = now;
        assignment.UpdatedByUserId = actorUserId;

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
            hasData: sourcePeriodIds.Count > 0 || reportResult.MatchedCount > 0);

        var response = new WorkAssignmentHandoverResponse
        {
            Assignment = detail,
            FromAssigneeUserId = fromAssigneeUserId,
            ToAssigneeUserId = toAssigneeUserId,
            WorkTemplateAssigneeId = binding.Id,
            PeriodCount = periodResult.ModifiedCount,
            ReportCount = reportResult.ModifiedCount,
            QueueItemCount = queueResult.ModifiedCount
        };

        await _ctx.WorkAssignmentHandoverHistories.InsertOneAsync(
            new WorkAssignmentHandoverHistory
            {
                OperationId = operationId,
                WorkId = assignment.WorkId,
                WorkAssignmentId = assignment.Id,
                AssignmentCode = assignment.Code,
                DynamicFormTemplateId = assignment.DynamicFormTemplateId,
                DynamicFormTemplateCode = assignment.DynamicFormTemplateCode,
                DynamicFormTemplateName = assignment.DynamicFormTemplateName,
                FromAssigneeUserId = fromAssigneeUserId,
                ToAssigneeUserId = toAssigneeUserId,
                ActorUserId = actorUserId,
                FromAssignee = CloneUserRef(fromAssignee),
                ToAssignee = CloneUserRef(targetAssignee),
                Actor = CloneUserRef(fromAssignee),
                Reason = NullIfWhiteSpace(request.Reason),
                Comment = NullIfWhiteSpace(request.Comment),
                WorkTemplateAssigneeId = binding.Id,
                PeriodCount = periodResult.ModifiedCount,
                ReportCount = reportResult.ModifiedCount,
                QueueItemCount = queueResult.ModifiedCount,
                Result = "SUCCESS",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = actorUserId,
                UpdatedByUserId = actorUserId,
                IsDeleted = false
            },
            cancellationToken: ct);

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
        WorkAssignment assignment,
        string targetAssigneeUserId,
        CancellationToken ct)
    {
        var assignmentId = assignment.Id;

        var targetBindingExists = await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkAssignmentId == assignmentId &&
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
                .Find(x =>
                    x.WorkId == assignment.WorkId &&
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
            .Find(x =>
                x.WorkAssignmentId == assignmentId &&
                x.AssigneeUserId == targetAssigneeUserId &&
                !x.IsDeleted)
            .AnyAsync(ct);

        if (targetPeriodExists)
            throw HandoverCollision(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_TARGET_PERIOD_EXISTS,
                assignment,
                targetAssigneeUserId);

        var targetReportExists = await _ctx.WorkAssignmentReports
            .Find(x =>
                x.WorkAssignmentId == assignmentId &&
                x.AssigneeUserId == targetAssigneeUserId &&
                !x.IsDeleted)
            .AnyAsync(ct);

        if (targetReportExists)
            throw HandoverCollision(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_TARGET_REPORT_EXISTS,
                assignment,
                targetAssigneeUserId);

        var targetQueueExists = await _ctx.WorkAssignmentQueueItems
            .Find(x =>
                x.WorkAssignmentId == assignmentId &&
                x.AssigneeUserId == targetAssigneeUserId &&
                !x.IsDeleted)
            .AnyAsync(ct);

        if (targetQueueExists)
            throw HandoverCollision(
                AppErrorCode.WORK_ASSIGNMENT_HANDOVER_TARGET_QUEUE_EXISTS,
                assignment,
                targetAssigneeUserId);
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

    private sealed record ReportTemplateKey(string WorkId, string? DynamicFormTemplateId, string UserId);
}
