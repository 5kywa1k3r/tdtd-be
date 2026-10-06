using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.Common;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.WorkAssignments.Queue;
using tdtd_be.Services.WorkAssignments.Runtime;

namespace tdtd_be.Services.WorkAssignmentReports.Runtime;

public interface IWorkReportLifecycleProjectionReconciler
{
    Task<bool> ReconcileReportAsync(string reportId, CancellationToken ct = default);
    Task<bool> RepairCurrentPeriodAsync(
        string workReportPeriodId,
        string actorUserId,
        CancellationToken ct = default);
    Task<int> ProcessPendingAsync(int maxReports, CancellationToken ct = default);
}

/// <summary>
/// Repairs post-lifecycle projections from current database state. Outbox entries describe why
/// work is pending, but never act as a stale state snapshot: every projection is derived again
/// after the report-level lease is acquired.
/// </summary>
public sealed class WorkReportLifecycleProjectionReconciler : IWorkReportLifecycleProjectionReconciler
{
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(10);
    private const int MaxStoredErrorLength = 4000;

    private readonly MongoDbContext _ctx;
    private readonly IConfiguration _configuration;
    private readonly IWorkAssignmentQueueService _queue;
    private readonly IWorkAssignmentStatusSyncService _statusSync;
    private readonly IDocRoleReadModelProjectionService _docRoleProjection;
    private readonly IWorkReportLabelStatisticsService _labelStatistics;
    private readonly IWorkReportTableStatisticsService _tableStatistics;
    private readonly IWorkReportFieldStatisticsService _fieldStatistics;
    private readonly IWorkReportAggregateDependentRecoveryService _aggregateDependentRecovery;
    private readonly IWorkAssignmentReportSectionProjectionService _sectionProjection;
    private readonly IWorkReportLifecycleBusinessLogProjector _businessLogProjector;
    private readonly IDynamicFlowRuntimeStateProjector _dynamicFlowRuntimeStateProjector;
    private readonly IWorkReportLifecycleProjectionFaultInjector _faultInjector;
    private readonly IWorkAssignmentAdvancedSummaryDirtyService _advancedSummaryDirty;
    private readonly IDynamicFlowDefinitionTransactionRunner _transactions;
    private readonly IDynamicFlowRuntimeActivationPolicy _runtimeActivation;
    private readonly IStatRunDirectProjectionService _directProjection;
    private readonly ILogger<WorkReportLifecycleProjectionReconciler> _logger;

    public WorkReportLifecycleProjectionReconciler(
        MongoDbContext ctx,
        IConfiguration configuration,
        IWorkAssignmentQueueService queue,
        IWorkAssignmentStatusSyncService statusSync,
        IDocRoleReadModelProjectionService docRoleProjection,
        IWorkReportLabelStatisticsService labelStatistics,
        IWorkReportTableStatisticsService tableStatistics,
        IWorkReportFieldStatisticsService fieldStatistics,
        IWorkReportAggregateDependentRecoveryService aggregateDependentRecovery,
        IWorkAssignmentReportSectionProjectionService sectionProjection,
        IWorkReportLifecycleBusinessLogProjector businessLogProjector,
        IDynamicFlowRuntimeStateProjector dynamicFlowRuntimeStateProjector,
        IWorkReportLifecycleProjectionFaultInjector faultInjector,
        IWorkAssignmentAdvancedSummaryDirtyService advancedSummaryDirty,
        IDynamicFlowDefinitionTransactionRunner transactions,
        IDynamicFlowRuntimeActivationPolicy runtimeActivation,
        IStatRunDirectProjectionService directProjection,
        ILogger<WorkReportLifecycleProjectionReconciler> logger)
    {
        _ctx = ctx;
        _configuration = configuration;
        _queue = queue;
        _statusSync = statusSync;
        _docRoleProjection = docRoleProjection;
        _labelStatistics = labelStatistics;
        _tableStatistics = tableStatistics;
        _fieldStatistics = fieldStatistics;
        _aggregateDependentRecovery = aggregateDependentRecovery;
        _sectionProjection = sectionProjection;
        _businessLogProjector = businessLogProjector;
        _dynamicFlowRuntimeStateProjector = dynamicFlowRuntimeStateProjector;
        _faultInjector = faultInjector;
        _advancedSummaryDirty = advancedSummaryDirty;
        _transactions = transactions;
        _runtimeActivation = runtimeActivation;
        _directProjection = directProjection;
        _logger = logger;
    }

    private async Task WakeAggregateAsync(string workId, CancellationToken ct)
    {
        try { await Services.AggregateMapping.Persistence.AggregateHostIntegration.DispatchAsync(_ctx, _transactions, _configuration, 100, ct, workId); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { _logger.LogWarning(ex, "Aggregate queue wake deferred; durable source invalidation remains pending. workId={WorkId}", workId); }
    }

    public async Task<bool> ReconcileReportAsync(string reportId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reportId))
            return false;

        var claim = await TryClaimAsync(reportId.Trim(), Array.Empty<string>(), ct);
        // The source command already persisted Aggregate invalidation in its transaction.
        // Wake its existing queue before slower projections/statistics; never calculate here.
        var sourceWorkId = claim?.Report.WorkId ?? await _ctx.WorkAssignmentReports.Find(r => r.Id == reportId.Trim())
            .Project(r => r.WorkId).FirstOrDefaultAsync(ct);
        if (sourceWorkId != null) await WakeAggregateAsync(sourceWorkId, ct);
        if (claim is null)
            return false;

        try
        {
            await ReconcileClaimAsync(claim, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The lifecycle command and pending entry are already durable. Foreground callers
            // therefore receive the committed result while the recurring worker retries repair.
            _logger.LogError(
                ex,
                "Lifecycle command committed with projection pending. reportId={ReportId} claimToken={ClaimToken}",
                claim.Report.Id,
                claim.Token);
            return false;
        }
    }

