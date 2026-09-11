using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

internal static partial class P5MaterializationProbe
{
    private const string BrowserT01Command = "p5-browser-t01-success";
    private const string BrowserT02Command = "p5-browser-t02-three-unit-success";
    private const string BrowserReadCommand = "p5-browser-runtime-read";
    private const string BrowserForwardCommand = "p5-browser-forward-negative";
    private const string BrowserPartialCommand = "p5-browser-state-partial";
    private const string BrowserRetryingCommand = "p5-browser-state-retrying";
    private const string BrowserReconciledCommand = "p5-browser-state-reconciled";
    private const string BrowserScheduleIdentityJson =
        "{\"anchor\":\"2026-07-23\",\"cadence\":\"once\",\"timezone\":\"Asia/Ho_Chi_Minh\"}";

    internal static async Task<int> RunBrowserFixtureAsync(string[] args)
    {
        var options = P5BrowserFixtureOptions.Parse(args);
        var runKey = options.RunKey ??
                     $"p5browser_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}";
        var paths = HarnessPaths.Create(runKey);
        var iterationRoot = paths.IterationRoot(1);
        var secretPath = Path.Combine(
            iterationRoot,
            "p5-browser-fixture.runtime.secret.json");
        var redactedPath = Path.Combine(
            iterationRoot,
            "p5-browser-fixture.json");
        var stopPath = Path.Combine(
            iterationRoot,
            "p5-browser-fixture.stop");
        var browserResultPath = Path.Combine(
            iterationRoot,
            "p5-browser-result.json");
        var oraclePath = Path.Combine(
            iterationRoot,
            "p5-browser-direct-mongo-oracle.json");
        var cleanupPath = Path.Combine(
            iterationRoot,
            "p5-browser-cleanup.json");
        var cleanupErrors = new List<string>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        ProbeFixture? fixture = null;
        P5BrowserReadFixture? readFixture = null;
        P5BrowserForwardFixture? forwardFixture = null;
        P5BrowserStateFixture? partialFixture = null;
        P5BrowserStateFixture? retryingFixture = null;
        P5BrowserStateFixture? reconciledFixture = null;
        string? adminPassword = null;
        string? failure = null;
        string? stopReason = null;
        string browserVerdict = "NOT_RUN";
        var passed = false;
        var readyAtUtc = DateTime.MinValue;
        var expiresAtUtc = DateTime.MinValue;
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.WriteLine($"P5 browser fixture artifacts: {paths.RunRoot}");
        try
        {
            mongo = await MongoReplicaSetLease.StartAsync(
                paths,
                iterationRoot,
                runKey,
                1,
                cancellation.Token);
            var database = mongo.Client.GetDatabase(mongo.DatabaseName);

            backend = await StartBrowserBackendAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                "seed",
                options.FrontendOrigin,
                cancellation.Token);
            using (var api = new ApiHarnessClient(backend.BaseUri))
            {
                var bootstrap = await BootstrapAndLoginAsync(
                    api,
                    backend,
                    database,
                    cancellation.Token);
                adminPassword = bootstrap.Password;
                fixture = await SeedFixtureAsync(database, cancellation.Token);
                await SeedBrowserIssuerWorkAclAsync(
                    database,
                    fixture,
                    cancellation.Token);
                await RequireBrowserIssuerWorkReadAsync(
                    api,
                    bootstrap.Token,
                    fixture,
                    cancellation.Token);
            }
            await StopBrowserBackendAsync(backend);
            backend = null;
            await ClearBrowserBackfillManifestsAsync(
                database,
                fixture,
                cancellation.Token);

            backend = await StartBrowserBackendAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                "partial",
                options.FrontendOrigin,
                cancellation.Token,
                BrowserPartialCommand,
                2,
                [DynamicFlowRuntimeFaultPoints.AfterAssignmentWrite]);
            await RestoreBrowserCandidateDefinitionsAsync(
                database,
                fixture,
                cancellation.Token);
            using (var api = new ApiHarnessClient(backend.BaseUri))
            {
                var partialAdminToken = await api.LoginAsync(
                    "admin",
                    adminPassword,
                    cancellation.Token);
                var preflight = await PreflightAsync(
                    api,
                    partialAdminToken,
                    fixture.BranchWorkId,
                    fixture.T01VersionId,
                    BrowserPartialCommand,
                    fixture.TargetUnitIds,
                    "2026-07-BROWSER-PARTIAL",
                    cancellation.Token);
                var partial = await ConfirmAsync(
                    api,
                    partialAdminToken,
                    fixture.BranchWorkId,
                    fixture.T01VersionId,
                    BrowserPartialCommand,
                    fixture.TargetUnitIds,
                    "2026-07-BROWSER-PARTIAL",
                    preflight.SnapshotToken,
                    cancellation.Token);
                RequireStatus(
                    partial,
                    "RECOVERY_REQUIRED",
                    "P5 browser PARTIAL fixture");
                await ValidateBranchPartialAsync(
                    database,
                    BrowserPartialCommand,
                    fixture.TargetUnitIds.Count,
                    cancellation.Token);
                partialFixture = await CaptureBrowserStateFixtureAsync(
                    database,
                    fixture.BranchWorkId,
                    BrowserPartialCommand,
                    DynamicFlowInstanceStates.Partial,
                    cancellation.Token);
            }
            await StopBrowserBackendAsync(backend);
            backend = null;
            await ClearBrowserBackfillManifestsAsync(
                database,
                fixture,
                cancellation.Token);

