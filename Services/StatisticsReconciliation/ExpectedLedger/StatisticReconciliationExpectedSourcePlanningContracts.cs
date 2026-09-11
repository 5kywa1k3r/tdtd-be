using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal static class StatisticReconciliationExpectedSourcePlanningLimits
{
    internal const int MaxLifecycleCandidates = 49_999;
    internal const int MaxContributionCandidates = 49_999;
}

public static class StatisticReconciliationExpectedLifecycleStatuses
{
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string Approved = "APPROVED";
    public const string Recalled = "RECALLED";
    public const string Returned = "RETURNED";
    public const string Terminated = "TERMINATED";
    public const string Invalidated = "INVALIDATED";
    public const string Superseded = "SUPERSEDED";

    public static readonly ImmutableHashSet<string> All =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            Draft,
            Submitted,
            Approved,
            Recalled,
            Returned,
            Terminated,
            Invalidated,
            Superseded);
}

public static class StatisticReconciliationExpectedRuntimeDispositions
{
    public const string Current = "CURRENT";
    public const string Terminated = "TERMINATED";
    public const string Invalidated = "INVALIDATED";
    public const string Superseded = "SUPERSEDED";

    public static readonly ImmutableHashSet<string> All =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            Current,
            Terminated,
            Invalidated,
            Superseded);
}

public static class StatisticReconciliationExpectedContributionPolicies
{
    public const string Exclude = "V_EXCLUDE";
    public const string Include = "V_INCLUDE";
}

public static class StatisticReconciliationExpectedSourceDecisionReasons
{
    public const string Included = "SOURCE_INCLUDED";
    public const string OutsideCurrentEpoch = "SOURCE_OUTSIDE_CURRENT_EPOCH";
    public const string DraftOrUnapproved = "SOURCE_DRAFT_OR_UNAPPROVED";
    public const string RecalledOrReturned = "SOURCE_RECALLED_OR_RETURNED";
    public const string Terminated = "SOURCE_TERMINATED";
    public const string Invalidated = "SOURCE_INVALIDATED";
    public const string Superseded = "SOURCE_SUPERSEDED";
    public const string NotEffective = "SOURCE_NOT_EFFECTIVE";
    public const string NotLocked = "SOURCE_NOT_LOCKED";
    public const string OlderApprovedRevision = "SOURCE_OLDER_APPROVED_REVISION";
    public const string ContributionDefaultExclude = "CONTRIBUTION_DEFAULT_EXCLUDE";
    public const string ContributionExcluded = "CONTRIBUTION_EXCLUDED";
}

public static class StatisticReconciliationExpectedSourcePlanningFailureReasons
{
    public const string SnapshotInvalid = "P10_EXPECTED_SOURCE_SNAPSHOT_INVALID";
    public const string LifecycleCandidateInvalid = "P10_EXPECTED_LIFECYCLE_CANDIDATE_INVALID";
    public const string LifecycleAmbiguous = "P10_EXPECTED_LIFECYCLE_AMBIGUOUS";
    public const string CurrentMemberMissing = "P10_EXPECTED_CURRENT_MEMBER_MISSING";
    public const string ContributionInvalid = "P10_EXPECTED_CONTRIBUTION_INVALID";
    public const string ContributionAmbiguous = "P10_EXPECTED_CONTRIBUTION_AMBIGUOUS";
}

