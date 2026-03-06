namespace tdtd_be.DTOs.WorkAssignments;

public sealed class SaveWorkAssignmentRequest
{
    public string DynamicExcelId { get; set; } = default!;
    public string AssignmentType { get; set; } = default!;
    public string AggregationType { get; set; } = default!;
    public AssignmentScheduleDto? Schedule { get; set; }

    public List<string> AssigneeUserIds { get; set; } = new();
    public List<string>? LeaderWatcherUserIds { get; set; }

    public string? Description { get; set; }
    public string? ParentAssignmentId { get; set; }

    public bool? IsActive { get; set; }
}