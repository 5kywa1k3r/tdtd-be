using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.StatisticsConfiguration;

/// <summary>
/// Append-only P8 command receipt. It stores hashes and the exact normalized
/// response, never a caller credential or raw request payload.
/// </summary>
[BsonIgnoreExtraElements]
[BsonCollection("stat_config_command_receipts")]
public sealed class StatConfigCommandReceipt
{
    [BsonId]
    public string Id { get; set; } = default!;

    [BsonElement("ownerKind")]
    public string OwnerKind { get; set; } = string.Empty;

    [BsonElement("ownerId")]
    public string OwnerId { get; set; } = string.Empty;

    [BsonElement("commandKind")]
    public string CommandKind { get; set; } = string.Empty;

    [BsonElement("commandId")]
    public string CommandId { get; set; } = string.Empty;

    [BsonElement("requestHash")]
    public string RequestHash { get; set; } = string.Empty;

    [BsonElement("responseJson")]
    public string ResponseJson { get; set; } = string.Empty;

    [BsonElement("responseHash")]
    public string ResponseHash { get; set; } = string.Empty;

    [BsonElement("resultConfigId")]
    public string ResultConfigId { get; set; } = string.Empty;

    [BsonElement("resultVersionId")]
    public string ResultVersionId { get; set; } = string.Empty;

    [BsonElement("resultVersionNo")]
    public int ResultVersionNo { get; set; }

    [BsonElement("resultRevision")]
    public long ResultRevision { get; set; }

    [BsonElement("resultStatus")]
    public string ResultStatus { get; set; } = string.Empty;

    [BsonElement("resultConfigHash")]
    public string ResultConfigHash { get; set; } = string.Empty;

    [BsonElement("actorUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ActorUserId { get; set; } = default!;

    [BsonElement("createdAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class StatConfigCommandKinds
{
    public const string UpsertLabel = "UPSERT_LABEL";
    public const string TombstoneLabel = "TOMBSTONE_LABEL";
    public const string UpsertBasicSummaryConfig =
        "UPSERT_BASIC_SUMMARY_CONFIG";
    public const string LockBasicSummaryConfig =
        "LOCK_BASIC_SUMMARY_CONFIG";
    public const string CreateBasicSummaryDraft =
        "CREATE_BASIC_SUMMARY_DRAFT";
    public const string GrantWorkSummaryTokenQuota =
        "GRANT_WORK_SUMMARY_TOKEN_QUOTA";
    public const string ConsumeAdvancedSummaryConfigLockToken =
        "CONSUME_ADVANCED_SUMMARY_CONFIG_LOCK_TOKEN";
    public const string CompensateAdvancedSummaryConfigLockToken =
        "COMPENSATE_ADVANCED_SUMMARY_CONFIG_LOCK_TOKEN";
}
