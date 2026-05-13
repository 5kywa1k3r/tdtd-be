using MongoDB.Bson;
using MongoDB.Driver;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Cache;
using tdtd_be.DashboardModel.DTOs;
using tdtd_be.Data;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.Common.Time;

namespace tdtd_be.DashboardModel.Services;

public interface IDashboardOverviewService
{
    Task<DashboardOverviewResponse> GetOverviewAsync(
        DashboardOverviewRequest? req,
        CancellationToken ct = default);

    Task<List<DashboardReportAssignmentOptionDto>> GetReportAssignmentOptionsAsync(
        DashboardReportAssignmentOptionsRequest? req,
        CancellationToken ct = default);
}

public sealed class DashboardOverviewService : IDashboardOverviewService
{
    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly RedisDashboardCache _cache;

    public DashboardOverviewService(
        MongoDbContext ctx,
        MeAccessor me,
        RedisDashboardCache cache)
    {
        _ctx = ctx;
        _me = me;
        _cache = cache;
    }

    public async Task<DashboardOverviewResponse> GetOverviewAsync(
        DashboardOverviewRequest? req,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        req ??= new DashboardOverviewRequest();

        var mode = NormalizeMode(req.Mode);
        var range = DashboardTimeRangeHelper.NormalizeMonthRange(req.FromUtc, req.ToUtc);
        var unitIds = NormalizeIds(req.UnitIds);
        var assignmentId = string.IsNullOrWhiteSpace(req.AssignmentId) ? "_" : req.AssignmentId.Trim();
        var topUnitCount = req.TopUnitCount <= 0 ? 3 : Math.Min(req.TopUnitCount, 10);

        var cacheKey = BuildCacheKey(me.Id, mode, range, unitIds, assignmentId, topUnitCount);

        return await _cache.GetOrCreateAsync(
            cacheKey,
            innerCt => LoadOverviewAsync(me.Id, mode, range, unitIds, assignmentId, topUnitCount, innerCt),
            ct: ct,
            forceRefresh: req.ForceRefresh,
            ttl: TimeSpan.FromMinutes(15));
    }

