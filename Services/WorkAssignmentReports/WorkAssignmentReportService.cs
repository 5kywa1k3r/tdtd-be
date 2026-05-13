using MongoDB.Driver;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Operations;
using tdtd_be.DTOs.WorkAssignments.AggregateTable;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services;
using tdtd_be.Services.WorkAssignments.Domain;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments.Aggregate;
using tdtd_be.Services.WorkAssignments.Queue;
using tdtd_be.Services.WorkAssignments.Runtime;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Services.WorkAssignmentReports;

public sealed class WorkAssignmentReportService : IWorkAssignmentReportService
{
    private static readonly Regex LabelCodeRegex = new("^[a-z0-9][a-z0-9_.-]{0,63}$", RegexOptions.Compiled);

    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentQueueService _queueService;
    private readonly IWorkAssignmentStatusSyncService _statusSync;
    private readonly IWorkAssignmentMaterializeJobService _materializeJob;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
    private readonly IDocRoleReadModelFreshnessService _docRoleReadModelFreshness;
    private readonly IWorkStatusOperationLogService _statusLog;
    private readonly IUserActionLogService _userActionLog;
    private readonly IWorkReportLabelStatisticsService _labelStatistics;
    private readonly IWorkReportTableStatisticsService _tableStatistics;
    private readonly IWorkReportFieldStatisticsService _fieldStatistics;
    private readonly IAggregateTableService _aggregateTableService;
    private readonly ILogger<WorkAssignmentReportService> _log;

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public WorkAssignmentReportService(
        MongoDbContext ctx,
        IWorkAssignmentQueueService queueService,
        IWorkAssignmentStatusSyncService statusSync,
        IWorkAssignmentMaterializeJobService materializeJob,
        IDocRoleReadModelProjectionService docRoleReadModelProjection,
        IDocRoleReadModelFreshnessService docRoleReadModelFreshness,
        IWorkStatusOperationLogService statusLog,
        IUserActionLogService userActionLog,
        IWorkReportLabelStatisticsService labelStatistics,
        IWorkReportTableStatisticsService tableStatistics,
        IWorkReportFieldStatisticsService fieldStatistics,
        IAggregateTableService aggregateTableService,
        ILogger<WorkAssignmentReportService> log)
    {
        _ctx = ctx;
        _queueService = queueService;
        _statusSync = statusSync;
        _materializeJob = materializeJob;
        _docRoleReadModelProjection = docRoleReadModelProjection;
        _docRoleReadModelFreshness = docRoleReadModelFreshness;
        _statusLog = statusLog;
        _userActionLog = userActionLog;
        _labelStatistics = labelStatistics;
        _tableStatistics = tableStatistics;
        _fieldStatistics = fieldStatistics;
        _aggregateTableService = aggregateTableService;
        _log = log;
    }

    public async Task<PagedResult<MyReportTemplateRow>> SearchMyReportTemplatesAsync(
        string workId,
        MyReportTemplateSearchRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(workId))
            throw ReportWorkIdRequired(workId);

        req ??= new MyReportTemplateSearchRequest();
        var page = req.Page < 0 ? 0 : req.Page;
        var pageSize = req.PageSize <= 0 ? 20 : req.PageSize;

        await EnsureMyReportListDocRolesForUserWorkAsync(workId, actorUserId, ct);

        var filter = Builders<MyReportTemplateListDocRole>.Filter.Eq(x => x.UserId, actorUserId)
            & Builders<MyReportTemplateListDocRole>.Filter.Eq(x => x.WorkId, workId)
            & Builders<MyReportTemplateListDocRole>.Filter.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var q = req.Q.Trim();
            var regex = new MongoDB.Bson.BsonRegularExpression(Regex.Escape(q), "i");

