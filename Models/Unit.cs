using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
public sealed class Unit : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("fullName")]
    public string FullName { get; set; } = default!;

    [BsonElement("code")]
    public string? Code { get; set; }

    [BsonElement("shortName")]
    public string? ShortName { get; set; }

    [BsonElement("symbol")]
    public string? Symbol { get; set; }

    [BsonElement("level")]
    public int Level { get; set; } // derived from Code

    [BsonElement("version")]
    public int Version { get; set; } = 1;

    [BsonElement("unitTypeCodes")]
    public List<string> UnitTypeCodes { get; set; } = new();

    [BsonElement("parentUnitId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ParentUnitId { get; set; }
}