using MongoDB.Driver;
using tdtd_be.Data.Indexes;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models.StatisticsConfiguration;

namespace tdtd_be.Services.StatisticsConfiguration;

public sealed partial class StatConfigOperationsService
{
    public async Task<StatConfigValidationProcessResponse> ProcessPendingAsync(
        int maxJobs,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RequireSystemAdmin(actor);
        maxJobs = Math.Clamp(maxJobs, 1, 100);

        var claimedCount = 0;
        var completedCount = 0;
        var retryingCount = 0;
        var failedCount = 0;
        var rows = new List<StatConfigValidationJobStatusResponse>();

        for (var index = 0; index < maxJobs; index++)
        {
            ct.ThrowIfCancellationRequested();
            var job = await ClaimNextAsync(ct);
            if (job is null)
                break;

            claimedCount++;
            var outcome = await ProcessClaimAsync(job, ct);
            if (outcome.Job is not null)
                rows.Add(ToSafeStatus(outcome.Job));

            switch (outcome.Kind)
            {
                case WorkerOutcomeKind.Completed:
                    completedCount++;
                    break;
                case WorkerOutcomeKind.Retrying:
                    retryingCount++;
                    break;
                case WorkerOutcomeKind.Failed:
                    failedCount++;
                    break;
            }
        }

        return new StatConfigValidationProcessResponse(
            claimedCount,
            completedCount,
            retryingCount,
            failedCount,
            rows);
    }

    private async Task<StatConfigValidationJob?> ClaimNextAsync(
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var now = DateTime.UtcNow;
            var eligible = EligibleClaimFilter(now);
            var candidate = await _ctx.StatConfigValidationJobs
                .Find(eligible)
                .SortBy(item => item.NextRetryAtUtc)
                .ThenBy(item => item.CreatedAtUtc)
                .ThenBy(item => item.Id)
                .FirstOrDefaultAsync(ct);
            if (candidate is null)
                return null;

            var priorRevision = candidate.StateRevision;
            var priorHash = candidate.StateHash;
            var claimToken = StatConfigCanonicalJson.HashObject(new
            {
                queue = StatConfigValidationQueue.Name,
                candidate.Id,
                worker = _workerId,
                nonce = Guid.NewGuid()
            });
            var leaseUntil = now.AddSeconds(_leaseSeconds);

            candidate.Status = StatConfigValidationJobStatuses.Running;
            candidate.IsActive = true;
            candidate.ClaimToken = claimToken;
            candidate.LeaseOwnerId = _workerId;
            candidate.LeaseUntilUtc = leaseUntil;
            candidate.LastHeartbeatAtUtc = now;
            candidate.LastRunAtUtc = now;
            candidate.NextRetryAtUtc = null;
            candidate.SafeCode = null;
            candidate.SafeMessage = null;
            candidate.UpdatedAtUtc = now;
            candidate.StateRevision = priorRevision + 1;
            candidate.StateHash = ComputeStateHash(candidate);

            var filter = Builders<StatConfigValidationJob>.Filter.And(
                Builders<StatConfigValidationJob>.Filter.Eq(
                    item => item.Id,
                    candidate.Id),
                Builders<StatConfigValidationJob>.Filter.Eq(
                    item => item.StateRevision,
                    priorRevision),
                Builders<StatConfigValidationJob>.Filter.Eq(
                    item => item.StateHash,
                    priorHash),
                EligibleClaimFilter(now));
            var update = Builders<StatConfigValidationJob>.Update
                .Set(item => item.Status, candidate.Status)
                .Set(item => item.IsActive, true)
                .Set(item => item.ClaimToken, claimToken)
                .Set(item => item.LeaseOwnerId, _workerId)
                .Set(item => item.LeaseUntilUtc, leaseUntil)
                .Set(item => item.LastHeartbeatAtUtc, now)
                .Set(item => item.LastRunAtUtc, now)
                .Set(item => item.NextRetryAtUtc, null)
                .Set(item => item.SafeCode, null)
                .Set(item => item.SafeMessage, null)
                .Set(item => item.UpdatedAtUtc, now)
                .Set(item => item.StateRevision, candidate.StateRevision)
                .Set(item => item.StateHash, candidate.StateHash);
            var claimed = await _ctx.StatConfigValidationJobs
                .FindOneAndUpdateAsync(
                    filter,
                    update,
                    new FindOneAndUpdateOptions<StatConfigValidationJob>
                    {
                        ReturnDocument = ReturnDocument.After
                    },
                    ct);
            if (claimed is not null)
                return claimed;
        }

