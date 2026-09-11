using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    private async Task RunIdentityCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P10-ID-01",
            Req("P10-REC-003", "P10-REC-004"),
            O("API_KESTREL", "DIRECT_MONGO", "RECEIPT", "QUEUE_TRACE"),
            async () =>
            {
                var request = NewCreateRequest("p10-id-owner-001");
                var before = await CountRunsAsync(ct);
                var response = await CreateRunAsync(
                    request,
                    Actor("executor").Token,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.Accepted,
                    "P10 ID-01 create");
                _identityRunId = RequireResponseString(
                    response,
                    "reconciliationId");
                var run = await LoadRunAsync(_identityRunId, ct);
                _authorizationSnapshotSha256 = BsonText(
                    run,
                    "authorizationSnapshotHash");
                HarnessAssert.Equal(
                    before + 1,
                    await CountRunsAsync(ct),
                    "P10 ID-01 durable owner count");
                HarnessAssert.Equal(
                    "QUEUED",
                    BsonText(run, "status"),
                    "P10 ID-01 initial status");
                HarnessAssert.True(
                    IsSha(BsonText(run, "receiptId")) &&
                    IsSha(BsonText(run, "requestHash")) &&
                    IsSha(BsonText(run, "receiptResponseHash")) &&
                    IsSha(BsonText(run, "stateHash")),
                    "P10 ID-01 receipt/request/state hashes are not canonical.");
                HarnessAssert.Equal(
                    0,
                    run.GetValue("operationReceipts", new BsonArray())
                        .AsBsonArray.Count,
                    "P10 ID-01 operation receipt count");
                _receiptLeaseTrace.Add(new
                {
                    caseId = "P10-ID-01",
                    reconciliationId = _identityRunId,
                    receiptId = BsonText(run, "receiptId"),
                    requestHash = BsonText(run, "requestHash"),
                    stateRevision = BsonLong(run, "stateRevision"),
                    stateHash = BsonText(run, "stateHash"),
                    status = BsonText(run, "status"),
                    durableOwners = 1
                });
                return ObserveRun(
                    response,
                    run,
                    "One request produced exactly one reconciliation aggregate, durable receipt and QUEUED job owner.",
                    "aggregate=1;receipt=1;queueOwner=1;status=QUEUED");
            });

        await RunCaseAsync(
            "P10-ID-02",
            Req("P10-REC-003"),
            O("DIRECT_MONGO", "SOURCE_FINGERPRINT", "P9_REGRESSION"),
            async () =>
            {
                var run = await LoadRunAsync(IdentityRunId(), ct);
                HarnessAssert.Equal(
                    Fixture().P9GenerationId,
                    BsonText(run, "p9GenerationId"),
                    "P10 P9 generation id pin");
                HarnessAssert.Equal(
                    Fixture().P9GenerationHash,
                    BsonText(run, "p9GenerationHash"),
                    "P10 P9 generation hash pin");
                HarnessAssert.Equal(
                    Fixture().SourcePayloadHash,
                    BsonText(run, "sourcePayloadHash"),
                    "P10 source payload hash pin");
                HarnessAssert.Equal(
                    Fixture().DynamicFormVersionId,
                    BsonText(run, "dynamicFormVersionId"),
                    "P10 Form version pin");
                HarnessAssert.Equal(
                    Fixture().FlowTemplateVersionId,
                    BsonText(run, "flowTemplateVersionId"),
                    "P10 Flow version pin");
                HarnessAssert.Equal(
                    Fixture().FlowPayloadHash,
                    BsonText(run, "flowPayloadHash"),
                    "P10 Flow payload hash pin");
                HarnessAssert.Equal(
                    Fixture().ConfigHash,
                    BsonText(run, "p8ConfigHash"),
                    "P10 P8 config pin");
                HarnessAssert.Equal(
                    Fixture().ConfigBundleHash,
                    BsonText(run, "p8ConfigBundleHash"),
                    "P10 P8 config bundle hash");
                HarnessAssert.Equal(
                    P9CatalogRawSha256,
                    BsonText(run, "p9CatalogRawSha256"),
                    "P10 P9 catalog raw pin");
                HarnessAssert.Equal(
                    Candidate().CatalogRawSha256,
                    BsonText(run, "candidateCatalogRawSha256"),
                    "P10 candidate catalog raw pin");
                HarnessAssert.Equal(
                    Candidate().SchemaSemanticSha256,
                    BsonText(run, "candidateSchemaSemanticSha256"),
                    "P10 candidate schema semantic pin");

                var sourceLifecycleHash =
                    StatisticReconciliationCanonicalJson.HashObject(new
                    {
                        schema = "P10_SOURCE_LIFECYCLE_PIN_V1",
                        sourceReportId = Fixture().ReportId,
                        sourcePayloadRevision = Fixture().SourcePayloadRevision,
                        sourcePayloadHash = Fixture().SourcePayloadHash,
                        sourceLifecycleRevision = Fixture().SourceLifecycleRevision,
                        sourceLifecycleEventKey = BsonText(
                            run,
                            "sourceLifecycleEventKey"),
                        sourceLifecycleStatus = "APPROVED",
                        sourceMembershipSignature = HashText(
                            "P10-AUTONOMOUS-SOURCE-MEMBERSHIP-V1")
                    });
                HarnessAssert.Equal(
                    sourceLifecycleHash,
                    BsonText(run, "sourceLifecycleHash"),
                    "P10 source lifecycle hash recomputation");
                HarnessAssert.True(
                    run.GetValue("sourceSetSha256", BsonNull.Value)
                        .IsBsonNull &&
                    run.GetValue("expectedAlgorithmSha256", BsonNull.Value)
                        .IsBsonNull,
                    "P10-01 populated future source/algorithm hashes.");
                return new P10CaseObservation(
                    200,
                    "Source, P9, Form, Flow, P8 config and candidate catalog pins recomputed from live Mongo and files.",
                    "p9=true;source=true;form=true;flow=true;p8=true;catalog=true;futureHashes=null",
                    RequestHashSha256: BsonText(run, "requestHash"),
                    ReceiptHashSha256: BsonText(
                        run,
                        "receiptResponseHash"),
                    StateHashSha256: BsonText(run, "stateHash"));
            });

        await RunCaseAsync(
            "P10-ID-03",
            Req("P10-REC-003", "P10-REC-005"),
            O("API_KESTREL", "DIRECT_MONGO", "AUTH_BEFORE_EXISTENCE"),
            async () =>
            {
                var request = NewCreateRequest("p10-id-server-derived-003");
                request["conceptKey"] = "AMOUNT_SERVER_DERIVED";
                var response = await CreateRunAsync(
                    request,
                    Actor("executor2").Token,
                    ct,
                    headers: new Dictionary<string, string>
                    {
                        ["X-Actor-User-Id"] = Actor("admin").Id,
                        ["X-Tenant-Unit-Id"] = Fixture().UnitBId,
                        ["X-Permission-Codes"] = "SYSTEM_ADMIN"
                    });
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.Accepted,
                    "P10 ID-03 create");
                var id = RequireResponseString(response, "reconciliationId");
                var run = await LoadRunAsync(id, ct);
                HarnessAssert.Equal(
                    Actor("executor2").Id,
                    BsonText(run, "actorUserId"),
                    "P10 server-derived actor");
                HarnessAssert.Equal(
                    Fixture().UnitAId,
                    BsonText(run, "tenantUnitId"),
                    "P10 server-derived tenant");
                HarnessAssert.Equal(
                    Fixture().WorkId,
                    BsonText(run, "workId"),
                    "P10 server-derived work");
                HarnessAssert.Equal(
                    Fixture().ScopeAssignmentId,
                    BsonText(run, "scopeAssignmentId"),
                    "P10 server-derived scope");
                var permissions = run["permissionCodes"].AsBsonArray
                    .Select(value => value.AsString)
                    .ToArray();
                HarnessAssert.True(
                    permissions.Contains(
                        "ROLE:MANAGER_LEVEL",
                        StringComparer.Ordinal) &&
                    !permissions.Contains(
                        "ROLE:SYSTEM_ADMIN",
                        StringComparer.Ordinal),
                    "P10 permission snapshot trusted spoofed headers.");
                return ObserveRun(
                    response,
                    run,
                    "Actor, tenant, work, scope and permission snapshot were derived only from authenticated server state.",
                    "actor=executor2;tenant=unitA;scope=exact;spoofedHeadersIgnored=true",
                    "executor2");
            });

        await RunCaseAsync(
            "P10-ID-04",
            Req("P10-REC-003", "P10-REC-004"),
            O("API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"),
            async () =>
            {
                var collection = RequireDatabase()
                    .GetCollection<BsonDocument>(RunCollection);
                var original = await LoadRunAsync(IdentityRunId(), ct);
                var originalBytesHash = HashBytes(original.ToBson());
                var tampered = (BsonDocument)original.DeepClone();
                tampered["actorUserId"] = ObjectId.Parse(
                    Actor("executor2").Id);
                await collection.ReplaceOneAsync(
                    new BsonDocument("_id", original["_id"]),
                    tampered,
                    cancellationToken: ct);
                try
                {
                    var response = await RequireApi().GetAsync(
                        $"{ReconciliationBasePath()}/{IdentityRunId()}/detail",
                        Actor("admin").Token,
                        ct: ct);
                    ExpectError(
                        response,
                        HttpStatusCode.Conflict,
                        "STAT_RECONCILIATION_JOB_CONFLICT",
                        "P10 immutable header tamper read");
                }
                finally
                {
                    await collection.ReplaceOneAsync(
                        new BsonDocument("_id", original["_id"]),
                        original,
                        cancellationToken: CancellationToken.None);
                }
                var restored = await LoadRunAsync(IdentityRunId(), ct);
                HarnessAssert.Equal(
                    originalBytesHash,
                    HashBytes(restored.ToBson()),
                    "P10 immutable tamper restoration");
                return new P10CaseObservation(
                    409,
                    "A direct immutable-header tamper was rejected before any product write and the exact fixture bytes were restored.",
                    "tamper=actorUserId;error=STAT_RECONCILIATION_JOB_CONFLICT;productWrites=0;restored=true",
                    ActorAlias: "admin",
                    RequestHashSha256: BsonText(original, "requestHash"),
                    ReceiptHashSha256: BsonText(
                        original,
                        "receiptResponseHash"),
                    StateHashSha256: BsonText(original, "stateHash"));
            });

        await RunCaseAsync(
            "P10-ID-05",
            Req("P10-REC-003", "P10-REC-004"),
            O("DIRECT_MONGO", "RECEIPT", "QUEUE_TRACE"),
            async () =>
            {
                var run = await LoadRunAsync(IdentityRunId(), ct);
                var expectedRequestHash =
                    StatisticReconciliationCanonicalJson.HashObject(new
                    {
                        schema = "P10_RECONCILIATION_CREATE_REQUEST_V1",
                        workId = Fixture().WorkId,
                        scopeAssignmentId = Fixture().ScopeAssignmentId,
                        commandId = "p10-id-owner-001",
                        p9ResultKind = "DIRECT",
                        p9ResultId = Fixture().P9ResultId,
                        p9RunId = Fixture().P9RunId,
                        conceptKey = Fixture().ConceptKey,
                        grain = Fixture().Grain,
                        filterHash = Fixture().FilterHash
                    });
                HarnessAssert.Equal(
                    expectedRequestHash,
                    BsonText(run, "requestHash"),
                    "P10 canonical create request hash");
                var expectedReceiptId =
                    StatisticReconciliationCanonicalJson.HashText(
                        string.Join(
                            "\n",
                            "P10_RECONCILIATION_RECEIPT_V1",
                            Actor("executor").Id,
                            Fixture().UnitAId,
                            Fixture().WorkId,
                            Fixture().ScopeAssignmentId,
                            "p10-id-owner-001"));
                HarnessAssert.Equal(
                    expectedReceiptId,
                    BsonText(run, "receiptId"),
                    "P10 canonical receipt id");
                var expectedReceiptResponseHash =
                    StatisticReconciliationCanonicalJson.HashObject(new
                    {
                        schema = "P10_RECONCILIATION_ACCEPTED_RESPONSE_V1",
                        reconciliationId = BsonText(run, "_id"),
                        receiptId = BsonText(run, "receiptId"),
                        commandId = BsonText(run, "commandId"),
                        requestHash = BsonText(run, "requestHash"),
                        immutableIdentityHash = BsonText(
                            run,
                            "immutableIdentityHash"),
                        immutableHeaderHash = BsonText(
                            run,
                            "immutableHeaderHash"),
                        acceptedAtUtc = FormatBsonUtc(run, "createdAtUtc")
                    });
                HarnessAssert.Equal(
                    expectedReceiptResponseHash,
                    BsonText(run, "receiptResponseHash"),
                    "P10 accepted response hash");
                var expectedStateHash = ComputeStateHash(run);
                HarnessAssert.Equal(
                    expectedStateHash,
                    BsonText(run, "stateHash"),
                    "P10 canonical state hash");
                return new P10CaseObservation(
                    200,
                    "Create request, durable receipt, accepted response and state hashes all recomputed canonically.",
                    "requestHash=true;receiptId=true;receiptHash=true;stateHash=true",
                    RequestHashSha256: expectedRequestHash,
                    ReceiptHashSha256: expectedReceiptResponseHash,
                    StateHashSha256: expectedStateHash);
            });

        await RunCaseAsync(
            "P10-ID-06",
            Req("P10-REC-003"),
            O(
                "API_KESTREL",
                "DIRECT_MONGO",
                "SOURCE_FINGERPRINT",
                "COLLECTION_DELTA"),
            async () =>
            {
                var jobs = RequireDatabase()
                    .GetCollection<BsonDocument>(
                        "work_report_statistic_rebuild_jobs");
                var filter = new BsonDocument(
                    "_id",
                    ObjectId.Parse(Fixture().P9RunId));
                var original = await jobs.Find(filter).SingleAsync(ct);
                var before = await CountRunsAsync(ct);
                var mutations = new (string Name, string Field, BsonValue Value)[]
                {
                    ("stale-result", "isCurrentPublication", false),
                    ("generation", "generationHash", "not-a-sha"),
                    ("config", "configHash", "not-a-sha"),
                    ("catalog", "catalogRawSha256", new string('0', 64)),
                    ("stage-lock", "stageLockSha256", new string('0', 64)),
                    ("published-prompt", "candidatePromptId", "P9-11")
                };
                foreach (var mutation in mutations)
                {
                    await jobs.UpdateOneAsync(
                        filter,
                        Builders<BsonDocument>.Update.Set(
                            mutation.Field,
                            mutation.Value),
                        cancellationToken: ct);
                    try
                    {
                        var request = NewCreateRequest(
                            $"p10-id-stale-{mutation.Name}-006");
                        request["conceptKey"] =
                            $"AMOUNT_{mutation.Name.ToUpperInvariant()}";
                        var response = await CreateRunAsync(
                            request,
                            Actor("admin").Token,
                            ct);
                        ExpectError(
                            response,
                            HttpStatusCode.BadRequest,
                            "STAT_RECONCILIATION_REQUEST_INVALID",
                            $"P10 stale {mutation.Name}");
                    }
                    finally
                    {
                        await jobs.ReplaceOneAsync(
                            filter,
                            original,
                            cancellationToken: CancellationToken.None);
                    }
                }
                HarnessAssert.Equal(
                    before,
                    await CountRunsAsync(ct),
                    "P10 stale source variants wrote a run");
                var identity = await LoadRunAsync(IdentityRunId(), ct);
                HarnessAssert.True(
                    identity.GetValue("sourceSetSha256", BsonNull.Value)
                        .IsBsonNull &&
                    identity.GetValue(
                        "expectedAlgorithmSha256",
                        BsonNull.Value).IsBsonNull &&
                    !identity.Contains("verdict") &&
                    !identity.Contains("delta"),
                    "P10-01 synthesized later-stage source or verdict fields.");
                return new P10CaseObservation(
                    400,
                    "Stale P9 result, generation, config and catalog pins were rejected before write; later source/algorithm/verdict fields remained absent.",
                    "staleVariants=6;writes=0;sourceSet=null;algorithm=null;officialVerdict=false",
                    ActorAlias: "admin",
                    RequestHashSha256: EvidenceRequestHash(
                        "reconciliations/stale-source",
                        new JsonObject { ["variants"] = 4 }),
                    ReceiptHashSha256: BsonText(
                        identity,
                        "receiptResponseHash"),
                    StateHashSha256: BsonText(identity, "stateHash"));
            });
    }

    private string IdentityRunId()
        => _identityRunId ?? throw new HarnessCaseNotRunnableException(
            "P10 identity run is unavailable.");

    private static bool IsSha(string value)
        => StatisticReconciliationCanonicalJson.IsCanonicalSha256(value);

    private static string? BsonNullableText(
        BsonDocument document,
        string field)
        => !document.TryGetValue(field, out var value) || value.IsBsonNull
            ? null
            : value.IsObjectId
                ? value.AsObjectId.ToString()
                : value.AsString;

    private static DateTime? BsonNullableUtc(
        BsonDocument document,
        string field)
        => !document.TryGetValue(field, out var value) || value.IsBsonNull
            ? null
            : value.ToUniversalTime();

    private static string? FormatUtc(DateTime? value)
    {
        if (!value.HasValue)
            return null;
        var utc = value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
        utc = new DateTime(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
        return utc.ToString("O", CultureInfo.InvariantCulture);
    }

    private static string? FormatBsonUtc(
        BsonDocument document,
        string field)
        => FormatUtc(BsonNullableUtc(document, field));

    private static string ComputeStateHash(BsonDocument run)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_RECONCILIATION_STATE_V1",
            reconciliationId = BsonText(run, "_id"),
            status = BsonText(run, "status"),
            stateRevision = BsonLong(run, "stateRevision"),
            retryCount = (int)BsonLong(run, "retryCount"),
            nextRetryAtUtc = FormatBsonUtc(run, "nextRetryAtUtc"),
            leaseOwnerId = BsonNullableText(run, "leaseOwnerId"),
            claimToken = BsonNullableText(run, "claimToken"),
            leaseUntilUtc = FormatBsonUtc(run, "leaseUntilUtc"),
            lastHeartbeatAtUtc = FormatBsonUtc(run, "lastHeartbeatAtUtc"),
            deadlineAtUtc = FormatBsonUtc(run, "deadlineAtUtc"),
            diagnosticCode = BsonNullableText(run, "diagnosticCode"),
            pendingGenerationId = BsonNullableText(
                run,
                "pendingGenerationId"),
            pendingGenerationHash = BsonNullableText(
                run,
                "pendingGenerationHash"),
            pendingGenerationPublishedAtUtc = FormatBsonUtc(
                run,
                "pendingGenerationPublishedAtUtc"),
            currentGenerationId = BsonNullableText(
                run,
                "currentGenerationId"),
            currentGenerationHash = BsonNullableText(
                run,
                "currentGenerationHash"),
            generationPublishRevision = BsonLong(
                run,
                "generationPublishRevision"),
            operationReceiptHistoryHash = BsonText(
                run,
                "operationReceiptHistoryHash"),
            cancelledAtUtc = FormatBsonUtc(run, "cancelledAtUtc"),
            failedAtUtc = FormatBsonUtc(run, "failedAtUtc")
        });

    private static string ComputePrivateRunHash(
        string methodName,
        BsonDocument document)
    {
        var run = BsonSerializer.Deserialize<StatisticReconciliationRun>(
            document);
        var method = typeof(StatisticReconciliationRunService).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(StatisticReconciliationRunService).FullName,
                methodName);
        return method.Invoke(null, [run]) as string
            ?? throw new InvalidOperationException(
                $"P10 canonical helper '{methodName}' returned no hash.");
    }
}
