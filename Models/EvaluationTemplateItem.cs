using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

public sealed class EvaluationTemplateItem
{
    [BsonElement("code")]
    public string Code { get; set; } = string.Empty;
    [BsonElement("label")]
    public string Label { get; set; } = string.Empty;
    [BsonElement("order")]
    public int Order { get; set; }
    [BsonElement("isActive")]
    public bool? IsActive { get; set; } = true;
}
