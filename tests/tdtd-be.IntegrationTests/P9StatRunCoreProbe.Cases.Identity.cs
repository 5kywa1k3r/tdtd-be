using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task RunIdentityCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-CORE-ID-01",
            async () =>
            {
                var before = await CountJobsAsync(ct);
                const string commandId = "p9-core-identity-001";
                var request = BuildCreateRequest(commandId);
                var response = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    request,
                    Actor("executor").Token,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.Accepted,
                    "P9 identity enqueue");
                _identityJobId = RequiredString(response.Json, "jobId");
                HarnessAssert.Equal(
                    _identityJobId,
                    RequiredString(response.Json, "runId"),
                    "Domain-owned jobId/runId identity");
                HarnessAssert.Equal(
                    commandId,
                    RequiredString(response.Json, "commandId"),
                    "Identity command");
                HarnessAssert.Equal(
                    "QUEUED",
                    RequiredString(response.Json, "status"),
                    "Identity initial status");
                HarnessAssert.Equal(
                    before + 1,
                    await CountJobsAsync(ct),
                    "Identity enqueue job delta");
                var collections = (await (await RequireDatabase()
                            .ListCollectionNamesAsync(cancellationToken: ct))
                        .ToListAsync(ct))
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var prohibited in ProhibitedRunCollections)
                {
                    HarnessAssert.True(
                        !collections.Contains(prohibited),
                        $"Parallel generic run/value collection exists: {prohibited}");
                }
                return new CaseObservation(
                    "The canonical rebuild-job document owns both jobId and runId; no generic run/value store appeared.",
                    "owner=work_report_statistic_rebuild_jobs;jobId=runId;genericStores=0");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-ID-02",
            async () =>
            {
                var response = await RequireApi().GetAsync(
                    $"api/stat-runs/jobs/{RequireIdentityJobId()}",
                    Actor("executor").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.OK,
                    "P9 identity pins read");
                HarnessAssert.Equal(
                    Fixture().ConfigId,
                    RequiredString(response.Json, "configId"),
                    "Config ID pin");
                HarnessAssert.Equal(
                    Fixture().ConfigVersionId,
                    RequiredString(response.Json, "configVersionId"),
                    "Config version ID pin");
                HarnessAssert.Equal(
                    Fixture().ConfigHash,
                    RequiredString(response.Json, "configHash"),
                    "Config hash pin");
                HarnessAssert.Equal(
                    CatalogRawSha256,
                    RequiredString(response.Json, "catalogRawSha256"),
                    "Catalog raw pin");
                HarnessAssert.Equal(
                    CatalogSemanticSha256,
                    RequiredString(response.Json, "catalogSemanticSha256"),
                    "Catalog semantic pin");
                HarnessAssert.Equal(
                    StageLockSha256,
                    RequiredString(response.Json, "stageLockSha256"),
                    "Stage-lock pin");

                var template = await RequireDatabase()
                    .GetCollection<BsonDocument>("dynamic_form_templates")
                    .Find(new BsonDocument(
                        "_id",
                        ObjectId.Parse(Fixture().TemplateId)))
                    .SingleAsync(ct);
                var sections = template["statisticConfigSections"]
                    .AsBsonDocument;
                var fieldSectionJson = BsonText(
                    sections,
                    "fieldSectionJson");
                var tableSectionJson = BsonText(
                    sections,
                    "tableSectionJson");
                var dependencyPins = template[
                        "statisticConfigDependencyPins"]
                    .AsBsonArray
                    .Select(value => value.AsString)
                    .ToArray();
                var recomputedConfigHash = CanonicalJsonSha256(new
                {
                    ownerKind = "DYNAMIC_FORM",
                    ownerId = Fixture().TemplateId,
                    fieldConfig = JsonNode.Parse(fieldSectionJson) ??
                                  throw new InvalidOperationException(
                                      "Authoritative field config is null."),
                    tableConfig = JsonNode.Parse(tableSectionJson) ??
                                  throw new InvalidOperationException(
                                      "Authoritative table config is null."),
                    dependencyPins
                });
                HarnessAssert.Equal(
                    recomputedConfigHash,
                    BsonText(template, "statisticConfigHash"),
                    "Authoritative template config hash recomputation");
                var snapshots = template["statisticConfigSnapshots"]
                    .AsBsonArray;
                HarnessAssert.Equal(
                    1,
                    snapshots.Count,
                    "Authoritative config snapshot count");
                var snapshot = snapshots[0].AsBsonDocument;
                HarnessAssert.Equal(
                    Fixture().ConfigVersionId,
                    BsonText(snapshot, "versionId"),
                    "Authoritative config snapshot version");
                HarnessAssert.Equal(
                    recomputedConfigHash,
                    BsonText(snapshot, "configHash"),
                    "Authoritative config snapshot hash");
                HarnessAssert.Equal(
                    fieldSectionJson,
                    BsonText(
                        snapshot["sections"].AsBsonDocument,
                        "fieldSectionJson"),
                    "Snapshot field section pin");
                HarnessAssert.Equal(
                    tableSectionJson,
                    BsonText(
                        snapshot["sections"].AsBsonDocument,
                        "tableSectionJson"),
                    "Snapshot table section pin");
                HarnessAssert.Equal(
                    recomputedConfigHash,
                    RequiredString(response.Json, "configHash"),
                    "API config hash recomputation");
                var job = await LoadJobAsync(
                    RequireIdentityJobId(),
                    ct);
                HarnessAssert.Equal(
                    recomputedConfigHash,
                    BsonText(job, "configHash"),
                    "Job config hash recomputation");

                var candidate = BuildCandidateOptions();
                var catalogBytes = await File.ReadAllBytesAsync(
                    candidate.CatalogPath,
                    ct);
                var schemaBytes = await File.ReadAllBytesAsync(
                    candidate.SchemaPath,
                    ct);
                HarnessAssert.Equal(
                    CatalogRawSha256,
                    HashBytes(catalogBytes),
                    "Catalog raw recomputation");
                HarnessAssert.Equal(
                    CatalogSemanticSha256,
                    CanonicalJsonFileSha256(catalogBytes),
                    "Catalog semantic recomputation");
                HarnessAssert.Equal(
                    SchemaRawSha256,
                    HashBytes(schemaBytes),
                    "Schema raw recomputation");
                HarnessAssert.Equal(
                    SchemaSemanticSha256,
                    CanonicalJsonFileSha256(schemaBytes),
                    "Schema semantic recomputation");
                return new CaseObservation(
                    "Locked config/version plus catalog/schema/stage-lock pins recomputed independently and matched the API/document.",
                    "configOwner=sections+snapshot+dependencyPins/recomputed;catalogRawSemantic=true;schemaRawSemantic=true;stageLock=true");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-ID-03",
            async () =>
            {
                var document = await LoadJobAsync(
                    RequireIdentityJobId(),
                    ct);
                HarnessAssert.Equal(
                    Actor("executor").Id,
                    BsonText(document, "actorUserId"),
                    "Server-derived actor ID");
                HarnessAssert.Equal(
                    Actor("executor").UnitId,
                    BsonText(document, "tenantUnitId"),
                    "Server-derived tenant unit");
                HarnessAssert.Equal(
                    Fixture().WorkId,
                    BsonText(document, "workId"),
                    "Server-derived work ID");
                HarnessAssert.Equal(
                    Fixture().AssignmentId,
                    BsonText(document, "scopeId"),
                    "Server-derived assignment scope");
                HarnessAssert.Equal(
                    "DIRECT_FIELD_TABLE_LABEL",
                    BsonText(document, "capabilityId"),
                    "Server-derived capability");
                HarnessAssert.Equal(
                    "P9_CORE_DIRECT_JOB",
                    BsonText(document, "routeId"),
                    "Server-derived route");
                return new CaseObservation(
                    "Actor, tenant, work, scope, capability and route identity were loaded server-side into the canonical job.",
                    "actor=server;tenant=server;work=server;scope=server;capability=server;route=server");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-ID-04",
            async () =>
            {
                var document = await LoadJobAsync(
                    RequireIdentityJobId(),
                    ct);
                HarnessAssert.Equal(
                    Fixture().ReportId,
                    BsonText(document, "sourceReportId"),
                    "Source report ID pin");
                HarnessAssert.Equal(
                    (long)Fixture().SourceRevision,
                    BsonLong(document, "sourcePayloadRevision"),
                    "Source revision pin");
                HarnessAssert.Equal(
                    Fixture().SourceHash,
                    BsonText(document, "sourcePayloadHash"),
                    "Source hash pin");
                HarnessAssert.Equal(
                    (long)Fixture().LifecycleRevision,
                    BsonLong(document, "sourceLifecycleRevision"),
                    "Lifecycle revision pin");
                HarnessAssert.Equal(
                    Fixture().PeriodKey,
                    BsonText(document, "periodKey"),
                    "Period key pin");
                HarnessAssert.Equal(
                    Fixture().PeriodInstanceKey,
                    BsonText(document, "periodInstanceKey"),
                    "Period instance pin");
                HarnessAssert.Equal(
                    Fixture().PeriodKind,
                    BsonText(document, "periodKind"),
                    "Period kind pin");
                HarnessAssert.Equal(
                    Fixture().PeriodStartUtc,
                    document["periodStartUtc"].ToUniversalTime(),
                    "Period start pin");
                HarnessAssert.Equal(
                    Fixture().PeriodEndUtc,
                    document["periodEndUtc"].ToUniversalTime(),
                    "Period end pin");

                var report = await RequireDatabase()
                    .GetCollection<BsonDocument>("work_assignment_report")
                    .Find(new BsonDocument(
                        "_id",
                        ObjectId.Parse(Fixture().ReportId)))
                    .SingleAsync(ct);
                var values1DJson = BsonText(report, "values1DJson");
                var fieldValuesJson = BsonText(
                    report,
                    "fieldValuesJson");
                var tableRootJson = BsonText(
                    report,
                    "tableValuesRootJson");
                var summarySourceJson = BsonText(
                    report,
                    "summarySourceJson");
                var blocks = await RequireDatabase()
                    .GetCollection<BsonDocument>(
                        "work_report_table_values")
                    .Find(new BsonDocument
                    {
                        ["reportId"] = ObjectId.Parse(Fixture().ReportId),
                        ["payloadRevision"] = Fixture().SourceRevision,
                        ["isDeleted"] = false
                    })
                    .ToListAsync(ct);
                var recomputedSourceHash = ComputeWorkReportPayloadHash(
                    values1DJson,
                    fieldValuesJson,
                    tableRootJson,
                    summarySourceJson,
                    blocks.Select(block => (
                        BsonText(block, "blockId"),
                        (int)BsonLong(block, "blockOrder"),
                        BsonText(block, "payloadHash"))));
                HarnessAssert.Equal(
                    recomputedSourceHash,
                    BsonText(report, "payloadHash"),
                    "Authoritative report source hash recomputation");
                HarnessAssert.Equal(
                    recomputedSourceHash,
                    BsonText(document, "sourcePayloadHash"),
                    "Job source hash recomputation");
                HarnessAssert.Equal(
                    3L,
                    BsonLong(report, "lifecycleRevision"),
                    "Approved lifecycle trace revision");
                var flowFamily = await RequireDatabase()
                    .GetCollection<BsonDocument>(
                        "dynamic_flow_templates")
                    .Find(new BsonDocument(
                        "_id",
                        ObjectId.Parse(Fixture().FlowFamilyId)))
                    .SingleAsync(ct);
                var flowVersion = await RequireDatabase()
                    .GetCollection<BsonDocument>(
                        "dynamic_flow_template_versions")
                    .Find(new BsonDocument(
                        "_id",
                        ObjectId.Parse(Fixture().FlowVersionId)))
                    .SingleAsync(ct);
                var flowInstance = await RequireDatabase()
                    .GetCollection<BsonDocument>(
                        "dynamic_flow_instances")
                    .Find(new BsonDocument(
                        "_id",
                        ObjectId.Parse(Fixture().FlowInstanceId)))
                    .SingleAsync(ct);
                var flowEpoch = await RequireDatabase()
                    .GetCollection<BsonDocument>(
                        "dynamic_flow_execution_epochs")
                    .Find(new BsonDocument(
                        "_id",
                        ObjectId.Parse(Fixture().FlowEpochId)))
                    .SingleAsync(ct);
                var flowSteps = RequireDatabase()
                    .GetCollection<BsonDocument>(
                        "dynamic_flow_step_instances");
                var flowStep = await flowSteps
                    .Find(new BsonDocument(
                        "_id",
                        ObjectId.Parse(Fixture().FlowStepInstanceId)))
                    .SingleAsync(ct);
                var recomputedFlowHash = CanonicalJsonSha256(
                    JsonNode.Parse(BsonText(flowVersion, "payloadJson")) ??
                    throw new InvalidOperationException(
                        "Flow version payload is null."));
                HarnessAssert.Equal(
                    Fixture().FlowPayloadHash,
                    recomputedFlowHash,
                    "Flow version payload hash recomputation");
                HarnessAssert.Equal(
                    Fixture().FlowFamilyId,
                    BsonText(document, "flowTemplateId"),
                    "Job flow family pin");
                HarnessAssert.Equal(
                    Fixture().FlowVersionId,
                    BsonText(document, "flowTemplateVersionId"),
                    "Job flow version pin");
                HarnessAssert.Equal(
                    Fixture().FlowInstanceId,
                    BsonText(document, "flowInstanceId"),
                    "Job flow instance pin");
                HarnessAssert.Equal(
                    Fixture().FlowPayloadHash,
                    BsonText(document, "flowPayloadHash"),
                    "Job flow payload hash pin");
                HarnessAssert.Equal(
                    (long)Fixture().FlowExecutionEpoch,
                    BsonLong(document, "flowExecutionEpoch"),
                    "Job flow execution epoch pin");
                HarnessAssert.Equal(
                    1L,
                    BsonLong(flowFamily, "familyRevision"),
                    "Flow family revision");
                HarnessAssert.Equal(
                    "LOCKED",
                    BsonText(flowVersion, "status"),
                    "Flow version locked status");
                HarnessAssert.Equal(
                    "ACTIVE",
                    BsonText(flowInstance, "state"),
                    "Flow instance state");
                HarnessAssert.True(
                    flowEpoch["isCanonical"].AsBoolean,
                    "Flow execution epoch is not canonical.");
                HarnessAssert.True(
                    flowStep["isCanonicalEpoch"].AsBoolean,
                    "Flow step is not in the canonical epoch.");
                HarnessAssert.Equal(
                    "APPROVED",
                    BsonText(flowStep, "state"),
                    "Flow step effective state");
                HarnessAssert.Equal(
                    Fixture().AssignmentId,
                    BsonText(flowStep, "assignmentId"),
                    "Flow step assignment owner link");
                HarnessAssert.Equal(
                    Fixture().ReportId,
                    BsonText(flowStep, "reportId"),
                    "Flow step report owner link");
                HarnessAssert.Equal(
                    (long)Fixture().LifecycleRevision,
                    BsonLong(flowStep, "reportLifecycleRevision"),
                    "Flow step report lifecycle revision");
                HarnessAssert.Equal(
                    "APPROVED",
                    BsonText(flowStep, "reportLifecycleStatus"),
                    "Flow step report lifecycle status");
                HarnessAssert.True(
                    flowStep["reportLifecycleIsActive"].AsBoolean,
                    "Flow step report lifecycle must be active.");
                HarnessAssert.Equal(
                    BsonText(report, "dynamicFormFamilyId"),
                    BsonText(flowStep, "formFamilyId"),
                    "Flow step Form family owner link");
                HarnessAssert.Equal(
                    BsonText(report, "dynamicFormTemplateId"),
                    BsonText(flowStep, "formVersionId"),
                    "Flow step Form version owner link");
                HarnessAssert.Equal(
                    BsonLong(report, "dynamicFormVersionNo"),
                    BsonLong(flowStep, "formVersionNo"),
                    "Flow step Form version number");
                HarnessAssert.Equal(
                    BsonText(report, "dynamicFormSchemaHash"),
                    BsonText(flowStep, "formSchemaHash"),
                    "Flow step Form schema owner link");
                HarnessAssert.Equal(
                    64,
                    BsonText(flowStep, "formSchemaHash").Length,
                    "Flow step Form schema hash length");
                var pairedReport = await RequireDatabase()
                    .GetCollection<BsonDocument>(
                        "work_assignment_report")
                    .Find(new BsonDocument(
                        "_id",
                        ObjectId.Parse(Fixture().PairedReportId)))
                    .SingleAsync(ct);
                HarnessAssert.Equal(
                    Fixture().PairedSourceHash,
                    BsonText(pairedReport, "payloadHash"),
                    "Paired report B source hash");

                var beforeLineageRejections = await CountJobsAsync(ct);
                var pairedRequest = BuildCreateRequest(
                    "p9-core-paired-source-reject-004");
                pairedRequest["sourceReportId"] =
                    Fixture().PairedReportId;
                pairedRequest["expectedSourceHash"] =
                    Fixture().PairedSourceHash;
                pairedRequest["period"]!
                    .AsObject()["periodInstanceKey"] =
                    "MONTH:2026-08:B";
                var pairedResponse = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    pairedRequest,
                    Actor("executor").Token,
                    ct);
                ExpectError(
                    pairedResponse,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_SOURCE_NOT_EFFECTIVE",
                    "Paired report owner-link rejection");
                HarnessAssert.Equal(
                    "FLOW_RUNTIME_PIN_NOT_EFFECTIVE",
                    ApiHarnessClient.FindStringRecursive(
                        pairedResponse.Json,
                        "reason"),
                    "Paired report owner-link rejection reason");
                HarnessAssert.Equal(
                    beforeLineageRejections,
                    await CountJobsAsync(ct),
                    "Paired report owner-link rejection changed jobs");

                var flowStepFilter = new BsonDocument(
                    "_id",
                    ObjectId.Parse(Fixture().FlowStepInstanceId));
                var originalLifecycleRevision =
                    flowStep["reportLifecycleRevision"].DeepClone();
                var originalFormSchemaHash =
                    flowStep["formSchemaHash"].DeepClone();
                try
                {
                    await flowSteps.UpdateOneAsync(
                        flowStepFilter,
                        new BsonDocument(
                            "$set",
                            new BsonDocument(
                                "reportLifecycleRevision",
                                Fixture().LifecycleRevision + 1)),
                        cancellationToken: ct);
                    var lifecycleTamper = BuildCreateRequest(
                        "p9-core-flow-lifecycle-tamper-004");
                    var lifecycleResponse = await CreateJobAsync(
                        "DIRECT_FIELD_TABLE_LABEL",
                        lifecycleTamper,
                        Actor("executor").Token,
                        ct);
                    ExpectError(
                        lifecycleResponse,
                        HttpStatusCode.Conflict,
                        "STAT_RUN_SOURCE_NOT_EFFECTIVE",
                        "Flow-step lifecycle tamper");
                    HarnessAssert.Equal(
                        "FLOW_RUNTIME_PIN_NOT_EFFECTIVE",
                        ApiHarnessClient.FindStringRecursive(
                            lifecycleResponse.Json,
                            "reason"),
                        "Flow-step lifecycle tamper reason");
                    HarnessAssert.Equal(
                        beforeLineageRejections,
                        await CountJobsAsync(ct),
                        "Flow-step lifecycle tamper changed jobs");

                    await flowSteps.UpdateOneAsync(
                        flowStepFilter,
                        new BsonDocument(
                            "$set",
                            new BsonDocument(
                                "reportLifecycleRevision",
                                originalLifecycleRevision)),
                        cancellationToken: ct);
                    await flowSteps.UpdateOneAsync(
                        flowStepFilter,
                        new BsonDocument(
                            "$set",
                            new BsonDocument(
                                "formSchemaHash",
                                new string('f', 64))),
                        cancellationToken: ct);
                    var schemaTamper = BuildCreateRequest(
                        "p9-core-flow-schema-tamper-004");
                    var schemaResponse = await CreateJobAsync(
                        "DIRECT_FIELD_TABLE_LABEL",
                        schemaTamper,
                        Actor("executor").Token,
                        ct);
                    ExpectError(
                        schemaResponse,
                        HttpStatusCode.Conflict,
                        "STAT_RUN_SOURCE_NOT_EFFECTIVE",
                        "Flow-step Form schema tamper");
                    HarnessAssert.Equal(
                        "FLOW_RUNTIME_PIN_NOT_EFFECTIVE",
                        ApiHarnessClient.FindStringRecursive(
                            schemaResponse.Json,
                            "reason"),
                        "Flow-step Form schema tamper reason");
                    HarnessAssert.Equal(
                        beforeLineageRejections,
                        await CountJobsAsync(ct),
                        "Flow-step Form schema tamper changed jobs");
                }
                finally
                {
                    await flowSteps.UpdateOneAsync(
                        flowStepFilter,
                        new BsonDocument(
                            "$set",
                            new BsonDocument
                            {
                                ["reportLifecycleRevision"] =
                                    originalLifecycleRevision,
                                ["formSchemaHash"] =
                                    originalFormSchemaHash
                            }),
                        cancellationToken: ct);
                }
                var apiResponse = await RequireApi().GetAsync(
                    $"api/stat-runs/jobs/{RequireIdentityJobId()}",
                    Actor("executor").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    apiResponse,
                    HttpStatusCode.OK,
                    "Source fingerprint read");
                HarnessAssert.Equal(
                    recomputedSourceHash,
                    RequiredString(apiResponse.Json, "sourceHash"),
                    "API source hash recomputation");
                return new CaseObservation(
                    "Authoritative source and Flow owner links were exact; paired report substitution plus lifecycle/schema tampering failed closed with zero writes and restored the fixture.",
                    "sourceHash=recomputed;pairedB=409/FLOW_RUNTIME_PIN_NOT_EFFECTIVE;ownerLinks=assignment+report+lifecycle+form;tamper=lifecycle+schema/409;writes=0;restored=true");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-ID-05",
            async () =>
            {
                var document = await LoadJobAsync(
                    RequireIdentityJobId(),
                    ct);
                var fixture = Fixture();
                var expectedRequestHash = CanonicalJsonSha256(new
                {
                    CapabilityId = "DIRECT_FIELD_TABLE_LABEL",
                    CommandId = "p9-core-identity-001",
                    WorkId = fixture.WorkId,
                    ScopeType = "ASSIGNMENT",
                    ScopeId = fixture.AssignmentId,
                    SourceReportId = fixture.ReportId,
                    DynamicFormTemplateId = fixture.TemplateId,
                    ExpectedConfigRevision = fixture.ConfigRevision,
                    ExpectedConfigHash = fixture.ConfigHash,
                    ExpectedSourceRevision = fixture.SourceRevision,
                    ExpectedSourceHash = fixture.SourceHash,
                    ExpectedLifecycleRevision = fixture.LifecycleRevision,
                    PeriodKey = fixture.PeriodKey,
                    PeriodInstanceKey = fixture.PeriodInstanceKey,
                    PeriodKind = fixture.PeriodKind,
                    PeriodStartUtc = (DateTime?)fixture.PeriodStartUtc,
                    PeriodEndUtc = (DateTime?)fixture.PeriodEndUtc
                });
                HarnessAssert.Equal(
                    expectedRequestHash,
                    BsonText(document, "requestHash"),
                    "Canonical request hash");
                var expectedReceipt = HashBytes(Encoding.UTF8.GetBytes(
                    string.Join(
                        "\n",
                        "STAT_RUN_RECEIPT_V1",
                        Actor("executor").Id,
                        Actor("executor").UnitId,
                        "DIRECT_FIELD_TABLE_LABEL",
                        "p9-core-identity-001")));
                HarnessAssert.Equal(
                    expectedReceipt,
                    BsonText(document, "receiptId"),
                    "Canonical receipt identity");
                HarnessAssert.True(
                    BsonText(document, "stateHash").Length == 64,
                    "State hash is not canonical SHA-256.");
                return new CaseObservation(
                    "Independent canonical JSON and receipt recomputation matched the durable request/command identity.",
                    "requestHash=recomputed;receiptId=recomputed;stateHash=sha256");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-ID-06",
            async () =>
            {
                var before = await CountJobsAsync(ct);
                var staleConfig = BuildCreateRequest(
                    "p9-core-stale-config-006");
                staleConfig["expectedConfigRevision"] =
                    Fixture().ConfigRevision + 1;
                var configResponse = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    staleConfig,
                    Actor("executor").Token,
                    ct);
                ExpectError(
                    configResponse,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_CONFIG_STALE",
                    "Stale config pin");

                var staleSource = BuildCreateRequest(
                    "p9-core-stale-source-006");
                staleSource["expectedSourceRevision"] =
                    Fixture().SourceRevision + 1;
                var sourceResponse = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    staleSource,
                    Actor("executor").Token,
                    ct);
                ExpectError(
                    sourceResponse,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_REVISION_CONFLICT",
                    "Stale source pin");
                HarnessAssert.Equal(
                    before,
                    await CountJobsAsync(ct),
                    "Stale pin attempts changed jobs");
                return new CaseObservation(
                    "Stale locked-config and source revisions returned stable conflicts with no receipt/job write.",
                    "config=STAT_RUN_CONFIG_STALE;source=STAT_RUN_REVISION_CONFLICT;writes=0");
            },
            ct);
    }

    private string RequireIdentityJobId()
        => _identityJobId ??
           throw new HarnessCaseNotRunnableException(
               "P9 identity job was not created.");
}
