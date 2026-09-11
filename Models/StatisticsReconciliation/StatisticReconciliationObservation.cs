using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.StatisticsReconciliation;

public static class StatisticReconciliationObservationRecordKinds
{
    public const string SourceDecision = "SOURCE_DECISION";
    public const string ConfigurationPin = "CONFIGURATION_PIN";
    public const string LineagePin = "LINEAGE_PIN";
    public const string ExpectedMetricPlan = "EXPECTED_METRIC_PLAN";
    public const string ExpectedAtom = "EXPECTED_ATOM";
    public const string ActualLayer = "ACTUAL_LAYER";
    public const string ActualAtom = "ACTUAL_ATOM";
    public const string ActualSourceDecision = "ACTUAL_SOURCE_DECISION";
    public const string GenerationCommit = "GENERATION_COMMIT";
}

[BsonIgnoreExtraElements]
[BsonCollection("work_report_statistic_reconciliation_observations")]
public sealed class StatisticReconciliationObservation
{
    [BsonId]
    public string Id { get; set; } = default!;

    [BsonElement("schemaVersion")]
    public string SchemaVersion { get; set; } = "P10_EXPECTED_OBSERVATION_V2";

    [BsonElement("recordKind")]
    public string RecordKind { get; set; } = default!;

    [BsonElement("reconciliationId")]
    public string ReconciliationId { get; set; } = default!;

    [BsonElement("immutableIdentitySha256")]
    public string ImmutableIdentitySha256 { get; set; } = default!;

    [BsonElement("immutableHeaderSha256")]
    public string ImmutableHeaderSha256 { get; set; } = default!;

    [BsonElement("generationId")]
    public string GenerationId { get; set; } = default!;

    [BsonElement("generationSemanticSha256")]
    public string GenerationSemanticSha256 { get; set; } = default!;

    [BsonElement("algorithmRevision")]
    public string AlgorithmRevision { get; set; } = default!;

    [BsonElement("algorithmSha256")]
    public string AlgorithmSha256 { get; set; } = default!;

    [BsonElement("sourceSetSha256")]
    public string SourceSetSha256 { get; set; } = default!;

    [BsonElement("actualMembershipSemanticSha256")]
    [BsonIgnoreIfNull]
    public string? ActualMembershipSemanticSha256 { get; set; }

    [BsonElement("actualSourceDecisionManifestSha256")]
    [BsonIgnoreIfNull]
    public string? ActualSourceDecisionManifestSha256 { get; set; }

    [BsonElement("lifecycleMetricScopeSha256")]
    [BsonIgnoreIfNull]
    public string? LifecycleMetricScopeSha256 { get; set; }

    [BsonElement("typedSemanticSha256")]
    public string TypedSemanticSha256 { get; set; } = default!;

    [BsonElement("metricPlanSha256")]
    public string MetricPlanSha256 { get; set; } = default!;

    [BsonElement("inputBindingSha256")]
    public string InputBindingSha256 { get; set; } = default!;

    [BsonElement("lifecycleSemanticSha256")]
    public string LifecycleSemanticSha256 { get; set; } = default!;

    [BsonElement("contributionSemanticSha256")]
    public string ContributionSemanticSha256 { get; set; } = default!;

    [BsonElement("recheckCaptureBindingSha256")]
    [BsonIgnoreIfNull]
    public string? RecheckCaptureBindingSha256 { get; set; }
    [BsonElement("capturedResultPinSetSha256")]
    public string CapturedResultPinSetSha256 { get; set; } = default!;

    [BsonElement("capturedExportPinSetSha256")]
    public string CapturedExportPinSetSha256 { get; set; } = default!;

    [BsonElement("summaryExpectedReconciliationId")]
    [BsonIgnoreIfNull]
    public string? SummaryExpectedReconciliationId { get; set; }

    [BsonElement("summaryExpectedGenerationId")]
    [BsonIgnoreIfNull]
    public string? SummaryExpectedGenerationId { get; set; }

    [BsonElement("summaryExpectedGenerationSha256")]
    [BsonIgnoreIfNull]
    public string? SummaryExpectedGenerationSha256 { get; set; }

    [BsonElement("summaryExpectedMetricPlanSha256")]
    [BsonIgnoreIfNull]
    public string? SummaryExpectedMetricPlanSha256 { get; set; }

    [BsonElement("summaryExpectedMetricPlanEntryCount")]
    [BsonIgnoreIfNull]
    public int? SummaryExpectedMetricPlanEntryCount { get; set; }

    [BsonElement("summaryExpectedManifestSha256")]
    [BsonIgnoreIfNull]
    public string? SummaryExpectedManifestSha256 { get; set; }

    [BsonElement("summaryExpectedDocumentCount")]
    [BsonIgnoreIfNull]
    public int? SummaryExpectedDocumentCount { get; set; }

    [BsonElement("summaryExpectedMembershipSemanticSha256")]
    [BsonIgnoreIfNull]
    public string? SummaryExpectedMembershipSemanticSha256 { get; set; }

