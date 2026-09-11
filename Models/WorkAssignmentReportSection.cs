using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models.Enums;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("work_assignment_report_sections")]
public sealed class WorkAssignmentReportSection : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("workAssignmentReportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkAssignmentReportId { get; set; } = default!;

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("workAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkAssignmentId { get; set; } = default!;

    [BsonElement("workReportPeriodId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkReportPeriodId { get; set; } = default!;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormTemplateId { get; set; }

    [BsonElement("dynamicFormTemplateCode")]
    public string? DynamicFormTemplateCode { get; set; }

    [BsonElement("dynamicFormTemplateName")]
    public string? DynamicFormTemplateName { get; set; }

    [BsonElement("dynamicFormFamilyId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormFamilyId { get; set; }

    [BsonElement("dynamicFormVersionNo")]
    public int? DynamicFormVersionNo { get; set; }

    [BsonElement("dynamicFormSchemaHash")]
    public string? DynamicFormSchemaHash { get; set; }

    [BsonElement("sectionId")]
    public string SectionId { get; set; } = default!;

    [BsonElement("sectionTitle")]
    public string SectionTitle { get; set; } = default!;

    [BsonElement("sectionOrder")]
    public int SectionOrder { get; set; }

    [BsonElement("status")]
    public WorkAssignmentReportStatus Status { get; set; }

    [BsonElement("fieldValuesJson")]
    public string? FieldValuesJson { get; set; }

    [BsonElement("tableValuesJson")]
    public string? TableValuesJson { get; set; }

    [BsonElement("fieldCount")]
    public int FieldCount { get; set; }

    [BsonElement("blockCount")]
    public int BlockCount { get; set; }

    [BsonElement("hasData")]
    public bool HasData { get; set; }

    [BsonElement("lastUpdatedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastUpdatedAtUtc { get; set; }

    [BsonElement("lastUpdatedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? LastUpdatedByUserId { get; set; }

    [BsonElement("sourcePayloadUpdatedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? SourcePayloadUpdatedAtUtc { get; set; }

    [BsonElement("sourcePayloadRevision")]
    public int SourcePayloadRevision { get; set; }

    [BsonElement("sourcePayloadHash")]
    public string? SourcePayloadHash { get; set; }

    [BsonElement("sourceLifecycleRevision")]
    public int SourceLifecycleRevision { get; set; }

    [BsonElement("dynamicFlowMappingReceiptId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFlowMappingReceiptId { get; set; }

    [BsonElement("dynamicFlowMappingProvenanceId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFlowMappingProvenanceId { get; set; }

    [BsonElement("dynamicFlowMappingProvenanceHash")]
    public string? DynamicFlowMappingProvenanceHash { get; set; }

    [BsonElement("dynamicFlowMappingResultPayloadRevision")]
    public int? DynamicFlowMappingResultPayloadRevision { get; set; }

    [BsonElement("dynamicFlowMappingResultPayloadHash")]
    public string? DynamicFlowMappingResultPayloadHash { get; set; }

    [BsonElement("sourceReportUpdatedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? SourceReportUpdatedAtUtc { get; set; }

    [BsonElement("payloadHash")]
    public string? PayloadHash { get; set; }
}
