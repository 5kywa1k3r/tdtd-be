using MongoDB.Driver;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using System.Globalization;
using System.Text.Json;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments.Queue;
using tdtd_be.Services.WorkAssignments.Runtime;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Services.WorkAssignmentReports;

public sealed class WorkAssignmentReportService : IWorkAssignmentReportService
{
    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentQueueService _queueService;
    private readonly IWorkAssignmentStatusSyncService _statusSync;
    private readonly IWorkAssignmentMaterializeJobService _materializeJob;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
    private readonly IDocRoleReadModelFreshnessService _docRoleReadModelFreshness;
    private readonly IWorkStatusOperationLogService _statusLog;
    private readonly IWorkReportLabelStatisticsService _labelStatistics;
    private readonly IWorkReportTableStatisticsService _tableStatistics;
    private readonly IWorkReportFieldStatisticsService _fieldStatistics;
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
        IWorkReportLabelStatisticsService labelStatistics,
        IWorkReportTableStatisticsService tableStatistics,
        IWorkReportFieldStatisticsService fieldStatistics,
        ILogger<WorkAssignmentReportService> log)
    {
        _ctx = ctx;
        _queueService = queueService;
        _statusSync = statusSync;
        _materializeJob = materializeJob;
        _docRoleReadModelProjection = docRoleReadModelProjection;
        _docRoleReadModelFreshness = docRoleReadModelFreshness;
        _statusLog = statusLog;
        _labelStatistics = labelStatistics;
        _tableStatistics = tableStatistics;
        _fieldStatistics = fieldStatistics;
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
            throw new ArgumentException("workId không được trống.", nameof(workId));

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
            var regex = new MongoDB.Bson.BsonRegularExpression(q, "i");

            filter &= Builders<MyReportTemplateListDocRole>.Filter.Or(
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

        var items = await _ctx.MyReportTemplateListDocRoles
            .Find(filter)
            .Sort(BuildTemplateListDocRoleSort(req.SortField, req.SortDirection))
            .Skip(page * pageSize)
            .Limit(pageSize)
            .Project(x => new MyReportTemplateRow
            {
                DynamicExcelId = x.DynamicExcelId,
                DynamicExcelCode = x.DynamicExcelCode,
                DynamicExcelName = x.DynamicExcelName,
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
            })
            .ToListAsync(ct);

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
            ?? throw new InvalidOperationException("Không tìm thấy WorkAssignment.");

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
            throw new UnauthorizedAccessException("Bạn không có quyền truy cập báo cáo của assignment này.");

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
        string dynamicExcelId,
        string actorUserId,
        CancellationToken ct)
    {
        return await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkId == workId &&
                x.DynamicExcelId == dynamicExcelId &&
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
        var access = await EnsureAssignmentReportAccessAsync(report.WorkAssignmentId, actorUserId, ct);

        var isAssignee = access.isAssignee || (!string.IsNullOrWhiteSpace(report.AssigneeUserId) && report.AssigneeUserId == actorUserId);
        if (!access.isOwner && !isAssignee)
            throw new UnauthorizedAccessException("Bạn không có quyền truy cập báo cáo này.");

        return (access.assignment, access.isOwner, isAssignee);
    }
    public async Task<MyReportTemplateDetailResponse> GetMyReportTemplateDetailAsync(
        string workId,
        string dynamicExcelId,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(workId))
            throw new ArgumentException("workId không được trống.", nameof(workId));

        if (string.IsNullOrWhiteSpace(dynamicExcelId))
            throw new ArgumentException("dynamicExcelId không được trống.", nameof(dynamicExcelId));

        var bindings = await LoadVisibleReportBindingsByTemplateAsync(workId, dynamicExcelId, actorUserId, ct);

        if (bindings.Count == 0)
            throw new UnauthorizedAccessException("Bạn không có quyền xem chi tiết biểu mẫu báo cáo này.");

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

        var template = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == dynamicExcelId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (template is null)
            throw new InvalidOperationException("Không tìm thấy DynamicExcelTemplate.");

        return new MyReportTemplateDetailResponse
        {
            WorkId = workId,
            DynamicExcelId = dynamicExcelId,
            DynamicExcelCode = template.Code,
            DynamicExcelName = template.Name,
            WorkTemplateAssigneeId = primaryBinding.Id,
            WorkAssignmentId = primaryBinding.WorkAssignmentId,
            TemplateSnapshotJson = JsonSerializer.Serialize(BuildTemplateSnapshot(template), _jsonOptions),
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
            throw new ArgumentException("workReportPeriodId không được trống.", nameof(workReportPeriodId));

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == workReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (period is null)
            throw new InvalidOperationException("Không tìm thấy kỳ báo cáo.");

        await EnsurePeriodAccessAsync(period, actorUserId, ct);

        if (period.AssigneeUserId != actorUserId)
            throw new UnauthorizedAccessException("Bạn không có quyền mở kỳ báo cáo này.");

        if (!period.IsActive)
            throw new InvalidOperationException("Kỳ báo cáo không còn hiệu lực.");

        if (!string.IsNullOrWhiteSpace(period.CurrentReportId))
        {
            var existed = await _ctx.WorkAssignmentReports
                .Find(x => x.Id == period.CurrentReportId && !x.IsDeleted)
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
            throw new ArgumentException("workAssignmentId không được trống.", nameof(workAssignmentId));

        req ??= new InitWorkAssignmentReportRequest();

        if (string.IsNullOrWhiteSpace(req.PeriodKey))
            throw new ArgumentException("PeriodKey không được trống.", nameof(req));

        var binding = await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkAssignmentId == workAssignmentId &&
                x.AssigneeUserId == actorUserId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (binding is null)
            throw new UnauthorizedAccessException("Bạn không có binding báo cáo hiện hành cho assignment này.");

        var existedPeriod = await _ctx.WorkReportPeriods
            .Find(x =>
                x.WorkTemplateAssigneeId == binding.Id &&
                x.PeriodKey == req.PeriodKey.Trim() &&
                (x.PeriodKind == null || x.PeriodKind == WorkReportPeriodKind.Scheduled) &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (existedPeriod is null)
            throw new InvalidOperationException("Kỳ báo cáo chưa được materialize hoặc không tồn tại.");

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
            throw new ArgumentException("workAssignmentId không được trống.", nameof(workAssignmentId));

        req ??= new CreateUserCreatedReportRequest();

        var binding = await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkAssignmentId == workAssignmentId &&
                x.AssigneeUserId == actorUserId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (binding is null)
            throw new UnauthorizedAccessException("Bạn không có binding báo cáo hiện hành cho assignment này.");

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == workAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Không tìm thấy WorkAssignment.");

        if (!assignment.IsActive || !binding.IsActive)
            throw new InvalidOperationException("Phân công báo cáo không còn hiệu lực.");

        if (!assignment.AllowUserCreatedReports || !binding.AllowUserCreatedReports)
            throw new InvalidOperationException("Phân công này chưa cho phép tạo báo cáo chủ động.");

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
                ?? throw new InvalidOperationException("Không tìm thấy kỳ định kỳ được liên kết.");
        }

        var now = DateTime.UtcNow;
        var reportDate = (req.ReportDate ?? now).Date;
        var periodKey = NormalizeUserCreatedPeriodKey(req.PeriodKey, reportDate);
        var periodStart = req.PeriodStart ?? reportDate;
        var periodEnd = req.PeriodEnd ?? reportDate;
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
            throw new ArgumentException("Id không được trống.", nameof(id));

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Không tìm thấy WorkAssignmentReport.");

        var reportAccess = await EnsureReportAccessAsync(entity, actorUserId, ct);
        if (!reportAccess.isAssignee || entity.AssigneeUserId != actorUserId)
            throw new UnauthorizedAccessException("Bạn không có quyền xóa báo cáo này.");

        if (entity.Status == WorkAssignmentReportStatus.Approved)
            throw new InvalidOperationException("Báo cáo đã được duyệt, không thể xóa.");

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Không tìm thấy kỳ báo cáo.");

        if (!WorkReportPeriodKind.IsUserCreated(period.PeriodKind))
            throw new InvalidOperationException("Chỉ được xóa báo cáo chủ động.");

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
            throw new ArgumentException("Id không được trống.", nameof(id));

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw new InvalidOperationException("Không tìm thấy WorkAssignmentReport.");

        await EnsureReportAccessAsync(entity, actorUserId, ct);

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        await _docRoleReadModelFreshness.EnsureReportPeriodFreshAsync(period, entity, actorUserId, ct);

        return await MapToResponseAsync(entity, period, ct);
    }

    public async Task<List<WorkAssignmentReportListRow>> GetByAssignmentAsync(
        string workAssignmentId,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(workAssignmentId))
            throw new ArgumentException("workAssignmentId không được trống.", nameof(workAssignmentId));

        var access = await EnsureAssignmentReportAccessAsync(workAssignmentId, actorUserId, ct);

        var reportFilter = Builders<WorkAssignmentReport>.Filter.Eq(x => x.WorkAssignmentId, workAssignmentId)
            & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false);

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
            throw new ArgumentException("Id không được trống.", nameof(id));

        if (req is null)
            throw new ArgumentNullException(nameof(req));

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw new InvalidOperationException("Không tìm thấy WorkAssignmentReport.");

        var reportAccess = await EnsureReportAccessAsync(entity, actorUserId, ct);
        if (!reportAccess.isAssignee || entity.AssigneeUserId != actorUserId)
            throw new UnauthorizedAccessException("Bạn không có quyền sửa báo cáo này.");

        if (entity.Status != WorkAssignmentReportStatus.Draft)
            throw new InvalidOperationException("Chỉ được lưu khi báo cáo đang ở trạng thái Draft");

        var expectedLength = entity.W * entity.H;
        var actualLength = req.Values1D?.Count ?? 0;

        if (actualLength != expectedLength)
            throw new InvalidOperationException($"Values1D không hợp lệ. Expected={expectedLength}, Actual={actualLength}.");

        var now = DateTime.UtcNow;
        var values1DJson = JsonSerializer.Serialize(req.Values1D, _jsonOptions);
        var fromStatus = entity.Status;
        var nextStatus = WorkAssignmentReportStatus.Draft;

        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.Values1DJson, values1DJson)
                .Set(x => x.FieldValuesJson, req.FieldValuesJson)
                .Set(x => x.TableValuesJson, req.TableValuesJson)
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
        entity.FieldValuesJson = req.FieldValuesJson;
        entity.TableValuesJson = req.TableValuesJson;
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

    public async Task<WorkAssignmentReportResponse> SubmitAsync(
        string id,
        SubmitWorkAssignmentReportRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id không được trống.", nameof(id));

        req ??= new SubmitWorkAssignmentReportRequest();

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw new InvalidOperationException("Không tìm thấy WorkAssignmentReport.");

        var reportAccess = await EnsureReportAccessAsync(entity, actorUserId, ct);
        if (!reportAccess.isAssignee || entity.AssigneeUserId != actorUserId)
            throw new UnauthorizedAccessException("Bạn không có quyền nộp báo cáo này.");

        if (entity.Status != WorkAssignmentReportStatus.Draft)
            throw new InvalidOperationException("Chỉ được nộp khi báo cáo đang ở trạng thái nháp");

        var now = DateTime.UtcNow;
        var fromStatus = entity.Status;

        if (req.Values1D is { Count: > 0 })
        {
            var expectedLength = entity.W * entity.H;
            if (req.Values1D.Count != expectedLength)
                throw new InvalidOperationException($"Values1D không hợp lệ. Expected={expectedLength}, Actual={req.Values1D.Count}.");

            entity.Values1DJson = JsonSerializer.Serialize(req.Values1D, _jsonOptions);
        }

        if (req.FieldValuesJson is not null)
            entity.FieldValuesJson = req.FieldValuesJson;

        if (req.TableValuesJson is not null)
            entity.TableValuesJson = req.TableValuesJson;

        var isLate = entity.DueAtUtc.HasValue && now > entity.DueAtUtc.Value;
        var lateReason = string.IsNullOrWhiteSpace(req.LateReason) ? entity.LateReason : req.LateReason?.Trim();

        if (isLate && string.IsNullOrWhiteSpace(lateReason))
            throw new InvalidOperationException("Báo cáo nộp trễ hạn bắt buộc phải có lý do.");

        entity.Status = WorkAssignmentReportStatus.Submitted;
        entity.CurrentProgressStatus = req.CurrentProgressStatus ?? entity.CurrentProgressStatus;
        entity.ReportReason = req.ReportReason ?? entity.ReportReason;
        entity.Difficulties = req.Difficulties ?? entity.Difficulties;
        entity.ProposedSolution = req.ProposedSolution ?? entity.ProposedSolution;
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
                .Set(x => x.Status, entity.Status)
                .Set(x => x.CurrentProgressStatus, entity.CurrentProgressStatus)
                .Set(x => x.ReportReason, entity.ReportReason)
                .Set(x => x.Difficulties, entity.Difficulties)
                .Set(x => x.ProposedSolution, entity.ProposedSolution)
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

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

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

        return await MapToResponseAsync(entity, period, ct);
    }

    private async Task<WorkAssignment> LoadReviewNodeAsync(
        WorkAssignment assignment,
        string reviewerUserId,
        CancellationToken ct)
    {
        if (string.Equals(assignment.CreatedByUserId, reviewerUserId, StringComparison.Ordinal))
            return assignment;

        var canReviewByRuntimeBinding = await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkAssignmentId == assignment.Id &&
                x.CreatedByUserId == reviewerUserId &&
                !x.IsDeleted)
            .AnyAsync(ct);

        if (canReviewByRuntimeBinding)
            return assignment;

        WorkAssignmentReviewPermissionHelper.EnsureCanReviewOnNode(assignment, reviewerUserId);
        return assignment;
    }

    public async Task<List<WorkAssignmentReportLogRow>> GetLogsAsync(
    string reportId,
    string actorUserId,
    CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(reportId))
            throw new ArgumentException("reportId không được trống.", nameof(reportId));

        var report = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (report is null)
            throw new InvalidOperationException("Không tìm thấy WorkAssignmentReport.");

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Không tìm thấy WorkAssignment.");

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

        if (!isAssignee && !canReview)
            throw new UnauthorizedAccessException("Bạn không có quyền xem log của báo cáo này.");

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
            throw new ArgumentException("Id không được trống.", nameof(id));

        req ??= new AcceptWorkAssignmentReportRequest();

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw new InvalidOperationException("Không tìm thấy WorkAssignmentReport.");

        if (entity.AssigneeUserId == actorUserId)
            throw new UnauthorizedAccessException("Người báo cáo không thể tự duyệt báo cáo của chính mình.");

        if (entity.Status != WorkAssignmentReportStatus.Submitted)
            throw new InvalidOperationException("Chỉ báo cáo đã nộp mới được duyệt.");

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == entity.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Không tìm thấy WorkAssignment.");

        await LoadReviewNodeAsync(assignment, actorUserId, ct);

        var now = DateTime.UtcNow;
        var lateReason = string.IsNullOrWhiteSpace(req.LateReasonOverride)
            ? entity.LateReason
            : req.LateReasonOverride.Trim();

        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.Status, WorkAssignmentReportStatus.Approved)
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
        entity.ReviewerComment = req.ReviewerComment;
        entity.LateReason = lateReason;
        entity.ReturnedAtUtc = null;
        entity.ReturnedByUserId = null;
        entity.ApprovedAtUtc = now;
        entity.ApprovedByUserId = actorUserId;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = actorUserId;

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (period is not null)
        {
            var nextPeriodStatus = ResolveApprovedPeriodStatus(period, entity, now);

            await _ctx.WorkReportPeriods.UpdateOneAsync(
                x => x.Id == period.Id && !x.IsDeleted,
                Builders<WorkReportPeriod>.Update
                    .Set(x => x.Status, nextPeriodStatus)
                    .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus))
                    .Set(x => x.LastReviewedAtUtc, now)
                    .Set(x => x.ReviewerComment, req.ReviewerComment)
                    .Set(x => x.AcceptedLateReason, lateReason)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId),
                cancellationToken: ct);

            period.Status = nextPeriodStatus;
            period.IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus);
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
            throw new ArgumentException("Id không được trống.", nameof(id));

        req ??= new ReturnWorkAssignmentReportRequest();

        if (string.IsNullOrWhiteSpace(req.ReturnReason))
            throw new InvalidOperationException("Trả lại báo cáo bắt buộc phải có lý do.");

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw new InvalidOperationException("Không tìm thấy WorkAssignmentReport.");

        if (entity.AssigneeUserId == actorUserId)
            throw new UnauthorizedAccessException("Người báo cáo không thể tự trả lại báo cáo của chính mình.");

        if (entity.Status != WorkAssignmentReportStatus.Submitted)
            throw new InvalidOperationException("Chỉ báo cáo đã nộp mới được trả lại.");

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == entity.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Không tìm thấy WorkAssignment.");

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
            throw new ArgumentException("Id không được trống.", nameof(id));

        req ??= new ReturnWorkAssignmentReportRequest();

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Không tìm thấy WorkAssignmentReport.");

        if (entity.AssigneeUserId != actorUserId)
            throw new UnauthorizedAccessException("Bạn không có quyền thu hồi báo cáo này.");

        if (entity.Status != WorkAssignmentReportStatus.Submitted)
            throw new InvalidOperationException("Chỉ báo cáo đã nộp và chưa duyệt mới được thu hồi.");

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
            throw new InvalidOperationException("Không tìm thấy WorkAssignment của kỳ báo cáo.");

        if (!assignment.IsActive)
            throw new InvalidOperationException("WorkAssignment đã dừng hiệu lực, không thể tạo draft mới.");

        var template = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == period.DynamicExcelId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (template is null)
            throw new InvalidOperationException("Không tìm thấy DynamicExcelTemplate.");

        var existedCurrent = await _ctx.WorkAssignmentReports
            .Find(x =>
                x.WorkReportPeriodId == period.Id &&
                x.IsCurrent &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (existedCurrent is not null)
        {
            await _docRoleReadModelProjection.RebuildReportPeriodAsync(period.Id, actorUserId, ct);
            return existedCurrent;
        }

        var now = DateTime.UtcNow;
        var periodInstanceKey = NormalizePeriodInstanceKey(period);
        var periodKind = NormalizePeriodKind(period.PeriodKind);

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
            PeriodStart = period.PeriodStart,
            PeriodEnd = period.PeriodEnd,
            DueAtUtc = period.DueAtUtc,

            Status = WorkAssignmentReportStatus.Draft,
            ScheduleSnapshotJson = JsonSerializer.Serialize(BuildScheduleSnapshot(assignment), _jsonOptions),

            DynamicExcelTemplateId = template.Id,
            DynamicExcelTemplateCode = template.Code,
            DynamicExcelTemplateName = template.Name,

            DataRectR0 = template.DataRectR0,
            DataRectC0 = template.DataRectC0,
            DataRectR1 = template.DataRectR1,
            DataRectC1 = template.DataRectC1,
            W = template.W,
            H = template.H,

            Values1DJson = JsonSerializer.Serialize(CreateEmptyValues1D(template.W, template.H), _jsonOptions),

            VersionNo = Math.Max(1, period.ReportVersionCount + 1),
            IsCurrent = true,

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

    private async Task<WorkAssignmentReportResponse> MapToResponseAsync(
        WorkAssignmentReport x,
        WorkReportPeriod? period,
        CancellationToken ct)
    {
        var template = await _ctx.DynamicExcelTemplates
            .Find(t => t.Id == x.DynamicExcelTemplateId && !t.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (template is null)
            throw new InvalidOperationException("Không tìm thấy DynamicExcelTemplate.");

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
            DynamicFormTemplateId = x.DynamicFormTemplateId ?? period?.DynamicFormTemplateId,
            DynamicFormTemplateCode = x.DynamicFormTemplateCode ?? period?.DynamicFormTemplateCode,
            DynamicFormTemplateName = x.DynamicFormTemplateName ?? period?.DynamicFormTemplateName,

            DataRectR0 = x.DataRectR0,
            DataRectC0 = x.DataRectC0,
            DataRectR1 = x.DataRectR1,
            DataRectC1 = x.DataRectC1,
            W = x.W,
            H = x.H,

            Values1DJson = x.Values1DJson,
            FieldValuesJson = x.FieldValuesJson,
            TableValuesJson = x.TableValuesJson,

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

                if (rebuildProjection)
                    await _docRoleReadModelProjection.RebuildReportPeriodAsync(period.Id, actorUserId, ct);
            }

            if (syncAssignment)
                await _statusSync.SyncFromAssignmentAsync(report.WorkAssignmentId, ct);

            await _labelStatistics.RebuildForReportAsync(report.Id, actorUserId, ct);
            await _tableStatistics.RebuildForReportAsync(report.Id, actorUserId, ct);
            await _fieldStatistics.RebuildForReportAsync(report.Id, actorUserId, ct);

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

    private static WorkReportPeriodStatus ResolveApprovedPeriodStatus(
        WorkReportPeriod period,
        WorkAssignmentReport report,
        DateTime now)
    {
        return WorkReportPeriodStatusHelper.ResolveApprovedStatus(
            period.Status,
            period.DueAtUtc,
            report.IsLateSubmission,
            now);
    }

    private static DateTime NormalizeDueAtUtc(DateTime date)
        => date.Date.AddDays(1).AddTicks(-1);

    private static TemplateSnapshotDTO BuildTemplateSnapshot(DynamicExcelTemplate template)
    {
        return new TemplateSnapshotDTO
        {
            TemplateId = template.Id,
            Code = template.Code,
            Name = template.Name,
            SpecJson = template.SpecJson,
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
            WeekDays = assignment.Schedule?.WeekDays?.ToArray() ?? Array.Empty<int>(),
            MonthDays = assignment.Schedule?.MonthDays?.ToArray() ?? Array.Empty<int>(),
            QuarterDays = assignment.Schedule?.QuarterDays?.ToArray() ?? Array.Empty<int>(),
            SemiAnnualDays = assignment.Schedule?.SemiAnnualDays?.ToArray() ?? Array.Empty<int>(),
            Note = assignment.Schedule?.Note
        };
    }

    private static List<decimal?> CreateEmptyValues1D(int w, int h)
    {
        var len = Math.Max(0, w) * Math.Max(0, h);
        return Enumerable.Range(0, len).Select(_ => (decimal?)null).ToList();
    }

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
            CurrentProgressStatus = x.CurrentProgressStatus,
            ReportReason = x.ReportReason,
            Difficulties = x.Difficulties,
            ProposedSolution = x.ProposedSolution,
            VersionNo = x.VersionNo,
            IsCurrent = x.IsCurrent,
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
            throw new UnauthorizedAccessException("Không xác định được người dùng thực hiện.");
    }

    private static string? NormalizeOptionalTextOrNull(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
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
