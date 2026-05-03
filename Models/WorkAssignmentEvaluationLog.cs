using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("work_assignment_evaluation_logs")]
public sealed class WorkAssignmentEvaluationLog : BaseEntity
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

    [BsonElement("action")]
    public string Action { get; set; } = string.Empty;
    // EVALUATE / UPDATE_EVALUATION / CLEAR_EVALUATION

    [BsonElement("fromEvaluationCode")]
    public string? FromEvaluationCode { get; set; }

    [BsonElement("fromEvaluationLabel")]
    public string? FromEvaluationLabel { get; set; }

    [BsonElement("toEvaluationCode")]
    public string? ToEvaluationCode { get; set; }

    [BsonElement("toEvaluationLabel")]
    public string? ToEvaluationLabel { get; set; }

    [BsonElement("comment")]
    public string? Comment { get; set; }

    [BsonElement("reason")]
    public string? Reason { get; set; }

    [BsonElement("actionByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ActionByUserId { get; set; } = default!;

    [BsonElement("actionAtUtc")]
    public DateTime ActionAtUtc { get; set; }

    [BsonElement("snapshotJson")]
    public string? SnapshotJson { get; set; }
}