using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.StatisticsRun;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.Enums;

namespace tdtd_be.Services.StatisticsRun;

/// <summary>
/// A process-local, purpose-bound identity for the P9 Foundation worker. It is
/// deliberately not a user/JWT identity and carries no administrator role or
/// reusable credential. The persisted lease owner is its only authority.
/// </summary>
public sealed class StatRunFoundationWorkerIdentity
{
    private const string RequiredPurpose = "P9_FOUNDATION_DIRECT_PROJECTION";

    internal StatRunFoundationWorkerIdentity(string workerId, string purpose)
    {
        WorkerId = workerId;
        Purpose = purpose;
    }

    public string WorkerId { get; }
    internal string Purpose { get; }

    internal static StatRunFoundationWorkerIdentity ForCurrentProcess(
        string applicationName)
    {
        var source = string.Join(
            "\n",
            RequiredPurpose,
            applicationName?.Trim() ?? string.Empty,
            Environment.MachineName,
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))
            .ToLowerInvariant();
        return new StatRunFoundationWorkerIdentity(
            $"p9-foundation:{digest[..24]}",
            RequiredPurpose);
    }

    internal void RequireFoundationPurpose()
    {
        if (!string.Equals(Purpose, RequiredPurpose, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(WorkerId) ||
            WorkerId.Length > 128 ||
            WorkerId.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '.' or '_' or ':' or '-')))
        {
            throw new InvalidOperationException("FOUNDATION_WORKER_IDENTITY_INVALID");
        }
    }
}

public sealed record StatRunFoundationProjectionReceipt(
    string ProjectionRunId,
    string GenerationId,
    string GenerationHash,
    bool IsReplay);

/// <summary>
/// Dedicated internal state port. A caller needs the non-user Foundation
/// identity and is still fenced by the persisted worker id + claim token CAS.
/// </summary>
public interface IStatRunFoundationWorkerStateOwner
{
    Task<StatRunWorkerLeaseResponse?> ClaimDirectAsync(
        StatRunFoundationWorkerIdentity identity,
        CancellationToken ct = default);

    Task HeartbeatAsync(
        StatRunFoundationWorkerIdentity identity,
        StatRunWorkerLeaseResponse lease,
        CancellationToken ct = default);

    Task CompleteAsync(
        StatRunFoundationWorkerIdentity identity,
        StatRunWorkerLeaseResponse lease,
        StatRunFoundationProjectionReceipt projection,
        CancellationToken ct = default);

    Task ReleaseFailureAsync(
        StatRunFoundationWorkerIdentity identity,
        StatRunWorkerLeaseResponse lease,
        StatRunFoundationFailure failure,
        CancellationToken ct = default);
}

public interface IStatRunFoundationDirectProjectionOwner
{
    Task<StatRunFoundationProjectionReceipt> ProjectAsync(
        StatRunFoundationWorkerIdentity identity,
        StatRunWorkerLeaseResponse lease,
        CancellationToken ct = default);
}

public interface IStatRunFoundationWorker
{
    Task<int> ProcessPendingAsync(
        int maxJobs = 3,
        CancellationToken ct = default);
}

