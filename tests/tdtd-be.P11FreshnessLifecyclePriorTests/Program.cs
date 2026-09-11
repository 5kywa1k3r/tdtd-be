using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

var rows = new List<object>();
var failed = 0;
await Check("actual_stale_prior_rejected_and_exact_ancestor_recovers", async () =>
{
    var test = await Setup.Create();
    var before = test.Fixture.Run.ToBson();
    ExpectThrow(() => LifecycleFixture.Build(test.SuccessorInput with
    {
        Prior = StatisticReconciliationActualLifecyclePriorOwner.CreatePrior(
            test.Fixture.Stale.ActualGenerationId, test.Fixture.Stale.ActualGenerationSha256,
            test.Fixture.Stale, test.StaleEvidence)
    }));
    var prior = test.Resolve();
    Require(prior.CommittedGenerationId == test.Ancestor.ActualGenerationId, "EXACT_ANCESTOR_ACTUAL");
    Require(prior.FinalVerdictGenerationId == test.Ancestor.VerdictGenerationId, "EXACT_ANCESTOR_VERDICT");
    var successor = LifecycleFixture.Build(test.SuccessorInput with { Prior = prior });
    StatisticReconciliationActualLifecycleEvidenceIntegrity.Validate(successor);
    Require(successor.Result.Outcome == "MATCHED" && successor.Result.EvidenceComplete, "ACTUAL_BRIDGE_MATCHED");
    Require(successor.Manifest.PriorCommittedGenerationId == test.Ancestor.ActualGenerationId, "MANIFEST_ANCESTOR_BOUND");
    Require(before.SequenceEqual(test.Fixture.Run.ToBson()), "PERSISTED_BASE_AND_MARKER_UNCHANGED");
    Require(test.Fixture.Run.Recheck!.BaseVerdictGenerationId == test.Fixture.Stale.VerdictGenerationId, "IMMEDIATE_STALE_SUPERSESSION_PRESERVED");
    var replay = LifecycleFixture.Build(test.SuccessorInput with { Prior = test.Resolve() });
    Require(replay.ManifestSha256 == successor.ManifestSha256, "EXACT_REPLAY");
});
await Check("terminal_admission_and_active_capture_share_exact_resolution", async () =>
{
    var test = await Setup.Create();
    var capture = test.Resolve();
    var terminal = test.Terminal();
    var before = terminal.ToBson();
    var admission = StatisticReconciliationActualLifecyclePriorOwner.CompleteFreshnessRecoveryPrior(
        terminal, test.Fixture.Stale, test.StaleEvidence, test.Lineage,
        test.AncestorEvidence, test.Fixture.Successor);
    Require(JsonSerializer.Serialize(capture) == JsonSerializer.Serialize(admission), "ADMISSION_CAPTURE_SAME_PRIOR");
    Require(before.SequenceEqual(terminal.ToBson()), "ADMISSION_ZERO_STATE_MUTATION");
});

