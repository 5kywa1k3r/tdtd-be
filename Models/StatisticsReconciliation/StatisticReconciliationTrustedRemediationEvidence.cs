using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models.StatisticsReconciliation;

/// <summary>
/// Append-only authorization that an exact P9 publication may remediate one
/// identity-mismatch verdict. No browser command supplies these semantic pins.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class StatisticReconciliationTrustedRemediationEvidence
{
    public const string CurrentSchemaVersion =
        "P10_TRUSTED_REMEDIATION_EVIDENCE_V1";

    public const string RecordKind = "TRUSTED_REMEDIATION_EVIDENCE";

    [BsonId]
    public string Id { get; set; } = default!;

    [BsonElement("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [BsonElement("recordKind")]
    public string Kind { get; set; } = RecordKind;

    [BsonElement("reconciliationId")]
    public string ReconciliationId { get; set; } = default!;

    [BsonElement("baseVerdictGenerationId")]
    public string BaseVerdictGenerationId { get; set; } = default!;

    [BsonElement("baseVerdictGenerationSha256")]
    public string BaseVerdictGenerationSha256 { get; set; } = default!;

    [BsonElement("baseActualGenerationId")]
    public string BaseActualGenerationId { get; set; } = default!;

    [BsonElement("baseActualGenerationSha256")]
    public string BaseActualGenerationSha256 { get; set; } = default!;

    [BsonElement("rootCauseClass")]
    public string RootCauseClass { get; set; } = default!;

    [BsonElement("referenceType")]
    public string ReferenceType { get; set; } = default!;

    [BsonElement("referenceId")]
    public string ReferenceId { get; set; } = default!;

    [BsonElement("referenceSha256")]
    public string ReferenceSha256 { get; set; } = default!;

    [BsonElement("beforeSourceSha256")]
    public string BeforeSourceSha256 { get; set; } = default!;

    [BsonElement("afterSourceSha256")]
    public string AfterSourceSha256 { get; set; } = default!;

    [BsonElement("beforeResultSha256")]
    public string BeforeResultSha256 { get; set; } = default!;

    [BsonElement("successorP9GenerationId")]
    public string SuccessorP9GenerationId { get; set; } = default!;

    [BsonElement("successorP9GenerationSha256")]
    public string SuccessorP9GenerationSha256 { get; set; } = default!;

    [BsonElement("successorCapturePlanSha256")]
    public string SuccessorCapturePlanSha256 { get; set; } = default!;

    [BsonElement("authorizationEvidenceSha256")]
    public string AuthorizationEvidenceSha256 { get; set; } = default!;

    [BsonElement("authorizedByUserId")]
    public string AuthorizedByUserId { get; set; } = default!;

    [BsonElement("authorizedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime AuthorizedAtUtc { get; set; }

    [BsonElement("evidenceSha256")]
    public string EvidenceSha256 { get; set; } = default!;
}
