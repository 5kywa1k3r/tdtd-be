using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Bounded P6-06 evidence for FLOW-T08. It exercises the real report-review
/// lifecycle, durable return advance, new assignment materialization,
/// immutable attempt lineage, terminal cycle exhaustion, ACL/replay barriers,
/// read/timeline fields, direct Mongo ownership and guarded cleanup.
/// </summary>
internal static partial class P5MaterializationProbe
{
    public static async Task<int> RunP606Async()
    {
        var runKey =
            $"p606_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}";
        var paths = HarnessPaths.Create(runKey);
        var iterationRoot = paths.IterationRoot(1);
        var cleanupErrors = new List<string>();
        var exchanges = new List<ApiExchangeEvidence>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        ApiHarnessClient? apiClient = null;
        object? barrierEvidence = null;
        object? lifecycleEvidence = null;
        object? exhaustedEvidence = null;
        object? directMongoEvidence = null;
        string? failure = null;
        var passed = false;

        try
        {
            mongo = await MongoReplicaSetLease.StartAsync(
                paths,
                iterationRoot,
                runKey,
                1,
                CancellationToken.None);
            var database = mongo.Client.GetDatabase(mongo.DatabaseName);
            backend = await StartP603BackendAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                "backend-p6-review-loop",
                8);
            var api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            var (adminToken, adminPassword) = await BootstrapAndLoginAsync(
                api,
                backend,
                database,
                CancellationToken.None);
            var baseFixture = await SeedFixtureAsync(
                database,
                CancellationToken.None);

            var successSeed = await SeedP601FixtureAsync(
                database,
                baseFixture,
                CancellationToken.None);
            var successFixture = await ConvertP601FixtureToP606Async(
                database,
                successSeed,
                maxReviewCycles: 2,
                CancellationToken.None);
            await RenameP603FixtureAsync(
                database,
                successSeed,
                "P6_06_REVIEW_FLOW_MAIN",
                "P6-06-REVIEW-MAIN",
                CancellationToken.None);

            var exhaustedSeed = await SeedP601FixtureAsync(
                database,
                baseFixture,
                CancellationToken.None);
            var exhaustedFixture = await ConvertP601FixtureToP606Async(
                database,
                exhaustedSeed,
                maxReviewCycles: 1,
                CancellationToken.None);
            await RenameP603FixtureAsync(
                database,
                exhaustedSeed,
                "P6_06_REVIEW_FLOW_MAX",
                "P6-06-REVIEW-MAX",
                CancellationToken.None);

            barrierEvidence = await AssertP606FutureBarrierAsync(
                api,
                adminToken,
                database,
                baseFixture,
                CancellationToken.None);

            var launch = await LaunchAsync(
                api,
                adminToken,
                baseFixture,
                successFixture.WorkId,
                successFixture.VersionId,
                "p6-06-review-launch",
                [successFixture.TargetUnitId],
                P601PeriodKey,
                CancellationToken.None);
            RequireStatus(
                launch.Confirm,
                "SUCCEEDED",
                "P6-06 review-loop launch");
            var instanceId = ApiHarnessClient.RequiredString(
                launch.Confirm.Json,
                "flowInstanceId");
            var initialStep = (await LoadP601StepsAsync(
                    database,
                    instanceId,
                    CancellationToken.None))
                .Single();
            var initialAssignmentId = initialStep.AssignmentId
                                      ?? throw new InvalidOperationException(
                                          "P6-06 initial attempt lacks assignment.");
            var submitted = await SubmitP606StepAsync(
                api,
                backend,
                adminToken,
                database,
                initialStep,
                "success-a1",
                CancellationToken.None);

            var outsiderToken = await PrepareP601ActorLoginAsync(
                api,
                backend,
                database,
                successSeed.OutsiderUserId,
                CancellationToken.None);
            var returnRequest = await BuildP606ReturnRequestAsync(
                database,
                submitted.ReportId,
                "p6-06-return-success",
                CancellationToken.None);
            var beforeAcl = await CaptureP601LedgerHashAsync(
                database,
                instanceId,
                CancellationToken.None);
            var outsiderReturn = await api.PostAsync(
                $"api/work-assignment-review/reports/{submitted.ReportId}/return",
                returnRequest,
                outsiderToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                outsiderReturn,
                HttpStatusCode.Forbidden,
                "P6-06 outsider return");
            Require(
                beforeAcl == await CaptureP601LedgerHashAsync(
                    database,
                    instanceId,
                    CancellationToken.None),
                "P6-06 ACL rejection changed the direct-Mongo ledger.");

            var returned = await api.PostAsync(
                $"api/work-assignment-review/reports/{submitted.ReportId}/return",
                returnRequest,
                adminToken,
                ct: CancellationToken.None);
            Require(
                returned.StatusCode is
                    HttpStatusCode.OK or
                    HttpStatusCode.Accepted,
                "P6-06 reviewer return was rejected.");
            initialStep = await WaitForP601StepStateAsync(
                api,
                adminToken,
                database,
                initialStep.Id,
                DynamicFlowStepStates.Returned,
                CancellationToken.None);
            var nextStep = await WaitForP606NextAttemptAsync(
                database,
                instanceId,
                initialStep.Id,
                CancellationToken.None);

            // Crash/restart boundary: the review-return transaction and its
            // pending runtime outbox are already durable, but attempt 2 has
            // not been materialized yet.
            exchanges.AddRange(api.Exchanges);
            api.Dispose();
            apiClient = null;
            await backend.StopAsync();
            await backend.DisposeAsync();
            backend = await StartP603BackendAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                "backend-p6-review-loop-restart",
                8);
            api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            adminToken = await api.LoginAsync(
                "admin",
                adminPassword,
                CancellationToken.None);

