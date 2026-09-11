using MongoDB.Driver;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed partial class StatisticReconciliationRunService
{
    private async Task<StatisticReconciliationRecheckCaptureBinding>
        ResolveRecheckCaptureBindingAsync(
            StatisticReconciliationRun run,
            WorkAssignment scope,
            MeResponse actor,
            CancellationToken ct)
    {
        var currentRun =
            StatisticReconciliationRecheckCaptureBindingCanonical
                .EffectiveCurrentRun(run);
        var p9Candidates = await _ctx.WorkReportStatisticRebuildJobs
            .Find(BuildP10CurrentP9PublicationFamilyFilter(
                Builders<WorkReportStatisticRebuildJob>.Filter,
                currentRun))
            .Limit(2)
            .ToListAsync(ct);
        if (p9Candidates.Count != 1)
            throw HiddenSourceFailure(actor,
                "RECHECK_P9_OWNER_CARDINALITY_INVALID");

        var selected = p9Candidates[0];
        if (selected.GenerationId == currentRun.P9GenerationId &&
            selected.GenerationHash == currentRun.P9GenerationHash)
            throw JobConflict("RECHECK_P9_SUCCESSOR_NOT_DISTINCT");

        // Reuse the complete P9 owner validation after the server has selected
        // the sole eligible id. No identifier from the recheck caller reaches
        // this path.
        var p9 = await LoadP9PublicationForRecheckAsync(
            new NormalizedCreate(
                "SERVER_RECHECK_BINDING",
                StatisticReconciliationP9ResultKinds.Direct,
                selected.Id,
                selected.Id,
                run.ConceptKey,
                run.Grain,
                run.FilterHash,
                run.CanonicalFilterJson,
                null),
            scope,
            currentRun,
            actor,
            ct);

        var oldPlan = currentRun.ActualCapturePlan ??
            throw JobConflict("RECHECK_BASE_CAPTURE_PLAN_REQUIRED");
        StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
            oldPlan, currentRun.ActualCapturePlanSha256);
        var exportFilterSha = oldPlan.SchemaVersion switch
        {
            StatisticReconciliationActualCapturePlanVersions.V3 or
            StatisticReconciliationActualCapturePlanVersions.V4 =>
                oldPlan.Export.FilterSha256!,
            StatisticReconciliationActualCapturePlanVersions.V1 or
            StatisticReconciliationActualCapturePlanVersions.V2 =>
                run.FilterHash,
            _ => throw JobConflict("RECHECK_CAPTURE_PLAN_VERSION_INVALID")
        };

        if (StatisticReconciliationActualCapturePlanIntegrity.IsV4(oldPlan))
            return await ResolveV4NotApplicableRecheckCaptureBindingAsync(
                currentRun,
                scope,
                actor,
                p9,
                oldPlan,
                exportFilterSha,
                ct);

        // The locked MATCHED Direct family does not advance independent
        // Basic/Advanced/Diff producers during a same-run recheck. Re-load the
        // exact base selector set and validate it is still clean; never select
        // a "current" summary owner and mix unrelated generations.
        var basic = await _ctx.WorkAssignmentBasicSummarySnapshots
            .Find(row =>
                row.Id == oldPlan.Basic.SnapshotId &&
                row.WorkId == scope.WorkId &&
                row.ScopeAssignmentId == scope.Id &&
                row.DynamicFormTemplateId == p9.DynamicFormTemplateId &&
                !row.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (basic is null ||
            !ValidBasicOwner(basic, oldPlan.Basic.Mode, p9))
            throw HiddenSourceFailure(actor,
                "RECHECK_BASE_BASIC_OWNER_INVALID");

        var daysTask = LoadExactRecheckAdvancedAsync(
            _ctx.WorkAssignmentAdvancedSummaryDayNodes,
            oldPlan.Advanced.DayNodeIds,
            ct);
        var monthsTask = LoadExactRecheckAdvancedAsync(
            _ctx.WorkAssignmentAdvancedSummaryMonthNodes,
            oldPlan.Advanced.MonthNodeIds,
            ct);
        var yearsTask = LoadExactRecheckAdvancedAsync(
            _ctx.WorkAssignmentAdvancedSummaryYearNodes,
            oldPlan.Advanced.YearNodeIds,
            ct);
        await Task.WhenAll(daysTask, monthsTask, yearsTask);
        var days = await daysTask;
        var months = await monthsTask;
        var years = await yearsTask;
        if (!ExactIds(days, oldPlan.Advanced.DayNodeIds) ||
            !ExactIds(months, oldPlan.Advanced.MonthNodeIds) ||
            !ExactIds(years, oldPlan.Advanced.YearNodeIds) ||
            days.Count == 0 ||
            !ValidAdvancedOwners(oldPlan.Advanced.SectionId,
                days, months, years, days[0], p9))
            throw HiddenSourceFailure(actor,
                "RECHECK_BASE_ADVANCED_OWNER_SET_INVALID");

        var diffCollection = _ctx.Db.GetCollection<WorkReportStatisticDiffResult>(
            "work_report_statistic_diff_results");
        var diffCandidates = await diffCollection.Find(row =>
                row.WorkId == scope.WorkId &&
                row.AssignmentId == scope.Id &&
                row.DynamicFormTemplateId == p9.DynamicFormTemplateId &&
                row.Status == "COMPLETED" && row.IsCurrent && row.IsFresh &&
                !row.IsDirty && !row.IsDeleted)
            .Limit(3)
            .ToListAsync(ct);
        var validDiffs = diffCandidates
            .Where(row => ValidDiffOwner(row, p9, run.ConceptKey))
            .Take(2)
            .ToArray();
        if (validDiffs.Length != 1)
            throw HiddenSourceFailure(actor,
                "RECHECK_SUCCESSOR_DIFF_OWNER_INVALID");
        var diff = validDiffs[0];
        var apiSurface = oldPlan.Api.Surface;
        var apiOwnerId = apiSurface switch
        {
            "DIRECT_FIELD" or "DIRECT_TABLE" or "DIRECT_LABEL" => p9.Id,
            "BASIC_SOURCE" => basic.Id,
            "P9_DIFF" => diff.Id,
            _ => throw HiddenSourceFailure(actor,
                "RECHECK_API_SURFACE_INVALID")
        };
        var apiPagePlan = await ResolveActualApiPagePlanAsync(
            apiSurface,
            run.CanonicalFilterJson ?? string.Empty,
            run.FilterHash,
            p9,
            scope,
            basic,
            diff,
            actor,
            ct);

        var exportCandidates = await LoadRecheckExportsAsync(
            p9, scope, basic, days, months, years, diff,
            oldPlan.Export.ResultKind,
            oldPlan.Export.ResultId,
            exportFilterSha,
            ct);
        if (exportCandidates.Count != 1)
            throw HiddenSourceFailure(actor,
                "RECHECK_EXPORT_OWNER_CARDINALITY_INVALID");
        var export = exportCandidates[0];

        string advancedSha;
        try
        {
            advancedSha = StatisticReconciliationActualCapturePlanIntegrity
                .AdvancedSelectorSha(oldPlan.Advanced.SectionId,
                    days, months, years);
        }
        catch (InvalidOperationException)
        {
            throw HiddenSourceFailure(actor,
                "RECHECK_ADVANCED_OWNER_SET_INVALID");
        }

        var plan = new StatisticReconciliationActualCapturePlan
        {
            SchemaVersion = oldPlan.SchemaVersion,
            BoundaryRegistryVersion = oldPlan.BoundaryRegistryVersion,
            ActualConfigurationBundleSha256 =
                StatisticReconciliationActualCapturePlanIntegrity
                    .ConfigurationBundleSha(BuildP8ConfigBundleHash(p9), p9,
                        basic, days, months, years, diff),
            Basic = new()
            {
                SnapshotId = basic.Id,
                Mode = oldPlan.Basic.Mode,
                ImmutableSelectorSha256 =
                    StatisticReconciliationActualCapturePlanIntegrity
                        .BasicSelectorSha(basic)
            },
            Advanced = new()
            {
                SectionId = oldPlan.Advanced.SectionId,
                DayNodeIds = days.Select(row => row.Id)
                    .OrderBy(id => id, StringComparer.Ordinal).ToList(),
                MonthNodeIds = months.Select(row => row.Id)
                    .OrderBy(id => id, StringComparer.Ordinal).ToList(),
                YearNodeIds = years.Select(row => row.Id)
                    .OrderBy(id => id, StringComparer.Ordinal).ToList(),
                ImmutableSelectorSha256 = advancedSha
            },
            Diff = new()
            {
                ResultId = diff.Id,
                RunId = diff.RunId,
                ImmutableSelectorSha256 =
                    StatisticReconciliationActualCapturePlanIntegrity
                        .DiffSelectorSha(diff)
            },
            Api = new()
            {
                Surface = apiSurface,
                OwnerResultId = apiOwnerId,
                ExpectedTotalRows = apiPagePlan.ExpectedTotalRows,
                PageSize = apiPagePlan.PageSize,
                PageCount = apiPagePlan.PageCount
            },
            Export = new()
            {
                ExportId = export.Id,
                ResultKind = export.ResultKind,
                WorkId = export.WorkId,
                ScopeType = export.ScopeType,
                ScopeId = export.ScopeId,
                ResultId = export.ResultId,
                FilterSha256 = oldPlan.SchemaVersion ==
                    StatisticReconciliationActualCapturePlanVersions.V3
                        ? export.FilterHash
                        : null,
                RequestSha256 = export.RequestHash,
                AuthorizationSnapshotSha256 = export.AuthorizationSnapshotHash,
                ContentSha256 = export.ContentHash,
                ColumnManifestSha256 = export.ColumnManifestSha256!,
                OwnerSemanticSha256 = export.SemanticHash
            }
        };
        plan.PlanSha256 =
            StatisticReconciliationActualCapturePlanIntegrity.PlanSha(plan);
        StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
            plan, plan.PlanSha256);

        var binding = new StatisticReconciliationRecheckCaptureBinding
        {
            P9ResultId = p9.Id,
            P9RunId = p9.Id,
            P9GenerationId = p9.GenerationId!,
            P9GenerationHash = p9.GenerationHash!,
            P9RunKind = p9.RunKind!,
            P9CapabilityId = p9.CapabilityId!,
            P9RouteId = p9.RouteId!,
            P9CandidateChainId = p9.CandidateChainId!,
            P9CandidatePromptId = p9.CandidatePromptId!,
            SourceReportId = p9.SourceReportId!,
            SourcePayloadRevision = p9.SourcePayloadRevision!.Value,
            SourcePayloadHash = p9.SourcePayloadHash!,
            SourceLifecycleRevision = p9.SourceLifecycleRevision!.Value,
            SourceLifecycleEventKey = p9.SourceLifecycleEventKey,
            SourceLifecycleHash = BuildSourceLifecycleHash(p9),
            SourceLifecycleStatus = p9.SourceStatus ?? string.Empty,
            DynamicFormFamilyId = p9.DynamicFormFamilyId,
            DynamicFormVersionId = p9.DynamicFormTemplateId,
            DynamicFormVersionNo = p9.DynamicFormVersionNo,
            DynamicFormSchemaHash = p9.DynamicFormSchemaHash,
            FlowTemplateId = p9.FlowTemplateId,
            FlowFamilyRevision = p9.FlowFamilyRevision,
            FlowTemplateVersionId = p9.FlowTemplateVersionId,
            FlowPayloadHash = p9.FlowPayloadHash,
            FlowInstanceId = p9.FlowInstanceId,
            FlowInstanceRevision = p9.FlowInstanceRevision,
            FlowExecutionEpoch = p9.FlowExecutionEpoch,
            FlowExecutionEpochId = p9.FlowExecutionEpochId,
            FlowExecutionEpochRevision = p9.FlowExecutionEpochRevision,
            FlowStepId = p9.FlowStepId,
            FlowBranchId = p9.FlowBranchId,
            FlowStepInstanceId = p9.FlowStepInstanceId,
            FlowStepInstanceRevision = p9.FlowStepInstanceRevision,
            FlowContributionPolicy = p9.FlowContributionPolicy,
            FlowContributionPolicyHash = p9.FlowContributionPolicyHash,
            FlowEffectiveStatus = p9.FlowEffectiveStatus,
            FlowContributionProvenanceHash =
                DeriveContributionProvenanceHash(p9),
            P8ConfigOwnerId = p9.DynamicFormTemplateId,
            P8ConfigId = p9.ConfigId,
            P8ConfigVersionId = p9.ConfigVersionId,
            P8ConfigVersionNo = p9.ConfigVersionNo,
            P8ConfigRevision = p9.ConfigRevision,
            P8ConfigHash = p9.ConfigHash,
            P8ConfigBundleHash = BuildP8ConfigBundleHash(p9),
            P9CatalogVersion = p9.CatalogVersion!,
            P9CatalogRawSha256 = p9.CatalogRawSha256!,
            P9CatalogSemanticSha256 = p9.CatalogSemanticSha256!,
            P9SchemaRawSha256 = p9.SchemaRawSha256!,
            P9SchemaSemanticSha256 = p9.SchemaSemanticSha256!,
            P9StageLockSha256 = p9.StageLockSha256!,
            PeriodKey = p9.PeriodKey!,
            PeriodInstanceKey = p9.PeriodInstanceKey!,
            PeriodKind = p9.PeriodKind!,
            PeriodStartUtc = NormalizeUtc(p9.PeriodStartUtc),
            PeriodEndUtc = NormalizeUtc(p9.PeriodEndUtc),
            TimeAxis = DeriveTimeAxis(p9, run.Grain),
            ActualCapturePlan = plan,
            ActualCapturePlanSha256 = plan.PlanSha256,
            ActualConfigurationBundleSha256 =
                plan.ActualConfigurationBundleSha256
        };
        StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(binding);
        StatisticReconciliationRecheckCaptureBindingCanonical.RequireValid(binding);
        return binding;
    }

    internal static FilterDefinition<WorkReportStatisticRebuildJob>
        BuildP10CurrentP9PublicationFamilyFilter(
            FilterDefinitionBuilder<WorkReportStatisticRebuildJob> filter,
            StatisticReconciliationRun run)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(run);
        return filter.Eq(
                   job => job.RunKind,
                   WorkReportStatisticRebuildJobRunKinds
                       .LifecycleDirectProjection) &
               filter.Eq(job => job.WorkId, run.WorkId) &
               filter.Eq(
                   job => job.Status,
                   WorkReportStatisticRebuildJobStatuses.Completed) &
               filter.Eq(job => job.IsCurrentPublication, true) &
               filter.Eq(job => job.IsActive, false) &
               filter.Eq(job => job.IsDeleted, false) &
               filter.Eq(
                   job => job.PeriodInstanceKey,
                   run.PeriodInstanceKey) &
               filter.Eq(job => job.PeriodKind, run.PeriodKind) &
               filter.Eq(
                   job => job.DynamicFormFamilyId,
                   run.DynamicFormFamilyId) &
               filter.Eq(
                   job => job.DynamicFormTemplateId,
                   run.DynamicFormVersionId) &
               filter.Eq(
                   job => job.DynamicFormVersionNo,
                   run.DynamicFormVersionNo) &
               filter.Eq(
                   job => job.DynamicFormSchemaHash,
                   run.DynamicFormSchemaHash) &
               filter.Eq(
                   job => job.CandidateChainId,
                   run.P9CandidateChainId) &
               filter.Eq(
                   job => job.CandidatePromptId,
                   run.P9CandidatePromptId);
    }

    private async Task<StatisticReconciliationRecheckCaptureBinding>
        ResolveV4NotApplicableRecheckCaptureBindingAsync(
            StatisticReconciliationRun run,
            WorkAssignment scope,
            MeResponse actor,
            WorkReportStatisticRebuildJob p9,
            StatisticReconciliationActualCapturePlan oldPlan,
            string exportFilterSha,
            CancellationToken ct)
    {
        var p8OwnerId = p9.DynamicFormTemplateId;
        var p8BundleSha256 = BuildP8ConfigBundleHash(p9);
        if (!StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                oldPlan.Basic.Disposition) ||
            !StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                oldPlan.Advanced.Disposition) ||
            !StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                oldPlan.Diff.Disposition) ||
            !StringComparer.Ordinal.Equals(
                oldPlan.P8ConfigurationOwnerId,
                run.P8ConfigOwnerId) ||
            !StringComparer.Ordinal.Equals(
                oldPlan.P8ConfigurationBundleSha256,
                run.P8ConfigBundleHash) ||
            !ValidNotApplicablePeriod(p9) ||
            oldPlan.Api.Surface is not (
                "DIRECT_FIELD" or "DIRECT_TABLE" or "DIRECT_LABEL"))
        {
            throw HiddenSourceFailure(
                actor,
                "RECHECK_NOT_APPLICABLE_BINDING_INVALID");
        }

        var emptyDays =
            Array.Empty<WorkAssignmentAdvancedSummaryDayNode>();
        var emptyMonths =
            Array.Empty<WorkAssignmentAdvancedSummaryMonthNode>();
        var emptyYears =
            Array.Empty<WorkAssignmentAdvancedSummaryYearNode>();
        var apiPagePlan = await ResolveActualApiPagePlanAsync(
            oldPlan.Api.Surface,
            run.CanonicalFilterJson ?? string.Empty,
            run.FilterHash,
            p9,
            scope,
            null!,
            null!,
            actor,
            ct);
        var exportCandidates = await LoadRecheckExportsAsync(
            p9,
            scope,
            null,
            emptyDays,
            emptyMonths,
            emptyYears,
            null,
            oldPlan.Export.ResultKind,
            oldPlan.Export.ResultId,
            exportFilterSha,
            ct);
        if (exportCandidates.Count != 1)
            throw HiddenSourceFailure(
                actor,
                "RECHECK_EXPORT_OWNER_CARDINALITY_INVALID");
        var export = exportCandidates[0];
        var notApplicable =
            StatisticReconciliationActualSummaryTargetDispositions.NotApplicable;
        var plan = new StatisticReconciliationActualCapturePlan
        {
            SchemaVersion = StatisticReconciliationActualCapturePlanVersions.V4,
            BoundaryRegistryVersion = oldPlan.BoundaryRegistryVersion,
            P8ConfigurationOwnerId = p8OwnerId,
            P8ConfigurationBundleSha256 = p8BundleSha256,
            Basic = new StatisticReconciliationActualBasicTargetPlan
            {
                Disposition = notApplicable,
                SnapshotId = string.Empty,
                Mode = string.Empty,
                ImmutableSelectorSha256 = null!,
                ApplicabilityProofSha256 =
                    StatisticReconciliationActualCapturePlanIntegrity
                        .ApplicabilityProofSha(
                            StatisticReconciliationActualCapturePlanIntegrity.BasicFamily,
                            notApplicable,
                            p8OwnerId,
                            p8BundleSha256,
                            null)
            },
            Advanced = new StatisticReconciliationActualAdvancedTargetPlan
            {
                Disposition = notApplicable,
                SectionId = string.Empty,
                DayNodeIds = [],
                MonthNodeIds = [],
                YearNodeIds = [],
                ImmutableSelectorSha256 = null!,
                ApplicabilityProofSha256 =
                    StatisticReconciliationActualCapturePlanIntegrity
                        .ApplicabilityProofSha(
                            StatisticReconciliationActualCapturePlanIntegrity.AdvancedFamily,
                            notApplicable,
                            p8OwnerId,
                            p8BundleSha256,
                            null)
            },
            Diff = new StatisticReconciliationActualDiffTargetPlan
            {
                Disposition = notApplicable,
                ResultId = string.Empty,
                RunId = string.Empty,
                ImmutableSelectorSha256 = null!,
                ApplicabilityProofSha256 =
                    StatisticReconciliationActualCapturePlanIntegrity
                        .ApplicabilityProofSha(
                            StatisticReconciliationActualCapturePlanIntegrity.DiffFamily,
                            notApplicable,
                            p8OwnerId,
                            p8BundleSha256,
                            null)
            },
            Api = new StatisticReconciliationActualApiTargetPlan
            {
                Surface = oldPlan.Api.Surface,
                OwnerResultId = p9.Id,
                ExpectedTotalRows = apiPagePlan.ExpectedTotalRows,
                PageSize = apiPagePlan.PageSize,
                PageCount = apiPagePlan.PageCount
            },
            Export = new StatisticReconciliationActualExportTargetPlan
            {
                ExportId = export.Id,
                ResultKind = export.ResultKind,
                WorkId = export.WorkId,
                ScopeType = export.ScopeType,
                ScopeId = export.ScopeId,
                ResultId = export.ResultId,
                FilterSha256 = export.FilterHash,
                RequestSha256 = export.RequestHash,
                AuthorizationSnapshotSha256 =
                    export.AuthorizationSnapshotHash,
                ContentSha256 = export.ContentHash,
                ColumnManifestSha256 = export.ColumnManifestSha256!,
                OwnerSemanticSha256 = export.SemanticHash
            }
        };
        plan.ActualConfigurationBundleSha256 =
            StatisticReconciliationActualCapturePlanIntegrity
                .V4ConfigurationBundleSha(plan);
        plan.PlanSha256 =
            StatisticReconciliationActualCapturePlanIntegrity.PlanSha(plan);
        StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
            plan,
            plan.PlanSha256);

        var binding = new StatisticReconciliationRecheckCaptureBinding
        {
            P9ResultId = p9.Id,
            P9RunId = p9.Id,
            P9GenerationId = p9.GenerationId!,
            P9GenerationHash = p9.GenerationHash!,
            P9RunKind = p9.RunKind!,
            P9CapabilityId = p9.CapabilityId!,
            P9RouteId = p9.RouteId!,
            P9CandidateChainId = p9.CandidateChainId!,
            P9CandidatePromptId = p9.CandidatePromptId!,
            SourceReportId = p9.SourceReportId!,
            SourcePayloadRevision = p9.SourcePayloadRevision!.Value,
            SourcePayloadHash = p9.SourcePayloadHash!,
            SourceLifecycleRevision = p9.SourceLifecycleRevision!.Value,
            SourceLifecycleEventKey = p9.SourceLifecycleEventKey,
            SourceLifecycleHash = BuildSourceLifecycleHash(p9),
            SourceLifecycleStatus = p9.SourceStatus ?? string.Empty,
            DynamicFormFamilyId = p9.DynamicFormFamilyId,
            DynamicFormVersionId = p9.DynamicFormTemplateId,
            DynamicFormVersionNo = p9.DynamicFormVersionNo,
            DynamicFormSchemaHash = p9.DynamicFormSchemaHash,
            FlowTemplateId = p9.FlowTemplateId,
            FlowFamilyRevision = p9.FlowFamilyRevision,
            FlowTemplateVersionId = p9.FlowTemplateVersionId,
            FlowPayloadHash = p9.FlowPayloadHash,
            FlowInstanceId = p9.FlowInstanceId,
            FlowInstanceRevision = p9.FlowInstanceRevision,
            FlowExecutionEpoch = p9.FlowExecutionEpoch,
            FlowExecutionEpochId = p9.FlowExecutionEpochId,
            FlowExecutionEpochRevision = p9.FlowExecutionEpochRevision,
            FlowStepId = p9.FlowStepId,
            FlowBranchId = p9.FlowBranchId,
            FlowStepInstanceId = p9.FlowStepInstanceId,
            FlowStepInstanceRevision = p9.FlowStepInstanceRevision,
            FlowContributionPolicy = p9.FlowContributionPolicy,
            FlowContributionPolicyHash = p9.FlowContributionPolicyHash,
            FlowEffectiveStatus = p9.FlowEffectiveStatus,
            FlowContributionProvenanceHash =
                DeriveContributionProvenanceHash(p9),
            P8ConfigOwnerId = p9.DynamicFormTemplateId,
            P8ConfigId = p9.ConfigId,
            P8ConfigVersionId = p9.ConfigVersionId,
            P8ConfigVersionNo = p9.ConfigVersionNo,
            P8ConfigRevision = p9.ConfigRevision,
            P8ConfigHash = p9.ConfigHash,
            P8ConfigBundleHash = p8BundleSha256,
            P9CatalogVersion = p9.CatalogVersion!,
            P9CatalogRawSha256 = p9.CatalogRawSha256!,
            P9CatalogSemanticSha256 = p9.CatalogSemanticSha256!,
            P9SchemaRawSha256 = p9.SchemaRawSha256!,
            P9SchemaSemanticSha256 = p9.SchemaSemanticSha256!,
            P9StageLockSha256 = p9.StageLockSha256!,
            PeriodKey = p9.PeriodKey!,
            PeriodInstanceKey = p9.PeriodInstanceKey!,
            PeriodKind = p9.PeriodKind!,
            PeriodStartUtc = NormalizeUtc(p9.PeriodStartUtc),
            PeriodEndUtc = NormalizeUtc(p9.PeriodEndUtc),
            TimeAxis = DeriveTimeAxis(p9, run.Grain),
            ActualCapturePlan = plan,
            ActualCapturePlanSha256 = plan.PlanSha256,
            ActualConfigurationBundleSha256 =
                plan.ActualConfigurationBundleSha256
        };
        StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(binding);
        StatisticReconciliationRecheckCaptureBindingCanonical.RequireValid(
            binding);
        return binding;
    }
    private static async Task<List<TNode>>
        LoadExactRecheckAdvancedAsync<TNode>(
            IMongoCollection<TNode> collection,
            IReadOnlyList<string> ids,
            CancellationToken ct)
        where TNode : WorkAssignmentAdvancedSummaryHierarchyNodeBase
    {
        if (ids.Count == 0 || ids.Count > 4096 ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw JobConflict("RECHECK_ADVANCED_OWNER_BOUNDS_EXCEEDED");
        return await collection.Find(row =>
                ids.Contains(row.Id) && !row.IsDeleted)
            .Limit(ids.Count + 1)
            .ToListAsync(ct);
    }
    private async Task<List<StatRunExportArtifact>> LoadRecheckExportsAsync(
        WorkReportStatisticRebuildJob p9,
        WorkAssignment scope,
        WorkAssignmentBasicSummarySnapshot? basic,
        IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode> days,
        IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode> months,
        IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode> years,
        WorkReportStatisticDiffResult? diff,
        string resultKind,
        string priorResultId,
        string filterHash,
        CancellationToken ct)
    {
        var resultIds = resultKind switch
        {
            StatRunExportResultKinds.DirectField or
            StatRunExportResultKinds.DirectTable or
            StatRunExportResultKinds.DirectLabel =>
                new[] { p9.Id, p9.GenerationId! },
            StatRunExportResultKinds.Basic or
            StatRunExportResultKinds.Flow =>
                basic is null ? Array.Empty<string>() : new[] { basic.Id },
            StatRunExportResultKinds.Advanced =>
                new[] { priorResultId },
            StatRunExportResultKinds.Diff =>
                diff is null ? Array.Empty<string>() : new[] { diff.Id },
            _ => Array.Empty<string>()
        };
        if (resultIds.Length == 0 ||
            resultIds.Any(string.IsNullOrWhiteSpace) ||
            (resultKind == StatRunExportResultKinds.Advanced &&
             !days.Cast<WorkAssignmentAdvancedSummaryHierarchyNodeBase>()
                 .Concat(months)
                 .Concat(years)
                 .Any(row => string.Equals(
                     row.Id, priorResultId, StringComparison.Ordinal))))
        {
            return new List<StatRunExportArtifact>();
        }

        var regularTask = _ctx.WorkReportStatisticExports.Find(row =>
                row.WorkId == scope.WorkId &&
                row.ScopeType == "ASSIGNMENT" &&
                row.ScopeId == scope.Id &&
                row.ResultKind == resultKind &&
                resultIds.Contains(row.ResultId) &&
                row.FilterHash == filterHash && !row.IsDeleted)
            .Limit(4).ToListAsync(ct);
        var diffTask = _ctx.WorkReportStatisticDiffExports.Find(row =>
                row.WorkId == scope.WorkId &&
                row.ScopeType == "ASSIGNMENT" &&
                row.ScopeId == scope.Id &&
                row.ResultKind == resultKind &&
                resultIds.Contains(row.ResultId) &&
                row.FilterHash == filterHash && !row.IsDeleted)
            .Limit(4).ToListAsync(ct);
        await Task.WhenAll(regularTask, diffTask);
        return (await regularTask).Concat(await diffTask)
            .Where(row => ValidExportOwner(
                row, p9, scope, basic, days, months, years, diff))
            .Take(2)
            .ToList();
    }
}
