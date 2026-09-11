using System.Collections.Immutable;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed class StatisticReconciliationActualSourceMongoOwnerReader(
    IStatRunDirectProjectionReadOwner owner)
    : IStatisticReconciliationActualSourceOwnerReader
{
    public async Task<IReadOnlyList<ActualSourceOwnerRevision>> ReadAsync(
        ActualSourceMembershipScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var ownerScope = new StatRunDirectSourceOwnerScope(
            scope.OwnerRunId,
            scope.OwnerGenerationId,
            scope.OwnerGenerationSha256,
            scope.WorkId,
            scope.PeriodInstanceKey,
            scope.DynamicFormTemplateId,
            scope.OwnerMembershipSignature,
            scope.OwnerDirectSourceRevision,
            scope.LifecycleMetricScope);
        var snapshot = await owner.ReadSourceOwnerAsync(
            ownerScope,
            cancellationToken).ConfigureAwait(false);
        if (snapshot.Scope != ownerScope)
            throw Fail("SOURCE_OWNER_SCOPE_REPLAY_MISMATCH");

        return snapshot.Members
            .Select(member => Map(scope, member))
            .OrderBy(member => member.OwnerOrdinal)
            .ThenBy(member => member.ReportId, StringComparer.Ordinal)
            .ToArray();
    }

    private static ActualSourceOwnerRevision Map(
        ActualSourceMembershipScope scope,
        StatRunDirectSourceMemberOwnerSnapshot member)
        => new(
            member.WorkId,
            member.WorkAssignmentId,
            member.PeriodInstanceKey,
            member.DynamicFormTemplateId,
            member.ReportId,
            member.PayloadDocumentId,
            member.PayloadRevision,
            member.PayloadSha256,
            member.LifecycleRevision,
            member.LifecycleSha256,
            member.LifecycleStatus,
            member.IsCurrent,
            member.IsActive,
            member.IsDeleted,
            member.InvalidatedByFlowEventId,
            member.AssignmentIsActive,
            member.PeriodIsActive,
            member.PeriodStatus,
            member.PeriodCurrentReportId,
            member.PeriodSourceLifecycleReportId,
            member.PeriodSourceLifecycleRevision,
            member.PeriodSourceLifecycleApplied,
            member.ContributionDecision,
            member.OwnerIncluded,
            member.OwnerDecisionCode,
            member.OwnerOrdinal,
            scope.OwnerRunId,
            scope.OwnerGenerationId,
            scope.OwnerGenerationSha256,
            scope.OwnerDirectSourceRevision,
            member.MappingRevision,
            member.MappingSemanticSha256,
            member.FlowEffectiveStatus,
            member.FlowRuntime is null ? null : Map(member.FlowRuntime),
            member.InLifecycleMetricScope);

    private static ActualFlowRuntimeOwnerRevision Map(
        StatRunDirectSourceFlowOwnerSnapshot value)
        => new(
            value.FlowFamilyRevision,
            value.FlowTemplateId,
            value.FlowTemplateVersionId,
            value.FlowTemplateVersionNo,
            value.FlowOriginVersionId,
            value.FlowPayloadSha256,
            value.FlowCatalogVersion,
            value.FlowCatalogSha256,
            value.FlowInstanceId,
            value.FlowInstanceRevision,
            value.FlowInstanceState,
            value.CurrentExecutionEpoch,
            value.ExecutionEpochId,
            value.ExecutionEpoch,
            value.ExecutionEpochRevision,
            value.ExecutionEpochState,
            value.IsCanonicalEpoch,
            value.StepInstanceId,
            value.StepRevision,
            value.StepState,
            value.StepExecutionEpoch,
            value.StepIsCanonicalEpoch,
            value.StepReportId,
            value.StepReportLifecycleRevision,
            value.StepReportLifecycleStatus,
            value.StepReportIsActive,
            value.StepInvalidatedAtUtc,
            value.StepInvalidatedByEventId,
            value.StepSupersededByStepInstanceId,
            value.FlowStepId,
            value.FlowBranchId,
            value.FlowAttemptNo,
            value.ContributionPolicy,
            value.ContributionSha256,
            value.ContributionWarning);

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new(reason);
}

