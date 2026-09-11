using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace tdtd_be.Services.StatisticsReconciliation.TypedDelta;

public sealed class StatisticReconciliationRootCauseClassifier
{
    private const int MaximumComparisons = 99_991;
    private readonly StatisticReconciliationStatefulComparator _stateful = new();

    public StatisticReconciliationRootCausePermissionEvidence CreatePermissionEvidence(
        string state,
        string authorizationSnapshotSha256)
    {
        if (state is not (
                StatisticReconciliationRootCausePermissionStates.Authorized or
                StatisticReconciliationRootCausePermissionStates.Denied or
                StatisticReconciliationRootCausePermissionStates.Ambiguous))
            throw Invalid("permission.state");
        RequireSha(authorizationSnapshotSha256, "permission.authorizationSnapshotSha256");
        return new StatisticReconciliationRootCausePermissionEvidence(
            state,
            authorizationSnapshotSha256,
            HashFields(
                "P10_ROOT_CAUSE_PERMISSION_EVIDENCE_V1",
                state,
                authorizationSnapshotSha256));
    }

    public StatisticReconciliationRootCauseIdentityEvidence CreateIdentityEvidence(
        string deltaCode,
        string identitySha256,
        string comparisonSha256)
    {
        if (deltaCode is not (
                StatisticReconciliationIdentityDeltaCodes.MissingIdentity or
                StatisticReconciliationIdentityDeltaCodes.ExtraIdentity))
            throw Invalid("identityEvidence.deltaCode");
        RequireSha(identitySha256, "identityEvidence.identitySha256");
        RequireSha(comparisonSha256, "identityEvidence.comparisonSha256");
        return new StatisticReconciliationRootCauseIdentityEvidence(
            deltaCode,
            identitySha256,
            comparisonSha256,
            HashFields(
                "P10_ROOT_CAUSE_IDENTITY_EVIDENCE_V1",
                deltaCode,
                identitySha256,
                comparisonSha256));
    }

