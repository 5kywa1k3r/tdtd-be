using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

internal static partial class P5MaterializationProbe
{
    private static async Task RunP706MappingCasesExpandedAsync(
        HarnessCaseRunner cases,
        List<object> mongoEvidence,
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        string adminToken,
        ProbeFixture baseFixture,
        P7MappingFixture fixture,
        int activationThrough,
        CancellationToken ct)
    {
        _ = backend;
        _ = adminToken;
        _ = baseFixture;

        var workId = await CloneP7MappingWorkAsync(
            database,
            fixture.WorkId,
            "P706-POLICY",
            ct);
        var scenario = await PrepareP7MappingScenarioAsync(
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            workId,
            "p7-06-policy-launch",
            "2026-07-P706-POLICY",
            ct);

        ApiHarnessResponse? canonicalPreview = null;
        await cases.RunAsync(
            "P7-06-SERVER-POLICY-ALLOW-CAPABILITY",
            async () =>
            {
                var before = await CaptureP7MappingPersistenceStateAsync(
                    database,
                    scenario.TargetReportId,
                    ct);
                canonicalPreview = await PreviewP7MappingAsync(
                    api,
                    scenario,
                    new JsonObject(),
                    ct);
                AssertP7MappingPreviewIdentity(
                    canonicalPreview,
                    fixture,
                    scenario,
                    activationThrough);
                Require(
                    ApiHarnessClient.RequiredString(
                        canonicalPreview.Json,
                        "mappingCapability") == "ALLOWED" &&
                    ApiHarnessClient.RequiredString(
                        canonicalPreview.Json,
                        "mappingCapabilityReason") ==
                    "DYNAMIC_FLOW_MAPPING_CAPABILITY_ALLOWED" &&
                    ApiHarnessClient.RequiredBool(
                        canonicalPreview.Json,
                        "canPreview") &&
                    ApiHarnessClient.RequiredBool(
                        canonicalPreview.Json,
                        "canApply") == (activationThrough >= 8) &&
                    (activationThrough >= 7
                        ? canonicalPreview.Json?["previewToken"] is not null
                        : canonicalPreview.Json?["previewToken"] is null),
                    "P7-06 baseline did not preserve the server-derived allow capability across later staged token/apply activation.");
                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-06-SERVER-POLICY-ALLOW-CAPABILITY",
                        before,
                        ct));
                return new CaseObservation(
                    "The exact locked Flow version, target runtime step, ASSIGNEE role, Form pins, and fresh Draft lifecycle produced the stable ALLOWED capability with the stage-appropriate token/apply surface and no write.",
                    P7MappingFingerprint(
                        "P7-06",
                        "server-policy-allow",
                        ApiHarnessClient.RequiredString(
                            canonicalPreview.Json,
                            "sourceSignature")));
            });

        await cases.RunAsync(
            "P7-06-FIELD-READ-WRITE-HIDDEN-LOCKED-MATRIX",
            async () =>
            {
                var before = await CaptureP7MappingPersistenceStateAsync(
                    database,
                    scenario.TargetReportId,
                    ct);

                var readDenied = await ExecuteP706FlowPayloadMutationAsync(
                    database,
                    scenario,
                    payload =>
                    {
                        var policy = FindP706Policy(
                            payload,
                            "fieldPolicies",
                            "p7-target-child-assignee-allow");
                        policy["read"] = false;
                        policy["write"] = true;
                        policy["hidden"] = false;
                        policy["locked"] = false;
                    },
                    canonicalize: true,
                    () => PreviewP7MappingAsync(
                        api,
                        scenario,
                        new JsonObject(),
                        ct),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    readDenied,
                    HttpStatusCode.OK,
                    "P7-06 target read=false preview");
                Require(
                    readDenied.Json?["changes"] is JsonArray redactedChanges &&
                    redactedChanges.Count == 0 &&
                    !readDenied.Body.Contains(
                        P7MappingHiddenSourceValue,
                        StringComparison.Ordinal),
                    "P7-06 target read=false did not redact the mapped value and diff.");

                var deniedModes = new[]
                {
                    new P706PolicyMode("write", false, false, false),
                    new P706PolicyMode("hidden", true, true, false),
                    new P706PolicyMode("locked", true, false, true)
                };
                var outcomes = new List<object>();
                foreach (var mode in deniedModes)
                {
                    var denied = await ExecuteP706FlowPayloadMutationAsync(
                        database,
                        scenario,
                        payload =>
                        {
                            var policy = FindP706Policy(
                                payload,
                                "fieldPolicies",
                                "p7-target-child-assignee-allow");
                            policy["read"] = true;
                            policy["write"] = mode.Name != "write";
                            policy["hidden"] = mode.Hidden;
                            policy["locked"] = mode.Locked;
                        },
                        canonicalize: true,
                        () => ExecuteP706WithPoisonedSourcePayloadAsync(
                            database,
                            scenario,
                            () => PreviewP7MappingAsync(
                                api,
                                scenario,
                                new JsonObject(),
                                ct),
                            ct),
                        ct);
                    AssertP706TargetFieldDenied(
                        denied,
                        $"P7-06 target {mode.Name}");
                    outcomes.Add(new
                    {
                        mode = mode.Name,
                        statusCode = (int)denied.StatusCode,
                        reason =
                            "DYNAMIC_FLOW_MAPPING_TARGET_FIELD_WRITE_FORBIDDEN",
                        sourceHydrated = false
                    });
                }

                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-06-FIELD-READ-WRITE-HIDDEN-LOCKED-MATRIX",
                        before,
                        ct));
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-06-FIELD-READ-WRITE-HIDDEN-LOCKED-MATRIX-OUTCOME",
                    readDeniedRedacted = true,
                    outcomes,
                    rawPayloadRecorded = false
                });
                return new CaseObservation(
                    "Target read=false redacted the projected value, while write=false, hidden, and locked each failed with the stable field reason before a poisoned source payload could hydrate.",
                    P7MappingFingerprint(
                        "P7-06",
                        "field-policy-matrix",
                        string.Join(",", deniedModes.Select(item => item.Name))));
            });

        await cases.RunAsync(
            "P7-06-LOCKED-AFTER-SUBMIT-LIFECYCLE",
            async () =>
            {
                var before = await CaptureP7MappingPersistenceStateAsync(
                    database,
                    scenario.TargetReportId,
                    ct);
                var fresh = await ExecuteP706FlowPayloadMutationAsync(
                    database,
                    scenario,
                    ConfigureP706LockedAfterSubmitField,
                    canonicalize: true,
                    () => PreviewP7MappingAsync(
                        api,
                        scenario,
                        new JsonObject(),
                        ct),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    fresh,
                    HttpStatusCode.OK,
                    "P7-06 lockedAfterSubmit fresh Draft");

                var historical = await ExecuteP706FlowPayloadMutationAsync(
                    database,
                    scenario,
                    ConfigureP706LockedAfterSubmitField,
                    canonicalize: true,
                    () => ExecuteP706WithReturnedHistoryAsync(
                        database,
                        scenario.TargetReportId,
                        () => PreviewP7MappingAsync(
                            api,
                            scenario,
                            new JsonObject(),
                            ct),
                        ct),
                    ct);
                AssertP706TargetFieldDenied(
                    historical,
                    "P7-06 lockedAfterSubmit returned history");

                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-06-LOCKED-AFTER-SUBMIT-LIFECYCLE",
                        before,
                        ct));
                return new CaseObservation(
                    "lockedAfterSubmit remained writable on a never-submitted Draft and became readonly when canonical report history proved a prior lifecycle submission.",
                    P7MappingFingerprint(
                        "P7-06",
                        "locked-after-submit",
                        ((int)fresh.StatusCode).ToString(),
                        ((int)historical.StatusCode).ToString()));
            });

        await cases.RunAsync(
            "P7-06-TABLE-POLICY-API-LIFECYCLE",
            async () =>
            {
                var before = await CaptureP7MappingPersistenceStateAsync(
                    database,
                    scenario.TargetReportId,
                    ct);
                var baseline = await api.GetAsync(
                    $"api/work-assignment-reports/{scenario.TargetReportId}",
                    scenario.ActorToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    baseline,
                    HttpStatusCode.OK,
                    "P7-06 table default-deny raw GET");
                var baselinePermissions =
                    RequireP706Permissions(baseline, "P7-06 table default-deny");
                Require(
                    RequiredP706Bool(
                        baselinePermissions,
                        "denyAllTableColumns"),
                    "P7-06 missing table policy coverage did not expose default deny.");

                var fresh = await ExecuteP706FlowPayloadMutationAsync(
                    database,
                    scenario,
                    payload => AddP706WildcardTablePolicy(
                        payload,
                        fixture,
                        lockedAfterSubmit: true),
                    canonicalize: true,
                    () => api.GetAsync(
                        $"api/work-assignment-reports/{scenario.TargetReportId}",
                        scenario.ActorToken,
                        ct: ct),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    fresh,
                    HttpStatusCode.OK,
                    "P7-06 table wildcard fresh Draft");
                var freshTable = RequireP706WildcardTablePermission(fresh);
                Require(
                    RequiredP706Bool(freshTable, "read") &&
                    RequiredP706Bool(freshTable, "write") &&
                    RequiredP706Bool(freshTable, "lockedAfterSubmit") &&
                    !RequiredP706Bool(freshTable, "locked"),
                    "P7-06 fresh table policy flags drifted.");

                var historical = await ExecuteP706FlowPayloadMutationAsync(
                    database,
                    scenario,
                    payload => AddP706WildcardTablePolicy(
                        payload,
                        fixture,
                        lockedAfterSubmit: true),
                    canonicalize: true,
                    () => ExecuteP706WithReturnedHistoryAsync(
                        database,
                        scenario.TargetReportId,
                        () => api.GetAsync(
                            $"api/work-assignment-reports/{scenario.TargetReportId}",
                            scenario.ActorToken,
                            ct: ct),
                        ct),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    historical,
                    HttpStatusCode.OK,
                    "P7-06 table wildcard returned history");
                var historicalTable =
                    RequireP706WildcardTablePermission(historical);
                Require(
                    RequiredP706Bool(historicalTable, "read") &&
                    !RequiredP706Bool(historicalTable, "write") &&
                    RequiredP706Bool(historicalTable, "locked"),
                    "P7-06 table lockedAfterSubmit did not become readonly after lifecycle history.");

                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-06-TABLE-POLICY-API-LIFECYCLE",
                        before,
                        ct));
                return new CaseObservation(
                    "The real report API exposed table default-deny with no configured coverage, then evaluated wildcard table read/write and lockedAfterSubmit from the exact locked Flow snapshot.",
                    P7MappingFingerprint(
                        "P7-06",
                        "table-policy-lifecycle",
                        RequiredP706Bool(
                            baselinePermissions,
                            "denyAllTableColumns").ToString(),
                        RequiredP706Bool(
                            historicalTable,
                            "locked").ToString()));
            });

        await cases.RunAsync(
            "P7-06-REQUIRED-EFFECTIVE-AND-POLICY-LOAD-FAIL-CLOSED",
            async () =>
            {
                var before = await CaptureP7MappingPersistenceStateAsync(
                    database,
                    scenario.TargetReportId,
                    ct);
                var populated = await ExecuteP706FlowPayloadMutationAsync(
                    database,
                    scenario,
                    payload =>
                    {
                        FindP706Policy(
                            payload,
                            "fieldPolicies",
                            "p7-target-child-assignee-allow")["required"] =
                            true;
                    },
                    canonicalize: true,
                    () => PreviewP7MappingAsync(
                        api,
                        scenario,
                        new JsonObject(),
                        ct),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    populated,
                    HttpStatusCode.OK,
                    "P7-06 required populated projection");
                Require(
                    !ApiHarnessClient.RequiredBool(
                        populated.Json,
                        "hasBlockingConflicts") &&
                    ApiHarnessClient.RequiredString(
                        populated.Json,
                        "mappingCapability") == "ALLOWED",
                    "P7-06 required field rejected the nonblank effective mapped draft.");

                var missing = await ExecuteP706FlowPayloadMutationAsync(
                    database,
                    scenario,
                    payload =>
                    {
                        var policies = payload["fieldPolicies"]?.AsArray()
                                       ?? throw new InvalidOperationException(
                                           "P7-06 fieldPolicies are missing.");
                        policies.Add(new JsonObject
                        {
                            ["policyId"] =
                                "p7-target-required-wildcard",
                            ["dynamicFormTemplateId"] =
                                fixture.ChildForm.FormVersionId,
                            ["stepId"] = "step_b",
                            ["stepCode"] = "B",
                            ["actorRole"] = "ASSIGNEE",
                            ["fieldId"] = "*",
                            ["fieldKey"] = "*",
                            ["read"] = true,
                            ["write"] = true,
                            ["required"] = true
                        });
                    },
                    canonicalize: true,
                    () => PreviewP7MappingAsync(
                        api,
                        scenario,
                        new JsonObject(),
                        ct),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    missing,
                    HttpStatusCode.OK,
                    "P7-06 required missing effective projection");
                Require(
                    ApiHarnessClient.RequiredBool(
                        missing.Json,
                        "hasBlockingConflicts") &&
                    ApiHarnessClient.RequiredString(
                        missing.Json,
                        "mappingCapability") == "BLOCKED" &&
                    ApiHarnessClient.RequiredString(
                        missing.Json,
                        "mappingCapabilityReason") ==
                    "DYNAMIC_FLOW_MAPPING_REQUIRED_VALUE_MISSING" &&
                    !ApiHarnessClient.RequiredBool(
                        missing.Json,
                        "canApply"),
                    "P7-06 missing required effective value did not return the stable blocked capability.");

                var unavailable = await ExecuteP706FlowPayloadMutationAsync(
                    database,
                    scenario,
                    payload =>
                    {
                        var policies = payload["fieldPolicies"]?.AsArray()
                                       ?? throw new InvalidOperationException(
                                           "P7-06 fieldPolicies are missing.");
                        policies.Add(new JsonObject
                        {
                            ["policyId"] =
                                "p7-target-child-assignee-conflict",
                            ["dynamicFormTemplateId"] =
                                fixture.ChildForm.FormVersionId,
                            ["stepId"] = "step_b",
                            ["stepCode"] = "B",
                            ["actorRole"] = "ASSIGNEE",
                            ["fieldId"] = "field_child_value",
                            ["fieldKey"] = "child_value",
                            ["read"] = true,
                            ["write"] = false
                        });
                    },
                    canonicalize: true,
                    () => PreviewP7MappingAsync(
                        api,
                        scenario,
                        new JsonObject(),
                        ct),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    unavailable,
                    HttpStatusCode.ServiceUnavailable,
                    "P7-06 incomplete locked policy snapshot");
                Require(
                    ApiHarnessClient.FindStringRecursive(
                        unavailable.Json,
                        "errorCode") ==
                    "DYNAMIC_FLOW_MAPPING_POLICY_UNAVAILABLE" &&
                    ApiHarnessClient.FindStringRecursive(
                        unavailable.Json,
                        "reason") ==
                    "DYNAMIC_FLOW_MAPPING_TARGET_POLICY_LOAD_FAILED",
                    "P7-06 incomplete locked policy did not normalize to the stable policy-unavailable contract.");

                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-06-REQUIRED-EFFECTIVE-AND-POLICY-LOAD-FAIL-CLOSED",
                        before,
                        ct));
                return new CaseObservation(
                    "Required validation accepted the nonblank effective projection, blocked an absent required target, and normalized incomplete locked policy coverage to HTTP 503 with zero writes.",
                    P7MappingFingerprint(
                        "P7-06",
                        "required-policy-load",
                        ApiHarnessClient.RequiredString(
                            missing.Json,
                            "mappingCapabilityReason"),
                        ApiHarnessClient.RequiredString(
                            unavailable.Json,
                            "errorCode")));
            });

        await cases.RunAsync(
            "P7-06-FORGED-CONTEXT-PREHYDRATION-AND-ACTIVATION-BARRIER",
            async () =>
            {
                var preview = canonicalPreview
                              ?? throw new HarnessCaseNotRunnableException(
                                  "P7-06 canonical preview did not initialize.");
                var before = await CaptureP7MappingPersistenceStateAsync(
                    database,
                    scenario.TargetReportId,
                    ct);
                var forgedId = ObjectId.GenerateNewId().ToString();
                var forgeries = new (string Field, JsonNode Value)[]
                {
                    ("actorRole", JsonValue.Create("*")!),
                    ("stepId", JsonValue.Create("*")!),
                    (
                        "dynamicFormTemplateId",
                        JsonValue.Create(forgedId)!),
                    ("formVersionId", JsonValue.Create(forgedId)!),
                    ("targetReportId", JsonValue.Create(forgedId)!)
                };
                var responses =
                    await ExecuteP706WithPoisonedSourcePayloadAsync(
                        database,
                        scenario,
                        async () =>
                        {
                            var result =
                                new List<(string Field, ApiHarnessResponse Response)>();
                            foreach (var forgery in forgeries)
                            {
                                var response =
                                    await PreviewP7MappingAsync(
                                        api,
                                        scenario,
                                        new JsonObject
                                        {
                                            [forgery.Field] =
                                                forgery.Value.DeepClone()
                                        },
                                        ct);
                                result.Add((forgery.Field, response));
                            }

                            return result;
                        },
                        ct);
                foreach (var outcome in responses)
                {
                    AssertP706ForgedContextRejected(
                        outcome.Response,
                        outcome.Field,
                        scenario.SourceReportId);
                }

                if (activationThrough < 8)
                {
                    var apply = await api.PostAsync(
                        $"api/work-assignment-reports/{scenario.TargetReportId}/draft/apply-dynamic-flow-mapping",
                        activationThrough >= 7
                            ? BuildP707ApplyRequest(
                                preview,
                                "p7-06-cumulative-preflight")
                            : new JsonObject(),
                        scenario.ActorToken,
                        ct: ct);
                    AssertP705ApplyActivationBarrier(
                        apply,
                        scenario,
                        scenario.SourceReportId);
                }

                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-06-FORGED-CONTEXT-PREHYDRATION-AND-ACTIVATION-BARRIER",
                        before,
                        ct));
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-06-FORGED-CONTEXT-PREHYDRATION-AND-ACTIVATION-BARRIER-OUTCOME",
                    forgedFields =
                        forgeries.Select(item => item.Field).ToArray(),
                    poisonedSourcePayloadReached = false,
                    previewTokenPresent =
                        preview.Json?["previewToken"] is not null,
                    canApply =
                        ApiHarnessClient.RequiredBool(
                            preview.Json,
                            "canApply"),
                    applyBlocked = activationThrough < 8,
                    rawPayloadRecorded = false
                });
                return new CaseObservation(
                    "Forged wildcard role, step, Form, report, and version context failed before the poisoned source payload could hydrate; token/apply remained owned by later prompts.",
                    P7MappingFingerprint(
                        "P7-06",
                        "forged-context-activation",
                        string.Join(
                            ",",
                            forgeries.Select(item => item.Field))));
            });
    }

    private static JsonObject FindP706Policy(
        JsonObject payload,
        string collectionName,
        string policyId)
        => payload[collectionName]?
               .AsArray()
               .OfType<JsonObject>()
               .Single(policy =>
                   policy["policyId"]?.GetValue<string>() == policyId)
           ?? throw new InvalidOperationException(
               $"P7-06 policy {policyId} was not found in {collectionName}.");

    private static void ConfigureP706LockedAfterSubmitField(
        JsonObject payload)
    {
        var policy = FindP706Policy(
            payload,
            "fieldPolicies",
            "p7-target-child-assignee-allow");
        policy["read"] = true;
        policy["write"] = true;
        policy["hidden"] = false;
        policy["locked"] = false;
        policy["lockedAfterSubmit"] = true;
    }

    private static void AddP706WildcardTablePolicy(
        JsonObject payload,
        P7MappingFixture fixture,
        bool lockedAfterSubmit)
    {
        var policies = payload["tableColumnPolicies"]?.AsArray()
                       ?? throw new InvalidOperationException(
                           "P7-06 tableColumnPolicies are missing.");
        policies.Add(new JsonObject
        {
            ["policyId"] = "p7-target-table-wildcard",
            ["dynamicFormTemplateId"] =
                fixture.ChildForm.FormVersionId,
            ["stepId"] = "step_b",
            ["stepCode"] = "B",
            ["actorRole"] = "ASSIGNEE",
            ["blockId"] = "*",
            ["columnKey"] = "*",
            ["read"] = true,
            ["write"] = true,
            ["required"] = false,
            ["hidden"] = false,
            ["locked"] = false,
            ["lockedAfterSubmit"] = lockedAfterSubmit
        });
    }

    private static JsonObject RequireP706Permissions(
        ApiHarnessResponse response,
        string label)
        => response.Json?["dynamicFlowPermissions"] as JsonObject
           ?? throw new InvalidOperationException(
               $"{label} omitted dynamicFlowPermissions.");

    private static JsonObject RequireP706WildcardTablePermission(
        ApiHarnessResponse response)
    {
        var permissions =
            RequireP706Permissions(response, "P7-06 wildcard table policy");
        var tableColumns = permissions["tableColumns"] as JsonObject
                           ?? throw new InvalidOperationException(
                               "P7-06 tableColumns permission map is missing.");
        return tableColumns["*:*"] as JsonObject
               ?? throw new InvalidOperationException(
                   "P7-06 wildcard table permission is missing.");
    }

    private static bool RequiredP706Bool(
        JsonObject value,
        string propertyName)
        => value[propertyName]?.GetValue<bool>()
           ?? throw new InvalidOperationException(
               $"P7-06 required boolean {propertyName} is missing.");

    private static void AssertP706TargetFieldDenied(
        ApiHarnessResponse response,
        string label)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Forbidden,
            label);
        Require(
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "errorCode") ==
            "DYNAMIC_FLOW_MAPPING_FIELD_WRITE_FORBIDDEN" &&
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "reason") ==
            "DYNAMIC_FLOW_MAPPING_TARGET_FIELD_WRITE_FORBIDDEN",
            $"{label} did not return the stable target field policy denial.");
    }

    private static void AssertP706ForgedContextRejected(
        ApiHarnessResponse response,
        string field,
        string sourceReportId)
    {
        if (field is "actorRole" or "dynamicFormTemplateId")
        {
            AssertP705FlowOwnedForgeryRejected(
                response,
                field,
                sourceReportId);
            return;
        }

        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Conflict,
            $"P7-06 forged {field}");
        Require(
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "errorCode") ==
            "DYNAMIC_FLOW_MAPPING_IDENTITY_CONFLICT" &&
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "field") == field &&
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "reason") ==
            "DYNAMIC_FLOW_MAPPING_IDENTITY_CONFLICT" &&
            !response.Body.Contains(
                sourceReportId,
                StringComparison.OrdinalIgnoreCase),
            $"P7-06 forged {field} did not return the exact non-leaking identity conflict.");
    }

    private static async Task<T> ExecuteP706FlowPayloadMutationAsync<T>(
        IMongoDatabase database,
        P7MappingScenario scenario,
        Action<JsonObject> mutate,
        bool canonicalize,
        Func<Task<T>> operation,
        CancellationToken ct)
    {
        var versions = database.GetCollection<DynamicFlowTemplateVersion>(
            "dynamic_flow_template_versions");
        var instances = database.GetCollection<DynamicFlowInstance>(
            "dynamic_flow_instances");
        var families = database.GetCollection<DynamicFlowTemplate>(
            "dynamic_flow_templates");
        var instance = await instances
            .Find(item => item.Id == scenario.FlowInstanceId)
            .SingleAsync(ct);
        var version = await versions
            .Find(item => item.Id == instance.FlowTemplateVersionId)
            .SingleAsync(ct);
        var family = await families
            .Find(item => item.Id == instance.FlowTemplateId)
            .SingleAsync(ct);
        var payload = JsonNode.Parse(version.PayloadJson)?.AsObject()
                      ?? throw new InvalidOperationException(
                          "P7-06 locked payload is invalid JSON.");
        mutate(payload);

        string nextJson;
        string nextHash;
        if (canonicalize)
        {
            var canonical =
                DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                    payload.ToJsonString(),
                    new DynamicFlowDefinitionValidationOptions(
                        AllowLegacy: false,
                        AllowServerManagedPins: true,
                        RequireServerManagedPins: true,
                        AllowHistoricalCatalogPins: true));
            nextJson = canonical.CanonicalJson;
            nextHash = canonical.PayloadHash;
        }
        else
        {
            nextJson = payload.ToJsonString();
            nextHash = Convert
                .ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(nextJson)))
                .ToLowerInvariant();
        }

        try
        {
            await versions.UpdateOneAsync(
                item => item.Id == version.Id,
                Builders<DynamicFlowTemplateVersion>.Update
                    .Set(item => item.PayloadJson, nextJson)
                    .Set(item => item.PayloadHash, nextHash),
                cancellationToken: ct);
            await families.UpdateOneAsync(
                item => item.Id == family.Id,
                Builders<DynamicFlowTemplate>.Update
                    .Set(item => item.CurrentVersionHash, nextHash),
                cancellationToken: ct);
            await instances.UpdateOneAsync(
                item => item.Id == instance.Id,
                Builders<DynamicFlowInstance>.Update
                    .Set(item => item.FlowPayloadHash, nextHash),
                cancellationToken: ct);
            return await operation();
        }
        finally
        {
            await instances.UpdateOneAsync(
                item => item.Id == instance.Id,
                Builders<DynamicFlowInstance>.Update
                    .Set(
                        item => item.FlowPayloadHash,
                        instance.FlowPayloadHash),
                cancellationToken: ct);
            await families.UpdateOneAsync(
                item => item.Id == family.Id,
                Builders<DynamicFlowTemplate>.Update
                    .Set(
                        item => item.CurrentVersionHash,
                        family.CurrentVersionHash),
                cancellationToken: ct);
            await versions.UpdateOneAsync(
                item => item.Id == version.Id,
                Builders<DynamicFlowTemplateVersion>.Update
                    .Set(item => item.PayloadJson, version.PayloadJson)
                    .Set(item => item.PayloadHash, version.PayloadHash),
                cancellationToken: ct);
        }
    }

    private static async Task<T>
        ExecuteP706WithPoisonedSourcePayloadAsync<T>(
            IMongoDatabase database,
            P7MappingScenario scenario,
            Func<Task<T>> operation,
            CancellationToken ct)
    {
        var reports = database.GetCollection<WorkAssignmentReport>(
            "work_assignment_report");
        var payloads = database.GetCollection<WorkReportPayload>(
            "work_report_payloads");
        var source = await reports
            .Find(item => item.Id == scenario.SourceReportId)
            .SingleAsync(ct);
        var payload = await payloads
            .Find(item =>
                item.ReportId == scenario.SourceReportId &&
                item.PayloadRevision == source.PayloadRevision &&
                !item.IsDeleted)
            .SingleAsync(ct);
        try
        {
            await payloads.UpdateOneAsync(
                item => item.Id == payload.Id,
                Builders<WorkReportPayload>.Update
                    .Set(item => item.PayloadHash, new string('0', 64)),
                cancellationToken: ct);
            return await operation();
        }
        finally
        {
            await payloads.UpdateOneAsync(
                item => item.Id == payload.Id,
                Builders<WorkReportPayload>.Update
                    .Set(item => item.PayloadHash, payload.PayloadHash),
                cancellationToken: ct);
        }
    }

    private static async Task<T> ExecuteP706WithReturnedHistoryAsync<T>(
        IMongoDatabase database,
        string reportId,
        Func<Task<T>> operation,
        CancellationToken ct)
    {
        var reports = database.GetCollection<WorkAssignmentReport>(
            "work_assignment_report");
        var report = await reports
            .Find(item => item.Id == reportId)
            .SingleAsync(ct);
        try
        {
            await reports.UpdateOneAsync(
                item => item.Id == reportId,
                Builders<WorkAssignmentReport>.Update
                    .Set(item => item.ReturnedAtUtc, DateTime.UtcNow),
                cancellationToken: ct);
            return await operation();
        }
        finally
        {
            await reports.UpdateOneAsync(
                item => item.Id == reportId,
                Builders<WorkAssignmentReport>.Update
                    .Set(
                        item => item.ReturnedAtUtc,
                        report.ReturnedAtUtc),
                cancellationToken: ct);
        }
    }

    private sealed record P706PolicyMode(
        string Name,
        bool Denied,
        bool Hidden,
        bool Locked);
}
