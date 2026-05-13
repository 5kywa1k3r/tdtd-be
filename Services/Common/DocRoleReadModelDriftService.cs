using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.Common;

public interface IDocRoleReadModelDriftService
{
    Task<DocRoleReadModelDriftResult> CheckAsync(
        DocRoleReadModelDriftOptions options,
        CancellationToken ct = default);
}

public sealed class DocRoleReadModelDriftOptions
{
    public string? WorkId { get; init; }
    public string? AssignmentId { get; init; }
    public string? WorkReportPeriodId { get; init; }
    public string? UserId { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed class DocRoleReadModelDriftResult
{
    public DateTime CheckedAtUtc { get; init; } = DateTime.UtcNow;
    public string? WorkId { get; init; }
    public string? AssignmentId { get; init; }
    public string? WorkReportPeriodId { get; init; }
    public string? UserId { get; init; }
    public int Limit { get; init; }
    public List<DocRoleReadModelDriftListResult> Lists { get; init; } = new();
    public int TotalIssueCount => Lists.Sum(x => x.IssueCount);
    public bool HasIssues => TotalIssueCount > 0;
}

public sealed class DocRoleReadModelDriftListResult
{
    public string Name { get; init; } = string.Empty;
    public string Collection { get; init; } = string.Empty;
    public string UniqueKey { get; init; } = string.Empty;
    public long ActiveRowCount { get; set; }
    public int ScannedRowCount { get; set; }
    public bool Truncated { get; set; }
    public List<DocRoleReadModelDriftIssue> Issues { get; } = new();
    public int IssueCount => Issues.Count;
}

public sealed class DocRoleReadModelDriftIssue
{
    public string Type { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public string? ProjectionId { get; init; }
    public string? SourceId { get; init; }
    public string? UserId { get; init; }
    public string Message { get; init; } = string.Empty;
    public Dictionary<string, string?> Fields { get; init; } = new();
}

public sealed class DocRoleReadModelDriftService : IDocRoleReadModelDriftService
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    private readonly MongoDbContext _ctx;

    public DocRoleReadModelDriftService(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<DocRoleReadModelDriftResult> CheckAsync(
        DocRoleReadModelDriftOptions options,
        CancellationToken ct = default)
    {
        var scope = await ResolveScopeAsync(options, ct);

        var lists = new List<DocRoleReadModelDriftListResult>
        {
            await CheckWorkListAsync(scope, ct),
            await CheckAssignmentListAsync(scope, ct),
            await CheckMyReportPeriodListAsync(scope, ct),
            await CheckMyReportTemplateListAsync(scope, ct),
            await CheckReviewReportListAsync(scope, ct)
        };

        return new DocRoleReadModelDriftResult
        {
            WorkId = scope.WorkId,
            AssignmentId = scope.AssignmentId,
            WorkReportPeriodId = scope.WorkReportPeriodId,
            UserId = scope.UserId,
            Limit = scope.Limit,
            Lists = lists
        };
    }

    private async Task<DocRoleReadModelDriftListResult> CheckWorkListAsync(
        DriftScope scope,
        CancellationToken ct)
    {
        var result = NewList("WorkListDocRole", "work_list_doc_roles", "userId + docId");
        var fb = Builders<WorkListDocRole>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);
        if (!string.IsNullOrWhiteSpace(scope.WorkId))
            filter &= fb.Eq(x => x.WorkId, scope.WorkId);
        if (!string.IsNullOrWhiteSpace(scope.UserId))
            filter &= fb.Eq(x => x.UserId, scope.UserId);

        var rows = await LoadRowsAsync(_ctx.WorkListDocRoles, filter, result, scope.Limit, ct);
        AddDuplicateIssues(result, rows, x => $"{x.UserId}:{x.DocId}", x => x.Id, "userId+docId");

        var workIds = rows.Select(x => x.WorkId).Where(NotBlank).Distinct(StringComparer.Ordinal).ToList();
        if (!string.IsNullOrWhiteSpace(scope.WorkId) && !workIds.Contains(scope.WorkId))
            workIds.Add(scope.WorkId);

        var works = await LoadWorksAsync(workIds, ct);
        var roles = await LoadDocRolesAsync(DocType.WORK, workIds, ct);

        foreach (var row in rows)
        {
            var key = $"{row.UserId}:{row.WorkId}";
            if (!works.TryGetValue(row.WorkId, out var source))
            {
                AddIssue(result, "ORPHAN_SOURCE", key, row.Id, row.WorkId, row.UserId,
                    "Projection row points to a missing or deleted Work.");
                continue;
            }

            AddStaleIssueIfNeeded(result, key, row.Id, source.Id, row.UserId, row.UpdatedAtUtc, source.UpdatedAtUtc);
            AddFieldMismatch(result, key, row.Id, source.Id, row.UserId, "docId", row.DocId, source.Id);
            AddFieldMismatch(result, key, row.Id, source.Id, row.UserId, "name", row.Name, source.Name ?? string.Empty);
            AddFieldMismatch(result, key, row.Id, source.Id, row.UserId, "status", row.Status, source.Status);
            AddFieldMismatch(result, key, row.Id, source.Id, row.UserId, "priority", row.Priority, source.Priority);
            AddFieldMismatch(result, key, row.Id, source.Id, row.UserId, "dueDate", row.DueDate, source.DueDate);
            AddRoleMismatch(result, key, row.Id, source.Id, row.UserId, row.Roles, roles, source.Id, row.UserId);
        }

        await AddMissingDocRoleProjectionIssuesAsync(
            result,
            DocType.WORK,
            workIds,
            scope.UserId,
            (docId, userId) => _ctx.WorkListDocRoles.CountDocumentsAsync(
                x => x.WorkId == docId && x.UserId == userId && !x.IsDeleted,
                cancellationToken: ct),
            scope.Limit,
            ct);

        return result;
    }

    private async Task<DocRoleReadModelDriftListResult> CheckAssignmentListAsync(
        DriftScope scope,
        CancellationToken ct)
    {
        var result = NewList("AssignmentListDocRole", "assignment_list_doc_roles", "userId + docId");
        var fb = Builders<AssignmentListDocRole>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);
        if (!string.IsNullOrWhiteSpace(scope.WorkId))
            filter &= fb.Eq(x => x.WorkId, scope.WorkId);
        if (!string.IsNullOrWhiteSpace(scope.AssignmentId))
            filter &= fb.Eq(x => x.AssignmentId, scope.AssignmentId);
        if (!string.IsNullOrWhiteSpace(scope.UserId))
            filter &= fb.Eq(x => x.UserId, scope.UserId);

        var rows = await LoadRowsAsync(_ctx.AssignmentListDocRoles, filter, result, scope.Limit, ct);
        AddDuplicateIssues(result, rows, x => $"{x.UserId}:{x.DocId}", x => x.Id, "userId+docId");

        var assignmentIds = rows.Select(x => x.AssignmentId).Where(NotBlank).Distinct(StringComparer.Ordinal).ToList();
        if (!string.IsNullOrWhiteSpace(scope.AssignmentId) && !assignmentIds.Contains(scope.AssignmentId))
            assignmentIds.Add(scope.AssignmentId);

        var assignments = await LoadAssignmentsAsync(assignmentIds, ct);
        var roles = await LoadDocRolesAsync(DocType.WORK_ASSIGNMENT, assignmentIds, ct);

        foreach (var row in rows)
        {
            var key = $"{row.UserId}:{row.AssignmentId}";
            if (!assignments.TryGetValue(row.AssignmentId, out var source))
            {
                AddIssue(result, "ORPHAN_SOURCE", key, row.Id, row.AssignmentId, row.UserId,
                    "Projection row points to a missing or deleted WorkAssignment.");
                continue;
            }

            var expectedAssignees = (source.Assignees ?? new List<UserRef>())
                .Select(x => x.UserId)
                .Where(NotBlank)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            AddStaleIssueIfNeeded(result, key, row.Id, source.Id, row.UserId, row.UpdatedAtUtc, source.UpdatedAtUtc);
            AddFieldMismatch(result, key, row.Id, source.Id, row.UserId, "docId", row.DocId, source.Id);
            AddFieldMismatch(result, key, row.Id, source.Id, row.UserId, "workId", row.WorkId, source.WorkId);
            AddFieldMismatch(result, key, row.Id, source.Id, row.UserId, "code", row.Code, source.Code ?? string.Empty);
            AddFieldMismatch(result, key, row.Id, source.Id, row.UserId, "isActive", row.IsActive, source.IsActive);
            AddFieldMismatch(result, key, row.Id, source.Id, row.UserId, "progressStatus", row.ProgressStatus, source.ProgressStatus);
            AddFieldMismatch(result, key, row.Id, source.Id, row.UserId, "hasOverduePeriod", row.HasOverduePeriod, source.HasOverduePeriod);
            AddListMismatch(result, key, row.Id, source.Id, row.UserId, "assigneeUserIds", row.AssigneeUserIds, expectedAssignees);
            AddRoleMismatch(result, key, row.Id, source.Id, row.UserId, row.Roles, roles, source.Id, row.UserId);
        }

        await AddMissingDocRoleProjectionIssuesAsync(
            result,
            DocType.WORK_ASSIGNMENT,
            assignmentIds,
            scope.UserId,
            (docId, userId) => _ctx.AssignmentListDocRoles.CountDocumentsAsync(
                x => x.AssignmentId == docId && x.UserId == userId && !x.IsDeleted,
                cancellationToken: ct),
            scope.Limit,
            ct);

        return result;
    }

    private async Task<DocRoleReadModelDriftListResult> CheckMyReportPeriodListAsync(
        DriftScope scope,
        CancellationToken ct)
    {
        var result = NewList("MyReportPeriodListDocRole", "my_report_period_list_doc_roles", "userId + workReportPeriodId");
        var fb = Builders<MyReportPeriodListDocRole>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);
        if (!string.IsNullOrWhiteSpace(scope.WorkId))
            filter &= fb.Eq(x => x.WorkId, scope.WorkId);
        if (!string.IsNullOrWhiteSpace(scope.AssignmentId))
            filter &= fb.Eq(x => x.AssignmentId, scope.AssignmentId);
        if (!string.IsNullOrWhiteSpace(scope.WorkReportPeriodId))
            filter &= fb.Eq(x => x.WorkReportPeriodId, scope.WorkReportPeriodId);
        if (!string.IsNullOrWhiteSpace(scope.UserId))
            filter &= fb.Eq(x => x.UserId, scope.UserId);

        var rows = await LoadRowsAsync(_ctx.MyReportPeriodListDocRoles, filter, result, scope.Limit, ct);
        AddDuplicateIssues(result, rows, x => $"{x.UserId}:{x.WorkReportPeriodId}", x => x.Id, "userId+workReportPeriodId");

        var periodIds = rows.Select(x => x.WorkReportPeriodId).Where(NotBlank).Distinct(StringComparer.Ordinal).ToList();
        if (!string.IsNullOrWhiteSpace(scope.WorkReportPeriodId) && !periodIds.Contains(scope.WorkReportPeriodId))
            periodIds.Add(scope.WorkReportPeriodId);

        var periods = await LoadPeriodsAsync(periodIds, ct);
        foreach (var row in rows)
        {
            var key = $"{row.UserId}:{row.WorkReportPeriodId}";
            if (!periods.TryGetValue(row.WorkReportPeriodId, out var period))
            {
                AddIssue(result, "ORPHAN_SOURCE", key, row.Id, row.WorkReportPeriodId, row.UserId,
                    "Projection row points to a missing or deleted WorkReportPeriod.");
                continue;
            }

            var report = await LoadCurrentReportAsync(period, ct);
            var sourceUpdatedAt = MaxDate(period.UpdatedAtUtc, report?.UpdatedAtUtc);

            AddStaleIssueIfNeeded(result, key, row.Id, period.Id, row.UserId, row.UpdatedAtUtc, sourceUpdatedAt);
            AddFieldMismatch(result, key, row.Id, period.Id, row.UserId, "userId", row.UserId, period.AssigneeUserId);
            AddFieldMismatch(result, key, row.Id, period.Id, row.UserId, "assigneeUserId", row.AssigneeUserId, period.AssigneeUserId);
            AddFieldMismatch(result, key, row.Id, period.Id, row.UserId, "workId", row.WorkId, period.WorkId);
            AddFieldMismatch(result, key, row.Id, period.Id, row.UserId, "assignmentId", row.AssignmentId, period.WorkAssignmentId);
            AddFieldMismatch(result, key, row.Id, period.Id, row.UserId, "currentReportId", row.CurrentReportId, NullIfWhiteSpace(period.CurrentReportId ?? report?.Id));
            AddFieldMismatch(result, key, row.Id, period.Id, row.UserId, "periodStatus", row.PeriodStatus, period.Status);
            AddFieldMismatch(result, key, row.Id, period.Id, row.UserId, "isOverdue", row.IsOverdue, period.IsOverdue);
            AddFieldMismatch(result, key, row.Id, period.Id, row.UserId, "reportStatus", row.ReportStatus, report?.Status);
            AddFieldMismatch(result, key, row.Id, period.Id, row.UserId, "isCurrentReport", row.IsCurrentReport, report?.IsCurrent ?? false);
        }

        await AddMissingMyReportPeriodIssuesAsync(result, scope, ct);
        return result;
    }

