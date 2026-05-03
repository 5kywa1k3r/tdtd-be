namespace tdtd_be.DTOs.WorkAssignments.Aggregate;

public sealed class AggregateReportRequest
{
    public string WorkAssignmentId { get; set; } = default!;
    public string? PeriodKey { get; set; }
    public string? SourceStatusMode { get; set; } // APPROVED_ONLY / APPROVED_AND_SUBMITTED
}