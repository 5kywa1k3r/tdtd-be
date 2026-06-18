using MongoDB.Driver;
using MongoDB.Bson;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.WorkAssignments.Domain;

namespace tdtd_be.Services.Common;

public interface IDocRoleReadModelProjectionService
{
    Task RebuildWorkAsync(string workId, string byUserId, CancellationToken ct);
    Task RebuildAssignmentAsync(string assignmentId, string byUserId, CancellationToken ct);
    Task RebuildWorkAssignmentsAsync(string workId, string byUserId, CancellationToken ct);
    Task RebuildReportPeriodAsync(string workReportPeriodId, string byUserId, CancellationToken ct);
    Task RebuildWorkReportPeriodsAsync(string workId, string byUserId, CancellationToken ct);
    Task RebuildMyReportTemplateAsync(string workId, string dynamicFormTemplateId, string userId, string byUserId, CancellationToken ct);
    Task SoftDeleteByDocAsync(DocType docType, string docId, string byUserId, CancellationToken ct);
}

public sealed class DocRoleReadModelProjectionService : IDocRoleReadModelProjectionService
{
    private readonly MongoDbContext _ctx;

    public DocRoleReadModelProjectionService(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task RebuildWorkAsync(string workId, string byUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workId))
            return;

        var work = await _ctx.Works
            .Find(x => x.Id == workId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (work is null)
        {
            await SoftDeleteByDocAsync(DocType.WORK, workId, byUserId, ct);
            return;
        }

        var now = DateTime.UtcNow;
        var roleMap = await BuildWorkRoleMapAsync(work, ct);
        var activeUserIds = roleMap.Keys.ToList();

        if (roleMap.Count > 0)
        {
            var writes = roleMap.Select(kvp =>
            {
                var userId = kvp.Key;
                var seed = kvp.Value;

                var filter = Builders<WorkListDocRole>.Filter.Eq(x => x.UserId, userId)
                             & Builders<WorkListDocRole>.Filter.Eq(x => x.DocId, work.Id);

                var update = Builders<WorkListDocRole>.Update
                    .Set(x => x.DocType, DocType.WORK)
                    .Set(x => x.DocId, work.Id)
                    .Set(x => x.WorkId, work.Id)
                    .Set(x => x.UserId, userId)
                    .Set(x => x.User, seed.User)
                    .Set(x => x.Roles, seed.Roles.OrderBy(x => (int)x).ToList())
                    .Set(x => x.AutoCode, work.AutoCode ?? string.Empty)
                    .Set(x => x.Code, work.Code)
                    .Set(x => x.Name, work.Name ?? string.Empty)
                    .Set(x => x.Type, work.Type)
                    .Set(x => x.Status, work.Status)
                    .Set(x => x.Priority, work.Priority)
                    .Set(x => x.WorkCreatedByUserId, work.CreatedByUserId)
                    .Set(x => x.OwnerName, work.Owner?.FullName)
                    .Set(x => x.LeaderDirectiveUserId, NullIfWhiteSpace(work.LeaderDirectiveUserId))
                    .Set(x => x.LeaderWatchCount, CountDistinct(work.LeaderWatchUserIds))
                    .Set(x => x.EvaluationTemplateId, NullIfWhiteSpace(work.EvaluationTemplateId))
                    .Set(x => x.EvaluationTemplateCode, work.EvaluationTemplateCode)
                    .Set(x => x.EvaluationTemplateLabel, work.EvaluationTemplateLabel)
                    .Set(x => x.HasManualEvaluations, work.HasManualEvaluations)
                    .Set(x => x.EvaluatedAssignmentCount, work.EvaluatedAssignmentCount)
                    .Set(x => x.WorstEvaluationCode, work.WorstEvaluationCode)
                    .Set(x => x.WorstEvaluationLabel, work.WorstEvaluationLabel)
                    .Set(x => x.DueDate, work.DueDate)
                    .Set(x => x.CompletedDate, work.CompletedDate)
                    .Set(x => x.CompletedAtUtc, work.CompletedAtUtc)
                    .Set(x => x.CompletedByUserId, NullIfWhiteSpace(work.CompletedByUserId))
                    .Set(x => x.WorkCreatedAtUtc, work.CreatedAtUtc)
                    .Set(x => x.IsDeleted, false)
                    .Set(x => x.DeletedAtUtc, (DateTime?)null)
                    .Set(x => x.DeletedByUserId, null)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, NormalizeAuditUserId(byUserId))
                    .SetOnInsert(x => x.CreatedAtUtc, now)
                    .SetOnInsert(x => x.CreatedByUserId, NormalizeAuditUserId(byUserId));

                return new UpdateOneModel<WorkListDocRole>(filter, update) { IsUpsert = true };
            }).ToList();

            await _ctx.WorkListDocRoles.BulkWriteAsync(writes, cancellationToken: ct);
        }

        var staleFilter = Builders<WorkListDocRole>.Filter.Eq(x => x.WorkId, work.Id)
                          & Builders<WorkListDocRole>.Filter.Eq(x => x.IsDeleted, false);

        if (activeUserIds.Count > 0)
            staleFilter &= Builders<WorkListDocRole>.Filter.Nin(x => x.UserId, activeUserIds);

