namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Dòng dữ liệu danh sách report.
/// Dùng cho các màn list theo assignment / search chung / history.
/// </summary>
public sealed class WorkAssignmentReportListRow
{
    public string Id { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
    public string WorkAssignmentId { get; set; } = string.Empty;
    public string WorkReportPeriodId { get; set; } = string.Empty;
    public string AssigneeUserId { get; set; } = string.Empty;

    public string PeriodKey { get; set; } = string.Empty;
    public string PeriodInstanceKey { get; set; } = string.Empty;
    public string PeriodKind { get; set; } = string.Empty;
    public string? ReportTitle { get; set; }
    public DateTime? ReportDate { get; set; }
    public string? LinkedScheduledPeriodId { get; set; }
    public DateTime? StartedDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public bool IsHistoricalData { get; set; }
    public bool HistoricalDataApproved { get; set; }
    public DateTime? HistoricalDataApprovedAtUtc { get; set; }
    public string? HistoricalDataApprovedByUserId { get; set; }
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public DateTime? DueAtUtc { get; set; }

    public int Status { get; set; }
    public int? ReportStatus { get; set; }
    public int? PeriodStatus { get; set; }
    public bool IsLateSubmission { get; set; }
    public string? LateReason { get; set; }

    public string DynamicExcelTemplateId { get; set; } = string.Empty;
    public string DynamicExcelTemplateCode { get; set; } = string.Empty;
    public string DynamicExcelTemplateName { get; set; } = string.Empty;
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateCode { get; set; }
    public string? DynamicFormTemplateName { get; set; }
    public string DataOrigin { get; set; } = string.Empty;
    public bool AggregateSnapshotDirty { get; set; }
    public DateTime? AggregateSnapshotDirtyAtUtc { get; set; }
    public DateTime? AggregateSnapshotRefreshedAtUtc { get; set; }
    public string? AggregateRefreshError { get; set; }

    public string? CurrentProgressStatus { get; set; }
    public string? ReportReason { get; set; }
    public string? Difficulties { get; set; }
    public string? ProposedSolution { get; set; }

    public int VersionNo { get; set; }
    public bool IsCurrent { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? DeactivatedAtUtc { get; set; }
    public string? DeactivationReason { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }
    public string? SubmittedByUserId { get; set; }
    public DateTime? ReturnedAtUtc { get; set; }
    public string? ReturnedByUserId { get; set; }
    public string? ReturnReason { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? ApprovedByUserId { get; set; }
    public bool AutoApproved { get; set; }
    public DateTime? AutoApprovedAtUtc { get; set; }
    public string? AutoApprovedByUserId { get; set; }
    public bool AutoApprovalLocked { get; set; }
    public DateTime? AutoApprovalConfirmedAtUtc { get; set; }
    public string? AutoApprovalConfirmedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