public sealed record StatisticReconciliationExpectedAuthoritativeRuntimePin(
    string RuntimeKind,
    string? FlowTemplateVersionId,
    string? FlowPayloadSha256,
    string? FlowInstanceId,
    string? FlowBranchId,
    string? FlowStepId,
    string? FlowStepInstanceId,
    long? FlowStepRevision,
    int? FlowAttemptNo,
    string? ExecutionEpochId,
    int? ExecutionEpoch,
    string? CurrentExecutionEpochId,
    int? CurrentExecutionEpoch,
    long? ExecutionEpochRevision,
    bool? IsCanonicalEpoch,
    string? ApprovalCommandId,
    string? ApprovalEventKey,
    string? MappingReceiptId,
    string? MappingProvenanceId,
    string? MappingProvenanceSha256,
    string? MappingResultSemanticSha256,
    int? MappingResultPayloadRevision,
    string? MappingResultPayloadSha256,
    string? MappingFlowVersionId,
    int? MappingFlowVersionNo,
    string? MappingFlowPayloadSha256,
    bool? MappingLocked,
    string? ConfigVersionId,
    string? ConfigSha256,
    string MembershipSignatureSha256,
    string ContributionPolicy,
    string ContributionPolicySha256,
    string ContributionProvenanceId,
    string ContributionProvenanceSha256,
    string LifecycleOwnerSha256,
    string RuntimeSemanticSha256);
public sealed record ExpectedLifecycleRevisionCandidate
{
    internal ExpectedLifecycleRevisionCandidate(
        string stableSourceId,
        ExpectedSourceIdentityPin identity,
        string payloadDocumentId,
        string payloadJson,
        string lifecycleStatus,
        bool isEffective,
        bool isLocked,
        string runtimeDisposition,
        StatisticReconciliationExpectedAuthoritativeRuntimePin? runtimePin = null)
    {
        StableSourceId = stableSourceId;
        Identity = identity;
        PayloadDocumentId = payloadDocumentId;
        PayloadJson = payloadJson;
        LifecycleStatus = lifecycleStatus;
        IsEffective = isEffective;
        IsLocked = isLocked;
        RuntimeDisposition = runtimeDisposition;
        RuntimePin = runtimePin;
    }

    public string StableSourceId { get; }
    public ExpectedSourceIdentityPin Identity { get; }
    public string PayloadDocumentId { get; }
    public string PayloadJson { get; }
    public string LifecycleStatus { get; }
    public bool IsEffective { get; }
    public bool IsLocked { get; }
    public string RuntimeDisposition { get; }
    public StatisticReconciliationExpectedAuthoritativeRuntimePin? RuntimePin { get; }
}

public sealed record ExpectedContributionCandidate
{
    internal ExpectedContributionCandidate(
        string stableSourceId,
        string? policy,
        string versionId,
        long revision,
        string policySha256,
        string provenanceId,
        string provenanceSha256,
        bool isLocked)
    {
        StableSourceId = stableSourceId;
        Policy = policy;
        VersionId = versionId;
        Revision = revision;
        PolicySha256 = policySha256;
        ProvenanceId = provenanceId;
        ProvenanceSha256 = provenanceSha256;
        IsLocked = isLocked;
    }

    public string StableSourceId { get; }
    public string? Policy { get; }
    public string VersionId { get; }
    public long Revision { get; }
    public string PolicySha256 { get; }
    public string ProvenanceId { get; }
    public string ProvenanceSha256 { get; }
    public bool IsLocked { get; }
}

public sealed class ExpectedAuthoritativeSourceSnapshot
{
    internal ExpectedAuthoritativeSourceSnapshot(
        ExpectedLedgerCompilationContextPin contextPin,
        IEnumerable<ExpectedLifecycleRevisionCandidate>? lifecycleCandidates,
        CurrentEpochFlowMembership currentEpochMembership,
        LockedP8Configuration lockedP8Configuration,
        P5P7RuntimeMappingContributionLineage runtimeMappingContributionLineage,
        IEnumerable<ExpectedContributionCandidate>? contributionCandidates)
    {
        ContextPin = contextPin;
        LifecycleCandidates = StatisticReconciliationExpectedLedgerInputLimits.Snapshot(
            lifecycleCandidates,
            StatisticReconciliationExpectedSourcePlanningLimits.MaxLifecycleCandidates,
            StatisticReconciliationExpectedSourcePlanningFailureReasons.SnapshotInvalid,
            "$.lifecycleCandidates");
        CurrentEpochMembership = currentEpochMembership;
        LockedP8Configuration = lockedP8Configuration;
        RuntimeMappingContributionLineage = runtimeMappingContributionLineage;
        ContributionCandidates = StatisticReconciliationExpectedLedgerInputLimits.Snapshot(
            contributionCandidates,
            StatisticReconciliationExpectedSourcePlanningLimits.MaxContributionCandidates,
            StatisticReconciliationExpectedSourcePlanningFailureReasons.SnapshotInvalid,
            "$.contributionCandidates");
    }

