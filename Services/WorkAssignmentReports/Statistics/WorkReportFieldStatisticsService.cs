using System.Globalization;
using System.Text;
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

public sealed class WorkReportFieldStatisticsService : IWorkReportFieldStatisticsService
{
    private const int ShortTextBucketMaxLength = 200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly Regex FullDateRegex = new(@"^(\d{2})/(\d{2})/(\d{4})$", RegexOptions.Compiled);
    private static readonly Regex MonthDateRegex = new(@"^(\d{2})/(\d{4})$", RegexOptions.Compiled);
    private static readonly Regex YearDateRegex = new(@"^(\d{4})$", RegexOptions.Compiled);

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly IWorkReportPayloadReader _payloadReader;

    public WorkReportFieldStatisticsService(
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
            await _ctx.WorkReportFieldStatValues
                .DeleteManyAsync(x => x.WorkAssignmentReportId == reportId, ct);
            return null;
        }

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == report.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        var dynamicFormTemplateId = NormalizeObjectIdOrNull(report.DynamicFormTemplateId ?? assignment?.DynamicFormTemplateId);

        await _ctx.WorkReportFieldStatValues.DeleteManyAsync(
            x => x.WorkAssignmentReportId == report.Id,
            ct);

        if (report.IsActive == false || report.Status != WorkAssignmentReportStatus.Approved)
        {
            return new ReportStatisticAggregateKey(
                report.WorkId,
                report.PeriodInstanceKey,
                dynamicFormTemplateId);
        }

        var contributionPolicy = WorkReportCumulativeContributionPolicy.FromReport(report);
        if (!contributionPolicy.IncludesReport)
        {
            return new ReportStatisticAggregateKey(
                report.WorkId,
                report.PeriodInstanceKey,
                dynamicFormTemplateId);
        }

