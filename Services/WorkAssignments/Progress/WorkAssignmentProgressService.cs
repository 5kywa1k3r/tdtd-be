using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.Common.Time;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.WorkAssignments.Progress;

public sealed class WorkAssignmentProgressService : IWorkAssignmentProgressService
{
    private const int MaxProgressCasAttempts = 5;

    private readonly MongoDbContext _ctx;

    public WorkAssignmentProgressService(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<ProgressComputeResult> ComputeProgressAsync(
        WorkAssignment assignment,
        CancellationToken ct)
    {
        var children = await _ctx.WorkAssignments
            .Find(x => x.ParentAssignmentId == assignment.Id && x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        if (children.Count == 0)
        {
            var work = await _ctx.Works
                .Find(x => x.Id == assignment.WorkId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw AssignmentWorkNotFound(assignment);

            return await ComputeLeafProgressAsync(assignment, work, ct);
        }

        return await ComputeParentProgressAsync(assignment, children, ct);
    }

    public async Task<ProgressProjectionResult> ComputeProjectionAsync(
        WorkAssignment assignment,
        CancellationToken ct)
    {
        var computed = await ComputeProgressAsync(assignment, ct);
        var worstFacts = await ComputeWorstFactsAsync(assignment, ct);
        return new ProgressProjectionResult
        {
            ProgressStatus = computed.ProgressStatus,
            HasAnyDuePeriod = computed.HasAnyDuePeriod,
            HasOverduePeriod = computed.HasOverduePeriod,
            LatestPeriodKey = computed.LatestPeriodKey,
            LatestDueAtUtc = computed.LatestDueAtUtc,
            WorstPeriodStatus = worstFacts.WorstPeriodStatus,
            WorstOverdueReasonCode = worstFacts.WorstOverdueReasonCode,
            WorstOverdueReasonLabel = worstFacts.WorstOverdueReasonLabel
        };
    }

    public async Task<ProgressComputeResult> ComputeLeafProgressAsync(
        WorkAssignment assignment,
        CancellationToken ct)
    {
        var work = await _ctx.Works
            .Find(x => x.Id == assignment.WorkId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AssignmentWorkNotFound(assignment);

        return await ComputeLeafProgressAsync(assignment, work, ct);
    }

    private async Task<ProgressComputeResult> ComputeLeafProgressAsync(
        WorkAssignment assignment,
        Work work,
        CancellationToken ct)
    {
        if (IsManuallyCompleted(assignment))
        {
            return new ProgressComputeResult
            {
                ProgressStatus = (int)WorkAssignmentProgressStatus.Completed,
                HasAnyDuePeriod = assignment.HasAnyDuePeriod,
                HasOverduePeriod = assignment.HasOverduePeriod,
                LatestPeriodKey = assignment.LatestPeriodKey,
                LatestDueAtUtc = assignment.LatestDueAtUtc
            };
        }

        var now = DateTime.UtcNow;

        var isOnceAssignment = IsOnceAssignment(assignment);

        if (!assignment.IsActive ||
            (!isOnceAssignment &&
             (assignment.Schedule == null || !ScheduleValidator.IsValid(assignment.Schedule))))
        {
            return new ProgressComputeResult
            {
                ProgressStatus = (int)WorkAssignmentProgressStatus.NotStarted,
                HasAnyDuePeriod = false,
                HasOverduePeriod = false,
                LatestPeriodKey = null,
                LatestDueAtUtc = null
            };
        }

        var facts = await BuildLeafProgressFactsAsync(assignment, work, now, ct);

        int status;
        if (!facts.HasAnyDuePeriod && !facts.HasAnyOpenPeriod)
        {
            status = (int)WorkAssignmentProgressStatus.NotStarted;
        }
        else if (facts.IsEnded && facts.HasDueButNotApproved)
        {
            status = (int)WorkAssignmentProgressStatus.Overdue;
        }
        else if (facts.HasDueButNotApproved)
        {
            status = (int)WorkAssignmentProgressStatus.AtRiskOverdue;
        }
        else
        {
            status = (int)WorkAssignmentProgressStatus.InProgress;
        }

        return new ProgressComputeResult
        {
            ProgressStatus = status,
            HasAnyDuePeriod = facts.HasAnyDuePeriod,
            HasOverduePeriod = facts.HasOverduePeriod,
            LatestPeriodKey = facts.LatestPeriodKey,
            LatestDueAtUtc = facts.LatestDueAtUtc
        };
    }

    public Task<ProgressComputeResult> ComputeParentProgressAsync(
        WorkAssignment parent,
        List<WorkAssignment> directChildren,
        CancellationToken ct)
    {
        if (IsManuallyCompleted(parent))
        {
            return Task.FromResult(new ProgressComputeResult
            {
                ProgressStatus = (int)WorkAssignmentProgressStatus.Completed,
                HasAnyDuePeriod = parent.HasAnyDuePeriod,
                HasOverduePeriod = parent.HasOverduePeriod,
                LatestPeriodKey = parent.LatestPeriodKey,
                LatestDueAtUtc = parent.LatestDueAtUtc
            });
        }

        var activeChildren = directChildren
            .Where(x => x.IsActive && !x.IsDeleted)
            .ToList();

        if (activeChildren.Count == 0)
        {
            return Task.FromResult(new ProgressComputeResult
            {
                ProgressStatus = (int)WorkAssignmentProgressStatus.NotStarted,
                HasAnyDuePeriod = false,
                HasOverduePeriod = false,
                LatestPeriodKey = null,
                LatestDueAtUtc = null
            });
        }

        var statuses = activeChildren.Select(x => x.ProgressStatus).ToList();

        int status;
        if (statuses.Any(x => x == (int)WorkAssignmentProgressStatus.Overdue))
            status = (int)WorkAssignmentProgressStatus.Overdue;
        else if (statuses.Any(x => x == (int)WorkAssignmentProgressStatus.AtRiskOverdue))
            status = (int)WorkAssignmentProgressStatus.AtRiskOverdue;
        else if (statuses.All(x => x == (int)WorkAssignmentProgressStatus.NotStarted))
            status = (int)WorkAssignmentProgressStatus.NotStarted;
        else
            status = (int)WorkAssignmentProgressStatus.InProgress;

        var latestDueChild = activeChildren
            .Where(x => x.LatestDueAtUtc.HasValue)
            .OrderByDescending(x => x.LatestDueAtUtc)
            .FirstOrDefault();

        return Task.FromResult(new ProgressComputeResult
        {
            ProgressStatus = status,
            HasAnyDuePeriod = activeChildren.Any(x => x.HasAnyDuePeriod),
            HasOverduePeriod = activeChildren.Any(x => x.HasOverduePeriod),
            LatestPeriodKey = latestDueChild?.LatestPeriodKey,
            LatestDueAtUtc = latestDueChild?.LatestDueAtUtc
        });
    }

    public async Task<ProgressRecomputeResult> RecomputeSingleAsync(
        string workAssignmentId,
        CancellationToken ct)
    {
        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == workAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND,
                new { assignmentId = workAssignmentId });

        return await RecomputeSingleAsync(assignment, ct);
    }

    public async Task<ProgressRecomputeResult> RecomputeSingleAsync(
        WorkAssignment assignment,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var current = assignment;

        for (var attempt = 1; attempt <= MaxProgressCasAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var oldStatus = current.ProgressStatus;
            var computed = await ComputeProgressAsync(current, ct);
            var worstFacts = await ComputeWorstFactsAsync(current, ct);
            var changed =
                current.ProgressStatus != computed.ProgressStatus ||
                current.HasAnyDuePeriod != computed.HasAnyDuePeriod ||
                current.HasOverduePeriod != computed.HasOverduePeriod ||
                current.LatestPeriodKey != computed.LatestPeriodKey ||
                current.LatestDueAtUtc != computed.LatestDueAtUtc ||
                current.WorstPeriodStatus != worstFacts.WorstPeriodStatus ||
                current.WorstOverdueReasonCode != worstFacts.WorstOverdueReasonCode ||
                current.WorstOverdueReasonLabel != worstFacts.WorstOverdueReasonLabel;

            if (!changed)
                return BuildRecomputeResult(current, oldStatus, computed, changed: false);

            var now = DateTime.UtcNow;
            var update = Builders<WorkAssignment>.Update
                .Set(x => x.ProgressStatus, computed.ProgressStatus)
                .Set(x => x.ProgressStatusUpdatedAtUtc, now)
                .Set(x => x.HasAnyDuePeriod, computed.HasAnyDuePeriod)
                .Set(x => x.HasOverduePeriod, computed.HasOverduePeriod)
                .Set(x => x.LatestPeriodKey, computed.LatestPeriodKey)
                .Set(x => x.LatestDueAtUtc, computed.LatestDueAtUtc)
                .Set(x => x.WorstPeriodStatus, worstFacts.WorstPeriodStatus)
                .Set(x => x.WorstOverdueReasonCode, worstFacts.WorstOverdueReasonCode)
                .Set(x => x.WorstOverdueReasonLabel, worstFacts.WorstOverdueReasonLabel);

            var expectedCompletedAtUtc = current.CompletedAtUtc;
            var expectedProgressStatusUpdatedAtUtc = current.ProgressStatusUpdatedAtUtc;
            var expectedMaterializationRevision = current.DynamicFlowMaterializationRevision;
            var expectedReportLifecycleSeriesRevision =
                current.ReportLifecycleSeriesRevision;
            var updateResult = await _ctx.WorkAssignments.UpdateOneAsync(
                BuildObservedProgressCasFilter(
                    current,
                    expectedCompletedAtUtc,
                    expectedProgressStatusUpdatedAtUtc,
                    expectedMaterializationRevision,
                    expectedReportLifecycleSeriesRevision),
                update,
                cancellationToken: ct);
            if (updateResult.MatchedCount == 0)
            {
                if (attempt >= MaxProgressCasAttempts)
                {
                    throw new InvalidOperationException(
                        $"WORK_ASSIGNMENT_PROGRESS_CAS_RETRY_EXHAUSTED assignmentId={current.Id};attempts={MaxProgressCasAttempts}");
                }

                current = await _ctx.WorkAssignments
                    .Find(x => x.Id == current.Id && !x.IsDeleted)
                    .FirstOrDefaultAsync(ct)
                    ?? throw AppExceptionFactory.NotFound(
                        AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND,
                        new { assignmentId = current.Id });
                continue;
            }

            current.ProgressStatus = computed.ProgressStatus;
            current.ProgressStatusUpdatedAtUtc = now;
            current.HasAnyDuePeriod = computed.HasAnyDuePeriod;
            current.HasOverduePeriod = computed.HasOverduePeriod;
            current.LatestPeriodKey = computed.LatestPeriodKey;
            current.LatestDueAtUtc = computed.LatestDueAtUtc;
            current.WorstPeriodStatus = worstFacts.WorstPeriodStatus;
            current.WorstOverdueReasonCode = worstFacts.WorstOverdueReasonCode;
            current.WorstOverdueReasonLabel = worstFacts.WorstOverdueReasonLabel;
            return BuildRecomputeResult(current, oldStatus, computed, changed: true);
        }

        throw new InvalidOperationException(
            $"WORK_ASSIGNMENT_PROGRESS_CAS_RETRY_EXHAUSTED assignmentId={current.Id};attempts={MaxProgressCasAttempts}");
    }

    public async Task<List<ProgressRecomputeResult>> RecomputeDirectChildrenAsync(
        string parentAssignmentId,
        CancellationToken ct)
    {
        var children = await _ctx.WorkAssignments
            .Find(x => x.ParentAssignmentId == parentAssignmentId && x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        if (children.Count == 0)
            return new List<ProgressRecomputeResult>();

        var childWorkIds = children
            .Select(x => x.WorkId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var works = await _ctx.Works
            .Find(x => childWorkIds.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(ct);

        var workMap = works.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);

        var results = new List<ProgressRecomputeResult>(children.Count);

        foreach (var child in children)
        {
            var oldStatus = child.ProgressStatus;

            ProgressComputeResult computed;
            var hasChildren = await _ctx.WorkAssignments
                .Find(x => x.ParentAssignmentId == child.Id && x.IsActive && !x.IsDeleted)
                .AnyAsync(ct);

            if (hasChildren)
            {
                computed = await ComputeProgressAsync(child, ct);
            }
            else
            {
                if (!workMap.TryGetValue(child.WorkId, out var work))
                    throw AssignmentWorkNotFound(child);

                computed = await ComputeLeafProgressAsync(child, work, ct);
            }

            var worstFacts = await ComputeWorstFactsAsync(child, ct);

            var changed =
                child.ProgressStatus != computed.ProgressStatus ||
                child.HasAnyDuePeriod != computed.HasAnyDuePeriod ||
                child.HasOverduePeriod != computed.HasOverduePeriod ||
                child.LatestPeriodKey != computed.LatestPeriodKey ||
                child.LatestDueAtUtc != computed.LatestDueAtUtc ||
                child.WorstPeriodStatus != worstFacts.WorstPeriodStatus ||
                child.WorstOverdueReasonCode != worstFacts.WorstOverdueReasonCode ||
                child.WorstOverdueReasonLabel != worstFacts.WorstOverdueReasonLabel;

            if (changed)
            {
                var now = DateTime.UtcNow;

                var update = Builders<WorkAssignment>.Update
                    .Set(x => x.ProgressStatus, computed.ProgressStatus)
                    .Set(x => x.ProgressStatusUpdatedAtUtc, now)
                    .Set(x => x.HasAnyDuePeriod, computed.HasAnyDuePeriod)
                    .Set(x => x.HasOverduePeriod, computed.HasOverduePeriod)
                    .Set(x => x.LatestPeriodKey, computed.LatestPeriodKey)
                    .Set(x => x.LatestDueAtUtc, computed.LatestDueAtUtc)
                    .Set(x => x.WorstPeriodStatus, worstFacts.WorstPeriodStatus)
                    .Set(x => x.WorstOverdueReasonCode, worstFacts.WorstOverdueReasonCode)
                    .Set(x => x.WorstOverdueReasonLabel, worstFacts.WorstOverdueReasonLabel);

                var expectedCompletedAtUtc = child.CompletedAtUtc;
                var expectedProgressStatusUpdatedAtUtc = child.ProgressStatusUpdatedAtUtc;
                var expectedMaterializationRevision = child.DynamicFlowMaterializationRevision;
                var expectedReportLifecycleSeriesRevision =
                    child.ReportLifecycleSeriesRevision;
                var updateResult = await _ctx.WorkAssignments.UpdateOneAsync(
                    BuildObservedProgressCasFilter(
                        child,
                        expectedCompletedAtUtc,
                        expectedProgressStatusUpdatedAtUtc,
                        expectedMaterializationRevision,
                        expectedReportLifecycleSeriesRevision),
                    update,
                    cancellationToken: ct);
                if (updateResult.MatchedCount == 0)
                {
                    var refreshed = await _ctx.WorkAssignments
                        .Find(x => x.Id == child.Id && !x.IsDeleted)
                        .FirstOrDefaultAsync(ct);
                    if (refreshed is not null)
                        results.Add(await RecomputeSingleAsync(refreshed, ct));
                    continue;
                }

                child.ProgressStatus = computed.ProgressStatus;
                child.ProgressStatusUpdatedAtUtc = now;
                child.HasAnyDuePeriod = computed.HasAnyDuePeriod;
                child.HasOverduePeriod = computed.HasOverduePeriod;
                child.LatestPeriodKey = computed.LatestPeriodKey;
                child.LatestDueAtUtc = computed.LatestDueAtUtc;
                child.WorstPeriodStatus = worstFacts.WorstPeriodStatus;
                child.WorstOverdueReasonCode = worstFacts.WorstOverdueReasonCode;
                child.WorstOverdueReasonLabel = worstFacts.WorstOverdueReasonLabel;
            }

            results.Add(new ProgressRecomputeResult
            {
                WorkAssignmentId = child.Id,
                OldStatus = oldStatus,
                NewStatus = computed.ProgressStatus,
                Changed = changed,
                HasAnyDuePeriod = computed.HasAnyDuePeriod,
                HasOverduePeriod = computed.HasOverduePeriod,
                LatestPeriodKey = computed.LatestPeriodKey,
                LatestDueAtUtc = computed.LatestDueAtUtc
            });
        }

        return results;
    }

    public async Task<List<ProgressRecomputeResult>> RecomputeParentChainAsync(
        string workAssignmentId,
        CancellationToken ct)
    {
        var results = new List<ProgressRecomputeResult>();

        var current = await _ctx.WorkAssignments
            .Find(x => x.Id == workAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        while (current != null && !string.IsNullOrWhiteSpace(current.ParentAssignmentId))
        {
            var parent = await _ctx.WorkAssignments
                .Find(x => x.Id == current.ParentAssignmentId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);

            if (parent == null)
                break;

            var recompute = await RecomputeSingleAsync(parent, ct);
            results.Add(recompute);
            current = parent;
        }

        return results;
    }

    private async Task<LeafProgressFacts> BuildLeafProgressFactsAsync(
        WorkAssignment assignment,
        Work work,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var isOnceAssignment = IsOnceAssignment(assignment);
        var parent = await LoadParentAssignmentAsync(assignment, ct);
        var facts = new LeafProgressFacts
        {
            NowUtc = nowUtc,
            HasSchedule = assignment.Schedule != null,
            IsEnded = ResolveAssignmentEndUtc(assignment, work, parent) is { } endUtc && endUtc < nowUtc
        };

        var materializedPeriods = await _ctx.WorkReportPeriods
            .Find(x => x.WorkAssignmentId == assignment.Id && x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        var progressPeriods = materializedPeriods
            .Where(WorkAssignmentReportTemporalPolicy.ContributesToProgress)
            .ToList();

        if (progressPeriods.Count > 0)
        {
            ApplyMaterializedPeriodFacts(facts, progressPeriods, assignment, work, parent, nowUtc);
            return facts;
        }

        var workStartDate = ResolveAssignmentStartUtc(assignment, nowUtc);
        var workEndDate = ResolveAssignmentEndUtc(assignment, work, parent);

        var rangeTo = nowUtc;
        if (workEndDate.HasValue && workEndDate.Value < nowUtc)
            rangeTo = workEndDate.Value;

        if (rangeTo < workStartDate)
            return facts;

        var dueItems = isOnceAssignment
            ? BuildOnceDueItemsInRange(assignment, workStartDate, rangeTo)
            : AssignmentScheduleDueHelper.GetDueItemsInRange(
                assignment.Schedule,
                workStartDate,
                rangeTo);

        if (dueItems.Count == 0)
        {
            if (workStartDate <= nowUtc &&
                (!workEndDate.HasValue || workEndDate.Value >= nowUtc))
            {
                facts.HasAnyOpenPeriod = true;
            }

            return facts;
        }

        facts.HasAnyDuePeriod = true;
        facts.LatestDueAtUtc = dueItems.Last().DueAtUtc;
        facts.LatestPeriodKey = dueItems.Last().PeriodKey;
        facts.HasDueButNotApproved = true;
        facts.HasOverduePeriod = dueItems.Any(x => x.DueAtUtc < nowUtc);

        if (!facts.IsEnded &&
            workStartDate <= nowUtc &&
            (!workEndDate.HasValue || workEndDate.Value >= nowUtc))
        {
            facts.HasAnyOpenPeriod = true;
        }

        return facts;
    }

    private static void ApplyMaterializedPeriodFacts(
        LeafProgressFacts facts,
        List<WorkReportPeriod> periods,
        WorkAssignment assignment,
        Work work,
        WorkAssignment? parent,
        DateTime nowUtc)
    {
        facts.HasMaterializedPeriods = true;
        facts.AreAllPeriodsApprovedWithinScope = periods.All(x => IsApprovedPeriodStatus(x.Status));
        facts.HasOverduePeriod = periods.Any(x => IsOverduePeriodStatus(x.Status));

        var duePeriods = periods
            .Where(x => !x.DueAtUtc.HasValue || x.DueAtUtc.Value <= nowUtc)
            .OrderBy(x => x.DueAtUtc)
            .ThenBy(x => x.PeriodKey)
            .ToList();

        facts.HasAnyDuePeriod = duePeriods.Count > 0;
        facts.HasDueButNotApproved = duePeriods.Any(x => !IsApprovedPeriodStatus(x.Status));

        var latestDue = duePeriods.LastOrDefault()
            ?? periods
                .OrderBy(x => x.DueAtUtc)
                .ThenBy(x => x.PeriodKey)
                .FirstOrDefault();

        facts.LatestDueAtUtc = latestDue?.DueAtUtc;
        facts.LatestPeriodKey = latestDue?.PeriodKey;

        var startUtc = ResolveAssignmentStartUtc(assignment, nowUtc);
        var endUtc = ResolveAssignmentEndUtc(assignment, work, parent);
        if (startUtc <= nowUtc && (!endUtc.HasValue || endUtc.Value >= nowUtc))
            facts.HasAnyOpenPeriod = true;
    }

    private static List<AssignmentScheduleDueItem> BuildOnceDueItemsInRange(
        WorkAssignment assignment,
        DateTime fromUtc,
        DateTime toUtc)
    {
        if (!assignment.DueAtUtc.HasValue)
            return new List<AssignmentScheduleDueItem>();

        var dueAtUtc = assignment.DueAtUtc.Value;
        if (dueAtUtc < fromUtc || dueAtUtc > toUtc)
            return new List<AssignmentScheduleDueItem>();

        return new List<AssignmentScheduleDueItem>
        {
            new()
            {
                DueAtUtc = dueAtUtc,
                PeriodKey = dueAtUtc.Date.ToString("yyyyMMdd")
            }
        };
    }

    private static DateTime ResolveAssignmentStartUtc(WorkAssignment assignment, DateTime nowUtc)
    {
        var start = WorkAssignmentDatePolicy.ResolveEffectiveStartDate(assignment, nowUtc);
        if (assignment.Schedule?.StartDate is { } scheduleStart && scheduleStart.Date > start)
            start = scheduleStart.Date;

        return start;
    }

    private static DateTime? ResolveAssignmentEndUtc(
        WorkAssignment assignment,
        Work work,
        WorkAssignment? parent)
    {
        return WorkAssignmentDatePolicy.ResolveEffectiveCompletedDate(assignment, work, parent)
               ?? (IsOnceAssignment(assignment) ? assignment.DueAtUtc : null);
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

    private static bool IsOnceAssignment(WorkAssignment assignment)
        => string.Equals(assignment.AssignmentType, WorkAssignmentTypes.Once, StringComparison.OrdinalIgnoreCase);

    private static bool IsManuallyCompleted(WorkAssignment assignment)
        => assignment.CompletedAtUtc.HasValue ||
           (assignment.ProgressStatus == (int)WorkAssignmentProgressStatus.Completed &&
            assignment.CompletedDate.HasValue);

    private static bool IsApprovedPeriodStatus(WorkReportPeriodStatus status)
        => WorkReportPeriodStatusHelper.IsTerminal(status);

    private static bool IsOverduePeriodStatus(WorkReportPeriodStatus status)
        => WorkReportPeriodStatusHelper.IsOverdue(status);

    private async Task<WorstFacts> ComputeWorstFactsAsync(WorkAssignment assignment, CancellationToken ct)
    {
        var children = await _ctx.WorkAssignments
            .Find(x => x.ParentAssignmentId == assignment.Id && x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        if (children.Count > 0)
        {
            var worstChild = children
                .OrderByDescending(GetWorstPeriodRank)
                .ThenByDescending(GetWorstReasonRank)
                .ThenByDescending(x => x.ProgressStatus)
                .ThenByDescending(x => x.LatestDueAtUtc)
                .FirstOrDefault();

            return new WorstFacts
            {
                WorstPeriodStatus = worstChild?.WorstPeriodStatus,
                WorstOverdueReasonCode = worstChild?.WorstOverdueReasonCode,
                WorstOverdueReasonLabel = worstChild?.WorstOverdueReasonLabel
            };
        }

        var periods = await _ctx.WorkReportPeriods
            .Find(x => x.WorkAssignmentId == assignment.Id && x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        periods = periods
            .Where(WorkAssignmentReportTemporalPolicy.ContributesToProgress)
            .ToList();

        if (periods.Count == 0)
        {
            return new WorstFacts();
        }

        var worstPeriod = periods
            .OrderByDescending(GetWorstPeriodRank)
            .ThenByDescending(x => x.DueAtUtc)
            .ThenByDescending(x => x.PeriodKey)
            .First();

        var (reasonCode, reasonLabel) = MapWorstReason(worstPeriod.Status);

        return new WorstFacts
        {
            WorstPeriodStatus = (int)worstPeriod.Status,
            WorstOverdueReasonCode = reasonCode,
            WorstOverdueReasonLabel = reasonLabel
        };
    }

    private static int GetWorstPeriodRank(WorkAssignment assignment)
        => GetWorstPeriodRank(assignment.WorstPeriodStatus);

    private static int GetWorstPeriodRank(WorkReportPeriod period)
        => GetWorstPeriodRank((int?)period.Status);

    private static int GetWorstPeriodRank(int? status)
        => status.HasValue
            ? WorkReportPeriodStatusHelper.GetPeriodRiskRank((WorkReportPeriodStatus)status.Value)
            : -1;

    private static int GetWorstReasonRank(WorkAssignment assignment)
    {
        return assignment.WorstOverdueReasonCode switch
        {
            "OVERDUE_SUBMITTED_WAITING_REVIEW" => 3,
            "OVERDUE_DRAFT" => 2,
            "OVERDUE_NOT_STARTED" => 1,
            "OVERDUE_APPROVED" => 0,
            _ => 0
        };
    }

    private static (string? Code, string? Label) MapWorstReason(WorkReportPeriodStatus status)
    {
        return status switch
        {
            WorkReportPeriodStatus.OverdueSubmitted => ("OVERDUE_SUBMITTED_WAITING_REVIEW", "Đã nộp nhưng quá hạn chờ duyệt"),
            WorkReportPeriodStatus.OverdueDraft => ("OVERDUE_DRAFT", "Đã lưu nháp nhưng quá hạn"),
            WorkReportPeriodStatus.OverduePending => ("OVERDUE_NOT_STARTED", "Chưa nộp báo cáo, đã quá hạn"),
            WorkReportPeriodStatus.OverdueApproved => ("OVERDUE_APPROVED", "Đã duyệt nhưng quá hạn"),
            _ => (null, null)
        };
    }

    private static ProgressRecomputeResult BuildRecomputeResult(
        WorkAssignment assignment,
        int oldStatus,
        ProgressComputeResult computed,
        bool changed)
        => new()
        {
            WorkAssignmentId = assignment.Id,
            OldStatus = oldStatus,
            NewStatus = computed.ProgressStatus,
            Changed = changed,
            HasAnyDuePeriod = computed.HasAnyDuePeriod,
            HasOverduePeriod = computed.HasOverduePeriod,
            LatestPeriodKey = computed.LatestPeriodKey,
            LatestDueAtUtc = computed.LatestDueAtUtc
        };

    private static FilterDefinition<WorkAssignment> BuildObservedProgressCasFilter(
        WorkAssignment assignment,
        DateTime? expectedCompletedAtUtc,
        DateTime? expectedProgressStatusUpdatedAtUtc,
        long expectedMaterializationRevision,
        long expectedReportLifecycleSeriesRevision)
    {
        var filter = Builders<WorkAssignment>.Filter;
        return filter.Eq(x => x.Id, assignment.Id) &
               filter.Eq(x => x.IsDeleted, false) &
               filter.Eq(x => x.IsActive, assignment.IsActive) &
               filter.Eq(x => x.CompletedAtUtc, expectedCompletedAtUtc) &
               filter.Eq(
                   x => x.ProgressStatusUpdatedAtUtc,
                   expectedProgressStatusUpdatedAtUtc) &
               BuildDynamicFlowMaterializationRevisionFilter(
                   expectedMaterializationRevision) &
               BuildReportLifecycleSeriesRevisionFilter(
                   expectedReportLifecycleSeriesRevision);
    }

    private static FilterDefinition<WorkAssignment>
        BuildDynamicFlowMaterializationRevisionFilter(long expectedRevision)
    {
        var filter = Builders<WorkAssignment>.Filter;
        var exact = filter.Eq(
            x => x.DynamicFlowMaterializationRevision,
            expectedRevision);
        return expectedRevision == 0
            ? filter.Or(
                exact,
                filter.Exists(
                    x => x.DynamicFlowMaterializationRevision,
                    false))
            : exact;
    }

    private static FilterDefinition<WorkAssignment>
        BuildReportLifecycleSeriesRevisionFilter(long expectedRevision)
    {
        var filter = Builders<WorkAssignment>.Filter;
        var exact = filter.Eq(
            x => x.ReportLifecycleSeriesRevision,
            expectedRevision);
        return expectedRevision == 0
            ? filter.Or(
                exact,
                filter.Exists(
                    x => x.ReportLifecycleSeriesRevision,
                    false))
            : exact;
    }

    private static AppException AssignmentWorkNotFound(WorkAssignment assignment)
        => AppExceptionFactory.NotFound(
            AppErrorCode.WORK_ASSIGNMENT_WORK_NOT_FOUND,
            new { assignmentId = assignment.Id, workId = assignment.WorkId });

    private sealed class WorstFacts
    {
        public int? WorstPeriodStatus { get; set; }
        public string? WorstOverdueReasonCode { get; set; }
        public string? WorstOverdueReasonLabel { get; set; }
    }
}
