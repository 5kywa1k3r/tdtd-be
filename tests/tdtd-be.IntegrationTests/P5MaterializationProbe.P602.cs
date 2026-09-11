using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Bounded P6-02 executable evidence for the exact FLOW-T04 A -> FORK -> B/C
/// contract. The probe never mutates CURRENT; an exact non-current v1.3 pin
/// requires explicit Testing activation.
/// </summary>
internal static partial class P5MaterializationProbe
{
    private const string P602LaunchCommand = "p6-02-t04-launch";
    private const string P602ForkCommand = "p6-02-t04-fork-a-bc";
    private const string P602PeriodKey = P601PeriodKey;

    public static async Task<int> RunP602Async()
    {
        var runKey = $"p602_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}";
        var paths = HarnessPaths.Create(runKey);
        var iterationRoot = paths.IterationRoot(1);
        var cleanupErrors = new List<string>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        ApiHarnessClient? apiClient = null;
        var exchanges = new List<ApiExchangeEvidence>();
        object? directMongoEvidence = null;
        object? barrierEvidence = null;
        object? recoveryEvidence = null;
        object? regressionEvidence = null;
        object? raceEvidence = null;
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
            var serverRoot = Path.Combine(iterationRoot, "backend-p6-parallel-fork");
            Directory.CreateDirectory(serverRoot);
            backend = await BackendServerLease.StartAsync(
                paths,
                serverRoot,
                runKey,
                mongo,
                CancellationToken.None,
                new BackendServerOptions
                {
                    EnableDynamicFlowRuntimeCandidate = true,
                    DynamicFlowRuntimeCandidateActivationThrough = 4,
                    DynamicFlowRuntimeFaultCommandId = P602ForkCommand,
                    DynamicFlowRuntimeFaultBranchOrdinal = 2,
                    DynamicFlowRuntimeFaultPoints =
                    [
                        DynamicFlowRuntimeFaultPoints.AfterAssignmentWrite
                    ]
                });

            var api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            var (adminToken, adminPassword) = await BootstrapAndLoginAsync(
                api,
                backend,
                database,
                CancellationToken.None);
            var p5Fixture = await SeedFixtureAsync(database, CancellationToken.None);
            var t03Fixture = await SeedP601FixtureAsync(
                database,
                p5Fixture,
                CancellationToken.None);
            regressionEvidence = await AssertP602RegressionAsync(
                api,
                adminToken,
                database,
                p5Fixture,
                t03Fixture,
                CancellationToken.None);
            await RenameP602RegressionFixtureAsync(
                database,
                t03Fixture,
                CancellationToken.None);
            var forkSeed = await SeedP601FixtureAsync(
                database,
                p5Fixture,
                CancellationToken.None);
            var fixture = await ConvertP601FixtureToP602Async(
                database,
                forkSeed,
                CancellationToken.None);
            barrierEvidence = await AssertP602FutureBarrierAsync(
                api,
                adminToken,
                database,
                p5Fixture,
                CancellationToken.None);

            var launch = await LaunchAsync(
                api,
                adminToken,
                p5Fixture,
                fixture.WorkId,
                fixture.VersionId,
                P602LaunchCommand,
                [fixture.TargetUnitId],
                P602PeriodKey,
                CancellationToken.None);
            RequireStatus(launch.Confirm, "SUCCEEDED", "P6-02 FLOW-T04 launch");
            var instanceId = ApiHarnessClient.RequiredString(
                launch.Confirm.Json,
                "flowInstanceId");
            var launchSteps = await LoadP601StepsAsync(
                database,
                instanceId,
                CancellationToken.None);
            Require(
                launchSteps.Count == 1 &&
                launchSteps.Single().FlowStepId == "step_a",
                "FLOW-T04 launch must materialize only entry step A.");
            var stepA = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                launchSteps.Single(),
                "field_note",
                "P5_NOTE",
                "p602-a",
                CancellationToken.None);
            var capabilityA = await ReadP601ForwardCapabilityAsync(
                api,
                adminToken,
                fixture.WorkId,
                instanceId,
                stepA.Id,
                expectedCanForward: true,
                CancellationToken.None);
            var assignmentA = stepA.AssignmentId
                              ?? throw new InvalidOperationException(
                                  "FLOW-T04 entry step lacks assignment.");

            var fanOutTasks = Enumerable.Range(0, 2)
                .Select(_ => ForwardP601Async(
                    api,
                    adminToken,
                    fixture.WorkId,
                    assignmentA,
                    P602ForkCommand,
                    capabilityA.InstanceRevision,
                    capabilityA.StepRevision,
                    CancellationToken.None))
                .ToArray();
            var fanOutResults = await Task.WhenAll(fanOutTasks);
            foreach (var response in fanOutResults)
                AssertP601ForwardSuccess(response, replayed: null, "A -> FORK -> B/C");
            Require(
                fanOutResults.Count(response =>
                    ApiHarnessClient.RequiredBool(response.Json, "replayed")) == 1,
                "Concurrent fork must yield one winner and one exact replay.");
            AssertP602SameForkResult(
                fanOutResults[0],
                fanOutResults[1],
                "concurrent fork replay");
            var branchRows = ReadP602ActivatedBranches(fanOutResults[0]);
            Require(
                branchRows.Count == 2 &&
                branchRows.Select(row => row.NodeCode)
                    .SequenceEqual(new[] { "B", "C" }, StringComparer.Ordinal) &&
                branchRows.Select(row => row.BranchId)
                    .Distinct(StringComparer.Ordinal).Count() == 2 &&
                branchRows.Select(row => row.ContributionId)
                    .Distinct(StringComparer.Ordinal).Count() == 2 &&
                branchRows.Select(row => row.GatewayInstanceId)
                    .Distinct(StringComparer.Ordinal).Count() == 1,
                "Fork response did not expose the exact independent B/C branch set.");

