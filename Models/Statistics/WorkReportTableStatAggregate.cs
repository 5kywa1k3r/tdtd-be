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

    [BsonElement("rowKey")]
    public string RowKey { get; set; } = default!;

    [BsonElement("columnKey")]
    public string ColumnKey { get; set; } = default!;

    [BsonElement("periodKey")]
    public string PeriodKey { get; set; } = default!;

    [BsonElement("periodInstanceKey")]
    public string PeriodInstanceKey { get; set; } = default!;

    [BsonElement("periodKind")]
    public string PeriodKind { get; set; } = default!;

    [BsonElement("reportStatus")]
    public int ReportStatus { get; set; }

    [BsonElement("valueCount")]
    public long ValueCount { get; set; }

    [BsonElement("sum")]
    public decimal Sum { get; set; }

    [BsonElement("min")]
    public decimal? Min { get; set; }

    [BsonElement("max")]
    public decimal? Max { get; set; }

    [BsonElement("reportCount")]
    public long ReportCount { get; set; }
}
