using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models.StatisticsConfiguration;

namespace tdtd_be.Services.StatisticsConfiguration;

public sealed partial class StatConfigOperationsService
{
    public Task<StatConfigValidationJobStatusResponse> ResetAsync(
        string jobId,
        StatConfigMutationEnvelope<StatConfigValidationResetPayload> request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RequireSystemAdmin(actor);
        jobId = RequireObjectId(jobId, "$.jobId");
        var command = StatConfigCanonicalJson.NormalizeCommand(
            request,
            ResetCommandKind);
        var reason = NormalizeReason(
            command.Payload.Reason,
            "$.payload.reason");
        var requestHash = StatConfigCanonicalJson.HashObject(new
        {
            commandKind = ResetCommandKind,
            jobId,
            expectedRevision = command.ExpectedRevision,
            expectedConfigHash = command.ExpectedConfigHash,
            payload = new { reason }
        });
        return ChangeStateAsync(
            jobId,
            ResetCommandKind,
            command.CommandId,
            command.ExpectedRevision,
            command.ExpectedConfigHash,
            requestHash,
            reason,
            reset: true,
            actor,
            ct);
    }

    public Task<StatConfigValidationJobStatusResponse> CancelAsync(
        string jobId,
        StatConfigMutationEnvelope<StatConfigValidationCancelPayload> request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RequireSystemAdmin(actor);
        jobId = RequireObjectId(jobId, "$.jobId");
        var command = StatConfigCanonicalJson.NormalizeCommand(
            request,
            CancelCommandKind);
        var reason = NormalizeReason(
            command.Payload.Reason,
            "$.payload.reason");
        var requestHash = StatConfigCanonicalJson.HashObject(new
        {
            commandKind = CancelCommandKind,
            jobId,
            expectedRevision = command.ExpectedRevision,
            expectedConfigHash = command.ExpectedConfigHash,
            payload = new { reason }
        });
        return ChangeStateAsync(
            jobId,
            CancelCommandKind,
            command.CommandId,
            command.ExpectedRevision,
            command.ExpectedConfigHash,
            requestHash,
            reason,
            reset: false,
            actor,
            ct);
    }

