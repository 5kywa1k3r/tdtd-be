using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

/// <summary>
/// Exact, immutable P6/P7 execution and definition authority used by mapping apply.
/// Writers must replace the containing document rather than refresh these pins from
/// current templates or runtime state.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class DynamicFlowMappingRuntimePin
{
    [BsonElement("flowFamilyId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowFamilyId { get; set; } = default!;

    [BsonElement("flowVersionId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowVersionId { get; set; } = default!;

    [BsonElement("flowVersionNo")]
    public int FlowVersionNo { get; set; }

    [BsonElement("flowPayloadHash")]
    public string FlowPayloadHash { get; set; } = string.Empty;

    [BsonElement("catalogVersion")]
    public string CatalogVersion { get; set; } = string.Empty;

    [BsonElement("catalogSemanticHash")]
    public string CatalogSemanticHash { get; set; } = string.Empty;

    [BsonElement("mappingRuleSetHash")]
    public string MappingRuleSetHash { get; set; } = string.Empty;

    [BsonElement("evaluatorVersion")]
    public string EvaluatorVersion { get; set; } = string.Empty;

    [BsonElement("functionRegistryVersion")]
    public string FunctionRegistryVersion { get; set; } = string.Empty;

    [BsonElement("functionRegistryHash")]
    public string FunctionRegistryHash { get; set; } = string.Empty;

    [BsonElement("flowInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowInstanceId { get; set; } = default!;

    [BsonElement("executionEpoch")]
    public int ExecutionEpoch { get; set; }

    [BsonElement("stepInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string StepInstanceId { get; set; } = default!;

    [BsonElement("stepId")]
    public string StepId { get; set; } = string.Empty;

    [BsonElement("branchId"), BsonRepresentation(BsonType.ObjectId)]
    public string BranchId { get; set; } = default!;

    [BsonElement("attemptNo")]
    public int AttemptNo { get; set; }

    [BsonElement("formFamilyId"), BsonRepresentation(BsonType.ObjectId)]
    public string FormFamilyId { get; set; } = default!;

    [BsonElement("formVersionId"), BsonRepresentation(BsonType.ObjectId)]
    public string FormVersionId { get; set; } = default!;

    [BsonElement("formVersionNo")]
    public int FormVersionNo { get; set; }

    [BsonElement("formSchemaHash")]
    public string FormSchemaHash { get; set; } = string.Empty;

    [BsonElement("formSnapshotHash")]
    public string FormSnapshotHash { get; set; } = string.Empty;
}

/// <summary>
/// Hash-only source authority retained for replay, audit, invalidation and reconcile.
/// Raw hidden values must not be persisted in this pin.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class DynamicFlowMappingSourcePin
{
    [BsonElement("sourceReportId"), BsonRepresentation(BsonType.ObjectId)]
    public string SourceReportId { get; set; } = default!;

    [BsonElement("sourceAssignmentId"), BsonRepresentation(BsonType.ObjectId)]
    public string SourceAssignmentId { get; set; } = default!;

    [BsonElement("sourceFlowInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string SourceFlowInstanceId { get; set; } = default!;

    [BsonElement("sourceExecutionEpoch")]
    public int SourceExecutionEpoch { get; set; }

    [BsonElement("sourceStepInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string SourceStepInstanceId { get; set; } = default!;

    [BsonElement("sourceStepId")]
    public string SourceStepId { get; set; } = string.Empty;

    [BsonElement("sourceBranchId"), BsonRepresentation(BsonType.ObjectId)]
    public string SourceBranchId { get; set; } = default!;

    [BsonElement("sourceAttemptNo")]
    public int SourceAttemptNo { get; set; }

    [BsonElement("sourceFormFamilyId"), BsonRepresentation(BsonType.ObjectId)]
    public string SourceFormFamilyId { get; set; } = default!;

    [BsonElement("sourceFormVersionId"), BsonRepresentation(BsonType.ObjectId)]
    public string SourceFormVersionId { get; set; } = default!;

    [BsonElement("sourceFormVersionNo")]
    public int SourceFormVersionNo { get; set; }

    [BsonElement("sourceFormSchemaHash")]
    public string SourceFormSchemaHash { get; set; } = string.Empty;

    [BsonElement("sourcePayloadRevision")]
    public int SourcePayloadRevision { get; set; }

    [BsonElement("sourcePayloadHash")]
    public string SourcePayloadHash { get; set; } = string.Empty;

    [BsonElement("sourceLifecycleRevision")]
    public int SourceLifecycleRevision { get; set; }

    [BsonElement("sourceLifecycleStatus")]
    public string SourceLifecycleStatus { get; set; } = string.Empty;

    [BsonElement("sourcePeriodInstanceKey")]
    public string SourcePeriodInstanceKey { get; set; } = string.Empty;

    [BsonElement("sourceFactHash")]
    public string SourceFactHash { get; set; } = string.Empty;
}

/// <summary>
/// Canonical idempotency receipt for one accepted mapping command. Identity is
/// enforced by the unique (targetReportId, commandId) index.
/// </summary>
[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_mapping_apply_receipts")]
public sealed class DynamicFlowMappingApplyReceipt
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("workId"), BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("targetAssignmentId"), BsonRepresentation(BsonType.ObjectId)]
    public string TargetAssignmentId { get; set; } = default!;

    [BsonElement("targetReportId"), BsonRepresentation(BsonType.ObjectId)]
    public string TargetReportId { get; set; } = default!;

    [BsonElement("commandId")]
    public string CommandId { get; set; } = string.Empty;

    [BsonElement("requestHash")]
    public string RequestHash { get; set; } = string.Empty;

    [BsonElement("previewTokenId")]
    public string PreviewTokenId { get; set; } = string.Empty;

    [BsonElement("previewTokenHash")]
    public string PreviewTokenHash { get; set; } = string.Empty;

    [BsonElement("previewIssuedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime PreviewIssuedAtUtc { get; set; }

    [BsonElement("previewExpiresAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime PreviewExpiresAtUtc { get; set; }

    [BsonElement("sourceSignatureVersion")]
    public string SourceSignatureVersion { get; set; } = string.Empty;

    [BsonElement("sourceSignature")]
    public string SourceSignature { get; set; } = string.Empty;

    [BsonElement("resultSemanticHash")]
    public string ResultSemanticHash { get; set; } = string.Empty;

    [BsonElement("authorizationSnapshotHash")]
    public string AuthorizationSnapshotHash { get; set; } = string.Empty;

    [BsonElement("expectedTargetPayloadRevision")]
    public int ExpectedTargetPayloadRevision { get; set; }

    [BsonElement("expectedTargetPayloadHash")]
    public string ExpectedTargetPayloadHash { get; set; } = string.Empty;

    [BsonElement("expectedTargetLifecycleRevision")]
    public int ExpectedTargetLifecycleRevision { get; set; }

    [BsonElement("expectedTargetStatus")]
    public string ExpectedTargetStatus { get; set; } = string.Empty;

    [BsonElement("expectedTargetIsActive")]
    public bool ExpectedTargetIsActive { get; set; }

    [BsonElement("runtimePin")]
    public DynamicFlowMappingRuntimePin RuntimePin { get; set; } = new();

    [BsonElement("sourcePins")]
    public List<DynamicFlowMappingSourcePin> SourcePins { get; set; } = new();

    [BsonElement("state")]
    public string State { get; set; } = DynamicFlowMappingApplyStates.Committed;

    [BsonElement("resultSnapshot")]
    public BsonDocument ResultSnapshot { get; set; } = new();

    [BsonElement("resultSnapshotHash")]
    public string ResultSnapshotHash { get; set; } = string.Empty;

    [BsonElement("writeSetHash")]
    public string WriteSetHash { get; set; } = string.Empty;

    [BsonElement("resultPayloadRevision")]
    public int ResultPayloadRevision { get; set; }

    [BsonElement("resultPayloadHash")]
    public string ResultPayloadHash { get; set; } = string.Empty;

    [BsonElement("resultLifecycleRevision")]
    public int ResultLifecycleRevision { get; set; }

    [BsonElement("provenanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string ProvenanceId { get; set; } = default!;

    [BsonElement("provenanceHash")]
    public string ProvenanceHash { get; set; } = string.Empty;

    [BsonElement("eventId"), BsonRepresentation(BsonType.ObjectId)]
    public string EventId { get; set; } = default!;

    [BsonElement("outboxIntentId"), BsonRepresentation(BsonType.ObjectId)]
    public string OutboxIntentId { get; set; } = default!;

    [BsonElement("actorUserId"), BsonRepresentation(BsonType.ObjectId)]
    public string ActorUserId { get; set; } = default!;

    [BsonElement("errorCode")]
    public string? ErrorCode { get; set; }

    [BsonElement("createdAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; }

    [BsonElement("committedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CommittedAtUtc { get; set; }

    [BsonElement("updatedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAtUtc { get; set; }

    [BsonElement("partialAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PartialAtUtc { get; set; }

    [BsonElement("retryingAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? RetryingAtUtc { get; set; }

    [BsonElement("reconciledAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ReconciledAtUtc { get; set; }
}

/// <summary>
/// Mapping-owned result and provenance. The snapshot/hash fields are immutable;
/// state/successor fields are reserved for P7 invalidation without deleting history.
/// </summary>
[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_mapping_provenance")]
public sealed class DynamicFlowMappingProvenanceRecord
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("receiptId"), BsonRepresentation(BsonType.ObjectId)]
    public string ReceiptId { get; set; } = default!;

    [BsonElement("workId"), BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("targetAssignmentId"), BsonRepresentation(BsonType.ObjectId)]
    public string TargetAssignmentId { get; set; } = default!;

    [BsonElement("targetReportId"), BsonRepresentation(BsonType.ObjectId)]
    public string TargetReportId { get; set; } = default!;

    [BsonElement("commandId")]
    public string CommandId { get; set; } = string.Empty;

    [BsonElement("targetPayloadRevision")]
    public int TargetPayloadRevision { get; set; }

    [BsonElement("targetPayloadHash")]
    public string TargetPayloadHash { get; set; } = string.Empty;

    [BsonElement("targetLifecycleRevision")]
    public int TargetLifecycleRevision { get; set; }

    [BsonElement("sourceSignatureVersion")]
    public string SourceSignatureVersion { get; set; } = string.Empty;

    [BsonElement("sourceSignature")]
    public string SourceSignature { get; set; } = string.Empty;

    [BsonElement("resultSemanticHash")]
    public string ResultSemanticHash { get; set; } = string.Empty;

    [BsonElement("mappingRuleSetHash")]
    public string MappingRuleSetHash { get; set; } = string.Empty;

    [BsonElement("runtimePin")]
    public DynamicFlowMappingRuntimePin RuntimePin { get; set; } = new();

    [BsonElement("sourcePins")]
    public List<DynamicFlowMappingSourcePin> SourcePins { get; set; } = new();

    [BsonElement("resultSnapshot")]
    public BsonDocument ResultSnapshot { get; set; } = new();

    [BsonElement("resultSnapshotHash")]
    public string ResultSnapshotHash { get; set; } = string.Empty;

    [BsonElement("provenanceSnapshot")]
    public BsonDocument ProvenanceSnapshot { get; set; } = new();

    [BsonElement("provenanceHash")]
    public string ProvenanceHash { get; set; } = string.Empty;

    [BsonElement("ownedTargetRefs")]
    public List<string> OwnedTargetRefs { get; set; } = new();

    [BsonElement("state")]
    public string State { get; set; } = DynamicFlowMappingProvenanceStates.Current;

    [BsonElement("supersedesProvenanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string? SupersedesProvenanceId { get; set; }

    [BsonElement("supersededByProvenanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string? SupersededByProvenanceId { get; set; }

    [BsonElement("invalidatedByEventId"), BsonRepresentation(BsonType.ObjectId)]
    public string? InvalidatedByEventId { get; set; }

    [BsonElement("invalidationReason")]
    public string? InvalidationReason { get; set; }

    [BsonElement("createdByUserId"), BsonRepresentation(BsonType.ObjectId)]
    public string CreatedByUserId { get; set; } = default!;

    [BsonElement("createdAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; }

    [BsonElement("invalidatedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? InvalidatedAtUtc { get; set; }
}

/// <summary>
/// Immutable mapping business event. EventKey is a deterministic hash calculated
/// before the transaction and is the exact-once projector identity.
/// </summary>
[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_mapping_events")]
public sealed class DynamicFlowMappingEvent
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("eventKey")]
    public string EventKey { get; set; } = string.Empty;

    [BsonElement("eventType")]
    public string EventType { get; set; } = string.Empty;

    [BsonElement("receiptId"), BsonRepresentation(BsonType.ObjectId)]
    public string ReceiptId { get; set; } = default!;

    [BsonElement("provenanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string ProvenanceId { get; set; } = default!;

    [BsonElement("targetReportId"), BsonRepresentation(BsonType.ObjectId)]
    public string TargetReportId { get; set; } = default!;

    [BsonElement("targetAssignmentId"), BsonRepresentation(BsonType.ObjectId)]
    public string TargetAssignmentId { get; set; } = default!;

    [BsonElement("commandId")]
    public string CommandId { get; set; } = string.Empty;

    [BsonElement("correlationId")]
    public string? CorrelationId { get; set; }

    [BsonElement("runtimePin")]
    public DynamicFlowMappingRuntimePin RuntimePin { get; set; } = new();

    [BsonElement("targetPayloadRevision")]
    public int TargetPayloadRevision { get; set; }

    [BsonElement("targetPayloadHash")]
    public string TargetPayloadHash { get; set; } = string.Empty;

    [BsonElement("targetLifecycleRevision")]
    public int TargetLifecycleRevision { get; set; }

    [BsonElement("sourceSignatureVersion")]
    public string SourceSignatureVersion { get; set; } = string.Empty;

    [BsonElement("sourceSignature")]
    public string SourceSignature { get; set; } = string.Empty;

    [BsonElement("resultSemanticHash")]
    public string ResultSemanticHash { get; set; } = string.Empty;

    [BsonElement("provenanceHash")]
    public string ProvenanceHash { get; set; } = string.Empty;

    [BsonElement("payload")]
    public BsonDocument Payload { get; set; } = new();

    [BsonElement("payloadHash")]
    public string PayloadHash { get; set; } = string.Empty;

    [BsonElement("actorUserId"), BsonRepresentation(BsonType.ObjectId)]
    public string ActorUserId { get; set; } = default!;

    [BsonElement("occurredAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime OccurredAtUtc { get; set; }
}

/// <summary>
/// Immutable committed input for all post-transaction projectors. Reconcile must
/// consume this snapshot and its hashes; it must never reload current rules/source.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class DynamicFlowMappingReconcileIntent
{
    [BsonElement("schemaVersion")]
    public string SchemaVersion { get; set; } = DynamicFlowMappingPersistenceSchema.ReconcileIntentVersion;

    [BsonElement("actorUserId"), BsonRepresentation(BsonType.ObjectId)]
    [BsonIgnoreIfDefault]
    public string ActorUserId { get; set; } = string.Empty;

    [BsonElement("receiptId"), BsonRepresentation(BsonType.ObjectId)]
    public string ReceiptId { get; set; } = default!;

    [BsonElement("provenanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string ProvenanceId { get; set; } = default!;

    [BsonElement("eventId"), BsonRepresentation(BsonType.ObjectId)]
    public string EventId { get; set; } = default!;

    [BsonElement("targetReportId"), BsonRepresentation(BsonType.ObjectId)]
    public string TargetReportId { get; set; } = default!;

    [BsonElement("targetAssignmentId"), BsonRepresentation(BsonType.ObjectId)]
    public string TargetAssignmentId { get; set; } = default!;

    [BsonElement("commandId")]
    public string CommandId { get; set; } = string.Empty;

    [BsonElement("targetPayloadRevision")]
    public int TargetPayloadRevision { get; set; }

    [BsonElement("targetPayloadHash")]
    public string TargetPayloadHash { get; set; } = string.Empty;

    [BsonElement("targetLifecycleRevision")]
    public int TargetLifecycleRevision { get; set; }

    [BsonElement("sourceSignatureVersion")]
    public string SourceSignatureVersion { get; set; } = string.Empty;

    [BsonElement("sourceSignature")]
    public string SourceSignature { get; set; } = string.Empty;

    [BsonElement("resultSemanticHash")]
    public string ResultSemanticHash { get; set; } = string.Empty;

    [BsonElement("mappingRuleSetHash")]
    public string MappingRuleSetHash { get; set; } = string.Empty;

    [BsonElement("provenanceHash")]
    public string ProvenanceHash { get; set; } = string.Empty;

    [BsonElement("runtimePin")]
    public DynamicFlowMappingRuntimePin RuntimePin { get; set; } = new();

    [BsonElement("invalidatedProvenanceIds")]
    [BsonIgnoreIfDefault]
    public List<string>? InvalidatedProvenanceIds { get; set; }

    [BsonElement("invalidatedByEventId")]
    [BsonRepresentation(BsonType.ObjectId)]
    [BsonIgnoreIfNull]
    public string? InvalidatedByEventId { get; set; }

    [BsonElement("invalidationReason")]
    [BsonIgnoreIfNull]
    public string? InvalidationReason { get; set; }

    [BsonElement("sourceReportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    [BsonIgnoreIfNull]
    public string? SourceReportId { get; set; }

    [BsonElement("rebuildOnly")]
    [BsonIgnoreIfDefault]
    public bool RebuildOnly { get; set; }

    [BsonElement("p8ExecutionEnabled")]
    [BsonIgnoreIfDefault]
    public bool P8ExecutionEnabled { get; set; }

    [BsonElement("p9ExecutionEnabled")]
    [BsonIgnoreIfDefault]
    public bool P9ExecutionEnabled { get; set; }

    [BsonElement("projectionSnapshot")]
    public BsonDocument ProjectionSnapshot { get; set; } = new();

    [BsonElement("projectionSnapshotHash")]
    public string ProjectionSnapshotHash { get; set; } = string.Empty;

    [BsonElement("auditSnapshot")]
    public BsonDocument AuditSnapshot { get; set; } = new();

    [BsonElement("auditSnapshotHash")]
    public string AuditSnapshotHash { get; set; } = string.Empty;

    [BsonElement("projectionBusinessKeys")]
    public List<string> ProjectionBusinessKeys { get; set; } = new();

    [BsonElement("committedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CommittedAtUtc { get; set; }
}

/// <summary>
/// Durable progress for one deterministic post-commit projector. Identity fields
/// are derived from the immutable intent and never change after outbox creation.
/// Mutable state is updated only through the outbox lease/repair-epoch fence.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class DynamicFlowMappingProjectorCheckpoint
{
    [BsonElement("projector")]
    public string Projector { get; set; } = string.Empty;

    [BsonElement("businessKey")]
    public string BusinessKey { get; set; } = string.Empty;

    [BsonElement("idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [BsonElement("state")]
    public string State { get; set; } =
        DynamicFlowMappingProjectorCheckpointStates.Pending;

    [BsonElement("attemptCount")]
    public int AttemptCount { get; set; }

    [BsonElement("activeRepairEpoch")]
    [BsonIgnoreIfNull]
    public long? ActiveRepairEpoch { get; set; }

    [BsonElement("startedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? StartedAtUtc { get; set; }

    [BsonElement("completedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? CompletedAtUtc { get; set; }

    [BsonElement("completedRepairEpoch")]
    [BsonIgnoreIfNull]
    public long? CompletedRepairEpoch { get; set; }

    [BsonElement("completionHash")]
    [BsonIgnoreIfNull]
    public string? CompletionHash { get; set; }
}

/// <summary>
/// Durable delivery envelope around a committed immutable reconcile intent.
/// Only state, attempts, lease and diagnostic fields are mutable after insert.
/// </summary>
[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_mapping_outbox")]
public sealed class DynamicFlowMappingOutboxItem
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("receiptId"), BsonRepresentation(BsonType.ObjectId)]
    public string ReceiptId { get; set; } = default!;

    [BsonElement("eventId"), BsonRepresentation(BsonType.ObjectId)]
    public string EventId { get; set; } = default!;

    [BsonElement("provenanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string ProvenanceId { get; set; } = default!;

    [BsonElement("targetReportId"), BsonRepresentation(BsonType.ObjectId)]
    public string TargetReportId { get; set; } = default!;

    [BsonElement("commandId")]
    public string CommandId { get; set; } = string.Empty;

    [BsonElement("operation")]
    public string Operation { get; set; } = string.Empty;

    [BsonElement("dedupeKey")]
    public string DedupeKey { get; set; } = string.Empty;

    [BsonElement("intent")]
    public DynamicFlowMappingReconcileIntent Intent { get; set; } = new();

    [BsonElement("intentHash")]
    public string IntentHash { get; set; } = string.Empty;

    [BsonElement("state")]
    public string State { get; set; } = DynamicFlowMappingOutboxStates.Pending;

    [BsonElement("attemptCount")]
    public int AttemptCount { get; set; }

    [BsonElement("repairEpoch")]
    public long RepairEpoch { get; set; }

    [BsonElement("projectorCheckpoints")]
    public List<DynamicFlowMappingProjectorCheckpoint>
        ProjectorCheckpoints { get; set; } = new();

    [BsonElement("nextAttemptAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime NextAttemptAtUtc { get; set; }

    [BsonElement("leaseId")]
    public string? LeaseId { get; set; }

    [BsonElement("leaseOwner")]
    public string? LeaseOwner { get; set; }

    [BsonElement("leaseAcquiredAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LeaseAcquiredAtUtc { get; set; }

    [BsonElement("leaseUntilUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LeaseUntilUtc { get; set; }

    [BsonElement("lastAttemptedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastAttemptedAtUtc { get; set; }

    [BsonElement("lastErrorCode")]
    public string? LastErrorCode { get; set; }

    [BsonElement("lastErrorSnapshotHash")]
    public string? LastErrorSnapshotHash { get; set; }

    [BsonElement("createdAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; }

    [BsonElement("updatedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAtUtc { get; set; }

    [BsonElement("partialAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PartialAtUtc { get; set; }

    [BsonElement("retryingAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? RetryingAtUtc { get; set; }

    [BsonElement("reconciledAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ReconciledAtUtc { get; set; }
}

public static class DynamicFlowMappingApplyStates
{
    public const string Committed = "COMMITTED";
    public const string Partial = "PARTIAL";
    public const string Retrying = "RETRYING";
    public const string Reconciled = "RECONCILED";
}

public static class DynamicFlowMappingProvenanceStates
{
    public const string Current = "CURRENT";
    public const string Invalidated = "INVALIDATED";
    public const string Superseded = "SUPERSEDED";
}

public static class DynamicFlowMappingOutboxStates
{
    public const string Pending = "PENDING";
    public const string Processing = "PROCESSING";
    public const string Partial = "PARTIAL";
    public const string Retrying = "RETRYING";
    public const string Reconciled = "RECONCILED";
    public const string DeadLetter = "DEAD_LETTER";
}

public static class DynamicFlowMappingProjectors
{
    public const string QueuePeriod = "QUEUE_PERIOD";
    public const string AssignmentStatus = "ASSIGNMENT_STATUS";
    public const string DocRolePeriod = "DOC_ROLE_PERIOD";
}

public static class DynamicFlowMappingProjectorCheckpointStates
{
    public const string Pending = "PENDING";
    public const string Processing = "PROCESSING";
    public const string Completed = "COMPLETED";
}

public static class DynamicFlowMappingEventTypes
{
    public const string ApplyCommitted = "DYNAMIC_FLOW_MAPPING_APPLY_COMMITTED";
    public const string RerunCommitted = "DYNAMIC_FLOW_MAPPING_RERUN_COMMITTED";
    public const string ApplyPartial = "DYNAMIC_FLOW_MAPPING_APPLY_PARTIAL";
    public const string ApplyReconciled = "DYNAMIC_FLOW_MAPPING_APPLY_RECONCILED";
    public const string ProvenanceInvalidated = "DYNAMIC_FLOW_MAPPING_PROVENANCE_INVALIDATED";
}

public static class DynamicFlowMappingPersistenceSchema
{
    public const string ReconcileIntentVersion = "P7-MAP-INTENT-1";
}
