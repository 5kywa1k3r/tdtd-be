using tdtd_be.Models.Enums;

namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Response chi tiết một report.
/// FE dùng để mở màn editor hoặc màn xem report.
/// </summary>
public sealed class WorkAssignmentReportResponse
{
    /// <summary>
    /// Id report.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Id work gốc.
    /// </summary>
    public string WorkId { get; set; } = string.Empty;

    /// <summary>
    /// Id assignment gốc.
    /// </summary>
    public string WorkAssignmentId { get; set; } = string.Empty;

    /// <summary>
    /// Id kỳ runtime mà report này thuộc về.
    /// </summary>
    public string WorkReportPeriodId { get; set; } = string.Empty;

    /// <summary>
    /// User được giao phải báo cáo.
    /// </summary>
    public string AssigneeUserId { get; set; } = string.Empty;

    /// <summary>
    /// Kỳ báo cáo của bản này.
    /// </summary>
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

    /// <summary>
    /// Trạng thái hiện tại của report record.
    /// </summary>
    public WorkAssignmentReportStatus Status { get; set; }

    /// <summary>
    /// Trạng thái ngoài cùng của kỳ.
    /// </summary>
    public WorkReportPeriodStatus? PeriodStatus { get; set; }

    public string TemplateSnapshotJson { get; set; } = string.Empty;
    public string ScheduleSnapshotJson { get; set; } = string.Empty;

    public string? DynamicExcelTemplateId { get; set; }
    public string DynamicExcelTemplateCode { get; set; } = string.Empty;
    public string DynamicExcelTemplateName { get; set; } = string.Empty;
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateCode { get; set; }
    public string? DynamicFormTemplateName { get; set; }
    public string SpecJson { get; set; } = string.Empty;

    public int DataRectR0 { get; set; }
    public int DataRectC0 { get; set; }
    public int DataRectR1 { get; set; }
    public int DataRectC1 { get; set; }
    public int W { get; set; }
    public int H { get; set; }

    public string Values1DJson { get; set; } = string.Empty;
    public string? FieldValuesJson { get; set; }
    public string? TableValuesJson { get; set; }
    public string DataOrigin { get; set; } = string.Empty;
    public string CumulativeContributionMode { get; set; } = string.Empty;
    public string? CumulativeContributionPolicyJson { get; set; }
    public string? SummarySourceJson { get; set; }
    public List<string> AggregateSourceReportIds { get; set; } = new();
    public List<string> AggregateSourceAssignmentIds { get; set; } = new();
    public DateTime? AggregateSourceUpdatedAtUtc { get; set; }
    public bool AggregateSnapshotDirty { get; set; }
    public DateTime? AggregateSnapshotDirtyAtUtc { get; set; }
    public DateTime? AggregateSnapshotRefreshedAtUtc { get; set; }
    public string? AggregateRefreshError { get; set; }

    /// <summary>
    /// Bộ field trải phẳng mà lãnh đạo quan tâm.
    /// </summary>
    public string? CurrentProgressStatus { get; set; }
    public string? ReportReason { get; set; }
    public string? Difficulties { get; set; }
    public string? ProposedSolution { get; set; }

    public bool IsLateSubmission { get; set; }
    public string? LateReason { get; set; }

    public string? ReviewerComment { get; set; }
    public string? ReviewerEvaluation { get; set; }
    public string? ReturnReason { get; set; }

    public int VersionNo { get; set; }
    public bool IsCurrent { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? DeactivatedAtUtc { get; set; }
    public string? DeactivatedByUserId { get; set; }
    public string? DeactivationReason { get; set; }
    public DateTime? ReactivatedAtUtc { get; set; }
    public string? ReactivatedByUserId { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }
    public string? SubmittedByUserId { get; set; }

    public DateTime? ReturnedAtUtc { get; set; }
    public string? ReturnedByUserId { get; set; }

    public DateTime? ApprovedAtUtc { get; set; }
    public string? ApprovedByUserId { get; set; }
    public bool AutoApproved { get; set; }
    public DateTime? AutoApprovedAtUtc { get; set; }
    public string? AutoApprovedByUserId { get; set; }
    public string? AutoApproveConditionSnapshotJson { get; set; }
    public bool AutoApprovalLocked { get; set; }
    public DateTime? AutoApprovalConfirmedAtUtc { get; set; }
    public string? AutoApprovalConfirmedByUserId { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
