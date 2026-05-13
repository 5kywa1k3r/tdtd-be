namespace tdtd_be.DTOs.Statistics;

public sealed class LabelStatisticSummaryRequest
{
    public string? WorkId { get; set; }
    public string? ScopeType { get; set; }
    public string? ScopeId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicExcelTemplateId { get; set; }
    public string? LabelCode { get; set; }
    public string? PeriodKey { get; set; }
    public string? PeriodInstanceKey { get; set; }
    public int? ReportStatus { get; set; }
    public int Page { get; set; } = 0;
    public int PageSize { get; set; } = 50;
}

public sealed class LabelStatisticSummaryRow
{
    public string WorkId { get; set; } = default!;
    public string ScopeType { get; set; } = default!;
    public string ScopeId { get; set; } = default!;
    public string? RootAssignmentId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateCode { get; set; }
    public string? DynamicFormTemplateName { get; set; }
    public string? DynamicExcelTemplateId { get; set; }
    public string BlockId { get; set; } = default!;
    public string LabelCode { get; set; } = default!;
    public string? LabelName { get; set; }
    public string? LabelColor { get; set; }
    public string LabelDataType { get; set; } = "NUMBER";
    public string PeriodKey { get; set; } = default!;
    public string PeriodInstanceKey { get; set; } = default!;
    public string PeriodKind { get; set; } = default!;
    public int ReportStatus { get; set; }
    public long RowCount { get; set; }
    public long ReportCount { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class LabelStatisticSummaryResponse
{
    public List<LabelStatisticSummaryRow> Rows { get; set; } = new();
    public long TotalRows { get; set; }
    public long TotalRowCount { get; set; }
    public long TotalReportCount { get; set; }
}

public sealed class RebuildLabelStatisticRequest
{
    public string WorkId { get; set; } = default!;
    public string? PeriodInstanceKey { get; set; }
    public string? DynamicFormTemplateId { get; set; }
}
