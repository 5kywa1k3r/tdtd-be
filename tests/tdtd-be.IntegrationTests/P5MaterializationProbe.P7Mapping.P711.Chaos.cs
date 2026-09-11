using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Dedicated P7-11 race/fault/crash campaign.
///
/// Every named P7-11 case owns the Kestrel exchanges and direct-Mongo facts
/// executed inside that case. This file deliberately does not translate an
/// unrelated P7-04..09 case or an arbitrary exchange into P7-11 evidence.
/// </summary>
internal static partial class P5MaterializationProbe
{
    private const string P711ReconcileCommandId =
        "p711-reconcile-exact-convergence";
    private const string P711LeaseLossCommandId =
        "p711-lease-loss";
    private const string P711CrashBeforeCommandId =
        "p711-crash-before-commit";
    private const string P711CrashAfterCommandId =
        "p711-crash-after-commit";

    private static readonly P711ApplyFaultCase[] P711ApplyFaultCases =
    [
        new(
            "P7-11-FAULT-BEFORE-RECEIPT",
            "p711-fault-before-receipt",
            DynamicFlowMappingApplyFaultPoints.BeforeReceipt,
            DurableBeforeRetry: false),
        new(
            "P7-11-FAULT-AFTER-RECEIPT",
            "p711-fault-after-receipt",
            DynamicFlowMappingApplyFaultPoints.AfterReceipt,
            DurableBeforeRetry: false),
        new(
            "P7-11-FAULT-BEFORE-TRANSACTION-COMMIT",
            "p711-fault-before-transaction-commit",
            DynamicFlowMappingApplyFaultPoints.BeforeTransactionCommit,
            DurableBeforeRetry: false),
        new(
            "P7-11-FAULT-AFTER-TRANSACTION-COMMIT",
            "p711-fault-after-transaction-commit",
            DynamicFlowMappingApplyFaultPoints.AfterTransactionCommit,
            DurableBeforeRetry: true),
        new(
            "P7-11-FAULT-BEFORE-INTENT-COMMIT",
            "p711-fault-before-intent-commit",
            DynamicFlowMappingApplyFaultPoints.BeforeIntentCommit,
            DurableBeforeRetry: false),
        new(
            "P7-11-FAULT-AFTER-INTENT-COMMIT",
            "p711-fault-after-intent-commit",
            DynamicFlowMappingApplyFaultPoints.AfterIntentCommit,
            DurableBeforeRetry: false),
        new(
            "P7-11-FAULT-BEFORE-PAYLOAD",
            "p711-fault-before-payload",
            DynamicFlowMappingApplyFaultPoints.BeforePayload,
            DurableBeforeRetry: false),
        new(
            "P7-11-FAULT-AFTER-PAYLOAD",
            "p711-fault-after-payload",
            DynamicFlowMappingApplyFaultPoints.AfterPayload,
            DurableBeforeRetry: false),
        new(
            "P7-11-FAULT-BEFORE-PROJECTION",
            "p711-fault-before-projection",
            DynamicFlowMappingApplyFaultPoints.BeforeProjection,
            DurableBeforeRetry: false),
        new(
            "P7-11-FAULT-AFTER-PROJECTION",
            "p711-fault-after-projection",
            DynamicFlowMappingApplyFaultPoints.AfterProjection,
            DurableBeforeRetry: false),
        new(
            "P7-11-FAULT-BEFORE-AUDIT",
            "p711-fault-before-audit",
            DynamicFlowMappingApplyFaultPoints.BeforeAudit,
            DurableBeforeRetry: false),
        new(
            "P7-11-FAULT-AFTER-AUDIT",
            "p711-fault-after-audit",
            DynamicFlowMappingApplyFaultPoints.AfterAudit,
            DurableBeforeRetry: false),
        new(
            "P7-11-FAULT-BEFORE-OUTBOX",
            "p711-fault-before-outbox",
            DynamicFlowMappingApplyFaultPoints.BeforeOutbox,
            DurableBeforeRetry: false),
        new(
            "P7-11-FAULT-AFTER-OUTBOX",
            "p711-fault-after-outbox",
            DynamicFlowMappingApplyFaultPoints.AfterOutbox,
            DurableBeforeRetry: false)
    ];

    private static async Task RunP711ChaosCasesAsync(
        HarnessCaseRunner cases,
        List<object> mongoEvidence,
        List<ApiExchangeEvidence> additionalApiEvidence,
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
        CancellationToken ct)
    {
        await RunP711FieldAndAuthCoreCasesAsync(
            cases,
            mongoEvidence,
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            ct);
        await RunP711TableCoreCasesAsync(
            cases,
            mongoEvidence,
            api,
            database,
            adminToken,
            fixture,
            ct);

        var initialCaseCount = cases.Results.Count;

        await RunP711SourceDriftRaceAsync(
            cases,
            mongoEvidence,
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            ct);
        await RunP711TargetCasRaceAsync(
            cases,
            mongoEvidence,
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            ct);
        await RunP711ExactReplayAsync(
            cases,
            mongoEvidence,
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            ct);
        await RunP711ChangedReplayAsync(
            cases,
            mongoEvidence,
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            ct);
        await RunP711LifecycleInvalidationAsync(
            cases,
            mongoEvidence,
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            ct);

        var epochSeed = await CloneP711DefinitionFixtureAsync(
            database,
            fixture,
            "EPOCH",
            ct);
        var epochFixture =
            await ConvertP709FixtureToEpochMappingAsync(
                database,
                epochSeed,
                ct);
        await RunP711EpochInvalidationAsync(
            cases,
            mongoEvidence,
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            epochFixture,
            ct);

        BackendServerLease? faultBackend = null;
        ApiHarnessClient? faultApi = null;
        try
        {
            var faultRoot = Path.Combine(
                iterationRoot,
                "p711-apply-fault-backend");
            Directory.CreateDirectory(faultRoot);
            faultBackend = await BackendServerLease.StartAsync(
                paths,
                faultRoot,
                $"{runKey}_p711_faults",
                mongo,
                ct,
                new BackendServerOptions
                {
                    SkipMongoIndexInitializationForTesting = true,
                    EnableDynamicFlowP7MappingCandidate = true,
                    DynamicFlowP7MappingActivationThrough = 11,
                    DynamicFlowMappingApplyFaultBindings =
                        P711ApplyFaultCases
                            .Select(item =>
                                new DynamicFlowMappingApplyFaultBinding(
                                    item.CommandId,
                                    [item.Point]))
                            .Append(
                                new DynamicFlowMappingApplyFaultBinding(
                                    P711LeaseLossCommandId,
                                    [
                                        DynamicFlowMappingApplyFaultPoints
                                            .AfterTransactionCommit
                                    ]))
                            .ToArray(),
                    DynamicFlowMappingReconcileFaultCommandId =
                        P711ReconcileCommandId,
                    DynamicFlowMappingReconcileFaultPoints =
                    [
                        DynamicFlowMappingReconcileFaultPoints
                            .BeforeProjectors,
                        DynamicFlowMappingReconcileFaultPoints
                            .BeforeFinalize
                    ]
                });
            faultApi = new ApiHarnessClient(faultBackend.BaseUri);
            var faultAdminToken =
                await LoginP711AdminAsync(
                    faultApi,
                    faultBackend,
                    database,
                    ct);

            foreach (var fault in P711ApplyFaultCases)
            {
                await RunP711ApplyFaultCaseAsync(
                    cases,
                    mongoEvidence,
                    faultApi,
                    faultBackend,
                    database,
                    faultAdminToken,
                    baseFixture,
                    fixture,
                    fault,
                    ct);
            }

            await RunP711LeaseLossAsync(
                cases,
                mongoEvidence,
                faultApi,
                faultBackend,
                database,
                faultAdminToken,
                baseFixture,
                fixture,
                additionalApiEvidence,
                paths,
                iterationRoot,
                runKey,
                mongo,
                ct);
            await RunP711CrashBeforeCommitAsync(
                cases,
                mongoEvidence,
                paths,
                iterationRoot,
                runKey,
                mongo,
                database,
                baseFixture,
                fixture,
                additionalApiEvidence,
                ct);
            await RunP711CrashAfterCommitAsync(
                cases,
                mongoEvidence,
                paths,
                iterationRoot,
                runKey,
                mongo,
                database,
                baseFixture,
                fixture,
                additionalApiEvidence,
                ct);
            await RunP711ReconcileConvergenceAsync(
                cases,
                mongoEvidence,
                faultApi,
                faultBackend,
                database,
                faultAdminToken,
                baseFixture,
                fixture,
                ct);
        }
        finally
        {
            if (faultApi is not null)
            {
                CaptureP711ApiExchanges(
                    additionalApiEvidence,
                    faultApi);
                faultApi.Dispose();
            }
            if (faultBackend is not null)
            {
                try
                {
                    await faultBackend.StopAsync();
                }
                finally
                {
                    await faultBackend.DisposeAsync();
                }
                Require(
                    faultBackend.StopVerified &&
                    faultBackend.PortReleaseVerified,
                    "P7-11 apply-fault backend cleanup failed.");
            }
        }

        await RunP711P8P9BarrierAsync(
            cases,
            mongoEvidence,
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            epochFixture,
            ct);

        Require(
            cases.Results.Count == initialCaseCount + 25,
            $"P7-11 chaos campaign emitted {cases.Results.Count - initialCaseCount} cases instead of 25.");
    }

    private static async Task<string> LoginP711AdminAsync(
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        CancellationToken ct)
    {
        var admin = await database.GetCollection<AppUser>("users")
            .Find(item =>
                item.Username == "admin" &&
                !item.IsDeleted)
            .SingleAsync(ct);
        return await PrepareP601ActorLoginAsync(
            api,
            backend,
            database,
            admin.Id,
            ct);
    }