            var afterReplay = await CaptureP601LedgerHashAsync(
                database,
                instanceId,
                CancellationToken.None);
            var changedReplay = await ForwardP601Async(
                api,
                adminToken,
                fixture.WorkId,
                assignmentA,
                P602ForkCommand,
                capabilityA.InstanceRevision,
                capabilityA.StepRevision + 1,
                CancellationToken.None);
            AssertP601Conflict(
                changedReplay,
                "DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT",
                "changed fork replay");
            var staleFork = await ForwardP601Async(
                api,
                adminToken,
                fixture.WorkId,
                assignmentA,
                "p6-02-t04-stale-fork",
                capabilityA.InstanceRevision,
                capabilityA.StepRevision,
                CancellationToken.None);
            AssertP601Conflict(
                staleFork,
                "DYNAMIC_FLOW_FORWARD_PARENT_NOT_APPROVED",
                "stale fork after parent consumption",
                HttpStatusCode.BadRequest);
            Require(
                afterReplay == await CaptureP601LedgerHashAsync(
                    database,
                    instanceId,
                    CancellationToken.None),
                "Changed replay or stale fork changed the durable ledger.");

            var failedOutbox = await WaitForP602InjectedFailureAsync(
                database,
                instanceId,
                CancellationToken.None);
            exchanges.AddRange(api.Exchanges);
            api.Dispose();
            apiClient = null;
            await backend.StopAsync();
            await backend.DisposeAsync();
            backend = null;

            await database.GetCollection<DynamicFlowRuntimeOutboxItem>(
                    "dynamic_flow_runtime_outbox")
                .UpdateOneAsync(
                    item =>
                        item.Id == failedOutbox.Id &&
                        item.Status == DynamicFlowRuntimeOutboxStatuses.Failed,
                    Builders<DynamicFlowRuntimeOutboxItem>.Update
                        .Set(item => item.NextAttemptAtUtc, DateTime.UtcNow.AddMinutes(30)),
                    cancellationToken: CancellationToken.None);

            var restartedServerRoot = Path.Combine(
                iterationRoot,
                "backend-p6-parallel-fork-restarted");
            Directory.CreateDirectory(restartedServerRoot);
            backend = await BackendServerLease.StartAsync(
                paths,
                restartedServerRoot,
                runKey,
                mongo,
                CancellationToken.None,
                new BackendServerOptions
                {
                    EnableDynamicFlowRuntimeCandidate = true,
                    DynamicFlowRuntimeCandidateActivationThrough = 4
                });
            api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            adminToken = await api.LoginAsync(
                "admin",
                adminPassword,
                CancellationToken.None);

            recoveryEvidence = await AssertP602RecoveryAsync(
                api,
                adminToken,
                database,
                instanceId,
                failedOutbox.Id,
                CancellationToken.None);
            var forkSteps = (await LoadP601StepsAsync(
                    database,
                    instanceId,
                    CancellationToken.None))
                .Where(step => step.FlowStepId is "step_b" or "step_c")
                .OrderBy(step => step.FlowStepCode, StringComparer.Ordinal)
                .ToList();
            Require(
                forkSteps.Count == 2,
                "Recovered fork does not contain exactly B/C.");
            for (var index = 0; index < forkSteps.Count; index++)
            {
                forkSteps[index] = await ApproveP601StepAsync(
                    api,
                    backend,
                    adminToken,
                    database,
                    forkSteps[index],
                    null,
                    null,
                    $"p602-{forkSteps[index].FlowStepCode.ToLowerInvariant()}",
                    CancellationToken.None);
            }

