namespace tdtd_be.DTOs.Statistics;

public sealed class TableStatisticSummaryRequest
{
    public string? WorkId { get; set; }
    public string? ScopeType { get; set; }
    public string? ScopeId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicExcelTemplateId { get; set; }
    public string? BlockId { get; set; }
    public string? TableMode { get; set; }
    public string? MetricKey { get; set; }
    public string? PeriodKey { get; set; }
    public string? PeriodInstanceKey { get; set; }
    public int? ReportStatus { get; set; }
    public int Page { get; set; } = 0;
    public int PageSize { get; set; } = 50;
}

public sealed class TableStatisticSummaryRow
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
    public string TableMode { get; set; } = default!;
    public string MetricKey { get; set; } = default!;
    public string RowKey { get; set; } = default!;
    public string ColumnKey { get; set; } = default!;
    public string PeriodKey { get; set; } = default!;
    public string PeriodInstanceKey { get; set; } = default!;
    public string PeriodKind { get; set; } = default!;
    public int ReportStatus { get; set; }
    public long ValueCount { get; set; }
    public decimal? Sum { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public decimal? Average { get; set; }
    public long ReportCount { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class TableStatisticSummaryResponse
{
    public List<TableStatisticSummaryRow> Rows { get; set; } = new();
    public long TotalRows { get; set; }
    public long TotalValueCount { get; set; }
    public decimal TotalSum { get; set; }
    public long TotalReportCount { get; set; }
}

public sealed class RebuildTableStatisticRequest
{
    public string WorkId { get; set; } = default!;
    public string? PeriodInstanceKey { get; set; }
    public string? DynamicFormTemplateId { get; set; }
}

public sealed class RebuildTableStatisticResponse
{
    public string WorkId { get; set; } = default!;
    public string? PeriodInstanceKey { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public int ReportCount { get; set; }
}