            string retrySnapshotToken;
            backend = await StartBrowserBackendAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                "retrying-partial",
                options.FrontendOrigin,
                cancellation.Token,
                BrowserRetryingCommand,
                2,
                [DynamicFlowRuntimeFaultPoints.AfterAssignmentWrite]);
            await RestoreBrowserCandidateDefinitionsAsync(
                database,
                fixture,
                cancellation.Token);
            using (var api = new ApiHarnessClient(backend.BaseUri))
            {
                var retryPrecursorAdminToken = await api.LoginAsync(
                    "admin",
                    adminPassword,
                    cancellation.Token);
                var preflight = await PreflightAsync(
                    api,
                    retryPrecursorAdminToken,
                    fixture.SideFaultGroupAWorkId,
                    fixture.T01VersionId,
                    BrowserRetryingCommand,
                    fixture.TargetUnitIds,
                    "2026-07-BROWSER-RETRYING",
                    cancellation.Token);
                retrySnapshotToken = preflight.SnapshotToken;
                var partial = await ConfirmAsync(
                    api,
                    retryPrecursorAdminToken,
                    fixture.SideFaultGroupAWorkId,
                    fixture.T01VersionId,
                    BrowserRetryingCommand,
                    fixture.TargetUnitIds,
                    "2026-07-BROWSER-RETRYING",
                    retrySnapshotToken,
                    cancellation.Token);
                RequireStatus(
                    partial,
                    "RECOVERY_REQUIRED",
                    "P5 browser RETRYING precursor");
            }
            await StopBrowserBackendAsync(backend);
            backend = null;
            await ClearBrowserBackfillManifestsAsync(
                database,
                fixture,
                cancellation.Token);

            backend = await StartBrowserBackendAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                "retrying",
                options.FrontendOrigin,
                cancellation.Token,
                BrowserRetryingCommand,
                null,
                [DynamicFlowRuntimeFaultPoints.AfterRetryStateWrite]);
            await RestoreBrowserCandidateDefinitionsAsync(
                database,
                fixture,
                cancellation.Token);
            using (var api = new ApiHarnessClient(backend.BaseUri))
            {
                var retryAdminToken = await api.LoginAsync(
                    "admin",
                    adminPassword,
                    cancellation.Token);
                var retry = await ConfirmAsync(
                    api,
                    retryAdminToken,
                    fixture.SideFaultGroupAWorkId,
                    fixture.T01VersionId,
                    BrowserRetryingCommand,
                    fixture.TargetUnitIds,
                    "2026-07-BROWSER-RETRYING",
                    retrySnapshotToken,
                    cancellation.Token);
                Require(
                    (int)retry.StatusCode is >= 500 and < 600,
                    $"P5 browser RETRYING injection expected HTTP 5xx, got {(int)retry.StatusCode}.");
                retryingFixture = await CaptureBrowserStateFixtureAsync(
                    database,
                    fixture.SideFaultGroupAWorkId,
                    BrowserRetryingCommand,
                    DynamicFlowInstanceStates.Retrying,
                    cancellation.Token);
            }
            await StopBrowserBackendAsync(backend);
            backend = null;
            await ClearBrowserBackfillManifestsAsync(
                database,
                fixture,
                cancellation.Token);

            backend = await StartBrowserBackendAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                "live",
                options.FrontendOrigin,
                cancellation.Token,
                BrowserT02Command,
                null,
                [DynamicFlowRuntimeFaultPoints.AfterIntentCommit]);
            await RestoreBrowserCandidateDefinitionsAsync(
                database,
                fixture,
                cancellation.Token);

            string adminToken;
            ProbePersistenceSnapshot readSnapshot;
            ProbePersistenceSnapshot forwardSnapshot;
            using (var api = new ApiHarnessClient(backend.BaseUri))
            {
                adminToken = await api.LoginAsync(
                    "admin",
                    adminPassword,
                    cancellation.Token);

                var readLaunch = await LaunchAsync(
                    api,
                    adminToken,
                    fixture,
                    fixture.MultiUnitWorkId,
                    fixture.T01VersionId,
                    BrowserReadCommand,
                    fixture.TargetUnitIds,
                    "2026-07-BROWSER-READ",
                    cancellation.Token);
                RequireStatus(
                    readLaunch.Confirm,
                    "SUCCEEDED",
                    "P5 browser read fixture");
                readSnapshot = await ValidateConvergedAsync(
                    database,
                    fixture,
                    fixture.MultiUnitWorkId,
                    BrowserReadCommand,
                    fixture.T01VersionId,
                    "FLOW-T01",
                    fixture.TargetUnitIds,
                    "2026-07-BROWSER-READ",
                    false,
                    cancellation.Token);

                var forwardTargets = fixture.TargetUnitIds.Take(1).ToArray();
                var forwardLaunch = await LaunchAsync(
                    api,
                    adminToken,
                    fixture,
                    fixture.StateWorkId,
                    fixture.T01VersionId,
                    BrowserForwardCommand,
                    forwardTargets,
                    "2026-07-BROWSER-FORWARD",
                    cancellation.Token);
                RequireStatus(
                    forwardLaunch.Confirm,
                    "SUCCEEDED",
                    "P5 browser forward fixture");
                forwardSnapshot = await ValidateConvergedAsync(
                    database,
                    fixture,
                    fixture.StateWorkId,
                    BrowserForwardCommand,
                    fixture.T01VersionId,
                    "FLOW-T01",
                    forwardTargets,
                    "2026-07-BROWSER-FORWARD",
                    false,
                    cancellation.Token);

                var reconciledTargets = fixture.TargetUnitIds.Skip(1).Take(1).ToArray();
                var reconciledLaunch = await LaunchAsync(
                    api,
                    adminToken,
                    fixture,
                    fixture.ReconcileFaultWorkId,
                    fixture.T01VersionId,
                    BrowserReconciledCommand,
                    reconciledTargets,
                    "2026-07-BROWSER-RECONCILED",
                    cancellation.Token);
                RequireStatus(
                    reconciledLaunch.Confirm,
                    "SUCCEEDED",
                    "P5 browser RECONCILED precursor");
                var reconciledSnapshot = await ValidateConvergedAsync(
                    database,
                    fixture,
                    fixture.ReconcileFaultWorkId,
                    BrowserReconciledCommand,
                    fixture.T01VersionId,
                    "FLOW-T01",
                    reconciledTargets,
                    "2026-07-BROWSER-RECONCILED",
                    false,
                    cancellation.Token);
                await InjectBrowserReconciledStateAsync(
                    database,
                    reconciledSnapshot.FlowInstanceId,
                    cancellation.Token);
                reconciledFixture = await CaptureBrowserStateFixtureAsync(
                    database,
                    fixture.ReconcileFaultWorkId,
                    BrowserReconciledCommand,
                    DynamicFlowInstanceStates.Reconciled,
                    cancellation.Token);

                readFixture = await SeedBrowserReadFixtureAsync(
                    api,
                    adminToken,
                    adminPassword,
                    backend,
                    database,
                    fixture,
                    readSnapshot.FlowInstanceId,
                    cancellation.Token);
                forwardFixture = await BuildBrowserForwardFixtureAsync(
                    database,
                    fixture.StateWorkId,
                    forwardSnapshot.FlowInstanceId,
                    cancellation.Token);
            }

            readyAtUtc = DateTime.UtcNow;
            expiresAtUtc = readyAtUtc.Add(options.Timeout);
            var manifest = BuildBrowserSecretManifest(
                runKey,
                backend,
                mongo,
                options,
                readyAtUtc,
                expiresAtUtc,
                stopPath,
                browserResultPath,
                fixture,
                readFixture,
                forwardFixture,
                partialFixture,
                retryingFixture,
                reconciledFixture);
            await EvidenceJson.WriteAsync(
                secretPath,
                manifest,
                cancellation.Token);
            await EvidenceJson.WriteAsync(
                redactedPath,
                BuildBrowserRedactedManifest(
                    "READY",
                    runKey,
                    backend,
                    options,
                    readyAtUtc,
                    expiresAtUtc,
                    stopPath,
                    browserResultPath,
                    fixture,
                    readFixture,
                    forwardFixture,
                    partialFixture,
                    retryingFixture,
                    reconciledFixture,
                    stopReason: null,
                    secretManifestDeleted: false),
                cancellation.Token);

            Console.WriteLine(
                $"P5_BROWSER_FIXTURE_READY={Path.GetFullPath(secretPath)}");
            Console.WriteLine(
                $"P5_BROWSER_FIXTURE_REDACTED={Path.GetFullPath(redactedPath)}");
            Console.WriteLine(
                $"P5_BROWSER_FIXTURE_STOP_FILE={Path.GetFullPath(stopPath)}");
            Console.Out.Flush();

            stopReason = await WaitForBrowserStopAsync(
                stopPath,
                expiresAtUtc,
                cancellation.Token);
            Require(
                stopReason == "STOP_FILE",
                "P5 browser fixture timed out before the browser stop-file was created.");
            var browserResult = await ReadBrowserResultAsync(
                browserResultPath,
                cancellation.Token);
            browserVerdict = RequireBrowserResult(browserResult);
            var oracle = await RunBrowserDirectMongoOracleAsync(
                database,
                fixture,
                readFixture,
                forwardFixture,
                partialFixture,
                retryingFixture,
                reconciledFixture,
                browserResult,
                cancellation.Token);
            await EvidenceJson.WriteAsync(
                oraclePath,
                oracle,
                cancellation.Token);
            passed = true;

            if (File.Exists(secretPath))
                File.Delete(secretPath);
            if (File.Exists(stopPath))
                File.Delete(stopPath);
            await EvidenceJson.WriteAsync(
                redactedPath,
                BuildBrowserRedactedManifest(
                    "STOPPED",
                    runKey,
                    backend,
                    options,
                    readyAtUtc,
                    expiresAtUtc,
                    stopPath,
                    browserResultPath,
                    fixture,
                    readFixture,
                    forwardFixture,
                    partialFixture,
                    retryingFixture,
                    reconciledFixture,
                    stopReason,
                    secretManifestDeleted: true),
                CancellationToken.None);
        }
        catch (Exception error)
        {
            failure = $"{error.GetType().Name}: {error.Message}";
            Console.Error.WriteLine(error);
        }
        finally
        {
            TryDeleteBrowserFixtureFile(secretPath, cleanupErrors);
            TryDeleteBrowserFixtureFile(stopPath, cleanupErrors);
            if (readyAtUtc != DateTime.MinValue &&
                backend is not null &&
                fixture is not null &&
                readFixture is not null &&
                forwardFixture is not null &&
                partialFixture is not null &&
                retryingFixture is not null &&
                reconciledFixture is not null)
            {
                try
                {
                    await EvidenceJson.WriteAsync(
                        redactedPath,
                        BuildBrowserRedactedManifest(
                            "STOPPED",
                            runKey,
                            backend,
                            options,
                            readyAtUtc,
                            expiresAtUtc,
                            stopPath,
                            browserResultPath,
                            fixture,
                            readFixture,
                            forwardFixture,
                            partialFixture,
                            retryingFixture,
                            reconciledFixture,
                            stopReason ?? "FAILURE",
                            secretManifestDeleted:
                                !File.Exists(secretPath)),
                        CancellationToken.None);
                }
                catch (Exception error)
                {
                    cleanupErrors.Add(
                        $"redacted-manifest: {error.Message}");
                }
            }

            if (backend is not null)
            {
                try
                {
                    await StopBrowserBackendAsync(backend);
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"backend: {error.Message}");
                }
            }

            if (mongo is not null)
            {
                try
                {
                    await mongo.DropDatabaseGuardedAsync(CancellationToken.None);
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
                    cleanupErrors.Add($"mongo-data: {error.Message}");
                }
                try
                {
                    await mongo.DisposeAsync();
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"mongo-dispose: {error.Message}");
                }
            }

            if (cleanupErrors.Count > 0)
            {
                passed = false;
                failure ??= "One or more P5 browser fixture cleanup checks failed.";
            }
            await EvidenceJson.WriteAsync(
                cleanupPath,
                new
                {
                    runKey,
                    verdict = cleanupErrors.Count == 0 ? "PASS" : "FAIL",
                    secretManifestDeleted = !File.Exists(secretPath),
                    stopFileDeleted = !File.Exists(stopPath),
                    backendStopped = backend is null || backend.StopVerified,
                    backendPortReleased =
                        backend is null || backend.PortReleaseVerified,
                    mongoDatabaseDropped =
                        mongo is null || mongo.DatabaseDropVerified,
                    mongoStopped =
                        mongo is null || mongo.ProcessStopVerified,
                    mongoPortReleased =
                        mongo is null || mongo.PortReleaseVerified,
                    mongoDataDirectoryRemoved =
                        mongo is null || mongo.DataDirectoryRemovalVerified,
                    errors = cleanupErrors
                },
                CancellationToken.None);
            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, "p5-browser-fixture-result.json"),
                new
                {
                    runKey,
                    verdict = passed ? "PASS" : "FAIL",
                    browserVerdict,
                    failure,
                    oraclePath,
                    cleanupPath,
                    completedAtUtc = DateTime.UtcNow
                },
                CancellationToken.None);
        }

        Console.WriteLine(
            passed
                ? $"[DAT] P5 browser fixture/oracle passed; artifact={paths.RunRoot}"
                : $"[KHONG_DAT] P5 browser fixture/oracle failed: {failure}");
        return passed ? 0 : 1;
    }
}

