using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_form_templates")]
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

    [BsonElement("tagCodes")]
    public string[] TagCodes { get; set; } = Array.Empty<string>();

    [BsonElement("createdByUsername")]
    public string CreatedByUsername { get; set; } = default!;

    [BsonElement("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [BsonElement("versionNo")]
    public int VersionNo { get; set; } = 1;

    [BsonElement("familyId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? FamilyId { get; set; }

    [BsonElement("previousVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? PreviousVersionId { get; set; }

    [BsonElement("clonedFromVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ClonedFromVersionId { get; set; }

    [BsonElement("lineageStatus")]
    public string? LineageStatus { get; set; }

    [BsonElement("wrapReuseKey")]
    public string? WrapReuseKey { get; set; }

    [BsonElement("revision")]
    public int Revision { get; set; } = 1;

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

    [BsonElement("publishedSchemaSnapshotJson")]
    public string? PublishedSchemaSnapshotJson { get; set; }

    [BsonElement("publishedSchemaHash")]
    public string? PublishedSchemaHash { get; set; }

    [BsonElement("sectionsJson")]
    public string SectionsJson { get; set; } = "[]";

    [BsonElement("fieldsJson")]
    public string FieldsJson { get; set; } = "[]";

    [BsonElement("excelBlockJson")]
    public string? ExcelBlockJson { get; set; }

    [BsonElement("blocksJson")]
    public string BlocksJson { get; set; } = "[]";

    [BsonElement("excelBlockDynamicExcelTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ExcelBlockDynamicExcelTemplateId { get; set; }

    [BsonElement("statisticConfigUpdatedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? StatisticConfigUpdatedAtUtc { get; set; }

    [BsonElement("statisticConfigUpdatedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? StatisticConfigUpdatedByUserId { get; set; }

    [BsonElement("statisticConfigUpdateMonthKey")]
    public string? StatisticConfigUpdateMonthKey { get; set; }

    [BsonElement("statisticConfigId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? StatisticConfigId { get; set; }

    [BsonElement("statisticConfigVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? StatisticConfigVersionId { get; set; }

    [BsonElement("statisticConfigPreviousVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? StatisticConfigPreviousVersionId { get; set; }

    [BsonElement("statisticConfigVersionNo")]
    public int StatisticConfigVersionNo { get; set; }

    [BsonElement("statisticConfigRevision")]
    public long StatisticConfigRevision { get; set; }

    [BsonElement("statisticConfigStatus")]
    public string? StatisticConfigStatus { get; set; }

    [BsonElement("statisticConfigHash")]
    public string? StatisticConfigHash { get; set; }

    [BsonElement("statisticConfigDependencyPins")]
    public List<string> StatisticConfigDependencyPins { get; set; } = new();

    [BsonElement("statisticConfigSections")]
    public DynamicFormStatisticConfigSections StatisticConfigSections { get; set; } = new();

    [BsonElement("statisticConfigSnapshots")]
    public List<DynamicFormStatisticConfigVersionSnapshot> StatisticConfigSnapshots { get; set; } = new();
}

[BsonIgnoreExtraElements]
public sealed class DynamicFormStatisticConfigSections
{
    [BsonElement("fieldSectionJson")]
    public string FieldSectionJson { get; set; } = "[]";

    [BsonElement("tableSectionJson")]
    public string TableSectionJson { get; set; } = "[]";
}

[BsonIgnoreExtraElements]
public sealed class DynamicFormStatisticConfigVersionSnapshot
{
    [BsonElement("versionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string VersionId { get; set; } = default!;

    [BsonElement("previousVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? PreviousVersionId { get; set; }

    [BsonElement("versionNo")]
    public int VersionNo { get; set; }

    [BsonElement("revision")]
    public long Revision { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = string.Empty;

    [BsonElement("configHash")]
    public string ConfigHash { get; set; } = string.Empty;

    [BsonElement("dependencyPins")]
    public List<string> DependencyPins { get; set; } = new();

    [BsonElement("sections")]
    public DynamicFormStatisticConfigSections Sections { get; set; } = new();

    [BsonElement("createdAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; }

    [BsonElement("createdByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? CreatedByUserId { get; set; }
}

public static class DynamicFormLineageStatuses
{
    public const string Root = "ROOT";
    public const string Version = "VERSION";
    public const string Clone = "CLONE";
    public const string Wrapped = "WRAPPED";
    public const string Legacy = "LEGACY";
}
