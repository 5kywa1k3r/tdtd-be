using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;

using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Options;
using tdtd_be.Services;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Owns only the isolated production stack and prerequisite identities for
/// P11-04. Form/Flow/launch/report/review and result transitions are deliberately absent
/// and must be driven by the browser through mounted production owners.
/// </summary>
internal static class P11ResultBrowserFixture
{
    internal const string CommandLineSwitch = "--p11-result-browser-fixture";
    private const string PromptId = "P11-04";
    private const string ScheduleIdentityJson =
        "{\"anchor\":\"2026-08-21\",\"cadence\":\"once\",\"timezone\":\"Asia/Ho_Chi_Minh\"}";

    public static async Task<int> RunAsync(string[] args)
    {
        var runKey = RequiredOption(args, "--run-key");
        var chainId = RequiredOption(args, "--chain-id");
        var frontendOrigin = new Uri(RequiredOption(args, "--frontend-origin"));
        var timeoutSeconds = IntOption(args, "--timeout-seconds", 1800, 30, 3600);
        var paths = HarnessPaths.CreateP11(runKey, chainId, PromptId);
        var continuationPaths = P11ContinuationSnapshot.RequiredRestorePaths(args, paths);
        var iterationRoot = paths.IterationRoot(1);
        var secretPath = Path.Combine(iterationRoot, "P11-04.fixture.runtime.secret.json");
        var redactedPath = Path.Combine(iterationRoot, "P11-04.fixture.json");
        var browserResultPath = Path.Combine(iterationRoot, "P11-04.browser-result.json");
        var oraclePath = Path.Combine(iterationRoot, "P11-04.direct-mongo.json");
        var cleanupPath = Path.Combine(iterationRoot, "P11-04.cleanup.json");
        var predecessorReadyPath = Path.Combine(iterationRoot, "P11-04.predecessor-ready.json");
        var reviewersReadyPath = Path.Combine(iterationRoot, "P11-04.reviewers-ready.json");
        var cleanupErrors = new List<string>();
        var fixtureErrors = new List<P11ResultCloseoutContract.FixtureFailure>();
        P11ResultCloseoutContract.OraclePrerequisites? oraclePrerequisites = null;
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        P11ContinuationSnapshot.SnapshotResult? continuation = null;
        P11CleanupInventory.Evidence? cleanupInventory = null;
        P11CleanupInventory.SecondDryRunEvidence? secondDryRun = null;
        var verdict = "FAIL";
        var databaseDropped = false;

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            mongo = await MongoReplicaSetLease.StartP11Async(
                paths, iterationRoot, runKey, 1, cancellation.Token);
            var database = mongo.Client.GetDatabase(mongo.DatabaseName);
            continuation = await P11ContinuationSnapshot.RestoreAsync(
                database,
                continuationPaths,
                chainId,
                cancellation.Token);
            var runtimeActorPassword =
                $"P11!{Convert.ToHexString(RandomNumberGenerator.GetBytes(18))}a";
            var prerequisite = await PrepareRestoredPrerequisitesAsync(
                database,
                continuation,
                runtimeActorPassword,
                cancellation.Token);
            var admin = prerequisite.Admin;
            var rootUnit = prerequisite.RootUnit;
            var jwtSigningKey = Convert.ToBase64String(
                RandomNumberGenerator.GetBytes(48));
            var actualApiOwnerToken = new JwtService(
                    Microsoft.Extensions.Options.Options.Create(new JwtOptions
                    {
                        Issuer = "tdtd-p1-integration",
                        Audience = "tdtd-p1-integration-client",
                        Key = jwtSigningKey,
                        AccessTokenMinutes = 30,
                        RefreshTokenDays = 1
                    }))
                .CreateAccessToken(
                    admin,
                    0L,
                    rootUnit.UnitTypeCodes,
                    rootUnit.Symbol ?? string.Empty,
                    rootUnit.FullName ?? string.Empty,
                    rootUnit.Code ?? string.Empty)
                .token;
            backend = await BackendServerLease.StartAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                cancellation.Token,
                new BackendServerOptions
                {
                    ActorPasswordOverride = runtimeActorPassword,
                    JwtSigningKey = jwtSigningKey,
                    P10ActualApiOwnerServiceAuthorizationParameter = actualApiOwnerToken,
                    EnableDynamicFlowRuntimeCandidate = true,
                    EnableDynamicFlowP7MappingCandidate = true,
                    HangfireServerEnabled = true,
                    HangfireRecurringRegistrationEnabled = true,
                    FrontendOrigin = frontendOrigin.GetLeftPart(UriPartial.Authority),
                    SuppressTestingFixedUtcNow = true
                });

            var adminPassword = runtimeActorPassword;
            P11StatisticLabelFixture primaryStatisticLabel;
            P11StatisticLabelFixture pagingStatisticLabel;
            using (var api = new ApiHarnessClient(backend.BaseUri))
            {
                var adminToken = await api.LoginAsync(
                    admin.Username,
                    adminPassword,
                    cancellation.Token);
                if (string.IsNullOrWhiteSpace(adminToken))
                    throw new InvalidOperationException("P11-04 restored owner login returned no token.");

                var runDigest = Sha256(runKey)[..12];
                primaryStatisticLabel = await CreateStatisticLabelConfigAsync(
                    api,
                    database,
                    adminToken,
                    runDigest,
                    "primary",
                    "#335C99",
                    cancellation.Token);
                pagingStatisticLabel = await CreateStatisticLabelConfigAsync(
                    api,
                    database,
                    adminToken,
                    runDigest,
                    "paging",
                    "#6B4E9B",
                    cancellation.Token);
                if (string.Equals(
                        primaryStatisticLabel.Id,
                        pagingStatisticLabel.Id,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        primaryStatisticLabel.Code,
                        pagingStatisticLabel.Code,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "P11-04 statistic label prerequisites are not distinct.");
                }
            }

