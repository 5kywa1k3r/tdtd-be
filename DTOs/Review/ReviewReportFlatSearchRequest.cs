namespace tdtd_be.DTOs.WorkAssignments.Review;

public sealed class ReviewReportFlatSearchRequest
{
    public string WorkId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string? ScopeAssignmentId { get; set; }
    public string? Q { get; set; }
    public string? DynamicExcelId { get; set; }
    public string? PeriodKey { get; set; }
    public bool? WaitingReviewOnly { get; set; }
    public int? ReportStatus { get; set; }
    public string? ReviewStatusBucket { get; set; }
    public List<string>? AssigneeUserIds { get; set; }
    public string? AssigneeUserId { get; set; }
    public List<string>? AssigneeUnitIds { get; set; }
    public string? AssigneeUnitId { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; } = 20;
}
