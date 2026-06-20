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
using tdtd_be.Services.WorkAssignments.SummaryTokens;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public sealed class WorkAssignmentAdvancedSummaryConfigService : IWorkAssignmentAdvancedSummaryConfigService
{
    private const int PreviewPeriodLimit = 3;
    private const int PreviewSeedReportScanLimit = 3000;
    private const int PreviewSourceReportLimit = 3000;
    private const int PreviewFieldSampleLimit = 5;
    private const int PreviewSampleTextLimit = 160;
    private const int CumulativeSectionFieldLimitExclusive = 250;
    private const int NonCumulativeSectionFieldLimitInclusive = 1000;
    private const string PreviewResultKind = "ADVANCED_SUMMARY_LATEST_THREE_PERIOD_PREVIEW";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions PreviewJsonOptions = new(JsonOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly MongoDbContext _ctx;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly IWorkReportPayloadReader _payloadReader;
    private readonly INotificationService _notifications;
    private readonly IWorkSummaryTokenService _summaryTokens;

    public WorkAssignmentAdvancedSummaryConfigService(
        MongoDbContext ctx,
        IBackgroundJobClient backgroundJobs,
        IWorkReportPayloadReader payloadReader,
        INotificationService notifications,
        IWorkSummaryTokenService summaryTokens)
    {
        _ctx = ctx;
        _backgroundJobs = backgroundJobs;
        _payloadReader = payloadReader;
        _notifications = notifications;
        _summaryTokens = summaryTokens;
    }

    public async Task<List<WorkAssignmentAdvancedSummaryConfigDto>> ListConfigsAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string sectionId,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        var context = await LoadContextAsync(assignmentId, dynamicFormTemplateId, sectionId, actorUserId, ct);
        var lockedCount = await CountLockedAsync(context.Scope.Id, context.Template.Id, context.Section.Id, ct);

        var rows = await _ctx.WorkAssignmentAdvancedSummaryConfigs
            .Find(x =>
                x.AssignmentId == context.Scope.Id &&
                x.DynamicFormTemplateId == context.Template.Id &&
                x.SectionId == context.Section.Id &&
                !x.IsDeleted)
            .Sort(Builders<WorkAssignmentAdvancedSummaryConfig>.Sort
                .Descending(x => x.VersionNo)
                .Descending(x => x.UpdatedAtUtc))
            .ToListAsync(ct);

        var gatesByConfigId = new Dictionary<string, AdvancedSummaryFieldGateInfo>(StringComparer.Ordinal);
        foreach (var row in rows)
            gatesByConfigId[row.Id] = await BuildFieldGateInfoAsync(context.Template, context.Section.Id, row.ConfigJson, ct);

        return rows.Select(x => Map(x, lockedCount, gatesByConfigId[x.Id])).ToList();
    }

    public async Task<WorkAssignmentAdvancedSummaryConfigDto> SaveDraftAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string sectionId,
        SaveWorkAssignmentAdvancedSummaryDraftRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        var context = await LoadContextAsync(assignmentId, dynamicFormTemplateId, sectionId, actorUserId, ct);
        var configJson = NormalizeConfigJson(req?.ConfigJson);
        var configHash = Sha256(configJson);
        var gate = await BuildFieldGateInfoAsync(context.Template, context.Section.Id, configJson, ct);
        EnsureFieldGateAllowsAdvanced(gate);
        var now = DateTime.UtcNow;
        var lockedCount = await CountLockedAsync(context.Scope.Id, context.Template.Id, context.Section.Id, ct);
        var versionNo = await NextVersionNoAsync(context.Scope.Id, context.Template.Id, context.Section.Id, ct);

        var existing = await _ctx.WorkAssignmentAdvancedSummaryConfigs
            .Find(x =>
                x.AssignmentId == context.Scope.Id &&
                x.DynamicFormTemplateId == context.Template.Id &&
                x.SectionId == context.Section.Id &&
                x.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Draft &&
                !x.IsDeleted)
            .SortByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct);

        var entity = existing ?? new WorkAssignmentAdvancedSummaryConfig
        {
            Id = ObjectId.GenerateNewId().ToString(),
            WorkId = context.Scope.WorkId,
            AssignmentId = context.Scope.Id,
            DynamicFormTemplateId = context.Template.Id,
            SectionId = context.Section.Id,
            CreatedAtUtc = now,
            CreatedByUserId = actorUserId
        };

        entity.SectionTitle = context.Section.Title;
        entity.Status = WorkAssignmentAdvancedSummaryConfigStatuses.Draft;
        entity.VersionNo = existing is null ? versionNo : existing.VersionNo;
        entity.DraftRevision = existing is null ? 1 : existing.DraftRevision + 1;
        entity.ConfigJson = configJson;
        entity.ConfigHash = configHash;
        entity.PreviewStatus = WorkAssignmentAdvancedSummaryPreviewStatuses.NotRequested;
        entity.PreviewJobId = null;
        entity.PreviewCorrelationId = null;
        entity.PreviewPeriodKeys = new List<string>();
        entity.PreviewResultJson = null;
        entity.PreviewError = null;
        entity.PreviewRequestedAtUtc = null;
        entity.PreviewFinishedAtUtc = null;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = actorUserId;
        entity.IsDeleted = false;

        if (existing is null)
            await _ctx.WorkAssignmentAdvancedSummaryConfigs.InsertOneAsync(entity, cancellationToken: ct);
        else
            await _ctx.WorkAssignmentAdvancedSummaryConfigs.ReplaceOneAsync(x => x.Id == entity.Id, entity, cancellationToken: ct);

        return Map(entity, lockedCount, gate);
    }

    public async Task<WorkAssignmentAdvancedSummaryConfigDto> LockConfigAsync(
        string configId,
        LockWorkAssignmentAdvancedSummaryConfigRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        configId = configId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(configId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_ARGUMENT_REQUIRED, new { field = "configId" });

        var entity = await _ctx.WorkAssignmentAdvancedSummaryConfigs
            .Find(x => x.Id == configId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(AppErrorCode.COMMON_NOT_FOUND, new { configId });

        var context = await LoadContextAsync(
            entity.AssignmentId,
            entity.DynamicFormTemplateId,
            entity.SectionId,
            actorUserId,
            ct);

        var lockedCount = await CountLockedAsync(context.Scope.Id, context.Template.Id, context.Section.Id, ct);
        var gate = await BuildFieldGateInfoAsync(context.Template, context.Section.Id, entity.ConfigJson, ct);
        if (entity.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Locked)
            return Map(entity, lockedCount, gate);

        if (entity.Status != WorkAssignmentAdvancedSummaryConfigStatuses.Draft)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { configId, entity.Status, reason = "ADVANCED_SUMMARY_CONFIG_STATUS_NOT_LOCKABLE" });
        }

        if (entity.PreviewStatus != WorkAssignmentAdvancedSummaryPreviewStatuses.Done)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    configId,
                    entity.PreviewStatus,
                    reason = "ADVANCED_SUMMARY_PREVIEW_REQUIRED"
                },
                "Advanced summary config must complete the latest-three-period preview before lock.");
        }

        EnsureFieldGateAllowsAdvanced(gate);

        var token = await _summaryTokens.ConsumeAdvancedConfigLockAsync(
            entity,
            lockedCount,
            actorUserId,
            req?.TokenId,
            ct);

        var now = DateTime.UtcNow;
        entity.Status = WorkAssignmentAdvancedSummaryConfigStatuses.Locked;
        entity.VersionNo = await NextVersionNoAsync(context.Scope.Id, context.Template.Id, context.Section.Id, ct);
        entity.LockedAtUtc = now;
        entity.LockedByUserId = actorUserId;
        entity.LockTokenId = token.LedgerId;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = actorUserId;

        try
        {
            await _ctx.WorkAssignmentAdvancedSummaryConfigs.ReplaceOneAsync(x => x.Id == entity.Id, entity, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                await _summaryTokens.MarkFailedAsync(
                    token.LedgerId,
                    actorUserId,
                    $"ADVANCED_SUMMARY_CONFIG_LOCK_FAILED: {ex.Message}",
                    ct);
            }
            catch
            {
                // Preserve the original lock failure; the ledger can be repaired from operation logs.
            }

            throw;
        }

        return Map(entity, lockedCount + 1, gate);
    }

    public async Task<WorkAssignmentAdvancedSummaryConfigDto> RequestPreviewAsync(
        string configId,
        PreviewWorkAssignmentAdvancedSummaryConfigRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        var entity = await LoadConfigForActionAsync(configId, ct);
        var context = await LoadContextAsync(
            entity.AssignmentId,
            entity.DynamicFormTemplateId,
            entity.SectionId,
            actorUserId,
            ct);

        if (entity.Status != WorkAssignmentAdvancedSummaryConfigStatuses.Draft)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { configId = entity.Id, entity.Status, reason = "ADVANCED_SUMMARY_CONFIG_STATUS_NOT_PREVIEWABLE" });
        }

        var gate = await BuildFieldGateInfoAsync(context.Template, context.Section.Id, entity.ConfigJson, ct);
        EnsureFieldGateAllowsAdvanced(gate);

        var previewStatus = NormalizePreviewStatus(entity.PreviewStatus);
        if (req?.ForceRefresh != true &&
            (previewStatus == WorkAssignmentAdvancedSummaryPreviewStatuses.Queued ||
             previewStatus == WorkAssignmentAdvancedSummaryPreviewStatuses.Running))
        {
            var lockedCount = await CountLockedAsync(context.Scope.Id, context.Template.Id, context.Section.Id, ct);
            return Map(entity, lockedCount, gate);
        }

        var correlationId = ObjectId.GenerateNewId().ToString();
        var jobId = _backgroundJobs.Enqueue<IWorkAssignmentAdvancedSummaryConfigService>(
            svc => svc.RunPreviewJobAsync(entity.Id, entity.ConfigHash, actorUserId, correlationId, CancellationToken.None));
        var now = DateTime.UtcNow;

        entity.PreviewStatus = WorkAssignmentAdvancedSummaryPreviewStatuses.Queued;
        entity.PreviewJobId = jobId;
        entity.PreviewCorrelationId = correlationId;
        entity.PreviewPeriodKeys = new List<string>();
        entity.PreviewResultJson = null;
        entity.PreviewError = null;
        entity.PreviewRequestedAtUtc = now;
        entity.PreviewFinishedAtUtc = null;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = actorUserId;

        await _ctx.WorkAssignmentAdvancedSummaryConfigs.ReplaceOneAsync(x => x.Id == entity.Id, entity, cancellationToken: ct);

        var lockedAfterQueue = await CountLockedAsync(context.Scope.Id, context.Template.Id, context.Section.Id, ct);
        return Map(entity, lockedAfterQueue, gate);
    }

    public async Task RunPreviewJobAsync(
        string configId,
        string expectedConfigHash,
        string actorUserId,
        string correlationId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        var entity = await LoadConfigForActionAsync(configId, ct);
        if (!IsCurrentPreviewJob(entity, expectedConfigHash, correlationId))
            return;

        WorkAssignment? notifyScope = null;
        DynamicFormTemplate? notifyTemplate = null;

        try
        {
            var context = await LoadContextAsync(
                entity.AssignmentId,
                entity.DynamicFormTemplateId,
                entity.SectionId,
                actorUserId,
                ct);
            notifyScope = context.Scope;
            notifyTemplate = context.Template;

            await MarkPreviewRunningAsync(entity.Id, expectedConfigHash, correlationId, ct);

            var result = await BuildLatestThreePeriodPreviewAsync(entity, context, ct);
            var resultJson = JsonSerializer.Serialize(result, PreviewJsonOptions);
            var finishedAtUtc = DateTime.UtcNow;

            await _ctx.WorkAssignmentAdvancedSummaryConfigs.UpdateOneAsync(
                CurrentPreviewFilter(entity.Id, expectedConfigHash, correlationId),
                Builders<WorkAssignmentAdvancedSummaryConfig>.Update
                    .Set(x => x.PreviewStatus, WorkAssignmentAdvancedSummaryPreviewStatuses.Done)
                    .Set(x => x.PreviewPeriodKeys, result.PeriodKeys)
                    .Set(x => x.PreviewResultJson, resultJson)
                    .Set(x => x.PreviewError, (string?)null)
                    .Set(x => x.PreviewFinishedAtUtc, finishedAtUtc)
                    .Set(x => x.UpdatedAtUtc, finishedAtUtc)
                    .Set(x => x.UpdatedByUserId, actorUserId),
                cancellationToken: ct);

            await NotifyAdvancedSummaryPreviewAsync(
                entity,
                actorUserId,
                notifyScope,
                notifyTemplate,
                correlationId,
                eventStatus: "done",
                title: "Advanced summary preview completed",
                stateText: "latest-three-period preview completed",
                severity: UserNotificationSeverities.Info,
                requiresAction: false,
                error: null,
                ct);
        }
        catch (Exception ex)
        {
            var error = BuildPreviewErrorMessage(ex);
            await MarkPreviewFailedAsync(entity.Id, expectedConfigHash, correlationId, actorUserId, error, ct);
            await NotifyAdvancedSummaryPreviewAsync(
                entity,
                actorUserId,
                notifyScope,
                notifyTemplate,
                correlationId,
                eventStatus: "failed",
                title: "Advanced summary preview failed",
                stateText: "latest-three-period preview failed",
                severity: UserNotificationSeverities.Warning,
                requiresAction: true,
                error,
                ct);
            throw;
        }
    }

    private async Task<AdvancedSummaryContext> LoadContextAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string sectionId,
        string actorUserId,
        CancellationToken ct)
    {
        assignmentId = assignmentId?.Trim() ?? string.Empty;
        dynamicFormTemplateId = dynamicFormTemplateId?.Trim() ?? string.Empty;
        sectionId = sectionId?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(assignmentId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_SCOPE_ID_REQUIRED);
        if (string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_DYNAMIC_FORM_TEMPLATE_ID_REQUIRED);
        if (string.IsNullOrWhiteSpace(sectionId))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
                new { field = "sectionId", reason = "ADVANCED_SUMMARY_SECTION_REQUIRED" });

        var scope = await _ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_PARENT_NOT_FOUND,
                new { assignmentId });

        if (!CanReadAssignment(scope, actorUserId))
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_READ_FORBIDDEN,
                new { assignmentId, actorUserId });
        }

        var template = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == dynamicFormTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
                new { dynamicFormTemplateId });

        var section = ResolveSection(template, sectionId);
        return new AdvancedSummaryContext(scope, template, section);
    }

    private async Task<WorkAssignmentAdvancedSummaryConfig> LoadConfigForActionAsync(
        string configId,
        CancellationToken ct)
    {
        configId = configId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(configId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_ARGUMENT_REQUIRED, new { field = "configId" });

        return await _ctx.WorkAssignmentAdvancedSummaryConfigs
            .Find(x => x.Id == configId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(AppErrorCode.COMMON_NOT_FOUND, new { configId });
    }

    private async Task<AdvancedSummaryPreviewResult> BuildLatestThreePeriodPreviewAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        AdvancedSummaryContext context,
        CancellationToken ct)
    {
        EnsurePreviewConfigSupported(config.ConfigJson);

        var sectionFields = await LoadSectionFieldsAsync(context.Template, context.Section.Id, ct);
        var gate = BuildFieldGateInfo(config.ConfigJson, sectionFields);
        EnsureFieldGateAllowsAdvanced(gate);
        var configAnalysis = AnalyzePreviewConfigJson(config.ConfigJson, sectionFields);
        if (configAnalysis.UnsupportedFeatures.Count > 0)
            throw UnsupportedPreviewConfig(configAnalysis.UnsupportedFeatures);

        var sourceAssignments = await LoadPreviewSourceAssignmentsAsync(
            context.Scope,
            context.Template.Id,
            ct);

        var warnings = new List<string>();
        warnings.AddRange(configAnalysis.UnknownFieldRefs.Select(x => $"Unknown field target ignored: {x}"));

        var targetFields = ResolvePreviewTargetFields(configAnalysis, sectionFields, warnings);
        var reportFilter = BuildPreviewReportFilter(
            sourceAssignments.Select(x => x.Id).ToList(),
            context.Template.Id);

        var seedReports = sourceAssignments.Count == 0
            ? new List<WorkAssignmentReport>()
            : await _ctx.WorkAssignmentReports
                .Find(reportFilter)
                .Sort(Builders<WorkAssignmentReport>.Sort
                    .Descending(x => x.PeriodKey)
                    .Descending(x => x.ApprovedAtUtc)
                    .Descending(x => x.Id))
                .Limit(PreviewSeedReportScanLimit)
                .ToListAsync(ct);

        if (seedReports.Count >= PreviewSeedReportScanLimit)
            warnings.Add($"Preview scanned the first {PreviewSeedReportScanLimit} reports while searching latest periods.");

        var periodKeys = seedReports
            .Select(x => x.PeriodKey?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .Take(PreviewPeriodLimit)
            .Cast<string>()
            .ToList();

        var sourceReports = periodKeys.Count == 0
            ? new List<WorkAssignmentReport>()
            : await _ctx.WorkAssignmentReports
                .Find(reportFilter & Builders<WorkAssignmentReport>.Filter.In(x => x.PeriodKey, periodKeys))
                .Sort(Builders<WorkAssignmentReport>.Sort
                    .Descending(x => x.PeriodKey)
                    .Ascending(x => x.WorkAssignmentId)
                    .Ascending(x => x.AssigneeUserId)
                    .Ascending(x => x.Id))
                .Limit(PreviewSourceReportLimit + 1)
                .ToListAsync(ct);

        if (sourceReports.Count > PreviewSourceReportLimit)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    configId = config.Id,
                    maxSourceReports = PreviewSourceReportLimit,
                    reason = "ADVANCED_SUMMARY_PREVIEW_SOURCE_REPORT_LIMIT_EXCEEDED"
                },
                $"Advanced summary preview source report limit exceeded ({PreviewSourceReportLimit}).");
        }

        var previewReports = sourceReports
            .OrderByDescending(x => x.PeriodKey, StringComparer.Ordinal)
            .ThenBy(x => x.WorkAssignmentId, StringComparer.Ordinal)
            .ThenBy(x => x.AssigneeUserId, StringComparer.Ordinal)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToList();

        var accumulators = targetFields
            .Select(x => new PreviewTargetAccumulator(x.Field, x.Method))
            .ToDictionary(x => x.Field.FieldId, StringComparer.Ordinal);
        var periodAccumulators = periodKeys
            .ToDictionary(x => x, x => new PreviewPeriodAccumulator(x), StringComparer.Ordinal);

        var reportIds = previewReports.Select(x => x.Id).ToList();
        var sectionRows = reportIds.Count == 0
            ? new List<WorkAssignmentReportSection>()
            : await _ctx.WorkAssignmentReportSections
                .Find(Builders<WorkAssignmentReportSection>.Filter.In(x => x.WorkAssignmentReportId, reportIds) &
                      Builders<WorkAssignmentReportSection>.Filter.Eq(x => x.SectionId, context.Section.Id) &
                      Builders<WorkAssignmentReportSection>.Filter.Eq(x => x.IsDeleted, false))
                .ToListAsync(ct);
        var sectionByReportId = sectionRows
            .GroupBy(x => x.WorkAssignmentReportId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        var fallbackPayloadReadCount = 0;
        foreach (var report in previewReports)
        {
            if (!periodAccumulators.TryGetValue(report.PeriodKey, out var periodAccumulator))
                continue;

            periodAccumulator.ReportCount++;
            string? fieldValuesJson;
            if (sectionByReportId.TryGetValue(report.Id, out var sectionRow))
            {
                periodAccumulator.SectionReportCount++;
                fieldValuesJson = sectionRow.FieldValuesJson;
            }
            else
            {
                fallbackPayloadReadCount++;
                var payload = await _payloadReader.LoadReportPayloadAsync(report, ct);
                fieldValuesJson = payload.FieldValuesJson;
            }

            if (!TryGetValuesObject(fieldValuesJson, out var valuesObject))
                continue;

            foreach (var target in targetFields)
            {
                if (!TryGetFieldValue(valuesObject, target.Field, out var value) || IsBlankJsonElement(value))
                    continue;

                var before = accumulators[target.Field.FieldId].ValueCount;
                accumulators[target.Field.FieldId].Add(value, report.PeriodKey);
                var delta = accumulators[target.Field.FieldId].ValueCount - before;
                if (delta > 0)
                {
                    periodAccumulator.ValueCount += delta;
                    periodAccumulator.FieldsWithData.Add(target.Field.FieldId);
                }
            }
        }

        if (fallbackPayloadReadCount > 0)
            warnings.Add($"Preview had to read {fallbackPayloadReadCount} full payload(s) because section snapshots were missing.");
        if (periodKeys.Count == 0)
            warnings.Add("No approved source reports were found for the latest-three-period preview.");
        if (targetFields.Count == 0)
            warnings.Add("No field target was available for this section preview.");

        return new AdvancedSummaryPreviewResult
        {
            SchemaVersion = 1,
            Kind = PreviewResultKind,
            GeneratedAtUtc = DateTime.UtcNow,
            ConfigId = config.Id,
            ConfigHash = config.ConfigHash,
            WorkId = config.WorkId,
            AssignmentId = config.AssignmentId,
            DynamicFormTemplateId = config.DynamicFormTemplateId,
            SectionId = config.SectionId,
            SectionTitle = config.SectionTitle,
            PeriodKeys = periodKeys,
            SourceAssignmentCount = sourceAssignments.Count,
            SourceReportCount = previewReports.Count,
            SectionReportCount = sectionRows.Count,
            SectionFieldCount = sectionFields.Count,
            TargetFieldCount = targetFields.Count,
            Warnings = warnings,
            Periods = periodAccumulators.Values
                .Select(x => x.ToDto())
                .ToList(),
            Fields = accumulators.Values
                .OrderBy(x => x.Field.FieldKey, StringComparer.Ordinal)
                .Select(x => x.ToDto())
                .ToList()
        };
    }

    private async Task<List<WorkAssignment>> LoadPreviewSourceAssignmentsAsync(
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

    private static FilterDefinition<WorkAssignmentReport> BuildPreviewReportFilter(
        List<string> sourceAssignmentIds,
        string dynamicFormTemplateId)
    {
        var fb = Builders<WorkAssignmentReport>.Filter;
        if (sourceAssignmentIds.Count == 0)
            return fb.Eq(x => x.Id, "__advanced_summary_preview_no_source__");

        var scheduledFilter = fb.Or(
            fb.Eq(x => x.PeriodKind, null),
            fb.Eq(x => x.PeriodKind, WorkReportPeriodKind.Scheduled));

        return fb.In(x => x.WorkAssignmentId, sourceAssignmentIds)
               & fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId)
               & scheduledFilter
               & fb.Eq(x => x.Status, WorkAssignmentReportStatus.Approved)
               & fb.Eq(x => x.IsDeleted, false)
               & fb.Eq(x => x.IsCurrent, true)
               & fb.Ne(x => x.IsActive, false)
               & fb.Ne(x => x.CumulativeContributionMode, WorkReportCumulativeContributionMode.Exclude);
    }

    private async Task<List<AdvancedFieldDefinition>> LoadSectionFieldsAsync(
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

    private async Task<AdvancedSummaryFieldGateInfo> BuildFieldGateInfoAsync(
        DynamicFormTemplate template,
        string sectionId,
        string configJson,
        CancellationToken ct)
    {
        var sectionFields = await LoadSectionFieldsAsync(template, sectionId, ct);
        return BuildFieldGateInfo(configJson, sectionFields);
    }

    private static AdvancedSummaryFieldGateInfo BuildFieldGateInfo(
        string configJson,
        List<AdvancedFieldDefinition> sectionFields)
    {
        var analysis = AnalyzePreviewConfigJson(configJson, sectionFields);
        var sectionFieldCount = sectionFields.Count;
        var targetFieldCount = analysis.HasExplicitTargets
            ? analysis.Targets.Count + analysis.UnknownFieldRefs.Count
            : sectionFieldCount;
        return BuildFieldGateInfoFromCounts(configJson, sectionFieldCount, targetFieldCount);
    }

    private static AdvancedSummaryFieldGateInfo BuildFieldGateInfoFromCounts(
        string configJson,
        int sectionFieldCount,
        int targetFieldCount)
    {
        var isCumulative = DetectCumulativeConfig(configJson);
        var fieldLimit = isCumulative
            ? CumulativeSectionFieldLimitExclusive - 1
            : NonCumulativeSectionFieldLimitInclusive;

        if (isCumulative && sectionFieldCount >= CumulativeSectionFieldLimitExclusive)
        {
            return new AdvancedSummaryFieldGateInfo(
                FieldGateStatuses.Blocked,
                isCumulative,
                sectionFieldCount,
                targetFieldCount,
                fieldLimit,
                $"Cumulative Advanced summary requires the section to have fewer than {CumulativeSectionFieldLimitExclusive} fields.");
        }

        if (!isCumulative && sectionFieldCount > NonCumulativeSectionFieldLimitInclusive)
        {
            return new AdvancedSummaryFieldGateInfo(
                FieldGateStatuses.Blocked,
                isCumulative,
                sectionFieldCount,
                targetFieldCount,
                fieldLimit,
                $"Advanced summary requires the section to have at most {NonCumulativeSectionFieldLimitInclusive} fields.");
        }

        return new AdvancedSummaryFieldGateInfo(
            FieldGateStatuses.Allowed,
            isCumulative,
            sectionFieldCount,
            targetFieldCount,
            fieldLimit,
            null);
    }

    private static void EnsureFieldGateAllowsAdvanced(AdvancedSummaryFieldGateInfo gate)
    {
        if (gate.Status == FieldGateStatuses.Allowed)
            return;

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new
            {
                gate.Status,
                gate.IsCumulative,
                gate.SectionFieldCount,
                gate.TargetFieldCount,
                gate.FieldLimit,
                reason = "ADVANCED_SUMMARY_SECTION_FIELD_LIMIT_EXCEEDED"
            },
            $"{gate.Reason} Please split the Dynamic Form section or use Basic summary.");
    }

    private static List<AdvancedPreviewTarget> ResolvePreviewTargetFields(
        AdvancedPreviewConfigAnalysis analysis,
        List<AdvancedFieldDefinition> sectionFields,
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
                .Select(x => new AdvancedPreviewTarget(x, DefaultPreviewMethod(x.FieldType)))
                .ToList();

        var output = new Dictionary<string, AdvancedPreviewTarget>(StringComparer.Ordinal);
        foreach (var target in analysis.Targets)
        {
            if (!fieldsById.TryGetValue(target.FieldRef, out var field) &&
                !fieldsByKey.TryGetValue(target.FieldRef, out field))
            {
                warnings.Add($"Config target is outside this section and was ignored: {target.FieldRef}");
                continue;
            }

            output[field.FieldId] = new AdvancedPreviewTarget(
                field,
                NormalizePreviewMethod(target.Method, field.FieldType, warnings));
        }

        return output.Values.ToList();
    }

    private static void EnsurePreviewConfigSupported(string configJson)
    {
        var analysis = AnalyzePreviewConfigJson(configJson, new List<AdvancedFieldDefinition>());
        if (analysis.UnsupportedFeatures.Count > 0)
            throw UnsupportedPreviewConfig(analysis.UnsupportedFeatures);
    }

    private static AdvancedPreviewConfigAnalysis AnalyzePreviewConfigJson(
        string configJson,
        List<AdvancedFieldDefinition> sectionFields)
    {
        var targets = new Dictionary<string, AdvancedPreviewTargetSpec>(StringComparer.Ordinal);
        var unknownFieldRefs = new HashSet<string>(StringComparer.Ordinal);
        var unsupported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownRefs = new HashSet<string>(
            sectionFields.SelectMany(x => new[] { x.FieldId, x.FieldKey }),
            StringComparer.Ordinal);

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson);
            CollectPreviewConfig(document.RootElement, targets, unknownFieldRefs, unsupported, knownRefs);
        }
        catch (JsonException ex)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "configJson", reason = "ADVANCED_SUMMARY_CONFIG_JSON_INVALID", ex.Message });
        }

        return new AdvancedPreviewConfigAnalysis(
            targets.Values.ToList(),
            unknownFieldRefs.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            unsupported.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            targets.Count > 0);
    }

    private static void CollectPreviewConfig(
        JsonElement element,
        Dictionary<string, AdvancedPreviewTargetSpec> targets,
        HashSet<string> unknownFieldRefs,
        HashSet<string> unsupported,
        HashSet<string> knownRefs)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectPreviewConfig(item, targets, unknownFieldRefs, unsupported, knownRefs);
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
            AddPreviewTarget(fieldRef, method, targets, unknownFieldRefs, knownRefs);
        }

        foreach (var prop in element.EnumerateObject())
        {
            var name = prop.Name.Trim();
            if (IsUnsupportedPreviewFeatureName(name))
                unsupported.Add(name);

            if (IsFieldIdArrayName(name) && prop.Value.ValueKind == JsonValueKind.Array)
            {
                var method = ReadStringProperty(element, "method")
                             ?? ReadStringProperty(element, "operation")
                             ?? ReadStringProperty(element, "statistic");
                foreach (var item in prop.Value.EnumerateArray())
                {
                    var itemRef = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;
                    AddPreviewTarget(itemRef, method, targets, unknownFieldRefs, knownRefs);
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
                    AddPreviewTarget(item.Name, method, targets, unknownFieldRefs, knownRefs);
                }
            }

            CollectPreviewConfig(prop.Value, targets, unknownFieldRefs, unsupported, knownRefs);
        }
    }

    private static void AddPreviewTarget(
        string? fieldRef,
        string? method,
        Dictionary<string, AdvancedPreviewTargetSpec> targets,
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

        targets[fieldRef] = new AdvancedPreviewTargetSpec(fieldRef, method?.Trim());
    }

    private static AppException UnsupportedPreviewConfig(IReadOnlyCollection<string> unsupportedFeatures)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new
            {
                unsupportedFeatures,
                reason = "ADVANCED_SUMMARY_PREVIEW_ENGINE_NOT_READY"
            },
            "Advanced summary preview currently supports simple section field methods only. Data range, condition, and filter execution will be added in a later phase.");

    private static bool IsUnsupportedPreviewFeatureName(string name)
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

    private static bool DetectCumulativeConfig(string configJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson);
            return DetectCumulativeConfig(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool DetectCumulativeConfig(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(DetectCumulativeConfig);

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var prop in element.EnumerateObject())
        {
            var normalizedName = prop.Name.Trim().ToLowerInvariant();
            if (IsCumulativeBooleanName(normalizedName) &&
                prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                if (prop.Value.GetBoolean())
                    return true;
            }

            if (IsCumulativeModeName(normalizedName) &&
                prop.Value.ValueKind == JsonValueKind.String &&
                IsCumulativeText(prop.Value.GetString()))
            {
                return true;
            }

            if (DetectCumulativeConfig(prop.Value))
                return true;
        }

        return false;
    }

    private static bool IsCumulativeBooleanName(string normalizedName)
        => normalizedName is "cumulative" or "iscumulative" or "usecumulative" or
            "cumulativeenabled" or "enablecumulative";

    private static bool IsCumulativeModeName(string normalizedName)
        => normalizedName is "mode" or "summarymode" or "aggregationmode" or
            "periodscopemode" or "periodmode" or "rangemode";

    private static bool IsCumulativeText(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Trim().Contains("CUMULATIVE", StringComparison.OrdinalIgnoreCase);

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

    private static List<AdvancedFieldDefinition> ExtractFieldDefinitions(
        string? fieldsJson,
        string sectionId,
        HashSet<string>? allowedFieldIds)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
            return new List<AdvancedFieldDefinition>();

        try
        {
            var rawFields = JsonSerializer.Deserialize<List<DynamicFormFieldDefinition>>(fieldsJson, JsonOptions)
                            ?? new List<DynamicFormFieldDefinition>();

            return rawFields
                .Select(x => ToFieldDefinition(x, sectionId, allowedFieldIds))
                .OfType<AdvancedFieldDefinition>()
                .GroupBy(x => x.FieldId, StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();
        }
        catch (JsonException)
        {
            return new List<AdvancedFieldDefinition>();
        }
    }

    private static AdvancedFieldDefinition? ToFieldDefinition(
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

        return new AdvancedFieldDefinition(
            fieldId,
            fieldKey,
            label,
            NormalizeFieldType(field.Type),
            options);
    }

    private static string NormalizeFieldType(string? value)
        => string.IsNullOrWhiteSpace(value) ? "text" : value.Trim();

    private static string? PickNonBlank(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static string DefaultPreviewMethod(string fieldType)
        => FieldTypeToSummaryDataType(fieldType) switch
        {
            "NUMBER" => "SUM",
            "DATE" => "MAX_DATE",
            "BOOLEAN" => "TRUE_COUNT",
            "SINGLE_SELECT" or "MULTI_SELECT" => "BUCKET_COUNT",
            _ => "COUNT"
        };

    private static string NormalizePreviewMethod(string? method, string fieldType, List<string> warnings)
    {
        var dataType = FieldTypeToSummaryDataType(fieldType);
        var normalized = string.IsNullOrWhiteSpace(method)
            ? DefaultPreviewMethod(fieldType)
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

        var fallback = DefaultPreviewMethod(fieldType);
        warnings.Add($"Unsupported preview method '{normalized}' for data type {dataType}; fallback to {fallback}.");
        return fallback;
    }

    private static string FieldTypeToSummaryDataType(string fieldType)
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

    private static bool TryGetFieldValue(
        JsonElement valuesObject,
        AdvancedFieldDefinition field,
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

    private static List<string> ReadDisplayValues(JsonElement value, AdvancedFieldDefinition field)
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

    private async Task MarkPreviewRunningAsync(
        string configId,
        string expectedConfigHash,
        string correlationId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await _ctx.WorkAssignmentAdvancedSummaryConfigs.UpdateOneAsync(
            CurrentPreviewFilter(configId, expectedConfigHash, correlationId),
            Builders<WorkAssignmentAdvancedSummaryConfig>.Update
                .Set(x => x.PreviewStatus, WorkAssignmentAdvancedSummaryPreviewStatuses.Running)
                .Set(x => x.PreviewError, (string?)null)
                .Set(x => x.UpdatedAtUtc, now),
            cancellationToken: ct);
    }

    private async Task MarkPreviewFailedAsync(
        string configId,
        string expectedConfigHash,
        string correlationId,
        string actorUserId,
        string error,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await _ctx.WorkAssignmentAdvancedSummaryConfigs.UpdateOneAsync(
            CurrentPreviewFilter(configId, expectedConfigHash, correlationId),
            Builders<WorkAssignmentAdvancedSummaryConfig>.Update
                .Set(x => x.PreviewStatus, WorkAssignmentAdvancedSummaryPreviewStatuses.Failed)
                .Set(x => x.PreviewError, error)
                .Set(x => x.PreviewFinishedAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
    }

    private static FilterDefinition<WorkAssignmentAdvancedSummaryConfig> CurrentPreviewFilter(
        string configId,
        string expectedConfigHash,
        string correlationId)
    {
        var fb = Builders<WorkAssignmentAdvancedSummaryConfig>.Filter;
        return fb.Eq(x => x.Id, configId)
               & fb.Eq(x => x.ConfigHash, expectedConfigHash)
               & fb.Eq(x => x.PreviewCorrelationId, correlationId)
               & fb.Eq(x => x.Status, WorkAssignmentAdvancedSummaryConfigStatuses.Draft)
               & fb.Eq(x => x.IsDeleted, false);
    }

    private static bool IsCurrentPreviewJob(
        WorkAssignmentAdvancedSummaryConfig entity,
        string expectedConfigHash,
        string correlationId)
        => entity.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Draft &&
           string.Equals(entity.ConfigHash, expectedConfigHash, StringComparison.Ordinal) &&
           string.Equals(entity.PreviewCorrelationId, correlationId, StringComparison.Ordinal);

    private static string NormalizePreviewStatus(string? status)
        => status?.Trim().ToUpperInvariant() switch
        {
            WorkAssignmentAdvancedSummaryPreviewStatuses.Queued => WorkAssignmentAdvancedSummaryPreviewStatuses.Queued,
            WorkAssignmentAdvancedSummaryPreviewStatuses.Running => WorkAssignmentAdvancedSummaryPreviewStatuses.Running,
            WorkAssignmentAdvancedSummaryPreviewStatuses.Done => WorkAssignmentAdvancedSummaryPreviewStatuses.Done,
            WorkAssignmentAdvancedSummaryPreviewStatuses.Failed => WorkAssignmentAdvancedSummaryPreviewStatuses.Failed,
            _ => WorkAssignmentAdvancedSummaryPreviewStatuses.NotRequested
        };

    private static string BuildPreviewErrorMessage(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        if (string.IsNullOrWhiteSpace(message))
            message = ex.GetType().Name;

        message = message.Trim();
        return message.Length <= 2000 ? message : message[..2000];
    }

    private Task NotifyAdvancedSummaryPreviewAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        string actorUserId,
        WorkAssignment? scope,
        DynamicFormTemplate? template,
        string correlationId,
        string eventStatus,
        string title,
        string stateText,
        string severity,
        bool requiresAction,
        string? error,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            return Task.CompletedTask;

        return _notifications.CreateManyAsync(new[]
        {
            new NotificationCommand
            {
                RecipientUserId = actorUserId,
                Type = UserNotificationTypes.AdvancedSummaryPreview,
                Severity = severity,
                Title = title,
                Body = BuildAdvancedSummaryPreviewNotificationBody(stateText, config, scope, template, correlationId, error),
                WorkId = config.WorkId,
                WorkAssignmentId = config.AssignmentId,
                AssignmentCode = scope?.Code,
                Category = UserNotificationCategories.Status,
                RequiresAction = requiresAction,
                ActionState = requiresAction
                    ? UserNotificationActionStates.Open
                    : UserNotificationActionStates.Resolved,
                SourceEntityType = "WORK_ASSIGNMENT_ADVANCED_SUMMARY_CONFIG",
                SourceEntityId = config.Id,
                RequestId = ObjectId.TryParse(correlationId, out _) ? correlationId : null,
                ActorUserId = actorUserId,
                TargetUserId = actorUserId,
                OccurredAtUtc = DateTime.UtcNow,
                EventKey = $"advanced-summary-preview:{eventStatus}:{config.Id}:{correlationId}"
            }
        }, ct);
    }

    private static string BuildAdvancedSummaryPreviewNotificationBody(
        string stateText,
        WorkAssignmentAdvancedSummaryConfig config,
        WorkAssignment? scope,
        DynamicFormTemplate? template,
        string correlationId,
        string? error)
    {
        var templateLabel = PickNonBlank(template?.Code, template?.Name, template?.Id, config.DynamicFormTemplateId) ?? "-";
        var scopeLabel = PickNonBlank(scope?.Code, scope?.Name, scope?.Id, config.AssignmentId) ?? "-";
        var sectionLabel = PickNonBlank(config.SectionTitle, config.SectionId) ?? "-";
        var body = $"Template {templateLabel}, assignment {scopeLabel}, section {sectionLabel}: {stateText}. Correlation: {correlationId}.";
        if (!string.IsNullOrWhiteSpace(error))
            body += $" Error: {error.Trim()}";

        return body.Length <= 1800 ? body : body[..1800];
    }

    private async Task<long> CountLockedAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string sectionId,
        CancellationToken ct)
        => await _ctx.WorkAssignmentAdvancedSummaryConfigs.CountDocumentsAsync(
            x =>
                x.AssignmentId == assignmentId &&
                x.DynamicFormTemplateId == dynamicFormTemplateId &&
                x.SectionId == sectionId &&
                x.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Locked &&
                !x.IsDeleted,
            cancellationToken: ct);

    private async Task<int> NextVersionNoAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string sectionId,
        CancellationToken ct)
    {
        var latest = await _ctx.WorkAssignmentAdvancedSummaryConfigs
            .Find(x =>
                x.AssignmentId == assignmentId &&
                x.DynamicFormTemplateId == dynamicFormTemplateId &&
                x.SectionId == sectionId &&
                x.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Locked &&
                !x.IsDeleted)
            .SortByDescending(x => x.VersionNo)
            .FirstOrDefaultAsync(ct);

        return Math.Max(1, (latest?.VersionNo ?? 0) + 1);
    }

    private static WorkAssignmentAdvancedSummaryConfigDto Map(
        WorkAssignmentAdvancedSummaryConfig x,
        long lockedCount,
        AdvancedSummaryFieldGateInfo? gate)
    {
        gate ??= AdvancedSummaryFieldGateInfo.Unknown;
        var isLocked = x.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Locked;
        var requiresPreview = !isLocked && x.PreviewStatus != WorkAssignmentAdvancedSummaryPreviewStatuses.Done;
        var requiresToken = !isLocked && lockedCount > 0;
        var gateBlocked = gate.Status == FieldGateStatuses.Blocked;

        return new WorkAssignmentAdvancedSummaryConfigDto
        {
            Id = x.Id,
            WorkId = x.WorkId,
            AssignmentId = x.AssignmentId,
            DynamicFormTemplateId = x.DynamicFormTemplateId,
            SectionId = x.SectionId,
            SectionTitle = x.SectionTitle,
            Status = x.Status,
            VersionNo = x.VersionNo,
            DraftRevision = x.DraftRevision,
            ConfigJson = x.ConfigJson,
            ConfigHash = x.ConfigHash,
            PreviewStatus = x.PreviewStatus,
            PreviewJobId = x.PreviewJobId,
            PreviewCorrelationId = x.PreviewCorrelationId,
            PreviewPeriodKeys = x.PreviewPeriodKeys,
            PreviewResultJson = x.PreviewResultJson,
            PreviewError = x.PreviewError,
            PreviewRequestedAtUtc = x.PreviewRequestedAtUtc,
            PreviewFinishedAtUtc = x.PreviewFinishedAtUtc,
            LockedAtUtc = x.LockedAtUtc,
            LockedByUserId = x.LockedByUserId,
            LockTokenId = x.LockTokenId,
            RequiresPreviewToLock = requiresPreview,
            RequiresTokenToLock = requiresToken,
            CanPreview = x.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Draft && !gateBlocked,
            CanLock = x.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Draft &&
                      !requiresPreview &&
                      !gateBlocked,
            FieldGateStatus = gate.Status,
            FieldGateReason = gate.Reason,
            IsCumulative = gate.IsCumulative,
            SectionFieldCount = gate.SectionFieldCount,
            TargetFieldCount = gate.TargetFieldCount,
            FieldLimit = gate.FieldLimit,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc
        };
    }

    private static DynamicFormSectionInfo ResolveSection(DynamicFormTemplate template, string sectionId)
    {
        try
        {
            var sections = JsonSerializer.Deserialize<List<DynamicFormSectionInfo>>(template.SectionsJson, JsonOptions)
                           ?? new List<DynamicFormSectionInfo>();
            var section = sections.FirstOrDefault(x => string.Equals(x.Id, sectionId, StringComparison.Ordinal));
            if (section is not null && !string.IsNullOrWhiteSpace(section.Id))
                return section with { Id = section.Id.Trim(), Title = string.IsNullOrWhiteSpace(section.Title) ? null : section.Title.Trim() };
        }
        catch (JsonException)
        {
            // Re-throw a product-level error below.
        }

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
            new
            {
                dynamicFormTemplateId = template.Id,
                sectionId,
                reason = "ADVANCED_SUMMARY_SECTION_NOT_FOUND"
            });
    }

    private static string NormalizeConfigJson(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            configJson = "{}";

        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = "configJson", reason = "ADVANCED_SUMMARY_CONFIG_JSON_OBJECT_REQUIRED" });
            }

            return JsonSerializer.Serialize(document.RootElement, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "configJson", reason = "ADVANCED_SUMMARY_CONFIG_JSON_INVALID", ex.Message });
        }
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

    private sealed record AdvancedPreviewConfigAnalysis(
        List<AdvancedPreviewTargetSpec> Targets,
        List<string> UnknownFieldRefs,
        List<string> UnsupportedFeatures,
        bool HasExplicitTargets);

    private sealed record AdvancedPreviewTargetSpec(
        string FieldRef,
        string? Method);

    private sealed record AdvancedPreviewTarget(
        AdvancedFieldDefinition Field,
        string Method);

    private static class FieldGateStatuses
    {
        public const string Allowed = "ALLOWED";
        public const string Blocked = "BLOCKED";
        public const string Unknown = "UNKNOWN";
    }

    private sealed record AdvancedSummaryFieldGateInfo(
        string Status,
        bool IsCumulative,
        int SectionFieldCount,
        int TargetFieldCount,
        int FieldLimit,
        string? Reason)
    {
        public static AdvancedSummaryFieldGateInfo Unknown { get; } = new(
            FieldGateStatuses.Unknown,
            IsCumulative: false,
            SectionFieldCount: 0,
            TargetFieldCount: 0,
            FieldLimit: 0,
            Reason: null);
    }

    private sealed record FieldOption(string Code, string Label);

    private sealed record AdvancedFieldDefinition(
        string FieldId,
        string FieldKey,
        string FieldLabel,
        string FieldType,
        List<FieldOption> Options);

    private sealed class PreviewPeriodAccumulator
    {
        public PreviewPeriodAccumulator(string periodKey)
        {
            PeriodKey = periodKey;
        }

        public string PeriodKey { get; }
        public int ReportCount { get; set; }
        public int SectionReportCount { get; set; }
        public int ValueCount { get; set; }
        public HashSet<string> FieldsWithData { get; } = new(StringComparer.Ordinal);

        public AdvancedSummaryPreviewPeriodDto ToDto()
            => new()
            {
                PeriodKey = PeriodKey,
                ReportCount = ReportCount,
                SectionReportCount = SectionReportCount,
                ValueCount = ValueCount,
                FieldsWithDataCount = FieldsWithData.Count
            };
    }

    private sealed class PreviewTargetAccumulator
    {
        private readonly List<string> _samples = new();
        private readonly Dictionary<string, int> _bucketCounts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _periodKeys = new(StringComparer.Ordinal);
        private decimal _sum;
        private decimal? _min;
        private decimal? _max;
        private int _numberCount;
        private DateTime? _minDate;
        private DateTime? _maxDate;
        private int _trueCount;
        private int _falseCount;

        public PreviewTargetAccumulator(AdvancedFieldDefinition field, string method)
        {
            Field = field;
            Method = method;
        }

        public AdvancedFieldDefinition Field { get; }
        public string Method { get; }
        public int ValueCount { get; private set; }

        public void Add(JsonElement value, string periodKey)
        {
            if (IsBlankJsonElement(value))
                return;

            var displayValues = ReadDisplayValues(value, Field);
            if (displayValues.Count == 0 && Field.FieldType is not ("number" or "boolean" or "date" or "dateTime"))
                return;

            _periodKeys.Add(periodKey);

            if (Field.FieldType == "number")
            {
                var number = ToNullableDecimal(value);
                if (!number.HasValue)
                    return;

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

                ValueCount++;
                if (boolean) _trueCount++; else _falseCount++;
                AddSample(boolean ? "true" : "false");
                return;
            }

            foreach (var displayValue in displayValues)
            {
                if (string.IsNullOrWhiteSpace(displayValue))
                    continue;

                ValueCount++;
                AddSample(displayValue);
                _bucketCounts[displayValue] = _bucketCounts.TryGetValue(displayValue, out var count) ? count + 1 : 1;
            }
        }

        public AdvancedSummaryPreviewFieldDto ToDto()
            => new()
            {
                FieldId = Field.FieldId,
                FieldKey = Field.FieldKey,
                Label = Field.FieldLabel,
                DataType = FieldTypeToSummaryDataType(Field.FieldType),
                Method = Method,
                ValueCount = ValueCount,
                Result = BuildResult(),
                SampleValues = _samples.ToList(),
                PeriodKeys = _periodKeys.OrderByDescending(x => x, StringComparer.Ordinal).ToList()
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
            if (string.IsNullOrWhiteSpace(value) || _samples.Count >= PreviewFieldSampleLimit)
                return;

            if (value.Length > PreviewSampleTextLimit)
                value = value[..PreviewSampleTextLimit];
            if (!_samples.Contains(value, StringComparer.Ordinal))
                _samples.Add(value);
        }
    }

    private sealed class AdvancedSummaryPreviewResult
    {
        public int SchemaVersion { get; set; }
        public string Kind { get; set; } = PreviewResultKind;
        public DateTime GeneratedAtUtc { get; set; }
        public string ConfigId { get; set; } = string.Empty;
        public string ConfigHash { get; set; } = string.Empty;
        public string WorkId { get; set; } = string.Empty;
        public string AssignmentId { get; set; } = string.Empty;
        public string DynamicFormTemplateId { get; set; } = string.Empty;
        public string SectionId { get; set; } = string.Empty;
        public string? SectionTitle { get; set; }
        public List<string> PeriodKeys { get; set; } = new();
        public int SourceAssignmentCount { get; set; }
        public int SourceReportCount { get; set; }
        public int SectionReportCount { get; set; }
        public int SectionFieldCount { get; set; }
        public int TargetFieldCount { get; set; }
        public List<string> Warnings { get; set; } = new();
        public List<AdvancedSummaryPreviewPeriodDto> Periods { get; set; } = new();
        public List<AdvancedSummaryPreviewFieldDto> Fields { get; set; } = new();
    }

    private sealed class AdvancedSummaryPreviewPeriodDto
    {
        public string PeriodKey { get; set; } = string.Empty;
        public int ReportCount { get; set; }
        public int SectionReportCount { get; set; }
        public int ValueCount { get; set; }
        public int FieldsWithDataCount { get; set; }
    }

    private sealed class AdvancedSummaryPreviewFieldDto
    {
        public string FieldId { get; set; } = string.Empty;
        public string FieldKey { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public int ValueCount { get; set; }
        public object? Result { get; set; }
        public List<string> SampleValues { get; set; } = new();
        public List<string> PeriodKeys { get; set; } = new();
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

    private sealed record AdvancedSummaryContext(
        WorkAssignment Scope,
        DynamicFormTemplate Template,
        DynamicFormSectionInfo Section);

    private sealed record DynamicFormSectionInfo(
        string Id,
        string? Title);
}
