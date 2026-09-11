using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationRaceProbe
{
    private async Task<P10RaceRecheckFixture> SeedRecheckCaptureOwnersAsync(
        CancellationToken ct)
    {
        var source = CoreFixture();
        var p9 = await Context().WorkReportStatisticRebuildJobs
            .Find(value => value.Id == source.P9RunId && !value.IsDeleted)
            .SingleAsync(ct);
        var start = p9.PeriodStartUtc is { Kind: DateTimeKind.Utc } startUtc
            ? startUtc : throw new InvalidOperationException("P10_RACE_RECHECK_PERIOD_START_INVALID");
        var end = p9.PeriodEndUtc is { Kind: DateTimeKind.Utc } endUtc && endUtc > start
            ? endUtc : throw new InvalidOperationException("P10_RACE_RECHECK_PERIOD_END_INVALID");
        var actorId = CoreActor("executor").Id;
        var configVersionNo = p9.ConfigVersionNo is > 0
            ? p9.ConfigVersionNo.Value
            : throw new InvalidOperationException("P10_RACE_RECHECK_CONFIG_VERSION_INVALID");
        var basicId = ObjectId.GenerateNewId().ToString();
        var dayId = ObjectId.GenerateNewId().ToString();
        var monthId = ObjectId.GenerateNewId().ToString();
        var yearId = ObjectId.GenerateNewId().ToString();
        var diffId = ObjectId.GenerateNewId().ToString();
        var diffRunId = ObjectId.GenerateNewId().ToString();
        var sectionId = "p10-race-recheck";
        var exportId = Sha($"P10-RACE-RECHECK-EXPORT:{_rootId}");
        var requestJson = StatRunCanonicalJson.CanonicalObject(new
        {
            dynamicFormTemplateId = source.DynamicFormVersionId,
            periodKey = source.PeriodKey,
            periodKeyFrom = (string?)null,
            periodKeyTo = (string?)null,
            periodScopeMode = "SINGLE_PERIOD",
            scopeAssignmentId = source.ScopeAssignmentId,
            sourceFlowBranchId = (string?)null,
            sourceFlowEffectiveStatus = p9.FlowEffectiveStatus,
            sourceFlowInstanceId = source.FlowInstanceId,
            sourceFlowStepId = (string?)null,
            sourceScopeMode = "FLOW_FINAL"
        });
        var basic = new WorkAssignmentBasicSummarySnapshot
        {
            Id = basicId, WorkId = source.WorkId,
            ScopeAssignmentId = source.ScopeAssignmentId,
            DynamicFormTemplateId = source.DynamicFormVersionId,
            RequestHash = StatisticReconciliationCanonicalJson.HashText(requestJson),
            RequestJson = requestJson, SourceScopeMode = "FLOW_FINAL",
            SourceFlowInstanceId = source.FlowInstanceId,
            SourceFlowEffectiveStatus = p9.FlowEffectiveStatus,
            SourceAssignmentIds = [source.ScopeAssignmentId],
            SourceReportIds = [source.ReportId],
            SourceSignatureHash = Sha("P10-RACE-RECHECK-BASIC-SOURCE"),
            ConfigId = source.ConfigId, ConfigVersionId = source.ConfigVersionId,
            ConfigVersionNo = configVersionNo, ConfigRevision = source.ConfigRevision,
            ConfigHash = source.ConfigHash, ConfigDependencyPins = [],
            CandidateChainId = p9.CandidateChainId!, CandidatePromptId = "P9-04",
            CandidateStage = 2,
            CandidateCatalogRawSha256 = "7955d4c0fb1aa03b5d28484b15f321752b16983996f3b5c5428d9192ff67a509",
            CandidateCatalogSemanticSha256 = "c7120bd77d338006e8df117c83718ee694aa407167a7ca214e2f71dc2f2da9f1",
            CandidateStageLockSha256 = "38d13a94dfe863625ae54396ceee6beccce9bf9de85cd4d26cb0e2e730fdf1ad",
            SnapshotJson = "{}", SnapshotDirty = false,
            SnapshotRefreshedAtUtc = FixedUtc,
            RefreshStatus = WorkAssignmentBasicSummaryRefreshStatuses.Done,
            RefreshJobId = "p10-race-recheck-basic",
            RefreshCorrelationId = "p10-race-recheck-basic",
            RefreshQueuedAtUtc = FixedUtc, RefreshStartedAtUtc = FixedUtc,
            RefreshFinishedAtUtc = FixedUtc, CreatedAtUtc = FixedUtc,
            UpdatedAtUtc = FixedUtc, CreatedByUserId = actorId,
            UpdatedByUserId = actorId, IsDeleted = false
        };

        TNode Node<TNode>(string id, string grain, string grainKey,
            IReadOnlyList<string> reports, IReadOnlyList<string> inputs)
            where TNode : WorkAssignmentAdvancedSummaryHierarchyNodeBase, new()
            => new()
            {
                Id = id, WorkId = source.WorkId,
                AssignmentId = source.ScopeAssignmentId,
                DynamicFormTemplateId = source.DynamicFormVersionId,
                SectionId = sectionId, ConfigId = source.ConfigId,
                ConfigVersionId = source.ConfigVersionId,
                ConfigVersionNo = configVersionNo,
                ConfigRevision = source.ConfigRevision, ConfigHash = source.ConfigHash,
                DependencyPins = [], TimeAxis = "UTC_GREGORIAN",
                CandidateChainId = p9.CandidateChainId!, CandidatePromptId = "P9-05",
                CandidateStage = 3,
                CandidateCatalogRawSha256 = "c3ebff7c0cfa4ce62003fb83e0cfc75ba9a9fd1419cc00b9df3836cf981085d3",
                CandidateCatalogSemanticSha256 = "d0b33a7ed334f0412488618e1ed23375fc72657fa3509adeaceec76013460c0f",
                CandidateStageLockSha256 = "505272c7c32544a7363d03c5e8b087bfcfac00aa3a7cef2e89ff7d45c7ecc5bc",
                Grain = grain, GrainKey = grainKey,
                WindowStartUtc = start, WindowEndExclusiveUtc = end,
                Status = WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean,
                IsDirty = false,
                SourceSignatureHash = Sha($"P10-RACE-RECHECK-{grain}-SOURCE"),
                SourceReportCount = reports.Count, SourceReportIds = reports.ToList(),
                InputNodeKeys = inputs.ToList(), ValueJson = "{}",
                ValueHash = StatisticReconciliationCanonicalJson.HashText("{}"),
                BuiltAtUtc = FixedUtc, BuildJobId = "p10-race-recheck-advanced",
                BuildCorrelationId = "p10-race-recheck-advanced",
                BuildCommandId = "p10-race-recheck-advanced",
                BuildRequestHash = Sha($"P10-RACE-RECHECK-{grain}-REQUEST"),
                BuildReceiptId = "p10-race-recheck-advanced", BuildAttemptNo = 1,
                FenceToken = 1, CreatedAtUtc = FixedUtc, UpdatedAtUtc = FixedUtc,
                CreatedByUserId = actorId, UpdatedByUserId = actorId, IsDeleted = false
            };
        var day = Node<WorkAssignmentAdvancedSummaryDayNode>(dayId, "DAY",
            source.PeriodKey, [source.ReportId], []);
        day.DayKey = source.PeriodKey;
        var yearKey = source.PeriodKey.Split('-')[0];
        var month = Node<WorkAssignmentAdvancedSummaryMonthNode>(monthId, "MONTH",
            source.PeriodKey, [], [day.GrainKey]);
        month.MonthKey = source.PeriodKey; month.YearKey = yearKey;
        var year = Node<WorkAssignmentAdvancedSummaryYearNode>(yearId, "YEAR",
            yearKey, [], [month.GrainKey]);
        year.YearKey = yearKey;

        var period = StatRunCanonicalJson.CanonicalObject(new
        { mode = "EXACT", periodKey = source.PeriodKey,
          periodKeyFrom = (string?)null, periodKeyTo = (string?)null });
        var diff = new WorkReportStatisticDiffResult
        {
            Id = diffId, RunId = diffRunId, WorkId = source.WorkId,
            AssignmentId = source.ScopeAssignmentId,
            DynamicFormTemplateId = source.DynamicFormVersionId,
            ConfigId = source.ConfigId, ConfigVersionId = source.ConfigVersionId,
            ConfigVersionNo = configVersionNo, ConfigRevision = source.ConfigRevision,
            ConfigHash = source.ConfigHash, DependencyPins = [],
            CandidateChainId = p9.CandidateChainId!, CandidatePromptId = "P9-06",
            CandidateStage = 4,
            CandidateCatalogRawSha256 = "b26b24d1bdf9337d85c3ab01c700e357b8a56080f2e808332d1ba612bceb9c68",
            CandidateCatalogSemanticSha256 = "b4de97a6975b94e4a4da4b7148844ba846af837d283f0e0065762aad10ffdb36",
            CandidateStageLockSha256 = "e237f0e260f0ba2704ccb830a9b3aca1bceb7c88eaca52dc679f6b08b9126397",
            LeftConceptKind = "FIELD", LeftConceptKey = source.ConceptKey,
            LeftConceptCode = source.ConceptKey, LeftDataType = "NUMBER",
            LeftPeriodJson = period, RightConceptKind = "FIELD",
            RightConceptKey = source.ConceptKey, RightConceptCode = source.ConceptKey,
            RightDataType = "NUMBER", RightPeriodJson = period,
            Direction = "LEFT_TO_RIGHT", MissingPolicy = "INCLUDE",
            EmptyPolicy = "INCLUDE", TimeAxis = "UTC_GREGORIAN",
            CommandId = "p10-race-recheck-diff",
            RequestHash = Sha("P10-RACE-RECHECK-DIFF-REQUEST"),
            ReceiptId = "p10-race-recheck-diff", RequestedByUserId = actorId,
            Status = P9StatisticDiffResultStatuses.Completed,
            JobId = "p10-race-recheck-diff", AttemptNo = 1, FenceToken = 1,
            SourcePins = [], Rows = [], TotalRowCount = 0,
            EqualRowCount = 0, ChangedRowCount = 0,
            ResultHash = Sha("P10-RACE-RECHECK-DIFF-RESULT"),
            IsCurrent = true, IsFresh = true, IsDirty = false,
            CompletedAtUtc = FixedUtc, CreatedAtUtc = FixedUtc,
            UpdatedAtUtc = FixedUtc, CreatedByUserId = actorId,
            UpdatedByUserId = actorId, IsDeleted = false
        };

        var sidecar = StatRunExportColumnManifestContract.Create(
        [ new StatRunExportColumnManifestEntry(0, "ordinal",
            StatRunExportColumnManifestContract.ValueTypes.Integer,
            StatRunExportColumnManifestContract.BlankPolicies.Forbidden, false) ]);
        var export = new StatRunExportArtifact
        {
            Id = exportId, CommandId = "p10-race-recheck-export",
            RequestHash = Sha("P10-RACE-RECHECK-EXPORT-REQUEST"),
            ReceiptId = Sha("P10-RACE-RECHECK-EXPORT-RECEIPT"),
            RequestedByUserId = actorId,
            AuthorizationSnapshotHash = Sha("P10-RACE-RECHECK-EXPORT-AUTH"),
            CapabilityId = StatRunCapabilities.DirectFieldTableLabel,
            ResultKind = StatRunExportResultKinds.DirectField,
            Format = StatRunExportFormats.Csv, WorkId = source.WorkId,
            ScopeType = "ASSIGNMENT", ScopeId = source.ScopeAssignmentId,
            PeriodInstanceKey = source.PeriodInstanceKey, ResultId = p9.Id,
            ResultHash = p9.GenerationHash!, ConfigHash = p9.ConfigHash!,
            SourceHash = p9.SourcePayloadHash!,
            LifecycleRevision = p9.SourceLifecycleRevision!.Value,
            CatalogVersion = StatRunCapabilityActivation.RequiredCatalogVersion,
            CatalogRawSha256 = StatRunCapabilityActivation.PublishedCatalogRawSha256,
            CatalogSemanticSha256 = StatRunCapabilityActivation.PublishedCatalogSemanticSha256,
            StageLockSha256 = StatRunCapabilityActivation.PublishedSealStageLockRawSha256,
            CandidateChainId = StatRunCapabilityActivation.RequiredChainId,
            CandidatePromptId = StatRunCapabilityActivation.PublishedPromptId,
            CandidateStage = 9, FilterHash = StatRunCanonicalJson.HashText("{}"),
            CanonicalFilterJson = "{}", ColumnManifestJson = sidecar.CanonicalJson,
            ColumnManifestSha256 = sidecar.Sha256,
            Status = StatRunExportStatuses.Completed,
            FileName = "p10-race-recheck.csv", ContentType = "text/csv",
            StorageKey = $"p10-race-recheck/{exportId}.csv",
            ContentHash = Sha("P10-RACE-RECHECK-EXPORT-CONTENT"),
            ByteCount = 0, RowCount = 0, ColumnCount = 1,
            CompletedAtUtc = FixedUtc, ExpiresAtUtc = FixedUtc.AddYears(1),
            CreatedAtUtc = FixedUtc, UpdatedAtUtc = FixedUtc,
            CreatedByUserId = actorId, UpdatedByUserId = actorId, IsDeleted = false
        };
        export.SemanticHash = StatRunExportColumnManifestContract
            .ComputeOwnerSemanticSha256(export.ResultKind, export.WorkId,
                export.ScopeType, export.ScopeId, export.ResultId,
                export.ResultHash, export.ConfigHash, export.SourceHash,
                export.FilterHash, export.LifecycleRevision, export.RowCount,
                export.ColumnCount, sidecar.Sha256);

        await Context().WorkAssignmentBasicSummarySnapshots.InsertOneAsync(basic,
            cancellationToken: ct);
        await Context().WorkAssignmentAdvancedSummaryDayNodes.InsertOneAsync(day,
            cancellationToken: ct);
        await Context().WorkAssignmentAdvancedSummaryMonthNodes.InsertOneAsync(month,
            cancellationToken: ct);
        await Context().WorkAssignmentAdvancedSummaryYearNodes.InsertOneAsync(year,
            cancellationToken: ct);
        await Context().Db.GetCollection<WorkReportStatisticDiffResult>(
            "work_report_statistic_diff_results").InsertOneAsync(diff,
            cancellationToken: ct);
        await Context().WorkReportStatisticExports.InsertOneAsync(export,
            cancellationToken: ct);

        var plan = new JsonObject
        {
            ["boundaryRegistryVersion"] = StatisticReconciliationActualCapturePlanIntegrity.BoundaryRegistryVersion,
            ["basicSnapshotId"] = basicId, ["basicMode"] = "FLOW_FINAL",
            ["advancedSectionId"] = sectionId,
            ["advancedDayNodeIds"] = new JsonArray(dayId),
            ["advancedMonthNodeIds"] = new JsonArray(monthId),
            ["advancedYearNodeIds"] = new JsonArray(yearId),
            ["diffResultId"] = diffId, ["diffRunId"] = diffRunId,
            ["apiSurface"] = "BASIC_SOURCE", ["apiOwnerResultId"] = basicId,
            ["exportId"] = exportId
        };
        return new P10RaceRecheckFixture(basicId, sectionId, [dayId], [monthId],
            [yearId], diffId, diffRunId, exportId, plan);
    }

    private async Task CleanupRecheckCaptureOwnersAsync(CancellationToken ct)
    {
        var f = _recheckCaptureFixture;
        if (f is null) return;
        await Context().WorkAssignmentBasicSummarySnapshots.DeleteOneAsync(
            value => value.Id == f.BasicId, ct);
        await Context().WorkAssignmentAdvancedSummaryDayNodes.DeleteManyAsync(
            value => f.DayIds.Contains(value.Id), ct);
        await Context().WorkAssignmentAdvancedSummaryMonthNodes.DeleteManyAsync(
            value => f.MonthIds.Contains(value.Id), ct);
        await Context().WorkAssignmentAdvancedSummaryYearNodes.DeleteManyAsync(
            value => f.YearIds.Contains(value.Id), ct);
        await Context().Db.GetCollection<WorkReportStatisticDiffResult>(
            "work_report_statistic_diff_results").DeleteOneAsync(
            value => value.Id == f.DiffId, ct);
        await Context().WorkReportStatisticExports.DeleteOneAsync(
            value => value.Id == f.ExportId, ct);
        _recheckCaptureFixture = null;
    }

    private static void ApplyRecheckCapturePlan(JsonObject request,
        P10RaceRecheckFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fixture);
        request["filter"] = new JsonObject();
        request["actualCapturePlan"] = fixture.ActualCapturePlanRequest.DeepClone();
    }
}

internal sealed record P10RaceRecheckFixture(string BasicId, string SectionId,
    IReadOnlyList<string> DayIds, IReadOnlyList<string> MonthIds,
    IReadOnlyList<string> YearIds, string DiffId, string DiffRunId,
    string ExportId, JsonObject ActualCapturePlanRequest);