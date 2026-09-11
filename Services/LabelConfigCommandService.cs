using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Labels;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services;

public interface ILabelConfigCommandService
{
    Task<LabelConfigResult> GetAsync(
        string id,
        CancellationToken ct);

    Task<LabelConfigResult> CreateAsync(
        StatConfigMutationEnvelope<LabelConfigPayload> request,
        CancellationToken ct);

    Task<LabelConfigResult> UpdateAsync(
        string id,
        StatConfigMutationEnvelope<LabelConfigPayload> request,
        CancellationToken ct);

    Task<LabelConfigResult> TombstoneAsync(
        string id,
        StatConfigMutationEnvelope<LabelTombstonePayload> request,
        CancellationToken ct);
}

/// <summary>
/// P8 command boundary for the canonical labels aggregate. The legacy
/// LabelService remains the search adapter; every HTTP mutation is routed here
/// and commits label + embedded version + durable receipt atomically.
/// </summary>
public sealed partial class LabelConfigCommandService :
    ILabelConfigCommandService
{
    private static readonly Regex HexColorRegex = new(
        "^#[0-9a-fA-F]{6}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly ILabelEnumCatalogService _enumCatalogs;
    private readonly IStatConfigTransactionRunner _transactions;

    public LabelConfigCommandService(
        MongoDbContext ctx,
        MeAccessor me,
        ILabelEnumCatalogService enumCatalogs,
        IStatConfigTransactionRunner transactions)
    {
        _ctx = ctx;
        _me = me;
        _enumCatalogs = enumCatalogs;
        _transactions = transactions;
    }

    public async Task<LabelConfigResult> GetAsync(
        string id,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        id = RequireObjectId(id);
        var filter =
            Builders<LabelCatalogItem>.Filter.Eq(item => item.Id, id) &
            BuildVisibleFilter(me);
        var label = await _ctx.Labels.Find(filter).FirstOrDefaultAsync(ct);
        if (label is null)
            throw LabelNotFound(id);

        EnsureConfigHashIntegrity(label);
        return ToResult(label, me, receiptId: null);
    }

    public async Task<LabelConfigResult> CreateAsync(
        StatConfigMutationEnvelope<LabelConfigPayload> request,
        CancellationToken ct)
    {
        using var isolationScope =
            StatConfigIsolationGuard.EnterConfigurationMutation(
                StatConfigOwnerKinds.Label);
        var me = _me.RequireMe();
        RequireManager(me);
        var command = StatConfigCanonicalJson.NormalizeCommand(
            request,
            StatConfigCommandKinds.UpsertLabel);
        var normalized = await NormalizePayloadAsync(
            command.Payload,
            me,
            ct);
        command = Rehash(command, normalized.HashContent);

        if (command.ExpectedRevision != 0 ||
            command.ExpectedConfigHash !=
            StatConfigCanonicalJson.EmptyConfigHash)
        {
            throw CasConflict(
                command,
                actualRevision: 0,
                actualHash: StatConfigCanonicalJson.EmptyConfigHash);
        }

        var ownerId = DeterministicObjectId(
            string.Concat(
                StatConfigOwnerKinds.Label,
                "\0",
                normalized.ScopeType,
                "\0",
                normalized.ScopeId ?? "-",
                "\0",
                normalized.Code));
        var receiptId = ReceiptId(ownerId, command.CommandId);

        return await ExecuteWithDuplicateReplayAsync(
            receiptId,
            command.RequestHash,
            async (session, transactionCt) =>
            {
                var replay = await LoadReplayAsync(
                    session,
                    receiptId,
                    command.RequestHash,
                    transactionCt);
                if (replay is not null)
                    return replay;

                var existingById = await _ctx.Labels
                    .Find(
                        session,
                        Builders<LabelCatalogItem>.Filter.Eq(
                            item => item.Id,
                            ownerId))
                    .FirstOrDefaultAsync(transactionCt);
                if (existingById is not null)
                {
                    throw existingById.IsDeleted
                        ? AppExceptionFactory.Create(
                            AppErrorCode.LABEL_CONFIG_TOMBSTONED,
                            new { ownerKind = StatConfigOwnerKinds.Label })
                        : AppExceptionFactory.Create(
                            AppErrorCode.LABEL_DUPLICATE_CODE,
                            new
                            {
                                normalized.Code,
                                normalized.ScopeType,
                                normalized.ScopeId
                            });
                }

                await EnsureCodeReservedAsync(
                    session,
                    normalized.ScopeType,
                    normalized.ScopeId,
                    normalized.Code,
                    exceptId: null,
                    transactionCt);
                await RevalidateDependencyAsync(
                    session,
                    normalized,
                    transactionCt);

                var now = DateTime.UtcNow;
                var versionId = ObjectId.GenerateNewId().ToString();
                var label = new LabelCatalogItem
                {
                    Id = ownerId,
                    ConfigId = ownerId,
                    VersionId = versionId,
                    PreviousVersionId = null,
                    VersionNo = 1,
                    Revision = 1,
                    Code = normalized.Code,
                    Name = normalized.Name,
                    NameLower = normalized.Name.ToLowerInvariant(),
                    Description = normalized.Description,
                    Color = normalized.Color,
                    GroupCode = normalized.GroupCode,
                    Usage = normalized.Usage,
                    DataType = normalized.DataType,
                    ValueSourceType = normalized.ValueSourceType,
                    ValueOptions = normalized.ValueOptions,
                    ValueSourceCatalogId =
                        normalized.ValueSourceCatalog?.Id,
                    ValueSourceCatalogCode =
                        normalized.ValueSourceCatalog?.Code,
                    ValueSourceCatalogName =
                        normalized.ValueSourceCatalog?.Name,
                    ScopeType = normalized.ScopeType,
                    ScopeId = normalized.ScopeId,
                    ManagedByUserId = me.Id,
                    IsSystem = false,
                    IsActive = normalized.IsActive,
                    DependencyPins = normalized.DependencyPins,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = me.Id,
                    UpdatedByUserId = me.Id,
                    IsDeleted = false
                };
                label.ConfigHash = ComputeConfigHash(
                    label,
                    CurrentStatus(label));
                label.VersionSnapshots.Add(
                    CreateVersionSnapshot(
                        label,
                        CurrentStatus(label),
                        me.Id,
                        now));

                var result = ToResult(label, me, receiptId);
                var receipt = CreateReceipt(
                    receiptId,
                    command,
                    label,
                    result,
                    me.Id,
                    now);

                await _ctx.Labels.InsertOneAsync(
                    session,
                    label,
                    cancellationToken: transactionCt);
                await _ctx.StatConfigCommandReceipts.InsertOneAsync(
                    session,
                    receipt,
                    cancellationToken: transactionCt);
                return result;
            },
            ct);
    }

    public async Task<LabelConfigResult> UpdateAsync(
        string id,
        StatConfigMutationEnvelope<LabelConfigPayload> request,
        CancellationToken ct)
    {
        using var isolationScope =
            StatConfigIsolationGuard.EnterConfigurationMutation(
                StatConfigOwnerKinds.Label);
        var me = _me.RequireMe();
        RequireManager(me);
        id = RequireObjectId(id);
        var command = StatConfigCanonicalJson.NormalizeCommand(
            request,
            StatConfigCommandKinds.UpsertLabel);
        var normalized = await NormalizePayloadAsync(
            command.Payload,
            me,
            ct);
        command = Rehash(command, normalized.HashContent);
        var receiptId = ReceiptId(id, command.CommandId);

        return await ExecuteWithDuplicateReplayAsync(
            receiptId,
            command.RequestHash,
            async (session, transactionCt) =>
            {
                var label = await LoadManageableAsync(
                    session,
                    id,
                    me,
                    includeDeleted: true,
                    transactionCt);
                if (label is null)
                    throw LabelNotFound(id);

                var replay = await LoadReplayAsync(
                    session,
                    receiptId,
                    command.RequestHash,
                    transactionCt);
                if (replay is not null)
                    return replay;

                if (label.IsDeleted)
                {
                    throw AppExceptionFactory.Create(
                        AppErrorCode.LABEL_CONFIG_TOMBSTONED,
                        new { ownerKind = StatConfigOwnerKinds.Label });
                }
                EnsureConfigHashIntegrity(label);
                EnsureImmutableIdentity(label, normalized);
                EnsureCas(label, command);
                await EnsureCodeReservedAsync(
                    session,
                    normalized.ScopeType,
                    normalized.ScopeId,
                    normalized.Code,
                    id,
                    transactionCt);
                await RevalidateDependencyAsync(
                    session,
                    normalized,
                    transactionCt);

                var priorRevision = label.Revision;
                var priorHash = label.ConfigHash!;
                var now = DateTime.UtcNow;
                var previousVersionId = label.VersionId;
                label.PreviousVersionId = previousVersionId;
                label.VersionId = ObjectId.GenerateNewId().ToString();
                label.VersionNo++;
                label.Revision++;
                label.Name = normalized.Name;
                label.NameLower = normalized.Name.ToLowerInvariant();
                label.Description = normalized.Description;
                label.Color = normalized.Color;
                label.GroupCode = normalized.GroupCode;
                label.Usage = normalized.Usage;
                label.DataType = normalized.DataType;
                label.ValueSourceType = normalized.ValueSourceType;
                label.ValueOptions = normalized.ValueOptions;
                label.ValueSourceCatalogId =
                    normalized.ValueSourceCatalog?.Id;
                label.ValueSourceCatalogCode =
                    normalized.ValueSourceCatalog?.Code;
                label.ValueSourceCatalogName =
                    normalized.ValueSourceCatalog?.Name;
                label.IsActive = normalized.IsActive;
                label.DependencyPins = normalized.DependencyPins;
                label.UpdatedAtUtc = now;
                label.UpdatedByUserId = me.Id;
                label.ConfigHash = ComputeConfigHash(
                    label,
                    CurrentStatus(label));
                label.VersionSnapshots ??= new();
                label.VersionSnapshots.Add(
                    CreateVersionSnapshot(
                        label,
                        CurrentStatus(label),
                        me.Id,
                        now));

                var filter =
                    Builders<LabelCatalogItem>.Filter.Eq(
                        item => item.Id,
                        id) &
                    Builders<LabelCatalogItem>.Filter.Eq(
                        item => item.Revision,
                        priorRevision) &
                    Builders<LabelCatalogItem>.Filter.Eq(
                        item => item.ConfigHash,
                        priorHash) &
                    Builders<LabelCatalogItem>.Filter.Eq(
                        item => item.IsDeleted,
                        false);
                var replacement = await _ctx.Labels.ReplaceOneAsync(
                    session,
                    filter,
                    label,
                    cancellationToken: transactionCt);
                if (replacement.ModifiedCount != 1)
                    throw CasConflict(command, priorRevision, priorHash);

                var result = ToResult(label, me, receiptId);
                await _ctx.StatConfigCommandReceipts.InsertOneAsync(
                    session,
                    CreateReceipt(
                        receiptId,
                        command,
                        label,
                        result,
                        me.Id,
                        now),
                    cancellationToken: transactionCt);
                return result;
            },
            ct);
    }

    public async Task<LabelConfigResult> TombstoneAsync(
        string id,
        StatConfigMutationEnvelope<LabelTombstonePayload> request,
        CancellationToken ct)
    {
        using var isolationScope =
            StatConfigIsolationGuard.EnterConfigurationMutation(
                StatConfigOwnerKinds.Label);
        var me = _me.RequireMe();
        RequireManager(me);
        id = RequireObjectId(id);
        var command = StatConfigCanonicalJson.NormalizeCommand(
            request,
            StatConfigCommandKinds.TombstoneLabel);
        var receiptId = ReceiptId(id, command.CommandId);

        return await ExecuteWithDuplicateReplayAsync(
            receiptId,
            command.RequestHash,
            async (session, transactionCt) =>
            {
                var label = await LoadManageableAsync(
                    session,
                    id,
                    me,
                    includeDeleted: true,
                    transactionCt);
                if (label is null)
                    throw LabelNotFound(id);

                var replay = await LoadReplayAsync(
                    session,
                    receiptId,
                    command.RequestHash,
                    transactionCt);
                if (replay is not null)
                    return replay;

                if (label.IsDeleted)
                {
                    throw AppExceptionFactory.Create(
                        AppErrorCode.LABEL_CONFIG_TOMBSTONED,
                        new { ownerKind = StatConfigOwnerKinds.Label });
                }
                EnsureConfigHashIntegrity(label);
                EnsureCas(label, command);
                if (await HasBackReferenceAsync(
                        session,
                        label,
                        transactionCt))
                {
                    throw AppExceptionFactory.Create(
                        AppErrorCode.LABEL_DELETE_REFERENCED,
                        new
                        {
                            ownerKind = StatConfigOwnerKinds.Label,
                            ownerId = label.Id
                        });
                }

                var priorRevision = label.Revision;
                var priorHash = label.ConfigHash!;
                var now = DateTime.UtcNow;
                label.PreviousVersionId = label.VersionId;
                label.VersionId = ObjectId.GenerateNewId().ToString();
                label.VersionNo++;
                label.Revision++;
                label.IsActive = false;
                label.IsDeleted = true;
                label.DeletedAtUtc = now;
                label.DeletedByUserId = me.Id;
                label.UpdatedAtUtc = now;
                label.UpdatedByUserId = me.Id;
                label.ConfigHash = ComputeConfigHash(
                    label,
                    StatConfigStatuses.Tombstoned);
                label.VersionSnapshots ??= new();
                label.VersionSnapshots.Add(
                    CreateVersionSnapshot(
                        label,
                        StatConfigStatuses.Tombstoned,
                        me.Id,
                        now));

                var filter =
                    Builders<LabelCatalogItem>.Filter.Eq(
                        item => item.Id,
                        id) &
                    Builders<LabelCatalogItem>.Filter.Eq(
                        item => item.Revision,
                        priorRevision) &
                    Builders<LabelCatalogItem>.Filter.Eq(
                        item => item.ConfigHash,
                        priorHash) &
                    Builders<LabelCatalogItem>.Filter.Eq(
                        item => item.IsDeleted,
                        false);
                var replacement = await _ctx.Labels.ReplaceOneAsync(
                    session,
                    filter,
                    label,
                    cancellationToken: transactionCt);
                if (replacement.ModifiedCount != 1)
                    throw CasConflict(command, priorRevision, priorHash);

                var result = ToResult(label, me, receiptId);
                await _ctx.StatConfigCommandReceipts.InsertOneAsync(
                    session,
                    CreateReceipt(
                        receiptId,
                        command,
                        label,
                        result,
                        me.Id,
                        now),
                    cancellationToken: transactionCt);
                return result;
            },
            ct);
    }

    private async Task<LabelConfigResult> ExecuteWithDuplicateReplayAsync(
        string receiptId,
        string requestHash,
        Func<
            IClientSessionHandle,
            CancellationToken,
            Task<LabelConfigResult>> operation,
        CancellationToken ct)
    {
        try
        {
            return await _transactions.ExecuteAsync(operation, ct);
        }
        catch (MongoWriteException ex) when (
            ex.WriteError?.Category ==
            ServerErrorCategory.DuplicateKey)
        {
            var receipt = await _ctx.StatConfigCommandReceipts
                .Find(item => item.Id == receiptId)
                .FirstOrDefaultAsync(ct);
            if (receipt is not null)
                return RestoreReplay(receipt, requestHash);

            throw AppExceptionFactory.Create(
                AppErrorCode.LABEL_DUPLICATE_CODE,
                new { reason = "SCOPE_CODE_RESERVED" });
        }
    }

    private async Task<LabelConfigResult?> LoadReplayAsync(
        IClientSessionHandle session,
        string receiptId,
        string requestHash,
        CancellationToken ct)
    {
        var receipt = await _ctx.StatConfigCommandReceipts
            .Find(
                session,
                item => item.Id == receiptId)
            .FirstOrDefaultAsync(ct);
        return receipt is null
            ? null
            : RestoreReplay(receipt, requestHash);
    }

    private static LabelConfigResult RestoreReplay(
        StatConfigCommandReceipt receipt,
        string requestHash)
    {
        if (!string.Equals(
                receipt.RequestHash,
                requestHash,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_COMMAND_REPLAY_CONFLICT,
                new
                {
                    ownerKind = receipt.OwnerKind,
                    ownerId = receipt.OwnerId,
                    commandId = receipt.CommandId
                });
        }
        if (!string.Equals(
                receipt.ResponseHash,
                StatConfigCanonicalJson.HashUtf8(receipt.ResponseJson),
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new
                {
                    ownerKind = receipt.OwnerKind,
                    ownerId = receipt.OwnerId,
                    reason = "RECEIPT_RESPONSE_INTEGRITY"
                });
        }

        try
        {
            return JsonSerializer.Deserialize<LabelConfigResult>(
                       receipt.ResponseJson,
                       StatConfigCanonicalJson.StrictJsonOptions)
                   ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new
                {
                    ownerKind = receipt.OwnerKind,
                    ownerId = receipt.OwnerId,
                    reason = "RECEIPT_RESPONSE_SCHEMA"
                });
        }
    }

    private static StatConfigCommandReceipt CreateReceipt(
        string receiptId,
        NormalizedStatConfigCommand<LabelConfigPayload> command,
        LabelCatalogItem label,
        LabelConfigResult result,
        string actorUserId,
        DateTime now)
    {
        var responseJson = StatConfigCanonicalJson.Canonicalize(result);
        return NewReceipt(
            receiptId,
            StatConfigCommandKinds.UpsertLabel,
            command.CommandId,
            command.RequestHash,
            label,
            responseJson,
            actorUserId,
            now);
    }

    private static StatConfigCommandReceipt CreateReceipt(
        string receiptId,
        NormalizedStatConfigCommand<LabelTombstonePayload> command,
        LabelCatalogItem label,
        LabelConfigResult result,
        string actorUserId,
        DateTime now)
    {
        var responseJson = StatConfigCanonicalJson.Canonicalize(result);
        return NewReceipt(
            receiptId,
            StatConfigCommandKinds.TombstoneLabel,
            command.CommandId,
            command.RequestHash,
            label,
            responseJson,
            actorUserId,
            now);
    }

    private static StatConfigCommandReceipt NewReceipt(
        string receiptId,
        string commandKind,
        string commandId,
        string requestHash,
        LabelCatalogItem label,
        string responseJson,
        string actorUserId,
        DateTime now)
        => new()
        {
            Id = receiptId,
            OwnerKind = StatConfigOwnerKinds.Label,
            OwnerId = label.Id,
            CommandKind = commandKind,
            CommandId = commandId,
            RequestHash = requestHash,
            ResponseJson = responseJson,
            ResponseHash =
                StatConfigCanonicalJson.HashUtf8(responseJson),
            ResultConfigId = label.ConfigId!,
            ResultVersionId = label.VersionId!,
            ResultVersionNo = label.VersionNo,
            ResultRevision = label.Revision,
            ResultStatus = CurrentStatus(label),
            ResultConfigHash = label.ConfigHash!,
            ActorUserId = actorUserId,
            CreatedAtUtc = now
        };

    private static NormalizedStatConfigCommand<LabelConfigPayload> Rehash(
        NormalizedStatConfigCommand<LabelConfigPayload> command,
        LabelHashContent content)
        => command with
        {
            RequestHash = StatConfigCanonicalJson.HashObject(new
            {
                commandKind = StatConfigCommandKinds.UpsertLabel,
                expectedRevision = command.ExpectedRevision,
                expectedConfigHash = command.ExpectedConfigHash,
                payload = content
            })
        };

    private async Task<NormalizedLabelPayload> NormalizePayloadAsync(
        LabelConfigPayload payload,
        MeResponse me,
        CancellationToken ct)
    {
        var code = LabelTaxonomyContract.NormalizeCode(
            payload.Code,
            "$.payload.code");
        var scopeType = LabelTaxonomyContract.NormalizeScopeType(
            payload.ScopeType,
            "$.payload.scopeType");
        var scope = ResolveManagedScope(
            me,
            scopeType,
            payload.ScopeId);
        var usage = LabelTaxonomyContract.NormalizeUsage(
            payload.Usage,
            "$.payload.usage");
        var dataType = LabelTaxonomyContract.NormalizeDataType(
            payload.DataType,
            "$.payload.dataType");
        var valueSourceType =
            LabelTaxonomyContract.NormalizeValueSourceType(
                payload.ValueSourceType,
                "$.payload.valueSourceType");
        var name = RequireName(payload.Name);
        var description = OptionalText(
            payload.Description,
            500,
            "$.payload.description");
        var color = NormalizeColor(payload.Color);
        var groupCode = string.IsNullOrWhiteSpace(payload.GroupCode)
            ? null
            : LabelTaxonomyContract.NormalizeCode(
                payload.GroupCode,
                "$.payload.groupCode");
        var options = NormalizeOptions(
            payload.ValueOptions,
            valueSourceType);

        LabelEnumCatalog? catalog = null;
        if (valueSourceType == LabelValueSourceTypes.EnumCatalog)
        {
            if (string.IsNullOrWhiteSpace(
                    payload.ValueSourceCatalogId))
            {
                throw SchemaError(
                    "$.payload.valueSourceCatalogId",
                    "ENUM_CATALOG_REQUIRED");
            }
            catalog = await _enumCatalogs
                .EnsureVisibleActiveCatalogAsync(
                    payload.ValueSourceCatalogId,
                    ct);
        }
        else if (!string.IsNullOrWhiteSpace(
                     payload.ValueSourceCatalogId))
        {
            throw SchemaError(
                "$.payload.valueSourceCatalogId",
                "CATALOG_FOR_SOURCE_FORBIDDEN");
        }

        if (valueSourceType != LabelValueSourceTypes.None)
        {
            if (usage == LabelUsages.Classification)
            {
                throw SchemaError(
                    "$.payload.valueSourceType",
                    "CLASSIFICATION_SOURCE_FORBIDDEN");
            }
            if (dataType is not (
                    LabelDataTypes.ShortText or
                    LabelDataTypes.StringList))
            {
                throw SchemaError(
                    "$.payload.dataType",
                    "VALUE_SOURCE_TYPE_INCOMPATIBLE");
            }
        }

        var dependencyPins = catalog is null
            ? new List<string>()
            : new List<string> { CatalogPin(catalog) };
        var status = payload.IsActive
            ? StatConfigStatuses.Active
            : StatConfigStatuses.Inactive;
        var hashContent = new LabelHashContent(
            code,
            name,
            description,
            color,
            groupCode,
            usage,
            dataType,
            valueSourceType,
            options.Select(item =>
                    new LabelHashOption(item.Code, item.Label))
                .ToList(),
            catalog?.Id,
            catalog?.Code,
            catalog?.Name,
            scope.scopeType,
            scope.scopeId,
            payload.IsActive,
            status,
            dependencyPins);
        return new NormalizedLabelPayload(
            code,
            name,
            description,
            color,
            groupCode,
            usage,
            dataType,
            valueSourceType,
            options,
            catalog,
            scope.scopeType,
            scope.scopeId,
            payload.IsActive,
            dependencyPins,
            hashContent);
    }

    private async Task RevalidateDependencyAsync(
        IClientSessionHandle session,
        NormalizedLabelPayload payload,
        CancellationToken ct)
    {
        if (payload.ValueSourceCatalog is null)
            return;

        var catalog = await _ctx.LabelEnumCatalogs
            .Find(
                session,
                item =>
                    item.Id == payload.ValueSourceCatalog.Id &&
                    item.IsActive &&
                    !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (catalog is null ||
            !string.Equals(
                CatalogPin(catalog),
                payload.DependencyPins.Single(),
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new
                {
                    path = "$.payload.valueSourceCatalogId",
                    reason = "DEPENDENCY_PIN_CHANGED"
                });
        }
    }

    private async Task EnsureCodeReservedAsync(
        IClientSessionHandle session,
        string scopeType,
        string? scopeId,
        string code,
        string? exceptId,
        CancellationToken ct)
    {
        var filter =
            Builders<LabelCatalogItem>.Filter.Eq(
                item => item.ScopeType,
                scopeType) &
            Builders<LabelCatalogItem>.Filter.Eq(
                item => item.ScopeId,
                scopeId) &
            Builders<LabelCatalogItem>.Filter.Eq(
                item => item.Code,
                code);
        if (!string.IsNullOrWhiteSpace(exceptId))
        {
            filter &= Builders<LabelCatalogItem>.Filter.Ne(
                item => item.Id,
                exceptId);
        }
        var existing = await _ctx.Labels
            .Find(session, filter)
            .Limit(1)
            .FirstOrDefaultAsync(ct);
        if (existing is null)
            return;

        throw AppExceptionFactory.Create(
            existing.IsDeleted
                ? AppErrorCode.LABEL_CONFIG_TOMBSTONED
                : AppErrorCode.LABEL_DUPLICATE_CODE,
            new { code, scopeType, scopeId });
    }

    private async Task<LabelCatalogItem?> LoadManageableAsync(
        IClientSessionHandle session,
        string id,
        MeResponse me,
        bool includeDeleted,
        CancellationToken ct)
    {
        var filter =
            Builders<LabelCatalogItem>.Filter.Eq(
                item => item.Id,
                id) &
            BuildManageableFilter(me);
        if (!includeDeleted)
        {
            filter &= Builders<LabelCatalogItem>.Filter.Eq(
                item => item.IsDeleted,
                false);
        }
        return await _ctx.Labels
            .Find(session, filter)
            .FirstOrDefaultAsync(ct);
    }

    private static void EnsureImmutableIdentity(
        LabelCatalogItem label,
        NormalizedLabelPayload payload)
    {
        if (!string.Equals(
                label.Code,
                payload.Code,
                StringComparison.Ordinal))
        {
            throw SchemaError(
                "$.payload.code",
                "IMMUTABLE_CODE");
        }
        if (!string.Equals(
                label.ScopeType,
                payload.ScopeType,
                StringComparison.Ordinal) ||
            !string.Equals(
                label.ScopeId,
                payload.ScopeId,
                StringComparison.Ordinal))
        {
            throw SchemaError(
                "$.payload.scopeType",
                "IMMUTABLE_SCOPE");
        }
    }

    private static void EnsureCas<TPayload>(
        LabelCatalogItem label,
        NormalizedStatConfigCommand<TPayload> command)
    {
        if (label.Revision != command.ExpectedRevision ||
            !string.Equals(
                label.ConfigHash,
                command.ExpectedConfigHash,
                StringComparison.Ordinal))
        {
            throw CasConflict(
                command,
                label.Revision,
                label.ConfigHash ?? string.Empty);
        }
    }

    private static AppException CasConflict<TPayload>(
        NormalizedStatConfigCommand<TPayload> command,
        long actualRevision,
        string actualHash)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                expectedRevision = command.ExpectedRevision,
                expectedConfigHash = command.ExpectedConfigHash,
                actualRevision,
                actualConfigHash = actualHash
            });

    private static void EnsureCurrentIdentity(LabelCatalogItem label)
    {
        label.ConfigId ??= label.Id;
        label.VersionId ??= label.Id;
        label.VersionNo = Math.Max(1, label.VersionNo);
        label.Revision = Math.Max(1, label.Revision);
        label.DependencyPins ??= new();
        label.VersionSnapshots ??= new();
        label.ConfigHash ??= ComputeConfigHash(
            label,
            CurrentStatus(label));
    }

    private static void EnsureConfigHashIntegrity(
        LabelCatalogItem label)
    {
        EnsureCurrentIdentity(label);
        var recomputed = ComputeConfigHash(
            label,
            CurrentStatus(label));
        if (!string.Equals(
                label.ConfigHash,
                recomputed,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new
                {
                    ownerKind = StatConfigOwnerKinds.Label,
                    ownerId = label.Id,
                    reason = "CONFIG_HASH_INTEGRITY"
                });
        }
    }

    private static long CurrentRevision(LabelCatalogItem label)
    {
        EnsureCurrentIdentity(label);
        return label.Revision;
    }

    private static string CurrentHash(LabelCatalogItem label)
    {
        EnsureCurrentIdentity(label);
        return label.ConfigHash!;
    }

    private static string ComputeConfigHash(
        LabelCatalogItem label,
        string status)
        => StatConfigCanonicalJson.HashObject(new
        {
            labelId = label.Id,
            label.Code,
            label.Name,
            label.Description,
            label.Color,
            label.GroupCode,
            label.Usage,
            label.DataType,
            label.ValueSourceType,
            valueOptions = (label.ValueOptions ??
                            new List<LabelValueOption>())
                .Select(item => new
                {
                    item.Code,
                    item.Label
                })
                .ToList(),
            label.ValueSourceCatalogId,
            label.ValueSourceCatalogCode,
            label.ValueSourceCatalogName,
            label.ScopeType,
            label.ScopeId,
            label.IsActive,
            status,
            dependencyPins = label.DependencyPins ??
                             new List<string>()
        });

    private static LabelConfigVersionSnapshot CreateVersionSnapshot(
        LabelCatalogItem label,
        string status,
        string actorUserId,
        DateTime createdAtUtc)
        => new()
        {
            LabelId = label.Id,
            VersionId = label.VersionId!,
            PreviousVersionId = label.PreviousVersionId,
            VersionNo = label.VersionNo,
            Revision = label.Revision,
            Status = status,
            ConfigHash = label.ConfigHash!,
            Code = label.Code,
            Name = label.Name,
            Usage = label.Usage,
            DataType = label.DataType,
            ValueSourceType = label.ValueSourceType,
            ScopeType = label.ScopeType,
            ScopeId = label.ScopeId,
            IsActive = label.IsActive,
            DependencyPins = label.DependencyPins.ToList(),
            CreatedAtUtc = createdAtUtc,
            CreatedByUserId = actorUserId
        };

    private static string CurrentStatus(LabelCatalogItem label)
        => label.IsDeleted
            ? StatConfigStatuses.Tombstoned
            : label.IsActive
                ? StatConfigStatuses.Active
                : StatConfigStatuses.Inactive;

    private static LabelConfigResult ToResult(
        LabelCatalogItem label,
        MeResponse me,
        string? receiptId)
    {
        EnsureCurrentIdentity(label);
        var canManage = CanManage(me, label);
        var row = new LabelRow(
            label.Id,
            label.Code,
            label.Name,
            label.Description,
            label.Color,
            label.GroupCode,
            label.Usage.Trim().ToUpperInvariant(),
            label.DataType.Trim().ToUpperInvariant(),
            label.ValueSourceType.Trim().ToUpperInvariant(),
            (label.ValueOptions ?? new())
                .Select(item =>
                    new LabelValueOptionDto(item.Code, item.Label))
                .ToList(),
            label.ValueSourceCatalogId,
            label.ValueSourceCatalogCode,
            label.ValueSourceCatalogName,
            label.ScopeType,
            label.ScopeId,
            label.IsSystem,
            label.IsActive,
            canManage,
            label.CreatedAtUtc,
            label.UpdatedAtUtc);
        var versions = label.VersionSnapshots
            .OrderBy(item => item.VersionNo)
            .Select(item => new LabelConfigVersionSnapshotDto(
                item.LabelId,
                item.VersionId,
                item.PreviousVersionId,
                item.VersionNo,
                item.Revision,
                item.Status,
                item.ConfigHash,
                item.Code,
                item.Name,
                item.Usage,
                item.DataType,
                item.ValueSourceType,
                item.ScopeType,
                item.ScopeId,
                item.IsActive,
                item.DependencyPins,
                item.CreatedAtUtc))
            .ToList();
        return new LabelConfigResult(
            StatConfigOwnerKinds.Label,
            label.Id,
            label.ConfigId!,
            label.VersionId!,
            label.VersionNo,
            label.Revision,
            CurrentStatus(label),
            label.ConfigHash!,
            label.DependencyPins,
            new StatConfigPermissionSet(
                CanReadConfig: true,
                CanManageDraft: canManage,
                CanLockVersion: false,
                CanViewResult: false,
                CanReadDiagnostics: false),
            row,
            versions,
            receiptId);
    }

    private async Task<bool> HasBackReferenceAsync(
        IClientSessionHandle session,
        LabelCatalogItem label,
        CancellationToken ct)
    {
        var quotedCode = new BsonRegularExpression(
            Regex.Escape(
                string.Concat((char)34, label.Code, (char)34)),
            "i");
        var formFilter =
            Builders<DynamicFormTemplate>.Filter.Eq(
                item => item.IsDeleted,
                false) &
            Builders<DynamicFormTemplate>.Filter.Or(
                Builders<DynamicFormTemplate>.Filter.AnyEq(
                    item => item.TagCodes,
                    label.Code),
                Builders<DynamicFormTemplate>.Filter.Regex(
                    item => item.SectionsJson,
                    quotedCode),
                Builders<DynamicFormTemplate>.Filter.Regex(
                    item => item.FieldsJson,
                    quotedCode),
                Builders<DynamicFormTemplate>.Filter.Regex(
                    item => item.ExcelBlockJson,
                    quotedCode),
                Builders<DynamicFormTemplate>.Filter.Regex(
                    item => item.BlocksJson,
                    quotedCode));
        if (await _ctx.DynamicFormTemplates.CountDocumentsAsync(
                session,
                formFilter,
                new CountOptions { Limit = 1 },
                ct) > 0)
            return true;

        var labelIdPattern = new BsonRegularExpression(
            Regex.Escape(label.Id),
            string.Empty);
        var dependentReceipt =
            Builders<StatConfigCommandReceipt>.Filter.Ne(
                item => item.OwnerKind,
                StatConfigOwnerKinds.Label) &
            Builders<StatConfigCommandReceipt>.Filter.Regex(
                item => item.ResponseJson,
                labelIdPattern);
        return await _ctx.StatConfigCommandReceipts
                   .CountDocumentsAsync(
                       session,
                       dependentReceipt,
                       new CountOptions { Limit = 1 },
                       ct) > 0;
    }

    private static List<LabelValueOption> NormalizeOptions(
        IReadOnlyList<LabelValueOptionDto>? values,
        string valueSourceType)
    {
        if (valueSourceType != LabelValueSourceTypes.FixedEnum)
        {
            if (values is { Count: > 0 })
            {
                throw SchemaError(
                    "$.payload.valueOptions",
                    "OPTIONS_FOR_SOURCE_FORBIDDEN");
            }
            return new List<LabelValueOption>();
        }
        if (values is null || values.Count == 0)
        {
            throw SchemaError(
                "$.payload.valueOptions",
                "FIXED_ENUM_OPTIONS_REQUIRED");
        }
        if (values.Count > 100)
        {
            throw SchemaError(
                "$.payload.valueOptions",
                "FIXED_ENUM_OPTIONS_MAX_100");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<LabelValueOption>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var code = LabelTaxonomyContract.NormalizeCode(
                values[index].Code,
                $"$.payload.valueOptions[{index}].code");
            if (!seen.Add(code))
            {
                throw SchemaError(
                    $"$.payload.valueOptions[{index}].code",
                    "DUPLICATE_OPTION_CODE");
            }
            var label = values[index].Label?.Trim();
            if (string.IsNullOrWhiteSpace(label) ||
                label.Length > 120)
            {
                throw SchemaError(
                    $"$.payload.valueOptions[{index}].label",
                    "OPTION_LABEL_INVALID");
            }
            result.Add(new LabelValueOption
            {
                Code = code,
                Label = label
            });
        }
        return result;
    }

    private static string CatalogPin(LabelEnumCatalog catalog)
    {
        var hash = StatConfigCanonicalJson.HashObject(new
        {
            catalog.Id,
            catalog.Code,
            catalog.Name,
            catalog.ScopeType,
            catalog.ScopeId,
            catalog.OptionsRevision,
            activeOptions = (catalog.Options ?? new())
                .Where(item => item.IsActive)
                .OrderBy(item => item.Code, StringComparer.Ordinal)
                .Select(item => new
                {
                    code = item.Code.Trim().ToLowerInvariant(),
                    item.Label,
                    item.Order
                })
                .ToList()
        });
        return
            $"LABEL_ENUM:{catalog.Id}:{catalog.OptionsRevision}:{hash}";
    }

    private static (
        string scopeType,
        string? scopeId) ResolveManagedScope(
        MeResponse me,
        string requestedScopeType,
        string? requestedScopeId)
    {
        if (RoleGuard.IsSystemAdmin(me))
        {
            if (requestedScopeType == LabelScopeTypes.Global)
            {
                if (!string.IsNullOrWhiteSpace(requestedScopeId))
                {
                    throw SchemaError(
                        "$.payload.scopeId",
                        "GLOBAL_SCOPE_ID_FORBIDDEN");
                }
                return (LabelScopeTypes.Global, null);
            }
            return (
                requestedScopeType,
                RequireScopeObjectId(requestedScopeId));
        }

        if (RoleGuard.TryGetManagerUnit(
                me,
                out var managedUnitId))
        {
            if (requestedScopeType != LabelScopeTypes.Unit ||
                !string.Equals(
                    requestedScopeId?.Trim(),
                    managedUnitId,
                    StringComparison.Ordinal))
            {
                throw AppExceptionFactory.Forbidden(
                    AppErrorCode.LABEL_SCOPE_MISMATCH,
                    new { requestedScopeType });
            }
            return (LabelScopeTypes.Unit, managedUnitId);
        }

        if (RoleGuard.IsManagerLevel(me))
        {
            if (string.IsNullOrWhiteSpace(me.UnitId))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.LABEL_SCOPE_LEVEL_UNAVAILABLE);
            }
            if (requestedScopeType != LabelScopeTypes.Level ||
                !string.Equals(
                    requestedScopeId?.Trim(),
                    me.UnitId,
                    StringComparison.Ordinal))
            {
                throw AppExceptionFactory.Forbidden(
                    AppErrorCode.LABEL_SCOPE_MISMATCH,
                    new { requestedScopeType });
            }
            return (LabelScopeTypes.Level, me.UnitId);
        }

        throw AppExceptionFactory.Forbidden(
            AppErrorCode.LABEL_MANAGER_REQUIRED);
    }

    private static FilterDefinition<LabelCatalogItem>
        BuildVisibleFilter(MeResponse me)
    {
        var filter = Builders<LabelCatalogItem>.Filter;
        if (RoleGuard.IsSystemAdmin(me))
            return FilterDefinition<LabelCatalogItem>.Empty;

        var scopes = new List<FilterDefinition<LabelCatalogItem>>
        {
            filter.Eq(
                item => item.ScopeType,
                LabelScopeTypes.Global)
        };
        if (!string.IsNullOrWhiteSpace(me.UnitId))
        {
            scopes.Add(
                filter.Eq(
                    item => item.ScopeType,
                    LabelScopeTypes.Unit) &
                filter.Eq(
                    item => item.ScopeId,
                    me.UnitId));
        }
        if (RoleGuard.TryGetManagerUnit(
                me,
                out var managedUnitId))
        {
            scopes.Add(
                filter.Eq(
                    item => item.ScopeType,
                    LabelScopeTypes.Unit) &
                filter.Eq(
                    item => item.ScopeId,
                    managedUnitId));
        }
        if (RoleGuard.IsManagerLevel(me) &&
            !string.IsNullOrWhiteSpace(me.UnitId))
        {
            scopes.Add(
                filter.Eq(
                    item => item.ScopeType,
                    LabelScopeTypes.Level) &
                filter.Eq(
                    item => item.ScopeId,
                    me.UnitId));
        }
        return filter.Or(scopes);
    }

    private static FilterDefinition<LabelCatalogItem>
        BuildManageableFilter(MeResponse me)
    {
        var filter = Builders<LabelCatalogItem>.Filter;
        if (RoleGuard.IsSystemAdmin(me))
            return FilterDefinition<LabelCatalogItem>.Empty;
        if (RoleGuard.TryGetManagerUnit(
                me,
                out var managedUnitId))
        {
            return filter.Eq(
                       item => item.ScopeType,
                       LabelScopeTypes.Unit) &
                   filter.Eq(
                       item => item.ScopeId,
                       managedUnitId);
        }
        if (RoleGuard.IsManagerLevel(me) &&
            !string.IsNullOrWhiteSpace(me.UnitId))
        {
            return filter.Eq(
                       item => item.ScopeType,
                       LabelScopeTypes.Level) &
                   filter.Eq(
                       item => item.ScopeId,
                       me.UnitId);
        }
        return filter.Where(_ => false);
    }

    private static bool CanManage(
        MeResponse me,
        LabelCatalogItem label)
    {
        if (RoleGuard.IsSystemAdmin(me))
            return true;
        if (RoleGuard.TryGetManagerUnit(
                me,
                out var managedUnitId))
        {
            return label.ScopeType == LabelScopeTypes.Unit &&
                   label.ScopeId == managedUnitId;
        }
        return RoleGuard.IsManagerLevel(me) &&
               label.ScopeType == LabelScopeTypes.Level &&
               label.ScopeId == me.UnitId;
    }

    private static void RequireManager(MeResponse me)
    {
        if (!RoleGuard.IsSystemAdmin(me) &&
            !RoleGuard.IsManagerLevel(me) &&
            !RoleGuard.TryGetManagerUnit(me, out _))
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.LABEL_MANAGER_REQUIRED);
        }
    }

    private static string RequireObjectId(string? value)
    {
        var normalized = value?.Trim();
        if (!ObjectId.TryParse(normalized, out _))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.LABEL_CONFIG_ID_INVALID,
                new { path = "$.ownerId" });
        }
        return normalized!;
    }

    private static string RequireScopeObjectId(string? value)
    {
        var normalized = value?.Trim();
        if (!ObjectId.TryParse(normalized, out _))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.LABEL_SCOPE_ID_INVALID,
                new { path = "$.payload.scopeId" });
        }
        return normalized!;
    }

    private static string RequireName(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.LABEL_NAME_REQUIRED,
                new { path = "$.payload.name" });
        }
        if (normalized.Length > 120)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.LABEL_NAME_TOO_LONG,
                new { path = "$.payload.name", maxLength = 120 });
        }
        return normalized;
    }

    private static string? OptionalText(
        string? value,
        int maxLength,
        string path)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;
        if (normalized.Length > maxLength)
            throw SchemaError(path, $"MAX_LENGTH_{maxLength}");
        return normalized;
    }

    private static string? NormalizeColor(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;
        if (!HexColorRegex.IsMatch(normalized))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.LABEL_COLOR_INVALID,
                new { path = "$.payload.color" });
        }
        return normalized.ToUpperInvariant();
    }

    private static string DeterministicObjectId(string seed)
    {
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(seed)))
            .ToLowerInvariant();
        return digest[..24];
    }

    private static string ReceiptId(
        string ownerId,
        string commandId)
        => StatConfigCanonicalJson.HashUtf8(
            $"{StatConfigOwnerKinds.Label}\0{ownerId}\0{commandId}");

    private static AppException LabelNotFound(string id)
        => AppExceptionFactory.NotFound(
            AppErrorCode.LABEL_NOT_FOUND,
            new { ownerKind = StatConfigOwnerKinds.Label, ownerId = id });

    private static AppException SchemaError(
        string path,
        string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            new { path, reason });

    private sealed record NormalizedLabelPayload(
        string Code,
        string Name,
        string? Description,
        string? Color,
        string? GroupCode,
        string Usage,
        string DataType,
        string ValueSourceType,
        List<LabelValueOption> ValueOptions,
        LabelEnumCatalog? ValueSourceCatalog,
        string ScopeType,
        string? ScopeId,
        bool IsActive,
        List<string> DependencyPins,
        LabelHashContent HashContent);

    private sealed record LabelHashContent(
        string Code,
        string Name,
        string? Description,
        string? Color,
        string? GroupCode,
        string Usage,
        string DataType,
        string ValueSourceType,
        IReadOnlyList<LabelHashOption> ValueOptions,
        string? ValueSourceCatalogId,
        string? ValueSourceCatalogCode,
        string? ValueSourceCatalogName,
        string ScopeType,
        string? ScopeId,
        bool IsActive,
        string Status,
        IReadOnlyList<string> DependencyPins);

    private sealed record LabelHashOption(
        string Code,
        string Label);
}
