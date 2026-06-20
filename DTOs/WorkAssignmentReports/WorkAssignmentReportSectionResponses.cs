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
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateCode { get; set; }
    public string? DynamicFormTemplateName { get; set; }
    public string FieldsJson { get; set; } = "[]";
    public string BlocksJson { get; set; } = "[]";
    public string? FieldValuesJson { get; set; }
    public string? TableValuesJson { get; set; }
}
