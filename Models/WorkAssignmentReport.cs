using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models.Enums;

namespace tdtd_be.Models;

/// <summary>
/// Dữ liệu báo cáo thực tế của một WorkAssignment tại một kỳ cụ thể.
/// 
/// Hiểu ngắn gọn:
/// - WorkTemplateAssignee = binding runtime hiện hành
/// - WorkReportPeriod = 1 kỳ phải báo cáo
/// - WorkAssignmentReport = 1 bản dữ liệu thực tế của kỳ đó
/// 
/// Report luôn snapshot lại template + schedule tại thời điểm phát sinh,
/// để sau này assignment đổi template/schedule thì report cũ vẫn giữ nguyên lịch sử.
/// </summary>
[BsonIgnoreExtraElements]
[BsonCollection("work_assignment_report")]
public sealed class WorkAssignmentReport : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("workAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkAssignmentId { get; set; } = default!;

    [BsonElement("workReportPeriodId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkReportPeriodId { get; set; } = default!;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormTemplateId { get; set; }

    [BsonElement("dynamicFormTemplateCode")]
    public string? DynamicFormTemplateCode { get; set; }

    [BsonElement("dynamicFormTemplateName")]
    public string? DynamicFormTemplateName { get; set; }

    [BsonElement("dynamicFormFamilyId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormFamilyId { get; set; }

    [BsonElement("dynamicFormVersionNo")]
    public int? DynamicFormVersionNo { get; set; }

    [BsonElement("dynamicFormSchemaHash")]
    public string? DynamicFormSchemaHash { get; set; }

    [BsonElement("assigneeUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string AssigneeUserId { get; set; } = default!;

    [BsonElement("periodKey")]
    public string PeriodKey { get; set; } = default!;

    [BsonElement("periodInstanceKey")]
    public string PeriodInstanceKey { get; set; } = default!;

    [BsonElement("periodKind")]
    public string PeriodKind { get; set; } = WorkReportPeriodKind.Scheduled;

    [BsonElement("reportTitle")]
    public string? ReportTitle { get; set; }

    [BsonElement("reportDate")]
    public DateTime? ReportDate { get; set; }

    [BsonElement("startedDate")]
    public DateTime? StartedDate { get; set; }

    [BsonElement("completedDate")]
    public DateTime? CompletedDate { get; set; }

    [BsonElement("isHistoricalData")]
    public bool IsHistoricalData { get; set; }

    [BsonElement("historicalDataApproved")]
    public bool HistoricalDataApproved { get; set; }

    [BsonElement("historicalDataApprovedAtUtc")]
    public DateTime? HistoricalDataApprovedAtUtc { get; set; }

    [BsonElement("historicalDataApprovedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? HistoricalDataApprovedByUserId { get; set; }

    [BsonElement("periodStart")]
    public DateTime? PeriodStart { get; set; }

    [BsonElement("periodEnd")]
    public DateTime? PeriodEnd { get; set; }

    [BsonElement("dueAtUtc")]
    public DateTime? DueAtUtc { get; set; }

    [BsonElement("status")]
    public WorkAssignmentReportStatus Status { get; set; } = WorkAssignmentReportStatus.Draft;

    [BsonElement("submittedAtUtc")]
    public DateTime? SubmittedAtUtc { get; set; }

    [BsonElement("approvedAtUtc")]
    public DateTime? ApprovedAtUtc { get; set; }

    [BsonElement("approvedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ApprovedByUserId { get; set; }

    [BsonElement("autoApprovedAtUtc")]
    public DateTime? AutoApprovedAtUtc { get; set; }

    [BsonElement("autoApprovedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? AutoApprovedByUserId { get; set; }

    [BsonElement("autoApproveConditionSnapshotJson")]
    public string? AutoApproveConditionSnapshotJson { get; set; }

    [BsonElement("autoApprovalConfirmedAtUtc")]
    public DateTime? AutoApprovalConfirmedAtUtc { get; set; }

    [BsonElement("autoApprovalConfirmedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? AutoApprovalConfirmedByUserId { get; set; }

    [BsonElement("data")]
    public object? Data { get; set; }

    [BsonElement("scheduleSnapshotJson")]
    public string ScheduleSnapshotJson { get; set; } = default!;

    [BsonElement("dynamicExcelTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicExcelTemplateId { get; set; }

    [BsonElement("dynamicExcelTemplateCode")]
    public string DynamicExcelTemplateCode { get; set; } = default!;

    [BsonElement("dynamicExcelTemplateName")]
    public string DynamicExcelTemplateName { get; set; } = default!;

    [BsonElement("specJson")]
    public string SpecJson { get; set; } = default!;

    [BsonElement("dataRectR0")]
    public int DataRectR0 { get; set; }

    [BsonElement("dataRectC0")]
    public int DataRectC0 { get; set; }

    [BsonElement("dataRectR1")]
    public int DataRectR1 { get; set; }

    [BsonElement("dataRectC1")]
    public int DataRectC1 { get; set; }

    [BsonElement("w")]
    public int W { get; set; }

    [BsonElement("h")]
    public int H { get; set; }

    [BsonElement("values1DJson")]
    public string Values1DJson { get; set; } = default!;

    [BsonElement("fieldValuesJson")]
    public string? FieldValuesJson { get; set; }

    [BsonElement("tableValuesJson")]
    public string? TableValuesJson { get; set; }

    [BsonElement("dataOrigin")]
    public string DataOrigin { get; set; } = WorkReportDataOrigin.ManualInput;

    [BsonElement("cumulativeContributionMode")]
    public string CumulativeContributionMode { get; set; } = WorkReportCumulativeContributionMode.Include;

    [BsonElement("cumulativeContributionPolicyJson")]
    public string? CumulativeContributionPolicyJson { get; set; }

    [BsonElement("summarySourceJson")]
    public string? SummarySourceJson { get; set; }

    [BsonElement("payloadRevision")]
    public int PayloadRevision { get; set; }

    [BsonElement("payloadHash")]
    public string? PayloadHash { get; set; }

    [BsonElement("payloadSizeBytes")]
    public long PayloadSizeBytes { get; set; }

    [BsonElement("payloadStatus")]
    public string? PayloadStatus { get; set; }

    [BsonElement("payloadUpdatedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PayloadUpdatedAtUtc { get; set; }

    [BsonElement("payloadMutationCommandId")]
    public string? PayloadMutationCommandId { get; set; }

    [BsonElement("payloadMutationCommandHash")]
    public string? PayloadMutationCommandHash { get; set; }

    [BsonElement("payloadMutationOperation")]
    public string? PayloadMutationOperation { get; set; }

    [BsonElement("payloadMutationStartedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PayloadMutationStartedAtUtc { get; set; }

    [BsonElement("lastPayloadCommandId")]
    public string? LastPayloadCommandId { get; set; }

    [BsonElement("lastPayloadCommandHash")]
    public string? LastPayloadCommandHash { get; set; }

    [BsonElement("lastPayloadCommandOperation")]
    public string? LastPayloadCommandOperation { get; set; }

    [BsonElement("lastPayloadCommandRevision")]
    public int? LastPayloadCommandRevision { get; set; }

    [BsonElement("dynamicFlowMappingReceiptId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFlowMappingReceiptId { get; set; }

    [BsonElement("dynamicFlowMappingProvenanceId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFlowMappingProvenanceId { get; set; }

    [BsonElement("dynamicFlowMappingProvenanceHash")]
    public string? DynamicFlowMappingProvenanceHash { get; set; }

    [BsonElement("dynamicFlowMappingResultPayloadRevision")]
    public int? DynamicFlowMappingResultPayloadRevision { get; set; }

    [BsonElement("dynamicFlowMappingResultPayloadHash")]
    public string? DynamicFlowMappingResultPayloadHash { get; set; }

    [BsonElement("lifecycleRevision")]
    public int LifecycleRevision { get; set; }

    [BsonElement("lastLifecycleCommandId")]
    public string? LastLifecycleCommandId { get; set; }

    [BsonElement("lastLifecycleCommandHash")]
    public string? LastLifecycleCommandHash { get; set; }

    [BsonElement("lastLifecycleCommandOperation")]
    public string? LastLifecycleCommandOperation { get; set; }

    [BsonElement("lastLifecycleCommandRevision")]
    public int? LastLifecycleCommandRevision { get; set; }

    [BsonElement("lastLifecycleCommandPayloadRevision")]
    public int? LastLifecycleCommandPayloadRevision { get; set; }

    [BsonElement("lastLifecycleCommandStatus")]
    public WorkAssignmentReportStatus? LastLifecycleCommandStatus { get; set; }

    [BsonElement("lastLifecycleCommandIsActive")]
    public bool? LastLifecycleCommandIsActive { get; set; }

    /// <summary>
    /// Durable lifecycle projection work appended atomically with the report lifecycle CAS.
    /// Entries stay pending until every derived projection has converged from current Mongo state.
    /// </summary>
    [BsonElement("lifecycleProjectionOutbox")]
    public List<WorkReportLifecycleProjectionOutboxEntry> LifecycleProjectionOutbox { get; set; } = new();

    [BsonElement("lifecycleProjectionClaimToken")]
    public string? LifecycleProjectionClaimToken { get; set; }

    [BsonElement("lifecycleProjectionClaimedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LifecycleProjectionClaimedAtUtc { get; set; }

    [BsonElement("lifecycleProjectionClaimExpiresAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LifecycleProjectionClaimExpiresAtUtc { get; set; }

    [BsonElement("lifecycleProjectionLastCompletedRevision")]
    public int LifecycleProjectionLastCompletedRevision { get; set; }

    [BsonElement("lifecycleProjectionLastCompletedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LifecycleProjectionLastCompletedAtUtc { get; set; }

    [BsonElement("lifecycleProjectionLastError")]
    public string? LifecycleProjectionLastError { get; set; }

    [BsonElement("aggregateDependentsLastRecoveredLifecycleRevision")]
    public int AggregateDependentsLastRecoveredLifecycleRevision { get; set; }

    [BsonElement("aggregateDependentsLastRecoveredAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? AggregateDependentsLastRecoveredAtUtc { get; set; }

    [BsonElement("aggregateSourceReportIds")]
    public List<string> AggregateSourceReportIds { get; set; } = new();

    [BsonElement("aggregateSourceAssignmentIds")]
    public List<string> AggregateSourceAssignmentIds { get; set; } = new();

    [BsonElement("aggregateSourceUpdatedAtUtc")]
    public DateTime? AggregateSourceUpdatedAtUtc { get; set; }

    [BsonElement("aggregateSnapshotDirty")]
    public bool AggregateSnapshotDirty { get; set; }

    [BsonElement("aggregateSnapshotDirtyAtUtc")]
    public DateTime? AggregateSnapshotDirtyAtUtc { get; set; }

    [BsonElement("aggregateSnapshotRefreshedAtUtc")]
    public DateTime? AggregateSnapshotRefreshedAtUtc { get; set; }

    [BsonElement("aggregateRefreshError")]
    public string? AggregateRefreshError { get; set; }

    [BsonElement("isLateSubmission")]
    public bool IsLateSubmission { get; set; }

    [BsonElement("lateReason")]
    public string? LateReason { get; set; }

    [BsonElement("reviewerComment")]
    public string? ReviewerComment { get; set; }

    [BsonElement("reviewerEvaluation")]
    public string? ReviewerEvaluation { get; set; }

    [BsonElement("returnReason")]
    public string? ReturnReason { get; set; }

    [BsonElement("submittedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? SubmittedByUserId { get; set; }

    [BsonElement("returnedAtUtc")]
    public DateTime? ReturnedAtUtc { get; set; }

    [BsonElement("returnedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ReturnedByUserId { get; set; }

    [BsonElement("versionNo")]
    public int VersionNo { get; set; } = 1;

    [BsonElement("isCurrent")]
    public bool IsCurrent { get; set; } = true;

    [BsonElement("invalidatedByFlowEventId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? InvalidatedByFlowEventId { get; set; }

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;

    [BsonElement("deactivatedAtUtc")]
    public DateTime? DeactivatedAtUtc { get; set; }

    [BsonElement("deactivatedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DeactivatedByUserId { get; set; }

    [BsonElement("deactivationReason")]
    public string? DeactivationReason { get; set; }

    [BsonElement("reactivatedAtUtc")]
    public DateTime? ReactivatedAtUtc { get; set; }

    [BsonElement("reactivatedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ReactivatedByUserId { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class WorkReportLifecycleProjectionOutboxEntry
{
    [BsonElement("entryKey")]
    public string EntryKey { get; set; } = default!;

    [BsonElement("commandId")]
    public string CommandId { get; set; } = default!;

    [BsonElement("lifecycleRevision")]
    public int LifecycleRevision { get; set; }

    [BsonElement("operation")]
    public string Operation { get; set; } = default!;

    [BsonElement("state")]
    public string State { get; set; } = WorkReportLifecycleProjectionOutboxStates.Pending;

    [BsonElement("actorUserId")]
    public string? ActorUserId { get; set; }

    [BsonElement("fromStatus")]
    public string FromStatus { get; set; } = default!;

    [BsonElement("toStatus")]
    public string ToStatus { get; set; } = default!;

    [BsonElement("fromIsActive")]
    public bool FromIsActive { get; set; }

    [BsonElement("toIsActive")]
    public bool ToIsActive { get; set; }

    [BsonElement("payloadRevision")]
    public int PayloadRevision { get; set; }

    [BsonElement("payloadHash")]
    public string? PayloadHash { get; set; }

    [BsonElement("dynamicFlowMappingReceiptId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFlowMappingReceiptId { get; set; }

    [BsonElement("dynamicFlowMappingProvenanceId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFlowMappingProvenanceId { get; set; }

    [BsonElement("dynamicFlowMappingProvenanceHash")]
    public string? DynamicFlowMappingProvenanceHash { get; set; }

    [BsonElement("dynamicFlowMappingResultPayloadRevision")]
    public int? DynamicFlowMappingResultPayloadRevision { get; set; }

    [BsonElement("dynamicFlowMappingResultPayloadHash")]
    public string? DynamicFlowMappingResultPayloadHash { get; set; }

    [BsonElement("createdAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; }

    [BsonElement("lastAttemptedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastAttemptedAtUtc { get; set; }

    [BsonElement("attemptCount")]
    public int AttemptCount { get; set; }

    [BsonElement("completedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CompletedAtUtc { get; set; }

    [BsonElement("lastError")]
    public string? LastError { get; set; }

    [BsonElement("directProjectionState")]
    public string? DirectProjectionState { get; set; }

    [BsonElement("directProjectionReason")]
    public string? DirectProjectionReason { get; set; }

    [BsonElement("directProjectionRunId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DirectProjectionRunId { get; set; }

    [BsonElement("directProjectionGenerationId")]
    public string? DirectProjectionGenerationId { get; set; }

    [BsonElement("directProjectionGenerationHash")]
    public string? DirectProjectionGenerationHash { get; set; }

    [BsonElement("directProjectionCompletedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? DirectProjectionCompletedAtUtc { get; set; }

    /// <summary>
    /// Immutable business-audit events captured by the same CAS as the lifecycle change.
    /// The projection worker writes every event with its stable key before acknowledging
    /// this outbox entry, so retries cannot omit or duplicate a committed audit record.
    /// </summary>
    [BsonElement("businessEvents")]
    public List<WorkReportLifecycleBusinessEvent> BusinessEvents { get; set; } = new();
}

[BsonIgnoreExtraElements]
public sealed class WorkReportLifecycleBusinessEvent
{
    [BsonElement("eventKey")]
    public string EventKey { get; set; } = default!;

    [BsonElement("reportLogAction")]
    [BsonIgnoreIfNull]
    public string? ReportLogAction { get; set; }

    [BsonElement("userAction")]
    [BsonIgnoreIfNull]
    public string? UserAction { get; set; }

    [BsonElement("statusOperation")]
    [BsonIgnoreIfNull]
    public string? StatusOperation { get; set; }

    [BsonElement("statusScope")]
    [BsonIgnoreIfNull]
    public string? StatusScope { get; set; }

    [BsonElement("fromStatus")]
    public string FromStatus { get; set; } = default!;

    [BsonElement("toStatus")]
    public string ToStatus { get; set; } = default!;

    [BsonElement("reason")]
    [BsonIgnoreIfNull]
    public string? Reason { get; set; }

    [BsonElement("comment")]
    [BsonIgnoreIfNull]
    public string? Comment { get; set; }

    [BsonElement("snapshotJson")]
    [BsonIgnoreIfNull]
    public string? SnapshotJson { get; set; }

    [BsonElement("summaryVerb")]
    [BsonIgnoreIfNull]
    public string? SummaryVerb { get; set; }

    [BsonElement("data")]
    [BsonIgnoreIfNull]
    public Dictionary<string, string>? Data { get; set; }
}

public static class WorkReportLifecycleProjectionOutboxStates
{
    public const string Pending = "PENDING";
    public const string Completed = "COMPLETED";
}
