using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models.StatisticsReconciliation;

/// <summary>
/// Server-resolved owner tuple used by one same-run recheck.  It is separate
/// from the immutable tuple that created the reconciliation so a recheck can
/// observe a genuinely newer P9 publication without rewriting history.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class StatisticReconciliationRecheckCaptureBinding
{
    public const string CurrentSchemaVersion =
        "P10_RECHECK_CAPTURE_BINDING_V1";

    [BsonElement("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [BsonElement("p9ResultId")]
    public string P9ResultId { get; set; } = default!;

    [BsonElement("p9RunId")]
    public string P9RunId { get; set; } = default!;

    [BsonElement("p9GenerationId")]
    public string P9GenerationId { get; set; } = default!;

    [BsonElement("p9GenerationHash")]
    public string P9GenerationHash { get; set; } = default!;

    [BsonElement("p9RunKind")]
    public string P9RunKind { get; set; } = default!;

    [BsonElement("p9CapabilityId")]
    public string P9CapabilityId { get; set; } = default!;

    [BsonElement("p9RouteId")]
    public string P9RouteId { get; set; } = default!;

    [BsonElement("p9CandidateChainId")]
    public string P9CandidateChainId { get; set; } = default!;

    [BsonElement("p9CandidatePromptId")]
    public string P9CandidatePromptId { get; set; } = default!;

    [BsonElement("sourceReportId")]
    public string SourceReportId { get; set; } = default!;

    [BsonElement("sourcePayloadRevision")]
    public int SourcePayloadRevision { get; set; }

    [BsonElement("sourcePayloadHash")]
    public string SourcePayloadHash { get; set; } = default!;

    [BsonElement("sourceLifecycleRevision")]
    public int SourceLifecycleRevision { get; set; }

    [BsonElement("sourceLifecycleEventKey")]
    public string? SourceLifecycleEventKey { get; set; }

    [BsonElement("sourceLifecycleHash")]
    public string SourceLifecycleHash { get; set; } = default!;

    [BsonElement("sourceLifecycleStatus")]
    public string SourceLifecycleStatus { get; set; } = default!;

    [BsonElement("dynamicFormFamilyId")]
    public string? DynamicFormFamilyId { get; set; }

    [BsonElement("dynamicFormVersionId")]
    public string DynamicFormVersionId { get; set; } = default!;

    [BsonElement("dynamicFormVersionNo")]
    public int? DynamicFormVersionNo { get; set; }

    [BsonElement("dynamicFormSchemaHash")]
    public string? DynamicFormSchemaHash { get; set; }

    [BsonElement("flowTemplateId")]
    public string? FlowTemplateId { get; set; }

    [BsonElement("flowFamilyRevision")]
    public int? FlowFamilyRevision { get; set; }

    [BsonElement("flowTemplateVersionId")]
    public string? FlowTemplateVersionId { get; set; }

    [BsonElement("flowPayloadHash")]
    public string? FlowPayloadHash { get; set; }

    [BsonElement("flowInstanceId")]
    public string? FlowInstanceId { get; set; }

    [BsonElement("flowInstanceRevision")]
    public long? FlowInstanceRevision { get; set; }

    [BsonElement("flowExecutionEpoch")]
    public int? FlowExecutionEpoch { get; set; }

    [BsonElement("flowExecutionEpochId")]
    public string? FlowExecutionEpochId { get; set; }

    [BsonElement("flowExecutionEpochRevision")]
    public long? FlowExecutionEpochRevision { get; set; }

    [BsonElement("flowStepId")]
    public string? FlowStepId { get; set; }

    [BsonElement("flowBranchId")]
    public string? FlowBranchId { get; set; }

    [BsonElement("flowStepInstanceId")]
    public string? FlowStepInstanceId { get; set; }

    [BsonElement("flowStepInstanceRevision")]
    public long? FlowStepInstanceRevision { get; set; }

    [BsonElement("flowContributionPolicy")]
    public string? FlowContributionPolicy { get; set; }

    [BsonElement("flowContributionPolicyHash")]
    public string? FlowContributionPolicyHash { get; set; }

    [BsonElement("flowEffectiveStatus")]
    public string? FlowEffectiveStatus { get; set; }

    [BsonElement("flowContributionProvenanceHash")]
    public string? FlowContributionProvenanceHash { get; set; }

    [BsonElement("p8ConfigOwnerId")]
    public string? P8ConfigOwnerId { get; set; }

    [BsonElement("p8ConfigId")]
    public string? P8ConfigId { get; set; }

    [BsonElement("p8ConfigVersionId")]
    public string? P8ConfigVersionId { get; set; }

    [BsonElement("p8ConfigVersionNo")]
    public int? P8ConfigVersionNo { get; set; }

    [BsonElement("p8ConfigRevision")]
    public long? P8ConfigRevision { get; set; }

    [BsonElement("p8ConfigHash")]
    public string? P8ConfigHash { get; set; }

    [BsonElement("p8ConfigBundleHash")]
    public string P8ConfigBundleHash { get; set; } = default!;

    [BsonElement("p9CatalogVersion")]
    public string P9CatalogVersion { get; set; } = default!;

    [BsonElement("p9CatalogRawSha256")]
    public string P9CatalogRawSha256 { get; set; } = default!;

    [BsonElement("p9CatalogSemanticSha256")]
    public string P9CatalogSemanticSha256 { get; set; } = default!;

    [BsonElement("p9SchemaRawSha256")]
    public string P9SchemaRawSha256 { get; set; } = default!;

    [BsonElement("p9SchemaSemanticSha256")]
    public string P9SchemaSemanticSha256 { get; set; } = default!;

    [BsonElement("p9StageLockSha256")]
    public string P9StageLockSha256 { get; set; } = default!;

    [BsonElement("periodKey")]
    public string PeriodKey { get; set; } = default!;

    [BsonElement("periodInstanceKey")]
    public string PeriodInstanceKey { get; set; } = default!;

    [BsonElement("periodKind")]
    public string PeriodKind { get; set; } = default!;

    [BsonElement("periodStartUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PeriodStartUtc { get; set; }

    [BsonElement("periodEndUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PeriodEndUtc { get; set; }

    [BsonElement("timeAxis")]
    public string TimeAxis { get; set; } = default!;

    [BsonElement("actualCapturePlan")]
    public StatisticReconciliationActualCapturePlan ActualCapturePlan
        { get; set; } = default!;

    [BsonElement("actualCapturePlanSha256")]
    public string ActualCapturePlanSha256 { get; set; } = default!;

    [BsonElement("actualConfigurationBundleSha256")]
    public string ActualConfigurationBundleSha256 { get; set; } = default!;

    [BsonElement("remediationEvidenceSha256")]
    [BsonIgnoreIfNull]
    public string? RemediationEvidenceSha256 { get; set; }

    [BsonElement("bindingSha256")]
    public string BindingSha256 { get; set; } = default!;
}
