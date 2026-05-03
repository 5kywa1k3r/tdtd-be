using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.Statistics;

[BsonIgnoreExtraElements]
[BsonCollection("work_report_field_stat_values")]
public sealed class WorkReportFieldStatValue : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("workAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkAssignmentId { get; set; } = default!;

    [BsonElement("rootAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? RootAssignmentId { get; set; }

    [BsonElement("ancestorAssignmentIds")]
    [BsonRepresentation(BsonType.ObjectId)]
    public List<string> AncestorAssignmentIds { get; set; } = new();

    [BsonElement("workReportPeriodId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkReportPeriodId { get; set; } = default!;

    [BsonElement("workAssignmentReportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkAssignmentReportId { get; set; } = default!;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormTemplateId { get; set; }

    [BsonElement("dynamicFormTemplateCode")]
    public string? DynamicFormTemplateCode { get; set; }

    [BsonElement("dynamicFormTemplateName")]
    public string? DynamicFormTemplateName { get; set; }

    [BsonElement("fieldId")]
    public string FieldId { get; set; } = default!;

    [BsonElement("fieldKey")]
    public string FieldKey { get; set; } = default!;

    [BsonElement("fieldLabel")]
    public string FieldLabel { get; set; } = default!;

    [BsonElement("fieldType")]
    public string FieldType { get; set; } = default!;

    [BsonElement("showInTree")]
    public bool ShowInTree { get; set; }

    [BsonElement("showInDetail")]
    public bool ShowInDetail { get; set; }

    [BsonElement("bucketKey")]
    public string? BucketKey { get; set; }

    [BsonElement("bucketLabel")]
    public string? BucketLabel { get; set; }

    [BsonElement("sourceKey")]
    public string SourceKey { get; set; } = default!;

    [BsonElement("valueKind")]
    public string ValueKind { get; set; } = default!;

    [BsonElement("numericValue")]
    public decimal? NumericValue { get; set; }

    [BsonElement("booleanValue")]
    public bool? BooleanValue { get; set; }

    [BsonElement("dateValueUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? DateValueUtc { get; set; }

    [BsonElement("periodKey")]
    public string PeriodKey { get; set; } = default!;

    [BsonElement("periodInstanceKey")]
    public string PeriodInstanceKey { get; set; } = default!;

    [BsonElement("periodKind")]
    public string PeriodKind { get; set; } = default!;

    [BsonElement("reportStatus")]
    public int ReportStatus { get; set; }
}
