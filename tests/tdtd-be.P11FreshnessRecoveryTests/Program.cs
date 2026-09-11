using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

var results = new List<object>();
var failures = 0;
await Check("canonical_producer_stale_then_recovery", async () =>
{
    var fixture = await Fixture.Create();
    Require(fixture.Stale.Verdict == "FAILED" && fixture.Stale.RootCauseClass == "FRESHNESS", "CANONICAL_STALE_MAPPING");
    Require(!fixture.Stale.CompleteEvidence && !fixture.Stale.AllRequiredLayersZero && !fixture.Stale.UnknownBlocksCloseout, "FRESHNESS_PRECEDES_LAYERS");
    Require(StatisticReconciliationRunService.IsFreshnessRecoveryRecheckBase(fixture.Run, fixture.Stale), "VALID_BASE");
    Require(StatisticReconciliationRunService.IsFreshnessRecoverySuccessor(fixture.Run, fixture.Successor), "VALID_SUCCESSOR");
});
await Check("same_semantic_config_new_revision_is_drift", async () =>
{
    var fixture = await Fixture.Create(sameConfigHash: true);
    Require(fixture.CapturedP8.VersionId != fixture.FrozenP8.VersionId && fixture.FrozenP8.VersionNo == fixture.CapturedP8.VersionNo + 1 && fixture.FrozenP8.Revision == fixture.CapturedP8.Revision + 1, "CANONICAL_PATCH_VERSION_ADVANCEMENT");
    Require(fixture.CapturedP8.ConfigHash == fixture.FrozenP8.ConfigHash && fixture.CapturedP8.BundleSha256 != fixture.FrozenP8.BundleSha256, "REVISION_BOUND_BUNDLE");
    Require(StatisticReconciliationRunService.IsFreshnessRecoveryRecheckBase(fixture.Run, fixture.Stale), "REVISION_ONLY_DRIFT_ACCEPTED");
    Require(StatisticReconciliationRunService.IsFreshnessRecoverySuccessor(fixture.Run, fixture.Successor), "REVISION_ONLY_SUCCESSOR_ACCEPTED");
});

