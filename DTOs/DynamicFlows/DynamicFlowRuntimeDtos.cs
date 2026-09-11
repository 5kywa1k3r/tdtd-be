using System.Text.Json.Serialization;
using tdtd_be.DTOs.WorkAssignments;

namespace tdtd_be.DTOs.DynamicFlows;

public class DynamicFlowPreflightRequest
{
    public string FlowTemplateVersionId { get; set; } = string.Empty;
    public string CommandId { get; set; } = string.Empty;
    public List<string> TargetUnitIds { get; set; } = new();
    public string PeriodKey { get; set; } = string.Empty;
    public string ScheduleIdentityJson { get; set; } = "{}";
}

public sealed class DynamicFlowPreflightPageQuery
{
    public int Limit { get; set; } = 25;
    public string? Cursor { get; set; }
}

public sealed class DynamicFlowConfirmRequest : DynamicFlowPreflightRequest
{
    public string SnapshotToken { get; set; } = string.Empty;
}

public sealed class DynamicFlowPreflightResponse
{
    public string WorkId { get; set; } = string.Empty;
    public string WorkType { get; set; } = string.Empty;
    public string CommandId { get; set; } = string.Empty;
    public string IssuerUserId { get; set; } = string.Empty;
    public string IssuerUnitId { get; set; } = string.Empty;
    public string CommandIdentityHash { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string SnapshotToken { get; set; } = string.Empty;
    public string Eligibility { get; set; } = string.Empty;
    public string? BlockedUntilPhase { get; set; }
    public DynamicFlowExactPinDto FlowPin { get; set; } = new();
    public DynamicFlowRuntimeStepDto EntryStep { get; set; } = new();
    public List<DynamicFlowFormPinDto> FormPins { get; set; } = new();
    public List<DynamicFlowTargetSnapshotDto> Targets { get; set; } = new();
    public long TargetTotal { get; set; }
    public int TargetLimit { get; set; }
    public bool TargetHasMore { get; set; }
    public string? TargetNextCursor { get; set; }
    public string PeriodKey { get; set; } = string.Empty;
    public string ScheduleIdentityJson { get; set; } = "{}";
    public string ScheduleIdentityHash { get; set; } = string.Empty;

    [JsonIgnore]
    public string LockedDefinitionJson { get; set; } = string.Empty;
}

public sealed class DynamicFlowConfirmResponse
{
    public string CommandId { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string SnapshotToken { get; set; } = string.Empty;
    public string Status { get; set; } = "PENDING_MATERIALIZATION_NOT_READY";
    public bool BusinessWritePerformed { get; set; }
    public string? FlowInstanceId { get; set; }
    public string? InstanceState { get; set; }
    public List<string> StepInstanceIds { get; set; } = new();
    public List<string> AssignmentIds { get; set; } = new();
}

public sealed class DynamicFlowExactPinDto
{
    public string FlowTemplateId { get; set; } = string.Empty;
    public string FlowTemplateVersionId { get; set; } = string.Empty;
    public int FlowTemplateVersionNo { get; set; }
    public string PayloadHash { get; set; } = string.Empty;
    public string CatalogVersion { get; set; } = string.Empty;
    public string CatalogSemanticHash { get; set; } = string.Empty;
    public string ArchetypeId { get; set; } = string.Empty;
    public string DefinitionRevision { get; set; } = string.Empty;
    public string TopologyHash { get; set; } = string.Empty;
}

public sealed class DynamicFlowFormPinDto
{
    public string FormNodeId { get; set; } = string.Empty;
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string DynamicFormFamilyId { get; set; } = string.Empty;
    public int DynamicFormVersionNo { get; set; }
    public string DynamicFormSchemaHash { get; set; } = string.Empty;
    public string DynamicFormSnapshotHash { get; set; } = string.Empty;
}

public sealed class DynamicFlowTargetSnapshotDto
{
    public string TargetUnitId { get; set; } = string.Empty;
    public List<string> AssigneeUserIds { get; set; } = new();
    public List<DynamicFlowParticipantUserSnapshotDto> Participants { get; set; } = new();
}

public sealed class DynamicFlowParticipantUserSnapshotDto
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string UnitId { get; set; } = string.Empty;
    public string? UnitSymbol { get; set; }
    public string? UnitShortName { get; set; }
    public string? UnitName { get; set; }
    public string? PositionCode { get; set; }
    public string? PositionName { get; set; }
}

public sealed class CreateDynamicFlowInstanceRequest
{
    public string FlowTemplateVersionId { get; set; } = string.Empty;
    public string? ParentAssignmentId { get; set; }
    public string? StepId { get; set; }
    public string? StepCode { get; set; }

