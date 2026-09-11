using System.Collections.Immutable;
using tdtd_be.Models;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualBasicModes
{
    internal const string DirectChildrenOrSelf =
        "DIRECT_CHILDREN_OR_SELF";
    internal const string DirectChildren = "DIRECT_CHILDREN";
    internal const string FlowBranch = "FLOW_BRANCH";
    internal const string FlowStep = "FLOW_STEP";
    internal const string FlowEffectivePath = "FLOW_EFFECTIVE_PATH";
    internal const string FlowFinal = "FLOW_FINAL";

    internal static readonly ImmutableArray<string> ExactSet =
    [
        DirectChildrenOrSelf,
        DirectChildren,
        FlowBranch,
        FlowStep,
        FlowEffectivePath,
        FlowFinal
    ];

    internal static bool IsFlow(string mode)
        => mode is FlowBranch or FlowStep or FlowEffectivePath or FlowFinal;
}

internal sealed record ActualBasicOwnerBoundary(
    string OwnerSnapshotId,
    string WorkId,
    string ScopeAssignmentId,
    string DynamicFormTemplateId,
    string SourceScopeMode,
    string? SourceFlowInstanceId,
    string? SourceFlowStepId,
    string? SourceFlowBranchId,
    string? SourceFlowEffectiveStatus,
    string RequestHash,
    string ConfigId,
    string ConfigVersionId,
    int ConfigVersionNo,
    long ConfigRevision,
    string ConfigSha256,
    ImmutableArray<string> ConfigDependencyPins,
    string CandidateChainId,
    string CandidatePromptId,
    int CandidateStage,
    string CandidateCatalogRawSha256,
    string CandidateCatalogSemanticSha256,
    string CandidateStageLockSha256);

internal interface IStatisticReconciliationActualBasicOwnerReader
{
    Task<WorkAssignmentBasicSummarySnapshot?> ReadSnapshotAsync(
        ActualBasicOwnerBoundary boundary,
        CancellationToken cancellationToken);
}

internal sealed record ActualBasicPayloadItemObservation(
    string Section,
    int OwnerOrdinal,
    string IdentityHint,
    string CanonicalJson,
    string SemanticSha256);

internal sealed record ActualBasicOwnerState(
    string RefreshStatus,
    bool IsDeleted,
    bool SnapshotDirty,
    bool HasRefreshedAtUtc,
    bool OwnerCompleteAndClean,
    bool FlowRuntimeRevisionBindingComplete,
    bool InnerMetaMatchesOuter,
    bool SourceAssignmentOrderCanonical,
    bool SourceReportOrderCanonical,
    bool SourceIdsUnique,
    string OwnerLifecycleSemanticSha256);

internal sealed record ActualBasicResultObservation(
    ActualBasicOwnerBoundary Boundary,
    string OwnerRunId,
    string RequestJson,
    string RequestRawSha256,
    string RequestCanonicalSha256,
    string SnapshotPayloadKind,
    int SnapshotPayloadVersion,
    string SnapshotJson,
    string SnapshotRawSha256,
    string SnapshotCanonicalSha256,
    ImmutableArray<string> SourceAssignmentIds,
    ImmutableArray<string> SourceReportIds,
    string SourceAssignmentSetSha256,
    string SourceReportSetSha256,
    string SourceSignatureSha256,
    ImmutableArray<ActualBasicPayloadItemObservation> ResultItems,
    ActualBasicOwnerState OwnerState,
    string CaptureSemanticSha256);
