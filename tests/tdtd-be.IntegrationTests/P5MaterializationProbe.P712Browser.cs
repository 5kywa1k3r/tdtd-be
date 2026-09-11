using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Capabilities;
using tdtd_be.Models;
using tdtd_be.Services;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

internal static partial class P5MaterializationProbe
{
    private const string P712Version = "1.4";
    private const string P712SemanticHash =
        "d2ca56a4745380688e24c6926752643d2b023b2d47bc1578d3b3e2f9379457ee";

    internal static async Task<int> RunP712BrowserFixtureAsync(string[] args)
    {
        var options = P712BrowserFixtureOptions.Parse(args);
        RequireP712OfficialActivation();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
            .ToLowerInvariant();
        var runKey = options.RunKey ??
            $"p712browser_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{nonce[..8]}";
        var paths = HarnessPaths.Create(runKey);
        Require(
            !Directory.EnumerateFileSystemEntries(paths.RunRoot).Any(),
            $"P7-12 run root is not fresh: {paths.RunRoot}");
        var root = paths.IterationRoot(1);
        var backendRoot = Path.Combine(root, "backend");
        Directory.CreateDirectory(backendRoot);
        var secretPath = Path.Combine(
            root,
            "p7-12-browser-fixture.runtime.secret.json");
        var redactedPath = Path.Combine(root, "p7-12-browser-fixture.json");
        var lifecyclePath = Path.Combine(
            root,
            "p7-12-browser-fixture.lifecycle.json");
        var browserResultPath = Path.Combine(
            root,
            "p7-12-browser-result.json");
        var stopPath = Path.Combine(root, "p7-12-browser.stop");
        var oraclePath = Path.Combine(
            root,
            "p7-12-browser-direct-mongo-oracle.json");
        var cleanupPath = Path.Combine(root, "p7-12-browser-cleanup.json");
        var resultPath = Path.Combine(
            root,
            "p7-12-browser-fixture-result.json");
        var rawSentinel =
            $"P7_RAW_SOURCE_{Convert.ToHexString(RandomNumberGenerator.GetBytes(18))}";
        var signingKey =
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        P712BrowserActors? actors = null;
        P7MappingFixture? fixture = null;
        P7MappingScenario? scenario = null;
        DynamicFlowStepInstance? targetStep = null;
        var cleanupErrors = new List<string>();
        IReadOnlyList<string> leakFiles = Array.Empty<string>();
        string? failure = null;
        string? redactedSha = null;
        string? browserSha = null;
        string? stopReason = null;
        var readyAtUtc = DateTime.MinValue;
        var expiresAtUtc = DateTime.MinValue;
        var passed = false;
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.WriteLine($"P7-12 browser fixture artifacts: {paths.RunRoot}");
        try
        {
            mongo = await MongoReplicaSetLease.StartAsync(
                paths,
                root,
                runKey,
                1,
                cancellation.Token);
            var database = mongo.Client.GetDatabase(mongo.DatabaseName);
            backend = await BackendServerLease.StartAsync(
                paths,
                backendRoot,
                runKey,
                mongo,
                cancellation.Token,
                new BackendServerOptions
                {
                    EnableDynamicFlowP7MappingCandidate = false,
                    DynamicFlowP7MappingActivationThrough = null,
                    DynamicFlowMappingPreviewTokenSigningKey = signingKey,
                    FrontendOrigin = options.FrontendOrigin
                        .GetLeftPart(UriPartial.Authority)
                });
            using (var api = new ApiHarnessClient(backend.BaseUri))
            {
                var bootstrap = await BootstrapAndLoginAsync(
                    api,
                    backend,
                    database,
                    cancellation.Token);
                var baseFixture = await SeedFixtureAsync(
                    database,
                    cancellation.Token);
                var p601 = await SeedP601FixtureAsync(
                    database,
                    baseFixture,
                    cancellation.Token);
                fixture = await AdaptP601ToP7MappingAsync(
                    database,
                    p601,
                    cancellation.Token);
                var workId = await CloneP7MappingWorkAsync(
                    database,
                    fixture.WorkId,
                    "P712-BROWSER",
                    cancellation.Token);
                scenario = await PrepareP7MappingScenarioAsync(
                    api,
                    backend,
                    database,
                    bootstrap.Token,
                    baseFixture,
                    fixture,
                    workId,
                    "p7-12-browser-launch",
                    "2026-07-P712-BROWSER",
                    cancellation.Token);
                (actors, scenario) = await SeedP712ActorsAsync(
                    api,
                    backend,
                    database,
                    baseFixture,
                    fixture,
                    scenario,
                    bootstrap.Password,
                    cancellation.Token);
                targetStep = await database
                    .GetCollection<DynamicFlowStepInstance>(
                        "dynamic_flow_step_instances")
                    .Find(step =>
                        step.Id == scenario.TargetStepInstanceId &&
                        !step.IsDeleted)
                    .SingleAsync(cancellation.Token);
                await database
                    .GetCollection<WorkAssignmentReport>(
                        "work_assignment_report")
                    .UpdateOneAsync(
                        report =>
                            report.Id == scenario.SourceReportId &&
                            !report.IsDeleted,
                        Builders<WorkAssignmentReport>.Update
                            .Set(
                                report => report.ReviewerComment,
                                rawSentinel)
                            .Set(
                                report => report.UpdatedAtUtc,
                                DateTime.UtcNow),
                        cancellationToken: cancellation.Token);
                var preview = await PreviewP7MappingAsync(
                    api,
                    scenario,
                    new JsonObject(),
                    cancellation.Token);
                AssertP7MappingPreviewIdentity(
                    preview,
                    fixture,
                    scenario,
                    activationThrough: 9);
                Require(
                    !preview.Body.Contains(
                        rawSentinel,
                        StringComparison.Ordinal),
                    "P7-12 readiness preview leaked raw source metadata.");
                foreach (var sourceReader in new[]
                         {
                             actors.Coordinator,
                             actors.Reviewer
                         })
                {
                    var sourceReaderToken = await api.LoginAsync(
                        sourceReader.Username,
                        sourceReader.Password,
                        cancellation.Token);
                    var sourceRead = await api.GetAsync(
                        $"api/work-assignment-reports/{scenario.SourceReportId}",
                        sourceReaderToken,
                        ct: cancellation.Token);
                    ApiHarnessClient.ExpectStatus(
                        sourceRead,
                        HttpStatusCode.OK,
                        $"P7-12 {sourceReader.Role} exact source report access");
                    Require(
                        sourceRead.Body.Contains(
                            rawSentinel,
                            StringComparison.Ordinal),
                        $"P7-12 {sourceReader.Role} source response omitted the exact raw sentinel.");
                }
            }

            readyAtUtc = DateTime.UtcNow;
            expiresAtUtc = readyAtUtc.Add(options.Timeout);
            await EvidenceJson.WriteAsync(
                redactedPath,
                BuildP712Manifest(
                    false,
                    "READY",
                    runKey,
                    nonce,
                    paths.RunRoot,
                    root,
                    backend,
                    mongo,
                    options,
                    fixture,
                    scenario,
                    targetStep,
                    actors,
                    rawSentinel,
                    readyAtUtc,
                    expiresAtUtc,
                    null,
                    browserResultPath,
                    stopPath,
                    null,
                    false),
                cancellation.Token);
            redactedSha = await P712FileShaAsync(
                redactedPath,
                cancellation.Token);
            await EvidenceJson.WriteAsync(
                secretPath,
                BuildP712Manifest(
                    true,
                    "READY",
                    runKey,
                    nonce,
                    paths.RunRoot,
                    root,
                    backend,
                    mongo,
                    options,
                    fixture,
                    scenario,
                    targetStep,
                    actors,
                    rawSentinel,
                    readyAtUtc,
                    expiresAtUtc,
                    redactedSha,
                    browserResultPath,
                    stopPath,
                    null,
                    false),
                cancellation.Token);
            Console.WriteLine(
                $"P7_BROWSER_FIXTURE_READY={Path.GetFullPath(secretPath)}");
            Console.WriteLine(
                $"P7_BROWSER_FIXTURE_REDACTED={Path.GetFullPath(redactedPath)}");
            Console.WriteLine(
                $"P7_BROWSER_FIXTURE_STOP_FILE={Path.GetFullPath(stopPath)}");
            Console.Out.Flush();

            P712BrowserValidation? validation = null;
            if (options.ReadinessOnly)
            {
                stopReason = "READINESS_ONLY";
            }
            else
            {
                stopReason = await WaitForP712StopAsync(
                    browserResultPath,
                    stopPath,
                    expiresAtUtc,
                    cancellation.Token);
                Require(
                    stopReason == "STOP_FILE",
                    "P7-12 fixture expired before result/stop handshake.");
                validation = await ValidateP712BrowserResultAsync(
                    browserResultPath,
                    root,
                    runKey,
                    nonce,
                    rawSentinel,
                    cancellation.Token);
                browserSha = validation.ResultSha256;
            }
            var oracle = await RunP712MongoOracleAsync(
                mongo.Client.GetDatabase(mongo.DatabaseName),
                scenario,
                actors,
                rawSentinel,
                validation,
                runKey,
                nonce,
                redactedSha,
                browserSha,
                options.ReadinessOnly,
                cancellation.Token);
            await EvidenceJson.WriteAsync(
                oraclePath,
                oracle,
                cancellation.Token);
            passed = true;
            TryDeleteP712(secretPath, cleanupErrors);
            TryDeleteP712(stopPath, cleanupErrors);
            await EvidenceJson.WriteAsync(
                lifecyclePath,
                BuildP712Manifest(
                    false,
                    "STOPPED",
                    runKey,
                    nonce,
                    paths.RunRoot,
                    root,
                    backend,
                    mongo,
                    options,
                    fixture,
                    scenario,
                    targetStep,
                    actors,
                    rawSentinel,
                    readyAtUtc,
                    expiresAtUtc,
                    redactedSha,
                    browserResultPath,
                    stopPath,
                    stopReason,
                    true),
                cancellation.Token);
        }
        catch (Exception error)
        {
            failure = $"{error.GetType().Name}: {error.Message}";
            Console.Error.WriteLine(error);
        }
        finally
        {
            TryDeleteP712(secretPath, cleanupErrors);
            TryDeleteP712(stopPath, cleanupErrors);
            await CleanupP712BackendAsync(backend, cleanupErrors);
            await CleanupP712MongoAsync(mongo, cleanupErrors);
            if (actors is not null)
            {
                try
                {
                    leakFiles = ScanP712Secrets(
                        root,
                        actors.All.Select(actor => actor.Password)
                            .Append(rawSentinel)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray());
                    if (leakFiles.Count > 0)
                    {
                        cleanupErrors.Add(
                            "artifact-security: " +
                            string.Join(", ", leakFiles));
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
                failure ??= "P7-12 cleanup/security verification failed.";
            }
            await EvidenceJson.WriteAsync(
                cleanupPath,
                new
                {
                    schemaVersion = 1,
                    runKey,
                    nonce,
                    verdict = cleanupErrors.Count == 0 ? "PASS" : "FAIL",
                    secretManifestDeleted = !File.Exists(secretPath),
                    backendStopped = backend?.StopVerified ?? false,
                    backendPortReleased =
                        backend?.PortReleaseVerified ?? false,
                    databaseDropped =
                        mongo?.DatabaseDropVerified ?? false,
                    mongoStopped = mongo?.ProcessStopVerified ?? false,
                    mongoPortReleased =
                        mongo?.PortReleaseVerified ?? false,
                    mongoDataRemoved =
                        mongo?.DataDirectoryRemovalVerified ?? false,
                    leakFiles,
                    cleanupErrors,
                    completedAtUtc = DateTime.UtcNow
                });
            await EvidenceJson.WriteAsync(
                resultPath,
                new
                {
                    schemaVersion = 1,
                    runKey,
                    nonce,
                    verdict = passed ? "PASS" : "FAIL",
                    mode = options.ReadinessOnly
                        ? "READINESS_ONLY"
                        : "BROWSER",
                    failure,
                    artifactRoot = root,
                    redactedManifestPath = redactedPath,
                    redactedManifestSha256 = redactedSha,
                    browserResultPath,
                    browserResultSha256 = browserSha,
                    oraclePath,
                    lifecyclePath,
                    cleanupPath,
                    completedAtUtc = DateTime.UtcNow
                });
        }
        Console.WriteLine(
            passed
                ? $"[DAT] P7-12 browser fixture passed; artifacts={root}"
                : $"[KHONG_DAT] P7-12 browser fixture failed: {failure}");
        return passed ? 0 : 1;
    }

    internal static async Task<int> RunP712RollbackProbeAsync(string[] args)
    {
        if (args.Any(arg => !string.Equals(
                arg,
                "--p7-rollback-probe",
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "P7 rollback probe accepts only --p7-rollback-probe.");
        }
        Require(
            DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion == "1.3",
            "P7 rollback probe requires generated CURRENT v1.3.");
        Require(
            !DynamicFlowP7CatalogCandidate.ActivationEnabled,
            "P7 rollback probe requires P7 activation false.");
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8))
            .ToLowerInvariant();
        var runKey =
            $"p712rollback_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{nonce[..8]}";
        var paths = HarnessPaths.Create(runKey);
        var root = paths.IterationRoot(1);
        var backendRoot = Path.Combine(root, "backend");
        Directory.CreateDirectory(backendRoot);
        var evidencePath = Path.Combine(
            root,
            "p7-12-rollback-v1.3-zero-write.json");
        var cleanupPath = Path.Combine(root, "p7-12-rollback-cleanup.json");
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        var cleanupErrors = new List<string>();
        string? failure = null;
        var passed = false;
        try
        {
            mongo = await MongoReplicaSetLease.StartAsync(
                paths,
                root,
                runKey,
                1,
                CancellationToken.None);
            var database = mongo.Client.GetDatabase(mongo.DatabaseName);
            backend = await BackendServerLease.StartAsync(
                paths,
                backendRoot,
                runKey,
                mongo,
                CancellationToken.None,
                new BackendServerOptions
                {
                    EnableDynamicFlowP7MappingCandidate = false,
                    DynamicFlowP7MappingActivationThrough = null
                });
            using var api = new ApiHarnessClient(backend.BaseUri);
            var bootstrap = await BootstrapAndLoginAsync(
                api,
                backend,
                database,
                CancellationToken.None);
            var baseFixture = await SeedFixtureAsync(
                database,
                CancellationToken.None);
            var p601 = await SeedP601FixtureAsync(
                database,
                baseFixture,
                CancellationToken.None);
            var rootForm = p601.ExpectedSteps.Single(
                step => step.NodeId == "step_a").Form;
            var childForm = p601.ExpectedSteps.Single(
                step => step.NodeId == "step_b").Form;
            var v13Fixture = new P7MappingFixture(
                p601.WorkId,
                p601.FamilyId,
                p601.VersionId,
                p601.PayloadHash,
                p601.TargetUnitId,
                p601.TargetUserId,
                p601.OutsiderUserId,
                rootForm,
                childForm);
            var workId = await CloneP7MappingWorkAsync(
                database,
                p601.WorkId,
                "P712-ROLLBACK",
                CancellationToken.None);
            var scenario = await PrepareP7MappingScenarioAsync(
                api,
                backend,
                database,
                bootstrap.Token,
                baseFixture,
                v13Fixture,
                workId,
                "p7-12-rollback-launch",
                "2026-07-P712-ROLLBACK",
                CancellationToken.None);
            var adapted = await AdaptP601ToP7MappingAsync(
                database,
                p601,
                CancellationToken.None);
            await database.GetCollection<DynamicFlowInstance>(
                    "dynamic_flow_instances")
                .UpdateOneAsync(
                    instance => instance.Id == scenario.FlowInstanceId,
                    Builders<DynamicFlowInstance>.Update
                        .Set(
                            instance => instance.FlowPayloadHash,
                            adapted.PayloadHash)
                        .Set(
                            instance => instance.CatalogVersion,
                            P712Version)
                        .Set(
                            instance => instance.CatalogSemanticHash,
                            P712SemanticHash)
                        .Set(
                            instance => instance.UpdatedAtUtc,
                            DateTime.UtcNow));
            var before = await CaptureP712RollbackCountsAsync(
                database,
                scenario.TargetReportId,
                CancellationToken.None);
            var beforeState = await CaptureP7MappingPersistenceStateAsync(
                database,
                scenario.TargetReportId,
                CancellationToken.None);
            var preview = await api.PostAsync(
                $"api/work-assignment-reports/{scenario.TargetReportId}/draft/preview-dynamic-flow-mapping",
                new JsonObject(),
                scenario.ActorToken,
                ct: CancellationToken.None);
            var apply = await api.PostAsync(
                $"api/work-assignment-reports/{scenario.TargetReportId}/draft/apply-dynamic-flow-mapping",
                new JsonObject(),
                scenario.ActorToken,
                ct: CancellationToken.None);
            AssertP712RollbackBlocked(preview, "preview");
            AssertP712RollbackBlocked(apply, "apply");
            var after = await CaptureP712RollbackCountsAsync(
                database,
                scenario.TargetReportId,
                CancellationToken.None);
            var afterState = await CaptureP7MappingPersistenceStateAsync(
                database,
                scenario.TargetReportId,
                CancellationToken.None);
            Require(
                before.OrderBy(pair => pair.Key)
                    .SequenceEqual(after.OrderBy(pair => pair.Key)) &&
                beforeState.Fingerprint == afterState.Fingerprint &&
                beforeState.NoOrphan &&
                afterState.NoOrphan,
                "P7 rollback preview/apply changed Mongo persistence.");
            await EvidenceJson.WriteAsync(
                evidencePath,
                new
                {
                    schemaVersion = 1,
                    runKey,
                    nonce,
                    verdict = "PASS",
                    generatedCurrent = new
                    {
                        version =
                            DynamicFormFlowCapabilityCatalogMetadata
                                .CatalogVersion,
                        semanticHash =
                            DynamicFormFlowCapabilityCatalogMetadata
                                .CatalogSha256,
                        p7ActivationEnabled =
                            DynamicFlowP7CatalogCandidate.ActivationEnabled,
                        testingCandidateOverrideEnabled = false
                    },
                    runtimePin = new
                    {
                        catalogVersion = P712Version,
                        catalogSemanticHash = P712SemanticHash,
                        adapted.PayloadHash,
                        scenario.FlowInstanceId,
                        scenario.TargetReportId
                    },
                    preview = P712BlockedOutcome(preview),
                    apply = P712BlockedOutcome(apply),
                    before,
                    after,
                    fingerprintBefore = beforeState.Fingerprint,
                    fingerprintAfter = afterState.Fingerprint,
                    deltaZero = true,
                    noOrphan = true,
                    completedAtUtc = DateTime.UtcNow
                });
            passed = true;
        }
        catch (Exception error)
        {
            failure = $"{error.GetType().Name}: {error.Message}";
            Console.Error.WriteLine(error);
        }
        finally
        {
            await CleanupP712BackendAsync(backend, cleanupErrors);
            await CleanupP712MongoAsync(mongo, cleanupErrors);
            if (cleanupErrors.Count > 0)
            {
                passed = false;
                failure ??= "P7 rollback cleanup failed.";
            }
            await EvidenceJson.WriteAsync(
                cleanupPath,
                new
                {
                    schemaVersion = 1,
                    runKey,
                    verdict = cleanupErrors.Count == 0 ? "PASS" : "FAIL",
                    backendStopped = backend?.StopVerified ?? false,
                    backendPortReleased =
                        backend?.PortReleaseVerified ?? false,
                    databaseDropped =
                        mongo?.DatabaseDropVerified ?? false,
                    mongoStopped = mongo?.ProcessStopVerified ?? false,
                    mongoPortReleased =
                        mongo?.PortReleaseVerified ?? false,
                    mongoDataRemoved =
                        mongo?.DataDirectoryRemovalVerified ?? false,
                    cleanupErrors,
                    failure,
                    completedAtUtc = DateTime.UtcNow
                });
        }
        Console.WriteLine(
            passed
                ? $"[DAT] P7-12 rollback v1.3 zero-write probe passed; artifact={evidencePath}"
                : $"[KHONG_DAT] P7-12 rollback probe failed: {failure}");
        return passed ? 0 : 1;
    }

    private static async Task<(P712BrowserActors, P7MappingScenario)>
        SeedP712ActorsAsync(
            ApiHarnessClient api,
            BackendServerLease backend,
            IMongoDatabase database,
            ProbeFixture baseFixture,
            P7MappingFixture fixture,
            P7MappingScenario scenario,
            string adminPassword,
            CancellationToken ct)
    {
        var users = database.GetCollection<AppUser>("users");
        var units = database.GetCollection<Unit>("units");
        var admin = await users.Find(user =>
                user.Username == "admin" &&
                !user.IsDeleted)
            .SingleAsync(ct);
        var sourceUser = await users.Find(user =>
                user.Id == fixture.SourceUserId &&
                !user.IsDeleted)
            .SingleAsync(ct);
        var initialTarget = await users.Find(user =>
                user.Id == scenario.ActorUserId &&
                !user.IsDeleted)
            .SingleAsync(ct);
        var outsider = await users.Find(user =>
                user.Id == fixture.OutsiderUserId &&
                !user.IsDeleted)
            .SingleAsync(ct);
        var rootUnit = await units.Find(unit =>
                unit.Id == admin.UnitId &&
                !unit.IsDeleted)
            .SingleAsync(ct);
        var now = DateTime.UtcNow;
        var coordinator = NewP712User(
            "coordinator",
            "P7-12 Browser Coordinator",
            admin.UnitId!,
            "COORDINATOR",
            ManagementAccountKind.UnitManager,
            admin.Id,
            now);
        var reporter = NewP712User(
            "reporter",
            "P7-12 Browser Source Reporter",
            sourceUser.UnitId!,
            "REPORTER",
            ManagementAccountKind.NormalUser,
            admin.Id,
            now);
        var reviewer = NewP712User(
            "reviewer",
            "P7-12 Browser Reviewer",
            baseFixture.TargetUnitIds[1],
            "REVIEWER",
            ManagementAccountKind.NormalUser,
            admin.Id,
            now);
        var targetOnly = NewP712User(
            "target",
            "P7-12 Browser Target Only",
            initialTarget.UnitId!,
            "TARGET_ONLY",
            ManagementAccountKind.NormalUser,
            admin.Id,
            now);
        var hasher = new PasswordHasher<AppUser>();
        foreach (var user in new[]
                 {
                     coordinator,
                     reporter,
                     reviewer,
                     targetOnly
                 })
        {
            user.PasswordHash = hasher.HashPassword(
                user,
                backend.ActorPassword);
        }
        await users.InsertManyAsync(
            [coordinator, reporter, reviewer, targetOnly],
            cancellationToken: ct);
        await users.UpdateOneAsync(
            user => user.Id == outsider.Id,
            Builders<AppUser>.Update
                .Set(
                    user => user.PasswordHash,
                    hasher.HashPassword(
                        outsider,
                        backend.ActorPassword))
                .Set(user => user.UpdatedAtUtc, now),
            cancellationToken: ct);

        await ReassignP7MappingSourceForRedactionAsync(
            database,
            scenario,
            reporter.Id,
            ct);

        var assignmentProjections = database
            .GetCollection<AssignmentListDocRole>(
                "assignment_list_doc_roles");
        var reporterSourceProjection = await assignmentProjections
            .Find(row =>
                row.AssignmentId == scenario.SourceAssignmentId &&
                row.UserId == reporter.Id &&
                row.Roles.Contains(DocRoleType.ASSIGNEE) &&
                !row.IsDeleted)
            .SingleAsync(ct);
        Require(
            reporterSourceProjection.WorkId == scenario.WorkId &&
            reporterSourceProjection.FlowInstanceId ==
                scenario.FlowInstanceId,
            "P7-12 reporter source assignment projection drifted.");

        var sourceReport = await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .Find(report =>
                report.Id == scenario.SourceReportId &&
                !report.IsDeleted)
            .SingleAsync(ct);
        Require(
            sourceReport.WorkAssignmentId ==
                scenario.SourceAssignmentId &&
            !string.IsNullOrWhiteSpace(
                sourceReport.WorkReportPeriodId),
            "P7-12 source report identity drifted.");

        var reviewProjections = database
            .GetCollection<ReviewReportListDocRole>(
                "review_report_list_doc_roles");
        var canonicalSourceReviewProjection =
            await reviewProjections
                .Find(row =>
                    row.WorkReportPeriodId ==
                        sourceReport.WorkReportPeriodId &&
                    row.CurrentReportId ==
                        scenario.SourceReportId &&
                    !row.IsDeleted)
                .SingleAsync(ct);
        Require(
            canonicalSourceReviewProjection.WorkId ==
                scenario.WorkId &&
            canonicalSourceReviewProjection.AssignmentId ==
                scenario.SourceAssignmentId,
            "P7-12 canonical source review projection drifted.");

        var sourceReaderIds = new[]
        {
            coordinator.Id,
            reviewer.Id
        };
        var existingAssignmentReaderCount =
            await assignmentProjections.CountDocumentsAsync(
                row =>
                    row.AssignmentId ==
                        scenario.SourceAssignmentId &&
                    sourceReaderIds.Contains(row.UserId) &&
                    !row.IsDeleted,
                cancellationToken: ct);
        var existingReviewReaderCount =
            await reviewProjections.CountDocumentsAsync(
                row =>
                    row.WorkReportPeriodId ==
                        sourceReport.WorkReportPeriodId &&
                    sourceReaderIds.Contains(row.ReviewerUserId) &&
                    !row.IsDeleted,
                cancellationToken: ct);
        Require(
            existingAssignmentReaderCount == 0 &&
            existingReviewReaderCount == 0,
            "P7-12 source-reader projections were not initially unique.");

        var rawAssignmentProjections = database
            .GetCollection<BsonDocument>(
                "assignment_list_doc_roles");
        var canonicalAssignmentDocument =
            await rawAssignmentProjections
                .Find(new BsonDocument(
                    "_id",
                    ObjectId.Parse(reporterSourceProjection.Id)))
                .SingleAsync(ct);
        var rawReviewProjections = database
            .GetCollection<BsonDocument>(
                "review_report_list_doc_roles");
        var canonicalReviewDocument =
            await rawReviewProjections
                .Find(new BsonDocument(
                    "_id",
                    ObjectId.Parse(
                        canonicalSourceReviewProjection.Id)))
                .SingleAsync(ct);
        await rawAssignmentProjections.InsertManyAsync(
            [
                CloneP712AssignmentReadProjection(
                    canonicalAssignmentDocument,
                    coordinator,
                    admin.Id,
                    now),
                CloneP712AssignmentReadProjection(
                    canonicalAssignmentDocument,
                    reviewer,
                    admin.Id,
                    now)
            ],
            cancellationToken: ct);
        await rawReviewProjections.InsertManyAsync(
            [
                CloneP712ReviewReadProjection(
                    canonicalReviewDocument,
                    coordinator,
                    admin.Id,
                    now),
                CloneP712ReviewReadProjection(
                    canonicalReviewDocument,
                    reviewer,
                    admin.Id,
                    now)
            ],
            cancellationToken: ct);

        var exactAssignmentReaderRows =
            await assignmentProjections
                .Find(row =>
                    row.AssignmentId ==
                        scenario.SourceAssignmentId &&
                    sourceReaderIds.Contains(row.UserId) &&
                    !row.IsDeleted)
                .ToListAsync(ct);
        var exactReviewReaderRows =
            await reviewProjections
                .Find(row =>
                    row.WorkReportPeriodId ==
                        sourceReport.WorkReportPeriodId &&
                    sourceReaderIds.Contains(row.ReviewerUserId) &&
                    !row.IsDeleted)
                .ToListAsync(ct);
        Require(
            exactAssignmentReaderRows.Count == 2 &&
            sourceReaderIds.All(actorId =>
                exactAssignmentReaderRows.Count(row =>
                    row.UserId == actorId) == 1) &&
            exactAssignmentReaderRows.All(row =>
                row.WorkId == scenario.WorkId &&
                row.FlowInstanceId == scenario.FlowInstanceId &&
                row.Roles.Count == 1 &&
                row.Roles[0] ==
                    DocRoleType.ASSIGNMENT_BRANCH_VIEWER) &&
            exactAssignmentReaderRows.Single(row =>
                    row.UserId == coordinator.Id)
                .VisibleUnitIds.Contains(coordinator.UnitId!) &&
            exactAssignmentReaderRows.Single(row =>
                    row.UserId == reviewer.Id)
                .VisibleUnitIds.Contains(reviewer.UnitId!),
            "P7-12 exact source assignment read projections drifted.");
        Require(
            exactReviewReaderRows.Count == 2 &&
            sourceReaderIds.All(actorId =>
                exactReviewReaderRows.Count(row =>
                    row.ReviewerUserId == actorId) == 1) &&
            exactReviewReaderRows.All(row =>
                row.WorkId == scenario.WorkId &&
                row.AssignmentId ==
                    scenario.SourceAssignmentId &&
                row.CurrentReportId ==
                    scenario.SourceReportId &&
                row.Roles.Count == 1 &&
                row.Roles[0] == DocRoleType.ASSIGNER),
            "P7-12 exact source review projections drifted.");

        var step = await database
            .GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(item =>
                item.Id == scenario.TargetStepInstanceId &&
                !item.IsDeleted)
            .SingleAsync(ct);
        await ReassignP7MappingTargetAsync(
            database,
            step,
            targetOnly.Id,
            ct);
        var coordinatorSourceReportGrant = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .UpdateOneAsync(
                assignment =>
                    assignment.Id == scenario.SourceAssignmentId &&
                    assignment.WorkId == scenario.WorkId &&
                    assignment.FlowInstanceId == scenario.FlowInstanceId &&
                    assignment.FlowTemplateId == fixture.FamilyId &&
                    assignment.FlowTemplateVersionNo != null &&
                    assignment.IsActive &&
                    !assignment.IsDeleted,
                Builders<WorkAssignment>.Update
                    .AddToSet(
                        assignment => assignment.LeaderWatcherUserIds,
                        coordinator.Id)
                    .Set(assignment => assignment.UpdatedAtUtc, now),
                cancellationToken: ct);
        Require(
            coordinatorSourceReportGrant.MatchedCount == 1 &&
            coordinatorSourceReportGrant.ModifiedCount == 1,
            "P7-12 coordinator source-report assignment grant was not applied exactly once.");
        var coordinatorDefinitionGrant = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .UpdateOneAsync(
                assignment =>
                    assignment.Id == scenario.TargetAssignmentId &&
                    assignment.FlowTemplateId == fixture.FamilyId &&
                    assignment.FlowTemplateVersionNo != null &&
                    assignment.FlowEffectiveStatus ==
                        DynamicFlowEffectiveStatuses.Effective &&
                    assignment.IsActive &&
                    !assignment.IsDeleted,
                Builders<WorkAssignment>.Update
                    .AddToSet(
                        assignment => assignment.LeaderWatcherUserIds,
                        coordinator.Id)
                    .Set(assignment => assignment.UpdatedAtUtc, now),
                cancellationToken: ct);
        Require(
            coordinatorDefinitionGrant.MatchedCount == 1 &&
            coordinatorDefinitionGrant.ModifiedCount == 1,
            "P7-12 coordinator definition-read assignment grant was not applied exactly once.");
        await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .UpdateOneAsync(
                report =>
                    report.Id == scenario.TargetReportId &&
                    !report.IsDeleted,
                Builders<WorkAssignmentReport>.Update
                    .Set(
                        report => report.AssigneeUserId,
                        targetOnly.Id)
                    .Set(report => report.UpdatedAtUtc, now),
                cancellationToken: ct);
        var targetToken = await PrepareP601ActorLoginAsync(
            api,
            backend,
            database,
            targetOnly.Id,
            ct);
        scenario = scenario with
        {
            ActorUserId = targetOnly.Id,
            ActorToken = targetToken
        };

        var work = await database.GetCollection<Work>("works")
            .Find(item =>
                item.Id == scenario.WorkId &&
                !item.IsDeleted)
            .SingleAsync(ct);
        var coordinatorRef = new UserRef
        {
            UserId = coordinator.Id,
            Username = coordinator.Username,
            FullName = coordinator.FullName,
            UnitId = rootUnit.Id,
            UnitSymbol = rootUnit.Symbol,
            UnitShortName = rootUnit.ShortName,
            UnitName = rootUnit.FullName,
            PositionCode = coordinator.PositionCode
        };
        await database.GetCollection<Work>("works")
            .UpdateOneAsync(
                item =>
                    item.Id == scenario.WorkId &&
                    !item.IsDeleted,
                Builders<Work>.Update
                    .Set(
                        item => item.LeaderDirectiveUserId,
                        coordinator.Id)
                    .Set(
                        item => item.LeaderDirective,
                        coordinatorRef)
                    .Set(item => item.UpdatedAtUtc, now)
                    .Set(item => item.UpdatedByUserId, admin.Id),
                cancellationToken: ct);
        await database.GetCollection<DocRole>("doc_roles")
            .InsertManyAsync(
                [
                    NewP612WorkRole(
                        work,
                        coordinator,
                        coordinatorRef,
                        DocRoleType.LEADER_DIRECTIVE,
                        admin.Id,
                        now),
                    NewP612WorkRole(
                        work,
                        reviewer,
                        ToP612UserRef(reviewer),
                        DocRoleType.LEADER_WATCH,
                        admin.Id,
                        now)
                ],
                cancellationToken: ct);
        var actors = new P712BrowserActors(
            NewP712Actor(
                admin,
                adminPassword,
                "owner",
                true,
                true,
                false,
                false,
                true,
                "OWNER_READ"),
            NewP712Actor(
                coordinator,
                backend.ActorPassword,
                "coordinator",
                true,
                true,
                false,
                false,
                true,
                "COORDINATOR_READ"),
            NewP712Actor(
                reporter,
                backend.ActorPassword,
                "reporter",
                false,
                true,
                false,
                false,
                true,
                "SOURCE_ONLY"),
            NewP712Actor(
                reviewer,
                backend.ActorPassword,
                "reviewer",
                false,
                true,
                false,
                false,
                true,
                "READONLY_WATCH"),
            NewP712Actor(
                targetOnly,
                backend.ActorPassword,
                "targetOnly",
                false,
                true,
                true,
                true,
                false,
                "TARGET_ASSIGNEE"),
            NewP712Actor(
                outsider,
                backend.ActorPassword,
                "outsider",
                false,
                false,
                false,
                false,
                false,
                "FORBIDDEN"));
        foreach (var actor in actors.All)
            _ = await api.LoginAsync(actor.Username, actor.Password, ct);
        return (actors, scenario);
    }

    private static BsonDocument CloneP712AssignmentReadProjection(
        BsonDocument source,
        AppUser actor,
        string issuerUserId,
        DateTime now)
    {
        var clone = source.DeepClone().AsBsonDocument;
        var actorUnitId = actor.UnitId
                          ?? throw new InvalidOperationException(
                              $"P7-12 source reader {actor.Id} lacks a unit.");
        clone["_id"] = ObjectId.GenerateNewId();
        clone["userId"] = ObjectId.Parse(actor.Id);
        clone["user"] = ToP612UserRef(actor).ToBsonDocument();
        clone["roles"] = new BsonArray
        {
            new BsonInt32(
                (int)DocRoleType.ASSIGNMENT_BRANCH_VIEWER)
        };
        var visibleUnitIds =
            clone.TryGetValue(
                "visibleUnitIds",
                out var rawVisibleUnitIds) &&
            rawVisibleUnitIds.IsBsonArray
                ? rawVisibleUnitIds.AsBsonArray
                : new BsonArray();
        var actorUnitObjectId = ObjectId.Parse(actorUnitId);
        if (!visibleUnitIds.Contains(actorUnitObjectId))
            visibleUnitIds.Add(actorUnitObjectId);
        clone["visibleUnitIds"] = visibleUnitIds;
        ResetP712ProjectionAudit(
            clone,
            issuerUserId,
            now);
        return clone;
    }

    private static BsonDocument CloneP712ReviewReadProjection(
        BsonDocument source,
        AppUser actor,
        string issuerUserId,
        DateTime now)
    {
        var clone = source.DeepClone().AsBsonDocument;
        clone["_id"] = ObjectId.GenerateNewId();
        clone["userId"] = ObjectId.Parse(actor.Id);
        clone["user"] = ToP612UserRef(actor).ToBsonDocument();
        clone["reviewerUserId"] = ObjectId.Parse(actor.Id);
        clone["roles"] = new BsonArray
        {
            new BsonInt32((int)DocRoleType.ASSIGNER)
        };
        ResetP712ProjectionAudit(
            clone,
            issuerUserId,
            now);
        return clone;
    }

    private static void ResetP712ProjectionAudit(
        BsonDocument projection,
        string issuerUserId,
        DateTime now)
    {
        var issuerObjectId = ObjectId.Parse(issuerUserId);
        projection["createdAtUtc"] = now;
        projection["updatedAtUtc"] = now;
        projection["createdByUserId"] = issuerObjectId;
        projection["updatedByUserId"] = issuerObjectId;
        projection["isDeleted"] = false;
        projection["deletedAtUtc"] = BsonNull.Value;
        projection["deletedByUserId"] = BsonNull.Value;
    }

    private static AppUser NewP712User(
        string role,
        string fullName,
        string unitId,
        string positionCode,
        string accountKind,
        string issuerId,
        DateTime now)
        => new()
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Username =
                $"p7_12_browser_{role}_{ObjectId.GenerateNewId().ToString()[..10]}",
            PasswordHash = string.Empty,
            FullName = fullName,
            UnitId = unitId,
            PositionCode = positionCode,
            AccountKind = accountKind,
            Roles = [],
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = issuerId,
            UpdatedByUserId = issuerId,
            IsDeleted = false
        };

