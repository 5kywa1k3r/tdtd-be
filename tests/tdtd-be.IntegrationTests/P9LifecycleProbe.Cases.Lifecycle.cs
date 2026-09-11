using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task RunLifecycleCasesAsync(CancellationToken ct)
    {
        await RunDraftCasesAsync(ct);
        await RunSubmitCasesAsync(ct);
        await RunApproveCasesAsync(ct);
        await RunEffectiveCasesAsync(ct);
        await RunOutboxCasesAsync(ct);
    }

    private async Task RunDraftCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-LFC-DRAFT-01",
            async () =>
            {
                var snapshot = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleDirectCounts(snapshot, 0, 0, "initial draft");
                HarnessAssert.Equal(
                    0,
                    BsonInt(await LoadLifecycleReportAsync(ct), "status"),
                    "Initial lifecycle fixture status");
                return new CaseObservation(
                    "Autonomous draft started in isolated Mongo with all six Direct owners and run jobs at zero rows.",
                    $"state=DRAFT;direct={LifecycleSnapshotSha256(snapshot)};writes=0");
            },
            ct);

        await RunCaseAsync(
            "P9-LFC-DRAFT-02",
            async () =>
            {
                var before = await CaptureLifecycleDirectSnapshotAsync(ct);
                var response = await RequireApi().PutAsync(
                    $"api/work-assignment-reports/{Fixture().ReportId}/draft",
                    _lfcSaveRequest!.DeepClone(),
                    Actor("executor").Token,
                    ct: ct);
                ExpectSuccess(response, "P9-LFC draft edit");
                var report = await LoadLifecycleReportAsync(ct);
                HarnessAssert.Equal(0, BsonInt(report, "status"), "Draft edit status");
                HarnessAssert.Equal(2, BsonInt(report, "payloadRevision"), "Draft edit payload revision");
                var after = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleSnapshotEqual(before, after, "draft edit");
                AddLifecycleMilestone("DRAFT_EDITED", after);
                return new CaseObservation(
                    "PUT /draft committed a payload edit through Kestrel while every official Direct owner remained byte-stable.",
                    $"http={(int)response.StatusCode};payloadRevision=2;delta=0;hash={LifecycleSnapshotSha256(after)}");
            },
            ct);

        await RunCaseAsync(
            "P9-LFC-DRAFT-03",
            async () =>
            {
                var before = await CaptureLifecycleDirectSnapshotAsync(ct);
                var replay = await RequireApi().PutAsync(
                    $"api/work-assignment-reports/{Fixture().ReportId}/draft",
                    _lfcSaveRequest!.DeepClone(),
                    Actor("executor").Token,
                    ct: ct);
                ExpectSuccess(replay, "P9-LFC draft replay");
                var report = await LoadLifecycleReportAsync(ct);
                HarnessAssert.Equal(2, BsonInt(report, "payloadRevision"), "Draft replay payload revision");
                var after = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleSnapshotEqual(before, after, "draft replay");
                return new CaseObservation(
                    "Exact draft command replay returned the durable revision without a second source or Direct mutation.",
                    $"command=p9-lfc-draft-save-001;payloadRevision=2;direct={LifecycleSnapshotSha256(after)}");
            },
            ct);

        await RunCaseAsync(
            "P9-LFC-DRAFT-04",
            async () =>
            {
                var fixture = Fixture();
                var recomputed = CanonicalJsonFileSha256(
                    Encoding.UTF8.GetBytes(fixture.ConfigPayloadJson));
                HarnessAssert.Equal(
                    fixture.ConfigHash,
                    recomputed,
                    "Locked configuration canonical hash");
                var activation = await EvaluateActivationAsync(
                    RequireApi(),
                    "DIRECT_FIELD_TABLE_LABEL",
                    "P9_LFC_DIRECT_PROJECTOR",
                    Actor("admin").Token,
                    ct);
                HarnessAssert.True(
                    RequiredBool(activation, "enabled"),
                    $"Published lifecycle activation failed: reason={activation["reason"]?.ToJsonString() ?? "<null>"}");
                var binding = ApiHarnessClient.RequiredObject(
                    activation["binding"],
                    "Published lifecycle activation binding");
                HarnessAssert.Equal(ChainId, RequiredString(binding, "chainId"), "Published lifecycle chain pin");
                HarnessAssert.Equal(LifecycleRuntimePromptId, RequiredString(binding, "promptId"), "Published lifecycle prompt pin");
                HarnessAssert.Equal(9, ApiHarnessClient.RequiredInt(binding, "stage"), "Published lifecycle stage");
                HarnessAssert.Equal("1.6", RequiredString(binding, "catalogVersion"), "Published lifecycle catalog version");
                HarnessAssert.Equal(LifecycleCatalogRawSha256, RequiredString(binding, "catalogRawSha256"), "Published lifecycle catalog raw pin");
                HarnessAssert.Equal(LifecycleCatalogSemanticSha256, RequiredString(binding, "catalogSemanticSha256"), "Published lifecycle catalog semantic pin");
                HarnessAssert.Equal(LifecycleStageLockSha256, RequiredString(binding, "stageLockSha256"), "Published lifecycle stage-lock pin");
                var snapshot = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleDirectCounts(snapshot, 0, 0, "draft config pin");
                return new CaseObservation(
                    "The locked P8 configuration and current published P9-12 lifecycle binding independently recomputed before lifecycle admission.",
                    $"config={recomputed};prompt={LifecycleRuntimePromptId};stage=9;stageLock={LifecycleStageLockSha256};writes=0");
            },
            ct);
    }

    private async Task RunSubmitCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-LFC-SUBMIT-01",
            async () =>
            {
                var report = await LoadLifecycleReportAsync(ct);
                _lfcSubmitRequest = new JsonObject
                {
                    ["expectedPayloadRevision"] = BsonInt(report, "payloadRevision"),
                    ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
                    ["commandId"] = "p9-lfc-submit-001"
                };
                var before = await CaptureLifecycleDirectSnapshotAsync(ct);
                var response = await RequireApi().PostAsync(
                    $"api/work-assignment-reports/{Fixture().ReportId}/submit",
                    _lfcSubmitRequest.DeepClone(),
                    Actor("executor").Token,
                    ct: ct);
                ExpectSuccess(response, "P9-LFC submit");
                var submitted = await LoadLifecycleReportAsync(ct);
                HarnessAssert.Equal(1, BsonInt(submitted, "status"), "Submitted status");
                HarnessAssert.Equal(2, BsonInt(submitted, "lifecycleRevision"), "Submitted lifecycle revision");
                var after = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleSnapshotEqual(before, after, "submit");
                AddLifecycleMilestone("SUBMITTED", after);
                return new CaseObservation(
                    "POST /submit committed the unapproved lifecycle revision through Kestrel with official Direct delta zero.",
                    $"http={(int)response.StatusCode};lifecycleRevision=2;direct={LifecycleSnapshotSha256(after)}");
            },
            ct);

        await RunCaseAsync(
            "P9-LFC-SUBMIT-02",
            async () =>
            {
                var report = await LoadLifecycleReportAsync(ct);
                var entry = FindOutboxEntry(report, "SUBMIT");
                HarnessAssert.Equal("p9-lfc-submit-001", BsonString(entry, "commandId"), "Submit outbox command");
                HarnessAssert.Equal(2, BsonInt(entry, "lifecycleRevision"), "Submit outbox lifecycle revision");
                HarnessAssert.Equal(BsonInt(report, "payloadRevision"), BsonInt(entry, "payloadRevision"), "Submit outbox payload revision");
                HarnessAssert.Equal(BsonString(report, "payloadHash"), BsonString(entry, "payloadHash"), "Submit outbox payload hash");
                var snapshot = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleDirectCounts(snapshot, 0, 0, "submit durable outbox");
                return new CaseObservation(
                    "Submit stored its deterministic command/source receipt in the same report aggregate, yet produced no Direct row or run.",
                    $"entry={BsonString(entry, "entryKey")};state={BsonString(entry, "state")};direct=0");
            },
            ct);

        await RunCaseAsync(
            "P9-LFC-SUBMIT-03",
            async () =>
            {
                var beforeReport = HashBytes((await LoadLifecycleReportAsync(ct)).ToBson());
                var before = await CaptureLifecycleDirectSnapshotAsync(ct);
                var replay = await RequireApi().PostAsync(
                    $"api/work-assignment-reports/{Fixture().ReportId}/submit",
                    _lfcSubmitRequest!.DeepClone(),
                    Actor("executor").Token,
                    ct: ct);
                ExpectSuccess(replay, "P9-LFC submit replay");
                var afterReport = HashBytes((await LoadLifecycleReportAsync(ct)).ToBson());
                HarnessAssert.Equal(beforeReport, afterReport, "Submit replay report bytes");
                var after = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleSnapshotEqual(before, after, "submit replay");
                return new CaseObservation(
                    "Exact submit replay was receipt-only and byte-stable for both report aggregate and Direct owners.",
                    $"report={afterReport};direct={LifecycleSnapshotSha256(after)}");
            },
            ct);

        await RunCaseAsync(
            "P9-LFC-SUBMIT-04",
            async () =>
            {
                var before = await CaptureLifecycleDirectSnapshotAsync(ct);
                var worker = await ProcessLifecycleOutboxAsync(20, ct);
                var after = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleSnapshotEqual(before, after, "unapproved worker drain");
                return new CaseObservation(
                    "Manual lifecycle worker drain acknowledged no new approved/effective source and left all official stores unchanged.",
                    $"http={(int)worker.StatusCode};processed={ReadProcessed(worker)};direct={LifecycleSnapshotSha256(after)}");
            },
            ct);
    }

    private async Task RunApproveCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-LFC-APPROVE-01",
            async () =>
            {
                await RequireDatabase()
                    .GetCollection<BsonDocument>("work_assignments")
                    .UpdateOneAsync(
                        new BsonDocument(
                            "_id",
                            ObjectId.Parse(Fixture().AssignmentId)),
                        Builders<BsonDocument>.Update.Set(
                            "createdByUserId",
                            ObjectId.Parse(Actor("admin").Id)),
                        cancellationToken: ct);
                var report = await LoadLifecycleReportAsync(ct);
                _lfcApproveRequest = new JsonObject
                {
                    ["expectedPayloadRevision"] = BsonInt(report, "payloadRevision"),
                    ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
                    ["commandId"] = BackendServerLease.P3LifecycleProjectionFailureCommandId,
                    ["comment"] = "P9-LFC approve with deterministic post-commit interruption"
                };
                var before = await CaptureLifecycleDirectSnapshotAsync(ct);
                var approve = await RequireApi().PostAsync(
                    $"api/work-assignment-review/reports/{Fixture().ReportId}/approve",
                    _lfcApproveRequest.DeepClone(),
                    Actor("admin").Token,
                    ct: ct);
                ExpectSuccess(approve, "P9-LFC approve post-commit fault");
                var approved = await LoadLifecycleReportAsync(ct);
                HarnessAssert.Equal(2, BsonInt(approved, "status"), "Approved status after injected fault");
                HarnessAssert.Equal(3, BsonInt(approved, "lifecycleRevision"), "Approved lifecycle revision after injected fault");
                var entry = FindOutboxEntry(approved, "REVIEW_APPROVE");
                _lfcApproveEventKey = BsonString(entry, "entryKey");
                HarnessAssert.Equal("PENDING", BsonString(entry, "state"), "Approve entry pending after injected fault");
                HarnessAssert.True(BsonInt(entry, "attemptCount") >= 1, "Approve entry did not record failed attempt");
                var after = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleSnapshotEqual(before, after, "approve post-commit interruption");
                AddLifecycleMilestone("APPROVED_INTENT_PENDING", after);
                _lfcQueueTrace.Add(new P9LifecycleQueueEvidence(
                    "APPROVE_POST_COMMIT_INTERRUPTED",
                    _lfcApproveEventKey,
                    BsonString(entry, "state"),
                    BsonInt(entry, "attemptCount"),
                    BsonNullableString(entry, "lastError"),
                    null,
                    null,
                    DateTime.UtcNow));
                return new CaseObservation(
                    "Approval source+outbox committed atomically; deterministic projection interruption left the intent pending and Direct delta zero.",
                    $"event={_lfcApproveEventKey};attempts={BsonInt(entry, "attemptCount")};direct=0");
            },
            ct);

        await RunCaseAsync(
            "P9-LFC-APPROVE-02",
            async () =>
            {
                var before = await CaptureLifecycleDirectSnapshotAsync(ct);
                var labels = RequireDatabase().GetCollection<BsonDocument>("labels");
                var labelFilter = new BsonDocument("code", LifecycleLabelCode);
                var disabled = await labels.UpdateOneAsync(
                    labelFilter,
                    Builders<BsonDocument>.Update.Set("isActive", false),
                    cancellationToken: ct);
                HarnessAssert.Equal(1L, disabled.MatchedCount, "Lifecycle label toggle match");
                var activation = await EvaluateActivationAsync(
                    RequireApi(),
                    "DIRECT_FIELD_TABLE_LABEL",
                    "P9_LFC_DIRECT_PROJECTOR",
                    Actor("admin").Token,
                    ct);
                HarnessAssert.True(
                    RequiredBool(activation, "enabled"),
                    $"Lifecycle Direct activation changed while a domain label was inactive. " +
                    $"reason={activation["reason"]?.ToJsonString() ?? "<null>"}");
                ApiHarnessResponse[] workers;
                try
                {
                    workers = await Task.WhenAll(
                        ProcessLifecycleOutboxAsync(20, ct),
                        ProcessLifecycleOutboxAsync(20, ct));
                }
                finally
                {
                    await labels.UpdateOneAsync(
                        labelFilter,
                        Builders<BsonDocument>.Update.Set("isActive", true),
                        cancellationToken: ct);
                }
                foreach (var worker in workers)
                    ApiHarnessClient.ExpectStatus(worker, HttpStatusCode.OK, "P9-LFC concurrent lifecycle worker");
                HarnessAssert.True(workers.Sum(ReadProcessed) >= 1, "Concurrent workers did not process approved intent");
                var report = await LoadLifecycleReportAsync(ct);
                var entry = FindOutboxEntry(report, "REVIEW_APPROVE");
                HarnessAssert.Equal("COMPLETED", BsonString(entry, "state"), "Approve outbox terminal state");
                HarnessAssert.Equal("PUBLISHED", BsonString(entry, "directProjectionState"), "Approve Direct link state");
                _lfcRunId = BsonString(entry, "directProjectionRunId");
                _lfcGenerationId = BsonString(entry, "directProjectionGenerationId");
                _lfcGenerationHash = BsonString(entry, "directProjectionGenerationHash");
                _lfcPublishedApproveEntry = (BsonDocument)entry.DeepClone();
                var after = await CaptureLifecycleDirectSnapshotAsync(ct);
                await AssertPublishedGenerationAsync(after, ct);
                _lfcPublishedDirect = after;
                AddLifecycleMilestone("DIRECT_GENERATION_PUBLISHED", after);
                _lfcQueueTrace.Add(new P9LifecycleQueueEvidence(
                    "CONCURRENT_WORKERS_CONVERGED",
                    _lfcApproveEventKey!,
                    BsonString(entry, "state"),
                    BsonInt(entry, "attemptCount"),
                    BsonNullableString(entry, "lastError"),
                    _lfcRunId,
                    _lfcGenerationId,
                    DateTime.UtcNow));
                return new CaseObservation(
                    "Two real Kestrel worker deliveries converged to one completed, all-six-store immutable generation while a post-approval live label toggle could not change the approved payload.",
                    $"processed={string.Join('+', workers.Select(ReadProcessed))};run={_lfcRunId};generation={_lfcGenerationId};labelDependency=approved-payload");
            },
            ct);

        await RunCaseAsync(
            "P9-LFC-APPROVE-03",
            async () =>
            {
                var before = await CaptureLifecycleDirectSnapshotAsync(ct);
                var replay = await RequireApi().PostAsync(
                    $"api/work-assignment-review/reports/{Fixture().ReportId}/approve",
                    _lfcApproveRequest!.DeepClone(),
                    Actor("admin").Token,
                    ct: ct);
                ExpectSuccess(replay, "P9-LFC approve replay");
                var drain = await ProcessLifecycleOutboxAsync(20, ct);
                var after = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleSnapshotEqual(before, after, "approve duplicate/replay");
                await AssertPublishedGenerationAsync(after, ct);
                return new CaseObservation(
                    "Duplicate approval delivery and an extra worker drain replayed the same receipt without another generation or row.",
                    $"approve={(int)replay.StatusCode};processed={ReadProcessed(drain)};generation={_lfcGenerationId};hash={LifecycleSnapshotSha256(after)}");
            },
            ct);

        await RunCaseAsync(
            "P9-LFC-APPROVE-04",
            async () =>
            {
                var job = await LoadLifecycleJobAsync(ct);
                var work = await RequireDatabase()
                    .GetCollection<BsonDocument>("works")
                    .Find(new BsonDocument(
                        "_id",
                        ObjectId.Parse(Fixture().WorkId)))
                    .SingleAsync(ct);
                HarnessAssert.Equal("LIFECYCLE_DIRECT_PROJECTION", BsonString(job, "runKind"), "Lifecycle job kind");
                HarnessAssert.Equal("COMPLETED", BsonString(job, "status"), "Lifecycle job status");
                HarnessAssert.True(BsonBool(job, "isCurrentPublication"), "Completed lifecycle job is not the current publication");
                HarnessAssert.Equal(Fixture().ReportId, BsonString(job, "sourceReportId"), "Lifecycle job source report pin");
                HarnessAssert.Equal(_lfcApproveEventKey, BsonString(job, "sourceLifecycleEventKey"), "Lifecycle job event pin");
                HarnessAssert.Equal(_lfcGenerationId, BsonString(job, "generationId"), "Lifecycle job generation pin");
                HarnessAssert.Equal(_lfcGenerationHash, BsonString(job, "generationHash"), "Lifecycle job generation hash");
                HarnessAssert.Equal(Fixture().ConfigHash, BsonString(job, "configHash"), "Lifecycle job config hash");
                HarnessAssert.Equal(ChainId, BsonString(job, "candidateChainId"), "Lifecycle job candidate chain");
                HarnessAssert.Equal(LifecycleRuntimePromptId, BsonString(job, "candidatePromptId"), "Lifecycle job candidate prompt");
                HarnessAssert.Equal("1.6", BsonString(job, "catalogVersion"), "Lifecycle job catalog version");
                HarnessAssert.Equal(LifecycleCatalogRawSha256, BsonString(job, "catalogRawSha256"), "Lifecycle job catalog raw pin");
                HarnessAssert.Equal(LifecycleCatalogSemanticSha256, BsonString(job, "catalogSemanticSha256"), "Lifecycle job catalog semantic pin");
                HarnessAssert.Equal(LifecycleStageLockSha256, BsonString(job, "stageLockSha256"), "Lifecycle job stage lock");
                HarnessAssert.Equal(Fixture().TemplateId, BsonString(job, "dynamicFormFamilyId"), "Lifecycle job form family pin");
                HarnessAssert.Equal(Fixture().TemplateId, BsonString(job, "dynamicFormTemplateId"), "Lifecycle job form template pin");
                HarnessAssert.Equal(1, BsonInt(job, "dynamicFormVersionNo"), "Lifecycle job form version pin");
                HarnessAssert.Equal(
                    BsonString(await LoadLifecycleReportAsync(ct), "dynamicFormSchemaHash"),
                    BsonString(job, "dynamicFormSchemaHash"),
                    "Lifecycle job form schema pin");
                HarnessAssert.True(IsCanonicalSha(BsonString(job, "sourceMembershipSignature")), "Lifecycle job membership signature");
                HarnessAssert.True(BsonLong(job, "directSourceRevision") > 0, "Lifecycle job source revision fence");
                HarnessAssert.Equal(
                    BsonLong(work, "directSourceRevision"),
                    BsonLong(job, "directSourceRevision"),
                    "Lifecycle job/work source revision fence");
                HarnessAssert.True(BsonLong(job, "directPublicationRevision") > 0, "Lifecycle job publication revision");
                HarnessAssert.Equal(
                    BsonLong(work, "directPublicationRevision"),
                    BsonLong(job, "directPublicationRevision"),
                    "Lifecycle job/work publication revision");
                var digests = job.GetValue("directStoreDigests", new BsonArray()).AsBsonArray;
                HarnessAssert.Equal(6, digests.Count, "Lifecycle job direct-store digest count");
                HarnessAssert.True(digests.All(value => BsonLong(value.AsBsonDocument, "rowCount") > 0), "A Direct store digest had no rows");
                return new CaseObservation(
                    "The completed current job is the sole publication root and pins receipt, source/config/catalog/stage, source/publication revisions, membership, six digests and generation hash.",
                    $"run={_lfcRunId};sourceRevision={BsonLong(job, "directSourceRevision")};publicationRevision={BsonLong(job, "directPublicationRevision")};digests=6;generationHash={_lfcGenerationHash}");
            },
            ct);
    }
}
