using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Bounded P6-03 executable evidence for FLOW-T05 versioned JOIN ALL. The
/// probe owns one immutable two-contribution gateway, injects a transactional
/// contribution failure, restarts the server, and proves exact convergence.
/// </summary>
internal static partial class P5MaterializationProbe
{
    private const string P603LaunchCommand = "p6-03-t05-launch";
    private const string P603ForwardCommand = "p6-03-t05-fanout";
    private const string P603PeriodKey = P601PeriodKey;

    public static async Task<int> RunP603Async()
    {
        var runKey = $"p603_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}";
        var paths = HarnessPaths.Create(runKey);
        var iterationRoot = paths.IterationRoot(1);
        var cleanupErrors = new List<string>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        ApiHarnessClient? apiClient = null;
        var exchanges = new List<ApiExchangeEvidence>();
        object? regressionEvidence = null;
        object? barrierEvidence = null;
        object? collectingEvidence = null;
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
                "backend-p6-join-all",
                5);
            var api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            var (adminToken, adminPassword) = await BootstrapAndLoginAsync(
                api,
                backend,
                database,
                CancellationToken.None);
            var p5Fixture = await SeedFixtureAsync(database, CancellationToken.None);

            var t03Seed = await SeedP601FixtureAsync(
                database,
                p5Fixture,
                CancellationToken.None);
            var p5ThroughT03 = await AssertP602RegressionAsync(
                api,
                adminToken,
                database,
                p5Fixture,
                t03Seed,
                CancellationToken.None);
            await RenameP602RegressionFixtureAsync(
                database,
                t03Seed,
                CancellationToken.None);

            var t04Seed = await SeedP601FixtureAsync(
                database,
                p5Fixture,
                CancellationToken.None);
            var t04Fixture = await ConvertP601FixtureToP602Async(
                database,
                t04Seed,
                CancellationToken.None);
            var t04Launch = await LaunchAsync(
                api,
                adminToken,
                p5Fixture,
                t04Fixture.WorkId,
                t04Fixture.VersionId,
                "p6-03-regression-t04-launch",
                [t04Fixture.TargetUnitId],
                "2026-07-P603-T04",
                CancellationToken.None);
            RequireStatus(t04Launch.Confirm, "SUCCEEDED", "P6-03 FLOW-T04 regression");
            var t04InstanceId = ApiHarnessClient.RequiredString(
                t04Launch.Confirm.Json,
                "flowInstanceId");
            Require(
                (await LoadP601StepsAsync(
                    database,
                    t04InstanceId,
                    CancellationToken.None)).Count == 1,
                "FLOW-T04 regression launch drifted after P6-03.");
            await RenameP603FixtureAsync(
                database,
                t04Seed,
                "P6_03_REGRESSION_FLOW_T04",
                "P6-03-REG-T04",
                CancellationToken.None);
            regressionEvidence = new
            {
                t01ThroughT03 = p5ThroughT03,
                t04 = new { instanceId = t04InstanceId, entryStepCount = 1 }
            };

