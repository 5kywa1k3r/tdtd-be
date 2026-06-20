using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_form_sections")]
public sealed class DynamicFormSectionDocument : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DynamicFormTemplateId { get; set; } = default!;

    [BsonElement("dynamicFormTemplateCode")]
    public string? DynamicFormTemplateCode { get; set; }

    [BsonElement("dynamicFormTemplateName")]
    public string? DynamicFormTemplateName { get; set; }

    [BsonElement("sectionId")]
    public string SectionId { get; set; } = default!;

    [BsonElement("title")]
    public string Title { get; set; } = default!;

    [BsonElement("description")]
    public string? Description { get; set; }

    [BsonElement("tagCodes")]
    public string[] TagCodes { get; set; } = Array.Empty<string>();

    [BsonElement("order")]
    public int Order { get; set; }

    [BsonElement("schemaVersion")]
    public int SchemaVersion { get; set; }

    [BsonElement("fieldIds")]
    public string[] FieldIds { get; set; } = Array.Empty<string>();

    [BsonElement("blockIds")]
    public string[] BlockIds { get; set; } = Array.Empty<string>();

    [BsonElement("fieldsJson")]
    public string FieldsJson { get; set; } = "[]";

    [BsonElement("blocksJson")]
    public string BlocksJson { get; set; } = "[]";

    [BsonElement("contentHash")]
    public string ContentHash { get; set; } = default!;
}