            await ProcessP606RuntimeOutboxAsync(
                api,
                adminToken,
                CancellationToken.None);
            await WaitForP601AssignmentAsync(
                database,
                nextStep.AssignmentId!,
                CancellationToken.None);
            nextStep = await WaitForP601StepStateAsync(
                api,
                adminToken,
                database,
                nextStep.Id,
                DynamicFlowStepStates.Assigned,
                CancellationToken.None);

            var beforeReplay = await CaptureP601LedgerHashAsync(
                database,
                instanceId,
                CancellationToken.None);
            var replay = await api.PostAsync(
                $"api/work-assignment-review/reports/{submitted.ReportId}/return",
                returnRequest,
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                replay,
                HttpStatusCode.OK,
                "P6-06 exact return replay");
            await ProcessP606LifecycleOutboxAsync(
                api,
                adminToken,
                CancellationToken.None);
            Require(
                beforeReplay == await CaptureP601LedgerHashAsync(
                    database,
                    instanceId,
                    CancellationToken.None),
                "P6-06 exact return replay created duplicate lineage.");

            nextStep = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                nextStep,
                null,
                null,
                "p606-success-a2",
                CancellationToken.None);
            await CompleteP601AssignmentAsync(
                api,
                adminToken,
                database,
                nextStep,
                CancellationToken.None);
            await WaitForP602InstanceStateAsync(
                api,
                adminToken,
                database,
                instanceId,
                DynamicFlowInstanceStates.Completed,
                CancellationToken.None);

            lifecycleEvidence = new
            {
                aclZeroWrite = true,
                exactReplayZeroWrite = true,
                backendRestartRecovered = true,
                initialStepInstanceId = initialStep.Id,
                initialAssignmentId,
                returnedReportId = submitted.ReportId,
                nextStepInstanceId = nextStep.Id,
                nextAssignmentId = nextStep.AssignmentId,
                initialReviewCycleNo = 1,
                nextReviewCycleNo = 2
            };

            var exhaustedLaunch = await LaunchAsync(
                api,
                adminToken,
                baseFixture,
                exhaustedFixture.WorkId,
                exhaustedFixture.VersionId,
                "p6-06-review-max-launch",
                [exhaustedFixture.TargetUnitId],
                P601PeriodKey,
                CancellationToken.None);
            RequireStatus(
                exhaustedLaunch.Confirm,
                "SUCCEEDED",
                "P6-06 max-cycle launch");
            var exhaustedInstanceId = ApiHarnessClient.RequiredString(
                exhaustedLaunch.Confirm.Json,
                "flowInstanceId");
            var exhaustedStep = (await LoadP601StepsAsync(
                    database,
                    exhaustedInstanceId,
                    CancellationToken.None))
                .Single();
            var exhaustedSubmit = await SubmitP606StepAsync(
                api,
                backend,
                adminToken,
                database,
                exhaustedStep,
                "max-a1",
                CancellationToken.None);
            var exhaustedRequest = await BuildP606ReturnRequestAsync(
                database,
                exhaustedSubmit.ReportId,
                "p6-06-return-exhausted",
                CancellationToken.None);
            var exhaustedReturn = await api.PostAsync(
                $"api/work-assignment-review/reports/{exhaustedSubmit.ReportId}/return",
                exhaustedRequest,
                adminToken,
                ct: CancellationToken.None);
            Require(
                exhaustedReturn.StatusCode is
                    HttpStatusCode.OK or
                    HttpStatusCode.Accepted,
                "P6-06 max-cycle return was rejected.");
            await WaitForP601StepStateAsync(
                api,
                adminToken,
                database,
                exhaustedStep.Id,
                DynamicFlowStepStates.Returned,
                CancellationToken.None);
            await WaitForP602InstanceStateAsync(
                api,
                adminToken,
                database,
                exhaustedInstanceId,
                DynamicFlowInstanceStates.Failed,
                CancellationToken.None);
            var exhaustedSteps = await LoadP601StepsAsync(
                database,
                exhaustedInstanceId,
                CancellationToken.None);
            var exhaustedInstance = await LoadP601InstanceAsync(
                database,
                exhaustedInstanceId,
                CancellationToken.None);
            var exhaustedEvents = await database
                .GetCollection<DynamicFlowRuntimeEvent>(
                    "dynamic_flow_runtime_events")
                .Find(item =>
                    item.FlowInstanceId == exhaustedInstanceId)
                .ToListAsync(CancellationToken.None);
            Require(
                exhaustedSteps.Count == 1 &&
                exhaustedInstance.LastErrorCode ==
                DynamicFlowReviewLoopTopologyContract.CycleExhausted &&
                exhaustedEvents.Count(item =>
                    item.EventType ==
                    DynamicFlowReviewLoopTopologyContract
                        .ReviewCycleExhaustedEvent) == 1,
                "P6-06 maxReviewCycles did not terminalize exactly once.");
            exhaustedEvidence = new
            {
                exhaustedInstanceId,
                maxReviewCycles = 1,
                attemptCount = exhaustedSteps.Count,
                exhaustedInstance.State,
                exhaustedInstance.LastErrorCode,
                stableTerminalEvent = true
            };

            directMongoEvidence = await AssertP606DirectMongoAsync(
                api,
                adminToken,
                database,
                successFixture,
                instanceId,
                initialStep.Id,
                nextStep.Id,
                submitted.ReportId,
                CancellationToken.None);
            passed = true;
        }
        catch (Exception error)
        {
            failure = $"{error.GetType().Name}: {error.Message}";
            Console.Error.WriteLine(error);
        }
        finally
        {
            if (apiClient is not null)
            {
                exchanges.AddRange(apiClient.Exchanges);
                apiClient.Dispose();
            }
            if (backend is not null)
            {
                try { await backend.StopAsync(); }
                catch (Exception error)
                {
                    cleanupErrors.Add($"backend-stop: {error.Message}");
                }
                await backend.DisposeAsync();
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
                    cleanupErrors.Add($"database-drop: {error.Message}");
                }
                try { await mongo.StopProcessAsync(); }
                catch (Exception error)
                {
                    cleanupErrors.Add($"mongo-stop: {error.Message}");
                }
                try { mongo.RemoveDataDirectoryGuarded(); }
                catch (Exception error)
                {
                    cleanupErrors.Add(
                        $"mongo-data-remove: {error.Message}");
                }
                await mongo.DisposeAsync();
            }
        }

        passed = passed && cleanupErrors.Count == 0;
        await EvidenceJson.WriteAsync(
            Path.Combine(
                iterationRoot,
                "p6-06-review-loop-api-exchanges.json"),
            exchanges);
        await EvidenceJson.WriteAsync(
            Path.Combine(
                iterationRoot,
                "p6-06-review-loop-direct-mongo.json"),
            new
            {
                runKey,
                barrierEvidence,
                lifecycleEvidence,
                exhaustedEvidence,
                directMongoEvidence
            });
        await EvidenceJson.WriteAsync(
            Path.Combine(
                iterationRoot,
                "p6-06-review-loop-result.json"),
            new
            {
                runKey,
                verdict = passed ? "PASS" : "FAIL",
                requirements = new[] { "P6-TOPO-009" },
                failure,
                cleanupErrors,
                artifactRoot = iterationRoot,
                completedAtUtc = DateTime.UtcNow
            });
        Console.WriteLine(
            passed
                ? $"[PASS] P6-06 review-loop probe passed; artifact={Path.Combine(iterationRoot, "p6-06-review-loop-result.json")}"
                : $"[FAIL] P6-06 review-loop probe failed: {failure ?? string.Join("; ", cleanupErrors)}; artifact={Path.Combine(iterationRoot, "p6-06-review-loop-result.json")}");
        return passed ? 0 : 1;
    }

    private static async Task<P606Fixture> ConvertP601FixtureToP606Async(
        IMongoDatabase database,
        P601Fixture seed,
        int maxReviewCycles,
        CancellationToken ct)
    {
        var rootForm = seed.ExpectedSteps
            .Single(step => step.NodeId == "step_a").Form;
        var payload = JsonNode.Parse(seed.CanonicalPayload)?.AsObject()
                      ?? throw new InvalidOperationException(
                          "P6-06 seed payload is invalid.");
        payload["archetypeId"] =
            DynamicFlowReviewLoopTopologyContract.ArchetypeId;
        payload.Remove("resultOwnerStepId");
        payload.Remove("resultOwnerFormNodeId");
        payload.Remove("statisticsOwnerStepId");
        payload.Remove("statisticsOwnerFormNodeId");
        payload["formNodes"] = new JsonArray(
            new JsonObject
            {
                ["formNodeId"] = rootForm.FormNodeId,
                ["role"] = "ROOT",
                ["dynamicFormTemplateId"] =
                    rootForm.FormVersionId,
                ["dynamicFormFamilyId"] =
                    rootForm.FormFamilyId,
                ["dynamicFormVersionNo"] =
                    rootForm.FormVersionNo,
                ["dynamicFormSchemaHash"] =
                    rootForm.FormSchemaHash,
                ["dynamicFormSnapshotHash"] =
                    rootForm.FormSchemaHash
            });
        payload["nodes"] = new JsonArray(
            P602Step("step_a", "REVIEW", rootForm.FormNodeId),
            new JsonObject
            {
                ["nodeId"] = "review_gateway",
                ["nodeCode"] = "REVIEW_GATE",
                ["nodeKind"] = DynamicFlowNodeKinds.Gateway,
                ["gateway"] = new JsonObject
                {
                    ["kind"] = DynamicFlowGatewayKinds.Review,
                    ["reviewRole"] = "REVIEWER"
                }
            });
        payload["edges"] = new JsonArray(
            P602Edge("edge-review-marker", "step_a", "review_gateway"));
        payload["finalResultPolicy"] = new JsonObject
        {
            ["maxReviewCycles"] = maxReviewCycles
        };
        var canonical =
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                payload.ToJsonString(),
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true));
        var topology = DynamicFlowReviewLoopTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        Require(
            topology.MaxReviewCycles == maxReviewCycles &&
            topology.ReviewNode.NodeId == "step_a" &&
            topology.ReviewGateway.NodeId == "review_gateway",
            "P6-06 fixture topology drifted.");
        await database.GetCollection<DynamicFlowTemplateVersion>(
                "dynamic_flow_template_versions")
            .UpdateOneAsync(
                item => item.Id == seed.VersionId,
                Builders<DynamicFlowTemplateVersion>.Update
                    .Set(item => item.PayloadJson, canonical.CanonicalJson)
                    .Set(item => item.PayloadHash, canonical.PayloadHash),
                cancellationToken: ct);
        await database.GetCollection<DynamicFlowTemplate>(
                "dynamic_flow_templates")
            .UpdateOneAsync(
                item => item.Id == seed.FamilyId,
                Builders<DynamicFlowTemplate>.Update.Set(
                    item => item.CurrentVersionHash,
                    canonical.PayloadHash),
                cancellationToken: ct);
        return new P606Fixture(
            seed.WorkId,
            seed.VersionId,
            seed.TargetUnitId,
            canonical.CanonicalJson,
            canonical.PayloadHash,
            maxReviewCycles);
    }

    private static async Task<P606SubmittedReport> SubmitP606StepAsync(
        ApiHarnessClient api,
        BackendServerLease backend,
        string adminToken,
        IMongoDatabase database,
        DynamicFlowStepInstance step,
        string suffix,
        CancellationToken ct)
    {
        var assignmentId = step.AssignmentId
                           ?? throw new InvalidOperationException(
                               "P6-06 attempt lacks assignment.");
        var period = await WaitForP601PeriodAsync(
            database,
            assignmentId,
            ct);
        var participantToken = await PrepareP601ActorLoginAsync(
            api,
            backend,
            database,
            step.ParticipantUserIds.Single(),
            ct);
        var open = await api.PostAsync(
            $"api/work-report-periods/{period.Id}/open",
            body: null,
            participantToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            open,
            HttpStatusCode.OK,
            $"P6-06 open {suffix}");
        var reportId = ApiHarnessClient.RequiredString(open.Json, "id");
        var submit = await api.PostAsync(
            $"api/work-assignment-reports/{reportId}/submit",
            new JsonObject
            {
                ["expectedPayloadRevision"] =
                    ApiHarnessClient.RequiredInt(
                        open.Json,
                        "payloadRevision"),
                ["expectedLifecycleRevision"] =
                    ApiHarnessClient.RequiredInt(
                        open.Json,
                        "lifecycleRevision"),
                ["commandId"] = $"p6-06-submit-{suffix}",
                ["values1D"] = new JsonArray(0),
                ["fieldValuesJson"] = null,
                ["tableValuesJson"] = null,
                ["dataOrigin"] = "MANUAL_INPUT",
                ["cumulativeContributionMode"] = "INCLUDE"
            },
            participantToken,
            ct: ct);
        Require(
            submit.StatusCode is
                HttpStatusCode.OK or
                HttpStatusCode.Accepted,
            $"P6-06 submit {suffix} failed.");
        await WaitForP601StepStateAsync(
            api,
            adminToken,
            database,
            step.Id,
            DynamicFlowStepStates.Submitted,
            ct);
        return new P606SubmittedReport(
            reportId,
            participantToken,
            period.Id);
    }

    private static async Task<JsonObject> BuildP606ReturnRequestAsync(
        IMongoDatabase database,
        string reportId,
        string commandId,
        CancellationToken ct)
    {
        var report = await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .Find(item => item.Id == reportId && !item.IsDeleted)
            .SingleAsync(ct);
        return new JsonObject
        {
            ["expectedPayloadRevision"] = report.PayloadRevision,
            ["expectedLifecycleRevision"] =
                report.LifecycleRevision,
            ["commandId"] = commandId,
            ["comment"] = "P6-06 return for exact resubmit attempt"
        };
    }

    private static async Task<DynamicFlowStepInstance>
        WaitForP606NextAttemptAsync(
            IMongoDatabase database,
            string instanceId,
            string previousStepId,
            CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            var next = await database
                .GetCollection<DynamicFlowStepInstance>(
                    "dynamic_flow_step_instances")
                .Find(item =>
                    item.FlowInstanceId == instanceId &&
                    item.PreviousAttemptStepInstanceId ==
                    previousStepId &&
                    !item.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (next is not null)
                return next;
            await Task.Delay(50, ct);
        }
        throw new InvalidOperationException(
            "P6-06 return did not durably create the next attempt.");
    }

    private static async Task ProcessP606LifecycleOutboxAsync(
        ApiHarnessClient api,
        string adminToken,
        CancellationToken ct)
    {
        var response = await api.PostAsync(
            "api/admin/operations/job-runs/lifecycle-projection-outbox/process?maxReports=20",
            body: null,
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            "P6-06 lifecycle projection worker");
    }

    private static async Task ProcessP606RuntimeOutboxAsync(
        ApiHarnessClient api,
        string adminToken,
        CancellationToken ct)
    {
        var response = await api.PostAsync(
            "api/admin/operations/dynamic-flow-runtime/outbox/process?maxItems=20",
            body: null,
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            "P6-06 runtime materialization worker");
    }

    private static async Task<object> AssertP606FutureBarrierAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        ProbeFixture fixture,
        CancellationToken ct)
    {
        var rows = new List<object>();
        foreach (var barrier in fixture.BarrierFixtures
                     .Where(item =>
                         string.CompareOrdinal(
                             item.ArchetypeId,
                             "FLOW-T09") >= 0)
                     .OrderBy(
                         item => item.ArchetypeId,
                         StringComparer.Ordinal))
        {
            var request = new DynamicFlowPreflightRequest
            {
                FlowTemplateVersionId = barrier.VersionId,
                CommandId =
                    $"p6-06-blocked-{barrier.ArchetypeId.ToLowerInvariant()}",
                TargetUnitIds = [fixture.TargetUnitIds[0]],
                PeriodKey = $"2026-07-{barrier.ArchetypeId}",
                ScheduleIdentityJson = "{}"
            };
            var before = await CaptureP601BusinessCountsAsync(
                database,
                ct);
            var preflight = await api.PostAsync(
                $"api/works/{barrier.WorkId}/dynamic-flows/preflight",
                request,
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                preflight,
                HttpStatusCode.OK,
                $"{barrier.ArchetypeId} blocked preflight");
            Require(
                ApiHarnessClient.RequiredString(
                    preflight.Json,
                    "eligibility") ==
                DynamicFlowRuntimeEligibilityPolicy.BlockedPhase,
                $"{barrier.ArchetypeId} must remain blocked.");
            var confirm = await api.PostAsync(
                $"api/works/{barrier.WorkId}/dynamic-flows/confirm",
                new DynamicFlowConfirmRequest
                {
                    FlowTemplateVersionId =
                        request.FlowTemplateVersionId,
                    CommandId = request.CommandId,
                    TargetUnitIds = request.TargetUnitIds,
                    PeriodKey = request.PeriodKey,
                    ScheduleIdentityJson =
                        request.ScheduleIdentityJson,
                    SnapshotToken =
                        ApiHarnessClient.RequiredString(
                            preflight.Json,
                            "snapshotToken")
                },
                adminToken,
                ct: ct);
            var after = await CaptureP601BusinessCountsAsync(
                database,
                ct);
            Require(
                ApiHarnessClient.RequiredString(
                    confirm.Json,
                    "status") ==
                "BLOCKED_UNTIL_TARGET_PHASE" &&
                before.All(pair =>
                    after.TryGetValue(pair.Key, out var count) &&
                    count == pair.Value),
                $"{barrier.ArchetypeId} crossed zero-write.");
            rows.Add(new { barrier.ArchetypeId, zeroWrite = true });
        }
        Require(
            rows.Count == 4,
            "P6-06 must verify the complete T09..T12 barrier.");
        return new { count = rows.Count, rows };
    }

    private static async Task<object> AssertP606DirectMongoAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        P606Fixture fixture,
        string instanceId,
        string firstStepId,
        string secondStepId,
        string returnedReportId,
        CancellationToken ct)
    {
        var instance = await LoadP601InstanceAsync(
            database,
            instanceId,
            ct);
        var topology = DynamicFlowReviewLoopTopologyContract.Require(
            instance.TopologySnapshotJson,
            instance.TopologySnapshotHash);
        var steps = await LoadP601StepsAsync(
            database,
            instanceId,
            ct);
        var first = steps.Single(item => item.Id == firstStepId);
        var second = steps.Single(item => item.Id == secondStepId);
        var assignments = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .Find(item =>
                item.FlowInstanceId == instanceId &&
                !item.IsDeleted)
            .ToListAsync(ct);
        var firstAssignment = assignments.Single(item =>
            item.Id == first.AssignmentId);
        var secondAssignment = assignments.Single(item =>
            item.Id == second.AssignmentId);
        var reports = await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .Find(item =>
                assignments.Select(assignment => assignment.Id)
                    .Contains(item.WorkAssignmentId) &&
                !item.IsDeleted)
            .ToListAsync(ct);
        var receipts = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                "dynamic_flow_runtime_command_receipts")
            .Find(item => item.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        var events = await database
            .GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events")
            .Find(item => item.FlowInstanceId == instanceId)
            .SortBy(item => item.Sequence)
            .ToListAsync(ct);
        var outbox = await database
            .GetCollection<DynamicFlowRuntimeOutboxItem>(
                "dynamic_flow_runtime_outbox")
            .Find(item => item.FlowInstanceId == instanceId)
            .ToListAsync(ct);

        Require(
            instance.State == DynamicFlowInstanceStates.Completed &&
            instance.ArchetypeId ==
            DynamicFlowReviewLoopTopologyContract.ArchetypeId &&
            instance.TopologySnapshotJson ==
            fixture.CanonicalPayload &&
            instance.TopologySnapshotHash == fixture.PayloadHash &&
            topology.MaxReviewCycles == fixture.MaxReviewCycles,
            "P6-06 instance/topology pins drifted.");
        Require(
            steps.Count == 2 &&
            first.AttemptNo == 1 &&
            first.ReviewCycleNo == 1 &&
            first.State == DynamicFlowStepStates.Returned &&
            first.SupersededByStepInstanceId == second.Id &&
            second.AttemptNo == 2 &&
            second.ReviewCycleNo == 2 &&
            second.PreviousAttemptStepInstanceId == first.Id &&
            second.PreviousAttemptAssignmentId ==
            first.AssignmentId &&
            second.SupersededByStepInstanceId is null &&
            second.State == DynamicFlowStepStates.Completed &&
            first.FlowStepId == second.FlowStepId &&
            first.BranchId == second.BranchId &&
            first.FormVersionId == second.FormVersionId &&
            first.ParticipantSnapshotId ==
            second.ParticipantSnapshotId &&
            first.Id != second.Id &&
            first.AssignmentId != second.AssignmentId,
            "P6-06 immutable attempt lineage drifted.");
        Require(
            assignments.Count == 2 &&
            firstAssignment.FlowEffectiveStatus ==
            DynamicFlowEffectiveStatuses.Invalidated &&
            firstAssignment.InvalidatedByFlowEventId is not null &&
            secondAssignment.FlowEffectiveStatus ==
            DynamicFlowEffectiveStatuses.Effective &&
            reports.Any(item =>
                item.Id == returnedReportId &&
                item.WorkAssignmentId ==
                firstAssignment.Id) &&
            reports.Any(item =>
                item.WorkAssignmentId ==
                secondAssignment.Id &&
                item.Id != returnedReportId),
            "P6-06 reused or failed to invalidate old artifacts.");
        Require(
            receipts.Count(item =>
                item.CommandType ==
                DynamicFlowReviewLoopTopologyContract
                    .ReviewAdvanceCommand) == 1 &&
            events.Count(item =>
                item.EventType ==
                DynamicFlowReviewLoopTopologyContract
                    .ReviewAttemptCreatedEvent) == 1 &&
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeEventTypes
                    .ReviewAttemptAssignmentMaterialized) == 1 &&
            events.Select(item => item.Sequence).SequenceEqual(
                Enumerable.Range(1, events.Count)
                    .Select(value => (long)value)) &&
            outbox.Count == 2 &&
            outbox.Count(item =>
                item.Operation ==
                DynamicFlowRuntimeMaterializationOperations
                    .MaterializeEntryAssignment) == 1 &&
            outbox.Count(item =>
                item.Operation ==
                DynamicFlowRuntimeMaterializationOperations
                    .MaterializeReviewAttemptAssignment) == 1 &&
            outbox.All(item =>
                item.Status ==
                DynamicFlowRuntimeOutboxStatuses.Completed) &&
            outbox.Select(item => item.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() == outbox.Count,
            "P6-06 receipt/event/outbox ledger duplicated or drifted.");

        var stepRead = await api.GetAsync(
            $"api/works/{fixture.WorkId}/dynamic-flows/instances/{instanceId}/steps?limit=50",
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            stepRead,
            HttpStatusCode.OK,
            "P6-06 step read");
        var readRows = ApiHarnessClient.RequiredArray(
            ApiHarnessClient.RequiredObject(
                stepRead.Json,
                "P6-06 step page")["items"],
            "P6-06 step rows")
            .Select(item =>
                ApiHarnessClient.RequiredObject(
                    item,
                    "P6-06 step row"))
            .ToArray();
        var firstRead = readRows.Single(item =>
            ApiHarnessClient.RequiredString(
                item,
                "stepInstanceId") == first.Id);
        var secondRead = readRows.Single(item =>
            ApiHarnessClient.RequiredString(
                item,
                "stepInstanceId") == second.Id);
        Require(
            ApiHarnessClient.RequiredInt(
                firstRead,
                "reviewCycleNo") == 1 &&
            ApiHarnessClient.RequiredInt(
                secondRead,
                "reviewCycleNo") == 2 &&
            ApiHarnessClient.RequiredInt(
                secondRead,
                "maxReviewCycles") ==
            fixture.MaxReviewCycles &&
            !ApiHarnessClient.RequiredBool(
                firstRead,
                "isCanonicalAttempt") &&
            ApiHarnessClient.RequiredBool(
                secondRead,
                "isCanonicalAttempt"),
            "P6-06 read contract omitted attempt authority.");
        var timeline = await api.GetAsync(
            $"api/works/{fixture.WorkId}/dynamic-flows/instances/{instanceId}/timeline?limit=100",
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            timeline,
            HttpStatusCode.OK,
            "P6-06 timeline read");
        Require(
            timeline.Json?.ToJsonString().Contains(
                DynamicFlowReviewLoopTopologyContract
                    .ReviewAttemptCreatedEvent,
                StringComparison.Ordinal) == true,
            "P6-06 timeline omitted the return-attempt event.");

        return new
        {
            instanceId,
            topologyHash = instance.TopologySnapshotHash,
            maxReviewCycles = topology.MaxReviewCycles,
            stepCount = steps.Count,
            assignmentCount = assignments.Count,
            reportCount = reports.Count,
            receiptCount = receipts.Count,
            eventCount = events.Count,
            outboxCount = outbox.Count,
            oneActiveAttempt = true,
            immutableLineage = true,
            oldArtifactsReadonly = true,
            readAndTimelineVerified = true,
            duplicateCount = 0,
            orphanCount = 0
        };
    }

    private sealed record P606Fixture(
        string WorkId,
        string VersionId,
        string TargetUnitId,
        string CanonicalPayload,
        string PayloadHash,
        int MaxReviewCycles);

    private sealed record P606SubmittedReport(
        string ReportId,
        string ParticipantToken,
        string PeriodId);
}
