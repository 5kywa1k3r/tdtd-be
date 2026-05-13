using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DashboardModel.DTOs;
using tdtd_be.DashboardModel.DTOs.MindMap;
using tdtd_be.Data;
using tdtd_be.DTOs.Common;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.Common;

namespace tdtd_be.DashboardModel.Services;

public interface IDashboardMindMapQueryService
{
    Task<DashboardMindMapWorkResponse> GetWorkTreeAsync(
        string workId,
        DashboardMindMapScopeRequest? scope,
        int page = 0,
        int pageSize = 20,
        CancellationToken ct = default);

    Task<DashboardMindMapCursorResult<DashboardTreeNodeDto>> GetRootAssignmentsAsync(
        string workId,
        string? cursor,
        int limit = 20,
        CancellationToken ct = default);

    Task<DashboardMindMapCursorResult<DashboardTreeNodeDto>> SearchChildrenCursorAsync(
        string parentAssignmentId,
        string? cursor,
        int limit = 20,
        CancellationToken ct = default);

    Task<PagedResult<DashboardTreeNodeDto>> SearchChildrenAsync(
        string parentAssignmentId,
        DashboardMindMapNodeChildrenSearchRequest? req,
        CancellationToken ct = default);

    Task<List<DashboardMindMapTemplateGroupDto>> SearchTemplateGroupsAsync(
        string assignmentId,
        CancellationToken ct = default);

    Task<DashboardMindMapCursorResult<DashboardMindMapTemplateUserDto>> SearchTemplateUsersAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string? q,
        string? cursor,
        int limit = 5,
        CancellationToken ct = default);

    Task<DashboardMindMapCursorResult<DashboardMindMapReportRowDto>> SearchTemplateReportsAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        DashboardMindMapTemplateReportsSearchRequest? req,
        CancellationToken ct = default);

    Task<DashboardMindMapNodeSummaryDto> GetNodeSummaryAsync(
        string assignmentId,
        DashboardMindMapScopeRequest? scope,
        CancellationToken ct = default);

    Task<PagedResult<DashboardMindMapUnitRowDto>> SearchNodeUnitsAsync(
        string assignmentId,
        DashboardMindMapNodeUnitsSearchRequest? req,
        CancellationToken ct = default);

    Task<PagedResult<DashboardMindMapReportRowDto>> SearchNodeReportsAsync(
        string assignmentId,
        DashboardMindMapNodeReportsSearchRequest? req,
        CancellationToken ct = default);

    Task<PagedResult<DashboardMindMapTableMetricReportRowDto>> SearchNodeTableMetricReportsAsync(
        string assignmentId,
        DashboardMindMapTableMetricReportsSearchRequest? req,
        CancellationToken ct = default);

    Task<PagedResult<DashboardMindMapFieldMetricReportRowDto>> SearchNodeFieldMetricReportsAsync(
        string assignmentId,
        DashboardMindMapFieldMetricReportsSearchRequest? req,
        CancellationToken ct = default);

    Task<PagedResult<DashboardMindMapLabelReportRowDto>> SearchNodeLabelReportsAsync(
        string assignmentId,
        DashboardMindMapLabelReportsSearchRequest? req,
        CancellationToken ct = default);
}

public sealed class DashboardMindMapQueryService : IDashboardMindMapQueryService
{
    private const int DefaultGraphLimit = 5;
    private const int MaxGraphLimit = 20;
    private const string BucketAll = "ALL";
    private const string BucketTodo = "TODO";
    private const string BucketDone = "DONE";
    private const string BucketPending = "PENDING";
    private const string BucketDraft = "DRAFT";
    private const string BucketSubmitted = "SUBMITTED";
    private const string BucketApproved = "APPROVED";
    private const string BucketOverdue = "OVERDUE";
    private const int LabelSummaryLimit = 8;
    private const int TableSummaryLimit = 8;
    private const int FieldSummaryLimit = 8;

    private static readonly Dictionary<string, string> BucketColors = new(StringComparer.OrdinalIgnoreCase)
    {
        [BucketTodo] = "#94a3b8",
        [BucketDone] = "#22c55e",
        [BucketPending] = "#94a3b8",
        [BucketDraft] = "#0ea5e9",
        [BucketSubmitted] = "#2563eb",
        [BucketApproved] = "#22c55e",
        [BucketOverdue] = "#ef4444",
    };

    private static readonly DocRoleType[] FullWorkReadRoles =
    {
        DocRoleType.OWNER,
        DocRoleType.LEADER_DIRECTIVE,
        DocRoleType.LEADER_WATCH,
    };

    private static readonly DocRoleType[] AssignmentReadRoles =
    {
        DocRoleType.ASSIGNEE,
        DocRoleType.ASSIGNER,
        DocRoleType.ASSIGNMENT_LEADER_WATCH,
    };

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly IDocRoleService _docRole;

    public DashboardMindMapQueryService(
        MongoDbContext ctx,
        MeAccessor me,
        IDocRoleService docRole)
    {
        _ctx = ctx;
        _me = me;
        _docRole = docRole;
    }

    private static AppException DashboardRequired(AppErrorCode code, string field, string? value = null)
        => AppExceptionFactory.BadRequest(
            code,
            new { field, value });

    private static AppException DashboardNotFound(AppErrorCode code, object details)
        => AppExceptionFactory.NotFound(code, details);

    private static AppException DashboardForbidden(AppErrorCode code, object details)
        => AppExceptionFactory.Forbidden(code, details);

    public async Task<DashboardMindMapWorkResponse> GetWorkTreeAsync(
        string workId,
        DashboardMindMapScopeRequest? scope,
        int page = 0,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        var safePage = Math.Max(page, 0);
        var safePageSize = ClampPageSize(pageSize);
        var normalizedScope = NormalizeScope(scope);

        var access = await LoadWorkAccessContextAsync(workId, me.Id, ct);
        var work = access.Work;

        var filter = BuildNodeBaseFilter(workId) & BuildRootAssignmentFilter();
        List<WorkAssignment> roots;
        long total;

        if (!access.FullAccess)
        {
            var scopedRoots = normalizedScope.HasFilters
                ? await FilterCandidatesByScopeAsync(workId, access.EntryAssignments, parent: null, normalizedScope, ct)
                : access.EntryAssignments;

            total = scopedRoots.Count;
            roots = scopedRoots
                .Skip(safePage * safePageSize)
                .Take(safePageSize)
                .ToList();
        }
        else if (!normalizedScope.HasFilters)
        {
            total = await _ctx.WorkAssignments.CountDocumentsAsync(filter, cancellationToken: ct);
            roots = await _ctx.WorkAssignments
                .Find(filter)
                .SortByDescending(x => x.HasOverduePeriod)
                .ThenByDescending(x => x.LatestDueAtUtc)
                .ThenByDescending(x => x.UpdatedAtUtc)
                .ThenBy(x => x.Path)
                .Skip(safePage * safePageSize)
                .Limit(safePageSize)
                .ToListAsync(ct);
        }
        else
        {
            var allRoots = await _ctx.WorkAssignments
                .Find(filter)
                .SortByDescending(x => x.HasOverduePeriod)
                .ThenByDescending(x => x.LatestDueAtUtc)
                .ThenByDescending(x => x.UpdatedAtUtc)
                .ThenBy(x => x.Path)
                .ToListAsync(ct);

            var scopedRoots = await FilterCandidatesByScopeAsync(
                workId,
                allRoots,
                parent: null,
                normalizedScope,
                ct);

            total = scopedRoots.Count;
            roots = scopedRoots
                .Skip(safePage * safePageSize)
                .Take(safePageSize)
                .ToList();
        }

        var workHasOverduePeriod = access.FullAccess
            ? await _ctx.WorkAssignments
                .Find(BuildNodeBaseFilter(workId) & Builders<WorkAssignment>.Filter.Eq(x => x.HasOverduePeriod, true))
                .Limit(1)
                .AnyAsync(ct)
            : access.EntryAssignments.Any(x => x.HasOverduePeriod);

        return new DashboardMindMapWorkResponse
        {
            Work = MapWork(work, workHasOverduePeriod),
            RootAssignments = new PagedResult<DashboardTreeNodeDto>(
                roots.Select(MapNode).ToList(),
                total,
                safePage,
                safePageSize),
        };
    }

    public async Task<DashboardMindMapCursorResult<DashboardTreeNodeDto>> GetRootAssignmentsAsync(
        string workId,
        string? cursor,
        int limit = 20,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        var access = await LoadWorkAccessContextAsync(workId, me.Id, ct);

        var safeLimit = ClampGraphLimit(limit);
        var offset = ParseCursor(cursor);

        if (!access.FullAccess)
        {
            return BuildCursorResult(
                access.EntryAssignments
                    .Skip(offset)
                    .Take(safeLimit)
                    .Select(MapNode)
                    .ToList(),
                access.EntryAssignments.Count,
                offset,
                safeLimit);
        }

        var filter = BuildNodeBaseFilter(workId) & BuildRootAssignmentFilter();
        var total = await _ctx.WorkAssignments.CountDocumentsAsync(filter, cancellationToken: ct);

        var roots = await _ctx.WorkAssignments
            .Find(filter)
            .Sort(BuildAssignmentSort())
            .Skip(offset)
            .Limit(safeLimit)
            .ToListAsync(ct);

        return BuildCursorResult(
            roots.Select(MapNode).ToList(),
            total,
            offset,
            safeLimit);
    }

    public async Task<DashboardMindMapCursorResult<DashboardTreeNodeDto>> SearchChildrenCursorAsync(
        string parentAssignmentId,
        string? cursor,
        int limit = 20,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        var parent = await LoadAccessibleAssignmentAsync(parentAssignmentId, me.Id, ct);
        var safeLimit = ClampGraphLimit(limit);
        var offset = ParseCursor(cursor);

        var filter = BuildNodeBaseFilter(parent.WorkId)
            & Builders<WorkAssignment>.Filter.Eq(x => x.ParentAssignmentId, parent.Id);
        var total = await _ctx.WorkAssignments.CountDocumentsAsync(filter, cancellationToken: ct);

        var children = await _ctx.WorkAssignments
            .Find(filter)
            .Sort(BuildAssignmentSort())
            .Skip(offset)
            .Limit(safeLimit)
            .ToListAsync(ct);

        return BuildCursorResult(
            children.Select(MapNode).ToList(),
            total,
            offset,
            safeLimit);
    }

    public async Task<PagedResult<DashboardTreeNodeDto>> SearchChildrenAsync(
        string parentAssignmentId,
        DashboardMindMapNodeChildrenSearchRequest? req,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        req ??= new DashboardMindMapNodeChildrenSearchRequest();

        var safePage = Math.Max(req.Page, 0);
        var safePageSize = ClampPageSize(req.PageSize);
        var normalizedScope = NormalizeScope(req);

        var parent = await LoadAccessibleAssignmentAsync(parentAssignmentId, me.Id, ct);

        var filter = BuildNodeBaseFilter(parent.WorkId)
            & Builders<WorkAssignment>.Filter.Eq(x => x.ParentAssignmentId, parent.Id);
        List<WorkAssignment> items;
        int total;

        if (!normalizedScope.HasFilters)
        {
            total = (int)await _ctx.WorkAssignments.CountDocumentsAsync(filter, cancellationToken: ct);
            items = await _ctx.WorkAssignments
                .Find(filter)
                .SortByDescending(x => x.HasOverduePeriod)
                .ThenByDescending(x => x.LatestDueAtUtc)
                .ThenByDescending(x => x.UpdatedAtUtc)
                .ThenBy(x => x.Path)
                .Skip(safePage * safePageSize)
                .Limit(safePageSize)
                .ToListAsync(ct);
        }
        else
        {
            var allChildren = await _ctx.WorkAssignments
                .Find(filter)
                .SortByDescending(x => x.HasOverduePeriod)
                .ThenByDescending(x => x.LatestDueAtUtc)
                .ThenByDescending(x => x.UpdatedAtUtc)
                .ThenBy(x => x.Path)
                .ToListAsync(ct);

            var scopedChildren = await FilterCandidatesByScopeAsync(
                parent.WorkId,
                allChildren,
                parent,
                normalizedScope,
                ct);

            total = scopedChildren.Count;
            items = scopedChildren
                .Skip(safePage * safePageSize)
                .Take(safePageSize)
                .ToList();
        }

        return new PagedResult<DashboardTreeNodeDto>(
            items.Select(MapNode).ToList(),
            total,
            safePage,
            safePageSize);
    }

    public async Task<List<DashboardMindMapTemplateGroupDto>> SearchTemplateGroupsAsync(
        string assignmentId,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        var node = await LoadAccessibleAssignmentAsync(assignmentId, me.Id, ct);

        var bindings = await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkAssignmentId == node.Id &&
                x.IsActive &&
                !x.IsDeleted)
            .ToListAsync(ct);

        var periods = await _ctx.WorkReportPeriods
            .Find(x =>
                x.WorkAssignmentId == node.Id &&
                x.IsActive &&
                !x.IsDeleted)
            .ToListAsync(ct);

