using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
public sealed class UserRef
{
    [BsonElement("userId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string UserId { get; set; } = default!;

    [BsonElement("username")]
    public string? Username { get; set; }

    [BsonElement("fullName")]
    public string? FullName { get; set; }

    [BsonElement("unitId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? UnitId { get; set; }

    [BsonElement("unitSymbol")]
    public string? UnitSymbol { get; set; }

    [BsonElement("unitShortName")]
    public string? UnitShortName { get; set; }

    [BsonElement("unitName")]
    public string? UnitName { get; set; }

    [BsonElement("positionCode")]
    public string? PositionCode { get; set; }

    [BsonElement("positionName")]
    public string? PositionName { get; set; }
}