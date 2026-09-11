using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignments.BasicSummary;

/// <summary>
/// P8-04 configuration-only command path. This file deliberately has no
/// report reader, summary executor, snapshot, refresh or background-job call.
/// </summary>
public sealed partial class WorkAssignmentBasicSummaryService
{
    public async Task<WorkAssignmentBasicSummaryConfigReadback>
        PutP8ConfigAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            JsonElement body,
            CancellationToken ct)
    {
        using var isolation =
            StatConfigIsolationGuard.EnterConfigurationMutation(
                StatConfigOwnerKinds.BasicSummary);
        var prepared = await P804AuthorizeBeforeBodyAsync(
            assignmentId,
            dynamicFormTemplateId,
            ct);
        var command = NormalizeP804PutCommand(body);
        return await P804ExecuteCommandAsync(
            prepared.AssignmentId,
            prepared.TemplateId,
            prepared.Me,
            command,
            P804PutCommandKind,
            P804ApplyPutAsync,
            ct);
    }

    public async Task<WorkAssignmentBasicSummaryConfigReadback>
        LockP8ConfigAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            JsonElement body,
            CancellationToken ct)
    {
        using var isolation =
            StatConfigIsolationGuard.EnterConfigurationMutation(
                StatConfigOwnerKinds.BasicSummary);
        var prepared = await P804AuthorizeBeforeBodyAsync(
            assignmentId,
            dynamicFormTemplateId,
            ct);
        var command = NormalizeP804EmptyCommand(
            body,
            P804LockCommandKind);
        return await P804ExecuteCommandAsync(
            prepared.AssignmentId,
            prepared.TemplateId,
            prepared.Me,
            command,
            P804LockCommandKind,
            P804ApplyLockAsync,
            ct);
    }

    public async Task<WorkAssignmentBasicSummaryConfigReadback>
        CreateNextP8DraftAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            JsonElement body,
            CancellationToken ct)
    {
        using var isolation =
            StatConfigIsolationGuard.EnterConfigurationMutation(
                StatConfigOwnerKinds.BasicSummary);
        var prepared = await P804AuthorizeBeforeBodyAsync(
            assignmentId,
            dynamicFormTemplateId,
            ct);
        var command = NormalizeP804EmptyCommand(
            body,
            P804NextDraftCommandKind);
        return await P804ExecuteCommandAsync(
            prepared.AssignmentId,
            prepared.TemplateId,
            prepared.Me,
            command,
            P804NextDraftCommandKind,
            P804ApplyNextDraftAsync,
            ct);
    }

    private async Task<P804PreparedCommand>
        P804AuthorizeBeforeBodyAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            CancellationToken ct)
    {
        var me = _me.RequireMe();
        assignmentId = P804NormalizeAssignmentId(assignmentId);
        _ = await P804LoadAuthorizedAssignmentAsync(
            null,
            assignmentId,
            me,
            requireManage: true,
            ct);
        var templateId =
            P804NormalizeTemplateId(dynamicFormTemplateId);
        _ = await P804LoadTemplateAsync(null, templateId, ct);
        return new P804PreparedCommand(
            me,
            assignmentId,
            templateId);
    }

    private async Task<WorkAssignmentBasicSummaryConfigReadback>
        P804ExecuteCommandAsync<TPayload>(
            string assignmentId,
            string templateId,
            MeResponse me,
            NormalizedStatConfigCommand<TPayload> command,
            string commandKind,
            Func<
                IClientSessionHandle,
                WorkAssignment,
                DynamicFormTemplate,
                P804ConfigState,
                TPayload,
                MeResponse,
                CancellationToken,
                Task<P804ConfigState>> apply,
            CancellationToken ct)
    {
        var ownerId = P804OwnerId(assignmentId, templateId);
        var receiptId =
            P804ReceiptId(ownerId, command.CommandId);
        try
        {
            return await _statConfigTransactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    var assignment =
                        await P804LoadAuthorizedAssignmentAsync(
                            session,
                            assignmentId,
                            me,
                            requireManage: true,
                            transactionCt);
                    var template = await P804LoadTemplateAsync(
                        session,
                        templateId,
                        transactionCt);
                    var replay = await P804LoadReplayAsync(
                        session,
                        receiptId,
                        command.RequestHash,
                        transactionCt);
                    if (replay is not null)
                        return replay;

                    var current = await P804LoadStateAsync(
                        session,
                        assignment,
                        template,
                        transactionCt);
                    P804EnsureCas(current, command);
                    var next = await apply(
                        session,
                        assignment,
                        template,
                        current,
                        command.Payload,
                        me,
                        transactionCt);
                    var mutationNow =
                        P804UtcNowAtMillisecondPrecision();
                    next = await P804PersistStateAsync(
                        session,
                        assignment,
                        template,
                        current,
                        next,
                        me.Id,
                        mutationNow,
                        transactionCt);
                    var result = P804ToReadback(
                        assignment,
                        template,
                        next,
                        me,
                        receiptId);
                    await _ctx.StatConfigCommandReceipts
                        .InsertOneAsync(
                            session,
                            P804CreateReceipt(
                                receiptId,
                                ownerId,
                                commandKind,
                                command,
                                next,
                                result,
                                me.Id,
                                mutationNow),
                            cancellationToken: transactionCt);
                    return result;
                },
                ct);
        }
        catch (MongoWriteException ex) when (
            ex.WriteError?.Category ==
            ServerErrorCategory.DuplicateKey)
        {
            var receipt = await _ctx.StatConfigCommandReceipts
                .Find(item => item.Id == receiptId)
                .FirstOrDefaultAsync(ct);
            if (receipt is not null)
            {
                return P804RestoreReplay(
                    receipt,
                    command.RequestHash);
            }
            throw P804CasConflict(
                current: null,
                command,
                "BASIC_SUMMARY_CONFIG_CONCURRENT_WRITE");
        }
        catch (AppException ex) when (
            ex.Code == AppErrorCode.STAT_CONFIG_CAS_CONFLICT)
        {
            var receipt = await _ctx.StatConfigCommandReceipts
                .Find(item => item.Id == receiptId)
                .FirstOrDefaultAsync(ct);
            if (receipt is not null)
            {
                return P804RestoreReplay(
                    receipt,
                    command.RequestHash);
            }
            throw;
        }
    }

    private async Task<P804ConfigState> P804ApplyPutAsync(
        IClientSessionHandle session,
        WorkAssignment assignment,
        DynamicFormTemplate template,
        P804ConfigState current,
        WorkAssignmentBasicSummaryConfigPayload payload,
        MeResponse me,
        CancellationToken ct)
    {
        if (current.Status == StatConfigStatuses.Locked)
        {
            throw P804StateConflict(
                current,
                "BASIC_SUMMARY_CONFIG_VERSION_LOCKED");
        }
        var dependencyPins =
            await P804ResolveDependencyPinsAsync(
                session,
                template,
                payload,
                me,
                ct);
        return current with
        {
            Revision = checked(current.Revision + 1),
            Status = StatConfigStatuses.Draft,
            ConfigHash = P804ComputeConfigHash(
                payload,
                dependencyPins),
            DependencyPins = dependencyPins,
            Payload = payload,
            IsVirtual = false
        };
    }

    private async Task<P804ConfigState> P804ApplyLockAsync(
        IClientSessionHandle session,
        WorkAssignment assignment,
        DynamicFormTemplate template,
        P804ConfigState current,
        WorkAssignmentBasicSummaryEmptyCommandPayload payload,
        MeResponse me,
        CancellationToken ct)
    {
        if (current.IsVirtual)
        {
            throw P804StateConflict(
                current,
                "BASIC_SUMMARY_CONFIG_DRAFT_NOT_PERSISTED");
        }
        if (current.Status == StatConfigStatuses.Locked)
        {
            throw P804StateConflict(
                current,
                "BASIC_SUMMARY_CONFIG_VERSION_ALREADY_LOCKED");
        }
        await P804EnsureDependenciesCurrentAsync(
            session,
            template,
            current,
            me,
            ct);
        return current with
        {
            Revision = checked(current.Revision + 1),
            Status = StatConfigStatuses.Locked,
            IsVirtual = false
        };
    }

    private async Task<P804ConfigState>
        P804ApplyNextDraftAsync(
            IClientSessionHandle session,
            WorkAssignment assignment,
            DynamicFormTemplate template,
            P804ConfigState current,
            WorkAssignmentBasicSummaryEmptyCommandPayload payload,
            MeResponse me,
            CancellationToken ct)
    {
        if (current.IsVirtual ||
            current.Status != StatConfigStatuses.Locked)
        {
            throw P804StateConflict(
                current,
                "BASIC_SUMMARY_CONFIG_VERSION_NOT_LOCKED");
        }
        await P804EnsureDependenciesCurrentAsync(
            session,
            template,
            current,
            me,
            ct);
        return current with
        {
            VersionId = ObjectId.GenerateNewId().ToString(),
            PreviousVersionId = current.VersionId,
            VersionNo = checked(current.VersionNo + 1),
            Revision = 0,
            Status = StatConfigStatuses.Draft,
            IsVirtual = false
        };
    }

    private async Task P804EnsureDependenciesCurrentAsync(
        IClientSessionHandle session,
        DynamicFormTemplate template,
        P804ConfigState current,
        MeResponse me,
        CancellationToken ct)
    {
        IReadOnlyList<string> resolved;
        try
        {
            resolved = await P804ResolveDependencyPinsAsync(
                session,
                template,
                current.Payload,
                me,
                ct);
        }
        catch (AppException ex) when (
            ex.Code == AppErrorCode.STAT_CONFIG_SCHEMA_INVALID ||
            ex.Code == AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND)
        {
            throw P804StateConflict(
                current,
                "BASIC_SUMMARY_CONFIG_DEPENDENCY_STALE");
        }
        var resolvedHash = P804ComputeConfigHash(
            current.Payload,
            resolved);
        if (!resolved.SequenceEqual(
                current.DependencyPins,
                StringComparer.Ordinal) ||
            !string.Equals(
                resolvedHash,
                current.ConfigHash,
                StringComparison.Ordinal))
        {
            throw P804StateConflict(
                current,
                "BASIC_SUMMARY_CONFIG_DEPENDENCY_STALE");
        }
    }

    private async Task<P804ConfigState> P804PersistStateAsync(
        IClientSessionHandle session,
        WorkAssignment assignment,
        DynamicFormTemplate template,
        P804ConfigState current,
        P804ConfigState next,
        string actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        var entity = current.Entity ??
                     new WorkAssignmentBasicSummaryConfig
                     {
                         Id = current.ConfigId,
                         WorkId = assignment.WorkId,
                         AssignmentId = assignment.Id,
                         DynamicFormTemplateId = template.Id,
                         CreatedAtUtc = now,
                         CreatedByUserId = actorUserId,
                         IsActive = true,
                         IsDeleted = false
                     };
        entity.WorkId = assignment.WorkId;
        entity.AssignmentId = assignment.Id;
        entity.DynamicFormTemplateId = template.Id;
        entity.VersionId = next.VersionId;
        entity.PreviousVersionId = next.PreviousVersionId;
        entity.VersionNo = next.VersionNo;
        entity.Revision = next.Revision;
        entity.Status = next.Status;
        entity.ConfigHash = next.ConfigHash;
        entity.ConfigJson = P804CanonicalPayload(next.Payload);
        entity.DependencyPins = next.DependencyPins.ToList();
        entity.DefaultMethodsJson = "{}";
        entity.RulesJson = P804BuildLegacyRulesJson(next.Payload);
        entity.IsActive = true;
        entity.IsDeleted = false;
        entity.UpdatedAtUtc = now;
        entity.UpdatedByUserId = actorUserId;
        entity.LockedAtUtc =
            next.Status == StatConfigStatuses.Locked
                ? now
                : null;
        entity.LockedByUserId =
            next.Status == StatConfigStatuses.Locked
                ? actorUserId
                : null;

        var versions = (current.Versions ??
                        Array.Empty<
                            WorkAssignmentBasicSummaryConfigVersion>())
            .Select(P804CloneVersion)
            .ToList();
        var previousSnapshot = versions.SingleOrDefault(
            item => item.VersionNo == next.VersionNo);
        var snapshot = new WorkAssignmentBasicSummaryConfigVersion
        {
            VersionId = next.VersionId,
            PreviousVersionId = next.PreviousVersionId,
            VersionNo = next.VersionNo,
            Revision = next.Revision,
            Status = next.Status,
            ConfigHash = next.ConfigHash,
            ConfigJson = entity.ConfigJson,
            DependencyPins = next.DependencyPins.ToList(),
            CreatedAtUtc = previousSnapshot?.CreatedAtUtc ?? now,
            CreatedByUserId =
                previousSnapshot?.CreatedByUserId ?? actorUserId,
            LockedAtUtc =
                next.Status == StatConfigStatuses.Locked
                    ? now
                    : null,
            LockedByUserId =
                next.Status == StatConfigStatuses.Locked
                    ? actorUserId
                    : null
        };
        if (previousSnapshot is null)
            versions.Add(snapshot);
        else
            versions[versions.IndexOf(previousSnapshot)] = snapshot;
        versions = versions
            .OrderBy(item => item.VersionNo)
            .ToList();
        entity.Versions = versions;

        if (current.Entity is null)
        {
            await _ctx.WorkAssignmentBasicSummaryConfigs
                .InsertOneAsync(
                    session,
                    entity,
                    cancellationToken: ct);
        }
        else
        {
            var replacement =
                await _ctx.WorkAssignmentBasicSummaryConfigs
                    .ReplaceOneAsync(
                        session,
                        P804BuildEntityCasFilter(current),
                        entity,
                        cancellationToken: ct);
            if (replacement.ModifiedCount != 1)
            {
                throw P804CasConflict(
                    current,
                    expectedRevision: current.Revision,
                    expectedConfigHash: current.ConfigHash,
                    "BASIC_SUMMARY_CONFIG_CONCURRENT_WRITE");
            }
        }
        return next with
        {
            Entity = entity,
            ConfigId = entity.Id,
            Versions = versions
        };
    }

    private static FilterDefinition<WorkAssignmentBasicSummaryConfig>
        P804BuildEntityCasFilter(P804ConfigState current)
    {
        var filter =
            Builders<WorkAssignmentBasicSummaryConfig>.Filter;
        var result =
            filter.Eq(item => item.Id, current.ConfigId) &
            filter.Eq(item => item.IsActive, true) &
            filter.Eq(item => item.IsDeleted, false);
        if (current.IsVirtual)
        {
            return result &
                   filter.Or(
                       filter.Eq(item => item.ConfigHash, null),
                       filter.Eq(item => item.ConfigHash, ""));
        }
        return result &
               filter.Eq(item => item.Revision, current.Revision) &
               filter.Eq(
                   item => item.ConfigHash,
                   current.ConfigHash);
    }

    private static string P804BuildLegacyRulesJson(
        WorkAssignmentBasicSummaryConfigPayload payload)
    {
        var rules = payload.Targets!
            .Where(target =>
                target.ConceptKind ==
                WorkAssignmentBasicSummaryConfigContract.Field)
            .Select(target =>
                new WorkAssignmentBasicSummaryRuleDto
                {
                    TargetKind = "FIELD",
                    TargetKey = target.ConceptKey!,
                    Operation = target.Operation!
                })
            .ToList();
        return StatConfigCanonicalJson.Canonicalize(rules);
    }

    private static WorkAssignmentBasicSummaryConfigVersion
        P804CloneVersion(
            WorkAssignmentBasicSummaryConfigVersion source)
        => new()
        {
            VersionId = source.VersionId,
            PreviousVersionId = source.PreviousVersionId,
            VersionNo = source.VersionNo,
            Revision = source.Revision,
            Status = source.Status,
            ConfigHash = source.ConfigHash,
            ConfigJson = source.ConfigJson,
            DependencyPins = source.DependencyPins.ToList(),
            CreatedAtUtc = source.CreatedAtUtc,
            CreatedByUserId = source.CreatedByUserId,
            LockedAtUtc = source.LockedAtUtc,
            LockedByUserId = source.LockedByUserId
        };

    private async Task<WorkAssignmentBasicSummaryConfigReadback?>
        P804LoadReplayAsync(
            IClientSessionHandle session,
            string receiptId,
            string requestHash,
            CancellationToken ct)
    {
        var receipt = await _ctx.StatConfigCommandReceipts
            .Find(session, item => item.Id == receiptId)
            .FirstOrDefaultAsync(ct);
        return receipt is null
            ? null
            : P804RestoreReplay(receipt, requestHash);
    }

    private static WorkAssignmentBasicSummaryConfigReadback
        P804RestoreReplay(
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
                    commandId = receipt.CommandId,
                    reason =
                        "BASIC_SUMMARY_COMMAND_REPLAY_CHANGED"
                });
        }
        if (!string.Equals(
                receipt.ResponseHash,
                StatConfigCanonicalJson.HashUtf8(
                    receipt.ResponseJson),
                StringComparison.Ordinal))
        {
            throw P804Integrity(
                "BASIC_SUMMARY_RECEIPT_RESPONSE_HASH_MISMATCH");
        }
        try
        {
            return JsonSerializer.Deserialize<
                       WorkAssignmentBasicSummaryConfigReadback>(
                       receipt.ResponseJson,
                       StatConfigCanonicalJson.StrictJsonOptions) ??
                   throw new JsonException();
        }
        catch (JsonException)
        {
            throw P804Integrity(
                "BASIC_SUMMARY_RECEIPT_RESPONSE_SCHEMA_INVALID");
        }
    }

    private static StatConfigCommandReceipt P804CreateReceipt<TPayload>(
        string receiptId,
        string ownerId,
        string commandKind,
        NormalizedStatConfigCommand<TPayload> command,
        P804ConfigState state,
        WorkAssignmentBasicSummaryConfigReadback response,
        string actorUserId,
        DateTime now)
    {
        var responseJson =
            StatConfigCanonicalJson.Canonicalize(response);
        return new StatConfigCommandReceipt
        {
            Id = receiptId,
            OwnerKind = StatConfigOwnerKinds.BasicSummary,
            OwnerId = ownerId,
            CommandKind = commandKind,
            CommandId = command.CommandId,
            RequestHash = command.RequestHash,
            ResponseJson = responseJson,
            ResponseHash =
                StatConfigCanonicalJson.HashUtf8(responseJson),
            ResultConfigId = state.ConfigId,
            ResultVersionId = state.VersionId,
            ResultVersionNo = state.VersionNo,
            ResultRevision = state.Revision,
            ResultStatus = state.Status,
            ResultConfigHash = state.ConfigHash,
            ActorUserId = actorUserId,
            CreatedAtUtc = now
        };
    }

    private static void P804EnsureCas<TPayload>(
        P804ConfigState current,
        NormalizedStatConfigCommand<TPayload> command)
    {
        if (current.Revision != command.ExpectedRevision ||
            !string.Equals(
                current.ConfigHash,
                command.ExpectedConfigHash,
                StringComparison.Ordinal))
        {
            throw P804CasConflict(
                current,
                command,
                "BASIC_SUMMARY_CONFIG_CAS_MISMATCH");
        }
    }

    private static AppException P804CasConflict<TPayload>(
        P804ConfigState? current,
        NormalizedStatConfigCommand<TPayload> command,
        string reason)
        => P804CasConflict(
            current,
            command.ExpectedRevision,
            command.ExpectedConfigHash,
            reason);

    private static AppException P804CasConflict(
        P804ConfigState? current,
        long expectedRevision,
        string expectedConfigHash,
        string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                expectedRevision,
                expectedConfigHash,
                actualRevision = current?.Revision,
                actualConfigHash = current?.ConfigHash,
                reason
            });

    private static AppException P804StateConflict(
        P804ConfigState current,
        string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                actualRevision = current.Revision,
                actualConfigHash = current.ConfigHash,
                actualStatus = current.Status,
                reason
            });

    internal static string P804ReceiptId(
        string ownerId,
        string commandId)
        => StatConfigCanonicalJson.HashUtf8(
            $"{StatConfigOwnerKinds.BasicSummary}\0" +
            $"{ownerId}\0{commandId}");

    private static DateTime P804UtcNowAtMillisecondPrecision()
    {
        var now = DateTime.UtcNow;
        return new DateTime(
            now.Ticks - now.Ticks %
            TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
    }

    private sealed record P804PreparedCommand(
        MeResponse Me,
        string AssignmentId,
        string TemplateId);
}
