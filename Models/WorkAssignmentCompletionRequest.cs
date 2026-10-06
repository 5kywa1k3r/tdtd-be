using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
public sealed class WorkAssignmentCompletionRequest
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = default!;
    public string WorkId { get; set; } = default!;
    public string AssignmentId { get; set; } = default!;
    public string CommandId { get; set; } = default!;
    public string RequesterUserId { get; set; } = default!;
    public string Reason { get; set; } = default!;
    public string State { get; set; } = "PENDING";
    public string? DecisionCommandId { get; set; }
    public string? DecidedByUserId { get; set; }
    public string? DecisionReason { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
}
