using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.Statistics;

[BsonIgnoreExtraElements]
[BsonCollection("work_report_table_stat_values")]
public sealed class WorkReportTableStatValue : BaseEntity
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

    [BsonElement("assigneeUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? AssigneeUserId { get; set; }

    [BsonElement("assigneeUnitId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? AssigneeUnitId { get; set; }

    [BsonElement("assignmentIsActive")]
    public bool AssignmentIsActive { get; set; }

    [BsonElement("reportIsActive")]
    public bool ReportIsActive { get; set; } = true;

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

    [BsonElement("dynamicExcelTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicExcelTemplateId { get; set; }

    [BsonElement("blockId")]
    public string BlockId { get; set; } = default!;

    [BsonElement("tableMode")]
    public string TableMode { get; set; } = "FIXED_GRID";

    [BsonElement("metricKey")]
    public string MetricKey { get; set; } = default!;

    [BsonElement("metricLabelCode")]
    public string? MetricLabelCode { get; set; }

    [BsonElement("rowKey")]
    public string RowKey { get; set; } = default!;

    [BsonElement("columnKey")]
    public string ColumnKey { get; set; } = default!;

    [BsonElement("sourceKey")]
    public string SourceKey { get; set; } = default!;

    [BsonElement("dataType")]
    public string DataType { get; set; } = "NUMBER";

    [BsonElement("bucketKey")]
    public string? BucketKey { get; set; }

    [BsonElement("bucketLabel")]
    public string? BucketLabel { get; set; }

    [BsonElement("textValue")]
    public string? TextValue { get; set; }

    [BsonElement("booleanValue")]
    public bool? BooleanValue { get; set; }

    [BsonElement("dateValue")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? DateValue { get; set; }

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

    [BsonElement("value")]
    public decimal Value { get; set; }

    [BsonElement("sourcePayloadRevision")]
    public int SourcePayloadRevision { get; set; }

    [BsonElement("sourcePayloadHash")]
    public string? SourcePayloadHash { get; set; }
}