    public async Task<List<DashboardReportAssignmentOptionDto>> GetReportAssignmentOptionsAsync(
        DashboardReportAssignmentOptionsRequest? req,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        req ??= new DashboardReportAssignmentOptionsRequest();

        var unitIds = NormalizeIds(req.UnitIds);
        var range = DashboardTimeRangeHelper.NormalizeMonthRange(req.FromUtc, req.ToUtc);

        // Dropdown assignment cho chế độ REPORT cần nhìn theo toàn bộ nhánh được phép xem,
        // không nên siết theo time-filter của assignment; khoảng ngày sẽ áp khi load report.
        var assignments = await LoadDashboardAssignmentScopeAsync(
            me.Id,
            unitIds,
            range,
            ct,
            applyTimeFilter: false);

        if (assignments.Count == 0)
            return new List<DashboardReportAssignmentOptionDto>();

        var workIds = assignments
            .Select(x => x.WorkId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var workMap = await LoadWorkMapAsync(workIds, ct);

        return assignments
            .OrderBy(x => x.WorkId, StringComparer.Ordinal)
            .ThenBy(x => x.Code, StringComparer.Ordinal)
            .ThenBy(x => x.DynamicExcelName, StringComparer.Ordinal)
            .Select(x =>
            {
                workMap.TryGetValue(x.WorkId, out var work);
                var workCode = work is null ? null : (string.IsNullOrWhiteSpace(work.Code) ? work.AutoCode : work.Code);
                var workName = work?.Name;
                return new DashboardReportAssignmentOptionDto
                {
                    AssignmentId = x.Id,
                    WorkId = x.WorkId,
                    WorkName = workName,
                    AssignmentCode = x.Code,
                    AssignmentName = x.DynamicExcelName,
                    Label = string.Join(" • ", new[]
                    {
                        string.IsNullOrWhiteSpace(workCode) ? null : workCode,
                        string.IsNullOrWhiteSpace(workName) ? null : workName,
                        string.IsNullOrWhiteSpace(x.Code) ? x.Id : x.Code,
                        string.IsNullOrWhiteSpace(x.DynamicExcelName) ? null : x.DynamicExcelName,
                    }.Where(s => !string.IsNullOrWhiteSpace(s)))
                };
            })
            .ToList();
    }

    private async Task<DashboardOverviewResponse> LoadOverviewAsync(
        string actorUserId,
        string mode,
        DashboardNormalizedRange range,
        List<string> unitIds,
        string assignmentId,
        int topUnitCount,
        CancellationToken ct)
    {
        return mode switch
        {
            "WORK_TASK" => await BuildWorkOverviewAsync(actorUserId, mode, range, unitIds, topUnitCount, ct),
            "WORK_TARGET" => await BuildWorkOverviewAsync(actorUserId, mode, range, unitIds, topUnitCount, ct),
            "ASSIGNMENT_CREATED" => await BuildAssignmentCreatedOverviewAsync(actorUserId, range, unitIds, topUnitCount, ct),
            "ASSIGNMENT_RECEIVED" => await BuildAssignmentReceivedOverviewAsync(actorUserId, range, unitIds, topUnitCount, ct),
            "REPORT" => await BuildReportOverviewAsync(actorUserId, range, unitIds, assignmentId, topUnitCount, ct),
            _ => await BuildWorkOverviewAsync(actorUserId, "WORK_TASK", range, unitIds, topUnitCount, ct)
        };
    }

    private async Task<DashboardOverviewResponse> BuildWorkOverviewAsync(
        string actorUserId,
        string mode,
        DashboardNormalizedRange range,
        List<string> unitIds,
        int topUnitCount,
        CancellationToken ct)
    {
        var works = await _ctx.Works
            .Find(Builders<Work>.Filter.Eq(x => x.IsDeleted, false)
                & Builders<Work>.Filter.Eq(x => x.CreatedByUserId, actorUserId)
                & BuildWorkTimeFilter(range))
            .SortByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        works = works
            .Where(x => IsWorkTypeMatch(x.Type.ToString(), mode))
            .ToList();

        if (works.Count == 0)
            return EmptyResponse(mode, range);

        var workIds = works.Select(x => x.Id).Distinct(StringComparer.Ordinal).ToList();
        var roots = await LoadRootAssignmentsByWorkIdsAsync(workIds, unitIds, ct);
        var rootsByWorkId = roots
            .GroupBy(x => x.WorkId)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        if (unitIds.Count > 0)
        {
            works = works
                .Where(x => rootsByWorkId.ContainsKey(x.Id))
                .ToList();

            if (works.Count == 0)
                return EmptyResponse(mode, range);
        }

        var cards = new List<DashboardOverviewMetricDto>();
        var totalProgress = new ProgressBucket();

        foreach (var root in roots)
            totalProgress.Add(root.ProgressStatus);

        cards.Add(new DashboardOverviewMetricDto { Key = "totalWorks", Label = mode == "WORK_TASK" ? "Tổng nhiệm vụ" : "Tổng chỉ tiêu", Value = works.Count, Category = "summary" });
        cards.Add(new DashboardOverviewMetricDto { Key = "totalRootAssignments", Label = "Số công việc đã giao", Value = roots.Count, Category = "summary" });
        cards.AddRange(BuildProgressCards(totalProgress));

        var pie = works
            .GroupBy(x => ((int)x.Status).ToString())
            .Select(g => BuildWorkStatusSlice(g.Key, g.Count()))
            .OrderByDescending(x => x.Value)
            .ToList();

        var rows = works
            .Select(work =>
            {
                rootsByWorkId.TryGetValue(work.Id, out var workRoots);
                workRoots ??= new List<WorkAssignment>();
                var progress = BuildProgressBucket(workRoots.Select(x => x.ProgressStatus));

                return new DashboardOverviewTableRowDto
                {
                    Id = work.Id,
                    WorkId = work.Id,
                    WorkCode = string.IsNullOrWhiteSpace(work.Code) ? work.AutoCode : work.Code,
                    WorkName = work.Name,
                    WorkType = work.Type.ToString(),
                    WorkStatus = (int)work.Status,
                    ReportTotal = workRoots.Count,
                    PendingCount = progress.NotStarted,
                    DraftCount = progress.InProgress,
                    SubmittedCount = progress.Completed,
                    ApprovedCount = progress.AtRiskOverdue,
                    OverdueCount = progress.Overdue,
                    UpdatedAtUtc = work.UpdatedAtUtc,
                };
            })
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ToList();

        var unitCharts = BuildWorkUnitCharts(works, rootsByWorkId, topUnitCount);

        return new DashboardOverviewResponse
        {
            Mode = mode,
            Range = BuildRange(range),
            Cards = cards,
            Pie = pie,
            UnitCharts = unitCharts,
            Rows = rows,
        };
    }


    private async Task<DashboardOverviewResponse> BuildAssignmentCreatedOverviewAsync(
        string actorUserId,
        DashboardNormalizedRange range,
        List<string> unitIds,
        int topUnitCount,
        CancellationToken ct)
    {
        var scopedAssignments = await LoadOwnedAssignmentBranchAsync(
            actorUserId,
            unitIds,
            range,
            ct,
            applyTimeFilter: false);

        if (scopedAssignments.Count == 0)
            return EmptyResponse("ASSIGNMENT_CREATED", range);

        var reportAgg = await BuildDerivedReportAggregateAsync(
            scopedAssignments,
            unitIds,
            range,
            topUnitCount,
            ct);

        var visibleAssignments = scopedAssignments
            .Where(x =>
                reportAgg.ByAssignmentId.TryGetValue(x.Id, out var summary) &&
                summary.Counts.Total > 0)
            .OrderBy(x => x.LatestDueAtUtc.HasValue
                ? x.LatestDueAtUtc.Value
                : x.UpdatedAtUtc)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (visibleAssignments.Count == 0)
            return EmptyResponse("ASSIGNMENT_CREATED", range);

        var workMap = await LoadWorkMapAsync(
            visibleAssignments.Select(x => x.WorkId).Distinct(StringComparer.Ordinal).ToList(),
            ct);

        var cards = new List<DashboardOverviewMetricDto>
        {
            new()
            {
                Key = "totalAssignments",
                Label = "Tổng công việc đã giao",
                Value = visibleAssignments.Count,
                Category = "summary"
            },
            new()
            {
                Key = "totalReports",
                Label = "Tổng báo cáo / kỳ",
                Value = reportAgg.Counts.Total,
                Category = "summary"
            },
        };

        cards.AddRange(BuildPeriodStatusCards(
            reportAgg.Counts,
            "Nhóm thẻ dưới đang mô tả trạng thái kỳ / báo cáo. Pie chart bên cạnh đang mô tả cơ cấu assignment."));

        var pie = visibleAssignments
            .GroupBy(x => x.ProgressStatus.ToString())
            .Select(g => BuildProgressSlice(g.Key, g.Count()))
            .OrderByDescending(x => x.Value)
            .ToList();

        var unitCharts = BuildAssignmentUnitCharts(visibleAssignments, topUnitCount, unitIds);

        var rows = visibleAssignments
            .Select(x =>
            {
                var counts = reportAgg.ByAssignmentId.TryGetValue(x.Id, out var summary)
                    ? summary.Counts
                    : new ReportCounts();

                var assignee = GetDashboardAssignees(x, unitIds).FirstOrDefault()
                    ?? x.Assignees?.FirstOrDefault();

                workMap.TryGetValue(x.WorkId, out var work);

                return new DashboardOverviewTableRowDto
                {
                    Id = x.Id,
                    WorkId = x.WorkId,
                    WorkCode = work is null ? null : (string.IsNullOrWhiteSpace(work.Code) ? work.AutoCode : work.Code),
                    WorkName = work?.Name,
                    AssignmentId = x.Id,
                    AssignmentCode = x.Code,
                    AssignmentName = x.DynamicExcelName,
                    AssignmentProgressStatus = x.ProgressStatus,
                    FirstAssigneeName = assignee?.FullName,
                    FirstAssigneeUsername = assignee?.Username,
                    UnitId = assignee?.UnitId,
                    UnitLabel = PickUnitLabel(assignee?.UnitSymbol, assignee?.UnitShortName, assignee?.UnitName),
                    ReportTotal = counts.Total,
                    PendingCount = counts.Pending,
                    DraftCount = counts.Draft,
                    SubmittedCount = counts.Submitted,
                    ApprovedCount = counts.Approved,
                    OverdueCount = counts.Overdue,
                    DueAtUtc = x.LatestDueAtUtc,
                    UpdatedAtUtc = x.UpdatedAtUtc,
                };
            })
            .OrderBy(x => x.DueAtUtc ?? x.UpdatedAtUtc ?? DateTime.MaxValue)
            .ThenBy(x => x.AssignmentCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DashboardOverviewResponse
        {
            Mode = "ASSIGNMENT_CREATED",
            Range = BuildRange(range),
            Cards = cards,
            Pie = pie,
            UnitCharts = unitCharts,
            Rows = rows,
        };
    }



    private async Task<DashboardOverviewResponse> BuildAssignmentReceivedOverviewAsync(
        string actorUserId,
        DashboardNormalizedRange range,
        List<string> unitIds,
        int topUnitCount,
        CancellationToken ct)
    {
        var scopedAssignments = await LoadReceivedAssignmentBranchAsync(
            actorUserId,
            unitIds,
            range,
            ct,
            applyTimeFilter: false);

        if (scopedAssignments.Count == 0)
            return EmptyResponse("ASSIGNMENT_RECEIVED", range);

        var reportAgg = await BuildDerivedReportAggregateAsync(
            scopedAssignments,
            unitIds,
            range,
            topUnitCount,
            ct);

        var visibleAssignments = scopedAssignments
            .Where(x =>
                reportAgg.ByAssignmentId.TryGetValue(x.Id, out var summary) &&
                summary.Counts.Total > 0)
            .OrderBy(x => x.LatestDueAtUtc.HasValue
                ? x.LatestDueAtUtc.Value
                : x.UpdatedAtUtc)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (visibleAssignments.Count == 0)
            return EmptyResponse("ASSIGNMENT_RECEIVED", range);

        var workMap = await LoadWorkMapAsync(
            visibleAssignments.Select(x => x.WorkId).Distinct(StringComparer.Ordinal).ToList(),
            ct);

        var cards = new List<DashboardOverviewMetricDto>
        {
            new()
            {
                Key = "totalAssignments",
                Label = "Tổng công việc được giao",
                Value = visibleAssignments.Count,
                Category = "summary"
            },
            new()
            {
                Key = "totalReports",
                Label = "Tổng báo cáo / kỳ",
                Value = reportAgg.Counts.Total,
                Category = "summary"
            },
        };

        cards.AddRange(BuildPeriodStatusCards(
            reportAgg.Counts,
            "Nhóm thẻ dưới đang mô tả trạng thái kỳ / báo cáo trong nhánh được giao."));

        var pie = BuildDerivedReportPie(reportAgg.Counts);
        var unitCharts = reportAgg.UnitCharts;

        var rows = visibleAssignments
            .Select(x =>
            {
                var counts = reportAgg.ByAssignmentId.TryGetValue(x.Id, out var summary)
                    ? summary.Counts
                    : new ReportCounts();

                var scopedAssignees = GetDashboardAssignees(x, unitIds);
                var assignee = scopedAssignees
                    .FirstOrDefault(a => string.Equals(a.UserId, actorUserId, StringComparison.Ordinal))
                    ?? scopedAssignees.FirstOrDefault()
                    ?? x.Assignees?.FirstOrDefault();

                workMap.TryGetValue(x.WorkId, out var work);

                return new DashboardOverviewTableRowDto
                {
                    Id = x.Id,
                    WorkId = x.WorkId,
                    WorkCode = work is null ? null : (string.IsNullOrWhiteSpace(work.Code) ? work.AutoCode : work.Code),
                    WorkName = work?.Name,
                    AssignmentId = x.Id,
                    AssignmentCode = x.Code,
                    AssignmentName = x.DynamicExcelName,
                    AssignmentProgressStatus = x.ProgressStatus,
                    FirstAssigneeName = assignee?.FullName,
                    FirstAssigneeUsername = assignee?.Username,
                    UnitId = assignee?.UnitId,
                    UnitLabel = PickUnitLabel(assignee?.UnitSymbol, assignee?.UnitShortName, assignee?.UnitName),
                    ReportTotal = counts.Total,
                    PendingCount = counts.Pending,
                    DraftCount = counts.Draft,
                    SubmittedCount = counts.Submitted,
                    ApprovedCount = counts.Approved,
                    OverdueCount = counts.Overdue,
                    DueAtUtc = x.LatestDueAtUtc,
                    UpdatedAtUtc = x.UpdatedAtUtc,
                };
            })
            .OrderBy(x => x.DueAtUtc ?? x.UpdatedAtUtc ?? DateTime.MaxValue)
            .ThenBy(x => x.AssignmentCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DashboardOverviewResponse
        {
            Mode = "ASSIGNMENT_RECEIVED",
            Range = BuildRange(range),
            Cards = cards,
            Pie = pie,
            UnitCharts = unitCharts,
            Rows = rows,
        };
    }


    private async Task<DashboardOverviewResponse> BuildReportOverviewAsync(
        string actorUserId,
        DashboardNormalizedRange range,
        List<string> unitIds,
        string assignmentId,
        int topUnitCount,
        CancellationToken ct)
    {
        var assignments = await LoadDashboardAssignmentScopeAsync(
            actorUserId,
            unitIds,
            range,
            ct,
            applyTimeFilter: false);

        if (assignments.Count == 0)
            return EmptyResponse("REPORT", range);

        if (!string.Equals(assignmentId, "_", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(assignmentId))
        {
            assignments = assignments
                .Where(x => string.Equals(x.Id, assignmentId, StringComparison.Ordinal))
                .ToList();
        }

        if (assignments.Count == 0)
            return EmptyResponse("REPORT", range);

        var reports = await LoadCurrentReportsByAssignmentIdsAsync(
            assignments.Select(x => x.Id).Distinct(StringComparer.Ordinal).ToList(),
            range,
            ct);

        var reportLookup = reports
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.WorkAssignmentId) &&
                !string.IsNullOrWhiteSpace(x.AssigneeUserId) &&
                !string.IsNullOrWhiteSpace(x.PeriodKey))
            .GroupBy(
                x => BuildAssignmentAssigneePeriodKey(x.WorkAssignmentId, x.AssigneeUserId!, x.PeriodKey!),
                StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var workMap = await LoadWorkMapAsync(
            assignments.Select(x => x.WorkId).Distinct(StringComparer.Ordinal).ToList(),
            ct);

        var rows = new List<DashboardOverviewTableRowDto>();
        var counts = new ReportCounts();
        var unitCounters = new Dictionary<string, Dictionary<string, UnitCounter>>(StringComparer.OrdinalIgnoreCase);
        var nowUtc = DateTime.UtcNow;

        foreach (var assignment in assignments
                     .OrderBy(x => x.WorkId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
        {
            var dueItems = AssignmentScheduleDueHelper.GetDueItemsInRange(
                assignment.Schedule,
                range.FromUtc,
                range.ToUtc);

            if (dueItems == null || dueItems.Count == 0)
                continue;

            var assignees = GetDashboardAssignees(assignment, unitIds);
            if (assignees.Count == 0)
                continue;

            workMap.TryGetValue(assignment.WorkId, out var work);

            foreach (var assignee in assignees)
            {
                if (string.IsNullOrWhiteSpace(assignee.UserId))
                    continue;

                foreach (var dueItem in dueItems
                             .OrderBy(x => x.DueAtUtc)
                             .ThenBy(x => x.PeriodKey, StringComparer.OrdinalIgnoreCase))
                {
                    var reportKey = BuildAssignmentAssigneePeriodKey(
                        assignment.Id,
                        assignee.UserId!,
                        dueItem.PeriodKey);

                    reportLookup.TryGetValue(reportKey, out var report);

                    var state = ResolveDerivedReportState(report, dueItem.DueAtUtc, nowUtc);
                    AddDerivedReportCount(counts, state);

                    var unitSlice = BuildDerivedReportSlice(state);
                    AddUnitCounter(unitCounters, assignee, unitSlice);

                    rows.Add(new DashboardOverviewTableRowDto
                    {
                        Id = report?.Id ?? reportKey,
                        WorkId = assignment.WorkId,
                        WorkCode = work is null ? null : (string.IsNullOrWhiteSpace(work.Code) ? work.AutoCode : work.Code),
                        WorkName = work?.Name,
                        AssignmentId = assignment.Id,
                        AssignmentCode = assignment.Code,
                        AssignmentName = assignment.DynamicExcelName,
                        FirstAssigneeName = assignee.FullName,
                        FirstAssigneeUsername = assignee.Username,
                        UnitId = assignee.UnitId,
                        UnitLabel = PickUnitLabel(assignee.UnitSymbol, assignee.UnitShortName, assignee.UnitName) ?? assignee.UnitId,
                        PeriodKey = dueItem.PeriodKey,
                        ReportStatusKey = BuildDerivedReportStatusLabel(state, report),
                        DueAtUtc = report?.DueAtUtc ?? dueItem.DueAtUtc,
                        UpdatedAtUtc = report?.UpdatedAtUtc,
                    });
                }
            }
        }

        var pie = BuildDerivedReportPie(counts);
        var unitCharts = FinalizeUnitCharts(unitCounters, topUnitCount);

        return new DashboardOverviewResponse
        {
            Mode = "REPORT",
            Range = BuildRange(range),
            Cards = new List<DashboardOverviewMetricDto>
            {
                new()
                {
                    Key = "totalReports",
                    Label = "Tổng báo cáo / kỳ",
                    Value = counts.Total,
                    Category = "summary",
                    Description = "Tổng đang suy từ số kỳ phải có theo lịch giao việc và người nhận."
                }
            }
            .Concat(BuildPeriodStatusCards(
                counts,
                "Nhóm thẻ dưới đang mô tả trạng thái kỳ / báo cáo suy từ lịch giao việc."))
            .ToList(),
            Pie = pie,
            UnitCharts = unitCharts,
            Rows = rows
                .OrderBy(x => x.DueAtUtc ?? DateTime.MaxValue)
                .ThenBy(x => x.PeriodKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.AssignmentCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.FirstAssigneeName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }


    private async Task<DerivedReportAggregate> BuildDerivedReportAggregateAsync(
        List<WorkAssignment> assignments,
        List<string> unitIds,
        DashboardNormalizedRange range,
        int topUnitCount,
        CancellationToken ct)
    {
        var result = new DerivedReportAggregate();

        if (assignments.Count == 0)
            return result;

        var reports = await LoadCurrentReportsByAssignmentIdsAsync(
            assignments.Select(x => x.Id).Distinct(StringComparer.Ordinal).ToList(),
            range,
            ct);

        var reportLookup = reports
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.WorkAssignmentId) &&
                !string.IsNullOrWhiteSpace(x.AssigneeUserId) &&
                !string.IsNullOrWhiteSpace(x.PeriodKey))
            .GroupBy(
                x => BuildAssignmentAssigneePeriodKey(x.WorkAssignmentId, x.AssigneeUserId!, x.PeriodKey!),
                StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var unitCounters = new Dictionary<string, Dictionary<string, UnitCounter>>(StringComparer.OrdinalIgnoreCase);
        var nowUtc = DateTime.UtcNow;

        foreach (var assignment in assignments)
        {
            var assignees = GetDashboardAssignees(assignment, unitIds);
            if (assignees.Count == 0)
                continue;

            var dueItems = AssignmentScheduleDueHelper.GetDueItemsInRange(
                assignment.Schedule,
                range.FromUtc,
                range.ToUtc);

            if (dueItems == null || dueItems.Count == 0)
                continue;

            if (!result.ByAssignmentId.TryGetValue(assignment.Id, out var assignmentSummary))
            {
                assignmentSummary = new AssignmentDerivedReportSummary();
                result.ByAssignmentId[assignment.Id] = assignmentSummary;
            }

            foreach (var assignee in assignees)
            {
                if (string.IsNullOrWhiteSpace(assignee.UserId))
                    continue;

                foreach (var dueItem in dueItems)
                {
                    var reportKey = BuildAssignmentAssigneePeriodKey(
                        assignment.Id,
                        assignee.UserId!,
                        dueItem.PeriodKey);

                    reportLookup.TryGetValue(reportKey, out var report);

                    var state = ResolveDerivedReportState(report, dueItem.DueAtUtc, nowUtc);

                    AddDerivedReportCount(result.Counts, state);
                    AddDerivedReportCount(assignmentSummary.Counts, state);

                    var slice = BuildDerivedReportSlice(state);
                    AddUnitCounter(unitCounters, assignee, slice);
                }
            }
        }

        result.UnitCharts = FinalizeUnitCharts(unitCounters, topUnitCount);
        return result;
    }

    private async Task<List<WorkAssignment>> LoadOwnedAssignmentBranchAsync(
        string actorUserId,
        List<string> unitIds,
        DashboardNormalizedRange range,
        CancellationToken ct,
        bool applyTimeFilter = true)
    {
        var seeds = await LoadOwnedAssignmentSeedsAsync(actorUserId, ct);
        return await LoadBranchAssignmentsBySeedsAsync(seeds, unitIds, range, ct, applyTimeFilter);
    }

    private async Task<List<WorkAssignment>> LoadReceivedAssignmentBranchAsync(
        string actorUserId,
        List<string> unitIds,
        DashboardNormalizedRange range,
        CancellationToken ct,
        bool applyTimeFilter = true)
    {
        var seeds = await LoadReceivedAssignmentSeedsAsync(actorUserId, ct);
        return await LoadBranchAssignmentsBySeedsAsync(seeds, unitIds, range, ct, applyTimeFilter);
    }

    private async Task<List<WorkAssignment>> LoadDashboardAssignmentScopeAsync(
        string actorUserId,
        List<string> unitIds,
        DashboardNormalizedRange range,
        CancellationToken ct,
        bool applyTimeFilter = true)
    {
        var ownedSeedsTask = LoadOwnedAssignmentSeedsAsync(actorUserId, ct);
        var receivedSeedsTask = LoadReceivedAssignmentSeedsAsync(actorUserId, ct);

        await Task.WhenAll(ownedSeedsTask, receivedSeedsTask);

        var seeds = ownedSeedsTask.Result
            .Concat(receivedSeedsTask.Result)
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        return await LoadBranchAssignmentsBySeedsAsync(seeds, unitIds, range, ct, applyTimeFilter);
    }

    private async Task<List<WorkAssignment>> LoadOwnedAssignmentSeedsAsync(
        string actorUserId,
        CancellationToken ct)
    {
        return await _ctx.WorkAssignments
            .Find(Builders<WorkAssignment>.Filter.Eq(x => x.CreatedByUserId, actorUserId)
                & Builders<WorkAssignment>.Filter.Eq(x => x.IsDeleted, false)
                & Builders<WorkAssignment>.Filter.Eq(x => x.IsActive, true))
            .SortBy(x => x.Path)
            .ToListAsync(ct);
    }

    private async Task<List<WorkAssignment>> LoadReceivedAssignmentSeedsAsync(
        string actorUserId,
        CancellationToken ct)
    {
        return await _ctx.WorkAssignments
            .Find(Builders<WorkAssignment>.Filter.Eq(x => x.IsDeleted, false)
                & Builders<WorkAssignment>.Filter.Eq(x => x.IsActive, true)
                & Builders<WorkAssignment>.Filter.Where(x =>
                    x.Assignees != null &&
                    x.Assignees.Any(a => a.UserId == actorUserId)))
            .SortBy(x => x.Path)
            .ToListAsync(ct);
    }

    private async Task<List<WorkAssignment>> LoadBranchAssignmentsBySeedsAsync(
        List<WorkAssignment> seeds,
        List<string> unitIds,
        DashboardNormalizedRange range,
        CancellationToken ct,
        bool applyTimeFilter = true)
    {
        var paths = CompactPaths(
            (seeds ?? new List<WorkAssignment>())
                .Select(x => x.Path)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList());

        if (paths.Count == 0)
            return new List<WorkAssignment>();

        var filter = Builders<WorkAssignment>.Filter.Eq(x => x.IsDeleted, false)
            & Builders<WorkAssignment>.Filter.Eq(x => x.IsActive, true)
            & BuildAssignmentUnitFilter(unitIds)
            & BuildAssignmentPathBranchFilter(paths);

        if (applyTimeFilter)
        {
            filter &= BuildAssignmentTimeFilter(range);
        }

        return await _ctx.WorkAssignments
            .Find(filter)
            .SortByDescending(x => x.HasOverduePeriod)
            .ThenBy(x => x.LatestDueAtUtc)
            .ThenBy(x => x.Path)
            .ToListAsync(ct);
    }

    private static FilterDefinition<WorkAssignment> BuildAssignmentPathBranchFilter(List<string> paths)
    {
        if (paths.Count == 0)
        {
            return Builders<WorkAssignment>.Filter.Where(_ => false);
        }

        var regexFilters = paths
            .Select(path =>
            {
                var escaped = Regex.Escape(path);
                var regex = new BsonRegularExpression($"^{escaped}(?:/|$)");
                return Builders<WorkAssignment>.Filter.Regex(x => x.Path, regex);
            })
            .ToList();

        return regexFilters.Count == 1
            ? regexFilters[0]
            : Builders<WorkAssignment>.Filter.Or(regexFilters);
    }

    private static List<string> CompactPaths(List<string> paths)
    {
        var normalized = (paths ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x.Length)
            .ThenBy(x => x, StringComparer.Ordinal)
            .ToList();

        var result = new List<string>();

        foreach (var path in normalized)
        {
            var isCovered = result.Any(parent =>
                string.Equals(path, parent, StringComparison.Ordinal) ||
                path.StartsWith(parent + "/", StringComparison.Ordinal));

            if (!isCovered)
            {
                result.Add(path);
            }
        }

        return result;
    }

    private async Task<List<WorkAssignment>> LoadRootAssignmentsByWorkIdsAsync(
        List<string> workIds,
        List<string> unitIds,
        CancellationToken ct)
    {
        if (workIds.Count == 0)
            return new List<WorkAssignment>();

        return await _ctx.WorkAssignments
            .Find(Builders<WorkAssignment>.Filter.In(x => x.WorkId, workIds)
                & Builders<WorkAssignment>.Filter.Eq(x => x.ParentAssignmentId, null as string)
                & Builders<WorkAssignment>.Filter.Eq(x => x.IsDeleted, false)
                & Builders<WorkAssignment>.Filter.Eq(x => x.IsActive, true)
                & BuildAssignmentUnitFilter(unitIds))
            .ToListAsync(ct);
    }

    private async Task<List<WorkReportPeriod>> LoadPeriodsByAssignmentIdsAsync(
        List<string> assignmentIds,
        List<string> unitIds,
        DashboardNormalizedRange range,
        CancellationToken ct)
    {
        if (assignmentIds.Count == 0)
            return new List<WorkReportPeriod>();

        var filter = Builders<WorkReportPeriod>.Filter.In(x => x.WorkAssignmentId, assignmentIds)
            & Builders<WorkReportPeriod>.Filter.Eq(x => x.IsDeleted, false)
            & Builders<WorkReportPeriod>.Filter.Eq(x => x.IsActive, true)
            & BuildPeriodTimeFilter(range);

        if (unitIds.Count > 0)
            filter &= Builders<WorkReportPeriod>.Filter.In(x => x.AssigneeUnitId, unitIds);

        return await _ctx.WorkReportPeriods
            .Find(filter)
            .ToListAsync(ct);
    }

    private async Task<List<WorkAssignmentReport>> LoadCurrentReportsByAssignmentIdsAsync(
        List<string> assignmentIds,
        DashboardNormalizedRange range,
        CancellationToken ct)
    {
        if (assignmentIds.Count == 0)
            return new List<WorkAssignmentReport>();

        var filter = Builders<WorkAssignmentReport>.Filter.In(x => x.WorkAssignmentId, assignmentIds)
            & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false)
            & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsCurrent, true)
            & Builders<WorkAssignmentReport>.Filter.Ne(x => x.IsActive, false)
            & BuildCurrentReportTimeFilter(range);

        return await _ctx.WorkAssignmentReports
            .Find(filter)
            .ToListAsync(ct);
    }

    private async Task<Dictionary<string, Work>> LoadWorkMapAsync(List<string> workIds, CancellationToken ct)
    {
        if (workIds.Count == 0)
            return new Dictionary<string, Work>(StringComparer.Ordinal);

        var works = await _ctx.Works
            .Find(x => workIds.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(ct);

        return works.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
    }

    private static DashboardRangeDto BuildRange(DashboardNormalizedRange range)
    {
        return new DashboardRangeDto
        {
            FromUtc = range.FromUtc,
            ToUtc = range.ToUtc,
            Label = range.Label,
        };
    }

    private static DashboardOverviewResponse EmptyResponse(string mode, DashboardNormalizedRange range)
    {
        return new DashboardOverviewResponse
        {
            Mode = mode,
            Range = BuildRange(range),
            Cards = new List<DashboardOverviewMetricDto>(),
            Pie = new List<DashboardOverviewPieSliceDto>(),
            UnitCharts = new Dictionary<string, List<DashboardUnitBarRowDto>>(StringComparer.OrdinalIgnoreCase),
            Rows = new List<DashboardOverviewTableRowDto>(),
        };
    }

    private static List<DashboardOverviewMetricDto> BuildProgressCards(ProgressBucket progress)
    {
        return new List<DashboardOverviewMetricDto>
        {
            new() { Key = "notStarted", Label = "Chưa thực hiện", Value = progress.NotStarted, Category = "status", Description = "Nhóm thẻ dưới đang mô tả trạng thái assignment / công việc." },
            new() { Key = "inProgress", Label = "Đang thực hiện", Value = progress.InProgress, Category = "status", Description = "Nhóm thẻ dưới đang mô tả trạng thái assignment / công việc." },
            new() { Key = "completed", Label = "Đã hoàn thành", Value = progress.Completed, ValueColor = "success.main", Category = "status", Description = "Nhóm thẻ dưới đang mô tả trạng thái assignment / công việc." },
            new() { Key = "atRiskOverdue", Label = "Có nguy cơ chậm", Value = progress.AtRiskOverdue, ValueColor = "warning.main", Category = "status", Description = "Nhóm thẻ dưới đang mô tả trạng thái assignment / công việc." },
            new() { Key = "overdue", Label = "Chậm muộn", Value = progress.Overdue, ValueColor = "error.main", Category = "status", Description = "Nhóm thẻ dưới đang mô tả trạng thái assignment / công việc." },
        };
    }

    private static List<DashboardOverviewMetricDto> BuildPeriodStatusCards(ReportCounts counts, string description)
    {
        return new List<DashboardOverviewMetricDto>
        {
            new() { Key = "pending", Label = "Chưa bắt đầu", Value = counts.Pending, Category = "status", Description = description },
            new() { Key = "draft", Label = "Bản nháp", Value = counts.Draft, ValueColor = "info.main", Category = "status", Description = description },
            new() { Key = "submitted", Label = "Đã gửi", Value = counts.Submitted, ValueColor = "primary.main", Category = "status", Description = description },
            new() { Key = "approved", Label = "Đã duyệt", Value = counts.Approved, ValueColor = "success.main", Category = "status", Description = description },
            new() { Key = "overdue", Label = "Quá hạn", Value = counts.Overdue, ValueColor = "error.main", Category = "status", Description = description },
        };
    }

    private static ProgressBucket BuildProgressBucket(IEnumerable<int> progressStatuses)
    {
        var bucket = new ProgressBucket();
        foreach (var status in progressStatuses)
            bucket.Add(status);
        return bucket;
    }

    private static DashboardOverviewPieSliceDto BuildProgressSlice(string key, int value)
    {
        return key switch
        {
            "0" => new DashboardOverviewPieSliceDto { Key = key, Label = "Chưa thực hiện", Value = value, Color = "#94a3b8" },
            "1" => new DashboardOverviewPieSliceDto { Key = key, Label = "Đang thực hiện", Value = value, Color = "#0ea5e9" },
            "2" => new DashboardOverviewPieSliceDto { Key = key, Label = "Đã hoàn thành", Value = value, Color = "#22c55e" },
            "3" => new DashboardOverviewPieSliceDto { Key = key, Label = "Có nguy cơ chậm", Value = value, Color = "#f59e0b" },
            "4" => new DashboardOverviewPieSliceDto { Key = key, Label = "Chậm muộn", Value = value, Color = "#ef4444" },
            _ => new DashboardOverviewPieSliceDto { Key = key, Label = key, Value = value, Color = "#6b7280" },
        };
    }

    private static DashboardOverviewPieSliceDto BuildWorkStatusSlice(string key, int value)
    {
        return BuildProgressSlice(key, value);
    }

    private static List<DashboardOverviewPieSliceDto> BuildReportPie(List<WorkReportPeriod> periods)
    {
        return periods
            .GroupBy(x => x.Status)
            .Select(g => new DashboardOverviewPieSliceDto
            {
                Key = g.Key.ToString(),
                Label = GetReportStatusLabel(g.Key),
                Value = g.Count(),
                Color = GetReportStatusColor(g.Key),
            })
            .OrderByDescending(x => x.Value)
            .ToList();
    }

    private static List<DashboardOverviewPieSliceDto> BuildCurrentReportPie(List<WorkAssignmentReport> reports)
    {
        return reports
            .GroupBy(x => new { x.Status, x.IsLateSubmission })
            .Select(g => new DashboardOverviewPieSliceDto
            {
                Key = $"{(int)g.Key.Status}:{(g.Key.IsLateSubmission ? 1 : 0)}",
                Label = GetCurrentReportStatusLabel(g.Key.Status, g.Key.IsLateSubmission),
                Value = g.Count(),
                Color = GetCurrentReportStatusColor(g.Key.Status, g.Key.IsLateSubmission),
            })
            .OrderByDescending(x => x.Value)
            .ToList();
    }

    private static List<UserRef> GetDashboardAssignees(WorkAssignment assignment, List<string> unitIds)
    {
        return (assignment.Assignees ?? new List<UserRef>())
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.UserId) &&
                (unitIds.Count == 0 || (!string.IsNullOrWhiteSpace(x.UnitId) && unitIds.Contains(x.UnitId))))
            .GroupBy(x => x.UserId!, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    private static string BuildAssignmentAssigneePeriodKey(string assignmentId, string assigneeUserId, string periodKey)
        => $"{assignmentId}__{assigneeUserId}__{periodKey}";

    private static DerivedReportState ResolveDerivedReportState(
        WorkAssignmentReport? report,
        DateTime dueAtUtc,
        DateTime nowUtc)
    {
        if (report is null)
            return dueAtUtc < nowUtc ? DerivedReportState.Overdue : DerivedReportState.Pending;

        if (report.Status == WorkAssignmentReportStatus.Approved)
            return DerivedReportState.Approved;

        var effectiveDueAtUtc = report.DueAtUtc ?? dueAtUtc;

        if (report.IsLateSubmission || effectiveDueAtUtc < nowUtc)
            return DerivedReportState.Overdue;

        return report.Status switch
        {
            WorkAssignmentReportStatus.Draft => DerivedReportState.Draft,
            WorkAssignmentReportStatus.Submitted => DerivedReportState.Submitted,
            WorkAssignmentReportStatus.Approved => DerivedReportState.Approved,
            _ => DerivedReportState.Pending,
        };
    }

    private static void AddDerivedReportCount(ReportCounts counts, DerivedReportState state)
    {
        switch (state)
        {
            case DerivedReportState.Pending:
                counts.Pending++;
                break;
            case DerivedReportState.Draft:
                counts.Draft++;
                break;
            case DerivedReportState.Submitted:
                counts.Submitted++;
                break;
            case DerivedReportState.Approved:
                counts.Approved++;
                break;
            case DerivedReportState.Overdue:
                counts.Overdue++;
                break;
        }

        counts.Total = counts.Pending + counts.Draft + counts.Submitted + counts.Approved + counts.Overdue;
    }

    private static DashboardOverviewPieSliceDto BuildDerivedReportSlice(DerivedReportState state)
    {
        return state switch
        {
            DerivedReportState.Pending => new DashboardOverviewPieSliceDto
            {
                Key = "pending",
                Label = "Chưa bắt đầu",
                Value = 0,
                Color = "#94a3b8",
            },
            DerivedReportState.Draft => new DashboardOverviewPieSliceDto
            {
                Key = "draft",
                Label = "Bản nháp",
                Value = 0,
                Color = "#0ea5e9",
            },
            DerivedReportState.Submitted => new DashboardOverviewPieSliceDto
            {
                Key = "submitted",
                Label = "Đã nộp",
                Value = 0,
                Color = "#2563eb",
            },
            DerivedReportState.Approved => new DashboardOverviewPieSliceDto
            {
                Key = "approved",
                Label = "Đã duyệt",
                Value = 0,
                Color = "#22c55e",
            },
            _ => new DashboardOverviewPieSliceDto
            {
                Key = "overdue",
                Label = "Quá hạn",
                Value = 0,
                Color = "#ef4444",
            },
        };
    }

    private static string BuildDerivedReportStatusLabel(
        DerivedReportState state,
        WorkAssignmentReport? report)
    {
        if (report is null)
        {
            return state == DerivedReportState.Overdue
                ? "Quá hạn chưa làm"
                : "Chưa bắt đầu";
        }

        return state switch
        {
            DerivedReportState.Draft => "Bản nháp",
            DerivedReportState.Submitted => "Đã nộp",
            DerivedReportState.Approved => "Đã duyệt",
            DerivedReportState.Overdue when report.Status == WorkAssignmentReportStatus.Draft => "Quá hạn bản nháp",
            DerivedReportState.Overdue when report.Status == WorkAssignmentReportStatus.Submitted => "Quá hạn đã nộp",
            DerivedReportState.Overdue when report.Status == WorkAssignmentReportStatus.Approved => "Đã duyệt",
            DerivedReportState.Overdue => "Quá hạn chưa làm",
            _ => "Chưa bắt đầu",
        };
    }

    private static List<DashboardOverviewPieSliceDto> BuildDerivedReportPie(ReportCounts counts)
    {
        var slices = new List<DashboardOverviewPieSliceDto>();

        void add(DerivedReportState state, int value)
        {
            if (value <= 0) return;
            var slice = BuildDerivedReportSlice(state);
            slice.Value = value;
            slices.Add(slice);
        }

        add(DerivedReportState.Pending, counts.Pending);
        add(DerivedReportState.Draft, counts.Draft);
        add(DerivedReportState.Submitted, counts.Submitted);
        add(DerivedReportState.Approved, counts.Approved);
        add(DerivedReportState.Overdue, counts.Overdue);

        return slices.OrderByDescending(x => x.Value).ToList();
    }

    private static void AddUnitCounter(
        Dictionary<string, Dictionary<string, UnitCounter>> counters,
        UserRef assignee,
        DashboardOverviewPieSliceDto slice)
    {
        if (!counters.TryGetValue(slice.Key, out var unitMap))
        {
            unitMap = new Dictionary<string, UnitCounter>(StringComparer.OrdinalIgnoreCase);
            counters[slice.Key] = unitMap;
        }

        var unitId = assignee.UnitId;
        var unitLabel = PickUnitLabel(assignee.UnitSymbol, assignee.UnitShortName, assignee.UnitName)
                        ?? assignee.UnitId
                        ?? "Không rõ đơn vị";

        var unitKey = unitId ?? unitLabel;
        if (!unitMap.TryGetValue(unitKey, out var counter))
        {
            counter = new UnitCounter(unitId, unitLabel);
            unitMap[unitKey] = counter;
        }

        counter.Add(slice);
    }


    private static Dictionary<string, List<DashboardUnitBarRowDto>> BuildAssignmentUnitCharts(
        List<WorkAssignment> assignments,
        int topUnitCount,
        List<string>? unitIds = null)
    {
        var counters = new Dictionary<string, Dictionary<string, UnitCounter>>(StringComparer.OrdinalIgnoreCase);

        var normalizedUnitIds = NormalizeIds(unitIds);

        foreach (var assignment in assignments)
        {
            var slice = BuildProgressSlice(assignment.ProgressStatus.ToString(), 0);
            if (!counters.TryGetValue(slice.Key, out var unitMap))
            {
                unitMap = new Dictionary<string, UnitCounter>(StringComparer.OrdinalIgnoreCase);
                counters[slice.Key] = unitMap;
            }

            var distinctUnits = (assignment.Assignees ?? new List<UserRef>())
                .Where(a =>
                    normalizedUnitIds.Count == 0 ||
                    (!string.IsNullOrWhiteSpace(a.UnitId) && normalizedUnitIds.Contains(a.UnitId)))
                .Select(a => new UnitLite(
                    a.UnitId,
                    PickUnitLabel(a.UnitSymbol, a.UnitShortName, a.UnitName)))
                .Where(x => !string.IsNullOrWhiteSpace(x.Label))
                .DistinctBy(x => x.UnitId ?? x.Label)
                .ToList();

            foreach (var unit in distinctUnits)
            {
                var unitKey = unit.UnitId ?? unit.Label!;
                if (!unitMap.TryGetValue(unitKey, out var counter))
                {
                    counter = new UnitCounter(unit.UnitId, unit.Label!);
                    unitMap[unitKey] = counter;
                }
                counter.Add(slice);
            }
        }

        return FinalizeUnitCharts(counters, topUnitCount);
    }

    private static Dictionary<string, List<DashboardUnitBarRowDto>> BuildPeriodUnitCharts(
        List<WorkReportPeriod> periods,
        int topUnitCount)
    {
        var counters = new Dictionary<string, Dictionary<string, UnitCounter>>(StringComparer.OrdinalIgnoreCase);

        foreach (var period in periods)
        {
            var sliceKey = period.Status.ToString();
            var slice = new DashboardOverviewPieSliceDto
            {
                Key = sliceKey,
                Label = GetReportStatusLabel(period.Status),
                Value = 0,
                Color = GetReportStatusColor(period.Status)
            };

            if (!counters.TryGetValue(sliceKey, out var unitMap))
            {
                unitMap = new Dictionary<string, UnitCounter>(StringComparer.OrdinalIgnoreCase);
                counters[sliceKey] = unitMap;
            }

            var unitLabel = string.IsNullOrWhiteSpace(period.AssigneeUnitId) ? "Không rõ đơn vị" : period.AssigneeUnitId;
            var unitKey = string.IsNullOrWhiteSpace(period.AssigneeUnitId) ? unitLabel : period.AssigneeUnitId;
            if (!unitMap.TryGetValue(unitKey, out var counter))
            {
                counter = new UnitCounter(period.AssigneeUnitId, unitLabel);
                unitMap[unitKey] = counter;
            }
            counter.Add(slice);
        }

        return FinalizeUnitCharts(counters, topUnitCount);
    }

    private static Dictionary<string, List<DashboardUnitBarRowDto>> BuildWorkUnitCharts(
        List<Work> works,
        Dictionary<string, List<WorkAssignment>> rootsByWorkId,
        int topUnitCount)
    {
        var counters = new Dictionary<string, Dictionary<string, UnitCounter>>(StringComparer.OrdinalIgnoreCase);

        foreach (var work in works)
        {
            var slice = BuildWorkStatusSlice(((int)work.Status).ToString(), 0);
            if (!counters.TryGetValue(slice.Key, out var unitMap))
            {
                unitMap = new Dictionary<string, UnitCounter>(StringComparer.OrdinalIgnoreCase);
                counters[slice.Key] = unitMap;
            }

            rootsByWorkId.TryGetValue(work.Id, out var roots);
            roots ??= new List<WorkAssignment>();

            var distinctUnits = roots
                .SelectMany(x => x.Assignees ?? new List<UserRef>())
                .Select(a => new UnitLite(a.UnitId, PickUnitLabel(a.UnitSymbol, a.UnitShortName, a.UnitName)))
                .Where(x => !string.IsNullOrWhiteSpace(x.Label))
                .DistinctBy(x => x.UnitId ?? x.Label)
                .ToList();

            foreach (var unit in distinctUnits)
            {
                var unitKey = unit.UnitId ?? unit.Label!;
                if (!unitMap.TryGetValue(unitKey, out var counter))
                {
                    counter = new UnitCounter(unit.UnitId, unit.Label!);
                    unitMap[unitKey] = counter;
                }
                counter.Add(slice);
            }
        }

        return FinalizeUnitCharts(counters, topUnitCount);
    }

    private static Dictionary<string, List<DashboardUnitBarRowDto>> FinalizeUnitCharts(
        Dictionary<string, Dictionary<string, UnitCounter>> counters,
        int topUnitCount)
    {
        return counters.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Values
                .OrderByDescending(x => x.Total)
                .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
                .Take(topUnitCount)
                .Select(x => new DashboardUnitBarRowDto
                {
                    UnitId = x.UnitId,
                    UnitLabel = x.Label,
                    Total = x.Total,
                    Segments = x.Segments.Values
                        .OrderByDescending(s => s.Value)
                        .Select(s => new DashboardUnitBarSegmentDto
                        {
                            Key = s.Key,
                            Label = s.Label,
                            Value = s.Value,
                            Color = s.Color,
                        })
                        .ToList(),
                })
                .ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static ReportCounts BuildReportCounts(IEnumerable<WorkReportPeriod> periods)
    {
        var counts = new ReportCounts();

        foreach (var p in periods)
        {
            if (WorkReportPeriodStatusHelper.IsOverdue(p.Status))
            {
                counts.Overdue++;
                continue;
            }

            switch (p.Status)
            {
                case WorkReportPeriodStatus.Pending:
                    counts.Pending++;
                    break;
                case WorkReportPeriodStatus.Draft:
                    counts.Draft++;
                    break;
                case WorkReportPeriodStatus.Submitted:
                    counts.Submitted++;
                    break;
                case WorkReportPeriodStatus.Approved:
                    counts.Approved++;
                    break;
            }
        }

        counts.Total = counts.Pending + counts.Draft + counts.Submitted + counts.Approved + counts.Overdue;
        return counts;
    }

    private static string GetReportStatusLabel(WorkReportPeriodStatus status)
    {
        return status switch
        {
            WorkReportPeriodStatus.Pending => "Chưa mở",
            WorkReportPeriodStatus.Draft => "Bản nháp",
            WorkReportPeriodStatus.Submitted => "Đã gửi",
            WorkReportPeriodStatus.Approved => "Đã duyệt",
            WorkReportPeriodStatus.OverduePending => "Quá hạn chưa mở",
            WorkReportPeriodStatus.OverdueDraft => "Quá hạn bản nháp",
            WorkReportPeriodStatus.OverdueSubmitted => "Quá hạn đã gửi",
            WorkReportPeriodStatus.OverdueApproved => "Quá hạn đã duyệt",
            _ => status.ToString(),
        };
    }

    private static string GetReportStatusColor(WorkReportPeriodStatus status)
    {
        return status switch
        {
            WorkReportPeriodStatus.Pending => "#94a3b8",
            WorkReportPeriodStatus.Draft => "#0ea5e9",
            WorkReportPeriodStatus.Submitted => "#2563eb",
            WorkReportPeriodStatus.Approved => "#22c55e",
            WorkReportPeriodStatus.OverduePending => "#f59e0b",
            WorkReportPeriodStatus.OverdueDraft => "#fb7185",
            WorkReportPeriodStatus.OverdueSubmitted => "#ef4444",
            WorkReportPeriodStatus.OverdueApproved => "#8D6E63",

            _ => "#6b7280",
        };
    }

    private static string GetCurrentReportStatusLabel(WorkAssignmentReportStatus status, bool isLateSubmission)
    {
        var core = status switch
        {
            WorkAssignmentReportStatus.Draft => "Bản nháp",
            WorkAssignmentReportStatus.Submitted => "Đã nộp",
            WorkAssignmentReportStatus.Approved => "Đã duyệt",
            _ => status.ToString(),
        };

        return isLateSubmission ? $"{core} (muộn)" : core;
    }

    private static string GetCurrentReportStatusColor(WorkAssignmentReportStatus status, bool isLateSubmission)
    {
        if (isLateSubmission)
            return "#ef4444";

        return status switch
        {
            WorkAssignmentReportStatus.Draft => "#0ea5e9",
            WorkAssignmentReportStatus.Submitted => "#2563eb",
            WorkAssignmentReportStatus.Approved => "#22c55e",
            _ => "#6b7280",
        };
    }

    private static string BuildCacheKey(
        string actorUserId,
        string mode,
        DashboardNormalizedRange range,
        List<string> unitIds,
        string assignmentId,
        int topUnitCount)
    {
        var raw = string.Join("|", new[]
        {
            "dashboard-overview-v2",
            actorUserId,
            mode,
            range.FromUtc.ToString("O"),
            range.ToUtc.ToString("O"),
            string.Join(",", unitIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
            assignmentId,
            topUnitCount.ToString(),
        });

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"dashboard:overview:{Convert.ToHexString(bytes)[..16]}";
    }

    private static string NormalizeMode(string? mode)
    {
        var value = string.IsNullOrWhiteSpace(mode) ? "WORK_TASK" : mode.Trim().ToUpperInvariant();
        return value switch
        {
            "WORK_TASK" or "WORK_TARGET" or "ASSIGNMENT_RECEIVED" or "ASSIGNMENT_CREATED" or "REPORT" => value,
            _ => "WORK_TASK"
        };
    }

    private static bool IsWorkTypeMatch(string? workType, string mode)
    {
        var normalized = workType?.Trim().ToUpperInvariant() ?? string.Empty;
        if (mode == "WORK_TASK")
            return normalized is "TASK" or "NHIEM_VU";

        if (mode == "WORK_TARGET")
            return normalized is "TARGET" or "CHI_TIEU";

        return false;
    }

    private static List<string> NormalizeIds(IEnumerable<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static FilterDefinition<Work> BuildWorkTimeFilter(DashboardNormalizedRange range)
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

    private static FilterDefinition<WorkAssignment> BuildAssignmentTimeFilter(DashboardNormalizedRange range)
    {
        var fb = Builders<WorkAssignment>.Filter;

        var byDueDate = fb.And(
            fb.Ne(x => x.DueAtUtc, null),
            fb.Gte(x => x.DueAtUtc, range.FromUtc),
            fb.Lte(x => x.DueAtUtc, range.ToUtc)
        );

        var byUpdated = fb.And(
            fb.Eq(x => x.DueAtUtc, null),
            fb.Gte(x => x.UpdatedAtUtc, range.FromUtc),
            fb.Lte(x => x.UpdatedAtUtc, range.ToUtc)
        );

        return fb.Or(byDueDate, byUpdated);
    }


    private static FilterDefinition<WorkAssignmentReport> BuildCurrentReportTimeFilter(DashboardNormalizedRange range)
    {
        var fb = Builders<WorkAssignmentReport>.Filter;
        var fromDayKey = range.FromDate.ToString("yyyyMMdd");
        var toDayKey = range.ToDate.ToString("yyyyMMdd");

        var byDueDate = fb.And(
            fb.Ne(x => x.DueAtUtc, null),
            fb.Gte(x => x.DueAtUtc, range.FromUtc),
            fb.Lte(x => x.DueAtUtc, range.ToUtc)
        );

        var byPeriodKey = fb.And(
            fb.Ne(x => x.PeriodKey, null),
            fb.Gte(x => x.PeriodKey, fromDayKey),
            fb.Lte(x => x.PeriodKey, toDayKey)
        );

        var fallbackUpdated = fb.And(
            fb.Eq(x => x.DueAtUtc, null),
            fb.Eq(x => x.PeriodKey, null),
            fb.Gte(x => x.UpdatedAtUtc, range.FromUtc),
            fb.Lte(x => x.UpdatedAtUtc, range.ToUtc)
        );

        return fb.Or(byDueDate, byPeriodKey, fallbackUpdated);
    }

    private static FilterDefinition<WorkAssignment> BuildAssignmentUnitFilter(List<string> unitIds)
    {
        if (unitIds.Count == 0)
            return Builders<WorkAssignment>.Filter.Empty;

        return Builders<WorkAssignment>.Filter.ElemMatch(
            x => x.Assignees,
            a => a.UnitId != null && unitIds.Contains(a.UnitId));
    }

    private static FilterDefinition<WorkReportPeriod> BuildPeriodTimeFilter(DashboardNormalizedRange range)
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

    private static string? PickUnitLabel(string? unitSymbol, string? unitShortName, string? unitName)
    {
        if (!string.IsNullOrWhiteSpace(unitSymbol)) return unitSymbol;
        if (!string.IsNullOrWhiteSpace(unitShortName)) return unitShortName;
        if (!string.IsNullOrWhiteSpace(unitName)) return unitName;
        return null;
    }

    private sealed class ProgressBucket
    {
        public int NotStarted { get; private set; }
        public int InProgress { get; private set; }
        public int Completed { get; private set; }
        public int AtRiskOverdue { get; private set; }
        public int Overdue { get; private set; }

        public void Add(int status)
        {
            switch ((WorkAssignmentProgressStatus)status)
            {
                case WorkAssignmentProgressStatus.NotStarted:
                    NotStarted++;
                    break;
                case WorkAssignmentProgressStatus.InProgress:
                    InProgress++;
                    break;
                case WorkAssignmentProgressStatus.Completed:
                    Completed++;
                    break;
                case WorkAssignmentProgressStatus.AtRiskOverdue:
                    AtRiskOverdue++;
                    break;
                case WorkAssignmentProgressStatus.Overdue:
                    Overdue++;
                    break;
            }
        }
    }


    private sealed class AssignmentDerivedReportSummary
    {
        public ReportCounts Counts { get; } = new();
    }

    private sealed class DerivedReportAggregate
    {
        public ReportCounts Counts { get; } = new();
        public Dictionary<string, AssignmentDerivedReportSummary> ByAssignmentId { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<DashboardUnitBarRowDto>> UnitCharts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ReportCounts
    {
        public int Total { get; set; }
        public int Pending { get; set; }
        public int Draft { get; set; }
        public int Submitted { get; set; }
        public int Approved { get; set; }
        public int Overdue { get; set; }
    }

    private enum DerivedReportState
    {
        Pending = 0,
        Draft = 1,
        Submitted = 2,
        Approved = 3,
        Overdue = 4,
    }

    private sealed record UnitLite(string? UnitId, string? Label);

    private sealed class UnitCounter
    {
        public string? UnitId { get; }
        public string Label { get; }
        public int Total { get; private set; }
        public Dictionary<string, SegmentCounter> Segments { get; } = new(StringComparer.OrdinalIgnoreCase);

        public UnitCounter(string? unitId, string label)
        {
            UnitId = unitId;
            Label = label;
        }

        public void Add(DashboardOverviewPieSliceDto slice)
        {
            Total++;
            if (!Segments.TryGetValue(slice.Key, out var counter))
            {
                counter = new SegmentCounter(slice.Key, slice.Label, slice.Color);
                Segments[slice.Key] = counter;
            }
            counter.Value++;
        }
    }

    private sealed class SegmentCounter
    {
        public string Key { get; }
        public string Label { get; }
        public string Color { get; }
        public int Value { get; set; }

        public SegmentCounter(string key, string label, string color)
        {
            Key = key;
            Label = label;
            Color = color;
        }
    }
}
