using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task RunOperationsCasesAsync(CancellationToken ct)
    {
        await RunOperationsReversalCasesAsync(ct);
        await RunOperationsRetryCasesAsync(ct);
        await RunOperationsResetCasesAsync(ct);
        await RunOperationsDiagnosticCasesAsync(ct);
        await RunOperationsCleanupCasesAsync(ct);
    }

    private async Task RunOperationsReversalCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-OPS-REVERSAL-01",
            async () =>
            {
                var approved = HarnessAssert.Required(
                    _opsApprovedSnapshot,
                    "approved INCLUDE snapshot");
                var report = await LoadLifecycleReportAsync(ct);
                _opsRecallRequest = new JsonObject
                {
                    ["expectedPayloadRevision"] = BsonInt(report, "payloadRevision"),
                    ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
                    ["commandId"] = "p9-ops-recall-001",
                    ["comment"] = "P9-08 canonical approved contribution recall"
                };
                var recall = await RequireApi().PostAsync(
                    $"api/work-assignment-review/reports/{Fixture().ReportId}/recall-approved",
                    _opsRecallRequest.DeepClone(),
                    Actor("admin").Token,
                    ct: ct);
                ExpectOperationsSuccess(recall, "P9-OPS recall approved");
                var entry = await DrainOperationsLifecycleEntryAsync(
                    "REVIEW_RECALL_APPROVED",
                    "PUBLISHED",
                    "p9-ops-recall-001",
                    ct);
                _opsReversalEventKey = BsonString(entry, "entryKey");
                _opsReversedSnapshot = await CaptureLifecycleDirectSnapshotAsync(ct);
                _opsReversalJob = await LoadCurrentOperationsPublicationAsync(ct);
                HarnessAssert.Equal(
                    _opsReversalEventKey,
                    BsonString(_opsReversalJob, "sourceLifecycleEventKey"),
                    "reversal current event");
                HarnessAssert.Equal(
                    "REVIEW_RECALL_APPROVED",
                    BsonString(
                        _opsReversalJob.GetValue("reversalAudit").AsBsonDocument,
                        "operation"),
                    "reversal audit operation");
                _opsLifecycleTrace.Add(new P9OperationsLifecycleTrace(
                    "RECALL_PUBLISHED",
                    "REVIEW_RECALL_APPROVED",
                    _opsReversalEventKey,
                    BsonInt(await LoadLifecycleReportAsync(ct), "lifecycleRevision"),
                    LifecycleSnapshotSha256(approved),
                    LifecycleSnapshotSha256(_opsReversedSnapshot),
                    BsonString(_opsReversalJob, "_id"),
                    BsonString(_opsReversalJob, "generationId"),
                    $"http={(int)recall.StatusCode};priorRun={_flwRunId};memberCount={BsonLong(_opsReversalJob, "totalReportCount")}"));
                return new CaseObservation(
                    "Recall committed through Kestrel and the durable lifecycle event published one canonical successor generation.",
                    $"event={_opsReversalEventKey};run={BsonString(_opsReversalJob, "_id")};generation={BsonString(_opsReversalJob, "generationId")};members={BsonLong(_opsReversalJob, "totalReportCount")}");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-REVERSAL-02",
            async () =>
            {
                var reversal = HarnessAssert.Required(
                    _opsReversalJob,
                    "reversal publication");
                var audit = reversal.GetValue("reversalAudit").AsBsonDocument;
                HarnessAssert.Equal(_flwRunId, BsonString(audit, "priorRunId"), "prior run lineage");
                HarnessAssert.Equal(_flwGenerationId, BsonString(audit, "priorGenerationId"), "prior generation lineage");
                HarnessAssert.Equal(_flwLedgerHash, BsonString(audit, "priorLedgerHash"), "prior contribution ledger");
                HarnessAssert.Equal(_flwReversalBaselineHash, BsonString(audit, "priorReversalBaselineHash"), "prior reversal baseline");
                HarnessAssert.True(IsCanonicalSha(BsonString(audit, "auditHash")), "reversal audit hash");
                HarnessAssert.Equal(0L, BsonLong(reversal, "totalReportCount"), "reversal member count");
                HarnessAssert.Equal(0, BsonInt(reversal, "flowContributionSourceCount"), "reversal source audit count");
                HarnessAssert.Equal(0, BsonInt(reversal, "flowContributionTargetCount"), "reversal target audit count");
                var digests = reversal.GetValue("directStoreDigests").AsBsonArray
                    .Select(value => value.AsBsonDocument)
                    .ToArray();
                HarnessAssert.Equal(LifecycleDirectCollections.Length, digests.Length, "reversal digest coverage");
                HarnessAssert.True(
                    digests.All(digest => BsonLong(digest, "rowCount") == 0),
                    "reversal successor contains official rows");

                var prior = await LoadJobAsync(_flwRunId!, ct);
                HarnessAssert.True(!BsonBool(prior, "isCurrentPublication"), "prior publication stayed current");
                HarnessAssert.Equal("STALE", BsonString(prior, "freshnessState"), "prior freshness");
                HarnessAssert.Equal("LIFECYCLE_SUPERSEDED", BsonString(prior, "staleReason"), "prior stale reason");
                var oldRowsHash = await CaptureOperationsGenerationRowsHashAsync(
                    _flwGenerationId,
                    ct);
                HarnessAssert.Equal(
                    _opsApprovedGenerationRowsHash,
                    oldRowsHash,
                    "immutable prior generation rows");
                return new CaseObservation(
                    "Reversal used the immutable prior ledger and published zero-member full rebuild without subtracting or deleting old rows.",
                    $"audit={BsonString(audit, "auditHash")};stores=6;rows=0;priorRows={oldRowsHash}");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-REVERSAL-03",
            async () =>
            {
                var before = await CaptureLifecycleDirectSnapshotAsync(ct);
                var replay = await RequireApi().PostAsync(
                    $"api/work-assignment-review/reports/{Fixture().ReportId}/recall-approved",
                    _opsRecallRequest!.DeepClone(),
                    Actor("admin").Token,
                    ct: ct);
                ExpectOperationsSuccess(replay, "P9-OPS recall replay");
                var workers = await Task.WhenAll(
                    ProcessLifecycleOutboxAsync(20, ct),
                    ProcessLifecycleOutboxAsync(20, ct),
                    ProcessLifecycleOutboxAsync(20, ct));
                var after = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleSnapshotEqual(before, after, "reversal exact replay/concurrent drain");
                var current = await LoadCurrentOperationsPublicationAsync(ct);
                HarnessAssert.Equal(
                    BsonString(_opsReversalJob!, "_id"),
                    BsonString(current, "_id"),
                    "reversal replay current identity");
                return new CaseObservation(
                    "Exact recall replay and three reordered worker drains converged to the same receipt/current generation.",
                    $"http={(int)replay.StatusCode};processed={string.Join('+', workers.Select(ReadProcessed))};snapshot={LifecycleSnapshotSha256(after)}");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-REVERSAL-04",
            async () =>
            {
                var markers = await LoadOperationsDirtyMarkersAsync(ct);
                foreach (var marker in markers)
                {
                    var kind = BsonString(marker, "_opsKind");
                    if (kind == "BASIC")
                    {
                        HarnessAssert.True(BsonBool(marker, "snapshotDirty"), "Basic marker not dirty");
                        HarnessAssert.True(
                            marker.TryGetValue("snapshotDirtyAtUtc", out var at) && at.IsValidDateTime,
                            "Basic dirty timestamp missing");
                    }
                    else if (kind.StartsWith("ADVANCED_", StringComparison.Ordinal))
                    {
                        HarnessAssert.True(BsonBool(marker, "isDirty"), $"{kind} marker not dirty");
                        HarnessAssert.True(
                            !string.IsNullOrWhiteSpace(BsonNullableString(marker, "dirtyReason")),
                            $"{kind} dirty reason missing");
                    }
                    else
                    {
                        HarnessAssert.True(BsonBool(marker, "isDirty"), "Diff marker not dirty");
                        HarnessAssert.True(!BsonBool(marker, "isFresh"), "Diff marker stayed fresh");
                        HarnessAssert.Equal(
                            "SOURCE_LIFECYCLE_CHANGED",
                            BsonString(marker, "failureCode"),
                            "Diff dirty failure code");
                    }
                }

                var approvedRevision = BsonLong(_opsApprovedJob!, "directSourceRevision");
                var reversalRevision = BsonLong(_opsReversalJob!, "directSourceRevision");
                HarnessAssert.True(
                    reversalRevision > approvedRevision,
                    "reversal did not advance Direct source revision fence");
                _opsCollectionDeltaVerified = true;
                return new CaseObservation(
                    "The reversal advanced the Direct source fence and dirtied Basic, all Advanced grains and current Diff reverse lookups.",
                    $"sourceRevision={approvedRevision}->{reversalRevision};dirty={string.Join(',', markers.Select(marker => BsonString(marker, "_opsKind")))}");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-REVERSAL-05",
            async () =>
            {
                await SeedCanonicalP7MappingLineageAsync(
                    ct,
                    "p9-ops-p7-apply-reapprove-005",
                    _flwIncludeVersionId);
                var report = await LoadLifecycleReportAsync(ct);
                const string commandId = "p9-ops-reapprove-005";
                var request = new JsonObject
                {
                    ["expectedPayloadRevision"] = BsonInt(report, "payloadRevision"),
                    ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
                    ["commandId"] = commandId,
                    ["comment"] = "P9-08 restart with a new canonical mapping revision"
                };
                var before = await CaptureLifecycleDirectSnapshotAsync(ct);
                var approve = await RequireApi().PostAsync(
                    $"api/work-assignment-review/reports/{Fixture().ReportId}/approve",
                    request,
                    Actor("admin").Token,
                    ct: ct);
                ExpectOperationsSuccess(approve, "P9-OPS reapprove");
                var entry = await DrainOperationsLifecycleEntryAsync(
                    "REVIEW_APPROVE",
                    "PUBLISHED",
                    commandId,
                    ct);
                _opsReapprovedSnapshot = await CaptureLifecycleDirectSnapshotAsync(ct);
                var current = await LoadCurrentOperationsPublicationAsync(ct);
                HarnessAssert.Equal(1L, BsonLong(current, "totalReportCount"), "reapproval member count");
                HarnessAssert.Equal(1, BsonInt(current, "flowContributionSourceCount"), "reapproval source count");
                HarnessAssert.True(BsonInt(current, "flowContributionTargetCount") > 0, "reapproval target count");
                HarnessAssert.True(
                    !string.Equals(
                        BsonString(current, "generationId"),
                        BsonString(_opsReversalJob!, "generationId"),
                        StringComparison.Ordinal),
                    "reapproval reused reversal generation");
                HarnessAssert.True(
                    !current.Contains("reversalAudit") || current["reversalAudit"].IsBsonNull,
                    "clean reapproval retained reversal audit");
                _opsLifecycleTrace.Add(new P9OperationsLifecycleTrace(
                    "REAPPROVE_PUBLISHED",
                    "REVIEW_APPROVE",
                    BsonString(entry, "entryKey"),
                    BsonInt(await LoadLifecycleReportAsync(ct), "lifecycleRevision"),
                    LifecycleSnapshotSha256(before),
                    LifecycleSnapshotSha256(_opsReapprovedSnapshot),
                    BsonString(current, "_id"),
                    BsonString(current, "generationId"),
                    $"sources=1;targets={BsonInt(current, "flowContributionTargetCount")}"));
                _opsDirectMongoVerified = true;
                return new CaseObservation(
                    "Re-approval/restart published one clean canonical contribution from the new source revision.",
                    $"run={BsonString(current, "_id")};generation={BsonString(current, "generationId")};sources=1;targets={BsonInt(current, "flowContributionTargetCount")}");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-REVERSAL-06",
            async () =>
            {
                var report = await LoadLifecycleReportAsync(ct);
                const string deactivateCommand = "p9-ops-deactivate-006";
                var deactivateRequest = new JsonObject
                {
                    ["expectedPayloadRevision"] = BsonInt(report, "payloadRevision"),
                    ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
                    ["commandId"] = deactivateCommand,
                    ["comment"] = "P9-08 terminate canonical equivalent"
                };
                var deactivate = await RequireApi().PostAsync(
                    $"api/work-assignment-review/reports/{Fixture().ReportId}/deactivate",
                    deactivateRequest.DeepClone(),
                    Actor("admin").Token,
                    ct: ct);
                ExpectOperationsSuccess(deactivate, "P9-OPS deactivate");
                var deactivatedEntry = await DrainOperationsLifecycleEntryAsync(
                    "REVIEW_DEACTIVATE_REPORT",
                    "PUBLISHED",
                    deactivateCommand,
                    ct);
                var deactivated = await LoadCurrentOperationsPublicationAsync(ct);
                HarnessAssert.Equal(0L, BsonLong(deactivated, "totalReportCount"), "deactivate member count");
                HarnessAssert.Equal(
                    "REVIEW_DEACTIVATE_REPORT",
                    BsonString(deactivated.GetValue("reversalAudit").AsBsonDocument, "operation"),
                    "deactivate reversal operation");
                var replayBefore = await CaptureLifecycleDirectSnapshotAsync(ct);
                var deactivateReplay = await RequireApi().PostAsync(
                    $"api/work-assignment-review/reports/{Fixture().ReportId}/deactivate",
                    deactivateRequest.DeepClone(),
                    Actor("admin").Token,
                    ct: ct);
                ExpectOperationsSuccess(deactivateReplay, "P9-OPS deactivate replay");
                var replayAfter = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleSnapshotEqual(replayBefore, replayAfter, "deactivate replay");

                await SeedCanonicalP7MappingLineageAsync(
                    ct,
                    "p9-ops-p7-apply-reactivate-006",
                    _flwIncludeVersionId);
                report = await LoadLifecycleReportAsync(ct);
                const string reactivateCommand = "p9-ops-reactivate-006";
                var reactivateRequest = new JsonObject
                {
                    ["expectedPayloadRevision"] = BsonInt(report, "payloadRevision"),
                    ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
                    ["commandId"] = reactivateCommand,
                    ["comment"] = "P9-08 restart canonical equivalent"
                };
                var reactivate = await RequireApi().PostAsync(
                    $"api/work-assignment-review/reports/{Fixture().ReportId}/reactivate",
                    reactivateRequest,
                    Actor("admin").Token,
                    ct: ct);
                ExpectOperationsSuccess(reactivate, "P9-OPS reactivate");
                var reactivatedEntry = await DrainOperationsLifecycleEntryAsync(
                    "REVIEW_REACTIVATE_REPORT",
                    "PUBLISHED",
                    reactivateCommand,
                    ct);
                var current = await LoadCurrentOperationsPublicationAsync(ct);
                HarnessAssert.Equal(1L, BsonLong(current, "totalReportCount"), "reactivate member count");
                HarnessAssert.Equal(1, BsonInt(current, "flowContributionSourceCount"), "reactivate source count");
                HarnessAssert.True(BsonInt(current, "flowContributionTargetCount") > 0, "reactivate targets");
                _opsReapprovedSnapshot = await CaptureLifecycleDirectSnapshotAsync(ct);
                _opsLifecycleTrace.Add(new P9OperationsLifecycleTrace(
                    "TERMINATE_RESTART_EQUIVALENTS",
                    "REVIEW_DEACTIVATE_REPORT->REVIEW_REACTIVATE_REPORT",
                    $"{BsonString(deactivatedEntry, "entryKey")}->{BsonString(reactivatedEntry, "entryKey")}",
                    BsonInt(await LoadLifecycleReportAsync(ct), "lifecycleRevision"),
                    LifecycleSnapshotSha256(replayBefore),
                    LifecycleSnapshotSha256(_opsReapprovedSnapshot),
                    BsonString(current, "_id"),
                    BsonString(current, "generationId"),
                    "recall/return/rollback=REVIEW_RECALL_APPROVED;terminate=REVIEW_DEACTIVATE_REPORT;restart=REVIEW_REACTIVATE_REPORT/REVIEW_APPROVE"));
                _opsDirectMongoVerified = true;
                _opsCollectionDeltaVerified = true;
                return new CaseObservation(
                    "Approved return/rollback converged through recall; terminate/restart canonical equivalents deactivated then reactivated with one current contribution.",
                    $"recall=REVIEW_RECALL_APPROVED;terminate={BsonString(deactivatedEntry, "entryKey")};restart={BsonString(reactivatedEntry, "entryKey")};current={BsonString(current, "_id")}");
            },
            ct);
    }

    private Task RunOperationsRetryCasesAsync(CancellationToken ct)
        => RunOperationsRetryCasesCoreAsync(ct);

    private Task RunOperationsResetCasesAsync(CancellationToken ct)
        => RunOperationsResetCasesCoreAsync(ct);

    private Task RunOperationsDiagnosticCasesAsync(CancellationToken ct)
        => RunOperationsDiagnosticCasesCoreAsync(ct);

    private Task RunOperationsCleanupCasesAsync(CancellationToken ct)
        => RunOperationsCleanupCasesCoreAsync(ct);
}