var badRuns = new (string Name, Action<StatisticReconciliationRun> Change)[]
{
    ("not_running", x => x.Status = "QUEUED"),
    ("missing_marker", x => x.Recheck = null),
    ("missing_lease", x => x.LeaseOwnerId = null),
    ("missing_claim", x => x.ClaimToken = null),
    ("pending_generation", x => x.PendingGenerationId = Fixture.Sha("pending")),
    ("current_actual_drift", x => x.CurrentGenerationHash = Fixture.Sha("wrong")),
    ("receipt_hash_drift", x => x.CurrentRecheckFinalizeReceipt!.ReceiptSha256 = Fixture.Sha("wrong")),
    ("creation_p9_run_changed", x => x.P9RunId = Fixture.Id('f')),
    ("creation_p9_generation_changed", x => x.P9GenerationId = Fixture.Sha("wrong")),
    ("creation_p9_hash_changed", x => x.P9GenerationHash = Fixture.Sha("wrong")),
    ("source_report_changed", x => x.SourceReportId = Fixture.Id('f')),
    ("source_payload_revision_changed", x => x.SourcePayloadRevision++),
    ("source_payload_hash_changed", x => x.SourcePayloadHash = Fixture.Sha("wrong")),
    ("source_lifecycle_revision_changed", x => x.SourceLifecycleRevision++),
    ("source_lifecycle_hash_changed", x => x.SourceLifecycleHash = Fixture.Sha("wrong")),
    ("source_lifecycle_event_changed", x => x.SourceLifecycleEventKey = Fixture.Sha("wrong")),
    ("source_lifecycle_status_changed", x => x.SourceLifecycleStatus = "RECALLED"),
    ("work_changed", x => x.WorkId = Fixture.Id('f')),
    ("assignment_changed", x => x.ScopeAssignmentId = Fixture.Id('f')),
    ("form_changed", x => x.DynamicFormVersionId = Fixture.Id('f')),
    ("period_changed", x => x.PeriodInstanceKey = "other"),
    ("marker_base_verdict_changed", x => { x.Recheck!.BaseVerdictGenerationId = Fixture.Sha("wrong"); StatisticReconciliationRecheckCanonical.RefreshMarkerHash(x.Recheck); }),
    ("marker_phase_changed", x => { x.Recheck!.Phase = StatisticReconciliationRecheckPhases.ReadyToClaim; StatisticReconciliationRecheckCanonical.RefreshMarkerHash(x.Recheck); }),
    ("successor_source_changed", x => { x.Recheck!.CaptureBinding.SourcePayloadHash = Fixture.Sha("wrong"); StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(x.Recheck.CaptureBinding); StatisticReconciliationRecheckCanonical.RefreshMarkerHash(x.Recheck); }),
    ("successor_same_p9", x => { x.Recheck!.CaptureBinding.P9GenerationId = x.CurrentGenerationRecheckCaptureBinding!.P9GenerationId; StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(x.Recheck.CaptureBinding); StatisticReconciliationRecheckCanonical.RefreshMarkerHash(x.Recheck); })
};
foreach (var bad in badRuns) await Check(bad.Name, async () =>
{
    var test = await Setup.Create(); bad.Change(test.Fixture.Run); ExpectThrow(() => test.Resolve());
});
await Check("ancestor_missing", async () => { var t = await Setup.Create(); t.Lineage.Remove(t.Ancestor); ExpectThrow(() => t.Resolve()); });
await Check("ancestor_ambiguous", async () => { var t = await Setup.Create(); t.Lineage.Add(t.Ancestor); ExpectThrow(() => t.Resolve()); });
await Check("current_verdict_ambiguous", async () => { var t = await Setup.Create(); t.Lineage.Add(t.Fixture.Stale); ExpectThrow(() => t.Resolve()); });
await Check("extra_canonical_root_is_not_ignored", async () =>
{
    var t = await Setup.Create();
    var root = await new StatisticReconciliationFinalVerdictPublisher(new MemoryReviewBackend()).PublishAsync(
        Fixture.Request(t.Fixture.CapturedP8, t.Fixture.CapturedP8, "extra_root"), Fixture.At);
    t.Lineage.Add(root); ExpectThrow(() => t.Resolve());
});
await Check("rehashed_receipt_wrong_actual_ancestor_rejected", async () =>
{
    var t = await Setup.Create(); t.Fixture.Run.CurrentRecheckFinalizeReceipt!.BaseActualGenerationId = Fixture.Sha("wrong_ancestor");
    Fixture.RefreshReceipt(t.Fixture.Run); ExpectThrow(() => t.Resolve());
});
await Check("canonical_lifecycle_manifest_wrong_actual_ancestor_rejected", async () =>
{
    var t = await Setup.Create();
    var prior = StatisticReconciliationActualLifecyclePriorOwner.CreatePrior(Fixture.Sha("wrong_ancestor"),
        t.Ancestor.ActualGenerationSha256, t.Ancestor, t.AncestorEvidence);
    t.StaleEvidence = LifecycleFixture.Build(await LifecycleFixture.FreshnessInputAsync("P11_FRESHNESS_LIFECYCLE_PRIOR", "STALE", true, prior));
    ExpectThrow(() => t.Resolve());
});
await Check("canonical_fork_from_ancestor_rejected", async () =>
{
    var t = await Setup.Create();
    var forkStore = new MemoryReviewBackend();
    await forkStore.TryAppendAsync(t.Ancestor);
    var fork = await new StatisticReconciliationFinalVerdictPublisher(forkStore).PublishSupersedingRecheckAsync(
        t.Fixture.Run.Id, t.Ancestor.VerdictGenerationId,
        Fixture.Request(t.Fixture.CapturedP8, t.Fixture.FrozenP8, "fork_actual"), null, Fixture.At.AddSeconds(2));
    t.Lineage.Add(fork);
    ExpectThrow(() => t.Resolve());
});
await Check("canonical_deeper_ancestor_is_not_scanned_or_skipped", async () =>
{
    var t = await Setup.Create();
    var matched = await t.Fixture.Publisher.PublishSupersedingRecheckAsync(t.Fixture.Run.Id,
        t.Fixture.Stale.VerdictGenerationId, Fixture.Request(t.Fixture.CapturedP8, t.Fixture.CapturedP8, "deeper_matched"),
        null, Fixture.At.AddSeconds(3));
    var stale = await t.Fixture.Publisher.PublishSupersedingRecheckAsync(t.Fixture.Run.Id,
        matched.VerdictGenerationId, Fixture.Request(t.Fixture.CapturedP8, t.Fixture.FrozenP8, "deeper_stale"),
        null, Fixture.At.AddSeconds(4));
    var capture = t.Fixture.Run.CurrentGenerationRecheckCaptureBinding!;
    var completed = StatisticReconciliationRecheckCanonical.NewMarker(t.Fixture.Run.Id, Fixture.Id('2'),
        new("deeper-prior", 12, Fixture.Sha("deeper-state")), "MATCHED", matched.ActualGenerationId,
        matched.ActualGenerationSha256, matched.VerdictGenerationId, matched.VerdictGenerationSha256, capture, Fixture.At.AddSeconds(3));
    completed.SuccessorGenerationId = stale.ActualGenerationId; completed.SuccessorGenerationHash = stale.ActualGenerationSha256;
    var run = t.Terminal(); run.CurrentGenerationId = stale.ActualGenerationId; run.CurrentGenerationHash = stale.ActualGenerationSha256;
    run.CurrentRecheckFinalizeReceipt = StatisticReconciliationRunService.BuildRecheckFinalizeReceipt(completed,
        stale.VerdictGenerationId, stale.VerdictGenerationSha256, "STALE", 16, Fixture.At.AddSeconds(4),
        currentP8Configuration: t.Fixture.FrozenP8);
    var prior = StatisticReconciliationActualLifecyclePriorOwner.CreatePrior(matched.ActualGenerationId,
        matched.ActualGenerationSha256, matched, t.AncestorEvidence);
    var evidence = LifecycleFixture.Build(await LifecycleFixture.FreshnessInputAsync("P11_FRESHNESS_LIFECYCLE_PRIOR", "DEEPER", true, prior));
    var lineage = await t.Fixture.Backend.ReadLineageAsync(run.Id);
    ExpectThrow(() => StatisticReconciliationActualLifecyclePriorOwner.CompleteFreshnessRecoveryPrior(
        run, stale, evidence, lineage, t.AncestorEvidence, t.Fixture.Successor));
});
await Check("ancestor_hash_tampered", async () => { var t = await Setup.Create(); t.Ancestor.VerdictGenerationSha256 = Fixture.Sha("wrong"); ExpectThrow(() => t.Resolve()); });
await Check("ancestor_lifecycle_incomplete", async () => { var t = await Setup.Create(); ExpectThrow(() => t.Resolve(t.StaleEvidence)); });
await Check("ancestor_lifecycle_unrelated", async () => { var t = await Setup.Create(); var other = await LifecycleFixture.NonFlowCurrentAsync("UNRELATED", 2); ExpectThrow(() => t.Resolve(LifecycleFixture.Build(other.Input))); });
await Check("current_lifecycle_manifest_tampered", async () => { var t = await Setup.Create(); t.StaleEvidence = t.StaleEvidence with { Manifest = t.StaleEvidence.Manifest with { PriorCommittedGenerationId = Fixture.Sha("wrong") } }; ExpectThrow(() => t.Resolve()); });
await Check("current_lifecycle_matched_is_not_freshness_bypass", async () => { var t = await Setup.Create(); t.StaleEvidence = t.AncestorEvidence; ExpectThrow(() => t.Resolve()); });
await Check("terminal_admission_source_drift_rejected", async () => { var t = await Setup.Create(); var run = t.Terminal(); run.SourceLifecycleHash = Fixture.Sha("wrong"); ExpectThrow(() => StatisticReconciliationActualLifecyclePriorOwner.CompleteFreshnessRecoveryPrior(run, t.Fixture.Stale, t.StaleEvidence, t.Lineage, t.AncestorEvidence, t.Fixture.Successor)); });
await Check("strict_bridge_reversal_evidence_not_relaxed", async () =>
{
    var reversal = await LifecycleFixture.ReversalAsync("STRICT_REVERSAL", "RECALLED", "WITHDRAW");
    Require(LifecycleFixture.Build(reversal.Input).Result.Outcome == "MATCHED", "VALID_REVERSAL_MATCHED");
    var changed = LifecycleFixture.MutateReversal(reversal.Input.P9ContributionAudit,
        x => x with { PriorRunId = Fixture.Id('f') });
    var result = LifecycleFixture.Build(reversal.Input with { P9ContributionAudit = changed });
    Require(result.Result.Outcome != "MATCHED" && !result.Result.EvidenceComplete, "WRONG_REVERSAL_STILL_REJECTED");
});

