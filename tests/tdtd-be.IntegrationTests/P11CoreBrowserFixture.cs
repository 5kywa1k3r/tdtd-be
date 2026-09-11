using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Owns only the isolated production stack and prerequisite identities for
/// P11-03. Form/Flow/launch/report/review transitions are deliberately absent
/// and must be driven by the browser through mounted production owners.
/// </summary>
internal static class P11CoreBrowserFixture
{
    internal const string CommandLineSwitch = "--p11-core-browser-fixture";
    private const string PromptId = "P11-03";
    private const string ScheduleIdentityJson =
        "{\"anchor\":\"2026-08-21\",\"cadence\":\"once\",\"timezone\":\"Asia/Ho_Chi_Minh\"}";

    public static async Task<int> RunAsync(string[] args)
    {
        var runKey = RequiredOption(args, "--run-key");
        var chainId = RequiredOption(args, "--chain-id");
        var frontendOrigin = new Uri(RequiredOption(args, "--frontend-origin"));
        var timeoutSeconds = IntOption(args, "--timeout-seconds", 1800, 30, 3600);
        var paths = HarnessPaths.CreateP11(runKey, chainId, PromptId);
        var continuationPaths = P11ContinuationSnapshot.OptionalExportPaths(args, paths);
        var iterationRoot = paths.IterationRoot(1);
        var secretPath = Path.Combine(iterationRoot, "P11-03.fixture.runtime.secret.json");
        var redactedPath = Path.Combine(iterationRoot, "P11-03.fixture.json");
        var browserResultPath = Path.Combine(iterationRoot, "P11-03.browser-result.json");
        var oraclePath = Path.Combine(iterationRoot, "P11-03.direct-mongo.json");
        var cleanupPath = Path.Combine(iterationRoot, "P11-03.cleanup.json");
        var cleanupErrors = new List<string>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        P11ContinuationSnapshot.SnapshotResult? continuation = null;
        P11CleanupInventory.Evidence? cleanupInventory = null;
        P11CleanupInventory.SecondDryRunEvidence? secondDryRun = null;
        var verdict = "FAIL";
        var databaseDropped = false;
        var mongoDataDirectoryRemoved = false;
        var backendQuiescedForContinuation = false;

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
            backend = await BackendServerLease.StartAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                cancellation.Token,
                new BackendServerOptions
                {
                    EnableDynamicFlowRuntimeCandidate = true,
                    EnableDynamicFlowP7MappingCandidate = true,
                    HangfireServerEnabled = true,
                    FrontendOrigin = frontendOrigin.GetLeftPart(UriPartial.Authority),
                    SuppressTestingFixedUtcNow = true
                });

            var database = mongo.Client.GetDatabase(mongo.DatabaseName);
            string adminPassword;
            AppUser admin;
            using (var api = new ApiHarnessClient(backend.BaseUri))
            {
                var bootstrap = await api.PostAsync(
                    "api/system/bootstrap",
                    new { },
                    headers: new Dictionary<string, string>
                    {
                        ["X-System-Bootstrap-Key"] = backend.BootstrapKey
                    },
                    ct: cancellation.Token);
                ApiHarnessClient.ExpectStatus(
                    bootstrap,
                    System.Net.HttpStatusCode.OK,
                    "P11-03 system bootstrap");
                adminPassword = ApiHarnessClient.RequiredString(bootstrap.Json, "defaultPassword");
                var adminToken = await api.LoginAsync("admin", adminPassword, cancellation.Token);
                if (string.IsNullOrWhiteSpace(adminToken))
                    throw new InvalidOperationException("P11-03 bootstrap login returned no token.");
            }

            admin = await database.GetCollection<AppUser>("users")
                .Find(item => item.Username == "admin" && !item.IsDeleted)
                .SingleAsync(cancellation.Token);
            if (string.IsNullOrWhiteSpace(admin.UnitId))
                throw new InvalidOperationException("P11-03 bootstrap admin lacks root unit.");
            var rootUnit = await database.GetCollection<Unit>("units")
                .Find(item => item.Id == admin.UnitId && !item.IsDeleted)
                .SingleAsync(cancellation.Token);

