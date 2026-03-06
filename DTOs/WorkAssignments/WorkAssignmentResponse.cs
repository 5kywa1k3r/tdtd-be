using tdtd_be.DTOs.Users;

namespace tdtd_be.DTOs.WorkAssignments;

public class WorkAssignmentResponse
{
    public string Id { get; set; } = default!;
    public string WorkId { get; set; } = default!;

    public string DynamicExcelId { get; set; } = default!;
    public string DynamicExcelCode { get; set; } = string.Empty;
    public string DynamicExcelName { get; set; } = string.Empty;

    public string WorkType { get; set; } = string.Empty;
    public string AssignmentType { get; set; } = string.Empty;
    public string AggregationType { get; set; } = string.Empty;

    public AssignmentScheduleDto? Schedule { get; set; }

    public List<UserRefDTO> Assignees { get; set; } = new();
    public List<string> LeaderWatcherUserIds { get; set; } = new();
    public List<UserRefDTO> LeaderWatchers { get; set; } = new();

    public string? Description { get; set; }
    public bool IsActive { get; set; }

    public bool HasData { get; set; }
    public bool TemplateLocked { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public string? ParentAssignmentId { get; set; }
    public string RootAssignmentId { get; set; } = default!;
    public int Level { get; set; }
    public string Code { get; set; } = default!;
    public string Path { get; set; } = default!;
}