using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

public enum WorkStatus
{
    S1 = 1,
    S2 = 2,
    S3 = 3,
    S4 = 4,
    S5 = 5
}

public enum WorkType
{
    TASK = 1,
    INDICATOR = 2
}

public enum WorkPriority
{
    LOW = 1,
    MEDIUM = 2,
    HIGH = 3
}

[BsonIgnoreExtraElements]
public sealed class Work : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("autoCode")]
    public string AutoCode { get; set; } = default!;

    [BsonElement("code")]
    public string? Code { get; set; }

    [BsonElement("name")]
    public string Name { get; set; } = default!;

    [BsonElement("description")]
    public string? Description { get; set; }

    [BsonElement("assignmentBasis")]
    public string? AssignmentBasis { get; set; }

    [BsonElement("status")]
    public WorkStatus Status { get; set; } = WorkStatus.S1;

    [BsonElement("leaderDirectiveUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string LeaderDirectiveUserId { get; set; } = default!;

    [BsonElement("leaderWatchUserIds")]
    [BsonRepresentation(BsonType.ObjectId)]
    public List<string> LeaderWatchUserIds { get; set; } = new();

    [BsonElement("startDate")]
    public DateTime? StartDate { get; set; }

    [BsonElement("endDate")]
    public DateTime? EndDate { get; set; }

    [BsonElement("dueDate")]
    public DateTime? DueDate { get; set; }

    [BsonElement("attachmentCount")]
    public int AttachmentCount { get; set; } = 0;

    [BsonElement("type")]
    public WorkType Type { get; set; } = WorkType.TASK;

    [BsonElement("priority")]
    public WorkPriority Priority { get; set; } = WorkPriority.MEDIUM;

    // snapshot
    [BsonElement("owner")]
    public UserRef? Owner { get; set; }

    [BsonElement("leaderDirective")]
    public UserRef? LeaderDirective { get; set; }

    [BsonElement("leaderWatch")]
    public List<UserRef> LeaderWatch { get; set; } = new();
}