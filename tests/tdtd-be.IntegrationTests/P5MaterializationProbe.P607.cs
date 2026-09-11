using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Bounded P6-07 evidence for one blocking, exact-version-pinned FLOW-T09
/// child. It covers ACL zero-write, exact replay, backend restart, successful
/// and failed child propagation, direct Mongo lineage and the T10..T12 barrier.
/// Depth, cycle and pin drift are additionally frozen by the unit contract.
/// </summary>
internal static partial class P5MaterializationProbe
{
    public static async Task<int> RunP607Async()
    {
        var runKey =
            $"p607_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}";
        var paths = HarnessPaths.Create(runKey);
        var iterationRoot = paths.IterationRoot(1);
        var cleanupErrors = new List<string>();
        var exchanges = new List<ApiExchangeEvidence>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        ApiHarnessClient? apiClient = null;
        object? successEvidence = null;
        object? failureEvidence = null;
        object? barrierEvidence = null;
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
                "backend-p6-subflow",
                9);
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

            var successChildSeed = await SeedP601FixtureAsync(
                database,
                baseFixture,
                CancellationToken.None);
            var successChild = await ConvertP601FixtureToP607ChildAsync(
                database,
                successChildSeed,
                maxReviewCycles: 2,
                CancellationToken.None);
            await RenameP603FixtureAsync(
                database,
                successChildSeed,
                "P6_07_CHILD_SUCCESS",
                "P6-07-CHILD-SUCCESS",
                CancellationToken.None);
            var successParentSeed = await SeedP601FixtureAsync(
                database,
                baseFixture,
                CancellationToken.None);
            var successParent = await ConvertP601FixtureToP607Async(
                database,
                successParentSeed,
                successChildSeed.FamilyId,
                successChildSeed.VersionId,
                CancellationToken.None);
            await RenameP603FixtureAsync(
                database,
                successParentSeed,
                "P6_07_PARENT_SUCCESS",
                "P6-07-PARENT-SUCCESS",
                CancellationToken.None);

            var failureChildSeed = await SeedP601FixtureAsync(
                database,
                baseFixture,
                CancellationToken.None);
            var failureChild = await ConvertP601FixtureToP607ChildAsync(
                database,
                failureChildSeed,
                maxReviewCycles: 1,
                CancellationToken.None);
            await RenameP603FixtureAsync(
                database,
                failureChildSeed,
                "P6_07_CHILD_FAILURE",
                "P6-07-CHILD-FAILURE",
                CancellationToken.None);
            var failureParentSeed = await SeedP601FixtureAsync(
                database,
                baseFixture,
                CancellationToken.None);
            var failureParent = await ConvertP601FixtureToP607Async(
                database,
                failureParentSeed,
                failureChildSeed.FamilyId,
                failureChildSeed.VersionId,
                CancellationToken.None);
            await RenameP603FixtureAsync(
                database,
                failureParentSeed,
                "P6_07_PARENT_FAILURE",
                "P6-07-PARENT-FAILURE",
                CancellationToken.None);

            barrierEvidence = await AssertP607FutureBarrierAsync(
                api,
                adminToken,
                database,
                baseFixture,
                CancellationToken.None);

