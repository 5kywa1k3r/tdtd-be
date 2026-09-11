using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed partial class StatisticReconciliationRunService
{
    private async Task<StatisticReconciliationActualCapturePlan?>
        ResolveActualCapturePlanAsync(
            NormalizedActualCapturePlanRequest? request,
            WorkReportStatisticRebuildJob p9,
            WorkAssignment scope,
            MeResponse actor,
            string conceptKey,
            string filterHash,
            string canonicalFilterJson,
            CancellationToken ct)
    {
        if (request is null)
            return null;

        var anyNotApplicable =
            StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                request.BasicDisposition) ||
            StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                request.AdvancedDisposition) ||
            StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                request.DiffDisposition);
        if (anyNotApplicable)
        {
            if (!StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                    request.BasicDisposition) ||
                !StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                    request.AdvancedDisposition) ||
                !StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                    request.DiffDisposition))
            {
                throw HiddenSourceFailure(
                    actor,
                    "ACTUAL_SUMMARY_MIXED_APPLICABILITY_UNSUPPORTED");
            }
            return await ResolveNotApplicableCapturePlanAsync(
                request,
                p9,
                scope,
                actor,
                filterHash,
                canonicalFilterJson,
                ct);
        }
        if (!StatisticReconciliationActualCapturePlanIntegrity.IsConfigured(
                request.BasicDisposition) ||
            !StatisticReconciliationActualCapturePlanIntegrity.IsConfigured(
                request.AdvancedDisposition) ||
            !StatisticReconciliationActualCapturePlanIntegrity.IsConfigured(
                request.DiffDisposition))
        {
            throw HiddenSourceFailure(actor, "ACTUAL_SUMMARY_DISPOSITION_INVALID");
        }

        var basic = await _ctx.WorkAssignmentBasicSummarySnapshots
            .Find(row => row.Id == request.BasicSnapshotId &&
                         row.WorkId == scope.WorkId &&
                         row.ScopeAssignmentId == scope.Id &&
                         row.DynamicFormTemplateId == p9.DynamicFormTemplateId &&
                         !row.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (basic is null || !ValidBasicOwner(basic, request.BasicMode, p9))
            throw HiddenSourceFailure(actor, "ACTUAL_BASIC_TARGET_INVALID");

        var advancedSeed = await _ctx.WorkAssignmentAdvancedSummaryDayNodes
            .Find(row => row.Id == request.AdvancedDayNodeIds[0] &&
                         row.WorkId == scope.WorkId &&
                         row.AssignmentId == scope.Id &&
                         row.DynamicFormTemplateId == p9.DynamicFormTemplateId &&
                         row.SectionId == request.AdvancedSectionId &&
                         !row.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (advancedSeed is null ||
            !ValidAdvancedOwner(advancedSeed, "DAY"))
        {
            throw HiddenSourceFailure(actor, "ACTUAL_ADVANCED_TARGET_INVALID");
        }

        var daysTask = LoadAdvancedOwnersAsync(
            _ctx.WorkAssignmentAdvancedSummaryDayNodes,
            request.AdvancedSectionId,
            scope,
            p9,
            advancedSeed,
            ct);
        var monthsTask = LoadAdvancedOwnersAsync(
            _ctx.WorkAssignmentAdvancedSummaryMonthNodes,
            request.AdvancedSectionId,
            scope,
            p9,
            advancedSeed,
            ct);
        var yearsTask = LoadAdvancedOwnersAsync(
            _ctx.WorkAssignmentAdvancedSummaryYearNodes,
            request.AdvancedSectionId,
            scope,
            p9,
            advancedSeed,
            ct);
        await Task.WhenAll(daysTask, monthsTask, yearsTask);
        var days = await daysTask;
        var months = await monthsTask;
        var years = await yearsTask;
        if (!ExactIds(days, request.AdvancedDayNodeIds) ||
            !ExactIds(months, request.AdvancedMonthNodeIds) ||
            !ExactIds(years, request.AdvancedYearNodeIds) ||
            !ValidAdvancedOwners(
                request.AdvancedSectionId,
                days,
                months,
                years,
                advancedSeed,
                p9))
        {
            throw HiddenSourceFailure(actor, "ACTUAL_ADVANCED_TARGET_INVALID");
        }

        var diffCollection = _ctx.Db.GetCollection<WorkReportStatisticDiffResult>(
            "work_report_statistic_diff_results");
        var diff = await diffCollection
            .Find(row => row.Id == request.DiffResultId &&
                         row.RunId == request.DiffRunId &&
                         row.WorkId == scope.WorkId &&
                         row.AssignmentId == scope.Id &&
                         row.DynamicFormTemplateId == p9.DynamicFormTemplateId &&
                         !row.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (diff is null || !ValidDiffOwner(diff, p9, conceptKey))
            throw HiddenSourceFailure(actor, "ACTUAL_DIFF_TARGET_INVALID");

        var expectedApiOwner = request.ApiSurface switch
        {
            "DIRECT_FIELD" or "DIRECT_TABLE" or "DIRECT_LABEL" => p9.Id,
            "BASIC_SOURCE" => basic.Id,
            "P9_DIFF" => diff.Id,
            _ => null
        };
        if (!string.Equals(
                request.ApiOwnerResultId,
                expectedApiOwner,
                StringComparison.Ordinal))
        {
            throw HiddenSourceFailure(actor, "ACTUAL_API_TARGET_INVALID");
        }

        var export = await LoadExactExportAsync(request.ExportId, ct);
        if (export is null || !ValidExportOwner(
                export, p9, scope, basic, days, months, years, diff))
            throw HiddenSourceFailure(actor, "ACTUAL_EXPORT_TARGET_INVALID");

        var apiPagePlan = await ResolveActualApiPagePlanAsync(
            request.ApiSurface,
            canonicalFilterJson,
            filterHash,
            p9,
            scope,
            basic,
            diff,
            actor,
            ct);

        string advancedSha;
        try
        {
            advancedSha =
                StatisticReconciliationActualCapturePlanIntegrity
                    .AdvancedSelectorSha(
                        request.AdvancedSectionId,
                        days,
                        months,
                        years);
        }
        catch (InvalidOperationException)
        {
            throw HiddenSourceFailure(actor, "ACTUAL_ADVANCED_TARGET_INVALID");
        }

        var plan = new StatisticReconciliationActualCapturePlan
        {
            SchemaVersion = StatisticReconciliationActualCapturePlanVersions.V3,
            BoundaryRegistryVersion = request.BoundaryRegistryVersion,
            ActualConfigurationBundleSha256 =
                StatisticReconciliationActualCapturePlanIntegrity
                    .ConfigurationBundleSha(
                        BuildP8ConfigBundleHash(p9),
                        p9,
                        basic,
                        days,
                        months,
                        years,
                        diff),
            Basic = new StatisticReconciliationActualBasicTargetPlan
            {
                SnapshotId = basic.Id,
                Mode = request.BasicMode,
                ImmutableSelectorSha256 =
                    StatisticReconciliationActualCapturePlanIntegrity
                        .BasicSelectorSha(basic)
            },
            Advanced = new StatisticReconciliationActualAdvancedTargetPlan
            {
                SectionId = request.AdvancedSectionId,
                DayNodeIds = request.AdvancedDayNodeIds.ToList(),
                MonthNodeIds = request.AdvancedMonthNodeIds.ToList(),
                YearNodeIds = request.AdvancedYearNodeIds.ToList(),
                ImmutableSelectorSha256 = advancedSha
            },
            Diff = new StatisticReconciliationActualDiffTargetPlan
            {
                ResultId = diff.Id,
                RunId = diff.RunId,
                ImmutableSelectorSha256 =
                    StatisticReconciliationActualCapturePlanIntegrity
                        .DiffSelectorSha(diff)
            },
            Api = new StatisticReconciliationActualApiTargetPlan
            {
                Surface = request.ApiSurface,
                OwnerResultId = request.ApiOwnerResultId,
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
                AuthorizationSnapshotSha256 = export.AuthorizationSnapshotHash,
                ContentSha256 = export.ContentHash,
                ColumnManifestSha256 = export.ColumnManifestSha256!,
                OwnerSemanticSha256 = export.SemanticHash
            }
        };
        plan.PlanSha256 =
            StatisticReconciliationActualCapturePlanIntegrity.PlanSha(plan);
        StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
            plan,
            plan.PlanSha256);
        return plan;
    }

    private async Task<StatisticReconciliationActualCapturePlan>
        ResolveNotApplicableCapturePlanAsync(
            NormalizedActualCapturePlanRequest request,
            WorkReportStatisticRebuildJob p9,
            WorkAssignment scope,
            MeResponse actor,
            string filterHash,
            string canonicalFilterJson,
            CancellationToken ct)
    {
        if (!ValidNotApplicablePeriod(p9) ||
            request.ApiSurface is not (
                "DIRECT_FIELD" or "DIRECT_TABLE" or "DIRECT_LABEL") ||
            !string.Equals(
                request.ApiOwnerResultId,
                p9.Id,
                StringComparison.Ordinal))
        {
            throw HiddenSourceFailure(actor, "ACTUAL_NOT_APPLICABLE_TARGET_INVALID");
        }

        var export = await LoadExactExportAsync(request.ExportId, ct);
        if (export is null || !ValidExportOwner(
                export,
                p9,
                scope,
                null,
                Array.Empty<WorkAssignmentAdvancedSummaryDayNode>(),
                Array.Empty<WorkAssignmentAdvancedSummaryMonthNode>(),
                Array.Empty<WorkAssignmentAdvancedSummaryYearNode>(),
                null))
        {
            throw HiddenSourceFailure(actor, "ACTUAL_EXPORT_TARGET_INVALID");
        }

        var apiPagePlan = await ResolveActualApiPagePlanAsync(
            request.ApiSurface,
            canonicalFilterJson,
            filterHash,
            p9,
            scope,
            null!,
            null!,
            actor,
            ct);
        var p8OwnerId = p9.DynamicFormTemplateId;
        var p8BundleSha256 = BuildP8ConfigBundleHash(p9);
        var notApplicable =
            StatisticReconciliationActualSummaryTargetDispositions.NotApplicable;
        var plan = new StatisticReconciliationActualCapturePlan
        {
            SchemaVersion = StatisticReconciliationActualCapturePlanVersions.V4,
            BoundaryRegistryVersion = request.BoundaryRegistryVersion,
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
                Surface = request.ApiSurface,
                OwnerResultId = request.ApiOwnerResultId,
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
                AuthorizationSnapshotSha256 = export.AuthorizationSnapshotHash,
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
        return plan;
    }

    private static bool ValidNotApplicablePeriod(
        WorkReportStatisticRebuildJob p9)
    {
        var start = p9.PeriodStartUtc;
        var end = p9.PeriodEndUtc;
        if (!start.HasValue || !end.HasValue)
            return !start.HasValue && !end.HasValue;
        return start.Value.Kind == DateTimeKind.Utc &&
               end.Value.Kind == DateTimeKind.Utc &&
               end.Value >= start.Value;
    }
    private async Task<List<TNode>> LoadAdvancedOwnersAsync<TNode>(
        IMongoCollection<TNode> collection,
        string sectionId,
        WorkAssignment scope,
        WorkReportStatisticRebuildJob p9,
        WorkAssignmentAdvancedSummaryHierarchyNodeBase boundary,
        CancellationToken ct)
        where TNode : WorkAssignmentAdvancedSummaryHierarchyNodeBase
    {
        var periodStart = NormalizeUtc(p9.PeriodStartUtc)!.Value;
        var periodEnd = NormalizeUtc(p9.PeriodEndUtc)!.Value;
        var fb = Builders<TNode>.Filter;
        return await collection.Find(
                fb.Eq(row => row.WorkId, scope.WorkId) &
                fb.Eq(row => row.AssignmentId, scope.Id) &
                fb.Eq(row => row.DynamicFormTemplateId, p9.DynamicFormTemplateId) &
                fb.Eq(row => row.SectionId, sectionId) &
                fb.Eq(row => row.ConfigId, boundary.ConfigId) &
                fb.Eq(row => row.ConfigVersionId, boundary.ConfigVersionId) &
                fb.Eq(row => row.ConfigVersionNo, boundary.ConfigVersionNo) &
                fb.Eq(row => row.ConfigRevision, boundary.ConfigRevision) &
                fb.Eq(row => row.ConfigHash, boundary.ConfigHash) &
                fb.Eq(row => row.DependencyPins, boundary.DependencyPins) &
                fb.Eq(row => row.TimeAxis, boundary.TimeAxis) &
                fb.Eq(row => row.CandidateChainId, boundary.CandidateChainId) &
                fb.Eq(row => row.CandidatePromptId, boundary.CandidatePromptId) &
                fb.Eq(row => row.CandidateStage, boundary.CandidateStage) &
                fb.Eq(row => row.CandidateCatalogRawSha256,
                    boundary.CandidateCatalogRawSha256) &
                fb.Eq(row => row.CandidateCatalogSemanticSha256,
                    boundary.CandidateCatalogSemanticSha256) &
                fb.Eq(row => row.CandidateStageLockSha256,
                    boundary.CandidateStageLockSha256) &
                fb.Eq(row => row.IsDeleted, false) &
                fb.Lte(row => row.WindowStartUtc, periodEnd) &
                fb.Gt(row => row.WindowEndExclusiveUtc, periodStart))
            .Limit(4097)
            .ToListAsync(ct);
    }

    private async Task<StatRunExportArtifact?> LoadExactExportAsync(
        string exportId,
        CancellationToken ct)
    {
        var regularTask = _ctx.WorkReportStatisticExports
            .Find(row => row.Id == exportId && !row.IsDeleted)
            .Limit(2)
            .ToListAsync(ct);
        var diffTask = _ctx.WorkReportStatisticDiffExports
            .Find(row => row.Id == exportId && !row.IsDeleted)
            .Limit(2)
            .ToListAsync(ct);
        await Task.WhenAll(regularTask, diffTask);
        var matches = (await regularTask).Concat(await diffTask).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool ValidBasicOwner(
        WorkAssignmentBasicSummarySnapshot row,
        string mode,
        WorkReportStatisticRebuildJob p9)
        => string.Equals(row.SourceScopeMode, mode, StringComparison.Ordinal) &&
           string.Equals(
               row.DynamicFormTemplateId,
               p9.DynamicFormTemplateId,
               StringComparison.Ordinal) &&
           ValidOwnerConfig(
               row.ConfigId,
               row.ConfigVersionId,
               row.ConfigVersionNo,
               row.ConfigRevision,
               row.ConfigHash) &&
           ValidBasicRuntimeScope(row, mode, p9) &&
           StatisticReconciliationCanonicalJson.IsCanonicalSha256(row.RequestHash) &&
           !row.SnapshotDirty &&
           string.Equals(row.RefreshStatus, "DONE", StringComparison.Ordinal) &&
           row.SnapshotRefreshedAtUtc is not null &&
           StatisticReconciliationCanonicalJson.IsCanonicalSha256(
               row.SourceSignatureHash) &&
           CanonicalObjectIdList(row.SourceAssignmentIds) &&
           CanonicalObjectIdList(row.SourceReportIds) &&
           ValidBasicPeriod(row, p9) &&
           ValidPins(row.ConfigDependencyPins, ordered: false) &&
           ValidCandidate(
               row.CandidateChainId,
               row.CandidatePromptId,
               row.CandidateStage,
               row.CandidateCatalogRawSha256,
               row.CandidateCatalogSemanticSha256,
               row.CandidateStageLockSha256,
               "P9-04",
               2,
               "7955d4c0fb1aa03b5d28484b15f321752b16983996f3b5c5428d9192ff67a509",
               "c7120bd77d338006e8df117c83718ee694aa407167a7ca214e2f71dc2f2da9f1",
               "38d13a94dfe863625ae54396ceee6beccce9bf9de85cd4d26cb0e2e730fdf1ad") &&
           mode is "DIRECT_CHILDREN_OR_SELF" or "DIRECT_CHILDREN" or
               "FLOW_BRANCH" or "FLOW_STEP" or
               "FLOW_EFFECTIVE_PATH" or "FLOW_FINAL";

    private static bool ValidBasicRuntimeScope(
        WorkAssignmentBasicSummarySnapshot row,
        string mode,
        WorkReportStatisticRebuildJob p9)
        => mode switch
        {
            "DIRECT_CHILDREN_OR_SELF" or "DIRECT_CHILDREN" =>
                row.SourceFlowInstanceId is null &&
                row.SourceFlowStepId is null &&
                row.SourceFlowBranchId is null &&
                row.SourceFlowEffectiveStatus is null,
            "FLOW_STEP" =>
                CanonicalObjectId(row.SourceFlowInstanceId) &&
                string.Equals(row.SourceFlowInstanceId, p9.FlowInstanceId,
                    StringComparison.Ordinal) &&
                RequiredOwnerText(row.SourceFlowStepId) &&
                row.SourceFlowBranchId is null &&
                string.Equals(row.SourceFlowStepId, p9.FlowStepId,
                    StringComparison.Ordinal),
            "FLOW_BRANCH" =>
                CanonicalObjectId(row.SourceFlowInstanceId) &&
                string.Equals(row.SourceFlowInstanceId, p9.FlowInstanceId,
                    StringComparison.Ordinal) &&
                CanonicalObjectId(row.SourceFlowBranchId) &&
                row.SourceFlowStepId is null &&
                string.Equals(row.SourceFlowBranchId, p9.FlowBranchId,
                    StringComparison.Ordinal),
            "FLOW_EFFECTIVE_PATH" or "FLOW_FINAL" =>
                CanonicalObjectId(row.SourceFlowInstanceId) &&
                string.Equals(row.SourceFlowInstanceId, p9.FlowInstanceId,
                    StringComparison.Ordinal) &&
                row.SourceFlowStepId is null &&
                row.SourceFlowBranchId is null,
            _ => false
        };

    private static bool ValidAdvancedOwners(
        string sectionId,
        IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode> days,
        IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode> months,
        IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode> years,
        WorkAssignmentAdvancedSummaryHierarchyNodeBase boundary,
        WorkReportStatisticRebuildJob p9)
    {
        var all = days.Cast<WorkAssignmentAdvancedSummaryHierarchyNodeBase>()
            .Concat(months)
            .Concat(years)
            .ToArray();
        return days.All(row => ValidAdvancedOwner(row, "DAY")) &&
            months.All(row => ValidAdvancedOwner(row, "MONTH")) &&
            years.All(row => ValidAdvancedOwner(row, "YEAR")) &&
            all.All(row =>
            string.Equals(row.SectionId, sectionId, StringComparison.Ordinal) &&
            SameAdvancedBoundary(row, boundary) &&
            ValidOwnerConfig(
                row.ConfigId,
                row.ConfigVersionId,
                row.ConfigVersionNo,
                row.ConfigRevision,
                row.ConfigHash) &&
            string.Equals(row.TimeAxis, "UTC_GREGORIAN", StringComparison.Ordinal) &&
            ValidPins(row.DependencyPins, ordered: true) &&
            ValidCandidate(
                row.CandidateChainId,
                row.CandidatePromptId,
                row.CandidateStage,
                row.CandidateCatalogRawSha256,
                row.CandidateCatalogSemanticSha256,
                row.CandidateStageLockSha256,
                "P9-05",
                3,
                "c3ebff7c0cfa4ce62003fb83e0cfc75ba9a9fd1419cc00b9df3836cf981085d3",
                "d0b33a7ed334f0412488618e1ed23375fc72657fa3509adeaceec76013460c0f",
                "505272c7c32544a7363d03c5e8b087bfcfac00aa3a7cef2e89ff7d45c7ecc5bc"));
    }

    private static bool ValidAdvancedOwner(
        WorkAssignmentAdvancedSummaryHierarchyNodeBase row,
        string grain)
        => string.Equals(row.Grain, grain, StringComparison.Ordinal) &&
           string.Equals(
               row.Status,
               WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean,
               StringComparison.Ordinal) &&
           !row.IsDirty &&
           row.BuiltAtUtc is not null &&
           StatisticReconciliationCanonicalJson.IsCanonicalSha256(
               row.SourceSignatureHash) &&
           row.SourceReportCount == row.SourceReportIds.Count &&
           CanonicalObjectIdList(row.SourceReportIds) &&
           StatisticReconciliationCanonicalJson.IsCanonicalSha256(
               row.ValueHash);

    internal static bool SameAdvancedBoundary(
        WorkAssignmentAdvancedSummaryHierarchyNodeBase row,
        WorkAssignmentAdvancedSummaryHierarchyNodeBase boundary)
        => string.Equals(row.ConfigId, boundary.ConfigId,
               StringComparison.Ordinal) &&
           string.Equals(row.ConfigVersionId, boundary.ConfigVersionId,
               StringComparison.Ordinal) &&
           row.ConfigVersionNo == boundary.ConfigVersionNo &&
           row.ConfigRevision == boundary.ConfigRevision &&
           string.Equals(row.ConfigHash, boundary.ConfigHash,
               StringComparison.Ordinal) &&
           row.DependencyPins.SequenceEqual(
               boundary.DependencyPins, StringComparer.Ordinal) &&
           string.Equals(row.TimeAxis, boundary.TimeAxis,
               StringComparison.Ordinal) &&
           string.Equals(row.CandidateChainId, boundary.CandidateChainId,
               StringComparison.Ordinal) &&
           string.Equals(row.CandidatePromptId, boundary.CandidatePromptId,
               StringComparison.Ordinal) &&
           row.CandidateStage == boundary.CandidateStage &&
           string.Equals(row.CandidateCatalogRawSha256,
               boundary.CandidateCatalogRawSha256,
               StringComparison.Ordinal) &&
           string.Equals(row.CandidateCatalogSemanticSha256,
               boundary.CandidateCatalogSemanticSha256,
               StringComparison.Ordinal) &&
           string.Equals(row.CandidateStageLockSha256,
               boundary.CandidateStageLockSha256,
               StringComparison.Ordinal);

    private static bool ValidDiffOwner(
        WorkReportStatisticDiffResult row,
        WorkReportStatisticRebuildJob p9,
        string conceptKey)
        => ValidOwnerConfig(
               row.ConfigId,
               row.ConfigVersionId,
               row.ConfigVersionNo,
               row.ConfigRevision,
               row.ConfigHash) &&
           string.Equals(row.Status, "COMPLETED", StringComparison.Ordinal) &&
           row.IsCurrent && row.IsFresh && !row.IsDirty &&
           row.CompletedAtUtc is not null &&
           StatisticReconciliationCanonicalJson.IsCanonicalSha256(
               row.ResultHash) &&
           row.TotalRowCount >= 0 &&
           row.TotalRowCount == row.Rows.Count &&
           row.EqualRowCount >= 0 && row.ChangedRowCount >= 0 &&
           row.EqualRowCount + row.ChangedRowCount == row.TotalRowCount &&
           ValidPins(row.DependencyPins, ordered: true) &&
           ValidCandidate(
               row.CandidateChainId,
               row.CandidatePromptId,
               row.CandidateStage,
               row.CandidateCatalogRawSha256,
               row.CandidateCatalogSemanticSha256,
               row.CandidateStageLockSha256,
               "P9-06",
               4,
               "b26b24d1bdf9337d85c3ab01c700e357b8a56080f2e808332d1ba612bceb9c68",
               "b4de97a6975b94e4a4da4b7148844ba846af837d283f0e0065762aad10ffdb36",
               "e237f0e260f0ba2704ccb830a9b3aca1bceb7c88eaca52dc679f6b08b9126397") &&
           ValidDiffPeriods(row, p9.PeriodKey) &&
           string.Equals(
               row.Direction == "RIGHT_TO_LEFT"
                   ? row.RightConceptKey
                   : row.LeftConceptKey,
               conceptKey,
               StringComparison.Ordinal);

    internal static bool ValidBasicPeriod(
        WorkAssignmentBasicSummarySnapshot row,
        WorkReportStatisticRebuildJob p9)
    {
        try
        {
            using var document = JsonDocument.Parse(row.RequestJson);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   JsonText(root, "scopeAssignmentId") == row.ScopeAssignmentId &&
                   JsonText(root, "dynamicFormTemplateId") ==
                       row.DynamicFormTemplateId &&
                   JsonText(root, "periodScopeMode") == "SINGLE_PERIOD" &&
                   JsonText(root, "periodKey") == p9.PeriodKey &&
                   JsonNull(root, "periodKeyFrom") &&
                   JsonNull(root, "periodKeyTo") &&
                   JsonText(root, "sourceScopeMode") == row.SourceScopeMode &&
                   JsonText(root, "sourceFlowInstanceId") ==
                       row.SourceFlowInstanceId &&
                   JsonOptionalText(root, "sourceFlowStepId") ==
                       row.SourceFlowStepId &&
                   JsonOptionalText(root, "sourceFlowBranchId") ==
                       row.SourceFlowBranchId &&
                   JsonOptionalText(root, "sourceFlowEffectiveStatus") ==
                       row.SourceFlowEffectiveStatus;
        }
        catch (JsonException)
        {
            return false;
        }
    }
    internal static bool ValidDiffPeriods(
        WorkReportStatisticDiffResult row,
        string? periodKey)
        => row.Direction switch
        {
            "LEFT_TO_RIGHT" =>
                ExactDiffPeriod(row.LeftPeriodJson, periodKey) &&
                CanonicalDiffPeriod(row.RightPeriodJson),
            "RIGHT_TO_LEFT" =>
                ExactDiffPeriod(row.RightPeriodJson, periodKey) &&
                CanonicalDiffPeriod(row.LeftPeriodJson),
            _ => false
        };

    internal static bool ExactDiffPeriod(string? json, string? periodKey)
    {
        if (!CanonicalDiffPeriod(json))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json ?? string.Empty);
            var root = document.RootElement;
            return JsonText(root, "mode") == "EXACT" &&
                   JsonText(root, "periodKey") == periodKey &&
                   JsonNull(root, "periodKeyFrom") &&
                   JsonNull(root, "periodKeyTo");
        }
        catch (JsonException)
        {
            return false;
        }
    }
    private static bool CanonicalDiffPeriod(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            var names = root.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (!names.SequenceEqual(
                    new[] { "mode", "periodKey", "periodKeyFrom", "periodKeyTo" }
                        .OrderBy(name => name, StringComparer.Ordinal),
                    StringComparer.Ordinal) ||
                !string.Equals(
                    StatisticReconciliationCanonicalJson.Canonicalize(root),
                    json,
                    StringComparison.Ordinal))
            {
                return false;
            }
            return JsonText(root, "mode") switch
            {
                "EXACT" => RequiredOwnerText(JsonText(root, "periodKey")) &&
                           JsonNull(root, "periodKeyFrom") &&
                           JsonNull(root, "periodKeyTo"),
                "RANGE" => JsonNull(root, "periodKey") &&
                           RequiredOwnerText(JsonText(root, "periodKeyFrom")) &&
                           RequiredOwnerText(JsonText(root, "periodKeyTo")) &&
                           string.CompareOrdinal(
                               JsonText(root, "periodKeyFrom"),
                               JsonText(root, "periodKeyTo")) <= 0,
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? JsonOptionalText(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? JsonText(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool JsonNull(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Null;
    private bool ValidExportOwner(
        StatRunExportArtifact row,
        WorkReportStatisticRebuildJob p9,
        WorkAssignment scope,
        WorkAssignmentBasicSummarySnapshot? basic,
        IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode> days,
        IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode> months,
        IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode> years,
        WorkReportStatisticDiffResult? diff)
    {
        if (!string.Equals(row.WorkId, scope.WorkId, StringComparison.Ordinal) ||
            !string.Equals(row.ScopeType, "ASSIGNMENT", StringComparison.Ordinal) ||
            !string.Equals(row.ScopeId, scope.Id, StringComparison.Ordinal) ||
            !string.Equals(row.PeriodInstanceKey, p9.PeriodInstanceKey,
                StringComparison.Ordinal) ||
            !string.Equals(row.SchemaVersion, "P9_CANONICAL_EXPORT_V1",
                StringComparison.Ordinal) ||
            !string.Equals(row.Status, StatRunExportStatuses.Completed,
                StringComparison.Ordinal) ||
            row.ExpiresAtUtc.Kind != DateTimeKind.Utc ||
            row.ExpiresAtUtc <= DateTime.UtcNow ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                row.FilterHash) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                row.RequestHash) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                row.AuthorizationSnapshotHash) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                row.ContentHash) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                row.ColumnManifestSha256) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                row.SemanticHash) ||
            !ValidExportResultBinding(
                row, p9, basic, days, months, years, diff))
        {
            return false;
        }
        try
        {
            var sidecar = StatRunExportColumnManifestContract.Parse(
                row.ColumnManifestJson,
                row.ColumnManifestSha256);
            return sidecar.Manifest.Columns.Count == row.ColumnCount &&
                   string.Equals(
                       row.SemanticHash,
                       StatRunExportColumnManifestContract
                           .ComputeOwnerSemanticSha256(
                               row.ResultKind,
                               row.WorkId,
                               row.ScopeType,
                               row.ScopeId,
                               row.ResultId,
                               row.ResultHash,
                               row.ConfigHash,
                               row.SourceHash,
                               row.FilterHash,
                               row.LifecycleRevision,
                               row.RowCount,
                               row.ColumnCount,
                               sidecar.Sha256),
                       StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool ValidExportResultBinding(
        StatRunExportArtifact row,
        WorkReportStatisticRebuildJob p9,
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
            return (string.Equals(row.ResultId, p9.Id, StringComparison.Ordinal) ||
                    string.Equals(row.ResultId, p9.GenerationId,
                        StringComparison.Ordinal)) &&
                   string.Equals(row.ResultHash, p9.GenerationHash,
                       StringComparison.Ordinal) &&
                   string.Equals(row.ConfigHash, p9.ConfigHash,
                       StringComparison.Ordinal) &&
                   string.Equals(row.SourceHash, p9.SourcePayloadHash,
                       StringComparison.Ordinal) &&
                   row.LifecycleRevision == p9.SourceLifecycleRevision &&
                   ValidExportCandidate(row);
        }

        if (basic is null)
            return false;
        var flow = basic.SourceScopeMode?.StartsWith(
            "FLOW", StringComparison.Ordinal) == true;
        if (row.ResultKind is StatRunExportResultKinds.Basic or
            StatRunExportResultKinds.Flow)
        {
            return (flow
                        ? row.ResultKind == StatRunExportResultKinds.Flow
                        : row.ResultKind == StatRunExportResultKinds.Basic) &&
                   string.Equals(row.ResultId, basic.Id,
                       StringComparison.Ordinal) &&
                   string.Equals(row.ResultHash,
                       StatRunCanonicalJson.HashText(
                           basic.SnapshotJson ?? string.Empty),
                       StringComparison.Ordinal) &&
                   string.Equals(row.ConfigHash, basic.ConfigHash,
                       StringComparison.Ordinal) &&
                   string.Equals(row.SourceHash, basic.SourceSignatureHash,
                       StringComparison.Ordinal) &&
                   row.LifecycleRevision == 0 &&
                   ValidExportCandidate(row);
        }

        if (row.ResultKind == StatRunExportResultKinds.Advanced)
        {
            var node = days.Cast<WorkAssignmentAdvancedSummaryHierarchyNodeBase>()
                .Concat(months)
                .Concat(years)
                .SingleOrDefault(value => string.Equals(
                    value.Id, row.ResultId, StringComparison.Ordinal));
            if (node is null)
                return false;
            var sourceHash = node.SourceSignatureHash ??
                StatRunCanonicalJson.HashObject(node.SourceReportIds);
            return string.Equals(row.ResultHash, node.ValueHash,
                       StringComparison.Ordinal) &&
                   string.Equals(row.ConfigHash, node.ConfigHash,
                       StringComparison.Ordinal) &&
                   string.Equals(row.SourceHash, sourceHash,
                       StringComparison.Ordinal) &&
                   row.LifecycleRevision == 0 &&
                   ValidExportCandidate(row);
        }

        if (row.ResultKind == StatRunExportResultKinds.Diff)
        {
            if (diff is null)
                return false;
            var sourceHash = StatRunCanonicalJson.HashObject(diff.SourcePins);
            var lifecycleRevision = diff.SourcePins.Count == 0
                ? 0
                : diff.SourcePins.Max(pin => pin.SourceLifecycleRevision);
            return string.Equals(row.ResultId, diff.Id,
                       StringComparison.Ordinal) &&
                   string.Equals(row.ResultHash, diff.ResultHash,
                       StringComparison.Ordinal) &&
                   string.Equals(row.ConfigHash, diff.ConfigHash,
                       StringComparison.Ordinal) &&
                   string.Equals(row.SourceHash, sourceHash,
                       StringComparison.Ordinal) &&
                   row.LifecycleRevision == lifecycleRevision &&
                   ValidExportCandidate(row);
        }
        return false;
    }

    private bool ValidExportCandidate(StatRunExportArtifact row)
    {
        var capability = StatRunExportContract.CapabilityFor(row.ResultKind);
        var evaluation = _statRunActivation.EvaluateCapability(
            capability,
            StatRunExportContract.RouteForCapability(capability));
        var binding = evaluation.Binding;
        return evaluation.Enabled && binding is not null &&
               string.Equals(row.CandidateChainId, binding.ChainId,
                   StringComparison.Ordinal) &&
               string.Equals(row.CandidatePromptId, binding.PromptId,
                   StringComparison.Ordinal) &&
               row.CandidateStage == binding.Stage &&
               string.Equals(row.CatalogVersion, binding.CatalogVersion,
                   StringComparison.Ordinal) &&
               string.Equals(row.CatalogRawSha256, binding.CatalogRawSha256,
                   StringComparison.Ordinal) &&
               string.Equals(row.CatalogSemanticSha256,
                   binding.CatalogSemanticSha256,
                   StringComparison.Ordinal) &&
               string.Equals(row.StageLockSha256, binding.StageLockSha256,
                   StringComparison.Ordinal);
    }

    private static bool ValidOwnerConfig(
        string? configId,
        string? configVersionId,
        int configVersionNo,
        long configRevision,
        string? configHash)
        => CanonicalObjectId(configId) &&
           CanonicalObjectId(configVersionId) &&
           configVersionNo > 0 &&
           configRevision > 0 &&
           StatisticReconciliationCanonicalJson.IsCanonicalSha256(configHash);

    private static bool ValidCandidate(
        string? chainId,
        string? promptId,
        int stage,
        string? catalogRaw,
        string? catalogSemantic,
        string? stageLock,
        string expectedPrompt,
        int expectedStage,
        string expectedRaw,
        string expectedSemantic,
        string expectedLock)
        => RequiredOwnerText(chainId) &&
           string.Equals(promptId, expectedPrompt, StringComparison.Ordinal) &&
           stage == expectedStage &&
           string.Equals(catalogRaw, expectedRaw, StringComparison.Ordinal) &&
           string.Equals(
               catalogSemantic,
               expectedSemantic,
               StringComparison.Ordinal) &&
           string.Equals(stageLock, expectedLock, StringComparison.Ordinal);

    private static bool ValidPins(
        IReadOnlyList<string>? values,
        bool ordered)
    {
        if (values is null || values.Count > 10_000 ||
            values.Any(value => !RequiredOwnerText(value)) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            return false;
        }
        return !ordered || values.SequenceEqual(
            values.OrderBy(value => value, StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    internal static bool ExactIds<TNode>(
        IReadOnlyList<TNode> rows,
        IReadOnlyList<string> expected)
        where TNode : WorkAssignmentAdvancedSummaryHierarchyNodeBase
        => rows.Select(row => row.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .SequenceEqual(expected, StringComparer.Ordinal);

    private static bool CanonicalObjectId(string? value)
        => MongoDB.Bson.ObjectId.TryParse(value, out var parsed) &&
           string.Equals(value, parsed.ToString(), StringComparison.Ordinal);

    private static bool RequiredOwnerText(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
           !value.Any(char.IsControl);
}