var badRuns = new (string Name, Action<StatisticReconciliationRun> Mutate)[]
{
    ("initial_stale_without_receipt", run => { run.CurrentRecheckFinalizeReceipt = null; run.CurrentGenerationRecheckCaptureBinding = null; }),
    ("missing_receipt", run => run.CurrentRecheckFinalizeReceipt = null),
    ("missing_current_binding", run => run.CurrentGenerationRecheckCaptureBinding = null),
    ("missing_frozen_p8", run => run.CurrentRecheckFinalizeReceipt!.CurrentP8Configuration = null),
    ("old_receipt_v1", run => { run.CurrentRecheckFinalizeReceipt!.SchemaVersion = StatisticReconciliationRecheckFinalizeReceipt.CurrentSchemaVersion; run.CurrentRecheckFinalizeReceipt.CurrentP8Configuration = null; Fixture.RefreshReceipt(run); }),
    ("wrong_status_failed", run => run.Status = "FAILED"),
    ("active_marker", run => run.Recheck = new()),
    ("pending_id", run => run.PendingGenerationId = Fixture.Sha("pending")),
    ("pending_hash", run => run.PendingGenerationHash = Fixture.Sha("pending")),
    ("pending_timestamp", run => run.PendingGenerationPublishedAtUtc = Fixture.At),
    ("lease_owner", run => run.LeaseOwnerId = "worker"),
    ("claim_token", run => run.ClaimToken = "token"),
    ("lease_until", run => run.LeaseUntilUtc = Fixture.At),
    ("heartbeat", run => run.LastHeartbeatAtUtc = Fixture.At),
    ("wrong_reconciliation", run => run.Id = Fixture.Id('f')),
    ("current_actual_id_mismatch", run => run.CurrentGenerationId = Fixture.Sha("other_actual")),
    ("current_actual_hash_mismatch", run => run.CurrentGenerationHash = Fixture.Sha("other_actual_hash")),
    ("receipt_hash_tamper", run => run.CurrentRecheckFinalizeReceipt!.ReceiptSha256 = Fixture.Sha("tampered")),
    ("receipt_verdict_id_rehashed", run => { run.CurrentRecheckFinalizeReceipt!.SuccessorVerdictGenerationId = Fixture.Sha("other_verdict"); Fixture.RefreshReceipt(run); }),
    ("receipt_verdict_hash_rehashed", run => { run.CurrentRecheckFinalizeReceipt!.SuccessorVerdictGenerationSha256 = Fixture.Sha("other_verdict_hash"); Fixture.RefreshReceipt(run); }),
    ("receipt_base_verdict_id_rehashed", run => { run.CurrentRecheckFinalizeReceipt!.BaseVerdictGenerationId = Fixture.Sha("other_base"); Fixture.RefreshReceipt(run); }),
    ("receipt_base_verdict_hash_rehashed", run => { run.CurrentRecheckFinalizeReceipt!.BaseVerdictGenerationSha256 = Fixture.Sha("other_base_hash"); Fixture.RefreshReceipt(run); }),
    ("capture_binding_tamper", run => run.CurrentGenerationRecheckCaptureBinding!.P8ConfigBundleHash = Fixture.Sha("tampered_bundle")),
    ("frozen_p8_tamper", run => run.CurrentRecheckFinalizeReceipt!.CurrentP8Configuration!.Revision++),
    ("no_config_drift", run => Fixture.ReplaceFrozenP8(run, 3, Fixture.Sha("config3"), version: Fixture.Id('5'), versionNo: 3)),
    ("same_revision_changed_hash", run => Fixture.ReplaceFrozenP8(run, 3, Fixture.Sha("other_config"))),
    ("older_revision", run => Fixture.ReplaceFrozenP8(run, 2, Fixture.Sha("older_config"))),
    ("different_owner", run => Fixture.ReplaceFrozenP8(run, 4, Fixture.Sha("config4"), owner: Fixture.Id('e'))),
    ("different_config_id", run => Fixture.ReplaceFrozenP8(run, 4, Fixture.Sha("config4"), config: Fixture.Id('e'))),
    ("same_version_id_cannot_advance", run => Fixture.ReplaceFrozenP8(run, 4, Fixture.Sha("config4"), version: Fixture.Id('5'))),
    ("different_version_no", run => Fixture.ReplaceFrozenP8(run, 4, Fixture.Sha("config4"), versionNo: 2)),
    ("same_version_higher_revision", run => Fixture.ReplaceFrozenP8(run, 4, Fixture.Sha("config4"), version: Fixture.Id('5'), versionNo: 3)),
    ("skipped_version", run => Fixture.ReplaceFrozenP8(run, 4, Fixture.Sha("config4"), versionNo: 5)),
    ("skipped_revision", run => Fixture.ReplaceFrozenP8(run, 5, Fixture.Sha("config4")))
};
foreach (var (name, mutate) in badRuns)
    await Check(name, async () =>
    {
        var fixture = await Fixture.Create();
        mutate(fixture.Run);
        Require(!StatisticReconciliationRunService.IsFreshnessRecoveryRecheckBase(fixture.Run, fixture.Stale), "INVALID_BASE_REJECTED");
    });

var badVerdicts = new (string Name, Action<StatisticReconciliationReview> Mutate)[]
{
    ("verdict_signable", verdict => verdict.Signable = true),
    ("verdict_closeout", verdict => verdict.CloseoutAllowed = true),
    ("verdict_complete", verdict => verdict.CompleteEvidence = true),
    ("verdict_allzero", verdict => verdict.AllRequiredLayersZero = true),
    ("verdict_identity_remediation", verdict => verdict.MissingOrExtraIdentity = true),
    ("verdict_unknown_blocking", verdict => verdict.UnknownBlocksCloseout = true),
    ("verdict_wrong_root", verdict => verdict.RootCauseClass = "UNKNOWN"),
    ("verdict_missing_freshness", verdict => verdict.FreshnessAssessmentSha256 = null),
    ("verdict_missing_classification", verdict => verdict.RootCauseClassificationSha256 = null),
    ("verdict_internal_failure", verdict => verdict.FailureKind = "INTERNAL"),
    ("verdict_failure_evidence", verdict => verdict.FailureEvidenceSha256 = Fixture.Sha("error")),
    ("verdict_hash_tamper", verdict => verdict.DocumentSemanticSha256 = Fixture.Sha("tampered"))
};
foreach (var (name, mutate) in badVerdicts)
    await Check(name, async () =>
    {
        var fixture = await Fixture.Create();
        mutate(fixture.Stale);
        Require(!StatisticReconciliationRunService.IsFreshnessRecoveryRecheckBase(fixture.Run, fixture.Stale), "INVALID_VERDICT_REJECTED");
    });