    public async Task<StatConfigValidationCleanupResponse> CleanupAsync(
        StatConfigMutationEnvelope<StatConfigValidationCleanupPayload> request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RequireSystemAdmin(actor);
        var command = StatConfigCanonicalJson.NormalizeCommand(
            request,
            CleanupCommandKind);
        if (command.ExpectedRevision != 0 ||
            !string.Equals(
                command.ExpectedConfigHash,
                StatConfigCanonicalJson.EmptyConfigHash,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new
                {
                    expectedRevision = 0,
                    expectedConfigHash =
                        StatConfigCanonicalJson.EmptyConfigHash
                });
        }

        var limit = Math.Clamp(command.Payload.Limit, 1, 500);
        if (limit != command.Payload.Limit)
            throw SchemaError("$.payload.limit", "RANGE_1_500_REQUIRED");
        var cutoff = command.Payload.CompletedBeforeUtc ?? DateTime.UtcNow;
        cutoff = cutoff.Kind == DateTimeKind.Utc
            ? cutoff
            : cutoff.ToUniversalTime();
        if (cutoff > DateTime.UtcNow.AddMinutes(1))
            throw SchemaError("$.payload.completedBeforeUtc", "FUTURE_NOT_ALLOWED");
        var payload = new StatConfigValidationCleanupPayload(
            cutoff,
            limit,
            command.Payload.DryRun);
        var requestHash = StatConfigCanonicalJson.HashObject(new
        {
            commandKind = CleanupCommandKind,
            ownerKind = QueueOwnerKind,
            ownerId = StatConfigValidationQueue.Name,
            expectedRevision = command.ExpectedRevision,
            expectedConfigHash = command.ExpectedConfigHash,
            payload
        });
        var receiptId = ReceiptId(
            QueueOwnerKind,
            StatConfigValidationQueue.Name,
            command.CommandId);
        var outsideReplay = await FindReceiptAsync(receiptId, ct);
        if (outsideReplay is not null)
        {
            return RestoreReceipt<StatConfigValidationCleanupResponse>(
                outsideReplay,
                CleanupCommandKind,
                requestHash,
                actor.Id);
        }

        try
        {
            return await _transactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    var replay = await FindReceiptAsync(
                        session,
                        receiptId,
                        transactionCt);
                    if (replay is not null)
                    {
                        return RestoreReceipt<
                            StatConfigValidationCleanupResponse>(
                                replay,
                                CleanupCommandKind,
                                requestHash,
                                actor.Id);
                    }

                    var terminal = new[]
                    {
                        StatConfigValidationJobStatuses.Completed,
                        StatConfigValidationJobStatuses.Failed,
                        StatConfigValidationJobStatuses.Cancelled
                    };
                    var filter = Builders<StatConfigValidationJob>.Filter.And(
                        Builders<StatConfigValidationJob>.Filter.Eq(
                            item => item.QueueName,
                            StatConfigValidationQueue.Name),
                        Builders<StatConfigValidationJob>.Filter.Eq(
                            item => item.IsDeleted,
                            false),
                        Builders<StatConfigValidationJob>.Filter.Eq(
                            item => item.IsActive,
                            false),
                        Builders<StatConfigValidationJob>.Filter.In(
                            item => item.Status,
                            terminal),
                        Builders<StatConfigValidationJob>.Filter.Ne(
                            item => item.ExpiresAtUtc,
                            null),
                        Builders<StatConfigValidationJob>.Filter.Lte(
                            item => item.ExpiresAtUtc,
                            cutoff));
                    var matched = await _ctx.StatConfigValidationJobs
                        .CountDocumentsAsync(
                            session,
                            filter,
                            cancellationToken: transactionCt);
                    var selected = await _ctx.StatConfigValidationJobs
                        .Find(session, filter)
                        .SortBy(item => item.ExpiresAtUtc)
                        .ThenBy(item => item.Id)
                        .Limit(limit)
                        .ToListAsync(transactionCt);
                    var selectedIds = selected
                        .Select(item => item.Id)
                        .ToArray();

                    _faults.ThrowIfConfigured(
                        command.CommandId,
                        StatConfigOperationsFaultPoints.CleanupTransient);

                    long deleted = 0;
                    if (!payload.DryRun && selectedIds.Length > 0)
                    {
                        var deleteResult = await _ctx
                            .StatConfigValidationJobs.DeleteManyAsync(
                                session,
                                Builders<StatConfigValidationJob>.Filter.In(
                                    item => item.Id,
                                    selectedIds),
                                cancellationToken: transactionCt);
                        deleted = deleteResult.DeletedCount;
                    }

                    var response = new StatConfigValidationCleanupResponse(
                        true,
                        payload.DryRun,
                        command.CommandId,
                        limit,
                        matched,
                        selectedIds.Length,
                        deleted,
                        matched > selectedIds.Length,
                        selectedIds);
                    var responseJson =
                        StatConfigCanonicalJson.Canonicalize(response);
                    var now = DateTime.UtcNow;
                    var receipt = new StatConfigCommandReceipt
                    {
                        Id = receiptId,
                        OwnerKind = QueueOwnerKind,
                        OwnerId = StatConfigValidationQueue.Name,
                        CommandKind = CleanupCommandKind,
                        CommandId = command.CommandId,
                        RequestHash = requestHash,
                        ResponseJson = responseJson,
                        ResponseHash =
                            StatConfigCanonicalJson.HashUtf8(responseJson),
                        ResultConfigId = string.Empty,
                        ResultVersionId = string.Empty,
                        ResultVersionNo = 0,
                        ResultRevision = 0,
                        ResultStatus = "COMPLETED",
                        ResultConfigHash =
                            StatConfigCanonicalJson.EmptyConfigHash,
                        ActorUserId = actor.Id,
                        CreatedAtUtc = now
                    };
                    var outbox = NewCleanupOutbox(
                        command.CommandId,
                        receiptId,
                        requestHash,
                        selectedIds,
                        actor.Id,
                        now);
                    await _ctx.StatConfigCommandReceipts.InsertOneAsync(
                        session,
                        receipt,
                        cancellationToken: transactionCt);
                    await _ctx.StatConfigAuditOutbox.InsertOneAsync(
                        session,
                        outbox,
                        cancellationToken: transactionCt);
                    return response;
                },
                ct);
        }
        catch (Exception error) when (IsDuplicateKey(error))
        {
            var replay = await WaitForReceiptAsync(receiptId, ct);
            if (replay is null)
                throw;
            return RestoreReceipt<StatConfigValidationCleanupResponse>(
                replay,
                CleanupCommandKind,
                requestHash,
                actor.Id);
        }
    }

    private async Task<StatConfigValidationJobStatusResponse>
        ChangeStateAsync(
            string jobId,
            string commandKind,
            string commandId,
            long expectedRevision,
            string expectedStateHash,
            string requestHash,
            string? reason,
            bool reset,
            MeResponse actor,
            CancellationToken ct)
    {
        var receiptId = ReceiptId(JobOwnerKind, jobId, commandId);
        var outsideReplay = await FindReceiptAsync(receiptId, ct);
        if (outsideReplay is not null)
        {
            return RestoreReceipt<StatConfigValidationJobStatusResponse>(
                outsideReplay,
                commandKind,
                requestHash,
                actor.Id);
        }

        try
        {
            return await _transactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    var replay = await FindReceiptAsync(
                        session,
                        receiptId,
                        transactionCt);
                    if (replay is not null)
                    {
                        return RestoreReceipt<
                            StatConfigValidationJobStatusResponse>(
                                replay,
                                commandKind,
                                requestHash,
                                actor.Id);
                    }

                    var job = await _ctx.StatConfigValidationJobs
                        .Find(
                            session,
                            item => item.Id == jobId && !item.IsDeleted)
                        .FirstOrDefaultAsync(transactionCt)
                        ?? throw JobNotFound(jobId);
                    if (job.StateRevision != expectedRevision ||
                        !string.Equals(
                            job.StateHash,
                            expectedStateHash,
                            StringComparison.Ordinal))
                    {
                        throw AppExceptionFactory.Create(
                            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                            new
                            {
                                expectedRevision,
                                expectedStateHash,
                                actualRevision = job.StateRevision,
                                actualStateHash = job.StateHash
                            });
                    }

                    var priorRevision = job.StateRevision;
                    var priorStateHash = job.StateHash;
                    var now = DateTime.UtcNow;
                    job.StateRevision++;
                    job.UpdatedAtUtc = now;
                    job.UpdatedByUserId = actor.Id;
                    job.ClaimToken = null;
                    job.LeaseOwnerId = null;
                    job.LeaseUntilUtc = null;
                    job.NextRetryAtUtc = null;
                    if (reset)
                    {
                        job.Status = StatConfigValidationJobStatuses.Reset;
                        job.IsActive = true;
                        job.RetryCount = 0;
                        job.CompletedAtUtc = null;
                        job.FailedAtUtc = null;
                        job.DeadLetterAtUtc = null;
                        job.CancelledAtUtc = null;
                        job.CancelledByUserId = null;
                        job.ResetAtUtc = now;
                        job.ResetByUserId = actor.Id;
                        job.ResetCount++;
                        job.SafeCode = null;
                        job.SafeMessage = null;
                        job.DiagnosticCode = null;
                        job.DiagnosticMessage = null;
                        job.FailureFingerprint = null;
                        job.ExpiresAtUtc = null;
                    }
                    else
                    {
                        job.Status = StatConfigValidationJobStatuses.Cancelled;
                        job.IsActive = false;
                        job.CancelledAtUtc = now;
                        job.CancelledByUserId = actor.Id;
                        job.SafeCode = "CANCELLED_BY_ADMIN";
                        job.SafeMessage = "Readiness validation was cancelled.";
                        job.DiagnosticCode = "CANCELLED_BY_ADMIN";
                        job.DiagnosticMessage = Truncate(reason, 500);
                        job.FailureFingerprint = null;
                        job.ExpiresAtUtc = now.Add(_terminalTtl);
                    }
                    job.StateHash = ComputeStateHash(job);

                    var response = ToSafeStatus(job);
                    var responseJson =
                        StatConfigCanonicalJson.Canonicalize(response);
                    var receipt = NewReceipt(
                        receiptId,
                        JobOwnerKind,
                        jobId,
                        commandKind,
                        commandId,
                        requestHash,
                        responseJson,
                        job,
                        actor.Id,
                        now);
                    var outboxId = StatConfigCanonicalJson.HashUtf8(
                        $"{JobTargetKind}\0{jobId}\0{commandId}");
                    var outbox = NewOutbox(
                        outboxId,
                        job.DedupeKey,
                        JobOwnerKind,
                        jobId,
                        commandKind,
                        commandId,
                        receiptId,
                        job,
                        reset
                            ? "STAT_CONFIG_VALIDATION_RESET"
                            : "STAT_CONFIG_VALIDATION_CANCELLED",
                        requestHash,
                        reset
                            ? "Configuration readiness validation reset."
                            : "Configuration readiness validation cancelled.",
                        actor.Id,
                        now);
                    if (reason is not null)
                        outbox.Note = reason;

                    var stateFilter =
                        Builders<StatConfigValidationJob>.Filter.And(
                            Builders<StatConfigValidationJob>.Filter.Eq(
                                item => item.Id,
                                jobId),
                            Builders<StatConfigValidationJob>.Filter.Eq(
                                item => item.StateRevision,
                                priorRevision),
                            Builders<StatConfigValidationJob>.Filter.Eq(
                                item => item.StateHash,
                                priorStateHash),
                            Builders<StatConfigValidationJob>.Filter.Eq(
                                item => item.IsDeleted,
                                false));
                    var replace = await _ctx.StatConfigValidationJobs
                        .ReplaceOneAsync(
                            session,
                            stateFilter,
                            job,
                            cancellationToken: transactionCt);
                    if (replace.ModifiedCount != 1)
                    {
                        throw AppExceptionFactory.Create(
                            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                            new { reason = "READINESS_STATE_CHANGED" });
                    }
                    await _ctx.StatConfigCommandReceipts.InsertOneAsync(
                        session,
                        receipt,
                        cancellationToken: transactionCt);
                    await _ctx.StatConfigAuditOutbox.InsertOneAsync(
                        session,
                        outbox,
                        cancellationToken: transactionCt);
                    return response;
                },
                ct);
        }
        catch (Exception error) when (IsDuplicateKey(error))
        {
            var replay = await WaitForReceiptAsync(receiptId, ct);
            if (replay is null)
                throw;
            return RestoreReceipt<StatConfigValidationJobStatusResponse>(
                replay,
                commandKind,
                requestHash,
                actor.Id);
        }
    }

    private static StatConfigAuditOutboxItem NewCleanupOutbox(
        string commandId,
        string receiptId,
        string payloadHash,
        IReadOnlyList<string> jobIds,
        string actorId,
        DateTime now)
        => new()
        {
            Id = StatConfigCanonicalJson.HashUtf8(
                $"{QueueOwnerKind}\0{StatConfigValidationQueue.Name}\0{commandId}"),
            DedupeKey = StatConfigCanonicalJson.HashObject(new
            {
                queue = StatConfigValidationQueue.Name,
                commandId,
                eventKind = "STAT_CONFIG_VALIDATION_CLEANUP"
            }),
            OwnerKind = QueueOwnerKind,
            OwnerId = StatConfigValidationQueue.Name,
            CommandKind = CleanupCommandKind,
            CommandId = commandId,
            ReceiptId = receiptId,
            TargetKind = QueueOwnerKind,
            TargetId = StatConfigValidationQueue.Name,
            EventKind = "STAT_CONFIG_VALIDATION_CLEANUP",
            EventVersion = 1,
            CorrelationId = StatConfigCanonicalJson.HashObject(new
            {
                queue = StatConfigValidationQueue.Name,
                commandId
            }),
            PayloadHash = payloadHash,
            SafeSummary =
                $"Configuration readiness cleanup selected {jobIds.Count} owned job(s).",
            Status = StatConfigAuditOutboxStatuses.Pending,
            AttemptCount = 0,
            CreatedByUserId = actorId,
            UpdatedByUserId = actorId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            IsDeleted = false
        };

    private static string? NormalizeReason(string? reason, string path)
    {
        reason = NormalizeOptional(reason);
        if (reason is { Length: > 500 })
            throw SchemaError(path, "MAX_LENGTH_500");
        return reason;
    }
}
