using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Deterministic P6-11 crash/lease boundary proof against real Kestrel and a
/// Mongo replica set. Testing-only marker pauses make each hard-kill boundary
/// observable without adding production timing behavior.
/// </summary>
internal static partial class P5MaterializationProbe
{
    public static async Task<int> RunP611ChaosBoundaryAsync()
    {
        var runKey =
            $"p611chaos_{Guid.NewGuid().ToString("N")[..12]}";
        var paths = HarnessPaths.Create(runKey);
        var iterationRoot = paths.IterationRoot(1);
        var cleanupErrors = new List<string>();
        var exchanges = new List<ApiExchangeEvidence>();
        var directMongoCases = new List<object>();
        var startedBackends = new List<BackendServerLease>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        ApiHarnessClient? apiClient = null;
        ProbeFixture? fixture = null;
        string? adminPassword = null;
        string? failure = null;
        var casesPassed = false;

        async Task StartBackendAsync(
            string phase,
            BackendServerOptions? options = null)
        {
            Require(
                backend is null && apiClient is null,
                $"P6-11 cannot start {phase} while another backend is active.");
            var serverRoot = Path.Combine(iterationRoot, phase);
            Directory.CreateDirectory(serverRoot);
            backend = await BackendServerLease.StartAsync(
                paths,
                serverRoot,
                runKey,
                mongo ?? throw new InvalidOperationException(
                    "P6-11 Mongo must start before Kestrel."),
                CancellationToken.None,
                options ?? new BackendServerOptions
                {
                    EnableDynamicFlowRuntimeCandidate = true,
                    DynamicFlowRuntimeCandidateActivationThrough = 12
                });
            startedBackends.Add(backend);
            apiClient = new ApiHarnessClient(backend.BaseUri);
            if (fixture is not null)
            {
                await RestoreCandidateDefinitionAsync(
                    mongo.Client.GetDatabase(mongo.DatabaseName),
                    fixture.T01VersionId,
                    CancellationToken.None);
            }
        }

        async Task StopBackendAsync()
        {
            var lease = backend;
            var client = apiClient;
            backend = null;
            apiClient = null;
            Exception? stopError = null;
            if (lease is not null)
            {
                try
                {
                    await lease.StopAsync();
                }
                catch (Exception error)
                {
                    stopError = error;
                }
            }
            if (client is not null)
            {
                exchanges.AddRange(client.Exchanges);
                client.Dispose();
            }
            if (lease is not null)
                await lease.DisposeAsync();
            if (stopError is not null)
                throw stopError;
        }

        async Task<string> LoginAsync()
        {
            Require(
                apiClient is not null && adminPassword is not null,
                "P6-11 login prerequisites are missing.");
            return await apiClient!.LoginAsync(
                "admin",
                adminPassword!,
                CancellationToken.None);
        }

        try
        {
            mongo = await MongoReplicaSetLease.StartAsync(
                paths,
                iterationRoot,
                runKey,
                1,
                CancellationToken.None);
            var database = mongo.Client.GetDatabase(mongo.DatabaseName);

            await StartBackendAsync("backend-seed");
            var seedApi = apiClient!;
            var bootstrap = await BootstrapAndLoginAsync(
                seedApi,
                backend!,
                database,
                CancellationToken.None);
            adminPassword = bootstrap.Password;
            fixture = await SeedFixtureAsync(
                database,
                CancellationToken.None);
            await StopBackendAsync();

            var targetUnitIds = new[] { fixture.TargetUnitIds[0] };

            // Boundary 1: process death while the intent transaction is open.
            const string beforeCommitCommand =
                "p6-11-hard-kill-before-instance-write";
            const string beforeCommitPeriod = "2026-07-P611-BEFORE";
            var beforeCommitRoot = Path.Combine(
                iterationRoot,
                "markers-before-instance");
            var beforeCommitReached = Path.Combine(
                beforeCommitRoot,
                "reached.marker");
            var beforeCommitRelease = Path.Combine(
                beforeCommitRoot,
                "release.marker");
            await StartBackendAsync(
                "backend-before-instance-kill",
                P611PauseOptions(
                    beforeCommitCommand,
                    0,
                    DynamicFlowRuntimeFaultPoints.BeforeInstanceWrite,
                    beforeCommitReached,
                    beforeCommitRelease));
            var beforeCommitApi = apiClient!;
            var beforeCommitToken = await LoginAsync();
            var beforeCommitPreflight = await PreflightAsync(
                beforeCommitApi,
                beforeCommitToken,
                fixture.CoreWorkId,
                fixture.T01VersionId,
                beforeCommitCommand,
                targetUnitIds,
                beforeCommitPeriod,
                CancellationToken.None);
            var beforeCommitTransactionBaseline =
                await CaptureP611MongoTransactionGaugeAsync(
                    mongo.Client.GetDatabase("admin"),
                    CancellationToken.None);
            Require(
                beforeCommitTransactionBaseline.CurrentOpen == 0 &&
                beforeCommitTransactionBaseline.CurrentActive == 0 &&
                beforeCommitTransactionBaseline.CurrentInactive == 0,
                $"P6-11 hard-kill boundary requires an isolated Mongo transaction baseline: {beforeCommitTransactionBaseline}");
            var beforeCommitRequest = ConfirmAsync(
                beforeCommitApi,
                beforeCommitToken,
                fixture.CoreWorkId,
                fixture.T01VersionId,
                beforeCommitCommand,
                targetUnitIds,
                beforeCommitPeriod,
                beforeCommitPreflight.SnapshotToken,
                CancellationToken.None);
            await WaitForP611MarkerAsync(
                beforeCommitReached,
                backend!,
                CancellationToken.None);
            await backend!.StopAsync();
            var beforeCommitInterrupted =
                await ObserveP611InterruptedRequestAsync(beforeCommitRequest);
            Require(
                beforeCommitInterrupted.Interrupted,
                "P6-11 BEFORE_INSTANCE_WRITE request survived a hard process kill.");
            await StopBackendAsync();
            var beforeCommitCrashLedger = await CaptureP611LedgerAsync(
                database,
                fixture.CoreWorkId,
                fixture.T01VersionId,
                beforeCommitCommand,
                CancellationToken.None);
            Require(
                beforeCommitCrashLedger.TotalBusinessRows == 0 &&
                string.IsNullOrWhiteSpace(
                    beforeCommitCrashLedger.WorkAssignmentTopologyOwner) &&
                string.IsNullOrWhiteSpace(
                    beforeCommitCrashLedger.WorkRuntimeInstanceId),
                "P6-11 BEFORE_INSTANCE_WRITE hard kill leaked a business-ledger row.");
            var beforeCommitOrphanRecovery =
                await WaitForP611OrphanTransactionRecoveryAsync(
                    mongo.Client.GetDatabase("admin"),
                    beforeCommitTransactionBaseline,
                    CancellationToken.None);

            await StartBackendAsync("backend-before-instance-replay");
            var beforeCommitReplayApi = apiClient!;
            var beforeCommitReplayToken = await LoginAsync();
            var beforeCommitReplay = await ConfirmAsync(
                beforeCommitReplayApi,
                beforeCommitReplayToken,
                fixture.CoreWorkId,
                fixture.T01VersionId,
                beforeCommitCommand,
                targetUnitIds,
                beforeCommitPeriod,
                beforeCommitPreflight.SnapshotToken,
                CancellationToken.None);
            RequireStatus(
                beforeCommitReplay,
                "SUCCEEDED",
                "P6-11 BEFORE_INSTANCE_WRITE restart replay");
            var beforeCommitFinal = await AssertP611OneMaterializationAsync(
                database,
                fixture,
                fixture.CoreWorkId,
                beforeCommitCommand,
                beforeCommitPeriod,
                expectedAttemptCount: 0,
                expectRecoveryEvents: false,
                CancellationToken.None);
            directMongoCases.Add(new
            {
                caseId = "P6-11-HARD-KILL-BEFORE-INSTANCE-WRITE",
                verdict = "PASS",
                marker = beforeCommitReached,
                interruptedRequest = beforeCommitInterrupted,
                afterCrash = beforeCommitCrashLedger,
                orphanTransactionRecovery = beforeCommitOrphanRecovery,
                restartReplayCount = 1,
                afterRestartReplay = beforeCommitFinal
            });
            await StopBackendAsync();

            // Boundary 2: process death after the core intent transaction commits.
            const string afterIntentCommand =
                "p6-11-hard-kill-after-intent-commit";
            const string afterIntentPeriod = "2026-07-P611-AFTER";
            var afterIntentRoot = Path.Combine(
                iterationRoot,
                "markers-after-intent");
            var afterIntentReached = Path.Combine(
                afterIntentRoot,
                "reached.marker");
            var afterIntentRelease = Path.Combine(
                afterIntentRoot,
                "release.marker");
            await StartBackendAsync(
                "backend-after-intent-kill",
                P611PauseOptions(
                    afterIntentCommand,
                    0,
                    DynamicFlowRuntimeFaultPoints.AfterIntentCommit,
                    afterIntentReached,
                    afterIntentRelease));
            var afterIntentApi = apiClient!;
            var afterIntentToken = await LoginAsync();
            var afterIntentPreflight = await PreflightAsync(
                afterIntentApi,
                afterIntentToken,
                fixture.BranchWorkId,
                fixture.T01VersionId,
                afterIntentCommand,
                targetUnitIds,
                afterIntentPeriod,
                CancellationToken.None);
            var afterIntentRequest = ConfirmAsync(
                afterIntentApi,
                afterIntentToken,
                fixture.BranchWorkId,
                fixture.T01VersionId,
                afterIntentCommand,
                targetUnitIds,
                afterIntentPeriod,
                afterIntentPreflight.SnapshotToken,
                CancellationToken.None);
            await WaitForP611MarkerAsync(
                afterIntentReached,
                backend!,
                CancellationToken.None);
            await backend!.StopAsync();
            var afterIntentInterrupted =
                await ObserveP611InterruptedRequestAsync(afterIntentRequest);
            Require(
                afterIntentInterrupted.Interrupted,
                "P6-11 AFTER_INTENT_COMMIT request survived a hard process kill.");
            await StopBackendAsync();
            var durableIntent =
                await ValidateDurableIntentBeforeMaterializationAsync(
                    database,
                    fixture.BranchWorkId,
                    afterIntentCommand,
                    targetUnitIds.Length,
                    CancellationToken.None);
            var afterIntentCrashLedger = await CaptureP611LedgerAsync(
                database,
                fixture.BranchWorkId,
                fixture.T01VersionId,
                afterIntentCommand,
                CancellationToken.None);
            Require(
                afterIntentCrashLedger.ReceiptCount == 1 &&
                afterIntentCrashLedger.InstanceCount == 1 &&
                afterIntentCrashLedger.ParticipantSnapshotCount == 1 &&
                afterIntentCrashLedger.StepCount == 1 &&
                afterIntentCrashLedger.EventCount == 1 &&
                afterIntentCrashLedger.OutboxCount == 1 &&
                afterIntentCrashLedger.WorkAssignmentTopologyOwner ==
                WorkAssignmentTopologyOwners.P5FlowRuntime &&
                afterIntentCrashLedger.WorkRuntimeInstanceId ==
                afterIntentCrashLedger.FlowInstanceId,
                "P6-11 AFTER_INTENT_COMMIT did not preserve the exact durable intent.");
            Require(
                afterIntentCrashLedger.AssignmentCount == 0 &&
                afterIntentCrashLedger.PeriodCount == 0 &&
                afterIntentCrashLedger.ReportCount == 0 &&
                afterIntentCrashLedger.QueueCount == 0,
                "P6-11 AFTER_INTENT_COMMIT leaked a materialized projection.");

            await StartBackendAsync("backend-after-intent-reconcile");
            var afterIntentRecoveryApi = apiClient!;
            var afterIntentRecoveryToken = await LoginAsync();
            var afterIntentReceipt = await database
                .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                    "dynamic_flow_runtime_command_receipts")
                .Find(item =>
                    item.CommandId == afterIntentCommand &&
                    item.WorkId == fixture.BranchWorkId)
                .SingleAsync();
            var afterIntentInstanceId = afterIntentReceipt.FlowInstanceId
                ?? throw new InvalidOperationException(
                    "P6-11 AFTER_INTENT_COMMIT receipt lacks flow instance.");
            var afterIntentReconcile = await afterIntentRecoveryApi.PostAsync(
                $"api/admin/operations/dynamic-flow-runtime/instances/{afterIntentInstanceId}/reconcile?apply=true",
                new { },
                afterIntentRecoveryToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                afterIntentReconcile,
                HttpStatusCode.OK,
                "P6-11 AFTER_INTENT_COMMIT reconcile");
            Require(
                ApiHarnessClient.RequiredBool(
                    afterIntentReconcile.Json,
                    "converged") &&
                ApiHarnessClient.RequiredBool(
                    afterIntentReconcile.Json,
                    "exactLedgerConverged"),
                "P6-11 AFTER_INTENT_COMMIT did not converge in one reconcile.");
            var afterIntentReplay = await ConfirmAsync(
                afterIntentRecoveryApi,
                afterIntentRecoveryToken,
                fixture.BranchWorkId,
                fixture.T01VersionId,
                afterIntentCommand,
                targetUnitIds,
                afterIntentPeriod,
                afterIntentPreflight.SnapshotToken,
                CancellationToken.None);
            RequireStatus(
                afterIntentReplay,
                "SUCCEEDED",
                "P6-11 AFTER_INTENT_COMMIT exact replay");
            var afterIntentFinal = await AssertP611OneMaterializationAsync(
                database,
                fixture,
                fixture.BranchWorkId,
                afterIntentCommand,
                afterIntentPeriod,
                expectedAttemptCount: 0,
                expectRecoveryEvents: true,
                CancellationToken.None);
            directMongoCases.Add(new
            {
                caseId = "P6-11-HARD-KILL-AFTER-INTENT-COMMIT",
                verdict = "PASS",
                marker = afterIntentReached,
                interruptedRequest = afterIntentInterrupted,
                durableIntent,
                afterCrash = afterIntentCrashLedger,
                reconcileConverged =
                    ApiHarnessClient.RequiredBool(
                        afterIntentReconcile.Json,
                        "converged"),
                reconcileCount = 1,
                afterRestartReconcile = afterIntentFinal
            });
            await StopBackendAsync();

            // Boundary 3: replace and expire a claimed lease, then let a fresh
            // worker finish before releasing the stale worker.
            const string leaseLossCommand =
                "p6-11-after-outbox-claim-lease-loss";
            const string leaseLossPeriod = "2026-07-P611-LEASE";
            var leaseLossRoot = Path.Combine(
                iterationRoot,
                "markers-after-outbox-claim");
            var leaseLossReached = Path.Combine(
                leaseLossRoot,
                "reached.marker");
            var leaseLossRelease = Path.Combine(
                leaseLossRoot,
                "release.marker");
            await StartBackendAsync(
                "backend-after-outbox-claim",
                P611PauseOptions(
                    leaseLossCommand,
                    1,
                    DynamicFlowRuntimeFaultPoints.AfterOutboxClaim,
                    leaseLossReached,
                    leaseLossRelease));
            var leaseLossApi = apiClient!;
            var leaseLossToken = await LoginAsync();
            var leaseLossPreflight = await PreflightAsync(
                leaseLossApi,
                leaseLossToken,
                fixture.SideFaultGroupAWorkId,
                fixture.T01VersionId,
                leaseLossCommand,
                targetUnitIds,
                leaseLossPeriod,
                CancellationToken.None);
            var leaseLossRequest = ConfirmAsync(
                leaseLossApi,
                leaseLossToken,
                fixture.SideFaultGroupAWorkId,
                fixture.T01VersionId,
                leaseLossCommand,
                targetUnitIds,
                leaseLossPeriod,
                leaseLossPreflight.SnapshotToken,
                CancellationToken.None);
            await WaitForP611MarkerAsync(
                leaseLossReached,
                backend!,
                CancellationToken.None);
            var leaseLossInstanceId = P611StableObjectId(
                $"{fixture.SideFaultGroupAWorkId}\n{fixture.T01VersionId}\n{leaseLossCommand}");
            var outboxCollection =
                database.GetCollection<DynamicFlowRuntimeOutboxItem>(
                    "dynamic_flow_runtime_outbox");
            var staleClaim = await outboxCollection
                .Find(item =>
                    item.FlowInstanceId == leaseLossInstanceId &&
                    item.Status ==
                    DynamicFlowRuntimeOutboxStatuses.Processing)
                .SingleAsync();
            Require(
                !string.IsNullOrWhiteSpace(staleClaim.LeaseId) &&
                staleClaim.LeaseUntilUtc > DateTime.UtcNow,
                "P6-11 AFTER_OUTBOX_CLAIM did not expose a live claimed lease.");
            var staleLeaseId = staleClaim.LeaseId!;
            var replacementLeaseId = ObjectId.GenerateNewId().ToString();
            var replacementLeaseUntilUtc =
                DateTime.UtcNow.AddSeconds(-1);
            var replaced = await outboxCollection.UpdateOneAsync(
                item =>
                    item.Id == staleClaim.Id &&
                    item.Status ==
                    DynamicFlowRuntimeOutboxStatuses.Processing &&
                    item.LeaseId == staleLeaseId,
                Builders<DynamicFlowRuntimeOutboxItem>.Update
                    .Set(item => item.LeaseId, replacementLeaseId)
                    .Set(
                        item => item.LeaseUntilUtc,
                        replacementLeaseUntilUtc)
                    .Set(item => item.UpdatedAtUtc, DateTime.UtcNow));
            Require(
                replaced.ModifiedCount == 1,
                "P6-11 direct-Mongo lease replacement CAS lost its target.");
            ApiHarnessResponse freshWorker;
            using (var freshApi = new ApiHarnessClient(backend!.BaseUri))
            {
                try
                {
                    freshWorker = await freshApi.PostAsync(
                        "api/admin/operations/dynamic-flow-runtime/outbox/process?maxItems=1",
                        body: null,
                        leaseLossToken,
                        ct: CancellationToken.None);
                }
                finally
                {
                    exchanges.AddRange(freshApi.Exchanges);
                }
            }
            ApiHarnessClient.ExpectStatus(
                freshWorker,
                HttpStatusCode.OK,
                "P6-11 fresh outbox worker");
            Require(
                ApiHarnessClient.RequiredInt(
                    freshWorker.Json,
                    "processed") == 1,
                "P6-11 fresh worker did not process the expired replacement lease.");
            var afterFreshWorker = await outboxCollection
                .Find(item => item.Id == staleClaim.Id)
                .SingleAsync();
            Require(
                afterFreshWorker.Status ==
                DynamicFlowRuntimeOutboxStatuses.Completed &&
                afterFreshWorker.AttemptCount == 0,
                "P6-11 fresh worker did not complete the claim exactly once.");
            CreateP611ReleaseMarker(leaseLossRelease);
            var staleWorkerResponse =
                await leaseLossRequest.WaitAsync(TimeSpan.FromSeconds(20));
            RequireStatus(
                staleWorkerResponse,
                "SUCCEEDED",
                "P6-11 released stale worker response");
            var leaseLossFinal = await AssertP611OneMaterializationAsync(
                database,
                fixture,
                fixture.SideFaultGroupAWorkId,
                leaseLossCommand,
                leaseLossPeriod,
                expectedAttemptCount: 0,
                expectRecoveryEvents: false,
                CancellationToken.None);
            var staleBackendStdout = backend!.StdoutPath;
            var staleBackendStderr = backend.StderrPath;
            await StopBackendAsync();
            var leaseLossLogs =
                (File.Exists(staleBackendStdout)
                    ? await File.ReadAllTextAsync(staleBackendStdout)
                    : string.Empty) +
                Environment.NewLine +
                (File.Exists(staleBackendStderr)
                    ? await File.ReadAllTextAsync(staleBackendStderr)
                    : string.Empty);
            var staleLeaseProtectionObserved = leaseLossLogs.Contains(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_LEASE_LOST",
                StringComparison.Ordinal);
            Require(
                staleLeaseProtectionObserved,
                "P6-11 did not observe stale-lease protection in Kestrel logs.");
            directMongoCases.Add(new
            {
                caseId = "P6-11-AFTER-OUTBOX-CLAIM-LEASE-LOSS",
                verdict = "PASS",
                marker = leaseLossReached,
                staleClaim = new
                {
                    staleClaim.Id,
                    staleLeaseId,
                    staleClaim.LeaseUntilUtc
                },
                directMongoReplacement = new
                {
                    replacementLeaseId,
                    expiredAtUtc = replacementLeaseUntilUtc,
                    modifiedCount = replaced.ModifiedCount
                },
                freshWorkerProcessed =
                    ApiHarnessClient.RequiredInt(
                        freshWorker.Json,
                        "processed"),
                staleLeaseProtectionObserved,
                afterFreshWorkerStatus = afterFreshWorker.Status,
                afterRelease = leaseLossFinal
            });

            // Boundary 4: the existing transient assignment-write failure must
            // remain restart/reconcile recoverable and idempotent.
            const string transientCommand =
                "p6-11-transient-after-assignment-write";
            const string transientPeriod = "2026-07-P611-TRANSIENT";
            await StartBackendAsync(
                "backend-transient-assignment-fault",
                new BackendServerOptions
                {
                    EnableDynamicFlowRuntimeCandidate = true,
                    DynamicFlowRuntimeCandidateActivationThrough = 12,
                    DynamicFlowRuntimeFaultCommandId = transientCommand,
                    DynamicFlowRuntimeFaultBranchOrdinal = 1,
                    DynamicFlowRuntimeFaultPoints =
                    [
                        DynamicFlowRuntimeFaultPoints.AfterAssignmentWrite
                    ]
                });
            var transientApi = apiClient!;
            var transientToken = await LoginAsync();
            var transientPreflight = await PreflightAsync(
                transientApi,
                transientToken,
                fixture.ReconcileFaultWorkId,
                fixture.T01VersionId,
                transientCommand,
                targetUnitIds,
                transientPeriod,
                CancellationToken.None);
            var transient = await ConfirmAsync(
                transientApi,
                transientToken,
                fixture.ReconcileFaultWorkId,
                fixture.T01VersionId,
                transientCommand,
                targetUnitIds,
                transientPeriod,
                transientPreflight.SnapshotToken,
                CancellationToken.None);
            RequireStatus(
                transient,
                "RECOVERY_REQUIRED",
                "P6-11 AFTER_ASSIGNMENT_WRITE transient failure");
            var transientPartial = await ValidateBranchPartialAsync(
                database,
                transientCommand,
                targetUnitIds.Length,
                CancellationToken.None);
            var transientCrashLedger = await CaptureP611LedgerAsync(
                database,
                fixture.ReconcileFaultWorkId,
                fixture.T01VersionId,
                transientCommand,
                CancellationToken.None);
            Require(
                transientCrashLedger.AssignmentCount == 1 &&
                transientCrashLedger.BindingCount == 1 &&
                transientCrashLedger.PeriodCount == 0 &&
                transientCrashLedger.QueueCount == 0 &&
                transientCrashLedger.OutboxAttemptCounts
                    .SequenceEqual(new[] { 1 }),
                "P6-11 transient fault did not preserve the expected partial ledger.");
            await StopBackendAsync();

            await StartBackendAsync("backend-transient-reconcile");
            var transientRecoveryApi = apiClient!;
            var transientRecoveryToken = await LoginAsync();
            var transientInstanceId = P611StableObjectId(
                $"{fixture.ReconcileFaultWorkId}\n{fixture.T01VersionId}\n{transientCommand}");
            var transientReconcile = await transientRecoveryApi.PostAsync(
                $"api/admin/operations/dynamic-flow-runtime/instances/{transientInstanceId}/reconcile?apply=true",
                new { },
                transientRecoveryToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                transientReconcile,
                HttpStatusCode.OK,
                "P6-11 transient reconcile");
            Require(
                ApiHarnessClient.RequiredBool(
                    transientReconcile.Json,
                    "converged") &&
                ApiHarnessClient.RequiredBool(
                    transientReconcile.Json,
                    "exactLedgerConverged"),
                "P6-11 transient assignment failure did not reconcile once.");
            var transientReplay = await ConfirmAsync(
                transientRecoveryApi,
                transientRecoveryToken,
                fixture.ReconcileFaultWorkId,
                fixture.T01VersionId,
                transientCommand,
                targetUnitIds,
                transientPeriod,
                transientPreflight.SnapshotToken,
                CancellationToken.None);
            RequireStatus(
                transientReplay,
                "SUCCEEDED",
                "P6-11 transient exact replay");
            var transientFinal = await AssertP611OneMaterializationAsync(
                database,
                fixture,
                fixture.ReconcileFaultWorkId,
                transientCommand,
                transientPeriod,
                expectedAttemptCount: 1,
                expectRecoveryEvents: true,
                CancellationToken.None);
            directMongoCases.Add(new
            {
                caseId = "P6-11-TRANSIENT-AFTER-ASSIGNMENT-WRITE",
                verdict = "PASS",
                partial = transientPartial,
                afterFault = transientCrashLedger,
                reconcileCount = 1,
                reconcileConverged =
                    ApiHarnessClient.RequiredBool(
                        transientReconcile.Json,
                        "converged"),
                afterRestartReconcile = transientFinal
            });
            await StopBackendAsync();

            casesPassed = directMongoCases.Count == 4;
        }
        catch (Exception error)
        {
            failure = $"{error.GetType().Name}: {error.Message}";
            Console.Error.WriteLine(error);
        }
        finally
        {
            if (backend is not null || apiClient is not null)
            {
                try
                {
                    await StopBackendAsync();
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"backend-stop: {error.Message}");
                }
            }
            if (mongo is not null)
            {
                try
                {
                    await mongo.DropDatabaseGuardedAsync(
                        CancellationToken.None);
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"mongo-drop: {error.Message}");
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
                    cleanupErrors.Add(
                        $"mongo-data-remove: {error.Message}");
                }
                await mongo.DisposeAsync();
            }
        }

        var backendCleanupVerified =
            startedBackends.Count > 0 &&
            startedBackends.All(item =>
                item.StopVerified && item.PortReleaseVerified);
        var mongoCleanupVerified =
            mongo is not null &&
            mongo.DatabaseDropVerified &&
            mongo.ProcessStopVerified &&
            mongo.PortReleaseVerified &&
            mongo.DataDirectoryRemovalVerified;
        var cleanupPassed =
            cleanupErrors.Count == 0 &&
            backendCleanupVerified &&
            mongoCleanupVerified;
        var passed = casesPassed && cleanupPassed;
        await EvidenceJson.WriteAsync(
            Path.Combine(
                iterationRoot,
                "p6-11-boundary-api.json"),
            exchanges);
        await EvidenceJson.WriteAsync(
            Path.Combine(
                iterationRoot,
                "p6-11-boundary-direct-mongo.json"),
            new
            {
                runKey,
                cases = directMongoCases
            });
        await EvidenceJson.WriteAsync(
            Path.Combine(
                iterationRoot,
                "p6-11-boundary-cleanup.json"),
            new
            {
                runKey,
                verdict = cleanupPassed ? "PASS" : "FAIL",
                backendCleanupVerified,
                mongoCleanupVerified,
                cleanupErrors,
                backends = startedBackends.Select(item => new
                {
                    item.ProcessId,
                    item.Port,
                    item.StopVerified,
                    item.PortReleaseVerified
                }),
                mongo = mongo is null
                    ? null
                    : new
                    {
                        mongo.DatabaseDropVerified,
                        mongo.ProcessStopVerified,
                        mongo.PortReleaseVerified,
                        mongo.DataDirectoryRemovalVerified
                    }
            });
        var resultPath = Path.Combine(
            iterationRoot,
            "p6-11-boundary-result.json");
        await EvidenceJson.WriteAsync(
            resultPath,
            new
            {
                runKey,
                verdict = passed ? "PASS" : "FAIL",
                requirements = new[] { "P6-TOPO-019" },
                requiredCaseCount = 4,
                passedCaseCount = directMongoCases.Count,
                failure,
                cleanupPassed,
                cleanupErrors,
                artifactRoot = iterationRoot,
                completedAtUtc = DateTime.UtcNow
            });
        Console.WriteLine(
            passed
                ? $"[PASS] P6-11 chaos boundaries passed; artifact={resultPath}"
                : $"[FAIL] P6-11 chaos boundaries failed: {failure ?? string.Join("; ", cleanupErrors)}; artifact={resultPath}");
        return passed ? 0 : 1;
    }

    private static BackendServerOptions P611PauseOptions(
        string commandId,
        int branchOrdinal,
        string point,
        string reachedFile,
        string releaseFile)
        => new()
        {
            EnableDynamicFlowRuntimeCandidate = true,
            DynamicFlowRuntimeCandidateActivationThrough = 12,
            DynamicFlowRuntimePauseCommandId = commandId,
            DynamicFlowRuntimePauseBranchOrdinal = branchOrdinal,
            DynamicFlowRuntimePausePoint = point,
            DynamicFlowRuntimePauseReachedFile = reachedFile,
            DynamicFlowRuntimePauseReleaseFile = releaseFile
        };

    private static async Task WaitForP611MarkerAsync(
        string markerPath,
        BackendServerLease backend,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(markerPath))
                return;
            await Task.Delay(25, ct);
        }
        throw new TimeoutException(
            $"P6-11 pause marker was not reached: {markerPath}\nSTDOUT:\n{LogTail.Read(backend.StdoutPath)}\nSTDERR:\n{LogTail.Read(backend.StderrPath)}");
    }

    private static void CreateP611ReleaseMarker(string markerPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(markerPath))!;
        Directory.CreateDirectory(directory);
        var temporary =
            $"{markerPath}.{Guid.NewGuid().ToString("N")[..12]}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read))
            {
                stream.WriteByte(1);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, markerPath);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static async Task<P611InterruptedRequest>
        ObserveP611InterruptedRequestAsync(
            Task<ApiHarnessResponse> request)
    {
        try
        {
            var response = await request.WaitAsync(TimeSpan.FromSeconds(10));
            return new P611InterruptedRequest(
                false,
                null,
                $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception error) when (
            error is HttpRequestException or
                TaskCanceledException or
                IOException or
                ObjectDisposedException)
        {
            return new P611InterruptedRequest(
                true,
                error.GetType().Name,
                error.Message);
        }
    }

    private static async Task<P611MongoTransactionGauge>
        CaptureP611MongoTransactionGaugeAsync(
            IMongoDatabase adminDatabase,
            CancellationToken ct)
    {
        var status = await adminDatabase.RunCommandAsync<BsonDocument>(
            new BsonDocument("serverStatus", 1),
            cancellationToken: ct);
        var transactions = status.GetValue(
            "transactions",
            BsonNull.Value);
        Require(
            transactions.IsBsonDocument,
            "P6-11 Mongo serverStatus lacks transaction metrics.");
        var metrics = transactions.AsBsonDocument;
        return new P611MongoTransactionGauge(
            P611TransactionMetric(metrics, "currentOpen"),
            P611TransactionMetric(metrics, "currentActive"),
            P611TransactionMetric(metrics, "currentInactive"),
            P611TransactionMetric(metrics, "totalStarted"),
            P611TransactionMetric(metrics, "totalAborted"));
    }

    private static async Task<P611OrphanTransactionRecovery>
        WaitForP611OrphanTransactionRecoveryAsync(
            IMongoDatabase adminDatabase,
            P611MongoTransactionGauge baseline,
            CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        var deadline = startedAtUtc.AddSeconds(120);
        var pollCount = 0;
        P611MongoTransactionGauge? afterKill = null;
        P611MongoTransactionGauge? latest = null;
        P611MongoTransactionGauge? terminal = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var current = await CaptureP611MongoTransactionGaugeAsync(
                adminDatabase,
                ct);
            pollCount++;
            afterKill ??= current;
            latest = current;
            Require(
                current.CurrentOpen ==
                current.CurrentActive + current.CurrentInactive,
                $"P6-11 Mongo transaction gauges are inconsistent: {current}");
            if (current.CurrentOpen == 0 &&
                current.TotalStarted == baseline.TotalStarted + 1 &&
                current.TotalAborted >= baseline.TotalAborted + 1)
            {
                terminal = current;
                break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        var observedAfterKill = afterKill ??
            throw new InvalidOperationException(
                "P6-11 captured no post-kill Mongo transaction metric.");
        Require(
            observedAfterKill.TotalStarted ==
            baseline.TotalStarted + 1 &&
            (observedAfterKill.CurrentOpen == 1 ||
             observedAfterKill.TotalAborted > baseline.TotalAborted),
            "P6-11 did not observe the killed Mongo transaction.");
        var observedTerminal = terminal ??
            throw new InvalidOperationException(
                $"P6-11 orphan Mongo transaction did not abort within 120 seconds. baseline={baseline}; first={observedAfterKill}; latest={latest}");
        return new P611OrphanTransactionRecovery(
            baseline,
            observedAfterKill,
            observedTerminal,
            pollCount,
            (long)(DateTime.UtcNow - startedAtUtc).TotalMilliseconds);
    }

    private static long P611TransactionMetric(
        BsonDocument metrics,
        string name)
    {
        Require(
            metrics.TryGetValue(name, out var value) &&
            value.IsNumeric,
            $"P6-11 Mongo transaction metric is missing: {name}.");
        return value.ToInt64();
    }

    private static async Task<P611LedgerSnapshot> CaptureP611LedgerAsync(
        IMongoDatabase database,
        string workId,
        string versionId,
        string commandId,
        CancellationToken ct)
    {
        var instanceId = P611StableObjectId(
            $"{workId}\n{versionId}\n{commandId}");
        var work = await database
            .GetCollection<Work>("works")
            .Find(item => item.Id == workId && !item.IsDeleted)
            .SingleAsync(ct);
        var receiptCount = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                "dynamic_flow_runtime_command_receipts")
            .CountDocumentsAsync(
                item =>
                    item.CommandId == commandId &&
                    item.WorkId == workId,
                cancellationToken: ct);
        var instances = await database
            .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(item => item.Id == instanceId)
            .ToListAsync(ct);
        var snapshotCount = await database
            .GetCollection<DynamicFlowParticipantSnapshot>(
                "dynamic_flow_participant_snapshots")
            .CountDocumentsAsync(
                item => item.FlowInstanceId == instanceId,
                cancellationToken: ct);
        var stepCount = await database
            .GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .CountDocumentsAsync(
                item => item.FlowInstanceId == instanceId,
                cancellationToken: ct);
        var eventCount = await database
            .GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events")
            .CountDocumentsAsync(
                item => item.FlowInstanceId == instanceId,
                cancellationToken: ct);
        var outbox = await database
            .GetCollection<DynamicFlowRuntimeOutboxItem>(
                "dynamic_flow_runtime_outbox")
            .Find(item => item.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        var assignmentCount = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .CountDocumentsAsync(
                item =>
                    (item.FlowInstanceId == instanceId ||
                     item.WorkId == workId) &&
                    !item.IsDeleted,
                cancellationToken: ct);
        var bindingCount = await database
            .GetCollection<WorkTemplateAssignee>(
                "work_template_assignees")
            .CountDocumentsAsync(
                item => item.WorkId == workId && !item.IsDeleted,
                cancellationToken: ct);
        var periodCount = await database
            .GetCollection<WorkReportPeriod>("work_report_periods")
            .CountDocumentsAsync(
                item => item.WorkId == workId && !item.IsDeleted,
                cancellationToken: ct);
        var reportCount = await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .CountDocumentsAsync(
                item => item.WorkId == workId && !item.IsDeleted,
                cancellationToken: ct);
        var queueCount = await database
            .GetCollection<WorkAssignmentQueueItem>(
                "work_assignment_queue")
            .CountDocumentsAsync(
                item => item.WorkId == workId && !item.IsDeleted,
                cancellationToken: ct);
        return new P611LedgerSnapshot(
            instanceId,
            receiptCount,
            instances.Count,
            snapshotCount,
            stepCount,
            eventCount,
            outbox.Count,
            assignmentCount,
            bindingCount,
            periodCount,
            reportCount,
            queueCount,
            work.AssignmentTopologyOwner,
            work.DynamicFlowRuntimeInstanceId,
            instances.Select(item => item.State).ToArray(),
            outbox.Select(item => item.Status).ToArray(),
            outbox.Select(item => item.AttemptCount).ToArray(),
            outbox.Select(item => item.LeaseId).ToArray());
    }

    private static async Task<P611LedgerSnapshot>
        AssertP611OneMaterializationAsync(
            IMongoDatabase database,
            ProbeFixture fixture,
            string workId,
            string commandId,
            string periodKey,
            int expectedAttemptCount,
            bool expectRecoveryEvents,
            CancellationToken ct)
    {
        await ValidateConvergedAsync(
            database,
            fixture,
            workId,
            commandId,
            fixture.T01VersionId,
            "FLOW-T01",
            [fixture.TargetUnitIds[0]],
            periodKey,
            expectRecoveryEvents,
            ct);
        var ledger = await CaptureP611LedgerAsync(
            database,
            workId,
            fixture.T01VersionId,
            commandId,
            ct);
        Require(
            ledger.ReceiptCount == 1 &&
            ledger.InstanceCount == 1 &&
            ledger.ParticipantSnapshotCount == 1 &&
            ledger.StepCount == 1 &&
            ledger.OutboxCount == 1 &&
            ledger.AssignmentCount == 1 &&
            ledger.BindingCount == 1 &&
            ledger.PeriodCount == 1 &&
            ledger.ReportCount == 0 &&
            ledger.QueueCount == 1,
            $"P6-11 {commandId} materialization cardinality drifted.");
        Require(
            ledger.OutboxStatuses.SequenceEqual(
                new[] { DynamicFlowRuntimeOutboxStatuses.Completed }) &&
            ledger.OutboxAttemptCounts.SequenceEqual(
                new[] { expectedAttemptCount }) &&
            ledger.OutboxLeaseIds.All(string.IsNullOrWhiteSpace),
            $"P6-11 {commandId} outbox terminal state drifted.");

        var step = await database
            .GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(item => item.FlowInstanceId == ledger.FlowInstanceId)
            .SingleAsync(ct);
        var assignment = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .Find(item =>
                item.FlowInstanceId == ledger.FlowInstanceId &&
                !item.IsDeleted)
            .SingleAsync(ct);
        var outbox = await database
            .GetCollection<DynamicFlowRuntimeOutboxItem>(
                "dynamic_flow_runtime_outbox")
            .Find(item => item.FlowInstanceId == ledger.FlowInstanceId)
            .SingleAsync(ct);
        var binding = await database
            .GetCollection<WorkTemplateAssignee>(
                "work_template_assignees")
            .Find(item => item.WorkId == workId && !item.IsDeleted)
            .SingleAsync(ct);
        var period = await database
            .GetCollection<WorkReportPeriod>("work_report_periods")
            .Find(item => item.WorkId == workId && !item.IsDeleted)
            .SingleAsync(ct);
        var queue = await database
            .GetCollection<WorkAssignmentQueueItem>(
                "work_assignment_queue")
            .Find(item => item.WorkId == workId && !item.IsDeleted)
            .SingleAsync(ct);
        Require(
            step.AssignmentId == assignment.Id &&
            outbox.StepInstanceId == step.Id &&
            outbox.Payload["commandId"].AsString == commandId &&
            binding.WorkAssignmentId == assignment.Id &&
            period.WorkAssignmentId == assignment.Id &&
            queue.WorkAssignmentId == assignment.Id,
            $"P6-11 {commandId} produced an orphan or mismatched projection.");
        return ledger;
    }

    private sealed record P611InterruptedRequest(
        bool Interrupted,
        string? ErrorType,
        string Message);

    private sealed record P611MongoTransactionGauge(
        long CurrentOpen,
        long CurrentActive,
        long CurrentInactive,
        long TotalStarted,
        long TotalAborted);

    private sealed record P611OrphanTransactionRecovery(
        P611MongoTransactionGauge Baseline,
        P611MongoTransactionGauge AfterKill,
        P611MongoTransactionGauge Terminal,
        int PollCount,
        long WaitElapsedMilliseconds);

    private static string P611StableObjectId(string seed)
        => Hash(seed)[..24];

    private sealed record P611LedgerSnapshot(
        string FlowInstanceId,
        long ReceiptCount,
        int InstanceCount,
        long ParticipantSnapshotCount,
        long StepCount,
        long EventCount,
        int OutboxCount,
        long AssignmentCount,
        long BindingCount,
        long PeriodCount,
        long ReportCount,
        long QueueCount,
        string? WorkAssignmentTopologyOwner,
        string? WorkRuntimeInstanceId,
        IReadOnlyList<string> InstanceStates,
        IReadOnlyList<string> OutboxStatuses,
        IReadOnlyList<int> OutboxAttemptCounts,
        IReadOnlyList<string?> OutboxLeaseIds)
    {
        public long TotalBusinessRows =>
            ReceiptCount +
            InstanceCount +
            ParticipantSnapshotCount +
            StepCount +
            EventCount +
            OutboxCount +
            AssignmentCount +
            BindingCount +
            PeriodCount +
            ReportCount +
            QueueCount;
    }
}
