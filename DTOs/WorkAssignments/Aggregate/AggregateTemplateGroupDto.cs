namespace tdtd_be.DTOs.WorkAssignments.AggregateTable;

public sealed class AggregateTemplateGroupDto
{
    public string DynamicExcelId { get; set; } = default!;
    public string DynamicExcelCode { get; set; } = string.Empty;
    public string DynamicExcelName { get; set; } = string.Empty;

    public string RepresentativeAssignmentId { get; set; } = default!;

    public int AssignmentCount { get; set; }
    public int ReportCount { get; set; }

    public List<AggregatePeriodOptionDto> Periods { get; set; } = new();
}