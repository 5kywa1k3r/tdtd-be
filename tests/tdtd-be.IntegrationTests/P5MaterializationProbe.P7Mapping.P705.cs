using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports;

namespace tdtd_be.IntegrationTests;

internal static partial class P5MaterializationProbe
{
    private const string P705FlowOwnedConfigurationReason =
        "DYNAMIC_FLOW_MAPPING_CONFIG_MUST_BE_FLOW_OWNED";

    private static async Task RunP705MappingCasesExpandedAsync(
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
            "P705-AUTH",
            ct);
        var scenario = await PrepareP7MappingScenarioAsync(
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            workId,
            "p7-05-auth-launch",
            "2026-07-P705-AUTH",
            ct);

        await ReassignP7MappingSourceForRedactionAsync(
            database,
            scenario,
            fixture.OutsiderUserId,
            ct);
        var sourceOnlyToken = await PrepareP601ActorLoginAsync(
            api,
            backend,
            database,
            fixture.OutsiderUserId,
            ct);

        var users = database.GetCollection<AppUser>("users");
        var admin = await users
            .Find(user =>
                user.Username == "admin" &&
                !user.IsDeleted)
            .SingleAsync(ct);
        var unrelatedActor = NewRuntimeReadActor(
            $"p7_05_unrelated_{ObjectId.GenerateNewId().ToString()[..8]}",
            "P7-05 Unrelated Actor",
            baseFixture.TargetUnitIds[2],
            admin.Id,
            DateTime.UtcNow);
        await users.InsertOneAsync(unrelatedActor, cancellationToken: ct);
        var unrelatedToken = await PrepareP601ActorLoginAsync(
            api,
            backend,
            database,
            unrelatedActor.Id,
            ct);

        var rawSentinel =
            $"P7_RAW_SOURCE_{Convert.ToHexString(RandomNumberGenerator.GetBytes(18))}";
        await database.GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .UpdateOneAsync(
                report =>
                    report.Id == scenario.SourceReportId &&
                    !report.IsDeleted,
                Builders<WorkAssignmentReport>.Update
                    .Set(report => report.ReviewerComment, rawSentinel),
                cancellationToken: ct);