            var apiBaseUrl = $"{backend.BaseUri.GetLeftPart(UriPartial.Authority)}/api";
            var readyAtUtc = DateTime.UtcNow;
            var secretManifest = new
            {
                schemaVersion = "P11_RESULT_BROWSER_FIXTURE_SECRET_V1",
                promptId = PromptId,
                chainId,
                runKey,
                readyAtUtc,
                expiresAtUtc = readyAtUtc.AddSeconds(timeoutSeconds),
                frontendOrigin = frontendOrigin.GetLeftPart(UriPartial.Authority),
                backendBaseUrl = backend.BaseUri.GetLeftPart(UriPartial.Authority),
                apiBaseUrl,
                mongo = new
                {
                    mongo.DatabaseName,
                    mongo.ConnectionString,
                    mongo.ProcessId
                },
                backend = new { backend.ProcessId },
                continuity = new
                {
                    mode = "RESTORED_EXACT_P11_03",
                    sourcePromptId = "P11-03",
                    continuation.SnapshotSha256,
                    continuation.PayloadSha256,
                    continuation.IdentitySha256,
                    continuation.IdentitySetSha256,
                    continuation.CollectionCount,
                    continuation.DocumentCount,
                    continuation.CapturedAtUtc,
                    continuation.SourceRunKeySha256,
                    ids = continuation.Ids,
                    testedTransitionReplayAllowed = false,
                    credentialRebindScope = new[] { "owner", "reporter", "outsider" },
                    reviewerProvisioningKind = "PREREQUISITE_ACTORS_ONLY"
                },
                actors = new
                {
                    owner = new
                    {
                        userId = admin.Id,
                        username = admin.Username,
                        password = adminPassword,
                        unitId = admin.UnitId
                    },
                    reporter = new
                    {
                        userId = prerequisite.Reporter.Id,
                        username = prerequisite.Reporter.Username,
                        password = backend.ActorPassword,
                        unitId = prerequisite.Reporter.UnitId
                    },
                    outsider = new
                    {
                        userId = prerequisite.Outsider.Id,
                        username = prerequisite.Outsider.Username,
                        password = backend.ActorPassword,
                        unitId = prerequisite.Outsider.UnitId
                    },
                    reviewers = prerequisite.Reviewers.ToDictionary(
                        item => item.Key,
                        item => new
                        {
                            userId = item.User.Id,
                            username = item.User.Username,
                            password = backend.ActorPassword,
                            unitId = item.User.UnitId,
                            gate = item.Gate
                        })
                },
                prerequisites = new
                {
                    rootUnitId = rootUnit.Id,
                    targetUnitId = prerequisite.TargetUnit.Id,
                    outsiderUnitId = prerequisite.OutsiderUnit.Id,
                    workId = prerequisite.Work.Id,
                    workCode = prerequisite.Work.Code,
                    primaryStatisticLabel,
                    pagingStatisticLabel
                },
                protocol = new { browserResultPath, oraclePath, cleanupPath, predecessorReadyPath, reviewersReadyPath }
            };
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            };
            await File.WriteAllTextAsync(
                secretPath,
                JsonSerializer.Serialize(secretManifest, jsonOptions),
                new UTF8Encoding(false),
                cancellation.Token);
            await File.WriteAllTextAsync(
                redactedPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "P11_RESULT_BROWSER_FIXTURE_V1",
                    promptId = PromptId,
                    chainId,
                    runKey,
                    readyAtUtc,
                    frontendOrigin = frontendOrigin.GetLeftPart(UriPartial.Authority),
                    apiBaseUrl,
                    databaseNameSha256 = Sha256(mongo.DatabaseName),
                    continuity = new
                    {
                        mode = "RESTORED_EXACT_P11_03",
                        sourcePromptId = "P11-03",
                        continuation.SnapshotSha256,
                        continuation.PayloadSha256,
                        continuation.IdentitySha256,
                        continuation.IdentitySetSha256,
                        continuation.CollectionCount,
                        continuation.DocumentCount,
                        continuation.SourceRunKeySha256
                    },
                    actorAliases = new[] { "owner", "reporter", "outsider", "reviewerForm", "reviewerFlow", "reviewerAssignment", "reviewerMapping", "reviewerStatistics" },
                    prerequisiteKinds = new[] { "RESTORED_P11_03_LOGICAL_SNAPSHOT", "CREDENTIAL_REBIND", "REVIEWER_USERS", "STATISTIC_LABEL_CONFIGS" },
                    testedTransitionsSeeded = Array.Empty<string>(),
                    productionOwnersRequired = new[]
                    {
                        "FORM_UI", "FLOW_UI", "WORK_PREFLIGHT_CONFIRM_UI",
                        "REPORT_UI", "REVIEW_UI", "RUNTIME_FINALIZE_CASCADE",
                        "P8_STAT_CONFIG_UI", "P9_FOUNDATION_HANGFIRE_WORKER",
                        "P9_RESULT_EXPORT_UI", "P10_RECONCILIATION_CTA",
                        "P10_RECONCILIATION_HANGFIRE_WORKER",
                        "P10_INDEPENDENT_REVIEW_UI", "P10_RECHECK_SUPERSESSION_UI",
                        "P10_EVIDENCE_UI", "ADMIN_USER_PERMISSION_REMOVAL_UI"
                    }
                }, jsonOptions),
                new UTF8Encoding(false),
                cancellation.Token);

            Console.WriteLine($"P11_RESULT_FIXTURE_READY={secretPath}");
            await WaitForEitherFileAsync(
                predecessorReadyPath, browserResultPath, cancellation.Token);
            if (File.Exists(predecessorReadyPath))
            {
                var predecessorReady = JsonNode.Parse(
                    await File.ReadAllTextAsync(predecessorReadyPath, cancellation.Token))
                    ?? throw new InvalidOperationException("P11-04 predecessor-ready protocol is empty.");
                var assignmentId = RequiredNodeString(predecessorReady, "assignmentId");
                var readyWorkId = RequiredNodeString(predecessorReady, "workId");
                var readyReportId = RequiredNodeString(predecessorReady, "reportId");
                var readySnapshotSha256 = RequiredNodeString(predecessorReady, "snapshotSha256");
                var readyIdentitySha256 = RequiredNodeString(predecessorReady, "identitySha256");
                if (!string.Equals(assignmentId, continuation.Ids["assignmentId"], StringComparison.Ordinal) ||
                    !string.Equals(readyWorkId, continuation.Ids["workId"], StringComparison.Ordinal) ||
                    !string.Equals(readyReportId, continuation.Ids["reportId"], StringComparison.Ordinal) ||
                    !string.Equals(readySnapshotSha256, continuation.SnapshotSha256, StringComparison.Ordinal) ||
                    !string.Equals(readyIdentitySha256, continuation.IdentitySha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "P11-04 predecessor-ready handshake does not match the restored exact P11-03 snapshot.");
                }
                var reviewerIds = prerequisite.Reviewers
                    .Select(item => item.User.Id)
                    .ToArray();
                var reviewerGrant = await database.GetCollection<WorkAssignment>("work_assignments")
                    .UpdateOneAsync(
                        item => item.Id == assignmentId && !item.IsDeleted,
                        Builders<WorkAssignment>.Update.AddToSetEach(
                            item => item.LeaderWatcherUserIds,
                            reviewerIds),
                        cancellationToken: cancellation.Token);
                if (reviewerGrant.MatchedCount != 1)
                    throw new InvalidOperationException("P11-04 reviewer assignment grant target is missing.");
                await File.WriteAllTextAsync(
                    reviewersReadyPath,
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = "P11_REVIEWERS_READY_V1",
                        assignmentId,
                        workId = readyWorkId,
                        reportId = readyReportId,
                        reviewerCount = reviewerIds.Length,
                        reviewerIdentitySha256 = Sha256(string.Join("\n", reviewerIds)),
                        snapshotSha256 = continuation.SnapshotSha256,
                        identitySha256 = continuation.IdentitySha256,
                        purpose = "PREREQUISITE_ASSIGNMENT_ACL_ONLY",
                        businessTransitionsSeeded = Array.Empty<string>()
                    }, jsonOptions),
                    new UTF8Encoding(false),
                    cancellation.Token);
            }
            await WaitForFileAsync(browserResultPath, cancellation.Token);
            var browserResult = JsonNode.Parse(
                await File.ReadAllTextAsync(browserResultPath, cancellation.Token))
                ?? throw new InvalidOperationException("P11-04 browser result is empty.");
            var browserVerdict = browserResult["verdict"]?.GetValue<string>() ?? "FAIL";
            oraclePrerequisites = P11ResultCloseoutContract.AssessOraclePrerequisites(browserResult);
            JsonObject oracle;
            if (oraclePrerequisites.Ready)
            {
                // The successful path retains all existing direct-Mongo parity checks.
                oracle = await CaptureDirectMongoOracleAsync(
                    database, browserResult, paths.WorkspaceRoot, cancellation.Token);
            }
            else
            {
                oracle = P11ResultCloseoutContract.BlockedOracle(oraclePrerequisites);
                oracle["jobDiagnostic"] = await CaptureIncompleteJobDiagnosticAsync(
                    database, browserResult, cancellation.Token);
            }
            await File.WriteAllTextAsync(
                oraclePath,
                oracle.ToJsonString(jsonOptions),
                new UTF8Encoding(false),
                cancellation.Token);
            verdict = browserVerdict == "PASS" && oracle["verdict"]?.GetValue<string>() == "PASS"
                ? "PASS"
                : "FAIL";
        }
        catch (Exception error)
        {
            var sanitized = P11ResultCloseoutContract.SanitizeFixtureFailure(error);
            fixtureErrors.Add(sanitized);
            Console.Error.WriteLine(JsonSerializer.Serialize(sanitized));
        }
        finally
        {
            try
            {
                if (File.Exists(secretPath)) File.Delete(secretPath);
            }
            catch (Exception error)
            {
                cleanupErrors.Add($"secret-delete:{error.Message}");
            }
            if (backend is not null)
            {
                try { await backend.StopAsync(); }
                catch (Exception error) { cleanupErrors.Add($"backend-stop:{error.Message}"); }
                try { await backend.DisposeAsync(); }
                catch (Exception error) { cleanupErrors.Add($"backend-dispose:{error.Message}"); }
            }
            if (mongo is not null)
            {
                try
                {
                    var hangfirePrefix = $"p1hf_{new string(runKey
                        .Where(char.IsLetterOrDigit)
                        .Take(24)
                        .ToArray()).ToLowerInvariant()}";
                    var allowedJourneyNewCollections = new[]
                    {
                        $"{hangfirePrefix}.jobGraph",
                        $"{hangfirePrefix}.locks",
                        $"{hangfirePrefix}.migrationLock",
                        $"{hangfirePrefix}.notifications",
                        $"{hangfirePrefix}.schema",
                        $"{hangfirePrefix}.server",
                        $"{hangfirePrefix}.stateHistory",
                        "work_report_statistic_reconciliation_exports"
                    };
                    cleanupInventory = await P11CleanupInventory.CaptureAsync(
                        mongo,
                        paths,
                        iterationRoot,
                        continuation?.ManifestPath,
                        allowedJourneyNewCollections,
                        CancellationToken.None);
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"cleanup-inventory:{error.Message}");
                }
                try
                {
                    await P11ServerExportCleanup.CaptureAndDeleteAsync(
                        paths, mongo.Client.GetDatabase(mongo.DatabaseName),
                        Path.Combine(iterationRoot, "server-export-cleanup.json"),
                        backend is null || backend.StopVerified, CancellationToken.None);
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"server-export-cleanup:{error.Message}");
                }
                try
                {
                    await mongo.DropDatabaseGuardedAsync(CancellationToken.None);
                    databaseDropped = mongo.DatabaseDropVerified;
                }
                catch (Exception error) { cleanupErrors.Add($"database-drop:{error.Message}"); }
                try { await mongo.DisposeAsync(); }
                catch (Exception error) { cleanupErrors.Add($"mongo-stop:{error.Message}"); }
                try { mongo.RemoveDataDirectoryGuarded(); }
                catch (Exception error) { cleanupErrors.Add($"mongo-data-remove:{error.Message}"); }
            }
            if (backend is not null && mongo is not null)
            {
                try
                {
                    secondDryRun = await P11CleanupInventory.CaptureSecondDryRunAsync(
                        mongo,
                        backend,
                        paths,
                        iterationRoot,
                        secretPath,
                        CancellationToken.None);
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"second-dry-run:{error.Message}");
                }
            }

            var secretManifestDeleted = !File.Exists(secretPath);
            var backendStopped = backend is null || backend.StopVerified;
            var backendPortReleased = backend is null || backend.PortReleaseVerified;
            var mongoStopped = mongo is null || mongo.ProcessStopVerified;
            var mongoPortReleased = mongo is null || mongo.PortReleaseVerified;
            var mongoDataDirectoryRemoved =
                mongo is null || mongo.DataDirectoryRemovalVerified;
            var cleanupDryRunPassed = cleanupInventory?.DryRunPassed == true;
            var physicalDryRunPassed = cleanupInventory?.PhysicalDryRunPassed == true;
            var exactOwnedResourceApply = databaseDropped && mongoDataDirectoryRemoved;
            var unknownCollectionDelta = cleanupInventory?.UnknownCollectionDelta;
            var secondDryRunEmpty = secondDryRun?.SecondDryRunEmpty == true;
            var physicalCleanupPassed = cleanupErrors.Count == 0 &&
                                physicalDryRunPassed && exactOwnedResourceApply && secondDryRunEmpty &&
                                secretManifestDeleted && databaseDropped &&
                                backendStopped && backendPortReleased &&
                                mongoStopped && mongoPortReleased && mongoDataDirectoryRemoved;
            var cleanupPassed = fixtureErrors.Count == 0 && cleanupErrors.Count == 0 &&
                                cleanupDryRunPassed &&
                                exactOwnedResourceApply &&
                                unknownCollectionDelta == 0 &&
                                secondDryRunEmpty &&
                                secretManifestDeleted &&
                                databaseDropped &&
                                backendStopped &&
                                backendPortReleased &&
                                mongoStopped &&
                                mongoPortReleased &&
                                mongoDataDirectoryRemoved;
            var cleanup = new
            {
                schemaVersion = "P11_RESULT_CLEANUP_V1",
                promptId = PromptId,
                runKey,
                inventoryArtifact = "mongo-cleanup-inventory.json",
                inventoryCaptured = cleanupInventory is not null,
                dryRunPassed = cleanupDryRunPassed,
                physicalDryRunPassed,
                plannedCollectionCount = cleanupInventory?.PlannedCollectionCount,
                plannedCollectionSetSha256 = cleanupInventory?.PlannedCollectionSetSha256,
                continuationCollectionCount = cleanupInventory?.ContinuationCollectionCount,
                continuationCollectionSetSha256 = cleanupInventory?.ContinuationCollectionSetSha256,
                continuationCollectionSetMatches = cleanupInventory?.ContinuationCollectionSetMatches,
                continuationCollectionsPreserved = cleanupInventory?.ContinuationCollectionsPreserved,
                allowedJourneyNewCollections = cleanupInventory?.AllowedJourneyNewCollections,
                journeyOwnedNewCollections = cleanupInventory?.JourneyOwnedNewCollections,
                unknownJourneyCollections = cleanupInventory?.UnknownJourneyCollections,
                missingContinuationCollections = cleanupInventory?.MissingContinuationCollections,
                missingAllowedJourneyNewCollections = cleanupInventory?.MissingAllowedJourneyNewCollections,
                exactOwnedResourceApply,
                unknownCollectionDelta,
                secondDryRunArtifact = "cleanup-second-dry-run.json",
                secondDryRunCaptured = secondDryRun is not null,
                secondDryRunRemainingCount = secondDryRun?.RemainingOwnedResourceCount,
                secondDryRunResourceSetSha256 = secondDryRun?.ProbedOwnedResourceSetSha256,
                secondDryRunEmpty,
                secretManifestDeleted,
                databaseDropped,
                continuationRestored = continuation is not null,
                continuationSnapshotSha256 = continuation?.SnapshotSha256,
                continuationPayloadSha256 = continuation?.PayloadSha256,
                continuationIdentitySha256 = continuation?.IdentitySha256,
                backendStopped,
                backendPortReleased,
                mongoStopped,
                mongoPortReleased,
                mongoDataDirectoryRemoved,
                fixtureErrors,
                oraclePrerequisites,
                physicalCleanup = new
                {
                    status = physicalCleanupPassed ? "RELEASED" : "UNVERIFIED_OR_REMAINING",
                    passed = physicalCleanupPassed,
                    postCleanupRemainingCount = secondDryRun?.RemainingOwnedResourceCount,
                    journeyCompletenessEvaluatedSeparately = true
                },
                strictPassGate = new
                {
                    eligible = verdict == "PASS" && cleanupPassed && oraclePrerequisites?.Ready == true,
                    exactCaseAccounting = oraclePrerequisites?.CaseAccounting,
                    fullJourneyTopologyVerified = cleanupDryRunPassed && unknownCollectionDelta == 0,
                    requiresExact16Of16 = true
                },
                errors = cleanupErrors,
                verdict = cleanupPassed ? "PASS" : "FAIL"
            };
            await File.WriteAllTextAsync(
                cleanupPath,
                JsonSerializer.Serialize(cleanup, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true
                }),
                new UTF8Encoding(false));
            if (!cleanupPassed) verdict = "FAIL";
        }

        Console.WriteLine($"P11_RESULT_FIXTURE_VERDICT={verdict}");
        return verdict == "PASS" ? 0 : 1;
    }

    private static async Task<P11PrerequisiteFixture> PrepareRestoredPrerequisitesAsync(
        IMongoDatabase database,
        P11ContinuationSnapshot.SnapshotResult continuation,
        string actorPassword,
        CancellationToken ct)
    {
        var users = database.GetCollection<AppUser>("users");
        var activeUsers = await users.Find(item => !item.IsDeleted).ToListAsync(ct);
        AppUser RequireActor(Func<AppUser, bool> predicate, string label)
        {
            var matches = activeUsers.Where(predicate).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    $"P11-04 restored {label} actor count is {matches.Length}, expected 1.");
            return matches[0];
        }

        var admin = RequireActor(
            item => string.Equals(item.Username, "admin", StringComparison.Ordinal),
            "owner");
        var reporter = RequireActor(
            item => item.Username.StartsWith("p11_03_reporter_", StringComparison.Ordinal),
            "reporter");
        var outsider = RequireActor(
            item => item.Username.StartsWith("p11_03_outsider_", StringComparison.Ordinal),
            "outsider");
        if (string.IsNullOrWhiteSpace(admin.UnitId) ||
            string.IsNullOrWhiteSpace(reporter.UnitId) ||
            string.IsNullOrWhiteSpace(outsider.UnitId))
        {
            throw new InvalidOperationException("P11-04 restored actor unit identity is missing.");
        }

        var units = database.GetCollection<Unit>("units");
        var rootUnit = await units.Find(item => item.Id == admin.UnitId && !item.IsDeleted)
            .SingleAsync(ct);
        var targetUnit = await units.Find(item => item.Id == reporter.UnitId && !item.IsDeleted)
            .SingleAsync(ct);
        var outsiderUnit = await units.Find(item => item.Id == outsider.UnitId && !item.IsDeleted)
            .SingleAsync(ct);
        if (targetUnit.Id == outsiderUnit.Id)
            throw new InvalidOperationException("P11-04 restored reporter and outsider scopes are not isolated.");

        var exactWorkId = continuation.Ids["workId"];
        var work = await database.GetCollection<Work>("works")
            .Find(item => item.Id == exactWorkId && !item.IsDeleted)
            .SingleAsync(ct);
        if (!string.Equals(work.Owner?.UserId, admin.Id, StringComparison.Ordinal) ||
            !string.Equals(work.LeaderDirectiveUserId, admin.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("P11-04 restored Work owner identity is not the exact P11-03 owner.");
        }

        var hasher = new PasswordHasher<AppUser>();
        var restoredActors = new[] { admin, reporter, outsider };
        var credentialUpdates = restoredActors.Select(actor =>
        {
            actor.PasswordHash = hasher.HashPassword(actor, actorPassword);
            return new UpdateOneModel<AppUser>(
                Builders<AppUser>.Filter.Eq(item => item.Id, actor.Id),
                Builders<AppUser>.Update.Set(item => item.PasswordHash, actor.PasswordHash));
        }).ToArray();
        var credentialResult = await users.BulkWriteAsync(
            credentialUpdates,
            cancellationToken: ct);
        if (credentialResult.MatchedCount != restoredActors.Length)
            throw new InvalidOperationException("P11-04 restored actor credential rebind was incomplete.");

        var now = DateTime.UtcNow;
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
        var reviewerSeeds = new[]
        {
            (Key: "reviewerForm", Gate: "FORM"),
            (Key: "reviewerFlow", Gate: "FLOW"),
            (Key: "reviewerAssignment", Gate: "ASSIGNMENT"),
            (Key: "reviewerMapping", Gate: "MAPPING"),
            (Key: "reviewerStatistics", Gate: "STATISTICS")
        };
        var reviewers = reviewerSeeds.Select(seed =>
        {
            var user = NewReviewerActor(seed.Key, targetUnit, admin, nonce, now);
            user.Roles = ["MANAGER_LEVEL"];
            user.PasswordHash = hasher.HashPassword(user, actorPassword);
            return new P11ReviewerFixture(seed.Key, seed.Gate, user);
        }).ToArray();
        await users.InsertManyAsync(
            reviewers.Select(item => item.User),
            cancellationToken: ct);
        return new P11PrerequisiteFixture(
            admin,
            rootUnit,
            targetUnit,
            outsiderUnit,
            reporter,
            outsider,
            reviewers,
            work);
    }

    private static AppUser NewReviewerActor(
        string kind,
        Unit unit,
        AppUser admin,
        string nonce,
        DateTime now)
        => new()
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Username = $"p11_04_{kind}_{nonce}".ToLowerInvariant(),
            PasswordHash = "P11_RUNTIME_PASSWORD_PENDING",
            FullName = $"P11-04 {kind} {nonce}",
            UnitId = unit.Id,
            PositionCode = "SPECIALIST",
            Roles = [],
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = admin.Id,
            UpdatedByUserId = admin.Id,
            IsDeleted = false
        };

    private static async Task<P11StatisticLabelFixture> CreateStatisticLabelConfigAsync(
        ApiHarnessClient api,
        IMongoDatabase database,
        string adminToken,
        string runDigest,
        string kind,
        string color,
        CancellationToken ct)
    {
        if (kind is not ("primary" or "paging"))
            throw new ArgumentOutOfRangeException(nameof(kind));

        const string emptyLabelConfigHash =
            "74234e98afe7498fb5daf1f36ac2d78acc339464f950703b8c019892f982b90b";
        var code = $"p11.04.{kind}.{runDigest}";
        var name = $"P11-04 {kind} statistic {runDigest}";
        var response = await api.PostAsync(
            "api/labels/config",
            new JsonObject
            {
                ["commandId"] = $"p11-04-{runDigest}-{kind}-statistic-label",
                ["expectedRevision"] = 0,
                ["expectedConfigHash"] = emptyLabelConfigHash,
                ["payload"] = new JsonObject
                {
                    ["code"] = code,
                    ["name"] = name,
                    ["description"] = $"P11-04 {kind} statistic label prerequisite",
                    ["color"] = color,
                    ["groupCode"] = "p11",
                    ["usage"] = "STATISTIC",
                    ["dataType"] = "NUMBER",
                    ["valueSourceType"] = "NONE",
                    ["valueOptions"] = new JsonArray(),
                    ["valueSourceCatalogId"] = null,
                    ["scopeType"] = "GLOBAL",
                    ["scopeId"] = null,
                    ["isActive"] = true
                }
            },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P11-04 {kind} statistic label config");

        var ownerId = ApiHarnessClient.RequiredString(response.Json, "ownerId");
        var versionId = ApiHarnessClient.RequiredString(response.Json, "versionId");
        var versionNo = ApiHarnessClient.RequiredInt(response.Json, "versionNo");
        var configHash = ApiHarnessClient.RequiredString(response.Json, "configHash");
        var persisted = await database.GetCollection<LabelCatalogItem>("labels")
            .Find(item => item.Code == code && !item.IsDeleted)
            .SingleAsync(ct);
        var persistedVersion = persisted.VersionSnapshots.SingleOrDefault();
        var persistedContractValid =
            string.Equals(persisted.Id, ownerId, StringComparison.Ordinal) &&
            string.Equals(persisted.ConfigId, ownerId, StringComparison.Ordinal) &&
            string.Equals(persisted.VersionId, versionId, StringComparison.Ordinal) &&
            persisted.VersionNo == versionNo &&
            versionNo == 1 &&
            persisted.Revision == 1 &&
            string.Equals(persisted.ConfigHash, configHash, StringComparison.Ordinal) &&
            configHash.Length == 64 &&
            string.Equals(persisted.Name, name, StringComparison.Ordinal) &&
            string.Equals(persisted.Usage, LabelUsages.Statistic, StringComparison.Ordinal) &&
            string.Equals(persisted.DataType, LabelDataTypes.Number, StringComparison.Ordinal) &&
            string.Equals(persisted.ValueSourceType, LabelValueSourceTypes.None, StringComparison.Ordinal) &&
            persisted.ValueOptions.Count == 0 &&
            persisted.ValueSourceCatalogId is null &&
            string.Equals(persisted.ScopeType, LabelScopeTypes.Global, StringComparison.Ordinal) &&
            persisted.ScopeId is null &&
            persisted.IsActive &&
            persistedVersion is not null &&
            string.Equals(persistedVersion.LabelId, ownerId, StringComparison.Ordinal) &&
            string.Equals(persistedVersion.VersionId, versionId, StringComparison.Ordinal) &&
            persistedVersion.VersionNo == 1 &&
            persistedVersion.Revision == 1 &&
            string.Equals(persistedVersion.Status, "ACTIVE", StringComparison.Ordinal) &&
            string.Equals(persistedVersion.ConfigHash, configHash, StringComparison.Ordinal);
        if (!persistedContractValid)
        {
            throw new InvalidOperationException(
                $"P11-04 {kind} statistic label persisted contract is invalid.");
        }

        return new P11StatisticLabelFixture(
            persisted.Id,
            persisted.Code,
            persisted.Name,
            versionId,
            versionNo,
            configHash,
            persisted.Usage,
            persisted.DataType,
            persisted.IsActive);
    }


    private static async Task<JsonObject> CaptureIncompleteJobDiagnosticAsync(
        IMongoDatabase database, JsonNode browserResult, CancellationToken ct)
    {
        var identity = P11ResultCloseoutContract.BoundJobIds(browserResult);
        if (identity is null)
            return new JsonObject { ["status"] = "PREREQUISITE_ID_MISSING_OR_INVALID", ["queryExecuted"] = false };
        try
        {
            var (jobId, reportId, assignmentId) = identity.Value;
            // Exact indexed identity and predecessor scope; project only finite diagnostic metadata.
            // Never serialize the job document, claim token, actor, payload or exception message.
            var job = await database.GetCollection<WorkReportStatisticRebuildJob>("work_report_statistic_rebuild_jobs")
                .Find(value => value.Id == jobId && value.SourceReportId == reportId &&
                               value.WorkAssignmentId == assignmentId &&
                               value.RunKind == WorkReportStatisticRebuildJobRunKinds.Foundation)
                .Project(value => new
                {
                    value.Id, value.Status, value.StateRevision, value.RetryCount, value.DiagnosticCode,
                    value.NextRetryAtUtc, value.DeadlineAtUtc, value.LastHeartbeatAtUtc
                })
                .Limit(1)
                .FirstOrDefaultAsync(ct);
            if (job is null)
                return new JsonObject { ["status"] = "BOUND_JOB_NOT_FOUND", ["queryExecuted"] = true };
            var diagnostic = P11ResultCloseoutContract.ProjectJobDiagnostic(
                job.Id, job.Status, job.StateRevision, job.RetryCount, job.DiagnosticCode,
                job.NextRetryAtUtc, job.DeadlineAtUtc, job.LastHeartbeatAtUtc);
            var result = JsonSerializer.SerializeToNode(diagnostic,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            result["queryExecuted"] = true;
            return result;
        }
        catch (Exception error)
        {
            // Diagnostic capture cannot replace the already established missing-prerequisite outcome.
            return new JsonObject
            {
                ["status"] = "READ_FAILED", ["queryExecuted"] = true,
                ["failure"] = JsonSerializer.SerializeToNode(P11ResultCloseoutContract.SanitizeFixtureFailure(error),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
            };
        }
    }

    private static async Task<JsonObject> CaptureDirectMongoOracleAsync(
        IMongoDatabase database,
        JsonNode browserResult,
        string workspaceRoot,
        CancellationToken ct)
    {
        var ids = browserResult["ids"]
                  ?? throw new InvalidOperationException("P11-04 browser result lacks ids.");
        var formId = RequiredNodeString(ids, "formVersionId");
        var flowFamilyId = RequiredNodeString(ids, "flowFamilyId");
        var flowVersionId = RequiredNodeString(ids, "flowVersionId");
        var flowPayloadHash = RequiredNodeString(ids, "flowPayloadHash");
        var flowContributionPolicy = RequiredNodeString(ids, "flowContributionPolicy");
        var flowContributionPolicyHash = RequiredNodeString(ids, "flowContributionPolicyHash");
        var workId = RequiredNodeString(ids, "workId");
        var instanceId = RequiredNodeString(ids, "flowInstanceId");
        var stepId = RequiredNodeString(ids, "stepInstanceId");
        var assignmentId = RequiredNodeString(ids, "assignmentId");
        var reportId = RequiredNodeString(ids, "reportId");
        var launchCommandId = RequiredNodeString(ids, "launchCommandId");        var statConfigId = RequiredNodeString(ids, "statConfigId");
        var statConfigVersionId = RequiredNodeString(ids, "statConfigVersionId");
        var statConfigRevision = ids["statConfigRevision"]?.GetValue<long>()
            ?? throw new InvalidOperationException("P11-04 browser result lacks ids.statConfigRevision.");
        var statConfigHash = RequiredNodeString(ids, "statConfigHash");
        var statFoundationJobId = RequiredNodeString(ids, "statFoundationJobId");
        var statResultId = RequiredNodeString(ids, "statResultId");
        var p9RunId = RequiredNodeString(ids, "p9RunId");
        var initialStatResultId = RequiredNodeString(
            ids, "reconciliationInitialP9GenerationId");
        var initialP9RunId = RequiredNodeString(
            ids, "reconciliationInitialP9RunId");
        var initialP8ConfigHash = RequiredNodeString(
            ids, "reconciliationInitialP8ConfigHash");
        var statCsvExportId = RequiredNodeString(ids, "statCsvExportId");
        var statXlsxExportId = RequiredNodeString(ids, "statXlsxExportId");
        var reconciliationId = RequiredNodeString(ids, "reconciliationId");
        var initialActualGenerationId = RequiredNodeString(
            ids, "reconciliationInitialActualGenerationId");
        var initialVerdictGenerationId = RequiredNodeString(
            ids, "reconciliationInitialGenerationId");
        var staleActualGenerationId = RequiredNodeString(
            ids, "reconciliationStaleActualGenerationId");
        var oracleStaleIdentity = UsesAttempt040StaleVerdictOracle(browserResult);
        var staleVerdictGenerationId = oracleStaleIdentity ? null : RequiredNodeString(
            ids, "reconciliationStaleGenerationId");
        var currentActualGenerationId = RequiredNodeString(
            ids, "reconciliationActualGenerationId");
        var currentVerdictGenerationId = RequiredNodeString(
            ids, "reconciliationGenerationId");
        var staleRecheckMarkerId = RequiredNodeString(
            ids, "reconciliationStaleRecheckMarkerId");
        var recheckMarkerId = RequiredNodeString(ids, "reconciliationRecheckMarkerId");
        var evidenceJsonId = RequiredNodeString(ids, "evidenceJsonArtifactId");
        var evidenceCsvId = RequiredNodeString(ids, "evidenceCsvArtifactId");
        var removedReviewerId = RequiredNodeString(ids, "permissionRemovedReviewerId");

        var checks = new JsonArray();
        async Task<long> Count(string collection, FilterDefinition<BsonDocument> filter)
            => await database.GetCollection<BsonDocument>(collection)
                .CountDocumentsAsync(filter, cancellationToken: ct);
        void Check(string id, bool passed, object detail)
            => checks.Add(new JsonObject
            {
                ["id"] = id,
                ["status"] = passed ? "PASS" : "FAIL",
                ["detail"] = JsonSerializer.SerializeToNode(detail)
            });
        var f = Builders<BsonDocument>.Filter;
        var formCount = await Count("dynamic_form_templates", f.Eq("_id", ObjectId.Parse(formId)));
        var familyCount = await Count("dynamic_flow_templates", f.Eq("_id", ObjectId.Parse(flowFamilyId)));
        var versionCount = await Count("dynamic_flow_template_versions", f.Eq("_id", ObjectId.Parse(flowVersionId)));
        var flowVersion = await database.GetCollection<BsonDocument>("dynamic_flow_template_versions")
            .Find(f.Eq("_id", ObjectId.Parse(flowVersionId))).SingleAsync(ct);
        var instanceCount = await Count(
            "dynamic_flow_instances",
            f.Eq("_id", ObjectId.Parse(instanceId)) & f.Eq("workId", ObjectId.Parse(workId)));
        var instanceTupleCount = await Count(
            "dynamic_flow_instances",
            f.Eq("workId", ObjectId.Parse(workId)) &
            f.Eq("flowTemplateVersionId", ObjectId.Parse(flowVersionId)) &
            f.Eq("launchCommandId", launchCommandId));
        var stepCount = await Count(
            "dynamic_flow_step_instances",
            f.Eq("_id", ObjectId.Parse(stepId)) & f.Eq("flowInstanceId", ObjectId.Parse(instanceId)));
        var assignmentCount = await Count(
            "work_assignments",
            f.Eq("_id", ObjectId.Parse(assignmentId)) & f.Eq("workId", ObjectId.Parse(workId)));
        var reportCount = await Count(
            "work_assignment_report",
            f.Eq("_id", ObjectId.Parse(reportId)) & f.Eq("workAssignmentId", ObjectId.Parse(assignmentId)));
        var reportLogs = await Count(
            "work_assignment_report_logs",
            f.Eq("workAssignmentReportId", ObjectId.Parse(reportId)));
        var receiptCount = await Count(
            "dynamic_flow_runtime_command_receipts",
            f.Eq("commandId", launchCommandId));
        var outboxCount = await Count(
            "dynamic_flow_runtime_outbox",
            f.Eq("flowInstanceId", ObjectId.Parse(instanceId)));

        var report = await database.GetCollection<BsonDocument>("work_assignment_report")
            .Find(f.Eq("_id", ObjectId.Parse(reportId))).SingleAsync(ct);
        var step = await database.GetCollection<BsonDocument>("dynamic_flow_step_instances")
            .Find(f.Eq("_id", ObjectId.Parse(stepId))).SingleAsync(ct);
        var instance = await database.GetCollection<BsonDocument>("dynamic_flow_instances")
            .Find(f.Eq("_id", ObjectId.Parse(instanceId))).SingleAsync(ct);
        var formDocument = await database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
            .Find(value => value.Id == formId && !value.IsDeleted).SingleAsync(ct);
        var statJobs = database.GetCollection<WorkReportStatisticRebuildJob>(
            "work_report_statistic_rebuild_jobs");
        var foundationJob = await statJobs
            .Find(value => value.Id == statFoundationJobId && !value.IsDeleted)
            .SingleAsync(ct);
        var projectionJob = await statJobs
            .Find(value => value.Id == p9RunId && !value.IsDeleted)
            .SingleAsync(ct);
        var statExports = await database.GetCollection<StatRunExportArtifact>(
                "work_report_statistic_exports")
            .Find(value => (value.Id == statCsvExportId || value.Id == statXlsxExportId) &&
                           !value.IsDeleted)
            .ToListAsync(ct);
        var reconciliation = await database.GetCollection<StatisticReconciliationRun>(
                "work_report_statistic_reconciliations")
            .Find(value => value.Id == reconciliationId && !value.IsDeleted)
            .SingleAsync(ct);
        var reconciliationCurrent = oracleStaleIdentity
            ? RequireAttempt040ReconciliationCurrentView(browserResult, reconciliation)
            : reconciliation;
        var reviewRecords = await database.GetCollection<
                StatisticReconciliationIndependentReviewAuditRecord>(
                "work_report_statistic_reconciliation_reviews")
            .Find(P11IndependentAuditFilter(reconciliationId))
            .ToListAsync(ct);
        JsonObject? staleOracleIdentity = null;
        if (oracleStaleIdentity)
        {
            var lineage = await database.GetCollection<StatisticReconciliationReview>(
                    "work_report_statistic_reconciliation_reviews")
                .Find(value => value.ReconciliationId == reconciliationId &&
                    value.RecordKind == StatisticReconciliationReviewKinds.FinalVerdict)
                .Limit(4).ToListAsync(ct);
            var totalReviewRecordCount = await Count("work_report_statistic_reconciliation_reviews",
                f.Eq("reconciliationId", reconciliationId));
            staleOracleIdentity = ResolveAttempt040StaleVerdictIdentity(
                browserResult, reconciliation, lineage, totalReviewRecordCount);
            staleVerdictGenerationId = RequiredNodeString(staleOracleIdentity, "staleVerdictGenerationId");
        }
        var evidenceRecords = await database.GetCollection<
                StatisticReconciliationEvidenceExport>(
                "work_report_statistic_reconciliation_exports")
            .Find(value => value.Id == evidenceJsonId || value.Id == evidenceCsvId)
            .ToListAsync(ct);
        var removedReviewer = await database.GetCollection<AppUser>("users")
            .Find(value => value.Id == removedReviewerId).SingleAsync(ct);
        var downloadedFiles = ValidateDownloadedFiles(
            browserResult["downloads"] as JsonArray,
            workspaceRoot,
            statCsvExportId,
            statXlsxExportId);

        Check("MONGO-FORM", formCount == 1, new { formId, count = formCount });
        Check("MONGO-FLOW",
            familyCount == 1 && versionCount == 1 &&
            string.Equals(flowContributionPolicy, "INCLUDE", StringComparison.Ordinal) &&
            string.Equals(SafeBson(flowVersion, "payloadHash"), flowPayloadHash, StringComparison.Ordinal) &&
            string.Equals(SafeBson(flowVersion, "contributionPolicy"), flowContributionPolicy, StringComparison.Ordinal) &&
            string.Equals(SafeBson(flowVersion, "contributionPolicyHash"), flowContributionPolicyHash, StringComparison.Ordinal),
            new
            {
                flowFamilyId, familyCount, flowVersionId, versionCount,
                flowPayloadHash, flowContributionPolicy, flowContributionPolicyHash
            });
        Check("MONGO-INSTANCE-IDEMPOTENT", instanceCount == 1 && instanceTupleCount == 1,
            new { instanceId, instanceCount, instanceTupleCount });
        Check("MONGO-STEP-ASSIGNMENT", stepCount == 1 && assignmentCount == 1,
            new { stepId, stepCount, assignmentId, assignmentCount });
        Check("MONGO-REPORT", reportCount == 1,
            new { reportId, reportCount, status = SafeBson(report, "status") });
        Check("MONGO-REVIEW-TRANSITIONS", reportLogs >= 4,
            new { reportId, reportLogs });
        Check("MONGO-RUNTIME-EFFECTS", receiptCount == 1 && outboxCount >= 1,
            new { launchCommandId, receiptCount, outboxCount });
        Check("MONGO-FINALIZE-CASCADE",
            TerminalValue(SafeBson(report, "status"), "Approved", "2") &&
            TerminalValue(SafeBson(step, "state"), "APPROVED", "Approved", "COMPLETED", "Completed") &&
            TerminalValue(SafeBson(instance, "state"), "FINALIZED", "Finalized") &&
            SafeBson(instance, "finalizedExecutionEpoch") == "1" &&
            !TerminalValue(SafeBson(instance, "finalizedByEventId"), "MISSING", "NULL"),
            new
            {
                reportStatus = SafeBson(report, "status"),
                stepState = SafeBson(step, "state"),
                instanceState = SafeBson(instance, "state"),
                finalizedExecutionEpoch = SafeBson(instance, "finalizedExecutionEpoch"),
                finalizedByEventId = SafeBson(instance, "finalizedByEventId")
            });        Check("MONGO-P8-STAT-CONFIG",
            formDocument.StatisticConfigId == statConfigId &&
            formDocument.StatisticConfigVersionId == statConfigVersionId &&
            formDocument.StatisticConfigRevision == statConfigRevision &&
            string.Equals(formDocument.StatisticConfigStatus, "LOCKED",
                StringComparison.Ordinal) &&
            formDocument.StatisticConfigHash == statConfigHash,
            new
            {
                formId,
                configId = formDocument.StatisticConfigId,
                versionId = formDocument.StatisticConfigVersionId,
                revision = formDocument.StatisticConfigRevision,
                status = formDocument.StatisticConfigStatus,
                configHash = formDocument.StatisticConfigHash
            });
        Check("MONGO-P9-FOUNDATION-PROJECTION",
            foundationJob.RunKind == WorkReportStatisticRebuildJobRunKinds.Foundation &&
            foundationJob.Status == WorkReportStatisticRebuildJobStatuses.Completed &&
            foundationJob.FreshnessState == WorkReportStatisticRebuildJobFreshnessStates.Fresh &&
            foundationJob.GenerationId == statResultId &&
            foundationJob.SourceReportId == reportId &&
            foundationJob.WorkAssignmentId == assignmentId &&
            foundationJob.ConfigHash == statConfigHash &&
            projectionJob.RunKind == WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection &&
            projectionJob.Status == WorkReportStatisticRebuildJobStatuses.Completed &&
            projectionJob.IsCurrentPublication &&
            projectionJob.GenerationId == statResultId &&
            projectionJob.GenerationHash == foundationJob.GenerationHash,
            new
            {
                foundationJobId = foundationJob.Id,
                foundationJob.Status,
                foundationJob.FreshnessState,
                foundationJob.GenerationId,
                projectionRunId = projectionJob.Id,
                projectionJob.RunKind,
                projectionJob.IsCurrentPublication
            });
        var exportFormats = statExports.Select(value => value.Format)
            .Order(StringComparer.Ordinal).ToArray();
        Check("MONGO-P9-EXPORTS",
            statExports.Count == 2 &&
            exportFormats.SequenceEqual(new[] { StatRunExportFormats.Csv, StatRunExportFormats.Xlsx }) &&
            statExports.All(value => value.Status == StatRunExportStatuses.Completed &&
                                     value.ResultKind == StatRunExportResultKinds.DirectField &&
                                     value.ResultId == initialStatResultId &&
                                     value.ConfigHash == initialP8ConfigHash &&
                                     value.DownloadCount >= 1) &&
            downloadedFiles.P9CsvParsed && downloadedFiles.P9XlsxParsed &&
            downloadedFiles.P9HashesMatch && downloadedFiles.P9CountsMatch &&
            downloadedFiles.FormulaSafe,
            new
            {
                ids = statExports.Select(value => value.Id).Order(StringComparer.Ordinal).ToArray(),
                formats = exportFormats,
                rows = statExports.Select(value => value.RowCount).ToArray(),
                columns = statExports.Select(value => value.ColumnCount).ToArray(),
                downloadedFiles
            });
        var recheckReceipt = reconciliation.CurrentRecheckFinalizeReceipt;
        var recheckBeginMarkerIds = reconciliation.RecheckBeginReceipts
            .Select(value => value.MarkerId).ToArray();
        var exactRecheckHistory = recheckBeginMarkerIds.Length == 2 &&
            recheckBeginMarkerIds.Distinct(StringComparer.Ordinal).Count() == 2 &&
            recheckBeginMarkerIds.Contains(staleRecheckMarkerId, StringComparer.Ordinal) &&
            recheckBeginMarkerIds.Contains(recheckMarkerId, StringComparer.Ordinal);
        Check("MONGO-P10-RECONCILIATION",
            reconciliation.Status == StatisticReconciliationRunStatuses.Matched &&
            reconciliationCurrent.P9ResultKind == "DIRECT" &&
            reconciliationCurrent.P9RunId == p9RunId &&
            reconciliationCurrent.P9ResultId == p9RunId &&
            reconciliationCurrent.P9GenerationId == statResultId &&
            reconciliationCurrent.SourceReportId == reportId &&
            reconciliationCurrent.P8ConfigHash == statConfigHash &&
            reconciliation.CurrentGenerationId == currentActualGenerationId &&
            reconciliation.CurrentGenerationId != initialActualGenerationId &&
            reconciliation.CurrentGenerationId != staleActualGenerationId &&
            reconciliation.PendingGenerationId is null &&
            reconciliation.Recheck is null &&
            recheckReceipt is not null &&
            recheckReceipt.MarkerId == recheckMarkerId &&
            recheckReceipt.BaseActualGenerationId == staleActualGenerationId &&
            recheckReceipt.BaseVerdictGenerationId == staleVerdictGenerationId &&
            recheckReceipt.SuccessorActualGenerationId == currentActualGenerationId &&
            recheckReceipt.SuccessorVerdictGenerationId == currentVerdictGenerationId &&
            recheckReceipt.TerminalStatus == StatisticReconciliationRunStatuses.Matched &&
            exactRecheckHistory,
            new
            {
                reconciliationId,
                reconciliation.Status,
                reconciliationCurrent.P9RunId,
                reconciliationCurrent.P9ResultId,
                reconciliationCurrent.P9GenerationId,
                initialActualGenerationId,
                initialVerdictGenerationId,
                staleActualGenerationId,
                staleVerdictGenerationId,
                currentActualGenerationId = reconciliation.CurrentGenerationId,
                currentVerdictGenerationId,
                recheckMarkerId = recheckReceipt?.MarkerId,
                recheckReceipt?.TerminalStatus,
                recheckBeginMarkerIds,
                exactRecheckHistory
            });
        if (staleOracleIdentity is not null)
            checks.Add(new JsonObject { ["id"] = "MONGO-P10-STALE-VERDICT-IDENTITY",
                ["status"] = "PASS", ["detail"] = staleOracleIdentity });
        var initialDecisions = reviewRecords.Where(value =>
            value.RecordKind == StatisticReconciliationReviewRecordKinds.Decision &&
            value.GenerationId == initialVerdictGenerationId && value.Decision == "APPROVE").ToArray();
        var currentDecisions = reviewRecords.Where(value =>
            value.RecordKind == StatisticReconciliationReviewRecordKinds.Decision &&
            value.GenerationId == currentVerdictGenerationId && value.Decision == "APPROVE" &&
            value.Status == StatisticReconciliationReviewStatuses.Active).ToArray();
        var staleDecisions = reviewRecords.Where(value =>
            value.RecordKind == StatisticReconciliationReviewRecordKinds.Decision &&
            value.GenerationId == staleVerdictGenerationId).ToArray();
        var initialToStaleSupersessions = reviewRecords.Where(value =>
            value.RecordKind == StatisticReconciliationReviewRecordKinds.Supersession &&
            value.GenerationId == initialVerdictGenerationId &&
            value.SupersededByGenerationId == staleVerdictGenerationId &&
            value.Status == StatisticReconciliationReviewStatuses.Superseded).ToArray();
        var unexpectedFinalSupersessions = reviewRecords.Where(value =>
            value.RecordKind == StatisticReconciliationReviewRecordKinds.Supersession &&
            value.SupersededByGenerationId == currentVerdictGenerationId).ToArray();
        var initialDecisionIds = initialDecisions.Select(value => value.Id)
            .ToHashSet(StringComparer.Ordinal);
        Check("MONGO-P10-INDEPENDENT-REVIEWS",
            initialDecisions.Length == 5 && currentDecisions.Length == 5 &&
            staleDecisions.Length == 0 && initialToStaleSupersessions.Length == 5 &&
            unexpectedFinalSupersessions.Length == 0 && reviewRecords.Count == 15 &&
            initialToStaleSupersessions.All(value =>
                value.SupersedesDecisionId is not null &&
                initialDecisionIds.Contains(value.SupersedesDecisionId)) &&
            initialToStaleSupersessions.Select(value => value.Gate)
                .Distinct(StringComparer.Ordinal).Count() == 5 &&
            currentDecisions.Select(value => value.Gate).Distinct(StringComparer.Ordinal).Count() == 5 &&
            currentDecisions.Select(value => value.ReviewerActorId).Distinct(StringComparer.Ordinal).Count() == 5,
            new
            {
                initialDecisions = initialDecisions.Length,
                staleDecisions = staleDecisions.Length,
                initialToStaleSupersessions = initialToStaleSupersessions.Length,
                unexpectedFinalSupersessions = unexpectedFinalSupersessions.Length,
                currentDecisions = currentDecisions.Length,
                totalAuditRecords = reviewRecords.Count,
                gates = currentDecisions.Select(value => value.Gate).Order(StringComparer.Ordinal).ToArray(),
                distinctReviewers = currentDecisions.Select(value => value.ReviewerActorId)
                    .Distinct(StringComparer.Ordinal).Count()
            });
        var evidenceHashesValid = evidenceRecords.All(value =>
            Sha256(value.Content) == value.ContentSha256 &&
            Sha256(Encoding.UTF8.GetBytes(value.ManifestJson)) == value.ManifestSha256 &&
            value.Content.LongLength == value.ContentLength);
        Check("MONGO-P10-EVIDENCE-ACL",
            evidenceRecords.Count == 2 &&
            evidenceRecords.Select(value => value.Format).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { "CSV", "JSON" }) &&
            evidenceRecords.All(value => value.GenerationId == currentVerdictGenerationId &&
                                         value.DetailLevel == "REDACTED" &&
                                         value.CreatedByActorId == removedReviewerId) &&
            evidenceHashesValid && removedReviewer.IsDeleted,
            new
            {
                artifactIds = evidenceRecords.Select(value => value.Id).Order(StringComparer.Ordinal).ToArray(),
                generationId = currentVerdictGenerationId,
                formats = evidenceRecords.Select(value => value.Format).Order(StringComparer.Ordinal).ToArray(),
                detailLevels = evidenceRecords.Select(value => value.DetailLevel).Distinct().ToArray(),
                evidenceHashesValid,
                removedReviewerIdSha256 = Sha256(removedReviewerId),
                removedReviewer.IsDeleted
            });
        var passed = checks.All(node => node?["status"]?.GetValue<string>() == "PASS");
        return new JsonObject
        {
            ["schemaVersion"] = "P11_RESULT_DIRECT_MONGO_V1",
            ["promptId"] = PromptId,
            ["verdict"] = passed ? "PASS" : "FAIL",
            ["checks"] = checks,
            ["identitySha256"] = Sha256(string.Join("\n",
                formId, flowFamilyId, flowVersionId, flowPayloadHash,
                flowContributionPolicy, flowContributionPolicyHash, workId, instanceId,
                stepId, assignmentId, reportId, launchCommandId,
                statConfigId, statConfigVersionId, statFoundationJobId,
                statResultId, p9RunId, initialStatResultId, initialP9RunId,
                statCsvExportId, statXlsxExportId, reconciliationId,
                initialActualGenerationId, initialVerdictGenerationId,
                staleActualGenerationId, staleVerdictGenerationId,
                currentActualGenerationId, currentVerdictGenerationId,
                staleRecheckMarkerId, recheckMarkerId,
                evidenceJsonId, evidenceCsvId))
        };
    }

    private static FilterDefinition<StatisticReconciliationIndependentReviewAuditRecord>
        P11IndependentAuditFilter(string reconciliationId)
        => Builders<StatisticReconciliationIndependentReviewAuditRecord>.Filter.Eq(
                value => value.ReconciliationId, reconciliationId) &
           Builders<StatisticReconciliationIndependentReviewAuditRecord>.Filter.In(
                value => value.RecordKind, new[] {
                    StatisticReconciliationReviewRecordKinds.Decision,
                    StatisticReconciliationReviewRecordKinds.Supersession });

    private static bool UsesAttempt040StaleVerdictOracle(JsonNode browser)
    {
        var usesOracle = P11ResultCloseoutContract.UsesAttempt040StaleVerdictOracle(
            browser, out var invalidProtocol);
        if (invalidProtocol is not null)
            throw new InvalidOperationException("P11_ATTEMPT040_STALE_ORACLE_IDENTITY_SOURCE_INVALID");
        return usesOracle;
    }

    private static StatisticReconciliationRun RequireAttempt040ReconciliationCurrentView(
        JsonNode browser, StatisticReconciliationRun persisted)
    {
        if (!UsesAttempt040StaleVerdictOracle(browser))
            throw new InvalidOperationException("P11_ATTEMPT040_CURRENT_VIEW_VERSION_REQUIRED");
        var ids = browser["ids"]!;
        if (persisted.Id != RequiredNodeString(ids, "reconciliationId") ||
            persisted.P9RunId != RequiredNodeString(ids, "reconciliationInitialP9RunId") ||
            persisted.P9ResultId != persisted.P9RunId ||
            persisted.P9GenerationId != RequiredNodeString(ids, "reconciliationInitialP9GenerationId") ||
            persisted.P8ConfigHash != RequiredNodeString(ids, "reconciliationInitialP8ConfigHash") ||
            persisted.SourceReportId != RequiredNodeString(ids, "reportId") ||
            persisted.CurrentGenerationRecheckCaptureBinding is null)
            throw new InvalidOperationException("P11_ATTEMPT040_CURRENT_VIEW_CREATION_TUPLE_INVALID");
        // This canonical helper validates the capture binding and returns a BSON
        // clone. It must never rewrite the persisted immutable creation tuple.
        var current = StatisticReconciliationRecheckCaptureBindingCanonical.EffectiveCurrentRun(persisted);
        if (current.P9ResultKind != "DIRECT" ||
            current.P9RunId != RequiredNodeString(ids, "p9RunId") ||
            current.P9ResultId != current.P9RunId ||
            current.P9GenerationId != RequiredNodeString(ids, "statResultId") ||
            current.P8ConfigHash != RequiredNodeString(ids, "statConfigHash") ||
            current.SourceReportId != persisted.SourceReportId ||
            current.P9RunId == persisted.P9RunId || current.P9GenerationId == persisted.P9GenerationId)
            throw new InvalidOperationException("P11_ATTEMPT040_CURRENT_VIEW_SUCCESSOR_TUPLE_INVALID");
        return current;
    }

    // Read-only consumer of browser evidence and canonical persisted verdicts. The browser
    // never claims a verdict identity which the 409 review response did not expose.
    private static JsonObject ResolveAttempt040StaleVerdictIdentity(JsonNode browser,
        StatisticReconciliationRun run, IReadOnlyList<StatisticReconciliationReview> lineage,
        long totalReviewRecordCount)
    {
        static string? S(JsonNode? node, string key) => node?[key]?.GetValue<string>();
        static bool B(JsonNode? node, string key, bool expected) => node?[key]?.GetValue<bool>() == expected;
        static bool N(JsonNode? node, string key, long expected) => node?[key]?.GetValue<long>() == expected;
        static void Require(bool valid, string code)
        { if (!valid) throw new InvalidOperationException("P11_ATTEMPT040_STALE_ORACLE_" + code); }
        Require(UsesAttempt040StaleVerdictOracle(browser), "VERSION_REQUIRED");
        _ = RequireAttempt040ReconciliationCurrentView(browser, run);
        var ids = browser["ids"]!;
        var proof = browser["observations"]!["reconciliationStale"]!;
        var detail = proof["detail"]; var summary = proof["summary"];
        var recovery = proof["reviewRecovery"]; var recoverySummary = recovery?["summary"];
        var review = recovery?["review"]; var dom = recovery?["dom"]; var network = recovery?["network"];
        var reconciliationId = RequiredNodeString(ids, "reconciliationId");
        var initialActualGenerationId = RequiredNodeString(ids, "reconciliationInitialActualGenerationId");
        var initialVerdictGenerationId = RequiredNodeString(ids, "reconciliationInitialGenerationId");
        var staleActualGenerationId = RequiredNodeString(ids, "reconciliationStaleActualGenerationId");
        var currentActualGenerationId = RequiredNodeString(ids, "reconciliationActualGenerationId");
        var currentVerdictGenerationId = RequiredNodeString(ids, "reconciliationGenerationId");
        var revision = summary?["stateRevision"]?.GetValue<long>() ?? 0;
        var stateHash = S(summary, "stateHash");
        Require(run.Id == reconciliationId && revision > 0 &&
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(stateHash) &&
            S(detail, "status") == "STALE" && S(detail, "currentGenerationId") == staleActualGenerationId &&
            N(detail, "stateRevision", revision) && S(detail, "stateHash") == stateHash &&
            S(summary, "status") == "STALE" && B(summary, "hasCurrentGeneration", true) &&
            B(summary, "hasPendingGeneration", false) &&
            S(recovery, "schemaVersion") == "P11_REVIEW_TARGET_NOT_SIGNABLE_RECOVERY_V1" &&
            S(recovery, "mode") == "REVIEW_TARGET_NOT_SIGNABLE" &&
            N(recoverySummary, "httpStatus", 200) && S(recoverySummary, "status") == "STALE" &&
            N(recoverySummary, "stateRevision", revision) && S(recoverySummary, "stateHash") == stateHash &&
            B(recoverySummary, "hasCurrentGeneration", true) && B(recoverySummary, "hasPendingGeneration", false) &&
            B(recoverySummary, "baseEligible", true) && B(recoverySummary, "inProgress", false) &&
            N(review, "httpStatus", 409) && N(review, "problemStatus", 409) &&
            S(review, "code") == "P10_REVIEW_TARGET_NOT_SIGNABLE" &&
            S(review, "problemTitle") == "Independent review state conflict." &&
            N(dom, "articleCount", 1) && S(dom, "phase") == "REVIEW_RECOVERY" &&
            S(dom, "httpStatus") == "409" && S(dom, "reason") == "REVIEW_TARGET_NOT_SIGNABLE" &&
            S(dom, "reconciliationId") == reconciliationId &&
            S(dom, "stateRevision") == revision.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            S(dom, "stateHash") == stateHash && N(dom, "ctaCount", 1) && N(dom, "ctaTestIdCount", 1) && B(dom, "ctaVisible", true) &&
            B(dom, "ctaEnabled", true) && N(dom, "reviewActionsCount", 0) && N(dom, "reviewGateCount", 0) &&
            N(dom, "domainIdentitiesCount", 0) && N(dom, "presentationMetadataCount", 0) &&
            N(network, "summaryRequestCount", 1) && N(network, "summaryResponseCount", 1) &&
            N(network, "reviewRequestCount", 1) && N(network, "reviewResponseCount", 1) &&
            N(network, "detailRequestCount", 0) && N(network, "detailResponseCount", 0) &&
            N(network, "recheckRequestCount", 0) && N(network, "recheckResponseCount", 0) &&
            N(network, "unexpectedNegativeCount", 0) &&
            network?["requestStartSequence"]?.GetValue<long>() is >= 0 &&
            network?["requestEndSequence"]?.GetValue<long>() >= network?["requestStartSequence"]?.GetValue<long>() &&
            network?["responseStartSequence"]?.GetValue<long>() is >= 0 &&
            network?["responseEndSequence"]?.GetValue<long>() >= network?["responseStartSequence"]?.GetValue<long>(),
            "BROWSER_RECOVERY_PROOF_INVALID");
        Require(totalReviewRecordCount == 18 && lineage.Count == 3 && lineage.All(value => value.ReconciliationId == reconciliationId &&
            value.RecordKind == StatisticReconciliationReviewKinds.FinalVerdict), "CANONICAL_CARDINALITY_INVALID");
        foreach (var verdict in lineage) StatisticReconciliationFinalVerdictPublisher.ValidateStored(verdict);
        Require(lineage.Select(value => value.VerdictGenerationId).Distinct(StringComparer.Ordinal).Count() == 3 &&
            lineage.Select(value => value.ActualGenerationId).Distinct(StringComparer.Ordinal).Count() == 3,
            "CANONICAL_IDENTITIES_NOT_DISTINCT");
        var initial = lineage.SingleOrDefault(value => value.ActualGenerationId == initialActualGenerationId);
        var stale = lineage.SingleOrDefault(value => value.ActualGenerationId == staleActualGenerationId);
        var current = lineage.SingleOrDefault(value => value.ActualGenerationId == currentActualGenerationId);
        Require(initial is not null && stale is not null && current is not null, "ACTUAL_IDENTITY_MISSING");
        Require(initial!.VerdictGenerationId == initialVerdictGenerationId && initial.Verdict == "MATCHED" &&
            initial.CompleteEvidence && initial.SupersedesVerdictGenerationId is null &&
            initial.SupersedesVerdictGenerationSha256 is null &&
            stale!.Verdict == "FAILED" && stale.FailureKind == StatisticReconciliationFinalVerdictFailureKinds.None &&
            stale.RootCauseClass == "FRESHNESS" && stale.FailureEvidenceSha256 is null &&
            stale.ActualGenerationSha256 == S(detail, "currentGenerationHash") &&
            !stale.CompleteEvidence && !stale.AllRequiredLayersZero && !stale.MissingOrExtraIdentity &&
            !stale.UnknownBlocksCloseout && !stale.Signable && !stale.CloseoutAllowed &&
            stale.SupersedesVerdictGenerationId == initial.VerdictGenerationId &&
            stale.SupersedesVerdictGenerationSha256 == initial.VerdictGenerationSha256 &&
            current!.VerdictGenerationId == currentVerdictGenerationId && current.Verdict == "MATCHED" &&
            current.CompleteEvidence && current.SupersedesVerdictGenerationId == stale.VerdictGenerationId &&
            current.SupersedesVerdictGenerationSha256 == stale.VerdictGenerationSha256,
            "EXACT_INITIAL_STALE_CURRENT_LINEAGE_INVALID");
        var receipt = run.CurrentRecheckFinalizeReceipt;
        Require(run.Status == "MATCHED" && run.Recheck is null && run.PendingGenerationId is null &&
            run.PendingGenerationHash is null && run.CurrentGenerationId == current!.ActualGenerationId &&
            run.CurrentGenerationHash == current.ActualGenerationSha256 && receipt is not null &&
            StatisticReconciliationRunService.HasValidRecheckFinalizeReceipt(run) &&
            receipt.MarkerId == RequiredNodeString(ids, "reconciliationRecheckMarkerId") &&
            receipt.BaseActualGenerationId == stale!.ActualGenerationId &&
            receipt.BaseActualGenerationSha256 == stale.ActualGenerationSha256 &&
            receipt.BaseVerdictGenerationId == stale.VerdictGenerationId &&
            receipt.BaseVerdictGenerationSha256 == stale.VerdictGenerationSha256 &&
            receipt.SuccessorActualGenerationId == current.ActualGenerationId &&
            receipt.SuccessorActualGenerationSha256 == current.ActualGenerationSha256 &&
            receipt.SuccessorVerdictGenerationId == current.VerdictGenerationId &&
            receipt.SuccessorVerdictGenerationSha256 == current.VerdictGenerationSha256 &&
            receipt.TerminalStatus == "MATCHED", "FINAL_RECEIPT_INVALID");
        return new JsonObject { ["schemaVersion"] = "P11_ATTEMPT040_STALE_VERDICT_ORACLE_V1",
            ["identitySource"] = "TRUSTED_MONGO_ORACLE_BY_ACTUAL_GENERATION",
            ["initialActualGenerationId"] = initialActualGenerationId,
            ["initialVerdictGenerationId"] = initialVerdictGenerationId,
            ["staleActualGenerationId"] = staleActualGenerationId,
            ["staleVerdictGenerationId"] = stale!.VerdictGenerationId,
            ["currentActualGenerationId"] = currentActualGenerationId,
            ["currentVerdictGenerationId"] = currentVerdictGenerationId,
            ["canonicalVerdictCount"] = lineage.Count, ["receiptValidated"] = true };
    }

    internal static (JsonObject Csv, JsonObject Xlsx) SelectP9ResultDownloads(
        JsonArray downloads,
        string csvExportId,
        string xlsxExportId)
    {
        static JsonObject SelectExact(
            JsonArray candidates,
            string format,
            string exportId)
        {
            var matches = candidates
                .OfType<JsonObject>()
                .Where(node =>
                    string.Equals(
                        node["kind"]?.GetValue<string>(),
                        "P9_RESULT_EXPORT",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        node["format"]?.GetValue<string>(),
                        format,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        node["exportId"]?.GetValue<string>(),
                        exportId,
                        StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    $"Expected exactly one P9 {format} download for exportId {exportId}; " +
                    $"observed {matches.Length}.");
            return matches[0];
        }

        if (string.IsNullOrWhiteSpace(csvExportId) ||
            string.IsNullOrWhiteSpace(xlsxExportId) ||
            string.Equals(csvExportId, xlsxExportId, StringComparison.Ordinal))
            throw new InvalidOperationException("P9 CSV/XLSX export identities are invalid.");

        return (
            SelectExact(downloads, "CSV", csvExportId),
            SelectExact(downloads, "XLSX", xlsxExportId));
    }

    private static P11DownloadedFileValidation ValidateDownloadedFiles(
        JsonArray? downloads,
        string workspaceRoot,
        string csvExportId,
        string xlsxExportId)
    {
        try
        {
            if (downloads is null)
                throw new InvalidOperationException("Browser result has no downloads array.");

            var root = Path.GetFullPath(workspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootPrefix = root + Path.DirectorySeparatorChar;
            var p9 = SelectP9ResultDownloads(downloads, csvExportId, xlsxExportId);

            static string ResolveDownloadPath(
                JsonObject node,
                string root,
                string rootPrefix)
            {
                var relativePath = node["path"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Download path missing.");
                var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
                if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(fullPath))
                    throw new InvalidOperationException(
                        "Download path escaped workspace or is missing.");
                return fullPath;
            }

            static int RequiredInt(JsonObject node, string name)
                => node[name]?.GetValue<int>()
                   ?? throw new InvalidOperationException($"Download {name} missing.");

            static string RequiredString(JsonObject node, string name)
                => node[name]?.GetValue<string>()
                   ?? throw new InvalidOperationException($"Download {name} missing.");

            var csvNode = p9.Csv;
            var csvPath = ResolveDownloadPath(csvNode, root, rootPrefix);
            var csvBytes = File.ReadAllBytes(csvPath);
            var csvRows = ParseCsv(csvBytes);
            var csvParsed = csvRows.Count >= 2 &&
                            csvRows.All(row => row.Count == csvRows[0].Count);
            var csvFormulaSafe = csvRows.Skip(1)
                .SelectMany(row => row)
                .Where(value => !string.IsNullOrEmpty(value))
                .All(value => value[0] is not ('=' or '+' or '-' or '@'));
            var csvHash = Sha256(csvBytes);

            var xlsxNode = p9.Xlsx;
            var xlsxPath = ResolveDownloadPath(xlsxNode, root, rootPrefix);
            var xlsxBytes = File.ReadAllBytes(xlsxPath);
            int xlsxRows;
            int xlsxColumns;
            bool xlsxFormulaSafe;
            using (var workbook = new XLWorkbook(new MemoryStream(xlsxBytes)))
            {
                var result = workbook.Worksheet("Result");
                xlsxRows = Math.Max(0, result.LastRowUsed()?.RowNumber() - 1 ?? 0);
                xlsxColumns = result.LastColumnUsed()?.ColumnNumber() ?? 0;
                xlsxFormulaSafe = !result.CellsUsed().Any(cell => cell.HasFormula);
            }

            var xlsxHash = Sha256(xlsxBytes);
            var hashesMatch =
                csvHash == RequiredString(csvNode, "sha256") &&
                csvHash == RequiredString(csvNode, "contentHash") &&
                xlsxHash == RequiredString(xlsxNode, "sha256") &&
                xlsxHash == RequiredString(xlsxNode, "contentHash");
            var countsMatch =
                csvRows.Count - 1 == RequiredInt(csvNode, "rowCount") &&
                csvRows[0].Count == RequiredInt(csvNode, "columnCount") &&
                xlsxRows == RequiredInt(xlsxNode, "rowCount") &&
                xlsxColumns == RequiredInt(xlsxNode, "columnCount");
            return new P11DownloadedFileValidation(
                csvParsed,
                xlsxRows >= 1 && xlsxColumns >= 1,
                hashesMatch,
                countsMatch,
                csvFormulaSafe && xlsxFormulaSafe,
                null);
        }
        catch (Exception exception)
        {
            return new P11DownloadedFileValidation(
                false, false, false, false, false,
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static List<List<string>> ParseCsv(byte[] content)
    {
        var offset = content.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        var text = Encoding.UTF8.GetString(content, offset, content.Length - offset);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(character);
                }
                continue;
            }
            if (character == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (character == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = new List<string>();
                index++;
            }
            else
            {
                field.Append(character);
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        if (quoted)
            throw new InvalidOperationException("CSV parser ended inside quoted field.");
        return rows;
    }
    private static string SafeBson(BsonDocument document, string name)
        => document.TryGetValue(name, out var value)
            ? value.IsString ? value.AsString : value.ToString() ?? "NULL"
            : "MISSING";

    private static bool TerminalValue(string actual, params string[] expected)
        => expected.Any(value => string.Equals(actual, value, StringComparison.OrdinalIgnoreCase));

    private static string RequiredNodeString(JsonNode node, string name)
    {
        var value = node[name]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"P11-04 browser result lacks ids.{name}.");
        return value;
    }

    private static async Task WaitForFileAsync(string path, CancellationToken ct)
    {
        while (!File.Exists(path)) await Task.Delay(250, ct);
    }
    private static async Task WaitForEitherFileAsync(
        string first,
        string second,
        CancellationToken ct)
    {
        while (!File.Exists(first) && !File.Exists(second))
            await Task.Delay(250, ct);
    }

    private static string RequiredOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        throw new ArgumentException($"Missing required option {name}.", nameof(args));
    }

    private static int IntOption(
        string[] args,
        string name,
        int fallback,
        int min,
        int max)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(args[index + 1], out var parsed) && parsed >= min && parsed <= max)
                return parsed;
            throw new ArgumentOutOfRangeException(name, $"{name} must be between {min} and {max}.");
        }
        return fallback;
    }

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string Sha256(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed record P11DownloadedFileValidation(
        bool P9CsvParsed,
        bool P9XlsxParsed,
        bool P9HashesMatch,
        bool P9CountsMatch,
        bool FormulaSafe,
        string? Error);
    private sealed record P11ReviewerFixture(
        string Key,
        string Gate,
        AppUser User);

    private sealed record P11StatisticLabelFixture(
        string Id,
        string Code,
        string Name,
        string VersionId,
        int VersionNo,
        string ConfigHash,
        string Usage,
        string DataType,
        bool IsActive);

    private sealed record P11PrerequisiteFixture(
        AppUser Admin,
        Unit RootUnit,
        Unit TargetUnit,
        Unit OutsiderUnit,
        AppUser Reporter,
        AppUser Outsider,
        IReadOnlyList<P11ReviewerFixture> Reviewers,
        Work Work);
}