            var prerequisite = await SeedPrerequisitesAsync(
                database,
                rootUnit,
                admin,
                backend.ActorPassword,
                cancellation.Token);
            var apiBaseUrl = $"{backend.BaseUri.GetLeftPart(UriPartial.Authority)}/api";
            var readyAtUtc = DateTime.UtcNow;
            var secretManifest = new
            {
                schemaVersion = "P11_CORE_BROWSER_FIXTURE_SECRET_V1",
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
                    }
                },
                prerequisites = new
                {
                    rootUnitId = rootUnit.Id,
                    targetUnitId = prerequisite.TargetUnit.Id,
                    outsiderUnitId = prerequisite.OutsiderUnit.Id,
                    workId = prerequisite.Work.Id,
                    workCode = prerequisite.Work.Code
                },
                launch = new
                {
                    commandId = $"p11-03-launch-{runKey}",
                    periodKey = "2026-08-P11-03",
                    scheduleIdentityJson = ScheduleIdentityJson,
                    targetUnitIds = new[] { prerequisite.TargetUnit.Id }
                },
                protocol = new { browserResultPath, oraclePath, cleanupPath }
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
                    schemaVersion = "P11_CORE_BROWSER_FIXTURE_V1",
                    promptId = PromptId,
                    chainId,
                    runKey,
                    readyAtUtc,
                    frontendOrigin = frontendOrigin.GetLeftPart(UriPartial.Authority),
                    apiBaseUrl,
                    databaseNameSha256 = Sha256(mongo.DatabaseName),
                    actorAliases = new[] { "owner", "reporter", "outsider" },
                    prerequisiteKinds = new[] { "UNIT", "USER", "WORK" },
                    testedTransitionsSeeded = Array.Empty<string>(),
                    productionOwnersRequired = new[]
                    {
                        "FORM_UI", "FLOW_UI", "WORK_PREFLIGHT_CONFIRM_UI",
                        "REPORT_UI", "REVIEW_UI", "RUNTIME_FINALIZE_CASCADE"
                    }
                }, jsonOptions),
                new UTF8Encoding(false),
                cancellation.Token);

            Console.WriteLine($"P11_CORE_FIXTURE_READY={secretPath}");
            await WaitForFileAsync(browserResultPath, cancellation.Token);
            var browserResult = JsonNode.Parse(
                await File.ReadAllTextAsync(browserResultPath, cancellation.Token))
                ?? throw new InvalidOperationException("P11-03 browser result is empty.");
            var browserVerdict = browserResult["verdict"]?.GetValue<string>() ?? "FAIL";
            var oracle = await CaptureDirectMongoOracleAsync(
                database, browserResult, cancellation.Token);
            await File.WriteAllTextAsync(
                oraclePath,
                oracle.ToJsonString(jsonOptions),
                new UTF8Encoding(false),
                cancellation.Token);
            verdict = browserVerdict == "PASS" && oracle["verdict"]?.GetValue<string>() == "PASS"
                ? "PASS"
                : "FAIL";
            if (verdict == "PASS" && continuationPaths is not null)
            {
                var browserIds = browserResult["ids"]
                    ?? throw new InvalidOperationException("P11-03 browser result lacks finalized identities.");
                await backend.StopAsync();
                backendQuiescedForContinuation = backend.StopVerified && backend.PortReleaseVerified;
                continuation = await P11ContinuationSnapshot.CreateAsync(
                    database,
                    continuationPaths,
                    chainId,
                    runKey,
                    browserIds,
                    RequiredNodeString(oracle, "identitySha256"),
                    cancellation.Token);
            }
        }
        catch (Exception error)
        {
            cleanupErrors.Add($"fixture:{error.GetType().Name}:{error.Message}");
            Console.Error.WriteLine(error);
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
                    cleanupInventory = await P11CleanupInventory.CaptureAsync(
                        mongo,
                        paths,
                        iterationRoot,
                        continuation?.ManifestPath,
                        Array.Empty<string>(),
                        CancellationToken.None);
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"cleanup-inventory:{error.Message}");
                }
                try
                {
                    await mongo.DropDatabaseGuardedAsync(CancellationToken.None);
                    databaseDropped = mongo.DatabaseDropVerified;
                }
                catch (Exception error) { cleanupErrors.Add($"database-drop:{error.Message}"); }
                try { await mongo.DisposeAsync(); }
                catch (Exception error) { cleanupErrors.Add($"mongo-stop:{error.Message}"); }
                try
                {
                    mongo.RemoveDataDirectoryGuarded();
                    mongoDataDirectoryRemoved = mongo.DataDirectoryRemovalVerified;
                }
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
            var continuationRetained = continuationPaths is null ||
                (continuation is not null &&
                 File.Exists(continuation.SnapshotPath) &&
                 File.Exists(continuation.ManifestPath));
            var cleanupDryRunPassed = cleanupInventory?.DryRunPassed == true;
            var exactOwnedResourceApply = databaseDropped && mongoDataDirectoryRemoved;
            var unknownCollectionDelta = cleanupInventory?.UnknownCollectionDelta;
            var secondDryRunEmpty = secondDryRun?.SecondDryRunEmpty == true;
            var cleanupPassed = cleanupErrors.Count == 0 &&
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
                                mongoDataDirectoryRemoved &&
                                continuationRetained;
            var cleanup = new
            {
                schemaVersion = "P11_CORE_CLEANUP_V1",
                promptId = PromptId,
                runKey,
                inventoryArtifact = "mongo-cleanup-inventory.json",
                inventoryCaptured = cleanupInventory is not null,
                dryRunPassed = cleanupDryRunPassed,
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
                backendStopped,
                backendPortReleased,
                mongoStopped,
                mongoPortReleased,
                mongoDataDirectoryRemoved,
                continuationRequested = continuationPaths is not null,
                continuationRetained,
                backendQuiescedForContinuation,
                continuationSnapshotSha256 = continuation?.SnapshotSha256,
                continuationPayloadSha256 = continuation?.PayloadSha256,
                continuationIdentitySha256 = continuation?.IdentitySha256,
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

        Console.WriteLine($"P11_CORE_FIXTURE_VERDICT={verdict}");
        return verdict == "PASS" ? 0 : 1;
    }

    private static async Task<P11PrerequisiteFixture> SeedPrerequisitesAsync(
        IMongoDatabase database,
        Unit root,
        AppUser admin,
        string actorPassword,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
        var targetUnit = NewUnit("REPORT", root, admin, nonce, now);
        var outsiderUnit = NewUnit("OUTSIDE", root, admin, nonce, now);
        await database.GetCollection<Unit>("units").InsertManyAsync(
            [targetUnit, outsiderUnit], cancellationToken: ct);

        var hasher = new PasswordHasher<AppUser>();
        var reporter = NewActor("reporter", targetUnit, admin, nonce, now);
        var outsider = NewActor("outsider", outsiderUnit, admin, nonce, now);
        reporter.PasswordHash = hasher.HashPassword(reporter, actorPassword);
        outsider.PasswordHash = hasher.HashPassword(outsider, actorPassword);
        await database.GetCollection<AppUser>("users").InsertManyAsync(
            [reporter, outsider], cancellationToken: ct);

        var work = new Work
        {
            Id = ObjectId.GenerateNewId().ToString(),
            AutoCode = $"P11-03-{nonce}",
            Code = $"P11-03-{nonce}",
            Name = $"P11-03 Form Flow Report Review {nonce}",
            Description = "Prerequisite Work only; P11-CORE transitions are browser-owned.",
            Status = WorkStatus.S1,
            Type = WorkType.TASK,
            Priority = WorkPriority.MEDIUM,
            LeaderDirectiveUserId = admin.Id,
            LeaderWatchUserIds = [],
            Owner = UserRefFor(admin, root),
            LeaderDirective = UserRefFor(admin, root),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = admin.Id,
            UpdatedByUserId = admin.Id,
            IsDeleted = false
        };
        await database.GetCollection<Work>("works").InsertOneAsync(work, cancellationToken: ct);
        await database.GetCollection<DocRole>("doc_roles").InsertManyAsync(
            [
                new DocRole
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    DocType = DocType.WORK,
                    DocId = work.Id,
                    UserId = admin.Id,
                    Role = DocRoleType.OWNER,
                    User = work.Owner,
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = admin.Id,
                    UpdatedByUserId = admin.Id
                },
                new DocRole
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    DocType = DocType.WORK,
                    DocId = work.Id,
                    UserId = admin.Id,
                    Role = DocRoleType.LEADER_DIRECTIVE,
                    User = work.LeaderDirective,
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = admin.Id,
                    UpdatedByUserId = admin.Id
                }
            ],
            cancellationToken: ct);
        return new P11PrerequisiteFixture(targetUnit, outsiderUnit, reporter, outsider, work);
    }

    private static Unit NewUnit(
        string kind,
        Unit root,
        AppUser admin,
        string nonce,
        DateTime now)
        => new()
        {
            Id = ObjectId.GenerateNewId().ToString(),
            FullName = $"P11-03 {kind} {nonce}",
            ShortName = $"P11{kind[..Math.Min(3, kind.Length)]}{nonce[..3]}",
            Symbol = $"P11{kind[..Math.Min(3, kind.Length)]}{nonce[..3]}",
            Code = $"P11{kind}{nonce}",
            Level = root.Level + 1,
            Version = 1,
            ParentUnitId = root.Id,
            UnitTypeCodes = [],
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = admin.Id,
            UpdatedByUserId = admin.Id,
            IsDeleted = false
        };

    private static AppUser NewActor(
        string kind,
        Unit unit,
        AppUser admin,
        string nonce,
        DateTime now)
        => new()
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Username = $"p11_03_{kind}_{nonce}",
            PasswordHash = "P11_RUNTIME_PASSWORD_PENDING",
            FullName = $"P11-03 {kind} {nonce}",
            UnitId = unit.Id,
            PositionCode = "SPECIALIST",
            Roles = [],
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = admin.Id,
            UpdatedByUserId = admin.Id,
            IsDeleted = false
        };

    private static UserRef UserRefFor(AppUser user, Unit unit)
        => new()
        {
            UserId = user.Id,
            Username = user.Username,
            FullName = user.FullName,
            UnitId = unit.Id,
            UnitSymbol = unit.Symbol,
            UnitShortName = unit.ShortName,
            UnitName = unit.FullName,
            PositionCode = user.PositionCode
        };

    private static async Task<JsonObject> CaptureDirectMongoOracleAsync(
        IMongoDatabase database,
        JsonNode browserResult,
        CancellationToken ct)
    {
        var ids = browserResult["ids"]
                  ?? throw new InvalidOperationException("P11-03 browser result lacks ids.");
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
        var launchCommandId = RequiredNodeString(ids, "launchCommandId");

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

        Check("MONGO-FORM", formCount == 1, new { formId, count = formCount });
        Check("MONGO-FLOW",
            familyCount == 1 && versionCount == 1 &&
            string.Equals(flowContributionPolicy, "INCLUDE", StringComparison.Ordinal) &&
            string.Equals(SafeBson(flowVersion, "payloadHash"), flowPayloadHash, StringComparison.Ordinal) &&
            string.Equals(SafeBson(flowVersion, "contributionPolicy"), flowContributionPolicy, StringComparison.Ordinal) &&
            string.Equals(SafeBson(flowVersion, "contributionPolicyHash"), flowContributionPolicyHash, StringComparison.Ordinal),
            new
            {
                flowFamilyId,
                familyCount,
                flowVersionId,
                versionCount,
                flowPayloadHash,
                flowContributionPolicy,
                flowContributionPolicyHash
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
            });
        var passed = checks.All(node => node?["status"]?.GetValue<string>() == "PASS");
        return new JsonObject
        {
            ["schemaVersion"] = "P11_CORE_DIRECT_MONGO_V1",
            ["promptId"] = PromptId,
            ["verdict"] = passed ? "PASS" : "FAIL",
            ["checks"] = checks,
            ["identitySha256"] = Sha256(string.Join("\n",
                formId, flowFamilyId, flowVersionId, flowPayloadHash,
                flowContributionPolicy, flowContributionPolicyHash, workId, instanceId,
                stepId, assignmentId, reportId, launchCommandId))
        };
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
            throw new InvalidOperationException($"P11-03 browser result lacks ids.{name}.");
        return value;
    }

    private static async Task WaitForFileAsync(string path, CancellationToken ct)
    {
        while (!File.Exists(path)) await Task.Delay(250, ct);
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

    private sealed record P11PrerequisiteFixture(
        Unit TargetUnit,
        Unit OutsiderUnit,
        AppUser Reporter,
        AppUser Outsider,
        Work Work);
}
