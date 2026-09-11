using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

// Canonical verdict producer fixture adapted from the existing P11 freshness tests.
sealed class Fixture
{
    public static readonly DateTime At = new(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
    public static LifecycleFixtureResult Context = null!;
    public static string RunId => Context.Input.ReconciliationId;
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
        var owner = Context.ActualOwner!;
        run.WorkId = owner.WorkId; run.ScopeAssignmentId = owner.WorkAssignmentId;
        run.SourceReportId = owner.ReportId; run.SourcePayloadRevision = owner.PayloadRevision; run.SourcePayloadHash = owner.PayloadSha256;
        run.SourceLifecycleRevision = owner.LifecycleRevision; run.SourceLifecycleHash = owner.LifecycleSha256;
        run.SourceLifecycleStatus = owner.LifecycleStatus; run.SourceLifecycleEventKey = capture.SourceLifecycleEventKey;
        run.DynamicFormVersionId = owner.DynamicFormTemplateId; run.DynamicFormSchemaHash = capture.DynamicFormSchemaHash;
        run.PeriodKey = owner.PeriodInstanceKey; run.PeriodInstanceKey = owner.PeriodInstanceKey;
        run.PeriodKind = capture.PeriodKind; run.PeriodStartUtc = capture.PeriodStartUtc; run.PeriodEndUtc = capture.PeriodEndUtc; run.TimeAxis = capture.TimeAxis;
        run.P9RunId = Context.Input.P9ContributionAudit.Scope.RunId; run.P9GenerationId = Context.Input.P9ContributionAudit.Scope.GenerationId;
        run.P9GenerationHash = Context.Input.P9ContributionAudit.Scope.GenerationSha256;
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
        StatisticReconciliationTrustedP8ConfigurationIdentityCanonical.Create(Context.Input.Source.DynamicFormTemplateId, Id('4'), revision == 3 ? Id('5') : Id('b'), checked((int)revision), revision, hash);

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
        var owner = Context.ActualOwner!;
        binding.SourceReportId = owner.ReportId;
        binding.SourcePayloadRevision = owner.PayloadRevision; binding.SourcePayloadHash = owner.PayloadSha256;
        binding.SourceLifecycleRevision = owner.LifecycleRevision; binding.SourceLifecycleHash = owner.LifecycleSha256;
        binding.SourceLifecycleStatus = owner.LifecycleStatus; binding.SourceLifecycleEventKey = Context.Input.P9ContributionAudit.TriggerLifecycleEventKey;
        binding.PeriodKey = owner.PeriodInstanceKey; binding.PeriodInstanceKey = owner.PeriodInstanceKey;
        binding.ActualCapturePlan.Export.WorkId = owner.WorkId;
        binding.ActualCapturePlan.Export.ScopeId = owner.WorkAssignmentId;
        binding.ActualCapturePlan.ActualConfigurationBundleSha256 = StatisticReconciliationActualCapturePlanIntegrity.V4ConfigurationBundleSha(binding.ActualCapturePlan);
        binding.ActualCapturePlan.PlanSha256 = StatisticReconciliationActualCapturePlanIntegrity.PlanSha(binding.ActualCapturePlan);
        binding.ActualCapturePlanSha256 = binding.ActualCapturePlan.PlanSha256;
        binding.ActualConfigurationBundleSha256 = binding.ActualCapturePlan.ActualConfigurationBundleSha256;
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
