using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.SummaryTokens;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignments.SummaryTokens;

public sealed partial class WorkSummaryTokenService
{
    private const string PoolHashSchema =
        "P8-WORK-SUMMARY-TOKEN-POOL-1";

    private static readonly Regex P8CommandIdRegex = new(
        "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex P8Sha256Regex = new(
        "^[a-f0-9]{64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<WorkSummaryTokenGrantResponse> GrantP8Async(
        string ownerUnitId,
        StatConfigMutationEnvelope<WorkSummaryTokenGrantP8Payload> request,
        MeResponse issuer,
        CancellationToken ct)
    {
        EnsureActor(issuer);
        if (!CanGrantExtraQuota(issuer))
            throw GrantForbidden(
                "adminRequiredForExtraGrant",
                issuer.Id,
                null);

        ownerUnitId = RequireCanonicalObjectId(
            ownerUnitId,
            "$.ownerUnitId");
        var command = StatConfigCanonicalJson.NormalizeCommand(
            request,
            StatConfigCommandKinds.GrantWorkSummaryTokenQuota);
        var units = command.Payload.Units;
        if (units <= 0 || units > MaxGrantUnits)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_SUMMARY_TOKEN_GRANT_UNITS_INVALID,
                new
                {
                    path = "$.payload.units",
                    units,
                    min = 1,
                    max = MaxGrantUnits
                });
        }

        var reason = NormalizeReason(
            command.Payload.Reason,
            "ADMIN_GRANT");
        command = RehashGrantCommand(command, ownerUnitId, units, reason);
        var tokenKind = WorkSummaryTokenKinds.AdvancedSummaryConfigLock;
        var commandReceiptId = ReceiptId(
            ownerUnitId,
            command.CommandId);

        try
        {
            return await _transactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    var replay = await FindRequestEntryAsync(
                        session,
                        ownerUnitId,
                        tokenKind,
                        commandReceiptId,
                        transactionCt);
                    if (replay is not null)
                    {
                        return await RestoreResponseAsync<WorkSummaryTokenGrantResponse>(
                            session,
                            replay,
                            command.RequestHash,
                            WorkSummaryTokenDirections.Grant,
                            StatConfigCommandKinds.GrantWorkSummaryTokenQuota,
                            transactionCt);
                    }

                    await LoadUnitRequiredP8Async(
                        session,
                        ownerUnitId,
                        transactionCt);

                    var now = DateTime.UtcNow;
                    var monthKey = ToMonthKey(now);
                    var state = await LoadOrBuildPoolAsync(
                        session,
                        ownerUnitId,
                        monthKey,
                        tokenKind,
                        issuer.Id,
                        transactionCt);
                    EnsureExternalCas(
                        state,
                        command.ExpectedRevision,
                        command.ExpectedConfigHash);

                    var ledgerId = DeterministicObjectId(
                        $"ENTRY\0{ownerUnitId}\0{tokenKind}\0{command.CommandId}");
                    var next = NextPool(
                        state.Pool,
                        issuer.Id,
                        ledgerId,
                        grantedUnits: checked(state.Pool.GrantedUnits + units),
                        usedUnits: state.Pool.UsedUnits,
                        now);
                    var receiptId = commandReceiptId;
                    var quota = ToQuota(next);
                    var result = new WorkSummaryTokenGrantResponse
                    {
                        LedgerId = ledgerId,
                        CommandReceiptId = receiptId,
                        OwnerUnitId = ownerUnitId,
                        IssuerUserId = issuer.Id,
                        TokenKind = tokenKind,
                        PeriodMonthKey = monthKey,
                        Units = units,
                        PoolRevision = next.Revision,
                        PoolHash = next.PoolHash!,
                        Quota = quota,
                        CreatedAtUtc = now
                    };
                    var entry = NewEntry(
                        ledgerId,
                        state,
                        next,
                        commandReceiptId,
                        command.RequestHash,
                        receiptId,
                        issuer.Id,
                        WorkSummaryTokenDirections.Grant,
                        units,
                        reason,
                        now);

                    await PersistMutationAsync(
                        session,
                        state,
                        next,
                        entry,
                        NewReceipt(
                            receiptId,
                            StatConfigCommandKinds.GrantWorkSummaryTokenQuota,
                            command.CommandId,
                            command.RequestHash,
                            next,
                            entry,
                            result,
                            issuer.Id,
                            now),
                        transactionCt);
                    return result;
                },
                ct);
        }
        catch (MongoWriteException ex) when (
            ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var replay = await RestoreOutsideTransactionAsync<WorkSummaryTokenGrantResponse>(
                ownerUnitId,
                tokenKind,
                commandReceiptId,
                command.RequestHash,
                WorkSummaryTokenDirections.Grant,
                StatConfigCommandKinds.GrantWorkSummaryTokenQuota,
                ct);
            if (replay is not null)
                return replay;
            throw ReplayConflict(
                ownerUnitId,
                command.CommandId,
                "DUPLICATE_LEDGER_IDENTITY");
        }
    }

    public async Task<WorkSummaryTokenCompensationResponse> CompensateP8Async(
        string ledgerId,
        StatConfigMutationEnvelope<WorkSummaryTokenCompensationP8Payload> request,
        MeResponse actor,
        CancellationToken ct)
    {
        EnsureActor(actor);
        if (!CanGrantExtraQuota(actor))
            throw GrantForbidden(
                "adminRequiredForCompensation",
                actor.Id,
                null);

        ledgerId = RequireCanonicalObjectId(
            ledgerId,
            "$.compensatedLedgerId");
        var command = StatConfigCanonicalJson.NormalizeCommand(
            request,
            StatConfigCommandKinds.CompensateAdvancedSummaryConfigLockToken);
        var reason = NormalizeReason(
            command.Payload.Reason,
            "SYSTEM_FAILURE_COMPENSATION");
        command = RehashCompensationCommand(
            command,
            ledgerId,
            reason);

        try
        {
            return await _transactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    var source = await _ctx.WorkSummaryTokenLedgers
                        .Find(
                            session,
                            x => x.Id == ledgerId &&
                                 !x.IsDeleted &&
                                 x.RecordKind !=
                                 WorkSummaryTokenLedgerRecordKinds.Pool)
                        .FirstOrDefaultAsync(transactionCt)
                        ?? throw AppExceptionFactory.NotFound(
                            AppErrorCode.COMMON_NOT_FOUND,
                            new { compensatedLedgerId = ledgerId });

                    EnsureCompensable(source);
                    var committedOwner = await _ctx
                        .WorkAssignmentAdvancedSummaryConfigs
                        .Find(
                            session,
                            x => x.Id == source.ConfigId &&
                                 x.LockTokenId == source.Id &&
                                 x.ConfigHash == source.ConfigHash &&
                                 x.VersionNo == source.ConfigVersionNo &&
                                 x.AssignmentId == source.WorkAssignmentId &&
                                 x.DynamicFormTemplateId ==
                                 source.DynamicFormTemplateId &&
                                 x.SectionId == source.SectionId &&
                                 (x.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Locked ||
                                  x.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Archived) &&
                                 !x.IsDeleted)
                        .FirstOrDefaultAsync(transactionCt);
                    if (committedOwner is not null)
                        throw TokenEntryNotOrphan(source);

                    var ownerUnitId = RequireCanonicalObjectId(
                        source.OwnerUnitId,
                        "$.source.ownerUnitId");
                    var tokenKind = source.TokenKind;
                    var commandReceiptId = ReceiptId(
                        ownerUnitId,
                        command.CommandId);

                    var replay = await FindRequestEntryAsync(
                        session,
                        ownerUnitId,
                        tokenKind,
                        commandReceiptId,
                        transactionCt);
                    if (replay is not null)
                    {
                        return await RestoreResponseAsync<WorkSummaryTokenCompensationResponse>(
                            session,
                            replay,
                            command.RequestHash,
                            WorkSummaryTokenDirections.Compensate,
                            StatConfigCommandKinds.CompensateAdvancedSummaryConfigLockToken,
                            transactionCt);
                    }

                    var alreadyCompensated = await FindCompensationAsync(
                        session,
                        ledgerId,
                        transactionCt);
                    if (alreadyCompensated is not null)
                    {
                        if (string.Equals(
                                alreadyCompensated.RequestHash,
                                command.RequestHash,
                                StringComparison.Ordinal))
                        {
                            return await RestoreResponseAsync<WorkSummaryTokenCompensationResponse>(
                                session,
                                alreadyCompensated,
                                command.RequestHash,
                                WorkSummaryTokenDirections.Compensate,
                                StatConfigCommandKinds.CompensateAdvancedSummaryConfigLockToken,
                                transactionCt);
                        }

                        throw ReplayConflict(
                            ownerUnitId,
                            command.CommandId,
                            "LEDGER_ALREADY_COMPENSATED");
                    }

                    await LoadUnitRequiredP8Async(
                        session,
                        ownerUnitId,
                        transactionCt);
                    var now = DateTime.UtcNow;
                    var state = await LoadOrBuildPoolAsync(
                        session,
                        ownerUnitId,
                        source.PeriodMonthKey,
                        tokenKind,
                        actor.Id,
                        transactionCt);
                    EnsureExternalCas(
                        state,
                        command.ExpectedRevision,
                        command.ExpectedConfigHash);
                    if (state.Pool.UsedUnits < source.Units)
                    {
                        throw PoolIntegrityConflict(
                            state.Pool,
                            "COMPENSATION_WOULD_UNDERFLOW");
                    }

                    var compensationLedgerId = DeterministicObjectId(
                        $"COMPENSATION\0{ledgerId}");
                    var next = NextPool(
                        state.Pool,
                        actor.Id,
                        compensationLedgerId,
                        grantedUnits: state.Pool.GrantedUnits,
                        usedUnits: state.Pool.UsedUnits - source.Units,
                        now);
                    var receiptId = commandReceiptId;
                    var result = new WorkSummaryTokenCompensationResponse
                    {
                        CompensationLedgerId = compensationLedgerId,
                        CommandReceiptId = receiptId,
                        CompensatedLedgerId = ledgerId,
                        OwnerUnitId = ownerUnitId,
                        TokenKind = tokenKind,
                        PeriodMonthKey = source.PeriodMonthKey,
                        Units = source.Units,
                        PoolRevision = next.Revision,
                        PoolHash = next.PoolHash!,
                        Quota = ToQuota(next),
                        CreatedAtUtc = now
                    };
                    var entry = NewEntry(
                        compensationLedgerId,
                        state,
                        next,
                        commandReceiptId,
                        command.RequestHash,
                        receiptId,
                        actor.Id,
                        WorkSummaryTokenDirections.Compensate,
                        source.Units,
                        reason,
                        now);
                    entry.CompensatesLedgerId = ledgerId;

                    await PersistMutationAsync(
                        session,
                        state,
                        next,
                        entry,
                        NewReceipt(
                            receiptId,
                            StatConfigCommandKinds.CompensateAdvancedSummaryConfigLockToken,
                            command.CommandId,
                            command.RequestHash,
                            next,
                            entry,
                            result,
                            actor.Id,
                            now),
                        transactionCt);
                    return result;
                },
                ct);
        }
        catch (MongoWriteException ex) when (
            ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await _ctx.WorkSummaryTokenLedgers
                .Find(x => x.CompensatesLedgerId == ledgerId &&
                           !x.IsDeleted &&
                           x.RecordKind ==
                           WorkSummaryTokenLedgerRecordKinds.Entry)
                .FirstOrDefaultAsync(ct);
            if (existing is not null &&
                string.Equals(
                    existing.RequestHash,
                    command.RequestHash,
                    StringComparison.Ordinal))
            {
                return await RestoreResponseOutsideSessionAsync<WorkSummaryTokenCompensationResponse>(
                    existing,
                    command.RequestHash,
                    WorkSummaryTokenDirections.Compensate,
                    StatConfigCommandKinds.CompensateAdvancedSummaryConfigLockToken,
                    ct);
            }

            throw ReplayConflict(
                ledgerId,
                command.CommandId,
                "LEDGER_ALREADY_COMPENSATED");
        }
    }

    public async Task<WorkSummaryTokenConsumeResult>
        ConsumeAdvancedConfigLockP8Async(
            IClientSessionHandle session,
            WorkAssignmentAdvancedSummaryConfig config,
            string ownerUnitId,
            bool isFree,
            string requestTokenId,
            string actorUserId,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(config);
        if (!session.IsInTransaction)
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_TRANSACTION_UNSUPPORTED,
                new
                {
                    reason = "P8_TOKEN_CONSUME_REQUIRES_CALLER_TRANSACTION"
                });
        }

        ownerUnitId = RequireCanonicalObjectId(
            ownerUnitId,
            "$.ownerUnitId");
        actorUserId = RequireCanonicalObjectId(
            actorUserId,
            "$.actorUserId");
        requestTokenId = RequireCommandId(requestTokenId);
        ValidateAdvancedConfigIdentity(config);
        await EnsureActorOwnsUnitAsync(
            session,
            actorUserId,
            ownerUnitId,
            ct);

        var tokenKind = WorkSummaryTokenKinds.AdvancedSummaryConfigLock;
        var requestHash = StatConfigCanonicalJson.HashObject(new
        {
            commandKind =
                StatConfigCommandKinds.ConsumeAdvancedSummaryConfigLockToken,
            requestTokenId,
            ownerUnitId,
            isFree,
            configId = config.Id,
            configVersionNo = config.VersionNo,
            configHash = config.ConfigHash,
            config.WorkId,
            config.AssignmentId,
            config.DynamicFormTemplateId,
            config.SectionId
        });

        var replay = await FindRequestEntryAsync(
            session,
            ownerUnitId,
            tokenKind,
            requestTokenId,
            ct);
        if (replay is not null)
        {
            return await RestoreResponseAsync<WorkSummaryTokenConsumeResult>(
                session,
                replay,
                requestHash,
                isFree
                    ? WorkSummaryTokenDirections.Free
                    : WorkSummaryTokenDirections.Consume,
                StatConfigCommandKinds.ConsumeAdvancedSummaryConfigLockToken,
                ct);
        }

        var now = DateTime.UtcNow;
        var monthKey = ToMonthKey(now);
        var state = await LoadOrBuildPoolAsync(
            session,
            ownerUnitId,
            monthKey,
            tokenKind,
            actorUserId,
            ct);
        var units = isFree ? 0 : 1;
        var usedBefore = state.Pool.UsedUnits;
        if (WouldExceedQuota(
                usedBefore,
                units,
                state.Pool.MonthlyQuota))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.WORK_SUMMARY_TOKEN_QUOTA_EXCEEDED,
                new
                {
                    ownerUnitId,
                    periodMonthKey = monthKey,
                    tokenKind,
                    monthlyQuota = state.Pool.MonthlyQuota,
                    baseMonthlyQuota = state.Pool.BaseMonthlyQuota,
                    grantedUnits = state.Pool.GrantedUnits,
                    usedBefore,
                    requestedUnits = units,
                    configId = config.Id,
                    configVersionNo = config.VersionNo
                });
        }

        var ledgerId = DeterministicObjectId(
            $"ENTRY\0{ownerUnitId}\0{tokenKind}\0{requestTokenId}");
        var next = NextPool(
            state.Pool,
            actorUserId,
            ledgerId,
            grantedUnits: state.Pool.GrantedUnits,
            usedUnits: checked(usedBefore + units),
            now);
        var receiptId = ReceiptId(ownerUnitId, requestTokenId);
        var result = new WorkSummaryTokenConsumeResult(
            ledgerId,
            receiptId,
            next.Id,
            ownerUnitId,
            tokenKind,
            monthKey,
            units,
            next.BaseMonthlyQuota,
            next.GrantedUnits,
            next.MonthlyQuota,
            usedBefore,
            next.UsedUnits,
            next.Revision,
            next.PoolHash!,
            isFree);
        var entry = NewEntry(
            ledgerId,
            state,
            next,
            requestTokenId,
            requestHash,
            receiptId,
            actorUserId,
            isFree
                ? WorkSummaryTokenDirections.Free
                : WorkSummaryTokenDirections.Consume,
            units,
            isFree
                ? "INITIAL_ADVANCED_SUMMARY_CONFIG_LOCK"
                : "CHANGE_ADVANCED_SUMMARY_CONFIG_LOCK",
            now);
        entry.OwnerUserId = actorUserId;
        entry.WorkId = config.WorkId;
        entry.WorkAssignmentId = config.AssignmentId;
        entry.DynamicFormTemplateId = config.DynamicFormTemplateId;
        entry.SectionId = config.SectionId;
        entry.ConfigId = config.Id;
        entry.ConfigVersionNo = config.VersionNo;
        entry.ConfigHash = config.ConfigHash;

        await PersistMutationAsync(
            session,
            state,
            next,
            entry,
            NewReceipt(
                receiptId,
                StatConfigCommandKinds.ConsumeAdvancedSummaryConfigLockToken,
                requestTokenId,
                requestHash,
                next,
                entry,
                result,
                actorUserId,
                now),
            ct);
        return result;
    }

    private async Task<WorkSummaryTokenQuotaResponse> BuildQuotaP8ReadAsync(
        string ownerUnitId,
        string tokenKind,
        string monthKey,
        CancellationToken ct)
    {
        var poolId = PoolId(ownerUnitId, monthKey, tokenKind);
        var pool = await _ctx.WorkSummaryTokenLedgers
            .Find(x => x.Id == poolId &&
                       !x.IsDeleted &&
                       x.RecordKind ==
                       WorkSummaryTokenLedgerRecordKinds.Pool)
            .FirstOrDefaultAsync(ct);
        if (pool is not null)
        {
            EnsurePoolIntegrity(
                pool,
                ownerUnitId,
                monthKey,
                tokenKind);
            return ToQuota(pool);
        }

        var totals = await ReadLegacyTotalsAsync(
            ownerUnitId,
            monthKey,
            tokenKind,
            ct);
        var baseQuota = await CountActiveUsersInUnitAsync(
            ownerUnitId,
            ct);
        var monthlyQuota = CalculateAllowance(
            baseQuota,
            totals.GrantedUnits);
        return new WorkSummaryTokenQuotaResponse
        {
            PoolId = poolId,
            Revision = 0,
            PoolHash = StatConfigCanonicalJson.EmptyConfigHash,
            OwnerUnitId = ownerUnitId,
            TokenKind = tokenKind,
            PeriodMonthKey = monthKey,
            BaseMonthlyQuota = baseQuota,
            GrantedUnits = totals.GrantedUnits,
            UsedUnits = totals.UsedUnits,
            MonthlyQuota = monthlyQuota,
            RemainingUnits = Math.Max(0, monthlyQuota - totals.UsedUnits)
        };
    }

    private async Task<PoolLoadState> LoadOrBuildPoolAsync(
        IClientSessionHandle session,
        string ownerUnitId,
        string monthKey,
        string tokenKind,
        string actorUserId,
        CancellationToken ct)
    {
        var poolId = PoolId(ownerUnitId, monthKey, tokenKind);
        var pool = await _ctx.WorkSummaryTokenLedgers
            .Find(
                session,
                x => x.Id == poolId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (pool is not null)
        {
            EnsurePoolIntegrity(
                pool,
                ownerUnitId,
                monthKey,
                tokenKind);
            return new PoolLoadState(
                pool,
                IsPersisted: true,
                CasHash: pool.PoolHash!);
        }

        var totals = await ReadLegacyTotalsAsync(
            session,
            ownerUnitId,
            monthKey,
            tokenKind,
            ct);
        var activeUsers = await _ctx.Users.CountDocumentsAsync(
            session,
            x => x.UnitId == ownerUnitId && !x.IsDeleted,
            cancellationToken: ct);
        var now = DateTime.UtcNow;
        pool = new WorkSummaryTokenLedger
        {
            Id = poolId,
            RecordKind = WorkSummaryTokenLedgerRecordKinds.Pool,
            OwnerUnitId = ownerUnitId,
            ActorUserId = actorUserId,
            TokenKind = tokenKind,
            Direction = WorkSummaryTokenDirections.Pool,
            Units = 0,
            BaseMonthlyQuota = CalculateBaseQuotaFromActiveUsers(
                checked((int)activeUsers)),
            GrantedUnits = totals.GrantedUnits,
            UsedUnits = totals.UsedUnits,
            PeriodMonthKey = monthKey,
            Revision = 0,
            Reason = "MONTHLY_QUOTA_POOL",
            Outcome = WorkSummaryTokenOutcomes.Success,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };
        pool.MonthlyQuota = CalculateAllowance(
            pool.BaseMonthlyQuota,
            pool.GrantedUnits);
        pool.PoolHash = ComputePoolHash(pool);
        return new PoolLoadState(
            pool,
            IsPersisted: false,
            CasHash: StatConfigCanonicalJson.EmptyConfigHash);
    }

    private static WorkSummaryTokenLedger NextPool(
        WorkSummaryTokenLedger current,
        string actorUserId,
        string ledgerId,
        int grantedUnits,
        int usedUnits,
        DateTime now)
    {
        var next = new WorkSummaryTokenLedger
        {
            Id = current.Id,
            RecordKind = WorkSummaryTokenLedgerRecordKinds.Pool,
            OwnerUnitId = current.OwnerUnitId,
            ActorUserId = actorUserId,
            TokenKind = current.TokenKind,
            Direction = WorkSummaryTokenDirections.Pool,
            Units = 0,
            BaseMonthlyQuota = current.BaseMonthlyQuota,
            GrantedUnits = grantedUnits,
            UsedUnits = usedUnits,
            PeriodMonthKey = current.PeriodMonthKey,
            Revision = checked(current.Revision + 1),
            LastLedgerId = ledgerId,
            Reason = "MONTHLY_QUOTA_POOL",
            Outcome = WorkSummaryTokenOutcomes.Success,
            CreatedAtUtc = current.CreatedAtUtc,
            UpdatedAtUtc = now,
            CreatedByUserId = current.CreatedByUserId,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };
        next.MonthlyQuota = CalculateAllowance(
            next.BaseMonthlyQuota,
            next.GrantedUnits);
        if (next.UsedUnits < 0)
            throw PoolIntegrityConflict(next, "USED_UNITS_NEGATIVE");
        next.PoolHash = ComputePoolHash(next);
        return next;
    }

    private async Task PersistMutationAsync(
        IClientSessionHandle session,
        PoolLoadState state,
        WorkSummaryTokenLedger next,
        WorkSummaryTokenLedger entry,
        StatConfigCommandReceipt receipt,
        CancellationToken ct)
    {
        if (state.IsPersisted)
        {
            var replacement = await _ctx.WorkSummaryTokenLedgers
                .ReplaceOneAsync(
                    session,
                    x => x.Id == state.Pool.Id &&
                         !x.IsDeleted &&
                         x.RecordKind ==
                         WorkSummaryTokenLedgerRecordKinds.Pool &&
                         x.Revision == state.Pool.Revision &&
                         x.PoolHash == state.Pool.PoolHash,
                    next,
                    cancellationToken: ct);
            if (replacement.ModifiedCount != 1)
            {
                throw PoolCasConflict(
                    state.Pool,
                    state.Pool.Revision,
                    state.Pool.PoolHash ?? string.Empty);
            }
        }
        else
        {
            await _ctx.WorkSummaryTokenLedgers.InsertOneAsync(
                session,
                next,
                cancellationToken: ct);
        }

        await _ctx.WorkSummaryTokenLedgers.InsertOneAsync(
            session,
            entry,
            cancellationToken: ct);
        await _ctx.StatConfigCommandReceipts.InsertOneAsync(
            session,
            receipt,
            cancellationToken: ct);
    }

    private static WorkSummaryTokenLedger NewEntry(
        string ledgerId,
        PoolLoadState state,
        WorkSummaryTokenLedger next,
        string requestTokenId,
        string requestHash,
        string receiptId,
        string actorUserId,
        string direction,
        int units,
        string reason,
        DateTime now)
        => new()
        {
            Id = ledgerId,
            RecordKind = WorkSummaryTokenLedgerRecordKinds.Entry,
            OwnerUnitId = next.OwnerUnitId,
            ActorUserId = actorUserId,
            IssuerUserId = direction == WorkSummaryTokenDirections.Grant
                ? actorUserId
                : null,
            TokenKind = next.TokenKind,
            Direction = direction,
            Units = units,
            BaseMonthlyQuota = next.BaseMonthlyQuota,
            GrantedUnits = next.GrantedUnits,
            UsedUnits = next.UsedUnits,
            MonthlyQuota = next.MonthlyQuota,
            Revision = next.Revision,
            PeriodMonthKey = next.PeriodMonthKey,
            RequestTokenId = requestTokenId,
            RequestHash = requestHash,
            PoolId = next.Id,
            PoolRevisionBefore = state.Pool.Revision,
            PoolRevisionAfter = next.Revision,
            PoolHashBefore = state.CasHash,
            PoolHashAfter = next.PoolHash,
            CommandReceiptId = receiptId,
            Reason = reason,
            Outcome = WorkSummaryTokenOutcomes.Success,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };

    private static StatConfigCommandReceipt NewReceipt<TResponse>(
        string receiptId,
        string commandKind,
        string commandId,
        string requestHash,
        WorkSummaryTokenLedger pool,
        WorkSummaryTokenLedger entry,
        TResponse response,
        string actorUserId,
        DateTime now)
    {
        var responseJson = StatConfigCanonicalJson.Canonicalize(response);
        return new StatConfigCommandReceipt
        {
            Id = receiptId,
            OwnerKind = WorkSummaryTokenOwnerKinds.Pool,
            OwnerId = pool.OwnerUnitId!,
            CommandKind = commandKind,
            CommandId = commandId,
            RequestHash = requestHash,
            ResponseJson = responseJson,
            ResponseHash = StatConfigCanonicalJson.HashUtf8(responseJson),
            ResultConfigId = pool.Id,
            ResultVersionId = entry.Id,
            ResultVersionNo = 0,
            ResultRevision = pool.Revision,
            ResultStatus = StatConfigStatuses.Active,
            ResultConfigHash = pool.PoolHash!,
            ActorUserId = actorUserId,
            CreatedAtUtc = now
        };
    }

    private async Task<WorkSummaryTokenLedger?> FindRequestEntryAsync(
        IClientSessionHandle session,
        string ownerUnitId,
        string tokenKind,
        string requestTokenId,
        CancellationToken ct)
        => await _ctx.WorkSummaryTokenLedgers
            .Find(
                session,
                x => x.OwnerUnitId == ownerUnitId &&
                     x.TokenKind == tokenKind &&
                     x.RequestTokenId == requestTokenId &&
                     !x.IsDeleted &&
                     x.RecordKind !=
                     WorkSummaryTokenLedgerRecordKinds.Pool)
            .FirstOrDefaultAsync(ct);

    private async Task<WorkSummaryTokenLedger?> FindCompensationAsync(
        IClientSessionHandle session,
        string ledgerId,
        CancellationToken ct)
        => await _ctx.WorkSummaryTokenLedgers
            .Find(
                session,
                x => x.CompensatesLedgerId == ledgerId &&
                     !x.IsDeleted &&
                     x.RecordKind ==
                     WorkSummaryTokenLedgerRecordKinds.Entry)
            .FirstOrDefaultAsync(ct);

    private async Task<TResponse> RestoreResponseAsync<TResponse>(
        IClientSessionHandle session,
        WorkSummaryTokenLedger entry,
        string requestHash,
        string expectedDirection,
        string expectedCommandKind,
        CancellationToken ct)
    {
        EnsureReplayEntry(
            entry,
            requestHash,
            expectedDirection);
        var receiptId = entry.CommandReceiptId;
        if (string.IsNullOrWhiteSpace(receiptId))
        {
            throw ReplayConflict(
                entry.OwnerUnitId ?? string.Empty,
                entry.RequestTokenId ?? string.Empty,
                "LEGACY_ENTRY_HAS_NO_DURABLE_RECEIPT");
        }

        var receipt = await _ctx.StatConfigCommandReceipts
            .Find(session, x => x.Id == receiptId)
            .FirstOrDefaultAsync(ct);
        return RestoreResponse<TResponse>(
            entry,
            receipt,
            requestHash,
            expectedCommandKind);
    }

    private async Task<TResponse>
        RestoreResponseOutsideSessionAsync<TResponse>(
            WorkSummaryTokenLedger entry,
            string requestHash,
            string expectedDirection,
            string expectedCommandKind,
            CancellationToken ct)
    {
        EnsureReplayEntry(
            entry,
            requestHash,
            expectedDirection);
        var receiptId = entry.CommandReceiptId;
        if (string.IsNullOrWhiteSpace(receiptId))
        {
            throw ReplayConflict(
                entry.OwnerUnitId ?? string.Empty,
                entry.RequestTokenId ?? string.Empty,
                "LEGACY_ENTRY_HAS_NO_DURABLE_RECEIPT");
        }

        var receipt = await _ctx.StatConfigCommandReceipts
            .Find(x => x.Id == receiptId)
            .FirstOrDefaultAsync(ct);
        return RestoreResponse<TResponse>(
            entry,
            receipt,
            requestHash,
            expectedCommandKind);
    }

    private static TResponse RestoreResponse<TResponse>(
        WorkSummaryTokenLedger entry,
        StatConfigCommandReceipt? receipt,
        string requestHash,
        string expectedCommandKind)
    {
        if (receipt is null ||
            !string.Equals(
                receipt.RequestHash,
                requestHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.CommandKind,
                expectedCommandKind,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.ResponseHash,
                StatConfigCanonicalJson.HashUtf8(receipt.ResponseJson),
                StringComparison.Ordinal))
        {
            throw ReplayConflict(
                entry.OwnerUnitId ?? string.Empty,
                entry.RequestTokenId ?? string.Empty,
                "TOKEN_RECEIPT_INTEGRITY_MISMATCH");
        }

        try
        {
            return JsonSerializer.Deserialize<TResponse>(
                       receipt.ResponseJson,
                       StatConfigCanonicalJson.StrictJsonOptions)
                   ?? throw new JsonException("Null receipt response.");
        }
        catch (JsonException ex)
        {
            throw new AppException(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new
                {
                    reason = "TOKEN_RECEIPT_RESPONSE_SCHEMA",
                    receipt.Id
                },
                innerException: ex);
        }
    }

    private async Task<TResponse?> RestoreOutsideTransactionAsync<TResponse>(
        string ownerUnitId,
        string tokenKind,
        string commandId,
        string requestHash,
        string expectedDirection,
        string expectedCommandKind,
        CancellationToken ct)
    {
        var entry = await _ctx.WorkSummaryTokenLedgers
            .Find(x => x.OwnerUnitId == ownerUnitId &&
                       x.TokenKind == tokenKind &&
                       x.RequestTokenId == commandId &&
                       !x.IsDeleted &&
                       x.RecordKind !=
                       WorkSummaryTokenLedgerRecordKinds.Pool)
            .FirstOrDefaultAsync(ct);
        return entry is null
            ? default
            : await RestoreResponseOutsideSessionAsync<TResponse>(
                entry,
                requestHash,
                expectedDirection,
                expectedCommandKind,
                ct);
    }

    private static void EnsureReplayEntry(
        WorkSummaryTokenLedger entry,
        string requestHash,
        string expectedDirection)
    {
        if (!string.Equals(
                entry.RequestHash,
                requestHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                entry.Direction,
                expectedDirection,
                StringComparison.Ordinal))
        {
            throw ReplayConflict(
                entry.OwnerUnitId ?? string.Empty,
                entry.RequestTokenId ?? string.Empty,
                "CHANGED_TOKEN_REQUEST_REPLAY");
        }
    }

    private async Task EnsureActorOwnsUnitAsync(
        IClientSessionHandle session,
        string actorUserId,
        string ownerUnitId,
        CancellationToken ct)
    {
        var actor = await _ctx.Users
            .Find(
                session,
                x => x.Id == actorUserId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.COMMON_NOT_FOUND,
                new { actorUserId });
        if (!string.Equals(
                actor.UnitId,
                ownerUnitId,
                StringComparison.Ordinal))
        {
            throw GrantForbidden(
                "actorUnitChanged",
                actorUserId,
                ownerUnitId);
        }

        await LoadUnitRequiredP8Async(
            session,
            ownerUnitId,
            ct);
    }

    private async Task<Unit> LoadUnitRequiredP8Async(
        IClientSessionHandle session,
        string ownerUnitId,
        CancellationToken ct)
        => await _ctx.Units
               .Find(
                   session,
                   x => x.Id == ownerUnitId && !x.IsDeleted)
               .FirstOrDefaultAsync(ct)
           ?? throw AppExceptionFactory.NotFound(
               AppErrorCode.COMMON_NOT_FOUND,
               new { ownerUnitId });

    private async Task<LegacyTotals> ReadLegacyTotalsAsync(
        string ownerUnitId,
        string monthKey,
        string tokenKind,
        CancellationToken ct)
    {
        var rows = await _ctx.WorkSummaryTokenLedgers
            .Find(x => x.OwnerUnitId == ownerUnitId &&
                       x.PeriodMonthKey == monthKey &&
                       x.TokenKind == tokenKind &&
                       x.Outcome == WorkSummaryTokenOutcomes.Success &&
                       !x.IsDeleted &&
                       x.RecordKind !=
                       WorkSummaryTokenLedgerRecordKinds.Pool)
            .ToListAsync(ct);
        return FoldLegacyTotals(rows, ownerUnitId, monthKey, tokenKind);
    }

    private async Task<LegacyTotals> ReadLegacyTotalsAsync(
        IClientSessionHandle session,
        string ownerUnitId,
        string monthKey,
        string tokenKind,
        CancellationToken ct)
    {
        var rows = await _ctx.WorkSummaryTokenLedgers
            .Find(
                session,
                x => x.OwnerUnitId == ownerUnitId &&
                     x.PeriodMonthKey == monthKey &&
                     x.TokenKind == tokenKind &&
                     x.Outcome == WorkSummaryTokenOutcomes.Success &&
                     !x.IsDeleted &&
                     x.RecordKind !=
                     WorkSummaryTokenLedgerRecordKinds.Pool)
            .ToListAsync(ct);
        return FoldLegacyTotals(rows, ownerUnitId, monthKey, tokenKind);
    }

    private static LegacyTotals FoldLegacyTotals(
        IEnumerable<WorkSummaryTokenLedger> rows,
        string ownerUnitId,
        string monthKey,
        string tokenKind)
    {
        var granted = 0;
        var consumed = 0;
        var compensated = 0;
        foreach (var row in rows)
        {
            switch (row.Direction)
            {
                case WorkSummaryTokenDirections.Grant when row.Units > 0:
                    granted = checked(granted + row.Units);
                    break;
                case WorkSummaryTokenDirections.Consume when row.Units > 0:
                    consumed = checked(consumed + row.Units);
                    break;
                case WorkSummaryTokenDirections.Free when row.Units == 0:
                    break;
                case WorkSummaryTokenDirections.Compensate
                    when row.Units > 0:
                    compensated = checked(compensated + row.Units);
                    break;
                default:
                    throw AppExceptionFactory.Create(
                        AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                        new
                        {
                            reason = "LEGACY_TOKEN_LEDGER_INCONSISTENT",
                            row.Id,
                            row.Direction,
                            row.Units,
                            ownerUnitId,
                            monthKey,
                            tokenKind
                        });
            }
        }

        if (compensated > consumed)
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new
                {
                    reason = "LEGACY_TOKEN_LEDGER_UNDERFLOW",
                    consumed,
                    compensated,
                    ownerUnitId,
                    monthKey,
                    tokenKind
                });
        }

        return new LegacyTotals(granted, consumed - compensated);
    }

    private static void EnsureExternalCas(
        PoolLoadState state,
        long expectedRevision,
        string expectedHash)
    {
        var actualRevision = state.IsPersisted
            ? state.Pool.Revision
            : 0;
        var actualHash = state.IsPersisted
            ? state.Pool.PoolHash!
            : StatConfigCanonicalJson.EmptyConfigHash;
        if (actualRevision != expectedRevision ||
            !string.Equals(
                actualHash,
                expectedHash,
                StringComparison.Ordinal))
        {
            throw PoolCasConflict(
                state.Pool,
                expectedRevision,
                expectedHash);
        }
    }

    private static void EnsurePoolIntegrity(
        WorkSummaryTokenLedger pool,
        string ownerUnitId,
        string monthKey,
        string tokenKind)
    {
        if (!string.Equals(
                pool.RecordKind,
                WorkSummaryTokenLedgerRecordKinds.Pool,
                StringComparison.Ordinal) ||
            !string.Equals(
                pool.OwnerUnitId,
                ownerUnitId,
                StringComparison.Ordinal) ||
            !string.Equals(
                pool.PeriodMonthKey,
                monthKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                pool.TokenKind,
                tokenKind,
                StringComparison.Ordinal) ||
            pool.Revision < 0 ||
            pool.BaseMonthlyQuota < 0 ||
            pool.GrantedUnits < 0 ||
            pool.UsedUnits < 0 ||
            pool.MonthlyQuota != CalculateAllowance(
                pool.BaseMonthlyQuota,
                pool.GrantedUnits) ||
            !string.Equals(
                pool.PoolHash,
                ComputePoolHash(pool),
                StringComparison.Ordinal))
        {
            throw PoolIntegrityConflict(
                pool,
                "POOL_STATE_HASH_OR_TOTAL_MISMATCH");
        }
    }

    private static void EnsureCompensable(
        WorkSummaryTokenLedger source)
    {
        if (!string.Equals(
                source.Direction,
                WorkSummaryTokenDirections.Consume,
                StringComparison.Ordinal) ||
            source.Units != 1 ||
            !string.Equals(
                source.Outcome,
                WorkSummaryTokenOutcomes.Success,
                StringComparison.Ordinal) ||
            !string.Equals(
                source.TokenKind,
                WorkSummaryTokenKinds.AdvancedSummaryConfigLock,
                StringComparison.Ordinal) ||
            !MonthKeyRegex.IsMatch(source.PeriodMonthKey))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
                new
                {
                    path = "$.compensatedLedgerId",
                    reason = "TOKEN_ENTRY_NOT_COMPENSABLE",
                    source.Id
                });
        }

        RequireCanonicalObjectId(
            source.ConfigId,
            "$.source.configId");
        RequireCanonicalObjectId(
            source.WorkAssignmentId,
            "$.source.workAssignmentId");
        RequireCanonicalObjectId(
            source.DynamicFormTemplateId,
            "$.source.dynamicFormTemplateId");
        if (source.ConfigVersionNo is null or <= 0 ||
            string.IsNullOrWhiteSpace(source.ConfigHash) ||
            !P8Sha256Regex.IsMatch(source.ConfigHash) ||
            string.IsNullOrWhiteSpace(source.SectionId) ||
            source.SectionId != source.SectionId.Trim())
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
                new
                {
                    path = "$.compensatedLedgerId",
                    reason = "TOKEN_ENTRY_CONFIG_LINKAGE_INVALID",
                    source.Id
                });
        }
    }

    private static void ValidateAdvancedConfigIdentity(
        WorkAssignmentAdvancedSummaryConfig config)
    {
        RequireCanonicalObjectId(config.Id, "$.config.id");
        RequireCanonicalObjectId(config.WorkId, "$.config.workId");
        RequireCanonicalObjectId(
            config.AssignmentId,
            "$.config.assignmentId");
        RequireCanonicalObjectId(
            config.DynamicFormTemplateId,
            "$.config.dynamicFormTemplateId");
        if (string.IsNullOrWhiteSpace(config.SectionId) ||
            config.SectionId != config.SectionId.Trim())
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
                new
                {
                    path = "$.config.sectionId",
                    reason = "SECTION_ID_INVALID"
                });
        }
        if (config.VersionNo <= 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
                new
                {
                    path = "$.config.versionNo",
                    reason = "VERSION_INVALID"
                });
        }
        if (string.IsNullOrWhiteSpace(config.ConfigHash) ||
            !P8Sha256Regex.IsMatch(config.ConfigHash))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
                new
                {
                    path = "$.config.configHash",
                    reason = "CONFIG_HASH_INVALID"
                });
        }
    }

    private static string ComputePoolHash(
        WorkSummaryTokenLedger pool)
        => StatConfigCanonicalJson.HashObject(new
        {
            schema = PoolHashSchema,
            recordKind = WorkSummaryTokenLedgerRecordKinds.Pool,
            ownerUnitId = pool.OwnerUnitId,
            tokenKind = pool.TokenKind,
            periodMonthKey = pool.PeriodMonthKey,
            baseMonthlyQuota = pool.BaseMonthlyQuota,
            grantedUnits = pool.GrantedUnits,
            usedUnits = pool.UsedUnits,
            monthlyQuota = pool.MonthlyQuota
        });

    private static WorkSummaryTokenQuotaResponse ToQuota(
        WorkSummaryTokenLedger pool)
        => new()
        {
            PoolId = pool.Id,
            Revision = pool.Revision,
            PoolHash = pool.PoolHash!,
            OwnerUnitId = pool.OwnerUnitId!,
            TokenKind = pool.TokenKind,
            PeriodMonthKey = pool.PeriodMonthKey,
            BaseMonthlyQuota = pool.BaseMonthlyQuota,
            GrantedUnits = pool.GrantedUnits,
            UsedUnits = pool.UsedUnits,
            MonthlyQuota = pool.MonthlyQuota,
            RemainingUnits = Math.Max(
                0,
                pool.MonthlyQuota - pool.UsedUnits)
        };

    private static NormalizedStatConfigCommand<
        WorkSummaryTokenGrantP8Payload> RehashGrantCommand(
            NormalizedStatConfigCommand<
                WorkSummaryTokenGrantP8Payload> command,
            string ownerUnitId,
            int units,
            string reason)
        => command with
        {
            Payload = new WorkSummaryTokenGrantP8Payload(
                units,
                reason),
            RequestHash = StatConfigCanonicalJson.HashObject(new
            {
                commandKind =
                    StatConfigCommandKinds.GrantWorkSummaryTokenQuota,
                commandId = command.CommandId,
                ownerUnitId,
                expectedRevision = command.ExpectedRevision,
                expectedConfigHash = command.ExpectedConfigHash,
                payload = new { units, reason }
            })
        };

    private static NormalizedStatConfigCommand<
        WorkSummaryTokenCompensationP8Payload>
        RehashCompensationCommand(
            NormalizedStatConfigCommand<
                WorkSummaryTokenCompensationP8Payload> command,
            string ledgerId,
            string reason)
        => command with
        {
            Payload = new WorkSummaryTokenCompensationP8Payload(reason),
            RequestHash = StatConfigCanonicalJson.HashObject(new
            {
                commandKind = StatConfigCommandKinds
                    .CompensateAdvancedSummaryConfigLockToken,
                commandId = command.CommandId,
                compensatedLedgerId = ledgerId,
                expectedRevision = command.ExpectedRevision,
                expectedConfigHash = command.ExpectedConfigHash,
                payload = new { reason }
            })
        };

    private static string RequireCanonicalObjectId(
        string? value,
        string path)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value != value.Trim() ||
            !ObjectId.TryParse(value, out var parsed) ||
            !string.Equals(
                parsed.ToString(),
                value,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
                new { path, reason = "CANONICAL_OBJECT_ID_REQUIRED" });
        }
        return value;
    }

    private static string RequireCommandId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value != value.Trim() ||
            !P8CommandIdRegex.IsMatch(value))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.STAT_CONFIG_COMMAND_ID_INVALID,
                new { path = "$.requestTokenId", maxLength = 128 });
        }
        return value;
    }

    private static string PoolId(
        string ownerUnitId,
        string monthKey,
        string tokenKind)
        => DeterministicObjectId(
            $"POOL\0{ownerUnitId}\0{monthKey}\0{tokenKind}");

    private static string DeterministicObjectId(string seed)
    {
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(seed)))
            .ToLowerInvariant();
        return digest[..24];
    }

    private static string ReceiptId(
        string ownerUnitId,
        string commandId)
        => StatConfigCanonicalJson.HashUtf8(
            $"{WorkSummaryTokenOwnerKinds.Pool}\0{ownerUnitId}\0{commandId}");

    private static AppException PoolCasConflict(
        WorkSummaryTokenLedger pool,
        long expectedRevision,
        string expectedHash)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                ownerKind = WorkSummaryTokenOwnerKinds.Pool,
                ownerId = pool.Id,
                expectedRevision,
                actualRevision = pool.Revision,
                expectedConfigHash = expectedHash,
                actualConfigHash = pool.PoolHash
            });

    private static AppException PoolIntegrityConflict(
        WorkSummaryTokenLedger pool,
        string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                ownerKind = WorkSummaryTokenOwnerKinds.Pool,
                ownerId = pool.Id,
                reason
            });

    private static AppException ReplayConflict(
        string ownerId,
        string commandId,
        string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_COMMAND_REPLAY_CONFLICT,
            new
            {
                ownerKind = WorkSummaryTokenOwnerKinds.Pool,
                ownerId,
                commandId,
                reason
            });

    private static AppException TokenEntryNotOrphan(
        WorkSummaryTokenLedger source)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                path = "$.compensatedLedgerId",
                reason = "TOKEN_ENTRY_NOT_ORPHAN",
                source.Id,
                source.ConfigId,
                source.ConfigVersionNo
            });

    private static AppException LegacyMutationBarrier(
        string operation,
        string replacement)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            new
            {
                path = "$",
                reason = "P8_COMMAND_ENVELOPE_REQUIRED",
                operation,
                replacement
            });

    private sealed record PoolLoadState(
        WorkSummaryTokenLedger Pool,
        bool IsPersisted,
        string CasHash);

    private sealed record LegacyTotals(
        int GrantedUnits,
        int UsedUnits);
}