await Check("initial_creation_binding_ignores_no_immutable_fields_and_does_not_mutate_run", async () =>
{
    var test = await Setup.Create(); var run = test.Fixture.Run;
    BindingGuardFixture.PopulateCreationContext(run); var before = run.ToBson();
    var committed = BindingGuardFixture.Context(run);
    Require(StatisticReconciliationActualRunBindingGuard.MatchesInitialCreation(run, committed), "ORIGINAL_CONTEXT_MATCHES");
    Require(!StatisticReconciliationActualRunBindingGuard.Matches(run, committed), "ORDINARY_CURRENT_PATH_NOT_RELAXED");
    var effective = StatisticReconciliationRecheckCaptureBindingCanonical.EffectiveCurrentRun(run);
    var current = BindingGuardFixture.Context(effective) with { RecheckCaptureBindingSha256 = run.CurrentGenerationRecheckCaptureBinding!.BindingSha256 };
    Require(StatisticReconciliationActualRunBindingGuard.Matches(run, current), "CURRENT_CONTEXT_MATCHES_EFFECTIVE_CURRENT");
    Require(!StatisticReconciliationActualRunBindingGuard.MatchesInitialCreation(run, current), "RECHECK_NOT_INITIAL");
    Require(!StatisticReconciliationActualRunBindingGuard.Matches(run, current with { ImmutableHeaderSha256 = Fixture.Sha("drift") }), "CURRENT_CONTEXT_SPLICE_REJECTED");
    Require(before.SequenceEqual(run.ToBson()), "GUARD_ZERO_MUTATION");
});
var badContexts = new (string Name, Func<StatisticReconciliationActualPublicationContext, StatisticReconciliationActualPublicationContext> Change)[]
{
    ("identity", x => x with { ImmutableIdentitySha256 = Fixture.Sha("wrong") }),
    ("header", x => x with { ImmutableHeaderSha256 = Fixture.Sha("wrong") }),
    ("filter", x => x with { FilterSha256 = Fixture.Sha("wrong") }),
    ("concept", x => x with { ConceptKey = "wrong" }),
    ("grain", x => x with { Grain = "wrong" }),
    ("p8_owner", x => x with { P8ConfigurationOwnerId = "wrong" }),
    ("p8_config", x => x with { P8ConfigurationBundleSha256 = Fixture.Sha("wrong") }),
    ("actual_config", x => x with { ActualConfigurationBundleSha256 = Fixture.Sha("wrong") }),
    ("flow_template", x => x with { FlowTemplateVersionId = "wrong" }),
    ("flow_payload", x => x with { FlowPayloadSha256 = Fixture.Sha("wrong") }),
    ("flow_instance", x => x with { FlowInstanceId = "wrong" }),
    ("flow_epoch", x => x with { ExecutionEpoch = 999 }),
    ("p9_catalog", x => x with { CatalogPins = x.CatalogPins with { P9CatalogRawSha256 = Fixture.Sha("wrong") } }),
    ("candidate_catalog", x => x with { CatalogPins = x.CatalogPins with { CandidateCatalogSemanticSha256 = Fixture.Sha("wrong") } }),
    ("pin_set", x => x with { CatalogPins = x.CatalogPins with { CatalogPinSetSha256 = Fixture.Sha("wrong") } }),
    ("recheck_marker", x => x with { RecheckCaptureBindingSha256 = Fixture.Sha("wrong") })
};
foreach (var bad in badContexts) await Check("initial_creation_binding_rejects_" + bad.Name, async () =>
{
    var test = await Setup.Create(); var run = test.Fixture.Run;
    BindingGuardFixture.PopulateCreationContext(run);
    Require(!StatisticReconciliationActualRunBindingGuard.MatchesInitialCreation(run,
        bad.Change(BindingGuardFixture.Context(run))), "INITIAL_CONTEXT_SPLICE_REJECTED");
});
foreach (var admission in new[] { false, true }) await Check(
    "actual_owner_branch_rejects_matched_lifecycle_on_stale_" + (admission ? "admission" : "capture"), async () =>
{
    var test = await Setup.Create(); var run = admission ? test.Terminal() : test.Fixture.Run;
    BindingGuardFixture.PopulateCreationContext(run);
    var effective = StatisticReconciliationRecheckCaptureBindingCanonical.EffectiveCurrentRun(run);
    var currentContext = BindingGuardFixture.Context(effective) with
        { RecheckCaptureBindingSha256 = run.CurrentGenerationRecheckCaptureBinding!.BindingSha256 };
    var actual = new StatisticReconciliationActualAppendResult(run.Id, run.CurrentGenerationId!,
        run.CurrentGenerationHash!, Fixture.Sha("publication_manifest"), 1, 0, [], [], currentContext,
        Fixture.Sha("source"), Fixture.Sha("runtime"), Fixture.Sha("result"), Fixture.Sha("export"), false, false,
        LifecycleManifestSha256: test.AncestorEvidence.ManifestSha256,
        LifecycleObservationCount: test.AncestorEvidence.Manifest.ObservationCount);
    var storage = new ReadOnlyLedgerFixture();
    var backend = new NoReadActualBackend();
    var owner = StatisticReconciliationActualLifecyclePriorOwner.ForReadOnlyAdmission(backend, test.Fixture.Backend,
        StatisticReconciliationActualLifecycleMongoLedger.ForReadOnlyAdmission(storage.Context));
    try
    {
        _ = await owner.ResolveValidatedCurrentAsync(run, actual, test.AncestorEvidence, test.Lineage,
            admission ? test.Fixture.Successor : null);
    }
    catch (InvalidOperationException error) when (error.Message == "P10_LIFECYCLE_FRESHNESS_RECOVERY_ACTIVE_BASE_BINDING")
    {
        Require(storage.Operations.Count == 0, "INCONSISTENT_LIFECYCLE_REJECTED_BEFORE_ANCESTOR_READ");
        return;
    }
    throw new InvalidOperationException("EXPECTED_ACTUAL_OWNER_BRANCH_REJECTION");
});
await Check("read_only_ledger_invalid_and_append_reject_without_storage", async () =>
{
    var test = await Setup.Create(); var storage = new ReadOnlyLedgerFixture();
    var reader = StatisticReconciliationActualLifecycleMongoLedger.ForReadOnlyAdmission(storage.Context);
    ExpectThrow(() => reader.AppendAsync(test.AncestorEvidence).GetAwaiter().GetResult());
    ExpectThrow(() => reader.ReadAndValidateByManifestAsync("", Fixture.Sha("manifest"), 1).GetAwaiter().GetResult());
    ExpectThrow(() => reader.ReadAndValidateAsync("run", "generation", Fixture.Sha("actual"), Fixture.Sha("manifest"), 0).GetAwaiter().GetResult());
    Require(storage.Operations.Count == 0, "READ_ONLY_INVALID_ZERO_STORAGE");
});
var result = new { schemaVersion = "P11_FRESHNESS_LIFECYCLE_PRIOR_REGRESSION_V1", status = failed == 0 ? "PASS" : "FAIL",
    total = rows.Count, failures = failed, rows, actualCanonicalVerdictProducer = true, actualLifecycleBridgeProducerConsumer = true,
    actualSharedOwnerResolutionConsumer = true, databaseAndPublisherStorageReadExecuted = false,
    actualBackendSchedulerExecuted = false, officialCaseCredit = 0, liveLaunchAuthorityCount = 0 };
