namespace tdtd_be.DTOs.DynamicFlows;

public sealed class DynamicFlowPolicyEvaluationContext
{
    public string? StepId { get; set; }
    public string? StepCode { get; set; }
    public string? ActorRole { get; set; }
    public bool IsAfterSubmit { get; set; }
}

public sealed class DynamicFlowPolicyEvaluationResult
{
    public Dictionary<string, DynamicFlowFieldPermissionDto> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DynamicFlowTableColumnPermissionDto> TableColumns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool DenyAllFields { get; set; }
    public bool DenyAllTableColumns { get; set; }
}

public sealed class DynamicFlowFieldPermissionDto
{
    public string TargetKey { get; set; } = string.Empty;
    public string? FieldId { get; set; }
    public string? FieldKey { get; set; }
    public bool Read { get; set; }
    public bool Write { get; set; }
    public bool Required { get; set; }
    public bool Hidden { get; set; }
    public bool Locked { get; set; }
    public bool LockedAfterSubmit { get; set; }
    public string? SourcePolicyId { get; set; }
}

public sealed class DynamicFlowTableColumnPermissionDto
{
    public string TargetKey { get; set; } = string.Empty;
    public string BlockId { get; set; } = string.Empty;
    public string ColumnKey { get; set; } = string.Empty;
    public bool Read { get; set; }
    public bool Write { get; set; }
    public bool Required { get; set; }
    public bool Hidden { get; set; }
    public bool Locked { get; set; }
    public bool LockedAfterSubmit { get; set; }
    public string? SourcePolicyId { get; set; }
}
