using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Models.Enums;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
public sealed class WorkListDocRole : DocRoleReadModelBase
{
    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("autoCode")]
    public string AutoCode { get; set; } = string.Empty;

    [BsonElement("code")]
    public string? Code { get; set; }

    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("type")]
    public WorkType Type { get; set; }

    [BsonElement("status")]
    public WorkStatus Status { get; set; }

    [BsonElement("priority")]
    public WorkPriority Priority { get; set; }

    [BsonElement("workCreatedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? WorkCreatedByUserId { get; set; }

    [BsonElement("ownerName")]
    public string? OwnerName { get; set; }

    [BsonElement("leaderDirectiveUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? LeaderDirectiveUserId { get; set; }

    [BsonElement("leaderWatchCount")]
    public int LeaderWatchCount { get; set; }

    [BsonElement("evaluationTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? EvaluationTemplateId { get; set; }

    [BsonElement("evaluationTemplateCode")]
    public string? EvaluationTemplateCode { get; set; }

    [BsonElement("evaluationTemplateLabel")]
    public string? EvaluationTemplateLabel { get; set; }

    [BsonElement("hasManualEvaluations")]
    public bool HasManualEvaluations { get; set; }

    [BsonElement("evaluatedAssignmentCount")]
    public int EvaluatedAssignmentCount { get; set; }

    [BsonElement("worstEvaluationCode")]
    public string? WorstEvaluationCode { get; set; }

    [BsonElement("worstEvaluationLabel")]
    public string? WorstEvaluationLabel { get; set; }

    [BsonElement("dueDate")]
    public DateTime? DueDate { get; set; }

    [BsonElement("workCreatedAtUtc")]
    public DateTime WorkCreatedAtUtc { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class AssignmentListDocRole : DocRoleReadModelBase
{
    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("assignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string AssignmentId { get; set; } = default!;

    [BsonElement("parentAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ParentAssignmentId { get; set; }

    [BsonElement("rootAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? RootAssignmentId { get; set; }

    [BsonElement("path")]
    public string Path { get; set; } = string.Empty;

    [BsonElement("level")]
    public int Level { get; set; }

    [BsonElement("code")]
    public string Code { get; set; } = string.Empty;

    [BsonElement("dynamicExcelId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicExcelId { get; set; }

    [BsonElement("dynamicExcelCode")]
    public string DynamicExcelCode { get; set; } = string.Empty;

    [BsonElement("dynamicExcelName")]
    public string DynamicExcelName { get; set; } = string.Empty;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormTemplateId { get; set; }

    [BsonElement("dynamicFormTemplateCode")]
    public string? DynamicFormTemplateCode { get; set; }

    [BsonElement("dynamicFormTemplateName")]
    public string? DynamicFormTemplateName { get; set; }

    [BsonElement("dynamicFormDataSourceRulesJson")]
    public string? DynamicFormDataSourceRulesJson { get; set; }

    [BsonElement("assignmentType")]
    public string AssignmentType { get; set; } = string.Empty;

    [BsonElement("aggregationType")]
    public string AggregationType { get; set; } = string.Empty;

    [BsonElement("assignees")]
    public List<UserRef> Assignees { get; set; } = new();

    [BsonElement("leaderWatchers")]
    public List<UserRef> LeaderWatchers { get; set; } = new();

    [BsonElement("description")]
    public string? Description { get; set; }

    [BsonElement("isActive")]
    public bool IsActive { get; set; }

    [BsonElement("allowUserCreatedReports")]
    public bool AllowUserCreatedReports { get; set; } = true;

    [BsonElement("startDate")]
    public DateTime? StartDate { get; set; }

    [BsonElement("completedDate")]
    public DateTime? CompletedDate { get; set; }

    [BsonElement("assignmentCreatedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? AssignmentCreatedByUserId { get; set; }

    [BsonElement("assigneeUserIds")]
    [BsonRepresentation(BsonType.ObjectId)]
    public List<string> AssigneeUserIds { get; set; } = new();

    [BsonElement("assigneeUnitIds")]
    [BsonRepresentation(BsonType.ObjectId)]
    public List<string> AssigneeUnitIds { get; set; } = new();

    [BsonElement("firstAssigneeName")]
    public string? FirstAssigneeName { get; set; }

    [BsonElement("firstAssigneeUnitName")]
    public string? FirstAssigneeUnitName { get; set; }

    [BsonElement("progressStatus")]
    public int ProgressStatus { get; set; }

    [BsonElement("progressStatusUpdatedAtUtc")]
    public DateTime? ProgressStatusUpdatedAtUtc { get; set; }

    [BsonElement("latestPeriodKey")]
    public string? LatestPeriodKey { get; set; }

    [BsonElement("latestDueAtUtc")]
    public DateTime? LatestDueAtUtc { get; set; }

    [BsonElement("hasAnyDuePeriod")]
    public bool HasAnyDuePeriod { get; set; }

    [BsonElement("hasOverduePeriod")]
    public bool HasOverduePeriod { get; set; }

    [BsonElement("worstPeriodStatus")]
    public int? WorstPeriodStatus { get; set; }

    [BsonElement("worstOverdueReasonCode")]
    public string? WorstOverdueReasonCode { get; set; }

    [BsonElement("worstOverdueReasonLabel")]
    public string? WorstOverdueReasonLabel { get; set; }

    [BsonElement("evaluationCode")]
    public string? EvaluationCode { get; set; }

    [BsonElement("evaluationLabel")]
    public string? EvaluationLabel { get; set; }

    [BsonElement("evaluationTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? EvaluationTemplateId { get; set; }

    [BsonElement("evaluationTemplateCode")]
    public string? EvaluationTemplateCode { get; set; }

    [BsonElement("evaluationTemplateLabel")]
    public string? EvaluationTemplateLabel { get; set; }

    [BsonElement("hasManualEvaluations")]
    public bool HasManualEvaluations { get; set; }

    [BsonElement("evaluatedAssignmentCount")]
    public int EvaluatedAssignmentCount { get; set; }

    [BsonElement("worstEvaluationCode")]
    public string? WorstEvaluationCode { get; set; }

    [BsonElement("worstEvaluationLabel")]
    public string? WorstEvaluationLabel { get; set; }

    [BsonElement("assignmentCreatedAtUtc")]
    public DateTime AssignmentCreatedAtUtc { get; set; }

    [BsonElement("assignmentUpdatedAtUtc")]
    public DateTime AssignmentUpdatedAtUtc { get; set; }

    [BsonElement("dueAtUtc")]
    public DateTime? DueAtUtc { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class MyReportTemplateListDocRole : DocRoleReadModelBase
{
    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("dynamicExcelId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicExcelId { get; set; }

    [BsonElement("dynamicExcelCode")]
    public string DynamicExcelCode { get; set; } = string.Empty;

    [BsonElement("dynamicExcelName")]
    public string DynamicExcelName { get; set; } = string.Empty;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormTemplateId { get; set; }

    [BsonElement("dynamicFormTemplateCode")]
    public string? DynamicFormTemplateCode { get; set; }

    [BsonElement("dynamicFormTemplateName")]
    public string? DynamicFormTemplateName { get; set; }

    [BsonElement("bindingCount")]
    public int BindingCount { get; set; }

    [BsonElement("periodCount")]
    public int PeriodCount { get; set; }

    [BsonElement("reportCount")]
    public int ReportCount { get; set; }

    [BsonElement("latestPeriodId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? LatestPeriodId { get; set; }

    [BsonElement("latestPeriodKey")]
    public string? LatestPeriodKey { get; set; }

    [BsonElement("latestPeriodStatus")]
    public WorkReportPeriodStatus? LatestPeriodStatus { get; set; }

    [BsonElement("latestDueAtUtc")]
    public DateTime? LatestDueAtUtc { get; set; }

    [BsonElement("latestReportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? LatestReportId { get; set; }

    [BsonElement("latestUpdatedAtUtc")]
    public DateTime? LatestUpdatedAtUtc { get; set; }

    [BsonElement("hasOverduePeriod")]
    public bool HasOverduePeriod { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class MyReportPeriodListDocRole : DocRoleReadModelBase
{
    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("assignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string AssignmentId { get; set; } = default!;

    [BsonElement("workTemplateAssigneeId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkTemplateAssigneeId { get; set; } = default!;

    [BsonElement("workReportPeriodId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkReportPeriodId { get; set; } = default!;

    [BsonElement("currentReportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? CurrentReportId { get; set; }

    [BsonElement("assigneeUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string AssigneeUserId { get; set; } = default!;

    [BsonElement("dynamicExcelId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DynamicExcelId { get; set; } = default!;

    [BsonElement("dynamicExcelCode")]
    public string DynamicExcelCode { get; set; } = string.Empty;

    [BsonElement("dynamicExcelName")]
    public string DynamicExcelName { get; set; } = string.Empty;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormTemplateId { get; set; }

    [BsonElement("dynamicFormTemplateCode")]
    public string? DynamicFormTemplateCode { get; set; }

    [BsonElement("dynamicFormTemplateName")]
    public string? DynamicFormTemplateName { get; set; }

    [BsonElement("periodKey")]
    public string PeriodKey { get; set; } = string.Empty;

    [BsonElement("periodInstanceKey")]
    public string PeriodInstanceKey { get; set; } = string.Empty;

    [BsonElement("periodKind")]
    public string PeriodKind { get; set; } = string.Empty;

    [BsonElement("reportTitle")]
    public string? ReportTitle { get; set; }

    [BsonElement("reportDate")]
    public DateTime? ReportDate { get; set; }

    [BsonElement("linkedScheduledPeriodId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? LinkedScheduledPeriodId { get; set; }

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

    [BsonElement("periodStatus")]
    public WorkReportPeriodStatus PeriodStatus { get; set; }

    [BsonElement("isOverdue")]
    public bool IsOverdue { get; set; }

    [BsonElement("reportStatus")]
    public WorkAssignmentReportStatus? ReportStatus { get; set; }

    [BsonElement("isCurrentReport")]
    public bool IsCurrentReport { get; set; }

    [BsonElement("reportIsActive")]
    public bool ReportIsActive { get; set; } = true;

    [BsonElement("reportDeactivatedAtUtc")]
    public DateTime? ReportDeactivatedAtUtc { get; set; }

    [BsonElement("reportDeactivationReason")]
    public string? ReportDeactivationReason { get; set; }

    [BsonElement("isLateSubmission")]
    public bool IsLateSubmission { get; set; }

    [BsonElement("versionNo")]
    public int VersionNo { get; set; }

    [BsonElement("lastSubmittedAtUtc")]
    public DateTime? LastSubmittedAtUtc { get; set; }

    [BsonElement("returnedAtUtc")]
    public DateTime? ReturnedAtUtc { get; set; }

    [BsonElement("approvedAtUtc")]
    public DateTime? ApprovedAtUtc { get; set; }

    [BsonElement("sortUpdatedAtUtc")]
    public DateTime SortUpdatedAtUtc { get; set; }

    [BsonElement("sourceCreatedAtUtc")]
    public DateTime SourceCreatedAtUtc { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class ReviewReportListDocRole : DocRoleReadModelBase
{
    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("assignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string AssignmentId { get; set; } = default!;

    [BsonElement("workReportPeriodId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkReportPeriodId { get; set; } = default!;

    [BsonElement("currentReportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? CurrentReportId { get; set; }

    [BsonElement("reviewerUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ReviewerUserId { get; set; } = default!;

    [BsonElement("dynamicExcelId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DynamicExcelId { get; set; } = default!;

    [BsonElement("dynamicExcelCode")]
    public string DynamicExcelCode { get; set; } = string.Empty;

    [BsonElement("dynamicExcelName")]
    public string DynamicExcelName { get; set; } = string.Empty;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormTemplateId { get; set; }

    [BsonElement("dynamicFormTemplateCode")]
    public string? DynamicFormTemplateCode { get; set; }

    [BsonElement("dynamicFormTemplateName")]
    public string? DynamicFormTemplateName { get; set; }

    [BsonElement("assigneeUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string AssigneeUserId { get; set; } = default!;

    [BsonElement("assigneeUserName")]
    public string? AssigneeUserName { get; set; }

    [BsonElement("assigneeFullName")]
    public string? AssigneeFullName { get; set; }

    [BsonElement("assigneeUnitId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? AssigneeUnitId { get; set; }

    [BsonElement("assigneeUnitName")]
    public string? AssigneeUnitName { get; set; }

    [BsonElement("assigneeUnitShortName")]
    public string? AssigneeUnitShortName { get; set; }

    [BsonElement("periodKey")]
    public string PeriodKey { get; set; } = string.Empty;

    [BsonElement("periodInstanceKey")]
    public string PeriodInstanceKey { get; set; } = string.Empty;

    [BsonElement("periodKind")]
    public string PeriodKind { get; set; } = string.Empty;

    [BsonElement("reportTitle")]
    public string? ReportTitle { get; set; }

    [BsonElement("reportDate")]
    public DateTime? ReportDate { get; set; }

    [BsonElement("linkedScheduledPeriodId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? LinkedScheduledPeriodId { get; set; }

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

    [BsonElement("periodStatus")]
    public WorkReportPeriodStatus PeriodStatus { get; set; }

    [BsonElement("isOverdue")]
    public bool IsOverdue { get; set; }

    [BsonElement("reportStatus")]
    public WorkAssignmentReportStatus? ReportStatus { get; set; }

    [BsonElement("reportIsActive")]
    public bool ReportIsActive { get; set; } = true;

    [BsonElement("reportDeactivatedAtUtc")]
    public DateTime? ReportDeactivatedAtUtc { get; set; }

    [BsonElement("reportDeactivationReason")]
    public string? ReportDeactivationReason { get; set; }

    [BsonElement("submittedAtUtc")]
    public DateTime? SubmittedAtUtc { get; set; }

    [BsonElement("approvedAtUtc")]
    public DateTime? ApprovedAtUtc { get; set; }

    [BsonElement("returnedAtUtc")]
    public DateTime? ReturnedAtUtc { get; set; }

    [BsonElement("returnReason")]
    public string? ReturnReason { get; set; }

    [BsonElement("reviewerComment")]
    public string? ReviewerComment { get; set; }

    [BsonElement("progressStatus")]
    public int ProgressStatus { get; set; }

    [BsonElement("progressStatusUpdatedAtUtc")]
    public DateTime? ProgressStatusUpdatedAtUtc { get; set; }

    [BsonElement("hasAnyDuePeriod")]
    public bool HasAnyDuePeriod { get; set; }

    [BsonElement("hasOverduePeriod")]
    public bool HasOverduePeriod { get; set; }

    [BsonElement("worstPeriodStatus")]
    public int? WorstPeriodStatus { get; set; }

    [BsonElement("worstOverdueReasonCode")]
    public string? WorstOverdueReasonCode { get; set; }

    [BsonElement("worstOverdueReasonLabel")]
    public string? WorstOverdueReasonLabel { get; set; }

    [BsonElement("reviewStatusBucket")]
    public string ReviewStatusBucket { get; set; } = string.Empty;

    [BsonElement("waitingReview")]
    public bool WaitingReview { get; set; }

    [BsonElement("returned")]
    public bool Returned { get; set; }

    [BsonElement("reviewRank")]
    public int ReviewRank { get; set; }

    [BsonElement("sortDueAtUtc")]
    public DateTime? SortDueAtUtc { get; set; }

    [BsonElement("sortUpdatedAtUtc")]
    public DateTime SortUpdatedAtUtc { get; set; }
}