            var completeTasks = forkSteps.Select(step =>
            {
                var assignmentId = step.AssignmentId
                                   ?? throw new InvalidOperationException(
                                       $"FLOW-T04 step {step.FlowStepCode} lacks assignment.");
                return api.PostAsync(
                    $"api/work-assignments/{assignmentId}/complete",
                    new JsonObject
                    {
                        ["completedDate"] = "2026-07-27T00:00:00Z",
                        ["note"] = $"P6-02 concurrent branch {step.FlowStepCode} completion"
                    },
                    adminToken,
                    ct: CancellationToken.None);
            }).ToArray();
            var completeResults = await Task.WhenAll(completeTasks);
            foreach (var response in completeResults)
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.OK,
                    "P6-02 concurrent fork branch completion");
            foreach (var step in forkSteps)
            {
                await WaitForP601StepStateAsync(
                    api,
                    adminToken,
                    database,
                    step.Id,
                    DynamicFlowStepStates.Completed,
                    CancellationToken.None);
            }
            var completedInstance = await WaitForP602InstanceStateAsync(
                api,
                adminToken,
                database,
                instanceId,
                DynamicFlowInstanceStates.Completed,
                CancellationToken.None);
            raceEvidence = new
            {
                concurrentCompletionRequests = completeResults.Length,
                completedBranchCount = forkSteps.Count,
                instanceState = completedInstance.State,
                exactlyOneTerminalAggregate = true
            };

            directMongoEvidence = await AssertP602DirectMongoAsync(
                database,
                fixture,
                instanceId,
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
                try
                {
                    await backend.StopAsync();
                }
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
                    await mongo.DropDatabaseGuardedAsync(CancellationToken.None);
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"database-drop: {error.Message}");
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
                    cleanupErrors.Add($"mongo-data-remove: {error.Message}");
                }
                await mongo.DisposeAsync();
            }
        }

        passed = passed && cleanupErrors.Count == 0;
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "p6-02-parallel-fork-api-exchanges.json"),
            exchanges);
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "p6-02-parallel-fork-direct-mongo.json"),
            new
            {
                runKey,
                directMongoEvidence,
                barrierEvidence,
                recoveryEvidence,
                regressionEvidence,
                raceEvidence
            });
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "p6-02-parallel-fork-result.json"),
            new
            {
                runKey,
                verdict = passed ? "PASS" : "FAIL",
                candidate = new
                {
                    version = DynamicFlowP6CatalogCandidate.Version,
                    semanticHash = DynamicFlowP6CatalogCandidate.SemanticHash,
                    currentCatalogModified = false,
                    testingActivationOnly =
                        !DynamicFlowP6CatalogCandidate.ActivationEnabled
                },
                requirements = new[] { "P6-TOPO-005" },
                failure,
                cleanupErrors,
                artifactRoot = iterationRoot,
                completedAtUtc = DateTime.UtcNow
            });

        Console.WriteLine(
            passed
                ? $"[PASS] P6-02 parallel-fork Kestrel/Mongo probe passed; artifact={Path.Combine(iterationRoot, "p6-02-parallel-fork-result.json")}"
                : $"[FAIL] P6-02 parallel-fork Kestrel/Mongo probe failed: {failure ?? string.Join("; ", cleanupErrors)}; artifact={Path.Combine(iterationRoot, "p6-02-parallel-fork-result.json")}");
        return passed ? 0 : 1;
    }

    private static async Task<P602Fixture> ConvertP601FixtureToP602Async(
        IMongoDatabase database,
        P601Fixture seed,
        CancellationToken ct)
    {
        var rootForm = seed.ExpectedSteps.Single(step => step.NodeId == "step_a").Form;
        var branchBForm = seed.ExpectedSteps.Single(step => step.NodeId == "step_b").Form;
        var branchCForm = seed.ExpectedSteps.Single(step => step.NodeId == "step_c").Form;
        var payload = JsonNode.Parse(seed.CanonicalPayload)?.AsObject()
                      ?? throw new InvalidOperationException(
                          "P6-02 seed payload is invalid.");
        payload["archetypeId"] = DynamicFlowParallelForkTopologyContract.ArchetypeId;
        payload.Remove("resultOwnerStepId");
        payload.Remove("resultOwnerFormNodeId");
        payload.Remove("statisticsOwnerStepId");
        payload.Remove("statisticsOwnerFormNodeId");
        payload["nodes"] = new JsonArray(
            P602Step("step_a", "A", rootForm.FormNodeId),
            new JsonObject
            {
                ["nodeId"] = "fork_f",
                ["nodeCode"] = "F",
                ["nodeKind"] = DynamicFlowNodeKinds.Gateway,
                ["gateway"] = new JsonObject
                {
                    ["kind"] = DynamicFlowGatewayKinds.Fork
                }
            },
            P602Step("step_b", "B", branchBForm.FormNodeId),
            P602Step("step_c", "C", branchCForm.FormNodeId));
        payload["edges"] = new JsonArray(
            P602Edge("tr_a_f", "step_a", "fork_f"),
            P602Edge("tr_f_c", "fork_f", "step_c"),
            P602Edge("tr_f_b", "fork_f", "step_b"));
        var canonical = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            payload.ToJsonString(),
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true,
                AllowHistoricalCatalogPins: true));
        var topology = DynamicFlowParallelForkTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        Require(
            topology.Branches.Select(branch => branch.Node.NodeCode)
                .SequenceEqual(new[] { "B", "C" }, StringComparer.Ordinal),
            "P6-02 fixture did not canonicalize to exact B/C.");

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
                Builders<DynamicFlowTemplate>.Update
                    .Set(item => item.CurrentVersionHash, canonical.PayloadHash),
                cancellationToken: ct);
        return new P602Fixture(
            seed.WorkId,
            seed.FamilyId,
            seed.VersionId,
            canonical.CanonicalJson,
            canonical.PayloadHash,
            seed.TargetUnitId,
            rootForm,
            branchBForm,
            branchCForm);
    }

    private static JsonObject P602Step(
        string nodeId,
        string nodeCode,
        string formNodeId)
        => new()
        {
            ["nodeId"] = nodeId,
            ["nodeCode"] = nodeCode,
            ["nodeKind"] = DynamicFlowNodeKinds.FormStep,
            ["formNodeId"] = formNodeId,
            ["declaredRoles"] = new JsonArray("OWNER")
        };

    private static JsonObject P602Edge(
        string transitionId,
        string fromNodeId,
        string toNodeId)
        => new()
        {
            ["transitionId"] = transitionId,
            ["fromNodeId"] = fromNodeId,
            ["toNodeId"] = toNodeId
        };

    private static async Task RenameP602RegressionFixtureAsync(
        IMongoDatabase database,
        P601Fixture fixture,
        CancellationToken ct)
    {
        await database.GetCollection<DynamicFlowTemplate>(
                "dynamic_flow_templates")
            .UpdateOneAsync(
                item => item.Id == fixture.FamilyId,
                Builders<DynamicFlowTemplate>.Update
                    .Set(item => item.Code, "P6_02_REGRESSION_FLOW_T03"),
                cancellationToken: ct);
        await database.GetCollection<Work>("works")
            .UpdateOneAsync(
                item => item.Id == fixture.WorkId,
                Builders<Work>.Update
                    .Set(item => item.AutoCode, "P6-02-REGRESSION-T03")
                    .Set(item => item.Code, "P6-02-REG-T03"),
                cancellationToken: ct);
        await database.GetCollection<Work>("works")
            .UpdateOneAsync(
                item => item.Id == fixture.OwnerOracleWorkId,
                Builders<Work>.Update
                    .Set(item => item.AutoCode, "P6-02-REGRESSION-T03-OWNER")
                    .Set(item => item.Code, "P6-02-REG-T03-OWNER"),
                cancellationToken: ct);
    }

    private static async Task<object> AssertP602RegressionAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        ProbeFixture p5Fixture,
        P601Fixture t03Fixture,
        CancellationToken ct)
    {
        var p5 = await AssertP601P5RegressionAsync(
            api,
            adminToken,
            database,
            p5Fixture,
            ct);
        var t03 = await LaunchAsync(
            api,
            adminToken,
            p5Fixture,
            t03Fixture.WorkId,
            t03Fixture.VersionId,
            "p6-02-regression-t03-launch",
            [t03Fixture.TargetUnitId],
            "2026-07-P602-T03",
            ct);
        RequireStatus(t03.Confirm, "SUCCEEDED", "P6-02 FLOW-T03 regression");
        var instanceId = ApiHarnessClient.RequiredString(
            t03.Confirm.Json,
            "flowInstanceId");
        var steps = await LoadP601StepsAsync(database, instanceId, ct);
        Require(
            steps.Count == 1 &&
            steps[0].FlowStepId == "step_a" &&
            steps[0].GatewayInstanceId is null &&
            steps[0].ContributionId is null,
            "FLOW-T03 regression launch drifted after P6-02.");
        return new
        {
            t01T02 = p5,
            t03 = new
            {
                instanceId,
                stepCount = steps.Count,
                archetype = DynamicFlowSequentialTopologyContract.ArchetypeId,
                noForkMetadata = true
            }
        };
    }

    private static async Task<object> AssertP602FutureBarrierAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        ProbeFixture fixture,
        CancellationToken ct)
    {
        var rows = new List<object>();
        foreach (var barrier in fixture.BarrierFixtures
                     .Where(item =>
                         string.CompareOrdinal(item.ArchetypeId, "FLOW-T05") >= 0)
                     .OrderBy(item => item.ArchetypeId, StringComparer.Ordinal))
        {
            var commandId = $"p6-02-blocked-{barrier.ArchetypeId.ToLowerInvariant()}";
            var request = new DynamicFlowPreflightRequest
            {
                FlowTemplateVersionId = barrier.VersionId,
                CommandId = commandId,
                TargetUnitIds = [fixture.TargetUnitIds[0]],
                PeriodKey = $"2026-07-{barrier.ArchetypeId}",
                ScheduleIdentityJson =
                    "{\"timezone\":\"Asia/Ho_Chi_Minh\",\"cadence\":\"once\",\"anchor\":\"2026-07-27\"}"
            };
            var before = await CaptureP601BusinessCountsAsync(database, ct);
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
                ApiHarnessClient.RequiredString(preflight.Json, "eligibility") ==
                DynamicFlowRuntimeEligibilityPolicy.BlockedPhase,
                $"{barrier.ArchetypeId} must remain P6 phase-blocked.");
            var confirm = await api.PostAsync(
                $"api/works/{barrier.WorkId}/dynamic-flows/confirm",
                new DynamicFlowConfirmRequest
                {
                    FlowTemplateVersionId = request.FlowTemplateVersionId,
                    CommandId = request.CommandId,
                    TargetUnitIds = request.TargetUnitIds,
                    PeriodKey = request.PeriodKey,
                    ScheduleIdentityJson = request.ScheduleIdentityJson,
                    SnapshotToken = ApiHarnessClient.RequiredString(
                        preflight.Json,
                        "snapshotToken")
                },
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                confirm,
                HttpStatusCode.OK,
                $"{barrier.ArchetypeId} blocked confirm");
            Require(
                ApiHarnessClient.RequiredString(confirm.Json, "status") ==
                "BLOCKED_UNTIL_TARGET_PHASE" &&
                !ApiHarnessClient.RequiredBool(confirm.Json, "businessWritePerformed"),
                $"{barrier.ArchetypeId} crossed the P6-02 write boundary.");
            var after = await CaptureP601BusinessCountsAsync(database, ct);
            Require(
                before.All(pair =>
                    after.TryGetValue(pair.Key, out var count) &&
                    count == pair.Value),
                $"{barrier.ArchetypeId} blocked request changed business counts.");
            rows.Add(new
            {
                barrier.ArchetypeId,
                confirmStatus = "BLOCKED_UNTIL_TARGET_PHASE",
                directMongoZeroWrite = true
            });
        }
        Require(rows.Count == 8, "P6-02 must verify the complete T05..T12 barrier.");
        return new { count = rows.Count, rows };
    }

    private static void AssertP602SameForkResult(
        ApiHarnessResponse left,
        ApiHarnessResponse right,
        string context)
    {
        AssertP601SameForwardResult(left, right, context);
        Require(
            ApiHarnessClient.RequiredString(left.Json, "gatewayInstanceId") ==
            ApiHarnessClient.RequiredString(right.Json, "gatewayInstanceId"),
            $"{context} changed gateway identity.");
        var leftBranches = ApiHarnessClient.RequiredArray(
            ApiHarnessClient.RequiredObject(left.Json, context)["activatedBranches"],
            $"{context} left activated branches");
        var rightBranches = ApiHarnessClient.RequiredArray(
            ApiHarnessClient.RequiredObject(right.Json, context)["activatedBranches"],
            $"{context} right activated branches");
        Require(
            leftBranches.ToJsonString() == rightBranches.ToJsonString(),
            $"{context} changed its exact branch result.");
    }

    private static IReadOnlyList<P602ActivatedBranch> ReadP602ActivatedBranches(
        ApiHarnessResponse response)
    {
        var body = ApiHarnessClient.RequiredObject(
            response.Json,
            "P6-02 fork response");
        return ApiHarnessClient.RequiredArray(
                body["activatedBranches"],
                "P6-02 activated branches")
            .Select(item =>
            {
                var row = ApiHarnessClient.RequiredObject(
                    item,
                    "P6-02 activated branch");
                return new P602ActivatedBranch(
                    ApiHarnessClient.RequiredString(row, "stepInstanceId"),
                    ApiHarnessClient.RequiredString(row, "assignmentId"),
                    ApiHarnessClient.RequiredString(row, "branchId"),
                    ApiHarnessClient.RequiredString(row, "parentBranchId"),
                    ApiHarnessClient.RequiredString(row, "gatewayInstanceId"),
                    ApiHarnessClient.RequiredString(row, "contributionId"),
                    ApiHarnessClient.RequiredString(row, "transitionId"),
                    ApiHarnessClient.RequiredString(row, "nodeId"),
                    ApiHarnessClient.RequiredString(row, "nodeCode"));
            })
            .ToList();
    }

    private static async Task<DynamicFlowRuntimeOutboxItem>
        WaitForP602InjectedFailureAsync(
            IMongoDatabase database,
            string instanceId,
            CancellationToken ct)
    {
        var collection = database.GetCollection<DynamicFlowRuntimeOutboxItem>(
            "dynamic_flow_runtime_outbox");
        for (var attempt = 1; attempt <= 80; attempt++)
        {
            var items = await collection
                .Find(item => item.FlowInstanceId == instanceId)
                .ToListAsync(ct);
            var failed = items.SingleOrDefault(item =>
                item.Operation ==
                DynamicFlowRuntimeMaterializationOperations
                    .MaterializeForkBranchAssignment &&
                item.Payload.TryGetValue("commandId", out var commandId) &&
                commandId.IsString &&
                commandId.AsString == P602ForkCommand &&
                item.Status == DynamicFlowRuntimeOutboxStatuses.Failed &&
                item.LastErrorCode == "DYNAMIC_FLOW_RUNTIME_INJECTED_FAILURE");
            if (failed is not null)
            {
                Require(
                    items.Count == 3 &&
                    items.Count(item =>
                        item.Operation ==
                        DynamicFlowRuntimeMaterializationOperations
                            .MaterializeForkBranchAssignment) == 2 &&
                    items.Count(item =>
                        item.Operation ==
                        DynamicFlowRuntimeMaterializationOperations
                            .MaterializeForkBranchAssignment &&
                        item.Status == DynamicFlowRuntimeOutboxStatuses.Completed) == 1,
                    "Fork crash boundary did not retain one completed and one failed branch.");
                return failed;
            }
            await Task.Delay(100, ct);
        }
        throw new InvalidOperationException(
            "P6-02 did not observe the injected fork-branch materialization failure.");
    }

    private static async Task<object> AssertP602RecoveryAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        string instanceId,
        string failedOutboxId,
        CancellationToken ct)
    {
        var outbox = database.GetCollection<DynamicFlowRuntimeOutboxItem>(
            "dynamic_flow_runtime_outbox");
        var failed = await outbox
            .Find(item => item.Id == failedOutboxId)
            .SingleAsync(ct);
        Require(
            failed.Operation ==
            DynamicFlowRuntimeMaterializationOperations
                .MaterializeForkBranchAssignment &&
            failed.Status == DynamicFlowRuntimeOutboxStatuses.Failed &&
            failed.CompletedAtUtc is null,
            "Restart did not preserve the failed fork outbox.");

        var operationPath =
            $"api/admin/operations/dynamic-flow-runtime/instances/{instanceId}/reconcile";
        var preview = await api.PostAsync(
            $"{operationPath}?apply=false",
            new { },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            preview,
            HttpStatusCode.OK,
            "P6-02 recovery dry-run");
        Require(
            !ApiHarnessClient.RequiredBool(preview.Json, "converged"),
            "P6-02 recovery dry-run did not detect fork drift.");

        var attempts = new List<object>();
        var converged = false;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var response = await api.PostAsync(
                $"{operationPath}?apply=true",
                new { },
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                response,
                HttpStatusCode.OK,
                $"P6-02 recovery apply {attempt}");
            var currentConverged =
                ApiHarnessClient.RequiredBool(response.Json, "converged");
            var exact =
                ApiHarnessClient.RequiredBool(response.Json, "exactLedgerConverged");
            attempts.Add(new { attempt, currentConverged, exact });
            if (currentConverged && exact)
            {
                converged = true;
                break;
            }
        }
        Require(converged, "P6-02 fork recovery did not converge.");

        var items = await outbox
            .Find(item => item.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        var steps = await LoadP601StepsAsync(database, instanceId, ct);
        var assignments = await database.GetCollection<WorkAssignment>(
                "work_assignments")
            .Find(item => item.FlowInstanceId == instanceId && !item.IsDeleted)
            .ToListAsync(ct);
        var branchAssignments = assignments
            .Where(item => item.FlowStepId is "step_b" or "step_c")
            .ToList();
        var bindingCollection = database.GetCollection<BsonDocument>(
            "work_template_assignees");
        var periodCollection = database.GetCollection<BsonDocument>(
            "work_report_periods");
        var bindingCount = 0L;
        var periodCount = 0L;
        foreach (var assignment in branchAssignments)
        {
            var assignmentId = ObjectId.Parse(assignment.Id);
            bindingCount += await bindingCollection.CountDocumentsAsync(
                new BsonDocument("workAssignmentId", assignmentId),
                cancellationToken: ct);
            periodCount += await periodCollection.CountDocumentsAsync(
                new BsonDocument("workAssignmentId", assignmentId),
                cancellationToken: ct);
        }
        Require(
            items.Count == 3 &&
            items.All(item =>
                item.Status == DynamicFlowRuntimeOutboxStatuses.Completed &&
                item.CompletedAtUtc.HasValue) &&
            steps.Count == 3 &&
            assignments.Count == 3 &&
            branchAssignments.Count == 2 &&
            bindingCount == 2 &&
            periodCount == 2,
            "P6-02 recovery left duplicates, orphans, or incomplete branch projections.");
        var replay = await api.PostAsync(
            $"{operationPath}?apply=true",
            new { },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            replay,
            HttpStatusCode.OK,
            "P6-02 recovery replay");
        Require(
            ApiHarnessClient.RequiredBool(replay.Json, "converged") &&
            ApiHarnessClient.RequiredBool(replay.Json, "exactLedgerConverged"),
            "P6-02 recovery replay did not stay converged.");
        return new
        {
            faultInjected = true,
            faultPoint = DynamicFlowRuntimeFaultPoints.AfterAssignmentWrite,
            failedBranchOutboxId = failed.Id,
            serverRestarted = true,
            dryRunDetected = true,
            applyConverged = true,
            attempts,
            replayConverged = true,
            stepCount = steps.Count,
            assignmentCount = assignments.Count,
            branchBindingCount = bindingCount,
            branchPeriodCount = periodCount,
            duplicateCount = 0,
            orphanCount = 0
        };
    }

    private static async Task<DynamicFlowInstance> WaitForP602InstanceStateAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        string instanceId,
        string expectedState,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            var instance = await LoadP601InstanceAsync(database, instanceId, ct);
            if (instance.State == expectedState)
                return instance;
            var worker = await api.PostAsync(
                "api/admin/operations/job-runs/lifecycle-projection-outbox/process?maxReports=20",
                body: null,
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                worker,
                HttpStatusCode.OK,
                $"P6-02 lifecycle worker for instance {expectedState}");
            await Task.Delay(50, ct);
        }
        var terminal = await LoadP601InstanceAsync(database, instanceId, ct);
        throw new InvalidOperationException(
            $"Instance {instanceId} did not reach {expectedState}; actual={terminal.State}.");
    }

    private static async Task<object> AssertP602DirectMongoAsync(
        IMongoDatabase database,
        P602Fixture fixture,
        string instanceId,
        CancellationToken ct)
    {
        var instance = await LoadP601InstanceAsync(database, instanceId, ct);
        var topology = DynamicFlowParallelForkTopologyContract.Require(
            instance.TopologySnapshotJson,
            instance.TopologySnapshotHash);
        Require(
            instance.WorkId == fixture.WorkId &&
            instance.FlowTemplateId == fixture.FamilyId &&
            instance.FlowTemplateVersionId == fixture.VersionId &&
            instance.CatalogVersion == DynamicFlowP6CatalogCandidate.Version &&
            instance.CatalogSemanticHash ==
            DynamicFlowP6CatalogCandidate.SemanticHash &&
            instance.ArchetypeId ==
            DynamicFlowParallelForkTopologyContract.ArchetypeId &&
            instance.FlowPayloadHash == fixture.PayloadHash &&
            instance.TopologySnapshotHash == fixture.PayloadHash &&
            instance.TopologySnapshotJson == fixture.CanonicalPayload &&
            instance.State == DynamicFlowInstanceStates.Completed,
            "P6-02 instance immutable pins or terminal state drifted.");
        Require(
            topology.EntryNode.NodeId == "step_a" &&
            topology.ForkNode.NodeId == "fork_f" &&
            topology.Branches.Select(branch => branch.Node.NodeId)
                .SequenceEqual(new[] { "step_b", "step_c" }, StringComparer.Ordinal),
            "P6-02 durable topology is not exact A -> FORK -> B/C.");

        var steps = await LoadP601StepsAsync(database, instanceId, ct);
        var assignments = await database.GetCollection<WorkAssignment>(
                "work_assignments")
            .Find(item => item.FlowInstanceId == instanceId && !item.IsDeleted)
            .ToListAsync(ct);
        Require(
            steps.Count == 3 &&
            assignments.Count == 3 &&
            steps.All(step => step.State == DynamicFlowStepStates.Completed) &&
            steps.Select(step => step.Id)
                .Distinct(StringComparer.Ordinal).Count() == 3 &&
            assignments.Select(item => item.Id)
                .Distinct(StringComparer.Ordinal).Count() == 3,
            "P6-02 final step/assignment cardinality drifted.");

        var rootBranchId = DynamicFlowParallelForkTopologyContract.BuildRootBranchId(
            instance.Id,
            instance.ExecutionEpoch,
            fixture.TargetUnitId);
        var gatewayInstanceId =
            DynamicFlowParallelForkTopologyContract.BuildGatewayInstanceId(
                instance.Id,
                instance.ExecutionEpoch,
                topology.ForkNode.NodeId,
                rootBranchId);
        var entry = steps.Single(step => step.FlowStepId == "step_a");
        var entryAssignment = assignments.Single(item => item.Id == entry.AssignmentId);
        Require(
            entry.BranchId == rootBranchId &&
            entry.GatewayInstanceId is null &&
            entry.GatewayVersion is null &&
            entry.ContributionId is null &&
            entry.NextNodeIds.SequenceEqual(
                new[] { topology.ForkNode.NodeId },
                StringComparer.Ordinal) &&
            entryAssignment.ParentFlowBranchId is null,
            "P6-02 entry/root branch pins drifted.");

        var expectedForms = new Dictionary<string, ProbeFormPinSnapshot>(
            StringComparer.Ordinal)
        {
            ["step_b"] = fixture.BranchBForm,
            ["step_c"] = fixture.BranchCForm
        };
        var branchRows = new List<object>();
        foreach (var branch in topology.Branches)
        {
            var step = steps.Single(item => item.FlowStepId == branch.Node.NodeId);
            var expectedBranchId =
                DynamicFlowParallelForkTopologyContract.BuildBranchId(
                    instance.Id,
                    instance.ExecutionEpoch,
                    gatewayInstanceId,
                    branch.Edge.TransitionId,
                    fixture.TargetUnitId);
            var expectedContributionId =
                DynamicFlowParallelForkTopologyContract.BuildContributionId(
                    gatewayInstanceId,
                    expectedBranchId,
                    branch.Edge.TransitionId);
            var expectedStepId =
                DynamicFlowParallelForkTopologyContract.BuildStepInstanceId(
                    instance.Id,
                    instance.ExecutionEpoch,
                    branch.Node.NodeId,
                    expectedBranchId,
                    1);
            var expectedAssignmentId =
                DynamicFlowParallelForkTopologyContract.BuildAssignmentId(
                    expectedStepId);
            var expectedForm = expectedForms[branch.Node.NodeId];
            var assignment = assignments.Single(item =>
                item.Id == expectedAssignmentId);
            Require(
                step.Id == expectedStepId &&
                step.AssignmentId == expectedAssignmentId &&
                step.BranchId == expectedBranchId &&
                step.ActivatedByTransitionId == branch.Edge.TransitionId &&
                step.GatewayInstanceId == gatewayInstanceId &&
                step.GatewayVersion == 1 &&
                step.ContributionId == expectedContributionId &&
                step.FormNodeId == expectedForm.FormNodeId &&
                step.FormFamilyId == expectedForm.FormFamilyId &&
                step.FormVersionId == expectedForm.FormVersionId &&
                step.FormVersionNo == expectedForm.FormVersionNo &&
                step.FormSchemaHash == expectedForm.FormSchemaHash &&
                step.FormSnapshotHash == expectedForm.FormSchemaHash &&
                step.IsTerminalNode &&
                step.NextNodeIds.Count == 0 &&
                step.ResultOwnerIdentity ==
                DynamicFlowParallelForkTopologyContract.BuildStepOwnerIdentity(
                    instance.Id,
                    instance.ExecutionEpoch,
                    branch.Node.NodeId,
                    expectedBranchId,
                    "result") &&
                step.StatisticOwnerIdentity ==
                DynamicFlowParallelForkTopologyContract.BuildStepOwnerIdentity(
                    instance.Id,
                    instance.ExecutionEpoch,
                    branch.Node.NodeId,
                    expectedBranchId,
                    "statistics") &&
                assignment.FlowBranchId == expectedBranchId &&
                assignment.ParentFlowBranchId == rootBranchId &&
                assignment.IsFlowFinalNode == true,
                $"P6-02 deterministic branch pins drifted at {branch.Node.NodeId}.");
            branchRows.Add(new
            {
                nodeId = branch.Node.NodeId,
                branchId = expectedBranchId,
                parentBranchId = rootBranchId,
                gatewayInstanceId,
                gatewayVersion = 1,
                contributionId = expectedContributionId,
                stepId = expectedStepId,
                assignmentId = expectedAssignmentId,
                resultOwnerIdentity = step.ResultOwnerIdentity,
                statisticOwnerIdentity = step.StatisticOwnerIdentity
            });
        }
        Require(
            steps.Select(step => step.AssignmentId)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(assignments.Select(item => item.Id)),
            "P6-02 contains an orphan step or assignment.");

        var receipts = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                "dynamic_flow_runtime_command_receipts")
            .Find(item => item.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        var forwardReceipts = receipts
            .Where(item => item.CommandType == "FORWARD")
            .ToList();
        Require(
            forwardReceipts.Count == 1 &&
            forwardReceipts[0].CommandId == P602ForkCommand &&
            forwardReceipts[0].Status ==
            DynamicFlowRuntimeCommandStatuses.Succeeded &&
            forwardReceipts[0].ResultSnapshot is not null &&
            forwardReceipts[0].ResultSnapshotHash ==
            DynamicFlowParallelForkTopologyContract.Hash(
                forwardReceipts[0].ResultSnapshot.ToJson()),
            "P6-02 fork receipt ledger drifted.");

        var outbox = await database.GetCollection<DynamicFlowRuntimeOutboxItem>(
                "dynamic_flow_runtime_outbox")
            .Find(item => item.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        Require(
            outbox.Count == 3 &&
            outbox.Count(item =>
                item.Operation ==
                DynamicFlowRuntimeMaterializationOperations
                    .MaterializeEntryAssignment) == 1 &&
            outbox.Count(item =>
                item.Operation ==
                DynamicFlowRuntimeMaterializationOperations
                    .MaterializeForkBranchAssignment) == 2 &&
            outbox.All(item =>
                item.Status == DynamicFlowRuntimeOutboxStatuses.Completed &&
                item.CompletedAtUtc.HasValue &&
                item.PayloadHash ==
                DynamicFlowParallelForkTopologyContract.Hash(item.Payload.ToJson())) &&
            outbox.Select(item => item.Id)
                .Distinct(StringComparer.Ordinal).Count() == outbox.Count &&
            outbox.Select(item => item.DedupeKey)
                .Distinct(StringComparer.Ordinal).Count() == outbox.Count,
            "P6-02 outbox ledger has duplicates, orphans, or incomplete rows.");

        var events = await database.GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events")
            .Find(item => item.FlowInstanceId == instanceId)
            .SortBy(item => item.Sequence)
            .ToListAsync(ct);
        Require(
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeEventTypes.ParallelForkAccepted) == 1 &&
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeEventTypes.ForkBranchAssignmentMaterialized) == 2 &&
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeStateProjectionEventTypes.InstanceCompleted) == 1 &&
            events.Select(item => item.Id)
                .Distinct(StringComparer.Ordinal).Count() == events.Count &&
            events.Select(item => item.Sequence)
                .SequenceEqual(
                    Enumerable.Range(1, events.Count).Select(value => (long)value)) &&
            events.All(item =>
                item.ExecutionEpoch == instance.ExecutionEpoch &&
                item.PayloadHash ==
                DynamicFlowParallelForkTopologyContract.Hash(item.Payload.ToJson())) &&
            instance.NextEventSequence == events.Count + 1,
            "P6-02 event chain is not exact and contiguous.");
        var forkEvents = events.Where(item =>
                item.EventType ==
                DynamicFlowRuntimeEventTypes.ForkBranchAssignmentMaterialized)
            .ToList();
        Require(
            forkEvents.Select(item => item.BranchId)
                .Distinct(StringComparer.Ordinal).Count() == 2 &&
            forkEvents.Select(item => item.ContributionId)
                .Distinct(StringComparer.Ordinal).Count() == 2 &&
            forkEvents.All(item =>
                item.GatewayInstanceId == gatewayInstanceId &&
                item.GatewayVersion == 1),
            "P6-02 branch event ownership metadata drifted.");

        return new
        {
            instanceId,
            rootBranchId,
            gatewayInstanceId,
            branchCount = branchRows.Count,
            branches = branchRows,
            stepCount = steps.Count,
            assignmentCount = assignments.Count,
            receiptCount = receipts.Count,
            forwardReceiptCount = forwardReceipts.Count,
            outboxCount = outbox.Count,
            eventCount = events.Count,
            forkAcceptedEventCount = 1,
            forkMaterializedEventCount = 2,
            instanceCompletedEventCount = 1,
            duplicateCount = 0,
            orphanCount = 0,
            exactPins = true,
            independentBranchOwnerIdentity = true,
            terminalState = instance.State
        };
    }

    private sealed record P602Fixture(
        string WorkId,
        string FamilyId,
        string VersionId,
        string CanonicalPayload,
        string PayloadHash,
        string TargetUnitId,
        ProbeFormPinSnapshot RootForm,
        ProbeFormPinSnapshot BranchBForm,
        ProbeFormPinSnapshot BranchCForm);

    private sealed record P602ActivatedBranch(
        string StepInstanceId,
        string AssignmentId,
        string BranchId,
        string ParentBranchId,
        string GatewayInstanceId,
        string ContributionId,
        string TransitionId,
        string NodeId,
        string NodeCode);
}


