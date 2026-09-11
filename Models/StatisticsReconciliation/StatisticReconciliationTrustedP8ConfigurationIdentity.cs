using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models.StatisticsReconciliation;

/// <summary>
/// Trusted authoritative P8 statistic configuration observed while a verdict is
/// finalized. Recheck detail reads this receipt-bound identity instead of
/// consulting a later mutable owner.
/// </summary>
public sealed record StatisticReconciliationTrustedP8ConfigurationIdentity
{
    public const string CurrentSchemaVersion =
        "P10_TRUSTED_CURRENT_P8_CONFIGURATION_IDENTITY_V1";

    [BsonElement("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [BsonElement("ownerId")]
    public string OwnerId { get; set; } = default!;

    [BsonElement("configId")]
    public string ConfigId { get; set; } = default!;

    [BsonElement("versionId")]
    public string VersionId { get; set; } = default!;

    [BsonElement("versionNo")]
    public int VersionNo { get; set; }

    [BsonElement("revision")]
    public long Revision { get; set; }

    [BsonElement("configHash")]
    public string ConfigHash { get; set; } = default!;

    [BsonElement("bundleSha256")]
    public string BundleSha256 { get; set; } = default!;

    [BsonElement("semanticSha256")]
    public string SemanticSha256 { get; set; } = default!;
}
