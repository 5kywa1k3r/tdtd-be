using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task BuildCloseoutFrontendAsync(CancellationToken ct)
    {
        var frontendRoot = Path.Combine(_paths.WorkspaceRoot, "tdtd-fe");
        var build = await RunUiProcessAsync(
            OperatingSystem.IsWindows() ? "npm.cmd" : "npm",
            ["run", "build"],
            frontendRoot,
            null,
            TimeSpan.FromMinutes(4),
            ct);
        var component = await RunUiProcessAsync(
            "node",
            [
                Path.Combine(frontendRoot, "node_modules", "vitest", "vitest.mjs"),
                "run",
                "tests/statRun/P9StatRunUiContract.test.tsx"
            ],
            frontendRoot,
            null,
            TimeSpan.FromMinutes(3),
            ct);
        _closeBuildPassed = build.ExitCode == 0 &&
                            component.ExitCode == 0 &&
                            component.StdOut.Contains("22 passed", StringComparison.Ordinal);
        var artifact = Path.Combine(
            _paths.RunRoot,
            "P9-CLOSE.build-component.json");
        await WriteStrictJsonAsync(
            artifact,
            new
            {
                schemaVersion = "P9_CLOSE_BUILD_COMPONENT_V1",
                productionBuild = BuildProcessEvidence(build),
                componentTests = BuildProcessEvidence(component),
                exactComponentCaseCount = 22,
                distRoot = Path.Combine(frontendRoot, "dist"),
                passed = _closeBuildPassed
            },
            ct);
        HarnessAssert.True(
            _closeBuildPassed,
            "P9-CLOSE production build or exact 22-case UI suite failed.");
    }

    private async Task RunCloseoutBrowser03Async(CancellationToken ct)
    {
        var direct = _exportFixtures[StatRunExportResultKinds.DirectField];
        var exportPath = CloseoutResultPath(
            "DIRECT_FIELD",
            direct.ResultId,
            Fixture().WorkId,
            new Dictionary<string, string?>
            {
                ["dynamicFormTemplateId"] = Fixture().TemplateId,
                ["scopeType"] = "WORK",
                ["periodKey"] = Fixture().PeriodKey,
                ["periodInstanceKey"] = Fixture().PeriodInstanceKey,
                ["resultHash"] = direct.ResultHash,
                ["configHash"] = direct.ConfigHash,
                ["sourceHash"] = direct.SourceHash,
                ["lifecycleRevision"] = direct.LifecycleRevision.ToString(),
                ["page"] = "0",
                ["pageSize"] = "50"
            });
        var request = new P9CloseBrowserSliceRequest(
            "browser03-export-security-mobile",
            CloseoutBrowserCaseIds[2],
            [],
            [],
            exportPath,
            null,
            null,
            null,
            null,
            ExpectedDatabaseMutation: true);
        var slice = await RunCloseoutBrowserSliceAsync(request, ct);
        _closeExportParse = ValidateCloseoutDownloads(slice.ResultPath);
        HarnessAssert.True(_closeExportParse.Passed,
            "P9-CLOSE independent CSV/XLSX browser download parse failed.");
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P9-CLOSE.export-parse.json"),
            _closeExportParse,
            ct);
    }

    private async Task PrepareCloseoutLifecycleBrowser02Async(
        CancellationToken ct)
    {
        _closeApprovedJobId = await CreateAndCompleteCloseoutFoundationJobAsync(
            "p9-close-browser-approved-foundation-001",
            "p9-close-approved-worker",
            ct);
        var beforeRecall = await CaptureDatabaseSnapshotAsync(ct);
        var report = await LoadLifecycleReportAsync(ct);
        const string recallCommand = "p9-close-browser-recall-001";
        var recall = await RequireApi().PostAsync(
            $"api/work-assignment-review/reports/{Fixture().ReportId}/recall-approved",
            new JsonObject
            {
                ["expectedPayloadRevision"] = BsonInt(report, "payloadRevision"),
                ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
                ["commandId"] = recallCommand,
                ["comment"] = "P9-12 production-browser canonical recall"
            },
            Actor("admin").Token,
            ct: ct);
        ExpectOperationsSuccess(recall, "P9-CLOSE recall approved");
        await DrainOperationsLifecycleEntryAsync(
            "REVIEW_RECALL_APPROVED",
            "PUBLISHED",
            recallCommand,
            ct);
        var reversal = await LoadCurrentOperationsPublicationAsync(ct);
        _closeReversalJobId = BsonText(reversal, "_id");
        var afterRecall = await CaptureDatabaseSnapshotAsync(ct);
        RecordCloseoutTransition(
            "lifecycle-recall",
            "Production API recall publishes a zero-member reversal and stales the prior generation.",
            beforeRecall,
            afterRecall,
            expectedMutation: true);

        var beforeReapprove = afterRecall;
        await SeedCanonicalP7MappingLineageAsync(
            ct,
            "p9-close-browser-p7-reapprove-002",
            _flwIncludeVersionId);
        report = await LoadLifecycleReportAsync(ct);
        const string reapproveCommand = "p9-close-browser-reapprove-002";
        var reapprove = await RequireApi().PostAsync(
            $"api/work-assignment-review/reports/{Fixture().ReportId}/approve",
            new JsonObject
            {
                ["expectedPayloadRevision"] = BsonInt(report, "payloadRevision"),
                ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
                ["commandId"] = reapproveCommand,
                ["comment"] = "P9-12 production-browser canonical rebuild"
            },
            Actor("admin").Token,
            ct: ct);
        ExpectOperationsSuccess(reapprove, "P9-CLOSE reapprove");
        await DrainOperationsLifecycleEntryAsync(
            "REVIEW_APPROVE",
            "PUBLISHED",
            reapproveCommand,
            ct);
        var rebuilt = await LoadCurrentOperationsPublicationAsync(ct);
        _closeRebuiltJobId = BsonText(rebuilt, "_id");
        var afterReapprove = await CaptureDatabaseSnapshotAsync(ct);
        RecordCloseoutTransition(
            "lifecycle-reapprove",
            "Production API reapproval publishes one new current generation.",
            beforeReapprove,
            afterReapprove,
            expectedMutation: true);

    }

    private async Task PrepareCloseoutRebuiltFoundationAsync(
        CancellationToken ct)
    {
        _closeRebuiltFoundationJobId = await CreateAndCompleteCloseoutFoundationJobAsync(
            "p9-close-browser-rebuilt-foundation-002",
            "p9-close-rebuilt-worker",
            ct);
        await RequireCloseoutFoundationFreshnessAsync(
            _closeApprovedJobId,
            "STALE",
            ct);
        await RequireCloseoutFoundationFreshnessAsync(
            _closeRebuiltFoundationJobId,
            "FRESH",
            ct);
    }

    private async Task RunCloseoutLifecycleBrowser02Async(
        CancellationToken ct)
    {
        var approvedJobId = _closeApprovedJobId
            ?? throw new InvalidOperationException(
                "P9-CLOSE approved Foundation job missing.");
        var rebuiltJobId = _closeRebuiltFoundationJobId
            ?? throw new InvalidOperationException(
                "P9-CLOSE rebuilt Foundation job missing.");

        await RestartCloseoutBackendForReadOnlyBrowserAsync(ct);

        var request = new P9CloseBrowserSliceRequest(
            "browser02-lifecycle-multi-context",
            CloseoutBrowserCaseIds[1],
            [],
            [],
            null,
            new P9CloseJobJourney(
                "approved-prior",
                CloseoutJobPath(approvedJobId),
                "STALE",
                "admin",
                "lifecycle-recall"),
            null,
            new P9CloseJobJourney(
                "rebuilt",
                CloseoutJobPath(rebuiltJobId),
                "READY",
                "admin",
                "lifecycle-reapprove"),
            null,
            ExpectedDatabaseMutation: false);
        await RunCloseoutBrowserSliceAsync(request, ct);
    }

    private async Task<string> CreateAndCompleteCloseoutFoundationJobAsync(
        string commandId,
        string workerId,
        CancellationToken ct)
    {
        var request = await BuildOperationsFoundationCreateRequestAsync(commandId, ct);
        var created = await CreateJobAsync(
            StatRunCapabilities.DirectFieldTableLabel,
            request,
            Actor("admin").Token,
            ct);
        ApiHarnessClient.ExpectStatus(
            created,
            HttpStatusCode.Accepted,
            $"P9-CLOSE Foundation enqueue {commandId}");
        HarnessAssert.Equal(
            "QUEUED",
            RequiredString(created.Json, "status"),
            $"P9-CLOSE Foundation queued state {commandId}");
        var jobId = RequiredString(created.Json, "jobId");

        var claim = await ClaimAsync(workerId, ct);
        var claimedJob = ApiHarnessClient.RequiredObject(
            claim["job"],
            $"P9-CLOSE Foundation claim {commandId}");
        HarnessAssert.Equal(
            jobId,
            RequiredString(claimedJob, "jobId"),
            $"P9-CLOSE Foundation claim identity {commandId}");
        var claimToken = RequiredString(claim, "claimToken");
        var completed = await RequireApi().PostAsync(
            $"api/testing/p9/stat-runs/jobs/{jobId}/complete",
            new { workerId, claimToken },
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            completed,
            HttpStatusCode.OK,
            $"P9-CLOSE Foundation complete {commandId}");
        HarnessAssert.Equal(
            "DONE",
            RequiredString(completed.Json, "status"),
            $"P9-CLOSE Foundation status {commandId}");
        HarnessAssert.Equal(
            "FRESH",
            RequiredString(completed.Json, "freshnessState"),
            $"P9-CLOSE Foundation freshness {commandId}");
        _ = RequiredString(completed.Json, "generationId");
        return jobId;
    }

    private async Task RequireCloseoutFoundationFreshnessAsync(
        string? jobId,
        string expectedFreshness,
        CancellationToken ct)
    {
        var response = await RequireApi().GetAsync(
            $"api/stat-runs/jobs/{jobId ?? string.Empty}",
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P9-CLOSE Foundation read {expectedFreshness}");
        HarnessAssert.Equal(
            "DONE",
            RequiredString(response.Json, "status"),
            $"P9-CLOSE Foundation completed state {expectedFreshness}");
        HarnessAssert.Equal(
            expectedFreshness,
            RequiredString(response.Json, "freshnessState"),
            $"P9-CLOSE Foundation read freshness {expectedFreshness}");
        if (string.Equals(expectedFreshness, "STALE", StringComparison.Ordinal))
        {
            HarnessAssert.Equal(
                "SOURCE_REVISION_CHANGED",
                RequiredString(response.Json, "staleReason"),
                "P9-CLOSE prior Foundation stale reason");
        }
    }

    private async Task RestartCloseoutBackendForReadOnlyBrowserAsync(
        CancellationToken ct)
    {
        var previous = RequireBackend();
        var actorPassword = previous.ActorPassword;
        var previousProcessId = previous.ProcessId;
        var previousPort = previous.Port;
        _api?.Dispose();
        _api = null;
        await previous.StopAsync();
        _backend = null;
        await previous.DisposeAsync();
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot,
                "P9-CLOSE.production-workers-stage.json"),
            new
            {
                schemaVersion = "P9_CLOSE_BACKEND_STAGE_V1",
                stage = "production-workers",
                environment = "Production",
                candidateEnabled = false,
                hangfireServerEnabled = true,
                processId = previousProcessId,
                port = previousPort,
                stopped = previous.StopVerified,
                portReleased = previous.PortReleaseVerified,
                passed = previous.StopVerified && previous.PortReleaseVerified
            },
            ct);

        var replacement = await BackendServerLease.StartAsync(
            _paths,
            Path.Combine(_iterationRoot, "production-v1.6-readonly-browser"),
            $"{_runKey}_readonly",
            RequireMongo(),
            ct,
            new BackendServerOptions
            {
                EnvironmentNameOverride = "Production",
                HangfireServerEnabled = false
            });
        _backend = replacement;
        _api = new ApiHarnessClient(_backend.BaseUri);
        foreach (var key in _actors.Keys.ToArray())
        {
            var password = key == "admin"
                ? _bootstrapPassword ?? throw new InvalidOperationException(
                    "P9-CLOSE bootstrap password unavailable.")
                : actorPassword;
            var token = await _api.LoginAsync(_actors[key].Username, password, ct);
            RememberSecret(token);
            _actors[key] = _actors[key] with { Token = token };
        }

        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot,
                "P9-CLOSE.production-readonly-browser-stage.json"),
            new
            {
                schemaVersion = "P9_CLOSE_BACKEND_STAGE_V1",
                stage = "production-readonly-browser",
                environment = "Production",
                candidateEnabled = false,
                hangfireServerEnabled = false,
                processId = _backend.ProcessId,
                port = _backend.Port,
                origin = _backend.BaseUri.GetLeftPart(UriPartial.Authority),
                passed = true
            },
            ct);
    }

    private async Task CreateCloseoutQueuedJobAsync(
        string commandId,
        CancellationToken ct)
    {
        var request = await BuildOperationsFoundationCreateRequestAsync(
            commandId,
            ct);
        var response = await CreateJobAsync(
            StatRunCapabilities.DirectFieldTableLabel,
            request,
            Actor("admin").Token,
            ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Accepted,
            "P9-CLOSE queued browser job");
        _closeQueuedJobId = RequiredString(response.Json, "jobId");
    }

    private async Task RestartCloseoutBackendForResultBrowserAsync(
        CancellationToken ct)
    {
        var previous = RequireBackend();
        var previousProcessId = previous.ProcessId;
        var previousPort = previous.Port;
        _api?.Dispose();
        _api = null;
        await previous.StopAsync();
        _backend = null;
        await previous.DisposeAsync();

        var replacement = await BackendServerLease.StartAsync(
            _paths,
            Path.Combine(_iterationRoot, "production-v1.6-results-browser"),
            $"{_runKey}_results",
            RequireMongo(),
            ct,
            new BackendServerOptions
            {
                EnvironmentNameOverride = "Production",
                HangfireServerEnabled = true
            });
        _backend = replacement;
        _api = new ApiHarnessClient(_backend.BaseUri);
        var actorPassword = _closeSeededActorPassword
            ?? throw new InvalidOperationException(
                "P9-CLOSE seeded actor password unavailable.");
        foreach (var key in _actors.Keys.ToArray())
        {
            var password = key == "admin"
                ? _bootstrapPassword ?? throw new InvalidOperationException(
                    "P9-CLOSE bootstrap password unavailable.")
                : actorPassword;
            var token = await _api.LoginAsync(_actors[key].Username, password, ct);
            RememberSecret(token);
            _actors[key] = _actors[key] with { Token = token };
        }

        HarnessAssert.True(
            previous.StopVerified && previous.PortReleaseVerified,
            $"P9-CLOSE readonly backend lease was not released: " +
            $"pid={previousProcessId};port={previousPort}");
    }

    private async Task RetireCloseoutQueuedJobAsync(CancellationToken ct)
    {
        var jobId = _closeQueuedJobId
            ?? throw new InvalidOperationException(
                "P9-CLOSE queued job missing before retirement.");
        var queued = await LoadJobAsync(jobId, ct);
        var request = BuildOperationsCas(
            queued,
            "p9-close-browser-retire-queued-001");
        var cancelled = await PostOperationsMutationAsync(
            jobId,
            "cancel",
            request,
            ct);
        var cancelledJob = OperationsMutationJob(
            cancelled,
            "P9-CLOSE retire queued job");
        HarnessAssert.Equal(
            "FAILED",
            RequiredString(cancelledJob, "status"),
            "P9-CLOSE retired queued status");
        HarnessAssert.Equal(
            "STAT_RUN_JOB_CANCELLED",
            RequiredString(cancelledJob, "diagnosticCode"),
            "P9-CLOSE retired queued diagnostic");
        _closeQueuedJobId = null;
    }

    private async Task RunCloseoutResultBrowser01Async(CancellationToken ct)
    {
        var currentRunId = _closeRebuiltJobId
            ?? throw new InvalidOperationException("P9-CLOSE rebuilt run missing.");
        var commonDirect = new Dictionary<string, string?>
        {
            ["dynamicFormTemplateId"] = Fixture().TemplateId,
            ["scopeType"] = "WORK",
            ["periodKey"] = Fixture().PeriodKey,
            ["periodInstanceKey"] = Fixture().PeriodInstanceKey,
            ["page"] = "0",
            ["pageSize"] = "1"
        };
        await RunCloseoutBrowserSliceAsync(
            new P9CloseBrowserSliceRequest(
                "browser01-direct",
                CloseoutBrowserCaseIds[0],
                ["DIRECT_FIELD", "DIRECT_TABLE", "DIRECT_LABEL"],
                [
                    new P9CloseResultJourney(
                        "DIRECT_FIELD",
                        CloseoutResultPath("DIRECT_FIELD", currentRunId,
                            Fixture().WorkId, commonDirect),
                        "READY", true, true, Fixture().ReportId),
                    new P9CloseResultJourney(
                        "DIRECT_TABLE",
                        CloseoutResultPath("DIRECT_TABLE", currentRunId,
                            Fixture().WorkId, commonDirect),
                        "EMPTY", false, false, null),
                    new P9CloseResultJourney(
                        "DIRECT_LABEL",
                        CloseoutResultPath("DIRECT_LABEL", currentRunId,
                            Fixture().WorkId,
                            MergeCloseoutQuery(commonDirect,
                                ("labelCode", LifecycleLabelCode),
                                ("pageSize", "50"))),
                        "EMPTY", false, false, null)
                ],
                null, null, null, null, null,
                ExpectedDatabaseMutation: false),
            ct);

        await PrepareBasicFixtureAsync(ct, preserveApprovedPairedSource: true);
        await SetSiblingActiveAsync(false, ct);
        await ConfigureBasicAsync("DIRECT_CHILDREN_OR_SELF", ct, "admin");
        await RunCloseoutBasicSliceAsync("BASIC", "browser01-basic", null, ct);

        foreach (var mode in new[]
                 {
                     "FLOW_BRANCH",
                     "FLOW_STEP",
                     "FLOW_EFFECTIVE_PATH",
                     "FLOW_FINAL"
                 })
        {
            await ConfigureBasicAsync(mode, ct, "admin");
            await RunCloseoutBasicSliceAsync(mode, $"browser01-{mode.ToLowerInvariant()}", mode, ct);
        }

        var templateCollection = RequireDatabase()
            .GetCollection<BsonDocument>("dynamic_form_templates");
        var assignmentCollection = RequireDatabase()
            .GetCollection<BsonDocument>("work_assignments");
        var templateBeforeSummaryFixtures = await templateCollection
            .Find(new BsonDocument("_id", ObjectId.Parse(Fixture().TemplateId)))
            .SingleAsync(ct);
        var assignmentBeforeSummaryFixtures = await assignmentCollection
            .Find(new BsonDocument("_id", ObjectId.Parse(Fixture().AssignmentId)))
            .SingleAsync(ct);

        await PrepareAdvancedFixtureAsync(ct);
        var advancedBuild = await RequestAdvancedBuildAsync(
            "day",
            "2026-08-01",
            "p9-close-advanced-day-001",
            false,
            Actor("executor").Token,
            ct);
        ApiHarnessClient.ExpectStatus(
            advancedBuild,
            HttpStatusCode.Accepted,
            "P9-CLOSE Advanced DAY build");
        _advDay = await WaitAdvancedNodeAsync(
            "work_assignment_advanced_summary_day_nodes",
            "2026-08-01",
            "CLEAN",
            ct);
        var advancedId = BsonText(_advDay, "_id");
        await RunCloseoutBrowserSliceAsync(
            new P9CloseBrowserSliceRequest(
                "browser01-advanced",
                CloseoutBrowserCaseIds[0],
                ["ADVANCED"],
                [new P9CloseResultJourney(
                    "ADVANCED",
                    CloseoutResultPath(
                        "ADVANCED",
                        advancedId,
                        Fixture().AssignmentId,
                        new Dictionary<string, string?>
                        {
                            ["configId"] = _advVersionId,
                            ["startDayKey"] = "2026-08-01",
                            ["endDayKey"] = "2026-08-01",
                            ["scopeType"] = "ASSIGNMENT",
                            ["page"] = "0",
                            ["pageSize"] = "50"
                        }),
                    "READY", false, false, null)],
                null, null, null, null, null,
                ExpectedDatabaseMutation: false),
            ct);

        await PrepareDiffFixtureAsync(ct);
        var diff = await ExecuteDiffRunAsync(
            _difConfigs[1],
            "p9-close-diff-table-001",
            100,
            Actor("executor").Token,
            HttpStatusCode.Created,
            ct);
        var diffResultId = DiffString(diff, "resultId");
        await RunCloseoutBrowserSliceAsync(
            new P9CloseBrowserSliceRequest(
                "browser01-diff",
                CloseoutBrowserCaseIds[0],
                ["DIFF"],
                [new P9CloseResultJourney(
                    "DIFF",
                    CloseoutResultPath(
                        "DIFF",
                        diffResultId,
                        Fixture().AssignmentId,
                        new Dictionary<string, string?>
                        {
                            ["dynamicFormTemplateId"] = Fixture().TemplateId,
                            ["scopeType"] = "ASSIGNMENT",
                            ["page"] = "0",
                            ["pageSize"] = "1"
                        }),
                    "READY", false, true, null)],
                null, null, null, null, null,
                ExpectedDatabaseMutation: false),
            ct);

        var restoredTemplate = await templateCollection.ReplaceOneAsync(
            new BsonDocument("_id", templateBeforeSummaryFixtures["_id"]),
            templateBeforeSummaryFixtures,
            cancellationToken: ct);
        HarnessAssert.Equal(
            1L,
            restoredTemplate.MatchedCount,
            "P9-CLOSE restore pre-summary template provenance");
        var restoredAssignment = await assignmentCollection.ReplaceOneAsync(
            new BsonDocument("_id", assignmentBeforeSummaryFixtures["_id"]),
            assignmentBeforeSummaryFixtures,
            cancellationToken: ct);
        HarnessAssert.Equal(
            1L,
            restoredAssignment.MatchedCount,
            "P9-CLOSE restore pre-summary assignment provenance");

        var kinds = _closeBrowserSlices
            .Where(item => item.CaseId == CloseoutBrowserCaseIds[0])
            .SelectMany(item => ReadCloseoutResultKinds(item.ResultPath))
            .ToArray();
        var expected = new[]
        {
            "DIRECT_FIELD", "DIRECT_TABLE", "DIRECT_LABEL", "BASIC",
            "FLOW_BRANCH", "FLOW_STEP", "FLOW_EFFECTIVE_PATH", "FLOW_FINAL",
            "ADVANCED", "DIFF"
        };
        HarnessAssert.True(
            kinds.SequenceEqual(expected, StringComparer.Ordinal),
            $"P9-CLOSE BROWSER-01 matrix drift. Actual={string.Join(',', kinds)}");
    }

    private async Task RunCloseoutBasicSliceAsync(
        string kind,
        string sliceId,
        string? flowMode,
        CancellationToken ct)
    {
        var primeRequest = new JsonObject
        {
            ["scopeAssignmentId"] = Fixture().AssignmentId,
            ["dynamicFormTemplateId"] = Fixture().TemplateId,
            ["periodScopeMode"] = "ALL_PERIODS",
            ["includeSourceRows"] = true,
            ["sourceView"] = new JsonObject
            {
                ["page"] = 0,
                ["pageSize"] = 50
            }
        };
        if (flowMode is not null)
        {
            primeRequest["sourceScopeMode"] = flowMode;
            primeRequest["sourceFlowInstanceId"] = Fixture().FlowInstanceId;
            primeRequest["sourceFlowEffectiveStatus"] = "EFFECTIVE";
            if (flowMode == "FLOW_BRANCH")
                primeRequest["sourceFlowBranchId"] = _basFlowBranchId;
            if (flowMode == "FLOW_STEP")
                primeRequest["sourceFlowStepId"] = "P9_CORE_STEP";
        }
        _ = await ReadBasicSummaryAsync(
            BasicSummaryRoute,
            (JsonObject)primeRequest.DeepClone(),
            Actor("executor").Token,
            ct);
        await AwaitBasicJobsIdleAsync(ct);
        var cached = await ReadBasicSummaryAsync(
            BasicSummaryRoute,
            (JsonObject)primeRequest.DeepClone(),
            Actor("executor").Token,
            ct);
        var cachedMeta = BasicMeta(cached);
        HarnessAssert.True(
            cachedMeta["snapshotDirty"]?.GetValue<bool>() != true,
            $"P9-CLOSE {kind} cached snapshot remains dirty.");
        HarnessAssert.True(
            cachedMeta["isCalculating"]?.GetValue<bool>() != true,
            $"P9-CLOSE {kind} cached snapshot remains calculating.");
        HarnessAssert.True(
            RequiredJsonInt(cachedMeta, "sourceReportCount") > 0,
            $"P9-CLOSE {kind} cached snapshot has no approved source report.");

        var query = new Dictionary<string, string?>
        {
            ["dynamicFormTemplateId"] = Fixture().TemplateId,
            ["scopeType"] = "ASSIGNMENT",
            ["page"] = "0",
            ["pageSize"] = "50"
        };
        if (flowMode is not null)
        {
            query["sourceScopeMode"] = flowMode;
            query["sourceFlowInstanceId"] = Fixture().FlowInstanceId;
            query["sourceFlowEffectiveStatus"] = "EFFECTIVE";
            if (flowMode == "FLOW_BRANCH")
                query["sourceFlowBranchId"] = _basFlowBranchId;
            if (flowMode == "FLOW_STEP")
                query["sourceFlowStepId"] = "P9_CORE_STEP";
        }
        var routeKind = flowMode is null ? "BASIC" : "FLOW";
        var resultId = (await ReadBasicIdentityAsync(ct)).ConfigId;
        await RunCloseoutBrowserSliceAsync(
            new P9CloseBrowserSliceRequest(
                sliceId,
                CloseoutBrowserCaseIds[0],
                [kind],
                [new P9CloseResultJourney(
                    kind,
                    CloseoutResultPath(routeKind, resultId,
                        Fixture().AssignmentId, query),
                    "READY", false, false, null)],
                null, null, null, null, null,
                ExpectedDatabaseMutation: false),
            ct);
    }

    private async Task<P9CloseBrowserSliceEvidence> RunCloseoutBrowserSliceAsync(
        P9CloseBrowserSliceRequest request,
        CancellationToken ct)
    {
        var sliceRoot = Path.Combine(
            _paths.RunRoot,
            "browser-slices",
            request.SliceId);
        Directory.CreateDirectory(sliceRoot);
        var resultPath = Path.Combine(sliceRoot, "browser-result.json");
        var secretPath = Path.Combine(sliceRoot, "fixture.runtime.secret.json");
        var redactedPath = Path.Combine(sliceRoot, "fixture.redacted.json");
        _closeSecretManifestPaths.Add(secretPath);

        var nonce = Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
            .ToLowerInvariant();
        var manifest = BuildCloseoutBrowserManifest(
            request, sliceRoot, resultPath, nonce, true);
        var redacted = BuildCloseoutBrowserManifest(
            request, sliceRoot, resultPath, nonce, false);
        await WriteStrictJsonAsync(redactedPath, redacted, ct);
        await WriteStrictJsonAsync(secretPath, manifest, ct);

        var before = await CaptureDatabaseSnapshotAsync(ct);
        P9UiProcessResult process;
        try
        {
            process = await RunUiProcessAsync(
                "node",
                [Path.Combine(_paths.WorkspaceRoot,
                    "scripts", "p9-stat-run-closeout-browser-gate.mjs")],
                _paths.WorkspaceRoot,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["P9_CLOSE_BROWSER_FIXTURE"] = secretPath,
                    ["P9_CLOSE_BROWSER_CASE"] = request.CaseId
                },
                TimeSpan.FromMinutes(5),
                ct);
        }
        finally
        {
            DeleteCloseoutSecretManifest(secretPath);
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"P9-CLOSE browser slice {request.SliceId} failed. " +
                $"stdout={Tail(process.StdOut, 6000)};stderr={Tail(process.StdErr, 6000)}");
        var after = await CaptureDatabaseSnapshotAsync(ct);
        var deltas = BuildDeltas(before, after);
        var changed = deltas.Where(item => item.Changed).ToArray();
        var zeroWrite = changed.Length == 0;
        if (!request.ExpectedDatabaseMutation)
            HarnessAssert.True(zeroWrite,
                $"P9-CLOSE read-only browser slice {request.SliceId} changed Mongo: " +
                string.Join(',', changed.Select(item => item.Collection)));

        var validated = ValidateCloseoutBrowserSlice(
            request,
            resultPath,
            SnapshotSha256(before),
            SnapshotSha256(after),
            zeroWrite);
        _closeBrowserSlices.Add(validated);
        RecordCloseoutTransition(
            $"browser-{request.SliceId}",
            $"Real Chrome slice {request.CaseId}; no-mock result derived from service-worker policy, interception registrations, and exact proxy reconciliation.",
            before,
            after,
            request.ExpectedDatabaseMutation);
        return validated;
    }

    private JsonObject BuildCloseoutBrowserManifest(
        P9CloseBrowserSliceRequest request,
        string outputRoot,
        string resultPath,
        string nonce,
        bool containsSecrets)
    {
        JsonObject ActorNode(string key)
        {
            var actor = Actor(key);
            return new JsonObject
            {
                ["key"] = key,
                ["userId"] = actor.Id,
                ["username"] = actor.Username,
                ["token"] = containsSecrets ? actor.Token : null
            };
        }
        JsonObject? JobNode(P9CloseJobJourney? item)
            => item is null
                ? null
                : new JsonObject
                {
                    ["label"] = item.Label,
                    ["path"] = item.Path,
                    ["expectedState"] = item.ExpectedState,
                    ["actor"] = item.Actor,
                    ["serverDatabaseDeltaId"] = item.ServerDatabaseDeltaId
                };
        var matrix = new JsonArray(request.Matrix.Select(item =>
            (JsonNode)new JsonObject
            {
                ["kind"] = item.Kind,
                ["path"] = item.Path,
                ["expectedState"] = item.ExpectedState,
                ["expectDrilldown"] = item.ExpectDrilldown,
                ["expectPaging"] = item.ExpectPaging,
                ["expectedDrilldownReportId"] = item.ExpectedDrilldownReportId
            }).ToArray());
        var requiredKinds = new JsonArray(
            request.RequiredKinds.Select(value => (JsonNode)value).ToArray());
        return new JsonObject
        {
            ["schemaVersion"] = "P9_CLOSE_BROWSER_FIXTURE_V1",
            ["chainId"] = ChainId,
            ["runKey"] = $"{_runKey}:{request.SliceId}",
            ["nonce"] = nonce,
            ["containsSecrets"] = containsSecrets,
            ["doNotPublish"] = containsSecrets,
            ["outputRoot"] = Path.GetFullPath(outputRoot),
            ["resultPath"] = Path.GetFullPath(resultPath),
            ["backendOrigin"] = RequireBackend().BaseUri.GetLeftPart(UriPartial.Authority),
            ["tokenStorageKey"] = "tdtd_access_token",
            ["frontend"] = new JsonObject
            {
                ["distRoot"] = Path.GetFullPath(Path.Combine(
                    _paths.WorkspaceRoot, "tdtd-fe", "dist"))
            },
            ["actors"] = new JsonObject
            {
                ["executor"] = ActorNode("executor"),
                ["executor2"] = ActorNode("executor2"),
                ["admin"] = ActorNode("admin"),
                ["outsider"] = ActorNode("outsider")
            },
            ["routes"] = new JsonObject
            {
                ["runs"] = CloseoutRunsPath()
            },
            ["jobs"] = new JsonObject
            {
                ["queued"] = JobNode(new P9CloseJobJourney(
                    "queued",
                    CloseoutJobPath(_closeQueuedJobId ?? _closeApprovedJobId ?? ObjectId.GenerateNewId().ToString()),
                    "QUEUED",
                    "admin",
                    "queued-job")),
                ["approved"] = JobNode(request.Approved),
                ["reversed"] = JobNode(request.Reversed),
                ["rebuilt"] = JobNode(request.Rebuilt)
            },
            ["results"] = new JsonObject
            {
                ["requiredKinds"] = requiredKinds,
                ["matrix"] = matrix,
                ["export"] = request.ExportPath is null
                    ? null
                    : new JsonObject { ["path"] = request.ExportPath }
            }
        };
    }

    private string CloseoutRunsPath()
        => $"/works/{Uri.EscapeDataString(Fixture().WorkId)}/statistics/" +
           $"{Uri.EscapeDataString(Fixture().AssignmentId)}/runs";

    private string CloseoutJobPath(
        string? jobId,
        string? scopePathId = null)
        => $"/works/{Uri.EscapeDataString(Fixture().WorkId)}/statistics/" +
           $"{Uri.EscapeDataString(scopePathId ?? Fixture().AssignmentId)}/runs/" +
           Uri.EscapeDataString(jobId ?? string.Empty);

    private string CloseoutResultPath(
        string kind,
        string resultId,
        string scopePathId,
        IReadOnlyDictionary<string, string?> query)
    {
        var pairs = query
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(item =>
                $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value!)}");
        return $"/works/{Uri.EscapeDataString(Fixture().WorkId)}/statistics/" +
               $"{Uri.EscapeDataString(scopePathId)}/results/" +
               $"{Uri.EscapeDataString(kind)}/{Uri.EscapeDataString(resultId)}?" +
               string.Join('&', pairs);
    }

    private static IReadOnlyDictionary<string, string?> MergeCloseoutQuery(
        IReadOnlyDictionary<string, string?> source,
        params (string Key, string? Value)[] changes)
    {
        var result = source.ToDictionary(item => item.Key, item => item.Value,
            StringComparer.Ordinal);
        foreach (var change in changes)
            result[change.Key] = change.Value;
        return result;
    }

    private void RecordCloseoutTransition(
        string id,
        string purpose,
        IReadOnlyDictionary<string, P9CollectionState> before,
        IReadOnlyDictionary<string, P9CollectionState> after,
        bool expectedMutation)
    {
        var deltas = BuildDeltas(before, after);
        var changed = deltas.Any(item => item.Changed);
        _closeDatabaseTransitions.Add(new P9CloseDatabaseTransition(
            id,
            purpose,
            SnapshotSha256(before),
            SnapshotSha256(after),
            deltas,
            expectedMutation,
            expectedMutation ? changed : !changed));
    }
}

internal sealed record P9CloseResultJourney(
    string Kind,
    string Path,
    string ExpectedState,
    bool ExpectDrilldown,
    bool ExpectPaging,
    string? ExpectedDrilldownReportId);

internal sealed record P9CloseJobJourney(
    string Label,
    string Path,
    string ExpectedState,
    string Actor,
    string ServerDatabaseDeltaId);

internal sealed record P9CloseBrowserSliceRequest(
    string SliceId,
    string CaseId,
    IReadOnlyList<string> RequiredKinds,
    IReadOnlyList<P9CloseResultJourney> Matrix,
    string? ExportPath,
    P9CloseJobJourney? Approved,
    P9CloseJobJourney? Reversed,
    P9CloseJobJourney? Rebuilt,
    string? Reserved,
    bool ExpectedDatabaseMutation);
