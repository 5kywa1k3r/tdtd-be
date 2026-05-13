using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Linq;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.Common.Time;

namespace tdtd_be.Services.WorkAssignments.Runtime;

public sealed class WorkAssignmentMaterializeJobService : IWorkAssignmentMaterializeJobService
{
    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentStatusSyncService _sync;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
    private readonly IWorkStatusOperationLogService _statusLog;
    private readonly ILogger<WorkAssignmentMaterializeJobService> _log;
    private readonly int _rollingWindowDays;

    public WorkAssignmentMaterializeJobService(
        MongoDbContext ctx,
        IWorkAssignmentStatusSyncService sync,
        IDocRoleReadModelProjectionService docRoleReadModelProjection,
        IWorkStatusOperationLogService statusLog,
        ILogger<WorkAssignmentMaterializeJobService> log,
        IConfiguration cfg)
    {
        _ctx = ctx;
        _sync = sync;
        _docRoleReadModelProjection = docRoleReadModelProjection;
        _statusLog = statusLog;
        _log = log;
        _rollingWindowDays = Math.Clamp(cfg.GetValue<int?>("WorkAssignmentMaterialize:RollingWindowDays") ?? 3, 1, 31);
    }

    public async Task EnqueueOrTouchAsync(WorkAssignment assignment, string actorUserId, CancellationToken ct = default)
    {
        if (assignment is null)
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_NODE_INVALID);

