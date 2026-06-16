namespace tdtd_be.DTOs.WorkAssignmentReports;

public sealed class CreateUserCreatedReportRequest
{
    public string? ReportTitle { get; set; }
    public DateTime? ReportDate { get; set; }
    public string? LinkedScheduledPeriodId { get; set; }
}
