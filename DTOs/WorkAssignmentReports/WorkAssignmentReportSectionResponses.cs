namespace tdtd_be.DTOs.WorkAssignmentReports;

public sealed class WorkAssignmentReportSectionSummaryRow
{
    public string SectionId { get; set; } = string.Empty;
    public string SectionTitle { get; set; } = string.Empty;
    public int SectionOrder { get; set; }
    public int FieldCount { get; set; }
    public int BlockCount { get; set; }
    public bool HasData { get; set; }
    public DateTime? LastUpdatedAtUtc { get; set; }
    public string? LastUpdatedByUserId { get; set; }
    public DateTime? SourcePayloadUpdatedAtUtc { get; set; }
    public int SourcePayloadRevision { get; set; }
    public string? SourcePayloadHash { get; set; }
    public string? DynamicFlowMappingReceiptId { get; set; }
    public string? DynamicFlowMappingProvenanceId { get; set; }
    public string? DynamicFlowMappingProvenanceHash { get; set; }
    public int? DynamicFlowMappingResultPayloadRevision { get; set; }
    public string? DynamicFlowMappingResultPayloadHash { get; set; }
    public int SourceLifecycleRevision { get; set; }
    public DateTime? SourceReportUpdatedAtUtc { get; set; }
}

public sealed class WorkAssignmentReportSectionDetailResponse
{
    public string ReportId { get; set; } = string.Empty;
    public string SectionId { get; set; } = string.Empty;
    public string SectionTitle { get; set; } = string.Empty;
    public int SectionOrder { get; set; }
    public int FieldCount { get; set; }
    public int BlockCount { get; set; }
    public bool HasData { get; set; }
    public DateTime? LastUpdatedAtUtc { get; set; }
    public string? LastUpdatedByUserId { get; set; }
    public DateTime? SourcePayloadUpdatedAtUtc { get; set; }
    public int SourcePayloadRevision { get; set; }
    public string? SourcePayloadHash { get; set; }
    public string? DynamicFlowMappingReceiptId { get; set; }
    public string? DynamicFlowMappingProvenanceId { get; set; }
    public string? DynamicFlowMappingProvenanceHash { get; set; }
    public int? DynamicFlowMappingResultPayloadRevision { get; set; }
    public string? DynamicFlowMappingResultPayloadHash { get; set; }
    public int SourceLifecycleRevision { get; set; }
    public DateTime? SourceReportUpdatedAtUtc { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateCode { get; set; }
    public string? DynamicFormTemplateName { get; set; }
    public string? DynamicFormFamilyId { get; set; }
    public int? DynamicFormVersionNo { get; set; }
    public string? DynamicFormSchemaHash { get; set; }
    public string FieldsJson { get; set; } = "[]";
    public string BlocksJson { get; set; } = "[]";
    public string? FieldValuesJson { get; set; }
    public string? TableValuesJson { get; set; }
}
