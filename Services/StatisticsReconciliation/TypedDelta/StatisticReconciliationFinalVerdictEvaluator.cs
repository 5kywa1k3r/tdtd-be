using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace tdtd_be.Services.StatisticsReconciliation.TypedDelta;

public sealed class StatisticReconciliationFinalVerdictEvaluator
{
    private readonly StatisticReconciliationRootCauseClassifier _rootCause;

    public StatisticReconciliationFinalVerdictEvaluator(
        StatisticReconciliationRootCauseClassifier? rootCause = null)
        => _rootCause = rootCause ?? new StatisticReconciliationRootCauseClassifier();

    public StatisticReconciliationFinalVerdictDecision Evaluate(
        StatisticReconciliationFinalVerdictRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var permission = NormalizePermission(request.Permission);
        if (permission.State != StatisticReconciliationRootCausePermissionStates.Authorized)
        {
            if (request.RootCauseRequest is not null)
                throw Invalid("permissionMustPrecedeEvidence");
            var opaque = _rootCause.Classify(new StatisticReconciliationRootCauseRequest(
                request.ComparisonBindingSha256,
                permission,
                null,
                []));
            var blindedBinding = HashFields(
                "P10_FINAL_VERDICT_PERMISSION_BINDING_V1",
                permission.EvidenceSemanticSha256);
            return Decision(
                false,
                "~",
                blindedBinding,
                "~",
                "~",
                "~",
                "~",
                "~",
                permission.EvidenceSemanticSha256,
                null,
                opaque.ClassificationSha256,
                StatisticReconciliationRootCauseClasses.Permission,
                StatisticReconciliationFinalVerdictFailureKinds.MissingEvidence,
                null,
                StatisticReconciliationFinalVerdicts.Failed,
                false,
                false,
                false,
                true,
                false,
                false);
        }

        RequireReconciliationId(request.ReconciliationId);
        RequireSha(request.ComparisonBindingSha256, "comparisonBindingSha256");
        RequireSha(request.ExpectedGenerationId, "expectedGenerationId");
        RequireSha(request.ExpectedGenerationSha256, "expectedGenerationSha256");
        RequireSha(request.ActualGenerationId, "actualGenerationId");
        RequireSha(request.ActualGenerationSha256, "actualGenerationSha256");
        RequireSha(request.DeltaManifestSha256, "deltaManifestSha256");
        RequireFailureKind(request.FailureKind);

        if (request.FailureKind != StatisticReconciliationFinalVerdictFailureKinds.None)
        {
            if (request.RootCauseRequest is not null)
                throw Invalid("failureMustPrecedeRootCause");
            RequireSha(request.FailureEvidenceSha256, "failureEvidenceSha256");
            return Decision(
                true,
                request.ReconciliationId,
                request.ComparisonBindingSha256,
                request.ExpectedGenerationId,
                request.ExpectedGenerationSha256,
                request.ActualGenerationId,
                request.ActualGenerationSha256,
                request.DeltaManifestSha256,
                permission.EvidenceSemanticSha256,
                null,
                null,
                StatisticReconciliationRootCauseClasses.Unknown,
                request.FailureKind,
                request.FailureEvidenceSha256,
                StatisticReconciliationFinalVerdicts.Failed,
                false,
                false,
                false,
                true,
                false,
                false);
        }

        if (request.FailureEvidenceSha256 is not null || request.RootCauseRequest is null)
            throw Invalid("rootCauseRequired");
        var rootRequest = request.RootCauseRequest;
        if (rootRequest.ComparisonBindingSha256 != request.ComparisonBindingSha256 ||
            rootRequest.Permission != permission)
            throw Invalid("rootCauseBinding");
        var root = _rootCause.Classify(rootRequest);

        var noDivergence = root.State ==
            StatisticReconciliationRootCauseClassificationStates.NoDivergence;
        var completeLayers = root.OrderedLayerEvidence.Length == 8 &&
            root.OrderedLayerEvidence.All(layer => layer.EvidenceComplete);
        var allZero = completeLayers && root.OrderedLayerEvidence.All(layer =>
            layer.DeltaState == StatisticReconciliationRootCauseLayerDeltaStates.Zero &&
            layer.NonzeroCount == 0 && layer.MissingCount == 0 && layer.ExtraCount == 0);
        var coherentFresh = root.FreshnessAssessmentSha256 is not null &&
            root.PrimaryClass != StatisticReconciliationRootCauseClasses.Freshness;
        var missingOrExtra = root.PrimaryClass is
            StatisticReconciliationRootCauseClasses.MissingIdentity or
            StatisticReconciliationRootCauseClasses.ExtraIdentity;

        string verdict;
        if (noDivergence && completeLayers && allZero && coherentFresh)
            verdict = StatisticReconciliationFinalVerdicts.Matched;
        else if (root.State == StatisticReconciliationRootCauseClassificationStates.Proven &&
                 root.Provable && coherentFresh && completeLayers &&
                 root.PrimaryClass is not (
                     StatisticReconciliationRootCauseClasses.Permission or
                     StatisticReconciliationRootCauseClasses.Freshness or
                     StatisticReconciliationRootCauseClasses.Unknown))
            verdict = StatisticReconciliationFinalVerdicts.Mismatched;
        else
            verdict = StatisticReconciliationFinalVerdicts.Failed;

        var unknownBlocks = root.UnknownBlocksCloseout ||
            root.PrimaryClass == StatisticReconciliationRootCauseClasses.Unknown;
        var closeoutAllowed = verdict == StatisticReconciliationFinalVerdicts.Matched &&
                              !unknownBlocks && !missingOrExtra;
        return Decision(
            true,
            request.ReconciliationId,
            request.ComparisonBindingSha256,
            request.ExpectedGenerationId,
            request.ExpectedGenerationSha256,
            request.ActualGenerationId,
            request.ActualGenerationSha256,
            request.DeltaManifestSha256,
            permission.EvidenceSemanticSha256,
            root.FreshnessAssessmentSha256,
            root.ClassificationSha256,
            root.PrimaryClass,
            request.FailureKind,
            null,
            verdict,
            completeLayers,
            allZero,
            missingOrExtra,
            unknownBlocks,
            closeoutAllowed,
            closeoutAllowed);
    }

