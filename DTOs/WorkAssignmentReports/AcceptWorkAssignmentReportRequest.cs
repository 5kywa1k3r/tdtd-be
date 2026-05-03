namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Request chấp nhận báo cáo.
/// Dành cho phase duyệt.
/// </summary>
public sealed class AcceptWorkAssignmentReportRequest
{
    public string? ReviewerComment { get; set; }

    /// <summary>
    /// Cho phép cấp trên nhập/chỉnh lý do trễ hạn nếu cần.
    /// </summary>
    public string? LateReasonOverride { get; set; }
}