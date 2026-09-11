using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

internal static partial class P5MaterializationProbe
{
    private static async Task RunP707MappingCasesExpandedAsync(
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
        var workId = await CloneP7MappingWorkAsync(
            database,
            fixture.WorkId,
            "P707-TOKEN",
            ct);
        var scenario = await PrepareP7MappingScenarioAsync(
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            workId,
            "p7-07-token-launch",
            "2026-07-P707-TOKEN",
            ct);

        ApiHarnessResponse? canonicalPreview = null;
        await cases.RunAsync(
            "P7-07-DETERMINISTIC-PREVIEW-PROPERTY-ORDER",
            async () =>
            {
                var before = await CaptureP7MappingPersistenceStateAsync(
                    database,
                    scenario.TargetReportId,
                    ct);
                var forwardOrder = new JsonObject
                {
                    ["flowFamilyId"] = fixture.FamilyId,
                    ["flowVersionId"] = fixture.VersionId,
                    ["flowPayloadHash"] = fixture.PayloadHash,
                    ["flowInstanceId"] = scenario.FlowInstanceId,
                    ["stepId"] = "step_b",
                    ["targetReportId"] = scenario.TargetReportId,
                    ["formVersionId"] = fixture.ChildForm.FormVersionId
                };
                var reverseOrder = new JsonObject
                {
                    ["formVersionId"] = fixture.ChildForm.FormVersionId,
                    ["targetReportId"] = scenario.TargetReportId,
                    ["stepId"] = "step_b",
                    ["flowInstanceId"] = scenario.FlowInstanceId,
                    ["flowPayloadHash"] = fixture.PayloadHash,
                    ["flowVersionId"] = fixture.VersionId,
                    ["flowFamilyId"] = fixture.FamilyId
                };

                canonicalPreview = await PreviewP7MappingAsync(
                    api,
                    scenario,
                    forwardOrder,
                    ct);
                var reordered = await PreviewP7MappingAsync(
                    api,
                    scenario,
                    reverseOrder,
                    ct);
                var repeated = await PreviewP7MappingAsync(
                    api,
                    scenario,
                    new JsonObject(),
                    ct);
                foreach (var preview in new[]
                         {
                             canonicalPreview,
                             reordered,
                             repeated
                         })
                {
                    AssertP7MappingPreviewIdentity(
                        preview,
                        fixture,
                        scenario,
                        activationThrough);
                    Require(
                        preview.Json?["previewToken"] is JsonValue &&
                        ApiHarnessClient.RequiredString(
                                preview.Json,
                                "previewToken")
                            .Split('.').Length == 3,
                        "P7-07 preview did not issue a scoped three-segment token.");
                }

                var sourceSignature = ApiHarnessClient.RequiredString(
                    canonicalPreview.Json,
                    "sourceSignature");
                var resultSemanticHash = ApiHarnessClient.RequiredString(
                    canonicalPreview.Json,
                    "resultSemanticHash");
                Require(
                    sourceSignature == ApiHarnessClient.RequiredString(
                        reordered.Json,
                        "sourceSignature") &&
                    sourceSignature == ApiHarnessClient.RequiredString(
                        repeated.Json,
                        "sourceSignature") &&
                    resultSemanticHash == ApiHarnessClient.RequiredString(
                        reordered.Json,
                        "resultSemanticHash") &&
                    resultSemanticHash == ApiHarnessClient.RequiredString(
                        repeated.Json,
                        "resultSemanticHash"),
                    "P7-07 source/result signatures changed across property order or repeated preview.");
                Require(
                    P707ProjectionShape(canonicalPreview.Json) ==
                    P707ProjectionShape(reordered.Json) &&
                    P707ProjectionShape(canonicalPreview.Json) ==
                    P707ProjectionShape(repeated.Json),
                    "P7-07 preview value/diff/redacted provenance changed on the same snapshot.");

                var claims = DecodeP707PreviewClaims(
                    ApiHarnessClient.RequiredString(
                        canonicalPreview.Json,
                        "previewToken"));
                Require(
                    claims.ActorId == scenario.ActorUserId &&
                    claims.TargetReportId == scenario.TargetReportId &&
                    claims.TargetAssignmentId ==
                    scenario.TargetAssignmentId &&
                    claims.FlowInstanceId == scenario.FlowInstanceId &&
                    claims.SourceSignature == sourceSignature &&
                    claims.ResultSemanticHash == resultSemanticHash &&
                    claims.ExpiresAtUtc - claims.IssuedAtUtc ==
                    DynamicFlowMappingSecurityContract.PreviewTokenTtl,
                    "P7-07 token claims did not bind the exact actor/target/runtime/snapshot.");

                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-07-DETERMINISTIC-PREVIEW-PROPERTY-ORDER",
                        before,
                        ct));
                return new CaseObservation(
                    "Three real previews over one snapshot produced identical value/diff, redacted provenance, source signature, and semantic hash independent of request property order.",
                    P7MappingFingerprint(
                        "P7-07",
                        "deterministic-preview",
                        sourceSignature,
                        resultSemanticHash));
            });

        await cases.RunAsync(
            "P7-07-VALID-PARITY-PREFLIGHT-P7-08-BARRIER",
            async () =>
            {
                var preview = canonicalPreview
                              ?? throw new HarnessCaseNotRunnableException(
                                  "P7-07 canonical preview did not initialize.");
                var before = await CaptureP7MappingPersistenceStateAsync(
                    database,
                    scenario.TargetReportId,
                    ct);
                if (activationThrough < 8)
                {
                    var response = await ApplyP707Async(
                        api,
                        scenario,
                        BuildP707ApplyRequest(
                            preview,
                            "p7-07-valid-parity"),
                        scenario.ActorToken,
                        ct);
                    AssertP707P708ActivationBarrier(
                        response,
                        "P7-07 valid parity preflight");
                }
                else
                {
                    Require(
                        ApiHarnessClient.RequiredBool(
                            preview.Json,
                            "canApply"),
                        "P7 cumulative activation >= P7-08 did not expose the later apply capability.");
                }

                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-07-VALID-PARITY-PREFLIGHT-P7-08-BARRIER",
                        before,
                        ct));
                return new CaseObservation(
                    activationThrough < 8
                        ? "Exact target revisions, source signature, semantic hash, actor scope, and token passed canonical apply preflight before the P7-08 persistence barrier."
                        : "The cumulative runner preserved the signed P7-07 zero-write proof while the owning P7-08 slice exposed apply.",
                    P7MappingFingerprint(
                        "P7-07",
                        "valid-parity",
                        activationThrough.ToString()));
            });

        await cases.RunAsync(
            "P7-07-SOURCE-TARGET-DRIFT-ZERO-WRITE",
            async () =>
            {
                var preview = canonicalPreview
                              ?? throw new HarnessCaseNotRunnableException(
                                  "P7-07 canonical preview did not initialize.");
                var before = await CaptureP7MappingPersistenceStateAsync(
                    database,
                    scenario.TargetReportId,
                    ct);
                var sourceDrift = await ExecuteP707WithSourceCurrentAsync(
                    database,
                    scenario.SourceReportId,
                    isCurrent: false,
                    () => ApplyP707Async(
                        api,
                        scenario,
                        BuildP707ApplyRequest(
                            preview,
                            "p7-07-source-drift"),
                        scenario.ActorToken,
                        ct),
                    ct);
                AssertP707Conflict(
                    sourceDrift,
                    "P7-07 source drift",
                    "DYNAMIC_FLOW_MAPPING_IDENTITY_CONFLICT",
                    "DYNAMIC_FLOW_MAPPING_SOURCE_REPORT_INEFFECTIVE",
                    "DYNAMIC_FLOW_MAPPING_SOURCE_PIN_CONFLICT");

                var targetDrift =
                    await ExecuteP707WithTargetLifecycleRevisionAsync(
                        database,
                        scenario,
                        () => ApplyP707Async(
                            api,
                            scenario,
                            BuildP707ApplyRequest(
                                preview,
                                "p7-07-target-drift"),
                            scenario.ActorToken,
                            ct),
                        ct);
                AssertP707Conflict(
                    targetDrift,
                    "P7-07 target drift",
                    "DYNAMIC_FLOW_MAPPING_TARGET_REVISION_CONFLICT",
                    "DYNAMIC_FLOW_MAPPING_EXPECTED_TARGET_STALE");

                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-07-SOURCE-TARGET-DRIFT-ZERO-WRITE",
                        before,
                        ct));
                return new CaseObservation(
                    "Canonical source effectiveness and target lifecycle drift each returned a stable HTTP 409 and restored the exact Mongo snapshot.",
                    P7MappingFingerprint(
                        "P7-07",
                        "source-target-drift",
                        ApiHarnessClient.FindStringRecursive(
                            sourceDrift.Json,
                            "reason") ?? string.Empty,
                        ApiHarnessClient.FindStringRecursive(
                            targetDrift.Json,
                            "reason") ?? string.Empty));
            });

        await cases.RunAsync(
            "P7-07-POLICY-PIN-DRIFT-ZERO-WRITE",
            async () =>
            {
                var preview = canonicalPreview
                              ?? throw new HarnessCaseNotRunnableException(
                                  "P7-07 canonical preview did not initialize.");
                var before = await CaptureP7MappingPersistenceStateAsync(
                    database,
                    scenario.TargetReportId,
                    ct);
                var policyDrift =
                    await ExecuteP706FlowPayloadMutationAsync(
                        database,
                        scenario,
                        payload =>
                        {
                            var policy = FindP706Policy(
                                payload,
                                "fieldPolicies",
                                "p7-target-child-assignee-allow");
                            policy["lockedAfterSubmit"] = false;
                        },
                        canonicalize: true,
                        () => ApplyP707Async(
                            api,
                            scenario,
                            BuildP707ApplyRequest(
                                preview,
                                "p7-07-policy-drift"),
                            scenario.ActorToken,
                            ct),
                        ct);
                AssertP707Conflict(
                    policyDrift,
                    "P7-07 policy drift",
                    "DYNAMIC_FLOW_MAPPING_SOURCE_SIGNATURE_CONFLICT",
                    "DYNAMIC_FLOW_MAPPING_SOURCE_SIGNATURE_CHANGED");

                var pinDrift = await ExecuteP707WithCatalogPinDriftAsync(
                    database,
                    scenario,
                    () => ApplyP707Async(
                        api,
                        scenario,
                        BuildP707ApplyRequest(
                            preview,
                            "p7-07-pin-drift"),
                        scenario.ActorToken,
                        ct),
                    ct);
                AssertP707Conflict(
                    pinDrift,
                    "P7-07 catalog pin drift",
                    "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                    "DYNAMIC_FLOW_MAPPING_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE");

                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-07-POLICY-PIN-DRIFT-ZERO-WRITE",
                        before,
                        ct));
                return new CaseObservation(
                    "Locked policy/hash drift invalidated preview parity, and catalog pin drift failed exact runtime identity; both mutations were restored with zero writes.",
                    P7MappingFingerprint(
                        "P7-07",
                        "policy-pin-drift",
                        ApiHarnessClient.FindStringRecursive(
                            policyDrift.Json,
                            "errorCode") ?? string.Empty,
                        ApiHarnessClient.FindStringRecursive(
                            pinDrift.Json,
                            "errorCode") ?? string.Empty));
            });

        await cases.RunAsync(
            "P7-07-TAMPERED-EXPIRED-TOKEN-NONLEAKING",
            async () =>
            {
                var preview = canonicalPreview
                              ?? throw new HarnessCaseNotRunnableException(
                                  "P7-07 canonical preview did not initialize.");
                var before = await CaptureP7MappingPersistenceStateAsync(
                    database,
                    scenario.TargetReportId,
                    ct);
                var token = ApiHarnessClient.RequiredString(
                    preview.Json,
                    "previewToken");
                var tamperedToken = TamperP707Token(token);
                var tampered = await ApplyP707Async(
                    api,
                    scenario,
                    BuildP707ApplyRequest(
                        preview,
                        "p7-07-tampered-token",
                        tamperedToken),
                    scenario.ActorToken,
                    ct);
                AssertP707TokenFailure(
                    tampered,
                    "P7-07 tampered token",
                    "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_INVALID",
                    DynamicFlowMappingSecurityContract
                        .PreviewTokenInvalidReason,
                    tamperedToken,
                    scenario);

                var claims = DecodeP707PreviewClaims(token);
                var expiredToken =
                    DynamicFlowMappingSecurityContract.IssuePreviewToken(
                        P707Binding(claims),
                        "p7-07-expired-token",
                        backend.DynamicFlowMappingPreviewSigningKey,
                        DateTime.UtcNow
                            .Subtract(
                                DynamicFlowMappingSecurityContract
                                    .PreviewTokenTtl)
                            .AddSeconds(-30));
                var expired = await ApplyP707Async(
                    api,
                    scenario,
                    BuildP707ApplyRequest(
                        preview,
                        "p7-07-expired-token",
                        expiredToken),
                    scenario.ActorToken,
                    ct);
                AssertP707TokenFailure(
                    expired,
                    "P7-07 expired token",
                    "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_EXPIRED",
                    DynamicFlowMappingSecurityContract
                        .PreviewTokenExpiredReason,
                    expiredToken,
                    scenario);

                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-07-TAMPERED-EXPIRED-TOKEN-NONLEAKING",
                        before,
                        ct));
                return new CaseObservation(
                    "A signature-bit tamper and an in-memory expired token returned their stable non-leaking HTTP 409 contracts before persistence.",
                    P7MappingFingerprint(
                        "P7-07",
                        "tamper-expiry",
                        ApiHarnessClient.FindStringRecursive(
                            tampered.Json,
                            "reason") ?? string.Empty,
                        ApiHarnessClient.FindStringRecursive(
                            expired.Json,
                            "reason") ?? string.Empty));
            });

        await cases.RunAsync(
            "P7-07-CROSS-ACTOR-TARGET-EPOCH-TOKEN-REUSE",
            async () =>
            {
                var preview = canonicalPreview
                              ?? throw new HarnessCaseNotRunnableException(
                                  "P7-07 canonical preview did not initialize.");
                var before = await CaptureP7MappingPersistenceStateAsync(
                    database,
                    scenario.TargetReportId,
                    ct);
                var token = ApiHarnessClient.RequiredString(
                    preview.Json,
                    "previewToken");
                var claims = DecodeP707PreviewClaims(token);

                var epochToken =
                    DynamicFlowMappingSecurityContract.IssuePreviewToken(
                        P707Binding(claims) with
                        {
                            ExecutionEpoch =
                                claims.ExecutionEpoch + 1
                        },
                        "p7-07-cross-epoch",
                        backend.DynamicFlowMappingPreviewSigningKey,
                        DateTime.UtcNow);
                var epochReuse = await ApplyP707Async(
                    api,
                    scenario,
                    BuildP707ApplyRequest(
                        preview,
                        "p7-07-cross-epoch",
                        epochToken),
                    scenario.ActorToken,
                    ct);
                AssertP707TokenFailure(
                    epochReuse,
                    "P7-07 cross-epoch token",
                    "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT",
                    "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT",
                    epochToken,
                    scenario);

                var actorReuse =
                    await ExecuteP707AsAlternateAuthorizedActorAsync(
                        api,
                        backend,
                        database,
                        scenario,
                        fixture.OutsiderUserId,
                        () => BuildP707ApplyRequest(
                            preview,
                            "p7-07-cross-actor"),
                        ct);
                AssertP707TokenFailure(
                    actorReuse,
                    "P7-07 cross-actor token",
                    "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT",
                    "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT",
                    token,
                    scenario);

                var secondWorkId = await CloneP7MappingWorkAsync(
                    database,
                    fixture.WorkId,
                    "P707-CROSS-TARGET",
                    ct);
                var secondScenario = await PrepareP7MappingScenarioAsync(
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    fixture,
                    secondWorkId,
                    "p7-07-cross-target-launch",
                    "2026-07-P707-CROSS-TARGET",
                    ct);
                Require(
                    secondScenario.ActorUserId == scenario.ActorUserId,
                    "P7-07 cross-target fixture did not retain the same authorized actor.");
                var secondPreview = await PreviewP7MappingAsync(
                    api,
                    secondScenario,
                    new JsonObject(),
                    ct);
                AssertP7MappingPreviewIdentity(
                    secondPreview,
                    fixture,
                    secondScenario,
                    activationThrough);
                var secondBefore =
                    await CaptureP7MappingPersistenceStateAsync(
                        database,
                        secondScenario.TargetReportId,
                        ct);
                var targetReuse = await ApplyP707Async(
                    api,
                    secondScenario,
                    BuildP707ApplyRequest(
                        secondPreview,
                        "p7-07-cross-target",
                        token),
                    secondScenario.ActorToken,
                    ct);
                AssertP707TokenFailure(
                    targetReuse,
                    "P7-07 cross-target token",
                    "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT",
                    "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT",
                    token,
                    secondScenario);

                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-07-CROSS-ACTOR-TARGET-EPOCH-TOKEN-REUSE-PRIMARY",
                        before,
                        ct));
                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        secondScenario.TargetReportId,
                        "P7-07-CROSS-ACTOR-TARGET-EPOCH-TOKEN-REUSE-SECONDARY",
                        secondBefore,
                        ct));
                return new CaseObservation(
                    "A valid signed token failed closed when reused across an authorized actor, an authorized target, or a different epoch, with no receipt or target mutation.",
                    P7MappingFingerprint(
                        "P7-07",
                        "cross-scope",
                        ApiHarnessClient.FindStringRecursive(
                            actorReuse.Json,
                            "reason") ?? string.Empty,
                        ApiHarnessClient.FindStringRecursive(
                            targetReuse.Json,
                            "reason") ?? string.Empty,
                        ApiHarnessClient.FindStringRecursive(
                            epochReuse.Json,
                            "reason") ?? string.Empty));
            });
    }

    private static Task<ApiHarnessResponse> ApplyP707Async(
        ApiHarnessClient api,
        P7MappingScenario scenario,
        JsonObject request,
        string actorToken,
        CancellationToken ct)
        => api.PostAsync(
            $"api/work-assignment-reports/{scenario.TargetReportId}/draft/apply-dynamic-flow-mapping",
            request,
            actorToken,
            ct: ct);

    private static JsonObject BuildP707ApplyRequest(
        ApiHarnessResponse preview,
        string commandLabel,
        string? previewToken = null)
        => new()
        {
            ["commandId"] =
                $"{commandLabel}-{ObjectId.GenerateNewId().ToString()[..8]}",
            ["expectedPayloadRevision"] =
                ApiHarnessClient.RequiredInt(
                    preview.Json,
                    "targetPayloadRevision"),
            ["expectedPayloadHash"] =
                ApiHarnessClient.RequiredString(
                    preview.Json,
                    "targetPayloadHash"),
            ["expectedLifecycleRevision"] =
                ApiHarnessClient.RequiredInt(
                    preview.Json,
                    "targetLifecycleRevision"),
            ["previewToken"] =
                previewToken ??
                ApiHarnessClient.RequiredString(
                    preview.Json,
                    "previewToken"),
            ["sourceSignature"] =
                ApiHarnessClient.RequiredString(
                    preview.Json,
                    "sourceSignature"),
            ["resultSemanticHash"] =
                ApiHarnessClient.RequiredString(
                    preview.Json,
                    "resultSemanticHash")
        };

    private static string P707ProjectionShape(JsonNode? response)
    {
        var root = response as JsonObject
                   ?? throw new InvalidOperationException(
                       "P7-07 preview response is missing.");
        return new JsonObject
        {
            ["fieldValuesJson"] =
                root["fieldValuesJson"]?.DeepClone(),
            ["tableValuesJson"] =
                root["tableValuesJson"]?.DeepClone(),
            ["changes"] = root["changes"]?.DeepClone(),
            ["sourceReports"] = root["sourceReports"]?.DeepClone(),
            ["hasBlockingConflicts"] =
                root["hasBlockingConflicts"]?.DeepClone(),
            ["mappingCapability"] =
                root["mappingCapability"]?.DeepClone(),
            ["mappingCapabilityReason"] =
                root["mappingCapabilityReason"]?.DeepClone()
        }.ToJsonString();
    }

    private static void AssertP707P708ActivationBarrier(
        ApiHarnessResponse response,
        string operation)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Conflict,
            operation);
        Require(
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "errorCode") ==
            "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE" &&
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "field") == "mappingSlice" &&
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "reason") ==
            "DYNAMIC_FLOW_MAPPING_APPLY_BLOCKED_UNTIL_P7_08" &&
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "blockedUntilPhase") == "P7-08",
            $"{operation} did not reach the exact P7-08 persistence barrier.");
    }

    private static void AssertP707Conflict(
        ApiHarnessResponse response,
        string operation,
        string errorCode,
        params string[] allowedReasons)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Conflict,
            operation);
        var reason = ApiHarnessClient.FindStringRecursive(
            response.Json,
            "reason");
        Require(
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "errorCode") == errorCode &&
            allowedReasons.Contains(reason, StringComparer.Ordinal),
            $"{operation} did not use the stable conflict contract; code=" +
            $"{ApiHarnessClient.FindStringRecursive(response.Json, "errorCode") ?? "<none>"};" +
            $"reason={reason ?? "<none>"}.");
    }

    private static void AssertP707TokenFailure(
        ApiHarnessResponse response,
        string operation,
        string errorCode,
        string reason,
        string forbiddenToken,
        P7MappingScenario scenario)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Conflict,
            operation);
        var actualCode = ApiHarnessClient.FindStringRecursive(
            response.Json,
            "errorCode");
        var actualReason = ApiHarnessClient.FindStringRecursive(
            response.Json,
            "reason");
        Require(
            string.Equals(actualCode, errorCode, StringComparison.Ordinal) &&
            string.Equals(actualReason, reason, StringComparison.Ordinal),
            $"{operation} did not use the stable non-leaking token contract; " +
            $"code={actualCode ?? "<none>"};reason={actualReason ?? "<none>"}.");
        Require(
            !response.Body.Contains(
                forbiddenToken,
                StringComparison.Ordinal) &&
            !response.Body.Contains(
                scenario.SourceReportId,
                StringComparison.OrdinalIgnoreCase) &&
            !response.Body.Contains(
                scenario.SourceAssignmentId,
                StringComparison.OrdinalIgnoreCase) &&
            !response.Body.Contains(
                P7MappingHiddenSourceValue,
                StringComparison.Ordinal),
            $"{operation} leaked token or canonical source identity/value.");
    }

    private static DynamicFlowMappingPreviewTokenClaims
        DecodeP707PreviewClaims(string token)
    {
        var segments = token.Split('.', StringSplitOptions.None);
        Require(
            segments.Length == 3 &&
            segments[0] ==
            DynamicFlowMappingSecurityContract.PreviewTokenVersion,
            "P7-07 server token format is invalid.");
        var segment = segments[1]
            .Replace('-', '+')
            .Replace('_', '/');
        segment = segment.PadRight(
            segment.Length + ((4 - segment.Length % 4) % 4),
            '=');
        var json = Encoding.UTF8.GetString(
            Convert.FromBase64String(segment));
        return JsonSerializer
                   .Deserialize<DynamicFlowMappingPreviewTokenClaims>(
                       json)
               ?? throw new InvalidOperationException(
                   "P7-07 token claims could not be decoded.");
    }

    private static DynamicFlowMappingPreviewTokenBinding P707Binding(
        DynamicFlowMappingPreviewTokenClaims claims)
        => new(
            claims.ActorId,
            claims.TargetReportId,
            claims.TargetAssignmentId,
            claims.FlowInstanceId,
            claims.ExecutionEpoch,
            claims.TargetPayloadRevision,
            claims.TargetLifecycleRevision,
            claims.SourceSignature,
            claims.ResultSemanticHash,
            claims.RuleSetHash,
            claims.AuthorizationScopeHash);

    private static string TamperP707Token(string token)
    {
        var segments = token.Split('.');
        var signature = segments[2].ToCharArray();
        signature[0] = signature[0] == 'A' ? 'B' : 'A';
        return $"{segments[0]}.{segments[1]}.{new string(signature)}";
    }

    private static async Task<T> ExecuteP707WithSourceCurrentAsync<T>(
        IMongoDatabase database,
        string sourceReportId,
        bool isCurrent,
        Func<Task<T>> operation,
        CancellationToken ct)
    {
        var reports = database.GetCollection<WorkAssignmentReport>(
            "work_assignment_report");
        var report = await reports
            .Find(item => item.Id == sourceReportId)
            .SingleAsync(ct);
        try
        {
            await reports.UpdateOneAsync(
                item => item.Id == sourceReportId,
                Builders<WorkAssignmentReport>.Update
                    .Set(item => item.IsCurrent, isCurrent),
                cancellationToken: ct);
            return await operation();
        }
        finally
        {
            await reports.UpdateOneAsync(
                item => item.Id == sourceReportId,
                Builders<WorkAssignmentReport>.Update
                    .Set(item => item.IsCurrent, report.IsCurrent),
                cancellationToken: ct);
        }
    }

    private static async Task<T>
        ExecuteP707WithTargetLifecycleRevisionAsync<T>(
            IMongoDatabase database,
            P7MappingScenario scenario,
            Func<Task<T>> operation,
            CancellationToken ct)
    {
        var reports = database.GetCollection<WorkAssignmentReport>(
            "work_assignment_report");
        var report = await reports
            .Find(item => item.Id == scenario.TargetReportId)
            .SingleAsync(ct);
        var steps = database.GetCollection<DynamicFlowStepInstance>(
            "dynamic_flow_step_instances");
        var step = await steps
            .Find(item => item.Id == scenario.TargetStepInstanceId)
            .SingleAsync(ct);
        try
        {
            await reports.UpdateOneAsync(
                item => item.Id == scenario.TargetReportId,
                Builders<WorkAssignmentReport>.Update
                    .Set(
                        item => item.LifecycleRevision,
                        report.LifecycleRevision + 1),
                cancellationToken: ct);
            await steps.UpdateOneAsync(
                item => item.Id == scenario.TargetStepInstanceId,
                Builders<DynamicFlowStepInstance>.Update
                    .Set(
                        item => item.ReportLifecycleRevision,
                        report.LifecycleRevision + 1),
                cancellationToken: ct);
            return await operation();
        }
        finally
        {
            await reports.UpdateOneAsync(
                item => item.Id == scenario.TargetReportId,
                Builders<WorkAssignmentReport>.Update
                    .Set(
                        item => item.LifecycleRevision,
                        report.LifecycleRevision),
                cancellationToken: ct);
            await steps.UpdateOneAsync(
                item => item.Id == scenario.TargetStepInstanceId,
                Builders<DynamicFlowStepInstance>.Update
                    .Set(
                        item => item.ReportLifecycleRevision,
                        step.ReportLifecycleRevision),
                cancellationToken: ct);
        }
    }

    private static async Task<T> ExecuteP707WithCatalogPinDriftAsync<T>(
        IMongoDatabase database,
        P7MappingScenario scenario,
        Func<Task<T>> operation,
        CancellationToken ct)
    {
        var instances = database.GetCollection<DynamicFlowInstance>(
            "dynamic_flow_instances");
        var instance = await instances
            .Find(item => item.Id == scenario.FlowInstanceId)
            .SingleAsync(ct);
        var driftHash =
            (instance.CatalogSemanticHash[0] == 'f' ? "e" : "f") +
            instance.CatalogSemanticHash[1..];
        try
        {
            await instances.UpdateOneAsync(
                item => item.Id == instance.Id,
                Builders<DynamicFlowInstance>.Update
                    .Set(item => item.CatalogSemanticHash, driftHash),
                cancellationToken: ct);
            return await operation();
        }
        finally
        {
            await instances.UpdateOneAsync(
                item => item.Id == instance.Id,
                Builders<DynamicFlowInstance>.Update
                    .Set(
                        item => item.CatalogSemanticHash,
                        instance.CatalogSemanticHash),
                cancellationToken: ct);
        }
    }

    private static async Task<ApiHarnessResponse>
        ExecuteP707AsAlternateAuthorizedActorAsync(
            ApiHarnessClient api,
            BackendServerLease backend,
            IMongoDatabase database,
            P7MappingScenario scenario,
            string alternateActorUserId,
            Func<JsonObject> requestFactory,
            CancellationToken ct)
    {
        var snapshots = await CaptureP707ReassignmentDocumentsAsync(
            database,
            scenario,
            ct);
        try
        {
            var targetStep = await database
                .GetCollection<DynamicFlowStepInstance>(
                    "dynamic_flow_step_instances")
                .Find(item =>
                    item.Id == scenario.TargetStepInstanceId)
                .SingleAsync(ct);
            await ReassignP7MappingTargetAsync(
                database,
                targetStep,
                alternateActorUserId,
                ct);
            await database.GetCollection<WorkAssignmentReport>(
                    "work_assignment_report")
                .UpdateOneAsync(
                    item => item.Id == scenario.TargetReportId,
                    Builders<WorkAssignmentReport>.Update
                        .Set(
                            item => item.AssigneeUserId,
                            alternateActorUserId),
                    cancellationToken: ct);
            var alternateToken = await PrepareP601ActorLoginAsync(
                api,
                backend,
                database,
                alternateActorUserId,
                ct);
            return await ApplyP707Async(
                api,
                scenario,
                requestFactory(),
                alternateToken,
                ct);
        }
        finally
        {
            await RestoreP707ReassignmentDocumentsAsync(
                database,
                snapshots,
                ct);
        }
    }

    private static async Task<List<P707MongoDocumentSnapshot>>
        CaptureP707ReassignmentDocumentsAsync(
            IMongoDatabase database,
            P7MappingScenario scenario,
            CancellationToken ct)
    {
        var result = new List<P707MongoDocumentSnapshot>();
        async Task CaptureAsync(
            string collectionName,
            BsonDocument filter)
        {
            var documents = await database
                .GetCollection<BsonDocument>(collectionName)
                .Find(filter)
                .ToListAsync(ct);
            result.AddRange(
                documents.Select(document =>
                    new P707MongoDocumentSnapshot(
                        collectionName,
                        document.DeepClone().AsBsonDocument)));
        }

        await CaptureAsync(
            "work_assignments",
            new BsonDocument(
                "_id",
                ObjectId.Parse(scenario.TargetAssignmentId)));
        await CaptureAsync(
            "work_template_assignees",
            new BsonDocument(
                "workAssignmentId",
                ObjectId.Parse(scenario.TargetAssignmentId)));
        await CaptureAsync(
            "assignment_list_doc_roles",
            new BsonDocument(
                "assignmentId",
                ObjectId.Parse(scenario.TargetAssignmentId)));
        await CaptureAsync(
            "work_report_periods",
            new BsonDocument(
                "workAssignmentId",
                ObjectId.Parse(scenario.TargetAssignmentId)));
        await CaptureAsync(
            "dynamic_flow_step_instances",
            new BsonDocument(
                "_id",
                ObjectId.Parse(scenario.TargetStepInstanceId)));
        await CaptureAsync(
            "work_assignment_report",
            new BsonDocument(
                "_id",
                ObjectId.Parse(scenario.TargetReportId)));
        return result;
    }

    private static async Task RestoreP707ReassignmentDocumentsAsync(
        IMongoDatabase database,
        IReadOnlyCollection<P707MongoDocumentSnapshot> snapshots,
        CancellationToken ct)
    {
        foreach (var snapshot in snapshots)
        {
            await database
                .GetCollection<BsonDocument>(snapshot.CollectionName)
                .ReplaceOneAsync(
                    new BsonDocument(
                        "_id",
                        snapshot.Document["_id"]),
                    snapshot.Document,
                    cancellationToken: ct);
        }
    }

    private sealed record P707MongoDocumentSnapshot(
        string CollectionName,
        BsonDocument Document);
}
