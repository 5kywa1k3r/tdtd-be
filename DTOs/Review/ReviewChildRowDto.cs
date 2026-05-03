namespace tdtd_be.DTOs.WorkAssignments.Review;

public sealed class ReviewChildRowDto
{
    public string WorkAssignmentId { get; set; } = default!;
    public string ParentId { get; set; } = default!;

    public string DynamicExcelId { get; set; } = default!;
    public string DynamicExcelCode { get; set; } = string.Empty;
    public string DynamicExcelName { get; set; } = string.Empty;

    public string AssigneeUserId { get; set; } = default!;
    public string AssigneeName { get; set; } = string.Empty;
    public string? UnitId { get; set; }
    public string? UnitName { get; set; }

    public int ProgressStatus { get; set; }
    public string ProgressStatusText { get; set; } = string.Empty;

    public bool HasAnyDuePeriod { get; set; }
    public bool HasOverduePeriod { get; set; }
    public string? LatestPeriodKey { get; set; }
    public DateTime? LatestDueAtUtc { get; set; }

    public string? CurrentReportId { get; set; }
    public int? CurrentReportStatus { get; set; }
    public DateTime? CurrentSubmittedAtUtc { get; set; }
    public DateTime? CurrentApprovedAtUtc { get; set; }

    public string? EvaluationCode { get; set; }
    public string? EvaluationLabel { get; set; }
    public int? WorstPeriodStatus { get; set; }
    public string? WorstOverdueReasonCode { get; set; }
    public string? WorstOverdueReasonLabel { get; set; }
}