using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Real Kestrel/replica-set proof for the P7-08 durable mapping writer.
/// Every negative request is surrounded by a direct-Mongo scoped hash, while
/// successful requests are verified from the canonical receipt and projections.
/// </summary>
internal static partial class P5MaterializationProbe
{
    private static async Task RunP708MappingCasesAsync(
        HarnessCaseRunner cases,
        List<object> mongoEvidence,
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        MongoReplicaSetLease mongo,
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        string adminToken,
        ProbeFixture baseFixture,
        P7MappingFixture fixture,
        int activationThrough,
        CancellationToken ct)
    {
        P708AppliedScenario? canonical = null;

        await cases.RunAsync(
            "P7-08-01-ATOMIC-APPLY-EXACT-WRITE-SET",
            async () =>
            {
                var prepared = await PrepareP708ApplyScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "P708-ATOMIC",
                    "p708-atomic-apply",
                    activationThrough,
                    ct);
                var beforeCounts = await CountP709MappingLedgerAsync(
                    database,
                    prepared.Scenario.TargetReportId,
                    ct);
                RequireP708EmptyMappingLedger(
                    beforeCounts,
                    "P7-08 atomic apply baseline");

                var adminDatabase =
                    database.Client.GetDatabase("admin");
                var transactionBefore =
                    await CaptureP611MongoTransactionGaugeAsync(
                        adminDatabase,
                        ct);
                var apply = await api.PostAsync(
                    P709ApplyPath(prepared.Scenario.TargetReportId),
                    CloneP709Request(prepared.Request),
                    prepared.Scenario.ActorToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    apply,
                    HttpStatusCode.OK,
                    "P7-08 initial durable apply");
                var transactionAfter =
                    await CaptureP611MongoTransactionGaugeAsync(
                        adminDatabase,
                        ct);

                var binding = await LoadAndAssertP709BindingAsync(
                    database,
                    prepared.Scenario.TargetReportId,
                    DynamicFlowMappingProvenanceStates.Current,
                    DynamicFlowMappingEventTypes.ApplyCommitted,
                    DynamicFlowMappingLifecycleContract
                        .ApplyInitialOperation,
                    ct);
                var afterCounts = await CountP709MappingLedgerAsync(
                    database,
                    prepared.Scenario.TargetReportId,
                    ct);
                RequireP708SingleMappingWriteSet(
                    afterCounts,
                    "P7-08 initial durable apply");
                Require(
                    transactionAfter.TotalStarted >=
                    transactionBefore.TotalStarted + 3 &&
                    transactionAfter.TotalAborted ==
                    transactionBefore.TotalAborted &&
                    transactionAfter.CurrentOpen ==
                    transactionBefore.CurrentOpen,
                    "P7-08 apply/reconcile did not close its atomic business transaction and foreground reconcile transactions cleanly.");
                AssertP708ApplyResponse(
                    apply,
                    binding,
                    "P7-08 initial durable apply");

                canonical = new P708AppliedScenario(
                    new P709MappedScenario(
                        prepared.Scenario,
                        CloneP709Request(prepared.Request),
                        binding),
                    apply);
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-08-01-ATOMIC-APPLY-EXACT-WRITE-SET",
                    targetReportId =
                        prepared.Scenario.TargetReportId,
                    binding.Receipt.Id,
                    provenanceId = binding.Provenance.Id,
                    eventId = binding.Event.Id,
                    outboxId = binding.Outbox.Id,
                    beforeCounts,
                    afterCounts,
                    transactionBefore,
                    transactionAfter,
                    transactionDelta =
                        transactionAfter.TotalStarted -
                        transactionBefore.TotalStarted,
                    responseBindingExposed =
                        P708ResponseBindingExposed(apply)
                });
                return new CaseObservation(
                    "One signed Kestrel apply committed one atomic business write set; foreground retry/finalize transactions reconciled that same intent without another business row.",
                    P7MappingFingerprint(
                        "P7-08-01",
                        binding.Receipt.Id,
                        binding.Provenance.Id,
                        binding.Receipt.WriteSetHash,
                        transactionBefore.ToString() ?? string.Empty,
                        transactionAfter.ToString() ?? string.Empty));
            });

        await cases.RunAsync(
            "P7-08-02-EXACT-RETRY-CANONICAL-ZERO-WRITE",
            async () =>
            {
                var applied = RequireP708Applied(
                    canonical,
                    "P7-08 exact retry");
                var before = await CaptureP709ScopedLedgerHashAsync(
                    database,
                    applied.Mapped.Scenario.TargetReportId,
                    ct);
                var countsBefore = await CountP709MappingLedgerAsync(
                    database,
                    applied.Mapped.Scenario.TargetReportId,
                    ct);
                var replay = await api.PostAsync(
                    P709ApplyPath(
                        applied.Mapped.Scenario.TargetReportId),
                    CloneP709Request(
                        applied.Mapped.InitialApplyRequest),
                    applied.Mapped.Scenario.ActorToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    replay,
                    HttpStatusCode.OK,
                    "P7-08 exact apply retry");
                var after = await CaptureP709ScopedLedgerHashAsync(
                    database,
                    applied.Mapped.Scenario.TargetReportId,
                    ct);
                var countsAfter = await CountP709MappingLedgerAsync(
                    database,
                    applied.Mapped.Scenario.TargetReportId,
                    ct);
                Require(
                    before == after && countsBefore == countsAfter,
                    "P7-08 exact retry changed the scoped Mongo write set.");

                var replayBinding =
                    await LoadAndAssertP709BindingAsync(
                        database,
                        applied.Mapped.Scenario.TargetReportId,
                        DynamicFlowMappingProvenanceStates.Current,
                        DynamicFlowMappingEventTypes.ApplyCommitted,
                        DynamicFlowMappingLifecycleContract
                            .ApplyInitialOperation,
                        ct);
                Require(
                    replayBinding.Receipt.Id ==
                    applied.Mapped.Binding.Receipt.Id &&
                    replayBinding.Provenance.Id ==
                    applied.Mapped.Binding.Provenance.Id &&
                    replayBinding.Event.Id ==
                    applied.Mapped.Binding.Event.Id &&
                    replayBinding.Outbox.Id ==
                    applied.Mapped.Binding.Outbox.Id,
                    "P7-08 exact retry did not resolve the canonical committed receipt.");
                AssertP708CanonicalReplayResponse(
                    applied.InitialResponse,
                    replay,
                    replayBinding);

                var manualFieldValues =
                    JsonNode.Parse(
                            replayBinding.Payload
                                .FieldValuesJson ?? "{}")
                        ?.AsObject() ??
                    new JsonObject();
                if (manualFieldValues["values"] is not
                    JsonObject manualValues)
                {
                    manualValues = new JsonObject();
                    manualFieldValues["values"] =
                        manualValues;
                }
                manualValues["field_child_value"] =
                    null;
                var manualValues1D =
                    JsonNode.Parse(
                        replayBinding.Payload.Values1DJson)
                    as JsonArray ??
                    new JsonArray();
                var manualSave = await api.PutAsync(
                    $"api/work-assignment-reports/{applied.Mapped.Scenario.TargetReportId}/draft",
                    new JsonObject
                    {
                        ["expectedPayloadRevision"] =
                            replayBinding.Report
                                .PayloadRevision,
                        ["commandId"] =
                            "p708-manual-after-mapping",
                        ["values1D"] = manualValues1D,
                        ["fieldValuesJson"] =
                            manualFieldValues
                                .ToJsonString(),
                        ["tableValuesJson"] =
                            replayBinding.Payload
                                .TableValuesRootJson,
                        ["dataOrigin"] =
                            replayBinding.Report.DataOrigin,
                        ["cumulativeContributionMode"] =
                            replayBinding.Report
                                .CumulativeContributionMode,
                        ["cumulativeContributionPolicyJson"] =
                            replayBinding.Report
                                .CumulativeContributionPolicyJson,
                        ["summarySourceJson"] =
                            replayBinding.Payload
                                .SummarySourceJson,
                        ["completedDate"] =
                            replayBinding.Report
                                .CompletedDate,
                        ["lateReason"] =
                            replayBinding.Report.LateReason,
                        ["note"] =
                            replayBinding.Report.Note
                    },
                    applied.Mapped.Scenario.ActorToken,
                    ct: ct);
                RequireP709Success(
                    manualSave,
                    "P7-08 valid manual save after mapping");
                var manuallyAdvanced =
                    await LoadP709ReportAsync(
                        database,
                        applied.Mapped.Scenario
                            .TargetReportId,
                        ct);
                var manuallyAdvancedPayload =
                    await database
                        .GetCollection<WorkReportPayload>(
                            "work_report_payloads")
                        .Find(item =>
                            item.ReportId ==
                            manuallyAdvanced.Id &&
                            !item.IsDeleted)
                        .SingleAsync(ct);
                Require(
                    manuallyAdvanced.PayloadRevision ==
                    replayBinding.Receipt
                        .ResultPayloadRevision + 1 &&
                    manuallyAdvanced.PayloadHash !=
                    replayBinding.Receipt
                        .ResultPayloadHash &&
                    manuallyAdvanced.LifecycleRevision ==
                    replayBinding.Receipt
                        .ResultLifecycleRevision &&
                    manuallyAdvancedPayload
                        .PayloadRevision ==
                    manuallyAdvanced.PayloadRevision &&
                    manuallyAdvancedPayload.PayloadHash ==
                    manuallyAdvanced.PayloadHash,
                    "P7-08 valid manual save did not advance the current payload beyond the receipt-bound mapping result.");
                var postManualHash =
                    await CaptureP709ScopedLedgerHashAsync(
                        database,
                        applied.Mapped.Scenario
                            .TargetReportId,
                        ct);
                var postManualCounts =
                    await CountP709MappingLedgerAsync(
                        database,
                        applied.Mapped.Scenario
                            .TargetReportId,
                        ct);
                Require(
                    postManualCounts == countsAfter,
                    "P7-08 valid manual save created a mapping ledger row.");

                var replayAfterManual =
                    await api.PostAsync(
                        P709ApplyPath(
                            applied.Mapped.Scenario
                                .TargetReportId),
                        CloneP709Request(
                            applied.Mapped
                                .InitialApplyRequest),
                        applied.Mapped.Scenario
                            .ActorToken,
                        ct: ct);
                ApiHarnessClient.ExpectStatus(
                    replayAfterManual,
                    HttpStatusCode.OK,
                    "P7-08 exact mapping replay after manual save");
                var afterManualReplayHash =
                    await CaptureP709ScopedLedgerHashAsync(
                        database,
                        applied.Mapped.Scenario
                            .TargetReportId,
                        ct);
                var afterManualReplayCounts =
                    await CountP709MappingLedgerAsync(
                        database,
                        applied.Mapped.Scenario
                            .TargetReportId,
                        ct);
                Require(
                    postManualHash ==
                    afterManualReplayHash &&
                    postManualCounts ==
                    afterManualReplayCounts,
                    "P7-08 exact mapping replay after a newer manual payload wrote or duplicated a mapping row.");
                var postManualReplayBinding =
                    await LoadAndAssertP709BindingAsync(
                        database,
                        applied.Mapped.Scenario
                            .TargetReportId,
                        DynamicFlowMappingProvenanceStates
                            .Current,
                        DynamicFlowMappingEventTypes
                            .ApplyCommitted,
                        DynamicFlowMappingLifecycleContract
                            .ApplyInitialOperation,
                        ct);
                Require(
                    postManualReplayBinding.Receipt.Id ==
                    replayBinding.Receipt.Id &&
                    postManualReplayBinding.Provenance.Id ==
                    replayBinding.Provenance.Id &&
                    postManualReplayBinding.Event.Id ==
                    replayBinding.Event.Id &&
                    postManualReplayBinding.Outbox.Id ==
                    replayBinding.Outbox.Id,
                    "P7-08 post-manual exact replay did not retain the original mapping identities.");
                AssertP708CanonicalReplayResponse(
                    applied.InitialResponse,
                    replayAfterManual,
                    postManualReplayBinding);
                Require(
                    ApiHarnessClient.RequiredInt(
                        replayAfterManual.Json,
                        "payloadRevision") ==
                    replayBinding.Receipt
                        .ResultPayloadRevision &&
                    ApiHarnessClient.FindStringRecursive(
                        replayAfterManual.Json,
                        "payloadHash") ==
                    replayBinding.Receipt
                        .ResultPayloadHash &&
                    ApiHarnessClient.RequiredInt(
                        replayAfterManual.Json,
                        "lifecycleRevision") ==
                    replayBinding.Receipt
                        .ResultLifecycleRevision &&
                    P708ResponseReceiptId(
                        replayAfterManual) ==
                    replayBinding.Receipt.Id &&
                    P708ResponseCommandId(
                        replayAfterManual) ==
                    replayBinding.Receipt.CommandId &&
                    P708ResponseSemanticHash(
                        replayAfterManual) ==
                    replayBinding.Receipt
                        .ResultSemanticHash &&
                    P708ResponseApplyState(
                        replayAfterManual) ==
                    replayBinding.Receipt.State &&
                    manuallyAdvanced.PayloadRevision !=
                    replayBinding.Receipt
                        .ResultPayloadRevision &&
                    manuallyAdvanced.PayloadHash !=
                    replayBinding.Receipt
                        .ResultPayloadHash,
                    "P7-08 post-manual exact replay returned the newer current header instead of the original receipt-bound result.");

                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-08-02-EXACT-RETRY-CANONICAL-ZERO-WRITE",
                    targetReportId =
                        applied.Mapped.Scenario.TargetReportId,
                    receiptId = replayBinding.Receipt.Id,
                    provenanceId = replayBinding.Provenance.Id,
                    before,
                    after,
                    countsBefore,
                    countsAfter,
                    manualSaveStatus =
                        (int)manualSave.StatusCode,
                    receiptBoundPayloadRevision =
                        replayBinding.Receipt
                            .ResultPayloadRevision,
                    receiptBoundPayloadHash =
                        replayBinding.Receipt
                            .ResultPayloadHash,
                    receiptBoundLifecycleRevision =
                        replayBinding.Receipt
                            .ResultLifecycleRevision,
                    currentPayloadRevision =
                        manuallyAdvanced.PayloadRevision,
                    currentPayloadHash =
                        manuallyAdvanced.PayloadHash,
                    currentLifecycleRevision =
                        manuallyAdvanced
                            .LifecycleRevision,
                    postManualHash,
                    afterManualReplayHash,
                    postManualCounts,
                    afterManualReplayCounts,
                    postManualReplayReceiptId =
                        P708ResponseReceiptId(
                            replayAfterManual),
                    postManualReplayCommandId =
                        P708ResponseCommandId(
                            replayAfterManual),
                    postManualReplaySemanticHash =
                        P708ResponseSemanticHash(
                            replayAfterManual),
                    postManualReplayApplyState =
                        P708ResponseApplyState(
                            replayAfterManual),
                    zeroWrite = true,
                    zeroNewMappingRows = true
                });
                return new CaseObservation(
                    "Exact retries stayed byte-stable, including after a valid manual save advanced the current payload: replay still returned the original receipt-bound mapping result with zero new mapping rows.",
                    P7MappingFingerprint(
                        "P7-08-02",
                        replayBinding.Receipt.Id,
                        replayBinding.Provenance.Id,
                        before,
                        after,
                        postManualHash,
                        afterManualReplayHash,
                        manuallyAdvanced.PayloadRevision
                            .ToString()));
            });

        await cases.RunAsync(
            "P7-08-03-CHANGED-REPLAY-MISMATCH-ZERO-WRITE",
            async () =>
            {
                var applied = RequireP708Applied(
                    canonical,
                    "P7-08 changed replay");
                var changed = CloneP709Request(
                    applied.Mapped.InitialApplyRequest);
                changed["resultSemanticHash"] =
                    P708DifferentHash(
                        ApiHarnessClient.RequiredString(
                            changed,
                            "resultSemanticHash"));
                var outcome =
                    await AssertP708ConflictAndZeroWriteAsync(
                        api,
                        database,
                        applied.Mapped.Scenario.TargetReportId,
                        changed,
                        applied.Mapped.Scenario.ActorToken,
                        "P7-08 changed same-command replay",
                        "DYNAMIC_FLOW_MAPPING_COMMAND_REPLAY_MISMATCH",
                        ct);
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-08-03-CHANGED-REPLAY-MISMATCH-ZERO-WRITE",
                    targetReportId =
                        applied.Mapped.Scenario.TargetReportId,
                    outcome.ErrorCode,
                    outcome.BeforeHash,
                    outcome.AfterHash,
                    zeroWrite = true
                });
                return new CaseObservation(
                    "The same command id with a changed canonical request hash failed with the stable mismatch code before any write.",
                    P7MappingFingerprint(
                        "P7-08-03",
                        outcome.ErrorCode,
                        outcome.BeforeHash,
                        outcome.AfterHash));
            });

        await cases.RunAsync(
            "P7-08-04-STALE-TARGET-CAS-ZERO-WRITE",
            async () =>
            {
                var prepared = await PrepareP708ApplyScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "P708-STALE-TARGET",
                    "p708-stale-target-apply",
                    activationThrough,
                    ct);
                var stale = CloneP709Request(
                    prepared.Request);
                var outcome =
                    await ExecuteP707WithTargetLifecycleRevisionAsync(
                        database,
                        prepared.Scenario,
                        () => AssertP708ConflictAndZeroWriteAsync(
                            api,
                            database,
                            prepared.Scenario.TargetReportId,
                            stale,
                            prepared.Scenario.ActorToken,
                            "P7-08 stale target apply",
                            "DYNAMIC_FLOW_MAPPING_TARGET_REVISION_CONFLICT",
                            ct),
                        ct);
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-08-04-STALE-TARGET-CAS-ZERO-WRITE",
                    targetReportId =
                        prepared.Scenario.TargetReportId,
                    outcome.ErrorCode,
                    outcome.BeforeHash,
                    outcome.AfterHash,
                    zeroWrite = true
                });
                return new CaseObservation(
                    "A fresh command whose preview target lifecycle CAS was advanced before apply failed without an orphan receipt or projection.",
                    P7MappingFingerprint(
                        "P7-08-04",
                        outcome.ErrorCode,
                        outcome.BeforeHash,
                        outcome.AfterHash));
            });

        await cases.RunAsync(
            "P7-08-05-CONCURRENT-CAS-EXACTLY-ONE-WINNER",
            async () =>
            {
                var prepared = await PrepareP708ApplyScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "P708-CONCURRENT",
                    "p708-concurrent-placeholder",
                    activationThrough,
                    ct);
                var secondPreview = await PreviewP7MappingAsync(
                    api,
                    prepared.Scenario,
                    new JsonObject(),
                    ct);
                AssertP7MappingPreviewIdentity(
                    secondPreview,
                    fixture,
                    prepared.Scenario,
                    activationThrough);
                var firstRequest = BuildP709ApplyRequest(
                    prepared.Preview,
                    "p708-concurrent-command-a");
                var secondRequest = BuildP709ApplyRequest(
                    secondPreview,
                    "p708-concurrent-command-b");
                var beforeCounts = await CountP709MappingLedgerAsync(
                    database,
                    prepared.Scenario.TargetReportId,
                    ct);
                RequireP708EmptyMappingLedger(
                    beforeCounts,
                    "P7-08 concurrent baseline");

                var responses = await Task.WhenAll(
                    api.PostAsync(
                        P709ApplyPath(
                            prepared.Scenario.TargetReportId),
                        firstRequest,
                        prepared.Scenario.ActorToken,
                        ct: ct),
                    api.PostAsync(
                        P709ApplyPath(
                            prepared.Scenario.TargetReportId),
                        secondRequest,
                        prepared.Scenario.ActorToken,
                        ct: ct));
                Require(
                    responses.Count(item =>
                        item.StatusCode == HttpStatusCode.OK) == 1 &&
                    responses.Count(item =>
                        item.StatusCode ==
                        HttpStatusCode.Conflict) == 1,
                    "P7-08 concurrent commands did not produce exactly one HTTP 200 winner and one HTTP 409 loser.");
                var loser = responses.Single(item =>
                    item.StatusCode == HttpStatusCode.Conflict);
                var loserCode = P708ErrorCode(loser);
                Require(
                    loserCode ==
                    "DYNAMIC_FLOW_MAPPING_TARGET_REVISION_CONFLICT",
                    $"P7-08 concurrent CAS loser returned unstable code '{loserCode}'.");

                var afterCounts = await CountP709MappingLedgerAsync(
                    database,
                    prepared.Scenario.TargetReportId,
                    ct);
                RequireP708SingleMappingWriteSet(
                    afterCounts,
                    "P7-08 concurrent winner");
                var binding = await LoadAndAssertP709BindingAsync(
                    database,
                    prepared.Scenario.TargetReportId,
                    DynamicFlowMappingProvenanceStates.Current,
                    DynamicFlowMappingEventTypes.ApplyCommitted,
                    DynamicFlowMappingLifecycleContract
                        .ApplyInitialOperation,
                    ct);
                var currentCount = await database
                    .GetCollection<DynamicFlowMappingProvenanceRecord>(
                        "dynamic_flow_mapping_provenance")
                    .CountDocumentsAsync(
                        item =>
                            item.TargetReportId ==
                            prepared.Scenario.TargetReportId &&
                            item.State ==
                            DynamicFlowMappingProvenanceStates.Current,
                        cancellationToken: ct);
                Require(
                    currentCount == 1,
                    "P7-08 concurrent CAS retained multiple canonical provenance rows.");

                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-08-05-CONCURRENT-CAS-EXACTLY-ONE-WINNER",
                    targetReportId =
                        prepared.Scenario.TargetReportId,
                    statuses = responses
                        .Select(item => (int)item.StatusCode)
                        .OrderBy(item => item)
                        .ToArray(),
                    loserCode,
                    beforeCounts,
                    afterCounts,
                    currentCount,
                    receiptId = binding.Receipt.Id,
                    provenanceId = binding.Provenance.Id
                });
                return new CaseObservation(
                    "Two different signed commands raced the same target CAS; one committed and the loser left no durable row.",
                    P7MappingFingerprint(
                        "P7-08-05",
                        binding.Receipt.Id,
                        binding.Provenance.Id,
                        loserCode,
                        beforeCounts.ToString() ?? string.Empty,
                        afterCounts.ToString() ?? string.Empty));
            });

        await cases.RunAsync(
            "P7-08-06-DIRECT-MONGO-DURABLE-BINDING",
            async () =>
            {
                var applied = RequireP708Applied(
                    canonical,
                    "P7-08 direct durable binding");
                var binding = await LoadAndAssertP709BindingAsync(
                    database,
                    applied.Mapped.Scenario.TargetReportId,
                    DynamicFlowMappingProvenanceStates.Current,
                    DynamicFlowMappingEventTypes.ApplyCommitted,
                    DynamicFlowMappingLifecycleContract
                        .ApplyInitialOperation,
                    ct);
                var period = await database
                    .GetCollection<WorkReportPeriod>(
                        "work_report_periods")
                    .Find(item =>
                        item.Id ==
                        binding.Report.WorkReportPeriodId &&
                        !item.IsDeleted)
                    .SingleAsync(ct);
                var audits = await database
                    .GetCollection<WorkAssignmentReportLog>(
                        "work_assignment_report_logs")
                    .Find(item =>
                        item.WorkAssignmentReportId ==
                        binding.Report.Id &&
                        item.Action ==
                        "APPLY_DYNAMIC_FLOW_MAPPING" &&
                        !item.IsDeleted)
                    .ToListAsync(ct);
                Require(
                    audits.Count == 1,
                    "P7-08 direct Mongo found a duplicate or missing mapping audit.");
                var audit = audits[0];
                var auditSnapshot = BsonDocument.Parse(
                    audit.SnapshotJson ??
                    throw new InvalidOperationException(
                        "P7-08 mapping audit snapshot is missing."));
                Require(
                    P708BsonText(auditSnapshot, "receiptId") ==
                    binding.Receipt.Id &&
                    P708BsonText(auditSnapshot, "provenanceId") ==
                    binding.Provenance.Id &&
                    P708BsonText(auditSnapshot, "eventId") ==
                    binding.Event.Id &&
                    P708BsonText(auditSnapshot, "targetReportId") ==
                    binding.Report.Id &&
                    audit.LifecycleEventKey ==
                    binding.Event.EventKey,
                    "P7-08 mapping audit lost exact receipt/provenance/event/header binding.");
                Require(
                    binding.Outbox.Intent.ReceiptId ==
                    binding.Receipt.Id &&
                    binding.Outbox.Intent.ProvenanceId ==
                    binding.Provenance.Id &&
                    binding.Outbox.Intent.TargetReportId ==
                    binding.Report.Id &&
                    binding.Outbox.Intent.TargetPayloadRevision ==
                    binding.Receipt.ResultPayloadRevision &&
                    binding.Outbox.Intent.TargetPayloadHash ==
                    binding.Receipt.ResultPayloadHash,
                    "P7-08 immutable reconcile intent lost the receipt-bound committed mapping result.");
                Require(
                    period.CurrentReportId == binding.Report.Id &&
                    period.WorkAssignmentId ==
                    binding.Report.WorkAssignmentId &&
                    period.Id ==
                    binding.Report.WorkReportPeriodId &&
                    period.Status is WorkReportPeriodStatus.Draft or
                        WorkReportPeriodStatus.OverdueDraft,
                    "P7-08 period projection is not bound to the mapped draft.");
                Require(
                    IsP7LowerSha256(
                        binding.Receipt.ResultSnapshotHash) &&
                    IsP7LowerSha256(
                        binding.Receipt.WriteSetHash) &&
                    IsP7LowerSha256(
                        binding.Provenance.ProvenanceHash) &&
                    IsP7LowerSha256(binding.Event.PayloadHash) &&
                    IsP7LowerSha256(binding.Outbox.IntentHash) &&
                    binding.Receipt.State ==
                    DynamicFlowMappingApplyStates.Reconciled &&
                    binding.Outbox.State ==
                    DynamicFlowMappingOutboxStates.Reconciled,
                    "P7-08 durable hashes or terminal RECONCILED states drifted.");

                var counts = await CountP709MappingLedgerAsync(
                    database,
                    binding.Report.Id,
                    ct);
                RequireP708SingleMappingWriteSet(
                    counts,
                    "P7-08 direct durable binding");
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-08-06-DIRECT-MONGO-DURABLE-BINDING",
                    targetReportId = binding.Report.Id,
                    receiptId = binding.Receipt.Id,
                    receiptState = binding.Receipt.State,
                    provenanceId = binding.Provenance.Id,
                    provenanceState = binding.Provenance.State,
                    eventId = binding.Event.Id,
                    outboxId = binding.Outbox.Id,
                    outboxState = binding.Outbox.State,
                    auditId = audit.Id,
                    periodId = period.Id,
                    periodStatus = period.Status.ToString(),
                    sectionCount = binding.Sections.Count,
                    counts,
                    binding.Receipt.WriteSetHash
                });
                return new CaseObservation(
                    "Direct Mongo joined the exact report header, payload, period, sections, receipt, provenance, event, immutable outbox intent, and audit at RECONCILED.",
                    P7MappingFingerprint(
                        "P7-08-06",
                        binding.Receipt.Id,
                        binding.Provenance.Id,
                        binding.Event.Id,
                        binding.Outbox.Id,
                        audit.Id,
                        period.Id,
                        binding.Receipt.WriteSetHash));
            });

        await cases.RunAsync(
            "P7-08-07-MANUAL-PROVENANCE-TAMPER-GUARD",
            async () =>
            {
                var forged = await CreateP709MappedScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "P708-TAMPER-FORGE",
                    "p708-tamper-forge-apply",
                    ct);
                var forgedHash = P708DifferentHash(
                    forged.Binding.Provenance.ProvenanceHash);
                var forgedUpdate = await database
                    .GetCollection<DynamicFlowMappingProvenanceRecord>(
                        "dynamic_flow_mapping_provenance")
                    .UpdateOneAsync(
                        item =>
                            item.Id ==
                            forged.Binding.Provenance.Id,
                        Builders<DynamicFlowMappingProvenanceRecord>
                            .Update
                            .Set(
                                item => item.ProvenanceHash,
                                forgedHash),
                        cancellationToken: ct);
                Require(
                    forgedUpdate.ModifiedCount == 1,
                    "P7-08 could not inject forged provenance.");
                var forgedOutcome =
                    await AssertP708ManualTamperBlockedAsync(
                        api,
                        database,
                        forged,
                        "p708-manual-forged-provenance",
                        forged.Binding.Provenance.Id,
                        ct);

                var removed = await CreateP709MappedScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "P708-TAMPER-REMOVE",
                    "p708-tamper-remove-apply",
                    ct);
                var removedDelete = await database
                    .GetCollection<DynamicFlowMappingProvenanceRecord>(
                        "dynamic_flow_mapping_provenance")
                    .DeleteOneAsync(
                        item =>
                            item.Id ==
                            removed.Binding.Provenance.Id,
                        ct);
                Require(
                    removedDelete.DeletedCount == 1,
                    "P7-08 could not inject removed provenance.");
                var removedOutcome =
                    await AssertP708ManualTamperBlockedAsync(
                        api,
                        database,
                        removed,
                        "p708-manual-removed-provenance",
                        string.Empty,
                        ct);

                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-08-07-MANUAL-PROVENANCE-TAMPER-GUARD",
                    forged = new
                    {
                        targetReportId =
                            forged.Scenario.TargetReportId,
                        provenanceId =
                            forged.Binding.Provenance.Id,
                        injectedHash = forgedHash,
                        forgedOutcome.ErrorCode,
                        forgedOutcome.BeforeHash,
                        forgedOutcome.AfterHash
                    },
                    removed = new
                    {
                        targetReportId =
                            removed.Scenario.TargetReportId,
                        provenanceId =
                            removed.Binding.Provenance.Id,
                        removedOutcome.ErrorCode,
                        removedOutcome.BeforeHash,
                        removedOutcome.AfterHash
                    },
                    rawPayloadRecorded = false,
                    noPostTamperWrite = true
                });
                return new CaseObservation(
                    "Manual payload requests could neither forge nor erase mapping-owned provenance; both injected corruptions failed with the stable tamper code and zero post-tamper writes.",
                    P7MappingFingerprint(
                        "P7-08-07",
                        forgedOutcome.ErrorCode,
                        forgedOutcome.BeforeHash,
                        forgedOutcome.AfterHash,
                        removedOutcome.ErrorCode,
                        removedOutcome.BeforeHash,
                        removedOutcome.AfterHash));
            });

        await cases.RunAsync(
            "P7-08-08-P709-LIFECYCLE-RERUN-INVALIDATION-BARRIER",
            async () =>
            {
                if (activationThrough >= 9)
                {
                    mongoEvidence.Add(new
                    {
                        caseId =
                            "P7-08-08-P709-LIFECYCLE-RERUN-INVALIDATION-BARRIER",
                        activationThrough,
                        barrierExpected = false,
                        successorSliceActive = true
                    });
                    return new CaseObservation(
                        "The cumulative runner is at P7-09 or later, so the temporary stage-8 lifecycle/rerun/invalidation barrier is intentionally lifted.",
                        P7MappingFingerprint(
                            "P7-08-08",
                            "barrier-lifted",
                            activationThrough.ToString()));
                }

                var mapped = await CreateP709MappedScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "P708-P709-BARRIER",
                    "p708-p709-barrier-apply",
                    ct);
                var before =
                    await CaptureP708LifecycleBarrierHashAsync(
                        database,
                        mapped,
                        ct);
                var report = await LoadP709ReportAsync(
                    database,
                    mapped.Scenario.TargetReportId,
                    ct);
                var source = await LoadP709ReportAsync(
                    database,
                    mapped.Scenario.SourceReportId,
                    ct);

                var submit = await api.PostAsync(
                    $"api/work-assignment-reports/{report.Id}/submit",
                    new JsonObject
                    {
                        ["expectedPayloadRevision"] =
                            report.PayloadRevision,
                        ["expectedLifecycleRevision"] =
                            report.LifecycleRevision,
                        ["commandId"] =
                            "p708-p709-barrier-submit"
                    },
                    mapped.Scenario.ActorToken,
                    ct: ct);
                RequireP708P709Barrier(
                    submit,
                    "P7-08 mapped submit");

                var approve = await api.PostAsync(
                    $"api/work-assignment-review/reports/{report.Id}/approve",
                    new JsonObject
                    {
                        ["expectedPayloadRevision"] =
                            report.PayloadRevision,
                        ["expectedLifecycleRevision"] =
                            report.LifecycleRevision,
                        ["commandId"] =
                            "p708-p709-barrier-approve",
                        ["comment"] =
                            "P7-08 approve must remain blocked"
                    },
                    adminToken,
                    ct: ct);
                RequireP708P709Barrier(
                    approve,
                    "P7-08 mapped approve");

                var rerunPreview = await PreviewP7MappingAsync(
                    api,
                    mapped.Scenario,
                    new JsonObject(),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    rerunPreview,
                    HttpStatusCode.OK,
                    "P7-08 rerun preview before P7-09 barrier");
                var rerun = await api.PostAsync(
                    P709ApplyPath(report.Id),
                    BuildP709ApplyRequest(
                        rerunPreview,
                        "p708-p709-barrier-rerun"),
                    mapped.Scenario.ActorToken,
                    ct: ct);
                RequireP708P709Barrier(
                    rerun,
                    "P7-08 mapped rerun");

                var invalidate = await api.PostAsync(
                    P709RecallPath(source.Id),
                    new JsonObject
                    {
                        ["expectedPayloadRevision"] =
                            source.PayloadRevision,
                        ["expectedLifecycleRevision"] =
                            source.LifecycleRevision,
                        ["commandId"] =
                            "p708-p709-barrier-source-recall",
                        ["comment"] =
                            "P7-08 invalidation must remain blocked"
                    },
                    adminToken,
                    ct: ct);
                RequireP708P709Barrier(
                    invalidate,
                    "P7-08 source invalidation");

                var after =
                    await CaptureP708LifecycleBarrierHashAsync(
                        database,
                        mapped,
                        ct);
                Require(
                    before == after,
                    "P7-08 P7-09 barrier attempts changed target/source persistence.");
                var reasons = new[]
                {
                    P708BarrierReason(submit),
                    P708BarrierReason(approve),
                    P708BarrierReason(rerun),
                    P708BarrierReason(invalidate)
                };
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-08-08-P709-LIFECYCLE-RERUN-INVALIDATION-BARRIER",
                    activationThrough,
                    targetReportId = report.Id,
                    sourceReportId = source.Id,
                    statuses = new[]
                    {
                        (int)submit.StatusCode,
                        (int)approve.StatusCode,
                        (int)rerun.StatusCode,
                        (int)invalidate.StatusCode
                    },
                    reasons,
                    before,
                    after,
                    zeroWrite = true
                });
                return new CaseObservation(
                    "At activation-through 8, mapped submit/approve, rerun, and source invalidation all stopped at the explicit P7-09 barrier with a byte-stable scoped ledger.",
                    P7MappingFingerprint(
                        "P7-08-08",
                        before,
                        after,
                        string.Join("\n", reasons)));
            });

        await cases.RunAsync(
            "P7-08-09-STANDALONE-TRANSACTION-REQUIRED-ZERO-WRITE",
            async () =>
            {
                var prepared = await PrepareP708ApplyScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "P708-STANDALONE",
                    "p708-standalone-placeholder",
                    activationThrough,
                    ct);
                var standaloneRoot = Path.Combine(
                    iterationRoot,
                    "p708-standalone-topology");
                P708MongoStandaloneLease? standalone = null;
                BackendServerLease? standaloneBackend = null;
                ApiHarnessClient? standaloneApi = null;
                string before = string.Empty;
                string after = string.Empty;
                string errorCode = string.Empty;
                string topology = string.Empty;
                int statusCode = 0;
                try
                {
                    standalone =
                        await P708MongoStandaloneLease.StartAsync(
                            paths,
                            standaloneRoot,
                            runKey,
                            ct);
                    var standaloneDatabase =
                        standalone.Client.GetDatabase(
                            standalone.DatabaseName);
                    var hello = await standalone.Client
                        .GetDatabase("admin")
                        .RunCommandAsync<BsonDocument>(
                            new BsonDocument("hello", 1),
                            cancellationToken: ct);
                    Require(
                        !hello.Contains("setName") &&
                        !hello.GetValue(
                                "isdbgrid",
                                BsonBoolean.False)
                            .ToBoolean(),
                        "P7-08 transaction-unavailable proof did not start a standalone Mongo topology.");
                    topology = "STANDALONE";

                    var backendRoot = Path.Combine(
                        standaloneRoot,
                        "backend");
                    Directory.CreateDirectory(backendRoot);
                    standaloneBackend =
                        await BackendServerLease.StartAsync(
                            paths,
                            backendRoot,
                            $"{runKey}_p708_standalone",
                            mongo,
                            ct,
                            new BackendServerOptions
                            {
                                MongoConnectionStringOverride =
                                    standalone.ConnectionString,
                                MongoDatabaseNameOverride =
                                    standalone.DatabaseName,
                                EnableDynamicFlowP7MappingCandidate =
                                    true,
                                DynamicFlowP7MappingActivationThrough =
                                    activationThrough
                            });
                    standaloneApi = new ApiHarnessClient(
                        standaloneBackend.BaseUri);
                    await CopyP708DatabaseAsync(
                        database,
                        standaloneDatabase,
                        ct);
                    var actorToken =
                        await PrepareP601ActorLoginAsync(
                            standaloneApi,
                            standaloneBackend,
                            standaloneDatabase,
                            prepared.Scenario.ActorUserId,
                            ct);
                    var standaloneScenario =
                        prepared.Scenario with
                        {
                            ActorToken = actorToken
                        };
                    var preview = await PreviewP7MappingAsync(
                        standaloneApi,
                        standaloneScenario,
                        new JsonObject(),
                        ct);
                    AssertP7MappingPreviewIdentity(
                        preview,
                        fixture,
                        standaloneScenario,
                        activationThrough);
                    var request = BuildP709ApplyRequest(
                        preview,
                        "p708-standalone-transaction-required");
                    var countsBefore =
                        await CountP709MappingLedgerAsync(
                            standaloneDatabase,
                            standaloneScenario.TargetReportId,
                            ct);
                    RequireP708EmptyMappingLedger(
                        countsBefore,
                        "P7-08 standalone baseline");
                    before =
                        await CaptureP709ScopedLedgerHashAsync(
                            standaloneDatabase,
                            standaloneScenario.TargetReportId,
                            ct);

                    var response = await standaloneApi.PostAsync(
                        P709ApplyPath(
                            standaloneScenario.TargetReportId),
                        request,
                        actorToken,
                        ct: ct);
                    statusCode = (int)response.StatusCode;
                    ApiHarnessClient.ExpectStatus(
                        response,
                        HttpStatusCode.ServiceUnavailable,
                        "P7-08 standalone mapping apply");
                    errorCode = P708ErrorCode(response);
                    Require(
                        errorCode ==
                        "DYNAMIC_FLOW_MAPPING_TRANSACTION_REQUIRED",
                        $"P7-08 standalone apply returned unstable code '{errorCode}'.");

                    after =
                        await CaptureP709ScopedLedgerHashAsync(
                            standaloneDatabase,
                            standaloneScenario.TargetReportId,
                            ct);
                    var countsAfter =
                        await CountP709MappingLedgerAsync(
                            standaloneDatabase,
                            standaloneScenario.TargetReportId,
                            ct);
                    Require(
                        before == after &&
                        countsBefore == countsAfter,
                        "P7-08 unsupported transaction topology left a partial mapping write.");
                }
                finally
                {
                    try
                    {
                        standaloneApi?.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            if (standaloneBackend is not null)
                            {
                                try
                                {
                                    await standaloneBackend.StopAsync();
                                }
                                finally
                                {
                                    await standaloneBackend.DisposeAsync();
                                }
                            }
                        }
                        finally
                        {
                            if (standalone is not null)
                                await standalone.DisposeAsync();
                        }
                    }
                }

                Require(
                    standaloneBackend is
                    {
                        StopVerified: true,
                        PortReleaseVerified: true
                    } &&
                    standalone is
                    {
                        DatabaseDropVerified: true,
                        ProcessStopVerified: true,
                        PortReleaseVerified: true,
                        DataDirectoryRemovalVerified: true
                    },
                    "P7-08 standalone topology did not pass strict process/database/data-directory cleanup.");
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-08-09-STANDALONE-TRANSACTION-REQUIRED-ZERO-WRITE",
                    targetReportId =
                        prepared.Scenario.TargetReportId,
                    topology,
                    statusCode,
                    errorCode,
                    before,
                    after,
                    zeroWrite = true,
                    cleanup = new
                    {
                        standaloneBackend.StopVerified,
                        backendPortReleaseVerified =
                            standaloneBackend
                                .PortReleaseVerified,
                        standalone.DatabaseDropVerified,
                        standalone.ProcessStopVerified,
                        mongoPortReleaseVerified =
                            standalone.PortReleaseVerified,
                        standalone.DataDirectoryRemovalVerified
                    }
                });
                return new CaseObservation(
                    "A real standalone mongod rejected the mandatory mapping transaction with the stable 503 contract, a byte-stable target ledger, and verified isolated cleanup.",
                    P7MappingFingerprint(
                        "P7-08-09",
                        topology,
                        statusCode.ToString(),
                        errorCode,
                        before,
                        after));
            });

        await cases.RunAsync(
            "P7-08-10-POSTCOMMIT-PARTIAL-RETRY-RECONCILE",
            async () =>
            {
                var prepared = await PrepareP708ApplyScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "P708-POSTCOMMIT-FAULT",
                    "p708-postcommit-placeholder",
                    activationThrough,
                    ct);
                var request = BuildP709ApplyRequest(
                    prepared.Preview,
                    P708PostcommitFaultCommandId);
                var beforeCounts =
                    await CountP709MappingLedgerAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                RequireP708EmptyMappingLedger(
                    beforeCounts,
                    "P7-08 postcommit fault baseline");

                var apply = await api.PostAsync(
                    P709ApplyPath(
                        prepared.Scenario.TargetReportId),
                    request,
                    prepared.Scenario.ActorToken,
                    ct: ct);
                var applyStatus = (int)apply.StatusCode;
                ApiHarnessClient.ExpectStatus(
                    apply,
                    HttpStatusCode.OK,
                    "P7-08 postcommit partial apply");

                var partialReceipt = await database
                    .GetCollection<
                        DynamicFlowMappingApplyReceipt>(
                        "dynamic_flow_mapping_apply_receipts")
                    .Find(item =>
                        item.TargetReportId ==
                        prepared.Scenario.TargetReportId &&
                        item.CommandId ==
                        P708PostcommitFaultCommandId)
                    .SingleAsync(ct);
                var partialOutbox = await database
                    .GetCollection<
                        DynamicFlowMappingOutboxItem>(
                        "dynamic_flow_mapping_outbox")
                    .Find(item =>
                        item.Id ==
                        partialReceipt.OutboxIntentId)
                    .SingleAsync(ct);
                var partialReceiptState =
                    partialReceipt.State;
                var partialOutboxState =
                    partialOutbox.State;
                var partialAttemptCount =
                    partialOutbox.AttemptCount;
                var partialResponseReceiptMatches =
                    P708ResponseReceiptId(apply) ==
                    partialReceipt.Id;
                var partialResponseState =
                    P708ResponseApplyState(apply);
                Require(
                    partialResponseReceiptMatches &&
                    partialResponseState ==
                    DynamicFlowMappingApplyStates.Partial &&
                    partialReceipt.State ==
                    DynamicFlowMappingApplyStates.Partial &&
                    partialOutbox.State ==
                    DynamicFlowMappingOutboxStates.Partial &&
                    partialOutbox.AttemptCount == 1 &&
                    partialReceipt.PartialAtUtc.HasValue &&
                    partialOutbox.PartialAtUtc.HasValue &&
                    partialReceipt.RetryingAtUtc.HasValue &&
                    partialOutbox.RetryingAtUtc.HasValue &&
                    IsP7LowerSha256(
                        partialOutbox
                            .LastErrorSnapshotHash ??
                        string.Empty),
                    "P7-08 injected postcommit fault did not return and persist the exact observable PARTIAL receipt/outbox state.");
                var partialCounts =
                    await CountP709MappingLedgerAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                RequireP708SingleMappingWriteSet(
                    partialCounts,
                    "P7-08 postcommit partial state");

                var partialPlanFingerprint =
                    P708ProjectorCheckpointFingerprint(
                        partialOutbox,
                        includeProgress: false);
                Require(
                    partialOutbox.RepairEpoch == 1 &&
                    P708HasExactProjectorPlan(partialOutbox) &&
                    partialOutbox.ProjectorCheckpoints.All(
                        checkpoint =>
                            checkpoint.State ==
                            DynamicFlowMappingProjectorCheckpointStates
                                .Pending &&
                            checkpoint.AttemptCount == 0 &&
                            checkpoint.ActiveRepairEpoch is null &&
                            checkpoint.CompletedRepairEpoch is null &&
                            checkpoint.CompletionHash is null),
                    "P7-08 BEFORE_PROJECTORS fault did not leave the deterministic projector plan wholly pending.");
                Require(
                    P708ResponseCommandId(apply) ==
                    P708PostcommitFaultCommandId &&
                    P708ResponseSemanticHash(apply) ==
                    ApiHarnessClient.RequiredString(
                        request,
                        "resultSemanticHash"),
                    "P7-08 PARTIAL response did not expose its exact command and semantic receipt binding.");

                await DelayUntilP708OutboxDueAsync(
                    partialOutbox,
                    "P7-08 BEFORE_PROJECTORS retry",
                    ct);
                var projectorWorker = await api.PostAsync(
                    "api/admin/operations/job-runs/dynamic-flow-mapping-outbox/process?maxItems=20",
                    body: null,
                    adminToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    projectorWorker,
                    HttpStatusCode.OK,
                    "P7-08 mapping outbox projector worker");
                var projectorWorkerProcessed =
                    ApiHarnessClient.RequiredInt(
                        projectorWorker.Json,
                        "processed");
                Require(
                    projectorWorkerProcessed == 1,
                    $"P7-08 projector worker processed an unexpected number of intents: {projectorWorkerProcessed}.");

                var finalizePartialReceipt = await database
                    .GetCollection<
                        DynamicFlowMappingApplyReceipt>(
                        "dynamic_flow_mapping_apply_receipts")
                    .Find(item =>
                        item.Id == partialReceipt.Id)
                    .SingleAsync(ct);
                var finalizePartialOutbox = await database
                    .GetCollection<
                        DynamicFlowMappingOutboxItem>(
                        "dynamic_flow_mapping_outbox")
                    .Find(item =>
                        item.Id == partialOutbox.Id)
                    .SingleAsync(ct);
                var completedPlanFingerprint =
                    P708ProjectorCheckpointFingerprint(
                        finalizePartialOutbox,
                        includeProgress: false);
                var completedProgressFingerprint =
                    P708ProjectorCheckpointFingerprint(
                        finalizePartialOutbox,
                        includeProgress: true);
                Require(
                    finalizePartialReceipt.State ==
                    DynamicFlowMappingApplyStates.Partial &&
                    finalizePartialOutbox.State ==
                    DynamicFlowMappingOutboxStates.Partial &&
                    finalizePartialOutbox.AttemptCount ==
                    partialOutbox.AttemptCount + 1 &&
                    finalizePartialOutbox.RepairEpoch ==
                    partialOutbox.RepairEpoch + 1 &&
                    finalizePartialReceipt.RetryingAtUtc
                        .HasValue &&
                    finalizePartialOutbox.RetryingAtUtc
                        .HasValue &&
                    partialPlanFingerprint ==
                    completedPlanFingerprint &&
                    P708HasExactProjectorPlan(
                        finalizePartialOutbox) &&
                    finalizePartialOutbox.ProjectorCheckpoints
                        .All(
                            checkpoint =>
                                checkpoint.State ==
                                DynamicFlowMappingProjectorCheckpointStates
                                    .Completed &&
                                checkpoint.AttemptCount == 1 &&
                                checkpoint.ActiveRepairEpoch is null &&
                                checkpoint.CompletedRepairEpoch ==
                                finalizePartialOutbox.RepairEpoch &&
                                IsP7LowerSha256(
                                    checkpoint.CompletionHash ??
                                    string.Empty)),
                    "P7-08 BEFORE_FINALIZE fault did not persist one completed, hash-bound attempt for every projector while retaining PARTIAL.");
                var projectedCounts =
                    await CountP709MappingLedgerAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                Require(
                    projectedCounts == partialCounts,
                    "P7-08 projector attempt duplicated a durable mapping row.");
                var projectedSideEffectHash =
                    await CaptureP708ProjectorSideEffectHashAsync(
                        database,
                        prepared.Scenario,
                        ct);

                await DelayUntilP708OutboxDueAsync(
                    finalizePartialOutbox,
                    "P7-08 BEFORE_FINALIZE retry",
                    ct);
                var finalizeWorker = await api.PostAsync(
                    "api/admin/operations/job-runs/dynamic-flow-mapping-outbox/process?maxItems=20",
                    body: null,
                    adminToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    finalizeWorker,
                    HttpStatusCode.OK,
                    "P7-08 mapping outbox finalize-only worker");
                var finalizeWorkerProcessed =
                    ApiHarnessClient.RequiredInt(
                        finalizeWorker.Json,
                        "processed");
                Require(
                    finalizeWorkerProcessed == 1,
                    $"P7-08 finalize-only worker processed an unexpected number of intents: {finalizeWorkerProcessed}.");

                var terminal =
                    await LoadAndAssertP709BindingAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        DynamicFlowMappingProvenanceStates.Current,
                        DynamicFlowMappingEventTypes.ApplyCommitted,
                        DynamicFlowMappingLifecycleContract
                            .ApplyInitialOperation,
                        ct);
                var sameIdentityAcrossPartialAndTerminal =
                    terminal.Receipt.Id ==
                    partialReceipt.Id &&
                    terminal.Outbox.Id ==
                    partialOutbox.Id;
                Require(
                    sameIdentityAcrossPartialAndTerminal &&
                    terminal.Receipt.State ==
                    DynamicFlowMappingApplyStates.Reconciled &&
                    terminal.Outbox.State ==
                    DynamicFlowMappingOutboxStates.Reconciled &&
                    terminal.Outbox.AttemptCount ==
                    finalizePartialOutbox.AttemptCount + 1 &&
                    terminal.Outbox.RepairEpoch ==
                    finalizePartialOutbox.RepairEpoch + 1 &&
                    P708ProjectorCheckpointFingerprint(
                        terminal.Outbox,
                        includeProgress: false) ==
                    completedPlanFingerprint &&
                    P708ProjectorCheckpointFingerprint(
                        terminal.Outbox,
                        includeProgress: true) ==
                    completedProgressFingerprint,
                    "P7-08 worker did not converge the exact partial intent once.");
                var terminalCounts =
                    await CountP709MappingLedgerAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                Require(
                    terminalCounts == partialCounts,
                    "P7-08 postcommit recovery duplicated a durable mapping row.");
                var terminalSideEffectHash =
                    await CaptureP708ProjectorSideEffectHashAsync(
                        database,
                        prepared.Scenario,
                        ct);
                Require(
                    terminalSideEffectHash ==
                    projectedSideEffectHash,
                    "P7-08 finalize-only retry reran a completed projector or changed a downstream side effect.");

                var workerReplayBefore =
                    await CaptureP708ProjectorSurfaceHashAsync(
                        database,
                        prepared.Scenario,
                        ct);
                var replayWorker = await api.PostAsync(
                    "api/admin/operations/job-runs/dynamic-flow-mapping-outbox/process?maxItems=20",
                    body: null,
                    adminToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    replayWorker,
                    HttpStatusCode.OK,
                    "P7-08 mapping outbox exact worker replay");
                var replayWorkerProcessed =
                    ApiHarnessClient.RequiredInt(
                        replayWorker.Json,
                        "processed");
                var workerReplayAfter =
                    await CaptureP708ProjectorSurfaceHashAsync(
                        database,
                        prepared.Scenario,
                        ct);
                Require(
                    replayWorkerProcessed == 0 &&
                    workerReplayBefore ==
                    workerReplayAfter,
                    "P7-08 terminal worker replay changed the reconciled write set.");
                var faultLog = LogTail.Read(
                        backend.StdoutPath,
                        400);
                var markerObserved = faultLog.Contains(
                        P708PostcommitFaultMarker,
                        StringComparison.Ordinal);
                var finalizeMarkerObserved =
                    faultLog.Contains(
                        P708PostcommitFinalizeFaultMarker,
                        StringComparison.Ordinal);
                Require(
                    markerObserved &&
                    finalizeMarkerObserved,
                    "P7-08 testing-only postcommit reconcile fault markers were not both observed in backend stdout.");
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-08-10-POSTCOMMIT-PARTIAL-RETRY-RECONCILE",
                    targetReportId = terminal.Report.Id,
                    faultCommandId =
                        P708PostcommitFaultCommandId,
                    faultPoints = new[]
                    {
                        P708PostcommitFaultPoint,
                        P708PostcommitFinalizeFaultPoint
                    },
                    markerObserved,
                    finalizeMarkerObserved,
                    applyStatus,
                    receiptId =
                        partialReceipt.Id,
                    outboxId =
                        partialOutbox.Id,
                    sameIdentityAcrossPartialAndTerminal,
                    partialResponseReceiptMatches,
                    partialResponseState,
                    partialReceiptState,
                    partialOutboxState,
                    partialAttemptCount,
                    initialRetryingAtUtc =
                        partialOutbox.RetryingAtUtc,
                    partialRepairEpoch =
                        partialOutbox.RepairEpoch,
                    partialPlanFingerprint,
                    projectorWorkerProcessed,
                    finalizePartialReceiptState =
                        finalizePartialReceipt.State,
                    finalizePartialOutboxState =
                        finalizePartialOutbox.State,
                    finalizePartialAttemptCount =
                        finalizePartialOutbox.AttemptCount,
                    finalizePartialRepairEpoch =
                        finalizePartialOutbox.RepairEpoch,
                    finalizeRetryingAtUtc =
                        finalizePartialOutbox.RetryingAtUtc,
                    completedPlanFingerprint,
                    completedProgressFingerprint,
                    completedProjectors =
                        finalizePartialOutbox
                            .ProjectorCheckpoints
                            .OrderBy(
                                checkpoint =>
                                    checkpoint.Projector,
                                StringComparer.Ordinal)
                            .Select(
                                checkpoint => new
                                {
                                    checkpoint.Projector,
                                    checkpoint.AttemptCount,
                                    checkpoint
                                        .CompletedRepairEpoch,
                                    checkpoint.CompletionHash
                                })
                            .ToArray(),
                    finalizeWorkerProcessed,
                    terminalReceiptState =
                        terminal.Receipt.State,
                    terminalOutboxState =
                        terminal.Outbox.State,
                    terminalAttemptCount =
                        terminal.Outbox.AttemptCount,
                    partialCounts,
                    projectedCounts,
                    terminalCounts,
                    projectedSideEffectHash,
                    terminalSideEffectHash,
                    replayWorkerProcessed,
                    workerReplayBefore,
                    workerReplayAfter,
                    exactOnce = true,
                    cleanup = new
                    {
                        ownedByMainGate = true
                    }
                });
                return new CaseObservation(
                    "Testing-only failures before projectors and before finalize exposed one durable PARTIAL intent; checkpointed leased retries ran each projector once, skipped all completed side effects during finalize, and terminal replay was zero-write.",
                    P7MappingFingerprint(
                        "P7-08-10",
                        terminal.Receipt.Id,
                        terminal.Outbox.Id,
                        partialReceiptState,
                        partialOutboxState,
                        terminal.Receipt.State,
                        terminal.Outbox.State,
                        completedProgressFingerprint,
                        projectedSideEffectHash,
                        terminalSideEffectHash,
                        workerReplayBefore,
                        workerReplayAfter));
            });
    }

    private static async Task<P708PreparedScenario>
        PrepareP708ApplyScenarioAsync(
            ApiHarnessClient api,
            BackendServerLease backend,
            IMongoDatabase database,
            string adminToken,
            ProbeFixture baseFixture,
            P7MappingFixture fixture,
            string suffix,
            string commandId,
            int activationThrough,
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
            $"p708-{suffix.ToLowerInvariant()}-launch",
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
            scenario,
            activationThrough);
        return new P708PreparedScenario(
            scenario,
            preview,
            BuildP709ApplyRequest(preview, commandId));
    }

    private static P708AppliedScenario RequireP708Applied(
        P708AppliedScenario? applied,
        string context)
        => applied ??
           throw new HarnessCaseNotRunnableException(
               $"{context} requires P7-08-01.");

    private static void RequireP708EmptyMappingLedger(
        P709MappingLedgerCounts counts,
        string context)
        => Require(
            counts.Receipts == 0 &&
            counts.Provenance == 0 &&
            counts.Events == 0 &&
            counts.Outbox == 0 &&
            counts.MappingAudits == 0,
            $"{context} was not an empty mapping ledger: {counts}.");

    private static void RequireP708SingleMappingWriteSet(
        P709MappingLedgerCounts counts,
        string context)
        => Require(
            counts.Receipts == 1 &&
            counts.Provenance == 1 &&
            counts.Events == 1 &&
            counts.Outbox == 1 &&
            counts.MappingAudits == 1,
            $"{context} did not contain exactly one canonical mapping write set: {counts}.");

    private static void AssertP708ApplyResponse(
        ApiHarnessResponse response,
        P709BindingBundle binding,
        string context)
    {
        Require(
            ApiHarnessClient.RequiredString(response.Json, "id") ==
            binding.Report.Id &&
            ApiHarnessClient.RequiredInt(
                response.Json,
                "payloadRevision") ==
            binding.Receipt.ResultPayloadRevision &&
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "payloadHash") ==
            binding.Receipt.ResultPayloadHash &&
            ApiHarnessClient.RequiredInt(
                response.Json,
                "lifecycleRevision") ==
            binding.Receipt.ResultLifecycleRevision,
            $"{context} did not return the committed report result.");

        var receiptId = P708ResponseReceiptId(response);
        var applyState = P708ResponseApplyState(response);
        Require(
            receiptId == binding.Receipt.Id &&
            applyState ==
            DynamicFlowMappingApplyStates.Reconciled &&
            P708ResponseCommandId(response) ==
            binding.Receipt.CommandId &&
            P708ResponseSemanticHash(response) ==
            binding.Receipt.ResultSemanticHash,
            $"{context} did not expose the exact canonical receipt, command, semantic hash, and RECONCILED state.");
    }

    private static void AssertP708CanonicalReplayResponse(
        ApiHarnessResponse initial,
        ApiHarnessResponse replay,
        P709BindingBundle binding)
    {
        AssertP708ApplyResponse(
            replay,
            binding,
            "P7-08 canonical replay");
        Require(
            ApiHarnessClient.RequiredString(initial.Json, "id") ==
            ApiHarnessClient.RequiredString(replay.Json, "id") &&
            ApiHarnessClient.RequiredInt(
                initial.Json,
                "payloadRevision") ==
            ApiHarnessClient.RequiredInt(
                replay.Json,
                "payloadRevision") &&
            ApiHarnessClient.FindStringRecursive(
                initial.Json,
                "payloadHash") ==
            ApiHarnessClient.FindStringRecursive(
                replay.Json,
                "payloadHash") &&
            ApiHarnessClient.RequiredInt(
                initial.Json,
                "lifecycleRevision") ==
            ApiHarnessClient.RequiredInt(
                replay.Json,
                "lifecycleRevision"),
            "P7-08 exact retry response did not resolve the original canonical report result.");

        var initialReceipt = P708ResponseReceiptId(initial);
        var replayReceipt = P708ResponseReceiptId(replay);
        Require(
            initialReceipt == binding.Receipt.Id &&
            replayReceipt == binding.Receipt.Id,
            "P7-08 exact retry exposed different receipt results.");
    }

    private static bool P708ResponseBindingExposed(
        ApiHarnessResponse response)
        => P708ResponseReceiptId(response) is not null ||
           P708ResponseApplyState(response) is not null;

    private static string? P708ResponseReceiptId(
        ApiHarnessResponse response)
        => ApiHarnessClient.FindStringRecursive(
               response.Json,
               "dynamicFlowMappingReceiptId") ??
           ApiHarnessClient.FindStringRecursive(
               response.Json,
               "mappingReceiptId") ??
           ApiHarnessClient.FindStringRecursive(
               response.Json,
               "receiptId");

    private static string? P708ResponseApplyState(
        ApiHarnessResponse response)
        => ApiHarnessClient.FindStringRecursive(
               response.Json,
               "dynamicFlowMappingApplyState") ??
           ApiHarnessClient.FindStringRecursive(
               response.Json,
               "mappingApplyState") ??
           ApiHarnessClient.FindStringRecursive(
               response.Json,
               "applyState");

    private static string? P708ResponseCommandId(
        ApiHarnessResponse response)
        => ApiHarnessClient.FindStringRecursive(
            response.Json,
            "dynamicFlowMappingCommandId");

    private static string? P708ResponseSemanticHash(
        ApiHarnessResponse response)
        => ApiHarnessClient.FindStringRecursive(
            response.Json,
            "dynamicFlowMappingResultSemanticHash");

    private static async Task<P708ConflictOutcome>
        AssertP708ConflictAndZeroWriteAsync(
            ApiHarnessClient api,
            IMongoDatabase database,
            string targetReportId,
            JsonObject request,
            string actorToken,
            string context,
            string expectedCode,
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
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Conflict,
            context);
        var errorCode = P708ErrorCode(response);
        Require(
            errorCode == expectedCode,
            $"{context} expected '{expectedCode}', got '{errorCode}'.");
        var after = await CaptureP709ScopedLedgerHashAsync(
            database,
            targetReportId,
            ct);
        Require(
            before == after,
            $"{context} changed the scoped Mongo ledger.");
        return new P708ConflictOutcome(
            errorCode,
            before,
            after);
    }

    private static async Task<P708ConflictOutcome>
        AssertP708ManualTamperBlockedAsync(
            ApiHarnessClient api,
            IMongoDatabase database,
            P709MappedScenario mapped,
            string commandId,
            string requestedProvenanceId,
            CancellationToken ct)
    {
        var before = await CaptureP709ScopedLedgerHashAsync(
            database,
            mapped.Scenario.TargetReportId,
            ct);
        var response = await api.PutAsync(
            $"api/work-assignment-reports/{mapped.Scenario.TargetReportId}/draft",
            new JsonObject
            {
                ["expectedPayloadRevision"] =
                    mapped.Binding.Report.PayloadRevision,
                ["commandId"] = commandId,
                ["values1D"] = new JsonArray(),
                ["summarySourceJson"] = null,
                ["dynamicFlowMappingReceiptId"] =
                    mapped.Binding.Receipt.Id,
                ["dynamicFlowMappingProvenanceId"] =
                    requestedProvenanceId,
                ["dynamicFlowMappingProvenanceHash"] =
                    new string('0', 64)
            },
            mapped.Scenario.ActorToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Conflict,
            commandId);
        var errorCode = P708ErrorCode(response);
        Require(
            errorCode ==
            "DYNAMIC_FLOW_MAPPING_PROVENANCE_TAMPERED",
            $"{commandId} returned unstable tamper code '{errorCode}'.");
        var after = await CaptureP709ScopedLedgerHashAsync(
            database,
            mapped.Scenario.TargetReportId,
            ct);
        Require(
            before == after,
            $"{commandId} wrote after detecting provenance tamper.");
        return new P708ConflictOutcome(
            errorCode,
            before,
            after);
    }

    private static string P708ErrorCode(
        ApiHarnessResponse response)
        => ApiHarnessClient.FindStringRecursive(
               response.Json,
               "errorCode") ??
           ApiHarnessClient.FindStringRecursive(
               response.Json,
               "code") ??
           string.Empty;

    private static string P708DifferentHash(string value)
    {
        Require(
            IsP7LowerSha256(value),
            "P7-08 cannot mutate a non-canonical hash.");
        var first = value[0] == 'a' ? 'b' : 'a';
        return first + value[1..];
    }

    private static string P708BsonText(
        BsonDocument document,
        string name)
    {
        Require(
            document.TryGetValue(name, out var value) &&
            !value.IsBsonNull,
            $"P7-08 BSON evidence field '{name}' is missing.");
        return value.IsString
            ? value.AsString
            : value.ToString() ?? string.Empty;
    }

    private static void RequireP708P709Barrier(
        ApiHarnessResponse response,
        string context)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Conflict,
            context);
        var errorCode = P708ErrorCode(response);
        var blockedUntil =
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "blockedUntilPhase");
        var reason = P708BarrierReason(response);
        Require(
            errorCode ==
            "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE" &&
            blockedUntil == "P7-09" &&
            reason.Contains(
                "BLOCKED_UNTIL_P7_09",
                StringComparison.Ordinal),
            $"{context} did not return the exact P7-09 barrier; code={errorCode}; phase={blockedUntil}; reason={reason}.");
    }

    private static string P708BarrierReason(
        ApiHarnessResponse response)
        => ApiHarnessClient.FindStringRecursive(
               response.Json,
               "reason") ??
           string.Empty;

    private static async Task DelayUntilP708OutboxDueAsync(
        DynamicFlowMappingOutboxItem outbox,
        string context,
        CancellationToken ct)
    {
        var delay =
            outbox.NextAttemptAtUtc -
            DateTime.UtcNow +
            TimeSpan.FromMilliseconds(250);
        if (delay <= TimeSpan.Zero)
            return;

        Require(
            delay < TimeSpan.FromSeconds(10),
            $"{context} exceeded the bounded harness window: {delay}.");
        await Task.Delay(delay, ct);
    }

    private static bool P708HasExactProjectorPlan(
        DynamicFlowMappingOutboxItem outbox)
        => outbox.ProjectorCheckpoints
            .Select(checkpoint => checkpoint.Projector)
            .OrderBy(projector => projector, StringComparer.Ordinal)
            .SequenceEqual(
                new[]
                {
                    DynamicFlowMappingProjectors.AssignmentStatus,
                    DynamicFlowMappingProjectors.DocRolePeriod,
                    DynamicFlowMappingProjectors.QueuePeriod
                }.OrderBy(
                    projector => projector,
                    StringComparer.Ordinal),
                StringComparer.Ordinal);

    private static string P708ProjectorCheckpointFingerprint(
        DynamicFlowMappingOutboxItem outbox,
        bool includeProgress)
        => P7MappingFingerprint(
            outbox.ProjectorCheckpoints
                .OrderBy(
                    checkpoint => checkpoint.Projector,
                    StringComparer.Ordinal)
                .Select(
                    checkpoint => string.Join(
                        "\n",
                        checkpoint.Projector,
                        checkpoint.BusinessKey,
                        checkpoint.IdempotencyKey,
                        includeProgress
                            ? checkpoint.State
                            : string.Empty,
                        includeProgress
                            ? checkpoint.AttemptCount.ToString()
                            : string.Empty,
                        includeProgress
                            ? checkpoint.ActiveRepairEpoch?.ToString() ??
                              string.Empty
                            : string.Empty,
                        includeProgress
                            ? checkpoint.CompletedRepairEpoch?.ToString() ??
                              string.Empty
                            : string.Empty,
                        includeProgress
                            ? checkpoint.CompletionHash ??
                              string.Empty
                            : string.Empty))
                .ToArray());

    private static async Task<string>
        CaptureP708ProjectorSurfaceHashAsync(
            IMongoDatabase database,
            P7MappingScenario scenario,
            CancellationToken ct)
        => await CaptureP708ProjectorSurfaceHashCoreAsync(
            database,
            scenario,
            includeMappingLedger: true,
            ct);

    private static async Task<string>
        CaptureP708ProjectorSideEffectHashAsync(
            IMongoDatabase database,
            P7MappingScenario scenario,
            CancellationToken ct)
        => await CaptureP708ProjectorSurfaceHashCoreAsync(
            database,
            scenario,
            includeMappingLedger: false,
            ct);

    private static async Task<string>
        CaptureP708ProjectorSurfaceHashCoreAsync(
            IMongoDatabase database,
            P7MappingScenario scenario,
            bool includeMappingLedger,
            CancellationToken ct)
    {
        var workId = ObjectId.Parse(scenario.WorkId);
        var assignmentId =
            ObjectId.Parse(scenario.TargetAssignmentId);
        var periodId =
            ObjectId.Parse(scenario.TargetPeriodId);
        var reportId =
            ObjectId.Parse(scenario.TargetReportId);
        var statusLogFilter =
            new BsonDocument(
                "$or",
                new BsonArray
                {
                    new BsonDocument(
                        "workId",
                        new BsonString(
                            scenario.WorkId)),
                    new BsonDocument(
                        "workAssignmentId",
                        new BsonString(
                            scenario
                                .TargetAssignmentId)),
                    new BsonDocument(
                        "workReportPeriodId",
                        new BsonString(
                            scenario.TargetPeriodId)),
                    new BsonDocument(
                        "workAssignmentReportId",
                        new BsonString(
                            scenario.TargetReportId))
                });
        var rows = new List<string>();
        if (includeMappingLedger)
        {
            rows.Add("p709-scoped-mapping-ledger");
            rows.Add(
                await CaptureP709ScopedLedgerHashAsync(
                    database,
                    scenario.TargetReportId,
                    ct));
        }
        rows.Add("p708-projector-side-effects");
        foreach (var scope in new[]
                 {
                     new P709MongoScope(
                         "work_report_periods",
                         new BsonDocument("_id", periodId)),
                     new P709MongoScope(
                         "work_assignments",
                         new BsonDocument("workId", workId)),
                     new P709MongoScope(
                         "works",
                         new BsonDocument("_id", workId)),
                     new P709MongoScope(
                         "work_assignment_queue",
                         new BsonDocument("workId", workId)),
                     new P709MongoScope(
                         "work_status_operation_logs",
                         statusLogFilter),
                     new P709MongoScope(
                         "work_list_doc_roles",
                         new BsonDocument("workId", workId)),
                     new P709MongoScope(
                         "assignment_list_doc_roles",
                         new BsonDocument("workId", workId)),
                     new P709MongoScope(
                         "my_report_template_list_doc_roles",
                         new BsonDocument("workId", workId)),
                     new P709MongoScope(
                         "my_report_period_list_doc_roles",
                         new BsonDocument("workId", workId)),
                     new P709MongoScope(
                         "review_report_list_doc_roles",
                         new BsonDocument("workId", workId)),
                     new P709MongoScope(
                         "review_assignment_summary_doc_roles",
                         new BsonDocument("workId", workId))
                 })
        {
            var documents = await database
                .GetCollection<BsonDocument>(
                    scope.Collection)
                .Find(scope.Filter)
                .Sort(
                    Builders<BsonDocument>.Sort
                        .Ascending("_id"))
                .ToListAsync(ct);
            rows.Add(scope.Collection);
            rows.AddRange(
                documents.Select(item =>
                    item.ToJson()));
        }

        rows.Add("target-identities");
        rows.Add(assignmentId.ToString());
        rows.Add(periodId.ToString());
        rows.Add(reportId.ToString());
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        string.Join("\n", rows))))
            .ToLowerInvariant();
    }

    private static async Task<string>
        CaptureP708LifecycleBarrierHashAsync(
            IMongoDatabase database,
            P709MappedScenario mapped,
            CancellationToken ct)
    {
        var targetHash = await CaptureP709ScopedLedgerHashAsync(
            database,
            mapped.Scenario.TargetReportId,
            ct);
        var source = await LoadP709ReportAsync(
            database,
            mapped.Scenario.SourceReportId,
            ct);
        var target = await LoadP709ReportAsync(
            database,
            mapped.Scenario.TargetReportId,
            ct);
        var periodIds = new[]
        {
            source.WorkReportPeriodId,
            target.WorkReportPeriodId
        };
        var periods = await database
            .GetCollection<WorkReportPeriod>(
                "work_report_periods")
            .Find(item =>
                periodIds.Contains(item.Id))
            .SortBy(item => item.Id)
            .ToListAsync(ct);
        var logs = await database
            .GetCollection<WorkAssignmentReportLog>(
                "work_assignment_report_logs")
            .Find(item =>
                item.WorkAssignmentReportId == source.Id ||
                item.WorkAssignmentReportId == target.Id)
            .SortBy(item => item.Id)
            .ToListAsync(ct);
        var material = string.Join(
            "\n",
            new[]
            {
                targetHash,
                source.ToBsonDocument().ToJson(),
                target.ToBsonDocument().ToJson(),
                string.Join(
                    "\n",
                    periods.Select(item =>
                        item.ToBsonDocument().ToJson())),
                string.Join(
                    "\n",
                    logs.Select(item =>
                        item.ToBsonDocument().ToJson()))
            });
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    private static async Task CopyP708DatabaseAsync(
        IMongoDatabase source,
        IMongoDatabase destination,
        CancellationToken ct)
    {
        using var cursor = await source.ListCollectionNamesAsync(
            cancellationToken: ct);
        var names = (await cursor.ToListAsync(ct))
            .Where(name =>
                !name.StartsWith(
                    "system.",
                    StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        using var destinationCursor =
            await destination.ListCollectionNamesAsync(
                cancellationToken: ct);
        var destinationNames = new HashSet<string>(
            await destinationCursor.ToListAsync(ct),
            StringComparer.Ordinal);
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            if (!destinationNames.Contains(name))
            {
                await destination.CreateCollectionAsync(
                    name,
                    cancellationToken: ct);
                destinationNames.Add(name);
            }
            var documents = await source
                .GetCollection<BsonDocument>(name)
                .Find(Builders<BsonDocument>.Filter.Empty)
                .ToListAsync(ct);
            var destinationCollection =
                destination.GetCollection<BsonDocument>(name);
            await destinationCollection.DeleteManyAsync(
                Builders<BsonDocument>.Filter.Empty,
                ct);
            if (documents.Count > 0)
            {
                await destinationCollection.InsertManyAsync(
                        documents,
                        new InsertManyOptions
                        {
                            IsOrdered = true
                        },
                        ct);
            }
        }
    }

    private sealed record P708PreparedScenario(
        P7MappingScenario Scenario,
        ApiHarnessResponse Preview,
        JsonObject Request);

    private sealed record P708AppliedScenario(
        P709MappedScenario Mapped,
        ApiHarnessResponse InitialResponse);

    private sealed record P708ConflictOutcome(
        string ErrorCode,
        string BeforeHash,
        string AfterHash);

    private sealed class P708MongoStandaloneLease :
        IAsyncDisposable
    {
        private readonly string _root;
        private readonly ManagedChildProcess _process;
        private bool _disposed;

        private P708MongoStandaloneLease(
            string root,
            string databaseName,
            int port,
            string dataDirectory,
            string logPath,
            string stdoutPath,
            string stderrPath,
            ManagedChildProcess process,
            MongoClient client)
        {
            _root = root;
            DatabaseName = databaseName;
            Port = port;
            DataDirectory = dataDirectory;
            LogPath = logPath;
            StdoutPath = stdoutPath;
            StderrPath = stderrPath;
            _process = process;
            Client = client;
            ConnectionString =
                $"mongodb://127.0.0.1:{port}/?directConnection=true&retryWrites=false&serverSelectionTimeoutMS=10000";
        }

        public string DatabaseName { get; }
        public int Port { get; }
        public string DataDirectory { get; }
        public string LogPath { get; }
        public string StdoutPath { get; }
        public string StderrPath { get; }
        public string ConnectionString { get; }
        public MongoClient Client { get; }
        public bool DatabaseDropVerified { get; private set; }
        public bool ProcessStopVerified { get; private set; }
        public bool PortReleaseVerified { get; private set; }
        public bool DataDirectoryRemovalVerified { get; private set; }

        public static async Task<P708MongoStandaloneLease>
            StartAsync(
                HarnessPaths paths,
                string root,
                string runKey,
                CancellationToken ct)
        {
            root = Path.GetFullPath(root);
            var allowed = Path.GetFullPath(paths.RunRoot)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            Require(
                root.StartsWith(
                    allowed,
                    StringComparison.OrdinalIgnoreCase),
                $"P7-08 standalone root escaped the run root: {root}");
            Directory.CreateDirectory(root);
            var dataDirectory = Path.Combine(
                root,
                "mongo-data");
            Directory.CreateDirectory(dataDirectory);
            var logPath = Path.Combine(root, "mongod.log");
            var stdoutPath = Path.Combine(
                root,
                "mongo.stdout.log");
            var stderrPath = Path.Combine(
                root,
                "mongo.stderr.log");
            var port = PortAllocator.GetFreeTcpPort();
            var mongodPath = HarnessPaths.ResolveMongodPath();
            var process = ManagedChildProcess.Start(
                mongodPath,
                new[]
                {
                    "--dbpath",
                    dataDirectory,
                    "--port",
                    port.ToString(
                        System.Globalization.CultureInfo
                            .InvariantCulture),
                    "--bind_ip",
                    "127.0.0.1",
                    "--logpath",
                    logPath,
                    "--logappend"
                },
                root,
                new Dictionary<string, string?>(),
                stdoutPath,
                stderrPath);
            var settings =
                MongoClientSettings.FromConnectionString(
                    $"mongodb://127.0.0.1:{port}/?directConnection=true&retryWrites=false&serverSelectionTimeoutMS=1000");
            settings.ConnectTimeout = TimeSpan.FromSeconds(2);
            settings.SocketTimeout = TimeSpan.FromSeconds(5);
            var client = new MongoClient(settings);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(45);
                Exception? last = null;
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    if (process.HasExited)
                    {
                        throw new InvalidOperationException(
                            $"P7-08 standalone mongod exited with code {process.ExitCode}; log={LogTail.Read(logPath)}; stderr={LogTail.Read(stderrPath)}");
                    }
                    try
                    {
                        await client.GetDatabase("admin")
                            .RunCommandAsync<BsonDocument>(
                                new BsonDocument("ping", 1),
                                cancellationToken: ct);
                        var databaseName =
                            $"tdtd_p708_standalone_{P708StandaloneToken(runKey, 24)}";
                        return new P708MongoStandaloneLease(
                            root,
                            databaseName,
                            port,
                            dataDirectory,
                            logPath,
                            stdoutPath,
                            stderrPath,
                            process,
                            client);
                    }
                    catch (Exception error) when (
                        error is MongoException or
                            TimeoutException)
                    {
                        last = error;
                    }
                    await Task.Delay(250, ct);
                }
                throw new TimeoutException(
                    $"P7-08 standalone Mongo ping timed out; last={last?.Message}; log={LogTail.Read(logPath)}");
            }
            catch
            {
                await process.DisposeAsync();
                RemoveP708StandaloneDataDirectory(
                    root,
                    dataDirectory);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                await Client.DropDatabaseAsync(DatabaseName);
                var remaining =
                    await Client.ListDatabaseNames().ToListAsync();
                Require(
                    !remaining.Contains(
                        DatabaseName,
                        StringComparer.Ordinal),
                    $"P7-08 standalone database {DatabaseName} survived guarded drop.");
                DatabaseDropVerified = true;
            }
            finally
            {
                try
                {
                    await Client.GetDatabase("admin")
                        .RunCommandAsync<BsonDocument>(
                            new BsonDocument
                            {
                                { "shutdown", 1 },
                                { "force", true }
                            });
                }
                catch (Exception error) when (
                    error is MongoException or
                        TimeoutException)
                {
                    // A successful shutdown normally closes the connection.
                }
                await _process.StopAsync(
                    TimeSpan.FromSeconds(15));
                Require(
                    _process.HasExited,
                    "P7-08 standalone mongod survived stop.");
                ProcessStopVerified = true;
                await _process.DisposeAsync();
                await PortAllocator.WaitUntilNotAcceptingAsync(
                    Port,
                    TimeSpan.FromSeconds(10),
                    "P7-08 standalone MongoDB");
                PortReleaseVerified = true;
                RemoveP708StandaloneDataDirectory(
                    _root,
                    DataDirectory);
                DataDirectoryRemovalVerified = true;
                await EvidenceJson.WriteAsync(
                    Path.Combine(
                        _root,
                        "standalone-cleanup.json"),
                    new
                    {
                        DatabaseName,
                        Port,
                        DatabaseDropVerified,
                        ProcessStopVerified,
                        PortReleaseVerified,
                        DataDirectoryRemovalVerified
                    });
            }
        }

        private static string P708StandaloneToken(
            string value,
            int maxLength)
        {
            var token = new string(
                value.ToLowerInvariant()
                    .Select(character =>
                        char.IsLetterOrDigit(character)
                            ? character
                            : '_')
                    .ToArray());
            return token.Length <= maxLength
                ? token
                : token[..maxLength];
        }

        private static void
            RemoveP708StandaloneDataDirectory(
                string root,
                string dataDirectory)
        {
            var allowed = Path.GetFullPath(root)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(dataDirectory);
            Require(
                target.StartsWith(
                    allowed,
                    StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(target) == "mongo-data",
                $"Refusing to remove unsafe P7-08 standalone directory '{target}'.");
            IOException? last = null;
            for (var attempt = 1;
                 attempt <= 50 && Directory.Exists(target);
                 attempt++)
            {
                try
                {
                    Directory.Delete(
                        target,
                        recursive: true);
                    last = null;
                }
                catch (IOException error) when (
                    attempt < 50)
                {
                    last = error;
                    Thread.Sleep(100);
                }
            }
            Require(
                !Directory.Exists(target),
                $"P7-08 standalone data directory survived cleanup; last={last?.Message}");
        }
    }
}