var badSuccessors = new (string Name, Action<StatisticReconciliationRecheckCaptureBinding> Mutate)[]
{
    ("successor_same_p9_run", successor => { successor.P9RunId = Fixture.Id('6'); successor.P9ResultId = Fixture.Id('6'); }),
    ("successor_remediation_evidence", successor => successor.RemediationEvidenceSha256 = Fixture.Sha("remediation")),
    ("successor_missing_config_revision", successor => successor.P8ConfigRevision = null),
    ("successor_same_p9_id", successor => successor.P9GenerationId = Fixture.Sha("captured_p9_id")),
    ("successor_same_p9_hash", successor => successor.P9GenerationHash = Fixture.Sha("captured_p9_hash")),
    ("successor_wrong_owner", successor => successor.P8ConfigOwnerId = Fixture.Id('e')),
    ("successor_wrong_config", successor => successor.P8ConfigId = Fixture.Id('e')),
    ("successor_wrong_version", successor => successor.P8ConfigVersionId = Fixture.Id('e')),
    ("successor_wrong_version_no", successor => successor.P8ConfigVersionNo++),
    ("successor_wrong_revision", successor => successor.P8ConfigRevision++),
    ("successor_wrong_config_hash", successor => successor.P8ConfigHash = Fixture.Sha("wrong_config")),
    ("successor_wrong_bundle", successor => successor.P8ConfigBundleHash = Fixture.Sha("wrong_bundle")),
    ("successor_invalid_source", successor => successor.SourcePayloadHash = "invalid"),
    ("successor_invalid_lifecycle", successor => successor.SourceLifecycleRevision = 0),
    ("successor_invalid_plan", successor => successor.ActualCapturePlanSha256 = Fixture.Sha("wrong_plan"))
};
foreach (var (name, mutate) in badSuccessors)
    await Check(name, async () =>
    {
        var fixture = await Fixture.Create();
        mutate(fixture.Successor);
        StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(fixture.Successor);
        Require(!StatisticReconciliationRunService.IsFreshnessRecoverySuccessor(fixture.Run, fixture.Successor), "INVALID_SUCCESSOR_REJECTED");
    });

await Check("publisher_preserves_stale_and_exact_replay", async () =>
{
    var fixture = await Fixture.Create();
    var oldDocument = JsonSerializer.Serialize(fixture.Stale);
    var matchedRequest = Fixture.Request(fixture.FrozenP8, fixture.FrozenP8, "recovered_actual");
    var successor = await fixture.Publisher.PublishSupersedingRecheckAsync(Fixture.RunId,
        fixture.Stale.VerdictGenerationId, matchedRequest, null, Fixture.At.AddSeconds(3));
    var replay = await fixture.Publisher.PublishSupersedingRecheckAsync(Fixture.RunId,
        fixture.Stale.VerdictGenerationId, matchedRequest, null, Fixture.At.AddSeconds(3));
    Require(successor.Verdict == "MATCHED" && successor.Signable && successor.CloseoutAllowed, "ACTUAL_MATCHED_PUBLISHER");
    Require(successor.SupersedesVerdictGenerationId == fixture.Stale.VerdictGenerationId && successor.ActualGenerationId != fixture.Stale.ActualGenerationId, "APPENDED_EXACT_LINEAGE");
    Require(replay.DocumentSemanticSha256 == successor.DocumentSemanticSha256, "EXACT_REPLAY");
    Require(JsonSerializer.Serialize(fixture.Stale) == oldDocument, "OLD_STALE_IMMUTABLE");
    Require((await fixture.Backend.ReadLineageAsync(Fixture.RunId)).Count == 3, "NO_DUPLICATE_VERDICT");
});

await Check("presentation_tracks_eligibility_and_active_marker", async () =>
{
    var fixture = await Fixture.Create();
    var ready = StatisticReconciliationRunService.BuildPresentationRecheck(fixture.Run, fixture.Stale);
    Require(ready.BaseEligible && !ready.InProgress, "ELIGIBLE_PRESENTATION");
    Require(!StatisticReconciliationRunService.BuildPresentationRecheck(fixture.Run, null).BaseEligible, "NO_VERDICT_NO_ACTION");
    fixture.Run.StateHash = "malformed";
    Require(!StatisticReconciliationRunService.BuildPresentationRecheck(fixture.Run, fixture.Stale).BaseEligible, "INVALID_CAS_NO_ACTION");
    fixture.Run.StateHash = Fixture.Sha("state10");
    fixture.Run.Recheck = new();
    var active = StatisticReconciliationRunService.BuildPresentationRecheck(fixture.Run, fixture.Stale);
    Require(!active.BaseEligible && active.InProgress, "ACTIVE_PRESENTATION");
    fixture.Run.Recheck = null;
    fixture.Run.CurrentRecheckFinalizeReceipt = null;
    Require(!StatisticReconciliationRunService.BuildPresentationRecheck(fixture.Run, fixture.Stale).BaseEligible, "UNPROVEN_STALE_NO_ACTION");
});