internal static partial class P5MaterializationProbe
{
    private static object BuildBrowserSecretManifest(
        string runKey,
        BackendServerLease backend,
        MongoReplicaSetLease mongo,
        P5BrowserFixtureOptions options,
        DateTime readyAtUtc,
        DateTime expiresAtUtc,
        string stopPath,
        string browserResultPath,
        ProbeFixture fixture,
        P5BrowserReadFixture read,
        P5BrowserForwardFixture forward,
        P5BrowserStateFixture partial,
        P5BrowserStateFixture retrying,
        P5BrowserStateFixture reconciled)
        => new
        {
            schemaVersion = 1,
            containsSecrets = true,
            doNotPublish = true,
            runKey,
            backendBaseUrl =
                backend.BaseUri.GetLeftPart(UriPartial.Authority),
            apiBaseUrl =
                $"{backend.BaseUri.GetLeftPart(UriPartial.Authority)}/api",
            frontendOrigin =
                options.FrontendOrigin.GetLeftPart(UriPartial.Authority),
            databaseName = mongo.DatabaseName,
            activation = new
            {
                catalogActivated =
                    DynamicFlowRuntimeCatalogCandidate.ActivationEnabled,
                testingCandidateOverrideEnabled =
                    !DynamicFlowRuntimeCatalogCandidate.ActivationEnabled
            },
            lifecycle = new
            {
                state = "READY",
                readyAtUtc,
                expiresAtUtc,
                stopFile = Path.GetFullPath(stopPath),
                browserResultFile =
                    Path.GetFullPath(browserResultPath),
                stopProtocol =
                    "Write the non-secret browser result JSON, then create the stop file."
            },
            actors = new
            {
                issuer = read.Issuer,
                reporter = read.Reporter,
                reviewer = read.Reviewer,
                outsider = read.Outsider
            },
            journeys = BuildBrowserJourneys(
                fixture,
                read,
                forward,
                partial,
                retrying,
                reconciled)
        };

    private static object BuildBrowserRedactedManifest(
        string state,
        string runKey,
        BackendServerLease backend,
        P5BrowserFixtureOptions options,
        DateTime readyAtUtc,
        DateTime expiresAtUtc,
        string stopPath,
        string browserResultPath,
        ProbeFixture fixture,
        P5BrowserReadFixture read,
        P5BrowserForwardFixture forward,
        P5BrowserStateFixture partial,
        P5BrowserStateFixture retrying,
        P5BrowserStateFixture reconciled,
        string? stopReason,
        bool secretManifestDeleted)
        => new
        {
            schemaVersion = 1,
            containsSecrets = false,
            credentials = "REDACTED",
            runKey,
            backendBaseUrl =
                backend.BaseUri.GetLeftPart(UriPartial.Authority),
            apiBaseUrl =
                $"{backend.BaseUri.GetLeftPart(UriPartial.Authority)}/api",
            frontendOrigin =
                options.FrontendOrigin.GetLeftPart(UriPartial.Authority),
            activation = new
            {
                catalogActivated =
                    DynamicFlowRuntimeCatalogCandidate.ActivationEnabled,
                testingCandidateOverrideEnabled =
                    !DynamicFlowRuntimeCatalogCandidate.ActivationEnabled
            },
            lifecycle = new
            {
                state,
                readyAtUtc,
                expiresAtUtc,
                stopFile = Path.GetFullPath(stopPath),
                browserResultFile =
                    Path.GetFullPath(browserResultPath),
                stopReason,
                secretManifestDeleted
            },
            actors = new
            {
                issuer = RedactBrowserActor(read.Issuer),
                reporter = RedactBrowserActor(read.Reporter),
                reviewer = RedactBrowserActor(read.Reviewer),
                outsider = RedactBrowserActor(read.Outsider)
            },
            journeys = BuildBrowserJourneys(
                fixture,
                read,
                forward,
                partial,
                retrying,
                reconciled)
        };

    private static object RedactBrowserActor(
        P5BrowserActorFixture actor)
        => new
        {
            actor.Username,
            actor.UserId,
            actor.Role,
            password = "REDACTED"
        };

    private static object BuildBrowserJourneys(
        ProbeFixture fixture,
        P5BrowserReadFixture read,
        P5BrowserForwardFixture forward,
        P5BrowserStateFixture partial,
        P5BrowserStateFixture retrying,
        P5BrowserStateFixture reconciled)
    {
        var t01Targets = fixture.TargetUnitIds.Take(1).ToArray();
        var t02Targets = fixture.TargetUnitIds.ToArray();
        var duplicateTargets = new[]
        {
            fixture.TargetUnitIds[2],
            fixture.TargetUnitIds[0],
            fixture.TargetUnitIds[1],
            fixture.TargetUnitIds[0],
            fixture.TargetUnitIds[2]
        };
        return new
        {
            t01 = new
            {
                workId = fixture.T01WorkId,
                versionId = fixture.T01VersionId,
                targetUnitIds = t01Targets,
                duplicateTargetUnitIds = new[]
                {
                    t01Targets[0],
                    t01Targets[0]
                },
                commandId = BrowserT01Command,
                periodKey = "2026-07-BROWSER-T01",
                scheduleIdentityJson =
                    BrowserScheduleIdentityJson,
                workPath =
                    $"/works/{fixture.T01WorkId}?tab=ASSIGN",
                preflightPath =
                    $"/api/works/{fixture.T01WorkId}/dynamic-flows/preflight",
                confirmPath =
                    $"/api/works/{fixture.T01WorkId}/dynamic-flows/confirm"
            },
            t02 = new
            {
                workId = fixture.T02WorkId,
                versionId = fixture.T02VersionId,
                targetUnitIds = t02Targets,
                duplicateTargetUnitIds = duplicateTargets,
                commandId = BrowserT02Command,
                periodKey = "2026-07-BROWSER-T02",
                scheduleIdentityJson =
                    BrowserScheduleIdentityJson,
                workPath =
                    $"/works/{fixture.T02WorkId}?tab=ASSIGN",
                preflightPath =
                    $"/api/works/{fixture.T02WorkId}/dynamic-flows/preflight",
                confirmPath =
                    $"/api/works/{fixture.T02WorkId}/dynamic-flows/confirm"
            },
            read = new
            {
                read.WorkId,
                instanceId = read.FlowInstanceId,
                instancePath =
                    $"/works/{read.WorkId}/flow-instances/{read.FlowInstanceId}/overview",
                apiPath =
                    $"/api/works/{read.WorkId}/dynamic-flows/instances/{read.FlowInstanceId}",
                inboxPath = "/api/dynamic-flows/inbox",
                reporterStep = read.ReporterStep,
                reviewerStep = read.ReviewerStep
            },
            states = new
            {
                partial,
                retrying,
                reconciled
            },
            forward = new
            {
                forward.WorkId,
                instanceId = forward.FlowInstanceId,
                forward.StepInstanceId,
                forward.BranchId,
                forward.AttemptNo,
                forward.AssignmentId,
                forward.Path,
                body = new { },
                forward.ExpectedStatus,
                errorCode = forward.ReasonCode,
                reasonCode = forward.ReasonCode
            }
        };
    }

