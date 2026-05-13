using tdtd_be.Models;

namespace tdtd_be.Services.Notifications;

public sealed class NotificationCommand
{
    public string RecipientUserId { get; set; } = default!;
    public string Type { get; set; } = string.Empty;
    public string Severity { get; set; } = UserNotificationSeverities.Info;
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
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public string EventKey { get; set; } = default!;
}