    private static async Task RunP711SourceDriftRaceAsync(
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
            "P7-11-RACE-SOURCE-DRIFT-VS-APPLY",
            async () =>
            {
                var prepared = await PrepareP708ApplyScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "P711-SOURCE-RACE",
                    "p711-source-drift-apply",
                    activationThrough: 9,
                    ct);
                var before =
                    await CaptureP709ScopedLedgerHashAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                var beforeCounts =
                    await CountP709MappingLedgerAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                RequireP708EmptyMappingLedger(
                    beforeCounts,
                    "P7-11 source/apply race baseline");

                var applyTask = api.PostAsync(
                    P709ApplyPath(
                        prepared.Scenario.TargetReportId),
                    CloneP709Request(prepared.Request),
                    prepared.Scenario.ActorToken,
                    ct: ct);
                var recallTask =
                    RecallP709ApprovedSourceAsync(
                        api,
                        database,
                        adminToken,
                        prepared.Scenario.SourceReportId,
                        "p711-source-drift-race-recall",
                        ct);
                await Task.WhenAll(applyTask, recallTask);
                var apply = await applyTask;
                Require(
                    apply.StatusCode is
                        HttpStatusCode.OK or
                        HttpStatusCode.Conflict,
                    $"P7-11 source/apply race returned unexpected apply status {(int)apply.StatusCode}.");

                P709InvalidationBundle? invalidation = null;
                P709BindingBundle? committed = null;
                if (apply.StatusCode != HttpStatusCode.OK)
                {
                    var errorCode = P708ErrorCode(apply);
                    Require(
                        errorCode is
                            "DYNAMIC_FLOW_MAPPING_IDENTITY_CONFLICT" or
                            "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT" or
                            "DYNAMIC_FLOW_MAPPING_SOURCE_SIGNATURE_CONFLICT",
                        $"P7-11 source/apply race returned unstable conflict '{errorCode}'.");
                }

                await ProcessP709LifecycleOutboxAsync(
                    api,
                    adminToken,
                    ct);
                if (apply.StatusCode == HttpStatusCode.OK)
                {
                    var mappingWorker = await api.PostAsync(
                        "api/admin/operations/job-runs/dynamic-flow-mapping-outbox/process?maxItems=20",
                        body: null,
                        adminToken,
                        ct: ct);
                    ApiHarnessClient.ExpectStatus(
                        mappingWorker,
                        HttpStatusCode.OK,
                        "P7-11 source/apply race mapping worker");
                    committed =
                        await LoadAndAssertP709BindingAsync(
                            database,
                            prepared.Scenario.TargetReportId,
                            DynamicFlowMappingProvenanceStates
                                .Invalidated,
                            DynamicFlowMappingEventTypes
                                .ApplyCommitted,
                            DynamicFlowMappingLifecycleContract
                                .ApplyInitialOperation,
                            ct);
                    invalidation =
                        await AssertP709SourceInvalidationAsync(
                            database,
                            new P709MappedScenario(
                                prepared.Scenario,
                                CloneP709Request(prepared.Request),
                                committed),
                            DynamicFlowMappingLifecycleContract
                                .SourceLifecycleDriftReason,
                            ct);
                }

                var after =
                    await CaptureP709ScopedLedgerHashAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                var afterCounts =
                    await CountP709MappingLedgerAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                if (committed is null)
                {
                    Require(
                        before == after &&
                        beforeCounts == afterCounts,
                        "P7-11 source drift winner left a mapping write.");
                }
                else
                {
                    Require(
                        afterCounts.Receipts == 1 &&
                        afterCounts.Provenance == 1 &&
                        afterCounts.Events == 2 &&
                        afterCounts.Outbox == 2 &&
                        afterCounts.MappingAudits == 1 &&
                        invalidation is not null,
                        "P7-11 apply winner did not converge through exactly one lifecycle invalidation.");
                }

                var outcome = committed is null
                    ? "SOURCE_DRIFT_WON_ZERO_WRITE"
                    : "APPLY_COMMITTED_THEN_INVALIDATED";
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-11-RACE-SOURCE-DRIFT-VS-APPLY",
                    targetReportId =
                        prepared.Scenario.TargetReportId,
                    sourceReportId =
                        prepared.Scenario.SourceReportId,
                    applyStatus = (int)apply.StatusCode,
                    outcome,
                    beforeCounts,
                    afterCounts,
                    receiptId = committed?.Receipt.Id,
                    provenanceId = committed?.Provenance.Id,
                    invalidationEventId =
                        invalidation?.Event.Id,
                    rebuildIntentId =
                        invalidation?.Outbox.Id,
                    duplicateCount = 0,
                    orphanCount = 0,
                    partialWriteSetCount = 0,
                    rawSourceValuesRecorded = false
                });
                return new CaseObservation(
                    "Concurrent Kestrel source recall versus signed apply either rejected the stale apply with zero writes or committed one receipt that the lifecycle worker invalidated exactly once.",
                    P7MappingFingerprint(
                        "P7-11-RACE-SOURCE-DRIFT-VS-APPLY",
                        "CONVERGED",
                        "DUPLICATE=0",
                        "ORPHAN=0",
                        "PARTIAL=0"));
            });
    }

    private static async Task RunP711TargetCasRaceAsync(
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
            "P7-11-RACE-TARGET-CAS-SINGLE-WINNER",
            async () =>
            {
                var prepared = await PrepareP708ApplyScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "P711-TARGET-CAS",
                    "p711-target-cas-placeholder",
                    activationThrough: 9,
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
                    activationThrough: 9);
                var firstRequest = BuildP709ApplyRequest(
                    prepared.Preview,
                    "p711-target-cas-a");
                var secondRequest = BuildP709ApplyRequest(
                    secondPreview,
                    "p711-target-cas-b");
                var before =
                    await CountP709MappingLedgerAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                RequireP708EmptyMappingLedger(
                    before,
                    "P7-11 target CAS baseline");

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
                    "P7-11 target CAS race did not produce one 200 and one 409.");
                var loserCode = P708ErrorCode(
                    responses.Single(item =>
                        item.StatusCode ==
                        HttpStatusCode.Conflict));
                Require(
                    loserCode ==
                    "DYNAMIC_FLOW_MAPPING_TARGET_REVISION_CONFLICT",
                    $"P7-11 target CAS loser code drifted: {loserCode}.");

                var after =
                    await CountP709MappingLedgerAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                RequireP708SingleMappingWriteSet(
                    after,
                    "P7-11 target CAS winner");
                var binding =
                    await LoadAndAssertP709BindingAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        DynamicFlowMappingProvenanceStates.Current,
                        DynamicFlowMappingEventTypes.ApplyCommitted,
                        DynamicFlowMappingLifecycleContract
                            .ApplyInitialOperation,
                        ct);
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-11-RACE-TARGET-CAS-SINGLE-WINNER",
                    targetReportId =
                        prepared.Scenario.TargetReportId,
                    statuses = responses
                        .Select(item => (int)item.StatusCode)
                        .OrderBy(item => item)
                        .ToArray(),
                    loserCode,
                    before,
                    after,
                    receiptId = binding.Receipt.Id,
                    provenanceId = binding.Provenance.Id,
                    duplicateCount = 0,
                    orphanCount = 0,
                    partialWriteSetCount = 0
                });
                return new CaseObservation(
                    "Two dedicated signed P7-11 applies raced one target CAS; exactly one committed and the loser owned no Mongo row.",
                    P7MappingFingerprint(
                        "P7-11-RACE-TARGET-CAS-SINGLE-WINNER",
                        "HTTP=200,409",
                        loserCode,
                        "WRITESETS=1"));
            });
    }

    private static async Task RunP711ExactReplayAsync(
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
            "P7-11-REPLAY-EXACT",
            async () =>
            {
                var mapped = await CreateP709MappedScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "P711-REPLAY-EXACT",
                    "p711-replay-exact",
                    ct);
                var before =
                    await CaptureP709ScopedLedgerHashAsync(
                        database,
                        mapped.Scenario.TargetReportId,
                        ct);
                var countsBefore =
                    await CountP709MappingLedgerAsync(
                        database,
                        mapped.Scenario.TargetReportId,
                        ct);
                var replay = await api.PostAsync(
                    P709ApplyPath(
                        mapped.Scenario.TargetReportId),
                    CloneP709Request(
                        mapped.InitialApplyRequest),
                    mapped.Scenario.ActorToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    replay,
                    HttpStatusCode.OK,
                    "P7-11 exact replay");
                var after =
                    await CaptureP709ScopedLedgerHashAsync(
                        database,
                        mapped.Scenario.TargetReportId,
                        ct);
                var countsAfter =
                    await CountP709MappingLedgerAsync(
                        database,
                        mapped.Scenario.TargetReportId,
                        ct);
                Require(
                    before == after &&
                    countsBefore == countsAfter,
                    "P7-11 exact replay changed Mongo.");
                var binding =
                    await LoadAndAssertP709BindingAsync(
                        database,
                        mapped.Scenario.TargetReportId,
                        DynamicFlowMappingProvenanceStates.Current,
                        DynamicFlowMappingEventTypes.ApplyCommitted,
                        DynamicFlowMappingLifecycleContract
                            .ApplyInitialOperation,
                        ct);
                Require(
                    binding.Receipt.Id ==
                    mapped.Binding.Receipt.Id &&
                    binding.Provenance.Id ==
                    mapped.Binding.Provenance.Id,
                    "P7-11 exact replay resolved different durable identities.");
                mongoEvidence.Add(new
                {
                    caseId = "P7-11-REPLAY-EXACT",
                    targetReportId =
                        mapped.Scenario.TargetReportId,
                    receiptId = binding.Receipt.Id,
                    provenanceId = binding.Provenance.Id,
                    countsBefore,
                    countsAfter,
                    scopedHashStable = before == after,
                    duplicateCount = 0,
                    orphanCount = 0,
                    partialWriteSetCount = 0
                });
                return new CaseObservation(
                    "Dedicated exact replay returned the original receipt while the complete scoped Mongo ledger remained byte-stable.",
                    P7MappingFingerprint(
                        "P7-11-REPLAY-EXACT",
                        "HTTP=200",
                        "ZERO-WRITE",
                        "IDENTITIES=SAME"));
            });
    }

    private static async Task RunP711ChangedReplayAsync(
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
            "P7-11-REPLAY-CHANGED",
            async () =>
            {
                var mapped = await CreateP709MappedScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "P711-REPLAY-CHANGED",
                    "p711-replay-changed",
                    ct);
                var changed = CloneP709Request(
                    mapped.InitialApplyRequest);
                changed["resultSemanticHash"] =
                    P708DifferentHash(
                        ApiHarnessClient.RequiredString(
                            changed,
                            "resultSemanticHash"));
                var outcome =
                    await AssertP708ConflictAndZeroWriteAsync(
                        api,
                        database,
                        mapped.Scenario.TargetReportId,
                        changed,
                        mapped.Scenario.ActorToken,
                        "P7-11 changed replay",
                        "DYNAMIC_FLOW_MAPPING_COMMAND_REPLAY_MISMATCH",
                        ct);
                mongoEvidence.Add(new
                {
                    caseId = "P7-11-REPLAY-CHANGED",
                    targetReportId =
                        mapped.Scenario.TargetReportId,
                    outcome.ErrorCode,
                    outcome.BeforeHash,
                    outcome.AfterHash,
                    zeroWrite =
                        outcome.BeforeHash ==
                        outcome.AfterHash,
                    duplicateCount = 0,
                    orphanCount = 0,
                    partialWriteSetCount = 0
                });
                return new CaseObservation(
                    "Dedicated changed replay returned the stable 409 mismatch and left the committed write set byte-identical.",
                    P7MappingFingerprint(
                        "P7-11-REPLAY-CHANGED",
                        "HTTP=409",
                        outcome.ErrorCode,
                        "ZERO-WRITE"));
            });
    }

    private static async Task RunP711LifecycleInvalidationAsync(
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
            "P7-11-INVALIDATION-LIFECYCLE",
            async () =>
            {
                var mapped = await CreateP709MappedScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "P711-LIFECYCLE-INVALIDATION",
                    "p711-lifecycle-invalidation-apply",
                    ct);
                await ForceP709SourcePayloadDriftAsync(
                    api,
                    backend,
                    database,
                    mapped.Scenario,
                    "p711-lifecycle-source-save",
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
                    "P7-11 lifecycle invalidation replay duplicated rows.");
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-11-INVALIDATION-LIFECYCLE",
                    targetReportId =
                        mapped.Scenario.TargetReportId,
                    sourceReportId =
                        mapped.Scenario.SourceReportId,
                    receiptId = mapped.Binding.Receipt.Id,
                    provenanceId =
                        mapped.Binding.Provenance.Id,
                    invalidationEventId =
                        invalidation.Event.Id,
                    rebuildIntentId =
                        invalidation.Outbox.Id,
                    beforeRetry,
                    afterRetry,
                    duplicateCount = 0,
                    orphanCount = 0,
                    partialWriteSetCount = 0
                });
                return new CaseObservation(
                    "A dedicated source payload mutation invalidated one provenance and emitted one rebuild intent; repeated lifecycle workers were zero-write.",
                    P7MappingFingerprint(
                        "P7-11-INVALIDATION-LIFECYCLE",
                        "INVALIDATIONS=1",
                        "REBUILD-INTENTS=1",
                        "REPLAY=ZERO-WRITE"));
            });
    }

    private static async Task<P7MappingFixture>
        CloneP711DefinitionFixtureAsync(
            IMongoDatabase database,
            P7MappingFixture seed,
            string suffix,
            CancellationToken ct)
    {
        var familyId = ObjectId.GenerateNewId();
        var versionId = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        var families = database.GetCollection<BsonDocument>(
            "dynamic_flow_templates");
        var versions = database.GetCollection<BsonDocument>(
            "dynamic_flow_template_versions");
        var family = (await families
                .Find(new BsonDocument(
                    "_id",
                    ObjectId.Parse(seed.FamilyId)))
                .SingleAsync(ct))
            .DeepClone()
            .AsBsonDocument;
        var version = (await versions
                .Find(new BsonDocument(
                    "_id",
                    ObjectId.Parse(seed.VersionId)))
                .SingleAsync(ct))
            .DeepClone()
            .AsBsonDocument;

        family["_id"] = familyId;
        family["code"] =
            $"P711_{suffix}_{familyId.ToString()[..8]}";
        family["name"] =
            $"P7-11 isolated {suffix} definition";
        family["currentVersionId"] = versionId;
        family["currentVersionNo"] = 1;
        family["currentVersionHash"] = seed.PayloadHash;
        family["createdAtUtc"] = now;
        family["updatedAtUtc"] = now;

        version["_id"] = versionId;
        version["templateId"] = familyId;
        version["versionNo"] = 1;
        version["createdAtUtc"] = now;
        version["updatedAtUtc"] = now;

        await families.InsertOneAsync(
            family,
            cancellationToken: ct);
        await versions.InsertOneAsync(
            version,
            cancellationToken: ct);
        return seed with
        {
            FamilyId = familyId.ToString(),
            VersionId = versionId.ToString()
        };
    }

    private static async Task RunP711EpochInvalidationAsync(
        HarnessCaseRunner cases,
        List<object> mongoEvidence,
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        string adminToken,
        ProbeFixture baseFixture,
        P709EpochFixture fixture,
        CancellationToken ct)
    {
        await cases.RunAsync(
            "P7-11-INVALIDATION-EPOCH",
            async () =>
            {
                var applied =
                    await PrepareP711EpochAppliedAsync(
                        api,
                        backend,
                        database,
                        adminToken,
                        baseFixture,
                        fixture,
                        "P711-EPOCH-INVALIDATION",
                        "p711-epoch-invalidation-launch",
                        "p711-epoch-invalidation-apply",
                        ct);
                var instance = await LoadP709InstanceAsync(
                    database,
                    applied.FlowInstanceId,
                    ct);
                var request = new JsonObject
                {
                    ["commandId"] =
                        "p711-epoch-invalidation-rollback",
                    ["expectedExecutionEpoch"] =
                        instance.ExecutionEpoch,
                    ["expectedInstanceRevision"] =
                        instance.Revision,
                    ["checkpointNodeId"] = "step_a",
                    ["reason"] =
                        "P7-11 epoch invalidation"
                };
                var rollback = await api.PostAsync(
                    P709EpochPath(
                        applied.WorkId,
                        applied.FlowInstanceId,
                        "rollback"),
                    request,
                    adminToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    rollback,
                    HttpStatusCode.OK,
                    "P7-11 epoch rollback");
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
                    "P7-11 rollback did not create epoch two.");
                var eventId =
                    ApiHarnessClient.RequiredString(
                        rollback.Json,
                        "eventId");
                var intentId =
                    ApiHarnessClient.RequiredString(
                        rollback.Json,
                        "rebuildIntentId");
                await AssertP709EpochInvalidationAsync(
                    database,
                    applied.Binding,
                    eventId,
                    intentId,
                    ct);
                var rebuildCount =
                    await CountP709EpochRebuildsAsync(
                        database,
                        applied.FlowInstanceId,
                        ct);
                var replay = await api.PostAsync(
                    P709EpochPath(
                        applied.WorkId,
                        applied.FlowInstanceId,
                        "rollback"),
                    CloneP709Request(request),
                    adminToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    replay,
                    HttpStatusCode.OK,
                    "P7-11 epoch rollback replay");
                Require(
                    ApiHarnessClient.RequiredBool(
                        replay.Json,
                        "replayed") &&
                    ApiHarnessClient.RequiredString(
                        replay.Json,
                        "eventId") == eventId &&
                    await CountP709EpochRebuildsAsync(
                        database,
                        applied.FlowInstanceId,
                        ct) == rebuildCount,
                    "P7-11 epoch rollback replay duplicated rows.");
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-11-INVALIDATION-EPOCH",
                    applied.WorkId,
                    applied.FlowInstanceId,
                    reportId = applied.Target.ReportId,
                    receiptId =
                        applied.Binding.Receipt.Id,
                    provenanceId =
                        applied.Binding.Provenance.Id,
                    invalidationEventId = eventId,
                    rebuildIntentId = intentId,
                    replacementExecutionEpoch = 2,
                    rebuildCount,
                    duplicateCount = 0,
                    orphanCount = 0,
                    partialWriteSetCount = 0
                });
                return new CaseObservation(
                    "Dedicated FLOW-T12 rollback invalidated the exact epoch-one mapping provenance; exact replay retained one event and one rebuild intent.",
                    P7MappingFingerprint(
                        "P7-11-INVALIDATION-EPOCH",
                        "EPOCH=1->2",
                        "INVALIDATIONS=1",
                        "REBUILDS=1",
                        "REPLAY=ZERO-WRITE"));
            });
    }

    private static async Task RunP711P8P9BarrierAsync(
        HarnessCaseRunner cases,
        List<object> mongoEvidence,
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        string adminToken,
        ProbeFixture baseFixture,
        P709EpochFixture fixture,
        CancellationToken ct)
    {
        await cases.RunAsync(
            "P7-11-P8-P9-ZERO-WRITE",
            async () =>
            {
                var applied =
                    await PrepareP711EpochAppliedAsync(
                        api,
                        backend,
                        database,
                        adminToken,
                        baseFixture,
                        fixture,
                        "P711-P8-P9-BARRIER",
                        "p711-p8-p9-barrier-launch",
                        "p711-p8-p9-barrier-apply",
                        ct);
                var before =
                    await CountP709DeferredExecutionRowsAsync(
                        database,
                        ct);
                var instance = await LoadP709InstanceAsync(
                    database,
                    applied.FlowInstanceId,
                    ct);
                var rollback = await api.PostAsync(
                    P709EpochPath(
                        applied.WorkId,
                        applied.FlowInstanceId,
                        "rollback"),
                    new JsonObject
                    {
                        ["commandId"] =
                            "p711-p8-p9-zero-write-rollback",
                        ["expectedExecutionEpoch"] =
                            instance.ExecutionEpoch,
                        ["expectedInstanceRevision"] =
                            instance.Revision,
                        ["checkpointNodeId"] = "step_a",
                        ["reason"] =
                            "P7-11 P8/P9 zero-write barrier"
                    },
                    adminToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    rollback,
                    HttpStatusCode.OK,
                    "P7-11 P8/P9 barrier rollback");
                var after =
                    await CountP709DeferredExecutionRowsAsync(
                        database,
                        ct);
                Require(
                    before == after,
                    "P7-11 rollback scheduled or materialized P8/P9 rows.");
                var eventId =
                    ApiHarnessClient.RequiredString(
                        rollback.Json,
                        "eventId");
                var intentId =
                    ApiHarnessClient.RequiredString(
                        rollback.Json,
                        "rebuildIntentId");
                await AssertP709EpochInvalidationAsync(
                    database,
                    applied.Binding,
                    eventId,
                    intentId,
                    ct);
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-11-P8-P9-ZERO-WRITE",
                    applied.WorkId,
                    applied.FlowInstanceId,
                    reportId = applied.Target.ReportId,
                    before,
                    after,
                    invalidationEventId = eventId,
                    rebuildIntentId = intentId,
                    p8WriteCount = 0,
                    p9WriteCount = 0,
                    duplicateCount = 0,
                    orphanCount = 0,
                    partialWriteSetCount = 0
                });
                return new CaseObservation(
                    "Dedicated epoch rollback emitted only the P7 invalidation/rebuild ledger; every P8/P9 deferred and summary collection count stayed unchanged.",
                    P7MappingFingerprint(
                        "P7-11-P8-P9-ZERO-WRITE",
                        "P8=0",
                        "P9=0",
                        "P7-REBUILD=1"));
            });
    }

    private static async Task<P711EpochApplied>
        PrepareP711EpochAppliedAsync(
            ApiHarnessClient api,
            BackendServerLease backend,
            IMongoDatabase database,
            string adminToken,
            ProbeFixture baseFixture,
            P709EpochFixture fixture,
            string suffix,
            string launchCommandId,
            string applyCommandId,
            CancellationToken ct)
    {
        var workId = await CloneP7MappingWorkAsync(
            database,
            fixture.WorkId,
            suffix,
            ct);
        var launch = await LaunchAsync(
            api,
            adminToken,
            baseFixture,
            workId,
            fixture.VersionId,
            launchCommandId,
            [fixture.TargetUnitId],
            P601PeriodKey,
            ct);
        RequireStatus(
            launch.Confirm,
            "SUCCEEDED",
            $"{suffix} FLOW-T12 launch");
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
        var target = await PrepareP709EpochTargetAsync(
            api,
            database,
            adminToken,
            actorToken,
            flowInstanceId,
            executionEpoch: 1,
            ct);
        var binding = await ApplyP709EpochMappingAsync(
            api,
            database,
            target,
            applyCommandId,
            ct);
        return new P711EpochApplied(
            workId,
            flowInstanceId,
            target,
            binding);
    }

    private static async Task RunP711ApplyFaultCaseAsync(
        HarnessCaseRunner cases,
        List<object> mongoEvidence,
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        string adminToken,
        ProbeFixture baseFixture,
        P7MappingFixture fixture,
        P711ApplyFaultCase fault,
        CancellationToken ct)
    {
        await cases.RunAsync(
            fault.CaseId,
            async () =>
            {
                var prepared = await PrepareP708ApplyScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    fault.CaseId.Replace(
                        "P7-11-FAULT-",
                        "P711-",
                        StringComparison.Ordinal),
                    fault.CommandId,
                    activationThrough: 9,
                    ct);
                var beforeHash =
                    await CaptureP709ScopedLedgerHashAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                var beforeCounts =
                    await CountP709MappingLedgerAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                RequireP708EmptyMappingLedger(
                    beforeCounts,
                    $"{fault.CaseId} baseline");

                var injected = await api.PostAsync(
                    P709ApplyPath(
                        prepared.Scenario.TargetReportId),
                    CloneP709Request(prepared.Request),
                    prepared.Scenario.ActorToken,
                    ct: ct);
                Require(
                    (int)injected.StatusCode >= 500,
                    $"{fault.CaseId} did not expose an injected server failure; status={(int)injected.StatusCode}.");
                var afterFaultHash =
                    await CaptureP709ScopedLedgerHashAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                var afterFaultCounts =
                    await CountP709MappingLedgerAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);

                string? committedReceiptId = null;
                string? committedOutboxId = null;
                string? committedReceiptState = null;
                string? committedOutboxState = null;
                if (fault.DurableBeforeRetry)
                {
                    RequireP708SingleMappingWriteSet(
                        afterFaultCounts,
                        $"{fault.CaseId} committed boundary");
                    var committedReceipt = await database
                        .GetCollection<
                            DynamicFlowMappingApplyReceipt>(
                            "dynamic_flow_mapping_apply_receipts")
                        .Find(item =>
                            item.TargetReportId ==
                            prepared.Scenario.TargetReportId &&
                            item.CommandId ==
                            fault.CommandId)
                        .SingleAsync(ct);
                    var committedOutbox = await database
                        .GetCollection<
                            DynamicFlowMappingOutboxItem>(
                            "dynamic_flow_mapping_outbox")
                        .Find(item =>
                            item.Id ==
                            committedReceipt.OutboxIntentId)
                        .SingleAsync(ct);
                    Require(
                        committedReceipt.State ==
                        DynamicFlowMappingApplyStates.Committed &&
                        committedOutbox.State ==
                        DynamicFlowMappingOutboxStates.Pending &&
                        beforeHash != afterFaultHash,
                        $"{fault.CaseId} did not stop at the exact committed pre-reconcile state.");
                    committedReceiptId =
                        committedReceipt.Id;
                    committedOutboxId =
                        committedOutbox.Id;
                    committedReceiptState =
                        committedReceipt.State;
                    committedOutboxState =
                        committedOutbox.State;
                }
                else
                {
                    Require(
                        beforeHash == afterFaultHash &&
                        beforeCounts == afterFaultCounts,
                        $"{fault.CaseId} leaked a transaction write.");
                    RequireP708EmptyMappingLedger(
                        afterFaultCounts,
                        $"{fault.CaseId} rolled-back boundary");
                }

                var retry = await api.PostAsync(
                    P709ApplyPath(
                        prepared.Scenario.TargetReportId),
                    CloneP709Request(prepared.Request),
                    prepared.Scenario.ActorToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    retry,
                    HttpStatusCode.OK,
                    $"{fault.CaseId} exact retry");
                var binding =
                    await LoadAndAssertP709BindingAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        DynamicFlowMappingProvenanceStates.Current,
                        DynamicFlowMappingEventTypes.ApplyCommitted,
                        DynamicFlowMappingLifecycleContract
                            .ApplyInitialOperation,
                        ct);
                var finalHash =
                    await CaptureP709ScopedLedgerHashAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                var finalCounts =
                    await CountP709MappingLedgerAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                RequireP708SingleMappingWriteSet(
                    finalCounts,
                    $"{fault.CaseId} terminal retry");
                Require(
                    binding.Receipt.State ==
                    DynamicFlowMappingApplyStates.Reconciled &&
                    binding.Outbox.State ==
                    DynamicFlowMappingOutboxStates.Reconciled,
                    $"{fault.CaseId} retry did not reconcile.");
                if (fault.DurableBeforeRetry)
                {
                    Require(
                        binding.Receipt.Id ==
                        committedReceiptId &&
                        binding.Outbox.Id ==
                        committedOutboxId,
                        $"{fault.CaseId} retry replaced the committed identities.");
                }

                mongoEvidence.Add(new
                {
                    caseId = fault.CaseId,
                    fault.CommandId,
                    faultPoint = fault.Point,
                    targetReportId =
                        prepared.Scenario.TargetReportId,
                    injectedStatus =
                        (int)injected.StatusCode,
                    retryStatus = (int)retry.StatusCode,
                    fault.DurableBeforeRetry,
                    beforeHash,
                    afterFaultHash,
                    finalHash,
                    beforeCounts,
                    afterFaultCounts,
                    finalCounts,
                    committedReceiptId,
                    committedOutboxId,
                    committedReceiptState,
                    committedOutboxState,
                    terminalReceiptId =
                        binding.Receipt.Id,
                    terminalOutboxId =
                        binding.Outbox.Id,
                    terminalReceiptState =
                        binding.Receipt.State,
                    terminalOutboxState =
                        binding.Outbox.State,
                    duplicateCount = 0,
                    orphanCount = 0,
                    partialWriteSetCount = 0,
                    rawSourceValuesRecorded = false
                });
                return new CaseObservation(
                    fault.DurableBeforeRetry
                        ? $"The exact {fault.Point} failure exposed one committed intent; same-command replay reconciled those identities once."
                        : $"The exact {fault.Point} failure aborted the complete Mongo transaction; same-command retry created one reconciled write set.",
                    P7MappingFingerprint(
                        fault.CaseId,
                        fault.Point,
                        fault.DurableBeforeRetry
                            ? "DURABLE-BEFORE-RETRY"
                            : "ZERO-WRITE-BEFORE-RETRY",
                        "FINAL=RECONCILED",
                        "DUPLICATE=0",
                        "ORPHAN=0",
                        "PARTIAL=0"));
            });
    }

    private static async Task RunP711ReconcileConvergenceAsync(
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
            "P7-11-RECONCILE-EXACT-CONVERGENCE",
            async () =>
            {
                var prepared = await PrepareP708ApplyScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "P711-RECONCILE",
                    P711ReconcileCommandId,
                    activationThrough: 9,
                    ct);
                var beforeCounts =
                    await CountP709MappingLedgerAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                RequireP708EmptyMappingLedger(
                    beforeCounts,
                    "P7-11 reconcile baseline");
                var apply = await api.PostAsync(
                    P709ApplyPath(
                        prepared.Scenario.TargetReportId),
                    CloneP709Request(prepared.Request),
                    prepared.Scenario.ActorToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    apply,
                    HttpStatusCode.OK,
                    "P7-11 reconcile faulted apply");

                var partialReceipt = await database
                    .GetCollection<
                        DynamicFlowMappingApplyReceipt>(
                        "dynamic_flow_mapping_apply_receipts")
                    .Find(item =>
                        item.TargetReportId ==
                        prepared.Scenario.TargetReportId &&
                        item.CommandId ==
                        P711ReconcileCommandId)
                    .SingleAsync(ct);
                var partialOutbox = await database
                    .GetCollection<DynamicFlowMappingOutboxItem>(
                        "dynamic_flow_mapping_outbox")
                    .Find(item =>
                        item.Id ==
                        partialReceipt.OutboxIntentId)
                    .SingleAsync(ct);
                Require(
                    partialReceipt.State ==
                    DynamicFlowMappingApplyStates.Partial &&
                    partialOutbox.State ==
                    DynamicFlowMappingOutboxStates.Partial &&
                    partialOutbox.AttemptCount == 1 &&
                    partialOutbox.ProjectorCheckpoints.All(
                        checkpoint =>
                            checkpoint.State ==
                            DynamicFlowMappingProjectorCheckpointStates
                                .Pending),
                    "P7-11 BEFORE_PROJECTORS fault did not leave one wholly pending plan.");
                var partialCounts =
                    await CountP709MappingLedgerAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                RequireP708SingleMappingWriteSet(
                    partialCounts,
                    "P7-11 reconcile partial");

                await DelayUntilP708OutboxDueAsync(
                    partialOutbox,
                    "P7-11 projector retry",
                    ct);
                var projectorWorker = await api.PostAsync(
                    "api/admin/operations/job-runs/dynamic-flow-mapping-outbox/process?maxItems=20",
                    body: null,
                    adminToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    projectorWorker,
                    HttpStatusCode.OK,
                    "P7-11 projector worker");
                Require(
                    ApiHarnessClient.RequiredInt(
                        projectorWorker.Json,
                        "processed") == 1,
                    "P7-11 projector worker did not process one intent.");
                var projectedReceipt = await database
                    .GetCollection<
                        DynamicFlowMappingApplyReceipt>(
                        "dynamic_flow_mapping_apply_receipts")
                    .Find(item =>
                        item.Id == partialReceipt.Id)
                    .SingleAsync(ct);
                var projectedOutbox = await database
                    .GetCollection<DynamicFlowMappingOutboxItem>(
                        "dynamic_flow_mapping_outbox")
                    .Find(item =>
                        item.Id == partialOutbox.Id)
                    .SingleAsync(ct);
                Require(
                    projectedReceipt.State ==
                    DynamicFlowMappingApplyStates.Partial &&
                    projectedOutbox.State ==
                    DynamicFlowMappingOutboxStates.Partial &&
                    projectedOutbox.ProjectorCheckpoints.All(
                        checkpoint =>
                            checkpoint.State ==
                            DynamicFlowMappingProjectorCheckpointStates
                                .Completed &&
                            checkpoint.AttemptCount == 1 &&
                            checkpoint.CompletedRepairEpoch ==
                            projectedOutbox.RepairEpoch &&
                            IsP7LowerSha256(
                                checkpoint.CompletionHash ??
                                string.Empty)),
                    "P7-11 BEFORE_FINALIZE fault did not retain completed checkpoint evidence.");
                var projectedSideEffectHash =
                    await CaptureP708ProjectorSideEffectHashAsync(
                        database,
                        prepared.Scenario,
                        ct);

                await DelayUntilP708OutboxDueAsync(
                    projectedOutbox,
                    "P7-11 finalize retry",
                    ct);
                var finalizeWorker = await api.PostAsync(
                    "api/admin/operations/job-runs/dynamic-flow-mapping-outbox/process?maxItems=20",
                    body: null,
                    adminToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    finalizeWorker,
                    HttpStatusCode.OK,
                    "P7-11 finalize worker");
                Require(
                    ApiHarnessClient.RequiredInt(
                        finalizeWorker.Json,
                        "processed") == 1,
                    "P7-11 finalize worker did not process one intent.");
                var terminal =
                    await LoadAndAssertP709BindingAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        DynamicFlowMappingProvenanceStates.Current,
                        DynamicFlowMappingEventTypes.ApplyCommitted,
                        DynamicFlowMappingLifecycleContract
                            .ApplyInitialOperation,
                        ct);
                var terminalCounts =
                    await CountP709MappingLedgerAsync(
                        database,
                        prepared.Scenario.TargetReportId,
                        ct);
                Require(
                    terminalCounts == partialCounts &&
                    terminal.Receipt.Id ==
                    partialReceipt.Id &&
                    terminal.Outbox.Id ==
                    partialOutbox.Id &&
                    terminal.Outbox.ProjectorCheckpoints.All(
                        checkpoint =>
                            checkpoint.AttemptCount == 1),
                    "P7-11 reconcile convergence duplicated a row or projector.");
                var terminalSideEffectHash =
                    await CaptureP708ProjectorSideEffectHashAsync(
                        database,
                        prepared.Scenario,
                        ct);
                Require(
                    terminalSideEffectHash ==
                    projectedSideEffectHash,
                    "P7-11 finalize-only retry changed a completed side effect.");

                var beforeReplay =
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
                    "P7-11 terminal worker replay");
                var afterReplay =
                    await CaptureP708ProjectorSurfaceHashAsync(
                        database,
                        prepared.Scenario,
                        ct);
                Require(
                    ApiHarnessClient.RequiredInt(
                        replayWorker.Json,
                        "processed") == 0 &&
                    beforeReplay == afterReplay,
                    "P7-11 terminal worker replay was not zero-write.");

                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-11-RECONCILE-EXACT-CONVERGENCE",
                    targetReportId =
                        prepared.Scenario.TargetReportId,
                    receiptId = terminal.Receipt.Id,
                    outboxId = terminal.Outbox.Id,
                    initialAttemptCount =
                        partialOutbox.AttemptCount,
                    projectedAttemptCount =
                        projectedOutbox.AttemptCount,
                    terminalAttemptCount =
                        terminal.Outbox.AttemptCount,
                    projectorAttempts =
                        terminal.Outbox
                            .ProjectorCheckpoints
                            .OrderBy(
                                item => item.Projector,
                                StringComparer.Ordinal)
                            .Select(item => new
                            {
                                item.Projector,
                                item.AttemptCount,
                                item.CompletionHash
                            })
                            .ToArray(),
                    partialCounts,
                    terminalCounts,
                    projectedSideEffectHash,
                    terminalSideEffectHash,
                    replayProcessed = 0,
                    replayHashStable =
                        beforeReplay == afterReplay,
                    duplicateCount = 0,
                    orphanCount = 0,
                    partialWriteSetCount = 0,
                    outstandingLeaseCount = 0
                });
                return new CaseObservation(
                    "Two exact reconcile faults produced one PARTIAL intent; checkpointed retries ran every projector once, finalized the same identities, and terminal replay was zero-write.",
                    P7MappingFingerprint(
                        "P7-11-RECONCILE-EXACT-CONVERGENCE",
                        "FAULTS=2",
                        "PROJECTOR-ATTEMPTS=1",
                        "FINAL=RECONCILED",
                        "REPLAY=ZERO-WRITE"));
            });
    }

    private static async Task RunP711LeaseLossAsync(
        HarnessCaseRunner cases,
        List<object> mongoEvidence,
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        string adminToken,
        ProbeFixture baseFixture,
        P7MappingFixture fixture,
        List<ApiExchangeEvidence> additionalApiEvidence,
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        MongoReplicaSetLease mongo,
        CancellationToken ct)
    {
        await cases.RunAsync(
            "P7-11-FAULT-LEASE-LOSS",
            async () =>
            {
                var prepared = await PrepareP708ApplyScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    "P711-LEASE-LOSS",
                    P711LeaseLossCommandId,
                    activationThrough: 9,
                    ct);
                var seed = await api.PostAsync(
                    P709ApplyPath(
                        prepared.Scenario.TargetReportId),
                    CloneP709Request(prepared.Request),
                    prepared.Scenario.ActorToken,
                    ct: ct);
                Require(
                    (int)seed.StatusCode >= 500,
                    "P7-11 lease-loss seed did not stop after transaction commit.");
                var receipt = await database
                    .GetCollection<
                        DynamicFlowMappingApplyReceipt>(
                        "dynamic_flow_mapping_apply_receipts")
                    .Find(item =>
                        item.TargetReportId ==
                        prepared.Scenario.TargetReportId &&
                        item.CommandId ==
                        P711LeaseLossCommandId)
                    .SingleAsync(ct);
                var outboxCollection = database
                    .GetCollection<DynamicFlowMappingOutboxItem>(
                        "dynamic_flow_mapping_outbox");
                var pending = await outboxCollection
                    .Find(item =>
                        item.Id == receipt.OutboxIntentId)
                    .SingleAsync(ct);
                Require(
                    receipt.State ==
                    DynamicFlowMappingApplyStates.Committed &&
                    pending.State ==
                    DynamicFlowMappingOutboxStates.Pending &&
                    pending.AttemptCount == 0,
                    "P7-11 lease-loss seed is not one committed pending intent.");

                var markerRoot = Path.Combine(
                    iterationRoot,
                    "p711-lease-loss-markers");
                Directory.CreateDirectory(markerRoot);
                var reached = Path.GetFullPath(
                    Path.Combine(markerRoot, "reached.marker"));
                var release = Path.GetFullPath(
                    Path.Combine(markerRoot, "release.marker"));
                BackendServerLease? leaseBackend = null;
                ApiHarnessClient? leaseApi = null;
                ApiHarnessClient? freshApi = null;
                try
                {
                    var backendRoot = Path.Combine(
                        iterationRoot,
                        "p711-lease-loss-backend");
                    Directory.CreateDirectory(backendRoot);
                    leaseBackend =
                        await BackendServerLease.StartAsync(
                            paths,
                            backendRoot,
                            $"{runKey}_p711_lease",
                            mongo,
                            ct,
                            new BackendServerOptions
                            {
                                SkipMongoIndexInitializationForTesting = true,
                                EnableDynamicFlowP7MappingCandidate =
                                    true,
                                DynamicFlowP7MappingActivationThrough =
                                    11,
                                DynamicFlowMappingReconcilePauseBindings =
                                [
                                    new DynamicFlowMappingReconcilePauseBinding(
                                        P711LeaseLossCommandId,
                                        pending.Id,
                                        DynamicFlowMappingReconcilePausePoints
                                            .AfterOutboxClaim,
                                        reached,
                                        release)
                                ]
                            });
                    leaseApi = new ApiHarnessClient(
                        leaseBackend.BaseUri);
                    var leaseAdminToken =
                        await LoginP711AdminAsync(
                            leaseApi,
                            leaseBackend,
                            database,
                            ct);
                    var staleWorker = leaseApi.PostAsync(
                        "api/admin/operations/job-runs/dynamic-flow-mapping-outbox/process?maxItems=20",
                        body: null,
                        leaseAdminToken,
                        ct: ct);
                    await WaitForP611MarkerAsync(
                        reached,
                        leaseBackend,
                        ct);

                    var staleClaim = await outboxCollection
                        .Find(item =>
                            item.Id == pending.Id &&
                            item.State ==
                            DynamicFlowMappingOutboxStates.Processing)
                        .SingleAsync(ct);
                    Require(
                        !string.IsNullOrWhiteSpace(
                            staleClaim.LeaseId) &&
                        staleClaim.LeaseUntilUtc >
                        DateTime.UtcNow,
                        "P7-11 mapping worker did not expose a live lease.");
                    var staleLeaseId =
                        staleClaim.LeaseId!;
                    var replacementLeaseId =
                        ObjectId.GenerateNewId().ToString();
                    var replacementExpiry =
                        DateTime.UtcNow.AddSeconds(-1);
                    var replaced = await outboxCollection
                        .UpdateOneAsync(
                            item =>
                                item.Id == staleClaim.Id &&
                                item.State ==
                                DynamicFlowMappingOutboxStates
                                    .Processing &&
                                item.LeaseId ==
                                staleLeaseId,
                            Builders<
                                DynamicFlowMappingOutboxItem>
                                .Update
                                .Set(
                                    item => item.LeaseId,
                                    replacementLeaseId)
                                .Set(
                                    item => item.LeaseUntilUtc,
                                    replacementExpiry)
                                .Set(
                                    item => item.UpdatedAtUtc,
                                    DateTime.UtcNow),
                            cancellationToken: ct);
                    Require(
                        replaced.ModifiedCount == 1,
                        "P7-11 lease replacement CAS lost its claim.");

                    freshApi = new ApiHarnessClient(
                        leaseBackend.BaseUri);
                    var freshWorker = await freshApi.PostAsync(
                        "api/admin/operations/job-runs/dynamic-flow-mapping-outbox/process?maxItems=20",
                        body: null,
                        leaseAdminToken,
                        ct: ct);
                    ApiHarnessClient.ExpectStatus(
                        freshWorker,
                        HttpStatusCode.OK,
                        "P7-11 fresh mapping worker");
                    Require(
                        ApiHarnessClient.RequiredInt(
                            freshWorker.Json,
                            "processed") == 1,
                        "P7-11 fresh mapping worker did not process the expired replacement lease.");
                    var afterFresh = await outboxCollection
                        .Find(item =>
                            item.Id == pending.Id)
                        .SingleAsync(ct);
                    Require(
                        afterFresh.State ==
                        DynamicFlowMappingOutboxStates.Reconciled &&
                        string.IsNullOrWhiteSpace(
                            afterFresh.LeaseId),
                        "P7-11 fresh mapping worker did not reconcile and release the lease.");

                    CreateP611ReleaseMarker(release);
                    var staleResponse = await staleWorker
                        .WaitAsync(
                            TimeSpan.FromSeconds(20),
                            ct);
                    ApiHarnessClient.ExpectStatus(
                        staleResponse,
                        HttpStatusCode.OK,
                        "P7-11 released stale mapping worker");
                    var afterStale = await outboxCollection
                        .Find(item =>
                            item.Id == pending.Id)
                        .SingleAsync(ct);
                    Require(
                        afterStale.State ==
                        DynamicFlowMappingOutboxStates.Reconciled &&
                        afterStale.LeaseId is null &&
                        afterStale.ProjectorCheckpoints.All(
                            checkpoint =>
                                checkpoint.State ==
                                DynamicFlowMappingProjectorCheckpointStates
                                    .Completed &&
                                checkpoint.AttemptCount == 1),
                        "P7-11 stale mapping worker mutated the fresh worker result.");

                    var replay = await api.PostAsync(
                        P709ApplyPath(
                            prepared.Scenario.TargetReportId),
                        CloneP709Request(prepared.Request),
                        prepared.Scenario.ActorToken,
                        ct: ct);
                    ApiHarnessClient.ExpectStatus(
                        replay,
                        HttpStatusCode.OK,
                        "P7-11 lease-loss exact replay");
                    var terminal =
                        await LoadAndAssertP709BindingAsync(
                            database,
                            prepared.Scenario.TargetReportId,
                            DynamicFlowMappingProvenanceStates
                                .Current,
                            DynamicFlowMappingEventTypes
                                .ApplyCommitted,
                            DynamicFlowMappingLifecycleContract
                                .ApplyInitialOperation,
                            ct);
                    var counts =
                        await CountP709MappingLedgerAsync(
                            database,
                            prepared.Scenario.TargetReportId,
                            ct);
                    RequireP708SingleMappingWriteSet(
                        counts,
                        "P7-11 lease-loss terminal");
                    mongoEvidence.Add(new
                    {
                        caseId =
                            "P7-11-FAULT-LEASE-LOSS",
                        targetReportId =
                            prepared.Scenario.TargetReportId,
                        receiptId = terminal.Receipt.Id,
                        outboxId = terminal.Outbox.Id,
                        staleLeaseId,
                        replacementLeaseId,
                        replacementExpiry,
                        replacedCount =
                            replaced.ModifiedCount,
                        freshWorkerProcessed = 1,
                        staleWorkerReleased = true,
                        terminalReceiptState =
                            terminal.Receipt.State,
                        terminalOutboxState =
                            terminal.Outbox.State,
                        projectorAttempts =
                            terminal.Outbox
                                .ProjectorCheckpoints
                                .Select(item =>
                                    item.AttemptCount)
                                .ToArray(),
                        counts,
                        duplicateCount = 0,
                        orphanCount = 0,
                        partialWriteSetCount = 0,
                        outstandingLeaseCount = 0
                    });
                }
                finally
                {
                    if (freshApi is not null)
                    {
                        CaptureP711ApiExchanges(
                            additionalApiEvidence,
                            freshApi);
                        freshApi.Dispose();
                    }
                    if (leaseApi is not null)
                    {
                        CaptureP711ApiExchanges(
                            additionalApiEvidence,
                            leaseApi);
                        leaseApi.Dispose();
                    }
                    if (leaseBackend is not null)
                    {
                        try
                        {
                            await leaseBackend.StopAsync();
                        }
                        finally
                        {
                            await leaseBackend.DisposeAsync();
                        }
                        Require(
                            leaseBackend.StopVerified &&
                            leaseBackend.PortReleaseVerified,
                            "P7-11 lease-loss backend cleanup failed.");
                    }
                }

                return new CaseObservation(
                    "A paused stale mapping worker lost its replaced lease; a fresh worker reconciled once and the released stale worker could not mutate the terminal result.",
                    P7MappingFingerprint(
                        "P7-11-FAULT-LEASE-LOSS",
                        "LEASE=REPLACED-AND-EXPIRED",
                        "FRESH=RECONCILED",
                        "STALE=FENCED",
                        "PROJECTOR-ATTEMPTS=1"));
            });
    }

    private static async Task RunP711CrashBeforeCommitAsync(
        HarnessCaseRunner cases,
        List<object> mongoEvidence,
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        MongoReplicaSetLease mongo,
        IMongoDatabase database,
        ProbeFixture baseFixture,
        P7MappingFixture fixture,
        List<ApiExchangeEvidence> additionalApiEvidence,
        CancellationToken ct)
    {
        await RunP711CrashRestartAsync(
            cases,
            mongoEvidence,
            paths,
            iterationRoot,
            runKey,
            mongo,
            database,
            baseFixture,
            fixture,
            additionalApiEvidence,
            "P7-11-CRASH-BEFORE-COMMIT-RESTART",
            P711CrashBeforeCommandId,
            "p711-crash-before-commit",
            DynamicFlowMappingApplyFaultPoints
                .BeforeTransactionCommit,
            durableBeforeRestart: false,
            ct: ct);
    }

    private static async Task RunP711CrashAfterCommitAsync(
        HarnessCaseRunner cases,
        List<object> mongoEvidence,
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        MongoReplicaSetLease mongo,
        IMongoDatabase database,
        ProbeFixture baseFixture,
        P7MappingFixture fixture,
        List<ApiExchangeEvidence> additionalApiEvidence,
        CancellationToken ct)
    {
        await RunP711CrashRestartAsync(
            cases,
            mongoEvidence,
            paths,
            iterationRoot,
            runKey,
            mongo,
            database,
            baseFixture,
            fixture,
            additionalApiEvidence,
            "P7-11-CRASH-AFTER-COMMIT-RESTART",
            P711CrashAfterCommandId,
            "p711-crash-after-commit",
            DynamicFlowMappingApplyFaultPoints
                .AfterTransactionCommit,
            durableBeforeRestart: true,
            ct: ct);
    }

    private static async Task RunP711CrashRestartAsync(
        HarnessCaseRunner cases,
        List<object> mongoEvidence,
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        MongoReplicaSetLease mongo,
        IMongoDatabase database,
        ProbeFixture baseFixture,
        P7MappingFixture fixture,
        List<ApiExchangeEvidence> additionalApiEvidence,
        string caseId,
        string commandId,
        string directoryName,
        string pausePoint,
        bool durableBeforeRestart,
        CancellationToken ct)
    {
        await cases.RunAsync(
            caseId,
            async () =>
            {
                var caseRoot = Path.Combine(
                    iterationRoot,
                    directoryName);
                var markerRoot = Path.Combine(
                    caseRoot,
                    "markers");
                Directory.CreateDirectory(markerRoot);
                var reached = Path.GetFullPath(
                    Path.Combine(markerRoot, "reached.marker"));
                var release = Path.GetFullPath(
                    Path.Combine(markerRoot, "release.marker"));
                var adminDatabase =
                    mongo.Client.GetDatabase("admin");

                BackendServerLease? crashBackend = null;
                ApiHarnessClient? crashApi = null;
                P708PreparedScenario? prepared = null;
                P611InterruptedRequest? interrupted = null;
                P611MongoTransactionGauge? transactionBaseline = null;
                P611MongoTransactionGauge? transactionAtPause = null;
                string? beforeHash = null;
                P709MappingLedgerCounts? beforeCounts = null;
                try
                {
                    var crashRoot = Path.Combine(
                        caseRoot,
                        "backend-kill");
                    Directory.CreateDirectory(crashRoot);
                    crashBackend =
                        await BackendServerLease.StartAsync(
                            paths,
                            crashRoot,
                            $"{runKey}_{directoryName}_kill",
                            mongo,
                            ct,
                            new BackendServerOptions
                            {
                                SkipMongoIndexInitializationForTesting = true,
                                EnableDynamicFlowP7MappingCandidate =
                                    true,
                                DynamicFlowP7MappingActivationThrough =
                                    11,
                                DynamicFlowMappingApplyPauseBindings =
                                [
                                    new DynamicFlowMappingApplyPauseBinding(
                                        commandId,
                                        pausePoint,
                                        reached,
                                        release)
                                ]
                            });
                    crashApi = new ApiHarnessClient(
                        crashBackend.BaseUri);
                    var crashAdminToken =
                        await LoginP711AdminAsync(
                            crashApi,
                            crashBackend,
                            database,
                            ct);
                    prepared =
                        await PrepareP708ApplyScenarioAsync(
                            crashApi,
                            crashBackend,
                            database,
                            crashAdminToken,
                            baseFixture,
                            fixture,
                            durableBeforeRestart
                                ? "P711-CRASH-AFTER"
                                : "P711-CRASH-BEFORE",
                            commandId,
                            activationThrough: 9,
                            ct);
                    beforeHash =
                        await CaptureP709ScopedLedgerHashAsync(
                            database,
                            prepared.Scenario.TargetReportId,
                            ct);
                    beforeCounts =
                        await CountP709MappingLedgerAsync(
                            database,
                            prepared.Scenario.TargetReportId,
                            ct);
                    RequireP708EmptyMappingLedger(
                        beforeCounts,
                        $"{caseId} baseline");
                    transactionBaseline =
                        await CaptureP611MongoTransactionGaugeAsync(
                            adminDatabase,
                            ct);
                    Require(
                        transactionBaseline.CurrentOpen == 0 &&
                        transactionBaseline.CurrentActive == 0 &&
                        transactionBaseline.CurrentInactive == 0,
                        $"{caseId} requires an isolated Mongo transaction baseline: {transactionBaseline}.");

                    var inFlight = crashApi.PostAsync(
                        P709ApplyPath(
                            prepared.Scenario.TargetReportId),
                        CloneP709Request(prepared.Request),
                        prepared.Scenario.ActorToken,
                        ct: CancellationToken.None);
                    await WaitForP611MarkerAsync(
                        reached,
                        crashBackend,
                        ct);
                    transactionAtPause =
                        await CaptureP611MongoTransactionGaugeAsync(
                            adminDatabase,
                            ct);
                    Require(
                        transactionAtPause.TotalStarted ==
                        transactionBaseline.TotalStarted + 1,
                        $"{caseId} did not execute exactly one Mongo transaction before the pause: baseline={transactionBaseline}; paused={transactionAtPause}.");
                    if (durableBeforeRestart)
                    {
                        Require(
                            transactionAtPause.CurrentOpen == 0,
                            $"{caseId} paused with an uncommitted Mongo transaction: {transactionAtPause}.");
                    }
                    else
                    {
                        Require(
                            transactionAtPause.CurrentOpen == 1 &&
                            transactionAtPause.CurrentOpen ==
                            transactionAtPause.CurrentActive +
                            transactionAtPause.CurrentInactive,
                            $"{caseId} did not pause with exactly one open Mongo transaction: {transactionAtPause}.");
                    }

                    await crashBackend.StopAsync();
                    interrupted =
                        await ObserveP611InterruptedRequestAsync(
                            inFlight);
                    Require(
                        interrupted.Interrupted,
                        $"{caseId} apply request survived the hard process kill.");
                }
                finally
                {
                    if (crashApi is not null)
                    {
                        CaptureP711ApiExchanges(
                            additionalApiEvidence,
                            crashApi);
                        crashApi.Dispose();
                    }
                    await DisposeP711BackendAsync(
                        crashBackend,
                        $"{caseId} killed backend");
                }

                var crashPrepared = prepared ??
                    throw new InvalidOperationException(
                        $"{caseId} did not prepare its mapping scenario.");
                var baselineHash = beforeHash ??
                    throw new InvalidOperationException(
                        $"{caseId} did not capture its baseline hash.");
                var baselineCounts = beforeCounts ??
                    throw new InvalidOperationException(
                        $"{caseId} did not capture its baseline counts.");
                var baselineGauge = transactionBaseline ??
                    throw new InvalidOperationException(
                        $"{caseId} did not capture its transaction baseline.");
                var pausedGauge = transactionAtPause ??
                    throw new InvalidOperationException(
                        $"{caseId} did not capture its paused transaction gauge.");
                var interruptedRequest = interrupted ??
                    throw new InvalidOperationException(
                        $"{caseId} did not observe its interrupted request.");

                var afterCrashHash =
                    await CaptureP709ScopedLedgerHashAsync(
                        database,
                        crashPrepared.Scenario.TargetReportId,
                        ct);
                var afterCrashCounts =
                    await CountP709MappingLedgerAsync(
                        database,
                        crashPrepared.Scenario.TargetReportId,
                        ct);
                P611OrphanTransactionRecovery? orphanRecovery = null;
                string? committedReceiptId = null;
                string? committedOutboxId = null;
                string? committedReceiptState = null;
                string? committedOutboxState = null;
                if (durableBeforeRestart)
                {
                    RequireP708SingleMappingWriteSet(
                        afterCrashCounts,
                        $"{caseId} durable crash state");
                    var committedReceipt = await database
                        .GetCollection<
                            DynamicFlowMappingApplyReceipt>(
                            "dynamic_flow_mapping_apply_receipts")
                        .Find(item =>
                            item.TargetReportId ==
                            crashPrepared.Scenario.TargetReportId &&
                            item.CommandId == commandId)
                        .SingleAsync(ct);
                    var committedOutbox = await database
                        .GetCollection<
                            DynamicFlowMappingOutboxItem>(
                            "dynamic_flow_mapping_outbox")
                        .Find(item =>
                            item.Id ==
                            committedReceipt.OutboxIntentId)
                        .SingleAsync(ct);
                    Require(
                        committedReceipt.State ==
                        DynamicFlowMappingApplyStates.Committed &&
                        committedOutbox.State ==
                        DynamicFlowMappingOutboxStates.Pending &&
                        committedOutbox.AttemptCount == 0 &&
                        baselineHash != afterCrashHash,
                        $"{caseId} did not preserve exactly one committed pending intent after process death.");
                    committedReceiptId = committedReceipt.Id;
                    committedOutboxId = committedOutbox.Id;
                    committedReceiptState = committedReceipt.State;
                    committedOutboxState = committedOutbox.State;
                }
                else
                {
                    Require(
                        baselineHash == afterCrashHash &&
                        baselineCounts == afterCrashCounts,
                        $"{caseId} leaked writes from the killed transaction.");
                    RequireP708EmptyMappingLedger(
                        afterCrashCounts,
                        $"{caseId} post-kill rollback");
                    orphanRecovery =
                        await WaitForP611OrphanTransactionRecoveryAsync(
                            adminDatabase,
                            baselineGauge,
                            ct);
                }

                BackendServerLease? restartBackend = null;
                ApiHarnessClient? restartApi = null;
                ApiHarnessResponse? restartApply = null;
                ApiHarnessResponse? terminalReplay = null;
                P709BindingBundle? terminalBinding = null;
                P709MappingLedgerCounts? terminalCounts = null;
                string? beforeTerminalReplayHash = null;
                string? afterTerminalReplayHash = null;
                try
                {
                    var restartRoot = Path.Combine(
                        caseRoot,
                        "backend-restart");
                    Directory.CreateDirectory(restartRoot);
                    restartBackend =
                        await BackendServerLease.StartAsync(
                            paths,
                            restartRoot,
                            $"{runKey}_{directoryName}_restart",
                            mongo,
                            ct,
                            new BackendServerOptions
                            {
                                SkipMongoIndexInitializationForTesting = true,
                                EnableDynamicFlowP7MappingCandidate =
                                    true,
                                DynamicFlowP7MappingActivationThrough =
                                    11
                            });
                    restartApi = new ApiHarnessClient(
                        restartBackend.BaseUri);
                    var restartActorToken =
                        await PrepareP601ActorLoginAsync(
                            restartApi,
                            restartBackend,
                            database,
                            crashPrepared.Scenario.ActorUserId,
                            ct);
                    JsonObject restartRequest;
                    if (durableBeforeRestart)
                    {
                        restartRequest =
                            CloneP709Request(
                                crashPrepared.Request);
                    }
                    else
                    {
                        var restartScenario =
                            crashPrepared.Scenario with
                            {
                                ActorToken = restartActorToken
                            };
                        var restartPreview =
                            await PreviewP7MappingAsync(
                                restartApi,
                                restartScenario,
                                new JsonObject(),
                                ct);
                        AssertP7MappingPreviewIdentity(
                            restartPreview,
                            fixture,
                            restartScenario,
                            9);
                        restartRequest =
                            BuildP709ApplyRequest(
                                restartPreview,
                                commandId);
                    }

                    restartApply = await restartApi.PostAsync(
                        P709ApplyPath(
                            crashPrepared.Scenario.TargetReportId),
                        CloneP709Request(restartRequest),
                        restartActorToken,
                        ct: ct);
                    ApiHarnessClient.ExpectStatus(
                        restartApply,
                        HttpStatusCode.OK,
                        $"{caseId} restart apply");
                    terminalBinding =
                        await LoadAndAssertP709BindingAsync(
                            database,
                            crashPrepared.Scenario.TargetReportId,
                            DynamicFlowMappingProvenanceStates.Current,
                            DynamicFlowMappingEventTypes.ApplyCommitted,
                            DynamicFlowMappingLifecycleContract
                                .ApplyInitialOperation,
                            ct);
                    terminalCounts =
                        await CountP709MappingLedgerAsync(
                            database,
                            crashPrepared.Scenario.TargetReportId,
                            ct);
                    RequireP708SingleMappingWriteSet(
                        terminalCounts,
                        $"{caseId} restart convergence");
                    Require(
                        terminalBinding.Receipt.State ==
                        DynamicFlowMappingApplyStates.Reconciled &&
                        terminalBinding.Outbox.State ==
                        DynamicFlowMappingOutboxStates.Reconciled &&
                        string.IsNullOrWhiteSpace(
                            terminalBinding.Outbox.LeaseId),
                        $"{caseId} restart did not converge to one lease-free reconciled write set.");
                    if (durableBeforeRestart)
                    {
                        Require(
                            terminalBinding.Receipt.Id ==
                            committedReceiptId &&
                            terminalBinding.Outbox.Id ==
                            committedOutboxId,
                            $"{caseId} restart replaced the committed receipt/outbox identities.");
                    }

                    beforeTerminalReplayHash =
                        await CaptureP709ScopedLedgerHashAsync(
                            database,
                            crashPrepared.Scenario.TargetReportId,
                            ct);
                    terminalReplay = await restartApi.PostAsync(
                        P709ApplyPath(
                            crashPrepared.Scenario.TargetReportId),
                        CloneP709Request(restartRequest),
                        restartActorToken,
                        ct: ct);
                    ApiHarnessClient.ExpectStatus(
                        terminalReplay,
                        HttpStatusCode.OK,
                        $"{caseId} terminal exact replay");
                    afterTerminalReplayHash =
                        await CaptureP709ScopedLedgerHashAsync(
                            database,
                            crashPrepared.Scenario.TargetReportId,
                            ct);
                    Require(
                        beforeTerminalReplayHash ==
                        afterTerminalReplayHash &&
                        terminalCounts ==
                        await CountP709MappingLedgerAsync(
                            database,
                            crashPrepared.Scenario.TargetReportId,
                            ct),
                        $"{caseId} terminal replay was not zero-write.");
                }
                finally
                {
                    if (restartApi is not null)
                    {
                        CaptureP711ApiExchanges(
                            additionalApiEvidence,
                            restartApi);
                        restartApi.Dispose();
                    }
                    await DisposeP711BackendAsync(
                        restartBackend,
                        $"{caseId} restart backend");
                }

                var finalBinding = terminalBinding ??
                    throw new InvalidOperationException(
                        $"{caseId} did not load its terminal binding.");
                var finalCounts = terminalCounts ??
                    throw new InvalidOperationException(
                        $"{caseId} did not load its terminal counts.");
                mongoEvidence.Add(new
                {
                    caseId,
                    commandId,
                    pausePoint,
                    durableBeforeRestart,
                    markerReached = File.Exists(reached),
                    killedProcessRequest = interruptedRequest,
                    targetReportId =
                        crashPrepared.Scenario.TargetReportId,
                    transactionBaseline = baselineGauge,
                    transactionAtPause = pausedGauge,
                    orphanTransactionRecovery = orphanRecovery,
                    beforeHash = baselineHash,
                    afterCrashHash,
                    beforeCounts = baselineCounts,
                    afterCrashCounts,
                    committedReceiptId,
                    committedOutboxId,
                    committedReceiptState,
                    committedOutboxState,
                    restartApplyStatus =
                        (int)(restartApply ??
                            throw new InvalidOperationException(
                                $"{caseId} lacks restart response."))
                            .StatusCode,
                    terminalReplayStatus =
                        (int)(terminalReplay ??
                            throw new InvalidOperationException(
                                $"{caseId} lacks terminal replay response."))
                            .StatusCode,
                    terminalReceiptId =
                        finalBinding.Receipt.Id,
                    terminalOutboxId =
                        finalBinding.Outbox.Id,
                    terminalReceiptState =
                        finalBinding.Receipt.State,
                    terminalOutboxState =
                        finalBinding.Outbox.State,
                    beforeTerminalReplayHash,
                    afterTerminalReplayHash,
                    finalCounts,
                    duplicateCount = 0,
                    orphanCount = 0,
                    partialWriteSetCount = 0,
                    outstandingLeaseCount = 0
                });
                return new CaseObservation(
                    durableBeforeRestart
                        ? "Process death after the Mongo commit preserved one pending intent; restart replay reconciled those exact identities and terminal replay was zero-write."
                        : "Process death with the Mongo transaction open exposed zero durable writes; orphan recovery completed and restart created one reconciled write set.",
                    P7MappingFingerprint(
                        caseId,
                        pausePoint,
                        durableBeforeRestart
                            ? "CRASH-STATE=COMMITTED-PENDING"
                            : "CRASH-STATE=ZERO-WRITE",
                        "RESTART=RECONCILED",
                        "REPLAY=ZERO-WRITE",
                        "DUPLICATE=0",
                        "ORPHAN=0",
                        "PARTIAL=0"));
            });
    }

    private static async Task DisposeP711BackendAsync(
        BackendServerLease? backend,
        string context)
    {
        if (backend is null)
            return;
        try
        {
            await backend.StopAsync();
        }
        finally
        {
            await backend.DisposeAsync();
        }
        Require(
            backend.StopVerified &&
            backend.PortReleaseVerified,
            $"{context} cleanup failed.");
    }

    private static void CaptureP711ApiExchanges(
        List<ApiExchangeEvidence> target,
        ApiHarnessClient source)
    {
        foreach (var exchange in source.Exchanges)
        {
            target.Add(exchange with
            {
                Sequence = target.Count + 1
            });
        }
    }

    private sealed record P711EpochApplied(
        string WorkId,
        string FlowInstanceId,
        P709EpochTarget Target,
        P709BindingBundle Binding);

    private sealed record P711ApplyFaultCase(
        string CaseId,
        string CommandId,
        string Point,
        bool DurableBeforeRetry);
}

