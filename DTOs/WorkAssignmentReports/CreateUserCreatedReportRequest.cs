namespace tdtd_be.DTOs.WorkAssignmentReports;

public sealed class CreateUserCreatedReportRequest
{
    public string? PeriodKey { get; set; }
    public string? ReportTitle { get; set; }
    public DateTime? ReportDate { get; set; }
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public string? LinkedScheduledPeriodId { get; set; }
}