/// <summary>
/// Production bridge from a claimed Foundation command to the canonical P9-02
/// lifecycle Direct projector. It resolves the exact server-owned lifecycle
/// entry; no request/user payload is accepted at this seam.
/// </summary>
public sealed class StatRunFoundationDirectProjectionOwner :
    IStatRunFoundationDirectProjectionOwner
{
    private readonly MongoDbContext _ctx;
    private readonly IStatRunDirectProjectionService _directProjection;

    public StatRunFoundationDirectProjectionOwner(
        MongoDbContext ctx,
        IStatRunDirectProjectionService directProjection)
    {
        _ctx = ctx;
        _directProjection = directProjection;
    }

    public async Task<StatRunFoundationProjectionReceipt> ProjectAsync(
        StatRunFoundationWorkerIdentity identity,
        StatRunWorkerLeaseResponse lease,
        CancellationToken ct = default)
    {
        identity.RequireFoundationPurpose();
        ArgumentNullException.ThrowIfNull(lease);
        var job = lease.Job ?? throw Fail("FOUNDATION_JOB_MISSING");
        if (!string.Equals(lease.WorkerId, identity.WorkerId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(lease.ClaimToken) ||
            lease.ClaimToken.Length != 32 ||
            lease.ClaimToken.Any(character => !Uri.IsHexDigit(character)) ||
            !ObjectId.TryParse(job.JobId, out var jobId) ||
            !ObjectId.TryParse(job.RunId, out var runId) ||
            jobId != runId ||
            !string.Equals(job.Status, "RUNNING", StringComparison.Ordinal) ||
            job.StateRevision < 1 ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.StateHash) ||
            !string.Equals(
                job.RunKind,
                WorkReportStatisticRebuildJobRunKinds.Foundation,
                StringComparison.Ordinal) ||
            !string.Equals(
                job.CapabilityId,
                StatRunCapabilities.DirectFieldTableLabel,
                StringComparison.Ordinal) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.ReceiptId) ||
            !ObjectId.TryParse(job.SourceReportId, out _))
        {
            throw Fail("FOUNDATION_JOB_IDENTITY_INVALID");
        }

        var report = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == job.SourceReportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw Fail("FOUNDATION_SOURCE_NOT_EFFECTIVE");
        var selected = RequireSourceEntry(job, report);
        var projected = await _directProjection.ProjectFoundationRefreshAsync(
            BuildRefreshPin(job),
            selected.EntryKey,
            selected.ActorUserId!,
            ct);
        return RequirePublishedProjection(projected);
    }

    internal static StatRunFoundationRefreshPin BuildRefreshPin(StatRunJobResponse job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new StatRunFoundationRefreshPin(
            CapabilityId: job.CapabilityId,
            RunKind: job.RunKind,
            WorkId: job.WorkId,
            ScopeType: job.ScopeType,
            ScopeId: job.ScopeId,
            SourceReportId: job.SourceReportId,
            SourceRevision: job.SourceRevision,
            SourceHash: job.SourceHash,
            LifecycleRevision: job.LifecycleRevision,
            DynamicFormTemplateId: job.DynamicFormTemplateId,
            ConfigId: job.ConfigId,
            ConfigVersionId: job.ConfigVersionId,
            ConfigVersionNo: job.ConfigVersionNo,
            ConfigRevision: job.ConfigRevision,
            ConfigHash: job.ConfigHash,
            CatalogVersion: job.CatalogVersion,
            CatalogRawSha256: job.CatalogRawSha256,
            CatalogSemanticSha256: job.CatalogSemanticSha256,
            SchemaRawSha256: job.SchemaRawSha256,
            SchemaSemanticSha256: job.SchemaSemanticSha256,
            StageLockSha256: job.StageLockSha256,
            CandidateChainId: job.CandidateChainId,
            FlowTemplateId: job.FlowTemplateId,
            FlowFamilyRevision: job.FlowFamilyRevision,
            FlowTemplateVersionNo: job.FlowTemplateVersionNo,
            FlowTemplateVersionId: job.FlowTemplateVersionId,
            FlowPayloadHash: job.FlowPayloadHash,
            FlowCatalogVersion: job.FlowCatalogVersion,
            FlowCatalogSemanticHash: job.FlowCatalogSemanticHash,
            FlowInstanceId: job.FlowInstanceId,
            FlowInstanceRevision: job.FlowInstanceRevision,
            FlowInstanceState: job.FlowInstanceState,
            FlowExecutionEpoch: job.FlowExecutionEpoch,
            FlowExecutionEpochId: job.FlowExecutionEpochId,
            FlowExecutionEpochRevision: job.FlowExecutionEpochRevision,
            FlowExecutionEpochState: job.FlowExecutionEpochState,
            FlowBranchId: job.FlowBranchId,
            FlowStepId: job.FlowStepId,
            FlowAttemptNo: job.FlowAttemptNo,
            FlowStepInstanceId: job.FlowStepInstanceId,
            FlowStepInstanceRevision: job.FlowStepInstanceRevision,
            FlowStepInstanceState: job.FlowStepInstanceState,
            PeriodKey: job.PeriodKey,
            PeriodInstanceKey: job.PeriodInstanceKey,
            PeriodKind: job.PeriodKind,
            PeriodStartUtc: job.PeriodStartUtc,
            PeriodEndUtc: job.PeriodEndUtc);
    }

    internal static StatRunFoundationProjectionReceipt RequirePublishedProjection(
        StatRunDirectProjectionResult projected)
    {
        if (!string.Equals(
                projected.State,
                StatRunDirectProjectionStates.Published,
                StringComparison.Ordinal) ||
            !ObjectId.TryParse(projected.RunId, out _) ||
            !StatRunCanonicalJson.IsCanonicalSha256(projected.GenerationId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(projected.GenerationHash))
        {
            throw Fail("FOUNDATION_DIRECT_PROJECTION_NOT_PUBLISHED");
        }

        return new StatRunFoundationProjectionReceipt(
            projected.RunId!,
            projected.GenerationId!,
            projected.GenerationHash!,
            projected.IsReplay);
    }

    internal static WorkReportLifecycleProjectionOutboxEntry RequireSourceEntry(
        StatRunJobResponse job, WorkAssignmentReport report)
    {
        if (report.Status != WorkAssignmentReportStatus.Approved ||
            !report.IsCurrent ||
            !report.IsActive ||
            !string.IsNullOrWhiteSpace(report.InvalidatedByFlowEventId) ||
            report.PayloadRevision != job.SourceRevision ||
            report.LifecycleRevision != job.LifecycleRevision ||
            !string.Equals(report.PayloadHash, job.SourceHash, StringComparison.Ordinal))
        {
            throw Fail("FOUNDATION_SOURCE_PIN_STALE");
        }

        var matches = (report.LifecycleProjectionOutbox ?? [])
            .Where(entry =>
                IsEffectiveDirectEntry(entry) &&
                entry.LifecycleRevision == job.LifecycleRevision &&
                entry.PayloadRevision == job.SourceRevision &&
                string.Equals(entry.PayloadHash, job.SourceHash, StringComparison.Ordinal) &&
                entry.State is (
                    WorkReportLifecycleProjectionOutboxStates.Pending or
                    WorkReportLifecycleProjectionOutboxStates.Completed))
            .OrderByDescending(entry => entry.CreatedAtUtc)
            .ThenByDescending(entry => entry.EntryKey, StringComparer.Ordinal)
            .ToArray();
        if (matches.Length != 1 ||
            !ObjectId.TryParse(matches[0].ActorUserId, out _))
        {
            throw Fail("FOUNDATION_LIFECYCLE_ENTRY_INVALID");
        }

        return matches[0];
    }

    internal static bool IsEffectiveDirectEntry(
        WorkReportLifecycleProjectionOutboxEntry entry)
        => ((entry.Operation is "REVIEW_APPROVE" or "REVIEW_CONFIRM_AUTO_APPROVE") &&
            string.Equals(entry.FromStatus, "SUBMITTED", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.ToStatus, "APPROVED", StringComparison.OrdinalIgnoreCase) &&
            entry.ToIsActive) ||
           (string.Equals(entry.Operation, "REVIEW_REACTIVATE_REPORT", StringComparison.Ordinal) &&
            string.Equals(entry.ToStatus, "APPROVED", StringComparison.OrdinalIgnoreCase) &&
            !entry.FromIsActive &&
            entry.ToIsActive);

    private static InvalidOperationException Fail(string diagnosticCode)
        => new(diagnosticCode);
}