Console.WriteLine(JsonSerializer.Serialize(result));
return failed == 0 ? 0 : 1;

async Task Check(string id, Func<Task> body)
{
    try { await body(); rows.Add(new { id, status = "PASS" }); }
    catch (Exception error) { failed++; rows.Add(new { id, status = "FAIL", errorType = error.GetType().Name, detail = error.Message }); }
}
static void Require(bool valid, string detail) { if (!valid) throw new InvalidOperationException(detail); }
static void ExpectThrow(Action action)
{
    try { action(); }
    catch (Exception error) when (error is InvalidOperationException or StatisticReconciliationRecheckException or StatisticReconciliationFinalVerdictException or StatisticReconciliationLifecycleException) { return; }
    throw new InvalidOperationException("EXPECTED_REJECTION");
}

sealed class Setup
{
    public Fixture Fixture = null!;
    public StatisticReconciliationReview Ancestor = null!;
    public List<StatisticReconciliationReview> Lineage = [];
    public StatisticReconciliationActualLifecycleEvidence AncestorEvidence = null!;
    public StatisticReconciliationActualLifecycleEvidence StaleEvidence = null!;
    public StatisticReconciliationActualLifecycleBuildInput SuccessorInput = null!;
    public static async Task<Setup> Create()
    {
        const string id = "P11_FRESHNESS_LIFECYCLE_PRIOR";
        Fixture.Context = await LifecycleFixture.NonFlowCurrentAsync(id, 2);
        var fixture = await Fixture.Create();
        var lineage = (await fixture.Backend.ReadLineageAsync(Fixture.RunId)).ToList();
        var ancestor = lineage.Single(x => x.SupersedesVerdictGenerationId is null);
        var ancestorEvidence = LifecycleFixture.Build(Fixture.Context.Input);
        if (ancestorEvidence.Result.Outcome != "MATCHED" || !ancestorEvidence.Result.EvidenceComplete)
            throw new InvalidOperationException("REAL_ANCESTOR_NOT_MATCHED");
        var prior = StatisticReconciliationActualLifecyclePriorOwner.CreatePrior(
            ancestor.ActualGenerationId, ancestor.ActualGenerationSha256, ancestor, ancestorEvidence);
        var staleInput = await LifecycleFixture.FreshnessInputAsync(id, "STALE", true, prior);
        var staleEvidence = LifecycleFixture.Build(staleInput);
        if (staleEvidence.Result.Outcome != "STALE" || staleEvidence.Result.RootCause != "FRESHNESS" || staleEvidence.Result.EvidenceComplete)
            throw new InvalidOperationException("REAL_CONFIG_DRIFT_NOT_STALE_FRESHNESS");
        var successorInput = await LifecycleFixture.FreshnessInputAsync(id, "SUCCESSOR", false, prior);
        var marker = StatisticReconciliationRecheckCanonical.NewMarker(fixture.Run.Id, Fixture.Id('2'),
            new("freshness-recovery", fixture.Run.StateRevision, fixture.Run.StateHash), "STALE",
            fixture.Run.CurrentGenerationId!, fixture.Run.CurrentGenerationHash!, fixture.Stale.VerdictGenerationId,
            fixture.Stale.VerdictGenerationSha256, fixture.Successor, Fixture.At.AddSeconds(2));
        marker.Phase = StatisticReconciliationRecheckPhases.CaptureRunning;
        StatisticReconciliationRecheckCanonical.RefreshMarkerHash(marker);
        fixture.Run.Status = "RUNNING"; fixture.Run.Recheck = marker;
        fixture.Run.LeaseOwnerId = "worker"; fixture.Run.ClaimToken = "claim";
        fixture.Run.LeaseUntilUtc = Fixture.At.AddMinutes(5);
        return new() { Fixture = fixture, Ancestor = ancestor, Lineage = lineage,
            AncestorEvidence = ancestorEvidence, StaleEvidence = staleEvidence, SuccessorInput = successorInput };
    }
    public StatisticReconciliationActualLifecyclePriorGeneration Resolve(StatisticReconciliationActualLifecycleEvidence? ancestor = null) =>
        StatisticReconciliationActualLifecyclePriorOwner.CompleteFreshnessRecoveryPrior(
            Fixture.Run, Fixture.Stale, StaleEvidence, Lineage, ancestor ?? AncestorEvidence);
    public StatisticReconciliationRun Terminal()
    {
        var result = BsonSerializer.Deserialize<StatisticReconciliationRun>(Fixture.Run.ToBson());
        result.Status = "STALE"; result.Recheck = null; result.LeaseOwnerId = null; result.ClaimToken = null;
        result.LeaseUntilUtc = null; result.LastHeartbeatAtUtc = null;
        return result;
    }
}