internal sealed class StatisticReconciliationActualDirectProjectionMongoOwnerReader(
    MongoDbContext context)
    : IStatisticReconciliationActualDirectProjectionOwnerReader
{
    private readonly StatisticReconciliationActualDirectGenerationMongoGuard _guard =
        new(context);

    public async Task<IReadOnlyList<WorkReportFieldStatValue>> ReadFieldRowsAsync(
        ActualDirectProjectionBoundary boundary,
        CancellationToken cancellationToken)
    {
        var job = await _guard.RequireAsync(boundary, cancellationToken)
            .ConfigureAwait(false);
        var rows = await context.WorkReportFieldStatValues
            .Find(row =>
                row.DirectProjection != null &&
                row.DirectProjection.RunId == boundary.RunId &&
                row.DirectProjection.GenerationId == boundary.GenerationId &&
                !row.IsDeleted)
            .Sort(Builders<WorkReportFieldStatValue>.Sort
                .Ascending(row => row.PeriodKey)
                .Ascending(row => row.FieldKey)
                .Ascending(row => row.BucketKey)
                .Ascending(row => row.WorkAssignmentReportId)
                .Ascending(row => row.SourceKey)
                .Ascending(row => row.Id))
            .Limit(StatisticReconciliationActualDirectProjectionAdapter.MaxProjectionRows + 1)
            .ToListAsync(cancellationToken);
        _guard.RequireStoreRowCount(
            job,
            StatisticReconciliationActualMongoOwnerStores.FieldValues,
            rows.Count);
        return rows;
    }

    public async Task<IReadOnlyList<WorkReportTableStatValue>>
        ReadTableMetricRowsAsync(
            ActualDirectProjectionBoundary boundary,
            CancellationToken cancellationToken)
    {
        var job = await _guard.RequireAsync(boundary, cancellationToken)
            .ConfigureAwait(false);
        var rows = await context.WorkReportTableStatValues
            .Find(row =>
                row.DirectProjection != null &&
                row.DirectProjection.RunId == boundary.RunId &&
                row.DirectProjection.GenerationId == boundary.GenerationId &&
                !row.IsDeleted)
            .Sort(Builders<WorkReportTableStatValue>.Sort
                .Ascending(row => row.PeriodKey)
                .Ascending(row => row.BlockId)
                .Ascending(row => row.MetricKey)
                .Ascending(row => row.RowKey)
                .Ascending(row => row.ColumnKey)
                .Ascending(row => row.WorkAssignmentReportId)
                .Ascending(row => row.SourceKey)
                .Ascending(row => row.Id))
            .Limit(StatisticReconciliationActualDirectProjectionAdapter.MaxProjectionRows + 1)
            .ToListAsync(cancellationToken);
        _guard.RequireStoreRowCount(
            job,
            StatisticReconciliationActualMongoOwnerStores.TableValues,
            rows.Count);
        return rows;
    }

    public async Task<IReadOnlyList<WorkReportLabelStatValue>>
        ReadRowLabelRowsAsync(
            ActualDirectProjectionBoundary boundary,
            CancellationToken cancellationToken)
    {
        var job = await _guard.RequireAsync(boundary, cancellationToken)
            .ConfigureAwait(false);
        var rows = await context.WorkReportLabelStatValues
            .Find(row =>
                row.DirectProjection != null &&
                row.DirectProjection.RunId == boundary.RunId &&
                row.DirectProjection.GenerationId == boundary.GenerationId &&
                !row.IsDeleted)
            .Sort(Builders<WorkReportLabelStatValue>.Sort
                .Ascending(row => row.PeriodKey)
                .Ascending(row => row.BlockId)
                .Ascending(row => row.LabelCode)
                .Ascending(row => row.RowKey)
                .Ascending(row => row.RowIndex)
                .Ascending(row => row.WorkAssignmentReportId)
                .Ascending(row => row.Id))
            .Limit(StatisticReconciliationActualDirectProjectionAdapter.MaxProjectionRows + 1)
            .ToListAsync(cancellationToken);
        _guard.RequireStoreRowCount(
            job,
            StatisticReconciliationActualMongoOwnerStores.LabelValues,
            rows.Count);
        return rows;
    }
}

