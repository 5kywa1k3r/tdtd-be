using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Capabilities;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Bounded P5-03 executable evidence. This mode uses the same real replica-set
/// and Kestrel leases as the main integration harness, but owns a deliberately
/// small fixture focused on durable launch intent and outbox convergence.
/// </summary>
internal static partial class P5MaterializationProbe
{
    private const string CoreFaultCommand = "p5-materialization-core-after-intent";
    private const string BranchFaultCommand = "p5-materialization-branch2-after-assignment";
    private const string NormalT01Command = "p5-materialization-t01-success";
    private const string NormalT02Command = "p5-materialization-t02-success";
    private const string HistoricalV11CatalogVersion = "1.1";
    private const string HistoricalV11CatalogSemanticHash =
        "e8a0b15bb5c7cab81ed49ec5c213105366a1194faa2d78168a46226d9cc505cf";
    private const string StateProjectionFaultCommand = "p5-state-submit-faults";
    private const string MultiUnitCommand = "p5-materialization-three-unit-dedupe";
    private const string ConcurrentCommand = "p5-materialization-concurrent-one-winner";
    private const string SideFaultGroupACommand = "p5-materialization-side-fault-group-a";
    private const string SideFaultGroupBCommand = "p5-materialization-side-fault-group-b";
    private const string SideFaultGroupCCommand = "p5-materialization-side-fault-group-c";
    private const string AggregateRecoveryFaultCommand =
        "p5-materialization-aggregate-recovery-faults";
    private const string ReconcileFaultCommand = "p5-materialization-reconcile-faults";
    private const string CompensationFaultCommand = "p5-materialization-compensation-faults";

    private static readonly string[] CoreFaultPoints =
    [
        DynamicFlowRuntimeFaultPoints.BeforeIntentTransaction,
        DynamicFlowRuntimeFaultPoints.BeforeReceiptWrite,
        DynamicFlowRuntimeFaultPoints.AfterReceiptWrite,
        DynamicFlowRuntimeFaultPoints.BeforeInstanceWrite,
        DynamicFlowRuntimeFaultPoints.AfterInstanceWrite,
        DynamicFlowRuntimeFaultPoints.BeforeSnapshotWrite,
        DynamicFlowRuntimeFaultPoints.AfterSnapshotWrite,
        DynamicFlowRuntimeFaultPoints.BeforeStepWrite,
        DynamicFlowRuntimeFaultPoints.AfterStepWrite,
        DynamicFlowRuntimeFaultPoints.BeforeBindingClaimWrite,
        DynamicFlowRuntimeFaultPoints.AfterBindingClaimWrite,
        DynamicFlowRuntimeFaultPoints.BeforeEventWrite,
        DynamicFlowRuntimeFaultPoints.AfterEventWrite,
        DynamicFlowRuntimeFaultPoints.BeforeOutboxWrite,
        DynamicFlowRuntimeFaultPoints.AfterOutboxWrite,
        DynamicFlowRuntimeFaultPoints.AfterIntentCommit
    ];

    private static readonly string[] StateProjectionFaultPoints =
    [
        DynamicFlowRuntimeStateProjectionFaultPoints.BeforeTransaction,
        DynamicFlowRuntimeStateProjectionFaultPoints.BeforeReceiptWrite,
        DynamicFlowRuntimeStateProjectionFaultPoints.AfterReceiptWrite,
        DynamicFlowRuntimeStateProjectionFaultPoints.BeforeStepWrite,
        DynamicFlowRuntimeStateProjectionFaultPoints.AfterStepWrite,
        DynamicFlowRuntimeStateProjectionFaultPoints.BeforeInstanceWrite,
        DynamicFlowRuntimeStateProjectionFaultPoints.AfterInstanceWrite,
        DynamicFlowRuntimeStateProjectionFaultPoints.BeforeEventWrite,
        DynamicFlowRuntimeStateProjectionFaultPoints.AfterEventWrite,
        DynamicFlowRuntimeStateProjectionFaultPoints.AfterTransactionCommit
    ];

    private static readonly string[] BranchRecoveryFaultPoints =
    [
        DynamicFlowRuntimeFaultPoints.AfterAssignmentWrite,
        DynamicFlowRuntimeFaultPoints.AfterDocRoleProjection,
        DynamicFlowRuntimeFaultPoints.AfterQueueWrite
    ];

    private static readonly string[] AggregateRecoveryFaultPoints =
    [
        DynamicFlowRuntimeFaultPoints.BeforeRetryStateWrite,
        DynamicFlowRuntimeFaultPoints.AfterRetryStateWrite,
        DynamicFlowRuntimeFaultPoints.BeforeRecoveryEventWrite,
        DynamicFlowRuntimeFaultPoints.AfterRecoveryEventWrite,
        DynamicFlowRuntimeFaultPoints.AfterBranchEventWrite,
        DynamicFlowRuntimeFaultPoints.AfterOutboxComplete,
        DynamicFlowRuntimeFaultPoints.BeforeInstanceFinalizeWrite,
        DynamicFlowRuntimeFaultPoints.AfterInstanceFinalizeWrite,
        DynamicFlowRuntimeFaultPoints.BeforeReceiptFinalizeWrite,
        DynamicFlowRuntimeFaultPoints.AfterReceiptFinalizeWrite
    ];

    private static readonly string[] SideFaultGroupAPoints =
    [
        DynamicFlowRuntimeFaultPoints.BeforeAssignmentWrite,
        DynamicFlowRuntimeFaultPoints.BeforeBindingWrite,
        DynamicFlowRuntimeFaultPoints.AfterBindingWrite,
        DynamicFlowRuntimeFaultPoints.BeforeDocRoleProjection
    ];

    private static readonly string[] SideFaultGroupBPoints =
    [
        DynamicFlowRuntimeFaultPoints.BeforePeriodWrite,
        DynamicFlowRuntimeFaultPoints.AfterPeriodWrite,
        DynamicFlowRuntimeFaultPoints.BeforeQueueWrite,
        DynamicFlowRuntimeFaultPoints.BeforeOutboxComplete
    ];

    private static readonly string[] SideFaultGroupCPoints =
    [
        DynamicFlowRuntimeFaultPoints.BeforeStepCompleteWrite,
        DynamicFlowRuntimeFaultPoints.AfterStepCompleteWrite,
        DynamicFlowRuntimeFaultPoints.BeforeBranchEventWrite
    ];

    private static readonly string[] ReconcileFaultPoints =
    [
        DynamicFlowRuntimeFaultPoints.BeforeLedgerRepairWrite,
        DynamicFlowRuntimeFaultPoints.AfterLedgerRepairWrite
    ];

    private static readonly string[] CompensationFaultPoints =
    [
        DynamicFlowRuntimeFaultPoints.AfterAssignmentWrite,
        DynamicFlowRuntimeFaultPoints.BeforeCompensationWrite,
        DynamicFlowRuntimeFaultPoints.AfterCompensationWrite,
        DynamicFlowRuntimeFaultPoints.BeforeCompensationProjection,
        DynamicFlowRuntimeFaultPoints.AfterCompensationProjection
    ];

    public static Task<int> RunAsync()
        => RunAsync(null);

    internal static async Task<int> RunAsync(P5MaterializationProbeOptions? options)
    {
        var runKey = options?.RunKey ??
                     $"p5mat_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}";
        var paths = options?.Paths ?? HarnessPaths.Create(runKey);
        var iteration = options?.Iteration ?? 1;
        var deliberateFailure = options?.DeliberateFailure ?? false;
        var iterationRoot = paths.IterationRoot(iteration);
        var cleanupErrors = new List<string>();
        var exchanges = new List<object>();
        var cases = new List<object>();
        var faultExecutions = new List<FaultExecutionObservation>();
        FaultExecutionObservation? stateProjectionFaultExecution = null;
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        ProbeFixture? seededFixture = null;
        P507SupplementalObservation? p507Observation = null;
        IReadOnlyList<HarnessCaseResult>? p507GateCases = null;
        string? failure = null;
        var passed = false;

        try
        {
            mongo = await MongoReplicaSetLease.StartAsync(
                paths,
                iterationRoot,
                runKey,
                iteration,
                CancellationToken.None);
            var database = mongo.Client.GetDatabase(mongo.DatabaseName);

            var coreServerRoot = Path.Combine(iterationRoot, "backend-core");
            Directory.CreateDirectory(coreServerRoot);
            backend = await BackendServerLease.StartAsync(
                paths,
                coreServerRoot,
                runKey,
                mongo,
                CancellationToken.None,
                new BackendServerOptions
                {
                    EnableDynamicFlowRuntimeCandidate = true,
                    DynamicFlowRuntimeFaultCommandId = CoreFaultCommand,
                    DynamicFlowRuntimeFaultPoints = CoreFaultPoints,
                    DynamicFlowRuntimeStateProjectionFaultCommandId =
                        StateProjectionFaultCommand,
                    DynamicFlowRuntimeStateProjectionFaultPoints =
                        StateProjectionFaultPoints
                });

            string adminPassword;
            string adminToken;
            ProbeFixture fixture;
            using (var api = new ApiHarnessClient(backend.BaseUri))
            {
                (adminToken, adminPassword) = await BootstrapAndLoginAsync(
                    api,
                    backend,
                    database,
                    CancellationToken.None);
                fixture = await SeedFixtureAsync(database, CancellationToken.None);
                seededFixture = fixture;

                var t01Targets = fixture.TargetUnitIds.Take(1).ToArray();
                var t01 = await LaunchAsync(
                    api,
                    adminToken,
                    fixture,
                    fixture.T01WorkId,
                    fixture.T01VersionId,
                    NormalT01Command,
                    t01Targets,
                    "2026-07-T01",
                    CancellationToken.None);
                RequireStatus(t01.Confirm, "SUCCEEDED", "FLOW-T01 launch");
                var t01Snapshot = await ValidateConvergedAsync(
                    database,
                    fixture,
                    fixture.T01WorkId,
                    NormalT01Command,
                    fixture.T01VersionId,
                    "FLOW-T01",
                    t01Targets,
                    "2026-07-T01",
                    false,
                    CancellationToken.None);
                var liveMetadataMutation = await MutateLiveParticipantMetadataAsync(
                    database,
                    t01Targets.Single(),
                    CancellationToken.None);
                var replay = await ConfirmAsync(
                    api,
                    adminToken,
                    fixture.T01WorkId,
                    fixture.T01VersionId,
                    NormalT01Command,
                    t01Targets,
                    "2026-07-T01",
                    t01.SnapshotToken,
                    CancellationToken.None);
                RequireStatus(replay, "SUCCEEDED", "FLOW-T01 exact replay");
                Require(
                    ApiHarnessClient.RequiredString(replay.Json, "flowInstanceId") == t01Snapshot.FlowInstanceId,
                    "Exact replay returned a different FLOW-T01 instance.");
                var t01ReplaySnapshot = await ValidateConvergedAsync(
                    database,
                    fixture,
                    fixture.T01WorkId,
                    NormalT01Command,
                    fixture.T01VersionId,
                    "FLOW-T01",
                    t01Targets,
                    "2026-07-T01",
                    false,
                    CancellationToken.None);
                Require(
                    t01Snapshot == t01ReplaySnapshot,
                    "Exact replay changed scoped durable/materialized counts.");
                var durableReplayObservation = await ValidateDurableParticipantReplayAsync(
                    database,
                    t01Snapshot.FlowInstanceId,
                    liveMetadataMutation,
                    CancellationToken.None);
                cases.Add(new
                {
                    caseId = "ASN-FLOW-01+05-T01-ONE-UNIT-EXACT-REPLAY",
                    verdict = "PASS",
                    observation = t01Snapshot,
                    liveMetadataMutation,
                    durableReplayObservation
                });

                var stateProjection = await RunStateProjectionScenarioAsync(
                    api,
                    adminToken,
                    backend,
                    database,
                    fixture,
                    CancellationToken.None);
                cases.Add(new
                {
                    caseId = "ASN-FLOW-07+10-STATE-PROJECTION",
                    verdict = "EXPECTED_FAULT_RECOVERED",
                    stateProjection
                });

                var t02Targets = fixture.TargetUnitIds.Skip(1).Take(1).ToArray();
                var t02 = await LaunchAsync(
                    api,
                    adminToken,
                    fixture,
                    fixture.T02WorkId,
                    fixture.T02VersionId,
                    NormalT02Command,
                    t02Targets,
                    "2026-07-T02",
                    CancellationToken.None);
                RequireStatus(t02.Confirm, "SUCCEEDED", "FLOW-T02 launch");
                var t02Snapshot = await ValidateConvergedAsync(
                    database,
                    fixture,
                    fixture.T02WorkId,
                    NormalT02Command,
                    fixture.T02VersionId,
                    "FLOW-T02",
                    t02Targets,
                    "2026-07-T02",
                    false,
                    CancellationToken.None);
                var t02Instance = await database
                    .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
                    .Find(x =>
                        x.Id == t02Snapshot.FlowInstanceId &&
                        !x.IsDeleted)
                    .SingleAsync(CancellationToken.None);
                var t02Steps = await database
                    .GetCollection<DynamicFlowStepInstance>(
                        "dynamic_flow_step_instances")
                    .Find(x =>
                        x.FlowInstanceId == t02Snapshot.FlowInstanceId &&
                        !x.IsDeleted)
                    .ToListAsync(CancellationToken.None);
                Require(
                    t02Snapshot.InstanceState == DynamicFlowInstanceStates.Active &&
                    t02Instance.State == DynamicFlowInstanceStates.Active &&
                    !t02Instance.CompletedAtUtc.HasValue &&
                    t02Steps.Count > 0 &&
                    t02Steps.All(step =>
                        step.State != DynamicFlowStepStates.Completed),
                    "FLOW-T02 crossed the P5 boundary into internal instance completion.");
                cases.Add(new
                {
                    caseId = "P5-MAT-02-T02-BOUNDARY",
                    verdict = "PASS",
                    observation = t02Snapshot,
                    internalCompletionTriggered = false,
                    instanceState = t02Instance.State,
                    completedStepCount = t02Steps.Count(step =>
                        step.State == DynamicFlowStepStates.Completed)
                });

                var deduplicatedTargets = new[]
                {
                    fixture.TargetUnitIds[2],
                    fixture.TargetUnitIds[0],
                    fixture.TargetUnitIds[1],
                    fixture.TargetUnitIds[0],
                    fixture.TargetUnitIds[2]
                };
                var multi = await LaunchAsync(
                    api,
                    adminToken,
                    fixture,
                    fixture.MultiUnitWorkId,
                    fixture.T01VersionId,
                    MultiUnitCommand,
                    deduplicatedTargets,
                    "2026-07-MULTI",
                    CancellationToken.None);
                RequireStatus(multi.Confirm, "SUCCEEDED", "FLOW-T01 three-unit dedupe");
                var multiSnapshot = await ValidateConvergedAsync(
                    database,
                    fixture,
                    fixture.MultiUnitWorkId,
                    MultiUnitCommand,
                    fixture.T01VersionId,
                    "FLOW-T01",
                    fixture.TargetUnitIds,
                    "2026-07-MULTI",
                    false,
                    CancellationToken.None);
                cases.Add(new
                {
                    caseId = "ASN-FLOW-02+03-THREE-BRANCH-TARGET-DEDUPE",
                    verdict = "PASS",
                    inputTargetCount = deduplicatedTargets.Length,
                    exactTargetCount = fixture.TargetUnitIds.Count,
                    observation = multiSnapshot
                });

                var runtimeRead = await RunRuntimeReadScenarioAsync(
                    backend.BaseUri,
                    adminToken,
                    backend.ActorPassword,
                    database,
                    fixture,
                    multiSnapshot.FlowInstanceId,
                    iterationRoot,
                    CancellationToken.None);
                cases.Add(runtimeRead.CaseEvidence);
                exchanges.Add(runtimeRead.ExchangeEvidence);

                var concurrentTargets = fixture.TargetUnitIds.Take(1).ToArray();
                var concurrentPreflight = await PreflightAsync(
                    api,
                    adminToken,
                    fixture.ConcurrentWorkId,
                    fixture.T01VersionId,
                    ConcurrentCommand,
                    concurrentTargets,
                    "2026-07-CONCURRENT",
                    CancellationToken.None);
                using var concurrentApiA = new ApiHarnessClient(backend.BaseUri);
                using var concurrentApiB = new ApiHarnessClient(backend.BaseUri);
                var concurrentResponses = await Task.WhenAll(
                    ConfirmAsync(
                        concurrentApiA,
                        adminToken,
                        fixture.ConcurrentWorkId,
                        fixture.T01VersionId,
                        ConcurrentCommand,
                        concurrentTargets,
                        "2026-07-CONCURRENT",
                        concurrentPreflight.SnapshotToken,
                        CancellationToken.None),
                    ConfirmAsync(
                        concurrentApiB,
                        adminToken,
                        fixture.ConcurrentWorkId,
                        fixture.T01VersionId,
                        ConcurrentCommand,
                        concurrentTargets,
                        "2026-07-CONCURRENT",
                        concurrentPreflight.SnapshotToken,
                        CancellationToken.None));
                foreach (var (response, index) in concurrentResponses.Select(
                             (response, index) => (response, index)))
                {
                    ApiHarnessClient.ExpectStatus(
                        response,
                        HttpStatusCode.OK,
                        $"concurrent confirm {index + 1}");
                    var status = ApiHarnessClient.RequiredString(response.Json, "status");
                    Require(
                        status is "SUCCEEDED" or "MATERIALIZING" or "RECOVERY_REQUIRED",
                        $"Concurrent confirm {index + 1} returned unexpected status {status}.");
                }
                var concurrentInstanceIds = concurrentResponses
                    .Select(response => ApiHarnessClient.RequiredString(
                        response.Json,
                        "flowInstanceId"))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                Require(
                    concurrentInstanceIds.Length == 1,
                    "Concurrent exact confirms returned multiple flow instances.");
                var concurrentTerminal = await ConfirmAsync(
                    api,
                    adminToken,
                    fixture.ConcurrentWorkId,
                    fixture.T01VersionId,
                    ConcurrentCommand,
                    concurrentTargets,
                    "2026-07-CONCURRENT",
                    concurrentPreflight.SnapshotToken,
                    CancellationToken.None);
                RequireStatus(
                    concurrentTerminal,
                    "SUCCEEDED",
                    "concurrent exact terminal replay");
                var terminalReplays = await Task.WhenAll(
                    ConfirmAsync(
                        concurrentApiA,
                        adminToken,
                        fixture.ConcurrentWorkId,
                        fixture.T01VersionId,
                        ConcurrentCommand,
                        concurrentTargets,
                        "2026-07-CONCURRENT",
                        concurrentPreflight.SnapshotToken,
                        CancellationToken.None),
                    ConfirmAsync(
                        concurrentApiB,
                        adminToken,
                        fixture.ConcurrentWorkId,
                        fixture.T01VersionId,
                        ConcurrentCommand,
                        concurrentTargets,
                        "2026-07-CONCURRENT",
                        concurrentPreflight.SnapshotToken,
                        CancellationToken.None));
                RequireStatus(terminalReplays[0], "SUCCEEDED", "concurrent terminal replay A");
                RequireStatus(terminalReplays[1], "SUCCEEDED", "concurrent terminal replay B");
                Require(
                    terminalReplays.All(response =>
                        ApiHarnessClient.RequiredString(response.Json, "flowInstanceId") ==
                        concurrentInstanceIds[0]),
                    "Concurrent terminal replays changed flow instance.");
                var concurrentSnapshot = await ValidateConvergedAsync(
                    database,
                    fixture,
                    fixture.ConcurrentWorkId,
                    ConcurrentCommand,
                    fixture.T01VersionId,
                    "FLOW-T01",
                    concurrentTargets,
                    "2026-07-CONCURRENT",
                    false,
                    CancellationToken.None);
                cases.Add(new
                {
                    caseId = "ASN-FLOW-05-CONCURRENT-ONE-WINNER",
                    verdict = "PASS",
                    initialStatuses = concurrentResponses.Select(OptionalStatus).ToArray(),
                    terminalReplayCount = terminalReplays.Length,
                    distinctInstanceCount = concurrentInstanceIds.Length,
                    observation = concurrentSnapshot
                });
                exchanges.Add(new
                {
                    phase = "concurrent-one-winner",
                    clientA = concurrentApiA.Exchanges,
                    clientB = concurrentApiB.Exchanges
                });

                p507Observation = await RunP507SupplementalGateAsync(
                    api,
                    adminToken,
                    backend.ActorPassword,
                    database,
                    fixture,
                    t01Snapshot.FlowInstanceId,
                    CancellationToken.None);
                cases.Add(p507Observation.CaseEvidence);

                var corePreflight = await PreflightAsync(
                    api,
                    adminToken,
                    fixture.CoreWorkId,
                    fixture.T01VersionId,
                    CoreFaultCommand,
                    fixture.TargetUnitIds,
                    "2026-07-CORE",
                    CancellationToken.None);
                var coreFaultAttempts = new List<FaultAttemptObservation>();
                var expectedCoreFaultExecutions =
                    10 + (6 * fixture.TargetUnitIds.Count);
                for (var attempt = 1; attempt <= expectedCoreFaultExecutions; attempt++)
                {
                    var coreFault = await ConfirmAsync(
                        api,
                        adminToken,
                        fixture.CoreWorkId,
                        fixture.T01VersionId,
                        CoreFaultCommand,
                        fixture.TargetUnitIds,
                        "2026-07-CORE",
                        corePreflight.SnapshotToken,
                        CancellationToken.None);
                    Require(
                        (int)coreFault.StatusCode is >= 500 and < 600,
                        $"Core boundary fault attempt {attempt} expected HTTP 5xx, got {(int)coreFault.StatusCode}.");
                    var receiptCount = await database
                        .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                            "dynamic_flow_runtime_command_receipts")
                        .CountDocumentsAsync(
                            x => x.CommandId == CoreFaultCommand,
                            cancellationToken: CancellationToken.None);
                    var durable = receiptCount == 1;
                    coreFaultAttempts.Add(new FaultAttemptObservation(
                        attempt,
                        (int)coreFault.StatusCode,
                        null,
                        durable));
                    if (attempt < expectedCoreFaultExecutions)
                    {
                        Require(!durable, "Core transaction became durable before AFTER_INTENT_COMMIT.");
                        await ValidateRolledBackIntentAsync(
                            database,
                            fixture.CoreWorkId,
                            CoreFaultCommand,
                            CancellationToken.None);
                    }
                    else
                    {
                        Require(durable, "AFTER_INTENT_COMMIT fault did not leave durable core intent.");
                    }
                }
                var durableIntent = await ValidateDurableIntentBeforeMaterializationAsync(
                    database,
                    fixture.CoreWorkId,
                    CoreFaultCommand,
                    fixture.TargetUnitIds.Count,
                    CancellationToken.None);
                var coreRecovered = await ConfirmAsync(
                    api,
                    adminToken,
                    fixture.CoreWorkId,
                    fixture.T01VersionId,
                    CoreFaultCommand,
                    fixture.TargetUnitIds,
                    "2026-07-CORE",
                    corePreflight.SnapshotToken,
                    CancellationToken.None);
                RequireStatus(coreRecovered, "SUCCEEDED", "after-intent exact retry");
                var coreConverged = await ValidateConvergedAsync(
                    database,
                    fixture,
                    fixture.CoreWorkId,
                    CoreFaultCommand,
                    fixture.T01VersionId,
                    "FLOW-T01",
                    fixture.TargetUnitIds,
                    "2026-07-CORE",
                    false,
                    CancellationToken.None);
                cases.Add(new
                {
                    caseId = "P5-MAT-03-CORE-TRANSACTION-ALL-BOUNDARIES",
                    verdict = "EXPECTED_FAULT_RECOVERED",
                    configuredBoundaryCount = CoreFaultPoints.Length,
                    injectedExecutionCount = coreFaultAttempts.Count,
                    attempts = coreFaultAttempts,
                    durableIntent,
                    recovered = coreConverged
                });

                exchanges.Add(new { phase = "core-and-boundary", api.Exchanges });
            }

            await backend.StopAsync();
            faultExecutions.Add(CaptureFaultExecution(
                "core-transaction-and-after-commit",
                CoreFaultCommand,
                CoreFaultPoints,
                backend,
                expectedExecutions: 10 + (6 * fixture.TargetUnitIds.Count)));
            stateProjectionFaultExecution = CaptureFaultExecution(
                "report-lifecycle-state-projection",
                StateProjectionFaultCommand,
                StateProjectionFaultPoints,
                backend,
                expectedExecutions: StateProjectionFaultPoints.Length,
                failureMessage:
                    DynamicFlowRuntimeStateProjectionFaultInjector.FailureMessage);
            await backend.DisposeAsync();
            backend = null;

            var branchServerRoot = Path.Combine(iterationRoot, "backend-branch");
            Directory.CreateDirectory(branchServerRoot);
            backend = await BackendServerLease.StartAsync(
                paths,
                branchServerRoot,
                runKey,
                mongo,
                CancellationToken.None,
                new BackendServerOptions
                {
                    EnableDynamicFlowRuntimeCandidate = true,
                    DynamicFlowRuntimeFaultCommandId = BranchFaultCommand,
                    DynamicFlowRuntimeFaultBranchOrdinal = 2,
                    DynamicFlowRuntimeFaultPoints = BranchRecoveryFaultPoints
                });
            // The release backfill intentionally still understands the frozen
            // 1.1 catalog. Re-assert the test-only 1.2 candidate pins after the
            // second startup so this fixture remains equivalent to one seeded
            // after startup, while preserving all runtime records across the
            // process restart.
            await RestoreCandidateDefinitionAsync(
                database,
                fixture.T01VersionId,
                CancellationToken.None);
            using (var api = new ApiHarnessClient(backend.BaseUri))
            {
                adminToken = await api.LoginAsync("admin", adminPassword, CancellationToken.None);
                var branchPreflight = await PreflightAsync(
                    api,
                    adminToken,
                    fixture.BranchWorkId,
                    fixture.T01VersionId,
                    BranchFaultCommand,
                    fixture.TargetUnitIds,
                    "2026-07-BRANCH",
                    CancellationToken.None);
                var partial = await ConfirmAsync(
                    api,
                    adminToken,
                    fixture.BranchWorkId,
                    fixture.T01VersionId,
                    BranchFaultCommand,
                    fixture.TargetUnitIds,
                    "2026-07-BRANCH",
                    branchPreflight.SnapshotToken,
                    CancellationToken.None);
                RequireStatus(partial, "RECOVERY_REQUIRED", "branch-2 injected fault");
                var partialSnapshot = await ValidateBranchPartialAsync(
                    database,
                    BranchFaultCommand,
                    fixture.TargetUnitIds.Count,
                    CancellationToken.None);
                var recoveryAttempts = new List<FaultAttemptObservation>
                {
                    new(
                        1,
                        (int)partial.StatusCode,
                        ApiHarnessClient.RequiredString(partial.Json, "status"),
                        true)
                };
                ApiHarnessResponse? recovered = null;
                for (var attempt = 2; attempt <= 32; attempt++)
                {
                    var response = await ConfirmAsync(
                        api,
                        adminToken,
                        fixture.BranchWorkId,
                        fixture.T01VersionId,
                        BranchFaultCommand,
                        fixture.TargetUnitIds,
                        "2026-07-BRANCH",
                        branchPreflight.SnapshotToken,
                        CancellationToken.None);
                    var status = OptionalStatus(response);
                    recoveryAttempts.Add(new FaultAttemptObservation(
                        attempt,
                        (int)response.StatusCode,
                        status,
                        true));
                    if (response.StatusCode == HttpStatusCode.OK &&
                        status == "SUCCEEDED")
                    {
                        recovered = response;
                        break;
                    }

                    Require(
                        (int)response.StatusCode is >= 500 and < 600 ||
                        (response.StatusCode == HttpStatusCode.OK &&
                         status is "RECOVERY_REQUIRED" or "MATERIALIZING"),
                        $"Branch recovery attempt {attempt} returned unexpected HTTP/status {(int)response.StatusCode}/{status}.");
                }
                Require(recovered is not null, "Branch-2 recovery matrix did not converge.");
                RequireStatus(recovered, "SUCCEEDED", "branch-2 exact retry");
                var branchConverged = await ValidateConvergedAsync(
                    database,
                    fixture,
                    fixture.BranchWorkId,
                    BranchFaultCommand,
                    fixture.T01VersionId,
                    "FLOW-T01",
                    fixture.TargetUnitIds,
                    "2026-07-BRANCH",
                    true,
                    CancellationToken.None);
                cases.Add(new
                {
                    caseId = "ASN-FLOW-04-BRANCH2-RECOVERY-MATRIX",
                    verdict = "EXPECTED_FAULT_RECOVERED",
                    configuredBoundaryCount = BranchRecoveryFaultPoints.Length,
                    attempts = recoveryAttempts,
                    partial = partialSnapshot,
                    recovered = branchConverged
                });
                exchanges.Add(new { phase = "branch-recovery", api.Exchanges });
            }

            await backend.StopAsync();
            faultExecutions.Add(CaptureFaultExecution(
                "branch2-recovery-and-finalization",
                BranchFaultCommand,
                BranchRecoveryFaultPoints,
                backend));
            await backend.DisposeAsync();
            backend = null;

            var sideGroupA = await RunSideFaultGroupAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                database,
                adminPassword,
                fixture,
                "side-group-a",
                fixture.SideFaultGroupAWorkId,
                SideFaultGroupACommand,
                SideFaultGroupAPoints,
                "2026-07-SIDE-A",
                1,
                true,
                CancellationToken.None);
            faultExecutions.Add(sideGroupA.FaultExecution);
            cases.Add(sideGroupA.CaseEvidence);
            exchanges.Add(sideGroupA.ExchangeEvidence);