        return null;
    }

    private static FilterDefinition<StatConfigValidationJob>
        EligibleClaimFilter(DateTime now)
    {
        var filter = Builders<StatConfigValidationJob>.Filter;
        return filter.And(
            filter.Eq(item => item.QueueName, StatConfigValidationQueue.Name),
            filter.Eq(item => item.IsDeleted, false),
            filter.Eq(item => item.IsActive, true),
            filter.Or(
                filter.In(
                    item => item.Status,
                    new[]
                    {
                        StatConfigValidationJobStatuses.Pending,
                        StatConfigValidationJobStatuses.Reset
                    }),
                filter.And(
                    filter.Eq(
                        item => item.Status,
                        StatConfigValidationJobStatuses.RetryWaiting),
                    filter.Or(
                        filter.Eq<DateTime?>(
                            item => item.NextRetryAtUtc,
                            null),
                        filter.Lte(item => item.NextRetryAtUtc, now))),
                filter.And(
                    filter.Eq(
                        item => item.Status,
                        StatConfigValidationJobStatuses.Running),
                    filter.Or(
                        filter.Eq<DateTime?>(
                            item => item.LeaseUntilUtc,
                            null),
                        filter.Lte(item => item.LeaseUntilUtc, now)))));
    }

    private async Task<WorkerOutcome> ProcessClaimAsync(
        StatConfigValidationJob claimed,
        CancellationToken ct)
    {
        var heartbeat = await HeartbeatAsync(claimed, ct);
        if (heartbeat is null)
            return new WorkerOutcome(WorkerOutcomeKind.Lost, null);

        try
        {
            using var boundedValidation =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            boundedValidation.CancelAfter(TimeSpan.FromSeconds(
                Math.Max(1, _leaseSeconds / 2)));
            await ValidateClaimAsync(
                heartbeat,
                boundedValidation.Token);
            var completed = await CompleteClaimAsync(heartbeat, ct);
            return new WorkerOutcome(
                completed is null
                    ? WorkerOutcomeKind.Lost
                    : WorkerOutcomeKind.Completed,
                completed);
        }
        catch (OperationCanceledException timeout)
            when (!ct.IsCancellationRequested)
        {
            return await FailClaimAsync(
                heartbeat,
                true,
                "VALIDATION_TIMEOUT",
                "Configuration readiness validation exceeded its bounded lease window.",
                timeout,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ReadinessValidationException validation)
        {
            return await FailClaimAsync(
                heartbeat,
                validation.IsTransient,
                validation.Code,
                validation.Diagnostic,
                validation,
                ct);
        }
        catch (Exception error)
        {
            var transient = IsTransientValidationError(error);
            _logger.LogWarning(
                error,
                "Stat-config readiness validation failed for job {JobId}; transient={Transient}.",
                heartbeat.Id,
                transient);
            return await FailClaimAsync(
                heartbeat,
                transient,
                transient
                    ? "VALIDATION_DEPENDENCY_UNAVAILABLE"
                    : "VALIDATION_FAILED",
                transient
                    ? "A required validation dependency is temporarily unavailable."
                    : "The pinned configuration bundle failed validation.",
                error,
                ct);
        }
    }

    private async Task<StatConfigValidationJob?> HeartbeatAsync(
        StatConfigValidationJob job,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var priorRevision = job.StateRevision;
        var priorHash = job.StateHash;
        job.LastHeartbeatAtUtc = now;
        job.LeaseUntilUtc = now.AddSeconds(_leaseSeconds);
        job.UpdatedAtUtc = now;
        job.StateRevision = priorRevision + 1;
        job.StateHash = ComputeStateHash(job);

        return await _ctx.StatConfigValidationJobs.FindOneAndUpdateAsync(
            FenceFilter(job, priorRevision, priorHash),
            Builders<StatConfigValidationJob>.Update
                .Set(item => item.LastHeartbeatAtUtc, job.LastHeartbeatAtUtc)
                .Set(item => item.LeaseUntilUtc, job.LeaseUntilUtc)
                .Set(item => item.UpdatedAtUtc, now)
                .Set(item => item.StateRevision, job.StateRevision)
                .Set(item => item.StateHash, job.StateHash),
            new FindOneAndUpdateOptions<StatConfigValidationJob>
            {
                ReturnDocument = ReturnDocument.After
            },
            ct);
    }

    private async Task ValidateClaimAsync(
        StatConfigValidationJob job,
        CancellationToken ct)
    {
        InjectValidationFault(
            job.EnqueueCommandId,
            StatConfigOperationsFaultPoints.ValidationTransient,
            true,
            "VALIDATION_TRANSIENT",
            "A validation dependency is temporarily unavailable.");
        InjectValidationFault(
            job.EnqueueCommandId,
            StatConfigOperationsFaultPoints.ValidationPermanent,
            false,
            "VALIDATION_PERMANENT",
            "The pinned configuration bundle is invalid.");

        if (!string.Equals(
                job.QueueName,
                StatConfigValidationQueue.Name,
                StringComparison.Ordinal))
        {
            throw PermanentValidation(
                "VALIDATION_QUEUE_MISMATCH",
                "The validation queue identity is invalid.");
        }

        var statuses = await MongoIndexInitializer
            .ValidateStatConfigIndexesAsync(_ctx.Db, _ctx.Options, ct);
        if (statuses.Any(item => !item.IsExact))
        {
            throw PermanentValidation(
                "INDEX_CONFIGURATION_INVALID",
                "Required readiness indexes do not match the owned contract.");
        }

        var receiptFilter = Builders<StatConfigCommandReceipt>.Filter;
        var source = await _ctx.StatConfigCommandReceipts
            .Find(receiptFilter.And(
                receiptFilter.Eq(item => item.OwnerKind, job.OwnerKind),
                receiptFilter.Eq(item => item.OwnerId, job.OwnerId),
                receiptFilter.Eq(item => item.ResultConfigId, job.ConfigId),
                receiptFilter.Eq(item => item.ResultVersionId, job.VersionId),
                receiptFilter.Eq(item => item.ResultVersionNo, job.VersionNo),
                receiptFilter.Eq(
                    item => item.ResultRevision,
                    job.ConfigRevision),
                receiptFilter.Eq(
                    item => item.ResultConfigHash,
                    job.ConfigHash),
                receiptFilter.Nin(
                    item => item.CommandKind,
                    new[]
                    {
                        EnqueueCommandKind,
                        ResetCommandKind,
                        CancelCommandKind,
                        CleanupCommandKind
                    })))
            .SortByDescending(item => item.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (source is null)
        {
            throw PermanentValidation(
                "VALIDATION_PINNED_SOURCE_MISSING",
                "The pinned configuration identity is unavailable.");
        }
        if (!string.Equals(
                source.ResponseHash,
                StatConfigCanonicalJson.HashUtf8(source.ResponseJson),
                StringComparison.Ordinal))
        {
            throw PermanentValidation(
                "VALIDATION_PINNED_SOURCE_INTEGRITY",
                "The pinned configuration identity failed integrity validation.");
        }

        var pins = ExtractDependencyPins(source.ResponseJson);
        var pinsHash = StatConfigCanonicalJson.HashObject(pins);
        var storedPinsCanonical = job.DependencyPins
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        if (!storedPinsCanonical.SequenceEqual(pins, StringComparer.Ordinal) ||
            !string.Equals(
                pinsHash,
                job.DependencyPinsHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                ComputeBundleHash(
                    job.OwnerKind,
                    job.OwnerId,
                    source,
                    pinsHash),
                job.BundleHash,
                StringComparison.Ordinal))
        {
            throw PermanentValidation(
                "VALIDATION_BUNDLE_INTEGRITY",
                "The pinned configuration bundle failed integrity validation.");
        }
    }

    private void InjectValidationFault(
        string commandId,
        string point,
        bool transient,
        string code,
        string diagnostic)
    {
        try
        {
            _faults.ThrowIfConfigured(commandId, point);
        }
        catch (StatConfigOperationsInjectedFaultException error)
        {
            throw new ReadinessValidationException(
                transient,
                code,
                diagnostic,
                error);
        }
    }

    private async Task<StatConfigValidationJob?> CompleteClaimAsync(
        StatConfigValidationJob job,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var priorRevision = job.StateRevision;
        var priorHash = job.StateHash;
        job.Status = StatConfigValidationJobStatuses.Completed;
        job.IsActive = false;
        job.CompletedAtUtc = now;
        job.FailedAtUtc = null;
        job.DeadLetterAtUtc = null;
        job.NextRetryAtUtc = null;
        job.SafeCode = "READY";
        job.SafeMessage = "Configuration readiness validation completed.";
        job.DiagnosticCode = null;
        job.DiagnosticMessage = null;
        job.FailureFingerprint = null;
        job.ExpiresAtUtc = now.Add(_terminalTtl);
        job.UpdatedAtUtc = now;
        job.StateRevision = priorRevision + 1;
        job.StateHash = ComputeStateHash(job);

        return await _ctx.StatConfigValidationJobs.FindOneAndUpdateAsync(
            FenceFilter(job, priorRevision, priorHash),
            Builders<StatConfigValidationJob>.Update
                .Set(item => item.Status, job.Status)
                .Set(item => item.IsActive, false)
                .Set(item => item.CompletedAtUtc, now)
                .Set(item => item.FailedAtUtc, null)
                .Set(item => item.DeadLetterAtUtc, null)
                .Set(item => item.NextRetryAtUtc, null)
                .Set(item => item.SafeCode, job.SafeCode)
                .Set(item => item.SafeMessage, job.SafeMessage)
                .Set(item => item.DiagnosticCode, null)
                .Set(item => item.DiagnosticMessage, null)
                .Set(item => item.FailureFingerprint, null)
                .Set(item => item.ExpiresAtUtc, job.ExpiresAtUtc)
                .Set(item => item.UpdatedAtUtc, now)
                .Set(item => item.StateRevision, job.StateRevision)
                .Set(item => item.StateHash, job.StateHash)
                .Unset(item => item.ClaimToken)
                .Unset(item => item.LeaseOwnerId)
                .Unset(item => item.LeaseUntilUtc),
            new FindOneAndUpdateOptions<StatConfigValidationJob>
            {
                ReturnDocument = ReturnDocument.After
            },
            ct);
    }

    private async Task<WorkerOutcome> FailClaimAsync(
        StatConfigValidationJob job,
        bool transient,
        string code,
        string diagnostic,
        Exception error,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var priorRevision = job.StateRevision;
        var priorHash = job.StateHash;
        var retryCount = job.RetryCount + 1;
        var exhausted = !transient || retryCount >= job.MaxRetryCount;
        var nextRetry = exhausted
            ? (DateTime?)null
            : now.AddSeconds(RetryDelaySeconds(retryCount));

        job.RetryCount = retryCount;
        job.Status = exhausted
            ? StatConfigValidationJobStatuses.Failed
            : StatConfigValidationJobStatuses.RetryWaiting;
        job.IsActive = !exhausted;
        job.NextRetryAtUtc = nextRetry;
        job.FailedAtUtc = exhausted ? now : null;
        job.DeadLetterAtUtc = exhausted ? now : null;
        job.SafeCode = exhausted ? "VALIDATION_FAILED" : "VALIDATION_RETRY";
        job.SafeMessage = exhausted
            ? "Configuration readiness validation failed."
            : "Configuration readiness validation will be retried.";
        job.DiagnosticCode = code;
        job.DiagnosticMessage = diagnostic;
        job.FailureFingerprint = StatConfigCanonicalJson.HashObject(new
        {
            exception = error.GetType().FullName,
            code,
            transient
        });
        job.ExpiresAtUtc = exhausted ? now.Add(_terminalTtl) : null;
        job.UpdatedAtUtc = now;
        job.StateRevision = priorRevision + 1;
        job.StateHash = ComputeStateHash(job);

        var updated = await _ctx.StatConfigValidationJobs
            .FindOneAndUpdateAsync(
                FenceFilter(job, priorRevision, priorHash),
                Builders<StatConfigValidationJob>.Update
                    .Set(item => item.RetryCount, retryCount)
                    .Set(item => item.Status, job.Status)
                    .Set(item => item.IsActive, job.IsActive)
                    .Set(item => item.NextRetryAtUtc, nextRetry)
                    .Set(item => item.FailedAtUtc, job.FailedAtUtc)
                    .Set(item => item.DeadLetterAtUtc, job.DeadLetterAtUtc)
                    .Set(item => item.SafeCode, job.SafeCode)
                    .Set(item => item.SafeMessage, job.SafeMessage)
                    .Set(item => item.DiagnosticCode, code)
                    .Set(item => item.DiagnosticMessage, diagnostic)
                    .Set(
                        item => item.FailureFingerprint,
                        job.FailureFingerprint)
                    .Set(item => item.ExpiresAtUtc, job.ExpiresAtUtc)
                    .Set(item => item.UpdatedAtUtc, now)
                    .Set(item => item.StateRevision, job.StateRevision)
                    .Set(item => item.StateHash, job.StateHash)
                    .Unset(item => item.ClaimToken)
                    .Unset(item => item.LeaseOwnerId)
                    .Unset(item => item.LeaseUntilUtc),
                new FindOneAndUpdateOptions<StatConfigValidationJob>
                {
                    ReturnDocument = ReturnDocument.After
                },
                ct);

        if (updated is null)
            return new WorkerOutcome(WorkerOutcomeKind.Lost, null);
        return new WorkerOutcome(
            exhausted
                ? WorkerOutcomeKind.Failed
                : WorkerOutcomeKind.Retrying,
            updated);
    }

    private static FilterDefinition<StatConfigValidationJob> FenceFilter(
        StatConfigValidationJob job,
        long priorRevision,
        string priorHash)
    {
        var filter = Builders<StatConfigValidationJob>.Filter;
        return filter.And(
            filter.Eq(item => item.Id, job.Id),
            filter.Eq(
                item => item.Status,
                StatConfigValidationJobStatuses.Running),
            filter.Eq(item => item.IsDeleted, false),
            filter.Eq(item => item.IsActive, true),
            filter.Eq(item => item.ClaimToken, job.ClaimToken),
            filter.Eq(item => item.LeaseOwnerId, job.LeaseOwnerId),
            filter.Ne<DateTime?>(item => item.LeaseUntilUtc, null),
            filter.Gt(item => item.LeaseUntilUtc, DateTime.UtcNow),
            filter.Eq(item => item.StateRevision, priorRevision),
            filter.Eq(item => item.StateHash, priorHash));
    }

    private int RetryDelaySeconds(int retryCount)
    {
        var exponent = Math.Clamp(retryCount - 1, 0, 20);
        var delay = (long)_retryBaseSeconds * (1L << exponent);
        return (int)Math.Min(_retryMaxSeconds, delay);
    }

    private static bool IsTransientValidationError(Exception error)
        => error is TimeoutException or MongoConnectionException or
               MongoExecutionTimeoutException ||
           error is MongoException mongo &&
               mongo.HasErrorLabel("RetryableWriteError") ||
           error.InnerException is not null &&
               IsTransientValidationError(error.InnerException);

    private static ReadinessValidationException PermanentValidation(
        string code,
        string diagnostic)
        => new(false, code, diagnostic);

    private sealed class ReadinessValidationException : Exception
    {
        public ReadinessValidationException(
            bool isTransient,
            string code,
            string diagnostic,
            Exception? inner = null)
            : base(code, inner)
        {
            IsTransient = isTransient;
            Code = code;
            Diagnostic = diagnostic;
        }

        public bool IsTransient { get; }
        public string Code { get; }
        public string Diagnostic { get; }
    }

    private enum WorkerOutcomeKind
    {
        Lost,
        Completed,
        Retrying,
        Failed
    }

    private sealed record WorkerOutcome(
        WorkerOutcomeKind Kind,
        StatConfigValidationJob? Job);
}
