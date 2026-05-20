using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public sealed class WorkReportTableStatisticsService : IWorkReportTableStatisticsService
{
    private const int ShortTextBucketMaxLength = 200;
    private static readonly Regex LabelCodeRegex = new("^[a-z0-9][a-z0-9_.-]{0,63}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly IWorkReportPayloadReader _payloadReader;

    public WorkReportTableStatisticsService(
        MongoDbContext ctx,
        MeAccessor me,
        IWorkReportPayloadReader payloadReader)
    {
        _ctx = ctx;
        _me = me;
        _payloadReader = payloadReader;
    }

    public async Task RebuildForReportAsync(
        string reportId,
        string? actorUserId,
        CancellationToken ct = default)
    {
        var aggregateKey = await RebuildValuesForReportAsync(reportId, actorUserId, ct);
        if (aggregateKey is null)
            return;

        await RebuildAggregatesForWorkPeriodAsync(
            aggregateKey.WorkId,
            aggregateKey.PeriodInstanceKey,
            aggregateKey.DynamicFormTemplateId,
            actorUserId,
            ct);
    }

    public async Task<ReportStatisticAggregateKey?> RebuildValuesForReportAsync(
        string reportId,
        string? actorUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reportId))
            return null;

        var report = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (report is null)
        {
            await _ctx.WorkReportTableStatValues
                .DeleteManyAsync(x => x.WorkAssignmentReportId == reportId, ct);
            return null;
        }

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == report.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        await _ctx.WorkReportTableStatValues.DeleteManyAsync(
            x => x.WorkAssignmentReportId == report.Id,
            ct);

        if (report.IsActive == false || report.Status != WorkAssignmentReportStatus.Approved)
        {
            return new ReportStatisticAggregateKey(
                report.WorkId,
                report.PeriodInstanceKey,
                report.DynamicFormTemplateId);
        }

        var contributionPolicy = WorkReportCumulativeContributionPolicy.FromReport(report);
        if (!contributionPolicy.IncludesReport)
        {
            return new ReportStatisticAggregateKey(
                report.WorkId,
                report.PeriodInstanceKey,
                report.DynamicFormTemplateId);
        }

        var metricTargets = await LoadMetricTargetMapAsync(report.DynamicFormTemplateId, ct);
        WorkReportPayloadConsistency.EnsureReadyForStatisticProjection(report);
        var payload = await _payloadReader.LoadReportPayloadAsync(report, ct);
        WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, payload);
        var metricValues = ExtractMetricValues(payload.TableValuesJson, metricTargets)
            .Where(value => contributionPolicy.ShouldIncludeTableMetric(
                value.BlockId,
                value.MetricKey,
                value.RowKey,
                value.ColumnKey,
                value.SourceKey))
            .ToList();
        if (metricValues.Count > 0)
        {
            var now = DateTime.UtcNow;
            var actorId = NormalizeObjectIdOrNull(actorUserId);
            var ancestorAssignmentIds = ExtractAncestorAssignmentIds(assignment, report.WorkAssignmentId);
            var sourceWindow = WorkAssignmentReportTemporalPolicy.ResolveSourceWindow(report);
            var projectionContext = WorkReportStatisticProjectionContextBuilder.From(report, assignment, period, payload);

            var rows = metricValues.Select(value => new WorkReportTableStatValue
            {
                Id = ObjectId.GenerateNewId().ToString(),
                WorkId = report.WorkId,
                WorkAssignmentId = report.WorkAssignmentId,
                AssigneeUserId = projectionContext.AssigneeUserId,
                AssigneeUnitId = projectionContext.AssigneeUnitId,
                AssignmentIsActive = projectionContext.AssignmentIsActive,
                ReportIsActive = projectionContext.ReportIsActive,
                RootAssignmentId = assignment?.RootAssignmentId,
                AncestorAssignmentIds = ancestorAssignmentIds,
                WorkReportPeriodId = report.WorkReportPeriodId,
                WorkAssignmentReportId = report.Id,
                DynamicFormTemplateId = NormalizeObjectIdOrNull(report.DynamicFormTemplateId),
                DynamicFormTemplateCode = report.DynamicFormTemplateCode,
                DynamicFormTemplateName = report.DynamicFormTemplateName,
                DynamicExcelTemplateId = NormalizeObjectIdOrNull(value.DynamicExcelTemplateId ?? report.DynamicExcelTemplateId),
                BlockId = value.BlockId,
                TableMode = value.TableMode,
                MetricKey = value.MetricKey,
                MetricLabelCode = metricTargets.LabelByMetric.TryGetValue(
                    BuildMetricLabelMapKey(value.BlockId, value.MetricKey),
                    out var metricLabelCode)
                    ? metricLabelCode
                    : null,
                RowKey = value.RowKey,
                ColumnKey = value.ColumnKey,
                SourceKey = value.SourceKey,
                DataType = value.DataType,
                BucketKey = value.BucketKey,
                BucketLabel = value.BucketLabel,
                TextValue = value.TextValue,
                BooleanValue = value.BooleanValue,
                DateValue = value.DateValue,
                PeriodKey = report.PeriodKey,
                PeriodInstanceKey = report.PeriodInstanceKey,
                PeriodKind = report.PeriodKind,
                PeriodAnchorDate = sourceWindow.PeriodAnchorDate,
                PeriodStartDate = sourceWindow.PeriodStartDate,
                PeriodEndDate = sourceWindow.PeriodEndDate,
                CompletedDate = sourceWindow.CompletedDate,
                IsHistoricalData = sourceWindow.IsHistoricalData,
                ReportStatus = (int)report.Status,
                Value = value.NumberValue ?? 0m,
                SourcePayloadRevision = projectionContext.SourcePayloadRevision,
                SourcePayloadHash = projectionContext.SourcePayloadHash,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = actorId,
                UpdatedByUserId = actorId,
                IsDeleted = false
            }).ToList();

            if (rows.Count > 0)
                await _ctx.WorkReportTableStatValues.InsertManyAsync(rows, cancellationToken: ct);
        }

        return new ReportStatisticAggregateKey(
            report.WorkId,
            report.PeriodInstanceKey,
            report.DynamicFormTemplateId);
    }

    public async Task RebuildAggregatesForWorkPeriodAsync(
        string workId,
        string? periodInstanceKey,
        string? dynamicFormTemplateId,
        string? actorUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workId))
            return;

        var valueFilter = BuildValueFilter(workId, periodInstanceKey, dynamicFormTemplateId);
        var aggregateFilter = BuildAggregateFilter(workId, periodInstanceKey, dynamicFormTemplateId);

        var values = await _ctx.WorkReportTableStatValues
            .Find(valueFilter)
            .ToListAsync(ct);

        await _ctx.WorkReportTableStatAggregates.DeleteManyAsync(aggregateFilter, ct);

        if (values.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var actorId = NormalizeObjectIdOrNull(actorUserId);
        var buckets = new Dictionary<AggregateKey, AggregateBucket>();

        foreach (var value in values)
        {
            foreach (var scope in ResolveScopes(value))
            {
                if (string.IsNullOrWhiteSpace(scope.ScopeId))
                    continue;

                var key = new AggregateKey(
                    value.WorkId,
                    scope.ScopeType,
                    scope.ScopeId,
                    value.DynamicFormTemplateId,
                    value.DynamicExcelTemplateId,
                    value.BlockId,
                    value.TableMode,
                    value.MetricKey,
                    value.MetricLabelCode,
                    value.DataType,
                    value.BucketKey,
                    value.PeriodInstanceKey,
                    value.ReportStatus);

                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new AggregateBucket
                    {
                        Row = new WorkReportTableStatAggregate
                        {
                            Id = ObjectId.GenerateNewId().ToString(),
                            WorkId = value.WorkId,
                            ScopeType = scope.ScopeType,
                            ScopeId = scope.ScopeId,
                            RootAssignmentId = value.RootAssignmentId,
                            DynamicFormTemplateId = value.DynamicFormTemplateId,
                            DynamicFormTemplateCode = value.DynamicFormTemplateCode,
                            DynamicFormTemplateName = value.DynamicFormTemplateName,
                            DynamicExcelTemplateId = value.DynamicExcelTemplateId,
                            BlockId = value.BlockId,
                            TableMode = value.TableMode,
                            MetricKey = value.MetricKey,
                            MetricLabelCode = value.MetricLabelCode,
                            RowKey = value.RowKey,
                            ColumnKey = value.ColumnKey,
                            DataType = NormalizeDataType(value.DataType),
                            BucketKey = value.BucketKey,
                            BucketLabel = value.BucketLabel,
                            PeriodKey = value.PeriodKey,
                            PeriodInstanceKey = value.PeriodInstanceKey,
                            PeriodKind = value.PeriodKind,
                            PeriodAnchorDate = value.PeriodAnchorDate,
                            PeriodStartDate = value.PeriodStartDate,
                            PeriodEndDate = value.PeriodEndDate,
                            CompletedDate = value.CompletedDate,
                            IsHistoricalData = value.IsHistoricalData,
                            ReportStatus = value.ReportStatus,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            CreatedByUserId = actorId,
                            UpdatedByUserId = actorId,
                            IsDeleted = false
                        }
                    };
                    buckets.Add(key, bucket);
                }

                bucket.Add(value);
            }
        }

        var aggregates = buckets.Values.Select(x =>
        {
            x.Row.ReportCount = x.ReportIds.Count;
            return x.Row;
        }).ToList();

        if (aggregates.Count > 0)
            await _ctx.WorkReportTableStatAggregates.InsertManyAsync(aggregates, cancellationToken: ct);
    }

    public async Task<RebuildTableStatisticResponse> RebuildForWorkPeriodAsync(
        RebuildTableStatisticRequest req,
        string? actorUserId,
        CancellationToken ct = default)
    {
        var actorId = string.IsNullOrWhiteSpace(actorUserId)
            ? _me.RequireMe().Id
            : actorUserId.Trim();

        var normalized = NormalizeRebuildRequest(req);
        await EnsureCanReadWorkAsync(normalized.WorkId, actorId, ct);

        var fb = Builders<WorkAssignmentReport>.Filter;
        var filter = fb.Eq(x => x.WorkId, normalized.WorkId)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(x => x.IsCurrent, true)
                     & fb.Ne(x => x.IsActive, false);

        if (!string.IsNullOrWhiteSpace(normalized.PeriodInstanceKey))
            filter &= fb.Eq(x => x.PeriodInstanceKey, normalized.PeriodInstanceKey);

        if (!string.IsNullOrWhiteSpace(normalized.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, normalized.DynamicFormTemplateId);

        var reports = await _ctx.WorkAssignmentReports
            .Find(filter)
            .Project(x => x.Id)
            .ToListAsync(ct);

        foreach (var reportId in reports)
            await RebuildForReportAsync(reportId, actorId, ct);

        if (reports.Count == 0 && !string.IsNullOrWhiteSpace(normalized.PeriodInstanceKey))
        {
            await RebuildAggregatesForWorkPeriodAsync(
                normalized.WorkId,
                normalized.PeriodInstanceKey,
                normalized.DynamicFormTemplateId,
                actorId,
                ct);
        }

        return new RebuildTableStatisticResponse
        {
            WorkId = normalized.WorkId,
            PeriodInstanceKey = normalized.PeriodInstanceKey,
            DynamicFormTemplateId = normalized.DynamicFormTemplateId,
            ReportCount = reports.Count
        };
    }

    public async Task<TableStatisticSummaryResponse> SearchSummaryAsync(
        TableStatisticSummaryRequest req,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        var normalized = NormalizeRequest(req);
        await EnsureCanReadScopeAsync(normalized, me.Id, ct);

        var filter = BuildSummaryFilter(normalized);
        var page = Math.Max(0, normalized.Page);
        var pageSize = Math.Clamp(normalized.PageSize <= 0 ? 50 : normalized.PageSize, 1, 200);

        var total = await _ctx.WorkReportTableStatAggregates.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.WorkReportTableStatAggregates
            .Find(filter)
            .SortByDescending(x => x.Sum)
            .ThenBy(x => x.MetricKey)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        var resultRows = rows.Select(x => new TableStatisticSummaryRow
        {
            WorkId = x.WorkId,
            ScopeType = x.ScopeType,
            ScopeId = x.ScopeId,
            RootAssignmentId = x.RootAssignmentId,
            DynamicFormTemplateId = x.DynamicFormTemplateId,
            DynamicFormTemplateCode = x.DynamicFormTemplateCode,
            DynamicFormTemplateName = x.DynamicFormTemplateName,
            DynamicExcelTemplateId = x.DynamicExcelTemplateId,
            BlockId = x.BlockId,
            TableMode = x.TableMode,
            MetricKey = x.MetricKey,
            MetricLabelCode = x.MetricLabelCode,
            RowKey = x.RowKey,
            ColumnKey = x.ColumnKey,
            DataType = NormalizeDataType(x.DataType),
            BucketKey = x.BucketKey,
            BucketLabel = x.BucketLabel,
            PeriodKey = x.PeriodKey,
            PeriodInstanceKey = x.PeriodInstanceKey,
            PeriodKind = x.PeriodKind,
            ReportStatus = x.ReportStatus,
            ValueCount = x.ValueCount,
            NumericValueCount = x.NumericValueCount,
            Sum = x.NumericValueCount > 0 ? x.Sum : null,
            Min = x.Min,
            Max = x.Max,
            Average = x.NumericValueCount > 0 ? x.Sum / x.NumericValueCount : null,
            TrueCount = x.TrueCount,
            FalseCount = x.FalseCount,
            EarliestDateUtc = x.EarliestDateUtc,
            LatestDateUtc = x.LatestDateUtc,
            ReportCount = x.ReportCount,
            UpdatedAtUtc = x.UpdatedAtUtc
        }).ToList();

        return new TableStatisticSummaryResponse
        {
            Rows = resultRows,
            TotalRows = total,
            TotalValueCount = resultRows.Sum(x => x.ValueCount),
            TotalSum = resultRows.Sum(x => x.Sum ?? 0m),
            TotalReportCount = resultRows.Sum(x => x.ReportCount)
        };
    }

    private static FilterDefinition<WorkReportTableStatValue> BuildValueFilter(
        string workId,
        string? periodInstanceKey,
        string? dynamicFormTemplateId)
    {
        var fb = Builders<WorkReportTableStatValue>.Filter;
        var filter = fb.Eq(x => x.WorkId, workId.Trim()) & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(periodInstanceKey))
            filter &= fb.Eq(x => x.PeriodInstanceKey, periodInstanceKey.Trim());

        if (!string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId.Trim());

        return filter;
    }

    private static FilterDefinition<WorkReportTableStatAggregate> BuildAggregateFilter(
        string workId,
        string? periodInstanceKey,
        string? dynamicFormTemplateId)
    {
        var fb = Builders<WorkReportTableStatAggregate>.Filter;
        var filter = fb.Eq(x => x.WorkId, workId.Trim()) & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(periodInstanceKey))
            filter &= fb.Eq(x => x.PeriodInstanceKey, periodInstanceKey.Trim());

        if (!string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId.Trim());

        return filter;
    }

    private static FilterDefinition<WorkReportTableStatAggregate> BuildSummaryFilter(
        TableStatisticSummaryRequest req)
    {
        var fb = Builders<WorkReportTableStatAggregate>.Filter;
        var filter = fb.Eq(x => x.WorkId, req.WorkId!.Trim()) & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(req.ScopeType))
            filter &= fb.Eq(x => x.ScopeType, req.ScopeType!.Trim().ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(req.ScopeId))
            filter &= fb.Eq(x => x.ScopeId, req.ScopeId!.Trim());

        if (!string.IsNullOrWhiteSpace(req.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, req.DynamicFormTemplateId!.Trim());

        if (!string.IsNullOrWhiteSpace(req.DynamicExcelTemplateId))
            filter &= fb.Eq(x => x.DynamicExcelTemplateId, req.DynamicExcelTemplateId!.Trim());

        if (!string.IsNullOrWhiteSpace(req.BlockId))
            filter &= fb.Eq(x => x.BlockId, NormalizeBlockId(req.BlockId));

        if (!string.IsNullOrWhiteSpace(req.TableMode))
            filter &= fb.Eq(x => x.TableMode, NormalizeTableMode(req.TableMode));

        if (!string.IsNullOrWhiteSpace(req.MetricKey))
            filter &= fb.Eq(x => x.MetricKey, req.MetricKey!.Trim());

        if (!string.IsNullOrWhiteSpace(req.MetricLabelCode))
            filter &= fb.Eq(x => x.MetricLabelCode, NormalizeLabelCode(req.MetricLabelCode));

        if (!string.IsNullOrWhiteSpace(req.DataType))
            filter &= fb.Eq(x => x.DataType, NormalizeDataType(req.DataType));

        if (!string.IsNullOrWhiteSpace(req.BucketKey))
            filter &= fb.Eq(x => x.BucketKey, req.BucketKey!.Trim());

        if (!string.IsNullOrWhiteSpace(req.PeriodKey))
            filter &= fb.Eq(x => x.PeriodKey, req.PeriodKey!.Trim());

        if (!string.IsNullOrWhiteSpace(req.PeriodInstanceKey))
            filter &= fb.Eq(x => x.PeriodInstanceKey, req.PeriodInstanceKey!.Trim());

        if (req.ReportStatus.HasValue)
            filter &= fb.Eq(x => x.ReportStatus, req.ReportStatus.Value);

        return filter;
    }

    private async Task<MetricTargetMap> LoadMetricTargetMapAsync(
        string? dynamicFormTemplateId,
        CancellationToken ct)
    {
        var map = new MetricTargetMap();
        if (string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            return map;

        var form = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == dynamicFormTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (form is null)
            return map;

        AddMetricTargetsFromBlockJson(form.BlocksJson, map);
        AddMetricTargetsFromBlockJson(form.ExcelBlockJson, map);
        return map;
    }

    private static void AddMetricTargetsFromBlockJson(
        string? blockJson,
        MetricTargetMap map)
    {
        if (string.IsNullOrWhiteSpace(blockJson))
            return;

        try
        {
            using var document = JsonDocument.Parse(blockJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in root.EnumerateArray())
                    AddMetricTargetsFromBlock(block, map);
                return;
            }

            AddMetricTargetsFromBlock(root, map);
        }
        catch (JsonException)
        {
            return;
        }
    }

    private static void AddMetricTargetsFromBlock(
        JsonElement block,
        MetricTargetMap map)
    {
        if (block.ValueKind != JsonValueKind.Object)
            return;

        var blockId = NormalizeBlockId(
            ReadOptionalString(block, "blockId")
            ?? ReadOptionalString(block, "id"));
        var tableMode = NormalizeTableMode(ReadOptionalString(block, "tableMode"));
        var hasDataRect = TryReadBlockDataRect(block, out var dataRect);

        if (block.TryGetProperty("metricRules", out var rules)
            && rules.ValueKind == JsonValueKind.Array)
        {
            foreach (var rule in rules.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Object)
                    continue;

                var metricKey = ReadOptionalString(rule, "metricKey");
                if (!string.IsNullOrWhiteSpace(metricKey))
                    map.AllowedMetrics.Add(BuildMetricLabelMapKey(blockId, metricKey));
            }
        }

        if (!block.TryGetProperty("metricLabelTargets", out var targets)
            || targets.ValueKind != JsonValueKind.Array)
            return;

        foreach (var target in targets.EnumerateArray())
        {
            if (target.ValueKind != JsonValueKind.Object)
                continue;

            var labelCode = NormalizeLabelCode(ReadOptionalString(target, "statisticLabelCode"));

            var metricKey = ReadOptionalString(target, "metricKey");
            if (!string.IsNullOrWhiteSpace(metricKey))
            {
                var key = BuildMetricLabelMapKey(blockId, metricKey);
                map.AllowedMetrics.Add(key);
                if (!string.IsNullOrWhiteSpace(labelCode))
                    map.LabelByMetric.TryAdd(key, labelCode);
                continue;
            }

            if (!TryReadMetricLabelRange(target, out var range) || !hasDataRect)
                continue;

            foreach (var mappedMetricKey in ExpandRangeMetricKeys(blockId, tableMode, dataRect, range))
            {
                var key = BuildMetricLabelMapKey(blockId, mappedMetricKey);
                map.AllowedMetrics.Add(key);
                if (!string.IsNullOrWhiteSpace(labelCode))
                    map.LabelByMetric.TryAdd(key, labelCode);
            }
        }
    }

    private static IEnumerable<string> ExpandRangeMetricKeys(
        string blockId,
        string tableMode,
        MetricLabelRange dataRect,
        MetricLabelRange range)
    {
        var r0 = Math.Max(dataRect.R0, range.R0);
        var c0 = Math.Max(dataRect.C0, range.C0);
        var r1 = Math.Min(dataRect.R1, range.R1);
        var c1 = Math.Min(dataRect.C1, range.C1);
        if (r1 < r0 || c1 < c0)
            yield break;

        if (tableMode == "APPEND_ROWS")
        {
            for (var c = c0; c <= c1; c++)
                yield return $"table:{blockId}.column:col_{c - dataRect.C0 + 1}";
            yield break;
        }

        if (tableMode == "APPEND_COLUMNS")
        {
            for (var r = r0; r <= r1; r++)
                yield return $"table:{blockId}.row:row_{r - dataRect.R0 + 1}";
            yield break;
        }

        for (var r = r0; r <= r1; r++)
        {
            for (var c = c0; c <= c1; c++)
            {
                var rowKey = $"row_{r - dataRect.R0 + 1}";
                var columnKey = $"col_{c - dataRect.C0 + 1}";
                yield return BuildMetricKey(blockId, rowKey, columnKey);
            }
        }
    }

    private async Task EnsureCanReadScopeAsync(
        TableStatisticSummaryRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.WorkId))
            throw ReportStatisticExceptions.WorkIdRequired("TABLE", req.WorkId);

        var scopeType = req.ScopeType?.Trim().ToUpperInvariant();
        if (scopeType is "ASSIGNMENT" or "ROOT")
        {
            if (string.IsNullOrWhiteSpace(req.ScopeId))
                throw ReportStatisticExceptions.ScopeIdRequired("TABLE", req.WorkId, scopeType, req.ScopeId);

            var assignment = await _ctx.WorkAssignments
                .Find(x => x.Id == req.ScopeId && x.WorkId == req.WorkId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw ReportStatisticExceptions.AssignmentNotFound("TABLE", req.WorkId, scopeType, req.ScopeId);

            if (CanReadAssignment(assignment, actorUserId))
                return;
        }
        else
        {
            var anyVisibleAssignment = await _ctx.WorkAssignments
                .Find(x =>
                    x.WorkId == req.WorkId &&
                    !x.IsDeleted &&
                    (x.CreatedByUserId == actorUserId || x.LeaderWatcherUserIds.Contains(actorUserId)))
                .Limit(1)
                .AnyAsync(ct);

            if (anyVisibleAssignment)
                return;
        }

        throw ReportStatisticExceptions.ReadForbidden("TABLE", req.WorkId, scopeType, req.ScopeId, actorUserId);
    }

    private async Task EnsureCanReadWorkAsync(
        string workId,
        string actorUserId,
        CancellationToken ct)
    {
        var anyVisibleAssignment = await _ctx.WorkAssignments
            .Find(x =>
                x.WorkId == workId &&
                !x.IsDeleted &&
                (x.CreatedByUserId == actorUserId || x.LeaderWatcherUserIds.Contains(actorUserId)))
            .Limit(1)
            .AnyAsync(ct);

        if (!anyVisibleAssignment)
            throw ReportStatisticExceptions.RebuildForbidden("TABLE", workId, actorUserId);
    }

    private static bool CanReadAssignment(WorkAssignment assignment, string actorUserId)
        => string.Equals(assignment.CreatedByUserId, actorUserId, StringComparison.Ordinal)
           || (assignment.LeaderWatcherUserIds?.Contains(actorUserId) ?? false);

    private static TableStatisticSummaryRequest NormalizeRequest(TableStatisticSummaryRequest req)
    {
        req ??= new TableStatisticSummaryRequest();
        var workId = req.WorkId?.Trim();
        if (string.IsNullOrWhiteSpace(workId))
            throw ReportStatisticExceptions.WorkIdRequired("TABLE", req.WorkId);

        var scopeType = string.IsNullOrWhiteSpace(req.ScopeType)
            ? "WORK"
            : req.ScopeType.Trim().ToUpperInvariant();

        if (scopeType is not ("WORK" or "ROOT" or "ASSIGNMENT"))
            throw ReportStatisticExceptions.ScopeTypeInvalid("TABLE", workId, req.ScopeType);

        var scopeId = string.IsNullOrWhiteSpace(req.ScopeId)
            ? (scopeType == "WORK" ? workId : null)
            : req.ScopeId.Trim();

        return new TableStatisticSummaryRequest
        {
            WorkId = workId,
            ScopeType = scopeType,
            ScopeId = scopeId,
            DynamicFormTemplateId = NormalizeOptionalId(req.DynamicFormTemplateId),
            DynamicExcelTemplateId = NormalizeOptionalId(req.DynamicExcelTemplateId),
            BlockId = string.IsNullOrWhiteSpace(req.BlockId) ? null : NormalizeBlockId(req.BlockId),
            TableMode = string.IsNullOrWhiteSpace(req.TableMode) ? null : NormalizeTableMode(req.TableMode),
            MetricKey = string.IsNullOrWhiteSpace(req.MetricKey) ? null : req.MetricKey.Trim(),
            MetricLabelCode = string.IsNullOrWhiteSpace(req.MetricLabelCode) ? null : NormalizeLabelCode(req.MetricLabelCode),
            DataType = string.IsNullOrWhiteSpace(req.DataType) ? null : NormalizeDataType(req.DataType),
            BucketKey = string.IsNullOrWhiteSpace(req.BucketKey) ? null : req.BucketKey.Trim(),
            PeriodKey = string.IsNullOrWhiteSpace(req.PeriodKey) ? null : req.PeriodKey.Trim(),
            PeriodInstanceKey = string.IsNullOrWhiteSpace(req.PeriodInstanceKey) ? null : req.PeriodInstanceKey.Trim(),
            ReportStatus = req.ReportStatus,
            Page = Math.Max(0, req.Page),
            PageSize = Math.Clamp(req.PageSize <= 0 ? 50 : req.PageSize, 1, 200)
        };
    }

    private static RebuildTableStatisticRequest NormalizeRebuildRequest(RebuildTableStatisticRequest req)
    {
        req ??= new RebuildTableStatisticRequest();
        var workId = req.WorkId?.Trim();
        if (string.IsNullOrWhiteSpace(workId))
            throw ReportStatisticExceptions.WorkIdRequired("TABLE", req.WorkId);

        return new RebuildTableStatisticRequest
        {
            WorkId = workId,
            PeriodInstanceKey = string.IsNullOrWhiteSpace(req.PeriodInstanceKey) ? null : req.PeriodInstanceKey.Trim(),
            DynamicFormTemplateId = NormalizeOptionalId(req.DynamicFormTemplateId)
        };
    }

    private static List<ParsedMetricValue> ExtractMetricValues(
        string? tableValuesJson,
        MetricTargetMap metricTargets)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson) || metricTargets.AllowedMetrics.Count == 0)
            return new List<ParsedMetricValue>();

        try
        {
            var root = JsonSerializer.Deserialize<TableValuesRoot>(tableValuesJson, JsonOptions);
            if (root?.Blocks is null || root.Blocks.Count == 0)
                return new List<ParsedMetricValue>();

            var rows = new List<ParsedMetricValue>();
            foreach (var block in root.Blocks)
            {
                var blockId = NormalizeBlockId(block.BlockId);
                var tableMode = NormalizeTableMode(block.TableMode);
                var dynamicExcelTemplateId = NormalizeObjectIdOrNull(block.DynamicExcelTemplateId);

                if (tableMode == "APPEND_ROWS")
                {
                    rows.AddRange(ExtractAppendRows(block, blockId, dynamicExcelTemplateId, metricTargets));
                    continue;
                }

                if (tableMode == "APPEND_COLUMNS")
                {
                    rows.AddRange(ExtractAppendColumns(block, blockId, dynamicExcelTemplateId, metricTargets));
                    continue;
                }

                if (tableMode == "MATRIX")
                {
                    rows.AddRange(ExtractMatrixCells(block, blockId, dynamicExcelTemplateId, metricTargets));
                    continue;
                }

                if (tableMode == "FIXED_GRID")
                    rows.AddRange(ExtractFixedGrid(block, blockId, dynamicExcelTemplateId, metricTargets));
            }

            return rows;
        }
        catch (JsonException)
        {
            return new List<ParsedMetricValue>();
        }
    }

    private static IEnumerable<ParsedMetricValue> ExtractFixedGrid(
        TableValuesBlock block,
        string blockId,
        string? dynamicExcelTemplateId,
        MetricTargetMap metricTargets)
    {
        if (block.Values1D is not { Count: > 0 })
            yield break;

        var metrics = NormalizeMetricDefinitions(block.MetricDefinitions, blockId, "FIXED_GRID");
        if (metrics.Count == 0)
            metrics = NormalizeIndexMap(block.IndexMap, blockId);
        if (metrics.Count == 0)
            metrics = BuildFallbackMetricMap(blockId, block.W, block.H, block.Values1D.Count, metricTargets);

        foreach (var metric in metrics)
        {
            if (!metricTargets.Contains(blockId, metric.MetricKey))
                continue;

            if (metric.Index < 0 || metric.Index >= block.Values1D.Count)
                continue;

            foreach (var value in ParseTypedMetricValue(
                         block.Values1D[metric.Index],
                         metric,
                         blockId,
                         "FIXED_GRID",
                         dynamicExcelTemplateId,
                         $"index:{metric.Index}"))
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<ParsedMetricValue> ExtractAppendRows(
        TableValuesBlock block,
        string blockId,
        string? dynamicExcelTemplateId,
        MetricTargetMap metricTargets)
    {
        foreach (var row in block.Rows ?? new List<TableAppendRow>())
        {
            if (row.Cells is null || row.Cells.Count == 0)
                continue;

            var rowSource = NormalizeMetricPart(row.RowInstanceId, $"row:{row.RowOrder.GetValueOrDefault()}");
            foreach (var cell in row.Cells)
            {
                var columnKey = NormalizeMetricPart(cell.Key, "value");
                var metric = ResolveAppendRowsMetric(block, blockId, columnKey);
                if (!metricTargets.Contains(blockId, metric.MetricKey))
                    continue;

                foreach (var value in ParseTypedMetricValue(
                             cell.Value,
                             metric,
                             blockId,
                             "APPEND_ROWS",
                             dynamicExcelTemplateId,
                             $"{rowSource}:{columnKey}"))
                {
                    yield return value;
                }
            }
        }
    }

    private static IEnumerable<ParsedMetricValue> ExtractAppendColumns(
        TableValuesBlock block,
        string blockId,
        string? dynamicExcelTemplateId,
        MetricTargetMap metricTargets)
    {
        foreach (var column in block.Columns ?? new List<TableAppendColumn>())
        {
            if (column.Cells is null || column.Cells.Count == 0)
                continue;

            var columnSource = NormalizeMetricPart(column.ColumnInstanceId, $"column:{column.ColumnOrder.GetValueOrDefault()}");
            foreach (var cell in column.Cells)
            {
                var rowKey = NormalizeMetricPart(cell.Key, "row");
                var metric = ResolveAppendColumnsMetric(block, blockId, rowKey);
                if (!metricTargets.Contains(blockId, metric.MetricKey))
                    continue;

                foreach (var value in ParseTypedMetricValue(
                             cell.Value,
                             metric,
                             blockId,
                             "APPEND_COLUMNS",
                             dynamicExcelTemplateId,
                             $"{columnSource}:{rowKey}"))
                {
                    yield return value;
                }
            }
        }
    }

    private static IEnumerable<ParsedMetricValue> ExtractMatrixCells(
        TableValuesBlock block,
        string blockId,
        string? dynamicExcelTemplateId,
        MetricTargetMap metricTargets)
    {
        foreach (var cell in block.Cells ?? new List<TableMatrixCell>())
        {
            var rowKey = NormalizeMetricPart(cell.RowKey, "row");
            var columnKey = NormalizeMetricPart(cell.ColumnKey, "column");
            var metricKey = string.IsNullOrWhiteSpace(cell.MetricKey)
                ? BuildMetricKey(blockId, rowKey, columnKey)
                : cell.MetricKey.Trim();
            if (!metricTargets.Contains(blockId, metricKey))
                continue;

            var metric = ResolveMatrixMetric(block, blockId, rowKey, columnKey, metricKey);

            foreach (var value in ParseTypedMetricValue(
                         cell.Value,
                         metric,
                         blockId,
                         "MATRIX",
                         dynamicExcelTemplateId,
                         metricKey))
            {
                yield return value;
            }
        }
    }

    private static MetricContract ResolveAppendRowsMetric(
        TableValuesBlock block,
        string blockId,
        string columnKey)
    {
        var definitions = NormalizeMetricDefinitions(block.MetricDefinitions, blockId, "APPEND_ROWS");
        return definitions.FirstOrDefault(x => string.Equals(x.ColumnKey, columnKey, StringComparison.Ordinal))
               ?? new MetricContract(
                   0,
                   "APPEND_ROWS",
                   columnKey,
                   $"table:{blockId}.column:{columnKey}",
                   "NUMBER",
                   Array.Empty<MetricOption>());
    }

    private static MetricContract ResolveAppendColumnsMetric(
        TableValuesBlock block,
        string blockId,
        string rowKey)
    {
        var definitions = NormalizeMetricDefinitions(block.MetricDefinitions, blockId, "APPEND_COLUMNS");
        return definitions.FirstOrDefault(x => string.Equals(x.RowKey, rowKey, StringComparison.Ordinal))
               ?? new MetricContract(
                   0,
                   rowKey,
                   "APPEND_COLUMNS",
                   $"table:{blockId}.row:{rowKey}",
                   "NUMBER",
                   Array.Empty<MetricOption>());
    }

    private static MetricContract ResolveMatrixMetric(
        TableValuesBlock block,
        string blockId,
        string rowKey,
        string columnKey,
        string metricKey)
    {
        var definitions = NormalizeMetricDefinitions(block.MetricDefinitions, blockId, "MATRIX");
        return definitions.FirstOrDefault(x => string.Equals(x.MetricKey, metricKey, StringComparison.Ordinal))
               ?? new MetricContract(
                   0,
                   rowKey,
                   columnKey,
                   metricKey,
                   "NUMBER",
                   Array.Empty<MetricOption>());
    }

    private static IEnumerable<ParsedMetricValue> ParseTypedMetricValue(
        JsonElement rawValue,
        MetricContract metric,
        string blockId,
        string tableMode,
        string? dynamicExcelTemplateId,
        string sourceKey)
    {
        var dataType = NormalizeDataType(metric.DataType);
        if (IsBlankJsonElement(rawValue))
            yield break;

        if (dataType == "NUMBER")
        {
            var number = ToNullableDecimal(rawValue);
            if (!number.HasValue)
                yield break;

            yield return BuildParsedMetricValue(
                blockId,
                tableMode,
                dynamicExcelTemplateId,
                metric,
                sourceKey,
                numberValue: number.Value);
            yield break;
        }

        if (dataType == "BOOLEAN")
        {
            if (!TryReadBoolean(rawValue, out var booleanValue))
                yield break;

            yield return BuildParsedMetricValue(
                blockId,
                tableMode,
                dynamicExcelTemplateId,
                metric,
                sourceKey,
                booleanValue: booleanValue);
            yield break;
        }

        if (dataType is "DATE" or "FULL_DATE")
        {
            var date = ReadDateValue(rawValue, dataType == "FULL_DATE");
            if (!date.HasValue)
                yield break;

            yield return BuildParsedMetricValue(
                blockId,
                tableMode,
                dynamicExcelTemplateId,
                metric,
                sourceKey,
                dateValue: date.Value);
            yield break;
        }

        if (dataType == "SHORT_TEXT")
        {
            var text = ReadShortTextBucket(rawValue);
            if (string.IsNullOrWhiteSpace(text))
                yield break;

            var option = ResolveMetricOption(metric.Options, text);
            if (metric.Options.Length > 0 && option is null)
                yield break;
            var bucketKey = option?.Code ?? text;
            var bucketLabel = option?.Label ?? text;

            yield return BuildParsedMetricValue(
                blockId,
                tableMode,
                dynamicExcelTemplateId,
                metric,
                sourceKey,
                textValue: bucketKey,
                bucketKey: bucketKey,
                bucketLabel: bucketLabel);
        }

        if (dataType == "MULTI_SELECT")
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in ReadStringListValues(rawValue))
            {
                var option = ResolveMetricOption(metric.Options, item);
                if (metric.Options.Length > 0 && option is null)
                    continue;

                var bucketKey = option?.Code ?? item;
                var bucketLabel = option?.Label ?? item;
                if (string.IsNullOrWhiteSpace(bucketKey) || !seen.Add(bucketKey))
                    continue;

                yield return BuildParsedMetricValue(
                    blockId,
                    tableMode,
                    dynamicExcelTemplateId,
                    metric,
                    sourceKey,
                    textValue: bucketKey,
                    bucketKey: bucketKey,
                    bucketLabel: bucketLabel);
            }
        }
    }

    private static ParsedMetricValue BuildParsedMetricValue(
        string blockId,
        string tableMode,
        string? dynamicExcelTemplateId,
        MetricContract metric,
        string sourceKey,
        decimal? numberValue = null,
        string? textValue = null,
        bool? booleanValue = null,
        DateTime? dateValue = null,
        string? bucketKey = null,
        string? bucketLabel = null)
        => new(
            blockId,
            tableMode,
            dynamicExcelTemplateId,
            metric.MetricKey,
            metric.RowKey,
            metric.ColumnKey,
            sourceKey,
            NormalizeDataType(metric.DataType),
            numberValue,
            textValue,
            booleanValue,
            dateValue,
            bucketKey,
            bucketLabel);

    private static List<MetricContract> NormalizeMetricDefinitions(
        List<TableMetricDefinition>? items,
        string blockId,
        string tableMode)
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

                return new MetricContract(
                    index,
                    rowKey,
                    columnKey,
                    metricKey,
                    NormalizeDataType(item.DataType),
                    NormalizeMetricOptions(item.Options));
            })
            .GroupBy(x => tableMode == "APPEND_ROWS"
                    ? x.ColumnKey
                    : tableMode == "APPEND_COLUMNS"
                        ? x.RowKey
                        : x.MetricKey,
                StringComparer.Ordinal)
            .Select(x => x.First())
            .OrderBy(x => x.Index)
            .ToList();
    }

    private static List<MetricContract> NormalizeIndexMap(
        List<TableIndexMapItem>? items,
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

                return new MetricContract(
                    index,
                    rowKey,
                    columnKey,
                    metricKey,
                    "NUMBER",
                    Array.Empty<MetricOption>());
            })
            .GroupBy(x => x.MetricKey, StringComparer.Ordinal)
            .Select(x => x.First())
            .OrderBy(x => x.Index)
            .ToList();
    }

    private static List<MetricContract> BuildFallbackMetricMap(
        string blockId,
        int? width,
        int? height,
        int valueCount,
        MetricTargetMap metricTargets)
    {
        var w = width.GetValueOrDefault();
        var h = height.GetValueOrDefault();
        if (w <= 0 || h <= 0 || valueCount <= 0 || metricTargets.AllowedMetrics.Count == 0)
            return new List<MetricContract>();

        var metrics = new List<MetricContract>();
        var normalizedBlockId = NormalizeBlockId(blockId);
        var prefix = $"{normalizedBlockId}:table:{normalizedBlockId}.row:";
        foreach (var key in metricTargets.AllowedMetrics)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var metricKey = key[(normalizedBlockId.Length + 1)..];
            if (!TryReadRowColumnFromMetricKey(metricKey, out var rowKey, out var columnKey))
                continue;

            var rowIndex = IndexFromOrdinalPart(rowKey, "row_");
            var columnIndex = IndexFromOrdinalPart(columnKey, "col_");
            if (!rowIndex.HasValue || !columnIndex.HasValue)
                continue;

            var index = rowIndex.Value * w + columnIndex.Value;
            if (index < 0 || index >= valueCount || index >= w * h)
                continue;

            metrics.Add(new MetricContract(
                index,
                rowKey,
                columnKey,
                BuildMetricKey(blockId, rowKey, columnKey),
                "NUMBER",
                Array.Empty<MetricOption>()));
        }

        return metrics
            .GroupBy(x => x.MetricKey, StringComparer.Ordinal)
            .Select(x => x.First())
            .OrderBy(x => x.Index)
            .ToList();
    }

    private static bool TryReadRowColumnFromMetricKey(
        string metricKey,
        out string rowKey,
        out string columnKey)
    {
        rowKey = string.Empty;
        columnKey = string.Empty;

        var rowMarker = ".row:";
        var columnMarker = ".column:";
        var rowStart = metricKey.IndexOf(rowMarker, StringComparison.Ordinal);
        var columnStart = metricKey.IndexOf(columnMarker, StringComparison.Ordinal);
        if (rowStart < 0 || columnStart < 0 || rowStart >= columnStart)
            return false;

        rowStart += rowMarker.Length;
        rowKey = metricKey[rowStart..columnStart].Trim();
        columnStart += columnMarker.Length;
        columnKey = metricKey[columnStart..].Trim();
        return !string.IsNullOrWhiteSpace(rowKey) && !string.IsNullOrWhiteSpace(columnKey);
    }

    private static int? IndexFromOrdinalPart(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        return int.TryParse(value[prefix.Length..], out var n) && n > 0
            ? n - 1
            : null;
    }

    private static IEnumerable<AggregateScope> ResolveScopes(WorkReportTableStatValue value)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        yield return Add("WORK", value.WorkId);

        if (!string.IsNullOrWhiteSpace(value.RootAssignmentId))
            yield return Add("ROOT", value.RootAssignmentId);

        foreach (var assignmentId in value.AncestorAssignmentIds.Append(value.WorkAssignmentId))
        {
            if (string.IsNullOrWhiteSpace(assignmentId))
                continue;

            yield return Add("ASSIGNMENT", assignmentId);
        }

        AggregateScope Add(string type, string? id)
        {
            var scopeId = id?.Trim() ?? string.Empty;
            var key = $"{type}:{scopeId}";
            return !string.IsNullOrWhiteSpace(scopeId) && seen.Add(key)
                ? new AggregateScope(type, scopeId)
                : new AggregateScope(string.Empty, string.Empty);
        }
    }

    private static List<string> ExtractAncestorAssignmentIds(WorkAssignment? assignment, string currentAssignmentId)
    {
        if (assignment is null || string.IsNullOrWhiteSpace(assignment.Path))
            return new List<string>();

        return assignment.Path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.Equals(x, currentAssignmentId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static decimal? ToNullableDecimal(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static bool IsBlankJsonElement(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => true,
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => !value.EnumerateArray().Any(),
            _ => false
        };

    private static string NormalizeDataType(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return normalized switch
        {
            "NUMBER" or "DECIMAL" or "NUMERIC" => "NUMBER",
            "SHORT_TEXT" or "SHORTTEXT" or "TEXT" or "STRING" or "STRING_LIST" or "STRINGLIST" => "SHORT_TEXT",
            "MULTI_SELECT" or "MULTISELECT" => "MULTI_SELECT",
            "BOOLEAN" or "BOOL" => "BOOLEAN",
            "DATE" => "DATE",
            "FULL_DATE" or "FULLDATE" or "STRICT_DATE" => "FULL_DATE",
            _ => "NUMBER"
        };
    }

    private static string? ReadShortTextBucket(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return text.Length <= ShortTextBucketMaxLength
            ? text
            : text[..ShortTextBucketMaxLength];
    }

    private static MetricOption[] NormalizeMetricOptions(List<TableMetricOption>? options)
    {
        if (options is null || options.Count == 0)
            return Array.Empty<MetricOption>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<MetricOption>();
        foreach (var option in options)
        {
            var code = option.Code?.Trim();
            if (string.IsNullOrWhiteSpace(code) || !seen.Add(code))
                continue;

            var label = string.IsNullOrWhiteSpace(option.Label) ? code : option.Label.Trim();
            rows.Add(new MetricOption(code, label));
        }

        return rows.ToArray();
    }

    private static IEnumerable<string> ReadStringListValues(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
                yield return text;
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var text = item.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
                yield return text;
        }
    }

    private static MetricOption? ResolveMetricOption(
        IReadOnlyCollection<MetricOption> options,
        string value)
    {
        if (options.Count == 0)
            return null;

        return options.FirstOrDefault(option =>
            string.Equals(option.Code, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.Label, value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadBoolean(JsonElement value, out bool result)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            result = value.GetBoolean();
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            if (number == 1m)
            {
                result = true;
                return true;
            }

            if (number == 0m)
            {
                result = false;
                return true;
            }
        }

        var text = value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim().ToLowerInvariant()
            : null;
        if (text is "true" or "1" or "yes" or "y" or "co" or "có")
        {
            result = true;
            return true;
        }

        if (text is "false" or "0" or "no" or "n" or "khong" or "không")
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }

    private static DateTime? ReadDateValue(JsonElement value, bool requireFullDate)
    {
        if (value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (DateTime.TryParseExact(
                text,
                "dd/MM/yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var fullDate))
        {
            return DateTime.SpecifyKind(fullDate.Date, DateTimeKind.Utc);
        }

        if (requireFullDate)
            return null;

        if (DateTime.TryParseExact(
                text,
                "MM/yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var monthDate))
        {
            return DateTime.SpecifyKind(new DateTime(monthDate.Year, monthDate.Month, 1), DateTimeKind.Utc);
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
               && year is >= 1 and <= 9999
            ? DateTime.SpecifyKind(new DateTime(year, 1, 1), DateTimeKind.Utc)
            : null;
    }

    private static string NormalizeBlockId(string? value)
        => string.IsNullOrWhiteSpace(value) ? "excel_block" : value.Trim();

    private static string NormalizeLabelCode(string? value)
    {
        var code = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return LabelCodeRegex.IsMatch(code) ? code : string.Empty;
    }

    private static string? ReadOptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static int? ReadOptionalNonNegativeInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out var number)
               && number >= 0
            ? number
            : null;
    }

    private static bool TryReadBlockDataRect(JsonElement block, out MetricLabelRange range)
    {
        range = default;
        return block.TryGetProperty("dataRect", out var dataRect)
               && dataRect.ValueKind == JsonValueKind.Object
               && TryReadRangeCoordinates(dataRect, out range);
    }

    private static bool TryReadMetricLabelRange(JsonElement target, out MetricLabelRange range)
    {
        if (target.TryGetProperty("range", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            return TryReadRangeCoordinates(nested, out range);
        }

        return TryReadRangeCoordinates(target, out range);
    }

    private static bool TryReadRangeCoordinates(JsonElement element, out MetricLabelRange range)
    {
        range = default;
        var r0 = ReadOptionalNonNegativeInt(element, "r0");
        var c0 = ReadOptionalNonNegativeInt(element, "c0");
        var r1 = ReadOptionalNonNegativeInt(element, "r1");
        var c1 = ReadOptionalNonNegativeInt(element, "c1");
        if (!r0.HasValue || !c0.HasValue || !r1.HasValue || !c1.HasValue)
            return false;
        if (r1.Value < r0.Value || c1.Value < c0.Value)
            return false;

        range = new MetricLabelRange(r0.Value, c0.Value, r1.Value, c1.Value);
        return true;
    }

    private static string BuildMetricLabelMapKey(string blockId, string metricKey)
        => $"{NormalizeBlockId(blockId)}:{metricKey.Trim()}";

    private static string NormalizeTableMode(string? value)
    {
        var tableMode = string.IsNullOrWhiteSpace(value) ? "FIXED_GRID" : value.Trim().ToUpperInvariant();
        return tableMode is "APPEND_ROWS" or "APPEND_COLUMNS" or "MATRIX" or "SUMMARY_TEMPLATE"
            ? tableMode
            : "FIXED_GRID";
    }

    private static string NormalizeMetricPart(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string BuildMetricKey(string blockId, string rowKey, string columnKey)
        => $"table:{blockId}.row:{rowKey}.column:{columnKey}";

    private static string? NormalizeOptionalId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeObjectIdOrNull(string? value)
        => ObjectId.TryParse(value, out _) ? value : null;

    private sealed class TableValuesRoot
    {
        public List<TableValuesBlock>? Blocks { get; set; }
    }

    private sealed class TableValuesBlock
    {
        public string? BlockId { get; set; }
        public string? DynamicExcelTemplateId { get; set; }
        public string? TableMode { get; set; }
        public int? W { get; set; }
        public int? H { get; set; }
        public List<JsonElement>? Values1D { get; set; }
        public List<TableIndexMapItem>? IndexMap { get; set; }
        public List<TableMetricDefinition>? MetricDefinitions { get; set; }
        public List<TableAppendRow>? Rows { get; set; }
        public List<TableAppendColumn>? Columns { get; set; }
        public List<TableMatrixCell>? Cells { get; set; }
    }

    private sealed class TableIndexMapItem
    {
        public int Index { get; set; } = -1;
        public string? RowKey { get; set; }
        public string? ColumnKey { get; set; }
        public string? MetricKey { get; set; }
    }

    private sealed class TableMetricDefinition
    {
        public int Index { get; set; } = -1;
        public string? MetricKey { get; set; }
        public string? RowKey { get; set; }
        public string? ColumnKey { get; set; }
        public string? DataType { get; set; }
        public List<TableMetricOption>? Options { get; set; }
    }

    private sealed class TableMetricOption
    {
        public string? Code { get; set; }
        public string? Label { get; set; }
    }

    private sealed class TableAppendRow
    {
        public string? RowInstanceId { get; set; }
        public int? RowOrder { get; set; }
        public Dictionary<string, JsonElement>? Cells { get; set; }
    }

    private sealed class TableAppendColumn
    {
        public string? ColumnInstanceId { get; set; }
        public int? ColumnOrder { get; set; }
        public Dictionary<string, JsonElement>? Cells { get; set; }
    }

    private sealed class TableMatrixCell
    {
        public string? RowKey { get; set; }
        public string? ColumnKey { get; set; }
        public string? MetricKey { get; set; }
        public JsonElement Value { get; set; }
    }

    private sealed class MetricTargetMap
    {
        public HashSet<string> AllowedMetrics { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> LabelByMetric { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Contains(string blockId, string metricKey)
            => AllowedMetrics.Contains(BuildMetricLabelMapKey(blockId, metricKey));
    }

    private sealed record ParsedMetricValue(
        string BlockId,
        string TableMode,
        string? DynamicExcelTemplateId,
        string MetricKey,
        string RowKey,
        string ColumnKey,
        string SourceKey,
        string DataType,
        decimal? NumberValue,
        string? TextValue,
        bool? BooleanValue,
        DateTime? DateValue,
        string? BucketKey,
        string? BucketLabel);

    private sealed record MetricContract(
        int Index,
        string RowKey,
        string ColumnKey,
        string MetricKey,
        string DataType,
        MetricOption[] Options);

    private sealed record MetricOption(
        string Code,
        string Label);

    private readonly record struct MetricLabelRange(
        int R0,
        int C0,
        int R1,
        int C1);

    private sealed record AggregateScope(string ScopeType, string ScopeId);

    private sealed record AggregateKey(
        string WorkId,
        string ScopeType,
        string ScopeId,
        string? DynamicFormTemplateId,
        string? DynamicExcelTemplateId,
        string BlockId,
        string TableMode,
        string MetricKey,
        string? MetricLabelCode,
        string DataType,
        string? BucketKey,
        string PeriodInstanceKey,
        int ReportStatus);

    private sealed class AggregateBucket
    {
        public WorkReportTableStatAggregate Row { get; set; } = default!;
        public HashSet<string> ReportIds { get; } = new(StringComparer.Ordinal);

        public void Add(WorkReportTableStatValue value)
        {
            Row.ValueCount += 1;

            var dataType = NormalizeDataType(value.DataType);
            if (dataType == "NUMBER")
            {
                Row.NumericValueCount += 1;
                Row.Sum += value.Value;
                Row.Min = Row.Min.HasValue ? Math.Min(Row.Min.Value, value.Value) : value.Value;
                Row.Max = Row.Max.HasValue ? Math.Max(Row.Max.Value, value.Value) : value.Value;
            }
            else if (dataType == "BOOLEAN" && value.BooleanValue.HasValue)
            {
                if (value.BooleanValue.Value)
                    Row.TrueCount += 1;
                else
                    Row.FalseCount += 1;
            }
            else if ((dataType == "DATE" || dataType == "FULL_DATE") && value.DateValue.HasValue)
            {
                var date = value.DateValue.Value;
                Row.EarliestDateUtc = Row.EarliestDateUtc.HasValue
                    ? (date < Row.EarliestDateUtc.Value ? date : Row.EarliestDateUtc.Value)
                    : date;
                Row.LatestDateUtc = Row.LatestDateUtc.HasValue
                    ? (date > Row.LatestDateUtc.Value ? date : Row.LatestDateUtc.Value)
                    : date;
            }

            ReportIds.Add(value.WorkAssignmentReportId);
        }
    }
}