    private static P712BrowserActor NewP712Actor(
        AppUser user,
        string password,
        string role,
        bool definition,
        bool runtime,
        bool canPreview,
        bool canApply,
        bool canReadSource,
        string expectedAccess)
        => new(
            user.Username,
            user.Id,
            password,
            role,
            new P712BrowserScope(
                definition,
                runtime,
                canPreview,
                canApply,
                canReadSource,
                expectedAccess));

    private static object BuildP712Manifest(
        bool secrets,
        string state,
        string runKey,
        string nonce,
        string runRoot,
        string artifactRoot,
        BackendServerLease backend,
        MongoReplicaSetLease mongo,
        P712BrowserFixtureOptions options,
        P7MappingFixture fixture,
        P7MappingScenario scenario,
        DynamicFlowStepInstance targetStep,
        P712BrowserActors actors,
        string rawSentinel,
        DateTime readyAtUtc,
        DateTime expiresAtUtc,
        string? redactedSha,
        string browserResultPath,
        string stopPath,
        string? stopReason,
        bool secretDeleted)
    {
        var frontend =
            options.FrontendOrigin.GetLeftPart(UriPartial.Authority);
        var backendOrigin =
            backend.BaseUri.GetLeftPart(UriPartial.Authority);
        var query =
            $"stepInstanceId={Uri.EscapeDataString(targetStep.Id)}" +
            $"&branchId={Uri.EscapeDataString(targetStep.BranchId)}" +
            $"&attemptNo={targetStep.AttemptNo}" +
            $"&assignmentId={Uri.EscapeDataString(scenario.TargetAssignmentId)}" +
            $"&reportId={Uri.EscapeDataString(scenario.TargetReportId)}";
        var preview =
            $"{backendOrigin}/api/work-assignment-reports/{scenario.TargetReportId}/draft/preview-dynamic-flow-mapping";
        var apply =
            $"{backendOrigin}/api/work-assignment-reports/{scenario.TargetReportId}/draft/apply-dynamic-flow-mapping";
        return new
        {
            schemaVersion = 1,
            containsSecrets = secrets,
            doNotPublish = secrets,
            runKey,
            nonce,
            runRoot = Path.GetFullPath(runRoot),
            artifactRoot = Path.GetFullPath(artifactRoot),
            backendBaseUrl = backendOrigin,
            apiBaseUrl = $"{backendOrigin}/api",
            frontendOrigin = frontend,
            databaseName = mongo.DatabaseName,
            productionFrontendBuildId = options.FrontendBuildId,
            productionFrontendSourceRevision =
                options.FrontendSourceRevision,
            activation = new
            {
                catalogVersion =
                    DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                catalogSemanticHash =
                    DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
                p7ActivationEnabled =
                    DynamicFlowP7CatalogCandidate.ActivationEnabled,
                testingCandidateOverrideEnabled = false,
                testingCandidateActivationThrough = (int?)null,
                previewTokenSigningKeyConfigured = true,
                p8P9RemainBlocked = true
            },
            definition = new
            {
                familyId = fixture.FamilyId,
                versionId = fixture.VersionId,
                payloadHash = fixture.PayloadHash,
                state = "LOCKED",
                mappingRuleId = "p7-note-to-child",
                url =
                    $"{frontend}/design/flows/{fixture.FamilyId}/versions/{fixture.VersionId}/mapping-metadata",
                overviewUrl =
                    $"{frontend}/design/flows/{fixture.FamilyId}/versions/{fixture.VersionId}/overview",
                apiUrl =
                    $"{backendOrigin}/api/dynamic-flow-templates/{fixture.FamilyId}"
            },
            runtime = new
            {
                workId = scenario.WorkId,
                flowInstanceId = scenario.FlowInstanceId,
                stepInstanceId = scenario.TargetStepInstanceId,
                branchId = targetStep.BranchId,
                attemptNo = targetStep.AttemptNo,
                assignmentId = scenario.TargetAssignmentId,
                reportId = scenario.TargetReportId,
                sourceStepInstanceId =
                    scenario.SourceStepInstanceId,
                sourceAssignmentId = scenario.SourceAssignmentId,
                sourceReportId = scenario.SourceReportId,
                entryUrl =
                    $"{frontend}/works/{scenario.WorkId}?tab=ASSIGN",
                url =
                    $"{frontend}/works/{scenario.WorkId}/flow-instances/{scenario.FlowInstanceId}/work-to-do?{query}",
                overviewUrl =
                    $"{frontend}/works/{scenario.WorkId}/flow-instances/{scenario.FlowInstanceId}/overview?{query}",
                timelineUrl =
                    $"{frontend}/works/{scenario.WorkId}/flow-instances/{scenario.FlowInstanceId}/timeline?{query}",
                targetReportApiUrl =
                    $"{backendOrigin}/api/work-assignment-reports/{scenario.TargetReportId}",
                sourceReportApiUrl =
                    $"{backendOrigin}/api/work-assignment-reports/{scenario.SourceReportId}"
            },
            mapping = new
            {
                previewEndpoint = preview,
                applyEndpoint = apply,
                sourceReportEndpoint =
                    $"{backendOrigin}/api/work-assignment-reports/{scenario.SourceReportId}",
                successfulActor = "targetOnly",
                successfulApplyCount = 1,
                rerunMode = "PREVIEW_ONLY",
                conflictMode =
                    "REUSE_PRE_APPLY_SIGNED_PREVIEW_WITH_NEW_COMMAND_ID",
                expectedConflictStatus = 409,
                expectedConflictWriteCount = 0
            },
            lifecycle = new
            {
                state,
                readyAtUtc,
                expiresAtUtc,
                browserResultFile =
                    Path.GetFullPath(browserResultPath),
                stopFile = Path.GetFullPath(stopPath),
                stopReason,
                secretManifestDeleted = secretDeleted,
                protocol =
                    "Write bound browser result JSON, then atomically create stop file."
            },
            handshake = new
            {
                runKey,
                nonce,
                readyManifestSha256 = redactedSha,
                staleResultRejected = true
            },
            evidenceContract = new
            {
                schemaVersion = 1,
                verdict = "PASS",
                networkMockCount = 0,
                journeyActors = new[]
                {
                    "owner",
                    "coordinator",
                    "reporter",
                    "reviewer",
                    "targetOnly",
                    "outsider"
                },
                screenshotMinimum = 2,
                screenshotPathsAbsolute = true,
                screenshotSha256Required = true,
                errorsMustBeEmpty = true,
                observedApplyCount = 1,
                observedConflictStatus = 409,
                observedConflictWriteCount = 0,
                rawSecretOccurrences = 0
            },
            security = new
            {
                rawSourceSentinel = secrets
                    ? rawSentinel
                    : "REDACTED",
                rawSourceSentinelSha256 = P712Hash(rawSentinel),
                resultMustNotEchoSentinel = true
            },
            actors = new
            {
                owner = P712ManifestActor(actors.Owner, secrets),
                coordinator =
                    P712ManifestActor(actors.Coordinator, secrets),
                reporter = P712ManifestActor(actors.Reporter, secrets),
                reviewer = P712ManifestActor(actors.Reviewer, secrets),
                targetOnly =
                    P712ManifestActor(actors.TargetOnly, secrets),
                outsider = P712ManifestActor(actors.Outsider, secrets)
            }
        };
    }

