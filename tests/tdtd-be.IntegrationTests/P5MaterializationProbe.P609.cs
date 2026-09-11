using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Bounded P6-09 proof against real Kestrel and a Mongo replica set. It
/// verifies coordinator-only add/cancel, exact replay, outbox materialization,
/// zero-write forged access, timeline audit, and the unique supplemental
/// identity index directly in Mongo.
/// </summary>
internal static partial class P5MaterializationProbe
{
    public static async Task<int> RunP609Async()
    {
        var runKey =
            $"p609_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}";
        var paths = HarnessPaths.Create(runKey);
        var iterationRoot = paths.IterationRoot(1);
        var cleanupErrors = new List<string>();
        var exchanges = new List<ApiExchangeEvidence>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        ApiHarnessClient? apiClient = null;
        object? directMongo = null;
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
                "backend-p6-supplemental",
                11);
            var api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            var (adminToken, _) = await BootstrapAndLoginAsync(
                api,
                backend,
                database,
                CancellationToken.None);
            var baseFixture = await SeedFixtureAsync(
                database,
                CancellationToken.None);
            var seed = await SeedP601FixtureAsync(
                database,
                baseFixture,
                CancellationToken.None);
            var fixture = await ConvertP601FixtureToP609Async(
                database,
                seed,
                CancellationToken.None);
            await RenameP603FixtureAsync(
                database,
                seed,
                "P6_09_SUPPLEMENTAL",
                "P6-09-SUPPLEMENTAL",
                CancellationToken.None);

            var launch = await LaunchAsync(
                api,
                adminToken,
                baseFixture,
                fixture.WorkId,
                fixture.VersionId,
                "p6-09-launch",
                [fixture.TargetUnitId],
                "2026-07-P609",
                CancellationToken.None);
            RequireStatus(
                launch.Confirm,
                "SUCCEEDED",
                "P6-09 FLOW-T11 launch");
            var instanceId = ApiHarnessClient.RequiredString(
                launch.Confirm.Json,
                "flowInstanceId");
            var instance = await database
                .GetCollection<DynamicFlowInstance>(
                    "dynamic_flow_instances")
                .Find(item => item.Id == instanceId)
                .SingleAsync();

            var addRequest = new DynamicFlowSupplementalAddRequest
            {
                CommandId = "p6-09-add-required",
                ExpectedInstanceRevision = instance.Revision,
                FormNodeId = fixture.FormNodeId,
                TargetUnitId = fixture.TargetUnitId,
                CompletionRequired = true
            };
            var add = await api.PostAsync(
                $"api/works/{fixture.WorkId}/dynamic-flows/instances/{instanceId}/supplemental-steps",
                addRequest,
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                add,
                HttpStatusCode.OK,
                "P6-09 add required supplemental");
            var supplementalStepId = ApiHarnessClient.RequiredString(
                add.Json,
                "supplementalStepId");
            Require(
                ApiHarnessClient.RequiredBool(
                    add.Json,
                    "completionRequired"),
                "P6-09 required policy was not persisted.");
            var addReplay = await api.PostAsync(
                $"api/works/{fixture.WorkId}/dynamic-flows/instances/{instanceId}/supplemental-steps",
                addRequest,
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                addReplay,
                HttpStatusCode.OK,
                "P6-09 add replay");
            Require(
                ApiHarnessClient.RequiredString(
                    addReplay.Json,
                    "supplementalStepId") == supplementalStepId &&
                ApiHarnessClient.RequiredBool(
                    addReplay.Json,
                    "replayed"),
                "P6-09 add replay changed identity.");

            var step = await database
                .GetCollection<DynamicFlowStepInstance>(
                    "dynamic_flow_step_instances")
                .Find(item => item.Id == supplementalStepId)
                .SingleAsync();
            Require(
                step.IsSupplemental &&
                step.State == DynamicFlowStepStates.Assigned &&
                step.AssignmentId is not null &&
                step.RequestedByUserId == instance.IssuerUserId,
                "P6-09 supplemental outbox did not materialize exact ownership.");