    public ExpectedLedgerCompilationContextPin ContextPin { get; }
    public ImmutableArray<ExpectedLifecycleRevisionCandidate> LifecycleCandidates { get; }
    public CurrentEpochFlowMembership CurrentEpochMembership { get; }
    public LockedP8Configuration LockedP8Configuration { get; }
    public P5P7RuntimeMappingContributionLineage RuntimeMappingContributionLineage { get; }
    public ImmutableArray<ExpectedContributionCandidate> ContributionCandidates { get; }
}

public sealed record ExpectedSourceDecision(
    string StableSourceId,
    ExpectedSourceIdentityPin Identity,
    string PayloadDocumentId,
    string LifecycleStatus,
    bool IsEffective,
    bool IsLocked,
    string RuntimeDisposition,
    string Disposition,
    string ReasonCode,
    string ContributionPolicy,
    string ContributionVersionId,
    long ContributionRevision,
    string ContributionPolicySha256,
    string ContributionProvenanceId,
    string ContributionProvenanceSha256,
    string DecisionSemanticSha256,
    StatisticReconciliationExpectedAuthoritativeRuntimePin? RuntimePin = null);

public sealed record ExpectedIncludedSource(
    ExpectedLifecycleRevisionCandidate LifecycleRevision,
    ExpectedContributionCandidate Contribution,
    string SourceSemanticSha256);

public sealed class StatisticReconciliationExpectedSourcePlan
{
    internal StatisticReconciliationExpectedSourcePlan(
        StatisticReconciliationExpectedLedgerCompileInput compileInput,
        StatisticReconciliationExpectedLedgerBoundInputs boundInputs,
        IEnumerable<ExpectedLifecycleRevisionCandidate> approvedEffectiveSources,
        IEnumerable<ExpectedIncludedSource> includedSources,
        IEnumerable<ExpectedSourceDecision> sourceDecisions,
        string lifecycleSemanticSha256,
        string contributionSemanticSha256,
        string sourceSetSha256)
    {
        CompileInput = compileInput;
        BoundInputs = boundInputs;
        ApprovedEffectiveSources = approvedEffectiveSources.ToImmutableArray();
        IncludedSources = includedSources.ToImmutableArray();
        SourceDecisions = sourceDecisions.ToImmutableArray();
        LifecycleSemanticSha256 = lifecycleSemanticSha256;
        ContributionSemanticSha256 = contributionSemanticSha256;
        SourceSetSha256 = sourceSetSha256;
    }

    public StatisticReconciliationExpectedLedgerCompileInput CompileInput { get; }
    public StatisticReconciliationExpectedLedgerBoundInputs BoundInputs { get; }
    public ImmutableArray<ExpectedLifecycleRevisionCandidate> ApprovedEffectiveSources { get; }
    public ImmutableArray<ExpectedIncludedSource> IncludedSources { get; }
    public ImmutableArray<ExpectedSourceDecision> SourceDecisions { get; }
    public string LifecycleSemanticSha256 { get; }
    public string ContributionSemanticSha256 { get; }
    public string SourceSetSha256 { get; }
}

internal interface IStatisticReconciliationExpectedAuthoritativeSnapshotReader
{
    ValueTask<ExpectedAuthoritativeSourceSnapshot> ReadAsync(
        ExpectedLedgerCompilationContextPin requestedContext,
        CancellationToken cancellationToken);
}

internal interface IStatisticReconciliationExpectedSourcePlanner
{
    StatisticReconciliationExpectedSourcePlan Plan(
        ExpectedAuthoritativeSourceSnapshot snapshot);
}
