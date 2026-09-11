using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsReconciliation;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    private static readonly string[] FoundationRouteIds =
    [
        CreateRouteId,
        ListRouteId,
        ReadRouteId,
        CancelRouteId,
        WorkerClaimRouteId,
        WorkerHeartbeatRouteId,
        WorkerRetryRouteId,
        WorkerPublishRouteId
    ];

    private async Task RunActivationCasesAsync(CancellationToken ct)
    {
        HarnessAssert.Equal(
            StatisticReconciliationCatalogPublicationState.PrePublish,
            StatisticReconciliationCapabilityActivation
                .ClassifyCatalogPublicationState(
                    StatisticReconciliationCapabilityActivation
                        .RequiredCurrentRawSha256,
                    StatisticReconciliationCapabilityActivation
                        .RequiredLockRawSha256),
            "P10 PRE_PUBLISH catalog state");
        HarnessAssert.Equal(
            StatisticReconciliationCatalogPublicationState.RolledBack,
            StatisticReconciliationCapabilityActivation
                .ClassifyCatalogPublicationState(
                    StatisticReconciliationCapabilityActivation
                        .RequiredCurrentRawSha256,
                    StatisticReconciliationCapabilityActivation
                        .PublishedLockRawSha256),
            "P10 ROLLED_BACK catalog state");
        HarnessAssert.Equal(
            StatisticReconciliationCatalogPublicationState.Restored,
            StatisticReconciliationCapabilityActivation
                .ClassifyCatalogPublicationState(
                    StatisticReconciliationCapabilityActivation
                        .PublishedCurrentRawSha256,
                    StatisticReconciliationCapabilityActivation
                        .PublishedLockRawSha256),
            "P10 RESTORED catalog state");
        HarnessAssert.Equal(
            StatisticReconciliationCatalogPublicationState.Unknown,
            StatisticReconciliationCapabilityActivation
                .ClassifyCatalogPublicationState(
                    StatisticReconciliationCapabilityActivation
                        .PublishedCurrentRawSha256,
                    StatisticReconciliationCapabilityActivation
                        .RequiredLockRawSha256),
            "P10 mixed publication state must fail closed");
        HarnessAssert.Equal(
            StatisticReconciliationCatalogPublicationState.Unknown,
            StatisticReconciliationCapabilityActivation
                .ClassifyCatalogPublicationState(
                    new string('0', 64),
                    StatisticReconciliationCapabilityActivation
                        .PublishedLockRawSha256),
            "P10 tampered CURRENT state must fail closed");

        HarnessAssert.True(
            StatisticReconciliationCapabilityActivation
                .IsExactPrePublishGeneratedCatalogMetadata(
                    StatisticReconciliationCapabilityActivation
                        .RequiredCurrentCatalogVersion,
                    StatisticReconciliationCapabilityActivation
                        .RequiredSourceCatalogSemanticSha256,
                    StatisticReconciliationCapabilityActivation
                        .RequiredSourceSchemaSemanticSha256),
            "P10 prepublish candidate must accept only the exact generated v1.6 tuple");
        HarnessAssert.True(
            !StatisticReconciliationCapabilityActivation
                .IsExactPrePublishGeneratedCatalogMetadata(
                    StatisticReconciliationCapabilityActivation
                        .RequiredCatalogVersion,
                    StatisticReconciliationCapabilityActivation
                        .PublishedCatalogSemanticSha256,
                    StatisticReconciliationCapabilityActivation
                        .PublishedSchemaSemanticSha256),
            "P10 prepublish candidate must reject the generated v1.7 tuple before publication");
        HarnessAssert.True(
            !StatisticReconciliationCapabilityActivation
                .ShouldUsePrePublishDisabledCompatibilityFallback(
                    true,
                    true,
                    false,
                    StatisticReconciliationCapabilityActivation
                        .RequiredCurrentCatalogVersion,
                    StatisticReconciliationCapabilityActivation
                        .RequiredSourceCatalogSemanticSha256,
                    StatisticReconciliationCapabilityActivation
                        .RequiredSourceSchemaSemanticSha256),
            "P10 Testing rollback must classify CURRENT/LOCK before applying the disabled compatibility fallback");
        HarnessAssert.True(
            !StatisticReconciliationCapabilityActivation
                .ShouldUsePrePublishDisabledCompatibilityFallback(
                    false,
                    true,
                    false,
                    StatisticReconciliationCapabilityActivation
                        .RequiredCurrentCatalogVersion,
                    StatisticReconciliationCapabilityActivation
                        .RequiredSourceCatalogSemanticSha256,
                    StatisticReconciliationCapabilityActivation
                        .RequiredSourceSchemaSemanticSha256),
            "P10 Production rollback with a valid workspace must classify CURRENT/LOCK before compatibility fallback");
        HarnessAssert.True(
            StatisticReconciliationCapabilityActivation
                .ShouldUsePrePublishDisabledCompatibilityFallback(
                    false,
                    false,
                    false,
                    StatisticReconciliationCapabilityActivation
                        .RequiredCurrentCatalogVersion,
                    StatisticReconciliationCapabilityActivation
                        .RequiredSourceCatalogSemanticSha256,
                    StatisticReconciliationCapabilityActivation
                        .RequiredSourceSchemaSemanticSha256),
            "P10 Production historical callability must preserve CANDIDATE_DISABLED without workspace discovery");

        await RunCaseAsync(
            "P10-ACT-01",
            Req("P10-REC-002"),
            O("API_KESTREL", "DIRECT_MONGO", "SOURCE_FINGERPRINT"),
            async () =>
            {
                foreach (var routeId in FoundationRouteIds)
                {
                    var evaluation = await EvaluateActivationAsync(
                        RequireApi(),
                        FoundationCapability,
                        routeId,
                        Actor("admin").Token,
                        ct);
                    HarnessAssert.True(
                        ApiHarnessClient.RequiredBool(
                            evaluation,
                            "enabled"),
                        $"P10 foundation route was not activated: {routeId}");
                    var binding = ApiHarnessClient.RequiredObject(
                        evaluation["binding"],
                        $"P10 binding {routeId}");
                    HarnessAssert.Equal(
                        ChainId,
                        ApiHarnessClient.RequiredString(binding, "chainId"),
                        $"P10 chain pin {routeId}");
                    HarnessAssert.Equal(
                        "P10-01",
                        ApiHarnessClient.RequiredString(binding, "promptId"),
                        $"P10 prompt pin {routeId}");
                    HarnessAssert.Equal(
                        0,
                        ApiHarnessClient.RequiredInt(binding, "stage"),
                        $"P10 stage {routeId}");
                    HarnessAssert.Equal(
                        "1.7",
                        ApiHarnessClient.RequiredString(
                            binding,
                            "catalogVersion"),
                        $"P10 catalog {routeId}");
                    _activationTrace.Add(new
                    {
                        caseId = "P10-ACT-01",
                        routeId,
                        capabilityId = FoundationCapability,
                        enabled = true,
                        chainId = ChainId,
                        candidateStage = 0,
                        promotions = Array.Empty<string>()
                    });
                }
                return new P10CaseObservation(
                    200,
                    "All eight frozen P10 foundation route IDs activated through the exact candidate binding.",
                    "routes=8;enabled=8;stage=0;catalog=1.7;promotions=0",
                    ActorAlias: "admin",
                    RequestHashSha256: EvidenceRequestHash(
                        "activation/evaluate",
                        new JsonArray(FoundationRouteIds
                            .Select(value => JsonValue.Create(value))
                            .ToArray())));
            });

        await RunCaseAsync(
            "P10-ACT-02",
            Req("P10-REC-002"),
            O("API_KESTREL", "COLLECTION_DELTA"),
            async () =>
            {
                var unknown = await EvaluateActivationAsync(
                    RequireApi(),
                    "UNKNOWN_RECONCILIATION_CAPABILITY",
                    CreateRouteId,
                    Actor("admin").Token,
                    ct);
                HarnessAssert.True(
                    !ApiHarnessClient.RequiredBool(unknown, "enabled"),
                    "Unknown P10 capability unexpectedly activated.");
                HarnessAssert.Equal(
                    "CAPABILITY_CONFLICT",
                    ApiHarnessClient.RequiredString(unknown, "reason"),
                    "Unknown P10 capability reason");
                var unmapped = await EvaluateActivationAsync(
                    RequireApi(),
                    FoundationCapability,
                    "P10_RECONCILIATION_FUTURE_ROUTE",
                    Actor("admin").Token,
                    ct);
                HarnessAssert.True(
                    !ApiHarnessClient.RequiredBool(unmapped, "enabled"),
                    "Future P10 route unexpectedly activated.");
                HarnessAssert.Equal(
                    "ROUTE_NOT_PROVEN",
                    ApiHarnessClient.RequiredString(unmapped, "reason"),
                    "Future P10 route reason");
                var conflict = await EvaluateActivationAsync(
                    RequireApi(),
                    "EXPECTED_ACTUAL_DELTA",
                    CreateRouteId,
                    Actor("admin").Token,
                    ct);
                HarnessAssert.True(
                    !ApiHarnessClient.RequiredBool(conflict, "enabled"),
                    "Future capability on foundation route unexpectedly activated.");
                HarnessAssert.Equal(
                    "ROUTE_NOT_PROVEN",
                    ApiHarnessClient.RequiredString(conflict, "reason"),
                    "Future capability/route mismatch reason");
                _activationTrace.Add(new
                {
                    caseId = "P10-ACT-02",
                    unknown = "CAPABILITY_CONFLICT",
                    unmapped = "ROUTE_NOT_PROVEN",
                    future = "ROUTE_NOT_PROVEN",
                    writes = 0
                });
                return new P10CaseObservation(
                    200,
                    "Unknown capability, unmapped route and future capability all failed closed.",
                    "unknown=CAPABILITY_CONFLICT;unmapped=ROUTE_NOT_PROVEN;future=ROUTE_NOT_PROVEN;writes=0",
                    ActorAlias: "admin",
                    RequestHashSha256: EvidenceRequestHash(
                        "activation/negative",
                        new JsonObject
                        {
                            ["capabilityId"] = "EXPECTED_ACTUAL_DELTA",
                            ["routeId"] = CreateRouteId
                        }));
            });

        await RunCaseAsync(
            "P10-ACT-03",
            Req("P10-REC-002"),
            O("API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"),
            async () =>
            {
                var before = await CountRunsAsync(ct);
                await RunVariantAsync(
                    "P10-ACT-03",
                    "tampered-stage-lock-pin",
                    new BackendServerOptions
                    {
                        P10ReconciliationCandidate = BuildCandidateOptions() with
                        {
                            StageLockSha256 = new string('0', 64)
                        }
                    },
                    async (api, token) =>
                    {
                        var evaluation = await EvaluateActivationAsync(
                            api,
                            FoundationCapability,
                            CreateRouteId,
                            token,
                            ct);
                        HarnessAssert.True(
                            !ApiHarnessClient.RequiredBool(
                                evaluation,
                                "enabled"),
                            "Tampered stage-lock pin unexpectedly activated.");
                        HarnessAssert.Equal(
                            "CANDIDATE_PIN_MISMATCH",
                            ApiHarnessClient.RequiredString(
                                evaluation,
                                "reason"),
                            "Tampered stage-lock reason");
                        return true;
                    },
                    ct);
                await RunVariantAsync(
                    "P10-ACT-03",
                    "wrong-owned-database",
                    new BackendServerOptions
                    {
                        P10ReconciliationCandidate = BuildCandidateOptions() with
                        {
                            ExpectedDatabase = "tdtd_p10_wrong_owner"
                        }
                    },
                    async (api, token) =>
                    {
                        var evaluation = await EvaluateActivationAsync(
                            api,
                            FoundationCapability,
                            CreateRouteId,
                            token,
                            ct);
                        HarnessAssert.True(
                            !ApiHarnessClient.RequiredBool(
                                evaluation,
                                "enabled"),
                            "Wrong P10 database pin unexpectedly activated.");
                        HarnessAssert.Equal(
                            "DATABASE_MISMATCH",
                            ApiHarnessClient.RequiredString(
                                evaluation,
                                "reason"),
                            "Wrong P10 database reason");
                        return true;
                    },
                    ct);
                HarnessAssert.Equal(
                    before,
                    await CountRunsAsync(ct),
                    "Tampered P10 variants changed durable runs");
                return new P10CaseObservation(
                    200,
                    "Tampered stage-lock and database ownership pins failed closed in isolated Kestrel processes.",
                    "stageLock=CANDIDATE_PIN_MISMATCH;database=DATABASE_MISMATCH;writes=0",
                    ActorAlias: "admin",
                    RequestHashSha256: EvidenceRequestHash(
                        "activation/tamper",
                        new JsonObject
                        {
                            ["stageLockSha256"] = new string('0', 64),
                            ["expectedDatabase"] = "tdtd_p10_wrong_owner"
                        }));
            });

        await RunCaseAsync(
            "P10-ACT-04",
            Req("P10-REC-002"),
            O("API_KESTREL", "COLLECTION_DELTA", "P9_REGRESSION"),
            async () =>
            {
                await RunVariantAsync(
                    "P10-ACT-04",
                    "production-current-v16-disabled",
                    new BackendServerOptions
                    {
                        P10ReconciliationCandidate = BuildCandidateOptions() with
                        {
                            Enabled = false
                        }
                    },
                    async (api, token) =>
                    {
                        foreach (var routeId in FoundationRouteIds)
                        {
                            var evaluation = await EvaluateActivationAsync(
                                api,
                                FoundationCapability,
                                routeId,
                                token,
                                ct);
                            HarnessAssert.True(
                                !ApiHarnessClient.RequiredBool(
                                    evaluation,
                                    "enabled"),
                                $"CURRENT v1.6 opened P10 route {routeId}.");
                            HarnessAssert.Equal(
                                "CANDIDATE_DISABLED",
                                ApiHarnessClient.RequiredString(
                                    evaluation,
                                    "reason"),
                                $"CURRENT v1.6 denial {routeId}");
                            _activationTrace.Add(new
                            {
                                caseId = "P10-ACT-04",
                                routeId,
                                enabled = false,
                                reason = "CANDIDATE_DISABLED",
                                current = "1.6"
                            });
                        }
                        return true;
                    },
                    ct);
                return new P10CaseObservation(
                    200,
                    "Production CURRENT v1.6 denied every one of the eight P10 routes.",
                    "current=1.6;routes=8;enabled=0;writes=0",
                    ActorAlias: "admin",
                    RequestHashSha256: EvidenceRequestHash(
                        "activation/current-v1.6",
                        new JsonArray(FoundationRouteIds
                            .Select(value => JsonValue.Create(value))
                            .ToArray())));
            });

        await RunCaseAsync(
            "P10-ACT-05",
            Req("P10-REC-002"),
            O("API_KESTREL", "COLLECTION_DELTA"),
            async () =>
            {
                await RunVariantAsync(
                    "P10-ACT-05",
                    "ambient-bypass-disabled",
                    new BackendServerOptions
                    {
                        P10ReconciliationCandidate = BuildCandidateOptions() with
                        {
                            Enabled = false
                        }
                    },
                    async (api, token) =>
                    {
                        var evaluation = await EvaluateActivationAsync(
                            api,
                            FoundationCapability,
                            CreateRouteId,
                            token,
                            ct,
                            new Dictionary<string, string>
                            {
                                ["X-P10-Enabled"] = "true",
                                ["X-Stat-Phase"] = "10",
                                ["X-Candidate-Chain"] = ChainId
                            });
                        HarnessAssert.True(
                            !ApiHarnessClient.RequiredBool(
                                evaluation,
                                "enabled"),
                            "Ambient headers bypassed disabled P10 candidate.");
                        HarnessAssert.Equal(
                            "CANDIDATE_DISABLED",
                            ApiHarnessClient.RequiredString(
                                evaluation,
                                "reason"),
                            "Ambient bypass denial");
                        return true;
                    },
                    ct);
                await RunVariantAsync(
                    "P10-ACT-05",
                    "non-testing-environment",
                    new BackendServerOptions
                    {
                        EnvironmentNameOverride = "Development",
                        P10ReconciliationCandidate = BuildCandidateOptions()
                    },
                    async (api, token) =>
                    {
                        var response = await api.PostAsync(
                            "api/testing/p10/statistic-reconciliations/activation/evaluate?enabled=true&phase=10",
                            new
                            {
                                capabilityId = FoundationCapability,
                                routeId = CreateRouteId,
                                enabled = true
                            },
                            token,
                            headers: new Dictionary<string, string>
                            {
                                ["X-P10-Enabled"] = "true"
                            },
                            ct: ct);
                        ApiHarnessClient.ExpectStatus(
                            response,
                            HttpStatusCode.NotFound,
                            "Non-Testing activation endpoint");
                        return true;
                    },
                    ct);
                return new P10CaseObservation(
                    404,
                    "Headers, query flags, body booleans and a non-Testing process could not activate P10.",
                    "headers=false;query=false;bodyBoolean=false;nonTesting=404;writes=0",
                    ActorAlias: "admin",
                    RequestHashSha256: EvidenceRequestHash(
                        "activation/ambient-bypass",
                        new JsonObject { ["enabled"] = true }));
            });

        await RunCaseAsync(
            "P10-ACT-06",
            Req("P10-REC-002"),
            O(
                "DIRECT_MONGO",
                "P9_REGRESSION",
                "P11_ZERO_WRITE",
                "P12_ZERO_WRITE",
                "PROFILE_BLOCK",
                "SOURCE_FINGERPRINT"),
            async () =>
            {
                HarnessAssert.Equal(
                    8,
                    StatConfigPhaseBarrier.CurrentPhase,
                    "Global statistics phase changed");
                var p9 = await RequireDatabase()
                    .GetCollection<BsonDocument>(
                        "work_report_statistic_rebuild_jobs")
                    .Find(new BsonDocument(
                        "_id",
                        ObjectId.Parse(Fixture().P9RunId)))
                    .SingleAsync(ct);
                var p9Hash = HashBytes(p9.ToBson());

                using var catalog = JsonDocument.Parse(
                    await File.ReadAllBytesAsync(Candidate().CatalogPath, ct));
                var profile = catalog.RootElement
                    .GetProperty("domains")
                    .GetProperty("statisticsCapabilities")
                    .EnumerateArray()
                    .Single(item => item.GetProperty("id").GetString() ==
                                    "FLOW_STATISTIC_PROFILE");
                HarnessAssert.Equal(
                    "INTENTIONAL_BLOCK",
                    profile.GetProperty("status").GetString(),
                    "FLOW_STATISTIC_PROFILE status");
                HarnessAssert.Equal(
                    JsonValueKind.Null,
                    profile.GetProperty("targetPhase").ValueKind,
                    "FLOW_STATISTIC_PROFILE target phase");

                var collectionNames = await (await RequireDatabase()
                        .ListCollectionNamesAsync(cancellationToken: ct))
                    .ToListAsync(ct);
                HarnessAssert.True(
                    collectionNames.All(name =>
                        !name.StartsWith("p11", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("p12", StringComparison.OrdinalIgnoreCase)),
                    "P11/P12 successor store unexpectedly exists.");
                var p9After = await RequireDatabase()
                    .GetCollection<BsonDocument>(
                        "work_report_statistic_rebuild_jobs")
                    .Find(new BsonDocument(
                        "_id",
                        ObjectId.Parse(Fixture().P9RunId)))
                    .SingleAsync(ct);
                HarnessAssert.Equal(
                    p9Hash,
                    HashBytes(p9After.ToBson()),
                    "P9 source publication changed");
                return new P10CaseObservation(
                    200,
                    "Phase stayed 8; P9 bytes were stable; profile/P11/P12 remained blocked and unwritten.",
                    "phase=8;p9Stable=true;profile=INTENTIONAL_BLOCK/null;p11Writes=0;p12Writes=0",
                    ActorAlias: "admin",
                    RequestHashSha256: EvidenceRequestHash(
                        "activation/barrier-regression",
                        new JsonObject
                        {
                            ["phase"] = 8,
                            ["p9RunId"] = Fixture().P9RunId
                        }),
                    ReceiptHashSha256: p9Hash,
                    StateHashSha256: BsonText(p9After, "stateHash"));
            });
    }
}
