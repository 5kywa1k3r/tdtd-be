using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("work_report_table_values")]
public sealed class WorkReportTableValue : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("reportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ReportId { get; set; } = default!;

    [BsonElement("blockId")]
    public string BlockId { get; set; } = default!;

    [BsonElement("payloadRevision")]
    public int PayloadRevision { get; set; }

    [BsonElement("blockOrder")]
    public int BlockOrder { get; set; }

    [BsonElement("tableMode")]
    public string TableMode { get; set; } = "FIXED_GRID";

    [BsonElement("valuesJson")]
    public string ValuesJson { get; set; } = "{}";

    [BsonElement("rowCount")]
    public int RowCount { get; set; }

    [BsonElement("columnCount")]
    public int ColumnCount { get; set; }

    [BsonElement("sizeBytes")]
    public long SizeBytes { get; set; }

    [BsonElement("payloadHash")]
    public string PayloadHash { get; set; } = string.Empty;

    [BsonElement("status")]
    public string Status { get; set; } = WorkReportPayloadStatus.Ready;
}
