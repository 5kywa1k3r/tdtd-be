using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

var rows = new List<object>(); var failures = 0;
var owner = Assembly.Load("tdtd-be.IntegrationTests").GetType("tdtd_be.IntegrationTests.P11ResultBrowserFixture", true)!;
var closeout = owner.Assembly.GetType("tdtd_be.IntegrationTests.P11ResultCloseoutContract", true)!;
object? Call(string method, params object[] args)
{
    try { return owner.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args); }
    catch (TargetInvocationException error) { throw error.InnerException!; }
}
void Require(bool condition, string code) { if (!condition) throw new InvalidOperationException(code); }
JsonObject Resolve(Case test) => (JsonObject)Call("ResolveAttempt040StaleVerdictIdentity", test.Browser, test.Run, test.Lineage, test.TotalReviewRecordCount)!;
object Assess(JsonNode browser) => closeout.GetMethod("AssessOraclePrerequisites",
    BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [browser])!;
T Assessment<T>(object value, string property) => (T)value.GetType().GetProperty(property)!.GetValue(value)!;
JsonNode CompleteBrowser(Case test)
{
    var browser = test.Browser.DeepClone(); var ids = browser["ids"]!.AsObject();
    foreach (var key in new[] { "formVersionId", "flowFamilyId", "flowVersionId", "workId", "flowInstanceId",
        "stepInstanceId", "assignmentId", "reportId", "statConfigId", "statConfigVersionId", "statFoundationJobId",
        "p9RunId", "p9ResultId", "reconciliationInitialP9RunId", "statCsvExportId", "statXlsxExportId",
        "reconciliationId", "permissionRemovedReviewerId" })
        if (!ids.ContainsKey(key)) ids[key] = Fixture.Id((char)('1' + Math.Abs(key.GetHashCode()) % 8));
    ids["p9ResultId"] = ids["p9RunId"]!.DeepClone();
    foreach (var key in new[] { "flowPayloadHash", "flowContributionPolicyHash", "statConfigHash", "statResultId",
        "reconciliationInitialP9GenerationId", "reconciliationInitialP8ConfigHash",
        "reconciliationInitialActualGenerationId", "reconciliationInitialGenerationId",
        "reconciliationStaleActualGenerationId", "reconciliationActualGenerationId", "reconciliationGenerationId",
        "reconciliationStaleRecheckMarkerId", "reconciliationRecheckMarkerId" })
        if (!ids.ContainsKey(key)) ids[key] = Fixture.Sha(key);
    ids["evidenceJsonArtifactId"] = Fixture.Sha("evidence-json");
    ids["evidenceCsvArtifactId"] = Fixture.Sha("evidence-csv");
    ids["launchCommandId"] = "attempt040-oracle-selftest";
    ids["flowContributionPolicy"] = "INCLUDE";
    ids["statConfigRevision"] = 2;
    var cases = new JsonArray();
    foreach (var prefix in new[] { "STAT", "RECON", "EVID" })
    {
        var count = prefix == "EVID" ? 4 : 6;
        for (var index = 1; index <= count; index++) cases.Add(new JsonObject {
            ["id"] = $"P11-{prefix}-{index:00}", ["status"] = "PASS" });
    }
    browser["p11Result"] = new JsonObject { ["expected"] = 16, ["passed"] = 16,
        ["failed"] = 0, ["notRun"] = 0, ["cases"] = cases };
    browser["verdict"] = "PASS";
    return JsonNode.Parse(browser.ToJsonString())!;
}
async Task Check(string name, Func<Task> action)
{
    try { await action(); rows.Add(new { name, status = "PASS" }); }
    catch (Exception error) { failures++; rows.Add(new { name, status = "FAIL", errorType = error.GetType().Name, code = error.Message }); }
}
await Check("real_canonical_three_verdict_producer_resolves_without_browser_verdict_id", async () => {
    var t = await Case.Create(); var beforeRun = t.Run.ToBson(); var beforeLineage = t.Lineage.Select(x => x.ToBson()).ToArray();
    var result = Resolve(t);
    Require(result["staleVerdictGenerationId"]!.GetValue<string>() == t.Lineage[1].VerdictGenerationId, "EXACT_STALE");
    Require(result["canonicalVerdictCount"]!.GetValue<int>() == 3 && result["receiptValidated"]!.GetValue<bool>(), "EXACT_CHAIN");
    Require(beforeRun.SequenceEqual(t.Run.ToBson()) && beforeLineage.Zip(t.Lineage).All(x => x.First.SequenceEqual(x.Second.ToBson())), "READ_ONLY");
    Require(!t.Browser["ids"]!.AsObject().ContainsKey("reconciliationStaleGenerationId"), "NO_FABRICATED_BROWSER_ID");
});
await Check("legacy_contract_does_not_enable_missing_id_fallback", async () => {
    var t = await Case.Create(); t.Browser["observations"]!["reconciliationStale"]!.AsObject().Remove("schemaVersion");
    Require((bool)Call("UsesAttempt040StaleVerdictOracle", t.Browser)! == false, "LEGACY_BRANCH_PRESERVED");
    try { Call("RequiredNodeString", t.Browser["ids"]!, "reconciliationStaleGenerationId"); throw new Exception("MISSING_ID_ACCEPTED"); }
    catch (Exception error) when (error is InvalidOperationException or StatisticReconciliationFinalVerdictException) { }
});
await Check("effective_current_view_preserves_original_creation_tuple", async () => {
    var t = await Case.Create(); var before = t.Run.ToBson();
    var current = (StatisticReconciliationRun)Call("RequireAttempt040ReconciliationCurrentView", t.Browser, t.Run)!;
    Require(current.P9RunId == t.Browser["ids"]!["p9RunId"]!.GetValue<string>() && current.P9RunId != t.Run.P9RunId, "SUCCESSOR_VIEW");
    Require(current.P8ConfigHash != t.Run.P8ConfigHash && !ReferenceEquals(current, t.Run), "CANONICAL_CLONE");
    Require(before.SequenceEqual(t.Run.ToBson()), "IMMUTABLE_CREATION_UNCHANGED");
});
await Check("closeout_gate_accepts_hash_artifacts_and_attempt040_oracle_protocol_after_serialization", async () => {
    var t = await Case.Create(); var browser = CompleteBrowser(t); var assessment = Assess(browser);
    Require(Assessment<bool>(assessment, "Ready"), "PREREQUISITES_NOT_READY");
    Require(browser["ids"]!["evidenceJsonArtifactId"]!.GetValue<string>().Length == 64, "ARTIFACT_NOT_HASH64");
    Require(!browser["ids"]!.AsObject().ContainsKey("reconciliationStaleGenerationId"), "STALE_ID_MUST_BE_ORACLE_ONLY");
    t.Browser = browser;
    Require(Resolve(t)["receiptValidated"]!.GetValue<bool>(), "GATE_TO_RESOLVER_FAILED");
});
await Check("closeout_gate_rejects_artifact_objectid_uppercase_and_fabricated_stale_id", async () => {
    var t = await Case.Create();
    foreach (var mutate in new Action<JsonNode>[] {
        b => b["ids"]!["evidenceJsonArtifactId"] = Fixture.Id('a'),
        b => b["ids"]!["evidenceCsvArtifactId"] = Fixture.Sha("upper").ToUpperInvariant(),
        b => b["ids"]!["reconciliationStaleGenerationId"] = Fixture.Sha("fabricated") })
    {
        var browser = CompleteBrowser(t); mutate(browser); var assessment = Assess(browser);
        Require(!Assessment<bool>(assessment, "Ready"), "INVALID_ID_ACCEPTED");
        Require(Assessment<IReadOnlyList<string>>(assessment, "InvalidPrerequisites").Count > 0,
            "INVALID_ID_NOT_CLASSIFIED");
    }
});
await Check("closeout_gate_requires_exact_attempt040_proof_or_legacy_stale_id", async () => {
    var t = await Case.Create(); var malformed = CompleteBrowser(t);
    malformed["observations"]!["reconciliationStale"]!.AsObject().Remove("verdictGenerationId");
    var malformedAssessment = Assess(malformed);
    Require(!Assessment<bool>(malformedAssessment, "Ready") &&
        Assessment<IReadOnlyList<string>>(malformedAssessment, "InvalidPrerequisites")
            .Contains("observations.reconciliationStale.oracleIdentityProtocol"), "MALFORMED_PROTOCOL_ACCEPTED");
    var legacy = CompleteBrowser(t);
    legacy["observations"]!["reconciliationStale"]!.AsObject().Remove("schemaVersion");
    legacy["ids"]!["reconciliationStaleGenerationId"] = Fixture.Sha("legacy-stale");
    Require(Assessment<bool>(Assess(legacy), "Ready"), "VALID_LEGACY_ID_REJECTED");
    legacy["ids"]!.AsObject().Remove("reconciliationStaleGenerationId");
    Require(Assessment<IReadOnlyList<string>>(Assess(legacy), "MissingPrerequisites")
        .Contains("ids.reconciliationStaleGenerationId"), "LEGACY_MISSING_ID_NOT_CLASSIFIED");
});
var browserMutants = new (string Name, Action<JsonNode> Mutate)[] {
    ("unknown_version", b => b["observations"]!["reconciliationStale"]!["schemaVersion"] = "P11_ATTEMPT041_STALE_REVIEW_RECOVERY_V1"),
    ("browser_fabricated_verdict", b => b["ids"]!["reconciliationStaleGenerationId"] = Fixture.Sha("fabricated")),
    ("browser_null_verdict_key", b => b["ids"]!["reconciliationStaleGenerationId"] = null),
    ("claimed_ui_identity", b => b["observations"]!["reconciliationStale"]!["verdictIdentitySource"] = "BROWSER"),
    ("claimed_supersession_count", b => b["observations"]!["reconciliationStale"]!["initialReviewSupersessionCount"] = 5),
    ("wrong_actual_id", b => b["ids"]!["reconciliationStaleActualGenerationId"] = Fixture.Sha("wrong")),
    ("wrong_initial_id", b => b["ids"]!["reconciliationInitialGenerationId"] = Fixture.Sha("wrong")),
    ("wrong_current_id", b => b["ids"]!["reconciliationGenerationId"] = Fixture.Sha("wrong")),
    ("wrong_detail_actual", b => b["observations"]!["reconciliationStale"]!["detail"]!["currentGenerationId"] = Fixture.Sha("wrong")),
    ("wrong_detail_actual_hash", b => b["observations"]!["reconciliationStale"]!["detail"]!["currentGenerationHash"] = Fixture.Sha("wrong")),
    ("wrong_detail_revision", b => b["observations"]!["reconciliationStale"]!["detail"]!["stateRevision"] = 8),
    ("wrong_detail_state_hash", b => b["observations"]!["reconciliationStale"]!["detail"]!["stateHash"] = Fixture.Sha("wrong")),
    ("review_200", b => b["observations"]!["reconciliationStale"]!["reviewRecovery"]!["review"]!["httpStatus"] = 200),
    ("wrong_problem", b => b["observations"]!["reconciliationStale"]!["reviewRecovery"]!["review"]!["code"] = "OTHER"),
    ("wrong_dom_phase", b => b["observations"]!["reconciliationStale"]!["reviewRecovery"]!["dom"]!["phase"] = "DETAIL"),
    ("wrong_dom_revision", b => b["observations"]!["reconciliationStale"]!["reviewRecovery"]!["dom"]!["stateRevision"] = "8"),
    ("review_gate_visible", b => b["observations"]!["reconciliationStale"]!["reviewRecovery"]!["dom"]!["reviewGateCount"] = 5),
    ("cta_missing", b => b["observations"]!["reconciliationStale"]!["reviewRecovery"]!["dom"]!["ctaTestIdCount"] = 0),
    ("detail_leak", b => b["observations"]!["reconciliationStale"]!["reviewRecovery"]!["network"]!["detailRequestCount"] = 1),
    ("ledger_order", b => b["observations"]!["reconciliationStale"]!["reviewRecovery"]!["network"]!["requestEndSequence"] = 0),
};
foreach (var (name, mutate) in browserMutants) await Check(name, async () => {
    var t = await Case.Create(); mutate(t.Browser);
    try { Resolve(t); throw new Exception("MUTANT_ACCEPTED"); } catch (Exception error) when (error is InvalidOperationException or StatisticReconciliationFinalVerdictException) { }
});
var storageMutants = new (string Name, Action<Case> Mutate)[] {
    ("missing_verdict", t => t.Lineage.RemoveAt(1)),
    ("duplicate_verdict", t => t.Lineage.Add(t.Lineage[1])),
    ("extra_unknown_review_kind", t => t.TotalReviewRecordCount++),
    ("missing_audit_record", t => t.TotalReviewRecordCount--),
    ("wrong_record_kind", t => t.Lineage[1].RecordKind = "REVIEW_DECISION"),
    ("wrong_reconciliation", t => t.Lineage[1].ReconciliationId = Fixture.Id('e')),
    ("canonical_semantic_tamper", t => t.Lineage[1].DocumentSemanticSha256 = Fixture.Sha("wrong")),
    ("chain_splice", t => t.Lineage[2].SupersedesVerdictGenerationId = t.Lineage[0].VerdictGenerationId),
    ("missing_receipt", t => t.Run.CurrentRecheckFinalizeReceipt = null),
    ("receipt_hash_tamper", t => t.Run.CurrentRecheckFinalizeReceipt!.ReceiptSha256 = Fixture.Sha("wrong")),
    ("receipt_base_rehashed", t => { t.Run.CurrentRecheckFinalizeReceipt!.BaseVerdictGenerationId = t.Lineage[0].VerdictGenerationId; Fixture.RefreshReceipt(t.Run); }),
    ("receipt_current_rehashed", t => { t.Run.CurrentRecheckFinalizeReceipt!.SuccessorVerdictGenerationSha256 = Fixture.Sha("wrong"); Fixture.RefreshReceipt(t.Run); }),
    ("active_marker", t => t.Run.Recheck = new()),
    ("pending_generation", t => t.Run.PendingGenerationId = Fixture.Sha("pending")),
    ("current_hash", t => t.Run.CurrentGenerationHash = Fixture.Sha("wrong")),
    ("creation_p9_run_drift", t => t.Run.P9RunId = Fixture.Id('e')),
    ("creation_p9_result_drift", t => t.Run.P9ResultId = Fixture.Id('e')),
    ("creation_p9_generation_drift", t => t.Run.P9GenerationId = Fixture.Sha("wrong")),
    ("creation_p8_drift", t => t.Run.P8ConfigHash = Fixture.Sha("wrong")),
    ("current_capture_missing", t => t.Run.CurrentGenerationRecheckCaptureBinding = null),
    ("current_capture_hash_tamper", t => t.Run.CurrentGenerationRecheckCaptureBinding!.BindingSha256 = Fixture.Sha("wrong")),
    ("current_p9_splice_rehashed", t => { t.Run.CurrentGenerationRecheckCaptureBinding!.P9GenerationId = Fixture.Sha("wrong"); StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(t.Run.CurrentGenerationRecheckCaptureBinding); }),
    ("current_p8_splice_rehashed", t => { t.Run.CurrentGenerationRecheckCaptureBinding!.P8ConfigHash = Fixture.Sha("wrong"); StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(t.Run.CurrentGenerationRecheckCaptureBinding); }),
};
foreach (var (name, mutate) in storageMutants) await Check(name, async () => {
    var t = await Case.Create(); mutate(t);
    try { Resolve(t); throw new Exception("MUTANT_ACCEPTED"); } catch (Exception error) when (error is InvalidOperationException or StatisticReconciliationFinalVerdictException) { }
});
await Check("real_mongo_audit_filter_separates_three_verdicts_and_fifteen_audit_records", async () => {
    var t = await Case.Create();
    var filter = (FilterDefinition<StatisticReconciliationIndependentReviewAuditRecord>)Call("P11IndependentAuditFilter", t.Run.Id)!;
    var bson = filter.Render(new RenderArgs<StatisticReconciliationIndependentReviewAuditRecord>(
        BsonSerializer.SerializerRegistry.GetSerializer<StatisticReconciliationIndependentReviewAuditRecord>(), BsonSerializer.SerializerRegistry));
    Require(bson.ElementCount == 2 && bson["reconciliationId"].AsString == t.Run.Id, "EXACT_RECONCILIATION_FILTER");
    var kinds = bson["recordKind"].AsBsonDocument;
    Require(kinds.ElementCount == 1 && kinds["$in"].AsBsonArray.Select(x => x.AsString).SequenceEqual(new[] { "REVIEW_DECISION", "REVIEW_SUPERSESSION" }), "EXACT_TWO_AUDIT_KINDS");
    var documents = t.Lineage.Select(x => x.ToBsonDocument()).Concat(Enumerable.Range(0, 15).Select(i =>
        new BsonDocument { ["reconciliationId"] = t.Run.Id, ["recordKind"] = i < 10 ? "REVIEW_DECISION" : "REVIEW_SUPERSESSION" })).ToList();
    documents.Add(new BsonDocument { ["reconciliationId"] = Fixture.Id('e'), ["recordKind"] = "REVIEW_DECISION" });
    documents.Add(new BsonDocument { ["reconciliationId"] = t.Run.Id, ["recordKind"] = "UNKNOWN" });
    // Exercise the actual driver's rendered equality/$in query against the mixed collection.
    var selected = documents.Where(d => d["reconciliationId"] == bson["reconciliationId"] && kinds["$in"].AsBsonArray.Contains(d["recordKind"])).ToArray();
    Require(selected.Length == 15 && selected.Count(d => d["recordKind"] == "REVIEW_SUPERSESSION") == 5, "AUDIT_FIFTEEN_NOT_EIGHTEEN");
    t.TotalReviewRecordCount = documents.Count(d => d["reconciliationId"] == bson["reconciliationId"]);
    Require(t.TotalReviewRecordCount == 19, "UNKNOWN_OWNED_ROW_CENSUS");
    try { Resolve(t); throw new Exception("EXTRA_UNKNOWN_KIND_HIDDEN"); }
    catch (InvalidOperationException error) when (error.Message == "P11_ATTEMPT040_STALE_ORACLE_CANONICAL_CARDINALITY_INVALID") { }
});
Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = "P11_ATTEMPT040_STALE_ORACLE_SELFTEST_V1", verdict = failures == 0 ? "PASS" : "FAIL", total = rows.Count, failures, rows,
    canonicalProducerConsumer = true, mongoFilterRendered = true, liveDatabaseUsed = false, browserUsed = false, protectedReads = 0 }));
