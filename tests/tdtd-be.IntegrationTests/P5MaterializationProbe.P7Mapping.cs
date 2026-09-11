using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

internal static partial class P5MaterializationProbe
{
    private const string P7MappingHiddenSourceValue =
        "P5_NOTE";
    private const string P7MappingResultFileName =
        "p7-mapping-gate-result.json";
    private const string P7MappingApiFileName =
        "p7-mapping-api-exchanges.json";
    private const string P7MappingMongoFileName =
        "p7-mapping-direct-mongo.json";
    private const string P708PostcommitFaultCommandId =
        "p708-postcommit-before-projectors";
    private const string P708PostcommitFaultPoint =
        "BEFORE_PROJECTORS";
    private const string P708PostcommitFinalizeFaultPoint =
        "BEFORE_FINALIZE";
    private const string P708PostcommitFaultMarker =
        "TEST_ONLY_DYNAMIC_FLOW_MAPPING_RECONCILE_FAILURE:BEFORE_PROJECTORS";
    private const string P708PostcommitFinalizeFaultMarker =
        "TEST_ONLY_DYNAMIC_FLOW_MAPPING_RECONCILE_FAILURE:BEFORE_FINALIZE";

    public static async Task<int> RunP7MappingGateAsync(string[] args)
    {
        var through = ParseP7MappingStage(args);
        var startedAtUtc = DateTime.UtcNow;
        var runKey =
            $"p7map_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.Create(runKey);
        var iterationRoot = paths.IterationRoot(1);
        var cases = new HarnessCaseRunner();
        var cleanupErrors = new List<string>();
        var mongoEvidence = new List<object>();
        var additionalApiEvidence =
            new List<ApiExchangeEvidence>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        ApiHarnessClient? api = null;
        string? failure = null;

        Console.WriteLine(
            $"P7 mapping gate through P7-{through:00} artifacts: {paths.RunRoot}");
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "environment.json"),
            new
            {
                schemaVersion = 1,
                gate = "P7-MAPPING",
                through = $"P7-{through:00}",
                runKey,
                startedAtUtc,
                isolation = new
                {
                    realKestrel = true,
                    isolatedMongoReplicaSet = true,
                    databasePerRun = true,
                    environment = "Testing",
                    p7MappingCandidateActivation = true,
                    previewSigningKeyMinimumUtf8Bytes = 32,
                    previewSigningKeyPersisted = false,
                    hangfireServer = false,
                    redis = false
                }
            });

        try
        {
            mongo = await MongoReplicaSetLease.StartAsync(
                paths,
                iterationRoot,
                runKey,
                1,
                CancellationToken.None);
            var database = mongo.Client.GetDatabase(mongo.DatabaseName);
            var backendRoot = Path.Combine(iterationRoot, "backend");
            Directory.CreateDirectory(backendRoot);
            backend = await BackendServerLease.StartAsync(
                paths,
                backendRoot,
                runKey,
                mongo,
                CancellationToken.None,
                new BackendServerOptions
                {
                    EnableDynamicFlowP7MappingCandidate = true,
                    DynamicFlowP7MappingActivationThrough = through,
                    DynamicFlowMappingReconcileFaultCommandId =
                        through >= 8
                            ? P708PostcommitFaultCommandId
                            : null,
                    DynamicFlowMappingReconcileFaultPoints =
                        through >= 8
                            ? new[]
                            {
                                P708PostcommitFaultPoint,
                                P708PostcommitFinalizeFaultPoint
                            }
                            : Array.Empty<string>()
                });
            api = new ApiHarnessClient(backend.BaseUri);
            var (adminToken, _) = await BootstrapAndLoginAsync(
                api,
                backend,
                database,
                CancellationToken.None);
            var baseFixture = await SeedFixtureAsync(
                database,
                CancellationToken.None);
            var p601 = await SeedP601FixtureAsync(
                database,
                baseFixture,
                CancellationToken.None);
            var historicalFixture =
                await CloneP601HistoricalP7FixtureAsync(
                    database,
                    p601,
                    CancellationToken.None);
            var mappingFixture = await AdaptP601ToP7MappingAsync(
                database,
                p601,
                CancellationToken.None);

            P7MappingScenario? canonicalScenario = null;
            await cases.RunAsync(
                "P7-04-CANONICAL-RUNTIME-SOURCE-PINS",
                async () =>
                {
                    var workId = await CloneP7MappingWorkAsync(
                        database,
                        mappingFixture.WorkId,
                        "P704-CANONICAL",
                        CancellationToken.None);
                    canonicalScenario = await PrepareP7MappingScenarioAsync(
                        api,
                        backend,
                        database,
                        adminToken,
                        baseFixture,
                        mappingFixture,
                        workId,
                        "p7-04-canonical-launch",
                        "2026-07-P704-CANONICAL",
                        CancellationToken.None);
                    var preview = await PreviewP7MappingAsync(
                        api,
                        canonicalScenario,
                        new JsonObject(),
                        CancellationToken.None);
                    AssertP7MappingPreviewIdentity(
                        preview,
                        mappingFixture,
                        canonicalScenario,
                        through);
                    mongoEvidence.Add(
                        await CaptureP7MappingScenarioEvidenceAsync(
                            database,
                            canonicalScenario,
                            "P7-04-CANONICAL-RUNTIME-SOURCE-PINS",
                            CancellationToken.None));
                    return new CaseObservation(
                        "Real preview resolved the exact v1.4 locked flow, A ancestor report, runtime epoch, and form pins.",
                        P7MappingFingerprint(
                            "P7-04",
                            "canonical",
                            ApiHarnessClient.RequiredString(
                                preview.Json,
                                "sourceSignature"),
                            ApiHarnessClient.RequiredString(
                                preview.Json,
                                "resultSemanticHash")));
                });

            await cases.RunAsync(
                "P7-04-SOURCE-DRIFT-ZERO-WRITE",
                async () =>
                {
                    var scenario = canonicalScenario
                                   ?? throw new HarnessCaseNotRunnableException(
                                       "P7-04 canonical scenario did not initialize.");
                    var beforeMongo =
                        await CaptureP7MappingPersistenceStateAsync(
                        database,
                        scenario.TargetReportId,
                        CancellationToken.None);
                    var before = beforeMongo.Fingerprint;
                    string? driftReason = null;
                    string? driftErrorCode = null;
                    string? driftField = null;
                    try
                    {
                        await database.GetCollection<WorkAssignmentReport>(
                                "work_assignment_report")
                            .UpdateOneAsync(
                                item => item.Id == scenario.SourceReportId,
                                Builders<WorkAssignmentReport>.Update
                                    .Set(item => item.IsCurrent, false),
                                cancellationToken: CancellationToken.None);
                        var drift = await api.PostAsync(
                            $"api/work-assignment-reports/{scenario.TargetReportId}/draft/preview-dynamic-flow-mapping",
                            new JsonObject(),
                            scenario.ActorToken,
                            ct: CancellationToken.None);
                        ApiHarnessClient.ExpectStatus(
                            drift,
                            HttpStatusCode.Conflict,
                            "P7-04 source drift preview");
                        driftReason = ApiHarnessClient.FindStringRecursive(
                            drift.Json,
                            "reason");
                        driftErrorCode =
                            ApiHarnessClient.FindStringRecursive(
                                drift.Json,
                                "errorCode");
                        driftField =
                            ApiHarnessClient.FindStringRecursive(
                                drift.Json,
                                "field");
                        Require(
                            driftReason is
                                "DYNAMIC_FLOW_MAPPING_SOURCE_REPORT_INEFFECTIVE" or
                                "DYNAMIC_FLOW_MAPPING_SOURCE_PIN_CONFLICT",
                            "P7-04 source drift did not expose the stable canonical-source drift reason; " +
                            $"actual={driftReason ?? "<none>"}.");
                    }
                    finally
                    {
                        await database.GetCollection<WorkAssignmentReport>(
                                "work_assignment_report")
                            .UpdateOneAsync(
                                item => item.Id == scenario.SourceReportId,
                                Builders<WorkAssignmentReport>.Update
                                    .Set(item => item.IsCurrent, true),
                                cancellationToken: CancellationToken.None);
                    }
                    var after = await CaptureP7MappingWriteSnapshotAsync(
                        database,
                        scenario.TargetReportId,
                        CancellationToken.None);
                    Require(
                        before == after,
                        "P7-04 source drift changed target/mapping persistence.");
                    mongoEvidence.Add(
                        await CaptureP7MappingNegativeMongoEvidenceAsync(
                            database,
                            scenario.TargetReportId,
                            "P7-04-SOURCE-DRIFT-ZERO-WRITE",
                            beforeMongo,
                            CancellationToken.None));
                    mongoEvidence.Add(new
                    {
                        caseId = "P7-04-SOURCE-DRIFT-ZERO-WRITE-OUTCOME",
                        statusCode = (int)HttpStatusCode.Conflict,
                        errorCode = driftErrorCode,
                        reason = driftReason,
                        field = driftField,
                        zeroWrite = true,
                        noOrphan = beforeMongo.NoOrphan,
                        rawPayloadRecorded = false
                    });
                    return new CaseObservation(
                        "Canonical source drift failed with HTTP 409 before any target, receipt, provenance, event, outbox, audit, payload, or section write.",
                        P7MappingFingerprint(
                            "P7-04",
                            "source-drift",
                            before,
                            after));
                });

            await cases.RunAsync(
                "P7-04-FORGED-SOURCE-SELECTOR-ZERO-WRITE",
                async () =>
                {
                    var scenario = canonicalScenario
                                   ?? throw new HarnessCaseNotRunnableException(
                                       "P7-04 canonical scenario did not initialize.");
                    var beforeMongo =
                        await CaptureP7MappingPersistenceStateAsync(
                        database,
                        scenario.TargetReportId,
                        CancellationToken.None);
                    var before = beforeMongo.Fingerprint;
                    var forged = await PreviewP7MappingAsync(
                        api,
                        scenario,
                        new JsonObject
                        {
                            ["sourceReportIds"] = new JsonArray(
                                ObjectId.GenerateNewId().ToString()),
                            ["sourceMode"] = "CHILD_FLOW"
                        },
                        CancellationToken.None);
                    ApiHarnessClient.ExpectStatus(
                        forged,
                        HttpStatusCode.BadRequest,
                        "P7-04 forged source selector");
                    var forgedErrorCode =
                        ApiHarnessClient.FindStringRecursive(
                            forged.Json,
                            "errorCode");
                    var forgedReason =
                        ApiHarnessClient.FindStringRecursive(
                            forged.Json,
                            "reason");
                    var forgedField =
                        ApiHarnessClient.FindStringRecursive(
                            forged.Json,
                            "field");
                    Require(
                        forgedReason ==
                        "DYNAMIC_FLOW_MAPPING_CONFIG_MUST_BE_FLOW_OWNED",
                        "P7-04 forged source selector bypassed the canonical source owner.");
                    var after = await CaptureP7MappingWriteSnapshotAsync(
                        database,
                        scenario.TargetReportId,
                        CancellationToken.None);
                    Require(
                        before == after,
                        "P7-04 forged source selector changed target/mapping persistence.");
                    mongoEvidence.Add(
                        await CaptureP7MappingNegativeMongoEvidenceAsync(
                            database,
                            scenario.TargetReportId,
                            "P7-04-FORGED-SOURCE-SELECTOR-ZERO-WRITE",
                            beforeMongo,
                            CancellationToken.None));
                    mongoEvidence.Add(new
                    {
                        caseId =
                            "P7-04-FORGED-SOURCE-SELECTOR-ZERO-WRITE-OUTCOME",
                        statusCode = (int)forged.StatusCode,
                        errorCode = forgedErrorCode,
                        reason = forgedReason,
                        field = forgedField,
                        zeroWrite = true,
                        noOrphan = beforeMongo.NoOrphan,
                        rawPayloadRecorded = false
                    });
                    return new CaseObservation(
                        "Caller-selected source IDs/mode were rejected before source hydration and before every mapping-owned write.",
                        P7MappingFingerprint(
                            "P7-04",
                            "forged-source-selector",
                            before,
                            after));
                });

            await cases.RunAsync(
                "P7-04-PARENT-ASSIGNMENT-SUBSTITUTION-ZERO-WRITE",
                async () =>
                {
                    var scenario = canonicalScenario
                                   ?? throw new HarnessCaseNotRunnableException(
                                       "P7-04 canonical scenario did not initialize.");
                    var beforeMongo =
                        await CaptureP7MappingPersistenceStateAsync(
                        database,
                        scenario.TargetReportId,
                        CancellationToken.None);
                    var before = beforeMongo.Fingerprint;
                    var substituted = await PreviewP7MappingAsync(
                        api,
                        scenario,
                        new JsonObject
                        {
                            ["targetAssignmentId"] =
                                scenario.SourceAssignmentId
                        },
                        CancellationToken.None);
                    ApiHarnessClient.ExpectStatus(
                        substituted,
                        HttpStatusCode.Conflict,
                        "P7-04 parent assignment substitution");
                    var substitutionErrorCode =
                        ApiHarnessClient.FindStringRecursive(
                            substituted.Json,
                            "errorCode");
                    var substitutionReason =
                        ApiHarnessClient.FindStringRecursive(
                            substituted.Json,
                            "reason");
                    var substitutionField =
                        ApiHarnessClient.FindStringRecursive(
                            substituted.Json,
                            "field");
                    Require(
                        substitutionErrorCode ==
                        "DYNAMIC_FLOW_MAPPING_IDENTITY_CONFLICT",
                        "P7-04 parent assignment substitution did not fail the exact runtime identity assertion.");
                    var after = await CaptureP7MappingWriteSnapshotAsync(
                        database,
                        scenario.TargetReportId,
                        CancellationToken.None);
                    Require(
                        before == after,
                        "P7-04 parent assignment substitution changed target/mapping persistence.");
                    mongoEvidence.Add(
                        await CaptureP7MappingNegativeMongoEvidenceAsync(
                            database,
                            scenario.TargetReportId,
                            "P7-04-PARENT-ASSIGNMENT-SUBSTITUTION-ZERO-WRITE",
                            beforeMongo,
                            CancellationToken.None));
                    mongoEvidence.Add(new
                    {
                        caseId =
                            "P7-04-PARENT-ASSIGNMENT-SUBSTITUTION-ZERO-WRITE-OUTCOME",
                        statusCode = (int)substituted.StatusCode,
                        errorCode = substitutionErrorCode,
                        reason = substitutionReason,
                        field = substitutionField,
                        zeroWrite = true,
                        noOrphan = beforeMongo.NoOrphan,
                        rawPayloadRecorded = false
                    });
                    return new CaseObservation(
                        "Substituting the approved parent/source assignment for the target assignment failed exact runtime binding with no write.",
                        P7MappingFingerprint(
                            "P7-04",
                            "parent-substitution",
                            before,
                            after));
                });

            await cases.RunAsync(
                "P7-04-VERSION-HASH-CATALOG-ASSERTION-DRIFT",
                async () =>
                {
                    var scenario = canonicalScenario
                                   ?? throw new HarnessCaseNotRunnableException(
                                       "P7-04 canonical scenario did not initialize.");
                    var beforeMongo =
                        await CaptureP7MappingPersistenceStateAsync(
                        database,
                        scenario.TargetReportId,
                        CancellationToken.None);
                    var before = beforeMongo.Fingerprint;
                    var assertions = new[]
                    {
                        (
                            Name: "flowVersionId",
                            Payload: new JsonObject
                            {
                                ["flowVersionId"] =
                                    ObjectId.GenerateNewId().ToString()
                            }),
                        (
                            Name: "flowPayloadHash",
                            Payload: new JsonObject
                            {
                                ["flowPayloadHash"] = new string('b', 64)
                            }),
                        (
                            Name: "catalogVersion",
                            Payload: new JsonObject
                            {
                                ["catalogVersion"] = "forged-p7-catalog"
                            }),
                        (
                            Name: "catalogSemanticHash",
                            Payload: new JsonObject
                            {
                                ["catalogSemanticHash"] =
                                    new string('c', 64)
                            }),
                        (
                            Name: "executionEpoch",
                            Payload: new JsonObject
                            {
                                ["executionEpoch"] = 999
                            })
                    };
                    var assertionOutcomes = new List<object>();
                    foreach (var assertion in assertions)
                    {
                        var drift = await PreviewP7MappingAsync(
                            api,
                            scenario,
                            assertion.Payload,
                            CancellationToken.None);
                        ApiHarnessClient.ExpectStatus(
                            drift,
                            HttpStatusCode.Conflict,
                            "P7-04 version/hash/catalog assertion drift");
                        Require(
                            ApiHarnessClient.FindStringRecursive(
                            drift.Json,
                            "errorCode") ==
                            "DYNAMIC_FLOW_MAPPING_IDENTITY_CONFLICT",
                            "P7-04 version/hash/catalog assertion drift returned an unstable identity boundary.");
                        assertionOutcomes.Add(new
                        {
                            assertion = assertion.Name,
                            statusCode = (int)drift.StatusCode,
                            errorCode = ApiHarnessClient.FindStringRecursive(
                                drift.Json,
                                "errorCode"),
                            reason = ApiHarnessClient.FindStringRecursive(
                                drift.Json,
                                "reason"),
                            field = ApiHarnessClient.FindStringRecursive(
                                drift.Json,
                                "field"),
                            zeroWrite = true,
                            noOrphan = beforeMongo.NoOrphan,
                            rawPayloadRecorded = false
                        });
                    }
                    var after = await CaptureP7MappingWriteSnapshotAsync(
                        database,
                        scenario.TargetReportId,
                        CancellationToken.None);
                    Require(
                        before == after,
                        "P7-04 version/hash/catalog assertion drift changed target/mapping persistence.");
                    mongoEvidence.Add(
                        await CaptureP7MappingNegativeMongoEvidenceAsync(
                            database,
                            scenario.TargetReportId,
                            "P7-04-VERSION-HASH-CATALOG-ASSERTION-DRIFT",
                            beforeMongo,
                            CancellationToken.None));
                    mongoEvidence.Add(new
                    {
                        caseId =
                            "P7-04-VERSION-HASH-CATALOG-ASSERTION-DRIFT-OUTCOMES",
                        outcomes = assertionOutcomes.ToArray(),
                        rawPayloadRecorded = false
                    });
                    return new CaseObservation(
                        "Forged locked version ID, payload hash, catalog version/hash, and execution epoch assertions all failed before hydration/CAS.",
                        P7MappingFingerprint(
                            "P7-04",
                            "definition-drift",
                            before,
                            after));
                });

            await cases.RunAsync(
                "P7-04-FOREIGN-INSTANCE-BRANCH-ASSERTION-DRIFT",
                async () =>
                {
                    var scenario = canonicalScenario
                                   ?? throw new HarnessCaseNotRunnableException(
                                       "P7-04 canonical scenario did not initialize.");
                    var beforeMongo =
                        await CaptureP7MappingPersistenceStateAsync(
                        database,
                        scenario.TargetReportId,
                        CancellationToken.None);
                    var before = beforeMongo.Fingerprint;
                    var foreignOutcomes = new List<object>();
                    foreach (var assertion in new[]
                             {
                                 (
                                     Name: "flowInstanceId",
                                     Payload: new JsonObject
                                     {
                                         ["flowInstanceId"] =
                                             ObjectId.GenerateNewId().ToString()
                                     }),
                                 (
                                     Name: "branchId",
                                     Payload: new JsonObject
                                     {
                                         ["branchId"] =
                                             ObjectId.GenerateNewId().ToString()
                                     })
                             })
                    {
                        var drift = await PreviewP7MappingAsync(
                            api,
                            scenario,
                            assertion.Payload,
                            CancellationToken.None);
                        ApiHarnessClient.ExpectStatus(
                            drift,
                            HttpStatusCode.Conflict,
                            "P7-04 foreign instance/branch assertion");
                        Require(
                            ApiHarnessClient.FindStringRecursive(
                                drift.Json,
                                "errorCode") ==
                            "DYNAMIC_FLOW_MAPPING_IDENTITY_CONFLICT",
                            "P7-04 foreign instance/branch assertion returned an unstable identity boundary.");
                        foreignOutcomes.Add(new
                        {
                            assertion = assertion.Name,
                            statusCode = (int)drift.StatusCode,
                            errorCode = ApiHarnessClient.FindStringRecursive(
                                drift.Json,
                                "errorCode"),
                            reason = ApiHarnessClient.FindStringRecursive(
                                drift.Json,
                                "reason"),
                            field = ApiHarnessClient.FindStringRecursive(
                                drift.Json,
                                "field"),
                            zeroWrite = true,
                            noOrphan = beforeMongo.NoOrphan,
                            rawPayloadRecorded = false
                        });
                    }
                    var after = await CaptureP7MappingWriteSnapshotAsync(
                        database,
                        scenario.TargetReportId,
                        CancellationToken.None);
                    Require(
                        before == after,
                        "P7-04 foreign instance/branch assertion changed target/mapping persistence.");
                    mongoEvidence.Add(
                        await CaptureP7MappingNegativeMongoEvidenceAsync(
                            database,
                            scenario.TargetReportId,
                            "P7-04-FOREIGN-INSTANCE-BRANCH-ASSERTION-DRIFT",
                            beforeMongo,
                            CancellationToken.None));
                    mongoEvidence.Add(new
                    {
                        caseId =
                            "P7-04-FOREIGN-INSTANCE-BRANCH-ASSERTION-DRIFT-OUTCOMES",
                        outcomes = foreignOutcomes.ToArray(),
                        rawPayloadRecorded = false
                    });
                    return new CaseObservation(
                        "Unrelated instance and branch assertions failed exact execution identity with zero mapping-owned writes.",
                        P7MappingFingerprint(
                            "P7-04",
                            "foreign-runtime",
                            before,
                            after));
                });

            await cases.RunAsync(
                "P7-04-HISTORICAL-LEGACY-MIXED-PIN-BARRIERS",
                async () =>
                {
                    var scenario = canonicalScenario
                                   ?? throw new HarnessCaseNotRunnableException(
                                       "P7-04 canonical scenario did not initialize.");
                    var beforeMongo =
                        await CaptureP7MappingPersistenceStateAsync(
                        database,
                        scenario.TargetReportId,
                        CancellationToken.None);
                    var before = beforeMongo.Fingerprint;
                    var barrierOutcomes = new List<object>();
                    var versions = database.GetCollection<
                        DynamicFlowTemplateVersion>(
                        "dynamic_flow_template_versions");
                    var instances = database.GetCollection<
                        DynamicFlowInstance>(
                        "dynamic_flow_instances");
                    var version = await versions
                        .Find(item => item.Id == mappingFixture.VersionId)
                        .SingleAsync(CancellationToken.None);
                    var instance = await instances
                        .Find(item =>
                            item.Id == scenario.FlowInstanceId)
                        .SingleAsync(CancellationToken.None);
                    try
                    {
                        await versions.UpdateOneAsync(
                            item => item.Id == version.Id,
                            Builders<DynamicFlowTemplateVersion>.Update
                                .Set(
                                    item => item.MigrationState,
                                    DynamicFlowDefinitionMigrationStates
                                        .RequiresReview),
                            cancellationToken: CancellationToken.None);
                        var legacy = await PreviewP7MappingAsync(
                            api,
                            scenario,
                            new JsonObject(),
                            CancellationToken.None);
                        ApiHarnessClient.ExpectStatus(
                            legacy,
                            HttpStatusCode.Conflict,
                            "P7-04 REQUIRES_REVIEW legacy barrier");
                        var legacyReason =
                            ApiHarnessClient.FindStringRecursive(
                                legacy.Json,
                                "reason");
                        Require(
                            legacyReason ==
                            DynamicFlowLockedSnapshotIntegrity.FailureReason,
                            "P7-04 REQUIRES_REVIEW version was implicitly upgraded.");
                        var legacyState =
                            await CaptureP7MappingPersistenceStateAsync(
                                database,
                                scenario.TargetReportId,
                                CancellationToken.None);
                        Require(
                            before == legacyState.Fingerprint,
                            "P7-04 REQUIRES_REVIEW barrier changed target/mapping persistence.");
                        barrierOutcomes.Add(new
                        {
                            barrier = "REQUIRES_REVIEW",
                            status = (int)legacy.StatusCode,
                            errorCode =
                                ApiHarnessClient.FindStringRecursive(
                                    legacy.Json,
                                    "errorCode"),
                            reason = legacyReason,
                            field =
                                ApiHarnessClient.FindStringRecursive(
                                    legacy.Json,
                                    "field"),
                            zeroWrite = true,
                            noOrphan = legacyState.NoOrphan,
                            rawPayloadRecorded = false
                        });
                    }
                    finally
                    {
                        await versions.UpdateOneAsync(
                            item => item.Id == version.Id,
                            Builders<DynamicFlowTemplateVersion>.Update
                                .Set(
                                    item => item.MigrationState,
                                    version.MigrationState),
                            cancellationToken: CancellationToken.None);
                    }

                    var historicalVersion = await versions
                        .Find(item =>
                            item.Id == historicalFixture.VersionId)
                        .SingleAsync(CancellationToken.None);
                    try
                    {
                        await instances.UpdateOneAsync(
                            item => item.Id == instance.Id,
                            Builders<DynamicFlowInstance>.Update
                                .Set(
                                    item => item.FlowTemplateId,
                                    historicalVersion.TemplateId)
                                .Set(
                                    item => item.FlowTemplateVersionId,
                                    historicalVersion.Id)
                                .Set(
                                    item => item.FlowTemplateVersionNo,
                                    historicalVersion.VersionNo)
                                .Set(
                                    item => item.FlowPayloadHash,
                                    historicalVersion.PayloadHash)
                                .Set(
                                    item => item.CatalogVersion,
                                    historicalVersion.CatalogVersion)
                                .Set(
                                    item => item.CatalogSemanticHash,
                                    historicalVersion.CatalogSemanticHash),
                            cancellationToken: CancellationToken.None);
                        var historicalBefore =
                            await CaptureP7MappingPersistenceStateAsync(
                                database,
                                scenario.TargetReportId,
                                CancellationToken.None);
                    var historical = await PreviewP7MappingAsync(
                        api,
                        scenario,
                        new JsonObject(),
                        CancellationToken.None);
                    Require(
                        historical.StatusCode is
                            HttpStatusCode.Conflict or
                            HttpStatusCode.ServiceUnavailable,
                        $"P7-04 historical v1.3 barrier expected HTTP 409/503, got {(int)historical.StatusCode}.");
                    var historicalReason =
                        ApiHarnessClient.FindStringRecursive(
                            historical.Json,
                            "reason");
                    Require(
                        historicalReason ==
                        "DYNAMIC_FLOW_MAPPING_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                        "P7-04 exact historical v1.3 pins were implicitly upgraded to P7.");
                    var historicalState =
                        await CaptureP7MappingPersistenceStateAsync(
                            database,
                            scenario.TargetReportId,
                            CancellationToken.None);
                    Require(
                        historicalBefore.Fingerprint ==
                        historicalState.Fingerprint,
                        "P7-04 historical v1.3 barrier changed target/mapping persistence.");
                    mongoEvidence.Add(
                        await CaptureP7MappingNegativeMongoEvidenceAsync(
                            database,
                            scenario.TargetReportId,
                            "P7-04-HISTORICAL-V1.3-ZERO-WRITE",
                            historicalBefore,
                            CancellationToken.None));
                    barrierOutcomes.Add(new
                    {
                        barrier = "HISTORICAL_V1_3",
                        status = (int)historical.StatusCode,
                        errorCode =
                            ApiHarnessClient.FindStringRecursive(
                                historical.Json,
                                "errorCode"),
                        reason = historicalReason,
                        field =
                            ApiHarnessClient.FindStringRecursive(
                                historical.Json,
                                "field"),
                        flowVersionId = historicalVersion.Id,
                        flowPayloadHash = historicalVersion.PayloadHash,
                        catalogVersion = historicalVersion.CatalogVersion,
                        catalogSemanticHash =
                            historicalVersion.CatalogSemanticHash,
                        zeroWrite = true,
                        noOrphan = historicalState.NoOrphan,
                        rawPayloadRecorded = false
                    });
                    }
                    finally
                    {
                        await instances.UpdateOneAsync(
                            item => item.Id == instance.Id,
                            Builders<DynamicFlowInstance>.Update
                                .Set(
                                    item => item.FlowTemplateId,
                                    instance.FlowTemplateId)
                                .Set(
                                    item => item.FlowTemplateVersionId,
                                    instance.FlowTemplateVersionId)
                                .Set(
                                    item => item.FlowTemplateVersionNo,
                                    instance.FlowTemplateVersionNo)
                                .Set(
                                    item => item.FlowPayloadHash,
                                    instance.FlowPayloadHash)
                                .Set(
                                    item => item.CatalogVersion,
                                    instance.CatalogVersion)
                                .Set(
                                    item => item.CatalogSemanticHash,
                                    instance.CatalogSemanticHash),
                            cancellationToken: CancellationToken.None);
                    }

                    try
                    {
                        await instances.UpdateOneAsync(
                            item => item.Id == instance.Id,
                            Builders<DynamicFlowInstance>.Update
                                .Set(
                                    item => item.CatalogSemanticHash,
                                    string.Empty),
                            cancellationToken: CancellationToken.None);
                        var missing = await PreviewP7MappingAsync(
                            api,
                            scenario,
                            new JsonObject(),
                            CancellationToken.None);
                        Require(
                            missing.StatusCode is
                                HttpStatusCode.Conflict or
                                HttpStatusCode.ServiceUnavailable,
                            $"P7-04 missing catalog pin barrier expected HTTP 409/503, got {(int)missing.StatusCode}.");
                        var missingReason =
                            ApiHarnessClient.FindStringRecursive(
                                missing.Json,
                                "reason");
                        Require(
                            missingReason ==
                            "DYNAMIC_FLOW_MAPPING_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                            "P7-04 mixed/missing catalog pins were implicitly upgraded.");
                        var missingState =
                            await CaptureP7MappingPersistenceStateAsync(
                                database,
                                scenario.TargetReportId,
                                CancellationToken.None);
                        Require(
                            before == missingState.Fingerprint,
                            "P7-04 mixed/missing pin barrier changed target/mapping persistence.");
                        barrierOutcomes.Add(new
                        {
                            barrier = "MIXED_MISSING_PINS",
                            status = (int)missing.StatusCode,
                            errorCode =
                                ApiHarnessClient.FindStringRecursive(
                                    missing.Json,
                                    "errorCode"),
                            reason = missingReason,
                            field =
                                ApiHarnessClient.FindStringRecursive(
                                    missing.Json,
                                    "field"),
                            zeroWrite = true,
                            noOrphan = missingState.NoOrphan,
                            rawPayloadRecorded = false
                        });
                    }
                    finally
                    {
                        await instances.UpdateOneAsync(
                            item => item.Id == instance.Id,
                            Builders<DynamicFlowInstance>.Update
                                .Set(
                                    item => item.CatalogVersion,
                                    instance.CatalogVersion)
                                .Set(
                                    item => item.CatalogSemanticHash,
                                    instance.CatalogSemanticHash),
                            cancellationToken: CancellationToken.None);
                    }

                    var after = await CaptureP7MappingWriteSnapshotAsync(
                        database,
                        scenario.TargetReportId,
                        CancellationToken.None);
                    Require(
                        before == after,
                        "P7-04 historical/legacy/mixed-pin barriers changed target/mapping persistence.");
                    mongoEvidence.Add(
                        await CaptureP7MappingNegativeMongoEvidenceAsync(
                            database,
                            scenario.TargetReportId,
                            "P7-04-HISTORICAL-LEGACY-MIXED-PIN-BARRIERS",
                            beforeMongo,
                            CancellationToken.None));
                    mongoEvidence.Add(new
                    {
                        caseId =
                            "P7-04-HISTORICAL-LEGACY-MIXED-PIN-BARRIERS-OUTCOMES",
                        outcomes = barrierOutcomes.ToArray(),
                        rawPayloadRecorded = false
                    });
                    return new CaseObservation(
                        "REQUIRES_REVIEW, exact historical v1.3, and missing/mixed catalog pins all stayed blocked with no implicit upgrade and no mapping write.",
                        P7MappingFingerprint(
                            "P7-04",
                            "legacy-pin-barriers",
                            before,
                            after));
                });

            await cases.RunAsync(
                "P7-04-NEGATIVE-MONGO-NO-ORPHAN",
                async () =>
                {
                    var scenario = canonicalScenario
                                   ?? throw new HarnessCaseNotRunnableException(
                                       "P7-04 canonical scenario did not initialize.");
                    var beforeMongo =
                        await CaptureP7MappingPersistenceStateAsync(
                            database,
                            scenario.TargetReportId,
                            CancellationToken.None);
                    var evidence =
                        await CaptureP7MappingNegativeMongoEvidenceAsync(
                            database,
                            scenario.TargetReportId,
                            "P7-04-NEGATIVE-MONGO-NO-ORPHAN",
                            beforeMongo,
                            CancellationToken.None);
                    mongoEvidence.Add(evidence);
                    return new CaseObservation(
                        "Direct Mongo confirmed all preceding P7-04 negative paths left no receipt, provenance, mapping event/outbox/audit, or dangling report header reference.",
                        P7MappingFingerprint(
                            "P7-04",
                            "negative-no-orphan",
                            scenario.TargetReportId));
                });

            if (through >= 5)
            {
                await RunP705MappingCasesAsync(
                    cases,
                    mongoEvidence,
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    mappingFixture,
                    through,
                    CancellationToken.None);
            }

            if (through >= 6)
            {
                await RunP706MappingCasesAsync(
                    cases,
                    mongoEvidence,
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    mappingFixture,
                    through,
                    CancellationToken.None);
            }

            if (through >= 7)
            {
                await RunP707MappingCasesAsync(
                    cases,
                    mongoEvidence,
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    mappingFixture,
                    through,
                    CancellationToken.None);
            }

            if (through >= 8)
            {
                await RunP708MappingCasesAsync(
                    cases,
                    mongoEvidence,
                    paths,
                    iterationRoot,
                    runKey,
                    mongo,
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    mappingFixture,
                    through,
                    CancellationToken.None);
            }

            if (through >= 9)
            {
                if (through >= 11)
                {
                    await RunP711ChaosCasesAsync(
                        cases,
                        mongoEvidence,
                        additionalApiEvidence,
                        paths,
                        iterationRoot,
                        runKey,
                        mongo,
                        api,
                        backend,
                        database,
                        adminToken,
                        baseFixture,
                        mappingFixture,
                        CancellationToken.None);
                }

                await RunP709MappingCasesAsync(
                    cases,
                    mongoEvidence,
                    api,
                    backend,
                    database,
                    adminToken,
                    baseFixture,
                    mappingFixture,
                    CancellationToken.None);

                if (through >= 11)
                {
                    mongoEvidence.Add(
                        await CaptureP711MongoGlobalSummaryAsync(
                            database,
                            CancellationToken.None));
                }
            }
        }
        catch (Exception error)
        {
            failure = $"{error.GetType().Name}: {error.Message}";
            Console.Error.WriteLine(error);
        }
        finally
        {
            api?.Dispose();
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

        var cleanupPassed = cleanupErrors.Count == 0 &&
                            backend is not null &&
                            backend.StopVerified &&
                            backend.PortReleaseVerified &&
                            mongo is not null &&
                            mongo.DatabaseDropVerified &&
                            mongo.ProcessStopVerified &&
                            mongo.PortReleaseVerified &&
                            mongo.DataDirectoryRemovalVerified;
        var passed = failure is null &&
                     cleanupPassed &&
                     cases.Results.Count > 0 &&
                     cases.Results.All(result =>
                         result.Verdict == HarnessVerdict.DAT);
        var normalizedSha256 = cases.BuildNormalizedSha256();
        var completedAtUtc = DateTime.UtcNow;
        var resultPayload = new
        {
            schemaVersion = 1,
            gate = "P7-MAPPING",
            through = $"P7-{through:00}",
            runKey,
            startedAtUtc,
            completedAtUtc,
            databaseName = mongo?.DatabaseName,
            replicaSetName = mongo?.ReplicaSetName,
            caseCount = cases.Results.Count,
            cases = cases.Results,
            normalizedSha256,
            cleanupPassed,
            cleanupErrors,
            passed,
            failure
        };
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, P7MappingResultFileName),
            resultPayload);
        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot, P7MappingResultFileName),
            resultPayload);
        await EvidenceCsv.WriteCasesAsync(
            Path.Combine(iterationRoot, "p7-mapping-gate-results.csv"),
            cases.Results.Select(result => (1, result)));
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, P7MappingMongoFileName),
            new
            {
                schemaVersion = 1,
                gate = "P7-MAPPING",
                through = $"P7-{through:00}",
                observations = mongoEvidence
            });
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, P7MappingApiFileName),
            new
            {
                schemaVersion = 1,
                gate = "P7-MAPPING",
                through = $"P7-{through:00}",
                exchanges = (api?.Exchanges ??
                             Array.Empty<ApiExchangeEvidence>())
                    .Concat(additionalApiEvidence)
                    .Select((exchange, index) => new
                    {
                        Sequence = index + 1,
                        sourceSequence = exchange.Sequence,
                        exchange.Method,
                        exchange.Path,
                        exchange.StatusCode,
                        exchange.CaseId,
                        requestBodyRecorded = false,
                        responseBodyRecorded = false
                    })
                    .ToArray()
            });
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "cleanup-manifest.json"),
            new
            {
                schemaVersion = 1,
                gate = "P7-MAPPING",
                runKey,
                state = cleanupPassed ? "CLEANED" : "CLEANUP_FAILED",
                databaseDropped = mongo?.DatabaseDropVerified ?? false,
                backendStopped = backend?.StopVerified ?? false,
                backendPortReleased = backend?.PortReleaseVerified ?? false,
                mongoStopped = mongo?.ProcessStopVerified ?? false,
                mongoPortReleased = mongo?.PortReleaseVerified ?? false,
                mongoDataRemoved =
                    mongo?.DataDirectoryRemovalVerified ?? false,
                cleanupErrors,
                completedAtUtc
            });

        Console.WriteLine(
            passed
                ? $"[PASS] P7 mapping gate through P7-{through:00} passed {cases.Results.Count} case(s); artifact={Path.Combine(iterationRoot, P7MappingResultFileName)}"
                : $"[FAIL] P7 mapping gate through P7-{through:00} failed: {failure ?? string.Join("; ", cleanupErrors)}; artifact={Path.Combine(iterationRoot, P7MappingResultFileName)}");
        return passed ? 0 : 1;
    }

    private static int ParseP7MappingStage(string[] args)
    {
        string? value = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith(
                    "--through=",
                    StringComparison.OrdinalIgnoreCase))
            {
                value = args[index]["--through=".Length..];
                break;
            }
            if (string.Equals(
                    args[index],
                    "--through",
                    StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length)
            {
                value = args[index + 1];
                break;
            }
        }

        value = string.IsNullOrWhiteSpace(value)
            ? "P7-09"
            : value.Trim().ToUpperInvariant();
        return value switch
        {
            "P7-04" => 4,
            "P7-05" => 5,
            "P7-06" => 6,
            "P7-07" => 7,
            "P7-08" => 8,
            "P7-09" => 9,
            "P7-11" => 11,
            _ => throw new ArgumentException(
                "--through must be one of P7-04, P7-05, P7-06, P7-07, P7-08, P7-09, or P7-11.")
        };
    }

    private static async Task<P7MappingFixture>
        CloneP601HistoricalP7FixtureAsync(
            IMongoDatabase database,
            P601Fixture p601,
            CancellationToken ct)
    {
        var familyId = ObjectId.GenerateNewId();
        var versionId = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        var families = database.GetCollection<BsonDocument>(
            "dynamic_flow_templates");
        var versions = database.GetCollection<BsonDocument>(
            "dynamic_flow_template_versions");
        var family = (await families
                .Find(new BsonDocument(
                    "_id",
                    ObjectId.Parse(p601.FamilyId)))
                .SingleAsync(ct))
            .DeepClone()
            .AsBsonDocument;
        var version = (await versions
                .Find(new BsonDocument(
                    "_id",
                    ObjectId.Parse(p601.VersionId)))
                .SingleAsync(ct))
            .DeepClone()
            .AsBsonDocument;

        family["_id"] = familyId;
        family["code"] =
            $"P7_HISTORICAL_V13_{familyId.ToString()[..8]}";
        family["name"] =
            "P7-04 exact historical locked v1.3 fixture";
        family["currentVersionId"] = versionId;
        family["currentVersionNo"] = 1;
        family["currentVersionHash"] = p601.PayloadHash;
        family["createdAtUtc"] = now;
        family["updatedAtUtc"] = now;

        version["_id"] = versionId;
        version["templateId"] = familyId;
        version["payloadJson"] = p601.CanonicalPayload;
        version["payloadHash"] = p601.PayloadHash;
        version["catalogVersion"] =
            DynamicFlowP6CatalogCandidate.Version;
        version["catalogSemanticHash"] =
            DynamicFlowP6CatalogCandidate.SemanticHash;
        version["migrationState"] =
            DynamicFlowDefinitionMigrationStates.Canonical;
        version["executionEligibility"] =
            DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase;
        version["executionBlockedReason"] =
            DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented;
        version["blockedUntilPhase"] = "P6";
        version["createdAtUtc"] = now;
        version["updatedAtUtc"] = now;

        await families.InsertOneAsync(family, cancellationToken: ct);
        await versions.InsertOneAsync(version, cancellationToken: ct);

        var rootForm = p601.ExpectedSteps.Single(step =>
            step.NodeId == "step_a").Form;
        var childForm = p601.ExpectedSteps.Single(step =>
            step.NodeId == "step_b").Form;
        return new P7MappingFixture(
            p601.WorkId,
            familyId.ToString(),
            versionId.ToString(),
            p601.PayloadHash,
            p601.TargetUnitId,
            p601.TargetUserId,
            p601.OutsiderUserId,
            rootForm,
            childForm);
    }

    private static async Task<P7MappingFixture> AdaptP601ToP7MappingAsync(
        IMongoDatabase database,
        P601Fixture p601,
        CancellationToken ct)
    {
        var rootForm = p601.ExpectedSteps.Single(step =>
            step.NodeId == "step_a").Form;
        var childForm = p601.ExpectedSteps.Single(step =>
            step.NodeId == "step_b").Form;
        var payload = JsonNode.Parse(p601.CanonicalPayload)?.AsObject()
                      ?? throw new InvalidOperationException(
                          "P601 payload cannot be parsed for P7 adaptation.");
        payload["catalogVersion"] = DynamicFlowP7CatalogCandidate.Version;
        payload["catalogSemanticHash"] =
            DynamicFlowP7CatalogCandidate.SemanticHash;
        payload["entryStepId"] = "step_a";
        payload["fieldPolicies"] = new JsonArray
        {
            new JsonObject
            {
                ["policyId"] = "p7-source-note-wildcard-deny",
                ["dynamicFormTemplateId"] = rootForm.FormVersionId,
                ["stepId"] = "step_a",
                ["stepCode"] = "A",
                ["actorRole"] = "*",
                ["fieldId"] = "field_note",
                ["fieldKey"] = "note",
                ["read"] = false,
                ["write"] = false
            },
            new JsonObject
            {
                ["policyId"] = "p7-source-note-assignee-allow",
                ["dynamicFormTemplateId"] = rootForm.FormVersionId,
                ["stepId"] = "step_a",
                ["stepCode"] = "A",
                ["actorRole"] = "ASSIGNEE",
                ["fieldId"] = "field_note",
                ["fieldKey"] = "note",
                ["read"] = true,
                ["write"] = true
            },
            new JsonObject
            {
                ["policyId"] = "p7-target-child-wildcard-deny",
                ["dynamicFormTemplateId"] = childForm.FormVersionId,
                ["stepId"] = "step_b",
                ["stepCode"] = "B",
                ["actorRole"] = "*",
                ["fieldId"] = "field_child_value",
                ["fieldKey"] = "child_value",
                ["read"] = false,
                ["write"] = false
            },
            new JsonObject
            {
                ["policyId"] = "p7-target-child-assignee-allow",
                ["dynamicFormTemplateId"] = childForm.FormVersionId,
                ["stepId"] = "step_b",
                ["stepCode"] = "B",
                ["actorRole"] = "ASSIGNEE",
                ["fieldId"] = "field_child_value",
                ["fieldKey"] = "child_value",
                ["read"] = true,
                ["write"] = true
            }
        };
        payload["tableColumnPolicies"] = new JsonArray();
        payload["mappingRules"] = new JsonArray
        {
            new JsonObject
            {
                ["mappingId"] = "p7-note-to-child",
                ["mappingVersion"] = 1,
                ["mappingKind"] = "FIELD",
                ["dataType"] = "TEXT",
                ["conflictPolicy"] = "OVERWRITE",
                ["contributionPolicy"] = "INCLUDE",
                ["evaluationGrain"] = "FLOW_INSTANCE",
                ["errorPolicy"] = "BLOCK_APPLY",
                ["inputs"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["inputKey"] = "source_note",
                        ["dataType"] = "TEXT",
                        ["cardinality"] = "ONE",
                        ["nullPolicy"] = "ERROR",
                        ["source"] = new JsonObject
                        {
                            ["kind"] = "FIELD",
                            ["dynamicFormTemplateId"] =
                                rootForm.FormVersionId,
                            ["stepId"] = "step_a",
                            ["stepCode"] = "A",
                            ["fieldId"] = "field_note",
                            ["fieldKey"] = "note",
                            ["dataType"] = "TEXT"
                        }
                    }
                },
                ["target"] = new JsonObject
                {
                    ["kind"] = "FIELD",
                    ["dynamicFormTemplateId"] = childForm.FormVersionId,
                    ["stepId"] = "step_b",
                    ["stepCode"] = "B",
                    ["fieldId"] = "field_child_value",
                    ["fieldKey"] = "child_value",
                    ["dataType"] = "TEXT"
                },
                ["calculation"] = new JsonObject
                {
                    ["kind"] = "EXPRESSION",
                    ["operation"] = "copy",
                    ["resultDataType"] = "TEXT",
                    ["expression"] = new JsonObject
                    {
                        ["op"] = "copy",
                        ["args"] = new JsonArray
                        {
                            new JsonObject { ["input"] = "source_note" }
                        }
                    }
                }
            }
        };

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
        var versions = database.GetCollection<DynamicFlowTemplateVersion>(
            "dynamic_flow_template_versions");
        await versions.UpdateOneAsync(
            item => item.Id == p601.VersionId,
            Builders<DynamicFlowTemplateVersion>.Update
                .Set(item => item.PayloadJson, canonical.CanonicalJson)
                .Set(item => item.PayloadHash, canonical.PayloadHash)
                .Set(
                    item => item.CatalogVersion,
                    DynamicFlowP7CatalogCandidate.Version)
                .Set(
                    item => item.CatalogSemanticHash,
                    DynamicFlowP7CatalogCandidate.SemanticHash)
                .Set(
                    item => item.ExecutionEligibility,
                    DynamicFlowExecutionEligibilities
                        .BlockedUntilTargetPhase)
                .Set(
                    item => item.ExecutionBlockedReason,
                    DynamicFlowExecutionBlockedReasons
                        .TargetPhaseNotImplemented)
                .Set(item => item.BlockedUntilPhase, "P7")
                .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
            cancellationToken: ct);
        await database.GetCollection<DynamicFlowTemplate>(
                "dynamic_flow_templates")
            .UpdateOneAsync(
                item => item.Id == p601.FamilyId,
                Builders<DynamicFlowTemplate>.Update
                    .Set(item => item.CurrentVersionHash, canonical.PayloadHash)
                    .Set(item => item.RootDynamicFormTemplateId, rootForm.FormVersionId)
                    .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken: ct);
        return new P7MappingFixture(
            p601.WorkId,
            p601.FamilyId,
            p601.VersionId,
            canonical.PayloadHash,
            p601.TargetUnitId,
            p601.TargetUserId,
            p601.OutsiderUserId,
            rootForm,
            childForm);
    }

    private static async Task<string> CloneP7MappingWorkAsync(
        IMongoDatabase database,
        string sourceWorkId,
        string suffix,
        CancellationToken ct)
    {
        var works = database.GetCollection<BsonDocument>("works");
        var source = await works
            .Find(new BsonDocument("_id", ObjectId.Parse(sourceWorkId)))
            .SingleAsync(ct);
        var clone = source.DeepClone().AsBsonDocument;
        var id = ObjectId.GenerateNewId();
        clone["_id"] = id;
        clone["autoCode"] = $"P7-MAPPING-{suffix}-{id.ToString()[..8]}";
        clone["code"] = $"P7-{suffix}-{id.ToString()[..6]}";
        clone["name"] = $"P7 mapping gate {suffix}";
        clone["createdAtUtc"] = DateTime.UtcNow;
        clone["updatedAtUtc"] = DateTime.UtcNow;
        await works.InsertOneAsync(clone, cancellationToken: ct);
        return id.ToString();
    }

    private static async Task<P7MappingScenario>
        PrepareP7MappingScenarioAsync(
            ApiHarnessClient api,
            BackendServerLease backend,
            IMongoDatabase database,
            string adminToken,
            ProbeFixture baseFixture,
            P7MappingFixture fixture,
            string workId,
            string launchCommandId,
            string periodKey,
            CancellationToken ct)
    {
        var launch = await LaunchAsync(
            api,
            adminToken,
            baseFixture,
            workId,
            fixture.VersionId,
            launchCommandId,
            new[] { fixture.TargetUnitId },
            P601PeriodKey,
            ct);
        RequireStatus(launch.Confirm, "SUCCEEDED", launchCommandId);
        var instanceId = ApiHarnessClient.RequiredString(
            launch.Confirm.Json,
            "flowInstanceId");
        var steps = await LoadP601StepsAsync(database, instanceId, ct);
        Require(
            steps.Count == 1 &&
            steps[0].FlowStepId == "step_a",
            "P7 mapping launch did not materialize exactly entry step A.");
        var sourceStep = await ApproveP601StepAsync(
            api,
            backend,
            adminToken,
            database,
            steps[0],
            "field_note",
            P7MappingHiddenSourceValue,
            $"{launchCommandId}-source",
            ct);
        var instance = await LoadP601InstanceAsync(database, instanceId, ct);
        var forward = await ForwardP601Async(
            api,
            adminToken,
            workId,
            sourceStep.AssignmentId!,
            $"{launchCommandId}-forward",
            instance.Revision,
            sourceStep.Revision,
            ct);
        AssertP601ForwardSuccess(
            forward,
            replayed: false,
            $"{launchCommandId} A -> B");
        DynamicFlowStepInstance targetStep = null!;
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            targetStep = (await LoadP601StepsAsync(database, instanceId, ct))
                .Single(step => step.FlowStepId == "step_b");
            if (!string.IsNullOrWhiteSpace(targetStep.AssignmentId))
                break;
            await Task.Delay(50, ct);
        }
        Require(
            !string.IsNullOrWhiteSpace(targetStep.AssignmentId),
            "P7 mapping target assignment did not materialize.");
        await WaitForP601AssignmentAsync(
            database,
            targetStep.AssignmentId!,
            ct);
        var targetPeriod = await WaitForP601PeriodAsync(
            database,
            targetStep.AssignmentId!,
            ct);
        var actorUserId = targetStep.ParticipantUserIds.Single();
        var actorToken = await PrepareP601ActorLoginAsync(
            api,
            backend,
            database,
            actorUserId,
            ct);
        var open = await api.PostAsync(
            $"api/work-report-periods/{targetPeriod.Id}/open",
            body: null,
            actorToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            open,
            HttpStatusCode.OK,
            $"{launchCommandId} open target report");
        var targetReportId = ApiHarnessClient.RequiredString(open.Json, "id");
        targetStep = await WaitForP7MappingTargetReportPinAsync(
            api,
            adminToken,
            database,
            targetStep.Id,
            targetReportId,
            ct);
        var sourceReportId = sourceStep.ReportId
                             ?? throw new InvalidOperationException(
                                 "P7 mapping source step lacks approved report.");
        return new P7MappingScenario(
            workId,
            instanceId,
            sourceStep.Id,
            targetStep.Id,
            sourceStep.AssignmentId!,
            targetStep.AssignmentId!,
            sourceReportId,
            targetReportId,
            targetPeriod.Id,
            actorUserId,
            actorToken);
    }

    private static async Task ReassignP7MappingTargetAsync(
        IMongoDatabase database,
        DynamicFlowStepInstance targetStep,
        string actorUserId,
        CancellationToken ct)
    {
        var user = await database.GetCollection<AppUser>("users")
            .Find(item => item.Id == actorUserId && !item.IsDeleted)
            .SingleAsync(ct);
        var unit = await database.GetCollection<Unit>("units")
            .Find(item => item.Id == user.UnitId && !item.IsDeleted)
            .SingleAsync(ct);
        var actor = new UserRef
        {
            UserId = user.Id,
            Username = user.Username,
            FullName = user.FullName,
            UnitId = user.UnitId,
            UnitSymbol = unit.Symbol,
            UnitShortName = unit.ShortName,
            UnitName = unit.FullName,
            PositionCode = user.PositionCode
        };
        await database.GetCollection<WorkAssignment>("work_assignments")
            .UpdateOneAsync(
                item => item.Id == targetStep.AssignmentId,
                Builders<WorkAssignment>.Update
                    .Set(item => item.Assignees, new List<UserRef> { actor })
                    .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken: ct);
        await database.GetCollection<WorkTemplateAssignee>(
                "work_template_assignees")
            .UpdateManyAsync(
                item =>
                    item.WorkAssignmentId == targetStep.AssignmentId &&
                    !item.IsDeleted,
                Builders<WorkTemplateAssignee>.Update
                    .Set(item => item.AssigneeUserId, user.Id)
                    .Set(item => item.AssigneeUsername, user.Username)
                    .Set(item => item.AssigneeFullName, user.FullName)
                    .Set(item => item.AssigneeUnitId, user.UnitId)
                    .Set(item => item.AssigneeUnitSymbol, unit.Symbol)
                    .Set(item => item.AssigneeUnitShortName, unit.ShortName)
                    .Set(item => item.AssigneeUnitName, unit.FullName)
                    .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken: ct);
        await database.GetCollection<AssignmentListDocRole>(
                "assignment_list_doc_roles")
            .UpdateManyAsync(
                item =>
                    item.AssignmentId == targetStep.AssignmentId &&
                    item.Roles.Contains(DocRoleType.ASSIGNEE) &&
                    !item.IsDeleted,
                Builders<AssignmentListDocRole>.Update
                    .Set(item => item.UserId, user.Id)
                    .Set(item => item.User, actor)
                    .Set(item => item.Assignees, new List<UserRef> { actor })
                    .Set(
                        item => item.AssigneeUserIds,
                        new List<string> { user.Id })
                    .Set(
                        item => item.AssigneeUnitIds,
                        new List<string> { user.UnitId })
                    .Set(item => item.FirstAssigneeName, user.FullName)
                    .Set(item => item.FirstAssigneeUnitName, unit.FullName)
                    .AddToSet(item => item.VisibleUnitIds, user.UnitId)
                    .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken: ct);
        await database.GetCollection<WorkReportPeriod>(
                "work_report_periods")
            .UpdateManyAsync(
                item => item.WorkAssignmentId == targetStep.AssignmentId,
                Builders<WorkReportPeriod>.Update
                    .Set(item => item.AssigneeUserId, user.Id)
                    .Set(item => item.AssigneeUnitId, user.UnitId)
                    .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken: ct);
        await database.GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .UpdateOneAsync(
                item => item.Id == targetStep.Id,
                Builders<DynamicFlowStepInstance>.Update
                    .Set(
                        item => item.ParticipantUserIds,
                        new List<string> { user.Id })
                    .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken: ct);
    }

    private static async Task<DynamicFlowStepInstance>
        WaitForP7MappingTargetReportPinAsync(
            ApiHarnessClient api,
            string adminToken,
            IMongoDatabase database,
            string stepId,
            string reportId,
            CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            var step = await database
                .GetCollection<DynamicFlowStepInstance>(
                    "dynamic_flow_step_instances")
                .Find(item => item.Id == stepId)
                .SingleAsync(ct);
            if (step.ReportId == reportId &&
                step.ReportLifecycleStatus == "DRAFT")
            {
                return step;
            }
            var worker = await api.PostAsync(
                "api/admin/operations/job-runs/lifecycle-projection-outbox/process?maxReports=20",
                body: null,
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                worker,
                HttpStatusCode.OK,
                "P7 mapping target report pin worker");
            await Task.Delay(50, ct);
        }
        throw new InvalidOperationException(
            $"P7 mapping target step {stepId} did not pin draft report {reportId}.");
    }

    private static Task<ApiHarnessResponse> PreviewP7MappingAsync(
        ApiHarnessClient api,
        P7MappingScenario scenario,
        JsonObject request,
        CancellationToken ct)
        => api.PostAsync(
            $"api/work-assignment-reports/{scenario.TargetReportId}/draft/preview-dynamic-flow-mapping",
            request,
            scenario.ActorToken,
            ct: ct);

    private static void AssertP7MappingPreviewIdentity(
        ApiHarnessResponse response,
        P7MappingFixture fixture,
        P7MappingScenario scenario,
        int activationThrough = 9)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            "P7 mapping preview");
        Require(
            ApiHarnessClient.RequiredString(response.Json, "targetReportId") ==
            scenario.TargetReportId,
            "P7 preview target report drift.");
        Require(
            ApiHarnessClient.RequiredString(response.Json, "flowFamilyId") ==
            fixture.FamilyId &&
            ApiHarnessClient.RequiredString(response.Json, "flowVersionId") ==
            fixture.VersionId &&
            ApiHarnessClient.RequiredString(response.Json, "flowPayloadHash") ==
            fixture.PayloadHash,
            "P7 preview flow pins drift.");
        Require(
            ApiHarnessClient.RequiredString(response.Json, "catalogVersion") ==
            DynamicFlowP7CatalogCandidate.Version &&
            ApiHarnessClient.RequiredString(
                response.Json,
                "catalogSemanticHash") ==
            DynamicFlowP7CatalogCandidate.SemanticHash,
            "P7 preview catalog pins drift.");
        Require(
            ApiHarnessClient.RequiredString(response.Json, "flowInstanceId") ==
            scenario.FlowInstanceId &&
            ApiHarnessClient.RequiredString(response.Json, "stepId") ==
            "step_b" &&
            ApiHarnessClient.RequiredInt(response.Json, "executionEpoch") == 1,
            "P7 preview runtime pins drift.");
        Require(
            ApiHarnessClient.RequiredString(response.Json, "formVersionId") ==
            fixture.ChildForm.FormVersionId &&
            ApiHarnessClient.RequiredString(response.Json, "formSchemaHash") ==
            fixture.ChildForm.FormSchemaHash,
            "P7 preview target form pins drift.");
        Require(
            IsP7LowerSha256(
                ApiHarnessClient.RequiredString(
                    response.Json,
                    "sourceSignature")) &&
            IsP7LowerSha256(
                ApiHarnessClient.RequiredString(
                    response.Json,
                    "resultSemanticHash")),
            "P7 preview signature/result bindings are incomplete.");
        if (activationThrough >= 7)
        {
            Require(
                !string.IsNullOrWhiteSpace(
                    ApiHarnessClient.RequiredString(
                        response.Json,
                        "previewToken")) &&
                response.Json?["previewIssuedAtUtc"] is not null &&
                response.Json?["previewExpiresAtUtc"] is not null,
                "P7 preview-token slice did not expose its signed token/timestamps.");
        }
        else
        {
            Require(
                response.Json?["previewToken"] is null &&
                response.Json?["previewIssuedAtUtc"] is null &&
                response.Json?["previewExpiresAtUtc"] is null,
                "P7 pre-token slice exposed a preview token or token timestamp.");
        }
        var hasBlockingConflicts =
            ApiHarnessClient.RequiredBool(
                response.Json,
                "hasBlockingConflicts");
        var expectedCanApply =
            activationThrough >= 8 &&
            !hasBlockingConflicts;
        Require(
            ApiHarnessClient.RequiredBool(response.Json, "canPreview") &&
            ApiHarnessClient.RequiredBool(response.Json, "canApply") ==
            expectedCanApply,
            "P7 preview capability slice drifted; " +
            $"activationThrough={activationThrough};" +
            $"canPreview={ApiHarnessClient.RequiredBool(response.Json, "canPreview")};" +
            $"canApply={ApiHarnessClient.RequiredBool(response.Json, "canApply")};" +
            $"blocking={hasBlockingConflicts};" +
            $"changes={DescribeP7MappingChangeOutcomes(response.Json)}.");
    }

    private static string DescribeP7MappingChangeOutcomes(JsonNode? response)
    {
        if (response?["changes"] is not JsonArray changes)
            return "<none>";
        return string.Join(
            ",",
            changes
                .OfType<JsonObject>()
                .Select(change =>
                    $"{change["mappingId"]?.GetValue<string>() ?? "<id>"}:" +
                    $"{change["status"]?.GetValue<string>() ?? "<status>"}:" +
                    $"{change["reason"]?.GetValue<string>() ?? "<reason>"}"));
    }

    private static async Task<string> CaptureP7MappingWriteSnapshotAsync(
        IMongoDatabase database,
        string targetReportId,
        CancellationToken ct)
    {
        var state = await CaptureP7MappingPersistenceStateAsync(
            database,
            targetReportId,
            ct);
        return state.Fingerprint;
    }

    private static async Task<P7MappingPersistenceState>
        CaptureP7MappingPersistenceStateAsync(
            IMongoDatabase database,
            string targetReportId,
            CancellationToken ct)
    {
        var report = await database
            .GetCollection<WorkAssignmentReport>("work_assignment_report")
            .Find(item => item.Id == targetReportId)
            .SingleAsync(ct);
        var targetId = ObjectId.Parse(targetReportId);
        async Task<long> CountAsync(
            string collection,
            BsonDocument filter)
            => await database
                .GetCollection<BsonDocument>(collection)
                .CountDocumentsAsync(filter, cancellationToken: ct);
        var receiptCount = await CountAsync(
            "dynamic_flow_mapping_apply_receipts",
            new BsonDocument("targetReportId", targetId));
        var provenanceCount = await CountAsync(
            "dynamic_flow_mapping_provenance",
            new BsonDocument("targetReportId", targetId));
        var eventCount = await CountAsync(
            "dynamic_flow_mapping_events",
            new BsonDocument("targetReportId", targetId));
        var outboxCount = await CountAsync(
            "dynamic_flow_mapping_outbox",
            new BsonDocument("targetReportId", targetId));
        var reportLogCount = await CountAsync(
            "work_assignment_report_logs",
            new BsonDocument(
                "workAssignmentReportId",
                targetId));
        var mappingAuditCount = await CountAsync(
            "work_assignment_report_logs",
            new BsonDocument
            {
                ["workAssignmentReportId"] = targetId,
                ["action"] = new BsonDocument(
                    "$in",
                    new BsonArray
                    {
                        "APPLY_DYNAMIC_FLOW_MAPPING",
                        "RERUN_DYNAMIC_FLOW_MAPPING"
                    })
            });
        var payloadCount = await CountAsync(
            "work_report_payloads",
            new BsonDocument("reportId", targetId));
        var sectionCount = await CountAsync(
            "work_assignment_report_sections",
            new BsonDocument(
                "workAssignmentReportId",
                targetId));
        var lifecycleProjectionOutboxFingerprint =
            P7MappingFingerprint(
                report.LifecycleProjectionOutbox
                    .Select(entry => string.Join(
                        ":",
                        entry.EntryKey,
                        entry.CommandId,
                        entry.LifecycleRevision,
                        entry.Operation,
                        entry.State,
                        entry.FromStatus,
                        entry.ToStatus,
                        entry.FromIsActive,
                        entry.ToIsActive,
                        entry.PayloadRevision,
                        entry.PayloadHash,
                        entry.DynamicFlowMappingReceiptId,
                        entry.DynamicFlowMappingProvenanceId,
                        entry.DynamicFlowMappingProvenanceHash,
                        entry.DynamicFlowMappingResultPayloadRevision,
                        entry.DynamicFlowMappingResultPayloadHash,
                        entry.CreatedAtUtc.Ticks,
                        entry.LastAttemptedAtUtc?.Ticks,
                        entry.AttemptCount,
                        entry.CompletedAtUtc?.Ticks,
                        entry.LastError,
                        entry.BusinessEvents.Count))
                    .ToArray());
        var fingerprint = P7MappingFingerprint(
            report.PayloadRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            report.PayloadHash ?? "<null>",
            report.PayloadSizeBytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            report.PayloadStatus ?? "<null>",
            report.PayloadUpdatedAtUtc?.Ticks.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
                ?? "<null>",
            report.PayloadMutationCommandId ?? "<null>",
            report.PayloadMutationCommandHash ?? "<null>",
            report.PayloadMutationOperation ?? "<null>",
            report.PayloadMutationStartedAtUtc?.Ticks.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
                ?? "<null>",
            report.LastPayloadCommandId ?? "<null>",
            report.LastPayloadCommandHash ?? "<null>",
            report.LastPayloadCommandOperation ?? "<null>",
            report.LastPayloadCommandRevision?.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
                ?? "<null>",
            report.LifecycleRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            report.Status.ToString(),
            report.IsActive.ToString(),
            report.IsCurrent.ToString(),
            report.DynamicFlowMappingReceiptId ?? "<null>",
            report.DynamicFlowMappingProvenanceId ?? "<null>",
            report.DynamicFlowMappingProvenanceHash ?? "<null>",
            report.DynamicFlowMappingResultPayloadRevision?.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
                ?? "<null>",
            report.DynamicFlowMappingResultPayloadHash ?? "<null>",
            report.LastLifecycleCommandId ?? "<null>",
            report.LastLifecycleCommandHash ?? "<null>",
            report.LastLifecycleCommandOperation ?? "<null>",
            report.LastLifecycleCommandRevision?.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
                ?? "<null>",
            report.LastLifecycleCommandPayloadRevision?.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
                ?? "<null>",
            report.LastLifecycleCommandStatus?.ToString() ?? "<null>",
            report.LastLifecycleCommandIsActive?.ToString() ?? "<null>",
            lifecycleProjectionOutboxFingerprint,
            receiptCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            provenanceCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            eventCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            outboxCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            reportLogCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            mappingAuditCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            payloadCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            sectionCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        var noOrphan =
            receiptCount == 0 &&
            provenanceCount == 0 &&
            eventCount == 0 &&
            outboxCount == 0 &&
            mappingAuditCount == 0 &&
            string.IsNullOrWhiteSpace(
                report.DynamicFlowMappingReceiptId) &&
            string.IsNullOrWhiteSpace(
                report.DynamicFlowMappingProvenanceId);
        return new P7MappingPersistenceState(
            report.PayloadRevision,
            report.PayloadHash,
            report.PayloadSizeBytes,
            report.PayloadStatus,
            report.PayloadUpdatedAtUtc,
            report.PayloadMutationCommandId,
            report.PayloadMutationCommandHash,
            report.PayloadMutationOperation,
            report.LastPayloadCommandId,
            report.LastPayloadCommandHash,
            report.LastPayloadCommandOperation,
            report.LastPayloadCommandRevision,
            report.LifecycleRevision,
            report.Status.ToString(),
            report.IsActive,
            report.IsCurrent,
            report.DynamicFlowMappingReceiptId,
            report.DynamicFlowMappingProvenanceId,
            report.DynamicFlowMappingProvenanceHash,
            report.DynamicFlowMappingResultPayloadRevision,
            report.DynamicFlowMappingResultPayloadHash,
            report.LastLifecycleCommandId,
            report.LastLifecycleCommandRevision,
            report.LifecycleProjectionOutbox.Count,
            lifecycleProjectionOutboxFingerprint,
            receiptCount,
            provenanceCount,
            eventCount,
            outboxCount,
            reportLogCount,
            mappingAuditCount,
            payloadCount,
            sectionCount,
            noOrphan,
            fingerprint);
    }

    private static async Task<object> CaptureP7MappingNegativeMongoEvidenceAsync(
        IMongoDatabase database,
        string targetReportId,
        string caseId,
        P7MappingPersistenceState before,
        CancellationToken ct)
    {
        var after = await CaptureP7MappingPersistenceStateAsync(
            database,
            targetReportId,
            ct);
        var zeroWrite = before.Fingerprint == after.Fingerprint;
        Require(
            zeroWrite,
            $"{caseId} direct-Mongo before/after persistence changed.");
        Require(
            before.NoOrphan && after.NoOrphan,
            $"{caseId} left a mapping receipt/provenance/event/outbox/audit/header orphan.");
        return new
        {
            caseId,
            targetReportId,
            before,
            after,
            zeroWrite,
            noOrphan = before.NoOrphan && after.NoOrphan,
            rawPayloadRecorded = false
        };
    }

    private static async Task<object> CaptureP7MappingScenarioEvidenceAsync(
        IMongoDatabase database,
        P7MappingScenario scenario,
        string caseId,
        CancellationToken ct)
    {
        var instance = await LoadP601InstanceAsync(
            database,
            scenario.FlowInstanceId,
            ct);
        var steps = await LoadP601StepsAsync(
            database,
            scenario.FlowInstanceId,
            ct);
        var source = await database
            .GetCollection<WorkAssignmentReport>("work_assignment_report")
            .Find(item => item.Id == scenario.SourceReportId)
            .SingleAsync(ct);
        var target = await database
            .GetCollection<WorkAssignmentReport>("work_assignment_report")
            .Find(item => item.Id == scenario.TargetReportId)
            .SingleAsync(ct);
        return new
        {
            caseId,
            flowInstanceId = scenario.FlowInstanceId,
            instance.ExecutionEpoch,
            instance.FlowTemplateVersionId,
            instance.FlowPayloadHash,
            instance.CatalogVersion,
            instance.CatalogSemanticHash,
            steps = steps.Select(step => new
            {
                step.Id,
                step.FlowStepId,
                step.FlowStepCode,
                step.State,
                step.ExecutionEpoch,
                step.BranchId,
                step.AttemptNo,
                step.AssignmentId,
                step.ReportId,
                step.FormFamilyId,
                step.FormVersionId,
                step.FormVersionNo,
                step.FormSchemaHash,
                reportPinned = !string.IsNullOrWhiteSpace(step.ReportId)
            }).ToArray(),
            source = new
            {
                reportId = source.Id,
                source.Status,
                source.PayloadRevision,
                source.PayloadHash,
                source.LifecycleRevision,
                source.IsActive,
                source.IsCurrent,
                rawPayloadRecorded = false
            },
            target = new
            {
                reportId = target.Id,
                target.Status,
                target.PayloadRevision,
                target.PayloadHash,
                target.LifecycleRevision,
                target.IsActive,
                target.IsCurrent,
                rawPayloadRecorded = false
            }
        };
    }

    private static string P7MappingFingerprint(params string[] values)
        => Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        string.Join("\n", values))))
            .ToLowerInvariant();

    private static bool IsP7LowerSha256(string value)
        => value.Length == 64 &&
           value.All(character =>
               character is >= '0' and <= '9' ||
               character is >= 'a' and <= 'f');

    private static async Task RunP705MappingCasesAsync(
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
        => await RunP705MappingCasesExpandedAsync(
            cases,
            mongoEvidence,
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            activationThrough,
            ct);

    private static async Task ReassignP7MappingSourceForRedactionAsync(
        IMongoDatabase database,
        P7MappingScenario scenario,
        string outsiderUserId,
        CancellationToken ct)
    {
        var sourceStep = await database
            .GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(item => item.Id == scenario.SourceStepInstanceId)
            .SingleAsync(ct);
        await ReassignP7MappingTargetAsync(
            database,
            sourceStep,
            outsiderUserId,
            ct);
        await database.GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .UpdateOneAsync(
                item =>
                    item.Id == scenario.SourceReportId &&
                    !item.IsDeleted,
                Builders<WorkAssignmentReport>.Update
                    .Set(item => item.AssigneeUserId, outsiderUserId)
                    .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken: ct);
    }

    private static void AssertP7MappingCallerRedaction(
        ApiHarnessResponse preview)
    {
        var sourceReports = preview.Json?["sourceReports"] as JsonArray
                            ?? throw new InvalidOperationException(
                                "P7-05 preview lacks sourceReports.");
        var source = sourceReports
            .OfType<JsonObject>()
            .Single();
        Require(
            source["identityRedacted"]?.GetValue<bool>() == true &&
            source["reportId"] is null &&
            source["workAssignmentId"] is null &&
            source["flowInstanceId"] is null &&
            source["stepInstanceId"] is null &&
            source["payloadHash"] is null &&
            source["lifecycleRevision"] is null,
            "P7-05 caller-facing source identity was not fully redacted.");
        var changes = preview.Json?["changes"] as JsonArray
                      ?? throw new InvalidOperationException(
                          "P7-05 preview lacks changes.");
        var provenance = changes
            .OfType<JsonObject>()
            .SelectMany(change =>
                (change["sources"] as JsonArray ??
                 new JsonArray()).OfType<JsonObject>())
            .ToList();
        Require(
            provenance.Count > 0 &&
            provenance.All(sourceFact =>
                sourceFact["sourceAssignmentId"] is null &&
                sourceFact["sourceReportId"] is null &&
                sourceFact["sourcePayloadHash"] is null &&
                sourceFact["valueJson"] is null),
            "P7-05 caller-facing change provenance retained forbidden source identity/value facts.");
        Require(
            preview.Json?["summarySourceJson"] is null,
            "P7-05 redacted preview retained summarySourceJson.");
    }

    private static async Task RunP706MappingCasesAsync(
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
        => await RunP706MappingCasesExpandedAsync(
            cases,
            mongoEvidence,
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            activationThrough,
            ct);

    private static async Task RunP707MappingCasesAsync(
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
        => await RunP707MappingCasesExpandedAsync(
            cases,
            mongoEvidence,
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            activationThrough,
            ct);

    private sealed record P7MappingFixture(
        string WorkId,
        string FamilyId,
        string VersionId,
        string PayloadHash,
        string TargetUnitId,
        string SourceUserId,
        string OutsiderUserId,
        ProbeFormPinSnapshot RootForm,
        ProbeFormPinSnapshot ChildForm);

    private sealed record P7MappingScenario(
        string WorkId,
        string FlowInstanceId,
        string SourceStepInstanceId,
        string TargetStepInstanceId,
        string SourceAssignmentId,
        string TargetAssignmentId,
        string SourceReportId,
        string TargetReportId,
        string TargetPeriodId,
        string ActorUserId,
        string ActorToken);

    private sealed record P7MappingPersistenceState(
        int TargetPayloadRevision,
        string? TargetPayloadHash,
        long TargetPayloadSizeBytes,
        string? TargetPayloadStatus,
        DateTime? TargetPayloadUpdatedAtUtc,
        string? TargetPayloadMutationCommandId,
        string? TargetPayloadMutationCommandHash,
        string? TargetPayloadMutationOperation,
        string? TargetLastPayloadCommandId,
        string? TargetLastPayloadCommandHash,
        string? TargetLastPayloadCommandOperation,
        int? TargetLastPayloadCommandRevision,
        int TargetLifecycleRevision,
        string TargetLifecycleStatus,
        bool TargetIsActive,
        bool TargetIsCurrent,
        string? TargetReceiptId,
        string? TargetProvenanceId,
        string? TargetProvenanceHash,
        int? TargetMappingResultPayloadRevision,
        string? TargetMappingResultPayloadHash,
        string? TargetLastLifecycleCommandId,
        int? TargetLastLifecycleCommandRevision,
        int TargetLifecycleProjectionOutboxCount,
        string TargetLifecycleProjectionOutboxFingerprint,
        long ReceiptCount,
        long ProvenanceCount,
        long EventCount,
        long OutboxCount,
        long ReportLogCount,
        long MappingAuditCount,
        long PayloadCount,
        long SectionCount,
        bool NoOrphan,
        string Fingerprint);
}
