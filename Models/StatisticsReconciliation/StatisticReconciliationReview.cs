using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.StatisticsReconciliation;

public static class StatisticReconciliationReviewKinds
{
    public const string FinalVerdict = "FINAL_VERDICT";
}

[BsonIgnoreExtraElements]
[BsonCollection("work_report_statistic_reconciliation_reviews")]
public sealed class StatisticReconciliationReview
{
    [BsonId]
    public string Id { get; set; } = default!;

    [BsonElement("schemaVersion")]
    public string SchemaVersion { get; set; } = "P10_FINAL_VERDICT_V1";

    [BsonElement("recordKind")]
    public string RecordKind { get; set; } = StatisticReconciliationReviewKinds.FinalVerdict;

    [BsonElement("reconciliationId")]
    public string ReconciliationId { get; set; } = default!;

    [BsonElement("verdictGenerationId")]
    public string VerdictGenerationId { get; set; } = default!;

    [BsonElement("verdictGenerationSha256")]
    public string VerdictGenerationSha256 { get; set; } = default!;

    [BsonElement("comparisonBindingSha256")]
    public string ComparisonBindingSha256 { get; set; } = default!;

    [BsonElement("expectedGenerationId")]
    public string ExpectedGenerationId { get; set; } = default!;

    [BsonElement("expectedGenerationSha256")]
    public string ExpectedGenerationSha256 { get; set; } = default!;

    [BsonElement("actualGenerationId")]
    public string ActualGenerationId { get; set; } = default!;

    [BsonElement("actualGenerationSha256")]
    public string ActualGenerationSha256 { get; set; } = default!;

    [BsonElement("deltaManifestSha256")]
    public string DeltaManifestSha256 { get; set; } = default!;

    [BsonElement("authorizationEvidenceSha256")]
    public string AuthorizationEvidenceSha256 { get; set; } = default!;

    [BsonElement("freshnessAssessmentSha256")]
    [BsonIgnoreIfNull]
    public string? FreshnessAssessmentSha256 { get; set; }

    [BsonElement("rootCauseClassificationSha256")]
    [BsonIgnoreIfNull]
    public string? RootCauseClassificationSha256 { get; set; }

    [BsonElement("rootCauseClass")]
    [BsonIgnoreIfNull]
    public string? RootCauseClass { get; set; }

    [BsonElement("failureKind")]
    public string FailureKind { get; set; } = default!;

    [BsonElement("failureEvidenceSha256")]
    [BsonIgnoreIfNull]
    public string? FailureEvidenceSha256 { get; set; }

    [BsonElement("verdict")]
    public string Verdict { get; set; } = default!;

    [BsonElement("completeEvidence")]
    public bool CompleteEvidence { get; set; }

    [BsonElement("allRequiredLayersZero")]
    public bool AllRequiredLayersZero { get; set; }

    [BsonElement("missingOrExtraIdentity")]
    public bool MissingOrExtraIdentity { get; set; }

    [BsonElement("unknownBlocksCloseout")]
    public bool UnknownBlocksCloseout { get; set; }

    [BsonElement("closeoutAllowed")]
    public bool CloseoutAllowed { get; set; }

    [BsonElement("signable")]
    public bool Signable { get; set; }

    [BsonElement("supersedesVerdictGenerationId")]
    [BsonIgnoreIfNull]
    public string? SupersedesVerdictGenerationId { get; set; }

    [BsonElement("supersedesVerdictGenerationSha256")]
    [BsonIgnoreIfNull]
    public string? SupersedesVerdictGenerationSha256 { get; set; }

    [BsonElement("remediation")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationVerdictRemediation? Remediation { get; set; }

    [BsonElement("documentSemanticSha256")]
    public string DocumentSemanticSha256 { get; set; } = default!;

    [BsonElement("createdAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class StatisticReconciliationVerdictRemediation
{
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

    [BsonElement("afterResultSha256")]
    public string AfterResultSha256 { get; set; } = default!;

    [BsonElement("recheckGenerationId")]
    public string RecheckGenerationId { get; set; } = default!;

    [BsonElement("recheckGenerationSha256")]
    public string RecheckGenerationSha256 { get; set; } = default!;

    [BsonElement("resultingVerdict")]
    public string ResultingVerdict { get; set; } = default!;

    [BsonElement("remediationSemanticSha256")]
    public string RemediationSemanticSha256 { get; set; } = default!;
}
