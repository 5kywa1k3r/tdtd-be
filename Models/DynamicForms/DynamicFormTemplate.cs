using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_form_template")]
public sealed class DynamicFormTemplate : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("code")]
    public string Code { get; set; } = default!;

    [BsonElement("name")]
    public string Name { get; set; } = default!;

    [BsonElement("description")]
    public string? Description { get; set; }

    [BsonElement("labels")]
    public string[] Labels { get; set; } = Array.Empty<string>();

    [BsonElement("createdByUsername")]
    public string CreatedByUsername { get; set; } = default!;

    [BsonElement("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [BsonElement("versionNo")]
    public int VersionNo { get; set; } = 1;

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;

    [BsonElement("isPublished")]
    public bool IsPublished { get; set; }

    [BsonElement("publishedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PublishedAtUtc { get; set; }

    [BsonElement("publishedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? PublishedByUserId { get; set; }

    [BsonElement("sectionsJson")]
    public string SectionsJson { get; set; } = "[]";

    [BsonElement("fieldsJson")]
    public string FieldsJson { get; set; } = "[]";

    [BsonElement("excelBlockJson")]
    public string? ExcelBlockJson { get; set; }

    [BsonElement("excelBlockDynamicExcelTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ExcelBlockDynamicExcelTemplateId { get; set; }
}
