namespace tdtd_be.DTOs.WorkAssignments.Review;

public sealed class ApproveReportRequest
{
    public int? ExpectedPayloadRevision { get; set; }
    public int? ExpectedLifecycleRevision { get; set; }
    public string? CommandId { get; set; }
    public string? Comment { get; set; }
    public bool ConfirmHistoricalDataApproval { get; set; }
}
