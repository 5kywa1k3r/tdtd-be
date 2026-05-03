using tdtd_be.DTOs.WorkAssignments.AggregateTable;

namespace tdtd_be.DTOs.WorkAssignments.Aggregate;

public sealed class AggregateReportResponse
{
    public string WorkAssignmentId { get; set; } = default!;
    public string DynamicExcelId { get; set; } = default!;
    public string DynamicExcelCode { get; set; } = string.Empty;
    public string DynamicExcelName { get; set; } = string.Empty;

    public string TemplateRuntimeType { get; set; } = default!;
    public string AggregateMode { get; set; } = default!;

    public string? PeriodKey { get; set; }
    public int SourceReportCount { get; set; }

    public object? Workbook { get; set; }

    public List<AggregateSourceRowDto> Sources { get; set; } = new();
}