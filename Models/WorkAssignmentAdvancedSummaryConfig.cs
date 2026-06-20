using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("work_assignment_advanced_summary_configs")]
public sealed class WorkAssignmentAdvancedSummaryConfig : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("assignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string AssignmentId { get; set; } = default!;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DynamicFormTemplateId { get; set; } = default!;

    [BsonElement("sectionId")]
    public string SectionId { get; set; } = default!;

    [BsonElement("sectionTitle")]
    public string? SectionTitle { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = WorkAssignmentAdvancedSummaryConfigStatuses.Draft;

    [BsonElement("versionNo")]
    public int VersionNo { get; set; } = 1;

    [BsonElement("draftRevision")]
    public int DraftRevision { get; set; } = 1;

    [BsonElement("configJson")]
    public string ConfigJson { get; set; } = "{}";

    [BsonElement("configHash")]
    public string ConfigHash { get; set; } = string.Empty;

    [BsonElement("previewStatus")]
    public string PreviewStatus { get; set; } = WorkAssignmentAdvancedSummaryPreviewStatuses.NotRequested;

    [BsonElement("previewJobId")]
    public string? PreviewJobId { get; set; }

    [BsonElement("previewCorrelationId")]
    public string? PreviewCorrelationId { get; set; }

    [BsonElement("previewPeriodKeys")]
    public List<string> PreviewPeriodKeys { get; set; } = new();

    [BsonElement("previewResultJson")]
    public string? PreviewResultJson { get; set; }

    [BsonElement("previewError")]
    public string? PreviewError { get; set; }

    [BsonElement("previewRequestedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PreviewRequestedAtUtc { get; set; }

    [BsonElement("previewFinishedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PreviewFinishedAtUtc { get; set; }

    [BsonElement("lockedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LockedAtUtc { get; set; }

    [BsonElement("lockedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? LockedByUserId { get; set; }

    [BsonElement("lockTokenId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? LockTokenId { get; set; }
}

public static class WorkAssignmentAdvancedSummaryConfigStatuses
{
    public const string Draft = "DRAFT";
    public const string Locked = "LOCKED";
    public const string Archived = "ARCHIVED";
}

public static class WorkAssignmentAdvancedSummaryPreviewStatuses
{
    public const string NotRequested = "NOT_REQUESTED";
    public const string Queued = "QUEUED";
    public const string Running = "RUNNING";
    public const string Done = "DONE";
    public const string Failed = "FAILED";
}
