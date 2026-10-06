using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Linq;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.Common.Time;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.WorkAssignments.Runtime;

public sealed class WorkAssignmentMaterializeJobService : IWorkAssignmentMaterializeJobService
{
    private readonly MongoDbContext _ctx;
    private readonly IDynamicFlowDefinitionTransactionRunner _transactions;
    private readonly IWorkAssignmentStatusSyncService _sync;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
    private readonly IWorkStatusOperationLogService _statusLog;
    private readonly ILogger<WorkAssignmentMaterializeJobService> _log;
    private readonly int _rollingWindowCount;

    public WorkAssignmentMaterializeJobService(
        MongoDbContext ctx,
        IDynamicFlowDefinitionTransactionRunner transactions,
        IWorkAssignmentStatusSyncService sync,
        IDocRoleReadModelProjectionService docRoleReadModelProjection,
        IWorkStatusOperationLogService statusLog,
        ILogger<WorkAssignmentMaterializeJobService> log,
        IConfiguration cfg)
    {
        _ctx = ctx;
        _transactions = transactions;
        _sync = sync;
        _docRoleReadModelProjection = docRoleReadModelProjection;
        _statusLog = statusLog;
        _log = log;
        _rollingWindowCount = Math.Clamp(
            cfg.GetValue<int?>("WorkAssignmentMaterialize:RollingWindowCount") ??
            cfg.GetValue<int?>("WorkAssignmentMaterialize:RollingWindowDays") ??
            3,
            1,
            31);
    }

    public async Task EnqueueOrTouchAsync(WorkAssignment assignment, string actorUserId, CancellationToken ct = default)
    {
        if (assignment is null)
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_NODE_INVALID);

