using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_instances")]
public sealed class DynamicFlowInstance : BaseEntity
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("workId"), BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("workType")]
    public string WorkType { get; set; } = string.Empty;

    [BsonElement("flowTemplateId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowTemplateId { get; set; } = default!;

    [BsonElement("flowTemplateVersionId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowTemplateVersionId { get; set; } = default!;

    [BsonElement("flowTemplateVersionNo")]
    public int FlowTemplateVersionNo { get; set; }

    [BsonElement("flowPayloadHash")]
    public string FlowPayloadHash { get; set; } = string.Empty;

    [BsonElement("catalogVersion")]
    public string CatalogVersion { get; set; } = string.Empty;

    [BsonElement("catalogSemanticHash")]
    public string CatalogSemanticHash { get; set; } = string.Empty;

    [BsonElement("archetypeId")]
    public string ArchetypeId { get; set; } = string.Empty;

    [BsonElement("definitionRevision")]
    public string DefinitionRevision { get; set; } = string.Empty;

    [BsonElement("topologySnapshotJson")]
    public string TopologySnapshotJson { get; set; } = string.Empty;

    [BsonElement("topologySnapshotHash")]
    public string TopologySnapshotHash { get; set; } = string.Empty;

    [BsonElement("executionEpoch")]
    public int ExecutionEpoch { get; set; } = 1;

    [BsonElement("finalizedExecutionEpoch")]
    public int? FinalizedExecutionEpoch { get; set; }

    [BsonElement("finalizedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? FinalizedAtUtc { get; set; }

    [BsonElement("finalizedByUserId"), BsonRepresentation(BsonType.ObjectId)]
    public string? FinalizedByUserId { get; set; }

    [BsonElement("finalizedByEventId"), BsonRepresentation(BsonType.ObjectId)]
    public string? FinalizedByEventId { get; set; }

    [BsonElement("entryFlowStepId")]
    public string EntryFlowStepId { get; set; } = string.Empty;

    [BsonElement("parentInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string? ParentInstanceId { get; set; }

    [BsonElement("parentStepInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string? ParentStepInstanceId { get; set; }

    [BsonElement("rootInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string? RootInstanceId { get; set; }

    [BsonElement("ancestryPath"), BsonRepresentation(BsonType.ObjectId)]
    public List<string> AncestryPath { get; set; } = new();

    [BsonElement("ancestryFlowFamilyIds"), BsonRepresentation(BsonType.ObjectId)]
    public List<string> AncestryFlowFamilyIds { get; set; } = new();

    [BsonElement("resultOwnerUserId"), BsonRepresentation(BsonType.ObjectId)]
    public string? ResultOwnerUserId { get; set; }

    [BsonElement("resultOwnerUnitId"), BsonRepresentation(BsonType.ObjectId)]
    public string? ResultOwnerUnitId { get; set; }

    [BsonElement("statisticOwnerIdentity")]
    public string? StatisticOwnerIdentity { get; set; }

    [BsonElement("periodKey")]
    public string PeriodKey { get; set; } = string.Empty;

    [BsonElement("scheduleIdentityHash")]
    public string ScheduleIdentityHash { get; set; } = string.Empty;

    [BsonElement("scheduleIdentityJson")]
    public string ScheduleIdentityJson { get; set; } = "{}";

    [BsonElement("periodicScheduleId"), BsonRepresentation(BsonType.ObjectId)]
    public string? PeriodicScheduleId { get; set; }

    [BsonElement("periodicOccurrenceId"), BsonRepresentation(BsonType.ObjectId)]
    public string? PeriodicOccurrenceId { get; set; }

    [BsonElement("timeZoneId")]
    public string? TimeZoneId { get; set; }

    [BsonElement("schedulePolicyVersion")]
    public string? SchedulePolicyVersion { get; set; }

    [BsonElement("participantSnapshotId"), BsonRepresentation(BsonType.ObjectId)]
    public string ParticipantSnapshotId { get; set; } = default!;

    [BsonElement("participantSnapshotHash")]
    public string ParticipantSnapshotHash { get; set; } = string.Empty;

    [BsonElement("issuerUserId"), BsonRepresentation(BsonType.ObjectId)]
    public string IssuerUserId { get; set; } = default!;

    [BsonElement("issuerUnitId"), BsonRepresentation(BsonType.ObjectId)]
    public string IssuerUnitId { get; set; } = default!;

    [BsonElement("launchCommandId")]
    public string LaunchCommandId { get; set; } = string.Empty;

    [BsonElement("state")]
    public string State { get; set; } = DynamicFlowInstanceStates.Pending;

    [BsonElement("resumeState")]
    public string? ResumeState { get; set; }

    [BsonElement("revision")]
    public long Revision { get; set; }

    [BsonElement("runtimeMaterializationFenceRevision")]
    public long RuntimeMaterializationFenceRevision { get; set; }

    [BsonElement("runtimeRecoveryEpoch")]
    public long RuntimeRecoveryEpoch { get; set; }

    [BsonElement("runtimeReconcileLeaseId")]
    public string? RuntimeReconcileLeaseId { get; set; }

    [BsonElement("runtimeReconcileLeaseExpiresAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? RuntimeReconcileLeaseExpiresAtUtc { get; set; }

    [BsonElement("nextEventSequence")]
    public long NextEventSequence { get; set; } = 1;

    [BsonElement("lastErrorCode")]
    public string? LastErrorCode { get; set; }

    [BsonElement("completedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CompletedAtUtc { get; set; }

    [BsonElement("compensatedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CompensatedAtUtc { get; set; }

    [BsonElement("compensationCheckedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CompensationCheckedAtUtc { get; set; }
}

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_step_instances")]
public sealed class DynamicFlowStepInstance : BaseEntity
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("flowInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowInstanceId { get; set; } = default!;

    [BsonElement("flowStepId")]
    public string FlowStepId { get; set; } = string.Empty;

    [BsonElement("flowStepCode")]
    public string FlowStepCode { get; set; } = string.Empty;

    [BsonElement("executionEpoch")]
    public int ExecutionEpoch { get; set; } = 1;

    [BsonElement("definitionRevision")]
    public string DefinitionRevision { get; set; } = string.Empty;

    [BsonElement("stepOrder")]
    public int StepOrder { get; set; }

    [BsonElement("formNodeId")]
    public string FormNodeId { get; set; } = string.Empty;

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

    [BsonElement("targetUnitId"), BsonRepresentation(BsonType.ObjectId)]
    public string TargetUnitId { get; set; } = default!;

    [BsonElement("participantUserIds"), BsonRepresentation(BsonType.ObjectId)]
    public List<string> ParticipantUserIds { get; set; } = new();

    [BsonElement("participantSnapshotId"), BsonRepresentation(BsonType.ObjectId)]
    public string ParticipantSnapshotId { get; set; } = default!;

    [BsonElement("attemptNo")]
    public int AttemptNo { get; set; } = 1;

    [BsonElement("reviewCycleNo")]
    public int ReviewCycleNo { get; set; } = 1;

    [BsonElement("previousAttemptStepInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string? PreviousAttemptStepInstanceId { get; set; }

    [BsonElement("previousAttemptAssignmentId"), BsonRepresentation(BsonType.ObjectId)]
    public string? PreviousAttemptAssignmentId { get; set; }

    [BsonElement("supersededByStepInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string? SupersededByStepInstanceId { get; set; }

    [BsonElement("branchId"), BsonRepresentation(BsonType.ObjectId)]
    public string BranchId { get; set; } = default!;

    [BsonElement("activatedByTransitionId")]
    public string? ActivatedByTransitionId { get; set; }

    [BsonElement("nextNodeIds")]
    public List<string> NextNodeIds { get; set; } = new();

    [BsonElement("isTerminalNode")]
    public bool IsTerminalNode { get; set; }

    [BsonElement("isCanonicalEpoch")]
    [BsonIgnoreIfNull]
    public bool? IsCanonicalEpoch { get; set; }

    [BsonElement("invalidatedByFlowEventId"), BsonRepresentation(BsonType.ObjectId)]
    public string? InvalidatedByFlowEventId { get; set; }

    [BsonElement("invalidatedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? InvalidatedAtUtc { get; set; }

    [BsonElement("isSupplemental")]
    public bool IsSupplemental { get; set; }

    [BsonElement("supplementalStepId")]
    public string? SupplementalStepId { get; set; }

    [BsonElement("requestedByUserId"), BsonRepresentation(BsonType.ObjectId)]
    public string? RequestedByUserId { get; set; }

    [BsonElement("completionRequired")]
    public bool CompletionRequired { get; set; }

    [BsonElement("supplementalCancelledAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? SupplementalCancelledAtUtc { get; set; }

    [BsonElement("supplementalCancelledByUserId"), BsonRepresentation(BsonType.ObjectId)]
    public string? SupplementalCancelledByUserId { get; set; }

    [BsonElement("supplementalCancelReason")]
    public string? SupplementalCancelReason { get; set; }

    [BsonElement("resultOwnerIdentity")]
    public string ResultOwnerIdentity { get; set; } = string.Empty;

    [BsonElement("statisticOwnerIdentity")]
    public string StatisticOwnerIdentity { get; set; } = string.Empty;

    [BsonElement("gatewayInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string? GatewayInstanceId { get; set; }

    [BsonElement("gatewayVersion")]
    public int? GatewayVersion { get; set; }

    [BsonElement("contributionId"), BsonRepresentation(BsonType.ObjectId)]
    public string? ContributionId { get; set; }

    [BsonElement("assignmentId"), BsonRepresentation(BsonType.ObjectId)]
    public string? AssignmentId { get; set; }

    [BsonElement("childInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string? ChildInstanceId { get; set; }

    [BsonElement("childFlowTemplateId"), BsonRepresentation(BsonType.ObjectId)]
    public string? ChildFlowTemplateId { get; set; }

    [BsonElement("childFlowVersionId"), BsonRepresentation(BsonType.ObjectId)]
    public string? ChildFlowVersionId { get; set; }

    [BsonElement("childState")]
    public string? ChildState { get; set; }

    [BsonElement("childOutcomeCode")]
    public string? ChildOutcomeCode { get; set; }

    [BsonElement("childLinkedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ChildLinkedAtUtc { get; set; }

    [BsonElement("reportId"), BsonRepresentation(BsonType.ObjectId)]
    public string? ReportId { get; set; }

    [BsonElement("reportLifecycleRevision")]
    public int? ReportLifecycleRevision { get; set; }

    [BsonElement("reportLifecycleEntryKey")]
    public string? ReportLifecycleEntryKey { get; set; }

    [BsonElement("reportLifecycleCommandId")]
    public string? ReportLifecycleCommandId { get; set; }

    [BsonElement("reportLifecycleStatus")]
    public string? ReportLifecycleStatus { get; set; }

    [BsonElement("reportLifecycleIsActive")]
    public bool? ReportLifecycleIsActive { get; set; }

    [BsonElement("state")]
    public string State { get; set; } = DynamicFlowStepStates.Pending;

    [BsonElement("resumeState")]
    public string? ResumeState { get; set; }

    [BsonElement("revision")]
    public long Revision { get; set; }

    [BsonElement("lastErrorCode")]
    public string? LastErrorCode { get; set; }
}

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_gateway_instances")]
public sealed class DynamicFlowGatewayInstance
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("flowInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowInstanceId { get; set; } = default!;

    [BsonElement("executionEpoch")]
    public int ExecutionEpoch { get; set; }

    [BsonElement("gatewayNodeId")]
    public string GatewayNodeId { get; set; } = string.Empty;

    [BsonElement("gatewayInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string GatewayInstanceId { get; set; } = default!;

    [BsonElement("gatewayVersion")]
    public int GatewayVersion { get; set; }

    [BsonElement("gatewayKind")]
    public string GatewayKind { get; set; } = string.Empty;

    [BsonElement("expectedContributionIds"), BsonRepresentation(BsonType.ObjectId)]
    public List<string> ExpectedContributionIds { get; set; } = new();

    [BsonElement("arrivedContributionIds"), BsonRepresentation(BsonType.ObjectId)]
    public List<string> ArrivedContributionIds { get; set; } = new();

    [BsonElement("requiredContributionCount")]
    public int RequiredContributionCount { get; set; }

    [BsonElement("cancelledContributionIds"), BsonRepresentation(BsonType.ObjectId)]
    public List<string> CancelledContributionIds { get; set; } = new();

    [BsonElement("lateContributionIds"), BsonRepresentation(BsonType.ObjectId)]
    public List<string> LateContributionIds { get; set; } = new();

    [BsonElement("winnerContributionId"), BsonRepresentation(BsonType.ObjectId)]
    public string? WinnerContributionId { get; set; }

    [BsonElement("inputSnapshotHash")]
    public string? InputSnapshotHash { get; set; }

    [BsonElement("evaluatorVersion")]
    public string? EvaluatorVersion { get; set; }

    [BsonElement("selectedEdgeId")]
    public string? SelectedEdgeId { get; set; }

    [BsonElement("decisionReasonCode")]
    public string? DecisionReasonCode { get; set; }

    [BsonElement("downstreamNodeId")]
    public string DownstreamNodeId { get; set; } = string.Empty;

    [BsonElement("state")]
    public string State { get; set; } = DynamicFlowGatewayStates.Collecting;

    [BsonElement("isCanonicalEpoch")]
    [BsonIgnoreIfNull]
    public bool? IsCanonicalEpoch { get; set; }

    [BsonElement("invalidatedByFlowEventId"), BsonRepresentation(BsonType.ObjectId)]
    public string? InvalidatedByFlowEventId { get; set; }

    [BsonElement("invalidatedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? InvalidatedAtUtc { get; set; }

    [BsonElement("revision")]
    public long Revision { get; set; }

    [BsonElement("releasedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ReleasedAtUtc { get; set; }

    [BsonElement("createdAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; }

    [BsonElement("updatedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAtUtc { get; set; }

    [BsonElement("updatedByUserId"), BsonRepresentation(BsonType.ObjectId)]
    public string UpdatedByUserId { get; set; } = default!;
}

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_gateway_contributions")]
public sealed class DynamicFlowGatewayContribution
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("flowInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowInstanceId { get; set; } = default!;

    [BsonElement("gatewayInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string GatewayInstanceId { get; set; } = default!;

    [BsonElement("gatewayVersion")]
    public int GatewayVersion { get; set; }

    [BsonElement("contributionId"), BsonRepresentation(BsonType.ObjectId)]
    public string ContributionId { get; set; } = default!;

    [BsonElement("branchId"), BsonRepresentation(BsonType.ObjectId)]
    public string BranchId { get; set; } = default!;

    [BsonElement("stepInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string StepInstanceId { get; set; } = default!;

    [BsonElement("assignmentId"), BsonRepresentation(BsonType.ObjectId)]
    public string AssignmentId { get; set; } = default!;

    [BsonElement("effectiveStatus")]
    public string EffectiveStatus { get; set; } = DynamicFlowEffectiveStatuses.Effective;

    [BsonElement("sourceCommandId")]
    public string SourceCommandId { get; set; } = string.Empty;

    [BsonElement("arrivedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ArrivedAtUtc { get; set; }

    [BsonElement("arrivedByUserId"), BsonRepresentation(BsonType.ObjectId)]
    public string ArrivedByUserId { get; set; } = default!;
}

public static class DynamicFlowGatewayStates
{
    public const string Collecting = "COLLECTING";
    public const string Satisfied = "SATISFIED";
    public const string Impossible = "IMPOSSIBLE";
}

public static class DynamicFlowGatewayContributionOutcomes
{
    public const string Effective = "EFFECTIVE";
    public const string LateIgnored = "LATE_IGNORED";
}

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_participant_snapshots")]
public sealed class DynamicFlowParticipantSnapshot
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("flowInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowInstanceId { get; set; } = default!;

    [BsonElement("issuerUserId"), BsonRepresentation(BsonType.ObjectId)]
    public string IssuerUserId { get; set; } = default!;

    [BsonElement("issuerUnitId"), BsonRepresentation(BsonType.ObjectId)]
    public string IssuerUnitId { get; set; } = default!;

    [BsonElement("bindings")]
    public List<DynamicFlowParticipantBinding> Bindings { get; set; } = new();

    [BsonElement("sourceRevisionTokens")]
    public SortedDictionary<string, string> SourceRevisionTokens { get; set; } = new(StringComparer.Ordinal);

    [BsonElement("snapshotHash")]
    public string SnapshotHash { get; set; } = string.Empty;

    [BsonElement("frozenAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime FrozenAtUtc { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class DynamicFlowParticipantBinding
{
    [BsonElement("targetUnitId"), BsonRepresentation(BsonType.ObjectId)]
    public string TargetUnitId { get; set; } = default!;

    [BsonElement("assigneeUserIds"), BsonRepresentation(BsonType.ObjectId)]
    public List<string> AssigneeUserIds { get; set; } = new();

    [BsonElement("participants")]
    public List<DynamicFlowParticipantUserSnapshot> Participants { get; set; } = new();

    [BsonElement("roleCodes")]
    public List<string> RoleCodes { get; set; } = new();
}

[BsonIgnoreExtraElements]
public sealed class DynamicFlowParticipantUserSnapshot
{
    [BsonElement("userId"), BsonRepresentation(BsonType.ObjectId)]
    public string UserId { get; set; } = default!;

    [BsonElement("username")]
    public string Username { get; set; } = string.Empty;

    [BsonElement("fullName")]
    public string FullName { get; set; } = string.Empty;

    [BsonElement("unitId"), BsonRepresentation(BsonType.ObjectId)]
    public string UnitId { get; set; } = default!;

    [BsonElement("unitSymbol")]
    public string? UnitSymbol { get; set; }

    [BsonElement("unitShortName")]
    public string? UnitShortName { get; set; }

    [BsonElement("unitName")]
    public string? UnitName { get; set; }

    [BsonElement("positionCode")]
    public string? PositionCode { get; set; }

    [BsonElement("positionName")]
    public string? PositionName { get; set; }
}

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_runtime_command_receipts")]
public sealed class DynamicFlowRuntimeCommandReceipt
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("scopeKind")]
    public string ScopeKind { get; set; } = DynamicFlowCommandScopeKinds.Launch;

    [BsonElement("scopeId")]
    public string ScopeId { get; set; } = string.Empty;

    [BsonElement("workId"), BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("flowTemplateVersionId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowTemplateVersionId { get; set; } = default!;

    [BsonElement("flowInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string? FlowInstanceId { get; set; }

    [BsonElement("commandType")]
    public string CommandType { get; set; } = string.Empty;

    [BsonElement("commandId")]
    public string CommandId { get; set; } = string.Empty;

    [BsonElement("requestHash")]
    public string RequestHash { get; set; } = string.Empty;

    [BsonElement("commandIdentityHash")]
    public string CommandIdentityHash { get; set; } = string.Empty;

    [BsonElement("snapshotToken")]
    public string SnapshotToken { get; set; } = string.Empty;

    [BsonElement("expectedRevision")]
    public long? ExpectedRevision { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = DynamicFlowRuntimeCommandStatuses.Pending;

    [BsonElement("resultSnapshot")]
    public BsonDocument? ResultSnapshot { get; set; }

    [BsonElement("resultSnapshotHash")]
    public string? ResultSnapshotHash { get; set; }

    [BsonElement("errorCode")]
    public string? ErrorCode { get; set; }

    [BsonElement("createdAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; }

    [BsonElement("updatedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAtUtc { get; set; }

    [BsonElement("updatedByUserId"), BsonRepresentation(BsonType.ObjectId)]
    public string? UpdatedByUserId { get; set; }

    [BsonElement("completedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CompletedAtUtc { get; set; }
}

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_runtime_events")]
public sealed class DynamicFlowRuntimeEvent
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("flowInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowInstanceId { get; set; } = default!;

    [BsonElement("stepInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string? StepInstanceId { get; set; }

    [BsonElement("executionEpoch")]
    public int ExecutionEpoch { get; set; } = 1;

    [BsonElement("branchId"), BsonRepresentation(BsonType.ObjectId)]
    public string? BranchId { get; set; }

    [BsonElement("attemptNo")]
    public int? AttemptNo { get; set; }

    [BsonElement("gatewayInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string? GatewayInstanceId { get; set; }

    [BsonElement("gatewayVersion")]
    public int? GatewayVersion { get; set; }

    [BsonElement("contributionId"), BsonRepresentation(BsonType.ObjectId)]
    public string? ContributionId { get; set; }

    [BsonElement("sequence")]
    public long Sequence { get; set; }

    [BsonElement("eventType")]
    public string EventType { get; set; } = string.Empty;

    [BsonElement("commandId")]
    public string CommandId { get; set; } = string.Empty;

    [BsonElement("correlationId")]
    public string? CorrelationId { get; set; }

    [BsonElement("sourceEventKey")]
    public string? SourceEventKey { get; set; }

    [BsonElement("fromState")]
    public string? FromState { get; set; }

    [BsonElement("toState")]
    public string? ToState { get; set; }

    [BsonElement("fromRevision")]
    public long? FromRevision { get; set; }

    [BsonElement("toRevision")]
    public long? ToRevision { get; set; }

    [BsonElement("reasonCode")]
    public string? ReasonCode { get; set; }

    [BsonElement("affectedRefs")]
    public List<string> AffectedRefs { get; set; } = new();

    [BsonElement("actorUserId"), BsonRepresentation(BsonType.ObjectId)]
    public string ActorUserId { get; set; } = default!;

    [BsonElement("visibleUnitIds"), BsonRepresentation(BsonType.ObjectId)]
    public List<string> VisibleUnitIds { get; set; } = new();

    [BsonElement("payload")]
    public BsonDocument Payload { get; set; } = new();

    [BsonElement("payloadHash")]
    public string PayloadHash { get; set; } = string.Empty;

    [BsonElement("occurredAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime OccurredAtUtc { get; set; }
}

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_runtime_outbox")]
public sealed class DynamicFlowRuntimeOutboxItem
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("flowInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowInstanceId { get; set; } = default!;

    [BsonElement("stepInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string StepInstanceId { get; set; } = default!;

    [BsonElement("operation")]
    public string Operation { get; set; } = string.Empty;

    [BsonElement("dedupeKey")]
    public string DedupeKey { get; set; } = string.Empty;

    [BsonElement("payload")]
    public BsonDocument Payload { get; set; } = new();

    [BsonElement("payloadHash")]
    public string PayloadHash { get; set; } = string.Empty;

    [BsonElement("status")]
    public string Status { get; set; } = DynamicFlowRuntimeOutboxStatuses.Pending;

    [BsonElement("attemptCount")]
    public int AttemptCount { get; set; }

    [BsonElement("repairEpoch")]
    public long RepairEpoch { get; set; }

    [BsonElement("nextAttemptAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime NextAttemptAtUtc { get; set; }

    [BsonElement("leaseId")]
    public string? LeaseId { get; set; }

    [BsonElement("leaseUntilUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LeaseUntilUtc { get; set; }

    [BsonElement("lastErrorCode")]
    public string? LastErrorCode { get; set; }

    [BsonElement("createdAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; }

    [BsonElement("updatedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAtUtc { get; set; }

    [BsonElement("updatedByUserId"), BsonRepresentation(BsonType.ObjectId)]
    public string? UpdatedByUserId { get; set; }

    [BsonElement("completedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CompletedAtUtc { get; set; }
}

public static class DynamicFlowInstanceStates
{
    public const string Pending = "PENDING";
    public const string Materializing = "MATERIALIZING";
    public const string Active = "ACTIVE";
    public const string Partial = "PARTIAL";
    public const string Retrying = "RETRYING";
    public const string Reconciled = "RECONCILED";
    public const string Completed = "COMPLETED";
    public const string Finalized = "FINALIZED";
    public const string Failed = "FAILED";
    public const string Terminated = "TERMINATED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending, Materializing, Active, Partial, Retrying, Reconciled, Completed,
        Finalized, Failed, Terminated
    };
}

public static class DynamicFlowStepStates
{
    public const string Pending = "PENDING";
    public const string Materializing = "MATERIALIZING";
    public const string Assigned = "ASSIGNED";
    public const string InProgress = "IN_PROGRESS";
    public const string Submitted = "SUBMITTED";
    public const string Returned = "RETURNED";
    public const string Approved = "APPROVED";
    public const string Completed = "COMPLETED";
    public const string WaitingChild = "WAITING_CHILD";
    public const string CancelledByGateway = "CANCELLED_BY_GATEWAY";
    public const string Partial = "PARTIAL";
    public const string Retrying = "RETRYING";
    public const string Reconciled = "RECONCILED";
    public const string Failed = "FAILED";
    public const string Terminated = "TERMINATED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending, Materializing, Assigned, InProgress, Submitted, Returned, Approved, Completed,
        WaitingChild,
        CancelledByGateway,
        Partial, Retrying, Reconciled, Failed, Terminated
    };
}

public static class DynamicFlowRuntimeStateContract
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> InstanceTransitions =
        Map(
            (DynamicFlowInstanceStates.Pending, DynamicFlowInstanceStates.Materializing),
            (DynamicFlowInstanceStates.Materializing, DynamicFlowInstanceStates.Active),
            (DynamicFlowInstanceStates.Materializing, DynamicFlowInstanceStates.Partial),
            (DynamicFlowInstanceStates.Materializing, DynamicFlowInstanceStates.Failed),
            (DynamicFlowInstanceStates.Active, DynamicFlowInstanceStates.Completed),
            (DynamicFlowInstanceStates.Completed, DynamicFlowInstanceStates.Finalized),
            (DynamicFlowInstanceStates.Completed, DynamicFlowInstanceStates.Active),
            (DynamicFlowInstanceStates.Completed, DynamicFlowInstanceStates.Terminated),
            (DynamicFlowInstanceStates.Active, DynamicFlowInstanceStates.Finalized),
            (DynamicFlowInstanceStates.Active, DynamicFlowInstanceStates.Partial),
            (DynamicFlowInstanceStates.Active, DynamicFlowInstanceStates.Failed),
            (DynamicFlowInstanceStates.Active, DynamicFlowInstanceStates.Terminated),
            (DynamicFlowInstanceStates.Partial, DynamicFlowInstanceStates.Retrying),
            (DynamicFlowInstanceStates.Partial, DynamicFlowInstanceStates.Failed),
            (DynamicFlowInstanceStates.Retrying, DynamicFlowInstanceStates.Reconciled),
            (DynamicFlowInstanceStates.Retrying, DynamicFlowInstanceStates.Failed),
            (DynamicFlowInstanceStates.Reconciled, DynamicFlowInstanceStates.Active));

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> StepTransitions =
        Map(
            (DynamicFlowStepStates.Pending, DynamicFlowStepStates.Materializing),
            (DynamicFlowStepStates.Materializing, DynamicFlowStepStates.Assigned),
            (DynamicFlowStepStates.Materializing, DynamicFlowStepStates.Partial),
            (DynamicFlowStepStates.Materializing, DynamicFlowStepStates.Failed),
            (DynamicFlowStepStates.Assigned, DynamicFlowStepStates.InProgress),
            (DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Submitted),
            (DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Returned),
            (DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Approved),
            (DynamicFlowStepStates.Returned, DynamicFlowStepStates.InProgress),
            (DynamicFlowStepStates.Approved, DynamicFlowStepStates.Returned),
            (DynamicFlowStepStates.Approved, DynamicFlowStepStates.Completed),
            (DynamicFlowStepStates.Approved, DynamicFlowStepStates.WaitingChild),
            (DynamicFlowStepStates.WaitingChild, DynamicFlowStepStates.Completed),
            (DynamicFlowStepStates.WaitingChild, DynamicFlowStepStates.Failed),
            (DynamicFlowStepStates.WaitingChild, DynamicFlowStepStates.Terminated),
            (DynamicFlowStepStates.Assigned, DynamicFlowStepStates.CancelledByGateway),
            (DynamicFlowStepStates.InProgress, DynamicFlowStepStates.CancelledByGateway),
            (DynamicFlowStepStates.Submitted, DynamicFlowStepStates.CancelledByGateway),
            (DynamicFlowStepStates.Returned, DynamicFlowStepStates.CancelledByGateway),
            (DynamicFlowStepStates.Approved, DynamicFlowStepStates.CancelledByGateway),
            (DynamicFlowStepStates.Assigned, DynamicFlowStepStates.Partial),
            (DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Partial),
            (DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Partial),
            (DynamicFlowStepStates.Returned, DynamicFlowStepStates.Partial),
            (DynamicFlowStepStates.Approved, DynamicFlowStepStates.Partial),
            (DynamicFlowStepStates.Partial, DynamicFlowStepStates.Retrying),
            (DynamicFlowStepStates.Partial, DynamicFlowStepStates.Failed),
            (DynamicFlowStepStates.Retrying, DynamicFlowStepStates.Reconciled),
            (DynamicFlowStepStates.Retrying, DynamicFlowStepStates.Failed),
            (DynamicFlowStepStates.Reconciled, DynamicFlowStepStates.Assigned),
            (DynamicFlowStepStates.Reconciled, DynamicFlowStepStates.InProgress),
            (DynamicFlowStepStates.Reconciled, DynamicFlowStepStates.Submitted),
            (DynamicFlowStepStates.Reconciled, DynamicFlowStepStates.Returned),
            (DynamicFlowStepStates.Reconciled, DynamicFlowStepStates.Approved));

    public static bool CanTransitionInstance(string from, string to)
        => DynamicFlowInstanceStates.All.Contains(from) &&
           DynamicFlowInstanceStates.All.Contains(to) &&
           InstanceTransitions.TryGetValue(from, out var targets) &&
           targets.Contains(to);

    public static bool CanTransitionStep(string from, string to)
        => DynamicFlowStepStates.All.Contains(from) &&
           DynamicFlowStepStates.All.Contains(to) &&
           StepTransitions.TryGetValue(from, out var targets) &&
           targets.Contains(to);

    public static void RequireInstanceTransition(string from, string to)
    {
        if (!CanTransitionInstance(from, to))
            throw new InvalidOperationException($"DYNAMIC_FLOW_INSTANCE_TRANSITION_INVALID:{from}:{to}");
    }

    public static void RequireStepTransition(string from, string to)
    {
        if (!CanTransitionStep(from, to))
            throw new InvalidOperationException($"DYNAMIC_FLOW_STEP_TRANSITION_INVALID:{from}:{to}");
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> Map(
        params (string From, string To)[] transitions)
        => transitions
            .GroupBy(item => item.From, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<string>)group.Select(item => item.To)
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
}

public static class DynamicFlowCommandScopeKinds
{
    public const string Launch = "LAUNCH";
    public const string Instance = "INSTANCE";
}

public static class DynamicFlowRuntimeCommandStatuses
{
    public const string Pending = "PENDING";
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
}

public static class DynamicFlowRuntimeOutboxStatuses
{
    public const string Pending = "PENDING";
    public const string Processing = "PROCESSING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string DeadLetter = "DEAD_LETTER";
}
