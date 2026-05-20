using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.Statistics;

[BsonIgnoreExtraElements]
[BsonCollection("work_report_table_stat_aggregates")]
public sealed class WorkReportTableStatAggregate : BaseEntity
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

    [BsonElement("dataType")]
    public string DataType { get; set; } = "NUMBER";

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

    [BsonElement("sum")]
    public decimal Sum { get; set; }

    [BsonElement("numericValueCount")]
    public long NumericValueCount { get; set; }

    [BsonElement("min")]
    public decimal? Min { get; set; }

    [BsonElement("max")]
    public decimal? Max { get; set; }

    [BsonElement("reportCount")]
    public long ReportCount { get; set; }

    [BsonElement("trueCount")]
    public long TrueCount { get; set; }

    [BsonElement("falseCount")]
    public long FalseCount { get; set; }

    [BsonElement("earliestDateUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? EarliestDateUtc { get; set; }

    [BsonElement("latestDateUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LatestDateUtc { get; set; }
}