            filter &= Builders<MyReportTemplateListDocRole>.Filter.Or(
                Builders<MyReportTemplateListDocRole>.Filter.Regex(x => x.DynamicFormTemplateCode, regex),
                Builders<MyReportTemplateListDocRole>.Filter.Regex(x => x.DynamicFormTemplateName, regex),
                Builders<MyReportTemplateListDocRole>.Filter.Regex(x => x.DynamicExcelCode, regex),
                Builders<MyReportTemplateListDocRole>.Filter.Regex(x => x.DynamicExcelName, regex)
            );
        }

        if (req.HasOverduePeriod.HasValue)
            filter &= Builders<MyReportTemplateListDocRole>.Filter.Eq(x => x.HasOverduePeriod, req.HasOverduePeriod.Value);

        if (req.HasReport.HasValue)
        {
            filter &= req.HasReport.Value
                ? Builders<MyReportTemplateListDocRole>.Filter.Gt(x => x.ReportCount, 0)
                : Builders<MyReportTemplateListDocRole>.Filter.Eq(x => x.ReportCount, 0);
        }

        var total = await _ctx.MyReportTemplateListDocRoles.CountDocumentsAsync(filter, cancellationToken: ct);

        var rows = await _ctx.MyReportTemplateListDocRoles
            .Find(filter)
            .Sort(BuildTemplateListDocRoleSort(req.SortField, req.SortDirection))
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        var items = rows.Select(x => new MyReportTemplateRow
        {
            DynamicFormTemplateId = x.DynamicFormTemplateId ?? string.Empty,
            DynamicFormTemplateCode = x.DynamicFormTemplateCode ?? string.Empty,
            DynamicFormTemplateName = x.DynamicFormTemplateName ?? string.Empty,
            DynamicExcelId = x.DynamicExcelId,
            DynamicExcelCode = x.DynamicExcelCode ?? string.Empty,
            DynamicExcelName = x.DynamicExcelName ?? string.Empty,
            BindingCount = x.BindingCount,
            PeriodCount = x.PeriodCount,
            ReportCount = x.ReportCount,
            LatestPeriodId = x.LatestPeriodId,
            LatestPeriodKey = x.LatestPeriodKey,
            LatestPeriodStatus = x.LatestPeriodStatus.HasValue ? (int?)x.LatestPeriodStatus.Value : null,
            LatestDueAtUtc = x.LatestDueAtUtc,
            LatestReportId = x.LatestReportId,
            LatestUpdatedAtUtc = x.LatestUpdatedAtUtc,
            HasOverduePeriod = x.HasOverduePeriod
        }).ToList();

        return new PagedResult<MyReportTemplateRow>(items, total, page, pageSize);
    }


    private async Task<(WorkAssignment assignment, bool isOwner, bool isAssignee)> EnsureAssignmentReportAccessAsync(
        string workAssignmentId,
        string actorUserId,
        CancellationToken ct)
    {
        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == workAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportAssignmentNotFound(workAssignmentId);

        var isOwner = assignment.CreatedByUserId == actorUserId;
        var isAssignee = false;

        var binding = await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkAssignmentId == workAssignmentId &&
                x.AssigneeUserId == actorUserId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (binding is not null)
            isAssignee = true;

        if (!isOwner && !isAssignee)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_ASSIGNMENT_ACCESS_FORBIDDEN,
                new { workAssignmentId, actorUserId });

        return (assignment, isOwner, isAssignee);
    }

    private async Task<List<WorkTemplateAssignee>> LoadVisibleReportBindingsAsync(
        string workId,
        string actorUserId,
        CancellationToken ct)
    {
        var filter = Builders<WorkTemplateAssignee>.Filter.Eq(x => x.WorkId, workId)
            & Builders<WorkTemplateAssignee>.Filter.Eq(x => x.AssigneeUserId, actorUserId)
            & Builders<WorkTemplateAssignee>.Filter.Eq(x => x.IsDeleted, false);

        return await _ctx.WorkTemplateAssignees
            .Find(filter)
            .ToListAsync(ct);
    }

    private async Task<List<WorkTemplateAssignee>> LoadVisibleReportBindingsByTemplateAsync(
        string workId,
        string dynamicFormTemplateId,
        string actorUserId,
        CancellationToken ct)
    {
        return await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkId == workId &&
                x.DynamicFormTemplateId == dynamicFormTemplateId &&
                x.AssigneeUserId == actorUserId &&
                !x.IsDeleted)
            .ToListAsync(ct);
    }

    private async Task EnsureMyReportListDocRolesForUserWorkAsync(
        string? workId,
        string actorUserId,
        CancellationToken ct)
    {
        var existingFilter = Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.UserId, actorUserId)
            & Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(workId))
            existingFilter &= Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.WorkId, workId);

        var hasProjectedRows = await _ctx.MyReportPeriodListDocRoles
            .Find(existingFilter)
            .AnyAsync(ct);

        if (hasProjectedRows)
            return;

        _log.LogWarning(
            "My report list projection missing. workId={workId} actorUserId={actorUserId}. Returning current projection only; run internal DocRole repair/backfill if source data exists.",
            workId,
            actorUserId);
    }

    private async Task<(WorkAssignment assignment, bool isOwner, bool isAssignee)> EnsurePeriodAccessAsync(
        WorkReportPeriod period,
        string actorUserId,
        CancellationToken ct)
    {
        var access = await EnsureAssignmentReportAccessAsync(period.WorkAssignmentId, actorUserId, ct);

        if (!string.IsNullOrWhiteSpace(period.AssigneeUserId) &&
            period.AssigneeUserId == actorUserId)
            return (access.assignment, access.isOwner, true);

        return access;
    }

    private async Task<(WorkAssignment assignment, bool isOwner, bool isAssignee)> EnsureReportAccessAsync(
        WorkAssignmentReport report,
        string actorUserId,
        CancellationToken ct)
    {
        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportAssignmentNotFound(report.WorkAssignmentId);

        var isOwner = assignment.CreatedByUserId == actorUserId;
        var isAssignee = !string.IsNullOrWhiteSpace(report.AssigneeUserId) && report.AssigneeUserId == actorUserId;

        if (!isAssignee)
        {
            isAssignee = await _ctx.WorkTemplateAssignees
                .Find(x =>
                    x.WorkAssignmentId == report.WorkAssignmentId &&
                    x.AssigneeUserId == actorUserId &&
                    !x.IsDeleted)
                .Limit(1)
                .AnyAsync(ct);
        }

        var canReview = !isOwner && !isAssignee && await HasReviewReportReadAccessAsync(report, actorUserId, ct);

        if (!isOwner && !isAssignee && !canReview)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_ACCESS_FORBIDDEN,
                ReportDetails(report, actorUserId));

        return (assignment, isOwner, isAssignee);
    }

    private Task<bool> HasReviewReportReadAccessAsync(
        WorkAssignmentReport report,
        string actorUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            return Task.FromResult(false);

        return _ctx.ReviewReportListDocRoles
            .Find(x =>
                x.ReviewerUserId == actorUserId &&
                (x.CurrentReportId == report.Id || x.WorkReportPeriodId == report.WorkReportPeriodId) &&
                !x.IsDeleted)
            .Limit(1)
            .AnyAsync(ct);
    }
    public async Task<MyReportTemplateDetailResponse> GetMyReportTemplateDetailAsync(
        string workId,
        string dynamicFormTemplateId,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(workId))
            throw ReportWorkIdRequired(workId);

        if (string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_DYNAMIC_FORM_TEMPLATE_ID_REQUIRED,
                new { workId, dynamicFormTemplateId });

        var bindings = await LoadVisibleReportBindingsByTemplateAsync(workId, dynamicFormTemplateId, actorUserId, ct);

        if (bindings.Count == 0)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_TEMPLATE_ACCESS_FORBIDDEN,
                new { workId, dynamicFormTemplateId, actorUserId });

        var primaryBinding = bindings
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.CreatedAtUtc)
            .First();

        var bindingIds = bindings
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .Select(x => x.Id!)
            .Distinct()
            .ToList();

        var periods = await _ctx.WorkReportPeriods
            .Find(x =>
                bindingIds.Contains(x.WorkTemplateAssigneeId) &&
                !x.IsDeleted)
            .SortByDescending(x => x.PeriodKey)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(ct);

        // Self-heal mềm: có binding nhưng chưa có period -> đánh thức materialize job
        if (periods.Count == 0)
        {
            await TouchMaterializeJobsIfNoPeriodsAsync(bindings, actorUserId, ct);

            periods = await _ctx.WorkReportPeriods
                .Find(x =>
                    bindingIds.Contains(x.WorkTemplateAssigneeId) &&
                    !x.IsDeleted)
                .SortByDescending(x => x.PeriodKey)
                .ThenByDescending(x => x.UpdatedAtUtc)
                .ToListAsync(ct);
        }

        var dynamicExcelId = primaryBinding.DynamicExcelId;
        var template = string.IsNullOrWhiteSpace(dynamicExcelId)
            ? null
            : await _ctx.DynamicExcelTemplates
                .Find(x => x.Id == dynamicExcelId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);

        return new MyReportTemplateDetailResponse
        {
            WorkId = workId,
            DynamicFormTemplateId = dynamicFormTemplateId,
            DynamicFormTemplateCode = primaryBinding.DynamicFormTemplateCode ?? string.Empty,
            DynamicFormTemplateName = primaryBinding.DynamicFormTemplateName ?? string.Empty,
            DynamicExcelId = dynamicExcelId,
            DynamicExcelCode = template?.Code ?? primaryBinding.DynamicExcelCode,
            DynamicExcelName = template?.Name ?? primaryBinding.DynamicExcelName,
            WorkTemplateAssigneeId = primaryBinding.Id,
            WorkAssignmentId = primaryBinding.WorkAssignmentId,
            TemplateSnapshotJson = template is null ? string.Empty : JsonSerializer.Serialize(BuildTemplateSnapshot(template), _jsonOptions),
            Periods = periods.Select(MapToPeriodRow).ToList()
        };
    }

    public async Task<WorkAssignmentReportResponse> OpenPeriodAsync(
        string workReportPeriodId,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(workReportPeriodId))
            throw ReportPeriodIdRequired(workReportPeriodId);

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == workReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (period is null)
            throw ReportPeriodNotFound(workReportPeriodId);

        await EnsurePeriodAccessAsync(period, actorUserId, ct);

        if (period.AssigneeUserId != actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_PERIOD_ACCESS_FORBIDDEN,
                PeriodDetails(period, actorUserId));

        if (!period.IsActive)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_PERIOD_INACTIVE,
                PeriodDetails(period, actorUserId));

        if (!string.IsNullOrWhiteSpace(period.CurrentReportId))
        {
            var existed = await _ctx.WorkAssignmentReports
                .Find(Builders<WorkAssignmentReport>.Filter.Eq(x => x.Id, period.CurrentReportId)
                      & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false)
                      & Builders<WorkAssignmentReport>.Filter.Ne(x => x.IsActive, false))
                .FirstOrDefaultAsync(ct);

            if (existed is not null)
            {
                await _docRoleReadModelProjection.RebuildReportPeriodAsync(period.Id, actorUserId, ct);
                await _docRoleReadModelFreshness.EnsureReportPeriodFreshAsync(period, existed, actorUserId, ct);
                return await MapToResponseAsync(existed, period, ct);
            }
        }

        var created = await CreateDraftForPeriodAsync(period, actorUserId, ct);
        await FinalizeReportStatusOperationAsync(
            "INIT_DRAFT",
            created,
            period,
            fromStatus: "NONE",
            toStatus: WorkAssignmentReportStatus.Draft.ToString(),
            actorUserId,
            upsertQueue: true,
            disableQueue: false,
            rebuildProjection: true,
            syncAssignment: true,
            ct);
        await _docRoleReadModelFreshness.EnsureReportPeriodFreshAsync(period, created, actorUserId, ct);

        return await MapToResponseAsync(created, period, ct);
    }

    public async Task<WorkAssignmentReportResponse> InitDraftAsync(
        string workAssignmentId,
        InitWorkAssignmentReportRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(workAssignmentId))
            throw ReportAssignmentIdRequired(workAssignmentId);

        req ??= new InitWorkAssignmentReportRequest();

        if (string.IsNullOrWhiteSpace(req.PeriodKey))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_PERIOD_KEY_REQUIRED,
                new { workAssignmentId, req.PeriodKey });

        var binding = await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkAssignmentId == workAssignmentId &&
                x.AssigneeUserId == actorUserId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (binding is null)
            throw ReportBindingNotFound(workAssignmentId, actorUserId);

        var existedPeriod = await _ctx.WorkReportPeriods
            .Find(x =>
                x.WorkTemplateAssigneeId == binding.Id &&
                x.PeriodKey == req.PeriodKey.Trim() &&
                (x.PeriodKind == null || x.PeriodKind == WorkReportPeriodKind.Scheduled) &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (existedPeriod is null)
            throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_PERIOD_NOT_MATERIALIZED,
                new { workAssignmentId, actorUserId, periodKey = req.PeriodKey });

        return await OpenPeriodAsync(existedPeriod.Id, actorUserId, ct);
    }

    public async Task<WorkAssignmentReportResponse> CreateUserCreatedReportAsync(
        string workAssignmentId,
        CreateUserCreatedReportRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(workAssignmentId))
            throw ReportAssignmentIdRequired(workAssignmentId);

        req ??= new CreateUserCreatedReportRequest();

        var binding = await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkAssignmentId == workAssignmentId &&
                x.AssigneeUserId == actorUserId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (binding is null)
            throw ReportBindingNotFound(workAssignmentId, actorUserId);

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == workAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportAssignmentNotFound(workAssignmentId);

        if (!assignment.IsActive || !binding.IsActive)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_ASSIGNMENT_INACTIVE,
                new { workAssignmentId, bindingId = binding.Id, actorUserId });

        WorkReportPeriod? linkedScheduledPeriod = null;
        if (!string.IsNullOrWhiteSpace(req.LinkedScheduledPeriodId))
        {
            linkedScheduledPeriod = await _ctx.WorkReportPeriods
                .Find(x =>
                    x.Id == req.LinkedScheduledPeriodId.Trim() &&
                    x.WorkTemplateAssigneeId == binding.Id &&
                    x.AssigneeUserId == actorUserId &&
                    !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw AppExceptionFactory.NotFound(
                    AppErrorCode.WORK_ASSIGNMENT_REPORT_LINKED_PERIOD_NOT_FOUND,
                    new
                    {
                        workAssignmentId,
                        bindingId = binding.Id,
                        actorUserId,
                        linkedScheduledPeriodId = req.LinkedScheduledPeriodId
                    });
        }

        var now = DateTime.UtcNow;
        var reportDate = NormalizeDate(req.ReportDate ?? now)!.Value;
        var periodKey = NormalizeUserCreatedPeriodKey(req.PeriodKey, reportDate);
        var periodStart = NormalizeDate(req.PeriodStart) ?? reportDate;
        var periodEnd = NormalizeDate(req.PeriodEnd) ?? reportDate;
        var startedDate = NormalizeDate(req.StartedDate ?? periodStart);
        var completedDate = NormalizeDate(req.CompletedDate);
        EnsureReportDateRange(periodStart, periodEnd, "PeriodStart", "PeriodEnd");
        EnsureReportDateRange(startedDate, completedDate, "StartedDate", "CompletedDate");
        var isHistoricalData = IsHistoricalReportData(reportDate, periodStart, periodEnd, now);
        var periodInstanceKey = $"USER_CREATED:{ObjectId.GenerateNewId()}";

        var period = new WorkReportPeriod
        {
            WorkId = assignment.WorkId,
            WorkAssignmentId = assignment.Id,
            WorkTemplateAssigneeId = binding.Id,
            DynamicExcelId = binding.DynamicExcelId,
            DynamicExcelCode = binding.DynamicExcelCode,
            DynamicExcelName = binding.DynamicExcelName,
            DynamicFormTemplateId = binding.DynamicFormTemplateId ?? assignment.DynamicFormTemplateId,
            DynamicFormTemplateCode = binding.DynamicFormTemplateCode ?? assignment.DynamicFormTemplateCode,
            DynamicFormTemplateName = binding.DynamicFormTemplateName ?? assignment.DynamicFormTemplateName,
            AssigneeUserId = actorUserId,
            AssigneeUnitId = binding.AssigneeUnitId,
            PeriodKey = periodKey,
            PeriodInstanceKey = periodInstanceKey,
            PeriodKind = WorkReportPeriodKind.UserCreated,
            ReportTitle = NormalizeOptionalTextOrNull(req.ReportTitle) ?? $"Báo cáo chủ động {periodKey}",
            ReportDate = reportDate,
            LinkedScheduledPeriodId = linkedScheduledPeriod?.Id,
            StartedDate = startedDate,
            CompletedDate = completedDate,
            IsHistoricalData = isHistoricalData,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            DueAtUtc = req.DueAtUtc,
            Status = ResolveDraftPeriodStatus(req.DueAtUtc, now),
            IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(ResolveDraftPeriodStatus(req.DueAtUtc, now)),
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId
        };

        await _ctx.WorkReportPeriods.InsertOneAsync(period, cancellationToken: ct);

        var created = await CreateDraftForPeriodAsync(period, actorUserId, ct);
        await FinalizeReportStatusOperationAsync(
            "CREATE_USER_REPORT",
            created,
            period,
            fromStatus: "NONE",
            toStatus: WorkAssignmentReportStatus.Draft.ToString(),
            actorUserId,
            upsertQueue: false,
            disableQueue: false,
            rebuildProjection: true,
            syncAssignment: true,
            ct);

        return await MapToResponseAsync(created, period, ct);
    }

    public async Task DeleteUserCreatedReportAsync(
        string id,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(id))
            throw ReportIdRequired(id);

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportNotFound(id);

        var reportAccess = await EnsureReportAccessAsync(entity, actorUserId, ct);
        if (!reportAccess.isAssignee || entity.AssigneeUserId != actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_DELETE_FORBIDDEN,
                ReportDetails(entity, actorUserId));

        if (entity.Status == WorkAssignmentReportStatus.Approved)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_DELETE_APPROVED_FORBIDDEN,
                ReportDetails(entity, actorUserId));

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportPeriodNotFound(entity.WorkReportPeriodId);

        if (!WorkReportPeriodKind.IsUserCreated(period.PeriodKind))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_DELETE_USER_CREATED_ONLY,
                PeriodDetails(period, actorUserId));

        var now = DateTime.UtcNow;
        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == entity.Id && !x.IsDeleted,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.IsDeleted, true)
                .Set(x => x.DeletedAtUtc, now)
                .Set(x => x.DeletedByUserId, actorUserId)
                .Set(x => x.IsCurrent, false)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        await _ctx.WorkReportPeriods.UpdateOneAsync(
            x => x.Id == period.Id && !x.IsDeleted,
            Builders<WorkReportPeriod>.Update
                .Set(x => x.IsDeleted, true)
                .Set(x => x.DeletedAtUtc, now)
                .Set(x => x.DeletedByUserId, actorUserId)
                .Set(x => x.IsActive, false)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        await InsertLogAsync(
            workId: entity.WorkId,
            workAssignmentId: entity.WorkAssignmentId,
            workReportPeriodId: entity.WorkReportPeriodId,
            workAssignmentReportId: entity.Id,
            action: "DELETE_USER_REPORT",
            fromStatus: entity.Status.ToString(),
            toStatus: "DELETED",
            actionByUserId: actorUserId,
            reason: null,
            comment: null,
            snapshotJson: null,
            ct: ct);

        await _docRoleReadModelProjection.SoftDeleteByDocAsync(DocType.WORK_REPORT, period.Id, actorUserId, ct);
        await _statusSync.SyncFromAssignmentAsync(entity.WorkAssignmentId, ct);
    }

    public async Task<WorkAssignmentReportResponse> GetByIdAsync(
        string id,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(id))
            throw ReportIdRequired(id);

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw ReportNotFound(id);

        await EnsureReportAccessAsync(entity, actorUserId, ct);

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        await _docRoleReadModelFreshness.EnsureReportPeriodFreshAsync(period, entity, actorUserId, ct);

        entity = await RefreshAggregateSnapshotForReadAsync(entity, actorUserId, ct);

        return await MapToResponseAsync(entity, period, ct);
    }

    public async Task<List<WorkAssignmentReportListRow>> GetByAssignmentAsync(
        string workAssignmentId,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(workAssignmentId))
            throw ReportAssignmentIdRequired(workAssignmentId);

        var access = await EnsureAssignmentReportAccessAsync(workAssignmentId, actorUserId, ct);

        var reportFilter = Builders<WorkAssignmentReport>.Filter.Eq(x => x.WorkAssignmentId, workAssignmentId)
            & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false)
            & Builders<WorkAssignmentReport>.Filter.Ne(x => x.IsActive, false);

        if (!access.isOwner)
            reportFilter &= Builders<WorkAssignmentReport>.Filter.Eq(x => x.AssigneeUserId, actorUserId);

        var rows = await _ctx.WorkAssignmentReports
            .Find(reportFilter)
            .SortByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(ct);

        return rows.Select(MapToListRow).ToList();
    }

    public async Task<PagedResult<WorkAssignmentReportListRow>> SearchAsync(
        WorkAssignmentReportSearchRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        req ??= new WorkAssignmentReportSearchRequest();
        var page = req.Page < 0 ? 0 : req.Page;
        var pageSize = req.PageSize <= 0 ? 20 : req.PageSize;

        await EnsureMyReportListDocRolesForUserWorkAsync(req.WorkId, actorUserId, ct);

        // NOTE:
        // Search này là search report/kỳ báo cáo của chính người nộp (assignee).
        // Không dùng cho inbox duyệt của người giao.
        // PeriodKey hiện được dùng như DayKey / DueOccurrenceKey.
        var filter = Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.IsDeleted, false)
            & Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.UserId, actorUserId);

        if (!string.IsNullOrWhiteSpace(req.WorkId))
            filter &= Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.WorkId, req.WorkId);

        if (!string.IsNullOrWhiteSpace(req.WorkAssignmentId))
            filter &= Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.AssignmentId, req.WorkAssignmentId);

        if (!string.IsNullOrWhiteSpace(req.WorkReportPeriodId))
            filter &= Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.WorkReportPeriodId, req.WorkReportPeriodId);

        if (!string.IsNullOrWhiteSpace(req.PeriodKey))
            filter &= Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.PeriodKey, req.PeriodKey.Trim());

        if (req.Status.HasValue)
            filter &= Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.ReportStatus, (WorkAssignmentReportStatus)req.Status.Value);

        if (req.IsCurrent.HasValue)
            filter &= Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.IsCurrentReport, req.IsCurrent.Value);

        if (req.IsLateSubmission.HasValue)
            filter &= Builders<MyReportPeriodListDocRole>.Filter.Eq(x => x.IsLateSubmission, req.IsLateSubmission.Value);

        if (req.DueFromUtc.HasValue)
            filter &= Builders<MyReportPeriodListDocRole>.Filter.Gte(x => x.DueAtUtc, req.DueFromUtc.Value);

        if (req.DueToUtc.HasValue)
            filter &= Builders<MyReportPeriodListDocRole>.Filter.Lte(x => x.DueAtUtc, req.DueToUtc.Value);

        if (req.SubmittedFromUtc.HasValue)
            filter &= Builders<MyReportPeriodListDocRole>.Filter.Gte(x => x.LastSubmittedAtUtc, req.SubmittedFromUtc.Value);

        if (req.SubmittedToUtc.HasValue)
            filter &= Builders<MyReportPeriodListDocRole>.Filter.Lte(x => x.LastSubmittedAtUtc, req.SubmittedToUtc.Value);

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var q = req.Q.Trim();
            var regex = new MongoDB.Bson.BsonRegularExpression(q, "i");

            filter &= Builders<MyReportPeriodListDocRole>.Filter.Or(
                Builders<MyReportPeriodListDocRole>.Filter.Regex(x => x.PeriodKey, regex),
                Builders<MyReportPeriodListDocRole>.Filter.Regex(x => x.DynamicExcelCode, regex),
                Builders<MyReportPeriodListDocRole>.Filter.Regex(x => x.DynamicExcelName, regex)
            );
        }

        var total = await _ctx.MyReportPeriodListDocRoles.CountDocumentsAsync(filter, cancellationToken: ct);

        var rows = await _ctx.MyReportPeriodListDocRoles
            .Find(filter)
            .Sort(BuildReportPeriodListDocRoleSort(req.SortField, req.SortDirection))
            .Skip(page * pageSize)
            .Limit(pageSize)
            .Project(MapToListRowProjection())
            .ToListAsync(ct);

        return new PagedResult<WorkAssignmentReportListRow>(
            rows,
            total,
            page,
            pageSize);
    }

    public async Task<WorkAssignmentReportResponse> SaveDraftAsync(
        string id,
        SaveWorkAssignmentReportDraftRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(id))
            throw ReportIdRequired(id);

        if (req is null)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_REQUEST_REQUIRED,
                new { reportId = id });

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw ReportNotFound(id);
        EnsureReportIsActive(entity);

        var reportAccess = await EnsureReportAccessAsync(entity, actorUserId, ct);
        if (!reportAccess.isAssignee || entity.AssigneeUserId != actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_SAVE_FORBIDDEN,
                ReportDetails(entity, actorUserId));

        if (entity.Status != WorkAssignmentReportStatus.Draft)
            throw InvalidReportStatus(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_SAVE_STATUS_INVALID,
                entity,
                WorkAssignmentReportStatus.Draft,
                actorUserId);

        var expectedLength = entity.W * entity.H;
        var actualLength = req.Values1D?.Count ?? 0;

        if (actualLength != expectedLength)
            throw InvalidReportValues(entity, expectedLength, actualLength, actorUserId);

        var now = DateTime.UtcNow;
        var fromStatus = entity.Status;
        var nextStatus = WorkAssignmentReportStatus.Draft;
        var nextDataOrigin = ResolveReportDataOrigin(req.DataOrigin, entity.DataOrigin);
        var nextContributionMode = ResolveCumulativeContributionMode(
            req.CumulativeContributionMode,
            req.DataOrigin,
            entity.CumulativeContributionMode,
            entity.DataOrigin);
        var nextContributionPolicyJson = ResolveContributionPolicyJsonOverride(
            req.CumulativeContributionPolicyJson,
            entity.CumulativeContributionPolicyJson,
            id,
            actorUserId);
        var nextSummarySourceJson = ResolveSummarySourceJsonOverride(
            req.SummarySourceJson,
            entity.SummarySourceJson,
            id,
            actorUserId);
        var nextAggregateSources = ExtractAggregateSourceSnapshot(nextSummarySourceJson);
        var acceptsReportDataPayload = ShouldAcceptReportDataPayload(entity, nextDataOrigin, nextSummarySourceJson);

        await ValidateRuntimeRowLabelsAsync(
            entity,
            acceptsReportDataPayload ? req.TableValuesJson : entity.TableValuesJson,
            ct);
        await ValidateRuntimeRecordTableAsync(
            entity,
            acceptsReportDataPayload ? req.TableValuesJson : entity.TableValuesJson,
            ct);

        var values1DJson = acceptsReportDataPayload
            ? JsonSerializer.Serialize(req.Values1D, _jsonOptions)
            : entity.Values1DJson;
        var fieldValuesJson = acceptsReportDataPayload ? req.FieldValuesJson : entity.FieldValuesJson;
        var tableValuesJson = acceptsReportDataPayload ? req.TableValuesJson : entity.TableValuesJson;
        var nextAggregateSnapshotDirty = nextAggregateSources.IsAggregate
            ? !acceptsReportDataPayload && entity.AggregateSnapshotDirty
            : false;
        var nextAggregateSnapshotDirtyAtUtc = nextAggregateSnapshotDirty
            ? entity.AggregateSnapshotDirtyAtUtc
            : null;
        var nextAggregateSnapshotRefreshedAtUtc = nextAggregateSources.IsAggregate && acceptsReportDataPayload
            ? now
            : entity.AggregateSnapshotRefreshedAtUtc;
        var nextAggregateSourceUpdatedAtUtc = nextAggregateSources.IsAggregate
            ? (acceptsReportDataPayload ? now : entity.AggregateSourceUpdatedAtUtc)
            : null;

        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.Values1DJson, values1DJson)
                .Set(x => x.FieldValuesJson, fieldValuesJson)
                .Set(x => x.TableValuesJson, tableValuesJson)
                .Set(x => x.DataOrigin, nextDataOrigin)
                .Set(x => x.CumulativeContributionMode, nextContributionMode)
                .Set(x => x.CumulativeContributionPolicyJson, nextContributionPolicyJson)
                .Set(x => x.SummarySourceJson, nextSummarySourceJson)
                .Set(x => x.AggregateSourceReportIds, nextAggregateSources.ReportIds)
                .Set(x => x.AggregateSourceAssignmentIds, nextAggregateSources.AssignmentIds)
                .Set(x => x.AggregateSourceUpdatedAtUtc, nextAggregateSourceUpdatedAtUtc)
                .Set(x => x.AggregateSnapshotDirty, nextAggregateSnapshotDirty)
                .Set(x => x.AggregateSnapshotDirtyAtUtc, nextAggregateSnapshotDirtyAtUtc)
                .Set(x => x.AggregateSnapshotRefreshedAtUtc, nextAggregateSnapshotRefreshedAtUtc)
                .Set(x => x.AggregateRefreshError, (string?)null)
                .Set(x => x.CurrentProgressStatus, req.CurrentProgressStatus)
                .Set(x => x.ReportReason, req.ReportReason)
                .Set(x => x.Difficulties, req.Difficulties)
                .Set(x => x.ProposedSolution, req.ProposedSolution)
                .Set(x => x.LateReason, req.LateReason)
                .Set(x => x.Status, nextStatus)
                .Set(x => x.CreatedByUserId, string.IsNullOrWhiteSpace(entity.CreatedByUserId) ? actorUserId : entity.CreatedByUserId)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        entity.Values1DJson = values1DJson;
        entity.FieldValuesJson = fieldValuesJson;
        entity.TableValuesJson = tableValuesJson;
        entity.DataOrigin = nextDataOrigin;
        entity.CumulativeContributionMode = nextContributionMode;
        entity.CumulativeContributionPolicyJson = nextContributionPolicyJson;
        entity.SummarySourceJson = nextSummarySourceJson;
        entity.AggregateSourceReportIds = nextAggregateSources.ReportIds;
        entity.AggregateSourceAssignmentIds = nextAggregateSources.AssignmentIds;
        entity.AggregateSourceUpdatedAtUtc = nextAggregateSourceUpdatedAtUtc;
        entity.AggregateSnapshotDirty = nextAggregateSnapshotDirty;
        entity.AggregateSnapshotDirtyAtUtc = nextAggregateSnapshotDirtyAtUtc;
        entity.AggregateSnapshotRefreshedAtUtc = nextAggregateSnapshotRefreshedAtUtc;
        entity.AggregateRefreshError = null;
        entity.CurrentProgressStatus = req.CurrentProgressStatus;
        entity.ReportReason = req.ReportReason;
        entity.Difficulties = req.Difficulties;
        entity.ProposedSolution = req.ProposedSolution;
        entity.LateReason = req.LateReason;
        entity.Status = nextStatus;
        entity.CreatedByUserId = string.IsNullOrWhiteSpace(entity.CreatedByUserId) ? actorUserId : entity.CreatedByUserId;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = actorUserId;

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (period is not null)
        {
            var periodStatus = ResolveDraftPeriodStatus(period.DueAtUtc, now);

            await _ctx.WorkReportPeriods.UpdateOneAsync(
                x => x.Id == period.Id && !x.IsDeleted,
                Builders<WorkReportPeriod>.Update
                    .Set(x => x.Status, periodStatus)
                    .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(periodStatus))
                    .Set(x => x.LastDraftSavedAtUtc, now)
                    .Set(x => x.CurrentProgressStatus, req.CurrentProgressStatus)
                    .Set(x => x.ReportReason, req.ReportReason)
                    .Set(x => x.Difficulties, req.Difficulties)
                    .Set(x => x.ProposedSolution, req.ProposedSolution)
                    .Set(x => x.LateReason, req.LateReason)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId),
                cancellationToken: ct);

            period.Status = periodStatus;
            period.IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(periodStatus);
            period.LastDraftSavedAtUtc = now;
            period.CurrentProgressStatus = req.CurrentProgressStatus;
            period.ReportReason = req.ReportReason;
            period.Difficulties = req.Difficulties;
            period.ProposedSolution = req.ProposedSolution;
            period.LateReason = req.LateReason;
            period.UpdatedAtUtc = now;
            period.UpdatedByUserId = actorUserId;
        }

        await InsertLogAsync(
            workId: entity.WorkId,
            workAssignmentId: entity.WorkAssignmentId,
            workReportPeriodId: entity.WorkReportPeriodId,
            workAssignmentReportId: entity.Id,
            action: "SAVE_DRAFT",
            fromStatus: fromStatus.ToString(),
            toStatus: nextStatus.ToString(),
            actionByUserId: actorUserId,
            reason: req.ReportReason,
            comment: req.Difficulties,
            snapshotJson: null,
            ct: ct);

        if (period is not null)
        {
            await FinalizeReportStatusOperationAsync(
                "SAVE_DRAFT",
                entity,
                period,
                fromStatus.ToString(),
                nextStatus.ToString(),
                actorUserId,
                upsertQueue: true,
                disableQueue: false,
                rebuildProjection: true,
                syncAssignment: true,
                ct);
        }

        return await MapToResponseAsync(entity, period, ct);
    }

    public async Task<WorkAssignmentReportResponse> ApplyDynamicFormAggregateDraftAsync(
        string id,
        ApplyDynamicFormAggregateDraftRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(id))
            throw ReportIdRequired(id);

        if (req is null || req.AggregateRequest is null)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_AGGREGATE_DRAFT_REQUEST_INVALID,
                new { reportId = id });

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw ReportNotFound(id);
        EnsureReportIsActive(entity);

        var reportAccess = await EnsureReportAccessAsync(entity, actorUserId, ct);
        if (!reportAccess.isAssignee || entity.AssigneeUserId != actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_SAVE_FORBIDDEN,
                ReportDetails(entity, actorUserId));

        if (entity.Status != WorkAssignmentReportStatus.Draft)
            throw InvalidReportStatus(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_SAVE_STATUS_INVALID,
                entity,
                WorkAssignmentReportStatus.Draft,
                actorUserId);

        if (string.IsNullOrWhiteSpace(entity.DynamicFormTemplateId))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_AGGREGATE_DRAFT_TEMPLATE_MISMATCH,
                ReportDetails(entity, actorUserId));

        var projection = await BuildDynamicFormAggregateDraftProjectionAsync(entity, req, ct);

        var saveReq = new SaveWorkAssignmentReportDraftRequest
        {
            Values1D = projection.TopLevelValues,
            FieldValuesJson = entity.FieldValuesJson,
            TableValuesJson = projection.TableValuesJson,
            DataOrigin = projection.DataOrigin,
            CumulativeContributionMode = projection.ContributionMode,
            CumulativeContributionPolicyJson = projection.ContributionPolicyJson,
            SummarySourceJson = projection.SummarySourceJson,
            CurrentProgressStatus = entity.CurrentProgressStatus,
            ReportReason = entity.ReportReason,
            Difficulties = entity.Difficulties,
            ProposedSolution = entity.ProposedSolution,
            LateReason = entity.LateReason,
            Note = entity.Note
        };

        return await SaveDraftAsync(id, saveReq, actorUserId, ct);
    }

    public async Task<WorkAssignmentReportResponse> PreviewDynamicFormAggregateDraftAsync(
        string id,
        ApplyDynamicFormAggregateDraftRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(id))
            throw ReportIdRequired(id);

        if (req is null || req.AggregateRequest is null)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_AGGREGATE_DRAFT_REQUEST_INVALID,
                new { reportId = id });

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw ReportNotFound(id);
        EnsureReportIsActive(entity);

        var reportAccess = await EnsureReportAccessAsync(entity, actorUserId, ct);
        if (!reportAccess.isAssignee || entity.AssigneeUserId != actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_SAVE_FORBIDDEN,
                ReportDetails(entity, actorUserId));

        if (entity.Status != WorkAssignmentReportStatus.Draft)
            throw InvalidReportStatus(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_SAVE_STATUS_INVALID,
                entity,
                WorkAssignmentReportStatus.Draft,
                actorUserId);

        if (string.IsNullOrWhiteSpace(entity.DynamicFormTemplateId))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_AGGREGATE_DRAFT_TEMPLATE_MISMATCH,
                ReportDetails(entity, actorUserId));

        var projection = await BuildDynamicFormAggregateDraftProjectionAsync(entity, req, ct);
        var sourceSnapshot = ExtractAggregateSourceSnapshot(projection.SummarySourceJson);
        var now = DateTime.UtcNow;

        entity.Values1DJson = JsonSerializer.Serialize(projection.TopLevelValues, _jsonOptions);
        entity.TableValuesJson = projection.TableValuesJson;
        entity.DataOrigin = projection.DataOrigin;
        entity.CumulativeContributionMode = projection.ContributionMode;
        entity.CumulativeContributionPolicyJson = projection.ContributionPolicyJson;
        entity.SummarySourceJson = projection.SummarySourceJson;
        entity.AggregateSourceReportIds = sourceSnapshot.ReportIds;
        entity.AggregateSourceAssignmentIds = sourceSnapshot.AssignmentIds;
        entity.AggregateSourceUpdatedAtUtc = now;
        entity.AggregateSnapshotDirty = false;
        entity.AggregateSnapshotDirtyAtUtc = null;
        entity.AggregateSnapshotRefreshedAtUtc = now;
        entity.AggregateRefreshError = null;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = actorUserId;

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        return await MapToResponseAsync(entity, period, ct);
    }

    private async Task<DynamicFormAggregateDraftProjection> BuildDynamicFormAggregateDraftProjectionAsync(
        WorkAssignmentReport entity,
        ApplyDynamicFormAggregateDraftRequest req,
        CancellationToken ct)
    {
        var aggregateReq = NormalizeAggregateDraftRequest(req.AggregateRequest);
        var dynamicFormTemplateId = NormalizeOptionalTextOrNull(entity.DynamicFormTemplateId)
            ?? throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_AGGREGATE_DRAFT_TEMPLATE_MISMATCH,
                ReportDetails(entity));

        if (!string.Equals(dynamicFormTemplateId, aggregateReq.DynamicFormTemplateId, StringComparison.Ordinal))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_AGGREGATE_DRAFT_TEMPLATE_MISMATCH,
                new
                {
                    reportId = entity.Id,
                    reportTemplateId = dynamicFormTemplateId,
                    aggregateTemplateId = aggregateReq.DynamicFormTemplateId
                });

        var aggregate = await _aggregateTableService.GetDynamicFormAggregateAsync(aggregateReq, ct);

        var form = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == dynamicFormTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (form is null)
            throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_REPORT_STATISTICS_DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
                new { dynamicFormTemplateId, reportId = entity.Id });

        var targetBlockId = NormalizeBlockId(req.TargetBlockId ?? aggregateReq.BlockId ?? aggregate.Meta.BlockId);
        var block = ResolveAggregateDraftBlock(form, targetBlockId)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_AGGREGATE_DRAFT_BLOCK_NOT_FOUND,
                new { reportId = entity.Id, dynamicFormTemplateId = form.Id, blockId = targetBlockId });

        var dataOrigin = WorkReportDataOrigin.Normalize(req.DataOrigin ?? WorkReportDataOrigin.AutoSummary);
        var valueSelector = NormalizeAggregateDraftValueSelector(req.ValueSelector);
        var previousSummary = TryReadAggregateDraftSummary(entity.SummarySourceJson);
        var existingTopLevelValues = DeserializeValues1D(entity.Values1DJson);
        var isTopLevelBlock = string.Equals(targetBlockId, ResolveTopLevelBlockId(form), StringComparison.Ordinal);
        var clearExisting = req.ClearExistingValues ?? dataOrigin != WorkReportDataOrigin.PartialMapping;
        var currentBlockValues = isTopLevelBlock
            ? existingTopLevelValues
            : ExtractBlockDecimalValues(entity.TableValuesJson, targetBlockId);
        var targetValues = clearExisting
            ? CreateEmptyValues1D(block.W, block.H)
            : NormalizeDecimalValues(currentBlockValues, block.W * block.H);

        if (!clearExisting && IsSameAggregateDraftTarget(previousSummary, dynamicFormTemplateId, targetBlockId))
            ClearAggregateDraftTargetIndexes(targetValues, previousSummary!.TargetIndexes);

        ApplyAggregateRowsToValues(targetValues, aggregate.Rows, block, valueSelector);

        var tableValuesJson = BuildAggregateDraftTableValuesJson(entity, form, block, targetValues, aggregate);
        var topLevelValues = isTopLevelBlock
            ? targetValues
            : NormalizeDecimalValues(existingTopLevelValues, entity.W * entity.H);
        var contributionMode = string.IsNullOrWhiteSpace(req.CumulativeContributionMode)
            ? WorkReportDataOrigin.DefaultContributionMode(dataOrigin)
            : WorkReportCumulativeContributionMode.Normalize(req.CumulativeContributionMode);
        var contributionPolicyJson = NormalizeOptionalTextOrNull(req.CumulativeContributionPolicyJson)
            ?? BuildAggregateDraftContributionPolicyJson(dataOrigin, aggregate.Rows, block.BlockId);
        var summarySourceJson = BuildAggregateDraftSummarySourceJson(
            dataOrigin,
            aggregateReq,
            aggregate,
            block,
            valueSelector,
            targetBlockId,
            clearExisting);

        return new DynamicFormAggregateDraftProjection(
            topLevelValues,
            tableValuesJson,
            dataOrigin,
            contributionMode,
            contributionPolicyJson,
            summarySourceJson);
    }

    public async Task RefreshDynamicFormAggregateDependentsAsync(
        string sourceReportId,
        string currentUserId,
        CancellationToken ct = default)
    {
        EnsureActor(currentUserId);
        if (string.IsNullOrWhiteSpace(sourceReportId))
            throw ReportIdRequired(sourceReportId);

        var source = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == sourceReportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (source is null)
            throw ReportNotFound(sourceReportId);

        await RefreshDynamicFormAggregateDependentsRecursiveAsync(
            source,
            currentUserId,
            new HashSet<string>(StringComparer.Ordinal),
            ct);
    }

    public async Task<WorkAssignmentReportResponse> SubmitAsync(
        string id,
        SubmitWorkAssignmentReportRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(id))
            throw ReportIdRequired(id);

        req ??= new SubmitWorkAssignmentReportRequest();

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw ReportNotFound(id);
        EnsureReportIsActive(entity);

        var reportAccess = await EnsureReportAccessAsync(entity, actorUserId, ct);
        if (!reportAccess.isAssignee || entity.AssigneeUserId != actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_SUBMIT_FORBIDDEN,
                ReportDetails(entity, actorUserId));

        if (entity.Status != WorkAssignmentReportStatus.Draft)
            throw InvalidReportStatus(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_SUBMIT_STATUS_INVALID,
                entity,
                WorkAssignmentReportStatus.Draft,
                actorUserId);

        var now = DateTime.UtcNow;
        var fromStatus = entity.Status;
        var previousSourceWindow = WorkAssignmentReportTemporalPolicy.ResolveSourceWindow(entity);
        var previousPeriodKey = entity.PeriodKey;
        var previousStatus = entity.Status;

        string? requestedValues1DJson = null;
        if (req.Values1D is { Count: > 0 })
        {
            var expectedLength = entity.W * entity.H;
            if (req.Values1D.Count != expectedLength)
                throw InvalidReportValues(entity, expectedLength, req.Values1D.Count, actorUserId);

            requestedValues1DJson = JsonSerializer.Serialize(req.Values1D, _jsonOptions);
        }

        var nextDataOrigin = ResolveReportDataOrigin(req.DataOrigin, entity.DataOrigin);
        var nextContributionMode = ResolveCumulativeContributionMode(
            req.CumulativeContributionMode,
            req.DataOrigin,
            entity.CumulativeContributionMode,
            entity.DataOrigin);
        var nextContributionPolicyJson = ResolveContributionPolicyJsonOverride(
            req.CumulativeContributionPolicyJson,
            entity.CumulativeContributionPolicyJson,
            id,
            actorUserId);
        var nextSummarySourceJson = ResolveSummarySourceJsonOverride(
            req.SummarySourceJson,
            entity.SummarySourceJson,
            id,
            actorUserId);
        var nextAggregateSources = ExtractAggregateSourceSnapshot(nextSummarySourceJson);
        var acceptsReportDataPayload = ShouldAcceptReportDataPayload(entity, nextDataOrigin, nextSummarySourceJson);

        await ValidateRuntimeRowLabelsAsync(
            entity,
            acceptsReportDataPayload ? req.TableValuesJson ?? entity.TableValuesJson : entity.TableValuesJson,
            ct);
        await ValidateRuntimeRecordTableAsync(
            entity,
            acceptsReportDataPayload ? req.TableValuesJson ?? entity.TableValuesJson : entity.TableValuesJson,
            ct);

        if (acceptsReportDataPayload)
        {
            if (requestedValues1DJson is not null)
                entity.Values1DJson = requestedValues1DJson;

            if (req.FieldValuesJson is not null)
                entity.FieldValuesJson = req.FieldValuesJson;

            if (req.TableValuesJson is not null)
                entity.TableValuesJson = req.TableValuesJson;
        }

        var nextAggregateSnapshotDirty = nextAggregateSources.IsAggregate
            ? !acceptsReportDataPayload && entity.AggregateSnapshotDirty
            : false;
        var nextAggregateSnapshotDirtyAtUtc = nextAggregateSnapshotDirty
            ? entity.AggregateSnapshotDirtyAtUtc
            : null;
        var nextAggregateSnapshotRefreshedAtUtc = nextAggregateSources.IsAggregate && acceptsReportDataPayload
            ? now
            : entity.AggregateSnapshotRefreshedAtUtc;
        var nextAggregateSourceUpdatedAtUtc = nextAggregateSources.IsAggregate
            ? (acceptsReportDataPayload ? now : entity.AggregateSourceUpdatedAtUtc)
            : null;

        entity.DataOrigin = nextDataOrigin;
        entity.CumulativeContributionMode = nextContributionMode;
        entity.CumulativeContributionPolicyJson = nextContributionPolicyJson;
        entity.SummarySourceJson = nextSummarySourceJson;
        entity.AggregateSourceReportIds = nextAggregateSources.ReportIds;
        entity.AggregateSourceAssignmentIds = nextAggregateSources.AssignmentIds;
        entity.AggregateSourceUpdatedAtUtc = nextAggregateSourceUpdatedAtUtc;
        entity.AggregateSnapshotDirty = nextAggregateSnapshotDirty;
        entity.AggregateSnapshotDirtyAtUtc = nextAggregateSnapshotDirtyAtUtc;
        entity.AggregateSnapshotRefreshedAtUtc = nextAggregateSnapshotRefreshedAtUtc;
        entity.AggregateRefreshError = null;

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        var isLate = entity.DueAtUtc.HasValue && now > entity.DueAtUtc.Value;
        var isHistoricalData = IsHistoricalReportData(entity, period, now);
        var startedDate = NormalizeDate(req.StartedDate) ?? entity.StartedDate ?? entity.PeriodStart ?? entity.ReportDate;
        var completedDate = NormalizeDate(req.CompletedDate) ?? entity.CompletedDate;
        if (isHistoricalData && !completedDate.HasValue)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_HISTORICAL_COMPLETED_DATE_REQUIRED,
                ReportDetails(entity, actorUserId));

        completedDate ??= now.Date;
        EnsureReportDateRange(startedDate, completedDate, "StartedDate", "CompletedDate");
        var lateReason = string.IsNullOrWhiteSpace(req.LateReason) ? entity.LateReason : req.LateReason?.Trim();

        if (isLate && string.IsNullOrWhiteSpace(lateReason))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_LATE_REASON_REQUIRED,
                ReportDetails(entity, actorUserId));

        entity.Status = WorkAssignmentReportStatus.Submitted;
        entity.CurrentProgressStatus = req.CurrentProgressStatus ?? entity.CurrentProgressStatus;
        entity.ReportReason = req.ReportReason ?? entity.ReportReason;
        entity.Difficulties = req.Difficulties ?? entity.Difficulties;
        entity.ProposedSolution = req.ProposedSolution ?? entity.ProposedSolution;
        entity.StartedDate = startedDate;
        entity.CompletedDate = completedDate;
        entity.IsHistoricalData = isHistoricalData;
        entity.IsLateSubmission = isLate;
        entity.LateReason = lateReason;
        entity.SubmittedAtUtc = now;
        entity.SubmittedByUserId = actorUserId;
        entity.ReturnedAtUtc = null;
        entity.ReturnedByUserId = null;
        entity.CreatedByUserId = string.IsNullOrWhiteSpace(entity.CreatedByUserId) ? actorUserId : entity.CreatedByUserId;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = actorUserId;

        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.Values1DJson, entity.Values1DJson)
                .Set(x => x.FieldValuesJson, entity.FieldValuesJson)
                .Set(x => x.TableValuesJson, entity.TableValuesJson)
                .Set(x => x.DataOrigin, entity.DataOrigin)
                .Set(x => x.CumulativeContributionMode, entity.CumulativeContributionMode)
                .Set(x => x.CumulativeContributionPolicyJson, entity.CumulativeContributionPolicyJson)
                .Set(x => x.SummarySourceJson, entity.SummarySourceJson)
                .Set(x => x.AggregateSourceReportIds, entity.AggregateSourceReportIds)
                .Set(x => x.AggregateSourceAssignmentIds, entity.AggregateSourceAssignmentIds)
                .Set(x => x.AggregateSourceUpdatedAtUtc, entity.AggregateSourceUpdatedAtUtc)
                .Set(x => x.AggregateSnapshotDirty, entity.AggregateSnapshotDirty)
                .Set(x => x.AggregateSnapshotDirtyAtUtc, entity.AggregateSnapshotDirtyAtUtc)
                .Set(x => x.AggregateSnapshotRefreshedAtUtc, entity.AggregateSnapshotRefreshedAtUtc)
                .Set(x => x.AggregateRefreshError, entity.AggregateRefreshError)
                .Set(x => x.Status, entity.Status)
                .Set(x => x.CurrentProgressStatus, entity.CurrentProgressStatus)
                .Set(x => x.ReportReason, entity.ReportReason)
                .Set(x => x.Difficulties, entity.Difficulties)
                .Set(x => x.ProposedSolution, entity.ProposedSolution)
                .Set(x => x.StartedDate, entity.StartedDate)
                .Set(x => x.CompletedDate, entity.CompletedDate)
                .Set(x => x.IsHistoricalData, entity.IsHistoricalData)
                .Set(x => x.IsLateSubmission, entity.IsLateSubmission)
                .Set(x => x.LateReason, entity.LateReason)
                .Set(x => x.SubmittedAtUtc, entity.SubmittedAtUtc)
                .Set(x => x.SubmittedByUserId, entity.SubmittedByUserId)
                .Set(x => x.ReturnedAtUtc, (DateTime?)null)
                .Set(x => x.ReturnedByUserId, (string?)null)
                .Set(x => x.CreatedByUserId, string.IsNullOrWhiteSpace(entity.CreatedByUserId) ? actorUserId : entity.CreatedByUserId)
                .Set(x => x.UpdatedAtUtc, entity.UpdatedAtUtc)
                .Set(x => x.UpdatedByUserId, entity.UpdatedByUserId),
            cancellationToken: ct);

        if (period is not null)
        {
            var periodStatus = WorkReportPeriodStatusHelper.ResolveSubmittedStatus(period.DueAtUtc, now);

            await _ctx.WorkReportPeriods.UpdateOneAsync(
                x => x.Id == period.Id && !x.IsDeleted,
                Builders<WorkReportPeriod>.Update
                    .Set(x => x.Status, periodStatus)
                    .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(periodStatus))
                    .Set(x => x.LastSubmittedAtUtc, now)
                    .Set(x => x.CurrentReportId, entity.Id)
                    .Set(x => x.CurrentProgressStatus, entity.CurrentProgressStatus)
                    .Set(x => x.ReportReason, entity.ReportReason)
                    .Set(x => x.Difficulties, entity.Difficulties)
                    .Set(x => x.ProposedSolution, entity.ProposedSolution)
                    .Set(x => x.StartedDate, entity.StartedDate)
                    .Set(x => x.CompletedDate, entity.CompletedDate)
                    .Set(x => x.IsHistoricalData, entity.IsHistoricalData)
                    .Set(x => x.LateReason, entity.LateReason)
                    .Set(x => x.RequiresLateReason, isLate)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId),
                cancellationToken: ct);

            period.Status = periodStatus;
            period.IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(periodStatus);
            period.LastSubmittedAtUtc = now;
            period.CurrentReportId = entity.Id;
            period.CurrentProgressStatus = entity.CurrentProgressStatus;
            period.ReportReason = entity.ReportReason;
            period.Difficulties = entity.Difficulties;
            period.ProposedSolution = entity.ProposedSolution;
            period.StartedDate = entity.StartedDate;
            period.CompletedDate = entity.CompletedDate;
            period.IsHistoricalData = entity.IsHistoricalData;
            period.LateReason = entity.LateReason;
            period.RequiresLateReason = isLate;
            period.UpdatedAtUtc = now;
            period.UpdatedByUserId = actorUserId;
        }

        await InsertLogAsync(
            workId: entity.WorkId,
            workAssignmentId: entity.WorkAssignmentId,
            workReportPeriodId: entity.WorkReportPeriodId,
            workAssignmentReportId: entity.Id,
            action: "SUBMIT",
            fromStatus: fromStatus.ToString(),
            toStatus: WorkAssignmentReportStatus.Submitted.ToString(),
            actionByUserId: actorUserId,
            reason: entity.ReportReason,
            comment: lateReason,
            snapshotJson: null,
            ct: ct);

        if (period is not null)
        {
            await FinalizeReportStatusOperationAsync(
                "SUBMIT",
                entity,
                period,
                fromStatus.ToString(),
                WorkAssignmentReportStatus.Submitted.ToString(),
                actorUserId,
                upsertQueue: true,
                disableQueue: false,
                rebuildProjection: true,
                syncAssignment: true,
                ct);
        }

        if (HasSourceWindowChanged(previousSourceWindow, previousPeriodKey, entity))
        {
            await RefreshDynamicFormAggregateDependentsForSourceWindowChangeAsync(
                entity,
                previousSourceWindow,
                previousPeriodKey,
                previousStatus,
                actorUserId,
                ct);
        }

        await _userActionLog.RecordAsync(new UserActionLogSeed
        {
            Action = UserActionLogActions.ReportSubmitted,
            Scope = "report",
            ActorUserId = actorUserId,
            WorkId = entity.WorkId,
            WorkAssignmentId = entity.WorkAssignmentId,
            WorkReportPeriodId = entity.WorkReportPeriodId,
            WorkAssignmentReportId = entity.Id,
            TargetUserId = entity.AssigneeUserId,
            Summary = $"Submitted report {entity.PeriodInstanceKey}",
            Data = new Dictionary<string, string>
            {
                { "fromStatus", fromStatus.ToString() },
                { "toStatus", WorkAssignmentReportStatus.Submitted.ToString() },
                { "isLateSubmission", isLate.ToString() },
                { "isHistoricalData", isHistoricalData.ToString() }
            },
            OccurredAtUtc = now
        }, CancellationToken.None);

        return await MapToResponseAsync(entity, period, ct);
    }

    private static Task<WorkAssignment> LoadReviewNodeAsync(
        WorkAssignment assignment,
        string reviewerUserId,
        CancellationToken ct)
    {
        WorkAssignmentReviewPermissionHelper.EnsureCanReviewOnNode(assignment, reviewerUserId);
        return Task.FromResult(assignment);
    }

    public async Task<List<WorkAssignmentReportLogRow>> GetLogsAsync(
    string reportId,
    string actorUserId,
    CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(reportId))
            throw ReportIdRequired(reportId);

        var report = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (report is null)
            throw ReportNotFound(reportId);

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportAssignmentNotFound(report.WorkAssignmentId);

        var isAssignee = report.AssigneeUserId == actorUserId;
        var canReview = false;

        try
        {
            await LoadReviewNodeAsync(assignment, actorUserId, ct);
            canReview = true;
        }
        catch
        {
            canReview = false;
        }

        if (!canReview)
            canReview = await HasReviewReportReadAccessAsync(report, actorUserId, ct);

        if (!isAssignee && !canReview)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_LOG_ACCESS_FORBIDDEN,
                ReportDetails(report, actorUserId));

        var logs = await _ctx.WorkAssignmentReportLogs
            .Find(x => x.WorkAssignmentReportId == reportId && !x.IsDeleted)
            .SortByDescending(x => x.ActionAtUtc)
            .ToListAsync(ct);

        return logs.Select(x => new WorkAssignmentReportLogRow
        {
            Id = x.Id,
            WorkId = x.WorkId,
            WorkAssignmentId = x.WorkAssignmentId,
            WorkReportPeriodId = x.WorkReportPeriodId,
            WorkAssignmentReportId = x.WorkAssignmentReportId,
            Action = x.Action,
            FromStatus = x.FromStatus,
            ToStatus = x.ToStatus,
            ActionByUserId = x.ActionByUserId,
            ActionAtUtc = x.ActionAtUtc,
            Reason = x.Reason,
            Comment = x.Comment,
            SnapshotJson = x.SnapshotJson
        }).ToList();
    }

    public async Task<WorkAssignmentReportResponse> AcceptAsync(
    string id,
    AcceptWorkAssignmentReportRequest req,
    string actorUserId,
    CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(id))
            throw ReportIdRequired(id);

        req ??= new AcceptWorkAssignmentReportRequest();

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw ReportNotFound(id);
        EnsureReportIsActive(entity);

        if (entity.AssigneeUserId == actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_REVIEW_SELF_FORBIDDEN,
                ReportDetails(entity, actorUserId));

        if (entity.Status != WorkAssignmentReportStatus.Submitted)
            throw InvalidReportStatus(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_APPROVE_STATUS_INVALID,
                entity,
                WorkAssignmentReportStatus.Submitted,
                actorUserId);

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == entity.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportAssignmentNotFound(entity.WorkAssignmentId);

        await LoadReviewNodeAsync(assignment, actorUserId, ct);

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        await EnsurePreviousReportsApprovedAsync(period, ct);

        var now = DateTime.UtcNow;
        var lateReason = string.IsNullOrWhiteSpace(req.LateReasonOverride)
            ? entity.LateReason
            : req.LateReasonOverride.Trim();
        var isHistoricalData = IsHistoricalReportData(entity, period, now);
        if (isHistoricalData && !req.ConfirmHistoricalDataApproval)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_HISTORICAL_APPROVAL_CONFIRMATION_REQUIRED,
                ReportDetails(entity, actorUserId));

        var historicalDataApproved = isHistoricalData || entity.HistoricalDataApproved;
        var historicalDataApprovedAtUtc = historicalDataApproved
            ? entity.HistoricalDataApprovedAtUtc ?? now
            : (DateTime?)null;
        var historicalDataApprovedByUserId = historicalDataApproved
            ? entity.HistoricalDataApprovedByUserId ?? actorUserId
            : null;

        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.Status, WorkAssignmentReportStatus.Approved)
                .Set(x => x.IsHistoricalData, isHistoricalData)
                .Set(x => x.HistoricalDataApproved, historicalDataApproved)
                .Set(x => x.HistoricalDataApprovedAtUtc, historicalDataApprovedAtUtc)
                .Set(x => x.HistoricalDataApprovedByUserId, historicalDataApprovedByUserId)
                .Set(x => x.ReviewerComment, req.ReviewerComment)
                .Set(x => x.LateReason, lateReason)
                .Set(x => x.ReturnedAtUtc, (DateTime?)null)
                .Set(x => x.ReturnedByUserId, (string?)null)
                .Set(x => x.ApprovedAtUtc, now)
                .Set(x => x.ApprovedByUserId, actorUserId)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        entity.Status = WorkAssignmentReportStatus.Approved;
        entity.IsHistoricalData = isHistoricalData;
        entity.HistoricalDataApproved = historicalDataApproved;
        entity.HistoricalDataApprovedAtUtc = historicalDataApprovedAtUtc;
        entity.HistoricalDataApprovedByUserId = historicalDataApprovedByUserId;
        entity.ReviewerComment = req.ReviewerComment;
        entity.LateReason = lateReason;
        entity.ReturnedAtUtc = null;
        entity.ReturnedByUserId = null;
        entity.ApprovedAtUtc = now;
        entity.ApprovedByUserId = actorUserId;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = actorUserId;

        if (period is not null)
        {
            var nextPeriodStatus = ResolveApprovedPeriodStatus(period, entity, now);

            await _ctx.WorkReportPeriods.UpdateOneAsync(
                x => x.Id == period.Id && !x.IsDeleted,
                Builders<WorkReportPeriod>.Update
                    .Set(x => x.Status, nextPeriodStatus)
                    .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus))
                    .Set(x => x.IsHistoricalData, isHistoricalData)
                    .Set(x => x.HistoricalDataApproved, historicalDataApproved)
                    .Set(x => x.HistoricalDataApprovedAtUtc, historicalDataApprovedAtUtc)
                    .Set(x => x.HistoricalDataApprovedByUserId, historicalDataApprovedByUserId)
                    .Set(x => x.LastReviewedAtUtc, now)
                    .Set(x => x.ReviewerComment, req.ReviewerComment)
                    .Set(x => x.AcceptedLateReason, lateReason)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId),
                cancellationToken: ct);

            period.Status = nextPeriodStatus;
            period.IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus);
            period.IsHistoricalData = isHistoricalData;
            period.HistoricalDataApproved = historicalDataApproved;
            period.HistoricalDataApprovedAtUtc = historicalDataApprovedAtUtc;
            period.HistoricalDataApprovedByUserId = historicalDataApprovedByUserId;
            period.LastReviewedAtUtc = now;
            period.ReviewerComment = req.ReviewerComment;
            period.AcceptedLateReason = lateReason;
            period.UpdatedAtUtc = now;
            period.UpdatedByUserId = actorUserId;
        }

        await InsertLogAsync(
            workId: entity.WorkId,
            workAssignmentId: entity.WorkAssignmentId,
            workReportPeriodId: entity.WorkReportPeriodId,
            workAssignmentReportId: entity.Id,
            action: "APPROVE",
            fromStatus: WorkAssignmentReportStatus.Submitted.ToString(),
            toStatus: WorkAssignmentReportStatus.Approved.ToString(),
            actionByUserId: actorUserId,
            reason: null,
            comment: req.ReviewerComment,
            snapshotJson: null,
            ct: ct);

        await FinalizeReportStatusOperationAsync(
            "APPROVE",
            entity,
            period,
            WorkAssignmentReportStatus.Submitted.ToString(),
            WorkAssignmentReportStatus.Approved.ToString(),
            actorUserId,
            upsertQueue: false,
            disableQueue: period is not null,
            rebuildProjection: period is not null,
            syncAssignment: true,
            ct);

        await _userActionLog.RecordAsync(new UserActionLogSeed
        {
            Action = UserActionLogActions.ReportApproved,
            Scope = "report",
            ActorUserId = actorUserId,
            WorkId = entity.WorkId,
            WorkAssignmentId = entity.WorkAssignmentId,
            WorkReportPeriodId = entity.WorkReportPeriodId,
            WorkAssignmentReportId = entity.Id,
            TargetUserId = entity.AssigneeUserId,
            Summary = $"Approved report {entity.PeriodInstanceKey}",
            Data = new Dictionary<string, string>
            {
                { "fromStatus", WorkAssignmentReportStatus.Submitted.ToString() },
                { "toStatus", WorkAssignmentReportStatus.Approved.ToString() },
                { "isHistoricalData", isHistoricalData.ToString() },
                { "historicalDataApproved", historicalDataApproved.ToString() }
            },
            OccurredAtUtc = now
        }, CancellationToken.None);

        return await MapToResponseAsync(entity, period, ct);
    }

    public async Task<WorkAssignmentReportResponse> ReturnAsync(
    string id,
    ReturnWorkAssignmentReportRequest req,
    string actorUserId,
    CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(id))
            throw ReportIdRequired(id);

        req ??= new ReturnWorkAssignmentReportRequest();

        if (string.IsNullOrWhiteSpace(req.ReturnReason))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_RETURN_COMMENT_REQUIRED,
                new { reportId = id, req.ReturnReason });

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw ReportNotFound(id);
        EnsureReportIsActive(entity);

        if (entity.AssigneeUserId == actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_REVIEW_SELF_FORBIDDEN,
                ReportDetails(entity, actorUserId));

        if (entity.Status != WorkAssignmentReportStatus.Submitted)
            throw InvalidReportStatus(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_RETURN_STATUS_INVALID,
                entity,
                WorkAssignmentReportStatus.Submitted,
                actorUserId);

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == entity.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportAssignmentNotFound(entity.WorkAssignmentId);

        await LoadReviewNodeAsync(assignment, actorUserId, ct);

        var now = DateTime.UtcNow;
        var returnReason = req.ReturnReason.Trim();

        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.Status, WorkAssignmentReportStatus.Draft)
                .Set(x => x.ReturnReason, returnReason)
                .Set(x => x.ReviewerComment, req.ReviewerComment)
                .Set(x => x.ReturnedAtUtc, now)
                .Set(x => x.ReturnedByUserId, actorUserId)
                .Set(x => x.ApprovedAtUtc, (DateTime?)null)
                .Set(x => x.ApprovedByUserId, (string?)null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        entity.Status = WorkAssignmentReportStatus.Draft;
        entity.ReturnReason = returnReason;
        entity.ReviewerComment = req.ReviewerComment;
        entity.ReturnedAtUtc = now;
        entity.ReturnedByUserId = actorUserId;
        entity.ApprovedAtUtc = null;
        entity.ApprovedByUserId = null;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = actorUserId;

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (period is not null)
        {
            var nextPeriodStatus = WorkReportPeriodStatusHelper.ResolveDraftStatus(period.DueAtUtc, now);

            await _ctx.WorkReportPeriods.UpdateOneAsync(
                x => x.Id == period.Id && !x.IsDeleted,
                Builders<WorkReportPeriod>.Update
                    .Set(x => x.Status, nextPeriodStatus)
                    .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus))
                    .Set(x => x.LastReviewedAtUtc, now)
                    .Set(x => x.ReturnReason, returnReason)
                    .Set(x => x.ReviewerComment, req.ReviewerComment)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId),
                cancellationToken: ct);

            period.Status = nextPeriodStatus;
            period.IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus);
            period.LastReviewedAtUtc = now;
            period.ReturnReason = returnReason;
            period.ReviewerComment = req.ReviewerComment;
            period.UpdatedAtUtc = now;
            period.UpdatedByUserId = actorUserId;
        }

        await InsertLogAsync(
            workId: entity.WorkId,
            workAssignmentId: entity.WorkAssignmentId,
            workReportPeriodId: entity.WorkReportPeriodId,
            workAssignmentReportId: entity.Id,
            action: "RETURN",
            fromStatus: WorkAssignmentReportStatus.Submitted.ToString(),
            toStatus: WorkAssignmentReportStatus.Draft.ToString(),
            actionByUserId: actorUserId,
            reason: returnReason,
            comment: req.ReviewerComment,
            snapshotJson: null,
            ct: ct);

        await FinalizeReportStatusOperationAsync(
            "RETURN",
            entity,
            period,
            WorkAssignmentReportStatus.Submitted.ToString(),
            WorkAssignmentReportStatus.Draft.ToString(),
            actorUserId,
            upsertQueue: period is not null,
            disableQueue: false,
            rebuildProjection: period is not null,
            syncAssignment: true,
            ct);

        await _userActionLog.RecordAsync(new UserActionLogSeed
        {
            Action = UserActionLogActions.ReportReturned,
            Scope = "report",
            ActorUserId = actorUserId,
            WorkId = entity.WorkId,
            WorkAssignmentId = entity.WorkAssignmentId,
            WorkReportPeriodId = entity.WorkReportPeriodId,
            WorkAssignmentReportId = entity.Id,
            TargetUserId = entity.AssigneeUserId,
            Summary = $"Returned report {entity.PeriodInstanceKey}",
            Data = new Dictionary<string, string>
            {
                { "fromStatus", WorkAssignmentReportStatus.Submitted.ToString() },
                { "toStatus", WorkAssignmentReportStatus.Draft.ToString() }
            },
            OccurredAtUtc = now
        }, CancellationToken.None);

        return await MapToResponseAsync(entity, period, ct);
    }

    public async Task<WorkAssignmentReportResponse> WithdrawSubmittedAsync(
    string id,
    ReturnWorkAssignmentReportRequest req,
    string actorUserId,
    CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(id))
            throw ReportIdRequired(id);

        req ??= new ReturnWorkAssignmentReportRequest();

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportNotFound(id);
        EnsureReportIsActive(entity);

        if (entity.AssigneeUserId != actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_WITHDRAW_FORBIDDEN,
                ReportDetails(entity, actorUserId));

        if (entity.Status != WorkAssignmentReportStatus.Submitted)
            throw InvalidReportStatus(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_WITHDRAW_STATUS_INVALID,
                entity,
                WorkAssignmentReportStatus.Submitted,
                actorUserId);

        var now = DateTime.UtcNow;
        var withdrawReason = string.IsNullOrWhiteSpace(req.ReturnReason)
            ? null
            : req.ReturnReason.Trim();

        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.Status, WorkAssignmentReportStatus.Draft)
                .Set(x => x.ReturnReason, withdrawReason)
                .Set(x => x.ReturnedAtUtc, (DateTime?)null)
                .Set(x => x.ReturnedByUserId, (string?)null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        entity.Status = WorkAssignmentReportStatus.Draft;
        entity.ReturnReason = withdrawReason;
        entity.ReturnedAtUtc = null;
        entity.ReturnedByUserId = null;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = actorUserId;

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (period is not null)
        {
            var nextPeriodStatus = WorkReportPeriodStatusHelper.ResolveDraftStatus(period.DueAtUtc, now);

            await _ctx.WorkReportPeriods.UpdateOneAsync(
                x => x.Id == period.Id && !x.IsDeleted,
                Builders<WorkReportPeriod>.Update
                    .Set(x => x.Status, nextPeriodStatus)
                    .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus))
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId),
                cancellationToken: ct);

            period.Status = nextPeriodStatus;
            period.IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus);
            period.UpdatedAtUtc = now;
            period.UpdatedByUserId = actorUserId;

        }

        await InsertLogAsync(
            workId: entity.WorkId,
            workAssignmentId: entity.WorkAssignmentId,
            workReportPeriodId: entity.WorkReportPeriodId,
            workAssignmentReportId: entity.Id,
            action: "Thu hồi báo cáo",
            fromStatus: WorkAssignmentReportStatus.Submitted.ToString(),
            toStatus: WorkAssignmentReportStatus.Draft.ToString(),
            actionByUserId: actorUserId,
            reason: withdrawReason,
            comment: req.ReviewerComment,
            snapshotJson: null,
            ct: ct);

        await FinalizeReportStatusOperationAsync(
            "WITHDRAW_SUBMITTED",
            entity,
            period,
            WorkAssignmentReportStatus.Submitted.ToString(),
            WorkAssignmentReportStatus.Draft.ToString(),
            actorUserId,
            upsertQueue: period is not null,
            disableQueue: false,
            rebuildProjection: period is not null,
            syncAssignment: true,
            ct);

        return await MapToResponseAsync(entity, period, ct);
    }

    // =======================
    // Internal helpers
    // =======================
    // =======================
    // Internal helpers
    // =======================

    private async Task<WorkAssignmentReport> CreateDraftForPeriodAsync(
        WorkReportPeriod period,
        string actorUserId,
        CancellationToken ct)
    {
        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == period.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (assignment is null)
            throw ReportAssignmentNotFound(period.WorkAssignmentId);

        if (!assignment.IsActive)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_ASSIGNMENT_INACTIVE,
                PeriodDetails(period, actorUserId));

        var template = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == period.DynamicExcelId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (template is null)
            throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_EXCEL_TEMPLATE_NOT_FOUND,
                new { dynamicExcelTemplateId = period.DynamicExcelId, periodId = period.Id });

        var existedCurrent = await _ctx.WorkAssignmentReports
            .Find(Builders<WorkAssignmentReport>.Filter.Eq(x => x.WorkReportPeriodId, period.Id)
                  & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsCurrent, true)
                  & Builders<WorkAssignmentReport>.Filter.Ne(x => x.IsActive, false)
                  & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false))
            .FirstOrDefaultAsync(ct);

        if (existedCurrent is not null)
        {
            await _docRoleReadModelProjection.RebuildReportPeriodAsync(period.Id, actorUserId, ct);
            return existedCurrent;
        }

        var now = DateTime.UtcNow;
        var periodInstanceKey = NormalizePeriodInstanceKey(period);
        var periodKind = NormalizePeriodKind(period.PeriodKind);
        var defaultDataOrigin = DynamicFormDataSourceRuleNormalizer.ResolveDefaultReportDataOrigin(
            assignment.DynamicFormDataSourceRulesJson);

        var entity = new WorkAssignmentReport
        {
            WorkId = period.WorkId,
            WorkAssignmentId = period.WorkAssignmentId,
            WorkReportPeriodId = period.Id,
            AssigneeUserId = period.AssigneeUserId,
            DynamicFormTemplateId = period.DynamicFormTemplateId,
            DynamicFormTemplateCode = period.DynamicFormTemplateCode,
            DynamicFormTemplateName = period.DynamicFormTemplateName,

            PeriodKey = period.PeriodKey,
            PeriodInstanceKey = periodInstanceKey,
            PeriodKind = periodKind,
            ReportTitle = period.ReportTitle,
            ReportDate = period.ReportDate,
            LinkedScheduledPeriodId = period.LinkedScheduledPeriodId,
            StartedDate = period.StartedDate ?? period.PeriodStart ?? period.ReportDate,
            CompletedDate = period.CompletedDate,
            IsHistoricalData = period.IsHistoricalData,
            HistoricalDataApproved = period.HistoricalDataApproved,
            HistoricalDataApprovedAtUtc = period.HistoricalDataApprovedAtUtc,
            HistoricalDataApprovedByUserId = period.HistoricalDataApprovedByUserId,
            PeriodStart = period.PeriodStart,
            PeriodEnd = period.PeriodEnd,
            DueAtUtc = period.DueAtUtc,

            Status = WorkAssignmentReportStatus.Draft,
            ScheduleSnapshotJson = JsonSerializer.Serialize(BuildScheduleSnapshot(assignment), _jsonOptions),

            DynamicExcelTemplateId = template.Id,
            DynamicExcelTemplateCode = template.Code,
            DynamicExcelTemplateName = template.Name,
            DynamicExcelTableKind = NormalizeDynamicExcelTableKind(template.TableKind),
            RecordTableSpecJson = template.RecordTableSpecJson,
            SpecJson = template.SpecJson,

            DataRectR0 = template.DataRectR0,
            DataRectC0 = template.DataRectC0,
            DataRectR1 = template.DataRectR1,
            DataRectC1 = template.DataRectC1,
            W = template.W,
            H = template.H,

            Values1DJson = JsonSerializer.Serialize(CreateEmptyValues1D(template.W, template.H), _jsonOptions),
            DataOrigin = defaultDataOrigin,
            CumulativeContributionMode = WorkReportDataOrigin.DefaultContributionMode(defaultDataOrigin),

            VersionNo = Math.Max(1, period.ReportVersionCount + 1),
            IsCurrent = true,
            IsActive = true,

            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };

        await _ctx.WorkAssignmentReports.InsertOneAsync(entity, cancellationToken: ct);

        var nextPeriodStatus = ResolveDraftPeriodStatus(period.DueAtUtc, now);

        await _ctx.WorkReportPeriods.UpdateOneAsync(
            x => x.Id == period.Id && !x.IsDeleted,
            Builders<WorkReportPeriod>.Update
                .Set(x => x.CurrentReportId, entity.Id)
                .Set(x => x.PeriodInstanceKey, periodInstanceKey)
                .Set(x => x.PeriodKind, periodKind)
                .Set(x => x.ReportVersionCount, entity.VersionNo)
                .Set(x => x.Status, nextPeriodStatus)
                .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus))
                .Set(x => x.LastDraftSavedAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        period.CurrentReportId = entity.Id;
        period.PeriodInstanceKey = periodInstanceKey;
        period.PeriodKind = periodKind;
        period.ReportVersionCount = entity.VersionNo;
        period.Status = nextPeriodStatus;
        period.IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus);
        period.LastDraftSavedAtUtc = now;
        period.UpdatedAtUtc = now;
        period.UpdatedByUserId = actorUserId;

        await InsertLogAsync(
            workId: entity.WorkId,
            workAssignmentId: entity.WorkAssignmentId,
            workReportPeriodId: entity.WorkReportPeriodId,
            workAssignmentReportId: entity.Id,
            action: "INIT_DRAFT",
            fromStatus: period.Status.ToString(),
            toStatus: WorkAssignmentReportStatus.Draft.ToString(),
            actionByUserId: actorUserId,
            reason: null,
            comment: null,
            snapshotJson: null,
            ct: ct);

        return entity;
    }

    private async Task ValidateRuntimeRowLabelsAsync(
        WorkAssignmentReport report,
        string? tableValuesJson,
        CancellationToken ct)
    {
        var rows = ExtractRuntimeRowLabelPayloads(report, tableValuesJson);
        if (rows.Count == 0)
            return;

        var labelCodes = rows
            .SelectMany(x => x.LabelCodes)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var activeCodes = await _ctx.Labels
            .Find(x => labelCodes.Contains(x.Code) && x.IsActive && !x.IsDeleted)
            .Project(x => x.Code)
            .ToListAsync(ct);

        var missingCodes = labelCodes
            .Except(activeCodes, StringComparer.Ordinal)
            .ToList();

        if (missingCodes.Count > 0)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_LABEL_NOT_FOUND_OR_INACTIVE,
                new
                {
                    reportId = report.Id,
                    workAssignmentId = report.WorkAssignmentId,
                    dynamicFormTemplateId = report.DynamicFormTemplateId,
                    labelCodes = missingCodes.Take(20).ToArray()
                });

        if (string.IsNullOrWhiteSpace(report.DynamicFormTemplateId))
            return;

        var form = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == report.DynamicFormTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        var allowedByBlock = BuildAllowedRowLabelsByBlock(form, report);
        if (allowedByBlock.Count == 0)
            return;

        foreach (var row in rows)
        {
            if (!allowedByBlock.TryGetValue(row.BlockId, out var allowed) || allowed.Count == 0)
                continue;

            var disallowed = row.LabelCodes
                .Where(code => !allowed.Contains(code))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (disallowed.Count > 0)
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.WORK_ASSIGNMENT_REPORT_LABEL_NOT_ALLOWED,
                    new
                    {
                        reportId = report.Id,
                        workAssignmentId = report.WorkAssignmentId,
                        dynamicFormTemplateId = report.DynamicFormTemplateId,
                        blockId = row.BlockId,
                        labelCodes = disallowed.Take(20).ToArray()
                    });
        }
    }

    private async Task ValidateRuntimeRecordTableAsync(
        WorkAssignmentReport report,
        string? tableValuesJson,
        CancellationToken ct)
    {
        if (!string.Equals(
                NormalizeDynamicExcelTableKind(report.DynamicExcelTableKind),
                DynamicExcelTableKind.RecordTable,
                StringComparison.Ordinal))
        {
            return;
        }

        var specJson = report.RecordTableSpecJson;
        if (string.IsNullOrWhiteSpace(specJson))
        {
            var template = await _ctx.DynamicExcelTemplates
                .Find(x => x.Id == report.DynamicExcelTemplateId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            specJson = template?.RecordTableSpecJson;
        }

        DynamicExcelRecordTableRuntime.ValidateTableValues(
            tableValuesJson,
            specJson,
            report.DynamicExcelTemplateId,
            report.Id);
    }

    private static List<RuntimeRowLabelPayload> ExtractRuntimeRowLabelPayloads(
        WorkAssignmentReport report,
        string? tableValuesJson)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson))
            return new List<RuntimeRowLabelPayload>();

        try
        {
            using var document = JsonDocument.Parse(tableValuesJson);
            if (!TryGetJsonProperty(document.RootElement, "blocks", out var blocks) ||
                blocks.ValueKind != JsonValueKind.Array)
            {
                return new List<RuntimeRowLabelPayload>();
            }

            var result = new List<RuntimeRowLabelPayload>();
            foreach (var block in blocks.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object)
                    continue;

                var blockId = NormalizeBlockId(ReadJsonString(block, "blockId"));
                if (!TryGetJsonProperty(block, "rowLabels", out var rowLabels) ||
                    rowLabels.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var row in rowLabels.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object)
                        continue;

                    var codes = ReadJsonStringArray(row, "rowLabelCodes")
                        .Select(code => NormalizeRuntimeLabelCode(code, report, blockId))
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    if (codes.Count > 0)
                        result.Add(new RuntimeRowLabelPayload(blockId, codes));
                }
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_TABLE_VALUES_JSON_INVALID,
                new
                {
                    reportId = report.Id,
                    workAssignmentId = report.WorkAssignmentId,
                    dynamicFormTemplateId = report.DynamicFormTemplateId,
                    error = ex.Message
                });
        }
    }

    private static Dictionary<string, HashSet<string>> BuildAllowedRowLabelsByBlock(
        DynamicFormTemplate? form,
        WorkAssignmentReport report)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (form is null)
            return result;

        AddAllowedRowLabelsFromJson(form.BlocksJson, result, report);
        AddAllowedRowLabelsFromJson(form.ExcelBlockJson, result, report);
        return result;
    }

    private static void AddAllowedRowLabelsFromJson(
        string? json,
        Dictionary<string, HashSet<string>> target,
        WorkAssignmentReport report)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in document.RootElement.EnumerateArray())
                    AddAllowedRowLabelsFromBlock(block, target, report);
            }
            else
            {
                AddAllowedRowLabelsFromBlock(document.RootElement, target, report);
            }
        }
        catch (JsonException)
        {
            return;
        }
    }

    private static void AddAllowedRowLabelsFromBlock(
        JsonElement block,
        Dictionary<string, HashSet<string>> target,
        WorkAssignmentReport report)
    {
        if (block.ValueKind != JsonValueKind.Object)
            return;

        var blockId = NormalizeBlockId(ReadJsonString(block, "blockId") ?? ReadJsonString(block, "id"));
        var codes = ReadJsonStringArray(block, "allowedRowLabelCodes")
            .Select(code => NormalizeRuntimeLabelCode(code, report, blockId))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (codes.Count == 0)
            return;

        if (!target.TryGetValue(blockId, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            target[blockId] = set;
        }

        foreach (var code in codes)
            set.Add(code);
    }

    private static bool TryGetJsonProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? ReadJsonString(JsonElement element, string name)
        => TryGetJsonProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<string> ReadJsonStringArray(JsonElement element, string name)
    {
        if (!TryGetJsonProperty(element, name, out var value) || value.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .ToList();
    }

    private static string NormalizeBlockId(string? value)
        => string.IsNullOrWhiteSpace(value) ? "excel_block" : value.Trim();

    private static string NormalizeRuntimeLabelCode(
        string? value,
        WorkAssignmentReport report,
        string blockId)
    {
        var code = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(code))
            return string.Empty;

        if (!LabelCodeRegex.IsMatch(code))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_LABEL_CODE_INVALID,
                new
                {
                    reportId = report.Id,
                    workAssignmentId = report.WorkAssignmentId,
                    dynamicFormTemplateId = report.DynamicFormTemplateId,
                    blockId,
                    labelCode = code
                });

        return code;
    }

    private async Task<WorkAssignmentReportResponse> MapToResponseAsync(
        WorkAssignmentReport x,
        WorkReportPeriod? period,
        CancellationToken ct)
    {
        var template = await _ctx.DynamicExcelTemplates
            .Find(t => t.Id == x.DynamicExcelTemplateId && !t.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (template is null)
            throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_DYNAMIC_EXCEL_TEMPLATE_NOT_FOUND,
                new
                {
                    reportId = x.Id,
                    workAssignmentId = x.WorkAssignmentId,
                    dynamicExcelTemplateId = x.DynamicExcelTemplateId,
                    dynamicFormTemplateId = x.DynamicFormTemplateId
                });

        return new WorkAssignmentReportResponse
        {
            Id = x.Id,
            WorkId = x.WorkId,
            WorkAssignmentId = x.WorkAssignmentId,
            WorkReportPeriodId = x.WorkReportPeriodId,
            AssigneeUserId = x.AssigneeUserId,
            PeriodKey = x.PeriodKey,
            PeriodInstanceKey = NormalizeReportPeriodInstanceKey(x),
            PeriodKind = NormalizePeriodKind(x.PeriodKind),
            ReportTitle = x.ReportTitle ?? period?.ReportTitle,
            ReportDate = x.ReportDate ?? period?.ReportDate,
            LinkedScheduledPeriodId = x.LinkedScheduledPeriodId ?? period?.LinkedScheduledPeriodId,
            StartedDate = x.StartedDate ?? period?.StartedDate,
            CompletedDate = x.CompletedDate ?? period?.CompletedDate,
            IsHistoricalData = x.IsHistoricalData || period?.IsHistoricalData == true,
            HistoricalDataApproved = x.HistoricalDataApproved || period?.HistoricalDataApproved == true,
            HistoricalDataApprovedAtUtc = x.HistoricalDataApprovedAtUtc ?? period?.HistoricalDataApprovedAtUtc,
            HistoricalDataApprovedByUserId = x.HistoricalDataApprovedByUserId ?? period?.HistoricalDataApprovedByUserId,
            PeriodStart = x.PeriodStart,
            PeriodEnd = x.PeriodEnd,
            DueAtUtc = x.DueAtUtc,
            Status = x.Status,
            PeriodStatus = period?.Status,

            TemplateSnapshotJson = JsonSerializer.Serialize(BuildTemplateSnapshot(template), _jsonOptions),
            ScheduleSnapshotJson = x.ScheduleSnapshotJson,

            DynamicExcelTemplateId = x.DynamicExcelTemplateId,
            DynamicExcelTemplateCode = x.DynamicExcelTemplateCode,
            DynamicExcelTemplateName = x.DynamicExcelTemplateName,
            TableKind = NormalizeDynamicExcelTableKind(x.DynamicExcelTableKind ?? template.TableKind),
            RecordTableSpecJson = string.IsNullOrWhiteSpace(x.RecordTableSpecJson)
                ? template.RecordTableSpecJson
                : x.RecordTableSpecJson,
            DynamicFormTemplateId = x.DynamicFormTemplateId ?? period?.DynamicFormTemplateId,
            DynamicFormTemplateCode = x.DynamicFormTemplateCode ?? period?.DynamicFormTemplateCode,
            DynamicFormTemplateName = x.DynamicFormTemplateName ?? period?.DynamicFormTemplateName,
            SpecJson = string.IsNullOrWhiteSpace(x.SpecJson) ? template.SpecJson : x.SpecJson,

            DataRectR0 = x.DataRectR0,
            DataRectC0 = x.DataRectC0,
            DataRectR1 = x.DataRectR1,
            DataRectC1 = x.DataRectC1,
            W = x.W,
            H = x.H,

            Values1DJson = x.Values1DJson,
            FieldValuesJson = x.FieldValuesJson,
            TableValuesJson = x.TableValuesJson,
            DataOrigin = WorkReportDataOrigin.Normalize(x.DataOrigin),
            CumulativeContributionMode = WorkReportCumulativeContributionMode.Normalize(x.CumulativeContributionMode),
            CumulativeContributionPolicyJson = x.CumulativeContributionPolicyJson,
            SummarySourceJson = x.SummarySourceJson,
            AggregateSourceReportIds = x.AggregateSourceReportIds ?? new List<string>(),
            AggregateSourceAssignmentIds = x.AggregateSourceAssignmentIds ?? new List<string>(),
            AggregateSourceUpdatedAtUtc = x.AggregateSourceUpdatedAtUtc,
            AggregateSnapshotDirty = x.AggregateSnapshotDirty,
            AggregateSnapshotDirtyAtUtc = x.AggregateSnapshotDirtyAtUtc,
            AggregateSnapshotRefreshedAtUtc = x.AggregateSnapshotRefreshedAtUtc,
            AggregateRefreshError = x.AggregateRefreshError,

            CurrentProgressStatus = x.CurrentProgressStatus,
            ReportReason = x.ReportReason,
            Difficulties = x.Difficulties,
            ProposedSolution = x.ProposedSolution,

            IsLateSubmission = x.IsLateSubmission,
            LateReason = x.LateReason,

            ReviewerComment = x.ReviewerComment,
            ReviewerEvaluation = x.ReviewerEvaluation,
            ReturnReason = x.ReturnReason,

            VersionNo = x.VersionNo,
            IsCurrent = x.IsCurrent,
            IsActive = x.IsActive,
            DeactivatedAtUtc = x.DeactivatedAtUtc,
            DeactivatedByUserId = x.DeactivatedByUserId,
            DeactivationReason = x.DeactivationReason,
            ReactivatedAtUtc = x.ReactivatedAtUtc,
            ReactivatedByUserId = x.ReactivatedByUserId,

            SubmittedAtUtc = x.SubmittedAtUtc,
            SubmittedByUserId = x.SubmittedByUserId,
            ReturnedAtUtc = x.ReturnedAtUtc,
            ReturnedByUserId = x.ReturnedByUserId,
            ApprovedAtUtc = x.ApprovedAtUtc,
            ApprovedByUserId = x.ApprovedByUserId,

            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc
        };
    }

    private sealed record RuntimeRowLabelPayload(string BlockId, List<string> LabelCodes);

    private async Task InsertLogAsync(
        string workId,
        string workAssignmentId,
        string workReportPeriodId,
        string workAssignmentReportId,
        string action,
        string fromStatus,
        string toStatus,
        string actionByUserId,
        string? reason,
        string? comment,
        string? snapshotJson,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var log = new WorkAssignmentReportLog
        {
            WorkId = workId,
            WorkAssignmentId = workAssignmentId,
            WorkReportPeriodId = workReportPeriodId,
            WorkAssignmentReportId = workAssignmentReportId,
            Action = action,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            ActionByUserId = actionByUserId,
            ActionAtUtc = now,
            Reason = reason,
            Comment = comment,
            SnapshotJson = snapshotJson,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actionByUserId,
            UpdatedByUserId = actionByUserId,
            IsDeleted = false
        };

        await _ctx.WorkAssignmentReportLogs.InsertOneAsync(log, cancellationToken: ct);
    }

    private async Task FinalizeReportStatusOperationAsync(
        string operation,
        WorkAssignmentReport report,
        WorkReportPeriod? period,
        string fromStatus,
        string toStatus,
        string actorUserId,
        bool upsertQueue,
        bool disableQueue,
        bool rebuildProjection,
        bool syncAssignment,
        CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        var periodStatus = period?.Status.ToString();

        try
        {
            if (period is not null)
            {
                if (disableQueue)
                    await _queueService.DisableByPeriodAsync(period.WorkAssignmentId, period.AssigneeUserId, period.PeriodKey, actorUserId, ct);
                else if (upsertQueue)
                    await _queueService.UpsertPeriodAsync(period, actorUserId, ct);
            }

            if (syncAssignment)
                await _statusSync.SyncFromAssignmentAsync(report.WorkAssignmentId, ct);

            if (period is not null && rebuildProjection)
                await _docRoleReadModelProjection.RebuildReportPeriodAsync(period.Id, actorUserId, ct);

            if (ShouldRebuildApprovedStatistics(fromStatus, toStatus))
            {
                await _labelStatistics.RebuildForReportAsync(report.Id, actorUserId, ct);
                await _tableStatistics.RebuildForReportAsync(report.Id, actorUserId, ct);
                await _fieldStatistics.RebuildForReportAsync(report.Id, actorUserId, ct);
            }

            if (ShouldRefreshAggregateDependents(fromStatus, toStatus))
            {
                await RefreshDynamicFormAggregateDependentsRecursiveAsync(
                    report,
                    actorUserId,
                    new HashSet<string>(StringComparer.Ordinal),
                    ct);
            }

            _log.LogInformation(
                "WorkAssignment report status operation completed. operation={operation} reportId={reportId} periodId={periodId} assignmentId={assignmentId} workId={workId} fromStatus={fromStatus} toStatus={toStatus}",
                operation,
                report.Id,
                report.WorkReportPeriodId,
                report.WorkAssignmentId,
                report.WorkId,
                fromStatus,
                toStatus);

            await WriteStatusOperationLogAsync(new WorkStatusOperationLog
            {
                Operation = operation,
                Scope = "report",
                Result = "SUCCESS",
                WorkId = report.WorkId,
                WorkAssignmentId = report.WorkAssignmentId,
                WorkReportPeriodId = report.WorkReportPeriodId,
                WorkAssignmentReportId = report.Id,
                ActorUserId = actorUserId,
                FromStatus = fromStatus,
                ToStatus = toStatus,
                PeriodToStatus = periodStatus,
                Summary = $"upsertQueue={upsertQueue};disableQueue={disableQueue};rebuildProjection={rebuildProjection};syncAssignment={syncAssignment}",
                StartedAtUtc = startedAtUtc
            }, startedAtUtc, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "WorkAssignment report status operation failed. operation={operation} reportId={reportId} periodId={periodId} assignmentId={assignmentId} workId={workId} actorUserId={actorUserId} fromStatus={fromStatus} toStatus={toStatus} periodStatus={periodStatus} upsertQueue={upsertQueue} disableQueue={disableQueue} rebuildProjection={rebuildProjection} syncAssignment={syncAssignment}",
                operation,
                report.Id,
                report.WorkReportPeriodId,
                report.WorkAssignmentId,
                report.WorkId,
                actorUserId,
                fromStatus,
                toStatus,
                periodStatus,
                upsertQueue,
                disableQueue,
                rebuildProjection,
                syncAssignment);

            await WriteStatusOperationLogAsync(new WorkStatusOperationLog
            {
                Operation = operation,
                Scope = "report",
                Result = "FAILED",
                WorkId = report.WorkId,
                WorkAssignmentId = report.WorkAssignmentId,
                WorkReportPeriodId = report.WorkReportPeriodId,
                WorkAssignmentReportId = report.Id,
                ActorUserId = actorUserId,
                FromStatus = fromStatus,
                ToStatus = toStatus,
                PeriodToStatus = periodStatus,
                Summary = $"upsertQueue={upsertQueue};disableQueue={disableQueue};rebuildProjection={rebuildProjection};syncAssignment={syncAssignment}",
                ErrorType = ex.GetType().FullName,
                ErrorMessage = ex.Message,
                ErrorStackTrace = ex.ToString(),
                StartedAtUtc = startedAtUtc
            }, startedAtUtc, ct);

            throw;
        }
    }

    private static bool ShouldRebuildApprovedStatistics(string? fromStatus, string? toStatus)
        => string.Equals(fromStatus, WorkAssignmentReportStatus.Approved.ToString(), StringComparison.OrdinalIgnoreCase)
           || string.Equals(toStatus, WorkAssignmentReportStatus.Approved.ToString(), StringComparison.OrdinalIgnoreCase);

    private static bool ShouldRefreshAggregateDependents(string? fromStatus, string? toStatus)
        => ShouldRebuildApprovedStatistics(fromStatus, toStatus);

    private async Task RefreshDynamicFormAggregateDependentsRecursiveAsync(
        WorkAssignmentReport source,
        string actorUserId,
        HashSet<string> visitedReportIds,
        CancellationToken ct)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.Id))
            return;
        if (!visitedReportIds.Add(source.Id))
            return;
        if (string.IsNullOrWhiteSpace(source.DynamicFormTemplateId))
            return;

        var sourceAssignment = await _ctx.WorkAssignments
            .Find(x => x.Id == source.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        var candidates = await LoadDynamicFormAggregateRefreshCandidatesAsync(source, ct);
        if (candidates.Count == 0)
            return;

        var scopeAssignments = new Dictionary<string, WorkAssignment?>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (visitedReportIds.Contains(candidate.Id))
                continue;

            var summary = TryReadAggregateDraftSummary(candidate.SummarySourceJson);
            if (summary is null)
                continue;

            bool mayInclude;
            try
            {
                mayInclude = await AggregateRequestMayIncludeSourceAsync(
                    summary.AggregateRequest,
                    source,
                    sourceAssignment,
                    scopeAssignments,
                    ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Dynamic Form aggregate auto-refresh skipped invalid dependency. sourceReportId={sourceReportId} candidateReportId={candidateReportId}",
                    source.Id,
                    candidate.Id);
                continue;
            }

            if (!mayInclude)
            {
                continue;
            }

            await RefreshAggregateDependentAfterSourceChangeAsync(
                candidate,
                source,
                actorUserId,
                ct);
        }
    }

    private async Task RefreshDynamicFormAggregateDependentsForSourceWindowChangeAsync(
        WorkAssignmentReport source,
        WorkReportSourceWindow previousWindow,
        string? previousPeriodKey,
        WorkAssignmentReportStatus previousStatus,
        string actorUserId,
        CancellationToken ct)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.Id))
            return;
        if (string.IsNullOrWhiteSpace(source.DynamicFormTemplateId))
            return;

        var currentWindow = WorkAssignmentReportTemporalPolicy.ResolveSourceWindow(source);
        if (previousWindow.Equals(currentWindow) &&
            string.Equals(previousPeriodKey, source.PeriodKey, StringComparison.Ordinal))
        {
            return;
        }

        var sourceAssignment = await _ctx.WorkAssignments
            .Find(x => x.Id == source.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        var candidates = await LoadDynamicFormAggregateRefreshCandidatesAsync(source, ct);
        if (candidates.Count == 0)
            return;

        var scopeAssignments = new Dictionary<string, WorkAssignment?>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var summary = TryReadAggregateDraftSummary(candidate.SummarySourceJson);
            if (summary is null)
                continue;

            var normalized = NormalizeAggregateDraftRequest(summary.AggregateRequest);
            bool mayInclude;
            try
            {
                var currentMayInclude =
                    AggregateRequestStatusMayIncludeSource(normalized, source.Status) &&
                    await AggregateRequestMayIncludeSourceWindowAsync(
                        normalized,
                        source,
                        currentWindow,
                        source.PeriodKey,
                        sourceAssignment,
                        scopeAssignments,
                        ct);
                var previousMayInclude =
                    AggregateRequestStatusMayIncludeSource(normalized, previousStatus) &&
                    await AggregateRequestMayIncludeSourceWindowAsync(
                        normalized,
                        source,
                        previousWindow,
                        previousPeriodKey,
                        sourceAssignment,
                        scopeAssignments,
                        ct);

                mayInclude = currentMayInclude || previousMayInclude;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Dynamic Form aggregate source-window refresh skipped invalid dependency. sourceReportId={sourceReportId} candidateReportId={candidateReportId}",
                    source.Id,
                    candidate.Id);
                continue;
            }

            if (!mayInclude)
                continue;

            await RefreshAggregateDependentAfterSourceChangeAsync(
                candidate,
                source,
                actorUserId,
                ct);
        }
    }

    private async Task RefreshAggregateDependentAfterSourceChangeAsync(
        WorkAssignmentReport candidate,
        WorkAssignmentReport source,
        string actorUserId,
        CancellationToken ct)
    {
        try
        {
            var summary = TryReadAggregateDraftSummary(candidate.SummarySourceJson);
            if (summary is null)
            {
                await MarkAggregateSnapshotDirtyAsync(candidate, source, actorUserId, ct);
                return;
            }

            var wasApproved = candidate.Status == WorkAssignmentReportStatus.Approved;
            var refreshed = await RefreshDynamicFormAggregateReportFromSummaryAsync(
                candidate,
                summary,
                actorUserId,
                ct);

            if (refreshed is null)
            {
                await MarkAggregateSnapshotDirtyAsync(candidate, source, actorUserId, ct);
                return;
            }

            if (wasApproved)
            {
                await MoveApprovedAggregateReportBackToSubmittedAsync(
                    refreshed,
                    actorUserId,
                    $"Nguồn tổng hợp thay đổi; sourceReportId={source.Id}",
                    ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Dynamic Form aggregate dependent refresh failed. sourceReportId={sourceReportId} candidateReportId={candidateReportId}",
                source.Id,
                candidate.Id);

            await MarkAggregateSnapshotDirtyAsync(
                candidate,
                source,
                actorUserId,
                ct,
                NormalizeOptionalTextOrNull(ex.Message) ?? ex.GetType().Name);
        }
    }

    private async Task MoveApprovedAggregateReportBackToSubmittedAsync(
        WorkAssignmentReport report,
        string actorUserId,
        string reason,
        CancellationToken ct)
    {
        if (report.Status != WorkAssignmentReportStatus.Approved)
            return;

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == report.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        var now = DateTime.UtcNow;
        var fromStatus = report.Status;

        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == report.Id && !x.IsDeleted,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.Status, WorkAssignmentReportStatus.Submitted)
                .Set(x => x.ApprovedAtUtc, (DateTime?)null)
                .Set(x => x.ApprovedByUserId, (string?)null)
                .Set(x => x.ReviewerComment, reason)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        report.Status = WorkAssignmentReportStatus.Submitted;
        report.ApprovedAtUtc = null;
        report.ApprovedByUserId = null;
        report.ReviewerComment = reason;
        report.UpdatedAtUtc = now;
        report.UpdatedByUserId = actorUserId;

        if (period is not null)
        {
            var nextPeriodStatus = WorkReportPeriodStatusHelper.ResolveSubmittedStatus(period.DueAtUtc, now);

            await _ctx.WorkReportPeriods.UpdateOneAsync(
                x => x.Id == period.Id && !x.IsDeleted,
                Builders<WorkReportPeriod>.Update
                    .Set(x => x.Status, nextPeriodStatus)
                    .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus))
                    .Set(x => x.LastReviewedAtUtc, (DateTime?)null)
                    .Set(x => x.ReviewerComment, reason)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId),
                cancellationToken: ct);

            period.Status = nextPeriodStatus;
            period.IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus);
            period.LastReviewedAtUtc = null;
            period.ReviewerComment = reason;
            period.UpdatedAtUtc = now;
            period.UpdatedByUserId = actorUserId;

            await FinalizeReportStatusOperationAsync(
                "AUTO_AGGREGATE_REVIEW_INVALIDATED",
                report,
                period,
                fromStatus.ToString(),
                WorkAssignmentReportStatus.Submitted.ToString(),
                actorUserId,
                upsertQueue: true,
                disableQueue: false,
                rebuildProjection: true,
                syncAssignment: true,
                ct);
        }
        else
        {
            await _statusSync.SyncFromAssignmentAsync(report.WorkAssignmentId, ct);
            if (!string.IsNullOrWhiteSpace(report.WorkReportPeriodId))
                await _docRoleReadModelProjection.RebuildReportPeriodAsync(report.WorkReportPeriodId, actorUserId, ct);
        }

        await InsertLogAsync(
            workId: report.WorkId,
            workAssignmentId: report.WorkAssignmentId,
            workReportPeriodId: report.WorkReportPeriodId,
            workAssignmentReportId: report.Id,
            action: "AUTO_AGGREGATE_REVIEW_INVALIDATED",
            fromStatus: fromStatus.ToString(),
            toStatus: WorkAssignmentReportStatus.Submitted.ToString(),
            actionByUserId: actorUserId,
            reason: reason,
            comment: reason,
            snapshotJson: null,
            ct: ct);
    }

    private async Task MarkAggregateSnapshotDirtyAsync(
        WorkAssignmentReport candidate,
        WorkAssignmentReport source,
        string actorUserId,
        CancellationToken ct,
        string? refreshError = null)
    {
        var now = DateTime.UtcNow;
        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == candidate.Id && !x.IsDeleted,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.AggregateSnapshotDirty, true)
                .Set(x => x.AggregateSnapshotDirtyAtUtc, now)
                .Set(x => x.AggregateRefreshError, refreshError)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        candidate.AggregateSnapshotDirty = true;
        candidate.AggregateSnapshotDirtyAtUtc = now;
        candidate.AggregateRefreshError = refreshError;
        candidate.UpdatedAtUtc = now;
        candidate.UpdatedByUserId = actorUserId;

        if (!string.IsNullOrWhiteSpace(candidate.WorkReportPeriodId))
            await _docRoleReadModelProjection.RebuildReportPeriodAsync(candidate.WorkReportPeriodId, actorUserId, ct);

        await InsertLogAsync(
            workId: candidate.WorkId,
            workAssignmentId: candidate.WorkAssignmentId,
            workReportPeriodId: candidate.WorkReportPeriodId,
            workAssignmentReportId: candidate.Id,
            action: "MARK_AGGREGATE_SNAPSHOT_DIRTY",
            fromStatus: candidate.Status.ToString(),
            toStatus: candidate.Status.ToString(),
            actionByUserId: actorUserId,
            reason: null,
            comment: string.IsNullOrWhiteSpace(refreshError)
                ? $"sourceReportId={source.Id}"
                : $"sourceReportId={source.Id};refreshError={refreshError}",
            snapshotJson: null,
            ct: ct);
    }

    private async Task<List<WorkAssignmentReport>> LoadDynamicFormAggregateRefreshCandidatesAsync(
        WorkAssignmentReport source,
        CancellationToken ct)
    {
        var aggregateOrigins = new[]
        {
            WorkReportDataOrigin.AutoSummary,
            WorkReportDataOrigin.CopiedSummary,
            WorkReportDataOrigin.PartialMapping
        };

        var fb = Builders<WorkAssignmentReport>.Filter;
        var filter = fb.Eq(x => x.WorkId, source.WorkId)
                     & fb.Eq(x => x.DynamicFormTemplateId, source.DynamicFormTemplateId)
                     & fb.Ne(x => x.Id, source.Id)
                     & fb.In(x => x.DataOrigin, aggregateOrigins)
                     & fb.Ne(x => x.SummarySourceJson, null)
                     & fb.Ne(x => x.IsActive, false)
                     & fb.Eq(x => x.IsDeleted, false);

        return await _ctx.WorkAssignmentReports
            .Find(filter)
            .SortBy(x => x.UpdatedAtUtc)
            .Limit(200)
            .ToListAsync(ct);
    }

    private async Task<WorkAssignmentReport> RefreshAggregateSnapshotForReadAsync(
        WorkAssignmentReport report,
        string actorUserId,
        CancellationToken ct)
    {
        if (!report.AggregateSnapshotDirty)
            return report;

        var summary = TryReadAggregateDraftSummary(report.SummarySourceJson);
        if (summary is null)
            return report;

        try
        {
            var refreshed = await RefreshDynamicFormAggregateReportFromSummaryAsync(
                report,
                summary,
                actorUserId,
                ct) ?? report;

            if (refreshed.Status == WorkAssignmentReportStatus.Approved)
            {
                await MoveApprovedAggregateReportBackToSubmittedAsync(
                    refreshed,
                    actorUserId,
                    "Nguồn tổng hợp đã thay đổi trước khi mở báo cáo.",
                    ct);
            }

            return refreshed;
        }
        catch (Exception ex)
        {
            var now = DateTime.UtcNow;
            var error = NormalizeOptionalTextOrNull(ex.Message) ?? ex.GetType().Name;
            _log.LogWarning(
                ex,
                "Dynamic Form aggregate snapshot lazy refresh failed. reportId={reportId} actorUserId={actorUserId}",
                report.Id,
                actorUserId);

            await _ctx.WorkAssignmentReports.UpdateOneAsync(
                x => x.Id == report.Id && !x.IsDeleted,
                Builders<WorkAssignmentReport>.Update
                    .Set(x => x.AggregateRefreshError, error)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId),
                cancellationToken: ct);

            report.AggregateRefreshError = error;
            report.UpdatedAtUtc = now;
            report.UpdatedByUserId = actorUserId;
            return report;
        }
    }

    private async Task<bool> AggregateRequestMayIncludeSourceAsync(
        DynamicFormAggregateRequest req,
        WorkAssignmentReport source,
        WorkAssignment? sourceAssignment,
        Dictionary<string, WorkAssignment?> scopeAssignments,
        CancellationToken ct)
    {
        var normalized = NormalizeAggregateDraftRequest(req);
        if (!AggregateRequestStatusMayIncludeSource(normalized, source.Status))
            return false;

        return await AggregateRequestMayIncludeSourceWindowAsync(
            normalized,
            source,
            WorkAssignmentReportTemporalPolicy.ResolveSourceWindow(source),
            source.PeriodKey,
            sourceAssignment,
            scopeAssignments,
            ct);
    }

    private async Task<bool> AggregateRequestMayIncludeSourceWindowAsync(
        DynamicFormAggregateRequest normalized,
        WorkAssignmentReport source,
        WorkReportSourceWindow sourceWindow,
        string? fallbackPeriodKey,
        WorkAssignment? sourceAssignment,
        Dictionary<string, WorkAssignment?> scopeAssignments,
        CancellationToken ct)
    {
        if (!string.Equals(normalized.DynamicFormTemplateId, source.DynamicFormTemplateId, StringComparison.Ordinal))
            return false;

        if (!PeriodWindowMatchesAggregateRequest(normalized, sourceWindow, fallbackPeriodKey))
            return false;

        if (sourceAssignment is null)
            return false;

        if (normalized.SelectedUnitIds is { Count: > 0 })
        {
            var selectedUnits = normalized.SelectedUnitIds.ToHashSet(StringComparer.Ordinal);
            if (!(sourceAssignment.Assignees ?? new List<UserRef>())
                    .Any(x => !string.IsNullOrWhiteSpace(x.UnitId) && selectedUnits.Contains(x.UnitId)))
            {
                return false;
            }
        }

        var scopeId = normalized.ScopeAssignmentId;
        if (!scopeAssignments.TryGetValue(scopeId, out var scopeAssignment))
        {
            scopeAssignment = await _ctx.WorkAssignments
                .Find(x => x.Id == scopeId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            scopeAssignments[scopeId] = scopeAssignment;
        }

        if (scopeAssignment is null)
            return false;

        if (string.Equals(normalized.ScopeMode, "SUBTREE", StringComparison.Ordinal))
        {
            var pathPrefix = $"{scopeAssignment.Path}/";
            return string.Equals(sourceAssignment.WorkId, scopeAssignment.WorkId, StringComparison.Ordinal)
                   && !string.IsNullOrWhiteSpace(sourceAssignment.Path)
                   && sourceAssignment.Path.StartsWith(pathPrefix, StringComparison.Ordinal);
        }

        return string.Equals(sourceAssignment.ParentAssignmentId, scopeAssignment.Id, StringComparison.Ordinal);
    }

    private static bool AggregateRequestStatusMayIncludeSource(
        DynamicFormAggregateRequest req,
        WorkAssignmentReportStatus sourceStatus)
    {
        var mode = (req.SourceStatusMode ?? "APPROVED_ONLY").Trim().ToUpperInvariant();
        if (mode == "APPROVED_AND_SUBMITTED")
        {
            return sourceStatus is WorkAssignmentReportStatus.Approved
                or WorkAssignmentReportStatus.Submitted;
        }

        return sourceStatus == WorkAssignmentReportStatus.Approved;
    }

    private static bool PeriodMatchesAggregateRequest(
        DynamicFormAggregateRequest req,
        WorkAssignmentReport source)
        => WorkAssignmentReportTemporalPolicy.MatchesPeriodScope(
            source,
            req.PeriodScopeMode,
            req.PeriodKey,
            req.PeriodKeyFrom,
            req.PeriodKeyTo);

    private static bool PeriodWindowMatchesAggregateRequest(
        DynamicFormAggregateRequest req,
        WorkReportSourceWindow sourceWindow,
        string? fallbackPeriodKey)
        => WorkAssignmentReportTemporalPolicy.MatchesPeriodScope(
            sourceWindow,
            fallbackPeriodKey,
            req.PeriodScopeMode,
            req.PeriodKey,
            req.PeriodKeyFrom,
            req.PeriodKeyTo);

    private static bool HasSourceWindowChanged(
        WorkReportSourceWindow previousWindow,
        string? previousPeriodKey,
        WorkAssignmentReport current)
        => !previousWindow.Equals(WorkAssignmentReportTemporalPolicy.ResolveSourceWindow(current))
           || !string.Equals(previousPeriodKey, current.PeriodKey, StringComparison.Ordinal);

    private async Task<WorkAssignmentReport?> RefreshDynamicFormAggregateReportFromSummaryAsync(
        WorkAssignmentReport report,
        AggregateDraftSummary summary,
        string actorUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(report.DynamicFormTemplateId))
            return null;

        var aggregateReq = NormalizeAggregateDraftRequest(summary.AggregateRequest);
        if (!string.Equals(report.DynamicFormTemplateId, aggregateReq.DynamicFormTemplateId, StringComparison.Ordinal))
            return null;

        var aggregate = await _aggregateTableService.GetDynamicFormAggregateAsync(aggregateReq, ct);

        var form = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == report.DynamicFormTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (form is null)
            return null;

        var targetBlockId = NormalizeBlockId(summary.TargetBlockId ?? aggregateReq.BlockId ?? aggregate.Meta.BlockId);
        var block = ResolveAggregateDraftBlock(form, targetBlockId);
        if (block is null)
            return null;

        var dataOrigin = WorkReportDataOrigin.Normalize(summary.DataOrigin);
        var existingTopLevelValues = DeserializeValues1D(report.Values1DJson);
        var isTopLevelBlock = string.Equals(targetBlockId, ResolveTopLevelBlockId(form), StringComparison.Ordinal);
        var currentBlockValues = isTopLevelBlock
            ? existingTopLevelValues
            : ExtractBlockDecimalValues(report.TableValuesJson, targetBlockId);
        var clearExisting = summary.ClearExistingValues ?? dataOrigin != WorkReportDataOrigin.PartialMapping;
        var targetValues = clearExisting
            ? CreateEmptyValues1D(block.W, block.H)
            : NormalizeDecimalValues(currentBlockValues, block.W * block.H);

        if (!clearExisting)
            ClearAggregateDraftTargetIndexes(targetValues, summary.TargetIndexes);

        ApplyAggregateRowsToValues(
            targetValues,
            aggregate.Rows,
            block,
            NormalizeAggregateDraftValueSelector(summary.ValueSelector));

        var tableValuesJson = BuildAggregateDraftTableValuesJson(report, form, block, targetValues, aggregate);
        var topLevelValues = isTopLevelBlock
            ? targetValues
            : NormalizeDecimalValues(existingTopLevelValues, report.W * report.H);
        var values1DJson = JsonSerializer.Serialize(topLevelValues, _jsonOptions);
        var contributionPolicyJson = BuildAggregateDraftContributionPolicyJson(dataOrigin, aggregate.Rows, block.BlockId);
        var summarySourceJson = BuildAggregateDraftSummarySourceJson(
            dataOrigin,
            aggregateReq,
            aggregate,
            block,
            NormalizeAggregateDraftValueSelector(summary.ValueSelector),
            targetBlockId,
            clearExisting);
        var sourceSnapshot = ExtractAggregateSourceSnapshot(summarySourceJson);
        var now = DateTime.UtcNow;

        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == report.Id && !x.IsDeleted,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.Values1DJson, values1DJson)
                .Set(x => x.TableValuesJson, tableValuesJson)
                .Set(x => x.DataOrigin, dataOrigin)
                .Set(x => x.CumulativeContributionMode, WorkReportDataOrigin.DefaultContributionMode(dataOrigin))
                .Set(x => x.CumulativeContributionPolicyJson, contributionPolicyJson)
                .Set(x => x.SummarySourceJson, summarySourceJson)
                .Set(x => x.AggregateSourceReportIds, sourceSnapshot.ReportIds)
                .Set(x => x.AggregateSourceAssignmentIds, sourceSnapshot.AssignmentIds)
                .Set(x => x.AggregateSourceUpdatedAtUtc, now)
                .Set(x => x.AggregateSnapshotDirty, false)
                .Set(x => x.AggregateSnapshotDirtyAtUtc, (DateTime?)null)
                .Set(x => x.AggregateSnapshotRefreshedAtUtc, now)
                .Set(x => x.AggregateRefreshError, (string?)null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        report.Values1DJson = values1DJson;
        report.TableValuesJson = tableValuesJson;
        report.DataOrigin = dataOrigin;
        report.CumulativeContributionMode = WorkReportDataOrigin.DefaultContributionMode(dataOrigin);
        report.CumulativeContributionPolicyJson = contributionPolicyJson;
        report.SummarySourceJson = summarySourceJson;
        report.AggregateSourceReportIds = sourceSnapshot.ReportIds;
        report.AggregateSourceAssignmentIds = sourceSnapshot.AssignmentIds;
        report.AggregateSourceUpdatedAtUtc = now;
        report.AggregateSnapshotDirty = false;
        report.AggregateSnapshotDirtyAtUtc = null;
        report.AggregateSnapshotRefreshedAtUtc = now;
        report.AggregateRefreshError = null;
        report.UpdatedAtUtc = now;
        report.UpdatedByUserId = actorUserId;

        await InsertLogAsync(
            workId: report.WorkId,
            workAssignmentId: report.WorkAssignmentId,
            workReportPeriodId: report.WorkReportPeriodId,
            workAssignmentReportId: report.Id,
            action: "AUTO_REFRESH_AGGREGATE",
            fromStatus: report.Status.ToString(),
            toStatus: report.Status.ToString(),
            actionByUserId: actorUserId,
            reason: null,
            comment: $"sourceReportCount={aggregate.Sources.Count};rowCount={aggregate.Rows.Count}",
            snapshotJson: null,
            ct: ct);

        if (report.Status == WorkAssignmentReportStatus.Approved)
        {
            await _labelStatistics.RebuildForReportAsync(report.Id, actorUserId, ct);
            await _tableStatistics.RebuildForReportAsync(report.Id, actorUserId, ct);
            await _fieldStatistics.RebuildForReportAsync(report.Id, actorUserId, ct);
        }

        if (!string.IsNullOrWhiteSpace(report.WorkReportPeriodId))
            await _docRoleReadModelProjection.RebuildReportPeriodAsync(report.WorkReportPeriodId, actorUserId, ct);

        return report;
    }

    private static void ClearAggregateDraftTargetIndexes(
        List<decimal?> targetValues,
        IReadOnlyCollection<int> targetIndexes)
    {
        foreach (var index in targetIndexes)
        {
            if (index >= 0 && index < targetValues.Count)
                targetValues[index] = null;
        }
    }

    private static bool IsSameAggregateDraftTarget(
        AggregateDraftSummary? summary,
        string dynamicFormTemplateId,
        string targetBlockId)
    {
        if (summary is null || summary.TargetIndexes.Count == 0)
            return false;

        if (!string.IsNullOrWhiteSpace(summary.AggregateRequest.DynamicFormTemplateId) &&
            !string.Equals(summary.AggregateRequest.DynamicFormTemplateId.Trim(), dynamicFormTemplateId, StringComparison.Ordinal))
        {
            return false;
        }

        var previousTargetBlockId = NormalizeBlockId(summary.TargetBlockId ?? summary.AggregateRequest.BlockId);
        return string.Equals(previousTargetBlockId, targetBlockId, StringComparison.Ordinal);
    }

    private async Task WriteStatusOperationLogAsync(
        WorkStatusOperationLog log,
        DateTime startedAtUtc,
        CancellationToken ct)
    {
        var completedAtUtc = DateTime.UtcNow;
        log.CompletedAtUtc = completedAtUtc;
        log.DurationMs = (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        await _statusLog.WriteAsync(log, ct);
    }

    private static List<MyReportTemplateRow> ApplyTemplateSort(
        List<MyReportTemplateRow> rows,
        string? sortField,
        string? sortDirection)
    {
        var desc = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);

        Func<MyReportTemplateRow, object?> keySelector = (sortField ?? "latestUpdatedAtUtc").ToLowerInvariant() switch
        {
            "dynamicformtemplatecode" => x => x.DynamicFormTemplateCode,
            "dynamicformtemplatename" => x => x.DynamicFormTemplateName,
            "dynamicexcelcode" => x => x.DynamicExcelCode,
            "dynamicexcelname" => x => x.DynamicExcelName,
            "latestdueatutc" => x => x.LatestDueAtUtc,
            "periodcount" => x => x.PeriodCount,
            _ => x => x.LatestUpdatedAtUtc
        };

        return desc
            ? rows.OrderByDescending(keySelector).ToList()
            : rows.OrderBy(keySelector).ToList();
    }

    private static SortDefinition<WorkAssignmentReport> BuildReportSort(string? sortField, string? sortDirection)
    {
        var desc = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);

        return (sortField ?? "updatedAtUtc").ToLowerInvariant() switch
        {
            "createdatutc" => desc
                ? Builders<WorkAssignmentReport>.Sort.Descending(x => x.CreatedAtUtc)
                : Builders<WorkAssignmentReport>.Sort.Ascending(x => x.CreatedAtUtc),

            "periodkey" => desc
                ? Builders<WorkAssignmentReport>.Sort.Descending(x => x.PeriodKey)
                : Builders<WorkAssignmentReport>.Sort.Ascending(x => x.PeriodKey),

            "versionno" => desc
                ? Builders<WorkAssignmentReport>.Sort.Descending(x => x.VersionNo)
                : Builders<WorkAssignmentReport>.Sort.Ascending(x => x.VersionNo),

            "dueatutc" => desc
                ? Builders<WorkAssignmentReport>.Sort.Descending(x => x.DueAtUtc)
                : Builders<WorkAssignmentReport>.Sort.Ascending(x => x.DueAtUtc),

            "submittedatutc" => desc
                ? Builders<WorkAssignmentReport>.Sort.Descending(x => x.SubmittedAtUtc)
                : Builders<WorkAssignmentReport>.Sort.Ascending(x => x.SubmittedAtUtc),

            _ => desc
                ? Builders<WorkAssignmentReport>.Sort.Descending(x => x.UpdatedAtUtc)
                : Builders<WorkAssignmentReport>.Sort.Ascending(x => x.UpdatedAtUtc)
        };
    }

    private static SortDefinition<MyReportTemplateListDocRole> BuildTemplateListDocRoleSort(string? sortField, string? sortDirection)
    {
        var desc = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);
        var sort = Builders<MyReportTemplateListDocRole>.Sort;

        return (sortField ?? "latestUpdatedAtUtc").ToLowerInvariant() switch
        {
            "dynamicformtemplatecode" => desc ? sort.Descending(x => x.DynamicFormTemplateCode) : sort.Ascending(x => x.DynamicFormTemplateCode),
            "dynamicformtemplatename" => desc ? sort.Descending(x => x.DynamicFormTemplateName) : sort.Ascending(x => x.DynamicFormTemplateName),
            "dynamicexcelcode" => desc ? sort.Descending(x => x.DynamicExcelCode) : sort.Ascending(x => x.DynamicExcelCode),
            "dynamicexcelname" => desc ? sort.Descending(x => x.DynamicExcelName) : sort.Ascending(x => x.DynamicExcelName),
            "latestdueatutc" => desc ? sort.Descending(x => x.LatestDueAtUtc) : sort.Ascending(x => x.LatestDueAtUtc),
            "periodcount" => desc ? sort.Descending(x => x.PeriodCount) : sort.Ascending(x => x.PeriodCount),
            _ => desc ? sort.Descending(x => x.LatestUpdatedAtUtc) : sort.Ascending(x => x.LatestUpdatedAtUtc)
        };
    }

    private static SortDefinition<MyReportPeriodListDocRole> BuildReportPeriodListDocRoleSort(string? sortField, string? sortDirection)
    {
        var desc = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);
        var sort = Builders<MyReportPeriodListDocRole>.Sort;

        return (sortField ?? "updatedAtUtc").ToLowerInvariant() switch
        {
            "createdatutc" => desc ? sort.Descending(x => x.SourceCreatedAtUtc) : sort.Ascending(x => x.SourceCreatedAtUtc),
            "periodkey" => desc ? sort.Descending(x => x.PeriodKey) : sort.Ascending(x => x.PeriodKey),
            "versionno" => desc ? sort.Descending(x => x.VersionNo) : sort.Ascending(x => x.VersionNo),
            "dueatutc" => desc ? sort.Descending(x => x.DueAtUtc) : sort.Ascending(x => x.DueAtUtc),
            "submittedatutc" => desc ? sort.Descending(x => x.LastSubmittedAtUtc) : sort.Ascending(x => x.LastSubmittedAtUtc),
            _ => desc ? sort.Descending(x => x.SortUpdatedAtUtc) : sort.Ascending(x => x.SortUpdatedAtUtc)
        };
    }

    private static WorkReportPeriodStatus ResolveDraftPeriodStatus(DateTime? dueAtUtc, DateTime now)
        => WorkReportPeriodStatusHelper.ResolveDraftStatus(dueAtUtc, now);

    private static DateTime? NormalizeDate(DateTime? value)
        => WorkAssignmentReportHistoricalDataHelper.NormalizeDate(value);

    private static void EnsureReportDateRange(
        DateTime? start,
        DateTime? end,
        string startField,
        string endField)
    {
        if (start.HasValue && end.HasValue && end.Value.Date < start.Value.Date)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_TIME_RANGE_INVALID,
                new { startField, endField, start, end });
    }

    private static bool IsHistoricalReportData(
        WorkAssignmentReport report,
        WorkReportPeriod? period,
        DateTime now)
    {
        var periodKind = string.IsNullOrWhiteSpace(report.PeriodKind)
            ? period?.PeriodKind
            : report.PeriodKind;

        return report.IsHistoricalData ||
               period?.IsHistoricalData == true ||
               WorkAssignmentReportHistoricalDataHelper.IsHistoricalUserCreatedData(
                   periodKind,
                   report.ReportDate ?? period?.ReportDate,
                   report.PeriodStart ?? period?.PeriodStart,
                   report.PeriodEnd ?? period?.PeriodEnd,
                   now);
    }

    private static bool IsHistoricalReportData(
        DateTime? reportDate,
        DateTime? periodStart,
        DateTime? periodEnd,
        DateTime now)
    {
        return WorkAssignmentReportHistoricalDataHelper.IsHistoricalUserCreatedData(
            WorkReportPeriodKind.UserCreated,
            reportDate,
            periodStart,
            periodEnd,
            now);
    }

    private static WorkReportPeriodStatus ResolveApprovedPeriodStatus(
        WorkReportPeriod period,
        WorkAssignmentReport report,
        DateTime now)
    {
        return WorkAssignmentReportHistoricalDataHelper.ResolveApprovedPeriodStatus(period, report, now);
    }

    private async Task EnsurePreviousReportsApprovedAsync(
        WorkReportPeriod? period,
        CancellationToken ct)
    {
        if (period is null)
            return;

        var fb = Builders<WorkReportPeriod>.Filter;
        var filter = fb.Eq(x => x.WorkAssignmentId, period.WorkAssignmentId)
                     & fb.Eq(x => x.AssigneeUserId, period.AssigneeUserId)
                     & fb.Eq(x => x.PeriodKind, period.PeriodKind)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Ne(x => x.Id, period.Id);

        if (!string.IsNullOrWhiteSpace(period.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, period.DynamicFormTemplateId);
        else if (!string.IsNullOrWhiteSpace(period.DynamicExcelId))
            filter &= fb.Eq(x => x.DynamicExcelId, period.DynamicExcelId);

        var candidates = await _ctx.WorkReportPeriods
            .Find(filter)
            .ToListAsync(ct);

        var previousOpenPeriod = candidates
            .Where(candidate => ComparePeriodOrder(candidate, period) < 0)
            .Where(candidate => !WorkReportPeriodStatusHelper.IsTerminal(candidate.Status))
            .OrderBy(ResolvePeriodOrder)
            .FirstOrDefault();

        if (previousOpenPeriod is not null)
            throw AppExceptionFactory.Create(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_PREVIOUS_PERIOD_OPEN,
                new
                {
                    periodId = period.Id,
                    periodInstanceKey = period.PeriodInstanceKey,
                    previousPeriodId = previousOpenPeriod.Id,
                    previousPeriodInstanceKey = previousOpenPeriod.PeriodInstanceKey,
                    previousPeriodStatus = previousOpenPeriod.Status
                });
    }

    private static int ComparePeriodOrder(WorkReportPeriod left, WorkReportPeriod right)
    {
        var byTime = DateTime.Compare(ResolvePeriodOrder(left), ResolvePeriodOrder(right));
        if (byTime != 0)
            return byTime;

        var byCreated = DateTime.Compare(left.CreatedAtUtc, right.CreatedAtUtc);
        if (byCreated != 0)
            return byCreated;

        return string.Compare(left.Id, right.Id, StringComparison.Ordinal);
    }

    private static DateTime ResolvePeriodOrder(WorkReportPeriod period)
    {
        if (WorkReportPeriodKind.IsUserCreated(period.PeriodKind))
            return period.ReportDate
                   ?? period.PeriodStart
                   ?? period.PeriodEnd
                   ?? period.DueAtUtc
                   ?? period.CreatedAtUtc;

        return period.PeriodStart
               ?? period.ReportDate
               ?? period.PeriodEnd
               ?? period.DueAtUtc
               ?? period.CreatedAtUtc;
    }

    private static DateTime NormalizeDueAtUtc(DateTime date)
        => AppTimeRangeHelper.EndOfUtcDate(date);

    private static TemplateSnapshotDTO BuildTemplateSnapshot(DynamicExcelTemplate template)
    {
        return new TemplateSnapshotDTO
        {
            TemplateId = template.Id,
            Code = template.Code,
            Name = template.Name,
            SpecJson = template.SpecJson,
            TableKind = NormalizeDynamicExcelTableKind(template.TableKind),
            RecordTableSpecJson = template.RecordTableSpecJson,
            RawWorkbookDataJson = template.RawWorkbookDataJson,
            DataRectR0 = template.DataRectR0,
            DataRectC0 = template.DataRectC0,
            DataRectR1 = template.DataRectR1,
            DataRectC1 = template.DataRectC1,
            W = template.W,
            H = template.H
        };
    }

    private static ScheduleSnapshotDTO BuildScheduleSnapshot(WorkAssignment assignment)
    {
        return new ScheduleSnapshotDTO
        {
            CycleType = assignment.Schedule?.CycleType ?? string.Empty,
            StartDate = assignment.Schedule?.StartDate,
            AssignmentStartDate = assignment.StartDate,
            AssignmentCompletedDate = assignment.CompletedDate,
            WeekDays = assignment.Schedule?.WeekDays?.ToArray() ?? Array.Empty<int>(),
            MonthDays = assignment.Schedule?.MonthDays?.ToArray() ?? Array.Empty<int>(),
            QuarterDays = assignment.Schedule?.QuarterDays?.ToArray() ?? Array.Empty<int>(),
            SemiAnnualDays = assignment.Schedule?.SemiAnnualDays?.ToArray() ?? Array.Empty<int>(),
            Note = assignment.Schedule?.Note,
            DynamicFormDataSourceRulesJson = assignment.DynamicFormDataSourceRulesJson
        };
    }

    private static List<decimal?> CreateEmptyValues1D(int w, int h)
    {
        var len = Math.Max(0, w) * Math.Max(0, h);
        return Enumerable.Range(0, len).Select(_ => (decimal?)null).ToList();
    }

    private static string NormalizeDynamicExcelTableKind(string? value)
        => string.Equals(value?.Trim(), DynamicExcelTableKind.RecordTable, StringComparison.OrdinalIgnoreCase)
            ? DynamicExcelTableKind.RecordTable
            : DynamicExcelTableKind.NumericGrid;

    private static WorkReportPeriodRow MapToPeriodRow(WorkReportPeriod x)
    {
        return new WorkReportPeriodRow
        {
            Id = x.Id,
            WorkId = x.WorkId,
            WorkAssignmentId = x.WorkAssignmentId,
            WorkTemplateAssigneeId = x.WorkTemplateAssigneeId,
            DynamicExcelId = x.DynamicExcelId,
            DynamicExcelCode = x.DynamicExcelCode,
            DynamicExcelName = x.DynamicExcelName,
            AssigneeUserId = x.AssigneeUserId,
            PeriodKey = x.PeriodKey,
            PeriodInstanceKey = NormalizePeriodInstanceKey(x),
            PeriodKind = NormalizePeriodKind(x.PeriodKind),
            ReportTitle = x.ReportTitle,
            ReportDate = x.ReportDate,
            LinkedScheduledPeriodId = x.LinkedScheduledPeriodId,
            StartedDate = x.StartedDate,
            CompletedDate = x.CompletedDate,
            IsHistoricalData = x.IsHistoricalData,
            HistoricalDataApproved = x.HistoricalDataApproved,
            HistoricalDataApprovedAtUtc = x.HistoricalDataApprovedAtUtc,
            HistoricalDataApprovedByUserId = x.HistoricalDataApprovedByUserId,
            PeriodStart = x.PeriodStart,
            PeriodEnd = x.PeriodEnd,
            DueAtUtc = x.DueAtUtc,
            Status = (int)x.Status,
            IsOverdue = x.IsOverdue,
            CurrentReportId = x.CurrentReportId,
            ReportVersionCount = x.ReportVersionCount,
            LastDraftSavedAtUtc = x.LastDraftSavedAtUtc,
            LastSubmittedAtUtc = x.LastSubmittedAtUtc,
            LastReviewedAtUtc = x.LastReviewedAtUtc,
            CurrentProgressStatus = x.CurrentProgressStatus,
            ReportReason = x.ReportReason,
            Difficulties = x.Difficulties,
            ProposedSolution = x.ProposedSolution,
            LateReason = x.LateReason,
            ReviewerComment = x.ReviewerComment,
            ReturnReason = x.ReturnReason
        };
    }

    private static WorkAssignmentReportListRow MapToListRow(WorkAssignmentReport x)
    {
        return new WorkAssignmentReportListRow
        {
            Id = x.Id,
            WorkId = x.WorkId,
            WorkAssignmentId = x.WorkAssignmentId,
            WorkReportPeriodId = x.WorkReportPeriodId,
            AssigneeUserId = x.AssigneeUserId,
            PeriodKey = x.PeriodKey,
            PeriodInstanceKey = NormalizeReportPeriodInstanceKey(x),
            PeriodKind = NormalizePeriodKind(x.PeriodKind),
            ReportTitle = x.ReportTitle,
            ReportDate = x.ReportDate,
            LinkedScheduledPeriodId = x.LinkedScheduledPeriodId,
            StartedDate = x.StartedDate,
            CompletedDate = x.CompletedDate,
            IsHistoricalData = x.IsHistoricalData,
            HistoricalDataApproved = x.HistoricalDataApproved,
            HistoricalDataApprovedAtUtc = x.HistoricalDataApprovedAtUtc,
            HistoricalDataApprovedByUserId = x.HistoricalDataApprovedByUserId,
            PeriodStart = x.PeriodStart,
            PeriodEnd = x.PeriodEnd,
            DueAtUtc = x.DueAtUtc,
            Status = (int)x.Status,
            ReportStatus = (int)x.Status,
            PeriodStatus = null,
            IsLateSubmission = x.IsLateSubmission,
            LateReason = x.LateReason,
            DynamicExcelTemplateId = x.DynamicExcelTemplateId,
            DynamicExcelTemplateCode = x.DynamicExcelTemplateCode,
            DynamicExcelTemplateName = x.DynamicExcelTemplateName,
            DynamicFormTemplateId = x.DynamicFormTemplateId,
            DynamicFormTemplateCode = x.DynamicFormTemplateCode,
            DynamicFormTemplateName = x.DynamicFormTemplateName,
            DataOrigin = WorkReportDataOrigin.Normalize(x.DataOrigin),
            AggregateSnapshotDirty = x.AggregateSnapshotDirty,
            AggregateSnapshotDirtyAtUtc = x.AggregateSnapshotDirtyAtUtc,
            AggregateSnapshotRefreshedAtUtc = x.AggregateSnapshotRefreshedAtUtc,
            AggregateRefreshError = x.AggregateRefreshError,
            CurrentProgressStatus = x.CurrentProgressStatus,
            ReportReason = x.ReportReason,
            Difficulties = x.Difficulties,
            ProposedSolution = x.ProposedSolution,
            VersionNo = x.VersionNo,
            IsCurrent = x.IsCurrent,
            IsActive = x.IsActive,
            DeactivatedAtUtc = x.DeactivatedAtUtc,
            DeactivationReason = x.DeactivationReason,
            SubmittedAtUtc = x.SubmittedAtUtc,
            SubmittedByUserId = x.SubmittedByUserId,
            ApprovedAtUtc = x.ApprovedAtUtc,
            ApprovedByUserId = x.ApprovedByUserId,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc
        };
    }

    private static System.Linq.Expressions.Expression<Func<MyReportPeriodListDocRole, WorkAssignmentReportListRow>> MapToListRowProjection()
        => x => new WorkAssignmentReportListRow
        {
            Id = x.CurrentReportId ?? string.Empty,
            WorkId = x.WorkId,
            WorkAssignmentId = x.AssignmentId,
            WorkReportPeriodId = x.WorkReportPeriodId,
            AssigneeUserId = x.AssigneeUserId,
            PeriodKey = x.PeriodKey,
            PeriodInstanceKey = x.PeriodInstanceKey,
            PeriodKind = x.PeriodKind,
            ReportTitle = x.ReportTitle,
            ReportDate = x.ReportDate,
            LinkedScheduledPeriodId = x.LinkedScheduledPeriodId,
            StartedDate = x.StartedDate,
            CompletedDate = x.CompletedDate,
            IsHistoricalData = x.IsHistoricalData,
            HistoricalDataApproved = x.HistoricalDataApproved,
            HistoricalDataApprovedAtUtc = x.HistoricalDataApprovedAtUtc,
            HistoricalDataApprovedByUserId = x.HistoricalDataApprovedByUserId,
            PeriodStart = x.PeriodStart,
            PeriodEnd = x.PeriodEnd,
            DueAtUtc = x.DueAtUtc,
            Status = x.ReportStatus.HasValue ? (int)x.ReportStatus.Value : (int)x.PeriodStatus,
            ReportStatus = x.ReportStatus.HasValue ? (int?)x.ReportStatus.Value : null,
            PeriodStatus = (int)x.PeriodStatus,
            IsLateSubmission = x.IsLateSubmission,
            LateReason = null,
            DynamicExcelTemplateId = x.DynamicExcelId,
            DynamicExcelTemplateCode = x.DynamicExcelCode,
            DynamicExcelTemplateName = x.DynamicExcelName,
            DynamicFormTemplateId = x.DynamicFormTemplateId,
            DynamicFormTemplateCode = x.DynamicFormTemplateCode,
            DynamicFormTemplateName = x.DynamicFormTemplateName,
            CurrentProgressStatus = null,
            ReportReason = null,
            Difficulties = null,
            ProposedSolution = null,
            VersionNo = x.VersionNo,
            IsCurrent = x.IsCurrentReport,
            IsActive = x.ReportIsActive,
            DeactivatedAtUtc = x.ReportDeactivatedAtUtc,
            DeactivationReason = x.ReportDeactivationReason,
            SubmittedAtUtc = x.LastSubmittedAtUtc,
            SubmittedByUserId = null,
            ReturnedAtUtc = x.ReturnedAtUtc,
            ReturnedByUserId = null,
            ReturnReason = null,
            ApprovedAtUtc = x.ApprovedAtUtc,
            ApprovedByUserId = null,
            CreatedAtUtc = x.SourceCreatedAtUtc,
            UpdatedAtUtc = x.SortUpdatedAtUtc
        };

    private static void EnsureActor(string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            throw AppExceptionFactory.Unauthorized(
                AppErrorCode.AUTH_UNAUTHORIZED,
                new { actorUserId });
    }

    private static void EnsureReportIsActive(WorkAssignmentReport report)
    {
        if (report.IsActive == false)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_INACTIVE,
                ReportDetails(report));
    }

    private static AppException ReportWorkIdRequired(string? workId)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_WORK_ID_REQUIRED,
            new { workId });

    private static AppException ReportAssignmentIdRequired(string? workAssignmentId)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_ASSIGNMENT_ID_REQUIRED,
            new { workAssignmentId });

    private static AppException ReportPeriodIdRequired(string? workReportPeriodId)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_PERIOD_ID_REQUIRED,
            new { workReportPeriodId });

    private static AppException ReportIdRequired(string? reportId)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_ID_REQUIRED,
            new { reportId });

    private static AppException ReportNotFound(string? reportId)
        => AppExceptionFactory.NotFound(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_NOT_FOUND,
            new { reportId });

    private static AppException ReportAssignmentNotFound(string? workAssignmentId)
        => AppExceptionFactory.NotFound(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_ASSIGNMENT_NOT_FOUND,
            new { workAssignmentId });

    private static AppException ReportPeriodNotFound(string? workReportPeriodId)
        => AppExceptionFactory.NotFound(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_PERIOD_NOT_FOUND,
            new { workReportPeriodId });

    private static AppException ReportBindingNotFound(string? workAssignmentId, string? actorUserId)
        => AppExceptionFactory.Forbidden(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_BINDING_NOT_FOUND,
            new { workAssignmentId, actorUserId });

    private static AppException InvalidReportStatus(
        AppErrorCode code,
        WorkAssignmentReport report,
        WorkAssignmentReportStatus expectedStatus,
        string? actorUserId = null)
        => AppExceptionFactory.BadRequest(
            code,
            new
            {
                reportId = report.Id,
                report.Status,
                expectedStatus,
                report.WorkId,
                report.WorkAssignmentId,
                report.WorkReportPeriodId,
                report.AssigneeUserId,
                actorUserId
            });

    private static AppException InvalidReportValues(
        WorkAssignmentReport report,
        int expectedLength,
        int actualLength,
        string? actorUserId = null)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
            new
            {
                reportId = report.Id,
                report.WorkId,
                report.WorkAssignmentId,
                report.WorkReportPeriodId,
                report.W,
                report.H,
                expectedLength,
                actualLength,
                actorUserId
            });

    private static object ReportDetails(WorkAssignmentReport report, string? actorUserId = null)
        => new
        {
            reportId = report.Id,
            report.WorkId,
            report.WorkAssignmentId,
            report.WorkReportPeriodId,
            report.AssigneeUserId,
            report.PeriodKey,
            report.PeriodInstanceKey,
            report.Status,
            actorUserId
        };

    private static object PeriodDetails(WorkReportPeriod period, string? actorUserId = null)
        => new
        {
            periodId = period.Id,
            period.WorkId,
            period.WorkAssignmentId,
            period.WorkTemplateAssigneeId,
            period.AssigneeUserId,
            period.PeriodKey,
            period.PeriodInstanceKey,
            period.PeriodKind,
            period.Status,
            actorUserId
        };

    private static string? NormalizeOptionalTextOrNull(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string ResolveReportDataOrigin(string? requested, string? current)
        => requested is null
            ? WorkReportDataOrigin.Normalize(current)
            : WorkReportDataOrigin.Normalize(requested);

    private static bool ShouldAcceptReportDataPayload(
        WorkAssignmentReport entity,
        string nextDataOrigin,
        string? nextSummarySourceJson)
    {
        if (!string.Equals(
                WorkReportDataOrigin.Normalize(nextDataOrigin),
                WorkReportDataOrigin.AutoSummary,
                StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(nextSummarySourceJson))
            return true;

        // Auto-summary cells are the report container cache. Once the aggregate
        // contract exists, normal draft/submit calls may update metadata only.
        return !string.Equals(
            NormalizeOptionalTextOrNull(entity.SummarySourceJson),
            NormalizeOptionalTextOrNull(nextSummarySourceJson),
            StringComparison.Ordinal);
    }

    private static string ResolveCumulativeContributionMode(
        string? requestedMode,
        string? requestedOrigin,
        string? currentMode,
        string? currentOrigin)
    {
        if (!string.IsNullOrWhiteSpace(requestedMode))
            return WorkReportCumulativeContributionMode.Normalize(requestedMode);

        if (requestedOrigin is not null)
            return WorkReportDataOrigin.DefaultContributionMode(requestedOrigin);

        if (!string.IsNullOrWhiteSpace(currentMode))
            return WorkReportCumulativeContributionMode.Normalize(currentMode);

        return WorkReportDataOrigin.DefaultContributionMode(currentOrigin);
    }

    private static string? ResolveOptionalJsonOverride(string? requested, string? current)
        => requested is null ? NormalizeOptionalTextOrNull(current) : NormalizeOptionalTextOrNull(requested);

    private static string? ResolveContributionPolicyJsonOverride(
        string? requested,
        string? current,
        string reportId,
        string actorUserId)
    {
        var json = ResolveOptionalJsonOverride(requested, current);
        EnsureJsonObjectOrNull(
            json,
            AppErrorCode.WORK_ASSIGNMENT_REPORT_CONTRIBUTION_POLICY_JSON_INVALID,
            "cumulativeContributionPolicyJson",
            reportId,
            actorUserId);
        return json;
    }

    private static string? ResolveSummarySourceJsonOverride(
        string? requested,
        string? current,
        string reportId,
        string actorUserId)
    {
        var json = ResolveOptionalJsonOverride(requested, current);
        EnsureJsonObjectOrNull(
            json,
            AppErrorCode.WORK_ASSIGNMENT_REPORT_SUMMARY_SOURCE_JSON_INVALID,
            "summarySourceJson",
            reportId,
            actorUserId);
        return json;
    }

    private static void EnsureJsonObjectOrNull(
        string? json,
        AppErrorCode errorCode,
        string field,
        string reportId,
        string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return;
        }
        catch (JsonException)
        {
            // Throw the coded error below so FE and automation receive a stable contract.
        }

        throw AppExceptionFactory.BadRequest(
            errorCode,
            new { field, reportId, actorUserId });
    }

    private static AggregateSourceSnapshot ExtractAggregateSourceSnapshot(string? summarySourceJson)
    {
        if (string.IsNullOrWhiteSpace(summarySourceJson))
            return new AggregateSourceSnapshot(false, new List<string>(), new List<string>());

        try
        {
            using var doc = JsonDocument.Parse(summarySourceJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new AggregateSourceSnapshot(false, new List<string>(), new List<string>());

            var kind = ReadJsonString(doc.RootElement, "kind");
            if (!string.Equals(kind, "DYNAMIC_FORM_AGGREGATE_DRAFT", StringComparison.Ordinal))
                return new AggregateSourceSnapshot(false, new List<string>(), new List<string>());

            return new AggregateSourceSnapshot(
                true,
                ReadJsonStringArray(doc.RootElement, "sourceReportIds"),
                ReadJsonStringArray(doc.RootElement, "sourceAssignmentIds"));
        }
        catch (JsonException)
        {
            return new AggregateSourceSnapshot(false, new List<string>(), new List<string>());
        }
    }

    private static AggregateDraftSummary? TryReadAggregateDraftSummary(string? summarySourceJson)
    {
        if (string.IsNullOrWhiteSpace(summarySourceJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(summarySourceJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var kind = ReadJsonString(doc.RootElement, "kind");
            if (!string.Equals(kind, "DYNAMIC_FORM_AGGREGATE_DRAFT", StringComparison.Ordinal))
                return null;

            if (!TryGetJsonProperty(doc.RootElement, "aggregateRequest", out var requestElement) ||
                requestElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var aggregateRequest = JsonSerializer.Deserialize<DynamicFormAggregateRequest>(
                requestElement.GetRawText(),
                _jsonOptions);
            if (aggregateRequest is null)
                return null;

            return new AggregateDraftSummary(
                WorkReportDataOrigin.Normalize(ReadJsonString(doc.RootElement, "dataOrigin")),
                aggregateRequest,
                ReadJsonString(doc.RootElement, "valueSelector"),
                ReadJsonString(doc.RootElement, "targetBlockId"),
                ReadJsonBool(doc.RootElement, "clearExistingValues"),
                ReadJsonIntArray(doc.RootElement, "targetIndexes"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool? ReadJsonBool(JsonElement element, string name)
    {
        if (!TryGetJsonProperty(element, name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static List<int> ReadJsonIntArray(JsonElement element, string name)
    {
        if (!TryGetJsonProperty(element, name, out var value) || value.ValueKind != JsonValueKind.Array)
            return new List<int>();

        return value
            .EnumerateArray()
            .Select(item =>
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number))
                    return (int?)number;
                if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out var parsed))
                    return parsed;
                return null;
            })
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    private static DynamicFormAggregateRequest NormalizeAggregateDraftRequest(DynamicFormAggregateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ScopeAssignmentId) ||
            string.IsNullOrWhiteSpace(req.DynamicFormTemplateId))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_AGGREGATE_DRAFT_REQUEST_INVALID,
                new { req.ScopeAssignmentId, req.DynamicFormTemplateId });
        }

        var periodScope = string.IsNullOrWhiteSpace(req.PeriodScopeMode)
            ? "ALL_PERIODS"
            : req.PeriodScopeMode.Trim().ToUpperInvariant();

        return new DynamicFormAggregateRequest
        {
            ScopeAssignmentId = req.ScopeAssignmentId.Trim(),
            ScopeMode = string.IsNullOrWhiteSpace(req.ScopeMode)
                ? "DIRECT_CHILDREN"
                : req.ScopeMode.Trim().ToUpperInvariant(),
            DynamicFormTemplateId = req.DynamicFormTemplateId.Trim(),
            BlockId = NormalizeBlockId(req.BlockId),
            TableMode = string.IsNullOrWhiteSpace(req.TableMode) ? null : req.TableMode.Trim().ToUpperInvariant(),
            MetricKeys = req.MetricKeys?
                .Select(x => x?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            PeriodScopeMode = periodScope,
            PeriodKey = NormalizeOptionalTextOrNull(req.PeriodKey),
            PeriodKeyFrom = NormalizeOptionalTextOrNull(req.PeriodKeyFrom),
            PeriodKeyTo = NormalizeOptionalTextOrNull(req.PeriodKeyTo),
            SourceStatusMode = "APPROVED_ONLY",
            SelectedUnitIds = req.SelectedUnitIds?
                .Select(NormalizeOptionalTextOrNull)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .Distinct(StringComparer.Ordinal)
                .ToList()
        };
    }

    private static string NormalizeAggregateDraftValueSelector(string? value)
    {
        var selector = value?.Trim().ToUpperInvariant();
        return selector switch
        {
            "COUNT" => "COUNT",
            "AVG" or "AVERAGE" => "AVERAGE",
            "MIN" => "MIN",
            "MAX" => "MAX",
            _ => "SUM"
        };
    }

    private static AggregateDraftBlockContract? ResolveAggregateDraftBlock(
        DynamicFormTemplate form,
        string targetBlockId)
    {
        var blocks = ReadAggregateDraftBlocks(form.BlocksJson);
        if (blocks.Count == 0 && !string.IsNullOrWhiteSpace(form.ExcelBlockJson))
            blocks.AddRange(ReadAggregateDraftBlocks(form.ExcelBlockJson));

        return blocks.FirstOrDefault(block =>
            string.Equals(block.BlockId, targetBlockId, StringComparison.Ordinal));
    }

    private static string ResolveTopLevelBlockId(DynamicFormTemplate form)
    {
        var blocks = ReadAggregateDraftBlocks(form.BlocksJson);
        if (blocks.Count > 0)
            return blocks[0].BlockId;

        var legacy = ReadAggregateDraftBlocks(form.ExcelBlockJson);
        return legacy.Count > 0 ? legacy[0].BlockId : "excel_block";
    }

    private static List<AggregateDraftBlockContract> ReadAggregateDraftBlocks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<AggregateDraftBlockContract>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.RootElement
                    .EnumerateArray()
                    .Select(ParseAggregateDraftBlock)
                    .Where(x => x is not null)
                    .Select(x => x!)
                    .ToList();
            }

            var block = ParseAggregateDraftBlock(doc.RootElement);
            return block is null ? new List<AggregateDraftBlockContract>() : new List<AggregateDraftBlockContract> { block };
        }
        catch (JsonException)
        {
            return new List<AggregateDraftBlockContract>();
        }
    }

    private static AggregateDraftBlockContract? ParseAggregateDraftBlock(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var blockId = NormalizeBlockId(ReadJsonString(element, "blockId") ?? ReadJsonString(element, "id"));
        var tableMode = NormalizeAggregateTableMode(ReadJsonString(element, "tableMode"));
        var w = ReadJsonInt(element, "w") ?? ReadJsonInt(element, "W") ?? 0;
        var h = ReadJsonInt(element, "h") ?? ReadJsonInt(element, "H") ?? 0;
        if (w <= 0 || h <= 0)
            return null;

        var dataRect = TryGetJsonProperty(element, "dataRect", out var dataRectNode) &&
                       dataRectNode.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(dataRectNode.GetRawText())
            : BuildDefaultDataRectNode(w, h);

        var indexMap = ReadAggregateDraftIndexMap(element, blockId, tableMode, w, h);
        var dynamicExcelTemplateId = ReadJsonString(element, "dynamicExcelTemplateId")
            ?? ReadJsonString(element, "excelBlockDynamicExcelTemplateId");

        return new AggregateDraftBlockContract(blockId, tableMode, w, h, dynamicExcelTemplateId, dataRect, indexMap);
    }

    private static string NormalizeAggregateTableMode(string? value)
    {
        var mode = value?.Trim().ToUpperInvariant();
        return mode is "APPEND_ROWS" or "APPEND_COLUMNS" or "MATRIX" or "SUMMARY_TEMPLATE"
            ? mode
            : "FIXED_GRID";
    }

    private static int? ReadJsonInt(JsonElement element, string name)
    {
        if (!TryGetJsonProperty(element, name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static List<AggregateDraftIndexMapItem> ReadAggregateDraftIndexMap(
        JsonElement block,
        string blockId,
        string tableMode,
        int w,
        int h)
    {
        var result = new List<AggregateDraftIndexMapItem>();
        if (TryGetJsonProperty(block, "indexMap", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var fallback = 0;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var index = ReadJsonInt(item, "index") ?? fallback;
                var rowKey = NormalizeMetricPart(ReadJsonString(item, "rowKey"), $"row_{fallback + 1}");
                var columnKey = NormalizeMetricPart(ReadJsonString(item, "columnKey"), "value");
                var metricKey = NormalizeOptionalTextOrNull(ReadJsonString(item, "metricKey"))
                    ?? BuildAggregateMetricKey(blockId, rowKey, columnKey);
                result.Add(new AggregateDraftIndexMapItem(index, rowKey, columnKey, metricKey));
                fallback++;
            }
        }

        if (result.Count > 0 || tableMode is not ("FIXED_GRID" or "MATRIX"))
            return result
                .GroupBy(x => x.MetricKey, StringComparer.Ordinal)
                .Select(x => x.First())
                .OrderBy(x => x.Index)
                .ToList();

        return Enumerable.Range(0, Math.Max(0, w * h))
            .Select(index =>
            {
                var rowKey = $"row_{(index / Math.Max(1, w)) + 1}";
                var columnKey = $"col_{(index % Math.Max(1, w)) + 1}";
                return new AggregateDraftIndexMapItem(index, rowKey, columnKey, BuildAggregateMetricKey(blockId, rowKey, columnKey));
            })
            .ToList();
    }

    private static JsonObject BuildDefaultDataRectNode(int w, int h)
        => new()
        {
            ["r0"] = 0,
            ["c0"] = 0,
            ["r1"] = Math.Max(0, h - 1),
            ["c1"] = Math.Max(0, w - 1)
        };

    private static string NormalizeMetricPart(string? value, string fallback)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static string BuildAggregateMetricKey(string blockId, string rowKey, string columnKey)
        => $"table:{blockId}.row:{rowKey}.column:{columnKey}";

    private static List<decimal?> DeserializeValues1D(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<decimal?>();

        try
        {
            return JsonSerializer.Deserialize<List<decimal?>>(json, _jsonOptions) ?? new List<decimal?>();
        }
        catch (JsonException)
        {
            return new List<decimal?>();
        }
    }

    private static List<decimal?> NormalizeDecimalValues(IReadOnlyList<decimal?> values, int length)
    {
        var result = CreateEmptyValues1D(length, 1);
        for (var i = 0; i < Math.Min(length, values.Count); i++)
            result[i] = values[i];
        return result;
    }

    private static List<decimal?> ExtractBlockDecimalValues(string? tableValuesJson, string blockId)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson))
            return new List<decimal?>();

        try
        {
            using var doc = JsonDocument.Parse(tableValuesJson);
            if (!TryGetJsonProperty(doc.RootElement, "blocks", out var blocks) ||
                blocks.ValueKind != JsonValueKind.Array)
            {
                return new List<decimal?>();
            }

            foreach (var block in blocks.EnumerateArray())
            {
                if (!string.Equals(NormalizeBlockId(ReadJsonString(block, "blockId")), blockId, StringComparison.Ordinal))
                    continue;

                if (!TryGetJsonProperty(block, "values1D", out var values) ||
                    values.ValueKind != JsonValueKind.Array)
                {
                    return new List<decimal?>();
                }

                return values.EnumerateArray().Select(ToNullableDecimal).ToList();
            }
        }
        catch (JsonException)
        {
            return new List<decimal?>();
        }

        return new List<decimal?>();
    }

    private static decimal? ToNullableDecimal(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static void ApplyAggregateRowsToValues(
        List<decimal?> targetValues,
        IReadOnlyCollection<DynamicFormAggregateRowDto> rows,
        AggregateDraftBlockContract block,
        string valueSelector)
    {
        foreach (var row in rows)
        {
            var value = SelectAggregateDraftValue(row, valueSelector);
            if (!value.HasValue)
                continue;

            var index = ResolveAggregateDraftValueIndex(row, block);
            if (index < 0 || index >= targetValues.Count)
                continue;

            targetValues[index] = value.Value;
        }
    }

    private static decimal? SelectAggregateDraftValue(DynamicFormAggregateRowDto row, string selector)
    {
        return selector switch
        {
            "COUNT" => row.Count,
            "AVERAGE" => row.Average,
            "MIN" => row.Min,
            "MAX" => row.Max,
            _ => row.Sum
        };
    }

    private static int ResolveAggregateDraftValueIndex(DynamicFormAggregateRowDto row, AggregateDraftBlockContract block)
    {
        if (string.Equals(block.TableMode, "SUMMARY_TEMPLATE", StringComparison.Ordinal) &&
            row.OutputRowIndex.HasValue)
        {
            return row.OutputRowIndex.Value * Math.Max(1, block.W);
        }

        if (string.Equals(block.TableMode, "APPEND_COLUMNS", StringComparison.Ordinal))
            return row.Index * Math.Max(1, block.W);

        return row.Index;
    }

    private static string BuildAggregateDraftTableValuesJson(
        WorkAssignmentReport report,
        DynamicFormTemplate form,
        AggregateDraftBlockContract block,
        List<decimal?> values,
        DynamicFormAggregateResponse aggregate)
    {
        var root = ParseTableValuesRoot(report.TableValuesJson) ?? new JsonObject();
        root["dynamicFormTemplateId"] = report.DynamicFormTemplateId ?? form.Id;
        root["dynamicFormTemplateCode"] = report.DynamicFormTemplateCode ?? form.Code;
        root["dynamicFormTemplateName"] = report.DynamicFormTemplateName ?? form.Name;
        root["updatedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        var blocks = root["blocks"] as JsonArray;
        if (blocks is null)
        {
            blocks = new JsonArray();
            root["blocks"] = blocks;
        }

        var existingRowLabels = ExtractExistingBlockProperty(report.TableValuesJson, block.BlockId, "rowLabels")
            ?? new JsonArray();

        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            var item = blocks[i] as JsonObject;
            var itemBlockId = item?["blockId"]?.GetValue<string>();
            if (string.Equals(NormalizeBlockId(itemBlockId), block.BlockId, StringComparison.Ordinal))
                blocks.RemoveAt(i);
        }

        blocks.Add(BuildAggregateDraftTableBlockNode(block, values, aggregate, existingRowLabels));
        return root.ToJsonString(_jsonOptions);
    }

    private static JsonObject? ParseTableValuesRoot(string? tableValuesJson)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson))
            return null;

        try
        {
            return JsonNode.Parse(tableValuesJson) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode? ExtractExistingBlockProperty(string? tableValuesJson, string blockId, string propertyName)
    {
        var root = ParseTableValuesRoot(tableValuesJson);
        if (root?["blocks"] is not JsonArray blocks)
            return null;

        foreach (var node in blocks)
        {
            if (node is not JsonObject block)
                continue;

            var itemBlockId = block["blockId"]?.GetValue<string>();
            if (!string.Equals(NormalizeBlockId(itemBlockId), blockId, StringComparison.Ordinal))
                continue;

            return block[propertyName]?.DeepClone();
        }

        return null;
    }

    private static JsonObject BuildAggregateDraftTableBlockNode(
        AggregateDraftBlockContract block,
        List<decimal?> values,
        DynamicFormAggregateResponse aggregate,
        JsonNode existingRowLabels)
    {
        var indexMap = BuildAggregateDraftIndexMap(block, aggregate.Rows);
        return new JsonObject
        {
            ["blockId"] = block.BlockId,
            ["dynamicExcelTemplateId"] = block.DynamicExcelTemplateId,
            ["tableMode"] = block.TableMode,
            ["w"] = block.W,
            ["h"] = block.H,
            ["dataRect"] = block.DataRect?.DeepClone() ?? BuildDefaultDataRectNode(block.W, block.H),
            ["values1D"] = JsonSerializer.SerializeToNode(values, _jsonOptions),
            ["indexMap"] = JsonSerializer.SerializeToNode(indexMap, _jsonOptions),
            ["rowLabels"] = existingRowLabels,
            ["rows"] = JsonSerializer.SerializeToNode(BuildAggregateDraftAppendRows(block, values), _jsonOptions),
            ["columns"] = JsonSerializer.SerializeToNode(BuildAggregateDraftAppendColumns(block, values), _jsonOptions),
            ["cells"] = JsonSerializer.SerializeToNode(BuildAggregateDraftMatrixCells(block, values, indexMap), _jsonOptions),
            ["aggregateMeta"] = JsonSerializer.SerializeToNode(new
            {
                aggregate.Meta.ScopeAssignmentId,
                aggregate.Meta.ScopeMode,
                aggregate.Meta.PeriodScopeMode,
                aggregate.Meta.PeriodKey,
                aggregate.Meta.PeriodKeyFrom,
                aggregate.Meta.PeriodKeyTo,
                aggregate.Meta.SourceStatusMode,
                aggregate.Meta.SourceReportCount,
                aggregate.Meta.MetricCount
            }, _jsonOptions)
        };
    }

    private static List<AggregateDraftIndexMapItem> BuildAggregateDraftIndexMap(
        AggregateDraftBlockContract block,
        IReadOnlyCollection<DynamicFormAggregateRowDto> rows)
    {
        if (block.IndexMap.Count > 0)
            return block.IndexMap;

        return rows
            .Select(row => new AggregateDraftIndexMapItem(
                ResolveAggregateDraftValueIndex(row, block),
                NormalizeMetricPart(row.RowKey, $"row_{row.Index + 1}"),
                NormalizeMetricPart(row.ColumnKey, "value"),
                row.SourceMetricKey ?? row.MetricKey))
            .GroupBy(x => x.MetricKey, StringComparer.Ordinal)
            .Select(x => x.First())
            .OrderBy(x => x.Index)
            .ToList();
    }

    private static List<object> BuildAggregateDraftAppendRows(AggregateDraftBlockContract block, List<decimal?> values)
    {
        if (!string.Equals(block.TableMode, "APPEND_ROWS", StringComparison.Ordinal))
            return new List<object>();

        var rows = new List<object>();
        for (var r = 0; r < block.H; r++)
        {
            var cells = new Dictionary<string, decimal?>(StringComparer.Ordinal);
            for (var c = 0; c < block.W; c++)
            {
                var value = values[r * block.W + c];
                if (value.HasValue)
                    cells[$"col_{c + 1}"] = value.Value;
            }

            if (cells.Count > 0)
            {
                rows.Add(new
                {
                    rowInstanceId = $"{block.BlockId}:summary-row:{r + 1}",
                    rowOrder = r + 1,
                    rowKey = $"summary:R{r + 1}",
                    rowLabelCodes = Array.Empty<string>(),
                    cells
                });
            }
        }

        return rows;
    }

    private static List<object> BuildAggregateDraftAppendColumns(AggregateDraftBlockContract block, List<decimal?> values)
    {
        if (!string.Equals(block.TableMode, "APPEND_COLUMNS", StringComparison.Ordinal))
            return new List<object>();

        var columns = new List<object>();
        for (var c = 0; c < block.W; c++)
        {
            var cells = new Dictionary<string, decimal?>(StringComparer.Ordinal);
            for (var r = 0; r < block.H; r++)
            {
                var value = values[r * block.W + c];
                if (value.HasValue)
                    cells[$"row_{r + 1}"] = value.Value;
            }

            if (cells.Count > 0)
            {
                columns.Add(new
                {
                    columnInstanceId = $"{block.BlockId}:summary-column:{c + 1}",
                    columnOrder = c + 1,
                    columnKey = $"summary:C{c + 1}",
                    columnLabelCodes = Array.Empty<string>(),
                    cells
                });
            }
        }

        return columns;
    }

    private static List<object> BuildAggregateDraftMatrixCells(
        AggregateDraftBlockContract block,
        List<decimal?> values,
        List<AggregateDraftIndexMapItem> indexMap)
    {
        if (!string.Equals(block.TableMode, "MATRIX", StringComparison.Ordinal))
            return new List<object>();

        var metricByIndex = indexMap.ToDictionary(x => x.Index, x => x);
        var cells = new List<object>();
        for (var i = 0; i < values.Count; i++)
        {
            if (!values[i].HasValue)
                continue;

            if (!metricByIndex.TryGetValue(i, out var metric))
            {
                metric = new AggregateDraftIndexMapItem(
                    i,
                    $"row_{(i / Math.Max(1, block.W)) + 1}",
                    $"col_{(i % Math.Max(1, block.W)) + 1}",
                    BuildAggregateMetricKey(block.BlockId, $"row_{(i / Math.Max(1, block.W)) + 1}", $"col_{(i % Math.Max(1, block.W)) + 1}"));
            }

            cells.Add(new
            {
                rowAxisKey = "row",
                rowKey = metric.RowKey,
                columnAxisKey = "column",
                columnKey = metric.ColumnKey,
                metricKey = metric.MetricKey,
                value = values[i]
            });
        }

        return cells;
    }

    private static string? BuildAggregateDraftContributionPolicyJson(
        string dataOrigin,
        IReadOnlyCollection<DynamicFormAggregateRowDto> rows,
        string blockId)
    {
        if (!string.Equals(WorkReportDataOrigin.Normalize(dataOrigin), WorkReportDataOrigin.PartialMapping, StringComparison.Ordinal))
            return null;

        var rules = rows
            .Select(row => new
            {
                targetKind = "TABLE_METRIC",
                blockId,
                metricKey = row.SourceMetricKey ?? row.MetricKey,
                rowKey = NormalizeOptionalTextOrNull(row.RowKey),
                columnKey = NormalizeOptionalTextOrNull(row.ColumnKey),
                mode = WorkReportCumulativeContributionMode.Exclude
            })
            .GroupBy(x => $"{x.blockId}|{x.metricKey}|{x.rowKey}|{x.columnKey}", StringComparer.Ordinal)
            .Select(x => x.First())
            .ToList();

        return JsonSerializer.Serialize(new
        {
            defaultMode = WorkReportCumulativeContributionMode.Include,
            rules
        }, _jsonOptions);
    }

    private static string BuildAggregateDraftSummarySourceJson(
        string dataOrigin,
        DynamicFormAggregateRequest aggregateRequest,
        DynamicFormAggregateResponse aggregate,
        AggregateDraftBlockContract block,
        string valueSelector,
        string targetBlockId,
        bool clearExistingValues)
    {
        var sourceReportIds = aggregate.Sources
            .Select(x => x.ReportId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sourceAssignmentIds = aggregate.Sources
            .Select(x => x.WorkAssignmentId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var targetIndexes = aggregate.Rows
            .Select(row => ResolveAggregateDraftValueIndex(row, block))
            .Where(index => index >= 0 && index < block.W * block.H)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            kind = "DYNAMIC_FORM_AGGREGATE_DRAFT",
            dataOrigin = WorkReportDataOrigin.Normalize(dataOrigin),
            appliedAtUtc = DateTime.UtcNow,
            valueSelector,
            targetBlockId,
            clearExistingValues,
            aggregateRequest = new DynamicFormAggregateRequest
            {
                ScopeAssignmentId = aggregateRequest.ScopeAssignmentId,
                ScopeMode = aggregateRequest.ScopeMode,
                DynamicFormTemplateId = aggregateRequest.DynamicFormTemplateId,
                BlockId = aggregateRequest.BlockId,
                TableMode = aggregateRequest.TableMode,
                MetricKeys = aggregateRequest.MetricKeys,
                PeriodScopeMode = aggregateRequest.PeriodScopeMode,
                PeriodKey = aggregateRequest.PeriodKey,
                PeriodKeyFrom = aggregateRequest.PeriodKeyFrom,
                PeriodKeyTo = aggregateRequest.PeriodKeyTo,
                SourceStatusMode = "APPROVED_ONLY",
                SelectedUnitIds = aggregate.Meta.SelectedUnitIds.Count > 0
                    ? aggregate.Meta.SelectedUnitIds
                    : aggregateRequest.SelectedUnitIds
            },
            sourceReportIds,
            sourceAssignmentIds,
            targetIndexes,
            rowCount = aggregate.Rows.Count,
            sourceReportCount = aggregate.Sources.Count
        }, _jsonOptions);
    }

    private static string NormalizeUserCreatedPeriodKey(string? value, DateTime reportDate)
    {
        var key = value?.Trim();
        if (!string.IsNullOrWhiteSpace(key))
            return key;

        return reportDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    private static string NormalizePeriodInstanceKey(WorkReportPeriod period)
        => string.IsNullOrWhiteSpace(period.PeriodInstanceKey)
            ? period.PeriodKey
            : period.PeriodInstanceKey.Trim();

    private static string NormalizeReportPeriodInstanceKey(WorkAssignmentReport report)
        => string.IsNullOrWhiteSpace(report.PeriodInstanceKey)
            ? report.PeriodKey
            : report.PeriodInstanceKey.Trim();

    private static string NormalizePeriodKind(string? value)
        => WorkReportPeriodKind.IsUserCreated(value)
            ? WorkReportPeriodKind.UserCreated
            : WorkReportPeriodKind.Scheduled;

    private sealed record AggregateDraftBlockContract(
        string BlockId,
        string TableMode,
        int W,
        int H,
        string? DynamicExcelTemplateId,
        JsonNode? DataRect,
        List<AggregateDraftIndexMapItem> IndexMap);

    private sealed record AggregateDraftIndexMapItem(
        int Index,
        string RowKey,
        string ColumnKey,
        string MetricKey);

    private sealed record AggregateSourceSnapshot(
        bool IsAggregate,
        List<string> ReportIds,
        List<string> AssignmentIds);

    private sealed record AggregateDraftSummary(
        string DataOrigin,
        DynamicFormAggregateRequest AggregateRequest,
        string? ValueSelector,
        string? TargetBlockId,
        bool? ClearExistingValues,
        List<int> TargetIndexes);

    private sealed record DynamicFormAggregateDraftProjection(
        List<decimal?> TopLevelValues,
        string TableValuesJson,
        string DataOrigin,
        string ContributionMode,
        string? ContributionPolicyJson,
        string SummarySourceJson);

    private sealed record PeriodDefinition(
        string PeriodKey,
        DateTime? PeriodStart,
        DateTime? PeriodEnd,
        DateTime? DueAtUtc);

    private async Task TouchMaterializeJobsIfNoPeriodsAsync(
        List<WorkTemplateAssignee> bindings,
        string actorUserId,
        CancellationToken ct)
    {
        if (bindings.Count == 0)
            return;

        var bindingIds = bindings
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .Select(x => x.Id!)
            .Distinct()
            .ToList();

        if (bindingIds.Count == 0)
            return;

        var hasAnyPeriod = await _ctx.WorkReportPeriods
            .Find(x => bindingIds.Contains(x.WorkTemplateAssigneeId) && !x.IsDeleted)
            .AnyAsync(ct);

        if (hasAnyPeriod)
            return;

        var assignmentIds = bindings
            .Where(x => !string.IsNullOrWhiteSpace(x.WorkAssignmentId))
            .Select(x => x.WorkAssignmentId!)
            .Distinct()
            .ToList();

        _log.LogInformation(
            "My report materialize touch fallback. actorUserId={actorUserId} assignmentCount={assignmentCount} bindingCount={bindingCount}",
            actorUserId,
            assignmentIds.Count,
            bindingIds.Count);

        foreach (var assignmentId in assignmentIds)
            await _materializeJob.EnqueueOrTouchByAssignmentIdAsync(assignmentId, actorUserId, ct);
    }
}
