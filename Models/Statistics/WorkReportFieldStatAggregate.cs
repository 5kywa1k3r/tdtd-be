using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.Statistics;

[BsonIgnoreExtraElements]
[BsonCollection("work_report_field_stat_aggregates")]
public sealed class WorkReportFieldStatAggregate : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("scopeType")]
    public string ScopeType { get; set; } = default!;

    [BsonElement("scopeId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ScopeId { get; set; } = default!;

    [BsonElement("rootAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? RootAssignmentId { get; set; }

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

    [BsonElement("periodKey")]
    public string PeriodKey { get; set; } = default!;

    [BsonElement("periodInstanceKey")]
    public string PeriodInstanceKey { get; set; } = default!;

    [BsonElement("periodKind")]
    public string PeriodKind { get; set; } = default!;

    [BsonElement("periodAnchorDate")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PeriodAnchorDate { get; set; }

    [BsonElement("periodStartDate")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PeriodStartDate { get; set; }

    [BsonElement("periodEndDate")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PeriodEndDate { get; set; }

    [BsonElement("completedDate")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CompletedDate { get; set; }

    [BsonElement("isHistoricalData")]
    public bool IsHistoricalData { get; set; }

    [BsonElement("reportStatus")]
    public int ReportStatus { get; set; }

    [BsonElement("valueCount")]
    public long ValueCount { get; set; }

    [BsonElement("numericValueCount")]
    public long NumericValueCount { get; set; }

    [BsonElement("sum")]
    public decimal Sum { get; set; }

    [BsonElement("min")]
    public decimal? Min { get; set; }

    [BsonElement("max")]
    public decimal? Max { get; set; }

    [BsonElement("trueCount")]
    public long TrueCount { get; set; }

    [BsonElement("falseCount")]
    public long FalseCount { get; set; }

    [BsonElement("latestDateUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LatestDateUtc { get; set; }

    [BsonElement("reportCount")]
    public long ReportCount { get; set; }
}
