using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Production;

var cases = new (string Id, Func<Task> Run)[]
{
    ("P10-PROD-WORKER-01", InitialClaimCaptureFinalize),
    ("P10-PROD-WORKER-02", RecheckSuccessorCapture),
    ("P10-PROD-WORKER-03", HeartbeatMaintainsLease),
    ("P10-PROD-WORKER-04", TransientFailureRequeuesAndRetries),
    ("P10-PROD-WORKER-05", PendingCrashWindowRecoversWithoutRecapture),
    ("P10-PROD-WORKER-06", ExactPendingReplayHasNoSecondBusinessWrite),
    ("P10-PROD-WORKER-07", InternalIdentityIsLeastPrivilegeAndNotHttpAdmin),
    ("P10-PROD-WORKER-08", SourceOwnsInternalLifecycleWithoutAdminHttp),
    ("P10-PROD-WORKER-09", NonFlowPublicationTupleIsCanonical)
};

foreach (var test in cases)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Id}");
    }
    catch (Exception error)
    {
        Console.WriteLine(
            $"FAIL {test.Id} {error.GetType().Name}:{error.Message}");
        return 1;
    }
}

Require(cases.Length == 9, "EXACT_CASE_COUNT");
Console.WriteLine(
    "P10_PRODUCTION_WORKER_OK cases=9 claim=true capture=true " +
    "heartbeat=true retry=true replay=true pendingRecovery=true " +
    "recheckSuccessor=true failureBusinessWrites=0 testingHttp=0 " +
    "internalAdminHttp=0 jwtCredential=0");
return 0;

static async Task InitialClaimCaptureFinalize()
{
    var runtime = new FakeRuntime();
    runtime.InitialClaims.Enqueue(Lease("initial", false));
    var worker = Worker(runtime);
    var processed = await worker.ProcessPendingAsync(1);
    Require(processed == 1, "INITIAL_PROCESSED");
    Sequence(runtime.Trace,
        "recover:none", "claim:recheck:none", "claim:initial",
        "capture:initial");
    Require(runtime.PublishPendingCount == 1 &&
            runtime.TrustedFinalizeCount == 1,
        "INITIAL_PUBLISH_FINALIZE");
}

static async Task RecheckSuccessorCapture()
{
    var runtime = new FakeRuntime();
    runtime.RecheckClaims.Enqueue(Lease("recheck", true));
    var processed = await Worker(runtime).ProcessPendingAsync(1);
    Require(processed == 1, "RECHECK_PROCESSED");
    Sequence(runtime.Trace,
        "recover:none", "claim:recheck", "capture:recheck");
    Require(runtime.SuccessorGenerationCount == 1 &&
            runtime.PublishPendingCount == 1 &&
            runtime.TrustedFinalizeCount == 1,
        "RECHECK_SUCCESSOR_TERMINAL");
}

static async Task HeartbeatMaintainsLease()
{
    var runtime = new FakeRuntime();
    runtime.InitialClaims.Enqueue(Lease("heartbeat", false));
    runtime.Capture = async (_, ct) =>
    {
        await runtime.HeartbeatObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(2), ct);
        runtime.RecordTerminal(false);
    };
    var processed = await Worker(runtime, TimeSpan.FromMilliseconds(10))
        .ProcessPendingAsync(1);
    Require(processed == 1 && runtime.HeartbeatCount >= 1,
        "HEARTBEAT_NOT_OBSERVED");
    Require(runtime.FailCount == 0, "HEARTBEAT_FALSE_FAIL");

    var faulted = new FakeRuntime();
    faulted.InitialClaims.Enqueue(Lease("heartbeat-fault", false));
    faulted.Heartbeat = (_, _) =>
        Task.FromException(new InvalidOperationException(
            "lease heartbeat failed"));
    faulted.Capture = async (_, ct) =>
    {
        await faulted.HeartbeatObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(2), ct);
    };
    Require(await Worker(faulted, TimeSpan.FromMilliseconds(10))
            .ProcessPendingAsync(1) == 1,
        "HEARTBEAT_FAULT_RECURRING_ABORTED");
    Require(faulted.FailCount == 1 &&
            faulted.DomainRetryWriteCount == 1 &&
            faulted.TrustedFinalizeCount == 0,
        "HEARTBEAT_FAULT_NOT_REQUEUED");
}