        await _ctx.WorkListDocRoles.UpdateManyAsync(
            staleFilter,
            BuildSoftDeleteUpdate<WorkListDocRole>(byUserId, now),
            cancellationToken: ct);
    }

    public async Task RebuildAssignmentAsync(string assignmentId, string byUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(assignmentId))
            return;

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (assignment is null)
        {
            await SoftDeleteByDocAsync(DocType.WORK_ASSIGNMENT, assignmentId, byUserId, ct);
            return;
        }

        var now = DateTime.UtcNow;
        var roleMap = await BuildAssignmentRoleMapAsync(assignment, ct);
        var activeUserIds = roleMap.Keys.ToList();

        if (roleMap.Count > 0)
        {
            var assigneeUserIds = (assignment.Assignees ?? new List<UserRef>())
                .Select(x => x.UserId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var assigneeUnitIds = (assignment.Assignees ?? new List<UserRef>())
                .Select(x => x.UnitId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var leaderWatcherIds = (assignment.LeaderWatcherUserIds ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var firstAssignee = (assignment.Assignees ?? new List<UserRef>())
                .Where(x => !string.IsNullOrWhiteSpace(x.UserId))
                .OrderBy(x => x.UnitShortName ?? string.Empty)
                .ThenBy(x => x.FullName ?? string.Empty)
                .FirstOrDefault();

            var writes = roleMap.Select(kvp =>
            {
                var userId = kvp.Key;
                var seed = kvp.Value;

                var filter = Builders<AssignmentListDocRole>.Filter.Eq(x => x.UserId, userId)
                             & Builders<AssignmentListDocRole>.Filter.Eq(x => x.DocId, assignment.Id);

                var update = Builders<AssignmentListDocRole>.Update
                    .Set(x => x.DocType, DocType.WORK_ASSIGNMENT)
                    .Set(x => x.DocId, assignment.Id)
                    .Set(x => x.AssignmentId, assignment.Id)
                    .Set(x => x.WorkId, assignment.WorkId)
                    .Set(x => x.UserId, userId)
                    .Set(x => x.User, seed.User)
                    .Set(x => x.Roles, seed.Roles.OrderBy(x => (int)x).ToList())
                    .Set(x => x.ParentAssignmentId, NullIfWhiteSpace(assignment.ParentAssignmentId))
                    .Set(x => x.RootAssignmentId, NullIfWhiteSpace(assignment.RootAssignmentId))
                    .Set(x => x.Path, assignment.Path ?? string.Empty)
                    .Set(x => x.Level, assignment.Level)
                    .Set(x => x.Code, assignment.Code ?? string.Empty)
                    .Set(x => x.Name, ResolveAssignmentName(assignment))
                    .Set(x => x.DynamicExcelId, assignment.DynamicExcelId)
                    .Set(x => x.DynamicExcelCode, assignment.DynamicExcelCode ?? string.Empty)
                    .Set(x => x.DynamicExcelName, assignment.DynamicExcelName ?? string.Empty)
                    .Set(x => x.DynamicFormTemplateId, NullIfWhiteSpace(assignment.DynamicFormTemplateId))
                    .Set(x => x.DynamicFormTemplateCode, assignment.DynamicFormTemplateCode)
                    .Set(x => x.DynamicFormTemplateName, assignment.DynamicFormTemplateName)
                    .Set(x => x.DynamicFormDataSourceRulesJson, assignment.DynamicFormDataSourceRulesJson)
                    .Set(x => x.AutoApproveConditionJson, assignment.AutoApproveConditionJson)
                    .Set(x => x.AssignmentType, assignment.AssignmentType ?? string.Empty)
                    .Set(x => x.AggregationType, assignment.AggregationType ?? string.Empty)
                    .Set(x => x.Assignees, CloneUserRefs(assignment.Assignees))
                    .Set(x => x.LeaderWatchers, CloneUserRefs(assignment.LeaderWatchers))
                    .Set(x => x.Description, assignment.Description)
                    .Set(x => x.IsActive, assignment.IsActive)
                    .Set(x => x.StartDate, assignment.StartDate)
                    .Set(x => x.DueDate, assignment.DueDate)
                    .Set(x => x.CompletedDate, assignment.CompletedDate)
                    .Set(x => x.CompletedAtUtc, assignment.CompletedAtUtc)
                    .Set(x => x.CompletedByUserId, NullIfWhiteSpace(assignment.CompletedByUserId))
                    .Set(x => x.AssignmentCreatedByUserId, assignment.CreatedByUserId)
                    .Set(x => x.AssigneeUserIds, assigneeUserIds)
                    .Set(x => x.AssigneeUnitIds, assigneeUnitIds)
                    .Set(x => x.FirstAssigneeName, firstAssignee?.FullName)
                    .Set(x => x.FirstAssigneeUnitName, firstAssignee?.UnitName)
                    .Set(x => x.ProgressStatus, assignment.ProgressStatus)
                    .Set(x => x.ProgressStatusUpdatedAtUtc, assignment.ProgressStatusUpdatedAtUtc)
                    .Set(x => x.LatestPeriodKey, assignment.LatestPeriodKey)
                    .Set(x => x.LatestDueAtUtc, assignment.LatestDueAtUtc)
                    .Set(x => x.HasAnyDuePeriod, assignment.HasAnyDuePeriod)
                    .Set(x => x.HasOverduePeriod, assignment.HasOverduePeriod)
                    .Set(x => x.WorstPeriodStatus, assignment.WorstPeriodStatus)
                    .Set(x => x.WorstOverdueReasonCode, assignment.WorstOverdueReasonCode)
                    .Set(x => x.WorstOverdueReasonLabel, assignment.WorstOverdueReasonLabel)
                    .Set(x => x.EvaluationCode, assignment.EvaluationCode)
                    .Set(x => x.EvaluationLabel, assignment.EvaluationLabel)
                    .Set(x => x.EvaluationTemplateId, NullIfWhiteSpace(assignment.EvaluationTemplateId))
                    .Set(x => x.EvaluationTemplateCode, assignment.EvaluationTemplateCode)
                    .Set(x => x.EvaluationTemplateLabel, assignment.EvaluationTemplateLabel)
                    .Set(x => x.HasManualEvaluations, assignment.HasManualEvaluations)
                    .Set(x => x.EvaluatedAssignmentCount, assignment.EvaluatedAssignmentCount)
                    .Set(x => x.WorstEvaluationCode, assignment.WorstEvaluationCode)
                    .Set(x => x.WorstEvaluationLabel, assignment.WorstEvaluationLabel)
                    .Set(x => x.AssignmentCreatedAtUtc, assignment.CreatedAtUtc)
                    .Set(x => x.AssignmentUpdatedAtUtc, assignment.UpdatedAtUtc)
                    .Set(x => x.DueAtUtc, assignment.DueAtUtc)
                    .Set(x => x.IsDeleted, false)
                    .Set(x => x.DeletedAtUtc, (DateTime?)null)
                    .Set(x => x.DeletedByUserId, null)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, NormalizeAuditUserId(byUserId))
                    .SetOnInsert(x => x.CreatedAtUtc, now)
                    .SetOnInsert(x => x.CreatedByUserId, NormalizeAuditUserId(byUserId));

                return new UpdateOneModel<AssignmentListDocRole>(filter, update) { IsUpsert = true };
            }).ToList();

            await _ctx.AssignmentListDocRoles.BulkWriteAsync(writes, cancellationToken: ct);
        }

        var staleFilter = Builders<AssignmentListDocRole>.Filter.Eq(x => x.AssignmentId, assignment.Id)
                          & Builders<AssignmentListDocRole>.Filter.Eq(x => x.IsDeleted, false);

        if (activeUserIds.Count > 0)
            staleFilter &= Builders<AssignmentListDocRole>.Filter.Nin(x => x.UserId, activeUserIds);

        await _ctx.AssignmentListDocRoles.UpdateManyAsync(
            staleFilter,
            BuildSoftDeleteUpdate<AssignmentListDocRole>(byUserId, now),
            cancellationToken: ct);
    }

    public async Task RebuildWorkAssignmentsAsync(string workId, string byUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workId))
            return;

        var assignmentIds = await _ctx.WorkAssignments
            .Find(x => x.WorkId == workId && !x.IsDeleted)
            .Project(x => x.Id)
            .ToListAsync(ct);

        foreach (var assignmentId in assignmentIds.Where(x => !string.IsNullOrWhiteSpace(x)))
            await RebuildAssignmentAsync(assignmentId, byUserId, ct);
    }

    public async Task RebuildReportPeriodAsync(string workReportPeriodId, string byUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workReportPeriodId))
            return;

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == workReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (period is null || !period.IsActive)
        {
            await SoftDeleteReportReadModelsByPeriodAsync(workReportPeriodId, byUserId, ct);
            return;
        }

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == period.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (assignment is null)
        {
            await SoftDeleteReportReadModelsByPeriodAsync(workReportPeriodId, byUserId, ct);
            return;
        }

        var report = await LoadCurrentReportAsync(period, ct);
        var binding = await _ctx.WorkTemplateAssignees
            .Find(x => x.Id == period.WorkTemplateAssigneeId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        var now = DateTime.UtcNow;
        var assignee = ResolveAssignee(period, assignment, binding);

        await UpsertMyReportListDocRolesAsync(period, report, assignee, byUserId, now, ct);
        await UpsertReviewReportListDocRolesAsync(period, report, assignment, binding, assignee, byUserId, now, ct);
    }

    public async Task RebuildWorkReportPeriodsAsync(string workId, string byUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workId))
            return;

        var periodIds = await _ctx.WorkReportPeriods
            .Find(x => x.WorkId == workId && !x.IsDeleted)
            .Project(x => x.Id)
            .ToListAsync(ct);

        foreach (var periodId in periodIds.Where(x => !string.IsNullOrWhiteSpace(x)))
            await RebuildReportPeriodAsync(periodId, byUserId, ct);
    }

    public async Task RebuildMyReportTemplateAsync(
        string workId,
        string dynamicFormTemplateId,
        string userId,
        string byUserId,
        CancellationToken ct)
    {
        await RebuildMyReportTemplateListDocRoleAsync(
            workId,
            dynamicFormTemplateId,
            userId,
            null,
            byUserId,
            DateTime.UtcNow,
            ct);
    }

    public async Task SoftDeleteByDocAsync(DocType docType, string docId, string byUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(docId))
            return;

        var now = DateTime.UtcNow;

        switch (docType)
        {
            case DocType.WORK:
                await _ctx.WorkListDocRoles.UpdateManyAsync(
                    Builders<WorkListDocRole>.Filter.Eq(x => x.WorkId, docId)
                    & Builders<WorkListDocRole>.Filter.Eq(x => x.IsDeleted, false),
                    BuildSoftDeleteUpdate<WorkListDocRole>(byUserId, now),
                    cancellationToken: ct);
                break;

            case DocType.WORK_ASSIGNMENT:
                await _ctx.AssignmentListDocRoles.UpdateManyAsync(
                    Builders<AssignmentListDocRole>.Filter.Eq(x => x.AssignmentId, docId)
                    & Builders<AssignmentListDocRole>.Filter.Eq(x => x.IsDeleted, false),
                    BuildSoftDeleteUpdate<AssignmentListDocRole>(byUserId, now),
                    cancellationToken: ct);
                break;

            case DocType.WORK_REPORT:
                await SoftDeleteReportReadModelsByPeriodAsync(docId, byUserId, ct);
                break;
        }
    }

    private async Task<Dictionary<string, RoleSeed>> BuildWorkRoleMapAsync(Work work, CancellationToken ct)
    {
        var map = new Dictionary<string, RoleSeed>(StringComparer.Ordinal);

        var roles = await _ctx.DocRoles
            .Find(x => x.DocType == DocType.WORK && x.DocId == work.Id && !x.IsDeleted)
            .ToListAsync(ct);

        foreach (var role in roles)
            AddRole(map, role.UserId, role.Role, role.User);

        AddRole(map, work.CreatedByUserId, DocRoleType.OWNER, work.Owner);
        AddRole(map, work.LeaderDirectiveUserId, DocRoleType.LEADER_DIRECTIVE, work.LeaderDirective);

        var watchRefs = (work.LeaderWatch ?? new List<UserRef>())
            .Where(x => !string.IsNullOrWhiteSpace(x.UserId))
            .ToDictionary(x => x.UserId, x => x, StringComparer.Ordinal);

        foreach (var watcherId in (work.LeaderWatchUserIds ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)))
            AddRole(map, watcherId, DocRoleType.LEADER_WATCH, watchRefs.GetValueOrDefault(watcherId));

        var activeAssignments = await _ctx.WorkAssignments
            .Find(x => x.WorkId == work.Id && x.IsActive && !x.IsDeleted)
            .Project(x => x.Assignees)
            .ToListAsync(ct);

        foreach (var assignee in activeAssignments
                     .SelectMany(x => x ?? new List<UserRef>())
                     .Where(x => !string.IsNullOrWhiteSpace(x.UserId)))
        {
            AddRole(map, assignee.UserId, DocRoleType.WORK_PARTICIPANT, assignee);
        }

        return map;
    }

    private async Task<Dictionary<string, RoleSeed>> BuildAssignmentRoleMapAsync(WorkAssignment assignment, CancellationToken ct)
    {
        var map = new Dictionary<string, RoleSeed>(StringComparer.Ordinal);

        var roles = await _ctx.DocRoles
            .Find(x => x.DocType == DocType.WORK_ASSIGNMENT && x.DocId == assignment.Id && !x.IsDeleted)
            .ToListAsync(ct);

        foreach (var role in roles)
            AddRole(map, role.UserId, role.Role, role.User);

        AddRole(map, assignment.CreatedByUserId, DocRoleType.ASSIGNER, null);

        foreach (var assignee in (assignment.Assignees ?? new List<UserRef>())
                     .Where(x => !string.IsNullOrWhiteSpace(x.UserId)))
        {
            AddRole(map, assignee.UserId, DocRoleType.ASSIGNEE, assignee);
        }

        foreach (var watcher in (assignment.LeaderWatchers ?? new List<UserRef>())
                     .Where(x => !string.IsNullOrWhiteSpace(x.UserId)))
        {
            AddRole(map, watcher.UserId, DocRoleType.ASSIGNMENT_LEADER_WATCH, watcher);
        }

        if (!string.IsNullOrWhiteSpace(assignment.ParentAssignmentId))
        {
            var parent = await _ctx.WorkAssignments
                .Find(x => x.Id == assignment.ParentAssignmentId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);

            if (parent is not null)
            {
                AddRole(map, parent.CreatedByUserId, DocRoleType.ASSIGNMENT_BRANCH_VIEWER, null);

                foreach (var parentAssignee in (parent.Assignees ?? new List<UserRef>())
                             .Where(x => !string.IsNullOrWhiteSpace(x.UserId)))
                {
                    AddRole(map, parentAssignee.UserId, DocRoleType.ASSIGNMENT_BRANCH_VIEWER, parentAssignee);
                }

            }
        }

        return map;
    }

    private async Task<WorkAssignmentReport?> LoadCurrentReportAsync(WorkReportPeriod period, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(period.CurrentReportId))
        {
            var byId = await _ctx.WorkAssignmentReports
                .Find(Builders<WorkAssignmentReport>.Filter.Eq(x => x.Id, period.CurrentReportId)
                      & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false)
                      & Builders<WorkAssignmentReport>.Filter.Ne(x => x.IsActive, false))
                .FirstOrDefaultAsync(ct);

            if (byId is not null)
                return byId;
        }

        return await _ctx.WorkAssignmentReports
            .Find(Builders<WorkAssignmentReport>.Filter.Eq(x => x.WorkReportPeriodId, period.Id)
                  & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsCurrent, true)
                  & Builders<WorkAssignmentReport>.Filter.Ne(x => x.IsActive, false)
                  & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false))
            .SortByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    private async Task UpsertMyReportListDocRolesAsync(
        WorkReportPeriod period,
        WorkAssignmentReport? report,
        UserRef? assignee,
        string byUserId,
        DateTime now,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(period.AssigneeUserId))
            return;

        var sortUpdatedAtUtc = MaxDate(period.UpdatedAtUtc, report?.UpdatedAtUtc);
        var sourceCreatedAtUtc = report?.CreatedAtUtc ?? period.CreatedAtUtc;

        var filter = Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.UserId, period.AssigneeUserId)
                     & Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.WorkReportPeriodId, period.Id);

        var update = Builders<MyReportPeriodListDocRole>.Update
            .Set(x => x.DocType, DocType.WORK_REPORT)
            .Set(x => x.DocId, period.Id)
            .Set(x => x.UserId, period.AssigneeUserId)
            .Set(x => x.User, assignee)
            .Set(x => x.Roles, new List<DocRoleType> { DocRoleType.ASSIGNEE })
            .Set(x => x.WorkId, period.WorkId)
            .Set(x => x.AssignmentId, period.WorkAssignmentId)
            .Set(x => x.WorkTemplateAssigneeId, period.WorkTemplateAssigneeId)
            .Set(x => x.WorkReportPeriodId, period.Id)
            .Set(x => x.CurrentReportId, NullIfWhiteSpace(report?.Id))
            .Set(x => x.AssigneeUserId, period.AssigneeUserId)
            .Set(x => x.DynamicExcelId, period.DynamicExcelId)
            .Set(x => x.DynamicExcelCode, period.DynamicExcelCode ?? string.Empty)
            .Set(x => x.DynamicExcelName, period.DynamicExcelName ?? string.Empty)
            .Set(x => x.DynamicFormTemplateId, NullIfWhiteSpace(period.DynamicFormTemplateId))
            .Set(x => x.DynamicFormTemplateCode, period.DynamicFormTemplateCode)
            .Set(x => x.DynamicFormTemplateName, period.DynamicFormTemplateName)
            .Set(x => x.PeriodKey, period.PeriodKey ?? string.Empty)
            .Set(x => x.PeriodInstanceKey, string.IsNullOrWhiteSpace(period.PeriodInstanceKey) ? period.PeriodKey ?? string.Empty : period.PeriodInstanceKey)
            .Set(x => x.PeriodKind, WorkReportPeriodKind.Scheduled)
            .Set(x => x.ReportTitle, period.ReportTitle)
            .Set(x => x.ReportDate, period.ReportDate)
            .Set(x => x.StartedDate, report?.StartedDate ?? period.StartedDate)
            .Set(x => x.CompletedDate, report?.CompletedDate ?? period.CompletedDate)
            .Set(x => x.IsHistoricalData, report?.IsHistoricalData ?? period.IsHistoricalData)
            .Set(x => x.HistoricalDataApproved, report?.HistoricalDataApproved ?? period.HistoricalDataApproved)
            .Set(x => x.HistoricalDataApprovedAtUtc, report?.HistoricalDataApprovedAtUtc ?? period.HistoricalDataApprovedAtUtc)
            .Set(x => x.HistoricalDataApprovedByUserId, NullIfWhiteSpace(report?.HistoricalDataApprovedByUserId ?? period.HistoricalDataApprovedByUserId))
            .Set(x => x.PeriodStart, period.PeriodStart)
            .Set(x => x.PeriodEnd, period.PeriodEnd)
            .Set(x => x.DueAtUtc, period.DueAtUtc)
            .Set(x => x.PeriodStatus, period.Status)
            .Set(x => x.IsOverdue, period.IsOverdue)
            .Set(x => x.ReportStatus, report?.Status)
            .Set(x => x.IsCurrentReport, report?.IsCurrent == true && report?.IsActive != false)
            .Set(x => x.ReportIsActive, report is not null && report.IsActive != false)
            .Set(x => x.ReportDeactivatedAtUtc, report?.DeactivatedAtUtc)
            .Set(x => x.ReportDeactivationReason, report?.DeactivationReason)
            .Set(x => x.IsLateSubmission, report?.IsLateSubmission ?? false)
            .Set(x => x.VersionNo, report?.VersionNo ?? period.ReportVersionCount)
            .Set(x => x.LastSubmittedAtUtc, period.LastSubmittedAtUtc)
            .Set(x => x.ReturnedAtUtc, report?.ReturnedAtUtc)
            .Set(x => x.ApprovedAtUtc, report?.ApprovedAtUtc)
            .Set(x => x.AutoApproved, WorkAssignmentAutoApprovalState.IsAutoApproved(report))
            .Set(x => x.AutoApprovedAtUtc, report?.AutoApprovedAtUtc)
            .Set(x => x.AutoApprovedByUserId, NullIfWhiteSpace(report?.AutoApprovedByUserId))
            .Set(x => x.AutoApprovalLocked, WorkAssignmentAutoApprovalState.IsLocked(report))
            .Set(x => x.AutoApprovalConfirmedAtUtc, report?.AutoApprovalConfirmedAtUtc)
            .Set(x => x.AutoApprovalConfirmedByUserId, NullIfWhiteSpace(report?.AutoApprovalConfirmedByUserId))
            .Set(x => x.SortUpdatedAtUtc, sortUpdatedAtUtc)
            .Set(x => x.SourceCreatedAtUtc, sourceCreatedAtUtc)
            .Set(x => x.IsDeleted, false)
            .Set(x => x.DeletedAtUtc, (DateTime?)null)
            .Set(x => x.DeletedByUserId, null)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, NormalizeAuditUserId(byUserId))
            .SetOnInsert(x => x.CreatedAtUtc, now)
            .SetOnInsert(x => x.CreatedByUserId, NormalizeAuditUserId(byUserId));

        await _ctx.MyReportPeriodListDocRoles.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);

        var staleFilter = Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.WorkReportPeriodId, period.Id)
                          & Builders<MyReportPeriodListDocRole>.Filter.Ne(x => x.UserId, period.AssigneeUserId)
                          & Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.IsDeleted, false);

        await _ctx.MyReportPeriodListDocRoles.UpdateManyAsync(
            staleFilter,
            BuildSoftDeleteUpdate<MyReportPeriodListDocRole>(byUserId, now),
            cancellationToken: ct);

        await RebuildMyReportTemplateListDocRoleAsync(
            period.WorkId,
            period.DynamicFormTemplateId,
            period.AssigneeUserId,
            assignee,
            byUserId,
            now,
            ct);
    }

    private async Task RebuildMyReportTemplateListDocRoleAsync(
        string workId,
        string? dynamicFormTemplateId,
        string userId,
        UserRef? user,
        string byUserId,
        DateTime now,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workId) ||
            string.IsNullOrWhiteSpace(dynamicFormTemplateId) ||
            string.IsNullOrWhiteSpace(userId))
            return;

        var rows = await _ctx.MyReportPeriodListDocRoles
            .Find(x =>
                x.UserId == userId &&
                x.WorkId == workId &&
                x.DynamicFormTemplateId == dynamicFormTemplateId &&
                !x.IsDeleted)
            .ToListAsync(ct);

        var filter = Builders<MyReportTemplateListDocRole>.Filter.Eq(x => x.UserId, userId)
                     & Builders<MyReportTemplateListDocRole>.Filter.Eq(x => x.WorkId, workId)
                     & Builders<MyReportTemplateListDocRole>.Filter.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId);

        if (rows.Count == 0)
        {
            await _ctx.MyReportTemplateListDocRoles.UpdateManyAsync(
                filter & Builders<MyReportTemplateListDocRole>.Filter.Eq(x => x.IsDeleted, false),
                BuildSoftDeleteUpdate<MyReportTemplateListDocRole>(byUserId, now),
                cancellationToken: ct);
            return;
        }

        var latest = rows
            .OrderByDescending(x => x.DueAtUtc ?? x.SortUpdatedAtUtc)
            .ThenByDescending(x => x.SortUpdatedAtUtc)
            .First();

        user ??= latest.User;

        var update = Builders<MyReportTemplateListDocRole>.Update
            .Set(x => x.DocType, DocType.WORK_REPORT)
            .Set(x => x.DocId, dynamicFormTemplateId)
            .Set(x => x.UserId, userId)
            .Set(x => x.User, user)
            .Set(x => x.Roles, new List<DocRoleType> { DocRoleType.ASSIGNEE })
            .Set(x => x.WorkId, workId)
            .Set(x => x.DynamicExcelId, NullIfWhiteSpace(latest.DynamicExcelId))
            .Set(x => x.DynamicExcelCode, latest.DynamicExcelCode)
            .Set(x => x.DynamicExcelName, latest.DynamicExcelName)
            .Set(x => x.DynamicFormTemplateId, NullIfWhiteSpace(dynamicFormTemplateId))
            .Set(x => x.DynamicFormTemplateCode, latest.DynamicFormTemplateCode)
            .Set(x => x.DynamicFormTemplateName, latest.DynamicFormTemplateName)
            .Set(x => x.BindingCount, rows.Select(x => x.WorkTemplateAssigneeId).Distinct(StringComparer.Ordinal).Count())
            .Set(x => x.PeriodCount, rows.Count)
            .Set(x => x.ReportCount, rows.Count(x => !string.IsNullOrWhiteSpace(x.CurrentReportId)))
            .Set(x => x.LatestPeriodId, latest.WorkReportPeriodId)
            .Set(x => x.LatestPeriodKey, latest.PeriodKey)
            .Set(x => x.LatestPeriodStatus, latest.PeriodStatus)
            .Set(x => x.LatestDueAtUtc, latest.DueAtUtc)
            .Set(x => x.LatestReportId, latest.CurrentReportId)
            .Set(x => x.LatestUpdatedAtUtc, latest.SortUpdatedAtUtc)
            .Set(x => x.HasOverduePeriod, rows.Any(x => x.IsOverdue))
            .Set(x => x.IsDeleted, false)
            .Set(x => x.DeletedAtUtc, (DateTime?)null)
            .Set(x => x.DeletedByUserId, null)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, NormalizeAuditUserId(byUserId))
            .SetOnInsert(x => x.CreatedAtUtc, now)
            .SetOnInsert(x => x.CreatedByUserId, NormalizeAuditUserId(byUserId));

        await _ctx.MyReportTemplateListDocRoles.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    private async Task UpsertReviewReportListDocRolesAsync(
        WorkReportPeriod period,
        WorkAssignmentReport? report,
        WorkAssignment assignment,
        WorkTemplateAssignee? binding,
        UserRef? assignee,
        string byUserId,
        DateTime now,
        CancellationToken ct)
    {
        var reviewerIds = new[] { assignment.CreatedByUserId }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (reviewerIds.Count == 0)
        {
            await _ctx.ReviewReportListDocRoles.UpdateManyAsync(
                Builders<ReviewReportListDocRole>.Filter.Eq(x => x.WorkReportPeriodId, period.Id)
                & Builders<ReviewReportListDocRole>.Filter.Eq(x => x.IsDeleted, false),
                BuildSoftDeleteUpdate<ReviewReportListDocRole>(byUserId, now),
                cancellationToken: ct);

            await _ctx.ReviewAssignmentSummaryDocRoles.UpdateManyAsync(
                Builders<ReviewAssignmentSummaryDocRole>.Filter.Eq(x => x.AssignmentId, period.WorkAssignmentId)
                & Builders<ReviewAssignmentSummaryDocRole>.Filter.Eq(x => x.IsDeleted, false),
                BuildSoftDeleteUpdate<ReviewAssignmentSummaryDocRole>(byUserId, now),
                cancellationToken: ct);
            return;
        }

        var writes = reviewerIds.Select(reviewerUserId =>
        {
            var filter = Builders<ReviewReportListDocRole>.Filter.Eq(x => x.ReviewerUserId, reviewerUserId)
                         & Builders<ReviewReportListDocRole>.Filter.Eq(x => x.WorkReportPeriodId, period.Id);

            var update = Builders<ReviewReportListDocRole>.Update
                .Set(x => x.DocType, DocType.WORK_REPORT)
                .Set(x => x.DocId, period.Id)
                .Set(x => x.UserId, reviewerUserId)
                .Set(x => x.User, null)
                .Set(x => x.Roles, new List<DocRoleType> { DocRoleType.ASSIGNER })
                .Set(x => x.WorkId, period.WorkId)
                .Set(x => x.AssignmentId, period.WorkAssignmentId)
                .Set(x => x.WorkReportPeriodId, period.Id)
                .Set(x => x.CurrentReportId, NullIfWhiteSpace(report?.Id))
                .Set(x => x.ReviewerUserId, reviewerUserId)
                .Set(x => x.AssigneeUserId, period.AssigneeUserId)
                .Set(x => x.AssigneeUserName, assignee?.Username)
                .Set(x => x.AssigneeFullName, assignee?.FullName)
                .Set(x => x.AssigneeUnitId, NullIfWhiteSpace(period.AssigneeUnitId ?? assignee?.UnitId))
                .Set(x => x.AssigneeUnitName, assignee?.UnitName)
                .Set(x => x.AssigneeUnitShortName, assignee?.UnitShortName)
                .Set(x => x.DynamicExcelId, period.DynamicExcelId)
                .Set(x => x.DynamicExcelCode, period.DynamicExcelCode ?? string.Empty)
                .Set(x => x.DynamicExcelName, period.DynamicExcelName ?? string.Empty)
                .Set(x => x.DynamicFormTemplateId, NullIfWhiteSpace(period.DynamicFormTemplateId))
                .Set(x => x.DynamicFormTemplateCode, period.DynamicFormTemplateCode)
                .Set(x => x.DynamicFormTemplateName, period.DynamicFormTemplateName)
                .Set(x => x.PeriodKey, period.PeriodKey ?? string.Empty)
                .Set(x => x.PeriodInstanceKey, string.IsNullOrWhiteSpace(period.PeriodInstanceKey) ? period.PeriodKey ?? string.Empty : period.PeriodInstanceKey)
                .Set(x => x.PeriodKind, WorkReportPeriodKind.Scheduled)
                .Set(x => x.ReportTitle, period.ReportTitle)
                .Set(x => x.ReportDate, period.ReportDate)
                .Set(x => x.StartedDate, report?.StartedDate ?? period.StartedDate)
                .Set(x => x.CompletedDate, report?.CompletedDate ?? period.CompletedDate)
                .Set(x => x.IsHistoricalData, report?.IsHistoricalData ?? period.IsHistoricalData)
                .Set(x => x.HistoricalDataApproved, report?.HistoricalDataApproved ?? period.HistoricalDataApproved)
                .Set(x => x.HistoricalDataApprovedAtUtc, report?.HistoricalDataApprovedAtUtc ?? period.HistoricalDataApprovedAtUtc)
                .Set(x => x.HistoricalDataApprovedByUserId, NullIfWhiteSpace(report?.HistoricalDataApprovedByUserId ?? period.HistoricalDataApprovedByUserId))
                .Set(x => x.PeriodStart, period.PeriodStart)
                .Set(x => x.PeriodEnd, period.PeriodEnd)
                .Set(x => x.DueAtUtc, period.DueAtUtc)
                .Set(x => x.PeriodStatus, period.Status)
                .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(period.Status))
                .Set(x => x.ReportStatus, report?.Status)
                .Set(x => x.ReportIsActive, report is not null && report.IsActive != false)
                .Set(x => x.ReportDeactivatedAtUtc, report?.DeactivatedAtUtc)
                .Set(x => x.ReportDeactivationReason, report?.DeactivationReason)
                .Set(x => x.SubmittedAtUtc, report?.SubmittedAtUtc ?? period.LastSubmittedAtUtc)
                .Set(x => x.ApprovedAtUtc, report?.ApprovedAtUtc)
                .Set(x => x.AutoApproved, WorkAssignmentAutoApprovalState.IsAutoApproved(report))
                .Set(x => x.AutoApprovedAtUtc, report?.AutoApprovedAtUtc)
                .Set(x => x.AutoApprovedByUserId, NullIfWhiteSpace(report?.AutoApprovedByUserId))
                .Set(x => x.AutoApprovalLocked, WorkAssignmentAutoApprovalState.IsLocked(report))
                .Set(x => x.AutoApprovalConfirmedAtUtc, report?.AutoApprovalConfirmedAtUtc)
                .Set(x => x.AutoApprovalConfirmedByUserId, NullIfWhiteSpace(report?.AutoApprovalConfirmedByUserId))
                .Set(x => x.ReturnedAtUtc, report?.ReturnedAtUtc)
                .Set(x => x.ReturnReason, report?.ReturnReason ?? period.ReturnReason)
                .Set(x => x.ReviewerComment, report?.ReviewerComment ?? period.ReviewerComment)
                .Set(x => x.ProgressStatus, assignment.ProgressStatus)
                .Set(x => x.ProgressStatusUpdatedAtUtc, assignment.ProgressStatusUpdatedAtUtc)
                .Set(x => x.HasAnyDuePeriod, assignment.HasAnyDuePeriod)
                .Set(x => x.HasOverduePeriod, assignment.HasOverduePeriod)
                .Set(x => x.WorstPeriodStatus, assignment.WorstPeriodStatus)
                .Set(x => x.WorstOverdueReasonCode, assignment.WorstOverdueReasonCode)
                .Set(x => x.WorstOverdueReasonLabel, assignment.WorstOverdueReasonLabel)
                .Set(x => x.ReviewStatusBucket, WorkReportPeriodStatusHelper.ToReviewStatusBucket(
                    period.Status,
                    period.ReturnReason,
                    report?.ReturnReason,
                    report?.ReturnedAtUtc))
                .Set(x => x.WaitingReview, WorkReportPeriodStatusHelper.IsWaitingReview(period.Status))
                .Set(x => x.Returned, WorkReportPeriodStatusHelper.IsReturned(
                    period.Status,
                    period.ReturnReason,
                    report?.ReturnReason,
                    report?.ReturnedAtUtc))
                .Set(x => x.ReviewRank, WorkReportPeriodStatusHelper.GetReviewRank(period.Status))
                .Set(x => x.SortDueAtUtc, period.DueAtUtc)
                .Set(x => x.SortUpdatedAtUtc, MaxDate(period.UpdatedAtUtc, report?.UpdatedAtUtc))
                .Set(x => x.IsDeleted, false)
                .Set(x => x.DeletedAtUtc, (DateTime?)null)
                .Set(x => x.DeletedByUserId, null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, NormalizeAuditUserId(byUserId))
                .SetOnInsert(x => x.CreatedAtUtc, now)
                .SetOnInsert(x => x.CreatedByUserId, NormalizeAuditUserId(byUserId));

            return new UpdateOneModel<ReviewReportListDocRole>(filter, update) { IsUpsert = true };
        }).ToList();

        await _ctx.ReviewReportListDocRoles.BulkWriteAsync(writes, cancellationToken: ct);

        var staleFilter = Builders<ReviewReportListDocRole>.Filter.Eq(x => x.WorkReportPeriodId, period.Id)
                          & Builders<ReviewReportListDocRole>.Filter.Eq(x => x.IsDeleted, false)
                          & Builders<ReviewReportListDocRole>.Filter.Nin(x => x.ReviewerUserId, reviewerIds);

        await _ctx.ReviewReportListDocRoles.UpdateManyAsync(
            staleFilter,
            BuildSoftDeleteUpdate<ReviewReportListDocRole>(byUserId, now),
            cancellationToken: ct);

        foreach (var reviewerUserId in reviewerIds)
        {
            await RebuildReviewAssignmentSummaryDocRoleAsync(
                period.WorkId,
                period.WorkAssignmentId,
                reviewerUserId,
                byUserId,
                now,
                ct);
        }

        await _ctx.ReviewAssignmentSummaryDocRoles.UpdateManyAsync(
            Builders<ReviewAssignmentSummaryDocRole>.Filter.Eq(x => x.AssignmentId, period.WorkAssignmentId)
            & Builders<ReviewAssignmentSummaryDocRole>.Filter.Eq(x => x.IsDeleted, false)
            & Builders<ReviewAssignmentSummaryDocRole>.Filter.Nin(x => x.ReviewerUserId, reviewerIds),
            BuildSoftDeleteUpdate<ReviewAssignmentSummaryDocRole>(byUserId, now),
            cancellationToken: ct);
    }

    private async Task RebuildReviewAssignmentSummaryDocRoleAsync(
        string workId,
        string assignmentId,
        string reviewerUserId,
        string byUserId,
        DateTime now,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workId) ||
            string.IsNullOrWhiteSpace(assignmentId) ||
            string.IsNullOrWhiteSpace(reviewerUserId))
            return;

        var filter = Builders<ReviewAssignmentSummaryDocRole>.Filter.Eq(x => x.ReviewerUserId, reviewerUserId)
                     & Builders<ReviewAssignmentSummaryDocRole>.Filter.Eq(x => x.WorkId, workId)
                     & Builders<ReviewAssignmentSummaryDocRole>.Filter.Eq(x => x.AssignmentId, assignmentId);

        var reportRows = await _ctx.ReviewReportListDocRoles
            .Find(x =>
                x.ReviewerUserId == reviewerUserId &&
                x.WorkId == workId &&
                x.AssignmentId == assignmentId &&
                !x.IsDeleted)
            .ToListAsync(ct);

        if (reportRows.Count == 0)
        {
            await _ctx.ReviewAssignmentSummaryDocRoles.UpdateManyAsync(
                filter & Builders<ReviewAssignmentSummaryDocRole>.Filter.Eq(x => x.IsDeleted, false),
                BuildSoftDeleteUpdate<ReviewAssignmentSummaryDocRole>(byUserId, now),
                cancellationToken: ct);
            return;
        }

        var latestPeriod = reportRows
            .OrderByDescending(x => x.DueAtUtc ?? x.SortUpdatedAtUtc)
            .ThenByDescending(x => x.SortUpdatedAtUtc)
            .First();
        var worstPeriod = reportRows
            .OrderByDescending(x => x.ReviewRank)
            .ThenByDescending(x => x.DueAtUtc ?? x.SortUpdatedAtUtc)
            .ThenByDescending(x => x.SortUpdatedAtUtc)
            .First();
        var assignees = BuildReviewSummaryAssignees(reportRows);
        var firstAssignee = assignees
            .OrderBy(x => x.UnitShortName ?? string.Empty)
            .ThenBy(x => x.FullName ?? string.Empty)
            .ThenBy(x => x.Username ?? string.Empty)
            .FirstOrDefault();
        var periodKeys = reportRows
            .Select(x => x.PeriodKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var reviewBuckets = reportRows
            .Select(x => x.ReviewStatusBucket)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var auditUserId = NormalizeAuditUserId(byUserId);

        var update = Builders<ReviewAssignmentSummaryDocRole>.Update
            .Set(x => x.DocType, DocType.WORK_ASSIGNMENT)
            .Set(x => x.DocId, assignmentId)
            .Set(x => x.UserId, reviewerUserId)
            .Set(x => x.User, null)
            .Set(x => x.Roles, new List<DocRoleType> { DocRoleType.ASSIGNER })
            .Set(x => x.WorkId, workId)
            .Set(x => x.AssignmentId, assignmentId)
            .Set(x => x.ReviewerUserId, reviewerUserId)
            .Set(x => x.DynamicExcelId, latestPeriod.DynamicExcelId)
            .Set(x => x.DynamicExcelCode, latestPeriod.DynamicExcelCode)
            .Set(x => x.DynamicExcelName, latestPeriod.DynamicExcelName)
            .Set(x => x.DynamicFormTemplateId, NullIfWhiteSpace(latestPeriod.DynamicFormTemplateId))
            .Set(x => x.DynamicFormTemplateCode, latestPeriod.DynamicFormTemplateCode)
            .Set(x => x.DynamicFormTemplateName, latestPeriod.DynamicFormTemplateName)
            .Set(x => x.Assignees, assignees)
            .Set(x => x.AssigneeUserIds, assignees.Select(x => x.UserId).Distinct(StringComparer.Ordinal).ToList())
            .Set(x => x.AssigneeUnitIds, assignees.Select(x => x.UnitId).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.Ordinal).ToList())
            .Set(x => x.FirstAssigneeUserName, firstAssignee?.Username)
            .Set(x => x.FirstAssigneeFullName, firstAssignee?.FullName)
            .Set(x => x.FirstAssigneeUnitShortName, firstAssignee?.UnitShortName)
            .Set(x => x.ProgressStatus, latestPeriod.ProgressStatus)
            .Set(x => x.ProgressStatusUpdatedAtUtc, latestPeriod.ProgressStatusUpdatedAtUtc)
            .Set(x => x.PeriodCount, reportRows.Count)
            .Set(x => x.PeriodKeys, periodKeys)
            .Set(x => x.ReportCount, reportRows.Count(x => !string.IsNullOrWhiteSpace(x.CurrentReportId)))
            .Set(x => x.LatestPeriodId, latestPeriod.WorkReportPeriodId)
            .Set(x => x.LatestPeriodKey, latestPeriod.PeriodKey)
            .Set(x => x.LatestPeriodStatus, latestPeriod.PeriodStatus)
            .Set(x => x.LatestDueAtUtc, latestPeriod.DueAtUtc)
            .Set(x => x.LatestReportId, NullIfWhiteSpace(latestPeriod.CurrentReportId))
            .Set(x => x.LatestUpdatedAtUtc, latestPeriod.SortUpdatedAtUtc)
            .Set(x => x.HasAnyDuePeriod, reportRows.Count > 0)
            .Set(x => x.HasOverduePeriod, reportRows.Any(x => x.IsOverdue))
            .Set(x => x.OverdueCount, reportRows.Count(x => x.IsOverdue))
            .Set(x => x.WaitingReviewCount, reportRows.Count(x => x.WaitingReview))
            .Set(x => x.ReturnedCount, reportRows.Count(x => x.Returned))
            .Set(x => x.ReviewStatusBuckets, reviewBuckets)
            .Set(x => x.WorstReviewStatusBucket, worstPeriod.ReviewStatusBucket)
            .Set(x => x.WorstReviewRank, worstPeriod.ReviewRank)
            .Set(x => x.WorstPeriodStatus, worstPeriod.PeriodStatus)
            .Set(x => x.WorstOverdueReasonCode, worstPeriod.WorstOverdueReasonCode)
            .Set(x => x.WorstOverdueReasonLabel, worstPeriod.WorstOverdueReasonLabel)
            .Set(x => x.EvaluationCode, null)
            .Set(x => x.EvaluationLabel, null)
            .Set(x => x.SortHasOverduePeriod, reportRows.Any(x => x.IsOverdue))
            .Set(x => x.SortLatestDueAtUtc, latestPeriod.DueAtUtc)
            .Set(x => x.SortUpdatedAtUtc, reportRows.Max(x => x.SortUpdatedAtUtc))
            .Set(x => x.IsDeleted, false)
            .Set(x => x.DeletedAtUtc, (DateTime?)null)
            .Set(x => x.DeletedByUserId, null)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, auditUserId)
            .SetOnInsert(x => x.CreatedAtUtc, now)
            .SetOnInsert(x => x.CreatedByUserId, auditUserId);

        await _ctx.ReviewAssignmentSummaryDocRoles.UpdateOneAsync(
            filter,
            update,
            new UpdateOptions { IsUpsert = true },
            ct);
    }

    private static List<UserRef> BuildReviewSummaryAssignees(IEnumerable<ReviewReportListDocRole> reportRows)
        => reportRows
            .Where(x => !string.IsNullOrWhiteSpace(x.AssigneeUserId))
            .GroupBy(x => x.AssigneeUserId, StringComparer.Ordinal)
            .Select(g =>
            {
                var row = g.First();
                return new UserRef
                {
                    UserId = row.AssigneeUserId,
                    Username = row.AssigneeUserName,
                    FullName = row.AssigneeFullName,
                    UnitId = row.AssigneeUnitId,
                    UnitName = row.AssigneeUnitName,
                    UnitShortName = row.AssigneeUnitShortName
                };
            })
            .OrderBy(x => x.UnitShortName ?? string.Empty)
            .ThenBy(x => x.FullName ?? string.Empty)
            .ThenBy(x => x.Username ?? string.Empty)
            .ToList();

    private async Task SoftDeleteReportReadModelsByPeriodAsync(string workReportPeriodId, string byUserId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var affectedReviewRows = await _ctx.ReviewReportListDocRoles
            .Find(x => x.WorkReportPeriodId == workReportPeriodId && !x.IsDeleted)
            .Project(x => new
            {
                x.WorkId,
                x.AssignmentId,
                x.ReviewerUserId
            })
            .ToListAsync(ct);
        var affectedTemplateRows = await _ctx.MyReportPeriodListDocRoles
            .Find(x => x.WorkReportPeriodId == workReportPeriodId && !x.IsDeleted)
            .Project(x => new
            {
                x.WorkId,
                x.DynamicExcelId,
                x.DynamicFormTemplateId,
                x.UserId,
                x.User
            })
            .ToListAsync(ct);

        await _ctx.MyReportPeriodListDocRoles.UpdateManyAsync(
            Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.WorkReportPeriodId, workReportPeriodId)
            & Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.IsDeleted, false),
            BuildSoftDeleteUpdate<MyReportPeriodListDocRole>(byUserId, now),
            cancellationToken: ct);

        foreach (var row in affectedTemplateRows
                     .GroupBy(x => new { x.WorkId, x.DynamicFormTemplateId, x.UserId })
                     .Select(g => g.First()))
        {
            await RebuildMyReportTemplateListDocRoleAsync(
                row.WorkId,
                row.DynamicFormTemplateId,
                row.UserId,
                row.User,
                byUserId,
                now,
                ct);
        }

        await _ctx.ReviewReportListDocRoles.UpdateManyAsync(
            Builders<ReviewReportListDocRole>.Filter.Eq(x => x.WorkReportPeriodId, workReportPeriodId)
            & Builders<ReviewReportListDocRole>.Filter.Eq(x => x.IsDeleted, false),
            BuildSoftDeleteUpdate<ReviewReportListDocRole>(byUserId, now),
            cancellationToken: ct);

        foreach (var row in affectedReviewRows
                     .Where(x =>
                         !string.IsNullOrWhiteSpace(x.WorkId) &&
                         !string.IsNullOrWhiteSpace(x.AssignmentId) &&
                         !string.IsNullOrWhiteSpace(x.ReviewerUserId))
                     .GroupBy(x => new { x.WorkId, x.AssignmentId, x.ReviewerUserId })
                     .Select(g => g.Key))
        {
            await RebuildReviewAssignmentSummaryDocRoleAsync(
                row.WorkId,
                row.AssignmentId,
                row.ReviewerUserId,
                byUserId,
                now,
                ct);
        }
    }

    private static UpdateDefinition<T> BuildSoftDeleteUpdate<T>(string byUserId, DateTime now)
    {
        var auditUserId = NormalizeAuditUserId(byUserId);

        return Builders<T>.Update
            .Set("isDeleted", true)
            .Set("deletedAtUtc", now)
            .Set("deletedByUserId", auditUserId)
            .Set("updatedAtUtc", now)
            .Set("updatedByUserId", auditUserId);
    }

    private static string? NormalizeAuditUserId(string? byUserId)
        => ObjectId.TryParse(byUserId, out _) ? byUserId : null;

    private static UserRef? ResolveAssignee(
        WorkReportPeriod period,
        WorkAssignment assignment,
        WorkTemplateAssignee? binding)
    {
        var fromAssignment = (assignment.Assignees ?? new List<UserRef>())
            .FirstOrDefault(x => string.Equals(x.UserId, period.AssigneeUserId, StringComparison.Ordinal));

        if (fromAssignment is not null)
            return CloneUserRef(fromAssignment);

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

    private static void AddRole(
        Dictionary<string, RoleSeed> map,
        string? userId,
        DocRoleType role,
        UserRef? user)
    {
        userId = NullIfWhiteSpace(userId);
        if (userId is null)
            return;

        if (!map.TryGetValue(userId, out var seed))
        {
            seed = new RoleSeed();
            map[userId] = seed;
        }

        seed.Roles.Add(role);

        if (seed.User is null && user is not null)
            seed.User = CloneUserRef(user);
    }

    private static UserRef CloneUserRef(UserRef x)
    {
        return new UserRef
        {
            UserId = x.UserId,
            Username = x.Username,
            FullName = x.FullName,
            UnitId = x.UnitId,
            UnitSymbol = x.UnitSymbol,
            UnitShortName = x.UnitShortName,
            UnitName = x.UnitName,
            PositionCode = x.PositionCode,
            PositionName = x.PositionName
        };
    }

    private static List<UserRef> CloneUserRefs(IEnumerable<UserRef>? refs)
        => (refs ?? Enumerable.Empty<UserRef>())
            .Where(x => !string.IsNullOrWhiteSpace(x.UserId))
            .Select(CloneUserRef)
            .ToList();

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

    private static DateTime MaxDate(DateTime left, DateTime? right)
        => right.HasValue && right.Value > left ? right.Value : left;

    private static int CountDistinct(IEnumerable<string>? values)
        => values?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .Count() ?? 0;

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class RoleSeed
    {
        public HashSet<DocRoleType> Roles { get; } = new();
        public UserRef? User { get; set; }
    }
}
