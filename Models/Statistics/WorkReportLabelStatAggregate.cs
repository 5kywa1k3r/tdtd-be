using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.Statistics;

[BsonIgnoreExtraElements]
[BsonCollection("work_report_label_stat_aggregates")]
public sealed class WorkReportLabelStatAggregate : BaseEntity
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

    [BsonElement("dynamicExcelTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicExcelTemplateId { get; set; }

    [BsonElement("blockId")]
    public string BlockId { get; set; } = default!;

    [BsonElement("labelCode")]
    public string LabelCode { get; set; } = default!;

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

    [BsonElement("rowCount")]
    public long RowCount { get; set; }

    [BsonElement("reportCount")]
    public long ReportCount { get; set; }
}