        var template = await LoadTemplateAsync(report, assignment, ct);
        var fields = ExtractStatisticFields(template?.FieldsJson);
        WorkReportPayloadConsistency.EnsureReadyForStatisticProjection(report);
        var payload = await _payloadReader.LoadReportPayloadAsync(report, ct);
        WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, payload);
        var values = ExtractFieldValues(payload.FieldValuesJson, fields)
            .Where(value => contributionPolicy.ShouldIncludeField(value.Field.FieldKey))
            .ToList();
        dynamicFormTemplateId = NormalizeObjectIdOrNull(
            report.DynamicFormTemplateId ?? assignment?.DynamicFormTemplateId ?? template?.Id);

        if (values.Count > 0)
        {
            var now = DateTime.UtcNow;
            var actorId = NormalizeObjectIdOrNull(actorUserId);
            var ancestorAssignmentIds = ExtractAncestorAssignmentIds(assignment, report.WorkAssignmentId);
            var sourceWindow = WorkAssignmentReportTemporalPolicy.ResolveSourceWindow(report);
            var projectionContext = WorkReportStatisticProjectionContextBuilder.From(report, assignment, period, payload);

            var rows = values.Select(value => new WorkReportFieldStatValue
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
                DynamicFormTemplateId = dynamicFormTemplateId,
                DynamicFormTemplateCode = report.DynamicFormTemplateCode ?? template?.Code,
                DynamicFormTemplateName = report.DynamicFormTemplateName ?? template?.Name,
                FieldId = value.Field.FieldId,
                FieldKey = value.Field.FieldKey,
                FieldLabel = value.Field.FieldLabel,
                FieldType = value.Field.FieldType,
                ShowInTree = value.Field.ShowInTree,
                ShowInDetail = value.Field.ShowInDetail,
                BucketKey = value.BucketKey,
                BucketLabel = value.BucketLabel,
                SourceKey = value.SourceKey,
                ValueKind = value.ValueKind,
                NumericValue = value.NumericValue,
                BooleanValue = value.BooleanValue,
                DateValueUtc = value.DateValueUtc,
                PeriodKey = report.PeriodKey,
                PeriodInstanceKey = report.PeriodInstanceKey,
                PeriodKind = report.PeriodKind,
                PeriodAnchorDate = sourceWindow.PeriodAnchorDate,
                PeriodStartDate = sourceWindow.PeriodStartDate,
                PeriodEndDate = sourceWindow.PeriodEndDate,
                CompletedDate = sourceWindow.CompletedDate,
                IsHistoricalData = sourceWindow.IsHistoricalData,
                ReportStatus = (int)report.Status,
                SourcePayloadRevision = projectionContext.SourcePayloadRevision,
                SourcePayloadHash = projectionContext.SourcePayloadHash,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = actorId,
                UpdatedByUserId = actorId,
                IsDeleted = false
            }).ToList();

            if (rows.Count > 0)
                await _ctx.WorkReportFieldStatValues.InsertManyAsync(rows, cancellationToken: ct);
        }

        return new ReportStatisticAggregateKey(
            report.WorkId,
            report.PeriodInstanceKey,
            dynamicFormTemplateId);
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

        var values = await _ctx.WorkReportFieldStatValues
            .Find(valueFilter)
            .ToListAsync(ct);

        await _ctx.WorkReportFieldStatAggregates.DeleteManyAsync(aggregateFilter, ct);

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
                    value.FieldId,
                    value.BucketKey,
                    value.PeriodInstanceKey,
                    value.ReportStatus);

                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new AggregateBucket
                    {
                        Row = new WorkReportFieldStatAggregate
                        {
                            Id = ObjectId.GenerateNewId().ToString(),
                            WorkId = value.WorkId,
                            ScopeType = scope.ScopeType,
                            ScopeId = scope.ScopeId,
                            RootAssignmentId = value.RootAssignmentId,
                            DynamicFormTemplateId = value.DynamicFormTemplateId,
                            DynamicFormTemplateCode = value.DynamicFormTemplateCode,
                            DynamicFormTemplateName = value.DynamicFormTemplateName,
                            FieldId = value.FieldId,
                            FieldKey = value.FieldKey,
                            FieldLabel = value.FieldLabel,
                            FieldType = value.FieldType,
                            ShowInTree = value.ShowInTree,
                            ShowInDetail = value.ShowInDetail,
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
            await _ctx.WorkReportFieldStatAggregates.InsertManyAsync(aggregates, cancellationToken: ct);
    }

    public async Task<RebuildFieldStatisticResponse> RebuildForWorkPeriodAsync(
        RebuildFieldStatisticRequest req,
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

        return new RebuildFieldStatisticResponse
        {
            WorkId = normalized.WorkId,
            PeriodInstanceKey = normalized.PeriodInstanceKey,
            DynamicFormTemplateId = normalized.DynamicFormTemplateId,
            ReportCount = reports.Count
        };
    }

    public async Task<FieldStatisticSummaryResponse> SearchSummaryAsync(
        FieldStatisticSummaryRequest req,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        var normalized = NormalizeRequest(req);
        await EnsureCanReadScopeAsync(normalized, me.Id, ct);

        var filter = BuildSummaryFilter(normalized);
        var page = Math.Max(0, normalized.Page);
        var pageSize = Math.Clamp(normalized.PageSize <= 0 ? 50 : normalized.PageSize, 1, 200);

        var total = await _ctx.WorkReportFieldStatAggregates.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.WorkReportFieldStatAggregates
            .Find(filter)
            .SortByDescending(x => x.ValueCount)
            .ThenByDescending(x => x.Sum)
            .ThenBy(x => x.FieldKey)
            .ThenBy(x => x.BucketKey)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        var resultRows = rows.Select(x => new FieldStatisticSummaryRow
        {
            WorkId = x.WorkId,
            ScopeType = x.ScopeType,
            ScopeId = x.ScopeId,
            RootAssignmentId = x.RootAssignmentId,
            DynamicFormTemplateId = x.DynamicFormTemplateId,
            DynamicFormTemplateCode = x.DynamicFormTemplateCode,
            DynamicFormTemplateName = x.DynamicFormTemplateName,
            FieldId = x.FieldId,
            FieldKey = x.FieldKey,
            FieldLabel = x.FieldLabel,
            FieldType = x.FieldType,
            ShowInTree = x.ShowInTree,
            ShowInDetail = x.ShowInDetail,
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

        return new FieldStatisticSummaryResponse
        {
            Rows = resultRows,
            TotalRows = total,
            TotalValueCount = resultRows.Sum(x => x.ValueCount),
            TotalSum = resultRows.Sum(x => x.Sum ?? 0m),
            TotalReportCount = resultRows.Sum(x => x.ReportCount)
        };
    }

    public async Task<FieldTextConcatResponse> SearchTextConcatAsync(
        FieldTextConcatRequest req,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        var normalized = NormalizeTextConcatRequest(req);
        var data = await LoadTextConcatRowsAsync(normalized, me.Id, ct);
        var totalRows = data.Rows.Count;
        var pageRows = data.Rows
            .Skip(normalized.Page * normalized.PageSize)
            .Take(normalized.PageSize)
            .ToList();

        var (concatenatedText, totalChars, truncated) = BuildConcatenatedText(data.Rows, normalized.MaxChars);

        return new FieldTextConcatResponse
        {
            WorkId = normalized.WorkId,
            ScopeType = normalized.ScopeType!,
            ScopeId = normalized.ScopeId,
            DynamicFormTemplateId = normalized.DynamicFormTemplateId,
            FieldId = data.Field.FieldId,
            FieldKey = data.Field.FieldKey,
            FieldLabel = data.Field.FieldLabel,
            FieldType = data.Field.FieldType,
            ConcatenatedText = concatenatedText,
            Rows = pageRows,
            Page = normalized.Page,
            PageSize = normalized.PageSize,
            TotalRows = totalRows,
            ReturnedRows = pageRows.Count,
            TotalChars = totalChars,
            MaxChars = normalized.MaxChars,
            Truncated = truncated || data.Rows.Any(x => x.RowTruncated),
            MatchingReportCount = data.MatchingReportCount,
            ScannedReportCount = data.ScannedReportCount,
            ScanLimit = normalized.ScanLimit,
            HasMoreReportsThanScanLimit = data.MatchingReportCount > data.ScannedReportCount
        };
    }

    public async Task<FieldTextConcatExportFile> ExportTextConcatCsvAsync(
        FieldTextConcatRequest req,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        var normalized = NormalizeTextConcatRequest(req, forExport: true);
        var data = await LoadTextConcatRowsAsync(normalized, me.Id, ct);
        var csv = BuildTextConcatCsv(normalized, data);
        var preamble = Encoding.UTF8.GetPreamble();
        var payload = Encoding.UTF8.GetBytes(csv);
        var content = new byte[preamble.Length + payload.Length];
        Buffer.BlockCopy(preamble, 0, content, 0, preamble.Length);
        Buffer.BlockCopy(payload, 0, content, preamble.Length, payload.Length);

        return new FieldTextConcatExportFile
        {
            Content = content,
            ContentType = "text/csv; charset=utf-8",
            FileName = BuildTextConcatExportFileName(data.Field)
        };
    }

    private async Task<TextConcatQueryResult> LoadTextConcatRowsAsync(
        FieldTextConcatRequest normalized,
        string actorUserId,
        CancellationToken ct)
    {
        var template = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == normalized.DynamicFormTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportStatisticExceptions.DynamicFormTemplateNotFound(
                "FIELD_TEXT",
                normalized.WorkId,
                normalized.DynamicFormTemplateId);

        var field = ResolveTextConcatField(template.FieldsJson, normalized);
        var assignments = await LoadTextConcatScopeAssignmentsAsync(normalized, actorUserId, ct);
        if (assignments.Count == 0)
        {
            return new TextConcatQueryResult
            {
                Field = field
            };
        }

        var assignmentIds = assignments
            .Select(x => x.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var reportFilter = BuildTextConcatReportFilter(normalized, assignmentIds);
        var matchingReportCount = await _ctx.WorkAssignmentReports
            .CountDocumentsAsync(reportFilter, cancellationToken: ct);

        var reports = await _ctx.WorkAssignmentReports
            .Find(reportFilter)
            .SortByDescending(x => x.PeriodKey)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .Limit(normalized.ScanLimit)
            .ToListAsync(ct);

        var periodIds = reports
            .Select(x => x.WorkReportPeriodId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var periodsById = periodIds.Count == 0
            ? new Dictionary<string, WorkReportPeriod>(StringComparer.Ordinal)
            : (await _ctx.WorkReportPeriods
                    .Find(x => periodIds.Contains(x.Id) && !x.IsDeleted)
                    .ToListAsync(ct))
                .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);

        var assignmentsById = assignments.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
        var parsedRows = new List<FieldTextConcatRow>();
        foreach (var report in reports)
        {
            periodsById.TryGetValue(report.WorkReportPeriodId, out var period);
            assignmentsById.TryGetValue(report.WorkAssignmentId, out var assignment);
            var payload = await _payloadReader.LoadReportPayloadAsync(report, ct);
            var value = ExtractConcatFieldValue(payload.FieldValuesJson, field, normalized.BucketKey);
            if (value is null || string.IsNullOrWhiteSpace(value.Text))
                continue;

            if (!string.IsNullOrWhiteSpace(normalized.Q) &&
                !value.Text.Contains(normalized.Q, StringComparison.OrdinalIgnoreCase) &&
                !value.Items.Any(item =>
                    item.Value.Contains(normalized.Q, StringComparison.OrdinalIgnoreCase) ||
                    item.Label.Contains(normalized.Q, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            parsedRows.Add(MapTextConcatRow(report, period, assignment, value, normalized.MaxRowChars));
        }

        return new TextConcatQueryResult
        {
            Field = field,
            Rows = parsedRows,
            MatchingReportCount = matchingReportCount,
            ScannedReportCount = reports.Count
        };
    }

    private async Task<DynamicFormTemplate?> LoadTemplateAsync(
        WorkAssignmentReport report,
        WorkAssignment? assignment,
        CancellationToken ct)
    {
        var ids = new[]
            {
                NormalizeObjectIdOrNull(report.DynamicFormTemplateId),
                NormalizeObjectIdOrNull(assignment?.DynamicFormTemplateId)
            }
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var id in ids)
        {
            var template = await _ctx.DynamicFormTemplates
                .Find(x => x.Id == id && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);

            if (template is not null)
                return template;
        }

        return null;
    }

    private static FilterDefinition<WorkReportFieldStatValue> BuildValueFilter(
        string workId,
        string? periodInstanceKey,
        string? dynamicFormTemplateId)
    {
        var fb = Builders<WorkReportFieldStatValue>.Filter;
        var filter = fb.Eq(x => x.WorkId, workId.Trim()) & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(periodInstanceKey))
            filter &= fb.Eq(x => x.PeriodInstanceKey, periodInstanceKey.Trim());

        if (!string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId.Trim());

        return filter;
    }

    private static FilterDefinition<WorkReportFieldStatAggregate> BuildAggregateFilter(
        string workId,
        string? periodInstanceKey,
        string? dynamicFormTemplateId)
    {
        var fb = Builders<WorkReportFieldStatAggregate>.Filter;
        var filter = fb.Eq(x => x.WorkId, workId.Trim()) & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(periodInstanceKey))
            filter &= fb.Eq(x => x.PeriodInstanceKey, periodInstanceKey.Trim());

        if (!string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId.Trim());

        return filter;
    }

    private static FilterDefinition<WorkReportFieldStatAggregate> BuildSummaryFilter(
        FieldStatisticSummaryRequest req)
    {
        var fb = Builders<WorkReportFieldStatAggregate>.Filter;
        var filter = fb.Eq(x => x.WorkId, req.WorkId!.Trim()) & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(req.ScopeType))
            filter &= fb.Eq(x => x.ScopeType, req.ScopeType!.Trim().ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(req.ScopeId))
            filter &= fb.Eq(x => x.ScopeId, req.ScopeId!.Trim());

        if (!string.IsNullOrWhiteSpace(req.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, req.DynamicFormTemplateId!.Trim());

        if (!string.IsNullOrWhiteSpace(req.FieldId))
            filter &= fb.Eq(x => x.FieldId, req.FieldId!.Trim());

        if (!string.IsNullOrWhiteSpace(req.FieldKey))
            filter &= fb.Eq(x => x.FieldKey, req.FieldKey!.Trim());

        if (!string.IsNullOrWhiteSpace(req.FieldType))
            filter &= fb.Eq(x => x.FieldType, NormalizeFieldType(req.FieldType));

        if (!string.IsNullOrWhiteSpace(req.BucketKey))
            filter &= fb.Eq(x => x.BucketKey, req.BucketKey!.Trim());

        if (req.ShowInTree.HasValue)
            filter &= fb.Eq(x => x.ShowInTree, req.ShowInTree.Value);

        if (req.ShowInDetail.HasValue)
            filter &= fb.Eq(x => x.ShowInDetail, req.ShowInDetail.Value);

        if (!string.IsNullOrWhiteSpace(req.PeriodKey))
            filter &= fb.Eq(x => x.PeriodKey, req.PeriodKey!.Trim());

        if (!string.IsNullOrWhiteSpace(req.PeriodKeyFrom))
            filter &= fb.Gte(x => x.PeriodKey, req.PeriodKeyFrom!.Trim());

        if (!string.IsNullOrWhiteSpace(req.PeriodKeyTo))
            filter &= fb.Lte(x => x.PeriodKey, req.PeriodKeyTo!.Trim());

        if (!string.IsNullOrWhiteSpace(req.PeriodInstanceKey))
            filter &= fb.Eq(x => x.PeriodInstanceKey, req.PeriodInstanceKey!.Trim());

        if (req.ReportStatus.HasValue)
            filter &= fb.Eq(x => x.ReportStatus, req.ReportStatus.Value);

        return filter;
    }

    private static FilterDefinition<WorkAssignmentReport> BuildTextConcatReportFilter(
        FieldTextConcatRequest req,
        List<string> assignmentIds)
    {
        var fb = Builders<WorkAssignmentReport>.Filter;
        var filter = fb.Eq(x => x.WorkId, req.WorkId.Trim())
            & fb.Eq(x => x.IsDeleted, false)
            & fb.Eq(x => x.IsCurrent, true)
            & fb.Ne(x => x.IsActive, false)
            & fb.In(x => x.WorkAssignmentId, assignmentIds)
            & fb.Eq(x => x.DynamicFormTemplateId, req.DynamicFormTemplateId.Trim());

        if (!string.IsNullOrWhiteSpace(req.PeriodKey))
            filter &= fb.Eq(x => x.PeriodKey, req.PeriodKey.Trim());

        if (!string.IsNullOrWhiteSpace(req.PeriodKeyFrom))
            filter &= fb.Gte(x => x.PeriodKey, req.PeriodKeyFrom.Trim());

        if (!string.IsNullOrWhiteSpace(req.PeriodKeyTo))
            filter &= fb.Lte(x => x.PeriodKey, req.PeriodKeyTo.Trim());

        if (!string.IsNullOrWhiteSpace(req.PeriodInstanceKey))
            filter &= fb.Eq(x => x.PeriodInstanceKey, req.PeriodInstanceKey.Trim());

        if (req.ReportStatus.HasValue)
            filter &= fb.Eq(x => x.Status, (WorkAssignmentReportStatus)req.ReportStatus.Value);

        return filter;
    }

    private async Task<List<WorkAssignment>> LoadTextConcatScopeAssignmentsAsync(
        FieldTextConcatRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        var fb = Builders<WorkAssignment>.Filter;
        var baseFilter = fb.Eq(x => x.WorkId, req.WorkId)
            & fb.Eq(x => x.IsDeleted, false)
            & fb.Eq(x => x.IsActive, true);

        if (req.ScopeType is "ASSIGNMENT" or "ROOT")
        {
            if (string.IsNullOrWhiteSpace(req.ScopeId))
                throw ReportStatisticExceptions.ScopeIdRequired("FIELD_TEXT", req.WorkId, req.ScopeType, req.ScopeId);

            var node = await _ctx.WorkAssignments
                .Find(baseFilter & fb.Eq(x => x.Id, req.ScopeId))
                .FirstOrDefaultAsync(ct)
                ?? throw ReportStatisticExceptions.AssignmentNotFound("FIELD_TEXT", req.WorkId, req.ScopeType, req.ScopeId);

            if (!CanReadAssignment(node, actorUserId))
                throw ReportStatisticExceptions.ReadForbidden("FIELD_TEXT", req.WorkId, req.ScopeType, req.ScopeId, actorUserId);

            var scopedFilter = req.ScopeType == "ROOT"
                ? baseFilter & fb.Eq(x => x.RootAssignmentId, node.RootAssignmentId)
                : baseFilter & fb.Eq(x => x.RootAssignmentId, node.RootAssignmentId)
                    & fb.Regex(x => x.Path, new BsonRegularExpression($"^{Regex.Escape(node.Path)}(?:/|$)"));

            return await _ctx.WorkAssignments
                .Find(scopedFilter)
                .SortBy(x => x.Path)
                .ToListAsync(ct);
        }

        var visibleFilter = baseFilter & fb.Or(
            fb.Eq(x => x.CreatedByUserId, actorUserId),
            fb.AnyEq(x => x.LeaderWatcherUserIds, actorUserId));

        return await _ctx.WorkAssignments
            .Find(visibleFilter)
            .SortBy(x => x.Path)
            .ToListAsync(ct);
    }

    private static FieldTextConcatRequest NormalizeTextConcatRequest(FieldTextConcatRequest req, bool forExport = false)
    {
        req ??= new FieldTextConcatRequest();

        var workId = req.WorkId?.Trim();
        if (string.IsNullOrWhiteSpace(workId))
            throw ReportStatisticExceptions.WorkIdRequired("FIELD_TEXT", req.WorkId);

        var dynamicFormTemplateId = NormalizeOptionalId(req.DynamicFormTemplateId);
        if (string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            throw ReportStatisticExceptions.DynamicFormTemplateIdRequired(
                "FIELD_TEXT",
                workId,
                req.DynamicFormTemplateId);

        if (string.IsNullOrWhiteSpace(req.FieldId) && string.IsNullOrWhiteSpace(req.FieldKey))
            throw ReportStatisticExceptions.FieldSelectorRequired(
                "FIELD_TEXT",
                workId,
                dynamicFormTemplateId,
                req.FieldId,
                req.FieldKey);

        var scopeType = string.IsNullOrWhiteSpace(req.ScopeType)
            ? "WORK"
            : req.ScopeType.Trim().ToUpperInvariant();

        if (scopeType is not ("WORK" or "ROOT" or "ASSIGNMENT"))
            throw ReportStatisticExceptions.ScopeTypeInvalid("FIELD_TEXT", workId, req.ScopeType);

        return new FieldTextConcatRequest
        {
            WorkId = workId,
            ScopeType = scopeType,
            ScopeId = string.IsNullOrWhiteSpace(req.ScopeId)
                ? (scopeType == "WORK" ? workId : null)
                : req.ScopeId.Trim(),
            DynamicFormTemplateId = dynamicFormTemplateId,
            FieldId = string.IsNullOrWhiteSpace(req.FieldId) ? null : req.FieldId.Trim(),
            FieldKey = string.IsNullOrWhiteSpace(req.FieldKey) ? null : req.FieldKey.Trim(),
            BucketKey = string.IsNullOrWhiteSpace(req.BucketKey) ? null : req.BucketKey.Trim(),
            Q = string.IsNullOrWhiteSpace(req.Q) ? null : req.Q.Trim(),
            PeriodKey = string.IsNullOrWhiteSpace(req.PeriodKey) ? null : req.PeriodKey.Trim(),
            PeriodKeyFrom = string.IsNullOrWhiteSpace(req.PeriodKeyFrom) ? null : req.PeriodKeyFrom.Trim(),
            PeriodKeyTo = string.IsNullOrWhiteSpace(req.PeriodKeyTo) ? null : req.PeriodKeyTo.Trim(),
            PeriodInstanceKey = string.IsNullOrWhiteSpace(req.PeriodInstanceKey) ? null : req.PeriodInstanceKey.Trim(),
            ReportStatus = req.ReportStatus,
            Page = forExport ? 0 : Math.Max(0, req.Page),
            PageSize = forExport
                ? Math.Clamp(req.PageSize <= 0 ? 1000 : req.PageSize, 1, 10000)
                : Math.Clamp(req.PageSize <= 0 ? 50 : req.PageSize, 1, 200),
            MaxChars = forExport
                ? Math.Clamp(req.MaxChars <= 0 ? 500000 : req.MaxChars, 1000, 1000000)
                : Math.Clamp(req.MaxChars <= 0 ? 10000 : req.MaxChars, 1000, 50000),
            MaxRowChars = forExport
                ? Math.Clamp(req.MaxRowChars <= 0 ? 10000 : req.MaxRowChars, 200, 50000)
                : Math.Clamp(req.MaxRowChars <= 0 ? 2000 : req.MaxRowChars, 200, 10000),
            ScanLimit = forExport
                ? Math.Clamp(req.ScanLimit <= 0 ? 20000 : req.ScanLimit, 100, 50000)
                : Math.Clamp(req.ScanLimit <= 0 ? 1000 : req.ScanLimit, 100, 5000)
        };
    }

    private static StatisticFieldDefinition ResolveTextConcatField(
        string? fieldsJson,
        FieldTextConcatRequest req)
    {
        var fields = ExtractTextFields(fieldsJson);
        var field = fields.FirstOrDefault(x =>
            (!string.IsNullOrWhiteSpace(req.FieldId) &&
             string.Equals(x.FieldId, req.FieldId, StringComparison.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(req.FieldKey) &&
             string.Equals(x.FieldKey, req.FieldKey, StringComparison.Ordinal)));

        if (field is null)
            throw ReportStatisticExceptions.TextFieldNotFound(
                "FIELD_TEXT",
                req.WorkId,
                req.DynamicFormTemplateId,
                req.FieldId,
                req.FieldKey);

        return field;
    }

    private static List<StatisticFieldDefinition> ExtractTextFields(string? fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
            return new List<StatisticFieldDefinition>();

        try
        {
            var rawFields = JsonSerializer.Deserialize<List<DynamicFormFieldDefinition>>(fieldsJson, JsonOptions);
            if (rawFields is null || rawFields.Count == 0)
                return new List<StatisticFieldDefinition>();

            return rawFields
                .Select(ToStatisticFieldDefinition)
                .OfType<StatisticFieldDefinition>()
                .Where(x => x.FieldType is "shortText" or "stringList" or "longText" or "singleSelect" or "multiSelect")
                .GroupBy(x => x.FieldId, StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();
        }
        catch (JsonException)
        {
            return new List<StatisticFieldDefinition>();
        }
    }

    private static FieldConcatValue? ExtractConcatFieldValue(
        string? fieldValuesJson,
        StatisticFieldDefinition field,
        string? bucketKey)
    {
        if (string.IsNullOrWhiteSpace(fieldValuesJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(fieldValuesJson);
            if (!TryGetValuesObject(doc.RootElement, out var valuesObject))
                return null;

            if (!TryGetFieldValue(valuesObject, field, out var value))
                return null;

            if (field.FieldType == "shortText")
            {
                var code = NormalizeShortTextBucket(ToNullableString(value));
                if (string.IsNullOrWhiteSpace(code))
                    return null;

                if (!string.IsNullOrWhiteSpace(bucketKey) &&
                    !string.Equals(code, bucketKey, StringComparison.Ordinal))
                {
                    return null;
                }

                var label = ResolveOptionLabel(field, code);
                return new FieldConcatValue(
                    label,
                    new List<FieldTextConcatItem>
                    {
                        new() { Value = code, Label = label }
                    });
            }

            if (field.FieldType == "singleSelect")
            {
                var code = ToNullableString(value)?.Trim();
                if (string.IsNullOrWhiteSpace(code))
                    return null;

                var label = ResolveOptionLabel(field, code);
                if (!string.IsNullOrWhiteSpace(bucketKey) &&
                    !string.Equals(code, bucketKey, StringComparison.Ordinal))
                {
                    return null;
                }

                return new FieldConcatValue(
                    label,
                    new List<FieldTextConcatItem>
                    {
                        new() { Value = code, Label = label }
                    });
            }

            if (field.FieldType == "multiSelect")
            {
                var items = ToStringArray(value)
                    .Distinct(StringComparer.Ordinal)
                    .Select(code => new FieldTextConcatItem
                    {
                        Value = code,
                        Label = ResolveOptionLabel(field, code)
                    })
                    .ToList();

                if (!string.IsNullOrWhiteSpace(bucketKey))
                {
                    items = items
                        .Where(item => string.Equals(item.Value, bucketKey, StringComparison.Ordinal))
                        .ToList();
                }

                if (items.Count == 0)
                    return null;

                return new FieldConcatValue(
                    string.Join(Environment.NewLine, items.Select(x => x.Label)),
                    items);
            }

            if (field.FieldType is "stringList" or "longText")
            {
                var items = ToStringArray(value)
                    .Select((text, index) => new FieldTextConcatItem
                    {
                        Value = text,
                        Label = $"Ý {index + 1}: {text}"
                    })
                    .ToList();

                if (!string.IsNullOrWhiteSpace(bucketKey))
                {
                    items = items
                        .Where(item => string.Equals(item.Value, bucketKey, StringComparison.Ordinal))
                        .ToList();
                }

                if (items.Count == 0)
                    return null;

                return new FieldConcatValue(
                    string.Join(Environment.NewLine, items.Select(x => x.Value)),
                    items);
            }

            var text = ToNullableString(value);
            return string.IsNullOrWhiteSpace(text)
                ? null
                : new FieldConcatValue(text.Trim(), new List<FieldTextConcatItem>());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FieldTextConcatRow MapTextConcatRow(
        WorkAssignmentReport report,
        WorkReportPeriod? period,
        WorkAssignment? assignment,
        FieldConcatValue value,
        int maxRowChars)
    {
        var assigneeUserId = period?.AssigneeUserId ?? report.AssigneeUserId;
        var assignee = assignment?.Assignees?.FirstOrDefault(a =>
            string.Equals(a.UserId, assigneeUserId, StringComparison.Ordinal));
        var text = value.Text;
        var rowText = text.Length > maxRowChars ? text[..maxRowChars] : text;

        return new FieldTextConcatRow
        {
            WorkAssignmentReportId = report.Id,
            WorkReportPeriodId = report.WorkReportPeriodId,
            AssignmentId = report.WorkAssignmentId,
            AssignmentCode = assignment?.Code,
            AssignmentName = assignment?.Name
                ?? assignment?.DynamicFormTemplateName
                ?? assignment?.DynamicExcelName
                ?? report.DynamicFormTemplateName
                ?? report.DynamicExcelTemplateName,
            AssigneeUserId = assigneeUserId,
            AssigneeFullName = assignee?.FullName ?? assignee?.Username ?? assigneeUserId,
            AssigneeUsername = assignee?.Username,
            UnitId = period?.AssigneeUnitId ?? assignee?.UnitId,
            UnitLabel = PickUnitLabel(assignee?.UnitSymbol, assignee?.UnitShortName, assignee?.UnitName)
                ?? period?.AssigneeUnitId,
            PeriodKey = report.PeriodKey,
            PeriodInstanceKey = report.PeriodInstanceKey,
            PeriodKind = report.PeriodKind,
            ReportStatus = (int)report.Status,
            Text = rowText,
            CharCount = text.Length,
            RowTruncated = text.Length > maxRowChars,
            Items = value.Items,
            SubmittedAtUtc = report.SubmittedAtUtc ?? period?.LastSubmittedAtUtc,
            ApprovedAtUtc = report.ApprovedAtUtc ?? period?.LastReviewedAtUtc
        };
    }

    private static (string Text, int TotalChars, bool Truncated) BuildConcatenatedText(
        List<FieldTextConcatRow> rows,
        int maxChars)
    {
        var sourceChars = rows.Sum(x => x.CharCount);
        var builder = new StringBuilder(capacity: Math.Min(maxChars, Math.Max(0, sourceChars)));
        var truncated = false;

        foreach (var row in rows)
        {
            var header = $"[{row.PeriodKey}] {row.AssignmentCode ?? row.AssignmentId} - {row.AssigneeFullName ?? row.AssigneeUserId ?? "-"}";
            var segment = builder.Length == 0
                ? $"{header}{Environment.NewLine}{row.Text}"
                : $"{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}{header}{Environment.NewLine}{row.Text}";

            var remaining = maxChars - builder.Length;
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            if (segment.Length > remaining)
            {
                builder.Append(segment.AsSpan(0, remaining));
                truncated = true;
                break;
            }

            builder.Append(segment);
        }

        return (builder.ToString(), sourceChars, truncated);
    }

    private static string BuildTextConcatCsv(
        FieldTextConcatRequest req,
        TextConcatQueryResult data)
    {
        var builder = new StringBuilder();
        var headers = new[]
        {
            "fieldLabel",
            "fieldKey",
            "scopeType",
            "scopeId",
            "periodKey",
            "periodInstanceKey",
            "periodKind",
            "reportStatus",
            "assignmentId",
            "assignmentCode",
            "assignmentName",
            "assigneeUserId",
            "assigneeFullName",
            "assigneeUsername",
            "unitId",
            "unitLabel",
            "charCount",
            "rowTruncated",
            "submittedAtUtc",
            "approvedAtUtc",
            "items",
            "text"
        };
        builder.AppendLine(string.Join(",", headers.Select(EscapeCsv)));

        foreach (var row in data.Rows)
        {
            var values = new[]
            {
                data.Field.FieldLabel,
                data.Field.FieldKey,
                req.ScopeType ?? string.Empty,
                req.ScopeId ?? string.Empty,
                row.PeriodKey,
                row.PeriodInstanceKey,
                row.PeriodKind,
                row.ReportStatus.ToString(CultureInfo.InvariantCulture),
                row.AssignmentId,
                row.AssignmentCode ?? string.Empty,
                row.AssignmentName,
                row.AssigneeUserId ?? string.Empty,
                row.AssigneeFullName ?? string.Empty,
                row.AssigneeUsername ?? string.Empty,
                row.UnitId ?? string.Empty,
                row.UnitLabel ?? string.Empty,
                row.CharCount.ToString(CultureInfo.InvariantCulture),
                row.RowTruncated ? "true" : "false",
                row.SubmittedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                row.ApprovedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                string.Join(" | ", row.Items.Select(x => $"{x.Label} ({x.Value})")),
                row.Text
            };
            builder.AppendLine(string.Join(",", values.Select(EscapeCsv)));
        }

        return builder.ToString();
    }

    private static string EscapeCsv(string? value)
    {
        var text = value ?? string.Empty;
        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string BuildTextConcatExportFileName(StatisticFieldDefinition field)
    {
        var fieldKey = SanitizeFilePart(field.FieldKey);
        return $"text-concat-{fieldKey}-{DateTime.UtcNow:yyyyMMddHHmmss}.csv";
    }

    private static string SanitizeFilePart(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "field" : value.Trim();
        var sanitized = Regex.Replace(raw, "[^A-Za-z0-9_.-]+", "-").Trim('-', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "field" : sanitized;
    }

    private static FieldTextConcatResponse BuildEmptyTextConcatResponse(
        FieldTextConcatRequest req,
        StatisticFieldDefinition field)
    {
        return new FieldTextConcatResponse
        {
            WorkId = req.WorkId,
            ScopeType = req.ScopeType!,
            ScopeId = req.ScopeId,
            DynamicFormTemplateId = req.DynamicFormTemplateId,
            FieldId = field.FieldId,
            FieldKey = field.FieldKey,
            FieldLabel = field.FieldLabel,
            FieldType = field.FieldType,
            Page = req.Page,
            PageSize = req.PageSize,
            MaxChars = req.MaxChars,
            ScanLimit = req.ScanLimit
        };
    }

    private static string? PickUnitLabel(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private async Task EnsureCanReadScopeAsync(
        FieldStatisticSummaryRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.WorkId))
            throw ReportStatisticExceptions.WorkIdRequired("FIELD", req.WorkId);

        var scopeType = req.ScopeType?.Trim().ToUpperInvariant();
        if (scopeType is "ASSIGNMENT" or "ROOT")
        {
            if (string.IsNullOrWhiteSpace(req.ScopeId))
                throw ReportStatisticExceptions.ScopeIdRequired("FIELD", req.WorkId, scopeType, req.ScopeId);

            var assignment = await _ctx.WorkAssignments
                .Find(x => x.Id == req.ScopeId && x.WorkId == req.WorkId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw ReportStatisticExceptions.AssignmentNotFound("FIELD", req.WorkId, scopeType, req.ScopeId);

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

        throw ReportStatisticExceptions.ReadForbidden("FIELD", req.WorkId, scopeType, req.ScopeId, actorUserId);
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
            throw ReportStatisticExceptions.RebuildForbidden("FIELD", workId, actorUserId);
    }

    private static bool CanReadAssignment(WorkAssignment assignment, string actorUserId)
        => string.Equals(assignment.CreatedByUserId, actorUserId, StringComparison.Ordinal)
           || (assignment.LeaderWatcherUserIds?.Contains(actorUserId) ?? false);

    private static FieldStatisticSummaryRequest NormalizeRequest(FieldStatisticSummaryRequest req)
    {
        req ??= new FieldStatisticSummaryRequest();
        var workId = req.WorkId?.Trim();
        if (string.IsNullOrWhiteSpace(workId))
            throw ReportStatisticExceptions.WorkIdRequired("FIELD", req.WorkId);

        var scopeType = string.IsNullOrWhiteSpace(req.ScopeType)
            ? "WORK"
            : req.ScopeType.Trim().ToUpperInvariant();

        if (scopeType is not ("WORK" or "ROOT" or "ASSIGNMENT"))
            throw ReportStatisticExceptions.ScopeTypeInvalid("FIELD", workId, req.ScopeType);

        var scopeId = string.IsNullOrWhiteSpace(req.ScopeId)
            ? (scopeType == "WORK" ? workId : null)
            : req.ScopeId.Trim();

        return new FieldStatisticSummaryRequest
        {
            WorkId = workId,
            ScopeType = scopeType,
            ScopeId = scopeId,
            DynamicFormTemplateId = NormalizeOptionalId(req.DynamicFormTemplateId),
            FieldId = string.IsNullOrWhiteSpace(req.FieldId) ? null : req.FieldId.Trim(),
            FieldKey = string.IsNullOrWhiteSpace(req.FieldKey) ? null : req.FieldKey.Trim(),
            FieldType = string.IsNullOrWhiteSpace(req.FieldType) ? null : NormalizeFieldType(req.FieldType),
            BucketKey = string.IsNullOrWhiteSpace(req.BucketKey) ? null : req.BucketKey.Trim(),
            ShowInTree = req.ShowInTree,
            ShowInDetail = req.ShowInDetail,
            PeriodKey = string.IsNullOrWhiteSpace(req.PeriodKey) ? null : req.PeriodKey.Trim(),
            PeriodKeyFrom = string.IsNullOrWhiteSpace(req.PeriodKeyFrom) ? null : req.PeriodKeyFrom.Trim(),
            PeriodKeyTo = string.IsNullOrWhiteSpace(req.PeriodKeyTo) ? null : req.PeriodKeyTo.Trim(),
            PeriodInstanceKey = string.IsNullOrWhiteSpace(req.PeriodInstanceKey) ? null : req.PeriodInstanceKey.Trim(),
            ReportStatus = req.ReportStatus,
            Page = Math.Max(0, req.Page),
            PageSize = Math.Clamp(req.PageSize <= 0 ? 50 : req.PageSize, 1, 200)
        };
    }

    private static RebuildFieldStatisticRequest NormalizeRebuildRequest(RebuildFieldStatisticRequest req)
    {
        req ??= new RebuildFieldStatisticRequest();
        var workId = req.WorkId?.Trim();
        if (string.IsNullOrWhiteSpace(workId))
            throw ReportStatisticExceptions.WorkIdRequired("FIELD", req.WorkId);

        return new RebuildFieldStatisticRequest
        {
            WorkId = workId,
            PeriodInstanceKey = string.IsNullOrWhiteSpace(req.PeriodInstanceKey) ? null : req.PeriodInstanceKey.Trim(),
            DynamicFormTemplateId = NormalizeOptionalId(req.DynamicFormTemplateId)
        };
    }

    private static List<StatisticFieldDefinition> ExtractStatisticFields(string? fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
            return new List<StatisticFieldDefinition>();

        try
        {
            var rawFields = JsonSerializer.Deserialize<List<DynamicFormFieldDefinition>>(fieldsJson, JsonOptions);
            if (rawFields is null || rawFields.Count == 0)
                return new List<StatisticFieldDefinition>();

            return rawFields
                .Where(x => x.IsStatistic)
                .Select(ToStatisticFieldDefinition)
                .OfType<StatisticFieldDefinition>()
                .GroupBy(x => x.FieldId, StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();
        }
        catch (JsonException)
        {
            return new List<StatisticFieldDefinition>();
        }
    }

    private static StatisticFieldDefinition? ToStatisticFieldDefinition(DynamicFormFieldDefinition field)
    {
        var fieldId = field.Id?.Trim();
        if (string.IsNullOrWhiteSpace(fieldId))
            return null;

        var fieldType = NormalizeFieldType(field.Type);
        var fieldKey = string.IsNullOrWhiteSpace(field.Key) ? fieldId : field.Key.Trim();
        var label = ResolveFieldDisplayName(field) ?? fieldKey;

        var options = (field.Options ?? new List<DynamicFormFieldOption>())
            .Select(x => new FieldOption(
                string.IsNullOrWhiteSpace(x.Code) ? string.Empty : x.Code.Trim(),
                string.IsNullOrWhiteSpace(x.Label) ? x.Code?.Trim() ?? string.Empty : x.Label.Trim()))
            .Where(x => !string.IsNullOrWhiteSpace(x.Code))
            .GroupBy(x => x.Code, StringComparer.Ordinal)
            .Select(x => x.First())
            .ToList();

        return new StatisticFieldDefinition(
            fieldId,
            fieldKey,
            label,
            fieldType,
            field.Statistic?.ShowInTree ?? false,
            field.Statistic?.ShowInDetail ?? true,
            options);
    }

    private static string? ResolveFieldDisplayName(DynamicFormFieldDefinition field)
        => PickNonBlank(field.Name, field.DisplayName, field.Label);

    private static string? PickNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static List<ParsedFieldValue> ExtractFieldValues(
        string? fieldValuesJson,
        IReadOnlyList<StatisticFieldDefinition> fields)
    {
        if (string.IsNullOrWhiteSpace(fieldValuesJson) || fields.Count == 0)
            return new List<ParsedFieldValue>();

        try
        {
            using var doc = JsonDocument.Parse(fieldValuesJson);
            if (!TryGetValuesObject(doc.RootElement, out var valuesObject))
                return new List<ParsedFieldValue>();

            var rows = new List<ParsedFieldValue>();
            foreach (var field in fields)
            {
                if (!TryGetFieldValue(valuesObject, field, out var value))
                    continue;

                rows.AddRange(ExtractFieldValue(field, value));
            }

            return rows;
        }
        catch (JsonException)
        {
            return new List<ParsedFieldValue>();
        }
    }

    private static IEnumerable<ParsedFieldValue> ExtractFieldValue(
        StatisticFieldDefinition field,
        JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            yield break;

        if (field.FieldType == "number")
        {
            var number = ToNullableDecimal(value);
            if (!number.HasValue)
                yield break;

            yield return new ParsedFieldValue(
                field,
                null,
                null,
                field.FieldId,
                "NUMBER",
                number,
                null,
                null);
            yield break;
        }

        if (field.FieldType == "boolean")
        {
            var boolean = ToNullableBoolean(value);
            if (!boolean.HasValue)
                yield break;

            yield return new ParsedFieldValue(
                field,
                boolean.Value ? "true" : "false",
                boolean.Value ? "Có" : "Không",
                field.FieldId,
                "BOOLEAN",
                null,
                boolean,
                null);
            yield break;
        }

        if (field.FieldType == "date")
        {
            var date = ToNullableDateUtc(value);
            if (!date.HasValue)
                yield break;

            yield return new ParsedFieldValue(
                field,
                null,
                null,
                field.FieldId,
                "DATE",
                null,
                null,
                date);
            yield break;
        }

        if (field.FieldType == "singleSelect")
        {
            var code = ToNullableString(value);
            if (string.IsNullOrWhiteSpace(code))
                yield break;

            code = code.Trim();
            yield return new ParsedFieldValue(
                field,
                code,
                ResolveOptionLabel(field, code),
                field.FieldId,
                "OPTION",
                null,
                null,
                null);
            yield break;
        }

        if (field.FieldType == "multiSelect")
        {
            foreach (var code in ToStringArray(value).Distinct(StringComparer.Ordinal))
            {
                yield return new ParsedFieldValue(
                    field,
                    code,
                    ResolveOptionLabel(field, code),
                    $"{field.FieldId}:{code}",
                    "OPTION",
                    null,
                    null,
                    null);
            }

            yield break;
        }

        if (field.FieldType == "shortText")
        {
            var code = NormalizeShortTextBucket(ToNullableString(value));
            if (string.IsNullOrWhiteSpace(code))
                yield break;

            var label = ResolveOptionLabel(field, code);
            yield return new ParsedFieldValue(
                field,
                code,
                label,
                field.FieldId,
                "TEXT_BUCKET",
                null,
                null,
                null);
            yield break;
        }

        if (field.FieldType is "stringList" or "longText")
        {
            if (ToStringArray(value).Count == 0)
                yield break;

            yield return new ParsedFieldValue(
                field,
                null,
                null,
                field.FieldId,
                "PRESENT",
                null,
                null,
                null);
            yield break;
        }

        var text = ToNullableString(value);
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        yield return new ParsedFieldValue(
            field,
            null,
            null,
            field.FieldId,
            "PRESENT",
            null,
            null,
            null);
    }

    private static bool TryGetValuesObject(JsonElement root, out JsonElement valuesObject)
    {
        valuesObject = default;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (root.TryGetProperty("values", out var nestedValues) &&
            nestedValues.ValueKind == JsonValueKind.Object)
        {
            valuesObject = nestedValues;
            return true;
        }

        valuesObject = root;
        return true;
    }

    private static bool TryGetFieldValue(
        JsonElement valuesObject,
        StatisticFieldDefinition field,
        out JsonElement value)
    {
        if (valuesObject.TryGetProperty(field.FieldId, out value))
            return true;

        if (!string.Equals(field.FieldKey, field.FieldId, StringComparison.Ordinal) &&
            valuesObject.TryGetProperty(field.FieldKey, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static List<string> ToStringArray(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var single = value.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(single) ? new List<string>() : new List<string> { single };
        }

        if (value.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return value
            .EnumerateArray()
            .Select(ToNullableString)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .ToList();
    }

    private static string? ToNullableString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            _ => null
        };
    }

    private static string? NormalizeShortTextBucket(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return text.Length <= ShortTextBucketMaxLength
            ? text
            : text[..ShortTextBucketMaxLength];
    }

    private static decimal? ToNullableDecimal(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                return parsed;

            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed))
                return parsed;
        }

        return null;
    }

    private static bool? ToNullableBoolean(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();

        if (value.ValueKind == JsonValueKind.String &&
            bool.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static DateTime? ToNullableDateUtc(JsonElement value)
    {
        var raw = ToNullableString(value);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = raw.Trim();
        var full = FullDateRegex.Match(normalized);
        if (full.Success)
        {
            var day = int.Parse(full.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(full.Groups[2].Value, CultureInfo.InvariantCulture);
            var year = int.Parse(full.Groups[3].Value, CultureInfo.InvariantCulture);
            return IsValidDatePart(year, month, day)
                ? DateTime.SpecifyKind(new DateTime(year, month, day), DateTimeKind.Utc)
                : null;
        }

        var monthOnly = MonthDateRegex.Match(normalized);
        if (monthOnly.Success)
        {
            var month = int.Parse(monthOnly.Groups[1].Value, CultureInfo.InvariantCulture);
            var year = int.Parse(monthOnly.Groups[2].Value, CultureInfo.InvariantCulture);
            return IsValidYear(year) && month is >= 1 and <= 12
                ? DateTime.SpecifyKind(new DateTime(year, month, 1), DateTimeKind.Utc)
                : null;
        }

        var yearOnly = YearDateRegex.Match(normalized);
        if (yearOnly.Success)
        {
            var year = int.Parse(yearOnly.Groups[1].Value, CultureInfo.InvariantCulture);
            return IsValidYear(year)
                ? DateTime.SpecifyKind(new DateTime(year, 1, 1), DateTimeKind.Utc)
                : null;
        }

        if (!DateTime.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return null;
        }

        return parsed.Kind == DateTimeKind.Utc
            ? parsed
            : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    private static bool IsValidDatePart(int year, int month, int day)
        => IsValidYear(year) &&
           month is >= 1 and <= 12 &&
           day >= 1 &&
           day <= DateTime.DaysInMonth(year, month);

    private static bool IsValidYear(int year)
        => year is >= 1 and <= 9999;

    private static string ResolveOptionLabel(StatisticFieldDefinition field, string code)
        => field.Options.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.Ordinal))?.Label ?? code;

    private static IEnumerable<AggregateScope> ResolveScopes(WorkReportFieldStatValue value)
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

    private static string NormalizeFieldType(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "shortText" : value.Trim();
        return raw switch
        {
            "number" => "number",
            "date" => "date",
            "fullDate" => "date",
            "singleSelect" => "singleSelect",
            "multiSelect" => "multiSelect",
            "boolean" => "boolean",
            "stringList" => "stringList",
            "longText" => "stringList",
            _ => "shortText"
        };
    }

    private static string? NormalizeOptionalId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeObjectIdOrNull(string? value)
        => ObjectId.TryParse(value, out _) ? value : null;

    private sealed class DynamicFormFieldDefinition
    {
        public string? Id { get; set; }
        public string? Key { get; set; }
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public string? Label { get; set; }
        public string? Type { get; set; }
        public bool IsStatistic { get; set; }
        public DynamicFormStatisticDefinition? Statistic { get; set; }
        public List<DynamicFormFieldOption>? Options { get; set; }
    }

    private sealed class DynamicFormStatisticDefinition
    {
        public bool ShowInDetail { get; set; } = true;
        public bool ShowInTree { get; set; }
    }

    private sealed class DynamicFormFieldOption
    {
        public string? Code { get; set; }
        public string? Label { get; set; }
    }

    private sealed record FieldOption(string Code, string Label);

    private sealed record StatisticFieldDefinition(
        string FieldId,
        string FieldKey,
        string FieldLabel,
        string FieldType,
        bool ShowInTree,
        bool ShowInDetail,
        List<FieldOption> Options);

    private sealed record ParsedFieldValue(
        StatisticFieldDefinition Field,
        string? BucketKey,
        string? BucketLabel,
        string SourceKey,
        string ValueKind,
        decimal? NumericValue,
        bool? BooleanValue,
        DateTime? DateValueUtc);

    private sealed record FieldConcatValue(
        string Text,
        List<FieldTextConcatItem> Items);

    private sealed record AggregateScope(string ScopeType, string ScopeId);

    private sealed record AggregateKey(
        string WorkId,
        string ScopeType,
        string ScopeId,
        string? DynamicFormTemplateId,
        string FieldId,
        string? BucketKey,
        string PeriodInstanceKey,
        int ReportStatus);

    private sealed class TextConcatQueryResult
    {
        public StatisticFieldDefinition Field { get; set; } = default!;
        public List<FieldTextConcatRow> Rows { get; set; } = new();
        public long MatchingReportCount { get; set; }
        public int ScannedReportCount { get; set; }
    }

    private sealed class AggregateBucket
    {
        public WorkReportFieldStatAggregate Row { get; set; } = default!;
        public HashSet<string> ReportIds { get; } = new(StringComparer.Ordinal);

        public void Add(WorkReportFieldStatValue value)
        {
            Row.ValueCount += 1;

            if (value.NumericValue.HasValue)
            {
                Row.NumericValueCount += 1;
                Row.Sum += value.NumericValue.Value;
                Row.Min = Row.Min.HasValue ? Math.Min(Row.Min.Value, value.NumericValue.Value) : value.NumericValue.Value;
                Row.Max = Row.Max.HasValue ? Math.Max(Row.Max.Value, value.NumericValue.Value) : value.NumericValue.Value;
            }

            if (value.BooleanValue.HasValue)
            {
                if (value.BooleanValue.Value)
                    Row.TrueCount += 1;
                else
                    Row.FalseCount += 1;
            }

            if (value.DateValueUtc.HasValue)
            {
                Row.EarliestDateUtc = Row.EarliestDateUtc.HasValue
                    ? (Row.EarliestDateUtc.Value <= value.DateValueUtc.Value ? Row.EarliestDateUtc.Value : value.DateValueUtc.Value)
                    : value.DateValueUtc.Value;

                Row.LatestDateUtc = Row.LatestDateUtc.HasValue
                    ? (Row.LatestDateUtc.Value >= value.DateValueUtc.Value ? Row.LatestDateUtc.Value : value.DateValueUtc.Value)
                    : value.DateValueUtc.Value;
            }

            ReportIds.Add(value.WorkAssignmentReportId);
        }
    }
}
