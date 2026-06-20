using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("work_summary_token_ledgers")]
public sealed class WorkSummaryTokenLedger : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("ownerUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? OwnerUserId { get; set; }

    [BsonElement("ownerUnitId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? OwnerUnitId { get; set; }

    [BsonElement("actorUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ActorUserId { get; set; } = default!;

    [BsonElement("issuerUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? IssuerUserId { get; set; }

    [BsonElement("tokenKind")]
    public string TokenKind { get; set; } = WorkSummaryTokenKinds.AdvancedSummaryConfigLock;

    [BsonElement("direction")]
    public string Direction { get; set; } = WorkSummaryTokenDirections.Consume;

    [BsonElement("units")]
    public int Units { get; set; }

    [BsonElement("monthlyQuota")]
    public int MonthlyQuota { get; set; }

    [BsonElement("periodMonthKey")]
    public string PeriodMonthKey { get; set; } = string.Empty;

    [BsonElement("requestTokenId")]
    public string? RequestTokenId { get; set; }

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? WorkId { get; set; }

    [BsonElement("workAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? WorkAssignmentId { get; set; }

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormTemplateId { get; set; }

    [BsonElement("sectionId")]
    public string? SectionId { get; set; }

    [BsonElement("configId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ConfigId { get; set; }

    [BsonElement("configVersionNo")]
    public int? ConfigVersionNo { get; set; }

    [BsonElement("configHash")]
    public string? ConfigHash { get; set; }

    [BsonElement("jobId")]
    public string? JobId { get; set; }

    [BsonElement("reason")]
    public string Reason { get; set; } = string.Empty;

    [BsonElement("outcome")]
    public string Outcome { get; set; } = WorkSummaryTokenOutcomes.Success;

    [BsonElement("error")]
    public string? Error { get; set; }
}

public static class WorkSummaryTokenKinds
{
    public const string AdvancedSummaryConfigLock = "ADVANCED_SUMMARY_CONFIG_LOCK";
    public const string AdvancedSummaryBroadHistoricalBuild = "ADVANCED_SUMMARY_BROAD_HISTORICAL_BUILD";
}

public static class WorkSummaryTokenDirections
{
    public const string Free = "FREE";
    public const string Consume = "CONSUME";
    public const string Grant = "GRANT";
}

public static class WorkSummaryTokenOutcomes
{
    public const string Success = "SUCCESS";
    public const string Failed = "FAILED";
}
