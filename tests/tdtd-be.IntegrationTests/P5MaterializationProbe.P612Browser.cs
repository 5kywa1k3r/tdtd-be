using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Long-lived P6-12 browser fixture. This is intentionally separate from the
/// frozen P5 Playwright fixture: it exposes a real Kestrel/Mongo session for an
/// externally controlled production frontend and seeds every FLOW-T01..T12
/// journey in one isolated database.
/// </summary>
internal static partial class P5MaterializationProbe
{
    private const int P612CandidateActivationThrough = 12;
    internal static async Task<int> RunP612BrowserFixtureAsync(string[] args)
    {
        var options = P612BrowserFixtureOptions.Parse(args);
        RequireP612ActivationMode(options);
        var runNonce = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(16))
            .ToLowerInvariant();
        var runKey = options.RunKey ??
                     $"p612browser_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{runNonce[..8]}";
        var paths = HarnessPaths.Create(runKey);
        Require(
            !Directory.EnumerateFileSystemEntries(paths.RunRoot).Any(),
            $"P6-12 run root is not fresh: {paths.RunRoot}");
        var iterationRoot = paths.IterationRoot(1);
        var secretPath = Path.Combine(
            iterationRoot,
            "p6-12-browser-fixture.runtime.secret.json");
        var redactedPath = Path.Combine(
            iterationRoot,
            "p6-12-browser-fixture.json");
        var lifecyclePath = Path.Combine(
            iterationRoot,
            "p6-12-browser-fixture.lifecycle.json");
        var stopPath = Path.Combine(
            iterationRoot,
            "p6-12-browser-fixture.stop");
        var browserResultPath = Path.Combine(
            iterationRoot,
            "p6-12-browser-result.json");
        var conflictTriggerPath = Path.Combine(
            iterationRoot,
            "p6-12-browser-conflict.trigger.json");
        var conflictAckPath = Path.Combine(
            iterationRoot,
            "p6-12-browser-conflict.ack.json");
        var oraclePath = Path.Combine(
            iterationRoot,
            "p6-12-browser-direct-mongo-oracle.json");
        var cleanupPath = Path.Combine(
            iterationRoot,
            "p6-12-browser-cleanup.json");
        var resultPath = Path.Combine(
            iterationRoot,
            "p6-12-browser-fixture-result.json");

        var cleanupErrors = new List<string>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        P612BrowserActors? actors = null;
        IReadOnlyList<P612BrowserJourney>? journeys = null;
        string? ownerToken = null;
        P612ConflictControlResult? conflictControl = null;
        string? failure = null;
        string? stopReason = null;
        var browserVerdict = options.ReadinessOnly
            ? "NOT_RUN_READINESS_ONLY"
            : "NOT_RUN";
        var passed = false;
        string? redactedManifestSha256 = null;
        string? browserResultSha256 = null;
        IReadOnlyList<string> artifactSecretLeakFiles =
            Array.Empty<string>();
        var readyAtUtc = DateTime.MinValue;
        var expiresAtUtc = DateTime.MinValue;
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.WriteLine($"P6-12 browser fixture artifacts: {paths.RunRoot}");
        try
        {
            mongo = await MongoReplicaSetLease.StartAsync(
                paths,
                iterationRoot,
                runKey,
                1,
                cancellation.Token);
            var database = mongo.Client.GetDatabase(mongo.DatabaseName);
            backend = await StartP612BrowserBackendAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                options.FrontendOrigin,
                cancellation.Token);

            using (var api = new ApiHarnessClient(backend.BaseUri))
            {
                var bootstrap = await BootstrapAndLoginAsync(
                    api,
                    backend,
                    database,
                    cancellation.Token);
                ownerToken = bootstrap.Token;
                var baseFixture = await SeedFixtureAsync(
                    database,
                    cancellation.Token);
                journeys = await SeedP612JourneyDefinitionsAsync(
                    database,
                    baseFixture,
                    cancellation.Token);
                actors = await SeedP612ActorsAndAclAsync(
                    api,
                    backend,
                    database,
                    baseFixture,
                    journeys,
                    bootstrap.Password,
                    cancellation.Token);
                journeys = await LaunchP612JourneysAsync(
                    api,
                    bootstrap.Token,
                    database,
                    baseFixture,
                    journeys,
                    cancellation.Token);
                journeys = await ArrangeP612MeaningfulStatesAsync(
                    api,
                    backend,
                    bootstrap.Token,
                    database,
                    actors,
                    journeys,
                    cancellation.Token);
                await RequireP612ActorAccessAsync(
                    api,
                    actors,
                    journeys,
                    cancellation.Token);
            }

            readyAtUtc = DateTime.UtcNow;
            expiresAtUtc = readyAtUtc.Add(options.Timeout);
            await EvidenceJson.WriteAsync(
                redactedPath,
                BuildP612BrowserManifest(
                    containsSecrets: false,
                    state: "READY",
                    runKey,
                    runNonce,
                    backend,
                    mongo,
                    options,
                    paths.RunRoot,
                    readyAtUtc,
                    expiresAtUtc,
                    stopPath,
                    browserResultPath,
                    conflictTriggerPath,
                    conflictAckPath,
                    actors,
                    journeys,
                    redactedManifestSha256: null,
                    stopReason: null,
                    secretManifestDeleted: false,
                    conflictControl: null),
                cancellation.Token);
            redactedManifestSha256 = await P612FileSha256Async(
                redactedPath,
                cancellation.Token);
            await EvidenceJson.WriteAsync(
                secretPath,
                BuildP612BrowserManifest(
                    containsSecrets: true,
                    state: "READY",
                    runKey,
                    runNonce,
                    backend,
                    mongo,
                    options,
                    paths.RunRoot,
                    readyAtUtc,
                    expiresAtUtc,
                    stopPath,
                    browserResultPath,
                    conflictTriggerPath,
                    conflictAckPath,
                    actors,
                    journeys,
                    redactedManifestSha256,
                    stopReason: null,
                    secretManifestDeleted: false,
                    conflictControl: null),
                cancellation.Token);

            Console.WriteLine(
                $"P6_12_BROWSER_FIXTURE_READY={Path.GetFullPath(secretPath)}");
            Console.WriteLine(
                $"P6_12_BROWSER_FIXTURE_REDACTED={Path.GetFullPath(redactedPath)}");
            Console.WriteLine(
                $"P6_12_BROWSER_FIXTURE_STOP_FILE={Path.GetFullPath(stopPath)}");
            Console.WriteLine(
                $"P6_BROWSER_FIXTURE_READY={Path.GetFullPath(secretPath)}");
            Console.Out.Flush();

            P612BrowserEvidenceValidationResult? browserEvidence = null;
            if (options.ReadinessOnly)
            {
                stopReason = "READINESS_ONLY";
            }
            else
            {
                var controlWait = await WaitForP612BrowserControlAsync(
                    stopPath,
                    conflictTriggerPath,
                    conflictAckPath,
                    expiresAtUtc,
                    backend,
                    database,
                    ownerToken!,
                    actors.Owner,
                    journeys.Single(row => row.JourneyId == "T11"),
                    runKey,
                    runNonce,
                    cancellation.Token);
                stopReason = controlWait.StopReason;
                conflictControl = controlWait.Conflict;
                Require(
                    stopReason == "STOP_FILE",
                    "P6-12 browser fixture timed out before the stop file was created.");
                Require(
                    conflictControl is not null,
                    "P6-12 browser fixture stopped without the required T11 stale-revision conflict control.");
                browserEvidence =
                    await P612BrowserEvidenceContract
                        .ParseAndValidateFileAsync(
                    browserResultPath,
                    BuildP612BrowserEvidenceExpectations(
                        paths.RunRoot,
                        runKey,
                        runNonce,
                        redactedManifestSha256,
                        options,
                        backend,
                        actors,
                        journeys),
                    cancellation.Token);
                browserResultSha256 = browserEvidence.ResultSha256;
                browserVerdict = "PASS";
            }

