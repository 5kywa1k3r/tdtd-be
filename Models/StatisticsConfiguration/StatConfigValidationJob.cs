using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.StatisticsConfiguration;

[BsonIgnoreExtraElements]
[BsonCollection("stat_config_validation_jobs")]
public sealed class StatConfigValidationJob : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("queueName")]
    public string QueueName { get; set; } = StatConfigValidationQueue.Name;

    [BsonElement("dedupeKey")]
    public string DedupeKey { get; set; } = string.Empty;

    [BsonElement("ownerKind")]
    public string OwnerKind { get; set; } = string.Empty;

    [BsonElement("ownerId")]
    public string OwnerId { get; set; } = string.Empty;

    [BsonElement("configId")]
    public string ConfigId { get; set; } = string.Empty;

    [BsonElement("versionId")]
    public string VersionId { get; set; } = string.Empty;

    [BsonElement("versionNo")]
    public int VersionNo { get; set; }

    [BsonElement("configRevision")]
    public long ConfigRevision { get; set; }

    [BsonElement("configHash")]
    public string ConfigHash { get; set; } = string.Empty;

    [BsonElement("dependencyPins")]
    public List<string> DependencyPins { get; set; } = new();

    [BsonElement("dependencyPinsHash")]
    public string DependencyPinsHash { get; set; } = string.Empty;

    [BsonElement("bundleHash")]
    public string BundleHash { get; set; } = string.Empty;

    [BsonElement("enqueueCommandId")]
    public string EnqueueCommandId { get; set; } = string.Empty;

    [BsonElement("commandReceiptId")]
    public string CommandReceiptId { get; set; } = string.Empty;

    [BsonElement("auditOutboxId")]
    public string AuditOutboxId { get; set; } = string.Empty;

    [BsonElement("status")]
    public string Status { get; set; } = StatConfigValidationJobStatuses.Pending;

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;

    [BsonElement("stateRevision")]
    public long StateRevision { get; set; }

    [BsonElement("stateHash")]
    public string StateHash { get; set; } = string.Empty;

    [BsonElement("requestedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string RequestedByUserId { get; set; } = default!;

    [BsonElement("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [BsonElement("retryCount")]
    public int RetryCount { get; set; }

    [BsonElement("maxRetryCount")]
    public int MaxRetryCount { get; set; }

    [BsonElement("nextRetryAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? NextRetryAtUtc { get; set; }

    [BsonElement("claimToken")]
    public string? ClaimToken { get; set; }

    [BsonElement("leaseOwnerId")]
    public string? LeaseOwnerId { get; set; }

    [BsonElement("leaseUntilUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LeaseUntilUtc { get; set; }

    [BsonElement("lastHeartbeatAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastHeartbeatAtUtc { get; set; }

    [BsonElement("lastRunAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastRunAtUtc { get; set; }

    [BsonElement("completedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CompletedAtUtc { get; set; }

    [BsonElement("failedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? FailedAtUtc { get; set; }

    [BsonElement("deadLetterAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? DeadLetterAtUtc { get; set; }

    [BsonElement("cancelledAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CancelledAtUtc { get; set; }

    [BsonElement("cancelledByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? CancelledByUserId { get; set; }

    [BsonElement("resetAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ResetAtUtc { get; set; }

    [BsonElement("resetByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ResetByUserId { get; set; }

    [BsonElement("resetCount")]
    public int ResetCount { get; set; }

    [BsonElement("safeCode")]
    public string? SafeCode { get; set; }

    [BsonElement("safeMessage")]
    public string? SafeMessage { get; set; }

    [BsonElement("diagnosticCode")]
    public string? DiagnosticCode { get; set; }

    [BsonElement("diagnosticMessage")]
    public string? DiagnosticMessage { get; set; }

    [BsonElement("failureFingerprint")]
    public string? FailureFingerprint { get; set; }

    [BsonElement("expiresAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ExpiresAtUtc { get; set; }
}

[BsonIgnoreExtraElements]
[BsonCollection("stat_config_audit_outbox")]
public sealed class StatConfigAuditOutboxItem : BaseEntity
{
    [BsonId]
    public string Id { get; set; } = default!;

    [BsonElement("dedupeKey")]
    public string DedupeKey { get; set; } = string.Empty;

    [BsonElement("ownerKind")]
    public string OwnerKind { get; set; } = string.Empty;

    [BsonElement("ownerId")]
    public string OwnerId { get; set; } = string.Empty;

    [BsonElement("commandKind")]
    public string CommandKind { get; set; } = string.Empty;

    [BsonElement("commandId")]
    public string CommandId { get; set; } = string.Empty;

    [BsonElement("receiptId")]
    public string ReceiptId { get; set; } = string.Empty;

    [BsonElement("targetKind")]
    public string TargetKind { get; set; } = string.Empty;

    [BsonElement("targetId")]
    public string TargetId { get; set; } = string.Empty;

    [BsonElement("configId")]
    public string? ConfigId { get; set; }

    [BsonElement("versionId")]
    public string? VersionId { get; set; }

    [BsonElement("versionNo")]
    public int? VersionNo { get; set; }

    [BsonElement("configRevision")]
    public long? ConfigRevision { get; set; }

    [BsonElement("configHash")]
    public string? ConfigHash { get; set; }

    [BsonElement("stateRevision")]
    public long? StateRevision { get; set; }

    [BsonElement("stateHash")]
    public string? StateHash { get; set; }

    [BsonElement("eventKind")]
    public string EventKind { get; set; } = string.Empty;

    [BsonElement("eventVersion")]
    public int EventVersion { get; set; } = 1;

    [BsonElement("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [BsonElement("payloadHash")]
    public string PayloadHash { get; set; } = string.Empty;

    [BsonElement("safeSummary")]
    public string SafeSummary { get; set; } = string.Empty;

    [BsonElement("status")]
    public string Status { get; set; } = StatConfigAuditOutboxStatuses.Pending;

    [BsonElement("attemptCount")]
    public int AttemptCount { get; set; }

    [BsonElement("nextAttemptAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? NextAttemptAtUtc { get; set; }

    [BsonElement("leaseUntilUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LeaseUntilUtc { get; set; }

    [BsonElement("completedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CompletedAtUtc { get; set; }

    [BsonElement("errorCode")]
    public string? ErrorCode { get; set; }

    [BsonElement("expiresAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ExpiresAtUtc { get; set; }
}

public static class StatConfigValidationQueue
{
    public const string Name = "stat-config-readiness";
}

public static class StatConfigValidationJobStatuses
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string RetryWaiting = "RETRY_WAITING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
    public const string Reset = "RESET";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        new[] { Pending, Running, RetryWaiting, Completed, Failed, Cancelled, Reset },
        StringComparer.Ordinal);
}

public static class StatConfigAuditOutboxStatuses
{
    public const string Pending = "PENDING";
    public const string Processing = "PROCESSING";
    public const string Completed = "COMPLETED";
    public const string RetryWaiting = "RETRY_WAITING";
    public const string Failed = "FAILED";
}
