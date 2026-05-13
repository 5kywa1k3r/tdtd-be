namespace tdtd_be.DTOs.WorkAssignments.Review;

public sealed class ApproveReportRequest
{
    public string? Comment { get; set; }
    public bool ConfirmHistoricalDataApproval { get; set; }
}
