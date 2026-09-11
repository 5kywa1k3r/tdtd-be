using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using tdtd_be.Common.Capabilities;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

internal static partial class P5MaterializationProbe
{
    internal const string P11CommandLineSwitch = "--p11-source-bound-diagnostic";

    internal static async Task<int> RunP11SourceBoundDiagnosticAsync(string[] args)
    {
        var chainId = RequiredArg(args, "--chain-id");
        var runKey = RequiredArg(args, "--run-key");
        var paths = HarnessPaths.CreateP11(runKey, chainId, "P11-01");
        var iterationRoot = paths.IterationRoot(1);
        var cleanupErrors = new List<string>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        var passed = false;
        string? failure = null;
        object? diagnostic = null;
        var deliberateAssertionRecovered = false;

        try
        {
            mongo = await MongoReplicaSetLease.StartP11Async(
                paths, iterationRoot, runKey, 1, CancellationToken.None);
            var database = mongo.Client.GetDatabase(mongo.DatabaseName);
            backend = await BackendServerLease.StartAsync(
                paths,
                Path.Combine(iterationRoot, "backend"),
                runKey,
                mongo,
                CancellationToken.None,
                new BackendServerOptions { EnableDynamicFlowRuntimeCandidate = true });

            using var api = new ApiHarnessClient(backend.BaseUri);
            var (adminToken, _) = await BootstrapAndLoginAsync(
                api, backend, database, CancellationToken.None);
            var fixture = await SeedFixtureAsync(database, CancellationToken.None);

            var formCreate = await api.PostAsync(
                "api/dynamic-forms",
                new JsonObject
                {
                    ["code"] = $"P11_DIAG_{runKey.ToUpperInvariant()}",
                    ["name"] = "P11 source-bound diagnostic Form",
                    ["description"] = "P11-01 production API viability diagnostic",
                    ["tagCodes"] = new JsonArray(),
                    ["schemaVersion"] = 1,
                    ["isActive"] = true,
                    ["schema"] = new JsonObject
                    {
                        ["sections"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = "main",
                                ["title"] = "Main",
                                ["order"] = 1
                            }
                        },
                        ["fields"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = "field_value",
                                ["sectionId"] = "main",
                                ["key"] = "value",
                                ["name"] = "Value",
                                ["type"] = "number",
                                ["required"] = false,
                                ["order"] = 1
                            }
                        },
                        ["blocks"] = new JsonArray()
                    }
                },
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(formCreate, HttpStatusCode.OK, "P11 production Form create");
            var formId = ApiHarnessClient.RequiredString(formCreate.Json, "id");
            var formRevision = ApiHarnessClient.RequiredInt(formCreate.Json, "revision");
            var formPublish = await api.PostAsync(
                $"api/dynamic-forms/{formId}/publish",
                new { expectedRevision = formRevision },
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(formPublish, HttpStatusCode.OK, "P11 production Form publish");
            Require(ApiHarnessClient.RequiredBool(formPublish.Json, "isPublished"),
                "P11 diagnostic Form did not publish.");

            var flowCreate = await api.PostAsync(
                "api/dynamic-flow-templates",
                new JsonObject
                {
                    ["commandId"] = $"{runKey}-flow-create",
                    ["code"] = $"P11_FLOW_{runKey.ToUpperInvariant()}",
                    ["name"] = "P11 source-bound diagnostic Flow",
                    ["description"] = "P11-01 CURRENT catalog viability diagnostic",
                    ["rootDynamicFormTemplateId"] = formId,
                    ["payload"] = BuildP11FlowPayload(formId)
                },
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(flowCreate, HttpStatusCode.OK, "P11 production Flow create");
            var family = ApiHarnessClient.RequiredObject(flowCreate.Json, "P11 Flow family");
            var draft = ApiHarnessClient.RequiredObject(family["draftVersion"], "P11 Flow draft");
            var familyId = ApiHarnessClient.RequiredString(family, "id");
            var versionId = ApiHarnessClient.RequiredString(draft, "id");
            var lockResponse = await api.PostAsync(
                $"api/dynamic-flow-templates/{familyId}/versions/{versionId}/lock",
                new
                {
                    commandId = $"{runKey}-flow-lock",
                    expectedFamilyRevision = ApiHarnessClient.RequiredInt(family, "familyRevision"),
                    expectedDraftRevision = ApiHarnessClient.RequiredInt(draft, "draftRevision"),
                    expectedPayloadHash = ApiHarnessClient.RequiredString(draft, "payloadHash")
                },
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(lockResponse, HttpStatusCode.OK, "P11 production Flow lock");
            var lockedRead = await api.GetAsync(
                $"api/dynamic-flow-templates/{familyId}/versions/{versionId}",
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(lockedRead, HttpStatusCode.OK, "P11 locked Flow read");
            Require(ApiHarnessClient.RequiredString(lockedRead.Json, "catalogVersion") ==
                    DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                "Locked Flow did not pin catalog CURRENT version.");
            Require(ApiHarnessClient.RequiredString(lockedRead.Json, "catalogSemanticHash") ==
                    DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
                "Locked Flow did not pin catalog CURRENT semantic hash.");

            var targetUnits = fixture.TargetUnitIds.Take(1).ToArray();
            var commandId = $"{runKey}-launch";
            var periodKey = "2026-08-P11";
            var preflight = await PreflightAsync(
                api, adminToken, fixture.T01WorkId, versionId, commandId,
                targetUnits, periodKey, CancellationToken.None);
            var confirm = await ConfirmAsync(
                api, adminToken, fixture.T01WorkId, versionId, commandId,
                targetUnits, periodKey, preflight.SnapshotToken, CancellationToken.None);
            RequireStatus(confirm, "SUCCEEDED", "P11 production Flow confirm");
            var flowInstanceId = ApiHarnessClient.RequiredString(confirm.Json, "flowInstanceId");

            var replay = await ConfirmAsync(
                api, adminToken, fixture.T01WorkId, versionId, commandId,
                targetUnits, periodKey, preflight.SnapshotToken, CancellationToken.None);
            ApiHarnessClient.ExpectStatus(replay, HttpStatusCode.OK, "P11 exact confirm replay");
            Require(ApiHarnessClient.RequiredString(replay.Json, "flowInstanceId") == flowInstanceId,
                "P11 exact confirm replay changed flowInstanceId.");

            var instance = await database.GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
                .Find(x => x.Id == flowInstanceId).SingleAsync();
            var steps = await database.GetCollection<DynamicFlowStepInstance>("dynamic_flow_step_instances")
                .Find(x => x.FlowInstanceId == flowInstanceId && !x.IsDeleted).ToListAsync();
            var outbox = await database.GetCollection<DynamicFlowRuntimeOutboxItem>("dynamic_flow_runtime_outbox")
                .Find(x => x.FlowInstanceId == flowInstanceId).ToListAsync();
            var assignments = await database.GetCollection<WorkAssignment>("work_assignments")
                .Find(x => x.FlowInstanceId == flowInstanceId && !x.IsDeleted).ToListAsync();
            Require(instance.State == DynamicFlowInstanceStates.Active,
                "P11 production instance did not become ACTIVE.");
            Require(instance.CatalogVersion == DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion &&
                    instance.CatalogSemanticHash == DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
                "P11 production instance catalog pin drifted from CURRENT.");
            Require(steps.Count == 1 && steps[0].FormVersionId == formId,
                "P11 materialized step did not preserve the production Form pin.");
            Require(outbox.Count == 1 && outbox.All(x =>
                    x.Status == DynamicFlowRuntimeOutboxStatuses.Completed),
                "P11 materialization outbox did not converge.");
            Require(assignments.Count == 1 && assignments[0].DynamicFormTemplateId == formId,
                "P11 materialization did not create the exact production assignment.");

            try
            {
                throw new InvalidOperationException("P11-DELIBERATE-ASSERTION");
            }
            catch (InvalidOperationException expected) when (
                expected.Message == "P11-DELIBERATE-ASSERTION")
            {
                deliberateAssertionRecovered = true;
            }

            diagnostic = new
            {
                schemaVersion = 1,
                runKey,
                productionOnly = true,
                form = new { id = formId, published = true },
                flow = new
                {
                    familyId,
                    versionId,
                    catalogVersion = instance.CatalogVersion,
                    catalogSemanticHash = instance.CatalogSemanticHash,
                    locked = true
                },
                runtime = new
                {
                    workId = fixture.T01WorkId,
                    flowInstanceId,
                    eligibility = DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
                    state = instance.State,
                    stepCount = steps.Count,
                    assignmentCount = assignments.Count,
                    outboxCount = outbox.Count,
                    outboxPendingCount = outbox.Count(x =>
                        x.Status != DynamicFlowRuntimeOutboxStatuses.Completed),
                    exactReplaySameInstance = true
                },
                actors = new
                {
                    issuerCount = 1,
                    assigneeCount = targetUnits.Length,
                    serverDerived = true,
                    authorityInRuntimePayload = false
                },
                deliberateAssertionRecovered
            };
            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, "p11-production-diagnostic.json"), diagnostic);
            passed = true;
        }
        catch (Exception error)
        {
            failure = $"{error.GetType().Name}: {error.Message}";
            Console.Error.WriteLine(error);
        }
        finally
        {
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

            var cleanupSucceeded = cleanupErrors.Count == 0 &&
                mongo?.DatabaseDropVerified == true &&
                mongo.ProcessStopVerified && mongo.PortReleaseVerified &&
                mongo.DataDirectoryRemovalVerified &&
                backend?.StopVerified == true && backend.PortReleaseVerified;
            passed &= cleanupSucceeded && deliberateAssertionRecovered;
            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, "p11-production-diagnostic-result.json"),
                new
                {
                    schemaVersion = 1,
                    runKey,
                    verdict = passed ? "PASS" : "FAIL",
                    failure,
                    diagnostic,
                    deliberateAssertionRecovered,
                    cleanup = new
                    {
                        databaseDropped = mongo?.DatabaseDropVerified ?? false,
                        mongoStopped = mongo?.ProcessStopVerified ?? false,
                        mongoPortReleased = mongo?.PortReleaseVerified ?? false,
                        mongoDataRemoved = mongo?.DataDirectoryRemovalVerified ?? false,
                        backendStopped = backend?.StopVerified ?? false,
                        backendPortReleased = backend?.PortReleaseVerified ?? false,
                        cleanupErrors
                    }
                });
        }

