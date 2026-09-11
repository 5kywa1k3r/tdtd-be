using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsRun;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsRun;

public sealed partial class StatRunService
{
    private const int MaxOperationReceiptsPerJob = 128;
    private static readonly TimeSpan MinimumCleanupRetention = TimeSpan.FromHours(24);
    private static readonly IReadOnlySet<string> OperationsRunKinds = new HashSet<string>(
        [
            WorkReportStatisticRebuildJobRunKinds.Foundation,
            WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection
        ],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> OperationsLifecyclePromptIds =
        new HashSet<string>(
            [
                "P9-02",
                "P9-03",
                "P9-04",
                "P9-05",
                "P9-06",
                "P9-07",
                "P9-08",
                StatRunCapabilityActivation.PublishedPromptId
            ],
            StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> OperationsTerminalStatuses = new HashSet<string>(
        [
            WorkReportStatisticRebuildJobStatuses.Completed,
            WorkReportStatisticRebuildJobStatuses.DeadLetter
        ],
        StringComparer.Ordinal);
    private static readonly string[] DirectGenerationCollections =
    [
        "work_report_field_stat_aggregates",
        "work_report_field_stat_values",
        "work_report_label_stat_aggregates",
        "work_report_label_stat_values",
        "work_report_table_stat_aggregates",
        "work_report_table_stat_values"
    ];

    public async Task<StatRunOperationsJobPageResponse> ListOperationsAsync(
        int limit,
        string? status,
        string? runKind,
        string? workId,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RequireCommandRole(actor);
        RequireOperationsActivation();
        if (limit is < 1 or > 100)
            throw Validation("limit", "LIMIT_OUT_OF_RANGE");

        var internalStatus = NormalizeOperationsStatus(status);
        var normalizedRunKind = NormalizeOperationsRunKind(runKind);
        var normalizedWorkId = string.IsNullOrWhiteSpace(workId)
            ? null
            : NormalizeObjectId(workId, "workId");

        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false) &
                     fb.Eq(x => x.CandidateChainId, StatRunCapabilityActivation.RequiredChainId) &
                     fb.In(x => x.RunKind, OperationsRunKinds);
        if (internalStatus is not null)
            filter &= fb.Eq(x => x.Status, internalStatus);
        if (normalizedRunKind is not null)
            filter &= fb.Eq(x => x.RunKind, normalizedRunKind);
        if (normalizedWorkId is not null)
            filter &= fb.Eq(x => x.WorkId, normalizedWorkId);

        if (!RoleGuard.IsSystemAdmin(actor))
        {
            if (!ObjectId.TryParse(actor.UnitId, out var unitId))
                throw Forbidden();
            var af = Builders<tdtd_be.Models.WorkAssignment>.Filter;
            var authorizedAssignments = await _ctx.WorkAssignments
                .Find(
                    af.Eq(x => x.IsDeleted, false) &
                    af.Eq(x => x.IsActive, true) &
                    (af.Eq(x => x.CreatedByUserId, actor.Id) |
                     af.AnyEq(x => x.LeaderWatcherUserIds, actor.Id)) &
                    (af.Eq(x => x.IssuedByUnitId, unitId.ToString()) |
                     af.AnyEq(x => x.TargetUnitIds!, unitId.ToString())))
                .Project(x => x.Id)
                .ToListAsync(ct);
            if (authorizedAssignments.Count == 0)
            {
                return new StatRunOperationsJobPageResponse
                {
                    Items = [],
                    Count = 0,
                    Limit = limit
                };
            }
            filter &= fb.Eq(x => x.ActorUserId, actor.Id) &
                      fb.Eq(x => x.TenantUnitId, unitId.ToString()) &
                      fb.In(x => x.WorkAssignmentId, authorizedAssignments);
        }

        var jobs = await _ctx.WorkReportStatisticRebuildJobs
            .Find(filter)
            .SortByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Limit(limit)
            .ToListAsync(ct);
        var items = new List<StatRunOperationsJobResponse>(jobs.Count);
        foreach (var job in jobs)
        {
            RequireOperationsReadIntegrity(job);
            items.Add(await ToOperationsResponseAsync(job, false, ct));
        }
        return new StatRunOperationsJobPageResponse
        {
            Items = items,
            Count = items.Count,
            Limit = limit
        };
    }

    public async Task<StatRunOperationsJobResponse> GetOperationsAsync(
        string jobId,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RequireCommandRole(actor);
        RequireOperationsActivation();
        if (!RoleGuard.IsSystemAdmin(actor) && !ObjectId.TryParse(jobId?.Trim(), out _))
            throw Forbidden();
        jobId = NormalizeObjectId(jobId, "jobId");
        var job = await LoadOperationsJobAsync(jobId, actor, includeDeleted: false, ct);
        await RequireCanReadJobScopeAsync(job, actor, ct);
        RequireOperationsReadIntegrity(job);
        return await ToOperationsResponseAsync(job, false, ct);
    }

    public async Task<StatRunJobDiagnosticResponse> GetOperationDiagnosticsAsync(
        string jobId,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RoleGuard.RequireSystemAdmin(actor);
        RequireOperationsActivation();
        jobId = NormalizeObjectId(jobId, "jobId");
        var job = await LoadOperationsJobAsync(jobId, actor, includeDeleted: false, ct);
        var now = MongoUtcNow();
        return new StatRunJobDiagnosticResponse
        {
            JobId = job.Id,
            RunKind = job.RunKind ?? string.Empty,
            Status = ToApiStatus(job.Status),
            StateRevision = job.StateRevision,
            StateHash = job.StateHash ?? string.Empty,
            StateIntegrity = HasOperationsStateIntegrity(job) ? "VALID" : "INVALID",
            HeaderIntegrity = HasOperationsHeaderIntegrity(job) ? "VALID" : "INVALID",
            ReceiptIntegrity = HasOperationReceiptIntegrity(job) ? "VALID" : "INVALID",
            LeaseState = job.Status != WorkReportStatisticRebuildJobStatuses.Running ||
                         !job.LeaseUntilUtc.HasValue
                ? "NONE"
                : job.LeaseUntilUtc > now ? "ACTIVE" : "EXPIRED",
            RetryCount = job.RetryCount,
            MaxRetryCount = _maxRetryCount,
            DiagnosticCode = RedactOperationsDiagnostic(job.DiagnosticCode),
            IsCurrentPublication = job.IsCurrentPublication,
            TotalReportCount = job.TotalReportCount,
            ProcessedReportCount = job.ProcessedReportCount,
            FailedReportCount = job.FailedReportCount,
            UpdatedAtUtc = job.UpdatedAtUtc
        };
    }

    public Task<StatRunOperationMutationResponse> RetryOperationAsync(
        string jobId,
        StatRunOperationCommandRequest request,
        MeResponse actor,
        CancellationToken ct = default)
        => ExecuteFoundationOperationAsync(
            jobId,
            WorkReportStatisticRebuildJobOperations.Retry,
            request,
            actor,
            ct);

    public Task<StatRunOperationMutationResponse> CancelOperationAsync(
        string jobId,
        StatRunOperationCommandRequest request,
        MeResponse actor,
        CancellationToken ct = default)
        => ExecuteFoundationOperationAsync(
            jobId,
            WorkReportStatisticRebuildJobOperations.Cancel,
            request,
            actor,
            ct);

    public Task<StatRunOperationMutationResponse> ResetOperationAsync(
        string jobId,
        StatRunOperationCommandRequest request,
        MeResponse actor,
        CancellationToken ct = default)
        => ExecuteFoundationOperationAsync(
            jobId,
            WorkReportStatisticRebuildJobOperations.Reset,
            request,
            actor,
            ct);

    public Task<StatRunOperationMutationResponse> RequeueOperationAsync(
        string jobId,
        StatRunOperationCommandRequest request,
        MeResponse actor,
        CancellationToken ct = default)
        => ExecuteFoundationOperationAsync(
            jobId,
            WorkReportStatisticRebuildJobOperations.Requeue,
            request,
            actor,
            ct);

    private async Task<StatRunOperationMutationResponse> ExecuteFoundationOperationAsync(
        string jobId,
        string operation,
        StatRunOperationCommandRequest request,
        MeResponse actor,
        CancellationToken ct)
    {
        RoleGuard.RequireSystemAdmin(actor);
        RequireOperationsActivation();
        jobId = NormalizeObjectId(jobId, "jobId");
        var commandId = NormalizeCommandId(request?.CommandId);
        if (request is null || request.ExpectedStateRevision < 1)
            throw Validation("expectedStateRevision", "REVISION_INVALID");
        RequireCanonicalHash(request.ExpectedStateHash, "expectedStateHash");
        if (!WorkReportStatisticRebuildJobOperations.All.Contains(operation) ||
            operation == WorkReportStatisticRebuildJobOperations.Cleanup)
        {
            throw Validation("operation", "OPERATION_INVALID");
        }

        var requestHash = StatRunCanonicalJson.HashObject(new
        {
            schema = "STAT_RUN_OPERATION_REQUEST_V1",
            jobId,
            actorUserId = actor.Id,
            operation,
            commandId,
            request.ExpectedStateRevision,
            request.ExpectedStateHash
        });
        var job = await LoadOperationsJobAsync(jobId, actor, includeDeleted: false, ct);
        if (!string.Equals(
                job.RunKind,
                WorkReportStatisticRebuildJobRunKinds.Foundation,
                StringComparison.Ordinal))
        {
            throw JobConflict("OPERATION_RUN_KIND_NOT_MUTABLE");
        }
        // The current operations route must be activated, while an immutable
        // same-chain job keeps its own historical stage lock. Requiring the
        // current stage hash here would break exact replay after the next
        // monotonic candidate stage.
        RequireOperationsReadIntegrity(job);
        var replay = await ResolveOperationReplayAsync(
            job,
            actor,
            operation,
            commandId,
            requestHash,
            ct);
        if (replay is not null)
            return replay;
        if (job.StateRevision != request.ExpectedStateRevision ||
            !string.Equals(job.StateHash, request.ExpectedStateHash, StringComparison.Ordinal))
        {
            throw JobConflict("OPERATION_CAS_STALE");
        }
        if ((job.OperationReceipts?.Count ?? 0) >= MaxOperationReceiptsPerJob)
            throw JobConflict("OPERATION_RECEIPT_HISTORY_FULL");

        var now = MongoUtcNow();
        var status = job.Status;
        var isActive = job.IsActive;
        var retryCount = job.RetryCount;
        var nextRetryAt = job.NextRetryAtUtc;
        var leaseUntil = job.LeaseUntilUtc;
        var claimToken = job.ClaimToken;
        var leaseOwnerId = job.LeaseOwnerId;
        var claimedAt = job.ClaimedAtUtc;
        var lastHeartbeatAt = job.LastHeartbeatAtUtc;
        var deadlineAt = job.DeadlineAtUtc;
        var generationId = job.GenerationId;
        var generationHash = job.GenerationHash;
        var freshnessState = job.FreshnessState ?? WorkReportStatisticRebuildJobFreshnessStates.Pending;
        var diagnosticCode = job.DiagnosticCode;
        var completedAt = job.CompletedAtUtc;
        var computedAt = job.ComputedAtUtc;
        var cancelledAt = job.CancelledAtUtc;
        var cancelledBy = job.CancelledByUserId;

        switch (operation)
        {
            case WorkReportStatisticRebuildJobOperations.Retry:
                if (job.Status == WorkReportStatisticRebuildJobStatuses.Running &&
                    job.LeaseUntilUtc.HasValue && job.LeaseUntilUtc > now)
                {
                    throw JobConflict("ACTIVE_LEASE_FENCED");
                }
                if (job.Status is not (
                        WorkReportStatisticRebuildJobStatuses.Pending or
                        WorkReportStatisticRebuildJobStatuses.RetryWaiting or
                        WorkReportStatisticRebuildJobStatuses.Running))
                {
                    throw JobConflict("RETRY_TRANSITION_INVALID");
                }
                if (!deadlineAt.HasValue || deadlineAt <= now)
                    throw JobConflict("JOB_DEADLINE_EXPIRED");
                retryCount = checked(job.RetryCount + 1);
                var exhausted = retryCount >= _maxRetryCount;
                status = exhausted
                    ? WorkReportStatisticRebuildJobStatuses.DeadLetter
                    : WorkReportStatisticRebuildJobStatuses.RetryWaiting;
                isActive = !exhausted;
                nextRetryAt = exhausted
                    ? null
                    : now.AddMinutes(Math.Min(5 * retryCount, 60));
                leaseUntil = null;
                claimToken = null;
                leaseOwnerId = null;
                diagnosticCode = "STAT_RUN_RETRY_REQUESTED";
                completedAt = exhausted ? now : null;
                freshnessState = WorkReportStatisticRebuildJobFreshnessStates.Pending;
                break;

            case WorkReportStatisticRebuildJobOperations.Cancel:
                if (job.Status is not (
                        WorkReportStatisticRebuildJobStatuses.Pending or
                        WorkReportStatisticRebuildJobStatuses.RetryWaiting or
                        WorkReportStatisticRebuildJobStatuses.Running))
                {
                    throw JobConflict("CANCEL_TRANSITION_INVALID");
                }
                status = WorkReportStatisticRebuildJobStatuses.DeadLetter;
                isActive = false;
                nextRetryAt = null;
                leaseUntil = null;
                claimToken = null;
                leaseOwnerId = null;
                diagnosticCode = "STAT_RUN_JOB_CANCELLED";
                completedAt = now;
                freshnessState = WorkReportStatisticRebuildJobFreshnessStates.Pending;
                cancelledAt = now;
                cancelledBy = actor.Id;
                break;

            case WorkReportStatisticRebuildJobOperations.Reset:
                if (job.Status != WorkReportStatisticRebuildJobStatuses.DeadLetter)
                    throw JobConflict("RESET_REQUIRES_DEAD_LETTER");
                status = WorkReportStatisticRebuildJobStatuses.Pending;
                isActive = true;
                retryCount = 0;
                nextRetryAt = now;
                leaseUntil = null;
                claimToken = null;
                leaseOwnerId = null;
                claimedAt = null;
                lastHeartbeatAt = null;
                deadlineAt = now.AddMinutes(10);
                generationId = null;
                generationHash = null;
                freshnessState = WorkReportStatisticRebuildJobFreshnessStates.Pending;
                diagnosticCode = null;
                completedAt = null;
                computedAt = null;
                cancelledAt = null;
                cancelledBy = null;
                break;

            case WorkReportStatisticRebuildJobOperations.Requeue:
                if (job.Status != WorkReportStatisticRebuildJobStatuses.RetryWaiting)
                    throw JobConflict("REQUEUE_REQUIRES_RETRY_WAITING");
                if (!deadlineAt.HasValue || deadlineAt <= now)
                    throw JobConflict("JOB_DEADLINE_EXPIRED");
                status = WorkReportStatisticRebuildJobStatuses.Pending;
                isActive = true;
                nextRetryAt = now;
                leaseUntil = null;
                claimToken = null;
                leaseOwnerId = null;
                claimedAt = null;
                diagnosticCode = null;
                completedAt = null;
                break;
        }

        var nextRevision = job.StateRevision + 1;
        var nextStateHash = BuildStateHash(
            job.Id,
            status,
            nextRevision,
            retryCount,
            nextRetryAt,
            leaseUntil,
            deadlineAt,
            claimToken,
            leaseOwnerId,
            lastHeartbeatAt,
            generationId,
            generationHash,
            job.ResetReceiptHistoryHash,
            freshnessState,
            diagnosticCode);
        var receipt = CreateOperationReceipt(
            job,
            actor,
            operation,
            commandId,
            requestHash,
            request.ExpectedStateRevision,
            request.ExpectedStateHash,
            nextRevision,
            nextStateHash,
            status,
            now,
            nextRetryAt,
            diagnosticCode);
        var nextReceiptHistoryHash = BuildOperationReceiptHistoryHash(
            (job.OperationReceipts ?? []).Append(receipt));

        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var filter = fb.Eq(x => x.Id, job.Id) &
                     fb.Eq(x => x.IsDeleted, false) &
                     fb.Eq(x => x.RunKind, WorkReportStatisticRebuildJobRunKinds.Foundation) &
                     fb.Eq(x => x.CandidateChainId, StatRunCapabilityActivation.RequiredChainId) &
                     fb.Eq(x => x.CapabilityId, job.CapabilityId) &
                     fb.Eq(x => x.RouteId, job.RouteId) &
                     fb.Eq(x => x.CatalogVersion, job.CatalogVersion) &
                     fb.Eq(x => x.CatalogRawSha256, job.CatalogRawSha256) &
                     fb.Eq(x => x.CatalogSemanticSha256, job.CatalogSemanticSha256) &
                     fb.Eq(x => x.SchemaRawSha256, job.SchemaRawSha256) &
                     fb.Eq(x => x.SchemaSemanticSha256, job.SchemaSemanticSha256) &
                     fb.Eq(x => x.StageLockSha256, job.StageLockSha256) &
                     fb.Eq(x => x.ImmutableHeaderHash, job.ImmutableHeaderHash) &
                     fb.Eq(x => x.Status, job.Status) &
                     fb.Eq(x => x.IsActive, job.IsActive) &
                     fb.Eq(x => x.StateRevision, job.StateRevision) &
                     fb.Eq(x => x.StateHash, job.StateHash);
        UpdateResult result;
        try
        {
            result = await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
                filter,
                Builders<WorkReportStatisticRebuildJob>.Update
                    .Set(x => x.Status, status)
                    .Set(x => x.IsActive, isActive)
                    .Set(x => x.RetryCount, retryCount)
                    .Set(x => x.NextRetryAtUtc, nextRetryAt)
                    .Set(x => x.LeaseUntilUtc, leaseUntil)
                    .Set(x => x.ClaimToken, claimToken)
                    .Set(x => x.LeaseOwnerId, leaseOwnerId)
                    .Set(x => x.ClaimedAtUtc, claimedAt)
                    .Set(x => x.LastHeartbeatAtUtc, lastHeartbeatAt)
                    .Set(x => x.DeadlineAtUtc, deadlineAt)
                    .Set(x => x.GenerationId, generationId)
                    .Set(x => x.GenerationHash, generationHash)
                    .Set(x => x.FreshnessState, freshnessState)
                    .Set(x => x.DiagnosticCode, diagnosticCode)
                    .Set(x => x.CompletedAtUtc, completedAt)
                    .Set(x => x.ComputedAtUtc, computedAt)
                    .Set(x => x.CancelledAtUtc, cancelledAt)
                    .Set(x => x.CancelledByUserId, cancelledBy)
                    .Set(x => x.LastErrorType, null)
                    .Set(x => x.LastError, null)
                    .Set(x => x.LastErrorAtUtc, diagnosticCode is null ? null : now)
                    .Set(x => x.OperationReceiptHistoryHash, nextReceiptHistoryHash)
                    .Push(x => x.OperationReceipts, receipt)
                    .Set(x => x.StateRevision, nextRevision)
                    .Set(x => x.StateHash, nextStateHash)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actor.Id),
                cancellationToken: ct);
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw JobConflict("ACTIVE_RUN_ALREADY_EXISTS");
        }
        if (result.ModifiedCount != 1)
        {
            var current = await LoadOperationsJobAsync(jobId, actor, includeDeleted: false, ct);
            if (string.Equals(
                    current.RunKind,
                    WorkReportStatisticRebuildJobRunKinds.Foundation,
                    StringComparison.Ordinal))
            {
                RequireOperationReceiptIntegrity(current);
                replay = await ResolveOperationReplayAsync(
                    current,
                    actor,
                    operation,
                    commandId,
                    requestHash,
                    ct);
                if (replay is not null)
                    return replay;
            }
            throw JobConflict("OPERATION_CAS_STALE");
        }

