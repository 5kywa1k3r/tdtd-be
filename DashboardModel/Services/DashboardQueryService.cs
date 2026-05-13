using MongoDB.Bson;
using MongoDB.Driver;
using System.Security.Cryptography;
using System.Text;
using tdtd_be.Caching;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Cache;
using tdtd_be.Common.Errors;
using tdtd_be.DashboardModel.DTOs;
using tdtd_be.Data;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;

namespace tdtd_be.DashboardModel.Services;

public interface IDashboardQueryService
{
    Task<MyWorksDashboardResponse> GetMyWorksSummaryAsync(
        MyWorksDashboardRequest req,
        CancellationToken ct = default);

    Task<WorkDashboardDetailDto> GetWorkDetailAsync(
        string workId,
        WorkDashboardDetailRequest? req,
        CancellationToken ct = default);
}

public sealed class DashboardQueryService : IDashboardQueryService
{
    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly RedisDashboardCache _dashboardCache;

    public DashboardQueryService(
        MongoDbContext ctx,
        MeAccessor me,
        RedisDashboardCache dashboardCache)
    {
        _ctx = ctx;
        _me = me;
        _dashboardCache = dashboardCache;
    }

    public async Task<MyWorksDashboardResponse> GetMyWorksSummaryAsync(
        MyWorksDashboardRequest req,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        req ??= new MyWorksDashboardRequest();

        var range = DashboardTimeRangeHelper.NormalizeMonthRange(req.FromUtc, req.ToUtc);
        var keyword = NormalizeKeyword(req.Keyword);
        var unitIds = NormalizeIds(req.UnitIds);
        var unitHash = BuildStableHash(unitIds);

        var cacheKey = CacheKeys.DashboardMyWorksSummary(
            me.Id,
            range.FromUtc,
            range.ToUtc,
            keyword,
            unitHash);

        return await _dashboardCache.GetOrCreateAsync(
            cacheKey,
            async innerCt =>
            {
                var rows = await LoadMyWorkRowsAsync(me.Id, req, range, innerCt);

                return new MyWorksDashboardResponse
                {
                    Range = new DashboardRangeDto
                    {
                        FromUtc = range.FromUtc,
                        ToUtc = range.ToUtc,
                        Label = range.Label
                    },
                    Summary = new MyWorksDashboardSummaryDto
                    {
                        TotalWorks = rows.Count,
                        ActiveRootAssignmentCount = rows.Sum(x => x.ActiveRootAssignmentCount),
                        ManualEvaluatedWorkCount = rows.Count(x => x.HasManualEvaluations),
                        RootAssignmentProgressCounts = SumProgressCounts(rows.Select(x => x.RootAssignmentProgressCounts))
                    },
                    Works = rows
                };
            },
            ct: ct,
            forceRefresh: req.ForceRefresh,
            ttl: TimeSpan.FromMinutes(30));
    }

    public async Task<WorkDashboardDetailDto> GetWorkDetailAsync(
        string workId,
        WorkDashboardDetailRequest? req,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.DASHBOARD_WORK_ID_REQUIRED, new { field = "workId", value = workId });

        var me = _me.RequireMe();
        req ??= new WorkDashboardDetailRequest();

        var range = DashboardTimeRangeHelper.NormalizeMonthRange(req.FromUtc, req.ToUtc);
        var unitIds = NormalizeIds(req.UnitIds);
        var unitHash = BuildStableHash(unitIds);

        var cacheKey = CacheKeys.DashboardWorkDetail(
            me.Id,
            workId,
            range.FromUtc,
            range.ToUtc,
            unitHash,
            req.IncludeRootAssignments,
            req.IncludeReportSummary);