    private async Task<DocRoleReadModelDriftListResult> CheckMyReportTemplateListAsync(
        DriftScope scope,
        CancellationToken ct)
    {
        var result = NewList("MyReportTemplateListDocRole", "my_report_template_list_doc_roles", "userId + workId + dynamicFormTemplateId");
        var fb = Builders<MyReportTemplateListDocRole>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);
        if (!string.IsNullOrWhiteSpace(scope.WorkId))
            filter &= fb.Eq(x => x.WorkId, scope.WorkId);
        if (!string.IsNullOrWhiteSpace(scope.UserId))
            filter &= fb.Eq(x => x.UserId, scope.UserId);

        var rows = await LoadRowsAsync(_ctx.MyReportTemplateListDocRoles, filter, result, scope.Limit, ct);
        AddDuplicateIssues(result, rows, x => $"{x.UserId}:{x.WorkId}:{x.DynamicFormTemplateId}", x => x.Id, "userId+workId+dynamicFormTemplateId");

        foreach (var row in rows)
        {
            var key = $"{row.UserId}:{row.WorkId}:{row.DynamicFormTemplateId}";
            var periodRows = await _ctx.MyReportPeriodListDocRoles
                .Find(x =>
                    x.UserId == row.UserId &&
                    x.WorkId == row.WorkId &&
                    x.DynamicFormTemplateId == row.DynamicFormTemplateId &&
                    !x.IsDeleted)
                .ToListAsync(ct);

            if (periodRows.Count == 0)
            {
                AddIssue(result, "ORPHAN_AGGREGATE", key, row.Id, row.DynamicFormTemplateId, row.UserId,
                    "Template projection has no active my-report period rows.");
                continue;
            }

            var latest = periodRows
                .OrderByDescending(x => x.DueAtUtc ?? x.SortUpdatedAtUtc)
                .ThenByDescending(x => x.SortUpdatedAtUtc)
                .First();
            var expectedBindingCount = periodRows.Select(x => x.WorkTemplateAssigneeId).Distinct(StringComparer.Ordinal).Count();
            var expectedReportCount = periodRows.Count(x => !string.IsNullOrWhiteSpace(x.CurrentReportId));
            var expectedHasOverdue = periodRows.Any(x => x.IsOverdue);
            var sourceUpdatedAt = periodRows.Max(x => x.UpdatedAtUtc);

            AddStaleIssueIfNeeded(result, key, row.Id, latest.WorkReportPeriodId, row.UserId, row.UpdatedAtUtc, sourceUpdatedAt);
            AddFieldMismatch(result, key, row.Id, latest.WorkReportPeriodId, row.UserId, "bindingCount", row.BindingCount, expectedBindingCount);
            AddFieldMismatch(result, key, row.Id, latest.WorkReportPeriodId, row.UserId, "periodCount", row.PeriodCount, periodRows.Count);
            AddFieldMismatch(result, key, row.Id, latest.WorkReportPeriodId, row.UserId, "reportCount", row.ReportCount, expectedReportCount);
            AddFieldMismatch(result, key, row.Id, latest.WorkReportPeriodId, row.UserId, "latestPeriodId", row.LatestPeriodId, latest.WorkReportPeriodId);
            AddFieldMismatch(result, key, row.Id, latest.WorkReportPeriodId, row.UserId, "latestReportId", row.LatestReportId, latest.CurrentReportId);
            AddFieldMismatch(result, key, row.Id, latest.WorkReportPeriodId, row.UserId, "latestUpdatedAtUtc", row.LatestUpdatedAtUtc, latest.SortUpdatedAtUtc);
            AddFieldMismatch(result, key, row.Id, latest.WorkReportPeriodId, row.UserId, "hasOverduePeriod", row.HasOverduePeriod, expectedHasOverdue);
        }