        if (string.IsNullOrWhiteSpace(assignment.Id))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_ID_REQUIRED,
                new { assignment.WorkId });

        var now = DateTime.UtcNow;

        var filter = Builders<WorkAssignmentMaterializeJobs>.Filter.And(
            Builders<WorkAssignmentMaterializeJobs>.Filter.Eq(x => x.WorkAssignmentId, assignment.Id),
            Builders<WorkAssignmentMaterializeJobs>.Filter.Eq(x => x.IsDeleted, false)
        );

        var shouldRun = assignment.IsActive;

        var update = Builders<WorkAssignmentMaterializeJobs>.Update
            .SetOnInsert(x => x.Id, ObjectId.GenerateNewId().ToString())
            .SetOnInsert(x => x.WorkId, assignment.WorkId)
            .SetOnInsert(x => x.WorkAssignmentId, assignment.Id!)
            .SetOnInsert(x => x.CreatedAtUtc, now)
            .SetOnInsert(x => x.CreatedByUserId, actorUserId)
            .Set(x => x.CursorAssigneeIndex, 0)
            .Set(x => x.CursorDueIndex, 0)
            .Set(x => x.Status, shouldRun ? MaterializeJobStatuses.Pending : MaterializeJobStatuses.Completed)
            .Set(x => x.IsActive, shouldRun)
            .Set(x => x.IsDeleted, false)
            .Set(x => x.NextRetryAtUtc, shouldRun ? now : null)
            .Set(x => x.LastError, null)
            .Set(x => x.LeaseUntilUtc, null)
            .Set(x => x.CompletedAtUtc, shouldRun ? null : now)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);

        await _ctx.WorkAssignmentMaterializeJobs.UpdateOneAsync(
            filter,
            update,
            new UpdateOptions { IsUpsert = true },
            ct);
    }

    public async Task EnqueueOrTouchByAssignmentIdAsync(string workAssignmentId, string actorUserId, CancellationToken ct = default)
    {
        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == workAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND,
                new { assignmentId = workAssignmentId });

        await EnqueueOrTouchAsync(assignment, actorUserId, ct);
    }

    public async Task DisableByAssignmentIdAsync(string workAssignmentId, string actorUserId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        await _ctx.WorkAssignmentMaterializeJobs.UpdateManyAsync(
            x => x.WorkAssignmentId == workAssignmentId && !x.IsDeleted,
            Builders<WorkAssignmentMaterializeJobs>.Update
                .Set(x => x.IsActive, false)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.NextRetryAtUtc, null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
    }

    public async Task<int> ProcessPendingJobsAsync(
        int maxJobs = 10,
        int batchSize = 20,
        CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;

        if (maxJobs <= 0)
            return 0;

        var processed = 0;
        var failed = 0;

        try
        {
            for (var i = 0; i < maxJobs; i++)
            {
                var job = await ClaimNextJobAsync(ct);
                if (job is null)
                    break;

                try
                {
                    await ProcessSingleJobAsync(job, batchSize, ct);
                    processed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await MarkRetryAsync(job, ex, ct);
                    failed++;
                }
            }

            if (processed > 0 || failed > 0)
            {
                _log.LogInformation(
                    "WorkAssignment materialize scan completed. processed={processed} failed={failed} maxJobs={maxJobs} batchSize={batchSize} rollingWindowDays={rollingWindowDays}",
                    processed,
                    failed,
                    maxJobs,
                    batchSize,
                    _rollingWindowDays);

                await WriteStatusOperationLogAsync(new WorkStatusOperationLog
                {
                    Operation = "MATERIALIZE_SCAN",
                    Scope = "materialize-scan",
                    Result = failed == 0 ? "SUCCESS" : "PARTIAL_FAILED",
                    ActorUserId = "system",
                    Summary = $"processed={processed};failed={failed};maxJobs={maxJobs};batchSize={batchSize};rollingWindowDays={_rollingWindowDays}",
                    StartedAtUtc = startedAtUtc
                }, startedAtUtc, ct);
            }

            return processed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(
                ex,
                "WorkAssignment materialize scan failed. processed={processed} failed={failed} maxJobs={maxJobs} batchSize={batchSize} rollingWindowDays={rollingWindowDays}",
                processed,
                failed,
                maxJobs,
                batchSize,
                _rollingWindowDays);

            await WriteStatusOperationLogAsync(new WorkStatusOperationLog
            {
                Operation = "MATERIALIZE_SCAN",
                Scope = "materialize-scan",
                Result = "FAILED",
                ActorUserId = "system",
                Summary = $"processed={processed};failed={failed};maxJobs={maxJobs};batchSize={batchSize};rollingWindowDays={_rollingWindowDays}",
                ErrorType = ex.GetType().FullName,
                ErrorMessage = ex.Message,
                ErrorStackTrace = ex.ToString(),
                StartedAtUtc = startedAtUtc
            }, startedAtUtc, ct);

            throw;
        }
    }

    private async Task<WorkAssignmentMaterializeJobs?> ClaimNextJobAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var leaseUntil = now.AddMinutes(3);

        return await _ctx.WorkAssignmentMaterializeJobs.FindOneAndUpdateAsync(
            x =>
                x.IsActive &&
                !x.IsDeleted &&
                (
                    x.Status == MaterializeJobStatuses.Pending ||
                    x.Status == MaterializeJobStatuses.RetryWaiting ||
                    (x.Status == MaterializeJobStatuses.Running && x.LeaseUntilUtc < now)
                ) &&
                (x.NextRetryAtUtc == null || x.NextRetryAtUtc <= now),
            Builders<WorkAssignmentMaterializeJobs>.Update
                .Set(x => x.Status, MaterializeJobStatuses.Running)
                .Set(x => x.LeaseUntilUtc, leaseUntil)
                .Set(x => x.LastHeartbeatAtUtc, now)
                .Set(x => x.LastRunAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, null),
            new FindOneAndUpdateOptions<WorkAssignmentMaterializeJobs>
            {
                ReturnDocument = ReturnDocument.After,
                Sort = Builders<WorkAssignmentMaterializeJobs>.Sort
                    .Ascending(x => x.NextRetryAtUtc)
                    .Ascending(x => x.CreatedAtUtc)
            },
            ct);
    }

    private async Task ProcessSingleJobAsync(
        WorkAssignmentMaterializeJobs job,
        int batchSize,
        CancellationToken ct)
    {
        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == job.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (assignment is null || !assignment.IsActive)
        {
            await CompleteJobAsync(job.Id!, ct);
            return;
        }

        var work = await _ctx.Works
            .Find(x => x.Id == assignment.WorkId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AssignmentWorkNotFound(assignment);

        var bindings = await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkAssignmentId == assignment.Id &&
                x.IsActive &&
                !x.IsDeleted)
            .SortBy(x => x.AssigneeUserId)
            .ToListAsync(ct);

        if (bindings.Count == 0)
        {
            await RequeueQuickAsync(job.Id!, job.CursorAssigneeIndex, job.CursorDueIndex, ct);
            return;
        }

        var dueItems = BuildDueItems(assignment, work);
        if (dueItems.Count == 0)
        {
            if (ShouldContinueRolling(assignment, work, DateTime.UtcNow))
                await RequeueNextRollingWindowAsync(job.Id!, ct);
            else
                await CompleteJobAsync(job.Id!, ct);

            return;
        }

        var createdOrTouched = 0;
        var assigneeIndex = Math.Max(0, job.CursorAssigneeIndex);
        var dueIndex = Math.Max(0, job.CursorDueIndex);
        var isOnceAssignment = IsOnceAssignment(assignment);

        while (assigneeIndex < bindings.Count && createdOrTouched < batchSize)
        {
            var binding = bindings[assigneeIndex];
            var assigneeUserId = binding.AssigneeUserId;

            if (string.IsNullOrWhiteSpace(assigneeUserId) || string.IsNullOrWhiteSpace(binding.Id))
            {
                assigneeIndex++;
                dueIndex = 0;
                continue;
            }

            while (dueIndex < dueItems.Count && createdOrTouched < batchSize)
            {
                var item = dueItems[dueIndex];

                var existed = await _ctx.WorkReportPeriods
                    .Find(x =>
                        x.WorkAssignmentId == assignment.Id &&
                        x.AssigneeUserId == assigneeUserId &&
                        x.PeriodKey == item.PeriodKey &&
                        (x.PeriodKind == null || x.PeriodKind == WorkReportPeriodKind.Scheduled) &&
                        !x.IsDeleted)
                    .FirstOrDefaultAsync(ct);

                if (!DateTime.TryParseExact(
                    item.PeriodKey,
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var periodDate))
                {
                    throw InvalidPeriodKey(item.PeriodKey, assignment.Id);
                }

                periodDate = periodDate.Date;

                var (periodStart, periodEnd) = isOnceAssignment
                    ? GetOncePeriodRange(assignment, work, periodDate, item.DueAtUtc)
                    : AssignmentScheduleTimeHelper.GetPeriodRange(assignment.Schedule!, periodDate);

                var now = DateTime.UtcNow;

                if (existed is null)
                {
                    var status = WorkReportPeriodStatusHelper.ResolveInitialStatus(item.DueAtUtc, now);

                    var period = new WorkReportPeriod
                    {
                        WorkId = assignment.WorkId,
                        WorkAssignmentId = assignment.Id,
                        WorkTemplateAssigneeId = binding.Id!,
                        DynamicExcelId = assignment.DynamicExcelId,
                        DynamicExcelCode = assignment.DynamicExcelCode,
                        DynamicExcelName = assignment.DynamicExcelName,
                        DynamicFormTemplateId = assignment.DynamicFormTemplateId,
                        DynamicFormTemplateCode = assignment.DynamicFormTemplateCode,
                        DynamicFormTemplateName = assignment.DynamicFormTemplateName,
                        AssigneeUserId = assigneeUserId,
                        AssigneeUnitId = binding.AssigneeUnitId,
                        PeriodKey = item.PeriodKey,
                        PeriodInstanceKey = item.PeriodKey,
                        PeriodKind = WorkReportPeriodKind.Scheduled,
                        ReportTitle = assignment.DynamicExcelName,
                        ReportDate = periodDate,
                        PeriodStart = periodStart,
                        PeriodEnd = periodEnd,
                        DueAtUtc = item.DueAtUtc,
                        Status = status,
                        IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(status),
                        IsActive = true,
                        IsDeleted = false,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        CreatedByUserId = null,
                        UpdatedByUserId = null
                    };

                    await _ctx.WorkReportPeriods.InsertOneAsync(period, cancellationToken: ct);

                    await _ctx.WorkAssignmentQueueItems.UpdateOneAsync(
                        x => x.WorkAssignmentId == period.WorkAssignmentId &&
                             x.AssigneeUserId == period.AssigneeUserId &&
                             x.PeriodKey == period.PeriodKey,
                        Builders<WorkAssignmentQueueItem>.Update
                            .SetOnInsert(x => x.Id, ObjectId.GenerateNewId().ToString())
                            .SetOnInsert(x => x.CreatedAtUtc, now)
                            .SetOnInsert(x => x.CreatedByUserId, null)
                            .Set(x => x.WorkId, period.WorkId)
                            .Set(x => x.WorkAssignmentId, period.WorkAssignmentId)
                            .Set(x => x.AssigneeUserId, period.AssigneeUserId)
                            .Set(x => x.PeriodKey, period.PeriodKey)
                            .Set(x => x.DueAtUtc, period.DueAtUtc)
                            .Set(x => x.NextScanAtUtc, period.DueAtUtc ?? now)
                            .Set(x => x.IsActive, WorkReportPeriodStatusHelper.ShouldKeepQueueActive(period.Status))
                            .Set(x => x.LastObservedPeriodStatus, (int)period.Status)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, null),
                        new UpdateOptions { IsUpsert = true },
                        ct);

                    await _docRoleReadModelProjection.RebuildReportPeriodAsync(period.Id, "system", ct);
                    createdOrTouched++;
                }
                else
                {
                    var updatedStatus = existed.Status;
                    var updatedIsOverdue = existed.IsOverdue;

                    if (existed.Status == WorkReportPeriodStatus.Pending ||
                        existed.Status == WorkReportPeriodStatus.OverduePending)
                    {
                        updatedStatus = WorkReportPeriodStatusHelper.ResolveInitialStatus(item.DueAtUtc, now);

                        updatedIsOverdue = WorkReportPeriodStatusHelper.IsOverdue(updatedStatus);
                    }

                    var updatedIsActive = WorkReportPeriodStatusHelper.ShouldKeepQueueActive(updatedStatus);
                    if (IsExistingPeriodCurrent(
                            existed,
                            binding.Id!,
                            item.DueAtUtc,
                            periodStart,
                            periodEnd,
                            updatedStatus,
                            updatedIsOverdue,
                            updatedIsActive))
                    {
                        dueIndex++;
                        continue;
                    }

                    await _ctx.WorkReportPeriods.UpdateOneAsync(
                        x => x.Id == existed.Id,
                        Builders<WorkReportPeriod>.Update
                            .Set(x => x.WorkTemplateAssigneeId, binding.Id!)
                            .Set(x => x.IsActive, updatedIsActive)
                            .Set(x => x.DueAtUtc, item.DueAtUtc)
                            .Set(x => x.DynamicFormTemplateId, assignment.DynamicFormTemplateId)
                            .Set(x => x.DynamicFormTemplateCode, assignment.DynamicFormTemplateCode)
                            .Set(x => x.DynamicFormTemplateName, assignment.DynamicFormTemplateName)
                            .Set(x => x.PeriodInstanceKey, string.IsNullOrWhiteSpace(existed.PeriodInstanceKey) ? existed.PeriodKey : existed.PeriodInstanceKey)
                            .Set(x => x.PeriodKind, WorkReportPeriodKind.Scheduled)
                            .Set(x => x.ReportDate, periodDate)
                            .Set(x => x.PeriodStart, periodStart)
                            .Set(x => x.PeriodEnd, periodEnd)
                            .Set(x => x.Status, updatedStatus)
                            .Set(x => x.IsOverdue, updatedIsOverdue)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, null),
                        cancellationToken: ct);

                    await _ctx.WorkAssignmentQueueItems.UpdateOneAsync(
                        x => x.WorkAssignmentId == existed.WorkAssignmentId &&
                             x.AssigneeUserId == existed.AssigneeUserId &&
                             x.PeriodKey == existed.PeriodKey,
                        Builders<WorkAssignmentQueueItem>.Update
                            .SetOnInsert(x => x.Id, ObjectId.GenerateNewId().ToString())
                            .SetOnInsert(x => x.CreatedAtUtc, now)
                            .SetOnInsert(x => x.CreatedByUserId, null)
                            .Set(x => x.WorkId, existed.WorkId)
                            .Set(x => x.WorkAssignmentId, existed.WorkAssignmentId)
                            .Set(x => x.AssigneeUserId, existed.AssigneeUserId)
                            .Set(x => x.PeriodKey, existed.PeriodKey)
                            .Set(x => x.DueAtUtc, item.DueAtUtc)
                            .Set(x => x.NextScanAtUtc, item.DueAtUtc)
                            .Set(x => x.IsActive, updatedIsActive)
                            .Set(x => x.LastObservedPeriodStatus, (int)updatedStatus)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, null),
                        new UpdateOptions { IsUpsert = true },
                        ct);

                    await _docRoleReadModelProjection.RebuildReportPeriodAsync(existed.Id, "system", ct);
                    createdOrTouched++;
                }

                dueIndex++;
            }

            if (dueIndex >= dueItems.Count)
            {
                assigneeIndex++;
                dueIndex = 0;
            }
        }

        if (assigneeIndex >= bindings.Count)
        {
            if (createdOrTouched > 0)
            {
                await _sync.SyncFromAssignmentAsync(assignment.Id, ct);
            }

            if (ShouldContinueRolling(assignment, work, DateTime.UtcNow))
                await RequeueNextRollingWindowAsync(job.Id!, ct);
            else
                await CompleteJobAsync(job.Id!, ct);

            return;
        }

        await RequeueQuickAsync(job.Id!, assigneeIndex, dueIndex, ct);
    }

    private List<AssignmentScheduleDueItem> BuildDueItems(WorkAssignment assignment, Work work)
    {
        if (IsOnceAssignment(assignment))
        {
            return new List<AssignmentScheduleDueItem>
            {
                BuildOnceDueItem(assignment, work)
            };
        }

        if (assignment.Schedule is null)
            return new List<AssignmentScheduleDueItem>();

        var today = DateTime.UtcNow.Date;
        var start = today;
        var scheduleStart = assignment.Schedule.StartDate?.Date ?? work.StartDate?.Date;
        if (scheduleStart.HasValue && scheduleStart.Value > start)
            start = scheduleStart.Value;

        var end = today.AddDays(_rollingWindowDays - 1);
        if (work.EndDate.HasValue && work.EndDate.Value.Date < end)
            end = work.EndDate.Value.Date;

        if (end < start)
            return new List<AssignmentScheduleDueItem>();

        return AssignmentScheduleDueHelper.GetDueItemsInRange(
            assignment.Schedule,
            start,
            end);
    }

    private bool ShouldContinueRolling(WorkAssignment assignment, Work work, DateTime nowUtc)
    {
        if (IsOnceAssignment(assignment) || assignment.Schedule is null || !assignment.IsActive)
            return false;

        var nextWindowStart = nowUtc.Date.AddDays(1);
        if (work.EndDate.HasValue && work.EndDate.Value.Date < nextWindowStart)
            return false;

        return true;
    }

    private static bool IsExistingPeriodCurrent(
        WorkReportPeriod existed,
        string bindingId,
        DateTime dueAtUtc,
        DateTime? periodStart,
        DateTime? periodEnd,
        WorkReportPeriodStatus status,
        bool isOverdue,
        bool isActive)
    {
        return string.Equals(existed.WorkTemplateAssigneeId, bindingId, StringComparison.Ordinal) &&
               existed.DueAtUtc == dueAtUtc &&
               NullableDateEquals(existed.PeriodStart, periodStart) &&
               NullableDateEquals(existed.PeriodEnd, periodEnd) &&
               existed.Status == status &&
               existed.IsOverdue == isOverdue &&
               existed.IsActive == isActive;
    }

    private static bool NullableDateEquals(DateTime? left, DateTime? right)
        => left == right;

    private static AssignmentScheduleDueItem BuildOnceDueItem(WorkAssignment assignment, Work work)
    {
        var dueAtUtc = NormalizeOnceDueAtUtc(assignment, work);
        var periodKey = dueAtUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        return new AssignmentScheduleDueItem
        {
            DueAtUtc = dueAtUtc,
            PeriodKey = periodKey
        };
    }

    private static DateTime NormalizeOnceDueAtUtc(WorkAssignment assignment, Work work)
    {
        if (assignment.DueAtUtc.HasValue)
            return assignment.DueAtUtc.Value;

        // fallback chỉ để chống dữ liệu cũ, không nên còn được dùng cho create mới
        var dueDate = work.EndDate?.Date
            ?? work.StartDate?.Date
            ?? assignment.CreatedAtUtc.Date;

        return dueDate == default
            ? DateTime.UtcNow.Date
            : dueDate;
    }

    private static (DateTime PeriodStart, DateTime PeriodEnd) GetOncePeriodRange(
        WorkAssignment assignment,
        Work work,
        DateTime periodDate,
        DateTime dueAtUtc)
    {
        var periodStart = work.StartDate?.Date
            ?? assignment.CreatedAtUtc.Date;

        var periodEnd = dueAtUtc.Date;
        if (periodEnd < periodStart)
            periodEnd = periodStart;

        return (periodStart, periodEnd);
    }

    private static bool IsOnceAssignment(WorkAssignment assignment)
    {
        var assignmentTypeText = Convert.ToString(assignment.AssignmentType, CultureInfo.InvariantCulture);
        return string.Equals(assignmentTypeText, "ONCE", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RequeueQuickAsync(
        string jobId,
        int cursorAssigneeIndex,
        int cursorDueIndex,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var jitterSeconds = Random.Shared.Next(15, 61);

        await _ctx.WorkAssignmentMaterializeJobs.UpdateOneAsync(
            x => x.Id == jobId && !x.IsDeleted,
            Builders<WorkAssignmentMaterializeJobs>.Update
                .Set(x => x.Status, MaterializeJobStatuses.Pending)
                .Set(x => x.CursorAssigneeIndex, cursorAssigneeIndex)
                .Set(x => x.CursorDueIndex, cursorDueIndex)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.LastHeartbeatAtUtc, now)
                .Set(x => x.NextRetryAtUtc, now.AddSeconds(jitterSeconds))
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, null),
            cancellationToken: ct);
    }

    private async Task RequeueNextRollingWindowAsync(string jobId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var jitterMinutes = Random.Shared.Next(0, 31);
        var nextRunAt = now.AddDays(1).AddMinutes(jitterMinutes);

        await _ctx.WorkAssignmentMaterializeJobs.UpdateOneAsync(
            x => x.Id == jobId && !x.IsDeleted,
            Builders<WorkAssignmentMaterializeJobs>.Update
                .Set(x => x.Status, MaterializeJobStatuses.Pending)
                .Set(x => x.CursorAssigneeIndex, 0)
                .Set(x => x.CursorDueIndex, 0)
                .Set(x => x.IsActive, true)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.LastHeartbeatAtUtc, now)
                .Set(x => x.NextRetryAtUtc, nextRunAt)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, null),
            cancellationToken: ct);
    }

    private async Task MarkRetryAsync(
        WorkAssignmentMaterializeJobs job,
        Exception ex,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var nextRetryCount = job.RetryCount + 1;
        var delayMinutes = Math.Min(10 * Math.Pow(2, Math.Max(0, nextRetryCount - 1)), 360);
        var jitterMinutes = Random.Shared.Next(0, 16);

        var nextRetryAt = now.AddMinutes(delayMinutes + jitterMinutes);
        var nextStatus = nextRetryCount >= 10
            ? MaterializeJobStatuses.DeadLetter
            : MaterializeJobStatuses.RetryWaiting;
        var nextRetryAtText = nextStatus == MaterializeJobStatuses.DeadLetter
            ? null
            : nextRetryAt.ToString("O", CultureInfo.InvariantCulture);

        await _ctx.WorkAssignmentMaterializeJobs.UpdateOneAsync(
            x => x.Id == job.Id && !x.IsDeleted,
            Builders<WorkAssignmentMaterializeJobs>.Update
                .Set(x => x.Status, nextStatus)
                .Set(x => x.RetryCount, nextRetryCount)
                .Set(x => x.LastError, ex.ToString())
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.NextRetryAtUtc, nextStatus == MaterializeJobStatuses.DeadLetter ? null : nextRetryAt)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, null),
            cancellationToken: ct);

        _log.LogError(
            ex,
            "WorkAssignment materialize job failed. jobId={jobId} workId={workId} assignmentId={assignmentId} retryCount={retryCount} nextStatus={nextStatus} nextRetryAtUtc={nextRetryAtUtc}",
            job.Id,
            job.WorkId,
            job.WorkAssignmentId,
            nextRetryCount,
            nextStatus,
            nextStatus == MaterializeJobStatuses.DeadLetter ? null : nextRetryAt);

        await WriteStatusOperationLogAsync(new WorkStatusOperationLog
        {
            Operation = "MATERIALIZE_JOB",
            Scope = "materialize-job",
            Result = "FAILED",
            WorkId = job.WorkId,
            WorkAssignmentId = job.WorkAssignmentId,
            ActorUserId = "system",
            FromStatus = job.Status,
            ToStatus = nextStatus,
            Summary = $"jobId={job.Id};retryCount={nextRetryCount};nextRetryAtUtc={nextRetryAtText}",
            ErrorType = ex.GetType().FullName,
            ErrorMessage = ex.Message,
            ErrorStackTrace = ex.ToString(),
            StartedAtUtc = now
        }, now, ct);
    }

    private async Task CompleteJobAsync(string jobId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        await _ctx.WorkAssignmentMaterializeJobs.UpdateOneAsync(
            x => x.Id == jobId && !x.IsDeleted,
            Builders<WorkAssignmentMaterializeJobs>.Update
                .Set(x => x.Status, MaterializeJobStatuses.Completed)
                .Set(x => x.IsActive, false)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.CompletedAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, null),
            cancellationToken: ct);
    }

    private async Task WriteStatusOperationLogAsync(
        WorkStatusOperationLog log,
        DateTime startedAtUtc,
        CancellationToken ct)
    {
        var completedAtUtc = DateTime.UtcNow;
        log.CompletedAtUtc = completedAtUtc;
        log.DurationMs = (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        await _statusLog.WriteAsync(log, ct);
    }

    private static AppException AssignmentWorkNotFound(WorkAssignment assignment)
        => AppExceptionFactory.NotFound(
            AppErrorCode.WORK_ASSIGNMENT_WORK_NOT_FOUND,
            new { assignmentId = assignment.Id, workId = assignment.WorkId });

    private static AppException InvalidPeriodKey(string periodKey, string? assignmentId)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_PERIOD_KEY_INVALID,
            new { assignmentId, periodKey });
}
