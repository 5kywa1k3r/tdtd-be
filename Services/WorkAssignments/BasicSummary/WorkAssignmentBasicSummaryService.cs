using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.WorkAssignments.BasicSummary;

public sealed class WorkAssignmentBasicSummaryService : IWorkAssignmentBasicSummaryService
{
    private const int DefaultMaxTextChars = 12000;
    private const int MaxTextCharsLimit = 100000;
    private const int MaxSourcePageSize = 200;
    private const int MaxSourceReportsPerSummary = 1000;
    private const int SnapshotPayloadVersion = 8;
    private const string ScopeMode = "DIRECT_CHILDREN_OR_SELF";
    private const string FeatureVersion = "basic-summary-v8";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly MongoDbContext _ctx;
    private readonly IWorkReportPayloadReader _payloadReader;
    private readonly IUnitSelectionService _unitSelection;

    public WorkAssignmentBasicSummaryService(
        MongoDbContext ctx,
        IWorkReportPayloadReader payloadReader,
        IUnitSelectionService unitSelection)
    {
        _ctx = ctx;
        _payloadReader = payloadReader;
        _unitSelection = unitSelection;
    }

    public async Task<WorkAssignmentBasicSummaryConfigDto?> GetConfigAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);

        var scope = await LoadScopeAssignmentAsync(assignmentId?.Trim() ?? string.Empty, ct);
        if (!CanReadAssignment(scope, actorUserId))
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_READ_FORBIDDEN,
                new { assignmentId, actorUserId });
        }

        var templateId = await NormalizeExistingTemplateIdAsync(dynamicFormTemplateId, ct);
        var config = await _ctx.WorkAssignmentBasicSummaryConfigs
            .Find(x =>
                x.AssignmentId == scope.Id &&
                x.DynamicFormTemplateId == templateId &&
                x.IsActive &&
                !x.IsDeleted)
            .SortByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct);

        return config is null ? null : MapConfig(config);
    }

    public async Task<WorkAssignmentBasicSummaryConfigDto> SaveConfigAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        SaveWorkAssignmentBasicSummaryConfigRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);

        var scope = await LoadScopeAssignmentAsync(assignmentId?.Trim() ?? string.Empty, ct);
        if (!CanReadAssignment(scope, actorUserId))
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_READ_FORBIDDEN,
                new { assignmentId, actorUserId });
        }

        var templateId = await NormalizeExistingTemplateIdAsync(dynamicFormTemplateId, ct);
        var defaultMethods = NormalizeDefaultMethods(req?.DefaultMethods);
        var rules = NormalizeRules(req?.Rules);
        var now = DateTime.UtcNow;

        var existing = await _ctx.WorkAssignmentBasicSummaryConfigs
            .Find(x =>
                x.AssignmentId == scope.Id &&
                x.DynamicFormTemplateId == templateId &&
                x.IsActive &&
                !x.IsDeleted)
            .SortByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct);

        var entity = existing ?? new WorkAssignmentBasicSummaryConfig
        {
            Id = ObjectId.GenerateNewId().ToString(),
            WorkId = scope.WorkId,
            AssignmentId = scope.Id,
            DynamicFormTemplateId = templateId,
            CreatedAtUtc = now,
            CreatedByUserId = actorUserId,
            IsActive = true,
            IsDeleted = false
        };

        entity.DefaultMethodsJson = JsonSerializer.Serialize(ToDefaultMethodsDto(defaultMethods), JsonOptions);
        entity.RulesJson = JsonSerializer.Serialize(rules, JsonOptions);
        entity.VersionNo = existing is null ? 1 : existing.VersionNo + 1;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = actorUserId;

        if (existing is null)
            await _ctx.WorkAssignmentBasicSummaryConfigs.InsertOneAsync(entity, cancellationToken: ct);
        else
            await _ctx.WorkAssignmentBasicSummaryConfigs.ReplaceOneAsync(x => x.Id == entity.Id, entity, cancellationToken: ct);

        return MapConfig(entity);
    }

    public async Task<WorkAssignmentBasicSummaryResponse> GetSummaryAsync(
        WorkAssignmentBasicSummaryRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);

        var normalized = await NormalizeRequestAsync(req, ct);
        var scope = await LoadScopeAssignmentAsync(normalized.ScopeAssignmentId, ct);

        if (!CanReadAssignment(scope, actorUserId))
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_READ_FORBIDDEN,
                new { normalized.ScopeAssignmentId, actorUserId });
        }

        ValidateSummaryScope(scope, normalized);

        var dynamicFormTemplateId = normalized.DynamicFormTemplateId ?? scope.DynamicFormTemplateId;
        if (string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_DYNAMIC_FORM_TEMPLATE_ID_REQUIRED);

        dynamicFormTemplateId = dynamicFormTemplateId.Trim();
        var template = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == dynamicFormTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
                new { dynamicFormTemplateId });

        var sourceAssignments = await LoadSourceAssignmentsAsync(
            scope,
            dynamicFormTemplateId,
            normalized.SelectedUnitIds,
            ct);

        if (IsPeriodicAssignment(scope) && normalized.PeriodScopeMode == "PERIOD_RANGE")
        {
            var rangeResponse = await GetPeriodicRangeSummaryAsync(
                scope,
                template,
                normalized,
                sourceAssignments,
                actorUserId,
                ct);

            ApplySourceView(rangeResponse, normalized.SourceView, normalized.IncludeSourceRows);
            return rangeResponse;
        }

        var sourceReports = await LoadSourceReportsAsync(
            sourceAssignments.Select(x => x.Id).ToList(),
            dynamicFormTemplateId,
            normalized,
            ct);

        var response = await GetOrBuildSummarySnapshotAsync(
            scope,
            template,
            normalized,
            sourceAssignments,
            sourceReports,
            actorUserId,
            ct);

        ApplySourceView(response, normalized.SourceView, normalized.IncludeSourceRows);

        return response;
    }

    private async Task<WorkAssignmentBasicSummaryResponse> GetOrBuildSummarySnapshotAsync(
        WorkAssignment scope,
        DynamicFormTemplate template,
        NormalizedRequest normalized,
        List<WorkAssignment> sourceAssignments,
        List<WorkAssignmentReport> sourceReports,
        string actorUserId,
        CancellationToken ct)
    {
        var requestHash = BuildRequestHash(normalized, scope.Id, template.Id);
        var sourceSignatureHash = BuildSourceSignatureHash(sourceAssignments, sourceReports);
        var snapshot = await LoadSnapshotAsync(requestHash, ct);
        var snapshotDirty = snapshot is null ||
                            normalized.ForceRefresh ||
                            snapshot.SnapshotDirty ||
                            !string.Equals(snapshot.SourceSignatureHash, sourceSignatureHash, StringComparison.Ordinal) ||
                            sourceReports.Any(x => x.AggregateSnapshotDirty);

        if (snapshot is not null && snapshotDirty && !snapshot.SnapshotDirty)
        {
            await MarkSnapshotDirtyAsync(snapshot.Id, ct);
            snapshot.SnapshotDirty = true;
            snapshot.SnapshotDirtyAtUtc ??= DateTime.UtcNow;
        }

        if (snapshot is not null && !snapshotDirty)
        {
            var cached = DeserializeSnapshot(snapshot, sourceAssignments, sourceReports);
            PrepareMeta(
                cached,
                snapshot.Id,
                scope,
                template,
                normalized.SelectedUnitIds,
                normalized.PeriodScopeMode,
                normalized.PeriodKey,
                normalized.PeriodKeyFrom,
                normalized.PeriodKeyTo,
                sourceAssignments.Count,
                sourceReports.Count,
                fromSnapshot: true,
                snapshotDirty: false,
                snapshot.SourceSignatureHash,
                snapshot.SnapshotDirtyAtUtc,
                snapshot.SnapshotRefreshedAtUtc);

            return cached;
        }

        try
        {
            var snapshotId = snapshot?.Id ?? ObjectId.GenerateNewId().ToString();
            var response = await BuildSummaryAsync(
                snapshotId,
                scope,
                template,
                normalized,
                sourceAssignments,
                sourceReports,
                sourceSignatureHash,
                ct);

            await SaveSnapshotAsync(
                snapshotId,
                scope,
                template.Id,
                requestHash,
                normalized,
                sourceAssignments,
                sourceReports,
                sourceSignatureHash,
                response,
                actorUserId,
                snapshot is null,
                ct);

            return response;
        }
        catch (Exception ex) when (snapshot is not null)
        {
            await MarkSnapshotRefreshErrorAsync(snapshot.Id, ex.Message, ct);
            throw;
        }
    }

    private async Task<WorkAssignmentBasicSummaryResponse> GetPeriodicRangeSummaryAsync(
        WorkAssignment scope,
        DynamicFormTemplate template,
        NormalizedRequest normalized,
        List<WorkAssignment> sourceAssignments,
        string actorUserId,
        CancellationToken ct)
    {
        var sourceAssignmentIds = sourceAssignments.Select(x => x.Id).ToList();
        var periodKeys = await LoadPeriodKeysAsync(sourceAssignmentIds, template.Id, normalized, ct);
        if (periodKeys.Count == 0)
        {
            var sourceSignatureHash = BuildSourceSignatureHash(sourceAssignments, Array.Empty<WorkAssignmentReport>());
            var emptyResponse = await BuildSummaryAsync(
                BuildCompositeSnapshotId(scope.Id, template.Id, normalized, Array.Empty<WorkAssignmentBasicSummaryResponse>()),
                scope,
                template,
                normalized,
                sourceAssignments,
                new List<WorkAssignmentReport>(),
                sourceSignatureHash,
                ct);
            emptyResponse.Warnings.Add("No materialized report periods were found in the selected range.");
            return emptyResponse;
        }

        var periodResponses = new List<WorkAssignmentBasicSummaryResponse>(periodKeys.Count);
        foreach (var periodKey in periodKeys)
        {
            var periodRequest = normalized with
            {
                PeriodScopeMode = "SINGLE_PERIOD",
                PeriodKey = periodKey,
                PeriodKeyFrom = null,
                PeriodKeyTo = null,
                IncludeSourceRows = true
            };

            var periodReports = await LoadSourceReportsAsync(
                sourceAssignmentIds,
                template.Id,
                periodRequest,
                ct);

            var periodResponse = await GetOrBuildSummarySnapshotAsync(
                scope,
                template,
                periodRequest,
                sourceAssignments,
                periodReports,
                actorUserId,
                ct);

            periodResponses.Add(periodResponse);
        }

        return MergePeriodicSnapshotSummaries(
            BuildCompositeSnapshotId(scope.Id, template.Id, normalized, periodResponses),
            scope,
            template,
            normalized,
            sourceAssignments,
            periodResponses);
    }

    private async Task<List<string>> LoadPeriodKeysAsync(
        List<string> sourceAssignmentIds,
        string dynamicFormTemplateId,
        NormalizedRequest req,
        CancellationToken ct)
    {
        if (sourceAssignmentIds.Count == 0)
            return new List<string>();

        var fb = Builders<WorkReportPeriod>.Filter;
        var filter = fb.In(x => x.WorkAssignmentId, sourceAssignmentIds)
                     & fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId)
                     & fb.Eq(x => x.PeriodKind, WorkReportPeriodKind.Scheduled)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Ne(x => x.IsActive, false);

        filter = AddWorkReportPeriodScopeFilter(
            filter,
            req.PeriodScopeMode,
            req.PeriodKey,
            req.PeriodKeyFrom,
            req.PeriodKeyTo);

        var periodKeys = await _ctx.WorkReportPeriods
            .Find(filter)
            .Project(x => x.PeriodKey)
            .ToListAsync(ct);

        if (periodKeys.Count == 0)
            periodKeys = await LoadReportPeriodKeysAsync(sourceAssignmentIds, dynamicFormTemplateId, req, ct);

        return periodKeys
            .Select(NormalizeDayKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<List<string>> LoadReportPeriodKeysAsync(
        List<string> sourceAssignmentIds,
        string dynamicFormTemplateId,
        NormalizedRequest req,
        CancellationToken ct)
    {
        var fb = Builders<WorkAssignmentReport>.Filter;
        var scheduledFilter = fb.Or(
            fb.Eq(x => x.PeriodKind, null),
            fb.Eq(x => x.PeriodKind, WorkReportPeriodKind.Scheduled));

        var filter = fb.In(x => x.WorkAssignmentId, sourceAssignmentIds)
                     & fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId)
                     & scheduledFilter
                     & fb.Eq(x => x.Status, WorkAssignmentReportStatus.Approved)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(x => x.IsCurrent, true)
                     & fb.Ne(x => x.IsActive, false)
                     & fb.Ne(x => x.CumulativeContributionMode, WorkReportCumulativeContributionMode.Exclude);

        filter = AddReportPeriodScopeFilter(
            filter,
            req.PeriodScopeMode,
            req.PeriodKey,
            req.PeriodKeyFrom,
            req.PeriodKeyTo);

        return await _ctx.WorkAssignmentReports
            .Find(filter)
            .Project(x => x.PeriodKey)
            .ToListAsync(ct);
    }

    private static FilterDefinition<WorkReportPeriod> AddWorkReportPeriodScopeFilter(
        FilterDefinition<WorkReportPeriod> filter,
        string? periodScopeMode,
        string? periodKey,
        string? periodKeyFrom,
        string? periodKeyTo)
    {
        var mode = NormalizePeriodScopeMode(periodScopeMode);
        if (mode == "ALL_PERIODS")
            return filter;

        var fb = Builders<WorkReportPeriod>.Filter;

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

        return filter;
    }

    private static WorkAssignmentBasicSummaryResponse MergePeriodicSnapshotSummaries(
        string snapshotId,
        WorkAssignment scope,
        DynamicFormTemplate template,
        NormalizedRequest req,
        List<WorkAssignment> sourceAssignments,
        List<WorkAssignmentBasicSummaryResponse> periodResponses)
    {
        var sources = periodResponses
            .SelectMany(x => x.Sources ?? new List<WorkAssignmentBasicSummarySourceDto>())
            .ToList();

        var response = new WorkAssignmentBasicSummaryResponse
        {
            Fields = periodResponses
                .SelectMany(x => x.Fields ?? new List<WorkAssignmentBasicSummaryItemDto>())
                .Where(IsNumericSummaryItem)
                .GroupBy(x => x.TargetKey, StringComparer.Ordinal)
                .Select(MergeNumericSummaryItems)
                .OrderBy(x => x.FieldKey ?? x.TargetKey, StringComparer.Ordinal)
                .ToList(),
            Tables = periodResponses
                .SelectMany(x => x.Tables ?? new List<WorkAssignmentBasicSummaryItemDto>())
                .Where(IsNumericSummaryItem)
                .GroupBy(x => x.TargetKey, StringComparer.Ordinal)
                .Select(MergeNumericSummaryItems)
                .OrderBy(x => x.BlockId, StringComparer.Ordinal)
                .ThenBy(x => x.Index ?? int.MaxValue)
                .ThenBy(x => x.MetricKey, StringComparer.Ordinal)
                .ToList(),
            Sources = sources,
            Warnings = periodResponses
                .SelectMany(x => x.Warnings ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList()
        };

        response.SummaryValues = BuildSummaryValues(response.Fields, response.Tables);

        PrepareMeta(
            response,
            snapshotId,
            scope,
            template,
            req.SelectedUnitIds,
            req.PeriodScopeMode,
            req.PeriodKey,
            req.PeriodKeyFrom,
            req.PeriodKeyTo,
            sourceAssignments.Count,
            sources.Count,
            fromSnapshot: periodResponses.Count > 0 && periodResponses.All(x => x.Meta.FromSnapshot),
            snapshotDirty: periodResponses.Any(x => x.Meta.SnapshotDirty),
            BuildMergedSourceSignatureHash(periodResponses),
            periodResponses
                .Select(x => x.Meta.SnapshotDirtyAtUtc)
                .Where(x => x.HasValue)
                .OrderBy(x => x)
                .FirstOrDefault(),
            periodResponses
                .Select(x => x.Meta.SnapshotRefreshedAtUtc)
                .Where(x => x.HasValue)
                .OrderByDescending(x => x)
                .FirstOrDefault());

        return response;
    }

    private static bool IsNumericSummaryItem(WorkAssignmentBasicSummaryItemDto item)
        => string.Equals(item.DataType, "NUMBER", StringComparison.OrdinalIgnoreCase) &&
           IsNumericOperation(item.Operation);

    private static WorkAssignmentBasicSummaryItemDto MergeNumericSummaryItems(
        IGrouping<string, WorkAssignmentBasicSummaryItemDto> group)
    {
        var first = group.First();
        var valueCount = 0;
        var reportCount = 0;
        var sum = 0m;
        decimal? min = null;
        decimal? max = null;

        foreach (var item in group)
        {
            valueCount += item.ValueCount;
            reportCount += item.ReportCount;
            if (item.Sum.HasValue)
                sum += item.Sum.Value;
            if (item.Min.HasValue)
                min = min.HasValue ? Math.Min(min.Value, item.Min.Value) : item.Min.Value;
            if (item.Max.HasValue)
                max = max.HasValue ? Math.Max(max.Value, item.Max.Value) : item.Max.Value;
        }

        var mean = valueCount > 0 ? sum / valueCount : (decimal?)null;
        var operation = NormalizeNumericOperationOrDefault(first.Operation, "SUM");

        return new WorkAssignmentBasicSummaryItemDto
        {
            TargetKind = first.TargetKind,
            TargetKey = first.TargetKey,
            FieldId = first.FieldId,
            FieldKey = first.FieldKey,
            BlockId = first.BlockId,
            TableMode = first.TableMode,
            MetricKey = first.MetricKey,
            RowKey = first.RowKey,
            ColumnKey = first.ColumnKey,
            Index = first.Index,
            Label = first.Label,
            DataType = "NUMBER",
            Operation = operation,
            Value = ResolveNumericValue(operation, valueCount, sum, min, max, mean),
            ValueCount = valueCount,
            ReportCount = reportCount,
            Sum = valueCount > 0 ? sum : null,
            Min = min,
            Max = max,
            Mean = mean
        };
    }

    private static object? ResolveNumericValue(
        string operation,
        int valueCount,
        decimal sum,
        decimal? min,
        decimal? max,
        decimal? mean)
        => operation switch
        {
            "SUM" => valueCount > 0 ? sum : null,
            "MIN" => min,
            "MAX" => max,
            "MEAN" => mean,
            _ => valueCount
        };

    private static string BuildCompositeSnapshotId(
        string scopeAssignmentId,
        string dynamicFormTemplateId,
        NormalizedRequest req,
        IEnumerable<WorkAssignmentBasicSummaryResponse> periodResponses)
    {
        var hash = Sha256(JsonSerializer.Serialize(new
        {
            FeatureVersion,
            kind = "period-range-composite",
            scopeAssignmentId,
            dynamicFormTemplateId,
            req.PeriodKeyFrom,
            req.PeriodKeyTo,
            req.SelectedUnitIds,
            req.DefaultMethods,
            req.Rules,
            periodSnapshots = periodResponses
                .Select(x => new
                {
                    x.Meta.PeriodKey,
                    x.Meta.SnapshotId,
                    x.Meta.SourceSignatureHash,
                    x.Meta.SnapshotRefreshedAtUtc
                })
                .OrderBy(x => x.PeriodKey, StringComparer.Ordinal)
                .ToList()
        }, JsonOptions));

        return $"range:{hash[..24]}";
    }

    private static string BuildMergedSourceSignatureHash(IEnumerable<WorkAssignmentBasicSummaryResponse> periodResponses)
        => Sha256(JsonSerializer.Serialize(
            periodResponses
                .Select(x => new
                {
                    x.Meta.PeriodKey,
                    x.Meta.SnapshotId,
                    x.Meta.SourceSignatureHash,
                    x.Meta.SourceReportCount,
                    x.Meta.SnapshotRefreshedAtUtc
                })
                .OrderBy(x => x.PeriodKey, StringComparer.Ordinal)
                .ToList(),
            JsonOptions));

    private async Task<NormalizedRequest> NormalizeRequestAsync(
        WorkAssignmentBasicSummaryRequest req,
        CancellationToken ct)
    {
        req ??= new WorkAssignmentBasicSummaryRequest();

        var scopeAssignmentId = req.ScopeAssignmentId?.Trim();
        if (string.IsNullOrWhiteSpace(scopeAssignmentId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_SCOPE_ID_REQUIRED);

        var selectedUnitIds = NormalizeStringList(req.SelectedUnitIds);
        if (selectedUnitIds.Count > 0)
            selectedUnitIds = await _unitSelection.ExpandVirtualUnitIdsAsync(selectedUnitIds, ct);

        var defaultMethods = NormalizeDefaultMethods(req.DefaultMethods);

        var rules = NormalizeRules(req.Rules);

        var sourceView = NormalizeSourceView(req.SourceView);

        return new NormalizedRequest(
            scopeAssignmentId,
            string.IsNullOrWhiteSpace(req.DynamicFormTemplateId) ? null : req.DynamicFormTemplateId.Trim(),
            selectedUnitIds,
            defaultMethods,
            rules,
            sourceView,
            NormalizePeriodScopeMode(req.PeriodScopeMode),
            NormalizeDayKey(req.PeriodKey),
            NormalizeDayKey(req.PeriodKeyFrom),
            NormalizeDayKey(req.PeriodKeyTo),
            req.ForceRefresh,
            req.IncludeSourceRows,
            Math.Clamp(req.MaxTextChars <= 0 ? DefaultMaxTextChars : req.MaxTextChars, 1000, MaxTextCharsLimit));
    }

    private async Task<WorkAssignment> LoadScopeAssignmentAsync(string scopeAssignmentId, CancellationToken ct)
        => await _ctx.WorkAssignments
               .Find(x => x.Id == scopeAssignmentId && !x.IsDeleted)
               .FirstOrDefaultAsync(ct)
           ?? throw AppExceptionFactory.NotFound(
               AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_PARENT_NOT_FOUND,
               new { scopeAssignmentId });

    private async Task<List<WorkAssignment>> LoadSourceAssignmentsAsync(
        WorkAssignment scope,
        string dynamicFormTemplateId,
        List<string> selectedUnitIds,
        CancellationToken ct)
    {
        var fb = Builders<WorkAssignment>.Filter;
        var sourceAssignmentTypes = ResolveSourceAssignmentTypes(scope.AssignmentType);
        var filter = fb.Eq(x => x.WorkId, scope.WorkId)
                     & fb.Eq(x => x.ParentAssignmentId, scope.Id)
                     & fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId)
                     & fb.In(x => x.AssignmentType, sourceAssignmentTypes)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(x => x.IsActive, true);

        if (selectedUnitIds.Count > 0)
            filter &= fb.ElemMatch(x => x.Assignees, a => a.UnitId != null && selectedUnitIds.Contains(a.UnitId));

        var directChildren = await _ctx.WorkAssignments
            .Find(filter)
            .SortBy(x => x.Path)
            .ThenBy(x => x.Code)
            .ToListAsync(ct);

        if (directChildren.Count > 0)
            return directChildren;

        if (IsActiveAssignmentForTemplate(scope, dynamicFormTemplateId, sourceAssignmentTypes) &&
            AssignmentMatchesSelectedUnits(scope, selectedUnitIds))
        {
            return new List<WorkAssignment> { scope };
        }

        return directChildren;
    }

    private static bool IsActiveAssignmentForTemplate(
        WorkAssignment assignment,
        string dynamicFormTemplateId,
        string[] supportedAssignmentTypes)
        => assignment.IsActive &&
           !assignment.IsDeleted &&
           supportedAssignmentTypes.Contains(assignment.AssignmentType, StringComparer.OrdinalIgnoreCase) &&
           string.Equals(assignment.DynamicFormTemplateId?.Trim(), dynamicFormTemplateId, StringComparison.Ordinal);

    private static bool AssignmentMatchesSelectedUnits(
        WorkAssignment assignment,
        List<string> selectedUnitIds)
        => selectedUnitIds.Count == 0 ||
           assignment.Assignees.Any(a =>
               !string.IsNullOrWhiteSpace(a.UnitId) &&
               selectedUnitIds.Contains(a.UnitId));

    private static string[] ResolveSourceAssignmentTypes(string? scopeAssignmentType)
        => string.Equals(scopeAssignmentType, WorkAssignmentTypes.PeriodicReport, StringComparison.OrdinalIgnoreCase)
            ? new[] { WorkAssignmentTypes.PeriodicReport }
            : new[] { WorkAssignmentTypes.Once };

    private static bool IsPeriodicAssignment(WorkAssignment assignment)
        => string.Equals(assignment.AssignmentType, WorkAssignmentTypes.PeriodicReport, StringComparison.OrdinalIgnoreCase);

    private async Task<List<WorkAssignmentReport>> LoadSourceReportsAsync(
        List<string> sourceAssignmentIds,
        string dynamicFormTemplateId,
        NormalizedRequest req,
        CancellationToken ct)
    {
        if (sourceAssignmentIds.Count == 0)
            return new List<WorkAssignmentReport>();

        var fb = Builders<WorkAssignmentReport>.Filter;
        var scheduledFilter = fb.Or(
            fb.Eq(x => x.PeriodKind, null),
            fb.Eq(x => x.PeriodKind, WorkReportPeriodKind.Scheduled));

        var filter = fb.In(x => x.WorkAssignmentId, sourceAssignmentIds)
                     & fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId)
                     & scheduledFilter
                     & fb.Eq(x => x.Status, WorkAssignmentReportStatus.Approved)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(x => x.IsCurrent, true)
                     & fb.Ne(x => x.IsActive, false)
                     & fb.Ne(x => x.CumulativeContributionMode, WorkReportCumulativeContributionMode.Exclude);

        filter = AddReportPeriodScopeFilter(
            filter,
            req.PeriodScopeMode,
            req.PeriodKey,
            req.PeriodKeyFrom,
            req.PeriodKeyTo);

        var reports = await _ctx.WorkAssignmentReports
            .Find(filter)
            .SortBy(x => x.WorkAssignmentId)
            .ThenBy(x => x.AssigneeUserId)
            .ThenBy(x => x.PeriodKey)
            .Limit(MaxSourceReportsPerSummary + 1)
            .ToListAsync(ct);

        if (reports.Count > MaxSourceReportsPerSummary)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_PERIOD_SCOPE_INVALID,
                new
                {
                    req.PeriodScopeMode,
                    req.PeriodKey,
                    req.PeriodKeyFrom,
                    req.PeriodKeyTo,
                    maxSourceReports = MaxSourceReportsPerSummary,
                    reason = "BASIC_SUMMARY_SOURCE_REPORT_LIMIT_EXCEEDED"
                },
                $"Basic summary source report limit exceeded ({MaxSourceReportsPerSummary}). Please narrow the period window or unit filter.");
        }

        return reports;
    }

    private static FilterDefinition<WorkAssignmentReport> AddReportPeriodScopeFilter(
        FilterDefinition<WorkAssignmentReport> filter,
        string? periodScopeMode,
        string? periodKey,
        string? periodKeyFrom,
        string? periodKeyTo)
    {
        var mode = NormalizePeriodScopeMode(periodScopeMode);
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

        return filter;
    }

    private async Task<WorkAssignmentBasicSummaryResponse> BuildSummaryAsync(
        string snapshotId,
        WorkAssignment scope,
        DynamicFormTemplate template,
        NormalizedRequest req,
        List<WorkAssignment> sourceAssignments,
        List<WorkAssignmentReport> sourceReports,
        string sourceSignatureHash,
        CancellationToken ct)
    {
        var fields = ExtractFieldDefinitions(template.FieldsJson)
            .Where(x => x.FieldType == "number")
            .ToList();
        var fieldAccumulators = fields
            .Select(field =>
            {
                var targetKey = $"field:{field.FieldId}";
                return new SummaryAccumulator(
                    targetKind: "FIELD",
                    targetKey,
                    label: field.FieldLabel,
                    dataType: FieldTypeToSummaryDataType(field.FieldType),
                    operation: ResolveOperation(req.Rules, "FIELD", targetKey, field.FieldId, DefaultFieldOperation(field, req.DefaultMethods)),
                    maxTextChars: req.MaxTextChars)
                {
                    FieldId = field.FieldId,
                    FieldKey = field.FieldKey
                };
            })
            .ToDictionary(x => x.TargetKey, StringComparer.Ordinal);

        var tableAccumulators = new Dictionary<string, SummaryAccumulator>(StringComparer.Ordinal);
        var skippedDirectAggregateBlocks = new HashSet<string>(StringComparer.Ordinal);
        var assignmentById = sourceAssignments.ToDictionary(x => x.Id, StringComparer.Ordinal);

        foreach (var report in sourceReports)
        {
            var payload = await _payloadReader.LoadReportPayloadAsync(report, ct);

            foreach (var fieldValue in ExtractFieldValues(payload.FieldValuesJson, fields))
            {
                var targetKey = $"field:{fieldValue.Field.FieldId}";
                if (fieldAccumulators.TryGetValue(targetKey, out var accumulator))
                    AddParsedValue(accumulator, fieldValue, report.Id);
            }

            foreach (var tableValue in ExtractTableValues(payload.TableValuesJson, skippedDirectAggregateBlocks))
            {
                var targetKey = $"table:{tableValue.BlockId}:{tableValue.MetricKey}";
                if (!tableAccumulators.TryGetValue(targetKey, out var accumulator))
                {
                    accumulator = new SummaryAccumulator(
                        targetKind: "TABLE",
                        targetKey,
                        label: tableValue.MetricKey,
                        dataType: tableValue.DataType,
                        operation: ResolveTableOperation(req.Rules, tableValue, targetKey, DefaultTableOperation(tableValue, req.DefaultMethods)),
                        maxTextChars: req.MaxTextChars)
                    {
                        BlockId = tableValue.BlockId,
                        TableMode = tableValue.TableMode,
                        MetricKey = tableValue.MetricKey,
                        RowKey = tableValue.RowKey,
                        ColumnKey = tableValue.ColumnKey,
                        Index = tableValue.Index
                    };
                    tableAccumulators[targetKey] = accumulator;
                }

                AddParsedValue(accumulator, tableValue, report.Id);
            }
        }

        var response = new WorkAssignmentBasicSummaryResponse
        {
            Fields = fieldAccumulators.Values
                .OrderBy(x => x.FieldKey ?? x.TargetKey, StringComparer.Ordinal)
                .Select(x => x.ToDto())
                .ToList(),
            Tables = tableAccumulators.Values
                .OrderBy(x => x.BlockId, StringComparer.Ordinal)
                .ThenBy(x => x.Index ?? int.MaxValue)
                .ThenBy(x => x.MetricKey, StringComparer.Ordinal)
                .Select(x => x.ToDto())
                .ToList(),
            Sources = sourceReports.Select(report => MapSource(report, assignmentById)).ToList()
        };

        response.SummaryValues = BuildSummaryValues(response.Fields, response.Tables);

        response.Warnings.AddRange(BuildWarnings(scope, sourceAssignments, sourceReports));
        if (skippedDirectAggregateBlocks.Count > 0)
        {
            response.Warnings.Add(
                $"Some table blocks exceeded the direct basic-summary limit ({DynamicExcelRuntimePolicy.MaxDirectTableAggregateInputCells} input cells) and were skipped: {string.Join(", ", skippedDirectAggregateBlocks.OrderBy(x => x, StringComparer.Ordinal))}.");
        }
        PrepareMeta(
            response,
            snapshotId,
            scope,
            template,
            req.SelectedUnitIds,
            req.PeriodScopeMode,
            req.PeriodKey,
            req.PeriodKeyFrom,
            req.PeriodKeyTo,
            sourceAssignments.Count,
            sourceReports.Count,
            fromSnapshot: false,
            snapshotDirty: false,
            sourceSignatureHash,
            snapshotDirtyAtUtc: null,
            snapshotRefreshedAtUtc: DateTime.UtcNow);

        return response;
    }

    private static void AddParsedValue(
        SummaryAccumulator accumulator,
        ParsedValue value,
        string reportId)
    {
        if (value.NumericValue.HasValue)
            accumulator.AddNumber(value.NumericValue.Value, reportId);
    }

    private static WorkAssignmentBasicSummarySourceDto MapSource(
        WorkAssignmentReport report,
        IReadOnlyDictionary<string, WorkAssignment> assignmentById)
    {
        assignmentById.TryGetValue(report.WorkAssignmentId, out var assignment);
        var assignee = assignment?.Assignees.FirstOrDefault(x =>
            string.Equals(x.UserId, report.AssigneeUserId, StringComparison.Ordinal));

        return new WorkAssignmentBasicSummarySourceDto
        {
            WorkAssignmentId = report.WorkAssignmentId,
            WorkAssignmentReportId = report.Id,
            WorkReportPeriodId = report.WorkReportPeriodId,
            AssigneeUserId = report.AssigneeUserId,
            AssigneeUsername = assignee?.Username,
            AssigneeFullName = assignee?.FullName,
            UnitId = assignee?.UnitId,
            UnitSymbol = assignee?.UnitSymbol,
            UnitShortName = assignee?.UnitShortName,
            UnitName = assignee?.UnitName,
            PeriodKey = report.PeriodKey,
            PeriodInstanceKey = report.PeriodInstanceKey,
            PeriodKind = report.PeriodKind,
            ReportStatus = (int)report.Status,
            SubmittedAtUtc = report.SubmittedAtUtc,
            ApprovedAtUtc = report.ApprovedAtUtc,
            PayloadUpdatedAtUtc = report.PayloadUpdatedAtUtc,
            PayloadRevision = report.PayloadRevision,
            PayloadHash = report.PayloadHash
        };
    }

    private static List<string> BuildWarnings(
        WorkAssignment scope,
        List<WorkAssignment> sourceAssignments,
        List<WorkAssignmentReport> sourceReports)
    {
        var warnings = new List<string>();
        if (sourceAssignments.Count == 0)
            warnings.Add("No active source assignments were found for this dynamic form template.");

        var reportedAssignmentIds = sourceReports
            .Select(x => x.WorkAssignmentId)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var missingApproved = sourceAssignments.Count(x => !reportedAssignmentIds.Contains(x.Id));
        if (missingApproved > 0)
            warnings.Add($"{missingApproved} source assignments do not have an approved scheduled report yet.");

        return warnings;
    }

    private async Task<WorkAssignmentBasicSummarySnapshot?> LoadSnapshotAsync(
        string requestHash,
        CancellationToken ct)
        => await _ctx.WorkAssignmentBasicSummarySnapshots
            .Find(x => x.RequestHash == requestHash && !x.IsDeleted)
            .SortByDescending(x => x.SnapshotRefreshedAtUtc)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct);

    private static WorkAssignmentBasicSummaryResponse DeserializeSnapshot(
        WorkAssignmentBasicSummarySnapshot snapshot,
        List<WorkAssignment> sourceAssignments,
        List<WorkAssignmentReport> sourceReports)
    {
        var response = DeserializeCompactSnapshot(snapshot.SnapshotJson) ??
                       DeserializeLegacySnapshot(snapshot.SnapshotJson) ??
                       new WorkAssignmentBasicSummaryResponse();

        var assignmentById = sourceAssignments.ToDictionary(x => x.Id, StringComparer.Ordinal);
        response.Sources = sourceReports.Select(report => MapSource(report, assignmentById)).ToList();
        response.SummaryValues = BuildSummaryValues(response.Fields, response.Tables);
        response.SourcesPage ??= new WorkAssignmentBasicSummarySourcePageDto();
        response.Warnings ??= new List<string>();
        return response;
    }

    private static WorkAssignmentBasicSummaryResponse? DeserializeLegacySnapshot(string snapshotJson)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkAssignmentBasicSummaryResponse>(snapshotJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static WorkAssignmentBasicSummaryResponse? DeserializeCompactSnapshot(string snapshotJson)
    {
        BasicSummarySnapshotPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BasicSummarySnapshotPayload>(snapshotJson, JsonOptions);
            if (payload is null || payload.Version != SnapshotPayloadVersion)
                return null;
        }
        catch (JsonException)
        {
            return null;
        }

        return new WorkAssignmentBasicSummaryResponse
        {
            Meta = payload.Meta ?? new WorkAssignmentBasicSummaryMetaDto(),
            Fields = payload.Fields ?? new List<WorkAssignmentBasicSummaryItemDto>(),
            Tables = InflateCompactTableBlocks(payload.TableBlocks ?? new List<CompactTableBlockSnapshot>()),
            Warnings = payload.Warnings ?? new List<string>()
        };
    }

    private static string SerializeSnapshotJson(WorkAssignmentBasicSummaryResponse response)
        => JsonSerializer.Serialize(CreateSnapshotPayload(response), SnapshotJsonOptions);

    private async Task SaveSnapshotAsync(
        string snapshotId,
        WorkAssignment scope,
        string dynamicFormTemplateId,
        string requestHash,
        NormalizedRequest req,
        List<WorkAssignment> sourceAssignments,
        List<WorkAssignmentReport> sourceReports,
        string sourceSignatureHash,
        WorkAssignmentBasicSummaryResponse response,
        string actorUserId,
        bool isNew,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var requestJson = JsonSerializer.Serialize(new
        {
            FeatureVersion,
            scopeAssignmentId = scope.Id,
            dynamicFormTemplateId,
            periodScopeMode = req.PeriodScopeMode,
            periodKey = req.PeriodKey,
            periodKeyFrom = req.PeriodKeyFrom,
            periodKeyTo = req.PeriodKeyTo,
            selectedUnitIds = req.SelectedUnitIds,
            defaultMethods = req.DefaultMethods,
            rules = req.Rules,
            maxTextChars = req.MaxTextChars
        }, JsonOptions);

        var update = Builders<WorkAssignmentBasicSummarySnapshot>.Update
            .SetOnInsert(x => x.Id, snapshotId)
            .SetOnInsert(x => x.CreatedAtUtc, now)
            .SetOnInsert(x => x.CreatedByUserId, actorUserId)
            .Set(x => x.WorkId, scope.WorkId)
            .Set(x => x.ScopeAssignmentId, scope.Id)
            .Set(x => x.DynamicFormTemplateId, dynamicFormTemplateId)
            .Set(x => x.RequestHash, requestHash)
            .Set(x => x.RequestJson, requestJson)
            .Set(x => x.SourceAssignmentIds, sourceAssignments.Select(x => x.Id).ToList())
            .Set(x => x.SourceReportIds, sourceReports.Select(x => x.Id).ToList())
            .Set(x => x.SourceSignatureHash, sourceSignatureHash)
            .Set(x => x.SnapshotJson, SerializeSnapshotJson(response))
            .Set(x => x.SnapshotDirty, false)
            .Set(x => x.SnapshotDirtyAtUtc, (DateTime?)null)
            .Set(x => x.SnapshotRefreshedAtUtc, now)
            .Set(x => x.RefreshError, (string?)null)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId)
            .Set(x => x.IsDeleted, false);

        await _ctx.WorkAssignmentBasicSummarySnapshots.UpdateOneAsync(
            x => x.Id == snapshotId,
            update,
            new UpdateOptions { IsUpsert = isNew },
            ct);
    }

    private static BasicSummarySnapshotPayload CreateSnapshotPayload(
        WorkAssignmentBasicSummaryResponse response)
        => new()
        {
            Version = SnapshotPayloadVersion,
            Meta = response.Meta,
            Fields = response.Fields.Select(CloneSnapshotItem).ToList(),
            TableBlocks = BuildCompactTableBlocks(response.Tables),
            Warnings = response.Warnings
        };

    private static List<CompactTableBlockSnapshot> BuildCompactTableBlocks(
        List<WorkAssignmentBasicSummaryItemDto> tables)
        => tables
            .Where(x => !string.IsNullOrWhiteSpace(x.BlockId))
            .GroupBy(x => $"{x.BlockId!}\u001F{(string.IsNullOrWhiteSpace(x.TableMode) ? "FIXED_GRID" : x.TableMode!)}", StringComparer.Ordinal)
            .Select(group =>
            {
                var parts = group.Key.Split('\u001F');
                return BuildCompactTableBlock(parts[0], parts.Length > 1 ? parts[1] : "FIXED_GRID", group.ToList());
            })
            .ToList();

    private static CompactTableBlockSnapshot BuildCompactTableBlock(
        string blockId,
        string tableMode,
        List<WorkAssignmentBasicSummaryItemDto> items)
    {
        var indexedItems = items
            .Where(x => IsNumericSummaryItem(x) && x.Index.HasValue && x.Index.Value >= 0)
            .ToList();
        var canVectorize = indexedItems.Count == items.Count &&
                           indexedItems.Select(x => x.Index!.Value).Distinct().Count() == indexedItems.Count;

        if (!canVectorize || indexedItems.Count == 0)
        {
            return new CompactTableBlockSnapshot
            {
                BlockId = blockId,
                TableMode = tableMode,
                Items = items.Select(CloneSnapshotItem).ToList()
            };
        }

        var length = indexedItems.Max(x => x.Index!.Value) + 1;
        var width = InferTableBlockWidth(blockId, indexedItems);
        var defaultOperation = indexedItems
            .Select(x => NormalizeNumericOperationOrDefault(x.Operation, "SUM"))
            .GroupBy(x => x, StringComparer.Ordinal)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .First()
            .Key;

        var valueCounts = CreateDecimalVector(length);
        var reportCounts = CreateDecimalVector(length);
        var sums = CreateDecimalVector(length);
        var mins = CreateDecimalVector(length);
        var maxes = CreateDecimalVector(length);
        var overrides = new List<CompactTableMetricOverride>();

        foreach (var item in indexedItems)
        {
            var index = item.Index!.Value;
            valueCounts[index] = item.ValueCount > 0 ? item.ValueCount : null;
            reportCounts[index] = item.ReportCount > 0 ? item.ReportCount : null;
            sums[index] = item.Sum;
            mins[index] = item.Min;
            maxes[index] = item.Max;

            var defaults = BuildDefaultTableMetric(blockId, index, width);
            var operation = NormalizeNumericOperationOrDefault(item.Operation, defaultOperation);
            var metricDiffers =
                !string.Equals(item.MetricKey, defaults.MetricKey, StringComparison.Ordinal) ||
                !string.Equals(item.RowKey, defaults.RowKey, StringComparison.Ordinal) ||
                !string.Equals(item.ColumnKey, defaults.ColumnKey, StringComparison.Ordinal);
            var operationDiffers = !string.Equals(operation, defaultOperation, StringComparison.Ordinal);
            var labelDiffers = !string.IsNullOrWhiteSpace(item.Label) &&
                               !string.Equals(item.Label, item.MetricKey, StringComparison.Ordinal);

            if (!metricDiffers && !operationDiffers && !labelDiffers)
                continue;

            overrides.Add(new CompactTableMetricOverride
            {
                Index = index,
                MetricKey = metricDiffers ? item.MetricKey : null,
                RowKey = metricDiffers ? item.RowKey : null,
                ColumnKey = metricDiffers ? item.ColumnKey : null,
                Operation = operationDiffers ? operation : null,
                Label = labelDiffers ? item.Label : null
            });
        }

        return new CompactTableBlockSnapshot
        {
            BlockId = blockId,
            TableMode = tableMode,
            Width = width,
            Length = length,
            Operation = defaultOperation,
            ValueCounts = SerializeDecimalVector(valueCounts),
            ReportCounts = SerializeDecimalVector(reportCounts),
            Sums = SerializeDecimalVector(sums),
            Mins = SerializeDecimalVector(mins),
            Maxes = SerializeDecimalVector(maxes),
            Overrides = overrides.Count == 0 ? null : overrides
        };
    }

    private static List<WorkAssignmentBasicSummaryItemDto> InflateCompactTableBlocks(
        List<CompactTableBlockSnapshot> blocks)
    {
        var tables = new List<WorkAssignmentBasicSummaryItemDto>();
        foreach (var block in blocks)
        {
            if (block.Items is { Count: > 0 })
            {
                tables.AddRange(block.Items.Select(CloneSnapshotItem));
                continue;
            }

            var valueCounts = DeserializeDecimalVector(block.ValueCounts);
            var reportCounts = DeserializeDecimalVector(block.ReportCounts);
            var sums = DeserializeDecimalVector(block.Sums);
            var mins = DeserializeDecimalVector(block.Mins);
            var maxes = DeserializeDecimalVector(block.Maxes);
            var length = new[] { block.Length, valueCounts.Count, reportCounts.Count, sums.Count, mins.Count, maxes.Count }.Max();
            var width = Math.Max(1, block.Width);
            var defaultOperation = NormalizeNumericOperationOrDefault(block.Operation, "SUM");
            var overrides = (block.Overrides ?? new List<CompactTableMetricOverride>())
                .GroupBy(x => x.Index)
                .ToDictionary(x => x.Key, x => x.First());

            for (var index = 0; index < length; index++)
            {
                var valueCount = DecimalVectorIntAt(valueCounts, index);
                var reportCount = DecimalVectorIntAt(reportCounts, index);
                var sum = DecimalVectorAt(sums, index);
                var min = DecimalVectorAt(mins, index);
                var max = DecimalVectorAt(maxes, index);
                if (valueCount <= 0 && reportCount <= 0 && !sum.HasValue && !min.HasValue && !max.HasValue)
                    continue;

                var defaults = BuildDefaultTableMetric(block.BlockId, index, width);
                overrides.TryGetValue(index, out var metricOverride);
                var metricKey = NormalizeMetricPart(metricOverride?.MetricKey, defaults.MetricKey);
                var rowKey = NormalizeMetricPart(metricOverride?.RowKey, defaults.RowKey);
                var columnKey = NormalizeMetricPart(metricOverride?.ColumnKey, defaults.ColumnKey);
                var operation = NormalizeNumericOperationOrDefault(metricOverride?.Operation, defaultOperation);
                var mean = valueCount > 0 && sum.HasValue ? sum.Value / valueCount : (decimal?)null;

                tables.Add(new WorkAssignmentBasicSummaryItemDto
                {
                    TargetKind = "TABLE",
                    TargetKey = $"table:{block.BlockId}:{metricKey}",
                    BlockId = block.BlockId,
                    TableMode = string.IsNullOrWhiteSpace(block.TableMode) ? "FIXED_GRID" : block.TableMode,
                    MetricKey = metricKey,
                    RowKey = rowKey,
                    ColumnKey = columnKey,
                    Index = index,
                    Label = NormalizeMetricPart(metricOverride?.Label, metricKey),
                    DataType = "NUMBER",
                    Operation = operation,
                    Value = ResolveNumericValue(operation, valueCount, sum ?? 0m, min, max, mean),
                    ValueCount = Math.Max(0, valueCount),
                    ReportCount = Math.Max(0, reportCount),
                    Sum = valueCount > 0 ? sum : null,
                    Min = min,
                    Max = max,
                    Mean = mean
                });
            }
        }

        return tables
            .OrderBy(x => x.BlockId, StringComparer.Ordinal)
            .ThenBy(x => x.Index ?? int.MaxValue)
            .ThenBy(x => x.MetricKey, StringComparer.Ordinal)
            .ToList();
    }

    private static List<decimal?> CreateDecimalVector(int length)
        => Enumerable.Repeat<decimal?>(null, Math.Max(0, length)).ToList();

    private static JsonNode? SerializeDecimalVector(IReadOnlyList<decimal?> values)
    {
        if (!values.Any(x => x.HasValue))
            return null;

        return JsonNode.Parse(Values1DCompression.SerializeDecimals(values, SnapshotJsonOptions));
    }

    private static List<decimal?> DeserializeDecimalVector(JsonNode? node)
        => node is null
            ? new List<decimal?>()
            : Values1DCompression.DeserializeDecimals(node.ToJsonString(SnapshotJsonOptions), SnapshotJsonOptions);

    private static decimal? DecimalVectorAt(IReadOnlyList<decimal?> values, int index)
        => index >= 0 && index < values.Count ? values[index] : null;

    private static int DecimalVectorIntAt(IReadOnlyList<decimal?> values, int index)
        => DecimalVectorAt(values, index) is { } value ? Math.Max(0, decimal.ToInt32(decimal.Truncate(value))) : 0;

    private static int InferTableBlockWidth(
        string blockId,
        IReadOnlyList<WorkAssignmentBasicSummaryItemDto> items)
    {
        var candidates = new List<int>();
        var maxColumn = 0;
        foreach (var item in items)
        {
            if (!item.Index.HasValue)
                continue;

            var rowIndex = ParseOrdinalIndex(item.RowKey ?? string.Empty, "row_");
            var columnIndex = ParseOrdinalIndex(item.ColumnKey ?? string.Empty, "col_");
            if (columnIndex.HasValue)
                maxColumn = Math.Max(maxColumn, columnIndex.Value + 1);

            if (!rowIndex.HasValue || !columnIndex.HasValue || rowIndex.Value <= 0)
                continue;

            var numerator = item.Index.Value - columnIndex.Value;
            if (numerator <= 0 || numerator % rowIndex.Value != 0)
                continue;

            var width = numerator / rowIndex.Value;
            if (width > columnIndex.Value)
                candidates.Add(width);
        }

        if (candidates.Count > 0)
        {
            return candidates
                .GroupBy(x => x)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key)
                .First()
                .Key;
        }

        return Math.Max(1, maxColumn);
    }

    private static CompactTableMetricParts BuildDefaultTableMetric(string blockId, int index, int width)
    {
        var safeWidth = Math.Max(1, width);
        var rowKey = $"row_{(index / safeWidth) + 1}";
        var columnKey = $"col_{(index % safeWidth) + 1}";
        return new CompactTableMetricParts(rowKey, columnKey, BuildMetricKey(blockId, rowKey, columnKey));
    }

    private static WorkAssignmentBasicSummaryItemDto CloneSnapshotItem(WorkAssignmentBasicSummaryItemDto item)
        => new()
        {
            TargetKind = item.TargetKind,
            TargetKey = item.TargetKey,
            FieldId = item.FieldId,
            FieldKey = item.FieldKey,
            BlockId = item.BlockId,
            TableMode = item.TableMode,
            MetricKey = item.MetricKey,
            RowKey = item.RowKey,
            ColumnKey = item.ColumnKey,
            Index = item.Index,
            Label = item.Label,
            DataType = item.DataType,
            Operation = item.Operation,
            Value = item.Value,
            ValueCount = item.ValueCount,
            ReportCount = item.ReportCount,
            Sum = item.Sum,
            Min = item.Min,
            Max = item.Max,
            Mean = item.Mean,
            TrueCount = item.TrueCount == 0 ? null : item.TrueCount,
            FalseCount = item.FalseCount == 0 ? null : item.FalseCount,
            MinDateUtc = item.MinDateUtc,
            MaxDateUtc = item.MaxDateUtc,
            Text = item.Text,
            TextCharCount = item.TextCharCount == 0 ? null : item.TextCharCount,
            TextTruncated = item.TextTruncated,
            Buckets = item.Buckets is { Count: > 0 } ? item.Buckets : new List<WorkAssignmentBasicSummaryBucketDto>()
        };

    private async Task MarkSnapshotDirtyAsync(string snapshotId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await _ctx.WorkAssignmentBasicSummarySnapshots.UpdateOneAsync(
            x => x.Id == snapshotId,
            Builders<WorkAssignmentBasicSummarySnapshot>.Update
                .Set(x => x.SnapshotDirty, true)
                .Set(x => x.SnapshotDirtyAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now),
            cancellationToken: ct);
    }

    private async Task MarkSnapshotRefreshErrorAsync(string snapshotId, string error, CancellationToken ct)
    {
        await _ctx.WorkAssignmentBasicSummarySnapshots.UpdateOneAsync(
            x => x.Id == snapshotId,
            Builders<WorkAssignmentBasicSummarySnapshot>.Update
                .Set(x => x.RefreshError, error)
                .Set(x => x.UpdatedAtUtc, DateTime.UtcNow),
            cancellationToken: ct);
    }

    private static void PrepareMeta(
        WorkAssignmentBasicSummaryResponse response,
        string snapshotId,
        WorkAssignment scope,
        DynamicFormTemplate template,
        List<string> selectedUnitIds,
        string periodScopeMode,
        string? periodKey,
        string? periodKeyFrom,
        string? periodKeyTo,
        int sourceAssignmentCount,
        int sourceReportCount,
        bool fromSnapshot,
        bool snapshotDirty,
        string? sourceSignatureHash,
        DateTime? snapshotDirtyAtUtc,
        DateTime? snapshotRefreshedAtUtc)
    {
        response.Meta = new WorkAssignmentBasicSummaryMetaDto
        {
            SnapshotId = snapshotId,
            ScopeAssignmentId = scope.Id,
            ScopeMode = ScopeMode,
            AssignmentType = scope.AssignmentType,
            DynamicFormTemplateId = template.Id,
            DynamicFormTemplateCode = template.Code,
            DynamicFormTemplateName = template.Name,
            SelectedUnitIds = selectedUnitIds,
            PeriodScopeMode = periodScopeMode,
            PeriodKey = periodKey,
            PeriodKeyFrom = periodKeyFrom,
            PeriodKeyTo = periodKeyTo,
            SourceAssignmentCount = sourceAssignmentCount,
            SourceReportCount = sourceReportCount,
            FromSnapshot = fromSnapshot,
            SnapshotDirty = snapshotDirty,
            SnapshotDirtyAtUtc = snapshotDirtyAtUtc,
            SnapshotRefreshedAtUtc = snapshotRefreshedAtUtc,
            SourceSignatureHash = sourceSignatureHash
        };
    }

    private static void ApplySourceView(
        WorkAssignmentBasicSummaryResponse response,
        NormalizedSourceView sourceView,
        bool includeSourceRows)
    {
        var allRows = response.Sources ?? new List<WorkAssignmentBasicSummarySourceDto>();
        var filteredRows = FilterSources(allRows, sourceView).ToList();
        var pageRows = includeSourceRows
            ? filteredRows
                .Skip(sourceView.Page * sourceView.PageSize)
                .Take(sourceView.PageSize)
                .ToList()
            : new List<WorkAssignmentBasicSummarySourceDto>();

        response.Sources = pageRows;
        response.SourcesPage = new WorkAssignmentBasicSummarySourcePageDto
        {
            Rows = pageRows,
            TotalRows = filteredRows.Count,
            Page = sourceView.Page,
            PageSize = sourceView.PageSize
        };
    }

    private static IEnumerable<WorkAssignmentBasicSummarySourceDto> FilterSources(
        IEnumerable<WorkAssignmentBasicSummarySourceDto> rows,
        NormalizedSourceView sourceView)
    {
        var query = rows;

        if (!string.IsNullOrWhiteSpace(sourceView.UnitId))
        {
            query = query.Where(x =>
                string.Equals(x.UnitId, sourceView.UnitId, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(sourceView.AssigneeUserId))
        {
            query = query.Where(x =>
                string.Equals(x.AssigneeUserId, sourceView.AssigneeUserId, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(sourceView.PeriodKey))
        {
            query = query.Where(x =>
                ContainsIgnoreCase(x.PeriodKey, sourceView.PeriodKey) ||
                ContainsIgnoreCase(x.PeriodInstanceKey, sourceView.PeriodKey));
        }

        if (!string.IsNullOrWhiteSpace(sourceView.Q))
        {
            query = query.Where(x =>
                ContainsIgnoreCase(x.UnitSymbol, sourceView.Q) ||
                ContainsIgnoreCase(x.UnitShortName, sourceView.Q) ||
                ContainsIgnoreCase(x.UnitName, sourceView.Q) ||
                ContainsIgnoreCase(x.AssigneeUsername, sourceView.Q) ||
                ContainsIgnoreCase(x.AssigneeFullName, sourceView.Q) ||
                ContainsIgnoreCase(x.PeriodKey, sourceView.Q) ||
                ContainsIgnoreCase(x.PeriodInstanceKey, sourceView.Q) ||
                ContainsIgnoreCase(x.WorkAssignmentReportId, sourceView.Q) ||
                ContainsIgnoreCase(x.WorkAssignmentId, sourceView.Q));
        }

        return query
            .OrderBy(x => x.UnitShortName ?? x.UnitName ?? x.UnitSymbol ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.AssigneeFullName ?? x.AssigneeUsername ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.PeriodKey, StringComparer.Ordinal)
            .ThenBy(x => x.WorkAssignmentReportId, StringComparer.Ordinal);
    }

    private static bool ContainsIgnoreCase(string? value, string? query)
        => !string.IsNullOrWhiteSpace(value) &&
           !string.IsNullOrWhiteSpace(query) &&
           value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static WorkAssignmentBasicSummaryValuesDto BuildSummaryValues(
        List<WorkAssignmentBasicSummaryItemDto> fields,
        List<WorkAssignmentBasicSummaryItemDto> tables)
    {
        var values = new WorkAssignmentBasicSummaryValuesDto();

        foreach (var field in fields)
        {
            var key = NormalizeOptionalText(field.FieldId) ?? NormalizeOptionalText(field.FieldKey);
            if (key is null || values.Fields.ContainsKey(key))
                continue;

            values.Fields[key] = new WorkAssignmentBasicSummaryValueDto
            {
                Value = field.Value,
                DisplayValue = FormatSummaryDisplayValue(field),
                DataType = field.DataType,
                Operation = field.Operation
            };
        }

        foreach (var group in tables
                     .Where(x => !string.IsNullOrWhiteSpace(x.BlockId))
                     .GroupBy(x => x.BlockId!, StringComparer.Ordinal))
        {
            var table = new WorkAssignmentBasicSummaryTableValuesDto
            {
                BlockId = group.Key,
                TableMode = group.FirstOrDefault()?.TableMode ?? "FIXED_GRID"
            };

            foreach (var item in group.OrderBy(x => x.Index ?? int.MaxValue).ThenBy(x => x.MetricKey, StringComparer.Ordinal))
            {
                var displayValue = FormatSummaryDisplayValue(item);
                table.Cells.Add(new WorkAssignmentBasicSummaryTableCellValueDto
                {
                    MetricKey = item.MetricKey ?? item.TargetKey,
                    RowKey = item.RowKey,
                    ColumnKey = item.ColumnKey,
                    Index = item.Index,
                    Value = item.Value,
                    DisplayValue = displayValue,
                    DataType = item.DataType,
                    Operation = item.Operation
                });

                if (!item.Index.HasValue || item.Index.Value < 0)
                    continue;

                while (table.Values1D.Count <= item.Index.Value)
                    table.Values1D.Add(null);

                table.Values1D[item.Index.Value] = CoerceWorkbookValue(item, displayValue);
            }

            values.Tables.Add(table);
        }

        return values;
    }

    private static object? CoerceWorkbookValue(WorkAssignmentBasicSummaryItemDto item, string? displayValue)
    {
        if (item.Value is null)
            return null;

        if (item.Operation is "SUM" or "MIN" or "MAX" or "MEAN" or "COUNT" or "TRUE_COUNT" or "FALSE_COUNT")
            return item.Value;

        return displayValue;
    }

    private static string? FormatSummaryDisplayValue(WorkAssignmentBasicSummaryItemDto item)
    {
        if (item.Buckets.Count > 0)
            return string.Join("; ", item.Buckets.Select(x => $"{x.Label}: {x.Count}"));

        if (!string.IsNullOrWhiteSpace(item.Text))
            return item.TextTruncated ? item.Text + "..." : item.Text;

        return item.Operation switch
        {
            "MIN_DATE" => FormatDateValue(item.MinDateUtc),
            "MAX_DATE" => FormatDateValue(item.MaxDateUtc),
            "MIN" => FormatDecimalValue(item.Min),
            "MAX" => FormatDecimalValue(item.Max),
            "MEAN" => FormatDecimalValue(item.Mean),
            "SUM" => FormatDecimalValue(item.Sum),
            "TRUE_COUNT" => item.TrueCount?.ToString(CultureInfo.InvariantCulture),
            "FALSE_COUNT" => item.FalseCount?.ToString(CultureInfo.InvariantCulture),
            _ => item.Value?.ToString()
        };
    }

    private static string? FormatDateValue(DateTime? value)
        => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? FormatDecimalValue(decimal? value)
        => value?.ToString(CultureInfo.InvariantCulture);

    private static string BuildRequestHash(NormalizedRequest req, string scopeAssignmentId, string dynamicFormTemplateId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            FeatureVersion,
            scopeAssignmentId,
            dynamicFormTemplateId,
            scopeMode = ScopeMode,
            periodScopeMode = req.PeriodScopeMode,
            periodKey = req.PeriodKey,
            periodKeyFrom = req.PeriodKeyFrom,
            periodKeyTo = req.PeriodKeyTo,
            selectedUnitIds = req.SelectedUnitIds,
            defaultMethods = req.DefaultMethods,
            rules = req.Rules,
            maxTextChars = req.MaxTextChars
        }, JsonOptions);

        return Sha256(payload);
    }

    private static string BuildSourceSignatureHash(
        IEnumerable<WorkAssignment> assignments,
        IEnumerable<WorkAssignmentReport> reports)
    {
        var payload = JsonSerializer.Serialize(new
        {
            assignments = assignments
                .OrderBy(x => x.Id, StringComparer.Ordinal)
                .Select(x => new
                {
                    x.Id,
                    x.UpdatedAtUtc,
                    x.IsActive,
                    assignees = x.Assignees
                        .Select(a => new { a.UserId, a.UnitId })
                        .OrderBy(a => a.UserId, StringComparer.Ordinal)
                        .ThenBy(a => a.UnitId, StringComparer.Ordinal)
                        .ToList()
                })
                .ToList(),
            reports = reports
                .OrderBy(x => x.Id, StringComparer.Ordinal)
                .Select(x => new
                {
                    x.Id,
                    x.WorkAssignmentId,
                    x.WorkReportPeriodId,
                    x.Status,
                    x.IsCurrent,
                    x.IsActive,
                    x.PeriodKey,
                    x.PeriodInstanceKey,
                    x.PeriodKind,
                    x.PeriodStart,
                    x.PeriodEnd,
                    x.DynamicFormTemplateId,
                    x.CumulativeContributionMode,
                    x.PayloadRevision,
                    x.PayloadHash,
                    x.PayloadUpdatedAtUtc,
                    x.UpdatedAtUtc,
                    x.AggregateSnapshotDirty,
                    x.AggregateSnapshotRefreshedAtUtc
                })
                .ToList()
        }, JsonOptions);

        return Sha256(payload);
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool CanReadAssignment(WorkAssignment assignment, string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            return false;

        return string.Equals(assignment.CreatedByUserId, actorUserId, StringComparison.Ordinal)
               || assignment.LeaderWatcherUserIds.Contains(actorUserId)
               || assignment.Assignees.Any(x => string.Equals(x.UserId, actorUserId, StringComparison.Ordinal));
    }

    private static void EnsureActor(string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            throw AppExceptionFactory.Unauthorized();
    }

    private static void ValidateSummaryScope(WorkAssignment scope, NormalizedRequest req)
    {
        if (!WorkAssignmentTypes.All.Contains(scope.AssignmentType, StringComparer.OrdinalIgnoreCase))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_TYPE_UNSUPPORTED,
                new
                {
                    scopeAssignmentId = scope.Id,
                    scope.AssignmentType,
                    supportedAssignmentTypes = WorkAssignmentTypes.All,
                    reason = "BASIC_SUMMARY_ASSIGNMENT_TYPE_UNSUPPORTED"
                });
        }

        if (req.PeriodScopeMode == "CUMULATIVE_TO_PERIOD")
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_PERIOD_SCOPE_INVALID,
                new { req.PeriodScopeMode, reason = "BASIC_SUMMARY_CUMULATIVE_DISABLED" },
                "Basic summary does not support cumulative period scope.");
        }

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

        if (req.PeriodScopeMode == "ALL_PERIODS")
        {
            if (string.Equals(scope.AssignmentType, WorkAssignmentTypes.PeriodicReport, StringComparison.OrdinalIgnoreCase))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_PERIOD_SCOPE_INVALID,
                    new
                    {
                        req.PeriodScopeMode,
                        scope.AssignmentType,
                        reason = "BASIC_SUMMARY_PERIODIC_WINDOW_REQUIRED"
                    },
                    "Periodic basic summary requires a single period or period range.");
            }

            return;
        }

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_PERIOD_SCOPE_INVALID,
            new { req.PeriodScopeMode });
    }

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

    private static List<string> NormalizeStringList(IEnumerable<string>? values)
        => (values ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

    private static string NormalizePeriodScopeMode(string? value)
    {
        var normalized = (value ?? "ALL_PERIODS").Trim().ToUpperInvariant();
        return normalized == "ALL_APPROVED_IN_SCOPE" ? "ALL_PERIODS" : normalized;
    }

    private static string? NormalizeDayKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length == 8)
            return digits;

        return value.Trim();
    }

    private static bool TryParseNormalizedDayKey(string? value, out DateTime date)
        => DateTime.TryParseExact(
            NormalizeDayKey(value),
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    private async Task<string> NormalizeExistingTemplateIdAsync(string? dynamicFormTemplateId, CancellationToken ct)
    {
        var templateId = dynamicFormTemplateId?.Trim();
        if (string.IsNullOrWhiteSpace(templateId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_DYNAMIC_FORM_TEMPLATE_ID_REQUIRED);

        var exists = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == templateId && !x.IsDeleted)
            .AnyAsync(ct);
        if (!exists)
        {
            throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
                new { dynamicFormTemplateId = templateId });
        }

        return templateId;
    }

    private static WorkAssignmentBasicSummaryConfigDto MapConfig(WorkAssignmentBasicSummaryConfig config)
        => new()
        {
            Id = config.Id,
            WorkId = config.WorkId,
            AssignmentId = config.AssignmentId,
            DynamicFormTemplateId = config.DynamicFormTemplateId,
            DefaultMethods = ParseConfigJson(config.DefaultMethodsJson, ToDefaultMethodsDto(NormalizeDefaultMethods(null))),
            Rules = NormalizeRules(ParseConfigJson<List<WorkAssignmentBasicSummaryRuleDto>>(config.RulesJson, new())),
            VersionNo = config.VersionNo,
            IsActive = config.IsActive
        };

    private static T ParseConfigJson<T>(string? json, T fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
            return fallback;

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static WorkAssignmentBasicSummaryDefaultMethodsDto ToDefaultMethodsDto(NormalizedDefaultMethods value)
        => new()
        {
            Number = value.Number,
            Date = value.Date,
            Boolean = value.Boolean,
            Text = value.Text,
            Selection = value.Selection
        };

    private static List<WorkAssignmentBasicSummaryRuleDto> NormalizeRules(
        IEnumerable<WorkAssignmentBasicSummaryRuleDto>? rules)
        => (rules ?? Array.Empty<WorkAssignmentBasicSummaryRuleDto>())
            .Where(x => !string.IsNullOrWhiteSpace(x.TargetKey) && !string.IsNullOrWhiteSpace(x.Operation))
            .Select(x => new WorkAssignmentBasicSummaryRuleDto
            {
                TargetKind = string.IsNullOrWhiteSpace(x.TargetKind)
                    ? "FIELD"
                    : x.TargetKind.Trim().ToUpperInvariant(),
                TargetKey = x.TargetKey.Trim(),
                Operation = NormalizeOperation(x.Operation)
            })
            .GroupBy(x => $"{x.TargetKind}:{x.TargetKey}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.TargetKind, StringComparer.Ordinal)
            .ThenBy(x => x.TargetKey, StringComparer.Ordinal)
            .ToList();

    private static NormalizedDefaultMethods NormalizeDefaultMethods(WorkAssignmentBasicSummaryDefaultMethodsDto? value)
    {
        var number = NormalizeNumericOperationOrDefault(value?.Number, "SUM");
        return new(
            Number: number,
            Date: number,
            Boolean: number,
            Text: number,
            Selection: number);
    }

    private static NormalizedSourceView NormalizeSourceView(WorkAssignmentBasicSummarySourceViewRequestDto? value)
    {
        var page = value?.Page ?? 0;
        var pageSize = value?.PageSize ?? 10;
        return new NormalizedSourceView(
            Q: NormalizeOptionalText(value?.Q),
            PeriodKey: NormalizeOptionalText(value?.PeriodKey),
            UnitId: NormalizeOptionalText(value?.UnitId),
            AssigneeUserId: NormalizeOptionalText(value?.AssigneeUserId),
            Page: Math.Max(0, page),
            PageSize: Math.Clamp(pageSize <= 0 ? 10 : pageSize, 1, MaxSourcePageSize));
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeOperationOrDefault(string? value, string fallback)
        => NormalizeNumericOperationOrDefault(value, fallback);

    private static string ResolveOperation(
        List<WorkAssignmentBasicSummaryRuleDto> rules,
        string targetKind,
        string targetKey,
        string alternateKey,
        string fallback)
    {
        var rule = rules.FirstOrDefault(x =>
            string.Equals(x.TargetKind, targetKind, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(x.TargetKey, targetKey, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(x.TargetKey, alternateKey, StringComparison.OrdinalIgnoreCase)));

        return rule is null ? fallback : NormalizeOperation(rule.Operation);
    }

    private static string ResolveTableOperation(
        List<WorkAssignmentBasicSummaryRuleDto> rules,
        ParsedTableValue value,
        string targetKey,
        string fallback)
    {
        var candidateKeys = new List<string>
        {
            targetKey,
            value.MetricKey
        };

        if (value.Index >= 0)
        {
            candidateKeys.Add($"table:{value.BlockId}:index:{value.Index}");
            candidateKeys.Add($"index:{value.Index}");
        }

        var rule = rules.FirstOrDefault(x =>
            string.Equals(x.TargetKind, "TABLE", StringComparison.OrdinalIgnoreCase) &&
            candidateKeys.Any(key => string.Equals(x.TargetKey, key, StringComparison.OrdinalIgnoreCase)));

        return rule is null ? fallback : NormalizeOperation(rule.Operation);
    }

    private static string NormalizeOperation(string? value)
        => NormalizeNumericOperationOrDefault(value, "SUM");

    private static string NormalizeNumericOperationOrDefault(string? value, string fallback)
    {
        var op = value?.Trim().ToUpperInvariant();
        return op switch
        {
            "SUM" => "SUM",
            "MIN" => "MIN",
            "MAX" => "MAX",
            "MEAN" or "AVG" or "AVERAGE" => "MEAN",
            "COUNT" => "COUNT",
            _ => fallback
        };
    }

    private static bool IsNumericOperation(string? value)
    {
        var op = value?.Trim().ToUpperInvariant();
        return op is "SUM" or "MIN" or "MAX" or "MEAN" or "AVG" or "AVERAGE" or "COUNT";
    }

    private static string DefaultFieldOperation(FieldDefinition field, NormalizedDefaultMethods defaults)
        => defaults.Number;

    private static string DefaultTableOperation(ParsedTableValue value, NormalizedDefaultMethods defaults)
        => defaults.Number;

    private static string FieldTypeToSummaryDataType(string fieldType)
        => fieldType switch
        {
            "number" => "NUMBER",
            "date" => "DATE",
            "boolean" => "BOOLEAN",
            "singleSelect" => "SINGLE_SELECT",
            "multiSelect" => "MULTI_SELECT",
            "stringList" => "STRING_LIST",
            _ => "TEXT"
        };

    private static List<FieldDefinition> ExtractFieldDefinitions(string? fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
            return new List<FieldDefinition>();

        try
        {
            var rawFields = JsonSerializer.Deserialize<List<DynamicFormFieldDefinition>>(fieldsJson, JsonOptions);
            if (rawFields is null || rawFields.Count == 0)
                return new List<FieldDefinition>();

            return rawFields
                .Select(ToFieldDefinition)
                .OfType<FieldDefinition>()
                .GroupBy(x => x.FieldId, StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();
        }
        catch (JsonException)
        {
            return new List<FieldDefinition>();
        }
    }

    private static FieldDefinition? ToFieldDefinition(DynamicFormFieldDefinition field)
    {
        var fieldId = field.Id?.Trim();
        if (string.IsNullOrWhiteSpace(fieldId))
            return null;

        var fieldKey = string.IsNullOrWhiteSpace(field.Key) ? fieldId : field.Key.Trim();
        var label = PickNonBlank(field.Name, field.DisplayName, field.Label) ?? fieldKey;
        var options = (field.Options ?? new List<DynamicFormFieldOption>())
            .Select(x => new FieldOption(
                string.IsNullOrWhiteSpace(x.Code) ? string.Empty : x.Code.Trim(),
                string.IsNullOrWhiteSpace(x.Label) ? x.Code?.Trim() ?? string.Empty : x.Label.Trim()))
            .Where(x => !string.IsNullOrWhiteSpace(x.Code))
            .GroupBy(x => x.Code, StringComparer.Ordinal)
            .Select(x => x.First())
            .ToList();

        return new FieldDefinition(fieldId, fieldKey, label, NormalizeFieldType(field.Type), options);
    }

    private static List<ParsedFieldValue> ExtractFieldValues(
        string? fieldValuesJson,
        IReadOnlyList<FieldDefinition> fields)
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

    private static IEnumerable<ParsedFieldValue> ExtractFieldValue(FieldDefinition field, JsonElement value)
    {
        if (IsBlankJsonElement(value))
            yield break;

        if (field.FieldType == "number")
        {
            var number = ToNullableDecimal(value);
            if (number.HasValue)
                yield return ParsedFieldValue.Number(field, number.Value);
            yield break;
        }

        if (field.FieldType == "boolean")
        {
            if (TryReadBoolean(value, out var boolean))
                yield return ParsedFieldValue.Boolean(field, boolean);
            yield break;
        }

        if (field.FieldType == "date")
        {
            var date = ReadDateValue(value, requireFullDate: false);
            if (date.HasValue)
                yield return ParsedFieldValue.Date(field, date.Value);
            yield break;
        }

        if (field.FieldType == "singleSelect")
        {
            var code = ToNullableString(value)?.Trim();
            if (!string.IsNullOrWhiteSpace(code))
                yield return ParsedFieldValue.Bucket(field, code, ResolveOptionLabel(field.Options, code));
            yield break;
        }

        if (field.FieldType == "multiSelect")
        {
            foreach (var code in ReadStringListValues(value).Distinct(StringComparer.Ordinal))
                yield return ParsedFieldValue.Bucket(field, code, ResolveOptionLabel(field.Options, code));
            yield break;
        }

        foreach (var text in ReadStringListValues(value))
            yield return ParsedFieldValue.Text(field, text);
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

    private static bool TryGetFieldValue(JsonElement valuesObject, FieldDefinition field, out JsonElement value)
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

    private static IEnumerable<ParsedTableValue> ExtractTableValues(
        string? tableValuesJson,
        ISet<string>? skippedDirectAggregateBlocks = null)
    {
        var output = new List<ParsedTableValue>();
        if (string.IsNullOrWhiteSpace(tableValuesJson))
            return output;

        try
        {
            using var document = JsonDocument.Parse(tableValuesJson);
            if (!document.RootElement.TryGetProperty("blocks", out var blocks) ||
                blocks.ValueKind != JsonValueKind.Array ||
                blocks.GetArrayLength() == 0)
            {
                return output;
            }

            foreach (var blockElement in blocks.EnumerateArray())
            {
                var block = JsonSerializer.Deserialize<TableValuesBlock>(blockElement.GetRawText(), JsonOptions);
                if (block is null)
                    continue;

                using var valuesReader = Values1DCompression.CreateBlockReader(blockElement);
                var blockId = NormalizeMetricPart(block.BlockId, "excel_block");
                var directInputCellCount = ResolveDirectAggregateInputCellCount(block, valuesReader);
                if (!DynamicExcelRuntimePolicy.CanRunDirectTableAggregation(directInputCellCount))
                {
                    skippedDirectAggregateBlocks?.Add($"{blockId} ({directInputCellCount})");
                    continue;
                }

                // statisticsDisabled only disables background projections; basic summary reads saved values directly.
                var tableMode = NormalizeTableMode(block.TableMode);
                var metrics = NormalizeMetricDefinitions(block.MetricDefinitions, blockId, tableMode);

                if (tableMode == "APPEND_ROWS")
                {
                    output.AddRange(ExtractAppendRows(block, blockId, metrics));
                    continue;
                }

                if (tableMode == "APPEND_COLUMNS")
                {
                    output.AddRange(ExtractAppendColumns(block, blockId, metrics));
                    continue;
                }

                if (tableMode == "MATRIX")
                {
                    output.AddRange(ExtractMatrix(block, blockId, metrics));
                    continue;
                }

                output.AddRange(ExtractFixedGrid(block, blockId, metrics, valuesReader));
            }
        }
        catch (JsonException)
        {
            return output;
        }

        return output;
    }

    private static int ResolveDirectAggregateInputCellCount(
        TableValuesBlock block,
        Values1DCompression.Values1DReader? valuesReader)
    {
        var metadataInputCellCount = block.StatisticsInputCellCount.GetValueOrDefault();
        var valuesInputCellCount = valuesReader?.Length ?? block.Values1D?.Count ?? 0;
        if (metadataInputCellCount > 0)
            return Math.Max(metadataInputCellCount, valuesInputCellCount);
        if (valuesInputCellCount > 0)
            return valuesInputCellCount;

        var width = block.W.GetValueOrDefault();
        var height = block.H.GetValueOrDefault();
        return width > 0 && height > 0 ? width * height : 0;
    }

    private static IEnumerable<ParsedTableValue> ExtractFixedGrid(
        TableValuesBlock block,
        string blockId,
        List<MetricContract> metricDefinitions,
        Values1DCompression.Values1DReader? valuesReader)
    {
        var valueCount = valuesReader?.Length ?? block.Values1D?.Count ?? 0;
        if (valueCount <= 0)
            yield break;

        var metrics = metricDefinitions.Count > 0
            ? metricDefinitions
            : NormalizeIndexMap(block.IndexMap, blockId);

        if (metrics.Count == 0)
            metrics = BuildFallbackMetricMap(blockId, block.W, block.H, valueCount);

        foreach (var metric in metrics)
        {
            if (metric.Index < 0 || metric.Index >= valueCount)
                continue;

            var metricValue = valuesReader is not null
                ? valuesReader.GetElement(metric.Index)
                : block.Values1D is not null && metric.Index < block.Values1D.Count
                    ? block.Values1D[metric.Index]
                    : (JsonElement?)null;
            if (!metricValue.HasValue)
                continue;

            foreach (var row in ParseTableCell(metricValue.Value, blockId, "FIXED_GRID", metric))
                yield return row;
        }
    }

    private static IEnumerable<ParsedTableValue> ExtractAppendRows(
        TableValuesBlock block,
        string blockId,
        List<MetricContract> metricDefinitions)
    {
        foreach (var row in block.Rows ?? new List<TableAppendRow>())
        {
            if (row.Cells is null)
                continue;

            foreach (var cell in row.Cells)
            {
                var columnKey = NormalizeMetricPart(cell.Key, "value");
                var metric = metricDefinitions.FirstOrDefault(x => string.Equals(x.ColumnKey, columnKey, StringComparison.Ordinal))
                             ?? new MetricContract(0, "APPEND_ROWS", columnKey, $"table:{blockId}.column:{columnKey}", "NUMBER", Array.Empty<MetricOption>());

                foreach (var parsed in ParseTableCell(cell.Value, blockId, "APPEND_ROWS", metric))
                    yield return parsed;
            }
        }
    }

    private static IEnumerable<ParsedTableValue> ExtractAppendColumns(
        TableValuesBlock block,
        string blockId,
        List<MetricContract> metricDefinitions)
    {
        foreach (var column in block.Columns ?? new List<TableAppendColumn>())
        {
            if (column.Cells is null)
                continue;

            foreach (var cell in column.Cells)
            {
                var rowKey = NormalizeMetricPart(cell.Key, "row");
                var metric = metricDefinitions.FirstOrDefault(x => string.Equals(x.RowKey, rowKey, StringComparison.Ordinal))
                             ?? new MetricContract(0, rowKey, "APPEND_COLUMNS", $"table:{blockId}.row:{rowKey}", "NUMBER", Array.Empty<MetricOption>());

                foreach (var parsed in ParseTableCell(cell.Value, blockId, "APPEND_COLUMNS", metric))
                    yield return parsed;
            }
        }
    }

    private static IEnumerable<ParsedTableValue> ExtractMatrix(
        TableValuesBlock block,
        string blockId,
        List<MetricContract> metricDefinitions)
    {
        foreach (var cell in block.Cells ?? new List<TableMatrixCell>())
        {
            var rowKey = NormalizeMetricPart(cell.RowKey, "row");
            var columnKey = NormalizeMetricPart(cell.ColumnKey, "column");
            var metricKey = string.IsNullOrWhiteSpace(cell.MetricKey)
                ? BuildMetricKey(blockId, rowKey, columnKey)
                : cell.MetricKey.Trim();
            var metric = metricDefinitions.FirstOrDefault(x => string.Equals(x.MetricKey, metricKey, StringComparison.Ordinal))
                         ?? new MetricContract(
                             ResolveMatrixMetricIndex(rowKey, columnKey, block.W),
                             rowKey,
                             columnKey,
                             metricKey,
                             "NUMBER",
                             Array.Empty<MetricOption>());

            foreach (var parsed in ParseTableCell(cell.Value, blockId, "MATRIX", metric))
                yield return parsed;
        }
    }

    private static IEnumerable<ParsedTableValue> ParseTableCell(
        JsonElement value,
        string blockId,
        string tableMode,
        MetricContract metric)
    {
        if (IsBlankJsonElement(value) || metric.DataType == "IGNORE")
            yield break;

        if (metric.DataType != "NUMBER")
            yield break;

        var number = ToNullableDecimal(value);
        if (number.HasValue)
            yield return ParsedTableValue.Number(blockId, tableMode, metric, number.Value);
    }

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

    private static List<MetricContract> NormalizeIndexMap(List<TableIndexMapItem>? items, string blockId)
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
                return new MetricContract(index, rowKey, columnKey, metricKey, "NUMBER", Array.Empty<MetricOption>());
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
        var metrics = new List<MetricContract>();
        var w = Math.Max(1, width.GetValueOrDefault());
        var h = Math.Max(1, height.GetValueOrDefault());
        var count = Math.Min(valueCount, Math.Max(valueCount, w * h));

        for (var index = 0; index < count; index++)
        {
            var rowIndex = w <= 0 ? 0 : index / w;
            var columnIndex = w <= 0 ? index : index % w;
            var rowKey = $"row_{rowIndex + 1}";
            var columnKey = $"col_{columnIndex + 1}";
            metrics.Add(new MetricContract(
                index,
                rowKey,
                columnKey,
                BuildMetricKey(blockId, rowKey, columnKey),
                "NUMBER",
                Array.Empty<MetricOption>()));
        }

        return metrics;
    }

    private static string BuildMetricKey(string blockId, string rowKey, string columnKey)
        => $"table:{blockId}.row:{rowKey}.column:{columnKey}";

    private static int ResolveMatrixMetricIndex(string rowKey, string columnKey, int? width)
    {
        var rowIndex = ParseOrdinalIndex(rowKey, "row_");
        var columnIndex = ParseOrdinalIndex(columnKey, "col_");
        var w = width.GetValueOrDefault();

        if (rowIndex.HasValue && columnIndex.HasValue && w > 0)
            return rowIndex.Value * w + columnIndex.Value;

        if (columnIndex.HasValue)
            return columnIndex.Value;

        if (rowIndex.HasValue)
            return rowIndex.Value;

        return 0;
    }

    private static int? ParseOrdinalIndex(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var raw = value[prefix.Length..];
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return null;

        return n - 1;
    }

    private static MetricOption[] NormalizeMetricOptions(List<TableMetricOption>? options)
    {
        if (options is null || options.Count == 0)
            return Array.Empty<MetricOption>();

        return options
            .Select(x => new MetricOption(
                string.IsNullOrWhiteSpace(x.Code) ? string.Empty : x.Code.Trim(),
                string.IsNullOrWhiteSpace(x.Label) ? x.Code?.Trim() ?? string.Empty : x.Label.Trim()))
            .Where(x => !string.IsNullOrWhiteSpace(x.Code))
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();
    }

    private static MetricOption? ResolveMetricOption(IReadOnlyCollection<MetricOption> options, string value)
        => options.FirstOrDefault(x =>
            string.Equals(x.Code, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Label, value, StringComparison.OrdinalIgnoreCase));

    private static string ResolveOptionLabel(IReadOnlyCollection<FieldOption> options, string code)
        => options.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.Ordinal))?.Label ?? code;

    private static string? PickNonBlank(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static string NormalizeFieldType(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "shortText" : value.Trim();
        return raw switch
        {
            "number" => "number",
            "date" or "fullDate" => "date",
            "singleSelect" => "singleSelect",
            "multiSelect" => "multiSelect",
            "boolean" => "boolean",
            "stringList" or "longText" => "stringList",
            _ => "shortText"
        };
    }

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
            "IGNORE" or "IGNORED" or "SKIP" => "IGNORE",
            _ => "NUMBER"
        };
    }

    private static string NormalizeTableMode(string? value)
    {
        var mode = value?.Trim().ToUpperInvariant();
        return mode is "APPEND_ROWS" or "APPEND_COLUMNS" or "MATRIX" or "FIXED_GRID"
            ? mode
            : "FIXED_GRID";
    }

    private static string NormalizeMetricPart(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static bool IsBlankJsonElement(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => true,
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => !value.EnumerateArray().Any(),
            _ => false
        };

    private static string? ToNullableString(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };

    private static decimal? ToNullableDecimal(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                return parsed;

            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed))
                return parsed;
        }

        return null;
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
        if (text is "true" or "1" or "yes" or "y" or "co" or "c\u00f3")
        {
            result = true;
            return true;
        }

        if (text is "false" or "0" or "no" or "n" or "khong" or "kh\u00f4ng")
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

        if (DateTime.TryParseExact(
                text,
                "yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var yearDate))
        {
            return DateTime.SpecifyKind(new DateTime(yearDate.Year, 1, 1), DateTimeKind.Utc);
        }

        if (DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed.Kind == DateTimeKind.Utc
                ? parsed
                : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        return null;
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
            var text = ToNullableString(item)?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
                yield return text;
        }
    }

    private sealed class BasicSummarySnapshotPayload
    {
        [JsonPropertyName("v")]
        public int Version { get; set; }

        [JsonPropertyName("meta")]
        public WorkAssignmentBasicSummaryMetaDto? Meta { get; set; }

        [JsonPropertyName("fields")]
        public List<WorkAssignmentBasicSummaryItemDto>? Fields { get; set; }

        [JsonPropertyName("tb")]
        public List<CompactTableBlockSnapshot>? TableBlocks { get; set; }

        [JsonPropertyName("warnings")]
        public List<string>? Warnings { get; set; }
    }

    private sealed class CompactTableBlockSnapshot
    {
        [JsonPropertyName("b")]
        public string BlockId { get; set; } = default!;

        [JsonPropertyName("m")]
        public string TableMode { get; set; } = "FIXED_GRID";

        [JsonPropertyName("w")]
        public int Width { get; set; } = 1;

        [JsonPropertyName("n")]
        public int Length { get; set; }

        [JsonPropertyName("op")]
        public string Operation { get; set; } = "SUM";

        [JsonPropertyName("vc")]
        public JsonNode? ValueCounts { get; set; }

        [JsonPropertyName("rc")]
        public JsonNode? ReportCounts { get; set; }

        [JsonPropertyName("s")]
        public JsonNode? Sums { get; set; }

        [JsonPropertyName("mi")]
        public JsonNode? Mins { get; set; }

        [JsonPropertyName("ma")]
        public JsonNode? Maxes { get; set; }

        [JsonPropertyName("ov")]
        public List<CompactTableMetricOverride>? Overrides { get; set; }

        [JsonPropertyName("items")]
        public List<WorkAssignmentBasicSummaryItemDto>? Items { get; set; }
    }

    private sealed class CompactTableMetricOverride
    {
        [JsonPropertyName("i")]
        public int Index { get; set; }

        [JsonPropertyName("mk")]
        public string? MetricKey { get; set; }

        [JsonPropertyName("rk")]
        public string? RowKey { get; set; }

        [JsonPropertyName("ck")]
        public string? ColumnKey { get; set; }

        [JsonPropertyName("op")]
        public string? Operation { get; set; }

        [JsonPropertyName("l")]
        public string? Label { get; set; }
    }

    private sealed record CompactTableMetricParts(
        string RowKey,
        string ColumnKey,
        string MetricKey);

    private sealed record NormalizedRequest(
        string ScopeAssignmentId,
        string? DynamicFormTemplateId,
        List<string> SelectedUnitIds,
        NormalizedDefaultMethods DefaultMethods,
        List<WorkAssignmentBasicSummaryRuleDto> Rules,
        NormalizedSourceView SourceView,
        string PeriodScopeMode,
        string? PeriodKey,
        string? PeriodKeyFrom,
        string? PeriodKeyTo,
        bool ForceRefresh,
        bool IncludeSourceRows,
        int MaxTextChars);

    private sealed record NormalizedDefaultMethods(
        string Number,
        string Date,
        string Boolean,
        string Text,
        string Selection);

    private sealed record NormalizedSourceView(
        string? Q,
        string? PeriodKey,
        string? UnitId,
        string? AssigneeUserId,
        int Page,
        int PageSize);

    private sealed class DynamicFormFieldDefinition
    {
        public string? Id { get; set; }
        public string? Key { get; set; }
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public string? Label { get; set; }
        public string? Type { get; set; }
        public List<DynamicFormFieldOption>? Options { get; set; }
    }

    private sealed class DynamicFormFieldOption
    {
        public string? Code { get; set; }
        public string? Label { get; set; }
    }

    private sealed record FieldOption(string Code, string Label);

    private sealed record FieldDefinition(
        string FieldId,
        string FieldKey,
        string FieldLabel,
        string FieldType,
        List<FieldOption> Options);

    private abstract record ParsedValue(
        decimal? NumericValue,
        bool? BooleanValue,
        DateTime? DateValueUtc,
        string? TextValue,
        string? BucketKey,
        string? BucketLabel);

    private sealed record ParsedFieldValue(
        FieldDefinition Field,
        decimal? NumericValue,
        bool? BooleanValue,
        DateTime? DateValueUtc,
        string? TextValue,
        string? BucketKey,
        string? BucketLabel)
        : ParsedValue(NumericValue, BooleanValue, DateValueUtc, TextValue, BucketKey, BucketLabel)
    {
        public static ParsedFieldValue Number(FieldDefinition field, decimal value)
            => new(field, value, null, null, null, null, null);

        public static ParsedFieldValue Boolean(FieldDefinition field, bool value)
            => new(field, null, value, null, null, null, null);

        public static ParsedFieldValue Date(FieldDefinition field, DateTime value)
            => new(field, null, null, value, null, null, null);

        public static ParsedFieldValue Text(FieldDefinition field, string value)
            => new(field, null, null, null, value, null, null);

        public static ParsedFieldValue Bucket(FieldDefinition field, string key, string label)
            => new(field, null, null, null, null, key, label);
    }

    private sealed record ParsedTableValue(
        string BlockId,
        string TableMode,
        string MetricKey,
        string RowKey,
        string ColumnKey,
        int Index,
        string DataType,
        decimal? NumericValue,
        bool? BooleanValue,
        DateTime? DateValueUtc,
        string? TextValue,
        string? BucketKey,
        string? BucketLabel)
        : ParsedValue(NumericValue, BooleanValue, DateValueUtc, TextValue, BucketKey, BucketLabel)
    {
        public static ParsedTableValue Number(string blockId, string tableMode, MetricContract metric, decimal value)
            => new(blockId, tableMode, metric.MetricKey, metric.RowKey, metric.ColumnKey, metric.Index, metric.DataType, value, null, null, null, null, null);

        public static ParsedTableValue Boolean(string blockId, string tableMode, MetricContract metric, bool value)
            => new(blockId, tableMode, metric.MetricKey, metric.RowKey, metric.ColumnKey, metric.Index, metric.DataType, null, value, null, null, null, null);

        public static ParsedTableValue Date(string blockId, string tableMode, MetricContract metric, DateTime value)
            => new(blockId, tableMode, metric.MetricKey, metric.RowKey, metric.ColumnKey, metric.Index, metric.DataType, null, null, value, null, null, null);

        public static ParsedTableValue Text(string blockId, string tableMode, MetricContract metric, string value)
            => new(blockId, tableMode, metric.MetricKey, metric.RowKey, metric.ColumnKey, metric.Index, metric.DataType, null, null, null, value, null, null);

        public static ParsedTableValue Bucket(string blockId, string tableMode, MetricContract metric, string key, string label)
            => new(blockId, tableMode, metric.MetricKey, metric.RowKey, metric.ColumnKey, metric.Index, metric.DataType, null, null, null, null, key, label);
    }

    private sealed class TableValuesRoot
    {
        public List<TableValuesBlock>? Blocks { get; set; }
    }

    private sealed class TableValuesBlock
    {
        public string? BlockId { get; set; }
        public string? TableMode { get; set; }
        public int? W { get; set; }
        public int? H { get; set; }
        public bool? StatisticsDisabled { get; set; }
        public int? StatisticsInputCellCount { get; set; }
        public int? StatisticsInputCellLimit { get; set; }
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
        public Dictionary<string, JsonElement>? Cells { get; set; }
    }

    private sealed class TableAppendColumn
    {
        public Dictionary<string, JsonElement>? Cells { get; set; }
    }

    private sealed class TableMatrixCell
    {
        public string? RowKey { get; set; }
        public string? ColumnKey { get; set; }
        public string? MetricKey { get; set; }
        public JsonElement Value { get; set; }
    }

    private sealed record MetricContract(
        int Index,
        string RowKey,
        string ColumnKey,
        string MetricKey,
        string DataType,
        MetricOption[] Options);

    private sealed record MetricOption(string Code, string Label);

    private sealed class SummaryAccumulator
    {
        private readonly int _maxTextChars;
        private readonly HashSet<string> _reportIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WorkAssignmentBasicSummaryBucketDto> _buckets = new(StringComparer.Ordinal);
        private readonly StringBuilder _text = new();
        private int _textCharCount;

        public SummaryAccumulator(
            string targetKind,
            string targetKey,
            string label,
            string dataType,
            string operation,
            int maxTextChars)
        {
            TargetKind = targetKind;
            TargetKey = targetKey;
            Label = label;
            DataType = dataType;
            Operation = operation;
            _maxTextChars = maxTextChars;
        }

        public string TargetKind { get; }
        public string TargetKey { get; }
        public string Label { get; }
        public string DataType { get; }
        public string Operation { get; }
        public string? FieldId { get; init; }
        public string? FieldKey { get; init; }
        public string? BlockId { get; init; }
        public string? TableMode { get; init; }
        public string? MetricKey { get; init; }
        public string? RowKey { get; init; }
        public string? ColumnKey { get; init; }
        public int? Index { get; init; }
        public int ValueCount { get; private set; }
        public decimal Sum { get; private set; }
        public decimal? Min { get; private set; }
        public decimal? Max { get; private set; }
        public int TrueCount { get; private set; }
        public int FalseCount { get; private set; }
        public DateTime? MinDateUtc { get; private set; }
        public DateTime? MaxDateUtc { get; private set; }
        public bool TextTruncated { get; private set; }

        public void AddNumber(decimal value, string reportId)
        {
            Track(reportId);
            ValueCount++;
            Sum += value;
            Min = Min.HasValue ? Math.Min(Min.Value, value) : value;
            Max = Max.HasValue ? Math.Max(Max.Value, value) : value;
        }

        public void AddBoolean(bool value, string reportId)
        {
            Track(reportId);
            ValueCount++;
            if (value) TrueCount++;
            else FalseCount++;
        }

        public void AddDate(DateTime value, string reportId)
        {
            Track(reportId);
            ValueCount++;
            MinDateUtc = MinDateUtc.HasValue && MinDateUtc.Value <= value ? MinDateUtc : value;
            MaxDateUtc = MaxDateUtc.HasValue && MaxDateUtc.Value >= value ? MaxDateUtc : value;
        }

        public void AddBucket(string key, string label, string reportId)
        {
            Track(reportId);
            ValueCount++;
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                bucket = new WorkAssignmentBasicSummaryBucketDto
                {
                    Key = key,
                    Label = label,
                    Count = 0
                };
                _buckets[key] = bucket;
            }

            bucket.Count++;
        }

        public void AddText(string value, string reportId)
        {
            Track(reportId);
            ValueCount++;
            var text = value.Trim();
            if (text.Length == 0)
                return;

            _textCharCount += text.Length;
            if (TextTruncated)
                return;

            var prefix = _text.Length == 0 ? string.Empty : Environment.NewLine;
            var segment = prefix + text;
            var remaining = _maxTextChars - _text.Length;
            if (remaining <= 0)
            {
                TextTruncated = true;
                return;
            }

            if (segment.Length > remaining)
            {
                _text.Append(segment.AsSpan(0, remaining));
                TextTruncated = true;
                return;
            }

            _text.Append(segment);
        }

        public WorkAssignmentBasicSummaryItemDto ToDto()
        {
            var mean = ValueCount > 0 ? Sum / ValueCount : (decimal?)null;
            var buckets = _buckets.Values
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Label, StringComparer.Ordinal)
                .ToList();

            return new WorkAssignmentBasicSummaryItemDto
            {
                TargetKind = TargetKind,
                TargetKey = TargetKey,
                FieldId = FieldId,
                FieldKey = FieldKey,
                BlockId = BlockId,
                TableMode = TableMode,
                MetricKey = MetricKey,
                RowKey = RowKey,
                ColumnKey = ColumnKey,
                Index = Index,
                Label = Label,
                DataType = DataType,
                Operation = Operation,
                Value = ResolveValue(Operation, mean, buckets),
                ValueCount = ValueCount,
                ReportCount = _reportIds.Count,
                Sum = ValueCount > 0 ? Sum : null,
                Min = Min,
                Max = Max,
                Mean = mean,
                TrueCount = TrueCount,
                FalseCount = FalseCount,
                MinDateUtc = MinDateUtc,
                MaxDateUtc = MaxDateUtc,
                Text = _text.Length == 0 ? null : _text.ToString(),
                TextCharCount = _textCharCount,
                TextTruncated = TextTruncated,
                Buckets = buckets
            };
        }

        private object? ResolveValue(
            string operation,
            decimal? mean,
            List<WorkAssignmentBasicSummaryBucketDto> buckets)
            => operation switch
            {
                "SUM" => ValueCount > 0 ? Sum : null,
                "MIN" => Min,
                "MAX" => Max,
                "MEAN" => mean,
                "TRUE_COUNT" => TrueCount,
                "FALSE_COUNT" => FalseCount,
                "MIN_DATE" => MinDateUtc,
                "MAX_DATE" => MaxDateUtc,
                "JOIN" => _text.Length == 0 ? null : _text.ToString(),
                "BUCKET_COUNT" => buckets,
                _ => ValueCount
            };

        private void Track(string reportId)
        {
            if (!string.IsNullOrWhiteSpace(reportId))
                _reportIds.Add(reportId);
        }
    }
}
