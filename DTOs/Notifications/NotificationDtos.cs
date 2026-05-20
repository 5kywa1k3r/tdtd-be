using tdtd_be.Models;

namespace tdtd_be.DTOs.Notifications;

public sealed class NotificationRowDto
{
    public string Id { get; set; } = default!;
    public string Type { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    public string? WorkId { get; set; }
    public WorkType? WorkType { get; set; }
    public string? WorkName { get; set; }
    public string? WorkAssignmentId { get; set; }
    public string? AssignmentCode { get; set; }
    public string? WorkReportPeriodId { get; set; }
    public string? WorkAssignmentReportId { get; set; }
    public string? Category { get; set; }
    public bool RequiresAction { get; set; }
    public string? ActionState { get; set; }
    public string? SourceEntityType { get; set; }
    public string? SourceEntityId { get; set; }
    public string? RequestId { get; set; }
    public string? ActionUrl { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string? ActorUserId { get; set; }
    public string? SourceUserId { get; set; }
    public string? TargetUserId { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
    public DateTime? ClickedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class NotificationSearchRequest
{
    public DateTime? CursorOccurredAtUtc { get; set; }
    public string? CursorId { get; set; }
    public int PageSize { get; set; } = 20;
    public string? WorkId { get; set; }
    public string? WorkAssignmentId { get; set; }
    public bool? UnreadOnly { get; set; }
    public List<string>? Types { get; set; }
    public string? Category { get; set; }
    public bool? RequiresAction { get; set; }
    public string? ActionState { get; set; }
}

public sealed class NotificationSearchResponse
{
    public List<NotificationRowDto> Items { get; set; } = new();
    public DateTime? NextCursorOccurredAtUtc { get; set; }
    public string? NextCursorId { get; set; }
    public bool HasMore { get; set; }
    public long UnreadCount { get; set; }
}

public sealed class NotificationUnreadCountResponse
{
    public long UnreadCount { get; set; }
}

public sealed class MarkNotificationsReadRequest
{
    public List<string> Ids { get; set; } = new();
}

public static class NotificationRealtimeChangeKinds
{
    public const string Created = "CREATED";
    public const string Read = "READ";
    public const string ReadMany = "READ_MANY";
    public const string ReadAll = "READ_ALL";
}

public sealed class NotificationRealtimeMessage
{
    public string NotificationId { get; set; } = default!;
    public string Type { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public string ChangeKind { get; set; } = NotificationRealtimeChangeKinds.Created;
}
