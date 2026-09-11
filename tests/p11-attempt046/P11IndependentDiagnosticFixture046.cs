using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using MongoDB.Driver;
using MongoDB.Bson;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    internal const string IndependentDiagnosticSwitch = "--p11-independent-diagnostic-fixture";
    private readonly List<object> _diagnosticPrerequisites = [];
    private string _diagnosticOutput = "";
    private string _diagnosticGroup = "";
    private string _diagnosticStage = "STARTING";
    private const string DiagnosticPrimaryStatisticLabelCode = "p11.diag.metric.amount";
    private const string DiagnosticPagingStatisticLabelCode = "p11.diag.metric.amount.secondary";
    private static readonly JsonSerializerOptions DiagnosticJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    // Same five aliases/gates/role as the official P11ResultBrowserFixture.
    // These are distinct prerequisite identities, never seeded review decisions.
    private static readonly (string Alias, string Gate)[] Diagnostic043ReviewerSeeds =
    [
        ("reviewerForm", "FORM"), ("reviewerFlow", "FLOW"),
        ("reviewerAssignment", "ASSIGNMENT"), ("reviewerMapping", "MAPPING"),
        ("reviewerStatistics", "STATISTICS")
    ];

    internal static async Task<int> RunIndependentDiagnosticFixtureAsync(string[] args)
    {
        string Required(string name) => ReadOption(args, name) ?? throw new InvalidOperationException("Missing " + name);
        var group = Required("--group");
        if (!new[] { "STAT_EXPORT", "INITIAL_RECON", "STALE_RECHECK", "MATCHED_SUCCESSOR", "EVIDENCE_PERMISSION" }.Contains(group))
            throw new InvalidOperationException("Unknown independent diagnostic group.");
        var runKey = Required("--run-key");
        if (!Regex.IsMatch(runKey, "^p11d_[a-zA-Z0-9_]{8,28}$"))
            throw new InvalidOperationException("Diagnostic run key must be short and fixture-scoped.");
        var origin = new Uri(Required("--frontend-origin"));
        if (origin.Scheme != "http" || !origin.IsLoopback || origin.AbsolutePath != "/")
            throw new InvalidOperationException("Diagnostic frontend must be one loopback HTTP origin.");
        var workspace = Directory.GetCurrentDirectory();
        if (!File.Exists(Path.Combine(workspace, "tdtd-be", "tdtd-be.csproj")))
            throw new InvalidOperationException("Diagnostic fixture must start at the workspace root.");
        var output = Path.GetFullPath(Required("--output-directory"));
        P11ServerExportCleanup.RequireUnder(Path.Combine(workspace, ".p11-artifacts", "offline-validation", "diagnostic-process-v1"), output);
        P11ServerExportCleanup.RequireSafeAncestors(workspace, output);
        Directory.CreateDirectory(output);
        P11ServerExportCleanup.WriteExclusive(Path.Combine(output, "fixture.claim.json"),
            new { schemaVersion = "P11_DIAGNOSTIC_FIXTURE_CLAIM_V1", fixtureId = runKey, backendRunId = runKey,
                group, processId = Environment.ProcessId, testedCaseCreditGranted = false });
        var leaseRoot = Path.GetFullPath(Path.Combine(workspace, ".build", "p11diag", runKey));
        P11ServerExportCleanup.RequireSafeAncestors(workspace, leaseRoot);
        if (Directory.Exists(leaseRoot)) throw new InvalidOperationException("Diagnostic lease directory already exists; no reuse.");
        Directory.CreateDirectory(leaseRoot);
        var paths = new HarnessPaths(workspace, Path.Combine(workspace, "tdtd-be"), Path.GetDirectoryName(leaseRoot)!, leaseRoot);
        var timeout = int.TryParse(ReadOption(args, "--timeout-seconds"), out var seconds) ? seconds : 900;
        if (timeout is < 60 or > 1800) throw new InvalidOperationException("Diagnostic timeout outside 60..1800 seconds.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        return await new P10ReconciliationCoreProbe(paths, runKey)
            { _diagnosticOutput = output, _diagnosticGroup = group }
            .ExecuteIndependentDiagnosticFixtureAsync(origin, cancellation.Token);
    }

    private async Task<int> ExecuteIndependentDiagnosticFixtureAsync(Uri origin, CancellationToken ct)
    {
        var errors = new List<string>();
        Exception? failure = null;
        P11ServerExportCleanupResult? exports = null;
        var secretPath = Path.Combine(_diagnosticOutput, "runtime.secret.json");
        try
        {
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            RememberSecret(key);
            _mongo = await MongoReplicaSetLease.StartP10Async(_paths, _iterationRoot, _runKey, 1, ct);
            _database = _mongo.Client.GetDatabase(_mongo.DatabaseName);
            _backend = await BackendServerLease.StartAsync(_paths, Path.Combine(_iterationRoot, "bootstrap"),
                _runKey, _mongo, ct, CloseoutBackendOptions(key) with { FrontendOrigin = origin.GetLeftPart(UriPartial.Authority) });
            _api = new ApiHarnessClient(_backend.BaseUri);
            await BootstrapAndSeedFixtureAsync(ct);
            var actorPassword = RequireBackend().ActorPassword;
            RememberSecret(actorPassword);
            var old = RequireBackend();
            _api.Dispose(); _api = null;
            await old.StopAsync(); await old.DisposeAsync();
            _backend = await BackendServerLease.StartAsync(_paths, Path.Combine(_iterationRoot, "production"),
                _runKey, _mongo, ct, CloseoutBackendOptions(key, Actor("executor").Token) with
                {
                    ActorPasswordOverride = actorPassword,
                    FrontendOrigin = origin.GetLeftPart(UriPartial.Authority),
                    HangfireServerEnabled = true, HangfireRecurringRegistrationEnabled = true
                });
            _api = new ApiHarnessClient(_backend.BaseUri);
            await AwaitInfrastructureAsync(ct);
            _diagnosticStage = "P9_PUBLICATION_SETUP";
            var publication = await PrepareProductionDirectLifecycleFixtureAsync(ct,
                useNativeNullPeriodPair: true,
                cumulativeContributionPolicyJson: BuildP11DirectFlowMappingContributionPolicy(),
                includeSecondStatisticField: true);
            _diagnosticStage = "REVIEWER_ACTORS_SETUP";
            await DiagnosticProvision041ReviewActorsAsync(publication.WorkAssignmentId, actorPassword, ct);
            // The generic P10 core seed contains one nonphysical export stub.
            // Remove only that exact owned stub; all diagnostic exports below
            // are created through the real API and have physical owner pins.
            var legacyStub = await RequireDatabase().GetCollection<BsonDocument>("work_report_statistic_exports")
                .DeleteOneAsync(new BsonDocument { ["_id"] = ObjectId.Parse(Fixture().P9ExportId),
                    ["storageKey"] = "p10/autonomous/p9-export.csv", ["byteCount"] = 0L }, ct);
            HarnessAssert.Equal(1L, legacyStub.DeletedCount, "Exact nonphysical core export stub removed");
            HarnessAssert.True(publication.DynamicFormFamilyId != publication.DynamicFormVersionId,
                "Independent fixture must exercise distinct form family/version ids.");
            await DiagnosticPrerequisiteAsync("P9_READY", new { publication.P9RunId, publication.P9GenerationId,
                publication.P9GenerationHash, publication.ConfigHash, publication.DynamicFormFamilyId,
                publication.DynamicFormVersionId, expectedBroadRows = 2, expectedSelectedRows = 1 }, ct);
            if (_diagnosticGroup == "EVIDENCE_PERMISSION")
            {
                _diagnosticStage = "DISTINCT_FIELD_LABEL_BINDING_SETUP";
                await DiagnosticCreateEvidenceMetricLabelsAsync(ct);
                publication = await DiagnosticPublishSuccessorAsync(publication, true, ct);
                await DiagnosticPrerequisiteAsync("DISTINCT_FIELD_LABEL_BINDING_READY", new {
                    fieldKey = P10ProductionDirectFieldKey,
                    statisticLabelCode = DiagnosticPrimaryStatisticLabelCode,
                    publication.P9RunId, publication.P9GenerationId, publication.ConfigRevision,
                    publication.ConfigHash }, ct);
            }
            StatisticReconciliationRun? reconciliation = null;
            if (_diagnosticGroup is not "STAT_EXPORT" and not "INITIAL_RECON")
            {
                _diagnosticStage = "INITIAL_MATCHED_SETUP";
                reconciliation = await DiagnosticCreateReconciliationAsync(publication, ct);
                await DiagnosticPrerequisiteAsync("INITIAL_MATCHED", new { reconciliation.Id, reconciliation.Status,
                    reconciliation.CurrentGenerationId, reconciliation.CurrentGenerationHash }, ct);
            }
            if (_diagnosticGroup is "STALE_RECHECK" or "MATCHED_SUCCESSOR")
            {
                _diagnosticStage = "SUCCESSOR_AND_CONFIG_DRIFT_SETUP";
                publication = await DiagnosticPublishSuccessorAsync(publication, false, ct);
                if (_diagnosticGroup == "MATCHED_SUCCESSOR") await DiagnosticExportAsync(publication, ct);
                await DiagnosticSaveConfigAsync(publication, true, ct);
                await DiagnosticPrerequisiteAsync("RECOVERY_READY", new { reconciliation!.Id,
                    publication.P9RunId, publication.P9GenerationId, publication.ConfigHash,
                    nextRecheckExpectedStatus = "STALE" }, ct);
                if (_diagnosticGroup == "MATCHED_SUCCESSOR")
                {
                    _diagnosticStage = "STALE_SETUP";
                    reconciliation = await DiagnosticRecheckAsync(publication, reconciliation!, "STALE", ct);
                    await DiagnosticRecordStaleBaseEvidenceAsync(reconciliation, ct);
                    _diagnosticStage = "DERIVER_COMPARISON_BINDING_SETUP";
                    await DiagnosticDeriverComparisonBindingAsync(reconciliation, ct);
                    _diagnosticStage = "STALE_REJECTION_SETUP";
                    HarnessAssert.True(reconciliation.StateRevision > 1, "Stale CAS probe needs a positive older revision");
                    await DiagnosticRejectRecheckAsync("STALE_CAS_REJECTED", publication, reconciliation,
                        reconciliation.StateRevision - 1, HttpStatusCode.Conflict,
                        "STAT_RECONCILIATION_REVISION_CONFLICT", "RECHECK_BEGIN_CAS_MISMATCH", ct);
                    await DiagnosticRejectRecheckAsync("STALE_SAME_P9_REJECTED", publication, reconciliation,
                        reconciliation.StateRevision, HttpStatusCode.Conflict,
                        "STAT_RECONCILIATION_JOB_CONFLICT", "RECHECK_P9_SUCCESSOR_NOT_DISTINCT", ct);
                    publication = await DiagnosticPublishSuccessorAsync(publication, null, ct);
                    _diagnosticStage = "FRESH_SUCCESSOR_EXPORT_REJECTION_SETUP";
                    await DiagnosticRejectRecheckAsync("FRESH_SUCCESSOR_MISSING_EXPORT_REJECTED", publication, reconciliation,
                        reconciliation.StateRevision, HttpStatusCode.BadRequest,
                        "STAT_RECONCILIATION_REQUEST_INVALID", "RECHECK_EXPORT_OWNER_CARDINALITY_INVALID", ct);
                    await DiagnosticPrerequisiteAsync("MATCHED_SUCCESSOR_READY", new { publication.P9RunId,
                        publication.P9GenerationId, nextRecheckExpectedStatus = "MATCHED" }, ct);
                }
            }
            _diagnosticStage = "READY";
            var workBase = $"/works/{publication.WorkId}/statistics/{publication.WorkAssignmentId}";
            var query = new Dictionary<string, string?>
            {
                ["dynamicFormTemplateId"] = publication.DynamicFormVersionId, ["scopeType"] = "ASSIGNMENT",
                ["periodKey"] = publication.PeriodKey, ["periodInstanceKey"] = publication.PeriodInstanceKey,
                ["resultHash"] = publication.P9GenerationHash, ["configHash"] = publication.ConfigHash,
                ["sourceHash"] = publication.SourcePayloadHash, ["lifecycleRevision"] = publication.SourceLifecycleRevision.ToString()
            };
            var resultRoute = origin.GetLeftPart(UriPartial.Authority) + workBase + "/results/DIRECT_FIELD/" +
                publication.P9GenerationId + "?" + string.Join("&", query.Where(x => x.Value is not null)
                    .Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value!)));
            JsonNode? originalFilters = null;
            if (reconciliation?.ActualCapturePlan?.Export is { } initialExport)
            {
                var artifact = await RequireDatabase().GetCollection<StatRunExportArtifact>("work_report_statistic_exports")
                    .Find(x => x.Id == initialExport.ExportId).SingleAsync(ct);
                originalFilters = JsonNode.Parse(artifact.CanonicalFilterJson ?? throw new InvalidOperationException("Original export canonical filter absent."));
            }
            var expectedFullRowCount = await RequireDatabase().GetCollection<WorkReportFieldStatValue>("work_report_field_stat_values")
                .CountDocumentsAsync(x => x.DirectProjection != null && x.DirectProjection.RunId == publication.P9RunId &&
                    x.DirectProjection.GenerationId == publication.P9GenerationId && !x.IsDeleted, cancellationToken: ct);
            var backendDllPath = _paths.ResolveBackendDll();
            var evidenceMetricBinding = _diagnosticGroup == "EVIDENCE_PERMISSION"
                ? await DiagnosticEvidenceMetricBindingAsync(publication, ct)
                : null;
            var ready = new
            {
                schemaVersion = "P11_INDEPENDENT_DIAGNOSTIC_FIXTURE_READY_V1",
                fixtureImplementation = "P11_INDEPENDENT_DIAGNOSTIC_FIXTURE_ATTEMPT046_V1",
                fixtureId = _runKey, backendRunId = _runKey, group = _diagnosticGroup,
                stage = _diagnosticStage, createdAtUtc = DateTime.UtcNow,
                runRoot = _diagnosticOutput, leaseRunRoot = _paths.RunRoot,
                apiBaseUrl = RequireBackend().BaseUri.ToString(), backendBaseUrl = RequireBackend().BaseUri.ToString(),
                frontendOrigin = origin.GetLeftPart(UriPartial.Authority),
                processes = new { fixturePid = Environment.ProcessId, backendPid = RequireBackend().ProcessId, mongoPid = RequireMongo().ProcessId },
                ports = new { backend = RequireBackend().Port, mongo = RequireMongo().Port },
                backendDll = DiagnosticPin(backendDllPath),
                runtimeFiles = new[] { backendDllPath, Path.ChangeExtension(backendDllPath, ".deps.json"),
                    Path.ChangeExtension(backendDllPath, ".runtimeconfig.json") }.Select(DiagnosticPin).ToArray(),
                originalSelector = originalFilters is null ? null : new { filters = originalFilters },
                expectedFullRowCount,
                evidenceMetricBinding,
                ids = new { formFamilyId = publication.DynamicFormFamilyId, formVersionId = publication.DynamicFormVersionId,
                    formFieldId = P10ProductionDirectFieldId, formFieldKey = P10ProductionDirectFieldKey,
                    pagingFieldId = P10ProductionDirectSecondFieldId, pagingFieldKey = P10ProductionDirectSecondFieldKey,
                    workId = publication.WorkId, assignmentId = publication.WorkAssignmentId, reportId = publication.SourceReportId,
                    conceptKey = P10ProductionDirectFieldKey, grain = "MONTH", periodKey = publication.PeriodKey,
                    periodInstanceKey = publication.PeriodInstanceKey, reconciliationPeriodKey = publication.PeriodKey,
                    reconciliationPeriodInstanceKey = publication.PeriodInstanceKey, reconciliationId = reconciliation?.Id,
                    ownerId = Actor("executor").Id, reviewerId = Actor("executor2").Id, adminId = Actor("admin").Id,
                    reviewerIds = Diagnostic043ReviewerSeeds.ToDictionary(item => item.Gate, item => Actor(item.Alias).Id) },
                statTerminal = new { generationId = publication.P9GenerationId, generationHash = publication.P9GenerationHash,
                    resultHash = publication.P9GenerationHash, configHash = publication.ConfigHash, configRevision = publication.ConfigRevision,
                    sourceHash = publication.SourcePayloadHash, lifecycleRevision = publication.SourceLifecycleRevision,
                    projectionRunId = publication.P9RunId, resultId = publication.P9ResultId,
                    dynamicFormTemplateId = publication.DynamicFormVersionId, scopeType = "ASSIGNMENT", scopeId = publication.WorkAssignmentId,
                    workId = publication.WorkId, periodKey = publication.PeriodKey, periodInstanceKey = publication.PeriodInstanceKey,
                    status = "DONE", freshnessState = "FRESH" },
                routes = new { result = resultRoute, reconciliation = reconciliation is null ? null :
                    origin.GetLeftPart(UriPartial.Authority) + workBase + "/reconciliations/" + reconciliation.Id,
                    reconciliationList = origin.GetLeftPart(UriPartial.Authority) + workBase + "/reconciliations" },
                reconciliation = reconciliation is null ? null : new { id = reconciliation.Id, status = reconciliation.Status,
                    expectedTerminalStatus = _diagnosticGroup == "STALE_RECHECK" ? "STALE" : "MATCHED",
                    stateRevision = reconciliation.StateRevision, stateHash = reconciliation.StateHash,
                    originalSelector = originalFilters is null ? null : new { filters = originalFilters } },
                prerequisites = _diagnosticPrerequisites, testedCaseCreditGranted = false,
                scheduler = new { hangfireServerEnabled = true, recurringRegistrationEnabled = true, manualWorkerClaimUsed = false }
            };
            object ActorSecret(string alias)
            {
                var actor = Actor(alias);
                // Core bootstrap retains the admin's bootstrap password; it is
                // not rebound to the normal actors' password on backend restart.
                var password = alias == "admin"
                    ? _bootstrapPassword ?? throw new InvalidOperationException("Diagnostic admin password is unavailable.")
                    : actorPassword;
                return new { username = actor.Username, password, userId = actor.Id };
            }
            P11ServerExportCleanup.WriteExclusive(secretPath, new { schemaVersion = "P11_DIAGNOSTIC_RUNTIME_SECRET_V1",
                fixtureImplementation = "P11_INDEPENDENT_DIAGNOSTIC_FIXTURE_ATTEMPT046_V1",
                fixtureId = _runKey, backendRunId = _runKey, group = _diagnosticGroup,
                actors = new { owner = ActorSecret("executor"), reviewer = ActorSecret("executor2"), admin = ActorSecret("admin"),
                    reviewers = Diagnostic043ReviewerSeeds.ToDictionary(item => item.Gate, item => ActorSecret(item.Alias)) } });
            await File.WriteAllTextAsync(Path.Combine(_diagnosticOutput, "readiness.public.json"), JsonSerializer.Serialize(ready, DiagnosticJson), ct);
            Console.WriteLine("P11_DIAGNOSTIC_READY=" + Path.Combine(_diagnosticOutput, "readiness.public.json"));
            while (true)
            {
                var line = await Console.In.ReadLineAsync(ct);
                if (line is null) break;
                var command = JsonNode.Parse(line)?.AsObject() ?? throw new InvalidOperationException("Empty diagnostic control.");
                if (command["action"]?.GetValue<string>() == "STOP") break;
                if (command["action"]?.GetValue<string>() != "REVOKE_REVIEWER" || _diagnosticGroup != "EVIDENCE_PERMISSION")
                    throw new InvalidOperationException("Diagnostic control action not allowed for group.");
                var commandId = command["commandId"]?.GetValue<string>() ?? "";
                if (!Regex.IsMatch(commandId, "^[a-zA-Z0-9_-]{1,60}$")) throw new InvalidOperationException("Invalid control command id.");
                var disabled = await RequireApi().DeleteAsync("api/admin/users/" + Actor("executor2").Id,
                    Actor("admin").Token, ct: ct);
                ApiHarnessClient.ExpectStatus(disabled, HttpStatusCode.NoContent, "Independent real admin user revocation");
                var revokedUser = await RequireDatabase().GetCollection<AppUser>("users")
                    .Find(user => user.Id == Actor("executor2").Id).SingleAsync(ct);
                HarnessAssert.True(revokedUser.IsDeleted, "Independent exact revoked actor persisted");
                var controlPath = Path.Combine(_diagnosticOutput, "control-" + commandId + ".receipt.json");
                P11ServerExportCleanup.WriteExclusive(controlPath, new { schemaVersion = "P11_DIAGNOSTIC_CONTROL_RECEIPT_V1",
                    fixtureId = _runKey, backendRunId = _runKey, group = _diagnosticGroup, commandId,
                    action = "REVOKE_REVIEWER", status = "PASS", userId = Actor("executor2").Id,
                    mechanism = "ADMIN_USER_DELETE_API", httpStatus = 204,
                    testedCaseCreditGranted = false });
                Console.WriteLine("P11_DIAGNOSTIC_CONTROL=" + controlPath);
            }
        }
        catch (Exception error)
        {
            failure = error;
            var text = error.GetType().Name + ": " + error.Message;
            foreach (var secret in _artifactSecrets.Where(x => !string.IsNullOrEmpty(x))) text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            P11ServerExportCleanup.WriteExclusive(Path.Combine(_diagnosticOutput, "setup-failure.public.json"),
                new { schemaVersion = "P11_DIAGNOSTIC_SETUP_FAILURE_V1", fixtureId = _runKey, backendRunId = _runKey,
                    group = _diagnosticGroup, stage = _diagnosticStage, status = "SETUP_FAIL", error = text,
                    prerequisites = _diagnosticPrerequisites, testedCaseCreditGranted = false });
            Console.Error.WriteLine("P11_DIAGNOSTIC_SETUP_FAIL=" + _diagnosticStage);
        }
        finally
        {
            try { if (File.Exists(secretPath)) File.Delete(secretPath); } catch (Exception error) { errors.Add("secret-cleanup:" + error.Message); }
            await StopBackendAsync(errors);
            // Evidence observation is separate from physical cleanup success.
            var terminalOutcome = await DiagnosticCaptureTerminalOutcomeAsync(
                _backend is null || _backend.StopVerified);
            if (_database is not null)
            {
                try { exports = await P11ServerExportCleanup.CaptureAndDeleteAsync(_paths, _database,
                    Path.Combine(_diagnosticOutput, "server-export-cleanup.json"),
                    _backend is null || _backend.StopVerified, CancellationToken.None); }
                catch (Exception error) { errors.Add("server-export-cleanup:" + error.Message); }
            }
            await CleanupMongoAsync(errors);
            var processes = (_backend is not null && !_backend.StopVerified ? 1 : 0) + (_mongo is not null && !_mongo.ProcessStopVerified ? 1 : 0);
            var listeners = (_backend is not null && !_backend.PortReleaseVerified ? 1 : 0) + (_mongo is not null && !_mongo.PortReleaseVerified ? 1 : 0);
            var resources = (_mongo is not null && !_mongo.DatabaseDropVerified ? 1 : 0) +
                (_mongo is not null && !_mongo.DataDirectoryRemovalVerified ? 1 : 0) + (File.Exists(secretPath) ? 1 : 0) +
                (exports is { RootAbsent: false } || errors.Any(x => x.StartsWith("server-export-cleanup:")) ? 1 : 0);
            P11ServerExportCleanup.WriteExclusive(Path.Combine(_diagnosticOutput, "cleanup.receipt.json"), new
            {
                schemaVersion = "P11_INDEPENDENT_DIAGNOSTIC_CLEANUP_V1", fixtureId = _runKey, backendRunId = _runKey,
                group = _diagnosticGroup, status = errors.Count == 0 && processes + listeners + resources == 0 ? "PASS" : "FAIL",
                remainingOwnedProcesses = processes, remainingListeners = listeners, remainingFixtureResources = resources,
                errors, testedCaseCreditGranted = false, serverExports = exports, terminalOutcome,
                databaseDropped = _mongo?.DatabaseDropVerified ?? true, mongoDataRemoved = _mongo?.DataDirectoryRemovalVerified ?? true,
                runtimeSecretAbsent = !File.Exists(secretPath), backendPid = _backend?.ProcessId, mongoPid = _mongo?.ProcessId,
                backendPort = _backend?.Port, mongoPort = _mongo?.Port, fixtureProcessExitsAfterReceipt = true,
                leaseRunRoot = _paths.RunRoot
            });
        }
        return failure is null && errors.Count == 0 ? 0 : 1;
    }

    private async Task DiagnosticProvision041ReviewActorsAsync(string assignmentId, string actorPassword, CancellationToken ct)
    {
        var admin = Actor("admin");
        var owner = Actor("executor");
        var now = DateTime.UtcNow;
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
        var users = RequireDatabase().GetCollection<AppUser>("users");
        var hasher = new PasswordHasher<AppUser>();
        var seeds = Diagnostic043ReviewerSeeds.Select(item =>
        {
            var seed = NewActor(item.Alias, $"p11_04_{item.Alias}_{nonce}".ToLowerInvariant(),
                $"P11-04 {item.Alias} {nonce}", owner.UnitId, ["MANAGER_LEVEL"], admin.Id, now);
            seed.User.PositionCode = "SPECIALIST";
            seed.User.PasswordHash = hasher.HashPassword(seed.User, actorPassword);
            return seed;
        }).ToArray();
        HarnessAssert.True(seeds.Select(seed => seed.User.Id).Distinct(StringComparer.Ordinal).Count() == 5 &&
            seeds.All(seed => seed.User.Id != owner.Id && seed.User.Id != Actor("executor2").Id && seed.User.Id != admin.Id),
            "Diagnostic five review actors must be distinct from each other, owner, export reviewer and admin.");
        await users.InsertManyAsync(seeds.Select(seed => seed.User), cancellationToken: ct);
        _cleanupHandles.AddRange(seeds.Select(seed => new P10CleanupHandle("users", seed.User.Id)));
        foreach (var seed in seeds)
        {
            var token = await RequireApi().LoginAsync(seed.User.Username, actorPassword, ct);
            RememberSecret(token);
            _actors.Add(seed.Key, new P10Actor(seed.Key, seed.User.Id, seed.User.Username,
                seed.User.UnitId ?? string.Empty, seed.User.AccountKind ?? "NORMAL_USER", seed.User.Roles, token));
        }
        // Match the official fixture's exact prerequisite assignment ACL grant.
        // No review, verdict, generation, export or lifecycle rows are created.
        var reviewerIds = seeds.Select(seed => seed.User.Id).ToArray();
        var grant = await RequireDatabase().GetCollection<WorkAssignment>("work_assignments")
            .UpdateOneAsync(item => item.Id == assignmentId && !item.IsDeleted,
                Builders<WorkAssignment>.Update.AddToSetEach(item => item.LeaderWatcherUserIds, reviewerIds),
                cancellationToken: ct);
        HarnessAssert.Equal(1L, grant.MatchedCount, "Diagnostic exact reviewer assignment grant target");
    }

    private async Task<object> DiagnosticCaptureTerminalOutcomeAsync(bool backendStopped)
    {
        var rows = new List<Dictionary<string, object?>>();
        var state = _database is null ? "DATABASE_UNAVAILABLE" : "NOT_CAPTURED";
        string? errorType = null, errorCode = null;
        try
        {
            if (_database is not null && backendStopped)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var ct = timeout.Token;
                var runs = await _database.GetCollection<StatisticReconciliationRun>(RunCollection)
                    .Find(FilterDefinition<StatisticReconciliationRun>.Empty)
                    .SortBy(value => value.Id).Limit(25).ToListAsync(ct);
                // Retain every bounded scalar snapshot before optional lineage reads can fail.
                rows.AddRange(runs.Select(DiagnosticOutcomeRunRow));
                if (runs.Count >= 25) throw new InvalidOperationException("DIAGNOSTIC_OUTCOME_RUN_LIMIT");
                for (var index = 0; index < runs.Count; index++)
                {
                    var run = runs[index];
                    await DiagnosticCaptureOutcomeLineageAsync(rows[index], run,
                        token => _database.GetCollection<StatisticReconciliationReview>(
                                "work_report_statistic_reconciliation_reviews")
                            .Find(value => value.ReconciliationId == run.Id &&
                                value.RecordKind == StatisticReconciliationReviewKinds.FinalVerdict)
                            .SortBy(value => value.CreatedAtUtc).Limit(25).ToListAsync(token), ct);
                }
                state = "CAPTURED";
            }
            else if (!backendStopped) state = "BACKEND_NOT_STOPPED";
        }
        catch (Exception error)
        {
            state = "CAPTURE_FAILED";
            errorType = error.GetType().Name;
            errorCode = DiagnosticOutcomeExceptionCode(error);
        }
        // Freeze the same camel-case public element for the file and cleanup receipt.
        var outcome = DiagnosticOutcomePublicElement(new {
            schemaVersion = "P11_DIAGNOSTIC_TERMINAL_OUTCOME_V1",
            fixtureId = _runKey, group = _diagnosticGroup, observedAtUtc = DateTime.UtcNow,
            state, backendStopped, readBeforeDatabaseDrop = true, rows, errorType, errorCode,
            evidenceObservationOnly = true, rawRunOrVerdictDocumentsRetained = false,
            secretsOrLeaseTokensRetained = false, physicalCleanupVerdictIndependent = true,
            testedCaseCreditGranted = false
        });
        try
        {
            P11ServerExportCleanup.WriteExclusive(
                Path.Combine(_diagnosticOutput, "outcome-snapshot.public.json"), outcome);
        }
        catch (Exception error)
        {
            // The fully projected observation is still bound inside cleanup.receipt.json.
            return new { snapshotFileState = "WRITE_FAILED", errorType = error.GetType().Name, outcome };
        }
        return new { snapshotFileState = "WRITTEN", outcome };
    }

    private static Dictionary<string, object?> DiagnosticOutcomeRunRow(StatisticReconciliationRun run)
    {
        var row = new Dictionary<string, object?> {
            ["runState"] = DiagnosticOutcomeRunProjection(run),
            ["receiptPresent"] = run.CurrentRecheckFinalizeReceipt is not null,
            ["receiptValid"] = null, ["receipt"] = null,
            ["lineageState"] = "NOT_READ", ["lineage"] = Array.Empty<object>(),
            ["lineageValidationErrors"] = Array.Empty<object>(), ["currentVerdictCount"] = null,
            // The setup lifecycle probe remains separate; its reader can ensure indexes.
            ["lifecycle"] = new { state = "NOT_READ_POST_STOP_OBSERVER", readAttempted = false }
        };
        if (run.CurrentRecheckFinalizeReceipt is { } receipt)
        {
            try
            {
                var valid = StatisticReconciliationRunService.HasValidRecheckFinalizeReceipt(run);
                row["receiptValid"] = valid;
                row["receipt"] = valid ? DiagnosticOutcomeReceiptProjection(receipt) :
                    new { state = "REDACTED_INVALID_RECEIPT" };
            }
            catch (Exception error)
            {
                row["receiptValid"] = false;
                row["receipt"] = new { state = "REDACTED_INVALID_RECEIPT",
                    errorType = error.GetType().Name, errorCode = DiagnosticOutcomeExceptionCode(error) };
            }
        }
        return row;
    }

    private static async Task DiagnosticCaptureOutcomeLineageAsync(
        Dictionary<string, object?> row, StatisticReconciliationRun run,
        Func<CancellationToken, Task<List<StatisticReconciliationReview>>> readLineage,
        CancellationToken ct)
    {
        row["lineageState"] = "READING";
        try
        {
            var lineage = await readLineage(ct);
            if (lineage.Count >= 25) throw new InvalidOperationException("DIAGNOSTIC_OUTCOME_LINEAGE_LIMIT");
            var projected = new List<object>();
            var validationErrors = new List<object>();
            var currentVerdictCount = 0;
            for (var index = 0; index < lineage.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var verdict = lineage[index];
                try
                {
                    StatisticReconciliationFinalVerdictPublisher.ValidateStored(verdict);
                    projected.Add(new { index, validationPassed = true,
                        verdict = DiagnosticOutcomeVerdictProjection(verdict) });
                    if (DiagnosticSafeOutcomeSha(run.CurrentGenerationId) is not null &&
                        DiagnosticSafeOutcomeSha(run.CurrentGenerationHash) is not null &&
                        verdict.ActualGenerationId == run.CurrentGenerationId &&
                        verdict.ActualGenerationSha256 == run.CurrentGenerationHash)
                        currentVerdictCount++;
                }
                catch (Exception error)
                {
                    // Invalid stored documents contribute only their bounded list index.
                    var failure = new { index, errorType = error.GetType().Name,
                        errorCode = DiagnosticOutcomeExceptionCode(error) };
                    validationErrors.Add(failure);
                    projected.Add(new { index, validationPassed = false, state = "REDACTED_INVALID_VERDICT" });
                }
            }
            row["lineage"] = projected;
            row["lineageValidationErrors"] = validationErrors;
            row["currentVerdictCount"] = currentVerdictCount;
            row["currentVerdictCountIncludesOnlyValidatedRecords"] = true;
            row["lineageState"] = validationErrors.Count == 0 ? "READ_VALID" : "READ_WITH_INVALID_VERDICTS";
        }
        catch (Exception error)
        {
            row["lineageState"] = "READ_FAILED";
            row["lineageError"] = new { errorType = error.GetType().Name,
                errorCode = DiagnosticOutcomeExceptionCode(error) };
            throw;
        }
    }

    private static JsonElement DiagnosticOutcomePublicElement(object value) =>
        JsonSerializer.SerializeToElement(value, DiagnosticJson);

    private static string? DiagnosticSafeOutcomeSha(string? value) =>
        value is not null && Regex.IsMatch(value, @"\A[a-f0-9]{64}\z") ? value : null;

    private static string? DiagnosticSafeOutcomeObjectId(string? value) =>
        value is not null && Regex.IsMatch(value, @"\A[a-f0-9]{24}\z") ? value : null;

    private static long? DiagnosticSafeOutcomeCount(long value) => value >= 0 ? value : null;

    private static string? DiagnosticSafeOutcomeCode(string? value) =>
        value is null ? null : Regex.IsMatch(value, @"\AP10_[A-Z0-9_]{1,96}\z") ? value : "NON_PUBLIC_CODE";

    private static string DiagnosticOutcomeExceptionCode(Exception error) => error switch {
        OperationCanceledException => "DIAGNOSTIC_OUTCOME_CANCELLED",
        InvalidOperationException { Message: "DIAGNOSTIC_OUTCOME_RUN_LIMIT" } => "DIAGNOSTIC_OUTCOME_RUN_LIMIT",
        InvalidOperationException { Message: "DIAGNOSTIC_OUTCOME_LINEAGE_LIMIT" } => "DIAGNOSTIC_OUTCOME_LINEAGE_LIMIT",
        StatisticReconciliationFinalVerdictException verdict =>
            DiagnosticSafeOutcomeCode(verdict.Code) ?? "NON_PUBLIC_CODE",
        _ => DiagnosticLifecycleExceptionCode(error)
    };

    private static object DiagnosticOutcomeRunProjection(StatisticReconciliationRun run) => new {
        id = DiagnosticSafeOutcomeObjectId(run.Id),
        status = StatisticReconciliationRunStatuses.All.Contains(run.Status) ? run.Status : "NON_PUBLIC_STATUS",
        stateRevision = DiagnosticSafeOutcomeCount(run.StateRevision),
        stateHash = DiagnosticSafeOutcomeSha(run.StateHash),
        diagnosticCode = DiagnosticSafeOutcomeCode(run.DiagnosticCode),
        retryCount = DiagnosticSafeOutcomeCount(run.RetryCount),
        maxRetryCount = DiagnosticSafeOutcomeCount(run.MaxRetryCount),
        generationPublishRevision = DiagnosticSafeOutcomeCount(run.GenerationPublishRevision),
        currentGenerationId = DiagnosticSafeOutcomeSha(run.CurrentGenerationId),
        currentGenerationHash = DiagnosticSafeOutcomeSha(run.CurrentGenerationHash),
        pendingGenerationId = DiagnosticSafeOutcomeSha(run.PendingGenerationId),
        pendingGenerationHash = DiagnosticSafeOutcomeSha(run.PendingGenerationHash),
        activeRecheck = run.Recheck is not null,
        recheckPhase = run.Recheck is null ? null :
            StatisticReconciliationRecheckPhases.All.Contains(run.Recheck.Phase) ? run.Recheck.Phase : "NON_PUBLIC_PHASE",
        hasLease = run.LeaseUntilUtc is not null
    };

    private static object DiagnosticOutcomeReceiptProjection(StatisticReconciliationRecheckFinalizeReceipt receipt) => new {
        schemaVersion = receipt.SchemaVersion is "P10_RECHECK_FINALIZE_RECEIPT_V1" or "P10_RECHECK_FINALIZE_RECEIPT_V2"
            ? receipt.SchemaVersion : "NON_PUBLIC_SCHEMA",
        terminalStatus = StatisticReconciliationRecheckTerminalStatuses.All.Contains(receipt.TerminalStatus)
            ? receipt.TerminalStatus : "NON_PUBLIC_STATUS",
        receiptSha256 = DiagnosticSafeOutcomeSha(receipt.ReceiptSha256),
        captureBindingSha256 = DiagnosticSafeOutcomeSha(receipt.CaptureBindingSha256),
        baseActualGenerationId = DiagnosticSafeOutcomeSha(receipt.BaseActualGenerationId),
        baseActualGenerationSha256 = DiagnosticSafeOutcomeSha(receipt.BaseActualGenerationSha256),
        baseVerdictGenerationId = DiagnosticSafeOutcomeSha(receipt.BaseVerdictGenerationId),
        baseVerdictGenerationSha256 = DiagnosticSafeOutcomeSha(receipt.BaseVerdictGenerationSha256),
        successorActualGenerationId = DiagnosticSafeOutcomeSha(receipt.SuccessorActualGenerationId),
        successorActualGenerationSha256 = DiagnosticSafeOutcomeSha(receipt.SuccessorActualGenerationSha256),
        successorVerdictGenerationId = DiagnosticSafeOutcomeSha(receipt.SuccessorVerdictGenerationId),
        successorVerdictGenerationSha256 = DiagnosticSafeOutcomeSha(receipt.SuccessorVerdictGenerationSha256)
    };

    private static object DiagnosticOutcomeVerdictProjection(StatisticReconciliationReview verdict) => new {
        verdictGenerationId = DiagnosticSafeOutcomeSha(verdict.VerdictGenerationId),
        verdictGenerationSha256 = DiagnosticSafeOutcomeSha(verdict.VerdictGenerationSha256),
        actualGenerationId = DiagnosticSafeOutcomeSha(verdict.ActualGenerationId),
        actualGenerationSha256 = DiagnosticSafeOutcomeSha(verdict.ActualGenerationSha256),
        expectedGenerationId = DiagnosticSafeOutcomeSha(verdict.ExpectedGenerationId),
        expectedGenerationSha256 = DiagnosticSafeOutcomeSha(verdict.ExpectedGenerationSha256),
        verdict = verdict.Verdict is "MATCHED" or "MISMATCHED" or "FAILED" ? verdict.Verdict : "NON_PUBLIC_STATUS",
        failureKind = verdict.FailureKind is "NONE" or "MISSING_EVIDENCE" or "TIMEOUT" or "INTERNAL"
            ? verdict.FailureKind : "NON_PUBLIC_CODE",
        rootCauseClass = verdict.RootCauseClass is null ? null :
            StatisticReconciliationRootCauseClasses.Ordered.Contains(verdict.RootCauseClass)
                ? verdict.RootCauseClass : "NON_PUBLIC_CODE",
        verdict.CompleteEvidence, verdict.AllRequiredLayersZero,
        verdict.MissingOrExtraIdentity, verdict.UnknownBlocksCloseout,
        verdict.Signable, verdict.CloseoutAllowed,
        comparisonBindingSha256 = DiagnosticSafeOutcomeSha(verdict.ComparisonBindingSha256),
        freshnessAssessmentSha256 = DiagnosticSafeOutcomeSha(verdict.FreshnessAssessmentSha256),
        rootCauseClassificationSha256 = DiagnosticSafeOutcomeSha(verdict.RootCauseClassificationSha256),
        documentSemanticSha256 = DiagnosticSafeOutcomeSha(verdict.DocumentSemanticSha256),
        supersedesVerdictGenerationId = DiagnosticSafeOutcomeSha(verdict.SupersedesVerdictGenerationId),
        supersedesVerdictGenerationSha256 = DiagnosticSafeOutcomeSha(verdict.SupersedesVerdictGenerationSha256)
    };

    private object DiagnosticPin(string path)
        => new { path, sha256 = P11ServerExportCleanup.Sha(File.ReadAllBytes(path)), bytes = new FileInfo(path).Length };
    private async Task DiagnosticPrerequisiteAsync(string id, object evidence, CancellationToken ct, string status = "PASS")
    {
        _diagnosticPrerequisites.Add(new { id, status, evidence, testedCaseCreditGranted = false });
        await File.WriteAllTextAsync(Path.Combine(_diagnosticOutput, "prerequisites.public.json"),
            JsonSerializer.Serialize(new { fixtureId = _runKey, group = _diagnosticGroup,
                prerequisites = _diagnosticPrerequisites, testedCaseCreditGranted = false }, DiagnosticJson), ct);
    }

    private async Task DiagnosticRecordStaleBaseEvidenceAsync(
        StatisticReconciliationRun reconciliation, CancellationToken ct)
    {
        var lineage = await RequireDatabase()
            .GetCollection<StatisticReconciliationReview>("work_report_statistic_reconciliation_reviews")
            .Find(value => value.ReconciliationId == reconciliation.Id &&
                value.RecordKind == StatisticReconciliationReviewKinds.FinalVerdict)
            .ToListAsync(ct);
        foreach (var verdict in lineage)
            StatisticReconciliationFinalVerdictPublisher.ValidateStored(verdict);
        var current = lineage.Where(value =>
                value.ActualGenerationId == reconciliation.CurrentGenerationId &&
                value.ActualGenerationSha256 == reconciliation.CurrentGenerationHash).ToArray();
        HarnessAssert.Equal(1, current.Length, "Diagnostic exact current verdict cardinality");
        var observed = current[0];
        var receipt = reconciliation.CurrentRecheckFinalizeReceipt;
        var captured = reconciliation.CurrentGenerationRecheckCaptureBinding;
        static object VerdictEvidence(StatisticReconciliationReview verdict) => new {
            verdict.VerdictGenerationId, verdict.VerdictGenerationSha256,
            verdict.ActualGenerationId, verdict.ActualGenerationSha256,
            verdict.ExpectedGenerationId, verdict.ExpectedGenerationSha256,
            verdict.Verdict, verdict.FailureKind, verdict.RootCauseClass,
            verdict.CompleteEvidence, verdict.AllRequiredLayersZero,
            verdict.MissingOrExtraIdentity, verdict.UnknownBlocksCloseout,
            verdict.Signable, verdict.CloseoutAllowed, verdict.ComparisonBindingSha256,
            verdict.FreshnessAssessmentSha256, verdict.RootCauseClassificationSha256,
            verdict.DocumentSemanticSha256, verdict.SupersedesVerdictGenerationId,
            verdict.SupersedesVerdictGenerationSha256
        };
        var lifecycleReadback = await DiagnosticLifecycleReadbackObservationAsync(reconciliation, ct);
        await DiagnosticPrerequisiteAsync("STALE_TERMINAL", new {
            lifecycleReadback,
            reconciliation.Id, reconciliation.Status,
            reconciliation.CurrentGenerationId, reconciliation.CurrentGenerationHash,
            reconciliation.StateRevision, reconciliation.StateHash,
            activeRecheck = reconciliation.Recheck is not null,
            hasPendingGeneration = reconciliation.PendingGenerationId is not null,
            currentVerdictValidated = true, currentVerdict = VerdictEvidence(observed),
            baseEligible = StatisticReconciliationRunService.IsFreshnessRecoveryRecheckBase(reconciliation, observed),
            presentationBaseEligible = StatisticReconciliationRunService.BuildPresentationRecheck(reconciliation, observed).BaseEligible,
            receiptValid = StatisticReconciliationRunService.HasValidRecheckFinalizeReceipt(reconciliation),
            receipt = receipt is null ? null : new {
                receipt.SchemaVersion, receipt.TerminalStatus, receipt.ReceiptSha256, receipt.CaptureBindingSha256,
                receipt.BaseActualGenerationId, receipt.BaseActualGenerationSha256,
                receipt.BaseVerdictGenerationId, receipt.BaseVerdictGenerationSha256,
                receipt.SuccessorActualGenerationId, receipt.SuccessorActualGenerationSha256,
                receipt.SuccessorVerdictGenerationId, receipt.SuccessorVerdictGenerationSha256,
                currentP8 = receipt.CurrentP8Configuration is null ? null : new {
                    receipt.CurrentP8Configuration.SchemaVersion,
                    receipt.CurrentP8Configuration.OwnerId, receipt.CurrentP8Configuration.ConfigId,
                    receipt.CurrentP8Configuration.VersionId, receipt.CurrentP8Configuration.VersionNo,
                    receipt.CurrentP8Configuration.Revision, receipt.CurrentP8Configuration.ConfigHash,
                    receipt.CurrentP8Configuration.BundleSha256, receipt.CurrentP8Configuration.SemanticSha256
                }
            },
            capturedP8 = captured is null ? null : new {
                ownerId = captured.P8ConfigOwnerId, configId = captured.P8ConfigId,
                versionId = captured.P8ConfigVersionId, versionNo = captured.P8ConfigVersionNo,
                revision = captured.P8ConfigRevision, configHash = captured.P8ConfigHash,
                bundleSha256 = captured.P8ConfigBundleHash,
                actualConfigurationBundleSha256 = captured.ActualConfigurationBundleSha256
            },
            lineageValidated = true,
            lineage = lineage.OrderBy(value => value.CreatedAtUtc)
                .ThenBy(value => value.VerdictGenerationId, StringComparer.Ordinal)
                .Select(VerdictEvidence).ToArray(),
            rawRunOrVerdictDocumentsRetained = false, evidenceProjectionOnly = true
        }, ct);
    }

    private async Task<object> DiagnosticLifecycleReadbackObservationAsync(
        StatisticReconciliationRun reconciliation, CancellationToken ct)
    {
        var stage = "ACTUAL_GENERATION_READ";
        var state = "NOT_READ";
        string? manifestSha256 = null, tupleSha256 = null, rowSetSha256 = null, resultSha256 = null;
        string? observedManifestSha256 = null, resultOutcome = null, resultRootCause = null;
        int? observationCount = null, observedObservationCount = null;
        bool? observedEvidenceComplete = null;
        var readbackPassed = false;
        var exceptions = new List<object>();
        try
        {
            var mongo = RequireMongo();
            var context = new tdtd_be.Data.MongoDbContext(
                Microsoft.Extensions.Options.Options.Create(new tdtd_be.Data.Infrastructure.MongoOptions {
                    ConnectionString = mongo.ConnectionString, Database = mongo.DatabaseName
                }));
            var actual = await StatisticReconciliationActualGenerationPublisher.ReadCompleteFromBackendAsync(
                new StatisticReconciliationActualObservationMongoBackend(context),
                reconciliation.Id, reconciliation.CurrentGenerationId!, ct);
            if (actual is null)
                state = "ACTUAL_GENERATION_MISSING";
            else
            {
                manifestSha256 = actual.LifecycleManifestSha256;
                observationCount = actual.LifecycleObservationCount;
                tupleSha256 = P11ServerExportCleanup.Sha(JsonSerializer.SerializeToUtf8Bytes(new {
                    actual.ReconciliationId, actual.GenerationId, actual.GenerationSemanticSha256,
                    manifestSha256, observationCount,
                    committedManifestSha256 = actual.CommittedRunBinding.LifecycleManifestSha256,
                    committedObservationCount = actual.CommittedRunBinding.LifecycleObservationCount
                }, DiagnosticJson));
                stage = "LIFECYCLE_BINDING";
                if (manifestSha256 is null || observationCount is not > 0 ||
                    actual.CommittedRunBinding.LifecycleManifestSha256 != manifestSha256 ||
                    actual.CommittedRunBinding.LifecycleObservationCount != observationCount)
                    state = "LIFECYCLE_BINDING_MISSING_OR_MISMATCHED";
                else
                {
                    stage = "EXACT_MANIFEST_READBACK";
                    var evidence = await new StatisticReconciliationActualLifecycleMongoLedger(context)
                        .ReadAndValidateByManifestAsync(actual.ReconciliationId, manifestSha256,
                            observationCount.Value, ct);
                    if (evidence is null)
                        state = "LIFECYCLE_EVIDENCE_MISSING";
                    else
                    {
                        observedManifestSha256 = evidence.ManifestSha256;
                        observedObservationCount = evidence.ObservationCount;
                        rowSetSha256 = evidence.Manifest.LifecycleRowSetSha256;
                        resultSha256 = evidence.Result.ResultSemanticSha256;
                        resultOutcome = evidence.Result.Outcome;
                        resultRootCause = evidence.Result.RootCause;
                        observedEvidenceComplete = evidence.Result.EvidenceComplete;
                        readbackPassed = observedManifestSha256 == manifestSha256 &&
                            observedObservationCount == observationCount &&
                            evidence.Rows.Length == observationCount;
                        state = readbackPassed ? "READBACK_PASS" : "READBACK_TUPLE_MISMATCH";
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            state = "READBACK_EXCEPTION";
            for (Exception? item = error; item is not null && exceptions.Count < 8; item = item.InnerException)
                exceptions.Add(new { type = item.GetType().Name, code = DiagnosticLifecycleExceptionCode(item) });
        }
        return new {
            stage, state, readbackPassed, manifestSha256, observationCount, tupleSha256,
            observedManifestSha256, observedObservationCount, rowSetSha256, resultSha256,
            resultOutcome, resultRootCause, observedEvidenceComplete,
            exceptions, observationOnly = true, lifecycleValidityFabricated = false,
            rawExceptionMessagesRetained = false, rawPayloadsRetained = false,
            testedCaseCreditGranted = false
        };
    }

    private static string DiagnosticLifecycleExceptionCode(Exception error)
    {
        if (error is StatisticReconciliationLifecycleException lifecycle &&
            Regex.IsMatch(lifecycle.Code, "^[A-Z0-9_]{1,100}$"))
            return lifecycle.Code;
        if (Regex.IsMatch(error.Message, "^P10_[A-Z0-9_]{1,100}$"))
            return error.Message;
        return error.Message switch
        {
            "P10 lifecycle observation count is outside bounds." => "OBSERVATION_COUNT_OUT_OF_BOUNDS",
            "P10 lifecycle observation cardinality drifted." => "OBSERVATION_CARDINALITY_DRIFT",
            "P10 lifecycle manifest binding drifted." => "MANIFEST_BINDING_DRIFT",
            "P10 lifecycle manifest cardinality is ambiguous." => "MANIFEST_CARDINALITY_AMBIGUOUS",
            "Lifecycle manifest cardinality or identity is invalid." => "MANIFEST_IDENTITY_INVALID",
            "Lifecycle row-set hash is invalid." => "ROW_SET_HASH_INVALID",
            "Lifecycle manifest row is missing or ambiguous." => "MANIFEST_ROW_MISSING_OR_AMBIGUOUS",
            "Lifecycle result does not recompute from its persisted timeline." => "RESULT_TIMELINE_RECOMPUTE_MISMATCH",
            "Lifecycle evidence rows are empty." => "EVIDENCE_ROWS_EMPTY",
            "Lifecycle row integrity hash is invalid." => "ROW_INTEGRITY_HASH_INVALID",
            "Unsupported lifecycle row kind." => "ROW_KIND_UNSUPPORTED",
            "Lifecycle row payload is empty." => "ROW_PAYLOAD_EMPTY",
            "Lifecycle row payload is not canonical JSON." => "ROW_PAYLOAD_NOT_CANONICAL",
            _ => error switch {
                JsonException => "JSON_READBACK_INVALID",
                FormatException => "READBACK_FORMAT_INVALID",
                NotSupportedException => "READBACK_TYPE_UNSUPPORTED",
                _ => "UNCLASSIFIED_READBACK_EXCEPTION"
            }
        };
    }

    private async Task DiagnosticDeriverComparisonBindingAsync(
        StatisticReconciliationRun reconciliation, CancellationToken ct)
    {
        var mongo = RequireMongo();
        var context = new tdtd_be.Data.MongoDbContext(
            Microsoft.Extensions.Options.Options.Create(new tdtd_be.Data.Infrastructure.MongoOptions {
                ConnectionString = mongo.ConnectionString, Database = mongo.DatabaseName
            }));
        var actual = await StatisticReconciliationActualGenerationPublisher.ReadCompleteFromBackendAsync(
            new StatisticReconciliationActualObservationMongoBackend(context),
            reconciliation.Id, reconciliation.CurrentGenerationId!, ct)
            ?? throw new InvalidOperationException("Diagnostic comparison regression requires committed actual generation");
        var exact = StatisticReconciliationTrustedVerdictPipeline.ExactExpectedBinding(actual);
        HarnessAssert.Equal(reconciliation.Id, exact.ReconciliationId, "Diagnostic expected reconciliation identity");
        var documents = await StatisticReconciliationExpectedMongoGenerationBindingReader.ReadGenerationAsync(
            context, reconciliation.Id, exact.GenerationId, ct);
        var expectedCommit = StatisticReconciliationExpectedObservationIntegrity.ValidateGeneration(documents);
        StatisticReconciliationExpectedMongoProjectionInputReader.RequireExactBinding(
            expectedCommit, expectedCommit.Commit ??
                throw new InvalidOperationException("Diagnostic expected commit missing"), exact);
        var expectedAtoms = documents.Where(value =>
                value.RecordKind == StatisticReconciliationObservationRecordKinds.ExpectedAtom)
            .Select(value => value.Atom ??
                throw new InvalidOperationException("Diagnostic expected atom missing"))
            .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(value => value.TransitionLeg, StringComparer.Ordinal)
            .ThenBy(value => value.TransitionKind, StringComparer.Ordinal)
            .ThenBy(value => value.AtomKind, StringComparer.Ordinal)
            .ThenBy(value => value.ValueIdentitySha256, StringComparer.Ordinal)
            .ToImmutableArray();
        var effective = StatisticReconciliationRecheckCaptureBindingCanonical.EffectiveCaptureRun(reconciliation);
        var originalRuntime = StatisticReconciliationTrustedVerdictDeriver.RunRuntimeBindingSha256(reconciliation);
        var effectiveRuntime = StatisticReconciliationTrustedVerdictDeriver.RunRuntimeBindingSha256(effective);
        var configPinsDiffer = reconciliation.ActualConfigurationBundleSha256 !=
            effective.ActualConfigurationBundleSha256;
        var runtimePinsDiffer = originalRuntime != effectiveRuntime;
        // This overload deliberately has no current-owner/lifecycle proof.
        // It exercises the actual comparison-pin consumer but grants no FRESH,
        // MATCHED, completeness, lifecycle or official journey credit.
        var observedRequest = StatisticReconciliationTrustedVerdictDeriver.Derive(
            reconciliation, expectedCommit, expectedAtoms, actual);
        var effectiveReferenceRequest = StatisticReconciliationTrustedVerdictDeriver.Derive(
            effective, expectedCommit, expectedAtoms, actual);
        var observed = observedRequest.RootCauseRequest?.Freshness ??
            throw new InvalidOperationException("Diagnostic actual deriver freshness absent");
        var reference = effectiveReferenceRequest.RootCauseRequest?.Freshness ??
            throw new InvalidOperationException("Diagnostic reference deriver freshness absent");
        var bindingEqual = observed.ExpectedBindingSha256 == reference.ExpectedBindingSha256;
        var capturedEqual = observed.CapturedPinsSha256 == reference.CapturedPinsSha256;
        var incompleteOnly = observed.ReasonCode == StatisticReconciliationFreshnessReasons.IncompleteCapture &&
            reference.ReasonCode == StatisticReconciliationFreshnessReasons.IncompleteCapture &&
            !observed.CompleteEvidence && !reference.CompleteEvidence &&
            !observed.MatchAllowed && !reference.MatchAllowed;
        var passed = (configPinsDiffer || runtimePinsDiffer) && bindingEqual && capturedEqual && incompleteOnly;
        await DiagnosticPrerequisiteAsync("ACTUAL_DERIVER_EFFECTIVE_COMPARISON_BINDING", new {
            actualConsumerExecuted = true, durableActualGenerationRead = true,
            durableExpectedGenerationValidated = true, originalComparisonPinsDiffer =
                configPinsDiffer || runtimePinsDiffer, configPinsDiffer, runtimePinsDiffer,
            expectedBindingSha256 = observed.ExpectedBindingSha256,
            effectiveReferenceExpectedBindingSha256 = reference.ExpectedBindingSha256,
            capturedPinsSha256 = observed.CapturedPinsSha256,
            effectiveReferenceCapturedPinsSha256 = reference.CapturedPinsSha256,
            bindingEqual, capturedEqual,
            observedState = observed.State, observedReason = observed.ReasonCode,
            referenceState = reference.State, referenceReason = reference.ReasonCode,
            completeEvidence = observed.CompleteEvidence,
            referenceCompleteEvidence = reference.CompleteEvidence,
            observedMatchAllowed = observed.MatchAllowed, referenceMatchAllowed = reference.MatchAllowed,
            matchCreditGranted = false,
            lifecycleProofFabricated = false, comparisonPinRegressionOnly = true
        }, ct, passed ? "PASS" : "FAIL");
        HarnessAssert.True(passed, "Actual deriver expected comparison binding must use effective captured pins; " +
            "pinsDiffer=" + (configPinsDiffer || runtimePinsDiffer) +
            "; expectedBindingEqual=" + bindingEqual + "; capturedEqual=" + capturedEqual +
            "; incompleteOnly=" + incompleteOnly);
    }

    private sealed record DiagnosticCollectionState(
        string Name, bool Exists, int DocumentCount, string DocumentsSha256);
    private sealed record DiagnosticBusinessState(
        string SchemaVersion, string Sha256, IReadOnlyList<DiagnosticCollectionState> Collections);

    private async Task<DiagnosticBusinessState> DiagnosticBusinessSnapshotAsync(CancellationToken ct)
    {
        var database = RequireDatabase();
        using var cursor = await database.ListCollectionNamesAsync(cancellationToken: ct);
        var existing = (await cursor.ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        // Include the whole owned family, including future family collections;
        // absent mandatory collections remain explicit so creation is detectable.
        var names = existing.Where(name =>
                name.StartsWith("work_report_statistic_reconcil", StringComparison.Ordinal))
            .Concat(new[] { RunCollection, "work_report_statistic_reconciliation_observations",
                "work_report_statistic_reconciliation_reviews", "work_report_statistic_reconciliation_exports" })
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var collections = new List<DiagnosticCollectionState>();
        foreach (var name in names)
        {
            var rows = existing.Contains(name)
                ? await database.GetCollection<BsonDocument>(name)
                    .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(ct)
                : [];
            var hashes = rows.Select(row => P11ServerExportCleanup.Sha(row.ToBson()))
                .Order(StringComparer.Ordinal).ToArray();
            collections.Add(new(name, existing.Contains(name), rows.Count,
                P11ServerExportCleanup.Sha(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", hashes)))));
        }
        var digest = P11ServerExportCleanup.Sha(
            JsonSerializer.SerializeToUtf8Bytes(collections, DiagnosticJson));
        return new("P11_RECONCILIATION_BUSINESS_SNAPSHOT_V1", digest, collections);
    }

    private async Task DiagnosticRejectRecheckAsync(
        string id, P10ProductionDirectFixturePins publication, StatisticReconciliationRun reconciliation,
        long expectedRevision, HttpStatusCode expectedStatus, string expectedCode,
        string expectedReason, CancellationToken ct)
    {
        var before = await DiagnosticBusinessSnapshotAsync(ct);
        var path = $"api/works/{publication.WorkId}/statistics/{publication.WorkAssignmentId}" +
            $"/reconciliations/{reconciliation.Id}/recheck";
        var response = await RequireApi().PostAsync(path, new {
            commandId = "diag-negative-" + Guid.NewGuid().ToString("N"),
            expectedStateRevision = expectedRevision, expectedStateHash = reconciliation.StateHash
        }, Actor("admin").Token, ct: ct);
        var after = await DiagnosticBusinessSnapshotAsync(ct);
        static string? MachineCode(JsonNode? node)
            => node is JsonValue value && value.TryGetValue<string>(out var text) &&
               Regex.IsMatch(text, "^[A-Z0-9_]{1,100}$") ? text : null;
        var body = response.Json as JsonObject;
        var details = body?["details"] as JsonObject;
        var code = MachineCode(body?["errorCode"]);
        var reason = MachineCode(details?["reason"]);
        int? declaredWrites = details?["writes"] is JsonValue writes &&
            writes.TryGetValue<int>(out var count) ? count : null;
        var unchanged = before.Sha256 == after.Sha256;
        var passed = response.StatusCode == expectedStatus && code == expectedCode &&
            reason == expectedReason && declaredWrites == 0 && unchanged;
        await DiagnosticPrerequisiteAsync(id, new {
            method = "POST", path, actorKind = "SYSTEM_ADMIN",
            httpStatus = (int)response.StatusCode, errorCode = code, reason,
            expectedHttpStatus = (int)expectedStatus, expectedErrorCode = expectedCode,
            expectedReason, declaredWrites,
            zeroWriteProof = new { status = unchanged ? "PASS" : "FAIL",
                reconciliationOwnedBusinessCollectionsUnchanged = unchanged,
                before, after, rawDocumentsRetained = false,
                excluded = new[] { "authentication audit", "infrastructure job audit" } },
            actualApiExecuted = true, acceptedExtraRecheck = response.StatusCode == HttpStatusCode.Accepted
        }, ct, passed ? "PASS" : "FAIL");
        HarnessAssert.True(passed, id + " rejected-command contract mismatch: http=" +
            (int)response.StatusCode + "; code=" + (code ?? "<absent>") +
            "; reason=" + (reason ?? "<absent>") + "; declaredWrites=" +
            (declaredWrites?.ToString() ?? "<absent>") + "; businessUnchanged=" + unchanged);
    }

    private async Task<string> DiagnosticExportAsync(P10ProductionDirectFixturePins p, CancellationToken ct)
    {
        var response = await RequireApi().PostAsync("api/stat-runs/exports", new
        {
            commandId = "diag-export-" + Guid.NewGuid().ToString("N"), format = "XLSX", resultKind = "DIRECT_FIELD",
            workId = p.WorkId, scopeType = "ASSIGNMENT", scopeId = p.WorkAssignmentId,
            periodInstanceKey = p.PeriodInstanceKey, resultId = p.P9RunId,
            expectedResultHash = p.P9GenerationHash, expectedConfigHash = p.ConfigHash,
            expectedSourceHash = p.SourcePayloadHash, expectedLifecycleRevision = p.SourceLifecycleRevision,
            filters = new { dynamicFormTemplateId = p.DynamicFormVersionId, fieldId = P10ProductionDirectFieldId,
                fieldKey = P10ProductionDirectFieldKey, blockId = (string?)null, metricKey = (string?)null,
                labelCode = (string?)null, periodKey = p.PeriodKey, bucketKey = (string?)null }
        }, Actor("executor").Token, ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Created, "Independent prerequisite exact-field export");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(response.Json!, "rowCount"), "Independent prerequisite exact-field export rows");
        return RequireResponseString(response, "exportId");
    }

    private async Task<StatisticReconciliationRun> DiagnosticCreateReconciliationAsync(P10ProductionDirectFixturePins p, CancellationToken ct)
    {
        var exportId = await DiagnosticExportAsync(p, ct);
        var body = new JsonObject { ["p9ResultKind"] = "DIRECT", ["p9ResultId"] = p.P9ResultId,
            ["p9RunId"] = p.P9RunId, ["conceptKey"] = P10ProductionDirectFieldKey, ["grain"] = "MONTH",
            ["filter"] = new JsonObject { ["periodInstanceKey"] = p.PeriodInstanceKey,
                ["fieldId"] = P10ProductionDirectFieldId, ["fieldKey"] = P10ProductionDirectFieldKey,
                ["periodKey"] = p.PeriodKey, ["bucketKey"] = null }, ["exportId"] = exportId };
        var url = $"api/works/{p.WorkId}/statistics/{p.WorkAssignmentId}/reconciliations";
        var plan = await RequireApi().PostAsync(url + "/capture-plan-preflight", body, Actor("executor").Token, ct: ct);
        ApiHarnessClient.ExpectStatus(plan, HttpStatusCode.OK, "Independent prerequisite capture-plan preflight");
        var token = RequireResponseString(plan, "capturePlanToken"); RememberSecret(token);
        body.Remove("exportId"); body["commandId"] = "diag-create-" + Guid.NewGuid().ToString("N"); body["capturePlanToken"] = token;
        var created = await RequireApi().PostAsync(url, body, Actor("executor").Token, ct: ct);
        ApiHarnessClient.ExpectStatus(created, HttpStatusCode.Accepted, "Independent prerequisite reconciliation create");
        var id = RequireResponseString(created, "reconciliationId");
        var terminal = await WaitForP11DirectTerminalAsync(id, TimeSpan.FromSeconds(150), ct);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(terminal);
        HarnessAssert.Equal("MATCHED", terminal.Status, "Independent initial reconciliation prerequisite");
        return terminal;
    }

    private async Task<JsonObject> DiagnosticSaveConfigAsync(P10ProductionDirectFixturePins p, bool secondEnabled, CancellationToken ct)
    {
        var url = $"api/dynamic-forms/{p.DynamicFormVersionId}/statistics";
        var before = await RequireApi().GetAsync(url, Actor("admin").Token, ct: ct);
        ApiHarnessClient.ExpectStatus(before, HttpStatusCode.OK, "Independent config read");
        var primaryLabelCode = _diagnosticGroup == "EVIDENCE_PERMISSION"
            ? DiagnosticPrimaryStatisticLabelCode : P10ProductionDirectFieldKey;
        var pagingLabelCode = _diagnosticGroup == "EVIDENCE_PERMISSION"
            ? DiagnosticPagingStatisticLabelCode : P10ProductionDirectSecondFieldKey;
        JsonObject Field(string id, string labelCode) => new() { ["fieldId"] = id, ["isStatistic"] = true,
            ["statisticLabelCodes"] = new JsonArray(labelCode), ["statistic"] = new JsonObject {
                ["aggregateOps"] = new JsonArray("COUNT", "SUM"), ["bucketMode"] = "NONE",
                ["showInDetail"] = true, ["showInTree"] = true } };
        var fields = new JsonArray(Field(P10ProductionDirectFieldId, primaryLabelCode));
        fields.Add(secondEnabled ? Field(P10ProductionDirectSecondFieldId, pagingLabelCode) :
            new JsonObject { ["fieldId"] = P10ProductionDirectSecondFieldId, ["isStatistic"] = false,
                ["statistic"] = null, ["statisticLabelCodes"] = new JsonArray() });
        var response = await RequireApi().PatchAsync(url, new JsonObject {
            ["commandId"] = "diag-config-" + Guid.NewGuid().ToString("N"),
            ["expectedRevision"] = before.Json!["revision"]!.DeepClone(),
            ["expectedConfigHash"] = before.Json!["configHash"]!.DeepClone(),
            ["payload"] = new JsonObject { ["fields"] = fields } }, Actor("admin").Token, ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, "Independent config update");
        HarnessAssert.True(response.Json!["revision"]!.GetValue<long>() > before.Json!["revision"]!.GetValue<long>(),
            "Independent config prerequisite must advance revision");
        return response.Json.AsObject();
    }

    private async Task DiagnosticCreateEvidenceMetricLabelsAsync(CancellationToken ct)
    {
        const string emptyLabelConfigHash =
            "74234e98afe7498fb5daf1f36ac2d78acc339464f950703b8c019892f982b90b";
        async Task CreateAsync(string code, string name, string color)
        {
            var response = await RequireApi().PostAsync("api/labels/config", new JsonObject {
                ["commandId"] = "diag-label-" + code.Replace('.', '-') + "-001",
                ["expectedRevision"] = 0,
                ["expectedConfigHash"] = emptyLabelConfigHash,
                ["payload"] = new JsonObject {
                    ["code"] = code, ["name"] = name,
                    ["description"] = "P11 diagnostic field-to-metric binding label",
                    ["color"] = color, ["groupCode"] = "p11-diag", ["usage"] = "STATISTIC",
                    ["dataType"] = "NUMBER", ["valueSourceType"] = "NONE",
                    ["valueOptions"] = new JsonArray(), ["valueSourceCatalogId"] = null,
                    ["scopeType"] = "GLOBAL", ["scopeId"] = null, ["isActive"] = true } },
                Actor("admin").Token, ct: ct);
            P10ProductionExpectSuccess(response, "P11 diagnostic statistic label " + code);
            var owner = await RequireDatabase().GetCollection<BsonDocument>("labels")
                .Find(new BsonDocument { ["code"] = code, ["isDeleted"] = false }).SingleAsync(ct);
            _cleanupHandles.Add(new P10CleanupHandle("labels",
                P10ProductionRequiredBsonObjectIdString(owner, "_id")));
        }
        await CreateAsync(DiagnosticPrimaryStatisticLabelCode, "P11 diagnostic amount metric", "#2457A7");
        await CreateAsync(DiagnosticPagingStatisticLabelCode, "P11 diagnostic secondary metric", "#7A3E9D");
    }

    private async Task<JsonObject> DiagnosticEvidenceMetricBindingAsync(
        P10ProductionDirectFixturePins publication, CancellationToken ct)
    {
        var response = await RequireApi().GetAsync(
            $"api/dynamic-forms/{publication.DynamicFormVersionId}/statistics", Actor("admin").Token, ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, "Independent metric binding config read");
        var config = response.Json!.AsObject();
        var fields = config["fields"]!.AsArray().OfType<JsonObject>().ToArray();
        var field = fields.Single(value => value["fieldId"]!.GetValue<string>() == P10ProductionDirectFieldId);
        var codes = field["statisticLabelCodes"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
        var snapshot = field["labelSnapshots"]!.AsArray().OfType<JsonObject>().Single();
        var allCodes = fields.Where(value => value["isStatistic"]?.GetValue<bool>() == true)
            .SelectMany(value => value["statisticLabelCodes"]!.AsArray().Select(code => code!.GetValue<string>())).ToArray();
        HarnessAssert.True(config["status"]!.GetValue<string>() == "LOCKED" &&
            config["configId"]!.GetValue<string>() == publication.ConfigId &&
            config["versionId"]!.GetValue<string>() == publication.ConfigVersionId &&
            config["versionNo"]!.GetValue<long>() == publication.ConfigVersionNo &&
            config["revision"]!.GetValue<long>() == publication.ConfigRevision &&
            config["configHash"]!.GetValue<string>() == publication.ConfigHash &&
            codes.SequenceEqual([DiagnosticPrimaryStatisticLabelCode]) &&
            DiagnosticPrimaryStatisticLabelCode != P10ProductionDirectFieldKey &&
            allCodes.Distinct(StringComparer.Ordinal).Count() == allCodes.Length,
            "Independent metric binding must be exact, distinct and bijective");
        var labelId = snapshot["labelId"]!.GetValue<string>();
        var labelVersionId = snapshot["versionId"]!.GetValue<string>();
        var labelVersionNo = snapshot["versionNo"]!.GetValue<long>();
        var labelConfigHash = snapshot["configHash"]!.GetValue<string>();
        var expectedPin = $"LABEL:{labelId}:{labelVersionId}:{labelVersionNo}:{labelConfigHash}";
        HarnessAssert.True(snapshot["code"]!.GetValue<string>() == DiagnosticPrimaryStatisticLabelCode &&
            snapshot["usage"]!.GetValue<string>() == "STATISTIC" &&
            snapshot["dataType"]!.GetValue<string>() == "NUMBER" &&
            snapshot["scopeType"]!.GetValue<string>() == "GLOBAL" && snapshot["scopeId"] is null &&
            snapshot["isActive"]!.GetValue<bool>() &&
            config["dependencyPins"]!.AsArray().Count(value => value!.GetValue<string>() == expectedPin) == 1,
            "Independent metric label snapshot/dependency pin mismatch");
        return new JsonObject {
            ["schemaVersion"] = "P11_EVIDENCE_METRIC_BINDING_V1", ["family"] = "DIRECT", ["kind"] = "FIELD",
            ["fieldId"] = P10ProductionDirectFieldId, ["fieldKey"] = P10ProductionDirectFieldKey,
            ["metricId"] = DiagnosticPrimaryStatisticLabelCode,
            ["statisticLabelCode"] = DiagnosticPrimaryStatisticLabelCode,
            ["labelId"] = labelId, ["labelVersionId"] = labelVersionId,
            ["labelVersionNo"] = labelVersionNo, ["labelConfigHash"] = labelConfigHash,
            ["configId"] = publication.ConfigId, ["configVersionId"] = publication.ConfigVersionId,
            ["configVersionNo"] = publication.ConfigVersionNo, ["configRevision"] = publication.ConfigRevision,
            ["configHash"] = publication.ConfigHash,
            ["fieldStructureHash"] = field["structureHash"]!.GetValue<string>() };
    }

    private async Task<P10ProductionDirectFixturePins> DiagnosticPublishSuccessorAsync(P10ProductionDirectFixturePins p, bool? secondEnabled, CancellationToken ct)
    {
        JsonObject config;
        if (secondEnabled.HasValue) config = await DiagnosticSaveConfigAsync(p, secondEnabled.Value, ct);
        else
        {
            var read = await RequireApi().GetAsync($"api/dynamic-forms/{p.DynamicFormVersionId}/statistics", Actor("admin").Token, ct: ct);
            ApiHarnessClient.ExpectStatus(read, HttpStatusCode.OK, "Independent successor config read"); config = read.Json!.AsObject();
        }
        var create = await RequireApi().PostAsync("api/stat-runs/DIRECT_FIELD_TABLE_LABEL/jobs", new {
            commandId = "diag-successor-" + Guid.NewGuid().ToString("N"), workId = p.WorkId,
            scopeType = "ASSIGNMENT", scopeId = p.WorkAssignmentId, sourceReportId = p.SourceReportId,
            dynamicFormTemplateId = p.DynamicFormVersionId,
            expectedConfigRevision = config["revision"]!.GetValue<long>(), expectedConfigHash = config["configHash"]!.GetValue<string>(),
            expectedSourceRevision = p.SourcePayloadRevision, expectedSourceHash = p.SourcePayloadHash,
            expectedLifecycleRevision = p.SourceLifecycleRevision,
            period = new { periodKey = p.PeriodKey, periodInstanceKey = p.PeriodInstanceKey, periodKind = p.PeriodKind,
                periodStart = p.PeriodStartUtc, periodEnd = p.PeriodEndUtc }
        }, Actor("admin").Token, ct: ct);
        ApiHarnessClient.ExpectStatus(create, HttpStatusCode.Accepted, "Independent real successor create");
        var id = RequireResponseString(create, "jobId");
        JsonObject? terminal = null;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            var poll = await RequireApi().GetAsync($"api/stat-runs/jobs/{id}", Actor("admin").Token, ct: ct);
            ApiHarnessClient.ExpectStatus(poll, HttpStatusCode.OK, "Independent successor poll");
            var status = poll.Json!["status"]!.GetValue<string>();
            if (status is "DONE" or "COMPLETED" or "FAILED" or "CANCELLED" or "DEAD_LETTER") { terminal = poll.Json.AsObject(); break; }
            await Task.Delay(250, ct);
        }
        HarnessAssert.True(terminal is not null && terminal["status"]!.GetValue<string>() is "DONE" or "COMPLETED" &&
            terminal["freshnessState"]!.GetValue<string>() == "FRESH", "Independent successor must be DONE/FRESH");
        var p9Id = terminal!["projectionRunId"]!.GetValue<string>();
        var job = await RequireDatabase().GetCollection<WorkReportStatisticRebuildJob>("work_report_statistic_rebuild_jobs")
            .Find(x => x.Id == p9Id).SingleAsync(ct);
        HarnessAssert.True(job.Id != p.P9RunId && job.IsCurrentPublication, "Independent real distinct successor publication");
        return p with { P9RunId = job.Id, P9ResultId = job.Id,
            P9GenerationId = job.GenerationId!, P9GenerationHash = job.GenerationHash!,
            ConfigId = job.ConfigId!, ConfigVersionId = job.ConfigVersionId!, ConfigVersionNo = job.ConfigVersionNo!.Value,
            ConfigRevision = job.ConfigRevision!.Value, ConfigHash = job.ConfigHash! };
    }

    private async Task<StatisticReconciliationRun> DiagnosticRecheckAsync(P10ProductionDirectFixturePins p,
        StatisticReconciliationRun prior, string status, CancellationToken ct)
    {
        var url = $"api/works/{p.WorkId}/statistics/{p.WorkAssignmentId}/reconciliations/{prior.Id}/recheck";
        var response = await RequireApi().PostAsync(url, new { commandId = "diag-recheck-" + Guid.NewGuid().ToString("N"),
            expectedStateRevision = prior.StateRevision, expectedStateHash = prior.StateHash }, Actor("executor").Token, ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Accepted, "Independent prerequisite recheck");
        var deadline = DateTime.UtcNow.AddSeconds(150);
        StatisticReconciliationRun? next = null;
        while (DateTime.UtcNow < deadline)
        {
            next = await RequireDatabase().GetCollection<StatisticReconciliationRun>(RunCollection)
                .Find(value => value.Id == prior.Id && !value.IsDeleted).SingleAsync(ct);
            if (DiagnosticRecheckSettled(next, prior.CurrentGenerationId, status))
            {
                StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(next);
                return next;
            }
            await Task.Delay(200, ct);
        }
        throw new TimeoutException("Independent recheck prerequisite did not settle: status=" + next?.Status +
            "; phase=" + (next?.Recheck?.Phase ?? "<none>") +
            "; sameGeneration=" + (next?.CurrentGenerationId == prior.CurrentGenerationId));
    }

    private static bool DiagnosticRecheckSettled(StatisticReconciliationRun run, string? priorGeneration, string status)
        => run.Status == status && run.CurrentGenerationId is not null && run.CurrentGenerationId != priorGeneration &&
           run.CurrentGenerationHash is not null && run.Recheck is null &&
           run.PendingGenerationId is null && run.PendingGenerationHash is null && run.PendingGenerationPublishedAtUtc is null &&
           run.LeaseOwnerId is null && run.ClaimToken is null && run.LeaseUntilUtc is null && run.LastHeartbeatAtUtc is null;

    internal static int RunIndependentDiagnosticReadinessSelftest()
    {
        // A real recheck publishes the new terminal generation before its
        // review-supersession marker is removed. Readiness must wait for both.
        StatisticReconciliationRun Snapshot(string status, string generation, string? phase) => new()
        {
            Status = status, CurrentGenerationId = generation, CurrentGenerationHash = new string('a', 64),
            Recheck = phase is null ? null : new StatisticReconciliationRecheckMarker { Phase = phase }
        };
        var timeline = new[]
        {
            Snapshot("QUEUED", "base-generation", "READY_TO_CLAIM"),
            Snapshot("STALE", "new-generation", StatisticReconciliationRecheckPhases.ReviewSupersessionPending),
            Snapshot("STALE", "new-generation", null)
        };
        var readyIndex = Array.FindIndex(timeline, state => DiagnosticRecheckSettled(state, "base-generation", "STALE"));
        HarnessAssert.Equal(2, readyIndex, "Fixture must not release browser while terminal supersession remains active");
        HarnessAssert.True(!DiagnosticRecheckSettled(Snapshot("STALE", "base-generation", null), "base-generation", "STALE"),
            "An unchanged generation cannot satisfy the new-generation prerequisite");
        var secretProbe = "PRIVATE_LEASE_TOKEN_NOT_FOR_PUBLIC_EVIDENCE";
        var rawPayload = "{\"authorization\":\"Bearer " + secretProbe + "\"}";
        var baseGeneration = new string('b', 64);
        var outcomeProbe = Snapshot("STALE", baseGeneration, null);
        outcomeProbe.Id = new string('c', 24);
        outcomeProbe.StateRevision = 14;
        outcomeProbe.StateHash = new string('d', 64);
        outcomeProbe.RetryCount = 2;
        outcomeProbe.GenerationPublishRevision = 7;
        outcomeProbe.ClaimToken = secretProbe;
        outcomeProbe.LeaseOwnerId = secretProbe;
        outcomeProbe.DiagnosticCode = secretProbe;
        outcomeProbe.LeaseUntilUtc = DateTime.UtcNow;
        // This is the production path: camel-case JsonElement, then the default
        // serializer used by WriteExclusive, both standalone and nested.
        JsonElement WrittenPublic(object value) => JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(DiagnosticOutcomePublicElement(value),
                new JsonSerializerOptions { WriteIndented = true }));
        void NoPrivateText(JsonElement value)
        {
            var text = value.GetRawText();
            HarnessAssert.True(!text.Contains(secretProbe, StringComparison.Ordinal) &&
                !text.Contains("authorization", StringComparison.Ordinal) &&
                !text.Contains("claimToken", StringComparison.Ordinal) &&
                !text.Contains("leaseOwnerId", StringComparison.Ordinal),
                "Public outcome excludes private fields, raw payloads and raw exception messages");
        }
        var row = DiagnosticOutcomeRunRow(outcomeProbe);
        var projectedJson = WrittenPublic(row).GetProperty("runState");
        NoPrivateText(projectedJson);
        HarnessAssert.Equal("NON_PUBLIC_CODE", projectedJson.GetProperty("diagnosticCode").GetString()!,
            "Nonpublic diagnostic text must be replaced, not retained");
        HarnessAssert.Equal(baseGeneration, projectedJson.GetProperty("currentGenerationId").GetString()!,
            "Rollback generation identity must remain observable");
        HarnessAssert.Equal(2L, projectedJson.GetProperty("retryCount").GetInt64(),
            "Retry count must remain observable");
        HarnessAssert.Equal(14L, projectedJson.GetProperty("stateRevision").GetInt64(),
            "Terminal revision must remain observable");
        HarnessAssert.True(projectedJson.GetProperty("hasLease").GetBoolean(),
            "Only lease presence is public, never its credentials");
        HarnessAssert.True(!WrittenPublic(row).GetProperty("receiptPresent").GetBoolean() &&
            WrittenPublic(row).GetProperty("receiptValid").ValueKind == JsonValueKind.Null,
            "No receipt cannot be reported as a validated receipt");
        HarnessAssert.Equal("NOT_READ_POST_STOP_OBSERVER",
            WrittenPublic(row).GetProperty("lifecycle").GetProperty("state").GetString()!,
            "Post-stop observer must not invoke the index-creating lifecycle reader");
        HarnessAssert.Equal("P10_RECHECK_CAPTURE_ABORTED", DiagnosticSafeOutcomeCode("P10_RECHECK_CAPTURE_ABORTED")!,
            "Bounded public worker diagnostic codes remain observable");

        HarnessAssert.True(DiagnosticSafeOutcomeSha(baseGeneration + "\n") is null &&
            DiagnosticSafeOutcomeObjectId(new string('c', 24) + "\n") is null &&
            DiagnosticSafeOutcomeCode("P10_RECHECK_CAPTURE_ABORTED\n") == "NON_PUBLIC_CODE",
            "Public scalar formats must reject trailing content");

        var verdictProjection = WrittenPublic(DiagnosticOutcomeVerdictProjection(new StatisticReconciliationReview {
            VerdictGenerationId = baseGeneration, Verdict = "MATCHED", FailureKind = "NONE", CompleteEvidence = true
        }));
        HarnessAssert.True(verdictProjection.GetProperty("completeEvidence").GetBoolean() &&
            !verdictProjection.TryGetProperty("CompleteEvidence", out _) &&
            !verdictProjection.GetProperty("signable").GetBoolean(),
            "Production default serialization must preserve camel case and false values after projection");

        // A failed optional read must preserve the scalar terminal snapshot.
        try
        {
            DiagnosticCaptureOutcomeLineageAsync(row, outcomeProbe,
                _ => Task.FromException<List<StatisticReconciliationReview>>(new OperationCanceledException(rawPayload)),
                CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("Selftest expected observer cancellation.");
        }
        catch (OperationCanceledException) { }
        var cancelled = WrittenPublic(new { outcome = DiagnosticOutcomePublicElement(new { rows = new[] { row } }) });
        NoPrivateText(cancelled);
        var retained = cancelled.GetProperty("outcome").GetProperty("rows")[0];
        HarnessAssert.Equal(baseGeneration, retained.GetProperty("runState").GetProperty("currentGenerationId").GetString()!,
            "Lineage cancellation must retain the observed terminal generation");
        HarnessAssert.Equal("DIAGNOSTIC_OUTCOME_CANCELLED",
            retained.GetProperty("lineageError").GetProperty("errorCode").GetString()!,
            "Cancellation must retain a useful safe code");

        var malformed = new StatisticReconciliationReview {
            Id = rawPayload, SchemaVersion = rawPayload, VerdictGenerationId = rawPayload,
            VerdictGenerationSha256 = rawPayload, ActualGenerationId = rawPayload,
            ActualGenerationSha256 = rawPayload, ExpectedGenerationId = rawPayload,
            ExpectedGenerationSha256 = rawPayload, Verdict = rawPayload, FailureKind = rawPayload,
            RootCauseClass = rawPayload, ComparisonBindingSha256 = rawPayload,
            FreshnessAssessmentSha256 = rawPayload, RootCauseClassificationSha256 = rawPayload,
            DocumentSemanticSha256 = rawPayload, SupersedesVerdictGenerationId = rawPayload,
            SupersedesVerdictGenerationSha256 = rawPayload
        };
        DiagnosticCaptureOutcomeLineageAsync(row, outcomeProbe,
            _ => Task.FromResult(new List<StatisticReconciliationReview> { malformed }),
            CancellationToken.None).GetAwaiter().GetResult();
        var invalidVerdict = WrittenPublic(row);
        NoPrivateText(invalidVerdict);
        HarnessAssert.Equal("REDACTED_INVALID_VERDICT",
            invalidVerdict.GetProperty("lineage")[0].GetProperty("state").GetString()!,
            "Invalid verdict fields must not survive validation failure");
        HarnessAssert.True(!invalidVerdict.GetProperty("lineage")[0].TryGetProperty("verdict", out _),
            "An invalid verdict retains only its bounded index and validation state");
        HarnessAssert.Equal(StatisticReconciliationFinalVerdictFailureCodes.PersistenceInvalid,
            invalidVerdict.GetProperty("lineageValidationErrors")[0].GetProperty("errorCode").GetString()!,
            "Typed validation codes must remain useful without exception details");
        NoPrivateText(WrittenPublic(DiagnosticOutcomeVerdictProjection(malformed)));

        outcomeProbe.CurrentRecheckFinalizeReceipt = new StatisticReconciliationRecheckFinalizeReceipt {
            SchemaVersion = rawPayload, TerminalStatus = rawPayload, ReceiptSha256 = rawPayload,
            CaptureBindingSha256 = rawPayload, BaseActualGenerationId = rawPayload,
            BaseActualGenerationSha256 = rawPayload, BaseVerdictGenerationId = rawPayload,
            BaseVerdictGenerationSha256 = rawPayload, SuccessorActualGenerationId = rawPayload,
            SuccessorActualGenerationSha256 = rawPayload, SuccessorVerdictGenerationId = rawPayload,
            SuccessorVerdictGenerationSha256 = rawPayload
        };
        var invalidReceipt = WrittenPublic(DiagnosticOutcomeRunRow(outcomeProbe));
        NoPrivateText(invalidReceipt);
        HarnessAssert.True(invalidReceipt.GetProperty("receiptPresent").GetBoolean() &&
            !invalidReceipt.GetProperty("receiptValid").GetBoolean(),
            "Invalid receipt presence must remain separate from validity");
        HarnessAssert.Equal("REDACTED_INVALID_RECEIPT", invalidReceipt.GetProperty("receipt").GetProperty("state").GetString()!,
            "Invalid receipt must be redacted entirely");
        NoPrivateText(WrittenPublic(DiagnosticOutcomeReceiptProjection(outcomeProbe.CurrentRecheckFinalizeReceipt)));

        outcomeProbe.Id = rawPayload;
        outcomeProbe.Status = rawPayload;
        outcomeProbe.StateHash = rawPayload;
        outcomeProbe.CurrentGenerationId = rawPayload;
        outcomeProbe.CurrentGenerationHash = rawPayload;
        outcomeProbe.PendingGenerationId = rawPayload;
        outcomeProbe.PendingGenerationHash = rawPayload;
        outcomeProbe.RetryCount = -1;
        outcomeProbe.Recheck = new StatisticReconciliationRecheckMarker { Phase = secretProbe };
        var malformedRun = WrittenPublic(DiagnosticOutcomeRunRow(outcomeProbe));
        NoPrivateText(malformedRun);
        HarnessAssert.True(malformedRun.GetProperty("runState").GetProperty("id").ValueKind == JsonValueKind.Null &&
            malformedRun.GetProperty("runState").GetProperty("currentGenerationId").ValueKind == JsonValueKind.Null &&
            malformedRun.GetProperty("runState").GetProperty("retryCount").ValueKind == JsonValueKind.Null,
            "Malformed IDs, hashes and negative counts must be omitted as null");
        HarnessAssert.Equal("NON_PUBLIC_STATUS", malformedRun.GetProperty("runState").GetProperty("status").GetString()!,
            "Unknown statuses must be redacted");
        HarnessAssert.Equal("NON_PUBLIC_PHASE", malformedRun.GetProperty("runState").GetProperty("recheckPhase").GetString()!,
            "Unknown phases must be redacted");
        try
        {
            DiagnosticCaptureOutcomeLineageAsync(row, outcomeProbe,
                _ => Task.FromResult(Enumerable.Repeat(malformed, 25).ToList()),
                CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("Selftest expected the lineage bound.");
        }
        catch (InvalidOperationException error) when (error.Message == "DIAGNOSTIC_OUTCOME_LINEAGE_LIMIT") { }
        HarnessAssert.Equal("DIAGNOSTIC_OUTCOME_LINEAGE_LIMIT",
            WrittenPublic(row).GetProperty("lineageError").GetProperty("errorCode").GetString()!,
            "Lineage bounds must retain their exact safe failure code");
        HarnessAssert.Equal("DIAGNOSTIC_OUTCOME_RUN_LIMIT",
            DiagnosticOutcomeExceptionCode(new InvalidOperationException("DIAGNOSTIC_OUTCOME_RUN_LIMIT")),
            "Run bounds must retain their exact safe failure code");
        Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = "P11_DIAGNOSTIC_READINESS_TIMELINE_SELFTEST_V1",
            status = "PASS", firstReadyIndex = readyIndex, snapshots = timeline.Length,
            supersessionPendingDidNotReleaseBrowser = true, sameGenerationRejected = true,
            outcomeProjectionRetainsRollbackAndRetry = true, outcomeProjectionExcludesSecrets = true,
            outcomeInvalidDocumentsRedacted = true, outcomeReadFailureRetainsScalarSnapshot = true,
            outcomeProductionSerializerExercised = true, outcomeLifecycleReaderNotInvoked = true,
            actualBackendExecuted = false, testedCaseCreditGranted = false }, DiagnosticJson));
        return 0;

    }
}