            var oracle = await RunP612DirectMongoOracleAsync(
                database,
                redactedPath,
                actors,
                journeys,
                browserEvidence,
                conflictControl,
                runKey,
                runNonce,
                redactedManifestSha256,
                browserResultSha256,
                cancellation.Token);
            await EvidenceJson.WriteAsync(
                oraclePath,
                oracle,
                cancellation.Token);
            passed = true;
            TryDeleteBrowserFixtureFile(secretPath, cleanupErrors);
            TryDeleteBrowserFixtureFile(stopPath, cleanupErrors);
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
                mongo is not null &&
                actors is not null &&
                journeys is not null)
            {
                try
                {
                    await EvidenceJson.WriteAsync(
                        lifecyclePath,
                        BuildP612BrowserManifest(
                            containsSecrets: false,
                            state: "STOPPED",
                            runKey,
                            runNonce,
                            backend,
                            mongo,
                            options,
                            paths.RunRoot,
                            readyAtUtc,
                            expiresAtUtc,
                            stopPath,
                            browserResultPath,
                            conflictTriggerPath,
                            conflictAckPath,
                            actors,
                            journeys,
                            redactedManifestSha256,
                            stopReason ?? "FAILURE",
                            secretManifestDeleted: !File.Exists(secretPath),
                            conflictControl),
                        CancellationToken.None);
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"redacted-manifest: {error.Message}");
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

            if (actors is not null)
            {
                try
                {
                    artifactSecretLeakFiles =
                        ScanP612ArtifactsForSecrets(
                            iterationRoot,
                            actors.All
                                .Select(actor => actor.Password)
                                .Distinct(StringComparer.Ordinal)
                                .ToArray());
                    if (artifactSecretLeakFiles.Count > 0)
                    {
                        cleanupErrors.Add(
                            "artifact-security: actor credential found in " +
                            string.Join(", ", artifactSecretLeakFiles));
                    }
                }
                catch (Exception error)
                {
                    cleanupErrors.Add(
                        $"artifact-security-scan: {error.Message}");
                }
            }

            if (cleanupErrors.Count > 0)
            {
                passed = false;
                failure ??=
                    "One or more P6-12 browser fixture cleanup checks failed.";
            }
            await EvidenceJson.WriteAsync(
                cleanupPath,
                new
                {
                    runKey,
                    verdict = cleanupErrors.Count == 0 ? "PASS" : "FAIL",
                    mode = options.ReadinessOnly
                        ? "READINESS_ONLY"
                        : "BROWSER",
                    runNonce,
                    expectedActivation = options.ExpectedActivation,
                    readyManifestPath = redactedPath,
                    readyManifestSha256 = redactedManifestSha256,
                    lifecyclePath,
                    browserResultSha256,
                    conflictControl,
                    artifactSecurity = new
                    {
                        scanned = actors is not null,
                        leakCount = artifactSecretLeakFiles.Count,
                        leakFiles = artifactSecretLeakFiles
                    },
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
                resultPath,
                new
                {
                    runKey,
                    verdict = options.ReadinessOnly
                        ? passed ? "NOT_EVALUATED" : "FAIL"
                        : passed ? "PASS" : "FAIL",
                    fixtureReadiness = passed ? "READY" : "FAILED",
                    mode = options.ReadinessOnly
                        ? "READINESS_ONLY"
                        : "BROWSER",
                    runNonce,
                    expectedActivation = options.ExpectedActivation,
                    browserVerdict,
                    readyManifestPath = redactedPath,
                    readyManifestSha256 = redactedManifestSha256,
                    browserResultSha256,
                    conflictControl,
                    productionFrontendBuildId = options.FrontendBuildId,
                    productionFrontendSourceRevision =
                        options.FrontendSourceRevision,
                    artifactSecretLeakCount =
                        artifactSecretLeakFiles.Count,
                    failure,
                    oraclePath,
                    cleanupPath,
                    completedAtUtc = DateTime.UtcNow
                },
                CancellationToken.None);
        }

        Console.WriteLine(
            passed
                ? options.ReadinessOnly
                    ? $"[READY] P6-12 fixture readiness/oracle completed; browser verdict NOT_EVALUATED; artifact={paths.RunRoot}"
                    : $"[DAT] P6-12 browser fixture/oracle passed; artifact={paths.RunRoot}"
                : $"[KHONG_DAT] P6-12 browser fixture/oracle failed: {failure}; artifact={paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private static Task<BackendServerLease> StartP612BrowserBackendAsync(
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        MongoReplicaSetLease mongo,
        Uri frontendOrigin,
        CancellationToken ct)
    {
        var serverRoot = Path.Combine(iterationRoot, "backend-p6-12-browser");
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
                    !DynamicFlowP6CatalogCandidate.ActivationEnabled,
                DynamicFlowRuntimeCandidateActivationThrough =
                    P612CandidateActivationThrough,
                FrontendOrigin =
                    frontendOrigin.GetLeftPart(UriPartial.Authority)
            });
    }

    private static async Task<IReadOnlyList<P612BrowserJourney>>
        SeedP612JourneyDefinitionsAsync(
            IMongoDatabase database,
            ProbeFixture baseFixture,
            CancellationToken ct)
    {
        var target = new[] { baseFixture.TargetUnitIds[0] };
        var rows = new List<P612BrowserJourney>
        {
            NewP612Journey(
                "T01",
                "FLOW-T01",
                baseFixture.T01WorkId,
                baseFixture.T01VersionId,
                target,
                new Dictionary<string, string>()),
            NewP612Journey(
                "T02",
                "FLOW-T02",
                baseFixture.T02WorkId,
                baseFixture.T02VersionId,
                baseFixture.TargetUnitIds.Take(2).ToArray(),
                new Dictionary<string, string>())
        };

        var t03Seed = await SeedP601FixtureAsync(database, baseFixture, ct);
        await RenameP603FixtureAsync(
            database,
            t03Seed,
            "P6_12_FLOW_T03",
            "P6-12-T03",
            ct);
        rows.Add(NewP612Journey(
            "T03",
            "FLOW-T03",
            t03Seed.WorkId,
            t03Seed.VersionId,
            target,
            Related(("familyId", t03Seed.FamilyId))));

        var t04Seed = await SeedP601FixtureAsync(database, baseFixture, ct);
        var t04 = await ConvertP601FixtureToP602Async(database, t04Seed, ct);
        await RenameP603FixtureAsync(
            database,
            t04Seed,
            "P6_12_FLOW_T04",
            "P6-12-T04",
            ct);
        rows.Add(NewP612Journey(
            "T04",
            "FLOW-T04",
            t04.WorkId,
            t04.VersionId,
            target,
            Related(("familyId", t04.FamilyId))));

        var t05Seed = await SeedP601FixtureAsync(database, baseFixture, ct);
        var t05 = await ConvertP601FixtureToP603Async(database, t05Seed, ct);
        await RenameP603FixtureAsync(
            database,
            t05Seed,
            "P6_12_FLOW_T05",
            "P6-12-T05",
            ct);
        rows.Add(NewP612Journey(
            "T05",
            "FLOW-T05",
            t05.WorkId,
            t05.VersionId,
            target,
            Related(("familyId", t05.FamilyId))));

        var t06Seed = await SeedP601FixtureAsync(database, baseFixture, ct);
        var t06 = await ConvertP601FixtureToP604Async(database, t06Seed, ct);
        await RenameP603FixtureAsync(
            database,
            t06Seed,
            "P6_12_FLOW_T06",
            "P6-12-T06",
            ct);
        rows.Add(NewP612Journey(
            "T06",
            "FLOW-T06",
            t06.WorkId,
            t06.VersionId,
            target,
            Related(("familyId", t06.FamilyId))));

        var t07Seed = await SeedP601FixtureAsync(database, baseFixture, ct);
        var t07PeriodKey = P612PeriodKey("T07");
        var t07 = await ConvertP601FixtureToP605Async(
            database,
            t07Seed,
            new JsonObject
            {
                ["operator"] = "EQ",
                ["field"] = "instance.periodKey",
                ["value"] = t07PeriodKey
            },
            ct);
        await RenameP603FixtureAsync(
            database,
            t07Seed,
            "P6_12_FLOW_T07",
            "P6-12-T07",
            ct);
        rows.Add(NewP612Journey(
            "T07",
            "FLOW-T07",
            t07.WorkId,
            t07.VersionId,
            target,
            Related(("familyId", t07.FamilyId))));

        var t08Seed = await SeedP601FixtureAsync(database, baseFixture, ct);
        var t08 = await ConvertP601FixtureToP606Async(
            database,
            t08Seed,
            maxReviewCycles: 2,
            ct);
        await RenameP603FixtureAsync(
            database,
            t08Seed,
            "P6_12_FLOW_T08",
            "P6-12-T08",
            ct);
        rows.Add(NewP612Journey(
            "T08",
            "FLOW-T08",
            t08.WorkId,
            t08.VersionId,
            target,
            Related(
                ("familyId", t08Seed.FamilyId),
                ("maxReviewCycles", t08.MaxReviewCycles.ToString()))));

        var t09ChildSeed = await SeedP601FixtureAsync(
            database,
            baseFixture,
            ct);
        _ = await ConvertP601FixtureToP607ChildAsync(
            database,
            t09ChildSeed,
            maxReviewCycles: 2,
            ct);
        await RenameP603FixtureAsync(
            database,
            t09ChildSeed,
            "P6_12_FLOW_T09_CHILD",
            "P6-12-T09-CHILD",
            ct);
        var t09Seed = await SeedP601FixtureAsync(database, baseFixture, ct);
        var t09 = await ConvertP601FixtureToP607Async(
            database,
            t09Seed,
            t09ChildSeed.FamilyId,
            t09ChildSeed.VersionId,
            ct);
        await RenameP603FixtureAsync(
            database,
            t09Seed,
            "P6_12_FLOW_T09",
            "P6-12-T09",
            ct);
        rows.Add(NewP612Journey(
            "T09",
            "FLOW-T09",
            t09.WorkId,
            t09.VersionId,
            target,
            Related(
                ("familyId", t09Seed.FamilyId),
                ("childWorkId", t09ChildSeed.WorkId),
                ("childFamilyId", t09.ChildFamilyId),
                ("childVersionId", t09.ChildVersionId))));

        var t10Seed = await SeedP601FixtureAsync(database, baseFixture, ct);
        var t10 = await ConvertP601FixtureToP608Async(
            database,
            t10Seed,
            "p6-12-daily",
            ct);
        await RenameP603FixtureAsync(
            database,
            t10Seed,
            "P6_12_FLOW_T10",
            "P6-12-T10",
            ct);
        rows.Add(NewP612Journey(
            "T10",
            "FLOW-T10",
            t10.WorkId,
            t10.VersionId,
            target,
            Related(
                ("familyId", t10Seed.FamilyId),
                ("scheduleKey", "p6-12-daily"))));

        var t11Seed = await SeedP601FixtureAsync(database, baseFixture, ct);
        var t11 = await ConvertP601FixtureToP609Async(
            database,
            t11Seed,
            ct);
        await RenameP603FixtureAsync(
            database,
            t11Seed,
            "P6_12_FLOW_T11",
            "P6-12-T11",
            ct);
        rows.Add(NewP612Journey(
            "T11",
            "FLOW-T11",
            t11.WorkId,
            t11.VersionId,
            target,
            Related(
                ("familyId", t11Seed.FamilyId),
                ("formNodeId", t11.FormNodeId))));

        var t12Seed = await SeedP601FixtureAsync(database, baseFixture, ct);
        var t12 = await ConvertP601FixtureToP610Async(
            database,
            t12Seed,
            ct);
        await RenameP603FixtureAsync(
            database,
            t12Seed,
            "P6_12_FLOW_T12",
            "P6-12-T12",
            ct);
        rows.Add(NewP612Journey(
            "T12",
            "FLOW-T12",
            t12.WorkId,
            t12.VersionId,
            target,
            Related(
                ("familyId", t12Seed.FamilyId),
                ("finalizeWorkId", t12.FinalizeWorkId),
                ("entryNodeId", t12.EntryNodeId))));

        Require(
            rows.Count == 12 &&
            rows.Select(row => row.JourneyId)
                .SequenceEqual(
                    Enumerable.Range(1, 12).Select(value => $"T{value:00}"),
                    StringComparer.Ordinal),
            "P6-12 fixture did not seed the exact T01..T12 journey set.");
        return rows;
    }

    private static async Task<P612BrowserActors> SeedP612ActorsAndAclAsync(
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        ProbeFixture baseFixture,
        IReadOnlyList<P612BrowserJourney> journeys,
        string adminPassword,
        CancellationToken ct)
    {
        var users = database.GetCollection<AppUser>("users");
        var units = database.GetCollection<Unit>("units");
        var admin = await users
            .Find(user => user.Username == "admin" && !user.IsDeleted)
            .SingleAsync(ct);
        var root = await units
            .Find(unit => unit.Id == admin.UnitId && !unit.IsDeleted)
            .SingleAsync(ct);
        var participants = await users
            .Find(user =>
                user.UnitId != null &&
                baseFixture.TargetUnitIds.Contains(user.UnitId) &&
                !user.IsDeleted)
            .ToListAsync(ct);
        var reporter = participants.Single(
            user => user.UnitId == baseFixture.TargetUnitIds[0]);
        var reviewer = participants.Single(
            user => user.UnitId == baseFixture.TargetUnitIds[1]);
        var outsider = participants.Single(
            user => user.UnitId == baseFixture.TargetUnitIds[2]);
        var now = DateTime.UtcNow;
        var coordinator = new AppUser
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Username = "p6_12_browser_coordinator",
            PasswordHash = string.Empty,
            FullName = "P6-12 Browser Coordinator",
            UnitId = admin.UnitId,
            PositionCode = "COORDINATOR",
            AccountKind = ManagementAccountKind.UnitManager,
            Roles = [],
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = admin.Id,
            UpdatedByUserId = admin.Id,
            IsDeleted = false
        };
        var hasher = new PasswordHasher<AppUser>();
        coordinator.PasswordHash = hasher.HashPassword(
            coordinator,
            backend.ActorPassword);
        await users.InsertOneAsync(coordinator, cancellationToken: ct);
        foreach (var actor in new[] { reporter, reviewer, outsider })
        {
            var passwordHash = hasher.HashPassword(
                actor,
                backend.ActorPassword);
            await users.UpdateOneAsync(
                user => user.Id == actor.Id && !user.IsDeleted,
                Builders<AppUser>.Update
                    .Set(user => user.PasswordHash, passwordHash)
                    .Set(user => user.UpdatedAtUtc, now),
                cancellationToken: ct);
        }

        var coordinatorRef = new UserRef
        {
            UserId = coordinator.Id,
            Username = coordinator.Username,
            FullName = coordinator.FullName,
            UnitId = coordinator.UnitId,
            UnitSymbol = root.Symbol,
            UnitShortName = root.ShortName,
            UnitName = root.FullName,
            PositionCode = coordinator.PositionCode
        };
        var relatedWorkIds = journeys
            .SelectMany(row => row.RelatedIds
                .Where(pair => pair.Key.EndsWith(
                    "WorkId",
                    StringComparison.Ordinal))
                .Select(pair => pair.Value));
        var aclWorkIds = journeys
            .Select(row => row.WorkId)
            .Concat(relatedWorkIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var works = await database.GetCollection<Work>("works")
            .Find(work => aclWorkIds.Contains(work.Id) && !work.IsDeleted)
            .ToListAsync(ct);
        Require(
            works.Count == aclWorkIds.Length,
            "P6-12 actor ACL fixture is missing one or more works.");
        await database.GetCollection<Work>("works").UpdateManyAsync(
            work => aclWorkIds.Contains(work.Id) && !work.IsDeleted,
            Builders<Work>.Update
                .Set(work => work.LeaderDirectiveUserId, coordinator.Id)
                .Set(work => work.LeaderDirective, coordinatorRef)
                .Set(work => work.UpdatedAtUtc, now)
                .Set(work => work.UpdatedByUserId, admin.Id),
            cancellationToken: ct);

        var roles = new List<DocRole>();
        foreach (var work in works)
        {
            roles.Add(NewP612WorkRole(
                work,
                admin,
                work.Owner,
                DocRoleType.OWNER,
                admin.Id,
                now));
            roles.Add(NewP612WorkRole(
                work,
                coordinator,
                coordinatorRef,
                DocRoleType.LEADER_DIRECTIVE,
                admin.Id,
                now));
        }
        await database.GetCollection<DocRole>("doc_roles")
            .InsertManyAsync(roles, cancellationToken: ct);

        var actors = new P612BrowserActors(
            new P612BrowserActor(
                admin.Username,
                admin.Id,
                adminPassword,
                "OWNER",
                "All seeded P6-12 works"),
            new P612BrowserActor(
                coordinator.Username,
                coordinator.Id,
                backend.ActorPassword,
                "COORDINATOR",
                "Leader-directive role on all seeded P6-12 works"),
            new P612BrowserActor(
                reporter.Username,
                reporter.Id,
                backend.ActorPassword,
                "REPORTER",
                "Target unit for T01 and T03..T12"),
            new P612BrowserActor(
                reviewer.Username,
                reviewer.Id,
                backend.ActorPassword,
                "REVIEWER",
                "Exact submitted-report reviewer on T08; T02 target unit"),
            new P612BrowserActor(
                outsider.Username,
                outsider.Id,
                backend.ActorPassword,
                "OUTSIDER",
                "No seeded work role or participant scope"));

        foreach (var actor in actors.All)
        {
            _ = await api.LoginAsync(actor.Username, actor.Password, ct);
        }
        return actors;
    }

    private static DocRole NewP612WorkRole(
        Work work,
        AppUser user,
        UserRef? userRef,
        DocRoleType role,
        string issuerUserId,
        DateTime now)
        => new()
        {
            Id = ObjectId.GenerateNewId().ToString(),
            DocType = DocType.WORK,
            DocId = work.Id,
            UserId = user.Id,
            Role = role,
            User = userRef ?? ToP612UserRef(user),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = issuerUserId,
            UpdatedByUserId = issuerUserId,
            IsDeleted = false
        };

    private static UserRef ToP612UserRef(AppUser user)
        => new()
        {
            UserId = user.Id,
            Username = user.Username,
            FullName = user.FullName,
            UnitId = user.UnitId,
            PositionCode = user.PositionCode
        };

    private static async Task<IReadOnlyList<P612BrowserJourney>>
        LaunchP612JourneysAsync(
            ApiHarnessClient api,
            string adminToken,
            IMongoDatabase database,
            ProbeFixture baseFixture,
            IReadOnlyList<P612BrowserJourney> journeys,
            CancellationToken ct)
    {
        var launched = new List<P612BrowserJourney>(journeys.Count);
        foreach (var journey in journeys)
        {
            if (journey.JourneyId == "T10")
            {
                var create = await api.PostAsync(
                    $"api/works/{journey.WorkId}/dynamic-flows/periodic-schedules",
                    new DynamicFlowPeriodicScheduleCreateRequest
                    {
                        FlowTemplateVersionId = journey.VersionId,
                        CommandId = journey.CommandId,
                        TargetUnitIds = journey.TargetUnitIds.ToList(),
                        TimeZoneId = "UTC",
                        LocalTime = "08:00",
                        EffectiveFromUtc =
                            DateTime.UtcNow.Date.AddDays(1).AddHours(8)
                    },
                    adminToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    create,
                    HttpStatusCode.OK,
                    "P6-12 FLOW-T10 periodic schedule");
                var scheduleId = ApiHarnessClient.RequiredString(
                    create.Json,
                    "scheduleId");
                var schedule = await database
                    .GetCollection<DynamicFlowPeriodicSchedule>(
                        "dynamic_flow_periodic_schedules")
                    .Find(row => row.Id == scheduleId && !row.IsDeleted)
                    .SingleAsync(ct);
                launched.Add(journey with
                {
                    ScheduleId = scheduleId,
                    RelatedIds = MergeP612Related(
                        journey.RelatedIds,
                        ("scheduleIdentityJson", schedule.ScheduleIdentityJson),
                        ("scheduleIdentityHash", schedule.ScheduleIdentityHash),
                        ("nextDueAtUtc", schedule.NextDueAtUtc.ToString("O")))
                });
                continue;
            }

            var launch = await LaunchAsync(
                api,
                adminToken,
                baseFixture,
                journey.WorkId,
                journey.VersionId,
                journey.CommandId,
                journey.TargetUnitIds,
                journey.PeriodKey,
                ct);
            RequireStatus(
                launch.Confirm,
                "SUCCEEDED",
                $"P6-12 {journey.ArchetypeId} launch");
            launched.Add(journey with
            {
                InstanceId = ApiHarnessClient.RequiredString(
                    launch.Confirm.Json,
                    "flowInstanceId")
            });
        }

        var launchedInstanceIds = launched
            .Where(row => row.InstanceId != null)
            .Select(row => row.InstanceId!)
            .ToArray();
        var instances = await database
            .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(instance =>
                launchedInstanceIds.Contains(instance.Id) &&
                !instance.IsDeleted)
            .ToListAsync(ct);
        Require(
            launchedInstanceIds.Length == 11 &&
            instances.Count == launchedInstanceIds.Length,
            $"P6-12 fixture launched IDs={launchedInstanceIds.Length}, persisted matching instances={instances.Count}; expected eleven.");
        for (var index = 0; index < launched.Count; index++)
        {
            if (launched[index].InstanceId is null)
                continue;
            var instance = instances.Single(
                row => row.Id == launched[index].InstanceId);
            launched[index] = launched[index] with
            {
                RelatedIds = MergeP612Related(
                    launched[index].RelatedIds,
                    ("scheduleIdentityJson", instance.ScheduleIdentityJson),
                    ("scheduleIdentityHash", instance.ScheduleIdentityHash))
            };
        }
        return launched;
    }

    private static async Task<IReadOnlyList<P612BrowserJourney>>
        ArrangeP612MeaningfulStatesAsync(
            ApiHarnessClient api,
            BackendServerLease backend,
            string adminToken,
            IMongoDatabase database,
            P612BrowserActors actors,
            IReadOnlyList<P612BrowserJourney> journeys,
            CancellationToken ct)
    {
        var arranged = journeys.ToDictionary(
            row => row.JourneyId,
            row => row,
            StringComparer.Ordinal);

        foreach (var journeyId in new[] { "T05", "T06" })
        {
            var journey = arranged[journeyId];
            await ReconcileP603Async(
                api,
                adminToken,
                journey.InstanceId!,
                ct);
            var entry = (await LoadP601StepsAsync(
                    database,
                    journey.InstanceId!,
                    ct))
                .Single();
            entry = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                entry,
                "field_note",
                "P5_NOTE",
                $"p612-{journeyId.ToLowerInvariant()}-entry",
                ct);
            var capability = await ReadP601ForwardCapabilityAsync(
                api,
                adminToken,
                journey.WorkId,
                journey.InstanceId!,
                entry.Id,
                expectedCanForward: true,
                ct);
            var forward = await ForwardP601Async(
                api,
                adminToken,
                journey.WorkId,
                entry.AssignmentId!,
                $"{journey.CommandId}-fanout",
                capability.InstanceRevision,
                capability.StepRevision,
                ct);
            AssertP601ForwardSuccess(
                forward,
                replayed: false,
                $"P6-12 {journeyId} entry fan-out");
            var branches = await WaitForP603BranchesAsync(
                database,
                journey.InstanceId!,
                ct);
            var gateway = await LoadP603GatewayAsync(
                database,
                journey.InstanceId!,
                ct);
            var expectedKind = journeyId == "T05"
                ? DynamicFlowGatewayKinds.JoinAll
                : DynamicFlowGatewayKinds.JoinAny;
            Require(
                gateway.GatewayKind == expectedKind &&
                gateway.State == DynamicFlowGatewayStates.Collecting &&
                gateway.ExpectedContributionIds.Count == 2 &&
                branches.Count == 2,
                $"P6-12 {journeyId} did not reach visible collecting state.");
            if (journeyId == "T06")
            {
                for (var index = 0; index < branches.Count; index++)
                {
                    branches[index] = await ApproveP601StepAsync(
                        api,
                        backend,
                        adminToken,
                        database,
                        branches[index],
                        null,
                        null,
                        $"p612-t06-{branches[index].FlowStepCode.ToLowerInvariant()}",
                        ct);
                }
                var winner = branches[0];
                var loser = branches[1];
                var winnerCompletion = await CompleteP604AssignmentAsync(
                    api,
                    adminToken,
                    winner.AssignmentId!,
                    winner.FlowStepCode,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    winnerCompletion,
                    HttpStatusCode.OK,
                    "P6-12 T06 quorum winner");
                await WaitForP602InstanceStateAsync(
                    api,
                    adminToken,
                    database,
                    journey.InstanceId!,
                    DynamicFlowInstanceStates.Completed,
                    ct);
                await WaitForP601StepStateAsync(
                    api,
                    adminToken,
                    database,
                    loser.Id,
                    DynamicFlowStepStates.CancelledByGateway,
                    ct);
                var lateCompletion = await CompleteP604AssignmentAsync(
                    api,
                    adminToken,
                    loser.AssignmentId!,
                    loser.FlowStepCode,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    lateCompletion,
                    HttpStatusCode.OK,
                    "P6-12 T06 late ignored completion");
                gateway = await LoadP603GatewayAsync(
                    database,
                    journey.InstanceId!,
                    ct);
                Require(
                    gateway.State == DynamicFlowGatewayStates.Satisfied &&
                    gateway.ArrivedContributionIds.Count == 1 &&
                    gateway.CancelledContributionIds.Count == 1 &&
                    gateway.LateContributionIds.Count == 1 &&
                    gateway.WinnerContributionId == winner.ContributionId &&
                    gateway.LateContributionIds.Single() ==
                    loser.ContributionId,
                    "P6-12 T06 did not persist one winner, one cancellation, and one exact late ledger.");
                arranged[journeyId] = journey with
                {
                    RelatedIds = MergeP612Related(
                        journey.RelatedIds,
                        ("entryStepInstanceId", entry.Id),
                        ("gatewayInstanceId", gateway.GatewayInstanceId),
                        ("winnerStepInstanceId", winner.Id),
                        ("cancelledLateStepInstanceId", loser.Id),
                        ("winnerContributionId", winner.ContributionId!),
                        ("lateContributionId", loser.ContributionId!)),
                    ExpectedState = Related(
                        ("instanceState", DynamicFlowInstanceStates.Completed),
                        ("gatewayKind", expectedKind),
                        ("gatewayState", DynamicFlowGatewayStates.Satisfied),
                        ("arrivedContributions", "1"),
                        ("cancelledContributions", "1"),
                        ("lateContributions", "1"),
                        ("lateOutcome", DynamicFlowGatewayContributionOutcomes.LateIgnored)),
                    BrowserActions =
                    [
                        "Assert the quorum card is SATISFIED with exactly one winner.",
                        "Assert cancelled contributors >= 1 and late contributors >= 1; verify the late row is LATE_IGNORED."
                    ]
                };
                continue;
            }
            arranged[journeyId] = journey with
            {
                RelatedIds = MergeP612Related(
                    journey.RelatedIds,
                    ("entryStepInstanceId", entry.Id),
                    ("gatewayInstanceId", gateway.GatewayInstanceId),
                    ("branchBStepInstanceId", branches[0].Id),
                    ("branchCStepInstanceId", branches[1].Id)),
                ExpectedState = Related(
                    ("instanceState", DynamicFlowInstanceStates.Active),
                    ("gatewayKind", expectedKind),
                    ("gatewayState", DynamicFlowGatewayStates.Collecting),
                    ("expectedContributions", "2"),
                    ("arrivedContributions", "0"),
                    ("activeBranchCount", "2")),
                BrowserActions =
                [
                    "Assert the JOIN_ALL card is COLLECTING.",
                    "Assert exactly two expected contributors, zero arrivals, and two visible missing contributors."
                ]
            };
        }

        {
            var journey = arranged["T07"];
            await ReconcileP603Async(
                api,
                adminToken,
                journey.InstanceId!,
                ct);
            var entry = (await LoadP601StepsAsync(
                    database,
                    journey.InstanceId!,
                    ct))
                .Single();
            entry = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                entry,
                "field_note",
                "P5_NOTE",
                "p612-t07-entry",
                ct);
            var capability = await ReadP601ForwardCapabilityAsync(
                api,
                adminToken,
                journey.WorkId,
                journey.InstanceId!,
                entry.Id,
                expectedCanForward: true,
                ct);
            var forward = await ForwardP601Async(
                api,
                adminToken,
                journey.WorkId,
                entry.AssignmentId!,
                $"{journey.CommandId}-decide",
                capability.InstanceRevision,
                capability.StepRevision,
                ct);
            AssertP601ForwardSuccess(
                forward,
                replayed: false,
                "P6-12 T07 conditional decision");
            var selectedStep = await WaitForP612StepAsync(
                database,
                journey.InstanceId!,
                step => step.FlowStepId is "step_b" or "step_c",
                ct);
            var gateway = await LoadP603GatewayAsync(
                database,
                journey.InstanceId!,
                ct);
            Require(
                gateway.GatewayKind == DynamicFlowGatewayKinds.Condition &&
                gateway.State == DynamicFlowGatewayStates.Satisfied &&
                gateway.SelectedEdgeId == "edge-g-b" &&
                gateway.DecisionReasonCode == "RULE_MATCHED",
                "P6-12 T07 did not persist its visible typed decision.");
            var outsiderToken = await api.LoginAsync(
                actors.Outsider.Username,
                actors.Outsider.Password,
                ct);
            var beforeForbidden = await CaptureP601LedgerHashAsync(
                database,
                journey.InstanceId!,
                ct);
            var forbidden = await ForwardP601Async(
                api,
                outsiderToken,
                journey.WorkId,
                entry.AssignmentId!,
                $"{journey.CommandId}-outsider-forged",
                capability.InstanceRevision,
                capability.StepRevision,
                ct);
            ApiHarnessClient.ExpectStatus(
                forbidden,
                HttpStatusCode.Forbidden,
                "P6-12 T07 outsider forged forward");
            Require(
                beforeForbidden == await CaptureP601LedgerHashAsync(
                    database,
                    journey.InstanceId!,
                    ct),
                "P6-12 T07 forbidden forward changed direct-Mongo state.");
            arranged["T07"] = journey with
            {
                RelatedIds = MergeP612Related(
                    journey.RelatedIds,
                    ("entryStepInstanceId", entry.Id),
                    ("gatewayInstanceId", gateway.GatewayInstanceId),
                    ("selectedStepInstanceId", selectedStep.Id),
                    ("selectedEdgeId", gateway.SelectedEdgeId!)),
                ExpectedState = Related(
                    ("instanceState", DynamicFlowInstanceStates.Active),
                    ("gatewayKind", DynamicFlowGatewayKinds.Condition),
                    ("gatewayState", DynamicFlowGatewayStates.Satisfied),
                    ("selectedEdgeId", "edge-g-b"),
                    ("decisionReasonCode", "RULE_MATCHED"),
                    ("outsiderNegativeStatus", "403"),
                    ("outsiderNegativeNoWrite", "true")),
                BrowserActions =
                [
                    "Open overview and assert the conditional decision card shows edge-g-b / RULE_MATCHED.",
                    "Open timeline and verify the immutable typed decision event."
                ]
            };
        }

        {
            var journey = arranged["T08"];
            var step = (await LoadP601StepsAsync(
                    database,
                    journey.InstanceId!,
                    ct))
                .Single();
            var submitted = await SubmitP606StepAsync(
                api,
                backend,
                adminToken,
                database,
                step,
                "p612-t08-review",
                ct);
            step = await WaitForP601StepStateAsync(
                api,
                adminToken,
                database,
                step.Id,
                DynamicFlowStepStates.Submitted,
                ct);
            var instance = await LoadP601InstanceAsync(
                database,
                journey.InstanceId!,
                ct);
            var reviewer = await database
                .GetCollection<AppUser>("users")
                .Find(user =>
                    user.Id == actors.Reviewer.UserId &&
                    !user.IsDeleted)
                .SingleAsync(ct);
            var now = DateTime.UtcNow;
            await database.GetCollection<WorkAssignment>("work_assignments")
                .UpdateOneAsync(
                    assignment =>
                        assignment.Id == step.AssignmentId &&
                        !assignment.IsDeleted,
                    Builders<WorkAssignment>.Update
                        .Set(
                            assignment => assignment.CreatedByUserId,
                            reviewer.Id)
                        .Set(
                            assignment => assignment.UpdatedByUserId,
                            reviewer.Id)
                        .Set(assignment => assignment.UpdatedAtUtc, now),
                    cancellationToken: ct);
            await SeedRuntimeReviewerScopeAsync(
                database,
                instance,
                step,
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
                step.AssignmentId!,
                instance.IssuerUserId,
                reviewer.Id,
                now,
                ct);
            arranged["T08"] = journey with
            {
                RelatedIds = MergeP612Related(
                    journey.RelatedIds,
                    ("reviewStepInstanceId", step.Id),
                    ("reviewAssignmentId", step.AssignmentId!),
                    ("reviewReportId", submitted.ReportId)),
                ExpectedState = Related(
                    ("instanceState", DynamicFlowInstanceStates.Active),
                    ("stepState", DynamicFlowStepStates.Submitted),
                    ("reviewCycleNo", step.ReviewCycleNo.ToString()),
                    ("reviewerUserId", reviewer.Id)),
                BrowserActions =
                [
                    "Sign in as REVIEWER, open the T08 steps tab, and select RelatedIds.reviewStepInstanceId.",
                    "Assert review-cycle lineage and the exact submitted report, then return it to create attempt 2."
                ]
            };
        }

        {
            var journey = arranged["T09"];
            var parentStep = (await LoadP601StepsAsync(
                    database,
                    journey.InstanceId!,
                    ct))
                .Single();
            parentStep = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                parentStep,
                null,
                null,
                "p612-t09-parent",
                ct);
            var parentInstance = await LoadP601InstanceAsync(
                database,
                journey.InstanceId!,
                ct);
            var launchChild = await api.PostAsync(
                $"api/works/{journey.WorkId}/dynamic-flows/instances/{journey.InstanceId}/steps/{parentStep.Id}/subflow",
                new DynamicFlowSubflowLaunchRequest
                {
                    CommandId = $"{journey.CommandId}-child",
                    ExpectedParentInstanceRevision = parentInstance.Revision,
                    ExpectedParentStepRevision = parentStep.Revision
                },
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                launchChild,
                HttpStatusCode.OK,
                "P6-12 T09 child launch");
            var childInstanceId = ApiHarnessClient.RequiredString(
                launchChild.Json,
                "childInstanceId");
            var childStep = await WaitForP612StepAsync(
                database,
                childInstanceId,
                _ => true,
                ct);
            parentStep = await database
                .GetCollection<DynamicFlowStepInstance>(
                    "dynamic_flow_step_instances")
                .Find(row => row.Id == parentStep.Id && !row.IsDeleted)
                .SingleAsync(ct);
            Require(
                parentStep.State == DynamicFlowStepStates.WaitingChild &&
                parentStep.ChildInstanceId == childInstanceId,
                "P6-12 T09 parent did not reach visible WAITING_CHILD lineage.");
            arranged["T09"] = journey with
            {
                RelatedIds = MergeP612Related(
                    journey.RelatedIds,
                    ("parentStepInstanceId", parentStep.Id),
                    ("childInstanceId", childInstanceId),
                    ("childStepInstanceId", childStep.Id)),
                ExpectedState = Related(
                    ("instanceState", DynamicFlowInstanceStates.Active),
                    ("parentStepState", DynamicFlowStepStates.WaitingChild),
                    ("childInstanceState", DynamicFlowInstanceStates.Active),
                    ("childLineageVisible", "true")),
                BrowserActions =
                [
                    "Select RelatedIds.parentStepInstanceId and assert WAITING_CHILD with exact child lineage.",
                    "Use p6-t09-open-child, then complete the child and verify parent propagation."
                ]
            };
        }

        {
            var journey = arranged["T11"];
            var instance = await LoadP601InstanceAsync(
                database,
                journey.InstanceId!,
                ct);
            var add = await api.PostAsync(
                $"api/works/{journey.WorkId}/dynamic-flows/instances/{journey.InstanceId}/supplemental-steps",
                new DynamicFlowSupplementalAddRequest
                {
                    CommandId = $"{journey.CommandId}-add-required",
                    ExpectedInstanceRevision = instance.Revision,
                    FormNodeId = journey.RelatedIds["formNodeId"],
                    TargetUnitId = journey.TargetUnitIds.Single(),
                    CompletionRequired = true
                },
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                add,
                HttpStatusCode.OK,
                "P6-12 T11 add required supplemental");
            var supplementalStepId = ApiHarnessClient.RequiredString(
                add.Json,
                "supplementalStepId");
            var supplemental = await WaitForP612StepAsync(
                database,
                journey.InstanceId!,
                step => step.Id == supplementalStepId,
                ct);
            Require(
                supplemental.IsSupplemental &&
                supplemental.CompletionRequired &&
                supplemental.State == DynamicFlowStepStates.Assigned,
                "P6-12 T11 supplemental identity is not visible/actionable.");
            arranged["T11"] = journey with
            {
                RelatedIds = MergeP612Related(
                    journey.RelatedIds,
                    ("supplementalStepInstanceId", supplemental.Id),
                    ("supplementalAssignmentId", supplemental.AssignmentId!)),
                ExpectedState = Related(
                    ("instanceState", DynamicFlowInstanceStates.Active),
                    ("supplementalState", DynamicFlowStepStates.Assigned),
                    ("completionRequired", "true"),
                    ("canCancelSupplemental", "true")),
                BrowserActions =
                [
                    "Select the original step to assert both supplemental add controls.",
                    "Then select RelatedIds.supplementalStepInstanceId and assert exact identity plus cancel control.",
                    "Capture the displayed instance/step revisions, complete the manifest conflict trigger/ACK protocol, submit the stale cancel, assert the exact 409/CAS error, and refresh until CANCELLED_BY_GATEWAY removes the cancel control."
                ]
            };
        }

        {
            var journey = arranged["T12"];
            var epochOneStep = (await LoadP601StepsAsync(
                    database,
                    journey.InstanceId!,
                    ct))
                .Single();
            epochOneStep = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                epochOneStep,
                null,
                null,
                "p612-t12-epoch-1",
                ct);
            var epochOne = await LoadP601InstanceAsync(
                database,
                journey.InstanceId!,
                ct);
            var rollback = await api.PostAsync(
                EpochRoute(
                    journey.WorkId,
                    journey.InstanceId!,
                    "rollback"),
                EpochRequest(
                    $"{journey.CommandId}-rollback",
                    epochOne.ExecutionEpoch,
                    epochOne.Revision,
                    journey.RelatedIds["entryNodeId"]),
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                rollback,
                HttpStatusCode.OK,
                "P6-12 T12 rollback to epoch 2");
            Require(
                ApiHarnessClient.RequiredInt(
                    rollback.Json,
                    "executionEpoch") == 2,
                "P6-12 T12 rollback did not advance to epoch 2.");
            var epochTwoStep = await WaitForP612StepAsync(
                database,
                journey.InstanceId!,
                step => step.ExecutionEpoch == 2,
                ct);
            var epochTwo = await LoadP601InstanceAsync(
                database,
                journey.InstanceId!,
                ct);
            Require(
                epochTwo.ExecutionEpoch == 2 &&
                epochTwoStep.IsCanonicalEpoch != false,
                "P6-12 T12 canonical epoch 2 did not materialize.");
            arranged["T12"] = journey with
            {
                RelatedIds = MergeP612Related(
                    journey.RelatedIds,
                    ("epochOneStepInstanceId", epochOneStep.Id),
                    ("epochTwoStepInstanceId", epochTwoStep.Id)),
                ExpectedState = Related(
                    ("instanceState", DynamicFlowInstanceStates.Active),
                    ("executionEpoch", "2"),
                    ("priorEpochState", DynamicFlowExecutionEpochStates.RolledBack),
                    ("canonicalEpochState", DynamicFlowExecutionEpochStates.Active)),
                BrowserActions =
                [
                    "Open overview and assert epoch 1 is invalidated and epoch 2 is canonical.",
                    "Use epoch controls with the displayed revision, refresh, and verify immutable history."
                ]
            };
        }

        foreach (var journeyId in new[] { "T01", "T02", "T03", "T04" })
        {
            var journey = arranged[journeyId];
            var instance = await LoadP601InstanceAsync(
                database,
                journey.InstanceId!,
                ct);
            var steps = await LoadP601StepsAsync(
                database,
                journey.InstanceId!,
                ct);
            arranged[journeyId] = journey with
            {
                ExpectedState = Related(
                    ("instanceState", instance.State),
                    ("executionEpoch", instance.ExecutionEpoch.ToString()),
                    ("materializedStepCount", steps.Count.ToString())),
                BrowserActions =
                [
                    "Open overview, refresh, then use the steps and timeline deep links.",
                    journeyId is "T03" or "T04"
                        ? "Approve the entry report and use the archetype forward action."
                        : "Verify launch identity and actor-scoped runtime visibility."
                ]
            };
        }

        {
            var journey = arranged["T10"];
            var schedule = await database
                .GetCollection<DynamicFlowPeriodicSchedule>(
                    "dynamic_flow_periodic_schedules")
                .Find(row => row.Id == journey.ScheduleId && !row.IsDeleted)
                .SingleAsync(ct);
            arranged["T10"] = journey with
            {
                ExpectedState = Related(
                    ("scheduleState", schedule.State),
                    ("scheduleKey", schedule.ScheduleKey),
                    ("timeZoneId", schedule.TimeZoneId),
                    ("nextDueAtUtc", schedule.NextDueAtUtc.ToString("O"))),
                BrowserActions =
                [
                    "Open the work entry panel and assert the exact ACTIVE periodic schedule.",
                    "Exercise missed-occurrence/manual-rerun UI only after a controlled process time."
                ]
            };
        }

        return Enumerable.Range(1, 12)
            .Select(value => arranged[$"T{value:00}"])
            .ToArray();
    }

    private static async Task<DynamicFlowStepInstance> WaitForP612StepAsync(
        IMongoDatabase database,
        string instanceId,
        Func<DynamicFlowStepInstance, bool> predicate,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 50; attempt++)
        {
            var rows = await LoadP601StepsAsync(database, instanceId, ct);
            var match = rows.FirstOrDefault(predicate);
            if (match is not null &&
                !string.IsNullOrWhiteSpace(match.AssignmentId))
            {
                return match;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
        throw new InvalidOperationException(
            $"P6-12 instance {instanceId} did not materialize the expected step.");
    }

    private static async Task RequireP612ActorAccessAsync(
        ApiHarnessClient api,
        P612BrowserActors actors,
        IReadOnlyList<P612BrowserJourney> journeys,
        CancellationToken ct)
    {
        var ownerToken = await api.LoginAsync(
            actors.Owner.Username,
            actors.Owner.Password,
            ct);
        var coordinatorToken = await api.LoginAsync(
            actors.Coordinator.Username,
            actors.Coordinator.Password,
            ct);
        var reporterToken = await api.LoginAsync(
            actors.Reporter.Username,
            actors.Reporter.Password,
            ct);
        var reviewerToken = await api.LoginAsync(
            actors.Reviewer.Username,
            actors.Reviewer.Password,
            ct);
        var outsiderToken = await api.LoginAsync(
            actors.Outsider.Username,
            actors.Outsider.Password,
            ct);
        var t03 = journeys.Single(row => row.JourneyId == "T03");
        var t03RuntimePath =
            $"api/works/{t03.WorkId}/dynamic-flows/instances/{t03.InstanceId}";
        var t08 = journeys.Single(row => row.JourneyId == "T08");
        var t08RuntimePath =
            $"api/works/{t08.WorkId}/dynamic-flows/instances/{t08.InstanceId}";
        var t10 = journeys.Single(row => row.JourneyId == "T10");

        foreach (var (label, token) in new[]
                 {
                     ("owner", ownerToken),
                     ("coordinator", coordinatorToken)
                 })
        {
            var workRead = await api.GetAsync(
                $"api/works/{t03.WorkId}",
                token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                workRead,
                HttpStatusCode.OK,
                $"P6-12 {label} work access");
        }
        foreach (var coordinatorJourney in journeys.Where(row =>
                     row.JourneyId is "T05" or "T06" or "T07" or "T09"))
        {
            var coordinatorRuntime = await api.GetAsync(
                $"api/works/{coordinatorJourney.WorkId}/dynamic-flows/instances/{coordinatorJourney.InstanceId}",
                coordinatorToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                coordinatorRuntime,
                HttpStatusCode.OK,
                $"P6-12 coordinator {coordinatorJourney.JourneyId} runtime access");
        }
        var ownerSchedules = await api.GetAsync(
            $"api/works/{t10.WorkId}/dynamic-flows/periodic-schedules",
            ownerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            ownerSchedules,
            HttpStatusCode.OK,
            "P6-12 owner T10 periodic schedule access");
        Require(
            ownerSchedules.Body.Contains(
                t10.ScheduleId!,
                StringComparison.Ordinal),
            "P6-12 owner response omitted the exact T10 schedule.");
        var reviewerWork = await api.GetAsync(
            $"api/works/{t08.WorkId}",
            reviewerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            reviewerWork,
            HttpStatusCode.OK,
            "P6-12 reviewer T08 work access");
        var reviewerRuntime = await api.GetAsync(
            $"{t08RuntimePath}/steps?limit=100",
            reviewerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            reviewerRuntime,
            HttpStatusCode.OK,
            "P6-12 reviewer T08 submitted-report access");
        Require(
            reviewerRuntime.Body.Contains(
                t08.RelatedIds["reviewReportId"],
                StringComparison.Ordinal),
            "P6-12 reviewer response omitted the exact T08 report.");
        var reporterRuntime = await api.GetAsync(
            t03RuntimePath,
            reporterToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            reporterRuntime,
            HttpStatusCode.OK,
            "P6-12 reporter runtime access");
        var outsiderWork = await api.GetAsync(
            $"api/works/{t03.WorkId}",
            outsiderToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            outsiderWork,
            HttpStatusCode.Forbidden,
            "P6-12 outsider forbidden work access");
        var outsiderRuntime = await api.GetAsync(
            t03RuntimePath,
            outsiderToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            outsiderRuntime,
            HttpStatusCode.NotFound,
            "P6-12 outsider hidden runtime access");
    }

    private static async Task<P612ControlWaitResult>
        WaitForP612BrowserControlAsync(
            string stopPath,
            string conflictTriggerPath,
            string conflictAckPath,
            DateTime expiresAtUtc,
            BackendServerLease backend,
            IMongoDatabase database,
            string ownerToken,
            P612BrowserActor owner,
            P612BrowserJourney t11,
            string runKey,
            string runNonce,
            CancellationToken ct)
    {
        P612ConflictControlResult? conflict = null;
        while (DateTime.UtcNow < expiresAtUtc)
        {
            ct.ThrowIfCancellationRequested();
            if (conflict is null && File.Exists(conflictTriggerPath))
            {
                var trigger = await ReadP612ConflictTriggerAsync(
                    conflictTriggerPath,
                    runKey,
                    runNonce,
                    t11,
                    ct);
                conflict = await AdvanceP612T11ConflictAsync(
                    backend,
                    database,
                    ownerToken,
                    owner,
                    t11,
                    trigger,
                    conflictAckPath,
                    runKey,
                    runNonce,
                    ct);
            }
            if (File.Exists(stopPath))
            {
                return new P612ControlWaitResult(
                    "STOP_FILE",
                    conflict);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
        }
        return new P612ControlWaitResult("TIMEOUT", conflict);
    }

    private static async Task<P612ConflictTrigger>
        ReadP612ConflictTriggerAsync(
            string path,
            string runKey,
            string runNonce,
            P612BrowserJourney t11,
            CancellationToken ct)
    {
        Exception? last = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var raw = await File.ReadAllTextAsync(path, ct);
                using var document = JsonDocument.Parse(
                    raw,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 8
                    });
                var root = document.RootElement;
                Require(
                    root.ValueKind == JsonValueKind.Object,
                    "P6-12 conflict trigger root must be an object.");
                var exactKeys = new[]
                {
                    "schemaVersion",
                    "runKey",
                    "nonce",
                    "journeyId",
                    "operation",
                    "instanceId",
                    "supplementalStepId",
                    "expectedInstanceRevision",
                    "expectedStepRevision"
                };
                var allowed = new HashSet<string>(
                    exactKeys,
                    StringComparer.Ordinal);
                var values = new Dictionary<string, JsonElement>(
                    StringComparer.Ordinal);
                foreach (var property in root.EnumerateObject())
                {
                    Require(
                        allowed.Contains(property.Name),
                        $"P6-12 conflict trigger has unknown property '{property.Name}'.");
                    Require(
                        values.TryAdd(property.Name, property.Value),
                        $"P6-12 conflict trigger has duplicate property '{property.Name}'.");
                }
                Require(
                    exactKeys.All(values.ContainsKey),
                    "P6-12 conflict trigger is missing one or more exact properties.");
                Require(
                    values["schemaVersion"].ValueKind ==
                    JsonValueKind.Number &&
                    values["schemaVersion"].TryGetInt32(out var schema) &&
                    schema == 1,
                    "P6-12 conflict trigger schemaVersion must be 1.");
                var trigger = new P612ConflictTrigger(
                    P612TriggerString(values, "runKey"),
                    P612TriggerString(values, "nonce"),
                    P612TriggerString(values, "journeyId"),
                    P612TriggerString(values, "operation"),
                    P612TriggerString(values, "instanceId"),
                    P612TriggerString(values, "supplementalStepId"),
                    P612TriggerPositiveLong(
                        values,
                        "expectedInstanceRevision"),
                    P612TriggerPositiveLong(
                        values,
                        "expectedStepRevision"));
                Require(
                    trigger.RunKey == runKey &&
                    trigger.Nonce == runNonce &&
                    trigger.JourneyId == "T11" &&
                    trigger.Operation == "CANCEL_SUPPLEMENTAL" &&
                    trigger.InstanceId == t11.InstanceId &&
                    trigger.SupplementalStepId ==
                    t11.RelatedIds["supplementalStepInstanceId"],
                    "P6-12 conflict trigger does not bind the active T11 fixture.");
                return trigger;
            }
            catch (Exception error) when (
                error is IOException or
                JsonException or
                InvalidOperationException)
            {
                last = error;
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
        }
        throw new InvalidOperationException(
            $"P6-12 conflict trigger remained unreadable or invalid: {last?.Message}",
            last);
    }

    private static string P612TriggerString(
        IReadOnlyDictionary<string, JsonElement> values,
        string key)
    {
        var value = values[key];
        Require(
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()),
            $"P6-12 conflict trigger '{key}' must be a non-blank string.");
        return value.GetString()!;
    }

    private static long P612TriggerPositiveLong(
        IReadOnlyDictionary<string, JsonElement> values,
        string key)
    {
        var value = values[key];
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var result) ||
            result <= 0)
        {
            throw new InvalidOperationException(
                $"P6-12 conflict trigger '{key}' must be a positive integer.");
        }
        return result;
    }

    private static async Task<P612ConflictControlResult>
        AdvanceP612T11ConflictAsync(
            BackendServerLease backend,
            IMongoDatabase database,
            string ownerToken,
            P612BrowserActor owner,
            P612BrowserJourney t11,
            P612ConflictTrigger trigger,
            string ackPath,
            string runKey,
            string runNonce,
            CancellationToken ct)
    {
        var instance = await LoadP601InstanceAsync(
            database,
            t11.InstanceId!,
            ct);
        var supplemental = (await LoadP601StepsAsync(
                database,
                t11.InstanceId!,
                ct))
            .Single(step => step.Id == trigger.SupplementalStepId);
        Require(
            instance.Revision == trigger.ExpectedInstanceRevision &&
            supplemental.Revision == trigger.ExpectedStepRevision &&
            supplemental.State == DynamicFlowStepStates.Assigned &&
            supplemental.IsSupplemental,
            "P6-12 conflict trigger revisions are stale before fixture advancement.");

        var commandId =
            $"p6-12-browser-conflict-{runNonce[..12]}";
        using var api = new ApiHarnessClient(backend.BaseUri);
        var response = await api.PostAsync(
            $"api/works/{t11.WorkId}/dynamic-flows/instances/{t11.InstanceId}/supplemental-steps/{supplemental.Id}/cancel",
            new DynamicFlowSupplementalCancelRequest
            {
                CommandId = commandId,
                ExpectedInstanceRevision = instance.Revision,
                ExpectedStepRevision = supplemental.Revision,
                Reason = "P6-12 controlled browser stale-revision conflict"
            },
            ownerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            "P6-12 controlled T11 supplemental cancellation");
        var currentInstance = await LoadP601InstanceAsync(
            database,
            t11.InstanceId!,
            ct);
        var currentStep = (await LoadP601StepsAsync(
                database,
                t11.InstanceId!,
                ct))
            .Single(step => step.Id == supplemental.Id);
        Require(
            currentInstance.Revision == instance.Revision + 1 &&
            currentStep.Revision == supplemental.Revision + 1 &&
            currentStep.State ==
            DynamicFlowStepStates.CancelledByGateway &&
            currentStep.SupplementalCancelledByUserId == owner.UserId,
            "P6-12 controlled T11 cancellation did not advance exact revisions/state.");
        var result = new P612ConflictControlResult(
            JourneyId: "T11",
            CommandId: commandId,
            ActorUserId: owner.UserId,
            InstanceId: currentInstance.Id,
            SupplementalStepId: currentStep.Id,
            PriorInstanceRevision: instance.Revision,
            PriorStepRevision: supplemental.Revision,
            ResultingInstanceRevision: currentInstance.Revision,
            ResultingStepRevision: currentStep.Revision,
            ResultingStepState: currentStep.State,
            EventId: ApiHarnessClient.RequiredString(
                response.Json,
                "eventId"));
        await EvidenceJson.WriteAsync(
            ackPath,
            new
            {
                schemaVersion = 1,
                runKey,
                nonce = runNonce,
                journeyId = result.JourneyId,
                operation = "CANCEL_SUPPLEMENTAL",
                result.CommandId,
                result.InstanceId,
                result.SupplementalStepId,
                result.PriorInstanceRevision,
                result.PriorStepRevision,
                result.ResultingInstanceRevision,
                result.ResultingStepRevision,
                result.ResultingStepState,
                result.EventId
            },
            ct);
        return result;
    }

    private static object BuildP612BrowserManifest(
        bool containsSecrets,
        string state,
        string runKey,
        string runNonce,
        BackendServerLease backend,
        MongoReplicaSetLease mongo,
        P612BrowserFixtureOptions options,
        string runRoot,
        DateTime readyAtUtc,
        DateTime expiresAtUtc,
        string stopPath,
        string browserResultPath,
        string conflictTriggerPath,
        string conflictAckPath,
        P612BrowserActors actors,
        IReadOnlyList<P612BrowserJourney> journeys,
        string? redactedManifestSha256,
        string? stopReason,
        bool secretManifestDeleted,
        P612ConflictControlResult? conflictControl)
    {
        var origin =
            options.FrontendOrigin.GetLeftPart(UriPartial.Authority);
        var evidenceJourneys = P612ExpectedJourneyEvidenceRows(
            origin,
            journeys);
        var actorCoverage = P612ExpectedActorCoverageRows();
        var journeyManifest = journeys.ToDictionary(
            row => row.JourneyId,
            row => (object)new
            {
                journeyId = row.JourneyId,
                archetypeId = row.ArchetypeId,
                row.WorkId,
                row.VersionId,
                row.TargetUnitIds,
                row.CommandId,
                row.PeriodKey,
                scheduleIdentityJson = row.RelatedIds["scheduleIdentityJson"],
                row.InstanceId,
                row.ScheduleId,
                routeContract = row.JourneyId == "T10"
                    ? new
                    {
                        kind = "WORK_ENTRY_PERIODIC",
                        runtimeInstanceExpected = false,
                        rootTestId = "p5-runtime-entry"
                    }
                    : new
                    {
                        kind = "RUNTIME_INSTANCE",
                        runtimeInstanceExpected = true,
                        rootTestId = "p5-runtime-page"
                    },
                row.RelatedIds,
                row.ExpectedState,
                row.BrowserActions,
                expectedActor = P612ExpectedActor(row),
                entryUrl = $"{origin}/works/{row.WorkId}?tab=ASSIGN",
                overviewUrl = row.InstanceId is null
                    ? null
                    : $"{origin}/works/{row.WorkId}/flow-instances/{row.InstanceId}/overview",
                stepsUrl = row.InstanceId is null
                    ? null
                    : $"{origin}/works/{row.WorkId}/flow-instances/{row.InstanceId}/work-to-do",
                timelineUrl = row.InstanceId is null
                    ? null
                    : $"{origin}/works/{row.WorkId}/flow-instances/{row.InstanceId}/timeline",
                browserTestIds = P612BrowserTestIds(row)
            },
            StringComparer.Ordinal);

        return new
        {
            schemaVersion = 1,
            containsSecrets,
            doNotPublish = containsSecrets,
            credentials = containsSecrets ? "INCLUDED" : "REDACTED",
            runKey,
            runNonce,
            backendBaseUrl =
                backend.BaseUri.GetLeftPart(UriPartial.Authority),
            apiBaseUrl =
                $"{backend.BaseUri.GetLeftPart(UriPartial.Authority)}/api",
            frontendOrigin = origin,
            productionFrontendBuildId = options.FrontendBuildId,
            productionFrontendSourceRevision =
                options.FrontendSourceRevision,
            databaseName = mongo.DatabaseName,
            mode = options.ReadinessOnly ? "READINESS_ONLY" : "BROWSER",
            runtime = new
            {
                environment = "Testing",
                productionEnvironmentClaimed = false,
                frontendMode = options.ReadinessOnly
                    ? "NOT_PROVIDED_READINESS_ONLY"
                    : "PRODUCTION_BUILD_REQUIRED_AFTER_READY"
            },
            activation = new
            {
                expectedMode = options.ExpectedActivation,
                catalogVersion = DynamicFlowP6CatalogCandidate.Version,
                catalogSemanticHash =
                    DynamicFlowP6CatalogCandidate.SemanticHash,
                catalogActivated =
                    DynamicFlowP6CatalogCandidate.ActivationEnabled,
                testingCandidateOverrideEnabled =
                    !DynamicFlowP6CatalogCandidate.ActivationEnabled,
                testingCandidateActivationThrough =
                    P612CandidateActivationThrough
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
                secretManifestDeleted,
                stopProtocol =
                    "Complete the T11 conflict trigger/ack protocol, write the non-secret browser result JSON, then create the stop file.",
                conflictControl = new
                {
                    journeyId = "T11",
                    triggerFile = Path.GetFullPath(conflictTriggerPath),
                    ackFile = Path.GetFullPath(conflictAckPath),
                    triggerSchemaVersion = 1,
                    triggerOperation = "CANCEL_SUPPLEMENTAL",
                    processed = conflictControl is not null,
                    outcome = conflictControl,
                    protocol =
                        "After capturing the displayed T11 instance and supplemental-step revisions, atomically write the exact trigger JSON. Wait for the ACK, submit the UI cancel with the captured stale revisions, assert 409/CAS conflict, then refresh and assert CANCELLED_BY_GATEWAY with the cancel control gone."
                }
            },
            handshake = new
            {
                runKey,
                runNonce,
                readyManifestSha256 = redactedManifestSha256,
                browserResultMustRepeatRunKeyAndNonce = true,
                staleResultRejected = true
            },
            browserEvidenceContract = new
            {
                schemaVersion = P612BrowserEvidenceContract.SchemaVersion,
                strict = true,
                runRoot = Path.GetFullPath(runRoot),
                runKey,
                nonce = runNonce,
                readyManifestSha256 = redactedManifestSha256,
                productionFrontendOrigin = origin,
                productionApiBaseUrl =
                    $"{backend.BaseUri.GetLeftPart(UriPartial.Authority)}/api",
                productionFrontendBuildId = options.FrontendBuildId,
                productionFrontendSourceRevision =
                    options.FrontendSourceRevision,
                verdict = "PASS",
                networkMockCount = 0,
                retryJourneyId = "T11",
                conflictJourneyId = "T11",
                journeys = evidenceJourneys.Select(row => new
                {
                    row.JourneyId,
                    row.ObservedId,
                    row.Actor,
                    row.ExactUrl,
                    row.RequiredTestIds,
                    row.StateSemantics
                }).ToArray(),
                actorCoverage,
                ui = new
                {
                    desktop = true,
                    mobile = true,
                    keyboard = true,
                    focus = true,
                    aria = true,
                    responsive = true
                },
                telemetry = new
                {
                    unexpectedConsoleErrorCount = 0,
                    unexpectedNetworkErrorCount = 0
                },
                artifacts = new
                {
                    screenshotCount = 24,
                    viewports = new[] { "desktop", "mobile" },
                    traceCountMinimum = 1,
                    allJourneyTraceCoverageRequired = true,
                    allPathsMustRemainUnderRunRoot = true,
                    exactSha256Required = true
                }
            },
            actors = new
            {
                owner = P612ManifestActor(actors.Owner, containsSecrets),
                coordinator =
                    P612ManifestActor(actors.Coordinator, containsSecrets),
                reporter =
                    P612ManifestActor(actors.Reporter, containsSecrets),
                reviewer =
                    P612ManifestActor(actors.Reviewer, containsSecrets),
                outsider =
                    P612ManifestActor(actors.Outsider, containsSecrets)
            },
            journeys = journeyManifest
        };
    }

    private static object P612ManifestActor(
        P612BrowserActor actor,
        bool containsSecrets)
        => new
        {
            actor.Username,
            actor.UserId,
            actor.Role,
            actor.Scope,
            password = containsSecrets ? actor.Password : "REDACTED"
        };

    private static IReadOnlyList<string> P612BrowserTestIds(
        P612BrowserJourney journey)
    {
        var ids = journey.JourneyId == "T10"
            ? new List<string> { "p5-runtime-entry" }
            : new List<string>
            {
                "p5-runtime-page",
                "p5-runtime-refresh"
            };
        switch (journey.JourneyId)
        {
            case "T05":
                ids.Add("p6-t05-join-all");
                ids.Add("p6-t05-missing-contributors");
                break;
            case "T06":
                ids.Add("p6-t06-join-quorum");
                ids.Add("p6-t06-quorum-progress");
                ids.Add("p6-t06-cancelled-contributors");
                ids.Add("p6-t06-late-contributors");
                break;
            case "T07":
                ids.Add("p6-t07-conditional-decision");
                break;
            case "T08":
                ids.Add("p6-t08-review-cycle");
                break;
            case "T09":
                ids.Add("p6-t09-child-lineage");
                ids.Add("p6-t09-open-child");
                break;
            case "T10":
                ids.Add($"p6-periodic-schedule-{journey.ScheduleId}");
                break;
            case "T11":
                ids.Add("p6-t11-supplemental-identity");
                ids.Add("p6-t11-add-required");
                ids.Add("p6-t11-add-optional");
                ids.Add("p6-t11-cancel");
                break;
            case "T12":
                ids.Add("p6-t12-epoch-controls");
                ids.Add("p6-t12-epoch-1");
                ids.Add("p6-t12-epoch-2");
                break;
        }
        return ids;
    }

    private static P612BrowserEvidenceExpectations
        BuildP612BrowserEvidenceExpectations(
            string runRoot,
            string runKey,
            string runNonce,
            string? redactedManifestSha256,
            P612BrowserFixtureOptions options,
            BackendServerLease backend,
            P612BrowserActors actors,
            IReadOnlyList<P612BrowserJourney> journeys)
    {
        Require(
            !string.IsNullOrWhiteSpace(redactedManifestSha256),
            "P6-12 browser evidence requires the immutable READY manifest SHA-256.");
        Require(
            !string.IsNullOrWhiteSpace(options.FrontendBuildId),
            "P6-12 browser evidence requires --frontend-build-id.");
        Require(
            !string.IsNullOrWhiteSpace(options.FrontendSourceRevision),
            "P6-12 browser evidence requires --frontend-source-revision.");
        var origin =
            options.FrontendOrigin.GetLeftPart(UriPartial.Authority);
        return new P612BrowserEvidenceExpectations
        {
            RunRoot = Path.GetFullPath(runRoot),
            RunKey = runKey,
            Nonce = runNonce,
            ReadyManifestSha256 = redactedManifestSha256!,
            ProductionFrontendOrigin = origin,
            ProductionApiBaseUrl =
                $"{backend.BaseUri.GetLeftPart(UriPartial.Authority)}/api",
            ProductionFrontendBuildId = options.FrontendBuildId!,
            ProductionFrontendSourceRevision =
                options.FrontendSourceRevision!,
            RetryJourneyId = "T11",
            ConflictJourneyId = "T11",
            Journeys = P612ExpectedJourneyEvidenceRows(origin, journeys),
            ActorCoverage = P612ExpectedActorCoverageRows(),
            ForbiddenSecrets = actors.All
                .Select(actor => actor.Password)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static IReadOnlyList<P612ExpectedJourneyEvidence>
        P612ExpectedJourneyEvidenceRows(
            string frontendOrigin,
            IReadOnlyList<P612BrowserJourney> journeys)
        => journeys
            .OrderBy(row => row.JourneyId, StringComparer.Ordinal)
            .Select(row => new P612ExpectedJourneyEvidence(
                JourneyId: row.JourneyId,
                ObservedId: P612ObservedId(row),
                Actor: P612ExpectedActor(row),
                ExactUrl: P612ExactJourneyUrl(frontendOrigin, row),
                RequiredTestIds: P612BrowserTestIds(row),
                StateSemantics: P612StateSemantics(row)))
            .ToArray();

    private static IReadOnlyList<P612ExpectedActorCoverage>
        P612ExpectedActorCoverageRows()
        =>
        [
            new("owner", ["T01", "T04", "T10", "T11", "T12"]),
            new(
                "coordinator",
                ["T05", "T06", "T07", "T09"]),
            new("reporter", ["T02", "T03"]),
            new("reviewer", ["T08"]),
            new("outsider", ["T03", "T12"])
        ];

    private static string P612ExactJourneyUrl(
        string frontendOrigin,
        P612BrowserJourney journey)
        => journey.JourneyId == "T10"
            ? $"{frontendOrigin}/works/{journey.WorkId}?tab=ASSIGN"
            : $"{frontendOrigin}/works/{journey.WorkId}/flow-instances/{journey.InstanceId}/overview";

    private static string P612ObservedId(P612BrowserJourney journey)
        => journey.JourneyId == "T10"
            ? journey.ScheduleId ??
              throw new InvalidOperationException(
                  "P6-12 T10 schedule ID is missing.")
            : journey.InstanceId ??
              throw new InvalidOperationException(
                  $"P6-12 {journey.JourneyId} instance ID is missing.");

    private static IReadOnlyList<string> P612StateSemantics(
        P612BrowserJourney journey)
    {
        var semantics = journey.ExpectedState
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}")
            .ToList();
        semantics.AddRange(
            journey.JourneyId switch
            {
                "T01" or "T02" or "T03" or "T04" =>
                [
                    $"stateText={journey.ExpectedState["instanceState"]}",
                    "stateIcon=active",
                    "ariaRole=status"
                ],
                "T05" =>
                [
                    "stateText=COLLECTING",
                    "stateIcon=pending",
                    "ariaRole=status"
                ],
                "T06" =>
                [
                    "stateText=SATISFIED",
                    "stateIcon=completed",
                    "secondaryStateText=CANCELLED_BY_GATEWAY|LATE_IGNORED",
                    "secondaryIcons=partial|reconciled",
                    "ariaRole=status"
                ],
                "T07" =>
                [
                    "stateText=SATISFIED",
                    "stateIcon=completed",
                    "ariaRole=status"
                ],
                "T08" =>
                [
                    "stateText=SUBMITTED",
                    "stateIcon=active",
                    "ariaRole=status"
                ],
                "T09" =>
                [
                    "stateText=WAITING_CHILD",
                    "stateIcon=active",
                    "ariaRole=status"
                ],
                "T10" =>
                [
                    "stateText=ACTIVE",
                    "stateIcon=active",
                    "ariaRole=status"
                ],
                "T11" =>
                [
                    "stateText=ASSIGNED",
                    "stateIcon=active",
                    "ariaRole=status"
                ],
                "T12" =>
                [
                    "stateText=ROLLED_BACK|ACTIVE",
                    "stateIcon=partial|active",
                    "ariaRole=status"
                ],
                _ => throw new InvalidOperationException(
                    $"Unexpected P6-12 journey '{journey.JourneyId}'.")
            });
        var duplicate = semantics
            .GroupBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"P6-12 {journey.JourneyId} state semantic '{duplicate.Key}' is duplicated.");
        }
        return semantics;
    }

    private static async Task<object> RunP612DirectMongoOracleAsync(
        IMongoDatabase database,
        string redactedPath,
        P612BrowserActors actors,
        IReadOnlyList<P612BrowserJourney> journeys,
        P612BrowserEvidenceValidationResult? browserEvidence,
        P612ConflictControlResult? conflictControl,
        string runKey,
        string runNonce,
        string? redactedManifestSha256,
        string? browserResultSha256,
        CancellationToken ct)
    {
        var instanceRows = new List<object>();
        var instances = database.GetCollection<DynamicFlowInstance>(
            "dynamic_flow_instances");
        var steps = database.GetCollection<DynamicFlowStepInstance>(
            "dynamic_flow_step_instances");
        var assignments = database.GetCollection<WorkAssignment>(
            "work_assignments");
        var versions = database.GetCollection<DynamicFlowTemplateVersion>(
            "dynamic_flow_template_versions");
        var receipts =
            database.GetCollection<DynamicFlowRuntimeCommandReceipt>(
                "dynamic_flow_runtime_command_receipts");
        var events = database.GetCollection<DynamicFlowRuntimeEvent>(
            "dynamic_flow_runtime_events");
        foreach (var journey in journeys.Where(row => row.InstanceId != null))
        {
            var instance = await instances
                .Find(row =>
                    row.Id == journey.InstanceId &&
                    !row.IsDeleted)
                .SingleAsync(ct);
            Require(
                instance.WorkId == journey.WorkId &&
                instance.FlowTemplateVersionId == journey.VersionId &&
                instance.ArchetypeId == journey.ArchetypeId &&
                instance.LaunchCommandId == journey.CommandId,
                $"P6-12 {journey.JourneyId} direct-Mongo identity drifted.");
            var version = await versions
                .Find(row =>
                    row.Id == journey.VersionId &&
                    !row.IsDeleted)
                .SingleAsync(ct);
            Require(
                instance.FlowPayloadHash == version.PayloadHash &&
                instance.TopologySnapshotHash == version.PayloadHash &&
                instance.CatalogVersion == version.CatalogVersion &&
                instance.CatalogSemanticHash ==
                version.CatalogSemanticHash,
                $"P6-12 {journey.JourneyId} immutable definition pins drifted.");
            if (int.Parse(journey.JourneyId[1..]) >= 3)
            {
                Require(
                    version.CatalogVersion ==
                    DynamicFlowP6CatalogCandidate.Version &&
                    version.CatalogSemanticHash ==
                    DynamicFlowP6CatalogCandidate.SemanticHash,
                    $"P6-12 {journey.JourneyId} is not pinned to the exact P6 candidate catalog.");
            }
            Require(
                journey.ExpectedState.TryGetValue(
                    "instanceState",
                    out var expectedInstanceState) &&
                instance.State == expectedInstanceState,
                $"P6-12 {journey.JourneyId} instance state did not match the exported expectation.");
            var journeySteps = await steps
                .Find(row =>
                    row.FlowInstanceId == instance.Id &&
                    !row.IsDeleted)
                .ToListAsync(ct);
            Require(
                journeySteps.Count > 0,
                $"P6-12 {journey.JourneyId} has no materialized step.");
            var assignmentIds = journeySteps
                .Where(row => row.AssignmentId != null)
                .Select(row => row.AssignmentId!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var assignmentCount = assignmentIds.Length == 0
                ? 0
                : await assignments.CountDocumentsAsync(
                    row =>
                        assignmentIds.Contains(row.Id) &&
                        !row.IsDeleted,
                    cancellationToken: ct);
            Require(
                assignmentCount == assignmentIds.Length,
                $"P6-12 {journey.JourneyId} has an orphan step assignment.");
            var launchReceiptCount = await receipts.CountDocumentsAsync(
                row =>
                    row.CommandId == journey.CommandId &&
                    row.FlowInstanceId == instance.Id &&
                    row.Status ==
                    DynamicFlowRuntimeCommandStatuses.Succeeded,
                cancellationToken: ct);
            var eventCount = await events.CountDocumentsAsync(
                row => row.FlowInstanceId == instance.Id,
                cancellationToken: ct);
            Require(
                launchReceiptCount == 1 && eventCount > 0,
                $"P6-12 {journey.JourneyId} launch receipt/event ledger drifted.");
            var topologyState =
                await AssertP612TopologySpecificStateAsync(
                    database,
                    journey,
                    instance,
                    journeySteps,
                    conflictControl,
                    ct);
            instanceRows.Add(new
            {
                journey.JourneyId,
                journey.ArchetypeId,
                journey.WorkId,
                journey.VersionId,
                journey.InstanceId,
                instance.State,
                instance.Revision,
                version.PayloadHash,
                version.CatalogVersion,
                version.CatalogSemanticHash,
                stepCount = journeySteps.Count,
                assignmentCount,
                launchReceiptCount,
                eventCount,
                topologyState,
                expectedState = journey.ExpectedState
            });
        }

        var t10 = journeys.Single(row => row.JourneyId == "T10");
        var schedule = await database
            .GetCollection<DynamicFlowPeriodicSchedule>(
                "dynamic_flow_periodic_schedules")
            .Find(row => row.Id == t10.ScheduleId && !row.IsDeleted)
            .SingleAsync(ct);
        var t10Version = await versions
            .Find(row => row.Id == t10.VersionId && !row.IsDeleted)
            .SingleAsync(ct);
        Require(
            schedule.WorkId == t10.WorkId &&
            schedule.FlowTemplateVersionId == t10.VersionId &&
            schedule.TargetUnitIds.SequenceEqual(t10.TargetUnitIds) &&
            schedule.FlowPayloadHash == t10Version.PayloadHash &&
            schedule.TopologySnapshotHash == t10Version.PayloadHash &&
            schedule.CatalogVersion ==
            DynamicFlowP6CatalogCandidate.Version &&
            schedule.CatalogSemanticHash ==
            DynamicFlowP6CatalogCandidate.SemanticHash &&
            schedule.ScheduleIdentityJson ==
            t10.RelatedIds["scheduleIdentityJson"] &&
            schedule.ScheduleIdentityHash ==
            t10.RelatedIds["scheduleIdentityHash"] &&
            schedule.State == t10.ExpectedState["scheduleState"],
            "P6-12 T10 direct-Mongo schedule identity drifted.");

        var scopedInstanceIds = journeys
            .Where(row => row.InstanceId != null)
            .Select(row => row.InstanceId!)
            .Append(journeys.Single(row => row.JourneyId == "T09")
                .RelatedIds["childInstanceId"])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var scopedSteps = await steps
            .Find(row =>
                scopedInstanceIds.Contains(row.FlowInstanceId) &&
                !row.IsDeleted)
            .ToListAsync(ct);
        var scopedAssignmentIds = scopedSteps
            .Where(row => row.AssignmentId != null)
            .Select(row => row.AssignmentId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var scopedAssignmentCount = await assignments.CountDocumentsAsync(
            row =>
                scopedAssignmentIds.Contains(row.Id) &&
                !row.IsDeleted,
            cancellationToken: ct);
        var scopedOutbox = await database
            .GetCollection<DynamicFlowRuntimeOutboxItem>(
                "dynamic_flow_runtime_outbox")
            .Find(row => scopedInstanceIds.Contains(row.FlowInstanceId))
            .ToListAsync(ct);
        var scopedStepIds = scopedSteps
            .Select(row => row.Id)
            .ToHashSet(StringComparer.Ordinal);
        Require(
            scopedAssignmentCount == scopedAssignmentIds.Length &&
            scopedSteps.All(row =>
                scopedInstanceIds.Contains(row.FlowInstanceId)) &&
            scopedOutbox.All(row =>
                scopedInstanceIds.Contains(row.FlowInstanceId) &&
                scopedStepIds.Contains(row.StepInstanceId)),
            "P6-12 scoped orphan scan found a step/assignment/outbox drift.");
        var duplicateLaunchReceiptCount = 0L;
        foreach (var journey in journeys.Where(row => row.InstanceId != null))
        {
            var count = await receipts.CountDocumentsAsync(
                row =>
                    row.CommandId == journey.CommandId &&
                    row.FlowInstanceId == journey.InstanceId,
                cancellationToken: ct);
            duplicateLaunchReceiptCount += Math.Max(0, count - 1);
        }
        Require(
            duplicateLaunchReceiptCount == 0,
            "P6-12 duplicate launch receipt scan found a duplicate.");

        var primaryWorkIds = journeys.Select(row => row.WorkId).ToArray();
        var ownerRoleCount = await database
            .GetCollection<DocRole>("doc_roles")
            .CountDocumentsAsync(
                row =>
                    row.DocType == DocType.WORK &&
                    primaryWorkIds.Contains(row.DocId) &&
                    row.UserId == actors.Owner.UserId &&
                    row.Role == DocRoleType.OWNER &&
                    !row.IsDeleted,
                cancellationToken: ct);
        var coordinatorRoleCount = await database
            .GetCollection<DocRole>("doc_roles")
            .CountDocumentsAsync(
                row =>
                    row.DocType == DocType.WORK &&
                    primaryWorkIds.Contains(row.DocId) &&
                    row.UserId == actors.Coordinator.UserId &&
                    row.Role == DocRoleType.LEADER_DIRECTIVE &&
                    !row.IsDeleted,
                cancellationToken: ct);
        var outsiderRoleCount = await database
            .GetCollection<DocRole>("doc_roles")
            .CountDocumentsAsync(
                row =>
                    primaryWorkIds.Contains(row.DocId) &&
                    row.UserId == actors.Outsider.UserId &&
                    !row.IsDeleted,
                cancellationToken: ct);
        Require(
            ownerRoleCount == 12 &&
            coordinatorRoleCount == 12 &&
            outsiderRoleCount == 0,
            "P6-12 actor work-role oracle drifted.");

        var redacted = await File.ReadAllTextAsync(redactedPath, ct);
        Require(
            actors.All.All(actor =>
                !redacted.Contains(
                    actor.Password,
                    StringComparison.Ordinal)),
            "P6-12 redacted manifest leaked an actor password.");

        var observedIds = browserEvidence is null
            ? null
            : journeys.ToDictionary(
                row => row.JourneyId,
                P612ObservedId,
                StringComparer.Ordinal);
        return new
        {
            verdict = browserEvidence is null ? "READY" : "PASS",
            browserVerdict = browserEvidence is null
                ? "NOT_RUN_READINESS_ONLY"
                : "PASS",
            handshake = new
            {
                runKey,
                runNonce,
                readyManifestSha256 = redactedManifestSha256,
                browserResultSha256
            },
            networkMockCount = browserEvidence is null
                ? (int?)null
                : 0,
            activation = new
            {
                catalogVersion = DynamicFlowP6CatalogCandidate.Version,
                catalogSemanticHash =
                    DynamicFlowP6CatalogCandidate.SemanticHash,
                catalogActivated =
                    DynamicFlowP6CatalogCandidate.ActivationEnabled,
                testingCandidateOverrideEnabled =
                    !DynamicFlowP6CatalogCandidate.ActivationEnabled,
                testingCandidateActivationThrough =
                    P612CandidateActivationThrough
            },
            exactJourneySet = journeys.Select(row => row.JourneyId).ToArray(),
            instances = instanceRows,
            periodic = new
            {
                journeyId = "T10",
                t10.WorkId,
                t10.VersionId,
                t10.ScheduleId,
                schedule.State,
                schedule.Revision
            },
            scopedIntegrity = new
            {
                instanceCount = scopedInstanceIds.Length,
                stepCount = scopedSteps.Count,
                assignmentCount = scopedAssignmentCount,
                outboxCount = scopedOutbox.Count,
                orphanCount = 0,
                duplicateLaunchReceiptCount
            },
            actorAcl = new
            {
                ownerRoleCount,
                coordinatorRoleCount,
                outsiderRoleCount,
                redactedManifestPasswordLeakCount = 0
            },
            browserObservedIds = observedIds,
            browserEvidence = browserEvidence is null
                ? null
                : new
                {
                    browserEvidence.ResultPath,
                    browserEvidence.ResultSha256,
                    browserEvidence.JourneyCount,
                    browserEvidence.ActorCount,
                    browserEvidence.ScreenshotCount,
                    browserEvidence.TraceCount,
                    artifactCount =
                        browserEvidence.ArtifactSha256ByFullPath.Count
                },
            conflictControl
        };
    }

    private static async Task<object> AssertP612TopologySpecificStateAsync(
        IMongoDatabase database,
        P612BrowserJourney journey,
        DynamicFlowInstance instance,
        IReadOnlyList<DynamicFlowStepInstance> journeySteps,
        P612ConflictControlResult? conflictControl,
        CancellationToken ct)
    {
        switch (journey.JourneyId)
        {
            case "T05":
            {
                var gateway = await LoadP603GatewayAsync(
                    database,
                    instance.Id,
                    ct);
                Require(
                    gateway.GatewayKind == DynamicFlowGatewayKinds.JoinAll &&
                    gateway.State == DynamicFlowGatewayStates.Collecting &&
                    gateway.ExpectedContributionIds.Count == 2 &&
                    gateway.ArrivedContributionIds.Count == 0 &&
                    gateway.CancelledContributionIds.Count == 0 &&
                    gateway.LateContributionIds.Count == 0,
                    "P6-12 T05 JOIN_ALL collecting oracle drifted.");
                return new
                {
                    gateway.GatewayKind,
                    gateway.State,
                    expectedCount = gateway.ExpectedContributionIds.Count,
                    arrivedCount = gateway.ArrivedContributionIds.Count,
                    missingCount = 2
                };
            }
            case "T06":
            {
                var gateway = await LoadP603GatewayAsync(
                    database,
                    instance.Id,
                    ct);
                Require(
                    gateway.GatewayKind == DynamicFlowGatewayKinds.JoinAny &&
                    gateway.State == DynamicFlowGatewayStates.Satisfied &&
                    gateway.ArrivedContributionIds.Count == 1 &&
                    gateway.CancelledContributionIds.Count == 1 &&
                    gateway.LateContributionIds.Count == 1 &&
                    gateway.WinnerContributionId ==
                    journey.RelatedIds["winnerContributionId"] &&
                    gateway.LateContributionIds.Single() ==
                    journey.RelatedIds["lateContributionId"],
                    "P6-12 T06 quorum/cancelled/late oracle drifted.");
                return new
                {
                    gateway.GatewayKind,
                    gateway.State,
                    arrivedCount = 1,
                    cancelledCount = 1,
                    lateCount = 1,
                    gateway.WinnerContributionId
                };
            }
            case "T07":
            {
                var gateway = await LoadP603GatewayAsync(
                    database,
                    instance.Id,
                    ct);
                Require(
                    gateway.GatewayKind ==
                    DynamicFlowGatewayKinds.Condition &&
                    gateway.State ==
                    DynamicFlowGatewayStates.Satisfied &&
                    gateway.SelectedEdgeId ==
                    journey.RelatedIds["selectedEdgeId"] &&
                    gateway.DecisionReasonCode == "RULE_MATCHED" &&
                    !string.IsNullOrWhiteSpace(
                        gateway.InputSnapshotHash) &&
                    !string.IsNullOrWhiteSpace(
                        gateway.EvaluatorVersion),
                    "P6-12 T07 typed decision oracle drifted.");
                return new
                {
                    gateway.GatewayKind,
                    gateway.State,
                    gateway.SelectedEdgeId,
                    gateway.DecisionReasonCode,
                    gateway.InputSnapshotHash,
                    gateway.EvaluatorVersion,
                    outsiderNegativeNoWrite = true
                };
            }
            case "T08":
            {
                var reviewStep = journeySteps.Single(
                    row => row.Id ==
                           journey.RelatedIds["reviewStepInstanceId"]);
                var reportId = journey.RelatedIds["reviewReportId"];
                var report = await database
                    .GetCollection<WorkAssignmentReport>(
                        "work_assignment_report")
                    .Find(row => row.Id == reportId && !row.IsDeleted)
                    .SingleAsync(ct);
                Require(
                    reviewStep.State ==
                    DynamicFlowStepStates.Submitted &&
                    reviewStep.ReportId == reportId &&
                    reviewStep.ReviewCycleNo == 1 &&
                    report.WorkAssignmentId ==
                    reviewStep.AssignmentId,
                    "P6-12 T08 submitted review-cycle oracle drifted.");
                return new
                {
                    reviewStepId = reviewStep.Id,
                    reviewStep.State,
                    reviewStep.ReviewCycleNo,
                    reportId,
                    report.PayloadRevision,
                    report.LifecycleRevision
                };
            }
            case "T09":
            {
                var parentStep = journeySteps.Single(
                    row => row.Id ==
                           journey.RelatedIds["parentStepInstanceId"]);
                var child = await database
                    .GetCollection<DynamicFlowInstance>(
                        "dynamic_flow_instances")
                    .Find(row =>
                        row.Id == journey.RelatedIds["childInstanceId"] &&
                        !row.IsDeleted)
                    .SingleAsync(ct);
                Require(
                    parentStep.State ==
                    DynamicFlowStepStates.WaitingChild &&
                    parentStep.ChildInstanceId == child.Id &&
                    child.ParentInstanceId == instance.Id &&
                    child.ParentStepInstanceId == parentStep.Id &&
                    child.FlowTemplateVersionId ==
                    journey.RelatedIds["childVersionId"] &&
                    child.State == DynamicFlowInstanceStates.Active,
                    "P6-12 T09 parent/child lineage oracle drifted.");
                return new
                {
                    parentStepId = parentStep.Id,
                    parentStepState = parentStep.State,
                    childInstanceId = child.Id,
                    childState = child.State,
                    child.ParentInstanceId,
                    child.ParentStepInstanceId,
                    child.FlowTemplateVersionId
                };
            }
            case "T11":
            {
                var supplemental = journeySteps.Single(
                    row => row.Id ==
                           journey.RelatedIds[
                               "supplementalStepInstanceId"]);
                var addEventCount = await database
                    .GetCollection<DynamicFlowRuntimeEvent>(
                        "dynamic_flow_runtime_events")
                    .CountDocumentsAsync(
                        row =>
                            row.FlowInstanceId == instance.Id &&
                            row.StepInstanceId == supplemental.Id &&
                            row.EventType ==
                            DynamicFlowSupplementalTopologyContract
                                .AddedEvent,
                        cancellationToken: ct);
                var cancelEventCount = await database
                    .GetCollection<DynamicFlowRuntimeEvent>(
                        "dynamic_flow_runtime_events")
                    .CountDocumentsAsync(
                        row =>
                            row.FlowInstanceId == instance.Id &&
                            row.StepInstanceId == supplemental.Id &&
                            row.EventType ==
                            DynamicFlowSupplementalTopologyContract
                                .CancelledEvent,
                        cancellationToken: ct);
                var cancelReceiptCount = conflictControl is null
                    ? await database
                        .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                            "dynamic_flow_runtime_command_receipts")
                        .CountDocumentsAsync(
                            row =>
                                row.FlowInstanceId == instance.Id &&
                                row.CommandType ==
                                DynamicFlowSupplementalTopologyContract
                                    .CancelCommand,
                            cancellationToken: ct)
                    : await database
                        .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                            "dynamic_flow_runtime_command_receipts")
                        .CountDocumentsAsync(
                            row =>
                                row.FlowInstanceId == instance.Id &&
                                row.CommandType ==
                                DynamicFlowSupplementalTopologyContract
                                    .CancelCommand &&
                                row.CommandId ==
                                conflictControl.CommandId,
                            cancellationToken: ct);
                var expectedSupplementalState =
                    conflictControl is null
                        ? DynamicFlowStepStates.Assigned
                        : DynamicFlowStepStates.CancelledByGateway;
                Require(
                    supplemental.IsSupplemental &&
                    supplemental.CompletionRequired &&
                    supplemental.State == expectedSupplementalState &&
                    supplemental.AssignmentId ==
                    journey.RelatedIds[
                        "supplementalAssignmentId"] &&
                    addEventCount == 1 &&
                    cancelEventCount ==
                    (conflictControl is null ? 0 : 1) &&
                    cancelReceiptCount ==
                    (conflictControl is null ? 0 : 1) &&
                    (conflictControl is null ||
                     (supplemental.SupplementalCancelledByUserId ==
                      conflictControl.ActorUserId &&
                      supplemental.Revision ==
                      conflictControl.ResultingStepRevision &&
                      instance.Revision ==
                      conflictControl.ResultingInstanceRevision)),
                    "P6-12 T11 supplemental oracle drifted.");
                return new
                {
                    supplementalStepId = supplemental.Id,
                    supplemental.State,
                    supplemental.CompletionRequired,
                    supplemental.AssignmentId,
                    addEventCount,
                    cancelEventCount,
                    cancelReceiptCount,
                    conflictControl
                };
            }
            case "T12":
            {
                var epochs = await database
                    .GetCollection<DynamicFlowExecutionEpoch>(
                        "dynamic_flow_execution_epochs")
                    .Find(row =>
                        row.FlowInstanceId == instance.Id &&
                        !row.IsDeleted)
                    .SortBy(row => row.ExecutionEpoch)
                    .ToListAsync(ct);
                var epochOne = epochs.Single(
                    row => row.ExecutionEpoch == 1);
                var epochTwo = epochs.Single(
                    row => row.ExecutionEpoch == 2);
                Require(
                    epochs.Count == 2 &&
                    epochOne.State ==
                    DynamicFlowExecutionEpochStates.RolledBack &&
                    !epochOne.IsCanonical &&
                    epochOne.ReplacedByExecutionEpoch == 2 &&
                    epochTwo.State ==
                    DynamicFlowExecutionEpochStates.Active &&
                    epochTwo.IsCanonical &&
                    instance.ExecutionEpoch == 2,
                    "P6-12 T12 execution-epoch oracle drifted.");
                return new
                {
                    executionEpoch = instance.ExecutionEpoch,
                    epochCount = epochs.Count,
                    epochOne = new
                    {
                        epochOne.State,
                        epochOne.IsCanonical,
                        epochOne.ReplacedByExecutionEpoch
                    },
                    epochTwo = new
                    {
                        epochTwo.State,
                        epochTwo.IsCanonical
                    }
                };
            }
            default:
                return new
                {
                    instance.State,
                    instance.ExecutionEpoch,
                    stepStates = journeySteps
                        .Select(row => row.State)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray()
                };
        }
    }

    private static P612BrowserJourney NewP612Journey(
        string journeyId,
        string archetypeId,
        string workId,
        string versionId,
        IReadOnlyList<string> targetUnitIds,
        IReadOnlyDictionary<string, string> relatedIds)
        => new(
            journeyId,
            archetypeId,
            workId,
            versionId,
            targetUnitIds,
            $"p6-12-browser-{journeyId.ToLowerInvariant()}",
            P612PeriodKey(journeyId),
            InstanceId: null,
            ScheduleId: null,
            relatedIds,
            ExpectedState: new Dictionary<string, string>(
                StringComparer.Ordinal),
            BrowserActions: Array.Empty<string>());

    private static string P612PeriodKey(string journeyId)
        => P601PeriodKey;

    private static async Task<string> P612FileSha256Async(
        string path,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static IReadOnlyList<string> ScanP612ArtifactsForSecrets(
        string root,
        IReadOnlyList<string> secrets)
    {
        var textExtensions = new HashSet<string>(
            [
                ".csv",
                ".har",
                ".htm",
                ".html",
                ".json",
                ".log",
                ".md",
                ".txt",
                ".xml",
                ".yaml",
                ".yml"
            ],
            StringComparer.OrdinalIgnoreCase);
        var nonEmptySecrets = secrets
            .Where(secret => !string.IsNullOrEmpty(secret))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (nonEmptySecrets.Length == 0)
            return Array.Empty<string>();

        var leaks = new List<string>();
        foreach (var path in Directory
                     .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!textExtensions.Contains(Path.GetExtension(path)))
                continue;
            var text = File.ReadAllText(path);
            if (nonEmptySecrets.Any(secret =>
                    text.Contains(secret, StringComparison.Ordinal)))
            {
                leaks.Add(Path.GetRelativePath(root, path));
            }
        }
        return leaks;
    }

    private static void RequireP612ActivationMode(
        P612BrowserFixtureOptions options)
    {
        if (options.ExpectedActivation == "candidate")
        {
            Require(
                !DynamicFlowP6CatalogCandidate.ActivationEnabled,
                "P6-12 candidate mode requires the P6 catalog activation constant to remain false.");
            return;
        }
        Require(
            DynamicFlowP6CatalogCandidate.ActivationEnabled,
            "P6-12 final mode requires the published P6 catalog activation constant to be true.");
    }

    private static string P612ExpectedActor(P612BrowserJourney journey)
        => journey.JourneyId switch
        {
            "T02" or "T03" => "reporter",
            "T05" or "T06" or "T07" or "T09" =>
                "coordinator",
            "T08" => "reviewer",
            _ => "owner"
        };

    private static IReadOnlyDictionary<string, string> Related(
        params (string Key, string Value)[] values)
        => values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> MergeP612Related(
        IReadOnlyDictionary<string, string> current,
        params (string Key, string Value)[] values)
    {
        var merged = new Dictionary<string, string>(
            current,
            StringComparer.Ordinal);
        foreach (var (key, value) in values)
            merged[key] = value;
        return merged;
    }

    private sealed record P612BrowserJourney(
        string JourneyId,
        string ArchetypeId,
        string WorkId,
        string VersionId,
        IReadOnlyList<string> TargetUnitIds,
        string CommandId,
        string PeriodKey,
        string? InstanceId,
        string? ScheduleId,
        IReadOnlyDictionary<string, string> RelatedIds,
        IReadOnlyDictionary<string, string> ExpectedState,
        IReadOnlyList<string> BrowserActions);

    private sealed record P612ConflictTrigger(
        string RunKey,
        string Nonce,
        string JourneyId,
        string Operation,
        string InstanceId,
        string SupplementalStepId,
        long ExpectedInstanceRevision,
        long ExpectedStepRevision);

    private sealed record P612ConflictControlResult(
        string JourneyId,
        string CommandId,
        string ActorUserId,
        string InstanceId,
        string SupplementalStepId,
        long PriorInstanceRevision,
        long PriorStepRevision,
        long ResultingInstanceRevision,
        long ResultingStepRevision,
        string ResultingStepState,
        string EventId);

    private sealed record P612ControlWaitResult(
        string StopReason,
        P612ConflictControlResult? Conflict);
}

