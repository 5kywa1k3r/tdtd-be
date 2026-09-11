using System.Collections.Immutable;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;

public static class StatisticReconciliationLifecycleStatuses
{
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string Approved = "APPROVED";
    public const string Recalled = "RECALLED";
    public const string Returned = "RETURNED";
    public const string Terminated = "TERMINATED";
    public const string Invalidated = "INVALIDATED";

    public static readonly ImmutableHashSet<string> All =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            Draft, Submitted, Approved, Recalled, Returned, Terminated,
            Invalidated);
}

public static class StatisticReconciliationLifecyclePolicies
{
    public const string Exclude = "V_EXCLUDE";
    public const string Include = "V_INCLUDE";
}

public static class StatisticReconciliationLifecycleEventKinds
{
    public const string Observe = "OBSERVE";
    public const string Approve = "APPROVE";
    public const string Recall = "RECALL";
    public const string Return = "RETURN";
    public const string Terminate = "TERMINATE";
    public const string Invalidate = "INVALIDATE";
    public const string Rollback = "ROLLBACK";
    public const string Restart = "RESTART";
    public const string Rebuild = "REBUILD";
    public const string Retry = "RETRY";

    public static readonly ImmutableHashSet<string> All =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            Observe, Approve, Recall, Return, Terminate, Invalidate,
            Rollback, Restart, Rebuild, Retry);
}

public static class StatisticReconciliationLifecycleOutcomes
{
    public const string Matched = "MATCHED";
    public const string Mismatched = "MISMATCHED";
    public const string Stale = "STALE";
}

public static class StatisticReconciliationLifecycleRootCauses
{
    public const string SourceMembership = "SOURCE_MEMBERSHIP";
    public const string Projection = "PROJECTION";
    public const string Aggregate = "AGGREGATE";
    public const string Snapshot = "SNAPSHOT";
    public const string Freshness = "FRESHNESS";
    public const string MissingIdentity = "MISSING_IDENTITY";
    public const string ExtraIdentity = "EXTRA_IDENTITY";
    public const string Unknown = "UNKNOWN";
}

public static class StatisticReconciliationLifecycleFailureCodes
{
    public const string EvidenceInvalid = "P10_LIFECYCLE_EVIDENCE_INVALID";
    public const string ReplayMismatch = "P10_LIFECYCLE_REPLAY_MISMATCH";
    public const string CasStale = "P10_LIFECYCLE_CAS_STALE";
    public const string FenceStale = "P10_LIFECYCLE_FENCE_STALE";
    public const string ObservationConflict = "P10_LIFECYCLE_OBSERVATION_CONFLICT";
    public const string Forbidden = "P10_LIFECYCLE_FORBIDDEN";
}

public sealed class StatisticReconciliationLifecycleException : Exception
{
    public StatisticReconciliationLifecycleException(string code, string detail)
        : base($"{code}:{detail}") => Code = code;

    public string Code { get; }
}

public sealed record StatisticReconciliationLifecycleSourceState(
    string ObservationId,
    long Sequence,
    string EventKind,
    string StableSourceId,
    string ReportId,
    string BranchId,
    string StepId,
    string EpochId,
    long Epoch,
    string CurrentEpochId,
    string LifecycleStatus,
    bool IsEffective,
    string ContributionPolicy,
    string? MappingVersionId,
    long? MappingRevision,
    bool MappingVersionLocked,
    string? MappingSha256,
    string? ProvenanceId,
    string? ProvenanceSha256,
    string SourceGenerationId,
    string SourceGenerationSha256,
    string CanonicalValue,
    string SemanticSha256);

public sealed record StatisticReconciliationLifecycleActualContribution(
    string ObservationId,
    string SourceObservationId,
    string StableSourceId,
    string GenerationId,
    string GenerationSha256,
    string? SupersedesGenerationId,
    string? PriorEffectiveGenerationSha256,
    string CanonicalContribution,
    string CanonicalDelta,
    string EpochId,
    string ContributionPolicy,
    string? MappingVersionId,
    long? MappingRevision,
    string? MappingSha256,
    string? ProvenanceId,
    string? ProvenanceSha256,
    bool EvidenceComplete,
    string SemanticSha256);

public sealed record StatisticReconciliationLifecycleRequest(
    string ReconciliationId,
    string ComparisonBindingSha256,
    ImmutableArray<StatisticReconciliationLifecycleSourceState> Sources,
    ImmutableArray<StatisticReconciliationLifecycleActualContribution> Actuals);

public sealed record StatisticReconciliationLifecycleStepResult(
    string SourceObservationId,
    string StableSourceId,
    long Sequence,
    string EventKind,
    string LifecycleStatus,
    string ContributionPolicy,
    string EpochId,
    string ExpectedBefore,
    string ExpectedAfter,
    string ExpectedDelta,
    string? ActualAfter,
    string? ActualDelta,
    bool Exact,
    string? RootCause,
    string StepSemanticSha256);

public sealed record StatisticReconciliationLifecycleResult(
    string ReconciliationId,
    string ComparisonBindingSha256,
    string Outcome,
    string? RootCause,
    bool EvidenceComplete,
    int SourceCount,
    int ActualCount,
    int MatchedCount,
    int MissingCount,
    int ExtraCount,
    ImmutableArray<StatisticReconciliationLifecycleStepResult> Steps,
    string RequestSemanticSha256,
    string ResultSemanticSha256);

public static class StatisticReconciliationLifecycleRecordKinds
{
    public const string Source = "SOURCE";
    public const string Actual = "ACTUAL";
    public const string Result = "RESULT";
}

[BsonIgnoreExtraElements]
public sealed record StatisticReconciliationLifecycleStoredObservation(
    [property: BsonElement("reconciliationId")] string ReconciliationId,
    [property: BsonElement("generationId")] string GenerationId,
    [property: BsonElement("recordKind")] string RecordKind,
    [property: BsonElement("observationId")] string ObservationId,
    [property: BsonElement("semanticSha256")] string SemanticSha256,
    [property: BsonElement("payloadSha256")] string PayloadSha256);

public sealed record StatisticReconciliationLifecycleAppendResult(
    int AppendedCount,
    int ReplayCount,
    int TotalCount,
    string ManifestSha256);

public sealed record StatisticReconciliationLifecycleRecheckCommand(
    string ReconciliationId,
    string CommandId,
    string RequestSha256,
    long ExpectedStateRevision,
    string ExpectedStateSha256,
    long FencingToken,
    string WorkerId,
    bool AuthorizationGranted);

public sealed record StatisticReconciliationLifecycleRecheckReceipt(
    string ReceiptId,
    string ReconciliationId,
    string CommandId,
    string RequestSha256,
    long AcceptedStateRevision,
    string AcceptedStateSha256,
    string GenerationId,
    string GenerationSha256,
    string ResultSemanticSha256,
    long FencingToken,
    string ReceiptSha256);

public sealed record StatisticReconciliationLifecycleRecheckState(
    string ReconciliationId,
    long StateRevision,
    string StateSha256,
    long HighestFencingToken,
    string? CurrentGenerationId,
    string? CurrentGenerationSha256,
    int PublicationCount,
    int ReceiptCount);

public sealed record StatisticReconciliationLifecycleRecheckResult(
    bool IsReplay,
    StatisticReconciliationLifecycleRecheckReceipt Receipt,
    StatisticReconciliationLifecycleRecheckState State,
    StatisticReconciliationLifecycleResult Result,
    StatisticReconciliationLifecycleAppendResult AppendResult);
