namespace tdtd_be.DTOs.Statistics;

public sealed class LabelStatisticSummaryRequest
{
    public string? WorkId { get; set; }
    public string? ScopeType { get; set; }
    public string? ScopeId { get; set; }
    public string? GenerationId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicExcelTemplateId { get; set; }
    public string? LabelCode { get; set; }
    public string? PeriodKey { get; set; }
    public string? PeriodInstanceKey { get; set; }
    public int? ReportStatus { get; set; }
    public bool IncludeDrilldown { get; set; }
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
    public string StatisticLabelLayer { get; set; } = "FIELD_STATISTIC_LABEL";
    public string RuntimeLabelLayer { get; set; } = "RUNTIME_ROW_LABEL";
    public string CatalogLabelLayer { get; set; } = "LABEL_CATALOG";
    public string ConfigurationLayer { get; set; } = "LOCKED_P8_CONFIG";
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
    public P9DirectResultMetadata Metadata { get; set; } = new();
    public List<LabelStatisticSummaryRow> Rows { get; set; } = new();
    public List<P9DirectDrilldownRow> DrilldownRows { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int ReturnedRows { get; set; }
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
