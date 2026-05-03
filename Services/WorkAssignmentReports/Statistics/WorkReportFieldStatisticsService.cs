using System.Globalization;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public sealed class WorkReportFieldStatisticsService : IWorkReportFieldStatisticsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;

    public WorkReportFieldStatisticsService(MongoDbContext ctx, MeAccessor me)
    {
        _ctx = ctx;
        _me = me;
    }

    public async Task RebuildForReportAsync(
        string reportId,
        string? actorUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reportId))
            return;

        var report = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (report is null)
        {
            await _ctx.WorkReportFieldStatValues
                .DeleteManyAsync(x => x.WorkAssignmentReportId == reportId, ct);
            return;
        }

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        await _ctx.WorkReportFieldStatValues.DeleteManyAsync(
            x => x.WorkAssignmentReportId == report.Id,
            ct);

        var template = await LoadTemplateAsync(report, assignment, ct);
        var fields = ExtractStatisticFields(template?.FieldsJson);
        var values = ExtractFieldValues(report.FieldValuesJson, fields);
        var dynamicFormTemplateId = NormalizeObjectIdOrNull(
            report.DynamicFormTemplateId ?? assignment?.DynamicFormTemplateId ?? template?.Id);

        if (values.Count > 0)
        {
            var now = DateTime.UtcNow;
            var actorId = NormalizeObjectIdOrNull(actorUserId);
            var ancestorAssignmentIds = ExtractAncestorAssignmentIds(assignment, report.WorkAssignmentId);

            var rows = values.Select(value => new WorkReportFieldStatValue
            {
                Id = ObjectId.GenerateNewId().ToString(),
                WorkId = report.WorkId,
                WorkAssignmentId = report.WorkAssignmentId,
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
                ReportStatus = (int)report.Status,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = actorId,
                UpdatedByUserId = actorId,
                IsDeleted = false
            }).ToList();

            if (rows.Count > 0)
                await _ctx.WorkReportFieldStatValues.InsertManyAsync(rows, cancellationToken: ct);
        }

        await RebuildAggregatesForWorkPeriodAsync(
            report.WorkId,
            report.PeriodInstanceKey,
            dynamicFormTemplateId,
            actorUserId,
            ct);
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
                     & fb.Eq(x => x.IsCurrent, true);

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

    private async Task EnsureCanReadScopeAsync(
        FieldStatisticSummaryRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.WorkId))
            throw new InvalidOperationException("Thieu WorkId.");

        var scopeType = req.ScopeType?.Trim().ToUpperInvariant();
        if (scopeType is "ASSIGNMENT" or "ROOT")
        {
            if (string.IsNullOrWhiteSpace(req.ScopeId))
                throw new InvalidOperationException("Thieu ScopeId.");

            var assignment = await _ctx.WorkAssignments
                .Find(x => x.Id == req.ScopeId && x.WorkId == req.WorkId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Khong tim thay assignment thong ke.");

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

        throw new UnauthorizedAccessException("Ban khong co quyen xem thong ke field nay.");
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
            throw new UnauthorizedAccessException("Ban khong co quyen sua thong ke field nay.");
    }

    private static bool CanReadAssignment(WorkAssignment assignment, string actorUserId)
        => string.Equals(assignment.CreatedByUserId, actorUserId, StringComparison.Ordinal)
           || (assignment.LeaderWatcherUserIds?.Contains(actorUserId) ?? false);

    private static FieldStatisticSummaryRequest NormalizeRequest(FieldStatisticSummaryRequest req)
    {
        req ??= new FieldStatisticSummaryRequest();
        var workId = req.WorkId?.Trim();
        if (string.IsNullOrWhiteSpace(workId))
            throw new InvalidOperationException("Thieu WorkId.");

        var scopeType = string.IsNullOrWhiteSpace(req.ScopeType)
            ? "WORK"
            : req.ScopeType.Trim().ToUpperInvariant();

        if (scopeType is not ("WORK" or "ROOT" or "ASSIGNMENT"))
            throw new InvalidOperationException("ScopeType khong hop le.");

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
            throw new InvalidOperationException("Thieu WorkId.");

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
        var label = string.IsNullOrWhiteSpace(field.Label) ? fieldKey : field.Label.Trim();

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
                boolean.Value ? "True" : "False",
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

        if (!DateTime.TryParse(
                raw,
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
            "singleSelect" => "singleSelect",
            "multiSelect" => "multiSelect",
            "boolean" => "boolean",
            "longText" => "longText",
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
                Row.LatestDateUtc = Row.LatestDateUtc.HasValue
                    ? (Row.LatestDateUtc.Value >= value.DateValueUtc.Value ? Row.LatestDateUtc.Value : value.DateValueUtc.Value)
                    : value.DateValueUtc.Value;
            }

            ReportIds.Add(value.WorkAssignmentReportId);
        }
    }
}
