using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Real Kestrel/replica-set coverage for the P7-09 mapping lifecycle. The
/// owning P7 runner supplies the already-started isolated backend/database and
/// calls this method after the P7-04..08 cases.
/// </summary>
internal static partial class P5MaterializationProbe
{
    private static async Task RunP709MappingCasesAsync(
        HarnessCaseRunner cases,
        List<object> mongoEvidence,
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        string adminToken,
        ProbeFixture baseFixture,
        P7MappingFixture fixture,
        CancellationToken ct)
    {
        await cases.RunAsync(
            "MAP-RERUN-01",
            async () =>
            {
                var mapped = await CreateP709MappedScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "MAP-RERUN-01",
                    "p709-map-rerun-01-apply",
                    ct);
                var before = await CaptureP709ScopedLedgerHashAsync(
                    database,
                    mapped.Scenario.TargetReportId,
                    ct);
                var replay = await api.PostAsync(
                    P709ApplyPath(mapped.Scenario.TargetReportId),
                    CloneP709Request(mapped.InitialApplyRequest),
                    mapped.Scenario.ActorToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    replay,
                    HttpStatusCode.OK,
                    "MAP-RERUN-01 exact apply replay");
                var after = await CaptureP709ScopedLedgerHashAsync(
                    database,
                    mapped.Scenario.TargetReportId,
                    ct);
                Require(
                    before == after,
                    "MAP-RERUN-01 exact replay changed the direct-Mongo ledger.");

                var counts = await CountP709MappingLedgerAsync(
                    database,
                    mapped.Scenario.TargetReportId,
                    ct);
                Require(
                    counts.Receipts == 1 &&
                    counts.Provenance == 1 &&
                    counts.Events == 1 &&
                    counts.Outbox == 1 &&
                    counts.MappingAudits == 1,
                    "MAP-RERUN-01 exact replay duplicated a durable mapping row.");
                var binding = await LoadAndAssertP709BindingAsync(
                    database,
                    mapped.Scenario.TargetReportId,
                    expectedState:
                        DynamicFlowMappingProvenanceStates.Current,
                    expectedEventType:
                        DynamicFlowMappingEventTypes.ApplyCommitted,
                    expectedOperation:
                        DynamicFlowMappingLifecycleContract
                            .ApplyInitialOperation,
                    ct);
                Require(
                    binding.Receipt.Id == mapped.Binding.Receipt.Id &&
                    binding.Provenance.Id ==
                    mapped.Binding.Provenance.Id,
                    "MAP-RERUN-01 replay resolved a different durable result.");

                mongoEvidence.Add(new
                {
                    caseId = "MAP-RERUN-01",
                    targetReportId = mapped.Scenario.TargetReportId,
                    receiptId = binding.Receipt.Id,
                    provenanceId = binding.Provenance.Id,
                    counts,
                    zeroWriteReplay = true
                });
                return new CaseObservation(
                    "Exact Kestrel retry resolved the original receipt while every scoped Mongo document stayed byte-stable.",
                    P7MappingFingerprint(
                        "MAP-RERUN-01",
                        binding.Receipt.Id,
                        binding.Provenance.Id,
                        before,
                        after));
            });

