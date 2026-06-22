namespace tdtd_be.DTOs.Statistics;

public sealed class WorkReportStatisticDiffTargetDto
{
    public string SourceKind { get; set; } = "FIELD";
    public string? DynamicFormTemplateId { get; set; }
    public string? FieldId { get; set; }
    public string? FieldKey { get; set; }
    public string? BlockId { get; set; }
    public string? MetricKey { get; set; }
    public string? MetricLabelCode { get; set; }
    public string? RowKey { get; set; }
    public string? ColumnKey { get; set; }
    public string? ConceptCode { get; set; }
    public string? BucketKey { get; set; }
    public string? SourceScopeMode { get; set; }
    public string? SourceFlowInstanceId { get; set; }
    public string? SourceFlowStepId { get; set; }
    public string? SourceFlowBranchId { get; set; }
    public string? SourceFlowEffectiveStatus { get; set; }
}

public sealed class WorkReportStatisticDiffRunRequest
{
    public string? ConfigId { get; set; }
    public string? WorkId { get; set; }
    public string? AssignmentId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public WorkReportStatisticDiffTargetDto? Current { get; set; }
    public WorkReportStatisticDiffTargetDto? Comparison { get; set; }
    public string? PeriodKey { get; set; }
    public string? PeriodCompareMode { get; set; }
    public string? Operator { get; set; }
    public string? JoinKey { get; set; }
    public bool? RequireSameConcept { get; set; }
    public List<string>? SelectedUnitIds { get; set; }
    public string? AssigneeUserId { get; set; }
    public int? ReportStatus { get; set; }
    public int? Limit { get; set; }
}

public sealed class WorkReportStatisticDiffSaveRequest
{
    public string? Id { get; set; }
    public string? WorkId { get; set; }
    public string? AssignmentId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? Name { get; set; }
    public WorkReportStatisticDiffTargetDto? Current { get; set; }
    public WorkReportStatisticDiffTargetDto? Comparison { get; set; }
    public string? PeriodCompareMode { get; set; }
    public string? Operator { get; set; }
    public string? JoinKey { get; set; }
    public bool? RequireSameConcept { get; set; }
    public string? ConfigJson { get; set; }
}

public sealed class WorkReportStatisticDiffConfigDto
{
    public string Id { get; set; } = default!;
    public string WorkId { get; set; } = default!;
    public string AssignmentId { get; set; } = default!;
    public string? DynamicFormTemplateId { get; set; }
    public string Name { get; set; } = string.Empty;
    public WorkReportStatisticDiffTargetDto Current { get; set; } = new();
    public WorkReportStatisticDiffTargetDto Comparison { get; set; } = new();
    public string PeriodCompareMode { get; set; } = "SAME_PERIOD";
    public string Operator { get; set; } = "CHANGED";
    public string JoinKey { get; set; } = "PERIOD";
    public bool RequireSameConcept { get; set; } = true;
    public string ConfigJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class WorkReportStatisticDiffRunResponse
{
    public string WorkId { get; set; } = default!;
    public string AssignmentId { get; set; } = default!;
    public string? ConfigId { get; set; }
    public string CurrentPeriodKey { get; set; } = default!;
    public string ComparisonPeriodKey { get; set; } = default!;
    public string PeriodCompareMode { get; set; } = "SAME_PERIOD";
    public string Operator { get; set; } = "CHANGED";
    public string JoinKey { get; set; } = "PERIOD";
    public bool RequireSameConcept { get; set; } = true;
    public int CurrentSourceAssignmentCount { get; set; }
    public int ComparisonSourceAssignmentCount { get; set; }
    public long CurrentProjectionCount { get; set; }
    public long ComparisonProjectionCount { get; set; }
    public int ComparedRowCount { get; set; }
    public int MatchedOperatorCount { get; set; }
    public bool Truncated { get; set; }
    public List<WorkReportStatisticDiffRowDto> Rows { get; set; } = new();
}

public sealed class WorkReportStatisticDiffRowDto
{
    public string Key { get; set; } = default!;
    public string CurrentPeriodKey { get; set; } = default!;
    public string ComparisonPeriodKey { get; set; } = default!;
    public string? RowKey { get; set; }
    public string? ConceptCode { get; set; }
    public string? DataCategory { get; set; }
    public WorkReportStatisticDiffValueDto? Current { get; set; }
    public WorkReportStatisticDiffValueDto? Comparison { get; set; }
    public decimal? Delta { get; set; }
    public bool Changed { get; set; }
    public bool MatchesOperator { get; set; }
    public string? MissingSide { get; set; }
}

public sealed class WorkReportStatisticDiffValueDto
{
    public string SourceKind { get; set; } = "FIELD";
    public string? Label { get; set; }
    public string? ConceptCode { get; set; }
    public string? DataCategory { get; set; }
    public string? ValueSignature { get; set; }
    public decimal? NumericValue { get; set; }
    public long ValueCount { get; set; }
    public long ReportCount { get; set; }
    public string? BucketKey { get; set; }
    public string? BucketLabel { get; set; }
}
