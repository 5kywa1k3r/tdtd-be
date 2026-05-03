using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
public sealed class UnitVersionHistory : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("unitId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string UnitId { get; set; } = default!;

    [BsonElement("version")]
    public int Version { get; set; }

    [BsonElement("fullName")]
    public string FullName { get; set; } = default!;

    [BsonElement("code")]
    public string Code { get; set; } = default!;

    [BsonElement("symbol")]
    public string? Symbol { get; set; }

    [BsonElement("shortName")]
    public string? ShortName { get; set; }

    [BsonElement("level")]
    public int Level { get; set; }

    [BsonElement("parentUnitId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ParentUnitId { get; set; }

    [BsonElement("unitTypeCodes")]
    public List<string> UnitTypeCodes { get; set; } = new();

    [BsonElement("primaryUnitTypeCode")]
    public string? PrimaryUnitTypeCode { get; set; }

    [BsonElement("isVirtual")]
    public bool IsVirtual { get; set; } = false;
}