        Console.WriteLine(passed
            ? $"P11_SOURCE_BOUND_DIAGNOSTIC_PASS artifact={iterationRoot}"
            : $"P11_SOURCE_BOUND_DIAGNOSTIC_FAIL failure={failure ?? string.Join(";", cleanupErrors)} artifact={iterationRoot}");
        return passed ? 0 : 1;
    }

    private static JsonObject BuildP11FlowPayload(string formId)
        => new()
        {
            ["schemaVersion"] = 2,
            ["archetypeId"] = "FLOW-T01",
            ["entryStepId"] = "step_root",
            ["rootDynamicFormTemplateId"] = formId,
            ["formNodes"] = new JsonArray
            {
                new JsonObject
                {
                    ["formNodeId"] = "root_form",
                    ["role"] = "ROOT",
                    ["dynamicFormTemplateId"] = formId
                }
            },
            ["nodes"] = new JsonArray
            {
                new JsonObject
                {
                    ["nodeId"] = "step_root",
                    ["nodeCode"] = "ROOT",
                    ["nodeKind"] = "FORM_STEP",
                    ["formNodeId"] = "root_form",
                    ["declaredRoles"] = new JsonArray("OWNER")
                }
            },
            ["edges"] = new JsonArray(),
            ["actorPolicies"] = new JsonArray
            {
                new JsonObject
                {
                    ["policyId"] = "actor-owner-root",
                    ["stepId"] = "step_root",
                    ["stepCode"] = "*",
                    ["actorRole"] = "OWNER",
                    ["allowForward"] = true
                }
            },
            ["fieldPolicies"] = new JsonArray
            {
                new JsonObject
                {
                    ["policyId"] = "fields-owner-root",
                    ["dynamicFormTemplateId"] = "*",
                    ["stepId"] = "step_root",
                    ["stepCode"] = "*",
                    ["actorRole"] = "OWNER",
                    ["fieldId"] = "*",
                    ["fieldKey"] = "*",
                    ["read"] = true,
                    ["write"] = true
                }
            },
            ["tableColumnPolicies"] = new JsonArray(),
            ["mappingRules"] = new JsonArray(),
            ["rollbackPolicy"] = new JsonObject(),
            ["finalResultPolicy"] = new JsonObject(),
            ["statisticProfile"] = new JsonObject()
        };

    private static string RequiredArg(string[] args, string name)
    {
        var index = Array.FindIndex(args, value =>
            string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new ArgumentException($"Missing required argument {name}.");
        return args[index + 1];
    }
}
