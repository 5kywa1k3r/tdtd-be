using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

public sealed class EvaluationTemplate: BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("representativeCode")]
    public string RepresentativeCode { get; set; } = string.Empty;
    [BsonElement("representativeLabel")]
    public string RepresentativeLabel { get; set; } = string.Empty;
    [BsonElement("items")]
    public List<EvaluationTemplateItem> Items { get; set; } = new();
    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;
    [BsonElement("unitCodeScope")]
    public string? UnitCodeScope { get; set; }
}
