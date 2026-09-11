using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    private const string CloseoutDirectFieldId = "field_amount";
    private const string CloseoutDirectMetricId = "amount";
    private const string CloseoutDirectAdvancedSectionId =
        "p10-closeout-direct-section";

    private static readonly JsonSerializerOptions CloseoutOwnerJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

    private P10CloseoutCaptureOwnerFixture? _closeoutCaptureOwnerFixture;

    private async Task<P10CloseoutCaptureOwnerFixture>
        PrepareCloseoutCaptureOwnersAsync(
            P10ProductionDirectFixturePins lifecycle,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        HarnessAssert.True(
            _closeoutCaptureOwnerFixture is null,
            "P10-CLOSE capture owners must be prepared once.");

        var database = RequireDatabase();
        var now = DateTime.UtcNow;
        var executor = Actor("executor");
        var fixture = Fixture();
        var p9 = await database
            .GetCollection<WorkReportStatisticRebuildJob>(
                "work_report_statistic_rebuild_jobs")
            .Find(value => value.Id == lifecycle.P9RunId && !value.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.Equal(
            lifecycle.P9GenerationHash,
            p9.GenerationHash,
            "P10-CLOSE capture owner P9 generation hash");
        HarnessAssert.Equal(
            lifecycle.ConfigBundleHash,
            fixture.ConfigBundleHash,
            "P10-CLOSE capture owner config bundle");

        var metricConfigurationJson = StatRunCanonicalJson.CanonicalObject(
            new
            {
                expectedMetrics = new[]
                {
                    new
                    {
                        family = "DIRECT",
                        kind = "FIELD",
                        metricId = CloseoutDirectMetricId,
                        periodKey = lifecycle.PeriodKey,
                        fieldId = CloseoutDirectFieldId,
                        jsonPointer = "/values1D",
                        valueType = "NUMBER",
                        unordered = false,
                        expandArray = true,
                        operations = new[]
                        {
                            "COUNT", "MAX", "MEAN", "MIN", "SUM"
                        }
                    }
                }
            });
        var p8Owner = new WorkAssignmentBasicSummaryConfig
        {
            Id = lifecycle.ConfigId,
            WorkId = fixture.WorkId,
            AssignmentId = fixture.ScopeAssignmentId,
            DynamicFormTemplateId = lifecycle.DynamicFormVersionId,
            DefaultMethodsJson = "{}",
            RulesJson = "[]",
            VersionId = lifecycle.ConfigVersionId,
            VersionNo = lifecycle.ConfigVersionNo,
            Revision = lifecycle.ConfigRevision,
            Status = "LOCKED",
            ConfigHash = lifecycle.ConfigHash,
            ConfigJson = metricConfigurationJson,
            DependencyPins = [],
            Versions = [],
            LockedAtUtc = now,
            LockedByUserId = executor.Id,
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = executor.Id,
            UpdatedByUserId = executor.Id,
            IsDeleted = false
        };
        await database
            .GetCollection<WorkAssignmentBasicSummaryConfig>(
                "work_assignment_basic_summary_configs")
            .InsertOneAsync(p8Owner, cancellationToken: ct);
        RegisterCloseoutCaptureCleanup(
            "work_assignment_basic_summary_configs",
            p8Owner.Id);

        var assignments = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .Find(value =>
                (value.Id == fixture.ScopeAssignmentId ||
                 value.Id == fixture.SiblingAssignmentId) &&
                value.WorkId == fixture.WorkId &&
                value.DynamicFormTemplateId == lifecycle.DynamicFormVersionId &&
                value.IsActive &&
                !value.IsDeleted)
            .SortBy(value => value.Id)
            .ToListAsync(ct);
        HarnessAssert.True(
            assignments.Count == 2 &&
            assignments.Select(value => value.Id).ToHashSet(
                StringComparer.Ordinal).SetEquals(
                [fixture.ScopeAssignmentId, fixture.SiblingAssignmentId]),
            "P10-CLOSE FLOW_STEP basic assignment membership");
        var report = await database
            .GetCollection<WorkAssignmentReport>("work_assignment_report")
            .Find(value =>
                value.Id == lifecycle.SourceReportId &&
                value.WorkAssignmentId == fixture.ScopeAssignmentId &&
                value.IsActive &&
                !value.IsDeleted)
            .SingleAsync(ct);
        var sourceSignature = CloseoutBasicSourceSignature(
            assignments,
            [report]);
        var flowInstanceId = CloseoutRequiredObjectId(
            p9.FlowInstanceId,
            "P9 flow instance");
        var flowStepId = CloseoutRequiredText(
            p9.FlowStepId,
            "P9 flow step");
        var flowEffectiveStatus = CloseoutRequiredText(
            p9.FlowEffectiveStatus,
            "P9 flow effective status");
        var basicId = ObjectId.GenerateNewId().ToString();
        var basicRequestJson = StatRunCanonicalJson.CanonicalObject(new
        {
            dynamicFormTemplateId = lifecycle.DynamicFormVersionId,
            periodKey = lifecycle.PeriodKey,
            periodKeyFrom = (string?)null,
            periodKeyTo = (string?)null,
            periodScopeMode = "SINGLE_PERIOD",
            scopeAssignmentId = fixture.ScopeAssignmentId,
            sourceFlowBranchId = (string?)null,
            sourceFlowEffectiveStatus = flowEffectiveStatus,
            sourceFlowInstanceId = flowInstanceId,
            sourceFlowStepId = flowStepId,
            sourceScopeMode = "FLOW_STEP"
        });
        var basicSnapshotJson = StatRunCanonicalJson.CanonicalObject(new
        {
            meta = new
            {
                snapshotId = basicId,
                scopeAssignmentId = fixture.ScopeAssignmentId,
                dynamicFormTemplateId = lifecycle.DynamicFormVersionId,
                sourceScopeMode = "FLOW_STEP",
                sourceFlowInstanceId = flowInstanceId,
                sourceFlowStepId = flowStepId,
                sourceFlowBranchId = (string?)null,
                sourceFlowEffectiveStatus = flowEffectiveStatus,
                sourceSignatureHash = sourceSignature,
                configId = lifecycle.ConfigId,
                configVersionId = lifecycle.ConfigVersionId,
                configVersionNo = lifecycle.ConfigVersionNo,
                configRevision = lifecycle.ConfigRevision,
                configHash = lifecycle.ConfigHash,
                candidateChainId = lifecycle.CandidateChainId,
                candidatePromptId = "P9-04",
                candidateStage = 2,
                candidateCatalogRawSha256 =
                    "7955d4c0fb1aa03b5d28484b15f321752b16983996f3b5c5428d9192ff67a509",
                candidateCatalogSemanticSha256 =
                    "c7120bd77d338006e8df117c83718ee694aa407167a7ca214e2f71dc2f2da9f1",
                candidateStageLockSha256 =
                    "38d13a94dfe863625ae54396ceee6beccce9bf9de85cd4d26cb0e2e730fdf1ad"
            },
            fields = Array.Empty<object>(),
            tables = Array.Empty<object>()
        });
        var basic = new WorkAssignmentBasicSummarySnapshot
        {
            Id = basicId,
            WorkId = fixture.WorkId,
            ScopeAssignmentId = fixture.ScopeAssignmentId,
            DynamicFormTemplateId = lifecycle.DynamicFormVersionId,
            RequestHash = StatRunCanonicalJson.HashText(basicRequestJson),
            RequestJson = basicRequestJson,
            SourceScopeMode = "FLOW_STEP",
            SourceFlowInstanceId = flowInstanceId,
            SourceFlowStepId = flowStepId,
            SourceFlowBranchId = null,
            SourceFlowEffectiveStatus = flowEffectiveStatus,
            SourceAssignmentIds = assignments
                .Select(value => value.Id)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList(),
            SourceReportIds = [lifecycle.SourceReportId],
            SourceSignatureHash = sourceSignature,
            ConfigId = lifecycle.ConfigId,
            ConfigVersionId = lifecycle.ConfigVersionId,
            ConfigVersionNo = lifecycle.ConfigVersionNo,
            ConfigRevision = lifecycle.ConfigRevision,
            ConfigHash = lifecycle.ConfigHash,
            ConfigDependencyPins = [],
            CandidateChainId = lifecycle.CandidateChainId,
            CandidatePromptId = "P9-04",
            CandidateStage = 2,
            CandidateCatalogRawSha256 =
                "7955d4c0fb1aa03b5d28484b15f321752b16983996f3b5c5428d9192ff67a509",
            CandidateCatalogSemanticSha256 =
                "c7120bd77d338006e8df117c83718ee694aa407167a7ca214e2f71dc2f2da9f1",
            CandidateStageLockSha256 =
                "38d13a94dfe863625ae54396ceee6beccce9bf9de85cd4d26cb0e2e730fdf1ad",
            SnapshotJson = basicSnapshotJson,
            SnapshotDirty = false,
            SnapshotRefreshedAtUtc = now,
            RefreshStatus = WorkAssignmentBasicSummaryRefreshStatuses.Done,
            RefreshJobId = "p10-closeout-basic",
            RefreshCorrelationId = "p10-closeout-basic",
            RefreshQueuedAtUtc = now,
            RefreshStartedAtUtc = now,
            RefreshFinishedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = executor.Id,
            UpdatedByUserId = executor.Id,
            IsDeleted = false
        };
        await database
            .GetCollection<WorkAssignmentBasicSummarySnapshot>(
                "work_assignment_basic_summary_snapshots")
            .InsertOneAsync(basic, cancellationToken: ct);
        RegisterCloseoutCaptureCleanup(
            "work_assignment_basic_summary_snapshots",
            basic.Id);

        var periodMonth = DateTime.ParseExact(
            lifecycle.PeriodKey,
            "yyyy-MM",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);
        periodMonth = DateTime.SpecifyKind(periodMonth, DateTimeKind.Utc);
        var yearStart = new DateTime(
            periodMonth.Year,
            1,
            1,
            0,
            0,
            0,
            DateTimeKind.Utc);
        var dayKey = periodMonth.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);
        var monthKey = periodMonth.ToString(
            "yyyy-MM",
            CultureInfo.InvariantCulture);
        var yearKey = periodMonth.ToString(
            "yyyy",
            CultureInfo.InvariantCulture);
        var advancedConfigId = ObjectId.GenerateNewId().ToString();
        var advancedConfigVersionId = ObjectId.GenerateNewId().ToString();
        var advancedConfigHash = HashText(
            $"P10-CLOSE-ADVANCED-CONFIG:{_runKey}");
        var dayId = ObjectId.GenerateNewId().ToString();
        var monthId = ObjectId.GenerateNewId().ToString();
        var yearId = ObjectId.GenerateNewId().ToString();

        TNode AdvancedNode<TNode>(
            string id,
            string grain,
            string grainKey,
            DateTime windowStart,
            DateTime windowEnd,
            IReadOnlyList<string> inputNodeKeys,
            string? day,
            string? month,
            string? year)
            where TNode : WorkAssignmentAdvancedSummaryHierarchyNodeBase, new()
        {
            var valueJson = StatRunCanonicalJson.CanonicalObject(new
            {
                kind = $"ADVANCED_SUMMARY_{grain}_NODE_V1",
                schemaVersion = 1,
                generatedAtUtc = now,
                configId = grain == "DAY"
                    ? advancedConfigVersionId
                    : advancedConfigId,
                configHash = advancedConfigHash,
                grain,
                grainKey,
                dayKey = day,
                monthKey = month,
                yearKey = year,
                windowStartUtc = windowStart,
                windowEndExclusiveUtc = windowEnd,
                sourceScopeMode = "FLOW_STEP",
                sourceFlowInstanceId = flowInstanceId,
                sourceFlowStepId = flowStepId,
                sourceFlowBranchId = (string?)null,
                sourceFlowEffectiveStatus = flowEffectiveStatus,
                sourceAssignmentCount = 0,
                sourceReportCount = 0,
                sectionReportCount = 0,
                sectionFieldCount = 0,
                targetFieldCount = 0,
                inputNodeCount = inputNodeKeys.Count,
                warnings = Array.Empty<string>(),
                fields = Array.Empty<object>()
            });
            return new TNode
            {
                Id = id,
                WorkId = fixture.WorkId,
                AssignmentId = fixture.ScopeAssignmentId,
                DynamicFormTemplateId = lifecycle.DynamicFormVersionId,
                SectionId = CloseoutDirectAdvancedSectionId,
                ConfigId = advancedConfigId,
                ConfigVersionId = advancedConfigVersionId,
                ConfigVersionNo = 1,
                ConfigRevision = 1,
                ConfigHash = advancedConfigHash,
                DependencyPins = [],
                TimeAxis = "UTC_GREGORIAN",
                CandidateChainId = lifecycle.CandidateChainId,
                CandidatePromptId = "P9-05",
                CandidateStage = 3,
                CandidateCatalogRawSha256 =
                    "c3ebff7c0cfa4ce62003fb83e0cfc75ba9a9fd1419cc00b9df3836cf981085d3",
                CandidateCatalogSemanticSha256 =
                    "d0b33a7ed334f0412488618e1ed23375fc72657fa3509adeaceec76013460c0f",
                CandidateStageLockSha256 =
                    "505272c7c32544a7363d03c5e8b087bfcfac00aa3a7cef2e89ff7d45c7ecc5bc",
                Grain = grain,
                GrainKey = grainKey,
                WindowStartUtc = windowStart,
                WindowEndExclusiveUtc = windowEnd,
                Status = WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean,
                IsDirty = false,
                DirtyReason = null,
                SourceSignatureHash = HashText(
                    $"P10-CLOSE-ADVANCED-SOURCE:{grain}:{grainKey}:{_runKey}"),
                SourceReportCount = 0,
                SourceReportIds = [],
                InputNodeKeys = inputNodeKeys.ToList(),
                ValueJson = valueJson,
                ValueHash = HashText(valueJson),
                BuiltAtUtc = now,
                BuildJobId = "p10-closeout-advanced",
                BuildCorrelationId = "p10-closeout-advanced",
                BuildCommandId = "p10-closeout-advanced",
                BuildRequestHash = HashText(
                    $"P10-CLOSE-ADVANCED-REQUEST:{grain}:{grainKey}:{_runKey}"),
                BuildReceiptId = "p10-closeout-advanced",
                BuildAttemptNo = 1,
                FenceToken = 1,
                LeaseOwner = null,
                LeaseExpiresAtUtc = null,
                BuildError = null,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = executor.Id,
                UpdatedByUserId = executor.Id,
                IsDeleted = false
            };
        }

        var dayNode = AdvancedNode<WorkAssignmentAdvancedSummaryDayNode>(
            dayId,
            "DAY",
            dayKey,
            periodMonth,
            periodMonth.AddDays(1),
            [],
            dayKey,
            null,
            null);
        dayNode.DayKey = dayKey;
        var monthInputs = Enumerable.Range(
                0,
                DateTime.DaysInMonth(periodMonth.Year, periodMonth.Month))
            .Select(offset => periodMonth.AddDays(offset).ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture))
            .ToArray();
        var monthNode = AdvancedNode<WorkAssignmentAdvancedSummaryMonthNode>(
            monthId,
            "MONTH",
            monthKey,
            periodMonth,
            periodMonth.AddMonths(1),
            monthInputs,
            null,
            monthKey,
            yearKey);
        monthNode.MonthKey = monthKey;
        monthNode.YearKey = yearKey;
        var yearInputs = Enumerable.Range(1, 12)
            .Select(month => $"{yearKey}-{month:00}")
            .ToArray();
        var yearNode = AdvancedNode<WorkAssignmentAdvancedSummaryYearNode>(
            yearId,
            "YEAR",
            yearKey,
            yearStart,
            yearStart.AddYears(1),
            yearInputs,
            null,
            null,
            yearKey);
        yearNode.YearKey = yearKey;

        await database.GetCollection<WorkAssignmentAdvancedSummaryDayNode>(
                "work_assignment_advanced_summary_day_nodes")
            .InsertOneAsync(dayNode, cancellationToken: ct);
        await database.GetCollection<WorkAssignmentAdvancedSummaryMonthNode>(
                "work_assignment_advanced_summary_month_nodes")
            .InsertOneAsync(monthNode, cancellationToken: ct);
        await database.GetCollection<WorkAssignmentAdvancedSummaryYearNode>(
                "work_assignment_advanced_summary_year_nodes")
            .InsertOneAsync(yearNode, cancellationToken: ct);
        RegisterCloseoutCaptureCleanup(
            "work_assignment_advanced_summary_day_nodes",
            dayId);
        RegisterCloseoutCaptureCleanup(
            "work_assignment_advanced_summary_month_nodes",
            monthId);
        RegisterCloseoutCaptureCleanup(
            "work_assignment_advanced_summary_year_nodes",
            yearId);

        var diffResultId = ObjectId.GenerateNewId().ToString();
        var diffRunId = ObjectId.GenerateNewId().ToString();
        var diffConfigId = ObjectId.GenerateNewId().ToString();
        var diffConfigVersionId = ObjectId.GenerateNewId().ToString();
        var diffConfigHash = HashText(
            $"P10-CLOSE-DIFF-CONFIG:{_runKey}");
        var diffPeriod = StatRunCanonicalJson.CanonicalObject(new
        {
            mode = "EXACT",
            periodKey = lifecycle.PeriodKey,
            periodKeyFrom = (string?)null,
            periodKeyTo = (string?)null
        });
        var diff = new WorkReportStatisticDiffResult
        {
            Id = diffResultId,
            RunId = diffRunId,
            WorkId = fixture.WorkId,
            AssignmentId = fixture.ScopeAssignmentId,
            DynamicFormTemplateId = lifecycle.DynamicFormVersionId,
            ConfigId = diffConfigId,
            ConfigVersionId = diffConfigVersionId,
            ConfigVersionNo = 1,
            ConfigRevision = 1,
            ConfigHash = diffConfigHash,
            DependencyPins = [],
            CandidateChainId = lifecycle.CandidateChainId,
            CandidatePromptId = "P9-06",
            CandidateStage = 4,
            CandidateCatalogRawSha256 =
                "b26b24d1bdf9337d85c3ab01c700e357b8a56080f2e808332d1ba612bceb9c68",
            CandidateCatalogSemanticSha256 =
                "b4de97a6975b94e4a4da4b7148844ba846af837d283f0e0065762aad10ffdb36",
            CandidateStageLockSha256 =
                "e237f0e260f0ba2704ccb830a9b3aca1bceb7c88eaca52dc679f6b08b9126397",
            LeftConceptKind = "FIELD",
            LeftConceptKey = CloseoutDirectMetricId,
            LeftConceptCode = CloseoutDirectMetricId,
            LeftDataType = "NUMBER",
            LeftPeriodJson = diffPeriod,
            RightConceptKind = "FIELD",
            RightConceptKey = CloseoutDirectMetricId,
            RightConceptCode = CloseoutDirectMetricId,
            RightDataType = "NUMBER",
            RightPeriodJson = diffPeriod,
            Direction = "LEFT_TO_RIGHT",
            MissingPolicy = "INCLUDE",
            EmptyPolicy = "INCLUDE",
            TimeAxis = "UTC_GREGORIAN",
            CommandId = "p10-closeout-diff",
            RequestHash = HashText($"P10-CLOSE-DIFF-REQUEST:{_runKey}"),
            ReceiptId = "p10-closeout-diff",
            RequestedByUserId = executor.Id,
            Status = P9StatisticDiffResultStatuses.Completed,
            JobId = "p10-closeout-diff",
            AttemptNo = 1,
            FenceToken = 1,
            SourcePins = [],
            Rows = [],
            TotalRowCount = 0,
            EqualRowCount = 0,
            ChangedRowCount = 0,
            IsCurrent = true,
            IsFresh = true,
            IsDirty = false,
            FailureCode = null,
            FailureMessage = null,
            CompletedAtUtc = now,
            ExpiresAtUtc = null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = executor.Id,
            UpdatedByUserId = executor.Id,
            IsDeleted = false
        };
        diff.ResultHash = StatisticReconciliationCanonicalJson.HashObject(new
        {
            diff.ConfigVersionId,
            diff.ConfigHash,
            diff.Direction,
            diff.MissingPolicy,
            diff.EmptyPolicy,
            rows = diff.Rows
        });
        await database.GetCollection<WorkReportStatisticDiffResult>(
                "work_report_statistic_diff_results")
            .InsertOneAsync(diff, cancellationToken: ct);
        RegisterCloseoutCaptureCleanup(
            "work_report_statistic_diff_results",
            diff.Id);

        var exportResponse = await RequireApi().PostAsync(
            "api/stat-runs/exports",
            new
            {
                commandId = "p10-closeout-direct-export-001",
                format = "CSV",
                resultKind = "DIRECT_FIELD",
                workId = fixture.WorkId,
                scopeType = "ASSIGNMENT",
                scopeId = fixture.ScopeAssignmentId,
                periodInstanceKey = lifecycle.PeriodInstanceKey,
                resultId = lifecycle.P9RunId,
                expectedResultHash = lifecycle.P9GenerationHash,
                expectedConfigHash = lifecycle.ConfigHash,
                expectedSourceHash = lifecycle.SourcePayloadHash,
                expectedLifecycleRevision = lifecycle.SourceLifecycleRevision,
                filters = new
                {
                    dynamicFormTemplateId = lifecycle.DynamicFormVersionId,
                    fieldId = CloseoutDirectFieldId,
                    fieldKey = CloseoutDirectMetricId,
                    blockId = (string?)null,
                    metricKey = (string?)null,
                    labelCode = (string?)null,
                    periodKey = lifecycle.PeriodKey,
                    bucketKey = (string?)null
                }
            },
            executor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            exportResponse,
            HttpStatusCode.Created,
            "P10-CLOSE production DIRECT export");
        var exportId = RequireResponseString(exportResponse, "exportId");
        HarnessAssert.True(
            exportId.Length == 24 &&
            exportId.All(character => character is
                (>= '0' and <= '9') or (>= 'a' and <= 'f')),
            "P10-CLOSE production export canonical ObjectId24 id");
        var exportRoot = Path.GetFullPath(Path.Combine(
            CloseoutWorkspacePath("tdtd-be"),
            ".build",
            "stat-run-exports",
            RequireMongo().DatabaseName));
        var exportArtifactDirectory =
            TrackCloseoutProductionExportArtifactDirectory(
                exportRoot,
                exportId);
        var export = await database
            .GetCollection<StatRunExportArtifact>(
                "work_report_statistic_exports")
            .Find(value => value.Id == exportId && !value.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.True(
            export.Status == StatRunExportStatuses.Completed &&
            export.RowCount == 1 &&
            export.ColumnCount > 1 &&
            export.ResultKind == StatRunExportResultKinds.DirectField &&
            export.ResultId == lifecycle.P9RunId &&
            export.ResultHash == lifecycle.P9GenerationHash &&
            export.ConfigHash == lifecycle.ConfigHash &&
            export.SourceHash == lifecycle.SourcePayloadHash &&
            export.LifecycleRevision == lifecycle.SourceLifecycleRevision &&
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                export.ColumnManifestSha256),
            "P10-CLOSE production export Mongo binding");
        RegisterCloseoutCaptureCleanup(
            "work_report_statistic_exports",
            export.Id);

        var exportPath = CloseoutResolveOwnedExportPath(
            exportRoot,
            export.StorageKey);
        var resolvedExportDirectory = Path.GetDirectoryName(exportPath) ??
            throw new InvalidOperationException(
                "P10-CLOSE export artifact has no directory.");
        HarnessAssert.Equal(
            exportArtifactDirectory,
            resolvedExportDirectory,
            "P10-CLOSE tracked production export directory");
        var exportBytes = await File.ReadAllBytesAsync(exportPath, ct);
        HarnessAssert.Equal(
            export.ByteCount,
            exportBytes.LongLength,
            "P10-CLOSE production export byte count");
        HarnessAssert.Equal(
            export.ContentHash,
            HashBytes(exportBytes),
            "P10-CLOSE production export content hash");

        var capturePlan = new JsonObject
        {
            ["boundaryRegistryVersion"] =
                StatisticReconciliationActualCapturePlanIntegrity
                    .BoundaryRegistryVersion,
            ["basicSnapshotId"] = basic.Id,
            ["basicMode"] = "FLOW_STEP",
            ["advancedSectionId"] = CloseoutDirectAdvancedSectionId,
            ["advancedDayNodeIds"] = new JsonArray(dayId),
            ["advancedMonthNodeIds"] = new JsonArray(monthId),
            ["advancedYearNodeIds"] = new JsonArray(yearId),
            ["diffResultId"] = diff.Id,
            ["diffRunId"] = diff.RunId,
            ["apiSurface"] = "DIRECT_FIELD",
            ["apiOwnerResultId"] = lifecycle.P9RunId,
            ["exportId"] = export.Id
        };
        _closeoutCaptureOwnerFixture = new P10CloseoutCaptureOwnerFixture(
            p8Owner.Id,
            basic.Id,
            dayNode.Id,
            monthNode.Id,
            yearNode.Id,
            diff.Id,
            diff.RunId,
            export.Id,
            exportRoot,
            exportPath,
            lifecycle.PeriodKey,
            lifecycle.PeriodInstanceKey,
            capturePlan);
        return _closeoutCaptureOwnerFixture;
    }

    private static void ApplyCloseoutMatchedCapturePlan(
        JsonObject request,
        P10CloseoutCaptureOwnerFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fixture);
        request["filter"] = new JsonObject
        {
            ["periodInstanceKey"] = fixture.PeriodInstanceKey,
            ["fieldId"] = CloseoutDirectFieldId,
            ["fieldKey"] = CloseoutDirectMetricId,
            ["bucketKey"] = null,
            ["periodKey"] = fixture.PeriodKey
        };
        request["actualCapturePlan"] =
            fixture.ActualCapturePlanRequest.DeepClone();
    }

    private void RegisterCloseoutCaptureCleanup(
        string collection,
        string id)
    {
        var handle = new P10CleanupHandle(collection, id);
        if (!_cleanupHandles.Contains(handle))
            _cleanupHandles.Add(handle);
    }

    private static string CloseoutBasicSourceSignature(
        IEnumerable<WorkAssignment> assignments,
        IEnumerable<WorkAssignmentReport> reports)
    {
        var payload = JsonSerializer.Serialize(new
        {
            assignments = assignments
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .Select(value => new
                {
                    value.Id,
                    value.UpdatedAtUtc,
                    value.IsActive,
                    assignees = value.Assignees
                        .Select(assignee => new
                        {
                            assignee.UserId,
                            assignee.UnitId
                        })
                        .OrderBy(assignee => assignee.UserId,
                            StringComparer.Ordinal)
                        .ThenBy(assignee => assignee.UnitId,
                            StringComparer.Ordinal)
                        .ToList()
                })
                .ToList(),
            reports = reports
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .Select(value => new
                {
                    value.Id,
                    value.WorkAssignmentId,
                    value.WorkReportPeriodId,
                    value.Status,
                    value.IsCurrent,
                    value.IsActive,
                    value.PeriodKey,
                    value.PeriodInstanceKey,
                    value.PeriodKind,
                    value.PeriodStart,
                    value.PeriodEnd,
                    value.DynamicFormTemplateId,
                    value.CumulativeContributionMode,
                    value.PayloadRevision,
                    value.PayloadHash,
                    value.PayloadUpdatedAtUtc,
                    value.UpdatedAtUtc,
                    value.AggregateSnapshotDirty,
                    value.AggregateSnapshotRefreshedAtUtc
                })
                .ToList()
        }, CloseoutOwnerJsonOptions);
        return HashText(payload);
    }

    private static string CloseoutResolveOwnedExportPath(
        string exportRoot,
        string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey) ||
            Path.IsPathRooted(storageKey) ||
            storageKey.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "P10-CLOSE export storage key is not owned.");
        }
        var root = Path.GetFullPath(exportRoot)
            .TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(
            root,
            storageKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "P10-CLOSE export path escaped the owned root.");
        }
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists ||
            rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                "P10-CLOSE export root is missing or redirected.");
        }
        for (var current = new FileInfo(path).Directory;
             current is not null &&
             current.FullName.StartsWith(
                 root,
                 StringComparison.OrdinalIgnoreCase);
             current = current.Parent)
        {
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException(
                    "P10-CLOSE export ancestor is redirected.");
            if (string.Equals(
                    current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    root,
                    StringComparison.OrdinalIgnoreCase))
                break;
        }
        return path;
    }

    private static string CloseoutRequiredObjectId(
        string? value,
        string name)
        => value is not null &&
           ObjectId.TryParse(value, out var parsed) &&
           parsed.ToString() == value
            ? value
            : throw new InvalidOperationException(
                $"P10-CLOSE {name} is not a canonical ObjectId.");

    private static string CloseoutRequiredText(
        string? value,
        string name)
        => !string.IsNullOrWhiteSpace(value) &&
           value == value.Trim() &&
           !value.Any(char.IsControl)
            ? value
            : throw new InvalidOperationException(
                $"P10-CLOSE {name} is invalid.");
}

internal sealed record P10CloseoutCaptureOwnerFixture(
    string P8OwnerId,
    string BasicSnapshotId,
    string AdvancedDayNodeId,
    string AdvancedMonthNodeId,
    string AdvancedYearNodeId,
    string DiffResultId,
    string DiffRunId,
    string ExportId,
    string ExportStorageRoot,
    string ExportStoragePath,
    string PeriodKey,
    string PeriodInstanceKey,
    JsonObject ActualCapturePlanRequest);
