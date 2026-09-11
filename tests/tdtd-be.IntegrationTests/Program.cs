using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains(P11DownloadedFileSelectionSelftest.CommandLineSwitch, StringComparer.Ordinal))
            return P11DownloadedFileSelectionSelftest.Run();

        if (args.Contains("--p11-independent-diagnostic-readiness-selftest", StringComparer.Ordinal))
            return P10ReconciliationCoreProbe.RunIndependentDiagnosticReadinessSelftest();

        if (args.Contains("--p11-server-export-cleanup-selftest", StringComparer.Ordinal))
            return P11ServerExportCleanupSelftest.Run(args);

        if (args.Contains(P10ReconciliationCoreProbe.IndependentDiagnosticSwitch, StringComparer.Ordinal))
            return await P10ReconciliationCoreProbe.RunIndependentDiagnosticFixtureAsync(args);

        if (args.Contains("--p11-attempt039-recheck-regression", StringComparer.Ordinal))
        {
            return await P10ReconciliationCoreProbe
                .RunP11DirectReconciliationRegressionAsync(attempt039RecoveryProbe: true);
        }

        if (args.Any(x => string.Equals(
                x,
                P11ResultBrowserFixture.CommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P11ResultBrowserFixture.RunAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P11CoreBrowserFixture.CommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P11CoreBrowserFixture.RunAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P5MaterializationProbe.P11CommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P5MaterializationProbe.RunP11SourceBoundDiagnosticAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P10ReconciliationCoreProbe.P11DirectReconciliationRegressionCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P10ReconciliationCoreProbe
                .RunP11DirectReconciliationRegressionAsync();
        }

        if (args.Any(x => string.Equals(
                x,
                P10ReconciliationCoreProbe.EffectiveMappingDirectRegressionCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P10ReconciliationCoreProbe.RunEffectiveMappingDirectRegressionAsync();
        }
        if (args.Any(x => string.Equals(
                x,
                P10ReconciliationCoreProbe.CloseoutBrowserCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P10ReconciliationCoreProbe.RunCloseoutBrowserAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P10ReconciliationRollbackProbe.CommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P10ReconciliationRollbackProbe.RunAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P10ReconciliationRaceProbe.CommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P10ReconciliationRaceProbe.RunAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P10TrustedFinalizerProbe.CommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P10TrustedFinalizerProbe.RunAsync();
        }

        if (args.Any(x => string.Equals(
                x,
                P10ReconciliationCoreProbe.CommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P10ReconciliationCoreProbe.RunAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P9StatRunCoreProbe.CloseoutBrowserCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P9StatRunCoreProbe.RunCloseoutBrowserAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P9StatRunCoreProbe.RaceCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P9StatRunCoreProbe.RunRaceAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P9StatRunCoreProbe.UiCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P9StatRunCoreProbe.RunUiAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P9StatRunCoreProbe.ExportCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P9StatRunCoreProbe.RunExportAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P9StatRunCoreProbe.OperationsCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P9StatRunCoreProbe.RunOperationsAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P9StatRunCoreProbe.FlowContributionCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P9StatRunCoreProbe.RunFlowContributionAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P9StatRunCoreProbe.DiffCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P9StatRunCoreProbe.RunDiffAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P9StatRunCoreProbe.AdvancedCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P9StatRunCoreProbe.RunAdvancedAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P9StatRunCoreProbe.BasicCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P9StatRunCoreProbe.RunBasicAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P9StatRunCoreProbe.DirectCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P9StatRunCoreProbe.RunDirectAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P9StatRunCoreProbe.FoundationRefreshV2RegressionCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P9StatRunCoreProbe
                .RunFoundationRefreshV2RegressionAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P9StatRunCoreProbe.LifecycleCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)) ||
            args.Any(x => string.Equals(
                x,
                P9StatRunCoreProbe.LifecycleCompatibilityCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P9StatRunCoreProbe.RunLifecycleAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P9StatRunCoreProbe.CommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P9StatRunCoreProbe.RunAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P8StatConfigProbe.RollbackCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P8StatConfigProbe.RunP812RollbackProbeAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P8StatConfigProbe.CommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)) ||
            args.Any(x => string.Equals(
                x,
                P8StatConfigProbe.BrowserFixtureSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P8StatConfigProbe.RunAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P7MappingChaosChildRunner.CommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P7MappingChaosChildRunner.RunAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                P7ChaosGateRunner.CommandLineSwitch,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P7ChaosGateRunner.RunAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                "--p7-mapping-gate",
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P5MaterializationProbe.RunP7MappingGateAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                "--p7-browser-fixture",
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P5MaterializationProbe.RunP712BrowserFixtureAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                "--p7-rollback-probe",
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P5MaterializationProbe.RunP712RollbackProbeAsync(args);
        }

        if (args.Any(x => string.Equals(x, "--p6-chaos-boundary-probe", StringComparison.OrdinalIgnoreCase)))
            return await P5MaterializationProbe.RunP611ChaosBoundaryAsync();

        if (args.Any(x => string.Equals(x, "--p6-chaos-gate", StringComparison.OrdinalIgnoreCase)))
            return await P6ChaosGateRunner.RunAsync(args);

        if (args.Any(x => string.Equals(x, "--p6-epoch-probe", StringComparison.OrdinalIgnoreCase)))
            return await P5MaterializationProbe.RunP610Async();

        if (args.Any(x => string.Equals(x, "--p6-supplemental-probe", StringComparison.OrdinalIgnoreCase)))
            return await P5MaterializationProbe.RunP609Async();

        if (args.Any(x => string.Equals(x, "--p6-periodic-probe", StringComparison.OrdinalIgnoreCase)))
            return await P5MaterializationProbe.RunP608Async();

        if (args.Any(x => string.Equals(x, "--p6-subflow-probe", StringComparison.OrdinalIgnoreCase)))
            return await P5MaterializationProbe.RunP607Async();

        if (args.Any(x => string.Equals(x, "--p6-review-loop-probe", StringComparison.OrdinalIgnoreCase)))
            return await P5MaterializationProbe.RunP606Async();

        if (args.Any(x => string.Equals(x, "--p6-typed-conditional-probe", StringComparison.OrdinalIgnoreCase)))
            return await P5MaterializationProbe.RunP605Async();

        if (args.Any(x => string.Equals(x, "--p6-join-quorum-probe", StringComparison.OrdinalIgnoreCase)))
            return await P5MaterializationProbe.RunP604Async();

        if (args.Any(x => string.Equals(x, "--p6-join-all-probe", StringComparison.OrdinalIgnoreCase)))
            return await P5MaterializationProbe.RunP603Async();

        if (args.Any(x => string.Equals(x, "--p6-parallel-fork-probe", StringComparison.OrdinalIgnoreCase)))
            return await P5MaterializationProbe.RunP602Async();

        if (args.Any(x => string.Equals(x, "--p6-sequential-probe", StringComparison.OrdinalIgnoreCase)))
            return await P5MaterializationProbe.RunP601Async();

        if (args.Any(x => string.Equals(
                x,
                "--p6-browser-fixture",
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P5MaterializationProbe.RunP612BrowserFixtureAsync(args);
        }

        if (args.Any(x => string.Equals(
                x,
                "--p5-browser-fixture",
                StringComparison.OrdinalIgnoreCase)))
        {
            return await P5MaterializationProbe.RunBrowserFixtureAsync(args);
        }

        if (args.Any(x => string.Equals(x, "--p5-chaos-gate", StringComparison.OrdinalIgnoreCase)))
            return await P5ChaosGateRunner.RunAsync(args);

        if (args.Any(x => string.Equals(x, "--p5-materialization-probe", StringComparison.OrdinalIgnoreCase)))
            return await P5MaterializationProbe.RunAsync();

        if (args.Any(x => string.Equals(x, "--p5-negative-probe", StringComparison.OrdinalIgnoreCase)))
            return await RunP5NegativeProbeAsync();

        var browserFixture = BrowserFixtureOptions.Parse(args);
        var iterations = browserFixture.Enabled ? 1 : ParseIterations(args);
        var deliberateFailure = args.Any(x => string.Equals(x, "--deliberate-failure", StringComparison.OrdinalIgnoreCase));
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var startedAt = DateTime.UtcNow;
        var runKey = BuildRunKey();
        HarnessPaths? paths = null;
        var iterationResults = new List<IterationResult>();
        string? fatalFailure = null;

        try
        {
            paths = HarnessPaths.Create(runKey);
            var mongodPath = HarnessPaths.ResolveMongodPath();
            var backendDll = paths.ResolveBackendDll();
            await EvidenceJson.WriteAsync(
                Path.Combine(paths.RunRoot, "environment.json"),
                EvidenceJson.EnvironmentSnapshot(runKey, mongodPath, backendDll, iterations),
                cancellation.Token);

            Console.WriteLine($"P1 integration artifacts: {paths.RunRoot}");
            for (var iteration = 1; iteration <= iterations; iteration++)
            {
                var result = await RunIterationAsync(
                    paths,
                    runKey,
                    iteration,
                    deliberateFailure,
                    browserFixture,
                    cancellation.Token);
                iterationResults.Add(result);
            }
        }
        catch (Exception ex)
        {
            fatalFailure = $"{ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine(ex);
        }

        var deterministic = iterationResults.Count == iterations &&
                            iterationResults.Select(x => x.NormalizedSha256).Distinct(StringComparer.Ordinal).Count() == 1;
        var passed = fatalFailure is null &&
                     deterministic &&
                     iterationResults.Count == iterations &&
                     iterationResults.All(x => x.CleanupSucceeded && x.Cases.All(c => c.Verdict == HarnessVerdict.DAT));
        var failureReason = fatalFailure;
        if (failureReason is null && !deterministic)
            failureReason = "Normalized results differ across clean iterations.";
        if (failureReason is null && iterationResults.Any(x => !x.CleanupSucceeded))
            failureReason = "One or more iteration cleanups failed.";
        if (failureReason is null && iterationResults.SelectMany(x => x.Cases).Any(x => x.Verdict != HarnessVerdict.DAT))
            failureReason = "One or more assertion-derived cases did not pass.";

        var resultPayload = new HarnessResult(
            runKey,
            startedAt,
            DateTime.UtcNow,
            iterations,
            iterationResults,
            deterministic,
            passed,
            failureReason);
        if (paths is not null)
        {
            await EvidenceJson.WriteAsync(Path.Combine(paths.RunRoot, "results.json"), resultPayload);
            await EvidenceCsv.WriteCasesAsync(
                Path.Combine(paths.RunRoot, "results.csv"),
                iterationResults.SelectMany(x => x.Cases.Select(c => (x.Iteration, Case: c))));
            await EvidenceJson.WriteAsync(
                Path.Combine(paths.RunRoot, "reconciliation-ledger.json"),
                new
                {
                    runKey,
                    expectedIterations = iterations,
                    actualIterations = iterationResults.Count,
                    normalized = iterationResults.Select(x => new
                    {
                        x.Iteration,
                        x.NormalizedSha256,
                        allCasesDat = x.Cases.All(c => c.Verdict == HarnessVerdict.DAT),
                        x.CleanupSucceeded
                    }),
                    deterministic,
                    deliberateFailureMode = deliberateFailure,
                    browserFixtureMode = browserFixture.Enabled,
                    passed
                });
            await EvidenceJson.WriteAsync(
                Path.Combine(paths.RunRoot, "cleanup-manifest.json"),
                new
                {
                    runKey,
                    completedAtUtc = DateTime.UtcNow,
                    iterations = iterationResults.Select(x => new
                    {
                        x.Iteration,
                        x.DatabaseName,
                        x.CleanupSucceeded,
                        x.CleanupErrors
                    })
                });
        }

        Console.WriteLine(
            passed
                ? $"[DAT] P1 integration harness passed {iterations} clean iteration(s); deterministic={deterministic}."
                : $"[KHONG_DAT] P1 integration harness failed: {failureReason}");
        return passed ? 0 : 1;
    }

    private static async Task<int> RunP5NegativeProbeAsync()
    {
        var runKey = $"p5neg_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}";
        var paths = HarnessPaths.Create(runKey);
        var iterationRoot = paths.IterationRoot(1);
        MongoReplicaSetLease? mongo = null;
        try
        {
            mongo = await MongoReplicaSetLease.StartAsync(paths, iterationRoot, runKey, 1, CancellationToken.None);
            var db = mongo.Client.GetDatabase(mongo.DatabaseName);
            var collections = new[]
            {
                "dynamic_flow_instances",
                "dynamic_flow_step_instances",
                "dynamic_flow_participant_snapshots",
                "dynamic_flow_runtime_command_receipts",
                "dynamic_flow_runtime_events",
                "dynamic_flow_runtime_outbox",
                "work_assignments",
                "work_assignment_report"
            };
            var before = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var collection in collections)
                before[collection] = await db.GetCollection<BsonDocument>(collection)
                    .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);

            var blocked = new DynamicFlowPreflightResponse
            {
                CommandId = "p5-negative-blocked",
                RequestHash = new string('a', 64),
                SnapshotToken = new string('b', 64),
                Eligibility = DynamicFlowRuntimeEligibilityPolicy.BlockedCatalog
            };
            var response = DynamicFlowRuntimePreflightContract.Confirm(
                blocked,
                new DynamicFlowConfirmRequest { SnapshotToken = blocked.SnapshotToken });
            if (response.BusinessWritePerformed || response.Status != "BLOCKED_UNTIL_TARGET_PHASE")
                throw new InvalidOperationException("Blocked confirm did not remain zero-write.");

            try
            {
                DynamicFlowRuntimePreflightContract.Confirm(
                    blocked,
                    new DynamicFlowConfirmRequest { SnapshotToken = new string('c', 64) });
                throw new InvalidOperationException("Stale confirm unexpectedly succeeded.");
            }
            catch (InvalidOperationException error) when (error.Message == "DYNAMIC_FLOW_PREFLIGHT_STALE")
            {
            }

            var after = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var collection in collections)
                after[collection] = await db.GetCollection<BsonDocument>(collection)
                    .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
            if (collections.Any(collection => before[collection] != after[collection]))
                throw new InvalidOperationException("A negative/preflight-only case changed direct Mongo counts.");

            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, "p5-negative-direct-mongo.json"),
                new
                {
                    runKey,
                    verdict = "PASS",
                    cases = new[] { "BLOCKED_V1_1_CONFIRM", "STALE_SNAPSHOT_CONFIRM" },
                    before,
                    after,
                    businessWritePerformed = false
                });
            Console.WriteLine($"[DAT] P5 negative direct-Mongo probe passed; artifact={Path.Combine(iterationRoot, "p5-negative-direct-mongo.json")}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            if (mongo is not null)
            {
                await mongo.DropDatabaseGuardedAsync(CancellationToken.None);
                await mongo.StopProcessAsync();
                mongo.RemoveDataDirectoryGuarded();
                await mongo.DisposeAsync();
            }
        }
    }

    private static async Task<IterationResult> RunIterationAsync(
        HarnessPaths paths,
        string runKey,
        int iteration,
        bool deliberateFailure,
        BrowserFixtureOptions browserFixture,
        CancellationToken ct)
    {
        var iterationRoot = paths.IterationRoot(iteration);
        var cleanupErrors = new List<string>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        IntegrationScenario? scenario = null;
        var cases = new HarnessCaseRunner();
        string databaseName = $"tdtd_p1_unallocated_{iteration:00}";
        string replicaSetName = $"p1rs_unallocated_{iteration:00}";

        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "cleanup-manifest.json"),
            new
            {
                runKey,
                iteration,
                state = "ALLOCATING",
                iterationRoot,
                startedAtUtc = DateTime.UtcNow
            },
            ct);

        try
        {
            mongo = await MongoReplicaSetLease.StartAsync(paths, iterationRoot, runKey, iteration, ct);
            databaseName = mongo.DatabaseName;
            replicaSetName = mongo.ReplicaSetName;
            var legacyProvenanceFixture = await SeedLegacyProvenanceFixtureAsync(mongo, ct);
            backend = await BackendServerLease.StartAsync(paths, iterationRoot, runKey, mongo, ct);
            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, "environment.json"),
                new
                {
                    runKey,
                    iteration,
                    databaseName,
                    replicaSetName,
                    mongoPort = mongo.Port,
                    mongoPid = mongo.ProcessId,
                    backendPort = backend.Port,
                    backendPid = backend.ProcessId,
                    hangfire = new { server = false, dashboard = false, recurring = false },
                    redis = false,
                    lifecycleProjectionOutbox = new
                    {
                        deterministicPostCommitFailureInjection = true,
                        failureCommandId = BackendServerLease.P3LifecycleProjectionFailureCommandId,
                        manualWorkerEndpoint = "POST /api/admin/operations/job-runs/lifecycle-projection-outbox/process"
                    },
                    startedAtUtc = DateTime.UtcNow
                },
                ct);

            scenario = new IntegrationScenario(
                mongo,
                backend,
                iterationRoot,
                deliberateFailure,
                legacyProvenanceFixture);
            if (browserFixture.Enabled)
            {
                await scenario.RunP3BrowserFixtureAsync(cases, browserFixture.Timeout, ct);
            }
            else
            {
                await scenario.RunAsync(cases, ct);
                await scenario.WriteArtifactsAsync(cases.Results, ct);
            }
        }
        catch (Exception ex)
        {
            await cases.RunAsync(
                "P1-BE-000-INFRASTRUCTURE",
                () => Task.FromException<CaseObservation>(ex));
        }
        finally
        {
            scenario?.Dispose();
            if (backend is not null)
            {
                try
                {
                    await backend.StopAsync();
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add($"backend-stop: {ex.Message}");
                }
                await backend.DisposeAsync();
            }

            if (mongo is not null)
            {
                try
                {
                    await mongo.DropDatabaseGuardedAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add($"database-drop: {ex.Message}");
                }
                try
                {
                    await mongo.StopProcessAsync();
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add($"mongo-stop: {ex.Message}");
                }
                try
                {
                    mongo.RemoveDataDirectoryGuarded();
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add($"mongo-data-remove: {ex.Message}");
                }
                await mongo.DisposeAsync();
            }
        }

        var normalizedSha256 = cases.BuildNormalizedSha256();
        var cleanupSucceeded = cleanupErrors.Count == 0 &&
                               (backend is null ||
                                backend.StopVerified &&
                                backend.PortReleaseVerified) &&
                               (mongo is null ||
                                mongo.DatabaseDropVerified &&
                                mongo.ProcessStopVerified &&
                                mongo.PortReleaseVerified &&
                                mongo.DataDirectoryRemovalVerified);
        var result = new IterationResult(
            iteration,
            runKey,
            databaseName,
            replicaSetName,
            cases.Results,
            normalizedSha256,
            cleanupSucceeded,
            cleanupErrors);
        await EvidenceJson.WriteAsync(Path.Combine(iterationRoot, "results.json"), result);
        await EvidenceCsv.WriteCasesAsync(
            Path.Combine(iterationRoot, "results.csv"),
            cases.Results.Select(x => (iteration, Case: x)));
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "cleanup-manifest.json"),
            new
            {
                runKey,
                iteration,
                databaseName,
                replicaSetName,
                state = cleanupSucceeded ? "CLEANED" : "CLEANUP_FAILED",
                databaseDropped = mongo?.DatabaseDropVerified ?? false,
                backendStopped = backend?.StopVerified ?? false,
                backendPortReleased = backend?.PortReleaseVerified ?? false,
                mongoStopped = mongo?.ProcessStopVerified ?? false,
                mongoPortReleased = mongo?.PortReleaseVerified ?? false,
                mongoDataRemoved = mongo?.DataDirectoryRemovalVerified ?? false,
                cleanupErrors,
                completedAtUtc = DateTime.UtcNow
            });
        return result;
    }

    private static async Task<LegacyProvenanceFixture> SeedLegacyProvenanceFixtureAsync(
        MongoReplicaSetLease mongo,
        CancellationToken ct)
    {
        var database = mongo.Client.GetDatabase(mongo.DatabaseName);
        var formId = ObjectId.GenerateNewId();
        var runtimeId = ObjectId.GenerateNewId();
        var flowFamilyId = ObjectId.GenerateNewId();
        var flowVersionId = ObjectId.GenerateNewId();
        var actorId = ObjectId.GenerateNewId();
        var fixedAt = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
        await database.GetCollection<BsonDocument>("dynamic_form_templates").InsertOneAsync(
            new BsonDocument
            {
                ["_id"] = formId,
                ["code"] = "P2_LEGACY_PROVENANCE_FORM",
                ["name"] = "P2 legacy provenance migration fixture",
                ["description"] = "seeded before backend startup",
                ["tagCodes"] = new BsonArray(),
                ["createdByUsername"] = "legacy_owner",
                ["schemaVersion"] = 1,
                ["revision"] = 0,
                ["isActive"] = true,
                ["isPublished"] = true,
                ["sectionsJson"] = "[{\"id\":\"legacy_section\",\"title\":\"Legacy section\",\"order\":1}]",
                ["fieldsJson"] = "[{\"id\":\"legacy_field\",\"sectionId\":\"legacy_section\",\"key\":\"legacy_answer\",\"name\":\"Legacy specific question\",\"type\":\"number\",\"required\":false,\"order\":1}]",
                ["excelBlockJson"] = BsonNull.Value,
                ["blocksJson"] = "[]",
                ["createdAtUtc"] = fixedAt,
                ["updatedAtUtc"] = fixedAt,
                ["createdByUserId"] = actorId,
                ["updatedByUserId"] = actorId,
                ["isDeleted"] = false
            },
            cancellationToken: ct);
        var legacyFlowPayload = $$"""
        {
          "schemaVersion": 1,
          "rootDynamicFormTemplateId": "{{formId}}",
          "formNodes": [
            {"formNodeId":"legacy-form","role":"OWNER","dynamicFormTemplateId":"{{formId}}"}
          ],
          "steps": [
            {"stepId":"legacy-step","stepCode":"START","stepName":"Legacy start","formNodeId":"legacy-form"}
          ],
          "transitions": []
        }
        """;
        var legacyFlowPayloadHash = Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacyFlowPayload)))
            .ToLowerInvariant();
        var legacyFlowFamily = new BsonDocument
        {
            ["_id"] = flowFamilyId,
            ["code"] = "P4_LEGACY_DEFINITION_MIGRATION_FIXTURE",
            ["name"] = "P4 legacy definition migration fixture",
            ["description"] = "seeded before backend startup",
            ["dynamicFormTemplateId"] = formId,
            ["status"] = "ACTIVE",
            ["currentVersionId"] = flowVersionId,
            ["currentVersionNo"] = 1,
            ["currentVersionHash"] = legacyFlowPayloadHash,
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["createdByUserId"] = actorId,
            ["updatedByUserId"] = actorId,
            ["isDeleted"] = false
        };
        var legacyFlowVersion = new BsonDocument
        {
            ["_id"] = flowVersionId,
            ["templateId"] = flowFamilyId,
            ["dynamicFormTemplateId"] = formId,
            ["versionNo"] = 1,
            ["status"] = "LOCKED",
            ["payloadJson"] = legacyFlowPayload,
            ["payloadHash"] = legacyFlowPayloadHash,
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["createdByUserId"] = actorId,
            ["updatedByUserId"] = actorId,
            ["isDeleted"] = false
        };
        await database.GetCollection<BsonDocument>("dynamic_flow_templates")
            .InsertOneAsync(legacyFlowFamily, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("dynamic_flow_template_versions")
            .InsertOneAsync(legacyFlowVersion, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("work_assignments").InsertOneAsync(
            new BsonDocument
            {
                ["_id"] = runtimeId,
                ["dynamicFormTemplateId"] = formId,
                ["isDeleted"] = false
            },
            cancellationToken: ct);
        return new LegacyProvenanceFixture(
            formId.ToString(),
            runtimeId.ToString(),
            "work_assignments",
            flowFamilyId.ToString(),
            flowVersionId.ToString(),
            actorId.ToString(),
            legacyFlowPayload,
            legacyFlowPayloadHash,
            Convert.ToHexString(SHA256.HashData(legacyFlowFamily.ToBson())).ToLowerInvariant(),
            Convert.ToHexString(SHA256.HashData(legacyFlowVersion.ToBson())).ToLowerInvariant());
    }

    private static int ParseIterations(string[] args)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("TDTD_TEST_ITERATIONS");
        var value = int.TryParse(fromEnvironment, out var envValue) ? envValue : 2;
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--iterations", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length &&
                int.TryParse(args[index + 1], out var parsed))
            {
                value = parsed;
            }
        }

        if (value is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(args), "Integration iterations must be between 1 and 5.");
        return value;
    }

    private static string BuildRunKey()
        => $"p1_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
}

