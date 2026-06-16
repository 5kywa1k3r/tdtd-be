namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Điều kiện tìm kiếm danh sách report.
/// Dùng cho các màn quản trị / history / search chung.
/// </summary>
public sealed class WorkAssignmentReportSearchRequest
{
    public int Page { get; set; } = 0;
    public int PageSize { get; set; } = 20;

    public string? WorkId { get; set; }
    public string? WorkAssignmentId { get; set; }
    public string? WorkReportPeriodId { get; set; }
    public string? AssigneeUserId { get; set; }

    /// <summary>
    /// Từ khóa tự do:
    /// - PeriodKey
    /// - DynamicExcelTemplateCode
    /// - DynamicExcelTemplateName
    /// </summary>
    public string? Q { get; set; }

    public string? PeriodKey { get; set; }

    /// <summary>
    /// Status của report record.
    /// </summary>
    public int? Status { get; set; }

    public bool? IsCurrent { get; set; }
    public bool? IsActive { get; set; }
    public bool? IsLateSubmission { get; set; }

    public DateTime? DueFromUtc { get; set; }
    public DateTime? DueToUtc { get; set; }
    public DateTime? SubmittedFromUtc { get; set; }
    public DateTime? SubmittedToUtc { get; set; }

    /// <summary>
    /// Gợi ý:
    /// - updatedAtUtc
    /// - createdAtUtc
    /// - periodKey
    /// - versionNo
    /// - dueAtUtc
    /// - submittedAtUtc
    /// </summary>
    public string? SortField { get; set; } = "updatedAtUtc";

    public string? SortDirection { get; set; } = "desc";
}
