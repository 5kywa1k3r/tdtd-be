using tdtd_be.DTOs.WorkAssignments.Aggregate;

namespace tdtd_be.DTOs.WorkAssignments.AggregateTable;

public sealed class DynamicFormAggregateRequest
{
    public string ScopeAssignmentId { get; set; } = default!;
    public string? ScopeMode { get; set; }

    public string DynamicFormTemplateId { get; set; } = default!;
    public string? BlockId { get; set; }
    public string? TableMode { get; set; }
    public List<string>? MetricKeys { get; set; }

    public string? PeriodScopeMode { get; set; }
    public string? PeriodKey { get; set; }
    public string? PeriodKeyFrom { get; set; }
    public string? PeriodKeyTo { get; set; }
    public string? SourceStatusMode { get; set; }
    public List<string>? SelectedUnitIds { get; set; }
    public string? AggregateConfigId { get; set; }
    public List<string>? IdentityColumns { get; set; }
}

public sealed class DynamicFormAggregateResponse
{
    public DynamicFormAggregateMetaDto Meta { get; set; } = new();
    public List<DynamicFormAggregateColumnDto> Columns { get; set; } = new();
    public List<DynamicFormAggregateRowDto> Rows { get; set; } = new();
    public DynamicFormStackedTableDto? StackedTable { get; set; }
    public List<AggregateSourceRowDto> Sources { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class DynamicFormAggregateMetaDto
{
    public string ScopeAssignmentId { get; set; } = default!;
    public string ScopeMode { get; set; } = "DIRECT_CHILDREN";

    public string DynamicFormTemplateId { get; set; } = default!;
    public string? DynamicFormTemplateCode { get; set; }
    public string? DynamicFormTemplateName { get; set; }

    public string BlockId { get; set; } = "excel_block";
    public string TableMode { get; set; } = "FIXED_GRID";

    public string? PeriodScopeMode { get; set; }
    public string? PeriodKey { get; set; }
    public string? PeriodKeyFrom { get; set; }
    public string? PeriodKeyTo { get; set; }
    public string? SourceStatusMode { get; set; }
    public List<string> SelectedUnitIds { get; set; } = new();
    public string? AggregateConfigId { get; set; }
    public List<string> IdentityColumns { get; set; } = new();

    public int SourceAssignmentCount { get; set; }
    public int SourceReportCount { get; set; }
    public int MetricCount { get; set; }
}

public sealed class DynamicFormStackedTableDto
{
    public string SourceTableMode { get; set; } = string.Empty;
    public string RowMode { get; set; } = "DIRECT_STACK";
    public List<DynamicFormStackedTableColumnDto> Columns { get; set; } = new();
    public List<DynamicFormStackedTableRowDto> Rows { get; set; } = new();
}

public sealed class DynamicFormStackedTableColumnDto
{
    public string Key { get; set; } = default!;
    public string Label { get; set; } = default!;
    public string Role { get; set; } = "METRIC";
    public string Type { get; set; } = "text";
    public string? MetricKey { get; set; }
    public string? SourceKey { get; set; }
}

public sealed class DynamicFormStackedTableRowDto
{
    public string RowKey { get; set; } = default!;
    public Dictionary<string, object?> Cells { get; set; } = new();
    public List<string> SourceReportIds { get; set; } = new();
    public List<string> SourceAssignmentIds { get; set; } = new();
}

public sealed class DynamicFormAggregateColumnDto
{
    public string Key { get; set; } = default!;
    public string Label { get; set; } = default!;
    public string Type { get; set; } = "number";
}

public sealed class DynamicFormAggregateRowDto
{
    public string MetricKey { get; set; } = default!;
    public string RowKey { get; set; } = default!;
    public string ColumnKey { get; set; } = default!;
    public int Index { get; set; }
    public string? Label { get; set; }

    public string? GroupType { get; set; }
    public string? GroupKey { get; set; }
    public string? GroupLabel { get; set; }
    public string? WorkAssignmentId { get; set; }
    public string? UnitSymbol { get; set; }
    public string? UnitShortName { get; set; }
    public string? UserName { get; set; }
    public string? FullName { get; set; }
    public string? SourceMetricKey { get; set; }
    public int? LayoutIndex { get; set; }
    public int? OutputGroupIndex { get; set; }
    public int? OutputRowIndex { get; set; }
    public int? OutputRowNumber { get; set; }
    public int? RowsPerGroup { get; set; }
    public long? ReportCount { get; set; }

    public int Count { get; set; }
    public decimal? Sum { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public decimal? Average { get; set; }
}