            var joinSeed = await SeedP601FixtureAsync(
                database,
                p5Fixture,
                CancellationToken.None);
            var fixture = await ConvertP601FixtureToP603Async(
                database,
                joinSeed,
                CancellationToken.None);
            barrierEvidence = await AssertP603FutureBarrierAsync(
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
                P603LaunchCommand,
                [fixture.TargetUnitId],
                P603PeriodKey,
                CancellationToken.None);
            RequireStatus(launch.Confirm, "SUCCEEDED", "P6-03 FLOW-T05 launch");
            var instanceId = ApiHarnessClient.RequiredString(
                launch.Confirm.Json,
                "flowInstanceId");
            await ReconcileP603Async(
                api,
                adminToken,
                instanceId,
                CancellationToken.None);
            var stepA = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                (await LoadP601StepsAsync(
                    database,
                    instanceId,
                    CancellationToken.None)).Single(),
                "field_note",
                "P5_NOTE",
                "p603-a",
                CancellationToken.None);
            var capability = await ReadP601ForwardCapabilityAsync(
                api,
                adminToken,
                fixture.WorkId,
                instanceId,
                stepA.Id,
                expectedCanForward: true,
                CancellationToken.None);
            var assignmentA = stepA.AssignmentId
                              ?? throw new InvalidOperationException(
                                  "FLOW-T05 entry step lacks assignment.");
            var forwardResults = await Task.WhenAll(
                ForwardP601Async(
                    api,
                    adminToken,
                    fixture.WorkId,
                    assignmentA,
                    P603ForwardCommand,
                    capability.InstanceRevision,
                    capability.StepRevision,
                    CancellationToken.None),
                ForwardP601Async(
                    api,
                    adminToken,
                    fixture.WorkId,
                    assignmentA,
                    P603ForwardCommand,
                    capability.InstanceRevision,
                    capability.StepRevision,
                    CancellationToken.None));
            foreach (var response in forwardResults)
                AssertP601ForwardSuccess(response, replayed: null, "T05 A -> B/C");
            Require(
                forwardResults.Count(response =>
                    ApiHarnessClient.RequiredBool(response.Json, "replayed")) == 1,
                "T05 concurrent fan-out must yield one winner and one replay.");
            AssertP602SameForkResult(
                forwardResults[0],
                forwardResults[1],
                "T05 concurrent fan-out replay");

            var branchSteps = await WaitForP603BranchesAsync(
                database,
                instanceId,
                CancellationToken.None);
            for (var index = 0; index < branchSteps.Count; index++)
            {
                branchSteps[index] = await ApproveP601StepAsync(
                    api,
                    backend,
                    adminToken,
                    database,
                    branchSteps[index],
                    null,
                    null,
                    $"p603-{branchSteps[index].FlowStepCode.ToLowerInvariant()}",
                    CancellationToken.None);
            }

            var firstBranch = branchSteps[0];
            var firstAssignmentId = firstBranch.AssignmentId
                                    ?? throw new InvalidOperationException(
                                        "First T05 contribution lacks assignment.");
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
                "backend-p6-join-all-fault",
                5,
                $"assignment-completed:{firstAssignmentId}",
                [
                    DynamicFlowRuntimeStateProjectionFaultPoints
                        .AfterJoinContributionWrite
                ]);
            api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            adminToken = await api.LoginAsync(
                "admin",
                adminPassword,
                CancellationToken.None);
            var firstCompletion = await CompleteP603AssignmentAsync(
                api,
                adminToken,
                firstAssignmentId,
                firstBranch.FlowStepCode,
                CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                firstCompletion,
                HttpStatusCode.InternalServerError,
                "P6-03 injected contribution failure");
            await TriggerP603LifecycleWorkerBestEffortAsync(
                api,
                adminToken,
                CancellationToken.None);

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
                "backend-p6-join-all-restarted",
                5);
            api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            adminToken = await api.LoginAsync(
                "admin",
                adminPassword,
                CancellationToken.None);
            var recoveredCompletion = await CompleteP603AssignmentAsync(
                api,
                adminToken,
                firstAssignmentId,
                firstBranch.FlowStepCode,
                CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                recoveredCompletion,
                HttpStatusCode.OK,
                "P6-03 recovered contribution completion");
            await ReconcileP603Async(
                api,
                adminToken,
                instanceId,
                CancellationToken.None);
            await WaitForP601StepStateAsync(
                api,
                adminToken,
                database,
                firstBranch.Id,
                DynamicFlowStepStates.Completed,
                CancellationToken.None);
            var collectingGateway = await LoadP603GatewayAsync(
                database,
                instanceId,
                CancellationToken.None);
            var collectingInstance = await LoadP601InstanceAsync(
                database,
                instanceId,
                CancellationToken.None);
            Require(
                collectingGateway.State == DynamicFlowGatewayStates.Collecting &&
                collectingGateway.ArrivedContributionIds.Count == 1 &&
                collectingGateway.ExpectedContributionIds.Count == 2 &&
                collectingInstance.State == DynamicFlowInstanceStates.Active,
                "JOIN ALL released before all expected contributions arrived.");
            collectingEvidence = new
            {
                serverRestarted = true,
                injectedFaultPoint =
                    DynamicFlowRuntimeStateProjectionFaultPoints
                        .AfterJoinContributionWrite,
                state = collectingGateway.State,
                arrivedCount = collectingGateway.ArrivedContributionIds.Count,
                missingCount = 1,
                instanceState = collectingInstance.State
            };

            var secondBranch = branchSteps[1];
            var secondAssignmentId = secondBranch.AssignmentId
                                     ?? throw new InvalidOperationException(
                                         "Second T05 contribution lacks assignment.");
            var secondCompletion = await CompleteP603AssignmentAsync(
                api,
                adminToken,
                secondAssignmentId,
                secondBranch.FlowStepCode,
                CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                secondCompletion,
                HttpStatusCode.OK,
                "P6-03 final contribution completion");
            await WaitForP601StepStateAsync(
                api,
                adminToken,
                database,
                secondBranch.Id,
                DynamicFlowStepStates.Completed,
                CancellationToken.None);
            await WaitForP602InstanceStateAsync(
                api,
                adminToken,
                database,
                instanceId,
                DynamicFlowInstanceStates.Completed,
                CancellationToken.None);
            await Task.WhenAll(
                ReconcileP603Async(
                    api,
                    adminToken,
                    instanceId,
                    CancellationToken.None),
                ReconcileP603Async(
                    api,
                    adminToken,
                    instanceId,
                    CancellationToken.None));
            directMongoEvidence = await AssertP603DirectMongoAsync(
                api,
                adminToken,
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
                try { await backend.StopAsync(); }
                catch (Exception error) { cleanupErrors.Add($"backend-stop: {error.Message}"); }
                await backend.DisposeAsync();
            }
            if (mongo is not null)
            {
                try { await mongo.DropDatabaseGuardedAsync(CancellationToken.None); }
                catch (Exception error) { cleanupErrors.Add($"database-drop: {error.Message}"); }
                try { await mongo.StopProcessAsync(); }
                catch (Exception error) { cleanupErrors.Add($"mongo-stop: {error.Message}"); }
                try { mongo.RemoveDataDirectoryGuarded(); }
                catch (Exception error) { cleanupErrors.Add($"mongo-data-remove: {error.Message}"); }
                await mongo.DisposeAsync();
            }
        }

        passed = passed && cleanupErrors.Count == 0;
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "p6-03-join-all-api-exchanges.json"),
            exchanges);
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "p6-03-join-all-direct-mongo.json"),
            new
            {
                runKey,
                regressionEvidence,
                barrierEvidence,
                collectingEvidence,
                directMongoEvidence
            });
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "p6-03-join-all-result.json"),
            new
            {
                runKey,
                verdict = passed ? "PASS" : "FAIL",
                requirements = new[] { "P6-TOPO-006" },
                candidate = new
                {
                    version = DynamicFlowP6CatalogCandidate.Version,
                    semanticHash = DynamicFlowP6CatalogCandidate.SemanticHash,
                    currentCatalogModified = false,
                    testingActivationOnly =
                        !DynamicFlowP6CatalogCandidate.ActivationEnabled
                },
                failure,
                cleanupErrors,
                artifactRoot = iterationRoot,
                completedAtUtc = DateTime.UtcNow
            });
        Console.WriteLine(
            passed
                ? $"[PASS] P6-03 JOIN ALL probe passed; artifact={Path.Combine(iterationRoot, "p6-03-join-all-result.json")}"
                : $"[FAIL] P6-03 JOIN ALL probe failed: {failure ?? string.Join("; ", cleanupErrors)}; artifact={Path.Combine(iterationRoot, "p6-03-join-all-result.json")}");
        return passed ? 0 : 1;
    }

    private static async Task<BackendServerLease> StartP603BackendAsync(
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        MongoReplicaSetLease mongo,
        string directoryName,
        int candidateActivationThrough,
        string? stateFaultCommandId = null,
        IReadOnlyList<string>? stateFaultPoints = null)
    {
        var serverRoot = Path.Combine(iterationRoot, directoryName);
        Directory.CreateDirectory(serverRoot);
        return await BackendServerLease.StartAsync(
            paths,
            serverRoot,
            runKey,
            mongo,
            CancellationToken.None,
            new BackendServerOptions
            {
                EnableDynamicFlowRuntimeCandidate = true,
                DynamicFlowRuntimeCandidateActivationThrough =
                    candidateActivationThrough,
                DynamicFlowRuntimeStateProjectionFaultCommandId =
                    stateFaultCommandId,
                DynamicFlowRuntimeStateProjectionFaultPoints =
                    stateFaultPoints ?? Array.Empty<string>()
            });
    }

    private static async Task<P602Fixture> ConvertP601FixtureToP603Async(
        IMongoDatabase database,
        P601Fixture seed,
        CancellationToken ct)
    {
        var rootForm = seed.ExpectedSteps.Single(step => step.NodeId == "step_a").Form;
        var branchBForm = seed.ExpectedSteps.Single(step => step.NodeId == "step_b").Form;
        var branchCForm = seed.ExpectedSteps.Single(step => step.NodeId == "step_c").Form;
        var payload = JsonNode.Parse(seed.CanonicalPayload)?.AsObject()
                      ?? throw new InvalidOperationException("P6-03 seed payload is invalid.");
        payload["archetypeId"] = DynamicFlowJoinAllTopologyContract.ArchetypeId;
        payload.Remove("resultOwnerStepId");
        payload.Remove("resultOwnerFormNodeId");
        payload.Remove("statisticsOwnerStepId");
        payload.Remove("statisticsOwnerFormNodeId");
        payload["nodes"] = new JsonArray(
            P602Step("step_a", "A", rootForm.FormNodeId),
            P602Step("step_b", "B", branchBForm.FormNodeId),
            P602Step("step_c", "C", branchCForm.FormNodeId),
            new JsonObject
            {
                ["nodeId"] = "join_j",
                ["nodeCode"] = "J",
                ["nodeKind"] = DynamicFlowNodeKinds.Gateway,
                ["gateway"] = new JsonObject
                {
                    ["kind"] = DynamicFlowGatewayKinds.JoinAll,
                    ["expectedIncomingNodeIds"] =
                        new JsonArray("step_b", "step_c")
                }
            },
            new JsonObject
            {
                ["nodeId"] = "final",
                ["nodeCode"] = "FINAL",
                ["nodeKind"] = DynamicFlowNodeKinds.Final
            });
        payload["edges"] = new JsonArray(
            P602Edge("tr_a_b", "step_a", "step_b"),
            P602Edge("tr_a_c", "step_a", "step_c"),
            P602Edge("tr_b_j", "step_b", "join_j"),
            P602Edge("tr_c_j", "step_c", "join_j"),
            P602Edge("tr_j_final", "join_j", "final"));
        var canonical = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            payload.ToJsonString(),
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true,
                AllowHistoricalCatalogPins: true));
        var topology = DynamicFlowJoinAllTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        Require(
            topology.Branches.Select(branch => branch.Node.NodeCode)
                .SequenceEqual(new[] { "B", "C" }, StringComparer.Ordinal),
            "P6-03 fixture did not canonicalize to exact B/C contributors.");
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

    private static async Task RenameP603FixtureAsync(
        IMongoDatabase database,
        P601Fixture fixture,
        string familyCode,
        string workCode,
        CancellationToken ct)
    {
        await database.GetCollection<DynamicFlowTemplate>(
                "dynamic_flow_templates")
            .UpdateOneAsync(
                item => item.Id == fixture.FamilyId,
                Builders<DynamicFlowTemplate>.Update
                    .Set(item => item.Code, familyCode),
                cancellationToken: ct);
        await database.GetCollection<Work>("works")
            .UpdateOneAsync(
                item => item.Id == fixture.WorkId,
                Builders<Work>.Update
                    .Set(item => item.AutoCode, workCode)
                    .Set(item => item.Code, workCode),
                cancellationToken: ct);
        await database.GetCollection<Work>("works")
            .UpdateOneAsync(
                item => item.Id == fixture.OwnerOracleWorkId,
                Builders<Work>.Update
                    .Set(item => item.AutoCode, $"{workCode}-OWNER")
                    .Set(item => item.Code, $"{workCode}-OWNER"),
                cancellationToken: ct);
    }

    private static async Task<object> AssertP603FutureBarrierAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        ProbeFixture fixture,
        CancellationToken ct)
    {
        var rows = new List<object>();
        foreach (var barrier in fixture.BarrierFixtures
                     .Where(item =>
                         string.CompareOrdinal(item.ArchetypeId, "FLOW-T06") >= 0)
                     .OrderBy(item => item.ArchetypeId, StringComparer.Ordinal))
        {
            var request = new DynamicFlowPreflightRequest
            {
                FlowTemplateVersionId = barrier.VersionId,
                CommandId = $"p6-03-blocked-{barrier.ArchetypeId.ToLowerInvariant()}",
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
                $"{barrier.ArchetypeId} must remain blocked after P6-03.");
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
                $"{barrier.ArchetypeId} crossed the P6-03 write boundary.");
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
        Require(rows.Count == 7, "P6-03 must verify the complete T06..T12 barrier.");
        return new { count = rows.Count, rows };
    }

    private static async Task<List<DynamicFlowStepInstance>> WaitForP603BranchesAsync(
        IMongoDatabase database,
        string instanceId,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 80; attempt++)
        {
            var steps = await LoadP601StepsAsync(database, instanceId, ct);
            var branches = steps
                .Where(step => step.FlowStepId is "step_b" or "step_c")
                .OrderBy(step => step.FlowStepCode, StringComparer.Ordinal)
                .ToList();
            if (branches.Count == 2 &&
                branches.All(step => !string.IsNullOrWhiteSpace(step.AssignmentId)))
            {
                return branches;
            }
            await Task.Delay(100, ct);
        }
        throw new InvalidOperationException(
            "P6-03 did not materialize both JOIN ALL contribution branches.");
    }

    private static Task<ApiHarnessResponse> CompleteP603AssignmentAsync(
        ApiHarnessClient api,
        string adminToken,
        string assignmentId,
        string nodeCode,
        CancellationToken ct)
        => api.PostAsync(
            $"api/work-assignments/{assignmentId}/complete",
            new JsonObject
            {
                ["completedDate"] = "2026-07-27T00:00:00Z",
                ["note"] = $"P6-03 JOIN ALL contribution {nodeCode}"
            },
            adminToken,
            ct: ct);

    private static async Task TriggerP603LifecycleWorkerBestEffortAsync(
        ApiHarnessClient api,
        string adminToken,
        CancellationToken ct)
    {
        try
        {
            await api.PostAsync(
                "api/admin/operations/job-runs/lifecycle-projection-outbox/process?maxReports=20",
                body: null,
                adminToken,
                ct: ct);
        }
        catch
        {
            // The configured fault is expected to surface at this boundary.
        }
    }

    private static async Task<ApiHarnessResponse> ReconcileP603Async(
        ApiHarnessClient api,
        string adminToken,
        string instanceId,
        CancellationToken ct)
    {
        var response = await api.PostAsync(
            $"api/admin/operations/dynamic-flow-runtime/instances/{instanceId}/reconcile?apply=true",
            new { },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            "P6-03 JOIN ALL reconcile");
        return response;
    }

    private static async Task<DynamicFlowGatewayInstance> LoadP603GatewayAsync(
        IMongoDatabase database,
        string instanceId,
        CancellationToken ct)
        => await database.GetCollection<DynamicFlowGatewayInstance>(
                "dynamic_flow_gateway_instances")
            .Find(item => item.FlowInstanceId == instanceId)
            .SingleAsync(ct);

    private static async Task<object> AssertP603DirectMongoAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        P602Fixture fixture,
        string instanceId,
        CancellationToken ct)
    {
        var instance = await LoadP601InstanceAsync(database, instanceId, ct);
        var topology = DynamicFlowJoinAllTopologyContract.Require(
            instance.TopologySnapshotJson,
            instance.TopologySnapshotHash);
        var gateway = await LoadP603GatewayAsync(database, instanceId, ct);
        var contributions = await database
            .GetCollection<DynamicFlowGatewayContribution>(
                "dynamic_flow_gateway_contributions")
            .Find(item => item.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        var steps = await LoadP601StepsAsync(database, instanceId, ct);
        var contributionSteps = steps
            .Where(step => step.FlowStepId is "step_b" or "step_c")
            .ToList();
        Require(
            instance.WorkId == fixture.WorkId &&
            instance.FlowTemplateId == fixture.FamilyId &&
            instance.FlowTemplateVersionId == fixture.VersionId &&
            instance.ArchetypeId == DynamicFlowJoinAllTopologyContract.ArchetypeId &&
            instance.TopologySnapshotHash == fixture.PayloadHash &&
            instance.State == DynamicFlowInstanceStates.Completed,
            "P6-03 immutable instance pins or terminal state drifted.");
        Require(
            topology.ForkNode.NodeId == "join_j" &&
            topology.Branches.Select(branch => branch.Node.NodeId)
                .SequenceEqual(new[] { "step_b", "step_c" }, StringComparer.Ordinal),
            "P6-03 durable topology is not exact JOIN ALL.");
        Require(
            gateway.GatewayKind == DynamicFlowGatewayKinds.JoinAll &&
            gateway.GatewayVersion == DynamicFlowJoinAllTopologyContract.GatewayVersion &&
            gateway.State == DynamicFlowGatewayStates.Satisfied &&
            gateway.Revision == 3 &&
            gateway.ReleasedAtUtc.HasValue &&
            gateway.ExpectedContributionIds.Count == 2 &&
            gateway.ArrivedContributionIds.Count == 2 &&
            gateway.ExpectedContributionIds.ToHashSet(StringComparer.Ordinal)
                .SetEquals(gateway.ArrivedContributionIds),
            "P6-03 gateway did not satisfy exactly once after both contributions.");
        Require(
            contributions.Count == 2 &&
            contributions.Select(item => item.Id)
                .Distinct(StringComparer.Ordinal).Count() == 2 &&
            contributions.Select(item => item.ContributionId)
                .Distinct(StringComparer.Ordinal).Count() == 2 &&
            contributions.All(item =>
                item.GatewayInstanceId == gateway.GatewayInstanceId &&
                item.GatewayVersion == gateway.GatewayVersion &&
                item.EffectiveStatus == DynamicFlowEffectiveStatuses.Effective) &&
            contributions.Select(item => item.ContributionId)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(gateway.ExpectedContributionIds) &&
            contributionSteps.Select(step => step.ContributionId!)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(contributions.Select(item => item.ContributionId)),
            "P6-03 contribution ledger has duplicates or orphans.");

        var events = await database.GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events")
            .Find(item => item.FlowInstanceId == instanceId)
            .SortBy(item => item.Sequence)
            .ToListAsync(ct);
        Require(
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeStateProjectionEventTypes
                    .JoinContributionAccepted) == 2 &&
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeStateProjectionEventTypes.JoinAllSatisfied) == 1 &&
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeStateProjectionEventTypes.InstanceCompleted) == 1 &&
            events.Select(item => item.Id)
                .Distinct(StringComparer.Ordinal).Count() == events.Count &&
            events.Select(item => item.Sequence)
                .SequenceEqual(
                    Enumerable.Range(1, events.Count).Select(value => (long)value)),
            "P6-03 event chain is not unique and contiguous.");

        var overview = await api.GetAsync(
            $"api/works/{fixture.WorkId}/dynamic-flows/instances/{instanceId}",
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            overview,
            HttpStatusCode.OK,
            "P6-03 gateway read model");
        var overviewBody = ApiHarnessClient.RequiredObject(
            overview.Json,
            "P6-03 overview");
        var gatewayRows = ApiHarnessClient.RequiredArray(
            overviewBody["gateways"],
            "P6-03 gateway rows");
        Require(
            gatewayRows.Count == 1 &&
            ApiHarnessClient.RequiredString(gatewayRows[0], "state") ==
            DynamicFlowGatewayStates.Satisfied &&
            ApiHarnessClient.RequiredArray(
                ApiHarnessClient.RequiredObject(
                    gatewayRows[0],
                    "P6-03 gateway row")["missingContributionIds"],
                "P6-03 missing contributions").Count == 0,
            "P6-03 read model did not expose SATISFIED with zero missing.");

        return new
        {
            instanceId,
            gatewayInstanceId = gateway.GatewayInstanceId,
            gatewayVersion = gateway.GatewayVersion,
            gatewayState = gateway.State,
            expectedContributionCount = gateway.ExpectedContributionIds.Count,
            arrivedContributionCount = gateway.ArrivedContributionIds.Count,
            missingContributionCount = 0,
            contributionLedgerCount = contributions.Count,
            contributionAcceptedEventCount = 2,
            joinAllSatisfiedEventCount = 1,
            instanceCompletedEventCount = 1,
            duplicateCount = 0,
            orphanCount = 0,
            readProjectionVerified = true
        };
    }
}