    /// <summary>
    /// Reprojects every lifecycle-derived period field from the current active
    /// report. The caller must hold the assignment lifecycle-series lease.
    /// </summary>
    public async Task<bool> RepairCurrentPeriodAsync(
        string workReportPeriodId,
        string actorUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workReportPeriodId))
            return false;

        var triggeringReport = await LoadAuthoritativeReportAsync(
            workReportPeriodId.Trim(),
            ct);
        triggeringReport ??= await LoadLatestPeriodReportAsync(
            workReportPeriodId.Trim(),
            ct);
        if (triggeringReport is null)
            return false;

        var state = await ReconcilePeriodAsync(
            triggeringReport,
            string.IsNullOrWhiteSpace(actorUserId) ? "system" : actorUserId.Trim(),
            ct);
        return state.Period is not null;
    }

    public async Task<int> ProcessPendingAsync(int maxReports, CancellationToken ct = default)
    {
        maxReports = Math.Clamp(maxReports, 1, 200);
        var attemptedReportIds = new List<string>(maxReports);

        while (attemptedReportIds.Count < maxReports)
        {
            ct.ThrowIfCancellationRequested();
            var claim = await TryClaimAsync(null, attemptedReportIds, ct);
            if (claim is null)
                break;

            attemptedReportIds.Add(claim.Report.Id);
            try
            {
                await ReconcileClaimAsync(claim, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Lifecycle projection reconciliation failed. reportId={ReportId} claimToken={ClaimToken}",
                    claim.Report.Id,
                    claim.Token);
            }
        }

        var backfilled = 0;
        var remaining = maxReports - attemptedReportIds.Count;
        if (remaining > 0 && _directProjection.IsCandidateEnabled())
        {
            backfilled = await _directProjection.BackfillAcknowledgedAsync(
                remaining,
                ct);
        }

        var aggregateDispatched = await tdtd_be.Services.AggregateMapping.Persistence.AggregateHostIntegration.DispatchAsync(
            _ctx, _transactions, _configuration, maxReports, ct);
        return attemptedReportIds.Count + backfilled + aggregateDispatched;
    }

    private async Task ReconcileClaimAsync(LifecycleProjectionClaim claim, CancellationToken ct)
    {
        var projectedRevision = claim.Report.LifecycleRevision;
        IReadOnlyCollection<string> attemptedEntryKeys = Array.Empty<string>();
        try
        {
            // A lifecycle command may have committed after the claim. Always reload so an older
            // delayed entry projects the newest report state rather than its captured target state.
            var report = await _ctx.WorkAssignmentReports
                .Find(x => x.Id == claim.Report.Id && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException($"Lifecycle projection report '{claim.Report.Id}' no longer exists.");
            projectedRevision = report.LifecycleRevision;

            var pending = (report.LifecycleProjectionOutbox ?? new List<WorkReportLifecycleProjectionOutboxEntry>())
                .Where(x =>
                    string.Equals(x.State, WorkReportLifecycleProjectionOutboxStates.Pending, StringComparison.Ordinal) &&
                    x.LifecycleRevision <= projectedRevision)
                .OrderBy(x => x.LifecycleRevision)
                .ThenBy(x => x.EntryKey, StringComparer.Ordinal)
                .ToList();
            if (pending.Count == 0)
            {
                await ReleaseClaimAsync(claim, ct);
                return;
            }
            attemptedEntryKeys = pending
                .Select(x => x.EntryKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            // Resolve both activation boundaries before any statistics write. When the P9
            // lifecycle route is proven, it exclusively owns Direct writes; otherwise this
            // preserves the P8 phase/binding deferral and legacy rebuild behavior byte-for-byte.
            var directProjectionCandidateEnabled =
                _directProjection.IsCandidateEnabled();
            var hasDynamicFlowMappingBinding =
                ShouldDeferDynamicFlowMappingStatistics(report);
            var deferDynamicFlowMappingStatistics =
                directProjectionCandidateEnabled ||
                StatConfigPhaseBarrier.IsBlocked(
                    StatConfigPhaseBarrierEntries.P9Projection) ||
                hasDynamicFlowMappingBinding;

            _faultInjector.ThrowIfConfigured(pending);

            var actorUserId = ResolveActorUserId(report, pending[^1]);
            await RenewClaimAsync(claim, ct);
            // This must create and repair the complete section set before the durable entry can
            // be acknowledged; an UpdateMany-only lifecycle sync would silently miss absent rows.
            await _sectionProjection.ProjectCurrentAndVerifyAsync(
                report,
                actorUserId,
                DateTime.UtcNow,
                ct);
            await RenewClaimAsync(claim, ct);
            var sourceMutationEventId =
                DynamicFlowMappingLifecycleContract.ComputeStableObjectId(
                    $"source-drift\n{report.Id}\n{pending[^1].EntryKey}");
            await DynamicFlowMappingLifecycleContract
                .InvalidateSourceDependentsAsync(
                    _ctx,
                    _transactions,
                    report,
                    sourceMutationEventId,
                    actorUserId,
                    DateTime.UtcNow,
                    _runtimeActivation
                        .P7MappingLifecycleExecutionEnabled,
                    ct);
            await RenewClaimAsync(claim, ct);
            var periodState = await ReconcilePeriodAsync(report, actorUserId, ct);
            await WakeAggregateAsync(report.WorkId, ct);

            await RenewClaimAsync(claim, ct);
            if (periodState.Period is not null)
                await _queue.UpsertPeriodAsync(periodState.Period, actorUserId, ct);

            await _statusSync.SyncFromAssignmentAsync(report.WorkAssignmentId, ct);
            if (periodState.Period is not null)
                await _docRoleProjection.RebuildReportPeriodAsync(periodState.Period.Id, actorUserId, ct);

            await RenewClaimAsync(claim, ct);
            await _dynamicFlowRuntimeStateProjector.ProjectReportLifecycleAsync(
                report,
                pending,
                ct);
            await RenewClaimAsync(claim, ct);
            var dirtyRange = WorkReportLifecycleProjectionPolicy.ResolveDirtyRange(
                pending,
                report);
            if (directProjectionCandidateEnabled)
            {
                await MarkP9DependentResultsDirtyAsync(
                    report,
                    dirtyRange,
                    actorUserId,
                    ct);
                await RenewClaimAsync(claim, ct);

                var directEntry = ResolveCurrentDirectProjectionEntry(
                    report,
                    pending);
                if (directEntry is not null)
                {
                    await _directProjection.ProjectLifecycleEntryAsync(
                        report.Id,
                        directEntry.EntryKey,
                        ResolveActorUserId(report, directEntry),
                        ct);
                    await RenewClaimAsync(claim, ct);
                }
            }

            if (!deferDynamicFlowMappingStatistics)
            {
                foreach (var reportId in new[]
                         {
                             report.Id,
                             periodState.AuthoritativeReport?.Id
                         }
                         .Where(x => !string.IsNullOrWhiteSpace(x))
                         .Select(x => x!)
                         .Distinct(StringComparer.Ordinal))
                {
                    await RenewClaimAsync(claim, ct);
                    await _labelStatistics.RebuildForReportAsync(
                        reportId,
                        actorUserId,
                        ct);
                    await _tableStatistics.RebuildForReportAsync(
                        reportId,
                        actorUserId,
                        ct);
                    await _fieldStatistics.RebuildForReportAsync(
                        reportId,
                        actorUserId,
                        ct);
                }
            }

            await RenewClaimAsync(claim, ct);
            await _aggregateDependentRecovery.RecoverPendingAsync(
                report,
                pending,
                actorUserId,
                ct);
            await RenewClaimAsync(claim, ct);

            if (!deferDynamicFlowMappingStatistics)
            {
                await _advancedSummaryDirty.MarkReportStatusMutationDirtyAsync(
                    report,
                    dirtyRange.Operation,
                    dirtyRange.FromStatus,
                    dirtyRange.ToStatus,
                    actorUserId,
                    ct);
            }

            await RenewClaimAsync(claim, ct);
            await _businessLogProjector.ProjectAndVerifyAsync(
                report,
                pending,
                periodState.Period,
                actorUserId,
                ct);
            await RenewClaimAsync(claim, ct);

            await CompletePendingEntriesAsync(claim, projectedRevision, attemptedEntryKeys, ct);
        }
        catch (Exception ex)
        {
            await RecordFailureAndReleaseClaimAsync(claim, attemptedEntryKeys, ex, CancellationToken.None);
            throw;
        }
    }

    private static bool ShouldDeferDynamicFlowMappingStatistics(
        WorkAssignmentReport report)
        => DynamicFlowMappingLifecycleBinding.FromReport(report) is not null;

    private static WorkReportLifecycleProjectionOutboxEntry?
        ResolveCurrentApprovedDirectProjectionEntry(
            WorkAssignmentReport report,
            IReadOnlyCollection<WorkReportLifecycleProjectionOutboxEntry> pending)
    {
        return pending
            .Where(entry =>
                (entry.Operation is "REVIEW_APPROVE" or "REVIEW_CONFIRM_AUTO_APPROVE") &&
                string.Equals(entry.FromStatus, "SUBMITTED", StringComparison.Ordinal) &&
                string.Equals(entry.ToStatus, "APPROVED", StringComparison.Ordinal) &&
                entry.ToIsActive &&
                entry.LifecycleRevision == report.LifecycleRevision &&
                entry.PayloadRevision == report.PayloadRevision &&
                string.Equals(entry.PayloadHash, report.PayloadHash, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.CreatedAtUtc)
            .ThenByDescending(entry => entry.EntryKey, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static WorkReportLifecycleProjectionOutboxEntry?
        ResolveCurrentDirectProjectionEntry(
            WorkAssignmentReport report,
            IReadOnlyCollection<WorkReportLifecycleProjectionOutboxEntry> pending)
    {
        var approval = ResolveCurrentApprovedDirectProjectionEntry(report, pending);
        if (approval is not null)
            return approval;

        return pending
            .Where(entry =>
                (entry.Operation is
                    "REVIEW_RECALL_APPROVED" or
                    "REVIEW_RETURN" or
                    "REVIEW_DEACTIVATE_REPORT" or
                    "REVIEW_REACTIVATE_REPORT" or
                    "WITHDRAW" or
                    "AUTO_AGGREGATE_REVIEW_INVALIDATED") &&
                entry.LifecycleRevision == report.LifecycleRevision &&
                entry.PayloadRevision == report.PayloadRevision &&
                string.Equals(entry.PayloadHash, report.PayloadHash, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.CreatedAtUtc)
            .ThenByDescending(entry => entry.EntryKey, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private async Task MarkP9DependentResultsDirtyAsync(
        WorkAssignmentReport report,
        WorkReportLifecycleDirtyRange dirtyRange,
        string actorUserId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var basicFilter = Builders<WorkAssignmentBasicSummarySnapshot>.Filter;
        await _ctx.WorkAssignmentBasicSummarySnapshots.UpdateManyAsync(
            basicFilter.Eq(x => x.IsDeleted, false) &
            (basicFilter.AnyEq(x => x.SourceReportIds, report.Id) |
             (basicFilter.Eq(x => x.WorkId, report.WorkId) &
              basicFilter.Eq(x => x.DynamicFormTemplateId, report.DynamicFormTemplateId) &
              basicFilter.AnyEq(x => x.SourceAssignmentIds, report.WorkAssignmentId))),
            Builders<WorkAssignmentBasicSummarySnapshot>.Update
                .Set(x => x.SnapshotDirty, true)
                .Set(x => x.SnapshotDirtyAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        if (!string.IsNullOrWhiteSpace(report.DynamicFormTemplateId))
        {
            var advancedReason =
                $"REPORT_STATUS_MUTATION:{dirtyRange.Operation}:{dirtyRange.FromStatus}->{dirtyRange.ToStatus}";
            await MarkP9AdvancedNodesDirtyAsync(
                _ctx.WorkAssignmentAdvancedSummaryDayNodes,
                report,
                advancedReason,
                actorUserId,
                now,
                ct);
            await MarkP9AdvancedNodesDirtyAsync(
                _ctx.WorkAssignmentAdvancedSummaryMonthNodes,
                report,
                advancedReason,
                actorUserId,
                now,
                ct);
            await MarkP9AdvancedNodesDirtyAsync(
                _ctx.WorkAssignmentAdvancedSummaryYearNodes,
                report,
                advancedReason,
                actorUserId,
                now,
                ct);
        }

        var diffResults = _ctx.Db.GetCollection<WorkReportStatisticDiffResult>(
            "work_report_statistic_diff_results");
        var diffFilter = Builders<WorkReportStatisticDiffResult>.Filter;
        await diffResults.UpdateManyAsync(
            diffFilter.Eq(x => x.WorkId, report.WorkId) &
            diffFilter.Eq(x => x.IsCurrent, true) &
            diffFilter.Eq(x => x.IsDeleted, false) &
            diffFilter.ElemMatch(
                x => x.SourcePins,
                pin => pin.SourceReportId == report.Id),
            Builders<WorkReportStatisticDiffResult>.Update
                .Set(x => x.IsFresh, false)
                .Set(x => x.IsDirty, true)
                .Set(x => x.FailureCode, "SOURCE_LIFECYCLE_CHANGED")
                .Set(x => x.FailureMessage, null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
    }

    private static async Task MarkP9AdvancedNodesDirtyAsync<T>(
        IMongoCollection<T> collection,
        WorkAssignmentReport report,
        string reason,
        string actorUserId,
        DateTime now,
        CancellationToken ct)
        where T : WorkAssignmentAdvancedSummaryHierarchyNodeBase
    {
        var fb = Builders<T>.Filter;
        var filter =
            fb.Eq(x => x.WorkId, report.WorkId) &
            fb.Eq(x => x.DynamicFormTemplateId, report.DynamicFormTemplateId) &
            fb.Eq(x => x.IsDeleted, false);
        var update = Builders<T>.Update
            .Set(x => x.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Dirty)
            .Set(x => x.IsDirty, true)
            .Set(x => x.DirtyReason, reason.Length <= 500 ? reason : reason[..500])
            .Set(x => x.BuildError, (string?)null)
            .Set(x => x.BuildJobId, (string?)null)
            .Set(x => x.BuildCorrelationId, (string?)null)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);
        await collection.UpdateManyAsync(filter, update, cancellationToken: ct);
    }

    private async Task<LifecycleProjectionClaim?> TryClaimAsync(
        string? reportId,
        IReadOnlyCollection<string> excludedReportIds,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var token = Guid.NewGuid().ToString("N");
        var fb = Builders<WorkAssignmentReport>.Filter;
        var pendingFilter = fb.ElemMatch(
            x => x.LifecycleProjectionOutbox,
            x => x.State == WorkReportLifecycleProjectionOutboxStates.Pending);
        var expiredLeaseFilter = fb.Or(
            fb.Exists(x => x.LifecycleProjectionClaimToken, false),
            fb.Eq(x => x.LifecycleProjectionClaimToken, null),
            fb.Exists(x => x.LifecycleProjectionClaimExpiresAtUtc, false),
            fb.Lte(x => x.LifecycleProjectionClaimExpiresAtUtc, now));
        var filter = fb.Eq(x => x.IsDeleted, false) & pendingFilter & expiredLeaseFilter;
        if (!string.IsNullOrWhiteSpace(reportId))
            filter &= fb.Eq(x => x.Id, reportId);
        if (excludedReportIds.Count > 0)
            filter &= fb.Nin(x => x.Id, excludedReportIds);

        var update = Builders<WorkAssignmentReport>.Update
            .Set(x => x.LifecycleProjectionClaimToken, token)
            .Set(x => x.LifecycleProjectionClaimedAtUtc, now)
            .Set(x => x.LifecycleProjectionClaimExpiresAtUtc, now.Add(ClaimLease));
        var report = await _ctx.WorkAssignmentReports.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<WorkAssignmentReport>
            {
                ReturnDocument = ReturnDocument.After,
                Sort = Builders<WorkAssignmentReport>.Sort.Ascending("lifecycleProjectionOutbox.createdAtUtc")
            },
            ct);

        return report is null ? null : new LifecycleProjectionClaim(report, token);
    }

    private async Task<PeriodProjectionState> ReconcilePeriodAsync(
        WorkAssignmentReport claimedReport,
        string actorUserId,
        CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var period = await _ctx.WorkReportPeriods
                .Find(x => x.Id == claimedReport.WorkReportPeriodId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (period is null)
                return new PeriodProjectionState(null, null);

            var authoritativeReports = await LoadAuthoritativeReportsAsync(
                period.Id,
                ct);
            var authoritativeReport = authoritativeReports.FirstOrDefault();
            var duplicateAuthoritativeReportIds = authoritativeReports
                .Skip(1)
                .Select(report => report.Id)
                .ToArray();
            var canonicalTrigger = authoritativeReport ??
                                   await LoadLatestPeriodReportAsync(period.Id, ct) ??
                                   claimedReport;
            var canonicalReportVersionCount =
                await _ctx.WorkAssignmentReports
                    .Find(x =>
                        x.WorkReportPeriodId == period.Id &&
                        !x.IsDeleted)
                    .SortByDescending(x => x.VersionNo)
                    .Project(x => x.VersionNo)
                    .FirstOrDefaultAsync(ct);
            var now = DateTime.UtcNow;
            var projection = WorkReportLifecycleProjectionPolicy.ResolvePeriod(
                period,
                authoritativeReport,
                canonicalTrigger,
                now);
            string? sourceReportId =
                authoritativeReport?.Id ?? canonicalTrigger.Id;
            var sourceRevision =
                authoritativeReport?.LifecycleRevision ??
                canonicalTrigger.LifecycleRevision;
            DateTime? sourceAppliedAtUtc = now;
            if (sourceRevision <= 0)
            {
                sourceReportId = string.IsNullOrWhiteSpace(
                    period.SourceLifecycleReportId)
                    ? null
                    : period.SourceLifecycleReportId;
                sourceRevision = sourceReportId is null
                    ? 0
                    : period.SourceLifecycleRevision;
                sourceAppliedAtUtc = sourceReportId is null
                    ? null
                    : period.SourceLifecycleAppliedAtUtc ?? now;
            }

            // UpdatedAtUtc + CurrentReportId form the period-local CAS. Source report revisions are
            // report-local namespaces, so a different source id must never bypass this observed-state
            // boundary and overwrite a newer projection unnoticed.
            var pf = Builders<WorkReportPeriod>.Filter;
            var filter = pf.Eq(x => x.Id, period.Id) &
                         pf.Eq(x => x.IsDeleted, false) &
                         pf.Eq(x => x.CurrentReportId, period.CurrentReportId) &
                         pf.Eq(x => x.UpdatedAtUtc, period.UpdatedAtUtc);
            var update = Builders<WorkReportPeriod>.Update
                .Set(x => x.CurrentReportId, projection.CurrentReportId)
                .Set(x => x.Status, projection.Status)
                .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(projection.Status))
                .Set(x => x.StartedDate, projection.StartedDate)
                .Set(x => x.CompletedDate, projection.CompletedDate)
                .Set(x => x.IsHistoricalData, projection.IsHistoricalData)
                .Set(x => x.DueAtUtc, projection.DueAtUtc)
                .Set(x => x.HistoricalDataApproved, projection.HistoricalDataApproved)
                .Set(x => x.HistoricalDataApprovedAtUtc, projection.HistoricalDataApprovedAtUtc)
                .Set(x => x.HistoricalDataApprovedByUserId, projection.HistoricalDataApprovedByUserId)
                .Set(x => x.LastDraftSavedAtUtc, projection.LastDraftSavedAtUtc)
                .Set(x => x.LastSubmittedAtUtc, projection.LastSubmittedAtUtc)
                .Set(x => x.LastReviewedAtUtc, projection.LastReviewedAtUtc)
                .Set(x => x.RequiresLateReason, projection.RequiresLateReason)
                .Set(x => x.AcceptedLateReason, projection.AcceptedLateReason)
                .Set(x => x.LateReason, projection.LateReason)
                .Set(x => x.ReviewerComment, projection.ReviewerComment)
                .Set(x => x.ReviewerEvaluation, projection.ReviewerEvaluation)
                .Set(x => x.ReturnReason, projection.ReturnReason)
                .Set(x => x.SourceLifecycleReportId, sourceReportId)
                .Set(x => x.SourceLifecycleRevision, sourceRevision)
                .Set(x => x.SourceLifecycleAppliedAtUtc, sourceAppliedAtUtc)
                .Set(
                    x => x.ReportVersionCount,
                    Math.Max(0, canonicalReportVersionCount))
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId);
            if (authoritativeReport is not null)
            {
                update = update
                    .Set(x => x.IsActive, true);
            }

            var periodUpdated = await _transactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    if (duplicateAuthoritativeReportIds.Length > 0)
                    {
                        await _ctx.WorkAssignmentReports.UpdateManyAsync(
                            session,
                            x =>
                                duplicateAuthoritativeReportIds.Contains(x.Id) &&
                                x.WorkReportPeriodId == period.Id &&
                                x.IsCurrent &&
                                x.IsActive &&
                                !x.IsDeleted,
                            Builders<WorkAssignmentReport>.Update
                                .Set(x => x.IsCurrent, false)
                                .Set(x => x.IsActive, false)
                                .Set(x => x.DeactivatedAtUtc, now)
                                .Set(
                                    x => x.DeactivationReason,
                                    "DYNAMIC_FLOW_DUPLICATE_ACTIVE_REPORT_REPAIRED")
                                .Set(x => x.UpdatedAtUtc, now)
                                .Set(x => x.UpdatedByUserId, actorUserId),
                            cancellationToken: transactionCt);
                    }
                    var periodUpdate =
                        await _ctx.WorkReportPeriods.UpdateOneAsync(
                            session,
                            filter,
                            update,
                            cancellationToken: transactionCt);
                    if (periodUpdate.MatchedCount != 1)
                        return false;
                    var sourceRevision =
                        await _ctx.WorkAssignments.UpdateOneAsync(
                            session,
                            x =>
                                x.Id == period.WorkAssignmentId &&
                                !x.IsDeleted,
                            Builders<WorkAssignment>.Update.Inc(
                                x => x.ReportLifecycleSeriesRevision,
                                1),
                            cancellationToken: transactionCt);
                    if (sourceRevision.MatchedCount != 1)
                    {
                        throw new InvalidOperationException(
                            "WORK_REPORT_LIFECYCLE_PROGRESS_SOURCE_MISSING");
                    }
                    await WorkDirectSourceRevisionFence.IncrementAsync(
                        _ctx,
                        session,
                        period.WorkId,
                        transactionCt);
                    return true;
                },
                ct);
            if (!periodUpdated)
                continue;

            var observedAuthoritativeReports =
                await LoadAuthoritativeReportsAsync(period.Id, ct);
            var observedAuthoritativeReport =
                observedAuthoritativeReports.FirstOrDefault();
            if (observedAuthoritativeReports.Count > 1 ||
                !SameLifecycleSource(
                    authoritativeReport,
                    observedAuthoritativeReport))
                continue;
            if (authoritativeReport is null)
            {
                var observedTrigger = await LoadLatestPeriodReportAsync(period.Id, ct);
                if (!SameLifecycleSource(canonicalTrigger, observedTrigger))
                    continue;
            }

            var projectedPeriod = await _ctx.WorkReportPeriods
                .Find(x => x.Id == period.Id && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (projectedPeriod is not null &&
                WorkReportLifecycleProjectionPolicy.MatchesPeriod(
                    projectedPeriod,
                    projection,
                    sourceReportId,
                    sourceRevision))
            {
                return new PeriodProjectionState(projectedPeriod, authoritativeReport);
            }
        }

        throw new InvalidOperationException(
            $"Lifecycle period projection could not converge after {maxAttempts} compare-and-swap attempts for report '{claimedReport.Id}'.");
    }

    private async Task<WorkAssignmentReport?> LoadAuthoritativeReportAsync(string periodId, CancellationToken ct)
        => (await LoadAuthoritativeReportsAsync(periodId, ct))
            .FirstOrDefault();

    private Task<List<WorkAssignmentReport>> LoadAuthoritativeReportsAsync(
        string periodId,
        CancellationToken ct)
    {
        var rf = Builders<WorkAssignmentReport>.Filter;
        return _ctx.WorkAssignmentReports
            .Find(rf.Eq(x => x.WorkReportPeriodId, periodId) &
                  rf.Eq(x => x.IsCurrent, true) &
                  rf.Eq(x => x.IsActive, true) &
                  rf.Eq(x => x.IsDeleted, false))
            .SortByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.VersionNo)
            .ThenByDescending(x => x.Id)
            .ToListAsync(ct);
    }

    private async Task<WorkAssignmentReport?> LoadLatestPeriodReportAsync(
        string periodId,
        CancellationToken ct)
    {
        var rf = Builders<WorkAssignmentReport>.Filter;
        return await _ctx.WorkAssignmentReports
            .Find(rf.Eq(x => x.WorkReportPeriodId, periodId) &
                  rf.Eq(x => x.IsDeleted, false))
            .SortByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.VersionNo)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static bool SameLifecycleSource(
        WorkAssignmentReport? expected,
        WorkAssignmentReport? observed)
        => string.Equals(expected?.Id, observed?.Id, StringComparison.Ordinal) &&
           (expected?.LifecycleRevision ?? 0) == (observed?.LifecycleRevision ?? 0);

    private async Task RenewClaimAsync(LifecycleProjectionClaim claim, CancellationToken ct)
    {
        var renewal = await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == claim.Report.Id && x.LifecycleProjectionClaimToken == claim.Token,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.LifecycleProjectionClaimExpiresAtUtc, DateTime.UtcNow.Add(ClaimLease)),
            cancellationToken: ct);
        if (renewal.MatchedCount != 1)
        {
            throw new InvalidOperationException(
                $"Lifecycle projection claim '{claim.Token}' was lost before renewal.");
        }
    }

    private async Task CompletePendingEntriesAsync(
        LifecycleProjectionClaim claim,
        int projectedRevision,
        IReadOnlyCollection<string> completedEntryKeys,
        CancellationToken ct)
    {
        if (completedEntryKeys.Count == 0)
            throw new InvalidOperationException("Lifecycle projection cannot complete an empty entry set.");

        var now = DateTime.UtcNow;
        var update = Builders<WorkAssignmentReport>.Update
            .Set("lifecycleProjectionOutbox.$[entry].state", WorkReportLifecycleProjectionOutboxStates.Completed)
            .Set("lifecycleProjectionOutbox.$[entry].completedAtUtc", now)
            .Set("lifecycleProjectionOutbox.$[entry].lastAttemptedAtUtc", now)
            .Inc("lifecycleProjectionOutbox.$[entry].attemptCount", 1)
            .Set("lifecycleProjectionOutbox.$[entry].lastError", (string?)null)
            .Max(x => x.LifecycleProjectionLastCompletedRevision, projectedRevision)
            .Set(x => x.LifecycleProjectionLastCompletedAtUtc, now)
            .Set(x => x.LifecycleProjectionLastError, null)
            .Set(x => x.LifecycleProjectionClaimToken, null)
            .Set(x => x.LifecycleProjectionClaimedAtUtc, null)
            .Set(x => x.LifecycleProjectionClaimExpiresAtUtc, null);
        var options = new UpdateOptions
        {
            ArrayFilters = new[]
            {
                new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument
                {
                    { "entry.state", WorkReportLifecycleProjectionOutboxStates.Pending },
                    { "entry.entryKey", new BsonDocument("$in", new BsonArray(completedEntryKeys)) }
                })
            }
        };
        var result = await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == claim.Report.Id && x.LifecycleProjectionClaimToken == claim.Token,
            update,
            options,
            ct);
        if (result.ModifiedCount != 1)
            throw new InvalidOperationException($"Lifecycle projection claim '{claim.Token}' was lost before completion.");
    }

    private async Task RecordFailureAndReleaseClaimAsync(
        LifecycleProjectionClaim claim,
        IReadOnlyCollection<string> attemptedEntryKeys,
        Exception exception,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var error = exception.ToString();
        if (error.Length > MaxStoredErrorLength)
            error = error[..MaxStoredErrorLength];

        var reportUpdate = Builders<WorkAssignmentReport>.Update
            .Set(x => x.LifecycleProjectionLastError, error)
            .Set(x => x.LifecycleProjectionClaimToken, null)
            .Set(x => x.LifecycleProjectionClaimedAtUtc, null)
            .Set(x => x.LifecycleProjectionClaimExpiresAtUtc, null);

        try
        {
            var claimFilter = Builders<WorkAssignmentReport>.Filter.Where(
                x => x.Id == claim.Report.Id && x.LifecycleProjectionClaimToken == claim.Token);
            if (attemptedEntryKeys.Count == 0)
            {
                await _ctx.WorkAssignmentReports.UpdateOneAsync(
                    claimFilter,
                    reportUpdate,
                    cancellationToken: ct);
            }
            else
            {
                var update = Builders<WorkAssignmentReport>.Update.Combine(
                    reportUpdate,
                    Builders<WorkAssignmentReport>.Update
                        .Set("lifecycleProjectionOutbox.$[entry].lastAttemptedAtUtc", now)
                        .Inc("lifecycleProjectionOutbox.$[entry].attemptCount", 1)
                        .Set("lifecycleProjectionOutbox.$[entry].lastError", error));
                var options = new UpdateOptions
                {
                    ArrayFilters = new[]
                    {
                        new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument
                        {
                            { "entry.state", WorkReportLifecycleProjectionOutboxStates.Pending },
                            { "entry.entryKey", new BsonDocument("$in", new BsonArray(attemptedEntryKeys)) }
                        })
                    }
                };
                await _ctx.WorkAssignmentReports.UpdateOneAsync(
                    claimFilter,
                    update,
                    options,
                    ct);
            }
        }
        catch (Exception releaseError)
        {
            _logger.LogError(
                releaseError,
                "Could not release lifecycle projection claim; lease expiry will recover it. reportId={ReportId} claimToken={ClaimToken}",
                claim.Report.Id,
                claim.Token);
        }
    }

    private async Task ReleaseClaimAsync(LifecycleProjectionClaim claim, CancellationToken ct)
    {
        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == claim.Report.Id && x.LifecycleProjectionClaimToken == claim.Token,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.LifecycleProjectionClaimToken, null)
                .Set(x => x.LifecycleProjectionClaimedAtUtc, null)
                .Set(x => x.LifecycleProjectionClaimExpiresAtUtc, null),
            cancellationToken: ct);
    }

    private static string ResolveActorUserId(
        WorkAssignmentReport report,
        WorkReportLifecycleProjectionOutboxEntry latest)
    {
        foreach (var candidate in new[]
                 {
                     latest.ActorUserId,
                     report.UpdatedByUserId,
                     report.CreatedByUserId,
                     report.AssigneeUserId
                 })
        {
            if (ObjectId.TryParse(candidate, out _))
                return candidate!;
        }

        throw new InvalidOperationException($"Report '{report.Id}' has no valid projection actor ObjectId.");
    }

    private sealed record LifecycleProjectionClaim(WorkAssignmentReport Report, string Token);
    private sealed record PeriodProjectionState(WorkReportPeriod? Period, WorkAssignmentReport? AuthoritativeReport);
}

