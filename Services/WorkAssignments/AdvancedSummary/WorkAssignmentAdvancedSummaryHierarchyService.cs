using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Notifications;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public sealed class WorkAssignmentAdvancedSummaryHierarchyService : IWorkAssignmentAdvancedSummaryHierarchyService
{
    private const int DayNodeSourceReportLimit = 3000;
    private const int DayNodeSampleTextLimit = 160;
    private const int DayNodeSampleLimit = 5;
    private const string DayNodeValueKind = "ADVANCED_SUMMARY_DAY_NODE_V1";
    private const string MonthNodeValueKind = "ADVANCED_SUMMARY_MONTH_NODE_V1";
    private const string YearNodeValueKind = "ADVANCED_SUMMARY_YEAR_NODE_V1";
    private const string QueryNodeValueKind = "ADVANCED_SUMMARY_QUERY_RESULT_V1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions ValueJsonOptions = new(JsonOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly MongoDbContext _ctx;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly IWorkReportPayloadReader _payloadReader;
    private readonly INotificationService _notifications;

    public WorkAssignmentAdvancedSummaryHierarchyService(
        MongoDbContext ctx,
        IBackgroundJobClient backgroundJobs,
        IWorkReportPayloadReader payloadReader,
        INotificationService notifications)
    {
        _ctx = ctx;
        _backgroundJobs = backgroundJobs;
        _payloadReader = payloadReader;
        _notifications = notifications;
    }

    public async Task<WorkAssignmentAdvancedSummaryDayNodeDto> RequestDayNodeBuildAsync(
        string configId,
        string dayKey,
        BuildWorkAssignmentAdvancedSummaryDayNodeRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        var normalizedDayKey = NormalizeDayKey(dayKey);
        var config = await LoadLockedConfigAsync(configId, ct);
        var context = await LoadContextAsync(config, actorUserId, ct);
        var (startUtc, endExclusiveUtc) = AdvancedSummaryHierarchyKeyHelper.GetDayBoundsUtc(normalizedDayKey);

        var existing = await LoadDayNodeAsync(config, normalizedDayKey, ct);
        if (existing is not null &&
            req?.ForceRefresh != true &&
            string.Equals(existing.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Building, StringComparison.Ordinal))
        {
            return MapDayNode(existing);
        }

        var correlationId = ObjectId.GenerateNewId().ToString();
        var jobId = _backgroundJobs.Enqueue<IWorkAssignmentAdvancedSummaryHierarchyService>(
            svc => svc.BuildDayNodeJobAsync(config.Id, normalizedDayKey, config.ConfigHash, actorUserId, correlationId, CancellationToken.None));
        var now = DateTime.UtcNow;

        var node = existing ?? new WorkAssignmentAdvancedSummaryDayNode
        {
            Id = ObjectId.GenerateNewId().ToString(),
            WorkId = config.WorkId,
            AssignmentId = config.AssignmentId,
            DynamicFormTemplateId = config.DynamicFormTemplateId,
            SectionId = config.SectionId,
            ConfigId = config.Id,
            ConfigVersionNo = config.VersionNo,
            ConfigHash = config.ConfigHash,
            Grain = WorkAssignmentAdvancedSummaryHierarchyGrains.Day,
            GrainKey = normalizedDayKey,
            DayKey = normalizedDayKey,
            WindowStartUtc = startUtc,
            WindowEndExclusiveUtc = endExclusiveUtc,
            CreatedAtUtc = now,
            CreatedByUserId = actorUserId,
            IsDeleted = false
        };

        node.WorkId = config.WorkId;
        node.AssignmentId = config.AssignmentId;
        node.DynamicFormTemplateId = config.DynamicFormTemplateId;
        node.SectionId = config.SectionId;
        node.ConfigId = config.Id;
        node.ConfigVersionNo = config.VersionNo;
        node.ConfigHash = config.ConfigHash;
        node.Grain = WorkAssignmentAdvancedSummaryHierarchyGrains.Day;
        node.GrainKey = normalizedDayKey;
        node.DayKey = normalizedDayKey;
        node.WindowStartUtc = startUtc;
        node.WindowEndExclusiveUtc = endExclusiveUtc;
        node.Status = WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Building;
        node.IsDirty = true;
        node.DirtyReason = req?.ForceRefresh == true ? "FORCE_REFRESH" : "BUILD_REQUESTED";
        node.BuildJobId = jobId;
        node.BuildCorrelationId = correlationId;
        node.BuildError = null;
        node.UpdatedAtUtc = now;
        node.UpdatedByUserId = actorUserId;
        node.IsDeleted = false;

        await _ctx.WorkAssignmentAdvancedSummaryDayNodes.ReplaceOneAsync(
            DayNodeIdentityFilter(config, normalizedDayKey),
            node,
            new ReplaceOptions { IsUpsert = true },
            ct);

        return MapDayNode(node);
    }

    public async Task BuildDayNodeJobAsync(
        string configId,
        string dayKey,
        string expectedConfigHash,
        string actorUserId,
        string correlationId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        var normalizedDayKey = NormalizeDayKey(dayKey);
        var config = await LoadLockedConfigAsync(configId, ct);
        if (!string.Equals(config.ConfigHash, expectedConfigHash, StringComparison.Ordinal))
            return;

        WorkAssignment? notifyScope = null;
        DynamicFormTemplate? notifyTemplate = null;
        try
        {
            var context = await LoadContextAsync(config, actorUserId, ct);
            notifyScope = context.Scope;
            notifyTemplate = context.Template;

            await MarkDayNodeBuildingAsync(config, normalizedDayKey, expectedConfigHash, correlationId, actorUserId, ct);

            var built = await BuildDayNodeAsync(config, context, normalizedDayKey, actorUserId, correlationId, ct);

            await _ctx.WorkAssignmentAdvancedSummaryDayNodes.ReplaceOneAsync(
                CurrentDayNodeBuildFilter(config, normalizedDayKey, expectedConfigHash, correlationId),
                built,
                new ReplaceOptions { IsUpsert = false },
                ct);

            await NotifyDayNodeBuildAsync(
                built,
                actorUserId,
                notifyScope,
                notifyTemplate,
                eventStatus: "done",
                title: "Advanced summary day node built",
                stateText: "day hierarchy node built",
                severity: UserNotificationSeverities.Info,
                requiresAction: false,
                error: null,
                ct);
        }
        catch (Exception ex)
        {
            var error = BuildErrorMessage(ex);
            await MarkDayNodeFailedAsync(config, normalizedDayKey, expectedConfigHash, correlationId, actorUserId, error, ct);
            await NotifyDayNodeBuildAsync(
                await LoadDayNodeAsync(config, normalizedDayKey, ct),
                actorUserId,
                notifyScope,
                notifyTemplate,
                eventStatus: "failed",
                title: "Advanced summary day node failed",
                stateText: "day hierarchy node build failed",
                severity: UserNotificationSeverities.Warning,
                requiresAction: true,
                error,
                ct);
            throw;
        }
    }

    public async Task<WorkAssignmentAdvancedSummaryMonthNodeDto> RequestMonthNodeBuildAsync(
        string configId,
        string monthKey,
        BuildWorkAssignmentAdvancedSummaryMonthNodeRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        var normalizedMonthKey = NormalizeMonthKey(monthKey);
        var config = await LoadLockedConfigAsync(configId, ct);
        await LoadContextAsync(config, actorUserId, ct);
        var (startUtc, endExclusiveUtc) = AdvancedSummaryHierarchyKeyHelper.GetMonthBoundsUtc(normalizedMonthKey);
        var yearKey = AdvancedSummaryHierarchyKeyHelper.ToYearKeyFromMonth(normalizedMonthKey);

        var existing = await LoadMonthNodeAsync(config, normalizedMonthKey, ct);
        if (existing is not null &&
            req?.ForceRefresh != true &&
            string.Equals(existing.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Building, StringComparison.Ordinal))
        {
            return MapMonthNode(existing);
        }

        var correlationId = ObjectId.GenerateNewId().ToString();
        var jobId = _backgroundJobs.Enqueue<IWorkAssignmentAdvancedSummaryHierarchyService>(
            svc => svc.BuildMonthNodeJobAsync(config.Id, normalizedMonthKey, config.ConfigHash, actorUserId, correlationId, CancellationToken.None));
        var now = DateTime.UtcNow;

        var node = existing ?? new WorkAssignmentAdvancedSummaryMonthNode
        {
            Id = ObjectId.GenerateNewId().ToString(),
            CreatedAtUtc = now,
            CreatedByUserId = actorUserId,
            IsDeleted = false
        };

        node.WorkId = config.WorkId;
        node.AssignmentId = config.AssignmentId;
        node.DynamicFormTemplateId = config.DynamicFormTemplateId;
        node.SectionId = config.SectionId;
        node.ConfigId = config.Id;
        node.ConfigVersionNo = config.VersionNo;
        node.ConfigHash = config.ConfigHash;
        node.Grain = WorkAssignmentAdvancedSummaryHierarchyGrains.Month;
        node.GrainKey = normalizedMonthKey;
        node.MonthKey = normalizedMonthKey;
        node.YearKey = yearKey;
        node.WindowStartUtc = startUtc;
        node.WindowEndExclusiveUtc = endExclusiveUtc;
        node.Status = WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Building;
        node.IsDirty = true;
        node.DirtyReason = req?.ForceRefresh == true ? "FORCE_REFRESH" : "BUILD_REQUESTED";
        node.BuildJobId = jobId;
        node.BuildCorrelationId = correlationId;
        node.BuildError = null;
        node.UpdatedAtUtc = now;
        node.UpdatedByUserId = actorUserId;
        node.IsDeleted = false;

        await _ctx.WorkAssignmentAdvancedSummaryMonthNodes.ReplaceOneAsync(
            MonthNodeIdentityFilter(config, normalizedMonthKey),
            node,
            new ReplaceOptions { IsUpsert = true },
            ct);

        return MapMonthNode(node);
    }

    public async Task BuildMonthNodeJobAsync(
        string configId,
        string monthKey,
        string expectedConfigHash,
        string actorUserId,
        string correlationId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        var normalizedMonthKey = NormalizeMonthKey(monthKey);
        var config = await LoadLockedConfigAsync(configId, ct);
        if (!string.Equals(config.ConfigHash, expectedConfigHash, StringComparison.Ordinal))
            return;

        WorkAssignment? notifyScope = null;
        DynamicFormTemplate? notifyTemplate = null;
        try
        {
            var context = await LoadContextAsync(config, actorUserId, ct);
            notifyScope = context.Scope;
            notifyTemplate = context.Template;

            await MarkNodeBuildingAsync(
                _ctx.WorkAssignmentAdvancedSummaryMonthNodes,
                CurrentMonthNodeBuildFilter(config, normalizedMonthKey, expectedConfigHash, correlationId),
                actorUserId,
                ct);

            var built = await BuildMonthNodeAsync(config, normalizedMonthKey, actorUserId, correlationId, ct);

            await _ctx.WorkAssignmentAdvancedSummaryMonthNodes.ReplaceOneAsync(
                CurrentMonthNodeBuildFilter(config, normalizedMonthKey, expectedConfigHash, correlationId),
                built,
                new ReplaceOptions { IsUpsert = false },
                ct);

            await NotifyHierarchyNodeBuildAsync(
                built,
                actorUserId,
                notifyScope,
                notifyTemplate,
                eventStatus: "done",
                title: "Advanced summary month node built",
                stateText: "month hierarchy node built",
                severity: UserNotificationSeverities.Info,
                requiresAction: false,
                error: null,
                ct);
        }
        catch (Exception ex)
        {
            var error = BuildErrorMessage(ex);
            await MarkNodeFailedAsync(
                _ctx.WorkAssignmentAdvancedSummaryMonthNodes,
                CurrentMonthNodeBuildFilter(config, normalizedMonthKey, expectedConfigHash, correlationId),
                actorUserId,
                error,
                ct);
            await NotifyHierarchyNodeBuildAsync(
                await LoadMonthNodeAsync(config, normalizedMonthKey, ct),
                actorUserId,
                notifyScope,
                notifyTemplate,
                eventStatus: "failed",
                title: "Advanced summary month node failed",
                stateText: "month hierarchy node build failed",
                severity: UserNotificationSeverities.Warning,
                requiresAction: true,
                error,
                ct);
            throw;
        }
    }

    public async Task<WorkAssignmentAdvancedSummaryYearNodeDto> RequestYearNodeBuildAsync(
        string configId,
        string yearKey,
        BuildWorkAssignmentAdvancedSummaryYearNodeRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        var normalizedYearKey = NormalizeYearKey(yearKey);
        var config = await LoadLockedConfigAsync(configId, ct);
        await LoadContextAsync(config, actorUserId, ct);
        var (startUtc, endExclusiveUtc) = AdvancedSummaryHierarchyKeyHelper.GetYearBoundsUtc(normalizedYearKey);

        var existing = await LoadYearNodeAsync(config, normalizedYearKey, ct);
        if (existing is not null &&
            req?.ForceRefresh != true &&
            string.Equals(existing.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Building, StringComparison.Ordinal))
        {
            return MapYearNode(existing);
        }

        var correlationId = ObjectId.GenerateNewId().ToString();
        var jobId = _backgroundJobs.Enqueue<IWorkAssignmentAdvancedSummaryHierarchyService>(
            svc => svc.BuildYearNodeJobAsync(config.Id, normalizedYearKey, config.ConfigHash, actorUserId, correlationId, CancellationToken.None));
        var now = DateTime.UtcNow;

        var node = existing ?? new WorkAssignmentAdvancedSummaryYearNode
        {
            Id = ObjectId.GenerateNewId().ToString(),
            CreatedAtUtc = now,
            CreatedByUserId = actorUserId,
            IsDeleted = false
        };

        node.WorkId = config.WorkId;
        node.AssignmentId = config.AssignmentId;
        node.DynamicFormTemplateId = config.DynamicFormTemplateId;
        node.SectionId = config.SectionId;
        node.ConfigId = config.Id;
        node.ConfigVersionNo = config.VersionNo;
        node.ConfigHash = config.ConfigHash;
        node.Grain = WorkAssignmentAdvancedSummaryHierarchyGrains.Year;
        node.GrainKey = normalizedYearKey;
        node.YearKey = normalizedYearKey;
        node.WindowStartUtc = startUtc;
        node.WindowEndExclusiveUtc = endExclusiveUtc;
        node.Status = WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Building;
        node.IsDirty = true;
        node.DirtyReason = req?.ForceRefresh == true ? "FORCE_REFRESH" : "BUILD_REQUESTED";
        node.BuildJobId = jobId;
        node.BuildCorrelationId = correlationId;
        node.BuildError = null;
        node.UpdatedAtUtc = now;
        node.UpdatedByUserId = actorUserId;
        node.IsDeleted = false;

        await _ctx.WorkAssignmentAdvancedSummaryYearNodes.ReplaceOneAsync(
            YearNodeIdentityFilter(config, normalizedYearKey),
            node,
            new ReplaceOptions { IsUpsert = true },
            ct);

        return MapYearNode(node);
    }

    public async Task BuildYearNodeJobAsync(
        string configId,
        string yearKey,
        string expectedConfigHash,
        string actorUserId,
        string correlationId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        var normalizedYearKey = NormalizeYearKey(yearKey);
        var config = await LoadLockedConfigAsync(configId, ct);
        if (!string.Equals(config.ConfigHash, expectedConfigHash, StringComparison.Ordinal))
            return;

        WorkAssignment? notifyScope = null;
        DynamicFormTemplate? notifyTemplate = null;
        try
        {
            var context = await LoadContextAsync(config, actorUserId, ct);
            notifyScope = context.Scope;
            notifyTemplate = context.Template;

            await MarkNodeBuildingAsync(
                _ctx.WorkAssignmentAdvancedSummaryYearNodes,
                CurrentYearNodeBuildFilter(config, normalizedYearKey, expectedConfigHash, correlationId),
                actorUserId,
                ct);

            var built = await BuildYearNodeAsync(config, normalizedYearKey, actorUserId, correlationId, ct);

            await _ctx.WorkAssignmentAdvancedSummaryYearNodes.ReplaceOneAsync(
                CurrentYearNodeBuildFilter(config, normalizedYearKey, expectedConfigHash, correlationId),
                built,
                new ReplaceOptions { IsUpsert = false },
                ct);

            await NotifyHierarchyNodeBuildAsync(
                built,
                actorUserId,
                notifyScope,
                notifyTemplate,
                eventStatus: "done",
                title: "Advanced summary year node built",
                stateText: "year hierarchy node built",
                severity: UserNotificationSeverities.Info,
                requiresAction: false,
                error: null,
                ct);
        }
        catch (Exception ex)
        {
            var error = BuildErrorMessage(ex);
            await MarkNodeFailedAsync(
                _ctx.WorkAssignmentAdvancedSummaryYearNodes,
                CurrentYearNodeBuildFilter(config, normalizedYearKey, expectedConfigHash, correlationId),
                actorUserId,
                error,
                ct);
            await NotifyHierarchyNodeBuildAsync(
                await LoadYearNodeAsync(config, normalizedYearKey, ct),
                actorUserId,
                notifyScope,
                notifyTemplate,
                eventStatus: "failed",
                title: "Advanced summary year node failed",
                stateText: "year hierarchy node build failed",
                severity: UserNotificationSeverities.Warning,
                requiresAction: true,
                error,
                ct);
            throw;
        }
    }

    public async Task<WorkAssignmentAdvancedSummaryHierarchyQueryResponse> QueryHierarchyAsync(
        string configId,
        QueryWorkAssignmentAdvancedSummaryHierarchyRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        var startDayKey = NormalizeQueryDayKey(req.StartDayKey, "startDayKey");
        var endDayKey = NormalizeQueryDayKey(string.IsNullOrWhiteSpace(req.EndDayKey) ? req.StartDayKey : req.EndDayKey, "endDayKey");
        var startUtc = AdvancedSummaryHierarchyKeyHelper.ParseDayKey(startDayKey);
        var endExclusiveUtc = AdvancedSummaryHierarchyKeyHelper.ParseDayKey(endDayKey).AddDays(1);
        if (endExclusiveUtc <= startUtc)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { startDayKey, endDayKey, reason = "ADVANCED_SUMMARY_QUERY_RANGE_INVALID" },
                "Advanced summary query end day must be on or after start day.");
        }

        var config = await LoadLockedConfigAsync(configId, ct);
        await LoadContextAsync(config, actorUserId, ct);

        var allDayKeys = EnumerateDayKeys(startUtc, endExclusiveUtc).ToList();
        var queryState = new HierarchyQueryState(
            await LoadQueryDayNodesAsync(config, allDayKeys, ct),
            await LoadQueryMonthNodesAsync(config, startUtc, endExclusiveUtc, ct),
            await LoadQueryYearNodesAsync(config, startUtc, endExclusiveUtc, ct));

        foreach (var span in PlanHierarchyQuerySpans(startUtc, endExclusiveUtc))
            ResolveQuerySpan(span, queryState);

        if (req.EnqueueMissing)
            await EnqueueQueryBuildsAsync(config, queryState, actorUserId, ct);

        var resultJson = default(string);
        var resultHash = default(string);
        if (queryState.Gaps.Count == 0)
        {
            var selected = queryState.SelectedNodes
                .OrderBy(x => x.WindowStartUtc)
                .ThenBy(x => x.Grain, StringComparer.Ordinal)
                .ToList();
            var result = BuildRollupValue(
                QueryNodeValueKind,
                "QUERY",
                $"{startDayKey}..{endDayKey}",
                monthKey: null,
                yearKey: string.Empty,
                startUtc,
                endExclusiveUtc,
                selected,
                x => $"{x.Grain}:{x.GrainKey}");
            resultJson = JsonSerializer.Serialize(result, ValueJsonOptions);
            resultHash = Sha256(resultJson);
        }

        return new WorkAssignmentAdvancedSummaryHierarchyQueryResponse
        {
            ConfigId = config.Id,
            ConfigHash = config.ConfigHash,
            WorkId = config.WorkId,
            AssignmentId = config.AssignmentId,
            DynamicFormTemplateId = config.DynamicFormTemplateId,
            SectionId = config.SectionId,
            StartDayKey = startDayKey,
            EndDayKey = endDayKey,
            WindowStartUtc = startUtc,
            WindowEndExclusiveUtc = endExclusiveUtc,
            Status = ResolveQueryStatus(queryState),
            ResultJson = resultJson,
            ResultHash = resultHash,
            SelectedNodes = queryState.SelectedNodes
                .OrderBy(x => x.WindowStartUtc)
                .ThenBy(x => x.Grain, StringComparer.Ordinal)
                .Select(MapQueryNode)
                .ToList(),
            MissingNodes = queryState.Gaps
                .Where(x => x.Node is null)
                .Select(MapQueryGap)
                .ToList(),
            DirtyNodes = queryState.Gaps
                .Where(x => x.Node is not null &&
                            !string.Equals(x.Node.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Building, StringComparison.Ordinal))
                .Select(MapQueryGap)
                .ToList(),
            BuildingNodes = queryState.Gaps
                .Where(x => string.Equals(x.Node?.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Building, StringComparison.Ordinal))
                .Select(MapQueryGap)
                .ToList(),
            EnqueuedNodes = queryState.EnqueuedNodes.ToList()
        };
    }

    public async Task<WorkAssignmentAdvancedSummaryDayNodeDiagnosticsResponse> DiagnoseDayNodeAsync(
        string configId,
        string dayKey,
        DiagnoseWorkAssignmentAdvancedSummaryDayNodeRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        req ??= new DiagnoseWorkAssignmentAdvancedSummaryDayNodeRequest();
        var normalizedDayKey = NormalizeDayKey(string.IsNullOrWhiteSpace(dayKey) ? req.DayKey : dayKey);
        var config = await LoadLockedConfigAsync(configId, ct);
        var cacheNode = await LoadDayNodeAsync(config, normalizedDayKey, ct);
        var diagnosticActorUserId = ResolveDiagnosticsActor(config, cacheNode, actorUserId);
        var context = await LoadContextAsync(config, diagnosticActorUserId, ct);
        var directNode = await BuildDayNodeAsync(
            config,
            context,
            normalizedDayKey,
            diagnosticActorUserId,
            $"diagnostics:{ObjectId.GenerateNewId()}",
            ct);

        var includeValueJson = req?.IncludeValueJson == true;
        var differences = CompareDayNodeDiagnostics(cacheNode, directNode);
        return new WorkAssignmentAdvancedSummaryDayNodeDiagnosticsResponse
        {
            ConfigId = config.Id,
            ConfigHash = config.ConfigHash,
            DayKey = normalizedDayKey,
            Status = ResolveDayNodeDiagnosticsStatus(cacheNode, differences),
            Matches = cacheNode is not null && differences.Count == 0,
            DiagnosticActorUserId = diagnosticActorUserId,
            CheckedAtUtc = DateTime.UtcNow,
            Differences = differences,
            Cache = cacheNode is null ? null : ToDayNodeDiagnosticSnapshot(cacheNode, includeValueJson),
            Direct = ToDayNodeDiagnosticSnapshot(directNode, includeValueJson)
        };
    }

    private async Task<WorkAssignmentAdvancedSummaryDayNode> BuildDayNodeAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        AdvancedSummaryBuildContext context,
        string dayKey,
        string actorUserId,
        string correlationId,
        CancellationToken ct)
    {
        EnsureSimpleConfigSupported(config.ConfigJson);
        var (startUtc, endExclusiveUtc) = AdvancedSummaryHierarchyKeyHelper.GetDayBoundsUtc(dayKey);
        var sectionFields = await LoadSectionFieldsAsync(context.Template, config.SectionId, ct);
        var configAnalysis = AnalyzeSimpleConfig(config.ConfigJson, sectionFields);
        if (configAnalysis.UnsupportedFeatures.Count > 0)
            throw UnsupportedConfig(configAnalysis.UnsupportedFeatures);

        var warnings = new List<string>();
        warnings.AddRange(configAnalysis.UnknownFieldRefs.Select(x => $"Unknown field target ignored: {x}"));
        var targetFields = ResolveTargets(configAnalysis, sectionFields, warnings);
        var sourceAssignments = await LoadSourceAssignmentsAsync(context.Scope, config.DynamicFormTemplateId, ct);
        var sourceReports = await LoadDaySourceReportsAsync(
            sourceAssignments.Select(x => x.Id).ToList(),
            config.DynamicFormTemplateId,
            dayKey,
            startUtc,
            endExclusiveUtc,
            ct);

        var reportIds = sourceReports.Select(x => x.Id).ToList();
        var sectionRows = reportIds.Count == 0
            ? new List<WorkAssignmentReportSection>()
            : await _ctx.WorkAssignmentReportSections
                .Find(Builders<WorkAssignmentReportSection>.Filter.In(x => x.WorkAssignmentReportId, reportIds) &
                      Builders<WorkAssignmentReportSection>.Filter.Eq(x => x.SectionId, config.SectionId) &
                      Builders<WorkAssignmentReportSection>.Filter.Eq(x => x.IsDeleted, false))
                .ToListAsync(ct);
        var sectionByReportId = sectionRows
            .GroupBy(x => x.WorkAssignmentReportId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        var accumulators = targetFields
            .Select(x => new DayNodeTargetAccumulator(x.Field, x.Method))
            .ToDictionary(x => x.Field.FieldId, StringComparer.Ordinal);
        var fallbackPayloadReadCount = 0;
        var sourceSignatureParts = new List<string>();

        foreach (var report in sourceReports)
        {
            string? fieldValuesJson;
            string? sectionPayloadHash = null;
            if (sectionByReportId.TryGetValue(report.Id, out var sectionRow))
            {
                fieldValuesJson = sectionRow.FieldValuesJson;
                sectionPayloadHash = sectionRow.PayloadHash;
            }
            else
            {
                fallbackPayloadReadCount++;
                var payload = await _payloadReader.LoadReportPayloadAsync(report, ct);
                fieldValuesJson = payload.FieldValuesJson;
            }

            sourceSignatureParts.Add(BuildSourceSignaturePart(report, sectionPayloadHash));

            if (!TryGetValuesObject(fieldValuesJson, out var valuesObject))
                continue;

            foreach (var target in targetFields)
            {
                if (!TryGetFieldValue(valuesObject, target.Field, out var fieldValue) || IsBlankJsonElement(fieldValue))
                    continue;

                accumulators[target.Field.FieldId].Add(fieldValue, report.Id);
            }
        }

        if (fallbackPayloadReadCount > 0)
            warnings.Add($"Day node read {fallbackPayloadReadCount} full payload(s) because section snapshots were missing.");
        if (sourceReports.Count == 0)
            warnings.Add("No approved source reports were found for this source day.");
        if (targetFields.Count == 0)
            warnings.Add("No field target was available for this day node.");

        var value = new AdvancedSummaryHierarchyNodeValue
        {
            SchemaVersion = 1,
            Kind = DayNodeValueKind,
            GeneratedAtUtc = DateTime.UtcNow,
            ConfigId = config.Id,
            ConfigHash = config.ConfigHash,
            Grain = WorkAssignmentAdvancedSummaryHierarchyGrains.Day,
            GrainKey = dayKey,
            DayKey = dayKey,
            MonthKey = AdvancedSummaryHierarchyKeyHelper.ToMonthKey(dayKey),
            YearKey = AdvancedSummaryHierarchyKeyHelper.ToYearKeyFromDay(dayKey),
            WindowStartUtc = startUtc,
            WindowEndExclusiveUtc = endExclusiveUtc,
            SourceAssignmentCount = sourceAssignments.Count,
            SourceReportCount = sourceReports.Count,
            SectionReportCount = sectionRows.Count,
            SectionFieldCount = sectionFields.Count,
            TargetFieldCount = targetFields.Count,
            Warnings = warnings,
            Fields = accumulators.Values
                .OrderBy(x => x.Field.FieldKey, StringComparer.Ordinal)
                .Select(x => x.ToDto())
                .ToList()
        };
        var valueJson = JsonSerializer.Serialize(value, ValueJsonOptions);
        var now = DateTime.UtcNow;
        var existingNode = await LoadDayNodeAsync(config, dayKey, ct);

        return new WorkAssignmentAdvancedSummaryDayNode
        {
            Id = existingNode?.Id ?? ObjectId.GenerateNewId().ToString(),
            WorkId = config.WorkId,
            AssignmentId = config.AssignmentId,
            DynamicFormTemplateId = config.DynamicFormTemplateId,
            SectionId = config.SectionId,
            ConfigId = config.Id,
            ConfigVersionNo = config.VersionNo,
            ConfigHash = config.ConfigHash,
            Grain = WorkAssignmentAdvancedSummaryHierarchyGrains.Day,
            GrainKey = dayKey,
            DayKey = dayKey,
            WindowStartUtc = startUtc,
            WindowEndExclusiveUtc = endExclusiveUtc,
            Status = WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean,
            IsDirty = false,
            DirtyReason = null,
            SourceSignatureHash = Sha256(string.Join("\n", sourceSignatureParts.OrderBy(x => x, StringComparer.Ordinal))),
            SourceReportCount = sourceReports.Count,
            SourceReportIds = sourceReports.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            InputNodeKeys = new List<string>(),
            ValueJson = valueJson,
            ValueHash = Sha256(valueJson),
            BuiltAtUtc = now,
            BuildJobId = existingNode?.BuildJobId,
            BuildCorrelationId = correlationId,
            BuildError = null,
            CreatedAtUtc = existingNode?.CreatedAtUtc ?? now,
            CreatedByUserId = existingNode?.CreatedByUserId ?? actorUserId,
            UpdatedAtUtc = now,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };
    }

    private async Task<WorkAssignmentAdvancedSummaryMonthNode> BuildMonthNodeAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        string monthKey,
        string actorUserId,
        string correlationId,
        CancellationToken ct)
    {
        EnsureSimpleConfigSupported(config.ConfigJson);
        var (startUtc, endExclusiveUtc) = AdvancedSummaryHierarchyKeyHelper.GetMonthBoundsUtc(monthKey);
        var expectedDayKeys = EnumerateDayKeys(startUtc, endExclusiveUtc).ToList();
        var childNodes = await LoadCleanDayNodesForMonthAsync(config, monthKey, expectedDayKeys, ct);
        ValidateCleanChildNodes(
            WorkAssignmentAdvancedSummaryHierarchyGrains.Month,
            monthKey,
            WorkAssignmentAdvancedSummaryHierarchyGrains.Day,
            expectedDayKeys,
            childNodes,
            x => x.DayKey);

        var rollup = BuildRollupValue(
            MonthNodeValueKind,
            WorkAssignmentAdvancedSummaryHierarchyGrains.Month,
            monthKey,
            monthKey,
            AdvancedSummaryHierarchyKeyHelper.ToYearKeyFromMonth(monthKey),
            startUtc,
            endExclusiveUtc,
            childNodes,
            x => x.DayKey);
        var valueJson = JsonSerializer.Serialize(rollup, ValueJsonOptions);
        var now = DateTime.UtcNow;
        var existingNode = await LoadMonthNodeAsync(config, monthKey, ct);

        return new WorkAssignmentAdvancedSummaryMonthNode
        {
            Id = existingNode?.Id ?? ObjectId.GenerateNewId().ToString(),
            WorkId = config.WorkId,
            AssignmentId = config.AssignmentId,
            DynamicFormTemplateId = config.DynamicFormTemplateId,
            SectionId = config.SectionId,
            ConfigId = config.Id,
            ConfigVersionNo = config.VersionNo,
            ConfigHash = config.ConfigHash,
            Grain = WorkAssignmentAdvancedSummaryHierarchyGrains.Month,
            GrainKey = monthKey,
            MonthKey = monthKey,
            YearKey = AdvancedSummaryHierarchyKeyHelper.ToYearKeyFromMonth(monthKey),
            WindowStartUtc = startUtc,
            WindowEndExclusiveUtc = endExclusiveUtc,
            Status = WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean,
            IsDirty = false,
            DirtyReason = null,
            SourceSignatureHash = BuildInputNodeSignature(childNodes),
            SourceReportCount = childNodes.Sum(x => x.SourceReportCount),
            SourceReportIds = new List<string>(),
            InputNodeKeys = childNodes.Select(x => x.DayKey).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            ValueJson = valueJson,
            ValueHash = Sha256(valueJson),
            BuiltAtUtc = now,
            BuildJobId = existingNode?.BuildJobId,
            BuildCorrelationId = correlationId,
            BuildError = null,
            CreatedAtUtc = existingNode?.CreatedAtUtc ?? now,
            CreatedByUserId = existingNode?.CreatedByUserId ?? actorUserId,
            UpdatedAtUtc = now,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };
    }

    private async Task<WorkAssignmentAdvancedSummaryYearNode> BuildYearNodeAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        string yearKey,
        string actorUserId,
        string correlationId,
        CancellationToken ct)
    {
        EnsureSimpleConfigSupported(config.ConfigJson);
        var (startUtc, endExclusiveUtc) = AdvancedSummaryHierarchyKeyHelper.GetYearBoundsUtc(yearKey);
        var expectedMonthKeys = Enumerable.Range(1, 12)
            .Select(month => $"{yearKey}-{month:00}")
            .ToList();
        var childNodes = await LoadCleanMonthNodesForYearAsync(config, yearKey, expectedMonthKeys, ct);
        ValidateCleanChildNodes(
            WorkAssignmentAdvancedSummaryHierarchyGrains.Year,
            yearKey,
            WorkAssignmentAdvancedSummaryHierarchyGrains.Month,
            expectedMonthKeys,
            childNodes,
            x => x.MonthKey);

        var rollup = BuildRollupValue(
            YearNodeValueKind,
            WorkAssignmentAdvancedSummaryHierarchyGrains.Year,
            yearKey,
            monthKey: null,
            yearKey,
            startUtc,
            endExclusiveUtc,
            childNodes,
            x => x.MonthKey);
        var valueJson = JsonSerializer.Serialize(rollup, ValueJsonOptions);
        var now = DateTime.UtcNow;
        var existingNode = await LoadYearNodeAsync(config, yearKey, ct);

        return new WorkAssignmentAdvancedSummaryYearNode
        {
            Id = existingNode?.Id ?? ObjectId.GenerateNewId().ToString(),
            WorkId = config.WorkId,
            AssignmentId = config.AssignmentId,
            DynamicFormTemplateId = config.DynamicFormTemplateId,
            SectionId = config.SectionId,
            ConfigId = config.Id,
            ConfigVersionNo = config.VersionNo,
            ConfigHash = config.ConfigHash,
            Grain = WorkAssignmentAdvancedSummaryHierarchyGrains.Year,
            GrainKey = yearKey,
            YearKey = yearKey,
            WindowStartUtc = startUtc,
            WindowEndExclusiveUtc = endExclusiveUtc,
            Status = WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean,
            IsDirty = false,
            DirtyReason = null,
            SourceSignatureHash = BuildInputNodeSignature(childNodes),
            SourceReportCount = childNodes.Sum(x => x.SourceReportCount),
            SourceReportIds = new List<string>(),
            InputNodeKeys = childNodes.Select(x => x.MonthKey).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            ValueJson = valueJson,
            ValueHash = Sha256(valueJson),
            BuiltAtUtc = now,
            BuildJobId = existingNode?.BuildJobId,
            BuildCorrelationId = correlationId,
            BuildError = null,
            CreatedAtUtc = existingNode?.CreatedAtUtc ?? now,
            CreatedByUserId = existingNode?.CreatedByUserId ?? actorUserId,
            UpdatedAtUtc = now,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };
    }

    private async Task<List<WorkAssignmentAdvancedSummaryDayNode>> LoadCleanDayNodesForMonthAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        string monthKey,
        IReadOnlyCollection<string> expectedDayKeys,
        CancellationToken ct)
    {
        var fb = Builders<WorkAssignmentAdvancedSummaryDayNode>.Filter;
        var filter = fb.Eq(x => x.AssignmentId, config.AssignmentId)
                     & fb.Eq(x => x.DynamicFormTemplateId, config.DynamicFormTemplateId)
                     & fb.Eq(x => x.SectionId, config.SectionId)
                     & fb.Eq(x => x.ConfigHash, config.ConfigHash)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.In(x => x.DayKey, expectedDayKeys);

        return await _ctx.WorkAssignmentAdvancedSummaryDayNodes
            .Find(filter)
            .Sort(Builders<WorkAssignmentAdvancedSummaryDayNode>.Sort.Ascending(x => x.DayKey))
            .ToListAsync(ct);
    }

    private async Task<List<WorkAssignmentAdvancedSummaryMonthNode>> LoadCleanMonthNodesForYearAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        string yearKey,
        IReadOnlyCollection<string> expectedMonthKeys,
        CancellationToken ct)
    {
        var fb = Builders<WorkAssignmentAdvancedSummaryMonthNode>.Filter;
        var filter = fb.Eq(x => x.AssignmentId, config.AssignmentId)
                     & fb.Eq(x => x.DynamicFormTemplateId, config.DynamicFormTemplateId)
                     & fb.Eq(x => x.SectionId, config.SectionId)
                     & fb.Eq(x => x.ConfigHash, config.ConfigHash)
                     & fb.Eq(x => x.YearKey, yearKey)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.In(x => x.MonthKey, expectedMonthKeys);

        return await _ctx.WorkAssignmentAdvancedSummaryMonthNodes
            .Find(filter)
            .Sort(Builders<WorkAssignmentAdvancedSummaryMonthNode>.Sort.Ascending(x => x.MonthKey))
            .ToListAsync(ct);
    }

    private static void ValidateCleanChildNodes<T>(
        string parentGrain,
        string parentKey,
        string childGrain,
        IReadOnlyCollection<string> expectedChildKeys,
        IReadOnlyCollection<T> childNodes,
        Func<T, string> keySelector)
        where T : WorkAssignmentAdvancedSummaryHierarchyNodeBase
    {
        var nodesByKey = childNodes
            .GroupBy(keySelector, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var missing = expectedChildKeys
            .Where(x => !nodesByKey.ContainsKey(x))
            .Take(40)
            .ToList();
        var dirty = childNodes
            .Where(x => !string.Equals(x.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean, StringComparison.Ordinal) ||
                        x.IsDirty ||
                        string.IsNullOrWhiteSpace(x.ValueHash))
            .Select(x => new
            {
                key = keySelector(x),
                x.Status,
                x.IsDirty,
                hasValueHash = !string.IsNullOrWhiteSpace(x.ValueHash)
            })
            .Take(40)
            .ToList();

        if (missing.Count == 0 && dirty.Count == 0)
            return;

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new
            {
                parentGrain,
                parentKey,
                childGrain,
                missingChildKeys = missing,
                dirtyChildNodes = dirty,
                reason = "ADVANCED_SUMMARY_CHILD_NODE_MISSING_OR_DIRTY"
            },
            $"Advanced summary {parentGrain.ToLowerInvariant()} node requires all {childGrain.ToLowerInvariant()} child nodes to be clean before rollup.");
    }

    private static AdvancedSummaryHierarchyNodeValue BuildRollupValue<T>(
        string kind,
        string grain,
        string grainKey,
        string? monthKey,
        string yearKey,
        DateTime windowStartUtc,
        DateTime windowEndExclusiveUtc,
        IReadOnlyCollection<T> childNodes,
        Func<T, string> keySelector)
        where T : WorkAssignmentAdvancedSummaryHierarchyNodeBase
    {
        var orderedChildren = childNodes
            .OrderBy(keySelector, StringComparer.Ordinal)
            .ToList();
        var childValues = orderedChildren
            .Select(ParseHierarchyNodeValue)
            .ToList();
        var fields = RollUpFields(childValues.SelectMany(x => x.Fields));
        var warnings = childValues
            .SelectMany(x => x.Warnings)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .Take(40)
            .ToList();

        if (orderedChildren.Count == 0)
            warnings.Add("No child nodes were available for rollup.");
        if (warnings.Count > 0)
            warnings.Insert(0, $"Rolled up from {orderedChildren.Count} {orderedChildren.FirstOrDefault()?.Grain ?? "child"} node(s).");

        return new AdvancedSummaryHierarchyNodeValue
        {
            SchemaVersion = 1,
            Kind = kind,
            GeneratedAtUtc = DateTime.UtcNow,
            ConfigId = orderedChildren.FirstOrDefault()?.ConfigId ?? string.Empty,
            ConfigHash = orderedChildren.FirstOrDefault()?.ConfigHash ?? string.Empty,
            Grain = grain,
            GrainKey = grainKey,
            MonthKey = monthKey,
            YearKey = yearKey,
            WindowStartUtc = windowStartUtc,
            WindowEndExclusiveUtc = windowEndExclusiveUtc,
            SourceAssignmentCount = childValues.Count == 0 ? 0 : childValues.Max(x => x.SourceAssignmentCount),
            SourceReportCount = orderedChildren.Sum(x => x.SourceReportCount),
            SectionReportCount = childValues.Sum(x => x.SectionReportCount),
            SectionFieldCount = childValues.Count == 0 ? 0 : childValues.Max(x => x.SectionFieldCount),
            TargetFieldCount = fields.Count,
            InputNodeCount = orderedChildren.Count,
            Warnings = warnings,
            Fields = fields
        };
    }

    private static List<AdvancedSummaryDayNodeFieldDto> RollUpFields(IEnumerable<AdvancedSummaryDayNodeFieldDto> childFields)
    {
        var accumulators = new Dictionary<string, HierarchyFieldRollupAccumulator>(StringComparer.Ordinal);
        foreach (var field in childFields)
        {
            if (string.IsNullOrWhiteSpace(field.FieldId))
                continue;

            if (!accumulators.TryGetValue(field.FieldId, out var accumulator))
            {
                accumulator = new HierarchyFieldRollupAccumulator(field);
                accumulators[field.FieldId] = accumulator;
            }

            accumulator.Add(field);
        }

        return accumulators.Values
            .OrderBy(x => x.FieldKey, StringComparer.Ordinal)
            .Select(x => x.ToDto())
            .ToList();
    }

    private static AdvancedSummaryHierarchyNodeValue ParseHierarchyNodeValue(
        WorkAssignmentAdvancedSummaryHierarchyNodeBase node)
    {
        try
        {
            return JsonSerializer.Deserialize<AdvancedSummaryHierarchyNodeValue>(node.ValueJson, JsonOptions)
                   ?? new AdvancedSummaryHierarchyNodeValue();
        }
        catch (JsonException ex)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    nodeId = node.Id,
                    node.Grain,
                    node.GrainKey,
                    reason = "ADVANCED_SUMMARY_CHILD_NODE_VALUE_JSON_INVALID",
                    ex.Message
                },
                "Advanced summary child node value JSON is invalid.");
        }
    }

    private static string BuildInputNodeSignature<T>(IEnumerable<T> childNodes)
        where T : WorkAssignmentAdvancedSummaryHierarchyNodeBase
        => Sha256(string.Join(
            "\n",
            childNodes
                .OrderBy(x => x.GrainKey, StringComparer.Ordinal)
                .Select(x => string.Join(
                    "|",
                    x.Grain,
                    x.GrainKey,
                    x.ValueHash ?? string.Empty,
                    x.SourceSignatureHash ?? string.Empty,
                    x.BuiltAtUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    x.UpdatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)))));

    private static IEnumerable<string> EnumerateDayKeys(DateTime startUtc, DateTime endExclusiveUtc)
    {
        for (var cursor = startUtc.Date; cursor < endExclusiveUtc.Date; cursor = cursor.AddDays(1))
            yield return AdvancedSummaryHierarchyKeyHelper.ToDayKey(cursor);
    }

    private async Task<Dictionary<string, WorkAssignmentAdvancedSummaryDayNode>> LoadQueryDayNodesAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        IReadOnlyCollection<string> dayKeys,
        CancellationToken ct)
    {
        if (dayKeys.Count == 0)
            return new Dictionary<string, WorkAssignmentAdvancedSummaryDayNode>(StringComparer.Ordinal);

        var fb = Builders<WorkAssignmentAdvancedSummaryDayNode>.Filter;
        var filter = fb.Eq(x => x.AssignmentId, config.AssignmentId)
                     & fb.Eq(x => x.DynamicFormTemplateId, config.DynamicFormTemplateId)
                     & fb.Eq(x => x.SectionId, config.SectionId)
                     & fb.Eq(x => x.ConfigHash, config.ConfigHash)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.In(x => x.DayKey, dayKeys);

        return (await _ctx.WorkAssignmentAdvancedSummaryDayNodes.Find(filter).ToListAsync(ct))
            .GroupBy(x => x.DayKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, WorkAssignmentAdvancedSummaryMonthNode>> LoadQueryMonthNodesAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        DateTime startUtc,
        DateTime endExclusiveUtc,
        CancellationToken ct)
    {
        var monthKeys = EnumerateMonthKeys(startUtc, endExclusiveUtc).ToList();
        if (monthKeys.Count == 0)
            return new Dictionary<string, WorkAssignmentAdvancedSummaryMonthNode>(StringComparer.Ordinal);

        var fb = Builders<WorkAssignmentAdvancedSummaryMonthNode>.Filter;
        var filter = fb.Eq(x => x.AssignmentId, config.AssignmentId)
                     & fb.Eq(x => x.DynamicFormTemplateId, config.DynamicFormTemplateId)
                     & fb.Eq(x => x.SectionId, config.SectionId)
                     & fb.Eq(x => x.ConfigHash, config.ConfigHash)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.In(x => x.MonthKey, monthKeys);

        return (await _ctx.WorkAssignmentAdvancedSummaryMonthNodes.Find(filter).ToListAsync(ct))
            .GroupBy(x => x.MonthKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, WorkAssignmentAdvancedSummaryYearNode>> LoadQueryYearNodesAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        DateTime startUtc,
        DateTime endExclusiveUtc,
        CancellationToken ct)
    {
        var yearKeys = EnumerateYearKeys(startUtc, endExclusiveUtc).ToList();
        if (yearKeys.Count == 0)
            return new Dictionary<string, WorkAssignmentAdvancedSummaryYearNode>(StringComparer.Ordinal);

        var fb = Builders<WorkAssignmentAdvancedSummaryYearNode>.Filter;
        var filter = fb.Eq(x => x.AssignmentId, config.AssignmentId)
                     & fb.Eq(x => x.DynamicFormTemplateId, config.DynamicFormTemplateId)
                     & fb.Eq(x => x.SectionId, config.SectionId)
                     & fb.Eq(x => x.ConfigHash, config.ConfigHash)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.In(x => x.YearKey, yearKeys);

        return (await _ctx.WorkAssignmentAdvancedSummaryYearNodes.Find(filter).ToListAsync(ct))
            .GroupBy(x => x.YearKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
    }

    private static List<HierarchyQuerySpan> PlanHierarchyQuerySpans(DateTime startUtc, DateTime endExclusiveUtc)
    {
        var spans = new List<HierarchyQuerySpan>();
        for (var cursor = startUtc.Date; cursor < endExclusiveUtc.Date;)
        {
            if (cursor.Month == 1 && cursor.Day == 1 && cursor.AddYears(1) <= endExclusiveUtc.Date)
            {
                spans.Add(new HierarchyQuerySpan(
                    WorkAssignmentAdvancedSummaryHierarchyGrains.Year,
                    AdvancedSummaryHierarchyKeyHelper.ToYearKey(cursor),
                    cursor,
                    cursor.AddYears(1)));
                cursor = cursor.AddYears(1);
                continue;
            }

            if (cursor.Day == 1 && cursor.AddMonths(1) <= endExclusiveUtc.Date)
            {
                spans.Add(new HierarchyQuerySpan(
                    WorkAssignmentAdvancedSummaryHierarchyGrains.Month,
                    AdvancedSummaryHierarchyKeyHelper.ToMonthKey(cursor),
                    cursor,
                    cursor.AddMonths(1)));
                cursor = cursor.AddMonths(1);
                continue;
            }

            spans.Add(new HierarchyQuerySpan(
                WorkAssignmentAdvancedSummaryHierarchyGrains.Day,
                AdvancedSummaryHierarchyKeyHelper.ToDayKey(cursor),
                cursor,
                cursor.AddDays(1)));
            cursor = cursor.AddDays(1);
        }

        return spans;
    }

    private static bool ResolveQuerySpan(HierarchyQuerySpan span, HierarchyQueryState state)
        => span.Grain switch
        {
            WorkAssignmentAdvancedSummaryHierarchyGrains.Year => ResolveQueryYearSpan(span, state),
            WorkAssignmentAdvancedSummaryHierarchyGrains.Month => ResolveQueryMonthSpan(span, state),
            _ => ResolveQueryDaySpan(span, state)
        };

    private static bool ResolveQueryYearSpan(HierarchyQuerySpan span, HierarchyQueryState state)
    {
        state.YearNodes.TryGetValue(span.GrainKey, out var yearNode);
        if (yearNode is not null && IsCleanHierarchyNode(yearNode))
        {
            state.AddSelected(yearNode);
            return true;
        }

        var before = state.Gaps.Count;
        foreach (var monthKey in EnumerateMonthKeys(span.StartUtc, span.EndExclusiveUtc))
        {
            var monthStart = AdvancedSummaryHierarchyKeyHelper.ParseMonthKey(monthKey);
            ResolveQueryMonthSpan(new HierarchyQuerySpan(
                WorkAssignmentAdvancedSummaryHierarchyGrains.Month,
                monthKey,
                monthStart,
                monthStart.AddMonths(1)), state);
        }

        var coveredByChildren = state.Gaps.Count == before;
        if (coveredByChildren)
        {
            state.AddWarmSpan(span, yearNode);
            return true;
        }

        if (yearNode is not null)
            state.AddGap(span, yearNode);
        return false;
    }

    private static bool ResolveQueryMonthSpan(HierarchyQuerySpan span, HierarchyQueryState state)
    {
        state.MonthNodes.TryGetValue(span.GrainKey, out var monthNode);
        if (monthNode is not null && IsCleanHierarchyNode(monthNode))
        {
            state.AddSelected(monthNode);
            return true;
        }

        var before = state.Gaps.Count;
        foreach (var dayKey in EnumerateDayKeys(span.StartUtc, span.EndExclusiveUtc))
        {
            var dayStart = AdvancedSummaryHierarchyKeyHelper.ParseDayKey(dayKey);
            ResolveQueryDaySpan(new HierarchyQuerySpan(
                WorkAssignmentAdvancedSummaryHierarchyGrains.Day,
                dayKey,
                dayStart,
                dayStart.AddDays(1)), state);
        }

        var coveredByChildren = state.Gaps.Count == before;
        if (coveredByChildren)
        {
            state.AddWarmSpan(span, monthNode);
            return true;
        }

        if (monthNode is not null)
            state.AddGap(span, monthNode);
        return false;
    }

    private static bool ResolveQueryDaySpan(HierarchyQuerySpan span, HierarchyQueryState state)
    {
        state.DayNodes.TryGetValue(span.GrainKey, out var dayNode);
        if (dayNode is not null && IsCleanHierarchyNode(dayNode))
        {
            state.AddSelected(dayNode);
            return true;
        }

        state.AddGap(span, dayNode);
        return false;
    }

    private async Task EnqueueQueryBuildsAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        HierarchyQueryState state,
        string actorUserId,
        CancellationToken ct)
    {
        foreach (var gap in state.Gaps)
        {
            if (gap.Span.Grain != WorkAssignmentAdvancedSummaryHierarchyGrains.Day ||
                !CanAutoEnqueueNode(gap.Node))
            {
                continue;
            }

            var dto = await RequestDayNodeBuildAsync(
                config.Id,
                gap.Span.GrainKey,
                new BuildWorkAssignmentAdvancedSummaryDayNodeRequest { ForceRefresh = gap.Node is not null },
                actorUserId,
                ct);
            state.AddEnqueued(MapQueryNode(dto));
        }

        foreach (var warm in state.WarmSpans)
        {
            if (!CanAutoEnqueueNode(warm.Node))
                continue;

            if (warm.Span.Grain == WorkAssignmentAdvancedSummaryHierarchyGrains.Month)
            {
                var dto = await RequestMonthNodeBuildAsync(
                    config.Id,
                    warm.Span.GrainKey,
                    new BuildWorkAssignmentAdvancedSummaryMonthNodeRequest { ForceRefresh = warm.Node is not null },
                    actorUserId,
                    ct);
                state.AddEnqueued(MapQueryNode(dto));
            }
            else if (warm.Span.Grain == WorkAssignmentAdvancedSummaryHierarchyGrains.Year)
            {
                var dto = await RequestYearNodeBuildAsync(
                    config.Id,
                    warm.Span.GrainKey,
                    new BuildWorkAssignmentAdvancedSummaryYearNodeRequest { ForceRefresh = warm.Node is not null },
                    actorUserId,
                    ct);
                state.AddEnqueued(MapQueryNode(dto));
            }
        }
    }

    private static string ResolveQueryStatus(HierarchyQueryState state)
    {
        if (state.Gaps.Count == 0)
            return "READY";
        if (state.EnqueuedNodes.Count > 0 ||
            state.Gaps.Any(x => string.Equals(x.Node?.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Building, StringComparison.Ordinal)))
        {
            return "BUILDING";
        }
        if (state.Gaps.Any(x => x.Node is not null))
            return "DIRTY";
        return "MISSING";
    }

    private static bool IsCleanHierarchyNode(WorkAssignmentAdvancedSummaryHierarchyNodeBase? node)
        => node is not null &&
           string.Equals(node.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean, StringComparison.Ordinal) &&
           !node.IsDirty &&
           !string.IsNullOrWhiteSpace(node.ValueHash);

    private static bool CanAutoEnqueueNode(WorkAssignmentAdvancedSummaryHierarchyNodeBase? node)
        => node is null ||
           (!string.Equals(node.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Building, StringComparison.Ordinal) &&
            !string.Equals(node.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Failed, StringComparison.Ordinal));

    private static WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto MapQueryGap(HierarchyQueryGap gap)
        => gap.Node is null
            ? new WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto
            {
                Grain = gap.Span.Grain,
                GrainKey = gap.Span.GrainKey,
                Status = "MISSING",
                IsDirty = true
            }
            : MapQueryNode(gap.Node);

    private static WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto MapQueryNode(
        WorkAssignmentAdvancedSummaryHierarchyNodeBase node)
        => new()
        {
            Grain = node.Grain,
            GrainKey = node.GrainKey,
            Status = node.Status,
            IsDirty = node.IsDirty,
            BuildJobId = node.BuildJobId,
            BuildCorrelationId = node.BuildCorrelationId,
            BuildError = node.BuildError,
            ValueHash = node.ValueHash,
            SourceReportCount = node.SourceReportCount
        };

    private static WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto MapQueryNode(
        WorkAssignmentAdvancedSummaryDayNodeDto node)
        => new()
        {
            Grain = node.Grain,
            GrainKey = node.GrainKey,
            Status = node.Status,
            IsDirty = node.IsDirty,
            BuildJobId = node.BuildJobId,
            BuildCorrelationId = node.BuildCorrelationId,
            BuildError = node.BuildError,
            ValueHash = node.ValueHash,
            SourceReportCount = node.SourceReportCount
        };

    private static WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto MapQueryNode(
        WorkAssignmentAdvancedSummaryMonthNodeDto node)
        => new()
        {
            Grain = node.Grain,
            GrainKey = node.GrainKey,
            Status = node.Status,
            IsDirty = node.IsDirty,
            BuildJobId = node.BuildJobId,
            BuildCorrelationId = node.BuildCorrelationId,
            BuildError = node.BuildError,
            ValueHash = node.ValueHash,
            SourceReportCount = node.SourceReportCount
        };

    private static WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto MapQueryNode(
        WorkAssignmentAdvancedSummaryYearNodeDto node)
        => new()
        {
            Grain = node.Grain,
            GrainKey = node.GrainKey,
            Status = node.Status,
            IsDirty = node.IsDirty,
            BuildJobId = node.BuildJobId,
            BuildCorrelationId = node.BuildCorrelationId,
            BuildError = node.BuildError,
            ValueHash = node.ValueHash,
            SourceReportCount = node.SourceReportCount
        };

    private static IEnumerable<string> EnumerateMonthKeys(DateTime startUtc, DateTime endExclusiveUtc)
    {
        var cursor = new DateTime(startUtc.Year, startUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = endExclusiveUtc.Date.AddDays(-1);
        var endMonth = new DateTime(end.Year, end.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        while (cursor <= endMonth)
        {
            yield return AdvancedSummaryHierarchyKeyHelper.ToMonthKey(cursor);
            cursor = cursor.AddMonths(1);
        }
    }

    private static IEnumerable<string> EnumerateYearKeys(DateTime startUtc, DateTime endExclusiveUtc)
    {
        var cursor = new DateTime(startUtc.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = endExclusiveUtc.Date.AddDays(-1);
        var endYear = new DateTime(end.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        while (cursor <= endYear)
        {
            yield return AdvancedSummaryHierarchyKeyHelper.ToYearKey(cursor);
            cursor = cursor.AddYears(1);
        }
    }

    private async Task<List<WorkAssignmentReport>> LoadDaySourceReportsAsync(
        List<string> sourceAssignmentIds,
        string dynamicFormTemplateId,
        string dayKey,
        DateTime dayStartUtc,
        DateTime dayEndExclusiveUtc,
        CancellationToken ct)
    {
        if (sourceAssignmentIds.Count == 0)
            return new List<WorkAssignmentReport>();

        var fb = Builders<WorkAssignmentReport>.Filter;
        var scheduledFilter = fb.Or(
            fb.Eq(x => x.PeriodKind, null),
            fb.Eq(x => x.PeriodKind, WorkReportPeriodKind.Scheduled));
        var coarseDayFilter = fb.Or(
            fb.And(
                fb.Gte(x => x.CompletedDate, dayStartUtc),
                fb.Lt(x => x.CompletedDate, dayEndExclusiveUtc)),
            fb.Eq(x => x.PeriodKey, dayKey),
            fb.And(
                fb.Lt(x => x.PeriodStart, dayEndExclusiveUtc),
                fb.Gte(x => x.PeriodEnd, dayStartUtc)));

        var filter = fb.In(x => x.WorkAssignmentId, sourceAssignmentIds)
                     & fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId)
                     & scheduledFilter
                     & fb.Eq(x => x.Status, WorkAssignmentReportStatus.Approved)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(x => x.IsCurrent, true)
                     & fb.Ne(x => x.IsActive, false)
                     & fb.Ne(x => x.CumulativeContributionMode, WorkReportCumulativeContributionMode.Exclude)
                     & coarseDayFilter;

        var candidates = await _ctx.WorkAssignmentReports
            .Find(filter)
            .Sort(Builders<WorkAssignmentReport>.Sort
                .Ascending(x => x.WorkAssignmentId)
                .Ascending(x => x.AssigneeUserId)
                .Ascending(x => x.Id))
            .Limit(DayNodeSourceReportLimit + 1)
            .ToListAsync(ct);

        var sourceReports = candidates
            .Where(x => string.Equals(ResolveReportSourceDayKey(x), dayKey, StringComparison.Ordinal))
            .Take(DayNodeSourceReportLimit + 1)
            .ToList();

        if (sourceReports.Count > DayNodeSourceReportLimit)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    dayKey,
                    maxSourceReports = DayNodeSourceReportLimit,
                    reason = "ADVANCED_SUMMARY_DAY_NODE_SOURCE_REPORT_LIMIT_EXCEEDED"
                },
                $"Advanced summary day-node source report limit exceeded ({DayNodeSourceReportLimit}).");
        }

        return sourceReports;
    }

    private static string ResolveReportSourceDayKey(WorkAssignmentReport report)
        => AdvancedSummaryReportSourceDayResolver.Resolve(report);

    private async Task<AdvancedSummaryBuildContext> LoadContextAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        string actorUserId,
        CancellationToken ct)
    {
        var scope = await _ctx.WorkAssignments
            .Find(x => x.Id == config.AssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_PARENT_NOT_FOUND, new { config.AssignmentId });

        if (!CanReadAssignment(scope, actorUserId))
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_READ_FORBIDDEN,
                new { config.AssignmentId, actorUserId });
        }

        var template = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == config.DynamicFormTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND, new { config.DynamicFormTemplateId });

        return new AdvancedSummaryBuildContext(scope, template);
    }

    private async Task<WorkAssignmentAdvancedSummaryConfig> LoadLockedConfigAsync(
        string configId,
        CancellationToken ct)
    {
        configId = configId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(configId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_ARGUMENT_REQUIRED, new { field = "configId" });

        var config = await _ctx.WorkAssignmentAdvancedSummaryConfigs
            .Find(x => x.Id == configId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(AppErrorCode.COMMON_NOT_FOUND, new { configId });

        if (config.Status != WorkAssignmentAdvancedSummaryConfigStatuses.Locked)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { configId, config.Status, reason = "ADVANCED_SUMMARY_CONFIG_NOT_LOCKED" },
                "Advanced summary hierarchy nodes can only be built from a locked config.");
        }

        return config;
    }

    private async Task<List<WorkAssignment>> LoadSourceAssignmentsAsync(
        WorkAssignment scope,
        string dynamicFormTemplateId,
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

        var directChildren = await _ctx.WorkAssignments
            .Find(filter)
            .SortBy(x => x.Path)
            .ThenBy(x => x.Code)
            .ToListAsync(ct);

        if (directChildren.Count > 0)
            return directChildren;

        return IsActiveAssignmentForTemplate(scope, dynamicFormTemplateId, sourceAssignmentTypes)
            ? new List<WorkAssignment> { scope }
            : new List<WorkAssignment>();
    }

    private async Task<List<FieldDefinition>> LoadSectionFieldsAsync(
        DynamicFormTemplate template,
        string sectionId,
        CancellationToken ct)
    {
        var section = await _ctx.DynamicFormSections
            .Find(x =>
                x.DynamicFormTemplateId == template.Id &&
                x.SectionId == sectionId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (section is not null)
            return ExtractFieldDefinitions(section.FieldsJson, sectionId, section.FieldIds.ToHashSet(StringComparer.Ordinal));

        return ExtractFieldDefinitions(template.FieldsJson, sectionId, null);
    }

    private async Task<WorkAssignmentAdvancedSummaryDayNode?> LoadDayNodeAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        string dayKey,
        CancellationToken ct)
        => await _ctx.WorkAssignmentAdvancedSummaryDayNodes
            .Find(DayNodeIdentityFilter(config, dayKey))
            .FirstOrDefaultAsync(ct);

    private async Task<WorkAssignmentAdvancedSummaryMonthNode?> LoadMonthNodeAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        string monthKey,
        CancellationToken ct)
        => await _ctx.WorkAssignmentAdvancedSummaryMonthNodes
            .Find(MonthNodeIdentityFilter(config, monthKey))
            .FirstOrDefaultAsync(ct);

    private async Task<WorkAssignmentAdvancedSummaryYearNode?> LoadYearNodeAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        string yearKey,
        CancellationToken ct)
        => await _ctx.WorkAssignmentAdvancedSummaryYearNodes
            .Find(YearNodeIdentityFilter(config, yearKey))
            .FirstOrDefaultAsync(ct);

    private async Task MarkDayNodeBuildingAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        string dayKey,
        string expectedConfigHash,
        string correlationId,
        string actorUserId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await _ctx.WorkAssignmentAdvancedSummaryDayNodes.UpdateOneAsync(
            CurrentDayNodeBuildFilter(config, dayKey, expectedConfigHash, correlationId),
            Builders<WorkAssignmentAdvancedSummaryDayNode>.Update
                .Set(x => x.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Building)
                .Set(x => x.IsDirty, true)
                .Set(x => x.BuildError, (string?)null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
    }

    private static async Task MarkNodeBuildingAsync<T>(
        IMongoCollection<T> collection,
        FilterDefinition<T> filter,
        string actorUserId,
        CancellationToken ct)
        where T : WorkAssignmentAdvancedSummaryHierarchyNodeBase
    {
        var now = DateTime.UtcNow;
        await collection.UpdateOneAsync(
            filter,
            Builders<T>.Update
                .Set(x => x.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Building)
                .Set(x => x.IsDirty, true)
                .Set(x => x.BuildError, (string?)null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
    }

    private static async Task MarkNodeFailedAsync<T>(
        IMongoCollection<T> collection,
        FilterDefinition<T> filter,
        string actorUserId,
        string error,
        CancellationToken ct)
        where T : WorkAssignmentAdvancedSummaryHierarchyNodeBase
    {
        var now = DateTime.UtcNow;
        await collection.UpdateOneAsync(
            filter,
            Builders<T>.Update
                .Set(x => x.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Failed)
                .Set(x => x.IsDirty, true)
                .Set(x => x.BuildError, error)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
    }

    private async Task MarkDayNodeFailedAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        string dayKey,
        string expectedConfigHash,
        string correlationId,
        string actorUserId,
        string error,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await _ctx.WorkAssignmentAdvancedSummaryDayNodes.UpdateOneAsync(
            CurrentDayNodeBuildFilter(config, dayKey, expectedConfigHash, correlationId),
            Builders<WorkAssignmentAdvancedSummaryDayNode>.Update
                .Set(x => x.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Failed)
                .Set(x => x.IsDirty, true)
                .Set(x => x.BuildError, error)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
    }

    private static FilterDefinition<WorkAssignmentAdvancedSummaryDayNode> DayNodeIdentityFilter(
        WorkAssignmentAdvancedSummaryConfig config,
        string dayKey)
    {
        var fb = Builders<WorkAssignmentAdvancedSummaryDayNode>.Filter;
        return fb.Eq(x => x.AssignmentId, config.AssignmentId)
               & fb.Eq(x => x.DynamicFormTemplateId, config.DynamicFormTemplateId)
               & fb.Eq(x => x.SectionId, config.SectionId)
               & fb.Eq(x => x.ConfigHash, config.ConfigHash)
               & fb.Eq(x => x.DayKey, dayKey)
               & fb.Eq(x => x.IsDeleted, false);
    }

    private static FilterDefinition<WorkAssignmentAdvancedSummaryMonthNode> MonthNodeIdentityFilter(
        WorkAssignmentAdvancedSummaryConfig config,
        string monthKey)
    {
        var fb = Builders<WorkAssignmentAdvancedSummaryMonthNode>.Filter;
        return fb.Eq(x => x.AssignmentId, config.AssignmentId)
               & fb.Eq(x => x.DynamicFormTemplateId, config.DynamicFormTemplateId)
               & fb.Eq(x => x.SectionId, config.SectionId)
               & fb.Eq(x => x.ConfigHash, config.ConfigHash)
               & fb.Eq(x => x.MonthKey, monthKey)
               & fb.Eq(x => x.IsDeleted, false);
    }

    private static FilterDefinition<WorkAssignmentAdvancedSummaryYearNode> YearNodeIdentityFilter(
        WorkAssignmentAdvancedSummaryConfig config,
        string yearKey)
    {
        var fb = Builders<WorkAssignmentAdvancedSummaryYearNode>.Filter;
        return fb.Eq(x => x.AssignmentId, config.AssignmentId)
               & fb.Eq(x => x.DynamicFormTemplateId, config.DynamicFormTemplateId)
               & fb.Eq(x => x.SectionId, config.SectionId)
               & fb.Eq(x => x.ConfigHash, config.ConfigHash)
               & fb.Eq(x => x.YearKey, yearKey)
               & fb.Eq(x => x.IsDeleted, false);
    }

    private static FilterDefinition<WorkAssignmentAdvancedSummaryDayNode> CurrentDayNodeBuildFilter(
        WorkAssignmentAdvancedSummaryConfig config,
        string dayKey,
        string expectedConfigHash,
        string correlationId)
    {
        var fb = Builders<WorkAssignmentAdvancedSummaryDayNode>.Filter;
        return DayNodeIdentityFilter(config, dayKey)
               & fb.Eq(x => x.ConfigHash, expectedConfigHash)
               & fb.Eq(x => x.BuildCorrelationId, correlationId);
    }

    private static FilterDefinition<WorkAssignmentAdvancedSummaryMonthNode> CurrentMonthNodeBuildFilter(
        WorkAssignmentAdvancedSummaryConfig config,
        string monthKey,
        string expectedConfigHash,
        string correlationId)
    {
        var fb = Builders<WorkAssignmentAdvancedSummaryMonthNode>.Filter;
        return MonthNodeIdentityFilter(config, monthKey)
               & fb.Eq(x => x.ConfigHash, expectedConfigHash)
               & fb.Eq(x => x.BuildCorrelationId, correlationId);
    }

    private static FilterDefinition<WorkAssignmentAdvancedSummaryYearNode> CurrentYearNodeBuildFilter(
        WorkAssignmentAdvancedSummaryConfig config,
        string yearKey,
        string expectedConfigHash,
        string correlationId)
    {
        var fb = Builders<WorkAssignmentAdvancedSummaryYearNode>.Filter;
        return YearNodeIdentityFilter(config, yearKey)
               & fb.Eq(x => x.ConfigHash, expectedConfigHash)
               & fb.Eq(x => x.BuildCorrelationId, correlationId);
    }

    private static WorkAssignmentAdvancedSummaryDayNodeDto MapDayNode(WorkAssignmentAdvancedSummaryDayNode x)
        => new()
        {
            Id = x.Id,
            WorkId = x.WorkId,
            AssignmentId = x.AssignmentId,
            DynamicFormTemplateId = x.DynamicFormTemplateId,
            SectionId = x.SectionId,
            ConfigId = x.ConfigId,
            ConfigVersionNo = x.ConfigVersionNo,
            ConfigHash = x.ConfigHash,
            Grain = x.Grain,
            GrainKey = x.GrainKey,
            DayKey = x.DayKey,
            Status = x.Status,
            IsDirty = x.IsDirty,
            DirtyReason = x.DirtyReason,
            SourceReportCount = x.SourceReportCount,
            SourceSignatureHash = x.SourceSignatureHash,
            ValueJson = x.ValueJson,
            ValueHash = x.ValueHash,
            BuiltAtUtc = x.BuiltAtUtc,
            BuildJobId = x.BuildJobId,
            BuildCorrelationId = x.BuildCorrelationId,
            BuildError = x.BuildError,
            WindowStartUtc = x.WindowStartUtc,
            WindowEndExclusiveUtc = x.WindowEndExclusiveUtc,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc
        };

    private static WorkAssignmentAdvancedSummaryMonthNodeDto MapMonthNode(WorkAssignmentAdvancedSummaryMonthNode x)
        => new()
        {
            Id = x.Id,
            WorkId = x.WorkId,
            AssignmentId = x.AssignmentId,
            DynamicFormTemplateId = x.DynamicFormTemplateId,
            SectionId = x.SectionId,
            ConfigId = x.ConfigId,
            ConfigVersionNo = x.ConfigVersionNo,
            ConfigHash = x.ConfigHash,
            Grain = x.Grain,
            GrainKey = x.GrainKey,
            MonthKey = x.MonthKey,
            YearKey = x.YearKey,
            Status = x.Status,
            IsDirty = x.IsDirty,
            DirtyReason = x.DirtyReason,
            SourceReportCount = x.SourceReportCount,
            SourceSignatureHash = x.SourceSignatureHash,
            ValueJson = x.ValueJson,
            ValueHash = x.ValueHash,
            BuiltAtUtc = x.BuiltAtUtc,
            BuildJobId = x.BuildJobId,
            BuildCorrelationId = x.BuildCorrelationId,
            BuildError = x.BuildError,
            WindowStartUtc = x.WindowStartUtc,
            WindowEndExclusiveUtc = x.WindowEndExclusiveUtc,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc
        };

    private static WorkAssignmentAdvancedSummaryYearNodeDto MapYearNode(WorkAssignmentAdvancedSummaryYearNode x)
        => new()
        {
            Id = x.Id,
            WorkId = x.WorkId,
            AssignmentId = x.AssignmentId,
            DynamicFormTemplateId = x.DynamicFormTemplateId,
            SectionId = x.SectionId,
            ConfigId = x.ConfigId,
            ConfigVersionNo = x.ConfigVersionNo,
            ConfigHash = x.ConfigHash,
            Grain = x.Grain,
            GrainKey = x.GrainKey,
            YearKey = x.YearKey,
            Status = x.Status,
            IsDirty = x.IsDirty,
            DirtyReason = x.DirtyReason,
            SourceReportCount = x.SourceReportCount,
            SourceSignatureHash = x.SourceSignatureHash,
            ValueJson = x.ValueJson,
            ValueHash = x.ValueHash,
            BuiltAtUtc = x.BuiltAtUtc,
            BuildJobId = x.BuildJobId,
            BuildCorrelationId = x.BuildCorrelationId,
            BuildError = x.BuildError,
            WindowStartUtc = x.WindowStartUtc,
            WindowEndExclusiveUtc = x.WindowEndExclusiveUtc,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc
        };

    private static WorkAssignmentAdvancedSummaryDayNodeDiagnosticSnapshot ToDayNodeDiagnosticSnapshot(
        WorkAssignmentAdvancedSummaryDayNode x,
        bool includeValueJson)
    {
        var comparableValueHash = TryBuildAdvancedSummaryComparableValueHash(
            x.ValueJson,
            out var valueHash,
            out var valueError)
            ? valueHash
            : null;

        return new WorkAssignmentAdvancedSummaryDayNodeDiagnosticSnapshot
        {
            NodeId = x.Id,
            Status = x.Status,
            IsDirty = x.IsDirty,
            SourceReportCount = x.SourceReportCount,
            SourceSignatureHash = x.SourceSignatureHash,
            ValueHash = x.ValueHash,
            ComparableValueHash = comparableValueHash,
            ComparableValueError = valueError,
            BuiltAtUtc = x.BuiltAtUtc,
            BuildJobId = x.BuildJobId,
            BuildCorrelationId = x.BuildCorrelationId,
            BuildError = x.BuildError,
            WindowStartUtc = x.WindowStartUtc,
            WindowEndExclusiveUtc = x.WindowEndExclusiveUtc,
            ValueJson = includeValueJson ? x.ValueJson : null
        };
    }

    private static List<string> CompareDayNodeDiagnostics(
        WorkAssignmentAdvancedSummaryDayNode? cacheNode,
        WorkAssignmentAdvancedSummaryDayNode directNode)
    {
        var differences = new List<string>();
        if (cacheNode is null)
        {
            differences.Add("CACHE_MISSING");
            return differences;
        }

        if (cacheNode.IsDirty)
            differences.Add("CACHE_DIRTY");
        if (!string.Equals(cacheNode.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean, StringComparison.Ordinal))
            differences.Add($"CACHE_STATUS_{cacheNode.Status}");
        if (cacheNode.SourceReportCount != directNode.SourceReportCount)
            differences.Add("SOURCE_REPORT_COUNT");
        if (!string.Equals(cacheNode.SourceSignatureHash, directNode.SourceSignatureHash, StringComparison.Ordinal))
            differences.Add("SOURCE_SIGNATURE_HASH");
        if (!cacheNode.SourceReportIds.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(
                directNode.SourceReportIds.OrderBy(x => x, StringComparer.Ordinal),
                StringComparer.Ordinal))
            differences.Add("SOURCE_REPORT_IDS");

        var cacheComparableOk = TryBuildAdvancedSummaryComparableValueHash(
            cacheNode.ValueJson,
            out var cacheComparableHash,
            out _);
        var directComparableOk = TryBuildAdvancedSummaryComparableValueHash(
            directNode.ValueJson,
            out var directComparableHash,
            out _);
        if (!cacheComparableOk)
            differences.Add("CACHE_VALUE_JSON_INVALID");
        if (!directComparableOk)
            differences.Add("DIRECT_VALUE_JSON_INVALID");
        if (cacheComparableOk &&
            directComparableOk &&
            !string.Equals(cacheComparableHash, directComparableHash, StringComparison.Ordinal))
        {
            differences.Add("COMPARABLE_VALUE_HASH");
        }

        return differences;
    }

    private static string ResolveDayNodeDiagnosticsStatus(
        WorkAssignmentAdvancedSummaryDayNode? cacheNode,
        IReadOnlyCollection<string> differences)
    {
        if (cacheNode is null)
            return "CACHE_MISSING";
        if (string.Equals(cacheNode.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Building, StringComparison.Ordinal))
            return "CACHE_BUILDING";
        if (string.Equals(cacheNode.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Failed, StringComparison.Ordinal))
            return "CACHE_FAILED";
        if (cacheNode.IsDirty ||
            string.Equals(cacheNode.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Dirty, StringComparison.Ordinal))
        {
            return "CACHE_DIRTY";
        }

        return differences.Count == 0 ? "MATCH" : "MISMATCH";
    }

    private static string ResolveDiagnosticsActor(
        WorkAssignmentAdvancedSummaryConfig config,
        WorkAssignmentAdvancedSummaryDayNode? cacheNode,
        string fallbackUserId)
    {
        if (!string.IsNullOrWhiteSpace(cacheNode?.UpdatedByUserId))
            return cacheNode.UpdatedByUserId;
        if (!string.IsNullOrWhiteSpace(cacheNode?.CreatedByUserId))
            return cacheNode.CreatedByUserId;
        if (!string.IsNullOrWhiteSpace(config.LockedByUserId))
            return config.LockedByUserId;
        if (!string.IsNullOrWhiteSpace(config.UpdatedByUserId))
            return config.UpdatedByUserId;
        if (!string.IsNullOrWhiteSpace(config.CreatedByUserId))
            return config.CreatedByUserId;

        return fallbackUserId;
    }

    private static bool TryBuildAdvancedSummaryComparableValueHash(
        string? valueJson,
        out string? hash,
        out string? error)
    {
        hash = null;
        error = null;
        try
        {
            hash = BuildAdvancedSummaryComparableValueHash(valueJson);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or AppException)
        {
            error = BuildErrorMessage(ex);
            return false;
        }
    }

    internal static string BuildAdvancedSummaryComparableValueHash(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
            throw new JsonException("Advanced summary value JSON is empty.");

        var value = JsonSerializer.Deserialize<AdvancedSummaryHierarchyNodeValue>(valueJson, JsonOptions)
                    ?? throw new JsonException("Advanced summary value JSON is empty.");
        var comparable = new AdvancedSummaryComparableNodeValue
        {
            SchemaVersion = value.SchemaVersion,
            Kind = value.Kind,
            ConfigId = value.ConfigId,
            ConfigHash = value.ConfigHash,
            Grain = value.Grain,
            GrainKey = value.GrainKey,
            DayKey = value.DayKey,
            MonthKey = value.MonthKey,
            YearKey = value.YearKey,
            WindowStartUtc = value.WindowStartUtc,
            WindowEndExclusiveUtc = value.WindowEndExclusiveUtc,
            SourceAssignmentCount = value.SourceAssignmentCount,
            SourceReportCount = value.SourceReportCount,
            SectionReportCount = value.SectionReportCount,
            SectionFieldCount = value.SectionFieldCount,
            TargetFieldCount = value.TargetFieldCount,
            InputNodeCount = value.InputNodeCount,
            Warnings = value.Warnings.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Fields = value.Fields
                .OrderBy(x => x.FieldKey, StringComparer.Ordinal)
                .ThenBy(x => x.FieldId, StringComparer.Ordinal)
                .ToList()
        };

        return Sha256(JsonSerializer.Serialize(comparable, ValueJsonOptions));
    }

    private Task NotifyDayNodeBuildAsync(
        WorkAssignmentAdvancedSummaryDayNode? node,
        string actorUserId,
        WorkAssignment? scope,
        DynamicFormTemplate? template,
        string eventStatus,
        string title,
        string stateText,
        string severity,
        bool requiresAction,
        string? error,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorUserId) || node is null)
            return Task.CompletedTask;

        var correlationId = string.IsNullOrWhiteSpace(node.BuildCorrelationId) ? node.Id : node.BuildCorrelationId;
        return _notifications.CreateManyAsync(new[]
        {
            new NotificationCommand
            {
                RecipientUserId = actorUserId,
                Type = UserNotificationTypes.AdvancedSummaryHierarchyBuild,
                Severity = severity,
                Title = title,
                Body = BuildDayNodeNotificationBody(stateText, node, scope, template, correlationId, error),
                WorkId = node.WorkId,
                WorkAssignmentId = node.AssignmentId,
                AssignmentCode = scope?.Code,
                Category = UserNotificationCategories.Status,
                RequiresAction = requiresAction,
                ActionState = requiresAction
                    ? UserNotificationActionStates.Open
                    : UserNotificationActionStates.Resolved,
                SourceEntityType = "WORK_ASSIGNMENT_ADVANCED_SUMMARY_DAY_NODE",
                SourceEntityId = node.Id,
                RequestId = ObjectId.TryParse(correlationId, out _) ? correlationId : null,
                ActorUserId = actorUserId,
                TargetUserId = actorUserId,
                OccurredAtUtc = DateTime.UtcNow,
                EventKey = $"advanced-summary-day-node:{eventStatus}:{node.Id}:{correlationId}"
            }
        }, ct);
    }

    private static string BuildDayNodeNotificationBody(
        string stateText,
        WorkAssignmentAdvancedSummaryDayNode node,
        WorkAssignment? scope,
        DynamicFormTemplate? template,
        string correlationId,
        string? error)
    {
        var templateLabel = PickNonBlank(template?.Code, template?.Name, template?.Id, node.DynamicFormTemplateId) ?? "-";
        var scopeLabel = PickNonBlank(scope?.Code, scope?.Name, scope?.Id, node.AssignmentId) ?? "-";
        var body = $"Template {templateLabel}, assignment {scopeLabel}, day {node.DayKey}: {stateText}. Correlation: {correlationId}.";
        if (!string.IsNullOrWhiteSpace(error))
            body += $" Error: {error.Trim()}";

        return body.Length <= 1800 ? body : body[..1800];
    }

    private Task NotifyHierarchyNodeBuildAsync(
        WorkAssignmentAdvancedSummaryHierarchyNodeBase? node,
        string actorUserId,
        WorkAssignment? scope,
        DynamicFormTemplate? template,
        string eventStatus,
        string title,
        string stateText,
        string severity,
        bool requiresAction,
        string? error,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorUserId) || node is null)
            return Task.CompletedTask;

        var grain = string.IsNullOrWhiteSpace(node.Grain) ? "NODE" : node.Grain;
        var correlationId = string.IsNullOrWhiteSpace(node.BuildCorrelationId) ? node.Id : node.BuildCorrelationId;
        return _notifications.CreateManyAsync(new[]
        {
            new NotificationCommand
            {
                RecipientUserId = actorUserId,
                Type = UserNotificationTypes.AdvancedSummaryHierarchyBuild,
                Severity = severity,
                Title = title,
                Body = BuildHierarchyNodeNotificationBody(stateText, node, scope, template, correlationId, error),
                WorkId = node.WorkId,
                WorkAssignmentId = node.AssignmentId,
                AssignmentCode = scope?.Code,
                Category = UserNotificationCategories.Status,
                RequiresAction = requiresAction,
                ActionState = requiresAction
                    ? UserNotificationActionStates.Open
                    : UserNotificationActionStates.Resolved,
                SourceEntityType = $"WORK_ASSIGNMENT_ADVANCED_SUMMARY_{grain}_NODE",
                SourceEntityId = node.Id,
                RequestId = ObjectId.TryParse(correlationId, out _) ? correlationId : null,
                ActorUserId = actorUserId,
                TargetUserId = actorUserId,
                OccurredAtUtc = DateTime.UtcNow,
                EventKey = $"advanced-summary-{grain.ToLowerInvariant()}-node:{eventStatus}:{node.Id}:{correlationId}"
            }
        }, ct);
    }

    private static string BuildHierarchyNodeNotificationBody(
        string stateText,
        WorkAssignmentAdvancedSummaryHierarchyNodeBase node,
        WorkAssignment? scope,
        DynamicFormTemplate? template,
        string correlationId,
        string? error)
    {
        var templateLabel = PickNonBlank(template?.Code, template?.Name, template?.Id, node.DynamicFormTemplateId) ?? "-";
        var scopeLabel = PickNonBlank(scope?.Code, scope?.Name, scope?.Id, node.AssignmentId) ?? "-";
        var grainLabel = string.IsNullOrWhiteSpace(node.Grain) ? "node" : node.Grain.ToLowerInvariant();
        var body = $"Template {templateLabel}, assignment {scopeLabel}, {grainLabel} {node.GrainKey}: {stateText}. Correlation: {correlationId}.";
        if (!string.IsNullOrWhiteSpace(error))
            body += $" Error: {error.Trim()}";

        return body.Length <= 1800 ? body : body[..1800];
    }

    private static void EnsureSimpleConfigSupported(string configJson)
    {
        var analysis = AnalyzeSimpleConfig(configJson, new List<FieldDefinition>());
        if (analysis.UnsupportedFeatures.Count > 0)
            throw UnsupportedConfig(analysis.UnsupportedFeatures);
    }

    private static SimpleConfigAnalysis AnalyzeSimpleConfig(
        string configJson,
        List<FieldDefinition> sectionFields)
    {
        var targets = new Dictionary<string, TargetSpec>(StringComparer.Ordinal);
        var unknownFieldRefs = new HashSet<string>(StringComparer.Ordinal);
        var unsupported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownRefs = new HashSet<string>(
            sectionFields.SelectMany(x => new[] { x.FieldId, x.FieldKey }),
            StringComparer.Ordinal);

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson);
            CollectSimpleConfig(document.RootElement, targets, unknownFieldRefs, unsupported, knownRefs);
        }
        catch (JsonException ex)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "configJson", reason = "ADVANCED_SUMMARY_CONFIG_JSON_INVALID", ex.Message });
        }

        return new SimpleConfigAnalysis(
            targets.Values.ToList(),
            unknownFieldRefs.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            unsupported.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            targets.Count > 0);
    }

    private static void CollectSimpleConfig(
        JsonElement element,
        Dictionary<string, TargetSpec> targets,
        HashSet<string> unknownFieldRefs,
        HashSet<string> unsupported,
        HashSet<string> knownRefs)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectSimpleConfig(item, targets, unknownFieldRefs, unsupported, knownRefs);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        var fieldRef = ReadStringProperty(element, "fieldId")
                       ?? ReadStringProperty(element, "targetFieldId")
                       ?? ReadStringProperty(element, "fieldKey");
        if (!string.IsNullOrWhiteSpace(fieldRef))
        {
            var method = ReadStringProperty(element, "method")
                         ?? ReadStringProperty(element, "operation")
                         ?? ReadStringProperty(element, "statistic");
            AddTarget(fieldRef, method, targets, unknownFieldRefs, knownRefs);
        }

        foreach (var prop in element.EnumerateObject())
        {
            var name = prop.Name.Trim();
            if (IsUnsupportedFeatureName(name))
                unsupported.Add(name);

            if (IsFieldIdArrayName(name) && prop.Value.ValueKind == JsonValueKind.Array)
            {
                var method = ReadStringProperty(element, "method")
                             ?? ReadStringProperty(element, "operation")
                             ?? ReadStringProperty(element, "statistic");
                foreach (var item in prop.Value.EnumerateArray())
                {
                    var itemRef = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;
                    AddTarget(itemRef, method, targets, unknownFieldRefs, knownRefs);
                }
            }

            if (IsMethodMapName(name) && prop.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in prop.Value.EnumerateObject())
                {
                    var method = item.Value.ValueKind == JsonValueKind.String
                        ? item.Value.GetString()
                        : ReadStringProperty(item.Value, "method")
                          ?? ReadStringProperty(item.Value, "operation")
                          ?? ReadStringProperty(item.Value, "statistic");
                    AddTarget(item.Name, method, targets, unknownFieldRefs, knownRefs);
                }
            }

            CollectSimpleConfig(prop.Value, targets, unknownFieldRefs, unsupported, knownRefs);
        }
    }

    private static void AddTarget(
        string? fieldRef,
        string? method,
        Dictionary<string, TargetSpec> targets,
        HashSet<string> unknownFieldRefs,
        HashSet<string> knownRefs)
    {
        fieldRef = fieldRef?.Trim();
        if (string.IsNullOrWhiteSpace(fieldRef))
            return;

        if (knownRefs.Count > 0 && !knownRefs.Contains(fieldRef))
        {
            unknownFieldRefs.Add(fieldRef);
            return;
        }

        targets[fieldRef] = new TargetSpec(fieldRef, method?.Trim());
    }

    private static AppException UnsupportedConfig(IReadOnlyCollection<string> unsupportedFeatures)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new
            {
                unsupportedFeatures,
                reason = "ADVANCED_SUMMARY_HIERARCHY_ENGINE_NOT_READY"
            },
            "Advanced summary hierarchy builder currently supports simple section field methods only. Data range, condition, and filter execution will be added in a later phase.");

    private static List<Target> ResolveTargets(
        SimpleConfigAnalysis analysis,
        List<FieldDefinition> sectionFields,
        List<string> warnings)
    {
        var fieldsById = sectionFields
            .GroupBy(x => x.FieldId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var fieldsByKey = sectionFields
            .Where(x => !string.Equals(x.FieldId, x.FieldKey, StringComparison.Ordinal))
            .GroupBy(x => x.FieldKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        if (!analysis.HasExplicitTargets)
            return sectionFields
                .Select(x => new Target(x, DefaultMethod(x.FieldType)))
                .ToList();

        var output = new Dictionary<string, Target>(StringComparer.Ordinal);
        foreach (var target in analysis.Targets)
        {
            if (!fieldsById.TryGetValue(target.FieldRef, out var field) &&
                !fieldsByKey.TryGetValue(target.FieldRef, out field))
            {
                warnings.Add($"Config target is outside this section and was ignored: {target.FieldRef}");
                continue;
            }

            output[field.FieldId] = new Target(field, NormalizeMethod(target.Method, field.FieldType, warnings));
        }

        return output.Values.ToList();
    }

    private static List<FieldDefinition> ExtractFieldDefinitions(
        string? fieldsJson,
        string sectionId,
        HashSet<string>? allowedFieldIds)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
            return new List<FieldDefinition>();

        try
        {
            var rawFields = JsonSerializer.Deserialize<List<DynamicFormFieldDefinition>>(fieldsJson, JsonOptions)
                            ?? new List<DynamicFormFieldDefinition>();

            return rawFields
                .Select(x => ToFieldDefinition(x, sectionId, allowedFieldIds))
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

    private static FieldDefinition? ToFieldDefinition(
        DynamicFormFieldDefinition field,
        string sectionId,
        HashSet<string>? allowedFieldIds)
    {
        var fieldId = field.Id?.Trim();
        if (string.IsNullOrWhiteSpace(fieldId))
            return null;

        if (allowedFieldIds is not null && !allowedFieldIds.Contains(fieldId))
            return null;

        if (allowedFieldIds is null)
        {
            var fieldSectionId = field.SectionId?.Trim();
            if (!string.IsNullOrWhiteSpace(fieldSectionId) &&
                !string.Equals(fieldSectionId, sectionId, StringComparison.Ordinal))
            {
                return null;
            }
        }

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

    private static bool IsUnsupportedFeatureName(string name)
    {
        var normalized = name.Trim().ToLowerInvariant();
        return normalized is "condition" or "conditions" or "datarange" or "dataranges" or
            "data_range" or "data_ranges" or "filter" or "filters" or "range" or "ranges";
    }

    private static bool IsFieldIdArrayName(string name)
    {
        var normalized = name.Trim().ToLowerInvariant();
        return normalized is "fieldids" or "field_ids" or "fields";
    }

    private static bool IsMethodMapName(string name)
    {
        var normalized = name.Trim().ToLowerInvariant();
        return normalized is "methods" or "operations" or "statistics";
    }

    private static string? ReadStringProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var prop in element.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            return prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString()?.Trim()
                : null;
        }

        return null;
    }

    private static string DefaultMethod(string fieldType)
        => FieldTypeToDataType(fieldType) switch
        {
            "NUMBER" => "SUM",
            "DATE" => "MAX_DATE",
            "BOOLEAN" => "TRUE_COUNT",
            "SINGLE_SELECT" or "MULTI_SELECT" => "BUCKET_COUNT",
            _ => "COUNT"
        };

    private static string NormalizeMethod(string? method, string fieldType, List<string> warnings)
    {
        var dataType = FieldTypeToDataType(fieldType);
        var normalized = string.IsNullOrWhiteSpace(method)
            ? DefaultMethod(fieldType)
            : method.Trim().ToUpperInvariant();

        var allowed = dataType switch
        {
            "NUMBER" => new HashSet<string>(StringComparer.Ordinal) { "SUM", "COUNT", "MEAN", "MIN", "MAX" },
            "DATE" => new HashSet<string>(StringComparer.Ordinal) { "MAX_DATE", "MIN_DATE", "COUNT" },
            "BOOLEAN" => new HashSet<string>(StringComparer.Ordinal) { "TRUE_COUNT", "FALSE_COUNT", "COUNT" },
            "SINGLE_SELECT" or "MULTI_SELECT" => new HashSet<string>(StringComparer.Ordinal) { "BUCKET_COUNT", "COUNT" },
            _ => new HashSet<string>(StringComparer.Ordinal) { "COUNT", "JOIN" }
        };

        if (allowed.Contains(normalized))
            return normalized;

        var fallback = DefaultMethod(fieldType);
        warnings.Add($"Unsupported day-node method '{normalized}' for data type {dataType}; fallback to {fallback}.");
        return fallback;
    }

    private static string FieldTypeToDataType(string fieldType)
        => fieldType switch
        {
            "number" => "NUMBER",
            "date" or "dateTime" => "DATE",
            "boolean" => "BOOLEAN",
            "singleSelect" => "SINGLE_SELECT",
            "multiSelect" => "MULTI_SELECT",
            "stringList" => "STRING_LIST",
            _ => "TEXT"
        };

    private static bool TryGetValuesObject(string? fieldValuesJson, out JsonElement valuesObject)
    {
        valuesObject = default;
        if (string.IsNullOrWhiteSpace(fieldValuesJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(fieldValuesJson);
            var root = document.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("values", out var nestedValues) &&
                nestedValues.ValueKind == JsonValueKind.Object)
            {
                valuesObject = nestedValues.Clone();
                return true;
            }

            valuesObject = root;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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

    private static bool IsBlankJsonElement(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;
        if (value.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(value.GetString());
        if (value.ValueKind == JsonValueKind.Array)
            return !value.EnumerateArray().Any(x => !IsBlankJsonElement(x));
        return false;
    }

    private static List<string> ReadDisplayValues(JsonElement value, FieldDefinition field)
    {
        if (field.FieldType == "multiSelect" || field.FieldType == "stringList")
            return ReadStringListValues(value)
                .Select(x => ResolveOptionLabel(field.Options, x))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

        if (field.FieldType == "singleSelect")
        {
            var code = ToNullableString(value);
            return string.IsNullOrWhiteSpace(code)
                ? new List<string>()
                : new List<string> { ResolveOptionLabel(field.Options, code) };
        }

        var text = ToNullableString(value);
        return string.IsNullOrWhiteSpace(text) ? new List<string>() : new List<string> { text };
    }

    private static IEnumerable<string> ReadStringListValues(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var text = ToNullableString(item);
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text.Trim();
            }
            yield break;
        }

        var single = ToNullableString(value);
        if (!string.IsNullOrWhiteSpace(single))
            yield return single.Trim();
    }

    private static string ResolveOptionLabel(IReadOnlyCollection<FieldOption> options, string code)
    {
        code = code.Trim();
        return options.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.Ordinal))?.Label ?? code;
    }

    private static string? ToNullableString(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetDecimal(out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };

    private static decimal? ToNullableDecimal(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool TryReadBoolean(JsonElement value, out bool result)
    {
        if (value.ValueKind == JsonValueKind.True)
        {
            result = true;
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            result = false;
            return true;
        }

        if (value.ValueKind == JsonValueKind.String &&
            bool.TryParse(value.GetString(), out result))
        {
            return true;
        }

        result = false;
        return false;
    }

    private static DateTime? ReadDateValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(
                value.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static decimal? ToNullableDecimalResult(object? value)
        => value switch
        {
            null => null,
            JsonElement element => ToNullableDecimal(element),
            decimal d => d,
            double d => (decimal)d,
            float f => (decimal)f,
            int i => i,
            long l => l,
            short s => s,
            byte b => b,
            string text when decimal.TryParse(
                text,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null
        };

    private static long? ToNullableLongResult(object? value)
    {
        var number = ToNullableDecimalResult(value);
        return number.HasValue ? (long)number.Value : null;
    }

    private static DateTime? ToNullableDateResult(object? value)
    {
        if (value is JsonElement element)
            return ReadDateValue(element);

        if (value is DateTime date)
            return date;

        if (value is string text &&
            DateTime.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static Dictionary<string, long> ReadBucketCountsResult(object? value)
    {
        var output = new Dictionary<string, long>(StringComparer.Ordinal);
        if (value is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return output;

            foreach (var prop in element.EnumerateObject())
            {
                var count = ToNullableLongResult(prop.Value);
                if (count.HasValue)
                    output[prop.Name] = count.Value;
            }

            return output;
        }

        if (value is IDictionary<string, int> intDict)
        {
            foreach (var item in intDict)
                output[item.Key] = item.Value;
            return output;
        }

        if (value is IDictionary<string, long> longDict)
        {
            foreach (var item in longDict)
                output[item.Key] = item.Value;
            return output;
        }

        if (value is IDictionary<string, object?> objectDict)
        {
            foreach (var item in objectDict)
            {
                var count = ToNullableLongResult(item.Value);
                if (count.HasValue)
                    output[item.Key] = count.Value;
            }
        }

        return output;
    }

    private static string BuildSourceSignaturePart(WorkAssignmentReport report, string? sectionPayloadHash)
        => string.Join(
            "|",
            report.Id,
            report.PayloadHash ?? string.Empty,
            report.PayloadRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            report.PayloadUpdatedAtUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            sectionPayloadHash ?? string.Empty,
            report.UpdatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    private static string NormalizeDayKey(string dayKey)
        => AdvancedSummaryHierarchyKeyHelper.ToDayKey(AdvancedSummaryHierarchyKeyHelper.ParseDayKey(dayKey));

    private static string NormalizeQueryDayKey(string dayKey, string field)
    {
        try
        {
            return NormalizeDayKey(dayKey);
        }
        catch (ArgumentException)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field, reason = "ADVANCED_SUMMARY_QUERY_DAY_KEY_INVALID" },
                "Advanced summary query day key must use yyyy-MM-dd.");
        }
    }

    private static string NormalizeMonthKey(string monthKey)
        => AdvancedSummaryHierarchyKeyHelper.ToMonthKey(AdvancedSummaryHierarchyKeyHelper.ParseMonthKey(monthKey));

    private static string NormalizeYearKey(string yearKey)
        => AdvancedSummaryHierarchyKeyHelper.ToYearKey(AdvancedSummaryHierarchyKeyHelper.ParseYearKey(yearKey));

    private static string NormalizeFieldType(string? value)
        => string.IsNullOrWhiteSpace(value) ? "text" : value.Trim();

    private static string? PickNonBlank(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static string[] ResolveSourceAssignmentTypes(string? scopeAssignmentType)
        => string.Equals(scopeAssignmentType, WorkAssignmentTypes.PeriodicReport, StringComparison.OrdinalIgnoreCase)
            ? new[] { WorkAssignmentTypes.PeriodicReport }
            : new[] { WorkAssignmentTypes.Once };

    private static bool IsActiveAssignmentForTemplate(
        WorkAssignment assignment,
        string dynamicFormTemplateId,
        string[] supportedAssignmentTypes)
        => assignment.IsActive &&
           !assignment.IsDeleted &&
           supportedAssignmentTypes.Contains(assignment.AssignmentType, StringComparer.OrdinalIgnoreCase) &&
           string.Equals(assignment.DynamicFormTemplateId?.Trim(), dynamicFormTemplateId, StringComparison.Ordinal);

    private static bool CanReadAssignment(WorkAssignment assignment, string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            return false;

        return string.Equals(assignment.CreatedByUserId, actorUserId, StringComparison.Ordinal)
               || assignment.LeaderWatcherUserIds.Contains(actorUserId)
               || assignment.Assignees.Any(x => string.Equals(x.UserId, actorUserId, StringComparison.Ordinal));
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string BuildErrorMessage(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        if (string.IsNullOrWhiteSpace(message))
            message = ex.GetType().Name;

        message = message.Trim();
        return message.Length <= 2000 ? message : message[..2000];
    }

    private static void EnsureActor(string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            throw AppExceptionFactory.Unauthorized();
    }

    private sealed record AdvancedSummaryBuildContext(
        WorkAssignment Scope,
        DynamicFormTemplate Template);

    private sealed record HierarchyQuerySpan(
        string Grain,
        string GrainKey,
        DateTime StartUtc,
        DateTime EndExclusiveUtc);

    private sealed record HierarchyQueryGap(
        HierarchyQuerySpan Span,
        WorkAssignmentAdvancedSummaryHierarchyNodeBase? Node);

    private sealed record HierarchyQueryWarmSpan(
        HierarchyQuerySpan Span,
        WorkAssignmentAdvancedSummaryHierarchyNodeBase? Node);

    private sealed class HierarchyQueryState
    {
        private readonly HashSet<string> _selectedKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _gapKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _warmKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _enqueuedKeys = new(StringComparer.Ordinal);

        public HierarchyQueryState(
            Dictionary<string, WorkAssignmentAdvancedSummaryDayNode> dayNodes,
            Dictionary<string, WorkAssignmentAdvancedSummaryMonthNode> monthNodes,
            Dictionary<string, WorkAssignmentAdvancedSummaryYearNode> yearNodes)
        {
            DayNodes = dayNodes;
            MonthNodes = monthNodes;
            YearNodes = yearNodes;
        }

        public Dictionary<string, WorkAssignmentAdvancedSummaryDayNode> DayNodes { get; }
        public Dictionary<string, WorkAssignmentAdvancedSummaryMonthNode> MonthNodes { get; }
        public Dictionary<string, WorkAssignmentAdvancedSummaryYearNode> YearNodes { get; }
        public List<WorkAssignmentAdvancedSummaryHierarchyNodeBase> SelectedNodes { get; } = new();
        public List<HierarchyQueryGap> Gaps { get; } = new();
        public List<HierarchyQueryWarmSpan> WarmSpans { get; } = new();
        public List<WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto> EnqueuedNodes { get; } = new();

        public void AddSelected(WorkAssignmentAdvancedSummaryHierarchyNodeBase node)
        {
            if (_selectedKeys.Add(NodeKey(node.Grain, node.GrainKey)))
                SelectedNodes.Add(node);
        }

        public void AddGap(HierarchyQuerySpan span, WorkAssignmentAdvancedSummaryHierarchyNodeBase? node)
        {
            if (_gapKeys.Add(NodeKey(span.Grain, span.GrainKey)))
                Gaps.Add(new HierarchyQueryGap(span, node));
        }

        public void AddWarmSpan(HierarchyQuerySpan span, WorkAssignmentAdvancedSummaryHierarchyNodeBase? node)
        {
            if (_warmKeys.Add(NodeKey(span.Grain, span.GrainKey)))
                WarmSpans.Add(new HierarchyQueryWarmSpan(span, node));
        }

        public void AddEnqueued(WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto node)
        {
            if (_enqueuedKeys.Add(NodeKey(node.Grain, node.GrainKey)))
                EnqueuedNodes.Add(node);
        }

        private static string NodeKey(string grain, string key) => $"{grain}:{key}";
    }

    private sealed record SimpleConfigAnalysis(
        List<TargetSpec> Targets,
        List<string> UnknownFieldRefs,
        List<string> UnsupportedFeatures,
        bool HasExplicitTargets);

    private sealed record TargetSpec(string FieldRef, string? Method);

    private sealed record Target(FieldDefinition Field, string Method);

    private sealed record FieldOption(string Code, string Label);

    private sealed record FieldDefinition(
        string FieldId,
        string FieldKey,
        string FieldLabel,
        string FieldType,
        List<FieldOption> Options);

    private sealed class DayNodeTargetAccumulator
    {
        private readonly List<string> _samples = new();
        private readonly Dictionary<string, int> _bucketCounts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sourceReportIds = new(StringComparer.Ordinal);
        private decimal _sum;
        private decimal? _min;
        private decimal? _max;
        private int _numberCount;
        private DateTime? _minDate;
        private DateTime? _maxDate;
        private int _trueCount;
        private int _falseCount;

        public DayNodeTargetAccumulator(FieldDefinition field, string method)
        {
            Field = field;
            Method = method;
        }

        public FieldDefinition Field { get; }
        public string Method { get; }
        public int ValueCount { get; private set; }

        public void Add(JsonElement value, string sourceReportId)
        {
            if (IsBlankJsonElement(value))
                return;

            var displayValues = ReadDisplayValues(value, Field);
            if (displayValues.Count == 0 && Field.FieldType is not ("number" or "boolean" or "date" or "dateTime"))
                return;

            if (Field.FieldType == "number")
            {
                var number = ToNullableDecimal(value);
                if (!number.HasValue)
                    return;

                _sourceReportIds.Add(sourceReportId);
                ValueCount++;
                _numberCount++;
                _sum += number.Value;
                _min = !_min.HasValue || number.Value < _min.Value ? number.Value : _min;
                _max = !_max.HasValue || number.Value > _max.Value ? number.Value : _max;
                AddSample(number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;
            }

            if (Field.FieldType is "date" or "dateTime")
            {
                var date = ReadDateValue(value);
                if (!date.HasValue)
                    return;

                _sourceReportIds.Add(sourceReportId);
                ValueCount++;
                _minDate = !_minDate.HasValue || date.Value < _minDate.Value ? date.Value : _minDate;
                _maxDate = !_maxDate.HasValue || date.Value > _maxDate.Value ? date.Value : _maxDate;
                AddSample(date.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                return;
            }

            if (Field.FieldType == "boolean")
            {
                if (!TryReadBoolean(value, out var boolean))
                    return;

                _sourceReportIds.Add(sourceReportId);
                ValueCount++;
                if (boolean) _trueCount++; else _falseCount++;
                AddSample(boolean ? "true" : "false");
                return;
            }

            foreach (var displayValue in displayValues)
            {
                if (string.IsNullOrWhiteSpace(displayValue))
                    continue;

                _sourceReportIds.Add(sourceReportId);
                ValueCount++;
                AddSample(displayValue);
                _bucketCounts[displayValue] = _bucketCounts.TryGetValue(displayValue, out var count) ? count + 1 : 1;
            }
        }

        public AdvancedSummaryDayNodeFieldDto ToDto()
            => new()
            {
                FieldId = Field.FieldId,
                FieldKey = Field.FieldKey,
                Label = Field.FieldLabel,
                DataType = FieldTypeToDataType(Field.FieldType),
                Method = Method,
                ValueCount = ValueCount,
                SourceReportCount = _sourceReportIds.Count,
                Result = BuildResult(),
                SampleValues = _samples.ToList()
            };

        private object? BuildResult()
            => Method switch
            {
                "SUM" => _sum,
                "COUNT" => ValueCount,
                "MEAN" => _numberCount == 0 ? null : _sum / _numberCount,
                "MIN" => _min,
                "MAX" => _max,
                "MIN_DATE" => _minDate?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                "MAX_DATE" => _maxDate?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                "TRUE_COUNT" => _trueCount,
                "FALSE_COUNT" => _falseCount,
                "BUCKET_COUNT" => _bucketCounts
                    .OrderBy(x => x.Key, StringComparer.Ordinal)
                    .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
                "JOIN" => string.Join(", ", _samples),
                _ => ValueCount
            };

        private void AddSample(string value)
        {
            value = value.Trim();
            if (string.IsNullOrWhiteSpace(value) || _samples.Count >= DayNodeSampleLimit)
                return;

            if (value.Length > DayNodeSampleTextLimit)
                value = value[..DayNodeSampleTextLimit];
            if (!_samples.Contains(value, StringComparer.Ordinal))
                _samples.Add(value);
        }
    }

    private sealed class HierarchyFieldRollupAccumulator
    {
        private readonly List<string> _samples = new();
        private readonly Dictionary<string, long> _bucketCounts = new(StringComparer.Ordinal);
        private decimal _sum;
        private decimal? _min;
        private decimal? _max;
        private decimal _weightedMeanSum;
        private long _meanWeight;
        private DateTime? _minDate;
        private DateTime? _maxDate;
        private long _trueCount;
        private long _falseCount;

        public HierarchyFieldRollupAccumulator(AdvancedSummaryDayNodeFieldDto seed)
        {
            FieldId = seed.FieldId;
            FieldKey = seed.FieldKey;
            Label = seed.Label;
            DataType = seed.DataType;
            Method = seed.Method;
        }

        public string FieldId { get; }
        public string FieldKey { get; }
        public string Label { get; }
        public string DataType { get; }
        public string Method { get; }
        public long ValueCount { get; private set; }
        public long SourceReportCount { get; private set; }

        public void Add(AdvancedSummaryDayNodeFieldDto field)
        {
            ValueCount += field.ValueCount;
            SourceReportCount += field.SourceReportCount;
            foreach (var sample in field.SampleValues)
                AddSample(sample);

            switch (Method)
            {
                case "SUM":
                    _sum += ToNullableDecimalResult(field.Result) ?? 0m;
                    break;
                case "MEAN":
                    var mean = ToNullableDecimalResult(field.Result);
                    if (mean.HasValue && field.ValueCount > 0)
                    {
                        _weightedMeanSum += mean.Value * field.ValueCount;
                        _meanWeight += field.ValueCount;
                    }
                    break;
                case "MIN":
                    AddMin(ToNullableDecimalResult(field.Result));
                    break;
                case "MAX":
                    AddMax(ToNullableDecimalResult(field.Result));
                    break;
                case "MIN_DATE":
                    AddMinDate(ToNullableDateResult(field.Result));
                    break;
                case "MAX_DATE":
                    AddMaxDate(ToNullableDateResult(field.Result));
                    break;
                case "TRUE_COUNT":
                    _trueCount += ToNullableLongResult(field.Result) ?? 0;
                    break;
                case "FALSE_COUNT":
                    _falseCount += ToNullableLongResult(field.Result) ?? 0;
                    break;
                case "BUCKET_COUNT":
                    foreach (var item in ReadBucketCountsResult(field.Result))
                        _bucketCounts[item.Key] = _bucketCounts.TryGetValue(item.Key, out var count) ? count + item.Value : item.Value;
                    break;
            }
        }

        public AdvancedSummaryDayNodeFieldDto ToDto()
            => new()
            {
                FieldId = FieldId,
                FieldKey = FieldKey,
                Label = Label,
                DataType = DataType,
                Method = Method,
                ValueCount = ValueCount > int.MaxValue ? int.MaxValue : (int)ValueCount,
                SourceReportCount = SourceReportCount > int.MaxValue ? int.MaxValue : (int)SourceReportCount,
                Result = BuildResult(),
                SampleValues = _samples.ToList()
            };

        private object? BuildResult()
            => Method switch
            {
                "SUM" => _sum,
                "COUNT" => ValueCount,
                "MEAN" => _meanWeight == 0 ? null : _weightedMeanSum / _meanWeight,
                "MIN" => _min,
                "MAX" => _max,
                "MIN_DATE" => _minDate?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                "MAX_DATE" => _maxDate?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                "TRUE_COUNT" => _trueCount,
                "FALSE_COUNT" => _falseCount,
                "BUCKET_COUNT" => _bucketCounts
                    .OrderBy(x => x.Key, StringComparer.Ordinal)
                    .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
                "JOIN" => string.Join(", ", _samples),
                _ => ValueCount
            };

        private void AddMin(decimal? value)
        {
            if (!value.HasValue)
                return;
            _min = !_min.HasValue || value.Value < _min.Value ? value.Value : _min;
        }

        private void AddMax(decimal? value)
        {
            if (!value.HasValue)
                return;
            _max = !_max.HasValue || value.Value > _max.Value ? value.Value : _max;
        }

        private void AddMinDate(DateTime? value)
        {
            if (!value.HasValue)
                return;
            _minDate = !_minDate.HasValue || value.Value < _minDate.Value ? value.Value : _minDate;
        }

        private void AddMaxDate(DateTime? value)
        {
            if (!value.HasValue)
                return;
            _maxDate = !_maxDate.HasValue || value.Value > _maxDate.Value ? value.Value : _maxDate;
        }

        private void AddSample(string value)
        {
            value = value.Trim();
            if (string.IsNullOrWhiteSpace(value) || _samples.Count >= DayNodeSampleLimit)
                return;

            if (value.Length > DayNodeSampleTextLimit)
                value = value[..DayNodeSampleTextLimit];
            if (!_samples.Contains(value, StringComparer.Ordinal))
                _samples.Add(value);
        }
    }

    private sealed class AdvancedSummaryHierarchyNodeValue
    {
        public int SchemaVersion { get; set; }
        public string Kind { get; set; } = DayNodeValueKind;
        public DateTime GeneratedAtUtc { get; set; }
        public string ConfigId { get; set; } = string.Empty;
        public string ConfigHash { get; set; } = string.Empty;
        public string Grain { get; set; } = string.Empty;
        public string GrainKey { get; set; } = string.Empty;
        public string DayKey { get; set; } = string.Empty;
        public string? MonthKey { get; set; }
        public string? YearKey { get; set; }
        public DateTime WindowStartUtc { get; set; }
        public DateTime WindowEndExclusiveUtc { get; set; }
        public int SourceAssignmentCount { get; set; }
        public long SourceReportCount { get; set; }
        public long SectionReportCount { get; set; }
        public int SectionFieldCount { get; set; }
        public int TargetFieldCount { get; set; }
        public int InputNodeCount { get; set; }
        public List<string> Warnings { get; set; } = new();
        public List<AdvancedSummaryDayNodeFieldDto> Fields { get; set; } = new();
    }

    private sealed class AdvancedSummaryComparableNodeValue
    {
        public int SchemaVersion { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string ConfigId { get; set; } = string.Empty;
        public string ConfigHash { get; set; } = string.Empty;
        public string Grain { get; set; } = string.Empty;
        public string GrainKey { get; set; } = string.Empty;
        public string DayKey { get; set; } = string.Empty;
        public string? MonthKey { get; set; }
        public string? YearKey { get; set; }
        public DateTime WindowStartUtc { get; set; }
        public DateTime WindowEndExclusiveUtc { get; set; }
        public int SourceAssignmentCount { get; set; }
        public long SourceReportCount { get; set; }
        public long SectionReportCount { get; set; }
        public int SectionFieldCount { get; set; }
        public int TargetFieldCount { get; set; }
        public int InputNodeCount { get; set; }
        public List<string> Warnings { get; set; } = new();
        public List<AdvancedSummaryDayNodeFieldDto> Fields { get; set; } = new();
    }

    private sealed class AdvancedSummaryDayNodeFieldDto
    {
        public string FieldId { get; set; } = string.Empty;
        public string FieldKey { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public int ValueCount { get; set; }
        public int SourceReportCount { get; set; }
        public object? Result { get; set; }
        public List<string> SampleValues { get; set; } = new();
    }

    private sealed class DynamicFormFieldDefinition
    {
        public string? Id { get; set; }
        public string? Key { get; set; }
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public string? Label { get; set; }
        public string? Type { get; set; }
        public string? SectionId { get; set; }
        public List<DynamicFormFieldOption>? Options { get; set; }
    }

    private sealed class DynamicFormFieldOption
    {
        public string? Code { get; set; }
        public string? Label { get; set; }
    }
}
