namespace tdtd_be.DTOs.WorkAssignments.Review;

public sealed class ReviewReportFlatRowDto
{
    public string AssignmentId { get; set; } = default!;
    public string WorkId { get; set; } = string.Empty;

    public string DynamicExcelId { get; set; } = default!;
    public string DynamicExcelCode { get; set; } = string.Empty;
    public string DynamicExcelName { get; set; } = string.Empty;

    public string? AssigneeUserId { get; set; }
    public string? AssigneeUserName { get; set; }
    public string? AssigneeFullName { get; set; }
    public string? AssigneeUnitId { get; set; }
    public string? AssigneeUnitName { get; set; }
    public string? AssigneeUnitShortName { get; set; }

    public string? WorkReportPeriodId { get; set; }

    public string PeriodKey { get; set; } = default!;
    public DateTime? StartedDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public bool IsHistoricalData { get; set; }
    public bool HistoricalDataApproved { get; set; }
    public DateTime? HistoricalDataApprovedAtUtc { get; set; }
    public string? HistoricalDataApprovedByUserId { get; set; }
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public int? PeriodStatus { get; set; }

    public string? ReportId { get; set; }
    public int? ReportStatus { get; set; }
    public bool ReportIsActive { get; set; } = true;
    public DateTime? ReportDeactivatedAtUtc { get; set; }
    public string? ReportDeactivationReason { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public DateTime? ReturnedAtUtc { get; set; }
    public string? ReturnReason { get; set; }
    public string? ReviewerComment { get; set; }

    public int ProgressStatus { get; set; }
    public DateTime? ProgressStatusUpdatedAtUtc { get; set; }
    public bool HasAnyDuePeriod { get; set; }
    public bool HasOverduePeriod { get; set; }

    public string? EvaluationCode { get; set; }
    public string? EvaluationLabel { get; set; }
    public int? WorstPeriodStatus { get; set; }
    public string? WorstOverdueReasonCode { get; set; }
    public string? WorstOverdueReasonLabel { get; set; }
}
