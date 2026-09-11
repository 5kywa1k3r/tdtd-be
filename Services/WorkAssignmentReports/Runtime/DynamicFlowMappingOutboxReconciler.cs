using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignments.Queue;
using tdtd_be.Services.WorkAssignments.Runtime;

namespace tdtd_be.Services.WorkAssignmentReports.Runtime;

public sealed record DynamicFlowMappingApplyResult(
    string ReceiptId,
    string CommandId,
    string State);

public interface IDynamicFlowMappingOutboxReconciler
{
    Task<int> ProcessPendingAsync(
        int maxItems,
        CancellationToken ct = default);

    Task<bool> ProcessByIdAsync(
        string outboxId,
        CancellationToken ct = default);

    Task<DynamicFlowMappingApplyResult?> GetApplyResultAsync(
        string receiptId,
        CancellationToken ct = default);
}

/// <summary>
/// Repairs projector work that was intentionally left outside the mapping apply
/// transaction. Every side effect is driven by the committed, hash-bound intent.
/// Receipt and outbox terminal state always move in one Mongo transaction.
/// </summary>
public sealed class DynamicFlowMappingOutboxReconciler
    : IDynamicFlowMappingOutboxReconciler
{
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };
    private static readonly string[] ClaimableStates =
    {
        DynamicFlowMappingOutboxStates.Pending,
        DynamicFlowMappingOutboxStates.Partial,
        DynamicFlowMappingOutboxStates.Retrying,
        DynamicFlowMappingOutboxStates.Processing
    };
    private static readonly HashSet<string> ApplyStates =
        new(StringComparer.Ordinal)
        {
            DynamicFlowMappingApplyStates.Committed,
            DynamicFlowMappingApplyStates.Partial,
            DynamicFlowMappingApplyStates.Retrying,
            DynamicFlowMappingApplyStates.Reconciled
        };

    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentQueueService _queue;
    private readonly IWorkAssignmentStatusSyncService _statusSync;
    private readonly IDocRoleReadModelProjectionService _docRoleProjection;
    private readonly IDynamicFlowDefinitionTransactionRunner _transactions;
    private readonly IDynamicFlowMappingReconcileFaultInjector _faults;
    private readonly ILogger<DynamicFlowMappingOutboxReconciler> _logger;
    private readonly string _leaseOwner;

    public DynamicFlowMappingOutboxReconciler(
        MongoDbContext ctx,
        IWorkAssignmentQueueService queue,
        IWorkAssignmentStatusSyncService statusSync,
        IDocRoleReadModelProjectionService docRoleProjection,
        IDynamicFlowDefinitionTransactionRunner transactions,
        IDynamicFlowMappingReconcileFaultInjector faults,
        ILogger<DynamicFlowMappingOutboxReconciler> logger)
    {
        _ctx = ctx;
        _queue = queue;
        _statusSync = statusSync;
        _docRoleProjection = docRoleProjection;
        _transactions = transactions;
        _faults = faults;
        _logger = logger;
        _leaseOwner =
            $"mapping-reconciler:{Environment.MachineName}:{Environment.ProcessId}";
    }

    public async Task<int> ProcessPendingAsync(
        int maxItems,
        CancellationToken ct = default)
    {
        maxItems = Math.Clamp(maxItems, 1, 200);
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var processed = 0;

        // A crash between legacy terminal writes can leave a RECONCILED outbox
        // with a non-terminal receipt. Scan receipts first so that split state
        // cannot be starved by a continuously busy due queue.
        var splitCandidates = await _ctx.DynamicFlowMappingApplyReceipts
            .Find(receipt =>
                receipt.State != DynamicFlowMappingApplyStates.Reconciled &&
                receipt.OutboxIntentId != null)
            .SortBy(receipt => receipt.UpdatedAtUtc)
            .ThenBy(receipt => receipt.Id)
            .Limit(maxItems)
            .Project(receipt => receipt.OutboxIntentId)
            .ToListAsync(ct);

        foreach (var outboxId in splitCandidates)
        {
            if (processed >= maxItems)
                return processed;
            if (string.IsNullOrWhiteSpace(outboxId) ||
                !attempted.Add(outboxId))
            {
                continue;
            }

            if (await ProcessByIdAsync(outboxId, ct))
                processed++;
        }

        var dueIds = await _ctx.DynamicFlowMappingOutbox
            .Find(BuildClaimableFilter(DateTime.UtcNow))
            .SortBy(item => item.NextAttemptAtUtc)
            .ThenBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id)
            .Limit(maxItems)
            .Project(item => item.Id)
            .ToListAsync(ct);

        foreach (var outboxId in dueIds)
        {
            if (processed >= maxItems)
                break;
            if (!attempted.Add(outboxId))
                continue;

            if (await ProcessByIdAsync(outboxId, ct))
                processed++;
        }

        return processed;
    }

    public async Task<bool> ProcessByIdAsync(
        string outboxId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(outboxId))
            return false;

        outboxId = outboxId.Trim();
        var observed = await _ctx.DynamicFlowMappingOutbox
            .Find(item => item.Id == outboxId)
            .FirstOrDefaultAsync(ct);
        if (observed is null)
            return false;
        if (string.Equals(
                observed.State,
                DynamicFlowMappingOutboxStates.Reconciled,
                StringComparison.Ordinal))
        {
            await RepairReconciledReceiptAsync(observed, ct);
            return true;
        }

        var claimed = await TryClaimAsync(outboxId, ct);
        if (claimed is null)
        {
            observed = await _ctx.DynamicFlowMappingOutbox
                .Find(item => item.Id == outboxId)
                .FirstOrDefaultAsync(ct);
            if (observed is not null &&
                string.Equals(
                    observed.State,
                    DynamicFlowMappingOutboxStates.Reconciled,
                    StringComparison.Ordinal))
            {
                await RepairReconciledReceiptAsync(observed, ct);
                return true;
            }

            return false;
        }

        // Pause immediately after the durable lease claim, before any renewal
        // or retry-state mutation, so a Testing harness can kill the owner or
        // advance/replace the exact lease and prove fenced recovery.
        _faults.PauseAfterOutboxClaimIfConfigured(
            claimed.CommandId,
            claimed.Id);

        try
        {
            if (!await MarkRetryingAsync(claimed, ct))
                return true;

            var reconcile = await LoadAndValidateIntentAsync(claimed, ct);
            var projectorPlan =
                await EnsureAndValidateProjectorPlanAsync(
                    claimed,
                    reconcile.PeriodId,
                    ct);
            await RenewLeaseAsync(claimed, ct);
            _faults.ThrowIfConfigured(
                claimed.CommandId,
                DynamicFlowMappingReconcileFaultPoints
                    .BeforeProjectors);
            await ExecuteProjectorAsync(
                claimed,
                projectorPlan.QueuePeriod,
                async projectorCt =>
                {
                    if (reconcile.Period is not null)
                    {
                        await _queue.UpsertPeriodAsync(
                            reconcile.Period,
                            reconcile.ActorUserId,
                            projectorCt);
                    }
                },
                ct);
            await ExecuteProjectorAsync(
                claimed,
                projectorPlan.AssignmentStatus,
                projectorCt =>
                    _statusSync.SyncFromAssignmentIdempotentAsync(
                        claimed.Intent.TargetAssignmentId,
                        projectorPlan.AssignmentStatus
                            .IdempotencyKey,
                        projectorCt),
                ct);
            await ExecuteProjectorAsync(
                claimed,
                projectorPlan.DocRolePeriod,
                async projectorCt =>
                {
                    if (reconcile.Period is not null)
                    {
                        await _docRoleProjection
                            .RebuildReportPeriodAsync(
                                reconcile.Period.Id,
                                reconcile.ActorUserId,
                                projectorCt);
                    }
                },
                ct);

            await RenewLeaseAsync(claimed, ct);
            _faults.ThrowIfConfigured(
                claimed.CommandId,
                DynamicFlowMappingReconcileFaultPoints
                    .BeforeFinalize);
            if (!await CompleteReconciledAsync(claimed, ct))
            {
                _logger.LogInformation(
                    "Dynamic Flow mapping reconcile completion lost its fence. outboxId={OutboxId} leaseId={LeaseId} repairEpoch={RepairEpoch}",
                    claimed.Id,
                    claimed.LeaseId,
                    claimed.RepairEpoch);
            }

            return true;
        }
        catch (Exception error)
        {
            try
            {
                await CompletePartialAsync(
                    claimed,
                    error,
                    CancellationToken.None);
            }
            catch (Exception terminalError)
            {
                _logger.LogError(
                    terminalError,
                    "Dynamic Flow mapping reconcile could not persist its failure state. outboxId={OutboxId} leaseId={LeaseId} repairEpoch={RepairEpoch}",
                    claimed.Id,
                    claimed.LeaseId,
                    claimed.RepairEpoch);
            }

            _logger.LogWarning(
                error,
                "Dynamic Flow mapping reconcile deferred. outboxId={OutboxId} receiptId={ReceiptId} attempt={Attempt} repairEpoch={RepairEpoch}",
                claimed.Id,
                claimed.ReceiptId,
                claimed.AttemptCount,
                claimed.RepairEpoch);
            if (error is OperationCanceledException &&
                ct.IsCancellationRequested)
            {
                throw;
            }

            return true;
        }
    }

    public async Task<DynamicFlowMappingApplyResult?> GetApplyResultAsync(
        string receiptId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(receiptId))
            return null;

        var receipt = await _ctx.DynamicFlowMappingApplyReceipts
            .Find(item => item.Id == receiptId.Trim())
            .FirstOrDefaultAsync(ct);
        if (receipt is null)
            return null;
        if (!ApplyStates.Contains(receipt.State))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_MAPPING_APPLY_STATE_INVALID");
        }

        return new DynamicFlowMappingApplyResult(
            receipt.Id,
            receipt.CommandId,
            receipt.State);
    }

    private async Task<DynamicFlowMappingOutboxItem?> TryClaimAsync(
        string outboxId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var leaseId = ObjectId.GenerateNewId().ToString();
        var filter =
            Builders<DynamicFlowMappingOutboxItem>.Filter.Eq(
                item => item.Id,
                outboxId) &
            BuildClaimableFilter(now);
        return await _ctx.DynamicFlowMappingOutbox.FindOneAndUpdateAsync(
            filter,
            Builders<DynamicFlowMappingOutboxItem>.Update
                .Set(
                    item => item.State,
                    DynamicFlowMappingOutboxStates.Processing)
                .Set(item => item.LeaseId, leaseId)
                .Set(item => item.LeaseOwner, _leaseOwner)
                .Set(item => item.LeaseAcquiredAtUtc, now)
                .Set(item => item.LeaseUntilUtc, now.Add(ClaimLease))
                .Set(item => item.LastAttemptedAtUtc, now)
                .Set(item => item.UpdatedAtUtc, now)
                .Inc(item => item.AttemptCount, 1)
                .Inc(item => item.RepairEpoch, 1),
            new FindOneAndUpdateOptions<DynamicFlowMappingOutboxItem>
            {
                ReturnDocument = ReturnDocument.After
            },
            ct);
    }

    private static FilterDefinition<DynamicFlowMappingOutboxItem>
        BuildClaimableFilter(DateTime now)
    {
        var fb = Builders<DynamicFlowMappingOutboxItem>.Filter;
        var leaseAvailable =
            fb.Eq(item => item.LeaseId, null) |
            fb.Eq(item => item.LeaseUntilUtc, null) |
            fb.Lt(item => item.LeaseUntilUtc, now);
        var due =
            fb.Lte(item => item.NextAttemptAtUtc, now) |
            fb.Exists(item => item.NextAttemptAtUtc, false);
        return fb.In(item => item.State, ClaimableStates) &
               leaseAvailable &
               due;
    }

    private async Task<bool> MarkRetryingAsync(
        DynamicFlowMappingOutboxItem claimed,
        CancellationToken ct)
        => await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var now = DateTime.UtcNow;
                var outboxUpdate =
                    await _ctx.DynamicFlowMappingOutbox.UpdateOneAsync(
                        session,
                        BuildFence(claimed, now),
                        Builders<DynamicFlowMappingOutboxItem>.Update
                            .Set(item => item.RetryingAtUtc, now)
                            .Set(
                                item => item.LeaseUntilUtc,
                                now.Add(ClaimLease))
                            .Set(item => item.UpdatedAtUtc, now),
                        cancellationToken: transactionCt);
                if (outboxUpdate.ModifiedCount != 1)
                    return false;

                var receiptUpdate =
                    await _ctx.DynamicFlowMappingApplyReceipts.UpdateOneAsync(
                        session,
                        BuildReceiptBinding(claimed) &
                        Builders<DynamicFlowMappingApplyReceipt>.Filter.Ne(
                            item => item.State,
                            DynamicFlowMappingApplyStates.Reconciled),
                        Builders<DynamicFlowMappingApplyReceipt>.Update
                            .Set(
                                item => item.State,
                                DynamicFlowMappingApplyStates.Retrying)
                            .Set(item => item.RetryingAtUtc, now)
                            .Set(item => item.UpdatedAtUtc, now)
                            .Unset(item => item.ErrorCode),
                        cancellationToken: transactionCt);
                if (receiptUpdate.ModifiedCount == 1)
                    return true;

                var receipt =
                    await _ctx.DynamicFlowMappingApplyReceipts
                        .Find(session, BuildReceiptBinding(claimed))
                        .FirstOrDefaultAsync(transactionCt);
                if (receipt is not null &&
                    string.Equals(
                        receipt.State,
                        DynamicFlowMappingApplyStates.Reconciled,
                        StringComparison.Ordinal))
                {
                    return true;
                }

                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_MAPPING_RECONCILE_RECEIPT_FENCE_LOST");
            },
            ct);

    private async Task RenewLeaseAsync(
        DynamicFlowMappingOutboxItem claimed,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var result = await _ctx.DynamicFlowMappingOutbox.UpdateOneAsync(
            BuildFence(claimed, now),
            Builders<DynamicFlowMappingOutboxItem>.Update
                .Set(item => item.LeaseUntilUtc, now.Add(ClaimLease))
                .Set(item => item.UpdatedAtUtc, now),
            cancellationToken: ct);
        if (result.ModifiedCount != 1)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_MAPPING_RECONCILE_LEASE_FENCE_LOST");
        }
    }

    private async Task<ProjectorPlan>
        EnsureAndValidateProjectorPlanAsync(
            DynamicFlowMappingOutboxItem claimed,
            string workReportPeriodId,
            CancellationToken ct)
    {
        var expected =
            DynamicFlowMappingProjectorContract.BuildPlan(
                claimed.Id,
                claimed.IntentHash,
                claimed.Intent.TargetAssignmentId,
                workReportPeriodId);
        if (claimed.ProjectorCheckpoints is null ||
            claimed.ProjectorCheckpoints.Count == 0)
        {
            var now = DateTime.UtcNow;
            var fb =
                Builders<DynamicFlowMappingOutboxItem>.Filter;
            var missingPlan =
                fb.Exists("projectorCheckpoints", false) |
                fb.Size("projectorCheckpoints", 0);
            var initialize =
                await _ctx.DynamicFlowMappingOutbox.UpdateOneAsync(
                    BuildFence(claimed, now) & missingPlan,
                    Builders<DynamicFlowMappingOutboxItem>.Update
                        .Set(
                            item => item.ProjectorCheckpoints,
                            expected)
                        .Set(item => item.UpdatedAtUtc, now),
                    cancellationToken: ct);
            if (initialize.ModifiedCount != 1)
            {
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_MAPPING_PROJECTOR_PLAN_FENCE_LOST");
            }

            claimed.ProjectorCheckpoints = expected;
        }

        ValidateProjectorPlan(claimed, expected);
        var byName = claimed.ProjectorCheckpoints.ToDictionary(
            item => item.Projector,
            StringComparer.Ordinal);
        return new ProjectorPlan(
            byName[DynamicFlowMappingProjectors.QueuePeriod],
            byName[DynamicFlowMappingProjectors.AssignmentStatus],
            byName[DynamicFlowMappingProjectors.DocRolePeriod]);
    }

    private async Task ExecuteProjectorAsync(
        DynamicFlowMappingOutboxItem claimed,
        DynamicFlowMappingProjectorCheckpoint checkpoint,
        Func<CancellationToken, Task> projector,
        CancellationToken ct)
    {
        if (string.Equals(
                checkpoint.State,
                DynamicFlowMappingProjectorCheckpointStates.Completed,
                StringComparison.Ordinal))
        {
            return;
        }

        await BeginProjectorAsync(claimed, checkpoint, ct);
        await projector(ct);
        await RenewLeaseAsync(claimed, ct);
        await CompleteProjectorAsync(claimed, checkpoint, ct);
    }

    private async Task BeginProjectorAsync(
        DynamicFlowMappingOutboxItem claimed,
        DynamicFlowMappingProjectorCheckpoint checkpoint,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var filter =
            BuildFence(claimed, now) &
            BuildProjectorIdentityFilter(
                checkpoint,
                requireIncomplete: true);
        var result = await _ctx.DynamicFlowMappingOutbox
            .UpdateOneAsync(
                filter,
                Builders<DynamicFlowMappingOutboxItem>.Update
                    .Set(
                        "projectorCheckpoints.$.state",
                        DynamicFlowMappingProjectorCheckpointStates
                            .Processing)
                    .Set(
                        "projectorCheckpoints.$.activeRepairEpoch",
                        claimed.RepairEpoch)
                    .Set(
                        "projectorCheckpoints.$.startedAtUtc",
                        now)
                    .Inc(
                        "projectorCheckpoints.$.attemptCount",
                        1)
                    .Set(
                        item => item.LeaseUntilUtc,
                        now.Add(ClaimLease))
                    .Set(item => item.UpdatedAtUtc, now),
                cancellationToken: ct);
        if (result.ModifiedCount != 1)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_MAPPING_PROJECTOR_START_FENCE_LOST");
        }

        checkpoint.State =
            DynamicFlowMappingProjectorCheckpointStates.Processing;
        checkpoint.ActiveRepairEpoch = claimed.RepairEpoch;
        checkpoint.StartedAtUtc = now;
        checkpoint.AttemptCount++;
    }

    private async Task CompleteProjectorAsync(
        DynamicFlowMappingOutboxItem claimed,
        DynamicFlowMappingProjectorCheckpoint checkpoint,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var completionHash =
            DynamicFlowMappingProjectorContract
                .ComputeCompletionHash(
                    claimed.IntentHash,
                    checkpoint,
                    claimed.RepairEpoch);
        var filter =
            BuildFence(claimed, now) &
            BuildProjectorCompletionFence(
                checkpoint,
                claimed.RepairEpoch);
        var result = await _ctx.DynamicFlowMappingOutbox
            .UpdateOneAsync(
                filter,
                Builders<DynamicFlowMappingOutboxItem>.Update
                    .Set(
                        "projectorCheckpoints.$.state",
                        DynamicFlowMappingProjectorCheckpointStates
                            .Completed)
                    .Set(
                        "projectorCheckpoints.$.completedAtUtc",
                        now)
                    .Set(
                        "projectorCheckpoints.$.completedRepairEpoch",
                        claimed.RepairEpoch)
                    .Set(
                        "projectorCheckpoints.$.completionHash",
                        completionHash)
                    .Unset(
                        "projectorCheckpoints.$.activeRepairEpoch")
                    .Set(item => item.UpdatedAtUtc, now),
                cancellationToken: ct);
        if (result.ModifiedCount != 1)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_MAPPING_PROJECTOR_COMPLETION_FENCE_LOST");
        }

        checkpoint.State =
            DynamicFlowMappingProjectorCheckpointStates.Completed;
        checkpoint.ActiveRepairEpoch = null;
        checkpoint.CompletedAtUtc = now;
        checkpoint.CompletedRepairEpoch = claimed.RepairEpoch;
        checkpoint.CompletionHash = completionHash;
    }

    private async Task<bool> CompleteReconciledAsync(
        DynamicFlowMappingOutboxItem claimed,
        CancellationToken ct)
        => await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var now = DateTime.UtcNow;
                var outboxUpdate =
                    await _ctx.DynamicFlowMappingOutbox.UpdateOneAsync(
                        session,
                        BuildFence(claimed, now) &
                        BuildAllProjectorsCompletedFilter(claimed),
                        Builders<DynamicFlowMappingOutboxItem>.Update
                            .Set(
                                item => item.State,
                                DynamicFlowMappingOutboxStates.Reconciled)
                            .Set(item => item.ReconciledAtUtc, now)
                            .Set(item => item.UpdatedAtUtc, now)
                            .Unset(item => item.LeaseId)
                            .Unset(item => item.LeaseOwner)
                            .Unset(item => item.LeaseAcquiredAtUtc)
                            .Unset(item => item.LeaseUntilUtc)
                            .Unset(item => item.LastErrorCode)
                            .Unset(item => item.LastErrorSnapshotHash),
                        cancellationToken: transactionCt);
                if (outboxUpdate.ModifiedCount != 1)
                    return false;

                var receiptUpdate =
                    await _ctx.DynamicFlowMappingApplyReceipts.UpdateOneAsync(
                        session,
                        BuildReceiptBinding(claimed),
                        Builders<DynamicFlowMappingApplyReceipt>.Update
                            .Set(
                                item => item.State,
                                DynamicFlowMappingApplyStates.Reconciled)
                            .Set(item => item.ReconciledAtUtc, now)
                            .Set(item => item.UpdatedAtUtc, now)
                            .Unset(item => item.ErrorCode),
                        cancellationToken: transactionCt);
                if (receiptUpdate.ModifiedCount == 1)
                    return true;

                var receipt =
                    await _ctx.DynamicFlowMappingApplyReceipts
                        .Find(session, BuildReceiptBinding(claimed))
                        .FirstOrDefaultAsync(transactionCt);
                if (receipt is not null &&
                    string.Equals(
                        receipt.State,
                        DynamicFlowMappingApplyStates.Reconciled,
                        StringComparison.Ordinal))
                {
                    return true;
                }

                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_MAPPING_RECONCILE_RECEIPT_TERMINAL_WRITE_FAILED");
            },
            ct);

    private async Task CompletePartialAsync(
        DynamicFlowMappingOutboxItem claimed,
        Exception error,
        CancellationToken ct)
    {
        var errorCode = error is AppException appError
            ? appError.Code.ToString()
            : "DYNAMIC_FLOW_MAPPING_RECONCILE_FAILED";
        var errorSnapshotHash = ComputeObjectHash(
            new
            {
                errorCode,
                errorType = error.GetType().FullName,
                outboxId = claimed.Id,
                claimed.AttemptCount,
                claimed.RepairEpoch
            });

        await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var now = DateTime.UtcNow;
                var outboxUpdate =
                    await _ctx.DynamicFlowMappingOutbox.UpdateOneAsync(
                        session,
                        BuildFence(claimed, now),
                        Builders<DynamicFlowMappingOutboxItem>.Update
                            .Set(
                                item => item.State,
                                DynamicFlowMappingOutboxStates.Partial)
                            .Set(item => item.PartialAtUtc, now)
                            .Set(
                                item => item.NextAttemptAtUtc,
                                now.AddSeconds(
                                    Math.Min(
                                        60,
                                        Math.Max(
                                            1,
                                            claimed.AttemptCount * 2))))
                            .Set(item => item.LastErrorCode, errorCode)
                            .Set(
                                item => item.LastErrorSnapshotHash,
                                errorSnapshotHash)
                            .Set(item => item.UpdatedAtUtc, now)
                            .Unset(item => item.LeaseId)
                            .Unset(item => item.LeaseOwner)
                            .Unset(item => item.LeaseAcquiredAtUtc)
                            .Unset(item => item.LeaseUntilUtc),
                        cancellationToken: transactionCt);
                if (outboxUpdate.ModifiedCount != 1)
                    return;

                var receiptUpdate =
                    await _ctx.DynamicFlowMappingApplyReceipts.UpdateOneAsync(
                        session,
                        BuildReceiptBinding(claimed),
                        Builders<DynamicFlowMappingApplyReceipt>.Update
                            .Set(
                                item => item.State,
                                DynamicFlowMappingApplyStates.Partial)
                            .Set(item => item.PartialAtUtc, now)
                            .Set(item => item.ErrorCode, errorCode)
                            .Set(item => item.UpdatedAtUtc, now),
                        cancellationToken: transactionCt);
                if (receiptUpdate.ModifiedCount != 1)
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_MAPPING_RECONCILE_RECEIPT_PARTIAL_WRITE_FAILED");
                }
            },
            ct);
    }

    private async Task RepairReconciledReceiptAsync(
        DynamicFlowMappingOutboxItem observed,
        CancellationToken ct)
    {
        ValidateImmutableEnvelope(observed);
        await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var terminalOutbox = await _ctx.DynamicFlowMappingOutbox
                    .Find(
                        session,
                        item =>
                            item.Id == observed.Id &&
                            item.ReceiptId == observed.ReceiptId &&
                            item.IntentHash == observed.IntentHash &&
                            item.State ==
                            DynamicFlowMappingOutboxStates.Reconciled)
                    .FirstOrDefaultAsync(transactionCt);
                if (terminalOutbox is null)
                    return;

                ValidateImmutableEnvelope(terminalOutbox);
                var now = terminalOutbox.ReconciledAtUtc ??
                          DateTime.UtcNow;
                var receiptUpdate =
                    await _ctx.DynamicFlowMappingApplyReceipts.UpdateOneAsync(
                        session,
                        BuildReceiptBinding(terminalOutbox),
                        Builders<DynamicFlowMappingApplyReceipt>.Update
                            .Set(
                                item => item.State,
                                DynamicFlowMappingApplyStates.Reconciled)
                            .Set(item => item.ReconciledAtUtc, now)
                            .Set(item => item.UpdatedAtUtc, now)
                            .Unset(item => item.ErrorCode),
                        cancellationToken: transactionCt);
                if (receiptUpdate.ModifiedCount == 1)
                    return;

                var receipt =
                    await _ctx.DynamicFlowMappingApplyReceipts
                        .Find(
                            session,
                            BuildReceiptBinding(terminalOutbox))
                        .FirstOrDefaultAsync(transactionCt);
                if (receipt is null ||
                    !string.Equals(
                        receipt.State,
                        DynamicFlowMappingApplyStates.Reconciled,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_MAPPING_RECONCILED_RECEIPT_REPAIR_FAILED");
                }
            },
            ct);
    }

    private async Task<ReconcileContext> LoadAndValidateIntentAsync(
        DynamicFlowMappingOutboxItem claimed,
        CancellationToken ct)
    {
        ValidateImmutableEnvelope(claimed);
        var intent = claimed.Intent;
        var receipt = await _ctx.DynamicFlowMappingApplyReceipts
            .Find(BuildReceiptBinding(claimed))
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "DYNAMIC_FLOW_MAPPING_RECONCILE_RECEIPT_MISSING");
        var provenance = await _ctx.DynamicFlowMappingProvenanceRecords
            .Find(item =>
                item.Id == intent.ProvenanceId &&
                item.ReceiptId == intent.ReceiptId &&
                item.TargetReportId == intent.TargetReportId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "DYNAMIC_FLOW_MAPPING_RECONCILE_PROVENANCE_MISSING");
        var mappingEvent = await _ctx.DynamicFlowMappingEvents
            .Find(item =>
                item.Id == intent.EventId &&
                item.ReceiptId == intent.ReceiptId &&
                item.ProvenanceId == intent.ProvenanceId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "DYNAMIC_FLOW_MAPPING_RECONCILE_EVENT_MISSING");
        var report = await _ctx.WorkAssignmentReports
            .Find(item =>
                item.Id == intent.TargetReportId &&
                !item.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "DYNAMIC_FLOW_MAPPING_RECONCILE_REPORT_MISSING");
        var payload = await _ctx.WorkReportPayloads
            .Find(item =>
                item.ReportId == intent.TargetReportId &&
                !item.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "DYNAMIC_FLOW_MAPPING_RECONCILE_PAYLOAD_MISSING");

        var writeSetHash = ComputeObjectHash(
            new
            {
                receiptId = receipt.Id,
                provenanceId = provenance.Id,
                eventId = mappingEvent.Id,
                eventKey = mappingEvent.EventKey,
                eventPayloadHash = mappingEvent.PayloadHash,
                outboxId = claimed.Id,
                outboxDedupeKey = claimed.DedupeKey,
                intentHash = claimed.IntentHash,
                targetReportId = report.Id,
                resultPayloadRevision = receipt.ResultPayloadRevision,
                resultPayloadHash = receipt.ResultPayloadHash,
                resultLifecycleRevision =
                    receipt.ResultLifecycleRevision,
                provenanceHash = provenance.ProvenanceHash
            });
        var immutablePinsValid =
            string.Equals(
                ComputeObjectHash(receipt.RuntimePin),
                ComputeObjectHash(provenance.RuntimePin),
                StringComparison.Ordinal) &&
            string.Equals(
                ComputeObjectHash(receipt.SourcePins),
                ComputeObjectHash(provenance.SourcePins),
                StringComparison.Ordinal) &&
            provenance.SourcePins.All(pin =>
                string.Equals(
                    pin.SourceFactHash,
                    ComputeSourceFactHash(pin),
                    StringComparison.Ordinal));
        var durableHashesValid =
            string.Equals(
                receipt.ResultSnapshotHash,
                ComputeDocumentHash(receipt.ResultSnapshot),
                StringComparison.Ordinal) &&
            string.Equals(
                provenance.ResultSnapshotHash,
                ComputeDocumentHash(provenance.ResultSnapshot),
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ResultSnapshotHash,
                provenance.ResultSnapshotHash,
                StringComparison.Ordinal) &&
            string.Equals(
                provenance.ProvenanceHash,
                ComputeDocumentHash(provenance.ProvenanceSnapshot),
                StringComparison.Ordinal) &&
            string.Equals(
                mappingEvent.PayloadHash,
                ComputeDocumentHash(mappingEvent.Payload),
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.WriteSetHash,
                writeSetHash,
                StringComparison.Ordinal);
        var referencesValid =
            string.Equals(
                receipt.ActorUserId,
                intent.ActorUserId,
                StringComparison.Ordinal) &&
            string.Equals(
                mappingEvent.ActorUserId,
                intent.ActorUserId,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ProvenanceId,
                provenance.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ProvenanceHash,
                provenance.ProvenanceHash,
                StringComparison.Ordinal) &&
            string.Equals(
                intent.ProvenanceHash,
                provenance.ProvenanceHash,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.SourceSignature,
                provenance.SourceSignature,
                StringComparison.Ordinal) &&
            string.Equals(
                intent.SourceSignature,
                provenance.SourceSignature,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ResultSemanticHash,
                provenance.ResultSemanticHash,
                StringComparison.Ordinal) &&
            string.Equals(
                intent.ResultSemanticHash,
                provenance.ResultSemanticHash,
                StringComparison.Ordinal);
        var resultValid =
            receipt.ResultPayloadRevision ==
            intent.TargetPayloadRevision &&
            receipt.ResultPayloadRevision ==
            provenance.TargetPayloadRevision &&
            receipt.ResultPayloadRevision ==
            report.DynamicFlowMappingResultPayloadRevision &&
            string.Equals(
                receipt.ResultPayloadHash,
                intent.TargetPayloadHash,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ResultPayloadHash,
                provenance.TargetPayloadHash,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ResultPayloadHash,
                report.DynamicFlowMappingResultPayloadHash,
                StringComparison.Ordinal) &&
            receipt.ResultLifecycleRevision ==
            intent.TargetLifecycleRevision &&
            receipt.ResultLifecycleRevision ==
            provenance.TargetLifecycleRevision &&
            report.PayloadRevision >= intent.TargetPayloadRevision &&
            (report.PayloadRevision != intent.TargetPayloadRevision ||
             string.Equals(
                 report.PayloadHash,
                 intent.TargetPayloadHash,
                 StringComparison.Ordinal)) &&
            report.LifecycleRevision ==
            intent.TargetLifecycleRevision &&
            string.Equals(
                report.WorkAssignmentId,
                intent.TargetAssignmentId,
                StringComparison.Ordinal) &&
            string.Equals(
                report.DynamicFlowMappingReceiptId,
                intent.ReceiptId,
                StringComparison.Ordinal) &&
            string.Equals(
                report.DynamicFlowMappingProvenanceId,
                intent.ProvenanceId,
                StringComparison.Ordinal) &&
            string.Equals(
                report.DynamicFlowMappingProvenanceHash,
                intent.ProvenanceHash,
                StringComparison.Ordinal) &&
            payload.PayloadRevision == report.PayloadRevision &&
            string.Equals(
                payload.PayloadHash,
                report.PayloadHash,
                StringComparison.Ordinal) &&
            string.Equals(
                payload.Status,
                WorkReportPayloadStatus.Ready,
                StringComparison.Ordinal) &&
            IsDynamicFlowMappingSummary(payload.SummarySourceJson);
        var selectedProvenanceStateValid =
            provenance.State is
                DynamicFlowMappingProvenanceStates.Current or
                DynamicFlowMappingProvenanceStates.Invalidated;
        if (!durableHashesValid ||
            !immutablePinsValid ||
            !referencesValid ||
            !resultValid ||
            !selectedProvenanceStateValid)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_MAPPING_RECONCILE_COMMITTED_INTENT_INVALID");
        }

        var periodId = RequiredSnapshotString(
            intent.ProjectionSnapshot,
            "workReportPeriodId");
        if (!string.Equals(
                report.WorkReportPeriodId,
                periodId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_MAPPING_RECONCILE_PERIOD_BINDING_MISMATCH");
        }

        var period = await _ctx.WorkReportPeriods
            .Find(item =>
                item.Id == periodId &&
                item.WorkAssignmentId == intent.TargetAssignmentId &&
                !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        return new ReconcileContext(
            intent.ActorUserId,
            periodId,
            period);
    }

    private static void ValidateImmutableEnvelope(
        DynamicFlowMappingOutboxItem outbox)
    {
        var intent = outbox.Intent ??
                     throw new InvalidOperationException(
                         "DYNAMIC_FLOW_MAPPING_RECONCILE_INTENT_MISSING");
        if (!ObjectId.TryParse(intent.ActorUserId, out _) ||
            !string.Equals(
                intent.SchemaVersion,
                DynamicFlowMappingPersistenceSchema
                    .ReconcileIntentVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                outbox.ReceiptId,
                intent.ReceiptId,
                StringComparison.Ordinal) ||
            !string.Equals(
                outbox.EventId,
                intent.EventId,
                StringComparison.Ordinal) ||
            !string.Equals(
                outbox.ProvenanceId,
                intent.ProvenanceId,
                StringComparison.Ordinal) ||
            !string.Equals(
                outbox.TargetReportId,
                intent.TargetReportId,
                StringComparison.Ordinal) ||
            !string.Equals(
                outbox.CommandId,
                intent.CommandId,
                StringComparison.Ordinal) ||
            !string.Equals(
                outbox.IntentHash,
                ComputeIntentHash(intent),
                StringComparison.Ordinal) ||
            !string.Equals(
                intent.ProjectionSnapshotHash,
                ComputeDocumentHash(intent.ProjectionSnapshot),
                StringComparison.Ordinal) ||
            !string.Equals(
                intent.AuditSnapshotHash,
                ComputeDocumentHash(intent.AuditSnapshot),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_MAPPING_RECONCILE_INTENT_HASH_MISMATCH");
        }
    }

    private static void ValidateProjectorPlan(
        DynamicFlowMappingOutboxItem claimed,
        IReadOnlyCollection<
            DynamicFlowMappingProjectorCheckpoint> expected)
    {
        var checkpoints = claimed.ProjectorCheckpoints ??
                          throw new InvalidOperationException(
                              "DYNAMIC_FLOW_MAPPING_PROJECTOR_PLAN_MISSING");
        if (checkpoints.Count != expected.Count ||
            checkpoints
                .GroupBy(
                    item => item.Projector,
                    StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_MAPPING_PROJECTOR_PLAN_INVALID");
        }

        var expectedByProjector = expected.ToDictionary(
            item => item.Projector,
            StringComparer.Ordinal);
        foreach (var checkpoint in checkpoints)
        {
            if (!expectedByProjector.TryGetValue(
                    checkpoint.Projector,
                    out var expectedCheckpoint) ||
                !string.Equals(
                    checkpoint.BusinessKey,
                    expectedCheckpoint.BusinessKey,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    checkpoint.IdempotencyKey,
                    expectedCheckpoint.IdempotencyKey,
                    StringComparison.Ordinal) ||
                checkpoint.AttemptCount < 0)
            {
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_MAPPING_PROJECTOR_IDENTITY_MISMATCH");
            }

            if (string.Equals(
                    checkpoint.State,
                    DynamicFlowMappingProjectorCheckpointStates.Pending,
                    StringComparison.Ordinal))
            {
                if (checkpoint.ActiveRepairEpoch is not null ||
                    checkpoint.CompletedAtUtc is not null ||
                    checkpoint.CompletedRepairEpoch is not null ||
                    checkpoint.CompletionHash is not null)
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_MAPPING_PROJECTOR_PROGRESS_INVALID");
                }

                continue;
            }

            if (string.Equals(
                    checkpoint.State,
                    DynamicFlowMappingProjectorCheckpointStates.Processing,
                    StringComparison.Ordinal))
            {
                if (checkpoint.ActiveRepairEpoch is not > 0 ||
                    checkpoint.ActiveRepairEpoch >
                    claimed.RepairEpoch ||
                    checkpoint.StartedAtUtc is null ||
                    checkpoint.CompletedAtUtc is not null ||
                    checkpoint.CompletedRepairEpoch is not null ||
                    checkpoint.CompletionHash is not null)
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_MAPPING_PROJECTOR_PROGRESS_INVALID");
                }

                continue;
            }

            if (!string.Equals(
                    checkpoint.State,
                    DynamicFlowMappingProjectorCheckpointStates.Completed,
                    StringComparison.Ordinal) ||
                checkpoint.ActiveRepairEpoch is not null ||
                checkpoint.CompletedAtUtc is null ||
                checkpoint.CompletedRepairEpoch is not > 0 ||
                checkpoint.CompletedRepairEpoch >
                claimed.RepairEpoch ||
                !string.Equals(
                    checkpoint.CompletionHash,
                    DynamicFlowMappingProjectorContract
                        .ComputeCompletionHash(
                            claimed.IntentHash,
                            checkpoint,
                            checkpoint.CompletedRepairEpoch.Value),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_MAPPING_PROJECTOR_PROGRESS_INVALID");
            }
        }
    }

    private static FilterDefinition<DynamicFlowMappingOutboxItem>
        BuildProjectorIdentityFilter(
            DynamicFlowMappingProjectorCheckpoint checkpoint,
            bool requireIncomplete)
    {
        var checkpointFilter =
            Builders<DynamicFlowMappingProjectorCheckpoint>.Filter
                .Eq(item => item.Projector, checkpoint.Projector) &
            Builders<DynamicFlowMappingProjectorCheckpoint>.Filter
                .Eq(item => item.BusinessKey, checkpoint.BusinessKey) &
            Builders<DynamicFlowMappingProjectorCheckpoint>.Filter
                .Eq(
                    item => item.IdempotencyKey,
                    checkpoint.IdempotencyKey);
        if (requireIncomplete)
        {
            checkpointFilter &=
                Builders<DynamicFlowMappingProjectorCheckpoint>
                    .Filter.Ne(
                        item => item.State,
                        DynamicFlowMappingProjectorCheckpointStates
                            .Completed);
        }

        return Builders<DynamicFlowMappingOutboxItem>.Filter
            .ElemMatch(
                item => item.ProjectorCheckpoints,
                checkpointFilter);
    }

    private static FilterDefinition<DynamicFlowMappingOutboxItem>
        BuildProjectorCompletionFence(
            DynamicFlowMappingProjectorCheckpoint checkpoint,
            long repairEpoch)
    {
        var checkpointFilter =
            Builders<DynamicFlowMappingProjectorCheckpoint>.Filter
                .Eq(item => item.Projector, checkpoint.Projector) &
            Builders<DynamicFlowMappingProjectorCheckpoint>.Filter
                .Eq(item => item.BusinessKey, checkpoint.BusinessKey) &
            Builders<DynamicFlowMappingProjectorCheckpoint>.Filter
                .Eq(
                    item => item.IdempotencyKey,
                    checkpoint.IdempotencyKey) &
            Builders<DynamicFlowMappingProjectorCheckpoint>.Filter
                .Eq(
                    item => item.State,
                    DynamicFlowMappingProjectorCheckpointStates
                        .Processing) &
            Builders<DynamicFlowMappingProjectorCheckpoint>.Filter
                .Eq(
                    item => item.ActiveRepairEpoch,
                    repairEpoch);
        return Builders<DynamicFlowMappingOutboxItem>.Filter
            .ElemMatch(
                item => item.ProjectorCheckpoints,
                checkpointFilter);
    }

    private static FilterDefinition<DynamicFlowMappingOutboxItem>
        BuildAllProjectorsCompletedFilter(
            DynamicFlowMappingOutboxItem claimed)
    {
        var fb = Builders<DynamicFlowMappingOutboxItem>.Filter;
        var completed = fb.Size(
            item => item.ProjectorCheckpoints,
            3);
        foreach (var checkpoint in claimed.ProjectorCheckpoints)
        {
            var checkpointFilter =
                Builders<DynamicFlowMappingProjectorCheckpoint>.Filter
                    .Eq(
                        item => item.Projector,
                        checkpoint.Projector) &
                Builders<DynamicFlowMappingProjectorCheckpoint>.Filter
                    .Eq(
                        item => item.BusinessKey,
                        checkpoint.BusinessKey) &
                Builders<DynamicFlowMappingProjectorCheckpoint>.Filter
                    .Eq(
                        item => item.IdempotencyKey,
                        checkpoint.IdempotencyKey) &
                Builders<DynamicFlowMappingProjectorCheckpoint>.Filter
                    .Eq(
                        item => item.State,
                        DynamicFlowMappingProjectorCheckpointStates
                            .Completed) &
                Builders<DynamicFlowMappingProjectorCheckpoint>.Filter
                    .Eq(
                        item => item.CompletedRepairEpoch,
                        checkpoint.CompletedRepairEpoch) &
                Builders<DynamicFlowMappingProjectorCheckpoint>.Filter
                    .Eq(
                        item => item.CompletionHash,
                        checkpoint.CompletionHash);
            completed &= fb.ElemMatch(
                item => item.ProjectorCheckpoints,
                checkpointFilter);
        }

        return completed;
    }

    private static FilterDefinition<DynamicFlowMappingOutboxItem>
        BuildFence(DynamicFlowMappingOutboxItem claimed)
        => BuildFence(claimed, DateTime.UtcNow);

    private static FilterDefinition<DynamicFlowMappingOutboxItem>
        BuildFence(
            DynamicFlowMappingOutboxItem claimed,
            DateTime now)
    {
        var fb = Builders<DynamicFlowMappingOutboxItem>.Filter;
        return fb.Eq(item => item.Id, claimed.Id) &
               fb.Eq(item => item.ReceiptId, claimed.ReceiptId) &
               fb.Eq(item => item.IntentHash, claimed.IntentHash) &
               fb.Eq(
                   item => item.State,
                   DynamicFlowMappingOutboxStates.Processing) &
               fb.Eq(item => item.LeaseId, claimed.LeaseId) &
               fb.Eq(item => item.RepairEpoch, claimed.RepairEpoch) &
               fb.Gt("leaseUntilUtc", now);
    }

    private static FilterDefinition<DynamicFlowMappingApplyReceipt>
        BuildReceiptBinding(DynamicFlowMappingOutboxItem outbox)
    {
        var fb = Builders<DynamicFlowMappingApplyReceipt>.Filter;
        return fb.Eq(item => item.Id, outbox.ReceiptId) &
               fb.Eq(item => item.OutboxIntentId, outbox.Id) &
               fb.Eq(item => item.TargetReportId, outbox.TargetReportId) &
               fb.Eq(item => item.CommandId, outbox.CommandId) &
               fb.Eq(item => item.ProvenanceId, outbox.ProvenanceId);
    }

    private static string RequiredSnapshotString(
        BsonDocument snapshot,
        string field)
    {
        if (snapshot.TryGetValue(field, out var value) &&
            value.IsString &&
            !string.IsNullOrWhiteSpace(value.AsString))
        {
            return value.AsString;
        }

        throw new InvalidOperationException(
            $"DYNAMIC_FLOW_MAPPING_RECONCILE_SNAPSHOT_FIELD_MISSING:{field}");
    }

    private static bool IsDynamicFlowMappingSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind ==
                   JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(
                       "kind",
                       out var kind) &&
                   kind.ValueKind == JsonValueKind.String &&
                   string.Equals(
                       kind.GetString(),
                       "DYNAMIC_FLOW_MAPPING",
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ComputeDocumentHash(BsonDocument document)
        => DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(
            document.ToJson(
                new JsonWriterSettings
                {
                    OutputMode = JsonOutputMode.RelaxedExtendedJson
                }));

    private static string ComputeIntentHash(
        DynamicFlowMappingReconcileIntent intent)
        => ComputeDocumentHash(intent.ToBsonDocument());

    private static string ComputeObjectHash(object value)
        => DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(
            JsonSerializer.Serialize(value, JsonOptions));

    private static string ComputeSourceFactHash(
        DynamicFlowMappingSourcePin pin)
        => ComputeObjectHash(
            new
            {
                sourceReportId = pin.SourceReportId,
                sourceAssignmentId = pin.SourceAssignmentId,
                sourceFlowInstanceId = pin.SourceFlowInstanceId,
                sourceExecutionEpoch = pin.SourceExecutionEpoch,
                sourceStepInstanceId = pin.SourceStepInstanceId,
                sourceStepId = pin.SourceStepId,
                sourceBranchId = pin.SourceBranchId,
                sourceAttemptNo = pin.SourceAttemptNo,
                sourceFormFamilyId = pin.SourceFormFamilyId,
                sourceFormVersionId = pin.SourceFormVersionId,
                sourceFormVersionNo = pin.SourceFormVersionNo,
                sourceFormSchemaHash = pin.SourceFormSchemaHash,
                sourcePayloadRevision = pin.SourcePayloadRevision,
                sourcePayloadHash = pin.SourcePayloadHash,
                sourceLifecycleRevision = pin.SourceLifecycleRevision,
                sourceLifecycleStatus = pin.SourceLifecycleStatus,
                sourcePeriodInstanceKey = pin.SourcePeriodInstanceKey
            });

    private sealed record ReconcileContext(
        string ActorUserId,
        string PeriodId,
        WorkReportPeriod? Period);

    private sealed record ProjectorPlan(
        DynamicFlowMappingProjectorCheckpoint QueuePeriod,
        DynamicFlowMappingProjectorCheckpoint AssignmentStatus,
        DynamicFlowMappingProjectorCheckpoint DocRolePeriod);
}
