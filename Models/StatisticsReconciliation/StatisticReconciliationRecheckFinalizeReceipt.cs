using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models.StatisticsReconciliation;

/// <summary>
/// Durable proof that a same-run recheck crossed the verdict/current/review
/// boundary. It remains after the active marker is cleared so an exact retry
/// can converge without reopening the recheck.
/// </summary>
public sealed class StatisticReconciliationRecheckFinalizeReceipt
{
    public const string CurrentSchemaVersion =
        "P10_RECHECK_FINALIZE_RECEIPT_V1";
    public const string CurrentP8SchemaVersion =
        "P10_RECHECK_FINALIZE_RECEIPT_V2";

    [BsonElement("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [BsonElement("markerId")]
    public string MarkerId { get; set; } = default!;

    [BsonElement("beginCommandId")]
    public string BeginCommandId { get; set; } = default!;

    [BsonElement("beginRequestHash")]
    public string BeginRequestHash { get; set; } = default!;

    [BsonElement("baseActualGenerationId")]
    public string BaseActualGenerationId { get; set; } = default!;

    [BsonElement("baseActualGenerationSha256")]
    public string BaseActualGenerationSha256 { get; set; } = default!;

    [BsonElement("baseVerdictGenerationId")]
    public string BaseVerdictGenerationId { get; set; } = default!;

    [BsonElement("baseVerdictGenerationSha256")]
    public string BaseVerdictGenerationSha256 { get; set; } = default!;

    [BsonElement("successorActualGenerationId")]
    public string SuccessorActualGenerationId { get; set; } = default!;

    [BsonElement("successorActualGenerationSha256")]
    public string SuccessorActualGenerationSha256 { get; set; } = default!;

    [BsonElement("successorVerdictGenerationId")]
    public string SuccessorVerdictGenerationId { get; set; } = default!;

    [BsonElement("successorVerdictGenerationSha256")]
    public string SuccessorVerdictGenerationSha256 { get; set; } = default!;

    [BsonElement("terminalStatus")]
    public string TerminalStatus { get; set; } = default!;

    [BsonElement("reviewSupersessionCommandId")]
    public string ReviewSupersessionCommandId { get; set; } = default!;

    [BsonElement("reviewSupersessionExpectedStateRevision")]
    public long ReviewSupersessionExpectedStateRevision { get; set; }

    [BsonElement("captureBindingSha256")]
    public string CaptureBindingSha256 { get; set; } = default!;

    [BsonElement("remediationBinding")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationRecheckRemediationBinding?
        RemediationBinding { get; set; }

    [BsonElement("remediationSemanticSha256")]
    [BsonIgnoreIfNull]
    public string? RemediationSemanticSha256 { get; set; }

    [BsonElement("currentP8Configuration")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationTrustedP8ConfigurationIdentity?
        CurrentP8Configuration { get; set; }
    [BsonElement("promotedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime PromotedAtUtc { get; set; }

    [BsonElement("receiptSha256")]
    public string ReceiptSha256 { get; set; } = default!;
}
