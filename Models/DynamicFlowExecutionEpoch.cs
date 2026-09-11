using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_execution_epochs")]
public sealed class DynamicFlowExecutionEpoch : BaseEntity
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("flowInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowInstanceId { get; set; } = default!;

    [BsonElement("executionEpoch")]
    public int ExecutionEpoch { get; set; }

    [BsonElement("state")]
    public string State { get; set; } = DynamicFlowExecutionEpochStates.Active;

    [BsonElement("checkpointNodeId")]
    public string CheckpointNodeId { get; set; } = string.Empty;

    [BsonElement("isCanonical")]
    public bool IsCanonical { get; set; } = true;

    [BsonElement("openedByCommandId")]
    public string OpenedByCommandId { get; set; } = string.Empty;

    [BsonElement("closedByCommandId")]
    public string? ClosedByCommandId { get; set; }

    [BsonElement("terminalEventId"), BsonRepresentation(BsonType.ObjectId)]
    public string? TerminalEventId { get; set; }

    [BsonElement("replacedByExecutionEpoch")]
    public int? ReplacedByExecutionEpoch { get; set; }

    [BsonElement("openedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime OpenedAtUtc { get; set; }

    [BsonElement("closedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ClosedAtUtc { get; set; }

    [BsonElement("revision")]
    public long Revision { get; set; } = 1;
}

public static class DynamicFlowExecutionEpochStates
{
    public const string Active = "ACTIVE";
    public const string Finalized = "FINALIZED";
    public const string RolledBack = "ROLLED_BACK";
    public const string Terminated = "TERMINATED";
    public const string Restarted = "RESTARTED";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Active,
            Finalized,
            RolledBack,
            Terminated,
            Restarted
        };
}
