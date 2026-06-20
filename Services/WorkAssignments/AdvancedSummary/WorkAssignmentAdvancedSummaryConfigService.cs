using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public sealed class WorkAssignmentAdvancedSummaryConfigService : IWorkAssignmentAdvancedSummaryConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly MongoDbContext _ctx;

    public WorkAssignmentAdvancedSummaryConfigService(MongoDbContext ctx)
    {
        _ctx = ctx;
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

        return rows.Select(x => Map(x, lockedCount)).ToList();
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

        return Map(entity, lockedCount);
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
        if (entity.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Locked)
            return Map(entity, lockedCount);

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

        if (lockedCount > 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    configId,
                    tokenId = req?.TokenId,
                    reason = "ADVANCED_SUMMARY_TOKEN_REQUIRED"
                },
                "Changing a locked Advanced summary config requires token governance in a later phase.");
        }

        var now = DateTime.UtcNow;
        entity.Status = WorkAssignmentAdvancedSummaryConfigStatuses.Locked;
        entity.VersionNo = await NextVersionNoAsync(context.Scope.Id, context.Template.Id, context.Section.Id, ct);
        entity.LockedAtUtc = now;
        entity.LockedByUserId = actorUserId;
        entity.LockTokenId = null;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = actorUserId;

        await _ctx.WorkAssignmentAdvancedSummaryConfigs.ReplaceOneAsync(x => x.Id == entity.Id, entity, cancellationToken: ct);

        return Map(entity, lockedCount + 1);
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
        long lockedCount)
    {
        var isLocked = x.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Locked;
        var requiresPreview = !isLocked && x.PreviewStatus != WorkAssignmentAdvancedSummaryPreviewStatuses.Done;
        var requiresToken = !isLocked && lockedCount > 0;

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
            CanLock = x.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Draft &&
                      !requiresPreview &&
                      !requiresToken,
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

    private sealed record AdvancedSummaryContext(
        WorkAssignment Scope,
        DynamicFormTemplate Template,
        DynamicFormSectionInfo Section);

    private sealed record DynamicFormSectionInfo(
        string Id,
        string? Title);
}