        if (string.IsNullOrWhiteSpace(assignment.Id))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_ID_REQUIRED,
                new { assignment.WorkId });

        // P5 runtime outbox/reconcile exclusively owns period materialization
        // for flow assignments. A legacy job must never race that ownership.
        if (!string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
        {
            await DisableByAssignmentIdAsync(assignment.Id, actorUserId, ct);
            return;
        }

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

    public async Task DisableByAssignmentIdAsync(string workAssignmentId, string? actorUserId, CancellationToken ct = default)
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
                    "WorkAssignment materialize scan completed. processed={processed} failed={failed} maxJobs={maxJobs} batchSize={batchSize} rollingWindowCount={rollingWindowCount}",
                    processed,
                    failed,
                    maxJobs,
                    batchSize,
                    _rollingWindowCount);

                await WriteStatusOperationLogAsync(new WorkStatusOperationLog
                {
                    Operation = "MATERIALIZE_SCAN",
                    Scope = "materialize-scan",
                    Result = failed == 0 ? "SUCCESS" : "PARTIAL_FAILED",
                    ActorUserId = "system",
                    Summary = $"processed={processed};failed={failed};maxJobs={maxJobs};batchSize={batchSize};rollingWindowCount={_rollingWindowCount}",
                    StartedAtUtc = startedAtUtc
                }, startedAtUtc, ct);
            }

            return processed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(
                ex,
                "WorkAssignment materialize scan failed. processed={processed} failed={failed} maxJobs={maxJobs} batchSize={batchSize} rollingWindowCount={rollingWindowCount}",
                processed,
                failed,
                maxJobs,
                batchSize,
                _rollingWindowCount);

            await WriteStatusOperationLogAsync(new WorkStatusOperationLog
            {
                Operation = "MATERIALIZE_SCAN",
                Scope = "materialize-scan",
                Result = "FAILED",
                ActorUserId = "system",
                Summary = $"processed={processed};failed={failed};maxJobs={maxJobs};batchSize={batchSize};rollingWindowCount={_rollingWindowCount}",
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

        if (assignment is null ||
            !assignment.IsActive ||
            !string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
        {
            await CompleteJobAsync(job.Id!, ct);
            return;
        }

        var work = await _ctx.Works
            .Find(x => x.Id == assignment.WorkId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AssignmentWorkNotFound(assignment);

        if (await IsCompletionLockedAsync(assignment, work, ct))
        {
            await CompleteJobAsync(job.Id!, ct);
            return;
        }

        var parent = await LoadParentAssignmentAsync(assignment, ct);

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

        var dueItems = BuildDueItems(assignment, work, parent);
        if (dueItems.Count == 0)
        {
            if (ShouldContinueRolling(assignment, work, parent, DateTime.UtcNow))
                await RequeueNextRollingOccurrenceWindowAsync(job.Id!, ct);
            else
                await CompleteJobAsync(job.Id!, ct);

            return;
        }

        var targetBatchSize = Math.Max(1, batchSize);
        var assigneeIndex = Math.Max(0, job.CursorAssigneeIndex);
        var dueIndex = Math.Max(0, job.CursorDueIndex);
        var isOnceAssignment = IsOnceAssignment(assignment);
        var targets = new List<MaterializeTarget>(Math.Min(targetBatchSize, bindings.Count * Math.Max(1, dueItems.Count)));

        while (dueIndex < dueItems.Count && targets.Count < targetBatchSize)
        {
            var item = dueItems[dueIndex];

            if (!DateTime.TryParseExact(
                item.PeriodKey,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var periodDate))
            {
                throw InvalidPeriodKey(item.PeriodKey, assignment.Id);
            }

            periodDate = ReportCivilDate.Encode(periodDate);

            var actualDueAtUtc = isOnceAssignment
                ? ResolveOnceDueAtUtc(assignment, work, parent)
                : item.DueAtUtc;
            if (isOnceAssignment && !actualDueAtUtc.HasValue)
                periodDate = assignment.CreatedAtUtc.Date;
            var (periodStart, periodEnd) = isOnceAssignment
                ? GetOncePeriodRange(assignment, work, parent, actualDueAtUtc)
                : AssignmentScheduleTimeHelper.GetPeriodRange(assignment.Schedule!, periodDate);

            while (assigneeIndex < bindings.Count && targets.Count < targetBatchSize)
            {
                var binding = bindings[assigneeIndex];
                var assigneeUserId = binding.AssigneeUserId;

                assigneeIndex++;

                if (string.IsNullOrWhiteSpace(assigneeUserId) || string.IsNullOrWhiteSpace(binding.Id))
                    continue;

                targets.Add(new MaterializeTarget(
                    Binding: binding,
                    DueItem: item,
                    DueAtUtc: actualDueAtUtc,
                    PeriodDate: periodDate,
                    PeriodStart: periodStart,
                    PeriodEnd: periodEnd));
            }

            if (assigneeIndex >= bindings.Count)
            {
                dueIndex++;
                assigneeIndex = 0;
            }
        }

        var reachedEnd = dueIndex >= dueItems.Count;
        var continueRolling = reachedEnd &&
                              ShouldContinueRolling(
                                  assignment,
                                  work,
                                  parent,
                                  DateTime.UtcNow);
        var changed = await ApplyMaterializeBatchAndAdvanceJobAsync(
            assignment,
            job,
            targets,
            assigneeIndex,
            dueIndex,
            dueItems.Count,
            continueRolling,
            ct);

        if (targets.Count > 0)
        {
            _log.LogInformation(
                "WorkAssignment materialize batch. assignmentId={assignmentId} targets={targets} created={created} updated={updated} skipped={skipped} nextAssigneeIndex={nextAssigneeIndex} nextDueIndex={nextDueIndex} batchSize={batchSize}",
                assignment.Id,
                targets.Count,
                changed.CreatedCount,
                changed.UpdatedCount,
                changed.SkippedCount,
                assigneeIndex,
                dueIndex,
                targetBatchSize);
        }

        if (changed.ChangedCount > 0)
            await _sync.SyncFromAssignmentAsync(assignment.Id, ct);

        foreach (var periodId in changed.RebuildPeriodIds)
            await _docRoleReadModelProjection.RebuildReportPeriodAsync(
                periodId,
                "system",
                ct);
    }

    private async Task<MaterializeBatchResult> ApplyMaterializeBatchAndAdvanceJobAsync(
        WorkAssignment assignment,
        WorkAssignmentMaterializeJobs job,
        IReadOnlyList<MaterializeTarget> targets,
        int nextAssigneeIndex,
        int nextDueIndex,
        int dueItemCount,
        bool continueRolling,
        CancellationToken ct)
    {
        return await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                // The schedule/binding targets were prepared before the
                // transaction. Revalidate the assignment snapshot so a stale
                // legacy batch fails closed rather than rewriting a newer
                // owner state.
                var currentAssignment = await _ctx.WorkAssignments
                    .Find(
                        session,
                        x => x.Id == assignment.Id &&
                             x.WorkId == assignment.WorkId &&
                             x.UpdatedAtUtc == assignment.UpdatedAtUtc &&
                             x.IsActive &&
                             !x.IsDeleted &&
                             x.FlowInstanceId == null)
                    .FirstOrDefaultAsync(transactionCt);
                if (currentAssignment is null)
                {
                    throw new InvalidOperationException(
                        "P9_MATERIALIZE_ASSIGNMENT_CAS_LOST");
                }

                var result = await ApplyMaterializeBatchAsync(
                    session,
                    currentAssignment,
                    targets,
                    transactionCt);

                if (result.ChangedCount > 0)
                {
                    await WorkDirectSourceRevisionFence.IncrementAsync(
                        _ctx,
                        session,
                        currentAssignment.WorkId,
                        transactionCt);
                }

                await AdvanceClaimedJobAsync(
                    session,
                    job,
                    nextAssigneeIndex,
                    nextDueIndex,
                    dueItemCount,
                    continueRolling,
                    transactionCt);

                return result;
            },
            ct);
    }

    private async Task<MaterializeBatchResult> ApplyMaterializeBatchAsync(
        IClientSessionHandle session,
        WorkAssignment assignment,
        IReadOnlyList<MaterializeTarget> targets,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var assigneeUserIds = targets
            .Select(x => x.Binding.AssigneeUserId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var periodKeys = targets
            .Select(x => x.DueItem.PeriodKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var bindingIds = targets
            .Select(x => x.Binding.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var currentBindings = await _ctx.WorkTemplateAssignees
            .Find(
                session,
                x => bindingIds.Contains(x.Id) &&
                     x.WorkId == assignment.WorkId &&
                     x.WorkAssignmentId == assignment.Id &&
                     x.IsActive &&
                     !x.IsDeleted)
            .ToListAsync(ct);
        var currentBindingById = currentBindings
            .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);

        // ONCE has one scheduled occurrence per assignee. A changed or newly
        // supplied deadline must reuse its identity, including legacy date keys.
        var isOnceAssignment = IsOnceAssignment(assignment);
        var existingPeriods = await _ctx.WorkReportPeriods
            .Find(
                session,
                x => x.WorkAssignmentId == assignment.Id &&
                     assigneeUserIds.Contains(x.AssigneeUserId) &&
                     (isOnceAssignment || periodKeys.Contains(x.PeriodKey)) &&
                     (x.PeriodKind == null || x.PeriodKind == WorkReportPeriodKind.Scheduled) &&
                     !x.IsDeleted)
            .ToListAsync(ct);

        var existingByAssigneeAndKey = existingPeriods
            .GroupBy(x => PeriodLookupKey(x.AssigneeUserId, isOnceAssignment ? "ONCE" : x.PeriodKey), StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                // Corrupt/legacy duplicates fail closed: if any row carries
                // lifecycle authority, select it so this target is skipped.
                x => x
                    .OrderByDescending(HasLifecycleAuthority)
                    .ThenByDescending(p => p.UpdatedAtUtc)
                    .First(),
                StringComparer.Ordinal);

        var periodWrites = new List<WriteModel<WorkReportPeriod>>();
        var queueWrites = new List<WriteModel<WorkAssignmentQueueItem>>();
        var rebuildPeriodIds = new List<string>();
        var created = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.Binding.Id) ||
                !currentBindingById.TryGetValue(target.Binding.Id, out var binding) ||
                !string.Equals(
                    binding.AssigneeUserId,
                    target.Binding.AssigneeUserId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    binding.AssigneeUnitId,
                    target.Binding.AssigneeUnitId,
                    StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            var item = target.DueItem;
            var assigneeUserId = binding.AssigneeUserId;

            if (string.IsNullOrWhiteSpace(assigneeUserId) || string.IsNullOrWhiteSpace(binding.Id))
            {
                skipped++;
                continue;
            }

            var lookupKey = PeriodLookupKey(assigneeUserId, isOnceAssignment ? "ONCE" : item.PeriodKey);
            var isHistoricalData = WorkAssignmentBackfillPeriodPolicy.IsBackfillHistoricalPeriod(
                assignment,
                target.PeriodStart,
                target.PeriodEnd,
                target.DueAtUtc ?? target.PeriodDate,
                now);

            if (!existingByAssigneeAndKey.TryGetValue(lookupKey, out var existed))
            {
                await tdtd_be.Services.AggregateMapping.Persistence.AggregateHostIntegration.SlotAsync(
                    _ctx, session, assignment.WorkId, binding.Id, binding.AssignmentType == "ONCE" ? "ONCE" : item.PeriodKey,
                    "materialize:" + binding.Id + ":" + item.PeriodKey, ct);
                var status = isHistoricalData
                    ? WorkReportPeriodStatus.Pending
                    : WorkReportPeriodStatusHelper.ResolveInitialStatus(target.DueAtUtc, now);
                var period = new WorkReportPeriod
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    WorkId = assignment.WorkId,
                    WorkAssignmentId = assignment.Id,
                    WorkTemplateAssigneeId = binding.Id!,
                    DynamicExcelId = assignment.DynamicExcelId,
                    DynamicExcelCode = assignment.DynamicExcelCode,
                    DynamicExcelName = assignment.DynamicExcelName,
                    DynamicFormTemplateId = assignment.DynamicFormTemplateId,
                    DynamicFormTemplateCode = assignment.DynamicFormTemplateCode,
                    DynamicFormTemplateName = assignment.DynamicFormTemplateName,
                    DynamicFormFamilyId = assignment.DynamicFormFamilyId,
                    DynamicFormVersionNo = assignment.DynamicFormVersionNo,
                    DynamicFormSchemaHash = assignment.DynamicFormSchemaHash,
                    AssigneeUserId = assigneeUserId,
                    AssigneeUnitId = binding.AssigneeUnitId,
                    PeriodKey = item.PeriodKey,
                    PeriodInstanceKey = item.PeriodKey,
                    PeriodKind = WorkReportPeriodKind.Scheduled,
                    ReportTitle = assignment.DynamicExcelName,
                    ReportDate = target.PeriodDate,
                    PeriodStart = target.PeriodStart,
                    PeriodEnd = target.PeriodEnd,
                    DueAtUtc = target.DueAtUtc,
                    Status = status,
                    IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(status),
                    IsHistoricalData = isHistoricalData,
                    IsActive = true,
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = null,
                    UpdatedByUserId = null
                };

                periodWrites.Add(new InsertOneModel<WorkReportPeriod>(period));
                queueWrites.Add(BuildQueueUpsert(
                    period.WorkId,
                    period.WorkAssignmentId,
                    period.AssigneeUserId,
                    period.PeriodKey,
                    target.DueAtUtc,
                    status,
                    !isHistoricalData && WorkReportPeriodStatusHelper.ShouldKeepQueueActive(status),
                    now));
                rebuildPeriodIds.Add(period.Id);
                created++;
                continue;
            }

            // P9 lifecycle owns these rows. A legacy materializer must not
            // rewrite their status, visibility, time window, form pin, or
            // queue state even when its pre-read saw an older Pending row.
            if (HasLifecycleAuthority(existed) ||
                (assignment.DeadlineRetainedPeriodsBeforeUtc.HasValue &&
                 existed.CreatedAtUtc <= assignment.DeadlineRetainedPeriodsBeforeUtc.Value))
            {
                skipped++;
                continue;
            }

            var updatedIsHistoricalData = existed.IsHistoricalData || isHistoricalData;
            var updatedStatus = existed.Status;
            var updatedIsOverdue = existed.IsOverdue;

            if (existed.Status == WorkReportPeriodStatus.Pending ||
                existed.Status == WorkReportPeriodStatus.OverduePending)
            {
                updatedStatus = updatedIsHistoricalData
                    ? WorkReportPeriodStatus.Pending
                    : WorkReportPeriodStatusHelper.ResolveInitialStatus(target.DueAtUtc, now);
                updatedIsOverdue = WorkReportPeriodStatusHelper.IsOverdue(updatedStatus);
            }

            // Period visibility is separate from queue scan activity: overdue periods must remain enterable.
            const bool updatedPeriodIsActive = true;
            if (IsExistingPeriodCurrent(
                    existed,
                    binding.Id!,
                    target.DueAtUtc,
                    target.PeriodStart,
                    target.PeriodEnd,
                    updatedStatus,
                    updatedIsOverdue,
                    updatedPeriodIsActive,
                    updatedIsHistoricalData))
            {
                skipped++;
                continue;
            }

            await tdtd_be.Services.AggregateMapping.Persistence.AggregateHostIntegration.SlotAsync(
                _ctx, session, assignment.WorkId, binding.Id, binding.AssignmentType == "ONCE" ? "ONCE" : item.PeriodKey,
                "materialize:" + existed.Id + ":" + existed.UpdatedAtUtc.Ticks, ct);
            var periodFilter = Builders<WorkReportPeriod>.Filter;
            periodWrites.Add(new UpdateOneModel<WorkReportPeriod>(
                periodFilter.Eq(x => x.Id, existed.Id) &
                periodFilter.Eq(x => x.WorkAssignmentId, assignment.Id) &
                periodFilter.Eq(x => x.UpdatedAtUtc, existed.UpdatedAtUtc) &
                periodFilter.Eq(x => x.IsDeleted, false) &
                periodFilter.Ne(x => x.Status, WorkReportPeriodStatus.Approved) &
                periodFilter.Ne(x => x.Status, WorkReportPeriodStatus.OverdueApproved) &
                periodFilter.Eq(x => x.CurrentReportId, null) &
                periodFilter.Eq(x => x.SourceLifecycleReportId, null) &
                (periodFilter.Eq(x => x.SourceLifecycleRevision, 0) |
                 periodFilter.Exists(x => x.SourceLifecycleRevision, false)) &
                periodFilter.Eq(x => x.SourceLifecycleAppliedAtUtc, null) &
                (periodFilter.Eq(x => x.HistoricalDataApproved, false) |
                 periodFilter.Exists(x => x.HistoricalDataApproved, false)) &
                periodFilter.Eq(x => x.HistoricalDataApprovedAtUtc, null),
                Builders<WorkReportPeriod>.Update
                    .Set(x => x.WorkTemplateAssigneeId, binding.Id!)
                    .Set(x => x.IsActive, updatedPeriodIsActive)
                    .Set(x => x.DueAtUtc, target.DueAtUtc)
                    .Set(x => x.PeriodInstanceKey, string.IsNullOrWhiteSpace(existed.PeriodInstanceKey) ? existed.PeriodKey : existed.PeriodInstanceKey)
                    .Set(x => x.PeriodKind, WorkReportPeriodKind.Scheduled)
                    .Set(x => x.ReportDate, isOnceAssignment ? existed.ReportDate : target.PeriodDate)
                    .Set(x => x.PeriodStart, target.PeriodStart)
                    .Set(x => x.PeriodEnd, target.PeriodEnd)
                    .Set(x => x.Status, updatedStatus)
                    .Set(x => x.IsOverdue, updatedIsOverdue)
                    .Set(x => x.IsHistoricalData, updatedIsHistoricalData)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, null)));

            queueWrites.Add(BuildQueueUpsert(
                existed.WorkId,
                existed.WorkAssignmentId,
                existed.AssigneeUserId,
                existed.PeriodKey,
                target.DueAtUtc,
                updatedStatus,
                !updatedIsHistoricalData && WorkReportPeriodStatusHelper.ShouldKeepQueueActive(updatedStatus),
                now));
            rebuildPeriodIds.Add(existed.Id);
            updated++;
        }

        if (periodWrites.Count > 0)
        {
            var result = await _ctx.WorkReportPeriods.BulkWriteAsync(
                session,
                periodWrites,
                new BulkWriteOptions { IsOrdered = false },
                ct);
            if (result.InsertedCount != created ||
                result.MatchedCount != updated ||
                result.ModifiedCount != updated)
            {
                throw new InvalidOperationException(
                    "P9_MATERIALIZE_PERIOD_CAS_LOST");
            }
        }

        if (queueWrites.Count > 0)
        {
            await _ctx.WorkAssignmentQueueItems.BulkWriteAsync(
                session,
                queueWrites,
                new BulkWriteOptions { IsOrdered = false },
                ct);
        }

        return new MaterializeBatchResult(
            created,
            updated,
            skipped,
            created + updated,
            rebuildPeriodIds
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    private static bool HasLifecycleAuthority(WorkReportPeriod period)
        => period.Status is WorkReportPeriodStatus.Approved or
                            WorkReportPeriodStatus.OverdueApproved ||
           !string.IsNullOrWhiteSpace(period.CurrentReportId) ||
           !string.IsNullOrWhiteSpace(period.SourceLifecycleReportId) ||
           period.SourceLifecycleRevision > 0 ||
           period.SourceLifecycleAppliedAtUtc.HasValue ||
           period.HistoricalDataApproved ||
           period.HistoricalDataApprovedAtUtc.HasValue;

    private async Task AdvanceClaimedJobAsync(
        IClientSessionHandle session,
        WorkAssignmentMaterializeJobs job,
        int nextAssigneeIndex,
        int nextDueIndex,
        int dueItemCount,
        bool continueRolling,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var reachedEnd = nextDueIndex >= dueItemCount;
        UpdateDefinition<WorkAssignmentMaterializeJobs> update;

        if (reachedEnd && continueRolling)
        {
            var nextRunAt = now
                .AddDays(1)
                .AddMinutes(Random.Shared.Next(0, 31));
            update = Builders<WorkAssignmentMaterializeJobs>.Update
                .Set(x => x.Status, MaterializeJobStatuses.Pending)
                .Set(x => x.CursorAssigneeIndex, 0)
                .Set(x => x.CursorDueIndex, 0)
                .Set(x => x.IsActive, true)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.LastHeartbeatAtUtc, now)
                .Set(x => x.NextRetryAtUtc, nextRunAt)
                .Set(x => x.CompletedAtUtc, null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, null);
        }
        else if (reachedEnd)
        {
            update = Builders<WorkAssignmentMaterializeJobs>.Update
                .Set(x => x.Status, MaterializeJobStatuses.Completed)
                .Set(x => x.IsActive, false)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.NextRetryAtUtc, null)
                .Set(x => x.CompletedAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, null);
        }
        else
        {
            update = Builders<WorkAssignmentMaterializeJobs>.Update
                .Set(x => x.Status, MaterializeJobStatuses.Pending)
                .Set(x => x.CursorAssigneeIndex, nextAssigneeIndex)
                .Set(x => x.CursorDueIndex, nextDueIndex)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.LastHeartbeatAtUtc, now)
                .Set(x => x.NextRetryAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, null);
        }

        var result = await _ctx.WorkAssignmentMaterializeJobs.UpdateOneAsync(
            session,
            x => x.Id == job.Id &&
                 x.WorkId == job.WorkId &&
                 x.WorkAssignmentId == job.WorkAssignmentId &&
                 x.Status == MaterializeJobStatuses.Running &&
                 x.IsActive &&
                 !x.IsDeleted &&
                 x.LeaseUntilUtc == job.LeaseUntilUtc &&
                 x.CursorAssigneeIndex == job.CursorAssigneeIndex &&
                 x.CursorDueIndex == job.CursorDueIndex,
            update,
            cancellationToken: ct);
        if (result.MatchedCount != 1 || result.ModifiedCount != 1)
        {
            throw new InvalidOperationException(
                "P9_MATERIALIZE_JOB_CAS_LOST");
        }
    }

    private static UpdateOneModel<WorkAssignmentQueueItem> BuildQueueUpsert(
        string workId,
        string workAssignmentId,
        string assigneeUserId,
        string periodKey,
        DateTime? dueAtUtc,
        WorkReportPeriodStatus status,
        bool isActive,
        DateTime now)
    {
        return new UpdateOneModel<WorkAssignmentQueueItem>(
            Builders<WorkAssignmentQueueItem>.Filter.Eq(x => x.WorkAssignmentId, workAssignmentId) &
            Builders<WorkAssignmentQueueItem>.Filter.Eq(x => x.AssigneeUserId, assigneeUserId) &
            Builders<WorkAssignmentQueueItem>.Filter.Eq(x => x.PeriodKey, periodKey),
            Builders<WorkAssignmentQueueItem>.Update
                .SetOnInsert(x => x.Id, ObjectId.GenerateNewId().ToString())
                .SetOnInsert(x => x.CreatedAtUtc, now)
                .SetOnInsert(x => x.CreatedByUserId, null)
                .Set(x => x.WorkId, workId)
                .Set(x => x.WorkAssignmentId, workAssignmentId)
                .Set(x => x.AssigneeUserId, assigneeUserId)
                .Set(x => x.PeriodKey, periodKey)
                .Set(x => x.DueAtUtc, dueAtUtc)
                .Set(x => x.NextScanAtUtc, dueAtUtc ?? now.AddHours(6))
                .Set(x => x.IsActive, isActive)
                .Set(x => x.LastObservedPeriodStatus, (int)status)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, null))
        {
            IsUpsert = true
        };
    }

    private static string PeriodLookupKey(string assigneeUserId, string periodKey)
        => $"{assigneeUserId}|{periodKey}";

    private List<AssignmentScheduleDueItem> BuildDueItems(
        WorkAssignment assignment,
        Work work,
        WorkAssignment? parent)
        => BuildDueItemsForMaterialize(
            assignment,
            work,
            parent,
            DateTime.UtcNow,
            _rollingWindowCount);

    internal static List<AssignmentScheduleDueItem> BuildDueItemsForMaterialize(
        WorkAssignment assignment,
        Work work,
        WorkAssignment? parent,
        DateTime nowUtc,
        int rollingWindowCount)
    {
        if (IsOnceAssignment(assignment))
        {
            var due = BuildOnceDueItem(assignment, work, parent);
            var boundary = WorkAssignmentDatePolicy.ResolveEffectiveDueDate(assignment, work, parent);
            if (boundary.HasValue && due.DueAtUtc.Date > boundary.Value.Date)
                return new List<AssignmentScheduleDueItem>();
            return new List<AssignmentScheduleDueItem>
            {
                due
            };
        }

        if (assignment.Schedule is null)
            return new List<AssignmentScheduleDueItem>();

        var today = nowUtc.Date;
        var start = WorkAssignmentDatePolicy.ResolveEffectiveStartDate(assignment, today);

        var scheduleStart = assignment.Schedule.StartDate?.Date;
        if (scheduleStart.HasValue && scheduleStart.Value > start)
            start = scheduleStart.Value;

        var effectiveCompletedDate = WorkAssignmentDatePolicy.ResolveEffectiveCompletedDate(assignment, work, parent);
        var elapsedEnd = today;
        if (effectiveCompletedDate.HasValue && effectiveCompletedDate.Value.Date < elapsedEnd)
            elapsedEnd = effectiveCompletedDate.Value.Date;

        var dueItems = start <= elapsedEnd
            ? AssignmentScheduleDueHelper.GetDueItemsInRange(assignment.Schedule, start, elapsedEnd)
            : new List<AssignmentScheduleDueItem>();

        var futureStart = today.AddDays(1);
        if (futureStart < start)
            futureStart = start;

        if (!effectiveCompletedDate.HasValue || effectiveCompletedDate.Value.Date >= futureStart)
        {
            var futureItems = AssignmentScheduleDueHelper.GetDueItemsForRollingOccurrenceWindow(
                assignment.Schedule,
                futureStart,
                futureStart,
                effectiveCompletedDate,
                Math.Max(1, rollingWindowCount));

            dueItems.AddRange(futureItems);
        }

        return dueItems
            .GroupBy(x => x.PeriodKey, StringComparer.Ordinal)
            .Select(x => x.OrderBy(i => i.DueAtUtc).First())
            .OrderBy(x => x.DueAtUtc)
            .ToList();
    }

    private bool ShouldContinueRolling(
        WorkAssignment assignment,
        Work work,
        WorkAssignment? parent,
        DateTime nowUtc)
    {
        if (IsOnceAssignment(assignment) || assignment.Schedule is null || !assignment.IsActive)
            return false;

        var nextWindowStart = nowUtc.Date.AddDays(1);
        var effectiveCompletedDate = WorkAssignmentDatePolicy.ResolveEffectiveCompletedDate(assignment, work, parent);
        if (effectiveCompletedDate.HasValue && effectiveCompletedDate.Value.Date < nextWindowStart)
            return false;

        return true;
    }

    private static bool IsExistingPeriodCurrent(
        WorkReportPeriod existed,
        string bindingId,
        DateTime? dueAtUtc,
        DateTime? periodStart,
        DateTime? periodEnd,
        WorkReportPeriodStatus status,
        bool isOverdue,
        bool isActive,
        bool isHistoricalData)
    {
        return string.Equals(existed.WorkTemplateAssigneeId, bindingId, StringComparison.Ordinal) &&
               existed.DueAtUtc == dueAtUtc &&
               NullableDateEquals(existed.PeriodStart, periodStart) &&
               NullableDateEquals(existed.PeriodEnd, periodEnd) &&
               existed.Status == status &&
               existed.IsOverdue == isOverdue &&
               existed.IsActive == isActive &&
               existed.IsHistoricalData == isHistoricalData;
    }

    private static bool NullableDateEquals(DateTime? left, DateTime? right)
        => left == right;

    private static AssignmentScheduleDueItem BuildOnceDueItem(
        WorkAssignment assignment,
        Work work,
        WorkAssignment? parent)
    {
        // The date key is only an occurrence anchor, never a fallback deadline.
        var dueAtUtc = ResolveOnceDueAtUtc(assignment, work, parent)
            ?? assignment.CreatedAtUtc.Date;
        var periodKey = dueAtUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        return new AssignmentScheduleDueItem
        {
            DueAtUtc = dueAtUtc,
            PeriodKey = periodKey
        };
    }

    private static DateTime? ResolveOnceDueAtUtc(
        WorkAssignment assignment,
        Work work,
        WorkAssignment? parent)
    {
        if (assignment.DueAtUtc.HasValue)
            return assignment.DueAtUtc.Value;

        return WorkAssignmentDatePolicy.ResolveEffectiveDueDate(assignment, work, parent);
    }

    private static (DateTime PeriodStart, DateTime? PeriodEnd) GetOncePeriodRange(
        WorkAssignment assignment,
        Work work,
        WorkAssignment? parent,
        DateTime? dueAtUtc)
    {
        var periodStart = WorkAssignmentDatePolicy.ResolveEffectiveStartDate(assignment, DateTime.UtcNow);

        var periodEnd = WorkAssignmentDatePolicy.ResolveEffectiveCompletedDate(assignment, work, parent)
            ?? dueAtUtc?.Date;
        if (periodEnd < periodStart)
            periodEnd = periodStart;

        return (periodStart, periodEnd);
    }

    private async Task<WorkAssignment?> LoadParentAssignmentAsync(WorkAssignment assignment, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(assignment.ParentAssignmentId))
            return null;

        return await _ctx.WorkAssignments
            .Find(x =>
                x.Id == assignment.ParentAssignmentId &&
                x.WorkId == assignment.WorkId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<bool> IsCompletionLockedAsync(WorkAssignment assignment, Work work, CancellationToken ct)
    {
        if (work.CompletedAtUtc.HasValue || work.Status == WorkStatus.S3)
            return true;

        if (IsManuallyCompleted(assignment))
            return true;

        var ancestorIds = ResolveAncestorIds(assignment);
        if (ancestorIds.Count == 0)
            return false;

        return await _ctx.WorkAssignments
            .Find(x =>
                ancestorIds.Contains(x.Id) &&
                x.WorkId == assignment.WorkId &&
                !x.IsDeleted &&
                (x.CompletedAtUtc != null ||
                 (x.ProgressStatus == (int)WorkAssignmentProgressStatus.Completed && x.CompletedDate != null)))
            .Limit(1)
            .AnyAsync(ct);
    }

    private static bool IsManuallyCompleted(WorkAssignment assignment)
        => assignment.CompletedAtUtc.HasValue ||
           (assignment.ProgressStatus == (int)WorkAssignmentProgressStatus.Completed &&
            assignment.CompletedDate.HasValue);

    private static List<string> ResolveAncestorIds(WorkAssignment assignment)
    {
        if (string.IsNullOrWhiteSpace(assignment.Path))
            return new List<string>();

        return assignment.Path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.Equals(x, assignment.Id, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
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

        await _ctx.WorkAssignmentMaterializeJobs.UpdateOneAsync(
            x => x.Id == jobId && !x.IsDeleted,
            Builders<WorkAssignmentMaterializeJobs>.Update
                .Set(x => x.Status, MaterializeJobStatuses.Pending)
                .Set(x => x.CursorAssigneeIndex, cursorAssigneeIndex)
                .Set(x => x.CursorDueIndex, cursorDueIndex)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.LastHeartbeatAtUtc, now)
                .Set(x => x.NextRetryAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, null),
            cancellationToken: ct);
    }

    private async Task RequeueNextRollingOccurrenceWindowAsync(string jobId, CancellationToken ct)
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

    private sealed record MaterializeTarget(
        WorkTemplateAssignee Binding,
        AssignmentScheduleDueItem DueItem,
        DateTime? DueAtUtc,
        DateTime PeriodDate,
        DateTime? PeriodStart,
        DateTime? PeriodEnd);

    private sealed record MaterializeBatchResult(
        int CreatedCount,
        int UpdatedCount,
        int SkippedCount,
        int ChangedCount,
        IReadOnlyList<string> RebuildPeriodIds);
}
