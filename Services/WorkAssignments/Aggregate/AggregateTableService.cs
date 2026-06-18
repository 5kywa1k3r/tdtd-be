using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.WorkAssignments.Aggregate;
using tdtd_be.DTOs.WorkAssignments.AggregateTable;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Services;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.WorkAssignments.Aggregate;

public sealed class AggregateTableService : IAggregateTableService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] DefaultStackIdentityColumns =
    {
        "periodKey",
        "unitSymbol",
        "unitShortName",
        "fullName",
        "userName",
        "sourceReportCount"
    };

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly IUnitSelectionService _unitSelection;
    private readonly IWorkReportPayloadReader _payloadReader;

    public AggregateTableService(
        MongoDbContext ctx,
        MeAccessor me,
        IUnitSelectionService unitSelection,
        IWorkReportPayloadReader payloadReader)
    {
        _ctx = ctx;
        _me = me;
        _unitSelection = unitSelection;
        _payloadReader = payloadReader;
    }

    public async Task<AggregateTableResponse> GetTableAsync(
        AggregateTableRequest req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var normalized = NormalizeRequest(req);

        ValidateAggregateRequest(normalized);

        await LoadAggregateParentAsync(normalized.ParentAssignmentId!, me.Id, ct, allowBranchRead: true);
        normalized.SelectedUnitIds = await ResolveSelectedUnitIdsAsync(normalized.SelectedUnitIds, ct);

        var assignments = await LoadAggregateChildrenAsync(
            normalized.ParentAssignmentId!,
            normalized.DynamicExcelId!,
            normalized.SelectedUnitIds,
            ct);

        if (assignments.Count == 0)
            return BuildEmptyAggregateResponse(normalized);

        var dynamicExcelTemplate = await LoadDynamicExcelTemplateAsync(normalized.DynamicExcelId!, ct);
        var effectiveReports = await LoadAggregateReportsAsync(assignments, normalized, ct);
        var sources = BuildAggregateSources(assignments, effectiveReports);

        if (effectiveReports.Count == 0)
            return BuildEmptyAggregateResponse(normalized, assignments[0], sources);

        return normalized.AggregateMode switch
        {
            "SUM_BY_CELL" => BuildSumByCellResult(normalized, assignments, effectiveReports, sources),
            "HORIZONTAL_BY_USER" => BuildRowsByUserResult(normalized, assignments, effectiveReports, sources),
            "VERTICAL_BY_USER" => BuildColumnsByUserResult(normalized, assignments, effectiveReports, sources),
            _ => throw InvalidAggregateMode(normalized.AggregateMode)
        };
    }

    public async Task<DynamicFormAggregateResponse> GetDynamicFormAggregateAsync(
        DynamicFormAggregateRequest req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var normalized = NormalizeDynamicFormRequest(req);

        ValidateDynamicFormRequest(normalized);

        var scopeRoot = await LoadAggregateParentAsync(normalized.ScopeAssignmentId, me.Id, ct, allowBranchRead: true);

        var template = await _ctx.DynamicFormTemplates
            .Find(x =>
                x.Id == normalized.DynamicFormTemplateId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND_OR_UNPUBLISHED,
                new { dynamicFormTemplateId = normalized.DynamicFormTemplateId });

        var contract = ResolveDynamicFormTableContract(template, normalized.BlockId, normalized.TableMode);
        var requestedMetricKeys = (normalized.MetricKeys ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var metrics = requestedMetricKeys.Count == 0
            ? contract.IndexMap
            : contract.IndexMap
                .Where(x => requestedMetricKeys.Contains(x.MetricKey))
                .ToList();

        if (metrics.Count == 0 && requestedMetricKeys.Count > 0)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_METRIC_KEY_NOT_FOUND,
                new
                {
                    normalized.DynamicFormTemplateId,
                    normalized.BlockId,
                    normalized.TableMode,
                    requestedMetricKeys
                });

        var warnings = new List<string>();
        if (metrics.Count == 0)
        {
            warnings.Add("Bảng Excel động chưa cấu hình chỉ tiêu thống kê nên không có dữ liệu tổng hợp bảng.");
            return BuildEmptyDynamicFormResponse(normalized, template, contract, metrics, warnings);
        }

        normalized.SelectedUnitIds = await ResolveSelectedUnitIdsAsync(normalized.SelectedUnitIds, ct);

        var assignments = await LoadDynamicFormAggregateAssignmentsAsync(
            scopeRoot,
            normalized.ScopeMode ?? "DIRECT_CHILDREN",
            normalized.DynamicFormTemplateId,
            normalized.SelectedUnitIds,
            ct);

        if (assignments.Count == 0)
        {
            return BuildEmptyDynamicFormResponse(normalized, template, contract, metrics, warnings);
        }

        var reports = await LoadDynamicFormAggregateReportsAsync(assignments, normalized, ct);
        var sources = BuildAggregateSources(assignments, reports);
        var stackedTable = contract.TableMode is "APPEND_ROWS" or "APPEND_COLUMNS"
            ? BuildDynamicFormStackedTable(assignments, reports, contract, metrics, normalized, warnings)
            : null;
        var rows = contract.TableMode == "SUMMARY_TEMPLATE"
            ? await BuildDynamicFormSummaryTemplateRowsAsync(scopeRoot, assignments, normalized, contract, metrics, warnings, ct)
            : await BuildDynamicFormProjectedMetricRowsAsync(reports, contract, metrics, warnings, ct)
                ?? BuildDynamicFormMetricRows(reports, contract, metrics, warnings);

        return new DynamicFormAggregateResponse
        {
            Meta = BuildDynamicFormMeta(
                normalized,
                template,
                contract,
                metrics.Count,
                assignments.Count,
                reports.Count),
            Columns = BuildDynamicFormColumns(),
            Rows = rows,
            StackedTable = stackedTable,
            Sources = sources,
            Warnings = warnings
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList(),
        };
    }

    public async Task<WorkAssignmentAggregateConfigDto?> GetAggregateConfigAsync(
        string assignmentId,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var assignment = await LoadAggregateParentAsync(assignmentId, me.Id, ct, allowBranchRead: true);
        var config = await _ctx.WorkAssignmentAggregateConfigs
            .Find(x => x.AssignmentId == assignment.Id && x.IsActive && !x.IsDeleted)
            .SortByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct);

        return config is null ? null : MapAggregateConfig(config);
    }

    public async Task<WorkAssignmentAggregateConfigDto> SaveAggregateConfigAsync(
        string assignmentId,
        SaveWorkAssignmentAggregateConfigRequest req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var assignment = await LoadAggregateParentAsync(assignmentId, me.Id, ct, allowBranchRead: false);
        var now = DateTime.UtcNow;
        var existing = await _ctx.WorkAssignmentAggregateConfigs
            .Find(x => x.AssignmentId == assignment.Id && x.IsActive && !x.IsDeleted)
            .SortByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct);

        var entity = existing ?? new WorkAssignmentAggregateConfig
        {
            Id = ObjectId.GenerateNewId().ToString(),
            WorkId = assignment.WorkId,
            AssignmentId = assignment.Id,
            CreatedAtUtc = now,
            CreatedByUserId = me.Id,
            IsDeleted = false,
            IsActive = true,
        };

        entity.SourceDynamicFormTemplateId = NormalizeOptionalText(req.SourceDynamicFormTemplateId);
        entity.SourceBlockId = NormalizeOptionalText(req.SourceBlockId);
        entity.SourceTableMode = NormalizeOptionalText(req.SourceTableMode)?.ToUpperInvariant();
        entity.TargetDynamicFormTemplateId = NormalizeOptionalText(req.TargetDynamicFormTemplateId);
        entity.TargetBlockId = NormalizeOptionalText(req.TargetBlockId);
        entity.AggregateKind = NormalizeAggregateConfigKind(req.AggregateKind);
        entity.IdentityColumns = NormalizeIdentityColumns(req.IdentityColumns);
        entity.PeriodAggregationRule = NormalizeOptionalText(req.PeriodAggregationRule) ?? "STACK_SINGLE_PERIOD_SUM_RANGE";
        entity.MetricMappingsJson = NormalizeOptionalText(req.MetricMappingsJson);
        entity.VersionNo = existing is null ? 1 : existing.VersionNo + 1;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = me.Id;

        if (existing is null)
            await _ctx.WorkAssignmentAggregateConfigs.InsertOneAsync(entity, cancellationToken: ct);
        else
            await _ctx.WorkAssignmentAggregateConfigs.ReplaceOneAsync(x => x.Id == entity.Id, entity, cancellationToken: ct);

        return MapAggregateConfig(entity);
    }

    private static string NormalizeAggregateConfigKind(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return normalized is "AUTO_MAP" or "MANUAL_MAP" or "STACK_ROWS" or "STACK_COLUMNS"
            ? normalized
            : "AUTO_MAP";
    }

    private static WorkAssignmentAggregateConfigDto MapAggregateConfig(WorkAssignmentAggregateConfig x)
        => new()
        {
            Id = x.Id,
            WorkId = x.WorkId,
            AssignmentId = x.AssignmentId,
            SourceDynamicFormTemplateId = x.SourceDynamicFormTemplateId,
            SourceBlockId = x.SourceBlockId,
            SourceTableMode = x.SourceTableMode,
            TargetDynamicFormTemplateId = x.TargetDynamicFormTemplateId,
            TargetBlockId = x.TargetBlockId,
            AggregateKind = x.AggregateKind,
            IdentityColumns = x.IdentityColumns ?? new List<string>(),
            PeriodAggregationRule = x.PeriodAggregationRule,
            MetricMappingsJson = x.MetricMappingsJson,
            VersionNo = x.VersionNo,
            IsActive = x.IsActive
        };

    private static AggregateTableRequest NormalizeRequest(AggregateTableRequest req)
    {
        var normalized = new AggregateTableRequest
        {
            ParentAssignmentId = req.ParentAssignmentId?.Trim(),
            DynamicExcelId = req.DynamicExcelId?.Trim(),
            PeriodScopeMode = NormalizePeriodScopeMode(req.PeriodScopeMode),
            PeriodKey = NormalizeDayKey(req.PeriodKey),
            PeriodKeyFrom = NormalizeDayKey(req.PeriodKeyFrom),
            PeriodKeyTo = NormalizeDayKey(req.PeriodKeyTo),
            SourceStatusMode = NormalizeSourceStatusMode(req.SourceStatusMode),
            SelectedUnitIds = NormalizeStringList(req.SelectedUnitIds),
            AggregateMode = NormalizeAggregateMode(req.AggregateMode),
        };

        if (!string.IsNullOrWhiteSpace(normalized.PeriodKeyFrom) &&
            !string.IsNullOrWhiteSpace(normalized.PeriodKeyTo) &&
            string.CompareOrdinal(normalized.PeriodKeyFrom, normalized.PeriodKeyTo) > 0)
        {
            (normalized.PeriodKeyFrom, normalized.PeriodKeyTo) = (normalized.PeriodKeyTo, normalized.PeriodKeyFrom);
        }

        return normalized;
    }

    private static string NormalizePeriodScopeMode(string? value)
        => (value ?? "SINGLE_PERIOD").Trim().ToUpperInvariant();

    private static string NormalizeSourceStatusMode(string? value)
        => "APPROVED_ONLY";

    private static string NormalizeAggregateMode(string? value)
        => (value ?? "SUM_BY_CELL").Trim().ToUpperInvariant();

    private static string? NormalizeDayKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length == 8)
            return digits;

        return value.Trim();
    }

    private static bool TryParseNormalizedDayKey(string? value, out DateTime date)
        => DateTime.TryParseExact(
            NormalizeDayKey(value),
            "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out date);

    private static List<string> NormalizeStringList(IEnumerable<string>? values)
        => (values ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private async Task<List<string>> ResolveSelectedUnitIdsAsync(
        IEnumerable<string>? selectedUnitIds,
        CancellationToken ct)
    {
        var normalized = NormalizeStringList(selectedUnitIds);
        if (normalized.Count == 0)
            return normalized;

        return await _unitSelection.ExpandVirtualUnitIdsAsync(normalized, ct);
    }

    private static void ValidateAggregateRequest(AggregateTableRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ParentAssignmentId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_PARENT_ID_REQUIRED);

        if (string.IsNullOrWhiteSpace(req.DynamicExcelId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_DYNAMIC_EXCEL_ID_REQUIRED);

        if (req.AggregateMode is not ("SUM_BY_CELL" or "HORIZONTAL_BY_USER" or "VERTICAL_BY_USER"))
            throw InvalidAggregateMode(req.AggregateMode);

        if (req.PeriodScopeMode == "SINGLE_PERIOD")
        {
            if (string.IsNullOrWhiteSpace(req.PeriodKey))
                throw MissingPeriodKey("PeriodKey", req.PeriodScopeMode);
            return;
        }

        if (req.PeriodScopeMode == "PERIOD_RANGE")
        {
            if (string.IsNullOrWhiteSpace(req.PeriodKeyFrom))
                throw MissingPeriodKey("PeriodKeyFrom", req.PeriodScopeMode);

            if (string.IsNullOrWhiteSpace(req.PeriodKeyTo))
                throw MissingPeriodKey("PeriodKeyTo", req.PeriodScopeMode);

            return;
        }

        if (req.PeriodScopeMode == "CUMULATIVE_TO_PERIOD")
        {
            if (string.IsNullOrWhiteSpace(req.PeriodKeyTo))
                throw MissingPeriodKey("PeriodKeyTo", req.PeriodScopeMode);
            return;
        }

        if (req.PeriodScopeMode != "ALL_PERIODS")
            throw InvalidPeriodScopeMode(req.PeriodScopeMode);
    }

    private static DynamicFormAggregateRequest NormalizeDynamicFormRequest(DynamicFormAggregateRequest req)
    {
        var normalized = new DynamicFormAggregateRequest
        {
            ScopeAssignmentId = req.ScopeAssignmentId?.Trim() ?? string.Empty,
            ScopeMode = "DIRECT_CHILDREN",
            DynamicFormTemplateId = req.DynamicFormTemplateId?.Trim() ?? string.Empty,
            BlockId = string.IsNullOrWhiteSpace(req.BlockId) ? null : req.BlockId.Trim(),
            TableMode = string.IsNullOrWhiteSpace(req.TableMode)
                ? "FIXED_GRID"
                : req.TableMode.Trim().ToUpperInvariant(),
            MetricKeys = req.MetricKeys?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            PeriodScopeMode = NormalizePeriodScopeMode(req.PeriodScopeMode),
            PeriodKey = NormalizeDayKey(req.PeriodKey),
            PeriodKeyFrom = NormalizeDayKey(req.PeriodKeyFrom),
            PeriodKeyTo = NormalizeDayKey(req.PeriodKeyTo),
            SourceStatusMode = NormalizeSourceStatusMode(req.SourceStatusMode),
            SelectedUnitIds = NormalizeStringList(req.SelectedUnitIds),
            AggregateConfigId = NormalizeOptionalText(req.AggregateConfigId),
            IdentityColumns = NormalizeIdentityColumns(req.IdentityColumns),
        };

        if (!string.IsNullOrWhiteSpace(normalized.PeriodKeyFrom) &&
            !string.IsNullOrWhiteSpace(normalized.PeriodKeyTo) &&
            string.CompareOrdinal(normalized.PeriodKeyFrom, normalized.PeriodKeyTo) > 0)
        {
            (normalized.PeriodKeyFrom, normalized.PeriodKeyTo) = (normalized.PeriodKeyTo, normalized.PeriodKeyFrom);
        }

        return normalized;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static List<string> NormalizeIdentityColumns(IEnumerable<string>? values)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "periodKey",
            "periodInstanceKey",
            "unitSymbol",
            "unitShortName",
            "userName",
            "fullName",
            "workAssignmentId",
            "reportId",
            "approvedAtUtc",
            "sourceReportCount"
        };

        var normalized = NormalizeStringList(values)
            .Where(allowed.Contains)
            .ToList();

        return normalized.Count > 0
            ? normalized
            : DefaultStackIdentityColumns.ToList();
    }

    private static void ValidateDynamicFormRequest(DynamicFormAggregateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ScopeAssignmentId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_SCOPE_ID_REQUIRED);

        if (req.ScopeMode != "DIRECT_CHILDREN")
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_SCOPE_MODE_INVALID,
                new { req.ScopeMode });

        if (string.IsNullOrWhiteSpace(req.DynamicFormTemplateId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_DYNAMIC_FORM_TEMPLATE_ID_REQUIRED);

        if (req.TableMode is not ("FIXED_GRID" or "APPEND_ROWS" or "APPEND_COLUMNS" or "MATRIX" or "SUMMARY_TEMPLATE"))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_TABLE_MODE_INVALID,
                new { req.TableMode });

        if (req.PeriodScopeMode == "SINGLE_PERIOD")
        {
            if (string.IsNullOrWhiteSpace(req.PeriodKey))
                throw MissingPeriodKey("PeriodKey", req.PeriodScopeMode);
            return;
        }

        if (req.PeriodScopeMode == "PERIOD_RANGE")
        {
            if (string.IsNullOrWhiteSpace(req.PeriodKeyFrom))
                throw MissingPeriodKey("PeriodKeyFrom", req.PeriodScopeMode);

            if (string.IsNullOrWhiteSpace(req.PeriodKeyTo))
                throw MissingPeriodKey("PeriodKeyTo", req.PeriodScopeMode);

            return;
        }

        if (req.PeriodScopeMode == "CUMULATIVE_TO_PERIOD")
        {
            if (string.IsNullOrWhiteSpace(req.PeriodKeyTo))
                throw MissingPeriodKey("PeriodKeyTo", req.PeriodScopeMode);
            return;
        }

        if (req.PeriodScopeMode != "ALL_PERIODS")
            throw InvalidPeriodScopeMode(req.PeriodScopeMode);
    }

    private static AppException InvalidAggregateMode(string? aggregateMode)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_MODE_INVALID,
            new { aggregateMode });

    private static AppException InvalidPeriodScopeMode(string? periodScopeMode)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_PERIOD_SCOPE_INVALID,
            new { periodScopeMode });

    private static AppException MissingPeriodKey(string field, string? periodScopeMode)
    {
        var code = field switch
        {
            "PeriodKeyFrom" => AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_PERIOD_KEY_FROM_REQUIRED,
            "PeriodKeyTo" => AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_PERIOD_KEY_TO_REQUIRED,
            _ => AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_PERIOD_KEY_REQUIRED
        };

        return AppExceptionFactory.BadRequest(code, new { field, periodScopeMode });
    }

    private static AppException DynamicFormBlockNotFound(string? dynamicFormTemplateId, string? blockId)
        => AppExceptionFactory.NotFound(
            AppErrorCode.DYNAMIC_FORM_BLOCK_NOT_FOUND,
            new { dynamicFormTemplateId, blockId });

    private static AppException DynamicFormTableContractInvalid(
        string? dynamicFormTemplateId,
        string blockId,
        string tableMode,
        string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.DYNAMIC_FORM_TABLE_CONTRACT_INVALID,
            new { dynamicFormTemplateId, blockId, tableMode, reason });

    private async Task<WorkAssignment> LoadAggregateParentAsync(
        string parentAssignmentId,
        string actorUserId,
        CancellationToken ct,
        bool allowBranchRead = false)
    {
        var parent = await _ctx.WorkAssignments
            .Find(x => x.Id == parentAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_PARENT_NOT_FOUND,
                new { parentAssignmentId });

        var canView = string.Equals(parent.CreatedByUserId, actorUserId, StringComparison.Ordinal)
            || (parent.LeaderWatcherUserIds?.Contains(actorUserId) ?? false)
            || (parent.Assignees?.Any(x => string.Equals(x.UserId, actorUserId, StringComparison.Ordinal)) ?? false);

        if (!canView && allowBranchRead)
        {
            canView = await WorkAssignmentReadAccessHelper.CanReadAssignmentOrAncestorAsync(
                _ctx,
                parent,
                actorUserId,
                ct);
        }

        if (!canView)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_READ_FORBIDDEN,
                new { parentAssignmentId, actorUserId });

        return parent;
    }

    private async Task<List<WorkAssignment>> LoadAggregateChildrenAsync(
        string parentAssignmentId,
        string dynamicExcelId,
        IReadOnlyCollection<string> selectedUnitIds,
        CancellationToken ct)
    {
        var fb = Builders<WorkAssignment>.Filter;
        var filter = fb.Eq(x => x.ParentAssignmentId, parentAssignmentId)
                     & fb.Eq(x => x.DynamicExcelId, dynamicExcelId)
                     & fb.Eq(x => x.IsActive, true)
                     & fb.Eq(x => x.IsDeleted, false);

        filter = AddSelectedUnitFilter(filter, selectedUnitIds);

        return await _ctx.WorkAssignments
            .Find(filter)
            .SortBy(x => x.DynamicExcelCode)
            .ThenBy(x => x.DynamicExcelName)
            .ThenBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);
    }

    private async Task<List<WorkAssignment>> LoadDynamicFormAggregateAssignmentsAsync(
        WorkAssignment scopeRoot,
        string scopeMode,
        string dynamicFormTemplateId,
        IReadOnlyCollection<string> selectedUnitIds,
        CancellationToken ct)
    {
        if (scopeMode == "SUBTREE")
        {
            var pathPrefix = $"{scopeRoot.Path}/";
            var fb = Builders<WorkAssignment>.Filter;
            var filter = fb.Eq(x => x.WorkId, scopeRoot.WorkId)
                         & fb.Regex(x => x.Path, new MongoDB.Bson.BsonRegularExpression($"^{System.Text.RegularExpressions.Regex.Escape(pathPrefix)}"))
                         & fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId)
                         & fb.Eq(x => x.IsActive, true)
                         & fb.Eq(x => x.IsDeleted, false);

            filter = AddSelectedUnitFilter(filter, selectedUnitIds);

            return await _ctx.WorkAssignments
                .Find(filter)
                .SortBy(x => x.Path)
                .ThenBy(x => x.DynamicFormTemplateCode)
                .ThenBy(x => x.DynamicFormTemplateName)
                .ThenBy(x => x.CreatedAtUtc)
                .ToListAsync(ct);
        }

        var directFb = Builders<WorkAssignment>.Filter;
        var directFilter = directFb.Eq(x => x.ParentAssignmentId, scopeRoot.Id)
                         & directFb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId)
                         & directFb.Eq(x => x.IsActive, true)
                         & directFb.Eq(x => x.IsDeleted, false);

        directFilter = AddSelectedUnitFilter(directFilter, selectedUnitIds);

        return await _ctx.WorkAssignments
            .Find(directFilter)
            .SortBy(x => x.DynamicFormTemplateCode)
            .ThenBy(x => x.DynamicFormTemplateName)
            .ThenBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);
    }

    private static FilterDefinition<WorkAssignment> AddSelectedUnitFilter(
        FilterDefinition<WorkAssignment> filter,
        IReadOnlyCollection<string> selectedUnitIds)
    {
        if (selectedUnitIds.Count == 0)
            return filter;

        var assigneeFilter = Builders<UserRef>.Filter.In(x => x.UnitId, selectedUnitIds);
        return filter & Builders<WorkAssignment>.Filter.ElemMatch(x => x.Assignees, assigneeFilter);
    }

    private async Task<List<WorkAssignmentReport>> LoadAggregateReportsAsync(
        List<WorkAssignment> assignments,
        AggregateTableRequest req,
        CancellationToken ct)
    {
        var assignmentIds = assignments.Select(x => x.Id).ToList();
        var filter = BuildAggregateReportFilter(assignmentIds, req);

        var reports = await _ctx.WorkAssignmentReports
            .Find(filter)
            .SortBy(x => x.PeriodKey)
            .ThenBy(x => x.PeriodInstanceKey)
            .ThenBy(x => x.ReportDate)
            .ThenBy(x => x.AssigneeUserId)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(ct);

        await HydrateReportPayloadsAsync(reports, ct);
        return reports;
    }

    private async Task<DynamicExcelTemplate?> LoadDynamicExcelTemplateAsync(string dynamicExcelId, CancellationToken ct)
    {
        var template = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == dynamicExcelId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        return template;
    }

    private async Task<List<WorkAssignmentReport>> LoadDynamicFormAggregateReportsAsync(
        List<WorkAssignment> assignments,
        DynamicFormAggregateRequest req,
        CancellationToken ct)
    {
        var assignmentIds = assignments.Select(x => x.Id).ToList();
        var filter = BuildDynamicFormAggregateReportFilter(assignmentIds, req);

        var reports = await _ctx.WorkAssignmentReports
            .Find(filter)
            .SortBy(x => x.PeriodKey)
            .ThenBy(x => x.PeriodInstanceKey)
            .ThenBy(x => x.ReportDate)
            .ThenBy(x => x.AssigneeUserId)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(ct);

        await HydrateReportPayloadsAsync(reports, ct);
        return reports;
    }

    private async Task HydrateReportPayloadsAsync(List<WorkAssignmentReport> reports, CancellationToken ct)
    {
        foreach (var report in reports)
        {
            var payload = await _payloadReader.LoadReportPayloadAsync(report, ct);
            report.Values1DJson = payload.Values1DJson;
            report.FieldValuesJson = payload.FieldValuesJson;
            report.TableValuesJson = payload.TableValuesJson;
            report.SummarySourceJson = payload.SummarySourceJson;
        }
    }

    private static FilterDefinition<WorkAssignmentReport> BuildAggregateReportFilter(
        List<string> assignmentIds,
        AggregateTableRequest req)
    {
        var filter = Builders<WorkAssignmentReport>.Filter.And(
            Builders<WorkAssignmentReport>.Filter.In(x => x.WorkAssignmentId, assignmentIds),
            Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false),
            Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsCurrent, true),
            Builders<WorkAssignmentReport>.Filter.Ne(x => x.IsActive, false),
            Builders<WorkAssignmentReport>.Filter.Ne(
                x => x.CumulativeContributionMode,
                WorkReportCumulativeContributionMode.Exclude));

        filter = AddReportPeriodScopeFilter(
            filter,
            req.PeriodScopeMode,
            req.PeriodKey,
            req.PeriodKeyFrom,
            req.PeriodKeyTo);

        if (req.SourceStatusMode == "APPROVED_AND_SUBMITTED")
        {
            filter &= Builders<WorkAssignmentReport>.Filter.In(
                x => x.Status,
                new[]
                {
                    WorkAssignmentReportStatus.Approved,
                    WorkAssignmentReportStatus.Submitted,
                });
        }
        else
        {
            filter &= Builders<WorkAssignmentReport>.Filter.Eq(
                x => x.Status,
                WorkAssignmentReportStatus.Approved);
        }

        return filter;
    }

    private static FilterDefinition<WorkAssignmentReport> BuildDynamicFormAggregateReportFilter(
        List<string> assignmentIds,
        DynamicFormAggregateRequest req)
    {
        var filter = Builders<WorkAssignmentReport>.Filter.And(
            Builders<WorkAssignmentReport>.Filter.In(x => x.WorkAssignmentId, assignmentIds),
            Builders<WorkAssignmentReport>.Filter.Eq(x => x.DynamicFormTemplateId, req.DynamicFormTemplateId),
            Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false),
            Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsCurrent, true),
            Builders<WorkAssignmentReport>.Filter.Ne(x => x.IsActive, false),
            Builders<WorkAssignmentReport>.Filter.Ne(
                x => x.CumulativeContributionMode,
                WorkReportCumulativeContributionMode.Exclude));

        filter = AddReportPeriodScopeFilter(
            filter,
            req.PeriodScopeMode,
            req.PeriodKey,
            req.PeriodKeyFrom,
            req.PeriodKeyTo);

        if (req.SourceStatusMode == "APPROVED_AND_SUBMITTED")
        {
            filter &= Builders<WorkAssignmentReport>.Filter.In(
                x => x.Status,
                new[]
                {
                    WorkAssignmentReportStatus.Approved,
                    WorkAssignmentReportStatus.Submitted,
                });
        }
        else
        {
            filter &= Builders<WorkAssignmentReport>.Filter.Eq(
                x => x.Status,
                WorkAssignmentReportStatus.Approved);
        }

        return filter;
    }

    private static FilterDefinition<WorkAssignmentReport> AddReportPeriodScopeFilter(
        FilterDefinition<WorkAssignmentReport> filter,
        string? periodScopeMode,
        string? periodKey,
        string? periodKeyFrom,
        string? periodKeyTo)
    {
        var mode = (periodScopeMode ?? "ALL_PERIODS").Trim().ToUpperInvariant();
        if (mode == "ALL_PERIODS")
            return filter;

        var fb = Builders<WorkAssignmentReport>.Filter;

        if (mode == "SINGLE_PERIOD")
        {
            var key = NormalizeDayKey(periodKey);
            if (TryParseNormalizedDayKey(key, out var date))
            {
                var dayStart = date.Date;
                var dayEndExclusive = dayStart.AddDays(1);
                var window = fb.And(
                    fb.Lt(x => x.PeriodStart, dayEndExclusive),
                    fb.Gte(x => x.PeriodEnd, dayStart));
                return filter & fb.Or(window, fb.Eq(x => x.PeriodKey, key));
            }

            return filter & fb.Eq(x => x.PeriodKey, key);
        }

        if (mode == "PERIOD_RANGE")
        {
            var fromKey = NormalizeDayKey(periodKeyFrom);
            var toKey = NormalizeDayKey(periodKeyTo);
            if (TryParseNormalizedDayKey(fromKey, out var from) &&
                TryParseNormalizedDayKey(toKey, out var to))
            {
                if (to < from)
                    (from, to) = (to, from);

                var fromStart = from.Date;
                var toEndExclusive = to.Date.AddDays(1);
                var window = fb.And(
                    fb.Lt(x => x.PeriodStart, toEndExclusive),
                    fb.Gte(x => x.PeriodEnd, fromStart));
                var keyRange = fb.And(
                    fb.Gte(x => x.PeriodKey, fromKey),
                    fb.Lte(x => x.PeriodKey, toKey));
                return filter & fb.Or(window, keyRange);
            }

            return filter & fb.Gte(x => x.PeriodKey, fromKey) & fb.Lte(x => x.PeriodKey, toKey);
        }

        if (mode == "CUMULATIVE_TO_PERIOD")
        {
            var toKey = NormalizeDayKey(periodKeyTo);
            if (TryParseNormalizedDayKey(toKey, out var to))
            {
                var window = fb.Lt(x => x.PeriodStart, to.Date.AddDays(1));
                return filter & fb.Or(window, fb.Lte(x => x.PeriodKey, toKey));
            }

            return filter & fb.Lte(x => x.PeriodKey, toKey);
        }

        return filter;
    }

    private static FilterDefinition<WorkReportTableStatAggregate> AddTableAggregatePeriodScopeFilter(
        FilterDefinition<WorkReportTableStatAggregate> filter,
        string? periodScopeMode,
        string? periodKey,
        string? periodKeyFrom,
        string? periodKeyTo)
    {
        var mode = (periodScopeMode ?? "ALL_PERIODS").Trim().ToUpperInvariant();
        if (mode == "ALL_PERIODS")
            return filter;

        var fb = Builders<WorkReportTableStatAggregate>.Filter;

        if (mode == "SINGLE_PERIOD")
        {
            var key = NormalizeDayKey(periodKey);
            if (TryParseNormalizedDayKey(key, out var date))
            {
                var dayStart = date.Date;
                var dayEndExclusive = dayStart.AddDays(1);
                var window = fb.And(
                    fb.Lt(x => x.PeriodStartDate, dayEndExclusive),
                    fb.Gte(x => x.PeriodEndDate, dayStart));
                return filter & fb.Or(window, fb.Eq(x => x.PeriodKey, key));
            }

            return filter & fb.Eq(x => x.PeriodKey, key);
        }

        if (mode == "PERIOD_RANGE")
        {
            var fromKey = NormalizeDayKey(periodKeyFrom);
            var toKey = NormalizeDayKey(periodKeyTo);
            if (TryParseNormalizedDayKey(fromKey, out var from) &&
                TryParseNormalizedDayKey(toKey, out var to))
            {
                if (to < from)
                    (from, to) = (to, from);

                var fromStart = from.Date;
                var toEndExclusive = to.Date.AddDays(1);
                var window = fb.And(
                    fb.Lt(x => x.PeriodStartDate, toEndExclusive),
                    fb.Gte(x => x.PeriodEndDate, fromStart));
                var keyRange = fb.And(
                    fb.Gte(x => x.PeriodKey, fromKey),
                    fb.Lte(x => x.PeriodKey, toKey));
                return filter & fb.Or(window, keyRange);
            }

            return filter & fb.Gte(x => x.PeriodKey, fromKey) & fb.Lte(x => x.PeriodKey, toKey);
        }

        if (mode == "CUMULATIVE_TO_PERIOD")
        {
            var toKey = NormalizeDayKey(periodKeyTo);
            if (TryParseNormalizedDayKey(toKey, out var to))
            {
                var window = fb.Lt(x => x.PeriodStartDate, to.Date.AddDays(1));
                return filter & fb.Or(window, fb.Lte(x => x.PeriodKey, toKey));
            }

            return filter & fb.Lte(x => x.PeriodKey, toKey);
        }

        return filter;
    }

    private static List<AggregateSourceRowDto> BuildAggregateSources(
        List<WorkAssignment> assignments,
        List<WorkAssignmentReport> reports)
    {
        var assignmentMap = assignments.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);

        return reports
            .Select(report =>
            {
                assignmentMap.TryGetValue(report.WorkAssignmentId, out var assignment);

                var assignee = assignment?.Assignees?.FirstOrDefault(x => x.UserId == report.AssigneeUserId)
                    ?? assignment?.Assignees?.FirstOrDefault();

                return new AggregateSourceRowDto
                {
                    ReportId = report.Id,
                    WorkAssignmentId = report.WorkAssignmentId,
                    AssigneeUserId = report.AssigneeUserId,
                    UserName = assignee?.Username,
                    FullName = assignee?.FullName,
                    UnitSymbol = assignee?.UnitSymbol,
                    UnitShortName = assignee?.UnitShortName,
                    ReportStatus = (int)report.Status,
                    PeriodKey = report.PeriodKey,
                    PeriodInstanceKey = report.PeriodInstanceKey,
                    PeriodKind = report.PeriodKind,
                    ReportDate = report.ReportDate,
                    SubmittedAtUtc = report.SubmittedAtUtc,
                    ApprovedAtUtc = report.ApprovedAtUtc,
                };
            })
            .OrderBy(x => x.PeriodKey, StringComparer.Ordinal)
            .ThenBy(x => x.PeriodInstanceKey ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.ReportDate)
            .ThenBy(x => x.UnitShortName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.FullName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.ReportId, StringComparer.Ordinal)
            .ToList();
    }

    private static DynamicFormAggregateResponse BuildEmptyDynamicFormResponse(
        DynamicFormAggregateRequest req,
        DynamicFormTemplate template,
        DynamicFormTableContract contract,
        List<MetricContract> metrics,
        List<string> warnings)
    {
        return new DynamicFormAggregateResponse
        {
            Meta = BuildDynamicFormMeta(req, template, contract, metrics.Count, 0, 0),
            Columns = BuildDynamicFormColumns(),
            Rows = contract.TableMode == "SUMMARY_TEMPLATE"
                ? new List<DynamicFormAggregateRowDto>()
                : BuildDynamicFormMetricRows(new List<WorkAssignmentReport>(), contract, metrics, warnings),
            StackedTable = contract.TableMode is "APPEND_ROWS" or "APPEND_COLUMNS"
                ? BuildEmptyStackedTable(contract, metrics, req)
                : null,
            Sources = new List<AggregateSourceRowDto>(),
            Warnings = warnings,
        };
    }

    private static DynamicFormAggregateMetaDto BuildDynamicFormMeta(
        DynamicFormAggregateRequest req,
        DynamicFormTemplate template,
        DynamicFormTableContract contract,
        int metricCount,
        int sourceAssignmentCount,
        int sourceReportCount)
    {
        return new DynamicFormAggregateMetaDto
        {
            ScopeAssignmentId = req.ScopeAssignmentId,
            ScopeMode = req.ScopeMode ?? "DIRECT_CHILDREN",
            DynamicFormTemplateId = template.Id,
            DynamicFormTemplateCode = template.Code,
            DynamicFormTemplateName = template.Name,
            BlockId = contract.BlockId,
            TableMode = contract.TableMode,
            PeriodScopeMode = req.PeriodScopeMode,
            PeriodKey = req.PeriodKey,
            PeriodKeyFrom = req.PeriodKeyFrom,
            PeriodKeyTo = req.PeriodKeyTo,
            SourceStatusMode = req.SourceStatusMode,
            SelectedUnitIds = req.SelectedUnitIds ?? new List<string>(),
            AggregateConfigId = req.AggregateConfigId,
            IdentityColumns = req.IdentityColumns ?? DefaultStackIdentityColumns.ToList(),
            SourceAssignmentCount = sourceAssignmentCount,
            SourceReportCount = sourceReportCount,
            MetricCount = metricCount,
        };
    }

    private static List<DynamicFormAggregateColumnDto> BuildDynamicFormColumns()
        => new()
        {
            new DynamicFormAggregateColumnDto { Key = "label", Label = "Chỉ tiêu", Type = "text" },
            new DynamicFormAggregateColumnDto { Key = "count", Label = "Số ô có dữ liệu", Type = "number" },
            new DynamicFormAggregateColumnDto { Key = "sum", Label = "Tổng", Type = "number" },
            new DynamicFormAggregateColumnDto { Key = "min", Label = "Nhỏ nhất", Type = "number" },
            new DynamicFormAggregateColumnDto { Key = "max", Label = "Lớn nhất", Type = "number" },
            new DynamicFormAggregateColumnDto { Key = "average", Label = "Trung bình", Type = "number" },
        };

    private async Task<List<DynamicFormAggregateRowDto>?> BuildDynamicFormProjectedMetricRowsAsync(
        List<WorkAssignmentReport> reports,
        DynamicFormTableContract contract,
        List<MetricContract> metrics,
        List<string> warnings,
        CancellationToken ct)
    {
        if (reports.Count == 0 || metrics.Count == 0)
            return null;

        var reportIds = reports
            .Where(report => ReportCanContributeAnyMetric(report, contract, metrics))
            .Select(x => x.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (reportIds.Count == 0)
            return null;

        var metricKeys = metrics
            .Select(x => x.MetricKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var fb = Builders<WorkReportTableStatValue>.Filter;
        var filter = fb.In(x => x.WorkAssignmentReportId, reportIds)
                     & fb.Eq(x => x.BlockId, contract.BlockId)
                     & fb.Eq(x => x.TableMode, contract.TableMode)
                     & fb.In(x => x.MetricKey, metricKeys)
                     & fb.Eq(x => x.DataType, "NUMBER")
                     & fb.Eq(x => x.IsDeleted, false);

        var values = await _ctx.WorkReportTableStatValues
            .Find(filter)
            .ToListAsync(ct);

        if (values.Count == 0)
            return null;

        var projectedReportIds = values
            .Select(x => x.WorkAssignmentReportId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        if (reportIds.Any(x => !projectedReportIds.Contains(x)))
        {
            warnings.Add("Một số báo cáo nguồn chưa có snapshot chỉ số bảng; hệ thống dùng dữ liệu bảng gốc để tổng hợp.");
            return null;
        }

        var acc = metrics.ToDictionary(
            x => x.MetricKey,
            x => new MetricAccumulator(x),
            StringComparer.Ordinal);

        foreach (var value in values)
        {
            if (!acc.TryGetValue(value.MetricKey, out var metricAcc))
                continue;

            metricAcc.Add(value.Value);
        }

        return acc.Values
            .OrderBy(x => x.Metric.Index)
            .Select(x => x.ToDto())
            .ToList();
    }

    private async Task<List<DynamicFormAggregateRowDto>> BuildDynamicFormSummaryTemplateRowsAsync(
        WorkAssignment scopeRoot,
        List<WorkAssignment> assignments,
        DynamicFormAggregateRequest req,
        DynamicFormTableContract contract,
        List<MetricContract> metrics,
        List<string> warnings,
        CancellationToken ct)
    {
        var layout = contract.SummaryTemplate
            ?? throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FORM_SUMMARY_LAYOUT_REQUIRED,
                new { req.DynamicFormTemplateId, req.BlockId, req.TableMode });

        if (assignments.Count == 0 || metrics.Count == 0)
            return new List<DynamicFormAggregateRowDto>();

        var assignmentIds = assignments
            .Select(x => x.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var metricKeys = metrics
            .Select(x => x.MetricKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var fb = Builders<WorkReportTableStatAggregate>.Filter;
        var filter = fb.Eq(x => x.WorkId, scopeRoot.WorkId)
                     & fb.Eq(x => x.ScopeType, "ASSIGNMENT")
                     & fb.In(x => x.ScopeId, assignmentIds)
                     & fb.Eq(x => x.DynamicFormTemplateId, req.DynamicFormTemplateId)
                     & fb.Eq(x => x.BlockId, layout.SourceBlockId)
                     & fb.In(x => x.MetricKey, metricKeys)
                     & fb.Eq(x => x.DataType, "NUMBER")
                     & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(layout.SourceTableMode))
            filter &= fb.Eq(x => x.TableMode, layout.SourceTableMode);

        filter = AddTableAggregatePeriodScopeFilter(
            filter,
            req.PeriodScopeMode,
            req.PeriodKey,
            req.PeriodKeyFrom,
            req.PeriodKeyTo);

        if (req.SourceStatusMode == "APPROVED_AND_SUBMITTED")
        {
            filter &= fb.In(
                x => x.ReportStatus,
                new[]
                {
                    (int)WorkAssignmentReportStatus.Approved,
                    (int)WorkAssignmentReportStatus.Submitted,
                });
        }
        else
        {
            filter &= fb.Eq(x => x.ReportStatus, (int)WorkAssignmentReportStatus.Approved);
        }

        var aggregates = await _ctx.WorkReportTableStatAggregates
            .Find(filter)
            .ToListAsync(ct);

        if (aggregates.Count == 0)
        {
            warnings.Add("Chưa có snapshot tổng hợp phù hợp với phạm vi hoặc kỳ đã chọn.");
        }

        if (layout.GroupBy.Any(x => x != "UNIT" && x != "ASSIGNMENT"))
        {
            warnings.Add("Preview hiển thị theo đơn vị hoặc công việc; các nhóm khác vẫn được giữ trong cấu hình tổng hợp.");
        }

        var metricByKey = metrics.ToDictionary(x => x.MetricKey, x => x, StringComparer.Ordinal);
        var acc = new Dictionary<SummaryAggregateKey, SummaryAggregateAccumulator>();
        foreach (var aggregate in aggregates)
        {
            if (!metricByKey.TryGetValue(aggregate.MetricKey, out var metric))
                continue;

            var key = new SummaryAggregateKey(aggregate.ScopeId, aggregate.MetricKey);
            if (!acc.TryGetValue(key, out var bucket))
            {
                bucket = new SummaryAggregateAccumulator(metric);
                acc[key] = bucket;
            }

            bucket.Add(aggregate);
        }

        var groupType = layout.GroupBy.Contains("UNIT", StringComparer.Ordinal)
            ? "UNIT"
            : "ASSIGNMENT";

        var orderedAssignments = assignments
            .OrderBy(x => ResolveAssignmentAssignee(x)?.UnitShortName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => ResolveAssignmentAssignee(x)?.UnitSymbol ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => ResolveAssignmentAssignee(x)?.FullName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.Path ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToList();

        var rows = new List<DynamicFormAggregateRowDto>();
        for (var groupIndex = 0; groupIndex < orderedAssignments.Count; groupIndex++)
        {
            var assignment = orderedAssignments[groupIndex];
            var assignee = ResolveAssignmentAssignee(assignment);
            var groupKey = groupType == "UNIT"
                ? NormalizeSummaryGroupKey(assignee?.UnitId ?? assignee?.UnitSymbol ?? assignment.Id)
                : assignment.Id;
            var groupLabel = BuildSummaryGroupLabel(assignment, assignee, groupType);

            foreach (var metric in metrics.OrderBy(x => x.Index))
            {
                acc.TryGetValue(new SummaryAggregateKey(assignment.Id, metric.MetricKey), out var bucket);
                var outputRowIndex = groupIndex * layout.RowsPerGroup
                                     + metric.SummaryRowOffset.GetValueOrDefault(metric.Index);
                rows.Add(new DynamicFormAggregateRowDto
                {
                    MetricKey = metric.MetricKey,
                    RowKey = groupKey,
                    ColumnKey = metric.ColumnKey,
                    Index = outputRowIndex,
                    Label = $"{groupLabel} / {metric.MetricKey}",
                    GroupType = groupType,
                    GroupKey = groupKey,
                    GroupLabel = groupLabel,
                    WorkAssignmentId = assignment.Id,
                    UnitSymbol = assignee?.UnitSymbol,
                    UnitShortName = assignee?.UnitShortName,
                    UserName = assignee?.Username,
                    FullName = assignee?.FullName,
                    SourceMetricKey = metric.MetricKey,
                    LayoutIndex = metric.Index,
                    OutputGroupIndex = groupIndex,
                    OutputRowIndex = outputRowIndex,
                    OutputRowNumber = outputRowIndex + 1,
                    RowsPerGroup = layout.RowsPerGroup,
                    ReportCount = bucket?.ReportCount ?? 0,
                    Count = ToSafeInt(bucket?.ValueCount ?? 0),
                    Sum = bucket is { ValueCount: > 0 } ? bucket.Sum : null,
                    Min = bucket?.Min,
                    Max = bucket?.Max,
                    Average = bucket is { ValueCount: > 0 } ? bucket.Sum / bucket.ValueCount : null,
                });
            }
        }

        return rows;
    }

    private static DynamicFormStackedTableDto BuildEmptyStackedTable(
        DynamicFormTableContract contract,
        List<MetricContract> metrics,
        DynamicFormAggregateRequest req)
        => new()
        {
            SourceTableMode = contract.TableMode,
            RowMode = ShouldAggregateStackBeforeStack(req.PeriodScopeMode) ? "PERIOD_GROUPED_STACK" : "DIRECT_STACK",
            Columns = BuildStackedTableColumns(contract, metrics, req.IdentityColumns ?? DefaultStackIdentityColumns.ToList()),
            Rows = new List<DynamicFormStackedTableRowDto>()
        };

    private static DynamicFormStackedTableDto BuildDynamicFormStackedTable(
        List<WorkAssignment> assignments,
        List<WorkAssignmentReport> reports,
        DynamicFormTableContract contract,
        List<MetricContract> metrics,
        DynamicFormAggregateRequest req,
        List<string> warnings)
    {
        var identityColumns = req.IdentityColumns ?? DefaultStackIdentityColumns.ToList();
        var aggregateBeforeStack = ShouldAggregateStackBeforeStack(req.PeriodScopeMode);
        var table = new DynamicFormStackedTableDto
        {
            SourceTableMode = contract.TableMode,
            RowMode = aggregateBeforeStack ? "PERIOD_GROUPED_STACK" : "DIRECT_STACK",
            Columns = BuildStackedTableColumns(contract, metrics, identityColumns)
        };

        if (reports.Count == 0 || metrics.Count == 0)
            return table;

        var assignmentMap = assignments.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
        table.Rows = aggregateBeforeStack
            ? BuildGroupedStackedRows(assignmentMap, reports, contract, metrics, identityColumns, warnings)
            : BuildDirectStackedRows(assignmentMap, reports, contract, metrics, identityColumns, warnings);

        return table;
    }

    private static bool ShouldAggregateStackBeforeStack(string? periodScopeMode)
        => !string.Equals(periodScopeMode, "SINGLE_PERIOD", StringComparison.Ordinal);

    private static List<DynamicFormStackedTableColumnDto> BuildStackedTableColumns(
        DynamicFormTableContract contract,
        List<MetricContract> metrics,
        IReadOnlyCollection<string> identityColumns)
    {
        var columns = identityColumns
            .Select(x => new DynamicFormStackedTableColumnDto
            {
                Key = x,
                Label = FormatIdentityColumnLabel(x),
                Role = "IDENTITY",
                Type = x == "sourceReportCount" ? "number" : "text"
            })
            .ToList();

        if (contract.TableMode == "APPEND_ROWS")
        {
            columns.Add(new DynamicFormStackedTableColumnDto { Key = "sourceRowNumber", Label = "Dòng nguồn", Role = "IDENTITY", Type = "number" });
            columns.Add(new DynamicFormStackedTableColumnDto { Key = "sourceRowKey", Label = "Khóa dòng nguồn", Role = "IDENTITY", Type = "text" });
        }
        else
        {
            columns.Add(new DynamicFormStackedTableColumnDto { Key = "sourceColumnNumber", Label = "Cột nguồn", Role = "IDENTITY", Type = "number" });
            columns.Add(new DynamicFormStackedTableColumnDto { Key = "sourceColumnKey", Label = "Khóa cột nguồn", Role = "IDENTITY", Type = "text" });
        }

        columns.AddRange(metrics
            .OrderBy(x => x.Index)
            .Select(metric => new DynamicFormStackedTableColumnDto
            {
                Key = metric.MetricKey,
                Label = FormatStackMetricLabel(contract, metric),
                Role = "METRIC",
                Type = "mixed",
                MetricKey = metric.MetricKey,
                SourceKey = contract.TableMode == "APPEND_ROWS" ? metric.ColumnKey : metric.RowKey
            }));

        return columns;
    }

    private static string FormatIdentityColumnLabel(string key)
        => key switch
        {
            "periodKey" => "Kỳ",
            "periodInstanceKey" => "Lần báo cáo",
            "unitSymbol" => "Mã đơn vị",
            "unitShortName" => "Đơn vị",
            "userName" => "Tài khoản",
            "fullName" => "Người báo cáo",
            "workAssignmentId" => "Công việc nguồn",
            "reportId" => "Báo cáo nguồn",
            "approvedAtUtc" => "Duyệt lúc",
            "sourceReportCount" => "Số báo cáo nguồn",
            _ => key
        };

    private static string FormatStackMetricLabel(DynamicFormTableContract contract, MetricContract metric)
        => contract.TableMode == "APPEND_ROWS"
            ? FirstNonBlank(metric.ColumnKey, metric.MetricKey) ?? metric.MetricKey
            : FirstNonBlank(metric.RowKey, metric.MetricKey) ?? metric.MetricKey;

    private static List<DynamicFormStackedTableRowDto> BuildDirectStackedRows(
        Dictionary<string, WorkAssignment> assignmentMap,
        List<WorkAssignmentReport> reports,
        DynamicFormTableContract contract,
        List<MetricContract> metrics,
        IReadOnlyCollection<string> identityColumns,
        List<string> warnings)
    {
        var rows = new List<DynamicFormStackedTableRowDto>();
        foreach (var report in reports)
        {
            assignmentMap.TryGetValue(report.WorkAssignmentId, out var assignment);
            var assignee = ResolveReportAssignee(assignment, report);
            var block = ExtractReportTableBlock(report.TableValuesJson, contract.BlockId);
            if (block is null)
            {
                warnings.Add("Một số báo cáo nguồn thiếu block bảng để stack nên đã được bỏ qua.");
                continue;
            }

            if (contract.TableMode == "APPEND_ROWS")
            {
                foreach (var row in block.Rows ?? new List<ReportTableAppendRow>())
                    rows.Add(BuildDirectStackedAppendRow(report, assignment, assignee, row, metrics, identityColumns));
            }
            else
            {
                foreach (var column in block.Columns ?? new List<ReportTableAppendColumn>())
                    rows.Add(BuildDirectStackedAppendColumn(report, assignment, assignee, column, metrics, identityColumns));
            }
        }

        return OrderStackedRows(rows);
    }

    private static DynamicFormStackedTableRowDto BuildDirectStackedAppendRow(
        WorkAssignmentReport report,
        WorkAssignment? assignment,
        UserRef? assignee,
        ReportTableAppendRow row,
        List<MetricContract> metrics,
        IReadOnlyCollection<string> identityColumns)
    {
        var cells = BuildStackIdentityCells(report, assignment, assignee, identityColumns, 1);
        var sourceRowNumber = row.RowOrder.GetValueOrDefault();
        cells["sourceRowNumber"] = sourceRowNumber > 0 ? sourceRowNumber : null;
        cells["sourceRowKey"] = FirstNonBlank(row.RowKey, row.RowInstanceId, $"row:{sourceRowNumber}") ?? $"row:{sourceRowNumber}";

        foreach (var metric in metrics.OrderBy(x => x.Index))
        {
            cells[metric.MetricKey] = row.Cells is not null && row.Cells.TryGetValue(metric.ColumnKey, out var value)
                ? ToStackCellObject(value)
                : null;
        }

        return new DynamicFormStackedTableRowDto
        {
            RowKey = $"{report.Id}:{row.RowInstanceId ?? row.RowKey ?? sourceRowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            Cells = cells,
            SourceReportIds = new List<string> { report.Id },
            SourceAssignmentIds = new List<string> { report.WorkAssignmentId }
        };
    }

    private static DynamicFormStackedTableRowDto BuildDirectStackedAppendColumn(
        WorkAssignmentReport report,
        WorkAssignment? assignment,
        UserRef? assignee,
        ReportTableAppendColumn column,
        List<MetricContract> metrics,
        IReadOnlyCollection<string> identityColumns)
    {
        var cells = BuildStackIdentityCells(report, assignment, assignee, identityColumns, 1);
        var sourceColumnNumber = column.ColumnOrder.GetValueOrDefault();
        cells["sourceColumnNumber"] = sourceColumnNumber > 0 ? sourceColumnNumber : null;
        cells["sourceColumnKey"] = FirstNonBlank(column.ColumnKey, column.ColumnInstanceId, $"column:{sourceColumnNumber}") ?? $"column:{sourceColumnNumber}";

        foreach (var metric in metrics.OrderBy(x => x.Index))
        {
            cells[metric.MetricKey] = column.Cells is not null && column.Cells.TryGetValue(metric.RowKey, out var value)
                ? ToStackCellObject(value)
                : null;
        }

        return new DynamicFormStackedTableRowDto
        {
            RowKey = $"{report.Id}:{column.ColumnInstanceId ?? column.ColumnKey ?? sourceColumnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            Cells = cells,
            SourceReportIds = new List<string> { report.Id },
            SourceAssignmentIds = new List<string> { report.WorkAssignmentId }
        };
    }

    private static List<DynamicFormStackedTableRowDto> BuildGroupedStackedRows(
        Dictionary<string, WorkAssignment> assignmentMap,
        List<WorkAssignmentReport> reports,
        DynamicFormTableContract contract,
        List<MetricContract> metrics,
        IReadOnlyCollection<string> identityColumns,
        List<string> warnings)
    {
        var buckets = new Dictionary<string, StackedRowAccumulator>(StringComparer.Ordinal);
        foreach (var report in reports)
        {
            assignmentMap.TryGetValue(report.WorkAssignmentId, out var assignment);
            var assignee = ResolveReportAssignee(assignment, report);
            var block = ExtractReportTableBlock(report.TableValuesJson, contract.BlockId);
            if (block is null)
            {
                warnings.Add("Một số báo cáo nguồn thiếu block bảng để stack nên đã được bỏ qua.");
                continue;
            }

            if (contract.TableMode == "APPEND_ROWS")
            {
                foreach (var row in block.Rows ?? new List<ReportTableAppendRow>())
                {
                    var sourceNumber = row.RowOrder.GetValueOrDefault();
                    var sourceKey = FirstNonBlank(row.RowKey, row.RowInstanceId, $"row:{sourceNumber}") ?? $"row:{sourceNumber}";
                    var bucket = GetStackBucket(buckets, report, assignment, assignee, identityColumns, "sourceRowNumber", sourceNumber > 0 ? sourceNumber : null, "sourceRowKey", sourceKey);
                    foreach (var metric in metrics)
                    {
                        if (row.Cells is not null && row.Cells.TryGetValue(metric.ColumnKey, out var value))
                            bucket.AddMetric(metric.MetricKey, value);
                    }
                }
            }
            else
            {
                foreach (var column in block.Columns ?? new List<ReportTableAppendColumn>())
                {
                    var sourceNumber = column.ColumnOrder.GetValueOrDefault();
                    var sourceKey = FirstNonBlank(column.ColumnKey, column.ColumnInstanceId, $"column:{sourceNumber}") ?? $"column:{sourceNumber}";
                    var bucket = GetStackBucket(buckets, report, assignment, assignee, identityColumns, "sourceColumnNumber", sourceNumber > 0 ? sourceNumber : null, "sourceColumnKey", sourceKey);
                    foreach (var metric in metrics)
                    {
                        if (column.Cells is not null && column.Cells.TryGetValue(metric.RowKey, out var value))
                            bucket.AddMetric(metric.MetricKey, value);
                    }
                }
            }
        }

        return OrderStackedRows(buckets.Values.Select(x => x.ToDto(metrics)).ToList());
    }

    private static StackedRowAccumulator GetStackBucket(
        Dictionary<string, StackedRowAccumulator> buckets,
        WorkAssignmentReport report,
        WorkAssignment? assignment,
        UserRef? assignee,
        IReadOnlyCollection<string> identityColumns,
        string sourceNumberKey,
        int? sourceNumber,
        string sourceKeyKey,
        string sourceKey)
    {
        var cells = BuildStackIdentityCells(report, assignment, assignee, identityColumns, 0);
        cells[sourceNumberKey] = sourceNumber;
        cells[sourceKeyKey] = sourceKey;

        var key = string.Join("|", cells.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}:{Convert.ToString(x.Value, System.Globalization.CultureInfo.InvariantCulture)}"));
        if (!buckets.TryGetValue(key, out var bucket))
        {
            bucket = new StackedRowAccumulator(key, cells);
            buckets[key] = bucket;
        }

        bucket.AddSource(report);
        return bucket;
    }

    private static List<DynamicFormStackedTableRowDto> OrderStackedRows(List<DynamicFormStackedTableRowDto> rows)
        => rows
            .OrderBy(x => Convert.ToString(x.Cells.GetValueOrDefault("periodKey")), StringComparer.Ordinal)
            .ThenBy(x => Convert.ToString(x.Cells.GetValueOrDefault("unitShortName")), StringComparer.Ordinal)
            .ThenBy(x => Convert.ToString(x.Cells.GetValueOrDefault("fullName")), StringComparer.Ordinal)
            .ThenBy(x => x.RowKey, StringComparer.Ordinal)
            .ToList();

    private static Dictionary<string, object?> BuildStackIdentityCells(
        WorkAssignmentReport report,
        WorkAssignment? assignment,
        UserRef? assignee,
        IReadOnlyCollection<string> identityColumns,
        int sourceReportCount)
    {
        var cells = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in identityColumns)
        {
            cells[column] = column switch
            {
                "periodKey" => report.PeriodKey,
                "periodInstanceKey" => report.PeriodInstanceKey,
                "unitSymbol" => assignee?.UnitSymbol,
                "unitShortName" => FirstNonBlank(assignee?.UnitShortName, assignee?.UnitName, assignee?.UnitSymbol),
                "userName" => assignee?.Username,
                "fullName" => FirstNonBlank(assignee?.FullName, assignee?.Username),
                "workAssignmentId" => assignment?.Id ?? report.WorkAssignmentId,
                "reportId" => report.Id,
                "approvedAtUtc" => report.ApprovedAtUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                "sourceReportCount" => sourceReportCount,
                _ => null
            };
        }

        return cells;
    }

    private static UserRef? ResolveReportAssignee(WorkAssignment? assignment, WorkAssignmentReport report)
        => assignment?.Assignees?.FirstOrDefault(x => x.UserId == report.AssigneeUserId)
           ?? assignment?.Assignees?.FirstOrDefault();

    private static object? ToStackCellObject(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => value.EnumerateArray().Select(ToStackCellObject).Where(x => x is not null).ToList(),
            _ => null
        };
    }

    private static bool HasStackCellValue(JsonElement value)
        => ToStackCellObject(value) switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            List<object?> list => list.Count > 0,
            _ => true
        };

    private static List<DynamicFormAggregateRowDto> BuildDynamicFormMetricRows(
        List<WorkAssignmentReport> reports,
        DynamicFormTableContract contract,
        List<MetricContract> metrics,
        List<string> warnings)
    {
        if (contract.TableMode == "APPEND_ROWS")
            return BuildDynamicFormAppendRowsMetricRows(reports, contract, metrics, warnings);

        if (contract.TableMode == "APPEND_COLUMNS")
            return BuildDynamicFormAppendColumnsMetricRows(reports, contract, metrics, warnings);

        if (contract.TableMode == "MATRIX")
            return BuildDynamicFormMatrixMetricRows(reports, contract, metrics, warnings);

        var acc = metrics.ToDictionary(
            x => x.MetricKey,
            x => new MetricAccumulator(x),
            StringComparer.Ordinal);

        foreach (var report in reports)
        {
            var policy = WorkReportCumulativeContributionPolicy.FromReport(report);
            if (!policy.IncludesReport)
                continue;

            var allowedMetrics = metrics
                .Where(metric => policy.ShouldIncludeTableMetric(
                    contract.BlockId,
                    metric.MetricKey,
                    metric.RowKey,
                    metric.ColumnKey,
                    null))
                .ToList();
            if (allowedMetrics.Count == 0)
                continue;

            using var valuesReader = ResolveReportBlockValuesReader(report.TableValuesJson, contract.BlockId);
            var fallbackValues = valuesReader is null
                ? ResolveLegacyReportValues(report, warnings)
                : null;
            foreach (var metric in allowedMetrics)
            {
                if (metric.Index < 0)
                    continue;

                var sourceKey = $"index:{metric.Index}";
                if (!policy.ShouldIncludeTableMetric(
                        contract.BlockId,
                        metric.MetricKey,
                        metric.RowKey,
                        metric.ColumnKey,
                        sourceKey))
                    continue;

                var value = valuesReader is not null
                    ? valuesReader.ReadDecimal(metric.Index)
                    : metric.Index < (fallbackValues?.Count ?? 0)
                        ? fallbackValues![metric.Index]
                        : null;
                acc[metric.MetricKey].Add(value);
            }
        }

        return acc.Values
            .OrderBy(x => x.Metric.Index)
            .Select(x => x.ToDto())
            .ToList();
    }

    private static List<DynamicFormAggregateRowDto> BuildDynamicFormAppendRowsMetricRows(
        List<WorkAssignmentReport> reports,
        DynamicFormTableContract contract,
        List<MetricContract> metrics,
        List<string> warnings)
    {
        var acc = metrics.ToDictionary(
            x => x.MetricKey,
            x => new MetricAccumulator(x),
            StringComparer.Ordinal);

        foreach (var report in reports)
        {
            var policy = WorkReportCumulativeContributionPolicy.FromReport(report);
            if (!policy.IncludesReport)
                continue;

            var block = ExtractReportTableBlock(report.TableValuesJson, contract.BlockId);
            if (block?.Rows is not { Count: > 0 })
            {
                warnings.Add("Một số báo cáo thiếu dữ liệu dòng phát sinh nên đã được bỏ qua khi tổng hợp.");
                continue;
            }

            foreach (var row in block.Rows)
            {
                if (row.Cells is null || row.Cells.Count == 0)
                    continue;

                foreach (var metric in metrics)
                {
                    if (!row.Cells.TryGetValue(metric.ColumnKey, out var value))
                        continue;

                    var rowSource = NormalizeMetricPart(row.RowInstanceId, $"row:{row.RowOrder.GetValueOrDefault()}");
                    var sourceKey = $"{rowSource}:{metric.ColumnKey}";
                    if (!policy.ShouldIncludeTableMetric(
                            contract.BlockId,
                            metric.MetricKey,
                            metric.RowKey,
                            metric.ColumnKey,
                            sourceKey))
                        continue;

                    acc[metric.MetricKey].Add(ToNullableDecimal(value));
                }
            }
        }

        return acc.Values
            .OrderBy(x => x.Metric.Index)
            .Select(x => x.ToDto())
            .ToList();
    }

    private static List<DynamicFormAggregateRowDto> BuildDynamicFormAppendColumnsMetricRows(
        List<WorkAssignmentReport> reports,
        DynamicFormTableContract contract,
        List<MetricContract> metrics,
        List<string> warnings)
    {
        var acc = metrics.ToDictionary(
            x => x.MetricKey,
            x => new MetricAccumulator(x),
            StringComparer.Ordinal);

        foreach (var report in reports)
        {
            var policy = WorkReportCumulativeContributionPolicy.FromReport(report);
            if (!policy.IncludesReport)
                continue;

            var block = ExtractReportTableBlock(report.TableValuesJson, contract.BlockId);
            if (block?.Columns is not { Count: > 0 })
            {
                warnings.Add("Một số báo cáo thiếu dữ liệu cột phát sinh nên đã được bỏ qua khi tổng hợp.");
                continue;
            }

            foreach (var column in block.Columns)
            {
                if (column.Cells is null || column.Cells.Count == 0)
                    continue;

                foreach (var metric in metrics)
                {
                    if (!column.Cells.TryGetValue(metric.RowKey, out var value))
                        continue;

                    var columnSource = NormalizeMetricPart(column.ColumnInstanceId, $"column:{column.ColumnOrder.GetValueOrDefault()}");
                    var sourceKey = $"{columnSource}:{metric.RowKey}";
                    if (!policy.ShouldIncludeTableMetric(
                            contract.BlockId,
                            metric.MetricKey,
                            metric.RowKey,
                            metric.ColumnKey,
                            sourceKey))
                        continue;

                    acc[metric.MetricKey].Add(ToNullableDecimal(value));
                }
            }
        }

        return acc.Values
            .OrderBy(x => x.Metric.Index)
            .Select(x => x.ToDto())
            .ToList();
    }

    private static List<DynamicFormAggregateRowDto> BuildDynamicFormMatrixMetricRows(
        List<WorkAssignmentReport> reports,
        DynamicFormTableContract contract,
        List<MetricContract> metrics,
        List<string> warnings)
    {
        var acc = metrics.ToDictionary(
            x => x.MetricKey,
            x => new MetricAccumulator(x),
            StringComparer.Ordinal);

        foreach (var report in reports)
        {
            var policy = WorkReportCumulativeContributionPolicy.FromReport(report);
            if (!policy.IncludesReport)
                continue;

            var block = ExtractReportTableBlock(report.TableValuesJson, contract.BlockId);
            if (block?.Cells is not { Count: > 0 })
            {
                warnings.Add("Một số báo cáo thiếu dữ liệu ô ma trận nên đã được bỏ qua khi tổng hợp.");
                continue;
            }

            foreach (var cell in block.Cells)
            {
                var metricKey = string.IsNullOrWhiteSpace(cell.MetricKey)
                    ? BuildMetricKey(
                        contract.BlockId,
                        NormalizeMetricPart(cell.RowKey, "row"),
                        NormalizeMetricPart(cell.ColumnKey, "column"))
                    : cell.MetricKey.Trim();

                if (!acc.TryGetValue(metricKey, out var metricAcc))
                    continue;

                var rowKey = NormalizeMetricPart(cell.RowKey, "row");
                var columnKey = NormalizeMetricPart(cell.ColumnKey, "column");
                if (!policy.ShouldIncludeTableMetric(
                        contract.BlockId,
                        metricKey,
                        rowKey,
                        columnKey,
                        metricKey))
                    continue;

                metricAcc.Add(ToNullableDecimal(cell.Value));
            }
        }

        return acc.Values
            .OrderBy(x => x.Metric.Index)
            .Select(x => x.ToDto())
            .ToList();
    }

    private static Values1DCompression.Values1DReader? ResolveReportBlockValuesReader(
        string? tableValuesJson,
        string blockId)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(tableValuesJson);
            if (!document.RootElement.TryGetProperty("blocks", out var blocks) ||
                blocks.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var block in blocks.EnumerateArray())
            {
                var currentBlockId = block.TryGetProperty("blockId", out var blockIdElement) &&
                                     blockIdElement.ValueKind == JsonValueKind.String
                    ? blockIdElement.GetString()
                    : null;
                if (!string.Equals(NormalizeBlockId(currentBlockId), blockId, StringComparison.Ordinal))
                    continue;

                return Values1DCompression.CreateBlockReader(block);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static List<decimal?> ResolveLegacyReportValues(
        WorkAssignmentReport report,
        List<string> warnings)
    {
        warnings.Add("Một số báo cáo thiếu dữ liệu bảng theo block; hệ thống dùng dữ liệu ô cũ để tổng hợp.");
        return DeserializeValues1D(report.Values1DJson);
    }

    private static bool ReportCanContributeAnyMetric(
        WorkAssignmentReport report,
        DynamicFormTableContract contract,
        List<MetricContract> metrics)
    {
        var policy = WorkReportCumulativeContributionPolicy.FromReport(report);
        return policy.IncludesReport &&
               metrics.Any(metric => policy.ShouldIncludeTableMetric(
                   contract.BlockId,
                   metric.MetricKey,
                   metric.RowKey,
                   metric.ColumnKey,
                   null));
    }

    private static ReportTableValuesBlock? ExtractReportTableBlock(
        string? tableValuesJson,
        string blockId)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson))
            return null;

        try
        {
            var expandedTableValuesJson = Values1DCompression.ExpandTableValuesJson(tableValuesJson, JsonOptions) ?? tableValuesJson;
            var root = JsonSerializer.Deserialize<ReportTableValuesRoot>(expandedTableValuesJson, JsonOptions);
            return root?.Blocks?
                .FirstOrDefault(x => string.Equals(
                    NormalizeBlockId(x.BlockId),
                    blockId,
                    StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DynamicFormTableContract ResolveDynamicFormTableContract(
        DynamicFormTemplate template,
        string? requestedBlockId,
        string? requestedTableMode)
    {
        var block = ResolveDynamicFormBlock(template, requestedBlockId);
        if (block is null)
        {
            if (!string.IsNullOrWhiteSpace(requestedBlockId))
                throw DynamicFormBlockNotFound(template.Id, requestedBlockId);

            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FORM_EXCEL_BLOCK_REQUIRED,
                new { dynamicFormTemplateId = template.Id });
        }

        var blockId = NormalizeBlockId(block.BlockId ?? block.Id ?? "excel_block");
        var requestedBlock = string.IsNullOrWhiteSpace(requestedBlockId)
            ? null
            : NormalizeBlockId(requestedBlockId);
        if (requestedBlock is not null &&
            !DynamicFormBlockMatchesRequest(block, requestedBlock))
        {
            throw DynamicFormBlockNotFound(template.Id, requestedBlockId);
        }

        var tableMode = string.IsNullOrWhiteSpace(block.TableMode)
            ? "FIXED_GRID"
            : block.TableMode.Trim().ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(requestedTableMode) &&
            !string.Equals(tableMode, requestedTableMode.Trim().ToUpperInvariant(), StringComparison.Ordinal))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FORM_TABLE_MODE_MISMATCH,
                new
                {
                    dynamicFormTemplateId = template.Id,
                    blockId,
                    requestedTableMode = requestedTableMode.Trim().ToUpperInvariant(),
                    tableMode
                });
        }

        if (tableMode == "APPEND_ROWS")
        {
            var appendRowMetrics = BuildConfiguredMetricMap(block, blockId, tableMode);
            return new DynamicFormTableContract(blockId, tableMode, appendRowMetrics);
        }

        if (tableMode == "APPEND_COLUMNS")
        {
            var appendColumnMetrics = BuildConfiguredMetricMap(block, blockId, tableMode);
            return new DynamicFormTableContract(blockId, tableMode, appendColumnMetrics);
        }

        if (tableMode == "MATRIX")
        {
            var matrixMap = BuildConfiguredMetricMap(block, blockId, tableMode);
            return new DynamicFormTableContract(blockId, tableMode, matrixMap);
        }

        if (tableMode == "SUMMARY_TEMPLATE")
        {
            var summaryTemplate = ResolveSummaryTemplateContract(block);
            var summaryMetrics = BuildSummaryTemplateMetricMap(summaryTemplate);
            if (summaryMetrics.Count == 0)
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.DYNAMIC_FORM_SUMMARY_METRICS_REQUIRED,
                    new { dynamicFormTemplateId = template.Id, blockId, tableMode });

            return new DynamicFormTableContract(blockId, tableMode, summaryMetrics, summaryTemplate);
        }

        if (tableMode != "FIXED_GRID")
        {
            throw DynamicFormTableContractInvalid(template.Id, blockId, tableMode, "TABLE_MODE_UNSUPPORTED");
        }

        var indexMap = BuildConfiguredMetricMap(block, blockId, tableMode);
        return new DynamicFormTableContract(blockId, tableMode, indexMap);
    }

    private static DynamicFormExcelBlockJson? ResolveDynamicFormBlock(
        DynamicFormTemplate template,
        string? requestedBlockId)
    {
        var requested = string.IsNullOrWhiteSpace(requestedBlockId)
            ? null
            : NormalizeBlockId(requestedBlockId);

        var blocks = ReadDynamicFormBlocks(template.BlocksJson);
        if (blocks.Count > 0)
        {
            if (requested is null)
                return blocks[0];

            return blocks.FirstOrDefault(block =>
                DynamicFormBlockMatchesRequest(block, requested));
        }

        if (string.IsNullOrWhiteSpace(template.ExcelBlockJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<DynamicFormExcelBlockJson>(
                template.ExcelBlockJson,
                JsonOptions);
        }
        catch (JsonException)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FORM_EXCEL_BLOCK_INVALID,
                new { dynamicFormTemplateId = template.Id });
        }
    }

    private static List<DynamicFormExcelBlockJson> ReadDynamicFormBlocks(string? blocksJson)
    {
        if (string.IsNullOrWhiteSpace(blocksJson))
            return new List<DynamicFormExcelBlockJson>();

        try
        {
            var blocks = JsonSerializer.Deserialize<List<DynamicFormExcelBlockJson>>(
                blocksJson,
                JsonOptions);

            return blocks?
                .Where(x => x is not null)
                .ToList()
                ?? new List<DynamicFormExcelBlockJson>();
        }
        catch (JsonException)
        {
            throw AppExceptionFactory.BadRequest(AppErrorCode.DYNAMIC_FORM_BLOCKS_JSON_INVALID);
        }
    }

    private static SummaryTemplateContract ResolveSummaryTemplateContract(DynamicFormExcelBlockJson block)
    {
        var sourceBlockIdRaw = FirstNonBlank(block.SourceBlockId, block.OutputLayout?.SourceBlockId);
        if (string.IsNullOrWhiteSpace(sourceBlockIdRaw))
            throw AppExceptionFactory.BadRequest(AppErrorCode.DYNAMIC_FORM_SUMMARY_SOURCE_REQUIRED);

        var sourceBlockId = NormalizeBlockId(sourceBlockIdRaw);
        var sourceTableMode = NormalizeSourceTableMode(
            FirstNonBlank(block.SourceTableMode, block.OutputLayout?.SourceTableMode));
        var groupBy = NormalizeSummaryGroupBy(block.GroupBy ?? block.OutputLayout?.GroupBy);
        var rawLayout = block.RowLayout ?? block.OutputLayout?.RowLayout ?? new List<SummaryTemplateRowLayoutJson>();
        var rowLayout = rawLayout
            .Select((item, index) =>
            {
                var metrics = (item.Metrics ?? new List<string>())
                    .Select(x => x?.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                return new SummaryTemplateRowLayout(
                    index,
                    NormalizeSummaryRepeatFor(item.RepeatFor),
                    Math.Clamp(item.RowsPerUnit.GetValueOrDefault(1), 1, 100),
                    string.IsNullOrWhiteSpace(item.Label) ? null : item.Label.Trim(),
                    metrics);
            })
            .Where(x => x.Metrics.Count > 0)
            .ToList();

        if (rowLayout.Count == 0)
            throw AppExceptionFactory.BadRequest(AppErrorCode.DYNAMIC_FORM_SUMMARY_METRICS_REQUIRED);

        var rowsPerGroup = rowLayout.Sum(x => Math.Max(x.RowsPerUnit, x.Metrics.Count));
        return new SummaryTemplateContract(sourceBlockId, sourceTableMode, groupBy, rowLayout, Math.Max(1, rowsPerGroup));
    }

    private static List<MetricContract> BuildSummaryTemplateMetricMap(SummaryTemplateContract contract)
    {
        var metrics = new List<MetricContract>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rowOffset = 0;
        foreach (var layout in contract.RowLayout.OrderBy(x => x.Index))
        {
            var itemSpan = Math.Max(layout.RowsPerUnit, layout.Metrics.Count);
            for (var metricIndex = 0; metricIndex < layout.Metrics.Count; metricIndex++)
            {
                var metricKey = layout.Metrics[metricIndex];
                if (!seen.Add(metricKey))
                    continue;

                metrics.Add(new MetricContract(
                    metrics.Count,
                    layout.RepeatFor,
                    layout.Label ?? $"metric_{metrics.Count + 1}",
                    metricKey,
                    rowOffset + Math.Min(metricIndex, itemSpan - 1),
                    contract.RowsPerGroup,
                    layout.Label));
            }

            rowOffset += itemSpan;
        }

        return metrics;
    }

    private static List<MetricContract> BuildConfiguredMetricMap(
        DynamicFormExcelBlockJson block,
        string blockId,
        string tableMode)
    {
        var knownByMetricKey = NormalizeMetricMap(block.IndexMap, blockId)
            .GroupBy(x => x.MetricKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var metrics = new Dictionary<string, MetricContract>(StringComparer.Ordinal);

        void AddMetric(MetricContract? metric)
        {
            if (metric is null || metrics.ContainsKey(metric.MetricKey))
                return;

            metrics[metric.MetricKey] = metric;
        }

        void AddMetricKey(string? metricKey, int fallbackIndex)
        {
            if (string.IsNullOrWhiteSpace(metricKey))
                return;

            var normalizedMetricKey = metricKey.Trim();
            AddMetric(knownByMetricKey.TryGetValue(normalizedMetricKey, out var known)
                ? known
                : ParseConfiguredMetricKey(normalizedMetricKey, tableMode, block.W, fallbackIndex));
        }

        if (block.MetricRules is { Count: > 0 })
        {
            for (var i = 0; i < block.MetricRules.Count; i++)
                AddMetricKey(block.MetricRules[i].MetricKey, i);
        }

        if (block.MetricLabelTargets is { Count: > 0 })
        {
            for (var i = 0; i < block.MetricLabelTargets.Count; i++)
            {
                var target = block.MetricLabelTargets[i];
                if (!string.IsNullOrWhiteSpace(target.MetricKey))
                {
                    AddMetricKey(target.MetricKey, i);
                    continue;
                }

                if (target.Range is null || block.DataRect is null)
                    continue;

                foreach (var metric in ExpandConfiguredMetricRange(blockId, tableMode, block.DataRect, target.Range, block.W, block.H))
                    AddMetric(metric);
            }
        }

        if (metrics.Count == 0 && tableMode is "FIXED_GRID" or "MATRIX")
        {
            foreach (var metric in BuildCoordinateMetricMap(block, blockId))
                AddMetric(metric);
        }

        return metrics.Values
            .OrderBy(x => x.Index)
            .ThenBy(x => x.MetricKey, StringComparer.Ordinal)
            .ToList();
    }

    private static bool DynamicFormBlockMatchesRequest(
        DynamicFormExcelBlockJson block,
        string requested)
    {
        if (string.Equals(
                NormalizeBlockId(block.BlockId ?? block.Id ?? "excel_block"),
                requested,
                StringComparison.Ordinal))
        {
            return true;
        }

        var dynamicExcelTemplateId = NormalizeOptionalText(block.DynamicExcelTemplateId);
        return dynamicExcelTemplateId is not null &&
               string.Equals(dynamicExcelTemplateId, requested, StringComparison.Ordinal);
    }

    private static List<MetricContract> NormalizeMetricMap(
        List<DynamicFormIndexMapItem>? items,
        string blockId)
    {
        if (items is null || items.Count == 0)
            return new List<MetricContract>();

        return items
            .Select((item, fallbackIndex) =>
            {
                var index = item.Index >= 0 ? item.Index : fallbackIndex;
                var rowKey = NormalizeMetricPart(item.RowKey, $"row_{fallbackIndex + 1}");
                var columnKey = NormalizeMetricPart(item.ColumnKey, "value");
                var metricKey = string.IsNullOrWhiteSpace(item.MetricKey)
                    ? BuildMetricKey(blockId, rowKey, columnKey)
                    : item.MetricKey.Trim();

                return new MetricContract(index, rowKey, columnKey, metricKey);
            })
            .GroupBy(x => x.MetricKey, StringComparer.Ordinal)
            .Select(x => x.First())
            .OrderBy(x => x.Index)
            .ToList();
    }

    private static IEnumerable<MetricContract> BuildCoordinateMetricMap(
        DynamicFormExcelBlockJson block,
        string blockId)
    {
        if (block.DataRect is null)
            yield break;

        var prefix = NormalizeCoordinateMetricPrefix(blockId);
        var index = 0;
        for (var r = block.DataRect.R0; r <= block.DataRect.R1; r++)
        {
            for (var c = block.DataRect.C0; c <= block.DataRect.C1; c++)
            {
                if (IsSpecialCoordinate(block.SpecialRanges, r, c))
                    continue;

                var rowKey = $"R{r + 1}";
                var columnKey = $"C{c + 1}";
                yield return new MetricContract(
                    index,
                    rowKey,
                    columnKey,
                    $"{prefix}.{rowKey}.{columnKey}");
                index++;
            }
        }
    }

    private static bool IsSpecialCoordinate(
        List<DynamicFormSpecialRangeJson>? specialRanges,
        int r,
        int c)
        => specialRanges?.Any(range =>
            r >= range.R0 &&
            r <= range.R1 &&
            c >= range.C0 &&
            c <= range.C1) == true;

    private static string NormalizeCoordinateMetricPrefix(string blockId)
    {
        var chars = blockId
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_')
            .ToArray();
        var normalized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "EXCEL_BLOCK" : normalized;
    }

    private static MetricContract ParseConfiguredMetricKey(
        string metricKey,
        string tableMode,
        int? width,
        int fallbackIndex)
    {
        var rowKey = TryReadMetricSegment(metricKey, ".row:");
        var columnKey = TryReadMetricSegment(metricKey, ".column:");
        if (!string.IsNullOrWhiteSpace(rowKey) && !string.IsNullOrWhiteSpace(columnKey))
        {
            rowKey = NormalizeMetricPart(rowKey, $"row_{fallbackIndex + 1}");
            columnKey = NormalizeMetricPart(columnKey, "value");
            return new MetricContract(
                IndexFromRowColumn(rowKey, columnKey, width) ?? fallbackIndex,
                rowKey,
                columnKey,
                metricKey);
        }

        if (tableMode == "APPEND_ROWS")
        {
            columnKey = TryReadMetricSegment(metricKey, ".column:");
            columnKey = NormalizeMetricPart(columnKey, $"col_{fallbackIndex + 1}");
            return new MetricContract(
                IndexFromOrdinalPart(columnKey, "col_") ?? fallbackIndex,
                "APPEND_ROWS",
                columnKey,
                metricKey);
        }

        if (tableMode == "APPEND_COLUMNS")
        {
            rowKey = TryReadMetricSegment(metricKey, ".row:");
            rowKey = NormalizeMetricPart(rowKey, $"row_{fallbackIndex + 1}");
            return new MetricContract(
                IndexFromOrdinalPart(rowKey, "row_") ?? fallbackIndex,
                rowKey,
                "APPEND_COLUMNS",
                metricKey);
        }

        return new MetricContract(
            fallbackIndex,
            tableMode == "APPEND_ROWS" ? "APPEND_ROWS" : $"row_{fallbackIndex + 1}",
            tableMode == "APPEND_COLUMNS" ? "APPEND_COLUMNS" : "value",
            metricKey);
    }

    private static IEnumerable<MetricContract> ExpandConfiguredMetricRange(
        string blockId,
        string tableMode,
        MetricRangeJson dataRect,
        MetricRangeJson range,
        int? width,
        int? height)
    {
        var r0 = Math.Max(dataRect.R0, range.R0);
        var c0 = Math.Max(dataRect.C0, range.C0);
        var r1 = Math.Min(dataRect.R1, range.R1);
        var c1 = Math.Min(dataRect.C1, range.C1);
        if (r1 < r0 || c1 < c0)
            yield break;

        var w = width.GetValueOrDefault(dataRect.C1 - dataRect.C0 + 1);
        var h = height.GetValueOrDefault(dataRect.R1 - dataRect.R0 + 1);

        if (tableMode == "APPEND_ROWS")
        {
            for (var c = c0; c <= c1; c++)
            {
                var columnOffset = c - dataRect.C0;
                if (columnOffset < 0 || columnOffset >= w)
                    continue;

                var columnKey = $"col_{columnOffset + 1}";
                yield return new MetricContract(
                    columnOffset,
                    "APPEND_ROWS",
                    columnKey,
                    $"table:{blockId}.column:{columnKey}");
            }

            yield break;
        }

        if (tableMode == "APPEND_COLUMNS")
        {
            for (var r = r0; r <= r1; r++)
            {
                var rowOffset = r - dataRect.R0;
                if (rowOffset < 0 || rowOffset >= h)
                    continue;

                var rowKey = $"row_{rowOffset + 1}";
                yield return new MetricContract(
                    rowOffset,
                    rowKey,
                    "APPEND_COLUMNS",
                    $"table:{blockId}.row:{rowKey}");
            }

            yield break;
        }

        for (var r = r0; r <= r1; r++)
        {
            for (var c = c0; c <= c1; c++)
            {
                var rowOffset = r - dataRect.R0;
                var columnOffset = c - dataRect.C0;
                if (rowOffset < 0 || rowOffset >= h || columnOffset < 0 || columnOffset >= w)
                    continue;

                var rowKey = $"row_{rowOffset + 1}";
                var columnKey = $"col_{columnOffset + 1}";
                yield return new MetricContract(
                    rowOffset * w + columnOffset,
                    rowKey,
                    columnKey,
                    BuildMetricKey(blockId, rowKey, columnKey));
            }
        }
    }

    private static string? TryReadMetricSegment(string metricKey, string marker)
    {
        var start = metricKey.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += marker.Length;
        var end = metricKey.IndexOf('.', start);
        return (end < 0 ? metricKey[start..] : metricKey[start..end]).Trim();
    }

    private static int? IndexFromRowColumn(string rowKey, string columnKey, int? width)
    {
        var rowIndex = IndexFromOrdinalPart(rowKey, "row_");
        var columnIndex = IndexFromOrdinalPart(columnKey, "col_");
        var w = width.GetValueOrDefault();
        if (!rowIndex.HasValue || !columnIndex.HasValue || w <= 0)
            return null;

        return rowIndex.Value * w + columnIndex.Value;
    }

    private static int? IndexFromOrdinalPart(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        return int.TryParse(value[prefix.Length..], out var n) && n > 0
            ? n - 1
            : null;
    }

    private static string NormalizeBlockId(string? value)
        => string.IsNullOrWhiteSpace(value) ? "excel_block" : value.Trim();

    private static string NormalizeMetricPart(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string BuildMetricKey(string blockId, string rowKey, string columnKey)
        => $"table:{blockId}.row:{rowKey}.column:{columnKey}";

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static string? NormalizeSourceTableMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().ToUpperInvariant();
        return normalized is "FIXED_GRID" or "APPEND_ROWS" or "APPEND_COLUMNS" or "MATRIX"
            ? normalized
            : null;
    }

    private static List<string> NormalizeSummaryGroupBy(List<string>? values)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "UNIT",
            "ASSIGNMENT",
            "ROOT_ASSIGNMENT",
            "USER",
            "LABEL",
            "PERIOD"
        };

        var groupBy = (values ?? new List<string>())
            .Select(x => x?.Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x) && allowed.Contains(x!))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return groupBy.Count > 0 ? groupBy : new List<string> { "UNIT" };
    }

    private static string NormalizeSummaryRepeatFor(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "selectedUnits"
            : value.Trim();

        return normalized is "selectedUnits" or "scopeAssignments" or "none"
            ? normalized
            : "selectedUnits";
    }

    private static UserRef? ResolveAssignmentAssignee(WorkAssignment assignment)
        => assignment.Assignees?.FirstOrDefault();

    private static string NormalizeSummaryGroupKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

    private static string BuildSummaryGroupLabel(
        WorkAssignment assignment,
        UserRef? assignee,
        string groupType)
    {
        if (groupType == "UNIT")
        {
            var unitLabel = FirstNonBlank(assignee?.UnitShortName, assignee?.UnitSymbol, assignee?.UnitName);
            if (!string.IsNullOrWhiteSpace(unitLabel))
                return unitLabel;
        }

        return FirstNonBlank(assignee?.FullName, assignee?.Username, assignment.Code, assignment.Id) ?? assignment.Id;
    }

    private static int ToSafeInt(long value)
    {
        if (value <= 0)
            return 0;

        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    private static AggregateTableResponse BuildEmptyAggregateResponse(AggregateTableRequest req)
    {
        return new AggregateTableResponse
        {
            DynamicExcelId = req.DynamicExcelId,
            PeriodScopeMode = req.PeriodScopeMode,
            PeriodKey = req.PeriodKey,
            PeriodKeyFrom = req.PeriodKeyFrom,
            PeriodKeyTo = req.PeriodKeyTo,
            SelectedUnitIds = req.SelectedUnitIds ?? new List<string>(),
            AggregateMode = req.AggregateMode,
            PeriodCount = 0,
            IncludedPeriodKeys = new List<string>(),
            MetaColumns = new List<string>(),
            Rows = new List<AggregateTableRowDto>(),
            Sources = new List<AggregateSourceRowDto>(),
        };
    }

    private static AggregateTableResponse BuildEmptyAggregateResponse(
        AggregateTableRequest req,
        WorkAssignment assignment,
        List<AggregateSourceRowDto> sources)
    {
        return new AggregateTableResponse
        {
            DynamicExcelId = assignment.DynamicExcelId,
            DynamicExcelCode = assignment.DynamicExcelCode,
            DynamicExcelName = assignment.DynamicExcelName,
            PeriodScopeMode = req.PeriodScopeMode,
            PeriodKey = req.PeriodKey,
            PeriodKeyFrom = req.PeriodKeyFrom,
            PeriodKeyTo = req.PeriodKeyTo,
            SelectedUnitIds = req.SelectedUnitIds ?? new List<string>(),
            AggregateMode = req.AggregateMode,
            PeriodCount = 0,
            IncludedPeriodKeys = new List<string>(),
            MetaColumns = new List<string>(),
            Rows = new List<AggregateTableRowDto>(),
            Sources = sources,
        };
    }

    private static (WorkAssignmentReport first, int valueCount, List<string> includedPeriodKeys) GetMeta(
        List<WorkAssignmentReport> reports)
    {
        var first = reports[0];
        var valueCount = Math.Max(0, first.DataRectR1 - first.DataRectR0 + 1)
            * Math.Max(0, first.DataRectC1 - first.DataRectC0 + 1);

        var includedPeriodKeys = reports
            .Select(x => x.PeriodKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        return (first, valueCount, includedPeriodKeys);
    }

    private AggregateTableResponse BuildSumByCellResult(
        AggregateTableRequest req,
        List<WorkAssignment> assignments,
        List<WorkAssignmentReport> reports,
        List<AggregateSourceRowDto> sources)
    {
        var (first, valueCount, includedPeriodKeys) = GetMeta(reports);
        var row = new AggregateTableRowDto
        {
            Values = Enumerable.Repeat<decimal?>(null, valueCount).ToList(),
        };

        foreach (var report in reports)
        {
            var values = ExtractRectValues(report);
            SumInto(row.Values, values);
        }

        return BuildResponse(req, assignments[0], first, includedPeriodKeys, new List<string>(), new List<AggregateTableRowDto> { row }, sources);
    }

    private AggregateTableResponse BuildRowsByUserResult(
        AggregateTableRequest req,
        List<WorkAssignment> assignments,
        List<WorkAssignmentReport> reports,
        List<AggregateSourceRowDto> sources)
    {
        var (first, valueCount, includedPeriodKeys) = GetMeta(reports);
        var rows = BuildGroupedRows(assignments, reports, valueCount);

        return BuildResponse(
            req,
            assignments[0],
            first,
            includedPeriodKeys,
            new List<string> { "userId", "userName", "fullName", "unitSymbol", "unitShortName" },
            rows,
            sources,
            aggregateMode: "HORIZONTAL_BY_USER");
    }

    private AggregateTableResponse BuildColumnsByUserResult(
        AggregateTableRequest req,
        List<WorkAssignment> assignments,
        List<WorkAssignmentReport> reports,
        List<AggregateSourceRowDto> sources)
    {
        var (first, valueCount, includedPeriodKeys) = GetMeta(reports);
        var groupedRows = BuildGroupedRows(assignments, reports, valueCount);
        var dataRowCount = Math.Max(0, first.DataRectR1 - first.DataRectR0 + 1);
        var dataColumnCount = Math.Max(0, first.DataRectC1 - first.DataRectC0 + 1);
        var rows = BuildTemplateRowsByUser(groupedRows, dataRowCount, dataColumnCount);

        return BuildResponse(
            req,
            assignments[0],
            first,
            includedPeriodKeys,
            new List<string> { "user", "unit", "sourceRowNumber" },
            rows,
            sources,
            aggregateMode: "VERTICAL_BY_USER");
    }

    private static List<AggregateTableRowDto> BuildGroupedRows(
        List<WorkAssignment> assignments,
        List<WorkAssignmentReport> reports,
        int valueCount)
    {
        var assignmentMap = assignments.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
        var rowMap = new Dictionary<string, AggregateTableRowDto>(StringComparer.Ordinal);

        foreach (var report in reports)
        {
            assignmentMap.TryGetValue(report.WorkAssignmentId, out var assignment);

            var assignee = assignment?.Assignees?.FirstOrDefault(x => x.UserId == report.AssigneeUserId)
                ?? assignment?.Assignees?.FirstOrDefault();

            var userId = report.AssigneeUserId ?? assignee?.UserId ?? string.Empty;
            if (!rowMap.TryGetValue(userId, out var row))
            {
                row = new AggregateTableRowDto
                {
                    WorkAssignmentId = assignment?.Id ?? report.WorkAssignmentId,
                    UserId = userId,
                    UserName = assignee?.Username,
                    FullName = assignee?.FullName,
                    UnitSymbol = assignee?.UnitSymbol,
                    UnitShortName = assignee?.UnitShortName,
                    Values = Enumerable.Repeat<decimal?>(null, valueCount).ToList(),
                };

                rowMap[userId] = row;
            }

            SumInto(row.Values, ExtractRectValues(report));
        }

        return rowMap.Values
            .OrderBy(x => x.UnitShortName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.FullName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.UserName ?? string.Empty, StringComparer.Ordinal)
            .ToList();
    }

    private static List<AggregateTableRowDto> BuildTemplateRowsByUser(
        List<AggregateTableRowDto> groupedRows,
        int dataRowCount,
        int dataColumnCount)
    {
        if (groupedRows.Count == 0 || dataRowCount <= 0 || dataColumnCount <= 0)
            return groupedRows;

        var rows = new List<AggregateTableRowDto>(groupedRows.Count * dataRowCount);
        foreach (var group in groupedRows)
        {
            for (var rowIndex = 0; rowIndex < dataRowCount; rowIndex++)
            {
                var values = new List<decimal?>(dataColumnCount);
                for (var columnIndex = 0; columnIndex < dataColumnCount; columnIndex++)
                {
                    var valueIndex = rowIndex * dataColumnCount + columnIndex;
                    values.Add(valueIndex >= 0 && valueIndex < group.Values.Count
                        ? group.Values[valueIndex]
                        : null);
                }

                rows.Add(new AggregateTableRowDto
                {
                    WorkAssignmentId = group.WorkAssignmentId,
                    UserId = group.UserId,
                    UserName = group.UserName,
                    FullName = group.FullName,
                    UnitSymbol = group.UnitSymbol,
                    UnitShortName = group.UnitShortName,
                    SourceRowIndex = rowIndex,
                    SourceRowNumber = rowIndex + 1,
                    SourceRowKey = $"row_{rowIndex + 1}",
                    Values = values,
                });
            }
        }

        return rows;
    }

    private static AggregateTableResponse BuildResponse(
        AggregateTableRequest req,
        WorkAssignment assignment,
        WorkAssignmentReport first,
        List<string> includedPeriodKeys,
        List<string> metaColumns,
        List<AggregateTableRowDto> rows,
        List<AggregateSourceRowDto> sources,
        string? aggregateMode = null)
    {
        return new AggregateTableResponse
        {
            DynamicExcelId = assignment.DynamicExcelId,
            DynamicExcelCode = assignment.DynamicExcelCode,
            DynamicExcelName = assignment.DynamicExcelName,
            PeriodScopeMode = req.PeriodScopeMode,
            PeriodKey = req.PeriodKey,
            PeriodKeyFrom = req.PeriodKeyFrom,
            PeriodKeyTo = req.PeriodKeyTo,
            SelectedUnitIds = req.SelectedUnitIds ?? new List<string>(),
            AggregateMode = aggregateMode ?? req.AggregateMode,
            PeriodCount = includedPeriodKeys.Count,
            IncludedPeriodKeys = includedPeriodKeys,
            DataRectR0 = first.DataRectR0,
            DataRectC0 = first.DataRectC0,
            DataRectR1 = first.DataRectR1,
            DataRectC1 = first.DataRectC1,
            W = first.W,
            H = first.H,
            MetaColumns = metaColumns,
            Rows = rows,
            Sources = sources,
        };
    }

    private static List<decimal?> ExtractRectValues(WorkAssignmentReport report)
    {
        var flatValues = DeserializeValues1D(report.Values1DJson);
        return DynamicExcelValues1D.ExtractDataRectValues(
            flatValues,
            report.DataRectR0,
            report.DataRectC0,
            report.DataRectR1,
            report.DataRectC1);
    }

    private static List<decimal?> DeserializeValues1D(string? json)
        => Values1DCompression.DeserializeDecimals(json, JsonOptions);

    private static decimal? ToNullableDecimal(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static void SumInto(List<decimal?> target, List<decimal?> source)
    {
        var max = Math.Min(target.Count, source.Count);
        for (var i = 0; i < max; i++)
        {
            target[i] = (target[i] ?? 0m) + (source[i] ?? 0m);
        }
    }

    private sealed record DynamicFormTableContract(
        string BlockId,
        string TableMode,
        List<MetricContract> IndexMap,
        SummaryTemplateContract? SummaryTemplate = null);

    private sealed record MetricContract(
        int Index,
        string RowKey,
        string ColumnKey,
        string MetricKey,
        int? SummaryRowOffset = null,
        int? SummaryRowsPerGroup = null,
        string? SummaryLayoutLabel = null);

    private sealed record SummaryTemplateContract(
        string SourceBlockId,
        string? SourceTableMode,
        List<string> GroupBy,
        List<SummaryTemplateRowLayout> RowLayout,
        int RowsPerGroup);

    private sealed record SummaryTemplateRowLayout(
        int Index,
        string RepeatFor,
        int RowsPerUnit,
        string? Label,
        List<string> Metrics);

    private sealed record SummaryAggregateKey(
        string ScopeId,
        string MetricKey);

    private sealed class DynamicFormExcelBlockJson
    {
        public string? BlockId { get; set; }
        public string? Id { get; set; }
        public string? DynamicExcelTemplateId { get; set; }
        public string? DynamicExcelCode { get; set; }
        public string? TableMode { get; set; }
        public int? W { get; set; }
        public int? H { get; set; }
        public MetricRangeJson? DataRect { get; set; }
        public List<DynamicFormSpecialRangeJson>? SpecialRanges { get; set; }
        public List<DynamicFormIndexMapItem>? IndexMap { get; set; }
        public List<DynamicFormMetricRuleJson>? MetricRules { get; set; }
        public List<DynamicFormMetricLabelTargetJson>? MetricLabelTargets { get; set; }
        public string? SourceBlockId { get; set; }
        public string? SourceTableMode { get; set; }
        public List<string>? GroupBy { get; set; }
        public List<SummaryTemplateRowLayoutJson>? RowLayout { get; set; }
        public SummaryTemplateOutputLayoutJson? OutputLayout { get; set; }
    }

    private sealed class DynamicFormIndexMapItem
    {
        public int Index { get; set; } = -1;
        public string? RowKey { get; set; }
        public string? ColumnKey { get; set; }
        public string? MetricKey { get; set; }
    }

    private sealed class DynamicFormSpecialRangeJson
    {
        public string? Role { get; set; }
        public int R0 { get; set; }
        public int C0 { get; set; }
        public int R1 { get; set; }
        public int C1 { get; set; }
    }

    private sealed class DynamicFormMetricRuleJson
    {
        public string? MetricKey { get; set; }
    }

    private sealed class DynamicFormMetricLabelTargetJson
    {
        public string? MetricKey { get; set; }
        public MetricRangeJson? Range { get; set; }
    }

    private sealed class MetricRangeJson
    {
        public int R0 { get; set; }
        public int C0 { get; set; }
        public int R1 { get; set; }
        public int C1 { get; set; }
    }

    private sealed class SummaryTemplateOutputLayoutJson
    {
        public string? SourceBlockId { get; set; }
        public string? SourceTableMode { get; set; }
        public List<string>? GroupBy { get; set; }
        public List<SummaryTemplateRowLayoutJson>? RowLayout { get; set; }
    }

    private sealed class SummaryTemplateRowLayoutJson
    {
        public string? RepeatFor { get; set; }
        public int? RowsPerUnit { get; set; }
        public string? Label { get; set; }
        public List<string>? Metrics { get; set; }
    }

    private sealed class ReportTableValuesRoot
    {
        public List<ReportTableValuesBlock>? Blocks { get; set; }
    }

    private sealed class ReportTableValuesBlock
    {
        public string? BlockId { get; set; }
        public string? TableMode { get; set; }
        public List<JsonElement>? Values1D { get; set; }
        public List<ReportTableAppendRow>? Rows { get; set; }
        public List<ReportTableAppendColumn>? Columns { get; set; }
        public List<ReportTableMatrixCell>? Cells { get; set; }
    }

    private sealed class ReportTableAppendRow
    {
        public string? RowInstanceId { get; set; }
        public int? RowOrder { get; set; }
        public string? RowKey { get; set; }
        public List<string>? RowLabelCodes { get; set; }
        public Dictionary<string, JsonElement>? Cells { get; set; }
    }

    private sealed class ReportTableAppendColumn
    {
        public string? ColumnInstanceId { get; set; }
        public int? ColumnOrder { get; set; }
        public string? ColumnKey { get; set; }
        public List<string>? ColumnLabelCodes { get; set; }
        public Dictionary<string, JsonElement>? Cells { get; set; }
    }

    private sealed class ReportTableMatrixCell
    {
        public string? RowAxisKey { get; set; }
        public string? RowKey { get; set; }
        public string? ColumnAxisKey { get; set; }
        public string? ColumnKey { get; set; }
        public string? MetricKey { get; set; }
        public JsonElement Value { get; set; }
    }

    private sealed class SummaryAggregateAccumulator
    {
        public SummaryAggregateAccumulator(MetricContract metric)
        {
            Metric = metric;
        }

        public MetricContract Metric { get; }
        public long ValueCount { get; private set; }
        public long ReportCount { get; private set; }
        public decimal Sum { get; private set; }
        public decimal? Min { get; private set; }
        public decimal? Max { get; private set; }

        public void Add(WorkReportTableStatAggregate row)
        {
            ValueCount += row.ValueCount;
            ReportCount += row.ReportCount;
            Sum += row.Sum;

            if (row.Min.HasValue)
                Min = Min.HasValue ? Math.Min(Min.Value, row.Min.Value) : row.Min.Value;

            if (row.Max.HasValue)
                Max = Max.HasValue ? Math.Max(Max.Value, row.Max.Value) : row.Max.Value;
        }
    }

    private sealed class StackedRowAccumulator
    {
        private readonly Dictionary<string, StackMetricAccumulator> _metrics = new(StringComparer.Ordinal);

        public StackedRowAccumulator(string key, Dictionary<string, object?> cells)
        {
            Key = key;
            Cells = cells;
        }

        public string Key { get; }
        public Dictionary<string, object?> Cells { get; }
        public HashSet<string> SourceReportIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SourceAssignmentIds { get; } = new(StringComparer.Ordinal);

        public void AddSource(WorkAssignmentReport report)
        {
            SourceReportIds.Add(report.Id);
            SourceAssignmentIds.Add(report.WorkAssignmentId);
            Cells["sourceReportCount"] = SourceReportIds.Count;
        }

        public void AddMetric(string metricKey, JsonElement value)
        {
            if (!HasStackCellValue(value))
                return;

            if (!_metrics.TryGetValue(metricKey, out var acc))
            {
                acc = new StackMetricAccumulator();
                _metrics[metricKey] = acc;
            }

            acc.Add(value);
        }

        public DynamicFormStackedTableRowDto ToDto(List<MetricContract> metrics)
        {
            foreach (var metric in metrics)
            {
                Cells[metric.MetricKey] = _metrics.TryGetValue(metric.MetricKey, out var acc)
                    ? acc.ToCell()
                    : null;
            }

            return new DynamicFormStackedTableRowDto
            {
                RowKey = Key,
                Cells = Cells,
                SourceReportIds = SourceReportIds.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                SourceAssignmentIds = SourceAssignmentIds.OrderBy(x => x, StringComparer.Ordinal).ToList()
            };
        }
    }

    private sealed class StackMetricAccumulator
    {
        public decimal Sum { get; private set; }
        public int NumericCount { get; private set; }
        public int NonNumericCount { get; private set; }

        public void Add(JsonElement value)
        {
            var number = ToNullableDecimal(value);
            if (number.HasValue)
            {
                Sum += number.Value;
                NumericCount++;
                return;
            }

            if (HasStackCellValue(value))
                NonNumericCount++;
        }

        public object? ToCell()
        {
            if (NumericCount > 0)
                return Sum;

            return NonNumericCount > 0 ? NonNumericCount : null;
        }
    }

    private sealed class MetricAccumulator
    {
        public MetricAccumulator(MetricContract metric)
        {
            Metric = metric;
        }

        public MetricContract Metric { get; }
        public int Count { get; private set; }
        public decimal Sum { get; private set; }
        public decimal? Min { get; private set; }
        public decimal? Max { get; private set; }

        public void Add(decimal? value)
        {
            if (!value.HasValue)
                return;

            Count++;
            Sum += value.Value;
            Min = Min.HasValue ? Math.Min(Min.Value, value.Value) : value.Value;
            Max = Max.HasValue ? Math.Max(Max.Value, value.Value) : value.Value;
        }

        public DynamicFormAggregateRowDto ToDto()
        {
            return new DynamicFormAggregateRowDto
            {
                MetricKey = Metric.MetricKey,
                RowKey = Metric.RowKey,
                ColumnKey = Metric.ColumnKey,
                Index = Metric.Index,
                Label = $"{Metric.RowKey} / {Metric.ColumnKey}",
                Count = Count,
                Sum = Count > 0 ? Sum : null,
                Min = Min,
                Max = Max,
                Average = Count > 0 ? Sum / Count : null,
            };
        }
    }
}
