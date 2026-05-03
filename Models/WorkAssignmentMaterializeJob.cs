using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonCollection("work_assignment_materialize_jobs")]
public sealed class WorkAssignmentMaterializeJobs
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]

    public string WorkId { get; set; } = default!;

    [BsonElement("workAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkAssignmentId { get; set; } = default!;

    [BsonElement("status")]
    public string Status { get; set; } = MaterializeJobStatuses.Pending;

    [BsonElement("retryCount")]
    public int RetryCount { get; set; }

    [BsonElement("nextRetryAtUtc")]
    public DateTime? NextRetryAtUtc { get; set; }

    [BsonElement("leaseUntilUtc")]
    public DateTime? LeaseUntilUtc { get; set; }
    [BsonElement("lastHeartbeatAtUtc")]
    public DateTime? LastHeartbeatAtUtc { get; set; }
    [BsonElement("lastRunAtUtc")]
    public DateTime? LastRunAtUtc { get; set; }
    [BsonElement("completedAtUtc")]
    public DateTime? CompletedAtUtc { get; set; }
    [BsonElement("lastError")]

    public string? LastError { get; set; }
    [BsonElement("cursorAssigneeIndex")]

    public int CursorAssigneeIndex { get; set; }
    [BsonElement("cursorDueIndex")]
    public int CursorDueIndex { get; set; }
    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;
    [BsonElement("isDeleted")]
    public bool IsDeleted { get; set; }
    [BsonElement("createdAtUtc")]

    public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }
    [BsonElement("createdByUserId")]
    public string? CreatedByUserId { get; set; }
    [BsonElement("updatedByUserId")]
    public string? UpdatedByUserId { get; set; }
}

public static class MaterializeJobStatuses
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string RetryWaiting = "RetryWaiting";
    public const string Completed = "Completed";
    public const string DeadLetter = "DeadLetter";
}