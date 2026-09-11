namespace tdtd_be.DTOs.DynamicFlows;

public sealed class DynamicFlowEpochCommandRequest
{
    public string CommandId { get; set; } = string.Empty;
    public int ExpectedExecutionEpoch { get; set; }
    public long ExpectedInstanceRevision { get; set; }
    public string? CheckpointNodeId { get; set; }
    public string? Reason { get; set; }
}

public sealed class DynamicFlowEpochCommandResponse
{
    public string CommandId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string FlowInstanceId { get; set; } = string.Empty;
    public int PreviousExecutionEpoch { get; set; }
    public int ExecutionEpoch { get; set; }
    public long InstanceRevision { get; set; }
    public string InstanceState { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string? RebuildIntentId { get; set; }
    public int InvalidatedStepCount { get; set; }
    public int InvalidatedGatewayCount { get; set; }
    public int InvalidatedAssignmentCount { get; set; }
    public int InvalidatedReportCount { get; set; }
    public bool BusinessWritePerformed { get; set; }
    public bool Replayed { get; set; }
}
