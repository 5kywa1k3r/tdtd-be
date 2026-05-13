namespace tdtd_be.DTOs.WorkAssignments;

public sealed class HandoverWorkAssignmentRequest
{
    public string FromAssigneeUserId { get; set; } = default!;
    public string ToAssigneeUserId { get; set; } = default!;
    public string? Reason { get; set; }
    public string? Comment { get; set; }
}

public sealed class WorkAssignmentHandoverResponse
{
    public WorkAssignmentResponse Assignment { get; set; } = default!;
    public string FromAssigneeUserId { get; set; } = default!;
    public string ToAssigneeUserId { get; set; } = default!;
    public string WorkTemplateAssigneeId { get; set; } = default!;
    public long PeriodCount { get; set; }
    public long ReportCount { get; set; }
    public long QueueItemCount { get; set; }
}