    private StatisticReconciliationRootCausePermissionEvidence NormalizePermission(
        StatisticReconciliationRootCausePermissionEvidence value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = _rootCause.CreatePermissionEvidence(
            value.State,
            value.AuthorizationSnapshotSha256);
        if (normalized.EvidenceSemanticSha256 != value.EvidenceSemanticSha256)
            throw Invalid("permissionEvidence");
        return normalized;
    }

    private static StatisticReconciliationFinalVerdictDecision Decision(
        bool publicationAllowed,
        string reconciliationId,
        string comparisonBindingSha256,
        string expectedGenerationId,
        string expectedGenerationSha256,
        string actualGenerationId,
        string actualGenerationSha256,
        string deltaManifestSha256,
        string authorizationEvidenceSha256,
        string? freshnessAssessmentSha256,
        string? rootCauseClassificationSha256,
        string? rootCauseClass,
        string failureKind,
        string? failureEvidenceSha256,
        string verdict,
        bool completeEvidence,
        bool allRequiredLayersZero,
        bool missingOrExtraIdentity,
        bool unknownBlocksCloseout,
        bool closeoutAllowed,
        bool signable)
    {
        var semantic = HashFields(
            "P10_FINAL_VERDICT_DECISION_V1",
            publicationAllowed ? "1" : "0",
            reconciliationId,
            comparisonBindingSha256,
            expectedGenerationId,
            expectedGenerationSha256,
            actualGenerationId,
            actualGenerationSha256,
            deltaManifestSha256,
            authorizationEvidenceSha256,
            freshnessAssessmentSha256 ?? "~",
            rootCauseClassificationSha256 ?? "~",
            rootCauseClass ?? "~",
            failureKind,
            failureEvidenceSha256 ?? "~",
            verdict,
            completeEvidence ? "1" : "0",
            allRequiredLayersZero ? "1" : "0",
            missingOrExtraIdentity ? "1" : "0",
            unknownBlocksCloseout ? "1" : "0",
            closeoutAllowed ? "1" : "0",
            signable ? "1" : "0");
        return new StatisticReconciliationFinalVerdictDecision(
            publicationAllowed,
            reconciliationId,
            comparisonBindingSha256,
            expectedGenerationId,
            expectedGenerationSha256,
            actualGenerationId,
            actualGenerationSha256,
            deltaManifestSha256,
            authorizationEvidenceSha256,
            freshnessAssessmentSha256,
            rootCauseClassificationSha256,
            rootCauseClass,
            failureKind,
            failureEvidenceSha256,
            verdict,
            completeEvidence,
            allRequiredLayersZero,
            missingOrExtraIdentity,
            unknownBlocksCloseout,
            closeoutAllowed,
            signable,
            semantic);
    }

    private static void RequireFailureKind(string value)
    {
        if (value is not (
            StatisticReconciliationFinalVerdictFailureKinds.None or
            StatisticReconciliationFinalVerdictFailureKinds.MissingEvidence or
            StatisticReconciliationFinalVerdictFailureKinds.Timeout or
            StatisticReconciliationFinalVerdictFailureKinds.Internal))
            throw Invalid("failureKind");
    }

    private static void RequireReconciliationId(string value)
    {
        if (value.Length != 24 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Invalid("reconciliationId");
    }

    internal static void RequireSha(string? value, string field)
    {
        if (value is null || value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Invalid(field);
    }

    internal static string HashFields(string domain, params string[] fields)
    {
        var value = string.Join("", new[] { domain }.Concat(fields)
            .Select(field => Encoding.UTF8.GetByteCount(field)
                .ToString(CultureInfo.InvariantCulture) + ":" + field));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    internal static StatisticReconciliationFinalVerdictException Invalid(string detail)
        => new(StatisticReconciliationFinalVerdictFailureCodes.EvidenceInvalid, detail);
}