        ApiHarnessResponse? canonicalPreview = null;
        await cases.RunAsync(
            "P7-05-SERVER-DERIVED-ROLE-REDACTED-PROVENANCE",
            async () =>
            {
                await AssertP705ServerDerivedAssigneeFixtureAsync(
                    database,
                    scenario,
                    fixture,
                    ct);
                var before =
                    await CaptureP7MappingPersistenceStateAsync(
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
                AssertP7MappingCallerRedaction(canonicalPreview);
                AssertP705CallerPreviewDoesNotExposeSource(
                    canonicalPreview,
                    scenario,
                    fixture,
                    rawSentinel);
                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-05-SERVER-DERIVED-ROLE-REDACTED-PROVENANCE",
                        before,
                        ct));
                mongoEvidence.Add(
                    await CaptureP705InternalSourceFactEvidenceAsync(
                        database,
                        scenario,
                        canonicalPreview,
                        ct));
                return new CaseObservation(
                    "An empty caller request resolved ASSIGNEE from the canonical target assignment, exact-role allow beat wildcard deny, caller provenance was fully redacted, and the internal source-fact signature remained reproducible.",
                    P7MappingFingerprint(
                        "P7-05",
                        "server-role-redaction",
                        ApiHarnessClient.RequiredString(
                            canonicalPreview.Json,
                            "sourceSignature")));
            });

        await cases.RunAsync(
            "P7-05-RAW-REPORT-EXPLICIT-ACL-MATRIX",
            async () =>
            {
                var targetBefore =
                    await CaptureP7MappingPersistenceStateAsync(
                        database,
                        scenario.TargetReportId,
                        ct);
                var sourceBefore =
                    await CaptureP7MappingPersistenceStateAsync(
                        database,
                        scenario.SourceReportId,
                        ct);

                var targetOwn = await api.GetAsync(
                    $"api/work-assignment-reports/{scenario.TargetReportId}",
                    scenario.ActorToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    targetOwn,
                    HttpStatusCode.OK,
                    "P7-05 target actor raw target GET");
                Require(
                    !targetOwn.Body.Contains(
                        rawSentinel,
                        StringComparison.Ordinal),
                    "P7-05 target raw report unexpectedly contained source-only metadata.");

                var sourceOwn = await api.GetAsync(
                    $"api/work-assignment-reports/{scenario.SourceReportId}",
                    sourceOnlyToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    sourceOwn,
                    HttpStatusCode.OK,
                    "P7-05 source actor raw source GET");
                Require(
                    sourceOwn.Body.Contains(
                        rawSentinel,
                        StringComparison.Ordinal),
                    "P7-05 explicitly authorized source actor did not receive its raw source metadata.");

                var targetOnlySource = await api.GetAsync(
                    $"api/work-assignment-reports/{scenario.SourceReportId}",
                    scenario.ActorToken,
                    ct: ct);
                AssertP705GenericReportForbidden(
                    targetOnlySource,
                    "P7-05 target-only actor raw source GET",
                    scenario.SourceReportId,
                    rawSentinel);

                var sourceOnlyTarget = await api.GetAsync(
                    $"api/work-assignment-reports/{scenario.TargetReportId}",
                    sourceOnlyToken,
                    ct: ct);
                AssertP705GenericReportForbidden(
                    sourceOnlyTarget,
                    "P7-05 source-only actor raw target GET",
                    scenario.TargetReportId,
                    rawSentinel);

                var unrelatedSource = await api.GetAsync(
                    $"api/work-assignment-reports/{scenario.SourceReportId}",
                    unrelatedToken,
                    ct: ct);
                AssertP705GenericReportForbidden(
                    unrelatedSource,
                    "P7-05 unrelated actor raw source GET",
                    scenario.SourceReportId,
                    rawSentinel);
                var unrelatedTarget = await api.GetAsync(
                    $"api/work-assignment-reports/{scenario.TargetReportId}",
                    unrelatedToken,
                    ct: ct);
                AssertP705GenericReportForbidden(
                    unrelatedTarget,
                    "P7-05 unrelated actor raw target GET",
                    scenario.TargetReportId,
                    rawSentinel);

                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-05-RAW-REPORT-EXPLICIT-ACL-MATRIX-TARGET",
                        targetBefore,
                        ct));
                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.SourceReportId,
                        "P7-05-RAW-REPORT-EXPLICIT-ACL-MATRIX-SOURCE",
                        sourceBefore,
                        ct));
                mongoEvidence.Add(new
                {
                    caseId = "P7-05-RAW-REPORT-EXPLICIT-ACL-MATRIX-OUTCOME",
                    targetActorTargetStatus = (int)targetOwn.StatusCode,
                    sourceActorSourceStatus = (int)sourceOwn.StatusCode,
                    targetActorSourceStatus =
                        (int)targetOnlySource.StatusCode,
                    sourceActorTargetStatus =
                        (int)sourceOnlyTarget.StatusCode,
                    unrelatedSourceStatus =
                        (int)unrelatedSource.StatusCode,
                    unrelatedTargetStatus =
                        (int)unrelatedTarget.StatusCode,
                    authorizedRawValueObserved = true,
                    rawPayloadRecorded = false
                });
                return new CaseObservation(
                    "Direct raw-report permission remained independent: each report assignee received only its own raw report, while target-only, source-only cross-access, and unrelated access received generic 403 with zero writes.",
                    P7MappingFingerprint(
                        "P7-05",
                        "raw-acl-matrix",
                        scenario.TargetReportId,
                        scenario.SourceReportId));
            });

        await cases.RunAsync(
            "P7-05-HIDDEN-MISSING-SAME-SHAPE-403",
            async () =>
            {
                var before =
                    await CaptureP7MappingPersistenceStateAsync(
                        database,
                        scenario.TargetReportId,
                        ct);
                var hidden = await api.GetAsync(
                    $"api/work-assignment-reports/{scenario.SourceReportId}",
                    scenario.ActorToken,
                    ct: ct);
                var missingId = ObjectId.GenerateNewId().ToString();
                var missing = await api.GetAsync(
                    $"api/work-assignment-reports/{missingId}",
                    scenario.ActorToken,
                    ct: ct);
                AssertP705GenericReportForbidden(
                    hidden,
                    "P7-05 hidden raw report",
                    scenario.SourceReportId,
                    rawSentinel);
                AssertP705GenericReportForbidden(
                    missing,
                    "P7-05 missing raw report",
                    missingId,
                    rawSentinel);
                Require(
                    NormalizeP705ErrorEnvelope(hidden.Json) ==
                    NormalizeP705ErrorEnvelope(missing.Json),
                    "P7-05 hidden and missing raw reports did not return the same error shape excluding traceId.");
                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-05-HIDDEN-MISSING-SAME-SHAPE-403",
                        before,
                        ct));
                return new CaseObservation(
                    "A hidden existing source report and a valid missing report returned the same generic HTTP 403 envelope except traceId and disclosed no identifiers or raw metadata.",
                    P7MappingFingerprint(
                        "P7-05",
                        "hidden-missing",
                        NormalizeP705ErrorEnvelope(hidden.Json)));
            });

        await cases.RunAsync(
            "P7-05-OUTSIDER-TARGET-PREVIEW-NON-ENUMERATION",
            async () =>
            {
                var before =
                    await CaptureP7MappingPersistenceStateAsync(
                        database,
                        scenario.TargetReportId,
                        ct);
                var sourceOnly = await api.PostAsync(
                    $"api/work-assignment-reports/{scenario.TargetReportId}/draft/preview-dynamic-flow-mapping",
                    new JsonObject(),
                    sourceOnlyToken,
                    ct: ct);
                var unrelated = await api.PostAsync(
                    $"api/work-assignment-reports/{scenario.TargetReportId}/draft/preview-dynamic-flow-mapping",
                    new JsonObject(),
                    unrelatedToken,
                    ct: ct);
                var missingId = ObjectId.GenerateNewId().ToString();
                var missing = await api.PostAsync(
                    $"api/work-assignment-reports/{missingId}/draft/preview-dynamic-flow-mapping",
                    new JsonObject(),
                    unrelatedToken,
                    ct: ct);
                AssertP705GenericReportForbidden(
                    sourceOnly,
                    "P7-05 source-only target preview",
                    scenario.TargetReportId,
                    rawSentinel);
                AssertP705GenericReportForbidden(
                    unrelated,
                    "P7-05 unrelated target preview",
                    scenario.TargetReportId,
                    rawSentinel);
                AssertP705GenericReportForbidden(
                    missing,
                    "P7-05 unrelated missing preview",
                    missingId,
                    rawSentinel);
                var expectedShape =
                    NormalizeP705ErrorEnvelope(sourceOnly.Json);
                Require(
                    NormalizeP705ErrorEnvelope(unrelated.Json) ==
                    expectedShape &&
                    NormalizeP705ErrorEnvelope(missing.Json) ==
                    expectedShape,
                    "P7-05 hidden and missing preview targets exposed distinguishable error shapes.");
                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-05-OUTSIDER-TARGET-PREVIEW-NON-ENUMERATION",
                        before,
                        ct));
                return new CaseObservation(
                    "Source-only and unrelated actors could not use mapping preview as a target-report oracle; hidden and missing targets shared the generic report 403 envelope.",
                    P7MappingFingerprint(
                        "P7-05",
                        "preview-non-enumeration",
                        expectedShape));
            });

        await cases.RunAsync(
            "P7-05-FLOW-OWNED-CONFIG-FORGERIES",
            async () =>
            {
                var before =
                    await CaptureP7MappingPersistenceStateAsync(
                        database,
                        scenario.TargetReportId,
                        ct);
                var forgedObjectId =
                    ObjectId.GenerateNewId().ToString();
                var forgeries = new (string Field, JsonNode Value)[]
                {
                    ("sourceMode", JsonValue.Create("EXPLICIT")!),
                    (
                        "sourceReportIds",
                        new JsonArray(
                            JsonValue.Create(forgedObjectId))),
                    (
                        "flowTemplateVersionId",
                        JsonValue.Create(forgedObjectId)!),
                    (
                        "flowTemplateId",
                        JsonValue.Create(forgedObjectId)!),
                    ("flowTemplateVersionNo", JsonValue.Create(777)!),
                    ("mappingRulesJson", JsonValue.Create("[]")!),
                    ("mappingRules", new JsonArray()),
                    ("conflictPolicy", JsonValue.Create("KEEP_TARGET")!),
                    (
                        "contributionPolicy",
                        JsonValue.Create("EXCLUDE")!),
                    ("requireSourceReport", JsonValue.Create(false)!)
                };
                var outcomes = new List<object>();
                foreach (var forgery in forgeries)
                {
                    var response = await PreviewP7MappingAsync(
                        api,
                        scenario,
                        new JsonObject
                        {
                            [forgery.Field] =
                                forgery.Value.DeepClone()
                        },
                        ct);
                    AssertP705FlowOwnedForgeryRejected(
                        response,
                        forgery.Field,
                        rawSentinel);
                    outcomes.Add(new
                    {
                        field = forgery.Field,
                        statusCode = (int)response.StatusCode,
                        reason = P705FlowOwnedConfigurationReason,
                        valueRecorded = false
                    });
                }
                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-05-FLOW-OWNED-CONFIG-FORGERIES",
                        before,
                        ct));
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-05-FLOW-OWNED-CONFIG-FORGERIES-OUTCOME",
                    outcomes,
                    rawPayloadRecorded = false
                });
                return new CaseObservation(
                    "Each legacy caller-owned source, locked-flow, rule, conflict, contribution, and source-required selector failed independently with the stable flow-owned-configuration contract.",
                    P7MappingFingerprint(
                        "P7-05",
                        "flow-owned-config",
                        string.Join(
                            ",",
                            forgeries.Select(item => item.Field))));
            });

        await cases.RunAsync(
            "P7-05-AUTHORITY-PROVENANCE-UNKNOWN-FORGERIES",
            async () =>
            {
                var before =
                    await CaptureP7MappingPersistenceStateAsync(
                        database,
                        scenario.TargetReportId,
                        ct);
                var forgeries = new (string Field, JsonNode Value)[]
                {
                    ("actorRole", JsonValue.Create("ISSUER")!),
                    (
                        "provenance",
                        new JsonObject
                        {
                            ["authority"] = "caller"
                        }),
                    ("changes", new JsonArray()),
                    ("sourceReports", new JsonArray()),
                    (
                        "forgedFutureSelector",
                        new JsonObject
                        {
                            ["enabled"] = true
                        })
                };
                var outcomes = new List<object>();
                foreach (var forgery in forgeries)
                {
                    var response = await PreviewP7MappingAsync(
                        api,
                        scenario,
                        new JsonObject
                        {
                            [forgery.Field] =
                                forgery.Value.DeepClone()
                        },
                        ct);
                    AssertP705FlowOwnedForgeryRejected(
                        response,
                        forgery.Field,
                        rawSentinel);
                    outcomes.Add(new
                    {
                        field = forgery.Field,
                        statusCode = (int)response.StatusCode,
                        reason = P705FlowOwnedConfigurationReason,
                        valueRecorded = false
                    });
                }
                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-05-AUTHORITY-PROVENANCE-UNKNOWN-FORGERIES",
                        before,
                        ct));
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-05-AUTHORITY-PROVENANCE-UNKNOWN-FORGERIES-OUTCOME",
                    outcomes,
                    rawPayloadRecorded = false
                });
                return new CaseObservation(
                    "Caller role, provenance, response-shaped changes/sourceReports, and an unknown future selector were rejected independently before runtime resolution.",
                    P7MappingFingerprint(
                        "P7-05",
                        "authority-forgeries",
                        string.Join(
                            ",",
                            forgeries.Select(item => item.Field))));
            });

        await cases.RunAsync(
            "P7-05-SOURCE-POLICY-DENY-BEFORE-HYDRATION",
            async () =>
            {
                var before =
                    await CaptureP7MappingPersistenceStateAsync(
                        database,
                        scenario.TargetReportId,
                        ct);
                var response =
                    await ExecuteP705SourcePolicyDenialWithPoisonedPayloadAsync(
                        api,
                        database,
                        scenario,
                        ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.Forbidden,
                    "P7-05 source read=false before hydration");
                Require(
                    ApiHarnessClient.FindStringRecursive(
                        response.Json,
                        "errorCode") ==
                    "DYNAMIC_FLOW_MAPPING_SOURCE_FORBIDDEN",
                    "P7-05 source policy denial used the wrong error code.");
                Require(
                    ApiHarnessClient.FindStringRecursive(
                        response.Json,
                        "reason") ==
                    "DYNAMIC_FLOW_MAPPING_SOURCE_FIELD_READ_FORBIDDEN",
                    "P7-05 source policy denial used the wrong reason.");
                Require(
                    !response.Body.Contains(
                        scenario.SourceReportId,
                        StringComparison.OrdinalIgnoreCase) &&
                    !response.Body.Contains(
                        scenario.SourceAssignmentId,
                        StringComparison.OrdinalIgnoreCase) &&
                    !response.Body.Contains(
                        rawSentinel,
                        StringComparison.Ordinal),
                    "P7-05 source policy denial leaked source identity or raw metadata.");
                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-05-SOURCE-POLICY-DENY-BEFORE-HYDRATION",
                        before,
                        ct));
                return new CaseObservation(
                    "An exact ASSIGNEE source read=false policy returned the stable source-forbidden reason even while the external payload hash was deliberately unreadable, proving denial preceded hydration.",
                    P7MappingFingerprint(
                        "P7-05",
                        "policy-before-hydration",
                        ApiHarnessClient.RequiredString(
                            response.Json,
                            "errorCode")));
            });

        await cases.RunAsync(
            "P7-05-LATER-SLICES-ACTIVATION-BARRIER",
            async () =>
            {
                var preview = canonicalPreview
                              ?? throw new HarnessCaseNotRunnableException(
                                  "P7-05 canonical preview did not initialize.");
                var before =
                    await CaptureP7MappingPersistenceStateAsync(
                        database,
                        scenario.TargetReportId,
                        ct);
                if (activationThrough < 7)
                {
                    var blocked = await api.PostAsync(
                        $"api/work-assignment-reports/{scenario.TargetReportId}/draft/apply-dynamic-flow-mapping",
                        new JsonObject
                        {
                            ["actorRole"] = "ISSUER"
                        },
                        scenario.ActorToken,
                        ct: ct);
                    var missingId =
                        ObjectId.GenerateNewId().ToString();
                    var blockedMissing = await api.PostAsync(
                        $"api/work-assignment-reports/{missingId}/draft/apply-dynamic-flow-mapping",
                        new JsonObject(),
                        unrelatedToken,
                        ct: ct);
                    AssertP705ApplyActivationBarrier(
                        blocked,
                        scenario,
                        rawSentinel);
                    AssertP705ApplyActivationBarrier(
                        blockedMissing,
                        scenario,
                        rawSentinel);
                    Require(
                        NormalizeP705ErrorEnvelope(blocked.Json) ==
                        NormalizeP705ErrorEnvelope(
                            blockedMissing.Json),
                        "P7-05 blocked apply exposed a target existence or actor oracle.");
                }
                else if (activationThrough < 8)
                {
                    var preflight = await api.PostAsync(
                        $"api/work-assignment-reports/{scenario.TargetReportId}/draft/apply-dynamic-flow-mapping",
                        BuildP707ApplyRequest(
                            preview,
                            "p7-05-cumulative-preflight"),
                        scenario.ActorToken,
                        ct: ct);
                    AssertP705ApplyActivationBarrier(
                        preflight,
                        scenario,
                        rawSentinel);
                }
                else
                {
                    Require(
                        preview.Json?["previewToken"] is not null,
                        "P7 cumulative activation >= P7-08 lacks the later preview-token slice.");
                }

                mongoEvidence.Add(
                    await CaptureP7MappingNegativeMongoEvidenceAsync(
                        database,
                        scenario.TargetReportId,
                        "P7-05-LATER-SLICES-ACTIVATION-BARRIER",
                        before,
                        ct));
                mongoEvidence.Add(new
                {
                    caseId =
                        "P7-05-LATER-SLICES-ACTIVATION-BARRIER-OUTCOME",
                    activationThrough,
                    previewTokenPresent =
                        preview.Json?["previewToken"] is not null,
                    canApply =
                        ApiHarnessClient.RequiredBool(
                            preview.Json,
                            "canApply"),
                    applyBarrierExpected = activationThrough < 8,
                    rawPayloadRecorded = false
                });
                return new CaseObservation(
                    activationThrough < 8
                        ? activationThrough < 7
                            ? "The signed P7-05 slice exposed preview only and returned the generic P7-08 barrier before request, target, receipt, or ACL resolution."
                            : "The cumulative P7-07 slice accepted the exact signed preflight and still stopped at the generic P7-08 persistence barrier."
                        : "The cumulative runner observed that the later token/apply slice had been activated by its owning prompt; the exact P7-05 barrier remains proven by the signed P7-05 artifact.",
                    P7MappingFingerprint(
                        "P7-05",
                        "activation-barrier",
                        activationThrough.ToString()));
            });
    }

    private static async Task AssertP705ServerDerivedAssigneeFixtureAsync(
        IMongoDatabase database,
        P7MappingScenario scenario,
        P7MappingFixture fixture,
        CancellationToken ct)
    {
        var assignment = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .Find(item => item.Id == scenario.TargetAssignmentId)
            .SingleAsync(ct);
        var report = await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .Find(item => item.Id == scenario.TargetReportId)
            .SingleAsync(ct);
        Require(
            WorkAssignmentReportService.ResolveDirectDynamicFlowActorRole(
                assignment,
                report,
                scenario.ActorUserId) == "ASSIGNEE",
            "P7-05 target actor role was not derived as ASSIGNEE from canonical runtime state.");

        var version = await database
            .GetCollection<DynamicFlowTemplateVersion>(
                "dynamic_flow_template_versions")
            .Find(item => item.Id == fixture.VersionId)
            .SingleAsync(ct);
        var payload = JsonNode.Parse(version.PayloadJson)?.AsObject()
                      ?? throw new InvalidOperationException(
                          "P7-05 locked flow payload is invalid JSON.");
        var policies = payload["fieldPolicies"] as JsonArray
                       ?? throw new InvalidOperationException(
                           "P7-05 locked flow has no field policies.");
        var wildcard = policies
            .OfType<JsonObject>()
            .Single(policy =>
                policy["policyId"]?.GetValue<string>() ==
                "p7-target-child-wildcard-deny");
        var exact = policies
            .OfType<JsonObject>()
            .Single(policy =>
                policy["policyId"]?.GetValue<string>() ==
                "p7-target-child-assignee-allow");
        Require(
            wildcard["actorRole"]?.GetValue<string>() == "*" &&
            wildcard["write"]?.GetValue<bool>() == false &&
            exact["actorRole"]?.GetValue<string>() == "ASSIGNEE" &&
            exact["write"]?.GetValue<bool>() == true,
            "P7-05 exact ASSIGNEE allow versus wildcard deny fixture drifted.");
    }

    private static void AssertP705CallerPreviewDoesNotExposeSource(
        ApiHarnessResponse preview,
        P7MappingScenario scenario,
        P7MappingFixture fixture,
        string rawSentinel)
    {
        var forbiddenValues = new[]
        {
            scenario.SourceReportId,
            scenario.SourceAssignmentId,
            scenario.SourceStepInstanceId,
            fixture.RootForm.FormVersionId,
            fixture.RootForm.FormSchemaHash,
            rawSentinel
        };
        foreach (var forbiddenValue in forbiddenValues)
        {
            Require(
                !preview.Body.Contains(
                    forbiddenValue,
                    StringComparison.OrdinalIgnoreCase),
                "P7-05 caller preview exposed a source-only identity, form pin, hash, or raw metadata value.");
        }
    }

    private static async Task<object>
        CaptureP705InternalSourceFactEvidenceAsync(
            IMongoDatabase database,
            P7MappingScenario scenario,
            ApiHarnessResponse preview,
            CancellationToken ct)
    {
        var version = await database
            .GetCollection<DynamicFlowTemplateVersion>(
                "dynamic_flow_template_versions")
            .Find(item =>
                item.Id ==
                ApiHarnessClient.RequiredString(
                    preview.Json,
                    "flowVersionId"))
            .SingleAsync(ct);
        var instance = await database
            .GetCollection<DynamicFlowInstance>(
                "dynamic_flow_instances")
            .Find(item => item.Id == scenario.FlowInstanceId)
            .SingleAsync(ct);
        var steps = database.GetCollection<DynamicFlowStepInstance>(
            "dynamic_flow_step_instances");
        var targetStep = await steps
            .Find(item => item.Id == scenario.TargetStepInstanceId)
            .SingleAsync(ct);
        var sourceStep = await steps
            .Find(item => item.Id == scenario.SourceStepInstanceId)
            .SingleAsync(ct);
        var reports = database.GetCollection<WorkAssignmentReport>(
            "work_assignment_report");
        var targetReport = await reports
            .Find(item => item.Id == scenario.TargetReportId)
            .SingleAsync(ct);
        var sourceReport = await reports
            .Find(item => item.Id == scenario.SourceReportId)
            .SingleAsync(ct);
        var sourceAssignment = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .Find(item => item.Id == scenario.SourceAssignmentId)
            .SingleAsync(ct);
        var rules =
            DynamicFlowMappingEngine.ReadRulesFromPayloadJson(
                version.PayloadJson);
        DynamicFlowMappingEngine.ValidateP7Rules(rules);
        var ruleSetHash =
            DynamicFlowMappingRuntimeContract.ComputeRuleSetHash(
                rules);
        var runtime = new DynamicFlowMappingRuntimeContext(
            version,
            instance,
            targetStep,
            rules,
            ruleSetHash);
        var sources = new[]
        {
            new DynamicFlowMappingSourceReport(
                sourceReport,
                sourceStep.FlowStepId,
                sourceStep.FlowStepCode,
                null,
                null,
                instance,
                sourceStep,
                sourceAssignment)
        };
        var computed =
            DynamicFlowMappingRuntimeContract.ComputeSourceSignature(
                runtime,
                targetReport,
                sources);
        var returned = ApiHarnessClient.RequiredString(
            preview.Json,
            "sourceSignature");
        Require(
            computed == returned &&
            ruleSetHash ==
            ApiHarnessClient.RequiredString(
                preview.Json,
                "mappingRuleSetHash"),
            "P7-05 redacted caller response did not retain signature parity with safe internal source facts.");
        return new
        {
            caseId =
                "P7-05-INTERNAL-SOURCE-FACT-SIGNATURE-PARITY",
            signatureVersion =
                DynamicFlowMappingSecurityContract
                    .SourceSignatureVersion,
            sourceSignature = computed,
            ruleSetHash,
            flowInstanceId = instance.Id,
            executionEpoch = sourceStep.ExecutionEpoch,
            source = new
            {
                assignmentId = sourceReport.WorkAssignmentId,
                reportId = sourceReport.Id,
                sourceReport.PayloadRevision,
                sourceReport.PayloadHash,
                sourceReport.LifecycleRevision,
                lifecycleStatus =
                    sourceReport.Status.ToString().ToUpperInvariant(),
                sourceReport.IsActive,
                sourceReport.IsCurrent,
                sourceReport.PeriodInstanceKey,
                stepInstanceId = sourceStep.Id,
                stepId = sourceStep.FlowStepId,
                sourceStep.BranchId,
                sourceStep.AttemptNo,
                sourceStep.FormFamilyId,
                sourceStep.FormVersionId,
                sourceStep.FormVersionNo,
                sourceStep.FormSchemaHash,
                sourceStep.FormSnapshotHash
            },
            signatureParity = true,
            rawPayloadRecorded = false,
            rawMetadataRecorded = false
        };
    }

    private static void AssertP705GenericReportForbidden(
        ApiHarnessResponse response,
        string operation,
        string forbiddenId,
        string rawSentinel)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Forbidden,
            operation);
        Require(
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "errorCode") ==
            "WORK_ASSIGNMENT_REPORT_ACCESS_FORBIDDEN",
            $"{operation} did not use the generic report access code.");
        Require(
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "service") == "REPORT",
            $"{operation} did not use the REPORT service envelope.");
        var root = response.Json as JsonObject
                   ?? throw new InvalidOperationException(
                       $"{operation} did not return an object envelope.");
        Require(
            root.ContainsKey("details") &&
            root["details"] is null,
            $"{operation} exposed non-null error details.");
        Require(
            !response.Body.Contains(
                forbiddenId,
                StringComparison.OrdinalIgnoreCase) &&
            !response.Body.Contains(
                rawSentinel,
                StringComparison.Ordinal),
            $"{operation} leaked a report identifier or raw metadata.");
    }

    private static string NormalizeP705ErrorEnvelope(JsonNode? response)
    {
        var clone = response?.DeepClone() as JsonObject
                    ?? throw new InvalidOperationException(
                        "P7-05 error envelope is missing.");
        clone.Remove("traceId");
        return clone.ToJsonString();
    }

    private static void AssertP705FlowOwnedForgeryRejected(
        ApiHarnessResponse response,
        string field,
        string rawSentinel)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.BadRequest,
            $"P7-05 forged {field}");
        Require(
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "errorCode") ==
            "COMMON_VALIDATION_FAILED" &&
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "field") == field &&
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "reason") ==
            P705FlowOwnedConfigurationReason,
            $"P7-05 forged {field} did not return the exact flow-owned configuration contract.");
        Require(
            !response.Body.Contains(
                rawSentinel,
                StringComparison.Ordinal),
            $"P7-05 forged {field} error leaked raw source metadata.");
    }

    private static async Task<ApiHarnessResponse>
        ExecuteP705SourcePolicyDenialWithPoisonedPayloadAsync(
            ApiHarnessClient api,
            IMongoDatabase database,
            P7MappingScenario scenario,
            CancellationToken ct)
    {
        var versions = database
            .GetCollection<DynamicFlowTemplateVersion>(
                "dynamic_flow_template_versions");
        var instanceCollection = database
            .GetCollection<DynamicFlowInstance>(
                "dynamic_flow_instances");
        var familyCollection = database
            .GetCollection<DynamicFlowTemplate>(
                "dynamic_flow_templates");
        var instance = await instanceCollection
            .Find(item => item.Id == scenario.FlowInstanceId)
            .SingleAsync(ct);
        var version = await versions
            .Find(item =>
                item.Id == instance.FlowTemplateVersionId)
            .SingleAsync(ct);
        var family = await familyCollection
            .Find(item => item.Id == instance.FlowTemplateId)
            .SingleAsync(ct);
        var payload = JsonNode.Parse(version.PayloadJson)?.AsObject()
                      ?? throw new InvalidOperationException(
                          "P7-05 source-deny payload is invalid JSON.");
        var policies = payload["fieldPolicies"] as JsonArray
                       ?? throw new InvalidOperationException(
                           "P7-05 source-deny payload has no field policies.");
        var exactSource = policies
            .OfType<JsonObject>()
            .Single(policy =>
                policy["policyId"]?.GetValue<string>() ==
                "p7-source-note-assignee-allow");
        exactSource["read"] = false;
        var canonical =
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                payload.ToJsonString(),
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true));
        DynamicFlowMappingEngine.ValidateP7Rules(
            DynamicFlowMappingEngine.ReadRulesFromPayloadJson(
                canonical.CanonicalJson));

        var sourceReport = await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .Find(report => report.Id == scenario.SourceReportId)
            .SingleAsync(ct);
        var payloads = database.GetCollection<WorkReportPayload>(
            "work_report_payloads");
        var externalPayload = await payloads
            .Find(item =>
                item.ReportId == scenario.SourceReportId &&
                item.PayloadRevision == sourceReport.PayloadRevision &&
                !item.IsDeleted)
            .SingleAsync(ct);
        var poisonHash = new string('0', 64);

        await versions.UpdateOneAsync(
            item => item.Id == version.Id,
            Builders<DynamicFlowTemplateVersion>.Update
                .Set(
                    item => item.PayloadJson,
                    canonical.CanonicalJson)
                .Set(
                    item => item.PayloadHash,
                    canonical.PayloadHash),
            cancellationToken: ct);
        await familyCollection.UpdateOneAsync(
            item => item.Id == family.Id,
            Builders<DynamicFlowTemplate>.Update
                .Set(
                    item => item.CurrentVersionHash,
                    canonical.PayloadHash),
            cancellationToken: ct);
        await instanceCollection.UpdateOneAsync(
            item => item.Id == instance.Id,
            Builders<DynamicFlowInstance>.Update
                .Set(
                    item => item.FlowPayloadHash,
                    canonical.PayloadHash),
            cancellationToken: ct);
        await payloads.UpdateOneAsync(
            item => item.Id == externalPayload.Id,
            Builders<WorkReportPayload>.Update
                .Set(item => item.PayloadHash, poisonHash),
            cancellationToken: ct);

        try
        {
            return await PreviewP7MappingAsync(
                api,
                scenario,
                new JsonObject(),
                ct);
        }
        finally
        {
            await payloads.UpdateOneAsync(
                item => item.Id == externalPayload.Id,
                Builders<WorkReportPayload>.Update
                    .Set(
                        item => item.PayloadHash,
                        externalPayload.PayloadHash),
                cancellationToken: ct);
            await instanceCollection.UpdateOneAsync(
                item => item.Id == instance.Id,
                Builders<DynamicFlowInstance>.Update
                    .Set(
                        item => item.FlowPayloadHash,
                        instance.FlowPayloadHash),
                cancellationToken: ct);
            await familyCollection.UpdateOneAsync(
                item => item.Id == family.Id,
                Builders<DynamicFlowTemplate>.Update
                    .Set(
                        item => item.CurrentVersionHash,
                        family.CurrentVersionHash),
                cancellationToken: ct);
            await versions.UpdateOneAsync(
                item => item.Id == version.Id,
                Builders<DynamicFlowTemplateVersion>.Update
                    .Set(
                        item => item.PayloadJson,
                        version.PayloadJson)
                    .Set(
                        item => item.PayloadHash,
                        version.PayloadHash),
                cancellationToken: ct);
        }
    }

    private static void AssertP705ApplyActivationBarrier(
        ApiHarnessResponse response,
        P7MappingScenario scenario,
        string rawSentinel)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Conflict,
            "P7-05 apply activation barrier");
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
            "P7-05 apply did not fail with the exact P7-08 activation barrier.");
        foreach (var forbidden in new[]
                 {
                     scenario.TargetReportId,
                     scenario.TargetAssignmentId,
                     scenario.SourceReportId,
                     scenario.SourceAssignmentId,
                     rawSentinel
                 })
        {
            Require(
                !response.Body.Contains(
                    forbidden,
                    StringComparison.OrdinalIgnoreCase),
                "P7-05 apply activation barrier leaked runtime identity or raw metadata.");
        }
    }
}