var report = JsonSerializer.Serialize(new
{
    schemaVersion = "P11_FRESHNESS_RECOVERY_CONTRACT_REGRESSION_V1",
    status = failures == 0 ? "PASS" : "FAIL",
    total = results.Count,
    failures,
    actualVerdictProducerConsumer = true,
    actualAdmissionPredicates = true,
    actualBackendSchedulerExecuted = false,
    officialCaseCredit = 0,
    liveLaunchAuthorityCount = 0,
    results
}, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(report);
if (args.Length == 2 && args[0] == "--output")
{
    var output = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, report + Environment.NewLine);
}
Environment.ExitCode = failures == 0 ? 0 : 1;

async Task Check(string name, Func<Task> body)
{
    try { await body(); results.Add(new { name, status = "PASS" }); }
    catch (Exception error) { failures++; results.Add(new { name, status = "FAIL", error = error.Message }); }
}
static void Require(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }

sealed class Fixture
{
    public static readonly DateTime At = new(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
    public static readonly string RunId = Id('1');
    public required StatisticReconciliationRun Run { get; init; }
    public required StatisticReconciliationReview Stale { get; init; }
    public required StatisticReconciliationRecheckCaptureBinding Successor { get; init; }
    public required StatisticReconciliationTrustedP8ConfigurationIdentity CapturedP8 { get; init; }
    public required StatisticReconciliationTrustedP8ConfigurationIdentity FrozenP8 { get; init; }
    public required StatisticReconciliationFinalVerdictPublisher Publisher { get; init; }
    public required MemoryReviewBackend Backend { get; init; }

    public static async Task<Fixture> Create(bool sameConfigHash = false)
    {
        var captured = P8(3, Sha("config3"));
        var frozen = P8(4, Sha(sameConfigHash ? "config3" : "config4"));
        var backend = new MemoryReviewBackend();
        var publisher = new StatisticReconciliationFinalVerdictPublisher(backend);
        var original = await publisher.PublishAsync(Request(captured, captured, "original_actual"), At);
        var stale = await publisher.PublishSupersedingRecheckAsync(RunId, original.VerdictGenerationId,
            Request(captured, frozen, "stale_actual"), null, At.AddSeconds(1));
        StatisticReconciliationFinalVerdictPublisher.ValidateStored(stale);
        var capture = Binding(captured, "captured_p9");
        var marker = StatisticReconciliationRecheckCanonical.NewMarker(RunId, Id('2'),
            new("initial-recheck", 5, Sha("state")), "MATCHED", original.ActualGenerationId,
            original.ActualGenerationSha256, original.VerdictGenerationId, original.VerdictGenerationSha256,
            capture, At);
        marker.SuccessorGenerationId = stale.ActualGenerationId;
        marker.SuccessorGenerationHash = stale.ActualGenerationSha256;
        var receipt = StatisticReconciliationRunService.BuildRecheckFinalizeReceipt(marker,
            stale.VerdictGenerationId, stale.VerdictGenerationSha256, "STALE", 9, At.AddSeconds(1),
            currentP8Configuration: frozen);
        var run = new StatisticReconciliationRun
        {
            Id = RunId, Status = "STALE", StateRevision = 10, StateHash = Sha("state10"),
            CurrentGenerationId = stale.ActualGenerationId, CurrentGenerationHash = stale.ActualGenerationSha256,
            CurrentGenerationRecheckCaptureBinding = capture, CurrentRecheckFinalizeReceipt = receipt
        };
        return new Fixture { Run = run, Stale = stale, Successor = Binding(frozen, "successor_p9"),
            CapturedP8 = captured, FrozenP8 = frozen, Backend = backend, Publisher = publisher };
    }

    public static StatisticReconciliationFinalVerdictRequest Request(
        StatisticReconciliationTrustedP8ConfigurationIdentity captured,
        StatisticReconciliationTrustedP8ConfigurationIdentity current, string actual)
    {
        var pins = new StatisticReconciliationActualFreshnessPins(
            new(Sha("source"), captured.BundleSha256, Sha("actual_config"), Sha("catalog"), Sha("runtime")),
            Sha("result"), Sha(actual + "_hash"), Sha("export"), captured.BundleSha256);
        var freshness = new StatisticReconciliationFreshnessEvaluator().Evaluate(new(pins.Binding, pins,
            pins with { Binding = pins.Binding with { P8ConfigurationBundleSha256 = current.BundleSha256 }, ConfigurationPinSetSha256 = current.BundleSha256 }, true, true, true));
        var classifier = new StatisticReconciliationRootCauseClassifier();
        var permission = classifier.CreatePermissionEvidence("AUTHORIZED", Sha("authorization"));
        var layers = freshness.State == "FRESH" ? Enumerable.Range(0, 8).Select(ordinal => classifier.CreateLayerEvidence(
            ordinal, StatisticReconciliationRootCauseLayers.Ordered[ordinal], freshness.ExpectedBindingSha256,
            true, Sha("expected"), Sha("actual"), Sha("delta"), 1, 0, 0, 0, 0,
            "ZERO_DELTA", "NOT_REQUIRED", null, null, [])).ToImmutableArray() : [];
        return new(RunId, freshness.ExpectedBindingSha256, Sha("expected_id"), Sha("expected_hash"),
            Sha(actual + "_id"), Sha(actual + "_hash"), Sha("delta_manifest"), permission,
            new(freshness.ExpectedBindingSha256, permission, freshness, layers), "NONE", null);
    }

    public static void ReplaceFrozenP8(StatisticReconciliationRun run, long revision, string hash,
        string? owner = null, string? config = null, string? version = null, int versionNo = 4)
    {
        run.CurrentRecheckFinalizeReceipt!.CurrentP8Configuration = StatisticReconciliationTrustedP8ConfigurationIdentityCanonical.Create(
            owner ?? Id('3'), config ?? Id('4'), version ?? Id('b'), versionNo, revision, hash);
        RefreshReceipt(run);
    }
    public static void RefreshReceipt(StatisticReconciliationRun run) => run.CurrentRecheckFinalizeReceipt!.ReceiptSha256 =
        StatisticReconciliationRunService.RecheckFinalizeReceiptHash(run.CurrentRecheckFinalizeReceipt);
    private static StatisticReconciliationTrustedP8ConfigurationIdentity P8(long revision, string hash) =>
        StatisticReconciliationTrustedP8ConfigurationIdentityCanonical.Create(Id('3'), Id('4'), revision == 3 ? Id('5') : Id('b'), checked((int)revision), revision, hash);

    private static StatisticReconciliationRecheckCaptureBinding Binding(
        StatisticReconciliationTrustedP8ConfigurationIdentity p8, string seed)
    {
        var p9RunId = seed == "captured_p9" ? Id('6') : Id('a');
        var plan = new StatisticReconciliationActualCapturePlan
        {
            SchemaVersion = StatisticReconciliationActualCapturePlanVersions.V4,
            BoundaryRegistryVersion = StatisticReconciliationActualCapturePlanIntegrity.BoundaryRegistryVersion,
            P8ConfigurationOwnerId = p8.OwnerId, P8ConfigurationBundleSha256 = p8.BundleSha256,
            Basic = new() { Disposition = "NOT_APPLICABLE", SnapshotId = "", Mode = "", ImmutableSelectorSha256 = null!,
                ApplicabilityProofSha256 = StatisticReconciliationActualCapturePlanIntegrity.ApplicabilityProofSha("BASIC", "NOT_APPLICABLE", p8.OwnerId, p8.BundleSha256, null) },
            Advanced = new() { Disposition = "NOT_APPLICABLE", SectionId = "", DayNodeIds = [], MonthNodeIds = [], YearNodeIds = [], ImmutableSelectorSha256 = null!,
                ApplicabilityProofSha256 = StatisticReconciliationActualCapturePlanIntegrity.ApplicabilityProofSha("ADVANCED", "NOT_APPLICABLE", p8.OwnerId, p8.BundleSha256, null) },
            Diff = new() { Disposition = "NOT_APPLICABLE", ResultId = "", RunId = "", ImmutableSelectorSha256 = null!,
                ApplicabilityProofSha256 = StatisticReconciliationActualCapturePlanIntegrity.ApplicabilityProofSha("DIFF", "NOT_APPLICABLE", p8.OwnerId, p8.BundleSha256, null) },
            Api = new() { Surface = "DIRECT_FIELD", OwnerResultId = p9RunId, ExpectedTotalRows = 1, PageSize = 200, PageCount = 1 },
            Export = new() { ExportId = Sha(seed + "export"), ResultKind = "DIRECT_FIELD", WorkId = Id('7'), ScopeType = "ASSIGNMENT", ScopeId = Id('8'),
                ResultId = p9RunId, FilterSha256 = Sha("filter"), RequestSha256 = Sha("request"), AuthorizationSnapshotSha256 = Sha("authorization"),
                ContentSha256 = Sha("content"), ColumnManifestSha256 = Sha("columns"), OwnerSemanticSha256 = Sha("owner") }
        };
        plan.ActualConfigurationBundleSha256 = StatisticReconciliationActualCapturePlanIntegrity.V4ConfigurationBundleSha(plan);
        plan.PlanSha256 = StatisticReconciliationActualCapturePlanIntegrity.PlanSha(plan);
        var binding = new StatisticReconciliationRecheckCaptureBinding
        {
            P9RunId = p9RunId, P9ResultId = p9RunId, P9GenerationId = Sha(seed + "_id"), P9GenerationHash = Sha(seed + "_hash"),
            P9RunKind = "LIFECYCLE_DIRECT_PROJECTION", P9CapabilityId = "P9", P9RouteId = "route", P9CandidateChainId = "chain", P9CandidatePromptId = "prompt",
            SourceReportId = Id('9'), SourcePayloadRevision = 1, SourcePayloadHash = Sha("source"), SourceLifecycleRevision = 1,
            SourceLifecycleEventKey = Sha("event"), SourceLifecycleHash = Sha("lifecycle"), SourceLifecycleStatus = "APPROVED",
            DynamicFormVersionId = p8.OwnerId, DynamicFormSchemaHash = Sha("schema"), P8ConfigOwnerId = p8.OwnerId, P8ConfigId = p8.ConfigId,
            P8ConfigVersionId = p8.VersionId, P8ConfigVersionNo = p8.VersionNo, P8ConfigRevision = p8.Revision, P8ConfigHash = p8.ConfigHash, P8ConfigBundleHash = p8.BundleSha256,
            P9CatalogVersion = "v1", P9CatalogRawSha256 = Sha("catalograw"), P9CatalogSemanticSha256 = Sha("catalogsemantic"), P9SchemaRawSha256 = Sha("schemaraw"),
            P9SchemaSemanticSha256 = Sha("schemasemantic"), P9StageLockSha256 = Sha("stage"), PeriodKey = "ALL", PeriodInstanceKey = "ALL", PeriodKind = "ALL", TimeAxis = "UTC_GREGORIAN",
            ActualCapturePlan = plan, ActualCapturePlanSha256 = plan.PlanSha256, ActualConfigurationBundleSha256 = plan.ActualConfigurationBundleSha256
        };
        StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(binding);
        StatisticReconciliationRecheckCaptureBindingCanonical.RequireValid(binding);
        return binding;
    }
    public static string Id(char seed) => new(seed, 24);
    public static string Sha(string seed) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
}

sealed class MemoryReviewBackend : IStatisticReconciliationReviewBackend
{
    private readonly Dictionary<string, StatisticReconciliationReview> values = new(StringComparer.Ordinal);
    public Task<StatisticReconciliationReview?> ReadAsync(string reconciliationId, string verdictGenerationId, CancellationToken ct = default)
        => Task.FromResult(values.TryGetValue(verdictGenerationId, out var value) && value.ReconciliationId == reconciliationId ? value : null);
    public Task<IReadOnlyList<StatisticReconciliationReview>> ReadLineageAsync(string reconciliationId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StatisticReconciliationReview>>(values.Values.Where(value => value.ReconciliationId == reconciliationId)
            .OrderBy(value => value.CreatedAtUtc).ThenBy(value => value.VerdictGenerationId, StringComparer.Ordinal).ToArray());
    public Task<bool> TryAppendAsync(StatisticReconciliationReview review, CancellationToken ct = default)
    {
        if (values.ContainsKey(review.VerdictGenerationId) || review.SupersedesVerdictGenerationId is not null &&
            values.Values.Any(value => value.ReconciliationId == review.ReconciliationId && value.SupersedesVerdictGenerationId == review.SupersedesVerdictGenerationId))
            return Task.FromResult(false);
        values.Add(review.VerdictGenerationId, review);
        return Task.FromResult(true);
    }
}
