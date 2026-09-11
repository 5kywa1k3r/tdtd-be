using Microsoft.Extensions.Logging.Abstractions;
using tdtd_be.DTOs.StatisticsRun;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

var cases = new (string Name, Func<Task> Run)[]
{
    ("success completes with exact canonical projection generation", SuccessCompletesExactGeneration),
    ("long projection heartbeats before completion", LongProjectionHeartbeats),
    ("projection failure releases bounded retry without domain completion", ProjectionFailureRetriesZeroDomainWrite),
    ("heartbeat failure cancels projection and retries", HeartbeatFailureCancelsProjection),
    ("stale completion fence cannot be overwritten", StaleCompletionFenceCannotOverwrite),
    ("projection replay is completed idempotently", ProjectionReplayCompletesIdempotently),
    ("lifecycle selector accepts only effective Direct transitions", LifecycleSelectorIsFailClosed),
    ("foundation bridge emits a logical V2 pin without outer request identity", FoundationBridgeUsesLogicalPin),
}.Concat(FoundationDiagnosticsCases.Cases)
    .Concat(FoundationRefreshIdentityCases.Cases)
    .ToArray();

var failed = 0;
foreach (var test in cases)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"RESULT total={cases.Length} passed={cases.Length - failed} failed={failed}");
return failed == 0 ? 0 : 1;

static async Task SuccessCompletesExactGeneration()
{
    var state = new FakeStateOwner(Lease());
    var receipt = Receipt(isReplay: false);
    var projection = new FakeProjectionOwner((_, _) => Task.FromResult(receipt));
    var worker = Worker(state, projection, TimeSpan.FromSeconds(60));

    var count = await worker.ProcessPendingAsync(3);

    Equal(1, count, "claimed count");
    Equal(1, state.CompleteCount, "complete count");
    Equal(0, state.RetryCount, "retry count");
    Same(receipt, state.CompletedReceipt, "exact projection receipt");
    Equal(0, state.HeartbeatCount, "short projection heartbeat count");
}

static async Task LongProjectionHeartbeats()
{
    var state = new FakeStateOwner(Lease());
    var projection = new FakeProjectionOwner(async (_, ct) =>
    {
        await state.FirstHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        return Receipt(isReplay: false);
    });
    var worker = Worker(state, projection, TimeSpan.FromMilliseconds(10));

    var count = await worker.ProcessPendingAsync(1);

    Equal(1, count, "claimed count");
    True(state.HeartbeatCount >= 1, "a long projection must renew its Foundation lease");
    Equal(1, state.CompleteCount, "complete after heartbeat");
    Equal(0, state.RetryCount, "retry after heartbeat");
}

static async Task ProjectionFailureRetriesZeroDomainWrite()
{
    var state = new FakeStateOwner(Lease());
    var domainWrites = 0;
    var projection = new FakeProjectionOwner((_, _) =>
    {
        // Fail before the canonical projector's publish boundary.
        throw new Exception("SYNTHETIC_TRANSIENT_PROJECTION_FAILURE");
    }, () => domainWrites++);
    var worker = Worker(state, projection, TimeSpan.FromMilliseconds(10));

    var count = await worker.ProcessPendingAsync(1);

    Equal(1, count, "claimed count");
    Equal(0, domainWrites, "domain writes on failed validation");
    Equal(0, state.CompleteCount, "complete count");
    Equal(1, state.RetryCount, "retry count");
    Equal(StatRunFoundationWorker.ProjectionFailure, state.LastDiagnostic, "retry diagnostic");
}

static async Task HeartbeatFailureCancelsProjection()
{
    var state = new FakeStateOwner(Lease()) { ThrowHeartbeat = true };
    var projectionCancelled = false;
    var projection = new FakeProjectionOwner(async (_, ct) =>
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            projectionCancelled = true;
            throw;
        }
        return Receipt(isReplay: false);
    });
    var worker = Worker(state, projection, TimeSpan.FromMilliseconds(10));

    var count = await worker.ProcessPendingAsync(1);

    Equal(1, count, "claimed count");
    True(projectionCancelled, "projection must stop after heartbeat failure");
    Equal(0, state.CompleteCount, "complete count");
    Equal(1, state.RetryCount, "retry count");
    Equal(StatRunFoundationWorker.HeartbeatFailure, state.LastDiagnostic, "retry diagnostic");
}

static async Task StaleCompletionFenceCannotOverwrite()
{
    var state = new FakeStateOwner(Lease())
    {
        ThrowComplete = true,
        ThrowRetry = true
    };
    var projection = new FakeProjectionOwner((_, _) =>
        Task.FromResult(Receipt(isReplay: false)));
    var worker = Worker(state, projection, TimeSpan.FromSeconds(60));

    var count = await worker.ProcessPendingAsync(1);

    Equal(1, count, "claimed count");
    Equal(1, state.CompleteCount, "stale complete attempt");
    Equal(1, state.RetryCount, "stale retry attempt");
    Equal(0, state.SuccessfulTerminalWrites, "successful terminal writes");
}

