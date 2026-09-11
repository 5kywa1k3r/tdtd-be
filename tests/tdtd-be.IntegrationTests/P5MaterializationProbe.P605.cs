using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Bounded P6-05 executable evidence for FLOW-T07. The probe exercises the
/// real Kestrel/Mongo boundary and proves server-owned typed evaluation,
/// immutable decision metadata, changed-fact replay, ACL/type-error zero
/// writes, the T08..T12 barrier, direct Mongo state and cleanup.
/// </summary>
internal static partial class P5MaterializationProbe
{
    private const string P605PeriodKey = P601PeriodKey;
    private const string P605ForwardCommand = "p6-05-t07-forward";

    public static async Task<int> RunP605Async()
    {
        var runKey =
            $"p605_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}";
        var paths = HarnessPaths.Create(runKey);
        var iterationRoot = paths.IterationRoot(1);
        var cleanupErrors = new List<string>();
        var exchanges = new List<ApiExchangeEvidence>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        ApiHarnessClient? apiClient = null;
        object? barrierEvidence = null;
        object? decisionEvidence = null;
        object? failureEvidence = null;
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
                "backend-p6-typed-conditional",
                7);
            var api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            var (adminToken, _) = await BootstrapAndLoginAsync(
                api,
                backend,
                database,
                CancellationToken.None);
            var p5Fixture = await SeedFixtureAsync(
                database,
                CancellationToken.None);
            var seed = await SeedP601FixtureAsync(
                database,
                p5Fixture,
                CancellationToken.None);
            var fixture = await ConvertP601FixtureToP605Async(
                database,
                seed,
                new JsonObject
                {
                    ["operator"] = "EQ",
                    ["field"] = "instance.periodKey",
                    ["value"] = P605PeriodKey
                },
                CancellationToken.None);
            await RenameP603FixtureAsync(
                database,
                seed,
                "P6_05_TYPED_FLOW_MAIN",
                "P6-05-TYPED-MAIN",
                CancellationToken.None);
            var typeSeed = await SeedP601FixtureAsync(
                database,
                p5Fixture,
                CancellationToken.None);
            var typeFixture = await ConvertP601FixtureToP605Async(
                database,
                typeSeed,
                new JsonObject
                {
                    ["operator"] = "GT",
                    ["field"] = "assignment.flowAttemptNo",
                    ["value"] = "not-a-decimal"
                },
                CancellationToken.None);
            barrierEvidence = await AssertP605FutureBarrierAsync(
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
                "p6-05-t07-launch",
                [fixture.TargetUnitId],
                P605PeriodKey,
                CancellationToken.None);
            RequireStatus(
                launch.Confirm,
                "SUCCEEDED",
                "P6-05 FLOW-T07 launch");
            var instanceId = ApiHarnessClient.RequiredString(
                launch.Confirm.Json,
                "flowInstanceId");
            await ReconcileP603Async(
                api,
                adminToken,
                instanceId,
                CancellationToken.None);
            var entry = (await LoadP601StepsAsync(
                    database,
                    instanceId,
                    CancellationToken.None))
                .Single();
            var assignmentId = entry.AssignmentId
                               ?? throw new InvalidOperationException(
                                   "P6-05 entry step lacks an assignment.");
            entry = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                entry,
                "field_note",
                "P5_NOTE",
                "p605-a",
                CancellationToken.None);
            var capability = await ReadP601ForwardCapabilityAsync(
                api,
                adminToken,
                fixture.WorkId,
                instanceId,
                entry.Id,
                expectedCanForward: true,
                CancellationToken.None);

            var outsiderToken = await PrepareP601ActorLoginAsync(
                api,
                backend,
                database,
                seed.OutsiderUserId,
                CancellationToken.None);
            var beforeOutsider = await CaptureP601LedgerHashAsync(
                database,
                instanceId,
                CancellationToken.None);
            var outsider = await ForwardP601Async(
                api,
                outsiderToken,
                fixture.WorkId,
                assignmentId,
                "p6-05-t07-outsider",
                capability.InstanceRevision,
                capability.StepRevision,
                CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                outsider,
                HttpStatusCode.Forbidden,
                "P6-05 conditional outsider forward");
            Require(
                beforeOutsider == await CaptureP601LedgerHashAsync(
                    database,
                    instanceId,
                    CancellationToken.None),
                "P6-05 ACL rejection changed the direct-Mongo ledger.");

            var forward = await ForwardP601Async(
                api,
                adminToken,
                fixture.WorkId,
                assignmentId,
                P605ForwardCommand,
                capability.InstanceRevision,
                capability.StepRevision,
                CancellationToken.None);
            AssertP601ForwardSuccess(
                forward,
                replayed: false,
                "P6-05 typed rule selection");
            AssertP605DecisionResponse(
                forward,
                "edge-g-b",
                "RULE_MATCHED",
                replayed: false);
            var persistedHash = ApiHarnessClient.RequiredString(
                forward.Json,
                "inputSnapshotHash");

            await database.GetCollection<DynamicFlowInstance>(
                    "dynamic_flow_instances")
                .UpdateOneAsync(
                    item => item.Id == instanceId,
                    Builders<DynamicFlowInstance>.Update.Set(
                        item => item.PeriodKey,
                        "2026-07-P605-CHANGED"),
                    cancellationToken: CancellationToken.None);
            var changedInstance = await LoadP601InstanceAsync(
                database,
                instanceId,
                CancellationToken.None);
            var changedAssignment = await database
                .GetCollection<WorkAssignment>("work_assignments")
                .Find(item => item.Id == assignmentId)
                .SingleAsync(CancellationToken.None);
            var changedSnapshot =
                DynamicFlowTypedConditionalTopologyContract.BuildFactSnapshot(
                    changedAssignment,
                    entry,
                    changedInstance,
                    null);
            Require(
                changedSnapshot.SnapshotHash != persistedHash,
                "P6-05 changed-fact fixture did not change the server snapshot.");
            var replay = await ForwardP601Async(
                api,
                adminToken,
                fixture.WorkId,
                assignmentId,
                P605ForwardCommand,
                capability.InstanceRevision,
                capability.StepRevision,
                CancellationToken.None);
            AssertP601ForwardSuccess(
                replay,
                replayed: true,
                "P6-05 changed-fact exact replay");
            AssertP605DecisionResponse(
                replay,
                "edge-g-b",
                "RULE_MATCHED",
                replayed: true);
            AssertP601SameForwardResult(
                forward,
                replay,
                "P6-05 changed-fact exact replay");
            Require(
                ApiHarnessClient.RequiredString(
                    replay.Json,
                    "inputSnapshotHash") == persistedHash,
                "P6-05 replay re-evaluated changed facts.");
            await database.GetCollection<DynamicFlowInstance>(
                    "dynamic_flow_instances")
                .UpdateOneAsync(
                    item => item.Id == instanceId,
                    Builders<DynamicFlowInstance>.Update.Set(
                        item => item.PeriodKey,
                        P605PeriodKey),
                    cancellationToken: CancellationToken.None);
            decisionEvidence = new
            {
                selectedEdgeId = "edge-g-b",
                decisionReasonCode = "RULE_MATCHED",
                evaluatorVersion =
                    DynamicFlowTypedConditionalTopologyContract
                        .EvaluatorVersion,
                inputSnapshotHash = persistedHash,
                changedFactSnapshotHash = changedSnapshot.SnapshotHash,
                changedFactReplayStable = true,
                aclZeroWrite = true
            };

            var typeLaunch = await LaunchAsync(
                api,
                adminToken,
                p5Fixture,
                typeFixture.WorkId,
                typeFixture.VersionId,
                "p6-05-t07-type-launch",
                [typeFixture.TargetUnitId],
                P605PeriodKey,
                CancellationToken.None);
            RequireStatus(
                typeLaunch.Confirm,
                "SUCCEEDED",
                "P6-05 type-failure launch");
            var typeInstanceId = ApiHarnessClient.RequiredString(
                typeLaunch.Confirm.Json,
                "flowInstanceId");
            await ReconcileP603Async(
                api,
                adminToken,
                typeInstanceId,
                CancellationToken.None);
            var typeEntry = (await LoadP601StepsAsync(
                    database,
                    typeInstanceId,
                    CancellationToken.None))
                .Single();
            typeEntry = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                typeEntry,
                "field_note",
                "P5_NOTE",
                "p605-type",
                CancellationToken.None);
            var typeCapability = await ReadP601ForwardCapabilityAsync(
                api,
                adminToken,
                typeFixture.WorkId,
                typeInstanceId,
                typeEntry.Id,
                expectedCanForward: true,
                CancellationToken.None);
            var beforeTypeHash = await CaptureP601LedgerHashAsync(
                database,
                typeInstanceId,
                CancellationToken.None);
            var beforeTypeCounts = await CaptureP601BusinessCountsAsync(
                database,
                CancellationToken.None);
            var typeFailure = await ForwardP601Async(
                api,
                adminToken,
                typeFixture.WorkId,
                typeEntry.AssignmentId!,
                "p6-05-t07-type-failure",
                typeCapability.InstanceRevision,
                typeCapability.StepRevision,
                CancellationToken.None);
            AssertP601Conflict(
                typeFailure,
                DynamicFlowTypedConditionalTopologyContract.TypeMismatch,
                "P6-05 typed condition mismatch",
                HttpStatusCode.BadRequest);
            var afterTypeCounts = await CaptureP601BusinessCountsAsync(
                database,
                CancellationToken.None);
            Require(
                beforeTypeHash == await CaptureP601LedgerHashAsync(
                    database,
                    typeInstanceId,
                    CancellationToken.None) &&
                beforeTypeCounts.All(pair =>
                    afterTypeCounts.TryGetValue(pair.Key, out var count) &&
                    count == pair.Value),
                "P6-05 type failure crossed the zero-write boundary.");
            failureEvidence = new
            {
                errorCode =
                    DynamicFlowTypedConditionalTopologyContract.TypeMismatch,
                zeroWrite = true
            };

            await WaitForP601AssignmentAsync(
                database,
                ApiHarnessClient.RequiredString(
                    forward.Json,
                    "nextAssignmentId"),
                CancellationToken.None);
            var selected = (await LoadP601StepsAsync(
                    database,
                    instanceId,
                    CancellationToken.None))
                .Single(step => step.FlowStepId == "step_b");
            selected = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                selected,
                null,
                null,
                "p605-b",
                CancellationToken.None);
            await CompleteP601AssignmentAsync(
                api,
                adminToken,
                database,
                selected,
                CancellationToken.None);
            await WaitForP602InstanceStateAsync(
                api,
                adminToken,
                database,
                instanceId,
                DynamicFlowInstanceStates.Completed,
                CancellationToken.None);

            directMongoEvidence = await AssertP605DirectMongoAsync(
                api,
                adminToken,
                database,
                fixture,
                instanceId,
                persistedHash,
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
                "p6-05-typed-conditional-api-exchanges.json"),
            exchanges);
        await EvidenceJson.WriteAsync(
            Path.Combine(
                iterationRoot,
                "p6-05-typed-conditional-direct-mongo.json"),
            new
            {
                runKey,
                barrierEvidence,
                decisionEvidence,
                failureEvidence,
                directMongoEvidence
            });
        await EvidenceJson.WriteAsync(
            Path.Combine(
                iterationRoot,
                "p6-05-typed-conditional-result.json"),
            new
            {
                runKey,
                verdict = passed ? "PASS" : "FAIL",
                requirements = new[] { "P6-TOPO-008" },
                failure,
                cleanupErrors,
                artifactRoot = iterationRoot,
                completedAtUtc = DateTime.UtcNow
            });
        Console.WriteLine(
            passed
                ? $"[PASS] P6-05 typed conditional probe passed; artifact={Path.Combine(iterationRoot, "p6-05-typed-conditional-result.json")}"
                : $"[FAIL] P6-05 typed conditional probe failed: {failure ?? string.Join("; ", cleanupErrors)}; artifact={Path.Combine(iterationRoot, "p6-05-typed-conditional-result.json")}");
        return passed ? 0 : 1;
    }

    private static async Task<P602Fixture> ConvertP601FixtureToP605Async(
        IMongoDatabase database,
        P601Fixture seed,
        JsonObject ruleCondition,
        CancellationToken ct)
    {
        var rootForm = seed.ExpectedSteps
            .Single(step => step.NodeId == "step_a").Form;
        var branchBForm = seed.ExpectedSteps
            .Single(step => step.NodeId == "step_b").Form;
        var branchCForm = seed.ExpectedSteps
            .Single(step => step.NodeId == "step_c").Form;
        var payload = JsonNode.Parse(seed.CanonicalPayload)?.AsObject()
                      ?? throw new InvalidOperationException(
                          "P6-05 seed payload is invalid.");
        payload["archetypeId"] =
            DynamicFlowTypedConditionalTopologyContract.ArchetypeId;
        payload.Remove("resultOwnerStepId");
        payload.Remove("resultOwnerFormNodeId");
        payload.Remove("statisticsOwnerStepId");
        payload.Remove("statisticsOwnerFormNodeId");
        payload["nodes"] = new JsonArray(
            P602Step("step_a", "A", rootForm.FormNodeId),
            new JsonObject
            {
                ["nodeId"] = "gateway_g",
                ["nodeCode"] = "G",
                ["nodeKind"] = DynamicFlowNodeKinds.Gateway,
                ["gateway"] = new JsonObject
                {
                    ["kind"] = DynamicFlowGatewayKinds.Condition
                }
            },
            P602Step("step_b", "B", branchBForm.FormNodeId),
            P602Step("step_c", "C", branchCForm.FormNodeId));
        payload["edges"] = new JsonArray(
            P602Edge("edge-a-g", "step_a", "gateway_g"),
            new JsonObject
            {
                ["transitionId"] = "edge-g-b",
                ["fromNodeId"] = "gateway_g",
                ["toNodeId"] = "step_b",
                ["condition"] = ruleCondition.DeepClone()
            },
            new JsonObject
            {
                ["transitionId"] = "edge-g-z-default",
                ["fromNodeId"] = "gateway_g",
                ["toNodeId"] = "step_c",
                ["condition"] = new JsonObject
                {
                    ["operator"] = "TRUE"
                }
            });
        var canonical =
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                payload.ToJsonString(),
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true));
        var topology =
            DynamicFlowTypedConditionalTopologyContract.Require(
                canonical.CanonicalJson,
                canonical.PayloadHash);
        Require(
            topology.Branches.Select(branch => branch.Edge.TransitionId)
                .SequenceEqual(
                    new[] { "edge-g-b", "edge-g-z-default" },
                    StringComparer.Ordinal),
            "P6-05 fixture branch order drifted.");
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

    private static async Task<object> AssertP605FutureBarrierAsync(
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
                             "FLOW-T08") >= 0)
                     .OrderBy(
                         item => item.ArchetypeId,
                         StringComparer.Ordinal))
        {
            var request = new DynamicFlowPreflightRequest
            {
                FlowTemplateVersionId = barrier.VersionId,
                CommandId =
                    $"p6-05-blocked-{barrier.ArchetypeId.ToLowerInvariant()}",
                TargetUnitIds = [fixture.TargetUnitIds[0]],
                PeriodKey = $"2026-07-{barrier.ArchetypeId}",
                ScheduleIdentityJson = "{}"
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
                ApiHarnessClient.RequiredString(
                    preflight.Json,
                    "eligibility") ==
                DynamicFlowRuntimeEligibilityPolicy.BlockedPhase,
                $"{barrier.ArchetypeId} must remain blocked.");
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
            var after = await CaptureP601BusinessCountsAsync(database, ct);
            Require(
                ApiHarnessClient.RequiredString(
                    confirm.Json,
                    "status") ==
                "BLOCKED_UNTIL_TARGET_PHASE" &&
                before.All(pair =>
                    after.TryGetValue(pair.Key, out var count) &&
                    count == pair.Value),
                $"{barrier.ArchetypeId} crossed the zero-write barrier.");
            rows.Add(new { barrier.ArchetypeId, zeroWrite = true });
        }
        Require(
            rows.Count == 5,
            "P6-05 must verify the complete T08..T12 barrier.");
        return new { count = rows.Count, rows };
    }

    private static void AssertP605DecisionResponse(
        ApiHarnessResponse response,
        string selectedEdgeId,
        string reasonCode,
        bool replayed)
    {
        Require(
            ApiHarnessClient.RequiredBool(response.Json, "replayed") ==
            replayed &&
            ApiHarnessClient.RequiredString(
                response.Json,
                "selectedEdgeId") == selectedEdgeId &&
            ApiHarnessClient.RequiredString(
                response.Json,
                "decisionReasonCode") == reasonCode &&
            ApiHarnessClient.RequiredString(
                response.Json,
                "evaluatorVersion") ==
            DynamicFlowTypedConditionalTopologyContract.EvaluatorVersion &&
            ApiHarnessClient.RequiredString(
                response.Json,
                "inputSnapshotHash").Length == 64 &&
            ApiHarnessClient.RequiredArray(
                response.Json?["activatedBranches"],
                "P6-05 activated branches").Count == 1,
            "P6-05 response omitted or changed the persisted decision.");
    }

    private static async Task<object> AssertP605DirectMongoAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        P602Fixture fixture,
        string instanceId,
        string expectedSnapshotHash,
        CancellationToken ct)
    {
        var instance = await LoadP601InstanceAsync(
            database,
            instanceId,
            ct);
        var topology =
            DynamicFlowTypedConditionalTopologyContract.Require(
                instance.TopologySnapshotJson,
                instance.TopologySnapshotHash);
        var steps = await LoadP601StepsAsync(database, instanceId, ct);
        var assignments = await database.GetCollection<WorkAssignment>(
                "work_assignments")
            .Find(item =>
                item.FlowInstanceId == instanceId &&
                !item.IsDeleted)
            .ToListAsync(ct);
        var gateways = await database
            .GetCollection<DynamicFlowGatewayInstance>(
                "dynamic_flow_gateway_instances")
            .Find(item => item.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        var receipts = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                "dynamic_flow_runtime_command_receipts")
            .Find(item => item.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        var outbox = await database.GetCollection<DynamicFlowRuntimeOutboxItem>(
                "dynamic_flow_runtime_outbox")
            .Find(item => item.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        var events = await database.GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events")
            .Find(item => item.FlowInstanceId == instanceId)
            .SortBy(item => item.Sequence)
            .ToListAsync(ct);
        var gateway = gateways.Single();
        var forwardReceipt = receipts.Single(item =>
            item.CommandType == "FORWARD");
        var decisionEvent = events.Single(item =>
            item.CommandId == P605ForwardCommand &&
            item.EventType ==
            DynamicFlowRuntimeEventTypes.ParallelForkAccepted);
        var decisionPayloadJson =
            decisionEvent.Payload.ToJson().ToLowerInvariant();
        var resultJson =
            forwardReceipt.ResultSnapshot?.ToJson().ToLowerInvariant() ??
            string.Empty;

        Require(
            instance.ArchetypeId ==
                DynamicFlowTypedConditionalTopologyContract.ArchetypeId &&
            instance.TopologySnapshotHash == fixture.PayloadHash &&
            instance.TopologySnapshotJson == fixture.CanonicalPayload &&
            instance.State == DynamicFlowInstanceStates.Completed &&
            topology.EntryNode.NodeId == "step_a" &&
            topology.ForkNode.NodeId == "gateway_g",
            "P6-05 instance pins or exact topology drifted.");
        Require(
            steps.Count == 2 &&
            assignments.Count == 2 &&
            steps.Count(item => item.FlowStepId == "step_b") == 1 &&
            steps.All(item => item.FlowStepId != "step_c") &&
            steps.All(item => item.State == DynamicFlowStepStates.Completed),
            "P6-05 materialized anything other than A and selected branch B.");
        Require(
            gateway.GatewayKind == DynamicFlowGatewayKinds.Condition &&
            gateway.State == DynamicFlowGatewayStates.Satisfied &&
            gateway.InputSnapshotHash == expectedSnapshotHash &&
            gateway.EvaluatorVersion ==
                DynamicFlowTypedConditionalTopologyContract
                    .EvaluatorVersion &&
            gateway.SelectedEdgeId == "edge-g-b" &&
            gateway.DecisionReasonCode == "RULE_MATCHED" &&
            gateway.DownstreamNodeId == "step_b" &&
            gateway.Id ==
                DynamicFlowTypedConditionalTopologyContract
                    .BuildDecisionLedgerId(
                        gateway.GatewayInstanceId,
                        gateway.GatewayVersion),
            "P6-05 decision ledger drifted.");
        Require(
            forwardReceipt.CommandId == P605ForwardCommand &&
            forwardReceipt.Status ==
                DynamicFlowRuntimeCommandStatuses.Succeeded &&
            forwardReceipt.ResultSnapshot is not null &&
            forwardReceipt.ResultSnapshotHash ==
                DynamicFlowParallelForkTopologyContract.Hash(
                    forwardReceipt.ResultSnapshot.ToJson()) &&
            forwardReceipt.ResultSnapshot["inputSnapshotHash"].AsString ==
                expectedSnapshotHash &&
            forwardReceipt.ResultSnapshot["selectedEdgeId"].AsString ==
                "edge-g-b" &&
            forwardReceipt.ResultSnapshot["evaluatorVersion"].AsString ==
                DynamicFlowTypedConditionalTopologyContract
                    .EvaluatorVersion,
            "P6-05 durable receipt/result hash drifted.");
        Require(
            decisionEvent.ReasonCode == "RULE_MATCHED" &&
            decisionEvent.Payload["inputSnapshotHash"].AsString ==
                expectedSnapshotHash &&
            decisionEvent.Payload["selectedEdgeId"].AsString ==
                "edge-g-b" &&
            decisionEvent.Payload["evaluatorVersion"].AsString ==
                DynamicFlowTypedConditionalTopologyContract
                    .EvaluatorVersion &&
            !decisionPayloadJson.Contains("\"facts\"") &&
            !decisionPayloadJson.Contains("instance.periodkey") &&
            !decisionPayloadJson.Contains(P605PeriodKey.ToLowerInvariant()) &&
            !resultJson.Contains("\"facts\"") &&
            events.Select(item => item.Sequence).SequenceEqual(
                Enumerable.Range(1, events.Count)
                    .Select(value => (long)value)) &&
            events.Select(item => item.Id)
                .Distinct(StringComparer.Ordinal).Count() == events.Count &&
            events.All(item =>
                item.PayloadHash ==
                DynamicFlowParallelForkTopologyContract.Hash(
                    item.Payload.ToJson())),
            "P6-05 decision event leaked facts or event chain drifted.");
        Require(
            outbox.Count == 2 &&
            outbox.Count(item =>
                item.Operation ==
                DynamicFlowRuntimeMaterializationOperations
                    .MaterializeEntryAssignment) == 1 &&
            outbox.Count(item =>
                item.Operation ==
                DynamicFlowRuntimeMaterializationOperations
                    .MaterializeForkBranchAssignment) == 1 &&
            outbox.All(item =>
                item.Status ==
                DynamicFlowRuntimeOutboxStatuses.Completed),
            "P6-05 outbox cardinality/status drifted.");

        var overview = await api.GetAsync(
            $"api/works/{fixture.WorkId}/dynamic-flows/instances/{instanceId}",
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            overview,
            HttpStatusCode.OK,
            "P6-05 conditional overview");
        var row = ApiHarnessClient.RequiredArray(
            ApiHarnessClient.RequiredObject(
                overview.Json,
                "P6-05 overview")["gateways"],
            "P6-05 gateways").Single()
            ?? throw new InvalidOperationException(
                "P6-05 overview gateway is null.");
        Require(
            row["inputSnapshotHash"]!.GetValue<string>() ==
                expectedSnapshotHash &&
            row["evaluatorVersion"]!.GetValue<string>() ==
                DynamicFlowTypedConditionalTopologyContract
                    .EvaluatorVersion &&
            row["selectedEdgeId"]!.GetValue<string>() == "edge-g-b" &&
            row["decisionReasonCode"]!.GetValue<string>() ==
                "RULE_MATCHED",
            "P6-05 read projection omitted decision metadata.");
        return new
        {
            instanceId,
            gateway.GatewayInstanceId,
            gateway.InputSnapshotHash,
            gateway.EvaluatorVersion,
            gateway.SelectedEdgeId,
            gateway.DecisionReasonCode,
            selectedNodeId = gateway.DownstreamNodeId,
            stepCount = steps.Count,
            assignmentCount = assignments.Count,
            receiptCount = receipts.Count,
            outboxCount = outbox.Count,
            eventCount = events.Count,
            factValuesPersisted = false,
            readProjectionVerified = true,
            duplicateCount = 0,
            orphanCount = 0
        };
    }
}
