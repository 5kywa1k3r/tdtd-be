using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation.TypedDelta;

public static class StatisticReconciliationFinalVerdicts
{
    public const string Matched = "MATCHED";
    public const string Mismatched = "MISMATCHED";
    public const string Failed = "FAILED";
}

public static class StatisticReconciliationFinalVerdictFailureKinds
{
    public const string None = "NONE";
    public const string MissingEvidence = "MISSING_EVIDENCE";
    public const string Timeout = "TIMEOUT";
    public const string Internal = "INTERNAL";
}

public static class StatisticReconciliationRemediationReferenceTypes
{
    public const string ImplementationPatch = "IMPLEMENTATION_PATCH";
    public const string AuthorizedP9Operation = "AUTHORIZED_P9_OPERATION";
}

public static class StatisticReconciliationFinalVerdictFailureCodes
{
    public const string EvidenceInvalid = "P10_DELTA_FINAL_VERDICT_EVIDENCE_INVALID";
    public const string PermissionDenied = "P10_DELTA_FINAL_VERDICT_PERMISSION_DENIED";
    public const string ReplayMismatch = "P10_DELTA_FINAL_VERDICT_REPLAY_MISMATCH";
    public const string SupersessionInvalid = "P10_DELTA_FINAL_VERDICT_SUPERSESSION_INVALID";
    public const string PersistenceInvalid = "P10_DELTA_FINAL_VERDICT_PERSISTENCE_INVALID";
}

public sealed record StatisticReconciliationFinalVerdictRequest(
    string ReconciliationId,
    string ComparisonBindingSha256,
    string ExpectedGenerationId,
    string ExpectedGenerationSha256,
    string ActualGenerationId,
    string ActualGenerationSha256,
    string DeltaManifestSha256,
    StatisticReconciliationRootCausePermissionEvidence Permission,
    StatisticReconciliationRootCauseRequest? RootCauseRequest,
    string FailureKind,
    string? FailureEvidenceSha256);

public sealed record StatisticReconciliationFinalVerdictDecision(
    bool PublicationAllowed,
    string ReconciliationId,
    string ComparisonBindingSha256,
    string ExpectedGenerationId,
    string ExpectedGenerationSha256,
    string ActualGenerationId,
    string ActualGenerationSha256,
    string DeltaManifestSha256,
    string AuthorizationEvidenceSha256,
    string? FreshnessAssessmentSha256,
    string? RootCauseClassificationSha256,
    string? RootCauseClass,
    string FailureKind,
    string? FailureEvidenceSha256,
    string Verdict,
    bool CompleteEvidence,
    bool AllRequiredLayersZero,
    bool MissingOrExtraIdentity,
    bool UnknownBlocksCloseout,
    bool CloseoutAllowed,
    bool Signable,
    string DecisionSemanticSha256);

public sealed record StatisticReconciliationVerdictRemediationRequest(
    string ReferenceType,
    string ReferenceId,
    string ReferenceSha256,
    string BeforeSourceSha256,
    string AfterSourceSha256,
    string BeforeResultSha256,
    string AfterResultSha256,
    string RecheckGenerationId,
    string RecheckGenerationSha256);

public sealed class StatisticReconciliationFinalVerdictException : Exception
{
    public StatisticReconciliationFinalVerdictException(string code, string detail)
        : base($"{code}:{detail}") => Code = code;

    public string Code { get; }
}

public interface IStatisticReconciliationReviewBackend
{
    Task<StatisticReconciliationReview?> ReadAsync(
        string reconciliationId,
        string verdictGenerationId,
        CancellationToken ct = default);

    Task<IReadOnlyList<StatisticReconciliationReview>> ReadLineageAsync(
        string reconciliationId,
        CancellationToken ct = default);

    Task<bool> TryAppendAsync(
        StatisticReconciliationReview review,
        CancellationToken ct = default);
}