            var outsiderToken = await PrepareP601ActorLoginAsync(
                api,
                backend,
                database,
                seed.OutsiderUserId,
                CancellationToken.None);
            var beforeForged = await CaptureP601LedgerHashAsync(
                database,
                instanceId,
                CancellationToken.None);
            var forged = await api.PostAsync(
                $"api/works/{fixture.WorkId}/dynamic-flows/instances/{instanceId}/supplemental-steps",
                new DynamicFlowSupplementalAddRequest
                {
                    CommandId = "p6-09-forged-add",
                    ExpectedInstanceRevision =
                        RequiredP601Long(
                            add.Json,
                            "instanceRevision"),
                    FormNodeId = fixture.FormNodeId,
                    TargetUnitId = fixture.TargetUnitId,
                    CompletionRequired = false
                },
                outsiderToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                forged,
                HttpStatusCode.Forbidden,
                "P6-09 forged coordinator");
            Require(
                beforeForged == await CaptureP601LedgerHashAsync(
                    database,
                    instanceId,
                    CancellationToken.None),
                "P6-09 forged command changed the direct-Mongo ledger.");

            var cancelRequest =
                new DynamicFlowSupplementalCancelRequest
                {
                    CommandId = "p6-09-cancel-required",
                    ExpectedInstanceRevision =
                        RequiredP601Long(
                            add.Json,
                            "instanceRevision"),
                    ExpectedStepRevision = step.Revision,
                    Reason = "integration cancellation"
                };
            var cancel = await api.PostAsync(
                $"api/works/{fixture.WorkId}/dynamic-flows/instances/{instanceId}/supplemental-steps/{supplementalStepId}/cancel",
                cancelRequest,
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                cancel,
                HttpStatusCode.OK,
                "P6-09 cancel supplemental");
            var cancelReplay = await api.PostAsync(
                $"api/works/{fixture.WorkId}/dynamic-flows/instances/{instanceId}/supplemental-steps/{supplementalStepId}/cancel",
                cancelRequest,
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                cancelReplay,
                HttpStatusCode.OK,
                "P6-09 cancel replay");
            Require(
                ApiHarnessClient.RequiredBool(
                    cancelReplay.Json,
                    "replayed"),
                "P6-09 cancel replay was not recognized.");

            var steps = database.GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances");
            var events = database.GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events");
            var receipts =
                database.GetCollection<DynamicFlowRuntimeCommandReceipt>(
                    "dynamic_flow_runtime_command_receipts");
            step = await steps
                .Find(item => item.Id == supplementalStepId)
                .SingleAsync();
            var supplementalEvents = await events
                .Find(item =>
                    item.FlowInstanceId == instanceId &&
                    item.StepInstanceId == supplementalStepId)
                .ToListAsync();
            var supplementalReceipts = await receipts
                .Find(item =>
                    item.FlowInstanceId == instanceId &&
                    (item.CommandType ==
                        DynamicFlowSupplementalTopologyContract.AddCommand ||
                     item.CommandType ==
                        DynamicFlowSupplementalTopologyContract.CancelCommand))
                .ToListAsync();
            Require(
                step.State == DynamicFlowStepStates.CancelledByGateway &&
                step.SupplementalCancelledByUserId ==
                    instance.IssuerUserId &&
                supplementalReceipts.Count == 2 &&
                supplementalEvents.Count(item =>
                    item.EventType ==
                    DynamicFlowSupplementalTopologyContract.AddedEvent) == 1 &&
                supplementalEvents.Count(item =>
                    item.EventType ==
                    DynamicFlowSupplementalTopologyContract
                        .MaterializedEvent) == 1 &&
                supplementalEvents.Count(item =>
                    item.EventType ==
                    DynamicFlowSupplementalTopologyContract
                        .CancelledEvent) == 1,
                "P6-09 direct-Mongo receipt/timeline audit drifted.");

