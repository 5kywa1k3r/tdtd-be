using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models.StatisticsReconciliation;

/// <summary>
/// Immutable subset of trusted remediation evidence carried by one recheck.
/// The successor P10 actual generation is added only after trusted capture.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class StatisticReconciliationRecheckRemediationBinding
{
    public const string CurrentSchemaVersion =
        "P10_RECHECK_REMEDIATION_BINDING_V1";

    [BsonElement("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [BsonElement("evidenceId")]
    public string EvidenceId { get; set; } = default!;

    [BsonElement("evidenceSha256")]
    public string EvidenceSha256 { get; set; } = default!;

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

    [BsonElement("bindingSha256")]
    public string BindingSha256 { get; set; } = default!;
}
