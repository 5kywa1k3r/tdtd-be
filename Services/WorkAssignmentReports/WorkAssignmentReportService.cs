using MongoDB.Driver;
using Microsoft.Extensions.Logging;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using MongoDB.Bson;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.DTOs.DynamicExcel;
using tdtd_be.DTOs.Operations;
using tdtd_be.DTOs.WorkAssignments.AggregateTable;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.Common.Time;
using tdtd_be.Services;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignments.Domain;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.WorkAssignments.Aggregate;
using tdtd_be.Services.WorkAssignments.Queue;
using tdtd_be.Services.WorkAssignments.Runtime;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Services.WorkAssignmentReports;

public sealed partial class WorkAssignmentReportService : IWorkAssignmentReportService
{
    private static readonly Regex LabelCodeRegex = new("^[a-z0-9][a-z0-9_.-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex RuntimeFullDateRegex = new(@"^(\d{2})/(\d{2})/(\d{4})$", RegexOptions.Compiled);
    private static readonly Regex RuntimeMonthDateRegex = new(@"^(\d{2})/(\d{4})$", RegexOptions.Compiled);
    private static readonly Regex RuntimeYearDateRegex = new(@"^(\d{4})$", RegexOptions.Compiled);
    private static readonly Regex PayloadCommandIdRegex = new("^[A-Za-z0-9][A-Za-z0-9._:-]{7,127}$", RegexOptions.Compiled);
    private const string RuntimeDataTypeNumber = "NUMBER";
    private const string RuntimeDataTypeDate = "DATE";
    private const string RuntimeDataTypeFullDate = "FULL_DATE";
    private const string RuntimeDataTypeBoolean = "BOOLEAN";
    private const string RuntimeDataTypeShortText = "SHORT_TEXT";
    private const string RuntimeDataTypeShortTextList = "SHORT_TEXT_LIST";
    private const string RuntimeDataTypeLongText = "LONG_TEXT";
    private const string RuntimeDataTypeStringList = "STRING_LIST";
    private const string RuntimeDataTypeIgnore = "IGNORE";
    private const string RuntimeDataTypeUnsupported = "UNSUPPORTED";

    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentQueueService _queueService;
    private readonly IWorkAssignmentStatusSyncService _statusSync;
    private readonly IWorkAssignmentMaterializeJobService _materializeJob;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
    private readonly IDocRoleReadModelFreshnessService _docRoleReadModelFreshness;
    private readonly IWorkStatusOperationLogService _statusLog;
    private readonly IUserActionLogService _userActionLog;
    private readonly IWorkReportPayloadReader _payloadReader;
    private readonly IWorkReportPayloadWriter _payloadWriter;
    private readonly IWorkReportLabelStatisticsService _labelStatistics;
    private readonly IWorkReportTableStatisticsService _tableStatistics;
    private readonly IWorkReportFieldStatisticsService _fieldStatistics;
    private readonly IAggregateTableService _aggregateTableService;
    private readonly ILabelEnumCatalogService _enumCatalogs;
    private readonly IWorkAssignmentAdvancedSummaryDirtyService _advancedSummaryDirty;
    private readonly IDynamicFlowPolicyEvaluator _dynamicFlowPolicyEvaluator;
    private readonly IWorkReportAggregateDependentRecoveryService _aggregateDependentRecovery;
    private readonly IWorkAssignmentReportSectionProjectionService _sectionProjection;
    private readonly IWorkReportLifecycleProjectionReconciler _lifecycleProjectionReconciler;
    private readonly IWorkReportLifecycleSeriesLockService _lifecycleSeriesLock;
    private readonly IDynamicFlowRuntimeActivationPolicy _dynamicFlowRuntimeActivationPolicy;
    private readonly IDynamicFlowDefinitionTransactionRunner _dynamicFlowTransactions;
    private readonly IDynamicFlowMappingOutboxReconciler
        _dynamicFlowMappingOutboxReconciler;
    private readonly IDynamicFlowMappingApplyFaultInjector
        _dynamicFlowMappingApplyFaultInjector;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkAssignmentReportService> _log;

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private const string EmptyValues1DJson = "[]";
    private static readonly ProjectionDefinition<DynamicExcelTemplate, DynamicExcelTemplate> DynamicExcelTemplateMetadataProjection =
        Builders<DynamicExcelTemplate>.Projection.Expression(x => new DynamicExcelTemplate
        {
            Id = x.Id,
            Code = x.Code,
            Name = x.Name,
            TableMode = x.TableMode,
            ContractVersion = x.ContractVersion,
            CreatedByUsername = x.CreatedByUsername,
            SpecJson = x.SpecJson,
            RawWorkbookDataJson = string.Empty,
            DataRectR0 = x.DataRectR0,
            DataRectC0 = x.DataRectC0,
            DataRectR1 = x.DataRectR1,
            DataRectC1 = x.DataRectC1,
            W = x.W,
            H = x.H
        });

    public WorkAssignmentReportService(
        MongoDbContext ctx,
        IWorkAssignmentQueueService queueService,
        IWorkAssignmentStatusSyncService statusSync,
        IWorkAssignmentMaterializeJobService materializeJob,
        IDocRoleReadModelProjectionService docRoleReadModelProjection,
        IDocRoleReadModelFreshnessService docRoleReadModelFreshness,
        IWorkStatusOperationLogService statusLog,
        IUserActionLogService userActionLog,
        IWorkReportPayloadReader payloadReader,
        IWorkReportPayloadWriter payloadWriter,
        IWorkReportLabelStatisticsService labelStatistics,
        IWorkReportTableStatisticsService tableStatistics,
        IWorkReportFieldStatisticsService fieldStatistics,
        IAggregateTableService aggregateTableService,
        ILabelEnumCatalogService enumCatalogs,
        IWorkAssignmentAdvancedSummaryDirtyService advancedSummaryDirty,
        IDynamicFlowPolicyEvaluator dynamicFlowPolicyEvaluator,
        IWorkReportAggregateDependentRecoveryService aggregateDependentRecovery,
        IWorkAssignmentReportSectionProjectionService sectionProjection,
        IWorkReportLifecycleProjectionReconciler lifecycleProjectionReconciler,
        IWorkReportLifecycleSeriesLockService lifecycleSeriesLock,
        IDynamicFlowRuntimeActivationPolicy dynamicFlowRuntimeActivationPolicy,
        IDynamicFlowDefinitionTransactionRunner dynamicFlowTransactions,
        IDynamicFlowMappingOutboxReconciler
            dynamicFlowMappingOutboxReconciler,
        IDynamicFlowMappingApplyFaultInjector
            dynamicFlowMappingApplyFaultInjector,
        IConfiguration configuration,
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
        _payloadReader = payloadReader;
        _payloadWriter = payloadWriter;
        _labelStatistics = labelStatistics;
        _tableStatistics = tableStatistics;
        _fieldStatistics = fieldStatistics;
        _aggregateTableService = aggregateTableService;
        _enumCatalogs = enumCatalogs;
        _advancedSummaryDirty = advancedSummaryDirty;
        _dynamicFlowPolicyEvaluator = dynamicFlowPolicyEvaluator;
        _aggregateDependentRecovery = aggregateDependentRecovery;
        _sectionProjection = sectionProjection;
        _lifecycleProjectionReconciler = lifecycleProjectionReconciler;
        _lifecycleSeriesLock = lifecycleSeriesLock;
        _dynamicFlowRuntimeActivationPolicy = dynamicFlowRuntimeActivationPolicy;
        _dynamicFlowTransactions = dynamicFlowTransactions;
        _dynamicFlowMappingOutboxReconciler =
            dynamicFlowMappingOutboxReconciler;
        _dynamicFlowMappingApplyFaultInjector =
            dynamicFlowMappingApplyFaultInjector;
        _configuration = configuration;
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
        var scopeAssignmentIds = await WorkAssignmentReadAccessHelper.ResolveReadableScopeIdsAsync(
            _ctx,
            workId,
            req.ScopeAssignmentId,
            actorUserId,
            ct);
        var isScopedBranchView = scopeAssignmentIds is not null;

        if (!isScopedBranchView)
            await EnsureMyReportListDocRolesForUserWorkAsync(workId, actorUserId, ct);

        var rowsByTemplate = new Dictionary<string, MyReportTemplateRow>(StringComparer.Ordinal);
        if (!isScopedBranchView)
        {
            var projectionFilter = Builders<MyReportTemplateListDocRole>.Filter.Eq(x => x.UserId, actorUserId)
                & Builders<MyReportTemplateListDocRole>.Filter.Eq(x => x.WorkId, workId)
                & Builders<MyReportTemplateListDocRole>.Filter.Eq(x => x.IsDeleted, false);

            var projectionRows = await _ctx.MyReportTemplateListDocRoles
                .Find(projectionFilter)
                .ToListAsync(ct);

            foreach (var row in projectionRows.Select(MapTemplateDocRoleToRow))
            {
                var key = BuildTemplateGroupKey(row.DynamicFormTemplateId, row.DynamicExcelId);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                rowsByTemplate[key] = row;
            }
        }

        var bindings = isScopedBranchView
            ? scopeAssignmentIds!.Count == 0
                ? new List<WorkTemplateAssignee>()
                : await _ctx.WorkTemplateAssignees
                    .Find(x =>
                        x.WorkId == workId &&
                        !x.IsDeleted &&
                        scopeAssignmentIds.Contains(x.WorkAssignmentId))
                    .ToListAsync(ct)
            : await LoadVisibleReportBindingsAsync(workId, actorUserId, ct);

        if (isScopedBranchView)
        {
            var scopedIds = scopeAssignmentIds!;
            var periodRows = scopedIds.Count == 0
                ? new List<MyReportPeriodListDocRole>()
                : await _ctx.MyReportPeriodListDocRoles
                    .Find(x =>
                        x.WorkId == workId &&
                        scopedIds.Contains(x.AssignmentId) &&
                        !x.IsDeleted)
                    .ToListAsync(ct);

            foreach (var row in BuildTemplateRowsFromReportPeriodRows(periodRows))
            {
                var key = BuildTemplateGroupKey(row.DynamicFormTemplateId, row.DynamicExcelId);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                rowsByTemplate[key] = row;
            }
        }

        foreach (var group in bindings
            .Where(x => x.IsActive)
            .GroupBy(x => BuildTemplateGroupKey(x.DynamicFormTemplateId, x.DynamicExcelId), StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(group.Key))
                continue;

            var bindingGroup = group.ToList();
            if (!rowsByTemplate.TryGetValue(group.Key, out var row))
            {
                rowsByTemplate[group.Key] = BuildTemplateRowFromBindings(bindingGroup);
                continue;
            }

            MergeBindingMetadata(row, bindingGroup);
        }

        var filteredRows = rowsByTemplate.Values
            .Where(row => MatchesTemplateSearch(row, req))
            .ToList();

        var total = filteredRows.Count;
        var items = ApplyTemplateSort(filteredRows, req.SortField, req.SortDirection)
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToList();

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

        var isOwner = WorkAssignmentCurrentAuthority.IsReviewer(assignment, actorUserId);
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

        var isOwner = WorkAssignmentCurrentAuthority.IsReviewer(assignment, actorUserId);
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

        if (DynamicFlowBranchVisibility.IsFlowAssignment(assignment) &&
            !await WorkAssignmentReadAccessHelper.CanReadAssignmentAsync(_ctx, assignment.Id, actorUserId, ct))
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_ACCESS_FORBIDDEN,
                ReportDetails(report, actorUserId));
        }

        var canReview = !isOwner && !isAssignee && await HasReviewReportReadAccessAsync(report, actorUserId, ct);
        var canReadAsAggregateSource = !isOwner &&
                                       !isAssignee &&
                                       !canReview &&
                                       await HasAggregateAncestorReadAccessAsync(assignment, actorUserId, ct);

        if (!isOwner && !isAssignee && !canReview && !canReadAsAggregateSource)
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

    private async Task<bool> HasAggregateAncestorReadAccessAsync(
        WorkAssignment reportAssignment,
        string actorUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorUserId) ||
            string.IsNullOrWhiteSpace(reportAssignment.WorkId) ||
            string.IsNullOrWhiteSpace(reportAssignment.Path))
        {
            return false;
        }

        return await WorkAssignmentReadAccessHelper.CanReadAssignmentOrAncestorAsync(
            _ctx,
            reportAssignment,
            actorUserId,
            ct);
    }
    public async Task<MyReportTemplateDetailResponse> GetMyReportTemplateDetailAsync(
        string workId,
        string dynamicFormTemplateId,
        string actorUserId,
        string? scopeAssignmentId = null,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(workId))
            throw ReportWorkIdRequired(workId);

        if (string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_DYNAMIC_FORM_TEMPLATE_ID_REQUIRED,
                new { workId, dynamicFormTemplateId });

        var scopeAssignmentIds = await WorkAssignmentReadAccessHelper.ResolveReadableScopeIdsAsync(
            _ctx,
            workId,
            scopeAssignmentId,
            actorUserId,
            ct);
        var isScopedBranchView = scopeAssignmentIds is not null;

        var bindings = isScopedBranchView
            ? scopeAssignmentIds!.Count == 0
                ? new List<WorkTemplateAssignee>()
                : await _ctx.WorkTemplateAssignees
                    .Find(x =>
                        x.WorkId == workId &&
                        x.DynamicFormTemplateId == dynamicFormTemplateId &&
                        scopeAssignmentIds.Contains(x.WorkAssignmentId) &&
                        !x.IsDeleted)
                    .ToListAsync(ct)
            : await LoadVisibleReportBindingsByTemplateAsync(workId, dynamicFormTemplateId, actorUserId, ct);

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
        if (periods.Count == 0 && !isScopedBranchView)
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
                .Project(DynamicExcelTemplateMetadataProjection)
                .FirstOrDefaultAsync(ct);

        var bindingAssignmentIds = bindings
            .Select(x => x.WorkAssignmentId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var assignmentIds = periods
            .Select(x => x.WorkAssignmentId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Concat(bindingAssignmentIds)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var assignments = assignmentIds.Count == 0
            ? new List<WorkAssignment>()
            : await _ctx.WorkAssignments
                .Find(x => assignmentIds.Contains(x.Id) && !x.IsDeleted)
                .ToListAsync(ct);

        var assignmentById = assignments
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .ToDictionary(x => x.Id!, StringComparer.Ordinal);

        var activeBindingIds = bindings.Where(x => x.IsActive).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);

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
            SpecJson = template?.SpecJson ?? string.Empty,
            TemplateSnapshotJson = template is null ? string.Empty : JsonSerializer.Serialize(BuildTemplateSnapshot(template), _jsonOptions),
            AssignmentOptions = bindings
                .OrderByDescending(x => x.IsActive)
                .ThenBy(x => x.WorkAssignmentId)
                .Select(x => MapToTemplateAssignmentOption(
                    x,
                    assignmentById.TryGetValue(x.WorkAssignmentId, out var assignment) ? assignment : null))
                .ToList(),
            Periods = periods
                .Select(x => MapToPeriodRow(
                    x,
                    assignmentById.TryGetValue(x.WorkAssignmentId, out var assignment) ? assignment : null,
                    DateTime.UtcNow,
                    activeBindingIds.Contains(x.WorkTemplateAssigneeId)))
                .ToList()
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

        var periodAccess = await EnsurePeriodAccessAsync(period, actorUserId, ct);

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
                await TryReconcileCommittedLifecycleProjectionAsync(existed.Id);
                await _docRoleReadModelProjection.RebuildReportPeriodAsync(period.Id, actorUserId, ct);
                await _docRoleReadModelFreshness.EnsureReportPeriodFreshAsync(period, existed, actorUserId, ct);
                // Reconciliation updates persisted outbox state, not the previously loaded object.
                return await MapPayloadCommandReplayResponseAsync(existed.Id, actorUserId, ct);
            }
        }

        await EnsureReportMutationScopeOpenAsync(periodAccess.assignment, actorUserId, ct, period.Id);

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
        await TryReconcileCommittedLifecycleProjectionAsync(created.Id);
        await _docRoleReadModelFreshness.EnsureReportPeriodFreshAsync(period, created, actorUserId, ct);

        // Return the committed projection state rather than the pre-reconciliation draft.
        return await MapPayloadCommandReplayResponseAsync(created.Id, actorUserId, ct);
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
            throw RawReportReadAccessForbidden();

        try
        {
            await EnsureReportAccessAsync(entity, actorUserId, ct);
        }
        catch (AppException error) when (
            error.Code is
                AppErrorCode.WORK_ASSIGNMENT_REPORT_ACCESS_FORBIDDEN or
                AppErrorCode.WORK_ASSIGNMENT_REPORT_ASSIGNMENT_NOT_FOUND)
        {
            throw RawReportReadAccessForbidden();
        }

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        await _docRoleReadModelFreshness.EnsureReportPeriodFreshAsync(period, entity, actorUserId, ct);

        entity = await RefreshAggregateSnapshotForReadAsync(entity, actorUserId, ct);

        return await MapToResponseAsync(entity, period, actorUserId, ct);
    }

    public async Task<List<WorkAssignmentReportSectionSummaryRow>> GetSectionSummariesAsync(
        string id,
        string actorUserId,
        CancellationToken ct = default)
    {
        var (entity, _) = await EnsureReadableReportAsync(id, actorUserId, ct);

        var sections = await _ctx.WorkAssignmentReportSections
            .Find(x => x.WorkAssignmentReportId == entity.Id && !x.IsDeleted)
            .SortBy(x => x.SectionOrder)
            .ThenBy(x => x.SectionId)
            .ToListAsync(ct);

        sections = await EnsureFreshReportSectionProjectionsAsync(
            entity,
            sections,
            actorUserId,
            ct);

        return sections.Select(MapSectionSummaryRow).ToList();
    }

    public async Task<WorkAssignmentReportSectionDetailResponse> GetSectionDetailAsync(
        string id,
        string sectionId,
        string actorUserId,
        CancellationToken ct = default)
    {
        var normalizedSectionId = NormalizeOptionalTextOrNull(sectionId);
        if (string.IsNullOrWhiteSpace(normalizedSectionId))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_ARGUMENT_REQUIRED,
                new { reportId = id, sectionId },
                "sectionId is required.");

        var (entity, assignment) = await EnsureReadableReportAsync(id, actorUserId, ct);

        var loadedSections = await _ctx.WorkAssignmentReportSections
            .Find(x => x.WorkAssignmentReportId == entity.Id && !x.IsDeleted)
            .ToListAsync(ct);
        var refreshed = await EnsureFreshReportSectionProjectionsAsync(
            entity,
            loadedSections,
            actorUserId,
            ct);
        var section = refreshed.FirstOrDefault(x =>
            string.Equals(x.SectionId, normalizedSectionId, StringComparison.Ordinal));

        if (section is null)
            throw AppExceptionFactory.NotFound(
                AppErrorCode.COMMON_NOT_FOUND,
                new { reportId = id, sectionId = normalizedSectionId },
                "Không tìm thấy dữ liệu phần của báo cáo.");

        DynamicFormSectionSnapshot? templateSection = null;
        if (!string.IsNullOrWhiteSpace(section.DynamicFormTemplateId))
        {
            var templateId = section.DynamicFormTemplateId;
            var template = await _ctx.DynamicFormTemplates
                .Find(x => x.Id == templateId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw AppExceptionFactory.NotFound(
                    AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
                    new { dynamicFormTemplateId = templateId });
            templateSection = DynamicFormSectionSnapshotBuilder.GetRequiredSection(template, section.SectionId);
        }

        var dynamicFlowPermissions = await ResolveDynamicFlowReportPermissionsAsync(
            assignment,
            entity,
            actorUserId,
            IsDynamicFlowReportAfterSubmit(entity),
            ct);
        if (dynamicFlowPermissions is not null && templateSection?.NativeTablesJson is not null)
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
                new { reportId = id, sectionId = section.SectionId, reason = "NATIVE_SECTION_FLOW_CONTRACT_REQUIRED" });
        var readablePayload = dynamicFlowPermissions is null
            ? new DynamicFlowReportReadablePayload(null, section.FieldValuesJson, section.TableValuesJson)
            : DynamicFlowReportPermissionEnforcer.ApplyReadRestrictions(
                dynamicFlowPermissions,
                null,
                section.FieldValuesJson,
                section.TableValuesJson,
                null);

        return new WorkAssignmentReportSectionDetailResponse
        {
            ReportId = entity.Id,
            SectionId = section.SectionId,
            SectionTitle = section.SectionTitle,
            SectionOrder = section.SectionOrder,
            FieldCount = section.FieldCount,
            BlockCount = section.BlockCount,
            NativeTableCount = section.NativeTableCount,
            HasData = section.HasData,
            LastUpdatedAtUtc = section.LastUpdatedAtUtc,
            LastUpdatedByUserId = section.LastUpdatedByUserId,
            SourcePayloadUpdatedAtUtc = section.SourcePayloadUpdatedAtUtc,
            SourcePayloadRevision = section.SourcePayloadRevision,
            SourcePayloadHash = section.SourcePayloadHash,
            DynamicFlowMappingReceiptId =
                section.DynamicFlowMappingReceiptId,
            DynamicFlowMappingProvenanceId =
                section.DynamicFlowMappingProvenanceId,
            DynamicFlowMappingProvenanceHash =
                section.DynamicFlowMappingProvenanceHash,
            DynamicFlowMappingResultPayloadRevision =
                section.DynamicFlowMappingResultPayloadRevision,
            DynamicFlowMappingResultPayloadHash =
                section.DynamicFlowMappingResultPayloadHash,
            SourceLifecycleRevision = section.SourceLifecycleRevision,
            SourceReportUpdatedAtUtc = section.SourceReportUpdatedAtUtc,
            DynamicFormTemplateId = section.DynamicFormTemplateId,
            DynamicFormTemplateCode = section.DynamicFormTemplateCode,
            DynamicFormTemplateName = section.DynamicFormTemplateName,
            DynamicFormFamilyId = section.DynamicFormFamilyId,
            DynamicFormVersionNo = section.DynamicFormVersionNo,
            DynamicFormSchemaHash = section.DynamicFormSchemaHash,
            FieldsJson = templateSection?.FieldsJson ?? "[]",
            BlocksJson = templateSection?.BlocksJson ?? "[]",
            NativeTablesVersion = templateSection?.NativeTablesJson is null ? null : 1,
            NativeTablesJson = templateSection?.NativeTablesJson,
            FieldValuesJson = readablePayload.FieldValuesJson,
            TableValuesJson = readablePayload.TableValuesJson
        };
    }

    private async Task<(WorkAssignmentReport report, WorkAssignment assignment)> EnsureReadableReportAsync(
        string id,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(id))
            throw ReportIdRequired(id);

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw ReportNotFound(id);

        var access = await EnsureReportAccessAsync(entity, actorUserId, ct);
        EnsureReportIsActive(entity);
        return (entity, access.assignment);
    }

    private static WorkAssignmentReportSectionSummaryRow MapSectionSummaryRow(
        WorkAssignmentReportSection section)
        => new()
        {
            SectionId = section.SectionId,
            SectionTitle = section.SectionTitle,
            SectionOrder = section.SectionOrder,
            FieldCount = section.FieldCount,
            BlockCount = section.BlockCount,
            NativeTableCount = section.NativeTableCount,
            HasData = section.HasData,
            LastUpdatedAtUtc = section.LastUpdatedAtUtc,
            LastUpdatedByUserId = section.LastUpdatedByUserId,
            SourcePayloadUpdatedAtUtc = section.SourcePayloadUpdatedAtUtc,
            SourcePayloadRevision = section.SourcePayloadRevision,
            SourcePayloadHash = section.SourcePayloadHash,
            DynamicFlowMappingReceiptId =
                section.DynamicFlowMappingReceiptId,
            DynamicFlowMappingProvenanceId =
                section.DynamicFlowMappingProvenanceId,
            DynamicFlowMappingProvenanceHash =
                section.DynamicFlowMappingProvenanceHash,
            DynamicFlowMappingResultPayloadRevision =
                section.DynamicFlowMappingResultPayloadRevision,
            DynamicFlowMappingResultPayloadHash =
                section.DynamicFlowMappingResultPayloadHash,
            SourceLifecycleRevision = section.SourceLifecycleRevision,
            SourceReportUpdatedAtUtc = section.SourceReportUpdatedAtUtc
        };

    private async Task<List<WorkAssignmentReportSection>> EnsureFreshReportSectionProjectionsAsync(
        WorkAssignmentReport report,
        IReadOnlyCollection<WorkAssignmentReportSection> loadedSections,
        string actorUserId,
        CancellationToken ct)
    {
        var projected = await _sectionProjection.EnsureCurrentAndVerifyAsync(
            report,
            loadedSections,
            report.UpdatedByUserId ?? actorUserId,
            report.PayloadUpdatedAtUtc ?? report.UpdatedAtUtc,
            ct);
        return projected.ToList();
    }

    private static long DateTimeToUnixMilliseconds(DateTime? value)
        => value.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
            : long.MinValue;

    public async Task<DynamicExcelDetail> GetReportTemplateWorkbookAsync(
        string id,
        string dynamicExcelTemplateId,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(id))
            throw ReportIdRequired(id);

        var requestedTemplateId = NormalizeOptionalTextOrNull(dynamicExcelTemplateId);
        if (string.IsNullOrWhiteSpace(requestedTemplateId))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_EXCEL_TEMPLATE_ID_REQUIRED,
                new { dynamicExcelTemplateId });

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw ReportNotFound(id);

        await EnsureReportAccessAsync(entity, actorUserId, ct);

        var allowedTemplateIds = await ResolveReportDynamicExcelTemplateIdsAsync(entity, ct);
        if (!allowedTemplateIds.Contains(requestedTemplateId))
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_ACCESS_FORBIDDEN,
                new
                {
                    reportId = id,
                    dynamicExcelTemplateId = requestedTemplateId,
                    allowedDynamicExcelTemplateIds = allowedTemplateIds
                });

        var template = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == requestedTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (template is null)
            throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_EXCEL_TEMPLATE_NOT_FOUND,
                new { dynamicExcelTemplateId = requestedTemplateId, reportId = id });

        return MapDynamicExcelDetail(template);
    }

    public async Task<List<WorkAssignmentReportListRow>> GetByAssignmentAsync(
        string workAssignmentId,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(workAssignmentId))
            throw ReportAssignmentIdRequired(workAssignmentId);

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == workAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportAssignmentNotFound(workAssignmentId);

        var isOwner = WorkAssignmentCurrentAuthority.IsReviewer(assignment, actorUserId);
        var isAssignee = await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkAssignmentId == workAssignmentId &&
                x.AssigneeUserId == actorUserId &&
                !x.IsDeleted)
            .Limit(1)
            .AnyAsync(ct);
        var canReadAssignment = await WorkAssignmentReadAccessHelper.CanReadAssignmentAsync(
            _ctx,
            workAssignmentId,
            actorUserId,
            ct);

        if (DynamicFlowBranchVisibility.IsFlowAssignment(assignment) && !canReadAssignment)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_ASSIGNMENT_ACCESS_FORBIDDEN,
                new { workAssignmentId, actorUserId });

        if (!isOwner && !isAssignee && !canReadAssignment)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_ASSIGNMENT_ACCESS_FORBIDDEN,
                new { workAssignmentId, actorUserId });

        var reportFilter = Builders<WorkAssignmentReport>.Filter.Eq(x => x.WorkAssignmentId, workAssignmentId)
            & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false)
            & Builders<WorkAssignmentReport>.Filter.Ne(x => x.IsActive, false);

        var canReadWholeAssignment = isOwner || (!isAssignee && canReadAssignment);
        if (!canReadWholeAssignment)
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

        var docs = await _ctx.MyReportPeriodListDocRoles
            .Find(filter)
            .Sort(BuildReportPeriodListDocRoleSort(req.SortField, req.SortDirection))
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
        var mapper = MapToListRowProjection().Compile();
        var rows = docs.Select(mapper).ToList();

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
        var reportAccess = await EnsureReportAccessAsync(entity, actorUserId, ct);
        if (!reportAccess.isAssignee || entity.AssigneeUserId != actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_SAVE_FORBIDDEN,
                ReportDetails(entity, actorUserId));
        EnsureReportIsActive(entity);
        await HydrateReportPayloadAsync(entity, ct);
        await EnsureDynamicFlowMappingProvenanceIntegrityAsync(entity, ct);
        await ValidateDynamicFlowMappingLifecycleBoundaryAsync(
            entity,
            ct);

        var payloadCommand = ResolvePayloadMutationCommand(
            entity,
            req.ExpectedPayloadRevision,
            req.CommandId,
            "SAVE_DRAFT",
            req.CommandHashOverride ?? ComputeReportPayloadCommandHash("SAVE_DRAFT", req));
        if (IsCompletedPayloadCommandReplay(entity, payloadCommand))
        {
            await TryReconcileCommittedLifecycleProjectionAsync(entity.Id);
            return await MapPayloadCommandReplayResponseAsync(entity.Id, actorUserId, CancellationToken.None);
        }

        await EnsureReportMutationScopeOpenAsync(reportAccess.assignment, actorUserId, ct, entity.WorkReportPeriodId);

        if (entity.Status != WorkAssignmentReportStatus.Draft)
            throw InvalidReportStatus(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_SAVE_STATUS_INVALID,
                entity,
                WorkAssignmentReportStatus.Draft,
                actorUserId);

        var actualLength = req.Values1D?.Count ?? 0;
        var runtimeTopLevelBlock = await ResolveRuntimeTopLevelBlockShapeAsync(entity, req.TableValuesJson, actualLength, ct);
        var expectedLength = runtimeTopLevelBlock is not null
            ? ResolveRuntimeInputCells(runtimeTopLevelBlock.Block, runtimeTopLevelBlock.DataRect, runtimeTopLevelBlock.W, runtimeTopLevelBlock.H).Count
            : ResolveReportRuntimeInputCells(entity).Count;

        if (actualLength != expectedLength)
            throw InvalidReportValues(entity, expectedLength, actualLength, actorUserId);

        if (runtimeTopLevelBlock is not null)
            ApplyRuntimeTopLevelShape(entity, runtimeTopLevelBlock);

        var now = DateTime.UtcNow;
        var fromStatus = entity.Status;
        var nextStatus = WorkAssignmentReportStatus.Draft;
        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        var completedDatePolicy = ResolveReportCompletedDatePolicy(reportAccess.assignment, entity, period, now);
        var isHistoricalData = IsHistoricalReportData(entity, period, completedDatePolicy);
        var startedDate = ResolveServerReportStartedDate(entity, period);
        var requestedCompletedDate = NormalizeDate(req.CompletedDate);
        var completedDate = ValidateReportCompletedDateInput(
            completedDatePolicy,
            completedDatePolicy.CanEditCompletedDate ? requestedCompletedDate ?? entity.CompletedDate : requestedCompletedDate,
            ReportDetails(entity, actorUserId),
            requireWhenMissing: false);
        EnsureReportDateRange(startedDate, completedDate, "StartedDate", "CompletedDate");
        var effectiveDueAtUtc = ResolveEffectiveReportDueAtUtc(entity.DueAtUtc ?? period?.DueAtUtc, reportAccess.assignment);
        var hasFlowOwnedMappingMetadata = IsDynamicFlowMappingSummary(entity.SummarySourceJson);
        EnsureDynamicFlowMappingProvenanceOverrideAllowed(
            entity,
            req.SummarySourceJson);
        EnsureDynamicFlowMappingSummaryOverrideAllowed(
            entity.SummarySourceJson,
            req.SummarySourceJson,
            id,
            actorUserId);
        var nextDataOrigin = hasFlowOwnedMappingMetadata
            ? WorkReportDataOrigin.Normalize(entity.DataOrigin)
            : ResolveReportDataOrigin(req.DataOrigin, entity.DataOrigin);
        var nextContributionMode = hasFlowOwnedMappingMetadata
            ? WorkReportCumulativeContributionMode.Normalize(entity.CumulativeContributionMode)
            : ResolveCumulativeContributionMode(
                req.CumulativeContributionMode,
                req.DataOrigin,
                entity.CumulativeContributionMode,
                entity.DataOrigin);
        var nextContributionPolicyJson = hasFlowOwnedMappingMetadata
            ? NormalizeOptionalTextOrNull(entity.CumulativeContributionPolicyJson)
            : ResolveContributionPolicyJsonOverride(
                req.CumulativeContributionPolicyJson,
                entity.CumulativeContributionPolicyJson,
                id,
                actorUserId);
        var nextSummarySourceJson = hasFlowOwnedMappingMetadata
            ? NormalizeOptionalTextOrNull(entity.SummarySourceJson)
            : ResolveSummarySourceJsonOverride(
                req.SummarySourceJson,
                entity.SummarySourceJson,
                id,
                actorUserId);
        var nextAggregateSources = ExtractAggregateSourceSnapshot(nextSummarySourceJson);
        var acceptsReportDataPayload = ShouldAcceptReportDataPayload(entity, nextDataOrigin, nextSummarySourceJson);
        var isStackedAggregatePayload = IsStackedAggregateSummary(nextSummarySourceJson);
        var requestValues1D = req.Values1D ?? new List<object?>();

        if (acceptsReportDataPayload &&
            !isStackedAggregatePayload &&
            DynamicFlowBranchVisibility.IsFlowAssignment(reportAccess.assignment) &&
            req.TableValuesJson is null &&
            runtimeTopLevelBlock is not null &&
            requestValues1D.Count > 0)
        {
            req.TableValuesJson = MergeDynamicFlowTopLevelValuesIntoTableValuesJson(
                entity,
                entity.TableValuesJson,
                runtimeTopLevelBlock,
                requestValues1D,
                actorUserId);
        }

        if (acceptsReportDataPayload && !isStackedAggregatePayload)
        {
            var writablePayload = await PrepareDynamicFlowReportWritePayloadAsync(
                reportAccess.assignment,
                entity,
                req.FieldValuesJson,
                req.TableValuesJson,
                actorUserId,
                validateRequired: false,
                ct: ct);
            req.FieldValuesJson = writablePayload.FieldValuesJson;
            req.TableValuesJson = writablePayload.TableValuesJson;
            if (runtimeTopLevelBlock is not null && !string.IsNullOrWhiteSpace(req.TableValuesJson))
            {
                var submittedValues1DJson = Values1DCompression.Serialize(requestValues1D, _jsonOptions);
                var synchronizedValues1DJson = ResolveDynamicFlowMappingTopLevelValuesJson(
                    submittedValues1DJson,
                    req.TableValuesJson,
                    runtimeTopLevelBlock.BlockId,
                    _jsonOptions);
                requestValues1D = Values1DCompression.DeserializeObjects(synchronizedValues1DJson, _jsonOptions);
            }
        }

        await ValidateRuntimeRowLabelsAsync(
            entity,
            acceptsReportDataPayload ? req.TableValuesJson : entity.TableValuesJson,
            ct);
        if (acceptsReportDataPayload && !isStackedAggregatePayload)
            req.FieldValuesJson = await ValidateRuntimeDataPayloadAsync(
                entity,
                requestValues1D,
                req.FieldValuesJson,
                req.TableValuesJson,
                validateRequiredFields: false,
                ct);

        var values1DJson = acceptsReportDataPayload
            ? Values1DCompression.Serialize(requestValues1D, _jsonOptions)
            : entity.Values1DJson;
        var fieldValuesJson = acceptsReportDataPayload ? req.FieldValuesJson : entity.FieldValuesJson;
        var tableValuesJson = acceptsReportDataPayload
            ? Values1DCompression.CompressTableValuesJson(req.TableValuesJson, _jsonOptions)
            : entity.TableValuesJson;
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
        var payloadResult = WorkReportPayloadService.PreflightReportPayload(
            entity,
            values1DJson,
            fieldValuesJson,
            tableValuesJson,
            nextSummarySourceJson,
            actorUserId,
            now);
        var reservation = await ReservePayloadMutationCommandAsync(entity, payloadCommand, now, ct, deferToTransaction: true);
        if (reservation == PayloadCommandReservationOutcome.CompletedReplay)
        {
            await TryReconcileCommittedLifecycleProjectionAsync(entity.Id);
            return await MapPayloadCommandReplayResponseAsync(entity.Id, actorUserId, CancellationToken.None);
        }

        var headerCommitUpdate = ApplyPayloadCommandCompletion(
                ApplyPayloadHeaderUpdate(
                    Builders<WorkAssignmentReport>.Update,
                    payloadResult,
                    now)
                .Set(x => x.DataOrigin, nextDataOrigin)
                .Set(x => x.CumulativeContributionMode, nextContributionMode)
                .Set(x => x.CumulativeContributionPolicyJson, nextContributionPolicyJson)
                .Set(x => x.AggregateSourceReportIds, nextAggregateSources.ReportIds)
                .Set(x => x.AggregateSourceAssignmentIds, nextAggregateSources.AssignmentIds)
                .Set(x => x.AggregateSourceUpdatedAtUtc, nextAggregateSourceUpdatedAtUtc)
                .Set(x => x.AggregateSnapshotDirty, nextAggregateSnapshotDirty)
                .Set(x => x.AggregateSnapshotDirtyAtUtc, nextAggregateSnapshotDirtyAtUtc)
                .Set(x => x.AggregateSnapshotRefreshedAtUtc, nextAggregateSnapshotRefreshedAtUtc)
                .Set(x => x.AggregateRefreshError, (string?)null)
                .Set(x => x.W, entity.W)
                .Set(x => x.H, entity.H)
                .Set(x => x.DataRectR0, entity.DataRectR0)
                .Set(x => x.DataRectC0, entity.DataRectC0)
                .Set(x => x.DataRectR1, entity.DataRectR1)
                .Set(x => x.DataRectC1, entity.DataRectC1)
                .Set(x => x.StartedDate, startedDate)
                .Set(x => x.CompletedDate, completedDate)
                .Set(x => x.IsHistoricalData, isHistoricalData)
                .Set(x => x.DueAtUtc, effectiveDueAtUtc)
                .Set(x => x.LateReason, req.LateReason)
                .Set(x => x.Status, nextStatus)
                .Set(x => x.CreatedByUserId, string.IsNullOrWhiteSpace(entity.CreatedByUserId) ? actorUserId : entity.CreatedByUserId)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
                payloadCommand,
                payloadResult);
        headerCommitUpdate = WorkReportLifecycleOutboxContract.Append(
            headerCommitUpdate,
            new WorkReportLifecycleOutboxSeed(
                payloadCommand.CommandId,
                entity.LifecycleRevision,
                "SAVE_DRAFT",
                actorUserId,
                fromStatus.ToString(),
                nextStatus.ToString(),
                entity.IsActive,
                entity.IsActive,
                payloadResult.PayloadRevision,
                payloadResult.PayloadHash,
                now,
                MappingBinding:
                    DynamicFlowMappingLifecycleBinding.FromReport(entity)));

        var headerCommit = await CommitLegacyLifecycleWithDirectSourceFenceAsync(
            entity,
            BuildPayloadMutationCommitFilter(entity, payloadCommand),
            headerCommitUpdate,
            ct, payloadCommandToReserve: payloadCommand,
            persistPayload: async (session, before, token) =>
            {
                await AggregateHostIntegration.GuardPayloadAsync(_ctx, session, before, fieldValuesJson, tableValuesJson, token);
                var written = await _payloadWriter.SaveReportPayloadAsync(before, values1DJson, fieldValuesJson,
                    tableValuesJson, nextSummarySourceJson, actorUserId, now, token, session);
                if (written != payloadResult) throw new AggregatePreviewException("AGG_PAYLOAD_PLAN_STALE");
            });

        if (headerCommit.ModifiedCount != 1)
            return await ResolvePayloadCommandCommitRaceAsync(entity.Id, payloadCommand, actorUserId, ct);

        entity.Values1DJson = values1DJson;
        entity.FieldValuesJson = fieldValuesJson;
        entity.TableValuesJson = tableValuesJson;
        ApplyPayloadMetadata(entity, payloadResult, now);
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
        entity.StartedDate = startedDate;
        entity.CompletedDate = completedDate;
        entity.IsHistoricalData = isHistoricalData;
        entity.DueAtUtc = effectiveDueAtUtc;
        entity.LateReason = req.LateReason;
        entity.Status = nextStatus;
        entity.CreatedByUserId = string.IsNullOrWhiteSpace(entity.CreatedByUserId) ? actorUserId : entity.CreatedByUserId;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = actorUserId;

        await TryReconcileCommittedLifecycleProjectionAsync(entity.Id);
        return await MapPayloadCommandReplayResponseAsync(entity.Id, actorUserId, CancellationToken.None);
    }

    public async Task<WorkAssignmentReportResponse> SaveDraftPatchAsync(
        string id,
        SaveWorkAssignmentReportDraftPatchRequest req,
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
        var reportAccess = await EnsureReportAccessAsync(entity, actorUserId, ct);
        if (!reportAccess.isAssignee || entity.AssigneeUserId != actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_SAVE_FORBIDDEN,
                ReportDetails(entity, actorUserId));
        EnsureReportIsActive(entity);
        await HydrateReportPayloadAsync(entity, ct);

        var merged = new SaveWorkAssignmentReportDraftRequest
        {
            ExpectedPayloadRevision = req.ExpectedPayloadRevision,
            CommandId = req.CommandId,
            CommandHashOverride = ComputeReportPayloadCommandHash("SAVE_DRAFT_PATCH", req),
            Values1D = MergeDraftValuesPatch(entity.Values1DJson, req.Values1DLength, req.Values1DPatch),
            FieldValuesJson = req.FieldValuesJson ?? entity.FieldValuesJson,
            TableValuesJson = MergeDraftTableBlockPatches(entity, req.TableBlockPatches),
            DataOrigin = req.DataOrigin,
            CumulativeContributionMode = req.CumulativeContributionMode,
            CumulativeContributionPolicyJson = req.CumulativeContributionPolicyJson,
            SummarySourceJson = req.SummarySourceJson,
            CompletedDate = req.CompletedDate,
            LateReason = req.LateReason,
            Note = req.Note
        };

        return await SaveDraftAsync(id, merged, actorUserId, ct);
    }

    public async Task<WorkAssignmentReportResponse> ApplyDynamicFormAggregateDraftAsync(
        string id,
        ApplyDynamicFormAggregateDraftRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        // Tạm khóa luồng tổng hợp cũ; giữ nguyên triển khai bên dưới.
        tdtd_be.Services.WorkAssignments.LegacyAggregateRetirement.Reject();
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
        var reportAccess = await EnsureReportAccessAsync(entity, actorUserId, ct);
        if (!reportAccess.isAssignee || entity.AssigneeUserId != actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_SAVE_FORBIDDEN,
                ReportDetails(entity, actorUserId));
        EnsureReportIsActive(entity);
        await HydrateReportPayloadAsync(entity, ct);

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
            ExpectedPayloadRevision = req.ExpectedPayloadRevision,
            CommandId = req.CommandId,
            CommandHashOverride = ComputeReportPayloadCommandHash("APPLY_DYNAMIC_FORM_AGGREGATE", req),
            Values1D = projection.TopLevelValues,
            FieldValuesJson = entity.FieldValuesJson,
            TableValuesJson = projection.TableValuesJson,
            DataOrigin = projection.DataOrigin,
            CumulativeContributionMode = projection.ContributionMode,
            CumulativeContributionPolicyJson = projection.ContributionPolicyJson,
            SummarySourceJson = projection.SummarySourceJson,
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
        // Tạm khóa luồng tổng hợp cũ; giữ nguyên triển khai bên dưới.
        tdtd_be.Services.WorkAssignments.LegacyAggregateRetirement.Reject();
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
        var reportAccess = await EnsureReportAccessAsync(entity, actorUserId, ct);
        if (!reportAccess.isAssignee || entity.AssigneeUserId != actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_SAVE_FORBIDDEN,
                ReportDetails(entity, actorUserId));
        EnsureReportIsActive(entity);
        await HydrateReportPayloadAsync(entity, ct);

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

        entity.Values1DJson = Values1DCompression.Serialize(projection.TopLevelValues, _jsonOptions);
        entity.TableValuesJson = Values1DCompression.CompressTableValuesJson(projection.TableValuesJson, _jsonOptions);
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
        entity.PayloadRevision = 0;
        entity.PayloadStatus = null;

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        return await MapToResponseAsync(entity, period, actorUserId, ct);
    }

    public async Task<DynamicFlowMappingPreviewResponse> PreviewDynamicFlowMappingAsync(
        string id,
        DynamicFlowMappingRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        if (string.IsNullOrWhiteSpace(id))
            throw ReportIdRequired(id);
        req ??= new DynamicFlowMappingRequest();
        EnsureDynamicFlowMappingRequestDoesNotOverrideConfig(req);

        var (entity, reportAccess, _) =
            await LoadDynamicFlowMappingDraftTargetAsync(
                id,
                actorUserId,
                ct);
        var result = await BuildDynamicFlowMappingProjectionAsync(
            entity,
            reportAccess.assignment,
            req,
            actorUserId,
            ct,
            issuePreviewToken:
                IsDynamicFlowMappingPreviewTokenEnabled());
        var projection =
            DynamicFlowReportPermissionEnforcer.ApplyMappingPreviewReadRestrictions(
                result.Permissions,
                result.Projection);
        await RedactDynamicFlowMappingSourceIdentityWithoutRawAccessAsync(
            projection,
            result.Sources,
            actorUserId,
            ct);
        return projection;
    }

    public async Task<WorkAssignmentReportResponse> ApplyDynamicFlowMappingAsync(
        string id,
        DynamicFlowMappingRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        if (string.IsNullOrWhiteSpace(id))
            throw ReportIdRequired(id);
        EnsureDynamicFlowMappingApplyEnabled();

        req ??= new DynamicFlowMappingRequest();
        EnsureDynamicFlowMappingRequestDoesNotOverrideConfig(req);
        var (receiptTarget, receiptAccess) =
            await LoadDynamicFlowMappingAuthorizedTargetAsync(
                id,
                actorUserId,
                ct);
        if (!IsDynamicFlowMappingApplyEnabled())
        {
            return await PreflightDynamicFlowMappingApplyAsync(
                receiptTarget,
                receiptAccess,
                req,
                actorUserId,
                ct);
        }

        var commandId = ResolveDynamicFlowMappingCommandId(
            receiptTarget,
            req);
        var requestHash = ComputeDynamicFlowMappingApplyRequestHash(
            receiptTarget,
            req,
            actorUserId,
            commandId);
        var existingReceipt = await LoadDynamicFlowMappingReceiptAsync(
            receiptTarget.Id,
            commandId,
            ct);
        if (existingReceipt is not null)
        {
            ValidateDynamicFlowMappingReceiptReplayRequest(
                existingReceipt,
                commandId,
                requestHash,
                actorUserId);
            var replayCommand = ResolveDynamicFlowMappingApplyCommand(
                receiptTarget,
                req,
                actorUserId);
            ValidateDynamicFlowMappingReceiptReplay(
                existingReceipt,
                replayCommand,
                actorUserId);
            await EnsureDynamicFlowMappingProvenanceIntegrityAsync(
                receiptTarget,
                ct);
            await ReconcileDynamicFlowMappingOutboxAsync(
                existingReceipt.OutboxIntentId,
                ct);
            return await MapDynamicFlowMappingReceiptReplayResponseAsync(
                existingReceipt,
                actorUserId,
                ct);
        }

        var entity = receiptTarget;
        var reportAccess = receiptAccess;
        var period = await ValidateDynamicFlowMappingDraftTargetAsync(
            entity,
            reportAccess,
            actorUserId,
            ct);
        var applyCommand = ResolveDynamicFlowMappingApplyCommand(
            entity,
            req,
            actorUserId);
        await EnsureReportMutationScopeOpenAsync(reportAccess.assignment, actorUserId, ct, entity.WorkReportPeriodId);
        var successorPlan =
            await BuildDynamicFlowMappingSuccessorAsync(entity, ct);

        var mappingRuntime = await ResolveDynamicFlowMappingRuntimeAsync(
            reportAccess.assignment,
            entity,
            req,
            ct);
        var projectionResult = await BuildDynamicFlowMappingProjectionAsync(
            entity,
            reportAccess.assignment,
            req,
            actorUserId,
            ct,
            mappingRuntime,
            issuePreviewToken: false);
        var projection = projectionResult.Projection;
        EnsureDynamicFlowMappingExpectedTarget(
            entity,
            applyCommand,
            reportAccess.assignment);
        EnsureDynamicFlowMappingPreviewParity(
            entity,
            reportAccess.assignment,
            applyCommand,
            projection);

        DynamicFlowMappingPreviewTokenClaims previewClaims;
        try
        {
            previewClaims = DynamicFlowMappingSecurityContract.ValidatePreviewToken(
                req.PreviewToken,
                _configuration["DynamicFlowMapping:PreviewTokenSigningKey"],
                new DynamicFlowMappingPreviewTokenBinding(
                    actorUserId,
                    entity.Id,
                    reportAccess.assignment.Id,
                    mappingRuntime.FlowInstance.Id,
                    mappingRuntime.TargetStep.ExecutionEpoch,
                    entity.PayloadRevision,
                    entity.LifecycleRevision,
                    projection.SourceSignature,
                    projection.ResultSemanticHash,
                    mappingRuntime.RuleSetHash,
                    ComputeDynamicFlowMappingAuthorizationScopeHash(
                        reportAccess.assignment,
                        entity,
                        mappingRuntime,
                        projectionResult.Permissions,
                        actorUserId)),
                DateTime.UtcNow);
        }
        catch (DynamicFlowMappingSecurityException error)
        {
            throw DynamicFlowMappingPreviewTokenFailure(entity, error);
        }

        if (projection.HasBlockingConflicts)
            throw DynamicFlowMappingBlockingConflict();

        // P7-07 deliberately runs the complete canonical apply preflight,
        // including authorization, source/rule re-resolution, parity, and token
        // validation. The durable writer remains behind the P7-08 barrier.
        EnsureDynamicFlowMappingPersistenceEnabled();

        var now = DateTime.UtcNow;
        var fromStatus = entity.Status;
        var values1DJson = ResolveDynamicFlowMappingTopLevelValuesJson(
            entity.Values1DJson,
            projection.TableValuesJson,
            projectionResult.TopLevelBlockId,
            _jsonOptions);
        values1DJson = NormalizeDynamicFlowMappingTopLevelValueSlots(
            values1DJson,
            ResolveReportRuntimeInputCells(entity).Count,
            _jsonOptions);
        var values = Values1DCompression.DeserializeObjects(values1DJson, _jsonOptions);
        var runtimeTopLevelBlock = await ResolveRuntimeTopLevelBlockShapeAsync(
            entity,
            projection.TableValuesJson,
            values.Count,
            ct);
        if (runtimeTopLevelBlock is not null)
            ApplyRuntimeTopLevelShape(entity, runtimeTopLevelBlock);
        await ValidateRuntimeRowLabelsAsync(entity, projection.TableValuesJson, ct);
        var canonicalMappedFieldValuesJson = await ValidateRuntimeDataPayloadAsync(
            entity,
            values,
            projection.FieldValuesJson,
            projection.TableValuesJson,
            validateRequiredFields: false,
            ct);

        var completedDatePolicy = ResolveReportCompletedDatePolicy(reportAccess.assignment, entity, period, now);
        var isHistoricalData = IsHistoricalReportData(entity, period, completedDatePolicy);
        var startedDate = ResolveServerReportStartedDate(entity, period);
        var completedDate = ValidateReportCompletedDateInput(
            completedDatePolicy,
            entity.CompletedDate,
            ReportDetails(entity, actorUserId),
            requireWhenMissing: false);
        EnsureReportDateRange(startedDate, completedDate, "StartedDate", "CompletedDate");
        var effectiveDueAtUtc = ResolveEffectiveReportDueAtUtc(entity.DueAtUtc ?? period?.DueAtUtc, reportAccess.assignment);
        var tableValuesJson = Values1DCompression.CompressTableValuesJson(projection.TableValuesJson, _jsonOptions);
        var sourceSnapshot = ExtractAggregateSourceSnapshot(projection.SummarySourceJson);

        var payloadResult = WorkReportPayloadService.PreflightReportPayload(
            entity,
            values1DJson,
            canonicalMappedFieldValuesJson,
            tableValuesJson,
            projection.SummarySourceJson,
            actorUserId,
            now);
        var periodStatus = period is null
            ? (WorkReportPeriodStatus?)null
            : ResolveDraftPeriodStatus(
                isHistoricalData,
                completedDate,
                effectiveDueAtUtc,
                now);
        var persistence = BuildDynamicFlowMappingPersistenceBundle(
            entity,
            reportAccess.assignment,
            period,
            projectionResult,
            applyCommand,
            successorPlan,
            previewClaims,
            payloadResult,
            sourceSnapshot,
            startedDate,
            completedDate,
            isHistoricalData,
            effectiveDueAtUtc,
            fromStatus,
            periodStatus,
            actorUserId,
            now);
        var payloadCommand = new PayloadMutationCommand(
            applyCommand.ExpectedPayloadRevision,
            applyCommand.CommandId,
            "APPLY_DYNAMIC_FLOW_MAPPING",
            applyCommand.RequestHash);
        var committedReport = BuildDynamicFlowMappingCommittedReport(
            entity,
            persistence,
            payloadResult,
            payloadCommand,
            projection,
            sourceSnapshot,
            values1DJson,
            canonicalMappedFieldValuesJson,
            tableValuesJson,
            startedDate,
            completedDate,
            isHistoricalData,
            effectiveDueAtUtc,
            actorUserId,
            now);

        DynamicFlowMappingTransactionOutcome transactionOutcome;
        try
        {
            transactionOutcome = await _dynamicFlowTransactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    var claimedReceipt = await _ctx.DynamicFlowMappingApplyReceipts
                        .Find(
                            session,
                            receipt =>
                                receipt.TargetReportId == entity.Id &&
                                receipt.CommandId == applyCommand.CommandId)
                        .FirstOrDefaultAsync(transactionCt);
                    if (claimedReceipt is not null)
                    {
                        ValidateDynamicFlowMappingReceiptReplay(
                            claimedReceipt,
                            applyCommand,
                            actorUserId);
                        return new DynamicFlowMappingTransactionOutcome(
                            claimedReceipt,
                            IsReplay: true);
                    }

                    var boundaryReport =
                        await RevalidateDynamicFlowMappingWriteBoundaryAsync(
                            session,
                            entity,
                            reportAccess.assignment,
                            projectionResult,
                            applyCommand,
                            actorUserId,
                            transactionCt);

                    _dynamicFlowMappingApplyFaultInjector.ThrowIfConfigured(
                        applyCommand.CommandId,
                        DynamicFlowMappingApplyFaultPoints.BeforeReceipt);
                    await _ctx.DynamicFlowMappingApplyReceipts.InsertOneAsync(
                        session,
                        persistence.Receipt,
                        cancellationToken: transactionCt);
                    _dynamicFlowMappingApplyFaultInjector.ThrowIfConfigured(
                        applyCommand.CommandId,
                        DynamicFlowMappingApplyFaultPoints.AfterReceipt);

                    _dynamicFlowMappingApplyFaultInjector.ThrowIfConfigured(
                        applyCommand.CommandId,
                        DynamicFlowMappingApplyFaultPoints.BeforePayload);
                    var writtenPayload = await _payloadWriter.SaveReportPayloadAsync(
                        boundaryReport,
                        values1DJson,
                        canonicalMappedFieldValuesJson,
                        tableValuesJson,
                        projection.SummarySourceJson,
                        actorUserId,
                        now,
                        transactionCt,
                        session);
                    EnsureDynamicFlowMappingPayloadWriteMatches(
                        payloadResult,
                        writtenPayload,
                        boundaryReport);
                    _dynamicFlowMappingApplyFaultInjector.ThrowIfConfigured(
                        applyCommand.CommandId,
                        DynamicFlowMappingApplyFaultPoints.AfterPayload);

                    var headerCommit = await _ctx.WorkAssignmentReports.UpdateOneAsync(
                        session,
                        BuildDynamicFlowMappingCommitFilter(
                            boundaryReport,
                            applyCommand),
                        ApplyDynamicFlowMappingHeaderCommit(
                            committedReport,
                            payloadCommand,
                            payloadResult,
                            persistence),
                        cancellationToken: transactionCt);
                    if (headerCommit.ModifiedCount != 1)
                    {
                        throw DynamicFlowMappingTargetRevisionConflict(
                            boundaryReport,
                            applyCommand,
                            "DYNAMIC_FLOW_MAPPING_TARGET_CAS_LOST");
                    }

                    await CommitDynamicFlowMappingSuccessorAsync(
                        session,
                        successorPlan,
                        now,
                        transactionCt);

                    if (period is not null && periodStatus.HasValue)
                    {
                        var periodCommit = await _ctx.WorkReportPeriods.UpdateOneAsync(
                            session,
                            periodRow =>
                                periodRow.Id == period.Id &&
                                periodRow.WorkAssignmentId ==
                                boundaryReport.WorkAssignmentId &&
                                periodRow.CurrentReportId ==
                                boundaryReport.Id &&
                                !periodRow.IsDeleted,
                            BuildDynamicFlowMappingPeriodUpdate(
                                periodStatus.Value,
                                startedDate,
                                completedDate,
                                isHistoricalData,
                                effectiveDueAtUtc,
                                actorUserId,
                                now),
                            cancellationToken: transactionCt);
                        if (periodCommit.MatchedCount != 1)
                        {
                            throw DynamicFlowMappingTargetRevisionConflict(
                                boundaryReport,
                                applyCommand,
                                "DYNAMIC_FLOW_MAPPING_PERIOD_CAS_LOST");
                        }
                    }

                    _dynamicFlowMappingApplyFaultInjector.ThrowIfConfigured(
                        applyCommand.CommandId,
                        DynamicFlowMappingApplyFaultPoints.BeforeProjection);
                    await _sectionProjection.ProjectAndVerifyAsync(
                        committedReport,
                        canonicalMappedFieldValuesJson,
                        tableValuesJson,
                        actorUserId,
                        now,
                        transactionCt,
                        session);
                    _dynamicFlowMappingApplyFaultInjector.ThrowIfConfigured(
                        applyCommand.CommandId,
                        DynamicFlowMappingApplyFaultPoints.AfterProjection);

                    // Provenance plus the mapping event are the immutable
                    // committed-intent envelope consumed by reconciliation.
                    _dynamicFlowMappingApplyFaultInjector.ThrowIfConfigured(
                        applyCommand.CommandId,
                        DynamicFlowMappingApplyFaultPoints.BeforeIntentCommit);
                    await _ctx.DynamicFlowMappingProvenanceRecords.InsertOneAsync(
                        session,
                        persistence.Provenance,
                        cancellationToken: transactionCt);
                    await _ctx.DynamicFlowMappingEvents.InsertOneAsync(
                        session,
                        persistence.Event,
                        cancellationToken: transactionCt);
                    _dynamicFlowMappingApplyFaultInjector.ThrowIfConfigured(
                        applyCommand.CommandId,
                        DynamicFlowMappingApplyFaultPoints.AfterIntentCommit);

                    _dynamicFlowMappingApplyFaultInjector.ThrowIfConfigured(
                        applyCommand.CommandId,
                        DynamicFlowMappingApplyFaultPoints.BeforeOutbox);
                    await _ctx.DynamicFlowMappingOutbox.InsertOneAsync(
                        session,
                        persistence.Outbox,
                        cancellationToken: transactionCt);
                    _dynamicFlowMappingApplyFaultInjector.ThrowIfConfigured(
                        applyCommand.CommandId,
                        DynamicFlowMappingApplyFaultPoints.AfterOutbox);

                    _dynamicFlowMappingApplyFaultInjector.ThrowIfConfigured(
                        applyCommand.CommandId,
                        DynamicFlowMappingApplyFaultPoints.BeforeAudit);
                    await _ctx.WorkAssignmentReportLogs.InsertOneAsync(
                        session,
                        persistence.AuditLog,
                        cancellationToken: transactionCt);
                    _dynamicFlowMappingApplyFaultInjector.ThrowIfConfigured(
                        applyCommand.CommandId,
                        DynamicFlowMappingApplyFaultPoints.AfterAudit);

                    // This is the last callback boundary before
                    // IDynamicFlowDefinitionTransactionRunner commits.
                    _dynamicFlowMappingApplyFaultInjector.ThrowIfConfigured(
                        applyCommand.CommandId,
                        DynamicFlowMappingApplyFaultPoints
                            .BeforeTransactionCommit);

                    return new DynamicFlowMappingTransactionOutcome(
                        persistence.Receipt,
                        IsReplay: false);
                },
                ct);
            // ExecuteAsync returned only after the Mongo transaction commit.
            // A one-shot failure here proves committed replay/reconcile.
            if (!transactionOutcome.IsReplay)
            {
                _dynamicFlowMappingApplyFaultInjector.ThrowIfConfigured(
                    applyCommand.CommandId,
                    DynamicFlowMappingApplyFaultPoints
                        .AfterTransactionCommit);
            }
        }
        catch (AppException error) when (
            error.Code == AppErrorCode.DYNAMIC_FLOW_TRANSACTION_UNSUPPORTED)
        {
            throw new AppException(
                AppErrorCode.DYNAMIC_FLOW_MAPPING_TRANSACTION_REQUIRED,
                new
                {
                    reportId = entity.Id,
                    commandId = applyCommand.CommandId,
                    reason = "DYNAMIC_FLOW_MAPPING_TRANSACTION_REQUIRED"
                },
                innerException: error);
        }
        catch (Exception error) when (IsMongoDuplicateKey(error))
        {
            var winner = await LoadDynamicFlowMappingReceiptAsync(
                entity.Id,
                applyCommand.CommandId,
                ct);
            if (winner is null)
                throw;

            ValidateDynamicFlowMappingReceiptReplay(
                winner,
                applyCommand,
                actorUserId);
            transactionOutcome = new DynamicFlowMappingTransactionOutcome(
                winner,
                IsReplay: true);
        }

        if (transactionOutcome.IsReplay)
        {
            await ReconcileDynamicFlowMappingOutboxAsync(
                transactionOutcome.Receipt.OutboxIntentId,
                ct);
            return await MapDynamicFlowMappingReceiptReplayResponseAsync(
                transactionOutcome.Receipt,
                actorUserId,
                ct);
        }

        ApplyDynamicFlowMappingPeriodInMemory(
            period,
            periodStatus,
            startedDate,
            completedDate,
            isHistoricalData,
            effectiveDueAtUtc,
            actorUserId,
            now);
        await ReconcileDynamicFlowMappingOutboxAsync(
            persistence.Outbox.Id,
            ct);
        return await MapToResponseAsync(committedReport, period, actorUserId, ct);
    }

    private async Task<WorkAssignmentReportResponse>
        PreflightDynamicFlowMappingApplyAsync(
            WorkAssignmentReport entity,
            (
                WorkAssignment assignment,
                bool isOwner,
                bool isAssignee
            ) reportAccess,
            DynamicFlowMappingRequest request,
            string actorUserId,
            CancellationToken ct)
    {
        _ = await ValidateDynamicFlowMappingDraftTargetAsync(
            entity,
            reportAccess,
            actorUserId,
            ct);
        var applyCommand = ResolveDynamicFlowMappingApplyCommand(
            entity,
            request,
            actorUserId);
        await EnsureReportMutationScopeOpenAsync(
            reportAccess.assignment,
            actorUserId,
            ct, entity.WorkReportPeriodId);

        var mappingRuntime = await ResolveDynamicFlowMappingRuntimeAsync(
            reportAccess.assignment,
            entity,
            request,
            ct);
        var projectionResult = await BuildDynamicFlowMappingProjectionAsync(
            entity,
            reportAccess.assignment,
            request,
            actorUserId,
            ct,
            mappingRuntime,
            issuePreviewToken: false);
        var projection = projectionResult.Projection;
        EnsureDynamicFlowMappingExpectedTarget(
            entity,
            applyCommand,
            reportAccess.assignment);
        EnsureDynamicFlowMappingPreviewParity(
            entity,
            reportAccess.assignment,
            applyCommand,
            projection);

        try
        {
            _ = DynamicFlowMappingSecurityContract.ValidatePreviewToken(
                request.PreviewToken,
                _configuration["DynamicFlowMapping:PreviewTokenSigningKey"],
                new DynamicFlowMappingPreviewTokenBinding(
                    actorUserId,
                    entity.Id,
                    reportAccess.assignment.Id,
                    mappingRuntime.FlowInstance.Id,
                    mappingRuntime.TargetStep.ExecutionEpoch,
                    entity.PayloadRevision,
                    entity.LifecycleRevision,
                    projection.SourceSignature,
                    projection.ResultSemanticHash,
                    mappingRuntime.RuleSetHash,
                    ComputeDynamicFlowMappingAuthorizationScopeHash(
                        reportAccess.assignment,
                        entity,
                        mappingRuntime,
                        projectionResult.Permissions,
                        actorUserId)),
                DateTime.UtcNow);
        }
        catch (DynamicFlowMappingSecurityException error)
        {
            throw DynamicFlowMappingPreviewTokenFailure(entity, error);
        }

        if (projection.HasBlockingConflicts)
            throw DynamicFlowMappingBlockingConflict();

        EnsureDynamicFlowMappingPersistenceEnabled();
        throw new InvalidOperationException(
            "P7-08 mapping persistence barrier did not block P7-07 preflight.");
    }

    private async Task<(WorkAssignmentReport entity, (WorkAssignment assignment, bool isOwner, bool isAssignee) reportAccess, WorkReportPeriod? period)>
        LoadDynamicFlowMappingDraftTargetAsync(
            string id,
            string actorUserId,
            CancellationToken ct)
    {
        var (entity, reportAccess) =
            await LoadDynamicFlowMappingAuthorizedTargetAsync(
                id,
                actorUserId,
                ct);
        var period = await ValidateDynamicFlowMappingDraftTargetAsync(
            entity,
            reportAccess,
            actorUserId,
            ct);
        return (entity, reportAccess, period);
    }

    private async Task<(WorkAssignmentReport entity, (WorkAssignment assignment, bool isOwner, bool isAssignee) reportAccess)>
        LoadDynamicFlowMappingAuthorizedTargetAsync(
            string id,
            string actorUserId,
            CancellationToken ct)
    {
        EnsureActor(actorUserId);

        if (string.IsNullOrWhiteSpace(id))
            throw ReportIdRequired(id);

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw DynamicFlowMappingTargetAccessForbidden();
        (WorkAssignment assignment, bool isOwner, bool isAssignee)
            reportAccess;
        try
        {
            reportAccess =
                await EnsureReportAccessAsync(
                    entity,
                    actorUserId,
                    ct);
        }
        catch (AppException error) when (
            error.Code is
                AppErrorCode.WORK_ASSIGNMENT_REPORT_ACCESS_FORBIDDEN or
                AppErrorCode.WORK_ASSIGNMENT_REPORT_ASSIGNMENT_NOT_FOUND)
        {
            throw DynamicFlowMappingTargetAccessForbidden();
        }
        if (!reportAccess.isAssignee || entity.AssigneeUserId != actorUserId)
            throw DynamicFlowMappingTargetAccessForbidden();

        return (entity, reportAccess);
    }

    private async Task<WorkReportPeriod?>
        ValidateDynamicFlowMappingDraftTargetAsync(
            WorkAssignmentReport entity,
            (WorkAssignment assignment, bool isOwner, bool isAssignee)
                reportAccess,
            string actorUserId,
            CancellationToken ct)
    {
        EnsureReportIsActive(entity);

        if (DynamicFlowBranchVisibility.IsFlowAssignment(reportAccess.assignment) &&
            !string.Equals(
                reportAccess.assignment.FlowEffectiveStatus,
                DynamicFlowEffectiveStatuses.Effective,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
                new
                {
                    reportId = entity.Id,
                    workAssignmentId = entity.WorkAssignmentId,
                    reportAccess.assignment.FlowEffectiveStatus,
                    reason = "DYNAMIC_FLOW_MAPPING_TARGET_NOT_EFFECTIVE"
                });
        }

        if (entity.Status != WorkAssignmentReportStatus.Draft)
            throw InvalidReportStatus(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_SAVE_STATUS_INVALID,
                entity,
                WorkAssignmentReportStatus.Draft,
                actorUserId);

        if (string.IsNullOrWhiteSpace(entity.DynamicFormTemplateId))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_AGGREGATE_DRAFT_TEMPLATE_MISMATCH,
                ReportDetails(entity, actorUserId));
        }

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        return period;
    }

    private async Task<DynamicFlowMappingProjectionResult> BuildDynamicFlowMappingProjectionAsync(
        WorkAssignmentReport entity,
        WorkAssignment assignment,
        DynamicFlowMappingRequest? req,
        string actorUserId,
        CancellationToken ct,
        DynamicFlowMappingRuntimeContext? resolvedRuntime = null,
        bool issuePreviewToken = true)
    {
        req ??= new DynamicFlowMappingRequest();
        var runtime = resolvedRuntime ??
                      await ResolveDynamicFlowMappingRuntimeAsync(
                          assignment,
                          entity,
                          req,
                          ct);
        var rules = runtime.Rules;
        var permissions = ResolveDynamicFlowMappingTargetPermissions(
            assignment,
            entity,
            runtime,
            actorUserId,
            ct);
        EnsureDynamicFlowMappingTargetRulesWritable(
            entity,
            assignment,
            rules,
            permissions);

        var sources = await ResolveCanonicalDynamicFlowMappingSourceReportsAsync(
            entity,
            assignment,
            runtime,
            actorUserId,
            ct);
        var requiresSource = rules.Any(RuleRequiresDynamicFlowMappingSource);
        if (sources.Count == 0 && requiresSource)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
                new
                {
                    reportId = entity.Id,
                    workAssignmentId = entity.WorkAssignmentId,
                    actorUserId,
                    reason = "DYNAMIC_FLOW_MAPPING_SOURCE_REPORT_NOT_FOUND"
                });
        }

        await HydrateReportPayloadAsync(entity, ct);
        foreach (var source in sources)
            await HydrateReportPayloadAsync(source.Report, ct);

        var topLevelBlockId = await EnsureDynamicFlowMappingTargetTableShapeAsync(entity, rules, ct);
        var now = DateTime.UtcNow;
        var projection = DynamicFlowMappingEngine.Preview(
            entity,
            sources,
            rules,
            requestConflictPolicy: null,
            requestContributionPolicy: null,
            now,
            assignment.FlowStepId,
            assignment.FlowStepCode,
            enforceP7Contract: true);

        var writeViolations = DynamicFlowReportPermissionEnforcer.FindWriteViolations(
            permissions,
            entity.FieldValuesJson,
            projection.FieldValuesJson,
            entity.TableValuesJson,
            projection.TableValuesJson);
        if (writeViolations.Count > 0)
        {
            throw DynamicFlowMappingPolicyDenied(
                entity,
                assignment,
                "DYNAMIC_FLOW_MAPPING_TARGET_WRITE_FORBIDDEN",
                writeViolations);
        }

        var requiredViolations =
            DynamicFlowReportPermissionEnforcer.FindRequiredViolations(
                permissions,
                projection.FieldValuesJson,
                projection.TableValuesJson);
        var requiredValuesMissing = requiredViolations.Count > 0;
        if (requiredValuesMissing)
            projection.HasBlockingConflicts = true;

        DynamicFlowMappingRuntimeContract.PopulateResponseIdentity(
            projection,
            runtime,
            entity);
        projection.SourceSignature =
            DynamicFlowMappingRuntimeContract.ComputeSourceSignature(
                runtime,
                entity,
                sources);
        projection.ResultSemanticHash =
            DynamicFlowMappingRuntimeContract.ComputeResultSemanticHash(projection);
        projection.MappingCapability = projection.HasBlockingConflicts
            ? DynamicFlowMappingRuntimeContract.CapabilityBlocked
            : DynamicFlowMappingRuntimeContract.CapabilityAllowed;
        projection.MappingCapabilityReason = requiredValuesMissing
            ? "DYNAMIC_FLOW_MAPPING_REQUIRED_VALUE_MISSING"
            : projection.HasBlockingConflicts
                ? "DYNAMIC_FLOW_MAPPING_CONFLICT"
                : "DYNAMIC_FLOW_MAPPING_CAPABILITY_ALLOWED";
        projection.CanPreview = true;
        projection.CanApply =
            IsDynamicFlowMappingApplyEnabled() &&
            !projection.HasBlockingConflicts;
        projection.Freshness = DynamicFlowMappingRuntimeContract.Fresh;

        if (issuePreviewToken)
        {
            var tokenKey = _configuration[
                "DynamicFlowMapping:PreviewTokenSigningKey"];
            try
            {
                projection.PreviewToken =
                    DynamicFlowMappingSecurityContract.IssuePreviewToken(
                        new DynamicFlowMappingPreviewTokenBinding(
                            actorUserId,
                            entity.Id,
                            assignment.Id,
                            runtime.FlowInstance.Id,
                            runtime.TargetStep.ExecutionEpoch,
                            entity.PayloadRevision,
                            entity.LifecycleRevision,
                            projection.SourceSignature,
                            projection.ResultSemanticHash,
                            runtime.RuleSetHash,
                            ComputeDynamicFlowMappingAuthorizationScopeHash(
                                assignment,
                                entity,
                                runtime,
                                permissions,
                                actorUserId)),
                        ObjectId.GenerateNewId().ToString(),
                        tokenKey,
                        now);
                projection.PreviewIssuedAtUtc = now;
                projection.PreviewExpiresAtUtc =
                    now.Add(DynamicFlowMappingSecurityContract.PreviewTokenTtl);
            }
            catch (DynamicFlowMappingSecurityException error)
            {
                throw AppExceptionFactory.Create(
                    AppErrorCode.DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_KEY_UNAVAILABLE,
                    new { reason = error.Reason });
            }
        }

        return new DynamicFlowMappingProjectionResult(
            projection,
            topLevelBlockId,
            runtime,
            sources,
            permissions);
    }

    private DynamicFlowPolicyEvaluationResult
        ResolveDynamicFlowMappingTargetPermissions(
            WorkAssignment assignment,
            WorkAssignmentReport report,
            DynamicFlowMappingRuntimeContext runtime,
            string actorUserId,
            CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var actorRole = ResolveDirectDynamicFlowActorRole(
            assignment,
            report,
            actorUserId);
        if (actorRole is null)
        {
            throw DynamicFlowMappingPolicyDenied(
                report,
                assignment,
                "DYNAMIC_FLOW_MAPPING_POLICY_NOT_LOADED");
        }

        try
        {
            return _dynamicFlowPolicyEvaluator.Evaluate(
                runtime.FlowVersion.PayloadJson,
                new DynamicFlowPolicyEvaluationContext
                {
                    StepId = runtime.TargetStep.FlowStepId,
                    StepCode = runtime.TargetStep.FlowStepCode,
                    ActorRole = actorRole,
                    IsAfterSubmit =
                        IsDynamicFlowReportAfterSubmit(report)
                });
        }
        catch (Exception error) when (
            error is AppException or
            InvalidOperationException or
            JsonException)
        {
            throw DynamicFlowMappingPolicyDenied(
                report,
                assignment,
                "DYNAMIC_FLOW_MAPPING_TARGET_POLICY_LOAD_FAILED");
        }
    }

    private async Task RedactDynamicFlowMappingSourceIdentityWithoutRawAccessAsync(
        DynamicFlowMappingPreviewResponse projection,
        IReadOnlyCollection<DynamicFlowMappingSourceReport> sources,
        string actorUserId,
        CancellationToken ct)
    {
        var redactedReportIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var hasRawAccess =
                string.Equals(
                    source.Report.AssigneeUserId,
                    actorUserId,
                    StringComparison.Ordinal) ||
                await WorkAssignmentReadAccessHelper.CanReadAssignmentAsync(
                    _ctx,
                    source.Report.WorkAssignmentId,
                    actorUserId,
                    ct);
            if (!hasRawAccess)
                redactedReportIds.Add(source.Report.Id);
        }

        if (redactedReportIds.Count == 0)
            return;
        DynamicFlowMappingCallerRedaction.RedactSourceIdentities(
            projection,
            redactedReportIds);
    }

    private async Task<string?> EnsureDynamicFlowMappingTargetTableShapeAsync(
        WorkAssignmentReport report,
        IReadOnlyList<DynamicFlowMappingRuleDto> rules,
        CancellationToken ct)
    {
        var targetBlockIds = rules
            .Select(rule => NormalizeOptionalTextOrNull(rule.Target?.BlockId) ?? NormalizeOptionalTextOrNull(rule.TargetBlockId))
            .Where(blockId => !string.IsNullOrWhiteSpace(blockId))
            .Select(blockId => blockId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (targetBlockIds.Count == 0 || string.IsNullOrWhiteSpace(report.DynamicFormTemplateId))
            return null;

        var formId = report.DynamicFormTemplateId.Trim();
        var form = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == formId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (form is null)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
                new
                {
                    reportId = report.Id,
                    dynamicFormTemplateId = formId,
                    reason = "DYNAMIC_FLOW_MAPPING_TARGET_FORM_NOT_FOUND"
                });
        }

        var contracts = ReadRuntimeTableBlocks(form);
        var expanded = Values1DCompression.ExpandTableValuesJson(report.TableValuesJson, _jsonOptions);
        JsonObject root;
        try
        {
            if (string.IsNullOrWhiteSpace(expanded))
            {
                root = new JsonObject();
            }
            else
            {
                root = JsonNode.Parse(expanded) as JsonObject
                    ?? throw AppExceptionFactory.BadRequest(
                        AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
                        new
                        {
                            reportId = report.Id,
                            field = "tableValuesJson",
                            reason = "DYNAMIC_FLOW_MAPPING_TARGET_TABLE_VALUES_OBJECT_REQUIRED"
                        });
            }
        }
        catch (JsonException)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
                new
                {
                    reportId = report.Id,
                    field = "tableValuesJson",
                    reason = "DYNAMIC_FLOW_MAPPING_TARGET_TABLE_VALUES_INVALID"
                });
        }

        var blocks = root["blocks"] as JsonArray;
        if (blocks is null)
        {
            if (root["blocks"] is not null)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
                    new
                    {
                        reportId = report.Id,
                        field = "tableValuesJson.blocks",
                        reason = "DYNAMIC_FLOW_MAPPING_TARGET_TABLE_BLOCKS_ARRAY_REQUIRED"
                    });
            }

            blocks = new JsonArray();
            root["blocks"] = blocks;
        }

        var changed = false;
        foreach (var blockId in targetBlockIds)
        {
            var normalizedBlockId = NormalizeBlockId(blockId);
            if (!contracts.TryGetValue(normalizedBlockId, out var contract))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
                    new
                    {
                        reportId = report.Id,
                        dynamicFormTemplateId = formId,
                        blockId = normalizedBlockId,
                        reason = "DYNAMIC_FLOW_MAPPING_TARGET_TABLE_BLOCK_NOT_FOUND"
                    });
            }

            var matchingBlocks = blocks
                .OfType<JsonObject>()
                .Where(block => string.Equals(
                    NormalizeBlockId(ReadJsonNodeString(block, "blockId", "id")),
                    contract.BlockId,
                    StringComparison.Ordinal))
                .ToList();
            if (matchingBlocks.Count > 1)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
                    new
                    {
                        reportId = report.Id,
                        blockId = contract.BlockId,
                        reason = "DYNAMIC_FLOW_MAPPING_TARGET_TABLE_BLOCK_DUPLICATE"
                    });
            }

            var runtimeBlock = matchingBlocks.FirstOrDefault();
            if (runtimeBlock is null)
            {
                runtimeBlock = new JsonObject();
                blocks.Add(runtimeBlock);
                changed = true;
            }
            else
            {
                var runtimeMode = ReadJsonNodeString(runtimeBlock, "tableMode")?.Trim().ToUpperInvariant();
                var contractMode = ReadJsonString(contract.Block, "tableMode")?.Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(contractMode))
                    contractMode = "FIXED_GRID";
                if (!string.IsNullOrWhiteSpace(runtimeMode) &&
                    !string.Equals(runtimeMode, contractMode, StringComparison.Ordinal))
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
                        new
                        {
                            reportId = report.Id,
                            blockId = contract.BlockId,
                            runtimeMode,
                            contractMode,
                            reason = "DYNAMIC_FLOW_MAPPING_TARGET_TABLE_MODE_MISMATCH"
                        });
                }
            }

            changed |= ApplyDynamicFlowMappingTargetTableShape(runtimeBlock, contract);
        }

        if (changed)
            report.TableValuesJson = root.ToJsonString(_jsonOptions);

        return ResolveTopLevelBlockId(form);
    }

    private static bool ApplyDynamicFlowMappingTargetTableShape(
        JsonObject runtimeBlock,
        RuntimeTableBlockContract contract)
    {
        var changed = false;
        changed |= SetJsonNodeIfMissing(runtimeBlock, "blockId", contract.BlockId);

        var tableMode = ReadJsonString(contract.Block, "tableMode")?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(tableMode))
            tableMode = "FIXED_GRID";
        changed |= SetJsonNodeIfMissing(runtimeBlock, "tableMode", tableMode);
        changed |= SetJsonNodeIfMissing(runtimeBlock, "w", contract.W);
        changed |= SetJsonNodeIfMissing(runtimeBlock, "h", contract.H);
        changed |= SetJsonNodeIfMissing(runtimeBlock, "dataRect", new JsonObject
        {
            ["r0"] = contract.DataRect.R0,
            ["c0"] = contract.DataRect.C0,
            ["r1"] = contract.DataRect.R1,
            ["c1"] = contract.DataRect.C1
        });

        var inputCells = ResolveRuntimeInputCells(contract.Block, contract.DataRect, contract.W, contract.H);
        if (runtimeBlock["values1D"] is not JsonArray values)
        {
            values = new JsonArray();
            runtimeBlock["values1D"] = values;
            changed = true;
        }
        while (values.Count < inputCells.Count)
        {
            values.Add(null);
            changed = true;
        }

        var expectedValueSlots = new JsonArray(inputCells.Select(cell => (JsonNode)new JsonObject
        {
            ["index"] = cell.Index,
            ["rowKey"] = $"row_{cell.R - contract.DataRect.R0 + 1}",
            ["columnKey"] = $"col_{cell.C - contract.DataRect.C0 + 1}",
            ["rowOffset"] = cell.R - contract.DataRect.R0,
            ["columnOffset"] = cell.C - contract.DataRect.C0,
            ["row"] = cell.R,
            ["column"] = cell.C
        }).ToArray());
        if (runtimeBlock["valueSlots"] is not JsonArray valueSlots ||
            !JsonNode.DeepEquals(valueSlots, expectedValueSlots))
        {
            runtimeBlock["valueSlots"] = expectedValueSlots;
            changed = true;
        }

        var hasSpecialRanges = ReadRuntimeSpecialRanges(contract.Block, contract.DataRect).Count > 0;
        changed |= SetJsonNodeIfMissing(runtimeBlock, "hasSpecialRanges", hasSpecialRanges);
        changed |= CopyJsonPropertyIfMissing(runtimeBlock, contract.Block, "dynamicExcelTemplateId");
        changed |= CopyJsonPropertyIfMissing(runtimeBlock, contract.Block, "indexMap");
        changed |= CopyJsonPropertyIfMissing(runtimeBlock, contract.Block, "statisticsDisabled");
        changed |= CopyJsonPropertyIfMissing(runtimeBlock, contract.Block, "statisticsInputCellCount");
        changed |= CopyJsonPropertyIfMissing(runtimeBlock, contract.Block, "statisticsInputCellLimit");
        changed |= CopyJsonPropertyIfMissing(runtimeBlock, contract.Block, "statisticsDisabledReason");

        if (tableMode == "APPEND_ROWS" && runtimeBlock["rows"] is not JsonArray)
        {
            runtimeBlock["rows"] = new JsonArray();
            changed = true;
        }
        else if (tableMode == "MATRIX" && runtimeBlock["cells"] is not JsonArray)
        {
            runtimeBlock["cells"] = new JsonArray();
            changed = true;
        }

        return changed;
    }

    private static string MergeDynamicFlowTopLevelValuesIntoTableValuesJson(
        WorkAssignmentReport report,
        string? tableValuesJson,
        RuntimeTableBlockContract contract,
        IReadOnlyList<object?> values,
        string actorUserId)
    {
        var contractMode = ReadJsonString(contract.Block, "tableMode")?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(contractMode))
            contractMode = "FIXED_GRID";
        if (contractMode != "FIXED_GRID")
        {
            throw RuntimeTableValuesInvalid(
                report,
                "DYNAMIC_FLOW_TOP_LEVEL_TABLE_VALUES_REQUIRED",
                new { actorUserId, blockId = contract.BlockId, tableMode = contractMode });
        }

        var expanded = Values1DCompression.ExpandTableValuesJson(tableValuesJson, _jsonOptions) ?? tableValuesJson;
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(expanded)
                ? new JsonObject()
                : JsonNode.Parse(expanded) as JsonObject
                  ?? throw RuntimeTableValuesInvalid(
                      report,
                      "DYNAMIC_FLOW_TOP_LEVEL_TABLE_VALUES_OBJECT_REQUIRED",
                      new { actorUserId, blockId = contract.BlockId });
        }
        catch (JsonException)
        {
            throw RuntimeTableValuesInvalid(
                report,
                "DYNAMIC_FLOW_TOP_LEVEL_TABLE_VALUES_INVALID",
                new { actorUserId, blockId = contract.BlockId });
        }

        var blocks = root["blocks"] as JsonArray;
        if (blocks is null)
        {
            if (root["blocks"] is not null)
            {
                throw RuntimeTableValuesInvalid(
                    report,
                    "DYNAMIC_FLOW_TOP_LEVEL_TABLE_BLOCKS_ARRAY_REQUIRED",
                    new { actorUserId, blockId = contract.BlockId });
            }

            blocks = new JsonArray();
            root["blocks"] = blocks;
        }

        var matchingBlocks = blocks
            .OfType<JsonObject>()
            .Where(block => string.Equals(
                NormalizeBlockId(ReadJsonNodeString(block, "blockId", "id")),
                contract.BlockId,
                StringComparison.Ordinal))
            .ToList();
        if (matchingBlocks.Count > 1)
        {
            throw RuntimeTableValuesInvalid(
                report,
                "DYNAMIC_FLOW_TOP_LEVEL_TABLE_BLOCK_DUPLICATE",
                new { actorUserId, blockId = contract.BlockId });
        }

        var runtimeBlock = matchingBlocks.FirstOrDefault();
        if (runtimeBlock is null)
        {
            runtimeBlock = new JsonObject();
            blocks.Add(runtimeBlock);
        }
        else
        {
            var runtimeMode = ReadJsonNodeString(runtimeBlock, "tableMode")?.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(runtimeMode) && runtimeMode != contractMode)
            {
                throw RuntimeTableValuesInvalid(
                    report,
                    "DYNAMIC_FLOW_TOP_LEVEL_TABLE_MODE_MISMATCH",
                    new { actorUserId, blockId = contract.BlockId, runtimeMode, contractMode });
            }
        }

        ApplyDynamicFlowMappingTargetTableShape(runtimeBlock, contract);
        runtimeBlock["values1D"] = JsonSerializer.SerializeToNode(values, _jsonOptions) as JsonArray
                                   ?? new JsonArray();
        return root.ToJsonString(_jsonOptions);
    }

    internal static string ResolveDynamicFlowMappingTopLevelValuesJson(
        string? currentValues1DJson,
        string? tableValuesJson,
        string? topLevelBlockId,
        JsonSerializerOptions jsonOptions)
    {
        var fallback = string.IsNullOrWhiteSpace(currentValues1DJson) ? EmptyValues1DJson : currentValues1DJson;
        if (string.IsNullOrWhiteSpace(tableValuesJson) || string.IsNullOrWhiteSpace(topLevelBlockId))
            return fallback;

        var expanded = Values1DCompression.ExpandTableValuesJson(tableValuesJson, jsonOptions) ?? tableValuesJson;
        try
        {
            using var document = JsonDocument.Parse(expanded);
            if (!TryGetJsonProperty(document.RootElement, "blocks", out var blocks) ||
                blocks.ValueKind != JsonValueKind.Array)
            {
                return fallback;
            }

            foreach (var block in blocks.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object ||
                    !string.Equals(
                        NormalizeBlockId(ReadJsonString(block, "blockId") ?? ReadJsonString(block, "id")),
                        NormalizeBlockId(topLevelBlockId),
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var values = Values1DCompression.ReadBlockObjects(block, jsonOptions);
                return values is null ? fallback : Values1DCompression.Serialize(values, jsonOptions);
            }
        }
        catch (JsonException)
        {
            return fallback;
        }

        return fallback;
    }

    internal static string NormalizeDynamicFlowMappingTopLevelValueSlots(
        string? values1DJson,
        int expectedLength,
        JsonSerializerOptions jsonOptions)
    {
        var normalized = string.IsNullOrWhiteSpace(values1DJson)
            ? EmptyValues1DJson
            : values1DJson;
        if (expectedLength <= 0)
            return normalized;

        var values = Values1DCompression.DeserializeObjects(
            normalized,
            jsonOptions);
        if (values.Count != 0)
            return normalized;

        return Values1DCompression.Serialize(
            Enumerable.Repeat<object?>(null, expectedLength).ToList(),
            jsonOptions);
    }

    private static bool SetJsonNodeIfMissing(JsonObject target, string propertyName, JsonNode? value)
    {
        if (target[propertyName] is not null)
            return false;

        target[propertyName] = value?.DeepClone();
        return true;
    }

    private static bool SetJsonNodeIfMissing(JsonObject target, string propertyName, string value)
        => SetJsonNodeIfMissing(target, propertyName, JsonValue.Create(value));

    private static bool SetJsonNodeIfMissing(JsonObject target, string propertyName, int value)
        => SetJsonNodeIfMissing(target, propertyName, JsonValue.Create(value));

    private static bool SetJsonNodeIfMissing(JsonObject target, string propertyName, bool value)
        => SetJsonNodeIfMissing(target, propertyName, JsonValue.Create(value));

    private static bool CopyJsonPropertyIfMissing(
        JsonObject target,
        JsonElement source,
        string propertyName)
    {
        if (target[propertyName] is not null || !TryGetJsonProperty(source, propertyName, out var value))
            return false;

        target[propertyName] = JsonNode.Parse(value.GetRawText());
        return true;
    }

    private static string? ReadJsonNodeString(JsonObject node, params string[] names)
    {
        foreach (var name in names)
        {
            if (node[name] is JsonValue value && value.TryGetValue<string>(out var text))
                return text;
        }

        return null;
    }

    private async Task<DynamicFlowMappingRuntimeContext> ResolveDynamicFlowMappingRuntimeAsync(
        WorkAssignment assignment,
        WorkAssignmentReport targetReport,
        DynamicFlowMappingRequest req,
        CancellationToken ct)
    {
        EnsureDynamicFlowMappingRequestDoesNotOverrideConfig(req);

        if (!DynamicFlowBranchVisibility.IsFlowAssignment(assignment) ||
            string.IsNullOrWhiteSpace(assignment.FlowInstanceId) ||
            string.IsNullOrWhiteSpace(assignment.FlowStepId) ||
            string.IsNullOrWhiteSpace(assignment.FlowBranchId) ||
            assignment.FlowExecutionEpoch is not > 0 ||
            assignment.FlowAttemptNo is not > 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
                new
                {
                    reportId = targetReport.Id,
                    workAssignmentId = assignment.Id,
                    reason = "DYNAMIC_FLOW_MAPPING_RUNTIME_PIN_MISSING"
                });
        }

        var instance = await _ctx.DynamicFlowInstances
            .Find(item =>
                item.Id == assignment.FlowInstanceId &&
                item.WorkId == assignment.WorkId &&
                item.ExecutionEpoch == assignment.FlowExecutionEpoch)
            .FirstOrDefaultAsync(ct);
        if (instance is null ||
            instance.State is not DynamicFlowInstanceStates.Active and
                not DynamicFlowInstanceStates.Completed and
                not DynamicFlowInstanceStates.Reconciled)
        {
            throw DynamicFlowMappingIdentityConflict(
                targetReport,
                assignment,
                "flowInstanceId",
                "DYNAMIC_FLOW_MAPPING_RUNTIME_INSTANCE_INVALID");
        }

        if (!_dynamicFlowRuntimeActivationPolicy.CanExecuteP7MappingPin(
                instance.CatalogVersion,
                instance.CatalogSemanticHash))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE,
                new
                {
                    field = "catalogVersion",
                    reason = "DYNAMIC_FLOW_MAPPING_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                    catalogVersion = instance.CatalogVersion,
                    catalogSemanticHash = instance.CatalogSemanticHash,
                    executionEligibility = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase,
                    blockedUntilPhase = "P7",
                    canExecute = false
                });
        }

        var version = await _ctx.DynamicFlowTemplateVersions
            .Find(item =>
                item.Id == instance.FlowTemplateVersionId &&
                item.TemplateId == instance.FlowTemplateId &&
                item.VersionNo == instance.FlowTemplateVersionNo &&
                item.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (version is null ||
            !string.Equals(version.PayloadHash, instance.FlowPayloadHash, StringComparison.Ordinal) ||
            !string.Equals(version.CatalogVersion, instance.CatalogVersion, StringComparison.Ordinal) ||
            !string.Equals(
                version.CatalogSemanticHash,
                instance.CatalogSemanticHash,
                StringComparison.Ordinal))
        {
            throw DynamicFlowMappingIdentityConflict(
                targetReport,
                assignment,
                "flowVersionId",
                "DYNAMIC_FLOW_MAPPING_LOCKED_FLOW_PIN_CONFLICT");
        }

        var family = await _ctx.DynamicFlowTemplates
            .Find(item => item.Id == instance.FlowTemplateId && !item.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw DynamicFlowMappingIdentityConflict(
                targetReport,
                assignment,
                "flowFamilyId",
                "DYNAMIC_FLOW_MAPPING_FLOW_FAMILY_NOT_FOUND");
        IReadOnlySet<string> referencedFormIds;
        try
        {
            referencedFormIds =
                DynamicFlowLockedSnapshotIntegrity.CollectReferencedFormIds(
                    new[] { version });
        }
        catch (AppException error) when (
            IsDynamicFlowMappingPolicySnapshotFailure(error))
        {
            throw DynamicFlowMappingPolicyDenied(
                targetReport,
                assignment,
                "DYNAMIC_FLOW_MAPPING_TARGET_POLICY_LOAD_FAILED");
        }
        var forms = await _ctx.DynamicFormTemplates
            .Find(item => referencedFormIds.Contains(item.Id) && !item.IsDeleted)
            .ToListAsync(ct);
        try
        {
            DynamicFlowLockedSnapshotIntegrity.Validate(
                family,
                new[] { version },
                forms.ToDictionary(item => item.Id, StringComparer.Ordinal));
        }
        catch (AppException error) when (
            IsDynamicFlowMappingPolicySnapshotFailure(error))
        {
            throw DynamicFlowMappingPolicyDenied(
                targetReport,
                assignment,
                "DYNAMIC_FLOW_MAPPING_TARGET_POLICY_LOAD_FAILED");
        }

        var targetStep = await _ctx.DynamicFlowStepInstances
            .Find(step =>
                step.FlowInstanceId == instance.Id &&
                step.ExecutionEpoch == instance.ExecutionEpoch &&
                step.AssignmentId == assignment.Id &&
                step.ReportId == targetReport.Id)
            .FirstOrDefaultAsync(ct);
        if (targetStep is null ||
            targetStep.IsCanonicalEpoch == false ||
            !string.IsNullOrWhiteSpace(targetStep.InvalidatedByFlowEventId) ||
            targetStep.InvalidatedAtUtc.HasValue ||
            !string.IsNullOrWhiteSpace(targetStep.SupersededByStepInstanceId) ||
            targetStep.State is not DynamicFlowStepStates.Assigned and
                not DynamicFlowStepStates.InProgress and
                not DynamicFlowStepStates.Returned)
        {
            throw DynamicFlowMappingIdentityConflict(
                targetReport,
                assignment,
                "stepInstanceId",
                "DYNAMIC_FLOW_MAPPING_TARGET_STEP_INVALID");
        }

        if (!string.Equals(assignment.FlowTemplateId, instance.FlowTemplateId, StringComparison.Ordinal) ||
            assignment.FlowTemplateVersionNo != instance.FlowTemplateVersionNo ||
            assignment.FlowExecutionEpoch != targetStep.ExecutionEpoch ||
            !string.Equals(assignment.FlowStepId, targetStep.FlowStepId, StringComparison.Ordinal) ||
            !string.Equals(assignment.FlowBranchId, targetStep.BranchId, StringComparison.Ordinal) ||
            assignment.FlowAttemptNo != targetStep.AttemptNo ||
            !string.Equals(targetReport.DynamicFormTemplateId, targetStep.FormVersionId, StringComparison.Ordinal) ||
            !string.Equals(
                targetReport.DynamicFormFamilyId,
                targetStep.FormFamilyId,
                StringComparison.Ordinal) ||
            targetReport.DynamicFormVersionNo != targetStep.FormVersionNo ||
            !string.Equals(
                targetReport.DynamicFormSchemaHash,
                targetStep.FormSchemaHash,
                StringComparison.Ordinal) ||
            targetStep.ReportLifecycleRevision != targetReport.LifecycleRevision ||
            !string.Equals(
                targetStep.ReportLifecycleStatus,
                targetReport.Status.ToString().ToUpperInvariant(),
                StringComparison.Ordinal) ||
            targetStep.ReportLifecycleIsActive != targetReport.IsActive)
        {
            throw DynamicFlowMappingIdentityConflict(
                targetReport,
                assignment,
                "runtimeIdentity",
                "DYNAMIC_FLOW_MAPPING_TARGET_RUNTIME_PIN_CONFLICT");
        }

        IReadOnlyList<DynamicFlowMappingRuleDto> rules;
        try
        {
            rules = DynamicFlowMappingEngine.ReadRulesFromPayloadJson(
                version.PayloadJson);
        }
        catch (AppException error) when (
            IsDynamicFlowMappingPolicySnapshotFailure(error))
        {
            throw DynamicFlowMappingPolicyDenied(
                targetReport,
                assignment,
                "DYNAMIC_FLOW_MAPPING_TARGET_POLICY_LOAD_FAILED");
        }
        try
        {
            DynamicFlowMappingEngine.ValidateP7Rules(rules);
        }
        catch (DynamicFlowMappingEvaluationException error)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
                new
                {
                    reportId = targetReport.Id,
                    workAssignmentId = assignment.Id,
                    reason = error.Reason
                });
        }

        var runtime = new DynamicFlowMappingRuntimeContext(
            version,
            instance,
            targetStep,
            rules,
            DynamicFlowMappingRuntimeContract.ComputeRuleSetHash(rules));
        try
        {
            DynamicFlowMappingRuntimeContract.ValidateCallerAssertions(req, runtime, targetReport);
        }
        catch (DynamicFlowMappingContractException error)
        {
            throw DynamicFlowMappingIdentityConflict(
                targetReport,
                assignment,
                error.Field ?? "runtimeIdentity",
                error.Reason);
        }

        return runtime;
    }

    private static bool IsDynamicFlowMappingPolicySnapshotFailure(
        AppException error)
    {
        for (Exception? current = error;
             current is not null;
             current = current.InnerException)
        {
            if (current is not AppException appError)
                continue;

            if (appError.Code is
                AppErrorCode.DYNAMIC_FLOW_ACTOR_POLICY_COVERAGE_INCOMPLETE or
                AppErrorCode.DYNAMIC_FLOW_FIELD_POLICY_COVERAGE_INCOMPLETE or
                AppErrorCode.DYNAMIC_FLOW_TABLE_POLICY_COVERAGE_INCOMPLETE or
                AppErrorCode.DYNAMIC_FLOW_TABLE_POLICY_ENDPOINT_INCOMPATIBLE or
                AppErrorCode.DYNAMIC_FLOW_POLICY_SPECIFICITY_CONFLICT or
                AppErrorCode.DYNAMIC_FLOW_POLICY_FIELD_UNKNOWN or
                AppErrorCode.DYNAMIC_FLOW_ACTOR_ROLE_UNKNOWN)
            {
                return true;
            }

            if (appError.Code == AppErrorCode.COMMON_VALIDATION_FAILED &&
                IsDynamicFlowMappingPolicyFailureDetails(
                    appError.Details))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDynamicFlowMappingPolicyFailureDetails(
        object? details)
    {
        if (details is null)
            return false;

        try
        {
            var json = JsonSerializer.Serialize(details);
            return new[]
                {
                    "DYNAMIC_FLOW_ACTOR_POLICY_COVERAGE_INCOMPLETE",
                    "DYNAMIC_FLOW_FIELD_POLICY_COVERAGE_INCOMPLETE",
                    "DYNAMIC_FLOW_TABLE_POLICY_COVERAGE_INCOMPLETE",
                    "DYNAMIC_FLOW_TABLE_POLICY_ENDPOINT_INCOMPATIBLE",
                    "DYNAMIC_FLOW_POLICY_SPECIFICITY_CONFLICT",
                    "DYNAMIC_FLOW_POLICY_SCOPE_DUPLICATE",
                    "DYNAMIC_FLOW_POLICY_FIELD_UNKNOWN",
                    "DYNAMIC_FLOW_ACTOR_ROLE_UNKNOWN"
                }
                .Any(reason => json.Contains(
                    reason,
                    StringComparison.Ordinal));
        }
        catch (Exception error) when (
            error is NotSupportedException or
            InvalidOperationException)
        {
            return false;
        }
    }

    private static AppException DynamicFlowMappingIdentityConflict(
        WorkAssignmentReport report,
        WorkAssignment assignment,
        string field,
        string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_MAPPING_IDENTITY_CONFLICT,
            new
            {
                reportId = report.Id,
                workAssignmentId = assignment.Id,
                field,
                reason
            });

    private static AppException
        DynamicFlowMappingTargetAccessForbidden()
        => RawReportReadAccessForbidden();

    internal static AppException DynamicFlowMappingBlockingConflict()
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
            new
            {
                reason = "DYNAMIC_FLOW_MAPPING_CONFLICT",
                hasBlockingConflicts = true
            });

    private static void EnsureDynamicFlowMappingRequestDoesNotOverrideConfig(DynamicFlowMappingRequest req)
    {
        try
        {
            DynamicFlowMappingSecurityContract
                .ValidateFlowOwnedRequestInputs(req);
        }
        catch (DynamicFlowMappingSecurityException error) when (
            string.Equals(
                error.Reason,
                DynamicFlowMappingSecurityContract
                    .FlowOwnedConfigurationReason,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = error.Field,
                    reason = error.Reason
                });
        }
    }

    private async Task<DynamicFlowTemplateVersion?> ResolveDynamicFlowMappingTemplateVersionAsync(
        WorkAssignment assignment,
        CancellationToken ct)
    {
        var templateId = NormalizeOptionalTextOrNull(assignment.FlowTemplateId);
        var versionNo = assignment.FlowTemplateVersionNo;
        if (string.IsNullOrWhiteSpace(templateId) || !versionNo.HasValue)
            return null;

        var version = await _ctx.DynamicFlowTemplateVersions
            .Find(x => x.TemplateId == templateId &&
                       x.VersionNo == versionNo.Value &&
                       x.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                       !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (version is null)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    flowTemplateId = templateId,
                    flowTemplateVersionNo = versionNo,
                    reason = "DYNAMIC_FLOW_TEMPLATE_VERSION_LOCKED_REQUIRED"
                });
        }

        return version;
    }

    private static void ValidateDynamicFlowMappingRules(IReadOnlyCollection<DynamicFlowMappingRuleDto> rules)
    {
        if (rules.Count == 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "mappingRules", reason = "DYNAMIC_FLOW_MAPPING_RULES_REQUIRED" });
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.MappingId))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = "mappingRules.mappingId", reason = "DYNAMIC_FLOW_MAPPING_ID_REQUIRED" });
            }

            if (!ids.Add(rule.MappingId.Trim()))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = "mappingRules.mappingId", mappingId = rule.MappingId, reason = "DYNAMIC_FLOW_MAPPING_ID_DUPLICATE" });
            }
        }
    }

    private async Task<List<DynamicFlowMappingSourceReport>>
        ResolveCanonicalDynamicFlowMappingSourceReportsAsync(
            WorkAssignmentReport targetReport,
            WorkAssignment targetAssignment,
            DynamicFlowMappingRuntimeContext runtime,
            string actorUserId,
            CancellationToken ct)
    {
        var sourceStepIds = runtime.Rules
            .SelectMany(rule =>
                rule.Inputs is { Count: > 0 }
                    ? rule.Inputs
                        .Where(input => !string.Equals(
                            input.Source.Kind,
                            "CONSTANT",
                            StringComparison.OrdinalIgnoreCase))
                        .Select(input => NormalizeOptionalTextOrNull(input.Source.StepId))
                    : new[] { NormalizeOptionalTextOrNull(rule.SourceStepId) })
            .Where(stepId => !string.IsNullOrWhiteSpace(stepId))
            .Select(stepId => stepId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (sourceStepIds.Count == 0)
            return new List<DynamicFlowMappingSourceReport>();
        if (sourceStepIds.Contains(runtime.TargetStep.FlowStepId, StringComparer.Ordinal))
        {
            throw DynamicFlowMappingIdentityConflict(
                targetReport,
                targetAssignment,
                "source.stepId",
                "DYNAMIC_FLOW_MAPPING_SOURCE_TARGET_CYCLE_FORBIDDEN");
        }

        var canonical = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            runtime.FlowVersion.PayloadJson,
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true,
                AllowHistoricalCatalogPins: true));
        foreach (var sourceStepId in sourceStepIds)
        {
            if (!IsDynamicFlowMappingTopologyAncestor(
                    canonical.Payload,
                    sourceStepId,
                    runtime.TargetStep.FlowStepId))
            {
                throw DynamicFlowMappingIdentityConflict(
                    targetReport,
                    targetAssignment,
                    "source.stepId",
                    "DYNAMIC_FLOW_MAPPING_SOURCE_TOPOLOGY_MISMATCH");
            }
        }

        var sourceSteps = await _ctx.DynamicFlowStepInstances
            .Find(step =>
                step.FlowInstanceId == runtime.FlowInstance.Id &&
                step.ExecutionEpoch == runtime.TargetStep.ExecutionEpoch &&
                sourceStepIds.Contains(step.FlowStepId))
            .ToListAsync(ct);
        if (sourceSteps.Count == 0)
            return new List<DynamicFlowMappingSourceReport>();

        var reportIds = sourceSteps
            .Select(step => NormalizeOptionalTextOrNull(step.ReportId))
            .Where(reportId => !string.IsNullOrWhiteSpace(reportId))
            .Select(reportId => reportId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var reports = await _ctx.WorkAssignmentReports
            .Find(report => reportIds.Contains(report.Id) && !report.IsDeleted)
            .ToListAsync(ct);
        var reportById = reports.ToDictionary(report => report.Id, StringComparer.Ordinal);

        var assignmentIds = sourceSteps
            .Select(step => NormalizeOptionalTextOrNull(step.AssignmentId))
            .Where(assignmentId => !string.IsNullOrWhiteSpace(assignmentId))
            .Select(assignmentId => assignmentId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var assignments = await _ctx.WorkAssignments
            .Find(assignment => assignmentIds.Contains(assignment.Id) && !assignment.IsDeleted)
            .ToListAsync(ct);
        var assignmentById = assignments.ToDictionary(
            assignment => assignment.Id,
            StringComparer.Ordinal);

        var actorRole = ResolveDirectDynamicFlowActorRole(
            targetAssignment,
            targetReport,
            actorUserId);
        if (actorRole is null)
        {
            if (await HasReviewReportReadAccessAsync(targetReport, actorUserId, ct))
            {
                actorRole = "REVIEWER";
            }
            else if (await HasAggregateAncestorReadAccessAsync(
                         targetAssignment,
                         actorUserId,
                         ct))
            {
                actorRole = "FINALIZER";
            }
            else
            {
                throw AppExceptionFactory.Forbidden(
                    AppErrorCode.DYNAMIC_FLOW_MAPPING_SOURCE_FORBIDDEN,
                    new
                    {
                        reportId = targetReport.Id,
                        workAssignmentId = targetAssignment.Id,
                        reason = "DYNAMIC_FLOW_MAPPING_SERVER_ROLE_UNRESOLVED"
                    });
            }
        }
        var result = new List<DynamicFlowMappingSourceReport>();
        foreach (var step in sourceSteps
                     .OrderBy(item => item.StepOrder)
                     .ThenBy(item => item.BranchId, StringComparer.Ordinal)
                     .ThenBy(item => item.AttemptNo)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            if (step.IsCanonicalEpoch == false ||
                !string.IsNullOrWhiteSpace(step.InvalidatedByFlowEventId) ||
                step.InvalidatedAtUtc.HasValue ||
                !string.IsNullOrWhiteSpace(step.SupersededByStepInstanceId) ||
                step.State is not DynamicFlowStepStates.Approved and
                    not DynamicFlowStepStates.Completed ||
                string.IsNullOrWhiteSpace(step.ReportId) ||
                string.IsNullOrWhiteSpace(step.AssignmentId) ||
                !reportById.TryGetValue(step.ReportId, out var report) ||
                !assignmentById.TryGetValue(step.AssignmentId, out var assignment))
            {
                throw DynamicFlowMappingIdentityConflict(
                    targetReport,
                    targetAssignment,
                    "source.stepInstanceId",
                    "DYNAMIC_FLOW_MAPPING_SOURCE_RUNTIME_INVALID");
            }

            if (!report.IsActive ||
                !report.IsCurrent ||
                report.Status != WorkAssignmentReportStatus.Approved ||
                !assignment.IsActive ||
                !string.Equals(
                    assignment.FlowEffectiveStatus,
                    DynamicFlowEffectiveStatuses.Effective,
                    StringComparison.Ordinal) ||
                !string.Equals(assignment.WorkId, targetAssignment.WorkId, StringComparison.Ordinal) ||
                !string.Equals(assignment.FlowInstanceId, runtime.FlowInstance.Id, StringComparison.Ordinal) ||
                assignment.FlowExecutionEpoch != runtime.TargetStep.ExecutionEpoch ||
                !string.Equals(assignment.FlowStepId, step.FlowStepId, StringComparison.Ordinal) ||
                !string.Equals(assignment.FlowBranchId, step.BranchId, StringComparison.Ordinal) ||
                assignment.FlowAttemptNo != step.AttemptNo ||
                !string.Equals(report.WorkAssignmentId, assignment.Id, StringComparison.Ordinal) ||
                !string.Equals(report.DynamicFormTemplateId, step.FormVersionId, StringComparison.Ordinal) ||
                !string.Equals(report.DynamicFormFamilyId, step.FormFamilyId, StringComparison.Ordinal) ||
                report.DynamicFormVersionNo != step.FormVersionNo ||
                !string.Equals(report.DynamicFormSchemaHash, step.FormSchemaHash, StringComparison.Ordinal) ||
                step.ReportLifecycleRevision != report.LifecycleRevision ||
                !string.Equals(
                    step.ReportLifecycleStatus,
                    report.Status.ToString().ToUpperInvariant(),
                    StringComparison.Ordinal) ||
                step.ReportLifecycleIsActive != report.IsActive ||
                report.PayloadRevision < 0 ||
                !IsLowerSha256(report.PayloadHash))
            {
                throw DynamicFlowMappingIdentityConflict(
                    targetReport,
                    targetAssignment,
                    "source.runtimeIdentity",
                    "DYNAMIC_FLOW_MAPPING_SOURCE_PIN_CONFLICT");
            }

            try
            {
                DynamicFlowMappingCanonicalSourceContract.Validate(
                    runtime.FlowInstance,
                    runtime.TargetStep,
                    targetAssignment,
                    step,
                    assignment,
                    report,
                    IsDynamicFlowMappingTopologyAncestor(
                        canonical.Payload,
                        step.FlowStepId,
                        runtime.TargetStep.FlowStepId));
            }
            catch (DynamicFlowMappingSourceContractException error)
            {
                throw DynamicFlowMappingIdentityConflict(
                    targetReport,
                    targetAssignment,
                    error.Field,
                    error.Reason);
            }

            DynamicFlowPolicyEvaluationResult sourcePermissions;
            try
            {
                sourcePermissions = _dynamicFlowPolicyEvaluator.Evaluate(
                    runtime.FlowVersion.PayloadJson,
                    new DynamicFlowPolicyEvaluationContext
                    {
                        StepId = step.FlowStepId,
                        StepCode = step.FlowStepCode,
                        ActorRole = actorRole,
                        IsAfterSubmit = true
                    });
            }
            catch (Exception error) when (
                error is AppException or
                InvalidOperationException or
                JsonException)
            {
                throw DynamicFlowMappingPolicyDenied(
                    targetReport,
                    targetAssignment,
                    "DYNAMIC_FLOW_MAPPING_SOURCE_POLICY_LOAD_FAILED");
            }

            EnsureDynamicFlowMappingSourceRulesReadable(
                targetReport,
                targetAssignment,
                runtime.Rules,
                step.FlowStepId,
                sourcePermissions);
            await HydrateReportPayloadAsync(report, ct);
            result.Add(new DynamicFlowMappingSourceReport(
                report,
                step.FlowStepId,
                step.FlowStepCode,
                report.FieldValuesJson,
                report.TableValuesJson,
                runtime.FlowInstance,
                step,
                assignment));
        }

        return result;
    }

    private static bool RuleRequiresDynamicFlowMappingSource(
        DynamicFlowMappingRuleDto rule)
        => rule.Inputs is { Count: > 0 }
            ? rule.Inputs.Any(input => !string.Equals(
                input.Source.Kind,
                "CONSTANT",
                StringComparison.OrdinalIgnoreCase))
            : !string.IsNullOrWhiteSpace(rule.SourceStepId);

    private static bool IsDynamicFlowMappingTopologyAncestor(
        DynamicFlowTemplatePayloadDto payload,
        string sourceStepId,
        string targetStepId)
    {
        var nextByNode = payload.Edges
            .GroupBy(edge => edge.FromNodeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(edge => edge.ToNodeId)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);
        var pending = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal)
        {
            sourceStepId
        };
        pending.Enqueue(sourceStepId);
        while (pending.TryDequeue(out var current))
        {
            if (!nextByNode.TryGetValue(current, out var nextNodes))
                continue;
            foreach (var next in nextNodes)
            {
                if (string.Equals(next, targetStepId, StringComparison.Ordinal))
                    return true;
                if (seen.Add(next))
                    pending.Enqueue(next);
            }
        }

        return false;
    }

    private static bool IsLowerSha256(string? value)
        => value is { Length: 64 } &&
           value.All(character =>
               character is >= '0' and <= '9' ||
               character is >= 'a' and <= 'f');

    private static void EnsureDynamicFlowMappingTargetRulesWritable(
        WorkAssignmentReport report,
        WorkAssignment assignment,
        IReadOnlyCollection<DynamicFlowMappingRuleDto> rules,
        DynamicFlowPolicyEvaluationResult permissions)
    {
        foreach (var rule in rules)
        {
            var targetKind = !string.IsNullOrWhiteSpace(rule.Target?.FieldId) ||
                             !string.IsNullOrWhiteSpace(rule.Target?.FieldKey) ||
                             !string.IsNullOrWhiteSpace(rule.TargetFieldId) ||
                             !string.IsNullOrWhiteSpace(rule.TargetFieldKey)
                ? "FIELD"
                : "TABLE_COLUMN";
            if (targetKind == "FIELD")
            {
                var fieldId = NormalizeOptionalTextOrNull(rule.Target?.FieldId) ??
                              NormalizeOptionalTextOrNull(rule.TargetFieldId);
                var fieldKey = NormalizeOptionalTextOrNull(rule.Target?.FieldKey) ??
                               NormalizeOptionalTextOrNull(rule.TargetFieldKey);
                var allowed = !permissions.DenyAllFields &&
                              permissions.Fields.Values.Any(permission =>
                                  DynamicFlowMappingFieldPermissionMatches(
                                      permission,
                                      fieldId,
                                      fieldKey) &&
                                  permission.Write &&
                                  !permission.Hidden &&
                                  !permission.Locked);
                if (!allowed)
                {
                    throw DynamicFlowMappingPolicyDenied(
                        report,
                        assignment,
                        "DYNAMIC_FLOW_MAPPING_TARGET_FIELD_WRITE_FORBIDDEN");
                }

                continue;
            }

            var blockId = NormalizeOptionalTextOrNull(rule.Target?.BlockId) ??
                          NormalizeOptionalTextOrNull(rule.TargetBlockId);
            var columnKey = NormalizeOptionalTextOrNull(rule.Target?.ColumnKey) ??
                            NormalizeOptionalTextOrNull(rule.TargetColumnKey);
            var tableAllowed = !permissions.DenyAllTableColumns &&
                               permissions.TableColumns.Values.Any(permission =>
                                   string.Equals(
                                       permission.BlockId,
                                       blockId,
                                       StringComparison.Ordinal) &&
                                   string.Equals(
                                       permission.ColumnKey,
                                       columnKey,
                                       StringComparison.OrdinalIgnoreCase) &&
                                   permission.Write &&
                                   !permission.Hidden &&
                                   !permission.Locked);
            if (!tableAllowed)
            {
                throw DynamicFlowMappingPolicyDenied(
                    report,
                    assignment,
                    "DYNAMIC_FLOW_MAPPING_TARGET_TABLE_WRITE_FORBIDDEN");
            }
        }
    }

    private static void EnsureDynamicFlowMappingSourceRulesReadable(
        WorkAssignmentReport targetReport,
        WorkAssignment targetAssignment,
        IReadOnlyCollection<DynamicFlowMappingRuleDto> rules,
        string sourceStepId,
        DynamicFlowPolicyEvaluationResult permissions)
    {
        foreach (var rule in rules)
        {
            var endpoints = rule.Inputs is { Count: > 0 }
                ? rule.Inputs
                    .Where(input =>
                        !string.Equals(
                            input.Source.Kind,
                            "CONSTANT",
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            input.Source.StepId,
                            sourceStepId,
                            StringComparison.Ordinal))
                    .Select(input => input.Source)
                : string.Equals(rule.SourceStepId, sourceStepId, StringComparison.Ordinal)
                    ? new[]
                    {
                        new DynamicFlowMappingEndpointDto
                        {
                            Kind = !string.IsNullOrWhiteSpace(rule.SourceFieldId) ||
                                   !string.IsNullOrWhiteSpace(rule.SourceFieldKey)
                                ? "FIELD"
                                : "TABLE_COLUMN",
                            FieldId = rule.SourceFieldId,
                            FieldKey = rule.SourceFieldKey,
                            BlockId = rule.SourceBlockId,
                            ColumnKey = rule.SourceColumnKey
                        }
                    }
                    : Array.Empty<DynamicFlowMappingEndpointDto>();

            foreach (var endpoint in endpoints)
            {
                if (string.Equals(endpoint.Kind, "FIELD", StringComparison.OrdinalIgnoreCase))
                {
                    var allowed = !permissions.DenyAllFields &&
                                  permissions.Fields.Values.Any(permission =>
                                      DynamicFlowMappingFieldPermissionMatches(
                                          permission,
                                          endpoint.FieldId,
                                          endpoint.FieldKey) &&
                                      permission.Read &&
                                      !permission.Hidden);
                    if (!allowed)
                    {
                        throw DynamicFlowMappingPolicyDenied(
                            targetReport,
                            targetAssignment,
                            "DYNAMIC_FLOW_MAPPING_SOURCE_FIELD_READ_FORBIDDEN");
                    }
                }
                else
                {
                    var allowed = !permissions.DenyAllTableColumns &&
                                  permissions.TableColumns.Values.Any(permission =>
                                      string.Equals(
                                          permission.BlockId,
                                          endpoint.BlockId,
                                          StringComparison.Ordinal) &&
                                      string.Equals(
                                          permission.ColumnKey,
                                          endpoint.ColumnKey,
                                          StringComparison.OrdinalIgnoreCase) &&
                                      permission.Read &&
                                      !permission.Hidden);
                    if (!allowed)
                    {
                        throw DynamicFlowMappingPolicyDenied(
                            targetReport,
                            targetAssignment,
                            "DYNAMIC_FLOW_MAPPING_SOURCE_TABLE_READ_FORBIDDEN");
                    }
                }
            }
        }
    }

    private static bool DynamicFlowMappingFieldPermissionMatches(
        DynamicFlowFieldPermissionDto permission,
        string? fieldId,
        string? fieldKey)
        => (!string.IsNullOrWhiteSpace(fieldId) &&
            (string.Equals(permission.FieldId, fieldId, StringComparison.Ordinal) ||
             string.Equals(permission.TargetKey, fieldId, StringComparison.Ordinal))) ||
           (!string.IsNullOrWhiteSpace(fieldKey) &&
            (string.Equals(
                 permission.FieldKey,
                 fieldKey,
                 StringComparison.OrdinalIgnoreCase) ||
             string.Equals(
                 permission.TargetKey,
                 fieldKey,
                 StringComparison.OrdinalIgnoreCase)));

    private static AppException DynamicFlowMappingPolicyDenied(
        WorkAssignmentReport report,
        WorkAssignment assignment,
        string reason,
        object? violations = null)
    {
        var code = reason switch
        {
            "DYNAMIC_FLOW_MAPPING_POLICY_NOT_LOADED" or
            "DYNAMIC_FLOW_MAPPING_TARGET_POLICY_LOAD_FAILED" or
            "DYNAMIC_FLOW_MAPPING_SOURCE_POLICY_LOAD_FAILED" =>
                AppErrorCode.DYNAMIC_FLOW_MAPPING_POLICY_UNAVAILABLE,
            "DYNAMIC_FLOW_MAPPING_TARGET_FIELD_WRITE_FORBIDDEN" =>
                AppErrorCode.DYNAMIC_FLOW_MAPPING_FIELD_WRITE_FORBIDDEN,
            "DYNAMIC_FLOW_MAPPING_TARGET_TABLE_WRITE_FORBIDDEN" =>
                AppErrorCode.DYNAMIC_FLOW_MAPPING_TABLE_COLUMN_WRITE_FORBIDDEN,
            "DYNAMIC_FLOW_MAPPING_SOURCE_FIELD_READ_FORBIDDEN" or
            "DYNAMIC_FLOW_MAPPING_SOURCE_TABLE_READ_FORBIDDEN" =>
                AppErrorCode.DYNAMIC_FLOW_MAPPING_SOURCE_FORBIDDEN,
            _ => AppErrorCode.DYNAMIC_FLOW_MAPPING_POLICY_DENIED
        };
        return AppExceptionFactory.Create(
            code,
            new
            {
                reportId = report.Id,
                workAssignmentId = assignment.Id,
                reason,
                violations
            });
    }

    private async Task<List<DynamicFlowMappingSourceReport>> ResolveDynamicFlowMappingSourceReportsAsync(
        WorkAssignmentReport targetReport,
        WorkAssignment targetAssignment,
        DynamicFlowMappingRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        var explicitIds = (req.SourceReportIds ?? new List<string>())
            .Select(x => NormalizeOptionalTextOrNull(x))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (explicitIds.Count > 0)
            return await LoadDynamicFlowMappingSourceReportsByIdsAsync(
                explicitIds,
                targetReport,
                targetAssignment,
                actorUserId,
                ct);

        var sourceMode = NormalizeDynamicFlowMappingSourceMode(req.SourceMode, targetAssignment);
        return sourceMode switch
        {
            "PREVIOUS_PERIOD" => await LoadPreviousPeriodDynamicFlowMappingSourceReportAsync(targetReport, actorUserId, ct),
            _ => await LoadChildFlowDynamicFlowMappingSourceReportsAsync(targetReport, targetAssignment, actorUserId, ct)
        };
    }

    private async Task<List<DynamicFlowMappingSourceReport>> LoadDynamicFlowMappingSourceReportsByIdsAsync(
        IReadOnlyCollection<string> sourceReportIds,
        WorkAssignmentReport targetReport,
        WorkAssignment targetAssignment,
        string actorUserId,
        CancellationToken ct)
    {
        var ids = sourceReportIds
            .Select(x => NormalizeObjectIdOrValidation(x, "sourceReportIds"))
            .ToList();
        var reports = await _ctx.WorkAssignmentReports
            .Find(x => ids.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(ct);
        var byId = reports.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var sourceAssignmentIds = reports.Select(report => report.WorkAssignmentId).Distinct(StringComparer.Ordinal).ToList();
        var sourceAssignments = await _ctx.WorkAssignments
            .Find(assignment => sourceAssignmentIds.Contains(assignment.Id) && !assignment.IsDeleted)
            .ToListAsync(ct);
        var assignmentById = sourceAssignments.ToDictionary(assignment => assignment.Id, StringComparer.Ordinal);
        var result = new List<DynamicFlowMappingSourceReport>();

        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var report))
                throw ReportNotFound(id);
            EnsureReportIsActive(report);
            if (!report.IsCurrent ||
                report.Status != WorkAssignmentReportStatus.Approved ||
                !string.Equals(report.WorkId, targetReport.WorkId, StringComparison.Ordinal) ||
                !assignmentById.TryGetValue(report.WorkAssignmentId, out var sourceAssignment) ||
                !DynamicFlowSourceContextMatches(targetAssignment, sourceAssignment))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
                    new
                    {
                        sourceReportId = report.Id,
                        targetReportId = targetReport.Id,
                        reason = "DYNAMIC_FLOW_MAPPING_SOURCE_CONTEXT_MISMATCH"
                    });
            }

            result.Add(await ToDynamicFlowMappingSourceReportAsync(report, sourceAssignment, ct));
        }

        return result;
    }

    private async Task<List<DynamicFlowMappingSourceReport>> LoadChildFlowDynamicFlowMappingSourceReportsAsync(
        WorkAssignmentReport targetReport,
        WorkAssignment targetAssignment,
        string actorUserId,
        CancellationToken ct)
    {
        var childFilter = Builders<WorkAssignment>.Filter.Eq(x => x.ParentAssignmentId, targetAssignment.Id)
                          & Builders<WorkAssignment>.Filter.Eq(x => x.WorkId, targetAssignment.WorkId)
                          & Builders<WorkAssignment>.Filter.Eq(x => x.IsActive, true)
                          & Builders<WorkAssignment>.Filter.Eq(x => x.FlowEffectiveStatus, DynamicFlowEffectiveStatuses.Effective)
                          & Builders<WorkAssignment>.Filter.Eq(x => x.IsDeleted, false);
        if (!string.IsNullOrWhiteSpace(targetAssignment.FlowInstanceId))
            childFilter &= Builders<WorkAssignment>.Filter.Eq(x => x.FlowInstanceId, targetAssignment.FlowInstanceId);
        if (!string.IsNullOrWhiteSpace(targetAssignment.FlowTemplateId))
            childFilter &= Builders<WorkAssignment>.Filter.Eq(x => x.FlowTemplateId, targetAssignment.FlowTemplateId);
        if (targetAssignment.FlowTemplateVersionNo.HasValue)
            childFilter &= Builders<WorkAssignment>.Filter.Eq(x => x.FlowTemplateVersionNo, targetAssignment.FlowTemplateVersionNo);

        var childAssignments = await _ctx.WorkAssignments
            .Find(childFilter)
            .ToListAsync(ct);
        var relatedAssignments = new List<WorkAssignment>(childAssignments);
        if (!string.IsNullOrWhiteSpace(targetAssignment.ParentAssignmentId))
        {
            var parentAssignment = await _ctx.WorkAssignments
                .Find(x => x.Id == targetAssignment.ParentAssignmentId &&
                           x.WorkId == targetAssignment.WorkId &&
                           x.IsActive &&
                           x.FlowEffectiveStatus == DynamicFlowEffectiveStatuses.Effective &&
                           !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (parentAssignment is not null && DynamicFlowSourceContextMatches(targetAssignment, parentAssignment))
                relatedAssignments.Add(parentAssignment);
        }

        relatedAssignments = relatedAssignments
            .GroupBy(assignment => assignment.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (relatedAssignments.Count == 0)
            return new List<DynamicFlowMappingSourceReport>();

        var relatedIds = relatedAssignments.Select(x => x.Id).ToList();
        var reports = await _ctx.WorkAssignmentReports
            .Find(x => relatedIds.Contains(x.WorkAssignmentId) &&
                       x.WorkId == targetReport.WorkId &&
                       x.PeriodInstanceKey == targetReport.PeriodInstanceKey &&
                       x.IsCurrent &&
                       x.IsActive &&
                       x.Status == WorkAssignmentReportStatus.Approved &&
                       !x.IsDeleted)
            .ToListAsync(ct);
        var assignmentById = relatedAssignments.ToDictionary(x => x.Id, StringComparer.Ordinal);

        var result = new List<DynamicFlowMappingSourceReport>();
        foreach (var report in reports.OrderBy(x => assignmentById.TryGetValue(x.WorkAssignmentId, out var a) ? a.FlowStepOrder ?? 0 : 0)
                                      .ThenBy(x => x.WorkAssignmentId, StringComparer.Ordinal))
        {
            result.Add(await ToDynamicFlowMappingSourceReportAsync(
                report,
                assignmentById.GetValueOrDefault(report.WorkAssignmentId),
                ct));
        }

        return result;
    }

    internal static bool DynamicFlowSourceContextMatches(
        WorkAssignment targetAssignment,
        WorkAssignment sourceAssignment)
    {
        if (!sourceAssignment.IsActive ||
            !string.Equals(targetAssignment.WorkId, sourceAssignment.WorkId, StringComparison.Ordinal))
            return false;

        if (!DynamicFlowBranchVisibility.IsFlowAssignment(targetAssignment) ||
            !DynamicFlowBranchVisibility.IsFlowAssignment(sourceAssignment) ||
            string.IsNullOrWhiteSpace(targetAssignment.FlowTemplateId) ||
            !targetAssignment.FlowTemplateVersionNo.HasValue)
        {
            return false;
        }

        var isSameAssignment = string.Equals(targetAssignment.Id, sourceAssignment.Id, StringComparison.Ordinal);
        var isDirectParent = string.Equals(targetAssignment.ParentAssignmentId, sourceAssignment.Id, StringComparison.Ordinal);
        var isDirectChild = string.Equals(sourceAssignment.ParentAssignmentId, targetAssignment.Id, StringComparison.Ordinal);
        if (!isSameAssignment && !isDirectParent && !isDirectChild)
            return false;

        if (DynamicFlowBranchVisibility.IsFlowAssignment(sourceAssignment) &&
            !string.Equals(
                sourceAssignment.FlowEffectiveStatus,
                DynamicFlowEffectiveStatuses.Effective,
                StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(targetAssignment.FlowInstanceId, sourceAssignment.FlowInstanceId, StringComparison.Ordinal) &&
               string.Equals(targetAssignment.FlowTemplateId, sourceAssignment.FlowTemplateId, StringComparison.Ordinal) &&
               targetAssignment.FlowTemplateVersionNo == sourceAssignment.FlowTemplateVersionNo;
    }

    private async Task<List<DynamicFlowMappingSourceReport>> LoadPreviousPeriodDynamicFlowMappingSourceReportAsync(
        WorkAssignmentReport targetReport,
        string actorUserId,
        CancellationToken ct)
    {
        var candidates = await _ctx.WorkAssignmentReports
            .Find(x => x.WorkAssignmentId == targetReport.WorkAssignmentId &&
                       x.Id != targetReport.Id &&
                       x.IsCurrent &&
                       x.IsActive &&
                       x.Status == WorkAssignmentReportStatus.Approved &&
                       !x.IsDeleted)
            .ToListAsync(ct);

        var previous = candidates
            .Where(x => IsBeforeTargetPeriod(x, targetReport))
            .OrderByDescending(x => x.PeriodStart ?? x.ReportDate ?? x.StartedDate ?? DateTime.MinValue)
            .ThenByDescending(x => x.PeriodInstanceKey, StringComparer.Ordinal)
            .FirstOrDefault();
        if (previous is null)
            return new List<DynamicFlowMappingSourceReport>();

        return new List<DynamicFlowMappingSourceReport>
        {
            await ToDynamicFlowMappingSourceReportAsync(previous, sourceAssignment: null, ct: ct)
        };
    }

    private async Task<DynamicFlowMappingSourceReport> ToDynamicFlowMappingSourceReportAsync(
        WorkAssignmentReport report,
        WorkAssignment? sourceAssignment,
        CancellationToken ct)
    {
        await HydrateReportPayloadAsync(report, ct);
        sourceAssignment ??= await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportAssignmentNotFound(report.WorkAssignmentId);

        return new DynamicFlowMappingSourceReport(
            report,
            sourceAssignment.FlowStepId,
            sourceAssignment.FlowStepCode,
            report.FieldValuesJson,
            report.TableValuesJson,
            Assignment: sourceAssignment);
    }

    private static bool IsBeforeTargetPeriod(WorkAssignmentReport candidate, WorkAssignmentReport target)
    {
        var candidateStart = candidate.PeriodStart ?? candidate.ReportDate ?? candidate.StartedDate;
        var targetStart = target.PeriodStart ?? target.ReportDate ?? target.StartedDate;
        if (candidateStart.HasValue && targetStart.HasValue)
            return candidateStart.Value < targetStart.Value;

        return string.Compare(
            NormalizeOptionalTextOrNull(candidate.PeriodInstanceKey),
            NormalizeOptionalTextOrNull(target.PeriodInstanceKey),
            StringComparison.Ordinal) < 0;
    }

    private static string NormalizeDynamicFlowMappingSourceMode(string? value, WorkAssignment targetAssignment)
    {
        var mode = value?.Trim().ToUpperInvariant();
        if (mode is "PREVIOUS_PERIOD" or "PREVIOUS" or "PREVIOUS_REPORT")
            return "PREVIOUS_PERIOD";
        if (mode is "CHILD_FLOW" or "CHILD" or "CHILD_RESULT")
            return "CHILD_FLOW";

        return DynamicFlowBranchVisibility.IsFlowAssignment(targetAssignment)
            ? "CHILD_FLOW"
            : "PREVIOUS_PERIOD";
    }

    private static string NormalizeObjectIdOrValidation(string? value, string field)
    {
        value = NormalizeOptionalTextOrNull(value);
        if (string.IsNullOrWhiteSpace(value) || !ObjectId.TryParse(value, out _))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field, value, reason = "OBJECT_ID_INVALID" });
        }

        return value;
    }

    private async Task<DynamicFormAggregateDraftProjection> BuildDynamicFormAggregateDraftProjectionAsync(
        WorkAssignmentReport entity,
        ApplyDynamicFormAggregateDraftRequest req,
        CancellationToken ct)
    {
        var aggregateReq = NormalizeAggregateDraftRequest(req.AggregateRequest);
        var targetDynamicFormTemplateId = NormalizeOptionalTextOrNull(entity.DynamicFormTemplateId)
            ?? throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_AGGREGATE_DRAFT_TEMPLATE_MISMATCH,
                ReportDetails(entity));

        var aggregate = await _aggregateTableService.GetDynamicFormAggregateAsync(aggregateReq, ct);

        var form = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == targetDynamicFormTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (form is null)
            throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_REPORT_STATISTICS_DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
                new { dynamicFormTemplateId = targetDynamicFormTemplateId, reportId = entity.Id });

        var sourceAndTargetShareTemplate = string.Equals(
            targetDynamicFormTemplateId,
            aggregateReq.DynamicFormTemplateId,
            StringComparison.Ordinal);
        var requestedTargetBlockId = NormalizeOptionalTextOrNull(req.TargetBlockId);
        if (!sourceAndTargetShareTemplate && string.IsNullOrWhiteSpace(requestedTargetBlockId))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_AGGREGATE_DRAFT_REQUEST_INVALID,
                new
                {
                    reportId = entity.Id,
                    sourceDynamicFormTemplateId = aggregateReq.DynamicFormTemplateId,
                    targetDynamicFormTemplateId,
                    reason = "TARGET_BLOCK_REQUIRED_WHEN_SOURCE_FORM_DIFFERS"
                });
        }

        var targetBlockId = NormalizeBlockId(requestedTargetBlockId ?? aggregateReq.BlockId ?? aggregate.Meta.BlockId);
        var block = ResolveAggregateDraftBlock(form, targetBlockId)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_AGGREGATE_DRAFT_BLOCK_NOT_FOUND,
                new { reportId = entity.Id, dynamicFormTemplateId = form.Id, blockId = targetBlockId });

        var dataOrigin = WorkReportDataOrigin.Normalize(req.DataOrigin ?? WorkReportDataOrigin.AutoSummary);
        var valueSelector = NormalizeAggregateDraftValueSelector(req.ValueSelector);
        var reportMapConfigJson = NormalizeReportMapConfigJson(req.ReportMapConfigJson, entity.Id);

        if (aggregate.StackedTable is not null &&
            string.Equals(block.TableMode, "APPEND_ROWS", StringComparison.Ordinal))
        {
            return BuildStackedAggregateDraftProjection(
                entity,
                req,
                aggregateReq,
                aggregate,
                form,
                block,
                dataOrigin,
                valueSelector,
                targetBlockId,
                reportMapConfigJson);
        }

        var previousSummary = TryReadAggregateDraftSummary(entity.SummarySourceJson);
        var existingTopLevelValues = DeserializeValues1D(entity.Values1DJson);
        var isTopLevelBlock = string.Equals(targetBlockId, ResolveTopLevelBlockId(form), StringComparison.Ordinal);
        var clearExisting = req.ClearExistingValues ?? dataOrigin != WorkReportDataOrigin.PartialMapping;
        var currentBlockValues = isTopLevelBlock
            ? existingTopLevelValues
            : ExtractBlockDecimalValues(entity.TableValuesJson, targetBlockId);
        var targetValues = clearExisting
            ? CreateEmptyValues1D(block.ValueLength, 1)
            : NormalizeDecimalValues(currentBlockValues, block.ValueLength);

        if (!clearExisting && IsSameAggregateDraftTarget(previousSummary, targetDynamicFormTemplateId, targetBlockId))
            ClearAggregateDraftTargetIndexes(targetValues, previousSummary!.TargetIndexes);

        var draftAggregate = ResolveMetricDraftAggregate(aggregate, block, valueSelector);
        ApplyAggregateRowsToValues(targetValues, draftAggregate.Rows, block, valueSelector);

        var tableValuesJson = BuildAggregateDraftTableValuesJson(entity, form, block, targetValues, draftAggregate);
        var topLevelValues = isTopLevelBlock
            ? targetValues.Select(x => (object?)x).ToList()
            : NormalizeDecimalValues(existingTopLevelValues, ResolveReportRuntimeInputCells(entity).Count).Select(x => (object?)x).ToList();
        var contributionMode = string.IsNullOrWhiteSpace(req.CumulativeContributionMode)
            ? WorkReportDataOrigin.DefaultContributionMode(dataOrigin)
            : WorkReportCumulativeContributionMode.Normalize(req.CumulativeContributionMode);
        var contributionPolicyJson = NormalizeOptionalTextOrNull(req.CumulativeContributionPolicyJson)
            ?? BuildAggregateDraftContributionPolicyJson(dataOrigin, draftAggregate.Rows, block.BlockId);
        var summarySourceJson = BuildAggregateDraftSummarySourceJson(
            dataOrigin,
            aggregateReq,
            draftAggregate,
            block,
            valueSelector,
            targetBlockId,
            clearExisting,
            targetDynamicFormTemplateId,
            reportMapConfigJson);

        return new DynamicFormAggregateDraftProjection(
            topLevelValues,
            tableValuesJson,
            dataOrigin,
            contributionMode,
            contributionPolicyJson,
            summarySourceJson);
    }

    private DynamicFormAggregateDraftProjection BuildStackedAggregateDraftProjection(
        WorkAssignmentReport entity,
        ApplyDynamicFormAggregateDraftRequest req,
        DynamicFormAggregateRequest aggregateReq,
        DynamicFormAggregateResponse aggregate,
        DynamicFormTemplate form,
        AggregateDraftBlockContract block,
        string dataOrigin,
        string valueSelector,
        string targetBlockId,
        string? reportMapConfigJson)
    {
        if (!string.Equals(block.TableMode, "APPEND_ROWS", StringComparison.Ordinal))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FORM_TABLE_MODE_MISMATCH,
                new
                {
                    reportId = entity.Id,
                    targetBlockId,
                    expectedTableMode = "APPEND_ROWS",
                    actualTableMode = block.TableMode,
                    reason = "STACKED_AGGREGATE_TARGET_REQUIRES_APPEND_ROWS"
                });
        }

        var stacked = aggregate.StackedTable!;
        var columnCount = Math.Max(block.W, stacked.Columns.Count);
        var rowCount = Math.Max(1, stacked.Rows.Count);
        var effectiveBlock = block with
        {
            W = columnCount,
            H = rowCount,
            ValueLength = columnCount * rowCount,
            DataRect = BuildExpandedAggregateDraftDataRect(block.DataRect, columnCount, rowCount)
        };

        var values = BuildStackedAggregateValues(stacked, columnCount);
        var tableValuesJson = BuildStackedAggregateDraftTableValuesJson(entity, form, effectiveBlock, values, aggregate);
        var isTopLevelBlock = string.Equals(targetBlockId, ResolveTopLevelBlockId(form), StringComparison.Ordinal);
        var topLevelValues = isTopLevelBlock
            ? values
            : NormalizeDecimalValues(DeserializeValues1D(entity.Values1DJson), ResolveReportRuntimeInputCells(entity).Count)
                .Select(x => (object?)x)
                .ToList();

        var contributionMode = string.IsNullOrWhiteSpace(req.CumulativeContributionMode)
            ? WorkReportDataOrigin.DefaultContributionMode(dataOrigin)
            : WorkReportCumulativeContributionMode.Normalize(req.CumulativeContributionMode);
        var contributionPolicyJson = NormalizeOptionalTextOrNull(req.CumulativeContributionPolicyJson)
            ?? BuildStackedAggregateDraftContributionPolicyJson(dataOrigin, stacked, effectiveBlock.BlockId);
        var summarySourceJson = BuildStackedAggregateDraftSummarySourceJson(
            dataOrigin,
            aggregateReq,
            aggregate,
            valueSelector,
            targetBlockId,
            form.Id,
            reportMapConfigJson);

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
        // Tổng hợp cũ tạm khóa; giữ dữ liệu, không phát sinh refresh.
        if (tdtd_be.Services.WorkAssignments.LegacyAggregateRetirement.IsDisabled) return;
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
        var reportAccess = await EnsureReportAccessAsync(entity, actorUserId, ct);
        if (!reportAccess.isAssignee || entity.AssigneeUserId != actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_SUBMIT_FORBIDDEN,
                ReportDetails(entity, actorUserId));
        EnsureReportIsActive(entity);
        await HydrateReportPayloadAsync(entity, ct);
        await ValidateDynamicFlowMappingLifecycleMutationBoundaryAsync(
            entity,
            "SUBMIT",
            ct);

        var submitCommandHash = ComputeReportPayloadCommandHash("SUBMIT", req);
        var payloadCommand = ResolvePayloadMutationCommand(
            entity,
            req.ExpectedPayloadRevision,
            req.CommandId,
            "SUBMIT",
            submitCommandHash);
        var payloadReplay = IsCompletedPayloadCommandReplay(entity, payloadCommand);
        if (payloadReplay)
        {
            var replayLifecycleCommand =
                BuildLegacySubmitLifecycleReplayCommand(
                    entity,
                    req.ExpectedLifecycleRevision,
                    payloadCommand);
            if (WorkReportLifecycleCommandContract.IsCompletedReplay(
                    entity,
                    replayLifecycleCommand) ||
                IsCompletedLegacyAutoApprovalSubmitReplay(
                    entity,
                    replayLifecycleCommand))
            {
                await TryReconcileCommittedLifecycleProjectionAsync(entity.Id);
                return await MapPayloadCommandReplayResponseAsync(
                    entity.Id,
                    actorUserId,
                    CancellationToken.None);
            }

            throw WorkReportLifecycleCommandContract.Conflict(
                entity,
                replayLifecycleCommand);
        }

        var (lifecycleCommand, lifecycleResolution) = WorkReportLifecycleCommandContract.Resolve(
            entity,
            req.ExpectedLifecycleRevision,
            req.ExpectedPayloadRevision,
            req.CommandId,
            "SUBMIT",
            submitCommandHash,
            payloadRevisionDelta: 1);
        if (lifecycleResolution ==
            WorkReportLifecycleCommandResolution.CompletedReplay)
        {
            throw WorkReportLifecycleCommandContract.Conflict(entity, lifecycleCommand);
        }

        await using var lifecycleSeriesLease = await _lifecycleSeriesLock.AcquireAsync(
            entity.WorkAssignmentId,
            lifecycleCommand.Operation,
            ct);

        await EnsureReportMutationScopeOpenAsync(reportAccess.assignment, actorUserId, ct, entity.WorkReportPeriodId);

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
        RuntimeTableBlockContract? runtimeTopLevelBlock = null;
        if (req.Values1D is { Count: > 0 })
        {
            runtimeTopLevelBlock = await ResolveRuntimeTopLevelBlockShapeAsync(entity, req.TableValuesJson, req.Values1D.Count, ct);
            var expectedLength = runtimeTopLevelBlock is not null
                ? ResolveRuntimeInputCells(runtimeTopLevelBlock.Block, runtimeTopLevelBlock.DataRect, runtimeTopLevelBlock.W, runtimeTopLevelBlock.H).Count
                : ResolveReportRuntimeInputCells(entity).Count;
            if (req.Values1D.Count != expectedLength)
                throw InvalidReportValues(entity, expectedLength, req.Values1D.Count, actorUserId);

            requestedValues1DJson = Values1DCompression.Serialize(req.Values1D, _jsonOptions);
        }

        var hasFlowOwnedMappingMetadata = IsDynamicFlowMappingSummary(entity.SummarySourceJson);
        EnsureDynamicFlowMappingSummaryOverrideAllowed(
            entity.SummarySourceJson,
            req.SummarySourceJson,
            id,
            actorUserId);
        var nextDataOrigin = hasFlowOwnedMappingMetadata
            ? WorkReportDataOrigin.Normalize(entity.DataOrigin)
            : ResolveReportDataOrigin(req.DataOrigin, entity.DataOrigin);
        var nextContributionMode = hasFlowOwnedMappingMetadata
            ? WorkReportCumulativeContributionMode.Normalize(entity.CumulativeContributionMode)
            : ResolveCumulativeContributionMode(
                req.CumulativeContributionMode,
                req.DataOrigin,
                entity.CumulativeContributionMode,
                entity.DataOrigin);
        var nextContributionPolicyJson = hasFlowOwnedMappingMetadata
            ? NormalizeOptionalTextOrNull(entity.CumulativeContributionPolicyJson)
            : ResolveContributionPolicyJsonOverride(
                req.CumulativeContributionPolicyJson,
                entity.CumulativeContributionPolicyJson,
                id,
                actorUserId);
        var nextSummarySourceJson = hasFlowOwnedMappingMetadata
            ? NormalizeOptionalTextOrNull(entity.SummarySourceJson)
            : ResolveSummarySourceJsonOverride(
                req.SummarySourceJson,
                entity.SummarySourceJson,
                id,
                actorUserId);
        var nextAggregateSources = ExtractAggregateSourceSnapshot(nextSummarySourceJson);
        var acceptsReportDataPayload = ShouldAcceptReportDataPayload(entity, nextDataOrigin, nextSummarySourceJson);
        var isStackedAggregatePayload = IsStackedAggregateSummary(nextSummarySourceJson);
        var nextFieldValuesJson = req.FieldValuesJson ?? entity.FieldValuesJson;
        var nextTableValuesJson = req.TableValuesJson ?? entity.TableValuesJson;
        var mergedDynamicFlowTopLevelValues = false;

        if (acceptsReportDataPayload &&
            !isStackedAggregatePayload &&
            DynamicFlowBranchVisibility.IsFlowAssignment(reportAccess.assignment) &&
            req.TableValuesJson is null &&
            requestedValues1DJson is not null &&
            runtimeTopLevelBlock is not null)
        {
            nextTableValuesJson = MergeDynamicFlowTopLevelValuesIntoTableValuesJson(
                entity,
                entity.TableValuesJson,
                runtimeTopLevelBlock,
                Values1DCompression.DeserializeObjects(requestedValues1DJson, _jsonOptions),
                actorUserId);
            mergedDynamicFlowTopLevelValues = true;
        }

        if (acceptsReportDataPayload && !isStackedAggregatePayload)
        {
            var writablePayload = await PrepareDynamicFlowReportWritePayloadAsync(
                reportAccess.assignment,
                entity,
                nextFieldValuesJson,
                nextTableValuesJson,
                actorUserId,
                validateRequired: true,
                ct: ct);
            nextFieldValuesJson = writablePayload.FieldValuesJson;
            nextTableValuesJson = writablePayload.TableValuesJson;

            if (req.TableValuesJson is not null || mergedDynamicFlowTopLevelValues)
            {
                var baseValues1DJson = requestedValues1DJson ?? entity.Values1DJson;
                var baseValuesCount = Values1DCompression.DeserializeObjects(baseValues1DJson, _jsonOptions).Count;
                runtimeTopLevelBlock ??= await ResolveRuntimeTopLevelBlockShapeAsync(
                    entity,
                    nextTableValuesJson,
                    baseValuesCount,
                    ct);
                if (runtimeTopLevelBlock is not null)
                {
                    requestedValues1DJson = ResolveDynamicFlowMappingTopLevelValuesJson(
                        baseValues1DJson,
                        nextTableValuesJson,
                        runtimeTopLevelBlock.BlockId,
                        _jsonOptions);
                }
            }
        }

        await ValidateRuntimeRowLabelsAsync(
            entity,
            acceptsReportDataPayload ? nextTableValuesJson : entity.TableValuesJson,
            ct);
        if (acceptsReportDataPayload && !isStackedAggregatePayload)
            nextFieldValuesJson = await ValidateRuntimeDataPayloadAsync(
                entity,
                requestedValues1DJson is not null
                    ? Values1DCompression.DeserializeObjects(requestedValues1DJson, _jsonOptions)
                    : DeserializeRawValues1D(entity.Values1DJson),
                nextFieldValuesJson,
                nextTableValuesJson,
                validateRequiredFields: true,
                ct);

        if (acceptsReportDataPayload)
        {
            if (requestedValues1DJson is not null)
                entity.Values1DJson = requestedValues1DJson;

            if (req.FieldValuesJson is not null)
                entity.FieldValuesJson = nextFieldValuesJson;

            if (req.TableValuesJson is not null || mergedDynamicFlowTopLevelValues)
                entity.TableValuesJson = Values1DCompression.CompressTableValuesJson(nextTableValuesJson, _jsonOptions);
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

        var completedDatePolicy = ResolveReportCompletedDatePolicy(reportAccess.assignment, entity, period, now);
        var isHistoricalData = IsHistoricalReportData(entity, period, completedDatePolicy);
        var startedDate = ResolveServerReportStartedDate(entity, period);
        var requestedCompletedDate = NormalizeDate(req.CompletedDate);
        var completedDate = ValidateReportCompletedDateInput(
            completedDatePolicy,
            completedDatePolicy.CanEditCompletedDate ? requestedCompletedDate ?? entity.CompletedDate : requestedCompletedDate,
            ReportDetails(entity, actorUserId),
            requireWhenMissing: true);
        EnsureReportDateRange(startedDate, completedDate, "StartedDate", "CompletedDate");
        var effectiveDueAtUtc = ResolveEffectiveReportDueAtUtc(entity.DueAtUtc ?? period?.DueAtUtc, reportAccess.assignment);
        var isLate = ResolveReportLateSubmission(
            isHistoricalData,
            completedDate,
            effectiveDueAtUtc,
            now);
        var lateReason = string.IsNullOrWhiteSpace(req.LateReason) ? entity.LateReason : req.LateReason?.Trim();

        if (isLate && string.IsNullOrWhiteSpace(lateReason))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_LATE_REASON_REQUIRED,
                ReportDetails(entity, actorUserId));

        var autoApproveByCondition =
            !isHistoricalData &&
            WorkAssignmentAutoApproveConditionNormalizer.Matches(
                reportAccess.assignment.AutoApproveConditionJson,
                entity.FieldValuesJson);
        var approveWithoutManualReview = autoApproveByCondition;
        var approvalActorUserId = ResolveAutoApproveActorUserId(reportAccess.assignment, actorUserId);
        var previousOpenPeriod = approveWithoutManualReview
            ? await FindPreviousOpenPeriodAsync(period, ct)
            : null;
        if (previousOpenPeriod is not null)
        {
            autoApproveByCondition = false;
            approveWithoutManualReview = false;
            approvalActorUserId = actorUserId;
        }

        var nextStatus = approveWithoutManualReview
            ? WorkAssignmentReportStatus.Approved
            : WorkAssignmentReportStatus.Submitted;
        var autoApproveComment = autoApproveByCondition
            ? WorkAssignmentAutoApprovalState.AutoApproveReviewerComment
            : null;
        var autoApproveReason = autoApproveByCondition
            ? "AUTO_APPROVE_CONDITION"
            : null;
        var autoApproveSnapshotJson = autoApproveByCondition
            ? reportAccess.assignment.AutoApproveConditionJson
            : null;
        var autoApprovalLifecycleCommand = approveWithoutManualReview
            ? BuildLegacyAutoApprovalLifecycleCommand(
                entity,
                lifecycleCommand,
                approvalActorUserId,
                autoApproveSnapshotJson)
            : null;
        var committedLifecycleCommand =
            autoApprovalLifecycleCommand ?? lifecycleCommand;

        entity.Status = nextStatus;
        entity.StartedDate = startedDate;
        entity.CompletedDate = completedDate;
        entity.IsHistoricalData = isHistoricalData;
        entity.DueAtUtc = effectiveDueAtUtc;
        entity.IsLateSubmission = isLate;
        entity.LateReason = lateReason;
        entity.SubmittedAtUtc = now;
        entity.SubmittedByUserId = actorUserId;
        entity.ReturnedAtUtc = null;
        entity.ReturnedByUserId = null;
        entity.ReviewerComment = approveWithoutManualReview ? autoApproveComment : entity.ReviewerComment;
        entity.ApprovedAtUtc = approveWithoutManualReview ? now : entity.ApprovedAtUtc;
        entity.ApprovedByUserId = approveWithoutManualReview ? approvalActorUserId : entity.ApprovedByUserId;
        entity.AutoApprovedAtUtc = approveWithoutManualReview ? now : null;
        entity.AutoApprovedByUserId = approveWithoutManualReview ? approvalActorUserId : null;
        entity.AutoApproveConditionSnapshotJson = autoApproveSnapshotJson;
        entity.AutoApprovalConfirmedAtUtc = null;
        entity.AutoApprovalConfirmedByUserId = null;
        entity.CreatedByUserId = string.IsNullOrWhiteSpace(entity.CreatedByUserId) ? actorUserId : entity.CreatedByUserId;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = approveWithoutManualReview ? approvalActorUserId : actorUserId;
        var payloadResult = WorkReportPayloadService.PreflightReportPayload(
            entity,
            entity.Values1DJson,
            entity.FieldValuesJson,
            entity.TableValuesJson,
            entity.SummarySourceJson,
            entity.UpdatedByUserId,
            now);
        await lifecycleSeriesLease.RenewAsync(ct);
        var reservation = await ReservePayloadMutationCommandAsync(entity, payloadCommand, now, ct, deferToTransaction: true);
        if (reservation == PayloadCommandReservationOutcome.CompletedReplay)
        {
            var current = await _ctx.WorkAssignmentReports
                .Find(x => x.Id == entity.Id && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw ReportNotFound(entity.Id);
            if (!WorkReportLifecycleCommandContract.IsCompletedReplay(current, lifecycleCommand) &&
                !IsCompletedLegacyAutoApprovalSubmitReplay(current, lifecycleCommand))
            {
                throw WorkReportLifecycleCommandContract.Conflict(
                    current,
                    lifecycleCommand);
            }

            await TryReconcileCommittedLifecycleProjectionAsync(current.Id);
            return await MapPayloadCommandReplayResponseAsync(current.Id, actorUserId, CancellationToken.None);
        }

        var headerCommitUpdate = WorkReportLifecycleCommandContract.ApplyCompletion(
                ApplyPayloadCommandCompletion(
                ApplyPayloadHeaderUpdate(
                    Builders<WorkAssignmentReport>.Update,
                payloadResult,
                now)
                .Set(x => x.DataOrigin, entity.DataOrigin)
                .Set(x => x.CumulativeContributionMode, entity.CumulativeContributionMode)
                .Set(x => x.CumulativeContributionPolicyJson, entity.CumulativeContributionPolicyJson)
                .Set(x => x.AggregateSourceReportIds, entity.AggregateSourceReportIds)
                .Set(x => x.AggregateSourceAssignmentIds, entity.AggregateSourceAssignmentIds)
                .Set(x => x.AggregateSourceUpdatedAtUtc, entity.AggregateSourceUpdatedAtUtc)
                .Set(x => x.AggregateSnapshotDirty, entity.AggregateSnapshotDirty)
                .Set(x => x.AggregateSnapshotDirtyAtUtc, entity.AggregateSnapshotDirtyAtUtc)
                .Set(x => x.AggregateSnapshotRefreshedAtUtc, entity.AggregateSnapshotRefreshedAtUtc)
                .Set(x => x.AggregateRefreshError, entity.AggregateRefreshError)
                .Set(x => x.Status, entity.Status)
                .Set(x => x.StartedDate, entity.StartedDate)
                .Set(x => x.CompletedDate, entity.CompletedDate)
                .Set(x => x.IsHistoricalData, entity.IsHistoricalData)
                .Set(x => x.DueAtUtc, entity.DueAtUtc)
                .Set(x => x.IsLateSubmission, entity.IsLateSubmission)
                .Set(x => x.LateReason, entity.LateReason)
                .Set(x => x.SubmittedAtUtc, entity.SubmittedAtUtc)
                .Set(x => x.SubmittedByUserId, entity.SubmittedByUserId)
                .Set(x => x.ReturnedAtUtc, (DateTime?)null)
                .Set(x => x.ReturnedByUserId, (string?)null)
                .Set(x => x.ReviewerComment, entity.ReviewerComment)
                .Set(x => x.ApprovedAtUtc, entity.ApprovedAtUtc)
                .Set(x => x.ApprovedByUserId, entity.ApprovedByUserId)
                .Set(x => x.AutoApprovedAtUtc, entity.AutoApprovedAtUtc)
                .Set(x => x.AutoApprovedByUserId, entity.AutoApprovedByUserId)
                .Set(x => x.AutoApproveConditionSnapshotJson, entity.AutoApproveConditionSnapshotJson)
                .Set(x => x.AutoApprovalConfirmedAtUtc, entity.AutoApprovalConfirmedAtUtc)
                .Set(x => x.AutoApprovalConfirmedByUserId, entity.AutoApprovalConfirmedByUserId)
                .Set(x => x.CreatedByUserId, string.IsNullOrWhiteSpace(entity.CreatedByUserId) ? actorUserId : entity.CreatedByUserId)
                .Set(x => x.UpdatedAtUtc, entity.UpdatedAtUtc)
                .Set(x => x.UpdatedByUserId, entity.UpdatedByUserId),
                payloadCommand,
                payloadResult),
                committedLifecycleCommand,
                entity.Status,
                resultIsActive: true);
        var mappingBinding =
            DynamicFlowMappingLifecycleBinding.FromReport(entity);
        var submitOutboxSeed = new WorkReportLifecycleOutboxSeed(
            lifecycleCommand.CommandId,
            lifecycleCommand.ExpectedLifecycleRevision + 1,
            lifecycleCommand.Operation,
            actorUserId,
            WorkAssignmentReportStatus.Draft.ToString(),
            WorkAssignmentReportStatus.Submitted.ToString(),
            FromIsActive: true,
            ToIsActive: true,
            PayloadRevision: payloadResult.PayloadRevision,
            PayloadHash: payloadResult.PayloadHash,
            CreatedAtUtc: now,
            BusinessComment: lateReason,
            MappingBinding: mappingBinding);
        WorkReportLifecycleOutboxSeed? autoApprovalOutboxSeed = null;
        if (autoApprovalLifecycleCommand is not null)
        {
            autoApprovalOutboxSeed = new WorkReportLifecycleOutboxSeed(
                autoApprovalLifecycleCommand.CommandId,
                autoApprovalLifecycleCommand.ExpectedLifecycleRevision + 1,
                autoApprovalLifecycleCommand.Operation,
                approvalActorUserId,
                WorkAssignmentReportStatus.Submitted.ToString(),
                WorkAssignmentReportStatus.Approved.ToString(),
                FromIsActive: true,
                ToIsActive: true,
                PayloadRevision: payloadResult.PayloadRevision,
                PayloadHash: payloadResult.PayloadHash,
                CreatedAtUtc: now,
                BusinessReason: autoApproveReason,
                BusinessComment: autoApproveComment,
                BusinessSnapshotJson: autoApproveSnapshotJson,
                MappingBinding: mappingBinding);
        }
        headerCommitUpdate = AppendLegacySubmitLifecycleOutbox(
            headerCommitUpdate,
            submitOutboxSeed,
            autoApprovalOutboxSeed);

        await lifecycleSeriesLease.RenewAsync(ct);
        var headerCommit = await CommitLegacyLifecycleWithDirectSourceFenceAsync(
            entity,
            BuildPayloadMutationCommitFilter(entity, payloadCommand) &
            WorkReportLifecycleCommandContract.BuildCommitFilter(
                entity,
                lifecycleCommand,
                WorkAssignmentReportStatus.Draft,
                expectedPayloadMutationCommandId: lifecycleCommand.CommandId),
            headerCommitUpdate,
            ct, lifecycleCommand, entity.Status, actorUserId,
            persistPayload: async (session, before, token) =>
            {
                await AggregateHostIntegration.GuardPayloadAsync(_ctx, session, before, entity.FieldValuesJson, entity.TableValuesJson, token);
                var written = await _payloadWriter.SaveReportPayloadAsync(before, entity.Values1DJson, entity.FieldValuesJson,
                    entity.TableValuesJson, entity.SummarySourceJson, entity.UpdatedByUserId, now, token, session);
                if (written != payloadResult) throw new AggregatePreviewException("AGG_PAYLOAD_PLAN_STALE");
            },
            aggregateSessionKey: req.AggregateSessionKey, aggregateConfirmation: req.AggregateConfirmationToken,
            resultLifecycleRevision: committedLifecycleCommand.ExpectedLifecycleRevision + 1,
            payloadCommandToReserve: payloadCommand);

        if (headerCommit.ModifiedCount != 1)
        {
            var current = await _ctx.WorkAssignmentReports
                .Find(x => x.Id == entity.Id && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw ReportNotFound(entity.Id);
            if (IsCompletedPayloadCommandReplay(current, payloadCommand) &&
                (WorkReportLifecycleCommandContract.IsCompletedReplay(
                     current,
                     lifecycleCommand) ||
                 IsCompletedLegacyAutoApprovalSubmitReplay(
                     current,
                     lifecycleCommand)))
            {
                await TryReconcileCommittedLifecycleProjectionAsync(current.Id);
                return await MapPayloadCommandReplayResponseAsync(current, actorUserId, CancellationToken.None);
            }

            throw WorkReportLifecycleCommandContract.Conflict(current, lifecycleCommand);
        }

        WorkReportLifecycleCommandContract.ApplyCompletionInMemory(
            entity,
            committedLifecycleCommand,
            entity.Status,
            resultIsActive: true);

        await TryReconcileCommittedLifecycleProjectionAsync(entity.Id);
        return await MapPayloadCommandReplayResponseAsync(entity.Id, actorUserId, CancellationToken.None);
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

        var canReadAssignment = await WorkAssignmentReadAccessHelper.CanReadAssignmentAsync(
            _ctx,
            assignment.Id,
            actorUserId,
            ct);
        if (DynamicFlowBranchVisibility.IsFlowAssignment(assignment) && !canReadAssignment)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_LOG_ACCESS_FORBIDDEN,
                ReportDetails(report, actorUserId));

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
            SnapshotJson = DynamicFlowReportPermissionEnforcer.RedactSummarySourceValues(x.SnapshotJson)
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
        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == entity.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportAssignmentNotFound(entity.WorkAssignmentId);

        await LoadReviewNodeAsync(assignment, actorUserId, ct);
        EnsureReportIsActive(entity);
        await ValidateDynamicFlowMappingLifecycleMutationBoundaryAsync(
            entity,
            "LEGACY_APPROVE",
            ct);

        if (entity.AssigneeUserId == actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_REVIEW_SELF_FORBIDDEN,
                ReportDetails(entity, actorUserId));

        var approveCommandHash = WorkReportLifecycleCommandContract.ComputeHash("LEGACY_APPROVE", req);
        var (lifecycleCommand, lifecycleResolution) = WorkReportLifecycleCommandContract.Resolve(
            entity,
            req.ExpectedLifecycleRevision,
            req.ExpectedPayloadRevision,
            req.CommandId,
            "LEGACY_APPROVE",
            approveCommandHash);
        if (lifecycleResolution == WorkReportLifecycleCommandResolution.CompletedReplay)
        {
            await TryReconcileCommittedLifecycleProjectionAsync(entity.Id);
            return await MapPayloadCommandReplayResponseAsync(entity, actorUserId, CancellationToken.None);
        }

        await using var lifecycleSeriesLease = await _lifecycleSeriesLock.AcquireAsync(
            entity.WorkAssignmentId,
            lifecycleCommand.Operation,
            ct);

        await EnsureReportMutationScopeOpenAsync(assignment, actorUserId, ct, entity.WorkReportPeriodId);

        if (entity.Status != WorkAssignmentReportStatus.Submitted)
            throw InvalidReportStatus(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_APPROVE_STATUS_INVALID,
                entity,
                WorkAssignmentReportStatus.Submitted,
                actorUserId);

        await HydrateReportPayloadAsync(entity, ct);
        WorkReportPayloadConsistency.EnsureReadyForStatisticProjection(entity);

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        await EnsurePreviousReportsApprovedAsync(period, ct);

        var now = DateTime.UtcNow;
        var lateReason = string.IsNullOrWhiteSpace(req.LateReasonOverride)
            ? entity.LateReason
            : req.LateReasonOverride.Trim();
        var completedDatePolicy = ResolveReportCompletedDatePolicy(assignment, entity, period, now);
        var isHistoricalData = IsHistoricalReportData(entity, period, completedDatePolicy);
        if (isHistoricalData && !entity.CompletedDate.HasValue)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_HISTORICAL_COMPLETED_DATE_REQUIRED,
                ReportDetails(entity, actorUserId));
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

        var reportCommitUpdate = WorkReportLifecycleCommandContract.ApplyCompletion(
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
                lifecycleCommand,
                WorkAssignmentReportStatus.Approved,
                resultIsActive: true);
        reportCommitUpdate = WorkReportLifecycleOutboxContract.Append(
            reportCommitUpdate,
            WorkReportLifecycleOutboxContract.FromCommand(
                entity,
                lifecycleCommand,
                WorkAssignmentReportStatus.Approved,
                resultIsActive: true,
                actorUserId: actorUserId,
                committedAtUtc: now,
                resultPayloadHash: entity.PayloadHash,
                businessComment: req.ReviewerComment));

        await lifecycleSeriesLease.RenewAsync(ct);
        var reportCommit = await CommitLegacyLifecycleWithDirectSourceFenceAsync(
            entity,
            WorkReportLifecycleCommandContract.BuildCommitFilter(
                entity,
                lifecycleCommand,
                WorkAssignmentReportStatus.Submitted),
            reportCommitUpdate,
            ct, lifecycleCommand, WorkAssignmentReportStatus.Approved, actorUserId);

        if (reportCommit.ModifiedCount != 1)
        {
            var current = await _ctx.WorkAssignmentReports
                .Find(x => x.Id == id && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw ReportNotFound(id);
            if (WorkReportLifecycleCommandContract.IsCompletedReplay(current, lifecycleCommand))
            {
                await TryReconcileCommittedLifecycleProjectionAsync(current.Id);
                return await MapPayloadCommandReplayResponseAsync(current, actorUserId, CancellationToken.None);
            }
            throw WorkReportLifecycleCommandContract.Conflict(current, lifecycleCommand);
        }

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
        WorkReportLifecycleCommandContract.ApplyCompletionInMemory(
            entity,
            lifecycleCommand,
            WorkAssignmentReportStatus.Approved,
            resultIsActive: true);
        await TryReconcileCommittedLifecycleProjectionAsync(entity.Id);
        return await MapPayloadCommandReplayResponseAsync(entity.Id, actorUserId, CancellationToken.None);
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

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw ReportNotFound(id);
        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == entity.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportAssignmentNotFound(entity.WorkAssignmentId);

        await LoadReviewNodeAsync(assignment, actorUserId, ct);
        EnsureReportIsActive(entity);
        await ValidateDynamicFlowMappingLifecycleMutationBoundaryAsync(
            entity,
            "LEGACY_RETURN",
            ct,
            DynamicFlowMappingIntegrityMode.AllowHistorical);

        if (entity.AssigneeUserId == actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_REVIEW_SELF_FORBIDDEN,
                ReportDetails(entity, actorUserId));

        if (string.IsNullOrWhiteSpace(req.ReturnReason))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_RETURN_COMMENT_REQUIRED,
                new { reportId = id, req.ReturnReason });

        var returnCommandHash = WorkReportLifecycleCommandContract.ComputeHash("LEGACY_RETURN", req);
        var (lifecycleCommand, lifecycleResolution) = WorkReportLifecycleCommandContract.Resolve(
            entity,
            req.ExpectedLifecycleRevision,
            req.ExpectedPayloadRevision,
            req.CommandId,
            "LEGACY_RETURN",
            returnCommandHash);
        if (lifecycleResolution == WorkReportLifecycleCommandResolution.CompletedReplay)
        {
            await TryReconcileCommittedLifecycleProjectionAsync(entity.Id);
            return await MapPayloadCommandReplayResponseAsync(entity, actorUserId, CancellationToken.None);
        }

        await using var lifecycleSeriesLease = await _lifecycleSeriesLock.AcquireAsync(
            entity.WorkAssignmentId,
            lifecycleCommand.Operation,
            ct);

        await EnsureReportMutationScopeOpenAsync(assignment, actorUserId, ct, entity.WorkReportPeriodId);

        if (entity.Status != WorkAssignmentReportStatus.Submitted)
            throw InvalidReportStatus(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_RETURN_STATUS_INVALID,
                entity,
                WorkAssignmentReportStatus.Submitted,
                actorUserId);

        var now = DateTime.UtcNow;
        var returnReason = req.ReturnReason.Trim();

        var reportCommitUpdate = WorkReportLifecycleCommandContract.ApplyCompletion(
                Builders<WorkAssignmentReport>.Update
                .Set(x => x.Status, WorkAssignmentReportStatus.Draft)
                .Set(x => x.ReturnReason, returnReason)
                .Set(x => x.ReviewerComment, req.ReviewerComment)
                .Set(x => x.ReturnedAtUtc, now)
                .Set(x => x.ReturnedByUserId, actorUserId)
                .Set(x => x.ApprovedAtUtc, (DateTime?)null)
                .Set(x => x.ApprovedByUserId, (string?)null)
                .Set(x => x.AutoApprovedAtUtc, (DateTime?)null)
                .Set(x => x.AutoApprovedByUserId, (string?)null)
                .Set(x => x.AutoApproveConditionSnapshotJson, (string?)null)
                .Set(x => x.AutoApprovalConfirmedAtUtc, (DateTime?)null)
                .Set(x => x.AutoApprovalConfirmedByUserId, (string?)null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
                lifecycleCommand,
                WorkAssignmentReportStatus.Draft,
                resultIsActive: true);
        reportCommitUpdate = WorkReportLifecycleOutboxContract.Append(
            reportCommitUpdate,
            WorkReportLifecycleOutboxContract.FromCommand(
                entity,
                lifecycleCommand,
                WorkAssignmentReportStatus.Draft,
                resultIsActive: true,
                actorUserId: actorUserId,
                committedAtUtc: now,
                resultPayloadHash: entity.PayloadHash,
                businessReason: returnReason,
                businessComment: req.ReviewerComment));

        await lifecycleSeriesLease.RenewAsync(ct);
        var reportCommit = await CommitLegacyLifecycleWithDirectSourceFenceAsync(
            entity,
            WorkReportLifecycleCommandContract.BuildCommitFilter(
                entity,
                lifecycleCommand,
                WorkAssignmentReportStatus.Submitted),
            reportCommitUpdate,
            ct, lifecycleCommand, WorkAssignmentReportStatus.Draft, actorUserId);

        if (reportCommit.ModifiedCount != 1)
        {
            var current = await _ctx.WorkAssignmentReports
                .Find(x => x.Id == id && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw ReportNotFound(id);
            if (WorkReportLifecycleCommandContract.IsCompletedReplay(current, lifecycleCommand))
            {
                await TryReconcileCommittedLifecycleProjectionAsync(current.Id);
                return await MapPayloadCommandReplayResponseAsync(current, actorUserId, CancellationToken.None);
            }
            throw WorkReportLifecycleCommandContract.Conflict(current, lifecycleCommand);
        }

        entity.Status = WorkAssignmentReportStatus.Draft;
        entity.ReturnReason = returnReason;
        entity.ReviewerComment = req.ReviewerComment;
        entity.ReturnedAtUtc = now;
        entity.ReturnedByUserId = actorUserId;
        entity.ApprovedAtUtc = null;
        entity.ApprovedByUserId = null;
        entity.AutoApprovedAtUtc = null;
        entity.AutoApprovedByUserId = null;
        entity.AutoApproveConditionSnapshotJson = null;
        entity.AutoApprovalConfirmedAtUtc = null;
        entity.AutoApprovalConfirmedByUserId = null;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = actorUserId;
        WorkReportLifecycleCommandContract.ApplyCompletionInMemory(
            entity,
            lifecycleCommand,
            WorkAssignmentReportStatus.Draft,
            resultIsActive: true);
        await TryReconcileCommittedLifecycleProjectionAsync(entity.Id);
        return await MapPayloadCommandReplayResponseAsync(entity.Id, actorUserId, CancellationToken.None);
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
        var reportAccess = await EnsureReportAccessAsync(entity, actorUserId, ct);
        if (!reportAccess.isAssignee || entity.AssigneeUserId != actorUserId)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_WITHDRAW_FORBIDDEN,
                ReportDetails(entity, actorUserId));
        EnsureReportIsActive(entity);
        await ValidateDynamicFlowMappingLifecycleMutationBoundaryAsync(
            entity,
            "WITHDRAW",
            ct,
            DynamicFlowMappingIntegrityMode.AllowHistorical);

        var withdrawCommandHash = WorkReportLifecycleCommandContract.ComputeHash("WITHDRAW", req);
        var (lifecycleCommand, lifecycleResolution) = WorkReportLifecycleCommandContract.Resolve(
            entity,
            req.ExpectedLifecycleRevision,
            req.ExpectedPayloadRevision,
            req.CommandId,
            "WITHDRAW",
            withdrawCommandHash);
        if (lifecycleResolution == WorkReportLifecycleCommandResolution.CompletedReplay)
        {
            await TryReconcileCommittedLifecycleProjectionAsync(entity.Id);
            return await MapPayloadCommandReplayResponseAsync(entity, actorUserId, CancellationToken.None);
        }

        await using var lifecycleSeriesLease = await _lifecycleSeriesLock.AcquireAsync(
            entity.WorkAssignmentId,
            lifecycleCommand.Operation,
            ct);

        var assignment = reportAccess.assignment;
        await EnsureReportMutationScopeOpenAsync(assignment, actorUserId, ct, entity.WorkReportPeriodId);

        var withdrawsAutoApproved = entity.Status == WorkAssignmentReportStatus.Approved &&
                                    WorkAssignmentAutoApprovalState.CanReporterWithdraw(entity);

        if (entity.Status != WorkAssignmentReportStatus.Submitted && !withdrawsAutoApproved)
            throw InvalidReportStatus(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_WITHDRAW_STATUS_INVALID,
                entity,
                WorkAssignmentReportStatus.Submitted,
                actorUserId);

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == entity.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (withdrawsAutoApproved)
            await EnsureNoLaterApprovedReportsAsync(period, ct);

        var now = DateTime.UtcNow;
        var withdrawReason = string.IsNullOrWhiteSpace(req.ReturnReason)
            ? null
            : req.ReturnReason.Trim();
        var fromStatus = entity.Status;

        var reportCommitUpdate = WorkReportLifecycleCommandContract.ApplyCompletion(
                Builders<WorkAssignmentReport>.Update
                .Set(x => x.Status, WorkAssignmentReportStatus.Draft)
                .Set(x => x.ReturnReason, withdrawReason)
                .Set(x => x.ReturnedAtUtc, (DateTime?)null)
                .Set(x => x.ReturnedByUserId, (string?)null)
                .Set(x => x.ApprovedAtUtc, (DateTime?)null)
                .Set(x => x.ApprovedByUserId, (string?)null)
                .Set(x => x.AutoApprovedAtUtc, (DateTime?)null)
                .Set(x => x.AutoApprovedByUserId, (string?)null)
                .Set(x => x.AutoApproveConditionSnapshotJson, (string?)null)
                .Set(x => x.AutoApprovalConfirmedAtUtc, (DateTime?)null)
                .Set(x => x.AutoApprovalConfirmedByUserId, (string?)null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
                lifecycleCommand,
                WorkAssignmentReportStatus.Draft,
                resultIsActive: true);
        reportCommitUpdate = WorkReportLifecycleOutboxContract.Append(
            reportCommitUpdate,
            WorkReportLifecycleOutboxContract.FromCommand(
                entity,
                lifecycleCommand,
                WorkAssignmentReportStatus.Draft,
                resultIsActive: true,
                actorUserId: actorUserId,
                committedAtUtc: now,
                resultPayloadHash: entity.PayloadHash,
                businessReason: withdrawReason,
                businessComment: req.ReviewerComment));

        await lifecycleSeriesLease.RenewAsync(ct);
        var reportCommit = await CommitLegacyLifecycleWithDirectSourceFenceAsync(
            entity,
            WorkReportLifecycleCommandContract.BuildCommitFilter(
                entity,
                lifecycleCommand,
                fromStatus),
            reportCommitUpdate,
            ct, lifecycleCommand, WorkAssignmentReportStatus.Draft, actorUserId);

        if (reportCommit.ModifiedCount != 1)
        {
            var current = await _ctx.WorkAssignmentReports
                .Find(x => x.Id == id && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw ReportNotFound(id);
            if (WorkReportLifecycleCommandContract.IsCompletedReplay(current, lifecycleCommand))
            {
                await TryReconcileCommittedLifecycleProjectionAsync(current.Id);
                return await MapPayloadCommandReplayResponseAsync(current, actorUserId, CancellationToken.None);
            }
            throw WorkReportLifecycleCommandContract.Conflict(current, lifecycleCommand);
        }

        entity.Status = WorkAssignmentReportStatus.Draft;
        entity.ReturnReason = withdrawReason;
        entity.ReturnedAtUtc = null;
        entity.ReturnedByUserId = null;
        entity.ApprovedAtUtc = null;
        entity.ApprovedByUserId = null;
        entity.AutoApprovedAtUtc = null;
        entity.AutoApprovedByUserId = null;
        entity.AutoApproveConditionSnapshotJson = null;
        entity.AutoApprovalConfirmedAtUtc = null;
        entity.AutoApprovalConfirmedByUserId = null;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = actorUserId;
        WorkReportLifecycleCommandContract.ApplyCompletionInMemory(
            entity,
            lifecycleCommand,
            WorkAssignmentReportStatus.Draft,
            resultIsActive: true);
        await TryReconcileCommittedLifecycleProjectionAsync(entity.Id);
        return await MapPayloadCommandReplayResponseAsync(entity.Id, actorUserId, CancellationToken.None);
    }

    private async Task<UpdateResult> CommitLegacyLifecycleWithDirectSourceFenceAsync(
        WorkAssignmentReport expectedReport,
        FilterDefinition<WorkAssignmentReport> reportCommitFilter,
        UpdateDefinition<WorkAssignmentReport> reportCommitUpdate,
        CancellationToken ct,
        WorkReportLifecycleCommand? lifecycleCommand = null,
        WorkAssignmentReportStatus? resultStatus = null,
        string? actorUserId = null,
        Func<IClientSessionHandle, WorkAssignmentReport, CancellationToken, Task>? persistPayload = null,
        string? aggregateSessionKey = null, string? aggregateConfirmation = null,
        int? resultLifecycleRevision = null,
        PayloadMutationCommand? payloadCommandToReserve = null,
        WorkAssignmentReportStatus reservationStatus = WorkAssignmentReportStatus.Draft)
    {
        ArgumentNullException.ThrowIfNull(expectedReport);
        ArgumentNullException.ThrowIfNull(reportCommitFilter);
        ArgumentNullException.ThrowIfNull(reportCommitUpdate);

        var expectedWorkId = expectedReport.WorkId?.Trim();
        if (string.IsNullOrWhiteSpace(expectedReport.Id) ||
            string.IsNullOrWhiteSpace(expectedWorkId))
        {
            throw new InvalidOperationException(
                "P9_DIRECT_SOURCE_FENCE_REPORT_WORK_REQUIRED");
        }

        var reportFilter = Builders<WorkAssignmentReport>.Filter;
        var authoritativeCommitFilter =
            reportCommitFilter &
            reportFilter.Eq(report => report.Id, expectedReport.Id) &
            reportFilter.Eq(report => report.WorkId, expectedWorkId) &
            reportFilter.Eq(report => report.IsDeleted, false);

        return await _dynamicFlowTransactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                if (payloadCommandToReserve is not null)
                    await ReservePayloadMutationCommandAsync(expectedReport, payloadCommandToReserve, DateTime.UtcNow,
                        transactionCt, reservationStatus, session: session);
                var before = await _ctx.WorkAssignmentReports.Find(session, authoritativeCommitFilter).FirstOrDefaultAsync(transactionCt);
                if (before is null)
                {
                    if (payloadCommandToReserve is not null) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
                    return new UpdateResult.Acknowledged(0, 0, null);
                }
                await AggregateHostIntegration.GuardReportAsync(_ctx, session, before, transactionCt);
                await tdtd_be.Services.WorkAssignments.Progress.WorkExecutionScopeGuard.EnsureOpenAsync(
                    _ctx, before.WorkAssignmentId, before.WorkReportPeriodId, transactionCt, session);
                if (lifecycleCommand is not null && resultStatus.HasValue)
                    await AggregateHostIntegration.LifecycleAsync(_ctx, session, _dynamicFlowTransactions, before,
                        lifecycleCommand.Operation, actorUserId!, lifecycleCommand.CommandId, resultStatus.Value,
                        resultLifecycleRevision ?? lifecycleCommand.ExpectedLifecycleRevision + 1, transactionCt,
                        _configuration, aggregateSessionKey, aggregateConfirmation);
                if (persistPayload is not null) await persistPayload(session, before, transactionCt);
                var reportCommit =
                    await _ctx.WorkAssignmentReports.UpdateOneAsync(
                        session,
                        authoritativeCommitFilter,
                        reportCommitUpdate,
                        cancellationToken: transactionCt);
                if (reportCommit.ModifiedCount != 1)
                    throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
                if (resultStatus == WorkAssignmentReportStatus.Approved)
                    await tdtd_be.Services.WorkAssignments.Progress.WorkExecutionScopeGuard.RecordFirstApprovalAsync(
                        _ctx, session, before.WorkAssignmentId, DateTime.UtcNow, transactionCt,
                        before.WorkReportPeriodId, before.Status);
                await AggregateHostIntegration.InvalidateReportAsync(_ctx, session, before,
                    lifecycleCommand?.CommandId ?? before.PayloadMutationCommandId ?? "report:" + before.Id + ":" + before.PayloadRevision,
                    transactionCt);

                var authoritativeReportWorkId =
                    await _ctx.WorkAssignmentReports
                        .Find(
                            session,
                            report =>
                                report.Id == expectedReport.Id &&
                                report.WorkId == expectedWorkId &&
                                !report.IsDeleted)
                        .Project(report => report.WorkId)
                        .FirstOrDefaultAsync(transactionCt);
                if (!string.Equals(
                        authoritativeReportWorkId,
                        expectedWorkId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "P9_DIRECT_SOURCE_FENCE_REPORT_CONFLICT");
                }

                var authoritativeWorkId = await _ctx.Works
                    .Find(
                        session,
                        work =>
                            work.Id == authoritativeReportWorkId &&
                            !work.IsDeleted)
                    .Project(work => work.Id)
                    .FirstOrDefaultAsync(transactionCt);
                if (!string.Equals(
                        authoritativeWorkId,
                        expectedWorkId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "P9_DIRECT_SOURCE_FENCE_WORK_NOT_FOUND");
                }

                await WorkDirectSourceRevisionFence.IncrementAsync(
                    _ctx,
                    session,
                    authoritativeWorkId,
                    transactionCt);
                return reportCommit;
            },
            ct);
    }

    private static WorkReportLifecycleCommand
        BuildLegacySubmitLifecycleReplayCommand(
            WorkAssignmentReport report,
            int? expectedLifecycleRevision,
            PayloadMutationCommand payloadCommand)
    {
        if (!expectedLifecycleRevision.HasValue ||
            expectedLifecycleRevision.Value < 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode
                    .WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_REQUIRED,
                new
                {
                    reportId = report.Id,
                    expectedLifecycleRevision,
                    currentLifecycleRevision = report.LifecycleRevision
                });
        }

        return new WorkReportLifecycleCommand(
            expectedLifecycleRevision.Value,
            payloadCommand.ExpectedPayloadRevision,
            payloadCommand.CommandId,
            "SUBMIT",
            payloadCommand.CommandHash,
            PayloadRevisionDelta: 1);
    }

    private static WorkReportLifecycleCommand
        BuildLegacyAutoApprovalLifecycleCommand(
            WorkAssignmentReport report,
            WorkReportLifecycleCommand submitCommand,
            string approvalActorUserId,
            string? autoApproveConditionSnapshotJson)
    {
        const string operation = "REVIEW_CONFIRM_AUTO_APPROVE";
        var approvalLifecycleRevision =
            submitCommand.ExpectedLifecycleRevision + 2;
        var approvalPayloadRevision =
            submitCommand.ExpectedPayloadRevision +
            submitCommand.PayloadRevisionDelta;
        var identityHash =
            WorkReportLifecycleOutboxContract.ComputeEntryKey(
                submitCommand.CommandId,
                approvalLifecycleRevision,
                operation);
        var commandId = $"system:auto-approve:{identityHash}";
        var commandHash = WorkReportLifecycleCommandContract.ComputeHash(
            operation,
            new
            {
                schemaVersion = "LEGACY_AUTO_APPROVE_LIFECYCLE_V1",
                reportId = report.Id,
                submitCommandId = submitCommand.CommandId,
                submitCommandHash = submitCommand.CommandHash,
                submitLifecycleRevision =
                    submitCommand.ExpectedLifecycleRevision + 1,
                approvalLifecycleRevision,
                approvalPayloadRevision,
                approvalActorUserId,
                autoApproveConditionSnapshotJson
            });

        return new WorkReportLifecycleCommand(
            ExpectedLifecycleRevision:
                submitCommand.ExpectedLifecycleRevision + 1,
            ExpectedPayloadRevision: approvalPayloadRevision,
            CommandId: commandId,
            Operation: operation,
            CommandHash: commandHash,
            PayloadRevisionDelta: 0);
    }

    private static bool IsCompletedLegacyAutoApprovalSubmitReplay(
        WorkAssignmentReport report,
        WorkReportLifecycleCommand submitCommand)
    {
        if (report.Status != WorkAssignmentReportStatus.Approved ||
            !WorkAssignmentAutoApprovalState.IsAutoApproved(report) ||
            string.IsNullOrWhiteSpace(report.AutoApprovedByUserId))
        {
            return false;
        }

        var approvalCommand = BuildLegacyAutoApprovalLifecycleCommand(
            report,
            submitCommand,
            report.AutoApprovedByUserId,
            report.AutoApproveConditionSnapshotJson);
        return WorkReportLifecycleCommandContract.IsCompletedReplay(
            report,
            approvalCommand);
    }

    private static UpdateDefinition<WorkAssignmentReport>
        AppendLegacySubmitLifecycleOutbox(
            UpdateDefinition<WorkAssignmentReport> update,
            WorkReportLifecycleOutboxSeed submitSeed,
            WorkReportLifecycleOutboxSeed? autoApprovalSeed)
    {
        if (autoApprovalSeed is null)
            return WorkReportLifecycleOutboxContract.Append(
                update,
                submitSeed);

        var entries = new[]
        {
            WorkReportLifecycleOutboxContract.CreateEntry(submitSeed),
            WorkReportLifecycleOutboxContract.CreateEntry(
                autoApprovalSeed)
        };
        return update
            .PushEach(
                report => report.LifecycleProjectionOutbox,
                entries)
            .Set(
                report => report.LifecycleProjectionLastError,
                null);
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

        await EnsureReportMutationScopeOpenAsync(assignment, actorUserId, ct, period.Id);

        var template = await ResolveDynamicExcelTemplateForPeriodAsync(period, ct);

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
        var effectiveDueAtUtc = ResolveEffectiveReportDueAtUtc(period.DueAtUtc, assignment);
        var completedDatePolicy = ResolveReportCompletedDatePolicy(assignment, null, period, now);
        var isHistoricalData = period.IsHistoricalData || IsBackfillCompletedDatePolicy(completedDatePolicy);
        // Cấu hình nguồn cũ chỉ giữ để đọc lịch sử; nháp mới không kế thừa chế độ mapping cũ.
        var defaultDataOrigin = tdtd_be.Services.WorkAssignments.LegacyAggregateRetirement.IsDisabled
            ? WorkReportDataOrigin.ManualInput
            : DynamicFormDataSourceRuleNormalizer.ResolveDefaultReportDataOrigin(assignment.DynamicFormDataSourceRulesJson);

        var entity = new WorkAssignmentReport
        {
            Id = ObjectId.GenerateNewId().ToString(),
            WorkId = period.WorkId,
            WorkAssignmentId = period.WorkAssignmentId,
            WorkReportPeriodId = period.Id,
            AssigneeUserId = period.AssigneeUserId,
            DynamicFormTemplateId = period.DynamicFormTemplateId ?? assignment.DynamicFormTemplateId,
            DynamicFormTemplateCode = period.DynamicFormTemplateCode ?? assignment.DynamicFormTemplateCode,
            DynamicFormTemplateName = period.DynamicFormTemplateName ?? assignment.DynamicFormTemplateName,
            DynamicFormFamilyId = period.DynamicFormFamilyId ?? assignment.DynamicFormFamilyId,
            DynamicFormVersionNo = period.DynamicFormVersionNo ?? assignment.DynamicFormVersionNo,
            DynamicFormSchemaHash = period.DynamicFormSchemaHash ?? assignment.DynamicFormSchemaHash,

            PeriodKey = period.PeriodKey,
            PeriodInstanceKey = periodInstanceKey,
            PeriodKind = periodKind,
            ReportTitle = period.ReportTitle,
            ReportDate = period.ReportDate,
            StartedDate = period.StartedDate ?? period.PeriodStart ?? period.ReportDate,
            CompletedDate = period.CompletedDate,
            IsHistoricalData = isHistoricalData,
            HistoricalDataApproved = period.HistoricalDataApproved,
            HistoricalDataApprovedAtUtc = period.HistoricalDataApprovedAtUtc,
            HistoricalDataApprovedByUserId = period.HistoricalDataApprovedByUserId,
            PeriodStart = period.PeriodStart,
            PeriodEnd = period.PeriodEnd,
            DueAtUtc = effectiveDueAtUtc,

            Status = WorkAssignmentReportStatus.Draft,
            ScheduleSnapshotJson = JsonSerializer.Serialize(BuildScheduleSnapshot(assignment), _jsonOptions),

            DynamicExcelTemplateId = template?.Id,
            DynamicExcelTemplateCode = template?.Code ?? string.Empty,
            DynamicExcelTemplateName = template?.Name ?? string.Empty,
            SpecJson = template?.SpecJson ?? string.Empty,

            DataRectR0 = template?.DataRectR0 ?? 0,
            DataRectC0 = template?.DataRectC0 ?? 0,
            DataRectR1 = template?.DataRectR1 ?? 0,
            DataRectC1 = template?.DataRectC1 ?? 0,
            W = template?.W ?? 0,
            H = template?.H ?? 0,

            Values1DJson = Values1DCompression.SerializeDecimals(
                template is null ? new List<decimal?>() : CreateEmptyValues1D(ResolveTemplateRuntimeInputCellCount(template), 1),
                _jsonOptions),
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

        var payloadResult = WorkReportPayloadService.PreflightReportPayload(
            entity,
            entity.Values1DJson,
            entity.FieldValuesJson,
            entity.TableValuesJson,
            entity.SummarySourceJson,
            actorUserId,
            now);
        ApplyPayloadMetadata(entity, payloadResult, now);

        var detailValues1DJson = entity.Values1DJson;
        var detailFieldValuesJson = entity.FieldValuesJson;
        var detailTableValuesJson = entity.TableValuesJson;
        var detailSummarySourceJson = entity.SummarySourceJson;
        entity.LifecycleProjectionOutbox =
        [
            WorkReportLifecycleOutboxContract.CreateEntry(
                new WorkReportLifecycleOutboxSeed(
                    CommandId: $"init-draft:{entity.Id}",
                    LifecycleRevision: entity.LifecycleRevision,
                    Operation: "INIT_DRAFT",
                    ActorUserId: actorUserId,
                    FromStatus: "NONE",
                    ToStatus: WorkAssignmentReportStatus.Draft.ToString(),
                    FromIsActive: false,
                    ToIsActive: true,
                    PayloadRevision: entity.PayloadRevision,
                    PayloadHash: entity.PayloadHash,
                    CreatedAtUtc: now))
        ];
        CompactEmbeddedPayloadHeader(entity);
        var nextPeriodStatus = ResolveDraftPeriodStatus(period, entity, now);
        await _dynamicFlowTransactions.ExecuteAsync(async (session, token) =>
        {
            await AggregateHostIntegration.GuardReportAsync(_ctx, session, entity, token);
            var written = await _payloadWriter.SaveReportPayloadAsync(
                new WorkAssignmentReport { Id = entity.Id, PayloadRevision = payloadResult.PayloadRevision - 1 },
                detailValues1DJson, detailFieldValuesJson, detailTableValuesJson, detailSummarySourceJson, actorUserId, now, token, session);
            if (written != payloadResult) throw new AggregatePreviewException("AGG_PAYLOAD_PLAN_STALE");
            await _ctx.WorkAssignmentReports.InsertOneAsync(session, entity, cancellationToken: token);
            await AggregateHostIntegration.CarryDeclarationAsync(_ctx, session, entity, token);
            await AggregateHostIntegration.SeedInstanceAsync(_ctx, session, entity, token);
            var periodCommit = await _ctx.WorkReportPeriods.UpdateOneAsync(session,
            x => x.Id == period.Id && !x.IsDeleted && x.CurrentReportId == period.CurrentReportId
                && x.ReportVersionCount == period.ReportVersionCount && x.UpdatedAtUtc == period.UpdatedAtUtc,
            Builders<WorkReportPeriod>.Update
                .Set(x => x.CurrentReportId, entity.Id)
                .Set(x => x.PeriodInstanceKey, periodInstanceKey)
                .Set(x => x.PeriodKind, periodKind)
                .Set(x => x.ReportVersionCount, entity.VersionNo)
                .Set(x => x.DueAtUtc, effectiveDueAtUtc)
                .Set(x => x.Status, nextPeriodStatus)
                .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus))
                .Set(x => x.IsHistoricalData, isHistoricalData)
                .Set(x => x.LastDraftSavedAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: token);
            if (periodCommit.ModifiedCount != 1) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
            await AggregateHostIntegration.InvalidateReportAsync(_ctx, session, entity, "init-draft:" + entity.Id, token);
            await WorkDirectSourceRevisionFence.IncrementAsync(_ctx, session, entity.WorkId, token);
        }, ct);
        RestoreRuntimePayload(entity, detailValues1DJson, detailFieldValuesJson, detailTableValuesJson, detailSummarySourceJson);

        period.CurrentReportId = entity.Id;
        period.PeriodInstanceKey = periodInstanceKey;
        period.PeriodKind = periodKind;
        period.ReportVersionCount = entity.VersionNo;
        period.DueAtUtc = effectiveDueAtUtc;
        period.Status = nextPeriodStatus;
        period.IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus);
        period.IsHistoricalData = isHistoricalData;
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

    private async Task<DynamicExcelTemplate?> ResolveDynamicExcelTemplateForPeriodAsync(
        WorkReportPeriod period,
        CancellationToken ct)
    {
        var dynamicExcelTemplateId = NormalizeOptionalTextOrNull(period.DynamicExcelId);

        if (string.IsNullOrWhiteSpace(dynamicExcelTemplateId) &&
            !string.IsNullOrWhiteSpace(period.DynamicFormTemplateId))
        {
            var form = await _ctx.DynamicFormTemplates
                .Find(x => x.Id == period.DynamicFormTemplateId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);

            if (form is null)
                throw AppExceptionFactory.NotFound(
                    AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
                    new { dynamicFormTemplateId = period.DynamicFormTemplateId, periodId = period.Id });

            dynamicExcelTemplateId =
                NormalizeOptionalTextOrNull(form.ExcelBlockDynamicExcelTemplateId) ??
                ExtractPrimaryDynamicExcelTemplateId(form.ExcelBlockJson, form.BlocksJson);

            if (string.IsNullOrWhiteSpace(dynamicExcelTemplateId))
                return null;
        }

        if (string.IsNullOrWhiteSpace(dynamicExcelTemplateId))
            throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_EXCEL_TEMPLATE_NOT_FOUND,
                new { dynamicExcelTemplateId = period.DynamicExcelId, periodId = period.Id });

        var template = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == dynamicExcelTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (template is null)
            throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_EXCEL_TEMPLATE_NOT_FOUND,
                new { dynamicExcelTemplateId, periodId = period.Id, period.DynamicFormTemplateId });

        return template;
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

        var activeLabels = await _ctx.Labels
            .Find(x => labelCodes.Contains(x.Code) && x.IsActive && !x.IsDeleted)
            .Project(x => new { x.Code, x.Usage, x.DataType })
            .ToListAsync(ct);

        var activeCodes = activeLabels.Select(x => x.Code).ToList();
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

        var invalidUsageCodes = activeLabels
            .Where(x => !LabelUsages.CanUseAsTableTarget(x.Usage))
            .Select(x => x.Code)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var labelsByCode = activeLabels
            .GroupBy(x => x.Code, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        if (invalidUsageCodes.Count > 0)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_LABEL_NOT_ALLOWED,
                new
                {
                    reportId = report.Id,
                    workAssignmentId = report.WorkAssignmentId,
                    dynamicFormTemplateId = report.DynamicFormTemplateId,
                    expectedUsage = LabelUsages.TableTarget,
                    invalidUsageCodes = invalidUsageCodes.Take(20).ToArray()
                });

        if (string.IsNullOrWhiteSpace(report.DynamicFormTemplateId))
            return;

        var form = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == report.DynamicFormTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        var allowedByBlock = BuildAllowedRowLabelsByBlock(form, report);
        if (allowedByBlock.Count == 0)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_LABEL_NOT_ALLOWED,
                new
                {
                    reportId = report.Id,
                    workAssignmentId = report.WorkAssignmentId,
                    dynamicFormTemplateId = report.DynamicFormTemplateId,
                    reason = "Runtime row labels require a block allowedRowLabelCodes allowlist.",
                    labelCodes = labelCodes.Take(20).ToArray()
                });

        foreach (var row in rows)
        {
            if (!allowedByBlock.TryGetValue(row.BlockId, out var allowed) || allowed.Codes.Count == 0)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.WORK_ASSIGNMENT_REPORT_LABEL_NOT_ALLOWED,
                    new
                    {
                        reportId = report.Id,
                        workAssignmentId = report.WorkAssignmentId,
                        dynamicFormTemplateId = report.DynamicFormTemplateId,
                        blockId = row.BlockId,
                        reason = "Runtime row labels require a block allowedRowLabelCodes allowlist.",
                        labelCodes = row.LabelCodes.Take(20).ToArray()
                    });
            }

            var disallowed = row.LabelCodes
                .Where(code => !allowed.Codes.Contains(code))
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

            if (string.IsNullOrWhiteSpace(allowed.DataType))
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.WORK_ASSIGNMENT_REPORT_LABEL_NOT_ALLOWED,
                    new
                    {
                        reportId = report.Id,
                        workAssignmentId = report.WorkAssignmentId,
                        dynamicFormTemplateId = report.DynamicFormTemplateId,
                        blockId = row.BlockId,
                        reason = "Runtime row labels require rowLabelDataType/targetDataType on the Dynamic Excel block.",
                        labelCodes = row.LabelCodes.Take(20).ToArray()
                    });

            var expectedDataType = LabelDataTypes.Normalize(allowed.DataType);
            var invalidTypeCodes = row.LabelCodes
                .Where(code => labelsByCode.TryGetValue(code, out var label)
                               && !string.Equals(
                                   LabelDataTypes.Normalize(label.DataType),
                                   expectedDataType,
                                   StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (invalidTypeCodes.Count > 0)
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.WORK_ASSIGNMENT_REPORT_LABEL_NOT_ALLOWED,
                    new
                    {
                        reportId = report.Id,
                        workAssignmentId = report.WorkAssignmentId,
                        dynamicFormTemplateId = report.DynamicFormTemplateId,
                        blockId = row.BlockId,
                        expectedUsage = LabelUsages.TableTarget,
                        expectedDataType,
                        invalidTypeCodes = invalidTypeCodes.Take(20).ToArray()
                    });
        }
    }

    private async Task<string?> ValidateRuntimeDataPayloadAsync(
        WorkAssignmentReport report,
        IReadOnlyList<object?>? values1D,
        string? fieldValuesJson,
        string? tableValuesJson,
        bool validateRequiredFields,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(report.DynamicFormTemplateId))
        {
            var topLevelOptionSets = await _enumCatalogs.LoadActiveOptionSetsAsync(
                ExtractRuntimeEnumCatalogIds(report.SpecJson),
                ct);
            ValidateTopLevelRuntimeValues(report, values1D ?? Array.Empty<object?>(), topLevelOptionSets);
            DynamicFormNativeTableValues.Validate(null, null, tableValuesJson, validateRequiredFields);
            return fieldValuesJson;
        }

        var form = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == report.DynamicFormTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (form is null)
            throw InvalidReportRuntimePayload(report, "dynamicFormTemplate", "DYNAMIC_FORM_RUNTIME_FORM_NOT_FOUND");

        EnsureRuntimeFormVersionBinding(report, form);

        var enumCatalogIds = ExtractRuntimeEnumCatalogIds(
            report.SpecJson,
            form.FieldsJson,
            form.ExcelBlockJson,
            form.BlocksJson,
            form.TablesJson);
        var optionSets = await LoadBoundEnumOptionSetsAsync(enumCatalogIds, ct);
        var allowedListUnitIds = await LoadNativeListUnitIdsAsync(form, tableValuesJson, ct);
        DynamicFormNativeTableValues.Validate(form, report.DynamicFormSchemaHash, tableValuesJson,
            validateRequiredFields, optionSets, enforceListUuidOnWrite: true,
            previousTableValuesJson: Values1DCompression.ExpandTableValuesJson(report.TableValuesJson, _jsonOptions),
            allowedSystemUnitIds: allowedListUnitIds);

        ValidateTopLevelRuntimeValues(report, values1D ?? Array.Empty<object?>(), optionSets);
        var canonicalFieldValuesJson = await CanonicalizeDynamicFieldRuntimeValuesAsync(
            report,
            form,
            fieldValuesJson,
            validateRequiredFields,
            optionSets,
            ct);
        ValidateDynamicTableRuntimeValues(report, form, tableValuesJson, optionSets);
        return canonicalFieldValuesJson;
    }

    private static void EnsureRuntimeFormVersionBinding(
        WorkAssignmentReport report,
        DynamicFormTemplate form)
    {
        if (!form.IsPublished)
            throw InvalidReportRuntimePayload(report, "dynamicFormTemplate", "DYNAMIC_FORM_RUNTIME_FORM_NOT_PUBLISHED");

        if (!report.DynamicFormVersionNo.HasValue || report.DynamicFormVersionNo.Value != form.VersionNo)
        {
            throw InvalidReportRuntimePayload(
                report,
                "dynamicFormTemplate",
                "DYNAMIC_FORM_RUNTIME_VERSION_MISMATCH");
        }

        var familyId = string.IsNullOrWhiteSpace(form.FamilyId) ? form.Id : form.FamilyId.Trim();
        if (string.IsNullOrWhiteSpace(report.DynamicFormFamilyId) ||
            !string.Equals(report.DynamicFormFamilyId.Trim(), familyId, StringComparison.Ordinal))
        {
            throw InvalidReportRuntimePayload(
                report,
                "dynamicFormTemplate",
                "DYNAMIC_FORM_RUNTIME_FAMILY_MISMATCH");
        }

        if (!string.IsNullOrWhiteSpace(report.DynamicFormTemplateCode) &&
            !string.Equals(report.DynamicFormTemplateCode.Trim(), form.Code?.Trim(), StringComparison.Ordinal))
        {
            throw InvalidReportRuntimePayload(
                report,
                "dynamicFormTemplate",
                "DYNAMIC_FORM_RUNTIME_CODE_MISMATCH");
        }

        DynamicFormPublishedSchemaSnapshot snapshot;
        try
        {
            snapshot = DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(form);
        }
        catch (InvalidOperationException)
        {
            throw InvalidReportRuntimePayload(
                report,
                "dynamicFormTemplate",
                "DYNAMIC_FORM_RUNTIME_PUBLISHED_SCHEMA_INVALID");
        }

        if (string.IsNullOrWhiteSpace(report.DynamicFormSchemaHash) ||
            !string.Equals(report.DynamicFormSchemaHash.Trim(), snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidReportRuntimePayload(
                report,
                "dynamicFormTemplate",
                "DYNAMIC_FORM_RUNTIME_SCHEMA_HASH_MISMATCH");
        }
    }

    private static JsonDocument? ParseRuntimeJsonObjectOrThrow(
        WorkAssignmentReport report,
        string? json,
        string scope)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document;

            document.Dispose();
            throw InvalidReportRuntimePayload(report, scope, "DYNAMIC_FORM_RUNTIME_JSON_OBJECT_REQUIRED");
        }
        catch (JsonException)
        {
            throw InvalidReportRuntimePayload(report, scope, "DYNAMIC_FORM_RUNTIME_JSON_INVALID");
        }
    }

    private static void ValidateTopLevelRuntimeValues(
        WorkAssignmentReport report,
        IReadOnlyList<object?> values1D,
        IReadOnlyDictionary<string, RuntimeEnumOptionSet> optionSets)
    {
        using var specDocument = ParseRuntimeJsonObjectOrThrow(report, report.SpecJson, "specJson");
        var spec = specDocument?.RootElement;
        var inputCells = ResolveReportRuntimeInputCells(report, spec);
        var expectedLength = inputCells.Count;
        if (values1D.Count != expectedLength)
            throw InvalidReportValues(report, expectedLength, values1D.Count);

        for (var index = 0; index < values1D.Count; index++)
        {
            var cell = inputCells[index];
            var r = cell.R;
            var c = cell.C;
            var cellContract = spec.HasValue
                ? ResolveRuntimeCellContract(spec.Value, r, c)
                : new RuntimeCellContract(RuntimeDataTypeNumber, Array.Empty<RuntimeOption>(), null, null);
            var options = ResolveRuntimeOptions(cellContract.Options, cellContract.EnumCatalogId, optionSets);

            if (IsRuntimeValueValid(values1D[index], cellContract.DataType, options, AllowsRawRuntimeCode(cellContract.ValueSourceType)))
                continue;

            throw InvalidReportRuntimeValue(
                report,
                "values1D",
                null,
                null,
                r,
                c,
                cellContract.DataType,
                values1D[index]);
        }
    }

    private static List<RuntimeInputCellRef> ResolveReportRuntimeInputCells(
        WorkAssignmentReport report,
        JsonElement? parsedSpec = null)
    {
        // A Form without a top-level Excel has no Values1D slots. Its zero
        // rectangle is an absent grid, not an Excel range containing cell A1.
        // Field/native Table payloads are validated against the pinned Form below.
        if (!string.IsNullOrWhiteSpace(report.DynamicFormTemplateId) &&
            string.IsNullOrWhiteSpace(report.DynamicExcelTemplateId) &&
            string.IsNullOrWhiteSpace(report.SpecJson) &&
            report.W == 0 && report.H == 0 &&
            report.DataRectR0 == 0 && report.DataRectC0 == 0 &&
            report.DataRectR1 == 0 && report.DataRectC1 == 0)
            return new List<RuntimeInputCellRef>();

        var dataRect = new RuntimeDataRect(report.DataRectR0, report.DataRectC0, report.DataRectR1, report.DataRectC1);
        return parsedSpec.HasValue
            ? ResolveRuntimeInputCells(parsedSpec.Value, dataRect, report.W, report.H)
            : ResolveRuntimeInputCells(default, dataRect, report.W, report.H);
    }

    private static int ResolveTemplateRuntimeInputCellCount(DynamicExcelTemplate template)
    {
        using var specDocument = TryParseRuntimeJsonObject(template.SpecJson);
        var dataRect = new RuntimeDataRect(template.DataRectR0, template.DataRectC0, template.DataRectR1, template.DataRectC1);
        return specDocument is null
            ? ResolveRuntimeInputCells(default, dataRect, template.W, template.H).Count
            : ResolveRuntimeInputCells(specDocument.RootElement, dataRect, template.W, template.H).Count;
    }

    private static void ValidateDynamicFieldRuntimeValues(
        WorkAssignmentReport report,
        DynamicFormTemplate form,
        string? fieldValuesJson,
        bool validateRequiredFields,
        IReadOnlyDictionary<string, RuntimeEnumOptionSet> optionSets)
    {
        var fields = ReadRuntimeFields(form.FieldsJson);
        if (fields.Count == 0)
            return;

        var values = ReadRuntimeFieldValues(report, fieldValuesJson);
        foreach (var field in fields)
        {
            var hasValue = values.TryGetValue(field.Id, out var value);
            if (!hasValue && !string.IsNullOrWhiteSpace(field.Key))
                hasValue = values.TryGetValue(field.Key, out value);

            if (validateRequiredFields && field.Required && (!hasValue || IsBlankRuntimeValue(value)))
            {
                throw InvalidReportRuntimeValue(
                    report,
                    "fieldValuesJson",
                    field.DisplayName,
                    null,
                    null,
                    null,
                    field.DataType,
                    null,
                    "required");
            }

            var options = ResolveRuntimeOptions(field.Options, field.EnumCatalogId, optionSets);
            if (!hasValue || IsRuntimeValueValid(value, field.DataType, options, AllowsRawRuntimeCode(field.ValueSourceType)))
                continue;

            throw InvalidReportRuntimeValue(
                report,
                "fieldValuesJson",
                field.DisplayName,
                null,
                null,
                null,
                field.DataType,
                value);
        }
    }

    private static void ValidateDynamicTableRuntimeValues(
        WorkAssignmentReport report,
        DynamicFormTemplate form,
        string? tableValuesJson,
        IReadOnlyDictionary<string, RuntimeEnumOptionSet> optionSets)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson))
            return;

        var contracts = ReadRuntimeTableBlocks(form);
        try
        {
            using var document = JsonDocument.Parse(tableValuesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_VALUES_OBJECT_REQUIRED");

            if (!TryGetJsonProperty(document.RootElement, "blocks", out var blocks) ||
                blocks.ValueKind != JsonValueKind.Array)
            {
                throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_BLOCKS_ARRAY_REQUIRED");
            }

            var seenBlockIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in blocks.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object)
                    throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_BLOCK_OBJECT_REQUIRED");

                var rawBlockId = ReadJsonString(block, "blockId") ?? ReadJsonString(block, "id");
                if (string.IsNullOrWhiteSpace(rawBlockId))
                    throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_BLOCK_ID_REQUIRED");

                var blockId = NormalizeBlockId(rawBlockId);
                if (!seenBlockIds.Add(blockId))
                {
                    throw RuntimeTableValuesInvalid(
                        report,
                        "DYNAMIC_FORM_TABLE_BLOCK_DUPLICATE",
                        new { blockId });
                }

                if (!contracts.TryGetValue(blockId, out var contract))
                {
                    throw RuntimeTableValuesInvalid(
                        report,
                        "DYNAMIC_FORM_TABLE_BLOCK_NOT_PUBLISHED",
                        new { blockId });
                }

                var contractMode = (ReadJsonString(contract.Block, "tableMode") ?? "FIXED_GRID")
                    .Trim()
                    .ToUpperInvariant();
                var runtimeMode = ReadJsonString(block, "tableMode")?.Trim().ToUpperInvariant();
                if (contractMode is not ("FIXED_GRID" or "APPEND_ROWS" or "APPEND_COLUMNS" or "MATRIX" or "SUMMARY_TEMPLATE"))
                {
                    throw RuntimeTableValuesInvalid(
                        report,
                        "DYNAMIC_FORM_TABLE_MODE_UNSUPPORTED",
                        new { blockId, contractMode });
                }
                if (!string.Equals(runtimeMode, contractMode, StringComparison.Ordinal))
                {
                    throw RuntimeTableValuesInvalid(
                        report,
                        "DYNAMIC_FORM_TABLE_MODE_MISMATCH",
                        new { blockId, runtimeMode, contractMode });
                }

                if (contractMode == "SUMMARY_TEMPLATE")
                {
                    throw RuntimeTableValuesInvalid(
                        report,
                        "DYNAMIC_FORM_SUMMARY_TEMPLATE_INPUT_FORBIDDEN",
                        new { blockId });
                }

                var values = Values1DCompression.ReadBlockObjects(block, _jsonOptions);
                if (values is null)
                {
                    throw RuntimeTableValuesInvalid(
                        report,
                        "DYNAMIC_FORM_TABLE_VALUES_ARRAY_REQUIRED",
                        new { blockId });
                }

                var inputCells = ResolveRuntimeInputCells(contract.Block, contract.DataRect, contract.W, contract.H);
                var expectedLength = inputCells.Count;
                var actualLength = values.Count;
                if (actualLength != expectedLength)
                    throw RuntimeTableValuesInvalid(
                        report,
                        "DYNAMIC_FORM_TABLE_VALUES_LENGTH_MISMATCH",
                        new { blockId, expectedLength, actualLength });

                ValidateRuntimeTableValueSlots(report, blockId, block, contract, inputCells);
                ValidateRuntimeTableProjectionConsistency(report, blockId, block, contractMode, values);

                for (var index = 0; index < values.Count; index++)
                {
                    var cell = inputCells[index];
                    var r = cell.R;
                    var c = cell.C;
                    var cellContract = ResolveRuntimeCellContract(contract.Block, r, c);
                    var options = ResolveRuntimeOptions(cellContract.Options, cellContract.EnumCatalogId, optionSets);
                    var value = values[index];
                    if (!IsRuntimeValueValid(value, cellContract.DataType, options, AllowsRawRuntimeCode(cellContract.ValueSourceType)))
                    {
                        throw InvalidReportRuntimeValue(
                            report,
                            "tableValuesJson",
                            null,
                            blockId,
                            r,
                            c,
                            cellContract.DataType,
                            value);
                    }

                }
            }
        }
        catch (JsonException)
        {
            throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_VALUES_JSON_INVALID");
        }
    }

    private async Task<string?> CanonicalizeDynamicFieldRuntimeValuesAsync(
        WorkAssignmentReport report,
        DynamicFormTemplate form,
        string? fieldValuesJson,
        bool validateRequiredFields,
        IReadOnlyDictionary<string, RuntimeEnumOptionSet> optionSets,
        CancellationToken ct)
    {
        var fields = ReadRuntimeFields(form.FieldsJson);
        var submittedValues = ReadRuntimeFieldValues(report, fieldValuesJson);
        var definitions = new List<ResolvedDynamicFormRuntimeFieldDefinition>(fields.Count);

        foreach (var field in fields)
        {
            var selectedCodes = ReadRuntimeFieldSelectedCodes(field, submittedValues);
            var allowedChoiceCodes = IsRuntimeChoiceFieldType(field.FieldType)
                ? await ResolveRuntimeFieldAllowedChoiceCodesAsync(field, selectedCodes, optionSets, ct)
                : null;

            definitions.Add(new ResolvedDynamicFormRuntimeFieldDefinition(
                field.Id,
                field.FieldType,
                field.Key,
                field.Required,
                allowedChoiceCodes));
        }

        try
        {
            var result = DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
                fieldValuesJson,
                definitions,
                validateRequiredFields);
            await ValidateEvidenceReferencesAsync(report, result.CanonicalFieldValuesJson,
                fields.Where(f => f.FieldType == "evidence").Select(f => f.Id), ct);
            return AttachRuntimeFieldSourceProvenance(
                result.CanonicalFieldValuesJson,
                fields,
                submittedValues,
                form.PublishedSchemaHash);
        }
        catch (DynamicFormRuntimeFieldValidationException ex)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
                new
                {
                    reportId = report.Id,
                    report.WorkId,
                    report.WorkAssignmentId,
                    report.WorkReportPeriodId,
                    report.DynamicFormTemplateId,
                    scope = "fieldValuesJson",
                    fieldId = ex.FieldId,
                    fieldType = ex.FieldType,
                    valueProperty = ex.ValueProperty,
                    reason = ex.Reason
                });
        }
    }

    private static bool IsRuntimeChoiceFieldType(string fieldType)
        => fieldType is "shortText" or "singleSelect" or "multiSelect";

    private static IReadOnlyList<string> ReadRuntimeFieldSelectedCodes(
        RuntimeFieldContract field,
        IReadOnlyDictionary<string, JsonElement> submittedValues)
    {
        if (!submittedValues.TryGetValue(field.Id, out var value) &&
            (string.IsNullOrWhiteSpace(field.Key) || !submittedValues.TryGetValue(field.Key, out value)))
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var code = value.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(code) ? Array.Empty<string>() : new[] { code };
        }

        if (value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyCollection<string>> ResolveRuntimeFieldAllowedChoiceCodesAsync(
        RuntimeFieldContract field,
        IReadOnlyCollection<string> selectedCodes,
        IReadOnlyDictionary<string, RuntimeEnumOptionSet> optionSets,
        CancellationToken ct)
    {
        var sourceType = LabelValueSourceTypes.Normalize(field.ValueSourceType);
        if (sourceType is LabelValueSourceTypes.None or LabelValueSourceTypes.FixedEnum)
            return field.Options.Select(option => option.Code).Distinct(StringComparer.Ordinal).ToArray();

        if (sourceType == LabelValueSourceTypes.EnumCatalog)
        {
            return !string.IsNullOrWhiteSpace(field.EnumCatalogId) &&
                   optionSets.TryGetValue(field.EnumCatalogId.Trim(), out var optionSet)
                ? optionSet.Codes.OrderBy(code => code, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
        }

        if (selectedCodes.Count == 0)
            return Array.Empty<string>();

        if (sourceType == LabelValueSourceTypes.SystemUnit)
        {
            var ids = selectedCodes
                .Where(code => ObjectId.TryParse(code, out _))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (ids.Count == 0)
                return Array.Empty<string>();
            return await _ctx.Units
                .Find(x => ids.Contains(x.Id) && !x.IsDeleted && x.Code != "ROOT")
                .Project(x => x.Id)
                .ToListAsync(ct);
        }

        if (sourceType == LabelValueSourceTypes.SystemLocality)
        {
            var allowed = SystemLocalityCatalog.Official.Rows.Select(x => x.Code).ToHashSet(StringComparer.Ordinal);
            return selectedCodes.Where(allowed.Contains).Distinct(StringComparer.Ordinal).ToArray();
        }

        if (sourceType == LabelValueSourceTypes.SystemUser)
        {
            var ids = selectedCodes
                .Where(code => ObjectId.TryParse(code, out _))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (ids.Count == 0)
                return Array.Empty<string>();
            return await _ctx.Users
                .Find(x => ids.Contains(x.Id) && !x.IsDeleted)
                .Project(x => x.Id)
                .ToListAsync(ct);
        }

        if (sourceType == LabelValueSourceTypes.SystemPosition)
        {
            var codes = selectedCodes.Distinct(StringComparer.Ordinal).ToList();
            return await _ctx.Positions
                .Find(x => codes.Contains(x.Code) && !x.IsDeleted)
                .Project(x => x.Code)
                .ToListAsync(ct);
        }

        if (sourceType == LabelValueSourceTypes.SystemUnitType)
        {
            var codes = selectedCodes.Distinct(StringComparer.Ordinal).ToList();
            return await _ctx.UnitTypes
                .Find(x => codes.Contains(x.Code) && !x.IsDeleted)
                .Project(x => x.Code)
                .ToListAsync(ct);
        }

        return Array.Empty<string>();
    }

    private static string AttachRuntimeFieldSourceProvenance(
        string canonicalFieldValuesJson,
        IReadOnlyCollection<RuntimeFieldContract> fields,
        IReadOnlyDictionary<string, JsonElement> submittedValues,
        string? schemaHash)
    {
        var root = JsonNode.Parse(canonicalFieldValuesJson) as JsonObject ?? new JsonObject();
        var values = root["values"] as JsonObject ?? root;
        var provenance = new JsonObject();

        foreach (var field in fields.Where(field => IsRuntimeChoiceFieldType(field.FieldType)))
        {
            if (!values.ContainsKey(field.Id))
                continue;

            var selectedCodes = ReadRuntimeFieldSelectedCodes(field, submittedValues);
            provenance[field.Id] = new JsonObject
            {
                ["sourceType"] = LabelValueSourceTypes.Normalize(field.ValueSourceType),
                ["sourceId"] = field.EnumCatalogId,
                ["sourceVersion"] = schemaHash,
                ["codes"] = new JsonArray(selectedCodes.Select(code => JsonValue.Create(code)).ToArray())
            };
        }

        if (provenance.Count == 0)
            return canonicalFieldValuesJson;

        if (ReferenceEquals(values, root))
        {
            var envelope = new JsonObject
            {
                ["sourceProvenance"] = provenance,
                ["values"] = root.DeepClone()
            };
            return envelope.ToJsonString(_jsonOptions);
        }

        root["sourceProvenance"] = provenance;
        return root.ToJsonString(_jsonOptions);
    }

    private static void ValidateRuntimeTableValueSlots(
        WorkAssignmentReport report,
        string blockId,
        JsonElement block,
        RuntimeTableBlockContract contract,
        IReadOnlyList<RuntimeInputCellRef> inputCells)
    {
        if (!TryGetJsonProperty(block, "valueSlots", out var slots) || slots.ValueKind != JsonValueKind.Array)
        {
            throw RuntimeTableValuesInvalid(
                report,
                "DYNAMIC_FORM_TABLE_VALUE_SLOTS_REQUIRED",
                new { blockId });
        }

        if (slots.GetArrayLength() != inputCells.Count)
        {
            throw RuntimeTableValuesInvalid(
                report,
                "DYNAMIC_FORM_TABLE_VALUE_SLOTS_LENGTH_MISMATCH",
                new { blockId, expectedLength = inputCells.Count, actualLength = slots.GetArrayLength() });
        }

        var seenIndexes = new HashSet<int>();
        foreach (var slot in slots.EnumerateArray())
        {
            if (slot.ValueKind != JsonValueKind.Object)
            {
                throw RuntimeTableValuesInvalid(
                    report,
                    "DYNAMIC_FORM_TABLE_VALUE_SLOT_OBJECT_REQUIRED",
                    new { blockId });
            }

            var index = ReadJsonInt(slot, "index");
            if (!index.HasValue || index.Value < 0 || index.Value >= inputCells.Count || !seenIndexes.Add(index.Value))
            {
                throw RuntimeTableValuesInvalid(
                    report,
                    "DYNAMIC_FORM_TABLE_VALUE_SLOT_INDEX_INVALID",
                    new { blockId, index });
            }

            var expected = inputCells[index.Value];
            var expectedRowKey = $"row_{expected.R - contract.DataRect.R0 + 1}";
            var expectedColumnKey = $"col_{expected.C - contract.DataRect.C0 + 1}";
            var rowKey = ReadJsonString(slot, "rowKey");
            var columnKey = ReadJsonString(slot, "columnKey");
            if (!string.Equals(rowKey, expectedRowKey, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(columnKey, expectedColumnKey, StringComparison.OrdinalIgnoreCase))
            {
                throw RuntimeTableValuesInvalid(
                    report,
                    "DYNAMIC_FORM_TABLE_VALUE_SLOT_COORDINATE_MISMATCH",
                    new { blockId, index, rowKey, columnKey, expectedRowKey, expectedColumnKey });
            }

            ValidateOptionalRuntimeSlotCoordinate(report, blockId, slot, "rowOffset", expected.R - contract.DataRect.R0, index.Value);
            ValidateOptionalRuntimeSlotCoordinate(report, blockId, slot, "columnOffset", expected.C - contract.DataRect.C0, index.Value);
            ValidateOptionalRuntimeSlotCoordinate(report, blockId, slot, "row", expected.R, index.Value);
            ValidateOptionalRuntimeSlotCoordinate(report, blockId, slot, "column", expected.C, index.Value);
        }
    }

    private static void ValidateOptionalRuntimeSlotCoordinate(
        WorkAssignmentReport report,
        string blockId,
        JsonElement slot,
        string propertyName,
        int expected,
        int index)
    {
        var actual = ReadJsonInt(slot, propertyName);
        if (actual.HasValue && actual.Value != expected)
        {
            throw RuntimeTableValuesInvalid(
                report,
                "DYNAMIC_FORM_TABLE_VALUE_SLOT_COORDINATE_MISMATCH",
                new { blockId, index, propertyName, expected, actual });
        }
    }

    private static void ValidateRuntimeTableProjectionConsistency(
        WorkAssignmentReport report,
        string blockId,
        JsonElement block,
        string tableMode,
        IReadOnlyList<object?> values)
    {
        EnsureRuntimeTableModeProjectionRequired(report, blockId, block, tableMode);
        var canonicalValues = JsonSerializer.SerializeToNode(values, _jsonOptions) as JsonArray ?? new JsonArray();
        var slotIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (TryGetJsonProperty(block, "valueSlots", out var slots) && slots.ValueKind == JsonValueKind.Array)
        {
            foreach (var slot in slots.EnumerateArray())
            {
                var index = ReadJsonInt(slot, "index");
                var rowKey = ReadJsonString(slot, "rowKey");
                var columnKey = ReadJsonString(slot, "columnKey");
                if (index.HasValue && !string.IsNullOrWhiteSpace(rowKey) && !string.IsNullOrWhiteSpace(columnKey))
                    slotIndexes[RuntimeTableSlotKey(rowKey, columnKey)] = index.Value;
            }
        }

        if (tableMode == "APPEND_ROWS" && TryGetJsonProperty(block, "rows", out var rows))
        {
            if (rows.ValueKind != JsonValueKind.Array)
                throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_ROWS_ARRAY_REQUIRED", new { blockId });

            var seenRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenRowInstanceIds = new HashSet<string>(StringComparer.Ordinal);
            var rowIndex = 0;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_ROW_OBJECT_REQUIRED", new { blockId, rowIndex });

                var rowInstanceId = ReadJsonString(row, "rowInstanceId");
                if (string.IsNullOrWhiteSpace(rowInstanceId) || !seenRowInstanceIds.Add(rowInstanceId.Trim()))
                    throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_ROW_INSTANCE_ID_INVALID", new { blockId, rowIndex, rowInstanceId });

                var rowOrder = ReadJsonInt(row, "rowOrder") ?? 0;
                var rowKey = $"row_{Math.Max(1, rowOrder)}";
                if (rowOrder <= 0 || !seenRows.Add(rowKey))
                    throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_ROW_ORDER_INVALID", new { blockId, rowIndex, rowOrder });
                if (!slotIndexes.Keys.Any(key => key.StartsWith(rowKey + "\u001f", StringComparison.OrdinalIgnoreCase)))
                    throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_ROW_SLOT_NOT_FOUND", new { blockId, rowIndex, rowOrder });

                if (!TryGetJsonProperty(row, "cells", out var cells) || cells.ValueKind != JsonValueKind.Object)
                    throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_ROW_CELLS_OBJECT_REQUIRED", new { blockId, rowIndex });
                foreach (var cell in cells.EnumerateObject())
                    ValidateRuntimeProjectionValue(report, blockId, "rows", rowKey, cell.Name, cell.Value, canonicalValues, slotIndexes);

                foreach (var property in row.EnumerateObject())
                {
                    if (RuntimeAppendRowMetadataKeys.Contains(property.Name))
                        continue;
                    ValidateRuntimeProjectionValue(report, blockId, "rows", rowKey, property.Name, property.Value, canonicalValues, slotIndexes);
                }

                rowIndex++;
            }
        }

        if (tableMode == "APPEND_COLUMNS" && TryGetJsonProperty(block, "columns", out var columns))
        {
            if (columns.ValueKind != JsonValueKind.Array)
                throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_COLUMNS_ARRAY_REQUIRED", new { blockId });

            var seenColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenColumnInstanceIds = new HashSet<string>(StringComparer.Ordinal);
            var columnIndex = 0;
            foreach (var column in columns.EnumerateArray())
            {
                if (column.ValueKind != JsonValueKind.Object)
                    throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_COLUMN_OBJECT_REQUIRED", new { blockId, columnIndex });

                var columnInstanceId = ReadJsonString(column, "columnInstanceId");
                if (string.IsNullOrWhiteSpace(columnInstanceId) || !seenColumnInstanceIds.Add(columnInstanceId.Trim()))
                    throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_COLUMN_INSTANCE_ID_INVALID", new { blockId, columnIndex, columnInstanceId });

                var columnOrder = ReadJsonInt(column, "columnOrder") ?? 0;
                var columnKey = $"col_{Math.Max(1, columnOrder)}";
                if (columnOrder <= 0 || !seenColumns.Add(columnKey))
                    throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_COLUMN_ORDER_INVALID", new { blockId, columnIndex, columnOrder });
                if (!slotIndexes.Keys.Any(key => key.EndsWith("\u001f" + columnKey, StringComparison.OrdinalIgnoreCase)))
                    throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_COLUMN_SLOT_NOT_FOUND", new { blockId, columnIndex, columnOrder });
                if (!TryGetJsonProperty(column, "cells", out var cells) || cells.ValueKind != JsonValueKind.Object)
                    throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_COLUMN_CELLS_OBJECT_REQUIRED", new { blockId, columnIndex });
                foreach (var cell in cells.EnumerateObject())
                    ValidateRuntimeProjectionValue(report, blockId, "columns", cell.Name, columnKey, cell.Value, canonicalValues, slotIndexes);

                columnIndex++;
            }
        }

        if (tableMode == "MATRIX" && TryGetJsonProperty(block, "cells", out var matrixCells))
        {
            if (matrixCells.ValueKind != JsonValueKind.Array)
                throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_MATRIX_CELLS_ARRAY_REQUIRED", new { blockId });

            var seenCells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cellIndex = 0;
            foreach (var cell in matrixCells.EnumerateArray())
            {
                if (cell.ValueKind != JsonValueKind.Object)
                    throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_MATRIX_CELL_OBJECT_REQUIRED", new { blockId, cellIndex });

                var rowKey = ReadJsonString(cell, "rowKey");
                var columnKey = ReadJsonString(cell, "columnKey");
                var rowAxisKey = ReadJsonString(cell, "rowAxisKey");
                var columnAxisKey = ReadJsonString(cell, "columnAxisKey");
                var metricKey = ReadJsonString(cell, "metricKey");
                if (string.IsNullOrWhiteSpace(rowKey) || string.IsNullOrWhiteSpace(columnKey) ||
                    string.IsNullOrWhiteSpace(rowAxisKey) || string.IsNullOrWhiteSpace(columnAxisKey) ||
                    string.IsNullOrWhiteSpace(metricKey) ||
                    !TryGetJsonProperty(cell, "value", out var projectedValue))
                {
                    throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_MATRIX_CELL_COORDINATE_REQUIRED", new { blockId, cellIndex });
                }
                if (!seenCells.Add(RuntimeTableSlotKey(rowKey, columnKey)))
                    throw RuntimeTableValuesInvalid(report, "DYNAMIC_FORM_TABLE_MATRIX_CELL_DUPLICATE", new { blockId, cellIndex, rowKey, columnKey });
                ValidateRuntimeProjectionValue(report, blockId, "cells", rowKey, columnKey, projectedValue, canonicalValues, slotIndexes);
                cellIndex++;
            }
        }
    }

    private static void EnsureRuntimeTableModeProjectionRequired(
        WorkAssignmentReport report,
        string blockId,
        JsonElement block,
        string tableMode)
    {
        var requiredProperty = tableMode switch
        {
            "APPEND_ROWS" => "rows",
            "APPEND_COLUMNS" => "columns",
            "MATRIX" => "cells",
            "FIXED_GRID" => null,
            _ => throw RuntimeTableValuesInvalid(
                report,
                "DYNAMIC_FORM_TABLE_MODE_UNSUPPORTED",
                new { blockId, tableMode })
        };

        if (requiredProperty is not null && !TryGetJsonProperty(block, requiredProperty, out _))
        {
            throw RuntimeTableValuesInvalid(
                report,
                $"DYNAMIC_FORM_TABLE_{requiredProperty.ToUpperInvariant()}_REQUIRED",
                new { blockId, tableMode });
        }

        if (tableMode == "FIXED_GRID" &&
            (TryGetJsonProperty(block, "rows", out _) ||
             TryGetJsonProperty(block, "columns", out _) ||
             TryGetJsonProperty(block, "cells", out _)))
        {
            throw RuntimeTableValuesInvalid(
                report,
                "DYNAMIC_FORM_FIXED_GRID_PROJECTION_FORBIDDEN",
                new { blockId, tableMode });
        }
    }

    private static void ValidateRuntimeProjectionValue(
        WorkAssignmentReport report,
        string blockId,
        string projection,
        string rowKey,
        string columnKey,
        JsonElement projectedValue,
        JsonArray canonicalValues,
        IReadOnlyDictionary<string, int> slotIndexes)
    {
        if (!slotIndexes.TryGetValue(RuntimeTableSlotKey(rowKey, columnKey), out var index) ||
            index < 0 || index >= canonicalValues.Count)
        {
            throw RuntimeTableValuesInvalid(
                report,
                "DYNAMIC_FORM_TABLE_PROJECTION_SLOT_NOT_FOUND",
                new { blockId, projection, rowKey, columnKey });
        }

        var projectedNode = projectedValue.ValueKind == JsonValueKind.Null
            ? null
            : JsonNode.Parse(projectedValue.GetRawText());
        if (!JsonNode.DeepEquals(canonicalValues[index], projectedNode))
        {
            throw RuntimeTableValuesInvalid(
                report,
                "DYNAMIC_FORM_TABLE_PROJECTION_VALUE_MISMATCH",
                new { blockId, projection, rowKey, columnKey, index });
        }
    }

    private static string RuntimeTableSlotKey(string rowKey, string columnKey)
        => $"{rowKey.Trim()}\u001f{columnKey.Trim()}";

    private static readonly HashSet<string> RuntimeAppendRowMetadataKeys = new(
        new[] { "cells", "rowKey", "rowInstanceId", "rowOrder", "rowIndex", "joinKey", "id", "rowLabelCodes" },
        StringComparer.OrdinalIgnoreCase);

    private static List<RuntimeFieldContract> ReadRuntimeFields(string? fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
            return new List<RuntimeFieldContract>();

        try
        {
            using var document = JsonDocument.Parse(fieldsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return new List<RuntimeFieldContract>();

            var result = new List<RuntimeFieldContract>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var id = ReadJsonString(item, "id") ?? ReadJsonString(item, "key");
                var key = ReadJsonString(item, "key") ?? id ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var displayName =
                    ReadJsonString(item, "name") ??
                    ReadJsonString(item, "displayName") ??
                    ReadJsonString(item, "label") ??
                    key;

                var fieldType = ReadJsonString(item, "type")?.Trim() ?? string.Empty;
                result.Add(new RuntimeFieldContract(
                    id.Trim(),
                    key.Trim(),
                    string.IsNullOrWhiteSpace(displayName) ? key.Trim() : displayName.Trim(),
                    fieldType,
                    NormalizeRuntimeFieldDataType(fieldType),
                    ReadJsonBool(item, "required") == true,
                    ReadRuntimeOptions(item),
                    ReadRuntimeEnumCatalogId(item),
                    ReadRuntimeValueSourceType(item)));
            }

            return result;
        }
        catch (JsonException)
        {
            return new List<RuntimeFieldContract>();
        }
    }

    private static Dictionary<string, JsonElement> ReadRuntimeFieldValues(
        WorkAssignmentReport report,
        string? fieldValuesJson)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(fieldValuesJson))
            return result;

        try
        {
            using var document = JsonDocument.Parse(fieldValuesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw InvalidReportRuntimePayload(report, "fieldValuesJson", "fieldValuesJson must be a JSON object.");

            var root = document.RootElement;
            if (TryGetJsonProperty(root, "values", out var valuesRoot))
                root = valuesRoot;

            if (root.ValueKind != JsonValueKind.Object)
                throw InvalidReportRuntimePayload(report, "fieldValuesJson.values", "values must be a JSON object.");

            foreach (var property in root.EnumerateObject())
                result[property.Name] = property.Value.Clone();

            return result;
        }
        catch (JsonException ex)
        {
            throw InvalidReportRuntimePayload(report, "fieldValuesJson", ex.Message);
        }
    }

    private static RuntimeOption[] ReadRuntimeOptions(JsonElement owner)
    {
        if (!TryGetJsonProperty(owner, "options", out var options) || options.ValueKind != JsonValueKind.Array)
            return Array.Empty<RuntimeOption>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<RuntimeOption>();
        foreach (var item in options.EnumerateArray())
        {
            var code = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString()?.Trim(),
                JsonValueKind.Object => ReadJsonString(item, "code")
                                        ?? ReadJsonString(item, "value")
                                        ?? ReadJsonString(item, "id"),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(code) || !seen.Add(code))
                continue;

            var label = item.ValueKind == JsonValueKind.Object
                ? ReadJsonString(item, "label")
                  ?? ReadJsonString(item, "name")
                  ?? ReadJsonString(item, "text")
                  ?? code
                : code;

            rows.Add(new RuntimeOption(code, string.IsNullOrWhiteSpace(label) ? code : label));
        }

        return rows.ToArray();
    }

    private static string? ReadRuntimeEnumCatalogId(JsonElement owner)
    {
        if (!TryGetJsonProperty(owner, "valueSource", out var source) || source.ValueKind != JsonValueKind.Object)
            return null;

        var sourceType = ReadRuntimeValueSourceType(owner);
        if (sourceType != LabelValueSourceTypes.EnumCatalog)
            return null;

        return ReadJsonString(source, "catalogId") ??
               ReadJsonString(source, "valueSourceCatalogId") ??
               ReadJsonString(source, "enumCatalogId");
    }

    private static string? ReadRuntimeValueSourceType(JsonElement owner)
    {
        if (!TryGetJsonProperty(owner, "valueSource", out var source) || source.ValueKind != JsonValueKind.Object)
            return null;

        return LabelValueSourceTypes.Normalize(
            ReadJsonString(source, "sourceType") ??
            ReadJsonString(source, "type") ??
            ReadJsonString(source, "valueSourceType"));
    }

    private static Dictionary<string, RuntimeTableBlockContract> ReadRuntimeTableBlocks(
        DynamicFormTemplate form)
    {
        var result = new Dictionary<string, RuntimeTableBlockContract>(StringComparer.Ordinal);
        foreach (var block in ReadRuntimeBlockElements(form.BlocksJson)
                     .Concat(ReadRuntimeBlockElements(form.ExcelBlockJson)))
        {
            var contract = ParseRuntimeTableBlock(block);
            if (contract is not null && !result.ContainsKey(contract.BlockId))
                result[contract.BlockId] = contract;
        }

        return result;
    }

    private static List<JsonElement> ReadRuntimeBlockElements(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<JsonElement>();

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                return document.RootElement
                    .EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .Select(item => item.Clone())
                    .ToList();
            }

            return document.RootElement.ValueKind == JsonValueKind.Object
                ? new List<JsonElement> { document.RootElement.Clone() }
                : new List<JsonElement>();
        }
        catch (JsonException)
        {
            return new List<JsonElement>();
        }
    }

    private static List<JsonElement> ReadRuntimeTableValueBlockElements(string? tableValuesJson)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson))
            return new List<JsonElement>();

        try
        {
            using var document = JsonDocument.Parse(tableValuesJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                TryGetJsonProperty(document.RootElement, "blocks", out var blocks) &&
                blocks.ValueKind == JsonValueKind.Array)
            {
                return blocks
                    .EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .Select(item => item.Clone())
                    .ToList();
            }

            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement
                    .EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .Select(item => item.Clone())
                    .ToList()
                : new List<JsonElement>();
        }
        catch (JsonException)
        {
            return new List<JsonElement>();
        }
    }

    private static RuntimeTableBlockContract? ResolveRuntimeTopLevelBlockShape(string? tableValuesJson, int valuesLength)
    {
        if (valuesLength <= 0)
            return null;

        foreach (var block in ReadRuntimeTableValueBlockElements(tableValuesJson))
        {
            var contract = ParseRuntimeTableBlock(block);
            if (contract is null)
                continue;

            var inputLength = ResolveRuntimeInputCells(contract.Block, contract.DataRect, contract.W, contract.H).Count;
            if (inputLength == valuesLength || ReadJsonArrayLength(block, "values1D") == valuesLength)
                return contract;
        }

        return null;
    }

    private async Task<RuntimeTableBlockContract?> ResolveRuntimeTopLevelBlockShapeAsync(
        WorkAssignmentReport report,
        string? tableValuesJson,
        int valuesLength,
        CancellationToken ct)
    {
        if (valuesLength <= 0)
            return null;

        var valueBlocks = ReadRuntimeTableValueBlockElements(tableValuesJson);
        Dictionary<string, RuntimeTableBlockContract>? templateContracts = null;
        string? topLevelBlockId = null;
        if (!string.IsNullOrWhiteSpace(report.DynamicFormTemplateId))
        {
            var dynamicFormTemplateId = report.DynamicFormTemplateId.Trim();
            var form = await _ctx.DynamicFormTemplates
                .Find(x => x.Id == dynamicFormTemplateId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (form is not null)
            {
                templateContracts = ReadRuntimeTableBlocks(form);
                topLevelBlockId = ResolveTopLevelBlockId(form);
            }
        }

        if (templateContracts is not null &&
            !string.IsNullOrWhiteSpace(topLevelBlockId) &&
            templateContracts.TryGetValue(NormalizeBlockId(topLevelBlockId), out var topLevelContract))
        {
            return topLevelContract;
        }

        if (valueBlocks.Count == 0)
            return null;

        foreach (var block in valueBlocks)
        {
            var blockId = NormalizeBlockId(ReadJsonString(block, "blockId") ?? ReadJsonString(block, "id"));
            var blockValuesLength = ReadJsonArrayLength(block, "values1D");
            if (blockValuesLength.HasValue && blockValuesLength.Value != valuesLength)
                continue;

            if (templateContracts is not null &&
                templateContracts.TryGetValue(blockId, out var templateContract))
            {
                var templateInputLength = ResolveRuntimeInputCells(
                    templateContract.Block,
                    templateContract.DataRect,
                    templateContract.W,
                    templateContract.H).Count;
                if (templateInputLength == valuesLength || blockValuesLength == valuesLength)
                    return templateContract;
            }

            var payloadContract = ParseRuntimeTableBlock(block);
            if (payloadContract is null)
                continue;

            var payloadInputLength = ResolveRuntimeInputCells(
                payloadContract.Block,
                payloadContract.DataRect,
                payloadContract.W,
                payloadContract.H).Count;
            if (payloadInputLength == valuesLength || blockValuesLength == valuesLength)
                return payloadContract;
        }

        return null;
    }

    private static void ApplyRuntimeTopLevelShape(WorkAssignmentReport report, RuntimeTableBlockContract block)
    {
        report.W = block.W;
        report.H = block.H;
        report.DataRectR0 = block.DataRect.R0;
        report.DataRectC0 = block.DataRect.C0;
        report.DataRectR1 = block.DataRect.R1;
        report.DataRectC1 = block.DataRect.C1;
    }

    private static int? ReadJsonArrayLength(JsonElement element, string name)
    {
        if (string.Equals(name, "values1D", StringComparison.OrdinalIgnoreCase))
            return Values1DCompression.ReadBlockValuesLength(element);

        return TryGetJsonProperty(element, name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.GetArrayLength()
            : null;
    }

    private static RuntimeTableBlockContract? ParseRuntimeTableBlock(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object)
            return null;

        var dataRect = ReadRuntimeDataRect(block);
        var w = ReadJsonInt(block, "w") ?? ReadJsonInt(block, "W") ?? RuntimeRectWidth(dataRect);
        var h = ReadJsonInt(block, "h") ?? ReadJsonInt(block, "H") ?? RuntimeRectHeight(dataRect);
        if (w <= 0 || h <= 0)
            return null;

        if (RuntimeRectWidth(dataRect) <= 0 || RuntimeRectHeight(dataRect) <= 0)
            dataRect = new RuntimeDataRect(0, 0, Math.Max(0, h - 1), Math.Max(0, w - 1));

        return new RuntimeTableBlockContract(
            NormalizeBlockId(ReadJsonString(block, "blockId") ?? ReadJsonString(block, "id")),
            w,
            h,
            dataRect,
            block.Clone());
    }

    private static RuntimeDataRect ReadRuntimeDataRect(JsonElement block)
    {
        var node = TryGetJsonProperty(block, "dataRect", out var dataRect) &&
                   dataRect.ValueKind == JsonValueKind.Object
            ? dataRect
            : block;

        var r0 = ReadJsonInt(node, "r0") ?? ReadJsonInt(node, "R0") ?? 0;
        var c0 = ReadJsonInt(node, "c0") ?? ReadJsonInt(node, "C0") ?? 0;
        var r1 = ReadJsonInt(node, "r1") ?? ReadJsonInt(node, "R1") ?? -1;
        var c1 = ReadJsonInt(node, "c1") ?? ReadJsonInt(node, "C1") ?? -1;
        return new RuntimeDataRect(r0, c0, r1, c1);
    }

    private static int RuntimeRectWidth(RuntimeDataRect rect)
        => rect.C1 >= rect.C0 ? rect.C1 - rect.C0 + 1 : 0;

    private static int RuntimeRectHeight(RuntimeDataRect rect)
        => rect.R1 >= rect.R0 ? rect.R1 - rect.R0 + 1 : 0;

    private static List<RuntimeInputCellRef> ResolveRuntimeInputCells(
        JsonElement specOrBlock,
        RuntimeDataRect dataRect,
        int width,
        int height)
    {
        var rect = RuntimeRectWidth(dataRect) > 0 && RuntimeRectHeight(dataRect) > 0
            ? dataRect
            : new RuntimeDataRect(0, 0, Math.Max(0, height - 1), Math.Max(0, width - 1));
        var specialRanges = ReadRuntimeSpecialRanges(specOrBlock, rect);
        var specialMask = BuildRuntimeSpecialCellMask(rect, specialRanges);
        var totalCells = RuntimeRectWidth(rect) * RuntimeRectHeight(rect);
        var cells = new List<RuntimeInputCellRef>(Math.Max(0, totalCells - (specialMask?.MaskedCount ?? 0)));
        var index = 0;

        for (var r = rect.R0; r <= rect.R1; r++)
        {
            for (var c = rect.C0; c <= rect.C1; c++)
            {
                if (IsRuntimeSpecialCell(specialMask, r, c))
                    continue;

                cells.Add(new RuntimeInputCellRef(index++, r, c));
            }
        }

        return cells;
    }

    private static List<RuntimeDataRect> ReadRuntimeSpecialRanges(JsonElement owner, RuntimeDataRect dataRect)
    {
        var ranges = new List<RuntimeDataRect>();
        if (owner.ValueKind != JsonValueKind.Object ||
            !TryGetJsonProperty(owner, "specialRanges", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return ranges;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var role = NormalizeRuntimeSpecialRole(ReadJsonString(item, "role") ?? ReadJsonString(item, "kind") ?? ReadJsonString(item, "type"));
            if (string.IsNullOrWhiteSpace(role))
                continue;

            var r0 = ReadJsonInt(item, "r0") ?? ReadJsonInt(item, "R0");
            var c0 = ReadJsonInt(item, "c0") ?? ReadJsonInt(item, "C0");
            var r1 = ReadJsonInt(item, "r1") ?? ReadJsonInt(item, "R1");
            var c1 = ReadJsonInt(item, "c1") ?? ReadJsonInt(item, "C1");
            if (!r0.HasValue || !c0.HasValue || !r1.HasValue || !c1.HasValue)
                continue;
            if (r1.Value < r0.Value || c1.Value < c0.Value)
                continue;

            var range = new RuntimeDataRect(r0.Value, c0.Value, r1.Value, c1.Value);
            if (!RuntimeRectContains(dataRect, range))
                continue;

            ranges.Add(range);
        }

        return ranges
            .OrderBy(range => range.R0)
            .ThenBy(range => range.C0)
            .ThenBy(range => range.R1)
            .ThenBy(range => range.C1)
            .ToList();
    }

    private static RuntimeSpecialCellMask? BuildRuntimeSpecialCellMask(
        RuntimeDataRect dataRect,
        IReadOnlyCollection<RuntimeDataRect> specialRanges)
    {
        if (specialRanges.Count == 0)
            return null;

        var width = RuntimeRectWidth(dataRect);
        var height = RuntimeRectHeight(dataRect);
        if (width <= 0 || height <= 0)
            return null;

        var flags = new bool[width * height];
        var masked = 0;
        foreach (var range in specialRanges)
        {
            var r0 = Math.Max(dataRect.R0, range.R0);
            var c0 = Math.Max(dataRect.C0, range.C0);
            var r1 = Math.Min(dataRect.R1, range.R1);
            var c1 = Math.Min(dataRect.C1, range.C1);
            if (r1 < r0 || c1 < c0)
                continue;

            for (var r = r0; r <= r1; r++)
            {
                var offset = (r - dataRect.R0) * width + (c0 - dataRect.C0);
                for (var c = c0; c <= c1; c++)
                {
                    if (!flags[offset])
                    {
                        flags[offset] = true;
                        masked++;
                    }
                    offset++;
                }
            }
        }

        return new RuntimeSpecialCellMask(dataRect, width, flags, masked);
    }

    private static bool IsRuntimeSpecialCell(RuntimeSpecialCellMask? mask, int r, int c)
    {
        if (mask is null)
            return false;
        if (!RuntimeRectContains(mask.DataRect, r, c))
            return false;

        return mask.Flags[(r - mask.DataRect.R0) * mask.Width + (c - mask.DataRect.C0)];
    }

    private static string? NormalizeRuntimeSpecialRole(string? value)
    {
        var role = value?.Trim().ToUpperInvariant();
        if (role == "FORMULAR")
            role = "FORMULA";
        if (role == "HEADER")
            role = "TITLE";
        if (role is "STYLE" or "EMPTY" or "EMPTY_INPUT")
            role = "BLANK";
        return role is "FORMULA" or "TITLE" or "BLANK" ? role : null;
    }

    private static bool RuntimeRectContains(RuntimeDataRect rect, int r, int c)
        => r >= rect.R0 && r <= rect.R1 && c >= rect.C0 && c <= rect.C1;

    private static bool RuntimeRectContains(RuntimeDataRect outer, RuntimeDataRect inner)
        => inner.R0 >= outer.R0 && inner.C0 >= outer.C0 && inner.R1 <= outer.R1 && inner.C1 <= outer.C1;

    private static bool RuntimeRectsOverlap(RuntimeDataRect a, RuntimeDataRect b)
        => a.R0 <= b.R1 && a.R1 >= b.R0 && a.C0 <= b.C1 && a.C1 >= b.C0;

    private static RuntimeCellContract ResolveRuntimeCellContract(JsonElement specOrBlock, int rowIndex, int columnIndex)
    {
        var kind = (ReadJsonString(specOrBlock, "kind") ??
                    ReadJsonString(specOrBlock, "excelSpecKind") ??
                    ReadJsonString(specOrBlock, "sourceKind"))
            ?.Trim()
            .ToUpperInvariant();
        var dataType = NormalizeRuntimeDataType(
            ReadJsonString(specOrBlock, "defaultDataType") ??
            ReadJsonString(specOrBlock, "dataType"));
        var options = ReadRuntimeOptions(specOrBlock, "defaultOptions");
        var enumCatalogId = ReadRuntimeEnumCatalogId(specOrBlock);
        var valueSourceType = ReadRuntimeValueSourceType(specOrBlock);

        if (!TryGetJsonProperty(specOrBlock, "dataTypeOverrides", out var overrides) ||
            overrides.ValueKind != JsonValueKind.Array)
        {
            return new RuntimeCellContract(dataType, options, enumCatalogId, valueSourceType);
        }

        foreach (var item in overrides.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var scope = ReadJsonString(item, "scope")?.Trim().ToUpperInvariant();
            var applies =
                kind == "TOP" && scope == "COLUMN" && ReadJsonInt(item, "index") == columnIndex ||
                kind == "LEFT" && scope == "ROW" && ReadJsonInt(item, "index") == rowIndex ||
                kind == "MATRIX" && scope == "RANGE" && RuntimeRangeContains(item, rowIndex, columnIndex);

            if (applies)
            {
                dataType = NormalizeRuntimeDataType(ReadJsonString(item, "dataType"));
                options = ReadRuntimeOptions(item, "options");
                enumCatalogId = ReadRuntimeEnumCatalogId(item);
                valueSourceType = ReadRuntimeValueSourceType(item);
            }
        }

        return new RuntimeCellContract(dataType, options, enumCatalogId, valueSourceType);
    }

    private static RuntimeOption[] ReadRuntimeOptions(JsonElement owner, string propertyName)
    {
        if (!TryGetJsonProperty(owner, propertyName, out var options) || options.ValueKind != JsonValueKind.Array)
            return Array.Empty<RuntimeOption>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<RuntimeOption>();
        foreach (var item in options.EnumerateArray())
        {
            var code = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString()?.Trim(),
                JsonValueKind.Object => ReadJsonString(item, "code")
                                        ?? ReadJsonString(item, "value")
                                        ?? ReadJsonString(item, "id"),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(code) || !seen.Add(code))
                continue;

            var label = item.ValueKind == JsonValueKind.Object
                ? ReadJsonString(item, "label")
                  ?? ReadJsonString(item, "name")
                  ?? ReadJsonString(item, "text")
                  ?? code
                : code;

            rows.Add(new RuntimeOption(code, string.IsNullOrWhiteSpace(label) ? code : label));
        }

        return rows.ToArray();
    }

    private static RuntimeOption[] ResolveRuntimeOptions(
        RuntimeOption[] inlineOptions,
        string? enumCatalogId,
        IReadOnlyDictionary<string, RuntimeEnumOptionSet> optionSets)
    {
        if (string.IsNullOrWhiteSpace(enumCatalogId))
            return inlineOptions;

        return optionSets.TryGetValue(enumCatalogId.Trim(), out var optionSet)
            ? optionSet.Codes.Select(code => new RuntimeOption(code, code)).ToArray()
            : Array.Empty<RuntimeOption>();
    }

    private static IReadOnlyList<string> ExtractRuntimeEnumCatalogIds(params string?[] jsonValues)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var json in jsonValues)
        {
            if (string.IsNullOrWhiteSpace(json))
                continue;

            try
            {
                using var document = JsonDocument.Parse(json);
                CollectRuntimeEnumCatalogIds(document.RootElement, result);
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return result.ToList();
    }

    private static void CollectRuntimeEnumCatalogIds(JsonElement element, ISet<string> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var catalogId = ReadRuntimeEnumCatalogId(element);
            if (!string.IsNullOrWhiteSpace(catalogId))
                result.Add(catalogId.Trim());

            foreach (var property in element.EnumerateObject())
                CollectRuntimeEnumCatalogIds(property.Value, result);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectRuntimeEnumCatalogIds(item, result);
        }
    }

    private static bool RuntimeRangeContains(JsonElement item, int rowIndex, int columnIndex)
    {
        var r0 = ReadJsonInt(item, "r0") ?? ReadJsonInt(item, "R0");
        var c0 = ReadJsonInt(item, "c0") ?? ReadJsonInt(item, "C0");
        var r1 = ReadJsonInt(item, "r1") ?? ReadJsonInt(item, "R1");
        var c1 = ReadJsonInt(item, "c1") ?? ReadJsonInt(item, "C1");
        return r0.HasValue && c0.HasValue && r1.HasValue && c1.HasValue &&
               rowIndex >= r0.Value && rowIndex <= r1.Value &&
               columnIndex >= c0.Value && columnIndex <= c1.Value;
    }

    private static string NormalizeRuntimeFieldDataType(string? fieldType)
        => fieldType?.Trim() switch
        {
            "number" => RuntimeDataTypeNumber,
            "date" => RuntimeDataTypeDate,
            "fullDate" => RuntimeDataTypeFullDate,
            "boolean" => RuntimeDataTypeBoolean,
            "longText" => RuntimeDataTypeLongText,
            "richText" => RuntimeDataTypeLongText,
            "stringList" => RuntimeDataTypeStringList,
            "singleSelect" => RuntimeDataTypeShortText,
            "multiSelect" => RuntimeDataTypeShortTextList,
            _ => RuntimeDataTypeShortText
        };

    private static string NormalizeRuntimeDataType(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return RuntimeDataTypeNumber;

        return normalized switch
        {
            RuntimeDataTypeNumber or "DECIMAL" or "NUMERIC" => RuntimeDataTypeNumber,
            RuntimeDataTypeDate => RuntimeDataTypeDate,
            RuntimeDataTypeFullDate or "FULLDATE" or "STRICT_DATE" => RuntimeDataTypeFullDate,
            RuntimeDataTypeBoolean or "BOOL" => RuntimeDataTypeBoolean,
            RuntimeDataTypeShortText or "TEXT" or "STRING" or "SHORTTEXT" => RuntimeDataTypeShortText,
            RuntimeDataTypeLongText or "LONGTEXT" => RuntimeDataTypeLongText,
            RuntimeDataTypeStringList or "STRINGLIST" => RuntimeDataTypeStringList,
            RuntimeDataTypeShortTextList or "MULTI_SELECT" or "MULTISELECT" => RuntimeDataTypeShortTextList,
            RuntimeDataTypeIgnore or "IGNORED" or "SKIP" => RuntimeDataTypeIgnore,
            _ => RuntimeDataTypeUnsupported
        };
    }

    private static bool IsRuntimeValueValid(
        object? value,
        string dataType,
        IReadOnlyCollection<RuntimeOption>? options = null,
        bool allowRawExternalCode = false)
    {
        if (dataType == RuntimeDataTypeIgnore)
            return true;

        if (IsBlankRuntimeValue(value))
            return true;

        if (allowRawExternalCode &&
            (dataType == RuntimeDataTypeShortText || dataType == RuntimeDataTypeShortTextList))
        {
            return IsRuntimeExternalCodeValueValid(value, dataType);
        }

        if (value is JsonElement element)
            return IsJsonRuntimeValueValid(element, dataType, options);

        return dataType switch
        {
            RuntimeDataTypeNumber => IsRuntimeNumber(value),
            RuntimeDataTypeDate => value is string text && IsRuntimeDateTextValid(text, requireFullDate: false),
            RuntimeDataTypeFullDate => value is string text && IsRuntimeDateTextValid(text, requireFullDate: true),
            RuntimeDataTypeBoolean => value is bool,
            RuntimeDataTypeShortText => IsRuntimeShortTextValueValid(value, options),
            RuntimeDataTypeShortTextList => IsRuntimeShortTextListValueValid(value, options),
            RuntimeDataTypeLongText => value is string,
            RuntimeDataTypeStringList => IsRuntimeFreeStringListValueValid(value),
            _ => false
        };
    }

    private static bool AllowsRawRuntimeCode(string? valueSourceType)
    {
        var sourceType = LabelValueSourceTypes.Normalize(valueSourceType);
        return LabelValueSourceTypes.UsesCatalog(sourceType) &&
               sourceType != LabelValueSourceTypes.EnumCatalog;
    }

    private static bool IsRuntimeExternalCodeValueValid(object? value, string dataType)
    {
        if (dataType == RuntimeDataTypeShortText)
        {
            if (value is string text)
                return !string.IsNullOrWhiteSpace(text);
            if (value is JsonElement element)
                return element.ValueKind == JsonValueKind.String &&
                       !string.IsNullOrWhiteSpace(element.GetString());
            return false;
        }

        if (dataType == RuntimeDataTypeShortTextList)
        {
            if (value is IEnumerable<string> list)
            {
                var values = list.ToArray();
                return values.All(item => !string.IsNullOrWhiteSpace(item)) &&
                       values.Distinct(StringComparer.Ordinal).Count() == values.Length;
            }
            if (value is JsonElement element)
            {
                if (element.ValueKind != JsonValueKind.Array)
                    return false;
                var values = element.EnumerateArray().ToArray();
                return values.All(item =>
                           item.ValueKind == JsonValueKind.String &&
                           !string.IsNullOrWhiteSpace(item.GetString())) &&
                       values.Select(item => item.GetString()!.Trim())
                           .Distinct(StringComparer.Ordinal)
                           .Count() == values.Length;
            }
            return false;
        }

        return false;
    }

    private static bool IsJsonRuntimeValueValid(
        JsonElement value,
        string dataType,
        IReadOnlyCollection<RuntimeOption>? options = null)
    {
        if (IsBlankJsonElement(value))
            return true;

        return dataType switch
        {
            RuntimeDataTypeNumber => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            RuntimeDataTypeDate => value.ValueKind == JsonValueKind.String &&
                                   IsRuntimeDateTextValid(value.GetString(), requireFullDate: false),
            RuntimeDataTypeFullDate => value.ValueKind == JsonValueKind.String &&
                                       IsRuntimeDateTextValid(value.GetString(), requireFullDate: true),
            RuntimeDataTypeBoolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            RuntimeDataTypeShortText => IsRuntimeShortTextJsonValid(value, options),
            RuntimeDataTypeShortTextList => IsRuntimeShortTextListJsonValid(value, options),
            RuntimeDataTypeLongText => value.ValueKind == JsonValueKind.String,
            RuntimeDataTypeStringList => IsRuntimeFreeStringListJsonValid(value),
            _ => false
        };
    }

    private static bool IsRuntimeShortTextValueValid(
        object? value,
        IReadOnlyCollection<RuntimeOption>? options)
    {
        if (value is string text)
            return RuntimeOptionContains(options, text);

        if (value is JsonElement element)
            return IsRuntimeShortTextJsonValid(element, options);

        return false;
    }

    private static bool IsRuntimeShortTextJsonValid(
        JsonElement value,
        IReadOnlyCollection<RuntimeOption>? options)
    {
        return value.ValueKind == JsonValueKind.String &&
               RuntimeOptionContains(options, value.GetString());
    }

    private static bool IsRuntimeShortTextListValueValid(
        object? value,
        IReadOnlyCollection<RuntimeOption>? options)
    {
        if (value is IEnumerable<string> list)
        {
            var values = list.ToArray();
            return values.All(item => RuntimeOptionContains(options, item)) &&
                   values.Distinct(StringComparer.Ordinal).Count() == values.Length;
        }

        if (value is JsonElement element)
            return IsRuntimeShortTextListJsonValid(element, options);

        return false;
    }

    private static bool IsRuntimeShortTextListJsonValid(
        JsonElement value,
        IReadOnlyCollection<RuntimeOption>? options)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return false;

        var values = value.EnumerateArray().ToArray();
        return values.All(item =>
                   item.ValueKind == JsonValueKind.String &&
                   RuntimeOptionContains(options, item.GetString())) &&
               values.Select(item => item.GetString()!.Trim())
                   .Distinct(StringComparer.Ordinal)
                   .Count() == values.Length;
    }

    private static bool IsRuntimeFreeStringListValueValid(object? value)
    {
        if (value is IEnumerable<string> list)
        {
            var values = list.ToArray();
            return values.All(item => !string.IsNullOrWhiteSpace(item)) &&
                   values.Distinct(StringComparer.Ordinal).Count() == values.Length;
        }
        if (value is JsonElement element)
            return IsRuntimeFreeStringListJsonValid(element);
        return false;
    }

    private static bool IsRuntimeFreeStringListJsonValid(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return false;

        var values = value.EnumerateArray().ToArray();
        return values.All(item =>
                   item.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(item.GetString())) &&
               values.Select(item => item.GetString()!.Trim())
                   .Distinct(StringComparer.Ordinal)
                   .Count() == values.Length;
    }

    private static bool RuntimeOptionContains(
        IReadOnlyCollection<RuntimeOption>? options,
        string? value)
    {
        if (options is null || options.Count == 0 || string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        return options.Any(option =>
            string.Equals(option.Code, normalized, StringComparison.Ordinal));
    }

    private static bool IsBlankRuntimeValue(object? value)
        => value switch
        {
            null => true,
            string text => string.IsNullOrWhiteSpace(text),
            JsonElement element => IsBlankJsonElement(element),
            _ => false
        };

    private static bool IsBlankJsonElement(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => true,
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => !value.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(item.GetString())),
            _ => false
        };

    private static bool IsRuntimeNumber(object? value)
        => value switch
        {
            byte or sbyte or short or ushort or int or uint or long or ulong or decimal => true,
            float f => float.IsFinite(f),
            double d => double.IsFinite(d),
            JsonElement element => IsJsonRuntimeValueValid(element, RuntimeDataTypeNumber),
            _ => false
        };

    private static bool IsRuntimeNumberText(string? text)
        => !string.IsNullOrWhiteSpace(text) &&
           decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out _);

    private static bool IsRuntimeZeroOrOneNumber(object? value)
    {
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var n))
                return n == 0m || n == 1m;
            return false;
        }

        if (!IsRuntimeNumber(value))
            return false;

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) &&
               (parsed == 0m || parsed == 1m);
    }

    private static bool TryParseRuntimeBooleanText(string? text, out bool value)
    {
        var normalized = text?.Trim().ToLowerInvariant();
        if (normalized is "true" or "1" or "yes" or "y" or "co" or "có")
        {
            value = true;
            return true;
        }

        if (normalized is "false" or "0" or "no" or "n" or "khong" or "không")
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }

    private static bool IsRuntimeDateTextValid(string? text, bool requireFullDate)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var trimmed = text.Trim();
        var full = RuntimeFullDateRegex.Match(trimmed);
        if (full.Success)
        {
            var day = int.Parse(full.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(full.Groups[2].Value, CultureInfo.InvariantCulture);
            var year = int.Parse(full.Groups[3].Value, CultureInfo.InvariantCulture);
            return IsRuntimeDatePartValid(year, month, day);
        }

        if (requireFullDate)
            return false;

        var monthOnly = RuntimeMonthDateRegex.Match(trimmed);
        if (monthOnly.Success)
        {
            var month = int.Parse(monthOnly.Groups[1].Value, CultureInfo.InvariantCulture);
            var year = int.Parse(monthOnly.Groups[2].Value, CultureInfo.InvariantCulture);
            return IsRuntimeYearValid(year) && month is >= 1 and <= 12;
        }

        var yearOnly = RuntimeYearDateRegex.Match(trimmed);
        return yearOnly.Success &&
               IsRuntimeYearValid(int.Parse(yearOnly.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private static bool IsRuntimeDatePartValid(int year, int month, int day)
        => IsRuntimeYearValid(year) &&
           month is >= 1 and <= 12 &&
           day >= 1 &&
           day <= DateTime.DaysInMonth(year, month);

    private static bool IsRuntimeYearValid(int year)
        => year is >= 1 and <= 9999;

    private static JsonDocument? TryParseRuntimeJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document;

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<object?> DeserializeRawValues1D(string? json)
        => Values1DCompression.DeserializeObjects(json, _jsonOptions);

    private static object? ToRuntimeObject(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => value.Clone()
        };

    private static AppException InvalidReportRuntimeValue(
        WorkAssignmentReport report,
        string scope,
        string? fieldName,
        string? blockId,
        int? rowIndex,
        int? columnIndex,
        string dataType,
        object? value,
        string? reason = null)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
            new
            {
                reportId = report.Id,
                report.WorkId,
                report.WorkAssignmentId,
                report.WorkReportPeriodId,
                report.DynamicFormTemplateId,
                scope,
                fieldName,
                blockId,
                rowIndex,
                columnIndex,
                cellRef = rowIndex.HasValue && columnIndex.HasValue ? RuntimeCellRef(rowIndex.Value, columnIndex.Value) : null,
                dataType,
                expectedFormat = RuntimeDataTypeFormat(dataType),
                reason,
                value = RuntimeDebugValue(value)
            });

    private static AppException InvalidReportRuntimePayload(
        WorkAssignmentReport report,
        string scope,
        string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
            new
            {
                reportId = report.Id,
                report.WorkId,
                report.WorkAssignmentId,
                report.WorkReportPeriodId,
                report.DynamicFormTemplateId,
                scope,
                reason
            });

    private static AppException RuntimeTableValuesInvalid(
        WorkAssignmentReport report,
        string reason,
        object? extra = null)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_TABLE_VALUES_JSON_INVALID,
            new
            {
                reportId = report.Id,
                report.WorkId,
                report.WorkAssignmentId,
                report.WorkReportPeriodId,
                report.DynamicFormTemplateId,
                reason,
                extra
            });

    private static string RuntimeDataTypeFormat(string dataType)
        => dataType switch
        {
            RuntimeDataTypeDate => "dd/MM/yyyy, MM/yyyy hoặc yyyy",
            RuntimeDataTypeFullDate => "dd/MM/yyyy",
            RuntimeDataTypeNumber => "number",
            RuntimeDataTypeBoolean => "true/false",
            RuntimeDataTypeShortText => "mã enum SHORT_TEXT",
            RuntimeDataTypeShortTextList => "mảng mã enum MULTI_SELECT",
            RuntimeDataTypeLongText => "string",
            RuntimeDataTypeStringList => "string[]",
            RuntimeDataTypeIgnore => "ignore",
            _ => dataType
        };

    private static object? RuntimeDebugValue(object? value)
    {
        if (value is not JsonElement element)
            return value;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }

    private static string RuntimeCellRef(int rowIndex, int columnIndex)
        => $"{RuntimeColumnRef(columnIndex)}{rowIndex + 1}";

    private static string RuntimeColumnRef(int columnIndex)
    {
        var text = string.Empty;
        var n = Math.Max(0, columnIndex) + 1;
        while (n > 0)
        {
            var mod = (n - 1) % 26;
            text = (char)('A' + mod) + text;
            n = (n - 1) / 26;
        }

        return text;
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

    private static Dictionary<string, AllowedRowLabelConfig> BuildAllowedRowLabelsByBlock(
        DynamicFormTemplate? form,
        WorkAssignmentReport report)
    {
        var result = new Dictionary<string, AllowedRowLabelConfig>(StringComparer.Ordinal);
        if (form is null)
            return result;

        AddAllowedRowLabelsFromJson(form.BlocksJson, result, report);
        AddAllowedRowLabelsFromJson(form.ExcelBlockJson, result, report);
        return result;
    }

    private static void AddAllowedRowLabelsFromJson(
        string? json,
        Dictionary<string, AllowedRowLabelConfig> target,
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
        Dictionary<string, AllowedRowLabelConfig> target,
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

        var dataType = ResolveAllowedRowLabelDataType(block, report);
        if (!target.TryGetValue(blockId, out var config))
        {
            config = new AllowedRowLabelConfig(new HashSet<string>(StringComparer.Ordinal), dataType);
            target[blockId] = config;
        }
        else if (string.IsNullOrWhiteSpace(config.DataType) && !string.IsNullOrWhiteSpace(dataType))
        {
            target[blockId] = config with { DataType = dataType };
        }

        foreach (var code in codes)
            target[blockId].Codes.Add(code);
    }

    private static string? ResolveAllowedRowLabelDataType(JsonElement block, WorkAssignmentReport report)
    {
        var explicitType = ReadJsonString(block, "rowLabelDataType")
                           ?? ReadJsonString(block, "rowLabelTargetDataType")
                           ?? ReadJsonString(block, "targetDataType")
                           ?? ReadJsonString(block, "labelDataType")
                           ?? ReadJsonString(block, "defaultDataType")
                           ?? ReadJsonString(block, "dataType");

        if (!string.IsNullOrWhiteSpace(explicitType))
            return LabelDataTypes.Normalize(explicitType);

        return LabelDataTypes.Number;
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

    private static string? ExtractPrimaryDynamicExcelTemplateId(string? excelBlockJson, string? blocksJson)
        => ExtractDynamicExcelTemplateId(excelBlockJson) ?? ExtractFirstDynamicExcelTemplateId(blocksJson);

    private async Task<HashSet<string>> ResolveReportDynamicExcelTemplateIdsAsync(
        WorkAssignmentReport report,
        CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        AddNormalizedId(ids, report.DynamicExcelTemplateId);

        if (string.IsNullOrWhiteSpace(report.DynamicFormTemplateId))
            return ids;

        var form = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == report.DynamicFormTemplateId && !x.IsDeleted)
            .Project(x => new DynamicFormTemplate
            {
                Id = x.Id,
                ExcelBlockDynamicExcelTemplateId = x.ExcelBlockDynamicExcelTemplateId,
                ExcelBlockJson = x.ExcelBlockJson,
                BlocksJson = x.BlocksJson
            })
            .FirstOrDefaultAsync(ct);

        if (form is null)
            return ids;

        AddNormalizedId(ids, form.ExcelBlockDynamicExcelTemplateId);
        AddDynamicExcelTemplateIdsFromJson(ids, form.ExcelBlockJson);
        AddDynamicExcelTemplateIdsFromJson(ids, form.BlocksJson);
        return ids;
    }

    private static void AddDynamicExcelTemplateIdsFromJson(HashSet<string> ids, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in document.RootElement.EnumerateArray())
                    AddNormalizedId(ids, ExtractDynamicExcelTemplateId(item));
                return;
            }

            AddNormalizedId(ids, ExtractDynamicExcelTemplateId(document.RootElement));
        }
        catch (JsonException)
        {
            // Form JSON is validated on write. Ignore malformed legacy payload here and fail by whitelist miss.
        }
    }

    private static void AddNormalizedId(HashSet<string> ids, string? value)
    {
        var normalized = NormalizeOptionalTextOrNull(value);
        if (!string.IsNullOrWhiteSpace(normalized))
            ids.Add(normalized);
    }

    private static string? ExtractDynamicExcelTemplateId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return ExtractDynamicExcelTemplateId(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractFirstDynamicExcelTemplateId(string? blocksJson)
    {
        if (string.IsNullOrWhiteSpace(blocksJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(blocksJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in document.RootElement.EnumerateArray())
            {
                var id = ExtractDynamicExcelTemplateId(item);
                if (!string.IsNullOrWhiteSpace(id))
                    return id;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? ExtractDynamicExcelTemplateId(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        return NormalizeOptionalTextOrNull(ReadJsonString(element, "dynamicExcelTemplateId"))
               ?? NormalizeOptionalTextOrNull(ReadJsonString(element, "DynamicExcelTemplateId"))
               ?? NormalizeOptionalTextOrNull(ReadJsonString(element, "excelBlockDynamicExcelTemplateId"))
               ?? NormalizeOptionalTextOrNull(ReadJsonString(element, "ExcelBlockDynamicExcelTemplateId"));
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

    private async Task<DynamicFlowReportWritablePayload> PrepareDynamicFlowReportWritePayloadAsync(
        WorkAssignment? assignment,
        WorkAssignmentReport report,
        string? nextFieldValuesJson,
        string? nextTableValuesJson,
        string actorUserId,
        bool validateRequired,
        CancellationToken ct)
    {
        var permissions = await ResolveDynamicFlowReportPermissionsAsync(
            assignment,
            report,
            actorUserId,
            isAfterSubmit: IsDynamicFlowReportAfterSubmit(report),
            ct);
        if (permissions is null)
            return new DynamicFlowReportWritablePayload(nextFieldValuesJson, nextTableValuesJson);

        var writablePayload = DynamicFlowReportPermissionEnforcer.PreserveUnreadableValues(
            permissions,
            report.FieldValuesJson,
            nextFieldValuesJson,
            report.TableValuesJson,
            nextTableValuesJson);

        var violations = DynamicFlowReportPermissionEnforcer.FindWriteViolations(
            permissions,
            report.FieldValuesJson,
            writablePayload.FieldValuesJson,
            report.TableValuesJson,
            writablePayload.TableValuesJson);
        if (violations.Count > 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
                new
                {
                    reportId = report.Id,
                    workAssignmentId = report.WorkAssignmentId,
                    actorUserId,
                    reason = "DYNAMIC_FLOW_REPORT_WRITE_FORBIDDEN",
                    violations = violations.Take(20).Select(x => new
                    {
                        x.TargetKind,
                        x.TargetKey,
                        x.SourcePolicyId,
                        x.Reason
                    }).ToList()
                });
        }

        if (!validateRequired)
            return writablePayload;

        var requiredViolations = DynamicFlowReportPermissionEnforcer.FindRequiredViolations(
            permissions,
            writablePayload.FieldValuesJson,
            writablePayload.TableValuesJson);
        if (requiredViolations.Count == 0)
            return writablePayload;

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
            new
            {
                reportId = report.Id,
                workAssignmentId = report.WorkAssignmentId,
                actorUserId,
                reason = "DYNAMIC_FLOW_REPORT_REQUIRED_VALUE_MISSING",
                violations = requiredViolations.Take(20).Select(x => new
                {
                    x.TargetKind,
                    x.TargetKey,
                    x.SourcePolicyId,
                    x.Reason
                }).ToList()
            });
    }

    private async Task<DynamicFlowPolicyEvaluationResult?> ResolveDynamicFlowReportPermissionsAsync(
        WorkAssignment? assignment,
        WorkAssignmentReport report,
        string actorUserId,
        bool isAfterSubmit,
        CancellationToken ct)
    {
        if (assignment is null ||
            !DynamicFlowBranchVisibility.IsFlowAssignment(assignment))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(assignment.FlowTemplateId) ||
            !assignment.FlowTemplateVersionNo.HasValue)
        {
            // Historical pre-policy Flow assignments remain readable/writable
            // under their frozen legacy contract. P7 mapping itself never
            // uses this compatibility path: its runtime resolver requires the
            // exact locked version/instance/step/Form pins before policy
            // evaluation or source hydration.
            return null;
        }

        var flowTemplateId = assignment.FlowTemplateId.Trim();
        var flowTemplateVersionNo = assignment.FlowTemplateVersionNo.Value;
        var version = await _ctx.DynamicFlowTemplateVersions
            .Find(x => x.TemplateId == flowTemplateId &&
                       x.VersionNo == flowTemplateVersionNo &&
                       x.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                       !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (version is null)
        {
            return null;
        }
        if (!DynamicFlowRuntimeEligibilityPolicy.IsP7MappingCatalog(
                version.CatalogVersion,
                version.CatalogSemanticHash) &&
            string.Equals(
                version.ExecutionEligibility,
                DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase,
                StringComparison.Ordinal))
        {
            // Historical catalog snapshots that were frozen as non-executable
            // pre-P7 definitions still need their original report lifecycle
            // to reach the explicit runtime barrier. They cannot enter P7
            // mapping because the exact mapping resolver rejects their pins.
            return null;
        }

        var actorRole = ResolveDirectDynamicFlowActorRole(assignment, report, actorUserId);
        if (actorRole is null)
        {
            if (await HasReviewReportReadAccessAsync(report, actorUserId, ct))
            {
                actorRole = "REVIEWER";
            }
            else if (await HasAggregateAncestorReadAccessAsync(assignment, actorUserId, ct))
            {
                actorRole = "FINALIZER";
            }
            else
            {
                throw AppExceptionFactory.Forbidden(
                    AppErrorCode.WORK_ASSIGNMENT_REPORT_ACCESS_FORBIDDEN,
                    new
                    {
                        reportId = report.Id,
                        workAssignmentId = report.WorkAssignmentId,
                        actorUserId,
                        reason = "DYNAMIC_FLOW_REPORT_ACTOR_ROLE_UNRESOLVED"
                    });
            }
        }

        var evaluated = _dynamicFlowPolicyEvaluator.Evaluate(
            version.PayloadJson,
            new DynamicFlowPolicyEvaluationContext
            {
                StepId = assignment.FlowStepId,
                StepCode = assignment.FlowStepCode,
                ActorRole = actorRole,
                IsAfterSubmit = isAfterSubmit
            });
        if (evaluated.DenyAllFields &&
            evaluated.DenyAllTableColumns &&
            !DynamicFlowRuntimeEligibilityPolicy.IsP7MappingCatalog(
                version.CatalogVersion,
                version.CatalogSemanticHash))
        {
            // Exact pre-P7 runtime snapshots without policy metadata keep
            // their frozen legacy behavior. A v1.4 mapping snapshot never
            // receives this bypass, and mapping preview/apply use the stricter
            // exact-runtime policy resolver above.
            return null;
        }

        return evaluated;
    }

    internal static string? ResolveDirectDynamicFlowActorRole(
        WorkAssignment assignment,
        WorkAssignmentReport report,
        string actorUserId)
    {
        if (assignment is null || report is null || string.IsNullOrWhiteSpace(actorUserId))
            return null;

        var actorId = actorUserId.Trim();
        var isBranchAssignee = string.Equals(report.AssigneeUserId, actorId, StringComparison.Ordinal) ||
                               (assignment.Assignees ?? new List<UserRef>())
                               .Any(user => string.Equals(user.UserId, actorId, StringComparison.Ordinal));
        if (isBranchAssignee)
            return NormalizeDynamicFlowActorRole(assignment.FlowRole) ?? "ASSIGNEE";

        if (WorkAssignmentCurrentAuthority.IsReviewer(assignment, actorId))
            return "ISSUER";

        if ((assignment.LeaderWatcherUserIds ?? new List<string>()).Contains(actorId, StringComparer.Ordinal) ||
            (assignment.LeaderWatchers ?? new List<UserRef>())
            .Any(user => string.Equals(user.UserId, actorId, StringComparison.Ordinal)))
        {
            return "COORDINATOR";
        }

        return null;
    }

    private static string? NormalizeDynamicFlowActorRole(string? role)
    {
        var normalized = role?.Trim().ToUpperInvariant();
        return normalized is "ISSUER" or "ASSIGNEE" or "COORDINATOR" or "REVIEWER" or "FINALIZER"
            ? normalized
            : null;
    }

    internal static bool IsDynamicFlowReportAfterSubmit(WorkAssignmentReport report)
        => report.Status != WorkAssignmentReportStatus.Draft ||
           report.SubmittedAtUtc.HasValue ||
           !string.IsNullOrWhiteSpace(report.SubmittedByUserId) ||
           report.ReturnedAtUtc.HasValue ||
           report.ApprovedAtUtc.HasValue ||
           report.AutoApprovedAtUtc.HasValue;

    private async Task<WorkAssignmentReportResponse> MapToResponseAsync(
        WorkAssignmentReport x,
        WorkReportPeriod? period,
        string actorUserId,
        CancellationToken ct)
    {
        DynamicExcelTemplate? template = null;
        if (!string.IsNullOrWhiteSpace(x.DynamicExcelTemplateId))
        {
            template = await _ctx.DynamicExcelTemplates
                .Find(t => t.Id == x.DynamicExcelTemplateId && !t.IsDeleted)
                .Project(DynamicExcelTemplateMetadataProjection)
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
        }

        var assignment = await _ctx.WorkAssignments
            .Find(a => a.Id == x.WorkAssignmentId && !a.IsDeleted)
            .FirstOrDefaultAsync(ct);
        var completedDatePolicy = ResolveReportCompletedDatePolicy(assignment, x, period, DateTime.UtcNow);
        var isHistoricalData = x.IsHistoricalData ||
                               period?.IsHistoricalData == true ||
                               IsBackfillCompletedDatePolicy(completedDatePolicy);
        var payload = await _payloadReader.LoadReportPayloadAsync(x, ct);
        var dynamicFlowPermissions = await ResolveDynamicFlowReportPermissionsAsync(
            assignment,
            x,
            actorUserId,
            isAfterSubmit: IsDynamicFlowReportAfterSubmit(x),
            ct);
        var readablePayload = dynamicFlowPermissions is null
            ? new DynamicFlowReportReadablePayload(
                payload.Values1DJson,
                payload.FieldValuesJson,
                payload.TableValuesJson)
            : DynamicFlowReportPermissionEnforcer.ApplyReadRestrictions(
                dynamicFlowPermissions,
                payload.Values1DJson,
                payload.FieldValuesJson,
                payload.TableValuesJson,
                x.DynamicExcelTemplateId);
        var actorOwnsReport = string.Equals(x.AssigneeUserId, actorUserId, StringComparison.Ordinal);
        var mutationScopeOpen = assignment is not null &&
                                await IsReportMutationScopeOpenAsync(assignment, ct, x.WorkReportPeriodId);
        var canMutate = actorOwnsReport && x.IsActive && mutationScopeOpen;
        var lifecycleProjectionPending = (x.LifecycleProjectionOutbox ?? new List<WorkReportLifecycleProjectionOutboxEntry>())
            .Any(entry => string.Equals(
                entry.State,
                WorkReportLifecycleProjectionOutboxStates.Pending,
                StringComparison.Ordinal));
        DynamicFlowMappingApplyReceipt? dynamicFlowMappingReceipt = null;
        if (!string.IsNullOrWhiteSpace(x.DynamicFlowMappingReceiptId))
        {
            dynamicFlowMappingReceipt = await _ctx.DynamicFlowMappingApplyReceipts
                .Find(receipt =>
                    receipt.Id == x.DynamicFlowMappingReceiptId &&
                    receipt.TargetReportId == x.Id)
                .FirstOrDefaultAsync(ct);
        }

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
            StartedDate = x.StartedDate ?? period?.StartedDate,
            CompletedDate = x.CompletedDate ?? period?.CompletedDate,
            CanEditCompletedDate = completedDatePolicy.CanEditCompletedDate,
            RequiresCompletedDate = completedDatePolicy.RequiresCompletedDate,
            CompletedDateMin = completedDatePolicy.CompletedDateMin,
            CompletedDateMax = completedDatePolicy.CompletedDateMax,
            CompletedDatePolicyReason = completedDatePolicy.Reason,
            IsHistoricalData = isHistoricalData,
            HistoricalDataApproved = x.HistoricalDataApproved || period?.HistoricalDataApproved == true,
            HistoricalDataApprovedAtUtc = x.HistoricalDataApprovedAtUtc ?? period?.HistoricalDataApprovedAtUtc,
            HistoricalDataApprovedByUserId = x.HistoricalDataApprovedByUserId ?? period?.HistoricalDataApprovedByUserId,
            PeriodStart = x.PeriodStart,
            PeriodEnd = x.PeriodEnd,
            DueAtUtc = x.DueAtUtc,
            Status = x.Status,
            PeriodStatus = period?.Status,

            TemplateSnapshotJson = template is null
                ? string.Empty
                : JsonSerializer.Serialize(BuildTemplateSnapshot(template), _jsonOptions),
            ScheduleSnapshotJson = x.ScheduleSnapshotJson,

            DynamicExcelTemplateId = x.DynamicExcelTemplateId,
            DynamicExcelTemplateCode = x.DynamicExcelTemplateCode,
            DynamicExcelTemplateName = x.DynamicExcelTemplateName,
            DynamicFormTemplateId = x.DynamicFormTemplateId ?? period?.DynamicFormTemplateId,
            DynamicFormTemplateCode = x.DynamicFormTemplateCode ?? period?.DynamicFormTemplateCode,
            DynamicFormTemplateName = x.DynamicFormTemplateName ?? period?.DynamicFormTemplateName,
            DynamicFormFamilyId = x.DynamicFormFamilyId ?? period?.DynamicFormFamilyId,
            DynamicFormVersionNo = x.DynamicFormVersionNo ?? period?.DynamicFormVersionNo,
            DynamicFormSchemaHash = x.DynamicFormSchemaHash ?? period?.DynamicFormSchemaHash,
            SpecJson = string.IsNullOrWhiteSpace(x.SpecJson) ? template?.SpecJson ?? string.Empty : x.SpecJson,

            DataRectR0 = x.DataRectR0,
            DataRectC0 = x.DataRectC0,
            DataRectR1 = x.DataRectR1,
            DataRectC1 = x.DataRectC1,
            W = x.W,
            H = x.H,

            Values1DJson = readablePayload.Values1DJson ?? EmptyValues1DJson,
            FieldValuesJson = readablePayload.FieldValuesJson,
            TableValuesJson = readablePayload.TableValuesJson,
            PayloadRevision = x.PayloadRevision,
            LifecycleRevision = x.LifecycleRevision,
            LifecycleCommitState = lifecycleProjectionPending
                ? WorkReportLifecycleCommitStates.CommittedPendingProjection
                : WorkReportLifecycleCommitStates.Committed,
            LifecycleProjectionPending = lifecycleProjectionPending,
            CanEditPayload = canMutate && x.Status == WorkAssignmentReportStatus.Draft,
            AggregateSubmissionPreviewRequired = dynamicFlowPermissions is null && !string.IsNullOrWhiteSpace(x.DynamicFormTemplateId)
                && await AggregateHostIntegration.RequiresSubmissionPreviewAsync(_ctx, x, ct),
            AggregateEditHint = dynamicFlowPermissions is null && !string.IsNullOrWhiteSpace(x.DynamicFormTemplateId)
                ? await AggregateHostIntegration.ReadEditHintAsync(_ctx, x, actorUserId, ct) : null,
            CanSubmit = canMutate && x.Status == WorkAssignmentReportStatus.Draft,
            CanWithdraw = canMutate &&
                          (x.Status == WorkAssignmentReportStatus.Submitted ||
                           WorkAssignmentAutoApprovalState.CanReporterWithdraw(x)),
            PayloadHash = x.PayloadHash,
            PayloadSizeBytes = x.PayloadSizeBytes,
            PayloadStatus = x.PayloadStatus,
            PayloadUpdatedAtUtc = x.PayloadUpdatedAtUtc,
            DynamicFlowMappingApplyState =
                dynamicFlowMappingReceipt?.State,
            DynamicFlowMappingReceiptId =
                dynamicFlowMappingReceipt?.Id,
            DynamicFlowMappingCommandId =
                dynamicFlowMappingReceipt?.CommandId,
            DynamicFlowMappingResultSemanticHash =
                dynamicFlowMappingReceipt?.ResultSemanticHash,
            DynamicFlowPermissions = dynamicFlowPermissions,
            DataOrigin = WorkReportDataOrigin.Normalize(x.DataOrigin),
            CumulativeContributionMode = WorkReportCumulativeContributionMode.Normalize(x.CumulativeContributionMode),
            CumulativeContributionPolicyJson = x.CumulativeContributionPolicyJson,
            SummarySourceJson = DynamicFlowReportPermissionEnforcer.RedactSummarySourceValues(
                payload.SummarySourceJson),
            AggregateSourceReportIds = x.AggregateSourceReportIds ?? new List<string>(),
            AggregateSourceAssignmentIds = x.AggregateSourceAssignmentIds ?? new List<string>(),
            AggregateSourceUpdatedAtUtc = x.AggregateSourceUpdatedAtUtc,
            AggregateSnapshotDirty = x.AggregateSnapshotDirty,
            AggregateSnapshotDirtyAtUtc = x.AggregateSnapshotDirtyAtUtc,
            AggregateSnapshotRefreshedAtUtc = x.AggregateSnapshotRefreshedAtUtc,
            AggregateRefreshError = x.AggregateRefreshError,

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
            AutoApproved = WorkAssignmentAutoApprovalState.IsAutoApproved(x),
            AutoApprovedAtUtc = x.AutoApprovedAtUtc,
            AutoApprovedByUserId = x.AutoApprovedByUserId,
            AutoApproveConditionSnapshotJson = x.AutoApproveConditionSnapshotJson,
            AutoApprovalLocked = WorkAssignmentAutoApprovalState.IsLocked(x),
            AutoApprovalConfirmedAtUtc = x.AutoApprovalConfirmedAtUtc,
            AutoApprovalConfirmedByUserId = x.AutoApprovalConfirmedByUserId,

            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc
        };
    }

    private async Task HydrateReportPayloadAsync(
        WorkAssignmentReport report,
        CancellationToken ct)
    {
        var payload = await _payloadReader.LoadReportPayloadAsync(report, ct);
        if (report.PayloadRevision > 0)
            WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, payload);
        report.Values1DJson = payload.Values1DJson;
        report.FieldValuesJson = payload.FieldValuesJson;
        report.TableValuesJson = payload.TableValuesJson;
        report.SummarySourceJson = payload.SummarySourceJson;
    }

    private static void ApplyPayloadMetadata(
        WorkAssignmentReport report,
        WorkReportPayloadWriteResult result,
        DateTime updatedAtUtc)
    {
        report.PayloadRevision = result.PayloadRevision;
        report.PayloadHash = result.PayloadHash;
        report.PayloadSizeBytes = result.PayloadSizeBytes;
        report.PayloadStatus = result.PayloadStatus;
        report.PayloadUpdatedAtUtc = updatedAtUtc;
    }

    private static PayloadMutationCommand ResolvePayloadMutationCommand(
        WorkAssignmentReport report,
        int? expectedPayloadRevision,
        string? commandId,
        string operation,
        string commandHash)
    {
        if (!expectedPayloadRevision.HasValue || expectedPayloadRevision.Value < 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_PAYLOAD_REVISION_REQUIRED,
                new
                {
                    reportId = report.Id,
                    expectedPayloadRevision,
                    currentPayloadRevision = report.PayloadRevision
                });
        }

        var normalizedCommandId = commandId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedCommandId) || !PayloadCommandIdRegex.IsMatch(normalizedCommandId))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_COMMAND_ID_REQUIRED,
                new
                {
                    reportId = report.Id,
                    commandId,
                    minLength = 8,
                    maxLength = 128
                });
        }

        var command = new PayloadMutationCommand(
            expectedPayloadRevision.Value,
            normalizedCommandId,
            operation,
            commandHash);

        if (string.Equals(report.LastPayloadCommandId, command.CommandId, StringComparison.Ordinal))
        {
            if (!string.Equals(report.LastPayloadCommandOperation, command.Operation, StringComparison.Ordinal) ||
                !string.Equals(report.LastPayloadCommandHash, command.CommandHash, StringComparison.Ordinal) ||
                report.LastPayloadCommandRevision != report.PayloadRevision ||
                command.ExpectedPayloadRevision != report.PayloadRevision - 1)
            {
                throw PayloadCommandReplayMismatch(report, command);
            }

            return command;
        }

        if (command.ExpectedPayloadRevision != report.PayloadRevision)
            throw PayloadRevisionConflict(report, command.ExpectedPayloadRevision);

        if (string.Equals(report.PayloadMutationCommandId, command.CommandId, StringComparison.Ordinal) &&
            (!string.Equals(report.PayloadMutationOperation, command.Operation, StringComparison.Ordinal) ||
             !string.Equals(report.PayloadMutationCommandHash, command.CommandHash, StringComparison.Ordinal)))
        {
            throw PayloadCommandReplayMismatch(report, command);
        }

        return command;
    }

    private static bool IsCompletedPayloadCommandReplay(
        WorkAssignmentReport report,
        PayloadMutationCommand command)
        => string.Equals(report.LastPayloadCommandId, command.CommandId, StringComparison.Ordinal)
           && string.Equals(report.LastPayloadCommandOperation, command.Operation, StringComparison.Ordinal)
           && string.Equals(report.LastPayloadCommandHash, command.CommandHash, StringComparison.Ordinal)
           && report.LastPayloadCommandRevision == report.PayloadRevision
           && command.ExpectedPayloadRevision == report.PayloadRevision - 1;

    private async Task<PayloadCommandReservationOutcome> ReservePayloadMutationCommandAsync(
        WorkAssignmentReport report,
        PayloadMutationCommand command,
        DateTime now,
        CancellationToken ct,
        WorkAssignmentReportStatus expectedStatus = WorkAssignmentReportStatus.Draft,
        bool expectedIsActive = true,
        bool deferToTransaction = false,
        IClientSessionHandle? session = null)
    {
        var fb = Builders<WorkAssignmentReport>.Filter;
        var sameCommand = fb.Eq(x => x.PayloadMutationCommandId, command.CommandId)
                          & fb.Eq(x => x.PayloadMutationCommandHash, command.CommandHash)
                          & fb.Eq(x => x.PayloadMutationOperation, command.Operation);
        var noCommand = fb.Eq(x => x.PayloadMutationCommandId, null);
        var payloadRevisionFilter = command.ExpectedPayloadRevision == 0
            ? fb.Eq(x => x.PayloadRevision, 0) | fb.Exists(x => x.PayloadRevision, false)
            : fb.Eq(x => x.PayloadRevision, command.ExpectedPayloadRevision);
        var filter = fb.Eq(x => x.Id, report.Id)
                      & fb.Eq(x => x.IsDeleted, false)
                      & fb.Eq(x => x.AssigneeUserId, report.AssigneeUserId)
                      & fb.Eq(x => x.IsActive, expectedIsActive)
                     & fb.Eq(x => x.Status, expectedStatus)
                     & payloadRevisionFilter
                     & (noCommand | sameCommand);

        var reservationUpdate = Builders<WorkAssignmentReport>.Update
                .Set(x => x.PayloadMutationCommandId, command.CommandId)
                .Set(x => x.PayloadMutationCommandHash, command.CommandHash)
                .Set(x => x.PayloadMutationOperation, command.Operation)
                .Set(x => x.PayloadMutationStartedAtUtc, now);
        UpdateResult result = deferToTransaction
            ? new UpdateResult.Acknowledged(await _ctx.WorkAssignmentReports.Find(filter).AnyAsync(ct) ? 1 : 0, 0, null)
            : session is null
                ? await _ctx.WorkAssignmentReports.UpdateOneAsync(filter, reservationUpdate, cancellationToken: ct)
                : await _ctx.WorkAssignmentReports.UpdateOneAsync(session, filter, reservationUpdate, cancellationToken: ct);
        if (result.MatchedCount == 1)
        {
            report.PayloadMutationCommandId = command.CommandId;
            report.PayloadMutationCommandHash = command.CommandHash;
            report.PayloadMutationOperation = command.Operation;
            report.PayloadMutationStartedAtUtc = now;
            return PayloadCommandReservationOutcome.Reserved;
        }

        if (session is not null) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");

        var current = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == report.Id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (current is null)
            throw ReportNotFound(report.Id);
        if (IsCompletedPayloadCommandReplay(current, command))
            return PayloadCommandReservationOutcome.CompletedReplay;

        if (string.Equals(current.PayloadMutationCommandId, command.CommandId, StringComparison.Ordinal) &&
            (!string.Equals(current.PayloadMutationCommandHash, command.CommandHash, StringComparison.Ordinal) ||
             !string.Equals(current.PayloadMutationOperation, command.Operation, StringComparison.Ordinal)))
        {
            throw PayloadCommandReplayMismatch(current, command);
        }

        throw PayloadRevisionConflict(current, command.ExpectedPayloadRevision);
    }

    private static FilterDefinition<WorkAssignmentReport> BuildPayloadMutationCommitFilter(
        WorkAssignmentReport report,
        PayloadMutationCommand command,
        WorkAssignmentReportStatus expectedStatus = WorkAssignmentReportStatus.Draft,
        bool expectedIsActive = true)
    {
        var fb = Builders<WorkAssignmentReport>.Filter;
        var payloadRevisionFilter = command.ExpectedPayloadRevision == 0
            ? fb.Eq(x => x.PayloadRevision, 0) | fb.Exists(x => x.PayloadRevision, false)
            : fb.Eq(x => x.PayloadRevision, command.ExpectedPayloadRevision);
        return fb.Eq(x => x.Id, report.Id)
               & fb.Eq(x => x.IsDeleted, false)
               & fb.Eq(x => x.AssigneeUserId, report.AssigneeUserId)
               & fb.Eq(x => x.IsActive, expectedIsActive)
               & fb.Eq(x => x.Status, expectedStatus)
               & payloadRevisionFilter
               & fb.Eq(x => x.PayloadMutationCommandId, command.CommandId)
               & fb.Eq(x => x.PayloadMutationCommandHash, command.CommandHash)
               & fb.Eq(x => x.PayloadMutationOperation, command.Operation);
    }

    private static UpdateDefinition<WorkAssignmentReport> ApplyPayloadCommandCompletion(
        UpdateDefinition<WorkAssignmentReport> update,
        PayloadMutationCommand command,
        WorkReportPayloadWriteResult result)
        => update
            .Unset(x => x.PayloadMutationCommandId)
            .Unset(x => x.PayloadMutationCommandHash)
            .Unset(x => x.PayloadMutationOperation)
            .Unset(x => x.PayloadMutationStartedAtUtc)
            .Set(x => x.LastPayloadCommandId, command.CommandId)
            .Set(x => x.LastPayloadCommandHash, command.CommandHash)
            .Set(x => x.LastPayloadCommandOperation, command.Operation)
            .Set(x => x.LastPayloadCommandRevision, result.PayloadRevision);

    private static void ApplyPayloadCommandCompletionInMemory(
        WorkAssignmentReport report,
        PayloadMutationCommand command,
        WorkReportPayloadWriteResult result)
    {
        report.PayloadMutationCommandId = null;
        report.PayloadMutationCommandHash = null;
        report.PayloadMutationOperation = null;
        report.PayloadMutationStartedAtUtc = null;
        report.LastPayloadCommandId = command.CommandId;
        report.LastPayloadCommandHash = command.CommandHash;
        report.LastPayloadCommandOperation = command.Operation;
        report.LastPayloadCommandRevision = result.PayloadRevision;
    }

    private async Task<WorkAssignmentReportResponse> ResolvePayloadCommandCommitRaceAsync(
        string reportId,
        PayloadMutationCommand command,
        string actorUserId,
        CancellationToken ct)
    {
        var current = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (current is null)
            throw ReportNotFound(reportId);
        if (!IsCompletedPayloadCommandReplay(current, command))
            throw PayloadRevisionConflict(current, command.ExpectedPayloadRevision);

        await TryReconcileCommittedLifecycleProjectionAsync(current.Id);
        return await MapPayloadCommandReplayResponseAsync(current, actorUserId, CancellationToken.None);
    }

    private Task<bool> TryReconcileCommittedLifecycleProjectionAsync(string reportId)
        => _lifecycleProjectionReconciler.ReconcileReportAsync(reportId, CancellationToken.None);

    private async Task<WorkAssignmentReportResponse> MapPayloadCommandReplayResponseAsync(
        string reportId,
        string actorUserId,
        CancellationToken ct)
    {
        var current = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (current is null)
            throw ReportNotFound(reportId);
        return await MapPayloadCommandReplayResponseAsync(current, actorUserId, ct);
    }

    private async Task<WorkAssignmentReportResponse> MapPayloadCommandReplayResponseAsync(
        WorkAssignmentReport report,
        string actorUserId,
        CancellationToken ct)
    {
        await HydrateReportPayloadAsync(report, ct);
        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == report.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        return await MapToResponseAsync(report, period, actorUserId, ct);
    }

    private async Task<WorkAssignmentReport> LoadCompletedPayloadCommandReportAsync(
        string reportId,
        PayloadMutationCommand command,
        CancellationToken ct)
    {
        var current = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (current is null)
            throw ReportNotFound(reportId);
        if (!IsCompletedPayloadCommandReplay(current, command))
            throw PayloadRevisionConflict(current, command.ExpectedPayloadRevision);

        await HydrateReportPayloadAsync(current, ct);
        return current;
    }

    private static string ComputeReportPayloadCommandHash(string operation, object request)
        => WorkReportLifecycleCommandContract.ComputeHash(operation, request);

    private static PayloadMutationCommand BuildSystemPayloadMutationCommand(
        WorkAssignmentReport report,
        string operation,
        WorkReportPayloadWriteResult preflight)
    {
        var commandHash = WorkReportLifecycleCommandContract.ComputeHash(
            operation,
            new
            {
                reportId = report.Id,
                expectedPayloadRevision = report.PayloadRevision,
                nextPayloadRevision = preflight.PayloadRevision,
                payloadHash = preflight.PayloadHash
            });
        var commandId = $"system:payload:{report.Id}:{report.PayloadRevision}:{commandHash[..24]}";
        return new PayloadMutationCommand(
            report.PayloadRevision,
            commandId,
            operation,
            commandHash);
    }

    private static AppException PayloadRevisionConflict(
        WorkAssignmentReport report,
        int expectedPayloadRevision)
        => AppExceptionFactory.Create(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_PAYLOAD_REVISION_CONFLICT,
            new
            {
                reportId = report.Id,
                expectedPayloadRevision,
                currentPayloadRevision = report.PayloadRevision,
                report.Status,
                report.UpdatedAtUtc,
                report.UpdatedByUserId,
                inProgressCommand = !string.IsNullOrWhiteSpace(report.PayloadMutationCommandId)
            });

    private static AppException PayloadCommandReplayMismatch(
        WorkAssignmentReport report,
        PayloadMutationCommand command)
        => AppExceptionFactory.Create(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_COMMAND_REPLAY_MISMATCH,
            new
            {
                reportId = report.Id,
                commandId = command.CommandId,
                operation = command.Operation,
                expectedPayloadRevision = command.ExpectedPayloadRevision,
                currentPayloadRevision = report.PayloadRevision
            });

    private static UpdateDefinition<WorkAssignmentReport> ApplyPayloadHeaderUpdate(
        UpdateDefinitionBuilder<WorkAssignmentReport> update,
        WorkReportPayloadWriteResult result,
        DateTime updatedAtUtc)
        => update
            .Set(x => x.Values1DJson, EmptyValues1DJson)
            .Set(x => x.FieldValuesJson, (string?)null)
            .Set(x => x.TableValuesJson, (string?)null)
            .Set(x => x.SummarySourceJson, (string?)null)
            .Set(x => x.PayloadRevision, result.PayloadRevision)
            .Set(x => x.PayloadHash, result.PayloadHash)
            .Set(x => x.PayloadSizeBytes, result.PayloadSizeBytes)
            .Set(x => x.PayloadStatus, result.PayloadStatus)
            .Set(x => x.PayloadUpdatedAtUtc, updatedAtUtc);

    private static void CompactEmbeddedPayloadHeader(WorkAssignmentReport report)
    {
        report.Values1DJson = EmptyValues1DJson;
        report.FieldValuesJson = null;
        report.TableValuesJson = null;
        report.SummarySourceJson = null;
    }

    private static void RestoreRuntimePayload(
        WorkAssignmentReport report,
        string values1DJson,
        string? fieldValuesJson,
        string? tableValuesJson,
        string? summarySourceJson)
    {
        report.Values1DJson = values1DJson;
        report.FieldValuesJson = fieldValuesJson;
        report.TableValuesJson = tableValuesJson;
        report.SummarySourceJson = summarySourceJson;
    }

    private sealed record RuntimeRowLabelPayload(string BlockId, List<string> LabelCodes);

    private sealed record AllowedRowLabelConfig(HashSet<string> Codes, string? DataType);

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
                if (string.Equals(toStatus, WorkAssignmentReportStatus.Approved.ToString(), StringComparison.OrdinalIgnoreCase))
                    WorkReportPayloadConsistency.EnsureReadyForStatisticProjection(report);

                await _labelStatistics.RebuildForReportAsync(report.Id, actorUserId, ct);
                await _tableStatistics.RebuildForReportAsync(report.Id, actorUserId, ct);
                await _fieldStatistics.RebuildForReportAsync(report.Id, actorUserId, ct);
            }

            if (ShouldRefreshAggregateDependents(fromStatus, toStatus))
            {
                await _aggregateDependentRecovery.RecoverPendingAsync(report.Id, actorUserId, ct);
            }

            await _advancedSummaryDirty.MarkReportStatusMutationDirtyAsync(
                report,
                operation,
                fromStatus,
                toStatus,
                actorUserId,
                ct);

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
        // Tổng hợp cũ tạm khóa; giữ dữ liệu, không phát sinh refresh.
        if (tdtd_be.Services.WorkAssignments.LegacyAggregateRetirement.IsDisabled) return;
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

            await HydrateReportPayloadAsync(candidate, ct);
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
                if (!AggregateSummaryReferencesSource(summary, source))
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
        // Tổng hợp cũ tạm khóa; giữ dữ liệu, không phát sinh refresh.
        if (tdtd_be.Services.WorkAssignments.LegacyAggregateRetirement.IsDisabled) return;
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
            await HydrateReportPayloadAsync(candidate, ct);
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
        if (await AggregateHostIntegration.PeriodicOwnedAsync(_ctx, candidate.Id, ct)) return;
        var aggregateRefreshFence =
            await AcquireAggregateRefreshLeaseIfDynamicFlowAsync(
                candidate.WorkAssignmentId,
                ct);
        if (aggregateRefreshFence.BlockedByCompletedFlow)
            return;
        await using var aggregateRefreshLease = aggregateRefreshFence.Lease;
        if (aggregateRefreshLease is not null)
        {
            var currentCandidate = await _ctx.WorkAssignmentReports
                .Find(x =>
                    x.Id == candidate.Id &&
                    x.IsActive &&
                    !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (currentCandidate is null)
                return;
            candidate = currentCandidate;
            await aggregateRefreshLease.RenewAsync(ct);
            await MarkAggregateSnapshotDirtyAsync(
                candidate,
                source,
                actorUserId,
                ct);
        }

        try
        {
            await HydrateReportPayloadAsync(candidate, ct);
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
                aggregateRefreshLease,
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
                    aggregateRefreshLease,
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

            if (aggregateRefreshLease is not null)
                await aggregateRefreshLease.RenewAsync(CancellationToken.None);
            await MarkAggregateSnapshotDirtyAsync(
                candidate,
                source,
                actorUserId,
                ct,
                NormalizeOptionalTextOrNull(ex.Message) ?? ex.GetType().Name);
        }
    }

    private async Task<AggregateRefreshFence>
        AcquireAggregateRefreshLeaseIfDynamicFlowAsync(
            string workAssignmentId,
            CancellationToken ct)
    {
        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == workAssignmentId && !x.IsDeleted)
            .Project(x => new
            {
                x.FlowInstanceId,
                x.CompletedAtUtc
            })
            .FirstOrDefaultAsync(ct);
        if (assignment is null ||
            string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
        {
            return new AggregateRefreshFence(
                BlockedByCompletedFlow: false,
                Lease: null);
        }
        if (assignment.CompletedAtUtc.HasValue)
        {
            return new AggregateRefreshFence(
                BlockedByCompletedFlow: true,
                Lease: null);
        }

        return new AggregateRefreshFence(
            BlockedByCompletedFlow: false,
            Lease: await _lifecycleSeriesLock.AcquireAsync(
                workAssignmentId,
                WorkReportLifecycleSeriesOperations.AggregateRefresh,
                ct));
    }

    private async Task MoveApprovedAggregateReportBackToSubmittedAsync(
        WorkAssignmentReport report,
        string actorUserId,
        string reason,
        WorkReportLifecycleSeriesLease? existingLifecycleSeriesLease,
        CancellationToken ct)
    {
        if (report.Status != WorkAssignmentReportStatus.Approved)
            return;

        const string lifecycleOperation = "AUTO_AGGREGATE_REVIEW_INVALIDATED";
        var normalizedReason = NormalizeOptionalTextOrNull(reason);
        var lifecycleCommandHash = WorkReportLifecycleCommandContract.ComputeHash(
            lifecycleOperation,
            new
            {
                reportId = report.Id,
                payloadRevision = report.PayloadRevision,
                reason = normalizedReason
            });
        var lifecycleCommandId = $"system:lifecycle:{report.Id}:{report.PayloadRevision}:{lifecycleCommandHash[..24]}";
        var (lifecycleCommand, lifecycleResolution) = WorkReportLifecycleCommandContract.Resolve(
            report,
            report.LifecycleRevision,
            report.PayloadRevision,
            lifecycleCommandId,
            lifecycleOperation,
            lifecycleCommandHash);
        if (lifecycleResolution == WorkReportLifecycleCommandResolution.CompletedReplay)
        {
            await ReconcileLifecycleProjectionUnlessAggregateRecoveryAsync(report.Id, ct);
            return;
        }

        await using var ownedLifecycleSeriesLease =
            existingLifecycleSeriesLease is null
                ? await _lifecycleSeriesLock.AcquireAsync(
                    report.WorkAssignmentId,
                    lifecycleCommand.Operation,
                    ct)
                : null;
        var lifecycleSeriesLease =
            existingLifecycleSeriesLease ?? ownedLifecycleSeriesLease!;

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == report.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        var now = DateTime.UtcNow;
        var fromStatus = report.Status;

        var reportCommitUpdate = WorkReportLifecycleCommandContract.ApplyCompletion(
                Builders<WorkAssignmentReport>.Update
                .Set(x => x.Status, WorkAssignmentReportStatus.Submitted)
                .Set(x => x.ApprovedAtUtc, (DateTime?)null)
                .Set(x => x.ApprovedByUserId, (string?)null)
                .Set(x => x.ReviewerComment, normalizedReason)
                .Set(x => x.AggregateSnapshotDirty, false)
                .Set(x => x.AggregateSnapshotDirtyAtUtc, (DateTime?)null)
                .Set(x => x.AggregateRefreshError, (string?)null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
                lifecycleCommand,
                WorkAssignmentReportStatus.Submitted,
                resultIsActive: true);
        reportCommitUpdate = WorkReportLifecycleOutboxContract.Append(
            reportCommitUpdate,
            WorkReportLifecycleOutboxContract.FromCommand(
                report,
                lifecycleCommand,
                WorkAssignmentReportStatus.Submitted,
                resultIsActive: true,
                actorUserId: actorUserId,
                committedAtUtc: now,
                resultPayloadHash: report.PayloadHash,
                businessReason: normalizedReason,
                businessComment: normalizedReason));

        await lifecycleSeriesLease.RenewAsync(ct);
        var reportCommit = await CommitLegacyLifecycleWithDirectSourceFenceAsync(
            report,
            WorkReportLifecycleCommandContract.BuildCommitFilter(
                report,
                lifecycleCommand,
                WorkAssignmentReportStatus.Approved),
            reportCommitUpdate,
            ct, lifecycleCommand, WorkAssignmentReportStatus.Submitted, actorUserId);

        if (reportCommit.ModifiedCount != 1)
        {
            var current = await _ctx.WorkAssignmentReports
                .Find(x => x.Id == report.Id && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw ReportNotFound(report.Id);
            if (WorkReportLifecycleCommandContract.IsCompletedReplay(current, lifecycleCommand))
            {
                await ReconcileLifecycleProjectionUnlessAggregateRecoveryAsync(current.Id, ct);
                return;
            }

            throw WorkReportLifecycleCommandContract.Conflict(current, lifecycleCommand);
        }

        report.Status = WorkAssignmentReportStatus.Submitted;
        report.ApprovedAtUtc = null;
        report.ApprovedByUserId = null;
        report.ReviewerComment = normalizedReason;
        report.AggregateSnapshotDirty = false;
        report.AggregateSnapshotDirtyAtUtc = null;
        report.AggregateRefreshError = null;
        report.UpdatedAtUtc = now;
        report.UpdatedByUserId = actorUserId;
        WorkReportLifecycleCommandContract.ApplyCompletionInMemory(
            report,
            lifecycleCommand,
            WorkAssignmentReportStatus.Submitted,
            resultIsActive: true);
        await ReconcileLifecycleProjectionUnlessAggregateRecoveryAsync(report.Id, CancellationToken.None);
    }

    private async Task ReconcileLifecycleProjectionUnlessAggregateRecoveryAsync(
        string reportId,
        CancellationToken ct)
    {
        if (WorkReportAggregateDependentRecoveryExecution.IsActive)
            return;

        await TryReconcileCommittedLifecycleProjectionAsync(reportId);
    }

    private async Task MarkAggregateSnapshotDirtyAsync(
        WorkAssignmentReport candidate,
        WorkAssignmentReport source,
        string actorUserId,
        CancellationToken ct,
        string? refreshError = null)
    {
        // Tổng hợp cũ tạm khóa; giữ dữ liệu, không phát sinh refresh.
        if (tdtd_be.Services.WorkAssignments.LegacyAggregateRetirement.IsDisabled) return;
        if (await AggregateHostIntegration.PeriodicOwnedAsync(_ctx, candidate.Id, ct)) return;
        var now = DateTime.UtcNow;
        if (!await TryUpdateLegacyAggregateMetadataAsync(candidate,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.AggregateSnapshotDirty, true)
                .Set(x => x.AggregateSnapshotDirtyAtUtc, now)
                .Set(x => x.AggregateRefreshError, refreshError)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            ct)) return;

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
        if (tdtd_be.Services.WorkAssignments.LegacyAggregateRetirement.IsDisabled) return report;
        if (await AggregateHostIntegration.PeriodicOwnedAsync(_ctx, report.Id, ct)) return report;
        if (!report.AggregateSnapshotDirty)
            return report;

        AggregateRefreshFence aggregateRefreshFence;
        try
        {
            aggregateRefreshFence =
                await AcquireAggregateRefreshLeaseIfDynamicFlowAsync(
                    report.WorkAssignmentId,
                    ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Dynamic Flow aggregate snapshot refresh deferred by lifecycle fence. reportId={reportId} actorUserId={actorUserId}",
                report.Id,
                actorUserId);
            return report;
        }
        if (aggregateRefreshFence.BlockedByCompletedFlow)
            return report;
        await using var aggregateRefreshLease = aggregateRefreshFence.Lease;
        if (aggregateRefreshLease is not null)
        {
            var current = await _ctx.WorkAssignmentReports
                .Find(x => x.Id == report.Id && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (current is null || !current.AggregateSnapshotDirty)
                return current ?? report;
            report = current;
        }

        await HydrateReportPayloadAsync(report, ct);
        var summary = TryReadAggregateDraftSummary(report.SummarySourceJson);
        if (summary is null)
            return report;

        try
        {
            var refreshed = await RefreshDynamicFormAggregateReportFromSummaryAsync(
                report,
                summary,
                actorUserId,
                aggregateRefreshLease,
                ct) ?? report;

            if (refreshed.Status == WorkAssignmentReportStatus.Approved)
            {
                await MoveApprovedAggregateReportBackToSubmittedAsync(
                    refreshed,
                    actorUserId,
                    "Nguồn tổng hợp đã thay đổi trước khi mở báo cáo.",
                    aggregateRefreshLease,
                    ct);
            }

            return refreshed;
        }
        catch (Exception ex)
        {
            if (aggregateRefreshLease is not null)
                await aggregateRefreshLease.RenewAsync(CancellationToken.None);
            var now = DateTime.UtcNow;
            var error = NormalizeOptionalTextOrNull(ex.Message) ?? ex.GetType().Name;
            _log.LogWarning(
                ex,
                "Dynamic Form aggregate snapshot lazy refresh failed. reportId={reportId} actorUserId={actorUserId}",
                report.Id,
                actorUserId);

            if (!await TryUpdateLegacyAggregateMetadataAsync(report,
                Builders<WorkAssignmentReport>.Update
                    .Set(x => x.AggregateRefreshError, error)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId),
                ct)) return report;

            report.AggregateRefreshError = error;
            report.UpdatedAtUtc = now;
            report.UpdatedByUserId = actorUserId;
            return report;
        }
    }

    private Task<bool> TryUpdateLegacyAggregateMetadataAsync(WorkAssignmentReport observed,
        UpdateDefinition<WorkAssignmentReport> update, CancellationToken ct)
        => _dynamicFlowTransactions.ExecuteAsync(async (session, token) =>
        {
            var tx = AggregateHostIntegration.Transaction(_ctx, session);
            var instances = await tx.QueryAsync<AggregateInstanceState>(AggregateCollections.Instances,
                new(observed.WorkId, observed.Id), token);
            if (instances.Any(i => i.Value.State != "UNLINKED")) return false;
            var result = await _ctx.WorkAssignmentReports.UpdateOneAsync(session,
                x => x.Id == observed.Id && !x.IsDeleted && x.PayloadRevision == observed.PayloadRevision
                    && x.LifecycleRevision == observed.LifecycleRevision && x.Status == observed.Status,
                update, cancellationToken: token);
            if (result.MatchedCount != 1) return false;
            // Serialize with creating/applying a mapping after the initial legacy skip check.
            await WorkDirectSourceRevisionFence.IncrementAsync(_ctx, session, observed.WorkId, token);
            return true;
        }, ct);

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
        WorkReportLifecycleSeriesLease? aggregateRefreshLease,
        CancellationToken ct)
    {
        if (tdtd_be.Services.WorkAssignments.LegacyAggregateRetirement.IsDisabled) return report;
        if (string.IsNullOrWhiteSpace(report.DynamicFormTemplateId))
            return null;

        var aggregateReq = NormalizeAggregateDraftRequest(summary.AggregateRequest);

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
        if (aggregate.StackedTable is not null &&
            string.Equals(block.TableMode, "APPEND_ROWS", StringComparison.Ordinal))
        {
            return await RefreshStackedDynamicFormAggregateReportFromSummaryAsync(
                report,
                summary,
                aggregateReq,
                aggregate,
                form,
                block,
                targetBlockId,
                dataOrigin,
                actorUserId,
                aggregateRefreshLease,
                ct);
        }

        var existingTopLevelValues = DeserializeValues1D(report.Values1DJson);
        var isTopLevelBlock = string.Equals(targetBlockId, ResolveTopLevelBlockId(form), StringComparison.Ordinal);
        var currentBlockValues = isTopLevelBlock
            ? existingTopLevelValues
            : ExtractBlockDecimalValues(report.TableValuesJson, targetBlockId);
        var clearExisting = summary.ClearExistingValues ?? dataOrigin != WorkReportDataOrigin.PartialMapping;
        var targetValues = clearExisting
            ? CreateEmptyValues1D(block.ValueLength, 1)
            : NormalizeDecimalValues(currentBlockValues, block.ValueLength);

        if (!clearExisting)
            ClearAggregateDraftTargetIndexes(targetValues, summary.TargetIndexes);

        var valueSelector = NormalizeAggregateDraftValueSelector(summary.ValueSelector);
        var draftAggregate = ResolveMetricDraftAggregate(aggregate, block, valueSelector);

        ApplyAggregateRowsToValues(
            targetValues,
            draftAggregate.Rows,
            block,
            valueSelector);

        var tableValuesJson = BuildAggregateDraftTableValuesJson(report, form, block, targetValues, draftAggregate);
        var topLevelValues = isTopLevelBlock
            ? targetValues
            : NormalizeDecimalValues(existingTopLevelValues, ResolveReportRuntimeInputCells(report).Count);
        var values1DJson = Values1DCompression.SerializeDecimals(topLevelValues, _jsonOptions);
        tableValuesJson = Values1DCompression.CompressTableValuesJson(tableValuesJson, _jsonOptions);
        var contributionPolicyJson = BuildAggregateDraftContributionPolicyJson(dataOrigin, draftAggregate.Rows, block.BlockId);
        var summarySourceJson = BuildAggregateDraftSummarySourceJson(
            dataOrigin,
            aggregateReq,
            draftAggregate,
            block,
            valueSelector,
            targetBlockId,
            clearExisting,
            report.DynamicFormTemplateId,
            summary.ReportMapConfigJson);
        var sourceSnapshot = ExtractAggregateSourceSnapshot(summarySourceJson);
        var now = DateTime.UtcNow;
        var runtimeTopLevelBlock = await ResolveRuntimeTopLevelBlockShapeAsync(
            report,
            tableValuesJson,
            Values1DCompression.DeserializeObjects(values1DJson, _jsonOptions).Count,
            ct);
        if (runtimeTopLevelBlock is not null)
            ApplyRuntimeTopLevelShape(report, runtimeTopLevelBlock);
        await ValidateRuntimeRowLabelsAsync(report, tableValuesJson, ct);
        var canonicalFieldValuesJson = await ValidateRuntimeDataPayloadAsync(
            report,
            Values1DCompression.DeserializeObjects(values1DJson, _jsonOptions),
            report.FieldValuesJson,
            tableValuesJson,
            validateRequiredFields: false,
            ct);
        var preflight = WorkReportPayloadService.PreflightReportPayload(
            report,
            values1DJson,
            canonicalFieldValuesJson,
            tableValuesJson,
            summarySourceJson,
            actorUserId,
            now);
        var expectedStatus = report.Status;
        var payloadCommand = BuildSystemPayloadMutationCommand(
            report,
            "AUTO_REFRESH_AGGREGATE",
            preflight);
        if (aggregateRefreshLease is not null)
            await aggregateRefreshLease.RenewAsync(ct);
        var reservation = await ReservePayloadMutationCommandAsync(
            report,
            payloadCommand,
            now,
            ct,
            expectedStatus, deferToTransaction: true);
        if (reservation == PayloadCommandReservationOutcome.CompletedReplay)
            return await LoadCompletedPayloadCommandReportAsync(report.Id, payloadCommand, ct);

        var payloadResult = preflight;

        if (aggregateRefreshLease is not null)
            await aggregateRefreshLease.RenewAsync(ct);
        var preserveAggregateDirty =
            aggregateRefreshLease is not null &&
            expectedStatus == WorkAssignmentReportStatus.Approved;
        var headerCommit = await CommitLegacyLifecycleWithDirectSourceFenceAsync(
            report,
            BuildPayloadMutationCommitFilter(report, payloadCommand, expectedStatus),
            ApplyPayloadCommandCompletion(
                ApplyPayloadHeaderUpdate(
                Builders<WorkAssignmentReport>.Update,
                payloadResult,
                now)
                .Set(x => x.DataOrigin, dataOrigin)
                .Set(x => x.CumulativeContributionMode, WorkReportDataOrigin.DefaultContributionMode(dataOrigin))
                .Set(x => x.CumulativeContributionPolicyJson, contributionPolicyJson)
                .Set(x => x.AggregateSourceReportIds, sourceSnapshot.ReportIds)
                .Set(x => x.AggregateSourceAssignmentIds, sourceSnapshot.AssignmentIds)
                .Set(x => x.AggregateSourceUpdatedAtUtc, now)
                .Set(x => x.AggregateSnapshotDirty, preserveAggregateDirty)
                .Set(
                    x => x.AggregateSnapshotDirtyAtUtc,
                    preserveAggregateDirty ? now : (DateTime?)null)
                .Set(x => x.AggregateSnapshotRefreshedAtUtc, now)
                .Set(x => x.AggregateRefreshError, (string?)null)
                .Set(x => x.W, report.W)
                .Set(x => x.H, report.H)
                .Set(x => x.DataRectR0, report.DataRectR0)
                .Set(x => x.DataRectC0, report.DataRectC0)
                .Set(x => x.DataRectR1, report.DataRectR1)
                .Set(x => x.DataRectC1, report.DataRectC1)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
                payloadCommand,
                payloadResult),
            ct, payloadCommandToReserve: payloadCommand, reservationStatus: expectedStatus,
            persistPayload: async (session, before, token) =>
            {
                await AggregateHostIntegration.GuardPayloadAsync(_ctx, session, before, canonicalFieldValuesJson, tableValuesJson, token);
                await AggregateHostIntegration.EnsureLegacyRefreshAsync(_ctx, session, before, token);
                var written = await _payloadWriter.SaveReportPayloadAsync(before, values1DJson, canonicalFieldValuesJson,
                    tableValuesJson, summarySourceJson, actorUserId, now, token, session);
                if (written != payloadResult) throw new AggregatePreviewException("AGG_PAYLOAD_PLAN_STALE");
            });

        if (headerCommit.ModifiedCount != 1)
            return await LoadCompletedPayloadCommandReportAsync(report.Id, payloadCommand, ct);

        report.Values1DJson = values1DJson;
        report.FieldValuesJson = canonicalFieldValuesJson;
        report.TableValuesJson = tableValuesJson;
        ApplyPayloadMetadata(report, payloadResult, now);
        ApplyPayloadCommandCompletionInMemory(report, payloadCommand, payloadResult);
        report.DataOrigin = dataOrigin;
        report.CumulativeContributionMode = WorkReportDataOrigin.DefaultContributionMode(dataOrigin);
        report.CumulativeContributionPolicyJson = contributionPolicyJson;
        report.SummarySourceJson = summarySourceJson;
        report.AggregateSourceReportIds = sourceSnapshot.ReportIds;
        report.AggregateSourceAssignmentIds = sourceSnapshot.AssignmentIds;
        report.AggregateSourceUpdatedAtUtc = now;
        report.AggregateSnapshotDirty = preserveAggregateDirty;
        report.AggregateSnapshotDirtyAtUtc =
            preserveAggregateDirty ? now : null;
        report.AggregateSnapshotRefreshedAtUtc = now;
        report.AggregateRefreshError = null;
        report.UpdatedAtUtc = now;
        report.UpdatedByUserId = actorUserId;

        await _sectionProjection.ProjectAndVerifyAsync(
            report,
            report.FieldValuesJson,
            report.TableValuesJson,
            actorUserId,
            now,
            ct);

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
            await _advancedSummaryDirty.MarkApprovedReportPayloadDirtyAsync(
                report,
                "AUTO_REFRESH_AGGREGATE",
                actorUserId,
                ct);
        }

        if (!string.IsNullOrWhiteSpace(report.WorkReportPeriodId))
            await _docRoleReadModelProjection.RebuildReportPeriodAsync(report.WorkReportPeriodId, actorUserId, ct);

        return report;
    }

    private async Task<WorkAssignmentReport?> RefreshStackedDynamicFormAggregateReportFromSummaryAsync(
        WorkAssignmentReport report,
        AggregateDraftSummary summary,
        DynamicFormAggregateRequest aggregateReq,
        DynamicFormAggregateResponse aggregate,
        DynamicFormTemplate form,
        AggregateDraftBlockContract block,
        string targetBlockId,
        string dataOrigin,
        string actorUserId,
        WorkReportLifecycleSeriesLease? aggregateRefreshLease,
        CancellationToken ct)
    {
        if (!string.Equals(block.TableMode, "APPEND_ROWS", StringComparison.Ordinal))
            return null;

        var stacked = aggregate.StackedTable ?? new DynamicFormStackedTableDto();
        var columnCount = Math.Max(block.W, stacked.Columns.Count);
        var rowCount = Math.Max(1, stacked.Rows.Count);
        var effectiveBlock = block with
        {
            W = columnCount,
            H = rowCount,
            ValueLength = columnCount * rowCount,
            DataRect = BuildExpandedAggregateDraftDataRect(block.DataRect, columnCount, rowCount)
        };

        var values = BuildStackedAggregateValues(stacked, columnCount, rowCount);
        var tableValuesJson = BuildStackedAggregateDraftTableValuesJson(report, form, effectiveBlock, values, aggregate);
        var isTopLevelBlock = string.Equals(targetBlockId, ResolveTopLevelBlockId(form), StringComparison.Ordinal);
        var existingTopLevelValues = DeserializeValues1D(report.Values1DJson);
        var topLevelValues = isTopLevelBlock
            ? values
            : NormalizeDecimalValues(existingTopLevelValues, ResolveReportRuntimeInputCells(report).Count)
                .Select(x => (object?)x)
                .ToList();
        var values1DJson = Values1DCompression.Serialize(topLevelValues, _jsonOptions);
        tableValuesJson = Values1DCompression.CompressTableValuesJson(tableValuesJson, _jsonOptions);
        var contributionPolicyJson = BuildStackedAggregateDraftContributionPolicyJson(dataOrigin, stacked, effectiveBlock.BlockId);
        var summarySourceJson = BuildStackedAggregateDraftSummarySourceJson(
            dataOrigin,
            aggregateReq,
            aggregate,
            NormalizeAggregateDraftValueSelector(summary.ValueSelector),
            targetBlockId,
            form.Id,
            summary.ReportMapConfigJson);
        var sourceSnapshot = ExtractAggregateSourceSnapshot(summarySourceJson);
        var now = DateTime.UtcNow;
        var runtimeTopLevelBlock = await ResolveRuntimeTopLevelBlockShapeAsync(
            report,
            tableValuesJson,
            Values1DCompression.DeserializeObjects(values1DJson, _jsonOptions).Count,
            ct);
        if (runtimeTopLevelBlock is not null)
            ApplyRuntimeTopLevelShape(report, runtimeTopLevelBlock);
        await ValidateRuntimeRowLabelsAsync(report, tableValuesJson, ct);
        var canonicalFieldValuesJson = await ValidateRuntimeDataPayloadAsync(
            report,
            Values1DCompression.DeserializeObjects(values1DJson, _jsonOptions),
            report.FieldValuesJson,
            tableValuesJson,
            validateRequiredFields: false,
            ct);
        var preflight = WorkReportPayloadService.PreflightReportPayload(
            report,
            values1DJson,
            canonicalFieldValuesJson,
            tableValuesJson,
            summarySourceJson,
            actorUserId,
            now);
        var expectedStatus = report.Status;
        var payloadCommand = BuildSystemPayloadMutationCommand(
            report,
            "REFRESH_AGGREGATE_STACKED_DRAFT",
            preflight);
        if (aggregateRefreshLease is not null)
            await aggregateRefreshLease.RenewAsync(ct);
        var reservation = await ReservePayloadMutationCommandAsync(
            report,
            payloadCommand,
            now,
            ct,
            expectedStatus, deferToTransaction: true);
        if (reservation == PayloadCommandReservationOutcome.CompletedReplay)
            return await LoadCompletedPayloadCommandReportAsync(report.Id, payloadCommand, ct);

        var payloadResult = preflight;

        if (aggregateRefreshLease is not null)
            await aggregateRefreshLease.RenewAsync(ct);
        var preserveAggregateDirty =
            aggregateRefreshLease is not null &&
            expectedStatus == WorkAssignmentReportStatus.Approved;
        var headerCommit = await CommitLegacyLifecycleWithDirectSourceFenceAsync(
            report,
            BuildPayloadMutationCommitFilter(report, payloadCommand, expectedStatus),
            ApplyPayloadCommandCompletion(
                ApplyPayloadHeaderUpdate(
                Builders<WorkAssignmentReport>.Update,
                payloadResult,
                now)
                .Set(x => x.DataOrigin, dataOrigin)
                .Set(x => x.CumulativeContributionMode, WorkReportDataOrigin.DefaultContributionMode(dataOrigin))
                .Set(x => x.CumulativeContributionPolicyJson, contributionPolicyJson)
                .Set(x => x.AggregateSourceReportIds, sourceSnapshot.ReportIds)
                .Set(x => x.AggregateSourceAssignmentIds, sourceSnapshot.AssignmentIds)
                .Set(x => x.AggregateSourceUpdatedAtUtc, now)
                .Set(x => x.AggregateSnapshotDirty, preserveAggregateDirty)
                .Set(
                    x => x.AggregateSnapshotDirtyAtUtc,
                    preserveAggregateDirty ? now : (DateTime?)null)
                .Set(x => x.AggregateSnapshotRefreshedAtUtc, now)
                .Set(x => x.AggregateRefreshError, (string?)null)
                .Set(x => x.W, report.W)
                .Set(x => x.H, report.H)
                .Set(x => x.DataRectR0, report.DataRectR0)
                .Set(x => x.DataRectC0, report.DataRectC0)
                .Set(x => x.DataRectR1, report.DataRectR1)
                .Set(x => x.DataRectC1, report.DataRectC1)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
                payloadCommand,
                payloadResult),
            ct, payloadCommandToReserve: payloadCommand, reservationStatus: expectedStatus,
            persistPayload: async (session, before, token) =>
            {
                await AggregateHostIntegration.GuardPayloadAsync(_ctx, session, before, canonicalFieldValuesJson, tableValuesJson, token);
                await AggregateHostIntegration.EnsureLegacyRefreshAsync(_ctx, session, before, token);
                var written = await _payloadWriter.SaveReportPayloadAsync(before, values1DJson, canonicalFieldValuesJson,
                    tableValuesJson, summarySourceJson, actorUserId, now, token, session);
                if (written != payloadResult) throw new AggregatePreviewException("AGG_PAYLOAD_PLAN_STALE");
            });

        if (headerCommit.ModifiedCount != 1)
            return await LoadCompletedPayloadCommandReportAsync(report.Id, payloadCommand, ct);

        report.Values1DJson = values1DJson;
        report.FieldValuesJson = canonicalFieldValuesJson;
        report.TableValuesJson = tableValuesJson;
        ApplyPayloadMetadata(report, payloadResult, now);
        ApplyPayloadCommandCompletionInMemory(report, payloadCommand, payloadResult);
        report.DataOrigin = dataOrigin;
        report.CumulativeContributionMode = WorkReportDataOrigin.DefaultContributionMode(dataOrigin);
        report.CumulativeContributionPolicyJson = contributionPolicyJson;
        report.SummarySourceJson = summarySourceJson;
        report.AggregateSourceReportIds = sourceSnapshot.ReportIds;
        report.AggregateSourceAssignmentIds = sourceSnapshot.AssignmentIds;
        report.AggregateSourceUpdatedAtUtc = now;
        report.AggregateSnapshotDirty = preserveAggregateDirty;
        report.AggregateSnapshotDirtyAtUtc =
            preserveAggregateDirty ? now : null;
        report.AggregateSnapshotRefreshedAtUtc = now;
        report.AggregateRefreshError = null;
        report.UpdatedAtUtc = now;
        report.UpdatedByUserId = actorUserId;

        await _sectionProjection.ProjectAndVerifyAsync(
            report,
            report.FieldValuesJson,
            report.TableValuesJson,
            actorUserId,
            now,
            ct);

        await InsertLogAsync(
            workId: report.WorkId,
            workAssignmentId: report.WorkAssignmentId,
            workReportPeriodId: report.WorkReportPeriodId,
            workAssignmentReportId: report.Id,
            action: "REFRESH_AGGREGATE_STACKED_DRAFT",
            fromStatus: report.Status.ToString(),
            toStatus: report.Status.ToString(),
            actionByUserId: actorUserId,
            reason: "SOURCE_CHANGED",
            comment: $"sourceReportCount={sourceSnapshot.ReportIds.Count}",
            snapshotJson: summarySourceJson,
            ct: ct);

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
        string targetDynamicFormTemplateId,
        string targetBlockId)
    {
        if (summary is null || summary.TargetIndexes.Count == 0)
            return false;

        if (!string.IsNullOrWhiteSpace(summary.TargetDynamicFormTemplateId) &&
            !string.Equals(summary.TargetDynamicFormTemplateId.Trim(), targetDynamicFormTemplateId, StringComparison.Ordinal))
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

    private async Task<HashSet<string>?> ResolveAssignmentScopeIdsAsync(
        string workId,
        string? scopeAssignmentId,
        CancellationToken ct)
    {
        var scopeId = NormalizeOptionalTextOrNull(scopeAssignmentId);
        if (string.IsNullOrWhiteSpace(scopeId))
            return null;

        var assignments = await _ctx.WorkAssignments
            .Find(x => x.WorkId == workId && !x.IsDeleted)
            .Project(x => new { x.Id, x.Path })
            .ToListAsync(ct);

        var scope = assignments.FirstOrDefault(x => string.Equals(x.Id, scopeId, StringComparison.Ordinal));
        if (scope is null)
            return new HashSet<string>(StringComparer.Ordinal);

        var scopePath = scope.Path?.Trim();
        return assignments
            .Where(x =>
                string.Equals(x.Id, scope.Id, StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(scopePath) &&
                 !string.IsNullOrWhiteSpace(x.Path) &&
                 x.Path.StartsWith($"{scopePath}/", StringComparison.Ordinal)))
            .Select(x => x.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static List<MyReportTemplateRow> BuildTemplateRowsFromReportPeriodRows(
        List<MyReportPeriodListDocRole> periodRows)
        => periodRows
            .GroupBy(x => BuildTemplateGroupKey(x.DynamicFormTemplateId, x.DynamicExcelId), StringComparer.Ordinal)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .Select(g =>
            {
                var rows = g.ToList();
                var latest = rows
                    .OrderByDescending(x => x.SortUpdatedAtUtc)
                    .ThenByDescending(x => x.SourceCreatedAtUtc)
                    .First();
                var latestPeriod = rows
                    .OrderByDescending(x => x.PeriodKey)
                    .ThenByDescending(x => x.SortUpdatedAtUtc)
                    .First();

                return new MyReportTemplateRow
                {
                    DynamicFormTemplateId = latest.DynamicFormTemplateId ?? string.Empty,
                    DynamicFormTemplateCode = latest.DynamicFormTemplateCode ?? string.Empty,
                    DynamicFormTemplateName = latest.DynamicFormTemplateName ?? string.Empty,
                    DynamicExcelId = latest.DynamicExcelId,
                    DynamicExcelCode = latest.DynamicExcelCode ?? string.Empty,
                    DynamicExcelName = latest.DynamicExcelName ?? string.Empty,
                    BindingCount = rows
                        .Select(x => x.WorkTemplateAssigneeId)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .Count(),
                    PeriodCount = rows
                        .Select(x => x.WorkReportPeriodId)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .Count(),
                    ReportCount = rows
                        .Select(x => x.CurrentReportId)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .Count(),
                    LatestPeriodId = latestPeriod.WorkReportPeriodId,
                    LatestPeriodKey = latestPeriod.PeriodKey,
                    LatestPeriodStatus = (int)latestPeriod.PeriodStatus,
                    LatestDueAtUtc = latestPeriod.DueAtUtc,
                    LatestReportId = latest.CurrentReportId,
                    LatestUpdatedAtUtc = latest.SortUpdatedAtUtc,
                    HasOverduePeriod = rows.Any(x => x.IsOverdue)
                };
            })
            .ToList();

    private static MyReportTemplateRow MapTemplateDocRoleToRow(MyReportTemplateListDocRole x)
        => new()
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
        };

    private static MyReportTemplateRow BuildTemplateRowFromBindings(List<WorkTemplateAssignee> bindings)
    {
        var latest = bindings
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.CreatedAtUtc)
            .First();

        return new MyReportTemplateRow
        {
            DynamicFormTemplateId = latest.DynamicFormTemplateId ?? string.Empty,
            DynamicFormTemplateCode = latest.DynamicFormTemplateCode ?? string.Empty,
            DynamicFormTemplateName = latest.DynamicFormTemplateName ?? string.Empty,
            DynamicExcelId = latest.DynamicExcelId,
            DynamicExcelCode = latest.DynamicExcelCode ?? string.Empty,
            DynamicExcelName = latest.DynamicExcelName ?? string.Empty,
            BindingCount = bindings
                .Select(x => x.Id)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .Count(),
            PeriodCount = 0,
            ReportCount = 0,
            LatestUpdatedAtUtc = latest.UpdatedAtUtc == default ? latest.CreatedAtUtc : latest.UpdatedAtUtc,
            HasOverduePeriod = false
        };
    }

    private static void MergeBindingMetadata(MyReportTemplateRow row, List<WorkTemplateAssignee> bindings)
    {
        var latest = bindings
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault();
        if (latest is null)
            return;

        row.DynamicFormTemplateId = FirstNonEmpty(row.DynamicFormTemplateId, latest.DynamicFormTemplateId);
        row.DynamicFormTemplateCode = FirstNonEmpty(row.DynamicFormTemplateCode, latest.DynamicFormTemplateCode);
        row.DynamicFormTemplateName = FirstNonEmpty(row.DynamicFormTemplateName, latest.DynamicFormTemplateName);
        row.DynamicExcelId = FirstNonEmpty(row.DynamicExcelId, latest.DynamicExcelId);
        row.DynamicExcelCode = FirstNonEmpty(row.DynamicExcelCode, latest.DynamicExcelCode);
        row.DynamicExcelName = FirstNonEmpty(row.DynamicExcelName, latest.DynamicExcelName);
        row.BindingCount = Math.Max(
            row.BindingCount,
            bindings
                .Select(x => x.Id)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .Count());

        var latestBindingUpdatedAt = latest.UpdatedAtUtc == default ? latest.CreatedAtUtc : latest.UpdatedAtUtc;
        if (!row.LatestUpdatedAtUtc.HasValue || row.LatestUpdatedAtUtc.Value < latestBindingUpdatedAt)
            row.LatestUpdatedAtUtc = latestBindingUpdatedAt;
    }

    private static string BuildTemplateGroupKey(string? dynamicFormTemplateId, string? dynamicExcelId)
    {
        var formId = NormalizeOptionalTextOrNull(dynamicFormTemplateId);
        if (!string.IsNullOrWhiteSpace(formId))
            return $"form:{formId}";

        var excelId = NormalizeOptionalTextOrNull(dynamicExcelId);
        return string.IsNullOrWhiteSpace(excelId) ? string.Empty : $"excel:{excelId}";
    }

    private static bool MatchesTemplateSearch(MyReportTemplateRow row, MyReportTemplateSearchRequest req)
    {
        if (req.HasOverduePeriod.HasValue && row.HasOverduePeriod != req.HasOverduePeriod.Value)
            return false;

        if (req.HasReport.HasValue && (row.ReportCount > 0) != req.HasReport.Value)
            return false;

        var q = NormalizeOptionalTextOrNull(req.Q);
        if (string.IsNullOrWhiteSpace(q))
            return true;

        return ContainsIgnoreCase(row.DynamicFormTemplateCode, q)
            || ContainsIgnoreCase(row.DynamicFormTemplateName, q)
            || ContainsIgnoreCase(row.DynamicExcelCode, q)
            || ContainsIgnoreCase(row.DynamicExcelName, q);
    }

    private static bool ContainsIgnoreCase(string? value, string q)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(q, StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(string? current, string? fallback)
        => string.IsNullOrWhiteSpace(current) ? fallback ?? string.Empty : current;

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
            "bindingcount" => x => x.BindingCount,
            "reportcount" => x => x.ReportCount,
            "latestperiodkey" => x => x.LatestPeriodKey,
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

    private static WorkReportPeriodStatus ResolveDraftPeriodStatus(
        bool isHistoricalData,
        DateTime? completedDate,
        DateTime? dueAtUtc,
        DateTime now)
        => WorkAssignmentReportHistoricalDataHelper.ResolveDraftPeriodStatus(
            isHistoricalData,
            completedDate,
            dueAtUtc,
            now);

    private static WorkReportPeriodStatus ResolveDraftPeriodStatus(
        WorkReportPeriod period,
        WorkAssignmentReport report,
        DateTime now)
        => WorkAssignmentReportHistoricalDataHelper.ResolveDraftPeriodStatus(
            report.IsHistoricalData || period.IsHistoricalData,
            report.CompletedDate ?? period.CompletedDate,
            report.DueAtUtc ?? period.DueAtUtc,
            now);

    private static WorkReportPeriodStatus ResolveSubmittedPeriodStatus(
        WorkReportPeriod period,
        WorkAssignmentReport report,
        DateTime now)
        => WorkAssignmentReportHistoricalDataHelper.ResolveSubmittedPeriodStatus(period, report, now);

    private static bool ResolveReportLateSubmission(
        bool isHistoricalData,
        DateTime? completedDate,
        DateTime? dueAtUtc,
        DateTime now)
        => WorkAssignmentReportHistoricalDataHelper.ResolveIsLateSubmission(
            isHistoricalData,
            completedDate,
            dueAtUtc,
            now);

    private static DateTime? NormalizeDate(DateTime? value)
        => WorkAssignmentReportHistoricalDataHelper.NormalizeDate(value);

    private static DateTime? ResolveServerReportStartedDate(
        WorkAssignmentReport report,
        WorkReportPeriod? period)
        => NormalizeDate(
            report.StartedDate
            ?? period?.StartedDate
            ?? report.PeriodStart
            ?? period?.PeriodStart
            ?? report.ReportDate
            ?? period?.ReportDate);

    private sealed record ReportCompletedDatePolicy(
        bool CanEditCompletedDate,
        bool RequiresCompletedDate,
        DateTime? CompletedDateMin,
        DateTime? CompletedDateMax,
        string Reason);

    private static ReportCompletedDatePolicy ResolveReportCompletedDatePolicy(
        WorkAssignment? assignment,
        WorkAssignmentReport? report,
        WorkReportPeriod? period,
        DateTime now)
    {
        if (assignment is null)
            return new ReportCompletedDatePolicy(false, false, null, null, "ASSIGNMENT_NOT_FOUND");

        var reportDate = ReportCivilDate.ReadPeriodDay(report?.ReportDate ?? period?.ReportDate);
        var sourceStart = ReportCivilDate.ReadPeriodDay(report?.PeriodStart ?? period?.PeriodStart ?? reportDate);
        var sourceEnd = ReportCivilDate.ReadPeriodDay(report?.PeriodEnd ?? period?.PeriodEnd ?? reportDate ?? sourceStart);
        var dueInstant = report?.DueAtUtc ?? period?.DueAtUtc;
        var sourceAnchor = dueInstant.HasValue ? ReportCivilDate.FromInstant(dueInstant.Value) : reportDate;

        if (!WorkAssignmentBackfillPeriodPolicy.TryResolveCompletedDateBounds(
                assignment,
                sourceStart,
                sourceEnd,
                sourceAnchor,
                now,
                out var minDate,
                out var maxDate))
        {
            return new ReportCompletedDatePolicy(false, false, null, null, "SCHEDULED_CURRENT");
        }

        return new ReportCompletedDatePolicy(
            true,
            true,
            minDate,
            maxDate,
            WorkAssignmentBackfillPeriodPolicy.CompletedDatePolicyReason);
    }

    private static DateTime? ValidateReportCompletedDateInput(
        ReportCompletedDatePolicy policy,
        DateTime? completedDate,
        object details,
        bool requireWhenMissing)
    {
        var normalized = NormalizeDate(completedDate);
        if (!normalized.HasValue)
        {
            if (requireWhenMissing && policy.RequiresCompletedDate)
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.WORK_ASSIGNMENT_REPORT_HISTORICAL_COMPLETED_DATE_REQUIRED,
                    details);

            return null;
        }

        if (!policy.CanEditCompletedDate)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_COMPLETED_DATE_NOT_ALLOWED,
                new { completedDate = normalized, policy, details });

        if ((policy.CompletedDateMin.HasValue && normalized.Value < policy.CompletedDateMin.Value.Date) ||
            (policy.CompletedDateMax.HasValue && normalized.Value > policy.CompletedDateMax.Value.Date))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_COMPLETED_DATE_OUT_OF_RANGE,
                new { completedDate = normalized, policy, details });
        }

        return normalized.Value;
    }

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
        ReportCompletedDatePolicy completedDatePolicy)
        => report.IsHistoricalData ||
           period?.IsHistoricalData == true ||
           IsBackfillCompletedDatePolicy(completedDatePolicy);

    private static bool IsBackfillCompletedDatePolicy(ReportCompletedDatePolicy policy)
        => string.Equals(
            policy.Reason,
            WorkAssignmentBackfillPeriodPolicy.CompletedDatePolicyReason,
            StringComparison.Ordinal);

    private static WorkReportPeriodStatus ResolveApprovedPeriodStatus(
        WorkReportPeriod period,
        WorkAssignmentReport report,
        DateTime now)
    {
        return WorkAssignmentReportHistoricalDataHelper.ResolveApprovedPeriodStatus(period, report, now);
    }

    private static string ResolveAutoApproveActorUserId(
        WorkAssignment assignment,
        string fallbackUserId)
        => string.IsNullOrWhiteSpace(WorkAssignmentCurrentAuthority.ReviewerId(assignment))
            ? fallbackUserId
            : WorkAssignmentCurrentAuthority.ReviewerId(assignment)!;

    private async Task EnsurePreviousReportsApprovedAsync(
        WorkReportPeriod? period,
        CancellationToken ct)
    {
        if (period is null)
            return;

        var previousOpenPeriod = await FindPreviousOpenPeriodAsync(period, ct);
        if (previousOpenPeriod is null)
            return;

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

    private async Task<WorkReportPeriod?> FindPreviousOpenPeriodAsync(
        WorkReportPeriod? period,
        CancellationToken ct)
    {
        if (period is null)
            return null;

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

        return candidates
            .Where(candidate => ComparePeriodOrder(candidate, period) < 0)
            .Where(candidate => !WorkReportPeriodStatusHelper.IsTerminal(candidate.Status))
            .OrderBy(ResolvePeriodOrder)
            .FirstOrDefault();
    }

    private async Task EnsureNoLaterApprovedReportsAsync(
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

        var laterApprovedPeriod = candidates
            .Where(candidate => ComparePeriodOrder(candidate, period) > 0)
            .Where(candidate => WorkReportPeriodStatusHelper.IsTerminal(candidate.Status))
            .OrderBy(ResolvePeriodOrder)
            .FirstOrDefault();

        if (laterApprovedPeriod is not null)
            throw AppExceptionFactory.Create(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_LATER_PERIOD_APPROVED,
                new
                {
                    periodId = period.Id,
                    periodInstanceKey = period.PeriodInstanceKey,
                    laterPeriodId = laterApprovedPeriod.Id,
                    laterPeriodInstanceKey = laterApprovedPeriod.PeriodInstanceKey,
                    laterPeriodStatus = laterApprovedPeriod.Status
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
        => period.PeriodStart
           ?? period.ReportDate
           ?? period.PeriodEnd
           ?? period.DueAtUtc
           ?? period.CreatedAtUtc;

    private static DateTime? ResolveEffectiveReportDueAtUtc(DateTime? reportDueAtUtc, WorkAssignment? assignment)
    {
        var assignmentDueAtUtc = ResolveAssignmentHardDueAtUtc(assignment);
        if (!reportDueAtUtc.HasValue)
            return assignmentDueAtUtc;

        return reportDueAtUtc.Value;
    }

    private static DateTime? ResolveAssignmentHardDueAtUtc(WorkAssignment? assignment)
    {
        if (assignment is null)
            return null;

        if (assignment.DueAtUtc.HasValue)
            return assignment.DueAtUtc.Value;

        return assignment.DueDate.HasValue
            ? NormalizeDueAtUtc(assignment.DueDate.Value)
            : null;
    }

    private static DateTime NormalizeDueAtUtc(DateTime date)
        => AppTimeRangeHelper.EndOfUtcDate(date);

    private static DynamicExcelDetail MapDynamicExcelDetail(DynamicExcelTemplate template)
        => new(
            template.Id,
            template.Code,
            template.Name,
            ReadDynamicExcelHeaderKind(template.SpecJson),
            string.IsNullOrWhiteSpace(template.TableMode) ? "FIXED_GRID" : template.TableMode,
            template.ContractVersion <= 0 ? 1 : template.ContractVersion,
            template.CreatedByUsername,
            template.CreatedAtUtc,
            template.RawWorkbookDataJson,
            template.SpecJson,
            new DynamicExcelDataRectDto(
                template.DataRectR0,
                template.DataRectC0,
                template.DataRectR1,
                template.DataRectC1),
            template.W,
            template.H);

    private static string? ReadDynamicExcelHeaderKind(string? specJson)
    {
        if (string.IsNullOrWhiteSpace(specJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(specJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var kind = ReadJsonString(document.RootElement, "kind")?.ToUpperInvariant();
            return kind is "TOP" or "LEFT" or "MATRIX" ? kind : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TemplateSnapshotDTO BuildTemplateSnapshot(DynamicExcelTemplate template)
    {
        return new TemplateSnapshotDTO
        {
            TemplateId = template.Id,
            Code = template.Code,
            Name = template.Name,
            SpecJson = template.SpecJson,
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
            AssignmentDueDate = assignment.DueDate,
            WeekDays = assignment.Schedule?.WeekDays?.ToArray() ?? Array.Empty<int>(),
            MonthDays = assignment.Schedule?.MonthDays?.ToArray() ?? Array.Empty<int>(),
            QuarterDays = assignment.Schedule?.QuarterDays?.ToArray() ?? Array.Empty<int>(),
            SemiAnnualDays = assignment.Schedule?.SemiAnnualDays?.ToArray() ?? Array.Empty<int>(),
            QuarterDayRules = assignment.Schedule?.QuarterDayRules?.Select(x =>
                new tdtd_be.DTOs.WorkAssignments.QuarterDayRuleDto(x.Quarter, (x.Days ?? Array.Empty<int>()).ToList()))
                .ToArray() ?? Array.Empty<tdtd_be.DTOs.WorkAssignments.QuarterDayRuleDto>(),
            SemiAnnualDayRules = assignment.Schedule?.SemiAnnualDayRules?.Select(x =>
                new tdtd_be.DTOs.WorkAssignments.SemiAnnualDayRuleDto(x.Half, (x.Days ?? Array.Empty<int>()).ToList()))
                .ToArray() ?? Array.Empty<tdtd_be.DTOs.WorkAssignments.SemiAnnualDayRuleDto>(),
            Note = assignment.Schedule?.Note,
            DynamicFormDataSourceRulesJson = assignment.DynamicFormDataSourceRulesJson
        };
    }

    private static List<decimal?> CreateEmptyValues1D(int w, int h)
    {
        var len = Math.Max(0, w) * Math.Max(0, h);
        return Enumerable.Range(0, len).Select(_ => (decimal?)null).ToList();
    }

    private static WorkReportPeriodRow MapToPeriodRow(
        WorkReportPeriod x,
        WorkAssignment? assignment,
        DateTime now,
        bool bindingActive)
    {
        var completedDatePolicy = ResolveReportCompletedDatePolicy(assignment, null, x, now);
        var isHistoricalData = x.IsHistoricalData || IsBackfillCompletedDatePolicy(completedDatePolicy);

        return new WorkReportPeriodRow
        {
            Id = x.Id,
            WorkId = x.WorkId,
            WorkAssignmentId = x.WorkAssignmentId,
            AssignmentType = assignment?.AssignmentType ?? string.Empty,
            WorkTemplateAssigneeId = x.WorkTemplateAssigneeId,
            IsActive = x.IsActive && assignment?.IsActive == true && bindingActive,
            DynamicExcelId = x.DynamicExcelId,
            DynamicExcelCode = x.DynamicExcelCode,
            DynamicExcelName = x.DynamicExcelName,
            AssigneeUserId = x.AssigneeUserId,
            PeriodKey = x.PeriodKey,
            PeriodInstanceKey = NormalizePeriodInstanceKey(x),
            PeriodKind = NormalizePeriodKind(x.PeriodKind),
            ReportTitle = x.ReportTitle,
            ReportDate = x.ReportDate,
            StartedDate = x.StartedDate,
            CompletedDate = x.CompletedDate,
            CanEditCompletedDate = completedDatePolicy.CanEditCompletedDate,
            RequiresCompletedDate = completedDatePolicy.RequiresCompletedDate,
            CompletedDateMin = completedDatePolicy.CompletedDateMin,
            CompletedDateMax = completedDatePolicy.CompletedDateMax,
            CompletedDatePolicyReason = completedDatePolicy.Reason,
            IsHistoricalData = isHistoricalData,
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
            LateReason = x.LateReason,
            ReviewerComment = x.ReviewerComment,
            ReturnReason = x.ReturnReason
        };
    }

    private static MyReportTemplateAssignmentOption MapToTemplateAssignmentOption(
        WorkTemplateAssignee binding,
        WorkAssignment? assignment)
        => new()
        {
            WorkAssignmentId = binding.WorkAssignmentId ?? string.Empty,
            WorkTemplateAssigneeId = binding.Id ?? string.Empty,
            AssignmentCode = assignment?.Code,
            AssignmentType = assignment?.AssignmentType ?? binding.AssignmentType,
            StartDate = assignment?.StartDate ?? binding.StartDate,
            DueDate = assignment?.DueDate ?? binding.DueDate,
            CompletedDate = assignment?.CompletedDate ?? binding.CompletedDate,
            DueAtUtc = assignment?.DueAtUtc,
            IsActive = assignment?.IsActive ?? binding.IsActive
        };

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
            DynamicFormFamilyId = x.DynamicFormFamilyId,
            DynamicFormVersionNo = x.DynamicFormVersionNo,
            DynamicFormSchemaHash = x.DynamicFormSchemaHash,
            DataOrigin = WorkReportDataOrigin.Normalize(x.DataOrigin),
            AggregateSnapshotDirty = x.AggregateSnapshotDirty,
            AggregateSnapshotDirtyAtUtc = x.AggregateSnapshotDirtyAtUtc,
            AggregateSnapshotRefreshedAtUtc = x.AggregateSnapshotRefreshedAtUtc,
            AggregateRefreshError = x.AggregateRefreshError,
            VersionNo = x.VersionNo,
            IsCurrent = x.IsCurrent,
            IsActive = x.IsActive,
            DeactivatedAtUtc = x.DeactivatedAtUtc,
            DeactivationReason = x.DeactivationReason,
            SubmittedAtUtc = x.SubmittedAtUtc,
            SubmittedByUserId = x.SubmittedByUserId,
            ApprovedAtUtc = x.ApprovedAtUtc,
            ApprovedByUserId = x.ApprovedByUserId,
            AutoApproved = WorkAssignmentAutoApprovalState.IsAutoApproved(x),
            AutoApprovedAtUtc = x.AutoApprovedAtUtc,
            AutoApprovedByUserId = x.AutoApprovedByUserId,
            AutoApprovalLocked = WorkAssignmentAutoApprovalState.IsLocked(x),
            AutoApprovalConfirmedAtUtc = x.AutoApprovalConfirmedAtUtc,
            AutoApprovalConfirmedByUserId = x.AutoApprovalConfirmedByUserId,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc
        };
    }

    private static System.Linq.Expressions.Expression<Func<MyReportPeriodListDocRole, WorkAssignmentReportListRow>> MapToListRowProjection()
        => x => new WorkAssignmentReportListRow
        {
            Id = x.CurrentReportId,
            WorkId = x.WorkId,
            WorkAssignmentId = x.AssignmentId,
            WorkReportPeriodId = x.WorkReportPeriodId,
            AssigneeUserId = x.AssigneeUserId,
            PeriodKey = x.PeriodKey,
            PeriodInstanceKey = x.PeriodInstanceKey,
            PeriodKind = x.PeriodKind,
            ReportTitle = x.ReportTitle,
            ReportDate = x.ReportDate,
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
            DynamicFormFamilyId = x.DynamicFormFamilyId,
            DynamicFormVersionNo = x.DynamicFormVersionNo,
            DynamicFormSchemaHash = x.DynamicFormSchemaHash,
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
            AutoApproved = x.AutoApproved,
            AutoApprovedAtUtc = x.AutoApprovedAtUtc,
            AutoApprovedByUserId = x.AutoApprovedByUserId,
            AutoApprovalLocked = x.AutoApprovalLocked,
            AutoApprovalConfirmedAtUtc = x.AutoApprovalConfirmedAtUtc,
            AutoApprovalConfirmedByUserId = x.AutoApprovalConfirmedByUserId,
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

    private async Task EnsureReportMutationScopeOpenAsync(
        WorkAssignment assignment,
        string actorUserId,
        CancellationToken ct, string? periodId = null)
    {
        if (await IsReportMutationScopeOpenAsync(assignment, ct, periodId))
            return;

        throw AppExceptionFactory.Create(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_SCOPE_COMPLETED_LOCKED,
            new { assignmentId = assignment.Id, assignment.WorkId, actorUserId });
    }

    private async Task<bool> IsReportMutationScopeOpenAsync(
        WorkAssignment assignment,
        CancellationToken ct, string? periodId = null)
    {
        if (string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
            return await tdtd_be.Services.WorkAssignments.Progress.WorkExecutionScopeGuard.IsOpenAsync(_ctx, assignment, periodId, ct);
        var work = await _ctx.Works
            .Find(x => x.Id == assignment.WorkId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (work is null ||
            work.CompletedAtUtc.HasValue ||
            work.Status == WorkStatus.S3 ||
            IsAssignmentManuallyCompleted(assignment))
        {
            return false;
        }

        var ancestorIds = ResolveAncestorIds(assignment);
        if (ancestorIds.Count == 0)
            return true;

        var hasCompletedAncestor = await _ctx.WorkAssignments
            .Find(x =>
                ancestorIds.Contains(x.Id) &&
                x.WorkId == assignment.WorkId &&
                !x.IsDeleted &&
                (x.CompletedAtUtc != null ||
                 (x.ProgressStatus == (int)WorkAssignmentProgressStatus.Completed && x.CompletedDate != null)))
            .Limit(1)
            .AnyAsync(ct);

        return !hasCompletedAncestor;
    }

    private static bool IsAssignmentManuallyCompleted(WorkAssignment assignment)
        => assignment.CompletedAtUtc.HasValue ||
           (assignment.ProgressStatus == (int)WorkAssignmentProgressStatus.Completed &&
            assignment.CompletedDate.HasValue);

    private static List<string> ResolveAncestorIds(WorkAssignment assignment)
    {
        if (string.IsNullOrWhiteSpace(assignment.Path))
            return new List<string>();

        return assignment.Path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.Equals(x, assignment.Id, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
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

    internal static AppException RawReportReadAccessForbidden()
        => AppExceptionFactory.Forbidden(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_ACCESS_FORBIDDEN);

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

    private static bool IsStackedAggregateSummary(string? summarySourceJson)
    {
        if (string.IsNullOrWhiteSpace(summarySourceJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(summarySourceJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   string.Equals(
                       ReadJsonString(doc.RootElement, "mapKind"),
                       "STACKED_TABLE",
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsDynamicFlowMappingSummary(string? summarySourceJson)
    {
        if (string.IsNullOrWhiteSpace(summarySourceJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(summarySourceJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   string.Equals(
                       ReadJsonString(document.RootElement, "kind"),
                       "DYNAMIC_FLOW_MAPPING",
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static void EnsureDynamicFlowMappingSummaryOverrideAllowed(
        string? currentSummarySourceJson,
        string? requestedSummarySourceJson,
        string reportId,
        string actorUserId)
    {
        if (requestedSummarySourceJson is null ||
            IsDynamicFlowMappingSummary(currentSummarySourceJson) ||
            !IsDynamicFlowMappingSummary(requestedSummarySourceJson))
        {
            return;
        }

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_SUMMARY_SOURCE_JSON_INVALID,
            new
            {
                reportId,
                actorUserId,
                field = "summarySourceJson",
                reason = "DYNAMIC_FLOW_MAPPING_METADATA_SERVER_OWNED"
            });
    }

    private static bool IsStackedAggregateSummary(AggregateDraftSummary summary)
        => string.Equals(summary.MapKind, "STACKED_TABLE", StringComparison.OrdinalIgnoreCase);

    private static bool AggregateSummaryReferencesSource(AggregateDraftSummary summary, WorkAssignmentReport source)
    {
        return summary.SourceReportIds.Any(id => string.Equals(id, source.Id, StringComparison.Ordinal))
               || summary.SourceAssignmentIds.Any(id => string.Equals(id, source.WorkAssignmentId, StringComparison.Ordinal));
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
        tdtd_be.Services.WorkAssignments.LegacyAggregateRetirement.RequireUnchangedLegacySummary(requested, current);
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
            if (!string.Equals(kind, "DYNAMIC_FORM_AGGREGATE_DRAFT", StringComparison.Ordinal) &&
                !string.Equals(kind, "DYNAMIC_FLOW_MAPPING", StringComparison.Ordinal))
            {
                return new AggregateSourceSnapshot(false, new List<string>(), new List<string>());
            }

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
                ReadJsonString(doc.RootElement, "mapKind"),
                ReadJsonString(doc.RootElement, "valueSelector"),
                ReadJsonString(doc.RootElement, "targetBlockId"),
                ReadJsonBool(doc.RootElement, "clearExistingValues"),
                ReadJsonIntArray(doc.RootElement, "targetIndexes"),
                ReadJsonStringArray(doc.RootElement, "sourceReportIds"),
                ReadJsonStringArray(doc.RootElement, "sourceAssignmentIds"),
                ReadJsonString(doc.RootElement, "targetDynamicFormTemplateId"),
                TryGetJsonProperty(doc.RootElement, "reportMapConfig", out var mapConfigElement) &&
                mapConfigElement.ValueKind == JsonValueKind.Object
                    ? mapConfigElement.GetRawText()
                    : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NormalizeReportMapConfigJson(string? value, string reportId)
    {
        var json = NormalizeOptionalTextOrNull(value);
        if (json is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            // Throw the coded error below so FE and automation receive a stable contract.
        }

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_SUMMARY_SOURCE_JSON_INVALID,
            new { field = "reportMapConfigJson", reportId });
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

        var runtimeDataRect = ReadRuntimeDataRect(element);
        if (RuntimeRectWidth(runtimeDataRect) <= 0 || RuntimeRectHeight(runtimeDataRect) <= 0)
            runtimeDataRect = new RuntimeDataRect(0, 0, Math.Max(0, h - 1), Math.Max(0, w - 1));
        var indexMap = ReadAggregateDraftIndexMap(element, blockId, tableMode, w, h, runtimeDataRect);
        var valueLength = ResolveRuntimeInputCells(element, runtimeDataRect, w, h).Count;
        var dynamicExcelTemplateId = ReadJsonString(element, "dynamicExcelTemplateId")
            ?? ReadJsonString(element, "excelBlockDynamicExcelTemplateId");

        return new AggregateDraftBlockContract(blockId, tableMode, w, h, valueLength, dynamicExcelTemplateId, dataRect, indexMap);
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
        int h,
        RuntimeDataRect dataRect)
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

        return ResolveRuntimeInputCells(block, dataRect, w, h)
            .Select(cell =>
            {
                var rowKey = $"row_{cell.R - dataRect.R0 + 1}";
                var columnKey = $"col_{cell.C - dataRect.C0 + 1}";
                return new AggregateDraftIndexMapItem(cell.Index, rowKey, columnKey, BuildAggregateMetricKey(blockId, rowKey, columnKey));
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
        => Values1DCompression.DeserializeDecimals(json, _jsonOptions);

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

                return Values1DCompression.ReadBlockDecimals(block) ?? new List<decimal?>();
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

    private static DynamicFormAggregateResponse ResolveMetricDraftAggregate(
        DynamicFormAggregateResponse aggregate,
        AggregateDraftBlockContract block,
        string valueSelector)
    {
        if (aggregate.StackedTable is null ||
            string.Equals(block.TableMode, "APPEND_ROWS", StringComparison.Ordinal))
        {
            return aggregate;
        }

        var stackedRows = BuildAggregateRowsFromStackedTable(aggregate.StackedTable, block);
        if (stackedRows.Count == 0 ||
            !stackedRows.Any(row => SelectAggregateDraftValue(row, valueSelector).HasValue))
        {
            return aggregate;
        }

        return new DynamicFormAggregateResponse
        {
            Meta = aggregate.Meta,
            Columns = aggregate.Columns,
            Rows = stackedRows,
            StackedTable = aggregate.StackedTable,
            Sources = aggregate.Sources,
            Warnings = aggregate.Warnings
        };
    }

    private static List<DynamicFormAggregateRowDto> BuildAggregateRowsFromStackedTable(
        DynamicFormStackedTableDto stacked,
        AggregateDraftBlockContract block)
    {
        return block.IndexMap
            .OrderBy(x => x.Index)
            .Select(metric =>
            {
                var count = 0;
                var numericCount = 0;
                var sum = 0m;
                decimal? min = null;
                decimal? max = null;

                foreach (var row in stacked.Rows)
                {
                    if (!row.Cells.TryGetValue(metric.MetricKey, out var raw) ||
                        !HasStackedMetricCellValue(raw))
                    {
                        continue;
                    }

                    count++;
                    var number = ToNullableDecimalObject(raw);
                    if (!number.HasValue)
                        continue;

                    numericCount++;
                    sum += number.Value;
                    min = min.HasValue ? Math.Min(min.Value, number.Value) : number.Value;
                    max = max.HasValue ? Math.Max(max.Value, number.Value) : number.Value;
                }

                return new DynamicFormAggregateRowDto
                {
                    MetricKey = metric.MetricKey,
                    RowKey = metric.RowKey,
                    ColumnKey = metric.ColumnKey,
                    Index = metric.Index,
                    Label = $"{metric.RowKey} / {metric.ColumnKey}",
                    Count = count,
                    Sum = numericCount > 0 ? sum : null,
                    Min = min,
                    Max = max,
                    Average = numericCount > 0 ? sum / numericCount : null
                };
            })
            .ToList();
    }

    private static bool HasStackedMetricCellValue(object? value)
    {
        return value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            JsonElement element => element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => false,
                JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
                JsonValueKind.Array => element.EnumerateArray().Any(x => HasStackedMetricCellValue(x)),
                _ => true
            },
            IEnumerable<object?> list => list.Any(HasStackedMetricCellValue),
            _ => true
        };
    }

    private static decimal? ToNullableDecimalObject(object? value)
    {
        return value switch
        {
            null => null,
            decimal v => v,
            int v => v,
            long v => v,
            double v when !double.IsNaN(v) && !double.IsInfinity(v) => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
            float v when !float.IsNaN(v) && !float.IsInfinity(v) => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
            JsonElement element => ToNullableDecimal(element),
            string text when decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
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

    private static string BuildStackedAggregateDraftTableValuesJson(
        WorkAssignmentReport report,
        DynamicFormTemplate form,
        AggregateDraftBlockContract block,
        List<object?> values,
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

        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            var item = blocks[i] as JsonObject;
            var itemBlockId = item?["blockId"]?.GetValue<string>();
            if (string.Equals(NormalizeBlockId(itemBlockId), block.BlockId, StringComparison.Ordinal))
                blocks.RemoveAt(i);
        }

        blocks.Add(BuildStackedAggregateDraftTableBlockNode(block, values, aggregate));
        return root.ToJsonString(_jsonOptions);
    }

    private static List<object?> BuildStackedAggregateValues(
        DynamicFormStackedTableDto stacked,
        int width,
        int? minimumRows = null)
    {
        var columns = stacked.Columns.Take(width).ToList();
        var rowCount = Math.Max(minimumRows ?? 0, stacked.Rows.Count);
        var values = new List<object?>(Math.Max(0, rowCount * width));
        foreach (var row in stacked.Rows)
        {
            foreach (var column in columns)
            {
                values.Add(row.Cells.TryGetValue(column.Key, out var value) ? value : null);
            }

            while (values.Count % width != 0)
                values.Add(null);
        }

        while (values.Count < rowCount * width)
            values.Add(null);

        return values;
    }

    private static JsonNode BuildExpandedAggregateDraftDataRect(
        JsonNode? current,
        int width,
        int height)
    {
        var r0 = 0;
        var c0 = 0;
        if (current is JsonObject obj)
        {
            r0 = ReadJsonNodeInt(obj, "r0") ?? ReadJsonNodeInt(obj, "R0") ?? 0;
            c0 = ReadJsonNodeInt(obj, "c0") ?? ReadJsonNodeInt(obj, "C0") ?? 0;
        }

        return new JsonObject
        {
            ["r0"] = r0,
            ["c0"] = c0,
            ["r1"] = r0 + Math.Max(1, height) - 1,
            ["c1"] = c0 + Math.Max(1, width) - 1
        };
    }

    private static int? ReadJsonNodeInt(JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var node) &&
           node is JsonValue value &&
           value.TryGetValue<int>(out var number)
            ? number
            : null;

    private static string? ReadJsonNodeString(JsonNode? node, string key)
        => node is JsonObject obj &&
           obj.TryGetPropertyValue(key, out var child) &&
           child is JsonValue value &&
           value.TryGetValue<string>(out var text)
            ? NormalizeOptionalTextOrNull(text)
            : null;

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

    private static List<object?> MergeDraftValuesPatch(
        string? currentValuesJson,
        int? expectedLength,
        IReadOnlyCollection<WorkReportValuePatchItem>? patch)
    {
        var values = JsonSerializer.SerializeToNode(
            Values1DCompression.DeserializeObjects(currentValuesJson, _jsonOptions),
            _jsonOptions) as JsonArray ?? new JsonArray();
        var length = expectedLength.HasValue && expectedLength.Value >= 0
            ? expectedLength.Value
            : values.Count;

        while (values.Count < length)
            values.Add(null);
        while (values.Count > length)
            values.RemoveAt(values.Count - 1);

        foreach (var item in patch ?? Array.Empty<WorkReportValuePatchItem>())
        {
            if (item.Index < 0 || item.Index >= length)
                throw InvalidDraftPatch(
                    "values1DPatch index nằm ngoài độ dài values1D.",
                    new { item.Index, values1DLength = length });

            values[item.Index] = CloneJsonElementToNode(item.Value);
        }

        return JsonSerializer.Deserialize<List<object?>>(values.ToJsonString(_jsonOptions), _jsonOptions)
               ?? new List<object?>();
    }

    private static string? MergeDraftTableBlockPatches(
        WorkAssignmentReport report,
        IReadOnlyCollection<WorkReportTableBlockPatch>? patches)
    {
        if (patches is null || patches.Count == 0)
            return report.TableValuesJson;

        var root = ParseTableValuesRoot(report.TableValuesJson) ?? new JsonObject();
        EnsureTableRootMetadata(root, report);

        if (root["blocks"] is not JsonArray blocks)
        {
            blocks = new JsonArray();
            root["blocks"] = blocks;
        }

        foreach (var patch in patches)
        {
            if (string.IsNullOrWhiteSpace(patch.BlockJson))
                throw InvalidDraftPatch("tableBlockPatches.blockJson không được trống.", new { patch.BlockId });

            JsonObject block;
            try
            {
                block = JsonNode.Parse(patch.BlockJson) as JsonObject
                        ?? throw InvalidDraftPatch("tableBlockPatches.blockJson phải là JSON object.", new { patch.BlockId });
            }
            catch (JsonException ex)
            {
                throw InvalidDraftPatch(
                    "tableBlockPatches.blockJson không phải JSON hợp lệ.",
                    new { patch.BlockId, ex.Message });
            }

            var blockId = NormalizeBlockId(ReadJsonNodeString(block, "blockId") ?? patch.BlockId);
            if (string.IsNullOrWhiteSpace(blockId))
                throw InvalidDraftPatch("tableBlockPatches.blockId không được trống.", null);
            block["blockId"] = blockId;

            var replaced = false;
            for (var index = 0; index < blocks.Count; index++)
            {
                if (blocks[index] is not JsonObject existing)
                    continue;

                var existingBlockId = NormalizeBlockId(ReadJsonNodeString(existing, "blockId"));
                if (!string.Equals(existingBlockId, blockId, StringComparison.Ordinal))
                    continue;

                blocks[index] = block;
                replaced = true;
                break;
            }

            if (!replaced)
                blocks.Add(block);
        }

        root["updatedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return root.ToJsonString(_jsonOptions);
    }

    private static JsonArray ParseJsonArrayOrEmpty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JsonArray();

        try
        {
            return JsonNode.Parse(json) as JsonArray ?? new JsonArray();
        }
        catch (JsonException)
        {
            return new JsonArray();
        }
    }

    private static JsonNode? CloneJsonElementToNode(JsonElement element)
        => element.ValueKind == JsonValueKind.Undefined
            ? null
            : JsonNode.Parse(element.GetRawText());

    private static void EnsureTableRootMetadata(JsonObject root, WorkAssignmentReport report)
    {
        if (root["dynamicFormTemplateId"] is null && !string.IsNullOrWhiteSpace(report.DynamicFormTemplateId))
            root["dynamicFormTemplateId"] = report.DynamicFormTemplateId;
        if (root["dynamicFormTemplateCode"] is null && !string.IsNullOrWhiteSpace(report.DynamicFormTemplateCode))
            root["dynamicFormTemplateCode"] = report.DynamicFormTemplateCode;
        if (root["dynamicFormTemplateName"] is null && !string.IsNullOrWhiteSpace(report.DynamicFormTemplateName))
            root["dynamicFormTemplateName"] = report.DynamicFormTemplateName;
    }

    private static string? ReadJsonNodeString(JsonObject obj, string name)
        => obj.TryGetPropertyValue(name, out var value) &&
           value is JsonValue jsonValue &&
           jsonValue.TryGetValue<string>(out var text)
            ? NormalizeOptionalTextOrNull(text)
            : null;

    private static AppException InvalidDraftPatch(string message, object? details)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            details,
            message);

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

    private static JsonObject BuildStackedAggregateDraftTableBlockNode(
        AggregateDraftBlockContract block,
        List<object?> values,
        DynamicFormAggregateResponse aggregate)
    {
        var stacked = aggregate.StackedTable ?? new DynamicFormStackedTableDto();
        var indexMap = BuildStackedAggregateDraftIndexMap(stacked, block.W);
        return new JsonObject
        {
            ["blockId"] = block.BlockId,
            ["dynamicExcelTemplateId"] = block.DynamicExcelTemplateId,
            ["tableMode"] = "APPEND_ROWS",
            ["w"] = block.W,
            ["h"] = block.H,
            ["dataRect"] = block.DataRect?.DeepClone() ?? BuildDefaultDataRectNode(block.W, block.H),
            ["values1D"] = JsonSerializer.SerializeToNode(values, _jsonOptions),
            ["indexMap"] = JsonSerializer.SerializeToNode(indexMap, _jsonOptions),
            ["metricDefinitions"] = JsonSerializer.SerializeToNode(BuildStackedAggregateMetricDefinitions(stacked, block.BlockId, block.W), _jsonOptions),
            ["rowLabels"] = new JsonArray(),
            ["rows"] = JsonSerializer.SerializeToNode(BuildStackedAggregateAppendRows(block, values), _jsonOptions),
            ["columns"] = new JsonArray(),
            ["cells"] = new JsonArray(),
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
                aggregate.Meta.MetricCount,
                stacked.SourceTableMode,
                stacked.RowMode
            }, _jsonOptions)
        };
    }

    private static List<AggregateDraftIndexMapItem> BuildStackedAggregateDraftIndexMap(
        DynamicFormStackedTableDto stacked,
        int width)
    {
        return stacked.Columns
            .Take(width)
            .Select((column, index) => new AggregateDraftIndexMapItem(
                index,
                "APPEND_ROWS",
                $"col_{index + 1}",
                column.MetricKey ?? $"aggregate:{NormalizeMetricPart(column.Key, $"col_{index + 1}")}"))
            .ToList();
    }

    private static List<object> BuildStackedAggregateMetricDefinitions(
        DynamicFormStackedTableDto stacked,
        string blockId,
        int width)
    {
        return stacked.Columns
            .Take(width)
            .Select((column, index) => new
            {
                blockId,
                metricKey = column.MetricKey ?? $"aggregate:{NormalizeMetricPart(column.Key, $"col_{index + 1}")}",
                rowKey = "APPEND_ROWS",
                columnKey = $"col_{index + 1}",
                index,
                displayLabel = column.Label,
                dataType = column.Type == "number" ? "NUMBER" : "SHORT_TEXT",
                sourceKind = "APPEND_ROWS",
                supportedOps = column.Type == "number"
                    ? new[] { "count", "sum", "min", "max", "average" }
                    : new[] { "count" }
            })
            .Cast<object>()
            .ToList();
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

    private static List<object> BuildStackedAggregateAppendRows(AggregateDraftBlockContract block, List<object?> values)
    {
        var rows = new List<object>();
        var width = Math.Max(1, block.W);
        var rowCount = values.Count == 0 ? 0 : (int)Math.Ceiling(values.Count / (double)width);
        for (var r = 0; r < rowCount; r++)
        {
            var cells = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var c = 0; c < width; c++)
            {
                var index = r * width + c;
                if (index >= values.Count)
                    break;

                var value = values[index];
                if (value is not null)
                    cells[$"col_{c + 1}"] = value;
            }

            rows.Add(new
            {
                rowInstanceId = $"{block.BlockId}:stack-row:{r + 1}",
                rowOrder = r + 1,
                rowKey = $"stack:R{r + 1}",
                rowLabelCodes = Array.Empty<string>(),
                cells
            });
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

    private static string? BuildStackedAggregateDraftContributionPolicyJson(
        string dataOrigin,
        DynamicFormStackedTableDto stacked,
        string blockId)
    {
        if (!string.Equals(WorkReportDataOrigin.Normalize(dataOrigin), WorkReportDataOrigin.PartialMapping, StringComparison.Ordinal))
            return null;

        var rules = stacked.Columns
            .Where(x => string.Equals(x.Role, "METRIC", StringComparison.OrdinalIgnoreCase))
            .Select(x => new
            {
                targetKind = "TABLE_METRIC",
                blockId,
                metricKey = x.MetricKey ?? x.Key,
                rowKey = "APPEND_ROWS",
                columnKey = NormalizeOptionalTextOrNull(x.SourceKey),
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
        bool clearExistingValues,
        string targetDynamicFormTemplateId,
        string? reportMapConfigJson)
    {
        var sourceReportIds = aggregate.Sources
            .Select(x => x.ReportId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sourceAssignmentIdList = aggregate.Sources
            .Select(x => x.WorkAssignmentId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();
        var targetIndexes = aggregate.Rows
            .Select(row => ResolveAggregateDraftValueIndex(row, block))
            .Where(index => index >= 0 && index < block.ValueLength)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        var reportMapConfig = string.IsNullOrWhiteSpace(reportMapConfigJson)
            ? null
            : JsonNode.Parse(reportMapConfigJson);
        var configuredSourceAssignmentId = ReadJsonNodeString(reportMapConfig, "sourceAssignmentId");
        if (!string.IsNullOrWhiteSpace(configuredSourceAssignmentId))
            sourceAssignmentIdList.Add(configuredSourceAssignmentId);
        var sourceAssignmentIds = sourceAssignmentIdList
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            kind = "DYNAMIC_FORM_AGGREGATE_DRAFT",
            mapKind = string.IsNullOrWhiteSpace(reportMapConfigJson) ? null : "REPORT_TABLE_TO_TABLE",
            dataOrigin = WorkReportDataOrigin.Normalize(dataOrigin),
            appliedAtUtc = DateTime.UtcNow,
            valueSelector,
            targetBlockId,
            targetDynamicFormTemplateId,
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
                    : aggregateRequest.SelectedUnitIds,
                AggregateConfigId = aggregateRequest.AggregateConfigId,
                IdentityColumns = aggregate.Meta.IdentityColumns
            },
            sourceReportIds,
            sourceAssignmentIds,
            reportMapConfig,
            targetIndexes,
            rowCount = aggregate.Rows.Count,
            sourceReportCount = aggregate.Sources.Count
        }, _jsonOptions);
    }

    private static string BuildStackedAggregateDraftSummarySourceJson(
        string dataOrigin,
        DynamicFormAggregateRequest aggregateRequest,
        DynamicFormAggregateResponse aggregate,
        string valueSelector,
        string targetBlockId,
        string targetDynamicFormTemplateId,
        string? reportMapConfigJson)
    {
        var sourceReportIds = aggregate.Sources
            .Select(x => x.ReportId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sourceAssignmentIdList = aggregate.Sources
            .Select(x => x.WorkAssignmentId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();
        var stacked = aggregate.StackedTable ?? new DynamicFormStackedTableDto();
        var reportMapConfig = string.IsNullOrWhiteSpace(reportMapConfigJson)
            ? null
            : JsonNode.Parse(reportMapConfigJson);
        var configuredSourceAssignmentId = ReadJsonNodeString(reportMapConfig, "sourceAssignmentId");
        if (!string.IsNullOrWhiteSpace(configuredSourceAssignmentId))
            sourceAssignmentIdList.Add(configuredSourceAssignmentId);
        var sourceAssignmentIds = sourceAssignmentIdList
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            kind = "DYNAMIC_FORM_AGGREGATE_DRAFT",
            mapKind = string.IsNullOrWhiteSpace(reportMapConfigJson) ? "STACKED_TABLE" : "REPORT_TABLE_TO_TABLE",
            dataOrigin = WorkReportDataOrigin.Normalize(dataOrigin),
            appliedAtUtc = DateTime.UtcNow,
            valueSelector,
            targetBlockId,
            targetDynamicFormTemplateId,
            clearExistingValues = true,
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
                    : aggregateRequest.SelectedUnitIds,
                AggregateConfigId = aggregateRequest.AggregateConfigId,
                IdentityColumns = aggregate.Meta.IdentityColumns
            },
            sourceReportIds,
            sourceAssignmentIds,
            reportMapConfig,
            stackedColumns = stacked.Columns.Select(x => new { x.Key, x.Label, x.Role, x.MetricKey, x.SourceKey }).ToArray(),
            rowCount = stacked.Rows.Count,
            sourceReportCount = aggregate.Sources.Count
        }, _jsonOptions);
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
        => WorkReportPeriodKind.Scheduled;

    private sealed record AggregateDraftBlockContract(
        string BlockId,
        string TableMode,
        int W,
        int H,
        int ValueLength,
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
        string? MapKind,
        string? ValueSelector,
        string? TargetBlockId,
        bool? ClearExistingValues,
        List<int> TargetIndexes,
        List<string> SourceReportIds,
        List<string> SourceAssignmentIds,
        string? TargetDynamicFormTemplateId,
        string? ReportMapConfigJson);

    private readonly record struct RuntimeDataRect(
        int R0,
        int C0,
        int R1,
        int C1);

    private sealed record RuntimeInputCellRef(
        int Index,
        int R,
        int C);

    private sealed record RuntimeSpecialCellMask(
        RuntimeDataRect DataRect,
        int Width,
        bool[] Flags,
        int MaskedCount);

    private sealed record RuntimeFieldContract(
        string Id,
        string Key,
        string DisplayName,
        string FieldType,
        string DataType,
        bool Required,
        RuntimeOption[] Options,
        string? EnumCatalogId,
        string? ValueSourceType);

    private sealed record RuntimeOption(
        string Code,
        string Label);

    private sealed record PayloadMutationCommand(
        int ExpectedPayloadRevision,
        string CommandId,
        string Operation,
        string CommandHash);

    private sealed record AggregateRefreshFence(
        bool BlockedByCompletedFlow,
        WorkReportLifecycleSeriesLease? Lease);

    private enum PayloadCommandReservationOutcome
    {
        Reserved,
        CompletedReplay
    }

    private sealed record RuntimeCellContract(
        string DataType,
        RuntimeOption[] Options,
        string? EnumCatalogId,
        string? ValueSourceType);

    private sealed record RuntimeTableBlockContract(
        string BlockId,
        int W,
        int H,
        RuntimeDataRect DataRect,
        JsonElement Block);

    private sealed record DynamicFormAggregateDraftProjection(
        List<object?> TopLevelValues,
        string TableValuesJson,
        string DataOrigin,
        string ContributionMode,
        string? ContributionPolicyJson,
        string SummarySourceJson);

    private sealed record DynamicFlowMappingProjectionResult(
        DynamicFlowMappingPreviewResponse Projection,
        string? TopLevelBlockId,
        DynamicFlowMappingRuntimeContext Runtime,
        IReadOnlyList<DynamicFlowMappingSourceReport> Sources,
        DynamicFlowPolicyEvaluationResult Permissions);

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