    private static async Task<string> WaitForBrowserStopAsync(
        string stopPath,
        DateTime expiresAtUtc,
        CancellationToken ct)
    {
        while (DateTime.UtcNow < expiresAtUtc)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(stopPath))
                return "STOP_FILE";
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
        return "TIMEOUT";
    }

    private static async Task<JsonObject> ReadBrowserResultAsync(
        string browserResultPath,
        CancellationToken ct)
    {
        Exception? last = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(browserResultPath))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(
                        browserResultPath,
                        ct);
                    return JsonNode.Parse(text) as JsonObject
                           ?? throw new InvalidOperationException(
                               "P5 browser result must be a JSON object.");
                }
                catch (Exception error) when (
                    error is IOException or JsonException)
                {
                    last = error;
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
        throw new InvalidOperationException(
            $"P5 browser result is missing or unreadable: {browserResultPath}. Last={last?.Message}");
    }

    private static string RequireBrowserResult(
        JsonObject browserResult)
    {
        var verdict =
            FindNamedString(browserResult, "verdict") ??
            FindNamedString(browserResult, "status") ??
            string.Empty;
        Require(
            string.Equals(verdict, "PASS", StringComparison.OrdinalIgnoreCase),
            $"P5 browser runner verdict was '{verdict}', expected PASS.");
        var networkMockCount = FindNamedInt(
            browserResult,
            "networkMockCount");
        Require(
            networkMockCount == 0,
            $"P5 browser runner networkMockCount was {networkMockCount?.ToString() ?? "missing"}, expected 0.");
        return "PASS";
    }

    private static async Task<object> RunBrowserDirectMongoOracleAsync(
        IMongoDatabase database,
        ProbeFixture fixture,
        P5BrowserReadFixture read,
        P5BrowserForwardFixture forward,
        P5BrowserStateFixture partialBefore,
        P5BrowserStateFixture retryingBefore,
        P5BrowserStateFixture reconciledBefore,
        JsonObject browserResult,
        CancellationToken ct)
    {
        var t01 = await ValidateConvergedAsync(
            database,
            fixture,
            fixture.T01WorkId,
            BrowserT01Command,
            fixture.T01VersionId,
            "FLOW-T01",
            fixture.TargetUnitIds.Take(1).ToArray(),
            "2026-07-BROWSER-T01",
            false,
            ct);
        var t02 = await ValidateConvergedAsync(
            database,
            fixture,
            fixture.T02WorkId,
            BrowserT02Command,
            fixture.T02VersionId,
            "FLOW-T02",
            fixture.TargetUnitIds,
            "2026-07-BROWSER-T02",
            false,
            ct);
        var forwardSnapshot = await ValidateConvergedAsync(
            database,
            fixture,
            forward.WorkId,
            BrowserForwardCommand,
            fixture.T01VersionId,
            "FLOW-T01",
            fixture.TargetUnitIds.Take(1).ToArray(),
            "2026-07-BROWSER-FORWARD",
            false,
            ct);
        Require(
            forwardSnapshot.FlowInstanceId == forward.FlowInstanceId,
            "Forward browser fixture instance changed.");
        var forwardHashAfter = await CaptureStateFlowHashAsync(
            database,
            forward.WorkId,
            forward.FlowInstanceId,
            forward.AssignmentId,
            ct);
        Require(
            forwardHashAfter == forward.BeforeHash,
            "Parent-not-approved browser forward changed scoped persistence.");

        var partialAfter = await CaptureBrowserStateFixtureAsync(
            database,
            partialBefore.WorkId,
            partialBefore.CommandId,
            DynamicFlowInstanceStates.Partial,
            ct);
        var retryingAfter = await CaptureBrowserStateFixtureAsync(
            database,
            retryingBefore.WorkId,
            retryingBefore.CommandId,
            DynamicFlowInstanceStates.Retrying,
            ct);
        var reconciledAfter = await CaptureBrowserStateFixtureAsync(
            database,
            reconciledBefore.WorkId,
            reconciledBefore.CommandId,
            DynamicFlowInstanceStates.Reconciled,
            ct);
        Require(
            partialAfter == partialBefore &&
            retryingAfter == retryingBefore &&
            reconciledAfter == reconciledBefore,
            "Browser state-read journeys mutated PARTIAL/RETRYING/RECONCILED fixtures.");

        var reporterReport = await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .Find(item =>
                item.Id == read.ReporterStep.ReportIds.Single() &&
                !item.IsDeleted)
            .SingleAsync(ct);
        var reviewerReport = await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .Find(item =>
                item.Id == read.ReviewerStep.ReportIds.Single() &&
                !item.IsDeleted)
            .SingleAsync(ct);
        Require(
            reporterReport.Status == WorkAssignmentReportStatus.Draft &&
            reviewerReport.Status == WorkAssignmentReportStatus.Submitted,
            "P5 browser report fixtures drifted from Draft/Submitted.");
        var readSteps = await database
            .GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(item =>
                item.FlowInstanceId == read.FlowInstanceId &&
                !item.IsDeleted)
            .ToListAsync(ct);
        Require(
            readSteps.Count == fixture.TargetUnitIds.Count &&
            readSteps.Single(item =>
                    item.Id == read.ReporterStep.StepInstanceId)
                .State == DynamicFlowStepStates.InProgress &&
            readSteps.Single(item =>
                    item.Id == read.ReviewerStep.StepInstanceId)
                .State == DynamicFlowStepStates.Submitted,
            "P5 browser read step/report state identity drifted.");
        var readInstance = await database
            .GetCollection<DynamicFlowInstance>(
                "dynamic_flow_instances")
            .Find(item =>
                item.Id == read.FlowInstanceId &&
                item.WorkId == read.WorkId &&
                !item.IsDeleted)
            .SingleAsync(ct);
        var readReceipt = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                "dynamic_flow_runtime_command_receipts")
            .Find(item =>
                item.CommandId == BrowserReadCommand &&
                item.FlowInstanceId == read.FlowInstanceId)
            .SingleAsync(ct);
        var readOutbox = await database
            .GetCollection<DynamicFlowRuntimeOutboxItem>(
                "dynamic_flow_runtime_outbox")
            .Find(item =>
                item.FlowInstanceId == read.FlowInstanceId)
            .ToListAsync(ct);
        var readAssignments = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .Find(item =>
                item.FlowInstanceId == read.FlowInstanceId &&
                !item.IsDeleted)
            .ToListAsync(ct);
        var readAssignmentIds = readAssignments
            .Select(item => item.Id)
            .ToArray();
        var readBindingCount = await database
            .GetCollection<WorkTemplateAssignee>(
                "work_template_assignees")
            .CountDocumentsAsync(
                item =>
                    readAssignmentIds.Contains(
                        item.WorkAssignmentId) &&
                    !item.IsDeleted,
                cancellationToken: ct);
        var readPeriodCount = await database
            .GetCollection<WorkReportPeriod>("work_report_periods")
            .CountDocumentsAsync(
                item =>
                    readAssignmentIds.Contains(
                        item.WorkAssignmentId) &&
                    !item.IsDeleted,
                cancellationToken: ct);
        var readQueueCount = await database
            .GetCollection<WorkAssignmentQueueItem>(
                "work_assignment_queue")
            .CountDocumentsAsync(
                item =>
                    readAssignmentIds.Contains(
                        item.WorkAssignmentId) &&
                    !item.IsDeleted,
                cancellationToken: ct);
        var readEventCount = await database
            .GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events")
            .CountDocumentsAsync(
                item =>
                    item.FlowInstanceId == read.FlowInstanceId,
                cancellationToken: ct);
        Require(
            readInstance.State == DynamicFlowInstanceStates.Active &&
            readReceipt.Status ==
            DynamicFlowRuntimeCommandStatuses.Succeeded &&
            readOutbox.Count == fixture.TargetUnitIds.Count &&
            readOutbox.All(item =>
                item.Status ==
                DynamicFlowRuntimeOutboxStatuses.Completed) &&
            readAssignments.Count == fixture.TargetUnitIds.Count &&
            readBindingCount == fixture.TargetUnitIds.Count &&
            readPeriodCount == fixture.TargetUnitIds.Count &&
            readQueueCount == fixture.TargetUnitIds.Count &&
            readEventCount >= fixture.TargetUnitIds.Count + 1,
            "P5 browser read materialization ledger drifted.");
        var reviewerRoleCount = await database
            .GetCollection<DocRole>("doc_roles")
            .CountDocumentsAsync(
                item =>
                    item.DocType == DocType.WORK_ASSIGNMENT &&
                    item.DocId == read.ReviewerStep.AssignmentId &&
                    item.UserId == read.Reviewer.UserId &&
                    item.Role == DocRoleType.ASSIGNER &&
                    !item.IsDeleted,
                cancellationToken: ct);
        Require(
            reviewerRoleCount == 1,
            "P5 browser reviewer assignment scope drifted.");
        var reviewerWorkReadRoleCount = await database
            .GetCollection<DocRole>("doc_roles")
            .CountDocumentsAsync(
                item =>
                    item.DocType == DocType.WORK &&
                    item.DocId == read.WorkId &&
                    item.UserId == read.Reviewer.UserId &&
                    item.Role == DocRoleType.WORK_PARTICIPANT &&
                    !item.IsDeleted,
                cancellationToken: ct);
        Require(
            reviewerWorkReadRoleCount == 1,
            "P5 browser reviewer work-read scope drifted.");

        var observedT01 =
            FindNamedString(browserResult, "t01InstanceId");
        var observedT02 =
            FindNamedString(browserResult, "t02InstanceId");
        var observedRead =
            FindNamedString(browserResult, "readInstanceId");
        var observedForward =
            FindNamedString(browserResult, "forwardAssignmentId");
        Require(
            observedT01 == t01.FlowInstanceId &&
            observedT02 == t02.FlowInstanceId &&
            observedRead == read.FlowInstanceId &&
            observedForward == forward.AssignmentId,
            "Browser-observed IDs do not match the direct-Mongo oracle.");

        var allInstances = await database
            .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(item => !item.IsDeleted)
            .ToListAsync(ct);
        var allSteps = await database
            .GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(item => !item.IsDeleted)
            .ToListAsync(ct);
        var allAssignments = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .Find(item =>
                item.FlowInstanceId != null &&
                !item.IsDeleted)
            .ToListAsync(ct);
        var allOutbox = await database
            .GetCollection<DynamicFlowRuntimeOutboxItem>(
                "dynamic_flow_runtime_outbox")
            .Find(FilterDefinition<DynamicFlowRuntimeOutboxItem>.Empty)
            .ToListAsync(ct);
        var instanceIds = allInstances
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var stepIds = allSteps
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var assignmentIds = allAssignments
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        Require(
            allSteps.All(step =>
                instanceIds.Contains(step.FlowInstanceId) &&
                step.AssignmentId is not null &&
                assignmentIds.Contains(step.AssignmentId)) &&
            allAssignments.All(assignment =>
                assignment.FlowInstanceId is not null &&
                instanceIds.Contains(assignment.FlowInstanceId)) &&
            allOutbox.All(item =>
                instanceIds.Contains(item.FlowInstanceId) &&
                stepIds.Contains(item.StepInstanceId)),
            "Direct-Mongo orphan scan found an instance/step/assignment/outbox drift.");

        return new
        {
            verdict = "PASS",
            networkMockCount = 0,
            activation = new
            {
                catalogActivated =
                    DynamicFlowRuntimeCatalogCandidate.ActivationEnabled,
                testingCandidateOverrideEnabled =
                    !DynamicFlowRuntimeCatalogCandidate.ActivationEnabled
            },
            browserObservedIds = new
            {
                t01InstanceId = observedT01,
                t02InstanceId = observedT02,
                readInstanceId = observedRead,
                forwardAssignmentId = observedForward
            },
            launch = new
            {
                t01,
                t02
            },
            read = new
            {
                read.WorkId,
                read.FlowInstanceId,
                reporterStepId =
                    read.ReporterStep.StepInstanceId,
                reporterReportId = reporterReport.Id,
                reporterStatus =
                    reporterReport.Status.ToString(),
                reviewerStepId =
                    read.ReviewerStep.StepInstanceId,
                reviewerReportId = reviewerReport.Id,
                reviewerStatus =
                    reviewerReport.Status.ToString(),
                reviewerRoleCount,
                revisions = new
                {
                    instance = readInstance.Revision,
                    reporterStep = readSteps.Single(item =>
                        item.Id ==
                        read.ReporterStep.StepInstanceId).Revision,
                    reviewerStep = readSteps.Single(item =>
                        item.Id ==
                        read.ReviewerStep.StepInstanceId).Revision
                },
                ledger = new
                {
                    receiptStatus = readReceipt.Status,
                    events = readEventCount,
                    outbox = readOutbox.Count,
                    assignments = readAssignments.Count,
                    bindings = readBindingCount,
                    periods = readPeriodCount,
                    queue = readQueueCount
                }
            },
            states = new
            {
                partial = partialAfter,
                retrying = retryingAfter,
                reconciled = reconciledAfter
            },
            forward = new
            {
                forward.WorkId,
                forward.FlowInstanceId,
                forward.AssignmentId,
                zeroWrite = true,
                reasonCode = forward.ReasonCode,
                hash = forwardHashAfter
            },
            orphanScan = new
            {
                instances = allInstances.Count,
                steps = allSteps.Count,
                assignments = allAssignments.Count,
                outbox = allOutbox.Count,
                orphanCount = 0
            },
            completedAtUtc = DateTime.UtcNow
        };
    }

    private static string? FindNamedString(
        JsonNode? node,
        string propertyName)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (string.Equals(
                        property.Key,
                        propertyName,
                        StringComparison.OrdinalIgnoreCase) &&
                    property.Value is JsonValue value &&
                    value.TryGetValue<string>(out var text))
                {
                    return text;
                }
                var nested = FindNamedString(
                    property.Value,
                    propertyName);
                if (nested is not null)
                    return nested;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var nested = FindNamedString(item, propertyName);
                if (nested is not null)
                    return nested;
            }
        }
        return null;
    }

    private static int? FindNamedInt(
        JsonNode? node,
        string propertyName)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (string.Equals(
                        property.Key,
                        propertyName,
                        StringComparison.OrdinalIgnoreCase) &&
                    property.Value is JsonValue value)
                {
                    if (value.TryGetValue<int>(out var integer))
                        return integer;
                    if (value.TryGetValue<long>(out var longValue) &&
                        longValue is >= int.MinValue and <= int.MaxValue)
                    {
                        return (int)longValue;
                    }
                }
                var nested = FindNamedInt(
                    property.Value,
                    propertyName);
                if (nested.HasValue)
                    return nested;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var nested = FindNamedInt(item, propertyName);
                if (nested.HasValue)
                    return nested;
            }
        }
        return null;
    }

    private static void TryDeleteBrowserFixtureFile(
        string path,
        ICollection<string> cleanupErrors)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception error)
        {
            cleanupErrors.Add(
                $"{Path.GetFileName(path)}: {error.Message}");
        }
    }
}

