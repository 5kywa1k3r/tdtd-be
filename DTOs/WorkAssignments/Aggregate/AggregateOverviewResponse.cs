namespace tdtd_be.DTOs.WorkAssignments.AggregateTable;

public sealed class AggregateOverviewResponse
{
    public string WorkId { get; set; } = default!;
    public List<AggregateTemplateGroupDto> Templates { get; set; } = new();
}