/// <summary>
/// Hangfire-facing production owner. It claims only DIRECT Foundation jobs,
/// keeps their lease alive while projection runs, completes with the canonical
/// projection generation, and releases failures through bounded retry.
/// </summary>
public sealed class StatRunFoundationWorker : IStatRunFoundationWorker
{
    internal const string ProjectionFailure = "FOUNDATION_DIRECT_PROJECTION_FAILED";
    internal const string HeartbeatFailure = "FOUNDATION_HEARTBEAT_FAILED";
    internal const string CancellationFailure = "FOUNDATION_WORKER_CANCELLED";

    private readonly IStatRunFoundationWorkerStateOwner _state;
    private readonly IStatRunFoundationDirectProjectionOwner _projection;
    private readonly StatRunFoundationWorkerIdentity _identity;
    private readonly TimeSpan _heartbeatInterval;
    private readonly ILogger<StatRunFoundationWorker> _logger;

    public StatRunFoundationWorker(
        IStatRunFoundationWorkerStateOwner state,
        IStatRunFoundationDirectProjectionOwner projection,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<StatRunFoundationWorker> logger)
        : this(
            state,
            projection,
            StatRunFoundationWorkerIdentity.ForCurrentProcess(environment.ApplicationName),
            TimeSpan.FromSeconds(Math.Clamp(
                configuration.GetValue<int?>("StatRunFoundationWorker:HeartbeatSeconds") ?? 30,
                1,
                60)),
            logger)
    {
    }

