namespace tdtd_be.DTOs.DynamicFlows;

public sealed class DynamicFlowRuntimeInstanceListQuery
{
    public string? State { get; set; }
    public int Limit { get; set; } = 25;
    public string? Cursor { get; set; }
}

public sealed class DynamicFlowRuntimePageQuery
{
    public int Limit { get; set; } = 25;
    public string? Cursor { get; set; }
}

public sealed class DynamicFlowRuntimeInboxQuery
{
    public string State { get; set; } = "ASSIGNED";
    public int Limit { get; set; } = 25;
    public string? Cursor { get; set; }
}

public sealed class DynamicFlowRuntimePageResponse<T>
{
    public List<T> Items { get; set; } = new();
    public long Total { get; set; }
    public int Limit { get; set; }
    public bool HasMore { get; set; }
    public string? NextCursor { get; set; }
}

public sealed class DynamicFlowRuntimeInstanceListRow
{
    public string WorkId { get; set; } = string.Empty;
    public string FlowInstanceId { get; set; } = string.Empty;
    public string FlowTemplateId { get; set; } = string.Empty;
    public string FlowTemplateVersionId { get; set; } = string.Empty;
    public int FlowTemplateVersionNo { get; set; }
    public string ArchetypeId { get; set; } = string.Empty;
    public string DefinitionRevision { get; set; } = string.Empty;
    public string TopologySnapshotHash { get; set; } = string.Empty;
    public int ExecutionEpoch { get; set; }
    public int? FinalizedExecutionEpoch { get; set; }
    public DateTime? FinalizedAtUtc { get; set; }
    public string EntryFlowStepId { get; set; } = string.Empty;
    public string? ParentInstanceId { get; set; }
    public string? ParentStepInstanceId { get; set; }
    public string? RootInstanceId { get; set; }
    public List<string> AncestryPath { get; set; } = new();
    public string PeriodKey { get; set; } = string.Empty;
    public string ScheduleIdentityHash { get; set; } = string.Empty;
    public string? PeriodicScheduleId { get; set; }
    public string? PeriodicOccurrenceId { get; set; }
    public string? TimeZoneId { get; set; }
    public string? SchedulePolicyVersion { get; set; }
    public string State { get; set; } = string.Empty;
    public long Revision { get; set; }
    public long RuntimeRecoveryEpoch { get; set; }
    public string RevisionToken { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
    public List<string> VisibilityScopes { get; set; } = new();
    public DynamicFlowRuntimeCapabilitiesDto Capabilities { get; set; } = new();
    public DynamicFlowRuntimeRecoveryStatusDto Recovery { get; set; } = new();
}

public sealed class DynamicFlowRuntimeInstanceOverview
{
    public string WorkId { get; set; } = string.Empty;
    public string FlowInstanceId { get; set; } = string.Empty;
    public string FlowTemplateId { get; set; } = string.Empty;
    public string FlowTemplateVersionId { get; set; } = string.Empty;
    public int FlowTemplateVersionNo { get; set; }
    public string ArchetypeId { get; set; } = string.Empty;
    public string DefinitionRevision { get; set; } = string.Empty;
    public string TopologySnapshotHash { get; set; } = string.Empty;
    public int ExecutionEpoch { get; set; }
    public int? FinalizedExecutionEpoch { get; set; }
    public DateTime? FinalizedAtUtc { get; set; }
    public string? FinalizedByUserId { get; set; }
    public string? FinalizedByEventId { get; set; }
    public string EntryFlowStepId { get; set; } = string.Empty;
    public string? ParentInstanceId { get; set; }
    public string? ParentStepInstanceId { get; set; }
    public string? RootInstanceId { get; set; }
    public List<string> AncestryPath { get; set; } = new();
    public string PeriodKey { get; set; } = string.Empty;
    public string ScheduleIdentityHash { get; set; } = string.Empty;
    public string? PeriodicScheduleId { get; set; }
    public string? PeriodicOccurrenceId { get; set; }
    public string? TimeZoneId { get; set; }
    public string? SchedulePolicyVersion { get; set; }
    public string ParticipantSnapshotId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public long Revision { get; set; }
    public long RuntimeRecoveryEpoch { get; set; }
    public string RevisionToken { get; set; } = string.Empty;
    public long VisibleStepCount { get; set; }
    public List<DynamicFlowRuntimeGatewayRow> Gateways { get; set; } = new();
    public List<DynamicFlowExecutionEpochDto> Epochs { get; set; } = new();
    public DateTime UpdatedAtUtc { get; set; }
    public List<string> VisibilityScopes { get; set; } = new();
    public DynamicFlowRuntimeCapabilitiesDto Capabilities { get; set; } = new();
    public DynamicFlowRuntimeRecoveryStatusDto Recovery { get; set; } = new();
}

public sealed class DynamicFlowExecutionEpochDto
{
    public string EpochId { get; set; } = string.Empty;
    public int ExecutionEpoch { get; set; }
    public string State { get; set; } = string.Empty;
    public string CheckpointNodeId { get; set; } = string.Empty;
    public bool IsCanonical { get; set; }
    public string OpenedByCommandId { get; set; } = string.Empty;
    public string? ClosedByCommandId { get; set; }
    public string? TerminalEventId { get; set; }
    public int? ReplacedByExecutionEpoch { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
}

public sealed class DynamicFlowRuntimeGatewayRow
{
    public string GatewayNodeId { get; set; } = string.Empty;
    public string GatewayInstanceId { get; set; } = string.Empty;
    public int GatewayVersion { get; set; }
    public string GatewayKind { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public bool IsCanonicalEpoch { get; set; }
    public string? InvalidatedByFlowEventId { get; set; }
    public DateTime? InvalidatedAtUtc { get; set; }
    public long Revision { get; set; }
    public string DownstreamNodeId { get; set; } = string.Empty;
    public List<string> ExpectedContributionIds { get; set; } = new();
    public List<string> ArrivedContributionIds { get; set; } = new();
    public List<string> MissingContributionIds { get; set; } = new();
    public int RequiredContributionCount { get; set; }
    public List<string> CancelledContributionIds { get; set; } = new();
    public List<string> LateContributionIds { get; set; } = new();
    public string? WinnerContributionId { get; set; }
    public string? InputSnapshotHash { get; set; }
    public string? EvaluatorVersion { get; set; }
    public string? SelectedEdgeId { get; set; }
    public string? DecisionReasonCode { get; set; }
    public DateTime? ReleasedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class DynamicFlowRuntimeStepRow
{
    public string WorkId { get; set; } = string.Empty;
    public string FlowInstanceId { get; set; } = string.Empty;
    public string FlowStepDefinitionId { get; set; } = string.Empty;
    public string FlowStepCode { get; set; } = string.Empty;
    public int StepOrder { get; set; }
    public int ExecutionEpoch { get; set; }
    public string DefinitionRevision { get; set; } = string.Empty;
    public string StepInstanceId { get; set; } = string.Empty;
    public string BranchId { get; set; } = string.Empty;
    public string? ParentBranchId { get; set; }
    public int AttemptNo { get; set; }
    public int ReviewCycleNo { get; set; }
    public int? MaxReviewCycles { get; set; }
    public string? PreviousAttemptStepInstanceId { get; set; }
    public string? PreviousAttemptAssignmentId { get; set; }
    public string? SupersededByStepInstanceId { get; set; }
    public bool IsCanonicalAttempt { get; set; }
    public string? ActivatedByTransitionId { get; set; }
    public string? GatewayInstanceId { get; set; }
    public int? GatewayVersion { get; set; }
    public string? ContributionId { get; set; }
    public List<string> NextNodeIds { get; set; } = new();
    public bool IsTerminalNode { get; set; }
    public bool IsCanonicalEpoch { get; set; }
    public string? InvalidatedByFlowEventId { get; set; }
    public DateTime? InvalidatedAtUtc { get; set; }
    public bool IsSupplemental { get; set; }
    public string? SupplementalStepId { get; set; }
    public string? RequestedByUserId { get; set; }
    public bool CompletionRequired { get; set; }
    public bool IsSupplementalCancelled { get; set; }
    public DateTime? SupplementalCancelledAtUtc { get; set; }
    public string? SupplementalCancelledByUserId { get; set; }
    public string? SupplementalCancelReason { get; set; }
    public string ResultOwnerIdentity { get; set; } = string.Empty;
    public string StatisticOwnerIdentity { get; set; } = string.Empty;
    public string TargetUnitId { get; set; } = string.Empty;
    public string? AssignmentId { get; set; }
    public string? ChildInstanceId { get; set; }
    public string? ChildFlowTemplateId { get; set; }
    public string? ChildFlowVersionId { get; set; }
    public string? ChildState { get; set; }
    public string? ChildOutcomeCode { get; set; }
    public DateTime? ChildLinkedAtUtc { get; set; }
    public string? ReportId { get; set; }
    public List<string> ReportIds { get; set; } = new();
    public string? SubmitReportId { get; set; }
    public string? ReviewReportId { get; set; }
    public string FormNodeId { get; set; } = string.Empty;
    public string FormFamilyId { get; set; } = string.Empty;
    public string FormVersionId { get; set; } = string.Empty;
    public int FormVersionNo { get; set; }
    public string FormSchemaHash { get; set; } = string.Empty;
    public string FormSnapshotHash { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public long Revision { get; set; }
    public string RevisionToken { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
    public List<string> VisibilityScopes { get; set; } = new();
    public DynamicFlowRuntimeCapabilitiesDto Capabilities { get; set; } = new();
    public DynamicFlowRuntimeRecoveryStatusDto Recovery { get; set; } = new();
}

public sealed class DynamicFlowRuntimeTimelineRow
{
    public string WorkId { get; set; } = string.Empty;
    public string FlowInstanceId { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string? StepInstanceId { get; set; }
    public string? FlowStepDefinitionId { get; set; }
    public int ExecutionEpoch { get; set; }
    public string? BranchId { get; set; }
    public string? GatewayInstanceId { get; set; }
    public int? GatewayVersion { get; set; }
    public string? ContributionId { get; set; }
    public int? AttemptNo { get; set; }
    public int? ReviewCycleNo { get; set; }
    public string? TargetUnitId { get; set; }
    public string? AssignmentId { get; set; }
    public string? ReportId { get; set; }
    public string? FormVersionId { get; set; }
    public int? FormVersionNo { get; set; }
    public long Sequence { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string CommandId { get; set; } = string.Empty;
    public string? FromState { get; set; }
    public string? ToState { get; set; }
    public long? FromRevision { get; set; }
    public long? ToRevision { get; set; }
    public string? ReasonCode { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public List<string> AffectedRefs { get; set; } = new();
    public DateTime OccurredAtUtc { get; set; }
}

public sealed class DynamicFlowRuntimeCapabilitiesDto
{
    public bool CanViewOverview { get; set; }
    public bool CanViewTimeline { get; set; }
    public bool CanViewAllBranches { get; set; }
    public bool CanOpenAssignment { get; set; }
    public bool CanOpenReport { get; set; }
    public bool CanSubmitReport { get; set; }
    public bool CanReviewReport { get; set; }
    public bool CanRetry { get; set; }
    public bool CanReconcile { get; set; }
    public bool CanForward { get; set; }
    public bool CanLaunchSubflow { get; set; }
    public bool CanManageSupplemental { get; set; }
    public bool CanCancelSupplemental { get; set; }
    public bool CanFinalize { get; set; }
    public bool CanRollback { get; set; }
    public bool CanTerminate { get; set; }
    public bool CanRestart { get; set; }
    public long ExpectedRevision { get; set; }
}

public sealed class DynamicFlowRuntimeRecoveryStatusDto
{
    public string State { get; set; } = string.Empty;
    public bool RecoveryRequired { get; set; }
    public string StatusText { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public string NextAction { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public string ReconcileStatus { get; set; } = "IDLE";
    public long ExpectedRevision { get; set; }
    public string RevisionToken { get; set; } = string.Empty;
    public bool CanRetry { get; set; }
    public bool CanReconcile { get; set; }
}