    private static object P712ManifestActor(
        P712BrowserActor actor,
        bool secrets)
        => new
        {
            username = actor.Username,
            userId = actor.UserId,
            password = secrets ? actor.Password : "REDACTED",
            role = actor.Role,
            scope = new
            {
                definition = actor.Scope.Definition,
                runtime = actor.Scope.Runtime,
                canPreview = actor.Scope.CanPreview,
                canApply = actor.Scope.CanApply,
                canReadSource = actor.Scope.CanReadSource,
                expectedAccess = actor.Scope.ExpectedAccess
            }
        };

    private static async Task<string> WaitForP712StopAsync(
        string resultPath,
        string stopPath,
        DateTime expiresAtUtc,
        CancellationToken ct)
    {
        while (DateTime.UtcNow < expiresAtUtc)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(stopPath))
            {
                Require(
                    File.Exists(resultPath),
                    "P7-12 stop appeared before browser result.");
                return "STOP_FILE";
            }
            await Task.Delay(150, ct);
        }
        return "TIMEOUT";
    }

    private static async Task<P712BrowserValidation>
        ValidateP712BrowserResultAsync(
            string resultPath,
            string artifactRoot,
            string runKey,
            string nonce,
            string rawSentinel,
            CancellationToken ct)
    {
        var raw = await File.ReadAllTextAsync(resultPath, ct);
        using var document = JsonDocument.Parse(
            raw,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        var root = document.RootElement;
        Require(
            P712Int(root, "schemaVersion") == 1 &&
            P712String(root, "runKey") == runKey &&
            P712String(root, "nonce") == nonce &&
            P712String(root, "verdict") == "PASS" &&
            P712Int(root, "networkMockCount") == 0,
            "P7-12 result handshake/verdict/networkMockCount failed.");
        Require(
            !raw.Contains(rawSentinel, StringComparison.Ordinal) &&
            !raw.Contains(
                P7MappingHiddenSourceValue,
                StringComparison.Ordinal),
            "P7-12 result echoed raw source metadata.");

        var expectedActors = new[]
        {
            "owner",
            "coordinator",
            "reporter",
            "reviewer",
            "targetOnly",
            "outsider"
        };
        var journeys = P712Property(root, "journeys");
        Require(
            journeys.ValueKind == JsonValueKind.Array,
            "P7-12 journeys must be an array.");
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var journeyCount = 0;
        foreach (var journey in journeys.EnumerateArray())
        {
            _ = P712String(journey, "caseId");
            covered.Add(P712String(journey, "actor"));
            _ = P712String(journey, "action");
            _ = P712String(journey, "status");
            journeyCount++;
        }
        Require(
            expectedActors.All(covered.Contains),
            "P7-12 journeys do not cover all actors.");

        var screenshots = P712Property(root, "screenshots");
        Require(
            screenshots.ValueKind == JsonValueKind.Array,
            "P7-12 screenshots must be an array.");
        var prefix = Path.GetFullPath(artifactRoot)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var screenshotCount = 0;
        foreach (var screenshot in screenshots.EnumerateArray())
        {
            var path = Path.GetFullPath(P712String(screenshot, "path"));
            var sha = P712String(screenshot, "sha256");
            Require(
                path.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase) &&
                File.Exists(path) &&
                IsP7LowerSha256(sha) &&
                await P712FileShaAsync(path, ct) == sha,
                $"P7-12 screenshot evidence invalid: {path}");
            _ = P712String(screenshot, "viewport");
            _ = P712String(screenshot, "caseId");
            screenshotCount++;
        }
        Require(
            screenshotCount >= 2,
            "P7-12 requires at least two screenshots.");
        var errors = P712Property(root, "errors");
        Require(
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() == 0,
            "P7-12 result errors must be empty.");
        var observed = P712Property(root, "observed");
        var apply = P712Property(observed, "apply");
        var conflict = P712Property(observed, "conflict");
        var negative = P712Property(observed, "negative");
        var roles = P712Property(observed, "roles");
        Require(
            P712Int(apply, "count") == 1 &&
            P712Int(conflict, "status") == 409 &&
            P712Int(conflict, "writeCount") == 0 &&
            P712Int(negative, "rawSecretOccurrences") == 0 &&
            roles.ValueKind == JsonValueKind.Object &&
            expectedActors.All(actor =>
                roles.TryGetProperty(actor, out _)),
            "P7-12 observed apply/conflict/negative/roles failed.");
        return new P712BrowserValidation(
            await P712FileShaAsync(resultPath, ct),
            journeyCount,
            screenshotCount,
            covered.OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());
    }

    private static async Task<object> RunP712MongoOracleAsync(
        IMongoDatabase database,
        P7MappingScenario scenario,
        P712BrowserActors actors,
        string rawSentinel,
        P712BrowserValidation? browser,
        string runKey,
        string nonce,
        string? redactedSha,
        string? browserSha,
        bool readinessOnly,
        CancellationToken ct)
    {
        var source = await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .Find(report =>
                report.Id == scenario.SourceReportId &&
                !report.IsDeleted)
            .SingleAsync(ct);
        var target = await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .Find(report =>
                report.Id == scenario.TargetReportId &&
                !report.IsDeleted)
            .SingleAsync(ct);
        Require(
            source.ReviewerComment == rawSentinel &&
            target.AssigneeUserId == actors.TargetOnly.UserId,
            "P7-12 source sentinel or target assignee drifted.");
        var state = await CaptureP7MappingPersistenceStateAsync(
            database,
            scenario.TargetReportId,
            ct);
        if (readinessOnly)
        {
            Require(
                state.NoOrphan &&
                state.ReceiptCount == 0 &&
                state.ProvenanceCount == 0 &&
                state.EventCount == 0 &&
                state.OutboxCount == 0 &&
                state.MappingAuditCount == 0,
                "P7-12 readiness-only wrote mapping persistence.");
        }
        else
        {
            Require(
                browser is not null &&
                state.ReceiptCount == 1 &&
                state.ProvenanceCount == 1 &&
                state.EventCount == 1 &&
                state.OutboxCount == 1 &&
                state.MappingAuditCount == 1 &&
                !string.IsNullOrWhiteSpace(
                    target.DynamicFlowMappingReceiptId) &&
                !string.IsNullOrWhiteSpace(
                    target.DynamicFlowMappingProvenanceId) &&
                !string.IsNullOrWhiteSpace(
                    target.DynamicFlowMappingProvenanceHash) &&
                target.DynamicFlowMappingResultPayloadRevision is not null &&
                !string.IsNullOrWhiteSpace(
                    target.DynamicFlowMappingResultPayloadHash),
                "P7-12 did not persist exactly one mapping apply.");
        }
        return new
        {
            schemaVersion = 1,
            runKey,
            nonce,
            verdict = "PASS",
            readinessOnly,
            readyManifestSha256 = redactedSha,
            browserResultSha256 = browserSha,
            browserEvidence = browser,
            activation = new
            {
                catalogVersion =
                    DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                catalogSemanticHash =
                    DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
                p7ActivationEnabled =
                    DynamicFlowP7CatalogCandidate.ActivationEnabled,
                testingCandidateOverrideEnabled = false
            },
            source = new
            {
                sourceReportId = source.Id,
                sourceAssigneeUserId = source.AssigneeUserId,
                rawSourceSentinelPresent = true,
                rawSourceSentinelSha256 = P712Hash(rawSentinel),
                rawSourceSentinelRecorded = false
            },
            target = new
            {
                targetReportId = target.Id,
                targetAssigneeUserId = target.AssigneeUserId,
                target.PayloadRevision,
                target.PayloadHash,
                target.LifecycleRevision,
                target.DynamicFlowMappingReceiptId,
                target.DynamicFlowMappingProvenanceId,
                target.DynamicFlowMappingProvenanceHash,
                target.DynamicFlowMappingResultPayloadRevision,
                target.DynamicFlowMappingResultPayloadHash
            },
            counts = new
            {
                receipts = state.ReceiptCount,
                provenance = state.ProvenanceCount,
                events = state.EventCount,
                outbox = state.OutboxCount,
                mappingAudits = state.MappingAuditCount,
                payloads = state.PayloadCount,
                sections = state.SectionCount
            },
            exactlyOneBusinessWrite =
                !readinessOnly &&
                state.ReceiptCount == 1 &&
                state.ProvenanceCount == 1 &&
                state.EventCount == 1 &&
                state.OutboxCount == 1 &&
                state.MappingAuditCount == 1,
            completedAtUtc = DateTime.UtcNow
        };
    }

    private static void RequireP712OfficialActivation()
        => Require(
            DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion ==
                P712Version &&
            DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256 ==
                P712SemanticHash &&
            DynamicFlowP7CatalogCandidate.ActivationEnabled,
            "P7-12 requires official CURRENT v1.4 with sealed hash.");

    private static JsonElement P712Property(
        JsonElement parent,
        string name)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out var value))
        {
            throw new InvalidOperationException(
                $"P7-12 result is missing '{name}'.");
        }
        return value;
    }

    private static string P712String(
        JsonElement parent,
        string name)
    {
        var value = P712Property(parent, name);
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException(
                $"P7-12 result '{name}' must be a string.");
        }
        return value.GetString()!;
    }

    private static int P712Int(
        JsonElement parent,
        string name)
    {
        var value = P712Property(parent, name);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result))
        {
            throw new InvalidOperationException(
                $"P7-12 result '{name}' must be an integer.");
        }
        return result;
    }

    private static async Task<string> P712FileShaAsync(
        string path,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(
                await SHA256.HashDataAsync(stream, ct))
            .ToLowerInvariant();
    }

    private static string P712Hash(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static IReadOnlyList<string> ScanP712Secrets(
        string root,
        IReadOnlyList<string> secrets)
    {
        var extensions = new HashSet<string>(
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
        var leaks = new List<string>();
        foreach (var path in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            if (!extensions.Contains(Path.GetExtension(path)))
                continue;
            var text = File.ReadAllText(path);
            if (secrets.Any(secret =>
                    !string.IsNullOrEmpty(secret) &&
                    text.Contains(secret, StringComparison.Ordinal)))
            {
                leaks.Add(Path.GetRelativePath(root, path));
            }
        }
        return leaks.OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static void TryDeleteP712(
        string path,
        List<string> errors)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception error)
        {
            errors.Add(
                $"delete-{Path.GetFileName(path)}: {error.Message}");
        }
    }

    private static async Task CleanupP712BackendAsync(
        BackendServerLease? backend,
        List<string> errors)
    {
        if (backend is null)
            return;
        try
        {
            await backend.StopAsync();
        }
        catch (Exception error)
        {
            errors.Add($"backend: {error.Message}");
        }
        try
        {
            await backend.DisposeAsync();
        }
        catch (Exception error)
        {
            errors.Add($"backend-dispose: {error.Message}");
        }
    }

    private static async Task CleanupP712MongoAsync(
        MongoReplicaSetLease? mongo,
        List<string> errors)
    {
        if (mongo is null)
            return;
        try
        {
            await mongo.DropDatabaseGuardedAsync(CancellationToken.None);
        }
        catch (Exception error)
        {
            errors.Add($"mongo-drop: {error.Message}");
        }
        try
        {
            await mongo.StopProcessAsync();
        }
        catch (Exception error)
        {
            errors.Add($"mongo-stop: {error.Message}");
        }
        try
        {
            mongo.RemoveDataDirectoryGuarded();
        }
        catch (Exception error)
        {
            errors.Add($"mongo-data: {error.Message}");
        }
        try
        {
            await mongo.DisposeAsync();
        }
        catch (Exception error)
        {
            errors.Add($"mongo-dispose: {error.Message}");
        }
    }

    private static void AssertP712RollbackBlocked(
        ApiHarnessResponse response,
        string operation)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Conflict,
            $"P7-12 rollback {operation}");
        Require(
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "errorCode") ==
                "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
            $"P7-12 rollback {operation} error code drifted.");
    }

    private static object P712BlockedOutcome(
        ApiHarnessResponse response)
        => new
        {
            statusCode = (int)response.StatusCode,
            errorCode = ApiHarnessClient.FindStringRecursive(
                response.Json,
                "errorCode"),
            field = ApiHarnessClient.FindStringRecursive(
                response.Json,
                "field"),
            reason = ApiHarnessClient.FindStringRecursive(
                response.Json,
                "reason"),
            blockedUntilPhase = ApiHarnessClient.FindStringRecursive(
                response.Json,
                "blockedUntilPhase")
        };

    private static async Task<IReadOnlyDictionary<string, long>>
        CaptureP712RollbackCountsAsync(
            IMongoDatabase database,
            string targetReportId,
            CancellationToken ct)
    {
        var id = ObjectId.Parse(targetReportId);
        var filters = new Dictionary<string, BsonDocument>(
            StringComparer.Ordinal)
        {
            ["work_assignment_report"] = new("_id", id),
            ["work_report_payloads"] = new("reportId", id),
            ["work_assignment_report_sections"] =
                new("workAssignmentReportId", id),
            ["work_assignment_report_logs"] =
                new("workAssignmentReportId", id),
            ["dynamic_flow_mapping_apply_receipts"] =
                new("targetReportId", id),
            ["dynamic_flow_mapping_provenance"] =
                new("targetReportId", id),
            ["dynamic_flow_mapping_events"] =
                new("targetReportId", id),
            ["dynamic_flow_mapping_outbox"] =
                new("targetReportId", id)
        };
        var counts = new Dictionary<string, long>(
            StringComparer.Ordinal);
        foreach (var (collection, filter) in filters)
        {
            counts[collection] = await database
                .GetCollection<BsonDocument>(collection)
                .CountDocumentsAsync(
                    filter,
                    cancellationToken: ct);
        }
        return counts;
    }

    private sealed record P712BrowserValidation(
        string ResultSha256,
        int JourneyCount,
        int ScreenshotCount,
        IReadOnlyList<string> CoveredActors);
}

