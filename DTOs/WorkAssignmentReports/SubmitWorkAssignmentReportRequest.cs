namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Request nộp báo cáo.
/// Cho phép submit kèm save dữ liệu nghiệp vụ ngay trong 1 lần gọi.
/// </summary>
public sealed class SubmitWorkAssignmentReportRequest
{
    public List<object?>? Values1D { get; set; } = default!;
    public string? FieldValuesJson { get; set; }
    public string? TableValuesJson { get; set; }
    /// <summary>
    /// Nếu đổi nguồn mà không gửi CumulativeContributionMode, BE tự áp default theo nguồn.
    /// </summary>
    public string? DataOrigin { get; set; }
    public string? CumulativeContributionMode { get; set; }
    public string? CumulativeContributionPolicyJson { get; set; }
    public string? SummarySourceJson { get; set; }
    public string? CurrentProgressStatus { get; set; }
    public string? ReportReason { get; set; }
    public string? Difficulties { get; set; }
    public string? ProposedSolution { get; set; }
    public DateTime? StartedDate { get; set; }
    public DateTime? CompletedDate { get; set; }

    /// <summary>
    /// Nếu nộp trễ hạn thì bắt buộc phải có lý do.
    /// </summary>
    public string? LateReason { get; set; }

    public string? Note { get; set; }
}
