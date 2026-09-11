using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignments.SummaryTokens;

public sealed partial class WorkSummaryTokenService
{
    private const string P9AdvancedBroadBuildCommandKind =
        "P9_CONSUME_ADVANCED_SUMMARY_BROAD_HISTORICAL_BUILD";

    public async Task<WorkSummaryTokenConsumeResult>
        ConsumeAdvancedBroadHistoricalBuildP9Async(
            WorkAssignmentAdvancedSummaryConfig config,
            string ownerUnitId,
            string commandId,
            string grain,
            string grainKey,
            string actorUserId,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ownerUnitId = RequireCanonicalObjectId(ownerUnitId, "$.ownerUnitId");
        actorUserId = RequireCanonicalObjectId(actorUserId, "$.actorUserId");
        commandId = RequireCommandId(commandId);
        grain = NormalizeRequired(grain, "grain").ToUpperInvariant();
        grainKey = NormalizeRequired(grainKey, "grainKey");
        ValidateAdvancedConfigIdentity(config);

        var tokenKind = WorkSummaryTokenKinds.AdvancedSummaryBroadHistoricalBuild;
        var requestTokenId = $"p9advb-{StatConfigCanonicalJson.HashUtf8(commandId)[..48]}";
        var requestHash = StatConfigCanonicalJson.HashObject(new
        {
            commandKind = P9AdvancedBroadBuildCommandKind,
            commandId,
            ownerUnitId,
            configId = config.ConfigId,
            configVersionId = config.Id,
            config.VersionNo,
            config.Revision,
            config.ConfigHash,
            grain,
            grainKey,
            timeAxis = "UTC_GREGORIAN"
        });

        return await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                await EnsureActorOwnsUnitAsync(
                    session,
                    actorUserId,
                    ownerUnitId,
                    transactionCt);

                var replay = await FindRequestEntryAsync(
                    session,
                    ownerUnitId,
                    tokenKind,
                    requestTokenId,
                    transactionCt);
                if (replay is not null)
                {
                    return await RestoreResponseAsync<WorkSummaryTokenConsumeResult>(
                        session,
                        replay,
                        requestHash,
                        WorkSummaryTokenDirections.Consume,
                        P9AdvancedBroadBuildCommandKind,
                        transactionCt);
                }

                var now = DateTime.UtcNow;
                var monthKey = ToMonthKey(now);
                var state = await LoadOrBuildPoolAsync(
                    session,
                    ownerUnitId,
                    monthKey,
                    tokenKind,
                    actorUserId,
                    transactionCt);
                const int units = 1;
                var usedBefore = state.Pool.UsedUnits;
                if (WouldExceedQuota(usedBefore, units, state.Pool.MonthlyQuota))
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
                            configId = config.ConfigId,
                            configVersionId = config.Id,
                            grain,
                            grainKey,
                            writes = 0
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
                    IsFree: false);
                var entry = NewEntry(
                    ledgerId,
                    state,
                    next,
                    requestTokenId,
                    requestHash,
                    receiptId,
                    actorUserId,
                    WorkSummaryTokenDirections.Consume,
                    units,
                    WorkSummaryTokenKinds.AdvancedSummaryBroadHistoricalBuild,
                    now);
                entry.OwnerUserId = actorUserId;
                entry.WorkId = config.WorkId;
                entry.WorkAssignmentId = config.AssignmentId;
                entry.DynamicFormTemplateId = config.DynamicFormTemplateId;
                entry.SectionId = config.SectionId;
                entry.ConfigId = config.ConfigId;
                entry.ConfigVersionNo = config.VersionNo;
                entry.ConfigHash = config.ConfigHash;
                entry.JobId = $"{grain}:{grainKey}";

                await PersistMutationAsync(
                    session,
                    state,
                    next,
                    entry,
                    NewReceipt(
                        receiptId,
                        P9AdvancedBroadBuildCommandKind,
                        requestTokenId,
                        requestHash,
                        next,
                        entry,
                        result,
                        actorUserId,
                        now),
                    transactionCt);
                return result;
            },
            ct);
    }
}