            var sideGroupB = await RunSideFaultGroupAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                database,
                adminPassword,
                fixture,
                "side-group-b",
                fixture.SideFaultGroupBWorkId,
                SideFaultGroupBCommand,
                SideFaultGroupBPoints,
                "2026-07-SIDE-B",
                1,
                true,
                CancellationToken.None);
            faultExecutions.Add(sideGroupB.FaultExecution);
            cases.Add(sideGroupB.CaseEvidence);
            exchanges.Add(sideGroupB.ExchangeEvidence);

            var sideGroupC = await RunSideFaultGroupAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                database,
                adminPassword,
                fixture,
                "side-group-c",
                fixture.SideFaultGroupCWorkId,
                SideFaultGroupCCommand,
                SideFaultGroupCPoints,
                "2026-07-SIDE-C",
                1,
                true,
                CancellationToken.None);
            faultExecutions.Add(sideGroupC.FaultExecution);
            cases.Add(sideGroupC.CaseEvidence);
            exchanges.Add(sideGroupC.ExchangeEvidence);

            var aggregateRecoveryGroup = await RunSideFaultGroupAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                database,
                adminPassword,
                fixture,
                "aggregate-recovery",
                fixture.AggregateRecoveryFaultWorkId,
                AggregateRecoveryFaultCommand,
                AggregateRecoveryFaultPoints,
                "2026-07-AGGREGATE-RECOVERY",
                null,
                false,
                CancellationToken.None);
            faultExecutions.Add(aggregateRecoveryGroup.FaultExecution);
            cases.Add(aggregateRecoveryGroup.CaseEvidence);
            exchanges.Add(aggregateRecoveryGroup.ExchangeEvidence);

            var reconcileScenario = await RunReconcileFaultScenarioAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                database,
                adminPassword,
                fixture,
                CancellationToken.None);
            faultExecutions.Add(reconcileScenario.FaultExecution);
            cases.Add(reconcileScenario.CaseEvidence);
            exchanges.Add(reconcileScenario.ExchangeEvidence);

            var compensationScenario = await RunCompensationFaultScenarioAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                database,
                adminPassword,
                fixture,
                CancellationToken.None);
            faultExecutions.Add(compensationScenario.FaultExecution);
            cases.Add(compensationScenario.CaseEvidence);
            exchanges.Add(compensationScenario.ExchangeEvidence);

            var faultMatrix = BuildFaultBoundaryMatrix(faultExecutions);
            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, "p5-materialization-fault-matrix.json"),
                faultMatrix);

            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, "p5-materialization-ledger.json"),
                new
                {
                    runKey,
                    positiveArchetypes = new[] { "FLOW-T01", "FLOW-T02" },
                    forbiddenArchetypesOpened = false,
                    asnCases = new[]
                    {
                        "ASN-FLOW-01",
                        "ASN-FLOW-02",
                        "ASN-FLOW-03",
                        "ASN-FLOW-04",
                        "ASN-FLOW-05",
                        "ASN-FLOW-07",
                        "ASN-FLOW-08",
                        "ASN-FLOW-09",
                        "ASN-FLOW-10"
                    },
                    faultMatrix,
                    stateProjectionFaultExecution,
                    cases
                });
            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, "p5-materialization-api-exchanges.json"),
                new { runKey, phases = exchanges });
            p507GateCases = BuildP507GateCaseRows(
                p507Observation ??
                throw new InvalidOperationException(
                    "P5-07 supplemental gate did not produce observations."),
                seededFixture ??
                throw new InvalidOperationException(
                    "P5-07 fixture manifest is missing."));
            passed = true;
        }
        catch (Exception error)
        {
            failure = $"{error.GetType().Name}: {error.Message}";
            Console.Error.WriteLine(error);
        }
        finally
        {
            if (backend is not null)
            {
                try
                {
                    await backend.StopAsync();
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"backend-stop: {error.Message}");
                }
                await backend.DisposeAsync();
            }

            if (mongo is not null)
            {
                try
                {
                    await mongo.DropDatabaseGuardedAsync(CancellationToken.None);
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"database-drop: {error.Message}");
                }
                try
                {
                    await mongo.StopProcessAsync();
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"mongo-stop: {error.Message}");
                }
                try
                {
                    mongo.RemoveDataDirectoryGuarded();
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"mongo-data-remove: {error.Message}");
                }
                await mongo.DisposeAsync();
            }

            var cleanupSucceeded =
                cleanupErrors.Count == 0 &&
                (mongo is null ||
                 mongo.DatabaseDropVerified &&
                 mongo.ProcessStopVerified &&
                 mongo.PortReleaseVerified &&
                 mongo.DataDirectoryRemovalVerified) &&
                (backend is null ||
                 backend.StopVerified &&
                 backend.PortReleaseVerified);
            passed &= cleanupSucceeded;
            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, "p5-materialization-cleanup.json"),
                new
                {
                    runKey,
                    databaseDropped = mongo?.DatabaseDropVerified ?? false,
                    mongoStopped = mongo?.ProcessStopVerified ?? false,
                    mongoPortReleased = mongo?.PortReleaseVerified ?? false,
                    mongoDataRemoved = mongo?.DataDirectoryRemovalVerified ?? false,
                    cleanupErrors,
                    completedAtUtc = DateTime.UtcNow
                });
            var summaryCases = p507GateCases ??
                               new[]
                               {
                                   new HarnessCaseResult(
                                       "P5-07-INFRASTRUCTURE",
                                       HarnessVerdict.KHONG_DAT,
                                       failure ?? "P5-07 probe stopped before semantic case reconciliation.",
                                       "p507-infrastructure-failure",
                                       0)
                               };
            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, "p5-p507-gate-summary.json"),
                new
                {
                    schemaVersion = 1,
                    gate = "P5-07",
                    runKey,
                    iteration,
                    databaseName = mongo?.DatabaseName ?? string.Empty,
                    replicaSetName = mongo?.ReplicaSetName ?? string.Empty,
                    seedIdentity = seededFixture is null
                        ? string.Empty
                        : BuildP507SeedIdentity(seededFixture),
                    fixtureClockUtc = "2026-07-23T00:00:00.0000000Z",
                    deliberateFailureRequested = deliberateFailure,
                    cases = summaryCases,
                    caseCount = summaryCases.Count,
                    normalizedSha256 = BuildP507NormalizedSha256(summaryCases),
                    cleanupSucceeded,
                    cleanupErrors,
                    passed,
                    failure
                });
            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, "p5-materialization-result.json"),
                new
                {
                    runKey,
                    verdict = passed ? "PASS" : "FAIL",
                    failure,
                    cleanupErrors,
                    artifactRoot = iterationRoot,
                    completedAtUtc = DateTime.UtcNow
                });
        }

        Console.WriteLine(
            passed
                ? $"[PASS] P5 materialization probe passed; injected cases are EXPECTED_FAULT_RECOVERED; artifact={Path.Combine(iterationRoot, "p5-materialization-result.json")}"
                : $"[FAIL] P5 materialization probe failed: {failure ?? string.Join("; ", cleanupErrors)}; artifact={Path.Combine(iterationRoot, "p5-materialization-result.json")}");
        return passed ? 0 : 1;
    }

    private static async Task<(string Token, string Password)> BootstrapAndLoginAsync(
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        CancellationToken ct)
    {
        var bootstrap = await api.PostAsync(
            "api/system/bootstrap",
            new { },
            headers: new Dictionary<string, string>
            {
                ["X-System-Bootstrap-Key"] = backend.BootstrapKey
            },
            ct: ct);
        ApiHarnessClient.ExpectStatus(bootstrap, HttpStatusCode.OK, "P5 system bootstrap");
        var password = ApiHarnessClient.RequiredString(bootstrap.Json, "defaultPassword");
        var token = await api.LoginAsync("admin", password, ct);
        var admin = await database.GetCollection<AppUser>("users")
            .Find(x => x.Username == "admin" && !x.IsDeleted)
            .SingleAsync(ct);
        Require(!string.IsNullOrWhiteSpace(admin.UnitId), "Bootstrap admin must belong to the root unit.");
        return (token, password);
    }

    private static async Task<ProbeFixture> SeedFixtureAsync(
        IMongoDatabase database,
        CancellationToken ct)
    {
        var users = database.GetCollection<AppUser>("users");
        var units = database.GetCollection<Unit>("units");
        var admin = await users.Find(x => x.Username == "admin" && !x.IsDeleted).SingleAsync(ct);
        var root = await units.Find(x => x.Id == admin.UnitId && !x.IsDeleted).SingleAsync(ct);
        var now = new DateTime(2026, 7, 23, 0, 0, 0, DateTimeKind.Utc);
        var unitIds = new[]
        {
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString()
        }.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var targetUnits = unitIds.Select((id, index) => new Unit
        {
            Id = id,
            FullName = $"P5 Materialization Unit {index + 1}",
            ShortName = $"P5M{index + 1}",
            Symbol = $"P5M{index + 1}",
            Code = $"100P5M{index + 1}",
            Level = root.Level + 1,
            Version = 1,
            ParentUnitId = root.Id,
            UnitTypeCodes = [],
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = admin.Id,
            UpdatedByUserId = admin.Id,
            IsDeleted = false
        }).ToArray();
        await units.InsertManyAsync(targetUnits, cancellationToken: ct);

        var participants = targetUnits.Select((unit, index) => new AppUser
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Username = $"p5_materialization_user_{index + 1}",
            PasswordHash = "P5_PROBE_NOT_USED",
            FullName = $"P5 Materialization User {index + 1}",
            UnitId = unit.Id,
            PositionCode = "SPECIALIST",
            Roles = [],
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = admin.Id,
            UpdatedByUserId = admin.Id,
            IsDeleted = false
        }).ToArray();
        await users.InsertManyAsync(participants, cancellationToken: ct);

        DynamicFormTemplate NewPublishedForm(string suffix, string fieldKey)
        {
            var optionsJson = suffix switch
            {
                "A" or "B" =>
                    ",\"options\":[{\"code\":\"P5_NOTE\",\"label\":\"P5 note\"}]",
                _ => string.Empty
            };
            var candidate = new DynamicFormTemplate
            {
                Id = ObjectId.GenerateNewId().ToString(),
                Code = $"P5_MATERIALIZATION_FORM_{suffix}",
                Name = $"P5 Materialization Form {suffix}",
                CreatedByUsername = admin.Username,
                SchemaVersion = 1,
                VersionNo = 1,
                Revision = 1,
                LineageStatus = DynamicFormLineageStatuses.Root,
                IsActive = true,
                IsPublished = true,
                SectionsJson =
                    $"[{{\"id\":\"section_{suffix.ToLowerInvariant()}\",\"title\":\"{suffix}\",\"order\":1}}]",
                FieldsJson =
                    $"[{{\"id\":\"field_{fieldKey}\",\"sectionId\":\"section_{suffix.ToLowerInvariant()}\",\"key\":\"{fieldKey}\",\"name\":\"{suffix} value\",\"type\":\"shortText\",\"required\":false,\"order\":1{optionsJson}}}]",
                BlocksJson = "[]",
                PublishedAtUtc = now,
                PublishedByUserId = admin.Id,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = admin.Id,
                UpdatedByUserId = admin.Id,
                IsDeleted = false
            };
            var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(candidate);
            candidate.PublishedSchemaSnapshotJson = snapshot.Json;
            candidate.PublishedSchemaHash = snapshot.Sha256;
            return candidate;
        }

        var formA = NewPublishedForm("A", "note");
        var formB = NewPublishedForm("B", "child_value");
        var formC = NewPublishedForm("C", "review_value");
        var formPins = new[]
        {
            new ProbeFormPin("root_form", "ROOT", formA, formA.PublishedSchemaHash!),
            new ProbeFormPin("child_form", "CHILD", formB, formB.PublishedSchemaHash!),
            new ProbeFormPin("review_form", "REVIEWER", formC, formC.PublishedSchemaHash!)
        };
        await database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
            .InsertManyAsync(formPins.Select(pin => pin.Form), cancellationToken: ct);

        var t01 = BuildFlow("FLOW-T01", formPins, admin.Id, now);
        var t02 = BuildFlow("FLOW-T02", formPins, admin.Id, now);
        var blockedFlows = Enumerable.Range(3, 10)
            .Select(number => BuildFlow(
                $"FLOW-T{number:00}",
                formPins,
                admin.Id,
                now))
            .ToArray();
        var lockedV11 = BuildFlow("FLOW-T01", formPins, admin.Id, now);
        lockedV11.Family.Code = "P5_MATERIALIZATION_FLOW_T01_LOCKED_V11";
        lockedV11.Family.Name = "P5 Materialization FLOW-T01 locked v1.1";
        lockedV11.Version.PayloadJson = lockedV11.Version.PayloadJson
            .Replace(
                $"\"catalogVersion\":\"{DynamicFlowRuntimeCatalogCandidate.Version}\"",
                $"\"catalogVersion\":\"{HistoricalV11CatalogVersion}\"",
                StringComparison.Ordinal)
            .Replace(
                $"\"catalogSemanticHash\":\"{DynamicFlowRuntimeCatalogCandidate.SemanticHash}\"",
                $"\"catalogSemanticHash\":\"{HistoricalV11CatalogSemanticHash}\"",
                StringComparison.Ordinal);
        lockedV11.Version.PayloadHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(lockedV11.Version.PayloadJson)))
            .ToLowerInvariant();
        lockedV11.Version.CatalogVersion = HistoricalV11CatalogVersion;
        lockedV11.Version.CatalogSemanticHash = HistoricalV11CatalogSemanticHash;
        lockedV11.Version.BlockedUntilPhase = "P5";
        lockedV11.Family.CurrentVersionHash = lockedV11.Version.PayloadHash;
        await database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .InsertManyAsync(
                new[] { t01.Family, t02.Family, lockedV11.Family }
                    .Concat(blockedFlows.Select(flow => flow.Family)),
                cancellationToken: ct);
        await database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .InsertManyAsync(
                new[] { t01.Version, t02.Version, lockedV11.Version }
                    .Concat(blockedFlows.Select(flow => flow.Version)),
                cancellationToken: ct);

        Work NewWork(string suffix) => new()
        {
            Id = ObjectId.GenerateNewId().ToString(),
            AutoCode = $"P5-MATERIALIZATION-{suffix}",
            Code = $"P5-MAT-{suffix}",
            Name = $"P5 materialization integration work {suffix}",
            Status = WorkStatus.S1,
            Type = WorkType.TASK,
            Priority = WorkPriority.MEDIUM,
            LeaderDirectiveUserId = admin.Id,
            LeaderWatchUserIds = [],
            Owner = new UserRef
            {
                UserId = admin.Id,
                Username = admin.Username,
                FullName = admin.FullName,
                UnitId = admin.UnitId,
                UnitSymbol = root.Symbol,
                UnitShortName = root.ShortName,
                UnitName = root.FullName,
                PositionCode = admin.PositionCode
            },
            LeaderDirective = new UserRef
            {
                UserId = admin.Id,
                Username = admin.Username,
                FullName = admin.FullName,
                UnitId = admin.UnitId,
                UnitSymbol = root.Symbol,
                UnitShortName = root.ShortName,
                UnitName = root.FullName,
                PositionCode = admin.PositionCode
            },
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = admin.Id,
            UpdatedByUserId = admin.Id,
            IsDeleted = false
        };
        var t01Work = NewWork("T01");
        var t02Work = NewWork("T02");
        var stateWork = NewWork("STATE");
        var multiUnitWork = NewWork("MULTI");
        var concurrentWork = NewWork("CONCURRENT");
        var coreWork = NewWork("CORE");
        var branchWork = NewWork("BRANCH");
        var sideFaultGroupAWork = NewWork("SIDE-A");
        var sideFaultGroupBWork = NewWork("SIDE-B");
        var sideFaultGroupCWork = NewWork("SIDE-C");
        var aggregateRecoveryFaultWork = NewWork("AGGREGATE-RECOVERY");
        var reconcileFaultWork = NewWork("RECONCILE");
        var compensationFaultWork = NewWork("COMPENSATION");
        var outsiderWork = NewWork("OUTSIDER-SPOOF");
        var changedReplayWork = NewWork("CHANGED-REPLAY");
        var staleWork = NewWork("STALE");
        var lockedV11Work = NewWork("LOCKED-V11");
        var blockedWorks = Enumerable.Range(3, 10)
            .Select(number => NewWork($"BLOCKED-T{number:00}"))
            .ToArray();
        await database.GetCollection<Work>("works")
            .InsertManyAsync(
                [
                    t01Work,
                    t02Work,
                    stateWork,
                    multiUnitWork,
                    concurrentWork,
                    coreWork,
                    branchWork,
                    sideFaultGroupAWork,
                    sideFaultGroupBWork,
                    sideFaultGroupCWork,
                    aggregateRecoveryFaultWork,
                    reconcileFaultWork,
                    compensationFaultWork,
                    outsiderWork,
                    changedReplayWork,
                    staleWork,
                    lockedV11Work
                ],
                cancellationToken: ct);
        await database.GetCollection<Work>("works")
            .InsertManyAsync(blockedWorks, cancellationToken: ct);

        return new ProbeFixture(
            t01Work.Id,
            t02Work.Id,
            stateWork.Id,
            multiUnitWork.Id,
            concurrentWork.Id,
            coreWork.Id,
            branchWork.Id,
            sideFaultGroupAWork.Id,
            sideFaultGroupBWork.Id,
            sideFaultGroupCWork.Id,
            aggregateRecoveryFaultWork.Id,
            reconcileFaultWork.Id,
            compensationFaultWork.Id,
            outsiderWork.Id,
            changedReplayWork.Id,
            staleWork.Id,
            lockedV11Work.Id,
            lockedV11.Version.Id,
            formA.Id,
            formA.FamilyId ?? formA.Id,
            formA.VersionNo,
            formA.PublishedSchemaHash!,
            t01.Version.Id,
            t02.Version.Id,
            unitIds,
            formPins.Select(pin => new ProbeFormPinSnapshot(
                    pin.FormNodeId,
                    pin.Role,
                    pin.Form.Id,
                    pin.Form.FamilyId ?? pin.Form.Id,
                    Math.Max(1, pin.Form.VersionNo),
                    pin.SchemaHash))
                .ToArray(),
            blockedFlows.Select((flow, index) => new ProbeBarrierFixture(
                    $"FLOW-T{index + 3:00}",
                    blockedWorks[index].Id,
                    flow.Version.Id))
                .ToArray());
    }

    private static (DynamicFlowTemplate Family, DynamicFlowTemplateVersion Version) BuildFlow(
        string archetypeId,
        IReadOnlyList<ProbeFormPin> formPins,
        string adminId,
        DateTime now)
    {
        var rootPin = formPins.Single(pin => pin.Role == "ROOT");
        var form = rootPin.Form;
        var familyId = ObjectId.GenerateNewId().ToString();
        var versionId = ObjectId.GenerateNewId().ToString();
        JsonObject FormStep(string id, string code, string formNodeId)
            => new()
            {
                ["nodeId"] = id,
                ["nodeCode"] = code,
                ["nodeKind"] = "FORM_STEP",
                ["formNodeId"] = formNodeId,
                ["declaredRoles"] = new JsonArray("OWNER")
            };
        JsonObject Gateway(string id, string code, string kind)
            => new()
            {
                ["nodeId"] = id,
                ["nodeCode"] = code,
                ["nodeKind"] = "GATEWAY",
                ["declaredRoles"] = new JsonArray(),
                ["gateway"] = new JsonObject { ["kind"] = kind }
            };
        JsonObject Final(string id = "final")
            => new()
            {
                ["nodeId"] = id,
                ["nodeCode"] = id.ToUpperInvariant(),
                ["nodeKind"] = "FINAL",
                ["declaredRoles"] = new JsonArray()
            };
        JsonObject Edge(string from, string to, int ordinal)
            => new()
            {
                ["transitionId"] = $"tr_{ordinal:00}_{from}_{to}",
                ["fromNodeId"] = from,
                ["toNodeId"] = to
            };

        var nodes = new JsonArray(FormStep("step_root", "ROOT", "root_form"));
        var edges = new JsonArray();
        switch (archetypeId)
        {
            case "FLOW-T01":
            case "FLOW-T02":
            case "FLOW-T11":
                break;
            case "FLOW-T03":
                nodes.Add(FormStep("step_child", "CHILD", "child_form"));
                edges.Add(Edge("step_root", "step_child", 1));
                break;
            case "FLOW-T04":
                nodes.Add(Gateway("fork", "FORK", "FORK"));
                nodes.Add(FormStep("step_child", "CHILD", "child_form"));
                nodes.Add(FormStep("step_review", "REVIEW", "review_form"));
                edges.Add(Edge("step_root", "fork", 1));
                edges.Add(Edge("fork", "step_child", 2));
                edges.Add(Edge("fork", "step_review", 3));
                break;
            case "FLOW-T05":
            case "FLOW-T06":
                nodes.Add(FormStep("step_child", "CHILD", "child_form"));
                nodes.Add(FormStep("step_review", "REVIEW", "review_form"));
                nodes.Add(Gateway(
                    "join",
                    "JOIN",
                    archetypeId == "FLOW-T05" ? "JOIN_ALL" : "JOIN_ANY"));
                nodes.Add(Final());
                edges.Add(Edge("step_root", "step_child", 1));
                edges.Add(Edge("step_root", "step_review", 2));
                edges.Add(Edge("step_child", "join", 3));
                edges.Add(Edge("step_review", "join", 4));
                edges.Add(Edge("join", "final", 5));
                break;
            default:
                var gatewayKind = archetypeId switch
                {
                    "FLOW-T07" => "CONDITION",
                    "FLOW-T08" => "REVIEW",
                    "FLOW-T09" => "SUBFLOW",
                    "FLOW-T10" => "SCHEDULE",
                    "FLOW-T12" => "ROLLBACK_FINALIZE",
                    _ => throw new InvalidOperationException(
                        $"Unsupported P5 probe archetype {archetypeId}.")
                };
                nodes.Add(Gateway("gateway", "GATEWAY", gatewayKind));
                nodes.Add(Final());
                edges.Add(Edge("step_root", "gateway", 1));
                edges.Add(Edge("gateway", "final", 2));
                break;
        }
        var payload = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["archetypeId"] = archetypeId,
            ["entryStepId"] = "step_root",
            ["rootDynamicFormTemplateId"] = form.Id,
            // The frozen P5 probe remains an exact v1.2 historical snapshot
            // after CURRENT advances to v1.3. Runtime rollback/readability
            // depends on these payload and version-level pins staying paired.
            ["catalogVersion"] = DynamicFlowRuntimeCatalogCandidate.Version,
            ["catalogSemanticHash"] = DynamicFlowRuntimeCatalogCandidate.SemanticHash,
            ["formNodes"] = new JsonArray(formPins.Select(pin => (JsonNode)new JsonObject
            {
                ["formNodeId"] = pin.FormNodeId,
                ["role"] = pin.Role,
                ["dynamicFormTemplateId"] = pin.Form.Id,
                ["dynamicFormFamilyId"] = pin.Form.FamilyId ?? pin.Form.Id,
                ["dynamicFormVersionNo"] = Math.Max(1, pin.Form.VersionNo),
                ["dynamicFormSchemaHash"] = pin.SchemaHash,
                ["dynamicFormSnapshotHash"] = pin.SchemaHash
            }).ToArray()),
            ["nodes"] = nodes,
            ["edges"] = edges,
            ["actorPolicies"] = new JsonArray
            {
                new JsonObject
                {
                    ["policyId"] = "actor-owner-root",
                    ["stepId"] = "*",
                    ["stepCode"] = "*",
                    ["actorRole"] = "*",
                    ["allowForward"] = true
                }
            },
            ["fieldPolicies"] = new JsonArray
            {
                new JsonObject
                {
                    ["policyId"] = "fields-owner-root",
                    ["dynamicFormTemplateId"] = "*",
                    ["stepId"] = "*",
                    ["stepCode"] = "*",
                    ["actorRole"] = "*",
                    ["fieldId"] = "*",
                    ["fieldKey"] = "*",
                    ["read"] = true,
                    ["write"] = true
                }
            },
            ["tableColumnPolicies"] = new JsonArray(),
            ["mappingRules"] = new JsonArray(),
            ["rollbackPolicy"] = new JsonObject(),
            ["finalResultPolicy"] = new JsonObject(),
            ["statisticProfile"] = new JsonObject()
        };
        var canonical = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            payload.ToJsonString(),
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true,
                AllowHistoricalCatalogPins: true));
        var family = new DynamicFlowTemplate
        {
            Id = familyId,
            Code = $"P5_MATERIALIZATION_{archetypeId.Replace("-", "_", StringComparison.Ordinal)}",
            Name = $"P5 Materialization {archetypeId}",
            FamilyRevision = 1,
            OwnerUserId = adminId,
            RootDynamicFormTemplateId = form.Id,
            Status = DynamicFlowTemplateStatuses.Active,
            CurrentVersionId = versionId,
            CurrentVersionNo = 1,
            CurrentVersionHash = canonical.PayloadHash,
            HasLockedVersion = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = adminId,
            UpdatedByUserId = adminId,
            IsDeleted = false
        };
        var version = new DynamicFlowTemplateVersion
        {
            Id = versionId,
            TemplateId = familyId,
            RootDynamicFormTemplateId = form.Id,
            VersionNo = 1,
            Status = DynamicFlowTemplateVersionStatuses.Locked,
            DraftRevision = 1,
            SchemaVersion = DynamicFlowDefinitionSchema.CurrentVersion,
            AdapterVersion = DynamicFlowDefinitionSchema.CurrentAdapterVersion,
            CatalogVersion = DynamicFlowRuntimeCatalogCandidate.Version,
            CatalogSemanticHash = DynamicFlowRuntimeCatalogCandidate.SemanticHash,
            PayloadJson = canonical.CanonicalJson,
            PayloadHash = canonical.PayloadHash,
            DefinitionLockable = true,
            ExecutionEligibility = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase,
            ExecutionBlockedReason = DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented,
            BlockedUntilPhase = "P5",
            MigrationState = DynamicFlowDefinitionMigrationStates.Canonical,
            LockedAtUtc = now,
            LockedByUserId = adminId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = adminId,
            UpdatedByUserId = adminId,
            IsDeleted = false
        };
        return (family, version);
    }

    private static async Task RestoreCandidateDefinitionAsync(
        IMongoDatabase database,
        string versionId,
        CancellationToken ct)
    {
        var versions = database.GetCollection<DynamicFlowTemplateVersion>(
            "dynamic_flow_template_versions");
        var version = await versions.Find(x => x.Id == versionId).SingleAsync(ct);
        await versions.UpdateOneAsync(
            x => x.Id == versionId,
            Builders<DynamicFlowTemplateVersion>.Update
                .Set(x => x.CatalogVersion, DynamicFlowRuntimeCatalogCandidate.Version)
                .Set(x => x.CatalogSemanticHash, DynamicFlowRuntimeCatalogCandidate.SemanticHash)
                .Set(x => x.SchemaVersion, DynamicFlowDefinitionSchema.CurrentVersion)
                .Set(x => x.AdapterVersion, DynamicFlowDefinitionSchema.CurrentAdapterVersion)
                .Set(x => x.MigrationState, DynamicFlowDefinitionMigrationStates.Canonical)
                .Set(x => x.DefinitionLockable, true)
                .Set(
                    x => x.ExecutionEligibility,
                    DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase)
                .Set(
                    x => x.ExecutionBlockedReason,
                    DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented)
                .Set(x => x.BlockedUntilPhase, "P5"),
            cancellationToken: ct);
        await database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .UpdateOneAsync(
                x => x.Id == version.TemplateId,
                Builders<DynamicFlowTemplate>.Update
                    .Set(x => x.Status, DynamicFlowTemplateStatuses.Active)
                    .Set(x => x.CurrentVersionId, version.Id)
                    .Set(x => x.CurrentVersionNo, version.VersionNo)
                    .Set(x => x.CurrentVersionHash, version.PayloadHash)
                    .Set(x => x.HasLockedVersion, true),
                cancellationToken: ct);
    }

    private static async Task<LaunchResult> LaunchAsync(
        ApiHarnessClient api,
        string token,
        ProbeFixture fixture,
        string workId,
        string versionId,
        string commandId,
        IReadOnlyCollection<string> targetUnitIds,
        string periodKey,
        CancellationToken ct)
    {
        var preflight = await PreflightAsync(
            api,
            token,
            workId,
            versionId,
            commandId,
            targetUnitIds,
            periodKey,
            ct);
        var confirm = await ConfirmAsync(
            api,
            token,
            workId,
            versionId,
            commandId,
            targetUnitIds,
            periodKey,
            preflight.SnapshotToken,
            ct);
        return new LaunchResult(preflight.SnapshotToken, confirm);
    }

    private static async Task<PreflightResult> PreflightAsync(
        ApiHarnessClient api,
        string token,
        string workId,
        string versionId,
        string commandId,
        IReadOnlyCollection<string> targetUnitIds,
        string periodKey,
        CancellationToken ct)
    {
        var response = await api.PostAsync(
            $"api/works/{workId}/dynamic-flows/preflight",
            new DynamicFlowPreflightRequest
            {
                FlowTemplateVersionId = versionId,
                CommandId = commandId,
                TargetUnitIds = targetUnitIds.Reverse().ToList(),
                PeriodKey = periodKey,
                ScheduleIdentityJson =
                    "{\"timezone\":\"Asia/Ho_Chi_Minh\",\"cadence\":\"once\",\"anchor\":\"2026-07-23\"}"
            },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"preflight {commandId}");
        Require(
            ApiHarnessClient.RequiredString(response.Json, "eligibility") ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            $"Preflight {commandId} was not candidate-eligible.");
        return new PreflightResult(ApiHarnessClient.RequiredString(response.Json, "snapshotToken"));
    }

    private static Task<ApiHarnessResponse> ConfirmAsync(
        ApiHarnessClient api,
        string token,
        string workId,
        string versionId,
        string commandId,
        IReadOnlyCollection<string> targetUnitIds,
        string periodKey,
        string snapshotToken,
        CancellationToken ct)
        => api.PostAsync(
            $"api/works/{workId}/dynamic-flows/confirm",
            new DynamicFlowConfirmRequest
            {
                FlowTemplateVersionId = versionId,
                CommandId = commandId,
                TargetUnitIds = targetUnitIds.ToList(),
                PeriodKey = periodKey,
                ScheduleIdentityJson =
                    "{\"anchor\":\"2026-07-23\",\"cadence\":\"once\",\"timezone\":\"Asia/Ho_Chi_Minh\"}",
                SnapshotToken = snapshotToken
            },
            token,
            ct: ct);

    private static async Task<object> RunStateProjectionScenarioAsync(
        ApiHarnessClient api,
        string adminToken,
        BackendServerLease backend,
        IMongoDatabase database,
        ProbeFixture fixture,
        CancellationToken ct)
    {
        const string launchCommandId = "p5-state-projection-launch";
        const string approveCommandId = "p5-state-review-approve";
        const string periodKey = "2026-07-23";
        var targetUnitId = fixture.TargetUnitIds[0];
        var launch = await LaunchAsync(
            api,
            adminToken,
            fixture,
            fixture.StateWorkId,
            fixture.T01VersionId,
            launchCommandId,
            [targetUnitId],
            periodKey,
            ct);
        RequireStatus(launch.Confirm, "SUCCEEDED", "state projection FLOW-T01 launch");
        var launchSnapshot = await ValidateConvergedAsync(
            database,
            fixture,
            fixture.StateWorkId,
            launchCommandId,
            fixture.T01VersionId,
            "FLOW-T01",
            [targetUnitId],
            periodKey,
            false,
            ct);

        var instanceId = launchSnapshot.FlowInstanceId;
        var step = await database.GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(x => x.FlowInstanceId == instanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var assignmentId = step.AssignmentId
                           ?? throw new InvalidOperationException(
                               "State projection step lacks assignment.");
        var assignment = await database.GetCollection<WorkAssignment>("work_assignments")
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .SingleAsync(ct);
        var period = await database.GetCollection<WorkReportPeriod>("work_report_periods")
            .Find(x =>
                x.WorkAssignmentId == assignmentId &&
                x.PeriodKey == periodKey &&
                !x.IsDeleted)
            .SingleAsync(ct);
        var participantId = step.ParticipantUserIds.Single();
        var users = database.GetCollection<AppUser>("users");
        var participant = await users
            .Find(x => x.Id == participantId && !x.IsDeleted)
            .SingleAsync(ct);
        var passwordHash = new PasswordHasher<AppUser>()
            .HashPassword(participant, backend.ActorPassword);
        await users.UpdateOneAsync(
            x => x.Id == participant.Id && !x.IsDeleted,
            Builders<AppUser>.Update
                .Set(x => x.PasswordHash, passwordHash)
                .Set(x => x.UpdatedAtUtc, DateTime.UtcNow),
            cancellationToken: ct);
        var participantToken = await api.LoginAsync(
            participant.Username,
            backend.ActorPassword,
            ct);
        var adminActorId = (await users
                .Find(x => x.Username == "admin" && !x.IsDeleted)
                .SingleAsync(ct))
            .Id;

        var beforeUnapprovedForward = await CaptureStateFlowHashAsync(
            database,
            fixture.StateWorkId,
            instanceId,
            assignmentId,
            ct);
        var unapprovedInstance = await database
            .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(x => x.Id == instanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var unapprovedStep = await database
            .GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(x => x.Id == step.Id && !x.IsDeleted)
            .SingleAsync(ct);
        var unapprovedForward = await api.PostAsync(
            $"api/works/{fixture.StateWorkId}/dynamic-flows/assignments/{assignmentId}/forward",
            new DynamicFlowBranchActionRequest
            {
                CommandId = "p5-state-unapproved-forward",
                ExpectedInstanceRevision = unapprovedInstance.Revision,
                ExpectedStepRevision = unapprovedStep.Revision,
                Reason = "P5 state projection parent approval guard"
            },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            unapprovedForward,
            HttpStatusCode.Conflict,
            "state projection unapproved forward");
        Require(
            ApiHarnessClient.FindStringRecursive(
                unapprovedForward.Json,
                "reason") ==
            "DYNAMIC_FLOW_FORWARD_PARENT_NOT_APPROVED",
            "Unapproved forward lacks the stable parent-not-approved reason.");
        var afterUnapprovedForward = await CaptureStateFlowHashAsync(
            database,
            fixture.StateWorkId,
            instanceId,
            assignmentId,
            ct);
        Require(
            beforeUnapprovedForward == afterUnapprovedForward,
            "Unapproved forward changed scoped persistence.");

        var open = await api.PostAsync(
            $"api/work-report-periods/{period.Id}/open",
            body: null,
            participantToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(open, HttpStatusCode.OK, "state projection report open");
        var reportId = ApiHarnessClient.RequiredString(open.Json, "id");
        var openedPayloadRevision = ApiHarnessClient.RequiredInt(open.Json, "payloadRevision");
        var openedLifecycleRevision = ApiHarnessClient.RequiredInt(open.Json, "lifecycleRevision");
        var openDraftReconcile = await RunOpenDraftReconcileAsync(
            api,
            adminToken,
            database,
            fixture.StateWorkId,
            instanceId,
            step.Id,
            assignmentId,
            period.Id,
            reportId,
            adminActorId,
            ct);
        var submitRequest = new JsonObject
        {
            ["expectedPayloadRevision"] = openedPayloadRevision,
            ["expectedLifecycleRevision"] = openedLifecycleRevision,
            ["commandId"] = StateProjectionFaultCommand,
            ["values1D"] = new JsonArray(0),
            ["fieldValuesJson"] = new JsonObject
            {
                ["values"] = new JsonObject
                {
                    ["field_note"] = "P5_NOTE"
                }
            }.ToJsonString(),
            ["tableValuesJson"] = null,
            ["dataOrigin"] = "MANUAL_INPUT",
            ["cumulativeContributionMode"] = "INCLUDE"
        };
        var submit = await api.PostAsync(
            $"api/work-assignment-reports/{reportId}/submit",
            submitRequest,
            participantToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            submit,
            HttpStatusCode.Accepted,
            "state projection injected submit");
        Require(
            ApiHarnessClient.RequiredString(submit.Json, "lifecycleCommitState") ==
            "COMMITTED_PENDING_PROJECTION" &&
            ApiHarnessClient.RequiredBool(submit.Json, "lifecycleProjectionPending"),
            "Injected submit did not expose its committed-pending projection state.");

        var afterForeground = await database
            .GetCollection<WorkAssignmentReport>("work_assignment_report")
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        Require(
            afterForeground.Status == WorkAssignmentReportStatus.Submitted &&
            afterForeground.LifecycleProjectionOutbox.Count(entry =>
                entry.CommandId == StateProjectionFaultCommand &&
                entry.State == WorkReportLifecycleProjectionOutboxStates.Pending) == 1,
            "Injected submit did not retain one durable P3 pending entry.");
        step = await database.GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(x => x.Id == step.Id && !x.IsDeleted)
            .SingleAsync(ct);
        Require(
            step.State == DynamicFlowStepStates.InProgress,
            "BEFORE_STATE_PROJECTION_TRANSACTION fault changed the runtime step.");

        var beforePendingCompletion = await CaptureStateFlowHashAsync(
            database,
            fixture.StateWorkId,
            instanceId,
            assignmentId,
            ct);
        var pendingCompletion = await api.PostAsync(
            $"api/work-assignments/{assignmentId}/complete",
            new JsonObject
            {
                ["completedDate"] = "2026-07-23T00:00:00Z",
                ["note"] = "P5 completion must wait for lifecycle projection"
            },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            pendingCompletion,
            HttpStatusCode.Conflict,
            "state projection pending completion guard");
        Require(
            ApiHarnessClient.FindStringRecursive(
                pendingCompletion.Json,
                "errorCode") ==
            "WORK_ASSIGNMENT_COMPLETION_PENDING_REPORTS" &&
            beforePendingCompletion == await CaptureStateFlowHashAsync(
                database,
                fixture.StateWorkId,
                instanceId,
                assignmentId,
                ct),
            "Completion during COMMITTED_PENDING_PROJECTION was not a stable zero-write conflict.");

        var workerAttempts = new List<StateProjectionWorkerAttempt>();
        WorkAssignmentReport? submittedReport = null;
        for (var attempt = 1; attempt <= StateProjectionFaultPoints.Length + 3; attempt++)
        {
            var worker = await api.PostAsync(
                "api/admin/operations/job-runs/lifecycle-projection-outbox/process?maxReports=20",
                body: null,
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                worker,
                HttpStatusCode.OK,
                $"state projection worker attempt {attempt}");
            Require(
                ApiHarnessClient.RequiredBool(worker.Json, "ok"),
                $"State projection worker attempt {attempt} returned ok=false.");
            submittedReport = await database
                .GetCollection<WorkAssignmentReport>("work_assignment_report")
                .Find(x => x.Id == reportId && !x.IsDeleted)
                .SingleAsync(ct);
            var pendingCount = submittedReport.LifecycleProjectionOutbox.Count(entry =>
                entry.CommandId == StateProjectionFaultCommand &&
                entry.State == WorkReportLifecycleProjectionOutboxStates.Pending);
            var stateReceiptCount = await database
                .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                    "dynamic_flow_runtime_command_receipts")
                .CountDocumentsAsync(
                    x =>
                        x.FlowInstanceId == instanceId &&
                        x.CommandType ==
                        DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle,
                    cancellationToken: ct);
            var currentStep = await database
                .GetCollection<DynamicFlowStepInstance>("dynamic_flow_step_instances")
                .Find(x => x.Id == step.Id && !x.IsDeleted)
                .SingleAsync(ct);
            workerAttempts.Add(new StateProjectionWorkerAttempt(
                attempt,
                ApiHarnessClient.RequiredInt(worker.Json, "processed"),
                pendingCount,
                stateReceiptCount,
                currentStep.State,
                currentStep.Revision));
            if (pendingCount == 0)
                break;
        }

        Require(submittedReport is not null, "State projection worker produced no report state.");
        Require(
            workerAttempts.Count == StateProjectionFaultPoints.Length,
            $"State projection fault campaign expected {StateProjectionFaultPoints.Length} worker attempts after foreground, got {workerAttempts.Count}.");
        Require(
            workerAttempts.All(item => item.Processed == 1) &&
            workerAttempts[^1].PendingCount == 0,
            "State projection worker campaign did not deterministically converge.");

        var submitProjection = await ValidateReportStateProjectionAsync(
            database,
            instanceId,
            step.Id,
            assignmentId,
            reportId,
            StateProjectionFaultCommand,
            participantId,
            DynamicFlowStepStates.InProgress,
            DynamicFlowStepStates.Submitted,
            [
                (DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Submitted)
            ],
            "REPORT_LIFECYCLE_SUBMIT",
            WorkAssignmentReportStatus.Submitted,
            true,
            true,
            ct);
        var submittedReadModels = await ValidateStateReadModelConvergenceAsync(
            database,
            fixture.StateWorkId,
            instanceId,
            step.Id,
            assignmentId,
            period.Id,
            reportId,
            WorkAssignmentReportStatus.Submitted,
            true,
            true,
            3,
            ct);

        var beforeSubmitReplay = await CaptureStateFlowHashAsync(
            database,
            fixture.StateWorkId,
            instanceId,
            assignmentId,
            ct);
        var submitReplay = await api.PostAsync(
            $"api/work-assignment-reports/{reportId}/submit",
            submitRequest,
            participantToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            submitReplay,
            HttpStatusCode.OK,
            "state projection submit exact replay");
        var afterSubmitReplay = await CaptureStateFlowHashAsync(
            database,
            fixture.StateWorkId,
            instanceId,
            assignmentId,
            ct);
        Require(
            beforeSubmitReplay == afterSubmitReplay,
            "Exact submit replay changed runtime/P3/read-model persistence.");

        var beforeIdleWorker = afterSubmitReplay;
        var idleWorker = await api.PostAsync(
            "api/admin/operations/job-runs/lifecycle-projection-outbox/process?maxReports=20",
            body: null,
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            idleWorker,
            HttpStatusCode.OK,
            "state projection idle worker replay");
        Require(
            ApiHarnessClient.RequiredInt(idleWorker.Json, "processed") == 0,
            "Idle state projection worker unexpectedly found pending work.");
        Require(
            beforeIdleWorker == await CaptureStateFlowHashAsync(
                database,
                fixture.StateWorkId,
                instanceId,
                assignmentId,
                ct),
            "Idle state projection worker changed scoped persistence.");

        submittedReport = await database
            .GetCollection<WorkAssignmentReport>("work_assignment_report")
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        var approveRequest = new JsonObject
        {
            ["expectedPayloadRevision"] = submittedReport.PayloadRevision,
            ["expectedLifecycleRevision"] = submittedReport.LifecycleRevision,
            ["commandId"] = approveCommandId,
            ["comment"] = "P5 state projection approved",
            ["confirmHistoricalDataApproval"] = false
        };
        var approve = await api.PostAsync(
            $"api/work-assignment-review/reports/{reportId}/approve",
            approveRequest,
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            approve,
            HttpStatusCode.OK,
            "state projection reviewer approve");
        var approvedReport = await database
            .GetCollection<WorkAssignmentReport>("work_assignment_report")
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        Require(
            approvedReport.Status == WorkAssignmentReportStatus.Approved &&
            approvedReport.LifecycleProjectionOutbox.All(entry =>
                entry.State == WorkReportLifecycleProjectionOutboxStates.Completed),
            "Review approval did not converge its durable P3 outbox.");

        var approveProjection = await ValidateReportStateProjectionAsync(
            database,
            instanceId,
            step.Id,
            assignmentId,
            reportId,
            approveCommandId,
            adminActorId,
            DynamicFlowStepStates.Submitted,
            DynamicFlowStepStates.Approved,
            [(DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Approved)],
            "REPORT_LIFECYCLE_REVIEW_APPROVE",
            WorkAssignmentReportStatus.Approved,
            true,
            true,
            ct);
        var approvedReadModels = await ValidateStateReadModelConvergenceAsync(
            database,
            fixture.StateWorkId,
            instanceId,
            step.Id,
            assignmentId,
            period.Id,
            reportId,
            WorkAssignmentReportStatus.Approved,
            true,
            true,
            4,
            ct);

        var lifecycleCycles = await RunReviewLifecycleCyclesAsync(
            api,
            adminToken,
            participantToken,
            database,
            fixture.StateWorkId,
            instanceId,
            step.Id,
            assignmentId,
            period.Id,
            reportId,
            participantId,
            adminActorId,
            approveRequest,
            ct);
        var stateReconcileRace = await RunStateReconcileRaceAsync(
            api,
            adminToken,
            database,
            fixture.StateWorkId,
            instanceId,
            step.Id,
            assignmentId,
            period.Id,
            reportId,
            adminActorId,
            ct);

        var beforeApprovedForward = await CaptureStateFlowHashAsync(
            database,
            fixture.StateWorkId,
            instanceId,
            assignmentId,
            ct);
        var approvedForward = await api.PostAsync(
            $"api/works/{fixture.StateWorkId}/dynamic-flows/assignments/{assignmentId}/forward",
            new JsonObject(),
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            approvedForward,
            HttpStatusCode.Conflict,
            "state projection approved forward");
        Require(
            ApiHarnessClient.FindStringRecursive(
                approvedForward.Json,
                "reason") ==
            "DYNAMIC_FLOW_COMMAND_BLOCKED_UNTIL_P6",
            "Approved forward lacks the stable P6 block reason.");
        var afterApprovedForward = await CaptureStateFlowHashAsync(
            database,
            fixture.StateWorkId,
            instanceId,
            assignmentId,
            ct);
        Require(
            beforeApprovedForward == afterApprovedForward,
            "Approved P6-blocked forward changed scoped persistence.");

        var aggregateCompletionGuards =
            await RunAggregateCompletionGuardsAsync(
                api,
                adminToken,
                database,
                fixture.StateWorkId,
                instanceId,
                step.Id,
                assignmentId,
                reportId,
                ct);

        var beforeCompletionStep = await database
            .GetCollection<DynamicFlowStepInstance>("dynamic_flow_step_instances")
            .Find(x => x.Id == step.Id && !x.IsDeleted)
            .SingleAsync(ct);
        var beforeCompletionInstance = await database
            .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(x => x.Id == instanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var beforeCompletionReceiptCount = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                "dynamic_flow_runtime_command_receipts")
            .CountDocumentsAsync(
                x => x.FlowInstanceId == instanceId,
                cancellationToken: ct);
        var beforeCompletionEventCount = await database
            .GetCollection<DynamicFlowRuntimeEvent>("dynamic_flow_runtime_events")
            .CountDocumentsAsync(
                x => x.FlowInstanceId == instanceId,
                cancellationToken: ct);
        var completionRequest = new JsonObject
        {
            ["completedDate"] = "2026-07-23T00:00:00Z",
            ["note"] = "P5 T01 internal completion"
        };
        const string completionRaceRecallCommandId =
            "p5-state-completion-race-recall";
        var raceReport = await database
            .GetCollection<WorkAssignmentReport>("work_assignment_report")
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        var raceReportHashBefore = Hash(
            raceReport.ToBsonDocument().ToJson());
        var completionRaceRecallRequest = new JsonObject
        {
            ["expectedPayloadRevision"] = raceReport.PayloadRevision,
            ["expectedLifecycleRevision"] = raceReport.LifecycleRevision,
            ["commandId"] = completionRaceRecallCommandId,
            ["comment"] = "P5 completion versus recall race"
        };
        var completionTask = api.PostAsync(
            $"api/work-assignments/{assignmentId}/complete",
            completionRequest,
            adminToken,
            ct: ct);
        var recallTask = api.PostAsync(
            $"api/work-assignment-review/reports/{reportId}/recall-approved",
            completionRaceRecallRequest,
            adminToken,
            ct: ct);
        await Task.WhenAll(completionTask, recallTask);
        var completionRaceResponse = await completionTask;
        var recallRaceResponse = await recallTask;
        var completionWon =
            completionRaceResponse.StatusCode == HttpStatusCode.OK;
        var recallWon = recallRaceResponse.StatusCode == HttpStatusCode.OK;
        Require(
            completionWon != recallWon &&
            (completionWon
                ? recallRaceResponse.StatusCode == HttpStatusCode.Conflict
                : completionRaceResponse.StatusCode == HttpStatusCode.Conflict),
            "Completion-vs-recall race did not produce exactly one winner and one conflict.");

        ApiHarnessResponse completion;
        object completionRace;
        if (completionWon)
        {
            var reportAfterRace = await database
                .GetCollection<WorkAssignmentReport>(
                    "work_assignment_report")
                .Find(x => x.Id == reportId && !x.IsDeleted)
                .SingleAsync(ct);
            Require(
                Hash(reportAfterRace.ToBsonDocument().ToJson()) ==
                raceReportHashBefore &&
                ApiHarnessClient.FindStringRecursive(
                    recallRaceResponse.Json,
                    "errorCode") ==
                "WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT",
                "Completion winner did not leave the losing recall report-write-free.");
            completion = completionRaceResponse;
            completionRace = new
            {
                winner = "COMPLETE",
                completionStatus = (int)completionRaceResponse.StatusCode,
                recallStatus = (int)recallRaceResponse.StatusCode,
                recallError =
                    "WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT",
                reportWriteByLosingRecall = false,
                terminalRetry = "completion exact replay"
            };
        }
        else
        {
            var completionError = ApiHarnessClient.FindStringRecursive(
                completionRaceResponse.Json,
                "errorCode") ??
                ApiHarnessClient.FindStringRecursive(
                    completionRaceResponse.Json,
                    "reason");
            Require(
                completionError is
                    "WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT" or
                    "WORK_ASSIGNMENT_COMPLETION_PENDING_REPORTS",
                "Recall winner produced an unexpected completion loser reason.");
            var recallRaceProjection =
                await ValidateReportStateProjectionAsync(
                    database,
                    instanceId,
                    step.Id,
                    assignmentId,
                    reportId,
                    completionRaceRecallCommandId,
                    adminActorId,
                    DynamicFlowStepStates.Approved,
                    DynamicFlowStepStates.Submitted,
                    [
                        (
                            DynamicFlowStepStates.Approved,
                            DynamicFlowStepStates.Returned),
                        (
                            DynamicFlowStepStates.Returned,
                            DynamicFlowStepStates.InProgress),
                        (
                            DynamicFlowStepStates.InProgress,
                            DynamicFlowStepStates.Submitted)
                    ],
                    "REPORT_LIFECYCLE_REVIEW_RECALL_APPROVED",
                    WorkAssignmentReportStatus.Submitted,
                    true,
                    true,
                    ct);
            var recalledRaceReadModels =
                await ValidateStateReadModelConvergenceAsync(
                    database,
                    fixture.StateWorkId,
                    instanceId,
                    step.Id,
                    assignmentId,
                    period.Id,
                    reportId,
                    WorkAssignmentReportStatus.Submitted,
                    true,
                    true,
                    17,
                    ct);
            var assignmentAfterRecall = await database
                .GetCollection<WorkAssignment>("work_assignments")
                .Find(x => x.Id == assignmentId && !x.IsDeleted)
                .SingleAsync(ct);
            Require(
                !assignmentAfterRecall.CompletedAtUtc.HasValue,
                "Losing completion wrote terminal assignment fields after recall won.");

            var beforeRecallReplay = await CaptureStateLedgerSnapshotAsync(
                database,
                fixture.StateWorkId,
                instanceId,
                step.Id,
                assignmentId,
                ct);
            var recallReplay = await api.PostAsync(
                $"api/work-assignment-review/reports/{reportId}/recall-approved",
                completionRaceRecallRequest,
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                recallReplay,
                HttpStatusCode.OK,
                "completion race recall exact replay");
            Require(
                beforeRecallReplay ==
                await CaptureStateLedgerSnapshotAsync(
                    database,
                    fixture.StateWorkId,
                    instanceId,
                    step.Id,
                    assignmentId,
                    ct),
                "Completion race recall exact replay changed scoped persistence.");

            var recalledReport = await database
                .GetCollection<WorkAssignmentReport>(
                    "work_assignment_report")
                .Find(x => x.Id == reportId && !x.IsDeleted)
                .SingleAsync(ct);
            const string raceReapproveCommandId =
                "p5-state-completion-race-reapprove";
            var raceReapproveRequest = new JsonObject
            {
                ["expectedPayloadRevision"] = recalledReport.PayloadRevision,
                ["expectedLifecycleRevision"] =
                    recalledReport.LifecycleRevision,
                ["commandId"] = raceReapproveCommandId,
                ["comment"] = "P5 reapprove after completion race",
                ["confirmHistoricalDataApproval"] = false
            };
            var raceReapprove = await api.PostAsync(
                $"api/work-assignment-review/reports/{reportId}/approve",
                raceReapproveRequest,
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                raceReapprove,
                HttpStatusCode.OK,
                "completion race reapprove");
            var raceReapproveProjection =
                await ValidateReportStateProjectionAsync(
                    database,
                    instanceId,
                    step.Id,
                    assignmentId,
                    reportId,
                    raceReapproveCommandId,
                    adminActorId,
                    DynamicFlowStepStates.Submitted,
                    DynamicFlowStepStates.Approved,
                    [
                        (
                            DynamicFlowStepStates.Submitted,
                            DynamicFlowStepStates.Approved)
                    ],
                    "REPORT_LIFECYCLE_REVIEW_APPROVE",
                    WorkAssignmentReportStatus.Approved,
                    true,
                    true,
                    ct);
            var raceReapprovedReadModels =
                await ValidateStateReadModelConvergenceAsync(
                    database,
                    fixture.StateWorkId,
                    instanceId,
                    step.Id,
                    assignmentId,
                    period.Id,
                    reportId,
                    WorkAssignmentReportStatus.Approved,
                    true,
                    true,
                    18,
                    ct);

            beforeCompletionStep = await database
                .GetCollection<DynamicFlowStepInstance>(
                    "dynamic_flow_step_instances")
                .Find(x => x.Id == step.Id && !x.IsDeleted)
                .SingleAsync(ct);
            beforeCompletionInstance = await database
                .GetCollection<DynamicFlowInstance>(
                    "dynamic_flow_instances")
                .Find(x => x.Id == instanceId && !x.IsDeleted)
                .SingleAsync(ct);
            beforeCompletionReceiptCount = await database
                .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                    "dynamic_flow_runtime_command_receipts")
                .CountDocumentsAsync(
                    x => x.FlowInstanceId == instanceId,
                    cancellationToken: ct);
            beforeCompletionEventCount = await database
                .GetCollection<DynamicFlowRuntimeEvent>(
                    "dynamic_flow_runtime_events")
                .CountDocumentsAsync(
                    x => x.FlowInstanceId == instanceId,
                    cancellationToken: ct);
            completion = await api.PostAsync(
                $"api/work-assignments/{assignmentId}/complete",
                completionRequest,
                adminToken,
                ct: ct);
            completionRace = new
            {
                winner = "RECALL",
                completionStatus =
                    (int)completionRaceResponse.StatusCode,
                completionError,
                recallStatus = (int)recallRaceResponse.StatusCode,
                losingCompletionTerminalWrite = false,
                recallRaceProjection,
                recalledRaceReadModels,
                recallReplayZeroWrite = true,
                raceReapproveProjection,
                raceReapprovedReadModels,
                terminalRetry = "reapprove then completion"
            };
        }
        ApiHarnessClient.ExpectStatus(
            completion,
            HttpStatusCode.OK,
            "state projection T01 assignment completion");
        var completionProjection = await ValidateT01CompletionAsync(
            database,
            fixture.StateWorkId,
            instanceId,
            step.Id,
            assignmentId,
            period.Id,
            reportId,
            adminActorId,
            ct);
        Require(
            completionProjection.StepRevision == beforeCompletionStep.Revision + 1 &&
            completionProjection.InstanceRevision ==
            beforeCompletionInstance.Revision + 1 &&
            completionProjection.ReceiptCount ==
            beforeCompletionReceiptCount + 1 &&
            completionProjection.EventCount ==
            beforeCompletionEventCount + 2,
            "T01 completion did not add exactly one receipt, one step/instance revision, and two typed events.");

        var completionReplay = await api.PostAsync(
            $"api/work-assignments/{assignmentId}/complete",
            completionRequest,
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            completionReplay,
            HttpStatusCode.OK,
            "state projection T01 assignment completion replay");
        var completionReplayProjection = await ValidateT01CompletionAsync(
            database,
            fixture.StateWorkId,
            instanceId,
            step.Id,
            assignmentId,
            period.Id,
            reportId,
            adminActorId,
            ct);
        Require(
            completionReplayProjection == completionProjection,
            "Exact T01 completion replay changed receipt/event/runtime revisions or read-model cardinality.");

        var terminalAggregateFence =
            await RunTerminalAggregateFenceAsync(
                api,
                adminToken,
                participantToken,
                database,
                fixture.StateWorkId,
                instanceId,
                step.Id,
                assignmentId,
                reportId,
                ct);

        var flowSteps = await database.GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(x => x.FlowInstanceId == instanceId && !x.IsDeleted)
            .ToListAsync(ct);
        Require(
            flowSteps.Count == 1 &&
            flowSteps.Single().FlowStepCode == "ROOT" &&
            flowSteps.Single().State == DynamicFlowStepStates.Completed,
            "State projection probe opened topology beyond FLOW-T01.");

        return new
        {
            flowInstanceId = instanceId,
            assignmentId,
            reportId,
            configuredBoundaryCount = StateProjectionFaultPoints.Length,
            foregroundStatus = (int)submit.StatusCode,
            openDraftReconcile,
            pendingCompletion = new
            {
                status = (int)pendingCompletion.StatusCode,
                code = "WORK_ASSIGNMENT_COMPLETION_PENDING_REPORTS",
                zeroWrite = true
            },
            workerAttempts,
            submitProjection,
            submittedReadModels,
            submitReplayStatus = (int)submitReplay.StatusCode,
            submitReplayZeroWrite = true,
            idleWorkerProcessed = 0,
            approveProjection,
            approvedReadModels,
            lifecycleCycles,
            stateReconcileRace,
            aggregateCompletionGuards,
            completionRace,
            completionStatus = (int)completion.StatusCode,
            completionProjection,
            completionReplayStatus = (int)completionReplay.StatusCode,
            completionReplayZeroAdditionalReceiptEventRevision = true,
            terminalAggregateFence,
            unapprovedForward = new
            {
                status = (int)unapprovedForward.StatusCode,
                reason = "DYNAMIC_FLOW_FORWARD_PARENT_NOT_APPROVED",
                zeroWrite = true
            },
            approvedForward = new
            {
                status = (int)approvedForward.StatusCode,
                reason = "DYNAMIC_FLOW_COMMAND_BLOCKED_UNTIL_P6",
                zeroWrite = true
            },
            positiveArchetypes = new[] { "FLOW-T01" },
            forbiddenArchetypesOpened = false
        };
    }

    private static async Task<object> RunAggregateCompletionGuardsAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        string workId,
        string flowInstanceId,
        string stepInstanceId,
        string assignmentId,
        string reportId,
        CancellationToken ct)
    {
        var reportObjectId = ObjectId.Parse(reportId);
        var reports = database.GetCollection<BsonDocument>(
            "work_assignment_report");
        var reportFilter = Builders<BsonDocument>.Filter.Eq(
            "_id",
            reportObjectId);
        var original = await reports
            .Find(reportFilter)
            .SingleAsync(ct);
        var originalHash = Hash(original.ToJson());
        var typedOriginal = await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        Require(
            typedOriginal.Status == WorkAssignmentReportStatus.Approved &&
            typedOriginal.IsActive &&
            typedOriginal.IsCurrent &&
            !typedOriginal.AggregateSnapshotDirty &&
            string.IsNullOrWhiteSpace(
                typedOriginal.PayloadMutationCommandId),
            "Aggregate completion guards did not start from the canonical approved report.");

        var completionRequest = new JsonObject
        {
            ["completedDate"] = "2026-07-23T00:00:00Z",
            ["note"] = "P5 aggregate completion guard"
        };

        await reports.UpdateOneAsync(
            reportFilter,
            Builders<BsonDocument>.Update
                .Set("aggregateSnapshotDirty", true)
                .Set(
                    "aggregateSnapshotDirtyAtUtc",
                    DateTime.UtcNow),
            cancellationToken: ct);
        var dirtyInjected = await reports
            .Find(reportFilter)
            .SingleAsync(ct);
        Require(
            dirtyInjected.GetValue(
                "aggregateSnapshotDirty",
                false).ToBoolean(),
            "Aggregate completion guard failed to inject a dirty snapshot.");
        var beforeDirtyCompletion = await CaptureStateFlowHashAsync(
            database,
            workId,
            flowInstanceId,
            assignmentId,
            ct);
        var dirtyCompletion = await api.PostAsync(
            $"api/work-assignments/{assignmentId}/complete",
            completionRequest,
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            dirtyCompletion,
            HttpStatusCode.Conflict,
            "aggregate dirty assignment completion guard");
        var dirtyCompletionCode =
            ApiHarnessClient.FindStringRecursive(
                dirtyCompletion.Json,
                "errorCode");
        Require(
            dirtyCompletionCode ==
            "WORK_ASSIGNMENT_COMPLETION_PENDING_REPORTS",
            "Aggregate dirty completion guard returned an unexpected error code.");
        Require(
            beforeDirtyCompletion ==
            await CaptureStateFlowHashAsync(
                database,
                workId,
                flowInstanceId,
                assignmentId,
                ct),
            "Aggregate dirty completion guard changed scoped persistence.");
        await reports.ReplaceOneAsync(
            reportFilter,
            original,
            cancellationToken: ct);

        const string payloadMutationCommandId =
            "p5-injected-payload-mutation-in-flight";
        await reports.UpdateOneAsync(
            reportFilter,
            Builders<BsonDocument>.Update.Set(
                "payloadMutationCommandId",
                payloadMutationCommandId),
            cancellationToken: ct);
        var payloadMutationInjected = await reports
            .Find(reportFilter)
            .SingleAsync(ct);
        Require(
            payloadMutationInjected.GetValue(
                "payloadMutationCommandId",
                BsonNull.Value).AsString ==
            payloadMutationCommandId,
            "Aggregate completion guard failed to inject the payload mutation fence.");
        var beforePayloadMutationCompletion =
            await CaptureStateFlowHashAsync(
                database,
                workId,
                flowInstanceId,
                assignmentId,
                ct);
        var payloadMutationCompletion = await api.PostAsync(
            $"api/work-assignments/{assignmentId}/complete",
            completionRequest,
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            payloadMutationCompletion,
            HttpStatusCode.Conflict,
            "payload mutation assignment completion guard");
        var payloadMutationCompletionCode =
            ApiHarnessClient.FindStringRecursive(
                payloadMutationCompletion.Json,
                "errorCode");
        Require(
            payloadMutationCompletionCode ==
            "WORK_ASSIGNMENT_COMPLETION_PENDING_REPORTS",
            "Payload mutation completion guard returned an unexpected error code.");
        Require(
            beforePayloadMutationCompletion ==
            await CaptureStateFlowHashAsync(
                database,
                workId,
                flowInstanceId,
                assignmentId,
                ct),
            "Payload mutation completion guard changed scoped persistence.");
        await reports.ReplaceOneAsync(
            reportFilter,
            original,
            cancellationToken: ct);

        var restored = await reports
            .Find(reportFilter)
            .SingleAsync(ct);
        Require(
            Hash(restored.ToJson()) == originalHash,
            "Aggregate completion guards did not restore the canonical approved report.");
        var ledger = await CaptureStateLedgerSnapshotAsync(
            database,
            workId,
            flowInstanceId,
            stepInstanceId,
            assignmentId,
            ct);
        Require(
            ledger.StepState == DynamicFlowStepStates.Approved,
            "Aggregate completion guards changed the approved runtime state.");

        return new
        {
            aggregateSnapshotDirty = new
            {
                status = (int)dirtyCompletion.StatusCode,
                errorCode = dirtyCompletionCode,
                zeroWrite = true
            },
            payloadMutationInFlight = new
            {
                commandId = payloadMutationCommandId,
                status = (int)payloadMutationCompletion.StatusCode,
                errorCode = payloadMutationCompletionCode,
                zeroWrite = true
            },
            canonicalApprovedReportRestored = true,
            runtimeState = ledger.StepState
        };
    }

    private static async Task<object> RunTerminalAggregateFenceAsync(
        ApiHarnessClient api,
        string adminToken,
        string participantToken,
        IMongoDatabase database,
        string workId,
        string flowInstanceId,
        string stepInstanceId,
        string assignmentId,
        string reportId,
        CancellationToken ct)
    {
        var reportObjectId = ObjectId.Parse(reportId);
        var assignmentObjectId = ObjectId.Parse(assignmentId);
        var rawReports = database.GetCollection<BsonDocument>(
            "work_assignment_report");
        var rawAssignments = database.GetCollection<BsonDocument>(
            "work_assignments");
        var reportFilter = Builders<BsonDocument>.Filter.Eq(
            "_id",
            reportObjectId);
        var assignmentFilter = Builders<BsonDocument>.Filter.Eq(
            "_id",
            assignmentObjectId);
        var originalReport = await rawReports
            .Find(reportFilter)
            .SingleAsync(ct);
        var completedAssignment = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .SingleAsync(ct);
        var approvedReport = await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        Require(
            completedAssignment.CompletedAtUtc.HasValue &&
            approvedReport.Status ==
            WorkAssignmentReportStatus.Approved &&
            approvedReport.IsActive &&
            approvedReport.IsCurrent,
            "Terminal aggregate fence did not start from a completed assignment with its canonical approved report.");

        await rawReports.UpdateOneAsync(
            reportFilter,
            Builders<BsonDocument>.Update
                .Set("aggregateSnapshotDirty", true)
                .Set(
                    "aggregateSnapshotDirtyAtUtc",
                    DateTime.UtcNow),
            cancellationToken: ct);
        var injectedReport = await rawReports
            .Find(reportFilter)
            .SingleAsync(ct);
        var assignmentBeforeRefresh = await rawAssignments
            .Find(assignmentFilter)
            .SingleAsync(ct);
        var aggregateRefresh = await api.GetAsync(
            $"api/work-assignment-reports/{reportId}",
            participantToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            aggregateRefresh,
            HttpStatusCode.OK,
            "terminal aggregate refresh fence");
        Require(
            ApiHarnessClient.RequiredBool(
                aggregateRefresh.Json,
                "aggregateSnapshotDirty"),
            "Completed assignment aggregate refresh did not remain deferred and dirty.");
        var assignmentAfterRefresh = await rawAssignments
            .Find(assignmentFilter)
            .SingleAsync(ct);
        var reportAfterRefresh = await rawReports
            .Find(reportFilter)
            .SingleAsync(ct);
        Require(
            Hash(assignmentAfterRefresh.ToJson()) ==
            Hash(assignmentBeforeRefresh.ToJson()),
            "P5 aggregate refresh lease touched the completed assignment.");
        Require(
            Hash(reportAfterRefresh.ToJson()) ==
            Hash(injectedReport.ToJson()),
            "P5 aggregate refresh mutated the terminal report payload or lifecycle state.");

        var beforeStatusMutation = await CaptureStateFlowHashAsync(
            database,
            workId,
            flowInstanceId,
            assignmentId,
            ct);
        var deactivateRequest = new JsonObject
        {
            ["expectedPayloadRevision"] =
                approvedReport.PayloadRevision,
            ["expectedLifecycleRevision"] =
                approvedReport.LifecycleRevision,
            ["commandId"] =
                "p5-terminal-deactivate-rejected",
            ["comment"] =
                "P5 terminal status mutation must be rejected"
        };
        var deactivate = await api.PostAsync(
            $"api/work-assignment-review/reports/{reportId}/deactivate",
            deactivateRequest,
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            deactivate,
            HttpStatusCode.Conflict,
            "terminal report status mutation");
        var deactivateCode =
            ApiHarnessClient.FindStringRecursive(
                deactivate.Json,
                "errorCode");
        Require(
            deactivateCode ==
            "WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT",
            "Terminal report status mutation returned an unexpected error code.");
        Require(
            beforeStatusMutation ==
            await CaptureStateFlowHashAsync(
                database,
                workId,
                flowInstanceId,
                assignmentId,
                ct),
            "Terminal report status mutation changed scoped persistence.");

        var beforePayloadMutation = await CaptureStateFlowHashAsync(
            database,
            workId,
            flowInstanceId,
            assignmentId,
            ct);
        var saveDraftRequest = new JsonObject
        {
            ["expectedPayloadRevision"] =
                approvedReport.PayloadRevision,
            ["commandId"] =
                "p5-terminal-payload-mutation-rejected",
            ["values1D"] = new JsonArray(0),
            ["fieldValuesJson"] =
                new JsonObject
                {
                    ["values"] = new JsonObject
                    {
                        ["field_note"] =
                            "P5 terminal mutation"
                    }
                }.ToJsonString(),
            ["tableValuesJson"] = null,
            ["dataOrigin"] = "MANUAL_INPUT",
            ["cumulativeContributionMode"] = "INCLUDE"
        };
        var saveDraft = await api.PutAsync(
            $"api/work-assignment-reports/{reportId}/draft",
            saveDraftRequest,
            participantToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            saveDraft,
            HttpStatusCode.Conflict,
            "terminal report payload mutation");
        var saveDraftCode =
            ApiHarnessClient.FindStringRecursive(
                saveDraft.Json,
                "errorCode");
        Require(
            saveDraftCode ==
            "WORK_ASSIGNMENT_REPORT_SCOPE_COMPLETED_LOCKED",
            "Terminal report payload mutation returned an unexpected error code.");
        Require(
            beforePayloadMutation ==
            await CaptureStateFlowHashAsync(
                database,
                workId,
                flowInstanceId,
                assignmentId,
                ct),
            "Terminal report payload mutation changed scoped persistence.");

        await rawReports.ReplaceOneAsync(
            reportFilter,
            originalReport,
            cancellationToken: ct);
        var restoredReport = await rawReports
            .Find(reportFilter)
            .SingleAsync(ct);
        Require(
            Hash(restoredReport.ToJson()) ==
            Hash(originalReport.ToJson()),
            "Terminal aggregate fence did not restore the injected dirty marker.");
        var ledger = await CaptureStateLedgerSnapshotAsync(
            database,
            workId,
            flowInstanceId,
            stepInstanceId,
            assignmentId,
            ct);
        Require(
            ledger.StepState == DynamicFlowStepStates.Completed,
            "Terminal aggregate fence changed the completed runtime state.");

        return new
        {
            leaseOperation =
                WorkReportLifecycleSeriesOperations.AggregateRefresh,
            refreshReadStatus =
                (int)aggregateRefresh.StatusCode,
            refreshDeferredDirty = true,
            assignmentLeaseUntouched = true,
            reportPayloadLifecycleUntouched = true,
            statusMutation = new
            {
                status = (int)deactivate.StatusCode,
                errorCode = deactivateCode,
                zeroWrite = true
            },
            payloadMutation = new
            {
                status = (int)saveDraft.StatusCode,
                errorCode = saveDraftCode,
                zeroWrite = true
            },
            dirtyInjectionRestored = true,
            runtimeState = ledger.StepState
        };
    }

    private static async Task<object> RunOpenDraftReconcileAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        string workId,
        string flowInstanceId,
        string stepInstanceId,
        string assignmentId,
        string periodId,
        string reportId,
        string actorUserId,
        CancellationToken ct)
    {
        var reports = database.GetCollection<WorkAssignmentReport>(
            "work_assignment_report");
        var report = await reports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        Require(
            report.Status == WorkAssignmentReportStatus.Draft &&
            report.IsActive &&
            report.IsCurrent &&
            report.LifecycleRevision == 0 &&
            report.LifecycleProjectionOutbox.Count == 1 &&
            report.LifecycleProjectionOutbox.Count(entry =>
                entry.CommandId == $"init-draft:{report.Id}" &&
                entry.LifecycleRevision == 0 &&
                entry.Operation == "INIT_DRAFT" &&
                entry.State ==
                WorkReportLifecycleProjectionOutboxStates.Completed &&
                entry.FromStatus == "NONE" &&
                entry.ToStatus ==
                WorkAssignmentReportStatus.Draft.ToString().ToUpperInvariant() &&
                !entry.FromIsActive &&
                entry.ToIsActive) == 1,
            "Open-period reconcile fixture is not an active/current Draft revision 0 with one completed INIT_DRAFT outbox.");
        var reportHash = Hash(report.ToBsonDocument().ToJson());
        var initDraftEntry = report.LifecycleProjectionOutbox.Single();
        var steps = database.GetCollection<DynamicFlowStepInstance>(
            "dynamic_flow_step_instances");
        var projectedStep = await steps
            .Find(x => x.Id == stepInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        Require(
            projectedStep.State == DynamicFlowStepStates.InProgress &&
            projectedStep.ReportId == reportId &&
            projectedStep.ReportLifecycleRevision == 0 &&
            projectedStep.ReportLifecycleEntryKey == initDraftEntry.EntryKey &&
            projectedStep.ReportLifecycleCommandId == initDraftEntry.CommandId &&
            projectedStep.ReportLifecycleStatus ==
            WorkAssignmentReportStatus.Draft.ToString().ToUpperInvariant() &&
            projectedStep.ReportLifecycleIsActive == true,
            "Foreground INIT_DRAFT projection did not converge runtime state/provenance.");
        const string injectedEntryKey =
            "p5-open-draft-injected-metadata-drift";
        var injectedDrift = await steps.UpdateOneAsync(
            x =>
                x.Id == stepInstanceId &&
                x.FlowInstanceId == flowInstanceId &&
                x.AssignmentId == assignmentId &&
                x.Revision == projectedStep.Revision &&
                x.State == DynamicFlowStepStates.InProgress &&
                x.ReportId == reportId &&
                x.ReportLifecycleRevision == 0 &&
                x.ReportLifecycleEntryKey == initDraftEntry.EntryKey &&
                x.ReportLifecycleCommandId == initDraftEntry.CommandId &&
                x.ReportLifecycleStatus ==
                WorkAssignmentReportStatus.Draft.ToString().ToUpperInvariant() &&
                x.ReportLifecycleIsActive == true &&
                !x.IsDeleted,
            Builders<DynamicFlowStepInstance>.Update.Set(
                x => x.ReportLifecycleEntryKey,
                injectedEntryKey),
            cancellationToken: ct);
        Require(
            injectedDrift.ModifiedCount == 1,
            "Could not inject the scoped Open Draft metadata drift.");
        var beforeStep = await steps
            .Find(x => x.Id == stepInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var beforeInstance = await database
            .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(x => x.Id == flowInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var receipts = database.GetCollection<DynamicFlowRuntimeCommandReceipt>(
            "dynamic_flow_runtime_command_receipts");
        var beforeReceiptCount = await receipts.CountDocumentsAsync(
            x => x.FlowInstanceId == flowInstanceId,
            cancellationToken: ct);
        var events = database.GetCollection<DynamicFlowRuntimeEvent>(
            "dynamic_flow_runtime_events");
        var beforeEventCount = await events.CountDocumentsAsync(
            x => x.FlowInstanceId == flowInstanceId,
            cancellationToken: ct);
        Require(
            beforeStep.State == DynamicFlowStepStates.InProgress &&
            beforeStep.Revision == projectedStep.Revision &&
            beforeStep.ReportId == projectedStep.ReportId &&
            beforeStep.ReportLifecycleRevision ==
            projectedStep.ReportLifecycleRevision &&
            beforeStep.ReportLifecycleEntryKey == injectedEntryKey &&
            beforeStep.ReportLifecycleCommandId ==
            projectedStep.ReportLifecycleCommandId &&
            beforeStep.ReportLifecycleStatus ==
            projectedStep.ReportLifecycleStatus &&
            beforeStep.ReportLifecycleIsActive ==
            projectedStep.ReportLifecycleIsActive,
            "Open Draft deliberate drift changed fields beyond the lifecycle entry key.");

        var operationPath =
            $"api/admin/operations/dynamic-flow-runtime/instances/{flowInstanceId}/reconcile";
        var preview = await api.PostAsync(
            $"{operationPath}?apply=false",
            new { },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            preview,
            HttpStatusCode.OK,
            "open Draft reconcile preview");
        Require(
            ApiHarnessClient.RequiredBool(
                preview.Json,
                "exactMaterializationLedgerConverged") &&
            !ApiHarnessClient.RequiredBool(
                preview.Json,
                "exactStateProjectionLedgerConverged") &&
            !ApiHarnessClient.RequiredBool(preview.Json, "converged") &&
            ApiHarnessClient.RequiredInt(
                preview.Json,
                "stateProjectionDriftCount") == 1 &&
            ApiHarnessClient.RequiredInt(
                preview.Json,
                "stateProjectionAppliedCount") == 0,
            "Open Draft reconcile preview did not isolate state/provenance drift.");

        var apply = await api.PostAsync(
            $"{operationPath}?apply=true",
            new { },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            apply,
            HttpStatusCode.OK,
            "open Draft reconcile apply");
        Require(
            ApiHarnessClient.RequiredBool(apply.Json, "converged") &&
            ApiHarnessClient.RequiredBool(
                apply.Json,
                "exactMaterializationLedgerConverged") &&
            ApiHarnessClient.RequiredBool(
                apply.Json,
                "exactStateProjectionLedgerConverged") &&
            ApiHarnessClient.RequiredBool(
                apply.Json,
                "exactLedgerConverged") &&
            ApiHarnessClient.RequiredInt(
                apply.Json,
                "stateProjectionAppliedCount") == 1,
            $"Open Draft reconcile apply did not converge exactly once. Response={apply.Body}");

        var reportAfter = await reports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        var afterStep = await database
            .GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(x => x.Id == stepInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var afterInstance = await database
            .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(x => x.Id == flowInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var stateReceipts = await receipts
            .Find(x =>
                x.FlowInstanceId == flowInstanceId &&
                x.CommandType ==
                DynamicFlowRuntimeStateProjectionOperations.StateReconcile)
            .ToListAsync(ct);
        Require(
            stateReceipts.Count == 1 &&
            await receipts.CountDocumentsAsync(
                x => x.FlowInstanceId == flowInstanceId,
                cancellationToken: ct) == beforeReceiptCount + 1,
            "Open Draft reconcile receipt cardinality drifted.");
        var receipt = stateReceipts.Single();
        var reconcileEvents = await events
            .Find(x =>
                x.FlowInstanceId == flowInstanceId &&
                x.SourceEventKey == receipt.CommandId)
            .ToListAsync(ct);
        var expectedRefs = new[]
        {
            $"assignment:{assignmentId}",
            $"step:{stepInstanceId}",
            $"report:{reportId}"
        };
        Require(
            Hash(reportAfter.ToBsonDocument().ToJson()) == reportHash &&
            afterStep.State == DynamicFlowStepStates.InProgress &&
            afterStep.Revision == beforeStep.Revision + 1 &&
            afterStep.ReportId == reportId &&
            afterStep.ReportLifecycleRevision == 0 &&
            afterStep.ReportLifecycleEntryKey == initDraftEntry.EntryKey &&
            afterStep.ReportLifecycleCommandId == initDraftEntry.CommandId &&
            afterStep.ReportLifecycleStatus ==
            WorkAssignmentReportStatus.Draft.ToString().ToUpperInvariant() &&
            afterStep.ReportLifecycleIsActive == true &&
            afterInstance.Revision == beforeInstance.Revision + 1 &&
            afterInstance.NextEventSequence ==
            beforeInstance.NextEventSequence + 1 &&
            reconcileEvents.Count == 1 &&
            reconcileEvents.Single().EventType ==
            DynamicFlowRuntimeStateProjectionEventTypes.StateReconciled &&
            reconcileEvents.Single().FromState ==
            DynamicFlowStepStates.InProgress &&
            reconcileEvents.Single().ToState ==
            DynamicFlowStepStates.InProgress &&
            reconcileEvents.Single().ReasonCode == "STATE_RECONCILE" &&
            reconcileEvents.Single().AffectedRefs.SequenceEqual(expectedRefs) &&
            reconcileEvents.Single().Payload["sourceReportId"].AsString ==
            reportId &&
            await events.CountDocumentsAsync(
                x => x.FlowInstanceId == flowInstanceId,
                cancellationToken: ct) == beforeEventCount + 1,
            "Open Draft reconcile state/provenance ledger drifted.");

        var readModels = await ValidateStateReadModelConvergenceAsync(
            database,
            workId,
            flowInstanceId,
            stepInstanceId,
            assignmentId,
            periodId,
            reportId,
            WorkAssignmentReportStatus.Draft,
            true,
            true,
            2,
            ct);
        return new
        {
            previewStatus = (int)preview.StatusCode,
            previewMaterializationExact = true,
            previewStateDriftCount = 1,
            applyStatus = (int)apply.StatusCode,
            appliedCount = 1,
            receiptId = receipt.Id,
            eventId = reconcileEvents.Single().Id,
            stepState = afterStep.State,
            stepRevisionDelta = 1,
            instanceRevisionDelta = 1,
            reportLifecycleRevision = 0,
            canonicalReportUnchanged = true,
            affectedRefs = expectedRefs,
            readModels
        };
    }

    private static async Task<object> RunReviewLifecycleCyclesAsync(
        ApiHarnessClient api,
        string adminToken,
        string participantToken,
        IMongoDatabase database,
        string workId,
        string flowInstanceId,
        string stepInstanceId,
        string assignmentId,
        string periodId,
        string reportId,
        string participantActorId,
        string reviewerActorId,
        JsonObject initialApproveRequest,
        CancellationToken ct)
    {
        const string deactivateCommandId = "p5-state-review-deactivate";
        const string reactivateCommandId = "p5-state-review-reactivate";
        const string recallCommandId = "p5-state-review-recall";
        const string returnCommandId =
            BackendServerLease.P3LifecycleProjectionFailureCommandId;
        const string resubmitCommandId = "p5-state-resubmit-after-return";
        const string reapproveCommandId = "p5-state-review-reapprove";
        var replayProofs = new List<object>();

        async Task AssertExactReplayAsync(
            string path,
            JsonObject request,
            string token,
            string operation)
        {
            var before = await CaptureStateLedgerSnapshotAsync(
                database,
                workId,
                flowInstanceId,
                stepInstanceId,
                assignmentId,
                ct);
            var replay = await api.PostAsync(path, request, token, ct: ct);
            ApiHarnessClient.ExpectStatus(
                replay,
                HttpStatusCode.OK,
                $"{operation} exact replay");
            var after = await CaptureStateLedgerSnapshotAsync(
                database,
                workId,
                flowInstanceId,
                stepInstanceId,
                assignmentId,
                ct);
            Require(
                before == after,
                $"{operation} exact replay changed state/revision/event/receipt/read-model persistence.");
            replayProofs.Add(new
            {
                operation,
                status = (int)replay.StatusCode,
                before.StepState,
                before.StepRevision,
                before.InstanceRevision,
                before.NextEventSequence,
                before.ReceiptCount,
                before.EventCount,
                zeroWrite = true
            });
        }

        var approvePath =
            $"api/work-assignment-review/reports/{reportId}/approve";
        await AssertExactReplayAsync(
            approvePath,
            initialApproveRequest,
            adminToken,
            "review approve");

        var reports = database.GetCollection<WorkAssignmentReport>(
            "work_assignment_report");
        var report = await reports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        var deactivateRequest = BuildReviewRequest(
            report,
            deactivateCommandId,
            "P5 approved report deactivate");
        var deactivatePath =
            $"api/work-assignment-review/reports/{reportId}/deactivate";
        var deactivate = await api.PostAsync(
            deactivatePath,
            deactivateRequest,
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            deactivate,
            HttpStatusCode.OK,
            "state projection approved report deactivate");
        var deactivateProjection = await ValidateReportStateProjectionAsync(
            database,
            flowInstanceId,
            stepInstanceId,
            assignmentId,
            reportId,
            deactivateCommandId,
            reviewerActorId,
            DynamicFlowStepStates.Approved,
            DynamicFlowStepStates.Approved,
            [(DynamicFlowStepStates.Approved, DynamicFlowStepStates.Approved)],
            "REPORT_LIFECYCLE_REVIEW_DEACTIVATE_REPORT",
            WorkAssignmentReportStatus.Approved,
            false,
            false,
            ct);
        var deactivatedReadModels = await ValidateStateReadModelConvergenceAsync(
            database,
            workId,
            flowInstanceId,
            stepInstanceId,
            assignmentId,
            periodId,
            reportId,
            WorkAssignmentReportStatus.Approved,
            false,
            false,
            5,
            ct);
        await AssertExactReplayAsync(
            deactivatePath,
            deactivateRequest,
            adminToken,
            "review deactivate");

        report = await reports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        var reactivateRequest = BuildReviewRequest(
            report,
            reactivateCommandId,
            "P5 approved report reactivate");
        var reactivatePath =
            $"api/work-assignment-review/reports/{reportId}/reactivate";
        var reactivate = await api.PostAsync(
            reactivatePath,
            reactivateRequest,
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            reactivate,
            HttpStatusCode.OK,
            "state projection approved report reactivate");
        var reactivateProjection = await ValidateReportStateProjectionAsync(
            database,
            flowInstanceId,
            stepInstanceId,
            assignmentId,
            reportId,
            reactivateCommandId,
            reviewerActorId,
            DynamicFlowStepStates.Approved,
            DynamicFlowStepStates.Approved,
            [(DynamicFlowStepStates.Approved, DynamicFlowStepStates.Approved)],
            "REPORT_LIFECYCLE_REVIEW_REACTIVATE_REPORT",
            WorkAssignmentReportStatus.Approved,
            true,
            true,
            ct);
        var reactivatedReadModels = await ValidateStateReadModelConvergenceAsync(
            database,
            workId,
            flowInstanceId,
            stepInstanceId,
            assignmentId,
            periodId,
            reportId,
            WorkAssignmentReportStatus.Approved,
            true,
            true,
            6,
            ct);
        await AssertExactReplayAsync(
            reactivatePath,
            reactivateRequest,
            adminToken,
            "review reactivate");

        report = await reports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        var recallRequest = BuildReviewRequest(
            report,
            recallCommandId,
            "P5 recall approved report");
        var recallPath =
            $"api/work-assignment-review/reports/{reportId}/recall-approved";
        var recall = await api.PostAsync(
            recallPath,
            recallRequest,
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            recall,
            HttpStatusCode.OK,
            "state projection recall approved report");
        var recallProjection = await ValidateReportStateProjectionAsync(
            database,
            flowInstanceId,
            stepInstanceId,
            assignmentId,
            reportId,
            recallCommandId,
            reviewerActorId,
            DynamicFlowStepStates.Approved,
            DynamicFlowStepStates.Submitted,
            [
                (DynamicFlowStepStates.Approved, DynamicFlowStepStates.Returned),
                (DynamicFlowStepStates.Returned, DynamicFlowStepStates.InProgress),
                (DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Submitted)
            ],
            "REPORT_LIFECYCLE_REVIEW_RECALL_APPROVED",
            WorkAssignmentReportStatus.Submitted,
            true,
            true,
            ct);
        var recalledReadModels = await ValidateStateReadModelConvergenceAsync(
            database,
            workId,
            flowInstanceId,
            stepInstanceId,
            assignmentId,
            periodId,
            reportId,
            WorkAssignmentReportStatus.Submitted,
            true,
            true,
            9,
            ct);
        await AssertExactReplayAsync(
            recallPath,
            recallRequest,
            adminToken,
            "review recall-approved");

        report = await reports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        var returnRequest = BuildReviewRequest(
            report,
            returnCommandId,
            "P5 return report for correction");
        var returnPath =
            $"api/work-assignment-review/reports/{reportId}/return";
        var instanceBeforeReturnRecovery = await database
            .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(x => x.Id == flowInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var recoveryEventCountBefore = await database
            .GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events")
            .CountDocumentsAsync(
                x =>
                    x.FlowInstanceId == flowInstanceId &&
                    (x.EventType ==
                     DynamicFlowRuntimeEventTypes.RecoveryStarted ||
                     x.EventType ==
                     DynamicFlowRuntimeEventTypes.RecoveryCompleted),
                cancellationToken: ct);
        var returned = await api.PostAsync(
            returnPath,
            returnRequest,
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            returned,
            HttpStatusCode.Accepted,
            "state projection injected reviewer return");
        Require(
            ApiHarnessClient.RequiredString(
                returned.Json,
                "lifecycleCommitState") ==
            "COMMITTED_PENDING_PROJECTION" &&
            ApiHarnessClient.RequiredBool(
                returned.Json,
                "lifecycleProjectionPending"),
            "Injected reviewer return did not retain durable lifecycle debt.");
        var pendingReturnedReport = await reports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        var pendingReturnStep = await database
            .GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(x => x.Id == stepInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        Require(
            pendingReturnedReport.Status == WorkAssignmentReportStatus.Draft &&
            pendingReturnedReport.LifecycleProjectionOutbox.Count(entry =>
                entry.CommandId == returnCommandId &&
                entry.State ==
                WorkReportLifecycleProjectionOutboxStates.Pending) == 1 &&
            pendingReturnStep.State == DynamicFlowStepStates.Submitted &&
            instanceBeforeReturnRecovery.State ==
            DynamicFlowInstanceStates.Active,
            "Injected reviewer return did not preserve a healthy runtime around its pending debt.");

        var returnReconcile = await api.PostAsync(
            $"api/admin/operations/dynamic-flow-runtime/instances/{flowInstanceId}/reconcile?apply=true",
            new { },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            returnReconcile,
            HttpStatusCode.OK,
            "pending reviewer return reconcile apply");
        var instanceAfterReturnRecovery = await database
            .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(x => x.Id == flowInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var recoveryEventCountAfter = await database
            .GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events")
            .CountDocumentsAsync(
                x =>
                    x.FlowInstanceId == flowInstanceId &&
                    (x.EventType ==
                     DynamicFlowRuntimeEventTypes.RecoveryStarted ||
                     x.EventType ==
                     DynamicFlowRuntimeEventTypes.RecoveryCompleted),
                cancellationToken: ct);
        Require(
            ApiHarnessClient.RequiredBool(returnReconcile.Json, "converged") &&
            ApiHarnessClient.RequiredBool(
                returnReconcile.Json,
                "exactMaterializationLedgerConverged") &&
            ApiHarnessClient.RequiredBool(
                returnReconcile.Json,
                "exactStateProjectionLedgerConverged") &&
            ApiHarnessClient.RequiredBool(
                returnReconcile.Json,
                "exactLedgerConverged") &&
            ApiHarnessClient.RequiredInt(
                returnReconcile.Json,
                "stateProjectionDriftCount") == 0 &&
            instanceAfterReturnRecovery.State ==
            DynamicFlowInstanceStates.Active &&
            recoveryEventCountAfter == recoveryEventCountBefore,
            "Pending reviewer return reconcile used aggregate PARTIAL/RETRYING recovery or failed exact convergence.");
        var returnProjection = await ValidateReportStateProjectionAsync(
            database,
            flowInstanceId,
            stepInstanceId,
            assignmentId,
            reportId,
            returnCommandId,
            reviewerActorId,
            DynamicFlowStepStates.Submitted,
            DynamicFlowStepStates.Returned,
            [(DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Returned)],
            "REPORT_LIFECYCLE_REVIEW_RETURN",
            WorkAssignmentReportStatus.Draft,
            true,
            true,
            ct);
        var returnedReadModels = await ValidateStateReadModelConvergenceAsync(
            database,
            workId,
            flowInstanceId,
            stepInstanceId,
            assignmentId,
            periodId,
            reportId,
            WorkAssignmentReportStatus.Draft,
            true,
            true,
            10,
            ct);
        await AssertExactReplayAsync(
            returnPath,
            returnRequest,
            adminToken,
            "review return");

        report = await reports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        var resubmitRequest = new JsonObject
        {
            ["expectedPayloadRevision"] = report.PayloadRevision,
            ["expectedLifecycleRevision"] = report.LifecycleRevision,
            ["commandId"] = resubmitCommandId,
            ["values1D"] = new JsonArray(0),
            ["fieldValuesJson"] = new JsonObject
            {
                ["values"] = new JsonObject
                {
                    ["field_note"] = "P5_NOTE"
                }
            }.ToJsonString(),
            ["tableValuesJson"] = null,
            ["dataOrigin"] = "MANUAL_INPUT",
            ["cumulativeContributionMode"] = "INCLUDE"
        };
        var submitPath =
            $"api/work-assignment-reports/{reportId}/submit";
        var resubmit = await api.PostAsync(
            submitPath,
            resubmitRequest,
            participantToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            resubmit,
            HttpStatusCode.OK,
            "state projection resubmit after return");
        var resubmitProjection = await ValidateReportStateProjectionAsync(
            database,
            flowInstanceId,
            stepInstanceId,
            assignmentId,
            reportId,
            resubmitCommandId,
            participantActorId,
            DynamicFlowStepStates.Returned,
            DynamicFlowStepStates.Submitted,
            [
                (DynamicFlowStepStates.Returned, DynamicFlowStepStates.InProgress),
                (DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Submitted)
            ],
            "REPORT_LIFECYCLE_SUBMIT",
            WorkAssignmentReportStatus.Submitted,
            true,
            true,
            ct);
        var resubmittedReadModels = await ValidateStateReadModelConvergenceAsync(
            database,
            workId,
            flowInstanceId,
            stepInstanceId,
            assignmentId,
            periodId,
            reportId,
            WorkAssignmentReportStatus.Submitted,
            true,
            true,
            12,
            ct);
        await AssertExactReplayAsync(
            submitPath,
            resubmitRequest,
            participantToken,
            "report resubmit");

        report = await reports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        var reapproveRequest = BuildReviewRequest(
            report,
            reapproveCommandId,
            "P5 reapprove returned report",
            includeHistoricalConfirmation: true);
        var reapprove = await api.PostAsync(
            approvePath,
            reapproveRequest,
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            reapprove,
            HttpStatusCode.OK,
            "state projection reapprove returned report");
        var reapproveProjection = await ValidateReportStateProjectionAsync(
            database,
            flowInstanceId,
            stepInstanceId,
            assignmentId,
            reportId,
            reapproveCommandId,
            reviewerActorId,
            DynamicFlowStepStates.Submitted,
            DynamicFlowStepStates.Approved,
            [(DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Approved)],
            "REPORT_LIFECYCLE_REVIEW_APPROVE",
            WorkAssignmentReportStatus.Approved,
            true,
            true,
            ct);
        var reapprovedReadModels = await ValidateStateReadModelConvergenceAsync(
            database,
            workId,
            flowInstanceId,
            stepInstanceId,
            assignmentId,
            periodId,
            reportId,
            WorkAssignmentReportStatus.Approved,
            true,
            true,
            13,
            ct);
        await AssertExactReplayAsync(
            approvePath,
            reapproveRequest,
            adminToken,
            "review reapprove");

        return new
        {
            approveReplay = replayProofs[0],
            deactivateStatus = (int)deactivate.StatusCode,
            deactivateProjection,
            deactivatedReadModels,
            reactivateStatus = (int)reactivate.StatusCode,
            reactivateProjection,
            reactivatedReadModels,
            recallStatus = (int)recall.StatusCode,
            recallProjection,
            recalledReadModels,
            returnStatus = (int)returned.StatusCode,
            returnReconcile = new
            {
                status = (int)returnReconcile.StatusCode,
                exactLedgerConverged = true,
                runtimeStateBefore =
                    instanceBeforeReturnRecovery.State,
                runtimeStateAfter =
                    instanceAfterReturnRecovery.State,
                aggregateRecoveryEventDelta =
                    recoveryEventCountAfter - recoveryEventCountBefore
            },
            returnProjection,
            returnedReadModels,
            resubmitStatus = (int)resubmit.StatusCode,
            resubmitProjection,
            resubmittedReadModels,
            reapproveStatus = (int)reapprove.StatusCode,
            reapproveProjection,
            reapprovedReadModels,
            exactReplayProofs = replayProofs
        };

        static JsonObject BuildReviewRequest(
            WorkAssignmentReport current,
            string commandId,
            string comment,
            bool includeHistoricalConfirmation = false)
        {
            var request = new JsonObject
            {
                ["expectedPayloadRevision"] = current.PayloadRevision,
                ["expectedLifecycleRevision"] = current.LifecycleRevision,
                ["commandId"] = commandId,
                ["comment"] = comment
            };
            if (includeHistoricalConfirmation)
                request["confirmHistoricalDataApproval"] = false;
            return request;
        }
    }

    private static async Task<object> RunStateReconcileRaceAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        string workId,
        string flowInstanceId,
        string stepInstanceId,
        string assignmentId,
        string periodId,
        string reportId,
        string actorUserId,
        CancellationToken ct)
    {
        var reports = database.GetCollection<WorkAssignmentReport>(
            "work_assignment_report");
        var report = await reports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        Require(
            report.Status == WorkAssignmentReportStatus.Approved &&
            report.IsActive &&
            report.IsCurrent &&
            report.LifecycleProjectionOutbox.All(entry =>
                entry.State == WorkReportLifecycleProjectionOutboxStates.Completed),
            "State reconcile race requires a canonical Approved report.");
        var reportHashBefore = Hash(report.ToBsonDocument().ToJson());

        var steps = database.GetCollection<DynamicFlowStepInstance>(
            "dynamic_flow_step_instances");
        var beforeStep = await steps
            .Find(x => x.Id == stepInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var beforeInstance = await database
            .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(x => x.Id == flowInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var receipts = database.GetCollection<DynamicFlowRuntimeCommandReceipt>(
            "dynamic_flow_runtime_command_receipts");
        var beforeStateReconcileReceiptIds = (await receipts
                .Find(x =>
                    x.FlowInstanceId == flowInstanceId &&
                    x.CommandType ==
                    DynamicFlowRuntimeStateProjectionOperations.StateReconcile)
                .Project(x => x.Id)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var runtimeEvents = database.GetCollection<DynamicFlowRuntimeEvent>(
            "dynamic_flow_runtime_events");
        var beforeEventCount = await runtimeEvents.CountDocumentsAsync(
            x => x.FlowInstanceId == flowInstanceId,
            cancellationToken: ct);

        var injectedReportId = ObjectId.GenerateNewId().ToString();
        var driftWrite = await steps.UpdateOneAsync(
            x =>
                x.Id == stepInstanceId &&
                x.Revision == beforeStep.Revision &&
                x.State == DynamicFlowStepStates.Approved &&
                !x.IsDeleted,
            Builders<DynamicFlowStepInstance>.Update
                .Set(x => x.ReportId, injectedReportId)
                .Set(
                    x => x.ReportLifecycleRevision,
                    Math.Max(0, report.LifecycleRevision - 1))
                .Set(x => x.ReportLifecycleEntryKey, "p5-injected-state-drift")
                .Set(x => x.ReportLifecycleCommandId, "p5-injected-state-drift")
                .Set(
                    x => x.ReportLifecycleStatus,
                    WorkAssignmentReportStatus.Submitted.ToString()
                        .ToUpperInvariant())
                .Set(x => x.ReportLifecycleIsActive, false),
            cancellationToken: ct);
        Require(
            driftWrite.ModifiedCount == 1,
            "Could not inject the scoped step/report metadata drift.");

        var operationPath =
            $"api/admin/operations/dynamic-flow-runtime/instances/{flowInstanceId}/reconcile";
        var preview = await api.PostAsync(
            $"{operationPath}?apply=false",
            new { },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            preview,
            HttpStatusCode.OK,
            "state reconcile metadata-drift preview");
        Require(
            !ApiHarnessClient.RequiredBool(preview.Json, "converged") &&
            ApiHarnessClient.RequiredBool(
                preview.Json,
                "exactMaterializationLedgerConverged") &&
            !ApiHarnessClient.RequiredBool(
                preview.Json,
                "exactStateProjectionLedgerConverged") &&
            ApiHarnessClient.RequiredInt(
                preview.Json,
                "stateProjectionDriftCount") == 1 &&
            ApiHarnessClient.RequiredInt(
                preview.Json,
                "stateProjectionAppliedCount") == 0 &&
            preview.Body.Contains(assignmentId, StringComparison.Ordinal),
            "State reconcile preview did not isolate the injected metadata drift.");

        var applyA = api.PostAsync(
            $"{operationPath}?apply=true",
            new { },
            adminToken,
            ct: ct);
        var applyB = api.PostAsync(
            $"{operationPath}?apply=true",
            new { },
            adminToken,
            ct: ct);
        await Task.WhenAll(applyA, applyB);
        var applyResponses = new[] { await applyA, await applyB };
        foreach (var (response, index) in applyResponses.Select(
                     (response, index) => (response, index)))
        {
            ApiHarnessClient.ExpectStatus(
                response,
                HttpStatusCode.OK,
                $"concurrent state reconcile apply {index + 1}");
            Require(
                ApiHarnessClient.RequiredBool(response.Json, "converged") &&
                ApiHarnessClient.RequiredBool(
                    response.Json,
                    "exactMaterializationLedgerConverged") &&
                ApiHarnessClient.RequiredBool(
                    response.Json,
                    "exactStateProjectionLedgerConverged") &&
                ApiHarnessClient.RequiredBool(
                    response.Json,
                    "exactLedgerConverged"),
                $"Concurrent state reconcile apply {index + 1} did not return exact convergence.");
        }
        var appliedCounts = applyResponses
            .Select(response => ApiHarnessClient.RequiredInt(
                response.Json,
                "stateProjectionAppliedCount"))
            .ToArray();
        Require(
            appliedCounts.Any(count => count == 1) &&
            appliedCounts.All(count => count is 0 or 1),
            "Concurrent state reconcile did not expose one bounded repair attempt.");

        var finalPreview = await api.PostAsync(
            $"{operationPath}?apply=false",
            new { },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            finalPreview,
            HttpStatusCode.OK,
            "state reconcile final preview");
        Require(
            ApiHarnessClient.RequiredBool(finalPreview.Json, "converged") &&
            ApiHarnessClient.RequiredBool(
                finalPreview.Json,
                "exactMaterializationLedgerConverged") &&
            ApiHarnessClient.RequiredBool(
                finalPreview.Json,
                "exactStateProjectionLedgerConverged") &&
            ApiHarnessClient.RequiredBool(
                finalPreview.Json,
                "exactLedgerConverged") &&
            ApiHarnessClient.RequiredInt(
                finalPreview.Json,
                "stateProjectionDriftCount") == 0,
            "State reconcile final preview did not prove exact ledger convergence.");

        var afterStep = await steps
            .Find(x => x.Id == stepInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var afterInstance = await database
            .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(x => x.Id == flowInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var reportAfter = await reports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        Require(
            Hash(reportAfter.ToBsonDocument().ToJson()) == reportHashBefore,
            "State reconcile changed the canonical P3 report/outbox.");
        var stateReconcileReceipts = await receipts
            .Find(x =>
                x.FlowInstanceId == flowInstanceId &&
                x.CommandType ==
                DynamicFlowRuntimeStateProjectionOperations.StateReconcile)
            .ToListAsync(ct);
        var newReceipts = stateReconcileReceipts
            .Where(item => !beforeStateReconcileReceiptIds.Contains(item.Id))
            .ToArray();
        Require(
            newReceipts.Length == 1,
            "Concurrent state reconcile created duplicate deterministic receipts.");
        var receipt = newReceipts.Single();
        var snapshot = receipt.ResultSnapshot
                       ?? throw new InvalidOperationException(
                           "State reconcile receipt lacks result snapshot.");
        var expectedReceiptId = Hash(string.Join(
            "\n",
            DynamicFlowCommandScopeKinds.Instance,
            flowInstanceId,
            DynamicFlowRuntimeStateProjectionOperations.StateReconcile,
            receipt.CommandId))[..24];
        Require(
            receipt.Id == expectedReceiptId &&
            receipt.ScopeKind == DynamicFlowCommandScopeKinds.Instance &&
            receipt.ScopeId == flowInstanceId &&
            receipt.CommandId.StartsWith(
                $"state-reconcile:{assignmentId}:",
                StringComparison.Ordinal) &&
            receipt.RequestHash ==
            Hash($"{receipt.CommandId}\n{DynamicFlowStepStates.Approved}") &&
            receipt.CommandIdentityHash ==
            Hash(
                $"{actorUserId}\n{assignmentId}\n{receipt.CommandId}\n{receipt.CommandId}") &&
            receipt.Status == DynamicFlowRuntimeCommandStatuses.Succeeded &&
            receipt.ResultSnapshotHash == Hash(snapshot.ToJson()) &&
            snapshot["flowInstanceId"].AsString == flowInstanceId &&
            snapshot["stepInstanceId"].AsString == stepInstanceId &&
            snapshot["assignmentId"].AsString == assignmentId &&
            snapshot["sourceCommandId"].AsString == receipt.CommandId &&
            snapshot["sourceEventKey"].AsString == receipt.CommandId &&
            snapshot["fromState"].AsString == DynamicFlowStepStates.Approved &&
            snapshot["toState"].AsString == DynamicFlowStepStates.Approved &&
            snapshot["fromStepRevision"].ToInt64() == beforeStep.Revision &&
            snapshot["toStepRevision"].ToInt64() == beforeStep.Revision + 1,
            "State reconcile deterministic receipt identity/result drifted.");

        var reconcileEvents = await runtimeEvents
            .Find(x =>
                x.FlowInstanceId == flowInstanceId &&
                x.SourceEventKey == receipt.CommandId)
            .ToListAsync(ct);
        Require(
            reconcileEvents.Count == 1,
            "Concurrent state reconcile created duplicate event chains.");
        var reconcileEvent = reconcileEvents.Single();
        var expectedRefs = new[]
        {
            $"assignment:{assignmentId}",
            $"step:{stepInstanceId}",
            $"report:{reportId}"
        };
        Require(
            reconcileEvent.Id ==
            Hash($"{flowInstanceId}\nstate-event\n{receipt.Id}\n0")[..24] &&
            reconcileEvent.EventType ==
            DynamicFlowRuntimeStateProjectionEventTypes.StateReconciled &&
            reconcileEvent.CommandId == receipt.CommandId &&
            reconcileEvent.CorrelationId == receipt.CommandId &&
            reconcileEvent.SourceEventKey == receipt.CommandId &&
            reconcileEvent.FromState == DynamicFlowStepStates.Approved &&
            reconcileEvent.ToState == DynamicFlowStepStates.Approved &&
            reconcileEvent.FromRevision == beforeStep.Revision &&
            reconcileEvent.ToRevision == beforeStep.Revision + 1 &&
            reconcileEvent.ReasonCode == "STATE_RECONCILE" &&
            reconcileEvent.ActorUserId == actorUserId &&
            reconcileEvent.AffectedRefs.SequenceEqual(expectedRefs) &&
            reconcileEvent.Payload["sourceReportId"].AsString == reportId &&
            reconcileEvent.PayloadHash == Hash(reconcileEvent.Payload.ToJson()),
            "State reconcile event did not reference the canonical report exactly.");
        Require(
            snapshot["eventIds"].AsBsonArray
                .Select(value => value.AsString)
                .SequenceEqual([reconcileEvent.Id]) &&
            afterStep.State == DynamicFlowStepStates.Approved &&
            afterStep.Revision == beforeStep.Revision + 1 &&
            afterStep.ReportId == reportId &&
            afterStep.ReportLifecycleRevision == reportAfter.LifecycleRevision &&
            afterStep.ReportLifecycleEntryKey ==
            reportAfter.LifecycleProjectionOutbox
                .OrderBy(entry => entry.LifecycleRevision)
                .ThenBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.EntryKey, StringComparer.Ordinal)
                .Last()
                .EntryKey &&
            afterStep.ReportLifecycleCommandId ==
            reportAfter.LifecycleProjectionOutbox
                .OrderBy(entry => entry.LifecycleRevision)
                .ThenBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.EntryKey, StringComparer.Ordinal)
                .Last()
                .CommandId &&
            afterStep.ReportLifecycleStatus ==
            WorkAssignmentReportStatus.Approved.ToString().ToUpperInvariant() &&
            afterStep.ReportLifecycleIsActive == true &&
            afterInstance.Revision == beforeInstance.Revision + 1 &&
            afterInstance.NextEventSequence ==
            beforeInstance.NextEventSequence + 1 &&
            await runtimeEvents.CountDocumentsAsync(
                x => x.FlowInstanceId == flowInstanceId,
                cancellationToken: ct) == beforeEventCount + 1,
            "State reconcile did not repair one exact step/instance revision.");

        var convergedReadModels = await ValidateStateReadModelConvergenceAsync(
            database,
            workId,
            flowInstanceId,
            stepInstanceId,
            assignmentId,
            periodId,
            reportId,
            WorkAssignmentReportStatus.Approved,
            true,
            true,
            14,
            ct);
        return new
        {
            injectedDrift = new
            {
                reportId = injectedReportId,
                reportLifecycleRevision =
                    Math.Max(0, report.LifecycleRevision - 1),
                reportLifecycleEntryKey = "p5-injected-state-drift",
                reportLifecycleCommandId = "p5-injected-state-drift",
                reportLifecycleStatus = "SUBMITTED",
                reportLifecycleIsActive = false
            },
            previewStatus = (int)preview.StatusCode,
            previewDriftCount = 1,
            concurrentApplyStatuses = applyResponses
                .Select(response => (int)response.StatusCode)
                .ToArray(),
            concurrentAppliedCounts = appliedCounts,
            receiptId = receipt.Id,
            sourceCommandId = receipt.CommandId,
            receiptDelta = 1,
            eventDelta = 1,
            stepRevisionDelta = 1,
            instanceRevisionDelta = 1,
            canonicalReportUnchanged = true,
            affectedRefs = expectedRefs,
            finalExactLedgerConverged = true,
            convergedReadModels
        };
    }

    private static async Task<object> ValidateReportStateProjectionAsync(
        IMongoDatabase database,
        string flowInstanceId,
        string stepInstanceId,
        string assignmentId,
        string reportId,
        string sourceCommandId,
        string actorUserId,
        string expectedFromState,
        string expectedToState,
        IReadOnlyList<(string From, string To)> expectedTransitions,
        string expectedReasonCode,
        WorkAssignmentReportStatus expectedReportStatus,
        bool expectedReportIsActive,
        bool expectedReportIsCurrent,
        CancellationToken ct)
    {
        Require(
            ObjectId.TryParse(actorUserId, out _),
            $"{sourceCommandId} expected actor is not a server-owned ObjectId.");
        var receipts = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                "dynamic_flow_runtime_command_receipts")
            .Find(x =>
                x.FlowInstanceId == flowInstanceId &&
                x.CommandType ==
                DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle)
            .ToListAsync(ct);
        var receipt = receipts.Single(item =>
            item.ResultSnapshot is { } result &&
            result.TryGetValue("sourceCommandId", out var sourceCommand) &&
            sourceCommand.IsString &&
            sourceCommand.AsString == sourceCommandId);
        var snapshot = receipt.ResultSnapshot
                       ?? throw new InvalidOperationException(
                           $"{sourceCommandId} state receipt lacks a result snapshot.");
        var report = await database
            .GetCollection<WorkAssignmentReport>("work_assignment_report")
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        var lifecycleEntry = report.LifecycleProjectionOutbox.Single(entry =>
            entry.CommandId == sourceCommandId);
        Require(
            report.Status == expectedReportStatus &&
            report.IsActive == expectedReportIsActive &&
            report.IsCurrent == expectedReportIsCurrent &&
            lifecycleEntry.ToStatus?.Trim().ToUpperInvariant() ==
            expectedReportStatus.ToString().ToUpperInvariant() &&
            lifecycleEntry.ToIsActive == expectedReportIsActive,
            $"{sourceCommandId} canonical report lifecycle drifted.");
        var expectedSourceEventKey = Hash(
            $"{report.Id}\n{lifecycleEntry.EntryKey}");
        var expectedReceiptId = Hash(string.Join(
            "\n",
            DynamicFlowCommandScopeKinds.Instance,
            flowInstanceId,
            DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle,
            expectedSourceEventKey))[..24];
        var expectedRequestHash = Hash(string.Join(
            "\n",
            report.Id,
            report.WorkAssignmentId,
            report.WorkReportPeriodId,
            lifecycleEntry.EntryKey,
            lifecycleEntry.CommandId,
            lifecycleEntry.LifecycleRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            lifecycleEntry.Operation?.Trim().ToUpperInvariant(),
            lifecycleEntry.FromStatus?.Trim().ToUpperInvariant(),
            lifecycleEntry.ToStatus?.Trim().ToUpperInvariant(),
            lifecycleEntry.FromIsActive ? "1" : "0",
            lifecycleEntry.ToIsActive ? "1" : "0",
            actorUserId));
        Require(
            receipt.ScopeKind == DynamicFlowCommandScopeKinds.Instance &&
            receipt.ScopeId == flowInstanceId &&
            receipt.Status == DynamicFlowRuntimeCommandStatuses.Succeeded &&
            receipt.Id == expectedReceiptId &&
            receipt.CommandId == expectedSourceEventKey &&
            receipt.RequestHash == expectedRequestHash &&
            receipt.CommandIdentityHash ==
            Hash(
                $"{actorUserId}\n{assignmentId}\n{sourceCommandId}\n{expectedSourceEventKey}") &&
            receipt.UpdatedByUserId == actorUserId &&
            receipt.ResultSnapshotHash == Hash(snapshot.ToJson()) &&
            snapshot["flowInstanceId"].AsString == flowInstanceId &&
            snapshot["stepInstanceId"].AsString == stepInstanceId &&
            snapshot["assignmentId"].AsString == assignmentId &&
            snapshot["sourceEventKey"].AsString == receipt.CommandId &&
            snapshot["sourceCommandId"].AsString == sourceCommandId &&
            snapshot["commandType"].AsString ==
            DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle &&
            snapshot["fromState"].AsString == expectedFromState &&
            snapshot["toState"].AsString == expectedToState &&
            lifecycleEntry.ActorUserId == actorUserId,
            $"{sourceCommandId} state receipt identity/result drifted.");

        var fromRevision = snapshot["fromStepRevision"].ToInt64();
        var toRevision = snapshot["toStepRevision"].ToInt64();
        Require(
            toRevision - fromRevision == expectedTransitions.Count,
            $"{sourceCommandId} state receipt revision delta drifted.");
        var events = await database
            .GetCollection<DynamicFlowRuntimeEvent>("dynamic_flow_runtime_events")
            .Find(x =>
                x.FlowInstanceId == flowInstanceId &&
                x.SourceEventKey == receipt.CommandId)
            .SortBy(x => x.Sequence)
            .ToListAsync(ct);
        Require(
            events.Count == expectedTransitions.Count,
            $"{sourceCommandId} state event cardinality drifted.");
        var expectedRefs = new[]
        {
            $"assignment:{assignmentId}",
            $"step:{stepInstanceId}",
            $"report:{reportId}"
        };
        for (var index = 0; index < expectedTransitions.Count; index++)
        {
            var expected = expectedTransitions[index];
            var runtimeEvent = events[index];
            var expectedEventType = expected.From == expected.To
                ? DynamicFlowRuntimeStateProjectionEventTypes.ReportLifecycleProjected
                : DynamicFlowRuntimeStateProjectionEventTypes.StepStateChanged;
            Require(
                runtimeEvent.EventType == expectedEventType &&
                runtimeEvent.CommandId == receipt.CommandId &&
                runtimeEvent.CorrelationId == sourceCommandId &&
                runtimeEvent.SourceEventKey == receipt.CommandId &&
                runtimeEvent.FromState == expected.From &&
                runtimeEvent.ToState == expected.To &&
                runtimeEvent.FromRevision == fromRevision + index &&
                runtimeEvent.ToRevision == fromRevision + index + 1 &&
                runtimeEvent.ReasonCode == expectedReasonCode &&
                runtimeEvent.ActorUserId == actorUserId &&
                runtimeEvent.AffectedRefs.SequenceEqual(expectedRefs) &&
                runtimeEvent.Id == Hash(
                    $"{flowInstanceId}\nstate-event\n{receipt.Id}\n{index}")[..24] &&
                runtimeEvent.VisibleUnitIds.Count ==
                runtimeEvent.VisibleUnitIds.Distinct(StringComparer.Ordinal).Count() &&
                runtimeEvent.PayloadHash == Hash(runtimeEvent.Payload.ToJson()) &&
                !runtimeEvent.Payload.Contains("fieldValuesJson") &&
                !runtimeEvent.Payload.Contains("tableValuesJson"),
                $"{sourceCommandId} typed state event {index + 1} drifted.");
        }
        Require(
            snapshot["eventIds"].AsBsonArray
                .Select(value => value.AsString)
                .SequenceEqual(events.Select(item => item.Id)),
            $"{sourceCommandId} receipt/event IDs drifted.");

        var step = await database
            .GetCollection<DynamicFlowStepInstance>("dynamic_flow_step_instances")
            .Find(x => x.Id == stepInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        Require(
            step.FlowInstanceId == flowInstanceId &&
            step.AssignmentId == assignmentId &&
            step.ReportId == reportId &&
            step.ReportLifecycleCommandId == sourceCommandId &&
            step.ReportLifecycleEntryKey == lifecycleEntry.EntryKey &&
            step.ReportLifecycleRevision == report.LifecycleRevision &&
            step.ReportLifecycleStatus ==
            expectedReportStatus.ToString().ToUpperInvariant() &&
            step.ReportLifecycleIsActive == expectedReportIsActive &&
            step.State == expectedToState &&
            step.Revision == toRevision,
            $"{sourceCommandId} step projection metadata drifted.");

        return new
        {
            sourceCommandId,
            receiptId = receipt.Id,
            sourceEventKey = receipt.CommandId,
            fromState = expectedFromState,
            toState = expectedToState,
            fromRevision,
            toRevision,
            eventCount = events.Count,
            eventSequences = events.Select(item => item.Sequence).ToArray(),
            actorUserId,
            reasonCode = expectedReasonCode,
            reportStatus = expectedReportStatus.ToString(),
            reportIsActive = expectedReportIsActive,
            reportIsCurrent = expectedReportIsCurrent,
            affectedRefs = expectedRefs,
            receiptCountForInstance = receipts.Count
        };
    }

    private static async Task<object> ValidateStateReadModelConvergenceAsync(
        IMongoDatabase database,
        string workId,
        string flowInstanceId,
        string stepInstanceId,
        string assignmentId,
        string periodId,
        string reportId,
        WorkAssignmentReportStatus expectedReportStatus,
        bool expectedReportIsActive,
        bool expectedReportIsCurrent,
        int expectedStateEvents,
        CancellationToken ct)
    {
        var instance = await database.GetCollection<DynamicFlowInstance>(
                "dynamic_flow_instances")
            .Find(x => x.Id == flowInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var step = await database.GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(x => x.Id == stepInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var assignment = await database.GetCollection<WorkAssignment>("work_assignments")
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .SingleAsync(ct);
        var report = await database
            .GetCollection<WorkAssignmentReport>("work_assignment_report")
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        var period = await database.GetCollection<WorkReportPeriod>("work_report_periods")
            .Find(x => x.Id == periodId && !x.IsDeleted)
            .SingleAsync(ct);
        var queue = await database
            .GetCollection<WorkAssignmentQueueItem>("work_assignment_queue")
            .Find(x => x.WorkAssignmentId == assignmentId && !x.IsDeleted)
            .SingleAsync(ct);
        Require(
            instance.WorkId == workId &&
            instance.State == DynamicFlowInstanceStates.Active &&
            step.FlowInstanceId == instance.Id &&
            step.AssignmentId == assignment.Id &&
            assignment.FlowInstanceId == instance.Id &&
            assignment.WorkId == workId &&
            report.WorkAssignmentId == assignment.Id &&
            report.WorkReportPeriodId == period.Id &&
            report.Status == expectedReportStatus &&
            report.IsActive == expectedReportIsActive &&
            report.IsCurrent == expectedReportIsCurrent &&
            (report.LifecycleRevision == 0
                ? report.LifecycleProjectionOutbox.Count == 1 &&
                  report.LifecycleProjectionOutbox.Count(entry =>
                      entry.CommandId == $"init-draft:{report.Id}" &&
                      entry.LifecycleRevision == 0 &&
                      entry.Operation == "INIT_DRAFT" &&
                      entry.State ==
                      WorkReportLifecycleProjectionOutboxStates.Completed &&
                      entry.FromStatus == "NONE" &&
                      entry.ToStatus ==
                      WorkAssignmentReportStatus.Draft.ToString().ToUpperInvariant() &&
                      !entry.FromIsActive &&
                      entry.ToIsActive) == 1 &&
                  report.LifecycleProjectionLastCompletedRevision == 0
                : report.LifecycleProjectionOutbox.Count > 0 &&
                  report.LifecycleProjectionOutbox.All(entry =>
                      entry.State ==
                      WorkReportLifecycleProjectionOutboxStates.Completed) &&
                  report.LifecycleProjectionLastCompletedRevision ==
                  report.LifecycleRevision) &&
            period.WorkAssignmentId == assignment.Id &&
            period.CurrentReportId ==
            (expectedReportIsActive && expectedReportIsCurrent ? report.Id : null) &&
            (report.LifecycleRevision == 0
                ? period.SourceLifecycleRevision == 0 &&
                  (string.IsNullOrWhiteSpace(period.SourceLifecycleReportId) ||
                   period.SourceLifecycleReportId == report.Id)
                : period.SourceLifecycleReportId == report.Id &&
                  period.SourceLifecycleRevision == report.LifecycleRevision) &&
            queue.WorkAssignmentId == assignment.Id &&
            queue.AssigneeUserId == period.AssigneeUserId &&
            queue.PeriodKey == period.PeriodKey &&
            queue.LastObservedPeriodStatus == (int)period.Status &&
            queue.IsActive ==
            (period.IsActive &&
             !period.IsHistoricalData &&
             tdtd_be.Services.Common.WorkReportPeriodStatusHelper
                 .ShouldKeepQueueActive(period.Status)),
            $"State/read-model source chain drifted at {expectedReportStatus}.");

        var assignmentRoles = await database.GetCollection<DocRole>("doc_roles")
            .Find(role =>
                role.DocType == DocType.WORK_ASSIGNMENT &&
                role.DocId == assignment.Id &&
                !role.IsDeleted)
            .ToListAsync(ct);
        Require(
            assignmentRoles.Count == 2 &&
            assignmentRoles.Count(role => role.Role == DocRoleType.ASSIGNER) == 1 &&
            assignmentRoles.Count(role =>
                role.Role == DocRoleType.ASSIGNEE &&
                role.UserId == period.AssigneeUserId) == 1,
            $"Assignment DocRole ledger drifted at {expectedReportStatus}.");

        var assignmentRows = await database
            .GetCollection<AssignmentListDocRole>("assignment_list_doc_roles")
            .Find(row => row.AssignmentId == assignment.Id && !row.IsDeleted)
            .ToListAsync(ct);
        Require(
            assignmentRows.Count == assignmentRoles.Count &&
            assignmentRows.All(row =>
                row.WorkId == workId &&
                row.FlowInstanceId == instance.Id),
            $"Assignment read model drifted at {expectedReportStatus}.");

        var myReportRows = await database
            .GetCollection<MyReportPeriodListDocRole>(
                "my_report_period_list_doc_roles")
            .Find(row =>
                row.AssignmentId == assignment.Id &&
                row.WorkReportPeriodId == period.Id &&
                !row.IsDeleted)
            .ToListAsync(ct);
        Require(
            myReportRows.Count == 1 &&
            myReportRows.All(row =>
                row.PeriodStatus == period.Status &&
                (expectedReportIsActive && expectedReportIsCurrent
                    ? row.CurrentReportId == report.Id &&
                      row.ReportStatus == report.Status &&
                      row.ReportIsActive == report.IsActive &&
                      row.IsCurrentReport
                    : string.IsNullOrWhiteSpace(row.CurrentReportId) &&
                      row.ReportStatus is null &&
                      !row.ReportIsActive &&
                      !row.IsCurrentReport)),
            $"Assignee report-period read model drifted at {expectedReportStatus}.");

        var reviewRows = await database
            .GetCollection<ReviewReportListDocRole>(
                "review_report_list_doc_roles")
            .Find(row =>
                row.AssignmentId == assignment.Id &&
                row.WorkReportPeriodId == period.Id &&
                !row.IsDeleted)
            .ToListAsync(ct);
        Require(
            reviewRows.Count > 0 &&
            reviewRows.All(row =>
                row.PeriodStatus == period.Status &&
                (expectedReportIsActive && expectedReportIsCurrent
                    ? row.CurrentReportId == report.Id &&
                      row.ReportStatus == report.Status &&
                      row.PayloadRevision == report.PayloadRevision &&
                      row.LifecycleRevision == report.LifecycleRevision &&
                      row.ReportIsActive == report.IsActive
                    : string.IsNullOrWhiteSpace(row.CurrentReportId) &&
                      row.ReportStatus is null &&
                      !row.ReportIsActive)),
            $"Reviewer report read model drifted at {expectedReportStatus}.");

        var events = await database
            .GetCollection<DynamicFlowRuntimeEvent>("dynamic_flow_runtime_events")
            .Find(x => x.FlowInstanceId == instance.Id)
            .SortBy(x => x.Sequence)
            .ToListAsync(ct);
        Require(
            events.Count > 0 &&
            events.Select(item => item.Sequence).Distinct().Count() == events.Count &&
            events.Select(item => item.Sequence)
                .SequenceEqual(Enumerable.Range(1, events.Count).Select(value => (long)value)) &&
            instance.NextEventSequence == events[^1].Sequence + 1 &&
            events.All(item => item.PayloadHash == Hash(item.Payload.ToJson())),
            $"Runtime event chain drifted at {expectedReportStatus}.");
        var stateEvents = events.Count(item =>
            DynamicFlowRuntimeStateProjectionEventTypes.All.Contains(item.EventType));
        Require(
            stateEvents == expectedStateEvents,
            $"Runtime state event count drifted at {expectedReportStatus}.");

        return new
        {
            reportStatus = expectedReportStatus.ToString(),
            reportIsActive = expectedReportIsActive,
            reportIsCurrent = expectedReportIsCurrent,
            reportLifecycleRevision = report.LifecycleRevision,
            periodStatus = period.Status.ToString(),
            periodSourceLifecycleRevision = period.SourceLifecycleRevision,
            queueActive = queue.IsActive,
            queueObservedPeriodStatus = queue.LastObservedPeriodStatus,
            assignmentProgressStatus = assignment.ProgressStatus.ToString(),
            assignmentDocRoleCount = assignmentRoles.Count,
            assignmentReadModelCount = assignmentRows.Count,
            myReportReadModelCount = myReportRows.Count,
            reviewReadModelCount = reviewRows.Count,
            runtimeEventCount = events.Count,
            runtimeStateEventCount = stateEvents,
            nextEventSequence = instance.NextEventSequence
        };
    }

    private static async Task<T01CompletionPersistenceSnapshot> ValidateT01CompletionAsync(
        IMongoDatabase database,
        string workId,
        string flowInstanceId,
        string stepInstanceId,
        string assignmentId,
        string periodId,
        string reportId,
        string actorUserId,
        CancellationToken ct)
    {
        const string completionReason = "ASSIGNMENT_COMPLETED";
        const string instanceCompletionReason = "ALL_RUNTIME_STEPS_COMPLETED";
        var sourceCommandId = $"assignment-completed:{assignmentId}";
        var instance = await database
            .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(x => x.Id == flowInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var step = await database
            .GetCollection<DynamicFlowStepInstance>("dynamic_flow_step_instances")
            .Find(x => x.Id == stepInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var assignment = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .SingleAsync(ct);
        var period = await database
            .GetCollection<WorkReportPeriod>("work_report_periods")
            .Find(x => x.Id == periodId && !x.IsDeleted)
            .SingleAsync(ct);
        var report = await database
            .GetCollection<WorkAssignmentReport>("work_assignment_report")
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .SingleAsync(ct);
        var queue = await database
            .GetCollection<WorkAssignmentQueueItem>("work_assignment_queue")
            .Find(x => x.WorkAssignmentId == assignmentId && !x.IsDeleted)
            .SingleAsync(ct);

        Require(
            assignment.WorkId == workId &&
            assignment.FlowInstanceId == flowInstanceId &&
            assignment.CompletedAtUtc.HasValue &&
            assignment.CompletedDate.HasValue &&
            assignment.CompletedByUserId == actorUserId &&
            assignment.ProgressStatus == (int)WorkAssignmentProgressStatus.Completed &&
            assignment.ProgressStatusUpdatedAtUtc.HasValue &&
            assignment.IsActive,
            "T01 completed assignment terminal fields drifted.");
        var assignmentCompletedAtUtc = assignment.CompletedAtUtc
                                       ?? throw new InvalidOperationException(
                                           "T01 completed assignment lacks completedAtUtc.");
        var assignmentCompletedDate = assignment.CompletedDate
                                      ?? throw new InvalidOperationException(
                                          "T01 completed assignment lacks completedDate.");
        var assignmentCompletedByUserId = assignment.CompletedByUserId
                                          ?? throw new InvalidOperationException(
                                              "T01 completed assignment lacks completedByUserId.");
        Require(
            report.Status == WorkAssignmentReportStatus.Approved &&
            report.IsActive &&
            report.IsCurrent &&
            period.WorkAssignmentId == assignmentId &&
            period.CurrentReportId == reportId &&
            period.SourceLifecycleReportId == reportId &&
            period.SourceLifecycleRevision == report.LifecycleRevision &&
            period.Status is
                WorkReportPeriodStatus.Approved or
                WorkReportPeriodStatus.OverdueApproved &&
            period.IsActive &&
            queue.WorkAssignmentId == assignmentId &&
            queue.LastObservedPeriodStatus == (int)period.Status &&
            !queue.IsActive,
            "T01 completion report-period/queue source chain drifted.");
        Require(
            step.FlowInstanceId == instance.Id &&
            step.AssignmentId == assignment.Id &&
            step.ReportId == report.Id &&
            step.ReportLifecycleRevision == report.LifecycleRevision &&
            step.ReportLifecycleStatus ==
            WorkAssignmentReportStatus.Approved.ToString().ToUpperInvariant() &&
            step.ReportLifecycleIsActive == true &&
            step.State == DynamicFlowStepStates.Completed &&
            instance.WorkId == workId &&
            instance.State == DynamicFlowInstanceStates.Completed &&
            instance.CompletedAtUtc.HasValue,
            "T01 completion did not terminalize the sole runtime step/instance.");

        var receipts = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                "dynamic_flow_runtime_command_receipts")
            .Find(x => x.FlowInstanceId == flowInstanceId)
            .ToListAsync(ct);
        var completionReceipts = receipts
            .Where(x =>
                x.CommandType ==
                DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete)
            .ToArray();
        Require(
            completionReceipts.Length == 1,
            "T01 completion receipt cardinality drifted.");
        var receipt = completionReceipts.Single();
        var snapshot = receipt.ResultSnapshot
                       ?? throw new InvalidOperationException(
                           "T01 completion receipt lacks result snapshot.");
        var expectedReceiptId = Hash(string.Join(
            "\n",
            DynamicFlowCommandScopeKinds.Instance,
            flowInstanceId,
            DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete,
            sourceCommandId))[..24];
        var completedAtToken = assignmentCompletedAtUtc
            .ToUniversalTime()
            .ToString("O");
        var completedDateToken = assignmentCompletedDate
            .ToUniversalTime()
            .ToString("O");
        var expectedRequestHash = Hash(string.Join(
            "\n",
            assignmentId,
            completedAtToken,
            completedDateToken,
            actorUserId));
        var expectedProjectionFingerprint = Hash(string.Join(
            "\n",
            "assignment-completion",
            assignmentId,
            completedAtToken,
            expectedRequestHash));
        Require(
            receipt.Id == expectedReceiptId &&
            receipt.ScopeKind == DynamicFlowCommandScopeKinds.Instance &&
            receipt.ScopeId == flowInstanceId &&
            receipt.WorkId == workId &&
            receipt.CommandType ==
            DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete &&
            receipt.CommandId == sourceCommandId &&
            receipt.RequestHash == expectedRequestHash &&
            receipt.CommandIdentityHash ==
            Hash(
                $"{actorUserId}\n{assignmentId}\n{sourceCommandId}\n{sourceCommandId}") &&
            receipt.Status == DynamicFlowRuntimeCommandStatuses.Succeeded &&
            receipt.UpdatedByUserId == actorUserId &&
            receipt.ResultSnapshotHash == Hash(snapshot.ToJson()) &&
            snapshot["flowInstanceId"].AsString == flowInstanceId &&
            snapshot["stepInstanceId"].AsString == stepInstanceId &&
            snapshot["assignmentId"].AsString == assignmentId &&
            snapshot["sourceEventKey"].AsString == sourceCommandId &&
            snapshot["sourceCommandId"].AsString == sourceCommandId &&
            snapshot["commandType"].AsString ==
            DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete &&
            snapshot["fromState"].AsString == DynamicFlowStepStates.Approved &&
            snapshot["toState"].AsString == DynamicFlowStepStates.Completed &&
            snapshot["projectionFingerprint"].AsString ==
            expectedProjectionFingerprint &&
            snapshot["instanceCompleted"].AsBoolean,
            "T01 completion receipt identity/result drifted.");

        var fromStepRevision = snapshot["fromStepRevision"].ToInt64();
        var toStepRevision = snapshot["toStepRevision"].ToInt64();
        var fromInstanceRevision = snapshot["fromInstanceRevision"].ToInt64();
        var toInstanceRevision = snapshot["toInstanceRevision"].ToInt64();
        Require(
            receipt.ExpectedRevision == fromStepRevision &&
            toStepRevision == fromStepRevision + 1 &&
            step.Revision == toStepRevision &&
            toInstanceRevision == fromInstanceRevision + 1 &&
            instance.Revision == toInstanceRevision,
            "T01 completion CAS revision chain drifted.");

        var completionEvents = await database
            .GetCollection<DynamicFlowRuntimeEvent>("dynamic_flow_runtime_events")
            .Find(x =>
                x.FlowInstanceId == flowInstanceId &&
                x.SourceEventKey == sourceCommandId)
            .SortBy(x => x.Sequence)
            .ToListAsync(ct);
        Require(
            completionEvents.Count == 2,
            "T01 completion event cardinality drifted.");
        var affectedRefs = new[]
        {
            $"assignment:{assignmentId}",
            $"step:{stepInstanceId}"
        };
        var stepEvent = completionEvents[0];
        var instanceEvent = completionEvents[1];
        Require(
            stepEvent.Id ==
            Hash($"{flowInstanceId}\nstate-event\n{receipt.Id}\n0")[..24] &&
            stepEvent.StepInstanceId == stepInstanceId &&
            stepEvent.EventType ==
            DynamicFlowRuntimeStateProjectionEventTypes.StepStateChanged &&
            stepEvent.CommandId == sourceCommandId &&
            stepEvent.CorrelationId == sourceCommandId &&
            stepEvent.FromState == DynamicFlowStepStates.Approved &&
            stepEvent.ToState == DynamicFlowStepStates.Completed &&
            stepEvent.FromRevision == fromStepRevision &&
            stepEvent.ToRevision == toStepRevision &&
            stepEvent.ReasonCode == completionReason &&
            stepEvent.ActorUserId == actorUserId &&
            stepEvent.AffectedRefs.SequenceEqual(affectedRefs) &&
            stepEvent.PayloadHash == Hash(stepEvent.Payload.ToJson()) &&
            !stepEvent.Payload.Contains("fieldValuesJson") &&
            !stepEvent.Payload.Contains("tableValuesJson"),
            "T01 assignment-completed step event drifted.");
        Require(
            instanceEvent.Id ==
            Hash(
                $"{flowInstanceId}\nstate-event\n{receipt.Id}\ninstance-completed")[..24] &&
            instanceEvent.StepInstanceId is null &&
            instanceEvent.EventType ==
            DynamicFlowRuntimeStateProjectionEventTypes.InstanceCompleted &&
            instanceEvent.CommandId == sourceCommandId &&
            instanceEvent.CorrelationId == sourceCommandId &&
            instanceEvent.FromState == DynamicFlowInstanceStates.Active &&
            instanceEvent.ToState == DynamicFlowInstanceStates.Completed &&
            instanceEvent.FromRevision == fromInstanceRevision &&
            instanceEvent.ToRevision == toInstanceRevision &&
            instanceEvent.ReasonCode == instanceCompletionReason &&
            instanceEvent.ActorUserId == actorUserId &&
            instanceEvent.AffectedRefs.SequenceEqual(affectedRefs) &&
            instanceEvent.Sequence == stepEvent.Sequence + 1 &&
            instanceEvent.PayloadHash == Hash(instanceEvent.Payload.ToJson()),
            "T01 instance-completed event drifted.");
        Require(
            snapshot["eventIds"].AsBsonArray
                .Select(value => value.AsString)
                .SequenceEqual(completionEvents.Select(item => item.Id)),
            "T01 completion receipt/event IDs drifted.");

        var allEvents = await database
            .GetCollection<DynamicFlowRuntimeEvent>("dynamic_flow_runtime_events")
            .Find(x => x.FlowInstanceId == flowInstanceId)
            .SortBy(x => x.Sequence)
            .ToListAsync(ct);
        Require(
            allEvents.Select(item => item.Sequence)
                .SequenceEqual(
                    Enumerable.Range(1, allEvents.Count)
                        .Select(value => (long)value)) &&
            instance.NextEventSequence == allEvents.Count + 1 &&
            allEvents.All(item =>
                item.PayloadHash == Hash(item.Payload.ToJson())),
            "T01 completion broke the append-only event sequence.");

        var assignmentRoles = await database
            .GetCollection<DocRole>("doc_roles")
            .Find(role =>
                role.DocType == DocType.WORK_ASSIGNMENT &&
                role.DocId == assignmentId &&
                !role.IsDeleted)
            .ToListAsync(ct);
        Require(
            assignmentRoles.Count == 2 &&
            assignmentRoles.Count(role =>
                role.Role == DocRoleType.ASSIGNER &&
                role.UserId == actorUserId) == 1 &&
            assignmentRoles.Count(role =>
                role.Role == DocRoleType.ASSIGNEE &&
                role.UserId == report.AssigneeUserId) == 1,
            "T01 completion changed the canonical assignment DocRole ledger.");
        var assignmentRows = await database
            .GetCollection<AssignmentListDocRole>(
                "assignment_list_doc_roles")
            .Find(row =>
                row.AssignmentId == assignmentId &&
                !row.IsDeleted)
            .ToListAsync(ct);
        Require(
            assignmentRows.Count == assignmentRoles.Count &&
            assignmentRows.All(row =>
                row.WorkId == workId &&
                row.FlowInstanceId == flowInstanceId &&
                row.CompletedAtUtc == assignment.CompletedAtUtc &&
                row.CompletedDate == assignment.CompletedDate &&
                row.CompletedByUserId == assignment.CompletedByUserId &&
                row.ProgressStatus == assignment.ProgressStatus &&
                row.ProgressStatusUpdatedAtUtc ==
                assignment.ProgressStatusUpdatedAtUtc &&
                row.IsActive == assignment.IsActive &&
                row.Roles.OrderBy(role => (int)role).SequenceEqual(
                    assignmentRoles
                        .Where(role => role.UserId == row.UserId)
                        .Select(role => role.Role)
                        .OrderBy(role => (int)role))),
            "T01 completion assignment read model does not exactly mirror source/roles.");

        var myReportRows = await database
            .GetCollection<MyReportPeriodListDocRole>(
                "my_report_period_list_doc_roles")
            .Find(row =>
                row.AssignmentId == assignmentId &&
                row.WorkReportPeriodId == periodId &&
                !row.IsDeleted)
            .ToListAsync(ct);
        Require(
            myReportRows.Count == 1 &&
            myReportRows.Single().UserId == report.AssigneeUserId &&
            myReportRows.Single().CurrentReportId == reportId &&
            myReportRows.Single().PeriodStatus == period.Status &&
            myReportRows.Single().ReportStatus == report.Status &&
            myReportRows.Single().ReportIsActive == report.IsActive,
            "T01 completion assignee period read model drifted.");
        var reviewRows = await database
            .GetCollection<ReviewReportListDocRole>(
                "review_report_list_doc_roles")
            .Find(row =>
                row.AssignmentId == assignmentId &&
                row.WorkReportPeriodId == periodId &&
                !row.IsDeleted)
            .ToListAsync(ct);
        Require(
            reviewRows.Count == 1 &&
            reviewRows.Single().ReviewerUserId == actorUserId &&
            reviewRows.Single().CurrentReportId == reportId &&
            reviewRows.Single().PeriodStatus == period.Status &&
            reviewRows.Single().ReportStatus == report.Status &&
            reviewRows.Single().PayloadRevision == report.PayloadRevision &&
            reviewRows.Single().LifecycleRevision == report.LifecycleRevision &&
            reviewRows.Single().ReportIsActive == report.IsActive &&
            reviewRows.Single().ProgressStatus == assignment.ProgressStatus &&
            reviewRows.Single().ProgressStatusUpdatedAtUtc ==
            assignment.ProgressStatusUpdatedAtUtc,
            "T01 completion reviewer period read model drifted.");

        return new T01CompletionPersistenceSnapshot(
            assignmentCompletedAtUtc,
            assignmentCompletedDate,
            assignmentCompletedByUserId,
            assignment.ProgressStatus,
            step.State,
            step.Revision,
            instance.State,
            instance.Revision,
            instance.NextEventSequence,
            receipts.Count,
            allEvents.Count,
            completionEvents.Count,
            queue.IsActive,
            assignmentRoles.Count,
            assignmentRows.Count,
            myReportRows.Count,
            reviewRows.Count,
            receipt.Id);
    }

    private static async Task<string> CaptureStateFlowHashAsync(
        IMongoDatabase database,
        string workId,
        string flowInstanceId,
        string assignmentId,
        CancellationToken ct)
    {
        var workObjectId = ObjectId.Parse(workId);
        var instanceObjectId = ObjectId.Parse(flowInstanceId);
        var assignmentObjectId = ObjectId.Parse(assignmentId);
        var rows = new List<string>();
        var bf = Builders<BsonDocument>.Filter;
        await AddCollectionAsync(
            "works",
            bf.Eq("_id", workObjectId));
        await AddCollectionAsync(
            "dynamic_flow_instances",
            bf.Eq("_id", instanceObjectId));
        await AddCollectionAsync(
            "dynamic_flow_step_instances",
            bf.Eq("flowInstanceId", instanceObjectId));
        await AddCollectionAsync(
            "dynamic_flow_runtime_command_receipts",
            bf.Eq("flowInstanceId", instanceObjectId));
        await AddCollectionAsync(
            "dynamic_flow_runtime_events",
            bf.Eq("flowInstanceId", instanceObjectId));
        await AddCollectionAsync(
            "dynamic_flow_runtime_outbox",
            bf.Eq("flowInstanceId", instanceObjectId));
        await AddCollectionAsync(
            "work_assignments",
            bf.Eq("_id", assignmentObjectId));
        await AddCollectionAsync(
            "work_template_assignees",
            bf.Eq("workAssignmentId", assignmentObjectId));
        await AddCollectionAsync(
            "work_report_periods",
            bf.Eq("workAssignmentId", assignmentObjectId));
        await AddCollectionAsync(
            "work_assignment_queue",
            bf.Eq("workAssignmentId", assignmentObjectId));
        await AddCollectionAsync(
            "work_assignment_report",
            bf.Eq("workAssignmentId", assignmentObjectId));
        await AddCollectionAsync(
            "work_assignment_report_sections",
            bf.Eq("workAssignmentId", assignmentObjectId));
        await AddCollectionAsync(
            "work_assignment_report_logs",
            bf.Eq("workAssignmentId", assignmentObjectId));
        await AddCollectionAsync(
            "user_action_logs",
            bf.Eq("workAssignmentId", assignmentObjectId));
        await AddCollectionAsync(
            "work_status_operation_logs",
            bf.Eq("workAssignmentId", assignmentId));
        await AddCollectionAsync(
            "doc_roles",
            bf.Or(
                bf.Eq("docId", assignmentObjectId),
                bf.Eq("docId", workObjectId)));
        await AddCollectionAsync(
            "assignment_list_doc_roles",
            bf.Eq("assignmentId", assignmentObjectId));
        await AddCollectionAsync(
            "my_report_period_list_doc_roles",
            bf.Eq("assignmentId", assignmentObjectId));
        await AddCollectionAsync(
            "review_report_list_doc_roles",
            bf.Eq("assignmentId", assignmentObjectId));
        return Hash(string.Join("\n", rows));

        async Task AddCollectionAsync(
            string collectionName,
            FilterDefinition<BsonDocument> filter)
        {
            var documents = await database.GetCollection<BsonDocument>(collectionName)
                .Find(filter)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .ToListAsync(ct);
            rows.Add(collectionName);
            rows.AddRange(documents.Select(document => document.ToJson()));
        }
    }

    private static async Task<StateLedgerSnapshot> CaptureStateLedgerSnapshotAsync(
        IMongoDatabase database,
        string workId,
        string flowInstanceId,
        string stepInstanceId,
        string assignmentId,
        CancellationToken ct)
    {
        var step = await database.GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(x => x.Id == stepInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var instance = await database.GetCollection<DynamicFlowInstance>(
                "dynamic_flow_instances")
            .Find(x => x.Id == flowInstanceId && !x.IsDeleted)
            .SingleAsync(ct);
        var receiptCount = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                "dynamic_flow_runtime_command_receipts")
            .CountDocumentsAsync(
                x => x.FlowInstanceId == flowInstanceId,
                cancellationToken: ct);
        var eventCount = await database
            .GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events")
            .CountDocumentsAsync(
                x => x.FlowInstanceId == flowInstanceId,
                cancellationToken: ct);
        var flowHash = await CaptureStateFlowHashAsync(
            database,
            workId,
            flowInstanceId,
            assignmentId,
            ct);
        return new StateLedgerSnapshot(
            step.State,
            step.Revision,
            instance.State,
            instance.Revision,
            instance.NextEventSequence,
            receiptCount,
            eventCount,
            flowHash);
    }

    private static async Task<LiveParticipantMutation> MutateLiveParticipantMetadataAsync(
        IMongoDatabase database,
        string targetUnitId,
        CancellationToken ct)
    {
        var users = database.GetCollection<AppUser>("users");
        var units = database.GetCollection<Unit>("units");
        var user = await users
            .Find(item => item.UnitId == targetUnitId && !item.IsDeleted)
            .SingleAsync(ct);
        var unit = await units
            .Find(item => item.Id == targetUnitId && !item.IsDeleted)
            .SingleAsync(ct);
        var mutatedUserName = $"{user.FullName} LIVE-MUTATED";
        var mutatedUnitName = $"{unit.FullName} LIVE-MUTATED";
        var mutatedUnitShortName = $"{unit.ShortName}-M";
        var now = DateTime.UtcNow;
        await users.UpdateOneAsync(
            item => item.Id == user.Id,
            Builders<AppUser>.Update
                .Set(item => item.FullName, mutatedUserName)
                .Set(item => item.UpdatedAtUtc, now),
            cancellationToken: ct);
        await units.UpdateOneAsync(
            item => item.Id == unit.Id,
            Builders<Unit>.Update
                .Set(item => item.FullName, mutatedUnitName)
                .Set(item => item.ShortName, mutatedUnitShortName)
                .Set(item => item.UpdatedAtUtc, now)
                .Inc(item => item.Version, 1),
            cancellationToken: ct);
        return new LiveParticipantMutation(
            targetUnitId,
            user.Id,
            user.FullName,
            mutatedUserName,
            unit.FullName,
            mutatedUnitName,
            unit.ShortName,
            mutatedUnitShortName);
    }

    private static async Task<object> ValidateDurableParticipantReplayAsync(
        IMongoDatabase database,
        string flowInstanceId,
        LiveParticipantMutation mutation,
        CancellationToken ct)
    {
        var liveUser = await database.GetCollection<AppUser>("users")
            .Find(item => item.Id == mutation.UserId)
            .SingleAsync(ct);
        var liveUnit = await database.GetCollection<Unit>("units")
            .Find(item => item.Id == mutation.TargetUnitId)
            .SingleAsync(ct);
        var snapshot = await database
            .GetCollection<DynamicFlowParticipantSnapshot>("dynamic_flow_participant_snapshots")
            .Find(item => item.FlowInstanceId == flowInstanceId)
            .SingleAsync(ct);
        var participant = snapshot.Bindings
            .Single(binding => binding.TargetUnitId == mutation.TargetUnitId)
            .Participants
            .Single(item => item.UserId == mutation.UserId);
        var assignments = await database.GetCollection<WorkAssignment>("work_assignments")
            .Find(item => item.FlowInstanceId == flowInstanceId && !item.IsDeleted)
            .ToListAsync(ct);
        var assignmentParticipant = assignments
            .SelectMany(item => item.Assignees)
            .Single(item => item.UserId == mutation.UserId);
        var assignmentIds = assignments.Select(item => item.Id).ToArray();
        var bindings = await database.GetCollection<WorkTemplateAssignee>("work_template_assignees")
            .Find(item =>
                assignmentIds.Contains(item.WorkAssignmentId) &&
                !item.IsDeleted)
            .ToListAsync(ct);
        var binding = bindings.Single(item => item.AssigneeUserId == mutation.UserId);
        Require(
            liveUser.FullName == mutation.MutatedUserFullName &&
            liveUnit.FullName == mutation.MutatedUnitFullName &&
            liveUnit.ShortName == mutation.MutatedUnitShortName,
            "Live participant/unit metadata mutation was not durable.");
        Require(
            participant.FullName == mutation.OriginalUserFullName &&
            participant.UnitName == mutation.OriginalUnitFullName &&
            participant.UnitShortName == mutation.OriginalUnitShortName &&
            assignmentParticipant.FullName == mutation.OriginalUserFullName &&
            assignmentParticipant.UnitName == mutation.OriginalUnitFullName &&
            binding.AssigneeFullName == mutation.OriginalUserFullName &&
            binding.AssigneeUnitName == mutation.OriginalUnitFullName,
            "Exact replay re-resolved live participant metadata instead of durable snapshot data.");
        Require(
            assignments.Count == 1 && bindings.Count == 1,
            "Exact replay after live metadata mutation created duplicate side effects.");

        var rawReceipt = await database
            .GetCollection<BsonDocument>("dynamic_flow_runtime_command_receipts")
            .Find(new BsonDocument(
                "flowInstanceId",
                ObjectId.Parse(flowInstanceId)))
            .SingleAsync(ct);
        Require(
            rawReceipt.TryGetValue("commandIdentityHash", out var commandIdentityHash) &&
            commandIdentityHash.IsString &&
            commandIdentityHash.AsString.Length == 64 &&
            rawReceipt.TryGetValue("snapshotToken", out var snapshotToken) &&
            snapshotToken.IsString &&
            !string.IsNullOrWhiteSpace(snapshotToken.AsString),
            "Durable receipt lacks commandIdentityHash/snapshotToken replay proof.");
        return new
        {
            liveUserFullName = liveUser.FullName,
            frozenUserFullName = participant.FullName,
            liveUnitName = liveUnit.FullName,
            frozenUnitName = participant.UnitName,
            assignmentCount = assignments.Count,
            bindingCount = bindings.Count,
            commandIdentityHash = commandIdentityHash.AsString,
            durableSnapshotTokenPresent = true
        };
    }

    private static async Task<ProbePersistenceSnapshot> ValidateConvergedAsync(
        IMongoDatabase database,
        ProbeFixture fixture,
        string expectedWorkId,
        string commandId,
        string versionId,
        string archetypeId,
        IReadOnlyCollection<string> expectedTargetUnitIds,
        string expectedPeriodKey,
        bool expectRecoveryEvents,
        CancellationToken ct)
    {
        var expectedTargets = expectedTargetUnitIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var targetCount = expectedTargets.Length;
        var receipts = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>("dynamic_flow_runtime_command_receipts")
            .Find(x => x.CommandId == commandId)
            .ToListAsync(ct);
        Require(receipts.Count == 1, $"{commandId} receipt cardinality mismatch.");
        var receipt = receipts.Single();
        Require(receipt.Status == DynamicFlowRuntimeCommandStatuses.Succeeded, $"{commandId} receipt did not succeed.");
        Require(receipt.WorkId == expectedWorkId, $"{commandId} receipt work pin drifted.");
        Require(receipt.FlowTemplateVersionId == versionId, $"{commandId} receipt Flow pin drifted.");
        Require(receipt.RequestHash.Length == 64, $"{commandId} request hash is missing.");
        Require(
            receipt.ResultSnapshot is not null &&
            receipt.ResultSnapshotHash == Hash(receipt.ResultSnapshot.ToJson()),
            $"{commandId} terminal result snapshot/hash drifted.");
        Require(receipt.CompletedAtUtc is not null, $"{commandId} receipt lacks terminal timestamp.");
        var instanceId = receipt.FlowInstanceId
                         ?? throw new InvalidOperationException($"{commandId} receipt lacks flowInstanceId.");
        var instance = await database.GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(x => x.Id == instanceId)
            .SingleAsync(ct);
        Require(instance.State == DynamicFlowInstanceStates.Active, $"{commandId} instance is not ACTIVE.");
        Require(instance.WorkId == expectedWorkId, $"{commandId} work pin drifted.");
        Require(instance.WorkType == WorkType.TASK.ToString(), $"{commandId} WorkType drifted.");
        Require(instance.FlowTemplateVersionId == versionId, $"{commandId} Flow version pin drifted.");
        Require(instance.FlowPayloadHash.Length == 64, $"{commandId} Flow payload hash is missing.");
        Require(instance.PeriodKey == expectedPeriodKey, $"{commandId} period identity drifted.");
        var expectedScheduleJson =
            "{\"anchor\":\"2026-07-23\",\"cadence\":\"once\",\"timezone\":\"Asia/Ho_Chi_Minh\"}";
        Require(
            instance.ScheduleIdentityHash == Hash(expectedScheduleJson),
            $"{commandId} schedule identity hash drifted.");
        Require(
            instance.CatalogVersion == DynamicFlowRuntimeCatalogCandidate.Version &&
            instance.CatalogSemanticHash == DynamicFlowRuntimeCatalogCandidate.SemanticHash,
            $"{commandId} candidate catalog pin drifted.");
        var expectedCommandIdentityHash =
            DynamicFlowRuntimePreflightContract.BuildCommandIdentityHash(
                expectedWorkId,
                versionId,
                new DynamicFlowPreflightRequest
                {
                    CommandId = commandId,
                    TargetUnitIds = expectedTargets.ToList(),
                    PeriodKey = expectedPeriodKey,
                    ScheduleIdentityJson =
                        "{\"timezone\":\"Asia/Ho_Chi_Minh\",\"cadence\":\"once\",\"anchor\":\"2026-07-23\"}"
                },
                instance.IssuerUserId);
        Require(
            receipt.CommandIdentityHash == expectedCommandIdentityHash,
            $"{commandId} exact command identity hash drifted.");

        var steps = await database.GetCollection<DynamicFlowStepInstance>("dynamic_flow_step_instances")
            .Find(x => x.FlowInstanceId == instanceId && !x.IsDeleted)
            .ToListAsync(ct);
        Require(steps.Count == targetCount, $"{commandId} step count mismatch.");
        Require(
            steps.Select(step => step.TargetUnitId)
                .OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(expectedTargets),
            $"{commandId} target-unit exact set drifted.");
        Require(
            steps.Select(step =>
                    $"{step.FlowInstanceId}:{step.FlowStepId}:{step.TargetUnitId}:{step.AttemptNo}")
                .Distinct(StringComparer.Ordinal)
                .Count() == steps.Count,
            $"{commandId} duplicate (instance,flowStep,targetUnit,attempt) business key.");
        Require(
            steps.All(step =>
                step.State == DynamicFlowStepStates.Assigned &&
                step.FormVersionId == fixture.FormVersionId &&
                step.FormFamilyId == fixture.FormFamilyId &&
                step.FormVersionNo == fixture.FormVersionNo &&
                step.FormSchemaHash == fixture.FormSchemaHash &&
                step.FormSnapshotHash == fixture.FormSchemaHash &&
                step.ParticipantSnapshotId == instance.ParticipantSnapshotId &&
                step.ParticipantUserIds.Count == 1 &&
                !string.IsNullOrWhiteSpace(step.AssignmentId)),
            $"{commandId} step state or exact Form pins drifted.");

        var snapshot = await database
            .GetCollection<DynamicFlowParticipantSnapshot>("dynamic_flow_participant_snapshots")
            .Find(x => x.FlowInstanceId == instanceId)
            .SingleAsync(ct);
        Require(snapshot.Bindings.Count == targetCount, $"{commandId} participant binding count mismatch.");
        Require(
            snapshot.Id == instance.ParticipantSnapshotId &&
            snapshot.SnapshotHash == instance.ParticipantSnapshotHash &&
            snapshot.SnapshotHash == ParticipantSnapshotHash(snapshot),
            $"{commandId} participant snapshot identity/hash drifted.");
        Require(
            snapshot.Bindings.Select(binding => binding.TargetUnitId)
                .OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(expectedTargets),
            $"{commandId} frozen participant target set drifted.");
        Require(
            snapshot.Bindings.All(binding =>
                binding.AssigneeUserIds.Count == 1 &&
                binding.Participants.Count == 1 &&
                binding.Participants[0].UserId == binding.AssigneeUserIds[0] &&
                binding.Participants[0].UnitId == binding.TargetUnitId &&
                !string.IsNullOrWhiteSpace(binding.Participants[0].Username) &&
                !string.IsNullOrWhiteSpace(binding.Participants[0].FullName) &&
                binding.RoleCodes.SequenceEqual(
                    new[] { DynamicFlowRuntimePlanner.AssignmentFlowRole })),
            $"{commandId} frozen participant cardinality/details drifted.");
        Require(snapshot.SnapshotHash.Length == 64, $"{commandId} participant snapshot hash is missing.");
        Require(
            fixture.FormPins.Count == 3 &&
            fixture.FormPins.Select(pin => pin.FormVersionId)
                .Distinct(StringComparer.Ordinal)
                .Count() == 3,
            $"{commandId} fixture does not carry distinct immutable Form A/B/C pins.");
        var expectedFormsToken = Hash(string.Join(
            "\n",
            fixture.FormPins
                .OrderBy(pin => pin.FormNodeId, StringComparer.Ordinal)
                .Select(pin => $"{pin.FormNodeId}:{pin.FormSchemaHash}")));
        Require(
            snapshot.SourceRevisionTokens.TryGetValue("forms", out var formsToken) &&
            formsToken == expectedFormsToken,
            $"{commandId} participant snapshot did not freeze exact Form A/B/C pins.");
        var pinnedFormIds = fixture.FormPins
            .Select(pin => pin.FormVersionId)
            .ToArray();
        var pinnedForms = await database.GetCollection<DynamicFormTemplate>(
                "dynamic_form_templates")
            .Find(form => pinnedFormIds.Contains(form.Id) && !form.IsDeleted)
            .ToListAsync(ct);
        Require(
            pinnedForms.Count == 3 &&
            fixture.FormPins.All(pin => pinnedForms.Any(form =>
                form.Id == pin.FormVersionId &&
                (form.FamilyId ?? form.Id) == pin.FormFamilyId &&
                form.VersionNo == pin.FormVersionNo &&
                form.PublishedSchemaHash == pin.FormSchemaHash)),
            $"{commandId} direct-Mongo Form A/B/C immutable pins drifted.");

        var events = await database.GetCollection<DynamicFlowRuntimeEvent>("dynamic_flow_runtime_events")
            .Find(x => x.FlowInstanceId == instanceId)
            .SortBy(x => x.Sequence)
            .ToListAsync(ct);
        Require(events.Count >= targetCount + 1, $"{commandId} event chain is incomplete.");
        Require(
            events.Select(item => item.Sequence).Distinct().Count() == events.Count,
            $"{commandId} event sequence is not unique.");
        Require(
            events.All(item =>
                item.CommandId == commandId &&
                item.PayloadHash == Hash(item.Payload.ToJson())),
            $"{commandId} event command/payload hash drifted.");
        Require(
            events.Count(item => item.EventType == DynamicFlowRuntimeEventTypes.LaunchIntentCommitted) == 1 &&
            events.Count(item => item.EventType == DynamicFlowRuntimeEventTypes.EntryAssignmentMaterialized) ==
            targetCount,
            $"{commandId} launch/branch event ledger mismatch.");
        if (expectRecoveryEvents)
        {
            Require(
                events.Any(item => item.EventType == DynamicFlowRuntimeEventTypes.RecoveryStarted) &&
                events.Any(item => item.EventType == DynamicFlowRuntimeEventTypes.RecoveryReconciled) &&
                events.Any(item => item.EventType == DynamicFlowRuntimeEventTypes.RecoveryCompleted),
                $"{commandId} recovery event chain is incomplete.");
        }

        var outbox = await database.GetCollection<DynamicFlowRuntimeOutboxItem>("dynamic_flow_runtime_outbox")
            .Find(x => x.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        Require(outbox.Count == targetCount, $"{commandId} outbox count mismatch.");
        Require(
            outbox.All(item =>
                item.Status == DynamicFlowRuntimeOutboxStatuses.Completed &&
                item.Operation == DynamicFlowRuntimeMaterializationOperations.MaterializeEntryAssignment &&
                item.Payload["archetypeId"].AsString == archetypeId &&
                item.Payload["periodKey"].AsString == expectedPeriodKey &&
                item.Payload["scheduleIdentityJson"].AsString == expectedScheduleJson &&
                item.Payload["scheduleIdentityHash"].AsString == instance.ScheduleIdentityHash &&
                item.PayloadHash == Hash(item.Payload.ToJson()) &&
                item.CompletedAtUtc is not null),
            $"{commandId} outbox did not complete inside the P5 archetype boundary.");
        Require(
            outbox.Select(item => item.DedupeKey).Distinct(StringComparer.Ordinal).Count() == targetCount &&
            outbox.Select(item => item.Payload["targetUnitId"].AsString)
                .OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(expectedTargets),
            $"{commandId} outbox dedupe/target ledger drifted.");

        var assignments = await database.GetCollection<WorkAssignment>("work_assignments")
            .Find(x => x.FlowInstanceId == instanceId && !x.IsDeleted)
            .ToListAsync(ct);
        Require(assignments.Count == targetCount, $"{commandId} assignment count mismatch.");
        Require(
            assignments.All(assignment =>
                assignment.WorkId == expectedWorkId &&
                assignment.WorkType == WorkType.TASK.ToString() &&
                assignment.FlowTemplateId == instance.FlowTemplateId &&
                assignment.FlowTemplateVersionNo == 1 &&
                assignment.FlowInstanceId == instanceId &&
                assignment.DynamicFormTemplateId == fixture.FormVersionId &&
                assignment.DynamicFormFamilyId == fixture.FormFamilyId &&
                assignment.DynamicFormVersionNo == fixture.FormVersionNo &&
                assignment.DynamicFormSchemaHash == fixture.FormSchemaHash &&
                assignment.IssuedByUnitId == instance.IssuerUnitId &&
                assignment.IsActive &&
                assignment.TargetUnitIds is { Count: 1 } &&
                expectedTargets.Contains(assignment.TargetUnitIds[0], StringComparer.Ordinal) &&
                assignment.Assignees.Count == 1 &&
                assignment.Assignees[0].UnitId == assignment.TargetUnitIds[0]),
            $"{commandId} assignment exact pins drifted.");
        var assignmentIds = assignments.Select(item => item.Id).ToArray();
        var bindings = await database.GetCollection<WorkTemplateAssignee>("work_template_assignees")
            .Find(x => x.WorkId == expectedWorkId && !x.IsDeleted)
            .ToListAsync(ct);
        Require(bindings.Count == targetCount, $"{commandId} binding count mismatch.");
        Require(
            bindings.All(binding =>
                assignmentIds.Contains(binding.WorkAssignmentId) &&
                binding.IsActive &&
                binding.DynamicFormTemplateId == fixture.FormVersionId &&
                binding.DynamicFormFamilyId == fixture.FormFamilyId &&
                binding.DynamicFormVersionNo == fixture.FormVersionNo &&
                binding.DynamicFormSchemaHash == fixture.FormSchemaHash &&
                expectedTargets.Contains(binding.AssigneeUnitId!, StringComparer.Ordinal)),
            $"{commandId} binding pins/refs drifted.");

        var periods = await database.GetCollection<WorkReportPeriod>("work_report_periods")
            .Find(x => x.WorkId == expectedWorkId && !x.IsDeleted)
            .ToListAsync(ct);
        Require(periods.Count == targetCount, $"{commandId} period count mismatch.");
        Require(
            periods.All(period =>
                assignmentIds.Contains(period.WorkAssignmentId) &&
                bindings.Any(binding =>
                    binding.Id == period.WorkTemplateAssigneeId &&
                    binding.AssigneeUserId == period.AssigneeUserId) &&
                period.PeriodKey == expectedPeriodKey &&
                period.PeriodInstanceKey == expectedPeriodKey &&
                period.DynamicFormTemplateId == fixture.FormVersionId &&
                period.DynamicFormFamilyId == fixture.FormFamilyId &&
                period.DynamicFormVersionNo == fixture.FormVersionNo &&
                period.DynamicFormSchemaHash == fixture.FormSchemaHash &&
                period.IsActive),
            $"{commandId} exact period identity/pins drifted.");

        var queues = await database.GetCollection<WorkAssignmentQueueItem>("work_assignment_queue")
            .Find(x => x.WorkId == expectedWorkId && !x.IsDeleted)
            .ToListAsync(ct);
        Require(queues.Count == targetCount, $"{commandId} queue count mismatch.");
        Require(
            queues.All(queue =>
                assignmentIds.Contains(queue.WorkAssignmentId) &&
                queue.PeriodKey == expectedPeriodKey &&
                periods.Any(period =>
                    period.WorkAssignmentId == queue.WorkAssignmentId &&
                    period.AssigneeUserId == queue.AssigneeUserId) &&
                queue.IsActive),
            $"{commandId} queue refs drifted.");

        var assignmentDocRoles = await database.GetCollection<DocRole>("doc_roles")
            .Find(role =>
                role.DocType == DocType.WORK_ASSIGNMENT &&
                assignmentIds.Contains(role.DocId) &&
                !role.IsDeleted)
            .ToListAsync(ct);
        Require(
            assignmentDocRoles.Count == targetCount * 2 &&
            assignments.All(assignment =>
                assignmentDocRoles.Count(role =>
                    role.DocId == assignment.Id &&
                    role.UserId == instance.IssuerUserId &&
                    role.Role == DocRoleType.ASSIGNER) == 1 &&
                assignment.Assignees.All(assignee =>
                    assignmentDocRoles.Count(role =>
                        role.DocId == assignment.Id &&
                        role.UserId == assignee.UserId &&
                        role.Role == DocRoleType.ASSIGNEE) == 1)),
            $"{commandId} assignment DocRole ledger drifted.");
        var expectedParticipantIds = bindings
            .Select(binding => binding.AssigneeUserId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var workParticipantRoles = await database.GetCollection<DocRole>("doc_roles")
            .Find(role =>
                role.DocType == DocType.WORK &&
                role.DocId == expectedWorkId &&
                role.Role == DocRoleType.WORK_PARTICIPANT &&
                !role.IsDeleted)
            .ToListAsync(ct);
        Require(
            workParticipantRoles.Select(role => role.UserId)
                .OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(expectedParticipantIds),
            $"{commandId} work participant DocRole ledger drifted.");

        var assignmentReadModels = await database
            .GetCollection<AssignmentListDocRole>("assignment_list_doc_roles")
            .Find(row => assignmentIds.Contains(row.AssignmentId) && !row.IsDeleted)
            .ToListAsync(ct);
        Require(
            assignmentReadModels.Count == targetCount * 2 &&
            assignmentReadModels.All(row =>
                row.WorkId == expectedWorkId &&
                row.FlowInstanceId == instanceId &&
                row.FlowTemplateId == instance.FlowTemplateId &&
                row.FlowTemplateVersionNo == instance.FlowTemplateVersionNo &&
                row.DynamicFormTemplateId == fixture.FormVersionId &&
                row.DynamicFormFamilyId == fixture.FormFamilyId &&
                row.DynamicFormVersionNo == fixture.FormVersionNo &&
                row.DynamicFormSchemaHash == fixture.FormSchemaHash),
            $"{commandId} assignment read-model ledger drifted.");
        var periodIds = periods.Select(period => period.Id).ToArray();
        var periodReadModels = await database
            .GetCollection<MyReportPeriodListDocRole>("my_report_period_list_doc_roles")
            .Find(row => periodIds.Contains(row.WorkReportPeriodId) && !row.IsDeleted)
            .ToListAsync(ct);
        Require(
            periodReadModels.Count == targetCount &&
            periodReadModels.All(row =>
                assignmentIds.Contains(row.AssignmentId) &&
                row.PeriodKey == expectedPeriodKey &&
                row.PeriodInstanceKey == expectedPeriodKey &&
                row.DynamicFormTemplateId == fixture.FormVersionId &&
                row.DynamicFormFamilyId == fixture.FormFamilyId &&
                row.DynamicFormVersionNo == fixture.FormVersionNo &&
                row.DynamicFormSchemaHash == fixture.FormSchemaHash),
            $"{commandId} report-period read-model ledger drifted.");

        var activeWorkAssignmentCount = await database.GetCollection<WorkAssignment>("work_assignments")
            .CountDocumentsAsync(
                x => x.WorkId == expectedWorkId && x.IsActive && !x.IsDeleted,
                cancellationToken: ct);
        Require(
            activeWorkAssignmentCount == targetCount &&
            steps.All(step =>
                assignments.Count(assignment =>
                    assignment.Id == step.AssignmentId &&
                    assignment.FlowBranchId == step.BranchId &&
                    assignment.TargetUnitIds![0] == step.TargetUnitId) == 1),
            $"{commandId} orphan or duplicate assignment detected.");

        return new ProbePersistenceSnapshot(
            instanceId,
            receipt.Status,
            instance.State,
            steps.Count,
            snapshot.Bindings.Count,
            events.Count,
            outbox.Count,
            assignments.Count,
            bindings.Count,
            periods.Count,
            queues.Count,
            assignmentDocRoles.Count,
            workParticipantRoles.Count,
            assignmentReadModels.Count,
            periodReadModels.Count,
            expectedPeriodKey,
            instance.ScheduleIdentityHash);
    }

    private static async Task<object> ValidateDurableIntentBeforeMaterializationAsync(
        IMongoDatabase database,
        string workId,
        string commandId,
        int targetCount,
        CancellationToken ct)
    {
        var receipt = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>("dynamic_flow_runtime_command_receipts")
            .Find(x => x.CommandId == commandId)
            .SingleAsync(ct);
        Require(
            receipt.Status == DynamicFlowRuntimeCommandStatuses.Pending,
            "After-intent receipt must remain PENDING before retry.");
        var instanceId = receipt.FlowInstanceId
                         ?? throw new InvalidOperationException("After-intent receipt lacks flowInstanceId.");
        var instanceCount = await database.GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .CountDocumentsAsync(x => x.Id == instanceId, cancellationToken: ct);
        var stepCount = await database.GetCollection<DynamicFlowStepInstance>("dynamic_flow_step_instances")
            .CountDocumentsAsync(x => x.FlowInstanceId == instanceId, cancellationToken: ct);
        var snapshotCount = await database
            .GetCollection<DynamicFlowParticipantSnapshot>("dynamic_flow_participant_snapshots")
            .CountDocumentsAsync(x => x.FlowInstanceId == instanceId, cancellationToken: ct);
        var eventCount = await database.GetCollection<DynamicFlowRuntimeEvent>("dynamic_flow_runtime_events")
            .CountDocumentsAsync(x => x.FlowInstanceId == instanceId, cancellationToken: ct);
        var outboxCount = await database.GetCollection<DynamicFlowRuntimeOutboxItem>("dynamic_flow_runtime_outbox")
            .CountDocumentsAsync(x => x.FlowInstanceId == instanceId, cancellationToken: ct);
        var assignmentCount = await database.GetCollection<WorkAssignment>("work_assignments")
            .CountDocumentsAsync(x => x.FlowInstanceId == instanceId, cancellationToken: ct);
        var bindingClaimCount = await database.GetCollection<WorkTemplateAssignee>(
                "work_template_assignees")
            .CountDocumentsAsync(
                x => x.WorkId == workId && x.IsActive && !x.IsDeleted,
                cancellationToken: ct);
        Require(instanceCount == 1, "After-intent instance was not durably committed.");
        Require(stepCount == targetCount, "After-intent steps were not durably committed.");
        Require(snapshotCount == 1, "After-intent participant snapshot was not durably committed.");
        Require(eventCount == 1, "After-intent launch event was not durably committed.");
        Require(outboxCount == targetCount, "After-intent outbox was not durably committed.");
        Require(assignmentCount == 0, "After-intent fault leaked assignment side effects.");
        Require(
            bindingClaimCount == targetCount,
            "After-intent core transaction did not durably reserve exact binding claims.");
        return new
        {
            flowInstanceId = instanceId,
            receiptStatus = receipt.Status,
            instanceCount,
            stepCount,
            snapshotCount,
            eventCount,
            outboxCount,
            assignmentCount,
            bindingClaimCount
        };
    }

    private static async Task ValidateRolledBackIntentAsync(
        IMongoDatabase database,
        string workId,
        string commandId,
        CancellationToken ct)
    {
        var receiptCount = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>("dynamic_flow_runtime_command_receipts")
            .CountDocumentsAsync(x => x.CommandId == commandId, cancellationToken: ct);
        var instanceCount = await database.GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .CountDocumentsAsync(x => x.LaunchCommandId == commandId, cancellationToken: ct);
        var assignmentCount = await database.GetCollection<WorkAssignment>("work_assignments")
            .CountDocumentsAsync(x => x.WorkId == workId, cancellationToken: ct);
        var bindingCount = await database.GetCollection<WorkTemplateAssignee>("work_template_assignees")
            .CountDocumentsAsync(x => x.WorkId == workId, cancellationToken: ct);
        Require(
            receiptCount == 0 &&
            instanceCount == 0 &&
            assignmentCount == 0 &&
            bindingCount == 0,
            $"Core boundary fault {commandId} leaked a transaction fragment.");
    }

    private static async Task<object> ValidateBranchPartialAsync(
        IMongoDatabase database,
        string commandId,
        int targetCount,
        CancellationToken ct)
    {
        var receipt = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>("dynamic_flow_runtime_command_receipts")
            .Find(x => x.CommandId == commandId)
            .SingleAsync(ct);
        var instanceId = receipt.FlowInstanceId
                         ?? throw new InvalidOperationException("Branch-fault receipt lacks flowInstanceId.");
        var instance = await database.GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(x => x.Id == instanceId)
            .SingleAsync(ct);
        var outbox = await database.GetCollection<DynamicFlowRuntimeOutboxItem>("dynamic_flow_runtime_outbox")
            .Find(x => x.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        var assignments = await database.GetCollection<WorkAssignment>("work_assignments")
            .Find(x => x.FlowInstanceId == instanceId && !x.IsDeleted)
            .ToListAsync(ct);
        Require(receipt.Status == DynamicFlowRuntimeCommandStatuses.Pending, "Partial receipt must remain PENDING.");
        Require(instance.State == DynamicFlowInstanceStates.Partial, "Injected branch failure did not enter PARTIAL.");
        Require(outbox.Count == targetCount, "Partial outbox cardinality mismatch.");
        Require(
            outbox.Count(item => item.Status == DynamicFlowRuntimeOutboxStatuses.Completed) == targetCount - 1 &&
            outbox.Count(item => item.Status == DynamicFlowRuntimeOutboxStatuses.Failed) == 1,
            "Injected branch failure did not isolate one failed outbox item.");
        Require(
            assignments.Count == targetCount,
            "AFTER_ASSIGNMENT_WRITE must preserve both deterministic assignments before recovery.");
        return new
        {
            flowInstanceId = instanceId,
            receiptStatus = receipt.Status,
            instanceState = instance.State,
            completedOutbox = outbox.Count(item => item.Status == DynamicFlowRuntimeOutboxStatuses.Completed),
            failedOutbox = outbox.Count(item => item.Status == DynamicFlowRuntimeOutboxStatuses.Failed),
            assignments = assignments.Count
        };
    }

    private static async Task<FaultGroupRunResult> RunSideFaultGroupAsync(
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        MongoReplicaSetLease mongo,
        IMongoDatabase database,
        string adminPassword,
        ProbeFixture fixture,
        string phase,
        string workId,
        string commandId,
        IReadOnlyList<string> faultPoints,
        string periodKey,
        int? branchOrdinal,
        bool expectCompleteRecoveryEventChain,
        CancellationToken ct)
    {
        BackendServerLease? lease = null;
        try
        {
            await ClearFixtureBackfillManifestsAsync(
                database,
                fixture.T01VersionId,
                ct);
            var serverRoot = Path.Combine(iterationRoot, $"backend-{phase}");
            Directory.CreateDirectory(serverRoot);
            lease = await BackendServerLease.StartAsync(
                paths,
                serverRoot,
                runKey,
                mongo,
                ct,
                new BackendServerOptions
                {
                    EnableDynamicFlowRuntimeCandidate = true,
                    DynamicFlowRuntimeFaultCommandId = commandId,
                    DynamicFlowRuntimeFaultBranchOrdinal = branchOrdinal,
                    DynamicFlowRuntimeFaultPoints = faultPoints
                });
            await RestoreCandidateDefinitionAsync(database, fixture.T01VersionId, ct);

            using var api = new ApiHarnessClient(lease.BaseUri);
            var token = await api.LoginAsync("admin", adminPassword, ct);
            var targets = fixture.TargetUnitIds.Take(1).ToArray();
            var preflight = await PreflightAsync(
                api,
                token,
                workId,
                fixture.T01VersionId,
                commandId,
                targets,
                periodKey,
                ct);
            var attempts = new List<FaultAttemptObservation>();
            ApiHarnessResponse? terminal = null;
            for (var attempt = 1; attempt <= 24; attempt++)
            {
                var response = await ConfirmAsync(
                    api,
                    token,
                    workId,
                    fixture.T01VersionId,
                    commandId,
                    targets,
                    periodKey,
                    preflight.SnapshotToken,
                    ct);
                var status = OptionalStatus(response);
                attempts.Add(new FaultAttemptObservation(
                    attempt,
                    (int)response.StatusCode,
                    status,
                    true));
                if (response.StatusCode == HttpStatusCode.OK &&
                    status == "SUCCEEDED")
                {
                    terminal = response;
                    break;
                }

                Require(
                    (int)response.StatusCode is >= 500 and < 600 ||
                    (response.StatusCode == HttpStatusCode.OK &&
                     status is "RECOVERY_REQUIRED" or "MATERIALIZING"),
                    $"{phase} attempt {attempt} returned unexpected HTTP/status {(int)response.StatusCode}/{status}.");
            }

            Require(terminal is not null, $"{phase} did not converge after injected boundaries.");
            RequireStatus(terminal, "SUCCEEDED", phase);
            var converged = await ValidateConvergedAsync(
                database,
                fixture,
                workId,
                commandId,
                fixture.T01VersionId,
                "FLOW-T01",
                targets,
                periodKey,
                expectCompleteRecoveryEventChain,
                ct);

            await lease.StopAsync();
            var faultExecution = CaptureFaultExecution(
                phase,
                commandId,
                faultPoints,
                lease);
            var caseEvidence = new
            {
                caseId = $"P5-MAT-FAULT-{phase.ToUpperInvariant()}",
                verdict = "EXPECTED_FAULT_RECOVERED",
                configuredBoundaryCount = faultPoints.Count,
                attempts,
                converged
            };
            var exchangeEvidence = new
            {
                phase,
                api.Exchanges
            };
            await lease.DisposeAsync();
            lease = null;
            return new FaultGroupRunResult(
                faultExecution,
                caseEvidence,
                exchangeEvidence);
        }
        finally
        {
            if (lease is not null)
            {
                try
                {
                    await lease.StopAsync();
                }
                catch
                {
                    // The outer probe reports process cleanup independently.
                }
                await lease.DisposeAsync();
            }
        }
    }

    private static async Task<FaultGroupRunResult> RunReconcileFaultScenarioAsync(
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        MongoReplicaSetLease mongo,
        IMongoDatabase database,
        string adminPassword,
        ProbeFixture fixture,
        CancellationToken ct)
    {
        BackendServerLease? lease = null;
        try
        {
            await ClearFixtureBackfillManifestsAsync(database, fixture.T01VersionId, ct);
            var phase = "reconcile-operation";
            var serverRoot = Path.Combine(iterationRoot, $"backend-{phase}");
            Directory.CreateDirectory(serverRoot);
            lease = await BackendServerLease.StartAsync(
                paths,
                serverRoot,
                runKey,
                mongo,
                ct,
                new BackendServerOptions
                {
                    EnableDynamicFlowRuntimeCandidate = true,
                    DynamicFlowRuntimeFaultCommandId = ReconcileFaultCommand,
                    DynamicFlowRuntimeFaultPoints = ReconcileFaultPoints
                });
            await RestoreCandidateDefinitionAsync(database, fixture.T01VersionId, ct);
            using var api = new ApiHarnessClient(lease.BaseUri);
            var token = await api.LoginAsync("admin", adminPassword, ct);
            var targets = fixture.TargetUnitIds.Take(1).ToArray();
            const string periodKey = "2026-07-RECONCILE";
            var launch = await LaunchAsync(
                api,
                token,
                fixture,
                fixture.ReconcileFaultWorkId,
                fixture.T01VersionId,
                ReconcileFaultCommand,
                targets,
                periodKey,
                ct);
            RequireStatus(launch.Confirm, "SUCCEEDED", "reconcile fixture launch");
            var flowInstanceId = ApiHarnessClient.RequiredString(
                launch.Confirm.Json,
                "flowInstanceId");
            var operationPath =
                $"api/admin/operations/dynamic-flow-runtime/instances/{flowInstanceId}/reconcile";
            var cleanPreview = await api.PostAsync(
                $"{operationPath}?apply=false",
                new { },
                token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                cleanPreview,
                HttpStatusCode.OK,
                "reconcile clean dry-run");
            Require(
                ApiHarnessClient.RequiredBool(cleanPreview.Json, "converged"),
                "Clean reconcile dry-run should report converged.");

            await database.GetCollection<WorkAssignmentQueueItem>("work_assignment_queue")
                .UpdateManyAsync(
                    item =>
                        item.WorkId == fixture.ReconcileFaultWorkId &&
                        !item.IsDeleted,
                    Builders<WorkAssignmentQueueItem>.Update
                        .Set(item => item.IsActive, false)
                        .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
                    cancellationToken: ct);
            await database.GetCollection<DynamicFlowRuntimeOutboxItem>(
                    "dynamic_flow_runtime_outbox")
                .UpdateManyAsync(
                    item =>
                        item.FlowInstanceId == flowInstanceId &&
                        item.Status == DynamicFlowRuntimeOutboxStatuses.Completed,
                    Builders<DynamicFlowRuntimeOutboxItem>.Update
                        .Set(
                            item => item.Status,
                            DynamicFlowRuntimeOutboxStatuses.Failed)
                        .Set(item => item.CompletedAtUtc, null)
                        .Set(
                            item => item.LastErrorCode,
                            "P5_RECONCILE_DURABLE_OUTBOX_DRIFT")
                        .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
                    cancellationToken: ct);
            var driftPreview = await api.PostAsync(
                $"{operationPath}?apply=false",
                new { },
                token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                driftPreview,
                HttpStatusCode.OK,
                "reconcile drift dry-run");
            Require(
                !ApiHarnessClient.RequiredBool(driftPreview.Json, "converged") &&
                !ApiHarnessClient.RequiredBool(driftPreview.Json, "exactLedgerConverged"),
                "Reconcile dry-run did not detect scoped queue drift.");

            var attempts = new List<FaultAttemptObservation>();
            ApiHarnessResponse? terminal = null;
            for (var attempt = 1; attempt <= ReconcileFaultPoints.Length + 2; attempt++)
            {
                var response = await api.PostAsync(
                    $"{operationPath}?apply=true",
                    new { },
                    token,
                    ct: ct);
                attempts.Add(new FaultAttemptObservation(
                    attempt,
                    (int)response.StatusCode,
                    response.Json?["converged"]?.GetValue<bool>() == true
                        ? "CONVERGED"
                        : null,
                    true));
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    Require(
                        ApiHarnessClient.RequiredBool(response.Json, "converged") &&
                        ApiHarnessClient.RequiredBool(response.Json, "exactLedgerConverged"),
                        "Applied reconcile returned a non-converged ledger.");
                    terminal = response;
                    break;
                }
                Require(
                    (int)response.StatusCode is >= 500 and < 600,
                    $"Reconcile fault attempt {attempt} expected HTTP 5xx.");
            }
            Require(terminal is not null, "Reconcile fault campaign did not converge.");
            var converged = await ValidateConvergedAsync(
                database,
                fixture,
                fixture.ReconcileFaultWorkId,
                ReconcileFaultCommand,
                fixture.T01VersionId,
                "FLOW-T01",
                targets,
                periodKey,
                true,
                ct);
            var reconciledReadback =
                await AssertRuntimeReadReconciledReadbackAsync(
                    api,
                    token,
                    database,
                    fixture.ReconcileFaultWorkId,
                    flowInstanceId,
                    ct);
            var instances = database.GetCollection<DynamicFlowInstance>(
                "dynamic_flow_instances");
            var beforeCursorDrift = await instances
                .Find(item => item.Id == flowInstanceId && !item.IsDeleted)
                .SingleAsync(ct);
            var exactEventCount = await database
                .GetCollection<DynamicFlowRuntimeEvent>(
                    "dynamic_flow_runtime_events")
                .CountDocumentsAsync(
                    item => item.FlowInstanceId == flowInstanceId,
                    cancellationToken: ct);
            var cursorDrift = await instances.UpdateOneAsync(
                item =>
                    item.Id == flowInstanceId &&
                    item.Revision == beforeCursorDrift.Revision &&
                    item.NextEventSequence ==
                    beforeCursorDrift.NextEventSequence &&
                    !item.IsDeleted,
                Builders<DynamicFlowInstance>.Update.Inc(
                    item => item.NextEventSequence,
                    17),
                cancellationToken: ct);
            Require(
                cursorDrift.ModifiedCount == 1,
                "Could not inject the runtime event cursor drift.");
            var cursorPreview = await api.PostAsync(
                $"{operationPath}?apply=false",
                new { },
                token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                cursorPreview,
                HttpStatusCode.OK,
                "event cursor drift dry-run");
            Require(
                !ApiHarnessClient.RequiredBool(
                    cursorPreview.Json,
                    "converged") &&
                ApiHarnessClient.RequiredBool(
                    cursorPreview.Json,
                    "exactMaterializationLedgerConverged") &&
                !ApiHarnessClient.RequiredBool(
                    cursorPreview.Json,
                    "exactStateProjectionLedgerConverged"),
                "Event cursor drift did not isolate the state ledger.");
            var cursorApply = await api.PostAsync(
                $"{operationPath}?apply=true",
                new { },
                token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                cursorApply,
                HttpStatusCode.OK,
                "event cursor drift apply");
            Require(
                ApiHarnessClient.RequiredBool(cursorApply.Json, "converged") &&
                ApiHarnessClient.RequiredBool(
                    cursorApply.Json,
                    "exactLedgerConverged"),
                "Event cursor repair did not converge.");
            var afterCursorRepair = await instances
                .Find(item => item.Id == flowInstanceId && !item.IsDeleted)
                .SingleAsync(ct);
            Require(
                afterCursorRepair.NextEventSequence == exactEventCount + 1 &&
                afterCursorRepair.Revision == beforeCursorDrift.Revision,
                "Event cursor repair changed a business revision or restored the wrong cursor.");
            var cursorReplay = await api.PostAsync(
                $"{operationPath}?apply=true",
                new { },
                token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                cursorReplay,
                HttpStatusCode.OK,
                "event cursor repair exact replay");
            var afterCursorReplay = await instances
                .Find(item => item.Id == flowInstanceId && !item.IsDeleted)
                .SingleAsync(ct);
            Require(
                ApiHarnessClient.RequiredBool(cursorReplay.Json, "converged") &&
                afterCursorReplay.NextEventSequence ==
                afterCursorRepair.NextEventSequence &&
                afterCursorReplay.Revision ==
                afterCursorRepair.Revision,
                "Event cursor repair replay was not a zero-write no-op.");
            var auditCount = await database.GetCollection<WorkStatusOperationLog>(
                    "work_status_operation_logs")
                .CountDocumentsAsync(
                    item =>
                        item.Operation == "DYNAMIC_FLOW_RUNTIME_RECONCILE" &&
                        item.Scope == $"dynamic-flow-runtime:{flowInstanceId}" &&
                        !item.IsDeleted,
                    cancellationToken: ct);
            Require(
                auditCount == attempts.Count + 4,
                "Reconcile dry-run/apply/failure audit ledger is incomplete.");

            await lease.StopAsync();
            var execution = CaptureFaultExecution(
                phase,
                ReconcileFaultCommand,
                ReconcileFaultPoints,
                lease);
            var caseEvidence = new
            {
                caseId = "P5-MAT-RECONCILE-DRYRUN-APPLY-FAULTS",
                verdict = "EXPECTED_FAULT_RECOVERED",
                flowInstanceId,
                cleanDryRunConverged = true,
                driftDryRunConverged = false,
                attempts,
                cursorRepair = new
                {
                    drift = 17,
                    expectedCursor = exactEventCount + 1,
                    observedCursor = afterCursorRepair.NextEventSequence,
                    businessRevisionPreserved =
                        afterCursorRepair.Revision ==
                        beforeCursorDrift.Revision,
                    exactReplayZeroWrite = true
                },
                auditCount,
                reconciledReadback,
                converged
            };
            var exchangeEvidence = new { phase, api.Exchanges };
            await lease.DisposeAsync();
            lease = null;
            return new FaultGroupRunResult(execution, caseEvidence, exchangeEvidence);
        }
        finally
        {
            if (lease is not null)
            {
                try
                {
                    await lease.StopAsync();
                }
                catch
                {
                    // The outer probe reports process cleanup independently.
                }
                await lease.DisposeAsync();
            }
        }
    }

    private static async Task<FaultGroupRunResult> RunCompensationFaultScenarioAsync(
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        MongoReplicaSetLease mongo,
        IMongoDatabase database,
        string adminPassword,
        ProbeFixture fixture,
        CancellationToken ct)
    {
        BackendServerLease? lease = null;
        try
        {
            await ClearFixtureBackfillManifestsAsync(database, fixture.T01VersionId, ct);
            var phase = "compensation-operation";
            var serverRoot = Path.Combine(iterationRoot, $"backend-{phase}");
            Directory.CreateDirectory(serverRoot);
            lease = await BackendServerLease.StartAsync(
                paths,
                serverRoot,
                runKey,
                mongo,
                ct,
                new BackendServerOptions
                {
                    EnableDynamicFlowRuntimeCandidate = true,
                    DynamicFlowRuntimeFaultCommandId = CompensationFaultCommand,
                    DynamicFlowRuntimeFaultPoints = CompensationFaultPoints
                });
            await RestoreCandidateDefinitionAsync(database, fixture.T01VersionId, ct);
            using var api = new ApiHarnessClient(lease.BaseUri);
            var token = await api.LoginAsync("admin", adminPassword, ct);
            var targets = fixture.TargetUnitIds.Take(1).ToArray();
            const string periodKey = "2026-07-COMPENSATION";
            var preflight = await PreflightAsync(
                api,
                token,
                fixture.CompensationFaultWorkId,
                fixture.T01VersionId,
                CompensationFaultCommand,
                targets,
                periodKey,
                ct);
            var initial = await ConfirmAsync(
                api,
                token,
                fixture.CompensationFaultWorkId,
                fixture.T01VersionId,
                CompensationFaultCommand,
                targets,
                periodKey,
                preflight.SnapshotToken,
                ct);
            RequireStatus(
                initial,
                "RECOVERY_REQUIRED",
                "compensation fixture injected assignment fault");
            var flowInstanceId = ApiHarnessClient.RequiredString(
                initial.Json,
                "flowInstanceId");
            await database.GetCollection<DynamicFlowParticipantSnapshot>(
                    "dynamic_flow_participant_snapshots")
                .UpdateOneAsync(
                    item => item.FlowInstanceId == flowInstanceId,
                    Builders<DynamicFlowParticipantSnapshot>.Update
                        .Set(item => item.SnapshotHash, new string('0', 64)),
                    cancellationToken: ct);

            var failureAttempts = new List<FaultAttemptObservation>();
            ApiHarnessResponse? failed = null;
            for (var attempt = 1; attempt <= 8; attempt++)
            {
                var response = await ConfirmAsync(
                    api,
                    token,
                    fixture.CompensationFaultWorkId,
                    fixture.T01VersionId,
                    CompensationFaultCommand,
                    targets,
                    periodKey,
                    preflight.SnapshotToken,
                    ct);
                var status = OptionalStatus(response);
                failureAttempts.Add(new FaultAttemptObservation(
                    attempt,
                    (int)response.StatusCode,
                    status,
                    true));
                if (response.StatusCode == HttpStatusCode.OK && status == "FAILED")
                {
                    failed = response;
                    break;
                }
                Require(
                    response.StatusCode == HttpStatusCode.OK &&
                    status is "RECOVERY_REQUIRED" or "MATERIALIZING",
                    $"Compensation terminalization attempt {attempt} returned unexpected HTTP/status {(int)response.StatusCode}/{status}.");
            }
            Require(failed is not null, "Compensation fixture did not reach terminal FAILED.");
            var failedInstance = await database.GetCollection<DynamicFlowInstance>(
                    "dynamic_flow_instances")
                .Find(item => item.Id == flowInstanceId)
                .SingleAsync(ct);
            Require(
                failedInstance.State == DynamicFlowInstanceStates.Failed,
                "Compensation fixture instance is not terminal FAILED.");

            var operationPath =
                $"api/admin/operations/dynamic-flow-runtime/instances/{flowInstanceId}/compensate";
            var preview = await api.PostAsync(
                $"{operationPath}?apply=false",
                new { },
                token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                preview,
                HttpStatusCode.OK,
                "compensation dry-run");
            Require(
                preview.Json?["affectedOwnedArtifacts"]?.GetValue<int>() > 0,
                "Compensation dry-run found no owned artifacts.");

            var applyAttempts = new List<FaultAttemptObservation>();
            ApiHarnessResponse? applied = null;
            for (var attempt = 1; attempt <= 8; attempt++)
            {
                var response = await api.PostAsync(
                    $"{operationPath}?apply=true",
                    new { },
                    token,
                    ct: ct);
                applyAttempts.Add(new FaultAttemptObservation(
                    attempt,
                    (int)response.StatusCode,
                    response.StatusCode == HttpStatusCode.OK ? "COMPENSATED" : null,
                    true));
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    applied = response;
                    break;
                }
                Require(
                    (int)response.StatusCode is >= 500 and < 600,
                    $"Compensation fault attempt {attempt} expected HTTP 5xx.");
            }
            Require(applied is not null, "Compensation fault campaign did not complete.");
            var compensationStateBeforeReplay = await database
                .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
                .Find(item => item.Id == flowInstanceId)
                .SingleAsync(ct);
            var concurrentReplayResponses = await Task.WhenAll(
                Enumerable.Range(0, 4).Select(_ =>
                    api.PostAsync(
                        $"{operationPath}?apply=true",
                        new { },
                        token,
                        ct: ct)));
            Require(
                concurrentReplayResponses.All(response =>
                    response.StatusCode == HttpStatusCode.OK),
                "Concurrent compensation replay did not remain idempotent.");
            var compensationStateAfterReplay = await database
                .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
                .Find(item => item.Id == flowInstanceId)
                .SingleAsync(ct);
            Require(
                compensationStateBeforeReplay.CompensatedAtUtc is not null &&
                compensationStateAfterReplay.CompensatedAtUtc ==
                compensationStateBeforeReplay.CompensatedAtUtc &&
                compensationStateAfterReplay.Revision ==
                compensationStateBeforeReplay.Revision &&
                compensationStateAfterReplay.NextEventSequence ==
                compensationStateBeforeReplay.NextEventSequence,
                "Concurrent compensation replay changed the durable completion marker.");
            var assignmentIds = await database.GetCollection<WorkAssignment>("work_assignments")
                .Find(item => item.FlowInstanceId == flowInstanceId && !item.IsDeleted)
                .Project(item => item.Id)
                .ToListAsync(ct);
            var activeAssignments = await database.GetCollection<WorkAssignment>(
                    "work_assignments")
                .CountDocumentsAsync(
                    item =>
                        item.FlowInstanceId == flowInstanceId &&
                        item.IsActive &&
                        !item.IsDeleted,
                    cancellationToken: ct);
            var activeBindings = await database.GetCollection<WorkTemplateAssignee>(
                    "work_template_assignees")
                .CountDocumentsAsync(
                    item =>
                        item.WorkId == fixture.CompensationFaultWorkId &&
                        item.IsActive &&
                        !item.IsDeleted,
                    cancellationToken: ct);
            var activePeriods = await database.GetCollection<WorkReportPeriod>(
                    "work_report_periods")
                .CountDocumentsAsync(
                    item =>
                        item.WorkId == fixture.CompensationFaultWorkId &&
                        item.IsActive &&
                        !item.IsDeleted,
                    cancellationToken: ct);
            var activeQueues = await database.GetCollection<WorkAssignmentQueueItem>(
                    "work_assignment_queue")
                .CountDocumentsAsync(
                    item =>
                        item.WorkId == fixture.CompensationFaultWorkId &&
                        item.IsActive &&
                        !item.IsDeleted,
                    cancellationToken: ct);
            var activeAssignmentRoles = assignmentIds.Count == 0
                ? 0
                : await database.GetCollection<DocRole>("doc_roles")
                    .CountDocumentsAsync(
                        item =>
                            assignmentIds.Contains(item.DocId) &&
                            !item.IsDeleted,
                        cancellationToken: ct);
            var activeAssignmentReadModels = assignmentIds.Count == 0
                ? 0
                : await database.GetCollection<AssignmentListDocRole>(
                        "assignment_list_doc_roles")
                    .CountDocumentsAsync(
                        item =>
                            assignmentIds.Contains(item.AssignmentId) &&
                            item.IsActive &&
                            !item.IsDeleted,
                        cancellationToken: ct);
            Require(
                activeAssignments == 0 &&
                activeBindings == 0 &&
                activePeriods == 0 &&
                activeQueues == 0 &&
                activeAssignmentRoles == 0 &&
                activeAssignmentReadModels == 0,
                "Scoped compensation left active owned artifacts/read models: " +
                $"assignments={activeAssignments}, bindings={activeBindings}, " +
                $"periods={activePeriods}, queues={activeQueues}, " +
                $"docRoles={activeAssignmentRoles}, readModels={activeAssignmentReadModels}.");
            var compensationEventCount = await database.GetCollection<DynamicFlowRuntimeEvent>(
                    "dynamic_flow_runtime_events")
                .CountDocumentsAsync(
                    item =>
                        item.FlowInstanceId == flowInstanceId &&
                        item.EventType == DynamicFlowRuntimeEventTypes.CompensationApplied,
                    cancellationToken: ct);
            Require(
                compensationEventCount == 1,
                "Compensation terminal audit event cardinality mismatch.");
            var auditCount = await database.GetCollection<WorkStatusOperationLog>(
                    "work_status_operation_logs")
                .CountDocumentsAsync(
                    item =>
                        item.Operation == "DYNAMIC_FLOW_RUNTIME_COMPENSATE" &&
                        item.Scope == $"dynamic-flow-runtime:{flowInstanceId}" &&
                        !item.IsDeleted,
                    cancellationToken: ct);
            Require(
                auditCount == applyAttempts.Count + 1,
                "Compensation dry-run/apply/failure audit ledger is incomplete.");

            await lease.StopAsync();
            var execution = CaptureFaultExecution(
                phase,
                CompensationFaultCommand,
                CompensationFaultPoints,
                lease);
            var caseEvidence = new
            {
                caseId = "P5-MAT-COMPENSATION-DRYRUN-APPLY-FAULTS",
                verdict = "EXPECTED_FAULT_RECOVERED",
                flowInstanceId,
                terminalState = failedInstance.State,
                failureAttempts,
                applyAttempts,
                concurrentReplayStatuses = concurrentReplayResponses
                    .Select(response => (int)response.StatusCode)
                    .ToArray(),
                compensationRevisionStable =
                    compensationStateAfterReplay.Revision ==
                    compensationStateBeforeReplay.Revision,
                auditCount,
                compensationEventCount,
                activeAssignments,
                activeBindings,
                activePeriods,
                activeQueues,
                activeAssignmentRoles,
                activeAssignmentReadModels
            };
            var exchangeEvidence = new { phase, api.Exchanges };
            await lease.DisposeAsync();
            lease = null;
            return new FaultGroupRunResult(execution, caseEvidence, exchangeEvidence);
        }
        finally
        {
            if (lease is not null)
            {
                try
                {
                    await lease.StopAsync();
                }
                catch
                {
                    // The outer probe reports process cleanup independently.
                }
                await lease.DisposeAsync();
            }
        }
    }

    private static async Task ClearFixtureBackfillManifestsAsync(
        IMongoDatabase database,
        string versionId,
        CancellationToken ct)
    {
        var version = await database.GetCollection<DynamicFlowTemplateVersion>(
                "dynamic_flow_template_versions")
            .Find(item => item.Id == versionId)
            .SingleAsync(ct);
        var documentIds = new BsonArray
        {
            ObjectId.Parse(version.Id),
            ObjectId.Parse(version.TemplateId)
        };
        var manifests = database.GetCollection<BsonDocument>(
            "dynamic_flow_definition_metadata_backfill_manifests");
        await manifests.DeleteManyAsync(
            new BsonDocument("documentId", new BsonDocument("$in", documentIds)),
            ct);
    }

    private static FaultExecutionObservation CaptureFaultExecution(
        string phase,
        string commandId,
        IReadOnlyCollection<string> configuredPoints,
        BackendServerLease backend,
        int? expectedExecutions = null,
        string? failureMessage = null)
    {
        failureMessage ??= DynamicFlowRuntimeFaultInjector.FailureMessage;
        var stdout = File.Exists(backend.StdoutPath)
            ? File.ReadAllText(backend.StdoutPath)
            : string.Empty;
        var stderr = File.Exists(backend.StderrPath)
            ? File.ReadAllText(backend.StderrPath)
            : string.Empty;
        var combined = $"{stdout}\n{stderr}";
        var hits = configuredPoints
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToDictionary(
                point => point,
                point => CountOccurrences(
                    combined,
                    $"{failureMessage}:{point}"),
                StringComparer.Ordinal);
        var missing = hits
            .Where(pair => pair.Value == 0)
            .Select(pair => pair.Key)
            .ToArray();
        Require(
            missing.Length == 0,
            $"{phase} did not execute configured fault point(s): {string.Join(", ", missing)}.");
        return new FaultExecutionObservation(
            phase,
            commandId,
            configuredPoints.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            hits,
            expectedExecutions);
    }

    private static object BuildFaultBoundaryMatrix(
        IReadOnlyCollection<FaultExecutionObservation> executions)
    {
        var operationOnly = new HashSet<string>(
            new[]
            {
                DynamicFlowRuntimeFaultPoints.BeforeLedgerRepairWrite,
                DynamicFlowRuntimeFaultPoints.AfterLedgerRepairWrite,
                DynamicFlowRuntimeFaultPoints.BeforeCompensationWrite,
                DynamicFlowRuntimeFaultPoints.AfterCompensationWrite,
                DynamicFlowRuntimeFaultPoints.BeforeCompensationProjection,
                DynamicFlowRuntimeFaultPoints.AfterCompensationProjection
            },
            StringComparer.Ordinal);
        var executed = executions
            .SelectMany(item => item.LogHits)
            .Where(pair => pair.Value > 0)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        var rows = DynamicFlowRuntimeFaultPoints.All
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(point => new
            {
                faultPoint = point,
                boundaryClass = FaultBoundaryClass(point),
                evidenceMode = executed.Contains(point)
                    ? "EXECUTED_API_KESTREL_REPLICA_SET_DIRECT_MONGO"
                    : operationOnly.Contains(point)
                        ? "STRUCTURAL_OPERATION_ONLY_NO_PUBLIC_API"
                        : "STRUCTURAL_INVENTORY",
                executed = executed.Contains(point),
                deliberateFailureReportedSuccess = false,
                evidencePhases = executions
                    .Where(item => item.LogHits.TryGetValue(point, out var count) && count > 0)
                    .Select(item => item.Phase)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray()
            })
            .ToArray();
        Require(
            rows.Length == DynamicFlowRuntimeFaultPoints.All.Count &&
            rows.Select(row => row.faultPoint).Distinct(StringComparer.Ordinal).Count() ==
            DynamicFlowRuntimeFaultPoints.All.Count,
            "Fault matrix does not map the complete runtime fault-point catalog.");
        Require(
            rows.All(row => row.executed) &&
            executed.SetEquals(DynamicFlowRuntimeFaultPoints.All),
            "Fault campaign did not execute every runtime boundary.");
        return new
        {
            boundaryCount = DynamicFlowRuntimeFaultPoints.All.Count,
            mappedBoundaryCount = rows.Length,
            executedBoundaryCount = executed.Count,
            operationBoundaryCount = operationOnly.Count,
            operationOnlyStructuralBoundaryCount = 0,
            allApiReachableBoundariesExecuted = true,
            allBoundariesExecuted = true,
            allBoundariesMapped = true,
            operationOnlyBoundaries = operationOnly.OrderBy(value => value, StringComparer.Ordinal),
            executions,
            rows
        };
    }

    private static string FaultBoundaryClass(string point)
    {
        if (CoreFaultPoints.Contains(point, StringComparer.Ordinal))
            return "CORE_TRANSACTION_OR_AFTER_COMMIT";
        if (point.Contains("RETRY", StringComparison.Ordinal) ||
            point.Contains("RECOVERY_EVENT", StringComparison.Ordinal))
        {
            return "RECOVERY_STATE_AND_EVENT";
        }
        if (point.Contains("FINALIZE", StringComparison.Ordinal))
            return "AGGREGATE_FINALIZATION";
        if (point.Contains("LEDGER_REPAIR", StringComparison.Ordinal))
            return "RECONCILE_OPERATION";
        if (point.Contains("COMPENSATION", StringComparison.Ordinal))
            return "COMPENSATION_OPERATION";
        return "IDEMPOTENT_BRANCH_MATERIALIZATION";
    }

    private static string? OptionalStatus(ApiHarnessResponse response)
        => response.Json?["status"]?.GetValue<string>();

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string ParticipantSnapshotHash(DynamicFlowParticipantSnapshot snapshot)
    {
        BsonDocument ParticipantDocument(DynamicFlowParticipantUserSnapshot user)
            => new()
            {
                { "userId", user.UserId },
                { "username", user.Username },
                { "fullName", user.FullName },
                { "unitId", user.UnitId },
                { "unitSymbol", BsonValue.Create(user.UnitSymbol) },
                { "unitShortName", BsonValue.Create(user.UnitShortName) },
                { "unitName", BsonValue.Create(user.UnitName) },
                { "positionCode", BsonValue.Create(user.PositionCode) },
                { "positionName", BsonValue.Create(user.PositionName) }
            };

        var sourceTokens = new BsonDocument(snapshot.SourceRevisionTokens
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new BsonElement(pair.Key, pair.Value)));
        var bindings = new BsonArray(snapshot.Bindings
            .OrderBy(binding => binding.TargetUnitId, StringComparer.Ordinal)
            .Select(binding => new BsonDocument
            {
                { "targetUnitId", binding.TargetUnitId },
                {
                    "assigneeUserIds",
                    new BsonArray(binding.AssigneeUserIds.OrderBy(
                        value => value,
                        StringComparer.Ordinal))
                },
                {
                    "participants",
                    new BsonArray(binding.Participants
                        .OrderBy(user => user.UserId, StringComparer.Ordinal)
                        .Select(ParticipantDocument))
                },
                {
                    "roleCodes",
                    new BsonArray(binding.RoleCodes.OrderBy(
                        value => value,
                        StringComparer.Ordinal))
                }
            }));
        return Hash(new BsonDocument
        {
            { "flowInstanceId", snapshot.FlowInstanceId },
            { "issuerUserId", snapshot.IssuerUserId },
            { "issuerUnitId", snapshot.IssuerUnitId },
            { "bindings", bindings },
            { "sourceRevisionTokens", sourceTokens }
        }.ToJson());
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static void RequireStatus(
        ApiHarnessResponse response,
        string expected,
        string operation)
    {
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, operation);
        var actual = ApiHarnessClient.RequiredString(response.Json, "status");
        Require(actual == expected, $"{operation} expected status {expected}, got {actual}.");
        Require(ApiHarnessClient.RequiredBool(response.Json, "businessWritePerformed"), $"{operation} performed no business write.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record ProbeFixture(
        string T01WorkId,
        string T02WorkId,
        string StateWorkId,
        string MultiUnitWorkId,
        string ConcurrentWorkId,
        string CoreWorkId,
        string BranchWorkId,
        string SideFaultGroupAWorkId,
        string SideFaultGroupBWorkId,
        string SideFaultGroupCWorkId,
        string AggregateRecoveryFaultWorkId,
        string ReconcileFaultWorkId,
        string CompensationFaultWorkId,
        string OutsiderWorkId,
        string ChangedReplayWorkId,
        string StaleWorkId,
        string LockedV11WorkId,
        string LockedV11VersionId,
        string FormVersionId,
        string FormFamilyId,
        int FormVersionNo,
        string FormSchemaHash,
        string T01VersionId,
        string T02VersionId,
        IReadOnlyList<string> TargetUnitIds,
        IReadOnlyList<ProbeFormPinSnapshot> FormPins,
        IReadOnlyList<ProbeBarrierFixture> BarrierFixtures);

    private sealed record ProbeBarrierFixture(
        string ArchetypeId,
        string WorkId,
        string VersionId);

    private sealed record ProbeFormPin(
        string FormNodeId,
        string Role,
        DynamicFormTemplate Form,
        string SchemaHash);

    private sealed record ProbeFormPinSnapshot(
        string FormNodeId,
        string Role,
        string FormVersionId,
        string FormFamilyId,
        int FormVersionNo,
        string FormSchemaHash);

    private sealed record PreflightResult(string SnapshotToken);

    private sealed record LaunchResult(string SnapshotToken, ApiHarnessResponse Confirm);

    private sealed record ProbePersistenceSnapshot(
        string FlowInstanceId,
        string ReceiptStatus,
        string InstanceState,
        int StepCount,
        int ParticipantBindingCount,
        int EventCount,
        int OutboxCount,
        int AssignmentCount,
        int BindingCount,
        int PeriodCount,
        int QueueCount,
        int AssignmentDocRoleCount,
        int WorkParticipantDocRoleCount,
        int AssignmentReadModelCount,
        int PeriodReadModelCount,
        string PeriodKey,
        string ScheduleIdentityHash);

    private sealed record FaultAttemptObservation(
        int Attempt,
        int HttpStatus,
        string? RuntimeStatus,
        bool DurableIntentPresent);

    private sealed record StateProjectionWorkerAttempt(
        int Attempt,
        int Processed,
        int PendingCount,
        long StateReceiptCount,
        string StepState,
        long StepRevision);

    private sealed record StateLedgerSnapshot(
        string StepState,
        long StepRevision,
        string InstanceState,
        long InstanceRevision,
        long NextEventSequence,
        long ReceiptCount,
        long EventCount,
        string FlowHash);

    private sealed record T01CompletionPersistenceSnapshot(
        DateTime AssignmentCompletedAtUtc,
        DateTime AssignmentCompletedDate,
        string AssignmentCompletedByUserId,
        int AssignmentProgressStatus,
        string StepState,
        long StepRevision,
        string InstanceState,
        long InstanceRevision,
        long NextEventSequence,
        long ReceiptCount,
        int EventCount,
        int CompletionEventCount,
        bool QueueActive,
        int AssignmentDocRoleCount,
        int AssignmentReadModelCount,
        int MyReportReadModelCount,
        int ReviewReadModelCount,
        string CompletionReceiptId);

    private sealed record FaultExecutionObservation(
        string Phase,
        string CommandId,
        IReadOnlyList<string> ConfiguredPoints,
        IReadOnlyDictionary<string, int> LogHits,
        int? ExpectedExecutions = null);

    private sealed record FaultGroupRunResult(
        FaultExecutionObservation FaultExecution,
        object CaseEvidence,
        object ExchangeEvidence);

    private sealed record LiveParticipantMutation(
        string TargetUnitId,
        string UserId,
        string OriginalUserFullName,
        string MutatedUserFullName,
        string OriginalUnitFullName,
        string MutatedUnitFullName,
        string? OriginalUnitShortName,
        string MutatedUnitShortName);
}

internal sealed record P5MaterializationProbeOptions(
    HarnessPaths Paths,
    string RunKey,
    int Iteration,
    bool DeliberateFailure);
