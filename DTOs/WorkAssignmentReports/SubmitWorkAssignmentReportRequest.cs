namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Request nộp báo cáo.
/// Cho phép submit kèm save dữ liệu nghiệp vụ ngay trong 1 lần gọi.
/// </summary>
public sealed class SubmitWorkAssignmentReportRequest
{
    public int? ExpectedPayloadRevision { get; set; }
    public int? ExpectedLifecycleRevision { get; set; }
    public string? CommandId { get; set; }
    public string? AggregateConfirmationToken { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AggregateSessionKey { get; set; }
    public List<object?>? Values1D { get; set; } = default!;
    public string? FieldValuesJson { get; set; }
    public string? TableValuesJson { get; set; }
    // Legacy Advanced editor contract is paused. Existing report metadata remains readable,
    // but ordinary submit JSON cannot mutate it.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? DataOrigin { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CumulativeContributionMode { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CumulativeContributionPolicyJson { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SummarySourceJson { get; set; }
    public DateTime? CompletedDate { get; set; }

    /// <summary>
    /// Nếu nộp trễ hạn thì bắt buộc phải có lý do.
    /// </summary>
    public string? LateReason { get; set; }

    public string? Note { get; set; }
}