    [BsonElement("summaryExpectedRuntimeKind")]
    [BsonIgnoreIfNull]
    public string? SummaryExpectedRuntimeKind { get; set; }

    [BsonElement("summaryExpectedIdentitySetSha256")]
    [BsonIgnoreIfNull]
    public string? SummaryExpectedIdentitySetSha256 { get; set; }

    [BsonElement("summaryMetricIdentityCount")]
    [BsonIgnoreIfNull]
    public int? SummaryMetricIdentityCount { get; set; }

    [BsonElement("summaryPlanBindingSha256")]
    [BsonIgnoreIfNull]
    public string? SummaryPlanBindingSha256 { get; set; }

    [BsonElement("summaryMappingManifestSha256")]
    [BsonIgnoreIfNull]
    public string? SummaryMappingManifestSha256 { get; set; }

    // Reserved server-only extension point for the independently derived
    // T24-T26 lifecycle manifest. It is part of the generation binding when
    // present and can never be supplied by an HTTP capture request.
    [BsonElement("lifecycleManifestSha256")]
    [BsonIgnoreIfNull]
    public string? LifecycleManifestSha256 { get; set; }

    [BsonElement("lifecycleObservationCount")]
    [BsonIgnoreIfNull]
    public int? LifecycleObservationCount { get; set; }

    [BsonElement("actualRelationalProofBindingSha256")]
    [BsonIgnoreIfNull]
    public string? ActualRelationalProofBindingSha256 { get; set; }

