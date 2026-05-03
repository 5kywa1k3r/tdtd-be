using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("work_assignment_queue")]
public sealed class WorkAssignmentQueueItem : BaseEntity
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

    [BsonElement("assigneeUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string AssigneeUserId { get; set; } = default!;

    [BsonElement("periodKey")]
    public string PeriodKey { get; set; } = default!;

    [BsonElement("dueAtUtc")]
    public DateTime? DueAtUtc { get; set; }

    [BsonElement("nextScanAtUtc")]
    public DateTime NextScanAtUtc { get; set; }

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;

    [BsonElement("lastScannedAtUtc")]
    public DateTime? LastScannedAtUtc { get; set; }

    [BsonElement("lastObservedPeriodStatus")]
    public int? LastObservedPeriodStatus { get; set; }
}
