using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed partial class StatisticReconciliationRunService
{
    private sealed record ResolvedActualApiPagePlan(
        long ExpectedTotalRows,
        int PageSize,
        int PageCount);

    private sealed record ActualApiFilter(
        IReadOnlyDictionary<string, JsonElement> Properties);

    internal sealed record ResolvedDirectFieldExpectedScope(
        string FieldId,
        string FieldKey,
        string? StatisticLabelCode,
        string? PeriodKey,
        string? PeriodKeyFrom,
        string? PeriodKeyTo);

    private async Task<ResolvedActualApiPagePlan> ResolveActualApiPagePlanAsync(
        string surface,
        string canonicalFilterJson,
        string filterSha256,
        WorkReportStatisticRebuildJob p9,
        WorkAssignment scope,
        WorkAssignmentBasicSummarySnapshot basic,
        WorkReportStatisticDiffResult diff,
        MeResponse actor,
        CancellationToken ct)
    {
        if (!TryReadActualApiFilter(
                surface,
                canonicalFilterJson,
                filterSha256,
                p9.PeriodInstanceKey,
                out var filter))
        {
            throw HiddenSourceFailure(actor, "ACTUAL_API_FILTER_INVALID");
        }

        var total = surface switch
        {
            "DIRECT_FIELD" => await CountDirectFieldRowsAsync(
                filter!, p9, scope, ct),
            "DIRECT_TABLE" => await CountDirectTableRowsAsync(
                filter!, p9, scope, ct),
            "DIRECT_LABEL" => await CountDirectLabelRowsAsync(
                filter!, p9, scope, ct),
            "BASIC_SOURCE" => await CountBasicSourceRowsAsync(
                filter!, basic, ct),
            "P9_DIFF" => diff.TotalRowCount,
            _ => -1
        };
        if (total < 0 ||
            total > StatisticReconciliationActualCapturePlanIntegrity
                .MaxApiTotalRows)
        {
            throw HiddenSourceFailure(actor, "ACTUAL_API_PAGE_PLAN_LIMIT");
        }

        var pageSize =
            StatisticReconciliationActualCapturePlanIntegrity.ApiPageSize;
        var pageCount = Math.Max(
            1,
            checked((int)((total + pageSize - 1) / pageSize)));
        if (!StatisticReconciliationActualCapturePlanIntegrity.ValidApiPagePlan(
                total,
                pageSize,
                pageCount))
        {
            throw HiddenSourceFailure(actor, "ACTUAL_API_PAGE_PLAN_INVALID");
        }

        return new ResolvedActualApiPagePlan(total, pageSize, pageCount);
    }

    internal static bool ValidActualApiFilter(
        string surface,
        string canonicalFilterJson,
        string filterSha256,
        string? periodInstanceKey)
        => TryReadActualApiFilter(
            surface,
            canonicalFilterJson,
            filterSha256,
            periodInstanceKey,
            out _);

    internal static bool TryResolveExactDirectFieldExpectedScope(
        string? canonicalFilterJson,
        string? filterSha256,
        string? periodInstanceKey,
        string? conceptKey,
        out ResolvedDirectFieldExpectedScope? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(canonicalFilterJson) ||
            string.IsNullOrWhiteSpace(filterSha256) ||
            !RequiredApiString(conceptKey) ||
            !TryReadActualApiFilter(
                "DIRECT_FIELD",
                canonicalFilterJson,
                filterSha256,
                periodInstanceKey,
                out var filter) ||
            filter is null)
        {
            return false;
        }

        var fieldId = ApiString(filter, "fieldId");
        var fieldKey = ApiString(filter, "fieldKey");
        if (!RequiredApiString(fieldId) ||
            !RequiredApiString(fieldKey) ||
            !string.Equals(fieldKey, conceptKey, StringComparison.Ordinal) ||
            ApiString(filter, "fieldType") is not null ||
            ApiString(filter, "bucketKey") is not null ||
            ApiBoolean(filter, "showInTree").HasValue ||
            ApiBoolean(filter, "showInDetail").HasValue ||
            ApiInt32(filter, "reportStatus").HasValue)
        {
            return false;
        }

        result = new ResolvedDirectFieldExpectedScope(
            fieldId!,
            fieldKey!,
            ApiString(filter, "statisticLabelCode")?.ToLowerInvariant(),
            ApiString(filter, "periodKey"),
            ApiString(filter, "periodKeyFrom"),
            ApiString(filter, "periodKeyTo"));
        return true;
    }

    private static bool TryReadActualApiFilter(
        string surface,
        string canonicalFilterJson,
        string filterSha256,
        string? periodInstanceKey,
        out ActualApiFilter? result)
    {
        result = null;
        try
        {
            using var document = JsonDocument.Parse(canonicalFilterJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !string.Equals(
                    StatisticReconciliationCanonicalJson.Canonicalize(root),
                    canonicalFilterJson,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    StatisticReconciliationCanonicalJson.HashText(
                        canonicalFilterJson),
                    filterSha256,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var properties = root.EnumerateObject().ToArray();
            if (properties.Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() != properties.Length)
            {
                return false;
            }

            var allowed = surface switch
            {
                "DIRECT_FIELD" => DirectFieldFilterProperties,
                "DIRECT_TABLE" => DirectTableFilterProperties,
                "DIRECT_LABEL" => DirectLabelFilterProperties,
                "BASIC_SOURCE" => BasicSourceFilterProperties,
                "P9_DIFF" => EmptyFilterProperties,
                _ => null
            };
            if (allowed is null ||
                properties.Any(property => !allowed.Contains(property.Name)))
            {
                return false;
            }

            var values = properties.ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.Ordinal);
            if (surface.StartsWith("DIRECT_", StringComparison.Ordinal))
            {
                if (!values.TryGetValue("periodInstanceKey", out var period) ||
                    period.ValueKind != JsonValueKind.String ||
                    !RequiredApiString(period.GetString()) ||
                    !string.Equals(
                        period.GetString(),
                        periodInstanceKey,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else if (surface == "P9_DIFF" && values.Count != 0)
            {
                return false;
            }

            foreach (var (name, value) in values)
            {
                if (name == "periodInstanceKey")
                    continue;
                if (BooleanFilterProperties.Contains(name))
                {
                    if (value.ValueKind is not (
                            JsonValueKind.True or JsonValueKind.False or
                            JsonValueKind.Null))
                    {
                        return false;
                    }
                    continue;
                }
                if (IntegerFilterProperties.Contains(name))
                {
                    if (value.ValueKind != JsonValueKind.Null &&
                        (value.ValueKind != JsonValueKind.Number ||
                         !value.TryGetInt32(out _)))
                    {
                        return false;
                    }
                    continue;
                }
                if (value.ValueKind != JsonValueKind.Null &&
                    (value.ValueKind != JsonValueKind.String ||
                     !RequiredApiString(value.GetString())))
                {
                    return false;
                }
            }

            result = new ActualApiFilter(values);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<long> CountDirectFieldRowsAsync(
        ActualApiFilter filter,
        WorkReportStatisticRebuildJob p9,
        WorkAssignment scope,
        CancellationToken ct)
    {
        var fb = Builders<WorkReportFieldStatAggregate>.Filter;
        var query = fb.Eq(row => row.WorkId, scope.WorkId) &
                    fb.Eq(row => row.ScopeType, "ASSIGNMENT") &
                    fb.Eq(row => row.ScopeId, scope.Id) &
                    fb.Eq(row => row.DynamicFormTemplateId,
                        p9.DynamicFormTemplateId) &
                    fb.Eq(row => row.PeriodInstanceKey,
                        ApiString(filter, "periodInstanceKey")) &
                    fb.Eq(row => row.IsDeleted, false) &
                    fb.Eq("directProjection.generationId", p9.GenerationId) &
                    fb.Or(
                        fb.Eq(row => row.ShowInTree, true),
                        fb.Eq(row => row.ShowInDetail, true));
        AddString(ref query, fb, row => row.FieldId,
            ApiString(filter, "fieldId"));
        AddString(ref query, fb, row => row.FieldKey,
            ApiString(filter, "fieldKey"));
        var statisticLabelCode =
            ApiString(filter, "statisticLabelCode")?.ToLowerInvariant();
        if (statisticLabelCode is not null)
            query &= fb.AnyEq(row => row.StatisticLabelCodes, statisticLabelCode);
        AddString(ref query, fb, row => row.FieldType,
            ApiString(filter, "fieldType"));
        AddString(ref query, fb, row => row.BucketKey,
            ApiString(filter, "bucketKey"));
        AddString(ref query, fb, row => row.PeriodKey,
            ApiString(filter, "periodKey"));
        var from = ApiString(filter, "periodKeyFrom");
        if (from is not null)
            query &= fb.Gte(row => row.PeriodKey, from);
        var to = ApiString(filter, "periodKeyTo");
        if (to is not null)
            query &= fb.Lte(row => row.PeriodKey, to);
        var showTree = ApiBoolean(filter, "showInTree");
        if (showTree.HasValue)
            query &= fb.Eq(row => row.ShowInTree, showTree.Value);
        var showDetail = ApiBoolean(filter, "showInDetail");
        if (showDetail.HasValue)
            query &= fb.Eq(row => row.ShowInDetail, showDetail.Value);
        var status = ApiInt32(filter, "reportStatus");
        if (status.HasValue)
            query &= fb.Eq(row => row.ReportStatus, status.Value);
        return await _ctx.WorkReportFieldStatAggregates
            .CountDocumentsAsync(query, cancellationToken: ct);
    }

    private async Task<long> CountDirectTableRowsAsync(
        ActualApiFilter filter,
        WorkReportStatisticRebuildJob p9,
        WorkAssignment scope,
        CancellationToken ct)
    {
        var fb = Builders<WorkReportTableStatAggregate>.Filter;
        var query = fb.Eq(row => row.WorkId, scope.WorkId) &
                    fb.Eq(row => row.ScopeType, "ASSIGNMENT") &
                    fb.Eq(row => row.ScopeId, scope.Id) &
                    fb.Eq(row => row.DynamicFormTemplateId,
                        p9.DynamicFormTemplateId) &
                    fb.Eq(row => row.PeriodInstanceKey,
                        ApiString(filter, "periodInstanceKey")) &
                    fb.Eq(row => row.IsDeleted, false) &
                    fb.Eq("directProjection.generationId", p9.GenerationId);
        AddString(ref query, fb, row => row.DynamicExcelTemplateId,
            ApiString(filter, "dynamicExcelTemplateId"));
        AddString(ref query, fb, row => row.BlockId,
            ApiString(filter, "blockId"));
        AddString(ref query, fb, row => row.TableMode,
            ApiString(filter, "tableMode")?.ToUpperInvariant());
        AddString(ref query, fb, row => row.MetricKey,
            ApiString(filter, "metricKey"));
        AddString(ref query, fb, row => row.MetricLabelCode,
            ApiString(filter, "metricLabelCode")?.ToLowerInvariant());
        AddString(ref query, fb, row => row.DataType,
            ApiString(filter, "dataType")?.ToUpperInvariant());
        AddString(ref query, fb, row => row.BucketKey,
            ApiString(filter, "bucketKey"));
        AddString(ref query, fb, row => row.PeriodKey,
            ApiString(filter, "periodKey"));
        var status = ApiInt32(filter, "reportStatus");
        if (status.HasValue)
            query &= fb.Eq(row => row.ReportStatus, status.Value);
        return await _ctx.WorkReportTableStatAggregates
            .CountDocumentsAsync(query, cancellationToken: ct);
    }

    private async Task<long> CountDirectLabelRowsAsync(
        ActualApiFilter filter,
        WorkReportStatisticRebuildJob p9,
        WorkAssignment scope,
        CancellationToken ct)
    {
        var fb = Builders<WorkReportLabelStatAggregate>.Filter;
        var query = fb.Eq(row => row.WorkId, scope.WorkId) &
                    fb.Eq(row => row.ScopeType, "ASSIGNMENT") &
                    fb.Eq(row => row.ScopeId, scope.Id) &
                    fb.Eq(row => row.DynamicFormTemplateId,
                        p9.DynamicFormTemplateId) &
                    fb.Eq(row => row.PeriodInstanceKey,
                        ApiString(filter, "periodInstanceKey")) &
                    fb.Eq(row => row.IsDeleted, false) &
                    fb.Eq("directProjection.generationId", p9.GenerationId);
        AddString(ref query, fb, row => row.DynamicExcelTemplateId,
            ApiString(filter, "dynamicExcelTemplateId"));
        AddString(ref query, fb, row => row.LabelCode,
            ApiString(filter, "labelCode")?.ToLowerInvariant());
        AddString(ref query, fb, row => row.PeriodKey,
            ApiString(filter, "periodKey"));
        var status = ApiInt32(filter, "reportStatus");
        if (status.HasValue)
            query &= fb.Eq(row => row.ReportStatus, status.Value);
        return await _ctx.WorkReportLabelStatAggregates
            .CountDocumentsAsync(query, cancellationToken: ct);
    }

    private async Task<long> CountBasicSourceRowsAsync(
        ActualApiFilter filter,
        WorkAssignmentBasicSummarySnapshot basic,
        CancellationToken ct)
    {
        if (!CanonicalObjectIdList(basic.SourceAssignmentIds) ||
            !CanonicalObjectIdList(basic.SourceReportIds))
        {
            return -1;
        }

        var assignmentsTask = _ctx.WorkAssignments
            .Find(row => basic.SourceAssignmentIds.Contains(row.Id) &&
                         !row.IsDeleted)
            .ToListAsync(ct);
        var reportsTask = _ctx.WorkAssignmentReports
            .Find(row => basic.SourceReportIds.Contains(row.Id) &&
                         !row.IsDeleted)
            .ToListAsync(ct);
        await Task.WhenAll(assignmentsTask, reportsTask);
        var assignments = await assignmentsTask;
        var reports = await reportsTask;
        if (!ExactIds(assignments.Select(row => row.Id),
                basic.SourceAssignmentIds) ||
            !ExactIds(reports.Select(row => row.Id), basic.SourceReportIds))
        {
            return -1;
        }

        var assignmentById = assignments.ToDictionary(
            row => row.Id,
            StringComparer.Ordinal);
        var q = ApiString(filter, "q");
        var period = ApiString(filter, "periodKey");
        var unitId = ApiString(filter, "unitId");
        var assigneeUserId = ApiString(filter, "assigneeUserId");
        long total = 0;
        foreach (var report in reports)
        {
            assignmentById.TryGetValue(report.WorkAssignmentId,
                out var assignment);
            var assignee = assignment?.Assignees.FirstOrDefault(value =>
                string.Equals(
                    value.UserId,
                    report.AssigneeUserId,
                    StringComparison.Ordinal));
            if (unitId is not null &&
                !string.Equals(assignee?.UnitId, unitId,
                    StringComparison.Ordinal))
            {
                continue;
            }
            if (assigneeUserId is not null &&
                !string.Equals(report.AssigneeUserId, assigneeUserId,
                    StringComparison.Ordinal))
            {
                continue;
            }
            if (period is not null &&
                !ContainsOrdinalIgnoreCase(report.PeriodKey, period) &&
                !ContainsOrdinalIgnoreCase(report.PeriodInstanceKey, period))
            {
                continue;
            }
            if (q is not null &&
                !ContainsOrdinalIgnoreCase(assignee?.UnitSymbol, q) &&
                !ContainsOrdinalIgnoreCase(assignee?.UnitShortName, q) &&
                !ContainsOrdinalIgnoreCase(assignee?.UnitName, q) &&
                !ContainsOrdinalIgnoreCase(assignee?.Username, q) &&
                !ContainsOrdinalIgnoreCase(assignee?.FullName, q) &&
                !ContainsOrdinalIgnoreCase(report.PeriodKey, q) &&
                !ContainsOrdinalIgnoreCase(report.PeriodInstanceKey, q) &&
                !ContainsOrdinalIgnoreCase(report.Id, q) &&
                !ContainsOrdinalIgnoreCase(report.WorkAssignmentId, q))
            {
                continue;
            }
            total++;
        }
        return total;
    }

    private static void AddString<TDocument>(
        ref FilterDefinition<TDocument> query,
        FilterDefinitionBuilder<TDocument> filters,
        System.Linq.Expressions.Expression<Func<TDocument, string?>> field,
        string? value)
    {
        if (value is null)
            return;
        query &= filters.Eq(field, value);
    }

    private static string? ApiString(ActualApiFilter filter, string name)
        => filter.Properties.TryGetValue(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ApiBoolean(ActualApiFilter filter, string name)
        => filter.Properties.TryGetValue(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    private static int? ApiInt32(ActualApiFilter filter, string name)
        => filter.Properties.TryGetValue(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static bool RequiredApiString(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Length <= 1024 &&
           string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
           !value.Any(char.IsControl);

    private static bool CanonicalObjectIdList(IReadOnlyList<string>? values)
        => values is not null &&
           values.Count <= 10_000 &&
           values.All(CanonicalObjectId) &&
           values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static bool ExactIds(
        IEnumerable<string> actual,
        IEnumerable<string> expected)
        => actual.OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(
                expected.OrderBy(value => value, StringComparer.Ordinal),
                StringComparer.Ordinal);

    private static bool ContainsOrdinalIgnoreCase(string? value, string query)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> DirectFieldFilterProperties =
        new HashSet<string>(
        [
            "periodInstanceKey", "fieldId", "fieldKey", "statisticLabelCode",
            "fieldType", "bucketKey", "showInTree", "showInDetail",
            "periodKey", "periodKeyFrom", "periodKeyTo", "reportStatus"
        ], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> DirectTableFilterProperties =
        new HashSet<string>(
        [
            "periodInstanceKey", "dynamicExcelTemplateId", "blockId",
            "tableMode", "metricKey", "metricLabelCode", "dataType",
            "bucketKey", "periodKey", "reportStatus"
        ], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> DirectLabelFilterProperties =
        new HashSet<string>(
        [
            "periodInstanceKey", "dynamicExcelTemplateId", "labelCode",
            "periodKey", "reportStatus"
        ], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> BasicSourceFilterProperties =
        new HashSet<string>(
        [
            "q", "periodKey", "unitId", "assigneeUserId"
        ], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> EmptyFilterProperties =
        new HashSet<string>(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> BooleanFilterProperties =
        new HashSet<string>(
        [
            "showInTree", "showInDetail"
        ], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> IntegerFilterProperties =
        new HashSet<string>(
        [
            "reportStatus"
        ], StringComparer.Ordinal);
}
