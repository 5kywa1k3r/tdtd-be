using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
public sealed class UnitType : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("code")]
    public string Code { get; set; } = default!; // unique

    [BsonElement("name")]
    public string Name { get; set; } = default!;

    [BsonElement("version")]
    public int Version { get; set; } = 1;
}