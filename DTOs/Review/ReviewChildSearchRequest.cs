namespace tdtd_be.DTOs.WorkAssignments.Review;

public sealed class ReviewChildSearchRequest
{
    public string ParentAssignmentId { get; set; } = default!;
    public string? Q { get; set; }
    public int? ProgressStatus { get; set; }
    public bool? HasOverdueOnly { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}