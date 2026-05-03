using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.Statistics;

[BsonIgnoreExtraElements]
[BsonCollection("work_report_label_stat_values")]
public sealed class WorkReportLabelStatValue : BaseEntity
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

    [BsonElement("dynamicExcelTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicExcelTemplateId { get; set; }

    [BsonElement("blockId")]
    public string BlockId { get; set; } = default!;

    [BsonElement("periodKey")]
    public string PeriodKey { get; set; } = default!;

    [BsonElement("periodInstanceKey")]
    public string PeriodInstanceKey { get; set; } = default!;

    [BsonElement("periodKind")]
    public string PeriodKind { get; set; } = default!;

    [BsonElement("reportStatus")]
    public int ReportStatus { get; set; }

    [BsonElement("sheetId")]
    public string? SheetId { get; set; }

    [BsonElement("rowKey")]
    public string RowKey { get; set; } = default!;

    [BsonElement("rowIndex")]
    public int RowIndex { get; set; }

    [BsonElement("labelCode")]
    public string LabelCode { get; set; } = default!;

    [BsonElement("source")]
    public string Source { get; set; } = "ROW_LABEL";
}
