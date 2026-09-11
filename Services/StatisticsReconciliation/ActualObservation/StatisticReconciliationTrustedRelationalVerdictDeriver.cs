using System.Collections.Immutable;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static partial class StatisticReconciliationTrustedVerdictDeriver
{
    private const string ExplicitMetricPrefix = "OWNER_EXPLICIT:";
    private static readonly ImmutableHashSet<string> IsomorphicAtomKinds =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "REPORT_COUNT", "ROW_COUNT", "COUNT", "NUMERIC_VALUE_COUNT",
            "SUM", "MIN", "MAX", "MEAN", "BUCKET", "DATE",
            "FULL_DATE", "PERIOD", "BOOLEAN", "ENUM", "TEXT");

    internal static StatisticReconciliationFinalVerdictRequest Derive(
        StatisticReconciliationRun run,
        StatisticReconciliationObservation expectedCommit,
        ImmutableArray<StatisticReconciliationObservationAtom> expectedAtoms,
        StatisticReconciliationActualAppendResult actual)
        => DeriveCore(run, expectedCommit, expectedAtoms, actual,
            currentOwners: null, currentEvidenceComplete: false,
            captureGuardsCurrent: false, currentFenceSha256: "~");

    internal static StatisticReconciliationFinalVerdictRequest Derive(
        StatisticReconciliationRun run,
        StatisticReconciliationObservation expectedCommit,
        ImmutableArray<StatisticReconciliationObservationAtom> expectedAtoms,
        StatisticReconciliationActualAppendResult actual,
        StatisticReconciliationActualFreshnessPins currentOwners,
        string currentFenceSha256,
        bool currentEvidenceComplete,
        bool currentCaptureCoherent,
        StatisticReconciliationTrustedLifecycleProof lifecycleProof)
        => DeriveCore(run, expectedCommit, expectedAtoms, actual,
            currentOwners ?? throw new ArgumentNullException(
                nameof(currentOwners)),
            currentEvidenceComplete, currentCaptureCoherent,
            RequiredSha(currentFenceSha256, "CURRENT_FENCE_SHA256_INVALID"),
            lifecycleProof ?? throw new ArgumentNullException(
                nameof(lifecycleProof)));

    private static StatisticReconciliationFinalVerdictRequest DeriveCore(
        StatisticReconciliationRun run,
        StatisticReconciliationObservation expectedCommit,
        ImmutableArray<StatisticReconciliationObservationAtom> expectedAtoms,
        StatisticReconciliationActualAppendResult actual,
        StatisticReconciliationActualFreshnessPins? currentOwners,
        bool currentEvidenceComplete,
        bool captureGuardsCurrent,
        string currentFenceSha256,
        StatisticReconciliationTrustedLifecycleProof? lifecycleProof = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(expectedCommit);
        ArgumentNullException.ThrowIfNull(actual);
        if (expectedAtoms.IsDefault || actual.TypedObservations.IsDefault ||
            actual.Layers.IsDefault)
            throw Fail("TYPED_EVIDENCE_UNINITIALIZED");

        var effectiveRun = StatisticReconciliationRecheckCaptureBindingCanonical
            .EffectiveCaptureRun(run);
        var plan = effectiveRun.ActualCapturePlan ??
                   throw Fail("ACTUAL_CAPTURE_PLAN_REQUIRED");
        try
        {
            StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
                plan, effectiveRun.ActualCapturePlanSha256);
        }
        catch (InvalidOperationException error)
        {
            throw new StatisticReconciliationTrustedVerdictPipelineException(
                "ACTUAL_CAPTURE_PLAN_INVALID", error);
        }
        var committedLayers = RequireCommittedLayers(actual);
        var relationalProof = RequireRelationalProof(
            actual,
            committedLayers);
        var summaryPlan = RequireSummaryPlanBinding(actual, relationalProof);

        var expectedRuntimeBinding = RunRuntimeBindingSha256(effectiveRun);
        var capturedRuntimeBinding = RunRuntimeBindingSha256(effectiveRun);
        var expectedMembership = RequiredSha(
            expectedCommit.Commit?.MembershipSemanticSha256,
            "EXPECTED_NEUTRAL_MEMBERSHIP_MISSING");
        var capturedMembership = RequiredSha(
            actual.CapturedMembershipSemanticSha256,
            "ACTUAL_NEUTRAL_MEMBERSHIP_MISSING");
        var expectedBinding = new StatisticReconciliationComparisonBindingPins(
            expectedCommit.SourceSetSha256,
            expectedCommit.P8ConfigurationBundleSha256,
            RequiredSha(effectiveRun.ActualConfigurationBundleSha256,
                "RUN_ACTUAL_CONFIGURATION_PIN_MISSING"),
            StatisticReconciliationTrustedCatalogComparison.FromExpected(
                expectedCommit.CatalogPins),
            expectedRuntimeBinding,
            expectedMembership);
        var capturedBinding = new StatisticReconciliationComparisonBindingPins(
            actual.CapturedSourceSetSha256,
            actual.CommittedRunBinding.P8ConfigurationBundleSha256,
            actual.CommittedRunBinding.ActualConfigurationBundleSha256,
            StatisticReconciliationTrustedCatalogComparison.FromActual(
                actual.CommittedRunBinding.CatalogPins),
            capturedRuntimeBinding,
            capturedMembership);
        var capturedPins = new StatisticReconciliationActualFreshnessPins(
            capturedBinding,
            actual.CapturedResultPinSetSha256,
            actual.GenerationSemanticSha256,
            actual.CapturedExportPinSetSha256,
            StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan)
                ? actual.CommittedRunBinding.P8ConfigurationBundleSha256
                : null);
        var freshness = new StatisticReconciliationFreshnessEvaluator().Evaluate(
            new StatisticReconciliationFreshnessRequest(
                expectedBinding,
                capturedPins,
                currentOwners ?? capturedPins,
                ActualGenerationComplete: true,
                RequiredLayersComplete: currentEvidenceComplete &&
                    lifecycleProof?.EvidenceComplete == true,
                CaptureCoherent: captureGuardsCurrent &&
                    lifecycleProof?.CaptureCoherent == true &&
                    lifecycleProof.Outcome !=
                        StatisticReconciliationLifecycleOutcomes.Stale &&
                    lifecycleProof.RootCause !=
                        StatisticReconciliationLifecycleRootCauses.Freshness));
        var binding = freshness.ExpectedBindingSha256;
        var permission = RootCause.CreatePermissionEvidence(
            StatisticReconciliationRootCausePermissionStates.Authorized,
            run.AuthorizationSnapshotHash);

        ImmutableArray<StatisticReconciliationRootCauseLayerEvidence> layers = [];
        if (freshness.State == StatisticReconciliationFreshnessStates.Fresh)
        {
            var output = ImmutableArray.CreateBuilder<
                StatisticReconciliationRootCauseLayerEvidence>(8);
            output.Add(SourceLayer(binding, expectedMembership,
                capturedMembership));
            var direct = DirectLayer(binding, expectedCommit, expectedAtoms,
                actual, committedLayers[1], plan, effectiveRun);
            output.Add(direct);
            output.Add(AggregateLayer(binding, expectedCommit, expectedAtoms,
                actual, committedLayers[2], committedLayers[1], plan,
                effectiveRun, direct));
            output.Add(SummaryLayer(3, "BASIC", binding, expectedCommit,
                expectedAtoms, actual, committedLayers[3],
                SummaryOwner(plan,
                    StatisticReconciliationActualCapturePlanIntegrity.BasicFamily,
                    plan.Basic.SnapshotId),
                plan.PlanSha256, summaryPlan, relationalProof.Facts));
            output.Add(SummaryLayer(4, "ADVANCED", binding, expectedCommit,
                expectedAtoms, actual, committedLayers[4],
                SummaryOwner(plan,
                    StatisticReconciliationActualCapturePlanIntegrity.AdvancedFamily,
                    $"{effectiveRun.ScopeAssignmentId}:{plan.Advanced.SectionId}"),
                plan.PlanSha256, summaryPlan, relationalProof.Facts));
            output.Add(SummaryLayer(5, "DIFF", binding, expectedCommit,
                expectedAtoms, actual, committedLayers[5],
                SummaryOwner(plan,
                    StatisticReconciliationActualCapturePlanIntegrity.DiffFamily,
                    plan.Diff.ResultId),
                plan.PlanSha256, summaryPlan, relationalProof.Facts));
            output.Add(ApiLayer(binding, expectedCommit, expectedAtoms, actual,
                committedLayers[6], plan, effectiveRun,
                output.ToImmutable(), relationalProof));
            output.Add(ExportLayer(binding, expectedCommit, expectedAtoms,
                actual, committedLayers[7], plan, effectiveRun, output[2],
                relationalProof));
            layers = ApplyLifecycleEvidence(
                output.MoveToImmutable(), binding, lifecycleProof);
        }

        var rootRequest = new StatisticReconciliationRootCauseRequest(
            binding, permission, freshness, layers);
        var lifecycleMetricScope = RequiredSha(
            actual.CapturedLifecycleMetricScopeSha256,
            "ACTUAL_LIFECYCLE_METRIC_SCOPE_MISSING");
        if (lifecycleMetricScope != RequiredSha(
                actual.CommittedRunBinding.LifecycleMetricScopeSha256,
                "COMMITTED_LIFECYCLE_METRIC_SCOPE_MISSING"))
            throw Fail("ACTUAL_LIFECYCLE_METRIC_SCOPE_BINDING_MISMATCH");
        var deltaManifest = H("P10_TRUSTED_RELATIONAL_DELTA_MANIFEST_V5",
            new[]
            {
                currentFenceSha256,
                lifecycleProof?.ProofSha256 ?? "~",
                lifecycleMetricScope
            }.Concat(
                layers.Select(value => value.DeltaManifestSha256)).ToArray());
        return new StatisticReconciliationFinalVerdictRequest(
            run.Id,
            binding,
            expectedCommit.GenerationId,
            expectedCommit.GenerationSemanticSha256,
            actual.GenerationId,
            actual.GenerationSemanticSha256,
            deltaManifest,
            permission,
            rootRequest,
            StatisticReconciliationFinalVerdictFailureKinds.None,
            null);
    }

    private static StatisticReconciliationRootCauseLayerEvidence DirectLayer(
        string binding,
        StatisticReconciliationObservation expectedCommit,
        ImmutableArray<StatisticReconciliationObservationAtom> expectedAtoms,
        StatisticReconciliationActualAppendResult actual,
        StatisticReconciliationActualCommittedLayer layer,
        StatisticReconciliationActualCapturePlan plan,
        StatisticReconciliationRun run)
    {
        var expected = expectedAtoms.Where(value => value.Family == "DIRECT")
            .ToArray();
        var actualAtoms = LayerAtoms(actual, layer.Layer);
        var relation = Relation("DIRECT", expectedCommit, expectedAtoms,
            actual, layer, plan.PlanSha256,
            run.P9RunId, run.P9GenerationId, run.P9GenerationHash);
        if (expected.Length == 0 || actualAtoms.Length == 0 ||
            !TryComparableExpected(expected, out var comparableExpected) ||
            actualAtoms.Any(IsSynthetic))
            return IncompleteLayer(1, binding, relation,
                "DIRECT_NON_ISOMORPHIC_OR_EMPTY");

        // Every explicit actual identity participates. Filtering to expected
        // keys would hide genuine ExtraIdentity deltas behind a full-set hash.
        var comparableActual = actualAtoms;
        if (comparableActual.Length == 0)
            return IncompleteLayer(1, binding, relation,
                "DIRECT_APPLICABLE_IDENTITIES_MISSING");
        return RelationalTypedLayer(1, binding, comparableExpected,
            comparableActual, expected, actualAtoms, layer, relation);
    }

    private static StatisticReconciliationRootCauseLayerEvidence AggregateLayer(
        string binding,
        StatisticReconciliationObservation expectedCommit,
        ImmutableArray<StatisticReconciliationObservationAtom> expectedAtoms,
        StatisticReconciliationActualAppendResult actual,
        StatisticReconciliationActualCommittedLayer layer,
        StatisticReconciliationActualCommittedLayer directLayer,
        StatisticReconciliationActualCapturePlan plan,
        StatisticReconciliationRun run,
        StatisticReconciliationRootCauseLayerEvidence directEvidence)
    {
        var expected = expectedAtoms.Where(value => value.Family == "DIRECT")
            .ToArray();
        var actualAtoms = LayerAtoms(actual, layer.Layer);
        var directOwner = $"{run.P9RunId}:{run.P9GenerationId}";
        var ownerRelation = directLayer.OwnerId == directOwner &&
            layer.OwnerId.Length > directOwner.Length + 1 &&
            layer.OwnerId.EndsWith($":{directOwner}", StringComparison.Ordinal);
        var relation = Relation("AGGREGATE", expectedCommit, expectedAtoms,
            actual, layer, plan.PlanSha256, directLayer.OwnerId,
            directLayer.OwnerVersionSha256, directEvidence.DeltaManifestSha256,
            run.P9GenerationHash);
        if (!ownerRelation || !directEvidence.EvidenceComplete ||
            directEvidence.NonzeroCount != 0 || actualAtoms.Length == 0 ||
            !TryComparableExpected(expected, out var comparableExpected))
            return IncompleteLayer(2, binding, relation,
                "AGGREGATE_RELATION_OR_EXPECTED_NOT_PROVEN");

        if (actualAtoms.Any(IsSynthetic))
            return IncompleteLayer(2, binding, relation,
                "AGGREGATE_OPAQUE_OR_SYNTHETIC_OUTPUT");
        // Include all explicit identities so extras are typed nonzero deltas.
        var comparableActual = actualAtoms;
        if (comparableActual.Length == 0)
            return IncompleteLayer(2, binding, relation,
                "AGGREGATE_ISOMORPHIC_IDENTITIES_MISSING");
        return RelationalTypedLayer(2, binding, comparableExpected,
            comparableActual, expected, actualAtoms, layer, relation);
    }

    private static string SummaryOwner(
        StatisticReconciliationActualCapturePlan plan,
        string family,
        string configuredOwner)
    {
        var disposition = family switch
        {
            StatisticReconciliationActualCapturePlanIntegrity.BasicFamily =>
                plan.Basic.Disposition,
            StatisticReconciliationActualCapturePlanIntegrity.AdvancedFamily =>
                plan.Advanced.Disposition,
            StatisticReconciliationActualCapturePlanIntegrity.DiffFamily =>
                plan.Diff.Disposition,
            _ => throw Fail("SUMMARY_FAMILY_INVALID")
        };
        return StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan) &&
               StatisticReconciliationActualCapturePlanIntegrity
                   .IsNotApplicable(disposition)
            ? StatisticReconciliationActualCapturePlanIntegrity
                .NotApplicableOwnerId(plan, family)
            : configuredOwner;
    }
    private static StatisticReconciliationRootCauseLayerEvidence SummaryLayer(
        int ordinal,
        string family,
        string binding,
        StatisticReconciliationObservation expectedCommit,
        ImmutableArray<StatisticReconciliationObservationAtom> expectedAtoms,
        StatisticReconciliationActualAppendResult actual,
        StatisticReconciliationActualCommittedLayer layer,
        string expectedOwner,
        string planSha256,
        StatisticReconciliationActualSummaryPlanBinding summaryPlan,
        StatisticReconciliationActualRelationalProofFacts facts)
    {
        var expected = expectedAtoms.Where(value => value.Family == family)
            .ToArray();
        var actualAtoms = LayerAtoms(actual, layer.Layer);
        var descriptorCount = summaryPlan.IdentityDescriptors.Count(value =>
            value.Family == family);
        var relation = Relation($"SUMMARY_{family}", expectedCommit,
            expectedAtoms, actual, layer, planSha256, expectedOwner,
            summaryPlan.SemanticSha256, facts.SemanticSha256,
            I(descriptorCount));
        if (expected.Length != 0 || descriptorCount != 0)
            return IncompleteLayer(ordinal, binding, relation,
                $"{family}_APPLICABLE_NON_ISOMORPHIC");
        // Expected compilation emits at least the seven count/state atoms for
        // every configured metric, even with zero source rows. Family absence
        // is therefore a locked plan non-applicability proof, not a claim that
        // the independently captured owner bytes equal an expected value.
        // Those out-of-scope bytes and their exact selector remain fully bound
        // by relation/LayerSha/ActualAtomSetSha.
        if (!FamilyNotApplicableProven(family, facts) ||
            layer.OwnerId != expectedOwner ||
            actualAtoms.Any(value => value.Family != family ||
                !IsSynthetic(value)))
            return IncompleteLayer(ordinal, binding, relation,
                $"{family}_NONAPPLICABLE_OWNER_OR_MANIFEST_INVALID");
        return ZeroRelationalLayer(ordinal, binding, relation,
            $"LOCKED_METRIC_FAMILY_NOT_APPLICABLE:{family}");
    }

    private static bool FamilyNotApplicableProven(
        string family,
        StatisticReconciliationActualRelationalProofFacts facts)
        => family switch
        {
            "BASIC" => facts.SummaryParityRelationCount == 0,
            "ADVANCED" =>
                facts.ExtendedAdvancedDescriptorProjectionCount == 0 &&
                facts.ExtendedAdvancedTypedAtomCount == 0 &&
                facts.ExtendedAdvancedDescriptorContributionCount == 0,
            "DIFF" => facts.ExtendedDiffTypedAtomCount == 0 &&
                      facts.ExtendedDiffSourcePairCount == 0,
            _ => false
        };

    private static StatisticReconciliationRootCauseLayerEvidence ApiLayer(
        string binding,
        StatisticReconciliationObservation expectedCommit,
        ImmutableArray<StatisticReconciliationObservationAtom> expectedAtoms,
        StatisticReconciliationActualAppendResult actual,
        StatisticReconciliationActualCommittedLayer layer,
        StatisticReconciliationActualCapturePlan plan,
        StatisticReconciliationRun run,
        ImmutableArray<StatisticReconciliationRootCauseLayerEvidence> prior,
        StatisticReconciliationActualRelationalProofBinding relationalProof)
    {
        var surface = plan.Api.Surface;
        var baseOrdinal = surface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField or
            StatisticReconciliationActualApiSurfaces.DirectTable or
            StatisticReconciliationActualApiSurfaces.DirectLabel => 2,
            StatisticReconciliationActualApiSurfaces.BasicSource => 3,
            StatisticReconciliationActualApiSurfaces.P9Diff => 5,
            _ => -1
        };
        var expectedOwner = surface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField or
            StatisticReconciliationActualApiSurfaces.DirectTable or
            StatisticReconciliationActualApiSurfaces.DirectLabel => run.P9RunId,
            StatisticReconciliationActualApiSurfaces.BasicSource =>
                plan.Basic.SnapshotId,
            StatisticReconciliationActualApiSurfaces.P9Diff => plan.Diff.ResultId,
            _ => string.Empty
        };
        var exactDirectFieldScope =
            StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan) &&
            surface == StatisticReconciliationActualApiSurfaces.DirectField;
        var expected = exactDirectFieldScope
            ? ProjectDirectFieldExpectedAtoms(run, expectedAtoms).ToArray()
            : expectedAtoms.Where(value => value.Family == "DIRECT").ToArray();
        var atoms = LayerAtoms(actual, layer.Layer);
        var comparableActual = atoms.Where(value => value.Family == "DIRECT" &&
                                                     !IsSynthetic(value))
            .ToArray();
        var opaqueAudit = atoms.Where(value => value.Family == "API" &&
                                               IsOpaque(value))
            .ToArray();
        var baseEvidence = baseOrdinal >= 0 && prior.Length > baseOrdinal
            ? prior[baseOrdinal]
            : null;
        var facts = relationalProof.Facts;
        var relation = Relation("API", expectedCommit, expectedAtoms, actual,
            layer, plan.PlanSha256, surface, expectedOwner,
            plan.Api.OwnerResultId ?? "~", I(plan.Api.ExpectedTotalRows),
            I(plan.Api.PageSize), I(plan.Api.PageCount),
            baseEvidence?.DeltaManifestSha256 ?? "~",
            run.FilterHash, run.AuthorizationSnapshotHash,
            actual.CapturedResultPinSetSha256,
            relationalProof.SemanticSha256,
            facts.CrossViewResolutionSha256,
            facts.CrossViewProofSha256,
            facts.CrossViewCompatibilityProofSha256,
            facts.CrossViewOriginalApiSemanticSha256);
        var ownerId = expectedOwner.Length == 0
            ? string.Empty
            : $"{StatisticReconciliationActualApiProtocol.RouteId(surface)}:{expectedOwner}";
        var provenance = facts.ApiCaptureSha256 == layer.CaptureSemanticSha256 &&
            facts.CrossViewOriginalApiSemanticSha256 ==
            layer.CaptureSemanticSha256;
        if (surface != StatisticReconciliationActualApiSurfaces.DirectField ||
            baseEvidence is null || !baseEvidence.EvidenceComplete ||
            baseEvidence.NonzeroCount != 0 ||
            baseEvidence.DeltaState !=
                StatisticReconciliationRootCauseLayerDeltaStates.Zero ||
            plan.Api.OwnerResultId != expectedOwner ||
            layer.OwnerId != ownerId || !provenance ||
            opaqueAudit.Length == 0 || comparableActual.Length == 0 ||
            atoms.Length != opaqueAudit.Length + comparableActual.Length ||
            !TryComparableExpected(expected, out var comparableExpected))
            return IncompleteLayer(6, binding, relation,
                "API_RELATIONAL_PROOF_INCOMPLETE");
        return RelationalTypedLayer(6, binding, comparableExpected,
            comparableActual, expected, atoms, layer, relation);
    }
    private static StatisticReconciliationRootCauseLayerEvidence ExportLayer(
        string binding,
        StatisticReconciliationObservation expectedCommit,
        ImmutableArray<StatisticReconciliationObservationAtom> expectedAtoms,
        StatisticReconciliationActualAppendResult actual,
        StatisticReconciliationActualCommittedLayer layer,
        StatisticReconciliationActualCapturePlan plan,
        StatisticReconciliationRun run,
        StatisticReconciliationRootCauseLayerEvidence aggregateEvidence,
        StatisticReconciliationActualRelationalProofBinding relationalProof)
    {
        var export = plan.Export;
        var exactDirectFieldScope =
            StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan) &&
            plan.Api.Surface ==
                StatisticReconciliationActualApiSurfaces.DirectField &&
            export.ResultKind == StatRunExportResultKinds.DirectField;
        var expected = exactDirectFieldScope
            ? ProjectDirectFieldExpectedAtoms(run, expectedAtoms).ToArray()
            : expectedAtoms.Where(value => value.Family == "DIRECT").ToArray();
        var atoms = LayerAtoms(actual, layer.Layer);
        var comparableActual = atoms.Where(value => value.Family == "DIRECT" &&
                                                     !IsSynthetic(value))
            .ToArray();
        var opaqueAudit = atoms.Where(value => value.Family == "EXPORT" &&
                                               IsOpaque(value))
            .ToArray();
        var facts = relationalProof.Facts;
        var relation = Relation("EXPORT", expectedCommit, expectedAtoms,
            actual, layer, plan.PlanSha256, export.ExportId,
            export.ResultKind, export.WorkId, export.ScopeType, export.ScopeId,
            export.ResultId, export.RequestSha256,
            export.AuthorizationSnapshotSha256, export.ContentSha256,
            export.ColumnManifestSha256, export.OwnerSemanticSha256,
            aggregateEvidence.DeltaManifestSha256,
            actual.CapturedResultPinSetSha256,
            actual.CapturedExportPinSetSha256,
            relationalProof.SemanticSha256,
            facts.CrossViewResolutionSha256,
            facts.CrossViewProofSha256,
            facts.CrossViewCompatibilityProofSha256,
            facts.CrossViewAuthorizationRelationSha256,
            facts.CrossViewOriginalExportSemanticSha256);
        var target = export.WorkId == run.WorkId &&
            export.ScopeType == "ASSIGNMENT" &&
            export.ScopeId == run.ScopeAssignmentId &&
            (export.ResultId == run.P9RunId ||
             export.ResultId == run.P9GenerationId);
        var provenance = facts.ExportCaptureSha256 == layer.CaptureSemanticSha256 &&
            facts.CrossViewOriginalExportSemanticSha256 ==
            layer.CaptureSemanticSha256;
        if (export.ResultKind != StatRunExportResultKinds.DirectField ||
            !target || !aggregateEvidence.EvidenceComplete ||
            aggregateEvidence.NonzeroCount != 0 ||
            aggregateEvidence.DeltaState !=
                StatisticReconciliationRootCauseLayerDeltaStates.Zero ||
            layer.OwnerId != export.ExportId || !provenance ||
            opaqueAudit.Length == 0 || comparableActual.Length == 0 ||
            atoms.Length != opaqueAudit.Length + comparableActual.Length ||
            !TryComparableExpected(expected, out var comparableExpected))
            return IncompleteLayer(7, binding, relation,
                "EXPORT_RELATIONAL_PROOF_INCOMPLETE");
        return RelationalTypedLayer(7, binding, comparableExpected,
            comparableActual, expected, atoms, layer, relation);
    }

    private static StatisticReconciliationActualRelationalProofBinding
        RequireRelationalProof(
            StatisticReconciliationActualAppendResult actual,
            ImmutableArray<StatisticReconciliationActualCommittedLayer> layers)
    {
        if (actual.RelationalProofBinding is null)
            throw Fail("ACTUAL_RELATIONAL_PROOF_BINDING_REQUIRED");
        StatisticReconciliationActualRelationalProofBinding normalized;
        try
        {
            normalized = StatisticReconciliationActualRelationalProofBinding
                .Normalize(actual.RelationalProofBinding);
        }
        catch (InvalidOperationException error)
        {
            throw new StatisticReconciliationTrustedVerdictPipelineException(
                "ACTUAL_RELATIONAL_PROOF_BINDING_INVALID",
                error);
        }
        var facts = normalized.Facts;
        if (layers.Length != 8 ||
            facts.SourceCaptureSha256 != layers[0].CaptureSemanticSha256 ||
            facts.DirectCaptureSha256 != layers[1].CaptureSemanticSha256 ||
            facts.AggregateCaptureSha256 != layers[2].CaptureSemanticSha256 ||
            facts.BasicCaptureSha256 != layers[3].CaptureSemanticSha256 ||
            facts.AdvancedCaptureSha256 != layers[4].CaptureSemanticSha256 ||
            facts.DiffCaptureSha256 != layers[5].CaptureSemanticSha256 ||
            facts.ApiCaptureSha256 != layers[6].CaptureSemanticSha256 ||
            facts.ExportCaptureSha256 != layers[7].CaptureSemanticSha256 ||
            facts.CrossViewOriginalApiSemanticSha256 !=
            layers[6].CaptureSemanticSha256 ||
            facts.CrossViewOriginalExportSemanticSha256 !=
            layers[7].CaptureSemanticSha256)
            throw Fail("ACTUAL_RELATIONAL_PROOF_CAPTURE_BINDING_MISMATCH");
        return normalized;
    }

    private static StatisticReconciliationActualSummaryPlanBinding
        RequireSummaryPlanBinding(
            StatisticReconciliationActualAppendResult actual,
            StatisticReconciliationActualRelationalProofBinding relationalProof)
    {
        var committed = actual.CommittedRunBinding.SummaryPlanBinding ??
            throw Fail("COMMITTED_SUMMARY_PLAN_BINDING_REQUIRED");
        if (actual.SummaryPlanBinding is null)
            throw Fail("ACTUAL_SUMMARY_PLAN_BINDING_REQUIRED");
        StatisticReconciliationActualSummaryPlanBinding normalized;
        StatisticReconciliationActualSummaryPlanBinding observed;
        try
        {
            normalized = StatisticReconciliationActualSummaryPlanBinding
                .Normalize(committed);
            observed = StatisticReconciliationActualSummaryPlanBinding
                .Normalize(actual.SummaryPlanBinding);
        }
        catch (InvalidOperationException error)
        {
            throw new StatisticReconciliationTrustedVerdictPipelineException(
                "ACTUAL_SUMMARY_PLAN_BINDING_INVALID", error);
        }
        if (observed.SemanticSha256 != normalized.SemanticSha256 ||
            relationalProof.Facts.SummaryPlanBindingSha256 !=
            normalized.SemanticSha256)
            throw Fail("ACTUAL_SUMMARY_PLAN_BINDING_MISMATCH");
        try
        {
            StatisticReconciliationActualRelationalProofOwner
                .RequirePlanProjections(relationalProof.Facts, normalized);
        }
        catch (InvalidOperationException error)
        {
            throw new StatisticReconciliationTrustedVerdictPipelineException(
                "ACTUAL_SUMMARY_PLAN_PROJECTION_INVALID", error);
        }
        return normalized;
    }
    internal static ImmutableArray<StatisticReconciliationObservationAtom>
        ProjectDirectFieldExpectedAtoms(
            StatisticReconciliationRun run,
            ImmutableArray<StatisticReconciliationObservationAtom> expectedAtoms)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (expectedAtoms.IsDefault ||
            !StatisticReconciliationRunService
                .TryResolveExactDirectFieldExpectedScope(
                    run.CanonicalFilterJson,
                    run.FilterHash,
                    run.PeriodInstanceKey,
                    run.ConceptKey,
                    out var scope) ||
            scope is null)
        {
            throw Fail("DIRECT_FIELD_EXPECTED_SCOPE_NOT_EXACT");
        }

        var projected = expectedAtoms.Where(atom =>
                atom.Family == "DIRECT" &&
                atom.Kind == "FIELD" &&
                atom.FieldId == scope.FieldId &&
                (scope.StatisticLabelCode is null ||
                 atom.MetricId == scope.StatisticLabelCode) &&
                ((scope.PeriodKey is null &&
                  scope.PeriodKeyFrom is null &&
                  scope.PeriodKeyTo is null) ||
                 atom.PeriodKey is not null) &&
                (scope.PeriodKey is null ||
                 atom.PeriodKey == scope.PeriodKey) &&
                (scope.PeriodKeyFrom is null ||
                 string.CompareOrdinal(atom.PeriodKey, scope.PeriodKeyFrom) >= 0) &&
                (scope.PeriodKeyTo is null ||
                 string.CompareOrdinal(atom.PeriodKey, scope.PeriodKeyTo) <= 0))
            .ToImmutableArray();
        if (projected.IsDefaultOrEmpty)
            throw Fail("DIRECT_FIELD_EXPECTED_SCOPE_NOT_EXACT");
        return projected;
    }

    private static bool TryComparableExpected(
        IEnumerable<StatisticReconciliationObservationAtom> input,
        out StatisticReconciliationObservationAtom[] comparable)
    {
        var result = new List<StatisticReconciliationObservationAtom>();
        foreach (var atom in input)
        {
            if (atom.ValueType != "NUMBER")
            {
                comparable = [];
                return false;
            }
            if (IsomorphicAtomKinds.Contains(atom.AtomKind) &&
                atom.ValueState ==
                    StatisticReconciliationExpectedValueStates.Value)
            {
                result.Add(atom);
                continue;
            }
            if (!ProvesOmittedZero(atom))
            {
                comparable = [];
                return false;
            }
        }
        comparable = result.ToArray();
        return comparable.Length > 0;
    }

    private static bool ProvesOmittedZero(
        StatisticReconciliationObservationAtom atom)
    {
        if (atom.DecimalScale != 0 || atom.OccurrenceCount != 0)
            return false;
        if (atom.AtomKind is StatisticReconciliationExpectedAtomKinds.Missing or
            StatisticReconciliationExpectedAtomKinds.Null or
            StatisticReconciliationExpectedAtomKinds.Empty)
            return atom.ValueState == atom.AtomKind && atom.CanonicalValue == "0";
        return IsomorphicAtomKinds.Contains(atom.AtomKind) &&
            atom.ValueState != StatisticReconciliationExpectedValueStates.Value &&
            atom.CanonicalValue.Length == 0;
    }

    private static StatisticReconciliationRootCauseLayerEvidence
        RelationalTypedLayer(
            int ordinal,
            string binding,
            IEnumerable<StatisticReconciliationObservationAtom> comparableExpected,
            IEnumerable<StatisticReconciliationActualTypedObservation> comparableActual,
            IEnumerable<StatisticReconciliationObservationAtom> fullExpected,
            IEnumerable<StatisticReconciliationActualTypedObservation> fullActual,
            StatisticReconciliationActualCommittedLayer layer,
            string relation)
    {
        var expectedArray = comparableExpected.ToArray();
        var actualArray = comparableActual.ToArray();
        var baseEvidence = TypedLayer(ordinal, binding, expectedArray, actualArray);
        var expectedSha = H("P10_TRUSTED_RELATIONAL_EXPECTED_LAYER_V2",
            StatisticReconciliationRootCauseLayers.Ordered[ordinal], relation,
            ExpectedAtomSetSha(fullExpected), ExpectedAtomSetSha(expectedArray));
        var actualSha = H("P10_TRUSTED_RELATIONAL_ACTUAL_LAYER_V2",
            StatisticReconciliationRootCauseLayers.Ordered[ordinal], relation,
            LayerSha(layer), ActualAtomSetSha(fullActual),
            ActualAtomSetSha(actualArray));
        var manifest = H("P10_TRUSTED_RELATIONAL_LAYER_DELTA_V2",
            StatisticReconciliationRootCauseLayers.Ordered[ordinal], relation,
            expectedSha, actualSha, baseEvidence.DeltaManifestSha256);
        return RootCause.CreateLayerEvidence(
            ordinal,
            StatisticReconciliationRootCauseLayers.Ordered[ordinal],
            binding,
            true,
            expectedSha,
            actualSha,
            manifest,
            baseEvidence.ComparisonCount,
            baseEvidence.NonzeroCount,
            baseEvidence.MissingCount,
            baseEvidence.ExtraCount,
            0,
            baseEvidence.DeltaState,
            baseEvidence.AttributionState,
            baseEvidence.NonzeroCount > 0 &&
                baseEvidence.IdentityEvidence.Length == 0 ? manifest : null,
            null,
            baseEvidence.IdentityEvidence);
    }

    private static StatisticReconciliationRootCauseLayerEvidence
        ZeroRelationalLayer(int ordinal, string binding, string relation,
            string proofKind)
    {
        var expectedSha = H("P10_TRUSTED_RELATIONAL_ZERO_EXPECTED_V1",
            StatisticReconciliationRootCauseLayers.Ordered[ordinal],
            proofKind, relation);
        var actualSha = H("P10_TRUSTED_RELATIONAL_ZERO_ACTUAL_V1",
            StatisticReconciliationRootCauseLayers.Ordered[ordinal],
            proofKind, relation);
        var manifest = H("P10_TRUSTED_RELATIONAL_ZERO_DELTA_V1",
            StatisticReconciliationRootCauseLayers.Ordered[ordinal],
            proofKind, relation, expectedSha, actualSha);
        return RootCause.CreateLayerEvidence(
            ordinal,
            StatisticReconciliationRootCauseLayers.Ordered[ordinal],
            binding,
            true,
            expectedSha,
            actualSha,
            manifest,
            0, 0, 0, 0, 0,
            StatisticReconciliationRootCauseLayerDeltaStates.Zero,
            StatisticReconciliationRootCauseAttributionStates.NotRequired,
            null, null, []);
    }

    private static StatisticReconciliationRootCauseLayerEvidence IncompleteLayer(
        int ordinal, string binding, string relation, string reason)
    {
        var expectedSha = H("P10_TRUSTED_RELATIONAL_INCOMPLETE_EXPECTED_V1",
            StatisticReconciliationRootCauseLayers.Ordered[ordinal], reason,
            relation);
        var actualSha = H("P10_TRUSTED_RELATIONAL_INCOMPLETE_ACTUAL_V1",
            StatisticReconciliationRootCauseLayers.Ordered[ordinal], reason,
            relation);
        var manifest = H("P10_TRUSTED_RELATIONAL_INCOMPLETE_DELTA_V1",
            StatisticReconciliationRootCauseLayers.Ordered[ordinal], reason,
            relation, expectedSha, actualSha);
        return RootCause.CreateLayerEvidence(
            ordinal,
            StatisticReconciliationRootCauseLayers.Ordered[ordinal],
            binding,
            false,
            expectedSha,
            actualSha,
            manifest,
            0, 0, 0, 0, 0,
            StatisticReconciliationRootCauseLayerDeltaStates.Undetermined,
            StatisticReconciliationRootCauseAttributionStates.Ambiguous,
            null, null, []);
    }

    private static ImmutableArray<StatisticReconciliationActualCommittedLayer>
        RequireCommittedLayers(StatisticReconciliationActualAppendResult actual)
    {
        if (actual.Layers.Length !=
            StatisticReconciliationActualCoherentLayers.RequiredOrder.Length)
            throw Fail("ACTUAL_LAYER_SET_INCOMPLETE");
        var ordered = actual.Layers.OrderBy(value => value.Ordinal).ToArray();
        var total = 0;
        for (var ordinal = 0; ordinal < ordered.Length; ordinal++)
        {
            var layer = ordered[ordinal];
            if (layer.Ordinal != ordinal ||
                layer.Layer !=
                    StatisticReconciliationActualCoherentLayers.RequiredOrder[ordinal] ||
                layer.ObservationCount < 0)
                throw Fail("ACTUAL_LAYER_METADATA_INVALID");
            var atoms = LayerAtoms(actual, layer.Layer);
            if (ordinal == 0 && atoms.Length != 0)
                throw Fail("ACTUAL_SOURCE_LAYER_TYPED_CARDINALITY_INVALID");
            if (layer.TypedObservationCount != atoms.Length ||
                layer.TypedObservationManifestSha256 !=
                    StatisticReconciliationActualTypedObservationCanonical
                        .ManifestSha256(atoms) ||
                atoms.Any(value => value.OwnerId != layer.OwnerId ||
                    value.OwnerVersionSha256 != layer.OwnerVersionSha256))
                throw Fail("ACTUAL_LAYER_TYPED_BINDING_INVALID");
            total = checked(total + atoms.Length);
        }
        if (total != actual.TypedObservations.Length)
            throw Fail("ACTUAL_TYPED_ATOM_SET_NOT_COVERED");
        return ordered.ToImmutableArray();
    }

    private static ImmutableArray<
        StatisticReconciliationRootCauseLayerEvidence> ApplyLifecycleEvidence(
            ImmutableArray<StatisticReconciliationRootCauseLayerEvidence> layers,
            string binding,
            StatisticReconciliationTrustedLifecycleProof proof)
    {
        if (layers.Length != 8)
            throw Fail("LIFECYCLE_LAYER_SET_INVALID");
        var ordinal = LifecycleOrdinal(proof);
        if (ordinal < 0)
        {
            var output = layers.ToBuilder();
            output[0] = IncompleteLayer(
                0,
                binding,
                H("P10_TRUSTED_LIFECYCLE_UNSUPPORTED_V1",
                    proof.ProofSha256,
                    proof.Outcome,
                    proof.RootCause ?? "~"),
                "LIFECYCLE_ROOT_UNSUPPORTED");
            return output.MoveToImmutable();
        }

        var current = layers[ordinal];
        if (!current.EvidenceComplete)
            return layers;
        var identityItems = proof.Mismatches
            .Where(value => value.RootCause is
                StatisticReconciliationLifecycleRootCauses.MissingIdentity or
                StatisticReconciliationLifecycleRootCauses.ExtraIdentity)
            .Select(value => RootCause.CreateIdentityEvidence(
                value.RootCause ==
                    StatisticReconciliationLifecycleRootCauses.MissingIdentity
                    ? StatisticReconciliationIdentityDeltaCodes.MissingIdentity
                    : StatisticReconciliationIdentityDeltaCodes.ExtraIdentity,
                value.IdentitySha256,
                H("P10_TRUSTED_LIFECYCLE_MISMATCH_COMPARISON_V1",
                    proof.ProofSha256,
                    value.RootCause,
                    value.IdentitySha256,
                    value.StepSemanticSha256,
                    value.DescriptorSha256)))
            .ToArray();
        var lifecycleMissing = identityItems.Count(value => value.DeltaCode ==
            StatisticReconciliationIdentityDeltaCodes.MissingIdentity);
        var lifecycleExtra = identityItems.Length - lifecycleMissing;
        if (lifecycleMissing != proof.MissingCount ||
            lifecycleExtra != proof.ExtraCount)
            return ReplaceLifecycleIncomplete(
                layers, ordinal, binding, proof,
                "LIFECYCLE_IDENTITY_COUNT_MISMATCH");

        var mismatchCount = proof.Mismatches.Length;
        var lifecycleNonzero = proof.Outcome ==
                StatisticReconciliationLifecycleOutcomes.Matched
            ? 0
            : mismatchCount;
        if (proof.Outcome != StatisticReconciliationLifecycleOutcomes.Matched &&
            lifecycleNonzero == 0)
            return ReplaceLifecycleIncomplete(
                layers, ordinal, binding, proof,
                "LIFECYCLE_MISMATCH_DESCRIPTOR_MISSING");
        var identities = current.IdentityEvidence.Concat(identityItems)
            .OrderBy(value => value.DeltaCode ==
                StatisticReconciliationIdentityDeltaCodes.MissingIdentity ? 0 : 1)
            .ThenBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(value => value.ComparisonSha256, StringComparer.Ordinal)
            .ToImmutableArray();
        var comparisonCount = checked(current.ComparisonCount + mismatchCount);
        var nonzero = checked(current.NonzeroCount + lifecycleNonzero);
        var missing = checked(current.MissingCount + lifecycleMissing);
        var extra = checked(current.ExtraCount + lifecycleExtra);
        var nonIdentityLifecycle = lifecycleNonzero -
            lifecycleMissing - lifecycleExtra;
        var native = nonIdentityLifecycle > 0
            ? H("P10_TRUSTED_LIFECYCLE_NATIVE_ATTRIBUTION_V1",
                proof.ProofSha256,
                proof.RootCause ?? "~",
                proof.MismatchSetSha256 ?? "~")
            : current.NativeAttributionSha256;
        var expectedSha = H(
            "P10_TRUSTED_LIFECYCLE_EXPECTED_LAYER_V1",
            current.ExpectedLayerSemanticSha256,
            proof.ManifestSha256 ?? "~",
            StatisticReconciliationLifecycleOutcomes.Matched);
        var actualSha = H(
            "P10_TRUSTED_LIFECYCLE_ACTUAL_LAYER_V1",
            current.ActualLayerSemanticSha256,
            proof.ProofSha256,
            proof.Outcome,
            proof.RootCause ?? "~",
            I(proof.MissingCount),
            I(proof.ExtraCount),
            proof.MismatchSetSha256 ?? "~");
        var manifest = H(
            "P10_TRUSTED_LIFECYCLE_LAYER_DELTA_V1",
            current.DeltaManifestSha256,
            proof.ProofSha256,
            expectedSha,
            actualSha);
        var outputLayers = layers.ToBuilder();
        outputLayers[ordinal] = RootCause.CreateLayerEvidence(
            ordinal,
            StatisticReconciliationRootCauseLayers.Ordered[ordinal],
            binding,
            true,
            expectedSha,
            actualSha,
            manifest,
            comparisonCount,
            nonzero,
            missing,
            extra,
            current.RedactedCount,
            nonzero == 0
                ? StatisticReconciliationRootCauseLayerDeltaStates.Zero
                : StatisticReconciliationRootCauseLayerDeltaStates.Nonzero,
            nonzero == 0
                ? StatisticReconciliationRootCauseAttributionStates.NotRequired
                : StatisticReconciliationRootCauseAttributionStates.Proven,
            nonzero == 0 ? null : native,
            current.TrustedPreRedactionProof,
            identities);
        return outputLayers.MoveToImmutable();
    }

    private static ImmutableArray<
        StatisticReconciliationRootCauseLayerEvidence>
        ReplaceLifecycleIncomplete(
            ImmutableArray<StatisticReconciliationRootCauseLayerEvidence> layers,
            int ordinal,
            string binding,
            StatisticReconciliationTrustedLifecycleProof proof,
            string reason)
    {
        var output = layers.ToBuilder();
        output[ordinal] = IncompleteLayer(
            ordinal,
            binding,
            H("P10_TRUSTED_LIFECYCLE_INCOMPLETE_V1",
                proof.ProofSha256,
                proof.RootCause ?? "~",
                proof.MismatchSetSha256 ?? "~"),
            reason);
        return output.MoveToImmutable();
    }

    private static int LifecycleOrdinal(
        StatisticReconciliationTrustedLifecycleProof proof)
    {
        if (proof.Outcome == StatisticReconciliationLifecycleOutcomes.Matched &&
            proof.RootCause is null && proof.Mismatches.Length == 0)
            return 0;
        return proof.RootCause switch
        {
            StatisticReconciliationLifecycleRootCauses.SourceMembership or
            StatisticReconciliationLifecycleRootCauses.MissingIdentity or
            StatisticReconciliationLifecycleRootCauses.ExtraIdentity => 0,
            StatisticReconciliationLifecycleRootCauses.Projection => 1,
            StatisticReconciliationLifecycleRootCauses.Aggregate => 2,
            _ => -1
        };
    }

    private static StatisticReconciliationActualTypedObservation[] LayerAtoms(
        StatisticReconciliationActualAppendResult actual, string layer)
        => actual.TypedObservations.Where(value => value.Layer == layer)
            .OrderBy(value => value.Ordinal).ToArray();

    private static bool IsOpaque(
        StatisticReconciliationActualTypedObservation value)
        => value.MetricId.StartsWith(OpaqueMetricPrefix,
            StringComparison.Ordinal);

    private static bool IsSynthetic(
        StatisticReconciliationActualTypedObservation value)
        => IsOpaque(value) || value.MetricId.StartsWith(ExplicitMetricPrefix,
            StringComparison.Ordinal);

    private static string Relation(
        string layer,
        StatisticReconciliationObservation expectedCommit,
        ImmutableArray<StatisticReconciliationObservationAtom> expectedAtoms,
        StatisticReconciliationActualAppendResult actual,
        StatisticReconciliationActualCommittedLayer committedLayer,
        params string[] fields)
        => H("P10_TRUSTED_RELATIONAL_PROOF_V4",
            new[]
            {
                layer,
                expectedCommit.GenerationId,
                expectedCommit.GenerationSemanticSha256,
                expectedCommit.MetricPlanSha256,
                ExpectedAtomSetSha(expectedAtoms),
                actual.GenerationId,
                actual.GenerationSemanticSha256,
                expectedCommit.SourceSetSha256,
                expectedCommit.Commit?.MembershipSemanticSha256 ?? "~",
                actual.CapturedSourceSetSha256,
                actual.CapturedMembershipSemanticSha256 ?? "~",
                actual.CapturedSourceDecisionManifestSha256 ?? "~",
                I(actual.CapturedSourceDecisionCount ?? -1),
                actual.CapturedLifecycleMetricScopeSha256 ?? "~",
                actual.CapturedRuntimePinSetSha256,
                actual.CapturedResultPinSetSha256,
                actual.CapturedExportPinSetSha256,
                actual.CommittedRunBinding.CoherentCatalogPinSetSha256,
                LayerSha(committedLayer),
                ActualAtomSetSha(LayerAtoms(actual, committedLayer.Layer))
            }.Concat(fields).ToArray());

    internal static string RunRuntimeBindingSha256(
        StatisticReconciliationRun run)
        => H("P10_TRUSTED_RUN_RUNTIME_BINDING_V1",
            run.FlowTemplateVersionId ?? "~",
            run.FlowPayloadHash ?? "~",
            run.FlowInstanceId ?? "~",
            run.FlowInstanceRevision?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "~",
            run.FlowExecutionEpochId ?? "~",
            run.FlowExecutionEpoch?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "~",
            run.FlowExecutionEpochRevision?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "~",
            run.FlowStepId ?? "~",
            run.FlowBranchId ?? "~",
            run.FlowStepInstanceId ?? "~",
            run.FlowStepInstanceRevision?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "~",
            run.FlowContributionPolicy ?? "~",
            run.FlowContributionPolicyHash ?? "~",
            run.FlowEffectiveStatus ?? "~",
            run.FlowContributionProvenanceHash ?? "~");

    private static string LayerSha(
        StatisticReconciliationActualCommittedLayer layer)
        => H("P10_TRUSTED_COMMITTED_LAYER_BINDING_V2",
            I(layer.Ordinal), layer.Layer, layer.OwnerId,
            layer.OwnerVersionSha256, I(layer.ObservationCount),
            layer.CaptureSemanticSha256, layer.LayerSemanticSha256,
            layer.TypedObservationManifestSha256,
            I(layer.TypedObservationCount));

    private static string ExpectedAtomSetSha(
        IEnumerable<StatisticReconciliationObservationAtom> atoms)
        => H("P10_TRUSTED_FULL_EXPECTED_ATOM_SET_V2",
            atoms.Select(value => value.AtomSemanticSha256)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray());

    private static string ActualAtomSetSha(
        IEnumerable<StatisticReconciliationActualTypedObservation> atoms)
        => H("P10_TRUSTED_FULL_ACTUAL_ATOM_SET_V2",
            atoms.Select(value => value.AtomSemanticSha256)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray());
}
internal static class StatisticReconciliationTrustedCatalogComparison
{
    private const string Domain = "P10_TRUSTED_COMPARISON_CATALOG_PINS_V1";