        var templateIds = bindings
            .Select(x => x.DynamicFormTemplateId)
            .Concat(periods.Select(x => x.DynamicFormTemplateId))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return templateIds
            .Select(templateId =>
            {
                var templateBindings = bindings
                    .Where(x => string.Equals(x.DynamicFormTemplateId, templateId, StringComparison.Ordinal))
                    .ToList();
                var templatePeriods = periods
                    .Where(x => string.Equals(x.DynamicFormTemplateId, templateId, StringComparison.Ordinal))
                    .ToList();
                var sampleBinding = templateBindings.FirstOrDefault();
                var samplePeriod = templatePeriods.FirstOrDefault();

                return new DashboardMindMapTemplateGroupDto
                {
                    AssignmentId = node.Id,
                    DynamicFormTemplateId = templateId,
                    DynamicFormTemplateCode = sampleBinding?.DynamicFormTemplateCode ?? samplePeriod?.DynamicFormTemplateCode ?? node.DynamicFormTemplateCode ?? string.Empty,
                    DynamicFormTemplateName = sampleBinding?.DynamicFormTemplateName ?? samplePeriod?.DynamicFormTemplateName ?? node.DynamicFormTemplateName ?? string.Empty,
                    DynamicExcelId = sampleBinding?.DynamicExcelId ?? samplePeriod?.DynamicExcelId ?? node.DynamicExcelId,
                    DynamicExcelCode = sampleBinding?.DynamicExcelCode ?? samplePeriod?.DynamicExcelCode ?? node.DynamicExcelCode,
                    DynamicExcelName = sampleBinding?.DynamicExcelName ?? samplePeriod?.DynamicExcelName ?? node.DynamicExcelName,
                    UserCount = templateBindings
                        .Select(x => x.AssigneeUserId)
                        .Concat(templatePeriods.Select(x => x.AssigneeUserId))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .Count(),
                    ReportCount = templatePeriods.Count,
                    OverdueCount = templatePeriods.Count(x => string.Equals(MapPeriodBucket(x.Status), BucketOverdue, StringComparison.OrdinalIgnoreCase)),
                    LatestDueAtUtc = templatePeriods
                        .Where(x => x.DueAtUtc.HasValue)
                        .OrderByDescending(x => x.DueAtUtc)
                        .Select(x => x.DueAtUtc)
                        .FirstOrDefault(),
                    ReportBar = BuildReportBar(BuildReportSummary(templatePeriods)),
                };
            })
            .OrderByDescending(x => x.OverdueCount)
            .ThenByDescending(x => x.ReportCount)
            .ThenBy(x => x.DynamicFormTemplateName)
            .ToList();
    }

    public async Task<DashboardMindMapCursorResult<DashboardMindMapTemplateUserDto>> SearchTemplateUsersAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string? q,
        string? cursor,
        int limit = 5,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        var node = await LoadAccessibleAssignmentAsync(assignmentId, me.Id, ct);
        var safeLimit = ClampGraphLimit(limit <= 0 ? DefaultGraphLimit : limit);
        var offset = ParseCursor(cursor);

        var bindings = await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkAssignmentId == node.Id &&
                x.DynamicFormTemplateId == dynamicFormTemplateId &&
                x.IsActive &&
                !x.IsDeleted)
            .ToListAsync(ct);

        var periods = await _ctx.WorkReportPeriods
            .Find(x =>
                x.WorkAssignmentId == node.Id &&
                x.DynamicFormTemplateId == dynamicFormTemplateId &&
                x.IsActive &&
                !x.IsDeleted)
            .ToListAsync(ct);

        var periodsByUser = periods
            .GroupBy(x => x.AssigneeUserId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);

        var rows = bindings
            .Select(x => MapTemplateUser(node.Id, dynamicFormTemplateId, x, periodsByUser.GetValueOrDefault(x.AssigneeUserId) ?? new List<WorkReportPeriod>()))
            .ToList();

        var missingPeriodUsers = periodsByUser.Keys
            .Where(userId => !rows.Any(x => string.Equals(x.AssigneeUserId, userId, StringComparison.Ordinal)))
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .ToList();

        foreach (var userId in missingPeriodUsers)
        {
            rows.Add(MapTemplateUserFromPeriods(node.Id, dynamicFormTemplateId, userId, periodsByUser[userId]));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var text = q.Trim().ToLowerInvariant();
            rows = rows
                .Where(x =>
                    x.AssigneeUserId.ToLowerInvariant().Contains(text) ||
                    x.AssigneeUsername.ToLowerInvariant().Contains(text) ||
                    x.AssigneeFullName.ToLowerInvariant().Contains(text) ||
                    (x.UnitLabel ?? string.Empty).ToLowerInvariant().Contains(text))
                .ToList();
        }

        rows = rows
            .OrderByDescending(x => x.OverdueCount)
            .ThenByDescending(x => x.TotalReports)
            .ThenBy(x => x.AssigneeFullName)
            .ToList();

        return BuildCursorResult(rows.Skip(offset).Take(safeLimit).ToList(), rows.Count, offset, safeLimit);
    }

    public async Task<DashboardMindMapCursorResult<DashboardMindMapReportRowDto>> SearchTemplateReportsAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        DashboardMindMapTemplateReportsSearchRequest? req,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        var node = await LoadAccessibleAssignmentAsync(assignmentId, me.Id, ct);
        req ??= new DashboardMindMapTemplateReportsSearchRequest();

        var safeLimit = ClampGraphLimit(req.Limit <= 0 ? DefaultGraphLimit : req.Limit);
        var offset = ParseCursor(req.Cursor);
        var assigneeUserIds = NormalizeIds(req.AssigneeUserIds);
        var statusBuckets = NormalizeReportBuckets(req.StatusBuckets);

        var filter = Builders<WorkReportPeriod>.Filter.Eq(x => x.WorkAssignmentId, node.Id)
            & Builders<WorkReportPeriod>.Filter.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId)
            & Builders<WorkReportPeriod>.Filter.Eq(x => x.IsDeleted, false)
            & Builders<WorkReportPeriod>.Filter.Eq(x => x.IsActive, true);

        if (assigneeUserIds.Count > 0)
            filter &= Builders<WorkReportPeriod>.Filter.In(x => x.AssigneeUserId, assigneeUserIds);

        if (statusBuckets.Count > 0)
            filter &= Builders<WorkReportPeriod>.Filter.In(x => x.Status, MapReportBucketsToPeriodStatuses(statusBuckets));

        if (req.FromUtc.HasValue)
            filter &= Builders<WorkReportPeriod>.Filter.Gte(x => x.DueAtUtc, req.FromUtc.Value);

        if (req.ToUtc.HasValue)
            filter &= Builders<WorkReportPeriod>.Filter.Lte(x => x.DueAtUtc, req.ToUtc.Value);

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var regex = new BsonRegularExpression(Regex.Escape(req.Q.Trim()), "i");
            var fb = Builders<WorkReportPeriod>.Filter;
            filter &= fb.Or(
                fb.Regex(x => x.PeriodKey, regex),
                fb.Regex(x => x.CurrentProgressStatus, regex),
                fb.Regex(x => x.ReportReason, regex),
                fb.Regex(x => x.Difficulties, regex),
                fb.Regex(x => x.ProposedSolution, regex),
                fb.Regex(x => x.LateReason, regex),
                fb.Regex(x => x.ReviewerComment, regex),
                fb.Regex(x => x.ReviewerEvaluation, regex));
        }

        var total = await _ctx.WorkReportPeriods.CountDocumentsAsync(filter, cancellationToken: ct);
        var periods = await _ctx.WorkReportPeriods
            .Find(filter)
            .SortByDescending(x => x.DueAtUtc)
            .ThenByDescending(x => x.PeriodKey)
            .Skip(offset)
            .Limit(safeLimit)
            .ToListAsync(ct);

        var currentReports = new Dictionary<string, WorkAssignmentReport>(StringComparer.Ordinal);
        var currentReportIds = periods
            .Where(x => !string.IsNullOrWhiteSpace(x.CurrentReportId))
            .Select(x => x.CurrentReportId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (currentReportIds.Count > 0)
        {
            currentReports = (await _ctx.WorkAssignmentReports
                    .Find(Builders<WorkAssignmentReport>.Filter.In(x => x.Id, currentReportIds)
                          & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false)
                          & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsCurrent, true)
                          & Builders<WorkAssignmentReport>.Filter.Ne(x => x.IsActive, false))
                    .ToListAsync(ct))
                .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
        }

        var bindings = await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkAssignmentId == node.Id &&
                x.DynamicFormTemplateId == dynamicFormTemplateId &&
                x.IsActive &&
                !x.IsDeleted)
            .ToListAsync(ct);

        var bindingByUserId = bindings
            .GroupBy(x => x.AssigneeUserId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        var rows = periods
            .Select(period =>
            {
                currentReports.TryGetValue(period.CurrentReportId ?? string.Empty, out var report);
                bindingByUserId.TryGetValue(period.AssigneeUserId, out var binding);
                return MapTemplateReportRow(period, node, binding, report);
            })
            .ToList();

        return BuildCursorResult(rows, total, offset, safeLimit);
    }

    public async Task<DashboardMindMapNodeSummaryDto> GetNodeSummaryAsync(
        string assignmentId,
        DashboardMindMapScopeRequest? scope,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        var node = await LoadAccessibleAssignmentAsync(assignmentId, me.Id, ct);
        var normalizedScope = NormalizeScope(scope);
        var ctx = await LoadSubtreeContextAsync(node, normalizedScope, includeCurrentReports: false, ct);

        var unitRows = BuildUnitRows(ctx);
        var reportSummary = BuildReportSummary(ctx.Periods);
        var labelSummaries = await LoadLabelSummariesAsync(ctx, normalizedScope, ct);
        var tableSummaries = await LoadTableSummariesAsync(ctx, normalizedScope, ct);
        var fieldSummaries = await LoadFieldSummariesAsync(ctx, normalizedScope, ct);

        return new DashboardMindMapNodeSummaryDto
        {
            Node = MapNode(node),
            DescendantAssignmentCount = Math.Max(ctx.Assignments.Count - 1, 0),
            ActiveAssignmentCount = ctx.Assignments.Count,
            TotalAssigneeCount = unitRows.Count,
            ReportSummary = reportSummary,
            UnitBar = BuildUnitBar(unitRows),
            ReportBar = BuildReportBar(reportSummary),
            LabelSummaries = labelSummaries,
            TableSummaries = tableSummaries,
            FieldSummaries = fieldSummaries,
        };
    }

    private async Task<List<DashboardMindMapLabelSummaryDto>> LoadLabelSummariesAsync(
        SubtreeContext ctx,
        MindMapScope scope,
        CancellationToken ct)
    {
        var node = ctx.Node;
        var fb = Builders<WorkReportLabelStatAggregate>.Filter;
        var filter = fb.Eq(x => x.WorkId, node.WorkId)
            & fb.Eq(x => x.ScopeType, "ASSIGNMENT")
            & fb.Eq(x => x.ScopeId, node.Id)
            & fb.Eq(x => x.IsDeleted, false);

        if (scope.HasFilters)
        {
            var periodInstanceKeys = ctx.Periods
                .Select(x => x.PeriodInstanceKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (periodInstanceKeys.Count == 0)
                return new List<DashboardMindMapLabelSummaryDto>();

            filter &= fb.In(x => x.PeriodInstanceKey, periodInstanceKeys);
        }

        var docs = await _ctx.WorkReportLabelStatAggregates
            .Aggregate()
            .Match(filter)
            .Group(new BsonDocument
            {
                { "_id", "$labelCode" },
                { "labelCode", new BsonDocument("$first", "$labelCode") },
                { "rowCount", new BsonDocument("$sum", "$rowCount") },
                { "reportCount", new BsonDocument("$sum", "$reportCount") },
                { "scopeType", new BsonDocument("$first", "$scopeType") },
                { "scopeId", new BsonDocument("$first", "$scopeId") },
                { "dynamicFormTemplateId", new BsonDocument("$first", "$dynamicFormTemplateId") },
                { "dynamicFormTemplateName", new BsonDocument("$first", "$dynamicFormTemplateName") },
                { "dynamicExcelTemplateId", new BsonDocument("$first", "$dynamicExcelTemplateId") },
                { "blockId", new BsonDocument("$first", "$blockId") },
            })
            .Sort(new BsonDocument
            {
                { "rowCount", -1 },
                { "labelCode", 1 },
            })
            .Limit(LabelSummaryLimit)
            .ToListAsync(ct);

        var rows = docs
            .Select(doc => new DashboardMindMapLabelSummaryDto
            {
                LabelCode = ReadBsonString(doc, "labelCode") ?? ReadBsonString(doc, "_id") ?? string.Empty,
                RowCount = ReadBsonInt64(doc, "rowCount"),
                ReportCount = ReadBsonInt64(doc, "reportCount"),
                ScopeType = ReadBsonString(doc, "scopeType") ?? "ASSIGNMENT",
                ScopeId = ReadBsonString(doc, "scopeId") ?? node.Id,
                DynamicFormTemplateId = ReadBsonString(doc, "dynamicFormTemplateId"),
                DynamicFormTemplateName = ReadBsonString(doc, "dynamicFormTemplateName"),
                DynamicExcelTemplateId = ReadBsonString(doc, "dynamicExcelTemplateId"),
                BlockId = ReadBsonString(doc, "blockId") ?? string.Empty,
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.LabelCode))
            .ToList();

        if (rows.Count == 0)
            return rows;

        var labelCodes = rows
            .Select(x => x.LabelCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var labelFilter = Builders<LabelCatalogItem>.Filter.In(x => x.Code, labelCodes)
            & Builders<LabelCatalogItem>.Filter.Eq(x => x.IsActive, true)
            & Builders<LabelCatalogItem>.Filter.Eq(x => x.IsDeleted, false);

        var labels = (await _ctx.Labels
                .Find(labelFilter)
                .ToListAsync(ct))
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (!labels.TryGetValue(row.LabelCode, out var label))
                continue;

            row.LabelName = label.Name;
            row.LabelColor = label.Color;
        }

        return rows;
    }

    private async Task<List<DashboardMindMapTableSummaryDto>> LoadTableSummariesAsync(
        SubtreeContext ctx,
        MindMapScope scope,
        CancellationToken ct)
    {
        var node = ctx.Node;
        var fb = Builders<WorkReportTableStatAggregate>.Filter;
        var filter = fb.Eq(x => x.WorkId, node.WorkId)
            & fb.Eq(x => x.ScopeType, "ASSIGNMENT")
            & fb.Eq(x => x.ScopeId, node.Id)
            & fb.Eq(x => x.IsDeleted, false);

        if (scope.HasFilters)
        {
            var periodInstanceKeys = ctx.Periods
                .Select(x => x.PeriodInstanceKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (periodInstanceKeys.Count == 0)
                return new List<DashboardMindMapTableSummaryDto>();

            filter &= fb.In(x => x.PeriodInstanceKey, periodInstanceKeys);
        }

        var docs = await _ctx.WorkReportTableStatAggregates
            .Aggregate()
            .Match(filter)
            .Group(new BsonDocument
            {
                {
                    "_id",
                    new BsonDocument
                    {
                        { "dynamicFormTemplateId", "$dynamicFormTemplateId" },
                        { "dynamicExcelTemplateId", "$dynamicExcelTemplateId" },
                        { "blockId", "$blockId" },
                        { "tableMode", "$tableMode" },
                        { "metricKey", "$metricKey" },
                        { "rowKey", "$rowKey" },
                        { "columnKey", "$columnKey" },
                    }
                },
                { "scopeType", new BsonDocument("$first", "$scopeType") },
                { "scopeId", new BsonDocument("$first", "$scopeId") },
                { "dynamicFormTemplateId", new BsonDocument("$first", "$dynamicFormTemplateId") },
                { "dynamicFormTemplateName", new BsonDocument("$first", "$dynamicFormTemplateName") },
                { "dynamicExcelTemplateId", new BsonDocument("$first", "$dynamicExcelTemplateId") },
                { "blockId", new BsonDocument("$first", "$blockId") },
                { "tableMode", new BsonDocument("$first", "$tableMode") },
                { "metricKey", new BsonDocument("$first", "$metricKey") },
                { "rowKey", new BsonDocument("$first", "$rowKey") },
                { "columnKey", new BsonDocument("$first", "$columnKey") },
                { "valueCount", new BsonDocument("$sum", "$valueCount") },
                { "sum", new BsonDocument("$sum", "$sum") },
                { "min", new BsonDocument("$min", "$min") },
                { "max", new BsonDocument("$max", "$max") },
                { "reportCount", new BsonDocument("$sum", "$reportCount") },
            })
            .Sort(new BsonDocument
            {
                { "sum", -1 },
                { "metricKey", 1 },
            })
            .Limit(TableSummaryLimit)
            .ToListAsync(ct);

        return docs
            .Select(doc =>
            {
                var valueCount = ReadBsonInt64(doc, "valueCount");
                var sum = ReadBsonDecimal(doc, "sum") ?? 0m;

                return new DashboardMindMapTableSummaryDto
                {
                    ScopeType = ReadBsonString(doc, "scopeType") ?? "ASSIGNMENT",
                    ScopeId = ReadBsonString(doc, "scopeId") ?? node.Id,
                    DynamicFormTemplateId = ReadBsonString(doc, "dynamicFormTemplateId"),
                    DynamicFormTemplateName = ReadBsonString(doc, "dynamicFormTemplateName"),
                    DynamicExcelTemplateId = ReadBsonString(doc, "dynamicExcelTemplateId"),
                    BlockId = ReadBsonString(doc, "blockId") ?? string.Empty,
                    TableMode = ReadBsonString(doc, "tableMode") ?? string.Empty,
                    MetricKey = ReadBsonString(doc, "metricKey") ?? string.Empty,
                    RowKey = ReadBsonString(doc, "rowKey") ?? string.Empty,
                    ColumnKey = ReadBsonString(doc, "columnKey") ?? string.Empty,
                    ValueCount = valueCount,
                    Sum = sum,
                    Min = ReadBsonDecimal(doc, "min"),
                    Max = ReadBsonDecimal(doc, "max"),
                    Average = valueCount > 0 ? sum / valueCount : null,
                    ReportCount = ReadBsonInt64(doc, "reportCount"),
                };
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.MetricKey))
            .ToList();
    }

    private async Task<List<DashboardMindMapFieldSummaryDto>> LoadFieldSummariesAsync(
        SubtreeContext ctx,
        MindMapScope scope,
        CancellationToken ct)
    {
        var node = ctx.Node;
        var fb = Builders<WorkReportFieldStatAggregate>.Filter;
        var filter = fb.Eq(x => x.WorkId, node.WorkId)
            & fb.Eq(x => x.ScopeType, "ASSIGNMENT")
            & fb.Eq(x => x.ScopeId, node.Id)
            & fb.Eq(x => x.ShowInTree, true)
            & fb.Eq(x => x.IsDeleted, false);

        if (scope.HasFilters)
        {
            var periodInstanceKeys = ctx.Periods
                .Select(x => x.PeriodInstanceKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (periodInstanceKeys.Count == 0)
                return new List<DashboardMindMapFieldSummaryDto>();

            filter &= fb.In(x => x.PeriodInstanceKey, periodInstanceKeys);
        }

        var docs = await _ctx.WorkReportFieldStatAggregates
            .Aggregate()
            .Match(filter)
            .Group(new BsonDocument
            {
                {
                    "_id",
                    new BsonDocument
                    {
                        { "dynamicFormTemplateId", "$dynamicFormTemplateId" },
                        { "fieldId", "$fieldId" },
                        { "bucketKey", "$bucketKey" },
                    }
                },
                { "scopeType", new BsonDocument("$first", "$scopeType") },
                { "scopeId", new BsonDocument("$first", "$scopeId") },
                { "dynamicFormTemplateId", new BsonDocument("$first", "$dynamicFormTemplateId") },
                { "dynamicFormTemplateName", new BsonDocument("$first", "$dynamicFormTemplateName") },
                { "fieldId", new BsonDocument("$first", "$fieldId") },
                { "fieldKey", new BsonDocument("$first", "$fieldKey") },
                { "fieldLabel", new BsonDocument("$first", "$fieldLabel") },
                { "fieldType", new BsonDocument("$first", "$fieldType") },
                { "bucketKey", new BsonDocument("$first", "$bucketKey") },
                { "bucketLabel", new BsonDocument("$first", "$bucketLabel") },
                { "valueCount", new BsonDocument("$sum", "$valueCount") },
                { "numericValueCount", new BsonDocument("$sum", "$numericValueCount") },
                { "sum", new BsonDocument("$sum", "$sum") },
                { "min", new BsonDocument("$min", "$min") },
                { "max", new BsonDocument("$max", "$max") },
                { "trueCount", new BsonDocument("$sum", "$trueCount") },
                { "falseCount", new BsonDocument("$sum", "$falseCount") },
                { "latestDateUtc", new BsonDocument("$max", "$latestDateUtc") },
                { "reportCount", new BsonDocument("$sum", "$reportCount") },
            })
            .Sort(new BsonDocument
            {
                { "valueCount", -1 },
                { "fieldKey", 1 },
                { "bucketKey", 1 },
            })
            .Limit(FieldSummaryLimit)
            .ToListAsync(ct);

        return docs
            .Select(doc =>
            {
                var numericValueCount = ReadBsonInt64(doc, "numericValueCount");
                var sum = ReadBsonDecimal(doc, "sum") ?? 0m;

                return new DashboardMindMapFieldSummaryDto
                {
                    ScopeType = ReadBsonString(doc, "scopeType") ?? "ASSIGNMENT",
                    ScopeId = ReadBsonString(doc, "scopeId") ?? node.Id,
                    DynamicFormTemplateId = ReadBsonString(doc, "dynamicFormTemplateId"),
                    DynamicFormTemplateName = ReadBsonString(doc, "dynamicFormTemplateName"),
                    FieldId = ReadBsonString(doc, "fieldId") ?? string.Empty,
                    FieldKey = ReadBsonString(doc, "fieldKey") ?? string.Empty,
                    FieldLabel = ReadBsonString(doc, "fieldLabel") ?? string.Empty,
                    FieldType = ReadBsonString(doc, "fieldType") ?? string.Empty,
                    BucketKey = ReadBsonString(doc, "bucketKey"),
                    BucketLabel = ReadBsonString(doc, "bucketLabel"),
                    ValueCount = ReadBsonInt64(doc, "valueCount"),
                    NumericValueCount = numericValueCount,
                    Sum = numericValueCount > 0 ? sum : null,
                    Min = ReadBsonDecimal(doc, "min"),
                    Max = ReadBsonDecimal(doc, "max"),
                    Average = numericValueCount > 0 ? sum / numericValueCount : null,
                    TrueCount = ReadBsonInt64(doc, "trueCount"),
                    FalseCount = ReadBsonInt64(doc, "falseCount"),
                    LatestDateUtc = ReadBsonDateTime(doc, "latestDateUtc"),
                    ReportCount = ReadBsonInt64(doc, "reportCount"),
                };
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.FieldId))
            .ToList();
    }

    public async Task<PagedResult<DashboardMindMapUnitRowDto>> SearchNodeUnitsAsync(
        string assignmentId,
        DashboardMindMapNodeUnitsSearchRequest? req,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        req ??= new DashboardMindMapNodeUnitsSearchRequest();

        var safePage = Math.Max(req.Page, 0);
        var safePageSize = ClampPageSize(req.PageSize);
        var bucket = NormalizeUnitBucket(req.Bucket);
        var normalizedScope = NormalizeScope(req);

        var node = await LoadAccessibleAssignmentAsync(assignmentId, me.Id, ct);
        var ctx = await LoadSubtreeContextAsync(node, normalizedScope, includeCurrentReports: false, ct);

        var rows = BuildUnitRows(ctx);

        if (!string.Equals(bucket, BucketAll, StringComparison.OrdinalIgnoreCase))
        {
            rows = rows
                .Where(x => string.Equals(x.Bucket, bucket, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var q = req.Q.Trim().ToLowerInvariant();
            rows = rows.Where(x =>
                    (x.AssigneeFullName ?? string.Empty).ToLowerInvariant().Contains(q) ||
                    (x.AssigneeUsername ?? string.Empty).ToLowerInvariant().Contains(q) ||
                    (x.UnitLabel ?? string.Empty).ToLowerInvariant().Contains(q) ||
                    (x.LatestPeriodKey ?? string.Empty).ToLowerInvariant().Contains(q) ||
                    (x.WorstOverdueReasonLabel ?? string.Empty).ToLowerInvariant().Contains(q))
                .ToList();
        }

        rows = rows
            .OrderByDescending(x => x.OverdueCount)
            .ThenByDescending(x => x.TotalReports)
            .ThenByDescending(x => x.LatestDueAtUtc)
            .ThenBy(x => x.AssigneeFullName)
            .ToList();

        var total = rows.Count;
        var paged = rows
            .Skip(safePage * safePageSize)
            .Take(safePageSize)
            .ToList();

        return new PagedResult<DashboardMindMapUnitRowDto>(paged, total, safePage, safePageSize);
    }

    public async Task<PagedResult<DashboardMindMapReportRowDto>> SearchNodeReportsAsync(
        string assignmentId,
        DashboardMindMapNodeReportsSearchRequest? req,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        req ??= new DashboardMindMapNodeReportsSearchRequest();

        var safePage = Math.Max(req.Page, 0);
        var safePageSize = ClampPageSize(req.PageSize);
        var bucket = NormalizeReportBucket(req.Bucket);
        var normalizedScope = NormalizeScope(req);

        var node = await LoadAccessibleAssignmentAsync(assignmentId, me.Id, ct);
        var ctx = await LoadSubtreeContextAsync(node, normalizedScope, includeCurrentReports: true, ct);

        var rows = BuildReportRows(ctx);

        if (!string.Equals(bucket, BucketAll, StringComparison.OrdinalIgnoreCase))
        {
            rows = rows
                .Where(x => IsReportBucketMatch(x.Bucket, bucket))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var q = req.Q.Trim().ToLowerInvariant();
            rows = rows.Where(x =>
                    (x.AssignmentCode ?? string.Empty).ToLowerInvariant().Contains(q) ||
                    (x.AssignmentName ?? string.Empty).ToLowerInvariant().Contains(q) ||
                    (x.AssigneeFullName ?? string.Empty).ToLowerInvariant().Contains(q) ||
                    (x.AssigneeUsername ?? string.Empty).ToLowerInvariant().Contains(q) ||
                    (x.UnitLabel ?? string.Empty).ToLowerInvariant().Contains(q) ||
                    (x.PeriodKey ?? string.Empty).ToLowerInvariant().Contains(q))
                .ToList();
        }

        rows = rows
            .OrderByDescending(x => string.Equals(x.Bucket, BucketOverdue, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.DueAtUtc)
            .ThenBy(x => x.PeriodKey)
            .ThenBy(x => x.AssigneeFullName)
            .ToList();

        var total = rows.Count;
        var paged = rows
            .Skip(safePage * safePageSize)
            .Take(safePageSize)
            .ToList();

        return new PagedResult<DashboardMindMapReportRowDto>(paged, total, safePage, safePageSize);
    }

    public async Task<PagedResult<DashboardMindMapTableMetricReportRowDto>> SearchNodeTableMetricReportsAsync(
        string assignmentId,
        DashboardMindMapTableMetricReportsSearchRequest? req,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        req ??= new DashboardMindMapTableMetricReportsSearchRequest();

        if (string.IsNullOrWhiteSpace(req.MetricKey))
            throw DashboardRequired(AppErrorCode.DASHBOARD_TABLE_METRIC_KEY_REQUIRED, nameof(req.MetricKey), req.MetricKey);

        var safePage = Math.Max(req.Page, 0);
        var safePageSize = ClampPageSize(req.PageSize);
        var normalizedScope = NormalizeScope(req);
        var node = await LoadAccessibleAssignmentAsync(assignmentId, me.Id, ct);
        var ctx = await LoadSubtreeContextAsync(node, normalizedScope, includeCurrentReports: false, ct);

        var assignmentIds = ctx.Assignments
            .Select(x => x.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (assignmentIds.Count == 0)
        {
            return new PagedResult<DashboardMindMapTableMetricReportRowDto>(
                new List<DashboardMindMapTableMetricReportRowDto>(),
                0,
                safePage,
                safePageSize);
        }

        var periodInstanceKeys = normalizedScope.HasFilters
            ? ctx.Periods
                .Select(x => x.PeriodInstanceKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : new List<string>();

        if (normalizedScope.HasFilters && periodInstanceKeys.Count == 0)
        {
            return new PagedResult<DashboardMindMapTableMetricReportRowDto>(
                new List<DashboardMindMapTableMetricReportRowDto>(),
                0,
                safePage,
                safePageSize);
        }

        var filter = BuildTableMetricValueFilter(node.WorkId, assignmentIds, periodInstanceKeys, req);
        var groupId = BuildTableMetricReportGroupId();

        var totalResult = await _ctx.WorkReportTableStatValues
            .Aggregate()
            .Match(filter)
            .Group(new BsonDocument { { "_id", groupId } })
            .Count()
            .FirstOrDefaultAsync(ct);

        var total = totalResult?.Count ?? 0;
        if (total == 0)
        {
            return new PagedResult<DashboardMindMapTableMetricReportRowDto>(
                new List<DashboardMindMapTableMetricReportRowDto>(),
                0,
                safePage,
                safePageSize);
        }

        var docs = await _ctx.WorkReportTableStatValues
            .Aggregate()
            .Match(filter)
            .Group(new BsonDocument
            {
                { "_id", groupId },
                { "workAssignmentReportId", new BsonDocument("$first", "$workAssignmentReportId") },
                { "workReportPeriodId", new BsonDocument("$first", "$workReportPeriodId") },
                { "assignmentId", new BsonDocument("$first", "$workAssignmentId") },
                { "blockId", new BsonDocument("$first", "$blockId") },
                { "tableMode", new BsonDocument("$first", "$tableMode") },
                { "metricKey", new BsonDocument("$first", "$metricKey") },
                { "rowKey", new BsonDocument("$first", "$rowKey") },
                { "columnKey", new BsonDocument("$first", "$columnKey") },
                { "periodKey", new BsonDocument("$first", "$periodKey") },
                { "periodInstanceKey", new BsonDocument("$first", "$periodInstanceKey") },
                { "periodKind", new BsonDocument("$first", "$periodKind") },
                { "reportStatus", new BsonDocument("$first", "$reportStatus") },
                { "valueCount", new BsonDocument("$sum", 1) },
                { "sum", new BsonDocument("$sum", "$value") },
                { "min", new BsonDocument("$min", "$value") },
                { "max", new BsonDocument("$max", "$value") },
                { "sourceKeys", new BsonDocument("$addToSet", "$sourceKey") },
            })
            .Sort(new BsonDocument
            {
                { "sum", -1 },
                { "periodInstanceKey", 1 },
                { "workAssignmentReportId", 1 },
            })
            .Skip(safePage * safePageSize)
            .Limit(safePageSize)
            .ToListAsync(ct);

        var reportIds = docs
            .Select(x => ReadBsonString(x, "workAssignmentReportId"))
            .OfType<string>()
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var periodIds = docs
            .Select(x => ReadBsonString(x, "workReportPeriodId"))
            .OfType<string>()
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var reportsById = reportIds.Count == 0
            ? new Dictionary<string, WorkAssignmentReport>(StringComparer.Ordinal)
            : (await _ctx.WorkAssignmentReports
                    .Find(Builders<WorkAssignmentReport>.Filter.In(x => x.Id, reportIds)
                          & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false)
                          & Builders<WorkAssignmentReport>.Filter.Ne(x => x.IsActive, false))
                    .ToListAsync(ct))
                .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);

        var periodsById = periodIds.Count == 0
            ? new Dictionary<string, WorkReportPeriod>(StringComparer.Ordinal)
            : (await _ctx.WorkReportPeriods
                    .Find(x => periodIds.Contains(x.Id) && !x.IsDeleted)
                    .ToListAsync(ct))
                .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);

        var rows = docs
            .Select(doc => MapTableMetricReportRow(doc, ctx, reportsById, periodsById))
            .ToList();

        return new PagedResult<DashboardMindMapTableMetricReportRowDto>(rows, total, safePage, safePageSize);
    }

    public async Task<PagedResult<DashboardMindMapFieldMetricReportRowDto>> SearchNodeFieldMetricReportsAsync(
        string assignmentId,
        DashboardMindMapFieldMetricReportsSearchRequest? req,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        req ??= new DashboardMindMapFieldMetricReportsSearchRequest();

        if (string.IsNullOrWhiteSpace(req.FieldId))
            throw DashboardRequired(AppErrorCode.DASHBOARD_FIELD_ID_REQUIRED, nameof(req.FieldId), req.FieldId);

        var safePage = Math.Max(req.Page, 0);
        var safePageSize = ClampPageSize(req.PageSize);
        var normalizedScope = NormalizeScope(req);
        var node = await LoadAccessibleAssignmentAsync(assignmentId, me.Id, ct);
        var ctx = await LoadSubtreeContextAsync(node, normalizedScope, includeCurrentReports: false, ct);

        var assignmentIds = ctx.Assignments
            .Select(x => x.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (assignmentIds.Count == 0)
        {
            return new PagedResult<DashboardMindMapFieldMetricReportRowDto>(
                new List<DashboardMindMapFieldMetricReportRowDto>(),
                0,
                safePage,
                safePageSize);
        }

        var periodInstanceKeys = normalizedScope.HasFilters
            ? ctx.Periods
                .Select(x => x.PeriodInstanceKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : new List<string>();

        if (normalizedScope.HasFilters && periodInstanceKeys.Count == 0)
        {
            return new PagedResult<DashboardMindMapFieldMetricReportRowDto>(
                new List<DashboardMindMapFieldMetricReportRowDto>(),
                0,
                safePage,
                safePageSize);
        }

        var filter = BuildFieldMetricValueFilter(node.WorkId, assignmentIds, periodInstanceKeys, req);
        var groupId = BuildFieldMetricReportGroupId();

        var totalResult = await _ctx.WorkReportFieldStatValues
            .Aggregate()
            .Match(filter)
            .Group(new BsonDocument { { "_id", groupId } })
            .Count()
            .FirstOrDefaultAsync(ct);

        var total = totalResult?.Count ?? 0;
        if (total == 0)
        {
            return new PagedResult<DashboardMindMapFieldMetricReportRowDto>(
                new List<DashboardMindMapFieldMetricReportRowDto>(),
                0,
                safePage,
                safePageSize);
        }

        var docs = await _ctx.WorkReportFieldStatValues
            .Aggregate()
            .Match(filter)
            .Group(new BsonDocument
            {
                { "_id", groupId },
                { "workAssignmentReportId", new BsonDocument("$first", "$workAssignmentReportId") },
                { "workReportPeriodId", new BsonDocument("$first", "$workReportPeriodId") },
                { "assignmentId", new BsonDocument("$first", "$workAssignmentId") },
                { "dynamicFormTemplateId", new BsonDocument("$first", "$dynamicFormTemplateId") },
                { "dynamicFormTemplateName", new BsonDocument("$first", "$dynamicFormTemplateName") },
                { "fieldId", new BsonDocument("$first", "$fieldId") },
                { "fieldKey", new BsonDocument("$first", "$fieldKey") },
                { "fieldLabel", new BsonDocument("$first", "$fieldLabel") },
                { "fieldType", new BsonDocument("$first", "$fieldType") },
                { "bucketKey", new BsonDocument("$first", "$bucketKey") },
                { "bucketLabel", new BsonDocument("$first", "$bucketLabel") },
                { "periodKey", new BsonDocument("$first", "$periodKey") },
                { "periodInstanceKey", new BsonDocument("$first", "$periodInstanceKey") },
                { "periodKind", new BsonDocument("$first", "$periodKind") },
                { "reportStatus", new BsonDocument("$first", "$reportStatus") },
                { "valueCount", new BsonDocument("$sum", 1) },
                { "numericValueCount", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$ne", new BsonArray { "$numericValue", BsonNull.Value }),
                        1,
                        0
                    }))
                },
                { "sum", new BsonDocument("$sum", new BsonDocument("$ifNull", new BsonArray { "$numericValue", 0 })) },
                { "min", new BsonDocument("$min", "$numericValue") },
                { "max", new BsonDocument("$max", "$numericValue") },
                { "trueCount", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { "$booleanValue", true }),
                        1,
                        0
                    }))
                },
                { "falseCount", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { "$booleanValue", false }),
                        1,
                        0
                    }))
                },
                { "latestDateUtc", new BsonDocument("$max", "$dateValueUtc") },
                { "sourceKeys", new BsonDocument("$addToSet", "$sourceKey") },
            })
            .Sort(new BsonDocument
            {
                { "sum", -1 },
                { "valueCount", -1 },
                { "periodInstanceKey", 1 },
                { "workAssignmentReportId", 1 },
            })
            .Skip(safePage * safePageSize)
            .Limit(safePageSize)
            .ToListAsync(ct);

        var reportIds = docs
            .Select(x => ReadBsonString(x, "workAssignmentReportId"))
            .OfType<string>()
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var periodIds = docs
            .Select(x => ReadBsonString(x, "workReportPeriodId"))
            .OfType<string>()
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var reportsById = reportIds.Count == 0
            ? new Dictionary<string, WorkAssignmentReport>(StringComparer.Ordinal)
            : (await _ctx.WorkAssignmentReports
                    .Find(Builders<WorkAssignmentReport>.Filter.In(x => x.Id, reportIds)
                          & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false)
                          & Builders<WorkAssignmentReport>.Filter.Ne(x => x.IsActive, false))
                    .ToListAsync(ct))
                .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);

        var periodsById = periodIds.Count == 0
            ? new Dictionary<string, WorkReportPeriod>(StringComparer.Ordinal)
            : (await _ctx.WorkReportPeriods
                    .Find(x => periodIds.Contains(x.Id) && !x.IsDeleted)
                    .ToListAsync(ct))
                .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);

        var rows = docs
            .Select(doc => MapFieldMetricReportRow(doc, ctx, reportsById, periodsById))
            .ToList();

        return new PagedResult<DashboardMindMapFieldMetricReportRowDto>(rows, total, safePage, safePageSize);
    }

    public async Task<PagedResult<DashboardMindMapLabelReportRowDto>> SearchNodeLabelReportsAsync(
        string assignmentId,
        DashboardMindMapLabelReportsSearchRequest? req,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        req ??= new DashboardMindMapLabelReportsSearchRequest();

        if (string.IsNullOrWhiteSpace(req.LabelCode))
            throw DashboardRequired(AppErrorCode.DASHBOARD_LABEL_CODE_REQUIRED, nameof(req.LabelCode), req.LabelCode);

        var safePage = Math.Max(req.Page, 0);
        var safePageSize = ClampPageSize(req.PageSize);
        var normalizedScope = NormalizeScope(req);
        var node = await LoadAccessibleAssignmentAsync(assignmentId, me.Id, ct);
        var ctx = await LoadSubtreeContextAsync(node, normalizedScope, includeCurrentReports: false, ct);

        var assignmentIds = ctx.Assignments
            .Select(x => x.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (assignmentIds.Count == 0)
        {
            return new PagedResult<DashboardMindMapLabelReportRowDto>(
                new List<DashboardMindMapLabelReportRowDto>(),
                0,
                safePage,
                safePageSize);
        }

        var periodInstanceKeys = normalizedScope.HasFilters
            ? ctx.Periods
                .Select(x => x.PeriodInstanceKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : new List<string>();

        if (normalizedScope.HasFilters && periodInstanceKeys.Count == 0)
        {
            return new PagedResult<DashboardMindMapLabelReportRowDto>(
                new List<DashboardMindMapLabelReportRowDto>(),
                0,
                safePage,
                safePageSize);
        }

        var filter = BuildLabelValueFilter(node.WorkId, assignmentIds, periodInstanceKeys, req);
        var groupId = BuildLabelReportGroupId();

        var totalResult = await _ctx.WorkReportLabelStatValues
            .Aggregate()
            .Match(filter)
            .Group(new BsonDocument { { "_id", groupId } })
            .Count()
            .FirstOrDefaultAsync(ct);

        var total = totalResult?.Count ?? 0;
        if (total == 0)
        {
            return new PagedResult<DashboardMindMapLabelReportRowDto>(
                new List<DashboardMindMapLabelReportRowDto>(),
                0,
                safePage,
                safePageSize);
        }

        var docs = await _ctx.WorkReportLabelStatValues
            .Aggregate()
            .Match(filter)
            .Group(new BsonDocument
            {
                { "_id", groupId },
                { "workAssignmentReportId", new BsonDocument("$first", "$workAssignmentReportId") },
                { "workReportPeriodId", new BsonDocument("$first", "$workReportPeriodId") },
                { "assignmentId", new BsonDocument("$first", "$workAssignmentId") },
                { "dynamicFormTemplateId", new BsonDocument("$first", "$dynamicFormTemplateId") },
                { "dynamicFormTemplateName", new BsonDocument("$first", "$dynamicFormTemplateName") },
                { "dynamicExcelTemplateId", new BsonDocument("$first", "$dynamicExcelTemplateId") },
                { "labelCode", new BsonDocument("$first", "$labelCode") },
                { "periodKey", new BsonDocument("$first", "$periodKey") },
                { "periodInstanceKey", new BsonDocument("$first", "$periodInstanceKey") },
                { "periodKind", new BsonDocument("$first", "$periodKind") },
                { "reportStatus", new BsonDocument("$first", "$reportStatus") },
                { "rowCount", new BsonDocument("$sum", 1) },
                { "blockIds", new BsonDocument("$addToSet", "$blockId") },
                { "rowKeys", new BsonDocument("$addToSet", "$rowKey") },
                { "sources", new BsonDocument("$addToSet", "$source") },
            })
            .Sort(new BsonDocument
            {
                { "rowCount", -1 },
                { "periodInstanceKey", 1 },
                { "workAssignmentReportId", 1 },
            })
            .Skip(safePage * safePageSize)
            .Limit(safePageSize)
            .ToListAsync(ct);

        var reportIds = docs
            .Select(x => ReadBsonString(x, "workAssignmentReportId"))
            .OfType<string>()
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var periodIds = docs
            .Select(x => ReadBsonString(x, "workReportPeriodId"))
            .OfType<string>()
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var reportsById = reportIds.Count == 0
            ? new Dictionary<string, WorkAssignmentReport>(StringComparer.Ordinal)
            : (await _ctx.WorkAssignmentReports
                    .Find(Builders<WorkAssignmentReport>.Filter.In(x => x.Id, reportIds)
                          & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false)
                          & Builders<WorkAssignmentReport>.Filter.Ne(x => x.IsActive, false))
                    .ToListAsync(ct))
                .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);

        var periodsById = periodIds.Count == 0
            ? new Dictionary<string, WorkReportPeriod>(StringComparer.Ordinal)
            : (await _ctx.WorkReportPeriods
                    .Find(x => periodIds.Contains(x.Id) && !x.IsDeleted)
                    .ToListAsync(ct))
                .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);

        var labelCode = NormalizeLabelCode(req.LabelCode);
        var label = string.IsNullOrWhiteSpace(labelCode)
            ? null
            : await _ctx.Labels
                .Find(x => x.Code == labelCode && x.IsActive && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);

        var rows = docs
            .Select(doc => MapLabelReportRow(doc, ctx, reportsById, periodsById, label))
            .ToList();

        return new PagedResult<DashboardMindMapLabelReportRowDto>(rows, total, safePage, safePageSize);
    }

    private static FilterDefinition<WorkReportTableStatValue> BuildTableMetricValueFilter(
        string workId,
        List<string> assignmentIds,
        List<string> periodInstanceKeys,
        DashboardMindMapTableMetricReportsSearchRequest req)
    {
        var fb = Builders<WorkReportTableStatValue>.Filter;
        var filter = fb.Eq(x => x.WorkId, workId)
            & fb.Eq(x => x.IsDeleted, false)
            & fb.In(x => x.WorkAssignmentId, assignmentIds)
            & fb.Eq(x => x.MetricKey, req.MetricKey.Trim());

        if (periodInstanceKeys.Count > 0)
            filter &= fb.In(x => x.PeriodInstanceKey, periodInstanceKeys);

        if (!string.IsNullOrWhiteSpace(req.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, req.DynamicFormTemplateId.Trim());

        if (!string.IsNullOrWhiteSpace(req.DynamicExcelTemplateId))
            filter &= fb.Eq(x => x.DynamicExcelTemplateId, req.DynamicExcelTemplateId.Trim());

        if (!string.IsNullOrWhiteSpace(req.BlockId))
            filter &= fb.Eq(x => x.BlockId, NormalizeTableBlockId(req.BlockId));

        if (!string.IsNullOrWhiteSpace(req.TableMode))
            filter &= fb.Eq(x => x.TableMode, NormalizeTableMode(req.TableMode));

        if (req.ReportStatus.HasValue)
            filter &= fb.Eq(x => x.ReportStatus, req.ReportStatus.Value);

        return filter;
    }

    private static FilterDefinition<WorkReportLabelStatValue> BuildLabelValueFilter(
        string workId,
        List<string> assignmentIds,
        List<string> periodInstanceKeys,
        DashboardMindMapLabelReportsSearchRequest req)
    {
        var fb = Builders<WorkReportLabelStatValue>.Filter;
        var filter = fb.Eq(x => x.WorkId, workId)
            & fb.Eq(x => x.IsDeleted, false)
            & fb.In(x => x.WorkAssignmentId, assignmentIds)
            & fb.Eq(x => x.LabelCode, NormalizeLabelCode(req.LabelCode));

        if (periodInstanceKeys.Count > 0)
            filter &= fb.In(x => x.PeriodInstanceKey, periodInstanceKeys);

        if (!string.IsNullOrWhiteSpace(req.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, req.DynamicFormTemplateId.Trim());

        if (!string.IsNullOrWhiteSpace(req.DynamicExcelTemplateId))
            filter &= fb.Eq(x => x.DynamicExcelTemplateId, req.DynamicExcelTemplateId.Trim());

        if (!string.IsNullOrWhiteSpace(req.BlockId))
            filter &= fb.Eq(x => x.BlockId, NormalizeTableBlockId(req.BlockId));

        if (req.ReportStatus.HasValue)
            filter &= fb.Eq(x => x.ReportStatus, req.ReportStatus.Value);

        return filter;
    }

    private static BsonDocument BuildTableMetricReportGroupId()
    {
        return new BsonDocument
        {
            { "workAssignmentReportId", "$workAssignmentReportId" },
            { "workReportPeriodId", "$workReportPeriodId" },
            { "assignmentId", "$workAssignmentId" },
            { "metricKey", "$metricKey" },
        };
    }

    private static FilterDefinition<WorkReportFieldStatValue> BuildFieldMetricValueFilter(
        string workId,
        List<string> assignmentIds,
        List<string> periodInstanceKeys,
        DashboardMindMapFieldMetricReportsSearchRequest req)
    {
        var fb = Builders<WorkReportFieldStatValue>.Filter;
        var filter = fb.Eq(x => x.WorkId, workId)
            & fb.Eq(x => x.IsDeleted, false)
            & fb.In(x => x.WorkAssignmentId, assignmentIds)
            & fb.Eq(x => x.FieldId, req.FieldId.Trim());

        if (periodInstanceKeys.Count > 0)
            filter &= fb.In(x => x.PeriodInstanceKey, periodInstanceKeys);

        if (!string.IsNullOrWhiteSpace(req.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, req.DynamicFormTemplateId.Trim());

        if (!string.IsNullOrWhiteSpace(req.BucketKey))
            filter &= fb.Eq(x => x.BucketKey, req.BucketKey.Trim());

        if (req.ReportStatus.HasValue)
            filter &= fb.Eq(x => x.ReportStatus, req.ReportStatus.Value);

        return filter;
    }

    private static BsonDocument BuildFieldMetricReportGroupId()
    {
        return new BsonDocument
        {
            { "workAssignmentReportId", "$workAssignmentReportId" },
            { "workReportPeriodId", "$workReportPeriodId" },
            { "assignmentId", "$workAssignmentId" },
            { "fieldId", "$fieldId" },
            { "bucketKey", "$bucketKey" },
        };
    }

    private static BsonDocument BuildLabelReportGroupId()
    {
        return new BsonDocument
        {
            { "workAssignmentReportId", "$workAssignmentReportId" },
            { "workReportPeriodId", "$workReportPeriodId" },
            { "assignmentId", "$workAssignmentId" },
            { "labelCode", "$labelCode" },
        };
    }

    private static DashboardMindMapTableMetricReportRowDto MapTableMetricReportRow(
        BsonDocument doc,
        SubtreeContext ctx,
        Dictionary<string, WorkAssignmentReport> reportsById,
        Dictionary<string, WorkReportPeriod> periodsById)
    {
        var reportId = ReadBsonString(doc, "workAssignmentReportId") ?? string.Empty;
        var periodId = ReadBsonString(doc, "workReportPeriodId") ?? string.Empty;
        var assignmentId = ReadBsonString(doc, "assignmentId") ?? string.Empty;
        var valueCount = ReadBsonInt64(doc, "valueCount");
        var sum = ReadBsonDecimal(doc, "sum") ?? 0m;

        reportsById.TryGetValue(reportId, out var report);
        periodsById.TryGetValue(periodId, out var period);
        ctx.AssignmentById.TryGetValue(assignmentId, out var assignment);

        var assignee = assignment?.Assignees?.FirstOrDefault(a =>
            string.Equals(a.UserId, period?.AssigneeUserId ?? report?.CreatedByUserId, StringComparison.Ordinal));

        return new DashboardMindMapTableMetricReportRowDto
        {
            WorkAssignmentReportId = reportId,
            WorkReportPeriodId = periodId,
            AssignmentId = assignmentId,
            AssignmentCode = assignment?.Code,
            AssignmentName = assignment?.DynamicExcelName ?? period?.DynamicExcelName ?? string.Empty,
            AssigneeUserId = period?.AssigneeUserId,
            AssigneeFullName = assignee?.FullName ?? assignee?.Username ?? period?.AssigneeUserId,
            AssigneeUsername = assignee?.Username,
            UnitId = period?.AssigneeUnitId ?? assignee?.UnitId,
            UnitLabel = PickUnitLabel(assignee?.UnitSymbol, assignee?.UnitShortName, assignee?.UnitName)
                ?? period?.AssigneeUnitId,
            PeriodKey = ReadBsonString(doc, "periodKey") ?? period?.PeriodKey ?? string.Empty,
            PeriodInstanceKey = ReadBsonString(doc, "periodInstanceKey") ?? period?.PeriodInstanceKey ?? string.Empty,
            PeriodKind = ReadBsonString(doc, "periodKind") ?? period?.PeriodKind ?? string.Empty,
            ReportStatus = ReadBsonInt32(doc, "reportStatus"),
            BlockId = ReadBsonString(doc, "blockId") ?? string.Empty,
            TableMode = ReadBsonString(doc, "tableMode") ?? string.Empty,
            MetricKey = ReadBsonString(doc, "metricKey") ?? string.Empty,
            RowKey = ReadBsonString(doc, "rowKey") ?? string.Empty,
            ColumnKey = ReadBsonString(doc, "columnKey") ?? string.Empty,
            ValueCount = valueCount,
            Sum = sum,
            Min = ReadBsonDecimal(doc, "min"),
            Max = ReadBsonDecimal(doc, "max"),
            Average = valueCount > 0 ? sum / valueCount : null,
            SourceKeys = ReadBsonStringArray(doc, "sourceKeys"),
            SubmittedAtUtc = report?.SubmittedAtUtc ?? period?.LastSubmittedAtUtc,
            ApprovedAtUtc = report?.ApprovedAtUtc ?? period?.LastReviewedAtUtc,
        };
    }

    private static DashboardMindMapFieldMetricReportRowDto MapFieldMetricReportRow(
        BsonDocument doc,
        SubtreeContext ctx,
        Dictionary<string, WorkAssignmentReport> reportsById,
        Dictionary<string, WorkReportPeriod> periodsById)
    {
        var reportId = ReadBsonString(doc, "workAssignmentReportId") ?? string.Empty;
        var periodId = ReadBsonString(doc, "workReportPeriodId") ?? string.Empty;
        var assignmentId = ReadBsonString(doc, "assignmentId") ?? string.Empty;
        var valueCount = ReadBsonInt64(doc, "valueCount");
        var numericValueCount = ReadBsonInt64(doc, "numericValueCount");
        var sum = ReadBsonDecimal(doc, "sum") ?? 0m;

        reportsById.TryGetValue(reportId, out var report);
        periodsById.TryGetValue(periodId, out var period);
        ctx.AssignmentById.TryGetValue(assignmentId, out var assignment);

        var assignee = assignment?.Assignees?.FirstOrDefault(a =>
            string.Equals(a.UserId, period?.AssigneeUserId ?? report?.CreatedByUserId, StringComparison.Ordinal));

        return new DashboardMindMapFieldMetricReportRowDto
        {
            WorkAssignmentReportId = reportId,
            WorkReportPeriodId = periodId,
            AssignmentId = assignmentId,
            AssignmentCode = assignment?.Code,
            AssignmentName = assignment?.DynamicExcelName ?? period?.DynamicExcelName ?? string.Empty,
            AssigneeUserId = period?.AssigneeUserId,
            AssigneeFullName = assignee?.FullName ?? assignee?.Username ?? period?.AssigneeUserId,
            AssigneeUsername = assignee?.Username,
            UnitId = period?.AssigneeUnitId ?? assignee?.UnitId,
            UnitLabel = PickUnitLabel(assignee?.UnitSymbol, assignee?.UnitShortName, assignee?.UnitName)
                ?? period?.AssigneeUnitId,
            PeriodKey = ReadBsonString(doc, "periodKey") ?? period?.PeriodKey ?? string.Empty,
            PeriodInstanceKey = ReadBsonString(doc, "periodInstanceKey") ?? period?.PeriodInstanceKey ?? string.Empty,
            PeriodKind = ReadBsonString(doc, "periodKind") ?? period?.PeriodKind ?? string.Empty,
            ReportStatus = ReadBsonInt32(doc, "reportStatus"),
            DynamicFormTemplateId = ReadBsonString(doc, "dynamicFormTemplateId"),
            DynamicFormTemplateName = ReadBsonString(doc, "dynamicFormTemplateName"),
            FieldId = ReadBsonString(doc, "fieldId") ?? string.Empty,
            FieldKey = ReadBsonString(doc, "fieldKey") ?? string.Empty,
            FieldLabel = ReadBsonString(doc, "fieldLabel") ?? string.Empty,
            FieldType = ReadBsonString(doc, "fieldType") ?? string.Empty,
            BucketKey = ReadBsonString(doc, "bucketKey"),
            BucketLabel = ReadBsonString(doc, "bucketLabel"),
            ValueCount = valueCount,
            NumericValueCount = numericValueCount,
            Sum = numericValueCount > 0 ? sum : null,
            Min = ReadBsonDecimal(doc, "min"),
            Max = ReadBsonDecimal(doc, "max"),
            Average = numericValueCount > 0 ? sum / numericValueCount : null,
            TrueCount = ReadBsonInt64(doc, "trueCount"),
            FalseCount = ReadBsonInt64(doc, "falseCount"),
            LatestDateUtc = ReadBsonDateTime(doc, "latestDateUtc"),
            SourceKeys = ReadBsonStringArray(doc, "sourceKeys"),
            SubmittedAtUtc = report?.SubmittedAtUtc ?? period?.LastSubmittedAtUtc,
            ApprovedAtUtc = report?.ApprovedAtUtc ?? period?.LastReviewedAtUtc,
        };
    }

    private static DashboardMindMapLabelReportRowDto MapLabelReportRow(
        BsonDocument doc,
        SubtreeContext ctx,
        Dictionary<string, WorkAssignmentReport> reportsById,
        Dictionary<string, WorkReportPeriod> periodsById,
        LabelCatalogItem? label)
    {
        var reportId = ReadBsonString(doc, "workAssignmentReportId") ?? string.Empty;
        var periodId = ReadBsonString(doc, "workReportPeriodId") ?? string.Empty;
        var assignmentId = ReadBsonString(doc, "assignmentId") ?? string.Empty;

        reportsById.TryGetValue(reportId, out var report);
        periodsById.TryGetValue(periodId, out var period);
        ctx.AssignmentById.TryGetValue(assignmentId, out var assignment);

        var assignee = assignment?.Assignees?.FirstOrDefault(a =>
            string.Equals(a.UserId, period?.AssigneeUserId ?? report?.CreatedByUserId, StringComparison.Ordinal));

        return new DashboardMindMapLabelReportRowDto
        {
            WorkAssignmentReportId = reportId,
            WorkReportPeriodId = periodId,
            AssignmentId = assignmentId,
            AssignmentCode = assignment?.Code,
            AssignmentName = assignment?.DynamicExcelName ?? period?.DynamicExcelName ?? string.Empty,
            AssigneeUserId = period?.AssigneeUserId,
            AssigneeFullName = assignee?.FullName ?? assignee?.Username ?? period?.AssigneeUserId,
            AssigneeUsername = assignee?.Username,
            UnitId = period?.AssigneeUnitId ?? assignee?.UnitId,
            UnitLabel = PickUnitLabel(assignee?.UnitSymbol, assignee?.UnitShortName, assignee?.UnitName)
                ?? period?.AssigneeUnitId,
            PeriodKey = ReadBsonString(doc, "periodKey") ?? period?.PeriodKey ?? string.Empty,
            PeriodInstanceKey = ReadBsonString(doc, "periodInstanceKey") ?? period?.PeriodInstanceKey ?? string.Empty,
            PeriodKind = ReadBsonString(doc, "periodKind") ?? period?.PeriodKind ?? string.Empty,
            ReportStatus = ReadBsonInt32(doc, "reportStatus"),
            DynamicFormTemplateId = ReadBsonString(doc, "dynamicFormTemplateId"),
            DynamicFormTemplateName = ReadBsonString(doc, "dynamicFormTemplateName"),
            DynamicExcelTemplateId = ReadBsonString(doc, "dynamicExcelTemplateId"),
            LabelCode = ReadBsonString(doc, "labelCode") ?? string.Empty,
            LabelName = label?.Name,
            LabelColor = label?.Color,
            RowCount = ReadBsonInt64(doc, "rowCount"),
            BlockIds = ReadBsonStringArray(doc, "blockIds"),
            RowKeys = ReadBsonStringArray(doc, "rowKeys"),
            Sources = ReadBsonStringArray(doc, "sources"),
            SubmittedAtUtc = report?.SubmittedAtUtc ?? period?.LastSubmittedAtUtc,
            ApprovedAtUtc = report?.ApprovedAtUtc ?? period?.LastReviewedAtUtc,
        };
    }

    private static int ClampPageSize(int pageSize) => Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, 100);

    private static int ClampGraphLimit(int limit) => Math.Clamp(limit <= 0 ? DefaultGraphLimit : limit, 1, MaxGraphLimit);

    private static int ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return 0;

        return int.TryParse(cursor.Trim(), out var parsed)
            ? Math.Max(parsed, 0)
            : 0;
    }

    private static SortDefinition<WorkAssignment> BuildAssignmentSort()
    {
        var sort = Builders<WorkAssignment>.Sort;
        return sort.Combine(
            sort.Descending(x => x.HasOverduePeriod),
            sort.Descending(x => x.LatestDueAtUtc),
            sort.Descending(x => x.UpdatedAtUtc),
            sort.Ascending(x => x.Path));
    }

    private static DashboardMindMapCursorResult<T> BuildCursorResult<T>(
        List<T> rows,
        long totalRows,
        int offset,
        int limit)
    {
        var nextOffset = offset + rows.Count;
        return new DashboardMindMapCursorResult<T>
        {
            Rows = rows,
            TotalRows = totalRows,
            Limit = limit,
            HasMore = nextOffset < totalRows,
            NextCursor = nextOffset < totalRows ? nextOffset.ToString() : null,
        };
    }

    private static FilterDefinition<WorkAssignment> BuildNodeBaseFilter(string workId)
    {
        return Builders<WorkAssignment>.Filter.Eq(x => x.WorkId, workId)
            & Builders<WorkAssignment>.Filter.Eq(x => x.IsDeleted, false)
            & Builders<WorkAssignment>.Filter.Eq(x => x.IsActive, true);
    }

    private static FilterDefinition<WorkAssignment> BuildRootAssignmentFilter()
    {
        var fb = Builders<WorkAssignment>.Filter;
        return fb.Or(
            fb.Eq(x => x.ParentAssignmentId, null as string),
            fb.Eq(x => x.ParentAssignmentId, string.Empty),
            fb.Eq(x => x.Level, 0),
            fb.Regex(x => x.Path, new BsonRegularExpression("^/[^/]+$")));
    }

    private async Task<WorkAccessContext> LoadWorkAccessContextAsync(
        string workId,
        string actorUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workId))
            throw DashboardRequired(AppErrorCode.DASHBOARD_WORK_ID_REQUIRED, nameof(workId), workId);

        var work = await _ctx.Works
            .Find(x => x.Id == workId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (work is null)
            throw DashboardNotFound(
                AppErrorCode.DASHBOARD_WORK_NOT_FOUND,
                new { workId });

        var hasFullAccess = string.Equals(work.CreatedByUserId, actorUserId, StringComparison.Ordinal)
            || await HasFullWorkReadRoleAsync(workId, actorUserId, ct);

        if (hasFullAccess)
            return new WorkAccessContext(work, true, new List<WorkAssignment>());

        var entryAssignments = await LoadAccessibleEntryAssignmentsAsync(workId, actorUserId, ct);
        if (entryAssignments.Count > 0)
            return new WorkAccessContext(work, false, entryAssignments);

        var hasWorkRole = await _docRole.HasAnyRoleAsync(DocType.WORK, workId, actorUserId, ct);
        if (hasWorkRole)
            return new WorkAccessContext(work, false, new List<WorkAssignment>());

        throw DashboardForbidden(
            AppErrorCode.DASHBOARD_WORK_READ_FORBIDDEN,
            new { workId, actorUserId });
    }

    private async Task<WorkAssignment> LoadAccessibleAssignmentAsync(string assignmentId, string actorUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(assignmentId))
            throw DashboardRequired(AppErrorCode.DASHBOARD_ASSIGNMENT_ID_REQUIRED, nameof(assignmentId), assignmentId);

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && !x.IsDeleted && x.IsActive)
            .FirstOrDefaultAsync(ct);

        if (assignment is null)
            throw DashboardNotFound(
                AppErrorCode.DASHBOARD_ASSIGNMENT_NOT_FOUND,
                new { assignmentId });

        var access = await LoadWorkAccessContextAsync(assignment.WorkId, actorUserId, ct);
        if (!access.FullAccess && !IsAssignmentInAccessibleBranch(assignment, access.EntryAssignments))
            throw DashboardForbidden(
                AppErrorCode.DASHBOARD_ASSIGNMENT_READ_FORBIDDEN,
                new { assignmentId, assignment.WorkId, actorUserId });

        return assignment;
    }

    private async Task<bool> HasFullWorkReadRoleAsync(
        string workId,
        string actorUserId,
        CancellationToken ct)
    {
        var filter = Builders<DocRole>.Filter.Eq(x => x.DocType, DocType.WORK)
            & Builders<DocRole>.Filter.Eq(x => x.DocId, workId)
            & Builders<DocRole>.Filter.Eq(x => x.UserId, actorUserId)
            & Builders<DocRole>.Filter.Eq(x => x.IsDeleted, false)
            & Builders<DocRole>.Filter.In(x => x.Role, FullWorkReadRoles);

        return await _ctx.DocRoles.Find(filter).AnyAsync(ct);
    }

    private async Task<List<WorkAssignment>> LoadAccessibleEntryAssignmentsAsync(
        string workId,
        string actorUserId,
        CancellationToken ct)
    {
        var roleFilter = Builders<DocRole>.Filter.Eq(x => x.DocType, DocType.WORK_ASSIGNMENT)
            & Builders<DocRole>.Filter.Eq(x => x.UserId, actorUserId)
            & Builders<DocRole>.Filter.Eq(x => x.IsDeleted, false)
            & Builders<DocRole>.Filter.In(x => x.Role, AssignmentReadRoles);

        var assignmentIdsByRole = (await _ctx.DocRoles
                .Find(roleFilter)
                .ToListAsync(ct))
            .Select(x => x.DocId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);

        var fb = Builders<WorkAssignment>.Filter;
        var accessFilters = new List<FilterDefinition<WorkAssignment>>
        {
            fb.Eq(x => x.CreatedByUserId, actorUserId),
            fb.Where(x => x.Assignees != null && x.Assignees.Any(a => a.UserId == actorUserId)),
        };

        if (assignmentIdsByRole.Count > 0)
            accessFilters.Add(fb.In(x => x.Id, assignmentIdsByRole));

        var filter = BuildNodeBaseFilter(workId) & fb.Or(accessFilters);

        var assignments = await _ctx.WorkAssignments
            .Find(filter)
            .Sort(BuildAssignmentSort())
            .ToListAsync(ct);

        return CompactAccessibleEntryAssignments(assignments);
    }

    private static List<WorkAssignment> CompactAccessibleEntryAssignments(List<WorkAssignment> assignments)
    {
        var result = new List<WorkAssignment>();

        foreach (var assignment in assignments
                     .OrderBy(x => string.IsNullOrWhiteSpace(x.Path) ? int.MaxValue : x.Path.Length)
                     .ThenBy(x => x.Path)
                     .ThenBy(x => x.Id))
        {
            if (IsAssignmentInAccessibleBranch(assignment, result))
                continue;

            result.Add(assignment);
        }

        return result
            .OrderByDescending(x => x.HasOverduePeriod)
            .ThenByDescending(x => x.LatestDueAtUtc)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .ThenBy(x => x.Path)
            .ToList();
    }

    private static bool IsAssignmentInAccessibleBranch(
        WorkAssignment assignment,
        IReadOnlyCollection<WorkAssignment> entryAssignments)
    {
        foreach (var entry in entryAssignments)
        {
            if (string.Equals(assignment.Id, entry.Id, StringComparison.Ordinal))
                return true;

            if (string.IsNullOrWhiteSpace(assignment.Path) || string.IsNullOrWhiteSpace(entry.Path))
                continue;

            if (string.Equals(assignment.Path, entry.Path, StringComparison.Ordinal)
                || assignment.Path.StartsWith(entry.Path + "/", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private async Task<SubtreeContext> LoadSubtreeContextAsync(
        WorkAssignment node,
        MindMapScope scope,
        bool includeCurrentReports,
        CancellationToken ct)
    {
        var pathPattern = $"^{Regex.Escape(node.Path)}(?:/|$)";
        var assignmentFilter = BuildNodeBaseFilter(node.WorkId)
            & Builders<WorkAssignment>.Filter.Eq(x => x.RootAssignmentId, node.RootAssignmentId)
            & Builders<WorkAssignment>.Filter.Regex(x => x.Path, new BsonRegularExpression(pathPattern));

        var assignments = await _ctx.WorkAssignments
            .Find(assignmentFilter)
            .SortBy(x => x.Path)
            .ToListAsync(ct);

        var assignmentIds = assignments
            .Select(x => x.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var periods = assignmentIds.Count == 0
            ? new List<WorkReportPeriod>()
            : await _ctx.WorkReportPeriods
                .Find(ApplyScopeToPeriodFilter(
                    Builders<WorkReportPeriod>.Filter.Eq(x => x.WorkId, node.WorkId)
                    & Builders<WorkReportPeriod>.Filter.In(x => x.WorkAssignmentId, assignmentIds)
                    & Builders<WorkReportPeriod>.Filter.Eq(x => x.IsDeleted, false)
                    & Builders<WorkReportPeriod>.Filter.Eq(x => x.IsActive, true),
                    scope))
                .ToListAsync(ct);

        var currentReports = new Dictionary<string, WorkAssignmentReport>(StringComparer.Ordinal);
        if (includeCurrentReports)
        {
            var currentReportIds = periods
                .Where(x => !string.IsNullOrWhiteSpace(x.CurrentReportId))
                .Select(x => x.CurrentReportId!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (currentReportIds.Count > 0)
            {
                var currentReportItems = await _ctx.WorkAssignmentReports
                    .Find(Builders<WorkAssignmentReport>.Filter.In(x => x.Id, currentReportIds)
                          & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false)
                          & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsCurrent, true)
                          & Builders<WorkAssignmentReport>.Filter.Ne(x => x.IsActive, false))
                    .ToListAsync(ct);

                currentReports = currentReportItems.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
            }
        }

        return new SubtreeContext(
            Node: node,
            Assignments: assignments,
            AssignmentById: assignments.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal),
            Periods: periods,
            CurrentReportsById: currentReports);
    }

    private MindMapScope NormalizeScope(DashboardMindMapScopeRequest? req)
    {
        var unitIds = new HashSet<string>(
            NormalizeIds(req?.UnitIds),
            StringComparer.OrdinalIgnoreCase);

        return new MindMapScope(
            BuildOptionalRange(req?.FromUtc, req?.ToUtc),
            unitIds);
    }

    private static List<string> NormalizeIds(List<string>? ids)
    {
        return ids?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();
    }

    private static string? ReadBsonString(BsonDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var value) || value.IsBsonNull)
            return null;

        return value.IsString ? value.AsString : value.ToString();
    }

    private static long ReadBsonInt64(BsonDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var value) || value.IsBsonNull)
            return 0;

        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            BsonType.Double => (long)value.AsDouble,
            BsonType.Decimal128 => (long)Decimal128.ToDecimal(value.AsDecimal128),
            _ => 0
        };
    }

    private static int ReadBsonInt32(BsonDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var value) || value.IsBsonNull)
            return 0;

        return value.BsonType switch
        {
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => (int)value.AsInt64,
            BsonType.Double => (int)value.AsDouble,
            BsonType.Decimal128 => (int)Decimal128.ToDecimal(value.AsDecimal128),
            _ => 0
        };
    }

    private static decimal? ReadBsonDecimal(BsonDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var value) || value.IsBsonNull)
            return null;

        return value.BsonType switch
        {
            BsonType.Decimal128 => Decimal128.ToDecimal(value.AsDecimal128),
            BsonType.Double => Convert.ToDecimal(value.AsDouble),
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            BsonType.String when decimal.TryParse(value.AsString, out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTime? ReadBsonDateTime(BsonDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var value) || value.IsBsonNull)
            return null;

        return value.BsonType == BsonType.DateTime
            ? value.ToUniversalTime()
            : null;
    }

    private static List<string> ReadBsonStringArray(BsonDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var value) || value.IsBsonNull || !value.IsBsonArray)
            return new List<string>();

        return value.AsBsonArray
            .Select(x => x.IsString ? x.AsString : x.ToString())
            .OfType<string>()
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizeTableBlockId(string? value)
        => string.IsNullOrWhiteSpace(value) ? "excel_block" : value.Trim();

    private static string NormalizeLabelCode(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static string NormalizeTableMode(string? value)
    {
        var tableMode = string.IsNullOrWhiteSpace(value) ? "FIXED_GRID" : value.Trim().ToUpperInvariant();
        return tableMode is "APPEND_ROWS" or "APPEND_COLUMNS" or "MATRIX" or "SUMMARY_TEMPLATE"
            ? tableMode
            : "FIXED_GRID";
    }

    private static DashboardNormalizedRange? BuildOptionalRange(DateTime? fromUtc, DateTime? toUtc)
    {
        if (!fromUtc.HasValue && !toUtc.HasValue)
            return null;

        return DashboardTimeRangeHelper.NormalizeMonthRange(fromUtc, toUtc);
    }

    private async Task<List<WorkAssignment>> FilterCandidatesByScopeAsync(
        string workId,
        List<WorkAssignment> candidates,
        WorkAssignment? parent,
        MindMapScope scope,
        CancellationToken ct)
    {
        if (!scope.HasFilters || candidates.Count == 0)
            return candidates;

        var candidateIds = candidates
            .Select(x => x.Id)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        List<WorkAssignment> subtreeAssignments;
        var candidateIdByAssignmentId = new Dictionary<string, string>(StringComparer.Ordinal);

        if (parent == null)
        {
            var fb = Builders<WorkAssignment>.Filter;
            var branchFilters = new List<FilterDefinition<WorkAssignment>>();
            foreach (var candidate in candidates)
            {
                var branchFilter = fb.Eq(x => x.Id, candidate.Id);
                if (!string.IsNullOrWhiteSpace(candidate.RootAssignmentId)
                    && !string.IsNullOrWhiteSpace(candidate.Path))
                {
                    branchFilter |= fb.Eq(x => x.RootAssignmentId, candidate.RootAssignmentId)
                                    & fb.Regex(
                                        x => x.Path,
                                        new BsonRegularExpression($"^{Regex.Escape(candidate.Path)}(?:/|$)"));
                }

                branchFilters.Add(branchFilter);
            }

            subtreeAssignments = await _ctx.WorkAssignments
                .Find(BuildNodeBaseFilter(workId) & fb.Or(branchFilters))
                .ToListAsync(ct);

            var candidatePathPairs = candidates
                .Select(x => new KeyValuePair<string, string>(x.Id, x.Path))
                .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .OrderByDescending(x => x.Value.Length)
                .ToList();

            foreach (var assignment in subtreeAssignments)
            {
                if (candidateIds.Contains(assignment.Id))
                {
                    candidateIdByAssignmentId[assignment.Id] = assignment.Id;
                    continue;
                }

                var candidateId = ResolveCandidateIdByPath(candidatePathPairs, assignment.Path);
                if (!string.IsNullOrWhiteSpace(candidateId))
                    candidateIdByAssignmentId[assignment.Id] = candidateId;
            }
        }
        else
        {
            var subtreeFilter = BuildNodeBaseFilter(workId)
                & Builders<WorkAssignment>.Filter.Eq(x => x.RootAssignmentId, parent.RootAssignmentId)
                & Builders<WorkAssignment>.Filter.Regex(
                    x => x.Path,
                    new BsonRegularExpression($"^{Regex.Escape(parent.Path)}(?:/|$)"));

            subtreeAssignments = await _ctx.WorkAssignments
                .Find(subtreeFilter)
                .ToListAsync(ct);

            var candidatePathPairs = candidates
                .Select(x => new KeyValuePair<string, string>(x.Id, x.Path))
                .OrderByDescending(x => x.Value.Length)
                .ToList();

            foreach (var assignment in subtreeAssignments)
            {
                if (assignment.Id == parent.Id)
                    continue;

                var candidateId = ResolveCandidateIdByPath(candidatePathPairs, assignment.Path);
                if (!string.IsNullOrWhiteSpace(candidateId))
                    candidateIdByAssignmentId[assignment.Id] = candidateId;
            }
        }

        var candidateUnitMatches = new HashSet<string>(StringComparer.Ordinal);
        if (scope.UnitIds.Count > 0)
        {
            foreach (var assignment in subtreeAssignments)
            {
                if (!candidateIdByAssignmentId.TryGetValue(assignment.Id, out var candidateId))
                    continue;

                if ((assignment.Assignees ?? new List<UserRef>()).Any(
                        x => !string.IsNullOrWhiteSpace(x.UnitId) && scope.UnitIds.Contains(x.UnitId)))
                {
                    candidateUnitMatches.Add(candidateId);
                }
            }
        }

        var candidatePeriodMatches = new HashSet<string>(StringComparer.Ordinal);
        if (scope.Range != null || scope.UnitIds.Count > 0)
        {
            var assignmentIds = candidateIdByAssignmentId.Keys.ToList();
            if (assignmentIds.Count > 0)
            {
                var periodFilter = ApplyScopeToPeriodFilter(
                    Builders<WorkReportPeriod>.Filter.Eq(x => x.WorkId, workId)
                    & Builders<WorkReportPeriod>.Filter.In(x => x.WorkAssignmentId, assignmentIds)
                    & Builders<WorkReportPeriod>.Filter.Eq(x => x.IsDeleted, false)
                    & Builders<WorkReportPeriod>.Filter.Eq(x => x.IsActive, true),
                    scope);

                var matchedAssignmentIds = await _ctx.WorkReportPeriods
                    .Find(periodFilter)
                    .Project(x => x.WorkAssignmentId)
                    .ToListAsync(ct);

                foreach (var assignmentId in matchedAssignmentIds.Distinct(StringComparer.Ordinal))
                {
                    if (candidateIdByAssignmentId.TryGetValue(assignmentId, out var candidateId))
                        candidatePeriodMatches.Add(candidateId);
                }
            }
        }

        return candidates
            .Where(candidate =>
            {
                var unitOk = scope.UnitIds.Count == 0
                    || candidateUnitMatches.Contains(candidate.Id)
                    || candidatePeriodMatches.Contains(candidate.Id);
                var timeOk = scope.Range == null || candidatePeriodMatches.Contains(candidate.Id);
                return unitOk && timeOk;
            })
            .ToList();
    }

    private static FilterDefinition<WorkReportPeriod> ApplyScopeToPeriodFilter(
        FilterDefinition<WorkReportPeriod> filter,
        MindMapScope scope)
    {
        if (scope.Range != null)
            filter &= BuildPeriodTimeFilter(scope.Range);

        if (scope.UnitIds.Count > 0)
            filter &= Builders<WorkReportPeriod>.Filter.In(x => x.AssigneeUnitId, scope.UnitIds);

        return filter;
    }

    private static FilterDefinition<WorkReportPeriod> BuildPeriodTimeFilter(DashboardNormalizedRange range)
    {
        var fb = Builders<WorkReportPeriod>.Filter;

        var byDueDate = fb.And(
            fb.Ne(x => x.DueAtUtc, null),
            fb.Gte(x => x.DueAtUtc, range.FromUtc),
            fb.Lte(x => x.DueAtUtc, range.ToUtc));

        var fallbackUpdated = fb.And(
            fb.Eq(x => x.DueAtUtc, null),
            fb.Gte(x => x.UpdatedAtUtc, range.FromUtc),
            fb.Lte(x => x.UpdatedAtUtc, range.ToUtc));

        return fb.Or(byDueDate, fallbackUpdated);
    }

    private static string? ResolveCandidateIdByPath(
        List<KeyValuePair<string, string>> candidatePathPairs,
        string? assignmentPath)
    {
        if (string.IsNullOrWhiteSpace(assignmentPath))
            return null;

        foreach (var pair in candidatePathPairs)
        {
            if (string.Equals(assignmentPath, pair.Value, StringComparison.Ordinal)
                || assignmentPath.StartsWith($"{pair.Value}/", StringComparison.Ordinal))
            {
                return pair.Key;
            }
        }

        return null;
    }

    private static DashboardWorkTreeWorkDto MapWork(Work work, bool hasOverduePeriod)
    {
        return new DashboardWorkTreeWorkDto
        {
            Id = work.Id,
            Code = string.IsNullOrWhiteSpace(work.Code) ? work.AutoCode : work.Code,
            Name = work.Name,
            Status = (int)work.Status,
            ActiveRootAssignmentCount = work.ActiveRootAssignmentCount,
            HasOverduePeriod = hasOverduePeriod,
            HasManualEvaluations = work.HasManualEvaluations,
            WorstEvaluationCode = work.WorstEvaluationCode,
            WorstEvaluationLabel = work.WorstEvaluationLabel,
            RootAssignmentProgressCounts = MapProgressCounts(work.RootAssignmentProgressCounts),
        };
    }

    private static DashboardTreeNodeDto MapNode(WorkAssignment assignment)
    {
        return new DashboardTreeNodeDto
        {
            Id = assignment.Id,
            WorkId = assignment.WorkId,
            ParentAssignmentId = assignment.ParentAssignmentId,
            RootAssignmentId = assignment.RootAssignmentId,
            Level = assignment.Level,
            Code = assignment.Code,
            DynamicFormTemplateId = assignment.DynamicFormTemplateId,
            DynamicFormTemplateCode = assignment.DynamicFormTemplateCode,
            DynamicFormTemplateName = assignment.DynamicFormTemplateName,
            DynamicExcelCode = assignment.DynamicExcelCode,
            DynamicExcelName = assignment.DynamicExcelName,
            Description = assignment.Description,
            SummaryText = BuildNodeSummaryText(assignment),
            IsActive = assignment.IsActive,
            ProgressStatus = assignment.ProgressStatus,
            HasAnyDuePeriod = assignment.HasAnyDuePeriod,
            HasOverduePeriod = assignment.HasOverduePeriod,
            WorstPeriodStatus = assignment.WorstPeriodStatus,
            WorstOverdueReasonCode = assignment.WorstOverdueReasonCode,
            WorstOverdueReasonLabel = assignment.WorstOverdueReasonLabel,
            LatestDueAtUtc = assignment.LatestDueAtUtc,
            ActiveChildCount = assignment.ActiveChildCount,
            HasChildren = assignment.ActiveChildCount > 0,
            ManualEvaluation = new DashboardNodeManualEvaluationDto
            {
                HasManualEvaluations = assignment.HasManualEvaluations,
                EvaluatedAssignmentCount = assignment.EvaluatedAssignmentCount,
                EvaluationCode = assignment.EvaluationCode,
                EvaluationLabel = assignment.EvaluationLabel,
                WorstEvaluationCode = assignment.WorstEvaluationCode,
                WorstEvaluationLabel = assignment.WorstEvaluationLabel,
            },
            ReportSummary = new DashboardNodeReportSummaryDto(),
            Assignees = (assignment.Assignees ?? new List<UserRef>())
                .Select(x => new DashboardNodeAssigneeDto
                {
                    UserId = x.UserId,
                    Username = x.Username ?? string.Empty,
                    FullName = x.FullName ?? x.Username ?? string.Empty,
                    UnitId = x.UnitId,
                    UnitName = x.UnitName,
                    UnitSymbol = x.UnitSymbol,
                    UnitShortName = x.UnitShortName,
                })
                .ToList(),
            ChildProgressCounts = MapProgressCounts(assignment.ChildProgressCounts),
        };
    }

    private static string BuildNodeSummaryText(WorkAssignment assignment)
    {
        var parts = new List<string>();

        if (assignment.Assignees is { Count: > 0 })
            parts.Add($"{assignment.Assignees.Count} assignee");

        if (assignment.ActiveChildCount > 0)
            parts.Add($"{assignment.ActiveChildCount} child");

        if (assignment.HasOverduePeriod)
            parts.Add("overdue");
        else if (assignment.HasAnyDuePeriod)
            parts.Add("has due period");

        if (!string.IsNullOrWhiteSpace(assignment.LatestPeriodKey))
            parts.Add($"latest {assignment.LatestPeriodKey}");

        if (!string.IsNullOrWhiteSpace(assignment.Description))
            parts.Add(assignment.Description.Trim());

        return string.Join("; ", parts);
    }

    private static DashboardMindMapTemplateUserDto MapTemplateUser(
        string assignmentId,
        string dynamicFormTemplateId,
        WorkTemplateAssignee binding,
        List<WorkReportPeriod> periods)
    {
        return new DashboardMindMapTemplateUserDto
        {
            AssignmentId = assignmentId,
            DynamicFormTemplateId = dynamicFormTemplateId,
            DynamicFormTemplateCode = binding.DynamicFormTemplateCode ?? string.Empty,
            DynamicFormTemplateName = binding.DynamicFormTemplateName ?? string.Empty,
            DynamicExcelId = binding.DynamicExcelId,
            AssigneeUserId = binding.AssigneeUserId,
            AssigneeUsername = binding.AssigneeUsername,
            AssigneeFullName = string.IsNullOrWhiteSpace(binding.AssigneeFullName)
                ? binding.AssigneeUsername
                : binding.AssigneeFullName,
            UnitId = binding.AssigneeUnitId,
            UnitLabel = PickUnitLabel(
                binding.AssigneeUnitSymbol,
                binding.AssigneeUnitShortName,
                binding.AssigneeUnitName),
            TotalReports = periods.Count,
            OverdueCount = periods.Count(x => string.Equals(MapPeriodBucket(x.Status), BucketOverdue, StringComparison.OrdinalIgnoreCase)),
            LatestDueAtUtc = periods
                .Where(x => x.DueAtUtc.HasValue)
                .OrderByDescending(x => x.DueAtUtc)
                .Select(x => x.DueAtUtc)
                .FirstOrDefault(),
            ReportBar = BuildReportBar(BuildReportSummary(periods)),
        };
    }

    private static DashboardMindMapTemplateUserDto MapTemplateUserFromPeriods(
        string assignmentId,
        string dynamicFormTemplateId,
        string assigneeUserId,
        List<WorkReportPeriod> periods)
    {
        var sample = periods.FirstOrDefault();
        return new DashboardMindMapTemplateUserDto
        {
            AssignmentId = assignmentId,
            DynamicFormTemplateId = dynamicFormTemplateId,
            DynamicFormTemplateCode = sample?.DynamicFormTemplateCode ?? string.Empty,
            DynamicFormTemplateName = sample?.DynamicFormTemplateName ?? string.Empty,
            DynamicExcelId = sample?.DynamicExcelId,
            AssigneeUserId = assigneeUserId,
            AssigneeUsername = assigneeUserId,
            AssigneeFullName = assigneeUserId,
            UnitId = periods.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.AssigneeUnitId))?.AssigneeUnitId,
            UnitLabel = periods.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.AssigneeUnitId))?.AssigneeUnitId,
            TotalReports = periods.Count,
            OverdueCount = periods.Count(x => string.Equals(MapPeriodBucket(x.Status), BucketOverdue, StringComparison.OrdinalIgnoreCase)),
            LatestDueAtUtc = periods
                .Where(x => x.DueAtUtc.HasValue)
                .OrderByDescending(x => x.DueAtUtc)
                .Select(x => x.DueAtUtc)
                .FirstOrDefault(),
            ReportBar = BuildReportBar(BuildReportSummary(periods)),
        };
    }

    private static DashboardMindMapReportRowDto MapTemplateReportRow(
        WorkReportPeriod period,
        WorkAssignment assignment,
        WorkTemplateAssignee? binding,
        WorkAssignmentReport? report)
    {
        return new DashboardMindMapReportRowDto
        {
            WorkReportPeriodId = period.Id,
            ReportId = report?.Id,
            AssignmentId = assignment.Id,
            AssignmentCode = assignment.Code,
            AssignmentName = assignment.DynamicExcelName ?? period.DynamicExcelName,
            AssigneeUserId = period.AssigneeUserId,
            AssigneeFullName = binding?.AssigneeFullName ?? binding?.AssigneeUsername ?? period.AssigneeUserId,
            AssigneeUsername = binding?.AssigneeUsername,
            UnitId = period.AssigneeUnitId ?? binding?.AssigneeUnitId,
            UnitLabel = PickUnitLabel(
                    binding?.AssigneeUnitSymbol,
                    binding?.AssigneeUnitShortName,
                    binding?.AssigneeUnitName)
                ?? period.AssigneeUnitId,
            Bucket = MapReportBucket(period.Status),
            PeriodKey = period.PeriodKey,
            PeriodStatus = (int)period.Status,
            ReportStatus = report == null ? null : (int)report.Status,
            DueAtUtc = period.DueAtUtc,
            SubmittedAtUtc = report?.SubmittedAtUtc ?? period.LastSubmittedAtUtc,
            ApprovedAtUtc = report?.ApprovedAtUtc ?? period.LastReviewedAtUtc,
            CurrentProgressStatus = period.CurrentProgressStatus,
            ReportReason = period.ReportReason,
            Difficulties = period.Difficulties,
            ProposedSolution = period.ProposedSolution,
            LateReason = period.LateReason,
            ReturnReason = period.ReturnReason,
            ReviewerComment = period.ReviewerComment,
            ReviewerEvaluation = period.ReviewerEvaluation,
        };
    }

    private static DashboardProgressCountDto MapProgressCounts(WorkProgressCountSnapshot? counts)
    {
        var source = counts ?? new WorkProgressCountSnapshot();
        return new DashboardProgressCountDto
        {
            NotStarted = source.NotStarted,
            InProgress = source.InProgress,
            Completed = source.Completed,
            AtRiskOverdue = source.AtRiskOverdue,
            Overdue = source.Overdue,
            Total = source.TotalActive,
        };
    }

    private static DashboardNodeReportSummaryDto BuildReportSummary(IEnumerable<WorkReportPeriod> periods)
    {
        var result = new DashboardNodeReportSummaryDto();

        foreach (var period in periods)
        {
            result.Total++;

            switch (period.Status)
            {
                case WorkReportPeriodStatus.Pending:
                    result.PendingCount++;
                    break;
                case WorkReportPeriodStatus.Draft:
                    result.DraftCount++;
                    break;
                case WorkReportPeriodStatus.Submitted:
                    result.SubmittedCount++;
                    break;
                case WorkReportPeriodStatus.Approved:
                    result.ApprovedCount++;
                    break;
                case WorkReportPeriodStatus.OverduePending:
                    result.OverduePendingCount++;
                    break;
                case WorkReportPeriodStatus.OverdueDraft:
                    result.OverdueDraftCount++;
                    break;
                case WorkReportPeriodStatus.OverdueSubmitted:
                    result.OverdueSubmittedCount++;
                    break;
                case WorkReportPeriodStatus.OverdueApproved:
                    result.OverdueApprovedCount++;
                    break;
            }
        }

        return result;
    }

    private static DashboardStackedBarDto BuildUnitBar(List<DashboardMindMapUnitRowDto> rows)
    {
        return new DashboardStackedBarDto
        {
            Key = "unit",
            Label = "Theo don vi / nguoi",
            Total = rows.Count,
            Segments = BuildUnitSegments(rows.GroupBy(x => x.Bucket).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase)),
        };
    }

    private static DashboardStackedBarDto BuildReportBar(DashboardNodeReportSummaryDto summary)
    {
        var overdue = summary.OverduePendingCount +
                      summary.OverdueDraftCount +
                      summary.OverdueSubmittedCount +
                      summary.OverdueApprovedCount;

        return new DashboardStackedBarDto
        {
            Key = "report",
            Label = "Theo tong report",
            Total = summary.Total,
            Segments = new List<DashboardStackedBarSegmentDto>
            {
                BuildSegment(BucketPending, "Chua bat dau", summary.PendingCount),
                BuildSegment(BucketDraft, "Draft", summary.DraftCount),
                BuildSegment(BucketSubmitted, "Da gui", summary.SubmittedCount),
                BuildSegment(BucketApproved, "Da duyet", summary.ApprovedCount),
                BuildSegment(BucketOverdue, "Qua han", overdue),
            },
        };
    }

    private static List<DashboardStackedBarSegmentDto> BuildUnitSegments(Dictionary<string, int> values)
    {
        return new List<DashboardStackedBarSegmentDto>
        {
            BuildSegment(BucketTodo, "Chua lam", values.GetValueOrDefault(BucketTodo)),
            BuildSegment(BucketDone, "Da lam", values.GetValueOrDefault(BucketDone)),
            BuildSegment(BucketOverdue, "Cham muon", values.GetValueOrDefault(BucketOverdue)),
        };
    }

    private static DashboardStackedBarSegmentDto BuildSegment(string key, string label, int value)
    {
        return new DashboardStackedBarSegmentDto
        {
            Key = key,
            Label = label,
            Value = value,
            Color = BucketColors[key],
        };
    }

    private static List<DashboardMindMapUnitRowDto> BuildUnitRows(SubtreeContext ctx)
    {
        var groups = ctx.Periods
            .GroupBy(x => BuildUnitGroupKey(x.AssigneeUserId, x.AssigneeUnitId), StringComparer.Ordinal)
            .ToList();

        var rows = new List<DashboardMindMapUnitRowDto>(groups.Count);

        foreach (var group in groups)
        {
            var periods = group.ToList();
            var sample = periods
                .OrderByDescending(GetPeriodSeverity)
                .ThenByDescending(x => x.DueAtUtc)
                .First();

            ctx.AssignmentById.TryGetValue(sample.WorkAssignmentId, out var assignment);
            var assignee = assignment?.Assignees?.FirstOrDefault(a => string.Equals(a.UserId, sample.AssigneeUserId, StringComparison.Ordinal));

            var todoCount = periods.Count(x => string.Equals(MapPeriodBucket(x.Status), BucketTodo, StringComparison.OrdinalIgnoreCase));
            var doneCount = periods.Count(x => string.Equals(MapPeriodBucket(x.Status), BucketDone, StringComparison.OrdinalIgnoreCase));
            var overdueCount = periods.Count(x => string.Equals(MapPeriodBucket(x.Status), BucketOverdue, StringComparison.OrdinalIgnoreCase));

            rows.Add(new DashboardMindMapUnitRowDto
            {
                AssigneeUserId = sample.AssigneeUserId,
                AssigneeUsername = assignee?.Username,
                AssigneeFullName = assignee?.FullName ?? assignee?.Username ?? sample.AssigneeUserId,
                UnitId = sample.AssigneeUnitId,
                UnitLabel = PickUnitLabel(assignee?.UnitSymbol, assignee?.UnitShortName, assignee?.UnitName) ?? sample.AssigneeUnitId,
                Bucket = ResolveUnitBucket(todoCount, doneCount, overdueCount),
                TotalReports = periods.Count,
                TodoCount = todoCount,
                DoneCount = doneCount,
                OverdueCount = overdueCount,
                LatestPeriodKey = sample.PeriodKey,
                LatestDueAtUtc = sample.DueAtUtc,
                CurrentProgressStatus = sample.CurrentProgressStatus,
                Difficulties = sample.Difficulties,
                LateReason = sample.LateReason,
                ReturnReason = sample.ReturnReason,
                ReviewerComment = sample.ReviewerComment,
                WorstOverdueReasonCode = assignment?.WorstOverdueReasonCode,
                WorstOverdueReasonLabel = assignment?.WorstOverdueReasonLabel,
            });
        }

        return rows;
    }

    private static List<DashboardMindMapReportRowDto> BuildReportRows(SubtreeContext ctx)
    {
        var rows = new List<DashboardMindMapReportRowDto>(ctx.Periods.Count);

        foreach (var period in ctx.Periods)
        {
            ctx.AssignmentById.TryGetValue(period.WorkAssignmentId, out var assignment);

            WorkAssignmentReport? report = null;
            if (!string.IsNullOrWhiteSpace(period.CurrentReportId))
                ctx.CurrentReportsById.TryGetValue(period.CurrentReportId!, out report);

            var assignee = assignment?.Assignees?.FirstOrDefault(a => string.Equals(a.UserId, period.AssigneeUserId, StringComparison.Ordinal));

            rows.Add(new DashboardMindMapReportRowDto
            {
                WorkReportPeriodId = period.Id,
                ReportId = report?.Id,
                AssignmentId = period.WorkAssignmentId,
                AssignmentCode = assignment?.Code,
                AssignmentName = assignment?.DynamicExcelName ?? period.DynamicExcelName,
                AssigneeUserId = period.AssigneeUserId,
                AssigneeFullName = assignee?.FullName ?? assignee?.Username ?? period.AssigneeUserId,
                AssigneeUsername = assignee?.Username,
                UnitId = period.AssigneeUnitId,
                UnitLabel = PickUnitLabel(assignee?.UnitSymbol, assignee?.UnitShortName, assignee?.UnitName) ?? period.AssigneeUnitId,
                Bucket = MapReportBucket(period.Status),
                PeriodKey = period.PeriodKey,
                PeriodStatus = (int)period.Status,
                ReportStatus = report == null ? null : (int)report.Status,
                DueAtUtc = period.DueAtUtc,
                SubmittedAtUtc = report?.SubmittedAtUtc,
                ApprovedAtUtc = report?.ApprovedAtUtc,
                CurrentProgressStatus = period.CurrentProgressStatus,
                ReportReason = period.ReportReason,
                Difficulties = period.Difficulties,
                ProposedSolution = period.ProposedSolution,
                LateReason = period.LateReason,
                ReturnReason = period.ReturnReason,
                ReviewerComment = period.ReviewerComment,
                ReviewerEvaluation = period.ReviewerEvaluation,
            });
        }

        return rows;
    }

    private static string BuildUnitGroupKey(string? assigneeUserId, string? assigneeUnitId)
        => $"{assigneeUserId ?? "-"}::{assigneeUnitId ?? "-"}";

    private static string NormalizeUnitBucket(string? bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            return BucketAll;

        return bucket.Trim().ToUpperInvariant() switch
        {
            BucketTodo => BucketTodo,
            BucketDone => BucketDone,
            BucketOverdue => BucketOverdue,
            _ => BucketAll,
        };
    }

    private static string NormalizeReportBucket(string? bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            return BucketAll;

        return bucket.Trim().ToUpperInvariant() switch
        {
            BucketPending => BucketPending,
            BucketDraft => BucketDraft,
            BucketSubmitted => BucketSubmitted,
            BucketApproved => BucketApproved,
            BucketOverdue => BucketOverdue,
            BucketTodo => BucketTodo,
            BucketDone => BucketDone,
            _ => BucketAll,
        };
    }

    private static List<string> NormalizeReportBuckets(IEnumerable<string>? buckets)
    {
        return (buckets ?? Array.Empty<string>())
            .Select(NormalizeReportBucket)
            .Where(x => !string.Equals(x, BucketAll, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveUnitBucket(int todoCount, int doneCount, int overdueCount)
    {
        if (overdueCount > 0) return BucketOverdue;
        if (todoCount > 0) return BucketTodo;
        if (doneCount > 0) return BucketDone;
        return BucketTodo;
    }

    private static string MapPeriodBucket(WorkReportPeriodStatus status)
    {
        if (WorkReportPeriodStatusHelper.IsOverdue(status))
            return BucketOverdue;

        if (WorkReportPeriodStatusHelper.IsWaitingReview(status) ||
            WorkReportPeriodStatusHelper.IsTerminal(status))
            return BucketDone;

        return status switch
        {
            WorkReportPeriodStatus.Pending => BucketTodo,
            WorkReportPeriodStatus.Draft => BucketTodo,
            _ => BucketTodo,
        };
    }

    private static string MapReportBucket(WorkReportPeriodStatus status)
    {
        if (WorkReportPeriodStatusHelper.IsOverdue(status))
            return BucketOverdue;

        return status switch
        {
            WorkReportPeriodStatus.Pending => BucketPending,
            WorkReportPeriodStatus.Draft => BucketDraft,
            WorkReportPeriodStatus.Submitted => BucketSubmitted,
            WorkReportPeriodStatus.Approved => BucketApproved,
            _ => BucketPending,
        };
    }

    private static List<WorkReportPeriodStatus> MapReportBucketsToPeriodStatuses(List<string> buckets)
    {
        var result = new List<WorkReportPeriodStatus>();

        foreach (var bucket in buckets)
        {
            switch (bucket)
            {
                case BucketPending:
                    result.Add(WorkReportPeriodStatus.Pending);
                    break;
                case BucketDraft:
                    result.Add(WorkReportPeriodStatus.Draft);
                    break;
                case BucketSubmitted:
                    result.Add(WorkReportPeriodStatus.Submitted);
                    break;
                case BucketApproved:
                    result.Add(WorkReportPeriodStatus.Approved);
                    break;
                case BucketOverdue:
                    result.AddRange(WorkReportPeriodStatusHelper.OverdueStatuses);
                    break;
                case BucketTodo:
                    result.AddRange(new[]
                    {
                        WorkReportPeriodStatus.Pending,
                        WorkReportPeriodStatus.Draft,
                    });
                    break;
                case BucketDone:
                    result.Add(WorkReportPeriodStatus.Submitted);
                    result.AddRange(WorkReportPeriodStatusHelper.TerminalStatuses);
                    break;
            }
        }

        return result.Distinct().ToList();
    }

    private static bool IsReportBucketMatch(string rowBucket, string requestedBucket)
    {
        if (string.Equals(requestedBucket, BucketAll, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(rowBucket, requestedBucket, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(requestedBucket, BucketTodo, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(rowBucket, BucketPending, StringComparison.OrdinalIgnoreCase)
                || string.Equals(rowBucket, BucketDraft, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(requestedBucket, BucketDone, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(rowBucket, BucketSubmitted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(rowBucket, BucketApproved, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static int GetPeriodSeverity(WorkReportPeriod period)
    {
        return period.Status switch
        {
            WorkReportPeriodStatus.OverdueSubmitted => 7,
            WorkReportPeriodStatus.OverdueDraft => 6,
            WorkReportPeriodStatus.OverduePending => 5,
            WorkReportPeriodStatus.Pending => 4,
            WorkReportPeriodStatus.Draft => 3,
            WorkReportPeriodStatus.Submitted => 2,
            WorkReportPeriodStatus.OverdueApproved => 1,
            WorkReportPeriodStatus.Approved => 0,
            _ => 0,
        };
    }

    private static string? PickUnitLabel(string? symbol, string? shortName, string? unitName)
        => !string.IsNullOrWhiteSpace(symbol)
            ? symbol
            : !string.IsNullOrWhiteSpace(shortName)
                ? shortName
                : unitName;

    private sealed record MindMapScope(
        DashboardNormalizedRange? Range,
        HashSet<string> UnitIds)
    {
        public bool HasFilters => Range != null || UnitIds.Count > 0;
    }

    private sealed record WorkAccessContext(
        Work Work,
        bool FullAccess,
        List<WorkAssignment> EntryAssignments);

    private sealed record SubtreeContext(
        WorkAssignment Node,
        List<WorkAssignment> Assignments,
        Dictionary<string, WorkAssignment> AssignmentById,
        List<WorkReportPeriod> Periods,
        Dictionary<string, WorkAssignmentReport> CurrentReportsById);
}