internal sealed record LegacyProvenanceFixture(
    string FormId,
    string RuntimeDocumentId,
    string RuntimeCollectionName,
    string FlowFamilyId,
    string FlowVersionId,
    string FlowActorId,
    string FlowPayloadJson,
    string FlowPayloadHash,
    string FlowFamilyBeforeSha256,
    string FlowVersionBeforeSha256);

internal sealed record BrowserFixtureOptions(bool Enabled, TimeSpan Timeout)
{
    private const int DefaultTimeoutSeconds = 1_200;

    public static BrowserFixtureOptions Parse(string[] args)
    {
        var enabled = args.Any(x => string.Equals(x, "--browser-fixture", StringComparison.OrdinalIgnoreCase));
        var configured = Environment.GetEnvironmentVariable("TDTD_BROWSER_FIXTURE_TIMEOUT_SECONDS");
        var timeoutSeconds = int.TryParse(configured, out var environmentValue)
            ? environmentValue
            : DefaultTimeoutSeconds;

        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--browser-fixture-timeout-seconds", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length &&
                int.TryParse(args[index + 1], out var parsed))
            {
                timeoutSeconds = parsed;
            }
        }

        if (timeoutSeconds is < 1 or > 3_600)
            throw new ArgumentOutOfRangeException(nameof(args), "Browser fixture timeout must be between 1 and 3600 seconds.");

        return new BrowserFixtureOptions(enabled, TimeSpan.FromSeconds(timeoutSeconds));
    }
}