static async Task TransientFailureRequeuesAndRetries()
{
    var runtime = new FakeRuntime();
    runtime.InitialClaims.Enqueue(Lease("retry-1", false));
    runtime.InitialClaims.Enqueue(Lease("retry-2", false));
    var attempt = 0;
    runtime.Capture = (lease, _) =>
    {
        attempt++;
        if (attempt == 1)
            throw new InvalidOperationException("owner drift");
        runtime.RecordTerminal(lease.IsRecheck);
        return Task.CompletedTask;
    };
    var worker = Worker(runtime);
    Require(await worker.ProcessPendingAsync(1) == 1,
        "RETRY_FIRST_PASS");
    Require(runtime.FailCount == 1 && runtime.DomainRetryWriteCount == 1,
        "RETRY_NOT_REQUEUED");
    Require(await worker.ProcessPendingAsync(1) == 1,
        "RETRY_SECOND_PASS");
    Require(runtime.TrustedFinalizeCount == 1 && attempt == 2,
        "RETRY_DID_NOT_TERMINALIZE");
    Require(runtime.BusinessOwnerWriteCount == 0 &&
            runtime.ExternalCallCount == 0,
        "FAILURE_ZERO_BUSINESS_WRITE");
}

static async Task PendingCrashWindowRecoversWithoutRecapture()
{
    var runtime = new FakeRuntime();
    runtime.InitialClaims.Enqueue(Lease("pending-crash", false));
    runtime.Capture = (_, _) =>
    {
        runtime.PublishPendingCount++;
        runtime.PendingRecovery.Enqueue(
            StatisticReconciliationProductionRecoveryState.Completed);
        throw new InvalidOperationException("crash after publish pending");
    };
    runtime.Fail = (_, _, _) =>
        throw new InvalidOperationException("stale fence after publish");
    var worker = Worker(runtime);
    Require(await worker.ProcessPendingAsync(1) == 1,
        "PENDING_CRASH_PASS");
    Require(await worker.ProcessPendingAsync(1) == 1,
        "PENDING_RECOVERY_PASS");
    Require(runtime.CaptureCount == 1 &&
            runtime.RecoveryFinalizeCount == 1 &&
            runtime.PublishPendingCount == 1,
        "PENDING_RECOVERY_RECAPTURED");
}

static async Task ExactPendingReplayHasNoSecondBusinessWrite()
{
    var runtime = new FakeRuntime();
    runtime.PendingRecovery.Enqueue(
        StatisticReconciliationProductionRecoveryState.Completed);
    runtime.PendingRecovery.Enqueue(
        StatisticReconciliationProductionRecoveryState.Completed);
    var processed = await Worker(runtime).ProcessPendingAsync(2);
    Require(processed == 2 && runtime.RecoveryFinalizeCount == 2,
        "PENDING_REPLAY_COUNT");
    Require(runtime.CaptureCount == 0 &&
            runtime.PublishPendingCount == 0 &&
            runtime.BusinessOwnerWriteCount == 0,
        "PENDING_REPLAY_SECOND_EFFECT");
}

