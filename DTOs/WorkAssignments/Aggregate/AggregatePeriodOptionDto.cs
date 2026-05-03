namespace tdtd_be.DTOs.WorkAssignments.AggregateTable;

public sealed class AggregatePeriodOptionDto
{
    public string PeriodKey { get; set; } = default!;
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public DateTime? DueAtUtc { get; set; }

    public int ReportCount { get; set; }
    public int ApprovedCount { get; set; }
    public int SubmittedCount { get; set; }
    public bool HasAnyData { get; set; }
}