internal sealed class StatisticReconciliationActualAggregateMongoOwnerReader(
    MongoDbContext context)
    : IStatisticReconciliationActualAggregateOwnerReader
{
    private readonly StatisticReconciliationActualDirectGenerationMongoGuard _guard =
        new(context);

    public async Task<IReadOnlyList<WorkReportFieldStatAggregate>>
        ReadFieldGenerationAsync(
            ActualAggregatePublicationBoundary boundary,
            CancellationToken cancellationToken)
    {
        await _guard.RequireAsync(boundary, cancellationToken)
            .ConfigureAwait(false);
        return await context.WorkReportFieldStatAggregates
            .Find(row =>
                row.DirectProjection != null &&
                row.DirectProjection.RunId == boundary.Direct.RunId &&
                row.DirectProjection.GenerationId == boundary.Direct.GenerationId &&
                !row.IsDeleted)
            .SortBy(row => row.Id)
            .Limit(StatisticReconciliationActualAggregateAdapter.MaxAggregateRows + 1)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkReportTableStatAggregate>>
        ReadTableMetricGenerationAsync(
            ActualAggregatePublicationBoundary boundary,
            CancellationToken cancellationToken)
    {
        await _guard.RequireAsync(boundary, cancellationToken)
            .ConfigureAwait(false);
        return await context.WorkReportTableStatAggregates
            .Find(row =>
                row.DirectProjection != null &&
                row.DirectProjection.RunId == boundary.Direct.RunId &&
                row.DirectProjection.GenerationId == boundary.Direct.GenerationId &&
                !row.IsDeleted)
            .SortBy(row => row.Id)
            .Limit(StatisticReconciliationActualAggregateAdapter.MaxAggregateRows + 1)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkReportLabelStatAggregate>>
        ReadRowLabelGenerationAsync(
            ActualAggregatePublicationBoundary boundary,
            CancellationToken cancellationToken)
    {
        await _guard.RequireAsync(boundary, cancellationToken)
            .ConfigureAwait(false);
        return await context.WorkReportLabelStatAggregates
            .Find(row =>
                row.DirectProjection != null &&
                row.DirectProjection.RunId == boundary.Direct.RunId &&
                row.DirectProjection.GenerationId == boundary.Direct.GenerationId &&
                !row.IsDeleted)
            .SortBy(row => row.Id)
            .Limit(StatisticReconciliationActualAggregateAdapter.MaxAggregateRows + 1)
            .ToListAsync(cancellationToken);
    }
}

internal sealed class StatisticReconciliationActualBasicMongoOwnerReader(
    MongoDbContext context)
    : IStatisticReconciliationActualBasicOwnerReader
{
    public async Task<WorkAssignmentBasicSummarySnapshot?> ReadSnapshotAsync(
        ActualBasicOwnerBoundary boundary,
        CancellationToken cancellationToken)
    {
        var rows = await context.WorkAssignmentBasicSummarySnapshots
            .Find(snapshot => snapshot.Id == boundary.OwnerSnapshotId)
            .SortBy(snapshot => snapshot.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        if (rows.Count > 1)
            throw Fail("BASIC_OWNER_SNAPSHOT_AMBIGUOUS");
        return rows.SingleOrDefault();
    }

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new(reason);
}

internal sealed class StatisticReconciliationActualAdvancedMongoOwnerReader(
    MongoDbContext context)
    : IStatisticReconciliationActualAdvancedOwnerReader
{
    public Task<IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode>>
        ReadDayNodesAsync(
            ActualAdvancedOwnerBoundary boundary,
            CancellationToken cancellationToken)
        => ReadDaysAsync(boundary.DayNodeIds, cancellationToken);

    public Task<IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode>>
        ReadMonthNodesAsync(
            ActualAdvancedOwnerBoundary boundary,
            CancellationToken cancellationToken)
        => ReadMonthsAsync(boundary.MonthNodeIds, cancellationToken);

    public Task<IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode>>
        ReadYearNodesAsync(
            ActualAdvancedOwnerBoundary boundary,
            CancellationToken cancellationToken)
        => ReadYearsAsync(boundary.YearNodeIds, cancellationToken);

    private async Task<IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode>>
        ReadDaysAsync(
            ImmutableArray<string> ids,
            CancellationToken cancellationToken)
    {
        if (ids.IsEmpty)
            return [];
        return await context.WorkAssignmentAdvancedSummaryDayNodes
            .Find(Builders<WorkAssignmentAdvancedSummaryDayNode>.Filter
                .In(node => node.Id, ids))
            .Sort(Builders<WorkAssignmentAdvancedSummaryDayNode>.Sort
                .Ascending(node => node.WindowStartUtc)
                .Ascending(node => node.GrainKey)
                .Ascending(node => node.Id))
            .Limit(ids.Length + 1)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode>>
        ReadMonthsAsync(
            ImmutableArray<string> ids,
            CancellationToken cancellationToken)
    {
        if (ids.IsEmpty)
            return [];
        return await context.WorkAssignmentAdvancedSummaryMonthNodes
            .Find(Builders<WorkAssignmentAdvancedSummaryMonthNode>.Filter
                .In(node => node.Id, ids))
            .Sort(Builders<WorkAssignmentAdvancedSummaryMonthNode>.Sort
                .Ascending(node => node.WindowStartUtc)
                .Ascending(node => node.GrainKey)
                .Ascending(node => node.Id))
            .Limit(ids.Length + 1)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode>>
        ReadYearsAsync(
            ImmutableArray<string> ids,
            CancellationToken cancellationToken)
    {
        if (ids.IsEmpty)
            return [];
        return await context.WorkAssignmentAdvancedSummaryYearNodes
            .Find(Builders<WorkAssignmentAdvancedSummaryYearNode>.Filter
                .In(node => node.Id, ids))
            .Sort(Builders<WorkAssignmentAdvancedSummaryYearNode>.Sort
                .Ascending(node => node.WindowStartUtc)
                .Ascending(node => node.GrainKey)
                .Ascending(node => node.Id))
            .Limit(ids.Length + 1)
            .ToListAsync(cancellationToken);
    }
}

internal sealed class StatisticReconciliationActualP9DiffMongoOwnerReader(
    MongoDbContext context)
    : IStatisticReconciliationActualP9DiffOwnerReader
{
    internal const string CollectionName = "work_report_statistic_diff_results";

    public async Task<WorkReportStatisticDiffResult?> ReadResultAsync(
        ActualP9DiffOwnerBoundary boundary,
        CancellationToken cancellationToken)
    {
        var collection = context.Db.GetCollection<WorkReportStatisticDiffResult>(
            CollectionName);
        var rows = await collection
            .Find(result => result.Id == boundary.ResultId)
            .SortBy(result => result.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        if (rows.Count > 1)
            throw Fail("P9_DIFF_OWNER_RESULT_AMBIGUOUS");
        return rows.SingleOrDefault();
    }

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new(reason);
}

internal sealed class StatisticReconciliationActualDirectGenerationMongoGuard(
    MongoDbContext context)
{
    internal async Task<WorkReportStatisticRebuildJob> RequireAsync(
        ActualDirectProjectionBoundary boundary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        var jobs = await context.WorkReportStatisticRebuildJobs
            .Find(job =>
                job.Id == boundary.RunId &&
                job.WorkId == boundary.WorkId &&
                job.PeriodInstanceKey == boundary.PeriodInstanceKey &&
                job.DynamicFormFamilyId == boundary.DynamicFormFamilyId &&
                job.DynamicFormTemplateId == boundary.DynamicFormTemplateId &&
                job.DynamicFormVersionNo == boundary.DynamicFormVersionNo &&
                job.DynamicFormSchemaHash == boundary.DynamicFormSchemaSha256 &&
                job.GenerationId == boundary.GenerationId &&
                job.GenerationHash == boundary.OwnerGenerationSha256 &&
                job.SourceLifecycleEventKey == boundary.OwnerLifecycleEventKey &&
                job.ComputedAtUtc == boundary.OwnerComputedAtUtc &&
                job.DirectSourceRevision == boundary.DirectSourceRevision &&
                job.ConfigId == boundary.ConfigId &&
                job.ConfigVersionId == boundary.ConfigVersionId &&
                job.ConfigVersionNo == boundary.ConfigVersionNo &&
                job.ConfigRevision == boundary.ConfigRevision &&
                job.ConfigHash == boundary.ConfigSha256 &&
                job.CandidateChainId == boundary.CandidateChainId &&
                job.CatalogVersion == boundary.CatalogVersion &&
                job.CatalogRawSha256 == boundary.CatalogRawSha256 &&
                job.CatalogSemanticSha256 == boundary.CatalogSemanticSha256 &&
                job.SchemaRawSha256 == boundary.SchemaRawSha256 &&
                job.SchemaSemanticSha256 == boundary.SchemaSemanticSha256 &&
                job.StageLockSha256 == boundary.StageLockSha256 &&
                job.SourceMembershipSignature == boundary.OwnerMembershipSignature &&
                job.Status == WorkReportStatisticRebuildJobStatuses.Completed &&
                job.IsCurrentPublication &&
                !job.IsActive &&
                !job.IsDeleted)
            .SortBy(job => job.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        if (jobs.Count != 1)
            throw Fail("DIRECT_OWNER_GENERATION_NOT_EXACT");
        RequireDigestInventory(jobs[0]);
        return jobs[0];
    }

    internal async Task<WorkReportStatisticRebuildJob> RequireAsync(
        ActualAggregatePublicationBoundary boundary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        var job = await RequireAsync(boundary.Direct, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(job.PublicationScopeKey,
                boundary.PublicationScopeKey, StringComparison.Ordinal) ||
            job.DirectPublicationRevision != boundary.DirectPublicationRevision ||
            !string.Equals(job.FreshnessState,
                boundary.FreshnessState, StringComparison.Ordinal) ||
            !job.PublishedAtUtc.HasValue ||
            !SameUtcMillisecond(job.PublishedAtUtc.Value, boundary.PublishedAtUtc))
        {
            throw Fail("AGGREGATE_OWNER_PUBLICATION_BOUNDARY_DRIFT");
        }

        var expected = boundary.AggregateStoreDigests
            .OrderBy(pin => pin.Store, StringComparer.Ordinal)
            .Select(pin => (pin.Store, pin.RowCount, pin.Sha256))
            .ToArray();
        var observed = job.DirectStoreDigests
            .Where(pin => StatisticReconciliationActualAggregateStores.ExactSet
                .Contains(pin.Store, StringComparer.Ordinal))
            .OrderBy(pin => pin.Store, StringComparer.Ordinal)
            .Select(pin => (pin.Store, pin.RowCount, pin.Sha256))
            .ToArray();
        if (!expected.SequenceEqual(observed))
            throw Fail("AGGREGATE_OWNER_STORE_DIGEST_DRIFT");
        return job;
    }

    internal void RequireStoreRowCount(
        WorkReportStatisticRebuildJob job,
        string store,
        int observedCount)
    {
        var pins = job.DirectStoreDigests
            .Where(pin => string.Equals(pin.Store, store,
                StringComparison.Ordinal))
            .ToArray();
        if (pins.Length != 1 || pins[0].RowCount != observedCount)
            throw Fail("DIRECT_OWNER_STORE_ROW_COUNT_DRIFT");
    }

    private static void RequireDigestInventory(
        WorkReportStatisticRebuildJob job)
    {
        var stores = job.DirectStoreDigests
            .Select(pin => pin.Store)
            .OrderBy(store => store, StringComparer.Ordinal)
            .ToArray();
        if (!stores.SequenceEqual(
                StatisticReconciliationActualMongoOwnerStores.All,
                StringComparer.Ordinal))
        {
            throw Fail("DIRECT_OWNER_DIGEST_INVENTORY_INVALID");
        }
    }

    private static bool SameUtcMillisecond(DateTime left, DateTime right)
        => new DateTimeOffset(left.ToUniversalTime()).ToUnixTimeMilliseconds()
           == new DateTimeOffset(right.ToUniversalTime()).ToUnixTimeMilliseconds();

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new(reason);
}

internal static class StatisticReconciliationActualMongoOwnerStores
{
    internal const string FieldValues = "work_report_field_stat_values";
    internal const string TableValues = "work_report_table_stat_values";
    internal const string LabelValues = "work_report_label_stat_values";

    internal static readonly ImmutableArray<string> All =
    [
        StatisticReconciliationActualAggregateStores.Field,
        FieldValues,
        StatisticReconciliationActualAggregateStores.RowLabel,
        LabelValues,
        StatisticReconciliationActualAggregateStores.TableMetric,
        TableValues
    ];
}