        return await _dashboardCache.GetOrCreateAsync(
            cacheKey,
            async innerCt =>
            {
                var work = await _ctx.Works
                    .Find(x => x.Id == workId && x.CreatedByUserId == me.Id && !x.IsDeleted)
                    .FirstOrDefaultAsync(innerCt);

                if (work is null)
                    throw AppExceptionFactory.NotFound(AppErrorCode.DASHBOARD_WORK_NOT_FOUND, new { workId });

                var roots = await LoadRootAssignmentsForSingleWorkAsync(me.Id, workId, unitIds, innerCt);

                var result = new WorkDashboardDetailDto
                {
                    Work = unitIds.Count == 0
                        ? MapMyWorkRow(work)
                        : MapMyWorkRow(work, roots)
                };

                if (!req.IncludeRootAssignments && !req.IncludeReportSummary)
                {
                    return result;
                }

                Dictionary<string, List<string>> assignmentIdsByRoot = new(StringComparer.Ordinal);
                List<WorkReportPeriod> periods = new();

                if (roots.Count > 0)
                {
                    var rootIds = roots.Select(x => x.Id).Distinct(StringComparer.Ordinal).ToList();

                    assignmentIdsByRoot = await LoadAssignmentIdsByRootAsync(me.Id, workId, rootIds, innerCt);

                    var assignmentIds = assignmentIdsByRoot
                        .Values
                        .SelectMany(x => x)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    if (assignmentIds.Count > 0 && (req.IncludeReportSummary || req.IncludeRootAssignments))
                    {
                        periods = await LoadPeriodsByAssignmentIdsAsync(workId, assignmentIds, unitIds, range, innerCt);
                    }
                }

                if (req.IncludeRootAssignments)
                {
                    result.RootAssignments = roots
                        .Select(root =>
                        {
                            assignmentIdsByRoot.TryGetValue(root.Id, out var descendantIds);
                            descendantIds ??= new List<string>();

                            var rootPeriods = descendantIds.Count == 0
                                ? Enumerable.Empty<WorkReportPeriod>()
                                : periods.Where(p => descendantIds.Contains(p.WorkAssignmentId, StringComparer.Ordinal));

                            return MapRootAssignmentRow(root, BuildReportSummary(rootPeriods));
                        })
                        .ToList();
                }

                if (req.IncludeReportSummary)
                {
                    result.ReportSummary = BuildReportSummary(periods);
                }

                return result;
            },
            ct: ct,
            forceRefresh: req.ForceRefresh,
            ttl: TimeSpan.FromMinutes(30));
    }

    private async Task<List<MyWorkSummaryRowDto>> LoadMyWorkRowsAsync(
        string actorUserId,
        MyWorksDashboardRequest req,
        DashboardNormalizedRange range,
        CancellationToken ct)
    {
        var works = await LoadOwnedWorksAsync(actorUserId, req.Keyword, range, ct);
        if (works.Count == 0)
            return new List<MyWorkSummaryRowDto>();

        var unitIds = NormalizeIds(req.UnitIds);
        if (unitIds.Count == 0)
        {
            return works
                .Select(MapMyWorkRow)
                .ToList();
        }

        var workIds = works.Select(x => x.Id).Distinct(StringComparer.Ordinal).ToList();

        var roots = await LoadRootAssignmentsForWorksAsync(actorUserId, workIds, unitIds, ct);
        if (roots.Count == 0)
            return new List<MyWorkSummaryRowDto>();

        var rootsByWorkId = roots
            .GroupBy(x => x.WorkId)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        return works
            .Where(w => rootsByWorkId.ContainsKey(w.Id))
            .Select(w => MapMyWorkRow(w, rootsByWorkId[w.Id]))
            .ToList();
    }

    private async Task<List<Work>> LoadOwnedWorksAsync(
        string actorUserId,
        string? keyword,
        DashboardNormalizedRange range,
        CancellationToken ct)
    {
        var f = Builders<Work>.Filter.Eq(x => x.IsDeleted, false)
              & Builders<Work>.Filter.Eq(x => x.CreatedByUserId, actorUserId)
              & BuildWorkTimeFilter(range);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var regex = new BsonRegularExpression(keyword.Trim(), "i");
            f &= Builders<Work>.Filter.Or(
                Builders<Work>.Filter.Regex(x => x.AutoCode, regex),
                Builders<Work>.Filter.Regex(x => x.Code, regex),
                Builders<Work>.Filter.Regex(x => x.Name, regex)
            );
        }

        return await _ctx.Works
            .Find(f)
            .SortByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
    }

    private async Task<List<WorkAssignment>> LoadRootAssignmentsForWorksAsync(
        string actorUserId,
        List<string> workIds,
        List<string> unitIds,
        CancellationToken ct)
    {
        if (workIds.Count == 0)
            return new List<WorkAssignment>();

        var f = Builders<WorkAssignment>.Filter.In(x => x.WorkId, workIds)
              & Builders<WorkAssignment>.Filter.Eq(x => x.CreatedByUserId, actorUserId)
              & Builders<WorkAssignment>.Filter.Eq(x => x.ParentAssignmentId, null as string)
              & Builders<WorkAssignment>.Filter.Eq(x => x.IsActive, true)
              & Builders<WorkAssignment>.Filter.Eq(x => x.IsDeleted, false);

        if (unitIds.Count > 0)
        {
            f &= Builders<WorkAssignment>.Filter.ElemMatch(
                x => x.Assignees,
                a => a.UnitId != null && unitIds.Contains(a.UnitId));
        }

        return await _ctx.WorkAssignments
            .Find(f)
            .SortByDescending(x => x.HasOverduePeriod)
            .ThenByDescending(x => x.LatestDueAtUtc)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .ThenBy(x => x.Path)
            .ToListAsync(ct);
    }

    private Task<List<WorkAssignment>> LoadRootAssignmentsForSingleWorkAsync(
        string actorUserId,
        string workId,
        List<string> unitIds,
        CancellationToken ct)
    {
        return LoadRootAssignmentsForWorksAsync(actorUserId, new List<string> { workId }, unitIds, ct);
    }

    private async Task<Dictionary<string, List<string>>> LoadAssignmentIdsByRootAsync(
        string actorUserId,
        string workId,
        List<string> rootIds,
        CancellationToken ct)
    {
        if (rootIds.Count == 0)
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var items = await _ctx.WorkAssignments
            .Find(x =>
                x.WorkId == workId &&
                x.CreatedByUserId == actorUserId &&
                x.IsActive &&
                !x.IsDeleted &&
                rootIds.Contains(x.RootAssignmentId))
            .Project(x => new AssignmentRootRef
            {
                Id = x.Id,
                RootAssignmentId = x.RootAssignmentId
            })
            .ToListAsync(ct);

        return items
            .GroupBy(x => x.RootAssignmentId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Id).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
    }

    private async Task<List<WorkReportPeriod>> LoadPeriodsByAssignmentIdsAsync(
        string workId,
        List<string> assignmentIds,
        List<string> unitIds,
        DashboardNormalizedRange range,
        CancellationToken ct)
    {
        if (assignmentIds.Count == 0)
            return new List<WorkReportPeriod>();

        var f = Builders<WorkReportPeriod>.Filter.Eq(x => x.WorkId, workId)
              & Builders<WorkReportPeriod>.Filter.In(x => x.WorkAssignmentId, assignmentIds)
              & Builders<WorkReportPeriod>.Filter.Eq(x => x.IsDeleted, false)
              & Builders<WorkReportPeriod>.Filter.Eq(x => x.IsActive, true)
              & BuildPeriodTimeFilter(range);

        if (unitIds.Count > 0)
        {
            f &= Builders<WorkReportPeriod>.Filter.In(x => x.AssigneeUnitId, unitIds);
        }

        return await _ctx.WorkReportPeriods
            .Find(f)
            .ToListAsync(ct);
    }

    private FilterDefinition<Work> BuildWorkTimeFilter(DashboardNormalizedRange range)
    {
        var fb = Builders<Work>.Filter;

        var overlapByStartEnd = fb.And(
            fb.Ne(x => x.StartDate, null),
            fb.Lte(x => x.StartDate, range.ToDate),
            fb.Or(
                fb.Eq(x => x.EndDate, null),
                fb.Gte(x => x.EndDate, range.FromDate)
            )
        );

        var byDueDate = fb.And(
            fb.Ne(x => x.DueDate, null),
            fb.Gte(x => x.DueDate, range.FromDate),
            fb.Lte(x => x.DueDate, range.ToDate)
        );

        var fallbackUpdated = fb.And(
            fb.Eq(x => x.StartDate, null),
            fb.Eq(x => x.EndDate, null),
            fb.Eq(x => x.DueDate, null),
            fb.Gte(x => x.UpdatedAtUtc, range.FromUtc),
            fb.Lte(x => x.UpdatedAtUtc, range.ToUtc)
        );

        return fb.Or(overlapByStartEnd, byDueDate, fallbackUpdated);
    }

    private FilterDefinition<WorkReportPeriod> BuildPeriodTimeFilter(DashboardNormalizedRange range)
    {
        var fb = Builders<WorkReportPeriod>.Filter;

        var byDueDate = fb.And(
            fb.Ne(x => x.DueAtUtc, null),
            fb.Gte(x => x.DueAtUtc, range.FromUtc),
            fb.Lte(x => x.DueAtUtc, range.ToUtc)
        );

        var fallbackUpdated = fb.And(
            fb.Eq(x => x.DueAtUtc, null),
            fb.Gte(x => x.UpdatedAtUtc, range.FromUtc),
            fb.Lte(x => x.UpdatedAtUtc, range.ToUtc)
        );

        return fb.Or(byDueDate, fallbackUpdated);
    }

    private static MyWorkSummaryRowDto MapMyWorkRow(Work x)
    {
        return new MyWorkSummaryRowDto
        {
            WorkId = x.Id,
            WorkCode = string.IsNullOrWhiteSpace(x.Code) ? x.AutoCode : x.Code,
            WorkName = x.Name,
            WorkType = x.Type.ToString(),
            Status = (int)x.Status,
            StartDate = x.StartDate,
            EndDate = x.EndDate,
            DueDate = x.DueDate,
            UpdatedAtUtc = x.UpdatedAtUtc,

            ActiveRootAssignmentCount = x.ActiveRootAssignmentCount,
            RootAssignmentProgressCounts = MapProgressCounts(x.RootAssignmentProgressCounts),

            HasManualEvaluations = x.HasManualEvaluations,
            EvaluatedAssignmentCount = x.EvaluatedAssignmentCount,
            WorstEvaluationCode = x.WorstEvaluationCode,
            WorstEvaluationLabel = x.WorstEvaluationLabel
        };
    }

    private static MyWorkSummaryRowDto MapMyWorkRow(Work x, List<WorkAssignment> filteredRoots)
    {
        var progressCounts = SumProgressCounts(filteredRoots.Select(r => BuildSingleProgressCount(r.ProgressStatus)));
        var (worstEvaluationCode, worstEvaluationLabel) = PickWorstEvaluation(filteredRoots);

        return new MyWorkSummaryRowDto
        {
            WorkId = x.Id,
            WorkCode = string.IsNullOrWhiteSpace(x.Code) ? x.AutoCode : x.Code,
            WorkName = x.Name,
            WorkType = x.Type.ToString(),
            Status = (int)x.Status,
            StartDate = x.StartDate,
            EndDate = x.EndDate,
            DueDate = x.DueDate,
            UpdatedAtUtc = x.UpdatedAtUtc,

            ActiveRootAssignmentCount = filteredRoots.Count,
            RootAssignmentProgressCounts = progressCounts,

            HasManualEvaluations = filteredRoots.Any(r => r.HasManualEvaluations || !string.IsNullOrWhiteSpace(r.EvaluationCode)),
            EvaluatedAssignmentCount = filteredRoots.Sum(r => r.EvaluatedAssignmentCount),
            WorstEvaluationCode = worstEvaluationCode,
            WorstEvaluationLabel = worstEvaluationLabel
        };
    }

    private static WorkDashboardRootAssignmentRowDto MapRootAssignmentRow(
        WorkAssignment x,
        DashboardNodeReportSummaryDto reportSummary)
    {
        return new WorkDashboardRootAssignmentRowDto
        {
            AssignmentId = x.Id,
            WorkId = x.WorkId,
            Code = x.Code,
            DynamicExcelId = x.DynamicExcelId,
            DynamicExcelCode = x.DynamicExcelCode,
            DynamicExcelName = x.DynamicExcelName,
            Description = x.Description,
            IsActive = x.IsActive,
            ProgressStatus = x.ProgressStatus,
            HasAnyDuePeriod = x.HasAnyDuePeriod,
            HasOverduePeriod = x.HasOverduePeriod,
            WorstPeriodStatus = x.WorstPeriodStatus,
            WorstOverdueReasonCode = x.WorstOverdueReasonCode,
            WorstOverdueReasonLabel = x.WorstOverdueReasonLabel,
            LatestDueAtUtc = x.LatestDueAtUtc,
            ActiveChildCount = x.ActiveChildCount,
            ChildProgressCounts = MapProgressCounts(x.ChildProgressCounts),
            HasManualEvaluations = x.HasManualEvaluations,
            EvaluatedAssignmentCount = x.EvaluatedAssignmentCount,
            EvaluationCode = x.EvaluationCode,
            EvaluationLabel = x.EvaluationLabel,
            WorstEvaluationCode = x.WorstEvaluationCode,
            WorstEvaluationLabel = x.WorstEvaluationLabel,
            ReportSummary = reportSummary,
            Assignees = MapAssignees(x.Assignees)
        };
    }

    private static DashboardNodeReportSummaryDto BuildReportSummary(IEnumerable<WorkReportPeriod> periods)
    {
        var rs = new DashboardNodeReportSummaryDto();

        foreach (var p in periods)
        {
            switch (p.Status)
            {
                case WorkReportPeriodStatus.Pending:
                    rs.PendingCount++;
                    break;
                case WorkReportPeriodStatus.Draft:
                    rs.DraftCount++;
                    break;
                case WorkReportPeriodStatus.Submitted:
                    rs.SubmittedCount++;
                    break;
                case WorkReportPeriodStatus.Approved:
                    rs.ApprovedCount++;
                    break;
                case WorkReportPeriodStatus.OverduePending:
                    rs.OverduePendingCount++;
                    break;
                case WorkReportPeriodStatus.OverdueDraft:
                    rs.OverdueDraftCount++;
                    break;
                case WorkReportPeriodStatus.OverdueSubmitted:
                    rs.OverdueSubmittedCount++;
                    break;
                case WorkReportPeriodStatus.OverdueApproved:
                    rs.OverdueApprovedCount++;
                    break;
            }
        }

        rs.Total =
            rs.PendingCount +
            rs.DraftCount +
            rs.SubmittedCount +
            rs.ApprovedCount +
            rs.OverduePendingCount +
            rs.OverdueDraftCount +
            rs.OverdueSubmittedCount +
            rs.OverdueApprovedCount;

        return rs;
    }

    private static DashboardProgressCountDto MapProgressCounts(WorkProgressCountSnapshot? x)
    {
        if (x is null)
            return new DashboardProgressCountDto();

        return new DashboardProgressCountDto
        {
            NotStarted = x.NotStarted,
            InProgress = x.InProgress,
            Completed = x.Completed,
            AtRiskOverdue = x.AtRiskOverdue,
            Overdue = x.Overdue,
            Total = x.TotalActive
        };
    }

    private static DashboardProgressCountDto BuildSingleProgressCount(int progressStatus)
    {
        var rs = new DashboardProgressCountDto();

        switch ((WorkAssignmentProgressStatus)progressStatus)
        {
            case WorkAssignmentProgressStatus.NotStarted:
                rs.NotStarted = 1;
                break;
            case WorkAssignmentProgressStatus.InProgress:
                rs.InProgress = 1;
                break;
            case WorkAssignmentProgressStatus.Completed:
                rs.Completed = 1;
                break;
            case WorkAssignmentProgressStatus.AtRiskOverdue:
                rs.AtRiskOverdue = 1;
                break;
            case WorkAssignmentProgressStatus.Overdue:
                rs.Overdue = 1;
                break;
        }

        rs.Total = rs.NotStarted + rs.InProgress + rs.Completed + rs.AtRiskOverdue + rs.Overdue;
        return rs;
    }

    private static DashboardProgressCountDto SumProgressCounts(IEnumerable<DashboardProgressCountDto> items)
    {
        var rs = new DashboardProgressCountDto();

        foreach (var x in items)
        {
            rs.NotStarted += x.NotStarted;
            rs.InProgress += x.InProgress;
            rs.Completed += x.Completed;
            rs.AtRiskOverdue += x.AtRiskOverdue;
            rs.Overdue += x.Overdue;
        }

        rs.Total = rs.NotStarted + rs.InProgress + rs.Completed + rs.AtRiskOverdue + rs.Overdue;
        return rs;
    }

    private static List<DashboardNodeAssigneeDto> MapAssignees(IEnumerable<UserRef>? assignees)
    {
        return (assignees ?? Enumerable.Empty<UserRef>())
            .Select(x => new DashboardNodeAssigneeDto
            {
                UserId = x.UserId,
                Username = x.Username,
                FullName = x.FullName,
                UnitId = x.UnitId,
                UnitName = x.UnitName,
                UnitSymbol = x.UnitSymbol,
                UnitShortName = x.UnitShortName
            })
            .ToList();
    }

    private static (string? Code, string? Label) PickWorstEvaluation(IEnumerable<WorkAssignment> roots)
    {
        foreach (var x in roots)
        {
            if (!string.IsNullOrWhiteSpace(x.WorstEvaluationCode) || !string.IsNullOrWhiteSpace(x.WorstEvaluationLabel))
                return (x.WorstEvaluationCode, x.WorstEvaluationLabel);

            if (!string.IsNullOrWhiteSpace(x.EvaluationCode) || !string.IsNullOrWhiteSpace(x.EvaluationLabel))
                return (x.EvaluationCode, x.EvaluationLabel);
        }

        return (null, null);
    }


    private static List<string> NormalizeIds(IEnumerable<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeKeyword(string? keyword)
    {
        return string.IsNullOrWhiteSpace(keyword)
            ? "_"
            : keyword.Trim().ToLowerInvariant();
    }

    private static string BuildStableHash(IEnumerable<string>? values)
    {
        var normalized = string.Join("|",
            (values ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(normalized))
            return "_";

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..12];
    }

    private sealed class AssignmentRootRef
    {
        public string Id { get; set; } = string.Empty;
        public string RootAssignmentId { get; set; } = string.Empty;
    }
}
