using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

public static class UserNotificationTypes
{
    public const string WorkDue = "WORK_DUE";
    public const string AssignmentDue = "ASSIGNMENT_DUE";
    public const string ReportDue = "REPORT_DUE";
    public const string AssignmentAssigned = "ASSIGNMENT_ASSIGNED";
    public const string AssignmentHandoverReceived = "ASSIGNMENT_HANDOVER_RECEIVED";
    public const string AssignmentHandoverCompleted = "ASSIGNMENT_HANDOVER_COMPLETED";
    public const string AssignmentHandoverRequested = "ASSIGNMENT_HANDOVER_REQUESTED";
    public const string AssignmentHandoverApproved = "ASSIGNMENT_HANDOVER_APPROVED";
    public const string AssignmentHandoverRejected = "ASSIGNMENT_HANDOVER_REJECTED";
    public const string DynamicFormCloneRequested = "DYNAMIC_FORM_CLONE_REQUESTED";
    public const string DynamicFormCloneApproved = "DYNAMIC_FORM_CLONE_APPROVED";
    public const string DynamicFormCloneRejected = "DYNAMIC_FORM_CLONE_REJECTED";
    public const string ReportReviewRequired = "REPORT_REVIEW_REQUIRED";
    public const string AssignmentEvaluationRequired = "ASSIGNMENT_EVALUATION_REQUIRED";
    public const string AssignmentAtRisk = "ASSIGNMENT_AT_RISK";
    public const string AssignmentOverdue = "ASSIGNMENT_OVERDUE";
    public const string ReportPeriodAtRisk = "REPORT_PERIOD_AT_RISK";
    public const string ReportPeriodOverdue = "REPORT_PERIOD_OVERDUE";
    public const string DynamicFormStatisticRebuild = "DYNAMIC_FORM_STATISTIC_REBUILD";
    public const string BasicSummaryRefresh = "BASIC_SUMMARY_REFRESH";
    public const string AdvancedSummaryPreview = "ADVANCED_SUMMARY_PREVIEW";
}

public static class UserNotificationSeverities
{
    public const string Info = "INFO";
    public const string Warning = "WARNING";
    public const string Due = "DUE";
}

public static class UserNotificationCategories
{
    public const string General = "GENERAL";
    public const string Handover = "HANDOVER";
    public const string Approval = "APPROVAL";
    public const string Report = "REPORT";
    public const string Status = "STATUS";
}

public static class UserNotificationActionStates
{
    public const string Open = "OPEN";
    public const string Resolved = "RESOLVED";
    public const string Dismissed = "DISMISSED";
}

[BsonIgnoreExtraElements]
[BsonCollection("notifications")]
public sealed class UserNotification : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("recipientUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string RecipientUserId { get; set; } = default!;

    [BsonElement("type")]
    public string Type { get; set; } = string.Empty;

    [BsonElement("severity")]
    public string Severity { get; set; } = UserNotificationSeverities.Info;

    [BsonElement("title")]
    public string Title { get; set; } = string.Empty;

    [BsonElement("body")]
    public string? Body { get; set; }

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? WorkId { get; set; }

    [BsonElement("workType")]
    public WorkType? WorkType { get; set; }

    [BsonElement("workName")]
    public string? WorkName { get; set; }

    [BsonElement("workAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? WorkAssignmentId { get; set; }

    [BsonElement("assignmentCode")]
    public string? AssignmentCode { get; set; }

    [BsonElement("workReportPeriodId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? WorkReportPeriodId { get; set; }

    [BsonElement("workAssignmentReportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? WorkAssignmentReportId { get; set; }

    [BsonElement("category")]
    public string? Category { get; set; }

    [BsonElement("requiresAction")]
    public bool RequiresAction { get; set; }

    [BsonElement("actionState")]
    public string? ActionState { get; set; }

    [BsonElement("sourceEntityType")]
    public string? SourceEntityType { get; set; }

    [BsonElement("sourceEntityId")]
    public string? SourceEntityId { get; set; }

    [BsonElement("requestId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? RequestId { get; set; }

    [BsonElement("actionUrl")]
    public string? ActionUrl { get; set; }

    [BsonElement("resolvedAtUtc")]
    public DateTime? ResolvedAtUtc { get; set; }

    [BsonElement("actorUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ActorUserId { get; set; }

    [BsonElement("sourceUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? SourceUserId { get; set; }

    [BsonElement("targetUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? TargetUserId { get; set; }

    [BsonElement("dueAtUtc")]
    public DateTime? DueAtUtc { get; set; }

    [BsonElement("occurredAtUtc")]
    public DateTime OccurredAtUtc { get; set; }

    [BsonElement("readAtUtc")]
    public DateTime? ReadAtUtc { get; set; }

    [BsonElement("clickedAtUtc")]
    public DateTime? ClickedAtUtc { get; set; }

    [BsonElement("eventKey")]
    public string EventKey { get; set; } = string.Empty;
}
