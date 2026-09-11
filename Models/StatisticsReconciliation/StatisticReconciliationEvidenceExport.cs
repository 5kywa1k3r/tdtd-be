using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.StatisticsReconciliation;

[BsonIgnoreExtraElements]
[BsonCollection("work_report_statistic_reconciliation_exports")]
public sealed class StatisticReconciliationEvidenceExport
{
    [BsonId]
    public string Id { get; set; } = default!;

    [BsonElement("schemaVersion")]
    public string SchemaVersion { get; set; } = "P10_RECONCILIATION_EVIDENCE_EXPORT_V1";

    [BsonElement("commandId")]
    public string CommandId { get; set; } = default!;

    [BsonElement("workId"), BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("scopeAssignmentId"), BsonRepresentation(BsonType.ObjectId)]
    public string ScopeAssignmentId { get; set; } = default!;

    [BsonElement("reconciliationId"), BsonRepresentation(BsonType.ObjectId)]
    public string ReconciliationId { get; set; } = default!;

    [BsonElement("generationId")]
    public string GenerationId { get; set; } = default!;

    [BsonElement("generationSha256")]
    public string GenerationSha256 { get; set; } = default!;

    [BsonElement("semanticVerdictSha256")]
    public string SemanticVerdictSha256 { get; set; } = default!;

    [BsonElement("reviewSignatureSha256")]
    public string ReviewSignatureSha256 { get; set; } = default!;

    [BsonElement("finalApprovalSha256")]
    public string FinalApprovalSha256 { get; set; } = default!;

    [BsonElement("permissionSnapshotSha256")]
    public string PermissionSnapshotSha256 { get; set; } = default!;

    [BsonElement("createdByActorId"), BsonRepresentation(BsonType.ObjectId)]
    public string CreatedByActorId { get; set; } = default!;

    [BsonElement("format")]
    public string Format { get; set; } = default!;

    [BsonElement("detailLevel")]
    public string DetailLevel { get; set; } = default!;

    [BsonElement("fileName")]
    public string FileName { get; set; } = default!;

    [BsonElement("contentType")]
    public string ContentType { get; set; } = default!;

    [BsonElement("manifestJson")]
    public string ManifestJson { get; set; } = default!;

    [BsonElement("manifestSha256")]
    public string ManifestSha256 { get; set; } = default!;

    [BsonElement("contentSha256")]
    public string ContentSha256 { get; set; } = default!;

    [BsonElement("contentLength")]
    public long ContentLength { get; set; }

    [BsonElement("content")]
    public byte[] Content { get; set; } = [];

    [BsonElement("createdAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; }

    [BsonElement("expiresAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ExpiresAtUtc { get; set; }

    [BsonElement("documentSha256")]
    public string DocumentSha256 { get; set; } = default!;
}
