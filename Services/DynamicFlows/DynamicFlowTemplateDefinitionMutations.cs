using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Capabilities;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.DynamicFlows;

public sealed partial class DynamicFlowTemplateService
{
    private static class DefinitionCommandKinds
    {
        public const string Create = "CREATE";
        public const string Update = "UPDATE";
        public const string SaveDraft = "SAVE_DRAFT";
        public const string Lock = "LOCK";
        public const string Archive = "ARCHIVE";
        public const string Delete = "DELETE";
        public const string Clone = "CLONE";
        public const string Reopen = "REOPEN";
    }

    public async Task<DynamicFlowTemplateVersionDto> GetVersionAsync(
        string templateId,
        string versionId,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        var template = await LoadTemplateForReadAsync(templateId, actor, actorUserId, ct);
        var readableVersionNos = await ResolveReadableVersionNosAsync(
            template,
            actor,
            actorUserId,
            ct);
        versionId = NormalizeDefinitionId(versionId, "versionId");
        var fb = Builders<DynamicFlowTemplateVersion>.Filter;
        var filter = fb.Eq(x => x.Id, versionId) &
                     fb.Eq(x => x.TemplateId, template.Id) &
                     fb.Eq(x => x.IsDeleted, false);
        if (readableVersionNos is not null)
        {
            filter &= fb.Eq(x => x.Status, DynamicFlowTemplateVersionStatuses.Locked) &
                      fb.In(x => x.VersionNo, readableVersionNos);
        }
        var version = await _ctx.DynamicFlowTemplateVersions
            .Find(filter)
            .FirstOrDefaultAsync(ct);
        if (version is null)
            throw DefinitionAccessForbidden();

        await ValidateLockedVersionsIntegrityAsync(new[] { template }, new[] { version }, ct);
        var dto = MapVersion(version, await IsVersionUsedAsync(template.Id, version.VersionNo, ct));
        await ApplyVersionPermissionsAsync(dto, template, version, actor, actorUserId, ct);
        return dto;
    }

