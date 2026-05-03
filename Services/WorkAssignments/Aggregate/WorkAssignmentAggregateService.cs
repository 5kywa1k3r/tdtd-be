using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.WorkAssignments.Aggregate;
using tdtd_be.DTOs.WorkAssignments.AggregateTable;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments.Progress;

namespace tdtd_be.Services.WorkAssignments.Aggregate;

public sealed class WorkAssignmentAggregateService : IWorkAssignmentAggregateService
{
    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentProgressService _progressService;
    private readonly IReportTemplateRuntimeTypeResolver _typeResolver;
    private readonly MeAccessor _me;

    public WorkAssignmentAggregateService(
        MongoDbContext ctx,
        IWorkAssignmentProgressService progressService,
        IReportTemplateRuntimeTypeResolver typeResolver,
        MeAccessor me)
    {
        _ctx = ctx;
        _progressService = progressService;
        _typeResolver = typeResolver;
        _me = me;
    }

    public async Task<AggregateReportResponse> GetAggregatedViewAsync(
    AggregateReportRequest req,
    CancellationToken ct)
    {
        var me = _me.RequireMe();

        if (string.IsNullOrWhiteSpace(req.WorkAssignmentId))
            throw new InvalidOperationException("Thiếu WorkAssignmentId.");

        var root = await _ctx.WorkAssignments
            .Find(x => x.Id == req.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Không tìm thấy node.");

        WorkAssignmentReviewPermissionHelper.EnsureCanReviewOnNode(root, me.Id);

        await _progressService.RecomputeDirectChildrenAsync(root.Id, ct);
        await _progressService.RecomputeSingleAsync(root, ct);

        var children = await _ctx.WorkAssignments
            .Find(x => x.ParentAssignmentId == root.Id && x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        if (children.Count == 0)
        {
            return new AggregateReportResponse
            {
                WorkAssignmentId = root.Id,
                DynamicExcelId = root.DynamicExcelId,
                DynamicExcelCode = root.DynamicExcelCode,
                DynamicExcelName = root.DynamicExcelName,
                TemplateRuntimeType = "FORM_1D",
                AggregateMode = ReportAggregateMode.SumCells,
                PeriodKey = req.PeriodKey,
                SourceReportCount = 0,
                Workbook = null,
                Sources = new List<AggregateSourceRowDto>()
            };
        }

        var childIds = children.Select(x => x.Id).ToList();

        var reportFilter = Builders<WorkAssignmentReport>.Filter.And(
            Builders<WorkAssignmentReport>.Filter.In(x => x.WorkAssignmentId, childIds),
            Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false)
        );

        if (!string.IsNullOrWhiteSpace(req.PeriodKey))
        {
            reportFilter &= Builders<WorkAssignmentReport>.Filter.Eq(x => x.PeriodKey, req.PeriodKey);
        }
        else
        {
            var latestKeys = children
                .Where(x => !string.IsNullOrWhiteSpace(x.LatestPeriodKey))
                .Select(x => x.LatestPeriodKey!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (latestKeys.Count > 0)
            {
                reportFilter &= Builders<WorkAssignmentReport>.Filter.In(x => x.PeriodKey, latestKeys);
            }
        }

        var mode = (req.SourceStatusMode ?? "APPROVED_ONLY").Trim().ToUpperInvariant();
        if (mode == "APPROVED_AND_SUBMITTED")
        {
            reportFilter &= Builders<WorkAssignmentReport>.Filter.In(
                x => x.Status,
                new[]
                {
                WorkAssignmentReportStatus.Approved,
                WorkAssignmentReportStatus.Submitted
                });
        }
        else
        {
            reportFilter &= Builders<WorkAssignmentReport>.Filter.Eq(
                x => x.Status,
                WorkAssignmentReportStatus.Approved);
        }

        var reports = await _ctx.WorkAssignmentReports
            .Find(reportFilter)
            .ToListAsync(ct);

        if (reports.Count == 0)
        {
            return new AggregateReportResponse
            {
                WorkAssignmentId = root.Id,
                DynamicExcelId = root.DynamicExcelId,
                DynamicExcelCode = root.DynamicExcelCode,
                DynamicExcelName = root.DynamicExcelName,
                TemplateRuntimeType = "FORM_1D",
                AggregateMode = ReportAggregateMode.SumCells,
                PeriodKey = req.PeriodKey,
                SourceReportCount = 0,
                Workbook = null,
                Sources = new List<AggregateSourceRowDto>()
            };
        }

        var latestReports = reports
            .GroupBy(x => new { x.WorkAssignmentId, x.PeriodKey })
            .Select(g => g.OrderByDescending(x => x.UpdatedAtUtc).First())
            .ToList();

        var templateRuntimeType = ResolveTemplateRuntimeType(root, latestReports);
        var aggregateMode = ReportAggregateHelper.ResolveAggregateMode(templateRuntimeType);

        var workbookList = latestReports
            .Select(GetReportWorkbookData)
            .Where(x => x != null)
            .ToList();

        var workbook = ReportAggregateHelper.AggregateWorkbook(templateRuntimeType, workbookList);

        var childMap = children.ToDictionary(x => x.Id, x => x);

        var sources = latestReports
            .Select(x =>
            {
                childMap.TryGetValue(x.WorkAssignmentId, out var child);

                var firstAssignee = child?.Assignees?.FirstOrDefault();

                return new AggregateSourceRowDto
                {
                    ReportId = x.Id,
                    WorkAssignmentId = x.WorkAssignmentId,
                    AssigneeUserId = firstAssignee?.UserId ?? string.Empty,
                    //AssigneeName = firstAssignee?.FullName ?? string.Empty,
                    //UnitId = firstAssignee?.UnitId,
                    //UnitName = firstAssignee?.UnitName,
                    ReportStatus = (int)x.Status,
                    PeriodKey = x.PeriodKey,
                    SubmittedAtUtc = x.SubmittedAtUtc,
                    ApprovedAtUtc = x.ApprovedAtUtc
                };
            })
            .ToList();

        return new AggregateReportResponse
        {
            WorkAssignmentId = root.Id,
            DynamicExcelId = root.DynamicExcelId,
            DynamicExcelCode = root.DynamicExcelCode,
            DynamicExcelName = root.DynamicExcelName,
            TemplateRuntimeType = templateRuntimeType,
            AggregateMode = aggregateMode,
            PeriodKey = req.PeriodKey,
            SourceReportCount = latestReports.Count,
            Workbook = workbook,
            Sources = sources
        };
    }

    private string ResolveTemplateRuntimeType(WorkAssignment root, List<WorkAssignmentReport> latestReports)
    {
        var firstWorkbook = latestReports
            .Select(GetReportWorkbookData)
            .FirstOrDefault(x => x != null);

        return _typeResolver.Resolve(firstWorkbook);
    }

    private static object? GetReportWorkbookData(WorkAssignmentReport report)
    {
        return report.Data;
    }
}