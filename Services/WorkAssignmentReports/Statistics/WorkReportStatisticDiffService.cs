using System.Globalization;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public sealed partial class WorkReportStatisticDiffService : IWorkReportStatisticDiffService
{
    private const string StatisticKind = "DIFF";
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;
    private const int MinProjectionScanLimit = 1000;
    private const int MaxProjectionScanLimit = 20000;

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly IStatConfigTransactionRunner _statConfigTransactions;
    private readonly IStatRunCandidateActivation _candidateActivation;

    public WorkReportStatisticDiffService(
        MongoDbContext ctx,
        MeAccessor me,
        IStatConfigTransactionRunner statConfigTransactions,
        IStatRunCandidateActivation candidateActivation)
    {
        _ctx = ctx;
        _me = me;
        _statConfigTransactions = statConfigTransactions;
        _candidateActivation = candidateActivation;
    }

    public async Task<List<WorkReportStatisticDiffConfigDto>> ListConfigsAsync(
        string? workId,
        string? assignmentId,
        string? dynamicFormTemplateId,
        CancellationToken ct = default)
    {
        _ = _me.RequireMe();
        throw P806LegacyBlocked(
            "DIFF_LEGACY_CONFIG_ROUTE_BLOCKED");
    }

    public async Task<WorkReportStatisticDiffConfigDto> SaveConfigAsync(
        WorkReportStatisticDiffSaveRequest req,
        string? actorUserId,
        CancellationToken ct = default)
    {
        _ = _me.RequireMe();
        throw P806LegacyBlocked(
            "DIFF_LEGACY_MUTATION_BLOCKED_USE_CAS_CONFIG_ROUTE");
    }

    public async Task DeleteConfigAsync(
        string configId,
        string? actorUserId,
        CancellationToken ct = default)
    {
        _ = _me.RequireMe();
        throw P806LegacyBlocked(
            "DIFF_LEGACY_DELETE_BLOCKED_USE_CAS_CONFIG_ROUTE");
    }

    public async Task<WorkReportStatisticDiffRunResponse> RunAsync(
        WorkReportStatisticDiffRunRequest req,
        CancellationToken ct = default)
    {
        StatConfigIsolationGuard.RejectResultMaterializer(
            "WORK_REPORT_STATISTIC_DIFF_RUN");
        throw new InvalidOperationException("Unreachable P9 barrier.");
    }

    private async Task<NormalizedRunRequest> NormalizeRunRequestAsync(
        WorkReportStatisticDiffRunRequest req,
        CancellationToken ct)
    {
        WorkReportStatisticDiffConfig? config = null;
        var configId = NormalizeOptionalText(req.ConfigId);
        if (configId is not null)
        {
            config = await _ctx.WorkReportStatisticDiffConfigs
                .Find(x => x.Id == configId && !x.IsDeleted && x.IsActive)
                .FirstOrDefaultAsync(ct);
            if (config is null)
                throw DiffConfigInvalid("configId", configId, "DIFF_CONFIG_NOT_FOUND");
        }

        var workId = NormalizeRequired(FirstNonBlank(req.WorkId, config?.WorkId), "workId");
        var assignmentId = NormalizeRequired(FirstNonBlank(req.AssignmentId, config?.AssignmentId), "assignmentId");
        var dynamicFormTemplateId = NormalizeOptionalText(FirstNonBlank(req.DynamicFormTemplateId, config?.DynamicFormTemplateId));
        var current = NormalizeTarget(
            req.Current ?? (config is null ? null : ToTargetDto(config.Current)),
            dynamicFormTemplateId,
            "current");
        var comparison = NormalizeTarget(
            req.Comparison ?? (config is null ? null : ToTargetDto(config.Comparison)),
            dynamicFormTemplateId,
            "comparison");
        var periodCompareMode = NormalizePeriodCompareMode(FirstNonBlank(req.PeriodCompareMode, config?.PeriodCompareMode));
        var op = NormalizeOperator(FirstNonBlank(req.Operator, config?.Operator));
        var joinKey = NormalizeJoinKey(FirstNonBlank(req.JoinKey, config?.JoinKey));
        var requireSameConcept = req.RequireSameConcept ?? config?.RequireSameConcept ?? true;
        var currentPeriodKey = NormalizeRequired(req.PeriodKey, "periodKey");
        var comparisonPeriodKey = ResolveComparisonPeriodKey(currentPeriodKey, periodCompareMode);

        EnsureRowJoinCompatible(current, comparison, joinKey);
        EnsureConceptCompatible(current.ConceptCode, comparison.ConceptCode, requireSameConcept);

        return new NormalizedRunRequest(
            workId,
            assignmentId,
            configId,
            current,
            comparison,
            currentPeriodKey,
            comparisonPeriodKey,
            periodCompareMode,
            op,
            joinKey,
            requireSameConcept,
            NormalizeSelectedUnitIds(req.SelectedUnitIds),
            NormalizeOptionalText(req.AssigneeUserId),
            req.ReportStatus,
            req.Limit);
    }

    private async Task<List<string>> LoadSourceAssignmentIdsAsync(
        WorkAssignment scope,
        WorkReportStatisticDiffTargetDto target,
        IReadOnlyCollection<string> selectedUnitIds,
        CancellationToken ct)
    {
        var sourceScope = WorkAssignmentSummarySourceScope.Normalize(
            scope,
            target.SourceScopeMode,
            target.SourceFlowInstanceId,
            target.SourceFlowStepId,
            target.SourceFlowBranchId,
            target.SourceFlowEffectiveStatus);

        var assignments = await WorkAssignmentSummarySourceScope.LoadAssignmentsAsync(
            _ctx.WorkAssignments,
            scope,
            target.DynamicFormTemplateId!,
            selectedUnitIds,
            sourceScope,
            ct);

        return assignments
            .Select(x => x.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private async Task<LoadedFacts> LoadFactsAsync(
        WorkReportStatisticDiffTargetDto target,
        IReadOnlyCollection<string> assignmentIds,
        string workId,
        string periodKey,
        string? assigneeUserId,
        int? reportStatus,
        string joinKey,
        int scanLimit,
        CancellationToken ct)
    {
        if (assignmentIds.Count == 0)
            return LoadedFacts.Empty;

        return target.SourceKind == WorkReportStatisticDiffSourceKinds.Table
            ? await LoadTableFactsAsync(target, assignmentIds, workId, periodKey, assigneeUserId, reportStatus, joinKey, scanLimit, ct)
            : await LoadFieldFactsAsync(target, assignmentIds, workId, periodKey, assigneeUserId, reportStatus, joinKey, scanLimit, ct);
    }

    private async Task<LoadedFacts> LoadFieldFactsAsync(
        WorkReportStatisticDiffTargetDto target,
        IReadOnlyCollection<string> assignmentIds,
        string workId,
        string periodKey,
        string? assigneeUserId,
        int? reportStatus,
        string joinKey,
        int scanLimit,
        CancellationToken ct)
    {
        var fb = Builders<WorkReportFieldStatValue>.Filter;
        var filter = fb.Eq(x => x.WorkId, workId) &
                     fb.Eq(x => x.IsDeleted, false) &
                     fb.Eq(x => x.DirectProjection, null) &
                     fb.In(x => x.WorkAssignmentId, assignmentIds) &
                     fb.Eq(x => x.PeriodKey, periodKey);

        filter &= fb.Eq(x => x.DynamicFormTemplateId, target.DynamicFormTemplateId);

        if (!string.IsNullOrWhiteSpace(target.FieldId))
            filter &= fb.Eq(x => x.FieldId, target.FieldId);
        if (!string.IsNullOrWhiteSpace(target.FieldKey))
            filter &= fb.Eq(x => x.FieldKey, target.FieldKey);
        if (!string.IsNullOrWhiteSpace(target.StatisticLabelCode))
            filter &= fb.AnyEq(x => x.StatisticLabelCodes, target.StatisticLabelCode);
        if (!string.IsNullOrWhiteSpace(target.ConceptCode))
            filter &= fb.Eq(x => x.ConceptCode, target.ConceptCode);
        if (!string.IsNullOrWhiteSpace(target.BucketKey))
            filter &= fb.Eq(x => x.BucketKey, target.BucketKey);
        if (!string.IsNullOrWhiteSpace(assigneeUserId))
            filter &= fb.Eq(x => x.AssigneeUserId, assigneeUserId);
        if (reportStatus.HasValue)
            filter &= fb.Eq(x => x.ReportStatus, reportStatus.Value);

        var values = await _ctx.WorkReportFieldStatValues
            .Find(filter)
            .SortBy(x => x.FieldKey)
            .ThenBy(x => x.BucketKey)
            .Limit(scanLimit + 1)
            .ToListAsync(ct);

        var truncated = values.Count > scanLimit;
        if (truncated)
            values = values.Take(scanLimit).ToList();

        var groups = new Dictionary<string, DiffFactAccumulator>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var key = BuildFactKey(joinKey, null);
            if (!groups.TryGetValue(key, out var acc))
            {
                acc = new DiffFactAccumulator(
                    key,
                    rowKey: null,
                    sourceKind: WorkReportStatisticDiffSourceKinds.Field);
                groups[key] = acc;
            }

            acc.AddField(value);
        }

        return new LoadedFacts(groups.ToDictionary(x => x.Key, x => x.Value.ToFact(), StringComparer.Ordinal), values.Count, truncated);
    }

    private async Task<LoadedFacts> LoadTableFactsAsync(
        WorkReportStatisticDiffTargetDto target,
        IReadOnlyCollection<string> assignmentIds,
        string workId,
        string periodKey,
        string? assigneeUserId,
        int? reportStatus,
        string joinKey,
        int scanLimit,
        CancellationToken ct)
    {
        var fb = Builders<WorkReportTableStatValue>.Filter;
        var filter = fb.Eq(x => x.WorkId, workId) &
                     fb.Eq(x => x.IsDeleted, false) &
                     fb.Eq(x => x.DirectProjection, null) &
                     fb.In(x => x.WorkAssignmentId, assignmentIds) &
                     fb.Eq(x => x.PeriodKey, periodKey);

        filter &= fb.Eq(x => x.DynamicFormTemplateId, target.DynamicFormTemplateId);

        if (!string.IsNullOrWhiteSpace(target.BlockId))
            filter &= fb.Eq(x => x.BlockId, target.BlockId);
        if (!string.IsNullOrWhiteSpace(target.MetricKey))
            filter &= fb.Eq(x => x.MetricKey, target.MetricKey);
        if (!string.IsNullOrWhiteSpace(target.MetricLabelCode))
            filter &= fb.Eq(x => x.MetricLabelCode, target.MetricLabelCode);
        if (!string.IsNullOrWhiteSpace(target.RowKey))
            filter &= fb.Eq(x => x.RowKey, target.RowKey);
        if (!string.IsNullOrWhiteSpace(target.ColumnKey))
            filter &= fb.Eq(x => x.ColumnKey, target.ColumnKey);
        if (!string.IsNullOrWhiteSpace(target.ConceptCode))
            filter &= fb.Eq(x => x.ConceptCode, target.ConceptCode);
        if (!string.IsNullOrWhiteSpace(target.BucketKey))
            filter &= fb.Eq(x => x.BucketKey, target.BucketKey);
        if (!string.IsNullOrWhiteSpace(assigneeUserId))
            filter &= fb.Eq(x => x.AssigneeUserId, assigneeUserId);
        if (reportStatus.HasValue)
            filter &= fb.Eq(x => x.ReportStatus, reportStatus.Value);

        var values = await _ctx.WorkReportTableStatValues
            .Find(filter)
            .SortBy(x => x.RowKey)
            .ThenBy(x => x.ColumnKey)
            .Limit(scanLimit + 1)
            .ToListAsync(ct);

        var truncated = values.Count > scanLimit;
        if (truncated)
            values = values.Take(scanLimit).ToList();

        var groups = new Dictionary<string, DiffFactAccumulator>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (joinKey == WorkReportStatisticDiffJoinKeys.RowKey && string.IsNullOrWhiteSpace(value.RowKey))
                throw DiffRowJoinRequired("rowKey", "TABLE_ROW_KEY_MISSING");

            var rowKey = joinKey == WorkReportStatisticDiffJoinKeys.RowKey ? value.RowKey.Trim() : null;
            var key = BuildFactKey(joinKey, rowKey);
            if (!groups.TryGetValue(key, out var acc))
            {
                acc = new DiffFactAccumulator(
                    key,
                    rowKey,
                    WorkReportStatisticDiffSourceKinds.Table);
                groups[key] = acc;
            }

            acc.AddTable(value);
        }

        return new LoadedFacts(groups.ToDictionary(x => x.Key, x => x.Value.ToFact(), StringComparer.Ordinal), values.Count, truncated);
    }

    private static List<WorkReportStatisticDiffRowDto> BuildDiffRows(
        NormalizedRunRequest req,
        IReadOnlyDictionary<string, DiffFact> currentFacts,
        IReadOnlyDictionary<string, DiffFact> comparisonFacts,
        int limit)
    {
        var keys = currentFacts.Keys
            .Concat(comparisonFacts.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        var rows = new List<WorkReportStatisticDiffRowDto>();
        foreach (var key in keys)
        {
            currentFacts.TryGetValue(key, out var current);
            comparisonFacts.TryGetValue(key, out var comparison);

            if (current is not null && comparison is not null)
            {
                EnsureConceptCompatible(current.ConceptCode, comparison.ConceptCode, req.RequireSameConcept);
                EnsureDataCategoryCompatible(current.DataCategory, comparison.DataCategory);
            }

            var delta = current?.NumericValue is not null && comparison?.NumericValue is not null
                ? current.NumericValue.Value - comparison.NumericValue.Value
                : (decimal?)null;
            var missingSide = current is null
                ? "CURRENT"
                : comparison is null
                    ? "COMPARISON"
                    : null;
            var changed = missingSide is not null ||
                          !string.Equals(current?.ValueSignature, comparison?.ValueSignature, StringComparison.Ordinal);

            rows.Add(new WorkReportStatisticDiffRowDto
            {
                Key = key,
                CurrentPeriodKey = req.CurrentPeriodKey,
                ComparisonPeriodKey = req.ComparisonPeriodKey,
                RowKey = current?.RowKey ?? comparison?.RowKey,
                ConceptCode = current?.ConceptCode ?? comparison?.ConceptCode,
                DataCategory = current?.DataCategory ?? comparison?.DataCategory,
                Current = current is null ? null : ToValueDto(current),
                Comparison = comparison is null ? null : ToValueDto(comparison),
                Delta = delta,
                Changed = changed,
                MatchesOperator = MatchesOperator(req.Operator, changed, delta, missingSide),
                MissingSide = missingSide
            });
        }

        return rows;
    }

    private static bool MatchesOperator(
        string op,
        bool changed,
        decimal? delta,
        string? missingSide)
        => op switch
        {
            WorkReportStatisticDiffOperators.Changed => changed,
            WorkReportStatisticDiffOperators.Delta => delta.HasValue && delta.Value != 0m,
            WorkReportStatisticDiffOperators.GreaterThan => delta.HasValue && delta.Value > 0m,
            WorkReportStatisticDiffOperators.LessThan => delta.HasValue && delta.Value < 0m,
            WorkReportStatisticDiffOperators.BucketChanged => changed,
            WorkReportStatisticDiffOperators.Missing => missingSide is not null,
            _ => changed
        };

    private static WorkReportStatisticDiffTargetDto NormalizeTarget(
        WorkReportStatisticDiffTargetDto? target,
        string? fallbackDynamicFormTemplateId,
        string fieldName)
    {
        if (target is null)
            throw DiffConfigInvalid(fieldName, null, "DIFF_TARGET_REQUIRED");

        var sourceKind = NormalizeSourceKind(target.SourceKind);
        var dynamicFormTemplateId = NormalizeOptionalText(FirstNonBlank(target.DynamicFormTemplateId, fallbackDynamicFormTemplateId));
        if (dynamicFormTemplateId is null)
            throw DiffConfigInvalid($"{fieldName}.dynamicFormTemplateId", null, "DYNAMIC_FORM_TEMPLATE_REQUIRED");

        var normalized = new WorkReportStatisticDiffTargetDto
        {
            SourceKind = sourceKind,
            DynamicFormTemplateId = dynamicFormTemplateId,
            FieldId = NormalizeOptionalText(target.FieldId),
            FieldKey = NormalizeOptionalText(target.FieldKey),
            StatisticLabelCode = NormalizeOptionalText(target.StatisticLabelCode)?.ToLowerInvariant(),
            BlockId = NormalizeOptionalText(target.BlockId),
            MetricKey = NormalizeOptionalText(target.MetricKey),
            MetricLabelCode = NormalizeOptionalText(target.MetricLabelCode),
            RowKey = NormalizeOptionalText(target.RowKey),
            ColumnKey = NormalizeOptionalText(target.ColumnKey),
            ConceptCode = WorkReportStatisticConceptMap.NormalizeConceptCode(target.ConceptCode),
            BucketKey = NormalizeOptionalText(target.BucketKey),
            SourceScopeMode = NormalizeOptionalText(target.SourceScopeMode) ?? WorkAssignmentSummarySourceScope.DirectChildrenOrSelf,
            SourceFlowInstanceId = NormalizeOptionalText(target.SourceFlowInstanceId),
            SourceFlowStepId = NormalizeOptionalText(target.SourceFlowStepId),
            SourceFlowBranchId = NormalizeOptionalText(target.SourceFlowBranchId),
            SourceFlowEffectiveStatus = NormalizeOptionalText(target.SourceFlowEffectiveStatus)
        };

        var hasFieldSelector = !string.IsNullOrWhiteSpace(normalized.FieldId) ||
                               !string.IsNullOrWhiteSpace(normalized.FieldKey) ||
                               !string.IsNullOrWhiteSpace(normalized.StatisticLabelCode) ||
                               !string.IsNullOrWhiteSpace(normalized.ConceptCode);
        var hasTableSelector = !string.IsNullOrWhiteSpace(normalized.BlockId) ||
                               !string.IsNullOrWhiteSpace(normalized.MetricKey) ||
                               !string.IsNullOrWhiteSpace(normalized.MetricLabelCode) ||
                               !string.IsNullOrWhiteSpace(normalized.RowKey) ||
                               !string.IsNullOrWhiteSpace(normalized.ColumnKey) ||
                               !string.IsNullOrWhiteSpace(normalized.ConceptCode);

        if (sourceKind == WorkReportStatisticDiffSourceKinds.Field && !hasFieldSelector)
            throw DiffConfigInvalid(fieldName, sourceKind, "FIELD_SELECTOR_REQUIRED");
        if (sourceKind == WorkReportStatisticDiffSourceKinds.Table && !hasTableSelector)
            throw DiffConfigInvalid(fieldName, sourceKind, "TABLE_SELECTOR_REQUIRED");

        return normalized;
    }

    private static string ResolveComparisonPeriodKey(string currentPeriodKey, string periodCompareMode)
    {
        if (periodCompareMode == WorkReportStatisticDiffPeriodCompareModes.SamePeriod)
            return currentPeriodKey;

        var key = currentPeriodKey.Trim();
        if (DateTime.TryParseExact(key, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dayCompact))
            return dayCompact.AddDays(-1).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        if (DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dayDashed))
            return dayDashed.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (DateTime.TryParseExact(key, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monthCompact))
            return monthCompact.AddMonths(-1).ToString("yyyyMM", CultureInfo.InvariantCulture);

        if (DateTime.TryParseExact(key, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monthDashed))
            return monthDashed.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);

        if (DateTime.TryParseExact(key, "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var year))
            return year.AddYears(-1).ToString("yyyy", CultureInfo.InvariantCulture);

        throw DiffConfigInvalid("periodKey", currentPeriodKey, "PREVIOUS_PERIOD_KEY_UNSUPPORTED");
    }

    private static void EnsureRowJoinCompatible(
        WorkReportStatisticDiffTargetDto current,
        WorkReportStatisticDiffTargetDto comparison,
        string joinKey)
    {
        if (joinKey != WorkReportStatisticDiffJoinKeys.RowKey)
            return;

        if (current.SourceKind != WorkReportStatisticDiffSourceKinds.Table ||
            comparison.SourceKind != WorkReportStatisticDiffSourceKinds.Table)
        {
            throw DiffRowJoinRequired("joinKey", "ROW_KEY_JOIN_REQUIRES_TABLE_SOURCES");
        }
    }

    private static void EnsureConceptCompatible(
        string? currentConcept,
        string? comparisonConcept,
        bool requireSameConcept)
    {
        if (!requireSameConcept)
            return;

        currentConcept = WorkReportStatisticConceptMap.NormalizeConceptCode(currentConcept);
        comparisonConcept = WorkReportStatisticConceptMap.NormalizeConceptCode(comparisonConcept);
        if (string.IsNullOrWhiteSpace(currentConcept) ||
            string.IsNullOrWhiteSpace(comparisonConcept) ||
            string.Equals(currentConcept, comparisonConcept, StringComparison.Ordinal))
        {
            return;
        }

        throw DiffIncompatible("conceptCode", currentConcept, comparisonConcept, "CONCEPT_MISMATCH");
    }

    private static void EnsureDataCategoryCompatible(string? currentCategory, string? comparisonCategory)
    {
        currentCategory = NormalizeOptionalText(currentCategory);
        comparisonCategory = NormalizeOptionalText(comparisonCategory);
        if (string.IsNullOrWhiteSpace(currentCategory) ||
            string.IsNullOrWhiteSpace(comparisonCategory) ||
            string.Equals(currentCategory, comparisonCategory, StringComparison.Ordinal))
        {
            return;
        }

        throw DiffIncompatible("dataCategory", currentCategory, comparisonCategory, "DATA_CATEGORY_MISMATCH");
    }

    private static string NormalizeSourceKind(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? WorkReportStatisticDiffSourceKinds.Field
            : value.Trim().ToUpperInvariant();

        return normalized switch
        {
            WorkReportStatisticDiffSourceKinds.Field => WorkReportStatisticDiffSourceKinds.Field,
            WorkReportStatisticDiffSourceKinds.Table => WorkReportStatisticDiffSourceKinds.Table,
            _ => throw DiffConfigInvalid("sourceKind", normalized, "SOURCE_KIND_UNSUPPORTED")
        };
    }

    private static string NormalizePeriodCompareMode(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? WorkReportStatisticDiffPeriodCompareModes.SamePeriod
            : value.Trim().ToUpperInvariant();

        return normalized switch
        {
            WorkReportStatisticDiffPeriodCompareModes.SamePeriod => WorkReportStatisticDiffPeriodCompareModes.SamePeriod,
            WorkReportStatisticDiffPeriodCompareModes.PreviousPeriod => WorkReportStatisticDiffPeriodCompareModes.PreviousPeriod,
            _ => throw DiffConfigInvalid("periodCompareMode", normalized, "PERIOD_COMPARE_MODE_UNSUPPORTED")
        };
    }

    private static string NormalizeOperator(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? WorkReportStatisticDiffOperators.Changed
            : value.Trim().ToUpperInvariant();

        return normalized switch
        {
            WorkReportStatisticDiffOperators.Changed => WorkReportStatisticDiffOperators.Changed,
            WorkReportStatisticDiffOperators.Delta => WorkReportStatisticDiffOperators.Delta,
            WorkReportStatisticDiffOperators.GreaterThan => WorkReportStatisticDiffOperators.GreaterThan,
            WorkReportStatisticDiffOperators.LessThan => WorkReportStatisticDiffOperators.LessThan,
            WorkReportStatisticDiffOperators.BucketChanged => WorkReportStatisticDiffOperators.BucketChanged,
            WorkReportStatisticDiffOperators.Missing => WorkReportStatisticDiffOperators.Missing,
            _ => throw DiffConfigInvalid("operator", normalized, "DIFF_OPERATOR_UNSUPPORTED")
        };
    }

    private static string NormalizeJoinKey(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? WorkReportStatisticDiffJoinKeys.Period
            : value.Trim().ToUpperInvariant();

        return normalized switch
        {
            WorkReportStatisticDiffJoinKeys.Period => WorkReportStatisticDiffJoinKeys.Period,
            WorkReportStatisticDiffJoinKeys.RowKey => WorkReportStatisticDiffJoinKeys.RowKey,
            _ => throw DiffConfigInvalid("joinKey", normalized, "JOIN_KEY_UNSUPPORTED")
        };
    }

    private static int NormalizeLimit(int? value)
        => Math.Clamp(value.GetValueOrDefault(DefaultLimit), 1, MaxLimit);

    private static int NormalizeProjectionScanLimit(int limit)
        => Math.Clamp(limit * 100, MinProjectionScanLimit, MaxProjectionScanLimit);

    private static List<string> NormalizeSelectedUnitIds(IEnumerable<string>? values)
        => (values ?? Array.Empty<string>())
            .Select(NormalizeOptionalText)
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string BuildFactKey(string joinKey, string? rowKey)
        => joinKey == WorkReportStatisticDiffJoinKeys.RowKey
            ? $"ROW:{rowKey}"
            : "PERIOD";

    private static string NormalizeRequired(string? value, string field)
    {
        var normalized = NormalizeOptionalText(value);
        if (normalized is null)
        {
            if (field == "workId")
                throw ReportStatisticExceptions.WorkIdRequired(StatisticKind, value);

            throw DiffConfigInvalid(field, value, "FIELD_REQUIRED");
        }

        return normalized;
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeConfigJson(string? configJson, WorkReportStatisticDiffConfig entity)
    {
        if (!string.IsNullOrWhiteSpace(configJson))
            return configJson.Trim();

        return JsonSerializer.Serialize(new
        {
            kind = "WORK_REPORT_STATISTIC_DIFF_CONFIG_V1",
            current = entity.Current,
            comparison = entity.Comparison,
            entity.PeriodCompareMode,
            entity.Operator,
            entity.JoinKey,
            entity.RequireSameConcept
        });
    }

    private static WorkReportStatisticDiffConfigDto ToDto(WorkReportStatisticDiffConfig x)
        => new()
        {
            Id = x.Id,
            WorkId = x.WorkId,
            AssignmentId = x.AssignmentId,
            DynamicFormTemplateId = x.DynamicFormTemplateId,
            Name = x.Name,
            Current = ToTargetDto(x.Current),
            Comparison = ToTargetDto(x.Comparison),
            PeriodCompareMode = x.PeriodCompareMode,
            Operator = x.Operator,
            JoinKey = x.JoinKey,
            RequireSameConcept = x.RequireSameConcept,
            ConfigJson = x.ConfigJson,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc
        };

    private static WorkReportStatisticDiffTargetDto ToTargetDto(WorkReportStatisticDiffSourceConfig x)
        => new()
        {
            SourceKind = x.SourceKind,
            DynamicFormTemplateId = x.DynamicFormTemplateId,
            FieldId = x.FieldId,
            FieldKey = x.FieldKey,
            StatisticLabelCode = x.StatisticLabelCode,
            BlockId = x.BlockId,
            MetricKey = x.MetricKey,
            MetricLabelCode = x.MetricLabelCode,
            RowKey = x.RowKey,
            ColumnKey = x.ColumnKey,
            ConceptCode = x.ConceptCode,
            BucketKey = x.BucketKey,
            SourceScopeMode = x.SourceScopeMode,
            SourceFlowInstanceId = x.SourceFlowInstanceId,
            SourceFlowStepId = x.SourceFlowStepId,
            SourceFlowBranchId = x.SourceFlowBranchId,
            SourceFlowEffectiveStatus = x.SourceFlowEffectiveStatus
        };

    private static WorkReportStatisticDiffSourceConfig ToSourceConfig(WorkReportStatisticDiffTargetDto x)
        => new()
        {
            SourceKind = x.SourceKind,
            DynamicFormTemplateId = x.DynamicFormTemplateId,
            FieldId = x.FieldId,
            FieldKey = x.FieldKey,
            StatisticLabelCode = x.StatisticLabelCode,
            BlockId = x.BlockId,
            MetricKey = x.MetricKey,
            MetricLabelCode = x.MetricLabelCode,
            RowKey = x.RowKey,
            ColumnKey = x.ColumnKey,
            ConceptCode = x.ConceptCode,
            BucketKey = x.BucketKey,
            SourceScopeMode = x.SourceScopeMode ?? WorkAssignmentSummarySourceScope.DirectChildrenOrSelf,
            SourceFlowInstanceId = x.SourceFlowInstanceId,
            SourceFlowStepId = x.SourceFlowStepId,
            SourceFlowBranchId = x.SourceFlowBranchId,
            SourceFlowEffectiveStatus = x.SourceFlowEffectiveStatus
        };

    private static WorkReportStatisticDiffValueDto ToValueDto(DiffFact fact)
        => new()
        {
            SourceKind = fact.SourceKind,
            Label = fact.Label,
            ConceptCode = fact.ConceptCode,
            DataCategory = fact.DataCategory,
            ValueSignature = fact.ValueSignature,
            NumericValue = fact.NumericValue,
            ValueCount = fact.ValueCount,
            ReportCount = fact.ReportCount,
            BucketKey = fact.BucketKey,
            BucketLabel = fact.BucketLabel
        };

    private static AppException DiffConfigInvalid(string field, object? value, string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_REPORT_STATISTIC_DIFF_CONFIG_INVALID,
            new { field, value, reason });

    private static AppException DiffIncompatible(string field, object? current, object? comparison, string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_REPORT_STATISTIC_DIFF_INCOMPATIBLE,
            new { field, current, comparison, reason });

    private static AppException DiffRowJoinRequired(string field, string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_REPORT_STATISTIC_DIFF_ROW_JOIN_REQUIRED,
            new { field, reason });

    private sealed record NormalizedRunRequest(
        string WorkId,
        string AssignmentId,
        string? ConfigId,
        WorkReportStatisticDiffTargetDto Current,
        WorkReportStatisticDiffTargetDto Comparison,
        string CurrentPeriodKey,
        string ComparisonPeriodKey,
        string PeriodCompareMode,
        string Operator,
        string JoinKey,
        bool RequireSameConcept,
        List<string> SelectedUnitIds,
        string? AssigneeUserId,
        int? ReportStatus,
        int? Limit);

    private sealed record LoadedFacts(
        IReadOnlyDictionary<string, DiffFact> Groups,
        long LoadedProjectionRows,
        bool Truncated)
    {
        public static LoadedFacts Empty { get; } = new(
            new Dictionary<string, DiffFact>(StringComparer.Ordinal),
            0,
            false);
    }

    private sealed record DiffFact(
        string Key,
        string? RowKey,
        string SourceKind,
        string? Label,
        string? ConceptCode,
        string? DataCategory,
        string? ValueSignature,
        decimal? NumericValue,
        long ValueCount,
        long ReportCount,
        string? BucketKey,
        string? BucketLabel);

    private sealed class DiffFactAccumulator
    {
        private readonly HashSet<string> _reportIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _conceptCodes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dataCategories = new(StringComparer.Ordinal);
        private readonly HashSet<string> _bucketKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _bucketLabels = new(StringComparer.Ordinal);
        private decimal _numericSum;
        private bool _hasNumeric;
        private long _trueCount;
        private long _falseCount;

        public DiffFactAccumulator(string key, string? rowKey, string sourceKind)
        {
            Key = key;
            RowKey = rowKey;
            SourceKind = sourceKind;
        }

        private string Key { get; }
        private string? RowKey { get; }
        private string SourceKind { get; }
        private string? Label { get; set; }
        private long ValueCount { get; set; }

        public void AddField(WorkReportFieldStatValue value)
        {
            Label ??= value.FieldLabel;
            ValueCount++;
            AddReport(value.WorkAssignmentReportId);
            AddConcept(value.ConceptCode);
            var category = ToFieldDataCategory(value.ValueKind);
            AddCategory(category);
            AddBucket(value.BucketKey, value.BucketLabel);

            if (category == "NUMBER" && value.NumericValue.HasValue)
            {
                _numericSum += value.NumericValue.Value;
                _hasNumeric = true;
            }
            else if (category == "BOOLEAN" && value.BooleanValue.HasValue)
            {
                if (value.BooleanValue.Value) _trueCount++;
                else _falseCount++;
            }
        }

        public void AddTable(WorkReportTableStatValue value)
        {
            Label ??= value.MetricLabelCode ?? value.MetricKey;
            ValueCount++;
            AddReport(value.WorkAssignmentReportId);
            AddConcept(value.ConceptCode);
            var category = ToTableDataCategory(value.DataType, value.BucketKey);
            AddCategory(category);
            AddBucket(value.BucketKey, value.BucketLabel ?? value.TextValue);

            if (category == "NUMBER")
            {
                _numericSum += value.Value;
                _hasNumeric = true;
            }
            else if (category == "BOOLEAN" && value.BooleanValue.HasValue)
            {
                if (value.BooleanValue.Value) _trueCount++;
                else _falseCount++;
            }
        }

        public DiffFact ToFact()
        {
            var category = SingleOrJoined(_dataCategories);
            var bucketKey = SingleOrJoined(_bucketKeys);
            var bucketLabel = SingleOrJoined(_bucketLabels);
            var numeric = _hasNumeric ? _numericSum : (decimal?)null;
            var signature = BuildValueSignature(category, numeric, _trueCount, _falseCount, bucketKey, ValueCount);

            return new DiffFact(
                Key,
                RowKey,
                SourceKind,
                Label,
                SingleOrJoined(_conceptCodes),
                category,
                signature,
                numeric,
                ValueCount,
                _reportIds.Count,
                bucketKey,
                bucketLabel);
        }

        private void AddReport(string? reportId)
        {
            if (!string.IsNullOrWhiteSpace(reportId))
                _reportIds.Add(reportId.Trim());
        }

        private void AddConcept(string? conceptCode)
        {
            var normalized = WorkReportStatisticConceptMap.NormalizeConceptCode(conceptCode);
            if (normalized is not null)
                _conceptCodes.Add(normalized);
        }

        private void AddCategory(string? category)
        {
            if (!string.IsNullOrWhiteSpace(category))
                _dataCategories.Add(category.Trim().ToUpperInvariant());
        }

        private void AddBucket(string? key, string? label)
        {
            if (!string.IsNullOrWhiteSpace(key))
                _bucketKeys.Add(key.Trim());
            if (!string.IsNullOrWhiteSpace(label))
                _bucketLabels.Add(label.Trim());
        }

        private static string? SingleOrJoined(HashSet<string> values)
        {
            if (values.Count == 0)
                return null;
            if (values.Count == 1)
                return values.First();

            return string.Join("|", values.OrderBy(x => x, StringComparer.Ordinal));
        }

        private static string? BuildValueSignature(
            string? category,
            decimal? numeric,
            long trueCount,
            long falseCount,
            string? bucketKey,
            long valueCount)
            => category switch
            {
                "NUMBER" => numeric.HasValue ? $"N:{numeric.Value.ToString(CultureInfo.InvariantCulture)}" : "N:<null>",
                "BOOLEAN" => $"BOOL:{trueCount}:{falseCount}",
                "BUCKET" => $"B:{bucketKey ?? "<none>"}:{valueCount}",
                "DATE" => $"D:{bucketKey ?? "<date>"}:{valueCount}",
                _ => $"V:{bucketKey ?? "<value>"}:{valueCount}"
            };

        private static string ToFieldDataCategory(string? valueKind)
        {
            var normalized = string.IsNullOrWhiteSpace(valueKind) ? string.Empty : valueKind.Trim().ToUpperInvariant();
            return normalized switch
            {
                "NUMBER" => "NUMBER",
                "BOOLEAN" => "BOOLEAN",
                "DATE" => "DATE",
                "OPTION" => "BUCKET",
                "TEXT_BUCKET" => "BUCKET",
                _ => "PRESENT"
            };
        }

        private static string ToTableDataCategory(string? dataType, string? bucketKey)
        {
            if (!string.IsNullOrWhiteSpace(bucketKey))
                return "BUCKET";

            var normalized = string.IsNullOrWhiteSpace(dataType) ? string.Empty : dataType.Trim().ToUpperInvariant();
            return normalized switch
            {
                "NUMBER" or "CURRENCY" or "PERCENT" => "NUMBER",
                "BOOLEAN" => "BOOLEAN",
                "DATE" => "DATE",
                "TEXT" or "STRING" or "SHORT_TEXT" or "LONG_TEXT" => "TEXT",
                _ => "PRESENT"
            };
        }
    }
}
