namespace tdtd_be.DTOs.WorkAssignments.Review;

public sealed class ReportActiveRequest
{
    public int? ExpectedPayloadRevision { get; set; }
    public int? ExpectedLifecycleRevision { get; set; }
    public string? CommandId { get; set; }
    public string? Comment { get; set; }
}