    internal StatRunFoundationWorker(
        IStatRunFoundationWorkerStateOwner state,
        IStatRunFoundationDirectProjectionOwner projection,
        StatRunFoundationWorkerIdentity identity,
        TimeSpan heartbeatInterval,
        ILogger<StatRunFoundationWorker> logger)
    {
        _state = state;
        _projection = projection;
        _identity = identity;
        _identity.RequireFoundationPurpose();
        if (heartbeatInterval <= TimeSpan.Zero || heartbeatInterval > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        _heartbeatInterval = heartbeatInterval;
        _logger = logger;
    }

    public async Task<int> ProcessPendingAsync(
        int maxJobs = 3,
        CancellationToken ct = default)
    {
        maxJobs = Math.Clamp(maxJobs, 1, 20);
        var claimedCount = 0;
        for (var index = 0; index < maxJobs; index++)
        {
            ct.ThrowIfCancellationRequested();
            StatRunWorkerLeaseResponse? lease;
            try
            {
                lease = await _state.ClaimDirectAsync(_identity, ct);
            }
            catch (Exception error)
            {
                StatRunFoundationDiagnostics.LogFailure(_logger, StatRunFoundationStage.Claim, error, null);
                throw;
            }
            if (lease is null)
                break;
            claimedCount++;

            try
            {
                await ProcessLeaseAsync(lease, ct);
            }
            catch (OperationCanceledException error) when (ct.IsCancellationRequested)
            {
                StatRunFoundationDiagnostics.LogFailure(_logger, StatRunFoundationStage.Cancellation, error, lease);
                await TryReleaseFailureAsync(
                    lease,
                    new StatRunFoundationFailure(
                        CancellationFailure,
                        StatRunFoundationFailureDisposition.Retry),
                    CancellationToken.None);
                throw;
            }
            catch (FoundationHeartbeatException)
            {
                await TryReleaseFailureAsync(
                    lease,
                    new StatRunFoundationFailure(
                        HeartbeatFailure,
                        StatRunFoundationFailureDisposition.Retry),
                    CancellationToken.None);
            }
            catch (Exception error)
            {
                await TryReleaseFailureAsync(
                    lease,
                    StatRunFoundationDiagnostics.ClassifyProjectionFailure(error),
                    CancellationToken.None);
            }
        }
        return claimedCount;
    }

    private async Task ProcessLeaseAsync(
        StatRunWorkerLeaseResponse lease,
        CancellationToken ct)
    {
        using var workCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<StatRunFoundationProjectionReceipt> projectionTask;
        try
        {
            projectionTask = _projection.ProjectAsync(_identity, lease, workCts.Token);
        }
        catch (Exception error)
        {
            StatRunFoundationDiagnostics.LogFailure(_logger, StatRunFoundationStage.Projection, error, lease);
            throw;
        }
        var heartbeatTask = MaintainHeartbeatAsync(lease, workCts.Token);
        StatRunFoundationProjectionReceipt receipt;
        try
        {
            var first = await Task.WhenAny(projectionTask, heartbeatTask);
            if (ReferenceEquals(first, heartbeatTask))
            {
                workCts.Cancel();
                try
                {
                    await projectionTask;
                }
                catch (OperationCanceledException) when (workCts.IsCancellationRequested)
                {
                    // The heartbeat failure remains the original failure.
                }
                catch (Exception error)
                {
                    StatRunFoundationDiagnostics.LogFailure(_logger, StatRunFoundationStage.Projection,
                        error, lease, secondary: true);
                }
                await heartbeatTask;
                throw new FoundationHeartbeatException();
            }
            try
            {
                receipt = await projectionTask;
            }
            catch (Exception error)
            {
                StatRunFoundationDiagnostics.LogFailure(_logger, StatRunFoundationStage.Projection, error, lease);
                throw;
            }
        }
        finally
        {
            workCts.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException) when (workCts.IsCancellationRequested)
            {
                // Expected once projection has reached a terminal receipt.
            }
            catch (FoundationHeartbeatException) when (projectionTask.IsFaulted || projectionTask.IsCanceled)
            {
                // Preserve the original failure, after observing the heartbeat task.
            }
        }
        try
        {
            await _state.CompleteAsync(_identity, lease, receipt, ct);
        }
        catch (Exception error)
        {
            StatRunFoundationDiagnostics.LogFailure(_logger, StatRunFoundationStage.Completion, error, lease);
            throw;
        }
    }

    private async Task MaintainHeartbeatAsync(
        StatRunWorkerLeaseResponse lease,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_heartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await _state.HeartbeatAsync(_identity, lease, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            StatRunFoundationDiagnostics.LogFailure(_logger, StatRunFoundationStage.Heartbeat, error, lease);
            throw new FoundationHeartbeatException();
        }
    }

    private async Task TryReleaseFailureAsync(
        StatRunWorkerLeaseResponse lease,
        StatRunFoundationFailure failure,
        CancellationToken ct)
    {
        try
        {
            await _state.ReleaseFailureAsync(_identity, lease, failure, ct);
        }
        catch (Exception error)
        {
            // A stale/expired fence is not overwritten; preserve the earlier failure separately.
            StatRunFoundationDiagnostics.LogFailure(_logger, StatRunFoundationStage.Retry,
                error, lease, secondary: true);
        }
    }

    private sealed class FoundationHeartbeatException : Exception { }
}