internal sealed record P612BrowserActor(
    string Username,
    string UserId,
    string Password,
    string Role,
    string Scope);

internal sealed record P612BrowserActors(
    P612BrowserActor Owner,
    P612BrowserActor Coordinator,
    P612BrowserActor Reporter,
    P612BrowserActor Reviewer,
    P612BrowserActor Outsider)
{
    public IReadOnlyList<P612BrowserActor> All =>
        [Owner, Coordinator, Reporter, Reviewer, Outsider];
}

internal sealed record P612BrowserFixtureOptions(
    Uri FrontendOrigin,
    TimeSpan Timeout,
    string? RunKey,
    bool ReadinessOnly,
    string ExpectedActivation,
    string? FrontendBuildId,
    string? FrontendSourceRevision)
{
    private const int DefaultTimeoutSeconds = 1_800;

    public static P612BrowserFixtureOptions Parse(string[] args)
    {
        string? frontendOrigin = null;
        string? runKey = null;
        var timeoutSeconds = DefaultTimeoutSeconds;
        var readinessOnly = false;
        string? expectedActivation = null;
        string? frontendBuildId = null;
        string? frontendSourceRevision = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(
                    args[index],
                    "--p6-browser-fixture",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
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
                         "--p6-browser-fixture-timeout-seconds",
                         StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Length &&
                     int.TryParse(args[++index], out var parsedTimeout))
            {
                timeoutSeconds = parsedTimeout;
            }
            else if (string.Equals(
                         args[index],
                         "--p6-browser-fixture-run-key",
                         StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Length)
            {
                runKey = args[++index];
            }
            else if (string.Equals(
                         args[index],
                         "--p6-browser-fixture-readiness-only",
                         StringComparison.OrdinalIgnoreCase))
            {
                readinessOnly = true;
            }
            else if (string.Equals(
                         args[index],
                         "--expected-activation",
                         StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Length)
            {
                expectedActivation = args[++index].Trim().ToLowerInvariant();
            }
            else if (string.Equals(
                         args[index],
                         "--frontend-build-id",
                         StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Length)
            {
                frontendBuildId = args[++index].Trim();
            }
            else if (string.Equals(
                         args[index],
                         "--frontend-source-revision",
                         StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Length)
            {
                frontendSourceRevision = args[++index].Trim();
            }
            else
            {
                throw new ArgumentException(
                    $"Unknown or incomplete P6 browser fixture argument: {args[index]}");
            }
        }

        if (!Uri.TryCreate(
                frontendOrigin,
                UriKind.Absolute,
                out var parsedOrigin) ||
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
                "P6 browser fixture timeout must be between 1 and 3600 seconds.");
        }
        if (!string.IsNullOrWhiteSpace(runKey) &&
            runKey.Any(ch =>
                !(char.IsLetterOrDigit(ch) || ch is '_' or '-')))
        {
            throw new ArgumentException(
                "P6 browser fixture run key may contain only letters, digits, '_' and '-'.");
        }
        if (expectedActivation is not ("candidate" or "final"))
        {
            throw new ArgumentException(
                "--expected-activation must be exactly 'candidate' or 'final'.");
        }
        if (!readinessOnly)
        {
            if (string.IsNullOrWhiteSpace(frontendBuildId) ||
                frontendBuildId.Any(ch =>
                    !(char.IsLetterOrDigit(ch) ||
                      ch is '_' or '-' or '.' or ':')))
            {
                throw new ArgumentException(
                    "--frontend-build-id is required in browser mode and may contain only letters, digits, '_', '-', '.', and ':'.");
            }
            if (frontendSourceRevision is null ||
                frontendSourceRevision.Length != 64 ||
                frontendSourceRevision.Any(ch =>
                    ch is not (>= '0' and <= '9') and
                        not (>= 'a' and <= 'f')))
            {
                throw new ArgumentException(
                    "--frontend-source-revision is required in browser mode and must be a lowercase 64-character SHA-256.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(frontendBuildId) ||
                 !string.IsNullOrWhiteSpace(frontendSourceRevision))
        {
            throw new ArgumentException(
                "Readiness-only mode does not accept production frontend build metadata.");
        }
        return new P612BrowserFixtureOptions(
            new Uri(
                parsedOrigin.GetLeftPart(UriPartial.Authority),
                UriKind.Absolute),
            TimeSpan.FromSeconds(timeoutSeconds),
            string.IsNullOrWhiteSpace(runKey) ? null : runKey,
            readinessOnly,
            expectedActivation,
            frontendBuildId,
            frontendSourceRevision);
    }
}