internal sealed record P5BrowserActorFixture(
    string Username,
    string UserId,
    string Password,
    string Role);

internal sealed record P5BrowserStepFixture(
    string StepInstanceId,
    string AssignmentId,
    string BranchId,
    string TargetUnitId,
    int AttemptNo,
    string State,
    IReadOnlyList<string> ReportIds,
    string? SubmitReportId,
    string? ReviewReportId,
    bool CanSubmit,
    bool CanReview);

internal sealed record P5BrowserReadFixture(
    string WorkId,
    string FlowInstanceId,
    P5BrowserActorFixture Issuer,
    P5BrowserActorFixture Reporter,
    P5BrowserActorFixture Reviewer,
    P5BrowserActorFixture Outsider,
    P5BrowserStepFixture ReporterStep,
    P5BrowserStepFixture ReviewerStep);

internal sealed record P5BrowserForwardFixture(
    string WorkId,
    string FlowInstanceId,
    string StepInstanceId,
    string BranchId,
    int AttemptNo,
    string AssignmentId,
    string Path,
    string BeforeHash,
    int ExpectedStatus,
    string ReasonCode);

internal sealed record P5BrowserStateFixture(
    string WorkId,
    string FlowInstanceId,
    string CommandId,
    string State,
    long Revision,
    long RuntimeRecoveryEpoch,
    string ReceiptStatus,
    int StepCount,
    int AssignmentCount,
    long BindingCount,
    long PeriodCount,
    long QueueCount,
    long AssignmentDocRoleCount,
    long EventCount,
    int CompletedOutboxCount,
    int FailedOutboxCount,
    int PendingOutboxCount,
    bool RecoveryRequired,
    string ReasonCode,
    string NextAction,
    string ReconcileStatus,
    string PersistenceHash);

internal sealed record P5BrowserFixtureOptions(
    Uri FrontendOrigin,
    TimeSpan Timeout,
    string? RunKey)
{
    private const int DefaultTimeoutSeconds = 1_800;

    public static P5BrowserFixtureOptions Parse(string[] args)
    {
        string? frontendOrigin = null;
        string? runKey = null;
        var timeoutSeconds = DefaultTimeoutSeconds;
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(
                    args[index],
                    "--frontend-origin",
                    StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length)
            {
                frontendOrigin = args[++index];
            }
            else if (string.Equals(
                         args[index],
                         "--p5-browser-fixture-timeout-seconds",
                         StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Length &&
                     int.TryParse(args[++index], out var parsedTimeout))
            {
                timeoutSeconds = parsedTimeout;
            }
            else if (string.Equals(
                         args[index],
                         "--p5-browser-fixture-run-key",
                         StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Length)
            {
                runKey = args[++index];
            }
        }

        if (!Uri.TryCreate(frontendOrigin, UriKind.Absolute, out var parsedOrigin) ||
            parsedOrigin.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(parsedOrigin.Host))
        {
            throw new ArgumentException(
                "--frontend-origin must be an absolute HTTP(S) origin.");
        }
        if (timeoutSeconds is < 1 or > 3_600)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "P5 browser fixture timeout must be between 1 and 3600 seconds.");
        }
        if (!string.IsNullOrWhiteSpace(runKey) &&
            runKey.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-')))
        {
            throw new ArgumentException(
                "P5 browser fixture run key may contain only letters, digits, '_' and '-'.");
        }

        var origin = new Uri(
            parsedOrigin.GetLeftPart(UriPartial.Authority),
            UriKind.Absolute);
        return new P5BrowserFixtureOptions(
            origin,
            TimeSpan.FromSeconds(timeoutSeconds),
            string.IsNullOrWhiteSpace(runKey) ? null : runKey);
    }
}

internal static partial class P5MaterializationProbe
{
    private static Task<BackendServerLease> StartBrowserBackendAsync(
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        MongoReplicaSetLease mongo,
        string phase,
        Uri frontendOrigin,
        CancellationToken ct,
        string? faultCommandId = null,
        int? faultBranchOrdinal = null,
        IReadOnlyList<string>? faultPoints = null)
    {
        var serverRoot = Path.Combine(iterationRoot, $"backend-browser-{phase}");
        Directory.CreateDirectory(serverRoot);
        return BackendServerLease.StartAsync(
            paths,
            serverRoot,
            runKey,
            mongo,
            ct,
            new BackendServerOptions
            {
                EnableDynamicFlowRuntimeCandidate =
                    !DynamicFlowRuntimeCatalogCandidate.ActivationEnabled,
                FrontendOrigin =
                    frontendOrigin.GetLeftPart(UriPartial.Authority),
                DynamicFlowRuntimeFaultCommandId = faultCommandId,
                DynamicFlowRuntimeFaultBranchOrdinal = faultBranchOrdinal,
                DynamicFlowRuntimeFaultPoints =
                    faultPoints ?? Array.Empty<string>()
            });
    }

    private static async Task StopBrowserBackendAsync(
        BackendServerLease backend)
    {
        await backend.StopAsync();
        await backend.DisposeAsync();
    }

    private static async Task RestoreBrowserCandidateDefinitionsAsync(
        IMongoDatabase database,
        ProbeFixture fixture,
        CancellationToken ct)
    {
        await RestoreCandidateDefinitionAsync(
            database,
            fixture.T01VersionId,
            ct);
        await RestoreCandidateDefinitionAsync(
            database,
            fixture.T02VersionId,
            ct);
    }

    private static async Task ClearBrowserBackfillManifestsAsync(
        IMongoDatabase database,
        ProbeFixture fixture,
        CancellationToken ct)
    {
        await ClearFixtureBackfillManifestsAsync(
            database,
            fixture.T01VersionId,
            ct);
        await ClearFixtureBackfillManifestsAsync(
            database,
            fixture.T02VersionId,
            ct);
    }

