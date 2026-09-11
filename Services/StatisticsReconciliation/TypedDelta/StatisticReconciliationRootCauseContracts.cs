using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.TypedDelta;

public static class StatisticReconciliationRootCauseClasses
{
    public const string SourceMembership = "SOURCE_MEMBERSHIP";
    public const string Projection = "PROJECTION";
    public const string Aggregate = "AGGREGATE";
    public const string Snapshot = "SNAPSHOT";
    public const string ApiTotal = "API_TOTAL";
    public const string Export = "EXPORT";
    public const string Freshness = "FRESHNESS";
    public const string Permission = "PERMISSION";
    public const string MissingIdentity = "MISSING_IDENTITY";
    public const string ExtraIdentity = "EXTRA_IDENTITY";
    public const string Unknown = "UNKNOWN";

    public static readonly ImmutableArray<string> Ordered =
    [
        SourceMembership,
        Projection,
        Aggregate,
        Snapshot,
        ApiTotal,
        Export,
        Freshness,
        Permission,
        MissingIdentity,
        ExtraIdentity,
        Unknown
    ];
}

public static class StatisticReconciliationRootCauseLayers
{
    public const string SourceMembership = "SOURCE_MEMBERSHIP";
    public const string DirectProjection = "DIRECT_PROJECTION";
    public const string Aggregate = "AGGREGATE";
    public const string Basic = "BASIC";
    public const string Advanced = "ADVANCED";
    public const string Diff = "DIFF";
    public const string Api = "API";
    public const string Export = "EXPORT";

    public static readonly ImmutableArray<string> Ordered =
    [
        SourceMembership,
        DirectProjection,
        Aggregate,
        Basic,
        Advanced,
        Diff,
        Api,
        Export
    ];
}

public static class StatisticReconciliationRootCauseClassificationStates
{
    public const string NoDivergence = "NO_DIVERGENCE";
    public const string Proven = "PROVEN";
    public const string Unknown = "UNKNOWN";
    public const string InsufficientEvidence = "INSUFFICIENT_EVIDENCE";
}

public static class StatisticReconciliationRootCausePermissionStates
{
    public const string Authorized = "AUTHORIZED";
    public const string Denied = "DENIED";
    public const string Ambiguous = "AMBIGUOUS";
}

public static class StatisticReconciliationRootCauseLayerDeltaStates
{
    public const string Zero = "ZERO_DELTA";
    public const string Nonzero = "NONZERO_DELTA";
    public const string Undetermined = "UNDETERMINED";
}

public static class StatisticReconciliationRootCauseAttributionStates
{
    public const string NotRequired = "NOT_REQUIRED";
    public const string Proven = "PROVEN";
    public const string Ambiguous = "AMBIGUOUS";
}

public static class StatisticReconciliationRootCauseFailureCodes
{
    public const string EvidenceInvalid = "P10_DELTA_ROOT_CAUSE_EVIDENCE_INVALID";
}

public sealed record StatisticReconciliationRootCausePermissionEvidence(
    string State,
    string AuthorizationSnapshotSha256,
    string EvidenceSemanticSha256);

public sealed record StatisticReconciliationRootCauseIdentityEvidence(
    string DeltaCode,
    string IdentitySha256,
    string ComparisonSha256,
    string EvidenceSemanticSha256);

public sealed record StatisticReconciliationRootCausePreRedactionPair(
    StatisticReconciliationStatefulObservation? Expected,
    StatisticReconciliationStatefulObservation? Actual,
    bool RedactedOutward);

public sealed class StatisticReconciliationRootCausePreRedactionProof
{
    internal StatisticReconciliationRootCausePreRedactionProof(
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
        string redactedComparisonManifestSha256,
        string proofSemanticSha256)
    {
        Ordinal = ordinal;
        Layer = layer;
        ComparisonBindingSha256 = comparisonBindingSha256;
        ExpectedLayerSemanticSha256 = expectedLayerSemanticSha256;
        ActualLayerSemanticSha256 = actualLayerSemanticSha256;
        ComparisonCount = comparisonCount;
        NonzeroCount = nonzeroCount;
        MissingCount = missingCount;
        ExtraCount = extraCount;
        RedactedCount = redactedCount;
        RedactedNonzeroCount = redactedNonzeroCount;
        IdentityEvidence = identityEvidence;
        ComparisonManifestSha256 = comparisonManifestSha256;
        RedactedComparisonManifestSha256 = redactedComparisonManifestSha256;
        ProofSemanticSha256 = proofSemanticSha256;
    }

    public int Ordinal { get; }
    public string Layer { get; }
    public string ComparisonBindingSha256 { get; }
    public string ExpectedLayerSemanticSha256 { get; }
    public string ActualLayerSemanticSha256 { get; }
    public int ComparisonCount { get; }
    public int NonzeroCount { get; }
    public int MissingCount { get; }
    public int ExtraCount { get; }
    public int RedactedCount { get; }
    public int RedactedNonzeroCount { get; }
    public ImmutableArray<StatisticReconciliationRootCauseIdentityEvidence>
        IdentityEvidence { get; }
    public string ComparisonManifestSha256 { get; }
    public string RedactedComparisonManifestSha256 { get; }
    public string ProofSemanticSha256 { get; }
}

public sealed record StatisticReconciliationRootCauseLayerEvidence(
    int Ordinal,
    string Layer,
    string ComparisonBindingSha256,
    bool EvidenceComplete,
    string ExpectedLayerSemanticSha256,
    string ActualLayerSemanticSha256,
    string DeltaManifestSha256,
    int ComparisonCount,
    int NonzeroCount,
    int MissingCount,
    int ExtraCount,
    int RedactedCount,
    string DeltaState,
    string AttributionState,
    string? NativeAttributionSha256,
    StatisticReconciliationRootCausePreRedactionProof? TrustedPreRedactionProof,
    ImmutableArray<StatisticReconciliationRootCauseIdentityEvidence> IdentityEvidence,
    string LayerEvidenceSemanticSha256);

public sealed record StatisticReconciliationRootCauseRequest(
    string ComparisonBindingSha256,
    StatisticReconciliationRootCausePermissionEvidence Permission,
    StatisticReconciliationFreshnessAssessment? Freshness,
    ImmutableArray<StatisticReconciliationRootCauseLayerEvidence> Layers);

public sealed record StatisticReconciliationRootCauseClassification(
    string State,
    string? PrimaryClass,
    string? EarliestLayer,
    int? EarliestLayerOrdinal,
    string? SupportingEvidenceSha256,
    string AuthorizationState,
    string? FreshnessAssessmentSha256,
    bool CauseRequired,
    bool Provable,
    bool UnknownBlocksCloseout,
    bool MissingIdentityRemediationRequired,
    bool ExtraIdentityRemediationRequired,
    ImmutableArray<StatisticReconciliationRootCauseLayerEvidence> OrderedLayerEvidence,
    string EvidenceSetSha256,
    string ClassificationSha256);

