using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

/// <summary>
/// Append-only idempotency receipt for a Dynamic Flow definition mutation.
/// The receipt stores canonical request/result tokens plus the exact committed
/// family/version result snapshots needed to make a later exact retry stable.
/// It never stores authentication credentials or caller-supplied raw request data.
/// </summary>
[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_definition_command_receipts")]
public sealed class DynamicFlowDefinitionCommandReceipt
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("actorUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ActorUserId { get; set; } = default!;

    [BsonElement("commandKind")]
    public string CommandKind { get; set; } = string.Empty;

    [BsonElement("commandId")]
    public string CommandId { get; set; } = string.Empty;

    [BsonElement("requestHash")]
    public string RequestHash { get; set; } = string.Empty;

    [BsonElement("familyId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string FamilyId { get; set; } = default!;

    [BsonElement("versionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? VersionId { get; set; }

    [BsonElement("resultFamilyRevision")]
    public int ResultFamilyRevision { get; set; }

    [BsonElement("resultDraftRevision")]
    public int? ResultDraftRevision { get; set; }

    [BsonElement("resultPayloadHash")]
    public string? ResultPayloadHash { get; set; }

    [BsonElement("resultFamilySnapshot")]
    public BsonDocument? ResultFamilySnapshot { get; set; }

    [BsonElement("resultFamilySnapshotSha256")]
    public string? ResultFamilySnapshotSha256 { get; set; }

    [BsonElement("resultVersionSnapshot")]
    public BsonDocument? ResultVersionSnapshot { get; set; }

    [BsonElement("resultVersionSnapshotSha256")]
    public string? ResultVersionSnapshotSha256 { get; set; }

    [BsonElement("correlationId")]
    public string? CorrelationId { get; set; }

    [BsonElement("outcome")]
    public string Outcome { get; set; } = DynamicFlowDefinitionCommandOutcomes.Succeeded;

    [BsonElement("createdAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [BsonElement("completedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CompletedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class DynamicFlowDefinitionCommandOutcomes
{
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
}
