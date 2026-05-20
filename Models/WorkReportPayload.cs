using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

public static class WorkReportPayloadStatus
{
    public const string Pending = "Pending";
    public const string Ready = "Ready";
}

[BsonIgnoreExtraElements]
[BsonCollection("work_report_payloads")]
public sealed class WorkReportPayload : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("reportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ReportId { get; set; } = default!;

    [BsonElement("payloadRevision")]
    public int PayloadRevision { get; set; }

    [BsonElement("values1DJson")]
    public string Values1DJson { get; set; } = "[]";

    [BsonElement("fieldValuesJson")]
    public string? FieldValuesJson { get; set; }

    [BsonElement("tableValuesRootJson")]
    public string? TableValuesRootJson { get; set; }

    [BsonElement("summarySourceJson")]
    public string? SummarySourceJson { get; set; }

    [BsonElement("payloadHash")]
    public string PayloadHash { get; set; } = string.Empty;

    [BsonElement("payloadSizeBytes")]
    public long PayloadSizeBytes { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = WorkReportPayloadStatus.Ready;
}
