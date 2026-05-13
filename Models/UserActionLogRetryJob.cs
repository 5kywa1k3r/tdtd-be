using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("user_action_log_retry_jobs")]
public sealed class UserActionLogRetryJob : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("dedupeKey")]
    public string DedupeKey { get; set; } = string.Empty;

    [BsonElement("action")]
    public string Action { get; set; } = string.Empty;

    [BsonElement("status")]
    public string Status { get; set; } = UserActionLogRetryJobStatuses.Pending;

    [BsonElement("payloadJson")]
    public string PayloadJson { get; set; } = string.Empty;

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

public static class UserActionLogRetryJobStatuses
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string RetryWaiting = "RetryWaiting";
    public const string Completed = "Completed";
    public const string DeadLetter = "DeadLetter";
}
