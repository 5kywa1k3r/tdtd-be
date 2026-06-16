using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("label_enum_catalogs")]
public sealed class LabelEnumCatalog : BaseEntity
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

    [BsonElement("scopeType")]
    public string ScopeType { get; set; } = LabelScopeTypes.Global;

    [BsonElement("scopeId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ScopeId { get; set; }

    [BsonElement("scopeUnitCode")]
    public string? ScopeUnitCode { get; set; }

    [BsonElement("scopeLevel")]
    public int? ScopeLevel { get; set; }

    [BsonElement("createdByUsername")]
    public string CreatedByUsername { get; set; } = default!;

    [BsonElement("createdByAccountKind")]
    public string? CreatedByAccountKind { get; set; }

    [BsonElement("optionsRevision")]
    public int OptionsRevision { get; set; } = 1;

    [BsonElement("options")]
    public List<LabelEnumOption> Options { get; set; } = new();

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;
}

[BsonIgnoreExtraElements]
[BsonCollection("label_enum_option_read_models")]
public sealed class LabelEnumOptionReadModel : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    [BsonElement("catalogId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string CatalogId { get; set; } = default!;

    [BsonElement("catalogCode")]
    public string CatalogCode { get; set; } = default!;

    [BsonElement("catalogName")]
    public string CatalogName { get; set; } = default!;

    [BsonElement("scopeType")]
    public string ScopeType { get; set; } = LabelScopeTypes.Global;

    [BsonElement("scopeId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ScopeId { get; set; }

    [BsonElement("scopeUnitCode")]
    public string? ScopeUnitCode { get; set; }

    [BsonElement("scopeLevel")]
    public int? ScopeLevel { get; set; }

    [BsonElement("code")]
    public string Code { get; set; } = default!;

    [BsonElement("label")]
    public string Label { get; set; } = default!;

    [BsonElement("labelLower")]
    public string LabelLower { get; set; } = default!;

    [BsonElement("searchText")]
    public string SearchText { get; set; } = default!;

    [BsonElement("order")]
    public int Order { get; set; }

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;
}

public sealed class LabelEnumOption
{
    [BsonElement("code")]
    public string Code { get; set; } = default!;

    [BsonElement("label")]
    public string Label { get; set; } = default!;

    [BsonElement("order")]
    public int Order { get; set; }

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;
}
