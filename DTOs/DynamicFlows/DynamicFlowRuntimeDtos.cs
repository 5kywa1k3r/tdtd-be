using tdtd_be.DTOs.WorkAssignments;

namespace tdtd_be.DTOs.DynamicFlows;

public sealed class CreateDynamicFlowInstanceRequest
{
    public string FlowTemplateVersionId { get; set; } = string.Empty;
    public string? ParentAssignmentId { get; set; }
    public string? StepId { get; set; }
    public string? StepCode { get; set; }
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
    public string? Reason { get; set; }
    public string? TargetAssignmentId { get; set; }
    public string? SnapshotJson { get; set; }
    public bool? IncludeDescendants { get; set; }
}

public sealed class DynamicFlowBranchActionResponse
{
    public string EventId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string FlowEffectiveStatus { get; set; } = string.Empty;
    public int? FlowAttemptNo { get; set; }
    public int AffectedAssignmentCount { get; set; }
    public int DirtyReportCount { get; set; }
}
