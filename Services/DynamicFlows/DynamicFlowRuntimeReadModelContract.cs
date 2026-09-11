using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignments.Domain;

namespace tdtd_be.Services.DynamicFlows;

internal sealed record DynamicFlowRuntimePeriodReadModelSource(
    WorkReportPeriod Period,
    WorkAssignment Assignment,
    WorkTemplateAssignee? Binding,
    WorkAssignmentReport? CurrentReport);

/// <summary>
/// Pure, source-derived equality contract for the DocRole read models rebuilt by
/// P5. Audit timestamps are deliberately excluded because projection writes own
/// their clock; all business fields, active-row markers and canonical role/user
/// identities are compared.
/// </summary>
internal static class DynamicFlowRuntimeReadModelContract
{
    public static bool MatchesAssignment(
        AssignmentListDocRole row,
        WorkAssignment assignment,
        string expectedUserId,
        IEnumerable<DocRoleType> expectedRoles,
        UserRef? expectedUser,
        IEnumerable<string>? inheritedVisibleUnitIds = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedUserId);
        ArgumentNullException.ThrowIfNull(expectedRoles);

        var assignees = CanonicalUsers(assignment.Assignees);
        var leaderWatchers = CanonicalUsers(assignment.LeaderWatchers);
        var assigneeUserIds = assignees
            .Select(item => item.UserId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var assigneeUnitIds = assignees
            .Select(item => item.UnitId)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var firstAssignee = assignees
            .OrderBy(item => item.UnitShortName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => item.FullName ?? string.Empty, StringComparer.Ordinal)
            .FirstOrDefault();
        var visibleUnitIds = DynamicFlowBranchVisibility.BuildVisibleUnitIds(
            assignment,
            inheritedVisibleUnitIds);

        return IsActiveRow(row) &&
               row.DocType == DocType.WORK_ASSIGNMENT &&
               string.Equals(row.DocId, assignment.Id, StringComparison.Ordinal) &&
               string.Equals(row.AssignmentId, assignment.Id, StringComparison.Ordinal) &&
               string.Equals(row.WorkId, assignment.WorkId, StringComparison.Ordinal) &&
               string.Equals(row.UserId, expectedUserId, StringComparison.Ordinal) &&
               SameUser(row.User, expectedUser) &&
               SameRoles(row.Roles, expectedRoles) &&
               string.Equals(
                   Normalize(row.ParentAssignmentId),
                   Normalize(assignment.ParentAssignmentId),
                   StringComparison.Ordinal) &&
               string.Equals(
                   Normalize(row.RootAssignmentId),
                   Normalize(assignment.RootAssignmentId),
                   StringComparison.Ordinal) &&
               string.Equals(row.Path, assignment.Path ?? string.Empty, StringComparison.Ordinal) &&
               string.Equals(
                   Normalize(row.FlowTemplateId),
                   Normalize(assignment.FlowTemplateId),
                   StringComparison.Ordinal) &&
               row.FlowTemplateVersionNo == assignment.FlowTemplateVersionNo &&
               string.Equals(
                   Normalize(row.FlowInstanceId),
                   Normalize(assignment.FlowInstanceId),
                   StringComparison.Ordinal) &&
               string.Equals(row.FlowStepId, assignment.FlowStepId, StringComparison.Ordinal) &&
               string.Equals(row.FlowStepCode, assignment.FlowStepCode, StringComparison.Ordinal) &&
               row.FlowStepOrder == assignment.FlowStepOrder &&
               string.Equals(
                   Normalize(row.FlowBranchId),
                   Normalize(assignment.FlowBranchId),
                   StringComparison.Ordinal) &&
               string.Equals(
                   Normalize(row.ParentFlowBranchId),
                   Normalize(assignment.ParentFlowBranchId),
                   StringComparison.Ordinal) &&
               row.FlowAttemptNo == assignment.FlowAttemptNo &&
               string.Equals(row.FlowRole, assignment.FlowRole, StringComparison.Ordinal) &&
               string.Equals(
                   row.FlowEffectiveStatus,
                   assignment.FlowEffectiveStatus,
                   StringComparison.Ordinal) &&
               string.Equals(
                   Normalize(row.IssuedByUnitId),
                   Normalize(assignment.IssuedByUnitId),
                   StringComparison.Ordinal) &&
               SameStrings(row.TargetUnitIds, assignment.TargetUnitIds) &&
               SameStrings(row.VisibleUnitIds, visibleUnitIds) &&
               row.AllowSubFlow == assignment.AllowSubFlow &&
               row.IsFlowFinalNode == assignment.IsFlowFinalNode &&
               string.Equals(
                   Normalize(row.InvalidatedByFlowEventId),
                   Normalize(assignment.InvalidatedByFlowEventId),
                   StringComparison.Ordinal) &&
               row.Level == assignment.Level &&
               string.Equals(row.Code, assignment.Code ?? string.Empty, StringComparison.Ordinal) &&
               string.Equals(row.Name, ResolveAssignmentName(assignment), StringComparison.Ordinal) &&
               string.Equals(row.DynamicExcelId, assignment.DynamicExcelId, StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicExcelCode,
                   assignment.DynamicExcelCode ?? string.Empty,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicExcelName,
                   assignment.DynamicExcelName ?? string.Empty,
                   StringComparison.Ordinal) &&
               string.Equals(
                   Normalize(row.DynamicFormTemplateId),
                   Normalize(assignment.DynamicFormTemplateId),
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicFormTemplateCode,
                   assignment.DynamicFormTemplateCode,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicFormTemplateName,
                   assignment.DynamicFormTemplateName,
                   StringComparison.Ordinal) &&
               string.Equals(
                   Normalize(row.DynamicFormFamilyId),
                   Normalize(assignment.DynamicFormFamilyId),
                   StringComparison.Ordinal) &&
               row.DynamicFormVersionNo == assignment.DynamicFormVersionNo &&
               string.Equals(
                   row.DynamicFormSchemaHash,
                   assignment.DynamicFormSchemaHash,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicFormDataSourceRulesJson,
                   assignment.DynamicFormDataSourceRulesJson,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.AutoApproveConditionJson,
                   assignment.AutoApproveConditionJson,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.AssignmentType,
                   assignment.AssignmentType ?? string.Empty,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.AggregationType,
                   assignment.AggregationType ?? string.Empty,
                   StringComparison.Ordinal) &&
               SameUsers(row.Assignees, assignees) &&
               SameUsers(row.LeaderWatchers, leaderWatchers) &&
               string.Equals(row.Description, assignment.Description, StringComparison.Ordinal) &&
               row.IsActive == assignment.IsActive &&
               row.StartDate == assignment.StartDate &&
               row.DueDate == assignment.DueDate &&
               row.CompletedDate == assignment.CompletedDate &&
               row.CompletedAtUtc == assignment.CompletedAtUtc &&
               string.Equals(
                   Normalize(row.CompletedByUserId),
                   Normalize(assignment.CompletedByUserId),
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.AssignmentCreatedByUserId,
                   assignment.CreatedByUserId,
                   StringComparison.Ordinal) &&
               SameStrings(row.AssigneeUserIds, assigneeUserIds) &&
               SameStrings(row.AssigneeUnitIds, assigneeUnitIds) &&
               string.Equals(
                   row.FirstAssigneeName,
                   firstAssignee?.FullName,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.FirstAssigneeUnitName,
                   firstAssignee?.UnitName,
                   StringComparison.Ordinal) &&
               row.ProgressStatus == assignment.ProgressStatus &&
               row.ProgressStatusUpdatedAtUtc == assignment.ProgressStatusUpdatedAtUtc &&
               string.Equals(row.LatestPeriodKey, assignment.LatestPeriodKey, StringComparison.Ordinal) &&
               row.LatestDueAtUtc == assignment.LatestDueAtUtc &&
               row.HasAnyDuePeriod == assignment.HasAnyDuePeriod &&
               row.HasOverduePeriod == assignment.HasOverduePeriod &&
               row.WorstPeriodStatus == assignment.WorstPeriodStatus &&
               string.Equals(
                   row.WorstOverdueReasonCode,
                   assignment.WorstOverdueReasonCode,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.WorstOverdueReasonLabel,
                   assignment.WorstOverdueReasonLabel,
                   StringComparison.Ordinal) &&
               string.Equals(row.EvaluationCode, assignment.EvaluationCode, StringComparison.Ordinal) &&
               string.Equals(row.EvaluationLabel, assignment.EvaluationLabel, StringComparison.Ordinal) &&
               string.Equals(
                   Normalize(row.EvaluationTemplateId),
                   Normalize(assignment.EvaluationTemplateId),
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.EvaluationTemplateCode,
                   assignment.EvaluationTemplateCode,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.EvaluationTemplateLabel,
                   assignment.EvaluationTemplateLabel,
                   StringComparison.Ordinal) &&
               row.HasManualEvaluations == assignment.HasManualEvaluations &&
               row.EvaluatedAssignmentCount == assignment.EvaluatedAssignmentCount &&
               string.Equals(
                   row.WorstEvaluationCode,
                   assignment.WorstEvaluationCode,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.WorstEvaluationLabel,
                   assignment.WorstEvaluationLabel,
                   StringComparison.Ordinal) &&
               row.AssignmentCreatedAtUtc == assignment.CreatedAtUtc &&
               row.AssignmentUpdatedAtUtc == assignment.UpdatedAtUtc &&
               row.DueAtUtc == assignment.DueAtUtc;
    }

