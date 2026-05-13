using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.WorkAssignmentReports;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public sealed class WorkReportTableStatisticsService : IWorkReportTableStatisticsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;

    public WorkReportTableStatisticsService(MongoDbContext ctx, MeAccessor me)
    {
        _ctx = ctx;
        _me = me;
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

        var metricValues = ExtractMetricValues(report.TableValuesJson)
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

            var rows = metricValues.Select(value => new WorkReportTableStatValue
            {
                Id = ObjectId.GenerateNewId().ToString(),
                WorkId = report.WorkId,
                WorkAssignmentId = report.WorkAssignmentId,
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
                RowKey = value.RowKey,
                ColumnKey = value.ColumnKey,
                SourceKey = value.SourceKey,
                PeriodKey = report.PeriodKey,
                PeriodInstanceKey = report.PeriodInstanceKey,
                PeriodKind = report.PeriodKind,
                PeriodAnchorDate = sourceWindow.PeriodAnchorDate,
                PeriodStartDate = sourceWindow.PeriodStartDate,
                PeriodEndDate = sourceWindow.PeriodEndDate,
                CompletedDate = sourceWindow.CompletedDate,
                IsHistoricalData = sourceWindow.IsHistoricalData,
                ReportStatus = (int)report.Status,
                Value = value.Value,
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
                            RowKey = value.RowKey,
                            ColumnKey = value.ColumnKey,
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
            RowKey = x.RowKey,
            ColumnKey = x.ColumnKey,
            PeriodKey = x.PeriodKey,
            PeriodInstanceKey = x.PeriodInstanceKey,
            PeriodKind = x.PeriodKind,
            ReportStatus = x.ReportStatus,
            ValueCount = x.ValueCount,
            Sum = x.ValueCount > 0 ? x.Sum : null,
            Min = x.Min,
            Max = x.Max,
            Average = x.ValueCount > 0 ? x.Sum / x.ValueCount : null,
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

        if (!string.IsNullOrWhiteSpace(req.PeriodKey))
            filter &= fb.Eq(x => x.PeriodKey, req.PeriodKey!.Trim());

        if (!string.IsNullOrWhiteSpace(req.PeriodInstanceKey))
            filter &= fb.Eq(x => x.PeriodInstanceKey, req.PeriodInstanceKey!.Trim());

        if (req.ReportStatus.HasValue)
            filter &= fb.Eq(x => x.ReportStatus, req.ReportStatus.Value);

        return filter;
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

    private static List<ParsedMetricValue> ExtractMetricValues(string? tableValuesJson)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson))
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
                    rows.AddRange(ExtractAppendRows(block, blockId, dynamicExcelTemplateId));
                    continue;
                }

                if (tableMode == "APPEND_COLUMNS")
                {
                    rows.AddRange(ExtractAppendColumns(block, blockId, dynamicExcelTemplateId));
                    continue;
                }

                if (tableMode == "MATRIX")
                {
                    rows.AddRange(ExtractMatrixCells(block, blockId, dynamicExcelTemplateId));
                    continue;
                }

                if (tableMode == "FIXED_GRID")
                    rows.AddRange(ExtractFixedGrid(block, blockId, dynamicExcelTemplateId));
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
        string? dynamicExcelTemplateId)
    {
        if (block.Values1D is not { Count: > 0 })
            yield break;

        var metrics = NormalizeIndexMap(block.IndexMap, blockId);
        if (metrics.Count == 0)
            metrics = BuildFallbackMetricMap(blockId, block.W, block.H, block.Values1D.Count);

        foreach (var metric in metrics)
        {
            if (metric.Index < 0 || metric.Index >= block.Values1D.Count)
                continue;

            var value = ToNullableDecimal(block.Values1D[metric.Index]);
            if (!value.HasValue)
                continue;

            yield return new ParsedMetricValue(
                blockId,
                "FIXED_GRID",
                dynamicExcelTemplateId,
                metric.MetricKey,
                metric.RowKey,
                metric.ColumnKey,
                $"index:{metric.Index}",
                value.Value);
        }
    }

    private static IEnumerable<ParsedMetricValue> ExtractAppendRows(
        TableValuesBlock block,
        string blockId,
        string? dynamicExcelTemplateId)
    {
        foreach (var row in block.Rows ?? new List<TableAppendRow>())
        {
            if (row.Cells is null || row.Cells.Count == 0)
                continue;

            var rowSource = NormalizeMetricPart(row.RowInstanceId, $"row:{row.RowOrder.GetValueOrDefault()}");
            foreach (var cell in row.Cells)
            {
                var value = ToNullableDecimal(cell.Value);
                if (!value.HasValue)
                    continue;

                var columnKey = NormalizeMetricPart(cell.Key, "value");
                yield return new ParsedMetricValue(
                    blockId,
                    "APPEND_ROWS",
                    dynamicExcelTemplateId,
                    $"table:{blockId}.column:{columnKey}",
                    "APPEND_ROWS",
                    columnKey,
                    $"{rowSource}:{columnKey}",
                    value.Value);
            }
        }
    }

    private static IEnumerable<ParsedMetricValue> ExtractAppendColumns(
        TableValuesBlock block,
        string blockId,
        string? dynamicExcelTemplateId)
    {
        foreach (var column in block.Columns ?? new List<TableAppendColumn>())
        {
            if (column.Cells is null || column.Cells.Count == 0)
                continue;

            var columnSource = NormalizeMetricPart(column.ColumnInstanceId, $"column:{column.ColumnOrder.GetValueOrDefault()}");
            foreach (var cell in column.Cells)
            {
                var value = ToNullableDecimal(cell.Value);
                if (!value.HasValue)
                    continue;

                var rowKey = NormalizeMetricPart(cell.Key, "row");
                yield return new ParsedMetricValue(
                    blockId,
                    "APPEND_COLUMNS",
                    dynamicExcelTemplateId,
                    $"table:{blockId}.row:{rowKey}",
                    rowKey,
                    "APPEND_COLUMNS",
                    $"{columnSource}:{rowKey}",
                    value.Value);
            }
        }
    }

    private static IEnumerable<ParsedMetricValue> ExtractMatrixCells(
        TableValuesBlock block,
        string blockId,
        string? dynamicExcelTemplateId)
    {
        foreach (var cell in block.Cells ?? new List<TableMatrixCell>())
        {
            var value = ToNullableDecimal(cell.Value);
            if (!value.HasValue)
                continue;

            var rowKey = NormalizeMetricPart(cell.RowKey, "row");
            var columnKey = NormalizeMetricPart(cell.ColumnKey, "column");
            var metricKey = string.IsNullOrWhiteSpace(cell.MetricKey)
                ? BuildMetricKey(blockId, rowKey, columnKey)
                : cell.MetricKey.Trim();

            yield return new ParsedMetricValue(
                blockId,
                "MATRIX",
                dynamicExcelTemplateId,
                metricKey,
                rowKey,
                columnKey,
                metricKey,
                value.Value);
        }
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
        int? height,
        int valueCount)
    {
        var w = width.GetValueOrDefault();
        var h = height.GetValueOrDefault();
        if (w <= 0 || h <= 0 || valueCount <= 0)
            return new List<MetricContract>();

        var count = Math.Min(w * h, valueCount);
        var metrics = new List<MetricContract>();
        for (var index = 0; index < count; index++)
        {
            var rowKey = $"row_{index / w + 1}";
            var columnKey = $"col_{index % w + 1}";
            metrics.Add(new MetricContract(index, rowKey, columnKey, BuildMetricKey(blockId, rowKey, columnKey)));
        }

        return metrics;
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
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string NormalizeBlockId(string? value)
        => string.IsNullOrWhiteSpace(value) ? "excel_block" : value.Trim();

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

    private sealed record ParsedMetricValue(
        string BlockId,
        string TableMode,
        string? DynamicExcelTemplateId,
        string MetricKey,
        string RowKey,
        string ColumnKey,
        string SourceKey,
        decimal Value);

    private sealed record MetricContract(
        int Index,
        string RowKey,
        string ColumnKey,
        string MetricKey);

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
        string PeriodInstanceKey,
        int ReportStatus);

    private sealed class AggregateBucket
    {
        public WorkReportTableStatAggregate Row { get; set; } = default!;
        public HashSet<string> ReportIds { get; } = new(StringComparer.Ordinal);

        public void Add(WorkReportTableStatValue value)
        {
            Row.ValueCount += 1;
            Row.Sum += value.Value;
            Row.Min = Row.Min.HasValue ? Math.Min(Row.Min.Value, value.Value) : value.Value;
            Row.Max = Row.Max.HasValue ? Math.Max(Row.Max.Value, value.Value) : value.Value;
            ReportIds.Add(value.WorkAssignmentReportId);
        }
    }
}
