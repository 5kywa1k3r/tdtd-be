namespace tdtd_be.DTOs.DynamicFlows;

public sealed class DynamicFlowMappingRequest
{
    public string? SourceMode { get; set; }
    public List<string>? SourceReportIds { get; set; }
    public string? FlowTemplateVersionId { get; set; }
    public string? FlowTemplateId { get; set; }
    public int? FlowTemplateVersionNo { get; set; }
    public string? MappingRulesJson { get; set; }
    public List<DynamicFlowMappingRuleDto>? MappingRules { get; set; }
    public string? ConflictPolicy { get; set; }
    public string? ContributionPolicy { get; set; }
    public bool? RequireSourceReport { get; set; }
}

public sealed class DynamicFlowMappingRuleDto
{
    public string MappingId { get; set; } = string.Empty;
    public int MappingVersion { get; set; } = 1;
    public string? SourceStepId { get; set; }
    public string? SourceStepCode { get; set; }
    public string? SourceFieldId { get; set; }
    public string? SourceFieldKey { get; set; }
    public string? SourceBlockId { get; set; }
    public string? SourceColumnKey { get; set; }
    public string? TargetStepId { get; set; }
    public string? TargetStepCode { get; set; }
    public string? TargetFieldId { get; set; }
    public string? TargetFieldKey { get; set; }
    public string? TargetBlockId { get; set; }
    public string? TargetColumnKey { get; set; }
    public string? ConceptCode { get; set; }
    public string? DataType { get; set; }
    public string? JoinKey { get; set; }
    public string? ValueTransform { get; set; }
    public string? ConflictPolicy { get; set; }
    public string? ContributionPolicy { get; set; }
}

public sealed class DynamicFlowMappingPreviewResponse
{
    public string TargetReportId { get; set; } = string.Empty;
    public string TargetAssignmentId { get; set; } = string.Empty;
    public string DataOrigin { get; set; } = string.Empty;
    public string CumulativeContributionMode { get; set; } = string.Empty;
    public string? CumulativeContributionPolicyJson { get; set; }
    public string? SummarySourceJson { get; set; }
    public string? FieldValuesJson { get; set; }
    public string? TableValuesJson { get; set; }
    public List<DynamicFlowMappingSourceReportDto> SourceReports { get; set; } = new();
    public List<DynamicFlowMappingChangeDto> Changes { get; set; } = new();
    public bool HasBlockingConflicts { get; set; }
}

public sealed class DynamicFlowMappingSourceReportDto
{
    public string ReportId { get; set; } = string.Empty;
    public string WorkAssignmentId { get; set; } = string.Empty;
    public string? FlowStepId { get; set; }
    public string? FlowStepCode { get; set; }
    public string PeriodInstanceKey { get; set; } = string.Empty;
}

public sealed class DynamicFlowMappingChangeDto
{
    public string MappingId { get; set; } = string.Empty;
    public int MappingVersion { get; set; }
    public string TargetKind { get; set; } = string.Empty;
    public string TargetKey { get; set; } = string.Empty;
    public string? SourceReportId { get; set; }
    public string? SourceKey { get; set; }
    public string? PreviousValueJson { get; set; }
    public string? NextValueJson { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? ConceptCode { get; set; }
    public string? ContributionPolicy { get; set; }
}