    public static UserRef? ResolveAssignmentUser(
        WorkAssignment assignment,
        string userId)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        return CanonicalUsers(assignment.Assignees)
                   .FirstOrDefault(item =>
                       string.Equals(item.UserId, userId, StringComparison.Ordinal)) ??
               CanonicalUsers(assignment.LeaderWatchers)
                   .FirstOrDefault(item =>
                       string.Equals(item.UserId, userId, StringComparison.Ordinal));
    }

    public static bool MatchesMyReportPeriod(
        MyReportPeriodListDocRole row,
        DynamicFlowRuntimePeriodReadModelSource source)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(source);

        var period = source.Period;
        var report = source.CurrentReport;
        var assignee = ResolveAssignee(source);

        return IsActiveRow(row) &&
               row.DocType == DocType.WORK_REPORT &&
               string.Equals(row.DocId, period.Id, StringComparison.Ordinal) &&
               string.Equals(row.UserId, period.AssigneeUserId, StringComparison.Ordinal) &&
               SameUser(row.User, assignee) &&
               SameRoles(row.Roles, new[] { DocRoleType.ASSIGNEE }) &&
               string.Equals(row.WorkId, period.WorkId, StringComparison.Ordinal) &&
               string.Equals(row.AssignmentId, period.WorkAssignmentId, StringComparison.Ordinal) &&
               string.Equals(
                   row.WorkTemplateAssigneeId,
                   period.WorkTemplateAssigneeId,
                   StringComparison.Ordinal) &&
               string.Equals(row.WorkReportPeriodId, period.Id, StringComparison.Ordinal) &&
               string.Equals(
                   Normalize(row.CurrentReportId),
                   Normalize(report?.Id),
                   StringComparison.Ordinal) &&
               string.Equals(row.AssigneeUserId, period.AssigneeUserId, StringComparison.Ordinal) &&
               string.Equals(row.DynamicExcelId, period.DynamicExcelId, StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicExcelCode,
                   period.DynamicExcelCode ?? string.Empty,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicExcelName,
                   period.DynamicExcelName ?? string.Empty,
                   StringComparison.Ordinal) &&
               string.Equals(
                   Normalize(row.DynamicFormTemplateId),
                   Normalize(period.DynamicFormTemplateId),
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicFormTemplateCode,
                   period.DynamicFormTemplateCode,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicFormTemplateName,
                   period.DynamicFormTemplateName,
                   StringComparison.Ordinal) &&
               string.Equals(
                   Normalize(row.DynamicFormFamilyId),
                   Normalize(period.DynamicFormFamilyId),
                   StringComparison.Ordinal) &&
               row.DynamicFormVersionNo == period.DynamicFormVersionNo &&
               string.Equals(
                   row.DynamicFormSchemaHash,
                   period.DynamicFormSchemaHash,
                   StringComparison.Ordinal) &&
               string.Equals(row.PeriodKey, period.PeriodKey ?? string.Empty, StringComparison.Ordinal) &&
               string.Equals(
                   row.PeriodInstanceKey,
                   string.IsNullOrWhiteSpace(period.PeriodInstanceKey)
                       ? period.PeriodKey ?? string.Empty
                       : period.PeriodInstanceKey,
                   StringComparison.Ordinal) &&
               string.Equals(row.PeriodKind, WorkReportPeriodKind.Scheduled, StringComparison.Ordinal) &&
               string.Equals(row.ReportTitle, period.ReportTitle, StringComparison.Ordinal) &&
               row.ReportDate == period.ReportDate &&
               row.StartedDate == (report?.StartedDate ?? period.StartedDate) &&
               row.CompletedDate == (report?.CompletedDate ?? period.CompletedDate) &&
               row.IsHistoricalData == (report?.IsHistoricalData ?? period.IsHistoricalData) &&
               row.HistoricalDataApproved ==
               (report?.HistoricalDataApproved ?? period.HistoricalDataApproved) &&
               row.HistoricalDataApprovedAtUtc ==
               (report?.HistoricalDataApprovedAtUtc ?? period.HistoricalDataApprovedAtUtc) &&
               string.Equals(
                   Normalize(row.HistoricalDataApprovedByUserId),
                   Normalize(
                       report?.HistoricalDataApprovedByUserId ??
                       period.HistoricalDataApprovedByUserId),
                   StringComparison.Ordinal) &&
               row.PeriodStart == period.PeriodStart &&
               row.PeriodEnd == period.PeriodEnd &&
               row.DueAtUtc == period.DueAtUtc &&
               row.PeriodStatus == period.Status &&
               row.IsOverdue == period.IsOverdue &&
               row.ReportStatus == report?.Status &&
               row.IsCurrentReport == (report?.IsCurrent == true && report.IsActive) &&
               row.ReportIsActive == (report is not null && report.IsActive) &&
               row.ReportDeactivatedAtUtc == report?.DeactivatedAtUtc &&
               string.Equals(
                   row.ReportDeactivationReason,
                   report?.DeactivationReason,
                   StringComparison.Ordinal) &&
               row.IsLateSubmission == (report?.IsLateSubmission ?? false) &&
               row.VersionNo == (report?.VersionNo ?? period.ReportVersionCount) &&
               row.LastSubmittedAtUtc == period.LastSubmittedAtUtc &&
               row.ReturnedAtUtc == report?.ReturnedAtUtc &&
               row.ApprovedAtUtc == report?.ApprovedAtUtc &&
               row.AutoApproved == WorkAssignmentAutoApprovalState.IsAutoApproved(report) &&
               row.AutoApprovedAtUtc == report?.AutoApprovedAtUtc &&
               string.Equals(
                   Normalize(row.AutoApprovedByUserId),
                   Normalize(report?.AutoApprovedByUserId),
                   StringComparison.Ordinal) &&
               row.AutoApprovalLocked == WorkAssignmentAutoApprovalState.IsLocked(report) &&
               row.AutoApprovalConfirmedAtUtc == report?.AutoApprovalConfirmedAtUtc &&
               string.Equals(
                   Normalize(row.AutoApprovalConfirmedByUserId),
                   Normalize(report?.AutoApprovalConfirmedByUserId),
                   StringComparison.Ordinal) &&
               row.SortUpdatedAtUtc == MaxDate(period.UpdatedAtUtc, report?.UpdatedAtUtc) &&
               row.SourceCreatedAtUtc == (report?.CreatedAtUtc ?? period.CreatedAtUtc);
    }

    public static bool MatchesReviewReport(
        ReviewReportListDocRole row,
        DynamicFlowRuntimePeriodReadModelSource source)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(source);

        var period = source.Period;
        var assignment = source.Assignment;
        var report = source.CurrentReport;
        var assignee = ResolveAssignee(source);
        var reviewerUserId = Normalize(assignment.CreatedByUserId);
        if (reviewerUserId is null)
            return false;

        return IsActiveRow(row) &&
               row.DocType == DocType.WORK_REPORT &&
               string.Equals(row.DocId, period.Id, StringComparison.Ordinal) &&
               string.Equals(row.UserId, reviewerUserId, StringComparison.Ordinal) &&
               row.User is null &&
               SameRoles(row.Roles, new[] { DocRoleType.ASSIGNER }) &&
               string.Equals(row.WorkId, period.WorkId, StringComparison.Ordinal) &&
               string.Equals(row.AssignmentId, period.WorkAssignmentId, StringComparison.Ordinal) &&
               string.Equals(row.WorkReportPeriodId, period.Id, StringComparison.Ordinal) &&
               string.Equals(
                   Normalize(row.CurrentReportId),
                   Normalize(report?.Id),
                   StringComparison.Ordinal) &&
               string.Equals(row.ReviewerUserId, reviewerUserId, StringComparison.Ordinal) &&
               string.Equals(row.DynamicExcelId, period.DynamicExcelId, StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicExcelCode,
                   period.DynamicExcelCode ?? string.Empty,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicExcelName,
                   period.DynamicExcelName ?? string.Empty,
                   StringComparison.Ordinal) &&
               string.Equals(
                   Normalize(row.DynamicFormTemplateId),
                   Normalize(period.DynamicFormTemplateId),
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicFormTemplateCode,
                   period.DynamicFormTemplateCode,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicFormTemplateName,
                   period.DynamicFormTemplateName,
                   StringComparison.Ordinal) &&
               string.Equals(
                   Normalize(row.DynamicFormFamilyId),
                   Normalize(period.DynamicFormFamilyId),
                   StringComparison.Ordinal) &&
               row.DynamicFormVersionNo == period.DynamicFormVersionNo &&
               string.Equals(
                   row.DynamicFormSchemaHash,
                   period.DynamicFormSchemaHash,
                   StringComparison.Ordinal) &&
               string.Equals(row.AssigneeUserId, period.AssigneeUserId, StringComparison.Ordinal) &&
               string.Equals(row.AssigneeUserName, assignee?.Username, StringComparison.Ordinal) &&
               string.Equals(row.AssigneeFullName, assignee?.FullName, StringComparison.Ordinal) &&
               string.Equals(
                   Normalize(row.AssigneeUnitId),
                   Normalize(period.AssigneeUnitId ?? assignee?.UnitId),
                   StringComparison.Ordinal) &&
               string.Equals(row.AssigneeUnitName, assignee?.UnitName, StringComparison.Ordinal) &&
               string.Equals(
                   row.AssigneeUnitShortName,
                   assignee?.UnitShortName,
                   StringComparison.Ordinal) &&
               string.Equals(row.PeriodKey, period.PeriodKey ?? string.Empty, StringComparison.Ordinal) &&
               string.Equals(
                   row.PeriodInstanceKey,
                   string.IsNullOrWhiteSpace(period.PeriodInstanceKey)
                       ? period.PeriodKey ?? string.Empty
                       : period.PeriodInstanceKey,
                   StringComparison.Ordinal) &&
               string.Equals(row.PeriodKind, WorkReportPeriodKind.Scheduled, StringComparison.Ordinal) &&
               string.Equals(row.ReportTitle, period.ReportTitle, StringComparison.Ordinal) &&
               row.ReportDate == period.ReportDate &&
               row.StartedDate == (report?.StartedDate ?? period.StartedDate) &&
               row.CompletedDate == (report?.CompletedDate ?? period.CompletedDate) &&
               row.IsHistoricalData == (report?.IsHistoricalData ?? period.IsHistoricalData) &&
               row.HistoricalDataApproved ==
               (report?.HistoricalDataApproved ?? period.HistoricalDataApproved) &&
               row.HistoricalDataApprovedAtUtc ==
               (report?.HistoricalDataApprovedAtUtc ?? period.HistoricalDataApprovedAtUtc) &&
               string.Equals(
                   Normalize(row.HistoricalDataApprovedByUserId),
                   Normalize(
                       report?.HistoricalDataApprovedByUserId ??
                       period.HistoricalDataApprovedByUserId),
                   StringComparison.Ordinal) &&
               row.PeriodStart == period.PeriodStart &&
               row.PeriodEnd == period.PeriodEnd &&
               row.DueAtUtc == period.DueAtUtc &&
               row.PeriodStatus == period.Status &&
               row.IsOverdue == WorkReportPeriodStatusHelper.IsOverdue(period.Status) &&
               row.ReportStatus == report?.Status &&
               row.PayloadRevision == (report?.PayloadRevision ?? 0) &&
               row.LifecycleRevision == (report?.LifecycleRevision ?? 0) &&
               row.ReportIsActive == (report is not null && report.IsActive) &&
               row.ReportDeactivatedAtUtc == report?.DeactivatedAtUtc &&
               string.Equals(
                   row.ReportDeactivationReason,
                   report?.DeactivationReason,
                   StringComparison.Ordinal) &&
               row.SubmittedAtUtc == (report?.SubmittedAtUtc ?? period.LastSubmittedAtUtc) &&
               row.ApprovedAtUtc == report?.ApprovedAtUtc &&
               row.AutoApproved == WorkAssignmentAutoApprovalState.IsAutoApproved(report) &&
               row.AutoApprovedAtUtc == report?.AutoApprovedAtUtc &&
               string.Equals(
                   Normalize(row.AutoApprovedByUserId),
                   Normalize(report?.AutoApprovedByUserId),
                   StringComparison.Ordinal) &&
               row.AutoApprovalLocked == WorkAssignmentAutoApprovalState.IsLocked(report) &&
               row.AutoApprovalConfirmedAtUtc == report?.AutoApprovalConfirmedAtUtc &&
               string.Equals(
                   Normalize(row.AutoApprovalConfirmedByUserId),
                   Normalize(report?.AutoApprovalConfirmedByUserId),
                   StringComparison.Ordinal) &&
               row.ReturnedAtUtc == report?.ReturnedAtUtc &&
               string.Equals(
                   row.ReturnReason,
                   report?.ReturnReason ?? period.ReturnReason,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.ReviewerComment,
                   report?.ReviewerComment ?? period.ReviewerComment,
                   StringComparison.Ordinal) &&
               row.ProgressStatus == assignment.ProgressStatus &&
               row.ProgressStatusUpdatedAtUtc == assignment.ProgressStatusUpdatedAtUtc &&
               row.HasAnyDuePeriod == assignment.HasAnyDuePeriod &&
               row.HasOverduePeriod == assignment.HasOverduePeriod &&
               row.WorstPeriodStatus == assignment.WorstPeriodStatus &&
               string.Equals(
                   row.WorstOverdueReasonCode,
                   assignment.WorstOverdueReasonCode,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.WorstOverdueReasonLabel,
                   assignment.WorstOverdueReasonLabel,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.ReviewStatusBucket,
                   WorkReportPeriodStatusHelper.ToReviewStatusBucket(
                       period.Status,
                       period.ReturnReason,
                       report?.ReturnReason,
                       report?.ReturnedAtUtc),
                   StringComparison.Ordinal) &&
               row.WaitingReview == WorkReportPeriodStatusHelper.IsWaitingReview(period.Status) &&
               row.Returned == WorkReportPeriodStatusHelper.IsReturned(
                   period.Status,
                   period.ReturnReason,
                   report?.ReturnReason,
                   report?.ReturnedAtUtc) &&
               row.ReviewRank == WorkReportPeriodStatusHelper.GetReviewRank(period.Status) &&
               row.SortDueAtUtc == period.DueAtUtc &&
               row.SortUpdatedAtUtc == MaxDate(period.UpdatedAtUtc, report?.UpdatedAtUtc);
    }

    public static bool MatchesMyReportTemplate(
        MyReportTemplateListDocRole row,
        IReadOnlyCollection<DynamicFlowRuntimePeriodReadModelSource> sources)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(sources);

        var templateId = Normalize(row.DynamicFormTemplateId);
        if (templateId is null)
            return false;
        var candidates = sources
            .Where(source =>
                string.Equals(source.Period.WorkId, row.WorkId, StringComparison.Ordinal) &&
                string.Equals(
                    Normalize(source.Period.DynamicFormTemplateId),
                    templateId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    source.Period.AssigneeUserId,
                    row.UserId,
                    StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0)
            return false;
        var latest = candidates
            .OrderByDescending(source =>
                source.Period.DueAtUtc ??
                MaxDate(source.Period.UpdatedAtUtc, source.CurrentReport?.UpdatedAtUtc))
            .ThenByDescending(source =>
                MaxDate(source.Period.UpdatedAtUtc, source.CurrentReport?.UpdatedAtUtc))
            .First();
        var expectedUser = candidates
            .Select(ResolveAssignee)
            .FirstOrDefault(user => user is not null);
        var latestUpdatedAtUtc =
            MaxDate(latest.Period.UpdatedAtUtc, latest.CurrentReport?.UpdatedAtUtc);

        return IsActiveRow(row) &&
               row.DocType == DocType.WORK_REPORT &&
               string.Equals(row.DocId, templateId, StringComparison.Ordinal) &&
               SameUser(row.User, expectedUser) &&
               SameRoles(row.Roles, new[] { DocRoleType.ASSIGNEE }) &&
               string.Equals(row.WorkId, latest.Period.WorkId, StringComparison.Ordinal) &&
               string.Equals(
                   Normalize(row.DynamicExcelId),
                   Normalize(latest.Period.DynamicExcelId),
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicExcelCode,
                   latest.Period.DynamicExcelCode ?? string.Empty,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicExcelName,
                   latest.Period.DynamicExcelName ?? string.Empty,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicFormTemplateCode,
                   latest.Period.DynamicFormTemplateCode,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicFormTemplateName,
                   latest.Period.DynamicFormTemplateName,
                   StringComparison.Ordinal) &&
               row.BindingCount == candidates
                   .Select(source => source.Period.WorkTemplateAssigneeId)
                   .Distinct(StringComparer.Ordinal)
                   .Count() &&
               row.PeriodCount == candidates.Length &&
               row.ReportCount == candidates.Count(source =>
                   Normalize(source.CurrentReport?.Id) is not null) &&
               string.Equals(row.LatestPeriodId, latest.Period.Id, StringComparison.Ordinal) &&
               string.Equals(
                   row.LatestPeriodKey,
                   latest.Period.PeriodKey,
                   StringComparison.Ordinal) &&
               row.LatestPeriodStatus == latest.Period.Status &&
               row.LatestDueAtUtc == latest.Period.DueAtUtc &&
               string.Equals(
                   Normalize(row.LatestReportId),
                   Normalize(latest.CurrentReport?.Id),
                   StringComparison.Ordinal) &&
               row.LatestUpdatedAtUtc == latestUpdatedAtUtc &&
               row.HasOverduePeriod == candidates.Any(source => source.Period.IsOverdue);
    }

    public static bool MatchesReviewAssignmentSummary(
        ReviewAssignmentSummaryDocRole row,
        IReadOnlyCollection<DynamicFlowRuntimePeriodReadModelSource> sources)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(sources);

        var candidates = sources
            .Where(source =>
                string.Equals(
                    source.Period.WorkAssignmentId,
                    row.AssignmentId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    Normalize(source.Assignment.CreatedByUserId),
                    row.ReviewerUserId,
                    StringComparison.Ordinal))
            .Select(ToReviewSource)
            .ToArray();
        if (candidates.Length == 0)
            return false;

        var latest = candidates
            .OrderByDescending(source => source.DueAtUtc ?? source.SortUpdatedAtUtc)
            .ThenByDescending(source => source.SortUpdatedAtUtc)
            .First();
        var worst = candidates
            .OrderByDescending(source => source.ReviewRank)
            .ThenByDescending(source => source.DueAtUtc ?? source.SortUpdatedAtUtc)
            .ThenByDescending(source => source.SortUpdatedAtUtc)
            .First();
        var assignees = candidates
            .Where(source => source.Assignee is not null)
            .Select(source => source.Assignee!)
            .GroupBy(user => user.UserId, StringComparer.Ordinal)
            .Select(group =>
            {
                var user = group.First();
                return new UserRef
                {
                    UserId = user.UserId,
                    Username = user.Username,
                    FullName = user.FullName,
                    UnitId = user.UnitId,
                    UnitName = user.UnitName,
                    UnitShortName = user.UnitShortName
                };
            })
            .ToArray();
        var firstAssignee = assignees
            .OrderBy(user => user.UnitShortName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(user => user.FullName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(user => user.Username ?? string.Empty, StringComparer.Ordinal)
            .FirstOrDefault();
        var periodKeys = candidates
            .Select(source => source.PeriodKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var reviewBuckets = candidates
            .Select(source => source.ReviewStatusBucket)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return IsActiveRow(row) &&
               row.DocType == DocType.WORK_ASSIGNMENT &&
               string.Equals(row.DocId, row.AssignmentId, StringComparison.Ordinal) &&
               string.Equals(row.UserId, row.ReviewerUserId, StringComparison.Ordinal) &&
               row.User is null &&
               SameRoles(row.Roles, new[] { DocRoleType.ASSIGNER }) &&
               string.Equals(row.WorkId, latest.WorkId, StringComparison.Ordinal) &&
               string.Equals(row.DynamicExcelId, latest.DynamicExcelId, StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicExcelCode,
                   latest.DynamicExcelCode,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicExcelName,
                   latest.DynamicExcelName,
                   StringComparison.Ordinal) &&
               string.Equals(
                   Normalize(row.DynamicFormTemplateId),
                   Normalize(latest.DynamicFormTemplateId),
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicFormTemplateCode,
                   latest.DynamicFormTemplateCode,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.DynamicFormTemplateName,
                   latest.DynamicFormTemplateName,
                   StringComparison.Ordinal) &&
               SameUsersUnordered(row.Assignees, assignees) &&
               SameStringsUnordered(
                   row.AssigneeUserIds,
                   assignees.Select(user => user.UserId)) &&
               SameStringsUnordered(
                   row.AssigneeUnitIds,
                   assignees
                       .Select(user => user.UnitId)
                       .Where(value => !string.IsNullOrWhiteSpace(value))
                       .Select(value => value!)) &&
               string.Equals(
                   row.FirstAssigneeUserName,
                   firstAssignee?.Username,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.FirstAssigneeFullName,
                   firstAssignee?.FullName,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.FirstAssigneeUnitShortName,
                   firstAssignee?.UnitShortName,
                   StringComparison.Ordinal) &&
               row.ProgressStatus == latest.ProgressStatus &&
               row.ProgressStatusUpdatedAtUtc == latest.ProgressStatusUpdatedAtUtc &&
               row.PeriodCount == candidates.Length &&
               SameStringsUnordered(row.PeriodKeys, periodKeys) &&
               row.ReportCount == candidates.Count(source =>
                   Normalize(source.CurrentReportId) is not null) &&
               string.Equals(row.LatestPeriodId, latest.PeriodId, StringComparison.Ordinal) &&
               string.Equals(row.LatestPeriodKey, latest.PeriodKey, StringComparison.Ordinal) &&
               row.LatestPeriodStatus == latest.PeriodStatus &&
               row.LatestDueAtUtc == latest.DueAtUtc &&
               string.Equals(
                   Normalize(row.LatestReportId),
                   Normalize(latest.CurrentReportId),
                   StringComparison.Ordinal) &&
               row.LatestUpdatedAtUtc == latest.SortUpdatedAtUtc &&
               row.HasAnyDuePeriod &&
               row.HasOverduePeriod == candidates.Any(source => source.IsOverdue) &&
               row.OverdueCount == candidates.Count(source => source.IsOverdue) &&
               row.WaitingReviewCount == candidates.Count(source => source.WaitingReview) &&
               row.ReturnedCount == candidates.Count(source => source.Returned) &&
               SameStringsUnordered(
                   row.ReviewStatusBuckets,
                   reviewBuckets,
                   StringComparer.OrdinalIgnoreCase) &&
               string.Equals(
                   row.WorstReviewStatusBucket,
                   worst.ReviewStatusBucket,
                   StringComparison.Ordinal) &&
               row.WorstReviewRank == worst.ReviewRank &&
               row.WorstPeriodStatus == worst.PeriodStatus &&
               string.Equals(
                   row.WorstOverdueReasonCode,
                   worst.WorstOverdueReasonCode,
                   StringComparison.Ordinal) &&
               string.Equals(
                   row.WorstOverdueReasonLabel,
                   worst.WorstOverdueReasonLabel,
                   StringComparison.Ordinal) &&
               row.EvaluationCode is null &&
               row.EvaluationLabel is null &&
               row.SortHasOverduePeriod == candidates.Any(source => source.IsOverdue) &&
               row.SortLatestDueAtUtc == latest.DueAtUtc &&
               row.SortUpdatedAtUtc == candidates.Max(source => source.SortUpdatedAtUtc);
    }

    private static ReviewSource ToReviewSource(
        DynamicFlowRuntimePeriodReadModelSource source)
    {
        var period = source.Period;
        var assignment = source.Assignment;
        var report = source.CurrentReport;
        return new ReviewSource(
            WorkId: period.WorkId,
            PeriodId: period.Id,
            CurrentReportId: Normalize(report?.Id),
            DynamicExcelId: period.DynamicExcelId,
            DynamicExcelCode: period.DynamicExcelCode ?? string.Empty,
            DynamicExcelName: period.DynamicExcelName ?? string.Empty,
            DynamicFormTemplateId: Normalize(period.DynamicFormTemplateId),
            DynamicFormTemplateCode: period.DynamicFormTemplateCode,
            DynamicFormTemplateName: period.DynamicFormTemplateName,
            Assignee: ResolveAssignee(source),
            ProgressStatus: assignment.ProgressStatus,
            ProgressStatusUpdatedAtUtc: assignment.ProgressStatusUpdatedAtUtc,
            PeriodKey: period.PeriodKey ?? string.Empty,
            PeriodStatus: period.Status,
            DueAtUtc: period.DueAtUtc,
            IsOverdue: WorkReportPeriodStatusHelper.IsOverdue(period.Status),
            WaitingReview: WorkReportPeriodStatusHelper.IsWaitingReview(period.Status),
            Returned: WorkReportPeriodStatusHelper.IsReturned(
                period.Status,
                period.ReturnReason,
                report?.ReturnReason,
                report?.ReturnedAtUtc),
            ReviewStatusBucket: WorkReportPeriodStatusHelper.ToReviewStatusBucket(
                period.Status,
                period.ReturnReason,
                report?.ReturnReason,
                report?.ReturnedAtUtc),
            ReviewRank: WorkReportPeriodStatusHelper.GetReviewRank(period.Status),
            WorstOverdueReasonCode: assignment.WorstOverdueReasonCode,
            WorstOverdueReasonLabel: assignment.WorstOverdueReasonLabel,
            SortUpdatedAtUtc: MaxDate(period.UpdatedAtUtc, report?.UpdatedAtUtc));
    }

    private static UserRef? ResolveAssignee(
        DynamicFlowRuntimePeriodReadModelSource source)
    {
        var fromAssignment = CanonicalUsers(source.Assignment.Assignees)
            .FirstOrDefault(user =>
                string.Equals(
                    user.UserId,
                    source.Period.AssigneeUserId,
                    StringComparison.Ordinal));
        if (fromAssignment is not null)
            return fromAssignment;

        var binding = source.Binding;
        if (binding is null || string.IsNullOrWhiteSpace(binding.AssigneeUserId))
            return null;
        return new UserRef
        {
            UserId = binding.AssigneeUserId,
            Username = binding.AssigneeUsername,
            FullName = binding.AssigneeFullName,
            UnitId = binding.AssigneeUnitId,
            UnitSymbol = binding.AssigneeUnitSymbol,
            UnitShortName = binding.AssigneeUnitShortName,
            UnitName = binding.AssigneeUnitName
        };
    }

    private static bool IsActiveRow(DocRoleReadModelBase row)
        => !row.IsDeleted &&
           row.DeletedAtUtc is null &&
           string.IsNullOrWhiteSpace(row.DeletedByUserId);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime MaxDate(DateTime left, DateTime? right)
        => right.HasValue && right.Value > left ? right.Value : left;

    private static string ResolveAssignmentName(WorkAssignment assignment)
    {
        var name = assignment.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        return assignment.DynamicFormTemplateName?.Trim()
               ?? assignment.DynamicExcelName?.Trim()
               ?? assignment.Code?.Trim()
               ?? assignment.Id
               ?? string.Empty;
    }

    private static UserRef[] CanonicalUsers(IEnumerable<UserRef>? users)
        => (users ?? Enumerable.Empty<UserRef>())
            .Where(user => !string.IsNullOrWhiteSpace(user.UserId))
            .ToArray();

    private static bool SameRoles(
        IEnumerable<DocRoleType>? actual,
        IEnumerable<DocRoleType> expected)
        => (actual ?? Enumerable.Empty<DocRoleType>())
            .OrderBy(role => (int)role)
            .SequenceEqual(expected.OrderBy(role => (int)role));

    private static bool SameStrings(
        IEnumerable<string>? actual,
        IEnumerable<string>? expected)
        => (actual ?? Enumerable.Empty<string>())
            .SequenceEqual(expected ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

    private static bool SameStringsUnordered(
        IEnumerable<string>? actual,
        IEnumerable<string>? expected,
        IEqualityComparer<string>? comparer = null)
    {
        comparer ??= StringComparer.Ordinal;
        var actualValues = (actual ?? Enumerable.Empty<string>()).ToArray();
        var expectedValues = (expected ?? Enumerable.Empty<string>()).ToArray();
        return actualValues.Length == expectedValues.Length &&
               actualValues.Distinct(comparer).Count() == actualValues.Length &&
               expectedValues.Distinct(comparer).Count() == expectedValues.Length &&
               actualValues.ToHashSet(comparer).SetEquals(expectedValues);
    }

    private static bool SameUsers(
        IEnumerable<UserRef>? actual,
        IEnumerable<UserRef>? expected)
    {
        var actualValues = (actual ?? Enumerable.Empty<UserRef>()).ToArray();
        var expectedValues = (expected ?? Enumerable.Empty<UserRef>()).ToArray();
        return actualValues.Length == expectedValues.Length &&
               actualValues.Zip(expectedValues).All(pair =>
                   SameUser(pair.First, pair.Second));
    }

    private static bool SameUsersUnordered(
        IEnumerable<UserRef>? actual,
        IEnumerable<UserRef>? expected)
    {
        var actualValues = (actual ?? Enumerable.Empty<UserRef>())
            .OrderBy(user => user.UserId, StringComparer.Ordinal)
            .ToArray();
        var expectedValues = (expected ?? Enumerable.Empty<UserRef>())
            .OrderBy(user => user.UserId, StringComparer.Ordinal)
            .ToArray();
        return SameUsers(actualValues, expectedValues);
    }

    private static bool SameUser(UserRef? actual, UserRef? expected)
    {
        if (actual is null || expected is null)
            return actual is null && expected is null;
        return string.Equals(actual.UserId, expected.UserId, StringComparison.Ordinal) &&
               string.Equals(actual.Username, expected.Username, StringComparison.Ordinal) &&
               string.Equals(actual.FullName, expected.FullName, StringComparison.Ordinal) &&
               string.Equals(actual.UnitId, expected.UnitId, StringComparison.Ordinal) &&
               string.Equals(actual.UnitSymbol, expected.UnitSymbol, StringComparison.Ordinal) &&
               string.Equals(actual.UnitShortName, expected.UnitShortName, StringComparison.Ordinal) &&
               string.Equals(actual.UnitName, expected.UnitName, StringComparison.Ordinal) &&
               string.Equals(actual.PositionCode, expected.PositionCode, StringComparison.Ordinal) &&
               string.Equals(actual.PositionName, expected.PositionName, StringComparison.Ordinal);
    }

    private sealed record ReviewSource(
        string WorkId,
        string PeriodId,
        string? CurrentReportId,
        string? DynamicExcelId,
        string DynamicExcelCode,
        string DynamicExcelName,
        string? DynamicFormTemplateId,
        string? DynamicFormTemplateCode,
        string? DynamicFormTemplateName,
        UserRef? Assignee,
        int ProgressStatus,
        DateTime? ProgressStatusUpdatedAtUtc,
        string PeriodKey,
        WorkReportPeriodStatus PeriodStatus,
        DateTime? DueAtUtc,
        bool IsOverdue,
        bool WaitingReview,
        bool Returned,
        string ReviewStatusBucket,
        int ReviewRank,
        string? WorstOverdueReasonCode,
        string? WorstOverdueReasonLabel,
        DateTime SortUpdatedAtUtc);
}
