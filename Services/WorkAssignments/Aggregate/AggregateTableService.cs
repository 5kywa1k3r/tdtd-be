using System.Text.Json;
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

namespace tdtd_be.Services.WorkAssignments.Aggregate;

public sealed class AggregateTableService : IAggregateTableService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly IUnitSelectionService _unitSelection;

    public AggregateTableService(MongoDbContext ctx, MeAccessor me, IUnitSelectionService unitSelection)
    {
        _ctx = ctx;
        _me = me;
        _unitSelection = unitSelection;
    }

    public async Task<AggregateTableResponse> GetTableAsync(
        AggregateTableRequest req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var normalized = NormalizeRequest(req);

        ValidateAggregateRequest(normalized);

        await LoadAggregateParentAsync(normalized.ParentAssignmentId!, me.Id, ct);
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
        {
            if (IsRecordTableTemplate(dynamicExcelTemplate, null))
                return BuildEmptyRecordTableResponse(normalized, assignments[0], sources, dynamicExcelTemplate);

            return BuildEmptyAggregateResponse(normalized, assignments[0], sources);
        }

        if (IsRecordTableTemplate(dynamicExcelTemplate, effectiveReports[0]))
            return BuildRecordTableResult(normalized, assignments, effectiveReports, sources, dynamicExcelTemplate);

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

        var scopeRoot = await LoadAggregateParentAsync(normalized.ScopeAssignmentId, me.Id, ct);

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

        if (metrics.Count == 0)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_METRIC_KEY_NOT_FOUND,
                new
                {
                    normalized.DynamicFormTemplateId,
                    normalized.BlockId,
                    normalized.TableMode,
                    requestedMetricKeys
                });

        normalized.SelectedUnitIds = await ResolveSelectedUnitIdsAsync(normalized.SelectedUnitIds, ct);

        var assignments = await LoadDynamicFormAggregateAssignmentsAsync(
            scopeRoot,
            normalized.ScopeMode ?? "DIRECT_CHILDREN",
            normalized.DynamicFormTemplateId,
            normalized.SelectedUnitIds,
            ct);

        var warnings = new List<string>();
        if (assignments.Count == 0)
        {
            return BuildEmptyDynamicFormResponse(normalized, template, contract, metrics, warnings);
        }

        var reports = await LoadDynamicFormAggregateReportsAsync(assignments, normalized, ct);
        var sources = BuildAggregateSources(assignments, reports);
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
            Sources = sources,
            Warnings = warnings
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList(),
        };
    }

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
            ScopeMode = string.IsNullOrWhiteSpace(req.ScopeMode)
                ? "DIRECT_CHILDREN"
                : req.ScopeMode.Trim().ToUpperInvariant(),
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
        };

        if (!string.IsNullOrWhiteSpace(normalized.PeriodKeyFrom) &&
            !string.IsNullOrWhiteSpace(normalized.PeriodKeyTo) &&
            string.CompareOrdinal(normalized.PeriodKeyFrom, normalized.PeriodKeyTo) > 0)
        {
            (normalized.PeriodKeyFrom, normalized.PeriodKeyTo) = (normalized.PeriodKeyTo, normalized.PeriodKeyFrom);
        }

        return normalized;
    }

    private static void ValidateDynamicFormRequest(DynamicFormAggregateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ScopeAssignmentId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_SCOPE_ID_REQUIRED);

        if (req.ScopeMode is not ("DIRECT_CHILDREN" or "SUBTREE"))
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
        CancellationToken ct)
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

        return await _ctx.WorkAssignmentReports
            .Find(filter)
            .SortBy(x => x.PeriodKey)
            .ThenBy(x => x.PeriodInstanceKey)
            .ThenBy(x => x.ReportDate)
            .ThenBy(x => x.AssigneeUserId)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(ct);
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

        return await _ctx.WorkAssignmentReports
            .Find(filter)
            .SortBy(x => x.PeriodKey)
            .ThenBy(x => x.PeriodInstanceKey)
            .ThenBy(x => x.ReportDate)
            .ThenBy(x => x.AssigneeUserId)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(ct);
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
                var window = fb.And(
                    fb.Lte(x => x.PeriodStart, date.Date),
                    fb.Gte(x => x.PeriodEnd, date.Date));
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

                var window = fb.And(
                    fb.Lte(x => x.PeriodStart, to.Date),
                    fb.Gte(x => x.PeriodEnd, from.Date));
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
                var window = fb.Lte(x => x.PeriodStart, to.Date);
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
                var window = fb.And(
                    fb.Lte(x => x.PeriodStartDate, date.Date),
                    fb.Gte(x => x.PeriodEndDate, date.Date));
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

                var window = fb.And(
                    fb.Lte(x => x.PeriodStartDate, to.Date),
                    fb.Gte(x => x.PeriodEndDate, from.Date));
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
                var window = fb.Lte(x => x.PeriodStartDate, to.Date);
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
            SourceAssignmentCount = sourceAssignmentCount,
            SourceReportCount = sourceReportCount,
            MetricCount = metricCount,
        };
    }

    private static List<DynamicFormAggregateColumnDto> BuildDynamicFormColumns()
        => new()
        {
            new DynamicFormAggregateColumnDto { Key = "metricKey", Label = "Mã chỉ số", Type = "text" },
            new DynamicFormAggregateColumnDto { Key = "rowKey", Label = "Dòng", Type = "text" },
            new DynamicFormAggregateColumnDto { Key = "columnKey", Label = "Cột", Type = "text" },
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
            warnings.Add("Preview hiện hiển thị theo đơn vị hoặc công việc; các nhóm khác vẫn được giữ trong cấu hình tổng hợp.");
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

            var values = ResolveReportBlockValues(report, contract.BlockId, warnings);
            foreach (var metric in allowedMetrics)
            {
                if (metric.Index < 0 || metric.Index >= values.Count)
                    continue;

                var sourceKey = $"index:{metric.Index}";
                if (!policy.ShouldIncludeTableMetric(
                        contract.BlockId,
                        metric.MetricKey,
                        metric.RowKey,
                        metric.ColumnKey,
                        sourceKey))
                    continue;

                acc[metric.MetricKey].Add(values[metric.Index]);
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

    private static List<decimal?> ResolveReportBlockValues(
        WorkAssignmentReport report,
        string blockId,
        List<string> warnings)
    {
        var block = ExtractReportTableBlock(report.TableValuesJson, blockId);
        if (block?.Values1D is { Count: > 0 })
            return block.Values1D.Select(ToNullableDecimal).ToList();

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
            var root = JsonSerializer.Deserialize<ReportTableValuesRoot>(tableValuesJson, JsonOptions);
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
            !string.Equals(blockId, requestedBlock, StringComparison.Ordinal))
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
            var appendRowMetrics = BuildAppendRowsMetricMap(blockId, block.W);
            if (appendRowMetrics.Count == 0)
                throw DynamicFormTableContractInvalid(template.Id, blockId, tableMode, "APPEND_ROWS_WIDTH_REQUIRED");

            return new DynamicFormTableContract(blockId, tableMode, appendRowMetrics);
        }

        if (tableMode == "APPEND_COLUMNS")
        {
            var appendColumnMetrics = BuildAppendColumnsMetricMap(blockId, block.H);
            if (appendColumnMetrics.Count == 0)
                throw DynamicFormTableContractInvalid(template.Id, blockId, tableMode, "APPEND_COLUMNS_HEIGHT_REQUIRED");

            return new DynamicFormTableContract(blockId, tableMode, appendColumnMetrics);
        }

        if (tableMode == "MATRIX")
        {
            var matrixMap = NormalizeMetricMap(block.IndexMap, blockId);
            if (matrixMap.Count == 0)
                matrixMap = BuildFallbackMetricMap(blockId, block.W, block.H);

            if (matrixMap.Count == 0)
                throw DynamicFormTableContractInvalid(template.Id, blockId, tableMode, "MATRIX_METRIC_MAP_REQUIRED");

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

        var indexMap = NormalizeMetricMap(block.IndexMap, blockId);
        if (indexMap.Count == 0)
            indexMap = BuildFallbackMetricMap(blockId, block.W, block.H);

        if (indexMap.Count == 0)
            throw DynamicFormTableContractInvalid(template.Id, blockId, tableMode, "FIXED_GRID_METRIC_MAP_REQUIRED");

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
                string.Equals(
                    NormalizeBlockId(block.BlockId ?? block.Id ?? "excel_block"),
                    requested,
                    StringComparison.Ordinal));
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

    private static List<MetricContract> BuildAppendRowsMetricMap(
        string blockId,
        int? width)
    {
        var w = width.GetValueOrDefault();
        if (w <= 0)
            return new List<MetricContract>();

        var metrics = new List<MetricContract>();
        for (var c = 0; c < w; c++)
        {
            var columnKey = $"col_{c + 1}";
            metrics.Add(new MetricContract(
                c,
                "APPEND_ROWS",
                columnKey,
                $"table:{blockId}.column:{columnKey}"));
        }

        return metrics;
    }

    private static List<MetricContract> BuildAppendColumnsMetricMap(
        string blockId,
        int? height)
    {
        var h = height.GetValueOrDefault();
        if (h <= 0)
            return new List<MetricContract>();

        var metrics = new List<MetricContract>();
        for (var r = 0; r < h; r++)
        {
            var rowKey = $"row_{r + 1}";
            metrics.Add(new MetricContract(
                r,
                rowKey,
                "APPEND_COLUMNS",
                $"table:{blockId}.row:{rowKey}"));
        }

        return metrics;
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

    private static List<MetricContract> BuildFallbackMetricMap(
        string blockId,
        int? width,
        int? height)
    {
        var w = width.GetValueOrDefault();
        var h = height.GetValueOrDefault();
        if (w <= 0 || h <= 0)
            return new List<MetricContract>();

        var metrics = new List<MetricContract>();
        for (var r = 0; r < h; r++)
        {
            for (var c = 0; c < w; c++)
            {
                var index = r * w + c;
                var rowKey = $"row_{r + 1}";
                var columnKey = $"col_{c + 1}";
                metrics.Add(new MetricContract(
                    index,
                    rowKey,
                    columnKey,
                    BuildMetricKey(blockId, rowKey, columnKey)));
            }
        }

        return metrics;
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

    private static bool IsRecordTableTemplate(
        DynamicExcelTemplate? template,
        WorkAssignmentReport? report)
    {
        var kind = report?.DynamicExcelTableKind ?? template?.TableKind;
        return string.Equals(kind?.Trim(), DynamicExcelTableKind.RecordTable, StringComparison.OrdinalIgnoreCase);
    }

    private static AggregateTableResponse BuildRecordTableResult(
        AggregateTableRequest req,
        List<WorkAssignment> assignments,
        List<WorkAssignmentReport> reports,
        List<AggregateSourceRowDto> sources,
        DynamicExcelTemplate? template)
    {
        var first = reports[0];
        var specJson = string.IsNullOrWhiteSpace(first.RecordTableSpecJson)
            ? template?.RecordTableSpecJson
            : first.RecordTableSpecJson;
        var spec = DynamicExcelRecordTableRuntime.ParseSpec(specJson);
        var assignmentMap = assignments.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
        var rows = new List<AggregateRecordTableRowDto>();

        foreach (var report in reports)
        {
            assignmentMap.TryGetValue(report.WorkAssignmentId, out var assignment);
            var assignee = assignment?.Assignees?.FirstOrDefault(x => x.UserId == report.AssigneeUserId)
                           ?? assignment?.Assignees?.FirstOrDefault();

            var recordRows = DynamicExcelRecordTableRuntime.ExtractRows(
                report.TableValuesJson,
                spec,
                report.DynamicExcelTemplateId);

            foreach (var record in recordRows)
            {
                var calculated = DynamicExcelRecordTableRuntime.BuildCalculatedValues(spec, record.Values);
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var column in spec.Columns)
                {
                    record.Values.TryGetValue(column.Key, out var value);
                    values[column.Key] = DynamicExcelRecordTableRuntime.ToJsonFriendly(value);
                }

                foreach (var output in spec.CalculatedOutputs)
                {
                    calculated.TryGetValue(output.Key, out var value);
                    values[output.Key] = DynamicExcelRecordTableRuntime.ToJsonFriendly(value);
                }

                rows.Add(new AggregateRecordTableRowDto
                {
                    ReportId = report.Id,
                    WorkAssignmentId = assignment?.Id ?? report.WorkAssignmentId,
                    UserId = report.AssigneeUserId ?? assignee?.UserId,
                    UserName = assignee?.Username,
                    FullName = assignee?.FullName,
                    UnitSymbol = assignee?.UnitSymbol,
                    UnitShortName = assignee?.UnitShortName,
                    PeriodKey = report.PeriodKey,
                    SourceRowIndex = record.RowIndex,
                    SourceRowKey = record.RowKey,
                    Values = values,
                });
            }
        }

        var includedPeriodKeys = reports
            .Select(x => x.PeriodKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        return new AggregateTableResponse
        {
            DynamicExcelId = template?.Id ?? first.DynamicExcelTemplateId,
            DynamicExcelCode = template?.Code ?? first.DynamicExcelTemplateCode,
            DynamicExcelName = template?.Name ?? first.DynamicExcelTemplateName,
            PeriodScopeMode = req.PeriodScopeMode,
            PeriodKey = req.PeriodKey,
            PeriodKeyFrom = req.PeriodKeyFrom,
            PeriodKeyTo = req.PeriodKeyTo,
            SelectedUnitIds = req.SelectedUnitIds ?? new List<string>(),
            AggregateMode = "RECORD_TABLE_CONCAT",
            PeriodCount = includedPeriodKeys.Count,
            IncludedPeriodKeys = includedPeriodKeys,
            DataRectR0 = 0,
            DataRectC0 = 0,
            DataRectR1 = 0,
            DataRectC1 = 0,
            W = 0,
            H = 0,
            MetaColumns = new List<string> { "periodKey", "user", "unit" },
            Rows = new List<AggregateTableRowDto>(),
            TableKind = DynamicExcelTableKind.RecordTable,
            RecordOrientation = spec.Orientation,
            RecordColumns = BuildRecordColumns(spec),
            RecordRows = rows,
            Warnings = rows.Count == 0
                ? new List<string> { "Không có dòng dữ liệu phát sinh trong các báo cáo đã chọn." }
                : new List<string>(),
            Sources = sources,
        };
    }

    private static AggregateTableResponse BuildEmptyRecordTableResponse(
        AggregateTableRequest req,
        WorkAssignment assignment,
        List<AggregateSourceRowDto> sources,
        DynamicExcelTemplate? template)
    {
        var columns = new List<AggregateRecordTableColumnDto>();
        var orientation = "ROWS";
        if (!string.IsNullOrWhiteSpace(template?.RecordTableSpecJson))
        {
            var spec = DynamicExcelRecordTableRuntime.ParseSpec(template.RecordTableSpecJson);
            columns = BuildRecordColumns(spec);
            orientation = spec.Orientation;
        }

        return new AggregateTableResponse
        {
            DynamicExcelId = template?.Id ?? assignment.DynamicExcelId,
            DynamicExcelCode = template?.Code ?? assignment.DynamicExcelCode,
            DynamicExcelName = template?.Name ?? assignment.DynamicExcelName,
            PeriodScopeMode = req.PeriodScopeMode,
            PeriodKey = req.PeriodKey,
            PeriodKeyFrom = req.PeriodKeyFrom,
            PeriodKeyTo = req.PeriodKeyTo,
            SelectedUnitIds = req.SelectedUnitIds ?? new List<string>(),
            AggregateMode = "RECORD_TABLE_CONCAT",
            PeriodCount = 0,
            IncludedPeriodKeys = new List<string>(),
            MetaColumns = new List<string> { "periodKey", "user", "unit" },
            Rows = new List<AggregateTableRowDto>(),
            TableKind = DynamicExcelTableKind.RecordTable,
            RecordOrientation = orientation,
            RecordColumns = columns,
            RecordRows = new List<AggregateRecordTableRowDto>(),
            Warnings = new List<string> { "Không có báo cáo đã duyệt phù hợp với bộ lọc hiện tại." },
            Sources = sources,
        };
    }

    private static List<AggregateRecordTableColumnDto> BuildRecordColumns(DynamicExcelRecordTableRuntime.TableSpec spec)
        => spec.Columns
            .Concat(spec.CalculatedOutputs)
            .Select(column => new AggregateRecordTableColumnDto
            {
                Key = column.Key,
                Label = column.Label,
                DataType = column.DataType,
                IsCalculated = column.IsCalculated,
            })
            .ToList();

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
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<decimal?>();

        try
        {
            return JsonSerializer.Deserialize<List<decimal?>>(json) ?? new List<decimal?>();
        }
        catch
        {
            try
            {
                var raw = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? new List<JsonElement>();
                return raw.Select(ToNullableDecimal).ToList();
            }
            catch
            {
                return new List<decimal?>();
            }
        }
    }

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
        public string? TableMode { get; set; }
        public int? W { get; set; }
        public int? H { get; set; }
        public List<DynamicFormIndexMapItem>? IndexMap { get; set; }
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