    private async Task<DynamicFlowTemplateDto> CreateDefinitionAsync(
        CreateDynamicFlowTemplateRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        req ??= new CreateDynamicFlowTemplateRequest();
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        if (!DynamicFlowTemplateReadAccess.CanCreateDefinition(actor))
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.DYNAMIC_FLOW_CREATE_FORBIDDEN,
                new { reason = "DYNAMIC_FLOW_CREATE_FORBIDDEN" });
        }
        EnsureNoRequestAuthority(req.AdditionalProperties);

        var commandId = EnsureCommandId(req.CommandId);
        var code = NormalizeCode(req.Code);
        var name = NormalizeRequiredText(req.Name, "name", 240);
        var description = NormalizeOptionalText(req.Description, 2000);
        var rootFormId = NormalizeRequiredObjectId(
            FirstNonBlank(req.RootDynamicFormTemplateId, req.DynamicFormTemplateId),
            "rootDynamicFormTemplateId");
        EnsureUnambiguousPayloadRequest(req.Payload, req.PayloadJson);
        var canonicalPayload = CanonicalRequestPayload(req.Payload);
        var canonicalPayloadJson = CanonicalRequestPayload(req.PayloadJson);
        var requestHash = RequestHash(new
        {
            code,
            name,
            descriptionWasProvided = req.Description is not null,
            description,
            rootDynamicFormTemplateId = rootFormId,
            payloadWasProvided = req.Payload.HasValue,
            payload = canonicalPayload,
            payloadJsonWasProvided = req.PayloadJson is not null,
            payloadJson = canonicalPayloadJson
        });
        var existingReceipt = await LoadMatchingReplayReceiptAsync(
            actorUserId,
            DefinitionCommandKinds.Create,
            commandId,
            requestHash,
            ct);
        if (existingReceipt is not null)
            return await MapFamilyReplayAsync(existingReceipt, actor, actorUserId, ct);

        var prepared = await PreparePayloadAsync(
            req.Payload,
            req.PayloadJson,
            rootFormId,
            actorUserId,
            requireLockable: false,
            trustedServerPayload: false,
            ct);
        var result = await RunCommandAsync(
            DefinitionCommandKinds.Create,
            commandId,
            requestHash,
            actorUserId,
            async (session, transactionCt) =>
            {
                var codeExists = await _ctx.DynamicFlowTemplates
                    .Find(session, x => x.Code == code && !x.IsDeleted)
                    .AnyAsync(transactionCt);
                if (codeExists)
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new { code, reason = "DYNAMIC_FLOW_TEMPLATE_CODE_DUPLICATE" });
                }

                var now = DateTime.UtcNow;
                var template = new DynamicFlowTemplate
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    Code = code,
                    Name = name,
                    Description = description,
                    FamilyRevision = 1,
                    OwnerUserId = actorUserId,
                    OwnerUnitId = actor.UnitId,
                    RootDynamicFormTemplateId = rootFormId,
                    Status = DynamicFlowTemplateStatuses.Draft,
                    HasLockedVersion = false,
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = actorUserId,
                    UpdatedByUserId = actorUserId
                };
                var draft = NewDraftVersion(
                    template.Id,
                    rootFormId,
                    versionNo: 1,
                    prepared,
                    actorUserId,
                    now);

                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.BeforeFamilyWrite);
                await _ctx.DynamicFlowTemplates.InsertOneAsync(session, template, cancellationToken: transactionCt);
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.AfterFamilyWrite);
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.BeforeVersionWrite);
                await _ctx.DynamicFlowTemplateVersions.InsertOneAsync(session, draft, cancellationToken: transactionCt);
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.AfterVersionWrite);

                await InsertReceiptAndAuditAsync(
                    session,
                    DefinitionCommandKinds.Create,
                    commandId,
                    requestHash,
                    actorUserId,
                    template,
                    draft,
                    transactionCt);
                return (template, draft);
            },
            async (receipt, replayCt) =>
            {
                var family = await LoadReceiptFamilyAsync(receipt, replayCt);
                var version = await LoadReceiptVersionAsync(receipt, replayCt);
                return (family, version);
            },
            ct);

        var dto = MapTemplate(
            result.Item1,
            new List<DynamicFlowTemplateVersionDto> { MapVersion(result.Item2, false) });
        ApplyTemplatePermissions(
            dto,
            result.Item1,
            actor,
            actorUserId,
            new HashSet<int>());
        return dto;
    }

    private async Task<DynamicFlowTemplateDto> UpdateDefinitionMetadataAsync(
        string templateId,
        UpdateDynamicFlowTemplateRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        req ??= new UpdateDynamicFlowTemplateRequest();
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        var template = await LoadTemplateForManageAsync(templateId, actor, actorUserId, ct);
        EnsureNoRequestAuthority(req.AdditionalProperties);
        var commandId = EnsureCommandId(req.CommandId);
        EnsurePositiveRevision(req.ExpectedFamilyRevision, "expectedFamilyRevision");

        var codeWasProvided = !string.IsNullOrWhiteSpace(req.Code);
        var requestedCode = codeWasProvided ? NormalizeCode(req.Code) : null;
        var nameWasProvided = !string.IsNullOrWhiteSpace(req.Name);
        var requestedName = nameWasProvided
            ? NormalizeRequiredText(req.Name, "name", 240)
            : null;
        var descriptionWasProvided = req.Description is not null;
        var requestedDescription = descriptionWasProvided
            ? NormalizeOptionalText(req.Description, 2000)
            : null;
        var requestedRoot = FirstNonBlank(req.RootDynamicFormTemplateId, req.DynamicFormTemplateId);
        var rootWasProvided = requestedRoot is not null;
        var requestedRootFormId = rootWasProvided
            ? NormalizeRequiredObjectId(requestedRoot, "rootDynamicFormTemplateId")
            : null;
        var requestHash = RequestHash(new
        {
            familyId = template.Id,
            req.ExpectedFamilyRevision,
            codeWasProvided,
            requestedCode,
            nameWasProvided,
            requestedName,
            descriptionWasProvided,
            requestedDescription,
            rootWasProvided,
            requestedRootFormId
        });
        var existingReceipt = await LoadMatchingReplayReceiptAsync(
            actorUserId,
            DefinitionCommandKinds.Update,
            commandId,
            requestHash,
            ct);
        if (existingReceipt is not null)
            return await MapFamilyReplayAsync(existingReceipt, actor, actorUserId, ct);

        EnsureTemplateNotArchived(template);
        var versionsBefore = await LoadVersionsAsync(template.Id, readableVersionNos: null, ct);
        await ValidateLockedVersionsIntegrityAsync(new[] { template }, versionsBefore, ct);
        var executeGrantVersionNos = await LoadExecuteGrantVersionNosAsync(
            template.Id,
            actorUserId,
            ct);

        var code = requestedCode ?? template.Code;
        var name = requestedName ?? template.Name;
        var description = descriptionWasProvided
            ? requestedDescription
            : template.Description;
        var rootFormId = requestedRootFormId ?? template.RootDynamicFormTemplateId;
        if (!string.Equals(rootFormId, template.RootDynamicFormTemplateId, StringComparison.Ordinal) &&
            template.HasLockedVersion)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { path = "rootDynamicFormTemplateId", reason = "DYNAMIC_FLOW_LOCKED_FAMILY_ROOT_IMMUTABLE" });
        }
        _ = await LoadDynamicFormTemplateOrNullAsync(rootFormId, actorUserId, ct);

        var result = await RunCommandAsync(
            DefinitionCommandKinds.Update,
            commandId,
            requestHash,
            actorUserId,
            async (session, transactionCt) =>
            {
                if (!string.Equals(code, template.Code, StringComparison.Ordinal))
                {
                    var duplicate = await _ctx.DynamicFlowTemplates
                        .Find(session, x => x.Code == code && x.Id != template.Id && !x.IsDeleted)
                        .AnyAsync(transactionCt);
                    if (duplicate)
                    {
                        throw AppExceptionFactory.BadRequest(
                            AppErrorCode.COMMON_VALIDATION_FAILED,
                            new { code, reason = "DYNAMIC_FLOW_TEMPLATE_CODE_DUPLICATE" });
                    }
                }

                var current = await _ctx.DynamicFlowTemplates
                    .Find(session, x =>
                        x.Id == template.Id &&
                        x.FamilyRevision == req.ExpectedFamilyRevision &&
                        !x.IsDeleted &&
                        x.Status != DynamicFlowTemplateStatuses.Archived)
                    .FirstOrDefaultAsync(transactionCt)
                    ?? throw RevisionConflict(template.Id, null, "DYNAMIC_FLOW_FAMILY_REVISION_STALE");
                if (!string.Equals(rootFormId, current.RootDynamicFormTemplateId, StringComparison.Ordinal) &&
                    current.HasLockedVersion)
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new { path = "rootDynamicFormTemplateId", reason = "DYNAMIC_FLOW_LOCKED_FAMILY_ROOT_IMMUTABLE" });
                }

                current.Code = code;
                current.Name = name;
                current.Description = description;
                current.RootDynamicFormTemplateId = rootFormId;
                current.FamilyRevision++;
                current.UpdatedAtUtc = DateTime.UtcNow;
                current.UpdatedByUserId = actorUserId;

                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.BeforeFamilyWrite);
                var replace = await _ctx.DynamicFlowTemplates.ReplaceOneAsync(
                    session,
                    x => x.Id == current.Id &&
                         x.FamilyRevision == req.ExpectedFamilyRevision &&
                         !x.IsDeleted &&
                         x.Status != DynamicFlowTemplateStatuses.Archived,
                    current,
                    cancellationToken: transactionCt);
                if (replace.MatchedCount != 1)
                    throw RevisionConflict(template.Id, null, "DYNAMIC_FLOW_FAMILY_REVISION_STALE");
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.AfterFamilyWrite);

                await InsertReceiptAndAuditAsync(
                    session,
                    DefinitionCommandKinds.Update,
                    commandId,
                    requestHash,
                    actorUserId,
                    current,
                    null,
                    transactionCt);
                return current;
            },
            LoadReceiptFamilyAsync,
            ct);

        var dto = MapTemplate(
            result,
            new List<DynamicFlowTemplateVersionDto>());
        ApplyTemplatePermissions(
            dto,
            result,
            actor,
            actorUserId,
            executeGrantVersionNos);
        return dto;
    }

    public async Task<DynamicFlowTemplateVersionDto> SaveDraftVersionAsync(
        string templateId,
        string versionId,
        SaveDynamicFlowTemplateVersionDraftRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new SaveDynamicFlowTemplateVersionDraftRequest();
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        var template = await LoadTemplateForManageAsync(templateId, actor, actorUserId, ct);
        EnsureNoRequestAuthority(req.AdditionalProperties);
        versionId = NormalizeDefinitionId(versionId, "versionId");
        var commandId = EnsureCommandId(req.CommandId);
        EnsurePositiveRevision(req.ExpectedDraftRevision, "expectedDraftRevision");
        var expectedPayloadHash = NormalizeExpectedHash(req.ExpectedPayloadHash, "expectedPayloadHash");
        EnsureUnambiguousPayloadRequest(req.Payload, req.PayloadJson);
        var canonicalPayload = CanonicalRequestPayload(req.Payload);
        var canonicalPayloadJson = CanonicalRequestPayload(req.PayloadJson);
        var requestHash = RequestHash(new
        {
            familyId = template.Id,
            versionId,
            req.ExpectedDraftRevision,
            expectedPayloadHash,
            payloadWasProvided = req.Payload.HasValue,
            payload = canonicalPayload,
            payloadJsonWasProvided = req.PayloadJson is not null,
            payloadJson = canonicalPayloadJson
        });
        var existingReceipt = await LoadMatchingReplayReceiptAsync(
            actorUserId,
            DefinitionCommandKinds.SaveDraft,
            commandId,
            requestHash,
            ct);
        if (existingReceipt is not null)
            return await MapVersionReplayAsync(existingReceipt, actor, actorUserId, ct);

        EnsureTemplateNotArchived(template);
        var version = await _ctx.DynamicFlowTemplateVersions
            .Find(x => x.Id == versionId && x.TemplateId == template.Id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw DefinitionAccessForbidden();
        var isUsed = await IsVersionUsedAsync(template.Id, version.VersionNo, ct);
        var executeGrantVersionNos = await LoadExecuteGrantVersionNosAsync(
            template.Id,
            actorUserId,
            ct);
        var rootFormId = NormalizeRequiredObjectId(template.RootDynamicFormTemplateId, "rootDynamicFormTemplateId");
        var prepared = await PreparePayloadAsync(
            req.Payload,
            req.PayloadJson,
            rootFormId,
            actorUserId,
            requireLockable: false,
            trustedServerPayload: false,
            ct);

        var result = await RunCommandAsync(
            DefinitionCommandKinds.SaveDraft,
            commandId,
            requestHash,
            actorUserId,
            async (session, transactionCt) =>
            {
                var currentFamily = await _ctx.DynamicFlowTemplates
                    .Find(session, x =>
                        x.Id == template.Id &&
                        x.FamilyRevision == template.FamilyRevision &&
                        !x.IsDeleted &&
                        x.Status != DynamicFlowTemplateStatuses.Archived)
                    .FirstOrDefaultAsync(transactionCt)
                    ?? throw RevisionConflict(template.Id, version.Id, "DYNAMIC_FLOW_FAMILY_CHANGED_DURING_SAVE");
                var current = await _ctx.DynamicFlowTemplateVersions
                    .Find(session, x =>
                        x.Id == version.Id &&
                        x.TemplateId == template.Id &&
                        x.Status == DynamicFlowTemplateVersionStatuses.Draft &&
                        x.DraftRevision == req.ExpectedDraftRevision &&
                        x.PayloadHash == expectedPayloadHash &&
                        !x.IsDeleted)
                    .FirstOrDefaultAsync(transactionCt)
                    ?? throw RevisionConflict(template.Id, version.Id, "DYNAMIC_FLOW_DRAFT_REVISION_STALE");

                var now = DateTime.UtcNow;
                current.DraftRevision++;
                current.RootDynamicFormTemplateId = rootFormId;
                ApplyPreparedPayload(current, prepared, definitionLockable: false);
                current.UpdatedAtUtc = now;
                current.UpdatedByUserId = actorUserId;

                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.BeforeVersionWrite);
                var replace = await _ctx.DynamicFlowTemplateVersions.ReplaceOneAsync(
                    session,
                    x => x.Id == current.Id &&
                         x.TemplateId == template.Id &&
                         x.Status == DynamicFlowTemplateVersionStatuses.Draft &&
                         x.DraftRevision == req.ExpectedDraftRevision &&
                         x.PayloadHash == expectedPayloadHash &&
                         !x.IsDeleted,
                    current,
                    cancellationToken: transactionCt);
                if (replace.MatchedCount != 1)
                    throw RevisionConflict(template.Id, version.Id, "DYNAMIC_FLOW_DRAFT_REVISION_STALE");
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.AfterVersionWrite);

                currentFamily.UpdatedAtUtc = now;
                currentFamily.UpdatedByUserId = actorUserId;
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.BeforeFamilyWrite);
                var familyTouch = await _ctx.DynamicFlowTemplates.ReplaceOneAsync(
                    session,
                    x => x.Id == currentFamily.Id &&
                         x.FamilyRevision == template.FamilyRevision &&
                         !x.IsDeleted &&
                         x.Status != DynamicFlowTemplateStatuses.Archived,
                    currentFamily,
                    cancellationToken: transactionCt);
                if (familyTouch.MatchedCount != 1)
                    throw RevisionConflict(template.Id, version.Id, "DYNAMIC_FLOW_FAMILY_CHANGED_DURING_SAVE");
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.AfterFamilyWrite);

                await InsertReceiptAndAuditAsync(
                    session,
                    DefinitionCommandKinds.SaveDraft,
                    commandId,
                    requestHash,
                    actorUserId,
                    currentFamily,
                    current,
                    transactionCt);
                return current;
            },
            LoadReceiptVersionAsync,
            ct);

        var dto = MapVersion(result, isUsed);
        ApplyVersionPermissions(
            dto,
            template,
            result,
            actor,
            actorUserId,
            executeGrantVersionNos);
        return dto;
    }

    public async Task<DynamicFlowTemplateVersionDto> LockVersionAsync(
        string templateId,
        string versionId,
        LockDynamicFlowTemplateVersionRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new LockDynamicFlowTemplateVersionRequest();
        if (req.ContributionPolicy is not null ||
            req.AcknowledgeContributionWarning.HasValue)
        {
            StatConfigCatalogActivation.EnsureMutationEnabled();
        }
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        var template = await LoadTemplateForManageAsync(templateId, actor, actorUserId, ct);
        EnsureNoRequestAuthority(req.AdditionalProperties);
        versionId = NormalizeDefinitionId(versionId, "versionId");
        var commandId = EnsureCommandId(req.CommandId);
        EnsurePositiveRevision(req.ExpectedFamilyRevision, "expectedFamilyRevision");
        EnsurePositiveRevision(req.ExpectedDraftRevision, "expectedDraftRevision");
        var expectedPayloadHash = NormalizeExpectedHash(req.ExpectedPayloadHash, "expectedPayloadHash");
        var contributionSelection = DynamicFlowContributionPolicyContract.ResolveLockSelection(
            req.ContributionPolicy,
            req.AcknowledgeContributionWarning);
        var requestHash = BuildLockRequestHash(
            template.Id,
            versionId,
            expectedPayloadHash,
            req,
            contributionSelection);
        var existingReceipt = await LoadMatchingReplayReceiptAsync(
            actorUserId,
            DefinitionCommandKinds.Lock,
            commandId,
            requestHash,
            ct);
        if (existingReceipt is not null)
            return await MapVersionReplayAsync(existingReceipt, actor, actorUserId, ct);

        EnsureTemplateNotArchived(template);
        var version = await _ctx.DynamicFlowTemplateVersions
            .Find(x => x.Id == versionId && x.TemplateId == template.Id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw DefinitionAccessForbidden();
        var isUsed = await IsVersionUsedAsync(template.Id, version.VersionNo, ct);
        var executeGrantVersionNos = await LoadExecuteGrantVersionNosAsync(
            template.Id,
            actorUserId,
            ct);
        var rootFormId = NormalizeRequiredObjectId(
            FirstNonBlank(version.RootDynamicFormTemplateId, template.RootDynamicFormTemplateId),
            "rootDynamicFormTemplateId");
        var prepared = await PreparePayloadAsync(
            null,
            version.PayloadJson,
            rootFormId,
            actorUserId,
            requireLockable: true,
            trustedServerPayload: true,
            ct);
        if (!string.Equals(prepared.PayloadHash, expectedPayloadHash, StringComparison.Ordinal) ||
            !string.Equals(prepared.PayloadHash, version.PayloadHash, StringComparison.Ordinal))
        {
            throw RevisionConflict(template.Id, version.Id, "DYNAMIC_FLOW_STORED_PAYLOAD_HASH_MISMATCH");
        }
        if (contributionSelection.Policy == DynamicFlowContributionPolicyContract.Include)
        {
            DynamicFlowTemplateVersion? origin = null;
            if (!string.IsNullOrWhiteSpace(version.OriginVersionId))
            {
                origin = await _ctx.DynamicFlowTemplateVersions
                    .Find(x =>
                        x.Id == version.OriginVersionId &&
                        x.TemplateId == template.Id &&
                        x.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                        !x.IsDeleted)
                    .FirstOrDefaultAsync(ct);
            }
            if (origin is not null)
                await ValidateLockedVersionsIntegrityAsync(new[] { template }, new[] { origin }, ct);
            DynamicFlowContributionPolicyContract.EnsureIncludeOrigin(version, origin);
        }

        var result = await RunCommandAsync(
            DefinitionCommandKinds.Lock,
            commandId,
            requestHash,
            actorUserId,
            async (session, transactionCt) =>
            {
                var currentFamily = await _ctx.DynamicFlowTemplates
                    .Find(session, x =>
                        x.Id == template.Id &&
                        x.FamilyRevision == req.ExpectedFamilyRevision &&
                        !x.IsDeleted &&
                        x.Status != DynamicFlowTemplateStatuses.Archived)
                    .FirstOrDefaultAsync(transactionCt)
                    ?? throw RevisionConflict(template.Id, version.Id, "DYNAMIC_FLOW_FAMILY_REVISION_STALE");
                var current = await _ctx.DynamicFlowTemplateVersions
                    .Find(session, x =>
                        x.Id == version.Id &&
                        x.TemplateId == template.Id &&
                        x.Status == DynamicFlowTemplateVersionStatuses.Draft &&
                        x.DraftRevision == req.ExpectedDraftRevision &&
                        x.PayloadHash == expectedPayloadHash &&
                        !x.IsDeleted)
                    .FirstOrDefaultAsync(transactionCt)
                    ?? throw RevisionConflict(template.Id, version.Id, "DYNAMIC_FLOW_DRAFT_REVISION_STALE");

                if (contributionSelection.Policy == DynamicFlowContributionPolicyContract.Include)
                {
                    DynamicFlowTemplateVersion? currentOrigin = null;
                    if (!string.IsNullOrWhiteSpace(current.OriginVersionId))
                    {
                        currentOrigin = await _ctx.DynamicFlowTemplateVersions
                            .Find(session, x =>
                                x.Id == current.OriginVersionId &&
                                x.TemplateId == current.TemplateId &&
                                x.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                                !x.IsDeleted)
                            .FirstOrDefaultAsync(transactionCt);
                    }
                    DynamicFlowContributionPolicyContract.EnsureIncludeOrigin(current, currentOrigin);
                }

                var now = DateTime.UtcNow;
                current.RootDynamicFormTemplateId = rootFormId;
                ApplyPreparedPayload(current, prepared, definitionLockable: true);
                DynamicFlowContributionPolicyContract.ApplyLockedPolicy(current, contributionSelection);
                current.Status = DynamicFlowTemplateVersionStatuses.Locked;
                current.LockedAtUtc = now;
                current.LockedByUserId = actorUserId;
                current.UpdatedAtUtc = now;
                current.UpdatedByUserId = actorUserId;

                currentFamily.Status = DynamicFlowTemplateStatuses.Active;
                currentFamily.CurrentVersionId = current.Id;
                currentFamily.CurrentVersionNo = current.VersionNo;
                currentFamily.CurrentVersionHash = current.PayloadHash;
                currentFamily.HasLockedVersion = true;
                currentFamily.FamilyRevision++;
                currentFamily.UpdatedAtUtc = now;
                currentFamily.UpdatedByUserId = actorUserId;

                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.BeforeVersionWrite);
                var versionReplace = await _ctx.DynamicFlowTemplateVersions.ReplaceOneAsync(
                    session,
                    x => x.Id == current.Id &&
                         x.TemplateId == template.Id &&
                         x.Status == DynamicFlowTemplateVersionStatuses.Draft &&
                         x.DraftRevision == req.ExpectedDraftRevision &&
                         x.PayloadHash == expectedPayloadHash &&
                         !x.IsDeleted,
                    current,
                    cancellationToken: transactionCt);
                if (versionReplace.MatchedCount != 1)
                    throw RevisionConflict(template.Id, version.Id, "DYNAMIC_FLOW_DRAFT_REVISION_STALE");
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.AfterVersionWrite);

                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.BeforeFamilyWrite);
                var familyReplace = await _ctx.DynamicFlowTemplates.ReplaceOneAsync(
                    session,
                    x => x.Id == currentFamily.Id &&
                         x.FamilyRevision == req.ExpectedFamilyRevision &&
                         !x.IsDeleted &&
                         x.Status != DynamicFlowTemplateStatuses.Archived,
                    currentFamily,
                    cancellationToken: transactionCt);
                if (familyReplace.MatchedCount != 1)
                    throw RevisionConflict(template.Id, version.Id, "DYNAMIC_FLOW_FAMILY_REVISION_STALE");
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.AfterFamilyWrite);

                await InsertReceiptAndAuditAsync(
                    session,
                    DefinitionCommandKinds.Lock,
                    commandId,
                    requestHash,
                    actorUserId,
                    currentFamily,
                    current,
                    transactionCt);
                return current;
            },
            LoadReceiptVersionAsync,
            ct);

        var dto = MapVersion(result, isUsed);
        ApplyVersionPermissions(
            dto,
            template,
            result,
            actor,
            actorUserId,
            executeGrantVersionNos);
        return dto;
    }

    public async Task<DynamicFlowTemplateDto> ArchiveAsync(
        string templateId,
        ArchiveDynamicFlowTemplateRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new ArchiveDynamicFlowTemplateRequest();
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        var template = await LoadTemplateForManageAsync(templateId, actor, actorUserId, ct);
        EnsureNoRequestAuthority(req.AdditionalProperties);
        var commandId = EnsureCommandId(req.CommandId);
        EnsurePositiveRevision(req.ExpectedFamilyRevision, "expectedFamilyRevision");
        var requestHash = RequestHash(new
        {
            familyId = template.Id,
            req.ExpectedFamilyRevision
        });
        var existingReceipt = await LoadMatchingReplayReceiptAsync(
            actorUserId,
            DefinitionCommandKinds.Archive,
            commandId,
            requestHash,
            ct);
        if (existingReceipt is not null)
            return await MapFamilyReplayAsync(existingReceipt, actor, actorUserId, ct);

        var versionsBefore = await LoadVersionsAsync(template.Id, readableVersionNos: null, ct);
        await ValidateLockedVersionsIntegrityAsync(new[] { template }, versionsBefore, ct);
        var executeGrantVersionNos = await LoadExecuteGrantVersionNosAsync(
            template.Id,
            actorUserId,
            ct);

        var result = await RunCommandAsync(
            DefinitionCommandKinds.Archive,
            commandId,
            requestHash,
            actorUserId,
            async (session, transactionCt) =>
            {
                var current = await _ctx.DynamicFlowTemplates
                    .Find(session, x =>
                        x.Id == template.Id &&
                        x.FamilyRevision == req.ExpectedFamilyRevision &&
                        !x.IsDeleted &&
                        x.Status != DynamicFlowTemplateStatuses.Archived)
                    .FirstOrDefaultAsync(transactionCt)
                    ?? throw RevisionConflict(template.Id, null, "DYNAMIC_FLOW_FAMILY_REVISION_STALE");
                var now = DateTime.UtcNow;
                current.Status = DynamicFlowTemplateStatuses.Archived;
                current.ArchivedAtUtc = now;
                current.ArchivedByUserId = actorUserId;
                current.FamilyRevision++;
                current.UpdatedAtUtc = now;
                current.UpdatedByUserId = actorUserId;

                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.BeforeFamilyWrite);
                var replace = await _ctx.DynamicFlowTemplates.ReplaceOneAsync(
                    session,
                    x => x.Id == current.Id &&
                         x.FamilyRevision == req.ExpectedFamilyRevision &&
                         !x.IsDeleted &&
                         x.Status != DynamicFlowTemplateStatuses.Archived,
                    current,
                    cancellationToken: transactionCt);
                if (replace.MatchedCount != 1)
                    throw RevisionConflict(template.Id, null, "DYNAMIC_FLOW_FAMILY_REVISION_STALE");
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.AfterFamilyWrite);

                await InsertReceiptAndAuditAsync(
                    session,
                    DefinitionCommandKinds.Archive,
                    commandId,
                    requestHash,
                    actorUserId,
                    current,
                    null,
                    transactionCt);
                return current;
            },
            LoadReceiptFamilyAsync,
            ct);

        var dto = MapTemplate(
            result,
            new List<DynamicFlowTemplateVersionDto>());
        ApplyTemplatePermissions(
            dto,
            result,
            actor,
            actorUserId,
            executeGrantVersionNos);
        return dto;
    }

    public async Task DeleteAsync(
        string templateId,
        DeleteDynamicFlowTemplateRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new DeleteDynamicFlowTemplateRequest();
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        EnsureNoRequestAuthority(req.AdditionalProperties);
        templateId = NormalizeDefinitionId(templateId, "familyId");
        var commandId = EnsureCommandId(req.CommandId);
        EnsurePositiveRevision(req.ExpectedFamilyRevision, "expectedFamilyRevision");
        var requestHash = RequestHash(new { familyId = templateId, req.ExpectedFamilyRevision });

        var replay = await LoadMatchingReplayReceiptAsync(
            actorUserId,
            DefinitionCommandKinds.Delete,
            commandId,
            requestHash,
            ct);
        if (replay is not null)
            return;

        var template = await LoadTemplateForManageAsync(templateId, actor, actorUserId, ct);
        await RunCommandAsync(
            DefinitionCommandKinds.Delete,
            commandId,
            requestHash,
            actorUserId,
            async (session, transactionCt) =>
            {
                var current = await _ctx.DynamicFlowTemplates
                    .Find(session, x =>
                        x.Id == template.Id &&
                        x.FamilyRevision == req.ExpectedFamilyRevision &&
                        !x.IsDeleted)
                    .FirstOrDefaultAsync(transactionCt)
                    ?? throw RevisionConflict(template.Id, null, "DYNAMIC_FLOW_FAMILY_REVISION_STALE");
                var versions = await _ctx.DynamicFlowTemplateVersions
                    .Find(session, x => x.TemplateId == template.Id && !x.IsDeleted)
                    .ToListAsync(transactionCt);
                var referenced = await _ctx.WorkAssignments
                    .Find(session, x => x.FlowTemplateId == template.Id && !x.IsDeleted)
                    .AnyAsync(transactionCt);
                if (current.HasLockedVersion ||
                    current.CurrentVersionId is not null ||
                    versions.Any(x => x.Status != DynamicFlowTemplateVersionStatuses.Draft) ||
                    referenced)
                {
                    throw AppExceptionFactory.Create(
                        AppErrorCode.DYNAMIC_FLOW_DELETE_NOT_ALLOWED,
                        new { reason = "DYNAMIC_FLOW_DELETE_NOT_ALLOWED" });
                }

                var now = DateTime.UtcNow;
                current.FamilyRevision++;
                current.IsDeleted = true;
                current.DeletedAtUtc = now;
                current.DeletedByUserId = actorUserId;
                current.UpdatedAtUtc = now;
                current.UpdatedByUserId = actorUserId;
                foreach (var item in versions)
                {
                    item.IsDeleted = true;
                    item.DeletedAtUtc = now;
                    item.DeletedByUserId = actorUserId;
                    item.UpdatedAtUtc = now;
                    item.UpdatedByUserId = actorUserId;
                }

                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.BeforeFamilyWrite);
                var familyReplace = await _ctx.DynamicFlowTemplates.ReplaceOneAsync(
                    session,
                    x => x.Id == current.Id &&
                         x.FamilyRevision == req.ExpectedFamilyRevision &&
                         !x.IsDeleted,
                    current,
                    cancellationToken: transactionCt);
                if (familyReplace.MatchedCount != 1)
                    throw RevisionConflict(template.Id, null, "DYNAMIC_FLOW_FAMILY_REVISION_STALE");
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.AfterFamilyWrite);

                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.BeforeVersionWrite);
                foreach (var item in versions)
                {
                    var versionReplace = await _ctx.DynamicFlowTemplateVersions.ReplaceOneAsync(
                        session,
                        x => x.Id == item.Id &&
                             x.TemplateId == template.Id &&
                             x.Status == DynamicFlowTemplateVersionStatuses.Draft &&
                             !x.IsDeleted,
                        item,
                        cancellationToken: transactionCt);
                    if (versionReplace.MatchedCount != 1)
                        throw RevisionConflict(template.Id, item.Id, "DYNAMIC_FLOW_VERSION_CHANGED_DURING_DELETE");
                }
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.AfterVersionWrite);

                await InsertReceiptAndAuditAsync(
                    session,
                    DefinitionCommandKinds.Delete,
                    commandId,
                    requestHash,
                    actorUserId,
                    current,
                    null,
                    transactionCt);
                return true;
            },
            (_, _) => Task.FromResult(true),
            ct);
    }

    public async Task<DynamicFlowTemplateDto> CloneAsync(
        string templateId,
        CloneDynamicFlowTemplateRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new CloneDynamicFlowTemplateRequest();
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        var sourceFamily = await LoadTemplateForManageAsync(templateId, actor, actorUserId, ct);
        if (!DynamicFlowTemplateReadAccess.CanCreateDefinition(actor))
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.DYNAMIC_FLOW_CREATE_FORBIDDEN,
                new { reason = "DYNAMIC_FLOW_CREATE_FORBIDDEN" });
        }
        EnsureNoRequestAuthority(req.AdditionalProperties);

        var commandId = EnsureCommandId(req.CommandId);
        EnsurePositiveRevision(req.ExpectedFamilyRevision, "expectedFamilyRevision");
        var sourceVersionId = NormalizeDefinitionId(req.SourceVersionId, "sourceVersionId");
        EnsurePositiveRevision(req.SourceDraftRevision, "sourceDraftRevision");
        var sourcePayloadHash = NormalizeExpectedHash(req.SourcePayloadHash, "sourcePayloadHash");
        var code = NormalizeCode(req.Code);
        var requestedName = string.IsNullOrWhiteSpace(req.Name)
            ? null
            : NormalizeRequiredText(req.Name, "name", 240);
        var descriptionWasProvided = req.Description is not null;
        var requestedDescription = descriptionWasProvided
            ? NormalizeOptionalText(req.Description, 2000)
            : null;
        var requestHash = RequestHash(new
        {
            sourceFamilyId = sourceFamily.Id,
            req.ExpectedFamilyRevision,
            sourceVersionId,
            req.SourceDraftRevision,
            sourcePayloadHash,
            code,
            requestedName,
            descriptionWasProvided,
            requestedDescription
        });
        var existingReceipt = await LoadMatchingReplayReceiptAsync(
            actorUserId,
            DefinitionCommandKinds.Clone,
            commandId,
            requestHash,
            ct);
        if (existingReceipt is not null)
            return await MapFamilyReplayAsync(existingReceipt, actor, actorUserId, ct);

        var result = await RunCommandAsync(
            DefinitionCommandKinds.Clone,
            commandId,
            requestHash,
            actorUserId,
            async (session, transactionCt) =>
            {
                var currentSource = await _ctx.DynamicFlowTemplates
                    .Find(session, x =>
                        x.Id == sourceFamily.Id &&
                        x.FamilyRevision == req.ExpectedFamilyRevision &&
                        !x.IsDeleted)
                    .FirstOrDefaultAsync(transactionCt)
                    ?? throw RevisionConflict(sourceFamily.Id, sourceVersionId, "DYNAMIC_FLOW_CLONE_SOURCE_STALE");
                var exactSourceVersion = await _ctx.DynamicFlowTemplateVersions
                    .Find(session, x =>
                        x.Id == sourceVersionId &&
                        x.TemplateId == sourceFamily.Id &&
                        x.DraftRevision == req.SourceDraftRevision &&
                        x.PayloadHash == sourcePayloadHash &&
                        (x.Status == DynamicFlowTemplateVersionStatuses.Draft ||
                         x.Status == DynamicFlowTemplateVersionStatuses.Locked) &&
                        !x.IsDeleted)
                    .FirstOrDefaultAsync(transactionCt)
                    ?? throw RevisionConflict(sourceFamily.Id, sourceVersionId, "DYNAMIC_FLOW_CLONE_SOURCE_STALE");
                if (string.Equals(
                        exactSourceVersion.Status,
                        DynamicFlowTemplateVersionStatuses.Locked,
                        StringComparison.Ordinal))
                {
                    await ValidateLockedVersionsIntegrityAsync(
                        new[] { currentSource },
                        new[] { exactSourceVersion },
                        transactionCt);
                }
                var rootFormId = NormalizeRequiredObjectId(
                    FirstNonBlank(
                        exactSourceVersion.RootDynamicFormTemplateId,
                        currentSource.RootDynamicFormTemplateId),
                    "rootDynamicFormTemplateId");
                var prepared = await PreparePayloadAsync(
                    null,
                    exactSourceVersion.PayloadJson,
                    rootFormId,
                    actorUserId,
                    requireLockable: false,
                    trustedServerPayload: true,
                    transactionCt);
                if (!string.Equals(prepared.PayloadHash, sourcePayloadHash, StringComparison.Ordinal) ||
                    !string.Equals(
                        exactSourceVersion.MigrationState,
                        DynamicFlowDefinitionMigrationStates.Canonical,
                        StringComparison.Ordinal))
                {
                    throw RevisionConflict(
                        sourceFamily.Id,
                        sourceVersionId,
                        "DYNAMIC_FLOW_CLONE_SOURCE_INTEGRITY_UNPROVEN");
                }
                var duplicate = await _ctx.DynamicFlowTemplates
                    .Find(session, x => x.Code == code && !x.IsDeleted)
                    .AnyAsync(transactionCt);
                if (duplicate)
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new { code, reason = "DYNAMIC_FLOW_TEMPLATE_CODE_DUPLICATE" });
                }

                var name = requestedName ?? $"{currentSource.Name} (copy)";
                var description = descriptionWasProvided
                    ? requestedDescription
                    : currentSource.Description;
                var now = DateTime.UtcNow;
                var cloneFamily = new DynamicFlowTemplate
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    Code = code,
                    Name = name,
                    Description = description,
                    FamilyRevision = 1,
                    OwnerUserId = actorUserId,
                    OwnerUnitId = actor.UnitId,
                    OriginFamilyId = currentSource.Id,
                    OriginVersionId = exactSourceVersion.Id,
                    RootDynamicFormTemplateId = rootFormId,
                    Status = DynamicFlowTemplateStatuses.Draft,
                    HasLockedVersion = false,
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = actorUserId,
                    UpdatedByUserId = actorUserId
                };
                var cloneVersion = NewDraftVersion(
                    cloneFamily.Id,
                    rootFormId,
                    versionNo: 1,
                    prepared,
                    actorUserId,
                    now);
                cloneVersion.OriginFamilyId = currentSource.Id;
                cloneVersion.OriginVersionId = exactSourceVersion.Id;

                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.BeforeFamilyWrite);
                await _ctx.DynamicFlowTemplates.InsertOneAsync(session, cloneFamily, cancellationToken: transactionCt);
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.AfterFamilyWrite);
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.BeforeVersionWrite);
                await _ctx.DynamicFlowTemplateVersions.InsertOneAsync(session, cloneVersion, cancellationToken: transactionCt);
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.AfterVersionWrite);

                await InsertReceiptAndAuditAsync(
                    session,
                    DefinitionCommandKinds.Clone,
                    commandId,
                    requestHash,
                    actorUserId,
                    cloneFamily,
                    cloneVersion,
                    transactionCt);
                return (family: cloneFamily, version: cloneVersion);
            },
            async (receipt, replayCt) =>
            {
                var family = await LoadReceiptFamilyAsync(receipt, replayCt);
                var version = await LoadReceiptVersionAsync(receipt, replayCt);
                return (family, version);
            },
            ct);

        var dto = MapTemplate(
            result.family,
            new List<DynamicFlowTemplateVersionDto> { MapVersion(result.version, false) });
        ApplyTemplatePermissions(
            dto,
            result.family,
            actor,
            actorUserId,
            new HashSet<int>());
        return dto;
    }

    public async Task<DynamicFlowTemplateVersionDto> ReopenVersionAsync(
        string templateId,
        string versionId,
        ReopenDynamicFlowTemplateVersionRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new ReopenDynamicFlowTemplateVersionRequest();
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        var template = await LoadTemplateForManageAsync(templateId, actor, actorUserId, ct);
        EnsureNoRequestAuthority(req.AdditionalProperties);
        versionId = NormalizeDefinitionId(versionId, "versionId");
        var commandId = EnsureCommandId(req.CommandId);
        EnsurePositiveRevision(req.ExpectedFamilyRevision, "expectedFamilyRevision");
        var requestHash = RequestHash(new
        {
            familyId = template.Id,
            sourceVersionId = versionId,
            req.ExpectedFamilyRevision
        });
        var existingReceipt = await LoadMatchingReplayReceiptAsync(
            actorUserId,
            DefinitionCommandKinds.Reopen,
            commandId,
            requestHash,
            ct);
        if (existingReceipt is not null)
            return await MapVersionReplayAsync(existingReceipt, actor, actorUserId, ct);

        EnsureTemplateNotArchived(template);
        var source = await _ctx.DynamicFlowTemplateVersions
            .Find(x =>
                x.Id == versionId &&
                x.TemplateId == template.Id &&
                x.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw DefinitionAccessForbidden();
        await ValidateLockedVersionsIntegrityAsync(new[] { template }, new[] { source }, ct);
        var executeGrantVersionNos = await LoadExecuteGrantVersionNosAsync(
            template.Id,
            actorUserId,
            ct);
        var rootFormId = NormalizeRequiredObjectId(
            FirstNonBlank(source.RootDynamicFormTemplateId, template.RootDynamicFormTemplateId),
            "rootDynamicFormTemplateId");
        var prepared = await PreparePayloadAsync(
            null,
            source.PayloadJson,
            rootFormId,
            actorUserId,
            requireLockable: false,
            trustedServerPayload: true,
            ct);
        if (!string.Equals(prepared.PayloadHash, source.PayloadHash, StringComparison.Ordinal) ||
            !string.Equals(source.MigrationState, DynamicFlowDefinitionMigrationStates.Canonical, StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_REVISION_CONFLICT,
                new
                {
                    familyId = template.Id,
                    versionId = source.Id,
                    reason = "DYNAMIC_FLOW_REOPEN_SOURCE_INTEGRITY_UNPROVEN"
                });
        }

        var result = await RunCommandAsync(
            DefinitionCommandKinds.Reopen,
            commandId,
            requestHash,
            actorUserId,
            async (session, transactionCt) =>
            {
                var currentFamily = await _ctx.DynamicFlowTemplates
                    .Find(session, x =>
                        x.Id == template.Id &&
                        x.FamilyRevision == req.ExpectedFamilyRevision &&
                        !x.IsDeleted &&
                        x.Status != DynamicFlowTemplateStatuses.Archived)
                    .FirstOrDefaultAsync(transactionCt)
                    ?? throw RevisionConflict(template.Id, source.Id, "DYNAMIC_FLOW_FAMILY_REVISION_STALE");
                var currentSource = await _ctx.DynamicFlowTemplateVersions
                    .Find(session, x =>
                        x.Id == source.Id &&
                        x.TemplateId == template.Id &&
                        x.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                        x.PayloadHash == source.PayloadHash &&
                        !x.IsDeleted)
                    .FirstOrDefaultAsync(transactionCt)
                    ?? throw RevisionConflict(template.Id, source.Id, "DYNAMIC_FLOW_REOPEN_SOURCE_STALE");
                var existingDraft = await _ctx.DynamicFlowTemplateVersions
                    .Find(session, x =>
                        x.TemplateId == template.Id &&
                        x.Status == DynamicFlowTemplateVersionStatuses.Draft &&
                        !x.IsDeleted)
                    .AnyAsync(transactionCt);
                if (existingDraft)
                    throw RevisionConflict(template.Id, source.Id, "DYNAMIC_FLOW_ACTIVE_DRAFT_ALREADY_EXISTS");
                var lastVersionNo = await _ctx.DynamicFlowTemplateVersions
                    .Find(session, x => x.TemplateId == template.Id && !x.IsDeleted)
                    .SortByDescending(x => x.VersionNo)
                    .Project(x => x.VersionNo)
                    .FirstOrDefaultAsync(transactionCt);

                var now = DateTime.UtcNow;
                var draft = NewDraftVersion(
                    template.Id,
                    rootFormId,
                    Math.Max(currentSource.VersionNo, lastVersionNo) + 1,
                    prepared,
                    actorUserId,
                    now);
                draft.OriginFamilyId = template.Id;
                draft.OriginVersionId = currentSource.Id;
                currentFamily.FamilyRevision++;
                currentFamily.UpdatedAtUtc = now;
                currentFamily.UpdatedByUserId = actorUserId;

                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.BeforeVersionWrite);
                await _ctx.DynamicFlowTemplateVersions.InsertOneAsync(session, draft, cancellationToken: transactionCt);
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.AfterVersionWrite);
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.BeforeFamilyWrite);
                var familyReplace = await _ctx.DynamicFlowTemplates.ReplaceOneAsync(
                    session,
                    x => x.Id == currentFamily.Id &&
                         x.FamilyRevision == req.ExpectedFamilyRevision &&
                         !x.IsDeleted &&
                         x.Status != DynamicFlowTemplateStatuses.Archived,
                    currentFamily,
                    cancellationToken: transactionCt);
                if (familyReplace.MatchedCount != 1)
                    throw RevisionConflict(template.Id, source.Id, "DYNAMIC_FLOW_FAMILY_REVISION_STALE");
                _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.AfterFamilyWrite);

                await InsertReceiptAndAuditAsync(
                    session,
                    DefinitionCommandKinds.Reopen,
                    commandId,
                    requestHash,
                    actorUserId,
                    currentFamily,
                    draft,
                    transactionCt);
                return draft;
            },
            LoadReceiptVersionAsync,
            ct);

        var dto = MapVersion(result, false);
        ApplyVersionPermissions(
            dto,
            template,
            result,
            actor,
            actorUserId,
            executeGrantVersionNos);
        return dto;
    }

    public async Task<DiffDynamicFlowTemplateVersionsDto> DiffVersionsAsync(
        string templateId,
        DiffDynamicFlowTemplateVersionsRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new DiffDynamicFlowTemplateVersionsRequest();
        var actor = await LoadRequiredActorAsync(actorUserId, ct);
        var template = await LoadTemplateForReadAsync(templateId, actor, actorUserId, ct);
        var readableVersionNos = await ResolveReadableVersionNosAsync(
            template,
            actor,
            actorUserId,
            ct);
        var fromId = NormalizeDefinitionId(req.FromVersionId, "fromVersionId");
        var toId = NormalizeDefinitionId(req.ToVersionId, "toVersionId");
        var fb = Builders<DynamicFlowTemplateVersion>.Filter;
        var filter = fb.Eq(x => x.TemplateId, template.Id) &
                     fb.In(x => x.Id, new[] { fromId, toId }) &
                     fb.Eq(x => x.IsDeleted, false);
        if (readableVersionNos is not null)
        {
            filter &= fb.Eq(x => x.Status, DynamicFlowTemplateVersionStatuses.Locked) &
                      fb.In(x => x.VersionNo, readableVersionNos);
        }
        var versions = await _ctx.DynamicFlowTemplateVersions
            .Find(filter)
            .ToListAsync(ct);
        var from = versions.FirstOrDefault(x => x.Id == fromId);
        var to = versions.FirstOrDefault(x => x.Id == toId);
        if (from is null || to is null)
            throw DefinitionAccessForbidden();

        await ValidateLockedVersionsIntegrityAsync(new[] { template }, versions, ct);
        var fromNode = JsonNode.Parse(from.PayloadJson)
                       ?? throw AppExceptionFactory.BadRequest(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID);
        var toNode = JsonNode.Parse(to.PayloadJson)
                     ?? throw AppExceptionFactory.BadRequest(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID);
        var operations = new List<DynamicFlowDefinitionDiffOperationDto>();
        BuildCanonicalDiff(fromNode, toNode, string.Empty, operations);
        return new DiffDynamicFlowTemplateVersionsDto
        {
            FamilyId = template.Id,
            FromVersionId = from.Id,
            ToVersionId = to.Id,
            FromPayloadHash = from.PayloadHash,
            ToPayloadHash = to.PayloadHash,
            Operations = operations
        };
    }

    private async Task<PreparedDefinitionPayload> PreparePayloadAsync(
        JsonElement? payload,
        string? payloadJson,
        string rootFormId,
        string actorUserId,
        bool requireLockable,
        bool trustedServerPayload,
        CancellationToken ct)
    {
        var ingress = DynamicFlowDefinitionPayloadContract.CanonicalizeRequestPayload(
            payload,
            payloadJson,
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: true,
                AllowServerManagedPins: trustedServerPayload,
                RequireServerManagedPins: false));
        if (!trustedServerPayload)
            NormalizeEmptyStatisticProfileForAuthoring(ingress.Payload);
        if (!string.IsNullOrWhiteSpace(ingress.Payload.RootDynamicFormTemplateId) &&
            !string.Equals(ingress.Payload.RootDynamicFormTemplateId, rootFormId, StringComparison.Ordinal))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID,
                new { path = "rootDynamicFormTemplateId", reason = "DYNAMIC_FLOW_ROOT_FORM_MISMATCH" });
        }
        ingress.Payload.RootDynamicFormTemplateId = rootFormId;

        var forms = await LoadDynamicFormTemplatesForPayloadAsync(
            rootFormId,
            ingress.CanonicalJson,
            actorUserId,
            ct);
        var rootDeclared = false;
        foreach (var formNode in ingress.Payload.FormNodes)
        {
            if (!forms.TryGetValue(formNode.DynamicFormTemplateId, out var form))
            {
                throw AppExceptionFactory.NotFound(
                    AppErrorCode.COMMON_NOT_FOUND,
                    new { reason = "DYNAMIC_FORM_TEMPLATE_NOT_FOUND" });
            }
            var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(form);
            formNode.DynamicFormFamilyId = FirstNonBlank(form.FamilyId, form.Id);
            formNode.DynamicFormVersionNo = Math.Max(1, form.VersionNo);
            formNode.DynamicFormSchemaHash = snapshot.Sha256;
            formNode.DynamicFormSnapshotHash = snapshot.Sha256;
            rootDeclared |= string.Equals(form.Id, rootFormId, StringComparison.Ordinal);
        }
        if (!rootDeclared)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID,
                new { path = "formNodes", reason = "DYNAMIC_FLOW_ROOT_FORM_NODE_REQUIRED" });
        }

        ingress.Payload.CatalogVersion = DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion;
        ingress.Payload.CatalogSemanticHash = DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256;
        var canonical = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            ingress.Payload,
            payloadJson: null,
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true));

        if (requireLockable)
        {
            if (HasNonEmptyStatisticProfile(canonical.Payload.StatisticProfile))
            {
                throw AppExceptionFactory.Create(
                    AppErrorCode.DYNAMIC_FLOW_STATISTIC_PROFILE_NOT_EXECUTABLE,
                    new
                    {
                        path = "statisticProfile",
                        tab = "result-statistics",
                        reason = "DYNAMIC_FLOW_STATISTIC_PROFILE_NOT_EXECUTABLE",
                        executionEligibility = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase,
                        blockedUntilPhase = "P8",
                        canExecute = false
                    });
            }
            EnsureLockableDynamicFormVersions(forms.Values);
            var endpointCatalogs = BuildFormEndpointCatalogs(canonical.Payload, forms);
            DynamicFlowDefinitionPayloadContract.ValidatePolicyCoverage(
                canonical.Payload,
                endpointCatalogs);
            DynamicFlowDefinitionPayloadContract.ValidateMappingDefinition(
                canonical.Payload,
                endpointCatalogs);
        }

        return new PreparedDefinitionPayload(
            canonical.CanonicalJson,
            canonical.PayloadHash,
            DynamicFlowDefinitionSchema.CurrentVersion,
            canonical.AdapterVersion,
            canonical.BlockedUntilPhase);
    }

    private static bool HasNonEmptyStatisticProfile(
        IReadOnlyDictionary<string, JsonElement> profile)
    {
        if (profile.Count == 0)
            return false;

        if (profile.Count != 1 ||
            !profile.TryGetValue("diffMode", out var diffMode) ||
            diffMode.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        var value = diffMode.GetString()?.Trim();
        return !string.IsNullOrWhiteSpace(value) &&
               !string.Equals(value, "NONE", StringComparison.OrdinalIgnoreCase);
    }

    internal static void NormalizeEmptyStatisticProfileForAuthoring(
        DynamicFlowTemplatePayloadDto payload)
    {
        if (payload.StatisticProfile.Count != 1 ||
            !payload.StatisticProfile.TryGetValue("diffMode", out var diffMode) ||
            diffMode.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var value = diffMode.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "NONE", StringComparison.OrdinalIgnoreCase))
        {
            payload.StatisticProfile.Clear();
        }
    }

    private static IReadOnlyDictionary<string, DynamicFlowFormEndpointCatalog> BuildFormEndpointCatalogs(
        DynamicFlowTemplatePayloadDto payload,
        IReadOnlyDictionary<string, DynamicFormTemplate> forms)
    {
        var result = new Dictionary<string, DynamicFlowFormEndpointCatalog>(StringComparer.Ordinal);
        foreach (var formNode in payload.FormNodes)
        {
            if (!forms.TryGetValue(formNode.DynamicFormTemplateId, out var form))
                continue;
            var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(form);
            var root = JsonNode.Parse(snapshot.Json) as JsonObject
                       ?? throw AppExceptionFactory.BadRequest(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID);
            var scalarFields = new List<DynamicFlowScalarEndpoint>();
            if (root["fields"] is JsonArray fields)
            {
                foreach (var field in fields.OfType<JsonObject>())
                {
                    var fieldId = ReadNodeString(field, "id", "fieldId");
                    if (!string.IsNullOrWhiteSpace(fieldId))
                        scalarFields.Add(new DynamicFlowScalarEndpoint(fieldId, ReadNodeString(field, "key", "fieldKey")));
                }
            }

            var tableColumns = new List<DynamicFlowTableColumnEndpoint>();
            if (root["blocks"] is JsonArray blocks)
            {
                foreach (var block in blocks.OfType<JsonObject>())
                {
                    var blockId = ReadNodeString(block, "blockId", "id");
                    if (string.IsNullOrWhiteSpace(blockId))
                        continue;
                    var tableMode = ReadNodeString(block, "tableMode")?.ToUpperInvariant() ?? "FIXED_GRID";
                    var columnKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    AddColumnKeys(block["indexMap"], columnKeys);
                    AddColumnKeys(block["valueSlots"], columnKeys);
                    AddColumnKeys(block["columns"], columnKeys);
                    AddColumnKeys(block["cells"], columnKeys);
                    AddColumnKeys(block["statisticColumns"], columnKeys);

                    if (block["dataRect"] is JsonObject dataRect &&
                        ReadNodeInt(dataRect, "c0") is { } c0 &&
                        ReadNodeInt(dataRect, "c1") is { } c1 &&
                        c1 >= c0 && c1 - c0 < 1000)
                    {
                        for (var offset = 0; offset <= c1 - c0; offset++)
                            columnKeys.Add($"col_{offset + 1}");
                    }
                    else if (ReadNodeInt(block, "w") is { } width && width is > 0 and <= 1000)
                    {
                        for (var offset = 0; offset < width; offset++)
                            columnKeys.Add($"col_{offset + 1}");
                    }

                    tableColumns.AddRange(columnKeys
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .Select(columnKey => new DynamicFlowTableColumnEndpoint(blockId, columnKey, tableMode)));
                }
            }

            result[formNode.FormNodeId] = new DynamicFlowFormEndpointCatalog(
                formNode.FormNodeId,
                scalarFields,
                tableColumns);
        }
        return result;
    }

    private static void AddColumnKeys(JsonNode? node, ISet<string> target)
    {
        if (node is not JsonArray array)
            return;
        foreach (var item in array.OfType<JsonObject>())
        {
            var key = ReadNodeString(item, "columnKey", "key", "column", "columnInstanceId");
            if (!string.IsNullOrWhiteSpace(key))
                target.Add(key);
        }
    }

    private static string? ReadNodeString(JsonObject node, params string[] names)
    {
        foreach (var name in names)
        {
            if (node.TryGetPropertyValue(name, out var value) &&
                value is JsonValue jsonValue &&
                jsonValue.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }
        return null;
    }

    private static int? ReadNodeInt(JsonObject node, string name)
    {
        if (!node.TryGetPropertyValue(name, out var value) || value is not JsonValue jsonValue)
            return null;
        if (jsonValue.TryGetValue<int>(out var integer))
            return integer;
        return jsonValue.TryGetValue<long>(out var longValue) && longValue is >= int.MinValue and <= int.MaxValue
            ? (int)longValue
            : null;
    }

    private static DynamicFlowTemplateVersion NewDraftVersion(
        string familyId,
        string rootFormId,
        int versionNo,
        PreparedDefinitionPayload prepared,
        string actorUserId,
        DateTime now)
    {
        var version = new DynamicFlowTemplateVersion
        {
            Id = ObjectId.GenerateNewId().ToString(),
            TemplateId = familyId,
            RootDynamicFormTemplateId = rootFormId,
            VersionNo = versionNo,
            Status = DynamicFlowTemplateVersionStatuses.Draft,
            DraftRevision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };
        ApplyPreparedPayload(version, prepared, definitionLockable: false);
        return version;
    }

    private static void ApplyPreparedPayload(
        DynamicFlowTemplateVersion version,
        PreparedDefinitionPayload prepared,
        bool definitionLockable)
    {
        version.SchemaVersion = prepared.SchemaVersion;
        version.AdapterVersion = prepared.AdapterVersion;
        version.CatalogVersion = DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion;
        version.CatalogSemanticHash = DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256;
        version.PayloadJson = prepared.CanonicalJson;
        version.PayloadHash = prepared.PayloadHash;
        version.DefinitionLockable = definitionLockable;
        version.ExecutionEligibility = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase;
        version.ExecutionBlockedReason = DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented;
        version.BlockedUntilPhase = prepared.BlockedUntilPhase;
        version.MigrationState = DynamicFlowDefinitionMigrationStates.Canonical;
    }

    private async Task<TResult> RunCommandAsync<TResult>(
        string commandKind,
        string commandId,
        string requestHash,
        string actorUserId,
        Func<IClientSessionHandle, CancellationToken, Task<TResult>> operation,
        Func<DynamicFlowDefinitionCommandReceipt, CancellationToken, Task<TResult>> replayLoader,
        CancellationToken ct)
    {
        var existing = await LoadReceiptAsync(actorUserId, commandKind, commandId, ct);
        if (existing is not null)
        {
            _ = await LoadRequiredActorAsync(actorUserId, ct);
            EnsureReplayMatches(existing, requestHash);
            return await replayLoader(existing, ct);
        }

        try
        {
            return await _transactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    var actorIsActive = await _ctx.Users
                        .Find(session, user => user.Id == actorUserId && !user.IsDeleted)
                        .Project(user => user.Id)
                        .AnyAsync(transactionCt);
                    if (!actorIsActive)
                        throw AppExceptionFactory.Unauthorized();

                    return await operation(session, transactionCt);
                },
                ct);
        }
        catch (Exception ex) when (IsMongoCommandRace(ex))
        {
            var racedReceipt = await LoadReceiptAsync(actorUserId, commandKind, commandId, ct);
            if (racedReceipt is not null)
            {
                _ = await LoadRequiredActorAsync(actorUserId, ct);
                EnsureReplayMatches(racedReceipt, requestHash);
                return await replayLoader(racedReceipt, ct);
            }

            throw RevisionConflict(null, null, "DYNAMIC_FLOW_CONCURRENT_MUTATION_LOST");
        }
    }

    private async Task<DynamicFlowDefinitionCommandReceipt?> LoadReceiptAsync(
        string actorUserId,
        string commandKind,
        string commandId,
        CancellationToken ct)
        => await _ctx.DynamicFlowDefinitionCommandReceipts
            .Find(x =>
                x.ActorUserId == actorUserId &&
                x.CommandKind == commandKind &&
                x.CommandId == commandId)
            .FirstOrDefaultAsync(ct);

    private async Task<DynamicFlowDefinitionCommandReceipt?> LoadMatchingReplayReceiptAsync(
        string actorUserId,
        string commandKind,
        string commandId,
        string requestHash,
        CancellationToken ct)
    {
        var receipt = await LoadReceiptAsync(actorUserId, commandKind, commandId, ct);
        if (receipt is null)
            return null;

        // Replays are reads of a previously committed result, but a disabled actor
        // must never be able to use an old command receipt as an authentication
        // bypass. Re-read the actor immediately before exposing the snapshot.
        _ = await LoadRequiredActorAsync(actorUserId, ct);
        EnsureReplayMatches(receipt, requestHash);
        return receipt;
    }

    private async Task<DynamicFlowTemplateDto> MapFamilyReplayAsync(
        DynamicFlowDefinitionCommandReceipt receipt,
        AppUser actor,
        string actorUserId,
        CancellationToken ct)
    {
        var family = await LoadReceiptFamilyAsync(receipt, ct);
        var versions = new List<DynamicFlowTemplateVersionDto>();
        if (!string.IsNullOrWhiteSpace(receipt.VersionId))
        {
            var version = await LoadReceiptVersionAsync(receipt, ct);
            var isUsed = await IsVersionUsedAsync(family.Id, version.VersionNo, ct);
            versions.Add(MapVersion(version, isUsed));
        }

        var dto = MapTemplate(family, versions);
        var executeGrantVersionNos = await LoadExecuteGrantVersionNosAsync(
            family.Id,
            actorUserId,
            ct);
        ApplyTemplatePermissions(
            dto,
            family,
            actor,
            actorUserId,
            executeGrantVersionNos);
        return dto;
    }

    private async Task<DynamicFlowTemplateVersionDto> MapVersionReplayAsync(
        DynamicFlowDefinitionCommandReceipt receipt,
        AppUser actor,
        string actorUserId,
        CancellationToken ct)
    {
        var family = await LoadReceiptFamilyAsync(receipt, ct);
        var version = await LoadReceiptVersionAsync(receipt, ct);
        var dto = MapVersion(
            version,
            await IsVersionUsedAsync(family.Id, version.VersionNo, ct));
        var executeGrantVersionNos = await LoadExecuteGrantVersionNosAsync(
            family.Id,
            actorUserId,
            ct);
        ApplyVersionPermissions(
            dto,
            family,
            version,
            actor,
            actorUserId,
            executeGrantVersionNos);
        return dto;
    }

    private static void EnsureReplayMatches(
        DynamicFlowDefinitionCommandReceipt receipt,
        string requestHash)
    {
        if (!string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT,
                new
                {
                    reason = "DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT",
                    commandKind = receipt.CommandKind
                });
        }
    }

    private async Task InsertReceiptAndAuditAsync(
        IClientSessionHandle session,
        string commandKind,
        string commandId,
        string requestHash,
        string actorUserId,
        DynamicFlowTemplate family,
        DynamicFlowTemplateVersion? version,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var familySnapshot = BuildReceiptFamilySnapshot(family);
        var versionSnapshot = BuildReceiptVersionSnapshot(version);
        var receipt = new DynamicFlowDefinitionCommandReceipt
        {
            Id = ObjectId.GenerateNewId().ToString(),
            ActorUserId = actorUserId,
            CommandKind = commandKind,
            CommandId = commandId,
            RequestHash = requestHash,
            FamilyId = family.Id,
            VersionId = version?.Id,
            ResultFamilyRevision = family.FamilyRevision,
            ResultDraftRevision = version?.DraftRevision,
            ResultPayloadHash = version?.PayloadHash,
            ResultFamilySnapshot = familySnapshot,
            ResultFamilySnapshotSha256 = ReceiptSnapshotSha256(familySnapshot),
            ResultVersionSnapshot = versionSnapshot,
            ResultVersionSnapshotSha256 = versionSnapshot is null
                ? null
                : ReceiptSnapshotSha256(versionSnapshot),
            CorrelationId = commandId,
            Outcome = DynamicFlowDefinitionCommandOutcomes.Succeeded,
            CreatedAtUtc = now,
            CompletedAtUtc = now
        };

        _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.BeforeReceiptWrite);
        await _ctx.DynamicFlowDefinitionCommandReceipts.InsertOneAsync(
            session,
            receipt,
            cancellationToken: ct);
        _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.AfterReceiptWrite);

        var audit = new UserActionLog
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Action = UserActionLogActions.DynamicFlowDefinitionMutated,
            Scope = "DYNAMIC_FLOW_DEFINITION",
            Result = UserActionLogResults.Success,
            OccurredAtUtc = now,
            ActorUserId = actorUserId,
            DynamicFlowFamilyId = family.Id,
            DynamicFlowVersionId = version?.Id,
            DynamicFlowCommandReceiptId = receipt.Id,
            Summary = commandKind,
            IdempotencyKey = $"dynamic-flow-definition:{receipt.Id}",
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["commandKind"] = commandKind,
                ["commandId"] = commandId,
                ["familyRevision"] = family.FamilyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["requestHash"] = requestHash,
                ["draftRevision"] = version?.DraftRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ["payloadHash"] = version?.PayloadHash ?? string.Empty
            },
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };

        _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.BeforeAuditWrite);
        await _ctx.UserActionLogs.InsertOneAsync(session, audit, cancellationToken: ct);
        _faults.ThrowIfConfigured(commandId, DynamicFlowDefinitionFaultPoints.AfterAuditWrite);
    }

    private async Task<DynamicFlowTemplate> LoadReceiptFamilyAsync(
        DynamicFlowDefinitionCommandReceipt receipt,
        CancellationToken ct)
    {
        if (receipt.ResultFamilySnapshot is not null)
            return ReadReceiptFamilySnapshot(receipt);

        return await _ctx.DynamicFlowTemplates
                   .Find(x =>
                       x.Id == receipt.FamilyId &&
                       x.FamilyRevision == receipt.ResultFamilyRevision)
                   .FirstOrDefaultAsync(ct)
               ?? throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT,
                new { reason = "DYNAMIC_FLOW_COMMAND_RESULT_SNAPSHOT_MISSING" });
    }

    private async Task<DynamicFlowTemplateVersion> LoadReceiptVersionAsync(
        DynamicFlowDefinitionCommandReceipt receipt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(receipt.VersionId))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT,
                new { reason = "DYNAMIC_FLOW_COMMAND_VERSION_RESULT_MISSING" });
        }

        if (receipt.ResultVersionSnapshot is not null)
            return ReadReceiptVersionSnapshot(receipt);

        return await _ctx.DynamicFlowTemplateVersions
                   .Find(x =>
                       x.Id == receipt.VersionId &&
                       x.TemplateId == receipt.FamilyId &&
                       x.DraftRevision == receipt.ResultDraftRevision &&
                       x.PayloadHash == receipt.ResultPayloadHash)
                   .FirstOrDefaultAsync(ct)
               ?? throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT,
                new { reason = "DYNAMIC_FLOW_COMMAND_RESULT_SNAPSHOT_MISSING" });
    }

    private async Task ApplyTemplatePermissionsAsync(
        DynamicFlowTemplateDto dto,
        DynamicFlowTemplate family,
        AppUser actor,
        string actorUserId,
        CancellationToken ct)
    {
        var executeGrantVersionNos = await LoadExecuteGrantVersionNosAsync(
            family.Id,
            actorUserId,
            ct);
        ApplyTemplatePermissions(
            dto,
            family,
            actor,
            actorUserId,
            executeGrantVersionNos);
    }

    private static void ApplyTemplatePermissions(
        DynamicFlowTemplateDto dto,
        DynamicFlowTemplate family,
        AppUser actor,
        string actorUserId,
        IReadOnlySet<int> executeGrantVersionNos)
    {
        dto.CanRead = true;
        dto.CanManage = DynamicFlowTemplateReadAccess.IsOwner(family, actorUserId) ||
                        DynamicFlowTemplateReadAccess.IsAdministrator(actor);
        var executionVersionNo = dto.CurrentVersionNo;
        dto.ExecuteGrant = executionVersionNo.HasValue &&
                           executeGrantVersionNos.Contains(executionVersionNo.Value);
        var eligibilityVersion = dto.CurrentVersion ?? dto.DraftVersion ?? dto.Versions.FirstOrDefault();
        dto.DefinitionLockable = eligibilityVersion?.DefinitionLockable ?? false;
        if (eligibilityVersion is null && family.HasLockedVersion)
            dto.DefinitionLockable = true;
        dto.ExecutionEligibility = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase;
        dto.ExecutionBlockedReason = DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented;
        dto.BlockedUntilPhase = eligibilityVersion?.BlockedUntilPhase;
        dto.CanExecute = false;

        var nested = dto.Versions
            .Concat(dto.CurrentVersion is null ? Array.Empty<DynamicFlowTemplateVersionDto>() : new[] { dto.CurrentVersion })
            .Concat(dto.DraftVersion is null ? Array.Empty<DynamicFlowTemplateVersionDto>() : new[] { dto.DraftVersion })
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => x.First());
        foreach (var versionDto in nested)
        {
            versionDto.CanRead = true;
            versionDto.CanManage = dto.CanManage;
            versionDto.ExecuteGrant = executeGrantVersionNos.Contains(versionDto.VersionNo);
            versionDto.ExecutionEligibility = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase;
            versionDto.ExecutionBlockedReason = DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented;
            versionDto.CanExecute = false;
        }
    }

    private async Task ApplyVersionPermissionsAsync(
        DynamicFlowTemplateVersionDto dto,
        DynamicFlowTemplate family,
        DynamicFlowTemplateVersion version,
        AppUser actor,
        string actorUserId,
        CancellationToken ct)
    {
        var executeGrantVersionNos = await LoadExecuteGrantVersionNosAsync(
            family.Id,
            actorUserId,
            ct);
        ApplyVersionPermissions(
            dto,
            family,
            version,
            actor,
            actorUserId,
            executeGrantVersionNos);
    }

    private static void ApplyVersionPermissions(
        DynamicFlowTemplateVersionDto dto,
        DynamicFlowTemplate family,
        DynamicFlowTemplateVersion version,
        AppUser actor,
        string actorUserId,
        IReadOnlySet<int> executeGrantVersionNos)
    {
        dto.CanRead = true;
        dto.CanManage = DynamicFlowTemplateReadAccess.IsOwner(family, actorUserId) ||
                        DynamicFlowTemplateReadAccess.IsAdministrator(actor);
        dto.ExecuteGrant = executeGrantVersionNos.Contains(version.VersionNo);
        dto.ExecutionEligibility = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase;
        dto.ExecutionBlockedReason = DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented;
        dto.CanExecute = false;
    }

    private async Task<HashSet<int>> LoadExecuteGrantVersionNosAsync(
        string familyId,
        string actorUserId,
        CancellationToken ct)
    {
        var versionNos = await _ctx.WorkAssignments
            .Find(DynamicFlowTemplateReadAccess.BuildAssignmentParticipantFilter(
                actorUserId,
                familyId))
            .Project(x => x.FlowTemplateVersionNo)
            .ToListAsync(ct);
        return versionNos
            .Where(versionNo => versionNo.HasValue)
            .Select(versionNo => versionNo!.Value)
            .ToHashSet();
    }

    private static string EnsureCommandId(string? commandId)
    {
        commandId = commandId?.Trim();
        if (string.IsNullOrWhiteSpace(commandId))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FLOW_COMMAND_ID_REQUIRED,
                new { path = "commandId", reason = "DYNAMIC_FLOW_COMMAND_ID_REQUIRED" });
        }
        if (commandId.Length > 200)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FLOW_COMMAND_ID_REQUIRED,
                new { path = "commandId", reason = "DYNAMIC_FLOW_COMMAND_ID_INVALID" });
        }
        return commandId;
    }

    private static void EnsureNoRequestAuthority(
        IReadOnlyDictionary<string, JsonElement>? additionalProperties)
    {
        if (additionalProperties is null || additionalProperties.Count == 0)
            return;

        var property = additionalProperties.Keys
            .OrderBy(x => x, StringComparer.Ordinal)
            .First();
        var authority = property.Equals("callerRole", StringComparison.OrdinalIgnoreCase) ||
                        property.Equals("callerRoles", StringComparison.OrdinalIgnoreCase) ||
                        property.Equals("ownerUserId", StringComparison.OrdinalIgnoreCase) ||
                        property.Equals("ownerUnitId", StringComparison.OrdinalIgnoreCase) ||
                        property.Equals("executeGrant", StringComparison.OrdinalIgnoreCase) ||
                        property.Equals("canRead", StringComparison.OrdinalIgnoreCase) ||
                        property.Equals("canManage", StringComparison.OrdinalIgnoreCase) ||
                        property.Equals("canExecute", StringComparison.OrdinalIgnoreCase) ||
                        property.Equals("permissions", StringComparison.OrdinalIgnoreCase) ||
                        property.Equals("permission", StringComparison.OrdinalIgnoreCase) ||
                        property.Equals("grant", StringComparison.OrdinalIgnoreCase);
        throw AppExceptionFactory.BadRequest(
            authority
                ? AppErrorCode.DYNAMIC_FLOW_CALLER_AUTHORITY_FORBIDDEN
                : AppErrorCode.DYNAMIC_FLOW_PAYLOAD_UNKNOWN_FIELD,
            new
            {
                path = property,
                reason = authority
                    ? "DYNAMIC_FLOW_CALLER_AUTHORITY_FORBIDDEN"
                    : "DYNAMIC_FLOW_REQUEST_UNKNOWN_FIELD"
            });
    }

    private static void EnsurePositiveRevision(int revision, string path)
    {
        if (revision <= 0)
            throw RevisionConflict(null, null, "DYNAMIC_FLOW_REVISION_REQUIRED", path);
    }

    private static string NormalizeExpectedHash(string? value, string path)
    {
        value = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length != 64 ||
            value.Any(x => !Uri.IsHexDigit(x)))
        {
            throw RevisionConflict(null, null, "DYNAMIC_FLOW_PAYLOAD_HASH_REQUIRED", path);
        }
        return value;
    }

    internal static string BuildLockRequestHash(
        string familyId,
        string versionId,
        string expectedPayloadHash,
        LockDynamicFlowTemplateVersionRequest request,
        DynamicFlowContributionPolicySelection contributionSelection)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(contributionSelection);

        // Preserve the exact pre-P8 shape when both newly introduced fields
        // are absent. This keeps already-committed LOCK receipts replayable,
        // while any explicit P8 field is bound by the expanded request hash.
        if (request.ContributionPolicy is null &&
            !request.AcknowledgeContributionWarning.HasValue)
        {
            return RequestHash(new
            {
                familyId,
                versionId,
                request.ExpectedFamilyRevision,
                request.ExpectedDraftRevision,
                expectedPayloadHash
            });
        }

        return RequestHash(new
        {
            familyId,
            versionId,
            request.ExpectedFamilyRevision,
            request.ExpectedDraftRevision,
            expectedPayloadHash,
            contributionPolicy = contributionSelection.Policy,
            contributionWarningAcknowledged = request.AcknowledgeContributionWarning ?? false
        });
    }

    private static string RequestHash<T>(T value)
        => Sha256(JsonSerializer.Serialize(value, JsonOptions));

    private static string? CanonicalRequestPayload(JsonElement? payload)
        => payload.HasValue
            ? DynamicFlowDefinitionPayloadContract.CanonicalizeJson(payload.Value.GetRawText())
            : null;

    private static string? CanonicalRequestPayload(string? payloadJson)
        => payloadJson is null
            ? null
            : DynamicFlowDefinitionPayloadContract.CanonicalizeJson(payloadJson);

    private static void EnsureUnambiguousPayloadRequest(
        JsonElement? payload,
        string? payloadJson)
    {
        var hasTypedPayload = payload.HasValue &&
                              payload.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        if (hasTypedPayload && payloadJson is not null)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FLOW_TEMPLATE_PAYLOAD_AMBIGUOUS,
                new
                {
                    reason = "DYNAMIC_FLOW_TEMPLATE_PAYLOAD_AMBIGUOUS",
                    path = "payload",
                    legacyField = "payloadJson"
                });
        }
    }

    private static AppException RevisionConflict(
        string? familyId,
        string? versionId,
        string reason,
        string? path = null)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_REVISION_CONFLICT,
            new { familyId, versionId, reason, path });

    private static bool IsMongoCommandRace(Exception exception)
    {
        if (exception is MongoWriteException writeException &&
            writeException.WriteError?.Code is 11000 or 112)
        {
            return true;
        }
        if (exception is MongoCommandException commandException &&
            commandException.Code is 11000 or 112 or 251)
        {
            return true;
        }
        if (exception is MongoException mongoException &&
            (mongoException.HasErrorLabel("TransientTransactionError") ||
             mongoException.HasErrorLabel("UnknownTransactionCommitResult")))
        {
            return true;
        }
        return exception.InnerException is not null && IsMongoCommandRace(exception.InnerException);
    }

    private static void BuildCanonicalDiff(
        JsonNode? from,
        JsonNode? to,
        string path,
        List<DynamicFlowDefinitionDiffOperationDto> operations)
    {
        if (JsonNode.DeepEquals(from, to))
            return;
        if (from is JsonObject fromObject && to is JsonObject toObject)
        {
            var keys = fromObject.Select(x => x.Key)
                .Concat(toObject.Select(x => x.Key))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var childPath = $"{path}/{EscapeJsonPointer(key)}";
                var hasFrom = fromObject.TryGetPropertyValue(key, out var fromChild);
                var hasTo = toObject.TryGetPropertyValue(key, out var toChild);
                if (!hasFrom)
                    operations.Add(DiffOperation(DynamicFlowDefinitionDiffOperations.Add, childPath, null, toChild));
                else if (!hasTo)
                    operations.Add(DiffOperation(DynamicFlowDefinitionDiffOperations.Remove, childPath, fromChild, null));
                else
                    BuildCanonicalDiff(fromChild, toChild, childPath, operations);
            }
            return;
        }
        if (from is JsonArray fromArray && to is JsonArray toArray)
        {
            var common = Math.Min(fromArray.Count, toArray.Count);
            for (var index = 0; index < common; index++)
                BuildCanonicalDiff(fromArray[index], toArray[index], $"{path}/{index}", operations);
            for (var index = fromArray.Count - 1; index >= toArray.Count; index--)
                operations.Add(DiffOperation(DynamicFlowDefinitionDiffOperations.Remove, $"{path}/{index}", fromArray[index], null));
            for (var index = common; index < toArray.Count; index++)
                operations.Add(DiffOperation(DynamicFlowDefinitionDiffOperations.Add, $"{path}/{index}", null, toArray[index]));
            return;
        }

        operations.Add(DiffOperation(
            DynamicFlowDefinitionDiffOperations.Replace,
            string.IsNullOrEmpty(path) ? "/" : path,
            from,
            to));
    }

    private static DynamicFlowDefinitionDiffOperationDto DiffOperation(
        string op,
        string path,
        JsonNode? from,
        JsonNode? to)
        => new()
        {
            Op = op,
            Path = path,
            FromValue = from is null ? null : JsonSerializer.SerializeToElement(from, JsonOptions),
            ToValue = to is null ? null : JsonSerializer.SerializeToElement(to, JsonOptions)
        };

    private static string EscapeJsonPointer(string value)
        => value.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    private sealed record PreparedDefinitionPayload(
        string CanonicalJson,
        string PayloadHash,
        int SchemaVersion,
        int AdapterVersion,
        string BlockedUntilPhase);
}
