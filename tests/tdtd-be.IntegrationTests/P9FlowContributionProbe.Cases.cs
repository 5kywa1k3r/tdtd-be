using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task RunFlowContributionCasesAsync(CancellationToken ct)
    {
        await RunFlowExcludeCasesAsync(ct);
        await RunFlowIncludeCasesAsync(ct);
        await RunFlowApplyCasesAsync(ct);
        await RunFlowReversalCasesAsync(ct);
    }

    private async Task RunFlowExcludeCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-FLW-EXCLUDE-01",
            async () =>
            {
                var response = await RequireApi().PutAsync(
                    $"api/work-assignment-reports/{Fixture().ReportId}/draft",
                    _lfcSaveRequest!.DeepClone(),
                    Actor("executor").Token,
                    ct: ct);
                ExpectSuccess(response, "P9-FLW mapped draft save");
                await SeedCanonicalP7MappingLineageAsync(ct);
                var report = await LoadLifecycleReportAsync(ct);
                HarnessAssert.Equal("EXCLUDE", BsonString(report, "cumulativeContributionMode"), "P7 mapped report mode");
                HarnessAssert.Equal(_flwMappingReceiptId, BsonString(report, "dynamicFlowMappingReceiptId"), "P7 receipt header");
                HarnessAssert.Equal(_flwMappingProvenanceId, BsonString(report, "dynamicFlowMappingProvenanceId"), "P7 provenance header");
                return new CaseObservation(
                    "The Kestrel-authored draft was bound to one canonical P7 apply receipt/provenance and retained P7-owned EXCLUDE on the report.",
                    $"http={(int)response.StatusCode};receipt={_flwMappingReceiptId};provenance={_flwMappingProvenanceId};mode=EXCLUDE");
            },
            ct);

        await RunCaseAsync(
            "P9-FLW-EXCLUDE-02",
            async () =>
            {
                var report = await LoadLifecycleReportAsync(ct);
                _lfcSubmitRequest = new JsonObject
                {
                    ["expectedPayloadRevision"] = BsonInt(report, "payloadRevision"),
                    ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
                    ["commandId"] = "p9-flw-submit-001"
                };
                var before = await CaptureLifecycleDirectSnapshotAsync(ct);
                var response = await RequireApi().PostAsync(
                    $"api/work-assignment-reports/{Fixture().ReportId}/submit",
                    _lfcSubmitRequest.DeepClone(),
                    Actor("executor").Token,
                    ct: ct);
                ExpectSuccess(response, "P9-FLW submit");
                var drain = await ProcessLifecycleOutboxAsync(20, ct);
                var after = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleSnapshotEqual(before, after, "P9-FLW submit EXCLUDE");
                return new CaseObservation(
                    "Submit carried the exact mapping header into lifecycle lineage while all Direct owners stayed zero.",
                    $"http={(int)response.StatusCode};processed={ReadProcessed(drain)};payloadRevision={BsonInt(await LoadLifecycleReportAsync(ct), "payloadRevision")};delta=0");
            },
            ct);

        await RunCaseAsync(
            "P9-FLW-EXCLUDE-03",
            async () =>
            {
                await RequireDatabase().GetCollection<BsonDocument>("work_assignments")
                    .UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(Fixture().AssignmentId)),
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
                    ["comment"] = "P9-FLW EXCLUDE then exact INCLUDE replay"
                };
                var approve = await RequireApi().PostAsync(
                    $"api/work-assignment-review/reports/{Fixture().ReportId}/approve",
                    _lfcApproveRequest.DeepClone(),
                    Actor("admin").Token,
                    ct: ct);
                ExpectSuccess(approve, "P9-FLW approve EXCLUDE");
                var terminal = await DrainFlowApprovalStateAsync("ZERO_WRITE", ct);
                var entry = terminal.Entry;
                _flwApprovalEventKey = BsonString(entry, "entryKey");
                HarnessAssert.Equal("ZERO_WRITE", BsonString(entry, "directProjectionState"), "EXCLUDE Direct state");
                HarnessAssert.Equal("SOURCE_NOT_EFFECTIVE", BsonString(entry, "directProjectionReason"), "EXCLUDE reason");
                _lfcPublishedApproveEntry = (BsonDocument)entry.DeepClone();
                return new CaseObservation(
                    "The locked V_EXCLUDE approval was consumed by real workers and terminally linked ZERO_WRITE.",
                    $"approve={(int)approve.StatusCode};processed={string.Join('+', terminal.Workers.Select(ReadProcessed))};event={_flwApprovalEventKey};state=ZERO_WRITE");
            },
            ct);

        await RunCaseAsync(
            "P9-FLW-EXCLUDE-04",
            async () =>
            {
                _flwExcludedSnapshot = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleDirectCounts(_flwExcludedSnapshot, 0, 0, "V_EXCLUDE publication boundary");
                return new CaseObservation(
                    "V_EXCLUDE produced exactly zero rows in all six Direct stores and zero rebuild-job/receipt owner rows.",
                    $"snapshot={LifecycleSnapshotSha256(_flwExcludedSnapshot)};directRows=0;jobs=0");
            },
            ct);

        await RunCaseAsync(
            "P9-FLW-EXCLUDE-05",
            async () =>
            {
                var version = await RequireDatabase()
                    .GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
                    .Find(item => item.Id == _flwExcludeVersionId)
                    .SingleAsync(ct);
                DynamicFlowContributionPolicyContract.ValidateLockedPolicy(version);
                HarnessAssert.Equal(DynamicFlowContributionPolicyContract.Exclude, version.ContributionPolicy, "locked default EXCLUDE");
                HarnessAssert.Equal(_flwMappingBytesBefore, await CaptureP7MappingBytesAsync(ct), "EXCLUDE P7 lineage bytes");
                return new CaseObservation(
                    "Default exclusion was hash-valid and read-only over the P7 receipt/provenance bytes.",
                    $"version={version.Id};policyHash={version.ContributionPolicyHash};mappingBytes={_flwMappingBytesBefore}");
            },
            ct);
    }

    private async Task RunFlowIncludeCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-FLW-INCLUDE-01",
            async () =>
            {
                await SwitchToLockedIncludeVersionAsync(ct);
                var versions = RequireDatabase().GetCollection<DynamicFlowTemplateVersion>(
                    "dynamic_flow_template_versions");
                var exclude = await versions.Find(item => item.Id == _flwExcludeVersionId).SingleAsync(ct);
                var include = await versions.Find(item => item.Id == _flwIncludeVersionId).SingleAsync(ct);
                DynamicFlowContributionPolicyContract.ValidateLockedPolicy(include);
                DynamicFlowContributionPolicyContract.EnsureIncludeOrigin(include, exclude);
                HarnessAssert.Equal(exclude.PayloadHash, include.PayloadHash, "V_INCLUDE baseline payload hash");
                HarnessAssert.Equal(DynamicFlowContributionPolicyContract.Include, include.ContributionPolicy, "V_INCLUDE policy");
                return new CaseObservation(
                    "V_INCLUDE was an exact-version opt-in whose locked origin is V_EXCLUDE and whose P7 payload bytes/hash are unchanged.",
                    $"exclude={exclude.Id};include={include.Id};payloadHash={include.PayloadHash};policyHash={include.ContributionPolicyHash}");
            },
            ct);

        await RunCaseAsync(
            "P9-FLW-INCLUDE-02",
            async () =>
            {
                var excludedEntry = (BsonDocument)_lfcPublishedApproveEntry!.DeepClone();
                await ReopenApproveEntryAsync(excludedEntry, null, ct);
                var workers = await Task.WhenAll(
                    ProcessLifecycleOutboxAsync(20, ct),
                    ProcessLifecycleOutboxAsync(20, ct));
                var terminal = await DrainFlowApprovalStateAsync("PUBLISHED", ct);
                var entry = terminal.Entry;
                HarnessAssert.Equal("PUBLISHED", BsonString(entry, "directProjectionState"), "V_INCLUDE Direct state");
                _lfcRunId = _flwRunId = BsonString(entry, "directProjectionRunId");
                _lfcGenerationId = _flwGenerationId = BsonString(entry, "directProjectionGenerationId");
                _lfcGenerationHash = BsonString(entry, "directProjectionGenerationHash");
                _lfcApproveEventKey = _flwApprovalEventKey;
                _lfcPublishedApproveEntry = (BsonDocument)entry.DeepClone();
                _flwIncludedSnapshot = await CaptureLifecycleDirectSnapshotAsync(ct);
                await AssertFlowPublishedGenerationAsync(_flwIncludedSnapshot, ct);
                return new CaseObservation(
                    "Two workers replayed the same approval under exact V_INCLUDE and converged to one immutable Direct generation.",
                    $"processed={string.Join('+', workers.Concat(terminal.Workers).Select(ReadProcessed))};run={_flwRunId};generation={_flwGenerationId}");
            },
            ct);

        await RunCaseAsync(
            "P9-FLW-INCLUDE-03",
            async () =>
            {
                _flwPublishedJob = await LoadLifecycleJobAsync(ct);
                HarnessAssert.Equal("INCLUDE", BsonString(_flwPublishedJob, "flowContributionPolicy"), "job contribution policy");
                HarnessAssert.Equal("P9_CONTRIBUTION_V2", BsonString(_flwPublishedJob, "flowContributionOperationVersion"), "operation version");
                HarnessAssert.Equal(1, BsonInt(_flwPublishedJob, "flowContributionSourceCount"), "flow source audit count");
                var expectedTargetCount = new[]
                    {
                        "work_report_field_stat_values",
                        "work_report_table_stat_values",
                        "work_report_label_stat_values"
                    }
                    .Sum(name => checked((int)_flwIncludedSnapshot![name].Count));
                HarnessAssert.True(expectedTargetCount > 0, "flow fixture has no value targets");
                HarnessAssert.Equal(expectedTargetCount, BsonInt(_flwPublishedJob, "flowContributionTargetCount"), "flow target audit count");
                _flwLedgerHash = BsonString(_flwPublishedJob, "flowContributionLedgerHash");
                _flwReversalBaselineHash = BsonString(_flwPublishedJob, "flowContributionReversalBaselineHash");
                HarnessAssert.True(IsCanonicalSha(_flwLedgerHash), "flow ledger hash");
                HarnessAssert.True(IsCanonicalSha(_flwReversalBaselineHash), "reversal baseline hash");
                return new CaseObservation(
                    "The existing rebuild job/receipt owner persisted one source audit plus per-target contribution identities; no Flow value store was created.",
                    $"sources=1;targets={BsonInt(_flwPublishedJob, "flowContributionTargetCount")};ledger={_flwLedgerHash}");
            },
            ct);

        await RunCaseAsync(
            "P9-FLW-INCLUDE-04",
            async () =>
            {
                var source = _flwPublishedJob!
                    .GetValue("flowContributionSources").AsBsonArray.Single().AsBsonDocument;
                HarnessAssert.Equal(Fixture().ReportId, BsonString(source, "sourceReportId"), "source audit report");
                HarnessAssert.Equal(_flwMappingReceiptId, BsonString(source, "mappingReceiptId"), "source audit receipt");
                HarnessAssert.Equal(_flwMappingProvenanceId, BsonString(source, "mappingProvenanceId"), "source audit provenance");
                HarnessAssert.Equal(_flwMappingProvenanceHash, BsonString(source, "mappingProvenanceHash"), "source audit provenance hash");
                HarnessAssert.Equal(_flwExcludeVersionId, BsonString(source, "mappingFlowVersionId"), "mapping baseline version");
                HarnessAssert.Equal(_flwIncludeVersionId, BsonString(source, "flowTemplateVersionId"), "effective include version");
                return new CaseObservation(
                    "The source audit links the original P7 mapping version/receipt/provenance to the exact effective V_INCLUDE version.",
                    $"receipt={_flwMappingReceiptId};provenance={_flwMappingProvenanceId};mappingVersion={_flwExcludeVersionId};effectiveVersion={_flwIncludeVersionId}");
            },
            ct);

        await RunCaseAsync(
            "P9-FLW-INCLUDE-05",
            async () =>
            {
                var recomputed = CanonicalJsonFileSha256(
                    Encoding.UTF8.GetBytes(Fixture().ConfigPayloadJson));
                HarnessAssert.Equal(Fixture().ConfigHash, recomputed, "locked config hash");
                HarnessAssert.Equal(FlowContributionStageLockSha256, BsonString(_flwPublishedJob!, "stageLockSha256"), "stage-5 lock pin");
                HarnessAssert.Equal(_flwMappingBytesBefore, await CaptureP7MappingBytesAsync(ct), "INCLUDE P7 lineage bytes");
                return new CaseObservation(
                    "Locked config, candidate stage and byte-exact P7 lineage remained independently recomputable after inclusion.",
                    $"config={recomputed};stage={FlowContributionStageLockSha256};mappingBytes={_flwMappingBytesBefore}");
            },
            ct);
    }

    private async Task RunFlowApplyCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-FLW-APPLY-01",
            async () =>
            {
                HarnessAssert.True(_flwExcludedSnapshot is not null && _flwIncludedSnapshot is not null, "flow snapshots missing");
                AssertLifecycleDirectCounts(_flwExcludedSnapshot!, 0, 0, "excluded delta source");
                HarnessAssert.Equal(1L, _flwIncludedSnapshot![LifecycleJobCollection].Count, "include job delta");
                HarnessAssert.True(
                    LifecycleDirectCollections.Sum(name => _flwIncludedSnapshot[name].Count) > 0,
                    "include did not populate any Direct target");
                var digests = _flwPublishedJob!.GetValue("directStoreDigests").AsBsonArray;
                HarnessAssert.Equal(LifecycleDirectCollections.Length, digests.Count, "six-store digest coverage");
                return new CaseObservation(
                    "The exact sparse target delta moved from zero to one generation whose digest ledger still covers all six Direct owners; Basic/Advanced consume those same owners.",
                    $"before={LifecycleSnapshotSha256(_flwExcludedSnapshot!)};after={LifecycleSnapshotSha256(_flwIncludedSnapshot)};stores=6;nonEmptyStores={LifecycleDirectCollections.Count(name => _flwIncludedSnapshot[name].Count > 0)};jobs=1");
            },
            ct);

        await RunCaseAsync(
            "P9-FLW-APPLY-02",
            async () =>
            {
                var before = await CaptureLifecycleDirectSnapshotAsync(ct);
                var replay = await RequireApi().PostAsync(
                    $"api/work-assignment-review/reports/{Fixture().ReportId}/approve",
                    _lfcApproveRequest!.DeepClone(),
                    Actor("admin").Token,
                    ct: ct);
                ExpectSuccess(replay, "P9-FLW approval replay");
                await ProcessLifecycleOutboxAsync(20, ct);
                var after = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleSnapshotEqual(before, after, "exact include replay");
                return new CaseObservation(
                    "Exact approval/API replay returned the original receipt and did not create a second generation, job or target row.",
                    $"http={(int)replay.StatusCode};snapshot={LifecycleSnapshotSha256(after)};jobs=1");
            },
            ct);

        await RunCaseAsync(
            "P9-FLW-APPLY-03",
            async () =>
            {
                var before = await CaptureLifecycleDirectSnapshotAsync(ct);
                await ReopenApproveEntryAsync(_lfcPublishedApproveEntry!, null, ct);
                var workers = await Task.WhenAll(
                    ProcessLifecycleOutboxAsync(20, ct),
                    ProcessLifecycleOutboxAsync(20, ct),
                    ProcessLifecycleOutboxAsync(20, ct));
                var after = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertLifecycleSnapshotEqual(before, after, "concurrent include replay");
                _lfcPublishedApproveEntry = (BsonDocument)FindOutboxEntry(
                    await LoadLifecycleReportAsync(ct),
                    "REVIEW_APPROVE").DeepClone();
                return new CaseObservation(
                    "Three concurrent deliveries converged through the run ID, row IDs and completed-ledger replay fence.",
                    $"processed={string.Join('+', workers.Select(ReadProcessed))};snapshot={LifecycleSnapshotSha256(after)};ledger={_flwLedgerHash}");
            },
            ct);

        await RunCaseAsync(
            "P9-FLW-APPLY-04",
            async () =>
            {
                var targets = _flwPublishedJob!.GetValue("flowContributionTargets").AsBsonArray
                    .Select(value => value.AsBsonDocument)
                    .ToArray();
                HarnessAssert.Equal(targets.Length, targets.Select(target => BsonString(target, "contributionId")).Distinct(StringComparer.Ordinal).Count(), "contribution identity uniqueness");
                HarnessAssert.Equal(targets.Length, targets.Select(target => $"{BsonString(target, "targetStore")}:{BsonString(target, "targetStatisticId")}").Distinct(StringComparer.Ordinal).Count(), "target overlap uniqueness");
                foreach (var target in targets)
                {
                    var exists = await RequireDatabase().GetCollection<BsonDocument>(BsonString(target, "targetStore"))
                        .Find(new BsonDocument("_id", ObjectId.Parse(BsonString(target, "targetStatisticId"))))
                        .AnyAsync(ct);
                    HarnessAssert.True(exists, "audited target row is missing");
                }
                return new CaseObservation(
                    "Every overlapping target is represented once by store+row identity and every ledger target resolves to a persisted Direct row.",
                    $"targets={targets.Length};uniqueContributions={targets.Length};uniqueStoreRows={targets.Length}");
            },
            ct);

        await RunCaseAsync(
            "P9-FLW-APPLY-05",
            async () =>
            {
                HarnessAssert.Equal(_flwMappingBytesBefore, await CaptureP7MappingBytesAsync(ct), "apply/replay P7 mapping bytes");
                var collections = (await (await RequireDatabase().ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct));
                var forbidden = collections.Where(name => name.Contains("flow_stat", StringComparison.OrdinalIgnoreCase) || name.Contains("flow_contribution_value", StringComparison.OrdinalIgnoreCase)).ToArray();
                HarnessAssert.Equal(0, forbidden.Length, "parallel Flow statistic store count");
                return new CaseObservation(
                    "Apply/replay never mutated P7 lineage and introduced no parallel Flow statistics-value collection.",
                    $"mappingBytes={_flwMappingBytesBefore};parallelStores=0;owners=6+job");
            },
            ct);
    }

    private async Task RunFlowReversalCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-FLW-REVERSAL-01",
            async () =>
            {
                var job = await LoadLifecycleJobAsync(ct);
                var sourceReversals = job.GetValue("flowContributionSources").AsBsonArray
                    .Select(value => BsonString(value.AsBsonDocument, "reversalIdentity"))
                    .ToArray();
                var targetReversals = job.GetValue("flowContributionTargets").AsBsonArray
                    .Select(value => BsonString(value.AsBsonDocument, "reversalIdentity"))
                    .ToArray();
                HarnessAssert.True(sourceReversals.Concat(targetReversals).All(IsCanonicalSha), "inverse identity hash");
                HarnessAssert.Equal(sourceReversals.Length + targetReversals.Length, sourceReversals.Concat(targetReversals).Distinct(StringComparer.Ordinal).Count(), "inverse identity uniqueness");
                return new CaseObservation(
                    "Each applied source/target has a canonical deterministic inverse identity for P9-08 without executing reversal here.",
                    $"sourceInverse={sourceReversals.Length};targetInverse={targetReversals.Length};baseline={_flwReversalBaselineHash}");
            },
            ct);

        await RunCaseAsync(
            "P9-FLW-REVERSAL-02",
            async () =>
            {
                var job = await LoadLifecycleJobAsync(ct);
                HarnessAssert.Equal(_flwLedgerHash, BsonString(job, "flowContributionLedgerHash"), "ledger replay hash");
                HarnessAssert.Equal(_flwReversalBaselineHash, BsonString(job, "flowContributionReversalBaselineHash"), "reversal replay hash");
                HarnessAssert.Equal(BsonInt(job, "flowContributionTargetCount"), job.GetValue("flowContributionTargets").AsBsonArray.Count, "target count fence");
                return new CaseObservation(
                    "The immutable ledger and reversal baseline survived all retries/races byte-for-byte and count-fence every target.",
                    $"ledger={_flwLedgerHash};reversalBaseline={_flwReversalBaselineHash};targets={BsonInt(job, "flowContributionTargetCount")}");
            },
            ct);

        await RunCaseAsync(
            "P9-FLW-REVERSAL-03",
            async () =>
            {
                var before = await CaptureLifecycleDirectSnapshotAsync(ct);
                var provenances = RequireDatabase().GetCollection<BsonDocument>("dynamic_flow_mapping_provenance");
                try
                {
                    await provenances.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(_flwMappingProvenanceId!)),
                        Builders<BsonDocument>.Update.Set("state", DynamicFlowMappingProvenanceStates.Invalidated),
                        cancellationToken: ct);
                    await ReopenApproveEntryAsync(_lfcPublishedApproveEntry!, null, ct);
                    await ProcessLifecycleOutboxAsync(20, ct);
                    var after = await CaptureLifecycleDirectSnapshotAsync(ct);
                    AssertLifecycleSnapshotEqual(before, after, "stale mapping zero target mutation");
                }
                finally
                {
                    await provenances.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(_flwMappingProvenanceId!)),
                        Builders<BsonDocument>.Update.Set("state", DynamicFlowMappingProvenanceStates.Current),
                        cancellationToken: ct);
                    await RestoreApproveEntryAsync(_lfcPublishedApproveEntry!, ct);
                }
                return new CaseObservation(
                    "An invalidated mapping provenance was rejected before target mutation; restoring it did not execute a second apply.",
                    $"provenance={_flwMappingProvenanceId};beforeAfter={LifecycleSnapshotSha256(before)};delta=0");
            },
            ct);

        await RunCaseAsync(
            "P9-FLW-REVERSAL-04",
            async () =>
            {
                var before = await CaptureDatabaseSnapshotAsync(ct);
                var profile = await RequireApi().PostAsync(
                    $"api/dynamic-flows/{Fixture().FlowFamilyId}/statistic-profile",
                    new { membership = new[] { Fixture().ReportId }, commandId = "p9-flw-spoofed-membership-019" },
                    Actor("admin").Token,
                    ct: ct);
                HarnessAssert.True(profile.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict, "blocked profile status");
                var after = await CaptureDatabaseSnapshotAsync(ct);
                HarnessAssert.Equal(SnapshotSha256(before), SnapshotSha256(after), "blocked profile/spoofed membership zero-write");
                _flwSecurityScanVerified = !(_flwPublishedJob?.ToJson() ?? string.Empty).Contains("password", StringComparison.OrdinalIgnoreCase) &&
                                           !(_flwPublishedJob?.ToJson() ?? string.Empty).Contains("connectionString", StringComparison.OrdinalIgnoreCase) &&
                                           !(_flwPublishedJob?.ToJson() ?? string.Empty).Contains("P9 autonomous source", StringComparison.Ordinal);
                HarnessAssert.True(_flwSecurityScanVerified, "flow ledger security scan");
                return new CaseObservation(
                    "Client-authored membership/profile input stayed blocked with full-database zero-write; the ledger contains hashes/IDs but no raw payload or secret material.",
                    $"http={(int)profile.StatusCode};db={SnapshotSha256(after)};securityScan=pass");
            },
            ct);

        await RunCaseAsync(
            "P9-FLW-REVERSAL-05",
            async () =>
            {
                var before = await CaptureDatabaseSnapshotAsync(ct);
                var p10 = await RequireApi().PostAsync(
                    "api/stat-config/barriers/P10_RECONCILE",
                    new
                    {
                        ownerKind = "UNIT",
                        ownerId = Fixture().UnitAId,
                        commandId = "p9-flw-p10-zero-write-020",
                        expectedBundleHash = Fixture().ConfigHash
                    },
                    Actor("admin").Token,
                    ct: ct);
                ExpectError(
                    p10,
                    HttpStatusCode.Conflict,
                    "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                    "P9-FLW P10 zero-write barrier");
                var after = await CaptureDatabaseSnapshotAsync(ct);
                HarnessAssert.Equal(SnapshotSha256(before), SnapshotSha256(after), "P10 barrier database snapshot");
                _flwP10ZeroWriteVerified = true;
                _flwIndexVerified = await VerifyFlowContributionIndexesAsync(ct);
                HarnessAssert.True(_flwIndexVerified, "flow contribution indexes/explain");
                return new CaseObservation(
                    "P10 remained 409/zero-write and live Mongo verified both bounded source/provenance reverse-lookup indexes.",
                    $"p10={(int)p10.StatusCode};db={SnapshotSha256(after)};indexes=2;ixscan=true");
            },
            ct);
    }

    private async Task<(BsonDocument Entry, IReadOnlyList<ApiHarnessResponse> Workers)>
        DrainFlowApprovalStateAsync(
            string expectedDirectState,
            CancellationToken ct)
    {
        var workers = new List<ApiHarnessResponse>();
        BsonDocument? observedEntry = null;
        for (var attempt = 0; attempt <= 8; attempt++)
        {
            var report = await LoadLifecycleReportAsync(ct);
            observedEntry = FindOutboxEntry(report, "REVIEW_APPROVE");
            if (string.Equals(
                    BsonNullableString(observedEntry, "directProjectionState"),
                    expectedDirectState,
                    StringComparison.Ordinal))
            {
                return ((BsonDocument)observedEntry.DeepClone(), workers);
            }

            if (attempt < 8)
                workers.Add(await ProcessLifecycleOutboxAsync(20, ct));
        }

        var lastError = BsonNullableString(observedEntry!, "lastError") ??
                        BsonNullableString(observedEntry!, "directProjectionReason") ??
                        "none";
        throw new InvalidOperationException(
            $"P9-FLW approval did not reach {expectedDirectState} after {workers.Count} drains. " +
            $"directState={BsonNullableString(observedEntry!, "directProjectionState") ?? "missing"};" +
            $"state={BsonNullableString(observedEntry!, "state") ?? "missing"};lastError={lastError}");
    }

    private async Task AssertFlowPublishedGenerationAsync(
        IReadOnlyDictionary<string, P9CollectionState> snapshot,
        CancellationToken ct)
    {
        HarnessAssert.True(
            !string.IsNullOrWhiteSpace(_lfcRunId) && ObjectId.TryParse(_lfcRunId, out _),
            "Published Flow Direct run id is missing or invalid");
        HarnessAssert.True(IsCanonicalSha(_lfcGenerationId), "Published Flow generation id is not SHA-256");
        HarnessAssert.True(IsCanonicalSha(_lfcGenerationHash), "Published Flow generation hash is not SHA-256");
        HarnessAssert.Equal(1L, snapshot[LifecycleJobCollection].Count, "Published Flow job count");

        var job = await LoadLifecycleJobAsync(ct);
        var digests = job.GetValue("directStoreDigests").AsBsonArray
            .Select(value => value.AsBsonDocument)
            .ToDictionary(
                value => BsonString(value, "store"),
                value => value,
                StringComparer.Ordinal);
        HarnessAssert.Equal(LifecycleDirectCollections.Length, digests.Count, "Flow six-store digest count");

        long totalRows = 0;
        foreach (var collection in LifecycleDirectCollections)
        {
            HarnessAssert.True(snapshot.TryGetValue(collection, out var state), $"Flow snapshot lacks {collection}");
            HarnessAssert.True(digests.TryGetValue(collection, out var digest), $"Flow digest lacks {collection}");
            HarnessAssert.Equal(state!.Count, BsonLong(digest!, "rowCount"), $"Flow digest row count {collection}");
            HarnessAssert.True(IsCanonicalSha(BsonString(digest!, "sha256")), $"Flow digest hash {collection}");

            var rows = await RequireDatabase().GetCollection<BsonDocument>(collection)
                .Find(new BsonDocument("directProjection.generationId", _lfcGenerationId))
                .ToListAsync(ct);
            HarnessAssert.Equal(state.Count, rows.Count, $"Flow generation row coverage {collection}");
            HarnessAssert.True(
                rows.All(row =>
                    string.Equals(
                        BsonString(row.GetValue("directProjection").AsBsonDocument, "runId"),
                        _lfcRunId,
                        StringComparison.Ordinal)),
                $"Published Flow rows in {collection} do not share one run id");
            totalRows += state.Count;
        }

        HarnessAssert.True(totalRows > 0, "Published Flow generation has no Direct targets");
    }

    private async Task SwitchToLockedIncludeVersionAsync(CancellationToken ct)
    {
        var database = RequireDatabase();
        var include = await database.GetCollection<DynamicFlowTemplateVersion>(
                "dynamic_flow_template_versions")
            .Find(version => version.Id == _flwIncludeVersionId)
            .SingleAsync(ct);
        await database.GetCollection<BsonDocument>("dynamic_flow_templates")
            .UpdateOneAsync(
                new BsonDocument("_id", ObjectId.Parse(Fixture().FlowFamilyId)),
                Builders<BsonDocument>.Update
                    .Set("currentVersionId", ObjectId.Parse(include.Id))
                    .Set("currentVersionNo", include.VersionNo)
                    .Set("currentVersionHash", include.PayloadHash),
                cancellationToken: ct);
        await database.GetCollection<BsonDocument>("work_assignments")
            .UpdateOneAsync(
                new BsonDocument("_id", ObjectId.Parse(Fixture().AssignmentId)),
                Builders<BsonDocument>.Update.Set("flowTemplateVersionNo", include.VersionNo),
                cancellationToken: ct);
        await database.GetCollection<BsonDocument>("dynamic_flow_instances")
            .UpdateOneAsync(
                new BsonDocument("_id", ObjectId.Parse(Fixture().FlowInstanceId)),
                Builders<BsonDocument>.Update
                    .Set("flowTemplateVersionId", ObjectId.Parse(include.Id))
                    .Set("flowTemplateVersionNo", include.VersionNo),
                cancellationToken: ct);
    }

    private async Task<bool> VerifyFlowContributionIndexesAsync(CancellationToken ct)
    {
        var collection = RequireDatabase().GetCollection<BsonDocument>(
            LifecycleJobCollection);
        var names = (await (await collection.Indexes.ListAsync(ct)).ToListAsync(ct))
            .Select(index => index["name"].AsString)
            .ToHashSet(StringComparer.Ordinal);
        const string sourceIndex =
            "ix_workReportStatisticRebuildJobs_flow_contribution_source";
        const string provenanceIndex =
            "ix_workReportStatisticRebuildJobs_flow_contribution_provenance";
        if (!names.Contains(sourceIndex) || !names.Contains(provenanceIndex))
            return false;
        var explain = await RequireDatabase().RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["explain"] = new BsonDocument
                {
                    ["find"] = LifecycleJobCollection,
                    ["filter"] = new BsonDocument
                    {
                        ["flowContributionSources.sourceReportId"] = ObjectId.Parse(Fixture().ReportId),
                        ["isCurrentPublication"] = true
                    },
                    ["hint"] = sourceIndex,
                    ["limit"] = 20
                },
                ["verbosity"] = "executionStats"
            },
            cancellationToken: ct);
        var plan = explain["queryPlanner"].AsBsonDocument["winningPlan"].AsBsonDocument;
        var stages = FindBsonStrings(plan, "stage").ToArray();
        var indexes = FindBsonStrings(plan, "indexName").ToArray();
        var stats = explain["executionStats"].AsBsonDocument;
        return stages.Contains("IXSCAN", StringComparer.Ordinal) &&
               !stages.Contains("COLLSCAN", StringComparer.Ordinal) &&
               indexes.Contains(sourceIndex, StringComparer.Ordinal) &&
               stats.GetValue("totalDocsExamined", 0).ToInt64() <= 100;
    }
}
