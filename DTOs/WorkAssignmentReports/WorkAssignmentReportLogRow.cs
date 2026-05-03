namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Dòng dữ liệu log nghiệp vụ của report.
/// </summary>
public sealed class WorkAssignmentReportLogRow
{
    public string Id { get; set; } = string.Empty;

    public string WorkId { get; set; } = string.Empty;
    public string WorkAssignmentId { get; set; } = string.Empty;
    public string WorkReportPeriodId { get; set; } = string.Empty;
    public string WorkAssignmentReportId { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;
    public string FromStatus { get; set; } = string.Empty;
    public string ToStatus { get; set; } = string.Empty;

    public string ActionByUserId { get; set; } = string.Empty;
    public DateTime ActionAtUtc { get; set; }

    public string? Reason { get; set; }
    public string? Comment { get; set; }
    public string? SnapshotJson { get; set; }
}