    public StatisticReconciliationRootCausePreRedactionProof CreatePreRedactionProof(
        int ordinal,
        string layer,
        string comparisonBindingSha256,
        string expectedLayerSemanticSha256,
        string actualLayerSemanticSha256,
        IEnumerable<StatisticReconciliationRootCausePreRedactionPair> pairs)
    {
        if (ordinal is < 0 or >= 8 ||
            layer != StatisticReconciliationRootCauseLayers.Ordered[ordinal])
            throw Invalid("preRedactionProof.ordinalOrLayer");
        RequireSha(comparisonBindingSha256,
            "preRedactionProof.comparisonBindingSha256");
        RequireSha(expectedLayerSemanticSha256,
            "preRedactionProof.expectedLayerSemanticSha256");
        RequireSha(actualLayerSemanticSha256,
            "preRedactionProof.actualLayerSemanticSha256");
        ArgumentNullException.ThrowIfNull(pairs);
        var inputs = pairs.Take(MaximumComparisons + 1).ToArray();
        if (inputs.Length is < 1 or > MaximumComparisons)
            throw Invalid("preRedactionProof.comparisonCount");

        var comparisons = inputs.Select((pair, index) =>
        {
            if (pair is null || pair.Expected?.ValueState ==
                    StatisticReconciliationObservationValueStates.Redacted ||
                pair.Actual?.ValueState ==
                    StatisticReconciliationObservationValueStates.Redacted)
                throw Invalid($"preRedactionProof.pairs[{index}]");
            try
            {
                var comparison = _stateful.Compare(pair.Expected, pair.Actual);
                return (pair.RedactedOutward, Comparison: comparison);
            }
            catch (StatisticReconciliationTypedComparisonException)
            {
                throw Invalid($"preRedactionProof.pairs[{index}]");
            }
        }).OrderBy(item => item.Comparison.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(item => item.Comparison.ComparisonSha256, StringComparer.Ordinal)
            .ToArray();
        if (comparisons.Select(item => item.Comparison.IdentitySha256)
                .Distinct(StringComparer.Ordinal).Count() != comparisons.Length)
            throw Invalid("preRedactionProof.identityUnique");

        var redacted = comparisons.Where(item => item.RedactedOutward).ToArray();
        if (redacted.Length == 0)
            throw Invalid("preRedactionProof.redactedCount");
        var nonzeroCount = comparisons.Count(item => !item.Comparison.Equal);
        var missingCount = comparisons.Count(item => item.Comparison.DeltaCode ==
            StatisticReconciliationIdentityDeltaCodes.MissingIdentity);
        var extraCount = comparisons.Count(item => item.Comparison.DeltaCode ==
            StatisticReconciliationIdentityDeltaCodes.ExtraIdentity);
        var redactedNonzeroCount = redacted.Count(item => !item.Comparison.Equal);
        var identityEvidence = comparisons
            .Where(item => item.Comparison.DeltaCode is
                StatisticReconciliationIdentityDeltaCodes.MissingIdentity or
                StatisticReconciliationIdentityDeltaCodes.ExtraIdentity)
            .Select(item => CreateIdentityEvidence(
                item.Comparison.DeltaCode,
                item.Comparison.IdentitySha256,
                item.Comparison.ComparisonSha256))
            .OrderBy(item => IdentityPriority(item.DeltaCode))
            .ThenBy(item => item.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(item => item.ComparisonSha256, StringComparer.Ordinal)
            .ThenBy(item => item.EvidenceSemanticSha256, StringComparer.Ordinal)
            .ToImmutableArray();
        var comparisonManifestSha256 = HashFields(
            "P10_ROOT_CAUSE_PRE_REDACTION_COMPARISON_SET_V1",
            comparisons.Select(item => item.Comparison.ComparisonSha256).ToArray());
        var redactedComparisonManifestSha256 = HashFields(
            "P10_ROOT_CAUSE_REDACTED_COMPARISON_SET_V1",
            redacted.Select(item => item.Comparison.ComparisonSha256).ToArray());
        var proofSha256 = PreRedactionProofSha(
            ordinal,
            layer,
            comparisonBindingSha256,
            expectedLayerSemanticSha256,
            actualLayerSemanticSha256,
            comparisons.Length,
            nonzeroCount,
            missingCount,
            extraCount,
            redacted.Length,
            redactedNonzeroCount,
            identityEvidence,
            comparisonManifestSha256,
            redactedComparisonManifestSha256);
        return new StatisticReconciliationRootCausePreRedactionProof(
            ordinal,
            layer,
            comparisonBindingSha256,
            expectedLayerSemanticSha256,
            actualLayerSemanticSha256,
            comparisons.Length,
            nonzeroCount,
            missingCount,
            extraCount,
            redacted.Length,
            redactedNonzeroCount,
            identityEvidence,
            comparisonManifestSha256,
            redactedComparisonManifestSha256,
            proofSha256);
    }

    public StatisticReconciliationRootCauseLayerEvidence CreateLayerEvidence(
        int ordinal,
        string layer,
        string comparisonBindingSha256,
        bool evidenceComplete,
        string expectedLayerSemanticSha256,
        string actualLayerSemanticSha256,
        string deltaManifestSha256,
        int comparisonCount,
        int nonzeroCount,
        int missingCount,
        int extraCount,
        int redactedCount,
        string deltaState,
        string attributionState,
        string? nativeAttributionSha256,
        StatisticReconciliationRootCausePreRedactionProof? trustedPreRedactionProof,
        IEnumerable<StatisticReconciliationRootCauseIdentityEvidence> identityEvidence)
    {
        if (ordinal is < 0 or >= 8 ||
            layer != StatisticReconciliationRootCauseLayers.Ordered[ordinal])
            throw Invalid("layer.ordinalOrName");
        RequireSha(comparisonBindingSha256, "layer.comparisonBindingSha256");
        RequireSha(expectedLayerSemanticSha256, "layer.expectedLayerSemanticSha256");
        RequireSha(actualLayerSemanticSha256, "layer.actualLayerSemanticSha256");
        RequireSha(deltaManifestSha256, "layer.deltaManifestSha256");
        if (comparisonCount is < 0 or > MaximumComparisons ||
            nonzeroCount < 0 || nonzeroCount > comparisonCount ||
            missingCount < 0 || extraCount < 0 || redactedCount < 0 ||
            missingCount + extraCount > nonzeroCount ||
            redactedCount > comparisonCount)
            throw Invalid("layer.counts");

        var identities = NormalizeIdentityEvidence(identityEvidence);
        if (identities.Count(item =>
                item.DeltaCode == StatisticReconciliationIdentityDeltaCodes.MissingIdentity) !=
                missingCount ||
            identities.Count(item =>
                item.DeltaCode == StatisticReconciliationIdentityDeltaCodes.ExtraIdentity) !=
                extraCount)
            throw Invalid("layer.identityCounts");

        string? trustedPreRedactionProofSha256 = null;
        if (redactedCount > 0)
        {
            if (trustedPreRedactionProof is null)
                throw Invalid("layer.trustedPreRedactionProof");
            var proof = NormalizePreRedactionProof(trustedPreRedactionProof);
            if (proof.Ordinal != ordinal || proof.Layer != layer ||
                proof.ComparisonBindingSha256 != comparisonBindingSha256 ||
                proof.ExpectedLayerSemanticSha256 != expectedLayerSemanticSha256 ||
                proof.ActualLayerSemanticSha256 != actualLayerSemanticSha256 ||
                proof.ComparisonManifestSha256 != deltaManifestSha256 ||
                proof.ComparisonCount != comparisonCount ||
                proof.NonzeroCount != nonzeroCount ||
                proof.MissingCount != missingCount || proof.ExtraCount != extraCount ||
                proof.RedactedCount != redactedCount ||
                proof.RedactedNonzeroCount > nonzeroCount ||
                !proof.IdentityEvidence.SequenceEqual(identities))
                throw Invalid("layer.preRedactionProofBinding");
            trustedPreRedactionProofSha256 = proof.ProofSemanticSha256;
        }
        else if (trustedPreRedactionProof is not null)
            throw Invalid("layer.unexpectedPreRedactionProof");

        if (!evidenceComplete)
        {
            if (deltaState != StatisticReconciliationRootCauseLayerDeltaStates.Undetermined ||
                attributionState != StatisticReconciliationRootCauseAttributionStates.Ambiguous ||
                comparisonCount != 0 || nonzeroCount != 0 || missingCount != 0 ||
                extraCount != 0 || redactedCount != 0 || identities.Length != 0 ||
                nativeAttributionSha256 is not null)
                throw Invalid("layer.incompleteShape");
        }
        else if (nonzeroCount == 0)
        {
            if (deltaState != StatisticReconciliationRootCauseLayerDeltaStates.Zero ||
                attributionState !=
                    StatisticReconciliationRootCauseAttributionStates.NotRequired ||
                identities.Length != 0 || nativeAttributionSha256 is not null)
                throw Invalid("layer.zeroShape");
        }
        else
        {
            if (deltaState != StatisticReconciliationRootCauseLayerDeltaStates.Nonzero ||
                attributionState is not (
                    StatisticReconciliationRootCauseAttributionStates.Proven or
                    StatisticReconciliationRootCauseAttributionStates.Ambiguous))
                throw Invalid("layer.nonzeroShape");
            if (attributionState == StatisticReconciliationRootCauseAttributionStates.Proven)
            {
                if (nativeAttributionSha256 is null && identities.Length == 0)
                    throw Invalid("layer.provenSupport");
                if (nativeAttributionSha256 is not null)
                    RequireSha(nativeAttributionSha256, "layer.nativeAttributionSha256");
            }
            else if (nativeAttributionSha256 is not null || identities.Length != 0)
                throw Invalid("layer.ambiguousSupport");
        }

        var layerSha = HashFields(
            "P10_ROOT_CAUSE_LAYER_EVIDENCE_V1",
            I(ordinal),
            layer,
            comparisonBindingSha256,
            evidenceComplete ? "1" : "0",
            expectedLayerSemanticSha256,
            actualLayerSemanticSha256,
            deltaManifestSha256,
            I(comparisonCount),
            I(nonzeroCount),
            I(missingCount),
            I(extraCount),
            I(redactedCount),
            deltaState,
            attributionState,
            nativeAttributionSha256 ?? "~",
            trustedPreRedactionProofSha256 ?? "~",
            HashFields(
                "P10_ROOT_CAUSE_IDENTITY_EVIDENCE_SET_V1",
                identities.Select(item => item.EvidenceSemanticSha256).ToArray()));
        return new StatisticReconciliationRootCauseLayerEvidence(
            ordinal,
            layer,
            comparisonBindingSha256,
            evidenceComplete,
            expectedLayerSemanticSha256,
            actualLayerSemanticSha256,
            deltaManifestSha256,
            comparisonCount,
            nonzeroCount,
            missingCount,
            extraCount,
            redactedCount,
            deltaState,
            attributionState,
            nativeAttributionSha256,
            trustedPreRedactionProof,
            identities,
            layerSha);
    }

    public StatisticReconciliationRootCauseClassification Classify(
        StatisticReconciliationRootCauseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var permission = NormalizePermission(request.Permission);

        if (permission.State != StatisticReconciliationRootCausePermissionStates.Authorized)
        {
            if (request.Freshness is not null || !request.Layers.IsDefaultOrEmpty)
                throw Invalid("request.permissionMustPrecedeEvidence");
            var opaquePermissionBinding = HashFields(
                "P10_ROOT_CAUSE_PERMISSION_BINDING_V1",
                permission.EvidenceSemanticSha256);
            return Result(
                opaquePermissionBinding,
                permission,
                null,
                [],
                StatisticReconciliationRootCauseClassificationStates.Proven,
                StatisticReconciliationRootCauseClasses.Permission,
                null,
                null,
                permission.EvidenceSemanticSha256,
                true,
                true);
        }

        RequireSha(request.ComparisonBindingSha256,
            "request.comparisonBindingSha256");
        var freshness = NormalizeFreshness(request.Freshness);
        if (freshness.ExpectedBindingSha256 != request.ComparisonBindingSha256)
            throw Invalid("request.freshnessBinding");
        if (freshness.State != StatisticReconciliationFreshnessStates.Fresh)
        {
            if (!request.Layers.IsDefaultOrEmpty)
                throw Invalid("request.freshnessMustPrecedeLayers");
            return Result(
                request.ComparisonBindingSha256,
                permission,
                freshness,
                [],
                StatisticReconciliationRootCauseClassificationStates.Proven,
                StatisticReconciliationRootCauseClasses.Freshness,
                null,
                null,
                freshness.AssessmentSha256,
                true,
                true);
        }

        var layers = NormalizeLayers(request.Layers, request.ComparisonBindingSha256);
        foreach (var layer in layers)
        {
            if (!layer.EvidenceComplete)
            {
                return Result(
                    request.ComparisonBindingSha256,
                    permission,
                    freshness,
                    layers,
                    StatisticReconciliationRootCauseClassificationStates
                        .InsufficientEvidence,
                    StatisticReconciliationRootCauseClasses.Unknown,
                    layer.Layer,
                    layer.Ordinal,
                    layer.LayerEvidenceSemanticSha256,
                    true,
                    false);
            }
            if (layer.NonzeroCount == 0)
                continue;
            if (layer.AttributionState ==
                StatisticReconciliationRootCauseAttributionStates.Ambiguous)
            {
                return Result(
                    request.ComparisonBindingSha256,
                    permission,
                    freshness,
                    layers,
                    StatisticReconciliationRootCauseClassificationStates.Unknown,
                    StatisticReconciliationRootCauseClasses.Unknown,
                    layer.Layer,
                    layer.Ordinal,
                    layer.LayerEvidenceSemanticSha256,
                    true,
                    false);
            }

            var identity = layer.IdentityEvidence.FirstOrDefault();
            if (identity is not null)
            {
                var identityClass = identity.DeltaCode ==
                    StatisticReconciliationIdentityDeltaCodes.MissingIdentity
                    ? StatisticReconciliationRootCauseClasses.MissingIdentity
                    : StatisticReconciliationRootCauseClasses.ExtraIdentity;
                return Result(
                    request.ComparisonBindingSha256,
                    permission,
                    freshness,
                    layers,
                    StatisticReconciliationRootCauseClassificationStates.Proven,
                    identityClass,
                    layer.Layer,
                    layer.Ordinal,
                    identity.EvidenceSemanticSha256,
                    true,
                    true);
            }
            return Result(
                request.ComparisonBindingSha256,
                permission,
                freshness,
                layers,
                StatisticReconciliationRootCauseClassificationStates.Proven,
                NativeClass(layer.Layer),
                layer.Layer,
                layer.Ordinal,
                layer.NativeAttributionSha256,
                true,
                true);
        }

        return Result(
            request.ComparisonBindingSha256,
            permission,
            freshness,
            layers,
            StatisticReconciliationRootCauseClassificationStates.NoDivergence,
            null,
            null,
            null,
            null,
            false,
            true);
    }

    private StatisticReconciliationRootCauseClassification Result(
        string comparisonBindingSha256,
        StatisticReconciliationRootCausePermissionEvidence permission,
        StatisticReconciliationFreshnessAssessment? freshness,
        ImmutableArray<StatisticReconciliationRootCauseLayerEvidence> layers,
        string state,
        string? primaryClass,
        string? earliestLayer,
        int? earliestOrdinal,
        string? supportingEvidenceSha256,
        bool causeRequired,
        bool provable)
    {
        if (supportingEvidenceSha256 is not null)
            RequireSha(supportingEvidenceSha256, "result.supportingEvidenceSha256");
        var layerManifestSha256 = HashFields(
            "P10_ROOT_CAUSE_LAYER_EVIDENCE_SET_V1",
            layers.Select(item => item.LayerEvidenceSemanticSha256).ToArray());
        var evidenceSetSha256 = HashFields(
            "P10_ROOT_CAUSE_EVIDENCE_SET_V1",
            comparisonBindingSha256,
            permission.EvidenceSemanticSha256,
            freshness?.AssessmentSha256 ?? "~",
            layerManifestSha256);
        var unknownBlocks = state is (
            StatisticReconciliationRootCauseClassificationStates.Unknown or
            StatisticReconciliationRootCauseClassificationStates.InsufficientEvidence);
        var missing = primaryClass == StatisticReconciliationRootCauseClasses.MissingIdentity;
        var extra = primaryClass == StatisticReconciliationRootCauseClasses.ExtraIdentity;
        var classificationSha256 = HashFields(
            "P10_ROOT_CAUSE_CLASSIFICATION_V1",
            state,
            primaryClass ?? "~",
            earliestLayer ?? "~",
            earliestOrdinal is null ? "~" : I(earliestOrdinal.Value),
            supportingEvidenceSha256 ?? "~",
            causeRequired ? "1" : "0",
            provable ? "1" : "0",
            unknownBlocks ? "1" : "0",
            missing ? "1" : "0",
            extra ? "1" : "0",
            evidenceSetSha256);
        return new StatisticReconciliationRootCauseClassification(
            state,
            primaryClass,
            earliestLayer,
            earliestOrdinal,
            supportingEvidenceSha256,
            permission.State,
            freshness?.AssessmentSha256,
            causeRequired,
            provable,
            unknownBlocks,
            missing,
            extra,
            layers,
            evidenceSetSha256,
            classificationSha256);
    }

    private StatisticReconciliationRootCausePreRedactionProof
        NormalizePreRedactionProof(
            StatisticReconciliationRootCausePreRedactionProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (proof.Ordinal is < 0 or >= 8 ||
            proof.Layer != StatisticReconciliationRootCauseLayers.Ordered[proof.Ordinal] ||
            proof.ComparisonCount is < 1 or > MaximumComparisons ||
            proof.NonzeroCount < 0 || proof.NonzeroCount > proof.ComparisonCount ||
            proof.MissingCount < 0 || proof.ExtraCount < 0 ||
            proof.MissingCount + proof.ExtraCount > proof.NonzeroCount ||
            proof.RedactedCount is < 1 ||
            proof.RedactedCount > proof.ComparisonCount ||
            proof.RedactedNonzeroCount < 0 ||
            proof.RedactedNonzeroCount > proof.RedactedCount ||
            proof.RedactedNonzeroCount > proof.NonzeroCount)
            throw Invalid("preRedactionProof.shape");
        RequireSha(proof.ComparisonBindingSha256,
            "preRedactionProof.comparisonBindingSha256");
        RequireSha(proof.ExpectedLayerSemanticSha256,
            "preRedactionProof.expectedLayerSemanticSha256");
        RequireSha(proof.ActualLayerSemanticSha256,
            "preRedactionProof.actualLayerSemanticSha256");
        RequireSha(proof.ComparisonManifestSha256,
            "preRedactionProof.comparisonManifestSha256");
        RequireSha(proof.RedactedComparisonManifestSha256,
            "preRedactionProof.redactedComparisonManifestSha256");
        RequireSha(proof.ProofSemanticSha256,
            "preRedactionProof.proofSemanticSha256");
        var identityEvidence = NormalizeIdentityEvidence(proof.IdentityEvidence);
        if (!identityEvidence.SequenceEqual(proof.IdentityEvidence) ||
            identityEvidence.Count(item => item.DeltaCode ==
                StatisticReconciliationIdentityDeltaCodes.MissingIdentity) !=
                proof.MissingCount ||
            identityEvidence.Count(item => item.DeltaCode ==
                StatisticReconciliationIdentityDeltaCodes.ExtraIdentity) !=
                proof.ExtraCount)
            throw Invalid("preRedactionProof.identityEvidence");
        var expected = PreRedactionProofSha(
            proof.Ordinal,
            proof.Layer,
            proof.ComparisonBindingSha256,
            proof.ExpectedLayerSemanticSha256,
            proof.ActualLayerSemanticSha256,
            proof.ComparisonCount,
            proof.NonzeroCount,
            proof.MissingCount,
            proof.ExtraCount,
            proof.RedactedCount,
            proof.RedactedNonzeroCount,
            identityEvidence,
            proof.ComparisonManifestSha256,
            proof.RedactedComparisonManifestSha256);
        if (expected != proof.ProofSemanticSha256)
            throw Invalid("preRedactionProof.proofSemanticSha256");
        return proof;
    }

    private StatisticReconciliationRootCausePermissionEvidence NormalizePermission(
        StatisticReconciliationRootCausePermissionEvidence value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = CreatePermissionEvidence(
            value.State,
            value.AuthorizationSnapshotSha256);
        if (normalized.EvidenceSemanticSha256 != value.EvidenceSemanticSha256)
            throw Invalid("permission.evidenceSemanticSha256");
        return normalized;
    }

    private static StatisticReconciliationFreshnessAssessment NormalizeFreshness(
        StatisticReconciliationFreshnessAssessment? value)
    {
        if (value is null)
            throw Invalid("request.freshness");
        var fresh = value.State == StatisticReconciliationFreshnessStates.Fresh;
        if (!fresh && value.State != StatisticReconciliationFreshnessStates.Stale)
            throw Invalid("freshness.state");
        if (fresh &&
            (value.DriftDomain != StatisticReconciliationFreshnessDomains.None ||
             value.ReasonCode != StatisticReconciliationFreshnessReasons.None ||
             !value.CompleteEvidence))
            throw Invalid("freshness.freshShape");
        if (!fresh && !IsCanonicalStaleFreshness(value))
            throw Invalid("freshness.staleShape");
        if (value.MatchAllowed != fresh || value.MismatchAsDataAllowed != fresh ||
            value.Signable != fresh)
            throw Invalid("freshness.actionGates");
        RequireSha(value.ExpectedBindingSha256, "freshness.expectedBindingSha256");
        RequireSha(value.CapturedPinsSha256, "freshness.capturedPinsSha256");
        RequireSha(value.CurrentPinsSha256, "freshness.currentPinsSha256");
        var derived = HashFields(
            "P10_FRESHNESS_ASSESSMENT_V1",
            value.State,
            value.DriftDomain,
            value.ReasonCode,
            value.CompleteEvidence ? "1" : "0",
            value.ExpectedBindingSha256,
            value.CapturedPinsSha256,
            value.CurrentPinsSha256);
        if (derived != value.AssessmentSha256)
            throw Invalid("freshness.assessmentSha256");
        return value;
    }

    private static bool IsCanonicalStaleFreshness(
        StatisticReconciliationFreshnessAssessment value)
        =>
            value.DriftDomain == StatisticReconciliationFreshnessDomains.Capture &&
            value.ReasonCode == StatisticReconciliationFreshnessReasons.IncompleteCapture &&
            !value.CompleteEvidence ||
            value.DriftDomain == StatisticReconciliationFreshnessDomains.Capture &&
            value.ReasonCode == StatisticReconciliationFreshnessReasons.MixedRevision &&
            value.CompleteEvidence ||
            value.DriftDomain == StatisticReconciliationFreshnessDomains.Source &&
            value.ReasonCode == StatisticReconciliationFreshnessReasons.SourceDrift &&
            value.CompleteEvidence ||
            value.DriftDomain == StatisticReconciliationFreshnessDomains.Configuration &&
            value.ReasonCode == StatisticReconciliationFreshnessReasons.ConfigurationDrift &&
            value.CompleteEvidence ||
            value.DriftDomain == StatisticReconciliationFreshnessDomains.Catalog &&
            value.ReasonCode == StatisticReconciliationFreshnessReasons.CatalogDrift &&
            value.CompleteEvidence ||
            value.DriftDomain == StatisticReconciliationFreshnessDomains.Runtime &&
            value.ReasonCode == StatisticReconciliationFreshnessReasons.RuntimeDrift &&
            value.CompleteEvidence ||
            value.DriftDomain == StatisticReconciliationFreshnessDomains.Result &&
            value.ReasonCode == StatisticReconciliationFreshnessReasons.ResultDrift &&
            value.CompleteEvidence ||
            value.DriftDomain == StatisticReconciliationFreshnessDomains.Export &&
            value.ReasonCode == StatisticReconciliationFreshnessReasons.ExportDrift &&
            value.CompleteEvidence;

    private ImmutableArray<StatisticReconciliationRootCauseLayerEvidence> NormalizeLayers(
        ImmutableArray<StatisticReconciliationRootCauseLayerEvidence> values,
        string comparisonBindingSha256)
    {
        if (values.IsDefault || values.Length !=
            StatisticReconciliationRootCauseLayers.Ordered.Length)
            throw Invalid("layers.exactCount");
        var output = ImmutableArray.CreateBuilder<
            StatisticReconciliationRootCauseLayerEvidence>(values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index] ?? throw Invalid($"layers[{index}]");
            if (value.Ordinal != index ||
                value.Layer != StatisticReconciliationRootCauseLayers.Ordered[index] ||
                value.ComparisonBindingSha256 != comparisonBindingSha256)
                throw Invalid($"layers[{index}].orderOrBinding");
            var normalized = CreateLayerEvidence(
                value.Ordinal,
                value.Layer,
                value.ComparisonBindingSha256,
                value.EvidenceComplete,
                value.ExpectedLayerSemanticSha256,
                value.ActualLayerSemanticSha256,
                value.DeltaManifestSha256,
                value.ComparisonCount,
                value.NonzeroCount,
                value.MissingCount,
                value.ExtraCount,
                value.RedactedCount,
                value.DeltaState,
                value.AttributionState,
                value.NativeAttributionSha256,
                value.TrustedPreRedactionProof,
                value.IdentityEvidence);
            if (normalized.LayerEvidenceSemanticSha256 !=
                    value.LayerEvidenceSemanticSha256 ||
                !normalized.IdentityEvidence.SequenceEqual(value.IdentityEvidence))
                throw Invalid($"layers[{index}].semanticSha256");
            output.Add(normalized);
        }
        return output.MoveToImmutable();
    }

    private ImmutableArray<StatisticReconciliationRootCauseIdentityEvidence>
        NormalizeIdentityEvidence(
            IEnumerable<StatisticReconciliationRootCauseIdentityEvidence> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var normalized = values.Select((value, index) =>
        {
            ArgumentNullException.ThrowIfNull(value);
            var item = CreateIdentityEvidence(
                value.DeltaCode,
                value.IdentitySha256,
                value.ComparisonSha256);
            if (item.EvidenceSemanticSha256 != value.EvidenceSemanticSha256)
                throw Invalid($"identityEvidence[{index}].semanticSha256");
            return item;
        }).ToImmutableArray();
        var canonical = normalized
            .OrderBy(item => IdentityPriority(item.DeltaCode))
            .ThenBy(item => item.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(item => item.ComparisonSha256, StringComparer.Ordinal)
            .ThenBy(item => item.EvidenceSemanticSha256, StringComparer.Ordinal)
            .ToImmutableArray();
        if (!normalized.SequenceEqual(canonical) ||
            normalized.Select(item => item.IdentitySha256)
                .Distinct(StringComparer.Ordinal).Count() != normalized.Length ||
            normalized.Select(item => item.EvidenceSemanticSha256)
                .Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw Invalid("identityEvidence.canonicalOrder");
        return normalized;
    }

    private static int IdentityPriority(string deltaCode)
        => deltaCode == StatisticReconciliationIdentityDeltaCodes.MissingIdentity ? 0 : 1;

    private static string NativeClass(string layer)
        => layer switch
        {
            StatisticReconciliationRootCauseLayers.SourceMembership =>
                StatisticReconciliationRootCauseClasses.SourceMembership,
            StatisticReconciliationRootCauseLayers.DirectProjection =>
                StatisticReconciliationRootCauseClasses.Projection,
            StatisticReconciliationRootCauseLayers.Aggregate =>
                StatisticReconciliationRootCauseClasses.Aggregate,
            StatisticReconciliationRootCauseLayers.Basic or
                StatisticReconciliationRootCauseLayers.Advanced or
                StatisticReconciliationRootCauseLayers.Diff =>
                StatisticReconciliationRootCauseClasses.Snapshot,
            StatisticReconciliationRootCauseLayers.Api =>
                StatisticReconciliationRootCauseClasses.ApiTotal,
            StatisticReconciliationRootCauseLayers.Export =>
                StatisticReconciliationRootCauseClasses.Export,
            _ => throw Invalid("layer.nativeClass")
        };

    private static string PreRedactionProofSha(
        int ordinal,
        string layer,
        string comparisonBindingSha256,
        string expectedLayerSemanticSha256,
        string actualLayerSemanticSha256,
        int comparisonCount,
        int nonzeroCount,
        int missingCount,
        int extraCount,
        int redactedCount,
        int redactedNonzeroCount,
        ImmutableArray<StatisticReconciliationRootCauseIdentityEvidence> identityEvidence,
        string comparisonManifestSha256,
        string redactedComparisonManifestSha256)
        => HashFields(
            "P10_ROOT_CAUSE_PRE_REDACTION_PROOF_V1",
            I(ordinal),
            layer,
            comparisonBindingSha256,
            expectedLayerSemanticSha256,
            actualLayerSemanticSha256,
            I(comparisonCount),
            I(nonzeroCount),
            I(missingCount),
            I(extraCount),
            I(redactedCount),
            I(redactedNonzeroCount),
            HashFields(
                "P10_ROOT_CAUSE_PRE_REDACTION_IDENTITY_SET_V1",
                identityEvidence.Select(item => item.EvidenceSemanticSha256).ToArray()),
            comparisonManifestSha256,
            redactedComparisonManifestSha256);

    private static void RequireSha(string? value, string path)
    {
        if (value is null || value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9') and
                                             not (>= 'a' and <= 'f')))
            throw Invalid(path);
    }

    private static StatisticReconciliationTypedComparisonException Invalid(string path)
        => new(
            StatisticReconciliationRootCauseFailureCodes.EvidenceInvalid,
            $"Canonical root-cause evidence required at {path}.");

    private static string HashFields(string domain, params string[] fields)
    {
        var builder = new StringBuilder();
        AppendHashField(builder, domain);
        foreach (var field in fields)
            AppendHashField(builder, field);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendHashField(StringBuilder builder, string value)
    {
        builder.Append(Encoding.UTF8.GetByteCount(value)
                .ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);
    }

    private static string I(long value)
        => value.ToString(CultureInfo.InvariantCulture);
}

