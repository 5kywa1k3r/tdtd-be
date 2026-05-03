using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("work_status_operation_logs")]
public sealed class WorkStatusOperationLog : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("operation")]
    public string Operation { get; set; } = string.Empty;

    [BsonElement("scope")]
    public string Scope { get; set; } = string.Empty;

    [BsonElement("result")]
    public string Result { get; set; } = string.Empty;

    [BsonElement("workId")]
    public string? WorkId { get; set; }

    [BsonElement("workAssignmentId")]
    public string? WorkAssignmentId { get; set; }

    [BsonElement("workReportPeriodId")]
    public string? WorkReportPeriodId { get; set; }

    [BsonElement("workAssignmentReportId")]
    public string? WorkAssignmentReportId { get; set; }

    [BsonElement("actorUserId")]
    public string? ActorUserId { get; set; }

    [BsonElement("fromStatus")]
    public string? FromStatus { get; set; }

    [BsonElement("toStatus")]
    public string? ToStatus { get; set; }

    [BsonElement("periodFromStatus")]
    public string? PeriodFromStatus { get; set; }

    [BsonElement("periodToStatus")]
    public string? PeriodToStatus { get; set; }

    [BsonElement("assignmentFromStatus")]
    public string? AssignmentFromStatus { get; set; }

    [BsonElement("assignmentToStatus")]
    public string? AssignmentToStatus { get; set; }

    [BsonElement("workFromStatus")]
    public string? WorkFromStatus { get; set; }

    [BsonElement("workToStatus")]
    public string? WorkToStatus { get; set; }

    [BsonElement("summary")]
    public string? Summary { get; set; }

    [BsonElement("errorType")]
    public string? ErrorType { get; set; }

    [BsonElement("errorMessage")]
    public string? ErrorMessage { get; set; }

    [BsonElement("errorStackTrace")]
    public string? ErrorStackTrace { get; set; }

    [BsonElement("startedAtUtc")]
    public DateTime StartedAtUtc { get; set; }

    [BsonElement("completedAtUtc")]
    public DateTime CompletedAtUtc { get; set; }

    [BsonElement("durationMs")]
    public long DurationMs { get; set; }
}