            var parentLaunch = await LaunchAsync(
                api,
                adminToken,
                baseFixture,
                successParent.WorkId,
                successParent.VersionId,
                "p6-07-parent-success-launch",
                [successParent.TargetUnitId],
                P601PeriodKey,
                CancellationToken.None);
            RequireStatus(
                parentLaunch.Confirm,
                "SUCCEEDED",
                "P6-07 success parent launch");
            var parentInstanceId = ApiHarnessClient.RequiredString(
                parentLaunch.Confirm.Json,
                "flowInstanceId");
            var parentStep = (await LoadP601StepsAsync(
                    database,
                    parentInstanceId,
                    CancellationToken.None))
                .Single();
            parentStep = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                parentStep,
                null,
                null,
                "p607-parent-success",
                CancellationToken.None);
            var parentInstance = await LoadP601InstanceAsync(
                database,
                parentInstanceId,
                CancellationToken.None);
            var subflowRequest = new DynamicFlowSubflowLaunchRequest
            {
                CommandId = "p6-07-child-success-launch",
                ExpectedParentInstanceRevision = parentInstance.Revision,
                ExpectedParentStepRevision = parentStep.Revision
            };

            var outsiderToken = await PrepareP601ActorLoginAsync(
                api,
                backend,
                database,
                successParentSeed.OutsiderUserId,
                CancellationToken.None);
            var beforeAcl = await CaptureP601LedgerHashAsync(
                database,
                parentInstanceId,
                CancellationToken.None);
            var outsider = await api.PostAsync(
                $"api/works/{successParent.WorkId}/dynamic-flows/instances/{parentInstanceId}/steps/{parentStep.Id}/subflow",
                subflowRequest,
                outsiderToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                outsider,
                HttpStatusCode.Forbidden,
                "P6-07 outsider subflow launch");
            Require(
                beforeAcl == await CaptureP601LedgerHashAsync(
                    database,
                    parentInstanceId,
                    CancellationToken.None),
                "P6-07 ACL rejection changed the parent ledger.");

            var launched = await api.PostAsync(
                $"api/works/{successParent.WorkId}/dynamic-flows/instances/{parentInstanceId}/steps/{parentStep.Id}/subflow",
                subflowRequest,
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                launched,
                HttpStatusCode.OK,
                "P6-07 child launch");
            var childInstanceId = ApiHarnessClient.RequiredString(
                launched.Json,
                "childInstanceId");
            Require(
                ApiHarnessClient.RequiredString(
                    launched.Json,
                    "childFlowVersionId") ==
                successChildSeed.VersionId &&
                ApiHarnessClient.RequiredString(
                    launched.Json,
                    "parentState") ==
                DynamicFlowStepStates.WaitingChild,
                "P6-07 did not use the exact pinned child version.");
            var replay = await api.PostAsync(
                $"api/works/{successParent.WorkId}/dynamic-flows/instances/{parentInstanceId}/steps/{parentStep.Id}/subflow",
                subflowRequest,
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                replay,
                HttpStatusCode.OK,
                "P6-07 exact launch replay");
            Require(
                ApiHarnessClient.RequiredBool(replay.Json, "replayed") &&
                ApiHarnessClient.RequiredString(
                    replay.Json,
                    "childInstanceId") == childInstanceId,
                "P6-07 exact replay did not reuse child identity.");

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
                "backend-p6-subflow-restart",
                9);
            api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            adminToken = await api.LoginAsync(
                "admin",
                adminPassword,
                CancellationToken.None);

            var childStep = (await LoadP601StepsAsync(
                    database,
                    childInstanceId,
                    CancellationToken.None))
                .Single();
            childStep = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                childStep,
                null,
                null,
                "p607-child-success",
                CancellationToken.None);
            await CompleteP601AssignmentAsync(
                api,
                adminToken,
                database,
                childStep,
                CancellationToken.None);
            await WaitForP602InstanceStateAsync(
                api,
                adminToken,
                database,
                childInstanceId,
                DynamicFlowInstanceStates.Completed,
                CancellationToken.None);
            await WaitForP602InstanceStateAsync(
                api,
                adminToken,
                database,
                parentInstanceId,
                DynamicFlowInstanceStates.Completed,
                CancellationToken.None);
            successEvidence = await AssertP607ScenarioAsync(
                database,
                successParent,
                successChild,
                parentInstanceId,
                childInstanceId,
                DynamicFlowInstanceStates.Completed,
                DynamicFlowStepStates.Completed,
                "COMPLETED",
                CancellationToken.None);

            var failedParentLaunch = await LaunchAsync(
                api,
                adminToken,
                baseFixture,
                failureParent.WorkId,
                failureParent.VersionId,
                "p6-07-parent-failure-launch",
                [failureParent.TargetUnitId],
                P601PeriodKey,
                CancellationToken.None);
            RequireStatus(
                failedParentLaunch.Confirm,
                "SUCCEEDED",
                "P6-07 failure parent launch");
            var failedParentId = ApiHarnessClient.RequiredString(
                failedParentLaunch.Confirm.Json,
                "flowInstanceId");
            var failedParentStep = (await LoadP601StepsAsync(
                    database,
                    failedParentId,
                    CancellationToken.None))
                .Single();
            failedParentStep = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                failedParentStep,
                null,
                null,
                "p607-parent-failure",
                CancellationToken.None);
            var failedParent = await LoadP601InstanceAsync(
                database,
                failedParentId,
                CancellationToken.None);
            var failedChildLaunch = await api.PostAsync(
                $"api/works/{failureParent.WorkId}/dynamic-flows/instances/{failedParentId}/steps/{failedParentStep.Id}/subflow",
                new DynamicFlowSubflowLaunchRequest
                {
                    CommandId = "p6-07-child-failure-launch",
                    ExpectedParentInstanceRevision = failedParent.Revision,
                    ExpectedParentStepRevision = failedParentStep.Revision
                },
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                failedChildLaunch,
                HttpStatusCode.OK,
                "P6-07 failure child launch");
            var failedChildId = ApiHarnessClient.RequiredString(
                failedChildLaunch.Json,
                "childInstanceId");
            var failedChildStep = (await LoadP601StepsAsync(
                    database,
                    failedChildId,
                    CancellationToken.None))
                .Single();
            var submitted = await SubmitP606StepAsync(
                api,
                backend,
                adminToken,
                database,
                failedChildStep,
                "p607-child-failure",
                CancellationToken.None);
            var returnRequest = await BuildP606ReturnRequestAsync(
                database,
                submitted.ReportId,
                "p6-07-child-failure-return",
                CancellationToken.None);
            var returned = await api.PostAsync(
                $"api/work-assignment-review/reports/{submitted.ReportId}/return",
                returnRequest,
                adminToken,
                ct: CancellationToken.None);
            Require(
                returned.StatusCode is
                    HttpStatusCode.OK or
                    HttpStatusCode.Accepted,
                "P6-07 failure return was rejected.");
            await WaitForP602InstanceStateAsync(
                api,
                adminToken,
                database,
                failedChildId,
                DynamicFlowInstanceStates.Failed,
                CancellationToken.None);
            await WaitForP602InstanceStateAsync(
                api,
                adminToken,
                database,
                failedParentId,
                DynamicFlowInstanceStates.Failed,
                CancellationToken.None);
            failureEvidence = await AssertP607ScenarioAsync(
                database,
                failureParent,
                failureChild,
                failedParentId,
                failedChildId,
                DynamicFlowInstanceStates.Failed,
                DynamicFlowStepStates.Failed,
                DynamicFlowSubflowTopologyContract.ChildFailed,
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
                    cleanupErrors.Add($"mongo-drop: {error.Message}");
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
                "p6-07-subflow-api-exchanges.json"),
            exchanges);
        await EvidenceJson.WriteAsync(
            Path.Combine(
                iterationRoot,
                "p6-07-subflow-direct-mongo.json"),
            new
            {
                runKey,
                successEvidence,
                failureEvidence,
                barrierEvidence
            });
        await EvidenceJson.WriteAsync(
            Path.Combine(
                iterationRoot,
                "p6-07-subflow-result.json"),
            new
            {
                runKey,
                verdict = passed ? "PASS" : "FAIL",
                requirements = new[] { "P6-TOPO-010" },
                failure,
                cleanupErrors,
                artifactRoot = iterationRoot,
                completedAtUtc = DateTime.UtcNow
            });
        Console.WriteLine(
            passed
                ? $"[PASS] P6-07 subflow probe passed; artifact={Path.Combine(iterationRoot, "p6-07-subflow-result.json")}"
                : $"[FAIL] P6-07 subflow probe failed: {failure ?? string.Join("; ", cleanupErrors)}; artifact={Path.Combine(iterationRoot, "p6-07-subflow-result.json")}");
        return passed ? 0 : 1;
    }

    private static async Task<P607Fixture> ConvertP601FixtureToP607Async(
        IMongoDatabase database,
        P601Fixture seed,
        string childFamilyId,
        string childVersionId,
        CancellationToken ct)
    {
        var rootForm = seed.ExpectedSteps
            .Single(step => step.NodeId == "step_a").Form;
        var payload = JsonNode.Parse(seed.CanonicalPayload)?.AsObject()
                      ?? throw new InvalidOperationException(
                          "P6-07 seed payload is invalid.");
        payload["archetypeId"] =
            DynamicFlowSubflowTopologyContract.ArchetypeId;
        payload.Remove("resultOwnerStepId");
        payload.Remove("resultOwnerFormNodeId");
        payload.Remove("statisticsOwnerStepId");
        payload.Remove("statisticsOwnerFormNodeId");
        payload["formNodes"] = new JsonArray(
            new JsonObject
            {
                ["formNodeId"] = rootForm.FormNodeId,
                ["role"] = "ROOT",
                ["dynamicFormTemplateId"] = rootForm.FormVersionId,
                ["dynamicFormFamilyId"] = rootForm.FormFamilyId,
                ["dynamicFormVersionNo"] = rootForm.FormVersionNo,
                ["dynamicFormSchemaHash"] = rootForm.FormSchemaHash,
                ["dynamicFormSnapshotHash"] = rootForm.FormSchemaHash
            });
        payload["nodes"] = new JsonArray(
            P602Step("step_a", "PARENT", rootForm.FormNodeId),
            new JsonObject
            {
                ["nodeId"] = "subflow_gateway",
                ["nodeCode"] = "CHILD",
                ["nodeKind"] = DynamicFlowNodeKinds.Gateway,
                ["gateway"] = new JsonObject
                {
                    ["kind"] = DynamicFlowGatewayKinds.Subflow,
                    ["subflowFamilyId"] = childFamilyId,
                    ["subflowVersionId"] = childVersionId
                }
            });
        payload["edges"] = new JsonArray(
            P602Edge(
                "edge-launch-child",
                "step_a",
                "subflow_gateway"));
        payload["finalResultPolicy"] = new JsonObject();
        var canonical =
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                payload.ToJsonString(),
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true));
        var topology = DynamicFlowSubflowTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        Require(
            topology.ChildFlowFamilyId == childFamilyId &&
            topology.ChildFlowVersionId == childVersionId,
            "P6-07 fixture child pin drifted.");
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
        return new P607Fixture(
            seed.WorkId,
            seed.VersionId,
            seed.TargetUnitId,
            childFamilyId,
            childVersionId,
            canonical.CanonicalJson,
            canonical.PayloadHash);
    }

    private static async Task<P606Fixture>
        ConvertP601FixtureToP607ChildAsync(
            IMongoDatabase database,
            P601Fixture seed,
            int maxReviewCycles,
            CancellationToken ct)
    {
        // Use a distinct exact form pin from the parent entry. The legacy
        // work/form/user binding is intentionally unique while active.
        var rootForm = seed.ExpectedSteps
            .Single(step => step.NodeId == "step_b").Form;
        var payload = JsonNode.Parse(seed.CanonicalPayload)?.AsObject()
                      ?? throw new InvalidOperationException(
                          "P6-07 child seed payload is invalid.");
        payload["archetypeId"] =
            DynamicFlowReviewLoopTopologyContract.ArchetypeId;
        payload["entryStepId"] = "step_b";
        payload["rootDynamicFormTemplateId"] =
            rootForm.FormVersionId;
        payload.Remove("resultOwnerStepId");
        payload.Remove("resultOwnerFormNodeId");
        payload.Remove("statisticsOwnerStepId");
        payload.Remove("statisticsOwnerFormNodeId");
        payload["formNodes"] = new JsonArray(
            new JsonObject
            {
                ["formNodeId"] = rootForm.FormNodeId,
                ["role"] = "ROOT",
                ["dynamicFormTemplateId"] = rootForm.FormVersionId,
                ["dynamicFormFamilyId"] = rootForm.FormFamilyId,
                ["dynamicFormVersionNo"] = rootForm.FormVersionNo,
                ["dynamicFormSchemaHash"] = rootForm.FormSchemaHash,
                ["dynamicFormSnapshotHash"] = rootForm.FormSchemaHash
            });
        payload["nodes"] = new JsonArray(
            P602Step("step_b", "REVIEW", rootForm.FormNodeId),
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
            P602Edge(
                "edge-review-marker",
                "step_b",
                "review_gateway"));
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
        _ = DynamicFlowReviewLoopTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        await database.GetCollection<DynamicFlowTemplateVersion>(
                "dynamic_flow_template_versions")
            .UpdateOneAsync(
                item => item.Id == seed.VersionId,
                Builders<DynamicFlowTemplateVersion>.Update
                    .Set(
                        item => item.RootDynamicFormTemplateId,
                        rootForm.FormVersionId)
                    .Set(item => item.PayloadJson, canonical.CanonicalJson)
                    .Set(item => item.PayloadHash, canonical.PayloadHash),
                cancellationToken: ct);
        await database.GetCollection<DynamicFlowTemplate>(
                "dynamic_flow_templates")
            .UpdateOneAsync(
                item => item.Id == seed.FamilyId,
                Builders<DynamicFlowTemplate>.Update
                    .Set(
                        item => item.RootDynamicFormTemplateId,
                        rootForm.FormVersionId)
                    .Set(
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

    private static async Task<object> AssertP607ScenarioAsync(
        IMongoDatabase database,
        P607Fixture parentFixture,
        P606Fixture childFixture,
        string parentInstanceId,
        string childInstanceId,
        string expectedInstanceState,
        string expectedStepState,
        string expectedOutcome,
        CancellationToken ct)
    {
        var parent = await LoadP601InstanceAsync(
            database,
            parentInstanceId,
            ct);
        var child = await LoadP601InstanceAsync(
            database,
            childInstanceId,
            ct);
        var parentStep = (await LoadP601StepsAsync(
                database,
                parentInstanceId,
                ct))
            .Single();
        var childSteps = await LoadP601StepsAsync(
            database,
            childInstanceId,
            ct);
        var parentEvents = await database
            .GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events")
            .Find(item => item.FlowInstanceId == parentInstanceId)
            .SortBy(item => item.Sequence)
            .ToListAsync(ct);
        var receipts = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                "dynamic_flow_runtime_command_receipts")
            .Find(item =>
                item.FlowInstanceId == parentInstanceId ||
                item.FlowInstanceId == childInstanceId)
            .ToListAsync(ct);

        Require(
            parent.State == expectedInstanceState &&
            parent.ArchetypeId ==
                DynamicFlowSubflowTopologyContract.ArchetypeId &&
            parent.TopologySnapshotJson ==
                parentFixture.CanonicalPayload &&
            parent.TopologySnapshotHash == parentFixture.PayloadHash,
            "P6-07 parent topology/state drifted.");
        Require(
            child.State == expectedInstanceState &&
            child.FlowTemplateId == parentFixture.ChildFamilyId &&
            child.FlowTemplateVersionId ==
                parentFixture.ChildVersionId &&
            child.TopologySnapshotJson ==
                childFixture.CanonicalPayload &&
            child.TopologySnapshotHash == childFixture.PayloadHash &&
            child.ParentInstanceId == parent.Id &&
            child.ParentStepInstanceId == parentStep.Id &&
            child.RootInstanceId == parent.Id &&
            child.AncestryPath.SequenceEqual(new[] { parent.Id }) &&
            child.AncestryFlowFamilyIds.SequenceEqual(
                new[] { parent.FlowTemplateId }),
            "P6-07 exact child pin or ancestry drifted.");
        Require(
            parentStep.State == expectedStepState &&
            parentStep.ChildInstanceId == child.Id &&
            parentStep.ChildFlowTemplateId == child.FlowTemplateId &&
            parentStep.ChildFlowVersionId ==
                child.FlowTemplateVersionId &&
            parentStep.ChildState == expectedInstanceState &&
            parentStep.ChildOutcomeCode == expectedOutcome,
            "P6-07 parent blocking outcome drifted.");
        Require(
            childSteps.Count == 1 &&
            receipts.Count(item =>
                item.FlowInstanceId == child.Id &&
                item.CommandType == "LAUNCH") == 1 &&
            receipts.Count(item =>
                item.FlowInstanceId == parent.Id &&
                item.CommandType ==
                    DynamicFlowRuntimeStateProjectionOperations
                        .SubflowChildTerminal) == 1 &&
            parentEvents.Count(item =>
                item.EventType ==
                    DynamicFlowSubflowTopologyContract
                        .ChildLaunchedEvent) == 1 &&
            parentEvents.Count(item =>
                item.EventType ==
                    (expectedInstanceState ==
                        DynamicFlowInstanceStates.Completed
                        ? DynamicFlowSubflowTopologyContract
                            .ChildCompletedEvent
                        : DynamicFlowSubflowTopologyContract
                            .ChildBlockedEvent)) == 1 &&
            parentEvents.Select(item => item.Sequence).SequenceEqual(
                Enumerable.Range(1, parentEvents.Count)
                    .Select(value => (long)value)),
            "P6-07 receipt/event ledger duplicated or drifted.");

        return new
        {
            parentInstanceId,
            childInstanceId,
            expectedInstanceState,
            exactChildFamilyId = child.FlowTemplateId,
            exactChildVersionId = child.FlowTemplateVersionId,
            ancestryDepth = child.AncestryPath.Count,
            receiptCount = receipts.Count,
            parentEventCount = parentEvents.Count,
            duplicateCount = 0,
            orphanCount = 0,
            immutableLineage = true
        };
    }

    private static async Task<object> AssertP607FutureBarrierAsync(
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
                             "FLOW-T10") >= 0)
                     .OrderBy(
                         item => item.ArchetypeId,
                         StringComparer.Ordinal))
        {
            var request = new DynamicFlowPreflightRequest
            {
                FlowTemplateVersionId = barrier.VersionId,
                CommandId =
                    $"p6-07-blocked-{barrier.ArchetypeId.ToLowerInvariant()}",
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
                    FlowTemplateVersionId =
                        request.FlowTemplateVersionId,
                    CommandId = request.CommandId,
                    TargetUnitIds = request.TargetUnitIds,
                    PeriodKey = request.PeriodKey,
                    ScheduleIdentityJson =
                        request.ScheduleIdentityJson,
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
                $"{barrier.ArchetypeId} crossed zero-write.");
            rows.Add(new { barrier.ArchetypeId, zeroWrite = true });
        }
        Require(rows.Count == 3, "P6-07 must verify T10..T12 barrier.");
        return new { count = rows.Count, rows };
    }

    private sealed record P607Fixture(
        string WorkId,
        string VersionId,
        string TargetUnitId,
        string ChildFamilyId,
        string ChildVersionId,
        string CanonicalPayload,
        string PayloadHash);
}
