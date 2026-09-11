namespace tdtd_be.DTOs.DynamicFlows;

public sealed class DynamicFlowSupplementalAddRequest
{
    public string CommandId { get; set; } = string.Empty;
    public long? ExpectedInstanceRevision { get; set; }
    public string FormNodeId { get; set; } = string.Empty;
    public string TargetUnitId { get; set; } = string.Empty;
    public List<string> ParticipantUserIds { get; set; } = new();
    public bool? CompletionRequired { get; set; }
}

public sealed class DynamicFlowSupplementalCancelRequest
{
    public string CommandId { get; set; } = string.Empty;
    public long? ExpectedInstanceRevision { get; set; }
    public long? ExpectedStepRevision { get; set; }
    public string? Reason { get; set; }
}

public sealed class DynamicFlowSupplementalCommandResponse
{
    public string CommandId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool BusinessWritePerformed { get; set; }
    public bool Replayed { get; set; }
    public string FlowInstanceId { get; set; } = string.Empty;
    public int ExecutionEpoch { get; set; }
    public long InstanceRevision { get; set; }
    public string SupplementalStepId { get; set; } = string.Empty;
    public string StepInstanceId { get; set; } = string.Empty;
    public string? AssignmentId { get; set; }
    public bool CompletionRequired { get; set; }
    public string State { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
}
