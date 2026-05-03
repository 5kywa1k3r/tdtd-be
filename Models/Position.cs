using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
public sealed class Position : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("code")]
    public string Code { get; set; } = default!;

    [BsonElement("name")]
    public string Name { get; set; } = default!;

    [BsonElement("order")]
    public int Order { get; set; }

    [BsonElement("rank")]
    public int Rank { get; set; }

    [BsonElement("unitTypeCodes")]
    public List<string> UnitTypeCodes { get; set; } = new();

    [BsonElement("version")]
    public int Version { get; set; } = 1;
}