    private static async Task SeedBrowserIssuerWorkAclAsync(
        IMongoDatabase database,
        ProbeFixture fixture,
        CancellationToken ct)
    {
        var workIds = new[]
        {
            fixture.T01WorkId,
            fixture.T02WorkId
        };
        var works = await database
            .GetCollection<Work>("works")
            .Find(item =>
                workIds.Contains(item.Id) &&
                !item.IsDeleted)
            .ToListAsync(ct);
        Require(
            works.Count == workIds.Length,
            "P5 browser issuer ACL fixture works are missing.");

        var now = DateTime.UtcNow;
        var roles = works
            .SelectMany(work =>
            {
                Require(
                    !string.IsNullOrWhiteSpace(work.CreatedByUserId) &&
                    !string.IsNullOrWhiteSpace(work.LeaderDirectiveUserId),
                    $"P5 browser issuer ACL metadata is incomplete for work {work.Id}.");
                return new[]
                {
                    new DocRole
                    {
                        Id = ObjectId.GenerateNewId().ToString(),
                        DocType = DocType.WORK,
                        DocId = work.Id,
                        UserId = work.CreatedByUserId!,
                        Role = DocRoleType.OWNER,
                        User = work.Owner,
                        IsDeleted = false,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        CreatedByUserId = work.CreatedByUserId,
                        UpdatedByUserId = work.CreatedByUserId
                    },
                    new DocRole
                    {
                        Id = ObjectId.GenerateNewId().ToString(),
                        DocType = DocType.WORK,
                        DocId = work.Id,
                        UserId = work.LeaderDirectiveUserId!,
                        Role = DocRoleType.LEADER_DIRECTIVE,
                        User = work.LeaderDirective,
                        IsDeleted = false,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        CreatedByUserId = work.CreatedByUserId,
                        UpdatedByUserId = work.CreatedByUserId
                    }
                };
            })
            .ToArray();
        await database
            .GetCollection<DocRole>("doc_roles")
            .InsertManyAsync(roles, cancellationToken: ct);

        foreach (var work in works)
        {
            var issuerRoleCount = await database
                .GetCollection<DocRole>("doc_roles")
                .CountDocumentsAsync(
                    item =>
                        item.DocType == DocType.WORK &&
                        item.DocId == work.Id &&
                        item.UserId == work.CreatedByUserId &&
                        (item.Role == DocRoleType.OWNER ||
                         item.Role == DocRoleType.LEADER_DIRECTIVE) &&
                        !item.IsDeleted,
                    cancellationToken: ct);
            Require(
                issuerRoleCount == 2,
                $"P5 browser issuer ACL seed drifted for work {work.Id}.");
        }
    }

    private static async Task RequireBrowserIssuerWorkReadAsync(
        ApiHarnessClient api,
        string issuerToken,
        ProbeFixture fixture,
        CancellationToken ct)
    {
        foreach (var workId in new[]
                 {
                     fixture.T01WorkId,
                     fixture.T02WorkId
                 })
        {
            var response = await api.GetAsync(
                $"api/works/{workId}",
                issuerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                response,
                HttpStatusCode.OK,
                $"P5 browser issuer normal-ACL work read {workId}");
        }
    }

    private static async Task<P5BrowserStateFixture>
        CaptureBrowserStateFixtureAsync(
            IMongoDatabase database,
            string workId,
            string commandId,
            string expectedState,
            CancellationToken ct)
    {
        var receipt = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                "dynamic_flow_runtime_command_receipts")
            .Find(item => item.CommandId == commandId)
            .SingleAsync(ct);
        var instanceId = receipt.FlowInstanceId
                         ?? throw new InvalidOperationException(
                             $"{commandId} receipt lacks flowInstanceId.");
        var instance = await database
            .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(item =>
                item.Id == instanceId &&
                item.WorkId == workId &&
                !item.IsDeleted)
            .SingleAsync(ct);
        Require(
            instance.State == expectedState,
            $"{commandId} expected {expectedState}, got {instance.State}.");
        var steps = await database
            .GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(item =>
                item.FlowInstanceId == instanceId &&
                !item.IsDeleted)
            .ToListAsync(ct);
        var outbox = await database
            .GetCollection<DynamicFlowRuntimeOutboxItem>(
                "dynamic_flow_runtime_outbox")
            .Find(item => item.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        var eventCount = await database
            .GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events")
            .CountDocumentsAsync(
                item => item.FlowInstanceId == instanceId,
                cancellationToken: ct);
        var assignments = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .Find(
                item =>
                    item.FlowInstanceId == instanceId &&
                    !item.IsDeleted)
            .SortBy(item => item.Id)
            .ToListAsync(ct);
        var assignmentIds = assignments
            .Select(item => item.Id)
            .ToArray();
        var bindingCount = await database
            .GetCollection<WorkTemplateAssignee>(
                "work_template_assignees")
            .CountDocumentsAsync(
                item =>
                    assignmentIds.Contains(item.WorkAssignmentId) &&
                    !item.IsDeleted,
                cancellationToken: ct);
        var periodCount = await database
            .GetCollection<WorkReportPeriod>("work_report_periods")
            .CountDocumentsAsync(
                item =>
                    assignmentIds.Contains(item.WorkAssignmentId) &&
                    !item.IsDeleted,
                cancellationToken: ct);
        var queueCount = await database
            .GetCollection<WorkAssignmentQueueItem>(
                "work_assignment_queue")
            .CountDocumentsAsync(
                item =>
                    assignmentIds.Contains(item.WorkAssignmentId) &&
                    !item.IsDeleted,
                cancellationToken: ct);
        var assignmentDocRoleCount = await database
            .GetCollection<DocRole>("doc_roles")
            .CountDocumentsAsync(
                item =>
                    item.DocType == DocType.WORK_ASSIGNMENT &&
                    assignmentIds.Contains(item.DocId) &&
                    !item.IsDeleted,
                cancellationToken: ct);
        var (recoveryRequired, reasonCode, nextAction, reconcileStatus) =
            expectedState switch
            {
                DynamicFlowInstanceStates.Partial =>
                    (true,
                        "DYNAMIC_FLOW_RUNTIME_PARTIAL",
                        "WAIT_FOR_RECONCILE",
                        "IDLE"),
                DynamicFlowInstanceStates.Retrying =>
                    (true,
                        "DYNAMIC_FLOW_RUNTIME_RETRYING",
                        "WAIT_FOR_RETRY",
                        "IDLE"),
                DynamicFlowInstanceStates.Reconciled =>
                    (false,
                        "DYNAMIC_FLOW_RUNTIME_RECONCILED",
                        "REFRESH",
                        "RECONCILED"),
                _ => throw new InvalidOperationException(
                    $"Unsupported P5 browser state fixture {expectedState}.")
            };
        return new P5BrowserStateFixture(
            workId,
            instanceId,
            commandId,
            instance.State,
            instance.Revision,
            instance.RuntimeRecoveryEpoch,
            receipt.Status,
            steps.Count,
            assignments.Count,
            bindingCount,
            periodCount,
            queueCount,
            assignmentDocRoleCount,
            eventCount,
            outbox.Count(item =>
                item.Status ==
                DynamicFlowRuntimeOutboxStatuses.Completed),
            outbox.Count(item =>
                item.Status ==
                DynamicFlowRuntimeOutboxStatuses.Failed),
            outbox.Count(item =>
                item.Status ==
                DynamicFlowRuntimeOutboxStatuses.Pending),
            recoveryRequired,
            reasonCode,
            nextAction,
            reconcileStatus,
            await CaptureStateFlowHashAsync(
                database,
                workId,
                instanceId,
                assignments.FirstOrDefault()?.Id
                ?? throw new InvalidOperationException(
                    $"{commandId} state fixture lacks an assignment."),
                ct));
    }

