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

        var value = new AdvancedSummaryDayNodeValue
        {
            SchemaVersion = 1,
            Kind = DayNodeValueKind,
            GeneratedAtUtc = DateTime.UtcNow,
            ConfigId = config.Id,
            ConfigHash = config.ConfigHash,
            DayKey = dayKey,
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
    {
        if (report.CompletedDate.HasValue)
            return AdvancedSummaryHierarchyKeyHelper.ToDayKey(report.CompletedDate.Value);

        if (!string.IsNullOrWhiteSpace(report.PeriodKey))
        {
            try { return AdvancedSummaryHierarchyKeyHelper.ToDayKey(AdvancedSummaryHierarchyKeyHelper.ParseDayKey(report.PeriodKey)); }
            catch (ArgumentException) { }
        }

        if (report.PeriodStart.HasValue)
            return AdvancedSummaryHierarchyKeyHelper.ToDayKey(report.PeriodStart.Value);

        if (report.ReportDate.HasValue)
            return AdvancedSummaryHierarchyKeyHelper.ToDayKey(report.ReportDate.Value);

        if (report.ApprovedAtUtc.HasValue)
            return AdvancedSummaryHierarchyKeyHelper.ToDayKey(report.ApprovedAtUtc.Value);

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new { reportId = report.Id, reason = "ADVANCED_SUMMARY_SOURCE_DAY_UNRESOLVABLE" },
            "Cannot resolve source day for approved report.");
    }

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

    private sealed class AdvancedSummaryDayNodeValue
    {
        public int SchemaVersion { get; set; }
        public string Kind { get; set; } = DayNodeValueKind;
        public DateTime GeneratedAtUtc { get; set; }
        public string ConfigId { get; set; } = string.Empty;
        public string ConfigHash { get; set; } = string.Empty;
        public string DayKey { get; set; } = string.Empty;
        public DateTime WindowStartUtc { get; set; }
        public DateTime WindowEndExclusiveUtc { get; set; }
        public int SourceAssignmentCount { get; set; }
        public int SourceReportCount { get; set; }
        public int SectionReportCount { get; set; }
        public int SectionFieldCount { get; set; }
        public int TargetFieldCount { get; set; }
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
