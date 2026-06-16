namespace tdtd_be.DTOs.WorkAssignments.AggregateTable;

public sealed class WorkAssignmentAggregateConfigDto
{
    public string? Id { get; set; }
    public string WorkId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string? SourceDynamicFormTemplateId { get; set; }
    public string? SourceBlockId { get; set; }
    public string? SourceTableMode { get; set; }
    public string? TargetDynamicFormTemplateId { get; set; }
    public string? TargetBlockId { get; set; }
    public string AggregateKind { get; set; } = "AUTO_MAP";
    public List<string> IdentityColumns { get; set; } = new();
    public string PeriodAggregationRule { get; set; } = "STACK_SINGLE_PERIOD_SUM_RANGE";
    public string? MetricMappingsJson { get; set; }
    public int VersionNo { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}

public sealed class SaveWorkAssignmentAggregateConfigRequest
{
    public string? SourceDynamicFormTemplateId { get; set; }
    public string? SourceBlockId { get; set; }
    public string? SourceTableMode { get; set; }
    public string? TargetDynamicFormTemplateId { get; set; }
    public string? TargetBlockId { get; set; }
    public string? AggregateKind { get; set; }
    public List<string>? IdentityColumns { get; set; }
    public string? PeriodAggregationRule { get; set; }
    public string? MetricMappingsJson { get; set; }
}
