using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("docrole_read_model_projection_retry_jobs")]
public sealed class DocRoleReadModelProjectionRetryJob : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("dedupeKey")]
    public string DedupeKey { get; set; } = string.Empty;

    [BsonElement("action")]
    public string Action { get; set; } = string.Empty;

    [BsonElement("status")]
    public string Status { get; set; } = DocRoleProjectionRetryJobStatuses.Pending;

    [BsonElement("workId")]
    public string? WorkId { get; set; }

    [BsonElement("assignmentId")]
    public string? AssignmentId { get; set; }

    [BsonElement("workReportPeriodId")]
    public string? WorkReportPeriodId { get; set; }

    [BsonElement("dynamicExcelId")]
    public string? DynamicExcelId { get; set; }

    [BsonElement("dynamicFormTemplateId")]
    public string? DynamicFormTemplateId { get; set; }

    [BsonElement("userId")]
    public string? UserId { get; set; }

    [BsonElement("docType")]
    public DocType? DocType { get; set; }

    [BsonElement("docId")]
    public string? DocId { get; set; }

    [BsonElement("byUserId")]
    public string ByUserId { get; set; } = "system";

    [BsonElement("reason")]
    public string? Reason { get; set; }

    [BsonElement("retryCount")]
    public int RetryCount { get; set; }

    [BsonElement("nextRetryAtUtc")]
    public DateTime? NextRetryAtUtc { get; set; }

    [BsonElement("leaseUntilUtc")]
    public DateTime? LeaseUntilUtc { get; set; }

    [BsonElement("lastRunAtUtc")]
    public DateTime? LastRunAtUtc { get; set; }

    [BsonElement("completedAtUtc")]
    public DateTime? CompletedAtUtc { get; set; }

    [BsonElement("lastErrorType")]
    public string? LastErrorType { get; set; }

    [BsonElement("lastError")]
    public string? LastError { get; set; }

    [BsonElement("lastErrorAtUtc")]
    public DateTime? LastErrorAtUtc { get; set; }

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;
}

public static class DocRoleProjectionRetryActions
{
    public const string RebuildWork = "REBUILD_WORK";
    public const string RebuildWorkAssignments = "REBUILD_WORK_ASSIGNMENTS";
    public const string RebuildAssignment = "REBUILD_ASSIGNMENT";
    public const string RebuildWorkReportPeriods = "REBUILD_WORK_REPORT_PERIODS";
    public const string RebuildReportPeriod = "REBUILD_REPORT_PERIOD";
    public const string RebuildMyReportTemplate = "REBUILD_MY_REPORT_TEMPLATE";
    public const string SoftDeleteDoc = "SOFT_DELETE_DOC";
}

public static class DocRoleProjectionRetryJobStatuses
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string RetryWaiting = "RetryWaiting";
    public const string Completed = "Completed";
    public const string DeadLetter = "DeadLetter";
}