        await AddMissingMyReportTemplateIssuesAsync(result, scope, ct);
        return result;
    }

    private async Task<DocRoleReadModelDriftListResult> CheckReviewReportListAsync(
        DriftScope scope,
        CancellationToken ct)
    {
        var result = NewList("ReviewReportListDocRole", "review_report_list_doc_roles", "reviewerUserId + workReportPeriodId");
        var fb = Builders<ReviewReportListDocRole>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);
        if (!string.IsNullOrWhiteSpace(scope.WorkId))
            filter &= fb.Eq(x => x.WorkId, scope.WorkId);
        if (!string.IsNullOrWhiteSpace(scope.AssignmentId))
            filter &= fb.Eq(x => x.AssignmentId, scope.AssignmentId);
        if (!string.IsNullOrWhiteSpace(scope.WorkReportPeriodId))
            filter &= fb.Eq(x => x.WorkReportPeriodId, scope.WorkReportPeriodId);
        if (!string.IsNullOrWhiteSpace(scope.UserId))
            filter &= fb.Eq(x => x.ReviewerUserId, scope.UserId);

        var rows = await LoadRowsAsync(_ctx.ReviewReportListDocRoles, filter, result, scope.Limit, ct);
        AddDuplicateIssues(result, rows, x => $"{x.ReviewerUserId}:{x.WorkReportPeriodId}", x => x.Id, "reviewerUserId+workReportPeriodId");

        var periodIds = rows.Select(x => x.WorkReportPeriodId).Where(NotBlank).Distinct(StringComparer.Ordinal).ToList();
        if (!string.IsNullOrWhiteSpace(scope.WorkReportPeriodId) && !periodIds.Contains(scope.WorkReportPeriodId))
            periodIds.Add(scope.WorkReportPeriodId);

        var periods = await LoadPeriodsAsync(periodIds, ct);
        var assignmentIds = periods.Values.Select(x => x.WorkAssignmentId)
            .Concat(rows.Select(x => x.AssignmentId))
            .Where(NotBlank)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var assignments = await LoadAssignmentsAsync(assignmentIds, ct);

        foreach (var row in rows)
        {
            var key = $"{row.ReviewerUserId}:{row.WorkReportPeriodId}";
            if (!periods.TryGetValue(row.WorkReportPeriodId, out var period))
            {
                AddIssue(result, "ORPHAN_SOURCE", key, row.Id, row.WorkReportPeriodId, row.ReviewerUserId,
                    "Projection row points to a missing or deleted WorkReportPeriod.");
                continue;
            }

            var assignment = assignments.GetValueOrDefault(period.WorkAssignmentId);
            var binding = await LoadBindingAsync(period.WorkTemplateAssigneeId, ct);
            var report = await LoadCurrentReportAsync(period, ct);
            var reviewers = BuildReviewerIds(assignment, binding);
            var sourceUpdatedAt = MaxDate(period.UpdatedAtUtc, report?.UpdatedAtUtc);
            if (assignment is not null)
                sourceUpdatedAt = MaxDate(sourceUpdatedAt, assignment.UpdatedAtUtc);

            AddStaleIssueIfNeeded(result, key, row.Id, period.Id, row.ReviewerUserId, row.UpdatedAtUtc, sourceUpdatedAt);
            if (!reviewers.Contains(row.ReviewerUserId))
            {
                AddIssue(result, "REVIEWER_SET_MISMATCH", key, row.Id, period.Id, row.ReviewerUserId,
                    "Reviewer row is not in the expected assignment/binding reviewer set.",
                    new Dictionary<string, string?>
                    {
                        ["expectedReviewerIds"] = string.Join(",", reviewers),
                        ["actualReviewerId"] = row.ReviewerUserId
                    });
            }

            AddFieldMismatch(result, key, row.Id, period.Id, row.ReviewerUserId, "userId", row.UserId, row.ReviewerUserId);
            AddFieldMismatch(result, key, row.Id, period.Id, row.ReviewerUserId, "workId", row.WorkId, period.WorkId);
            AddFieldMismatch(result, key, row.Id, period.Id, row.ReviewerUserId, "assignmentId", row.AssignmentId, period.WorkAssignmentId);
            AddFieldMismatch(result, key, row.Id, period.Id, row.ReviewerUserId, "assigneeUserId", row.AssigneeUserId, period.AssigneeUserId);
            AddFieldMismatch(result, key, row.Id, period.Id, row.ReviewerUserId, "currentReportId", row.CurrentReportId, NullIfWhiteSpace(period.CurrentReportId ?? report?.Id));
            AddFieldMismatch(result, key, row.Id, period.Id, row.ReviewerUserId, "periodStatus", row.PeriodStatus, period.Status);
            AddFieldMismatch(result, key, row.Id, period.Id, row.ReviewerUserId, "isOverdue", row.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(period.Status));
            AddFieldMismatch(result, key, row.Id, period.Id, row.ReviewerUserId, "reportStatus", row.ReportStatus, report?.Status);

            if (assignment is not null)
            {
                AddFieldMismatch(result, key, row.Id, period.Id, row.ReviewerUserId, "progressStatus", row.ProgressStatus, assignment.ProgressStatus);
                AddFieldMismatch(result, key, row.Id, period.Id, row.ReviewerUserId, "hasOverduePeriod", row.HasOverduePeriod, assignment.HasOverduePeriod);
                AddFieldMismatch(result, key, row.Id, period.Id, row.ReviewerUserId, "worstPeriodStatus", row.WorstPeriodStatus, assignment.WorstPeriodStatus);
            }
        }

        await AddMissingReviewReportIssuesAsync(result, scope, ct);
        return result;
    }

    private async Task AddMissingDocRoleProjectionIssuesAsync(
        DocRoleReadModelDriftListResult result,
        DocType docType,
        IEnumerable<string> docIds,
        string? userId,
        Func<string, string, Task<long>> countProjection,
        int limit,
        CancellationToken ct)
    {
        var ids = CleanIds(docIds);
        if (ids.Count == 0)
            return;

        var fb = Builders<DocRole>.Filter;
        var filter = fb.Eq(x => x.DocType, docType)
                     & fb.In(x => x.DocId, ids)
                     & fb.Eq(x => x.IsDeleted, false);
        if (!string.IsNullOrWhiteSpace(userId))
            filter &= fb.Eq(x => x.UserId, userId);

        var expected = await _ctx.DocRoles
            .Find(filter)
            .Limit(limit)
            .ToListAsync(ct);

        foreach (var group in expected.GroupBy(x => new { x.DocId, x.UserId }))
        {
            if (await countProjection(group.Key.DocId, group.Key.UserId) > 0)
                continue;

            AddIssue(result, "MISSING_PROJECTION", $"{group.Key.UserId}:{group.Key.DocId}", null, group.Key.DocId, group.Key.UserId,
                "Active DocRole source row exists but the matching list projection row is missing.");
        }
    }

    private async Task AddMissingMyReportPeriodIssuesAsync(
        DocRoleReadModelDriftListResult result,
        DriftScope scope,
        CancellationToken ct)
    {
        var periods = await LoadScopedPeriodsAsync(scope, ct);
        foreach (var period in periods.Where(x => !string.IsNullOrWhiteSpace(x.AssigneeUserId)))
        {
            if (!string.IsNullOrWhiteSpace(scope.UserId) &&
                !string.Equals(scope.UserId, period.AssigneeUserId, StringComparison.Ordinal))
                continue;

            var exists = await _ctx.MyReportPeriodListDocRoles.CountDocumentsAsync(
                x => x.WorkReportPeriodId == period.Id &&
                     x.UserId == period.AssigneeUserId &&
                     !x.IsDeleted,
                cancellationToken: ct);
            if (exists > 0)
                continue;

            AddIssue(result, "MISSING_PROJECTION", $"{period.AssigneeUserId}:{period.Id}", null, period.Id, period.AssigneeUserId,
                "Active WorkReportPeriod exists but the assignee my-report period projection row is missing.");
        }
    }

    private async Task AddMissingMyReportTemplateIssuesAsync(
        DocRoleReadModelDriftListResult result,
        DriftScope scope,
        CancellationToken ct)
    {
        var periods = await LoadScopedPeriodsAsync(scope, ct);
        var groups = periods
            .Where(x => !string.IsNullOrWhiteSpace(x.AssigneeUserId))
            .Where(x => !string.IsNullOrWhiteSpace(x.DynamicFormTemplateId))
            .Where(x => string.IsNullOrWhiteSpace(scope.UserId) || string.Equals(scope.UserId, x.AssigneeUserId, StringComparison.Ordinal))
            .GroupBy(x => new { x.AssigneeUserId, x.WorkId, x.DynamicFormTemplateId });

        foreach (var group in groups)
        {
            var exists = await _ctx.MyReportTemplateListDocRoles.CountDocumentsAsync(
                x => x.UserId == group.Key.AssigneeUserId &&
                     x.WorkId == group.Key.WorkId &&
                     x.DynamicFormTemplateId == group.Key.DynamicFormTemplateId &&
                     !x.IsDeleted,
                cancellationToken: ct);
            if (exists > 0)
                continue;

            AddIssue(result, "MISSING_PROJECTION", $"{group.Key.AssigneeUserId}:{group.Key.WorkId}:{group.Key.DynamicFormTemplateId}", null, group.First().Id, group.Key.AssigneeUserId,
                "Active WorkReportPeriod rows exist but the my-report template aggregate projection row is missing.");
        }
    }

    private async Task AddMissingReviewReportIssuesAsync(
        DocRoleReadModelDriftListResult result,
        DriftScope scope,
        CancellationToken ct)
    {
        var periods = await LoadScopedPeriodsAsync(scope, ct);
        foreach (var period in periods)
        {
            var assignment = await _ctx.WorkAssignments
                .Find(x => x.Id == period.WorkAssignmentId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            var binding = await LoadBindingAsync(period.WorkTemplateAssigneeId, ct);
            var reviewers = BuildReviewerIds(assignment, binding);
            foreach (var reviewerId in reviewers)
            {
                if (!string.IsNullOrWhiteSpace(scope.UserId) &&
                    !string.Equals(scope.UserId, reviewerId, StringComparison.Ordinal))
                    continue;

                var exists = await _ctx.ReviewReportListDocRoles.CountDocumentsAsync(
                    x => x.WorkReportPeriodId == period.Id &&
                         x.ReviewerUserId == reviewerId &&
                         !x.IsDeleted,
                    cancellationToken: ct);
                if (exists > 0)
                    continue;

                AddIssue(result, "MISSING_PROJECTION", $"{reviewerId}:{period.Id}", null, period.Id, reviewerId,
                    "Expected reviewer row from assignment/binding creator set is missing.");
            }
        }
    }

    private async Task<List<WorkReportPeriod>> LoadScopedPeriodsAsync(DriftScope scope, CancellationToken ct)
    {
        var fb = Builders<WorkReportPeriod>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);
        if (!string.IsNullOrWhiteSpace(scope.WorkReportPeriodId))
            filter &= fb.Eq(x => x.Id, scope.WorkReportPeriodId);
        if (!string.IsNullOrWhiteSpace(scope.AssignmentId))
            filter &= fb.Eq(x => x.WorkAssignmentId, scope.AssignmentId);
        if (!string.IsNullOrWhiteSpace(scope.WorkId))
            filter &= fb.Eq(x => x.WorkId, scope.WorkId);

        return await _ctx.WorkReportPeriods
            .Find(filter)
            .SortBy(x => x.Id)
            .Limit(scope.Limit)
            .ToListAsync(ct);
    }

    private async Task<DriftScope> ResolveScopeAsync(DocRoleReadModelDriftOptions? options, CancellationToken ct)
    {
        options ??= new DocRoleReadModelDriftOptions();
        var limit = options.Limit <= 0 ? DefaultLimit : Math.Min(options.Limit, MaxLimit);
        var workId = NullIfWhiteSpace(options.WorkId);
        var assignmentId = NullIfWhiteSpace(options.AssignmentId);
        var periodId = NullIfWhiteSpace(options.WorkReportPeriodId);

        if (!string.IsNullOrWhiteSpace(periodId))
        {
            var period = await _ctx.WorkReportPeriods
                .Find(x => x.Id == periodId)
                .FirstOrDefaultAsync(ct);
            if (period is not null)
            {
                workId ??= period.WorkId;
                assignmentId ??= period.WorkAssignmentId;
            }
        }

        if (!string.IsNullOrWhiteSpace(assignmentId))
        {
            var assignment = await _ctx.WorkAssignments
                .Find(x => x.Id == assignmentId)
                .FirstOrDefaultAsync(ct);
            if (assignment is not null)
                workId ??= assignment.WorkId;
        }

        return new DriftScope(workId, assignmentId, periodId, NullIfWhiteSpace(options.UserId), limit);
    }

    private async Task<List<T>> LoadRowsAsync<T>(
        IMongoCollection<T> collection,
        FilterDefinition<T> filter,
        DocRoleReadModelDriftListResult result,
        int limit,
        CancellationToken ct) where T : BaseEntity
    {
        result.ActiveRowCount = await collection.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await collection.Find(filter).Limit(limit + 1).ToListAsync(ct);
        result.Truncated = rows.Count > limit;
        if (result.Truncated)
            rows = rows.Take(limit).ToList();
        result.ScannedRowCount = rows.Count;
        return rows;
    }

    private async Task<Dictionary<string, Work>> LoadWorksAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var clean = CleanIds(ids);
        if (clean.Count == 0)
            return new Dictionary<string, Work>(StringComparer.Ordinal);

        var rows = await _ctx.Works
            .Find(x => clean.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(ct);
        return rows.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, WorkAssignment>> LoadAssignmentsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var clean = CleanIds(ids);
        if (clean.Count == 0)
            return new Dictionary<string, WorkAssignment>(StringComparer.Ordinal);

        var rows = await _ctx.WorkAssignments
            .Find(x => clean.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(ct);
        return rows.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, WorkReportPeriod>> LoadPeriodsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var clean = CleanIds(ids);
        if (clean.Count == 0)
            return new Dictionary<string, WorkReportPeriod>(StringComparer.Ordinal);

        var rows = await _ctx.WorkReportPeriods
            .Find(x => clean.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(ct);
        return rows.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, List<DocRoleType>>> LoadDocRolesAsync(
        DocType docType,
        IEnumerable<string> docIds,
        CancellationToken ct)
    {
        var clean = CleanIds(docIds);
        if (clean.Count == 0)
            return new Dictionary<string, List<DocRoleType>>(StringComparer.Ordinal);

        var rows = await _ctx.DocRoles
            .Find(x => x.DocType == docType && clean.Contains(x.DocId) && !x.IsDeleted)
            .ToListAsync(ct);

        return rows
            .GroupBy(x => $"{x.DocId}:{x.UserId}", StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => x.Select(r => r.Role).Distinct().OrderBy(r => (int)r).ToList(),
                StringComparer.Ordinal);
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

    private async Task<WorkTemplateAssignee?> LoadBindingAsync(string? bindingId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bindingId))
            return null;

        return await _ctx.WorkTemplateAssignees
            .Find(x => x.Id == bindingId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
    }

    private static List<string> BuildReviewerIds(WorkAssignment? assignment, WorkTemplateAssignee? binding)
        => new[] { assignment?.CreatedByUserId }
            .Where(NotBlank)
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static DocRoleReadModelDriftListResult NewList(string name, string collection, string key)
        => new()
        {
            Name = name,
            Collection = collection,
            UniqueKey = key
        };

    private static void AddDuplicateIssues<T>(
        DocRoleReadModelDriftListResult result,
        IEnumerable<T> rows,
        Func<T, string> keySelector,
        Func<T, string?> idSelector,
        string keyName)
    {
        foreach (var group in rows.GroupBy(keySelector).Where(x => x.Count() > 1))
        {
            AddIssue(result, "DUPLICATE_ACTIVE_KEY", group.Key, null, null, null,
                $"Multiple active projection rows share the same {keyName}.",
                new Dictionary<string, string?>
                {
                    ["projectionIds"] = string.Join(",", group.Select(idSelector).Where(NotBlank))
                });
        }
    }

    private static void AddRoleMismatch(
        DocRoleReadModelDriftListResult result,
        string key,
        string? projectionId,
        string? sourceId,
        string? userId,
        IEnumerable<DocRoleType> actualRoles,
        IReadOnlyDictionary<string, List<DocRoleType>> roles,
        string docId,
        string rowUserId)
    {
        var roleKey = $"{docId}:{rowUserId}";
        var expected = roles.TryGetValue(roleKey, out var value)
            ? value
            : new List<DocRoleType>();
        var actual = actualRoles.Distinct().OrderBy(x => (int)x).ToList();
        if (actual.Select(x => (int)x).SequenceEqual(expected.Select(x => (int)x)))
            return;

        AddIssue(result, "ROLE_SET_MISMATCH", key, projectionId, sourceId, userId,
            "Projection roles[] does not match active DocRole source rows.",
            new Dictionary<string, string?>
            {
                ["actual"] = string.Join(",", actual.Select(x => x.ToString())),
                ["expected"] = string.Join(",", expected.Select(x => x.ToString()))
            });
    }

    private static void AddListMismatch(
        DocRoleReadModelDriftListResult result,
        string key,
        string? projectionId,
        string? sourceId,
        string? userId,
        string field,
        IEnumerable<string> actual,
        IEnumerable<string> expected)
    {
        var actualList = actual.Where(NotBlank).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var expectedList = expected.Where(NotBlank).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (actualList.SequenceEqual(expectedList, StringComparer.Ordinal))
            return;

        AddIssue(result, "FIELD_MISMATCH", key, projectionId, sourceId, userId,
            $"Projection field `{field}` does not match source.",
            new Dictionary<string, string?>
            {
                ["field"] = field,
                ["actual"] = string.Join(",", actualList),
                ["expected"] = string.Join(",", expectedList)
            });
    }

    private static void AddFieldMismatch<T>(
        DocRoleReadModelDriftListResult result,
        string key,
        string? projectionId,
        string? sourceId,
        string? userId,
        string field,
        T? actual,
        T? expected)
    {
        if (EqualityComparer<T?>.Default.Equals(actual, expected))
            return;

        AddIssue(result, "FIELD_MISMATCH", key, projectionId, sourceId, userId,
            $"Projection field `{field}` does not match source.",
            new Dictionary<string, string?>
            {
                ["field"] = field,
                ["actual"] = FormatValue(actual),
                ["expected"] = FormatValue(expected)
            });
    }

    private static void AddStaleIssueIfNeeded(
        DocRoleReadModelDriftListResult result,
        string key,
        string? projectionId,
        string? sourceId,
        string? userId,
        DateTime projectionUpdatedAtUtc,
        DateTime sourceUpdatedAtUtc)
    {
        if (projectionUpdatedAtUtc >= sourceUpdatedAtUtc)
            return;

        AddIssue(result, "STALE_SOURCE", key, projectionId, sourceId, userId,
            "Projection updatedAtUtc is older than source updatedAtUtc.",
            new Dictionary<string, string?>
            {
                ["projectionUpdatedAtUtc"] = projectionUpdatedAtUtc.ToString("O"),
                ["sourceUpdatedAtUtc"] = sourceUpdatedAtUtc.ToString("O")
            });
    }

    private static void AddIssue(
        DocRoleReadModelDriftListResult result,
        string type,
        string key,
        string? projectionId,
        string? sourceId,
        string? userId,
        string message,
        Dictionary<string, string?>? fields = null)
    {
        result.Issues.Add(new DocRoleReadModelDriftIssue
        {
            Type = type,
            Key = key,
            ProjectionId = projectionId,
            SourceId = sourceId,
            UserId = userId,
            Message = message,
            Fields = fields ?? new Dictionary<string, string?>()
        });
    }

    private static List<string> CleanIds(IEnumerable<string?> ids)
        => ids.Where(NotBlank).Select(x => x!).Distinct(StringComparer.Ordinal).ToList();

    private static bool NotBlank(string? value)
        => !string.IsNullOrWhiteSpace(value);

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime MaxDate(DateTime left, DateTime? right)
        => right.HasValue && right.Value > left ? right.Value : left;

    private static string? FormatValue<T>(T? value)
        => value switch
        {
            null => null,
            DateTime dt => dt.ToString("O"),
            DateTimeOffset dto => dto.ToString("O"),
            _ => value.ToString()
        };

    private sealed record DriftScope(
        string? WorkId,
        string? AssignmentId,
        string? WorkReportPeriodId,
        string? UserId,
        int Limit);
}
