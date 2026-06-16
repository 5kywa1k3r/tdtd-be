using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("work_assignment_basic_summary_snapshots")]
public sealed class WorkAssignmentBasicSummarySnapshot : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("scopeAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ScopeAssignmentId { get; set; } = default!;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DynamicFormTemplateId { get; set; } = default!;

    [BsonElement("requestHash")]
    public string RequestHash { get; set; } = default!;

    [BsonElement("requestJson")]
    public string RequestJson { get; set; } = "{}";

    [BsonElement("sourceAssignmentIds")]
    [BsonRepresentation(BsonType.ObjectId)]
    public List<string> SourceAssignmentIds { get; set; } = new();

    [BsonElement("sourceReportIds")]
    [BsonRepresentation(BsonType.ObjectId)]
    public List<string> SourceReportIds { get; set; } = new();

    [BsonElement("sourceSignatureHash")]
    public string SourceSignatureHash { get; set; } = default!;

    [BsonElement("snapshotJson")]
    public string SnapshotJson { get; set; } = "{}";

    [BsonElement("snapshotDirty")]
    public bool SnapshotDirty { get; set; }

    [BsonElement("snapshotDirtyAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? SnapshotDirtyAtUtc { get; set; }

    [BsonElement("snapshotRefreshedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? SnapshotRefreshedAtUtc { get; set; }

    [BsonElement("refreshError")]
    public string? RefreshError { get; set; }
}