        var updated = await LoadOperationsJobAsync(jobId, actor, includeDeleted: false, ct);
        RequireOperationsReadIntegrity(updated);
        return new StatRunOperationMutationResponse
        {
            Job = await ToOperationsResponseAsync(updated, false, ct),
            Receipt = ToOperationReceiptResponse(receipt, false)
        };
    }

    private async Task<StatRunOperationMutationResponse?> ResolveOperationReplayAsync(
        WorkReportStatisticRebuildJob job,
        MeResponse actor,
        string operation,
        string commandId,
        string requestHash,
        CancellationToken ct)
    {
        var matches = (job.OperationReceipts ?? [])
            .Where(item =>
                string.Equals(item.ActorUserId, actor.Id, StringComparison.Ordinal) &&
                string.Equals(item.Operation, operation, StringComparison.Ordinal) &&
                string.Equals(item.CommandId, commandId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
            return null;
        if (matches.Length != 1)
            throw JobConflict("OPERATION_RECEIPT_HISTORY_INTEGRITY_INVALID");
        var receipt = matches[0];
        if (!string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal))
        {
            throw new AppException(
                AppErrorCode.STAT_RUN_COMMAND_REPLAY_MISMATCH,
                new { reason = "OPERATION_REQUEST_HASH_MISMATCH", writes = 0 });
        }
        return new StatRunOperationMutationResponse
        {
            Job = await ToOperationsResponseAsync(job, true, ct),
            Receipt = ToOperationReceiptResponse(receipt, true)
        };
    }

    private async Task<WorkReportStatisticRebuildJob> LoadOperationsJobAsync(
        string jobId,
        MeResponse actor,
        bool includeDeleted,
        CancellationToken ct)
    {
        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var filter = fb.Eq(x => x.Id, jobId) &
                     fb.Eq(x => x.CandidateChainId, StatRunCapabilityActivation.RequiredChainId) &
                     fb.In(x => x.RunKind, OperationsRunKinds);
        if (!includeDeleted)
            filter &= fb.Eq(x => x.IsDeleted, false);
        if (!RoleGuard.IsSystemAdmin(actor))
        {
            if (!ObjectId.TryParse(actor.UnitId, out var unitId))
                throw Forbidden();
            filter &= fb.Eq(x => x.ActorUserId, actor.Id) &
                      fb.Eq(x => x.TenantUnitId, unitId.ToString());
        }
        var job = await _ctx.WorkReportStatisticRebuildJobs
            .Find(filter)
            .FirstOrDefaultAsync(ct);
        if (job is not null)
            return job;
        if (RoleGuard.IsSystemAdmin(actor))
            throw JobConflict("JOB_NOT_AVAILABLE");
        throw Forbidden();
    }

    private async Task<StatRunOperationsJobResponse> ToOperationsResponseAsync(
        WorkReportStatisticRebuildJob job,
        bool isReplay,
        CancellationToken ct)
    {
        var freshness = job.FreshnessState ?? WorkReportStatisticRebuildJobFreshnessStates.Pending;
        var staleReason = job.StaleReason;
        if (string.Equals(
                job.RunKind,
                WorkReportStatisticRebuildJobRunKinds.Foundation,
                StringComparison.Ordinal))
        {
            (freshness, staleReason) = await ResolveFreshnessAsync(job, ct);
        }
        return new StatRunOperationsJobResponse
        {
            JobId = job.Id,
            RunId = job.Id,
            ReceiptId = job.ReceiptId ?? string.Empty,
            CapabilityId = job.CapabilityId ?? string.Empty,
            RunKind = job.RunKind ?? string.Empty,
            Status = ToApiStatus(job.Status),
            StateRevision = job.StateRevision,
            StateHash = job.StateHash ?? string.Empty,
            IsReplay = isReplay,
            WorkId = job.WorkId ?? string.Empty,
            ScopeType = job.ScopeType ?? string.Empty,
            ScopeId = job.ScopeId,
            SourceReportId = job.SourceReportId,
            SourceRevision = job.SourcePayloadRevision ?? 0,
            LifecycleRevision = job.SourceLifecycleRevision ?? 0,
            RetryCount = job.RetryCount,
            NextRetryAtUtc = job.NextRetryAtUtc,
            FreshnessState = freshness,
            StaleReason = RedactOperationsStaleReason(staleReason),
            DiagnosticCode = RedactOperationsDiagnostic(job.DiagnosticCode),
            IsCurrentPublication = job.IsCurrentPublication,
            CreatedAtUtc = job.CreatedAtUtc,
            UpdatedAtUtc = job.UpdatedAtUtc
        };
    }

    private static StatRunOperationReceiptResponse ToOperationReceiptResponse(
        WorkReportStatisticRebuildJobOperationReceipt receipt,
        bool isReplay)
        => new()
        {
            ReceiptId = receipt.ReceiptId,
            JobId = receipt.JobId,
            Operation = receipt.Operation,
            CommandId = receipt.CommandId,
            RequestHash = receipt.RequestHash,
            AcceptedStateRevision = receipt.AcceptedStateRevision,
            AcceptedStateHash = receipt.AcceptedStateHash,
            AcceptedStatus = ToApiStatus(receipt.AcceptedStatus),
            AcceptedAtUtc = receipt.AcceptedAtUtc,
            AcceptedNextRetryAtUtc = receipt.AcceptedNextRetryAtUtc,
            IsReplay = isReplay
        };

    private WorkReportStatisticRebuildJobOperationReceipt CreateOperationReceipt(
        WorkReportStatisticRebuildJob job,
        MeResponse actor,
        string operation,
        string commandId,
        string requestHash,
        long expectedStateRevision,
        string expectedStateHash,
        long acceptedStateRevision,
        string acceptedStateHash,
        string acceptedStatus,
        DateTime acceptedAtUtc,
        DateTime? acceptedNextRetryAtUtc,
        string? diagnosticCode,
        string? batchHash = null,
        int? batchSize = null,
        IReadOnlyList<string>? batchJobIds = null)
    {
        var receipt = new WorkReportStatisticRebuildJobOperationReceipt
        {
            ReceiptId = StatRunCanonicalJson.HashText(string.Join(
                "\n",
                "STAT_RUN_OPERATION_RECEIPT_V1",
                actor.Id,
                NormalizeTenantKey(actor.UnitId),
                job.Id,
                operation,
                commandId)),
            JobId = job.Id,
            ActorUserId = actor.Id,
            Operation = operation,
            CommandId = commandId,
            RequestHash = requestHash,
            ExpectedStateRevision = expectedStateRevision,
            ExpectedStateHash = expectedStateHash,
            AcceptedStateRevision = acceptedStateRevision,
            AcceptedStateHash = acceptedStateHash,
            AcceptedStatus = acceptedStatus,
            AcceptedAtUtc = acceptedAtUtc,
            AcceptedNextRetryAtUtc = acceptedNextRetryAtUtc,
            DiagnosticCode = diagnosticCode,
            BatchHash = batchHash,
            BatchSize = batchSize,
            BatchJobIds = batchJobIds?.ToList()
        };
        receipt.ReceiptHash = BuildOperationReceiptHash(receipt);
        return receipt;
    }

    private static string BuildOperationReceiptHash(
        WorkReportStatisticRebuildJobOperationReceipt receipt)
        => StatRunCanonicalJson.HashObject(new
        {
            schema = "STAT_RUN_OPERATION_RECEIPT_V1",
            receipt.ReceiptId,
            receipt.JobId,
            receipt.ActorUserId,
            receipt.Operation,
            receipt.CommandId,
            receipt.RequestHash,
            receipt.ExpectedStateRevision,
            receipt.ExpectedStateHash,
            receipt.AcceptedStateRevision,
            receipt.AcceptedStateHash,
            receipt.AcceptedStatus,
            acceptedAtUtc = FormatUtc(receipt.AcceptedAtUtc),
            acceptedNextRetryAtUtc = FormatUtc(receipt.AcceptedNextRetryAtUtc),
            receipt.DiagnosticCode,
            receipt.BatchHash,
            receipt.BatchSize,
            receipt.BatchJobIds
        });

    private static string BuildOperationReceiptHistoryHash(
        IEnumerable<WorkReportStatisticRebuildJobOperationReceipt> receipts)
        => StatRunCanonicalJson.HashObject(new
        {
            schema = "STAT_RUN_OPERATION_RECEIPT_HISTORY_V1",
            receipts = receipts.Select(item => new
            {
                item.ReceiptHash,
                computedReceiptHash = BuildOperationReceiptHash(item)
            }).ToArray()
        });

    private static void RequireOperationReceiptIntegrity(WorkReportStatisticRebuildJob job)
    {
        if (!HasOperationReceiptIntegrity(job))
            throw JobConflict("OPERATION_RECEIPT_HISTORY_INTEGRITY_INVALID");
    }

    private static bool HasOperationReceiptIntegrity(WorkReportStatisticRebuildJob job)
    {
        var receipts = job.OperationReceipts ?? [];
        if (receipts.Count > MaxOperationReceiptsPerJob)
            return false;
        if (receipts.Count == 0)
            return string.IsNullOrWhiteSpace(job.OperationReceiptHistoryHash);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var receipt in receipts)
        {
            var identity = $"{receipt.ActorUserId}\n{receipt.Operation}\n{receipt.CommandId}";
            if (!identities.Add(identity) ||
                !string.Equals(receipt.JobId, job.Id, StringComparison.Ordinal) ||
                !WorkReportStatisticRebuildJobOperations.All.Contains(receipt.Operation) ||
                string.IsNullOrWhiteSpace(receipt.ActorUserId) ||
                string.IsNullOrWhiteSpace(receipt.CommandId) ||
                receipt.ExpectedStateRevision < 1 ||
                receipt.AcceptedStateRevision != receipt.ExpectedStateRevision + 1 ||
                !StatRunCanonicalJson.IsCanonicalSha256(receipt.ReceiptId) ||
                !StatRunCanonicalJson.IsCanonicalSha256(receipt.RequestHash) ||
                !StatRunCanonicalJson.IsCanonicalSha256(receipt.ExpectedStateHash) ||
                !StatRunCanonicalJson.IsCanonicalSha256(receipt.AcceptedStateHash) ||
                !StatRunCanonicalJson.IsCanonicalSha256(receipt.ReceiptHash) ||
                !string.Equals(receipt.ReceiptHash, BuildOperationReceiptHash(receipt), StringComparison.Ordinal) ||
                !IsOperationsStatus(receipt.AcceptedStatus) ||
                !HasValidOperationBatch(receipt))
            {
                return false;
            }
        }
        return StatRunCanonicalJson.IsCanonicalSha256(job.OperationReceiptHistoryHash) &&
               string.Equals(
                   job.OperationReceiptHistoryHash,
                   BuildOperationReceiptHistoryHash(receipts),
                   StringComparison.Ordinal);
    }

    private static bool HasValidOperationBatch(
        WorkReportStatisticRebuildJobOperationReceipt receipt)
    {
        if (receipt.Operation != WorkReportStatisticRebuildJobOperations.Cleanup)
        {
            return receipt.BatchHash is null &&
                   receipt.BatchSize is null &&
                   receipt.BatchJobIds is null;
        }
        var ids = receipt.BatchJobIds;
        return StatRunCanonicalJson.IsCanonicalSha256(receipt.BatchHash) &&
               receipt.BatchSize is >= 1 and <= 100 &&
               ids is not null &&
               ids.Count == receipt.BatchSize &&
               ids.Distinct(StringComparer.Ordinal).Count() == ids.Count &&
               ids.All(item => ObjectId.TryParse(item, out _)) &&
               ids.Contains(receipt.JobId, StringComparer.Ordinal);
    }

    private static void RequireOperationsReadIntegrity(WorkReportStatisticRebuildJob job)
    {
        if (!HasOperationsHeaderIntegrity(job))
            throw JobConflict("IMMUTABLE_HEADER_INTEGRITY_INVALID");
        if (!HasOperationsStateIntegrity(job))
            throw JobConflict("STATE_INTEGRITY_INVALID");
        RequireOperationReceiptIntegrity(job);
    }

    private static bool HasOperationsHeaderIntegrity(WorkReportStatisticRebuildJob job)
    {
        if (!string.Equals(job.CandidateChainId, StatRunCapabilityActivation.RequiredChainId, StringComparison.Ordinal) ||
            !OperationsRunKinds.Contains(job.RunKind ?? string.Empty) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.ReceiptId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.RequestHash) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.ImmutableHeaderHash) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.CatalogRawSha256) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.CatalogSemanticSha256) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.SchemaRawSha256) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.SchemaSemanticSha256) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.StageLockSha256))
        {
            return false;
        }
        if (string.Equals(
                job.RunKind,
                WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection,
                StringComparison.Ordinal))
        {
            return HasLifecycleOperationsHeaderIntegrity(job);
        }
        try
        {
            RequireImmutableHeaderIntegrity(job);
            return true;
        }
        catch (AppException)
        {
            return false;
        }
    }

    internal static bool HasLifecycleOperationsHeaderIntegrity(
        WorkReportStatisticRebuildJob job)
    {
        if (!string.Equals(
                job.CapabilityId,
                StatRunCapabilities.DirectFieldTableLabel,
                StringComparison.Ordinal) ||
            !string.Equals(
                job.RouteId,
                StatRunRouteRegistry.LifecycleDirectProjector,
                StringComparison.Ordinal) ||
            !string.Equals(job.ScopeType, "WORK_PERIOD_TEMPLATE", StringComparison.Ordinal) ||
            !string.Equals(
                job.ScopeKind,
                WorkReportStatisticRebuildJobScopeKinds.Bounded,
                StringComparison.Ordinal) ||
            !HasLifecycleOperationsPromptIntegrity(job) ||
            !ObjectId.TryParse(job.Id, out _) ||
            !ObjectId.TryParse(job.SourceReportId, out var sourceReportId) ||
            !ObjectId.TryParse(job.ActorUserId, out _) ||
            !ObjectId.TryParse(job.TenantUnitId, out _) ||
            !ObjectId.TryParse(job.WorkId, out _) ||
            !ObjectId.TryParse(job.WorkAssignmentId, out _) ||
            !ObjectId.TryParse(job.DynamicFormFamilyId, out _) ||
            !ObjectId.TryParse(job.DynamicFormTemplateId, out _) ||
            !ObjectId.TryParse(job.ConfigId, out _) ||
            !ObjectId.TryParse(job.ConfigVersionId, out _) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.SourceLifecycleEventKey) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.SourcePayloadHash) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.SourceMembershipSignature) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.PublicationScopeKey) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.GenerationId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.DynamicFormSchemaHash) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.ConfigHash) ||
            !job.ComputedAtUtc.HasValue ||
            job.TotalReportCount < 0 ||
            (job.FlowContributionOriginVersionId is not null &&
             !ObjectId.TryParse(job.FlowContributionOriginVersionId, out _)) ||
            !HasLifecycleOperationsReversalAuditIntegrity(job))
        {
            return false;
        }

        if (!HasCanonicalLifecycleProjectionIdentity(job) ||
            !string.Equals(job.ScopeId, job.WorkId, StringComparison.Ordinal))
        {
            return false;
        }

        var expectedHeaderHash = BuildLifecycleOperationsHeaderHash(job);
        return string.Equals(job.RequestHash, expectedHeaderHash, StringComparison.Ordinal) &&
               string.Equals(job.ImmutableHeaderHash, expectedHeaderHash, StringComparison.Ordinal);
    }

    internal static bool HasLifecycleOperationsPromptIntegrity(
        WorkReportStatisticRebuildJob job)
        => IsFoundationRefreshLifecycleProjection(job)
            ? string.Equals(
                  job.CandidatePromptId,
                  StatRunCapabilityActivation.LifecycleRequiredPromptId,
                  StringComparison.Ordinal) ||
              string.Equals(
                  job.CandidatePromptId,
                  StatRunCapabilityActivation.PublishedPromptId,
                  StringComparison.Ordinal)
            : OperationsLifecyclePromptIds.Contains(
                job.CandidatePromptId ?? string.Empty);


    private static bool HasLifecycleOperationsReversalAuditIntegrity(
        WorkReportStatisticRebuildJob job)
    {
        var audit = job.ReversalAudit;
        if (audit is null)
            return true;
        if (audit.PriorSourceAuditHashes is null ||
            audit.PriorTargetLedgerHashes is null ||
            !ObjectId.TryParse(audit.SourceReportId, out _) ||
            !ObjectId.TryParse(audit.PriorRunId, out _) ||
            !StatRunCanonicalJson.IsCanonicalSha256(audit.ReceiptId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(audit.PriorGenerationId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(audit.PriorGenerationHash) ||
            !StatRunCanonicalJson.IsCanonicalSha256(audit.AuditHash) ||
            audit.PriorSourceCount != audit.PriorSourceAuditHashes.Count ||
            audit.PriorTargetCount != audit.PriorTargetLedgerHashes.Count ||
            audit.PriorSourceCount < 0 ||
            audit.PriorTargetCount < 0 ||
            audit.PriorSourceAuditHashes.Any(item =>
                !StatRunCanonicalJson.IsCanonicalSha256(item)) ||
            audit.PriorTargetLedgerHashes.Any(item =>
                !StatRunCanonicalJson.IsCanonicalSha256(item)) ||
            !string.Equals(audit.EventKey, job.SourceLifecycleEventKey, StringComparison.Ordinal) ||
            !string.Equals(audit.SourceReportId, job.SourceReportId, StringComparison.Ordinal) ||
            audit.SourceLifecycleRevision != job.SourceLifecycleRevision ||
            !string.Equals(audit.ReceiptId, job.ReceiptId, StringComparison.Ordinal))
        {
            return false;
        }
        var expectedAuditHash = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_DIRECT_REVERSAL_AUDIT_V1",
            runId = job.Id,
            generationId = job.GenerationId,
            audit.Operation,
            audit.EventKey,
            audit.SourceReportId,
            audit.SourceLifecycleRevision,
            audit.ReceiptId,
            audit.PriorRunId,
            audit.PriorGenerationId,
            audit.PriorGenerationHash,
            audit.PriorLedgerHash,
            audit.PriorReversalBaselineHash,
            audit.PriorSourceCount,
            audit.PriorTargetCount,
            audit.PriorSourceAuditHashes,
            audit.PriorTargetLedgerHashes
        });
        return string.Equals(audit.AuditHash, expectedAuditHash, StringComparison.Ordinal);
    }

    private static string BuildLifecycleOperationsHeaderHash(
        WorkReportStatisticRebuildJob job)
    {
        if (IsFoundationRefreshLifecycleProjection(job))
        {
            return StatRunDirectProjectionService
                .ComputeFoundationRefreshImmutableHeaderHash(job);
        }

        var baseHeaderHash = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_LIFECYCLE_DIRECT_HEADER_V2",
            runId = job.Id,
            receiptId = job.ReceiptId,
            sourceReportId = job.SourceReportId,
            lifecycleEventKey = job.SourceLifecycleEventKey,
            commandId = job.CommandId,
            payloadRevision = job.SourcePayloadRevision,
            payloadHash = job.SourcePayloadHash,
            lifecycleRevision = job.SourceLifecycleRevision,
            directSourceRevision = job.DirectSourceRevision,
            publicationScopeKey = job.PublicationScopeKey,
            sourceMembershipSignature = job.SourceMembershipSignature,
            generationId = job.GenerationId,
            dynamicFormFamilyId = job.DynamicFormFamilyId,
            dynamicFormTemplateId = job.DynamicFormTemplateId,
            dynamicFormVersionNo = job.DynamicFormVersionNo,
            dynamicFormSchemaHash = job.DynamicFormSchemaHash,
            configId = job.ConfigId,
            configVersionId = job.ConfigVersionId,
            configVersionNo = job.ConfigVersionNo,
            configRevision = job.ConfigRevision,
            configHash = job.ConfigHash,
            candidateChainId = job.CandidateChainId,
            catalogVersion = job.CatalogVersion,
            catalogRawSha256 = job.CatalogRawSha256,
            catalogSemanticSha256 = job.CatalogSemanticSha256,
            schemaRawSha256 = job.SchemaRawSha256,
            schemaSemanticSha256 = job.SchemaSemanticSha256,
            stageLockSha256 = job.StageLockSha256,
            promptId = job.CandidatePromptId,
            actorUserId = job.ActorUserId,
            tenantUnitId = job.TenantUnitId,
            workId = job.WorkId,
            workAssignmentId = job.WorkAssignmentId,
            periodKey = job.PeriodKey,
            periodInstanceKey = job.PeriodInstanceKey,
            periodKind = job.PeriodKind,
            periodStartUtc = job.PeriodStartUtc,
            periodEndUtc = job.PeriodEndUtc,
            memberCount = job.TotalReportCount,
            flowEffectiveStatus = job.FlowEffectiveStatus,
            flowTemplateId = job.FlowTemplateId,
            flowTemplateVersionNo = job.FlowTemplateVersionNo,
            flowExecutionEpoch = job.FlowExecutionEpoch,
            flowBranchId = job.FlowBranchId,
            flowStepId = job.FlowStepId,
            flowAttemptNo = job.FlowAttemptNo,
            flowFamilyRevision = job.FlowFamilyRevision,
            flowTemplateVersionId = job.FlowTemplateVersionId,
            flowContributionOriginVersionId = job.FlowContributionOriginVersionId,
            flowPayloadHash = job.FlowPayloadHash,
            flowCatalogVersion = job.FlowCatalogVersion,
            flowCatalogSemanticHash = job.FlowCatalogSemanticHash,
            flowInstanceId = job.FlowInstanceId,
            flowInstanceRevision = job.FlowInstanceRevision,
            flowInstanceState = job.FlowInstanceState,
            executionEpochId = job.FlowExecutionEpochId,
            executionEpochRevision = job.FlowExecutionEpochRevision,
            executionEpochState = job.FlowExecutionEpochState,
            stepInstanceId = job.FlowStepInstanceId,
            stepInstanceRevision = job.FlowStepInstanceRevision,
            stepInstanceState = job.FlowStepInstanceState,
            computedAtUtc = job.ComputedAtUtc
        });
        var audit = job.ReversalAudit;
        if (audit is null)
            return baseHeaderHash;
        return StatRunCanonicalJson.HashObject(new
        {
            version = "P9_LIFECYCLE_DIRECT_REVERSAL_HEADER_V1",
            baseHeaderHash,
            audit.AuditHash,
            audit.PriorRunId,
            audit.PriorGenerationId,
            audit.PriorGenerationHash,
            audit.PriorLedgerHash,
            audit.PriorReversalBaselineHash
        });
    }

    private static bool HasOperationsStateIntegrity(WorkReportStatisticRebuildJob job)
    {
        if (!IsOperationsStatus(job.Status) || job.StateRevision < 1 ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.StateHash))
        {
            return false;
        }
        if (string.Equals(
                job.RunKind,
                WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection,
                StringComparison.Ordinal))
        {
            return string.Equals(
                job.StateHash,
                BuildLifecycleOperationsStateHash(job),
                StringComparison.Ordinal);
        }
        try
        {
            RequireStateIntegrity(job);
            return true;
        }
        catch (AppException)
        {
            return false;
        }
    }

    private static string BuildLifecycleOperationsStateHash(WorkReportStatisticRebuildJob job)
        => StatRunCanonicalJson.HashObject(new
        {
            version = "P9_LIFECYCLE_DIRECT_STATE_V1",
            runId = job.Id,
            status = job.Status,
            revision = job.StateRevision,
            claimToken = job.ClaimToken,
            workerId = job.LeaseOwnerId,
            generationId = job.GenerationId,
            generationHash = job.GenerationHash
        });

    private void RequireOperationsActivation()
    {
        foreach (var capability in StatRunCapabilities.All)
            _activation.RequireFoundation(capability, StatRunRouteRegistry.CoreJob(capability));
    }

    private static string? NormalizeOperationsStatus(string? value)
    {
        var status = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(status))
            return null;
        return status switch
        {
            "QUEUED" or WorkReportStatisticRebuildJobStatuses.Pending =>
                WorkReportStatisticRebuildJobStatuses.Pending,
            "RUNNING" => WorkReportStatisticRebuildJobStatuses.Running,
            "RETRYING" or WorkReportStatisticRebuildJobStatuses.RetryWaiting =>
                WorkReportStatisticRebuildJobStatuses.RetryWaiting,
            "DONE" or WorkReportStatisticRebuildJobStatuses.Completed =>
                WorkReportStatisticRebuildJobStatuses.Completed,
            "FAILED" or WorkReportStatisticRebuildJobStatuses.DeadLetter =>
                WorkReportStatisticRebuildJobStatuses.DeadLetter,
            _ => throw Validation("status", "STATUS_INVALID")
        };
    }

    private static string? NormalizeOperationsRunKind(string? value)
    {
        var runKind = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(runKind))
            return null;
        if (!OperationsRunKinds.Contains(runKind))
            throw Validation("runKind", "RUN_KIND_INVALID");
        return runKind;
    }

    private static bool IsOperationsStatus(string? value)
        => value is
            WorkReportStatisticRebuildJobStatuses.Pending or
            WorkReportStatisticRebuildJobStatuses.Running or
            WorkReportStatisticRebuildJobStatuses.RetryWaiting or
            WorkReportStatisticRebuildJobStatuses.Completed or
            WorkReportStatisticRebuildJobStatuses.DeadLetter;

    public async Task<StatRunCleanupPreviewResponse> PreviewCleanupAsync(
        StatRunCleanupPreviewRequest request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RoleGuard.RequireSystemAdmin(actor);
        RequireOperationsActivation();
        var (olderThanUtc, maxJobs) = NormalizeCleanupRequest(
            request?.OlderThanUtc ?? default,
            request?.MaxJobs ?? 0);
        return await BuildCleanupPreviewAsync(olderThanUtc, maxJobs, ct);
    }

    public async Task<StatRunCleanupApplyResponse> ApplyCleanupAsync(
        StatRunCleanupApplyRequest request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RoleGuard.RequireSystemAdmin(actor);
        RequireOperationsActivation();
        var commandId = NormalizeCommandId(request?.CommandId);
        var (olderThanUtc, maxJobs) = NormalizeCleanupRequest(
            request?.OlderThanUtc ?? default,
            request?.MaxJobs ?? 0);
        RequireCanonicalHash(request?.ExpectedCandidateHash, "expectedCandidateHash");
        var requestHash = StatRunCanonicalJson.HashObject(new
        {
            schema = "STAT_RUN_CLEANUP_REQUEST_V1",
            actorUserId = actor.Id,
            commandId,
            olderThanUtc = FormatUtc(olderThanUtc),
            maxJobs,
            request!.ExpectedCandidateHash
        });

        var receiptFb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var receiptFilter =
            receiptFb.Eq(
                x => x.CandidateChainId,
                StatRunCapabilityActivation.RequiredChainId) &
            receiptFb.In(x => x.RunKind, OperationsRunKinds) &
            receiptFb.ElemMatch(
                x => x.OperationReceipts,
                item => item.ActorUserId == actor.Id &&
                        item.Operation == WorkReportStatisticRebuildJobOperations.Cleanup &&
                        item.CommandId == commandId);
        var replayJobs = await _ctx.WorkReportStatisticRebuildJobs
            .Find(receiptFilter)
            .SortBy(x => x.Id)
            .ToListAsync(ct);
        if (replayJobs.Count > 0)
        {
            foreach (var replayJob in replayJobs)
            {
                RequireOperationsReadIntegrity(replayJob);
                if (!replayJob.IsDeleted || replayJob.IsCurrentPublication ||
                    replayJob.IsActive ||
                    !OperationsTerminalStatuses.Contains(replayJob.Status))
                {
                    throw JobConflict("CLEANUP_REPLAY_TOMBSTONE_INVALID");
                }
            }
            var receipts = replayJobs
                .SelectMany(job => (job.OperationReceipts ?? []).Where(item =>
                    string.Equals(item.ActorUserId, actor.Id, StringComparison.Ordinal) &&
                    string.Equals(item.Operation, WorkReportStatisticRebuildJobOperations.Cleanup, StringComparison.Ordinal) &&
                    string.Equals(item.CommandId, commandId, StringComparison.Ordinal)))
                .ToArray();
            if (receipts.Length == 0 || receipts.Any(item =>
                    !string.Equals(item.RequestHash, requestHash, StringComparison.Ordinal) ||
                    !string.Equals(item.BatchHash, request.ExpectedCandidateHash, StringComparison.Ordinal)))
            {
                throw new AppException(
                    AppErrorCode.STAT_RUN_COMMAND_REPLAY_MISMATCH,
                    new { reason = "CLEANUP_REQUEST_HASH_MISMATCH", writes = 0 });
            }
            var replayCandidateIds = receipts[0].BatchJobIds?.ToArray()
                ?? throw JobConflict("OPERATION_RECEIPT_HISTORY_INTEGRITY_INVALID");
            if (receipts.Any(item =>
                    item.BatchJobIds is null ||
                    !item.BatchJobIds.SequenceEqual(replayCandidateIds, StringComparer.Ordinal)))
            {
                throw JobConflict("OPERATION_RECEIPT_HISTORY_INTEGRITY_INVALID");
            }
            var receiptByJob = receipts.ToDictionary(item => item.JobId, StringComparer.Ordinal);
            if (receiptByJob.Count != replayJobs.Count || replayJobs.Any(job =>
                    !receiptByJob.TryGetValue(job.Id, out var receipt) ||
                    receipt.AcceptedStateRevision != job.StateRevision ||
                    !string.Equals(receipt.AcceptedStateHash, job.StateHash, StringComparison.Ordinal) ||
                    !string.Equals(receipt.AcceptedStatus, job.Status, StringComparison.Ordinal)))
            {
                throw JobConflict("OPERATION_RECEIPT_HISTORY_INTEGRITY_INVALID");
            }
            // A process may have stopped after the job tombstone CAS but before
            // finishing all six generation-row tombstones. Replaying this
            // idempotent false-only update closes that gap without touching a
            // current row or changing the operation receipt.
            foreach (var completedId in receiptByJob.Keys)
            {
                if (await IsRunReferencedAsync(completedId, ct))
                    throw JobConflict("CLEANUP_PROTECTED_JOB", receiptByJob.Count);
                await SoftDeleteDirectGenerationRowsAsync(
                    completedId,
                    actor.Id,
                    MongoUtcNow(),
                    ct);
            }
            foreach (var missingId in replayCandidateIds.Where(id => !receiptByJob.ContainsKey(id)))
            {
                var resumedReceipt = await ApplyCleanupCandidateAsync(
                    missingId,
                    replayCandidateIds,
                    request.ExpectedCandidateHash,
                    olderThanUtc,
                    commandId,
                    requestHash,
                    actor,
                    receiptByJob.Count,
                    ct);
                receiptByJob.Add(missingId, resumedReceipt);
            }
            return new StatRunCleanupApplyResponse
            {
                CommandId = commandId,
                RequestHash = requestHash,
                CandidateHash = request.ExpectedCandidateHash,
                AppliedCount = replayCandidateIds.Length,
                JobIds = replayCandidateIds,
                ReceiptIds = replayCandidateIds.Select(id => receiptByJob[id].ReceiptId).ToArray(),
                IsReplay = true
            };
        }

        var preview = await BuildCleanupPreviewAsync(olderThanUtc, maxJobs, ct);
        if (!string.Equals(
                preview.CandidateHash,
                request.ExpectedCandidateHash,
                StringComparison.Ordinal))
        {
            throw JobConflict("CLEANUP_CANDIDATE_SET_STALE");
        }
        if (preview.Candidates.Count == 0)
            throw JobConflict("CLEANUP_EMPTY_NOT_RECEIPTABLE");

        var candidateIds = preview.Candidates.Select(item => item.JobId).ToArray();
        var references = await LoadReferencedRunIdsAsync(candidateIds, ct);
        if (references.Count > 0)
            throw JobConflict("CLEANUP_REFERENCE_CHANGED");

        var applied = new List<WorkReportStatisticRebuildJobOperationReceipt>(candidateIds.Length);
        foreach (var candidateId in candidateIds)
        {
            applied.Add(await ApplyCleanupCandidateAsync(
                candidateId,
                candidateIds,
                preview.CandidateHash,
                olderThanUtc,
                commandId,
                requestHash,
                actor,
                applied.Count,
                ct));
        }

        return new StatRunCleanupApplyResponse
        {
            CommandId = commandId,
            RequestHash = requestHash,
            CandidateHash = preview.CandidateHash,
            AppliedCount = applied.Count,
            JobIds = applied.Select(item => item.JobId).ToArray(),
            ReceiptIds = applied.Select(item => item.ReceiptId).ToArray(),
            IsReplay = false
        };
    }

    private async Task<WorkReportStatisticRebuildJobOperationReceipt>
        ApplyCleanupCandidateAsync(
            string candidateId,
            IReadOnlyList<string> candidateIds,
            string candidateHash,
            DateTime olderThanUtc,
            string commandId,
            string requestHash,
            MeResponse actor,
            int priorWrites,
            CancellationToken ct)
    {
        var job = await _ctx.WorkReportStatisticRebuildJobs
            .Find(x => x.Id == candidateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw JobConflict("CLEANUP_CAS_STALE", priorWrites);
        RequireOperationsReadIntegrity(job);
        if (job.IsCurrentPublication || job.IsActive ||
            !OperationsTerminalStatuses.Contains(job.Status) ||
            job.UpdatedAtUtc > olderThanUtc ||
            await IsRunReferencedAsync(job.Id, ct))
        {
            throw JobConflict("CLEANUP_PROTECTED_JOB", priorWrites);
        }
        if ((job.OperationReceipts?.Count ?? 0) >= MaxOperationReceiptsPerJob)
            throw JobConflict("OPERATION_RECEIPT_HISTORY_FULL", priorWrites);

        var now = MongoUtcNow();
        var nextRevision = job.StateRevision + 1;
        var nextStateHash = string.Equals(
                job.RunKind,
                WorkReportStatisticRebuildJobRunKinds.Foundation,
                StringComparison.Ordinal)
            ? BuildStateHash(
                job.Id,
                job.Status,
                nextRevision,
                job.RetryCount,
                job.NextRetryAtUtc,
                job.LeaseUntilUtc,
                job.DeadlineAtUtc,
                job.ClaimToken,
                job.LeaseOwnerId,
                job.LastHeartbeatAtUtc,
                job.GenerationId,
                job.GenerationHash,
                job.ResetReceiptHistoryHash,
                job.FreshnessState ?? WorkReportStatisticRebuildJobFreshnessStates.Pending,
                job.DiagnosticCode)
            : BuildLifecycleOperationsStateHash(job, nextRevision);
        var receipt = CreateOperationReceipt(
            job,
            actor,
            WorkReportStatisticRebuildJobOperations.Cleanup,
            commandId,
            requestHash,
            job.StateRevision,
            job.StateHash ?? string.Empty,
            nextRevision,
            nextStateHash,
            job.Status,
            now,
            job.NextRetryAtUtc,
            job.DiagnosticCode,
            candidateHash,
            candidateIds.Count,
            candidateIds);
        var nextReceiptHistoryHash = BuildOperationReceiptHistoryHash(
            (job.OperationReceipts ?? []).Append(receipt));
        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var cleanupCas = fb.Eq(x => x.Id, job.Id) &
                         fb.Eq(x => x.IsDeleted, false) &
                         fb.Eq(x => x.IsActive, false) &
                         fb.Eq(x => x.IsCurrentPublication, false) &
                         fb.In(x => x.Status, OperationsTerminalStatuses) &
                         fb.Lte(x => x.UpdatedAtUtc, olderThanUtc) &
                         fb.Eq(x => x.StateRevision, job.StateRevision) &
                         fb.Eq(x => x.StateHash, job.StateHash);
        var result = await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            cleanupCas,
            Builders<WorkReportStatisticRebuildJob>.Update
                .Set(x => x.IsDeleted, true)
                .Set(x => x.StateRevision, nextRevision)
                .Set(x => x.StateHash, nextStateHash)
                .Set(x => x.OperationReceiptHistoryHash, nextReceiptHistoryHash)
                .Push(x => x.OperationReceipts, receipt)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actor.Id),
            cancellationToken: ct);
        if (result.ModifiedCount != 1)
            throw JobConflict("CLEANUP_CAS_STALE", priorWrites);
        await SoftDeleteDirectGenerationRowsAsync(job.Id, actor.Id, now, ct);
        return receipt;
    }

    private async Task<StatRunCleanupPreviewResponse> BuildCleanupPreviewAsync(
        DateTime olderThanUtc,
        int maxJobs,
        CancellationToken ct)
    {
        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false) &
                     fb.Eq(x => x.CandidateChainId, StatRunCapabilityActivation.RequiredChainId) &
                     fb.In(x => x.RunKind, OperationsRunKinds) &
                     fb.In(x => x.Status, OperationsTerminalStatuses) &
                     fb.Lte(x => x.UpdatedAtUtc, olderThanUtc);
        var scanLimit = Math.Min(500, Math.Max(maxJobs * 5, maxJobs));
        var scanned = await _ctx.WorkReportStatisticRebuildJobs
            .Find(filter)
            .SortBy(x => x.UpdatedAtUtc)
            .ThenBy(x => x.Id)
            .Limit(scanLimit)
            .ToListAsync(ct);
        foreach (var job in scanned)
            RequireOperationsReadIntegrity(job);

        var referenced = await LoadReferencedRunIdsAsync(
            scanned.Select(item => item.Id).ToArray(),
            ct);
        var protectedCurrent = scanned.Count(item => item.IsCurrentPublication || item.IsActive);
        var protectedReferenced = scanned.Count(item => referenced.Contains(item.Id));
        var candidates = scanned
            .Where(item => !item.IsCurrentPublication && !item.IsActive && !referenced.Contains(item.Id))
            .Take(maxJobs)
            .ToArray();
        var candidateHash = StatRunCanonicalJson.HashObject(new
        {
            schema = "STAT_RUN_CLEANUP_CANDIDATES_V1",
            olderThanUtc = FormatUtc(olderThanUtc),
            maxJobs,
            candidates = candidates.Select(item => new
            {
                item.Id,
                item.RunKind,
                item.Status,
                item.StateRevision,
                item.StateHash,
                updatedAtUtc = FormatUtc(item.UpdatedAtUtc)
            }).ToArray()
        });
        return new StatRunCleanupPreviewResponse
        {
            OlderThanUtc = olderThanUtc,
            MaxJobs = maxJobs,
            ScannedCount = scanned.Count,
            ProtectedCurrentCount = protectedCurrent,
            ProtectedReferencedCount = protectedReferenced,
            CandidateHash = candidateHash,
            Candidates = candidates.Select(item => new StatRunCleanupCandidateResponse
            {
                JobId = item.Id,
                RunKind = item.RunKind ?? string.Empty,
                Status = ToApiStatus(item.Status),
                UpdatedAtUtc = item.UpdatedAtUtc
            }).ToArray()
        };
    }

    private static (DateTime OlderThanUtc, int MaxJobs) NormalizeCleanupRequest(
        DateTime olderThanUtc,
        int maxJobs)
    {
        if (olderThanUtc == default || olderThanUtc.Kind == DateTimeKind.Unspecified)
            throw Validation("olderThanUtc", "UTC_REQUIRED");
        var normalized = NormalizeUtc(olderThanUtc)!.Value;
        if (maxJobs is < 1 or > 100)
            throw Validation("maxJobs", "LIMIT_OUT_OF_RANGE");
        if (normalized > DateTime.UtcNow.Add(-MinimumCleanupRetention))
            throw Validation("olderThanUtc", "RETENTION_WINDOW_TOO_SHORT");
        return (normalized, maxJobs);
    }

    private async Task<HashSet<string>> LoadReferencedRunIdsAsync(
        IReadOnlyCollection<string> runIds,
        CancellationToken ct)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var runId in runIds)
        {
            if (await IsRunReferencedAsync(runId, ct))
                referenced.Add(runId);
        }
        return referenced;
    }

    private async Task<bool> IsRunReferencedAsync(string runId, CancellationToken ct)
    {
        var reversalReference = new BsonDocument
        {
            { "isDeleted", false },
            { "reversalAudit.priorRunId", new ObjectId(runId) }
        };
        if (await _ctx.WorkReportStatisticRebuildJobs
                .Find(reversalReference)
                .Limit(1)
                .AnyAsync(ct))
        {
            return true;
        }

        var diffResults = _ctx.Db.GetCollection<WorkReportStatisticDiffResult>(
            "work_report_statistic_diff_results");
        var df = Builders<WorkReportStatisticDiffResult>.Filter;
        if (await diffResults
                .Find(
                    df.Eq(x => x.IsDeleted, false) &
                    df.ElemMatch(x => x.SourcePins, pin => pin.DirectRunId == runId))
                .Limit(1)
                .AnyAsync(ct))
        {
            return true;
        }

        var idValues = new BsonArray
        {
            new ObjectId(runId),
            runId
        };
        var referenceFilter = new BsonDocument("$or", new BsonArray
        {
            new BsonDocument("runId", new BsonDocument("$in", idValues)),
            new BsonDocument("sourceRunId", new BsonDocument("$in", idValues)),
            new BsonDocument("directRunId", new BsonDocument("$in", idValues)),
            new BsonDocument("resultRunId", new BsonDocument("$in", idValues))
        }).Add("isDeleted", new BsonDocument("$ne", true));
        foreach (var collectionName in new[]
                 {
                     "work_report_statistic_exports",
                     "work_report_statistic_diff_exports"
                 })
        {
            if (await _ctx.Db.GetCollection<BsonDocument>(collectionName)
                    .Find(referenceFilter)
                    .Limit(1)
                    .AnyAsync(ct))
            {
                return true;
            }
        }
        return false;
    }

    private async Task SoftDeleteDirectGenerationRowsAsync(
        string runId,
        string actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        var filter = new BsonDocument
        {
            { "directProjection.runId", new ObjectId(runId) },
            { "isDeleted", false }
        };
        var update = new BsonDocument("$set", new BsonDocument
        {
            { "isDeleted", true },
            { "updatedAtUtc", now },
            { "updatedByUserId", new ObjectId(actorUserId) }
        });
        foreach (var collectionName in DirectGenerationCollections)
        {
            await _ctx.Db.GetCollection<BsonDocument>(collectionName)
                .UpdateManyAsync(filter, update, cancellationToken: ct);
        }
    }

    private static string BuildLifecycleOperationsStateHash(
        WorkReportStatisticRebuildJob job,
        long revision)
        => StatRunCanonicalJson.HashObject(new
        {
            version = "P9_LIFECYCLE_DIRECT_STATE_V1",
            runId = job.Id,
            status = job.Status,
            revision,
            claimToken = job.ClaimToken,
            workerId = job.LeaseOwnerId,
            generationId = job.GenerationId,
            generationHash = job.GenerationHash
        });

    private static string? RedactOperationsDiagnostic(string? value)
        => value is
            "TRANSIENT_TEST" or
            "TIMEOUT_TEST" or
            "TERMINAL_TEST" or
            "STAT_RUN_JOB_TIMEOUT" or
            "STAT_RUN_JOB_CANCELLED" or
            "STAT_RUN_RETRY_REQUESTED" or
            "STAT_RUN_REVERSAL_SUPERSEDED"
            ? value
            : string.IsNullOrWhiteSpace(value) ? null : "STAT_RUN_JOB_FAILED";

    private static string? RedactOperationsStaleReason(string? value)
        => value is
            "SOURCE_NOT_EFFECTIVE" or
            "SOURCE_REVISION_CHANGED" or
            "CONFIG_CHANGED" or
            "RUNTIME_CHANGED" or
            "STAT_RUN_REVERSAL_SUPERSEDED"
            ? value
            : string.IsNullOrWhiteSpace(value) ? null : "STAT_RUN_RESULT_STALE";
}
