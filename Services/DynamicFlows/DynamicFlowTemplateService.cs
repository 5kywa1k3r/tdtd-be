using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicFlows;

public sealed class DynamicFlowTemplateService : IDynamicFlowTemplateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static readonly string[] RequiredArrayProperties =
    {
        "steps",
        "transitions",
        "actorPolicies",
        "fieldPolicies",
        "tableColumnPolicies",
        "mappingRules"
    };

    private readonly MongoDbContext _ctx;

    public DynamicFlowTemplateService(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<PagedResult<DynamicFlowTemplateDto>> SearchAsync(
        DynamicFlowTemplateSearchRequest req,
        CancellationToken ct = default)
    {
        req ??= new DynamicFlowTemplateSearchRequest();
        var page = Math.Max(0, req.Page);
        var pageSize = Math.Clamp(req.PageSize <= 0 ? 20 : req.PageSize, 1, 100);

        var fb = Builders<DynamicFlowTemplate>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);

        var status = NormalizeStatusOrNull(req.Status, allowDraft: true);
        if (!string.IsNullOrWhiteSpace(status))
            filter &= fb.Eq(x => x.Status, status);

        var dynamicFormTemplateId = NormalizeOptionalObjectId(req.DynamicFormTemplateId, "dynamicFormTemplateId");
        if (!string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId);

        var query = req.Query?.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var regex = new BsonRegularExpression(Regex.Escape(query), "i");
            filter &= fb.Regex(x => x.Code, regex) | fb.Regex(x => x.Name, regex);
        }

        var total = await _ctx.DynamicFlowTemplates.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.DynamicFlowTemplates
            .Find(filter)
            .Sort(Builders<DynamicFlowTemplate>.Sort.Descending(x => x.UpdatedAtUtc))
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return new PagedResult<DynamicFlowTemplateDto>(
            rows.Select(x => MapTemplate(x, versions: null)).ToList(),
            total,
            page,
            pageSize);
    }

    public async Task<DynamicFlowTemplateDto> GetAsync(
        string id,
        CancellationToken ct = default)
    {
        var template = await LoadTemplateAsync(id, ct);
        var versions = await LoadVersionsAsync(template.Id, ct);
        var usedVersionNos = await LoadUsedVersionNosAsync(template.Id, ct);
        return MapTemplate(template, versions.Select(x => MapVersion(x, usedVersionNos.Contains(x.VersionNo))).ToList());
    }

    public async Task<DynamicFlowTemplateDto> CreateAsync(
        CreateDynamicFlowTemplateRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new CreateDynamicFlowTemplateRequest();
        var code = NormalizeCode(req.Code);
        var name = NormalizeRequiredText(req.Name, "name", maxLength: 240);
        var description = NormalizeOptionalText(req.Description, maxLength: 2000);
        var dynamicFormTemplateId = NormalizeOptionalObjectId(req.DynamicFormTemplateId, "dynamicFormTemplateId");
        var dynamicFormTemplate = await LoadDynamicFormTemplateOrNullAsync(dynamicFormTemplateId, ct);
        await EnsureCodeAvailableAsync(code, exceptTemplateId: null, ct);

        var payloadJson = NormalizePayloadJson(req.PayloadJson, requireLockable: false, dynamicFormTemplate);
        var payloadHash = Sha256(payloadJson);
        var now = DateTime.UtcNow;
        var template = new DynamicFlowTemplate
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Code = code,
            Name = name,
            Description = description,
            DynamicFormTemplateId = dynamicFormTemplateId,
            Status = DynamicFlowTemplateStatuses.Draft,
            IsDeleted = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId
        };

        var draft = new DynamicFlowTemplateVersion
        {
            Id = ObjectId.GenerateNewId().ToString(),
            TemplateId = template.Id,
            DynamicFormTemplateId = dynamicFormTemplateId,
            VersionNo = 1,
            Status = DynamicFlowTemplateVersionStatuses.Draft,
            DraftRevision = 1,
            PayloadJson = payloadJson,
            PayloadHash = payloadHash,
            IsDeleted = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId
        };

        await _ctx.DynamicFlowTemplates.InsertOneAsync(template, cancellationToken: ct);
        await _ctx.DynamicFlowTemplateVersions.InsertOneAsync(draft, cancellationToken: ct);

        return MapTemplate(template, new List<DynamicFlowTemplateVersionDto> { MapVersion(draft, isUsed: false) });
    }

    public async Task<DynamicFlowTemplateDto> UpdateAsync(
        string id,
        UpdateDynamicFlowTemplateRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new UpdateDynamicFlowTemplateRequest();
        var template = await LoadTemplateAsync(id, ct);
        await EnsureCanManageAsync(template, actorUserId, ct);
        EnsureTemplateNotArchived(template);

        var code = string.IsNullOrWhiteSpace(req.Code) ? template.Code : NormalizeCode(req.Code);
        if (!string.Equals(code, template.Code, StringComparison.Ordinal))
            await EnsureCodeAvailableAsync(code, exceptTemplateId: template.Id, ct);

        var dynamicFormTemplateId = req.DynamicFormTemplateId is null
            ? template.DynamicFormTemplateId
            : NormalizeOptionalObjectId(req.DynamicFormTemplateId, "dynamicFormTemplateId");
        _ = await LoadDynamicFormTemplateOrNullAsync(dynamicFormTemplateId, ct);

        template.Code = code;
        template.Name = string.IsNullOrWhiteSpace(req.Name)
            ? template.Name
            : NormalizeRequiredText(req.Name, "name", maxLength: 240);
        template.Description = req.Description is null
            ? template.Description
            : NormalizeOptionalText(req.Description, maxLength: 2000);
        template.DynamicFormTemplateId = dynamicFormTemplateId;
        template.UpdatedAtUtc = DateTime.UtcNow;
        template.UpdatedByUserId = actorUserId;

        await _ctx.DynamicFlowTemplates.ReplaceOneAsync(x => x.Id == template.Id, template, cancellationToken: ct);
        return await GetAsync(template.Id, ct);
    }

    public async Task<List<DynamicFlowTemplateVersionDto>> ListVersionsAsync(
        string templateId,
        CancellationToken ct = default)
    {
        var template = await LoadTemplateAsync(templateId, ct);
        var versions = await LoadVersionsAsync(template.Id, ct);
        var usedVersionNos = await LoadUsedVersionNosAsync(template.Id, ct);
        return versions.Select(x => MapVersion(x, usedVersionNos.Contains(x.VersionNo))).ToList();
    }

    public async Task<DynamicFlowTemplateVersionDto> SaveDraftVersionAsync(
        string templateId,
        SaveDynamicFlowTemplateVersionDraftRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new SaveDynamicFlowTemplateVersionDraftRequest();
        var template = await LoadTemplateAsync(templateId, ct);
        await EnsureCanManageAsync(template, actorUserId, ct);
        EnsureTemplateNotArchived(template);

        var dynamicFormTemplate = await LoadDynamicFormTemplateOrNullAsync(template.DynamicFormTemplateId, ct);
        var payloadJson = NormalizePayloadJson(req.PayloadJson, requireLockable: false, dynamicFormTemplate);
        var payloadHash = Sha256(payloadJson);
        var now = DateTime.UtcNow;
        var existingDraft = await _ctx.DynamicFlowTemplateVersions
            .Find(x =>
                x.TemplateId == template.Id &&
                x.Status == DynamicFlowTemplateVersionStatuses.Draft &&
                !x.IsDeleted)
            .SortByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (existingDraft is not null)
        {
            existingDraft.DraftRevision++;
            existingDraft.DynamicFormTemplateId = template.DynamicFormTemplateId;
            existingDraft.PayloadJson = payloadJson;
            existingDraft.PayloadHash = payloadHash;
            existingDraft.UpdatedAtUtc = now;
            existingDraft.UpdatedByUserId = actorUserId;
            await _ctx.DynamicFlowTemplateVersions.ReplaceOneAsync(
                x => x.Id == existingDraft.Id,
                existingDraft,
                cancellationToken: ct);
            return MapVersion(existingDraft, isUsed: false);
        }

        var version = new DynamicFlowTemplateVersion
        {
            Id = ObjectId.GenerateNewId().ToString(),
            TemplateId = template.Id,
            DynamicFormTemplateId = template.DynamicFormTemplateId,
            VersionNo = await NextVersionNoAsync(template.Id, ct),
            Status = DynamicFlowTemplateVersionStatuses.Draft,
            DraftRevision = 1,
            PayloadJson = payloadJson,
            PayloadHash = payloadHash,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };

        await _ctx.DynamicFlowTemplateVersions.InsertOneAsync(version, cancellationToken: ct);
        return MapVersion(version, isUsed: false);
    }

    public async Task<DynamicFlowTemplateVersionDto> LockVersionAsync(
        string versionId,
        LockDynamicFlowTemplateVersionRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        var version = await LoadVersionAsync(versionId, ct);
        var template = await LoadTemplateAsync(version.TemplateId, ct);
        await EnsureCanManageAsync(template, actorUserId, ct);
        EnsureTemplateNotArchived(template);

        if (version.Status == DynamicFlowTemplateVersionStatuses.Locked)
            return MapVersion(version, await IsVersionUsedAsync(template.Id, version.VersionNo, ct));

        if (version.Status != DynamicFlowTemplateVersionStatuses.Draft)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { versionId, version.Status, reason = "DYNAMIC_FLOW_TEMPLATE_VERSION_NOT_LOCKABLE" });
        }

        var dynamicFormTemplate = await LoadDynamicFormTemplateOrNullAsync(version.DynamicFormTemplateId, ct);
        var normalizedPayload = NormalizePayloadJson(version.PayloadJson, requireLockable: true, dynamicFormTemplate);
        var now = DateTime.UtcNow;
        version.PayloadJson = normalizedPayload;
        version.PayloadHash = Sha256(normalizedPayload);
        version.Status = DynamicFlowTemplateVersionStatuses.Locked;
        version.LockedAtUtc = now;
        version.LockedByUserId = actorUserId;
        version.UpdatedAtUtc = now;
        version.UpdatedByUserId = actorUserId;

        template.Status = DynamicFlowTemplateStatuses.Active;
        template.CurrentVersionId = version.Id;
        template.CurrentVersionNo = version.VersionNo;
        template.CurrentVersionHash = version.PayloadHash;
        template.UpdatedAtUtc = now;
        template.UpdatedByUserId = actorUserId;

        await _ctx.DynamicFlowTemplateVersions.ReplaceOneAsync(x => x.Id == version.Id, version, cancellationToken: ct);
        await _ctx.DynamicFlowTemplates.ReplaceOneAsync(x => x.Id == template.Id, template, cancellationToken: ct);

        return MapVersion(version, isUsed: false);
    }

    public async Task DeleteAsync(
        string id,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        var template = await LoadTemplateAsync(id, ct);
        await EnsureCanManageAsync(template, actorUserId, ct);
        var usedVersionNos = await LoadUsedVersionNosAsync(template.Id, ct);
        if (usedVersionNos.Count > 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { templateId = template.Id, usedVersionNos, reason = "DYNAMIC_FLOW_TEMPLATE_USED" });
        }

        var now = DateTime.UtcNow;
        var templateUpdate = Builders<DynamicFlowTemplate>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.DeletedAtUtc, now)
            .Set(x => x.DeletedByUserId, actorUserId)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);

        await _ctx.DynamicFlowTemplates.UpdateOneAsync(
            x => x.Id == template.Id && !x.IsDeleted,
            templateUpdate,
            cancellationToken: ct);

        var versionUpdate = Builders<DynamicFlowTemplateVersion>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.DeletedAtUtc, now)
            .Set(x => x.DeletedByUserId, actorUserId)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);

        await _ctx.DynamicFlowTemplateVersions.UpdateManyAsync(
            x => x.TemplateId == template.Id && !x.IsDeleted,
            versionUpdate,
            cancellationToken: ct);
    }

    private async Task<DynamicFlowTemplate> LoadTemplateAsync(string id, CancellationToken ct)
    {
        id = id?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_ARGUMENT_REQUIRED, new { field = "templateId" });

        return await _ctx.DynamicFlowTemplates
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(AppErrorCode.COMMON_NOT_FOUND, new { templateId = id });
    }

    private async Task<DynamicFlowTemplateVersion> LoadVersionAsync(string id, CancellationToken ct)
    {
        id = id?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_ARGUMENT_REQUIRED, new { field = "versionId" });

        return await _ctx.DynamicFlowTemplateVersions
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(AppErrorCode.COMMON_NOT_FOUND, new { versionId = id });
    }

    private async Task<List<DynamicFlowTemplateVersion>> LoadVersionsAsync(string templateId, CancellationToken ct)
        => await _ctx.DynamicFlowTemplateVersions
            .Find(x => x.TemplateId == templateId && !x.IsDeleted)
            .Sort(Builders<DynamicFlowTemplateVersion>.Sort
                .Descending(x => x.VersionNo)
                .Descending(x => x.UpdatedAtUtc))
            .ToListAsync(ct);

    private async Task<int> NextVersionNoAsync(string templateId, CancellationToken ct)
    {
        var latest = await _ctx.DynamicFlowTemplateVersions
            .Find(x => x.TemplateId == templateId && !x.IsDeleted)
            .SortByDescending(x => x.VersionNo)
            .FirstOrDefaultAsync(ct);

        return Math.Max(1, (latest?.VersionNo ?? 0) + 1);
    }

    private async Task<HashSet<int>> LoadUsedVersionNosAsync(string templateId, CancellationToken ct)
    {
        var rows = await _ctx.WorkAssignments
            .Find(x => x.FlowTemplateId == templateId && x.FlowTemplateVersionNo != null && !x.IsDeleted)
            .Project(x => x.FlowTemplateVersionNo)
            .ToListAsync(ct);

        return rows
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToHashSet();
    }

    private async Task<bool> IsVersionUsedAsync(string templateId, int versionNo, CancellationToken ct)
        => await _ctx.WorkAssignments.CountDocumentsAsync(
            x =>
                x.FlowTemplateId == templateId &&
                x.FlowTemplateVersionNo == versionNo &&
                !x.IsDeleted,
            cancellationToken: ct) > 0;

    private async Task EnsureCanManageAsync(DynamicFlowTemplate template, string actorUserId, CancellationToken ct)
    {
        if (string.Equals(template.CreatedByUserId, actorUserId, StringComparison.Ordinal))
            return;

        var actor = await _ctx.Users
            .Find(x => x.Id == actorUserId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.Unauthorized();

        if (HasRole(actor, "ADMIN") || HasRole(actor, "SYSTEM_ADMIN"))
            return;

        throw AppExceptionFactory.Forbidden(
            AppErrorCode.AUTH_FORBIDDEN,
            new { templateId = template.Id, actorUserId, reason = "DYNAMIC_FLOW_TEMPLATE_MANAGE_FORBIDDEN" });
    }

    private static bool HasRole(AppUser actor, string role)
        => actor.Roles.Any(x => string.Equals(x, role, StringComparison.OrdinalIgnoreCase));

    private async Task EnsureCodeAvailableAsync(string code, string? exceptTemplateId, CancellationToken ct)
    {
        var exists = await _ctx.DynamicFlowTemplates
            .Find(x =>
                x.Code == code &&
                x.Id != exceptTemplateId &&
                !x.IsDeleted)
            .AnyAsync(ct);

        if (exists)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { code, reason = "DYNAMIC_FLOW_TEMPLATE_CODE_DUPLICATE" });
        }
    }

    private static void EnsureTemplateNotArchived(DynamicFlowTemplate template)
    {
        if (template.Status == DynamicFlowTemplateStatuses.Archived)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { templateId = template.Id, reason = "DYNAMIC_FLOW_TEMPLATE_ARCHIVED" });
        }
    }

    private static DynamicFlowTemplateDto MapTemplate(
        DynamicFlowTemplate template,
        List<DynamicFlowTemplateVersionDto>? versions)
    {
        versions ??= new List<DynamicFlowTemplateVersionDto>();
        return new DynamicFlowTemplateDto
        {
            Id = template.Id,
            Code = template.Code,
            Name = template.Name,
            Description = template.Description,
            DynamicFormTemplateId = template.DynamicFormTemplateId,
            Status = template.Status,
            CurrentVersionId = template.CurrentVersionId,
            CurrentVersionNo = template.CurrentVersionNo,
            CurrentVersionHash = template.CurrentVersionHash,
            CurrentVersion = versions.FirstOrDefault(x => string.Equals(x.Id, template.CurrentVersionId, StringComparison.Ordinal)),
            DraftVersion = versions.FirstOrDefault(x => x.Status == DynamicFlowTemplateVersionStatuses.Draft),
            Versions = versions,
            CreatedAtUtc = template.CreatedAtUtc,
            UpdatedAtUtc = template.UpdatedAtUtc
        };
    }

    private static DynamicFlowTemplateVersionDto MapVersion(DynamicFlowTemplateVersion version, bool isUsed)
        => new()
        {
            Id = version.Id,
            TemplateId = version.TemplateId,
            DynamicFormTemplateId = version.DynamicFormTemplateId,
            VersionNo = version.VersionNo,
            Status = version.Status,
            DraftRevision = version.DraftRevision,
            PayloadJson = version.PayloadJson,
            PayloadHash = version.PayloadHash,
            IsUsed = isUsed,
            LockedAtUtc = version.LockedAtUtc,
            LockedByUserId = version.LockedByUserId,
            ArchivedAtUtc = version.ArchivedAtUtc,
            ArchivedByUserId = version.ArchivedByUserId,
            CreatedAtUtc = version.CreatedAtUtc,
            UpdatedAtUtc = version.UpdatedAtUtc
        };

    internal static string NormalizePayloadJson(
        string? payloadJson,
        bool requireLockable,
        DynamicFormTemplate? dynamicFormTemplate = null)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            payloadJson = "{}";

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payloadJson);
        }
        catch (JsonException ex)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "payloadJson", reason = "DYNAMIC_FLOW_TEMPLATE_PAYLOAD_JSON_INVALID", ex.Message });
        }

        if (node is not JsonObject root)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "payloadJson", reason = "DYNAMIC_FLOW_TEMPLATE_PAYLOAD_OBJECT_REQUIRED" });
        }

        foreach (var property in RequiredArrayProperties)
            EnsureArrayProperty(root, property);

        EnsureObjectProperty(root, "rollbackPolicy");
        EnsureObjectProperty(root, "finalResultPolicy");
        EnsureObjectProperty(root, "statisticProfile");

        if (requireLockable)
            ValidateLockablePayload(root);

        DynamicFlowPolicyValidator.ValidatePayload(root, dynamicFormTemplate);

        return root.ToJsonString(JsonOptions);
    }

    private async Task<DynamicFormTemplate?> LoadDynamicFormTemplateOrNullAsync(
        string? dynamicFormTemplateId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            return null;

        return await _ctx.DynamicFormTemplates
            .Find(x => x.Id == dynamicFormTemplateId && !x.IsDeleted && x.IsActive)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.COMMON_NOT_FOUND,
                new { dynamicFormTemplateId, reason = "DYNAMIC_FORM_TEMPLATE_NOT_FOUND" });
    }

    private static void ValidateLockablePayload(JsonObject root)
    {
        var steps = root["steps"] as JsonArray;
        if (steps is null || steps.Count == 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "steps", reason = "DYNAMIC_FLOW_TEMPLATE_STEPS_REQUIRED" });
        }

        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        var stepCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in steps)
        {
            if (item is not JsonObject step)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = "steps", reason = "DYNAMIC_FLOW_TEMPLATE_STEP_OBJECT_REQUIRED" });
            }

            var stepId = PickString(step, "stepId", "id");
            var stepCode = PickString(step, "stepCode", "code");
            if (string.IsNullOrWhiteSpace(stepId))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = "steps.stepId", reason = "DYNAMIC_FLOW_TEMPLATE_STEP_ID_REQUIRED" });
            }

            if (string.IsNullOrWhiteSpace(stepCode))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = "steps.stepCode", reason = "DYNAMIC_FLOW_TEMPLATE_STEP_CODE_REQUIRED" });
            }

            if (!stepIds.Add(stepId))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = "steps.stepId", stepId, reason = "DYNAMIC_FLOW_TEMPLATE_STEP_ID_DUPLICATE" });
            }

            if (!stepCodes.Add(stepCode))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = "steps.stepCode", stepCode, reason = "DYNAMIC_FLOW_TEMPLATE_STEP_CODE_DUPLICATE" });
            }
        }
    }

    private static void EnsureArrayProperty(JsonObject root, string property)
    {
        if (!root.TryGetPropertyValue(property, out var value) || value is null)
        {
            root[property] = new JsonArray();
            return;
        }

        if (value is not JsonArray)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = property, reason = "DYNAMIC_FLOW_TEMPLATE_ARRAY_REQUIRED" });
        }
    }

    private static void EnsureObjectProperty(JsonObject root, string property)
    {
        if (!root.TryGetPropertyValue(property, out var value) || value is null)
        {
            root[property] = new JsonObject();
            return;
        }

        if (value is not JsonObject)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = property, reason = "DYNAMIC_FLOW_TEMPLATE_OBJECT_REQUIRED" });
        }
    }

    private static string? PickString(JsonObject root, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (root.TryGetPropertyValue(property, out var node) && node is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }

    private static string NormalizeCode(string? code)
    {
        code = NormalizeRequiredText(code, "code", maxLength: 80).ToUpperInvariant();
        if (!Regex.IsMatch(code, "^[A-Z0-9][A-Z0-9_.-]*$"))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "code", reason = "DYNAMIC_FLOW_TEMPLATE_CODE_INVALID" });
        }

        return code;
    }

    private static string NormalizeRequiredText(string? value, string field, int maxLength)
    {
        value = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_ARGUMENT_REQUIRED, new { field });
        if (value.Length > maxLength)
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, new { field, maxLength });
        return value;
    }

    private static string? NormalizeOptionalText(string? value, int maxLength)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Length > maxLength)
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, new { field = "description", maxLength });
        return value;
    }

    private static string? NormalizeStatusOrNull(string? status, bool allowDraft)
    {
        status = status?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(status))
            return null;

        var allowed = allowDraft
            ? new[] { DynamicFlowTemplateStatuses.Draft, DynamicFlowTemplateStatuses.Active, DynamicFlowTemplateStatuses.Archived }
            : new[] { DynamicFlowTemplateStatuses.Active, DynamicFlowTemplateStatuses.Archived };

        if (!allowed.Contains(status, StringComparer.Ordinal))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "status", status, reason = "DYNAMIC_FLOW_TEMPLATE_STATUS_INVALID" });
        }

        return status;
    }

    private static string? NormalizeOptionalObjectId(string? value, string field)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!ObjectId.TryParse(value, out _))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field, reason = "OBJECT_ID_INVALID" });
        }

        return value;
    }

    private static void EnsureActor(string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            throw AppExceptionFactory.Unauthorized();
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
