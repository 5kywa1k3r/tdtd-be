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

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public sealed class WorkReportLabelStatisticsService : IWorkReportLabelStatisticsService
{
    private static readonly Regex LabelCodeRegex = new("^[a-z0-9][a-z0-9_.-]{0,63}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;

    public WorkReportLabelStatisticsService(MongoDbContext ctx, MeAccessor me)
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
            await _ctx.WorkReportLabelStatValues
                .DeleteManyAsync(x => x.WorkAssignmentReportId == reportId, ct);
            return null;
        }

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        await _ctx.WorkReportLabelStatValues.DeleteManyAsync(
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

        var now = DateTime.UtcNow;
        var rowLabels = ExtractRowLabels(report.TableValuesJson)
            .Select(row => row with
            {
                LabelCodes = row.LabelCodes
                    .Where(labelCode => contributionPolicy.ShouldIncludeLabel(
                        row.BlockId,
                        row.RowKey,
                        row.Source,
                        labelCode))
                    .ToList()
            })
            .Where(row => row.LabelCodes.Count > 0)
            .ToList();

        if (rowLabels.Count > 0)
        {
            var activeLabelCodes = await LoadActiveLabelCodesAsync(
                rowLabels.SelectMany(x => x.LabelCodes),
                ct);
            rowLabels = rowLabels
                .Select(row => row with
                {
                    LabelCodes = row.LabelCodes
                        .Where(activeLabelCodes.Contains)
                        .ToList()
                })
                .Where(row => row.LabelCodes.Count > 0)
                .ToList();
        }

        if (rowLabels.Count > 0)
        {
            var ancestorAssignmentIds = ExtractAncestorAssignmentIds(assignment, report.WorkAssignmentId);
            var sourceWindow = WorkAssignmentReportTemporalPolicy.ResolveSourceWindow(report);
            var values = rowLabels.SelectMany(row =>
                row.LabelCodes.Select(labelCode => new WorkReportLabelStatValue
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
                    DynamicExcelTemplateId = NormalizeObjectIdOrNull(row.DynamicExcelTemplateId ?? report.DynamicExcelTemplateId),
                    BlockId = row.BlockId,
                    PeriodKey = report.PeriodKey,
                    PeriodInstanceKey = report.PeriodInstanceKey,
                    PeriodKind = report.PeriodKind,
                    PeriodAnchorDate = sourceWindow.PeriodAnchorDate,
                    PeriodStartDate = sourceWindow.PeriodStartDate,
                    PeriodEndDate = sourceWindow.PeriodEndDate,
                    CompletedDate = sourceWindow.CompletedDate,
                    IsHistoricalData = sourceWindow.IsHistoricalData,
                    ReportStatus = (int)report.Status,
                    SheetId = row.SheetId,
                    RowKey = row.RowKey,
                    RowIndex = row.RowIndex,
                    LabelCode = labelCode,
                    Source = row.Source,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = NormalizeObjectIdOrNull(actorUserId),
                    UpdatedByUserId = NormalizeObjectIdOrNull(actorUserId),
                    IsDeleted = false
                }))
                .ToList();

            if (values.Count > 0)
                await _ctx.WorkReportLabelStatValues.InsertManyAsync(values, cancellationToken: ct);
        }

        return new ReportStatisticAggregateKey(
            report.WorkId,
            report.PeriodInstanceKey,
            report.DynamicFormTemplateId);
    }

    private async Task<HashSet<string>> LoadActiveLabelCodesAsync(
        IEnumerable<string> labelCodes,
        CancellationToken ct)
    {
        var codes = labelCodes
            .Select(NormalizeLabelCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var labels = await _ctx.Labels
            .Find(x => codes.Contains(x.Code) && x.IsActive && !x.IsDeleted)
            .Project(x => x.Code)
            .ToListAsync(ct);

        return labels.ToHashSet(StringComparer.OrdinalIgnoreCase);
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

        var values = await _ctx.WorkReportLabelStatValues
            .Find(valueFilter)
            .ToListAsync(ct);

        await _ctx.WorkReportLabelStatAggregates.DeleteManyAsync(aggregateFilter, ct);

        if (values.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var actorId = NormalizeObjectIdOrNull(actorUserId);
        var buckets = new Dictionary<AggregateKey, AggregateBucket>();

        foreach (var value in values)
        {
            foreach (var scope in ResolveScopes(value))
            {
                var key = new AggregateKey(
                    value.WorkId,
                    scope.ScopeType,
                    scope.ScopeId,
                    value.DynamicFormTemplateId,
                    value.DynamicExcelTemplateId,
                    value.BlockId,
                    value.LabelCode,
                    value.PeriodInstanceKey,
                    value.ReportStatus);

                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new AggregateBucket
                    {
                        Row = new WorkReportLabelStatAggregate
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
                            LabelCode = value.LabelCode,
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

                bucket.Row.RowCount += 1;
                bucket.ReportIds.Add(value.WorkAssignmentReportId);
            }
        }

        var aggregates = buckets.Values.Select(x =>
        {
            x.Row.ReportCount = x.ReportIds.Count;
            return x.Row;
        }).ToList();

        if (aggregates.Count > 0)
            await _ctx.WorkReportLabelStatAggregates.InsertManyAsync(aggregates, cancellationToken: ct);
    }

    public async Task<LabelStatisticSummaryResponse> SearchSummaryAsync(
        LabelStatisticSummaryRequest req,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        var normalized = NormalizeRequest(req);
        await EnsureCanReadScopeAsync(normalized, me.Id, ct);

        var filter = BuildSummaryFilter(normalized);
        var page = Math.Max(0, normalized.Page);
        var pageSize = Math.Clamp(normalized.PageSize <= 0 ? 50 : normalized.PageSize, 1, 200);

        var total = await _ctx.WorkReportLabelStatAggregates.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.WorkReportLabelStatAggregates
            .Find(filter)
            .SortByDescending(x => x.RowCount)
            .ThenBy(x => x.LabelCode)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        var labelCodes = rows
            .Select(x => x.LabelCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var labels = labelCodes.Count == 0
            ? new Dictionary<string, LabelCatalogItem>(StringComparer.OrdinalIgnoreCase)
            : (await _ctx.Labels
                .Find(x => labelCodes.Contains(x.Code) && x.IsActive && !x.IsDeleted)
                .ToListAsync(ct))
                .ToDictionary(x => x.Code, x => x, StringComparer.OrdinalIgnoreCase);

        var resultRows = rows.Select(x =>
        {
            labels.TryGetValue(x.LabelCode, out var label);
            return new LabelStatisticSummaryRow
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
                LabelCode = x.LabelCode,
                LabelName = label?.Name,
                LabelColor = label?.Color,
                LabelDataType = LabelDataTypes.Normalize(label?.DataType),
                PeriodKey = x.PeriodKey,
                PeriodInstanceKey = x.PeriodInstanceKey,
                PeriodKind = x.PeriodKind,
                ReportStatus = x.ReportStatus,
                RowCount = x.RowCount,
                ReportCount = x.ReportCount,
                UpdatedAtUtc = x.UpdatedAtUtc
            };
        }).ToList();

        return new LabelStatisticSummaryResponse
        {
            Rows = resultRows,
            TotalRows = total,
            TotalRowCount = resultRows.Sum(x => x.RowCount),
            TotalReportCount = resultRows.Sum(x => x.ReportCount)
        };
    }

    private static FilterDefinition<WorkReportLabelStatValue> BuildValueFilter(
        string workId,
        string? periodInstanceKey,
        string? dynamicFormTemplateId)
    {
        var fb = Builders<WorkReportLabelStatValue>.Filter;
        var filter = fb.Eq(x => x.WorkId, workId.Trim()) & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(periodInstanceKey))
            filter &= fb.Eq(x => x.PeriodInstanceKey, periodInstanceKey.Trim());

        if (!string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId.Trim());

        return filter;
    }

    private static FilterDefinition<WorkReportLabelStatAggregate> BuildAggregateFilter(
        string workId,
        string? periodInstanceKey,
        string? dynamicFormTemplateId)
    {
        var fb = Builders<WorkReportLabelStatAggregate>.Filter;
        var filter = fb.Eq(x => x.WorkId, workId.Trim()) & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(periodInstanceKey))
            filter &= fb.Eq(x => x.PeriodInstanceKey, periodInstanceKey.Trim());

        if (!string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId.Trim());

        return filter;
    }

    private static FilterDefinition<WorkReportLabelStatAggregate> BuildSummaryFilter(
        LabelStatisticSummaryRequest req)
    {
        var fb = Builders<WorkReportLabelStatAggregate>.Filter;
        var filter = fb.Eq(x => x.WorkId, req.WorkId!.Trim()) & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(req.ScopeType))
            filter &= fb.Eq(x => x.ScopeType, req.ScopeType!.Trim().ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(req.ScopeId))
            filter &= fb.Eq(x => x.ScopeId, req.ScopeId!.Trim());

        if (!string.IsNullOrWhiteSpace(req.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, req.DynamicFormTemplateId!.Trim());

        if (!string.IsNullOrWhiteSpace(req.DynamicExcelTemplateId))
            filter &= fb.Eq(x => x.DynamicExcelTemplateId, req.DynamicExcelTemplateId!.Trim());

        if (!string.IsNullOrWhiteSpace(req.LabelCode))
            filter &= fb.Eq(x => x.LabelCode, NormalizeLabelCode(req.LabelCode));

        if (!string.IsNullOrWhiteSpace(req.PeriodKey))
            filter &= fb.Eq(x => x.PeriodKey, req.PeriodKey!.Trim());

        if (!string.IsNullOrWhiteSpace(req.PeriodInstanceKey))
            filter &= fb.Eq(x => x.PeriodInstanceKey, req.PeriodInstanceKey!.Trim());

        if (req.ReportStatus.HasValue)
            filter &= fb.Eq(x => x.ReportStatus, req.ReportStatus.Value);

        return filter;
    }

    private async Task EnsureCanReadScopeAsync(
        LabelStatisticSummaryRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.WorkId))
            throw ReportStatisticExceptions.WorkIdRequired("LABEL", req.WorkId);

        var scopeType = req.ScopeType?.Trim().ToUpperInvariant();
        if (scopeType is "ASSIGNMENT" or "ROOT")
        {
            if (string.IsNullOrWhiteSpace(req.ScopeId))
                throw ReportStatisticExceptions.ScopeIdRequired("LABEL", req.WorkId, scopeType, req.ScopeId);

            var assignment = await _ctx.WorkAssignments
                .Find(x => x.Id == req.ScopeId && x.WorkId == req.WorkId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw ReportStatisticExceptions.AssignmentNotFound("LABEL", req.WorkId, scopeType, req.ScopeId);

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

        throw ReportStatisticExceptions.ReadForbidden("LABEL", req.WorkId, scopeType, req.ScopeId, actorUserId);
    }

    private static bool CanReadAssignment(WorkAssignment assignment, string actorUserId)
        => string.Equals(assignment.CreatedByUserId, actorUserId, StringComparison.Ordinal)
           || (assignment.LeaderWatcherUserIds?.Contains(actorUserId) ?? false);

    private static LabelStatisticSummaryRequest NormalizeRequest(LabelStatisticSummaryRequest req)
    {
        req ??= new LabelStatisticSummaryRequest();
        var workId = req.WorkId?.Trim();
        if (string.IsNullOrWhiteSpace(workId))
            throw ReportStatisticExceptions.WorkIdRequired("LABEL", req.WorkId);

        var scopeType = string.IsNullOrWhiteSpace(req.ScopeType)
            ? "WORK"
            : req.ScopeType.Trim().ToUpperInvariant();

        if (scopeType is not ("WORK" or "ROOT" or "ASSIGNMENT"))
            throw ReportStatisticExceptions.ScopeTypeInvalid("LABEL", workId, req.ScopeType);

        var scopeId = string.IsNullOrWhiteSpace(req.ScopeId)
            ? (scopeType == "WORK" ? workId : null)
            : req.ScopeId.Trim();

        return new LabelStatisticSummaryRequest
        {
            WorkId = workId,
            ScopeType = scopeType,
            ScopeId = scopeId,
            DynamicFormTemplateId = NormalizeOptionalId(req.DynamicFormTemplateId),
            DynamicExcelTemplateId = NormalizeOptionalId(req.DynamicExcelTemplateId),
            LabelCode = string.IsNullOrWhiteSpace(req.LabelCode) ? null : NormalizeLabelCode(req.LabelCode),
            PeriodKey = string.IsNullOrWhiteSpace(req.PeriodKey) ? null : req.PeriodKey.Trim(),
            PeriodInstanceKey = string.IsNullOrWhiteSpace(req.PeriodInstanceKey) ? null : req.PeriodInstanceKey.Trim(),
            ReportStatus = req.ReportStatus,
            Page = Math.Max(0, req.Page),
            PageSize = Math.Clamp(req.PageSize <= 0 ? 50 : req.PageSize, 1, 200)
        };
    }

    private static List<ParsedRowLabel> ExtractRowLabels(string? tableValuesJson)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson))
            return new List<ParsedRowLabel>();

        try
        {
            var root = JsonSerializer.Deserialize<TableValuesRoot>(tableValuesJson, JsonOptions);
            if (root?.Blocks is null || root.Blocks.Count == 0)
                return new List<ParsedRowLabel>();

            var result = new List<ParsedRowLabel>();
            foreach (var block in root.Blocks)
            {
                var blockId = string.IsNullOrWhiteSpace(block.BlockId)
                    ? "excel_block"
                    : block.BlockId.Trim();

                foreach (var row in block.RowLabels ?? new List<TableValuesRowLabel>())
                {
                    var rowIndex = NormalizeRowIndex(row.RowIndex, row.RowKey);
                    if (rowIndex < 0)
                        continue;

                    var labelCodes = NormalizeLabelCodes(row.RowLabelCodes);
                    if (labelCodes.Count == 0)
                        continue;

                    result.Add(new ParsedRowLabel(
                        blockId,
                        NormalizeOptionalId(block.DynamicExcelTemplateId),
                        string.IsNullOrWhiteSpace(row.SheetId) ? "sheet_1" : row.SheetId.Trim(),
                        string.IsNullOrWhiteSpace(row.RowKey) ? $"sheet_1:R{rowIndex + 1}" : row.RowKey.Trim(),
                        rowIndex,
                        labelCodes,
                        string.IsNullOrWhiteSpace(row.Source) ? "ROW_LABEL" : row.Source.Trim()));
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return new List<ParsedRowLabel>();
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

    private static IEnumerable<AggregateScope> ResolveScopes(WorkReportLabelStatValue value)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        yield return Add("WORK", value.WorkId);

        if (!string.IsNullOrWhiteSpace(value.RootAssignmentId))
            yield return Add("ROOT", value.RootAssignmentId);

        foreach (var assignmentId in value.AncestorAssignmentIds.Append(value.WorkAssignmentId))
        {
            if (string.IsNullOrWhiteSpace(assignmentId))
                continue;

            var scope = Add("ASSIGNMENT", assignmentId);
            if (!string.IsNullOrWhiteSpace(scope.ScopeId))
                yield return scope;
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

    private static int NormalizeRowIndex(int? rowIndex, string? rowKey)
    {
        if (rowIndex.HasValue && rowIndex.Value >= 0)
            return rowIndex.Value;

        if (string.IsNullOrWhiteSpace(rowKey))
            return -1;

        var match = Regex.Match(rowKey.Trim(), "R(\\d+)$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return -1;

        return int.TryParse(match.Groups[1].Value, out var oneBased) && oneBased > 0
            ? oneBased - 1
            : -1;
    }

    private static List<string> NormalizeLabelCodes(IEnumerable<string>? values)
        => values?
            .Select(NormalizeLabelCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList()
           ?? new List<string>();

    private static string NormalizeLabelCode(string? value)
    {
        var code = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return LabelCodeRegex.IsMatch(code) ? code : string.Empty;
    }

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
        public List<TableValuesRowLabel>? RowLabels { get; set; }
    }

    private sealed class TableValuesRowLabel
    {
        public string? SheetId { get; set; }
        public string? RowKey { get; set; }
        public int? RowIndex { get; set; }
        public List<string>? RowLabelCodes { get; set; }
        public string? Source { get; set; }
    }

    private sealed record ParsedRowLabel(
        string BlockId,
        string? DynamicExcelTemplateId,
        string SheetId,
        string RowKey,
        int RowIndex,
        List<string> LabelCodes,
        string Source);

    private sealed record AggregateScope(string ScopeType, string ScopeId);

    private sealed record AggregateKey(
        string WorkId,
        string ScopeType,
        string ScopeId,
        string? DynamicFormTemplateId,
        string? DynamicExcelTemplateId,
        string BlockId,
        string LabelCode,
        string PeriodInstanceKey,
        int ReportStatus);

    private sealed class AggregateBucket
    {
        public WorkReportLabelStatAggregate Row { get; set; } = default!;
        public HashSet<string> ReportIds { get; } = new(StringComparer.Ordinal);
    }
}
