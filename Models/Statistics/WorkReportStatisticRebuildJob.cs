using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.Statistics;

[BsonIgnoreExtraElements]
[BsonCollection("work_report_statistic_rebuild_jobs")]
public sealed class WorkReportStatisticRebuildJob : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("dedupeKey")]
    public string DedupeKey { get; set; } = default!;

    // P9 operational receipt and canonical run identity. The job id is the run id;
    // this domain owner deliberately does not create a parallel generic run store.
    [BsonElement("receiptId")]
    public string? ReceiptId { get; set; }

    [BsonElement("commandId")]
    public string? CommandId { get; set; }

    [BsonElement("requestHash")]
    public string? RequestHash { get; set; }

    [BsonElement("receiptResponseHash")]
    public string? ReceiptResponseHash { get; set; }

    [BsonElement("immutableHeaderHash")]
    public string? ImmutableHeaderHash { get; set; }

    [BsonElement("receiptAcceptedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ReceiptAcceptedAtUtc { get; set; }

    [BsonElement("capabilityId")]
    public string? CapabilityId { get; set; }

    [BsonElement("routeId")]
    public string? RouteId { get; set; }

    [BsonElement("runKind")]
    public string? RunKind { get; set; }

    [BsonElement("actorUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ActorUserId { get; set; }

    [BsonElement("tenantUnitId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? TenantUnitId { get; set; }

    [BsonElement("scopeType")]
    public string? ScopeType { get; set; }

    [BsonElement("scopeId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ScopeId { get; set; }

    [BsonElement("sourceReportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? SourceReportId { get; set; }

    [BsonElement("sourcePayloadRevision")]
    public int? SourcePayloadRevision { get; set; }

    [BsonElement("sourcePayloadHash")]
    public string? SourcePayloadHash { get; set; }

    [BsonElement("sourceLifecycleRevision")]
    public int? SourceLifecycleRevision { get; set; }

    [BsonElement("sourceLifecycleEventKey")]
    public string? SourceLifecycleEventKey { get; set; }

    // Legacy lifecycle projections deliberately leave these fields absent. V2
    // Foundation refreshes persist a canonical logical identity so the same
    // lifecycle event can be republished for a different locked configuration
    // without weakening V1 exact-once behavior.
    [BsonElement("directProjectionIdentityVersion")]
    public string? DirectProjectionIdentityVersion { get; set; }

    [BsonElement("directProjectionIdentityKey")]
    public string? DirectProjectionIdentityKey { get; set; }

    [BsonElement("sourceMembershipSignature")]
    public string? SourceMembershipSignature { get; set; }

    [BsonElement("directSourceRevision")]
    public long? DirectSourceRevision { get; set; }

    [BsonElement("publicationScopeKey")]
    public string? PublicationScopeKey { get; set; }

    [BsonElement("directPublicationRevision")]
    public long? DirectPublicationRevision { get; set; }

    [BsonElement("isCurrentPublication")]
    public bool IsCurrentPublication { get; set; }

    [BsonElement("sourceStatus")]
    public string? SourceStatus { get; set; }

    [BsonElement("configId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ConfigId { get; set; }

    [BsonElement("configVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ConfigVersionId { get; set; }

    [BsonElement("configVersionNo")]
    public int? ConfigVersionNo { get; set; }

    [BsonElement("configRevision")]
    public long? ConfigRevision { get; set; }

    [BsonElement("configHash")]
    public string? ConfigHash { get; set; }

    [BsonElement("catalogVersion")]
    public string? CatalogVersion { get; set; }

    [BsonElement("catalogRawSha256")]
    public string? CatalogRawSha256 { get; set; }

    [BsonElement("catalogSemanticSha256")]
    public string? CatalogSemanticSha256 { get; set; }

    [BsonElement("schemaRawSha256")]
    public string? SchemaRawSha256 { get; set; }

    [BsonElement("schemaSemanticSha256")]
    public string? SchemaSemanticSha256 { get; set; }

    [BsonElement("stageLockSha256")]
    public string? StageLockSha256 { get; set; }

    [BsonElement("candidateChainId")]
    public string? CandidateChainId { get; set; }

    [BsonElement("candidatePromptId")]
    public string? CandidatePromptId { get; set; }

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DynamicFormTemplateId { get; set; } = default!;

    [BsonElement("dynamicFormFamilyId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormFamilyId { get; set; }

    [BsonElement("dynamicFormVersionNo")]
    public int? DynamicFormVersionNo { get; set; }

    [BsonElement("dynamicFormSchemaHash")]
    public string? DynamicFormSchemaHash { get; set; }

    [BsonElement("dynamicFormTemplateCode")]
    public string? DynamicFormTemplateCode { get; set; }

    [BsonElement("dynamicFormTemplateName")]
    public string? DynamicFormTemplateName { get; set; }

    [BsonElement("scopeKind")]
    public string ScopeKind { get; set; } = WorkReportStatisticRebuildJobScopeKinds.Template;

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? WorkId { get; set; }

    [BsonElement("workAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? WorkAssignmentId { get; set; }

    [BsonElement("flowInstanceId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? FlowInstanceId { get; set; }

    [BsonElement("flowInstanceRevision")]
    public long? FlowInstanceRevision { get; set; }

    [BsonElement("flowInstanceState")]
    public string? FlowInstanceState { get; set; }

    [BsonElement("flowEffectiveStatus")]
    public string? FlowEffectiveStatus { get; set; }

    [BsonElement("flowTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? FlowTemplateId { get; set; }

    [BsonElement("flowFamilyRevision")]
    public int? FlowFamilyRevision { get; set; }

    [BsonElement("flowTemplateVersionNo")]
    public int? FlowTemplateVersionNo { get; set; }

    [BsonElement("flowTemplateVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? FlowTemplateVersionId { get; set; }

    [BsonElement("flowContributionOriginVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? FlowContributionOriginVersionId { get; set; }

    [BsonElement("flowPayloadHash")]
    public string? FlowPayloadHash { get; set; }

    [BsonElement("flowCatalogVersion")]
    public string? FlowCatalogVersion { get; set; }

    [BsonElement("flowCatalogSemanticHash")]
    public string? FlowCatalogSemanticHash { get; set; }

    [BsonElement("flowExecutionEpoch")]
    public int? FlowExecutionEpoch { get; set; }

    [BsonElement("flowExecutionEpochId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? FlowExecutionEpochId { get; set; }

    [BsonElement("flowExecutionEpochRevision")]
    public long? FlowExecutionEpochRevision { get; set; }

    [BsonElement("flowExecutionEpochState")]
    public string? FlowExecutionEpochState { get; set; }

    [BsonElement("flowBranchId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? FlowBranchId { get; set; }

    [BsonElement("flowStepId")]
    public string? FlowStepId { get; set; }

    [BsonElement("flowAttemptNo")]
    public int? FlowAttemptNo { get; set; }

    [BsonElement("flowStepInstanceId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? FlowStepInstanceId { get; set; }

    [BsonElement("flowStepInstanceRevision")]
    public long? FlowStepInstanceRevision { get; set; }

    [BsonElement("flowStepInstanceState")]
    public string? FlowStepInstanceState { get; set; }

    [BsonElement("flowContributionPolicy")]
    public string? FlowContributionPolicy { get; set; }

    [BsonElement("flowContributionPolicyHash")]
    public string? FlowContributionPolicyHash { get; set; }

    [BsonElement("flowContributionWarning")]
    public string? FlowContributionWarning { get; set; }

    [BsonElement("flowContributionOperationVersion")]
    public string? FlowContributionOperationVersion { get; set; }

    [BsonElement("flowContributionLedgerHash")]
    public string? FlowContributionLedgerHash { get; set; }

    [BsonElement("flowContributionReversalBaselineHash")]
    public string? FlowContributionReversalBaselineHash { get; set; }

    [BsonElement("flowContributionSourceCount")]
    public int FlowContributionSourceCount { get; set; }

    [BsonElement("nonFlowContributionSourceCount")]
    public int NonFlowContributionSourceCount { get; set; }

    [BsonElement("flowContributionTargetCount")]
    public int FlowContributionTargetCount { get; set; }

    [BsonElement("flowContributionSources")]
    public List<WorkReportFlowContributionSourceAudit> FlowContributionSources { get; set; } = [];

    [BsonElement("nonFlowContributionSources")]
    public List<WorkReportNonFlowContributionSourceAudit> NonFlowContributionSources { get; set; } = [];

    [BsonElement("flowContributionTargets")]
    public List<WorkReportFlowContributionTargetAudit> FlowContributionTargets { get; set; } = [];

    [BsonElement("periodKey")]
    public string? PeriodKey { get; set; }

    [BsonElement("periodInstanceKey")]
    public string? PeriodInstanceKey { get; set; }

    [BsonElement("periodKind")]
    public string? PeriodKind { get; set; }

    [BsonElement("periodStartUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PeriodStartUtc { get; set; }

    [BsonElement("periodEndUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PeriodEndUtc { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = WorkReportStatisticRebuildJobStatuses.Pending;

    [BsonElement("stateRevision")]
    public long StateRevision { get; set; }

    [BsonElement("stateHash")]
    public string? StateHash { get; set; }

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;

    [BsonElement("requestedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string RequestedByUserId { get; set; } = default!;

    [BsonElement("priority")]
    public string Priority { get; set; } = WorkReportStatisticRebuildJobPriorities.Normal;

    [BsonElement("totalReportCount")]
    public long TotalReportCount { get; set; }

    [BsonElement("processedReportCount")]
    public long ProcessedReportCount { get; set; }

    [BsonElement("failedReportCount")]
    public long FailedReportCount { get; set; }

    [BsonElement("lastReportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? LastReportId { get; set; }

    [BsonElement("retryCount")]
    public int RetryCount { get; set; }

    [BsonElement("nextRetryAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? NextRetryAtUtc { get; set; }

    [BsonElement("leaseUntilUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LeaseUntilUtc { get; set; }

    [BsonElement("claimToken")]
    public string? ClaimToken { get; set; }

    [BsonElement("leaseOwnerId")]
    public string? LeaseOwnerId { get; set; }

    [BsonElement("claimedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ClaimedAtUtc { get; set; }

    [BsonElement("lastHeartbeatAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastHeartbeatAtUtc { get; set; }

    [BsonElement("deadlineAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? DeadlineAtUtc { get; set; }

    [BsonElement("initialDeadlineAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? InitialDeadlineAtUtc { get; set; }

    [BsonElement("lastRunAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastRunAtUtc { get; set; }

    [BsonElement("completedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CompletedAtUtc { get; set; }

    [BsonElement("generationId")]
    public string? GenerationId { get; set; }

    [BsonElement("generationHash")]
    public string? GenerationHash { get; set; }

    [BsonElement("directStoreDigests")]
    public List<WorkReportDirectStoreDigest> DirectStoreDigests { get; set; } = [];

    [BsonElement("publishedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PublishedAtUtc { get; set; }

    [BsonElement("computedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ComputedAtUtc { get; set; }

    [BsonElement("freshnessState")]
    public string? FreshnessState { get; set; }

    [BsonElement("staleReason")]
    public string? StaleReason { get; set; }

    [BsonElement("diagnosticCode")]
    public string? DiagnosticCode { get; set; }

    [BsonElement("lastErrorType")]
    public string? LastErrorType { get; set; }

    [BsonElement("lastError")]
    public string? LastError { get; set; }

    [BsonElement("lastErrorAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastErrorAtUtc { get; set; }

    [BsonElement("lastResetCommandId")]
    public string? LastResetCommandId { get; set; }

    [BsonElement("lastResetRequestHash")]
    public string? LastResetRequestHash { get; set; }

    [BsonElement("lastResetAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastResetAtUtc { get; set; }

    [BsonElement("lastResetByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? LastResetByUserId { get; set; }

    [BsonElement("resetReceiptHistoryHash")]
    public string? ResetReceiptHistoryHash { get; set; }

    [BsonElement("resetReceipts")]
    public List<WorkReportStatisticRebuildJobResetReceipt> ResetReceipts { get; set; } = [];

    [BsonElement("operationReceiptHistoryHash")]
    public string? OperationReceiptHistoryHash { get; set; }

    [BsonElement("operationReceipts")]
    public List<WorkReportStatisticRebuildJobOperationReceipt> OperationReceipts { get; set; } = [];

    [BsonElement("cancelledAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CancelledAtUtc { get; set; }

    [BsonElement("cancelledByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? CancelledByUserId { get; set; }

    [BsonElement("reversalAudit")]
    public WorkReportStatisticReversalAudit? ReversalAudit { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class WorkReportStatisticReversalAudit
{
    [BsonElement("operation")]
    public string Operation { get; set; } = default!;

    [BsonElement("eventKey")]
    public string EventKey { get; set; } = default!;

    [BsonElement("sourceReportId"), BsonRepresentation(BsonType.ObjectId)]
    public string SourceReportId { get; set; } = default!;

    [BsonElement("sourceLifecycleRevision")]
    public int SourceLifecycleRevision { get; set; }

    [BsonElement("receiptId")]
    public string ReceiptId { get; set; } = default!;

    [BsonElement("priorRunId"), BsonRepresentation(BsonType.ObjectId)]
    public string PriorRunId { get; set; } = default!;

    [BsonElement("priorGenerationId")]
    public string PriorGenerationId { get; set; } = default!;

    [BsonElement("priorGenerationHash")]
    public string PriorGenerationHash { get; set; } = default!;

    [BsonElement("priorLedgerHash")]
    public string? PriorLedgerHash { get; set; }

    [BsonElement("priorReversalBaselineHash")]
    public string? PriorReversalBaselineHash { get; set; }

    [BsonElement("priorSourceCount")]
    public int PriorSourceCount { get; set; }

    [BsonElement("priorTargetCount")]
    public int PriorTargetCount { get; set; }

    [BsonElement("priorSourceAuditHashes")]
    public List<string> PriorSourceAuditHashes { get; set; } = [];

    [BsonElement("priorTargetLedgerHashes")]
    public List<string> PriorTargetLedgerHashes { get; set; } = [];

    [BsonElement("auditHash")]
    public string AuditHash { get; set; } = default!;
}

[BsonIgnoreExtraElements]
public sealed class WorkReportFlowContributionSourceAudit
{
    [BsonElement("sourceAuditId")]
    public string SourceAuditId { get; set; } = default!;

    [BsonElement("sourceReportId"), BsonRepresentation(BsonType.ObjectId)]
    public string SourceReportId { get; set; } = default!;

    [BsonElement("sourcePayloadRevision")]
    public int SourcePayloadRevision { get; set; }

    [BsonElement("sourcePayloadHash")]
    public string SourcePayloadHash { get; set; } = default!;

    [BsonElement("sourceLifecycleRevision")]
    public int SourceLifecycleRevision { get; set; }

    [BsonElement("approvalCommandId")]
    public string ApprovalCommandId { get; set; } = default!;

    [BsonElement("approvalEventKey")]
    public string ApprovalEventKey { get; set; } = default!;

    [BsonElement("mappingReceiptId"), BsonRepresentation(BsonType.ObjectId)]
    public string MappingReceiptId { get; set; } = default!;

    [BsonElement("mappingProvenanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string MappingProvenanceId { get; set; } = default!;

    [BsonElement("mappingProvenanceHash")]
    public string MappingProvenanceHash { get; set; } = default!;

    [BsonElement("mappingCommandId")]
    public string MappingCommandId { get; set; } = default!;

    [BsonElement("mappingRequestHash")]
    public string MappingRequestHash { get; set; } = default!;

    [BsonElement("previewTokenId")]
    public string PreviewTokenId { get; set; } = default!;

    [BsonElement("previewTokenHash")]
    public string PreviewTokenHash { get; set; } = default!;

    [BsonElement("mappingSourceSignatureVersion")]
    public string MappingSourceSignatureVersion { get; set; } = default!;

    [BsonElement("mappingSourceSignature")]
    public string MappingSourceSignature { get; set; } = default!;

    [BsonElement("mappingSourcePinsHash")]
    public string MappingSourcePinsHash { get; set; } = default!;

    [BsonElement("mappingResultSemanticHash")]
    public string MappingResultSemanticHash { get; set; } = default!;

    [BsonElement("mappingResultPayloadRevision")]
    public int MappingResultPayloadRevision { get; set; }

    [BsonElement("mappingResultPayloadHash")]
    public string MappingResultPayloadHash { get; set; } = default!;

    [BsonElement("mappingRuleSetHash")]
    public string MappingRuleSetHash { get; set; } = default!;

    [BsonElement("mappingEvaluatorVersion")]
    public string MappingEvaluatorVersion { get; set; } = default!;

    [BsonElement("mappingFunctionRegistryVersion")]
    public string MappingFunctionRegistryVersion { get; set; } = default!;

    [BsonElement("mappingFunctionRegistryHash")]
    public string MappingFunctionRegistryHash { get; set; } = default!;

    [BsonElement("mappingFlowVersionId"), BsonRepresentation(BsonType.ObjectId)]
    public string MappingFlowVersionId { get; set; } = default!;

    [BsonElement("mappingFlowVersionNo")]
    public int MappingFlowVersionNo { get; set; }

    [BsonElement("mappingFlowPayloadHash")]
    public string MappingFlowPayloadHash { get; set; } = default!;

    [BsonElement("flowTemplateVersionId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowTemplateVersionId { get; set; } = default!;

    [BsonElement("flowPayloadHash")]
    public string FlowPayloadHash { get; set; } = default!;

    [BsonElement("flowInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowInstanceId { get; set; } = default!;

    [BsonElement("flowExecutionEpoch")]
    public int FlowExecutionEpoch { get; set; }

    [BsonElement("flowStepInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowStepInstanceId { get; set; } = default!;

    [BsonElement("flowBranchId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowBranchId { get; set; } = default!;

    [BsonElement("flowStepId")]
    public string FlowStepId { get; set; } = default!;

    [BsonElement("flowAttemptNo")]
    public int FlowAttemptNo { get; set; }

    [BsonElement("workId"), BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("workAssignmentId"), BsonRepresentation(BsonType.ObjectId)]
    public string WorkAssignmentId { get; set; } = default!;

    [BsonElement("periodInstanceKey")]
    public string PeriodInstanceKey { get; set; } = default!;

    [BsonElement("configVersionId"), BsonRepresentation(BsonType.ObjectId)]
    public string ConfigVersionId { get; set; } = default!;

    [BsonElement("configHash")]
    public string ConfigHash { get; set; } = default!;

    [BsonElement("membershipSignature")]
    public string MembershipSignature { get; set; } = default!;

    [BsonElement("contributionPolicy")]
    public string ContributionPolicy { get; set; } = default!;

    [BsonElement("contributionPolicyHash")]
    public string ContributionPolicyHash { get; set; } = default!;

    [BsonElement("reversalIdentity")]
    public string ReversalIdentity { get; set; } = default!;

    [BsonElement("sourceAuditHash")]
    public string SourceAuditHash { get; set; } = default!;
}

[BsonIgnoreExtraElements]
public sealed class WorkReportNonFlowContributionSourceAudit
{
    [BsonElement("sourceAuditId")]
    public string SourceAuditId { get; set; } = default!;

    [BsonElement("sourceReportId"), BsonRepresentation(BsonType.ObjectId)]
    public string SourceReportId { get; set; } = default!;

    [BsonElement("sourcePayloadRevision")]
    public int SourcePayloadRevision { get; set; }

    [BsonElement("sourcePayloadHash")]
    public string SourcePayloadHash { get; set; } = default!;

    [BsonElement("sourceLifecycleRevision")]
    public int SourceLifecycleRevision { get; set; }

    [BsonElement("approvalCommandId")]
    public string ApprovalCommandId { get; set; } = default!;

    [BsonElement("approvalEventKey")]
    public string ApprovalEventKey { get; set; } = default!;

    [BsonElement("workId"), BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("workAssignmentId"), BsonRepresentation(BsonType.ObjectId)]
    public string WorkAssignmentId { get; set; } = default!;

    [BsonElement("periodInstanceKey")]
    public string PeriodInstanceKey { get; set; } = default!;

    [BsonElement("configVersionId"), BsonRepresentation(BsonType.ObjectId)]
    public string ConfigVersionId { get; set; } = default!;

    [BsonElement("configHash")]
    public string ConfigHash { get; set; } = default!;

    [BsonElement("membershipSignature")]
    public string MembershipSignature { get; set; } = default!;

    [BsonElement("policyVersion")]
    public string PolicyVersion { get; set; } = default!;

    [BsonElement("policyOwnerId")]
    public string PolicyOwnerId { get; set; } = default!;

    [BsonElement("policyRevision")]
    public long PolicyRevision { get; set; }

    [BsonElement("policy")]
    public string Policy { get; set; } = default!;

    [BsonElement("policySha256")]
    public string PolicySha256 { get; set; } = default!;

    [BsonElement("contributionOperation")]
    public string ContributionOperation { get; set; } = default!;

    [BsonElement("reversalIdentity")]
    public string ReversalIdentity { get; set; } = default!;

    [BsonElement("sourceAuditHash")]
    public string SourceAuditHash { get; set; } = default!;
}

[BsonIgnoreExtraElements]
public sealed class WorkReportFlowContributionTargetAudit
{
    [BsonElement("contributionId")]
    public string ContributionId { get; set; } = default!;

    [BsonElement("sourceAuditId")]
    public string SourceAuditId { get; set; } = default!;

    [BsonElement("sourceReportId"), BsonRepresentation(BsonType.ObjectId)]
    public string SourceReportId { get; set; } = default!;

    [BsonElement("targetStore")]
    public string TargetStore { get; set; } = default!;

    [BsonElement("targetStatisticId"), BsonRepresentation(BsonType.ObjectId)]
    public string TargetStatisticId { get; set; } = default!;

    [BsonElement("targetIdentityHash")]
    public string TargetIdentityHash { get; set; } = default!;

    [BsonElement("operation")]
    public string Operation { get; set; } = default!;

    [BsonElement("operationVersion")]
    public string OperationVersion { get; set; } = default!;

    [BsonElement("runId"), BsonRepresentation(BsonType.ObjectId)]
    public string RunId { get; set; } = default!;

    [BsonElement("receiptId")]
    public string ReceiptId { get; set; } = default!;

    [BsonElement("generationId")]
    public string GenerationId { get; set; } = default!;

    [BsonElement("reversalIdentity")]
    public string ReversalIdentity { get; set; } = default!;

    [BsonElement("state")]
    public string State { get; set; } = WorkReportFlowContributionStates.Applied;

    [BsonElement("ledgerEntryHash")]
    public string LedgerEntryHash { get; set; } = default!;
}

public static class WorkReportFlowContributionStates
{
    public const string Applied = "APPLIED";
}

public sealed class WorkReportStatisticRebuildJobOperationReceipt
{
    [BsonElement("receiptId")]
    public string ReceiptId { get; set; } = default!;

    [BsonElement("jobId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string JobId { get; set; } = default!;

    [BsonElement("actorUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ActorUserId { get; set; } = default!;

    [BsonElement("operation")]
    public string Operation { get; set; } = default!;

    [BsonElement("commandId")]
    public string CommandId { get; set; } = default!;

    [BsonElement("requestHash")]
    public string RequestHash { get; set; } = default!;

    [BsonElement("expectedStateRevision")]
    public long ExpectedStateRevision { get; set; }

    [BsonElement("expectedStateHash")]
    public string ExpectedStateHash { get; set; } = default!;

    [BsonElement("acceptedStateRevision")]
    public long AcceptedStateRevision { get; set; }

    [BsonElement("acceptedStateHash")]
    public string AcceptedStateHash { get; set; } = default!;

    [BsonElement("acceptedStatus")]
    public string AcceptedStatus { get; set; } = default!;

    [BsonElement("acceptedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime AcceptedAtUtc { get; set; }

    [BsonElement("acceptedNextRetryAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? AcceptedNextRetryAtUtc { get; set; }

    [BsonElement("diagnosticCode")]
    public string? DiagnosticCode { get; set; }

    [BsonElement("batchHash")]
    public string? BatchHash { get; set; }

    [BsonElement("batchSize")]
    public int? BatchSize { get; set; }

    [BsonElement("batchJobIds")]
    public List<string>? BatchJobIds { get; set; }

    [BsonElement("receiptHash")]
    public string ReceiptHash { get; set; } = default!;
}

public static class WorkReportStatisticRebuildJobOperations
{
    public const string Retry = "RETRY";
    public const string Cancel = "CANCEL";
    public const string Reset = "RESET";
    public const string Requeue = "REQUEUE";
    public const string Cleanup = "CLEANUP";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Retry, Cancel, Reset, Requeue, Cleanup],
        StringComparer.Ordinal);
}

public sealed class WorkReportStatisticRebuildJobResetReceipt
{
    [BsonElement("jobId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string JobId { get; set; } = default!;

    [BsonElement("actorUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ActorUserId { get; set; } = default!;

    [BsonElement("commandId")]
    public string CommandId { get; set; } = default!;

    [BsonElement("requestHash")]
    public string RequestHash { get; set; } = default!;

    [BsonElement("expectedStateRevision")]
    public long ExpectedStateRevision { get; set; }

    [BsonElement("expectedStateHash")]
    public string ExpectedStateHash { get; set; } = default!;

    [BsonElement("acceptedStateRevision")]
    public long AcceptedStateRevision { get; set; }

    [BsonElement("acceptedStateHash")]
    public string AcceptedStateHash { get; set; } = default!;

    [BsonElement("acceptedStatus")]
    public string AcceptedStatus { get; set; } = default!;

    [BsonElement("acceptedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime AcceptedAtUtc { get; set; }

    [BsonElement("acceptedDeadlineAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime AcceptedDeadlineAtUtc { get; set; }

    [BsonElement("receiptHash")]
    public string ReceiptHash { get; set; } = default!;
}

public static class WorkReportStatisticRebuildJobStatuses
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string RetryWaiting = "RETRY_WAITING";
    public const string Completed = "COMPLETED";
    public const string DeadLetter = "DEAD_LETTER";
}

public static class WorkReportStatisticRebuildJobPriorities
{
    public const string Normal = "NORMAL";
    public const string High = "HIGH";
}

public static class WorkReportStatisticRebuildJobScopeKinds
{
    public const string Template = "TEMPLATE";
    public const string Bounded = "BOUNDED";
}

public static class WorkReportStatisticRebuildJobRunKinds
{
    public const string Foundation = "FOUNDATION_ONLY";
    public const string LifecycleDirectProjection = "LIFECYCLE_DIRECT_PROJECTION";
}

public static class WorkReportDirectProjectionIdentityVersions
{
    public const string FoundationRefreshV2 = "FOUNDATION_REFRESH_V2";
}
public static class WorkReportStatisticRebuildJobFreshnessStates
{
    public const string Pending = "PENDING";
    public const string Fresh = "FRESH";
    public const string Stale = "STALE";
}

[BsonIgnoreExtraElements]
public sealed class WorkReportDirectStoreDigest
{
    [BsonElement("store")]
    public string Store { get; set; } = default!;

    [BsonElement("rowCount")]
    public long RowCount { get; set; }

    [BsonElement("sha256")]
    public string Sha256 { get; set; } = default!;
}
