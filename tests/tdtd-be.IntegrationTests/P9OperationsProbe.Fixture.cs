using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task PrepareOperationsSingleReportRuntimeFixtureAsync(
        CancellationToken ct)
    {
        var periods = RequireDatabase()
            .GetCollection<BsonDocument>("work_report_periods");
        var pairedPeriodId = ObjectId.Parse(Fixture().PairedReportPeriodId);
        var assignmentId = ObjectId.Parse(Fixture().AssignmentId);
        var before = await periods
            .Find(new BsonDocument("_id", pairedPeriodId))
            .SingleAsync(ct);
        HarnessAssert.Equal(
            Fixture().AssignmentId,
            BsonText(before, "workAssignmentId"),
            "P9-OPS paired period assignment");
        HarnessAssert.Equal(
            "MONTH:2026-08:B",
            BsonText(before, "periodInstanceKey"),
            "P9-OPS paired period identity");
        HarnessAssert.True(
            before.GetValue("isActive", false).AsBoolean,
            "P9-OPS paired period must start active.");

        var filter = new BsonDocument
        {
            ["_id"] = pairedPeriodId,
            ["workAssignmentId"] = assignmentId,
            ["periodInstanceKey"] = "MONTH:2026-08:B",
            ["isActive"] = true,
            ["isDeleted"] = false
        };
        if (before.TryGetValue("updatedAtUtc", out var updatedAtUtc))
            filter["updatedAtUtc"] = updatedAtUtc.DeepClone();
        var inactivated = await periods.UpdateOneAsync(
            filter,
            Builders<BsonDocument>.Update
                .Set("isActive", false)
                .Set("updatedAtUtc", DateTime.UtcNow),
            cancellationToken: ct);
        HarnessAssert.Equal(
            1L,
            inactivated.MatchedCount,
            "P9-OPS paired period CAS match");
        HarnessAssert.Equal(
            1L,
            inactivated.ModifiedCount,
            "P9-OPS paired period CAS update");

        var activePeriods = await periods
            .Find(new BsonDocument
            {
                ["workAssignmentId"] = assignmentId,
                ["isActive"] = true,
                ["isDeleted"] = false
            })
            .ToListAsync(ct);
        HarnessAssert.Equal(
            1,
            activePeriods.Count,
            "P9-OPS single active report period");
        HarnessAssert.Equal(
            Fixture().ReportPeriodId,
            BsonText(activePeriods[0], "_id"),
            "P9-OPS authoritative active report period");
    }

    private async Task AssertOperationsFoundationExactReportPinAsync(
        CancellationToken ct)
    {
        var step = await RequireDatabase()
            .GetCollection<BsonDocument>("dynamic_flow_step_instances")
            .Find(new BsonDocument(
                "_id",
                ObjectId.Parse(Fixture().FlowStepInstanceId)))
            .SingleAsync(ct);
        HarnessAssert.Equal(
            Fixture().ReportId,
            BsonText(step, "reportId"),
            "P9-OPS Foundation exact flow-step report owner pin");
        var report = await LoadLifecycleReportAsync(ct);
        HarnessAssert.Equal(
            BsonInt(report, "lifecycleRevision"),
            BsonInt(step, "reportLifecycleRevision"),
            "P9-OPS Foundation exact flow-step lifecycle revision");
        HarnessAssert.Equal(
            "APPROVED",
            BsonString(step, "reportLifecycleStatus"),
            "P9-OPS Foundation flow-step lifecycle status");
        HarnessAssert.True(
            step.GetValue("reportLifecycleIsActive", false).AsBoolean,
            "P9-OPS Foundation flow-step lifecycle active pin");
    }

    private async Task PrepareOperationsApprovedIncludeFixtureAsync(
        CancellationToken ct)
    {
        var save = await RequireApi().PutAsync(
            $"api/work-assignment-reports/{Fixture().ReportId}/draft",
            _lfcSaveRequest!.DeepClone(),
            Actor("executor").Token,
            ct: ct);
        ExpectSuccess(save, "P9-OPS mapped draft save");
        await SeedCanonicalP7MappingLineageAsync(
            ct,
            "p9-ops-p7-apply-fixture-001");

        var report = await LoadLifecycleReportAsync(ct);
        _lfcSubmitRequest = new JsonObject
        {
            ["expectedPayloadRevision"] = BsonInt(report, "payloadRevision"),
            ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
            ["commandId"] = "p9-ops-submit-001"
        };
        var submit = await RequireApi().PostAsync(
            $"api/work-assignment-reports/{Fixture().ReportId}/submit",
            _lfcSubmitRequest.DeepClone(),
            Actor("executor").Token,
            ct: ct);
        ExpectSuccess(submit, "P9-OPS submit");
        await ProcessLifecycleOutboxAsync(20, ct);

        await RequireDatabase().GetCollection<BsonDocument>("work_assignments")
            .UpdateOneAsync(
                new BsonDocument("_id", ObjectId.Parse(Fixture().AssignmentId)),
                Builders<BsonDocument>.Update.Set(
                    "createdByUserId",
                    ObjectId.Parse(Actor("admin").Id)),
                cancellationToken: ct);
        report = await LoadLifecycleReportAsync(ct);
        _lfcApproveRequest = new JsonObject
        {
            ["expectedPayloadRevision"] = BsonInt(report, "payloadRevision"),
            ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
            ["commandId"] = BackendServerLease.P3LifecycleProjectionFailureCommandId,
            ["comment"] = "P9-OPS approved INCLUDE fixture"
        };
        var approve = await RequireApi().PostAsync(
            $"api/work-assignment-review/reports/{Fixture().ReportId}/approve",
            _lfcApproveRequest.DeepClone(),
            Actor("admin").Token,
            ct: ct);
        ExpectSuccess(approve, "P9-OPS approve EXCLUDE boundary");
        var excluded = await DrainFlowApprovalStateAsync("ZERO_WRITE", ct);
        _lfcPublishedApproveEntry = (BsonDocument)excluded.Entry.DeepClone();
        _opsApprovalEventKey = _flwApprovalEventKey = BsonString(
            excluded.Entry,
            "entryKey");

        await SwitchToLockedIncludeVersionAsync(ct);
        await ReopenApproveEntryAsync(_lfcPublishedApproveEntry, null, ct);
        await Task.WhenAll(
            ProcessLifecycleOutboxAsync(20, ct),
            ProcessLifecycleOutboxAsync(20, ct));
        var included = await DrainFlowApprovalStateAsync("PUBLISHED", ct);
        _lfcPublishedApproveEntry = (BsonDocument)included.Entry.DeepClone();
        _lfcRunId = _flwRunId = BsonString(included.Entry, "directProjectionRunId");
        _lfcGenerationId = _flwGenerationId = BsonString(
            included.Entry,
            "directProjectionGenerationId");
        _lfcGenerationHash = BsonString(
            included.Entry,
            "directProjectionGenerationHash");
        _lfcApproveEventKey = _opsApprovalEventKey;
        _opsApprovedSnapshot = _flwIncludedSnapshot =
            await CaptureLifecycleDirectSnapshotAsync(ct);
        await AssertFlowPublishedGenerationAsync(_opsApprovedSnapshot, ct);
        _opsApprovedJob = _flwPublishedJob = await LoadLifecycleJobAsync(ct);
        _flwLedgerHash = BsonString(_opsApprovedJob, "flowContributionLedgerHash");
        _flwReversalBaselineHash = BsonString(
            _opsApprovedJob,
            "flowContributionReversalBaselineHash");
        HarnessAssert.True(IsCanonicalSha(_flwLedgerHash), "P9-OPS approved ledger hash");
        HarnessAssert.True(
            IsCanonicalSha(_flwReversalBaselineHash),
            "P9-OPS reversal baseline hash");
        var approvedHash = LifecycleSnapshotSha256(_opsApprovedSnapshot);
        _opsLifecycleTrace.Add(new P9OperationsLifecycleTrace(
            "FIXTURE_APPROVED_INCLUDE",
            "REVIEW_APPROVE",
            _opsApprovalEventKey,
            BsonInt(await LoadLifecycleReportAsync(ct), "lifecycleRevision"),
            approvedHash,
            approvedHash,
            _flwRunId,
            _flwGenerationId,
            $"http={(int)approve.StatusCode};ledger={_flwLedgerHash};targets={BsonInt(_opsApprovedJob, "flowContributionTargetCount")}"));
        _opsApprovedGenerationRowsHash = await CaptureOperationsGenerationRowsHashAsync(
            _flwGenerationId,
            ct);
        await SeedOperationsDependentFreshnessMarkersAsync(ct);
    }

    private async Task<JsonObject> BuildOperationsFoundationCreateRequestAsync(
        string commandId,
        CancellationToken ct)
    {
        var report = await LoadLifecycleReportAsync(ct);
        return new JsonObject
        {
            ["commandId"] = commandId,
            ["workId"] = Fixture().WorkId,
            ["scopeType"] = "ASSIGNMENT",
            ["scopeId"] = Fixture().AssignmentId,
            ["sourceReportId"] = Fixture().ReportId,
            ["dynamicFormTemplateId"] = Fixture().TemplateId,
            ["expectedConfigRevision"] = Fixture().ConfigRevision,
            ["expectedConfigHash"] = Fixture().ConfigHash,
            ["expectedSourceRevision"] = BsonInt(report, "payloadRevision"),
            ["expectedSourceHash"] = BsonString(report, "payloadHash"),
            ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
            ["period"] = new JsonObject
            {
                ["periodKey"] = BsonString(report, "periodKey"),
                ["periodInstanceKey"] = BsonString(report, "periodInstanceKey"),
                ["periodKind"] = BsonString(report, "periodKind"),
                ["periodStart"] = report.GetValue("periodStart", BsonNull.Value).IsBsonNull
                    ? null
                    : report["periodStart"].ToUniversalTime(),
                ["periodEnd"] = report.GetValue("periodEnd", BsonNull.Value).IsBsonNull
                    ? null
                    : report["periodEnd"].ToUniversalTime()
            }
        };
    }

    private async Task<BsonDocument[]> LoadOperationsJobsAsync(CancellationToken ct)
    {
        var jobs = await RequireDatabase()
            .GetCollection<BsonDocument>(LifecycleJobCollection)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);
        return jobs.ToArray();
    }

    private static string OperationsSecretSafeHash(string value)
        => HashBytes(System.Text.Encoding.UTF8.GetBytes(value));

    private static void ExpectOperationsSuccess(
        ApiHarnessResponse response,
        string context)
    {
        HarnessAssert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted,
            $"{context} expected 200/202. Actual={(int)response.StatusCode}; Body={response.Body}");
    }

    private async Task<BsonDocument> DrainOperationsLifecycleEntryAsync(
        string operation,
        string expectedDirectState,
        string? commandId,
        CancellationToken ct)
    {
        BsonDocument? observed = null;
        for (var attempt = 0; attempt <= 10; attempt++)
        {
            var report = await LoadLifecycleReportAsync(ct);
            observed = report
                .GetValue("lifecycleProjectionOutbox", new BsonArray())
                .AsBsonArray
                .Select(value => value.AsBsonDocument)
                .Where(entry => string.Equals(
                    BsonNullableString(entry, "operation"),
                    operation,
                    StringComparison.Ordinal))
                .Where(entry => commandId is null || string.Equals(
                    BsonNullableString(entry, "commandId"),
                    commandId,
                    StringComparison.Ordinal))
                .OrderByDescending(entry => BsonInt(entry, "lifecycleRevision"))
                .ThenByDescending(entry => BsonString(entry, "entryKey"), StringComparer.Ordinal)
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"P9-OPS lifecycle entry {operation}/{commandId ?? "*"} is missing.");
            if (string.Equals(
                    BsonNullableString(observed, "directProjectionState"),
                    expectedDirectState,
                    StringComparison.Ordinal))
            {
                return (BsonDocument)observed.DeepClone();
            }

            if (attempt < 10)
                await ProcessLifecycleOutboxAsync(20, ct);
        }

        throw new InvalidOperationException(
            $"P9-OPS lifecycle entry {operation} did not reach {expectedDirectState}. " +
            $"state={BsonNullableString(observed!, "state") ?? "missing"};" +
            $"direct={BsonNullableString(observed!, "directProjectionState") ?? "missing"};" +
            $"error={BsonNullableString(observed!, "lastError") ?? "none"}");
    }

    private async Task<BsonDocument> LoadCurrentOperationsPublicationAsync(
        CancellationToken ct)
    {
        var baseline = HarnessAssert.Required(
            _opsApprovedJob,
            "P9-OPS approved publication");
        var scopeKey = BsonString(baseline, "publicationScopeKey");
        var current = await RequireDatabase()
            .GetCollection<BsonDocument>(LifecycleJobCollection)
            .Find(new BsonDocument
            {
                ["publicationScopeKey"] = scopeKey,
                ["runKind"] = "LIFECYCLE_DIRECT_PROJECTION",
                ["status"] = "COMPLETED",
                ["isCurrentPublication"] = true,
                ["isDeleted"] = false
            })
            .ToListAsync(ct);
        HarnessAssert.Equal(1, current.Count, "P9-OPS current publication count");
        return current[0];
    }

    private async Task<string> CaptureOperationsGenerationRowsHashAsync(
        string? generationId,
        CancellationToken ct)
    {
        HarnessAssert.True(IsCanonicalSha(generationId), "P9-OPS generation hash pin");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var collectionName in LifecycleDirectCollections
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            var rows = await RequireDatabase()
                .GetCollection<BsonDocument>(collectionName)
                .Find(new BsonDocument(
                    "directProjection.generationId",
                    generationId))
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                var bytes = row.ToBson();
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes(
                    $"{collectionName}:{bytes.Length}:"));
                hash.AppendData(bytes);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task SeedOperationsDependentFreshnessMarkersAsync(
        CancellationToken ct)
    {
        var reportId = ObjectId.Parse(Fixture().ReportId);
        var workId = ObjectId.Parse(Fixture().WorkId);
        var now = DateTime.UtcNow;
        var pins = LoadOperationsCandidatePins();
        var directRunId = ObjectId.Parse(_flwRunId!);
        var common = new BsonDocument
        {
            ["workId"] = workId,
            ["dynamicFormTemplateId"] = ObjectId.Parse(Fixture().TemplateId),
            ["candidateChainId"] = ChainId,
            ["candidatePromptId"] = OperationsPromptId,
            ["candidateStage"] = 6,
            ["candidateCatalogRawSha256"] = pins.CatalogRawSha256,
            ["candidateCatalogSemanticSha256"] = pins.CatalogSemanticSha256,
            ["candidateStageLockSha256"] = pins.StageLockSha256,
            ["createdAtUtc"] = now,
            ["updatedAtUtc"] = now,
            ["createdByUserId"] = ObjectId.Parse(Actor("admin").Id),
            ["updatedByUserId"] = ObjectId.Parse(Actor("admin").Id),
            ["isDeleted"] = false
        };

        var basicId = ObjectId.GenerateNewId();
        var basic = (BsonDocument)common.DeepClone();
        basic["_id"] = basicId;
        basic["scopeAssignmentId"] = ObjectId.Parse(Fixture().AssignmentId);
        basic["sourceAssignmentIds"] = new BsonArray
        {
            ObjectId.Parse(Fixture().AssignmentId),
            Fixture().AssignmentId
        };
        basic["sourceReportIds"] = new BsonArray
        {
            reportId,
            Fixture().ReportId
        };
        basic["sourceSignatureHash"] = StatRunCanonicalJson.HashText(
            $"P9-OPS-BASIC\n{Fixture().ReportId}\n{_flwGenerationId}");
        basic["snapshotDirty"] = false;
        basic["refreshStatus"] = "DONE";
        await RequireDatabase()
            .GetCollection<BsonDocument>("work_assignment_basic_summary_snapshots")
            .InsertOneAsync(basic, cancellationToken: ct);
        _opsDirtyMarkerIds["BASIC"] = basicId.ToString();

        foreach (var (kind, collection) in new[]
                 {
                     ("ADVANCED_DAY", "work_assignment_advanced_summary_day_nodes"),
                     ("ADVANCED_MONTH", "work_assignment_advanced_summary_month_nodes"),
                     ("ADVANCED_YEAR", "work_assignment_advanced_summary_year_nodes")
                 })
        {
            var id = ObjectId.GenerateNewId();
            var advanced = (BsonDocument)common.DeepClone();
            advanced["_id"] = id;
            advanced["assignmentId"] = ObjectId.Parse(Fixture().AssignmentId);
            advanced["sourceReportIds"] = new BsonArray { reportId };
            advanced["isDirty"] = false;
            advanced["dirtyReason"] = BsonNull.Value;
            advanced["status"] = "DONE";
            await RequireDatabase()
                .GetCollection<BsonDocument>(collection)
                .InsertOneAsync(advanced, cancellationToken: ct);
            _opsDirtyMarkerIds[kind] = id.ToString();
        }

        var diffId = ObjectId.GenerateNewId();
        var diff = (BsonDocument)common.DeepClone();
        diff["_id"] = diffId;
        diff["runId"] = directRunId;
        diff["sourcePins"] = new BsonArray
        {
            new BsonDocument
            {
                ["side"] = "LEFT",
                ["sourceReportId"] = reportId,
                ["directRunId"] = directRunId,
                ["directGenerationId"] = _flwGenerationId
            }
        };
        diff["isCurrent"] = true;
        diff["isFresh"] = true;
        diff["isDirty"] = false;
        diff["status"] = "COMPLETED";
        await RequireDatabase()
            .GetCollection<BsonDocument>("work_report_statistic_diff_results")
            .InsertOneAsync(diff, cancellationToken: ct);
        _opsDirtyMarkerIds["DIFF"] = diffId.ToString();
    }

    private async Task<BsonDocument[]> LoadOperationsDirtyMarkersAsync(
        CancellationToken ct)
    {
        var documents = new List<BsonDocument>();
        foreach (var (kind, collection) in new[]
                 {
                     ("BASIC", "work_assignment_basic_summary_snapshots"),
                     ("ADVANCED_DAY", "work_assignment_advanced_summary_day_nodes"),
                     ("ADVANCED_MONTH", "work_assignment_advanced_summary_month_nodes"),
                     ("ADVANCED_YEAR", "work_assignment_advanced_summary_year_nodes"),
                     ("DIFF", "work_report_statistic_diff_results")
                 })
        {
            var id = ObjectId.Parse(_opsDirtyMarkerIds[kind]);
            var document = await RequireDatabase()
                .GetCollection<BsonDocument>(collection)
                .Find(new BsonDocument("_id", id))
                .SingleAsync(ct);
            document["_opsKind"] = kind;
            documents.Add(document);
        }
        return documents.ToArray();
    }
}