    private static async Task InjectBrowserReconciledStateAsync(
        IMongoDatabase database,
        string flowInstanceId,
        CancellationToken ct)
    {
        var instances = database.GetCollection<DynamicFlowInstance>(
            "dynamic_flow_instances");
        var initial = await instances
            .Find(item => item.Id == flowInstanceId && !item.IsDeleted)
            .SingleAsync(ct);
        Require(
            initial.State == DynamicFlowInstanceStates.Active,
            "RECONCILED browser injection must begin from ACTIVE.");
        var now = DateTime.UtcNow;
        DynamicFlowRuntimeStateContract.RequireInstanceTransition(
            DynamicFlowInstanceStates.Active,
            DynamicFlowInstanceStates.Partial);
        var partial = await instances.UpdateOneAsync(
            item =>
                item.Id == flowInstanceId &&
                item.State == DynamicFlowInstanceStates.Active &&
                item.Revision == initial.Revision &&
                !item.IsDeleted,
            Builders<DynamicFlowInstance>.Update
                .Set(item => item.State, DynamicFlowInstanceStates.Partial)
                .Set(item => item.ResumeState, DynamicFlowInstanceStates.Active)
                .Set(item => item.UpdatedAtUtc, now)
                .Set(item => item.UpdatedByUserId, initial.IssuerUserId)
                .Inc(item => item.Revision, 1)
                .Inc(item => item.RuntimeRecoveryEpoch, 1),
            cancellationToken: ct);
        Require(
            partial.ModifiedCount == 1,
            "RECONCILED browser injection ACTIVE->PARTIAL CAS failed.");

        DynamicFlowRuntimeStateContract.RequireInstanceTransition(
            DynamicFlowInstanceStates.Partial,
            DynamicFlowInstanceStates.Retrying);
        var retrying = await instances.UpdateOneAsync(
            item =>
                item.Id == flowInstanceId &&
                item.State == DynamicFlowInstanceStates.Partial &&
                item.Revision == initial.Revision + 1 &&
                !item.IsDeleted,
            Builders<DynamicFlowInstance>.Update
                .Set(item => item.State, DynamicFlowInstanceStates.Retrying)
                .Set(item => item.UpdatedAtUtc, now)
                .Set(item => item.UpdatedByUserId, initial.IssuerUserId)
                .Inc(item => item.Revision, 1),
            cancellationToken: ct);
        Require(
            retrying.ModifiedCount == 1,
            "RECONCILED browser injection PARTIAL->RETRYING CAS failed.");

        DynamicFlowRuntimeStateContract.RequireInstanceTransition(
            DynamicFlowInstanceStates.Retrying,
            DynamicFlowInstanceStates.Reconciled);
        var reconciled = await instances.UpdateOneAsync(
            item =>
                item.Id == flowInstanceId &&
                item.State == DynamicFlowInstanceStates.Retrying &&
                item.Revision == initial.Revision + 2 &&
                !item.IsDeleted,
            Builders<DynamicFlowInstance>.Update
                .Set(item => item.State, DynamicFlowInstanceStates.Reconciled)
                .Set(item => item.LastErrorCode, null)
                .Set(item => item.UpdatedAtUtc, now)
                .Set(item => item.UpdatedByUserId, initial.IssuerUserId)
                .Inc(item => item.Revision, 1)
                .Inc(item => item.NextEventSequence, 1),
            cancellationToken: ct);
        Require(
            reconciled.ModifiedCount == 1,
            "RECONCILED browser injection RETRYING->RECONCILED CAS failed.");

        var payload = new BsonDocument
        {
            { "flowInstanceId", initial.Id },
            { "workId", initial.WorkId },
            { "recoveryEpoch", initial.RuntimeRecoveryEpoch + 1 },
            { "fixture", "P5_BROWSER_INJECTED_RECOVERY_READBACK" }
        };
        await database
            .GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events")
            .InsertOneAsync(
                new DynamicFlowRuntimeEvent
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    FlowInstanceId = initial.Id,
                    Sequence = initial.NextEventSequence,
                    EventType =
                        DynamicFlowRuntimeEventTypes.RecoveryReconciled,
                    CommandId = initial.LaunchCommandId,
                    CorrelationId = initial.LaunchCommandId,
                    SourceEventKey =
                        $"p5-browser-reconciled:{initial.RuntimeRecoveryEpoch + 1}",
                    FromState = DynamicFlowInstanceStates.Retrying,
                    ToState = DynamicFlowInstanceStates.Reconciled,
                    FromRevision = initial.Revision + 2,
                    ToRevision = initial.Revision + 3,
                    ReasonCode = "P5_BROWSER_INJECTED_RECOVERY_READBACK",
                    AffectedRefs = [$"instance:{initial.Id}"],
                    ActorUserId = initial.IssuerUserId,
                    VisibleUnitIds = [initial.IssuerUnitId],
                    Payload = payload,
                    PayloadHash = Hash(payload.ToJson()),
                    OccurredAtUtc = now
                },
                cancellationToken: ct);
    }

    private static async Task<P5BrowserReadFixture>
        SeedBrowserReadFixtureAsync(
            ApiHarnessClient api,
            string issuerToken,
            string issuerPassword,
            BackendServerLease backend,
            IMongoDatabase database,
            ProbeFixture fixture,
            string flowInstanceId,
            CancellationToken ct)
    {
        var instance = await database
            .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(item =>
                item.Id == flowInstanceId &&
                item.WorkId == fixture.MultiUnitWorkId &&
                !item.IsDeleted)
            .SingleAsync(ct);
        var steps = await database
            .GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(item =>
                item.FlowInstanceId == flowInstanceId &&
                !item.IsDeleted)
            .Sort(
                Builders<DynamicFlowStepInstance>.Sort
                    .Ascending(item => item.TargetUnitId)
                    .Ascending(item => item.Id))
            .ToListAsync(ct);
        Require(
            steps.Count == fixture.TargetUnitIds.Count,
            "P5 browser read fixture must contain three materialized steps.");
        var reporterStep = steps[0];
        var reviewerStep = steps[1];
        var reporterId = reporterStep.ParticipantUserIds.Single();
        var reviewerAssigneeId = reviewerStep.ParticipantUserIds.Single();
        var users = database.GetCollection<AppUser>("users");
        var reporter = await users
            .Find(item => item.Id == reporterId && !item.IsDeleted)
            .SingleAsync(ct);
        var reviewerAssignee = await users
            .Find(item => item.Id == reviewerAssigneeId && !item.IsDeleted)
            .SingleAsync(ct);
        var admin = await users
            .Find(item => item.Username == "admin" && !item.IsDeleted)
            .SingleAsync(ct);
        var now = DateTime.UtcNow;
        var hasher = new PasswordHasher<AppUser>();
        reporter.PasswordHash =
            hasher.HashPassword(reporter, backend.ActorPassword);
        reporter.UpdatedAtUtc = now;
        reviewerAssignee.PasswordHash =
            hasher.HashPassword(reviewerAssignee, backend.ActorPassword);
        reviewerAssignee.UpdatedAtUtc = now;
        await users.ReplaceOneAsync(
            item => item.Id == reporter.Id && !item.IsDeleted,
            reporter,
            cancellationToken: ct);
        await users.ReplaceOneAsync(
            item => item.Id == reviewerAssignee.Id && !item.IsDeleted,
            reviewerAssignee,
            cancellationToken: ct);

        var reviewer = NewRuntimeReadActor(
            "p5_browser_reviewer",
            "P5 Browser Reviewer",
            instance.IssuerUnitId,
            instance.IssuerUserId,
            now);
        reviewer.PasswordHash =
            hasher.HashPassword(reviewer, backend.ActorPassword);
        var outsider = NewRuntimeReadActor(
            "p5_browser_outsider",
            "P5 Browser Outsider",
            instance.IssuerUnitId,
            instance.IssuerUserId,
            now);
        outsider.PasswordHash =
            hasher.HashPassword(outsider, backend.ActorPassword);
        await users.InsertManyAsync(
            [reviewer, outsider],
            cancellationToken: ct);

        var reporterToken = await api.LoginAsync(
            reporter.Username,
            backend.ActorPassword,
            ct);
        var reviewerAssigneeToken = await api.LoginAsync(
            reviewerAssignee.Username,
            backend.ActorPassword,
            ct);
        var reviewerToken = await api.LoginAsync(
            reviewer.Username,
            backend.ActorPassword,
            ct);
        var outsiderToken = await api.LoginAsync(
            outsider.Username,
            backend.ActorPassword,
            ct);

        var periods = database.GetCollection<WorkReportPeriod>(
            "work_report_periods");
        var reporterPeriod = await periods
            .Find(item =>
                item.WorkAssignmentId == reporterStep.AssignmentId &&
                item.PeriodKey == "2026-07-BROWSER-READ" &&
                !item.IsDeleted)
            .SingleAsync(ct);
        var reviewerPeriod = await periods
            .Find(item =>
                item.WorkAssignmentId == reviewerStep.AssignmentId &&
                item.PeriodKey == "2026-07-BROWSER-READ" &&
                !item.IsDeleted)
            .SingleAsync(ct);

        var reporterOpen = await api.PostAsync(
            $"api/work-report-periods/{reporterPeriod.Id}/open",
            body: null,
            reporterToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            reporterOpen,
            HttpStatusCode.OK,
            "P5 browser reporter report open");
        var reporterReportId =
            ApiHarnessClient.RequiredString(reporterOpen.Json, "id");
        await EnsureBrowserOpenReportReconciledAsync(
            api,
            issuerToken,
            database,
            flowInstanceId,
            reporterStep.Id,
            reporterReportId,
            ct);

        var reviewerOpen = await api.PostAsync(
            $"api/work-report-periods/{reviewerPeriod.Id}/open",
            body: null,
            reviewerAssigneeToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            reviewerOpen,
            HttpStatusCode.OK,
            "P5 browser reviewer-branch report open");
        var reviewerReportId =
            ApiHarnessClient.RequiredString(reviewerOpen.Json, "id");
        await EnsureBrowserOpenReportReconciledAsync(
            api,
            issuerToken,
            database,
            flowInstanceId,
            reviewerStep.Id,
            reviewerReportId,
            ct);

        var reviewerSubmit = await api.PostAsync(
            $"api/work-assignment-reports/{reviewerReportId}/submit",
            new JsonObject
            {
                ["expectedPayloadRevision"] =
                    ApiHarnessClient.RequiredInt(
                        reviewerOpen.Json,
                        "payloadRevision"),
                ["expectedLifecycleRevision"] =
                    ApiHarnessClient.RequiredInt(
                        reviewerOpen.Json,
                        "lifecycleRevision"),
                ["commandId"] = "p5-browser-reviewer-report-submit",
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
            },
            reviewerAssigneeToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            reviewerSubmit,
            HttpStatusCode.OK,
            "P5 browser reviewer-branch report submit");
        var reviewerAssignmentId = reviewerStep.AssignmentId
                                   ?? throw new InvalidOperationException(
                                       "Reviewer step lacks assignment.");
        var reviewerAssignmentUpdate = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .UpdateOneAsync(
                item =>
                    item.Id == reviewerAssignmentId &&
                    item.FlowInstanceId == flowInstanceId &&
                    !item.IsDeleted,
                Builders<WorkAssignment>.Update
                    .Set(item => item.CreatedByUserId, reviewer.Id)
                    .Set(item => item.UpdatedByUserId, reviewer.Id)
                    .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken: ct);
        Require(
            reviewerAssignmentUpdate.ModifiedCount == 1,
            "P5 browser reviewer ownership injection failed.");
        await SeedRuntimeReviewerScopeAsync(
            database,
            instance,
            reviewerStep,
            reviewer,
            now,
            ct);
        await SeedBrowserReviewerWorkReadRoleAsync(
            database,
            instance,
            reviewer,
            now,
            ct);
        await SeedBrowserReviewerAssignmentReadModelAsync(
            database,
            reviewerAssignmentId,
            instance.IssuerUserId,
            reviewer.Id,
            now,
            ct);

        reporterStep = await database
            .GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(item => item.Id == reporterStep.Id && !item.IsDeleted)
            .SingleAsync(ct);
        reviewerStep = await database
            .GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(item => item.Id == reviewerStep.Id && !item.IsDeleted)
            .SingleAsync(ct);
        Require(
            reporterStep.State == DynamicFlowStepStates.InProgress &&
            reporterStep.ReportId == reporterReportId,
            "Reporter browser step did not reconcile to IN_PROGRESS with its report.");
        Require(
            reviewerStep.State == DynamicFlowStepStates.Submitted &&
            reviewerStep.ReportId == reviewerReportId,
            "Reviewer browser step did not project to SUBMITTED with its report.");

        var instancePath =
            $"api/works/{fixture.MultiUnitWorkId}/dynamic-flows/instances/{flowInstanceId}";
        var issuerOverview = await api.GetAsync(
            instancePath,
            issuerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            issuerOverview,
            HttpStatusCode.OK,
            "P5 browser issuer overview precheck");
        var reviewerWorkRead = await api.GetAsync(
            $"api/works/{fixture.MultiUnitWorkId}",
            reviewerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            reviewerWorkRead,
            HttpStatusCode.OK,
            "P5 browser reviewer normal-ACL work read precheck");
        var reporterSteps = await api.GetAsync(
            $"{instancePath}/steps?limit=200",
            reporterToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            reporterSteps,
            HttpStatusCode.OK,
            "P5 browser reporter steps precheck");
        Require(
            reporterSteps.Body.Contains(
                reporterReportId,
                StringComparison.Ordinal),
            "Reporter runtime response did not expose its report ID.");
        var reviewerSteps = await api.GetAsync(
            $"{instancePath}/steps?limit=200",
            reviewerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            reviewerSteps,
            HttpStatusCode.OK,
            "P5 browser reviewer steps precheck");
        var reviewerStepRows = AssertPage(
            reviewerSteps,
            HttpStatusCode.OK,
            1,
            1,
            "P5 browser reviewer exact step precheck");
        var reviewerStepRow = RequiredItem(
            reviewerStepRows,
            0,
            "P5 browser reviewer exact step precheck");
        AssertStepIdentity(
            reviewerStepRow,
            instance,
            reviewerStep,
            "REVIEWER",
            "P5 browser reviewer exact step precheck");
        var reviewerReportIds = ApiHarnessClient.RequiredArray(
            reviewerStepRow["reportIds"],
            "P5 browser reviewer report IDs precheck");
        var reviewerCapabilities = ApiHarnessClient.RequiredObject(
            reviewerStepRow["capabilities"],
            "P5 browser reviewer capabilities precheck");
        Require(
            reviewerStep.State == DynamicFlowStepStates.Submitted &&
            reviewerReportIds.Count == 1 &&
            reviewerReportIds[0]?.GetValue<string>() == reviewerReportId &&
            ReadNullableString(reviewerStepRow, "reviewReportId") ==
            reviewerReportId &&
            ApiHarnessClient.RequiredBool(
                reviewerCapabilities,
                "canReviewReport"),
            "Reviewer runtime response did not expose the exact SUBMITTED review target/capability.");
        var outsiderOverview = await api.GetAsync(
            instancePath,
            outsiderToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            outsiderOverview,
            HttpStatusCode.NotFound,
            "P5 browser outsider hidden overview precheck");

        return new P5BrowserReadFixture(
            fixture.MultiUnitWorkId,
            flowInstanceId,
            new P5BrowserActorFixture(
                "admin",
                admin.Id,
                issuerPassword,
                "ISSUER"),
            new P5BrowserActorFixture(
                reporter.Username,
                reporter.Id,
                backend.ActorPassword,
                "REPORTER"),
            new P5BrowserActorFixture(
                reviewer.Username,
                reviewer.Id,
                backend.ActorPassword,
                "REVIEWER"),
            new P5BrowserActorFixture(
                outsider.Username,
                outsider.Id,
                backend.ActorPassword,
                "OUTSIDER"),
            BrowserStepFixture(
                reporterStep,
                reporterReportId,
                canSubmit: true,
                canReview: false),
            BrowserStepFixture(
                reviewerStep,
                reviewerReportId,
                canSubmit: false,
                canReview: true));
    }

    private static async Task EnsureBrowserOpenReportReconciledAsync(
        ApiHarnessClient api,
        string issuerToken,
        IMongoDatabase database,
        string flowInstanceId,
        string stepInstanceId,
        string reportId,
        CancellationToken ct)
    {
        var steps = database.GetCollection<DynamicFlowStepInstance>(
            "dynamic_flow_step_instances");
        var step = await steps
            .Find(item => item.Id == stepInstanceId && !item.IsDeleted)
            .SingleAsync(ct);
        if (step.State == DynamicFlowStepStates.InProgress &&
            step.ReportId == reportId)
        {
            return;
        }

        Require(
            step.State == DynamicFlowStepStates.Assigned,
            $"Open report reconcile expected ASSIGNED/IN_PROGRESS, got {step.State}.");
        var operationPath =
            $"api/admin/operations/dynamic-flow-runtime/instances/{flowInstanceId}/reconcile";
        var preview = await api.PostAsync(
            $"{operationPath}?apply=false",
            new { },
            issuerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            preview,
            HttpStatusCode.OK,
            "P5 browser open-report reconcile preview");
        var apply = await api.PostAsync(
            $"{operationPath}?apply=true",
            new { },
            issuerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            apply,
            HttpStatusCode.OK,
            "P5 browser open-report reconcile apply");
        Require(
            ApiHarnessClient.RequiredBool(apply.Json, "converged") &&
            ApiHarnessClient.RequiredBool(
                apply.Json,
                "exactMaterializationLedgerConverged") &&
            ApiHarnessClient.RequiredBool(
                apply.Json,
                "exactStateProjectionLedgerConverged"),
            "P5 browser open-report reconcile did not converge.");
        step = await steps
            .Find(item => item.Id == stepInstanceId && !item.IsDeleted)
            .SingleAsync(ct);
        Require(
            step.State == DynamicFlowStepStates.InProgress &&
            step.ReportId == reportId,
            "P5 browser open-report reconcile did not bind IN_PROGRESS/report ID.");
    }

    private static async Task SeedBrowserReviewerAssignmentReadModelAsync(
        IMongoDatabase database,
        string assignmentId,
        string issuerUserId,
        string reviewerUserId,
        DateTime now,
        CancellationToken ct)
    {
        var rows = database.GetCollection<BsonDocument>(
            "assignment_list_doc_roles");
        var source = await rows.Find(
                new BsonDocument
                {
                    { "assignmentId", ObjectId.Parse(assignmentId) },
                    { "userId", ObjectId.Parse(issuerUserId) },
                    { "isDeleted", false }
                })
            .SingleAsync(ct);
        var reviewerRow = source.DeepClone().AsBsonDocument;
        reviewerRow["_id"] = ObjectId.GenerateNewId();
        reviewerRow["userId"] = ObjectId.Parse(reviewerUserId);
        reviewerRow["assignmentCreatedByUserId"] =
            ObjectId.Parse(reviewerUserId);
        reviewerRow["createdByUserId"] =
            ObjectId.Parse(reviewerUserId);
        reviewerRow["updatedByUserId"] =
            ObjectId.Parse(reviewerUserId);
        reviewerRow["createdAtUtc"] = now;
        reviewerRow["updatedAtUtc"] = now;
        reviewerRow.Remove("user");
        await rows.InsertOneAsync(
            reviewerRow,
            cancellationToken: ct);
    }

    private static async Task SeedBrowserReviewerWorkReadRoleAsync(
        IMongoDatabase database,
        DynamicFlowInstance instance,
        AppUser reviewer,
        DateTime now,
        CancellationToken ct)
    {
        var roles = database.GetCollection<DocRole>("doc_roles");
        var existing = await roles.CountDocumentsAsync(
            item =>
                item.DocType == DocType.WORK &&
                item.DocId == instance.WorkId &&
                item.UserId == reviewer.Id &&
                !item.IsDeleted,
            cancellationToken: ct);
        Require(
            existing == 0,
            "P5 browser reviewer unexpectedly had a work role before the exact read grant.");
        await roles.InsertOneAsync(
            new DocRole
            {
                Id = ObjectId.GenerateNewId().ToString(),
                DocType = DocType.WORK,
                DocId = instance.WorkId,
                UserId = reviewer.Id,
                Role = DocRoleType.WORK_PARTICIPANT,
                User = new UserRef
                {
                    UserId = reviewer.Id,
                    Username = reviewer.Username,
                    FullName = reviewer.FullName,
                    UnitId = reviewer.UnitId,
                    PositionCode = reviewer.PositionCode
                },
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = instance.IssuerUserId,
                UpdatedByUserId = instance.IssuerUserId,
                IsDeleted = false
            },
            cancellationToken: ct);
    }

    private static P5BrowserStepFixture BrowserStepFixture(
        DynamicFlowStepInstance step,
        string reportId,
        bool canSubmit,
        bool canReview)
        => new(
            step.Id,
            step.AssignmentId
            ?? throw new InvalidOperationException(
                "Browser step lacks assignment."),
            step.BranchId,
            step.TargetUnitId,
            step.AttemptNo,
            step.State,
            [reportId],
            canSubmit ? reportId : null,
            canReview ? reportId : null,
            canSubmit,
            canReview);

    private static async Task<P5BrowserForwardFixture>
        BuildBrowserForwardFixtureAsync(
            IMongoDatabase database,
            string workId,
            string flowInstanceId,
            CancellationToken ct)
    {
        var step = await database
            .GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(item =>
                item.FlowInstanceId == flowInstanceId &&
                !item.IsDeleted)
            .SingleAsync(ct);
        var assignmentId = step.AssignmentId
                           ?? throw new InvalidOperationException(
                               "Forward browser step lacks assignment.");
        return new P5BrowserForwardFixture(
            workId,
            flowInstanceId,
            step.Id,
            step.BranchId,
            step.AttemptNo,
            assignmentId,
            $"/api/works/{workId}/dynamic-flows/assignments/{assignmentId}/forward",
            await CaptureStateFlowHashAsync(
                database,
                workId,
                flowInstanceId,
                assignmentId,
                ct),
            409,
            "DYNAMIC_FLOW_FORWARD_PARENT_NOT_APPROVED");
    }
}
