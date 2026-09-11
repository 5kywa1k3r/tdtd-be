namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Request trả lại báo cáo để user cấp dưới báo cáo lại.
/// </summary>
public sealed class ReturnWorkAssignmentReportRequest
{
    public int? ExpectedPayloadRevision { get; set; }
    public int? ExpectedLifecycleRevision { get; set; }
    public string? CommandId { get; set; }
    public string ReturnReason { get; set; } = string.Empty;
    public string? ReviewerComment { get; set; }
}