internal sealed record P712BrowserActor(
    string Username,
    string UserId,
    string Password,
    string Role,
    P712BrowserScope Scope);

internal sealed record P712BrowserScope(
    bool Definition,
    bool Runtime,
    bool CanPreview,
    bool CanApply,
    bool CanReadSource,
    string ExpectedAccess);

internal sealed record P712BrowserActors(
    P712BrowserActor Owner,
    P712BrowserActor Coordinator,
    P712BrowserActor Reporter,
    P712BrowserActor Reviewer,
    P712BrowserActor TargetOnly,
    P712BrowserActor Outsider)
{
    public IReadOnlyList<P712BrowserActor> All =>
        [
            Owner,
            Coordinator,
            Reporter,
            Reviewer,
            TargetOnly,
            Outsider
        ];
}

internal sealed record P712BrowserFixtureOptions(
    Uri FrontendOrigin,
    TimeSpan Timeout,
    string? RunKey,
    bool ReadinessOnly,
    string? FrontendBuildId,
    string? FrontendSourceRevision)
{
    private const int DefaultTimeoutSeconds = 1_800;

    public static P712BrowserFixtureOptions Parse(string[] args)
    {
        string? origin = null;
        string? runKey = null;
        string? buildId = null;
        string? sourceRevision = null;
        var timeout = DefaultTimeoutSeconds;
        var readinessOnly = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(
                    args[index],
                    "--p7-browser-fixture",
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
                origin = args[++index];
            }
            else if (string.Equals(
                         args[index],
                         "--p7-browser-fixture-timeout-seconds",
                         StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Length &&
                     int.TryParse(args[++index], out var parsed))
            {
                timeout = parsed;
            }
            else if (string.Equals(
                         args[index],
                         "--p7-browser-fixture-run-key",
                         StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Length)
            {
                runKey = args[++index];
            }
            else if (string.Equals(
                         args[index],
                         "--p7-browser-fixture-readiness-only",
                         StringComparison.OrdinalIgnoreCase))
            {
                readinessOnly = true;
            }
            else if (string.Equals(
                         args[index],
                         "--frontend-build-id",
                         StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Length)
            {
                buildId = args[++index].Trim();
            }
            else if (string.Equals(
                         args[index],
                         "--frontend-source-revision",
                         StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Length)
            {
                sourceRevision = args[++index].Trim();
            }
            else
            {
                throw new ArgumentException(
                    $"Unknown/incomplete P7 browser argument: {args[index]}");
            }
        }
        if (!Uri.TryCreate(
                origin,
                UriKind.Absolute,
                out var parsedOrigin) ||
            parsedOrigin.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(parsedOrigin.Host))
        {
            throw new ArgumentException(
                "--frontend-origin must be absolute HTTP(S).");
        }
        if (timeout is < 1 or > 3_600)
            throw new ArgumentOutOfRangeException(nameof(args));
        if (!string.IsNullOrWhiteSpace(runKey) &&
            runKey.Any(character =>
                !(char.IsLetterOrDigit(character) ||
                  character is '_' or '-')))
        {
            throw new ArgumentException("Invalid P7 browser run key.");
        }
        if (!readinessOnly)
        {
            if (string.IsNullOrWhiteSpace(buildId))
            {
                throw new ArgumentException(
                    "--frontend-build-id is required.");
            }
            if (sourceRevision is null ||
                sourceRevision.Length != 64 ||
                sourceRevision.Any(character =>
                    character is not (>= '0' and <= '9') and
                        not (>= 'a' and <= 'f')))
            {
                throw new ArgumentException(
                    "--frontend-source-revision must be lowercase SHA-256.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(buildId) ||
                 !string.IsNullOrWhiteSpace(sourceRevision))
        {
            throw new ArgumentException(
                "Readiness-only rejects frontend build metadata.");
        }
        return new P712BrowserFixtureOptions(
            new Uri(
                parsedOrigin.GetLeftPart(UriPartial.Authority),
                UriKind.Absolute),
            TimeSpan.FromSeconds(timeout),
            string.IsNullOrWhiteSpace(runKey) ? null : runKey,
            readinessOnly,
            buildId,
            sourceRevision);
    }
}



