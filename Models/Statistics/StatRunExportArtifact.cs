using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.Statistics;

public static class StatRunExportFormats
{
    public const string Csv = "CSV";
    public const string Xlsx = "XLSX";
}

public static class StatRunExportResultKinds
{
    public const string DirectField = "DIRECT_FIELD";
    public const string DirectTable = "DIRECT_TABLE";
    public const string DirectLabel = "DIRECT_LABEL";
    public const string Basic = "BASIC";
    public const string Advanced = "ADVANCED";
    public const string Diff = "DIFF";
    public const string Flow = "FLOW";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [DirectField, DirectTable, DirectLabel, Basic, Advanced, Diff, Flow],
        StringComparer.Ordinal);
}

public static class StatRunExportStatuses
{
    public const string Completed = "COMPLETED";
    public const string Expired = "EXPIRED";
}

[BsonIgnoreExtraElements]
[BsonCollection("work_report_statistic_exports")]
public sealed class StatRunExportArtifact : BaseEntity
{
    [BsonId]
    public string Id { get; set; } = default!;

    [BsonElement("commandId")]
    public string CommandId { get; set; } = default!;

    [BsonElement("requestHash")]
    public string RequestHash { get; set; } = default!;

    [BsonElement("receiptId")]
    public string ReceiptId { get; set; } = default!;

    [BsonElement("requestedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string RequestedByUserId { get; set; } = default!;

    [BsonElement("authorizationSnapshotHash")]
    public string AuthorizationSnapshotHash { get; set; } = default!;

    [BsonElement("authorizationPolicy")]
    public string AuthorizationPolicy { get; set; } = "REQUESTOR_OR_SYSTEM_ADMIN";

    [BsonElement("capabilityId")]
    public string CapabilityId { get; set; } = default!;

    [BsonElement("resultKind")]
    public string ResultKind { get; set; } = default!;

    [BsonElement("format")]
    public string Format { get; set; } = default!;

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("scopeType")]
    public string ScopeType { get; set; } = default!;

    [BsonElement("scopeId")]
    public string ScopeId { get; set; } = default!;

    [BsonElement("periodInstanceKey")]
    public string? PeriodInstanceKey { get; set; }

    [BsonElement("resultId")]
    public string ResultId { get; set; } = default!;

    [BsonElement("resultHash")]
    public string ResultHash { get; set; } = default!;

    [BsonElement("configHash")]
    public string ConfigHash { get; set; } = default!;

    [BsonElement("sourceHash")]
    public string SourceHash { get; set; } = default!;

    [BsonElement("lifecycleRevision")]
    public int LifecycleRevision { get; set; }

    [BsonElement("catalogVersion")]
    public string CatalogVersion { get; set; } = default!;

    [BsonElement("catalogRawSha256")]
    public string CatalogRawSha256 { get; set; } = default!;

    [BsonElement("catalogSemanticSha256")]
    public string CatalogSemanticSha256 { get; set; } = default!;

    [BsonElement("stageLockSha256")]
    public string StageLockSha256 { get; set; } = default!;

    [BsonElement("candidateChainId")]
    public string CandidateChainId { get; set; } = default!;

    [BsonElement("candidatePromptId")]
    public string CandidatePromptId { get; set; } = default!;

    [BsonElement("candidateStage")]
    public int CandidateStage { get; set; }

    [BsonElement("filterHash")]
    public string FilterHash { get; set; } = default!;

    [BsonElement("canonicalFilterJson")]
    [BsonIgnoreIfNull]
    public string? CanonicalFilterJson { get; set; }

    [BsonElement("schemaVersion")]
    public string SchemaVersion { get; set; } = "P9_CANONICAL_EXPORT_V1";

    [BsonElement("semanticHash")]
    public string SemanticHash { get; set; } = default!;

    // Future artifacts carry this immutable sidecar. Legacy artifacts have
    // neither field and are deliberately ineligible for P10 actual capture.
    [BsonElement("columnManifestJson")]
    public string? ColumnManifestJson { get; set; }

    [BsonElement("columnManifestSha256")]
    public string? ColumnManifestSha256 { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = StatRunExportStatuses.Completed;

    [BsonElement("fileName")]
    public string FileName { get; set; } = default!;

    [BsonElement("contentType")]
    public string ContentType { get; set; } = default!;

    [BsonElement("storageKey")]
    public string StorageKey { get; set; } = default!;

    [BsonElement("contentHash")]
    public string ContentHash { get; set; } = default!;

    [BsonElement("byteCount")]
    public long ByteCount { get; set; }

    [BsonElement("rowCount")]
    public int RowCount { get; set; }

    [BsonElement("columnCount")]
    public int ColumnCount { get; set; }

    [BsonElement("downloadCount")]
    public int DownloadCount { get; set; }

    [BsonElement("lastDownloadedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastDownloadedAtUtc { get; set; }

    [BsonElement("completedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CompletedAtUtc { get; set; }

    [BsonElement("expiresAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ExpiresAtUtc { get; set; }
}
