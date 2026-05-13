using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.Statistics;

[BsonIgnoreExtraElements]
[BsonCollection("work_report_statistic_rebuild_jobs")]
public sealed class WorkReportStatisticRebuildJob : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("dedupeKey")]
    public string DedupeKey { get; set; } = default!;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DynamicFormTemplateId { get; set; } = default!;

    [BsonElement("dynamicFormTemplateCode")]
    public string? DynamicFormTemplateCode { get; set; }

    [BsonElement("dynamicFormTemplateName")]
    public string? DynamicFormTemplateName { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = WorkReportStatisticRebuildJobStatuses.Pending;

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;

    [BsonElement("requestedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string RequestedByUserId { get; set; } = default!;

    [BsonElement("priority")]
    public string Priority { get; set; } = WorkReportStatisticRebuildJobPriorities.Normal;

    [BsonElement("totalReportCount")]
    public long TotalReportCount { get; set; }

    [BsonElement("processedReportCount")]
    public long ProcessedReportCount { get; set; }

    [BsonElement("failedReportCount")]
    public long FailedReportCount { get; set; }

    [BsonElement("lastReportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? LastReportId { get; set; }

    [BsonElement("retryCount")]
    public int RetryCount { get; set; }

    [BsonElement("nextRetryAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? NextRetryAtUtc { get; set; }

    [BsonElement("leaseUntilUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LeaseUntilUtc { get; set; }

    [BsonElement("lastRunAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastRunAtUtc { get; set; }

    [BsonElement("completedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CompletedAtUtc { get; set; }

    [BsonElement("lastErrorType")]
    public string? LastErrorType { get; set; }

    [BsonElement("lastError")]
    public string? LastError { get; set; }

    [BsonElement("lastErrorAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastErrorAtUtc { get; set; }
}

public static class WorkReportStatisticRebuildJobStatuses
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string RetryWaiting = "RETRY_WAITING";
    public const string Completed = "COMPLETED";
    public const string DeadLetter = "DEAD_LETTER";
}

public static class WorkReportStatisticRebuildJobPriorities
{
    public const string Normal = "NORMAL";
    public const string High = "HIGH";
}
