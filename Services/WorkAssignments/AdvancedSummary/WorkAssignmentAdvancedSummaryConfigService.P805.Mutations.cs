using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public sealed partial class WorkAssignmentAdvancedSummaryConfigService
{
    private async Task<P805ConfigState> P805ApplyPutAsync(
        IClientSessionHandle session,
        P805OwnerContext owner,
        P805ConfigState current,
        WorkAssignmentAdvancedSummaryConfigPayload payload,
        MeResponse me,
        string commandId,
        DateTime now,
        CancellationToken ct)
    {
        _ = commandId;
        if (!current.IsVirtual &&
            current.Status != StatConfigStatuses.Draft)
        {
            throw P805StateConflict(
                current,
                "ADVANCED_SUMMARY_CONFIG_VERSION_NOT_DRAFT");
        }

        var dependencyPins =
            await P805ResolveDependencyPinsAsync(
                session,
                owner,
                payload,
                ct);
        var nextRevision = checked(current.Revision + 1);
        var configHash = P805ComputeConfigHash(
            payload,
            dependencyPins);
        if (current.IsVirtual)
        {
            var entity = P805CreateDraftEntity(
                owner,
                current.ConfigId,
                current.VersionId,
                previousVersionId: null,
                versionNo: 1,
                revision: nextRevision,
                payload,
                dependencyPins,
                configHash,
                me.Id,
                now);
            await _ctx.WorkAssignmentAdvancedSummaryConfigs
                .InsertOneAsync(
                    session,
                    entity,
                    cancellationToken: ct);
        }
        else
        {
            var source = payload.SourceScope!;
            var update =
                Builders<WorkAssignmentAdvancedSummaryConfig>
                    .Update
                    .Set(
                        item => item.SectionTitle,
                        owner.Section.Title)
                    .Set(
                        item => item.SourceScopeMode,
                        source.Mode!)
                    .Set(
                        item => item.SourceFlowInstanceId,
                        source.FlowInstanceId)
                    .Set(
                        item => item.SourceFlowStepId,
                        source.FlowStepId)
                    .Set(
                        item => item.SourceFlowBranchId,
                        source.FlowBranchId)
                    .Set(
                        item => item.SourceFlowEffectiveStatus,
                        source.FlowEffectiveStatus)
                    .Set(
                        item => item.Revision,
                        nextRevision)
                    .Set(
                        item => item.DraftRevision,
                        checked((int)nextRevision))
                    .Set(
                        item => item.Status,
                        StatConfigStatuses.Draft)
                    .Set(
                        item => item.ConfigJson,
                        P805CanonicalPayload(payload))
                    .Set(
                        item => item.ConfigHash,
                        configHash)
                    .Set(
                        item => item.DependencyPins,
                        dependencyPins.ToList())
                    .Set(
                        item => item.ValidationReceiptJson,
                        null)
                    .Set(
                        item => item.ValidationReceiptHash,
                        null)
                    .Set(item => item.UpdatedAtUtc, now)
                    .Set(item => item.UpdatedByUserId, me.Id);
            var result =
                await _ctx.WorkAssignmentAdvancedSummaryConfigs
                    .UpdateOneAsync(
                        session,
                        P805BuildRowCasFilter(current.Entity!),
                        update,
                        cancellationToken: ct);
            P805RequireModified(
                result,
                current,
                "ADVANCED_SUMMARY_CONFIG_CONCURRENT_PUT");
        }
        return await P805LoadStateAsync(session, owner, ct);
    }

    private async Task<P805ConfigState> P805ApplyLockAsync(
        IClientSessionHandle session,
        P805OwnerContext owner,
        P805ConfigState current,
        WorkAssignmentAdvancedSummaryEmptyCommandPayload payload,
        MeResponse me,
        string commandId,
        DateTime now,
        CancellationToken ct)
    {
        _ = payload;
        if (current.IsVirtual ||
            current.Status != StatConfigStatuses.Draft ||
            current.Entity is null)
        {
            throw P805StateConflict(
                current,
                "ADVANCED_SUMMARY_CONFIG_DRAFT_NOT_PERSISTED");
        }
        await P805EnsureDependenciesCurrentAsync(
            session,
            owner,
            current.Payload,
            current.DependencyPins,
            ct);
        if (!string.Equals(
                P805ComputeConfigHash(
                    current.Payload,
                    current.DependencyPins),
                current.ConfigHash,
                StringComparison.Ordinal))
        {
            throw P805StateConflict(
                current,
                "ADVANCED_SUMMARY_CONFIG_HASH_STALE");
        }

        var nextRevision = checked(current.Revision + 1);
        var receipt = P805BuildValidationReceipt(
            P805OwnerId(owner),
            current.ConfigId,
            current.VersionId,
            current.VersionNo,
            nextRevision,
            current.ConfigHash,
            current.DependencyPins,
            current.Payload);

        var historical = await
            _ctx.WorkAssignmentAdvancedSummaryConfigs
                .Find(
                    session,
                    item =>
                        item.ConfigId == current.ConfigId &&
                        item.LockedAtUtc != null)
                .Limit(1)
                .FirstOrDefaultAsync(ct);
        var isFree = historical is null;
        var ownerUnitId = P805RequiredObjectId(
            me.UnitId,
            "$.actor.unitId",
            "OWNER_UNIT_ID_REQUIRED");
        var requestTokenId = receipt.ReceiptId;
        var consume = await _summaryTokens
            .ConsumeAdvancedConfigLockP8Async(
                session,
                current.Entity,
                ownerUnitId,
                isFree,
                requestTokenId,
                me.Id,
                ct);

        var priorLocked = current.Versions
            .SingleOrDefault(
                item =>
                    item.Status == StatConfigStatuses.Locked &&
                    item.Id != current.Entity.Id);
        if (priorLocked is not null)
        {
            await P805ArchiveLockedRowAsync(
                session,
                priorLocked,
                me.Id,
                now,
                "ADVANCED_SUMMARY_PREVIOUS_LOCK_ARCHIVE_CONFLICT",
                ct);
        }

        var receiptJson =
            StatConfigCanonicalJson.Canonicalize(receipt);
        var update =
            Builders<WorkAssignmentAdvancedSummaryConfig>
                .Update
                .Set(
                    item => item.Status,
                    StatConfigStatuses.Locked)
                .Set(item => item.Revision, nextRevision)
                .Set(
                    item => item.DraftRevision,
                    checked((int)nextRevision))
                .Set(item => item.LockedAtUtc, now)
                .Set(item => item.LockedByUserId, me.Id)
                .Set(item => item.LockTokenId, consume.LedgerId)
                .Set(
                    item => item.ValidationReceiptJson,
                    receiptJson)
                .Set(
                    item => item.ValidationReceiptHash,
                    StatConfigCanonicalJson.HashUtf8(
                        receiptJson))
                .Set(item => item.UpdatedAtUtc, now)
                .Set(item => item.UpdatedByUserId, me.Id);
        var result =
            await _ctx.WorkAssignmentAdvancedSummaryConfigs
                .UpdateOneAsync(
                    session,
                    P805BuildRowCasFilter(current.Entity),
                    update,
                    cancellationToken: ct);
        P805RequireModified(
            result,
            current,
            "ADVANCED_SUMMARY_CONFIG_CONCURRENT_LOCK");
        return await P805LoadStateAsync(session, owner, ct);
    }

    private async Task<P805ConfigState>
        P805ApplyNextDraftAsync(
            IClientSessionHandle session,
            P805OwnerContext owner,
            P805ConfigState current,
            WorkAssignmentAdvancedSummaryEmptyCommandPayload payload,
            MeResponse me,
            string commandId,
            DateTime now,
            CancellationToken ct)
    {
        _ = payload;
        _ = commandId;
        if (current.IsVirtual ||
            current.Status is not (
                StatConfigStatuses.Locked or
                StatConfigStatuses.Archived))
        {
            throw P805StateConflict(
                current,
                "ADVANCED_SUMMARY_CONFIG_VERSION_NOT_LOCKED_OR_ARCHIVED");
        }
        await P805EnsureDependenciesCurrentAsync(
            session,
            owner,
            current.Payload,
            current.DependencyPins,
            ct);

        var versionNo = checked(current.VersionNo + 1);
        var entity = P805CreateDraftEntity(
            owner,
            current.ConfigId,
            P805DeterministicId(
                P805OwnerId(owner),
                $"VERSION:{versionNo}"),
            current.VersionId,
            versionNo,
            revision: 0,
            current.Payload,
            current.DependencyPins,
            current.ConfigHash,
            me.Id,
            now);
        await _ctx.WorkAssignmentAdvancedSummaryConfigs
            .InsertOneAsync(
                session,
                entity,
                cancellationToken: ct);
        return await P805LoadStateAsync(session, owner, ct);
    }

    private async Task<P805ConfigState> P805ApplyArchiveAsync(
        IClientSessionHandle session,
        P805OwnerContext owner,
        P805ConfigState current,
        WorkAssignmentAdvancedSummaryEmptyCommandPayload payload,
        MeResponse me,
        string commandId,
        DateTime now,
        CancellationToken ct)
    {
        _ = payload;
        _ = commandId;
        if (current.IsVirtual ||
            current.Status != StatConfigStatuses.Locked ||
            current.Entity is null)
        {
            throw P805StateConflict(
                current,
                "ADVANCED_SUMMARY_CONFIG_VERSION_NOT_LOCKED");
        }
        await P805ArchiveLockedRowAsync(
            session,
            current.Entity,
            me.Id,
            now,
            "ADVANCED_SUMMARY_CONFIG_CONCURRENT_ARCHIVE",
            ct);
        return await P805LoadStateAsync(session, owner, ct);
    }

    private static WorkAssignmentAdvancedSummaryConfig
        P805CreateDraftEntity(
            P805OwnerContext owner,
            string configId,
            string versionId,
            string? previousVersionId,
            int versionNo,
            long revision,
            WorkAssignmentAdvancedSummaryConfigPayload payload,
            IReadOnlyList<string> dependencyPins,
            string configHash,
            string actorUserId,
            DateTime now)
    {
        var source = payload.SourceScope!;
        return new WorkAssignmentAdvancedSummaryConfig
        {
            Id = versionId,
            ConfigId = configId,
            PreviousVersionId = previousVersionId,
            WorkId = owner.Assignment.WorkId,
            AssignmentId = owner.Assignment.Id,
            DynamicFormTemplateId = owner.Template.Id,
            SectionId = owner.Section.SectionId,
            SectionTitle = owner.Section.Title,
            SourceScopeMode = source.Mode!,
            SourceFlowInstanceId = source.FlowInstanceId,
            SourceFlowStepId = source.FlowStepId,
            SourceFlowBranchId = source.FlowBranchId,
            SourceFlowEffectiveStatus =
                source.FlowEffectiveStatus,
            Status = StatConfigStatuses.Draft,
            VersionNo = versionNo,
            DraftRevision = checked((int)revision),
            Revision = revision,
            ConfigJson = P805CanonicalPayload(payload),
            ConfigHash = configHash,
            DependencyPins = dependencyPins.ToList(),
            ValidationReceiptJson = null,
            ValidationReceiptHash = null,
            LockedAtUtc = null,
            LockedByUserId = null,
            LockTokenId = null,
            ArchivedAtUtc = null,
            ArchivedByUserId = null,
            CreatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedAtUtc = now,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };
    }

    private async Task P805ArchiveLockedRowAsync(
        IClientSessionHandle session,
        WorkAssignmentAdvancedSummaryConfig row,
        string actorUserId,
        DateTime now,
        string conflictReason,
        CancellationToken ct)
    {
        var nextRevision = checked(row.Revision + 1);
        var update =
            Builders<WorkAssignmentAdvancedSummaryConfig>
                .Update
                .Set(
                    item => item.Status,
                    StatConfigStatuses.Archived)
                .Set(item => item.Revision, nextRevision)
                .Set(
                    item => item.DraftRevision,
                    checked((int)nextRevision))
                .Set(item => item.ArchivedAtUtc, now)
                .Set(item => item.ArchivedByUserId, actorUserId)
                .Set(item => item.UpdatedAtUtc, now)
                .Set(item => item.UpdatedByUserId, actorUserId);
        var result =
            await _ctx.WorkAssignmentAdvancedSummaryConfigs
                .UpdateOneAsync(
                    session,
                    P805BuildRowCasFilter(row),
                    update,
                    cancellationToken: ct);
        if (result.ModifiedCount != 1)
        {
            throw AppExceptionFactory.Create(
                tdtd_be.Common.Errors.AppErrorCode
                    .STAT_CONFIG_CAS_CONFLICT,
                new
                {
                    actualRevision = row.Revision,
                    actualConfigHash = row.ConfigHash,
                    reason = conflictReason
                });
        }
    }

    private static FilterDefinition<
        WorkAssignmentAdvancedSummaryConfig>
        P805BuildRowCasFilter(
            WorkAssignmentAdvancedSummaryConfig row)
    {
        var filter =
            Builders<WorkAssignmentAdvancedSummaryConfig>.Filter;
        return filter.Eq(item => item.Id, row.Id) &
               filter.Eq(item => item.ConfigId, row.ConfigId) &
               filter.Eq(item => item.Revision, row.Revision) &
               filter.Eq(item => item.ConfigHash, row.ConfigHash) &
               filter.Eq(item => item.Status, row.Status) &
               filter.Eq(item => item.IsDeleted, false);
    }

    private static void P805RequireModified(
        UpdateResult result,
        P805ConfigState current,
        string reason)
    {
        if (result.ModifiedCount != 1)
        {
            throw P805CasConflict(
                current,
                current.Revision,
                current.ConfigHash,
                reason);
        }
    }
}
