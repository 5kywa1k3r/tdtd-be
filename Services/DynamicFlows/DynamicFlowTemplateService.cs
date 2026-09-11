using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Capabilities;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.Services.DynamicFlows;

public sealed partial class DynamicFlowTemplateService : IDynamicFlowTemplateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static readonly string[] RequiredArrayProperties =
    {
        "formNodes",
        "steps",
        "transitions",
        "actorPolicies",
        "fieldPolicies",
        "tableColumnPolicies",
        "mappingRules"
    };

    private readonly MongoDbContext _ctx;
    private readonly IDynamicFlowDefinitionTransactionRunner _transactions;
    private readonly IDynamicFlowDefinitionFaultInjector _faults;

    public DynamicFlowTemplateService(
        MongoDbContext ctx,
        IDynamicFlowDefinitionTransactionRunner transactions,
        IDynamicFlowDefinitionFaultInjector faults)
    {
        _ctx = ctx;
        _transactions = transactions;
        _faults = faults;
    }

    public async Task<PagedResult<DynamicFlowTemplateDto>> SearchAsync(
        DynamicFlowTemplateSearchRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new DynamicFlowTemplateSearchRequest();
        var page = Math.Max(0, req.Page);
        var pageSize = req.PageSize <= 0 ? 20 : req.PageSize;
        if (pageSize > 500)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { path = "pageSize", limit = 500, reason = "PAGE_SIZE_LIMIT_EXCEEDED" });
        }

        var fb = Builders<DynamicFlowTemplate>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        var actorVersionNos = await LoadParticipantVersionNosAsync(actorUserId, ct);
        var isAdministrator = DynamicFlowTemplateReadAccess.IsAdministrator(actor);
        if (!isAdministrator)
        {
            var participantTemplateIds = actorVersionNos.Keys.ToHashSet(StringComparer.Ordinal);
            var visibleFilter = fb.Eq(x => x.OwnerUserId, actorUserId) |
                                ((fb.Eq(x => x.OwnerUserId, null) | fb.Exists(x => x.OwnerUserId, false)) &
                                 fb.Eq(x => x.CreatedByUserId, actorUserId));
            if (participantTemplateIds.Count > 0)
                visibleFilter |= fb.In(x => x.Id, participantTemplateIds);
            filter &= visibleFilter;
        }

        var status = NormalizeStatusOrNull(req.Status, allowDraft: true);
        if (!string.IsNullOrWhiteSpace(status))
            filter &= fb.Eq(x => x.Status, status);

        var rootDynamicFormTemplateId = NormalizeOptionalObjectId(
            FirstNonBlank(req.RootDynamicFormTemplateId, req.DynamicFormTemplateId),
            "rootDynamicFormTemplateId");
        if (!string.IsNullOrWhiteSpace(rootDynamicFormTemplateId))
            filter &= fb.Eq(x => x.RootDynamicFormTemplateId, rootDynamicFormTemplateId);

        var query = req.Query?.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var regex = new BsonRegularExpression(Regex.Escape(query), "i");
            filter &= fb.Regex(x => x.Code, regex) | fb.Regex(x => x.Name, regex);
        }

        var total = await _ctx.DynamicFlowTemplates.CountDocumentsAsync(filter, cancellationToken: ct);
        var sortDirection = string.Equals(req.SortDirection?.Trim(), "asc", StringComparison.OrdinalIgnoreCase)
            ? 1
            : -1;
        var sortField = req.SortBy?.Trim().ToLowerInvariant();
        var primarySort = sortField switch
        {
            "code" => new BsonDocument("code", sortDirection),
            "name" => new BsonDocument("name", sortDirection),
            "status" => new BsonDocument("status", sortDirection),
            "createdatutc" => new BsonDocument("createdAtUtc", sortDirection),
            _ => new BsonDocument("updatedAtUtc", sortDirection)
        };
        var sort = new BsonDocumentSortDefinition<DynamicFlowTemplate>(
            new BsonDocument
            {
                { primarySort.GetElement(0).Name, primarySort.GetElement(0).Value },
                { "_id", sortDirection }
            });
        var rows = await _ctx.DynamicFlowTemplates
            .Find(filter)
            .Sort(sort)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        var pageTemplateIds = rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).ToList();
        var versionRows = new List<DynamicFlowTemplateVersion>();
        var usedVersionKeys = new HashSet<string>(StringComparer.Ordinal);
        if (pageTemplateIds.Count > 0)
        {
            var unrestrictedRows = isAdministrator
                ? rows
                : rows
                    .Where(row => DynamicFlowTemplateReadAccess.IsOwner(row, actorUserId))
                    .ToList();
            var unrestrictedTemplateIds = unrestrictedRows
                .Select(row => row.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var restrictedRows = isAdministrator
                ? new List<DynamicFlowTemplate>()
                : rows
                    .Where(row => !DynamicFlowTemplateReadAccess.IsOwner(row, actorUserId))
                    .ToList();

            var versionFilters = new List<FilterDefinition<DynamicFlowTemplateVersion>>();
            var versionFilterBuilder = Builders<DynamicFlowTemplateVersion>.Filter;
            if (unrestrictedTemplateIds.Count > 0)
            {
                var unrestrictedVersionFilters = new List<FilterDefinition<DynamicFlowTemplateVersion>>
                {
                    versionFilterBuilder.In(version => version.TemplateId, unrestrictedTemplateIds) &
                    versionFilterBuilder.Eq(version => version.Status, DynamicFlowTemplateVersionStatuses.Draft)
                };
                var unrestrictedCurrentVersionIds = unrestrictedRows
                    .Select(row => row.CurrentVersionId)
                    .Where(versionId => !string.IsNullOrWhiteSpace(versionId))
                    .Select(versionId => versionId!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (unrestrictedCurrentVersionIds.Count > 0)
                {
                    unrestrictedVersionFilters.Add(
                        versionFilterBuilder.In(version => version.Id, unrestrictedCurrentVersionIds));
                }

                // Search rows need the editable draft and pinned current
                // summaries. Full immutable history remains on /versions.
                versionFilters.Add(versionFilterBuilder.Or(unrestrictedVersionFilters));
            }

            var assignmentFilters = new List<FilterDefinition<WorkAssignment>>();
            var assignmentFilterBuilder = Builders<WorkAssignment>.Filter;
            if (unrestrictedTemplateIds.Count > 0)
                assignmentFilters.Add(assignmentFilterBuilder.In(
                    assignment => assignment.FlowTemplateId,
                    unrestrictedTemplateIds));

            foreach (var restrictedRow in restrictedRows)
            {
                if (!actorVersionNos.TryGetValue(restrictedRow.Id, out var grantedVersionNos) ||
                    grantedVersionNos.Count == 0)
                {
                    continue;
                }

                versionFilters.Add(
                    versionFilterBuilder.Eq(version => version.TemplateId, restrictedRow.Id) &
                    versionFilterBuilder.Eq(version => version.Status, DynamicFlowTemplateVersionStatuses.Locked) &
                    versionFilterBuilder.In(version => version.VersionNo, grantedVersionNos));
                assignmentFilters.Add(
                    assignmentFilterBuilder.Eq(assignment => assignment.FlowTemplateId, restrictedRow.Id) &
                    assignmentFilterBuilder.In(
                        assignment => assignment.FlowTemplateVersionNo,
                        grantedVersionNos.Select(versionNo => (int?)versionNo)));
            }

            // The family page is already ACL-filtered. Apply the corresponding
            // exact-version ACL to both projections as well so participant-only
            // requests never materialize draft or ungranted payloads.
            var versionAccessFilter = versionFilterBuilder.Eq(version => version.IsDeleted, false) &
                                      versionFilterBuilder.In(version => version.TemplateId, pageTemplateIds) &
                                      versionFilterBuilder.Or(versionFilters);
            var assignmentAccessFilter = assignmentFilterBuilder.Eq(assignment => assignment.IsDeleted, false) &
                                         assignmentFilterBuilder.Ne(assignment => assignment.FlowTemplateVersionNo, null) &
                                         assignmentFilterBuilder.Or(assignmentFilters);
            var versionTask = _ctx.DynamicFlowTemplateVersions
                .Find(versionAccessFilter)
                .Sort(Builders<DynamicFlowTemplateVersion>.Sort
                    .Ascending(version => version.TemplateId)
                    .Descending(version => version.VersionNo)
                    .Descending(version => version.UpdatedAtUtc))
                .ToListAsync(ct);
            var usedVersionTask = _ctx.WorkAssignments
                .Find(assignmentAccessFilter)
                .Project(assignment => new
                {
                    assignment.FlowTemplateId,
                    assignment.FlowTemplateVersionNo
                })
                .ToListAsync(ct);
            await Task.WhenAll(versionTask, usedVersionTask);
            versionRows = await versionTask;
            usedVersionKeys = (await usedVersionTask)
                .Where(item => !string.IsNullOrWhiteSpace(item.FlowTemplateId) && item.FlowTemplateVersionNo.HasValue)
                .Select(item => VersionKey(item.FlowTemplateId!, item.FlowTemplateVersionNo!.Value))
                .ToHashSet(StringComparer.Ordinal);
        }

        await ValidateLockedVersionsIntegrityAsync(rows, versionRows, ct);
        var versionsByTemplateId = versionRows
            .GroupBy(version => version.TemplateId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var mappedRows = rows.Select(row =>
            {
                var restricted = !isAdministrator &&
                                 !DynamicFlowTemplateReadAccess.IsOwner(row, actorUserId);
                versionsByTemplateId.TryGetValue(row.Id, out var visibleVersionRows);
                visibleVersionRows ??= new List<DynamicFlowTemplateVersion>();
                actorVersionNos.TryGetValue(row.Id, out var grantedVersionNos);
                grantedVersionNos ??= new HashSet<int>();
                if (restricted)
                {
                    visibleVersionRows = visibleVersionRows
                        .Where(version =>
                            version.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                            grantedVersionNos.Contains(version.VersionNo))
                        .ToList();
                }

                var visibleVersions = visibleVersionRows
                    .Select(version => MapVersion(
                        version,
                        usedVersionKeys.Contains(VersionKey(version.TemplateId, version.VersionNo))))
                    .ToList();
                var dto = MapTemplate(
                    row,
                    visibleVersions,
                    restrictCurrentToVisibleVersions: restricted);
                ApplySearchTemplatePermissions(
                    dto,
                    row,
                    actor,
                    actorUserId,
                    grantedVersionNos);
                return dto;
            }).ToList();

        return new PagedResult<DynamicFlowTemplateDto>(
            mappedRows,
            total,
            page,
            pageSize);
    }

    public async Task<DynamicFlowTemplateDto> GetAsync(
        string id,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        var template = await LoadTemplateForReadAsync(id, actor, actorUserId, ct);
        var readableVersionNos = await ResolveReadableVersionNosAsync(template, actor, actorUserId, ct);
        var versions = await LoadVersionsAsync(template.Id, readableVersionNos, ct);
        await ValidateLockedVersionsIntegrityAsync(new[] { template }, versions, ct);
        var usedVersionNos = await LoadUsedVersionNosAsync(template.Id, ct);
        var dto = MapTemplate(
            template,
            versions.Select(x => MapVersion(x, usedVersionNos.Contains(x.VersionNo))).ToList(),
            restrictCurrentToVisibleVersions: readableVersionNos is not null);
        await ApplyTemplatePermissionsAsync(dto, template, actor, actorUserId, ct);
        return dto;
    }

    public async Task<DynamicFlowTemplateDto> CreateAsync(
        CreateDynamicFlowTemplateRequest req,
        string actorUserId,
        CancellationToken ct = default)
        => await CreateDefinitionAsync(req, actorUserId, ct);

    public async Task<DynamicFlowTemplateDto> UpdateAsync(
        string id,
        UpdateDynamicFlowTemplateRequest req,
        string actorUserId,
        CancellationToken ct = default)
        => await UpdateDefinitionMetadataAsync(id, req, actorUserId, ct);

    public async Task<List<DynamicFlowTemplateVersionDto>> ListVersionsAsync(
        string templateId,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        var template = await LoadTemplateForReadAsync(templateId, actor, actorUserId, ct);
        var readableVersionNos = await ResolveReadableVersionNosAsync(template, actor, actorUserId, ct);
        var versions = await LoadVersionsAsync(template.Id, readableVersionNos, ct);
        await ValidateLockedVersionsIntegrityAsync(new[] { template }, versions, ct);
        var usedVersionNos = await LoadUsedVersionNosAsync(template.Id, ct);
        var result = versions.Select(x => MapVersion(x, usedVersionNos.Contains(x.VersionNo))).ToList();
        for (var index = 0; index < result.Count; index++)
            await ApplyVersionPermissionsAsync(result[index], template, versions[index], actor, actorUserId, ct);
        return result;
    }

    public async Task<DynamicFlowTemplateVersionDto> SaveDraftVersionAsync(
        string templateId,
        SaveDynamicFlowTemplateVersionDraftRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        var template = await LoadTemplateForManageAsync(templateId, actor, actorUserId, ct);
        var existingDraft = await _ctx.DynamicFlowTemplateVersions
            .Find(x =>
                x.TemplateId == template.Id &&
                x.Status == DynamicFlowTemplateVersionStatuses.Draft &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (existingDraft is null)
            throw RevisionConflict(template.Id, null, "DYNAMIC_FLOW_DRAFT_REOPEN_REQUIRED");

        return await SaveDraftVersionAsync(template.Id, existingDraft.Id, req, actorUserId, ct);
    }

    public async Task<DynamicFlowTemplateVersionDto> LockVersionAsync(
        string versionId,
        LockDynamicFlowTemplateVersionRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        var version = await LoadVersionForManageAsync(versionId, actorUserId, ct);
        return await LockVersionAsync(version.TemplateId, version.Id, req, actorUserId, ct);
    }

    public async Task DeleteAsync(
        string id,
        string actorUserId,
        CancellationToken ct = default)
        => await DeleteAsync(id, new DeleteDynamicFlowTemplateRequest(), actorUserId, ct);

    private async Task<DynamicFlowTemplate> LoadTemplateForReadAsync(
        string id,
        string actorUserId,
        CancellationToken ct)
    {
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        return await LoadTemplateForReadAsync(id, actor, actorUserId, ct);
    }

    private async Task<DynamicFlowTemplate> LoadTemplateForReadAsync(
        string id,
        AppUser actor,
        string actorUserId,
        CancellationToken ct)
    {
        id = NormalizeDefinitionId(id, "familyId");
        var fb = Builders<DynamicFlowTemplate>.Filter;
        var filter = fb.Eq(x => x.Id, id) & fb.Eq(x => x.IsDeleted, false);
        if (!DynamicFlowTemplateReadAccess.IsAdministrator(actor))
        {
            var participant = await _ctx.WorkAssignments
                .Find(DynamicFlowTemplateReadAccess.BuildAssignmentParticipantFilter(actorUserId, id))
                .Project(x => x.Id)
                .AnyAsync(ct);
            if (!participant)
                filter &= BuildTemplateOwnerFilter(actorUserId);
        }

        var template = await _ctx.DynamicFlowTemplates
            .Find(filter)
            .FirstOrDefaultAsync(ct);
        if (template is null)
            throw DefinitionAccessForbidden();
        return template;
    }

    private async Task<DynamicFlowTemplate> LoadTemplateForManageAsync(
        string id,
        string actorUserId,
        CancellationToken ct)
    {
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        return await LoadTemplateForManageAsync(id, actor, actorUserId, ct);
    }

    private async Task<DynamicFlowTemplate> LoadTemplateForManageAsync(
        string id,
        AppUser actor,
        string actorUserId,
        CancellationToken ct)
    {
        id = NormalizeDefinitionId(id, "familyId");
        var fb = Builders<DynamicFlowTemplate>.Filter;
        var filter = fb.Eq(x => x.Id, id) & fb.Eq(x => x.IsDeleted, false);
        if (!DynamicFlowTemplateReadAccess.IsAdministrator(actor))
            filter &= BuildTemplateOwnerFilter(actorUserId);

        var template = await _ctx.DynamicFlowTemplates
            .Find(filter)
            .FirstOrDefaultAsync(ct);
        if (template is null)
            throw DefinitionAccessForbidden();
        return template;
    }

    private async Task<DynamicFlowTemplateVersion> LoadVersionForManageAsync(
        string id,
        string actorUserId,
        CancellationToken ct)
    {
        id = NormalizeDefinitionId(id, "versionId");
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        var templateId = await _ctx.DynamicFlowTemplateVersions
            .Find(x => x.Id == id && !x.IsDeleted)
            .Project(x => x.TemplateId)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(templateId))
            throw DefinitionAccessForbidden();

        var template = await LoadTemplateForManageAsync(templateId, actor, actorUserId, ct);
        var version = await _ctx.DynamicFlowTemplateVersions
            .Find(x => x.Id == id && x.TemplateId == template.Id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (version is null)
            throw DefinitionAccessForbidden();
        return version;
    }

    private async Task<List<DynamicFlowTemplateVersion>> LoadVersionsAsync(
        string templateId,
        IReadOnlySet<int>? readableVersionNos,
        CancellationToken ct)
    {
        var fb = Builders<DynamicFlowTemplateVersion>.Filter;
        var filter = fb.Eq(x => x.TemplateId, templateId) &
                     fb.Eq(x => x.IsDeleted, false);
        if (readableVersionNos is not null)
        {
            filter &= fb.Eq(x => x.Status, DynamicFlowTemplateVersionStatuses.Locked) &
                      fb.In(x => x.VersionNo, readableVersionNos);
        }

        return await _ctx.DynamicFlowTemplateVersions
            .Find(filter)
            .Sort(Builders<DynamicFlowTemplateVersion>.Sort
                .Descending(x => x.VersionNo)
                .Descending(x => x.UpdatedAtUtc))
            .ToListAsync(ct);
    }

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

    private async Task<Dictionary<string, HashSet<int>>> LoadParticipantVersionNosAsync(
        string actorUserId,
        CancellationToken ct)
    {
        var filter = DynamicFlowTemplateReadAccess.BuildAssignmentParticipantFilter(actorUserId);
        var rows = await _ctx.WorkAssignments
            .Find(filter)
            .Project(x => new { x.FlowTemplateId, x.FlowTemplateVersionNo })
            .ToListAsync(ct);

        return rows
            .Where(x => !string.IsNullOrWhiteSpace(x.FlowTemplateId) && x.FlowTemplateVersionNo.HasValue)
            .GroupBy(x => x.FlowTemplateId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(x => x.FlowTemplateVersionNo!.Value).ToHashSet(),
                StringComparer.Ordinal);
    }

    private async Task<HashSet<int>?> ResolveReadableVersionNosAsync(
        DynamicFlowTemplate template,
        AppUser actor,
        string actorUserId,
        CancellationToken ct)
    {
        if (DynamicFlowTemplateReadAccess.IsOwner(template, actorUserId))
            return null;

        if (DynamicFlowTemplateReadAccess.IsAdministrator(actor))
            return null;

        var assignmentFilter = DynamicFlowTemplateReadAccess.BuildAssignmentParticipantFilter(
            actorUserId,
            template.Id);
        var readableVersionNos = await _ctx.WorkAssignments
            .Find(assignmentFilter)
            .Project(x => x.FlowTemplateVersionNo)
            .ToListAsync(ct);
        var resolved = readableVersionNos
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToHashSet();

        if (resolved.Count > 0)
            return resolved;

        // Keep existing and missing resources indistinguishable to callers that
        // do not have definition read access. In particular, never echo the
        // probed family or actor identifiers in the denial payload.
        throw DefinitionAccessForbidden();
    }

    private async Task ValidateLockedVersionsIntegrityAsync(
        IReadOnlyCollection<DynamicFlowTemplate> families,
        IReadOnlyCollection<DynamicFlowTemplateVersion> versions,
        CancellationToken ct)
    {
        var lockedVersions = versions
            .Where(version => string.Equals(
                version.Status,
                DynamicFlowTemplateVersionStatuses.Locked,
                StringComparison.Ordinal))
            .ToList();
        if (lockedVersions.Count == 0)
            return;

        var formIds = DynamicFlowLockedSnapshotIntegrity
            .CollectReferencedFormIds(lockedVersions)
            .ToList();
        var forms = formIds.Count == 0
            ? new List<DynamicFormTemplate>()
            : await _ctx.DynamicFormTemplates
                .Find(form =>
                    formIds.Contains(form.Id) &&
                    !form.IsDeleted &&
                    form.IsActive &&
                    form.IsPublished)
                .ToListAsync(ct);
        var formsById = forms.ToDictionary(form => form.Id, StringComparer.Ordinal);
        var familiesById = families.ToDictionary(family => family.Id, StringComparer.Ordinal);

        foreach (var group in lockedVersions.GroupBy(version => version.TemplateId, StringComparer.Ordinal))
        {
            if (!familiesById.TryGetValue(group.Key, out var family))
            {
                throw new AppException(
                    AppErrorCode.DYNAMIC_FLOW_REVISION_CONFLICT,
                    new { reason = DynamicFlowLockedSnapshotIntegrity.FailureReason });
            }
            try
            {
                foreach (var version in group)
                    DynamicFlowContributionPolicyContract.ValidateLockedPolicy(version);
            }
            catch (InvalidOperationException error)
            {
                throw new AppException(
                    AppErrorCode.DYNAMIC_FLOW_REVISION_CONFLICT,
                    new { reason = DynamicFlowLockedSnapshotIntegrity.FailureReason },
                    innerException: error);
            }
            DynamicFlowLockedSnapshotIntegrity.Validate(family, group, formsById);
        }
    }

    private async Task<AppUser> LoadRequiredActorAsync(string actorUserId, CancellationToken ct)
        => await _ctx.Users
            .Find(x => x.Id == actorUserId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.Unauthorized();

    private static FilterDefinition<DynamicFlowTemplate> BuildTemplateOwnerFilter(string actorUserId)
    {
        var fb = Builders<DynamicFlowTemplate>.Filter;
        return fb.Eq(x => x.OwnerUserId, actorUserId) |
               ((fb.Eq(x => x.OwnerUserId, null) | fb.Exists(x => x.OwnerUserId, false)) &
                fb.Eq(x => x.CreatedByUserId, actorUserId));
    }

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
        List<DynamicFlowTemplateVersionDto>? versions,
        bool restrictCurrentToVisibleVersions = false)
    {
        versions ??= new List<DynamicFlowTemplateVersionDto>();
        var visibleCurrentVersion = versions.FirstOrDefault(
            x => string.Equals(x.Id, template.CurrentVersionId, StringComparison.Ordinal));
        if (restrictCurrentToVisibleVersions && visibleCurrentVersion is null)
            visibleCurrentVersion = versions.OrderByDescending(x => x.VersionNo).FirstOrDefault();

        return new DynamicFlowTemplateDto
        {
            Id = template.Id,
            Code = template.Code,
            Name = template.Name,
            Description = template.Description,
            RootDynamicFormTemplateId = template.RootDynamicFormTemplateId,
            DynamicFormTemplateId = template.RootDynamicFormTemplateId,
            FamilyRevision = Math.Max(1, template.FamilyRevision),
            OwnerUserId = template.OwnerUserId ?? template.CreatedByUserId,
            OwnerUnitId = template.OwnerUnitId,
            Lineage = new DynamicFlowDefinitionLineageDto
            {
                OriginFamilyId = template.OriginFamilyId,
                OriginVersionId = template.OriginVersionId
            },
            Status = template.Status,
            CurrentVersionId = restrictCurrentToVisibleVersions ? visibleCurrentVersion?.Id : template.CurrentVersionId,
            CurrentVersionNo = restrictCurrentToVisibleVersions ? visibleCurrentVersion?.VersionNo : template.CurrentVersionNo,
            CurrentVersionHash = restrictCurrentToVisibleVersions ? visibleCurrentVersion?.PayloadHash : template.CurrentVersionHash,
            CurrentVersion = visibleCurrentVersion,
            DraftVersion = versions.FirstOrDefault(x => x.Status == DynamicFlowTemplateVersionStatuses.Draft),
            Versions = versions,
            HasLockedVersion = template.HasLockedVersion || !string.IsNullOrWhiteSpace(template.CurrentVersionId),
            ArchivedAtUtc = template.ArchivedAtUtc,
            ArchivedByUserId = template.ArchivedByUserId,
            IsDeleted = template.IsDeleted,
            CreatedAtUtc = template.CreatedAtUtc,
            UpdatedAtUtc = template.UpdatedAtUtc
        };
    }

    private static void ApplySearchTemplatePermissions(
        DynamicFlowTemplateDto dto,
        DynamicFlowTemplate family,
        AppUser actor,
        string actorUserId,
        IReadOnlySet<int> grantedVersionNos)
    {
        dto.CanRead = true;
        dto.CanManage = DynamicFlowTemplateReadAccess.IsOwner(family, actorUserId) ||
                        DynamicFlowTemplateReadAccess.IsAdministrator(actor);
        dto.ExecuteGrant = dto.CurrentVersionNo.HasValue &&
                           grantedVersionNos.Contains(dto.CurrentVersionNo.Value);
        var eligibilityVersion = dto.CurrentVersion ?? dto.DraftVersion ?? dto.Versions.FirstOrDefault();
        dto.DefinitionLockable = eligibilityVersion?.DefinitionLockable ?? false;
        if (eligibilityVersion is null && family.HasLockedVersion)
            dto.DefinitionLockable = true;
        dto.ExecutionEligibility = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase;
        dto.ExecutionBlockedReason = DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented;
        dto.BlockedUntilPhase = eligibilityVersion?.BlockedUntilPhase;
        dto.CanExecute = false;

        var nestedVersions = dto.Versions
            .Concat(dto.CurrentVersion is null
                ? Array.Empty<DynamicFlowTemplateVersionDto>()
                : new[] { dto.CurrentVersion })
            .Concat(dto.DraftVersion is null
                ? Array.Empty<DynamicFlowTemplateVersionDto>()
                : new[] { dto.DraftVersion })
            .GroupBy(version => version.Id, StringComparer.Ordinal)
            .Select(group => group.First());
        foreach (var version in nestedVersions)
        {
            version.CanRead = true;
            version.CanManage = dto.CanManage;
            version.ExecuteGrant = grantedVersionNos.Contains(version.VersionNo);
            version.ExecutionEligibility = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase;
            version.ExecutionBlockedReason = DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented;
            version.CanExecute = false;
        }
    }

    private static string VersionKey(string templateId, int versionNo)
        => $"{templateId}\n{versionNo.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static DynamicFlowTemplateVersionDto MapVersion(DynamicFlowTemplateVersion version, bool isUsed)
        => new()
        {
            Id = version.Id,
            TemplateId = version.TemplateId,
            RootDynamicFormTemplateId = version.RootDynamicFormTemplateId,
            DynamicFormTemplateId = version.RootDynamicFormTemplateId,
            VersionNo = version.VersionNo,
            Status = version.Status,
            DraftRevision = version.DraftRevision,
            SchemaVersion = version.SchemaVersion,
            AdapterVersion = version.AdapterVersion,
            CatalogVersion = version.CatalogVersion,
            CatalogSemanticHash = version.CatalogSemanticHash,
            Lineage = new DynamicFlowDefinitionLineageDto
            {
                OriginFamilyId = version.OriginFamilyId,
                OriginVersionId = version.OriginVersionId
            },
            Payload = DynamicFlowTemplatePayloadContract.ReadCanonical(
                version.PayloadJson,
                version.RootDynamicFormTemplateId,
                string.Equals(
                    version.MigrationState,
                    DynamicFlowDefinitionMigrationStates.RequiresReview,
                    StringComparison.Ordinal)),
            PayloadJson = version.PayloadJson,
            PayloadHash = version.PayloadHash,
            ContributionPolicy = version.ContributionPolicy,
            ContributionPolicyHash = version.ContributionPolicyHash,
            ContributionWarning = version.ContributionWarning,
            IsUsed = isUsed,
            DefinitionLockable = version.DefinitionLockable,
            ExecutionEligibility = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase,
            ExecutionBlockedReason = DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented,
            BlockedUntilPhase = version.BlockedUntilPhase ?? ResolveBlockedUntilPhase(version.PayloadJson),
            CanExecute = false,
            MigrationState = version.MigrationState,
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
        => NormalizePayloadJson(
            payloadJson,
            requireLockable,
            dynamicFormTemplate?.Id,
            dynamicFormTemplate is null
                ? null
                : new Dictionary<string, DynamicFormTemplate>(StringComparer.Ordinal)
                {
                    [dynamicFormTemplate.Id] = dynamicFormTemplate
                });

    internal static string NormalizePayloadJson(
        string? payloadJson,
        bool requireLockable,
        string? rootDynamicFormTemplateId,
        IReadOnlyDictionary<string, DynamicFormTemplate>? dynamicFormTemplates)
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

        NormalizeFormNodes(root, rootDynamicFormTemplateId, dynamicFormTemplates);
        EnsureObjectProperty(root, "rollbackPolicy");
        EnsureObjectProperty(root, "finalResultPolicy");
        EnsureObjectProperty(root, "statisticProfile");
        NormalizeStatisticProfile(root, requireLockable);

        if (requireLockable)
            ValidateLockablePayload(root);

        DynamicFlowPolicyValidator.ValidatePayload(root, dynamicFormTemplates);

        return root.ToJsonString(JsonOptions);
    }

    internal static void EnsureLockableDynamicFormVersions(
        IEnumerable<DynamicFormTemplate> dynamicFormTemplates)
    {
        ArgumentNullException.ThrowIfNull(dynamicFormTemplates);
        var forms = dynamicFormTemplates
            .GroupBy(form => form.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(form => form.Id, StringComparer.Ordinal)
            .ToList();

        var unpublishedFormIds = forms
            .Where(form => !form.IsPublished)
            .Select(form => form.Id)
            .ToList();
        if (unpublishedFormIds.Count > 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = "dynamicFormTemplateIds",
                    dynamicFormTemplateIds = unpublishedFormIds,
                    reason = "DYNAMIC_FLOW_TEMPLATE_PUBLISHED_FORMS_REQUIRED"
                });
        }

        var missingVersionMetadataIds = forms
            .Where(form => string.IsNullOrWhiteSpace(form.FamilyId) || form.VersionNo <= 0)
            .Select(form => form.Id)
            .ToList();
        if (missingVersionMetadataIds.Count > 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = "formNodes",
                    dynamicFormTemplateIds = missingVersionMetadataIds,
                    reason = "DYNAMIC_FLOW_FORM_VERSION_METADATA_REQUIRED"
                });
        }

        var missingSchemaHashIds = forms
            .Where(form => string.IsNullOrWhiteSpace(form.PublishedSchemaHash))
            .Select(form => form.Id)
            .ToList();
        if (missingSchemaHashIds.Count > 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = "formNodes.dynamicFormSchemaHash",
                    dynamicFormTemplateIds = missingSchemaHashIds,
                    reason = "DYNAMIC_FLOW_PUBLISHED_FORM_SCHEMA_HASH_REQUIRED"
                });
        }

        var missingSchemaSnapshotIds = forms
            .Where(form => string.IsNullOrWhiteSpace(form.PublishedSchemaSnapshotJson))
            .Select(form => form.Id)
            .ToList();
        if (missingSchemaSnapshotIds.Count > 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = "formNodes.dynamicFormSchemaHash",
                    dynamicFormTemplateIds = missingSchemaSnapshotIds,
                    reason = "DYNAMIC_FLOW_PUBLISHED_FORM_SCHEMA_SNAPSHOT_REQUIRED"
                });
        }

        var mismatchedSchemaHashIds = forms
            .Where(form => !string.Equals(
                Sha256(form.PublishedSchemaSnapshotJson!),
                form.PublishedSchemaHash,
                StringComparison.OrdinalIgnoreCase))
            .Select(form => form.Id)
            .ToList();

        if (mismatchedSchemaHashIds.Count > 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = "formNodes.dynamicFormSchemaHash",
                    dynamicFormTemplateIds = mismatchedSchemaHashIds,
                    reason = "DYNAMIC_FLOW_PUBLISHED_FORM_SCHEMA_HASH_MISMATCH"
                });
        }
    }

    private async Task<Dictionary<string, DynamicFormTemplate>> LoadDynamicFormTemplatesForPayloadAsync(
        string? rootDynamicFormTemplateId,
        string? payloadJson,
        string actorUserId,
        CancellationToken ct)
    {
        var ids = CollectDynamicFormTemplateIds(rootDynamicFormTemplateId, payloadJson)
            .Select(x => NormalizeOptionalObjectId(x, "dynamicFormTemplateId"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<string, DynamicFormTemplate>(StringComparer.Ordinal);

        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        var rows = await _ctx.DynamicFormTemplates
            .Find(DynamicFormBindingAccessPolicy.BuildMayBindFilter(
                actor,
                ids,
                requirePublished: false))
            .ToListAsync(ct);

        // Keep existence/ownership/activity inside the Mongo ACL projection,
        // but do not fold publication state into authorization. An authorized
        // owner must reach the stable published-version preflight below,
        // while a missing or hidden Form still receives the same generic 403.
        if (rows.Count != ids.Count)
            throw DynamicFormBindingAccessPolicy.Forbidden();

        var byId = rows.ToDictionary(x => x.Id, StringComparer.Ordinal);
        DynamicFormBindingAccessPolicy.EnsureMayBind(actor, rows);
        // A Flow draft already creates a durable design-time reference. Bind
        // only immutable published Form versions so a concurrent Form draft
        // delete cannot leave the Flow template dangling before lock.
        EnsureLockableDynamicFormVersions(rows);

        return byId;
    }

    private static IEnumerable<string?> CollectDynamicFormTemplateIds(
        string? rootDynamicFormTemplateId,
        string? payloadJson)
    {
        yield return rootDynamicFormTemplateId;

        if (string.IsNullOrWhiteSpace(payloadJson))
            yield break;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payloadJson);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (node is not JsonObject root)
            yield break;

        yield return PickString(root, "rootDynamicFormTemplateId", "dynamicFormTemplateId");

        if (root["formNodes"] is JsonArray formNodes)
        {
            foreach (var item in formNodes)
            {
                if (item is JsonObject formNode)
                    yield return PickString(formNode, "dynamicFormTemplateId", "formTemplateId");
            }
        }

        if (root["steps"] is JsonArray steps)
        {
            foreach (var item in steps)
            {
                if (item is JsonObject step)
                    yield return PickString(step, "dynamicFormTemplateId", "formTemplateId");
            }
        }

        if (root["mappingRules"] is JsonArray mappings)
        {
            foreach (var item in mappings)
            {
                if (item is not JsonObject mapping)
                    continue;
                yield return PickString(mapping, "sourceDynamicFormTemplateId", "sourceFormTemplateId");
                yield return PickString(mapping, "targetDynamicFormTemplateId", "targetFormTemplateId");
                if (mapping["inputs"] is JsonArray inputs)
                {
                    foreach (var input in inputs.OfType<JsonObject>())
                    {
                        if (input["source"] is JsonObject source)
                            yield return PickString(source, "dynamicFormTemplateId", "formTemplateId");
                    }
                }

                if (mapping["target"] is JsonObject target)
                    yield return PickString(target, "dynamicFormTemplateId", "formTemplateId");
            }
        }
    }

    private static void NormalizeFormNodes(
        JsonObject root,
        string? requestedRootDynamicFormTemplateId,
        IReadOnlyDictionary<string, DynamicFormTemplate>? dynamicFormTemplates)
    {
        var rootDynamicFormTemplateId = FirstNonBlank(
            requestedRootDynamicFormTemplateId,
            PickString(root, "rootDynamicFormTemplateId"),
            PickString(root, "dynamicFormTemplateId"));

        if (!string.IsNullOrWhiteSpace(rootDynamicFormTemplateId))
        {
            rootDynamicFormTemplateId = NormalizeOptionalObjectId(rootDynamicFormTemplateId, "rootDynamicFormTemplateId");
            root["rootDynamicFormTemplateId"] = rootDynamicFormTemplateId;
            root.Remove("dynamicFormTemplateId");
        }

        if (root["formNodes"] is not JsonArray formNodes)
        {
            formNodes = new JsonArray();
            root["formNodes"] = formNodes;
        }

        var nodeFormIds = new HashSet<string>(StringComparer.Ordinal);
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < formNodes.Count; index++)
        {
            if (formNodes[index] is not JsonObject formNode)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"formNodes[{index}]", reason = "DYNAMIC_FLOW_FORM_NODE_OBJECT_REQUIRED" });
            }

            var formId = NormalizeOptionalObjectId(
                PickString(formNode, "dynamicFormTemplateId", "formTemplateId"),
                $"formNodes[{index}].dynamicFormTemplateId");
            if (string.IsNullOrWhiteSpace(formId))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new
                    {
                        field = $"formNodes[{index}].dynamicFormTemplateId",
                        reason = "DYNAMIC_FLOW_FORM_NODE_FORM_REQUIRED"
                    });
            }

            formNode["dynamicFormTemplateId"] = formId;
            if (!nodeFormIds.Add(formId))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new
                    {
                        field = $"formNodes[{index}].dynamicFormTemplateId",
                        dynamicFormTemplateId = formId,
                        reason = "DYNAMIC_FLOW_FORM_NODE_FORM_DUPLICATE"
                    });
            }

            var formNodeId = FirstNonBlank(PickString(formNode, "formNodeId", "nodeId", "id"), formId);
            formNode["formNodeId"] = formNodeId;
            if (!nodeIds.Add(formNodeId!))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new
                    {
                        field = $"formNodes[{index}].formNodeId",
                        formNodeId,
                        reason = "DYNAMIC_FLOW_FORM_NODE_ID_DUPLICATE"
                    });
            }

            var role = PickString(formNode, "role")?.ToUpperInvariant();
            formNode["role"] = string.IsNullOrWhiteSpace(role)
                ? string.Equals(formId, rootDynamicFormTemplateId, StringComparison.Ordinal) ? "ROOT" : "CHILD"
                : role;
        }

        if (!string.IsNullOrWhiteSpace(rootDynamicFormTemplateId) &&
            !nodeFormIds.Contains(rootDynamicFormTemplateId))
        {
            var rootNodeId = "root";
            while (nodeIds.Contains(rootNodeId))
                rootNodeId = $"root_{nodeIds.Count + 1}";

            formNodes.Insert(0, new JsonObject
            {
                ["formNodeId"] = rootNodeId,
                ["role"] = "ROOT",
                ["dynamicFormTemplateId"] = rootDynamicFormTemplateId
            });
            nodeFormIds.Add(rootDynamicFormTemplateId);
            nodeIds.Add(rootNodeId);
        }

        if (dynamicFormTemplates is not null)
        {
            for (var index = 0; index < formNodes.Count; index++)
            {
                if (formNodes[index] is not JsonObject formNode)
                    continue;

                var formId = PickString(formNode, "dynamicFormTemplateId");
                if (string.IsNullOrWhiteSpace(formId) ||
                    !dynamicFormTemplates.TryGetValue(formId, out var form))
                {
                    throw AppExceptionFactory.NotFound(
                        AppErrorCode.COMMON_NOT_FOUND,
                        new
                        {
                            field = $"formNodes[{index}].dynamicFormTemplateId",
                            dynamicFormTemplateId = formId,
                            reason = "DYNAMIC_FORM_TEMPLATE_NOT_FOUND"
                        });
                }

                var familyId = FirstNonBlank(form.FamilyId, form.Id)!;
                formNode["dynamicFormFamilyId"] = familyId;
                formNode["dynamicFormVersionNo"] = Math.Max(1, form.VersionNo);
                if (string.IsNullOrWhiteSpace(form.PublishedSchemaHash))
                    formNode.Remove("dynamicFormSchemaHash");
                else
                    formNode["dynamicFormSchemaHash"] = form.PublishedSchemaHash.Trim();
            }
        }

        var formIdByNodeId = ReadFormIdByNodeId(formNodes);
        var onlyFormId = nodeFormIds.Count == 1 ? nodeFormIds.First() : null;
        if (root["steps"] is JsonArray steps)
        {
            for (var index = 0; index < steps.Count; index++)
            {
                if (steps[index] is not JsonObject step)
                    continue;

                var stepFormId = NormalizeOptionalObjectId(
                    PickString(step, "dynamicFormTemplateId", "formTemplateId"),
                    $"steps[{index}].dynamicFormTemplateId");
                var stepNodeId = PickString(step, "formNodeId", "nodeId");
                string? resolvedByNode = null;
                if (!string.IsNullOrWhiteSpace(stepNodeId))
                {
                    if (!formIdByNodeId.TryGetValue(stepNodeId, out resolvedByNode))
                    {
                        throw AppExceptionFactory.BadRequest(
                            AppErrorCode.COMMON_VALIDATION_FAILED,
                            new
                            {
                                field = $"steps[{index}].formNodeId",
                                formNodeId = stepNodeId,
                                reason = "DYNAMIC_FLOW_STEP_FORM_NODE_UNKNOWN"
                            });
                    }

                    if (!string.IsNullOrWhiteSpace(stepFormId) &&
                        !string.Equals(stepFormId, resolvedByNode, StringComparison.Ordinal))
                    {
                        throw AppExceptionFactory.BadRequest(
                            AppErrorCode.COMMON_VALIDATION_FAILED,
                            new
                            {
                                field = $"steps[{index}]",
                                formNodeId = stepNodeId,
                                dynamicFormTemplateId = stepFormId,
                                formNodeDynamicFormTemplateId = resolvedByNode,
                                reason = "DYNAMIC_FLOW_STEP_FORM_NODE_MISMATCH"
                            });
                    }
                }

                stepFormId ??= resolvedByNode;
                if (string.IsNullOrWhiteSpace(stepFormId))
                    stepFormId = onlyFormId;

                if (!string.IsNullOrWhiteSpace(stepFormId))
                {
                    if (!nodeFormIds.Contains(stepFormId))
                    {
                        throw AppExceptionFactory.BadRequest(
                            AppErrorCode.COMMON_VALIDATION_FAILED,
                            new
                            {
                                field = $"steps[{index}].dynamicFormTemplateId",
                                dynamicFormTemplateId = stepFormId,
                                reason = "DYNAMIC_FLOW_STEP_FORM_NODE_REQUIRED"
                            });
                    }

                    step["dynamicFormTemplateId"] = stepFormId;
                }
            }
        }

    }

    private static Dictionary<string, string> ReadFormIdByNodeId(JsonArray formNodes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in formNodes)
        {
            if (item is not JsonObject formNode)
                continue;
            var formNodeId = PickString(formNode, "formNodeId", "nodeId", "id");
            var formId = PickString(formNode, "dynamicFormTemplateId", "formTemplateId");
            if (!string.IsNullOrWhiteSpace(formNodeId) && !string.IsNullOrWhiteSpace(formId))
                result[formNodeId] = formId;
        }

        return result;
    }

    private async Task<DynamicFormTemplate?> LoadDynamicFormTemplateOrNullAsync(
        string? dynamicFormTemplateId,
        string actorUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            return null;

        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        var form = await _ctx.DynamicFormTemplates
            .Find(DynamicFormBindingAccessPolicy.BuildMayBindFilter(
                actor,
                new[] { dynamicFormTemplateId },
                requirePublished: false))
            .FirstOrDefaultAsync(ct)
            ?? throw DynamicFormBindingAccessPolicy.Forbidden();

        DynamicFormBindingAccessPolicy.EnsureMayBind(actor, new[] { form });
        EnsureLockableDynamicFormVersions(new[] { form });
        return form;
    }

    private static void ValidateLockablePayload(JsonObject root)
    {
        var rootDynamicFormTemplateId = PickString(root, "rootDynamicFormTemplateId");
        if (string.IsNullOrWhiteSpace(rootDynamicFormTemplateId))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "rootDynamicFormTemplateId", reason = "DYNAMIC_FLOW_TEMPLATE_ROOT_FORM_REQUIRED" });
        }

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
            var dynamicFormTemplateId = PickString(step, "dynamicFormTemplateId", "formTemplateId");
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

            if (string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = "steps.dynamicFormTemplateId", stepId, stepCode, reason = "DYNAMIC_FLOW_TEMPLATE_STEP_FORM_REQUIRED" });
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

    private static void NormalizeStatisticProfile(JsonObject root, bool requireLockable)
    {
        if (root["statisticProfile"] is not JsonObject profile || profile.Count == 0)
            return;

        var diffMode = PickString(profile, "diffMode");
        if (profile.Count == 1 &&
            (string.IsNullOrWhiteSpace(diffMode) ||
             string.Equals(diffMode, "NONE", StringComparison.OrdinalIgnoreCase)))
        {
            profile.Clear();
            return;
        }

        if (requireLockable)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = "statisticProfile",
                    reason = "DYNAMIC_FLOW_STATISTIC_PROFILE_NOT_EXECUTABLE"
                });
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

    private static string NormalizeDefinitionId(string? value, string field)
        => NormalizeRequiredObjectId(value, field);

    private static AppException DefinitionAccessForbidden()
        => AppExceptionFactory.Forbidden(
            AppErrorCode.AUTH_FORBIDDEN,
            new { reason = "DYNAMIC_FLOW_DEFINITION_ACCESS_FORBIDDEN" });

    private static string NormalizeRequiredObjectId(string? value, string field)
    {
        var normalized = NormalizeOptionalObjectId(value, field);
        if (string.IsNullOrWhiteSpace(normalized))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_ARGUMENT_REQUIRED, new { field });
        return normalized;
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

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

    private static string ResolveBlockedUntilPhase(string payloadJson)
    {
        try
        {
            var root = JsonNode.Parse(payloadJson) as JsonObject;
            var archetypeId = PickString(root ?? new JsonObject(), "archetypeId")?.ToUpperInvariant();
            return archetypeId is "FLOW-T01" or "FLOW-T02" ? "P5" : "P6";
        }
        catch (JsonException)
        {
            return "P6";
        }
    }
}