    // Kept only for source compatibility with older in-process callers. Runtime role is
    // derived from the assignment relationship and must not be accepted over the wire.
    [JsonIgnore]
    public string? FlowRole { get; set; }
    public List<string> TargetUnitIds { get; set; } = new();
    public string? Name { get; set; }
    public string? AssignmentType { get; set; }
    public string? AggregationType { get; set; }
    public AssignmentScheduleDto? Schedule { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public List<string>? LeaderWatcherUserIds { get; set; }
    public string? DynamicFormDataSourceRulesJson { get; set; }
    public string? AutoApproveConditionJson { get; set; }
    public string? Description { get; set; }
    public bool? IsActive { get; set; }
}

public sealed class DynamicFlowInstanceLaunchResponse
{
    public string WorkId { get; set; } = string.Empty;
    public string FlowInstanceId { get; set; } = string.Empty;
    public string FlowTemplateId { get; set; } = string.Empty;
    public int FlowTemplateVersionNo { get; set; }
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public DynamicFlowRuntimeStepDto Step { get; set; } = new();
    public List<DynamicFlowAssignmentBranchResponse> Branches { get; set; } = new();
}

public sealed class DynamicFlowRuntimeStepDto
{
    public string StepId { get; set; } = string.Empty;
    public string StepCode { get; set; } = string.Empty;
    public int StepOrder { get; set; }
    public string FormNodeId { get; set; } = string.Empty;
    public List<string> NextStepIds { get; set; } = new();
    public bool IsTerminalNode { get; set; }
}

public sealed class DynamicFlowAssignmentBranchResponse
{
    public string AssignmentId { get; set; } = string.Empty;
    public string TargetUnitId { get; set; } = string.Empty;
    public string FlowBranchId { get; set; } = string.Empty;
    public string? ParentFlowBranchId { get; set; }
    public bool AllowSubFlow { get; set; }
    public bool IsFlowFinalNode { get; set; }
    public WorkAssignmentResponse Assignment { get; set; } = default!;
}

public sealed class DynamicFlowBranchActionRequest
{
    public string CommandId { get; set; } = string.Empty;
    public long? ExpectedInstanceRevision { get; set; }
    public long? ExpectedStepRevision { get; set; }
    public string? Reason { get; set; }
    public string? TargetAssignmentId { get; set; }
    public string? SnapshotJson { get; set; }
    public bool? IncludeDescendants { get; set; }
}

public sealed class DynamicFlowBranchActionResponse
{
    public string CommandId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool BusinessWritePerformed { get; set; }
    public bool Replayed { get; set; }
    public string FlowInstanceId { get; set; } = string.Empty;
    public int ExecutionEpoch { get; set; }
    public long InstanceRevision { get; set; }
    public string StepInstanceId { get; set; } = string.Empty;
    public string? NextStepInstanceId { get; set; }
    public string? NextAssignmentId { get; set; }
    public string? ActivatedTransitionId { get; set; }
    public string? GatewayInstanceId { get; set; }
    public string? InputSnapshotHash { get; set; }
    public string? EvaluatorVersion { get; set; }
    public string? SelectedEdgeId { get; set; }
    public string? DecisionReasonCode { get; set; }
    public List<DynamicFlowActivatedBranchDto> ActivatedBranches { get; set; } = new();
    public string EventId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string FlowEffectiveStatus { get; set; } = string.Empty;
    public int? FlowAttemptNo { get; set; }
    public int AffectedAssignmentCount { get; set; }
    public int DirtyReportCount { get; set; }
}

public sealed class DynamicFlowActivatedBranchDto
{
    public string StepInstanceId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string BranchId { get; set; } = string.Empty;
    public string ParentBranchId { get; set; } = string.Empty;
    public string GatewayInstanceId { get; set; } = string.Empty;
    public int GatewayVersion { get; set; }
    public string ContributionId { get; set; } = string.Empty;
    public string TransitionId { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string NodeCode { get; set; } = string.Empty;
}

public sealed class DynamicFlowSubflowLaunchRequest
{
    public string CommandId { get; set; } = string.Empty;
    public long? ExpectedParentInstanceRevision { get; set; }
    public long? ExpectedParentStepRevision { get; set; }
}

public sealed class DynamicFlowSubflowLaunchResponse
{
    public string CommandId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool BusinessWritePerformed { get; set; }
    public bool Replayed { get; set; }
    public string ParentInstanceId { get; set; } = string.Empty;
    public string ParentStepInstanceId { get; set; } = string.Empty;
    public string ChildInstanceId { get; set; } = string.Empty;
    public string ChildFlowTemplateId { get; set; } = string.Empty;
    public string ChildFlowVersionId { get; set; } = string.Empty;
    public int AncestryDepth { get; set; }
    public string ParentState { get; set; } = string.Empty;
    public string ChildState { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
}
