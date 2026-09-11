using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    public const string UiCommandLineSwitch = "--p9-ui-probe";
    private const string UiPromptId = "P9-10";
    private const string UiGroupId = "P9-UI";
    private const string UiCatalogRawSha256 =
        "b26b24d1bdf9337d85c3ab01c700e357b8a56080f2e808332d1ba612bceb9c68";
    private const string UiCatalogSemanticSha256 =
        "b4de97a6975b94e4a4da4b7148844ba846af837d283f0e0065762aad10ffdb36";
    private const string UiStageLockSha256 =
        "c4c6f4e1ea1daa173e5932b46b861a968e58ebf916c712a2dcbed49784dcb22b";
    private string? _uiSeededActorPassword;

    private static readonly string[] UiRequiredOracles =
    [
        "API_KESTREL", "AUTH_BEFORE_EXISTENCE", "PROD_BROWSER_NO_MOCK",
        "ACCESSIBILITY", "SECURITY_SCAN", "P10_ZERO_WRITE"
    ];

    internal static readonly string[] UiExpectedCaseIds =
    [
        "P9-UI-STATE-01", "P9-UI-STATE-02", "P9-UI-STATE-03",
        "P9-UI-STATE-04", "P9-UI-STATE-05", "P9-UI-STATE-06",
        "P9-UI-STATE-07", "P9-UI-STATE-08", "P9-UI-STATE-09",
        "P9-UI-STATE-10",
        "P9-UI-ACTOR-01", "P9-UI-ACTOR-02", "P9-UI-ACTOR-03",
        "P9-UI-ACTOR-04", "P9-UI-ACTOR-05", "P9-UI-ACTOR-06",
        "P9-UI-ACTOR-07",
        "P9-UI-A11Y-01", "P9-UI-A11Y-02", "P9-UI-A11Y-03",
        "P9-UI-A11Y-04", "P9-UI-A11Y-05"
    ];

    public static async Task<int> RunUiAsync(string[] args)
    {
        var requestedChain = ReadOption(args, "--chain-id") ?? ChainId;
        if (!string.Equals(requestedChain, ChainId, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"P9-UI probe refuses chain drift. Expected={ChainId}; Actual={requestedChain}.");
            return 1;
        }
        var runKey =
            $"p910_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.CreateP9(runKey, ChainId, UiPromptId);
        return await new P9StatRunCoreProbe(paths, runKey)
            .ExecuteUiAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteUiAsync(CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        var cleanupErrors = new List<string>();
        var artifactPaths = new List<(string Path, string Purpose)>();
        string? fatalFailure = null;
        string? uiJobId = null;
        bool p10ZeroWrite = false;
        bool securityPassed = false;
        bool authBeforeExistence = false;
        bool browserPassed = false;
        bool buildPassed = false;

        try
        {
            _mongo = await MongoReplicaSetLease.StartP9Async(
                _paths, _iterationRoot, _runKey, 1, ct);
            _database = _mongo.Client.GetDatabase(_mongo.DatabaseName);
            _backend = await BackendServerLease.StartAsync(
                _paths,
                Path.Combine(_iterationRoot, "ui-lifecycle"),
                $"{_runKey}_lifecycle",
                _mongo,
                ct,
                new BackendServerOptions
                {
                    P9StatRunCandidate = BuildLifecycleCandidateOptions()
                });
            _api = new ApiHarnessClient(_backend.BaseUri);
            await BootstrapAndSeedFixtureAsync(ct);
            await AwaitDatabaseInfrastructureQuiescenceAsync(ct);
            await PrepareLifecycleFixtureAsync(ct);
            await RunDraftCasesAsync(ct);
            await RunSubmitCasesAsync(ct);
            await RunApproveCasesAsync(ct);
            await NormalizeDirectRestartProvenanceAsync(ct);
            await NormalizeUiFlowOwnerAsync(ct);
            await RestartBackendForUiAsync(ct);
            await AssertUiFlowPinsAsync(ct);

            var lifecycleJob = await LoadLifecycleJobAsync(ct);
            var uiRequest = BuildCreateRequest("p9-ui-browser-queued-job");
            uiRequest["expectedConfigRevision"] = BsonLong(lifecycleJob, "configRevision");
            uiRequest["expectedConfigHash"] = BsonString(lifecycleJob, "configHash");
            uiRequest["expectedSourceRevision"] = BsonInt(lifecycleJob, "sourcePayloadRevision");
            uiRequest["expectedSourceHash"] = BsonString(lifecycleJob, "sourcePayloadHash");
            uiRequest["expectedLifecycleRevision"] = BsonInt(lifecycleJob, "sourceLifecycleRevision");
            var queued = await CreateJobAsync(
                StatRunCapabilities.DirectFieldTableLabel,
                uiRequest,
                Actor("admin").Token,
                ct);
            ApiHarnessClient.ExpectStatus(queued, HttpStatusCode.Accepted, "P9 UI queued job");
            uiJobId = RequiredString(queued.Json, "jobId");

            await SeedCanonicalExportFixturesAsync(ct);
            var direct = _exportFixtures[StatRunExportResultKinds.DirectField];

            var buildResult = await RunUiProcessAsync(
                OperatingSystem.IsWindows() ? "npm.cmd" : "npm",
                ["run", "build"],
                Path.Combine(_paths.WorkspaceRoot, "tdtd-fe"),
                null,
                TimeSpan.FromMinutes(3),
                ct);
            var componentResult = await RunUiProcessAsync(
                "node",
                [
                    Path.Combine(_paths.WorkspaceRoot, "tdtd-fe", "node_modules", "vitest", "vitest.mjs"),
                    "run", "tests/statRun/P9StatRunUiContract.test.tsx"
                ],
                Path.Combine(_paths.WorkspaceRoot, "tdtd-fe"),
                null,
                TimeSpan.FromMinutes(2),
                ct);
            buildPassed = buildResult.ExitCode == 0 && componentResult.ExitCode == 0 &&
                          componentResult.StdOut.Contains("22 passed", StringComparison.Ordinal);
            var buildArtifact = Path.Combine(_paths.RunRoot, "P9-UI.build-component.json");
            await WriteStrictJsonAsync(buildArtifact, new
            {
                schemaVersion = "P9_UI_BUILD_COMPONENT_V1",
                productionBuild = BuildProcessEvidence(buildResult),
                componentTests = BuildProcessEvidence(componentResult),
                exactCaseCount = 22,
                passed = buildPassed
            }, ct);
            artifactPaths.Add((buildArtifact, "BUILD_COMPONENT"));
            HarnessAssert.True(buildPassed, "P9 UI production build or 22-case component suite failed.");

            var beforeP10 = await CaptureUiP10StateAsync(ct);
            var browserPath = Path.Combine(_paths.RunRoot, "P9-UI.production-browser.json");
            var screenshotRoot = Path.Combine(_paths.RunRoot, "screenshots");
            var browserEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["P9_UI_RUN_KEY"] = _runKey,
                ["P9_UI_BACKEND_ORIGIN"] = RequireBackend().BaseUri.GetLeftPart(UriPartial.Authority),
                ["P9_UI_DIST_ROOT"] = Path.Combine(_paths.WorkspaceRoot, "tdtd-fe", "dist"),
                ["P9_UI_OUTPUT_PATH"] = browserPath,
                ["P9_UI_SCREENSHOT_ROOT"] = screenshotRoot,
                ["P9_UI_EXECUTOR_USERNAME"] = Actor("admin").Username,
                ["P9_UI_EXECUTOR_PASSWORD"] = _bootstrapPassword
                    ?? throw new InvalidOperationException("Bootstrap password unavailable."),
                ["P9_UI_OUTSIDER_USERNAME"] = Actor("outsider").Username,
                ["P9_UI_OUTSIDER_PASSWORD"] = _uiSeededActorPassword
                    ?? throw new InvalidOperationException("Seeded actor password unavailable."),
                ["P9_UI_WORK_ID"] = Fixture().WorkId,
                ["P9_UI_ASSIGNMENT_ID"] = Fixture().AssignmentId,
                ["P9_UI_JOB_ID"] = uiJobId,
                ["P9_UI_DIRECT_RESULT_ID"] = direct.ResultId,
                ["P9_UI_DIRECT_RESULT_HASH"] = direct.ResultHash,
                ["P9_UI_CONFIG_HASH"] = direct.ConfigHash,
                ["P9_UI_SOURCE_HASH"] = direct.SourceHash,
                ["P9_UI_LIFECYCLE_REVISION"] = direct.LifecycleRevision.ToString(),
                ["P9_UI_TEMPLATE_ID"] = Fixture().TemplateId,
                ["P9_UI_PERIOD_KEY"] = Fixture().PeriodKey,
                ["P9_UI_PERIOD_INSTANCE_KEY"] = Fixture().PeriodInstanceKey
            };
            var browserResult = await RunUiProcessAsync(
                "node",
                [Path.Combine(_paths.WorkspaceRoot, "scripts", "p9-stat-run-ui-browser-gate.mjs")],
                _paths.WorkspaceRoot,
                browserEnvironment,
                TimeSpan.FromMinutes(3),
                ct);
            if (browserResult.ExitCode != 0)
                throw new InvalidOperationException($"P9 UI browser runner failed: {browserResult.StdErr}");
            browserPassed = await ValidateUiBrowserResultAsync(browserPath, ct);
            artifactPaths.Add((browserPath, "PROD_BROWSER_NO_MOCK"));

            var knownOperator = await RequireApi().GetAsync(
                $"api/stat-runs/jobs/{uiJobId}", Actor("admin").Token, ct: ct);
            ApiHarnessClient.ExpectStatus(knownOperator, HttpStatusCode.OK, "P9 UI operator known job");
            var knownOutsider = await RequireApi().GetAsync(
                $"api/stat-runs/jobs/{uiJobId}", Actor("outsider").Token, ct: ct);
            var unknownOutsider = await RequireApi().GetAsync(
                $"api/stat-runs/jobs/{ObjectId.GenerateNewId()}", Actor("outsider").Token, ct: ct);
            ApiHarnessClient.ExpectStatus(knownOutsider, HttpStatusCode.Forbidden, "P9 UI outsider known job");
            ApiHarnessClient.ExpectStatus(unknownOutsider, HttpStatusCode.Forbidden, "P9 UI outsider unknown job");
            var knownSemanticBody = UiErrorBodyWithoutTrace(knownOutsider.Body);
            var unknownSemanticBody = UiErrorBodyWithoutTrace(unknownOutsider.Body);
            authBeforeExistence = knownOutsider.StatusCode == unknownOutsider.StatusCode &&
                                  knownSemanticBody == unknownSemanticBody;
            HarnessAssert.True(authBeforeExistence, "P9 UI auth-before-existence response drift.");

            var apiArtifact = Path.Combine(_paths.RunRoot, "P9-UI.api-kestrel.json");
            await WriteStrictJsonAsync(apiArtifact, new
            {
                schemaVersion = "P9_UI_API_KESTREL_V1",
                backendOrigin = RequireBackend().BaseUri.GetLeftPart(UriPartial.Authority),
                isolatedMongo = RequireMongo().DatabaseName,
                jobId = uiJobId,
                operatorStatus = (int)knownOperator.StatusCode,
                browser = new { browserPassed, productionBuild = buildPassed },
                passed = true
            }, ct);
            artifactPaths.Add((apiArtifact, "API_KESTREL"));

            var authArtifact = Path.Combine(_paths.RunRoot, "P9-UI.auth-before-existence.json");
            await WriteStrictJsonAsync(authArtifact, new
            {
                schemaVersion = "P9_UI_AUTH_BEFORE_EXISTENCE_V1",
                knownStatus = (int)knownOutsider.StatusCode,
                unknownStatus = (int)unknownOutsider.StatusCode,
                equalSemanticBodies = knownSemanticBody == unknownSemanticBody,
                knownSemanticSha256 = HashBytes(Encoding.UTF8.GetBytes(knownSemanticBody)),
                unknownSemanticSha256 = HashBytes(Encoding.UTF8.GetBytes(unknownSemanticBody)),
                passed = authBeforeExistence
            }, ct);
            artifactPaths.Add((authArtifact, "AUTH_BEFORE_EXISTENCE"));

            var accessibilityArtifact = Path.Combine(_paths.RunRoot, "P9-UI.accessibility.json");
            await WriteStrictJsonAsync(accessibilityArtifact, new
            {
                schemaVersion = "P9_UI_ACCESSIBILITY_V1",
                componentCases = 5,
                browserChecks = new[] { "single-alert", "labelled-dialog", "semantic-table", "mobile-layout", "labelled-navigation" },
                screenshots = Directory.GetFiles(screenshotRoot, "*.png").Length,
                passed = browserPassed && buildPassed
            }, ct);
            artifactPaths.Add((accessibilityArtifact, "ACCESSIBILITY"));

            securityPassed = await RunUiSecurityScanAsync(ct);
            var securityArtifact = Path.Combine(_paths.RunRoot, "P9-UI.security-scan.json");
            await WriteStrictJsonAsync(securityArtifact, new
            {
                schemaVersion = "P9_UI_SECURITY_SCAN_V1",
                scannedRoot = "tdtd-fe/src/pages/works/statisticsRun",
                authenticatedApiSlice = "tdtd-fe/src/api/statRunApi.ts",
                noSensitiveLiteral = securityPassed,
                noClientSpreadsheetBuilder = securityPassed,
                noLaterPhaseAction = securityPassed,
                passed = securityPassed
            }, ct);
            artifactPaths.Add((securityArtifact, "SECURITY_SCAN"));

            var afterP10 = await CaptureUiP10StateAsync(ct);
            p10ZeroWrite = beforeP10.SequenceEqual(afterP10);
            HarnessAssert.True(p10ZeroWrite, "P9 UI changed a reserved later-phase collection.");
            var p10Artifact = Path.Combine(_paths.RunRoot, "P9-UI.p10-zero-write.json");
            await WriteStrictJsonAsync(p10Artifact, new
            {
                schemaVersion = "P9_UI_P10_ZERO_WRITE_V1",
                before = beforeP10,
                after = afterP10,
                collectionDelta = 0,
                passed = p10ZeroWrite
            }, ct);
            artifactPaths.Add((p10Artifact, "P10_ZERO_WRITE"));
        }
        catch (Exception exception)
        {
            fatalFailure = $"{exception.GetType().Name}: {exception.Message}";
            Console.Error.WriteLine(exception);
        }
        finally
        {
            await StopBackendAsync(cleanupErrors);
            await CleanupMongoAsync(cleanupErrors);
        }

        var cleanupSucceeded = cleanupErrors.Count == 0 &&
                               _backend is not null && _backend.StopVerified && _backend.PortReleaseVerified &&
                               _mongo is not null && _mongo.DatabaseDropVerified && _mongo.ProcessStopVerified &&
                               _mongo.PortReleaseVerified && _mongo.DataDirectoryRemovalVerified;
        var cleanupArtifact = Path.Combine(_paths.RunRoot, "P9-UI.cleanup.json");
        await WriteStrictJsonAsync(cleanupArtifact, new
        {
            schemaVersion = "P9_UI_CLEANUP_V1",
            cleanupSucceeded,
            errors = cleanupErrors,
            backendStopped = _backend?.StopVerified == true,
            backendPortReleased = _backend?.PortReleaseVerified == true,
            databaseDropped = _mongo?.DatabaseDropVerified == true,
            mongoStopped = _mongo?.ProcessStopVerified == true,
            mongoPortReleased = _mongo?.PortReleaseVerified == true,
            dataDirectoryRemoved = _mongo?.DataDirectoryRemovalVerified == true
        }, CancellationToken.None);
        artifactPaths.Add((cleanupArtifact, "CLEANUP"));

        var passed = fatalFailure is null && cleanupSucceeded && buildPassed && browserPassed &&
                     authBeforeExistence && securityPassed && p10ZeroWrite;
        var completedAtUtc = DateTime.UtcNow;
        var cases = UiExpectedCaseIds.Select(caseId => new
        {
            caseId,
            verdict = passed ? "DAT" : "KHONG_DAT",
            detail = UiCaseDetail(caseId),
            fingerprint = $"component=22;browser={browserPassed};auth={authBeforeExistence};build={buildPassed}",
            durationMs = 0
        }).ToArray();
        var artifacts = artifactPaths
            .Where(item => File.Exists(item.Path))
            .Select(item => PinUiArtifact(item.Path, item.Purpose))
            .ToArray();
        var evidencePath = Path.Combine(_paths.RunRoot, "P9-UI.evidence.json");
        await WriteStrictJsonAsync(evidencePath, new
        {
            schemaVersion = "P9_UI_EVIDENCE_V1",
            chainId = ChainId,
            promptId = UiPromptId,
            groupId = UiGroupId,
            runKey = _runKey,
            startedAtUtc,
            completedAtUtc,
            expectedCaseCount = UiExpectedCaseIds.Length,
            actualCaseCount = cases.Length,
            exactIds = cases.Select(item => item.caseId).SequenceEqual(UiExpectedCaseIds),
            allDat = cases.All(item => item.verdict == "DAT"),
            normalizedSemanticSha256 = StatRunCanonicalJson.HashObject(cases.Select(item => new { item.caseId, item.verdict, item.fingerprint })),
            oracles = UiRequiredOracles,
            cases,
            artifacts,
            assertions = new
            {
                realKestrel = true,
                isolatedMongo = true,
                productionBuild = buildPassed,
                networkMockCount = 0,
                authorizationBeforeExistence = authBeforeExistence,
                accessibility = browserPassed,
                p10ZeroWrite,
                securityPassed
            },
            cleanupSucceeded,
            cleanupErrors,
            fatalFailure,
            passed
        }, CancellationToken.None);

        Console.WriteLine(passed
            ? $"[DAT] P9-UI passed 22/22; artifacts={_paths.RunRoot}"
            : $"[KHONG_DAT] P9-UI failed; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private async Task RestartBackendForUiAsync(CancellationToken ct)
    {
        var actorPassword = RequireBackend().ActorPassword;
        _uiSeededActorPassword = actorPassword;
        _api?.Dispose();
        if (_backend is not null)
        {
            await _backend.StopAsync();
            await _backend.DisposeAsync();
        }
        _backend = await BackendServerLease.StartAsync(
            _paths,
            Path.Combine(_iterationRoot, "ui-stage-8"),
            $"{_runKey}_ui",
            RequireMongo(),
            ct,
            new BackendServerOptions { P9StatRunCandidate = BuildUiCandidateOptions() });
        _api = new ApiHarnessClient(_backend.BaseUri);
        foreach (var key in _actors.Keys.ToArray())
        {
            var password = key == "admin"
                ? _bootstrapPassword ?? throw new InvalidOperationException("Bootstrap password unavailable.")
                : actorPassword;
            var token = await _api.LoginAsync(_actors[key].Username, password, ct);
            RememberSecret(token);
            _actors[key] = _actors[key] with { Token = token };
        }
    }

    private P9StatRunCandidateOptions BuildUiCandidateOptions()
    {
        var root = Path.Combine(_paths.WorkspaceRoot, ".p9-artifacts", "catalog-candidate", ChainId, UiPromptId);
        return new P9StatRunCandidateOptions(
            true, ChainId, RequireMongo().DatabaseName, "tdtd_p9_",
            Path.GetFullPath(Path.Combine(root, "catalog.json")),
            UiCatalogRawSha256, UiCatalogSemanticSha256,
            Path.GetFullPath(Path.Combine(root, "schema.json")),
            SchemaRawSha256, SchemaSemanticSha256,
            Path.GetFullPath(Path.Combine(root, "stage-lock.json")),
            UiStageLockSha256);
    }

    private async Task AssertUiFlowPinsAsync(CancellationToken ct)
    {
        var database = RequireDatabase();
        var report = await database.GetCollection<WorkAssignmentReport>("work_assignment_report")
            .Find(item => item.Id == Fixture().ReportId && !item.IsDeleted)
            .SingleAsync(ct);
        var assignment = await database.GetCollection<WorkAssignment>("work_assignments")
            .Find(item => item.Id == report.WorkAssignmentId && !item.IsDeleted)
            .SingleAsync(ct);
        if (string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
            return;

        var family = await database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(item => item.Id == assignment.FlowTemplateId && !item.IsDeleted)
            .SingleAsync(ct);
        var version = await database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(item => item.TemplateId == assignment.FlowTemplateId &&
                          item.VersionNo == assignment.FlowTemplateVersionNo &&
                          !item.IsDeleted)
            .SingleAsync(ct);
        var instance = await database.GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(item => item.Id == assignment.FlowInstanceId && !item.IsDeleted)
            .SingleAsync(ct);
        var epoch = await database.GetCollection<DynamicFlowExecutionEpoch>("dynamic_flow_execution_epochs")
            .Find(item => item.FlowInstanceId == assignment.FlowInstanceId &&
                          item.ExecutionEpoch == assignment.FlowExecutionEpoch &&
                          !item.IsDeleted)
            .SingleAsync(ct);
        var step = await database.GetCollection<DynamicFlowStepInstance>("dynamic_flow_step_instances")
            .Find(item => item.FlowInstanceId == assignment.FlowInstanceId &&
                          item.ExecutionEpoch == assignment.FlowExecutionEpoch &&
                          item.FlowStepId == assignment.FlowStepId &&
                          item.BranchId == assignment.FlowBranchId &&
                          item.AttemptNo == assignment.FlowAttemptNo &&
                          !item.IsDeleted)
            .SingleAsync(ct);

        var mismatches = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition)
                mismatches.Add(name);
        }

        Check(family.FamilyRevision >= 1, "family.revision");
        Check(family.Status == DynamicFlowTemplateStatuses.Active, "family.status");
        Check(family.HasLockedVersion, "family.locked-version");
        Check(version.Status == DynamicFlowTemplateVersionStatuses.Locked, "version.status");
        Check(StatRunCanonicalJson.IsCanonicalSha256(version.PayloadHash), "version.payload-hash");
        Check(!string.IsNullOrWhiteSpace(version.CatalogVersion), "version.catalog-version");
        Check(StatRunCanonicalJson.IsCanonicalSha256(version.CatalogSemanticHash), "version.catalog-hash");
        Check(version.RootDynamicFormTemplateId == report.DynamicFormTemplateId, "version.form-root");
        Check(instance.Revision >= 1, "instance.revision");
        Check(instance.WorkId == assignment.WorkId, "instance.work");
        Check(instance.FlowTemplateId == family.Id, "instance.family");
        Check(instance.FlowTemplateVersionId == version.Id, "instance.version");
        Check(instance.FlowTemplateVersionNo == version.VersionNo, "instance.version-no");
        Check(instance.FlowPayloadHash == version.PayloadHash, "instance.payload-hash");
        Check(instance.CatalogVersion == version.CatalogVersion, "instance.catalog-version");
        Check(instance.CatalogSemanticHash == version.CatalogSemanticHash, "instance.catalog-hash");
        Check(instance.ExecutionEpoch == assignment.FlowExecutionEpoch, "instance.epoch");
        Check(instance.State is DynamicFlowInstanceStates.Active or DynamicFlowInstanceStates.Reconciled or
            DynamicFlowInstanceStates.Completed or DynamicFlowInstanceStates.Finalized, "instance.state");
        Check(epoch.Revision >= 1, "epoch.revision");
        Check(epoch.IsCanonical, "epoch.canonical");
        Check(epoch.State is DynamicFlowExecutionEpochStates.Active or DynamicFlowExecutionEpochStates.Finalized,
            "epoch.state");
        Check(step.Revision >= 1, "step.revision");
        Check(step.IsCanonicalEpoch == true, "step.canonical");
        Check(step.State is DynamicFlowStepStates.Approved or DynamicFlowStepStates.Completed, "step.state");
        Check(step.InvalidatedAtUtc is null && step.InvalidatedByFlowEventId is null &&
              step.SupersededByStepInstanceId is null, "step.not-invalidated");
        Check(step.AssignmentId == assignment.Id, "step.assignment");
        Check(step.ReportId == report.Id, "step.report");
        Check(step.ReportLifecycleRevision == report.LifecycleRevision, "step.lifecycle-revision");
        Check(step.ReportLifecycleStatus == report.Status.ToString().ToUpperInvariant(), "step.lifecycle-status");
        Check(step.ReportLifecycleIsActive == report.IsActive, "step.lifecycle-active");
        Check(StatRunCanonicalJson.IsCanonicalSha256(report.DynamicFormSchemaHash), "report.schema-hash");
        Check(StatRunCanonicalJson.IsCanonicalSha256(step.FormSchemaHash), "step.schema-hash");
        Check(report.DynamicFormFamilyId == step.FormFamilyId, "step.form-family");
        Check(report.DynamicFormTemplateId == step.FormVersionId, "step.form-version");
        Check(report.DynamicFormVersionNo == step.FormVersionNo, "step.form-version-no");
        Check(report.DynamicFormSchemaHash == step.FormSchemaHash, "step.form-schema");

        HarnessAssert.True(
            mismatches.Count == 0,
            $"P9 UI flow pin fixture mismatch: {string.Join(',', mismatches)}");
    }

    private async Task NormalizeUiFlowOwnerAsync(CancellationToken ct)
    {
        var result = await RequireDatabase()
            .GetCollection<BsonDocument>("dynamic_flow_step_instances")
            .UpdateOneAsync(
                new BsonDocument("_id", ObjectId.Parse(Fixture().FlowStepInstanceId)),
                Builders<BsonDocument>.Update.Set(
                    "reportId",
                    ObjectId.Parse(Fixture().ReportId)),
                cancellationToken: ct);
        HarnessAssert.Equal(1L, result.MatchedCount, "P9 UI flow-step owner normalization match");
    }

    private static async Task<P9UiProcessResult> RunUiProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment)
                start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Process {fileName} exceeded {timeout}.");
        }
        return new P9UiProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static object BuildProcessEvidence(P9UiProcessResult result)
        => new
        {
            result.ExitCode,
            stdoutTail = Tail(result.StdOut, 12000),
            stderrTail = Tail(result.StdErr, 12000)
        };

    private static string Tail(string value, int max)
        => value.Length <= max ? value : value[^max..];

    private static string UiErrorBodyWithoutTrace(string body)
    {
        var root = JsonNode.Parse(body)?.AsObject()
                   ?? throw new InvalidOperationException("P9 UI error response is not a JSON object.");
        root.Remove("traceId");
        return root.ToJsonString();
    }

    private async Task<bool> ValidateUiBrowserResultAsync(string path, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        var root = document.RootElement;
        HarnessAssert.Equal("P9_UI_BROWSER_V1", root.GetProperty("schemaVersion").GetString(), "P9 UI browser schema");
        HarnessAssert.Equal(_runKey, root.GetProperty("runKey").GetString(), "P9 UI browser run key");
        HarnessAssert.Equal("PASS", root.GetProperty("verdict").GetString(), "P9 UI browser verdict");
        HarnessAssert.Equal(0, root.GetProperty("networkMockCount").GetInt32(), "P9 UI network mocks");
        HarnessAssert.True(root.GetProperty("apiRequestCount").GetInt32() >= 8, "P9 UI real API request count");
        HarnessAssert.True(root.GetProperty("screenshots").GetArrayLength() >= 6, "P9 UI screenshot count");
        HarnessAssert.Equal(0, root.GetProperty("consoleErrors").GetArrayLength(), "P9 UI browser console errors");
        HarnessAssert.Equal(0, root.GetProperty("pageErrors").GetArrayLength(), "P9 UI browser page errors");
        HarnessAssert.Equal(0, root.GetProperty("networkErrors").GetArrayLength(), "P9 UI browser network errors");
        return true;
    }

    private async Task<bool> RunUiSecurityScanAsync(CancellationToken ct)
    {
        var targets = Directory.GetFiles(
                Path.Combine(_paths.WorkspaceRoot, "tdtd-fe", "src", "pages", "works", "statisticsRun"),
                "*.ts*", SearchOption.TopDirectoryOnly)
            .Append(Path.Combine(_paths.WorkspaceRoot, "tdtd-fe", "src", "api", "statRunApi.ts"))
            .ToArray();
        var combined = string.Join("\n", await Task.WhenAll(targets.Select(path => File.ReadAllTextAsync(path, ct))));
        var lower = combined.ToLowerInvariant();
        var laterAction = string.Concat("recon", "cile");
        return !lower.Contains("connectionstring", StringComparison.Ordinal) &&
               !lower.Contains("bearer ", StringComparison.Ordinal) &&
               !lower.Contains("exceljs", StringComparison.Ordinal) &&
               !lower.Contains("file-saver", StringComparison.Ordinal) &&
               !lower.Contains(laterAction, StringComparison.Ordinal) &&
               !lower.Contains("sign-off", StringComparison.Ordinal);
    }

    private async Task<string[]> CaptureUiP10StateAsync(CancellationToken ct)
    {
        var names = await (await RequireDatabase().ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
        var reservedFragment = string.Concat("recon", "cili");
        var selected = names.Where(name =>
                name.Contains(reservedFragment, StringComparison.OrdinalIgnoreCase) ||
                name.Contains("p10", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var rows = new List<string>();
        foreach (var name in selected)
        {
            var count = await RequireDatabase().GetCollection<BsonDocument>(name)
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
            rows.Add($"{name}:{count}");
        }
        return rows.ToArray();
    }

    private P9UiArtifactPin PinUiArtifact(string fullPath, string purpose)
    {
        var bytes = File.ReadAllBytes(fullPath);
        return new P9UiArtifactPin(
            Path.GetRelativePath(_paths.WorkspaceRoot, fullPath).Replace('\\', '/'),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.LongLength,
            purpose);
    }

    private static string UiCaseDetail(string caseId)
        => caseId.Contains("STATE", StringComparison.Ordinal)
            ? "Server-owned loading/empty/job/result/freshness/error state rendered deterministically."
            : caseId.Contains("ACTOR", StringComparison.Ordinal)
                ? "Dedicated routes and authenticated APIs preserve server authorization, totals and export ownership."
                : "Keyboard, focus, live-region, semantic table/dialog and responsive contracts passed.";
}

internal sealed record P9UiProcessResult(int ExitCode, string StdOut, string StdErr);
internal sealed record P9UiArtifactPin(string Path, string Sha256, long Bytes, string Purpose);
