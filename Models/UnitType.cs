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

    [BsonElement("positionRules")]
    public List<UnitTypePositionRule> PositionRules { get; set; } = new();

    [BsonElement("version")]
    public int Version { get; set; } = 1;
}

[BsonIgnoreExtraElements]
public sealed class UnitTypePositionRule
{
    [BsonElement("positionCode")]
    public string PositionCode { get; set; } = default!;

    [BsonElement("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    [BsonElement("maxUsersPerUnit")]
    public int? MaxUsersPerUnit { get; set; }

    [BsonElement("sortOrder")]
    public int SortOrder { get; set; }
}