static async Task ProjectionReplayCompletesIdempotently()
{
    var state = new FakeStateOwner(Lease());
    var replay = Receipt(isReplay: true);
    var projection = new FakeProjectionOwner((_, _) => Task.FromResult(replay));
    var worker = Worker(state, projection, TimeSpan.FromSeconds(60));

    await worker.ProcessPendingAsync(1);

    Equal(1, state.CompleteCount, "replay complete count");
    True(state.CompletedReceipt?.IsReplay == true, "exact projector replay marker");
    Equal(Hash('b'), state.CompletedReceipt?.GenerationId, "replayed generation id");
    Equal(Hash('c'), state.CompletedReceipt?.GenerationHash, "replayed generation hash");
}

static Task LifecycleSelectorIsFailClosed()
{
    var approved = new WorkReportLifecycleProjectionOutboxEntry
    {
        Operation = "REVIEW_APPROVE",
        FromStatus = "SUBMITTED",
        ToStatus = "APPROVED",
        ToIsActive = true
    };
    var reactivated = new WorkReportLifecycleProjectionOutboxEntry
    {
        Operation = "REVIEW_REACTIVATE_REPORT",
        FromStatus = "APPROVED",
        ToStatus = "APPROVED",
        FromIsActive = false,
        ToIsActive = true
    };
    var submitted = new WorkReportLifecycleProjectionOutboxEntry
    {
        Operation = "SUBMIT",
        FromStatus = "DRAFT",
        ToStatus = "SUBMITTED",
        ToIsActive = true
    };
    var recalled = new WorkReportLifecycleProjectionOutboxEntry
    {
        Operation = "REVIEW_RECALL_APPROVED",
        FromStatus = "APPROVED",
        ToStatus = "SUBMITTED",
        ToIsActive = true
    };

    True(StatRunFoundationDirectProjectionOwner.IsEffectiveDirectEntry(approved),
        "approved entry");
    True(StatRunFoundationDirectProjectionOwner.IsEffectiveDirectEntry(reactivated),
        "reactivated entry");
    False(StatRunFoundationDirectProjectionOwner.IsEffectiveDirectEntry(submitted),
        "submitted entry");
    False(StatRunFoundationDirectProjectionOwner.IsEffectiveDirectEntry(recalled),
        "recalled entry");
    return Task.CompletedTask;
}

static Task FoundationBridgeUsesLogicalPin()
{
    var job = Lease().Job;
    var pin = StatRunFoundationDirectProjectionOwner.BuildRefreshPin(job);
    Equal(job.CapabilityId, pin.CapabilityId, "pin capability");
    Equal(job.WorkId, pin.WorkId, "pin work");
    Equal(job.SourceReportId, pin.SourceReportId, "pin source report");
    Equal(job.SourceRevision, pin.SourceRevision, "pin source revision");
    Equal(job.ConfigRevision, pin.ConfigRevision, "pin config revision");
    Equal(job.ConfigHash, pin.ConfigHash, "pin config hash");
    Equal(job.PeriodInstanceKey, pin.PeriodInstanceKey, "pin period instance");

    var propertyNames = typeof(StatRunFoundationRefreshPin)
        .GetProperties()
        .Select(property => property.Name)
        .ToHashSet(StringComparer.Ordinal);
    foreach (var prohibited in new[]
    {
        "JobId", "RunId", "ReceiptId", "CommandId", "RequestHash", "ImmutableHeaderHash"
    })
    {
        False(propertyNames.Contains(prohibited), "outer identity leaked into logical pin: " + prohibited);
    }
    return Task.CompletedTask;
}

static StatRunFoundationWorker Worker(
    FakeStateOwner state,
    FakeProjectionOwner projection,
    TimeSpan heartbeat)
    => new(
        state,
        projection,
        new StatRunFoundationWorkerIdentity(
            "p9-foundation:test",
            "P9_FOUNDATION_DIRECT_PROJECTION"),
        heartbeat,
        NullLogger<StatRunFoundationWorker>.Instance);

