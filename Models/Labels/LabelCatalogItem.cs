using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("labels")]
public sealed class LabelCatalogItem : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("code")]
    public string Code { get; set; } = default!;

    [BsonElement("name")]
    public string Name { get; set; } = default!;

    [BsonElement("nameLower")]
    public string NameLower { get; set; } = default!;

    [BsonElement("description")]
    public string? Description { get; set; }

    [BsonElement("color")]
    public string? Color { get; set; }

    [BsonElement("groupCode")]
    public string? GroupCode { get; set; }

    [BsonElement("scopeType")]
    public string ScopeType { get; set; } = LabelScopeTypes.Global;

    [BsonElement("scopeId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ScopeId { get; set; }

    [BsonElement("managedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ManagedByUserId { get; set; }

    [BsonElement("isSystem")]
    public bool IsSystem { get; set; }

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;
}

public static class LabelScopeTypes
{
    public const string Global = "GLOBAL";
    public const string Level = "LEVEL";
    public const string Unit = "UNIT";
}