    // The complete server-only V7 relational proof is stored once on the
    // generation commit. Every generation document carries the binding SHA so
    // immutable replay validation cannot mix content from another proof.
    [BsonElement("actualRelationalProof")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationObservationActualRelationalProof?
        ActualRelationalProof { get; set; }

    [BsonElement("tenantUnitId")]
    public string? TenantUnitId { get; set; }

    [BsonElement("workId")]
    public string WorkId { get; set; } = default!;

    [BsonElement("scopeAssignmentId")]
    public string ScopeAssignmentId { get; set; } = default!;

    [BsonElement("periodKey")]
    public string PeriodKey { get; set; } = default!;

    [BsonElement("periodInstanceKey")]
    public string PeriodInstanceKey { get; set; } = default!;

    [BsonElement("conceptKey")]
    public string ConceptKey { get; set; } = default!;

    [BsonElement("grain")]
    public string Grain { get; set; } = default!;

    [BsonElement("timeAxis")]
    public string TimeAxis { get; set; } = default!;

    [BsonElement("filterSha256")]
    public string FilterSha256 { get; set; } = default!;

    [BsonElement("dynamicFormVersionId")]
    public string DynamicFormVersionId { get; set; } = default!;

    [BsonElement("dynamicFormSchemaSha256")]
    public string DynamicFormSchemaSha256 { get; set; } = default!;

    [BsonElement("runtimeKind")]
    [BsonIgnoreIfNull]
    public string? RuntimeKind { get; set; }

    [BsonElement("flowTemplateVersionId")]
    [BsonIgnoreIfNull]
    public string? FlowTemplateVersionId { get; set; }

    [BsonElement("flowPayloadSha256")]
    public string? FlowPayloadSha256 { get; set; }

    [BsonElement("flowInstanceId")]
    public string? FlowInstanceId { get; set; }

    [BsonElement("executionEpochId")]
    public string? ExecutionEpochId { get; set; }

    [BsonElement("executionEpoch")]
    [BsonIgnoreIfNull]
    public int? ExecutionEpoch { get; set; }

    [BsonElement("executionEpochRevision")]
    [BsonIgnoreIfNull]
    public long? ExecutionEpochRevision { get; set; }

    [BsonElement("p8ConfigurationOwnerId")]
    public string P8ConfigurationOwnerId { get; set; } = default!;

    [BsonElement("p8ConfigurationBundleSha256")]
    public string P8ConfigurationBundleSha256 { get; set; } = default!;

    [BsonElement("actualConfigurationBundleSha256")]
    public string ActualConfigurationBundleSha256 { get; set; } = default!;

    [BsonElement("catalogPins")]
    public StatisticReconciliationObservationCatalogPins CatalogPins { get; set; } = new();

    [BsonElement("sourceDecision")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationObservationSourceDecision? SourceDecision { get; set; }

    [BsonElement("configurationPin")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationObservationConfigurationPin? ConfigurationPin { get; set; }

    [BsonElement("lineagePin")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationObservationLineagePin? LineagePin { get; set; }

    [BsonElement("metricPlan")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationObservationMetricPlan? MetricPlan { get; set; }

    [BsonElement("atom")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationObservationAtom? Atom { get; set; }

    [BsonElement("actualLayer")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationObservationActualLayer? ActualLayer { get; set; }

    [BsonElement("actualSourceDecision")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationObservationActualSourceDecision?
        ActualSourceDecision { get; set; }

    [BsonElement("commit")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationObservationCommit? Commit { get; set; }

    [BsonElement("documentSemanticSha256")]
    public string DocumentSemanticSha256 { get; set; } = default!;

    [BsonElement("createdAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class StatisticReconciliationObservationCatalogPins
{
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
    [BsonElement("candidateChainId")]
    public string CandidateChainId { get; set; } = default!;
    [BsonElement("candidatePromptId")]
    public string CandidatePromptId { get; set; } = default!;
    [BsonElement("candidateCatalogVersion")]
    public string CandidateCatalogVersion { get; set; } = default!;
    [BsonElement("candidateCatalogRawSha256")]
    public string CandidateCatalogRawSha256 { get; set; } = default!;
    [BsonElement("candidateCatalogSemanticSha256")]
    public string CandidateCatalogSemanticSha256 { get; set; } = default!;
    [BsonElement("candidateSchemaRawSha256")]
    public string CandidateSchemaRawSha256 { get; set; } = default!;
    [BsonElement("candidateSchemaSemanticSha256")]
    public string CandidateSchemaSemanticSha256 { get; set; } = default!;
    [BsonElement("candidateStageLockSha256")]
    public string CandidateStageLockSha256 { get; set; } = default!;
    [BsonElement("catalogPinSetSha256")]
    public string CatalogPinSetSha256 { get; set; } = default!;
}

public sealed class StatisticReconciliationObservationSourceDecision
{
    [BsonElement("stableSourceId")]
    public string StableSourceId { get; set; } = default!;
    [BsonElement("identityKey")]
    public string IdentityKey { get; set; } = default!;
    [BsonElement("reportId")]
    public string ReportId { get; set; } = default!;
    [BsonElement("workAssignmentId")]
    [BsonIgnoreIfNull]
    public string? WorkAssignmentId { get; set; }
    [BsonElement("sourceStableIdentitySha256")]
    [BsonIgnoreIfNull]
    public string? SourceStableIdentitySha256 { get; set; }
    [BsonElement("payloadDocumentId")]
    public string PayloadDocumentId { get; set; } = default!;
    [BsonElement("payloadRevision")]
    public int PayloadRevision { get; set; }
    [BsonElement("payloadOwnerSha256")]
    public string PayloadOwnerSha256 { get; set; } = default!;
    [BsonElement("payloadCanonicalSha256")]
    public string PayloadCanonicalSha256 { get; set; } = default!;
    [BsonElement("lifecycleRevision")]
    public int LifecycleRevision { get; set; }
    [BsonElement("lifecycleSha256")]
    public string LifecycleSha256 { get; set; } = default!;
    [BsonElement("lifecycleStatus")]
    public string LifecycleStatus { get; set; } = default!;
    [BsonElement("isEffective")]
    public bool IsEffective { get; set; }
    [BsonElement("isLocked")]
    public bool IsLocked { get; set; }
    [BsonElement("runtimeDisposition")]
    public string RuntimeDisposition { get; set; } = default!;
    [BsonElement("disposition")]
    public string Disposition { get; set; } = default!;
    [BsonElement("reasonCode")]
    public string ReasonCode { get; set; } = default!;
    [BsonElement("contributionPolicy")]
    public string ContributionPolicy { get; set; } = default!;
    [BsonElement("contributionVersionId")]
    public string ContributionVersionId { get; set; } = default!;
    [BsonElement("contributionRevision")]
    public long ContributionRevision { get; set; }
    [BsonElement("contributionPolicySha256")]
    public string ContributionPolicySha256 { get; set; } = default!;
    [BsonElement("contributionProvenanceId")]
    public string ContributionProvenanceId { get; set; } = default!;
    [BsonElement("contributionProvenanceSha256")]
    public string ContributionProvenanceSha256 { get; set; } = default!;
    [BsonElement("decisionSemanticSha256")]
    public string DecisionSemanticSha256 { get; set; } = default!;
    [BsonElement("authoritativeRuntime")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationObservationAuthoritativeRuntimePin?
        AuthoritativeRuntime { get; set; }
}

public sealed class StatisticReconciliationObservationAuthoritativeRuntimePin
{
    [BsonElement("runtimeKind")]
    public string RuntimeKind { get; set; } = default!;
    [BsonElement("flowTemplateVersionId"), BsonIgnoreIfNull]
    public string? FlowTemplateVersionId { get; set; }
    [BsonElement("flowPayloadSha256"), BsonIgnoreIfNull]
    public string? FlowPayloadSha256 { get; set; }
    [BsonElement("flowInstanceId"), BsonIgnoreIfNull]
    public string? FlowInstanceId { get; set; }
    [BsonElement("flowBranchId"), BsonIgnoreIfNull]
    public string? FlowBranchId { get; set; }
    [BsonElement("flowStepId"), BsonIgnoreIfNull]
    public string? FlowStepId { get; set; }
    [BsonElement("flowStepInstanceId"), BsonIgnoreIfNull]
    public string? FlowStepInstanceId { get; set; }
    [BsonElement("flowStepRevision"), BsonIgnoreIfNull]
    public long? FlowStepRevision { get; set; }
    [BsonElement("flowAttemptNo"), BsonIgnoreIfNull]
    public int? FlowAttemptNo { get; set; }
    [BsonElement("executionEpochId"), BsonIgnoreIfNull]
    public string? ExecutionEpochId { get; set; }
    [BsonElement("executionEpoch"), BsonIgnoreIfNull]
    public int? ExecutionEpoch { get; set; }
    [BsonElement("currentExecutionEpochId"), BsonIgnoreIfNull]
    public string? CurrentExecutionEpochId { get; set; }
    [BsonElement("currentExecutionEpoch"), BsonIgnoreIfNull]
    public int? CurrentExecutionEpoch { get; set; }
    [BsonElement("executionEpochRevision"), BsonIgnoreIfNull]
    public long? ExecutionEpochRevision { get; set; }
    [BsonElement("isCanonicalEpoch"), BsonIgnoreIfNull]
    public bool? IsCanonicalEpoch { get; set; }
    [BsonElement("approvalCommandId"), BsonIgnoreIfNull]
    public string? ApprovalCommandId { get; set; }
    [BsonElement("approvalEventKey"), BsonIgnoreIfNull]
    public string? ApprovalEventKey { get; set; }
    [BsonElement("mappingReceiptId"), BsonIgnoreIfNull]
    public string? MappingReceiptId { get; set; }
    [BsonElement("mappingProvenanceId"), BsonIgnoreIfNull]
    public string? MappingProvenanceId { get; set; }
    [BsonElement("mappingProvenanceSha256"), BsonIgnoreIfNull]
    public string? MappingProvenanceSha256 { get; set; }
    [BsonElement("mappingResultSemanticSha256"), BsonIgnoreIfNull]
    public string? MappingResultSemanticSha256 { get; set; }
    [BsonElement("mappingResultPayloadRevision"), BsonIgnoreIfNull]
    public int? MappingResultPayloadRevision { get; set; }
    [BsonElement("mappingResultPayloadSha256"), BsonIgnoreIfNull]
    public string? MappingResultPayloadSha256 { get; set; }
    [BsonElement("mappingFlowVersionId"), BsonIgnoreIfNull]
    public string? MappingFlowVersionId { get; set; }
    [BsonElement("mappingFlowVersionNo"), BsonIgnoreIfNull]
    public int? MappingFlowVersionNo { get; set; }
    [BsonElement("mappingFlowPayloadSha256"), BsonIgnoreIfNull]
    public string? MappingFlowPayloadSha256 { get; set; }
    [BsonElement("mappingLocked"), BsonIgnoreIfNull]
    public bool? MappingLocked { get; set; }
    [BsonElement("configVersionId"), BsonIgnoreIfNull]
    public string? ConfigVersionId { get; set; }
    [BsonElement("configSha256"), BsonIgnoreIfNull]
    public string? ConfigSha256 { get; set; }
    [BsonElement("membershipSignatureSha256")]
    public string MembershipSignatureSha256 { get; set; } = default!;
    [BsonElement("contributionPolicy")]
    public string ContributionPolicy { get; set; } = default!;
    [BsonElement("contributionPolicySha256")]
    public string ContributionPolicySha256 { get; set; } = default!;
    [BsonElement("contributionProvenanceId")]
    public string ContributionProvenanceId { get; set; } = default!;
    [BsonElement("contributionProvenanceSha256")]
    public string ContributionProvenanceSha256 { get; set; } = default!;
    [BsonElement("lifecycleOwnerSha256")]
    public string LifecycleOwnerSha256 { get; set; } = default!;
    [BsonElement("runtimeSemanticSha256")]
    public string RuntimeSemanticSha256 { get; set; } = default!;
}
public sealed class StatisticReconciliationObservationConfigurationPin
{
    [BsonElement("kind")]
    public string Kind { get; set; } = default!;
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
    [BsonElement("configSha256")]
    public string ConfigSha256 { get; set; } = default!;
}

public sealed class StatisticReconciliationObservationLineagePin
{
    [BsonElement("layer")]
    public string Layer { get; set; } = default!;
    [BsonElement("ownerId")]
    public string OwnerId { get; set; } = default!;
    [BsonElement("versionId")]
    public string VersionId { get; set; } = default!;
    [BsonElement("revision")]
    public long Revision { get; set; }
    [BsonElement("sha256")]
    public string Sha256 { get; set; } = default!;
}

public sealed class StatisticReconciliationObservationMetricPlan
{
    [BsonElement("schemaVersion")]
    public string SchemaVersion { get; set; } = default!;
    [BsonElement("identitySha256")]
    public string IdentitySha256 { get; set; } = default!;
    [BsonElement("family")]
    public string Family { get; set; } = default!;
    [BsonElement("kind")]
    public string Kind { get; set; } = default!;
    [BsonElement("metricId")]
    public string MetricId { get; set; } = default!;
    [BsonElement("fieldId")]
    [BsonIgnoreIfNull]
    public string? FieldId { get; set; }
    [BsonElement("tableId")]
    [BsonIgnoreIfNull]
    public string? TableId { get; set; }
    [BsonElement("rowId")]
    [BsonIgnoreIfNull]
    public string? RowId { get; set; }
    [BsonElement("labelId")]
    [BsonIgnoreIfNull]
    public string? LabelId { get; set; }
    [BsonElement("basicScope")]
    [BsonIgnoreIfNull]
    public string? BasicScope { get; set; }
    [BsonElement("basicScopeId")]
    [BsonIgnoreIfNull]
    public string? BasicScopeId { get; set; }
    [BsonElement("advancedGrain")]
    [BsonIgnoreIfNull]
    public string? AdvancedGrain { get; set; }
    [BsonElement("diffKind")]
    [BsonIgnoreIfNull]
    public string? DiffKind { get; set; }
    [BsonElement("periodKey")]
    public string PeriodKey { get; set; } = default!;
    [BsonElement("jsonPointer")]
    public string JsonPointer { get; set; } = default!;
    [BsonElement("valueType")]
    public string ValueType { get; set; } = default!;
    [BsonElement("unordered")]
    public bool Unordered { get; set; }
    [BsonElement("expandArray")]
    public bool ExpandArray { get; set; }
    [BsonElement("operations")]
    public List<string> Operations { get; set; } = [];
    [BsonElement("transitionMode")]
    public string TransitionMode { get; set; } = default!;
    [BsonElement("beforeJsonPointer")]
    [BsonIgnoreIfNull]
    public string? BeforeJsonPointer { get; set; }
    [BsonElement("afterJsonPointer")]
    [BsonIgnoreIfNull]
    public string? AfterJsonPointer { get; set; }
    [BsonElement("differenceOperation")]
    [BsonIgnoreIfNull]
    public string? DifferenceOperation { get; set; }
    [BsonElement("transitionKind")]
    [BsonIgnoreIfNull]
    public string? TransitionKind { get; set; }
    [BsonElement("planEntrySha256")]
    public string PlanEntrySha256 { get; set; } = default!;
}

public sealed class StatisticReconciliationObservationAtom
{
    [BsonElement("identitySha256")]
    public string IdentitySha256 { get; set; } = default!;
    [BsonElement("family")]
    public string Family { get; set; } = default!;
    [BsonElement("kind")]
    public string Kind { get; set; } = default!;
    [BsonElement("metricId")]
    public string MetricId { get; set; } = default!;
    [BsonElement("fieldId")]
    public string? FieldId { get; set; }
    [BsonElement("tableId")]
    public string? TableId { get; set; }
    [BsonElement("rowId")]
    public string? RowId { get; set; }
    [BsonElement("labelId")]
    public string? LabelId { get; set; }
    [BsonElement("basicScope")]
    public string? BasicScope { get; set; }
    [BsonElement("basicScopeId")]
    public string? BasicScopeId { get; set; }
    [BsonElement("flowBranchId")]
    public string? FlowBranchId { get; set; }
    [BsonElement("flowStepId")]
    public string? FlowStepId { get; set; }
    [BsonElement("advancedGrain")]
    public string? AdvancedGrain { get; set; }
    [BsonElement("diffKind")]
    public string? DiffKind { get; set; }
    [BsonElement("periodKey")]
    public string? PeriodKey { get; set; }
    [BsonElement("atomKind")]
    public string AtomKind { get; set; } = default!;
    [BsonElement("valueType")]
    public string ValueType { get; set; } = default!;
    [BsonElement("valueState")]
    public string ValueState { get; set; } = default!;
    [BsonElement("canonicalValue")]
    public string CanonicalValue { get; set; } = default!;
    [BsonElement("decimalScale")]
    public int DecimalScale { get; set; }
    [BsonElement("occurrenceCount")]
    public long OccurrenceCount { get; set; }
    [BsonElement("reportCount")]
    public long ReportCount { get; set; }
    [BsonElement("rowCount")]
    public long RowCount { get; set; }
    [BsonElement("numericValueCount")]
    public long NumericValueCount { get; set; }
    [BsonElement("valueIdentitySha256")]
    public string ValueIdentitySha256 { get; set; } = default!;
    [BsonElement("atomSemanticSha256")]
    public string AtomSemanticSha256 { get; set; } = default!;
    [BsonElement("transitionLeg")]
    [BsonIgnoreIfNull]
    public string? TransitionLeg { get; set; }
    [BsonElement("transitionKind")]
    [BsonIgnoreIfNull]
    public string? TransitionKind { get; set; }
    [BsonElement("collectionSemantics")]
    [BsonIgnoreIfNull]
    public string? CollectionSemantics { get; set; }
}

public sealed class StatisticReconciliationObservationActualSourceDecision
{
    [BsonElement("ordinal")]
    public int Ordinal { get; set; }
    [BsonElement("workId")]
    public string WorkId { get; set; } = default!;
    [BsonElement("workAssignmentId")]
    public string WorkAssignmentId { get; set; } = default!;
    [BsonElement("reportId")]
    public string ReportId { get; set; } = default!;
    [BsonElement("sourceStableIdentitySha256")]
    public string SourceStableIdentitySha256 { get; set; } = default!;
    [BsonElement("neutralStableIdentitySha256")]
    public string NeutralStableIdentitySha256 { get; set; } = default!;
    [BsonElement("included")]
    public bool Included { get; set; }
    [BsonElement("ownerDecisionCode")]
    public string OwnerDecisionCode { get; set; } = default!;
    [BsonElement("ownerOrdinal")]
    public int OwnerOrdinal { get; set; }
    [BsonElement("ownerStateSemanticSha256")]
    public string OwnerStateSemanticSha256 { get; set; } = default!;
    [BsonElement("sourceDecisionSemanticSha256")]
    public string SourceDecisionSemanticSha256 { get; set; } = default!;
    [BsonElement("semanticSha256")]
    public string SemanticSha256 { get; set; } = default!;
}

public sealed class StatisticReconciliationObservationCommit
{
    [BsonElement("sourceDecisionCount")]
    public int SourceDecisionCount { get; set; }
    [BsonElement("metricPlanEntryCount")]
    public int MetricPlanEntryCount { get; set; }
    [BsonElement("configurationPinCount")]
    public int ConfigurationPinCount { get; set; }
    [BsonElement("lineagePinCount")]
    public int LineagePinCount { get; set; }
    [BsonElement("atomCount")]
    public int AtomCount { get; set; }
    [BsonElement("documentCount")]
    public int DocumentCount { get; set; }
    [BsonElement("manifestSha256")]
    public string ManifestSha256 { get; set; } = default!;
    [BsonElement("membershipSemanticSha256")]
    [BsonIgnoreIfNull]
    public string? MembershipSemanticSha256 { get; set; }

    [BsonElement("summaryIdentityDescriptors")]
    [BsonIgnoreIfNull]
    public List<StatisticReconciliationObservationSummaryIdentityDescriptor>?
        SummaryIdentityDescriptors { get; set; }
}

public sealed class StatisticReconciliationObservationActualRelationalProof
{
    [BsonElement("schemaVersion")]
    public string SchemaVersion { get; set; } = default!;
    [BsonElement("factsSchemaVersion")]
    public string FactsSchemaVersion { get; set; } = default!;
    [BsonElement("summaryPlanBindingSha256")]
    public string SummaryPlanBindingSha256 { get; set; } = default!;
    [BsonElement("centralProjectedPlanSha256")]
    public string CentralProjectedPlanSha256 { get; set; } = default!;
    [BsonElement("centralPlanProjectionProofSha256")]
    public string CentralPlanProjectionProofSha256 { get; set; } = default!;
    [BsonElement("sourceCaptureSha256")]
    public string SourceCaptureSha256 { get; set; } = default!;
    [BsonElement("directCaptureSha256")]
    public string DirectCaptureSha256 { get; set; } = default!;
    [BsonElement("aggregateCaptureSha256")]
    public string AggregateCaptureSha256 { get; set; } = default!;
    [BsonElement("basicCaptureSha256")]
    public string BasicCaptureSha256 { get; set; } = default!;
    [BsonElement("advancedCaptureSha256")]
    public string AdvancedCaptureSha256 { get; set; } = default!;
    [BsonElement("diffCaptureSha256")]
    public string DiffCaptureSha256 { get; set; } = default!;
    [BsonElement("apiCaptureSha256"), BsonIgnoreIfNull]
    public string? ApiCaptureSha256 { get; set; }
    [BsonElement("exportCaptureSha256")]
    public string ExportCaptureSha256 { get; set; } = default!;
    [BsonElement("rawProofSha256")]
    public string RawProofSha256 { get; set; } = default!;
    [BsonElement("rawSourceManifestSha256")]
    public string RawSourceManifestSha256 { get; set; } = default!;
    [BsonElement("rawSourceCount")]
    public int RawSourceCount { get; set; }
    [BsonElement("rawAtomManifestSha256")]
    public string RawAtomManifestSha256 { get; set; } = default!;
    [BsonElement("rawAtomCount")]
    public int RawAtomCount { get; set; }
    [BsonElement("rawDoubleCollectProofSha256")]
    public string RawDoubleCollectProofSha256 { get; set; } = default!;
    [BsonElement("summaryParityProofSha256")]
    public string SummaryParityProofSha256 { get; set; } = default!;
    [BsonElement("summaryParityRawManifestSha256")]
    public string SummaryParityRawManifestSha256 { get; set; } = default!;
    [BsonElement("summaryParityRawAtomCount")]
    public int SummaryParityRawAtomCount { get; set; }
    [BsonElement("summaryParityOwnerManifestSha256")]
    public string SummaryParityOwnerManifestSha256 { get; set; } = default!;
    [BsonElement("summaryParityOwnerItemCount")]
    public int SummaryParityOwnerItemCount { get; set; }
    [BsonElement("summaryParityRelationManifestSha256")]
    public string SummaryParityRelationManifestSha256 { get; set; } = default!;
    [BsonElement("summaryParityRelationCount")]
    public int SummaryParityRelationCount { get; set; }
    [BsonElement("directParityProofSha256")]
    public string DirectParityProofSha256 { get; set; } = default!;
    [BsonElement("directParityOwnerManifestSha256")]
    public string DirectParityOwnerManifestSha256 { get; set; } = default!;
    [BsonElement("directParityOwnerItemCount")]
    public int DirectParityOwnerItemCount { get; set; }
    [BsonElement("directParityRelationManifestSha256")]
    public string DirectParityRelationManifestSha256 { get; set; } = default!;
    [BsonElement("directParityRelationCount")]
    public int DirectParityRelationCount { get; set; }
    [BsonElement("extendedRawResolutionProofSha256")]
    public string ExtendedRawResolutionProofSha256 { get; set; } = default!;
    [BsonElement("extendedAdvancedProofSha256")]
    public string ExtendedAdvancedProofSha256 { get; set; } = default!;
    [BsonElement("extendedAdvancedSchemaOptionBindingSha256")]
    public string ExtendedAdvancedSchemaOptionBindingSha256 { get; set; } = default!;
    [BsonElement("extendedAdvancedDescriptorProjectionManifestSha256")]
    public string ExtendedAdvancedDescriptorProjectionManifestSha256 { get; set; } = default!;
    [BsonElement("extendedAdvancedDescriptorProjectionCount")]
    public int ExtendedAdvancedDescriptorProjectionCount { get; set; }
    [BsonElement("extendedAdvancedTypedAtomManifestSha256")]
    public string ExtendedAdvancedTypedAtomManifestSha256 { get; set; } = default!;
    [BsonElement("extendedAdvancedTypedAtomCount")]
    public int ExtendedAdvancedTypedAtomCount { get; set; }
    [BsonElement("extendedAdvancedDescriptorContributionManifestSha256")]
    public string ExtendedAdvancedDescriptorContributionManifestSha256 { get; set; } = default!;
    [BsonElement("extendedAdvancedDescriptorContributionCount")]
    public int ExtendedAdvancedDescriptorContributionCount { get; set; }
    [BsonElement("extendedDiffProofSha256")]
    public string ExtendedDiffProofSha256 { get; set; } = default!;
    [BsonElement("extendedDiffPeriodBindingSha256")]
    public string ExtendedDiffPeriodBindingSha256 { get; set; } = default!;
    [BsonElement("extendedDiffTypedAtomManifestSha256")]
    public string ExtendedDiffTypedAtomManifestSha256 { get; set; } = default!;
    [BsonElement("extendedDiffTypedAtomCount")]
    public int ExtendedDiffTypedAtomCount { get; set; }
    [BsonElement("extendedDiffSourcePairManifestSha256")]
    public string ExtendedDiffSourcePairManifestSha256 { get; set; } = default!;
    [BsonElement("extendedDiffSourcePairCount")]
    public int ExtendedDiffSourcePairCount { get; set; }
    [BsonElement("extendedPartitionDoubleCollectManifestSha256")]
    public string ExtendedPartitionDoubleCollectManifestSha256 { get; set; } = default!;
    [BsonElement("extendedPartitionDoubleCollectCount")]
    public int ExtendedPartitionDoubleCollectCount { get; set; }
    [BsonElement("extendedOwnerParityProofSha256")]
    public string ExtendedOwnerParityProofSha256 { get; set; } = default!;
    [BsonElement("extendedOwnerDescriptorManifestSha256")]
    public string ExtendedOwnerDescriptorManifestSha256 { get; set; } = default!;
    [BsonElement("extendedOwnerDescriptorCount")]
    public int ExtendedOwnerDescriptorCount { get; set; }
    [BsonElement("extendedOwnerTypedAtomManifestSha256")]
    public string ExtendedOwnerTypedAtomManifestSha256 { get; set; } = default!;
    [BsonElement("extendedOwnerTypedAtomCount")]
    public int ExtendedOwnerTypedAtomCount { get; set; }
    [BsonElement("extendedOwnerItemManifestSha256")]
    public string ExtendedOwnerItemManifestSha256 { get; set; } = default!;
    [BsonElement("extendedOwnerItemCount")]
    public int ExtendedOwnerItemCount { get; set; }
    [BsonElement("extendedOwnerRelationManifestSha256")]
    public string ExtendedOwnerRelationManifestSha256 { get; set; } = default!;
    [BsonElement("extendedOwnerRelationCount")]
    public int ExtendedOwnerRelationCount { get; set; }
    [BsonElement("crossViewAuthorizationRelationSha256")]
    public string CrossViewAuthorizationRelationSha256 { get; set; } = default!;
    [BsonElement("crossViewResolutionSha256")]
    public string CrossViewResolutionSha256 { get; set; } = default!;
    [BsonElement("crossViewProofSha256")]
    public string CrossViewProofSha256 { get; set; } = default!;
    [BsonElement("crossViewCompatibilityProofSha256")]
    public string CrossViewCompatibilityProofSha256 { get; set; } = default!;
    [BsonElement("crossViewOriginalApiSemanticSha256")]
    public string CrossViewOriginalApiSemanticSha256 { get; set; } = default!;
    [BsonElement("crossViewOriginalExportSemanticSha256")]
    public string CrossViewOriginalExportSemanticSha256 { get; set; } = default!;
    [BsonElement("factsSemanticSha256")]
    public string FactsSemanticSha256 { get; set; } = default!;
    [BsonElement("firstRelationalResolutionSha256")]
    public string FirstRelationalResolutionSha256 { get; set; } = default!;
    [BsonElement("secondRelationalResolutionSha256")]
    public string SecondRelationalResolutionSha256 { get; set; } = default!;
    [BsonElement("doubleResolveProofSha256")]
    public string DoubleResolveProofSha256 { get; set; } = default!;
    [BsonElement("semanticSha256")]
    public string SemanticSha256 { get; set; } = default!;
}
public sealed class StatisticReconciliationObservationSummaryIdentityDescriptor
{
    [BsonElement("family")]
    public string Family { get; set; } = default!;
    [BsonElement("kind")]
    public string Kind { get; set; } = default!;
    [BsonElement("metricId")]
    public string MetricId { get; set; } = default!;
    [BsonElement("fieldId")]
    [BsonIgnoreIfNull]
    public string? FieldId { get; set; }
    [BsonElement("tableId")]
    [BsonIgnoreIfNull]
    public string? TableId { get; set; }
    [BsonElement("rowId")]
    [BsonIgnoreIfNull]
    public string? RowId { get; set; }
    [BsonElement("labelId")]
    [BsonIgnoreIfNull]
    public string? LabelId { get; set; }
    [BsonElement("basicScope")]
    [BsonIgnoreIfNull]
    public string? BasicScope { get; set; }
    [BsonElement("basicScopeId")]
    [BsonIgnoreIfNull]
    public string? BasicScopeId { get; set; }
    [BsonElement("advancedGrain")]
    [BsonIgnoreIfNull]
    public string? AdvancedGrain { get; set; }
    [BsonElement("diffKind")]
    [BsonIgnoreIfNull]
    public string? DiffKind { get; set; }
    [BsonElement("periodKey")]
    public string PeriodKey { get; set; } = default!;
    [BsonElement("identitySha256")]
    public string IdentitySha256 { get; set; } = default!;
    [BsonElement("valueType")]
    public string ValueType { get; set; } = default!;
    [BsonElement("atomKinds")]
    public List<string> AtomKinds { get; set; } = [];
    [BsonElement("semanticSha256")]
    public string SemanticSha256 { get; set; } = default!;
    [BsonElement("identitySchemaVersion")]
    public string IdentitySchemaVersion { get; set; } = default!;
    [BsonElement("planEntrySchemaVersion")]
    public string PlanEntrySchemaVersion { get; set; } = default!;
    [BsonElement("jsonPointer")]
    public string JsonPointer { get; set; } = default!;
    [BsonElement("unordered")]
    public bool Unordered { get; set; }
    [BsonElement("expandArray")]
    public bool ExpandArray { get; set; }
    [BsonElement("operations")]
    public List<string> Operations { get; set; } = [];
    [BsonElement("transitionMode")]
    public string TransitionMode { get; set; } = default!;
    [BsonElement("beforeJsonPointer"), BsonIgnoreIfNull]
    public string? BeforeJsonPointer { get; set; }
    [BsonElement("afterJsonPointer"), BsonIgnoreIfNull]
    public string? AfterJsonPointer { get; set; }
    [BsonElement("differenceOperation"), BsonIgnoreIfNull]
    public string? DifferenceOperation { get; set; }
    [BsonElement("transitionKind"), BsonIgnoreIfNull]
    public string? TransitionKind { get; set; }
    [BsonElement("transitionLegs")]
    public List<string> TransitionLegs { get; set; } = [];
    [BsonElement("collectionSemantics"), BsonIgnoreIfNull]
    public string? CollectionSemantics { get; set; }
    [BsonElement("planEntrySha256")]
    public string PlanEntrySha256 { get; set; } = default!;
}

public sealed class StatisticReconciliationObservationActualLayer
{
    [BsonElement("ordinal")]
    public int Ordinal { get; set; }
    [BsonElement("observationCount")]
    public long ObservationCount { get; set; }
    [BsonElement("captureSemanticSha256")]
    public string CaptureSemanticSha256 { get; set; } = default!;
    [BsonElement("typedObservationCount")]
    public int TypedObservationCount { get; set; }
    [BsonElement("typedObservationManifestSha256")]
    public string TypedObservationManifestSha256 { get; set; } = default!;
    [BsonElement("layerSemanticSha256")]
    public string LayerSemanticSha256 { get; set; } = default!;
}
