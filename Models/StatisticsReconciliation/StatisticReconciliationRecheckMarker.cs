using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models.StatisticsReconciliation;

public static class StatisticReconciliationRecheckPhases
{
    public const string ReadyToClaim = "READY_TO_CLAIM";
    public const string CaptureRunning = "CAPTURE_RUNNING";
    public const string PendingPublished = "PENDING_PUBLISHED";
    public const string ReviewSupersessionPending =
        "CURRENT_SWAPPED_REVIEW_SUPERSESSION_PENDING";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [
            ReadyToClaim,
            CaptureRunning,
            PendingPublished,
            ReviewSupersessionPending
        ],
        StringComparer.Ordinal);
}

/// <summary>
/// Durable same-run recheck marker. The marker makes an otherwise-invalid
/// current+worker/current+pending combination explicit and binds every step to
/// the exact current generation that was visible when recheck began.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class StatisticReconciliationRecheckMarker
{
    public const string CurrentSchemaVersion = "P10_SAME_RUN_RECHECK_V1";

    [BsonElement("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [BsonElement("markerId")]
    public string MarkerId { get; set; } = default!;

    [BsonElement("beginActorUserId")]
    public string BeginActorUserId { get; set; } = default!;

    [BsonElement("beginCommandId")]
    public string BeginCommandId { get; set; } = default!;

    [BsonElement("beginRequestHash")]
    public string BeginRequestHash { get; set; } = default!;

    [BsonElement("beginExpectedStateRevision")]
    public long BeginExpectedStateRevision { get; set; }

    [BsonElement("beginExpectedStateHash")]
    public string BeginExpectedStateHash { get; set; } = default!;

    [BsonElement("baseTerminalStatus")]
    public string BaseTerminalStatus { get; set; } = default!;

    [BsonElement("baseCurrentGenerationId")]
    public string BaseCurrentGenerationId { get; set; } = default!;

    [BsonElement("baseCurrentGenerationHash")]
    public string BaseCurrentGenerationHash { get; set; } = default!;

    [BsonElement("baseVerdictGenerationId")]
    public string BaseVerdictGenerationId { get; set; } = default!;

    [BsonElement("baseVerdictGenerationHash")]
    public string BaseVerdictGenerationHash { get; set; } = default!;

    [BsonElement("captureBinding")]
    public StatisticReconciliationRecheckCaptureBinding CaptureBinding
        { get; set; } = default!;

    [BsonElement("remediationBinding")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationRecheckRemediationBinding?
        RemediationBinding { get; set; }
    [BsonElement("phase")]
    public string Phase { get; set; } =
        StatisticReconciliationRecheckPhases.ReadyToClaim;

    [BsonElement("successorGenerationId")]
    [BsonIgnoreIfNull]
    public string? SuccessorGenerationId { get; set; }

    [BsonElement("successorGenerationHash")]
    [BsonIgnoreIfNull]
    public string? SuccessorGenerationHash { get; set; }

    [BsonElement("successorVerdictGenerationId")]
    [BsonIgnoreIfNull]
    public string? SuccessorVerdictGenerationId { get; set; }

    [BsonElement("successorVerdictGenerationHash")]
    [BsonIgnoreIfNull]
    public string? SuccessorVerdictGenerationHash { get; set; }

    [BsonElement("reviewSupersessionCommandId")]
    public string ReviewSupersessionCommandId { get; set; } = default!;

    [BsonElement("begunAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime BegunAtUtc { get; set; }

    [BsonElement("markerStateHash")]
    public string MarkerStateHash { get; set; } = default!;
}
