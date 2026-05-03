using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
public sealed class AppUser : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("username")]
    public string Username { get; set; } = default!; // lowercase

    [BsonElement("passwordHash")]
    public string PasswordHash { get; set; } = default!;

    [BsonElement("fullName")]
    public string FullName { get; set; } = default!;

    [BsonElement("unitId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? UnitId { get; set; }

    [BsonElement("positionCode")]
    public string? PositionCode { get; set; }

    [BsonElement("accountKind")]
    public string? AccountKind { get; set; }

    [BsonElement("roles")]
    public List<string> Roles { get; set; } = new();
}
