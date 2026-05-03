using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
public abstract class DocRoleBase : BaseEntity
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

    [BsonElement("user")]
    public UserRef? User { get; set; }
}

[BsonIgnoreExtraElements]
public abstract class DocRoleReadModelBase : DocRoleBase
{
    [BsonElement("roles")]
    public List<DocRoleType> Roles { get; set; } = new();
}