public sealed record WorkReportPeriodLifecycleProjection(
    string? CurrentReportId,
    WorkReportPeriodStatus Status,
    DateTime? StartedDate,
    DateTime? CompletedDate,
    bool IsHistoricalData,
    DateTime? DueAtUtc,
    bool HistoricalDataApproved,
    DateTime? HistoricalDataApprovedAtUtc,
    string? HistoricalDataApprovedByUserId,
    DateTime? LastDraftSavedAtUtc,
    DateTime? LastSubmittedAtUtc,
    DateTime? LastReviewedAtUtc,
    bool RequiresLateReason,
    string? AcceptedLateReason,
    string? LateReason,
    string? ReviewerComment,
    string? ReviewerEvaluation,
    string? ReturnReason);

public sealed record WorkReportLifecycleDirtyRange(string Operation, string FromStatus, string ToStatus);

public static class WorkReportLifecycleProjectionPolicy
{
    public static WorkReportPeriodLifecycleProjection ResolvePeriod(
        WorkReportPeriod period,
        WorkAssignmentReport? authoritativeReport,
        WorkAssignmentReport triggeringReport,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(triggeringReport);
        nowUtc = EnsureUtc(nowUtc);

        if (authoritativeReport is null)
        {
            var deactivationTransition =
                ResolveCurrentTransition(triggeringReport);
            var canonicalDueAtUtc =
                triggeringReport.DueAtUtc ?? period.DueAtUtc;
            return new WorkReportPeriodLifecycleProjection(
                CurrentReportId: null,
                Status: WorkReportPeriodStatusHelper.ResolveInitialStatus(
                    canonicalDueAtUtc,
                    nowUtc),
                StartedDate: triggeringReport.StartedDate,
                CompletedDate: triggeringReport.CompletedDate,
                IsHistoricalData: triggeringReport.IsHistoricalData,
                DueAtUtc: canonicalDueAtUtc,
                HistoricalDataApproved: triggeringReport.HistoricalDataApproved,
                HistoricalDataApprovedAtUtc:
                    triggeringReport.HistoricalDataApprovedAtUtc,
                HistoricalDataApprovedByUserId:
                    triggeringReport.HistoricalDataApprovedByUserId,
                LastDraftSavedAtUtc: null,
                LastSubmittedAtUtc: null,
                LastReviewedAtUtc: null,
                RequiresLateReason: false,
                AcceptedLateReason: ResolveAcceptedLateReason(
                    period,
                    triggeringReport,
                    deactivationTransition),
                LateReason: null,
                ReviewerComment: null,
                ReviewerEvaluation: null,
                ReturnReason: null);
        }

        var activeDueAtUtc =
            authoritativeReport.DueAtUtc ?? period.DueAtUtc;
        var status = authoritativeReport.Status switch
        {
            WorkAssignmentReportStatus.Draft => WorkAssignmentReportHistoricalDataHelper.ResolveDraftPeriodStatus(
                authoritativeReport.IsHistoricalData || period.IsHistoricalData,
                authoritativeReport.CompletedDate ?? period.CompletedDate,
                activeDueAtUtc,
                nowUtc),
            WorkAssignmentReportStatus.Submitted => WorkAssignmentReportHistoricalDataHelper.ResolveSubmittedPeriodStatus(
                period,
                authoritativeReport,
                nowUtc),
            WorkAssignmentReportStatus.Approved => WorkAssignmentReportHistoricalDataHelper.ResolveApprovedPeriodStatus(
                period,
                authoritativeReport,
                nowUtc),
            _ => throw new ArgumentOutOfRangeException(
                nameof(authoritativeReport),
                authoritativeReport.Status,
                "Unsupported report lifecycle status.")
        };

        var transition = ResolveCurrentTransition(authoritativeReport);
        var lastReviewedAt = ResolveLastReviewedAt(period, authoritativeReport, transition);
        var acceptedLateReason = ResolveAcceptedLateReason(period, authoritativeReport, transition);

        return new WorkReportPeriodLifecycleProjection(
            CurrentReportId: authoritativeReport.Id,
            Status: status,
            StartedDate: authoritativeReport.StartedDate,
            CompletedDate: authoritativeReport.CompletedDate,
            IsHistoricalData: authoritativeReport.IsHistoricalData,
            DueAtUtc: activeDueAtUtc,
            HistoricalDataApproved: authoritativeReport.HistoricalDataApproved,
            HistoricalDataApprovedAtUtc: authoritativeReport.HistoricalDataApprovedAtUtc,
            HistoricalDataApprovedByUserId: authoritativeReport.HistoricalDataApprovedByUserId,
            LastDraftSavedAtUtc: authoritativeReport.Status == WorkAssignmentReportStatus.Draft
                ? authoritativeReport.UpdatedAtUtc
                : period.LastDraftSavedAtUtc,
            LastSubmittedAtUtc: authoritativeReport.SubmittedAtUtc,
            LastReviewedAtUtc: lastReviewedAt,
            RequiresLateReason: authoritativeReport.IsLateSubmission,
            AcceptedLateReason: acceptedLateReason,
            LateReason: authoritativeReport.LateReason,
            ReviewerComment: authoritativeReport.ReviewerComment,
            ReviewerEvaluation: authoritativeReport.ReviewerEvaluation,
            ReturnReason: authoritativeReport.ReturnReason);
    }

