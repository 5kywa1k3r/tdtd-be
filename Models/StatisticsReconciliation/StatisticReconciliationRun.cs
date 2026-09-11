using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.StatisticsReconciliation;

[BsonIgnoreExtraElements]
[BsonCollection("work_report_statistic_reconciliations")]
public sealed class StatisticReconciliationRun : tdtd_be.Models.BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("receiptId")]
    public string ReceiptId { get; set; } = default!;

    [BsonElement("commandId")]
    public string CommandId { get; set; } = default!;

    [BsonElement("requestHash")]
    public string RequestHash { get; set; } = default!;

    [BsonElement("receiptResponseHash")]
    public string ReceiptResponseHash { get; set; } = default!;

    [BsonElement("immutableIdentityHash")]
    public string ImmutableIdentityHash { get; set; } = default!;

    [BsonElement("immutableHeaderHash")]
    public string ImmutableHeaderHash { get; set; } = default!;

    [BsonElement("actorUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ActorUserId { get; set; } = default!;

    [BsonElement("tenantUnitId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? TenantUnitId { get; set; }

    [BsonElement("permissionCodes")]
    public List<string> PermissionCodes { get; set; } = [];

    [BsonElement("authorizationSnapshotHash")]
    public string AuthorizationSnapshotHash { get; set; } = default!;

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("scopeAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ScopeAssignmentId { get; set; } = default!;

    [BsonElement("p9ResultKind")]
    public string P9ResultKind { get; set; } = default!;

    [BsonElement("p9ResultId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string P9ResultId { get; set; } = default!;

    [BsonElement("p9RunId")]
    [BsonRepresentation(BsonType.ObjectId)]
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
    [BsonRepresentation(BsonType.ObjectId)]
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
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormFamilyId { get; set; }

    [BsonElement("dynamicFormVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DynamicFormVersionId { get; set; } = default!;

    [BsonElement("dynamicFormVersionNo")]
    public int? DynamicFormVersionNo { get; set; }

    [BsonElement("dynamicFormSchemaHash")]
    public string? DynamicFormSchemaHash { get; set; }

    [BsonElement("flowTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? FlowTemplateId { get; set; }

    [BsonElement("flowFamilyRevision")]
    public int? FlowFamilyRevision { get; set; }

    [BsonElement("flowTemplateVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? FlowTemplateVersionId { get; set; }

    [BsonElement("flowPayloadHash")]
    public string? FlowPayloadHash { get; set; }

    [BsonElement("flowInstanceId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? FlowInstanceId { get; set; }

    [BsonElement("flowInstanceRevision")]
    public long? FlowInstanceRevision { get; set; }

    [BsonElement("flowExecutionEpoch")]
    public int? FlowExecutionEpoch { get; set; }

    [BsonElement("flowExecutionEpochId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? FlowExecutionEpochId { get; set; }

    [BsonElement("flowExecutionEpochRevision")]
    public long? FlowExecutionEpochRevision { get; set; }

    [BsonElement("flowStepId")]
    public string? FlowStepId { get; set; }

    [BsonElement("flowBranchId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? FlowBranchId { get; set; }

    [BsonElement("flowStepInstanceId")]
    [BsonRepresentation(BsonType.ObjectId)]
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
    [BsonRepresentation(BsonType.ObjectId)]
    public string? P8ConfigOwnerId { get; set; }

    [BsonElement("p8ConfigId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? P8ConfigId { get; set; }

    [BsonElement("p8ConfigVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
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

    [BsonElement("candidateChainId")]
    public string CandidateChainId { get; set; } = default!;

    [BsonElement("candidatePromptId")]
    public string CandidatePromptId { get; set; } = default!;

    [BsonElement("candidateStage")]
    public int CandidateStage { get; set; }

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

    [BsonElement("conceptKey")]
    public string ConceptKey { get; set; } = default!;

    [BsonElement("grain")]
    public string Grain { get; set; } = default!;

    [BsonElement("timeAxis")]
    public string TimeAxis { get; set; } = default!;

    [BsonElement("pageContractHash")]
    public string PageContractHash { get; set; } = default!;

    [BsonElement("filterHash")]
    public string FilterHash { get; set; } = default!;

    [BsonElement("canonicalFilterJson")]
    public string? CanonicalFilterJson { get; set; }

    [BsonElement("actualCapturePlan")]
    public StatisticReconciliationActualCapturePlan? ActualCapturePlan { get; set; }

    [BsonElement("actualCapturePlanSha256")]
    public string? ActualCapturePlanSha256 { get; set; }

    [BsonElement("actualConfigurationBundleSha256")]
    public string? ActualConfigurationBundleSha256 { get; set; }

    [BsonElement("sourceSetSha256")]
    public string? SourceSetSha256 { get; set; }

    [BsonElement("expectedAlgorithmSha256")]
    public string? ExpectedAlgorithmSha256 { get; set; }

    [BsonElement("expectedAlgorithmRevision")]
    public string? ExpectedAlgorithmRevision { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = StatisticReconciliationRunStatuses.Queued;

    [BsonElement("stateRevision")]
    public long StateRevision { get; set; }

    [BsonElement("stateHash")]
    public string StateHash { get; set; } = default!;

    [BsonElement("retryCount")]
    public int RetryCount { get; set; }

    [BsonElement("maxRetryCount")]
    public int MaxRetryCount { get; set; }

    [BsonElement("nextRetryAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? NextRetryAtUtc { get; set; }

    [BsonElement("leaseOwnerId")]
    public string? LeaseOwnerId { get; set; }

    [BsonElement("claimToken")]
    public string? ClaimToken { get; set; }

    [BsonElement("leaseUntilUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LeaseUntilUtc { get; set; }

    [BsonElement("lastHeartbeatAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastHeartbeatAtUtc { get; set; }

    // Pins the creation-time deadline used by the immutable-header hash.
    // Legacy rows omit this field and fall back to DeadlineAtUtc until the
    // first same-run recheck atomically backfills the original value.
    [BsonElement("initialDeadlineAtUtc")]
    [BsonIgnoreIfNull]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? InitialDeadlineAtUtc { get; set; }

    [BsonElement("deadlineAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime DeadlineAtUtc { get; set; }

    [BsonElement("diagnosticCode")]
    public string? DiagnosticCode { get; set; }

    [BsonElement("pendingGenerationId")]
    public string? PendingGenerationId { get; set; }

    [BsonElement("pendingGenerationHash")]
    public string? PendingGenerationHash { get; set; }

    [BsonElement("pendingGenerationPublishedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PendingGenerationPublishedAtUtc { get; set; }

    [BsonElement("currentGenerationId")]
    public string? CurrentGenerationId { get; set; }

    [BsonElement("currentGenerationHash")]
    public string? CurrentGenerationHash { get; set; }

    [BsonElement("recheck")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationRecheckMarker? Recheck { get; set; }

    [BsonElement("currentGenerationRecheckCaptureBinding")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationRecheckCaptureBinding?
        CurrentGenerationRecheckCaptureBinding { get; set; }

    [BsonElement("currentRecheckFinalizeReceipt")]
    [BsonIgnoreIfNull]
    public StatisticReconciliationRecheckFinalizeReceipt?
        CurrentRecheckFinalizeReceipt { get; set; }

    [BsonElement("reviewDecisionRevision")]
    public long ReviewDecisionRevision { get; set; }

    [BsonElement("generationPublishRevision")]
    public long GenerationPublishRevision { get; set; }

    [BsonElement("operationReceiptHistoryHash")]
    public string OperationReceiptHistoryHash { get; set; } = default!;

    [BsonElement("operationReceipts")]
    public List<StatisticReconciliationOperationReceipt> OperationReceipts { get; set; } = [];

    [BsonElement("recheckBeginReceiptHistoryHash")]
    [BsonIgnoreIfNull]
    public string? RecheckBeginReceiptHistoryHash { get; set; }

    [BsonElement("recheckBeginReceipts")]
    public List<StatisticReconciliationRecheckBeginReceipt> RecheckBeginReceipts
        { get; set; } = [];

    [BsonElement("latestWriterUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string LatestWriterUserId { get; set; } = default!;

    [BsonElement("cancelledAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CancelledAtUtc { get; set; }

    [BsonElement("failedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? FailedAtUtc { get; set; }
}

public sealed class StatisticReconciliationOperationReceipt
{
    [BsonElement("receiptId")]
    public string ReceiptId { get; set; } = default!;

    [BsonElement("reconciliationId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ReconciliationId { get; set; } = default!;

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

    [BsonElement("receiptHash")]
    public string ReceiptHash { get; set; } = default!;
}

public static class StatisticReconciliationRunStatuses
{
    public const string Queued = "QUEUED";
    public const string Running = "RUNNING";
    public const string Matched = "MATCHED";
    public const string Mismatched = "MISMATCHED";
    public const string Stale = "STALE";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Queued, Running, Matched, Mismatched, Stale, Failed, Cancelled],
        StringComparer.Ordinal);
}

public static class StatisticReconciliationP9ResultKinds
{
    public const string Direct = "DIRECT";
}
