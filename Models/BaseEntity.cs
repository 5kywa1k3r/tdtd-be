using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
public abstract class BaseEntity
{
    // Aggregate transactions reserve the same business rows used for authorization.
    // Keep the fence on typed reads/replacements; ignoring it breaks binding readback.
    [BsonElement("aggregateCommitFence")]
    [BsonIgnoreIfDefault]
    [System.Text.Json.Serialization.JsonIgnore]
    public long AggregateCommitFence { get; set; }

    [BsonElement("createdByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? CreatedByUserId { get; set; }

    [BsonElement("updatedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? UpdatedByUserId { get; set; }

    [BsonElement("createdAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [BsonElement("updatedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [BsonElement("note")]
    public string? Note { get; set; }
    [BsonElement("isDeleted")]
    public bool IsDeleted { get; set; } = false;
    [BsonElement("deletedAtUtc")]
    public DateTime? DeletedAtUtc { get; set; }

    [BsonElement("deletedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DeletedByUserId { get; set; }
}
