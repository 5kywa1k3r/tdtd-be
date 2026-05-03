namespace tdtd_be.DTOs.WorkAssignments.AggregateTable;

public sealed class AggregateSourceRowDto
{
    public string ReportId { get; set; } = default!;
    public string WorkAssignmentId { get; set; } = default!;
    public string? AssigneeUserId { get; set; }
    public string? UserName { get; set; }
    public string? FullName { get; set; }
    public string? UnitSymbol { get; set; }
    public string? UnitShortName { get; set; }

    public int ReportStatus { get; set; }
    public string PeriodKey { get; set; } = default!;
    public string? PeriodInstanceKey { get; set; }
    public string? PeriodKind { get; set; }
    public DateTime? ReportDate { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
}