    public static bool MatchesPeriod(
        WorkReportPeriod period,
        WorkReportPeriodLifecycleProjection projection,
        string? sourceReportId,
        int sourceRevision)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(projection);

        var hasSource = !string.IsNullOrWhiteSpace(sourceReportId);
        return MatchesPeriodValues(period, projection) &&
               string.Equals(period.SourceLifecycleReportId, sourceReportId, StringComparison.Ordinal) &&
               period.SourceLifecycleRevision == sourceRevision &&
               (hasSource
                   ? period.SourceLifecycleAppliedAtUtc.HasValue
                   : sourceRevision == 0 &&
                     period.SourceLifecycleAppliedAtUtc is null);
    }

    public static bool MatchesPeriodValues(
        WorkReportPeriod period,
        WorkReportPeriodLifecycleProjection projection)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(projection);

        return string.Equals(period.CurrentReportId, projection.CurrentReportId, StringComparison.Ordinal) &&
               period.Status == projection.Status &&
               period.IsOverdue == WorkReportPeriodStatusHelper.IsOverdue(projection.Status) &&
               period.StartedDate == projection.StartedDate &&
               period.CompletedDate == projection.CompletedDate &&
               period.IsHistoricalData == projection.IsHistoricalData &&
               period.DueAtUtc == projection.DueAtUtc &&
               period.HistoricalDataApproved == projection.HistoricalDataApproved &&
               period.HistoricalDataApprovedAtUtc == projection.HistoricalDataApprovedAtUtc &&
               string.Equals(
                   period.HistoricalDataApprovedByUserId,
                   projection.HistoricalDataApprovedByUserId,
                   StringComparison.Ordinal) &&
               period.LastDraftSavedAtUtc == projection.LastDraftSavedAtUtc &&
               period.LastSubmittedAtUtc == projection.LastSubmittedAtUtc &&
               period.LastReviewedAtUtc == projection.LastReviewedAtUtc &&
               period.RequiresLateReason == projection.RequiresLateReason &&
               string.Equals(period.AcceptedLateReason, projection.AcceptedLateReason, StringComparison.Ordinal) &&
               string.Equals(period.LateReason, projection.LateReason, StringComparison.Ordinal) &&
               string.Equals(period.ReviewerComment, projection.ReviewerComment, StringComparison.Ordinal) &&
               string.Equals(period.ReviewerEvaluation, projection.ReviewerEvaluation, StringComparison.Ordinal) &&
               string.Equals(period.ReturnReason, projection.ReturnReason, StringComparison.Ordinal) &&
               (projection.CurrentReportId is null || period.IsActive);
    }

    private static WorkReportLifecycleProjectionOutboxEntry? ResolveCurrentTransition(
        WorkAssignmentReport report)
        => (report.LifecycleProjectionOutbox ?? new List<WorkReportLifecycleProjectionOutboxEntry>())
            .Where(x => x.LifecycleRevision == report.LifecycleRevision)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.EntryKey, StringComparer.Ordinal)
            .FirstOrDefault();

    private static DateTime? ResolveLastReviewedAt(
        WorkReportPeriod period,
        WorkAssignmentReport report,
        WorkReportLifecycleProjectionOutboxEntry? transition)
    {
        if (report.Status == WorkAssignmentReportStatus.Approved)
            return report.ApprovedAtUtc ?? report.UpdatedAtUtc;

        var operation = transition?.Operation?.Trim().ToUpperInvariant()
                        ?? report.LastLifecycleCommandOperation?.Trim().ToUpperInvariant();
        if (operation is "REVIEW_RETURN" or "LEGACY_RETURN")
            return report.ReturnedAtUtc ?? report.UpdatedAtUtc;
        if (operation == "REVIEW_RECALL_APPROVED")
            return report.UpdatedAtUtc;
        if (operation == "AUTO_AGGREGATE_REVIEW_INVALIDATED")
            return null;
        if (operation == "REVIEW_REACTIVATE_REPORT")
            return report.ApprovedAtUtc;
        if (operation == "WITHDRAW" &&
            string.Equals(
                transition?.FromStatus,
                WorkAssignmentReportStatus.Approved.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A normal submit/withdraw preserves the most recent review marker. The report clears
        // ReturnedAtUtc on submit, so the period is the canonical carrier for this value.
        return period.LastReviewedAtUtc;
    }

    private static string? ResolveAcceptedLateReason(
        WorkReportPeriod period,
        WorkAssignmentReport report,
        WorkReportLifecycleProjectionOutboxEntry? transition)
    {
        if (report.Status == WorkAssignmentReportStatus.Approved)
            return report.LateReason;

        if (string.Equals(transition?.Operation, "WITHDRAW", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                transition?.FromStatus,
                WorkAssignmentReportStatus.Approved.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return period.AcceptedLateReason;
    }

    public static WorkReportLifecycleDirtyRange ResolveDirtyRange(
        IReadOnlyCollection<WorkReportLifecycleProjectionOutboxEntry> pending,
        WorkAssignmentReport currentReport)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(currentReport);
        if (pending.Count == 0)
            throw new ArgumentException("At least one pending lifecycle projection is required.", nameof(pending));

        var ordered = pending
            .OrderBy(x => x.LifecycleRevision)
            .ThenBy(x => x.EntryKey, StringComparer.Ordinal)
            .ToList();
        var touchesApproved = ordered.Any(x =>
            string.Equals(x.FromStatus, WorkAssignmentReportStatus.Approved.ToString(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.ToStatus, WorkAssignmentReportStatus.Approved.ToString(), StringComparison.OrdinalIgnoreCase));
        var togglesActive = ordered.Any(x => x.FromIsActive != x.ToIsActive);
        var fromStatus = touchesApproved
            ? WorkAssignmentReportStatus.Approved.ToString()
            : togglesActive
                ? (ordered[0].FromIsActive ? "ACTIVE" : "INACTIVE")
                : ordered[0].FromStatus;
        var toStatus = touchesApproved
            ? currentReport.Status.ToString()
            : togglesActive
                ? (currentReport.IsActive ? "ACTIVE" : "INACTIVE")
                : currentReport.Status.ToString();

        return new WorkReportLifecycleDirtyRange(
            $"LIFECYCLE_OUTBOX_RECONCILE:{ordered[0].LifecycleRevision}-{ordered[^1].LifecycleRevision}",
            fromStatus,
            toStatus);
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}
