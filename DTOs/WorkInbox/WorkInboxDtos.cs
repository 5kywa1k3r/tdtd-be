using tdtd_be.DTOs.Notifications;

namespace tdtd_be.DTOs.WorkInbox;

public sealed class WorkInboxSearchRequest
{
    public string? WorkId { get; set; }
    public string? AssignmentId { get; set; }
    public string? Function { get; set; }
    public int PageSize { get; set; } = 30;
    public int? CursorPriority { get; set; }
    public DateTime? CursorDueAtUtc { get; set; }
    public string? CursorId { get; set; }
}

public sealed class WorkInboxTarget
{
    public string Kind { get; set; } = "ASSIGNMENT";
    public string WorkId { get; set; } = "";
    public string? AssignmentId { get; set; }
    public string? PeriodId { get; set; }
    public string? ReportId { get; set; }
    public string? AssigneeUserId { get; set; }
    public string? RequestId { get; set; }
    public bool ReadOnly { get; set; }
}

public sealed class WorkInboxItem
{
    public string Id { get; set; } = "";
    public string Function { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public bool CanRequestClone { get; set; }
    public string WorkId { get; set; } = "";
    public string WorkName { get; set; } = "";
    public string? AssignmentId { get; set; }
    public string? AssignmentCode { get; set; }
    public string? AssignmentName { get; set; }
    public string? PeriodKey { get; set; }
    public string? AssigneeName { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public string State { get; set; } = "OPEN";
    public bool RequiresAction { get; set; }
    public bool IsResubmission { get; set; }
    public int Priority { get; set; }
    public DateTime SortDueAtUtc { get; set; }
    public WorkInboxTarget Target { get; set; } = new();
}

public sealed class WorkInboxPage
{
    public List<WorkInboxItem> Items { get; set; } = new();
    public bool HasMore { get; set; }
    public int? NextCursorPriority { get; set; }
    public DateTime? NextCursorDueAtUtc { get; set; }
    public string? NextCursorId { get; set; }
}

public sealed class WorkInboxSummary
{
    public long Total { get; set; }
    public Dictionary<string, long> ByFunction { get; set; } = new();
    public int RecentUnreadCount { get; set; }
    public double? DueSoonHours { get; set; }
    public bool NotifyReviewRequired { get; set; }
}

public sealed class WorkInboxNotification
{
    public NotificationRowDto Notification { get; set; } = new();
    public string? ItemId { get; set; }
}

public sealed class WorkInboxNotificationPage
{
    public List<WorkInboxNotification> Items { get; set; } = new();
    public bool HasMore { get; set; }
    public DateTime? NextCursorOccurredAtUtc { get; set; }
    public string? NextCursorId { get; set; }
    public int RecentUnreadCount { get; set; }
}