        await cases.RunAsync(
            "MAP-RERUN-02",
            async () =>
            {
                var mapped = await CreateP709MappedScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "MAP-RERUN-02",
                    "p709-map-rerun-02-apply",
                    ct);
                var targetReportId = mapped.Scenario.TargetReportId;

                var changedReplay =
                    CloneP709Request(mapped.InitialApplyRequest);
                changedReplay["resultSemanticHash"] =
                    P7MappingFingerprint(
                        "MAP-RERUN-02",
                        "changed-replay");
                await AssertP709ConflictAndZeroWriteAsync(
                    api,
                    database,
                    targetReportId,
                    changedReplay,
                    mapped.Scenario.ActorToken,
                    "MAP-RERUN-02 changed replay",
                    [
                        "DYNAMIC_FLOW_MAPPING_COMMAND_REPLAY_MISMATCH"
                    ],
                    ct);

                var staleTarget =
                    CloneP709Request(mapped.InitialApplyRequest);
                staleTarget["commandId"] =
                    "p709-map-rerun-02-stale-target";
                await AssertP709ConflictAndZeroWriteAsync(
                    api,
                    database,
                    targetReportId,
                    staleTarget,
                    mapped.Scenario.ActorToken,
                    "MAP-RERUN-02 stale target preview",
                    [
                        "DYNAMIC_FLOW_MAPPING_IDENTITY_CONFLICT",
                        "DYNAMIC_FLOW_MAPPING_TARGET_REVISION_CONFLICT",
                        "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT"
                    ],
                    ct);

                var freshPreview = await PreviewP7MappingAsync(
                    api,
                    mapped.Scenario,
                    new JsonObject(),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    freshPreview,
                    HttpStatusCode.OK,
                    "MAP-RERUN-02 fresh rerun preview");

                var staleSource = BuildP709ApplyRequest(
                    freshPreview,
                    "p709-map-rerun-02-stale-source");
                staleSource["sourceSignature"] =
                    P7MappingFingerprint(
                        "MAP-RERUN-02",
                        "stale-source-signature");
                await AssertP709ConflictAndZeroWriteAsync(
                    api,
                    database,
                    targetReportId,
                    staleSource,
                    mapped.Scenario.ActorToken,
                    "MAP-RERUN-02 stale source pin",
                    [
                        "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT",
                        "DYNAMIC_FLOW_MAPPING_SOURCE_SIGNATURE_CONFLICT"
                    ],
                    ct);

                var staleLifecycle = BuildP709ApplyRequest(
                    freshPreview,
                    "p709-map-rerun-02-stale-lifecycle");
                staleLifecycle["expectedLifecycleRevision"] =
                    ApiHarnessClient.RequiredInt(
                        freshPreview.Json,
                        "targetLifecycleRevision") + 1;
                await AssertP709ConflictAndZeroWriteAsync(
                    api,
                    database,
                    targetReportId,
                    staleLifecycle,
                    mapped.Scenario.ActorToken,
                    "MAP-RERUN-02 stale lifecycle pin",
                    [
                        "DYNAMIC_FLOW_MAPPING_TARGET_REVISION_CONFLICT"
                    ],
                    ct);

                var staleToken = BuildP709ApplyRequest(
                    freshPreview,
                    "p709-map-rerun-02-stale-preview-token");
                staleToken["previewToken"] =
                    mapped.InitialApplyRequest["previewToken"]?.DeepClone();
                await AssertP709ConflictAndZeroWriteAsync(
                    api,
                    database,
                    targetReportId,
                    staleToken,
                    mapped.Scenario.ActorToken,
                    "MAP-RERUN-02 stale signed preview",
                    [
                        "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_INVALID",
                        "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT"
                    ],
                    ct);

                var counts = await CountP709MappingLedgerAsync(
                    database,
                    targetReportId,
                    ct);
                Require(
                    counts.Receipts == 1 &&
                    counts.Provenance == 1 &&
                    counts.Events == 1 &&
                    counts.Outbox == 1 &&
                    counts.MappingAudits == 1,
                    "MAP-RERUN-02 rejected requests changed mapping counts.");
                mongoEvidence.Add(new
                {
                    caseId = "MAP-RERUN-02",
                    targetReportId,
                    rejectedAttempts = 5,
                    counts,
                    allZeroWrite = true
                });
                return new CaseObservation(
                    "Changed replay plus stale target, source, lifecycle, and signed-preview pins each returned Kestrel 409 with a byte-stable Mongo ledger.",
                    P7MappingFingerprint(
                        "MAP-RERUN-02",
                        targetReportId,
                        counts.ToString() ?? string.Empty));
            });

        await cases.RunAsync(
            "MAP-RERUN-03",
            async () =>
            {
                var mapped = await CreateP709MappedScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "MAP-RERUN-03",
                    "p709-map-rerun-03-apply",
                    ct);
                await ForceP709SourcePayloadDriftAsync(
                    api,
                    backend,
                    database,
                    mapped.Scenario,
                    "p709-map-rerun-03-source-save",
                    ct);
                await ProcessP709LifecycleOutboxAsync(
                    api,
                    adminToken,
                    ct);
                var invalidation =
                    await AssertP709SourceInvalidationAsync(
                        database,
                        mapped,
                        DynamicFlowMappingLifecycleContract
                            .SourcePayloadDriftReason,
                        ct);

                var beforeRetry =
                    await CountP709MappingLedgerAsync(
                        database,
                        mapped.Scenario.TargetReportId,
                        ct);
                await ProcessP709LifecycleOutboxAsync(
                    api,
                    adminToken,
                    ct);
                await ProcessP709LifecycleOutboxAsync(
                    api,
                    adminToken,
                    ct);
                var afterRetry =
                    await CountP709MappingLedgerAsync(
                        database,
                        mapped.Scenario.TargetReportId,
                        ct);
                Require(
                    beforeRetry == afterRetry,
                    "MAP-RERUN-03 lifecycle retry duplicated invalidation ledger rows.");

                await RestoreP709SourceApprovalAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    mapped.Scenario,
                    "p709-map-rerun-03",
                    ct);
                var successor = await ApplyP709FreshRerunAsync(
                    api,
                    database,
                    mapped,
                    "p709-map-rerun-03-successor",
                    ct);
                await AssertP709SuccessorLineageAsync(
                    database,
                    mapped.Binding.Provenance.Id,
                    successor.Provenance.Id,
                    mapped.Scenario.TargetReportId,
                    ct);

                mongoEvidence.Add(new
                {
                    caseId = "MAP-RERUN-03",
                    targetReportId =
                        mapped.Scenario.TargetReportId,
                    invalidatedProvenanceId =
                        mapped.Binding.Provenance.Id,
                    invalidationEventId = invalidation.Event.Id,
                    rebuildIntentId = invalidation.Outbox.Id,
                    successorProvenanceId =
                        successor.Provenance.Id,
                    exactOnceInvalidation = true
                });
                return new CaseObservation(
                    "A Kestrel source save invalidated the consumed payload pin exactly once, emitted one rebuild-only intent, and required a fresh preview for the canonical successor.",
                    P7MappingFingerprint(
                        "MAP-RERUN-03",
                        mapped.Binding.Provenance.Id,
                        invalidation.Event.Id,
                        invalidation.Outbox.Id,
                        successor.Provenance.Id));
            });

        await cases.RunAsync(
            "MAP-RERUN-04",
            async () =>
            {
                var mapped = await CreateP709MappedScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "MAP-RERUN-04",
                    "p709-map-rerun-04-apply",
                    ct);
                var recallRequest =
                    await RecallP709ApprovedSourceAsync(
                        api,
                        database,
                        adminToken,
                        mapped.Scenario.SourceReportId,
                        "p709-map-rerun-04-source-recall",
                        ct);
                await ProcessP709LifecycleOutboxAsync(
                    api,
                    adminToken,
                    ct);
                var invalidation =
                    await AssertP709SourceInvalidationAsync(
                        database,
                        mapped,
                        DynamicFlowMappingLifecycleContract
                            .SourceLifecycleDriftReason,
                        ct);
                var beforeReplay =
                    await CountP709MappingLedgerAsync(
                        database,
                        mapped.Scenario.TargetReportId,
                        ct);
                var replay = await api.PostAsync(
                    P709RecallPath(mapped.Scenario.SourceReportId),
                    CloneP709Request(recallRequest),
                    adminToken,
                    ct: ct);
                RequireP709Success(
                    replay,
                    "MAP-RERUN-04 exact source recall replay");
                var afterReplay =
                    await CountP709MappingLedgerAsync(
                        database,
                        mapped.Scenario.TargetReportId,
                        ct);
                Require(
                    beforeReplay == afterReplay,
                    "MAP-RERUN-04 lifecycle replay duplicated invalidation rows.");
                var eventCount = await database
                    .GetCollection<DynamicFlowMappingEvent>(
                        "dynamic_flow_mapping_events")
                    .CountDocumentsAsync(
                        item =>
                            item.ProvenanceId ==
                            mapped.Binding.Provenance.Id &&
                            item.EventType ==
                            DynamicFlowMappingEventTypes
                                .ProvenanceInvalidated,
                        cancellationToken: ct);
                Require(
                    eventCount == 1,
                    "MAP-RERUN-04 expected exactly one provenance-invalidated event.");

                mongoEvidence.Add(new
                {
                    caseId = "MAP-RERUN-04",
                    sourceReportId =
                        mapped.Scenario.SourceReportId,
                    targetReportId =
                        mapped.Scenario.TargetReportId,
                    provenanceId =
                        mapped.Binding.Provenance.Id,
                    invalidationEventId = invalidation.Event.Id,
                    rebuildIntentId = invalidation.Outbox.Id,
                    lifecycleReplayZeroWrite = true
                });
                return new CaseObservation(
                    "A real reviewer recall invalidated the consumed lifecycle pin exactly once and preserved the historical mapping ledger on replay.",
                    P7MappingFingerprint(
                        "MAP-RERUN-04",
                        mapped.Binding.Provenance.Id,
                        invalidation.Event.Id,
                        invalidation.Outbox.Id));
            });

        await cases.RunAsync(
            "MAP-RERUN-05",
            async () =>
            {
                var mapped = await CreateP709MappedScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "MAP-RERUN-05",
                    "p709-map-rerun-05-apply",
                    ct);
                var firstPreview = await PreviewP7MappingAsync(
                    api,
                    mapped.Scenario,
                    new JsonObject(),
                    ct);
                var secondPreview = await PreviewP7MappingAsync(
                    api,
                    mapped.Scenario,
                    new JsonObject(),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    firstPreview,
                    HttpStatusCode.OK,
                    "MAP-RERUN-05 first concurrent preview");
                ApiHarnessClient.ExpectStatus(
                    secondPreview,
                    HttpStatusCode.OK,
                    "MAP-RERUN-05 second concurrent preview");
                var firstRequest = BuildP709ApplyRequest(
                    firstPreview,
                    "p709-map-rerun-05-race-a");
                var secondRequest = BuildP709ApplyRequest(
                    secondPreview,
                    "p709-map-rerun-05-race-b");
                var before = await CountP709MappingLedgerAsync(
                    database,
                    mapped.Scenario.TargetReportId,
                    ct);
                var responses = await Task.WhenAll(
                    api.PostAsync(
                        P709ApplyPath(
                            mapped.Scenario.TargetReportId),
                        firstRequest,
                        mapped.Scenario.ActorToken,
                        ct: ct),
                    api.PostAsync(
                        P709ApplyPath(
                            mapped.Scenario.TargetReportId),
                        secondRequest,
                        mapped.Scenario.ActorToken,
                        ct: ct));
                Require(
                    responses.Count(response =>
                        response.StatusCode == HttpStatusCode.OK) == 1 &&
                    responses.Count(response =>
                        response.StatusCode ==
                        HttpStatusCode.Conflict) == 1,
                    "MAP-RERUN-05 expected exactly one HTTP 200 winner and one HTTP 409 loser.");
                var loser = responses.Single(response =>
                    response.StatusCode == HttpStatusCode.Conflict);
                RequireP709MappingConflict(
                    loser,
                    "MAP-RERUN-05 concurrent loser",
                    [
                        "DYNAMIC_FLOW_MAPPING_TARGET_REVISION_CONFLICT",
                        "DYNAMIC_FLOW_MAPPING_SUCCESSOR_CAS_LOST"
                    ]);

                var after = await CountP709MappingLedgerAsync(
                    database,
                    mapped.Scenario.TargetReportId,
                    ct);
                Require(
                    after.Receipts == before.Receipts + 1 &&
                    after.Provenance == before.Provenance + 1 &&
                    after.Events == before.Events + 1 &&
                    after.Outbox == before.Outbox + 1 &&
                    after.MappingAudits ==
                    before.MappingAudits + 1,
                    "MAP-RERUN-05 concurrent loser left an orphan or duplicate ledger row.");
                var winner =
                    await LoadAndAssertP709BindingAsync(
                        database,
                        mapped.Scenario.TargetReportId,
                        DynamicFlowMappingProvenanceStates.Current,
                        DynamicFlowMappingEventTypes.RerunCommitted,
                        DynamicFlowMappingLifecycleContract
                            .ApplyRerunOperation,
                        ct);
                await AssertP709SuccessorLineageAsync(
                    database,
                    mapped.Binding.Provenance.Id,
                    winner.Provenance.Id,
                    mapped.Scenario.TargetReportId,
                    ct);
                var currentCount = await database
                    .GetCollection<
                        DynamicFlowMappingProvenanceRecord>(
                        "dynamic_flow_mapping_provenance")
                    .CountDocumentsAsync(
                        item =>
                            item.TargetReportId ==
                            mapped.Scenario.TargetReportId &&
                            item.State ==
                            DynamicFlowMappingProvenanceStates
                                .Current,
                        cancellationToken: ct);
                Require(
                    currentCount == 1,
                    "MAP-RERUN-05 retained more than one canonical provenance.");

                mongoEvidence.Add(new
                {
                    caseId = "MAP-RERUN-05",
                    targetReportId =
                        mapped.Scenario.TargetReportId,
                    predecessorProvenanceId =
                        mapped.Binding.Provenance.Id,
                    winnerProvenanceId =
                        winner.Provenance.Id,
                    httpStatuses =
                        responses.Select(response =>
                                (int)response.StatusCode)
                            .OrderBy(value => value)
                            .ToArray(),
                    before,
                    after,
                    canonicalProvenanceCount = currentCount
                });
                return new CaseObservation(
                    "Two signed reruns raced through Kestrel; target/predecessor CAS admitted one successor and Mongo contained no loser-owned rows.",
                    P7MappingFingerprint(
                        "MAP-RERUN-05",
                        mapped.Binding.Provenance.Id,
                        winner.Provenance.Id,
                        before.ToString() ?? string.Empty,
                        after.ToString() ?? string.Empty));
            });

        await RunP709LifecycleBindingCaseAsync(
            cases,
            mongoEvidence,
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            ct);
        await RunP709ReturnToDraftSuccessorCaseAsync(
            cases,
            mongoEvidence,
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            ct);
        await RunP709EpochInvalidationCaseAsync(
            cases,
            mongoEvidence,
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            ct);
    }

    private static async Task RunP709LifecycleBindingCaseAsync(
        HarnessCaseRunner cases,
        List<object> mongoEvidence,
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        string adminToken,
        ProbeFixture baseFixture,
        P7MappingFixture fixture,
        CancellationToken ct)
    {
        await cases.RunAsync(
            "MAP-RERUN-06",
            async () =>
            {
                var mapped = await CreateP709MappedScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "MAP-RERUN-06-LIFECYCLE",
                    "p709-map-rerun-06-apply",
                    ct);
                var expected = P709MappingReference.From(
                    mapped.Binding.Report);
                var operations = new List<object>();

                var submitted = await SubmitP709CurrentReportAsync(
                    api,
                    database,
                    mapped.Scenario.TargetReportId,
                    mapped.Scenario.ActorToken,
                    "p709-map-rerun-06-submit",
                    ct);
                Require(
                    submitted.Status ==
                    WorkAssignmentReportStatus.Submitted,
                    "MAP-RERUN-06 submit did not reach SUBMITTED.");
                await ProcessP709LifecycleOutboxAsync(
                    api,
                    adminToken,
                    ct);
                await AssertP709LifecycleBindingAsync(
                    database,
                    submitted,
                    "p709-map-rerun-06-submit",
                    "SUBMIT",
                    expected,
                    ct);
                operations.Add(new
                {
                    operation = "SUBMIT",
                    submitted.LifecycleRevision
                });

                var approved = await PostP709ReviewLifecycleAsync(
                    api,
                    database,
                    adminToken,
                    mapped.Scenario.TargetReportId,
                    "approve",
                    "p709-map-rerun-06-approve",
                    "P7-09 approve mapped report",
                    includeHistoricalConfirmation: true,
                    ct);
                Require(
                    approved.Status ==
                    WorkAssignmentReportStatus.Approved,
                    "MAP-RERUN-06 approve did not reach APPROVED.");
                await ProcessP709LifecycleOutboxAsync(
                    api,
                    adminToken,
                    ct);
                await AssertP709LifecycleBindingAsync(
                    database,
                    approved,
                    "p709-map-rerun-06-approve",
                    "REVIEW_APPROVE",
                    expected,
                    ct);
                operations.Add(new
                {
                    operation = "REVIEW_APPROVE",
                    approved.LifecycleRevision
                });

                var recalled = await PostP709ReviewLifecycleAsync(
                    api,
                    database,
                    adminToken,
                    mapped.Scenario.TargetReportId,
                    "recall-approved",
                    "p709-map-rerun-06-recall",
                    "P7-09 recall mapped approval",
                    includeHistoricalConfirmation: false,
                    ct);
                Require(
                    recalled.Status ==
                    WorkAssignmentReportStatus.Submitted,
                    "MAP-RERUN-06 recall did not return to SUBMITTED.");
                await ProcessP709LifecycleOutboxAsync(
                    api,
                    adminToken,
                    ct);
                await AssertP709LifecycleBindingAsync(
                    database,
                    recalled,
                    "p709-map-rerun-06-recall",
                    "REVIEW_RECALL_APPROVED",
                    expected,
                    ct);
                operations.Add(new
                {
                    operation = "REVIEW_RECALL_APPROVED",
                    recalled.LifecycleRevision
                });

                var returned = await PostP709ReviewLifecycleAsync(
                    api,
                    database,
                    adminToken,
                    mapped.Scenario.TargetReportId,
                    "return",
                    "p709-map-rerun-06-return",
                    "P7-09 return mapped report",
                    includeHistoricalConfirmation: false,
                    ct);
                Require(
                    returned.Status ==
                    WorkAssignmentReportStatus.Draft,
                    "MAP-RERUN-06 return did not reach DRAFT.");
                await ProcessP709LifecycleOutboxAsync(
                    api,
                    adminToken,
                    ct);
                await AssertP709LifecycleBindingAsync(
                    database,
                    returned,
                    "p709-map-rerun-06-return",
                    "REVIEW_RETURN",
                    expected,
                    ct);
                operations.Add(new
                {
                    operation = "REVIEW_RETURN",
                    returned.LifecycleRevision
                });

                var withdrawMapped =
                    await CreateP709MappedScenarioAsync(
                        api,
                        backend,
                        database,
                        adminToken,
                        baseFixture,
                        fixture,
                        "MAP-RERUN-06-WITHDRAW",
                        "p709-map-rerun-06-withdraw-apply",
                        ct);
                var withdrawReference =
                    P709MappingReference.From(
                        withdrawMapped.Binding.Report);
                var withdrawSubmitted =
                    await SubmitP709CurrentReportAsync(
                        api,
                        database,
                        withdrawMapped.Scenario.TargetReportId,
                        withdrawMapped.Scenario.ActorToken,
                        "p709-map-rerun-06-withdraw-submit",
                        ct);
                await ProcessP709LifecycleOutboxAsync(
                    api,
                    adminToken,
                    ct);
                await AssertP709LifecycleBindingAsync(
                    database,
                    withdrawSubmitted,
                    "p709-map-rerun-06-withdraw-submit",
                    "SUBMIT",
                    withdrawReference,
                    ct);
                var withdrawRequest = new JsonObject
                {
                    ["expectedPayloadRevision"] =
                        withdrawSubmitted.PayloadRevision,
                    ["expectedLifecycleRevision"] =
                        withdrawSubmitted.LifecycleRevision,
                    ["commandId"] =
                        "p709-map-rerun-06-withdraw",
                    ["returnReason"] =
                        "P7-09 assignee withdraw",
                    ["reviewerComment"] =
                        "P7-09 assignee withdraw"
                };
                var withdrawResponse = await api.PostAsync(
                    $"api/work-assignment-reports/{withdrawSubmitted.Id}/withdraw-submitted",
                    withdrawRequest,
                    withdrawMapped.Scenario.ActorToken,
                    ct: ct);
                RequireP709Success(
                    withdrawResponse,
                    "MAP-RERUN-06 withdraw submitted");
                var withdrawn = await LoadP709ReportAsync(
                    database,
                    withdrawSubmitted.Id,
                    ct);
                Require(
                    withdrawn.Status ==
                    WorkAssignmentReportStatus.Draft,
                    "MAP-RERUN-06 withdraw did not return to DRAFT.");
                await ProcessP709LifecycleOutboxAsync(
                    api,
                    adminToken,
                    ct);
                await AssertP709LifecycleBindingAsync(
                    database,
                    withdrawn,
                    "p709-map-rerun-06-withdraw",
                    "WITHDRAW",
                    withdrawReference,
                    ct);
                operations.Add(new
                {
                    operation = "WITHDRAW",
                    withdrawn.LifecycleRevision
                });

                var canonical = await database
                    .GetCollection<
                        DynamicFlowMappingProvenanceRecord>(
                        "dynamic_flow_mapping_provenance")
                    .Find(item =>
                        item.Id == expected.ProvenanceId)
                    .SingleAsync(ct);
                var withdrawCanonical = await database
                    .GetCollection<
                        DynamicFlowMappingProvenanceRecord>(
                        "dynamic_flow_mapping_provenance")
                    .Find(item =>
                        item.Id ==
                        withdrawReference.ProvenanceId)
                    .SingleAsync(ct);
                Require(
                    canonical.State ==
                    DynamicFlowMappingProvenanceStates.Current &&
                    withdrawCanonical.State ==
                    DynamicFlowMappingProvenanceStates.Current,
                    "MAP-RERUN-06 lifecycle operations changed canonical mapping provenance.");

                mongoEvidence.Add(new
                {
                    caseId = "MAP-RERUN-06",
                    targetReportId =
                        mapped.Scenario.TargetReportId,
                    withdrawTargetReportId =
                        withdrawMapped.Scenario.TargetReportId,
                    mapping = expected,
                    withdrawMapping = withdrawReference,
                    operations
                });
                return new CaseObservation(
                    "Submit, approve, recall, return, and withdraw committed real lifecycle entries whose outbox events and section projections retained the exact mapping receipt/provenance/payload binding.",
                    P7MappingFingerprint(
                        "MAP-RERUN-06",
                        expected.ReceiptId,
                        expected.ProvenanceId,
                        withdrawReference.ReceiptId,
                        withdrawReference.ProvenanceId,
                        string.Join(
                            ",",
                            operations.Select(item =>
                                item.ToString()))));
            });
    }

    private static async Task
        RunP709ReturnToDraftSuccessorCaseAsync(
            HarnessCaseRunner cases,
            List<object> mongoEvidence,
            ApiHarnessClient api,
            BackendServerLease backend,
            IMongoDatabase database,
            string adminToken,
            ProbeFixture baseFixture,
            P7MappingFixture fixture,
            CancellationToken ct)
    {
        await cases.RunAsync(
            "MAP-RERUN-07",
            async () =>
            {
                var mapped = await CreateP709MappedScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "MAP-RERUN-07",
                    "p709-map-rerun-07-apply",
                    ct);
                var predecessorImmutable =
                    P709ImmutableProvenanceHash(
                        mapped.Binding.Provenance);
                var submitted = await SubmitP709CurrentReportAsync(
                    api,
                    database,
                    mapped.Scenario.TargetReportId,
                    mapped.Scenario.ActorToken,
                    "p709-map-rerun-07-submit",
                    ct);
                Require(
                    submitted.Status ==
                    WorkAssignmentReportStatus.Submitted,
                    "MAP-RERUN-07 submit did not reach SUBMITTED.");
                await ProcessP709LifecycleOutboxAsync(
                    api,
                    adminToken,
                    ct);
                var returned = await PostP709ReviewLifecycleAsync(
                    api,
                    database,
                    adminToken,
                    mapped.Scenario.TargetReportId,
                    "return",
                    "p709-map-rerun-07-return",
                    "P7-09 return before rerun",
                    includeHistoricalConfirmation: false,
                    ct);
                Require(
                    returned.Status ==
                    WorkAssignmentReportStatus.Draft,
                    "MAP-RERUN-07 return did not reach DRAFT.");
                await ProcessP709LifecycleOutboxAsync(
                    api,
                    adminToken,
                    ct);

                var successor = await ApplyP709FreshRerunAsync(
                    api,
                    database,
                    mapped,
                    "p709-map-rerun-07-successor",
                    ct);
                await AssertP709SuccessorLineageAsync(
                    database,
                    mapped.Binding.Provenance.Id,
                    successor.Provenance.Id,
                    mapped.Scenario.TargetReportId,
                    ct);
                var predecessor = await database
                    .GetCollection<
                        DynamicFlowMappingProvenanceRecord>(
                        "dynamic_flow_mapping_provenance")
                    .Find(item =>
                        item.Id ==
                        mapped.Binding.Provenance.Id)
                    .SingleAsync(ct);
                Require(
                    predecessorImmutable ==
                    P709ImmutableProvenanceHash(predecessor),
                    "MAP-RERUN-07 rerun mutated immutable predecessor evidence.");
                var counts = await CountP709MappingLedgerAsync(
                    database,
                    mapped.Scenario.TargetReportId,
                    ct);
                Require(
                    counts.Receipts == 2 &&
                    counts.Provenance == 2 &&
                    counts.Events == 2 &&
                    counts.Outbox == 2 &&
                    counts.MappingAudits == 2,
                    "MAP-RERUN-07 successor did not produce exactly one new ledger write set.");

                mongoEvidence.Add(new
                {
                    caseId = "MAP-RERUN-07",
                    targetReportId =
                        mapped.Scenario.TargetReportId,
                    predecessorProvenanceId =
                        predecessor.Id,
                    successorProvenanceId =
                        successor.Provenance.Id,
                    predecessorImmutable = true,
                    counts
                });
                return new CaseObservation(
                    "Reviewer return preserved the mapped draft binding; a fresh signed preview created one successor and left predecessor evidence immutable and linked.",
                    P7MappingFingerprint(
                        "MAP-RERUN-07",
                        predecessor.Id,
                        successor.Provenance.Id,
                        predecessorImmutable));
            });
    }

    private static async Task RunP709EpochInvalidationCaseAsync(
        HarnessCaseRunner cases,
        List<object> mongoEvidence,
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        string adminToken,
        ProbeFixture baseFixture,
        P7MappingFixture mappingFixture,
        CancellationToken ct)
    {
        await cases.RunAsync(
            "MAP-RERUN-08",
            async () =>
            {
                var fixture =
                    await ConvertP709FixtureToEpochMappingAsync(
                        database,
                        mappingFixture,
                        ct);
                var workId = await CloneP7MappingWorkAsync(
                    database,
                    fixture.WorkId,
                    "MAP-RERUN-08-EPOCH",
                    ct);
                var launch = await LaunchAsync(
                    api,
                    adminToken,
                    baseFixture,
                    workId,
                    fixture.VersionId,
                    "p709-map-rerun-08-launch",
                    [fixture.TargetUnitId],
                    P601PeriodKey,
                    ct);
                RequireStatus(
                    launch.Confirm,
                    "SUCCEEDED",
                    "MAP-RERUN-08 FLOW-T12 launch");
                var flowInstanceId =
                    ApiHarnessClient.RequiredString(
                        launch.Confirm.Json,
                        "flowInstanceId");
                var actorToken = await PrepareP601ActorLoginAsync(
                    api,
                    backend,
                    database,
                    fixture.ActorUserId,
                    ct);
                var epochOne = await PrepareP709EpochTargetAsync(
                    api,
                    database,
                    adminToken,
                    actorToken,
                    flowInstanceId,
                    executionEpoch: 1,
                    ct);
                var epochOneBinding =
                    await ApplyP709EpochMappingAsync(
                        api,
                        database,
                        epochOne,
                        "p709-map-rerun-08-epoch-1-apply",
                        ct);
                var rollbackDeferredBefore =
                    await CountP709DeferredExecutionRowsAsync(
                        database,
                        ct);

                var instance = await LoadP709InstanceAsync(
                    database,
                    flowInstanceId,
                    ct);
                var rollbackRequest = new JsonObject
                {
                    ["commandId"] =
                        "p709-map-rerun-08-rollback",
                    ["expectedExecutionEpoch"] =
                        instance.ExecutionEpoch,
                    ["expectedInstanceRevision"] =
                        instance.Revision,
                    ["checkpointNodeId"] = "step_a",
                    ["reason"] =
                        "P7-09 rollback mapping provenance"
                };
                var rollback = await api.PostAsync(
                    P709EpochPath(
                        workId,
                        flowInstanceId,
                        "rollback"),
                    rollbackRequest,
                    adminToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    rollback,
                    HttpStatusCode.OK,
                    "MAP-RERUN-08 rollback");
                Require(
                    ApiHarnessClient.RequiredInt(
                        rollback.Json,
                        "previousExecutionEpoch") == 1 &&
                    ApiHarnessClient.RequiredInt(
                        rollback.Json,
                        "executionEpoch") == 2 &&
                    ApiHarnessClient.RequiredBool(
                        rollback.Json,
                        "businessWritePerformed"),
                    "MAP-RERUN-08 rollback did not create epoch two.");
                var rollbackEventId =
                    ApiHarnessClient.RequiredString(
                        rollback.Json,
                        "eventId");
                var rollbackIntentId =
                    ApiHarnessClient.RequiredString(
                        rollback.Json,
                        "rebuildIntentId");
                await AssertP709EpochInvalidationAsync(
                    database,
                    epochOneBinding,
                    rollbackEventId,
                    rollbackIntentId,
                    ct);
                var rollbackDeferredAfter =
                    await CountP709DeferredExecutionRowsAsync(
                        database,
                        ct);
                Require(
                    rollbackDeferredBefore ==
                    rollbackDeferredAfter,
                    "MAP-RERUN-08 rollback scheduled or materialized P8/P9 work.");
                var rollbackOutboxCount = await CountP709EpochRebuildsAsync(
                    database,
                    flowInstanceId,
                    ct);
                var rollbackReplay = await api.PostAsync(
                    P709EpochPath(
                        workId,
                        flowInstanceId,
                        "rollback"),
                    CloneP709Request(rollbackRequest),
                    adminToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    rollbackReplay,
                    HttpStatusCode.OK,
                    "MAP-RERUN-08 rollback replay");
                Require(
                    ApiHarnessClient.RequiredBool(
                        rollbackReplay.Json,
                        "replayed") &&
                    ApiHarnessClient.RequiredString(
                        rollbackReplay.Json,
                        "eventId") == rollbackEventId &&
                    await CountP709EpochRebuildsAsync(
                        database,
                        flowInstanceId,
                        ct) == rollbackOutboxCount,
                    "MAP-RERUN-08 rollback replay duplicated epoch ledger rows.");

                var epochTwo = await PrepareP709EpochTargetAsync(
                    api,
                    database,
                    adminToken,
                    actorToken,
                    flowInstanceId,
                    executionEpoch: 2,
                    ct);
                var epochTwoBinding =
                    await ApplyP709EpochMappingAsync(
                        api,
                        database,
                        epochTwo,
                        "p709-map-rerun-08-epoch-2-apply",
                        ct);
                var restartDeferredBefore =
                    await CountP709DeferredExecutionRowsAsync(
                        database,
                        ct);
                instance = await LoadP709InstanceAsync(
                    database,
                    flowInstanceId,
                    ct);
                var restartRequest = new JsonObject
                {
                    ["commandId"] =
                        "p709-map-rerun-08-restart",
                    ["expectedExecutionEpoch"] =
                        instance.ExecutionEpoch,
                    ["expectedInstanceRevision"] =
                        instance.Revision,
                    ["checkpointNodeId"] = "step_a",
                    ["reason"] =
                        "P7-09 restart mapping provenance"
                };
                var restart = await api.PostAsync(
                    P709EpochPath(
                        workId,
                        flowInstanceId,
                        "restart"),
                    restartRequest,
                    adminToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    restart,
                    HttpStatusCode.OK,
                    "MAP-RERUN-08 restart");
                Require(
                    ApiHarnessClient.RequiredInt(
                        restart.Json,
                        "previousExecutionEpoch") == 2 &&
                    ApiHarnessClient.RequiredInt(
                        restart.Json,
                        "executionEpoch") == 3,
                    "MAP-RERUN-08 restart did not create epoch three.");
                var restartEventId =
                    ApiHarnessClient.RequiredString(
                        restart.Json,
                        "eventId");
                var restartIntentId =
                    ApiHarnessClient.RequiredString(
                        restart.Json,
                        "rebuildIntentId");
                await AssertP709EpochInvalidationAsync(
                    database,
                    epochTwoBinding,
                    restartEventId,
                    restartIntentId,
                    ct);
                var restartDeferredAfter =
                    await CountP709DeferredExecutionRowsAsync(
                        database,
                        ct);
                Require(
                    restartDeferredBefore ==
                    restartDeferredAfter,
                    "MAP-RERUN-08 restart scheduled or materialized P8/P9 work.");
                var restartOutboxCount = await CountP709EpochRebuildsAsync(
                    database,
                    flowInstanceId,
                    ct);
                var restartReplay = await api.PostAsync(
                    P709EpochPath(
                        workId,
                        flowInstanceId,
                        "restart"),
                    CloneP709Request(restartRequest),
                    adminToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    restartReplay,
                    HttpStatusCode.OK,
                    "MAP-RERUN-08 restart replay");
                Require(
                    ApiHarnessClient.RequiredBool(
                        restartReplay.Json,
                        "replayed") &&
                    ApiHarnessClient.RequiredString(
                        restartReplay.Json,
                        "eventId") == restartEventId &&
                    await CountP709EpochRebuildsAsync(
                        database,
                        flowInstanceId,
                        ct) == restartOutboxCount,
                    "MAP-RERUN-08 restart replay duplicated epoch ledger rows.");

                var rebuilds = await database
                    .GetCollection<DynamicFlowRuntimeOutboxItem>(
                        "dynamic_flow_runtime_outbox")
                    .Find(item =>
                        item.FlowInstanceId == flowInstanceId &&
                        item.Operation ==
                        DynamicFlowFinalizeTopologyContract
                            .RebuildIntentOperation)
                    .SortBy(item => item.CreatedAtUtc)
                    .ToListAsync(ct);
                Require(
                    rebuilds.Count == 2,
                    "MAP-RERUN-08 expected one rollback and one restart rebuild intent.");

                mongoEvidence.Add(new
                {
                    caseId = "MAP-RERUN-08",
                    flowInstanceId,
                    epochOneReportId = epochOne.ReportId,
                    epochOneProvenanceId =
                        epochOneBinding.Provenance.Id,
                    rollbackEventId,
                    rollbackIntentId,
                    epochTwoReportId = epochTwo.ReportId,
                    epochTwoProvenanceId =
                        epochTwoBinding.Provenance.Id,
                    restartEventId,
                    restartIntentId,
                    replacementExecutionEpoch = 3,
                    rollbackDeferredBefore,
                    rollbackDeferredAfter,
                    restartDeferredBefore,
                    restartDeferredAfter
                });
                return new CaseObservation(
                    "Real FLOW-T12 rollback and restart invalidated epoch-bound mapping provenance, created deterministic replacement epochs and rebuild-only intents, and scheduled no P8/P9 work.",
                    P7MappingFingerprint(
                        "MAP-RERUN-08",
                        flowInstanceId,
                        epochOneBinding.Provenance.Id,
                        rollbackEventId,
                        epochTwoBinding.Provenance.Id,
                        restartEventId));
            });
    }

    private static async Task<P709MappedScenario>
        CreateP709MappedScenarioAsync(
            ApiHarnessClient api,
            BackendServerLease backend,
            IMongoDatabase database,
            string adminToken,
            ProbeFixture baseFixture,
            P7MappingFixture fixture,
            string suffix,
            string commandId,
            CancellationToken ct)
    {
        var workId = await CloneP7MappingWorkAsync(
            database,
            fixture.WorkId,
            suffix,
            ct);
        var scenario = await PrepareP7MappingScenarioAsync(
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            workId,
            $"p709-{suffix.ToLowerInvariant()}-launch",
            $"2026-07-{suffix}",
            ct);
        var preview = await PreviewP7MappingAsync(
            api,
            scenario,
            new JsonObject(),
            ct);
        AssertP7MappingPreviewIdentity(
            preview,
            fixture,
            scenario);
        var request = BuildP709ApplyRequest(
            preview,
            commandId);
        var apply = await api.PostAsync(
            P709ApplyPath(scenario.TargetReportId),
            request,
            scenario.ActorToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            apply,
            HttpStatusCode.OK,
            $"{suffix} initial mapping apply");
        var binding = await LoadAndAssertP709BindingAsync(
            database,
            scenario.TargetReportId,
            DynamicFlowMappingProvenanceStates.Current,
            DynamicFlowMappingEventTypes.ApplyCommitted,
            DynamicFlowMappingLifecycleContract
                .ApplyInitialOperation,
            ct);
        return new P709MappedScenario(
            scenario,
            CloneP709Request(request),
            binding);
    }

    private static JsonObject BuildP709ApplyRequest(
        ApiHarnessResponse preview,
        string commandId)
        => new()
        {
            ["expectedPayloadRevision"] =
                ApiHarnessClient.RequiredInt(
                    preview.Json,
                    "targetPayloadRevision"),
            ["expectedPayloadHash"] =
                ApiHarnessClient.RequiredString(
                    preview.Json,
                    "targetPayloadHash"),
            ["expectedLifecycleRevision"] =
                ApiHarnessClient.RequiredInt(
                    preview.Json,
                    "targetLifecycleRevision"),
            ["commandId"] = commandId,
            ["previewToken"] =
                ApiHarnessClient.RequiredString(
                    preview.Json,
                    "previewToken"),
            ["sourceSignature"] =
                ApiHarnessClient.RequiredString(
                    preview.Json,
                    "sourceSignature"),
            ["resultSemanticHash"] =
                ApiHarnessClient.RequiredString(
                    preview.Json,
                    "resultSemanticHash")
        };

    private static JsonObject CloneP709Request(JsonObject request)
        => request.DeepClone().AsObject();

    private static string P709ApplyPath(string reportId)
        => $"api/work-assignment-reports/{reportId}/draft/apply-dynamic-flow-mapping";

    private static string P709PreviewPath(string reportId)
        => $"api/work-assignment-reports/{reportId}/draft/preview-dynamic-flow-mapping";

    private static string P709RecallPath(string reportId)
        => $"api/work-assignment-review/reports/{reportId}/recall-approved";

    private static string P709EpochPath(
        string workId,
        string flowInstanceId,
        string action)
        => $"api/works/{workId}/dynamic-flows/instances/{flowInstanceId}/{action}";

    private static async Task AssertP709ConflictAndZeroWriteAsync(
        ApiHarnessClient api,
        IMongoDatabase database,
        string targetReportId,
        JsonObject request,
        string actorToken,
        string context,
        IReadOnlyCollection<string> expectedCodes,
        CancellationToken ct)
    {
        var before = await CaptureP709ScopedLedgerHashAsync(
            database,
            targetReportId,
            ct);
        var response = await api.PostAsync(
            P709ApplyPath(targetReportId),
            request,
            actorToken,
            ct: ct);
        RequireP709MappingConflict(
            response,
            context,
            expectedCodes);
        var after = await CaptureP709ScopedLedgerHashAsync(
            database,
            targetReportId,
            ct);
        Require(
            before == after,
            $"{context} changed the direct-Mongo ledger.");
    }

    private static void RequireP709MappingConflict(
        ApiHarnessResponse response,
        string context,
        IReadOnlyCollection<string> expectedCodes)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Conflict,
            context);
        var code =
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "errorCode") ??
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "code") ??
            string.Empty;
        Require(
            expectedCodes.Contains(code, StringComparer.Ordinal),
            $"{context} returned unexpected error code '{code}'.");
    }

    private static void RequireP709Success(
        ApiHarnessResponse response,
        string context)
    {
        Require(
            response.StatusCode is HttpStatusCode.OK or
                HttpStatusCode.Accepted,
            $"{context} expected HTTP 200/202, got {(int)response.StatusCode}; body={response.Body}");
    }

    private static async Task<WorkAssignmentReport>
        LoadP709ReportAsync(
            IMongoDatabase database,
            string reportId,
            CancellationToken ct)
        => await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .Find(item =>
                item.Id == reportId &&
                !item.IsDeleted)
            .SingleAsync(ct);

    private static async Task<DynamicFlowInstance>
        LoadP709InstanceAsync(
            IMongoDatabase database,
            string instanceId,
            CancellationToken ct)
        => await database
            .GetCollection<DynamicFlowInstance>(
                "dynamic_flow_instances")
            .Find(item =>
                item.Id == instanceId &&
                !item.IsDeleted)
            .SingleAsync(ct);

    private static async Task<P709BindingBundle>
        LoadAndAssertP709BindingAsync(
            IMongoDatabase database,
            string targetReportId,
            string expectedState,
            string expectedEventType,
            string expectedOperation,
            CancellationToken ct)
    {
        var report = await LoadP709ReportAsync(
            database,
            targetReportId,
            ct);
        var reference = P709MappingReference.From(report);
        var receipt = await database
            .GetCollection<DynamicFlowMappingApplyReceipt>(
                "dynamic_flow_mapping_apply_receipts")
            .Find(item =>
                item.Id == reference.ReceiptId &&
                item.TargetReportId == report.Id)
            .SingleAsync(ct);
        var provenance = await database
            .GetCollection<DynamicFlowMappingProvenanceRecord>(
                "dynamic_flow_mapping_provenance")
            .Find(item =>
                item.Id == reference.ProvenanceId &&
                item.ReceiptId == reference.ReceiptId &&
                item.TargetReportId == report.Id)
            .SingleAsync(ct);
        var mappingEvent = await database
            .GetCollection<DynamicFlowMappingEvent>(
                "dynamic_flow_mapping_events")
            .Find(item =>
                item.Id == receipt.EventId &&
                item.ReceiptId == receipt.Id)
            .SingleAsync(ct);
        var outbox = await database
            .GetCollection<DynamicFlowMappingOutboxItem>(
                "dynamic_flow_mapping_outbox")
            .Find(item =>
                item.Id == receipt.OutboxIntentId &&
                item.ReceiptId == receipt.Id)
            .SingleAsync(ct);
        var payload = await database
            .GetCollection<WorkReportPayload>(
                "work_report_payloads")
            .Find(item =>
                item.ReportId == report.Id &&
                !item.IsDeleted)
            .SingleAsync(ct);
        var sections = await database
            .GetCollection<WorkAssignmentReportSection>(
                "work_assignment_report_sections")
            .Find(item =>
                item.WorkAssignmentReportId == report.Id &&
                !item.IsDeleted)
            .ToListAsync(ct);

        Require(
            provenance.State == expectedState &&
            mappingEvent.EventType == expectedEventType &&
            outbox.Operation == expectedOperation,
            "P7-09 mapping state/event/outbox operation drifted.");
        Require(
            receipt.ProvenanceId == provenance.Id &&
            receipt.ProvenanceHash ==
            provenance.ProvenanceHash &&
            mappingEvent.ProvenanceId == provenance.Id &&
            mappingEvent.ProvenanceHash ==
            provenance.ProvenanceHash &&
            outbox.ProvenanceId == provenance.Id &&
            outbox.Intent.ProvenanceId ==
            provenance.Id &&
            outbox.Intent.ProvenanceHash ==
            provenance.ProvenanceHash,
            "P7-09 mapping durable references are not exact.");
        Require(
            receipt.ResultPayloadRevision ==
            reference.ResultPayloadRevision &&
            receipt.ResultPayloadHash ==
            reference.ResultPayloadHash &&
            provenance.TargetPayloadRevision ==
            receipt.ResultPayloadRevision &&
            provenance.TargetPayloadHash ==
            receipt.ResultPayloadHash,
            "P7-09 mapped payload binding drifted.");
        Require(
            receipt.State ==
            DynamicFlowMappingApplyStates.Reconciled &&
            outbox.State ==
            DynamicFlowMappingOutboxStates.Reconciled,
            "P7-09 inline mapping reconcile did not converge.");
        Require(
            payload.PayloadRevision ==
            report.PayloadRevision &&
            payload.PayloadHash == report.PayloadHash &&
            payload.Status == WorkReportPayloadStatus.Ready,
            "P7-09 current payload/header drifted.");
        Require(
            sections.Count > 0 &&
            sections.All(section =>
                section.DynamicFlowMappingReceiptId ==
                reference.ReceiptId &&
                section.DynamicFlowMappingProvenanceId ==
                reference.ProvenanceId &&
                section.DynamicFlowMappingProvenanceHash ==
                reference.ProvenanceHash &&
                section.DynamicFlowMappingResultPayloadRevision ==
                reference.ResultPayloadRevision &&
                section.DynamicFlowMappingResultPayloadHash ==
                reference.ResultPayloadHash),
            "P7-09 section projection lost exact mapping binding.");
        return new P709BindingBundle(
            report,
            receipt,
            provenance,
            mappingEvent,
            outbox,
            payload,
            sections);
    }

    private static async Task<P709MappingLedgerCounts>
        CountP709MappingLedgerAsync(
            IMongoDatabase database,
            string targetReportId,
            CancellationToken ct)
    {
        var targetId = ObjectId.Parse(targetReportId);
        var receipts = await database
            .GetCollection<BsonDocument>(
                "dynamic_flow_mapping_apply_receipts")
            .CountDocumentsAsync(
                new BsonDocument("targetReportId", targetId),
                cancellationToken: ct);
        var provenance = await database
            .GetCollection<BsonDocument>(
                "dynamic_flow_mapping_provenance")
            .CountDocumentsAsync(
                new BsonDocument("targetReportId", targetId),
                cancellationToken: ct);
        var events = await database
            .GetCollection<BsonDocument>(
                "dynamic_flow_mapping_events")
            .CountDocumentsAsync(
                new BsonDocument("targetReportId", targetId),
                cancellationToken: ct);
        var outbox = await database
            .GetCollection<BsonDocument>(
                "dynamic_flow_mapping_outbox")
            .CountDocumentsAsync(
                new BsonDocument("targetReportId", targetId),
                cancellationToken: ct);
        var audits = await database
            .GetCollection<BsonDocument>(
                "work_assignment_report_logs")
            .CountDocumentsAsync(
                new BsonDocument
                {
                    { "workAssignmentReportId", targetId },
                    {
                        "action",
                        new BsonDocument(
                            "$in",
                            new BsonArray
                            {
                                "APPLY_DYNAMIC_FLOW_MAPPING",
                                "RERUN_DYNAMIC_FLOW_MAPPING"
                            })
                    }
                },
                cancellationToken: ct);
        return new P709MappingLedgerCounts(
            receipts,
            provenance,
            events,
            outbox,
            audits);
    }

    private static async Task<string>
        CaptureP709ScopedLedgerHashAsync(
            IMongoDatabase database,
            string targetReportId,
            CancellationToken ct)
    {
        var targetId = ObjectId.Parse(targetReportId);
        var rows = new List<string>();
        foreach (var scope in new[]
                 {
                     new P709MongoScope(
                         "work_assignment_report",
                         new BsonDocument("_id", targetId)),
                     new P709MongoScope(
                         "dynamic_flow_mapping_apply_receipts",
                         new BsonDocument(
                             "targetReportId",
                             targetId)),
                     new P709MongoScope(
                         "dynamic_flow_mapping_provenance",
                         new BsonDocument(
                             "targetReportId",
                             targetId)),
                     new P709MongoScope(
                         "dynamic_flow_mapping_events",
                         new BsonDocument(
                             "targetReportId",
                             targetId)),
                     new P709MongoScope(
                         "dynamic_flow_mapping_outbox",
                         new BsonDocument(
                             "targetReportId",
                             targetId)),
                     new P709MongoScope(
                         "work_assignment_report_logs",
                         new BsonDocument(
                             "workAssignmentReportId",
                             targetId)),
                     new P709MongoScope(
                         "work_report_payloads",
                         new BsonDocument("reportId", targetId)),
                     new P709MongoScope(
                         "work_assignment_report_sections",
                         new BsonDocument(
                             "workAssignmentReportId",
                             targetId)),
                     new P709MongoScope(
                         "work_report_table_values",
                         new BsonDocument("reportId", targetId))
                 })
        {
            var documents = await database
                .GetCollection<BsonDocument>(scope.Collection)
                .Find(scope.Filter)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .ToListAsync(ct);
            rows.Add(scope.Collection);
            rows.AddRange(documents.Select(item => item.ToJson()));
        }
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        string.Join("\n", rows))))
            .ToLowerInvariant();
    }

    private static async Task<P709InvalidationBundle>
        AssertP709SourceInvalidationAsync(
            IMongoDatabase database,
            P709MappedScenario mapped,
            string expectedReason,
            CancellationToken ct)
    {
        var provenance = await database
            .GetCollection<DynamicFlowMappingProvenanceRecord>(
                "dynamic_flow_mapping_provenance")
            .Find(item =>
                item.Id == mapped.Binding.Provenance.Id)
            .SingleAsync(ct);
        var events = await database
            .GetCollection<DynamicFlowMappingEvent>(
                "dynamic_flow_mapping_events")
            .Find(item =>
                item.ProvenanceId == provenance.Id &&
                item.EventType ==
                DynamicFlowMappingEventTypes
                    .ProvenanceInvalidated)
            .ToListAsync(ct);
        var outboxes = await database
            .GetCollection<DynamicFlowMappingOutboxItem>(
                "dynamic_flow_mapping_outbox")
            .Find(item =>
                item.ProvenanceId == provenance.Id &&
                item.Operation ==
                DynamicFlowMappingLifecycleContract
                    .RebuildIntentOperation)
            .ToListAsync(ct);
        var source = await LoadP709ReportAsync(
            database,
            mapped.Scenario.SourceReportId,
            ct);
        var sourcePin = provenance.SourcePins.SingleOrDefault(item =>
            item.SourceReportId == source.Id);
        Require(
            provenance.State ==
            DynamicFlowMappingProvenanceStates.Invalidated &&
            provenance.InvalidationReason == expectedReason &&
            !string.IsNullOrWhiteSpace(
                provenance.InvalidatedByEventId) &&
            provenance.InvalidatedAtUtc.HasValue,
            "P7-09 source drift did not invalidate the current provenance with the expected reason; " +
            $"state={provenance.State}; reason={provenance.InvalidationReason ?? "<null>"}; " +
            $"expectedReason={expectedReason}; invalidatedByEventId={provenance.InvalidatedByEventId ?? "<null>"}; " +
            $"invalidatedAtUtc={provenance.InvalidatedAtUtc?.ToString("O") ?? "<null>"}; " +
            $"eventCount={events.Count}; outboxCount={outboxes.Count}; " +
            $"sourceStatus={source.Status}; sourceIsActive={source.IsActive}; sourceIsCurrent={source.IsCurrent}; " +
            $"sourcePayloadRevision={source.PayloadRevision}; sourcePayloadHash={source.PayloadHash}; " +
            $"sourceLifecycleRevision={source.LifecycleRevision}; " +
            $"pinPayloadRevision={sourcePin?.SourcePayloadRevision.ToString() ?? "<missing>"}; " +
            $"pinPayloadHash={sourcePin?.SourcePayloadHash ?? "<missing>"}; " +
            $"pinLifecycleRevision={sourcePin?.SourceLifecycleRevision.ToString() ?? "<missing>"}; " +
            $"pinLifecycleStatus={sourcePin?.SourceLifecycleStatus ?? "<missing>"}.");
        Require(
            events.Count == 1 &&
            outboxes.Count == 1,
            "P7-09 source drift did not emit exactly one invalidation event and rebuild intent.");
        var mappingEvent = events[0];
        var outbox = outboxes[0];
        Require(
            mappingEvent.ReceiptId ==
            provenance.ReceiptId &&
            mappingEvent.CommandId ==
            provenance.InvalidatedByEventId &&
            outbox.EventId == mappingEvent.Id &&
            outbox.ReceiptId ==
            provenance.ReceiptId &&
            outbox.State ==
            DynamicFlowMappingOutboxStates.Reconciled &&
            outbox.Intent.RebuildOnly &&
            !outbox.Intent.P8ExecutionEnabled &&
            !outbox.Intent.P9ExecutionEnabled &&
            outbox.Intent.InvalidationReason ==
            expectedReason &&
            outbox.Intent.InvalidatedByEventId ==
            provenance.InvalidatedByEventId &&
            outbox.Intent.InvalidatedProvenanceIds is
            [var invalidatedId] &&
            invalidatedId == provenance.Id,
            "P7-09 invalidation event/rebuild-only intent binding drifted.");
        var counts = await CountP709MappingLedgerAsync(
            database,
            mapped.Scenario.TargetReportId,
            ct);
        Require(
            counts.Receipts == 1 &&
            counts.Provenance == 1 &&
            counts.Events == 2 &&
            counts.Outbox == 2 &&
            counts.MappingAudits == 1,
            "P7-09 invalidation wrote outside its one event/one rebuild-intent budget.");
        return new P709InvalidationBundle(
            provenance,
            mappingEvent,
            outbox);
    }

    private static async Task<P709BindingBundle>
        ApplyP709FreshRerunAsync(
            ApiHarnessClient api,
            IMongoDatabase database,
            P709MappedScenario mapped,
            string commandId,
            CancellationToken ct)
    {
        var preview = await api.PostAsync(
            P709PreviewPath(
                mapped.Scenario.TargetReportId),
            new JsonObject(),
            mapped.Scenario.ActorToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            preview,
            HttpStatusCode.OK,
            $"{commandId} fresh preview");
        var request = BuildP709ApplyRequest(
            preview,
            commandId);
        var apply = await api.PostAsync(
            P709ApplyPath(
                mapped.Scenario.TargetReportId),
            request,
            mapped.Scenario.ActorToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            apply,
            HttpStatusCode.OK,
            $"{commandId} successor apply");
        return await LoadAndAssertP709BindingAsync(
            database,
            mapped.Scenario.TargetReportId,
            DynamicFlowMappingProvenanceStates.Current,
            DynamicFlowMappingEventTypes.RerunCommitted,
            DynamicFlowMappingLifecycleContract
                .ApplyRerunOperation,
            ct);
    }

    private static async Task AssertP709SuccessorLineageAsync(
        IMongoDatabase database,
        string predecessorId,
        string successorId,
        string targetReportId,
        CancellationToken ct)
    {
        var records = await database
            .GetCollection<DynamicFlowMappingProvenanceRecord>(
                "dynamic_flow_mapping_provenance")
            .Find(item =>
                item.TargetReportId == targetReportId &&
                (item.Id == predecessorId ||
                 item.Id == successorId))
            .ToListAsync(ct);
        var predecessor = records.Single(item =>
            item.Id == predecessorId);
        var successor = records.Single(item =>
            item.Id == successorId);
        Require(
            predecessor.State ==
            DynamicFlowMappingProvenanceStates.Superseded &&
            predecessor.SupersededByProvenanceId ==
            successor.Id &&
            predecessor.InvalidationReason ==
            DynamicFlowMappingLifecycleContract
                .SupersededByRerunReason &&
            predecessor.InvalidatedAtUtc.HasValue &&
            successor.State ==
            DynamicFlowMappingProvenanceStates.Current &&
            successor.SupersedesProvenanceId ==
            predecessor.Id &&
            successor.SupersededByProvenanceId is null,
            "P7-09 predecessor/successor lineage is not canonical.");
        var currentCount = records.Count(item =>
            item.State ==
            DynamicFlowMappingProvenanceStates.Current);
        Require(
            currentCount == 1,
            "P7-09 lineage retained multiple CURRENT records.");
    }

    private static string P709ImmutableProvenanceHash(
        DynamicFlowMappingProvenanceRecord provenance)
    {
        var document = provenance.ToBsonDocument();
        foreach (var mutable in new[]
                 {
                     "state",
                     "supersededByProvenanceId",
                     "invalidatedByEventId",
                     "invalidationReason",
                     "invalidatedAtUtc"
                 })
        {
            document.Remove(mutable);
        }
        return P7MappingFingerprint(document.ToJson());
    }

    private static async Task ForceP709SourcePayloadDriftAsync(
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        P7MappingScenario scenario,
        string commandId,
        CancellationToken ct)
    {
        var source = await LoadP709ReportAsync(
            database,
            scenario.SourceReportId,
            ct);
        var payload = await database
            .GetCollection<WorkReportPayload>(
                "work_report_payloads")
            .Find(item =>
                item.ReportId == source.Id &&
                !item.IsDeleted)
            .SingleAsync(ct);
        var draftUpdate = await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .UpdateOneAsync(
                item =>
                    item.Id == source.Id &&
                    item.Status ==
                    WorkAssignmentReportStatus.Approved &&
                    item.IsActive &&
                    item.IsCurrent &&
                    !item.IsDeleted,
                Builders<WorkAssignmentReport>.Update
                    .Set(
                        item => item.Status,
                        WorkAssignmentReportStatus.Draft)
                    .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken: ct);
        Require(
            draftUpdate.ModifiedCount == 1,
            "MAP-RERUN-03 could not prepare the source draft mutation boundary.");

        var fieldValues =
            JsonNode.Parse(payload.FieldValuesJson ?? "{}")
                ?.AsObject() ??
            new JsonObject();
        if (fieldValues["values"] is not JsonObject values)
        {
            values = new JsonObject();
            fieldValues["values"] = values;
        }
        values["field_note"] = null;
        var values1D =
            JsonNode.Parse(payload.Values1DJson) as JsonArray ??
            new JsonArray();
        var actorToken = await PrepareP601ActorLoginAsync(
            api,
            backend,
            database,
            source.AssigneeUserId,
            ct);
        var save = await api.PutAsync(
            $"api/work-assignment-reports/{source.Id}/draft",
            new JsonObject
            {
                ["expectedPayloadRevision"] =
                    source.PayloadRevision,
                ["commandId"] = commandId,
                ["values1D"] = values1D,
                ["fieldValuesJson"] =
                    fieldValues.ToJsonString(),
                ["tableValuesJson"] =
                    payload.TableValuesRootJson,
                ["dataOrigin"] = source.DataOrigin,
                ["cumulativeContributionMode"] =
                    source.CumulativeContributionMode,
                ["cumulativeContributionPolicyJson"] =
                    source.CumulativeContributionPolicyJson,
                ["completedDate"] =
                    source.CompletedDate,
                ["lateReason"] = source.LateReason,
                ["note"] = source.Note
            },
            actorToken,
            ct: ct);
        RequireP709Success(
            save,
            "MAP-RERUN-03 source payload save");
        var changed = await LoadP709ReportAsync(
            database,
            source.Id,
            ct);
        Require(
            changed.PayloadRevision ==
            source.PayloadRevision + 1 &&
            changed.PayloadHash != source.PayloadHash &&
            changed.Status ==
            WorkAssignmentReportStatus.Draft,
            "MAP-RERUN-03 Kestrel source save did not commit payload drift.");
    }

    private static async Task RestoreP709SourceApprovalAsync(
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        string adminToken,
        P7MappingScenario scenario,
        string commandPrefix,
        CancellationToken ct)
    {
        var source = await LoadP709ReportAsync(
            database,
            scenario.SourceReportId,
            ct);
        var actorToken = await ResolveP709ReportActorTokenAsync(
            api,
            backend,
            database,
            scenario.SourceReportId,
            scenario,
            ct);
        var payload = await database
            .GetCollection<WorkReportPayload>(
                "work_report_payloads")
            .Find(item =>
                item.ReportId == source.Id &&
                !item.IsDeleted)
            .SingleAsync(ct);
        var fieldValues =
            JsonNode.Parse(payload.FieldValuesJson ?? "{}")
                ?.AsObject() ??
            new JsonObject();
        if (fieldValues["values"] is not JsonObject values)
        {
            values = new JsonObject();
            fieldValues["values"] = values;
        }
        values["field_note"] = P7MappingHiddenSourceValue;
        var restoreSave = await api.PutAsync(
            $"api/work-assignment-reports/{source.Id}/draft",
            new JsonObject
            {
                ["expectedPayloadRevision"] =
                    source.PayloadRevision,
                ["commandId"] =
                    $"{commandPrefix}-source-restore-value",
                ["values1D"] =
                    JsonNode.Parse(payload.Values1DJson) as
                    JsonArray ?? new JsonArray(),
                ["fieldValuesJson"] =
                    fieldValues.ToJsonString(),
                ["tableValuesJson"] =
                    payload.TableValuesRootJson,
                ["dataOrigin"] = source.DataOrigin,
                ["cumulativeContributionMode"] =
                    source.CumulativeContributionMode,
                ["cumulativeContributionPolicyJson"] =
                    source.CumulativeContributionPolicyJson,
                ["summarySourceJson"] =
                    payload.SummarySourceJson,
                ["completedDate"] =
                    source.CompletedDate,
                ["lateReason"] = source.LateReason,
                ["note"] = source.Note
            },
            actorToken,
            ct: ct);
        RequireP709Success(
            restoreSave,
            "MAP-RERUN-03 restore source value");
        var submitted = await SubmitP709CurrentReportAsync(
            api,
            database,
            scenario.SourceReportId,
            actorToken,
            $"{commandPrefix}-source-submit",
            ct);
        Require(
            submitted.Status ==
            WorkAssignmentReportStatus.Submitted,
            "MAP-RERUN-03 restored source did not reach SUBMITTED.");
        var approved = await PostP709ReviewLifecycleAsync(
            api,
            database,
            adminToken,
            scenario.SourceReportId,
            "approve",
            $"{commandPrefix}-source-approve",
            "P7-09 reapprove payload-drift source",
            includeHistoricalConfirmation: true,
            ct);
        Require(
            approved.Status ==
            WorkAssignmentReportStatus.Approved,
            "MAP-RERUN-03 restored source did not reach APPROVED.");
    }

    private static async Task<string> ResolveP709ReportActorTokenAsync(
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        string reportId,
        P7MappingScenario scenario,
        CancellationToken ct)
    {
        var report = await LoadP709ReportAsync(
            database,
            reportId,
            ct);
        if (report.AssigneeUserId ==
            scenario.ActorUserId)
        {
            return scenario.ActorToken;
        }
        return await PrepareP601ActorLoginAsync(
            api,
            backend,
            database,
            report.AssigneeUserId,
            ct);
    }

    private static async Task<JsonObject>
        RecallP709ApprovedSourceAsync(
            ApiHarnessClient api,
            IMongoDatabase database,
            string adminToken,
            string sourceReportId,
            string commandId,
            CancellationToken ct)
    {
        var source = await LoadP709ReportAsync(
            database,
            sourceReportId,
            ct);
        Require(
            source.Status ==
            WorkAssignmentReportStatus.Approved,
            "MAP-RERUN-04 source must start APPROVED.");
        var request = new JsonObject
        {
            ["expectedPayloadRevision"] =
                source.PayloadRevision,
            ["expectedLifecycleRevision"] =
                source.LifecycleRevision,
            ["commandId"] = commandId,
            ["comment"] =
                "P7-09 source lifecycle drift"
        };
        var response = await api.PostAsync(
            P709RecallPath(source.Id),
            request,
            adminToken,
            ct: ct);
        RequireP709Success(
            response,
            "MAP-RERUN-04 source recall");
        var recalled = await LoadP709ReportAsync(
            database,
            source.Id,
            ct);
        Require(
            recalled.Status ==
            WorkAssignmentReportStatus.Submitted &&
            recalled.LifecycleRevision ==
            source.LifecycleRevision + 1,
            "MAP-RERUN-04 source recall did not commit lifecycle drift.");
        return request;
    }

    private static async Task ProcessP709LifecycleOutboxAsync(
        ApiHarnessClient api,
        string adminToken,
        CancellationToken ct)
    {
        var worker = await api.PostAsync(
            "api/admin/operations/job-runs/lifecycle-projection-outbox/process?maxReports=50",
            body: null,
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            worker,
            HttpStatusCode.OK,
            "P7-09 lifecycle projection worker");
    }

    private static async Task<WorkAssignmentReport>
        SubmitP709CurrentReportAsync(
            ApiHarnessClient api,
            IMongoDatabase database,
            string reportId,
            string actorToken,
            string commandId,
            CancellationToken ct)
    {
        var report = await LoadP709ReportAsync(
            database,
            reportId,
            ct);
        var response = await api.PostAsync(
            $"api/work-assignment-reports/{report.Id}/submit",
            new JsonObject
            {
                ["expectedPayloadRevision"] =
                    report.PayloadRevision,
                ["expectedLifecycleRevision"] =
                    report.LifecycleRevision,
                ["commandId"] = commandId
            },
            actorToken,
            ct: ct);
        RequireP709Success(
            response,
            $"{commandId} submit");
        return await LoadP709ReportAsync(
            database,
            report.Id,
            ct);
    }

    private static async Task<WorkAssignmentReport>
        PostP709ReviewLifecycleAsync(
            ApiHarnessClient api,
            IMongoDatabase database,
            string adminToken,
            string reportId,
            string action,
            string commandId,
            string comment,
            bool includeHistoricalConfirmation,
            CancellationToken ct)
    {
        var report = await LoadP709ReportAsync(
            database,
            reportId,
            ct);
        var request = new JsonObject
        {
            ["expectedPayloadRevision"] =
                report.PayloadRevision,
            ["expectedLifecycleRevision"] =
                report.LifecycleRevision,
            ["commandId"] = commandId,
            ["comment"] = comment
        };
        if (includeHistoricalConfirmation)
        {
            request["confirmHistoricalDataApproval"] =
                report.IsHistoricalData;
        }
        var response = await api.PostAsync(
            $"api/work-assignment-review/reports/{report.Id}/{action}",
            request,
            adminToken,
            ct: ct);
        RequireP709Success(
            response,
            $"{commandId} review {action}");
        return await LoadP709ReportAsync(
            database,
            report.Id,
            ct);
    }

    private static async Task AssertP709LifecycleBindingAsync(
        IMongoDatabase database,
        WorkAssignmentReport report,
        string commandId,
        string expectedOperation,
        P709MappingReference expected,
        CancellationToken ct)
    {
        var current = await LoadP709ReportAsync(
            database,
            report.Id,
            ct);
        Require(
            P709MappingReference.From(current) == expected,
            $"{expectedOperation} changed report mapping header binding.");
        var entry = current.LifecycleProjectionOutbox
            .Single(item =>
                item.CommandId == commandId);
        Require(
            entry.Operation == expectedOperation &&
            entry.State ==
            WorkReportLifecycleProjectionOutboxStates.Completed &&
            entry.DynamicFlowMappingReceiptId ==
            expected.ReceiptId &&
            entry.DynamicFlowMappingProvenanceId ==
            expected.ProvenanceId &&
            entry.DynamicFlowMappingProvenanceHash ==
            expected.ProvenanceHash &&
            entry.DynamicFlowMappingResultPayloadRevision ==
            expected.ResultPayloadRevision &&
            entry.DynamicFlowMappingResultPayloadHash ==
            expected.ResultPayloadHash &&
            entry.BusinessEvents.Count > 0,
            $"{expectedOperation} lifecycle outbox lost exact mapping binding; " +
            $"actualOperation={entry.Operation}; state={entry.State}; " +
            $"receiptId={entry.DynamicFlowMappingReceiptId ?? "<null>"}; expectedReceiptId={expected.ReceiptId}; " +
            $"provenanceId={entry.DynamicFlowMappingProvenanceId ?? "<null>"}; expectedProvenanceId={expected.ProvenanceId}; " +
            $"provenanceHash={entry.DynamicFlowMappingProvenanceHash ?? "<null>"}; expectedProvenanceHash={expected.ProvenanceHash}; " +
            $"resultPayloadRevision={entry.DynamicFlowMappingResultPayloadRevision?.ToString() ?? "<null>"}; " +
            $"expectedResultPayloadRevision={expected.ResultPayloadRevision}; " +
            $"resultPayloadHash={entry.DynamicFlowMappingResultPayloadHash ?? "<null>"}; " +
            $"expectedResultPayloadHash={expected.ResultPayloadHash}; businessEventCount={entry.BusinessEvents.Count}.");
        foreach (var businessEvent in entry.BusinessEvents)
        {
            var data = businessEvent.Data ??
                       throw new InvalidOperationException(
                           $"{expectedOperation} business event data is missing.");
            Require(
                data.GetValueOrDefault(
                    "dynamicFlowMappingReceiptId") ==
                expected.ReceiptId &&
                data.GetValueOrDefault(
                    "dynamicFlowMappingProvenanceId") ==
                expected.ProvenanceId &&
                data.GetValueOrDefault(
                    "dynamicFlowMappingProvenanceHash") ==
                expected.ProvenanceHash &&
                data.GetValueOrDefault(
                    "dynamicFlowMappingResultPayloadRevision") ==
                expected.ResultPayloadRevision.ToString(
                    System.Globalization.CultureInfo
                        .InvariantCulture) &&
                data.GetValueOrDefault(
                    "dynamicFlowMappingResultPayloadHash") ==
                expected.ResultPayloadHash,
                $"{expectedOperation} business event lost exact mapping binding.");
        }
        var sections = await database
            .GetCollection<WorkAssignmentReportSection>(
                "work_assignment_report_sections")
            .Find(item =>
                item.WorkAssignmentReportId == current.Id &&
                !item.IsDeleted)
            .ToListAsync(ct);
        Require(
            sections.Count > 0 &&
            sections.All(section =>
                section.DynamicFlowMappingReceiptId ==
                expected.ReceiptId &&
                section.DynamicFlowMappingProvenanceId ==
                expected.ProvenanceId &&
                section.DynamicFlowMappingProvenanceHash ==
                expected.ProvenanceHash &&
                section.DynamicFlowMappingResultPayloadRevision ==
                expected.ResultPayloadRevision &&
                section.DynamicFlowMappingResultPayloadHash ==
                expected.ResultPayloadHash),
            $"{expectedOperation} section projection lost mapping binding.");
    }

    private static async Task<P709EpochFixture>
        ConvertP709FixtureToEpochMappingAsync(
            IMongoDatabase database,
            P7MappingFixture seed,
            CancellationToken ct)
    {
        var versions = database
            .GetCollection<DynamicFlowTemplateVersion>(
                "dynamic_flow_template_versions");
        var version = await versions
            .Find(item => item.Id == seed.VersionId)
            .SingleAsync(ct);
        var payload =
            JsonNode.Parse(version.PayloadJson)?.AsObject() ??
            throw new InvalidOperationException(
                "P7-09 epoch seed payload cannot be parsed.");
        var form = seed.RootForm;
        payload["archetypeId"] =
            DynamicFlowFinalizeTopologyContract.ArchetypeId;
        payload["catalogVersion"] =
            DynamicFlowP7CatalogCandidate.Version;
        payload["catalogSemanticHash"] =
            DynamicFlowP7CatalogCandidate.SemanticHash;
        payload["entryStepId"] = "step_a";
        payload["rootDynamicFormTemplateId"] =
            form.FormVersionId;
        payload["resultOwnerStepId"] = "step_a";
        payload["resultOwnerFormNodeId"] = form.FormNodeId;
        payload["statisticsOwnerStepId"] = "step_a";
        payload["statisticsOwnerFormNodeId"] =
            form.FormNodeId;
        payload["formNodes"] = new JsonArray(
            new JsonObject
            {
                ["formNodeId"] = form.FormNodeId,
                ["role"] = "ROOT",
                ["dynamicFormTemplateId"] =
                    form.FormVersionId,
                ["dynamicFormFamilyId"] =
                    form.FormFamilyId,
                ["dynamicFormVersionNo"] =
                    form.FormVersionNo,
                ["dynamicFormSchemaHash"] =
                    form.FormSchemaHash,
                ["dynamicFormSnapshotHash"] =
                    form.FormSchemaHash
            });
        payload["nodes"] = new JsonArray(
            new JsonObject
            {
                ["nodeId"] = "step_a",
                ["nodeCode"] = "EPOCH_ENTRY",
                ["nodeKind"] =
                    DynamicFlowNodeKinds.FormStep,
                ["formNodeId"] = form.FormNodeId,
                ["declaredRoles"] =
                    new JsonArray("ASSIGNEE")
            },
            new JsonObject
            {
                ["nodeId"] = "epoch_gate",
                ["nodeCode"] = "EPOCH_GATE",
                ["nodeKind"] =
                    DynamicFlowNodeKinds.Gateway,
                ["gateway"] = new JsonObject
                {
                    ["kind"] =
                        DynamicFlowGatewayKinds
                            .RollbackFinalize,
                    ["rollbackTargetNodeId"] = "step_a"
                }
            },
            new JsonObject
            {
                ["nodeId"] = "final",
                ["nodeCode"] = "FINAL",
                ["nodeKind"] =
                    DynamicFlowNodeKinds.Final
            });
        payload["edges"] = new JsonArray(
            new JsonObject
            {
                ["transitionId"] =
                    "tr_entry_epoch",
                ["fromNodeId"] = "step_a",
                ["toNodeId"] = "epoch_gate"
            },
            new JsonObject
            {
                ["transitionId"] =
                    "tr_epoch_final",
                ["fromNodeId"] = "epoch_gate",
                ["toNodeId"] = "final"
            });
        payload["actorPolicies"] = new JsonArray();
        payload["fieldPolicies"] = new JsonArray
        {
            new JsonObject
            {
                ["policyId"] =
                    "p709-epoch-field-policy",
                ["dynamicFormTemplateId"] =
                    form.FormVersionId,
                ["stepId"] = "step_a",
                ["stepCode"] = "EPOCH_ENTRY",
                ["actorRole"] = "*",
                ["fieldId"] = "field_note",
                ["fieldKey"] = "note",
                ["read"] = true,
                ["write"] = true
            }
        };
        payload["tableColumnPolicies"] = new JsonArray();
        payload["mappingRules"] = new JsonArray
        {
            new JsonObject
            {
                ["mappingId"] =
                    "p709-epoch-constant-note",
                ["mappingVersion"] = 1,
                ["mappingKind"] = "FIELD",
                ["dataType"] = "TEXT",
                ["conflictPolicy"] = "OVERWRITE",
                ["contributionPolicy"] = "EXCLUDE",
                ["evaluationGrain"] = "FLOW_INSTANCE",
                ["errorPolicy"] = "BLOCK_APPLY",
                ["inputs"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["inputKey"] = "epoch_note",
                        ["dataType"] = "TEXT",
                        ["cardinality"] = "ONE",
                        ["nullPolicy"] = "ERROR",
                        ["source"] = new JsonObject
                        {
                            ["kind"] = "CONSTANT",
                            ["dataType"] = "TEXT"
                        },
                        ["constantValue"] =
                            P7MappingHiddenSourceValue
                    }
                },
                ["target"] = new JsonObject
                {
                    ["kind"] = "FIELD",
                    ["dynamicFormTemplateId"] =
                        form.FormVersionId,
                    ["stepId"] = "step_a",
                    ["stepCode"] = "EPOCH_ENTRY",
                    ["fieldId"] = "field_note",
                    ["fieldKey"] = "note",
                    ["dataType"] = "TEXT"
                },
                ["calculation"] = new JsonObject
                {
                    ["kind"] = "EXPRESSION",
                    ["operation"] = "copy",
                    ["resultDataType"] = "TEXT",
                    ["expression"] = new JsonObject
                    {
                        ["op"] = "copy",
                        ["args"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["input"] = "epoch_note"
                            }
                        }
                    }
                }
            }
        };
        payload["rollbackPolicy"] = new JsonObject();
        payload["finalResultPolicy"] = new JsonObject();
        payload["statisticProfile"] = new JsonObject();

        var canonical =
            DynamicFlowDefinitionPayloadContract
                .CanonicalizeAndValidate(
                    payload.ToJsonString(),
                    new DynamicFlowDefinitionValidationOptions(
                        AllowLegacy: false,
                        AllowServerManagedPins: true,
                        RequireServerManagedPins: true,
                        AllowHistoricalCatalogPins: true));
        var topology =
            DynamicFlowFinalizeTopologyContract.Require(
                canonical.CanonicalJson,
                canonical.PayloadHash);
        DynamicFlowMappingEngine.ValidateP7Rules(
            DynamicFlowMappingEngine
                .ReadRulesFromPayloadJson(
                    canonical.CanonicalJson));
        Require(
            topology.EntryNode.NodeId == "step_a" &&
            topology.RollbackTargetNodeId == "step_a",
            "P7-09 epoch fixture did not freeze the exact rollback target.");
        var versionUpdate = await versions.UpdateOneAsync(
            item => item.Id == seed.VersionId,
            Builders<DynamicFlowTemplateVersion>.Update
                .Set(
                    item => item.PayloadJson,
                    canonical.CanonicalJson)
                .Set(
                    item => item.PayloadHash,
                    canonical.PayloadHash)
                .Set(
                    item => item.CatalogVersion,
                    DynamicFlowP7CatalogCandidate.Version)
                .Set(
                    item => item.CatalogSemanticHash,
                    DynamicFlowP7CatalogCandidate
                        .SemanticHash)
                .Set(
                    item => item.ExecutionEligibility,
                    DynamicFlowExecutionEligibilities
                        .BlockedUntilTargetPhase)
                .Set(
                    item => item.ExecutionBlockedReason,
                    DynamicFlowExecutionBlockedReasons
                        .TargetPhaseNotImplemented)
                .Set(item => item.BlockedUntilPhase, "P7")
                .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
            cancellationToken: ct);
        Require(
            versionUpdate.MatchedCount == 1,
            "P7-09 epoch version update did not match.");
        var familyUpdate = await database
            .GetCollection<DynamicFlowTemplate>(
                "dynamic_flow_templates")
            .UpdateOneAsync(
                item => item.Id == seed.FamilyId,
                Builders<DynamicFlowTemplate>.Update
                    .Set(
                        item => item.CurrentVersionHash,
                        canonical.PayloadHash)
                    .Set(
                        item => item.RootDynamicFormTemplateId,
                        form.FormVersionId)
                    .Set(
                        item => item.UpdatedAtUtc,
                        DateTime.UtcNow),
                cancellationToken: ct);
        Require(
            familyUpdate.MatchedCount == 1,
            "P7-09 epoch family update did not match.");
        return new P709EpochFixture(
            seed.WorkId,
            seed.FamilyId,
            seed.VersionId,
            canonical.PayloadHash,
            seed.TargetUnitId,
            seed.SourceUserId,
            form);
    }

    private static async Task<P709EpochTarget>
        PrepareP709EpochTargetAsync(
            ApiHarnessClient api,
            IMongoDatabase database,
            string adminToken,
            string actorToken,
            string flowInstanceId,
            int executionEpoch,
            CancellationToken ct)
    {
        DynamicFlowStepInstance? step = null;
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            step = await database
                .GetCollection<DynamicFlowStepInstance>(
                    "dynamic_flow_step_instances")
                .Find(item =>
                    item.FlowInstanceId == flowInstanceId &&
                    item.ExecutionEpoch == executionEpoch &&
                    item.IsCanonicalEpoch != false &&
                    !item.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (step is not null &&
                !string.IsNullOrWhiteSpace(
                    step.AssignmentId))
            {
                break;
            }
            await Task.Delay(50, ct);
        }
        if (step is null ||
            string.IsNullOrWhiteSpace(step.AssignmentId))
        {
            throw new InvalidOperationException(
                $"P7-09 epoch {executionEpoch} assignment did not materialize.");
        }
        Require(
            step.FlowStepId == "step_a" &&
            step.ExecutionEpoch == executionEpoch,
            "P7-09 epoch target step identity drifted.");
        var period = await WaitForP601PeriodAsync(
            database,
            step.AssignmentId,
            ct);
        var open = await api.PostAsync(
            $"api/work-report-periods/{period.Id}/open",
            body: null,
            actorToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            open,
            HttpStatusCode.OK,
            $"P7-09 open epoch {executionEpoch} report");
        var reportId = ApiHarnessClient.RequiredString(
            open.Json,
            "id");
        step = await WaitForP7MappingTargetReportPinAsync(
            api,
            adminToken,
            database,
            step.Id,
            reportId,
            ct);
        return new P709EpochTarget(
            flowInstanceId,
            executionEpoch,
            step.Id,
            step.AssignmentId!,
            reportId,
            actorToken);
    }

    private static async Task<P709BindingBundle>
        ApplyP709EpochMappingAsync(
            ApiHarnessClient api,
            IMongoDatabase database,
            P709EpochTarget target,
            string commandId,
            CancellationToken ct)
    {
        var preview = await api.PostAsync(
            P709PreviewPath(target.ReportId),
            new JsonObject(),
            target.ActorToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            preview,
            HttpStatusCode.OK,
            $"{commandId} epoch preview");
        Require(
            ApiHarnessClient.RequiredString(
                preview.Json,
                "flowInstanceId") ==
            target.FlowInstanceId &&
            ApiHarnessClient.RequiredInt(
                preview.Json,
                "executionEpoch") ==
            target.ExecutionEpoch &&
            ApiHarnessClient.RequiredString(
                preview.Json,
                "stepInstanceId") ==
            target.StepInstanceId &&
            ApiHarnessClient.RequiredString(
                preview.Json,
                "stepId") == "step_a" &&
            ApiHarnessClient.RequiredBool(
                preview.Json,
                "canPreview") &&
            ApiHarnessClient.RequiredBool(
                preview.Json,
                "canApply"),
            "P7-09 epoch mapping preview pins drifted.");
        var request = BuildP709ApplyRequest(
            preview,
            commandId);
        var apply = await api.PostAsync(
            P709ApplyPath(target.ReportId),
            request,
            target.ActorToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            apply,
            HttpStatusCode.OK,
            $"{commandId} epoch apply");
        var binding = await LoadAndAssertP709BindingAsync(
            database,
            target.ReportId,
            DynamicFlowMappingProvenanceStates.Current,
            DynamicFlowMappingEventTypes.ApplyCommitted,
            DynamicFlowMappingLifecycleContract
                .ApplyInitialOperation,
            ct);
        Require(
            binding.Provenance.RuntimePin.FlowInstanceId ==
            target.FlowInstanceId &&
            binding.Provenance.RuntimePin.ExecutionEpoch ==
            target.ExecutionEpoch &&
            binding.Provenance.SourcePins.Count == 0,
            "P7-09 constant epoch mapping persisted unexpected source/runtime pins.");
        return binding;
    }

    private static async Task AssertP709EpochInvalidationAsync(
        IMongoDatabase database,
        P709BindingBundle binding,
        string eventId,
        string rebuildIntentId,
        CancellationToken ct)
    {
        var provenance = await database
            .GetCollection<DynamicFlowMappingProvenanceRecord>(
                "dynamic_flow_mapping_provenance")
            .Find(item =>
                item.Id == binding.Provenance.Id)
            .SingleAsync(ct);
        var report = await LoadP709ReportAsync(
            database,
            binding.Report.Id,
            ct);
        var rebuild = await database
            .GetCollection<DynamicFlowRuntimeOutboxItem>(
                "dynamic_flow_runtime_outbox")
            .Find(item => item.Id == rebuildIntentId)
            .SingleAsync(ct);
        var runtimeEvent = await database
            .GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events")
            .Find(item => item.Id == eventId)
            .SingleAsync(ct);
        Require(
            provenance.State ==
            DynamicFlowMappingProvenanceStates.Invalidated &&
            provenance.InvalidatedByEventId == eventId &&
            provenance.InvalidationReason ==
            DynamicFlowMappingLifecycleContract
                .EpochInvalidatedReason &&
            provenance.InvalidatedAtUtc.HasValue &&
            provenance.SupersededByProvenanceId is null,
            "P7-09 epoch command did not invalidate mapping provenance.");
        Require(
            !report.IsCurrent &&
            !report.IsActive &&
            report.InvalidatedByFlowEventId == eventId,
            "P7-09 epoch command did not invalidate the mapped report closure.");
        Require(
            rebuild.Operation ==
            DynamicFlowFinalizeTopologyContract
                .RebuildIntentOperation &&
            rebuild.Status ==
            DynamicFlowRuntimeOutboxStatuses.Completed &&
            rebuild.Payload["mappingRebuildOnly"]
                .AsBoolean &&
            !rebuild.Payload["p8ExecutionEnabled"]
                .AsBoolean &&
            !rebuild.Payload["p9ExecutionEnabled"]
                .AsBoolean &&
            rebuild.Payload["mappingProvenanceIds"]
                .AsBsonArray
                .Select(item => item.AsString)
                .SequenceEqual(
                    [provenance.Id],
                    StringComparer.Ordinal),
            "P7-09 epoch rebuild intent is not mapping-only/P8-P9-disabled.");
        Require(
            runtimeEvent.Payload["mappingProvenanceIds"]
                .AsBsonArray
                .Select(item => item.AsString)
                .Contains(
                    provenance.Id,
                    StringComparer.Ordinal) &&
            !runtimeEvent.Payload["p8ExecutionEnabled"]
                .AsBoolean &&
            !runtimeEvent.Payload["p9ExecutionEnabled"]
                .AsBoolean &&
            runtimeEvent.AffectedRefs.Contains(
                $"mappingProvenance:{provenance.Id}",
                StringComparer.Ordinal),
            "P7-09 runtime event lost invalidated mapping provenance.");
        var mappingCounts =
            await CountP709MappingLedgerAsync(
                database,
                report.Id,
                ct);
        Require(
            mappingCounts.Receipts == 1 &&
            mappingCounts.Provenance == 1 &&
            mappingCounts.Events == 1 &&
            mappingCounts.Outbox == 1 &&
            mappingCounts.MappingAudits == 1,
            "P7-09 epoch invalidation wrote a second mapping apply/event/outbox row.");
    }

    private static Task<long> CountP709EpochRebuildsAsync(
        IMongoDatabase database,
        string flowInstanceId,
        CancellationToken ct)
        => database
            .GetCollection<DynamicFlowRuntimeOutboxItem>(
                "dynamic_flow_runtime_outbox")
            .CountDocumentsAsync(
                item =>
                    item.FlowInstanceId == flowInstanceId &&
                    item.Operation ==
                    DynamicFlowFinalizeTopologyContract
                        .RebuildIntentOperation,
                cancellationToken: ct);

    private static async Task<P709DeferredExecutionCounts>
        CountP709DeferredExecutionRowsAsync(
            IMongoDatabase database,
            CancellationToken ct)
    {
        async Task<long> CountAsync(string collection)
            => await database
                .GetCollection<BsonDocument>(collection)
                .CountDocumentsAsync(
                    FilterDefinition<BsonDocument>.Empty,
                    cancellationToken: ct);

        return new P709DeferredExecutionCounts(
            await CountAsync(
                "work_report_field_stat_values"),
            await CountAsync(
                "work_report_field_stat_aggregates"),
            await CountAsync(
                "work_report_table_stat_values"),
            await CountAsync(
                "work_report_table_stat_aggregates"),
            await CountAsync(
                "work_report_label_stat_values"),
            await CountAsync(
                "work_report_label_stat_aggregates"),
            await CountAsync(
                "work_report_statistic_rebuild_jobs"),
            await CountAsync(
                "work_assignment_basic_summary_snapshots"),
            await CountAsync(
                "work_assignment_advanced_summary_day_nodes"),
            await CountAsync(
                "work_assignment_advanced_summary_month_nodes"),
            await CountAsync(
                "work_assignment_advanced_summary_year_nodes"),
            await CountAsync(
                "work_summary_token_ledgers"));
    }

    private sealed record P709MappedScenario(
        P7MappingScenario Scenario,
        JsonObject InitialApplyRequest,
        P709BindingBundle Binding);

    private sealed record P709BindingBundle(
        WorkAssignmentReport Report,
        DynamicFlowMappingApplyReceipt Receipt,
        DynamicFlowMappingProvenanceRecord Provenance,
        DynamicFlowMappingEvent Event,
        DynamicFlowMappingOutboxItem Outbox,
        WorkReportPayload Payload,
        IReadOnlyList<WorkAssignmentReportSection> Sections);

    private sealed record P709InvalidationBundle(
        DynamicFlowMappingProvenanceRecord Provenance,
        DynamicFlowMappingEvent Event,
        DynamicFlowMappingOutboxItem Outbox);

    private sealed record P709MappingLedgerCounts(
        long Receipts,
        long Provenance,
        long Events,
        long Outbox,
        long MappingAudits);

    private sealed record P709MongoScope(
        string Collection,
        FilterDefinition<BsonDocument> Filter);

    private sealed record P709MappingReference(
        string ReceiptId,
        string ProvenanceId,
        string ProvenanceHash,
        int ResultPayloadRevision,
        string ResultPayloadHash)
    {
        public static P709MappingReference From(
            WorkAssignmentReport report)
        {
            if (string.IsNullOrWhiteSpace(
                    report.DynamicFlowMappingReceiptId) ||
                string.IsNullOrWhiteSpace(
                    report.DynamicFlowMappingProvenanceId) ||
                !DynamicFlowMappingLifecycleContract.IsLowerSha256(
                    report.DynamicFlowMappingProvenanceHash) ||
                report.DynamicFlowMappingResultPayloadRevision
                    is not > 0 ||
                !DynamicFlowMappingLifecycleContract.IsLowerSha256(
                    report.DynamicFlowMappingResultPayloadHash))
            {
                throw new InvalidOperationException(
                    $"Report {report.Id} lacks a complete P7 mapping binding.");
            }
            return new P709MappingReference(
                report.DynamicFlowMappingReceiptId,
                report.DynamicFlowMappingProvenanceId,
                report.DynamicFlowMappingProvenanceHash!,
                report.DynamicFlowMappingResultPayloadRevision.Value,
                report.DynamicFlowMappingResultPayloadHash!);
        }
    }

    private sealed record P709EpochFixture(
        string WorkId,
        string FamilyId,
        string VersionId,
        string PayloadHash,
        string TargetUnitId,
        string ActorUserId,
        ProbeFormPinSnapshot Form);

    private sealed record P709EpochTarget(
        string FlowInstanceId,
        int ExecutionEpoch,
        string StepInstanceId,
        string AssignmentId,
        string ReportId,
        string ActorToken);

    private sealed record P709DeferredExecutionCounts(
        long FieldValues,
        long FieldAggregates,
        long TableValues,
        long TableAggregates,
        long LabelValues,
        long LabelAggregates,
        long StatisticRebuildJobs,
        long BasicSummarySnapshots,
        long AdvancedSummaryDayNodes,
        long AdvancedSummaryMonthNodes,
        long AdvancedSummaryYearNodes,
        long SummaryTokenLedgers);
}