static Task NonFlowPublicationTupleIsCanonical()
{
    var method = typeof(StatisticReconciliationRunService).GetMethod(
        "HasInvalidP9FlowRuntime",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("FLOW_RUNTIME_GUARD_MISSING");
    bool Invalid(WorkReportStatisticRebuildJob job) =>
        (bool)(method.Invoke(null, [job]) ?? true);

    var nonFlow = new WorkReportStatisticRebuildJob();
    Require(!Invalid(nonFlow), "NON_FLOW_TUPLE_REJECTED");

    nonFlow.FlowStepId = "mixed-step";
    Require(Invalid(nonFlow), "NON_FLOW_MIXED_TUPLE_ACCEPTED");

    var flow = new WorkReportStatisticRebuildJob
    {
        FlowTemplateId = "507f1f77bcf86cd799439001",
        FlowFamilyRevision = 1,
        FlowTemplateVersionId = "507f1f77bcf86cd799439002",
        FlowPayloadHash = new string('a', 64),
        FlowInstanceId = "507f1f77bcf86cd799439003",
        FlowInstanceRevision = 1,
        FlowExecutionEpoch = 1,
        FlowExecutionEpochId = "507f1f77bcf86cd799439004",
        FlowExecutionEpochRevision = 1,
        FlowStepId = "step-1",
        FlowBranchId = "507f1f77bcf86cd799439005",
        FlowStepInstanceId = "507f1f77bcf86cd799439006",
        FlowStepInstanceRevision = 1,
        FlowContributionPolicy = "INCLUDE",
        FlowContributionPolicyHash = new string('b', 64),
        FlowEffectiveStatus = "EFFECTIVE"
    };
    Require(!Invalid(flow), "FLOW_TUPLE_REJECTED");

    flow.FlowPayloadHash = "not-a-hash";
    Require(Invalid(flow), "FLOW_INVALID_HASH_ACCEPTED");
    return Task.CompletedTask;
}

static Task InternalIdentityIsLeastPrivilegeAndNotHttpAdmin()
{
    var identity = new StatisticReconciliationInternalWorkerIdentity(
        Options.Create(new StatisticReconciliationProductionWorkerOptions()));
    Require(!RoleGuard.IsSystemAdmin(identity.Actor),
        "INTERNAL_ACTOR_MUST_NOT_BE_SYSTEM_ADMIN");
    Require(identity.Actor.Roles.SequenceEqual(
        [StatisticReconciliationInternalWorkerIdentity.CapabilityRole],
        StringComparer.Ordinal),
        "INTERNAL_ACTOR_ROLE_SET");
    StatisticReconciliationInternalWorkerAccess
        .RequireWorkerOrSystemAdmin(identity.Actor);
    ExpectThrows(() => RoleGuard.RequireSystemAdmin(identity.Actor),
        "CONTROLLER_MUST_REJECT_INTERNAL_ACTOR");

    var roleOnly = Actor(
        accountKind: string.Empty,
        [StatisticReconciliationInternalWorkerIdentity.CapabilityRole]);
    var kindOnly = Actor(
        StatisticReconciliationInternalWorkerIdentity.AccountKind,
        []);
    var broadened = Actor(
        StatisticReconciliationInternalWorkerIdentity.AccountKind,
        [StatisticReconciliationInternalWorkerIdentity.CapabilityRole,
            "ADMIN"]);
    foreach (var forged in new[] { roleOnly, kindOnly, broadened })
        ExpectThrows(() => StatisticReconciliationInternalWorkerAccess
                .RequireWorkerOrSystemAdmin(forged),
            "FORGED_INTERNAL_IDENTITY_ACCEPTED");
    return Task.CompletedTask;
}

static Task SourceOwnsInternalLifecycleWithoutAdminHttp()
{
    var source = File.ReadAllText(Path.Combine(
        FindRepositoryRoot(), "tdtd-be", "Services",
        "StatisticsReconciliation", "Production",
        "StatisticReconciliationProductionWorker.cs"));
    foreach (var forbidden in new[]
             {
                 "HttpClient", "/api/testing/", "api/admin/internal",
                 "Jwt:Key", "SYSTEM_ADMIN"
             })
    {
        Require(!source.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            $"FORBIDDEN_PRODUCTION_WORKER_SOURCE:{forbidden}");
    }
    Ordered(source,
        "ClaimAsync(",
        "captureWorker.CaptureAsync(");
    Contains(source, "runService.ClaimRecheckAsync(");
    Contains(source, "runService.HeartbeatAsync(");
    Contains(source, "runService.FailAsync(");
    Contains(source, "verdictPipeline.FinalizeAsync(");
    Contains(source, "StatisticReconciliationRecheckPhases.ReadyToClaim");
    var compactSource = string.Concat(source.Where(
        character => !char.IsWhiteSpace(character)));
    Contains(compactSource,
        "StatisticReconciliationRecheckPhases.ReviewSupersessionPending");
    return Task.CompletedTask;
}

static StatisticReconciliationProductionWorker Worker(
    FakeRuntime runtime,
    TimeSpan? heartbeat = null)
    => new(
        runtime,
        heartbeat ?? TimeSpan.FromSeconds(5),
        NullLogger<StatisticReconciliationProductionWorker>.Instance);

static StatisticReconciliationProductionLease Lease(
    string id,
    bool recheck)
    => new(
        id,
        "p10-production-worker",
        new string('a', 64),
        DateTime.UtcNow.AddMinutes(1),
        recheck);

static MeResponse Actor(string accountKind, List<string> roles)
    => new(
        "000000000000000000000001",
        "business-actor",
        "Business actor",
        [],
        string.Empty,
        null,
        null,
        null,
        roles,
        null,
        false,
        accountKind);

static void ExpectThrows(Action action, string reason)
{
    try
    {
        action();
    }
    catch
    {
        return;
    }
    throw new InvalidOperationException(reason);
}

static void Sequence(IReadOnlyList<string> actual, params string[] expected)
    => Require(actual.SequenceEqual(expected, StringComparer.Ordinal),
        $"TRACE expected=[{string.Join(',', expected)}] " +
        $"actual=[{string.Join(',', actual)}]");

static void Contains(string source, string value)
    => Require(source.Contains(value, StringComparison.Ordinal),
        $"SOURCE_TOKEN:{value}");

static void Ordered(string source, params string[] values)
{
    var position = -1;
    foreach (var value in values)
    {
        var next = source.IndexOf(value, position + 1, StringComparison.Ordinal);
        Require(next > position, $"SOURCE_ORDER:{value}");
        position = next;
    }
}

static string FindRepositoryRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, "tdtd-be")))
            return current.FullName;
        current = current.Parent;
    }
    throw new InvalidOperationException("REPOSITORY_ROOT_NOT_FOUND");
}