static StatRunWorkerLeaseResponse Lease()
    => new()
    {
        WorkerId = "p9-foundation:test",
        ClaimToken = new string('d', 32),
        LeaseUntilUtc = DateTime.UtcNow.AddMinutes(10),
        Job = new StatRunJobResponse
        {
            JobId = "100000000000000000000001",
            RunId = "100000000000000000000001",
            ReceiptId = Hash('a'),
            CapabilityId = StatRunCapabilities.DirectFieldTableLabel,
            RunKind = WorkReportStatisticRebuildJobRunKinds.Foundation,
            Status = "RUNNING",
            StateRevision = 2,
            StateHash = Hash('f'),
            WorkId = "500000000000000000000001",
            ScopeType = "WORK_PERIOD",
            ScopeId = "600000000000000000000001",
            SourceReportId = "200000000000000000000001",
            SourceRevision = 1,
            SourceHash = Hash('e'),
            LifecycleRevision = 1,
            DynamicFormTemplateId = "700000000000000000000001",
            ConfigId = "800000000000000000000001",
            ConfigVersionId = "900000000000000000000001",
            ConfigVersionNo = 2,
            ConfigRevision = 3,
            ConfigHash = Hash('9'),
            CatalogVersion = "P9",
            CatalogRawSha256 = Hash('1'),
            CatalogSemanticSha256 = Hash('2'),
            SchemaRawSha256 = Hash('3'),
            SchemaSemanticSha256 = Hash('4'),
            StageLockSha256 = Hash('5'),
            CandidateChainId = "p11_chain_test",
            PeriodKey = "2026-09",
            PeriodInstanceKey = "period-instance-test",
            PeriodKind = "MONTH"
        }
    };

static StatRunFoundationProjectionReceipt Receipt(bool isReplay)
    => new(
        "300000000000000000000001",
        Hash('b'),
        Hash('c'),
        isReplay);

static string Hash(char value) => new(value, 64);

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected={expected}; actual={actual}");
}

static void Same(object expected, object? actual, string message)
{
    if (!ReferenceEquals(expected, actual))
        throw new InvalidOperationException(message);
}

static void True(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void False(bool condition, string message) => True(!condition, message);

internal sealed class FakeStateOwner : IStatRunFoundationWorkerStateOwner
{
    private StatRunWorkerLeaseResponse? _next;

    internal FakeStateOwner(StatRunWorkerLeaseResponse next) => _next = next;

    internal int HeartbeatCount { get; private set; }
    internal int CompleteCount { get; private set; }
    internal int RetryCount { get; private set; }
    internal int SuccessfulTerminalWrites { get; private set; }
    internal int TerminalCount { get; private set; }
    internal string? LastDiagnostic { get; private set; }
    internal StatRunFoundationFailureDisposition? LastDisposition { get; private set; }
    internal StatRunFoundationProjectionReceipt? CompletedReceipt { get; private set; }
    internal bool ThrowHeartbeat { get; init; }
    internal bool ThrowComplete { get; init; }
    internal bool ThrowRetry { get; init; }
    internal TaskCompletionSource FirstHeartbeat { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<StatRunWorkerLeaseResponse?> ClaimDirectAsync(
        StatRunFoundationWorkerIdentity identity,
        CancellationToken ct = default)
    {
        var result = _next;
        _next = null;
        return Task.FromResult(result);
    }

    public Task HeartbeatAsync(
        StatRunFoundationWorkerIdentity identity,
        StatRunWorkerLeaseResponse lease,
        CancellationToken ct = default)
    {
        HeartbeatCount++;
        FirstHeartbeat.TrySetResult();
        if (ThrowHeartbeat)
            throw new InvalidOperationException("STALE_WORKER_FENCE");
        return Task.CompletedTask;
    }

    public Task CompleteAsync(
        StatRunFoundationWorkerIdentity identity,
        StatRunWorkerLeaseResponse lease,
        StatRunFoundationProjectionReceipt projection,
        CancellationToken ct = default)
    {
        CompleteCount++;
        if (ThrowComplete)
            throw new InvalidOperationException("STALE_WORKER_FENCE");
        CompletedReceipt = projection;
        SuccessfulTerminalWrites++;
        return Task.CompletedTask;
    }

    public Task ReleaseFailureAsync(
        StatRunFoundationWorkerIdentity identity,
        StatRunWorkerLeaseResponse lease,
        StatRunFoundationFailure failure,
        CancellationToken ct = default)
    {
        if (failure.Disposition == StatRunFoundationFailureDisposition.Retry)
            RetryCount++;
        else
            TerminalCount++;
        LastDiagnostic = failure.DiagnosticCode;
        LastDisposition = failure.Disposition;
        if (ThrowRetry)
            throw new InvalidOperationException("STALE_WORKER_FENCE");
        return Task.CompletedTask;
    }
}

internal sealed class FakeProjectionOwner : IStatRunFoundationDirectProjectionOwner
{
    private readonly Func<StatRunWorkerLeaseResponse, CancellationToken,
        Task<StatRunFoundationProjectionReceipt>> _project;
    private readonly Action? _beforeDomainWrite;

    internal FakeProjectionOwner(
        Func<StatRunWorkerLeaseResponse, CancellationToken,
            Task<StatRunFoundationProjectionReceipt>> project,
        Action? beforeDomainWrite = null)
    {
        _project = project;
        _beforeDomainWrite = beforeDomainWrite;
    }

    public async Task<StatRunFoundationProjectionReceipt> ProjectAsync(
        StatRunFoundationWorkerIdentity identity,
        StatRunWorkerLeaseResponse lease,
        CancellationToken ct = default)
    {
        var receipt = await _project(lease, ct);
        _beforeDomainWrite?.Invoke();
        return receipt;
    }
}
