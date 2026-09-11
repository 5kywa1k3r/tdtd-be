using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal interface IStatisticReconciliationActualPendingVerdictMaterialOwner
{
    Task<StatisticReconciliationActualTrustedCaptureMaterial>
        ResolvePendingVerdictAsync(
            StatisticReconciliationRun persistedRun,
            CancellationToken cancellationToken);

    Task<StatisticReconciliationActualTrustedCaptureMaterial>
        ResolveCurrentVerdictAsync(
            StatisticReconciliationRun persistedRun,
            CancellationToken cancellationToken);
}

/// <summary>
/// Rebuilds capture material only from a live claimed run and its immutable,
/// server-resolved capture plan. It performs no writes and never selects a
/// current/latest owner.
/// </summary>
internal sealed class StatisticReconciliationActualMongoClaimedCaptureMaterialOwner(
    MongoDbContext context,
    IStatRunCandidateActivation statRunActivation)
    : IStatisticReconciliationActualClaimedCaptureMaterialOwner,
      IStatisticReconciliationActualPendingVerdictMaterialOwner
{
    public async Task<StatisticReconciliationActualTrustedCaptureMaterial>
        ResolveAsync(
            StatisticReconciliationActualClaimedCaptureCommand command,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.WorkerActor);
        global::tdtd_be.Services.StatisticsReconciliation.Production.StatisticReconciliationInternalWorkerAccess.RequireWorkerOrSystemAdmin(command.WorkerActor);
        var reconciliationId = ObjectIdText(
            command.ReconciliationId,
            "RECONCILIATION_ID");
        var workerId = Required(command.WorkerId, "WORKER_ID");
        var claimToken = Required(command.ClaimToken, "CLAIM_TOKEN");

        var runs = await context.StatisticReconciliationRuns
            .Find(run => run.Id == reconciliationId && !run.IsDeleted)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (runs.Count != 1)
            throw Fail("RUN_NOT_EXACT");
        var persistedRun = runs[0];
        // Missing/legacy/tampered plans and stale fences fail before any
        // plan-referenced owner. State integrity is checked only against the
        // persisted run; the fresh binding becomes a transient owner view.
        _ = StatisticReconciliationActualClaimedRunPreOwnerGuard.Require(
            persistedRun,
            workerId,
            claimToken,
            DateTime.UtcNow,
            StatisticReconciliationRunService.RequireActualCaptureReadIntegrity,
            StatisticReconciliationActualCapturePlanIntegrity.RequireValid);
        return await ResolveCoreAsync(persistedRun, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StatisticReconciliationActualTrustedCaptureMaterial>
        ResolvePendingVerdictAsync(
            StatisticReconciliationRun persistedRun,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(persistedRun);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
            persistedRun);
        var pending =
            persistedRun.Status == StatisticReconciliationRunStatuses.Queued &&
            persistedRun.PendingGenerationId is not null &&
            persistedRun.PendingGenerationHash is not null &&
            persistedRun.PendingGenerationPublishedAtUtc.HasValue &&
            persistedRun.LeaseOwnerId is null &&
            persistedRun.ClaimToken is null &&
            !persistedRun.LeaseUntilUtc.HasValue &&
            !persistedRun.LastHeartbeatAtUtc.HasValue;
        if (!pending)
            throw Fail("PENDING_VERDICT_STATE_INVALID");
        return await ResolveCoreAsync(persistedRun, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StatisticReconciliationActualTrustedCaptureMaterial>
        ResolveCurrentVerdictAsync(
            StatisticReconciliationRun persistedRun,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(persistedRun);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
            persistedRun);
        var terminal = persistedRun.Status is
            StatisticReconciliationRunStatuses.Matched or
            StatisticReconciliationRunStatuses.Mismatched or
            StatisticReconciliationRunStatuses.Stale or
            StatisticReconciliationRunStatuses.Failed;
        var current = terminal && persistedRun.Recheck is null &&
            persistedRun.CurrentGenerationId is not null &&
            persistedRun.CurrentGenerationHash is not null &&
            persistedRun.PendingGenerationId is null &&
            persistedRun.PendingGenerationHash is null &&
            !persistedRun.PendingGenerationPublishedAtUtc.HasValue &&
            persistedRun.LeaseOwnerId is null &&
            persistedRun.ClaimToken is null &&
            !persistedRun.LeaseUntilUtc.HasValue &&
            !persistedRun.LastHeartbeatAtUtc.HasValue;
        if (!current)
            throw Fail("CURRENT_VERDICT_STATE_INVALID");
        return await ResolveCoreAsync(persistedRun, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<StatisticReconciliationActualTrustedCaptureMaterial>
        ResolveCoreAsync(
            StatisticReconciliationRun persistedRun,
            CancellationToken cancellationToken)
    {
        var run = StatisticReconciliationRecheckCaptureBindingCanonical
            .EffectiveCaptureRun(persistedRun);
        var plan = run.ActualCapturePlan ??
            throw Fail("CAPTURE_PLAN_REQUIRED");
        StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
            plan, run.ActualCapturePlanSha256);
        if (!StringComparer.Ordinal.Equals(
                plan.ActualConfigurationBundleSha256,
                run.ActualConfigurationBundleSha256))
            throw Fail("CONFIGURATION_BUNDLE_DRIFT");
        RequireCapturePlanRunBinding(run, plan);
        RequirePeriodShape(run, plan);
        var canonicalFilter = Required(
            run.CanonicalFilterJson,
            "CANONICAL_FILTER_JSON",
            64 * 1024);

        var job = await LoadP9Async(run, cancellationToken).ConfigureAwait(false);
        RequireP9RunBinding(run, job);
        var direct = DirectBoundary(job);
        _ = await new StatisticReconciliationActualDirectGenerationMongoGuard(context)
            .RequireAsync(direct, cancellationToken)
            .ConfigureAwait(false);
        var aggregate = AggregateBoundary(job, direct);

        WorkAssignmentBasicSummarySnapshot? basicSnapshot = null;
        StatisticReconciliationActualBasicCaptureTarget? basic = null;
        if (!StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan) ||
            StatisticReconciliationActualCapturePlanIntegrity.IsConfigured(
                plan.Basic.Disposition))
        {
            basicSnapshot = await LoadBasicAsync(
                    plan.Basic.SnapshotId,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireBasicBinding(run, plan, basicSnapshot);
            basic = new StatisticReconciliationActualBasicCaptureTarget(
                plan.Basic.Mode,
                BasicBoundary(basicSnapshot));
        }

        var days = new List<WorkAssignmentAdvancedSummaryDayNode>();
        var months = new List<WorkAssignmentAdvancedSummaryMonthNode>();
        var years = new List<WorkAssignmentAdvancedSummaryYearNode>();
        ActualAdvancedOwnerBoundary? advanced = null;
        if (!StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan) ||
            StatisticReconciliationActualCapturePlanIntegrity.IsConfigured(
                plan.Advanced.Disposition))
        {
            (days, months, years) = await LoadAdvancedAsync(
                    run,
                    plan,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireAdvancedBinding(run, plan, days, months, years);
            advanced = AdvancedBoundary(plan, days, months, years);
        }

        WorkReportStatisticDiffResult? diffResult = null;
        ActualP9DiffOwnerBoundary? diff = null;
        if (!StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan) ||
            StatisticReconciliationActualCapturePlanIntegrity.IsConfigured(
                plan.Diff.Disposition))
        {
            diffResult = await LoadDiffAsync(
                    plan.Diff.ResultId,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireDiffBinding(run, plan, diffResult);
            diff = DiffBoundary(diffResult);
        }

        var expectedApiOwner = plan.Api.Surface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField or
            StatisticReconciliationActualApiSurfaces.DirectTable or
            StatisticReconciliationActualApiSurfaces.DirectLabel => job.Id,
            StatisticReconciliationActualApiSurfaces.BasicSource =>
                basicSnapshot?.Id ?? throw Fail("API_BASIC_OWNER_NOT_APPLICABLE"),
            StatisticReconciliationActualApiSurfaces.P9Diff =>
                diffResult?.Id ?? throw Fail("API_DIFF_OWNER_NOT_APPLICABLE"),
            _ => throw Fail("API_SURFACE_UNSUPPORTED")
        };
        if (!StringComparer.Ordinal.Equals(
                plan.Api.OwnerResultId,
                expectedApiOwner))
            throw Fail("API_OWNER_BINDING_DRIFT");
        var expectedPageCount = Math.Max(
            1,
            checked((int)((plan.Api.ExpectedTotalRows + 199L) / 200L)));
        if (plan.Api.ExpectedTotalRows is < 0 or > 6_400 ||
            plan.Api.PageSize != 200 ||
            plan.Api.PageCount is < 1 or > 32 ||
            plan.Api.PageCount != expectedPageCount)
            throw Fail("API_PAGE_PLAN_INVALID");
        var api = new StatisticReconciliationActualApiCaptureRequest(
            plan.Api.Surface,
            run.WorkId,
            run.ScopeAssignmentId,
            run.DynamicFormVersionId,
            expectedApiOwner,
            canonicalFilter,
            plan.Api.ExpectedTotalRows,
            Enumerable.Range(0, plan.Api.PageCount)
                .Select(page => new StatisticReconciliationActualApiPageSelector(
                    page,
                    plan.Api.PageSize))
                .ToImmutableArray());

        var exportArtifact = await LoadExportAsync(
                plan.Export,
                cancellationToken)
            .ConfigureAwait(false);
        RequireExportBinding(
            run,
            plan.Export,
            exportArtifact,
            job,
            basicSnapshot,
            days,
            months,
            years,
            diffResult);
        var export = new StatisticReconciliationActualExportOwnerTarget(
            plan.Export.ExportId,
            plan.Export.ResultKind,
            plan.Export.WorkId,
            plan.Export.ScopeType,
            plan.Export.ScopeId,
            plan.Export.ResultId,
            Sha(exportArtifact.SourceHash, "EXPORT_SOURCE_OWNER"),
            Sha(exportArtifact.ConfigHash, "EXPORT_CONFIG_OWNER"),
            plan.Export.RequestSha256,
            plan.Export.AuthorizationSnapshotSha256,
            plan.Export.ContentSha256,
            plan.Export.ColumnManifestSha256,
            plan.Export.OwnerSemanticSha256,
            Required(run.PeriodInstanceKey, "EXPORT_PERIOD_INSTANCE_KEY"),
            ExpectedExportFilterSha(run, plan.Export));

        var lifecycleMetricScope = await new
                StatisticReconciliationActualLifecycleMetricScopeOwner(context)
            .ResolveAsync(run, plan, basic?.Boundary, cancellationToken)
            .ConfigureAwait(false);
        var source = new ActualSourceMembershipScope(
            Required(run.WorkId, "WORK_ID"),
            Required(run.PeriodInstanceKey, "PERIOD_INSTANCE_KEY"),
            Required(run.DynamicFormVersionId, "FORM_VERSION_ID"),
            Required(job.SourceMembershipSignature, "SOURCE_MEMBERSHIP"),
            job.Id,
            Required(job.GenerationId, "P9_GENERATION_ID"),
            Sha(job.GenerationHash, "P9_GENERATION_HASH"),
            Positive(job.DirectSourceRevision, "P9_DIRECT_SOURCE_REVISION"),
            lifecycleMetricScope);

        var sourceReportIds = basicSnapshot is null
            ? ImmutableArray<string>.Empty
            : Ids(
                basicSnapshot.SourceReportIds,
                "BASIC_SOURCE_REPORT_IDS",
                allowEmpty: false);
        var selectors = new StatisticReconciliationActualBoundaryOwnerSelectors(
            sourceReportIds,
            basicSnapshot?.Id,
            plan.Basic.ImmutableSelectorSha256,
            Ids(plan.Advanced.DayNodeIds, "ADVANCED_DAY_IDS", true),
            Ids(plan.Advanced.MonthNodeIds, "ADVANCED_MONTH_IDS", true),
            Ids(plan.Advanced.YearNodeIds, "ADVANCED_YEAR_IDS", true),
            plan.Advanced.ImmutableSelectorSha256,
            diffResult?.Id,
            plan.Diff.ImmutableSelectorSha256,
            plan.Export.ResultKind == StatRunExportResultKinds.Diff
                ? "work_report_statistic_diff_exports"
                : "work_report_statistic_exports",
            exportArtifact.Id,
            exportArtifact.LifecycleRevision);

        return new StatisticReconciliationActualTrustedCaptureMaterial(
            run,
            plan.BoundaryRegistryVersion,
            source,
            direct,
            aggregate,
            basic,
            advanced,
            diff,
            api,
            export,
            selectors) { PersistedRun = persistedRun };

    }

    private static void RequireCapturePlanRunBinding(
        StatisticReconciliationRun run,
        StatisticReconciliationActualCapturePlan plan)
    {
        if (!StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan))
            return;
        if (!Same(plan.P8ConfigurationOwnerId, run.P8ConfigOwnerId) ||
            !Same(plan.P8ConfigurationBundleSha256, run.P8ConfigBundleHash) ||
            !StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                plan.Basic.Disposition) ||
            !StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                plan.Advanced.Disposition) ||
            !StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                plan.Diff.Disposition))
        {
            throw Fail("V4_APPLICABILITY_RUN_BINDING_DRIFT");
        }
    }

    private static void RequirePeriodShape(
        StatisticReconciliationRun run,
        StatisticReconciliationActualCapturePlan plan)
    {
        var start = run.PeriodStartUtc;
        var end = run.PeriodEndUtc;
        if (!start.HasValue || !end.HasValue)
        {
            if (start.HasValue != end.HasValue ||
                !StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan) ||
                !StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                    plan.Advanced.Disposition))
                throw Fail("PERIOD_WINDOW_INVALID");
            return;
        }
        if (start.Value.Kind != DateTimeKind.Utc ||
            end.Value.Kind != DateTimeKind.Utc ||
            end.Value < start.Value ||
            ((!StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan) ||
              !StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                  plan.Advanced.Disposition)) && end.Value == start.Value))
        {
            throw Fail("PERIOD_WINDOW_INVALID");
        }
    }
    private async Task<WorkReportStatisticRebuildJob> LoadP9Async(
        StatisticReconciliationRun run,
        CancellationToken cancellationToken)
    {
        var id = ObjectIdText(run.P9RunId, "P9_RUN_ID");
        var rows = await context.WorkReportStatisticRebuildJobs
            .Find(job => job.Id == id)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Count == 1 ? rows[0] : throw Fail("P9_OWNER_NOT_EXACT");
    }

    private async Task<WorkAssignmentBasicSummarySnapshot> LoadBasicAsync(
        string id,
        CancellationToken cancellationToken)
    {
        id = ObjectIdText(id, "BASIC_SNAPSHOT_ID");
        var rows = await context.WorkAssignmentBasicSummarySnapshots
            .Find(row => row.Id == id)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Count == 1 ? rows[0] : throw Fail("BASIC_OWNER_NOT_EXACT");
    }

    private async Task<(
        List<WorkAssignmentAdvancedSummaryDayNode> Days,
        List<WorkAssignmentAdvancedSummaryMonthNode> Months,
        List<WorkAssignmentAdvancedSummaryYearNode> Years)> LoadAdvancedAsync(
            StatisticReconciliationRun run,
            StatisticReconciliationActualCapturePlan plan,
            CancellationToken cancellationToken)
    {
        var dayIds = Ids(plan.Advanced.DayNodeIds, "ADVANCED_DAY_IDS", true);
        var monthIds = Ids(plan.Advanced.MonthNodeIds, "ADVANCED_MONTH_IDS", true);
        var yearIds = Ids(plan.Advanced.YearNodeIds, "ADVANCED_YEAR_IDS", true);
        var periodStart = Utc(run.PeriodStartUtc, "PERIOD_START_UTC");
        var periodEnd = Utc(run.PeriodEndUtc, "PERIOD_END_UTC");
        if (periodEnd <= periodStart)
            throw Fail("ADVANCED_PERIOD_WINDOW_INVALID");
        var dayTask = LoadTopologyAsync(
            context.WorkAssignmentAdvancedSummaryDayNodes,
            run,
            plan.Advanced.SectionId,
            periodStart,
            periodEnd,
            cancellationToken);
        var monthTask = LoadTopologyAsync(
            context.WorkAssignmentAdvancedSummaryMonthNodes,
            run,
            plan.Advanced.SectionId,
            periodStart,
            periodEnd,
            cancellationToken);
        var yearTask = LoadTopologyAsync(
            context.WorkAssignmentAdvancedSummaryYearNodes,
            run,
            plan.Advanced.SectionId,
            periodStart,
            periodEnd,
            cancellationToken);
        await Task.WhenAll(dayTask, monthTask, yearTask).ConfigureAwait(false);
        var days = await dayTask;
        var months = await monthTask;
        var years = await yearTask;
        RequireExactTopology(days, dayIds, "ADVANCED_DAY_IDS");
        RequireExactTopology(months, monthIds, "ADVANCED_MONTH_IDS");
        RequireExactTopology(years, yearIds, "ADVANCED_YEAR_IDS");
        return (days, months, years);
    }

    private static async Task<List<TNode>> LoadTopologyAsync<TNode>(
        IMongoCollection<TNode> collection,
        StatisticReconciliationRun run,
        string sectionId,
        DateTime periodStart,
        DateTime periodEnd,
        CancellationToken cancellationToken)
        where TNode : WorkAssignmentAdvancedSummaryHierarchyNodeBase
    {
        var filter = Builders<TNode>.Filter;
        var rows = await collection.Find(
                filter.Eq(row => row.WorkId, run.WorkId) &
                filter.Eq(row => row.AssignmentId, run.ScopeAssignmentId) &
                filter.Eq(
                    row => row.DynamicFormTemplateId,
                    run.DynamicFormVersionId) &
                filter.Eq(row => row.SectionId, sectionId) &
                filter.Eq(row => row.IsDeleted, false) &
                filter.Lte(row => row.WindowStartUtc, periodEnd) &
                filter.Gt(row => row.WindowEndExclusiveUtc, periodStart))
            .Limit(4097)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count > 4096)
            throw Fail("ADVANCED_TOPOLOGY_BOUNDS_EXCEEDED");
        return rows;
    }

    private static void RequireExactTopology<TNode>(
        IReadOnlyList<TNode> rows,
        ImmutableArray<string> expectedIds,
        string name)
        where TNode : WorkAssignmentAdvancedSummaryHierarchyNodeBase
    {
        var actualIds = Ids(
            rows.Select(row => row.Id).ToArray(),
            name,
            allowEmpty: true);
        if (!actualIds.SequenceEqual(expectedIds, StringComparer.Ordinal))
            throw Fail("ADVANCED_OWNER_SET_DRIFT");
    }

    private async Task<WorkReportStatisticDiffResult> LoadDiffAsync(
        string id,
        CancellationToken cancellationToken)
    {
        id = ObjectIdText(id, "DIFF_RESULT_ID");
        var collection = context.Db.GetCollection<WorkReportStatisticDiffResult>(
            StatisticReconciliationActualP9DiffMongoOwnerReader.CollectionName);
        var rows = await collection.Find(row => row.Id == id)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Count == 1 ? rows[0] : throw Fail("DIFF_OWNER_NOT_EXACT");
    }

    private async Task<StatRunExportArtifact> LoadExportAsync(
        StatisticReconciliationActualExportTargetPlan plan,
        CancellationToken cancellationToken)
    {
        var collection = plan.ResultKind == StatRunExportResultKinds.Diff
            ? context.WorkReportStatisticDiffExports
            : context.WorkReportStatisticExports;
        var rows = await collection.Find(row => row.Id == plan.ExportId)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Count == 1 ? rows[0] : throw Fail("EXPORT_OWNER_NOT_EXACT");
    }

    private static void RequireLiveFence(
        StatisticReconciliationRun run,
        string workerId,
        string claimToken)
    {
        var now = DateTime.UtcNow;
        if (!StringComparer.Ordinal.Equals(
                run.Status,
                StatisticReconciliationRunStatuses.Running) ||
            !StringComparer.Ordinal.Equals(run.LeaseOwnerId, workerId) ||
            !StringComparer.Ordinal.Equals(run.ClaimToken, claimToken) ||
            run.LeaseUntilUtc is not { Kind: DateTimeKind.Utc } leaseUntil ||
            leaseUntil <= now ||
            run.DeadlineAtUtc.Kind != DateTimeKind.Utc ||
            run.DeadlineAtUtc <= now ||
            !HasCaptureGenerationShape(run))
            throw Fail("STALE_WORKER_FENCE");
    }

    private static bool HasCaptureGenerationShape(
        StatisticReconciliationRun run)
    {
        var noPending = run.PendingGenerationId is null &&
                        run.PendingGenerationHash is null &&
                        run.PendingGenerationPublishedAtUtc is null;
        if (!noPending)
            return false;
        if (run.Recheck is null)
            return run.CurrentGenerationId is null &&
                   run.CurrentGenerationHash is null;
        return run.Recheck.Phase ==
                   StatisticReconciliationRecheckPhases.CaptureRunning &&
               Same(run.CurrentGenerationId,
                   run.Recheck.BaseCurrentGenerationId) &&
               Same(run.CurrentGenerationHash,
                   run.Recheck.BaseCurrentGenerationHash);
    }

    private static void RequireP9RunBinding(
        StatisticReconciliationRun run,
        WorkReportStatisticRebuildJob job)
    {
        if (!P9RunBindingMatches(run, job))
            throw Fail("P9_RUN_BINDING_DRIFT");
    }

    internal static bool P9RunBindingMatches(
        StatisticReconciliationRun run,
        WorkReportStatisticRebuildJob job)
        => Same(job.Id, run.P9RunId) &&
           Same(job.WorkId, run.WorkId) &&
           Same(job.DynamicFormFamilyId, run.DynamicFormFamilyId) &&
           Same(job.DynamicFormTemplateId, run.DynamicFormVersionId) &&
           job.DynamicFormVersionNo == run.DynamicFormVersionNo &&
           Same(job.DynamicFormSchemaHash, run.DynamicFormSchemaHash) &&
           Same(job.GenerationId, run.P9GenerationId) &&
           Same(job.GenerationHash, run.P9GenerationHash) &&
           Same(job.SourceReportId, run.SourceReportId) &&
           job.SourcePayloadRevision == run.SourcePayloadRevision &&
           Same(job.SourcePayloadHash, run.SourcePayloadHash) &&
           job.SourceLifecycleRevision == run.SourceLifecycleRevision &&
           Same(job.SourceLifecycleEventKey, run.SourceLifecycleEventKey) &&
           Same(job.ConfigId, run.P8ConfigId) &&
           Same(job.ConfigVersionId, run.P8ConfigVersionId) &&
           job.ConfigVersionNo == run.P8ConfigVersionNo &&
           job.ConfigRevision == run.P8ConfigRevision &&
           Same(job.ConfigHash, run.P8ConfigHash) &&
           Same(job.CandidateChainId, run.P9CandidateChainId) &&
           Same(job.CandidatePromptId, run.P9CandidatePromptId) &&
           Same(job.CatalogVersion, run.P9CatalogVersion) &&
           Same(job.CatalogRawSha256, run.P9CatalogRawSha256) &&
           Same(job.CatalogSemanticSha256, run.P9CatalogSemanticSha256) &&
           Same(job.SchemaRawSha256, run.P9SchemaRawSha256) &&
           Same(job.SchemaSemanticSha256, run.P9SchemaSemanticSha256) &&
           Same(job.StageLockSha256, run.P9StageLockSha256) &&
           Same(job.PeriodKey, run.PeriodKey) &&
           Same(job.PeriodInstanceKey, run.PeriodInstanceKey) &&
           Same(job.PeriodKind, run.PeriodKind) &&
           job.PeriodStartUtc == run.PeriodStartUtc &&
           job.PeriodEndUtc == run.PeriodEndUtc &&
           Same(job.FlowInstanceId, run.FlowInstanceId) &&
           job.FlowInstanceRevision == run.FlowInstanceRevision &&
           job.FlowExecutionEpoch == run.FlowExecutionEpoch &&
           Same(job.FlowExecutionEpochId, run.FlowExecutionEpochId) &&
           job.FlowExecutionEpochRevision == run.FlowExecutionEpochRevision &&
           Same(job.FlowStepInstanceId, run.FlowStepInstanceId) &&
           job.FlowStepInstanceRevision == run.FlowStepInstanceRevision &&
           Same(job.Status, WorkReportStatisticRebuildJobStatuses.Completed) &&
           job.IsCurrentPublication && !job.IsActive && !job.IsDeleted;

    private static ActualDirectProjectionBoundary DirectBoundary(
        WorkReportStatisticRebuildJob job)
        => new(
            Required(job.WorkId, "P9_WORK_ID"),
            Required(job.PeriodInstanceKey, "P9_PERIOD_INSTANCE_KEY"),
            ObjectIdText(job.DynamicFormFamilyId, "P9_FORM_FAMILY_ID"),
            ObjectIdText(job.DynamicFormTemplateId, "P9_FORM_VERSION_ID"),
            Positive(job.DynamicFormVersionNo, "P9_FORM_VERSION_NO"),
            Sha(job.DynamicFormSchemaHash, "P9_FORM_SCHEMA"),
            ObjectIdText(job.Id, "P9_RUN_ID"),
            Required(job.GenerationId, "P9_GENERATION_ID"),
            Sha(job.GenerationHash, "P9_GENERATION_HASH"),
            Required(job.SourceLifecycleEventKey, "P9_LIFECYCLE_EVENT"),
            Utc(job.ComputedAtUtc, "P9_COMPUTED_AT"),
            Positive(job.DirectSourceRevision, "P9_DIRECT_SOURCE_REVISION"),
            ObjectIdText(job.ConfigId, "P9_CONFIG_ID"),
            ObjectIdText(job.ConfigVersionId, "P9_CONFIG_VERSION_ID"),
            Positive(job.ConfigVersionNo, "P9_CONFIG_VERSION_NO"),
            Positive(job.ConfigRevision, "P9_CONFIG_REVISION"),
            Sha(job.ConfigHash, "P9_CONFIG_HASH"),
            Required(job.CandidateChainId, "P9_CANDIDATE_CHAIN"),
            Required(job.CatalogVersion, "P9_CATALOG_VERSION"),
            Sha(job.CatalogRawSha256, "P9_CATALOG_RAW"),
            Sha(job.CatalogSemanticSha256, "P9_CATALOG_SEMANTIC"),
            Sha(job.SchemaRawSha256, "P9_SCHEMA_RAW"),
            Sha(job.SchemaSemanticSha256, "P9_SCHEMA_SEMANTIC"),
            Sha(job.StageLockSha256, "P9_STAGE_LOCK"),
            Sha(job.SourceMembershipSignature, "P9_SOURCE_MEMBERSHIP"));

    private static ActualAggregatePublicationBoundary AggregateBoundary(
        WorkReportStatisticRebuildJob job,
        ActualDirectProjectionBoundary direct)
    {
        var pins = job.DirectStoreDigests
            .Where(pin => StatisticReconciliationActualAggregateStores.ExactSet
                .Contains(pin.Store, StringComparer.Ordinal))
            .OrderBy(pin => pin.Store, StringComparer.Ordinal)
            .Select(pin => new ActualAggregateStoreDigestPin(
                Required(pin.Store, "AGGREGATE_STORE"),
                NonNegative(pin.RowCount, "AGGREGATE_ROW_COUNT"),
                Sha(pin.Sha256, "AGGREGATE_STORE_SHA")))
            .ToImmutableArray();
        if (pins.Length != StatisticReconciliationActualAggregateStores.ExactSet.Length ||
            pins.Select(pin => pin.Store).Distinct(StringComparer.Ordinal).Count() !=
            pins.Length)
            throw Fail("AGGREGATE_DIGEST_INVENTORY_INVALID");
        return new ActualAggregatePublicationBoundary(
            direct,
            Required(job.PublicationScopeKey, "PUBLICATION_SCOPE"),
            Positive(job.DirectPublicationRevision, "PUBLICATION_REVISION"),
            Required(job.FreshnessState, "FRESHNESS_STATE"),
            Utc(job.PublishedAtUtc, "PUBLISHED_AT"),
            pins);
    }

    private static void RequireBasicBinding(
        StatisticReconciliationRun run,
        StatisticReconciliationActualCapturePlan plan,
        WorkAssignmentBasicSummarySnapshot row)
    {
        if (!Same(row.Id, plan.Basic.SnapshotId) ||
            !Same(row.WorkId, run.WorkId) ||
            !Same(row.ScopeAssignmentId, run.ScopeAssignmentId) ||
            !Same(row.DynamicFormTemplateId, run.DynamicFormVersionId) ||
            !Same(row.SourceScopeMode, plan.Basic.Mode) ||
            row.IsDeleted ||
            !Same(
                StatisticReconciliationActualCapturePlanIntegrity
                    .BasicSelectorSha(row),
                plan.Basic.ImmutableSelectorSha256))
            throw Fail("BASIC_OWNER_BINDING_DRIFT");
    }

    private static ActualBasicOwnerBoundary BasicBoundary(
        WorkAssignmentBasicSummarySnapshot row)
        => new(
            row.Id,
            row.WorkId,
            row.ScopeAssignmentId,
            row.DynamicFormTemplateId,
            row.SourceScopeMode,
            row.SourceFlowInstanceId,
            row.SourceFlowStepId,
            row.SourceFlowBranchId,
            row.SourceFlowEffectiveStatus,
            Sha(row.RequestHash, "BASIC_REQUEST"),
            ObjectIdText(row.ConfigId, "BASIC_CONFIG_ID"),
            ObjectIdText(row.ConfigVersionId, "BASIC_CONFIG_VERSION_ID"),
            Positive(row.ConfigVersionNo, "BASIC_CONFIG_VERSION_NO"),
            Positive(row.ConfigRevision, "BASIC_CONFIG_REVISION"),
            Sha(row.ConfigHash, "BASIC_CONFIG_HASH"),
            Values(row.ConfigDependencyPins, "BASIC_DEPENDENCY_PINS"),
            Required(row.CandidateChainId, "BASIC_CANDIDATE_CHAIN"),
            Required(row.CandidatePromptId, "BASIC_CANDIDATE_PROMPT"),
            Positive(row.CandidateStage, "BASIC_CANDIDATE_STAGE"),
            Sha(row.CandidateCatalogRawSha256, "BASIC_CATALOG_RAW"),
            Sha(row.CandidateCatalogSemanticSha256, "BASIC_CATALOG_SEMANTIC"),
            Sha(row.CandidateStageLockSha256, "BASIC_STAGE_LOCK"));

    private static void RequireAdvancedBinding(
        StatisticReconciliationRun run,
        StatisticReconciliationActualCapturePlan plan,
        IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode> days,
        IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode> months,
        IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode> years)
    {
        var all = days.Cast<WorkAssignmentAdvancedSummaryHierarchyNodeBase>()
            .Concat(months)
            .Concat(years)
            .ToArray();
        if (all.Length == 0 || all.Any(row =>
                !Same(row.WorkId, run.WorkId) ||
                !Same(row.AssignmentId, run.ScopeAssignmentId) ||
                !Same(row.DynamicFormTemplateId, run.DynamicFormVersionId) ||
                !Same(row.SectionId, plan.Advanced.SectionId) ||
                row.IsDeleted) ||
            !Same(
                StatisticReconciliationActualCapturePlanIntegrity
                    .AdvancedSelectorSha(plan.Advanced.SectionId, days, months, years),
                plan.Advanced.ImmutableSelectorSha256))
            throw Fail("ADVANCED_OWNER_BINDING_DRIFT");
        var anchor = all[0];
        if (all.Skip(1).Any(row => !SameAdvancedOwner(anchor, row)))
            throw Fail("ADVANCED_OWNER_BOUNDARY_MIXED");
    }

    private static ActualAdvancedOwnerBoundary AdvancedBoundary(
        StatisticReconciliationActualCapturePlan plan,
        IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode> days,
        IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode> months,
        IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode> years)
    {
        var anchor = days.Cast<WorkAssignmentAdvancedSummaryHierarchyNodeBase>()
            .Concat(months)
            .Concat(years)
            .First();
        return new ActualAdvancedOwnerBoundary(
            anchor.WorkId,
            anchor.AssignmentId,
            anchor.DynamicFormTemplateId,
            plan.Advanced.SectionId,
            anchor.ConfigId,
            anchor.ConfigVersionId,
            anchor.ConfigVersionNo,
            anchor.ConfigRevision,
            anchor.ConfigHash,
            Values(anchor.DependencyPins, "ADVANCED_DEPENDENCY_PINS"),
            Required(anchor.TimeAxis, "ADVANCED_TIME_AXIS"),
            Required(anchor.CandidateChainId, "ADVANCED_CANDIDATE_CHAIN"),
            Required(anchor.CandidatePromptId, "ADVANCED_CANDIDATE_PROMPT"),
            Positive(anchor.CandidateStage, "ADVANCED_CANDIDATE_STAGE"),
            Sha(anchor.CandidateCatalogRawSha256, "ADVANCED_CATALOG_RAW"),
            Sha(anchor.CandidateCatalogSemanticSha256, "ADVANCED_CATALOG_SEMANTIC"),
            Sha(anchor.CandidateStageLockSha256, "ADVANCED_STAGE_LOCK"),
            Ids(plan.Advanced.DayNodeIds, "ADVANCED_DAY_IDS", true),
            Ids(plan.Advanced.MonthNodeIds, "ADVANCED_MONTH_IDS", true),
            Ids(plan.Advanced.YearNodeIds, "ADVANCED_YEAR_IDS", true));
    }

    private static bool SameAdvancedOwner(
        WorkAssignmentAdvancedSummaryHierarchyNodeBase left,
        WorkAssignmentAdvancedSummaryHierarchyNodeBase right)
        => Same(left.WorkId, right.WorkId) &&
           Same(left.AssignmentId, right.AssignmentId) &&
           Same(left.DynamicFormTemplateId, right.DynamicFormTemplateId) &&
           Same(left.SectionId, right.SectionId) &&
           Same(left.ConfigId, right.ConfigId) &&
           Same(left.ConfigVersionId, right.ConfigVersionId) &&
           left.ConfigVersionNo == right.ConfigVersionNo &&
           left.ConfigRevision == right.ConfigRevision &&
           Same(left.ConfigHash, right.ConfigHash) &&
           left.DependencyPins.SequenceEqual(right.DependencyPins,
               StringComparer.Ordinal) &&
           Same(left.TimeAxis, right.TimeAxis) &&
           Same(left.CandidateChainId, right.CandidateChainId) &&
           Same(left.CandidatePromptId, right.CandidatePromptId) &&
           left.CandidateStage == right.CandidateStage &&
           Same(left.CandidateCatalogRawSha256, right.CandidateCatalogRawSha256) &&
           Same(left.CandidateCatalogSemanticSha256,
               right.CandidateCatalogSemanticSha256) &&
           Same(left.CandidateStageLockSha256, right.CandidateStageLockSha256);

    private static void RequireDiffBinding(
        StatisticReconciliationRun run,
        StatisticReconciliationActualCapturePlan plan,
        WorkReportStatisticDiffResult row)
    {
        if (!Same(row.Id, plan.Diff.ResultId) ||
            !Same(row.RunId, plan.Diff.RunId) ||
            !Same(row.WorkId, run.WorkId) ||
            !Same(row.AssignmentId, run.ScopeAssignmentId) ||
            !Same(row.DynamicFormTemplateId, run.DynamicFormVersionId) ||
            row.IsDeleted ||
            !Same(
                StatisticReconciliationActualCapturePlanIntegrity.DiffSelectorSha(row),
                plan.Diff.ImmutableSelectorSha256))
            throw Fail("DIFF_OWNER_BINDING_DRIFT");
    }

    private static ActualP9DiffOwnerBoundary DiffBoundary(
        WorkReportStatisticDiffResult row)
        => new(
            row.Id,
            row.RunId,
            row.WorkId,
            row.AssignmentId,
            Required(row.DynamicFormTemplateId, "DIFF_FORM_VERSION"),
            ObjectIdText(row.ConfigId, "DIFF_CONFIG_ID"),
            ObjectIdText(row.ConfigVersionId, "DIFF_CONFIG_VERSION_ID"),
            Positive(row.ConfigVersionNo, "DIFF_CONFIG_VERSION_NO"),
            Positive(row.ConfigRevision, "DIFF_CONFIG_REVISION"),
            Sha(row.ConfigHash, "DIFF_CONFIG_HASH"),
            Values(row.DependencyPins, "DIFF_DEPENDENCY_PINS"),
            Required(row.CandidateChainId, "DIFF_CANDIDATE_CHAIN"),
            Required(row.CandidatePromptId, "DIFF_CANDIDATE_PROMPT"),
            Positive(row.CandidateStage, "DIFF_CANDIDATE_STAGE"),
            Sha(row.CandidateCatalogRawSha256, "DIFF_CATALOG_RAW"),
            Sha(row.CandidateCatalogSemanticSha256, "DIFF_CATALOG_SEMANTIC"),
            Sha(row.CandidateStageLockSha256, "DIFF_STAGE_LOCK"));

    private void RequireExportBinding(
        StatisticReconciliationRun run,
        StatisticReconciliationActualExportTargetPlan plan,
        StatRunExportArtifact row,
        WorkReportStatisticRebuildJob job,
        WorkAssignmentBasicSummarySnapshot? basic,
        IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode> days,
        IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode> months,
        IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode> years,
        WorkReportStatisticDiffResult? diff)
    {
        var expectedFilterSha = ExpectedExportFilterSha(run, plan);
        if (!Same(row.Id, plan.ExportId) ||
            !Same(row.ResultKind, plan.ResultKind) ||
            !Same(row.WorkId, plan.WorkId) ||
            !Same(row.ScopeType, plan.ScopeType) ||
            !Same(row.ScopeId, plan.ScopeId) ||
            !Same(row.ResultId, plan.ResultId) ||
            !Same(row.PeriodInstanceKey, run.PeriodInstanceKey) ||
            !Same(row.RequestHash, plan.RequestSha256) ||
            !Same(row.AuthorizationSnapshotHash,
                plan.AuthorizationSnapshotSha256) ||
            !Same(row.ContentHash, plan.ContentSha256) ||
            !Same(row.ColumnManifestSha256,
                plan.ColumnManifestSha256) ||
            !Same(row.SemanticHash, plan.OwnerSemanticSha256) ||
            !Same(row.FilterHash, expectedFilterSha) ||
            string.IsNullOrWhiteSpace(row.CanonicalFilterJson) ||
            !Same(
                StatisticReconciliationActualJson.RawSha256(
                    row.CanonicalFilterJson),
                expectedFilterSha) ||
            !ExportResultBinding(
                row, job, basic, days, months, years, diff) ||
            row.IsDeleted ||
            !Same(row.Status, StatRunExportStatuses.Completed) ||
            row.ExpiresAtUtc.Kind != DateTimeKind.Utc ||
            row.ExpiresAtUtc <= DateTime.UtcNow)
            throw Fail("EXPORT_OWNER_BINDING_DRIFT");
    }

    private static string ExpectedExportFilterSha(
        StatisticReconciliationRun run,
        StatisticReconciliationActualExportTargetPlan plan)
        => run.ActualCapturePlan?.SchemaVersion switch
        {
            StatisticReconciliationActualCapturePlanVersions.V3 or
            StatisticReconciliationActualCapturePlanVersions.V4 =>
                Sha(plan.FilterSha256, "EXPORT_FILTER_SHA256"),
            StatisticReconciliationActualCapturePlanVersions.V1 or
            StatisticReconciliationActualCapturePlanVersions.V2 =>
                Sha(run.FilterHash, "LEGACY_EXPORT_FILTER_SHA256"),
            _ => throw Fail("EXPORT_CAPTURE_PLAN_VERSION_INVALID")
        };

    private bool ExportResultBinding(
        StatRunExportArtifact row,
        WorkReportStatisticRebuildJob job,
        WorkAssignmentBasicSummarySnapshot? basic,
        IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode> days,
        IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode> months,
        IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode> years,
        WorkReportStatisticDiffResult? diff)
    {
        if (row.ResultKind is StatRunExportResultKinds.DirectField or
            StatRunExportResultKinds.DirectTable or
            StatRunExportResultKinds.DirectLabel)
        {
            return (Same(row.ResultId, job.Id) ||
                    Same(row.ResultId, job.GenerationId)) &&
                   Same(row.ResultHash, job.GenerationHash) &&
                   Same(row.SourceHash, job.SourcePayloadHash) &&
                   Same(row.ConfigHash, job.ConfigHash) &&
                   row.LifecycleRevision == job.SourceLifecycleRevision &&
                   ExportCandidate(row);
        }

        if (row.ResultKind is StatRunExportResultKinds.Basic or
            StatRunExportResultKinds.Flow)
        {
            if (basic is null)
                return false;
            var expectedKind =
                StatisticReconciliationActualBasicModes.IsFlow(
                    basic.SourceScopeMode)
                    ? StatRunExportResultKinds.Flow
                    : StatRunExportResultKinds.Basic;
            return Same(row.ResultKind, expectedKind) &&
                   Same(row.ResultId, basic.Id) &&
                   Same(row.ResultHash,
                       StatRunCanonicalJson.HashText(
                           basic.SnapshotJson ?? string.Empty)) &&
                   Same(row.SourceHash, basic.SourceSignatureHash) &&
                   Same(row.ConfigHash, basic.ConfigHash) &&
                   row.LifecycleRevision == 0 &&
                   ExportCandidate(row);
        }

        if (row.ResultKind == StatRunExportResultKinds.Advanced)
        {
            var node = days.Cast<WorkAssignmentAdvancedSummaryHierarchyNodeBase>()
                .Concat(months)
                .Concat(years)
                .SingleOrDefault(value => Same(value.Id, row.ResultId));
            if (node is null)
                return false;
            return Same(row.ResultHash, node.ValueHash) &&
                   Same(row.SourceHash,
                       node.SourceSignatureHash ??
                       StatRunCanonicalJson.HashObject(node.SourceReportIds)) &&
                   Same(row.ConfigHash, node.ConfigHash) &&
                   row.LifecycleRevision == 0 &&
                   ExportCandidate(row);
        }

        if (row.ResultKind == StatRunExportResultKinds.Diff)
        {
            if (diff is null)
                return false;
            var lifecycleRevision = diff.SourcePins.Count == 0
                ? 0
                : diff.SourcePins.Max(value =>
                    value.SourceLifecycleRevision);
            return Same(row.ResultId, diff.Id) &&
                   Same(row.ResultHash, diff.ResultHash) &&
                   Same(row.SourceHash,
                       StatRunCanonicalJson.HashObject(diff.SourcePins)) &&
                   Same(row.ConfigHash, diff.ConfigHash) &&
                   row.LifecycleRevision == lifecycleRevision &&
                   ExportCandidate(row);
        }

        return false;
    }

    private bool ExportCandidate(StatRunExportArtifact row)
    {
        var capability = StatRunExportContract.CapabilityFor(row.ResultKind);
        var evaluation = statRunActivation.EvaluateCapability(
            capability,
            StatRunExportContract.RouteForCapability(capability));
        var binding = evaluation.Binding;
        return evaluation.Enabled && binding is not null &&
               Same(row.CandidateChainId, binding.ChainId) &&
               Same(row.CandidatePromptId, binding.PromptId) &&
               row.CandidateStage == binding.Stage &&
               Same(row.CatalogVersion, binding.CatalogVersion) &&
               Same(row.CatalogRawSha256, binding.CatalogRawSha256) &&
               Same(row.CatalogSemanticSha256,
                   binding.CatalogSemanticSha256) &&
               Same(row.StageLockSha256, binding.StageLockSha256);
    }

    private static ImmutableArray<string> Ids(
        IReadOnlyList<string>? values,
        string name,
        bool allowEmpty)
    {
        if (values is null || values.Count > 4096 ||
            (!allowEmpty && values.Count == 0))
            throw Fail($"{name}_INVALID");
        var normalized = values.Select(value => ObjectIdText(value, name))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw Fail($"{name}_DUPLICATE");
        return normalized;
    }

    private static ImmutableArray<string> Values(
        IReadOnlyList<string>? values,
        string name)
    {
        if (values is null || values.Count > 10_000)
            throw Fail($"{name}_INVALID");
        var normalized = values.Select(value => Required(value, name))
            .ToImmutableArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw Fail($"{name}_DUPLICATE");
        return normalized;
    }

    private static string ObjectIdText(string? value, string name)
    {
        value = Required(value, name);
        if (!ObjectId.TryParse(value, out var parsed) ||
            !StringComparer.Ordinal.Equals(value, parsed.ToString()))
            throw Fail($"{name}_NON_CANONICAL_OBJECT_ID");
        return value;
    }

    private static bool Same(string? left, string? right)
        => StringComparer.Ordinal.Equals(left, right);

    private static string Required(
        string? value,
        string name,
        int maxLength = 1024)
        => StatisticReconciliationActualCanonical.Required(value, name, maxLength);

    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);

    private static int Positive(int? value, string name)
        => value is > 0 ? value.Value : throw Fail($"{name}_INVALID");

    private static int Positive(int value, string name)
        => value > 0 ? value : throw Fail($"{name}_INVALID");

    private static long Positive(long? value, string name)
        => value is > 0 ? value.Value : throw Fail($"{name}_INVALID");

    private static long Positive(long value, string name)
        => value > 0 ? value : throw Fail($"{name}_INVALID");

    private static long NonNegative(long value, string name)
        => value >= 0 ? value : throw Fail($"{name}_INVALID");

    private static DateTime Utc(DateTime? value, string name)
        => value is { Kind: DateTimeKind.Utc }
            ? value.Value
            : throw Fail($"{name}_INVALID");

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"ACTUAL_TRUSTED_MATERIAL_{reason}");
}
