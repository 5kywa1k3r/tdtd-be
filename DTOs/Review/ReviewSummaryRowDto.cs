namespace tdtd_be.DTOs.WorkAssignments.Review;

public sealed class ReviewSummaryAssigneeDto
{
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? FullName { get; set; }
    public string? UnitId { get; set; }
    public string? UnitName { get; set; }
    public string? UnitShortName { get; set; }
}

public sealed class ReviewSummaryRowDto
{
    public string AssignmentId { get; set; } = default!;
    public string WorkId { get; set; } = string.Empty;

    public string DynamicExcelId { get; set; } = default!;
    public string DynamicExcelCode { get; set; } = string.Empty;
    public string DynamicExcelName { get; set; } = string.Empty;

    public List<ReviewSummaryAssigneeDto> Assignees { get; set; } = new();

    public int ProgressStatus { get; set; }
    public DateTime? ProgressStatusUpdatedAtUtc { get; set; }

    public string? LatestPeriodKey { get; set; }
    public int? LatestPeriodStatus { get; set; }
    public DateTime? LatestDueAtUtc { get; set; }
    public bool HasAnyDuePeriod { get; set; }
    public bool HasOverduePeriod { get; set; }

    public string? EvaluationCode { get; set; }
    public string? EvaluationLabel { get; set; }

    public int? WorstPeriodStatus { get; set; }
    public string? WorstOverdueReasonCode { get; set; }
    public string? WorstOverdueReasonLabel { get; set; }
}
