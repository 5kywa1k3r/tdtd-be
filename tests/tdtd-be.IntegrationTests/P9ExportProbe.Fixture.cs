using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task SeedCanonicalExportFixturesAsync(CancellationToken ct)
    {
        var fixture = Fixture();
        var actorId = Actor("admin").Id;
        var now = DateTime.UtcNow;
        var rows = Enumerable.Range(0, 203)
            .Select(index => new Dictionary<string, object?>
            {
                ["code"] = index == 0 ? "Việt Nam, \"dòng\"\r\ntiếp" : $"ROW-{index:000}",
                ["amount"] = index == 0 ? 12.5m : index,
                ["active"] = index % 2 == 0,
                ["occurredAtUtc"] = new DateTime(2026, 8, 4, 1, 2, 3, DateTimeKind.Utc)
                    .AddMinutes(index),
                ["empty"] = null,
                ["danger"] = index switch
                {
                    0 => "=1+1",
                    1 => "+SUM(A1:A2)",
                    2 => "-2+3",
                    3 => "@cmd",
                    _ => "safe"
                }
            })
            .ToArray();
        var snapshotJson = JsonSerializer.Serialize(rows);
        var sourceHash = StatRunCanonicalJson.HashText("P9-EXP-CANONICAL-SOURCE");

        var basic = await InsertExportSnapshotAsync(
            StatRunExportResultKinds.Basic,
            "DIRECT_CHILDREN_OR_SELF",
            snapshotJson,
            sourceHash,
            actorId,
            now,
            ct);
        _exportFixtures[basic.ResultKind] = basic;
        var flow = await InsertExportSnapshotAsync(
            StatRunExportResultKinds.Flow,
            "FLOW_ACTIVE_BRANCH",
            snapshotJson,
            sourceHash,
            actorId,
            now,
            ct);
        _exportFixtures[flow.ResultKind] = flow;

        var advancedId = ObjectId.GenerateNewId().ToString();
        var advancedHash = StatRunCanonicalJson.HashText(snapshotJson);
        var advanced = new WorkAssignmentAdvancedSummaryDayNode
        {
            Id = advancedId,
            WorkId = fixture.WorkId,
            AssignmentId = fixture.AssignmentId,
            DynamicFormTemplateId = fixture.TemplateId,
            SectionId = "P9-EXP",
            ConfigId = fixture.ConfigId,
            ConfigVersionId = fixture.ConfigVersionId,
            ConfigVersionNo = fixture.ConfigVersionNo,
            ConfigRevision = fixture.ConfigRevision,
            ConfigHash = fixture.ConfigHash,
            DependencyPins = [sourceHash],
            CandidateChainId = ChainId,
            CandidatePromptId = ExportPromptId,
            CandidateStage = 7,
            CandidateCatalogRawSha256 = ExportCatalogRawSha256,
            CandidateCatalogSemanticSha256 = ExportCatalogSemanticSha256,
            CandidateStageLockSha256 = ExportStageLockSha256,
            Grain = WorkAssignmentAdvancedSummaryHierarchyGrains.Day,
            GrainKey = "2026-08-04",
            DayKey = "2026-08-04",
            WindowStartUtc = new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc),
            WindowEndExclusiveUtc = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc),
            Status = WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean,
            IsDirty = false,
            SourceSignatureHash = sourceHash,
            SourceReportCount = 1,
            SourceReportIds = [fixture.ReportId],
            ValueJson = snapshotJson,
            ValueHash = advancedHash,
            BuiltAtUtc = now,
            CreatedByUserId = actorId,
            UpdatedByUserId = actorId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        await RequireDatabase().GetCollection<WorkAssignmentAdvancedSummaryDayNode>(
                "work_assignment_advanced_summary_day_nodes")
            .InsertOneAsync(advanced, cancellationToken: ct);
        _exportFixtures[StatRunExportResultKinds.Advanced] = new P9ExportCanonicalFixture(
            StatRunExportResultKinds.Advanced,
            StatRunCapabilities.AdvancedSummary,
            fixture.WorkId,
            "ASSIGNMENT",
            fixture.AssignmentId,
            null,
            advancedId,
            advancedHash,
            fixture.ConfigHash,
            sourceHash,
            0,
            rows.Length);

        var diffRows = new List<P9StatisticDiffResultRow>
        {
            new()
            {
                RowId = StatRunCanonicalJson.HashText("P9-EXP-DIFF-ROW"),
                Ordinal = 1,
                Key = "amount",
                ConceptKind = "FIELD",
                ConceptKey = "amount",
                Left = new P9StatisticDiffTypedValue
                {
                    State = P9StatisticDiffValueStates.Value,
                    DataType = "NUMBER",
                    CanonicalValue = "12.5",
                    NumericValue = 12.5m
                },
                Right = new P9StatisticDiffTypedValue
                {
                    State = P9StatisticDiffValueStates.Value,
                    DataType = "TEXT",
                    CanonicalValue = "=1+1"
                },
                Equal = false,
                DifferenceKind = "CHANGED",
                NumericDelta = 1.5m
            }
        };
        var sourcePins = new List<P9StatisticDiffSourcePin>
        {
            new()
            {
                Side = "LEFT",
                SourceReportId = fixture.ReportId,
                SourcePayloadRevision = fixture.SourceRevision,
                SourcePayloadHash = fixture.SourceHash,
                SourceLifecycleRevision = fixture.LifecycleRevision,
                DirectRunId = _lfcRunId!,
                DirectGenerationId = _lfcGenerationId!
            }
        };
        var diffId = ObjectId.GenerateNewId().ToString();
        var diffResultHash = StatRunCanonicalJson.HashObject(diffRows);
        var diffSourceHash = StatRunCanonicalJson.HashObject(sourcePins);
        var diff = new WorkReportStatisticDiffResult
        {
            Id = diffId,
            RunId = ObjectId.GenerateNewId().ToString(),
            WorkId = fixture.WorkId,
            AssignmentId = fixture.AssignmentId,
            DynamicFormTemplateId = fixture.TemplateId,
            ConfigId = fixture.ConfigId,
            ConfigVersionId = fixture.ConfigVersionId,
            ConfigVersionNo = fixture.ConfigVersionNo,
            ConfigRevision = fixture.ConfigRevision,
            ConfigHash = fixture.ConfigHash,
            DependencyPins = [sourceHash],
            CandidateChainId = ChainId,
            CandidatePromptId = ExportPromptId,
            CandidateStage = 7,
            CandidateCatalogRawSha256 = ExportCatalogRawSha256,
            CandidateCatalogSemanticSha256 = ExportCatalogSemanticSha256,
            CandidateStageLockSha256 = ExportStageLockSha256,
            LeftConceptKind = "FIELD",
            LeftConceptKey = "amount",
            LeftConceptCode = "amount",
            LeftDataType = "NUMBER",
            LeftPeriodJson = "{}",
            RightConceptKind = "FIELD",
            RightConceptKey = "amount",
            RightConceptCode = "amount",
            RightDataType = "NUMBER",
            RightPeriodJson = "{}",
            Direction = "LEFT_TO_RIGHT",
            MissingPolicy = "EXPLICIT",
            EmptyPolicy = "DISTINCT",
            CommandId = "p9-exp-diff-fixture",
            RequestHash = StatRunCanonicalJson.HashText("P9-EXP-DIFF-REQUEST"),
            ReceiptId = StatRunCanonicalJson.HashText("P9-EXP-DIFF-RECEIPT"),
            RequestedByUserId = actorId,
            Status = P9StatisticDiffResultStatuses.Completed,
            JobId = ObjectId.GenerateNewId().ToString(),
            AttemptNo = 1,
            SourcePins = sourcePins,
            Rows = diffRows,
            TotalRowCount = diffRows.Count,
            ChangedRowCount = diffRows.Count,
            ResultHash = diffResultHash,
            IsCurrent = true,
            IsFresh = true,
            IsDirty = false,
            CompletedAtUtc = now,
            ExpiresAtUtc = now.AddDays(1),
            CreatedByUserId = actorId,
            UpdatedByUserId = actorId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        await RequireDatabase().GetCollection<WorkReportStatisticDiffResult>(
                "work_report_statistic_diff_results")
            .InsertOneAsync(diff, cancellationToken: ct);
        _exportFixtures[StatRunExportResultKinds.Diff] = new P9ExportCanonicalFixture(
            StatRunExportResultKinds.Diff,
            StatRunCapabilities.Diff,
            fixture.WorkId,
            "ASSIGNMENT",
            fixture.AssignmentId,
            null,
            diffId,
            diffResultHash,
            fixture.ConfigHash,
            diffSourceHash,
            fixture.LifecycleRevision,
            diffRows.Count);

        var lifecycleJob = await LoadLifecycleJobAsync(ct);
        _exportFixtures[StatRunExportResultKinds.DirectField] = new P9ExportCanonicalFixture(
            StatRunExportResultKinds.DirectField,
            StatRunCapabilities.DirectFieldTableLabel,
            fixture.WorkId,
            "WORK",
            fixture.WorkId,
            fixture.PeriodInstanceKey,
            _lfcRunId!,
            BsonString(lifecycleJob, "generationHash"),
            BsonString(lifecycleJob, "configHash"),
            BsonString(lifecycleJob, "sourcePayloadHash"),
            BsonInt(lifecycleJob, "sourceLifecycleRevision"),
            1);

        var empty = await InsertExportSnapshotAsync(
            "EMPTY",
            "DIRECT_CHILDREN_OR_SELF",
            "[]",
            sourceHash,
            actorId,
            now,
            ct);
        _exportFixtures["EMPTY"] = empty;
        var limitJson = JsonSerializer.Serialize(
            Enumerable.Range(0, StatRunExportContract.MaxRows + 1)
                .Select(index => new { index }));
        var limit = await InsertExportSnapshotAsync(
            "LIMIT",
            "DIRECT_CHILDREN_OR_SELF",
            limitJson,
            sourceHash,
            actorId,
            now,
            ct);
        _exportFixtures["LIMIT"] = limit;
    }

    private async Task<P9ExportCanonicalFixture> InsertExportSnapshotAsync(
        string fixtureKind,
        string sourceScopeMode,
        string snapshotJson,
        string sourceHash,
        string actorId,
        DateTime now,
        CancellationToken ct)
    {
        var fixture = Fixture();
        var id = ObjectId.GenerateNewId().ToString();
        var snapshot = new WorkAssignmentBasicSummarySnapshot
        {
            Id = id,
            WorkId = fixture.WorkId,
            ScopeAssignmentId = fixture.AssignmentId,
            DynamicFormTemplateId = fixture.TemplateId,
            RequestHash = StatRunCanonicalJson.HashText($"P9-EXP-{fixtureKind}-REQUEST"),
            RequestJson = "{}",
            SourceScopeMode = sourceScopeMode,
            SourceAssignmentIds = [fixture.AssignmentId],
            SourceReportIds = [fixture.ReportId],
            SourceSignatureHash = sourceHash,
            ConfigId = fixture.ConfigId,
            ConfigVersionId = fixture.ConfigVersionId,
            ConfigVersionNo = fixture.ConfigVersionNo,
            ConfigRevision = fixture.ConfigRevision,
            ConfigHash = fixture.ConfigHash,
            ConfigDependencyPins = [sourceHash],
            CandidateChainId = ChainId,
            CandidatePromptId = ExportPromptId,
            CandidateStage = 7,
            CandidateCatalogRawSha256 = ExportCatalogRawSha256,
            CandidateCatalogSemanticSha256 = ExportCatalogSemanticSha256,
            CandidateStageLockSha256 = ExportStageLockSha256,
            SnapshotJson = snapshotJson,
            SnapshotDirty = false,
            SnapshotRefreshedAtUtc = now,
            RefreshStatus = WorkAssignmentBasicSummaryRefreshStatuses.Done,
            RefreshFinishedAtUtc = now,
            CreatedByUserId = actorId,
            UpdatedByUserId = actorId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        await RequireDatabase().GetCollection<WorkAssignmentBasicSummarySnapshot>(
                "work_assignment_basic_summary_snapshots")
            .InsertOneAsync(snapshot, cancellationToken: ct);
        var resultKind = fixtureKind is "EMPTY" or "LIMIT"
            ? StatRunExportResultKinds.Basic
            : fixtureKind;
        return new P9ExportCanonicalFixture(
            resultKind,
            resultKind == StatRunExportResultKinds.Flow
                ? StatRunCapabilities.FlowScopes
                : StatRunCapabilities.BasicSummary,
            fixture.WorkId,
            "ASSIGNMENT",
            fixture.AssignmentId,
            null,
            id,
            StatRunCanonicalJson.HashText(snapshotJson),
            fixture.ConfigHash,
            sourceHash,
            0,
            JsonDocument.Parse(snapshotJson).RootElement.GetArrayLength());
    }
}