return failures == 0 ? 0 : 1;

sealed class Case
{
    public required JsonNode Browser; public required StatisticReconciliationRun Run; public required List<StatisticReconciliationReview> Lineage;
    public long TotalReviewRecordCount = 18;
    public static async Task<Case> Create()
    {
        Fixture.Context = await LifecycleFixture.NonFlowCurrentAsync("P11_ATTEMPT040_ORACLE", 2);
        var f = await Fixture.Create();
        var current = await f.Publisher.PublishSupersedingRecheckAsync(Fixture.RunId, f.Stale.VerdictGenerationId,
            Fixture.Request(f.FrozenP8, f.FrozenP8, "successor_actual"), null, Fixture.At.AddSeconds(3));
        var lineage = (await f.Backend.ReadLineageAsync(Fixture.RunId)).ToList(); var initial = lineage[0];
        var marker = StatisticReconciliationRecheckCanonical.NewMarker(Fixture.RunId, Fixture.Id('2'),
            new("successor-recheck", 10, Fixture.Sha("state10")), "STALE", f.Stale.ActualGenerationId,
            f.Stale.ActualGenerationSha256, f.Stale.VerdictGenerationId, f.Stale.VerdictGenerationSha256,
            f.Successor, Fixture.At.AddSeconds(2));
        marker.SuccessorGenerationId = current.ActualGenerationId; marker.SuccessorGenerationHash = current.ActualGenerationSha256;
        f.Run.CurrentRecheckFinalizeReceipt = StatisticReconciliationRunService.BuildRecheckFinalizeReceipt(marker,
            current.VerdictGenerationId, current.VerdictGenerationSha256, "MATCHED", 13, Fixture.At.AddSeconds(3), currentP8Configuration: f.FrozenP8);
        f.Run.Status = "MATCHED"; f.Run.CurrentGenerationId = current.ActualGenerationId; f.Run.CurrentGenerationHash = current.ActualGenerationSha256;
        f.Run.CurrentGenerationRecheckCaptureBinding = f.Successor;
        f.Run.P9ResultId = f.Run.P9RunId; f.Run.P8ConfigHash = f.CapturedP8.ConfigHash; f.Run.P9ResultKind = "DIRECT";
        var browser = JsonSerializer.SerializeToNode(new { ids = new { reconciliationId = Fixture.RunId,
            reconciliationInitialActualGenerationId = initial.ActualGenerationId, reconciliationInitialGenerationId = initial.VerdictGenerationId,
            reconciliationStaleActualGenerationId = f.Stale.ActualGenerationId, reconciliationActualGenerationId = current.ActualGenerationId,
            reconciliationGenerationId = current.VerdictGenerationId, reconciliationRecheckMarkerId = marker.MarkerId,
            reconciliationInitialP9RunId = f.Run.P9RunId, reconciliationInitialP9GenerationId = f.Run.P9GenerationId,
            reconciliationInitialP8ConfigHash = f.Run.P8ConfigHash, reportId = f.Run.SourceReportId,
            p9RunId = f.Successor.P9RunId, statResultId = f.Successor.P9GenerationId, statConfigHash = f.Successor.P8ConfigHash },
            observations = new { reconciliationStale = new { schemaVersion = "P11_ATTEMPT040_STALE_REVIEW_RECOVERY_V1", verdictGenerationId = (string?)null,
                verdictIdentitySource = "TRUSTED_MONGO_ORACLE_BY_ACTUAL_GENERATION", initialReviewSupersessionCount = (int?)null,
                supersessionEvidenceSource = "TRUSTED_MONGO_ORACLE", detail = new { status = "STALE", currentGenerationId = f.Stale.ActualGenerationId,
                    currentGenerationHash = f.Stale.ActualGenerationSha256, stateRevision = 9, stateHash = Fixture.Sha("state9") },
                summary = new { status = "STALE", stateRevision = 9, stateHash = Fixture.Sha("state9"), hasCurrentGeneration = true, hasPendingGeneration = false },
                reviewRecovery = new { schemaVersion = "P11_REVIEW_TARGET_NOT_SIGNABLE_RECOVERY_V1", mode = "REVIEW_TARGET_NOT_SIGNABLE",
                    summary = new { httpStatus = 200, status = "STALE", stateRevision = 9, stateHash = Fixture.Sha("state9"), hasCurrentGeneration = true, hasPendingGeneration = false, baseEligible = true, inProgress = false },
                    review = new { httpStatus = 409, problemStatus = 409, code = "P10_REVIEW_TARGET_NOT_SIGNABLE", problemTitle = "Independent review state conflict." },
                    dom = new { articleCount = 1, phase = "REVIEW_RECOVERY", httpStatus = "409", reason = "REVIEW_TARGET_NOT_SIGNABLE", reconciliationId = Fixture.RunId,
                        stateRevision = "9", stateHash = Fixture.Sha("state9"), ctaCount = 1, ctaTestIdCount = 1, ctaVisible = true, ctaEnabled = true,
                        reviewActionsCount = 0, reviewGateCount = 0, domainIdentitiesCount = 0, presentationMetadataCount = 0 },
                    network = new { summaryRequestCount = 1, summaryResponseCount = 1, reviewRequestCount = 1, reviewResponseCount = 1,
                        detailRequestCount = 0, detailResponseCount = 0, recheckRequestCount = 0, recheckResponseCount = 0, unexpectedNegativeCount = 0,
                        requestStartSequence = 1, requestEndSequence = 2, responseStartSequence = 1, responseEndSequence = 2 }
                } } } })!;
        return new Case { Browser = browser, Run = f.Run, Lineage = lineage };
    }
}