static void Require(bool condition, string reason)
{
    if (!condition)
        throw new InvalidOperationException(reason);
}

internal sealed class FakeRuntime
    : IStatisticReconciliationProductionRuntime
{
    internal Queue<StatisticReconciliationProductionRecoveryState>
        PendingRecovery { get; } = new();
    internal Queue<StatisticReconciliationProductionLease> InitialClaims
        { get; } = new();
    internal Queue<StatisticReconciliationProductionLease> RecheckClaims
        { get; } = new();
    internal List<string> Trace { get; } = [];
    internal TaskCompletionSource HeartbeatObserved { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    internal Func<StatisticReconciliationProductionLease, CancellationToken,
        Task>? Capture { get; set; }
    internal Func<StatisticReconciliationProductionLease, CancellationToken,
        Task>? Heartbeat { get; set; }
    internal Func<StatisticReconciliationProductionLease, string,
        CancellationToken, Task>? Fail { get; set; }
    internal int HeartbeatCount { get; private set; }
    internal int CaptureCount { get; set; }
    internal int FailCount { get; private set; }
    internal int DomainRetryWriteCount { get; private set; }
    internal int PublishPendingCount { get; set; }
    internal int TrustedFinalizeCount { get; private set; }
    internal int RecoveryFinalizeCount { get; private set; }
    internal int SuccessorGenerationCount { get; private set; }
    internal int BusinessOwnerWriteCount { get; private set; }
    internal int ExternalCallCount { get; private set; }

    public Task<StatisticReconciliationProductionRecoveryState>
        RecoverOneAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = PendingRecovery.Count == 0
            ? StatisticReconciliationProductionRecoveryState.None
            : PendingRecovery.Dequeue();
        Trace.Add(result ==
                  StatisticReconciliationProductionRecoveryState.None
            ? "recover:none"
            : "recover:completed");
        if (result ==
            StatisticReconciliationProductionRecoveryState.Completed)
            RecoveryFinalizeCount++;
        return Task.FromResult(result);
    }

    public Task<StatisticReconciliationProductionLease?> ClaimInitialAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (InitialClaims.Count == 0)
        {
            Trace.Add("claim:initial:none");
            return Task.FromResult<
                StatisticReconciliationProductionLease?>(null);
        }
        var lease = InitialClaims.Dequeue();
        Trace.Add("claim:initial");
        return Task.FromResult<
            StatisticReconciliationProductionLease?>(lease);
    }

    public Task<StatisticReconciliationProductionLease?> ClaimRecheckAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RecheckClaims.Count == 0)
        {
            Trace.Add("claim:recheck:none");
            return Task.FromResult<
                StatisticReconciliationProductionLease?>(null);
        }
        var lease = RecheckClaims.Dequeue();
        Trace.Add("claim:recheck");
        return Task.FromResult<
            StatisticReconciliationProductionLease?>(lease);
    }

    public async Task CaptureAsync(
        StatisticReconciliationProductionLease lease,
        CancellationToken cancellationToken)
    {
        Trace.Add($"capture:{lease.ReconciliationId}");
        CaptureCount++;
        if (Capture is not null)
        {
            await Capture(lease, cancellationToken);
            return;
        }
        RecordTerminal(lease.IsRecheck);
    }

    internal void RecordTerminal(bool recheck)
    {
        PublishPendingCount++;
        TrustedFinalizeCount++;
        if (recheck)
            SuccessorGenerationCount++;
    }

    public Task HeartbeatAsync(
        StatisticReconciliationProductionLease lease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HeartbeatCount++;
        Trace.Add($"heartbeat:{lease.ReconciliationId}");
        HeartbeatObserved.TrySetResult();
        return Heartbeat?.Invoke(lease, cancellationToken) ?? Task.CompletedTask;
    }

    public Task FailTransientAsync(
        StatisticReconciliationProductionLease lease,
        string failureCode,
        CancellationToken cancellationToken)
    {
        FailCount++;
        Trace.Add($"fail:{lease.ReconciliationId}:{failureCode}");
        if (Fail is not null)
            return Fail(lease, failureCode, cancellationToken);
        DomainRetryWriteCount++;
        return Task.CompletedTask;
    }
}