    internal static string FromExpected(
        StatisticReconciliationObservationCatalogPins pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        return Build(
            pins.P9CatalogVersion,
            pins.P9CatalogRawSha256,
            pins.P9CatalogSemanticSha256,
            pins.P9SchemaRawSha256,
            pins.P9SchemaSemanticSha256,
            pins.P9StageLockSha256,
            pins.CandidateChainId,
            pins.CandidatePromptId,
            pins.CandidateCatalogVersion,
            pins.CandidateCatalogRawSha256,
            pins.CandidateCatalogSemanticSha256,
            pins.CandidateSchemaRawSha256,
            pins.CandidateSchemaSemanticSha256,
            pins.CandidateStageLockSha256);
    }

    internal static string FromActual(
        StatisticReconciliationActualPublicationCatalogPins pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        return Build(
            pins.P9CatalogVersion,
            pins.P9CatalogRawSha256,
            pins.P9CatalogSemanticSha256,
            pins.P9SchemaRawSha256,
            pins.P9SchemaSemanticSha256,
            pins.P9StageLockSha256,
            pins.CandidateChainId,
            pins.CandidatePromptId,
            pins.CandidateCatalogVersion,
            pins.CandidateCatalogRawSha256,
            pins.CandidateCatalogSemanticSha256,
            pins.CandidateSchemaRawSha256,
            pins.CandidateSchemaSemanticSha256,
            pins.CandidateStageLockSha256);
    }

