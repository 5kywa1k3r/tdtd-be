using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models.StatisticsReconciliation;

/// <summary>
/// Append-only begin receipt retained after the active marker is cleared.
/// Unlike worker operation receipts, its accepted state is historical and is
/// therefore never required to equal the run's current state.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class StatisticReconciliationRecheckBeginReceipt
{
    [BsonElement("receiptId")]
    public string ReceiptId { get; set; } = default!;

    [BsonElement("actorUserId")]
    public string ActorUserId { get; set; } = default!;

    [BsonElement("commandId")]
    public string CommandId { get; set; } = default!;

    [BsonElement("expectedStateRevision")]
    public long ExpectedStateRevision { get; set; }

    [BsonElement("expectedStateHash")]
    public string ExpectedStateHash { get; set; } = default!;

    [BsonElement("requestHash")]
    public string RequestHash { get; set; } = default!;

    [BsonElement("markerId")]
    public string MarkerId { get; set; } = default!;

    [BsonElement("acceptedStateRevision")]
    public long AcceptedStateRevision { get; set; }

    [BsonElement("acceptedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime AcceptedAtUtc { get; set; }

    [BsonElement("receiptHash")]
    public string ReceiptHash { get; set; } = default!;
}
