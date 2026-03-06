using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

public enum DocType
{
    WORK = 1,
    WORK_ASSIGNMENT = 2,
    WORK_REPORT = 3
}

public enum DocRoleType
{
    OWNER = 1,
    LEADER_DIRECTIVE = 2,
    LEADER_WATCH = 3,

    ASSIGNEE = 10,
    ASSIGNER = 11,
    ASSIGNMENT_LEADER_WATCH = 13,

    WORK_PARTICIPANT = 20
}

[BsonIgnoreExtraElements]
public sealed class DocRole : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("docType")]
    public DocType DocType { get; set; }

    [BsonElement("docId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DocId { get; set; } = default!;

    [BsonElement("userId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string UserId { get; set; } = default!;

    [BsonElement("role")]
    public DocRoleType Role { get; set; }

    [BsonElement("user")]
    public UserRef? User { get; set; }
}