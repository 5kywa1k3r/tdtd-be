namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Dòng dữ liệu của một kỳ báo cáo runtime.
/// FE dùng để hiển thị list kỳ trong màn detail template.
/// </summary>
public sealed class WorkReportPeriodRow
{
    public string Id { get; set; } = string.Empty;

    public string WorkId { get; set; } = string.Empty;
    public string WorkAssignmentId { get; set; } = string.Empty;
    public string WorkTemplateAssigneeId { get; set; } = string.Empty;

    public string DynamicExcelId { get; set; } = string.Empty;
    public string DynamicExcelCode { get; set; } = string.Empty;
    public string DynamicExcelName { get; set; } = string.Empty;

    public string AssigneeUserId { get; set; } = string.Empty;

    public string PeriodKey { get; set; } = string.Empty;
    public string PeriodInstanceKey { get; set; } = string.Empty;
    public string PeriodKind { get; set; } = string.Empty;
    public string? ReportTitle { get; set; }
    public DateTime? ReportDate { get; set; }
    public string? LinkedScheduledPeriodId { get; set; }
    public DateTime? StartedDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public bool CanEditCompletedDate { get; set; }
    public bool RequiresCompletedDate { get; set; }
    public DateTime? CompletedDateMin { get; set; }
    public DateTime? CompletedDateMax { get; set; }
    public string? CompletedDatePolicyReason { get; set; }
    public bool IsHistoricalData { get; set; }
    public bool HistoricalDataApproved { get; set; }
    public DateTime? HistoricalDataApprovedAtUtc { get; set; }
    public string? HistoricalDataApprovedByUserId { get; set; }
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public DateTime? DueAtUtc { get; set; }

    public int Status { get; set; }
    public bool IsOverdue { get; set; }

    public string? CurrentReportId { get; set; }
    public int ReportVersionCount { get; set; }

    public DateTime? LastDraftSavedAtUtc { get; set; }
    public DateTime? LastSubmittedAtUtc { get; set; }
    public DateTime? LastReviewedAtUtc { get; set; }

    public string? CurrentProgressStatus { get; set; }
    public string? ReportReason { get; set; }
    public string? Difficulties { get; set; }
    public string? ProposedSolution { get; set; }
    public string? LateReason { get; set; }

    public string? ReviewerComment { get; set; }
    public string? ReviewerEvaluation { get; set; }
    public string? ReturnReason { get; set; }
}