    internal static string FromCurrent(
        StatisticReconciliationActualPublicationCatalogPins pins,
        string committedCoherentCatalogPinSetSha256,
        string currentCoherentCatalogPinSetSha256)
    {
        var logical = FromActual(pins);
        var committed = StatisticReconciliationActualCanonical.Sha256(
            committedCoherentCatalogPinSetSha256,
            "TRUSTED_COMPARISON_COMMITTED_COHERENT_CATALOG");
        var current = StatisticReconciliationActualCanonical.Sha256(
            currentCoherentCatalogPinSetSha256,
            "TRUSTED_COMPARISON_CURRENT_COHERENT_CATALOG");
        return StringComparer.Ordinal.Equals(committed, current)
            ? logical
            : StatisticReconciliationActualCanonical.Hash(
                "P10_TRUSTED_COMPARISON_CATALOG_DRIFT_V1",
                logical,
                committed,
                current);
    }

    private static string Build(
        string p9CatalogVersion,
        string p9CatalogRawSha256,
        string p9CatalogSemanticSha256,
        string p9SchemaRawSha256,
        string p9SchemaSemanticSha256,
        string p9StageLockSha256,
        string candidateChainId,
        string candidatePromptId,
        string candidateCatalogVersion,
        string candidateCatalogRawSha256,
        string candidateCatalogSemanticSha256,
        string candidateSchemaRawSha256,
        string candidateSchemaSemanticSha256,
        string candidateStageLockSha256)
        => StatisticReconciliationActualCanonical.Hash(
            Domain,
            StatisticReconciliationActualCanonical.Required(
                p9CatalogVersion, "TRUSTED_COMPARISON_P9_CATALOG_VERSION"),
            StatisticReconciliationActualCanonical.Sha256(
                p9CatalogRawSha256, "TRUSTED_COMPARISON_P9_CATALOG_RAW"),
            StatisticReconciliationActualCanonical.Sha256(
                p9CatalogSemanticSha256,
                "TRUSTED_COMPARISON_P9_CATALOG_SEMANTIC"),
            StatisticReconciliationActualCanonical.Sha256(
                p9SchemaRawSha256, "TRUSTED_COMPARISON_P9_SCHEMA_RAW"),
            StatisticReconciliationActualCanonical.Sha256(
                p9SchemaSemanticSha256,
                "TRUSTED_COMPARISON_P9_SCHEMA_SEMANTIC"),
            StatisticReconciliationActualCanonical.Sha256(
                p9StageLockSha256, "TRUSTED_COMPARISON_P9_STAGE_LOCK"),
            StatisticReconciliationActualCanonical.Required(
                candidateChainId, "TRUSTED_COMPARISON_CANDIDATE_CHAIN"),
            StatisticReconciliationActualCanonical.Required(
                candidatePromptId, "TRUSTED_COMPARISON_CANDIDATE_PROMPT"),
            StatisticReconciliationActualCanonical.Required(
                candidateCatalogVersion,
                "TRUSTED_COMPARISON_CANDIDATE_CATALOG_VERSION"),
            StatisticReconciliationActualCanonical.Sha256(
                candidateCatalogRawSha256,
                "TRUSTED_COMPARISON_CANDIDATE_CATALOG_RAW"),
            StatisticReconciliationActualCanonical.Sha256(
                candidateCatalogSemanticSha256,
                "TRUSTED_COMPARISON_CANDIDATE_CATALOG_SEMANTIC"),
            StatisticReconciliationActualCanonical.Sha256(
                candidateSchemaRawSha256,
                "TRUSTED_COMPARISON_CANDIDATE_SCHEMA_RAW"),
            StatisticReconciliationActualCanonical.Sha256(
                candidateSchemaSemanticSha256,
                "TRUSTED_COMPARISON_CANDIDATE_SCHEMA_SEMANTIC"),
            StatisticReconciliationActualCanonical.Sha256(
                candidateStageLockSha256,
                "TRUSTED_COMPARISON_CANDIDATE_STAGE_LOCK"));
}