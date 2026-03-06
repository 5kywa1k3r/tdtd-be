using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

public enum WorkHistoryType
{
    CREATED = 1,
    UPDATED = 2,
    DELETED = 3,
    // B2/B3 sẽ thêm: ASSIGNED, SUBMITTED, REVIEWED...
}

[BsonIgnoreExtraElements]
public sealed class WorkHistory : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("type")]
    public WorkHistoryType Type { get; set; }

    [BsonElement("atUtc")]
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;

    [BsonElement("byUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ByUserId { get; set; } = default!;

    // payload nhẹ (để sau muốn diff gì thì bỏ)
    [BsonElement("data")]
    public Dictionary<string, object>? Data { get; set; }
}