            var duplicateRejected = false;
            try
            {
                await steps.InsertOneAsync(new DynamicFlowStepInstance
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    FlowInstanceId = instanceId,
                    ExecutionEpoch = instance.ExecutionEpoch,
                    IsSupplemental = true,
                    SupplementalStepId = supplementalStepId,
                    State = DynamicFlowStepStates.Pending,
                    IsDeleted = false
                });
            }
            catch (MongoWriteException error)
                when (error.WriteError?.Category ==
                      ServerErrorCategory.DuplicateKey)
            {
                duplicateRejected = true;
            }
            Require(
                duplicateRejected,
                "P6-09 unique supplemental identity index accepted duplicate.");
            directMongo = new
            {
                instanceId,
                supplementalStepId,
                state = step.State,
                step.CompletionRequired,
                receiptCount = supplementalReceipts.Count,
                eventTypes = supplementalEvents
                    .Select(item => item.EventType)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                uniqueDuplicateRejected = duplicateRejected,
                forgedZeroWriteHash = beforeForged
            };
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
                    cleanupErrors.Add(
                        $"backend-stop: {error.Message}");
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
                    cleanupErrors.Add(
                        $"mongo-drop: {error.Message}");
                }
                try { await mongo.StopProcessAsync(); }
                catch (Exception error)
                {
                    cleanupErrors.Add(
                        $"mongo-stop: {error.Message}");
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
                "p6-09-supplemental-api-exchanges.json"),
            exchanges);
        await EvidenceJson.WriteAsync(
            Path.Combine(
                iterationRoot,
                "p6-09-supplemental-direct-mongo.json"),
            new { runKey, directMongo });
        var resultPath = Path.Combine(
            iterationRoot,
            "p6-09-supplemental-result.json");
        await EvidenceJson.WriteAsync(
            resultPath,
            new
            {
                runKey,
                verdict = passed ? "PASS" : "FAIL",
                requirements = new[] { "P6-TOPO-012" },
                failure,
                cleanupErrors,
                artifactRoot = iterationRoot,
                completedAtUtc = DateTime.UtcNow
            });
        Console.WriteLine(
            passed
                ? $"[PASS] P6-09 supplemental probe passed; artifact={resultPath}"
                : $"[FAIL] P6-09 supplemental probe failed: {failure ?? string.Join("; ", cleanupErrors)}; artifact={resultPath}");
        return passed ? 0 : 1;
    }

    private static async Task<P609Fixture>
        ConvertP601FixtureToP609Async(
            IMongoDatabase database,
            P601Fixture seed,
            CancellationToken ct)
    {
        var rootForm = seed.ExpectedSteps
            .Single(step => step.NodeId == "step_a").Form;
        var payload = JsonNode.Parse(seed.CanonicalPayload)?.AsObject()
                      ?? throw new InvalidOperationException(
                          "P6-09 seed payload is invalid.");
        payload["archetypeId"] =
            DynamicFlowSupplementalTopologyContract.ArchetypeId;
        payload["entryStepId"] = "step_a";
        payload["resultOwnerStepId"] = "step_a";
        payload["resultOwnerFormNodeId"] = rootForm.FormNodeId;
        payload["statisticsOwnerStepId"] = "step_a";
        payload["statisticsOwnerFormNodeId"] = rootForm.FormNodeId;
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
            new JsonObject
            {
                ["nodeId"] = "step_a",
                ["nodeCode"] = "SUPPLEMENTAL_TEMPLATE",
                ["nodeKind"] = DynamicFlowNodeKinds.FormStep,
                ["formNodeId"] = rootForm.FormNodeId,
                ["declaredRoles"] = new JsonArray("ASSIGNEE")
            });
        payload["edges"] = new JsonArray();
        payload["actorPolicies"] = new JsonArray();
        payload["fieldPolicies"] = new JsonArray();
        payload["tableColumnPolicies"] = new JsonArray();
        payload["mappingRules"] = new JsonArray();
        payload["finalResultPolicy"] = new JsonObject();
        var canonical =
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                payload.ToJsonString(),
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true));
        _ = DynamicFlowSupplementalTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
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
        return new P609Fixture(
            seed.WorkId,
            seed.VersionId,
            seed.TargetUnitId,
            rootForm.FormNodeId);
    }

    private sealed record P609Fixture(
        string WorkId,
        string VersionId,
        string TargetUnitId,
        string FormNodeId);
}
